using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Adminbot.Services.Telemetry;

/// <summary>Measures successful SQLite transaction ownership independently of active command and handler stages.</summary>
/// <remarks>Transaction lifetime includes awaited business/network work, so its inclusive diagnostic must never be summed with command durations or handler StageMs. Only opaque ownership ids, clocks and coarse trace identity are retained. EF has no disposal interceptor hook; the sole subscribed DiagnosticListener event is TransactionDisposed, without enabling EF logging.</remarks>
internal sealed class LatencySqliteTransactionLifetimeObserver : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>>
{
    /// <summary>Hard cap on active lifetime identities; no overflow ownership is retained.</summary>
    private const int MaximumActiveTransactions = 1024;
    /// <summary>Bounds filtered listener subscription ownership; EF normally exposes one process listener.</summary>
    private const int MaximumListeners = 16;
    /// <summary>Serializes short metadata changes, never database, network or handler work.</summary>
    private readonly Lock _gate = new();
    /// <summary>Nonblocking application writer, never a logger or database owner.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Monotonic/UTC clock shared only with the optional deterministic measurement seam.</summary>
    private readonly TimeProvider _clock;
    /// <summary>Bounded active identities, without transaction, context or scope references.</summary>
    private readonly Dictionary<Guid, ActiveTransaction> _active = new();
    /// <summary>Filtered listener subscription disposables, never provider objects.</summary>
    private readonly List<IDisposable> _listeners = new();
    /// <summary>Listener discovery subscription released at interceptor disposal.</summary>
    private readonly IDisposable _allListeners;
    /// <summary>Idempotent disposal flag excluding late diagnostic callbacks.</summary>
    private int _disposed;

