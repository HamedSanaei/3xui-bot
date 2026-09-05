using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Metrics;

/// <summary>Retries isolated local SQLite work without replaying network side effects.</summary>
/// <remarks>Each delegate invocation must create and dispose its own context and transaction. No external I/O is allowed.</remarks>
public static class SqliteOperation
{
    private static readonly Meter Meter = new("Adminbot.Persistence");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("sqlite.busy.retries");

    /// <summary>Executes an idempotent local database operation with at most three attempts.</summary>
    /// <typeparam name="T">Detached result type; never return a live context or query.</typeparam>
    /// <param name="operation">Required local operation; creates a fresh context on every attempt.</param>
    /// <param name="cancellationToken">Cancellation of database work and retry delays.</param>
    /// <returns>The operation result after a successful commit; no additional save is required.</returns>
    /// <exception cref="SqliteException">The final BUSY/LOCKED error or any non-retryable SQLite error.</exception>
    /// <remarks>Only SQLite primary codes 5 and 6 are retried. A failed commit must have been rolled back by disposal.</remarks>
    /// <example><code>await SqliteOperation.RunAsync(async token =&gt; { await using var db = factory.CreateDbContext(); return await db.Users.CountAsync(token); }, token);</code></example>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await operation(cancellationToken); }
            catch (Exception ex) when (attempt < 2 && IsBusy(ex))
            {
                Retries.Add(1);
                await Task.Delay((attempt == 0 ? 50 : 150) + Random.Shared.Next(0, 51), cancellationToken);
            }
        }
    }

    /// <summary>Recognizes only SQLite contention errors, including wrapped EF save errors.</summary>
    /// <param name="exception">Required failure from a local database operation.</param>
    /// <returns>True for BUSY or LOCKED; false for constraints, validation, cancellation, and network errors.</returns>
    /// <remarks>Only local SQLite contention is retryable. Callers must recreate their context for each attempt and keep external requests outside the delegate.</remarks>
    public static bool IsBusy(Exception exception) => exception is SqliteException { SqliteErrorCode: 5 or 6 }
        || exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 } };

    /// <summary>Builds a private-cache WAL-compatible SQLite connection string.</summary>
    /// <param name="path">Required local database filename; never a connection string or secret.</param>
    /// <returns>A connection string with a five-second SQLite command/busy timeout.</returns>
    /// <remarks>Only local SQLite contention is retryable. Callers must recreate their context for each attempt and keep external requests outside the delegate.</remarks>
    public static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path, DefaultTimeout = 5, Cache = SqliteCacheMode.Private
    }.ToString();
}
