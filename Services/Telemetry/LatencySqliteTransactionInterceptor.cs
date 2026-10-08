using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Adminbot.Services.Telemetry;

/// <summary>Measures SQLite provider transaction boundaries and separately records inclusive transaction lifetime.</summary>
/// <remarks>Boundary timers alone participate in handler stages. Lifetime includes awaited work between begin-success and commit/rollback/disposal, uses sqlite_transaction diagnostics, and must never be summed with boundary or exclusive handler durations.</remarks>
public sealed class LatencySqliteTransactionInterceptor : DbTransactionInterceptor, IDisposable
{
    /// <summary>Bounded transaction-boundary ownership and sanitized completion recorder.</summary>
    private readonly LatencySqliteObserver _observer;

    /// <summary>Enabled-only identity/clock ownership and narrowly filtered EF disposal notifications.</summary>
    private readonly LatencySqliteTransactionLifetimeObserver _lifetime;

    /// <summary>Creates a payload-free transaction observer for foreground and background SQLite contexts.</summary>
    /// <param name="telemetry">Application singleton writer; null disables background records.</param>
    /// <param name="timeProvider">Optional transaction-lifetime monotonic/UTC clock; null uses TimeProvider.System.</param>
    /// <remarks>Register as a DI-owned singleton alongside ConnectionCleanupInterceptor; DI disposes its filtered diagnostic subscriptions. Transaction behavior is never suppressed or retried.</remarks>
    /// <example><code>var transactions = new LatencySqliteTransactionInterceptor(telemetry); options.AddInterceptors(transactions, transactions.ConnectionCleanupInterceptor);</code></example>
    public LatencySqliteTransactionInterceptor(LatencyTelemetryService telemetry, TimeProvider timeProvider = null)
    {
        _observer = new(telemetry);
        _lifetime = telemetry?.Enabled == true ? new(telemetry, timeProvider) : null;
        ConnectionCleanupInterceptor = new LatencySqliteConnectionCleanupInterceptor(_observer, _lifetime);
    }

    /// <summary>Gets the companion cleanup observer required to release aborted begin and orphaned lifetime metadata.</summary>
    /// <remarks>Register beside this exact transaction interceptor instance. Fallback cleanup explicitly marks the unavailable exact transaction endpoint.</remarks>
    public DbConnectionInterceptor ConnectionCleanupInterceptor { get; }

    /// <summary>Starts one provider transaction boundary, not the encompassing transaction lifetime.</summary>
    /// <param name="connection">SQLite provider connection used only for type classification.</param>
    /// <param name="eventData">EF-generated transaction ownership metadata.</param>
    /// <param name="operation">Compile-time begin, commit, rollback, or savepoint category.</param>
    private void Start(DbConnection connection, TransactionEventData eventData, string operation)
        => _observer.Start(eventData.TransactionId, connection, TelegramUpdateStage.SqliteWrite, operation, eventData.ConnectionId);

    /// <summary>Starts the begin boundary before a provider transaction object exists.</summary>
    /// <param name="connection">Provider connection used only for a SQLite type check.</param>
    /// <param name="eventData">EF-generated begin metadata with an opaque transaction id.</param>
    /// <param name="operation">Compile-time transaction_begin category.</param>
    private void Start(DbConnection connection, TransactionStartingEventData eventData, string operation)
        => _observer.Start(eventData.TransactionId, connection, TelegramUpdateStage.SqliteWrite, operation, eventData.ConnectionId);

    /// <summary>Completes one transaction boundary without altering its outcome.</summary>
    /// <param name="connection">Provider connection used only for SQLite classification.</param>
    /// <param name="eventData">EF-generated transaction ownership and duration metadata.</param>
    /// <param name="operation">Closed fallback operation category.</param>
    /// <param name="outcome">Closed completed or failed category.</param>
    /// <param name="exception">Optional provider exception; only numeric code/category are retained.</param>
    private void Complete(DbConnection connection, TransactionEndEventData eventData, string operation, string outcome, Exception exception = null)
    {
        _observer.Complete(eventData.TransactionId, connection, eventData.Duration, "sqlite_transaction_completed", operation,
            TelegramUpdateStage.SqliteWrite, outcome, exception);
        if (outcome != "completed") return;
        if (operation == "transaction_begin") _lifetime?.Start(eventData.TransactionId, eventData.ConnectionId, connection);
        else if (operation == "transaction_commit") _lifetime?.Complete(eventData.TransactionId, "completed");
        else if (operation == "transaction_rollback") _lifetime?.Complete(eventData.TransactionId, "rolled_back");
    }

