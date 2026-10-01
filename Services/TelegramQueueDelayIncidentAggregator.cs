using System;
using System.Collections.Generic;

/// <summary>Payload-free operator report containing only observations not included in an earlier report.</summary>
/// <param name="BotId">Canonical runtime bot id, or "multiple" when bounded overflow merges lanes.</param>
/// <param name="TelegramUserId">Telegram actor id; zero for mixed-lane overflow.</param>
/// <param name="AffectedCount">Number of newly reported delayed updates.</param>
/// <param name="MaxQueueWaitMs">Largest queue wait in this report, in milliseconds.</param>
/// <param name="DominantBlocker">Largest attributed positive overlap in this report, with lower sequence winning ties; nullable.</param>
/// <param name="StartedAtUtc">Earliest accepted time in this report.</param>
/// <param name="EndedAtUtc">Latest actual start in this report.</param>
/// <param name="WindowStartedAtUtc">Beginning of the bounded reporting window.</param>
/// <param name="WindowEndedAtUtc">Time the reporting window was closed.</param>
/// <param name="Reason">Closed report reason: first, periodic, severe, disjoint, expired, evicted, shutdown, or overflow.</param>
/// <param name="Overflow">True when overload merged reports while preserving their counts and extrema.</param>
internal sealed record TelegramQueueDelayIncidentSummary(
    string BotId, long TelegramUserId, long AffectedCount, double MaxQueueWaitMs,
    TelegramLaneExecutionSummary DominantBlocker, DateTime StartedAtUtc, DateTime EndedAtUtc,
    DateTime WindowStartedAtUtc, DateTime WindowEndedAtUtc, string Reason, bool Overflow = false);

/// <summary>Coalesces connected same-bot/user wait intervals into bounded, expiring operator incidents.</summary>
/// <remarks>
/// Observation performs memory operations only under a short instance lock: it never logs, sends, or launches tasks.
/// A first report is queued immediately. Further victims share the incident regardless of predecessor identity when
/// their accepted-to-started intervals touch/overlap and the last observation is less than ten seconds old.
/// New observations are reported every fixed ten-second window (not a sliding debounce), or on a wait of at least
/// thirty seconds and each subsequent doubling. Disjoint intervals and exactly ten seconds of inactivity start a new
/// incident. Expiry, eviction and shutdown close pending reports before discarding state. Counts always describe NEW
/// observations, so first plus later reports can be summed without double counting. The coordinator's existing two-
/// second recovery timer flushes due windows, giving a normal deadline of ten seconds plus one coordinator tick.
/// At most 1024 lanes and 1024 queued reports are retained by default; each maintenance pass visits at most that many
/// lanes. Report overflow retains counts, wait extrema, blocker extrema and interval bounds in one explicit mixed-lane
/// report instead of dropping pending observations. This is best-effort diagnostics, never durable business state.
/// </remarks>
internal sealed class TelegramQueueDelayIncidentAggregator
{
    /// <summary>Fixed maximum coalescing interval and idle retention.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    /// <summary>Protects memory-only observation and report bookkeeping; sinks are invoked after release.</summary>
    private readonly object _gate = new();
    /// <summary>Positive shared cap for retained lane states and queued individual reports.</summary>
    private readonly int _capacity;
    /// <summary>Exact bot/actor lookup into the bounded least-recently-observed lane list.</summary>
    private readonly Dictionary<TelegramUpdateExecutionKey, LinkedListNode<Incident>> _lanes = new();
    /// <summary>Least-recently-observed ordering used for deterministic capacity eviction.</summary>
    private readonly LinkedList<Incident> _recency = new();
    /// <summary>Pending immutable report snapshots, bounded independently of retained lane state.</summary>
    private readonly Queue<TelegramQueueDelayIncidentSummary> _reports = new();
    /// <summary>One lossless-count overflow bucket when the bounded report queue is full.</summary>
    private TelegramQueueDelayIncidentSummary _overflow;

