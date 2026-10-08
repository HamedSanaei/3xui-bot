using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Adminbot.Services.Telemetry;

/// <summary>Retries isolated local SQLite work without replaying network side effects.</summary>
/// <remarks>Each delegate invocation must create and dispose its own context and transaction. No external I/O is allowed.</remarks>
public static class SqliteOperation
{
    /// <summary>Existing contention metric; never tagged with SQL, paths, or customer values.</summary>
    private static readonly Meter Meter = new("Adminbot.Persistence");
    /// <summary>Counts actual existing BUSY/LOCKED retries without changing the retry policy.</summary>
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("sqlite.busy.retries");
    /// <summary>Explicit application-wide writer binding so background contention is observed too.</summary>
    private static LatencyTelemetryService _telemetry;

    /// <summary>Binds the application singleton nonblocking writer for foreground and background SQLite diagnostics.</summary>
    /// <param name="telemetry">Optional singleton writer; null removes background recording without affecting persistence.</param>
    /// <remarks>Call once during application initialization. No database or telemetry file is accessed by this setter.</remarks>
    /// <example><code>SqliteOperation.ConfigureTelemetry(telemetry);</code></example>
    public static void ConfigureTelemetry(LatencyTelemetryService telemetry) => Volatile.Write(ref _telemetry, telemetry);