    /// <summary>Subscribes exclusively to transaction-disposal notifications for an enabled collector.</summary>
    /// <param name="telemetry">Enabled singleton nonblocking writer.</param>
    /// <param name="timeProvider">Optional monotonic/UTC clock; null uses TimeProvider.System.</param>
    /// <remarks>The containing interceptor creates this observer only when collection is enabled and owns its disposal.</remarks>
    internal LatencySqliteTransactionLifetimeObserver(LatencyTelemetryService telemetry, TimeProvider timeProvider)
    {
        _telemetry = telemetry;
        _clock = timeProvider ?? TimeProvider.System;
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>Starts lifetime only after SQLite successfully creates its provider transaction.</summary>
    /// <param name="id">Opaque EF transaction ownership id.</param>
    /// <param name="connectionId">Opaque EF connection ownership id for fallback cleanup.</param>
    /// <param name="connection">Provider connection used only for SQLite classification, never retained.</param>
    /// <remarks>Overflow emits an explicit unavailable-duration incident, not a fabricated completed transaction.</remarks>
    internal void Start(Guid id, Guid connectionId, DbConnection connection)
    {
        try
        {
            if (connection is not SqliteConnection || LatencyTelemetrySuppression.IsActive || Volatile.Read(ref _disposed) != 0) return;
            var scope = TelegramUpdateLatencyScope.Current;
            var receiver = UpdateTelemetryTracker.Current;
            var identity = new ActiveTransaction(connectionId, _clock.GetTimestamp(), _clock.GetUtcNow().UtcDateTime,
                scope?.TraceId ?? receiver?.TraceId, scope?.BotId ?? receiver?.BotId, scope?.UpdateId ?? receiver?.UpdateId,
                scope == null ? receiver?.Sequence : scope.Sequence > 0 ? scope.Sequence : null, LatencySqliteOperationScope.Current);
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _active.ContainsKey(id)) return;
                if (_active.Count < MaximumActiveTransactions) { _active.Add(id, identity); return; }
            }
            _telemetry.TryRecord(new LatencyTelemetryEvent
            {
                EventType = "telemetry_incident", TimestampUtc = identity.StartedAtUtc,
                TraceId = identity.TraceId, BotId = identity.BotId, UpdateId = identity.UpdateId, Sequence = identity.Sequence,
                Stage = "sqlite_transaction", Operation = "transaction_lifetime", Category = "sqlite_transaction_metadata",
                Outcome = "degraded", TimingQuality = "metadata_capacity_exceeded", FailureClassification = "sqlite_metadata_capacity"
            });
        }
        catch { /* Instrumentation never changes successful transaction creation. */ }
    }

    /// <summary>Completes an owned lifetime once, without assigning any time to an active handler stage.</summary>
    /// <param name="id">Opaque EF transaction id captured after successful begin.</param>
    /// <param name="outcome">Compile-time completed, rolled_back or disposed terminal category.</param>
    /// <param name="timingQuality">Closed provenance; connection fallback explicitly lacks the exact transaction endpoint.</param>
    /// <remarks>Removal happens before observer calls; subsequent disposal after commit/rollback is idempotent.</remarks>
    internal void Complete(Guid id, string outcome, string timingQuality = "inclusive_transaction_lifetime_not_handler_stage")
    {
        try
        {
            ActiveTransaction active;
            lock (_gate)
                if (!_active.Remove(id, out active)) return;
            Record(active, outcome, timingQuality);
        }
        catch { /* Transaction outcomes and exceptions belong solely to the provider/caller. */ }
    }

    /// <summary>Releases remaining lifetime identities when EF closes or disposes their connection.</summary>
    /// <param name="connectionId">Opaque connection id, never a provider path or connection string.</param>
    /// <remarks>Used only when no earlier commit, rollback or precise TransactionDisposed notification released ownership. The delayed endpoint is explicitly labeled unavailable.</remarks>
    internal void CompleteConnection(Guid connectionId)
    {
        try
        {
            lock (_gate)
            {
                while (true)
                {
                    Guid id = default;
                    bool found = false;
                    foreach (var pair in _active)
                        if (pair.Value.ConnectionId == connectionId) { id = pair.Key; found = true; break; }
                    if (!found) return;
                    var active = _active[id];
                    _active.Remove(id);
                    Record(active, "disposed", "transaction_end_unavailable_connection_cleanup");
                }
            }
        }
        catch { /* Diagnostic cleanup never alters provider connection cleanup. */ }
    }

    /// <summary>Aggregates healthy fast uncorrelated commits or emits one sanitized inclusive lifetime completion with captured origin identity.</summary>
    /// <param name="active">Opaque lifetime clocks and coarse correlation only.</param>
    /// <param name="outcome">Fixed terminal category assigned by an actual provider notification.</param>
    /// <param name="timingQuality">Closed provenance separating lifetime from command/stage accounting.</param>
    /// <remarks>UTC origin is retained for provenance but duration uses only the monotonic clock. Slow, correlated, rollback, disposal and uncertain endpoints remain detailed; fast successful background commits allocate no event.</remarks>
    private void Record(ActiveTransaction active, string outcome, string timingQuality)
    {
        if (LatencyTelemetrySuppression.IsActive || Volatile.Read(ref _disposed) != 0) return;
        var elapsedMs = _clock.GetElapsedTime(active.StartedTimestamp, _clock.GetTimestamp()).TotalMilliseconds;
        if (timingQuality == "inclusive_transaction_lifetime_not_handler_stage"
            && _telemetry.TryAggregateBackgroundSqlite("sqlite_transaction_completed", "transaction_lifetime", active.Category,
                elapsedMs, outcome, active.TraceId != null || active.BotId != null || active.UpdateId != null || active.Sequence != null)) return;
        _telemetry.TryRecord(new LatencyTelemetryEvent
        {
            EventType = "sqlite_transaction_completed", TimestampUtc = _clock.GetUtcNow().UtcDateTime,
            TraceId = active.TraceId, BotId = active.BotId, UpdateId = active.UpdateId, Sequence = active.Sequence,
            Stage = "sqlite_transaction", Operation = "transaction_lifetime", Outcome = outcome,
            Category = active.Category,
            DurationMs = elapsedMs,
            TimingQuality = timingQuality
        });
    }

    /// <summary>Attaches only to EF's diagnostic listener and only enables the transaction-disposed event.</summary>
    /// <param name="listener">Discovered process listener; never retained as a database/context owner.</param>
    /// <remarks>Subscriptions are bounded and released by Dispose. No command, query, transaction payload or verbose EF event stream is enabled.</remarks>
    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        try
        {
            if (listener.Name != "Microsoft.EntityFrameworkCore") return;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _listeners.Count >= MaximumListeners) return;
                _listeners.Add(listener.Subscribe(this, name => name == RelationalEventId.TransactionDisposed.Name));
            }
        }
        catch { /* Listener discovery cannot prevent EF context creation. */ }
    }

    /// <summary>Consumes the one allowed EF disposal event without retaining its transaction or context payload.</summary>
    /// <param name="notification">Transient framework-owned event envelope.</param>
    /// <remarks>Only a previously captured SQLite transaction id can produce a lifetime completion.</remarks>
    void IObserver<KeyValuePair<string, object>>.OnNext(KeyValuePair<string, object> notification)
    {
        if (notification.Key == RelationalEventId.TransactionDisposed.Name && notification.Value is TransactionEventData transaction)
            Complete(transaction.TransactionId, "disposed");
    }

    /// <summary>Ignores listener discovery completion; no provider ownership is affected.</summary>
    void IObserver<DiagnosticListener>.OnCompleted() { }
    /// <summary>Ignores listener discovery failure without logging exception payloads.</summary>
    /// <param name="error">Framework observer failure, never retained or serialized.</param>
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    /// <summary>Ignores EF event stream completion; transaction endpoints are not fabricated.</summary>
    void IObserver<KeyValuePair<string, object>>.OnCompleted() { }
    /// <summary>Ignores EF event stream failure without changing business execution.</summary>
    /// <param name="error">Framework observer failure, never retained or serialized.</param>
    void IObserver<KeyValuePair<string, object>>.OnError(Exception error) { }

    /// <summary>Detaches diagnostic subscriptions and releases metadata without disposing any provider resource.</summary>
    /// <remarks>Register the containing interceptor as a DI-owned singleton so shutdown invokes this method. Shutdown is not a transaction endpoint and does not fabricate lifetime completions.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _allListeners.Dispose(); } catch { /* Diagnostic shutdown cannot fail host disposal. */ }
        lock (_gate)
        {
            foreach (var subscription in _listeners)
                try { subscription.Dispose(); } catch { /* Continue detaching independent subscriptions. */ }
            _listeners.Clear();
            _active.Clear();
        }
    }

    /// <summary>Contains only bounded internal ownership, clocks and coarse trace identity.</summary>
    /// <param name="ConnectionId">Opaque EF connection id for fallback cleanup.</param>
    /// <param name="StartedTimestamp">Monotonic begin-success timestamp.</param>
    /// <param name="StartedAtUtc">UTC begin-success instant, retained only for origin/overflow provenance.</param>
    /// <param name="TraceId">Opaque trace id, or null for background work.</param>
    /// <param name="BotId">Internal bounded bot id, or null for background work.</param>
    /// <param name="UpdateId">Telegram update id, never a user/chat/customer id.</param>
    /// <param name="Sequence">Positive durable sequence, or null when absent.</param>
    /// <param name="Category">Optional fixed background SQLite worker category, never a tenant/customer identity.</param>
    private readonly record struct ActiveTransaction(Guid ConnectionId, long StartedTimestamp, DateTime StartedAtUtc,
        string TraceId, string BotId, long? UpdateId, long? Sequence, string Category);
}