    /// <summary>Creates an instance-local diagnostic buffer.</summary>
    /// <param name="capacity">Positive maximum lane states and queued reports, normally 1024.</param>
    /// <remarks>No timers or external resources are owned by this class.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive.</exception>
    /// <example><code>var incidents = new TelegramQueueDelayIncidentAggregator();</code></example>
    internal TelegramQueueDelayIncidentAggregator(int capacity = 1024)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Gets retained lane count for bounded-state verification.</summary>
    internal int LaneCount { get { lock (_gate) return _lanes.Count; } }

    /// <summary>Observes one long wait without invoking logging or any other external callback.</summary>
    /// <param name="key">Exact runtime bot/Telegram actor FIFO key.</param>
    /// <param name="acceptedAtUtc">Durable UTC acceptance, the wait interval's left endpoint.</param>
    /// <param name="startedAtUtc">Actual UTC execution start, the wait interval's right endpoint.</param>
    /// <param name="waitMs">Nonnegative observed queue wait in milliseconds.</param>
    /// <param name="blocker">Nullable metadata-only dominant predecessor for this victim.</param>
    /// <param name="nowUtc">UTC observation clock, supplied explicitly for deterministic window tests.</param>
    /// <param name="nowTimestampMs">Optional monotonic milliseconds for deterministic tests; production uses Environment.TickCount64, independent of UTC clock jumps.</param>
    /// <remarks>Changing predecessor ids never splits a connected incident. Invalid nonpositive intervals are ignored.</remarks>
    /// <example><code>incidents.Observe(item.Key, item.AcceptedAtUtc, started, waitMs, blocker, DateTime.UtcNow);</code></example>
    internal void Observe(TelegramUpdateExecutionKey key, DateTime acceptedAtUtc, DateTime startedAtUtc,
        double waitMs, TelegramLaneExecutionSummary blocker, DateTime nowUtc, long? nowTimestampMs = null)
    {
        if (startedAtUtc <= acceptedAtUtc || !double.IsFinite(waitMs) || waitMs < 0) return;
        var timestamp = nowTimestampMs ?? Environment.TickCount64;
        lock (_gate)
        {
            if (_lanes.TryGetValue(key, out var node))
            {
                var existing = node.Value;
                // Concurrent callers can capture timestamps before acquiring the lock in the opposite order.
                timestamp = Math.Max(timestamp, existing.LastSeenTimestamp);
                var expired = timestamp - existing.LastSeenTimestamp >= Window.TotalMilliseconds;
                var disconnected = acceptedAtUtc > existing.End || startedAtUtc < existing.Start;
                if (expired || disconnected)
                {
                    Close(existing, nowUtc, expired ? "expired" : "disjoint");
                    _lanes.Remove(key);
                    _recency.Remove(node);
                    node = null;
                }
                else if (timestamp - existing.WindowTimestamp >= Window.TotalMilliseconds)
                {
                    Close(existing, nowUtc, "periodic");
                    existing.WindowTimestamp = timestamp;
                    existing.WindowStart = nowUtc;
                }
            }
            if (node == null)
            {
                if (_lanes.Count == _capacity)
                {
                    var oldest = _recency.First;
                    Close(oldest.Value, nowUtc, "evicted");
                    _lanes.Remove(oldest.Value.Key);
                    _recency.RemoveFirst();
                }
                node = _recency.AddLast(new Incident(key, acceptedAtUtc, startedAtUtc, nowUtc, timestamp));
                _lanes.Add(key, node);
                Add(node.Value, acceptedAtUtc, startedAtUtc, waitMs, blocker);
                Close(node.Value, nowUtc, "first");
                node.Value.SevereBoundary = waitMs < 30000 ? 30000 : waitMs * 2;
                return;
            }
            var incident = node.Value;
            incident.LastSeenTimestamp = timestamp;
            incident.Start = acceptedAtUtc < incident.Start ? acceptedAtUtc : incident.Start;
            incident.End = startedAtUtc > incident.End ? startedAtUtc : incident.End;
            _recency.Remove(node);
            _recency.AddLast(node);
            Add(incident, acceptedAtUtc, startedAtUtc, waitMs, blocker);
            if (waitMs >= incident.SevereBoundary)
            {
                Close(incident, nowUtc, "severe");
                incident.SevereBoundary = waitMs * 2;
            }
        }
    }

