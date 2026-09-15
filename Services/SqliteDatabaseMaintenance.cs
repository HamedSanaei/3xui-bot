using Microsoft.Data.Sqlite;

/// <summary>
/// Measures the on-disk footprint of the application's SQLite databases and runs the file-level maintenance that
/// returns freed pages to the operating system.
/// </summary>
/// <remarks>
/// <para>
/// This helper exists because SQLite never shrinks a database file on its own. Deleting rows only moves pages onto the
/// internal free list, so a database that once held large JSON payloads keeps its high-water-mark size until a
/// <c>VACUUM</c> rewrites it. Every cleanup pass therefore measures before and after, then vacuums only when it
/// actually changed rows.
/// </para>
/// <para>
/// The helper is stateless and never logs, so callers own the logging, the retention decision, and the scheduling
/// policy. It performs no network access and touches only the exact database path it is given.
/// </para>
/// </remarks>
public static class SqliteDatabaseMaintenance
{
    /// <summary>
    /// On-disk sizes of one SQLite database and its write-ahead log sidecar files.
    /// </summary>
    /// <param name="MainBytes">Size of the main <c>*.db</c> file in bytes, or zero when the file does not exist yet.</param>
    /// <param name="WalBytes">Size of the <c>*.db-wal</c> write-ahead log in bytes, or zero when it is absent.</param>
    /// <param name="SharedMemoryBytes">Size of the <c>*.db-shm</c> shared-memory index in bytes, or zero when absent.</param>
    /// <remarks>
    /// A large write-ahead log is not wasted space: it is checkpointed back into the main file. It is measured anyway
    /// because it explains a sudden growth report that a main-file size alone cannot.
    /// </remarks>
    public readonly record struct SqliteDatabaseSize(long MainBytes, long WalBytes, long SharedMemoryBytes)
    {
        /// <summary>Gets the combined byte footprint of the main file and its sidecars.</summary>
        public long TotalBytes => MainBytes + WalBytes + SharedMemoryBytes;
    }

    /// <summary>
    /// Reads the current on-disk size of one SQLite database.
    /// </summary>
    /// <param name="databasePath">
    /// Absolute or working-directory-relative path of the database file. The file does not have to exist; a missing
    /// database reports all zero sizes. This must be a real local file path, never a connection string or a folder.
    /// </param>
    /// <returns>
    /// The main, write-ahead log, and shared-memory byte counts. Missing files report zero for that component rather
    /// than <c>-1</c>, so callers can log and subtract the values directly.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="databasePath" /> is null, empty, or whitespace.</exception>
    /// <remarks>
    /// Sizes are read from the file system rather than from <c>PRAGMA page_count</c> because the file system value is
    /// what an operator sees in <c>ls</c>, <c>du</c>, and disk-usage alerts, and it includes the pages the free list
    /// still holds.
    /// </remarks>
    /// <example>
    /// <code>
    /// var before = SqliteDatabaseMaintenance.Measure(appConfig.UserDatabasePath);
    /// _logger.LogInformation("users.db bytes={Bytes}", before.TotalBytes);
    /// </code>
    /// </example>
    public static SqliteDatabaseSize Measure(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        return new SqliteDatabaseSize(Length(databasePath), Length(databasePath + "-wal"), Length(databasePath + "-shm"));
    }

    /// <summary>
    /// Checkpoints the write-ahead log, rewrites the database compactly, and refreshes query statistics.
    /// </summary>
    /// <param name="databasePath">
    /// Absolute or working-directory-relative path of the database file to maintain. Only this file is opened; no
    /// other database, folder, or server is touched.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for host shutdown.</param>
    /// <returns>
    /// <c>true</c> when the checkpoint, <c>VACUUM</c>, and <c>ANALYZE</c> all completed; <c>false</c> when SQLite
    /// refused because another reader or writer holds the database busy, or when shutdown was requested first. A
    /// <c>false</c> result is not an error: the next scheduled pass retries and no data is changed either way.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="databasePath" /> is null, empty, or whitespace.</exception>
    /// <remarks>
    /// <para>
    /// Ordering matters. <c>PRAGMA wal_checkpoint(TRUNCATE)</c> first folds every committed write-ahead log frame into
    /// the main file and truncates the log, so the subsequent <c>VACUUM</c> sees a stable database. <c>VACUUM</c> then
    /// rebuilds the file using only live pages, which is what returns disk space to the operating system.
    /// <c>ANALYZE</c> finishes by rewriting the internal statistics so the query planner keeps using the indexes the
    /// delete-heavy workload relied on.
    /// </para>
    /// <para>
    /// VACUUM requires free disk space roughly equal to the database size and an exclusive lock for its duration. It
    /// must never run inside a transaction, so this method opens its own short-lived connection instead of borrowing an
    /// Entity Framework context. Callers must therefore invoke it after every context for this database has been
    /// disposed.
    /// </para>
    /// <para>
    /// Side effects: rewrites the database file, truncates the write-ahead log, and updates SQLite statistics. Row
    /// contents, row counts, and schema are not modified.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// if (changedRows > 0) maintenanceRan = await SqliteDatabaseMaintenance.VacuumAndAnalyzeAsync(path, token);
    /// </code>
    /// </example>
    public static async Task<bool> VacuumAndAnalyzeAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        try
        {
            await using var connection = new SqliteConnection(SqliteOperation.ConnectionString(databasePath));
            await connection.OpenAsync(cancellationToken);
            // Fold committed WAL frames into the main file before rewriting it; a VACUUM with a populated log both
            // wastes work and leaves the sidecar file at its previous high-water mark.
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken);
            // VACUUM cannot run inside a transaction, so it is issued as its own statement on a connection that EF
            // never owns. A busy database refuses the statement instead of corrupting anything, and the next pass
            // retries.
            await ExecuteAsync(connection, "VACUUM", cancellationToken);
            await ExecuteAsync(connection, "ANALYZE", cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SqliteException)
        {
            // Contention and insufficient temporary space both surface here. Neither may fail the host or the cleanup
            // pass: the deleted rows are already committed and the file simply stays at its previous size.
            return false;
        }
    }

    /// <summary>Reads one file's length without following a missing path to an exception.</summary>
    /// <param name="path">Absolute or relative path of the file to measure.</param>
    /// <returns>The file length in bytes, or zero when the file does not exist or cannot be inspected.</returns>
    /// <remarks>A directory, a permission error, or a vanished sidecar all report zero so size logging never throws.</remarks>
    private static long Length(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Executes one maintenance statement that returns no result set.</summary>
    /// <param name="connection">Open connection to the database being maintained.</param>
    /// <param name="sql">Constant maintenance statement issued by this helper; never caller-supplied text.</param>
    /// <param name="cancellationToken">Cancellation token for host shutdown.</param>
    /// <returns>A task completing after the statement finishes.</returns>
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