    /// <summary>Executes an idempotent local database operation with at most three attempts.</summary>
    /// <typeparam name="T">Detached result type; never return a live context or query.</typeparam>
    /// <param name="operation">Required local operation; creates a fresh context on every attempt.</param>
    /// <param name="cancellationToken">Cancellation of database work and retry delays.</param>
    /// <param name="operationName">Compiler-supplied internal caller name; only explicit allowlisted categories are emitted. Arbitrary input maps to sqlite_local.</param>
    /// <returns>The operation result after a successful commit; no additional save is required.</returns>
    /// <exception cref="SqliteException">The final BUSY/LOCKED error or any non-retryable SQLite error.</exception>
    /// <remarks>Only SQLite primary codes 5 and 6 are retried, preserving three attempts and the existing 50/150 ms + 0–50 ms jitter. A failed commit must have been rolled back by disposal. Diagnostic durations include existing retries and backoff, never network or financial work added by instrumentation.</remarks>
    /// <example><code>await SqliteOperation.RunAsync(async token =&gt; { await using var db = factory.CreateDbContext(); return await db.Users.CountAsync(token); }, token);</code></example>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default,
        [CallerMemberName] string operationName = null)
    {
        var scope = TelegramUpdateLatencyScope.Current;
        var telemetry = Volatile.Read(ref _telemetry) ?? scope?.Telemetry;
        var observed = !LatencyTelemetrySuppression.IsActive && (telemetry?.Enabled == true || scope != null);
        var started = observed ? Stopwatch.GetTimestamp() : 0;
        var category = observed ? OperationCategory(operationName) : null;
        var retryCount = 0;
        var attemptCount = 0;
        double busyWaitMs = 0;
        Exception failure = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptCount++;
                try { return await operation(cancellationToken); }
                catch (Exception ex) when (attempt < 2 && IsBusy(ex))
                {
                    Retries.Add(1);
                    retryCount++;
                    var delayMs = (attempt == 0 ? 50 : 150) + Random.Shared.Next(0, 51);
                    var waiting = observed ? Stopwatch.GetTimestamp() : 0;
                    using var stage = scope?.Measure(TelegramUpdateStage.SqliteBusyRetry) ?? default;
                    try { await Task.Delay(delayMs, cancellationToken); }
                    finally
                    {
                        if (observed)
                        {
                            var elapsed = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds;
                            busyWaitMs += elapsed;
                            scope?.RecordSqliteBusyRetry(elapsed);
                            Record(telemetry, scope, "sqlite_busy_retry", category, elapsed, retryCount, busyWaitMs,
                                attempt + 1, ex, cancellationToken.IsCancellationRequested ? "cancelled" : "retrying");
                        }
                    }
                }
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            if (observed)
                Record(telemetry, scope, "sqlite_operation_completed", category, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    retryCount, busyWaitMs, attemptCount, failure,
                    failure is OperationCanceledException ? "cancelled" : failure == null ? "completed" : "failed");
        }
    }

    /// <summary>Normalizes internal caller names to a finite diagnostic operation vocabulary.</summary>
    /// <param name="caller">Compiler-supplied caller name, or arbitrary explicit input that must not be emitted.</param>
    /// <returns>A closed category; unrecognized values never appear in telemetry.</returns>
    private static string OperationCategory(string caller) => caller switch
    {
        "TryAcceptAsync" or "PersistAsync" or "AdmitAsync" or "ClaimAsync" or "FinishAsync" or "RecoverAsync" or "ClaimNextAsync" => "inbox",
        "DebitAsync" or "CreditAsync" or "RefundAsync" or "GetBalanceAsync" => "wallet",
        "SettleAsync" or "ConfirmAsync" or "ReconcileAsync" => "payment",
        "ReserveAsync" or "TryStartPostAsync" or "MarkFailureAsync" or "MarkAppliedAsync" => "xui_operation",
        "SaveAsync" or "SaveChangesAsync" => "persistence",
        "GetAsync" or "FindAsync" or "LoadAsync" => "lookup",
        _ => "sqlite_local"
    };

    /// <summary>Enqueues one retry or logical-operation observation without changing persistence semantics.</summary>
    /// <param name="telemetry">Optional singleton nonblocking writer.</param>
    /// <param name="scope">Optional update-local trace metadata owner.</param>
    /// <param name="eventType">Compile-time retry/completion event family.</param>
    /// <param name="category">Allowlisted operation category, never a caller input string.</param>
    /// <param name="elapsedMs">Measured inclusive logical-operation or retry-wait milliseconds.</param>
    /// <param name="retryCount">Actual BUSY/LOCKED retry count, from zero through two.</param>
    /// <param name="busyWaitMs">Accumulated measured retry-delay milliseconds.</param>
    /// <param name="attempt">One-based actual invocation count; zero if cancelled before the first invocation.</param>
    /// <param name="failure">Optional original failure; only safe category and numeric SQLite code are inspected.</param>
    /// <param name="outcome">Closed completed, failed, cancelled, or retrying category.</param>
    /// <remarks>Background records deliberately omit unavailable update identity rather than inventing an update.</remarks>
    private static void Record(LatencyTelemetryService telemetry, TelegramUpdateLatencyScope scope, string eventType,
        string category, double elapsedMs, int retryCount, double busyWaitMs, int attempt, Exception failure, string outcome)
    {
        try
        {
            if (LatencyTelemetrySuppression.IsActive || telemetry?.Enabled != true) return;
            var receiver = UpdateTelemetryTracker.Current;
            telemetry.TryRecord(new LatencyTelemetryEvent
            {
                EventType = eventType, TraceId = scope?.TraceId ?? receiver?.TraceId,
                BotId = scope?.BotId ?? receiver?.BotId, UpdateId = scope?.UpdateId ?? receiver?.UpdateId,
                Sequence = scope == null ? receiver?.Sequence : scope.Sequence > 0 ? scope.Sequence : null, Operation = category,
                Category = LatencySqliteOperationScope.Current,
                Stage = eventType == "sqlite_busy_retry" ? "sqlite_busy_retry" : null,
                DurationMs = elapsedMs, BusyRetryCount = retryCount, BusyWaitMs = busyWaitMs, Attempt = attempt,
                Outcome = outcome, SqliteErrorCode = LatencySqliteObserver.ErrorCode(failure),
                ExceptionCategory = LatencySqliteObserver.ExceptionCategory(failure),
                FailureClassification = LatencySqliteObserver.ErrorCode(failure) is 5 or 6 ? "sqlite_contention" : failure == null ? null : "sqlite_failure",
                CancellationSource = outcome == "cancelled" ? "caller" : null, TimingQuality = "measured_wall_time"
            });
        }
        catch { /* Diagnostic enqueue failures must not replace local operation results. */ }
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
