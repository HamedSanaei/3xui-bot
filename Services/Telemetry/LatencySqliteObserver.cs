using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Adminbot.Services.Telemetry;

/// <summary>Records bounded payload-free SQLite execution boundaries for foreground and background callers.</summary>
/// <param name="telemetry">Application singleton nonblocking writer; null disables background events.</param>
/// <remarks>Never retains commands, transactions, SQL, parameters, contexts, paths, or connection strings. Only active ownership ids and closed categories are retained.</remarks>
internal sealed class LatencySqliteObserver(LatencyTelemetryService telemetry)
{
    /// <summary>Maximum simultaneously retained operation handles; completions still emit duration when this bound is reached.</summary>
    private const int MaximumActiveOperations = 1024;
    /// <summary>Protects only short diagnostic ownership changes, never database work.</summary>
    private readonly Lock _gate = new();
    /// <summary>Active stage handles keyed by EF-generated opaque operation ids.</summary>
    private readonly Dictionary<Guid, ActiveOperation> _active = new();

    /// <summary>Starts a SQLite boundary without changing or suppressing provider execution.</summary>
    /// <param name="id">EF-generated command or transaction id; never a customer identifier.</param>
    /// <param name="connection">Provider connection used only for the SQLite type check; not retained or inspected.</param>
    /// <param name="stage">Compile-time read/write stage category.</param>
    /// <param name="operation">Compile-time operation category, such as sqlite_read or transaction_commit.</param>
    /// <param name="connectionId">Opaque EF connection ownership for cleanup of aborted transaction begin boundaries.</param>
    /// <remarks>Disabled background recording returns immediately. A foreground scope still receives stage accounting.</remarks>
    internal void Start(Guid id, DbConnection connection, TelegramUpdateStage stage, string operation, Guid connectionId = default)
    {
        try
        {
            if (LatencyTelemetrySuppression.IsActive) return;
            var scope = TelegramUpdateLatencyScope.Current;
            if (connection is not SqliteConnection || (scope == null && telemetry?.Enabled != true)) return;
            lock (_gate)
            {
                if (_active.Count >= MaximumActiveOperations || _active.ContainsKey(id)) return;
                _active.Add(id, new ActiveOperation(Stopwatch.GetTimestamp(), scope, scope?.Measure(stage) ?? default,
                    operation, stage, connectionId, LatencySqliteOperationScope.Current));
            }
        }
        catch { /* Diagnostics cannot change provider behavior. */ }
    }

    /// <summary>Completes one SQLite boundary and emits a sanitized result, including operations with no update scope.</summary>
    /// <param name="id">EF-generated ownership id matching the start notification.</param>
    /// <param name="connection">Provider connection for a SQLite-only check; never retained.</param>
    /// <param name="duration">Optional EF-reported execution duration when no active slot exists; null means unavailable, never zero.</param>
    /// <param name="eventType">Compile-time command or transaction completion event family.</param>
    /// <param name="operation">Compile-time fallback category if no active slot was retained.</param>
    /// <param name="stage">Compile-time fallback read/write stage.</param>
    /// <param name="outcome">Closed completed, failed, or cancelled category.</param>
    /// <param name="exception">Optional provider failure inspected only for numeric SQLite code and closed exception category.</param>
    /// <remarks>Disposes the exact stage handle, then performs only a nonblocking writer enqueue. No SQL metadata is emitted.</remarks>
    internal void Complete(Guid id, DbConnection connection, TimeSpan? duration, string eventType, string operation,
        TelegramUpdateStage stage, string outcome, Exception exception = null)
    {
        try
        {
            ActiveOperation active;
            bool found;
            lock (_gate) found = _active.Remove(id, out active);
            if (!found && connection is not SqliteConnection) return;
            if (found) active.Timer.Dispose();
            if (LatencyTelemetrySuppression.IsActive || telemetry?.Enabled != true) return;
            var scope = found ? active.Scope : TelegramUpdateLatencyScope.Current;
            var receiver = UpdateTelemetryTracker.Current;
            telemetry.TryRecord(new LatencyTelemetryEvent
            {
                EventType = eventType, TraceId = scope?.TraceId ?? receiver?.TraceId,
                BotId = scope?.BotId ?? receiver?.BotId, UpdateId = scope?.UpdateId ?? receiver?.UpdateId,
                Sequence = scope == null ? receiver?.Sequence : scope.Sequence > 0 ? scope.Sequence : null,
                Stage = TelegramUpdateLatencyScope.StageName(found ? active.Stage : stage),
                Operation = found ? active.Operation : operation, Outcome = outcome,
                Category = found ? active.Category : LatencySqliteOperationScope.Current,
                DurationMs = found ? Stopwatch.GetElapsedTime(active.Started).TotalMilliseconds : duration?.TotalMilliseconds,
                SqliteErrorCode = ErrorCode(exception), ExceptionCategory = ExceptionCategory(exception),
                CancellationSource = outcome == "cancelled" ? "caller" : null,
                FailureClassification = ErrorCode(exception) is 5 or 6 ? "sqlite_contention" : exception == null ? null : "sqlite_failure",
                TimingQuality = found ? "measured_wall_time" : duration.HasValue ? "provider_duration" : "boundary_duration_unavailable"
            });
        }
        catch { /* Telemetry cannot replace database results or failures. */ }
    }

