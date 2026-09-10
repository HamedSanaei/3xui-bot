using Microsoft.Data.Sqlite;

/// <summary>
/// Releases pooled SQLite connections for the database files owned by one test fixture without touching pools
/// that belong to any other fixture running in parallel.
/// </summary>
/// <remarks>
/// SQLite connection pooling in Microsoft.Data.Sqlite is keyed per exact connection string, and the library
/// offers no "clear pools for this data source" API. A fixture therefore has to clear the pool for each
/// connection-string shape used against its own files. A fixture that clears only the shape it happened to
/// construct itself leaks pooled handles for the shapes created by <c>Program.RegisterApplicationServices</c>,
/// by the durable log dispatcher's read-only backup snapshot, or by ad-hoc test code, and the later fixture
/// directory delete then fails with
/// <c>IOException: The process cannot access the file ... because it is being used by another process</c>.
///
/// Every connection string this helper clears embeds the absolute <c>DataSource</c> path it was given, so the
/// operation is strictly scoped to one fixture's files. This deliberately replaces the global
/// <see cref="SqliteConnection.ClearAllPools" /> call, which released pooled connections for unrelated fixtures
/// and could disturb a fixture that was still running in parallel.
/// </remarks>
internal static class SqliteTestPools
{
    /// <summary>
    /// Clears the SQLite connection pool for every <c>*.db</c> file inside one fixture-owned temporary directory,
    /// including the durable log outbox and the backup snapshot files an application host creates beside them.
    /// </summary>
    /// <param name="directory">
    /// Absolute path of the temporary directory owned by a single fixture. It must live under the operating-system
    /// temporary root; the helper refuses any other path so a real application database can never be touched.
    /// Pass the same directory the fixture deletes, never a parent, a content root, or a configured Data folder.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="directory" /> does not resolve under the operating-system temporary directory,
    /// which would indicate a mis-wired fixture that must not be released automatically.
    /// </exception>
    /// <remarks>
    /// A missing directory is treated as already released and returns immediately. Every discovered file is passed
    /// to <see cref="ClearFor" />, so clearing stays scoped to the supplied fixture directory and cannot disturb a
    /// fixture running in parallel. The helper never deletes files.
    /// </remarks>
    /// <example>
    /// <code>
    /// SqliteTestPools.ClearForDirectory(fixtureDirectory);
    /// Directory.Delete(fixtureDirectory, recursive: true);
    /// </code>
    /// </example>
    public static void ClearForDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to release SQLite pools outside the OS temporary directory.");

        // The application may create users.db, credentials.db, the durable outbox, and *_backup.db snapshots in
        // this directory; releasing all of them keeps fixture cleanup independent of which files a test triggered.
        foreach (var databasePath in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
            ClearFor(databasePath);
    }

    /// <summary>
    /// Clears the SQLite connection pool for every connection-string shape this test project uses against one
    /// fixture-owned database file.
    /// </summary>
    /// <param name="databasePath">
    /// Absolute path of the fixture-owned SQLite database file whose pooled connections must be released. The
    /// file does not have to exist; clearing a pool that was never created is a harmless no-op.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="databasePath" /> is null, empty, or whitespace, which would otherwise make the
    /// helper clear an unpredictable default database.
    /// </exception>
    /// <remarks>
    /// Call this from fixture disposal immediately before deleting the fixture directory, after every test-owned
    /// operation has stopped. The helper never opens a connection and never deletes files; it only evicts pooled
    /// connections whose connection string embeds <paramref name="databasePath" />.
    /// </remarks>
    /// <example>
    /// <code>
    /// SqliteTestPools.ClearFor(Path.Combine(fixtureDirectory, "users.db"));
    /// SqliteTestPools.ClearFor(Path.Combine(fixtureDirectory, "credentials.db"));
    /// </code>
    /// </example>
    public static void ClearFor(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A fixture database path is required.", nameof(databasePath));

        foreach (var connectionString in ConnectionStringShapes(databasePath))
        {
            using var connection = new SqliteConnection(connectionString);
            // The pool key is derived from this connection's connection string only, so parallel fixtures with
            // different absolute paths can never be affected by this call.
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Enumerates the connection-string shapes used against a fixture-owned SQLite file in this test project.
    /// </summary>
    /// <param name="databasePath">Absolute fixture-owned database path embedded into every emitted connection string.</param>
    /// <returns>
    /// Connection strings covering the minimal <c>Data Source=...</c> literal form, the read-only backup
    /// snapshot form used by the durable log dispatcher, the production
    /// <see cref="SqliteOperation.ConnectionString" /> form, the explicit private-cache fixture form, the
    /// shared-cache durable-outbox form, and the non-pooled form. The sequence is never empty and each entry
    /// contains only the supplied path, so callers can clear them all without affecting other fixtures.
    /// </returns>
    /// <remarks>
    /// Pools that do not exist are simply not cleared; emitting a superset is safe because Microsoft.Data.Sqlite
    /// ignores unknown pool keys.
    /// </remarks>
    private static IEnumerable<string> ConnectionStringShapes(string databasePath)
    {
        // Exact literal form used by `new SqliteConnection("Data Source=" + path)`.
        yield return "Data Source=" + databasePath;
        // Exact literal form used by the durable log dispatcher when it snapshots a source database for backup.
        yield return "Data Source=" + databasePath + ";Mode=ReadOnly";
        // Builder form of the same read-only backup snapshot connection.
        yield return new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        // Minimal normalized form, identical in effect to the literal above when the provider canonicalizes it.
        yield return new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        // Production options used by SqliteOperation.ConnectionString and Program-registered DbContexts.
        yield return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 5,
            Cache = SqliteCacheMode.Private
        }.ToString();
        // Explicit private-cache form used by fixtures that pin the open mode.
        yield return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5
        }.ToString();
        // Shared-cache form used by the durable Telegram log outbox.
        yield return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();
        // Non-pooled form: no pool is created for it, kept so pooling can never silently return.
        yield return new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    }
}