    /// <summary>Closes due windows or all pending state, then delivers bounded reports outside the lock.</summary>
    /// <param name="nowUtc">Current UTC maintenance clock.</param>
    /// <param name="emit">Best-effort local logger callback; exceptions are isolated per report.</param>
    /// <param name="shutdown">True closes every pending window and clears all lane state.</param>
    /// <param name="nowTimestampMs">Optional monotonic milliseconds for tests; production uses Environment.TickCount64.</param>
    /// <remarks>Only the scheduler coordinator calls this method. A failing sink is not retried and cannot fault execution.</remarks>
    /// <example><code>incidents.Flush(DateTime.UtcNow, summary => logger.LogWarning("{Summary}", summary));</code></example>
    internal void Flush(DateTime nowUtc, Action<TelegramQueueDelayIncidentSummary> emit, bool shutdown = false, long? nowTimestampMs = null)
    {
        var timestamp = nowTimestampMs ?? Environment.TickCount64;
        lock (_gate)
        {
            var node = _recency.First;
            while (node != null)
            {
                var next = node.Next;
                var incident = node.Value;
                var expired = timestamp - incident.LastSeenTimestamp >= Window.TotalMilliseconds;
                if (shutdown || expired || timestamp - incident.WindowTimestamp >= Window.TotalMilliseconds)
                {
                    Close(incident, nowUtc, shutdown ? "shutdown" : expired ? "expired" : "periodic");
                    incident.WindowTimestamp = timestamp;
                    incident.WindowStart = nowUtc;
                }
                if (shutdown || expired)
                {
                    _lanes.Remove(incident.Key);
                    _recency.Remove(node);
                }
                node = next;
            }
        }
        // Bounded draining also prevents observations arriving concurrently from extending maintenance indefinitely.
        for (var i = 0; i <= _capacity; i++)
        {
            TelegramQueueDelayIncidentSummary report;
            lock (_gate)
            {
                if (i < _capacity && _reports.Count > 0) report = _reports.Dequeue();
                else { report = _overflow; _overflow = null; }
            }
            if (report == null) break;
            try { emit(report); } catch { /* Diagnostic sinks never participate in business success. */ }
        }
    }

    /// <summary>Accumulates one victim into the current unreported batch.</summary>
    /// <param name="incident">Retained lane incident under the instance lock.</param>
    /// <param name="accepted">UTC victim acceptance.</param>
    /// <param name="started">UTC victim actual start.</param>
    /// <param name="wait">Queue wait in milliseconds.</param>
    /// <param name="blocker">Nullable predecessor metadata.</param>
    /// <remarks>Called only with the instance lock held; batches are cleared when their report is queued, not when delivered.</remarks>
    /// <example><code>Add(incident, acceptedAtUtc, startedAtUtc, queueWaitMs, blocker); // Under _gate.</code></example>
    private static void Add(Incident incident, DateTime accepted, DateTime started, double wait, TelegramLaneExecutionSummary blocker)
    {
        incident.Pending++;
        incident.MaxWait = Math.Max(incident.MaxWait, wait);
        incident.PendingStart = accepted < incident.PendingStart ? accepted : incident.PendingStart;
        incident.PendingEnd = started > incident.PendingEnd ? started : incident.PendingEnd;
        incident.Blocker = Dominant(incident.Blocker, blocker);
    }

    /// <summary>Selects greatest overlap, breaking ties by lower inbox sequence.</summary>
    /// <param name="left">Nullable current maximum.</param>
    /// <param name="right">Nullable candidate maximum.</param>
    /// <returns>The dominant metadata, or null if neither exists.</returns>
    /// <remarks>Selection is independent of predecessor identity, so attribution changes cannot split incidents.</remarks>
    /// <example><code>var blocker = Dominant(batch.Blocker, victimBlocker);</code></example>
    private static TelegramLaneExecutionSummary Dominant(TelegramLaneExecutionSummary left, TelegramLaneExecutionSummary right)
        => right != null && (left == null || right.BlockingOverlapMs > left.BlockingOverlapMs ||
            (right.BlockingOverlapMs == left.BlockingOverlapMs && right.Sequence < left.Sequence)) ? right : left;