    /// <summary>Releases orphaned begin measurements when EF disposes or closes their provider connection.</summary>
    /// <param name="connectionId">Opaque EF connection ownership id; never a database path or customer value.</param>
    /// <remarks>EF can omit TransactionFailed when begin throws before a transaction exists. Closure establishes an aborted boundary, not its missing exception details.</remarks>
    internal void AbortConnection(Guid connectionId)
    {
        try
        {
            lock (_gate)
            {
                while (true)
                {
                    Guid foundId = default;
                    bool found = false;
                    foreach (var pair in _active)
                        if (pair.Value.ConnectionId == connectionId) { foundId = pair.Key; found = true; break; }
                    if (!found) return;
                    var active = _active[foundId];
                    _active.Remove(foundId);
                    active.Timer.Dispose();
                    if (telemetry?.Enabled == true && !LatencyTelemetrySuppression.IsActive)
                        telemetry.TryRecord(new LatencyTelemetryEvent
                        {
                            EventType = "sqlite_transaction_completed", TraceId = active.Scope?.TraceId ?? UpdateTelemetryTracker.Current?.TraceId,
                            BotId = active.Scope?.BotId ?? UpdateTelemetryTracker.Current?.BotId,
                            UpdateId = active.Scope?.UpdateId ?? UpdateTelemetryTracker.Current?.UpdateId,
                            Sequence = active.Scope == null ? UpdateTelemetryTracker.Current?.Sequence
                                : active.Scope.Sequence > 0 ? active.Scope.Sequence : null,
                            Operation = active.Operation, Stage = TelegramUpdateLatencyScope.StageName(active.Stage),
                            Category = active.Category,
                            DurationMs = Stopwatch.GetElapsedTime(active.Started).TotalMilliseconds, Outcome = "aborted",
                            TimingQuality = "boundary_end_unavailable", FailureClassification = "sqlite_boundary_aborted"
                        });
                }
            }
        }
        catch { /* Provider cleanup is never prevented by diagnostic cleanup. */ }
    }

    /// <summary>Reads only a SQLite numeric primary result code, including an EF save wrapper.</summary>
    /// <param name="exception">Optional failure; message and stack trace are never inspected.</param>
    /// <returns>SQLite primary result code, or null for unavailable/non-SQLite failures.</returns>
    internal static int? ErrorCode(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode,
        DbUpdateException { InnerException: SqliteException sqlite } => sqlite.SqliteErrorCode,
        _ => null
    };

    /// <summary>Classifies a provider exception without retaining its message or dynamic type name.</summary>
    /// <param name="exception">Optional original provider exception, never serialized.</param>
    /// <returns>A closed category, or null when execution succeeded.</returns>
    internal static string ExceptionCategory(Exception exception) => exception switch
    {
        null => null,
        OperationCanceledException => "cancellation",
        SqliteException => "sqlite",
        DbUpdateException => "ef_update",
        _ => "other"
    };

    /// <summary>Retains only active measurement ownership and safe operation categories.</summary>
    /// <param name="Started">Monotonic operation start timestamp.</param>
    /// <param name="Scope">Optional update metadata owner; contains no customer payload.</param>
    /// <param name="Timer">Idempotent stage completion handle.</param>
    /// <param name="Operation">Closed operation category.</param>
    /// <param name="Stage">Closed read/write stage category.</param>
    /// <param name="ConnectionId">Opaque EF connection ownership for orphan cleanup.</param>
    /// <param name="Category">Optional fixed background worker category captured at operation start.</param>
    private readonly record struct ActiveOperation(long Started, TelegramUpdateLatencyScope Scope,
        TelegramUpdateLatencyScope.StageTimer Timer, string Operation, TelegramUpdateStage Stage, Guid ConnectionId, string Category);
}