    /// <summary>Starts measurement immediately before the synchronous SQLite begin boundary.</summary>
    /// <param name="connection">Provider connection, never retained or serialized.</param>
    /// <param name="eventData">EF ownership metadata for this transaction begin.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <returns>The original result, leaving isolation level and provider execution unchanged.</returns>
    /// <remarks>Only metadata is recorded; no transaction is created by this interceptor.</remarks>
    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Start(connection, eventData, "transaction_begin");
        return result;
    }

    /// <summary>Completes measurement when the provider has begun its transaction.</summary>
    /// <param name="connection">Provider connection, never inspected for secrets.</param>
    /// <param name="eventData">EF duration and ownership metadata.</param>
    /// <param name="result">Provider transaction returned unchanged.</param>
    /// <returns>The original transaction with the caller's existing ownership.</returns>
    /// <remarks>No transaction lifetime, financial operation, or retry behavior changes.</remarks>
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Complete(connection, eventData, "transaction_begin", "completed");
        return result;
    }

    /// <summary>Starts measurement immediately before the asynchronous SQLite begin boundary.</summary>
    /// <param name="connection">Provider connection, never retained or serialized.</param>
    /// <param name="eventData">EF ownership metadata for this transaction begin.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation, unchanged by observation.</param>
    /// <returns>The original result, leaving isolation level and provider execution unchanged.</returns>
    /// <remarks>Only metadata is recorded; no transaction is created by this interceptor.</remarks>
    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        Start(connection, eventData, "transaction_begin");
        return ValueTask.FromResult(result);
    }

    /// <summary>Completes measurement when the provider has begun its transaction.</summary>
    /// <param name="connection">Provider connection, never inspected for secrets.</param>
    /// <param name="eventData">EF duration and ownership metadata.</param>
    /// <param name="result">Provider transaction returned unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation, unchanged by observation.</param>
    /// <returns>The original transaction with the caller's existing ownership.</returns>
    /// <remarks>No transaction lifetime, financial operation, or retry behavior changes.</remarks>
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Complete(connection, eventData, "transaction_begin", "completed");
        return ValueTask.FromResult(result);
    }

    /// <summary>Starts the synchronous transaction commit provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Start(transaction?.Connection, eventData, "transaction_commit");
        return result;
    }

    /// <summary>Records the completed synchronous transaction commit boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Complete(transaction?.Connection, eventData, "transaction_commit", "completed");
    }

    /// <summary>Starts the asynchronous transaction commit provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Start(transaction?.Connection, eventData, "transaction_commit");
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the completed asynchronous transaction commit boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>A completed notification task; transaction outcome is unchanged.</returns>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(transaction?.Connection, eventData, "transaction_commit", "completed");
        return Task.CompletedTask;
    }

    /// <summary>Starts the synchronous transaction rollback provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Start(transaction?.Connection, eventData, "transaction_rollback");
        return result;
    }

    /// <summary>Records the completed synchronous transaction rollback boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Complete(transaction?.Connection, eventData, "transaction_rollback", "completed");
    }

    /// <summary>Starts the asynchronous transaction rollback provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Start(transaction?.Connection, eventData, "transaction_rollback");
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the completed asynchronous transaction rollback boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>A completed notification task; transaction outcome is unchanged.</returns>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(transaction?.Connection, eventData, "transaction_rollback", "completed");
        return Task.CompletedTask;
    }

    /// <summary>Starts the synchronous savepoint create provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override InterceptionResult CreatingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Start(transaction?.Connection, eventData, "savepoint_create");
        return result;
    }

    /// <summary>Records the completed synchronous savepoint create boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, null, "sqlite_transaction_completed", "savepoint_create", TelegramUpdateStage.SqliteWrite, "completed");
    }

    /// <summary>Starts the asynchronous savepoint create provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Start(transaction?.Connection, eventData, "savepoint_create");
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the completed asynchronous savepoint create boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>A completed notification task; transaction outcome is unchanged.</returns>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, null, "sqlite_transaction_completed", "savepoint_create", TelegramUpdateStage.SqliteWrite, "completed");
        return Task.CompletedTask;
    }

    /// <summary>Starts the synchronous savepoint rollback provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override InterceptionResult RollingBackToSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Start(transaction?.Connection, eventData, "savepoint_rollback");
        return result;
    }

    /// <summary>Records the completed synchronous savepoint rollback boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, null, "sqlite_transaction_completed", "savepoint_rollback", TelegramUpdateStage.SqliteWrite, "completed");
    }

    /// <summary>Starts the asynchronous savepoint rollback provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override ValueTask<InterceptionResult> RollingBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Start(transaction?.Connection, eventData, "savepoint_rollback");
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the completed asynchronous savepoint rollback boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>A completed notification task; transaction outcome is unchanged.</returns>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, null, "sqlite_transaction_completed", "savepoint_rollback", TelegramUpdateStage.SqliteWrite, "completed");
        return Task.CompletedTask;
    }

    /// <summary>Starts the synchronous savepoint release provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override InterceptionResult ReleasingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Start(transaction?.Connection, eventData, "savepoint_release");
        return result;
    }

    /// <summary>Records the completed synchronous savepoint release boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, null, "sqlite_transaction_completed", "savepoint_release", TelegramUpdateStage.SqliteWrite, "completed");
    }

    /// <summary>Starts the asynchronous savepoint release provider boundary.</summary>
    /// <param name="transaction">Existing provider transaction, never retained or serialized.</param>
    /// <param name="eventData">EF-generated ownership metadata.</param>
    /// <param name="result">Existing interception result preserved unchanged.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>The original interception result; provider execution is not suppressed.</returns>
    /// <remarks>Only boundary wall time is measured; work between boundaries is excluded.</remarks>
    public override ValueTask<InterceptionResult> ReleasingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Start(transaction?.Connection, eventData, "savepoint_release");
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the completed asynchronous savepoint release boundary.</summary>
    /// <param name="transaction">Provider transaction; no entities or identifiers are recorded.</param>
    /// <param name="eventData">EF completion duration and ownership metadata.</param>
    /// <param name="cancellationToken">Original provider cancellation token, unchanged.</param>
    /// <returns>A completed notification task; transaction outcome is unchanged.</returns>
    /// <remarks>No financial, transaction, or cancellation behavior changes.</remarks>
    public override Task ReleasedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, null, "sqlite_transaction_completed", "savepoint_release", TelegramUpdateStage.SqliteWrite, "completed");
        return Task.CompletedTask;
    }

    /// <summary>Records a failed provider boundary without changing rollback or failure propagation.</summary>
    /// <param name="transaction">Provider transaction if one exists; not retained.</param>
    /// <param name="eventData">EF failure metadata; exception content is never emitted.</param>
    /// <remarks>SQLite numeric codes distinguish contention from other failures; exception messages are excluded.</remarks>
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, eventData.Duration, "sqlite_transaction_completed", "transaction", TelegramUpdateStage.SqliteWrite, eventData.Exception is OperationCanceledException ? "cancelled" : "failed", eventData.Exception);
    }

    /// <summary>Records a failed provider boundary without changing rollback or failure propagation.</summary>
    /// <param name="transaction">Provider transaction if one exists; not retained.</param>
    /// <param name="eventData">EF failure metadata; exception content is never emitted.</param>
    /// <param name="cancellationToken">Original provider cancellation token.</param>
    /// <returns>A completed notification task without suppressing failure.</returns>
    /// <remarks>SQLite numeric codes distinguish contention from other failures; exception messages are excluded.</remarks>
    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _observer.Complete(eventData.TransactionId, transaction?.Connection, eventData.Duration, "sqlite_transaction_completed", "transaction", TelegramUpdateStage.SqliteWrite, eventData.Exception is OperationCanceledException ? "cancelled" : "failed", eventData.Exception);
        return Task.CompletedTask;
    }

    /// <summary>Releases only diagnostic lifetime metadata and filtered subscriptions owned by this interceptor.</summary>
    /// <remarks>DI shutdown does not commit, roll back, dispose provider resources or fabricate transaction endpoints.</remarks>
    public void Dispose() => _lifetime?.Dispose();
}