    /// <summary>Queues and clears an unreported batch before expiry, eviction, or reporting-window closure.</summary>
    /// <param name="incident">Retained lane state, protected by the instance lock.</param>
    /// <param name="now">UTC close time.</param>
    /// <param name="reason">Closed-vocabulary lifecycle reason.</param>
    /// <remarks>Called under the instance lock. Queue saturation preserves pending counts/extrema in the explicit mixed-lane overflow bucket.</remarks>
    /// <example><code>Close(incident, DateTime.UtcNow, "expired"); // Under _gate.</code></example>
    private void Close(Incident incident, DateTime now, string reason)
    {
        if (incident.Pending == 0) return;
        var report = new TelegramQueueDelayIncidentSummary(incident.Key.BotId, incident.Key.TelegramUserId,
            incident.Pending, incident.MaxWait, incident.Blocker, incident.PendingStart, incident.PendingEnd,
            incident.WindowStart, now, reason);
        if (_reports.Count < _capacity) _reports.Enqueue(report);
        else if (_overflow == null) _overflow = report with { BotId = "multiple", TelegramUserId = 0, Reason = "overflow", Overflow = true };
        else _overflow = _overflow with
        {
            AffectedCount = _overflow.AffectedCount + report.AffectedCount,
            MaxQueueWaitMs = Math.Max(_overflow.MaxQueueWaitMs, report.MaxQueueWaitMs),
            DominantBlocker = Dominant(_overflow.DominantBlocker, report.DominantBlocker),
            StartedAtUtc = report.StartedAtUtc < _overflow.StartedAtUtc ? report.StartedAtUtc : _overflow.StartedAtUtc,
            EndedAtUtc = report.EndedAtUtc > _overflow.EndedAtUtc ? report.EndedAtUtc : _overflow.EndedAtUtc,
            WindowStartedAtUtc = report.WindowStartedAtUtc < _overflow.WindowStartedAtUtc ? report.WindowStartedAtUtc : _overflow.WindowStartedAtUtc,
            WindowEndedAtUtc = now
        };
        incident.Pending = 0;
        incident.MaxWait = 0;
        incident.Blocker = null;
        incident.PendingStart = DateTime.MaxValue;
        incident.PendingEnd = DateTime.MinValue;
    }

    /// <summary>One connected lane incident with a fixed deadline and an unreported observation batch.</summary>
    private sealed class Incident
    {
        /// <summary>Exact tenant/runtime bot and actor lane identity.</summary>
        internal readonly TelegramUpdateExecutionKey Key;
        /// <summary>Connected incident interval bounds and UTC reporting-window origin.</summary>
        internal DateTime Start, End, WindowStart;
        /// <summary>Monotonic observation and reporting-window starts, in milliseconds.</summary>
        internal long LastSeenTimestamp, WindowTimestamp;
        /// <summary>Bounds of observations not yet included in any queued report.</summary>
        internal DateTime PendingStart = DateTime.MaxValue, PendingEnd = DateTime.MinValue;
        /// <summary>Number of delayed updates not yet included in a queued summary.</summary>
        internal long Pending;
        /// <summary>Maximum queue-wait milliseconds in the unreported batch.</summary>
        internal double MaxWait;
        /// <summary>Next immediately visible escalation in queue-wait milliseconds.</summary>
        internal double SevereBoundary;
        /// <summary>Greatest positive victim-specific overlap in the unreported batch.</summary>
        internal TelegramLaneExecutionSummary Blocker;

        /// <summary>Initializes one incident without external effects.</summary>
        /// <param name="key">Exact runtime bot/Telegram actor identity.</param>
        /// <param name="start">Initial UTC accepted time.</param>
        /// <param name="end">Initial UTC actual start.</param>
        /// <param name="now">UTC observation clock.</param>
        /// <param name="timestamp">Monotonic observation time in milliseconds.</param>
        internal Incident(TelegramUpdateExecutionKey key, DateTime start, DateTime end, DateTime now, long timestamp)
        { Key = key; Start = start; End = end; LastSeenTimestamp = timestamp; WindowStart = now; WindowTimestamp = timestamp; }
    }
}
