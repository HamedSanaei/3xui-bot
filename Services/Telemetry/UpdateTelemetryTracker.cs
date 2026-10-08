using System.Diagnostics;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Adminbot.Services.Telemetry;

/// <summary>Connects receiver, durable admission and execution timing without storing telemetry in SQLite.</summary>
/// <remarks>
/// Only payload-free identities and clocks are retained, bounded by durable inbox capacity. FIFO and persistence
/// remain owned by the inbox. A deterministic bot/update trace survives restart; monotonic clocks do not.
/// </remarks>
public sealed class UpdateTelemetryTracker
{
    /// <summary>Receiver-local metadata; asynchronous receiver admission inherits this value, not another bot's.</summary>
    private static readonly AsyncLocal<UpdateTelemetryTimeline> Ambient = new();
    /// <summary>Serializes bounded metadata publication before the inbox wakes the scheduler.</summary>
    private readonly Lock _gate = new();
    /// <summary>Committed, not-yet-claimed timelines; contains no lane, chat, sender or update payload.</summary>
    private readonly Dictionary<long, UpdateTelemetryTimeline> _waiting = new();
    /// <summary>Bounded in-flight receiver origins, allowing a periodic scan to claim before post-commit publication.</summary>
    private readonly Dictionary<(string BotId, long UpdateId), UpdateTelemetryTimeline> _receiving = new();
    /// <summary>Bounded live execution metadata for exact-once shutdown recovery summaries; never contains work-item payloads.</summary>
    private readonly Dictionary<long, UpdateTelemetryTimeline> _executing = new();
    private readonly LatencyTelemetryService _telemetry;
    private readonly int _capacity;

    /// <summary>Creates a process-local tracker beside the bounded durable scheduler.</summary>
    /// <param name="telemetry">Required nonblocking JSONL event sink; disabled collection makes receiver tracking a no-op.</param>
    /// <param name="capacity">Positive durable inbox capacity in update counts, from validated application settings.</param>
    /// <remarks>No database or file is opened; metadata overflow is reported rather than blocking admission.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive.</exception>
    public UpdateTelemetryTracker(LatencyTelemetryService telemetry, int capacity)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _telemetry = telemetry;
        _capacity = capacity;
    }

    /// <summary>Gets the payload-free receiver timeline active in this async flow, or null outside reception.</summary>
    public static UpdateTelemetryTimeline Current => Ambient.Value;

    /// <summary>Marks the application's first directly observable reception point.</summary>
    /// <param name="botId">Required configured internal bot id, never a token, Telegram user id or chat id.</param>
    /// <param name="updateId">Telegram update id supplied by the SDK receiver.</param>
    /// <param name="updateType">SDK enum name only, not message text or callback data.</param>
    /// <returns>A disposable receiver metadata scope, or null when collection is disabled.</returns>
    /// <remarks>The receiver disposes this after admission; committed metadata remains until claim. It cannot measure user-action-to-Telegram time.</remarks>
    /// <example><code>using var received = tracker.Receive(bot.Id, update.Id, update.Type.ToString());</code></example>
    public UpdateTelemetryTimeline Receive(string botId, long updateId, string updateType)
    {
        if (!_telemetry.Enabled) return null;
        var timeline = new UpdateTelemetryTimeline(this, _telemetry, botId, updateId, updateType, Ambient.Value);
        lock (_gate)
        {
            if (_receiving.Count < _capacity)
                _receiving.TryAdd((botId, updateId), timeline);
            else
                timeline.Emit("telegram_timeline_metadata_lost", "capacity_exceeded");
        }
        Ambient.Value = timeline;
        timeline.Emit("telegram_update_received");
        return timeline;
    }

    /// <summary>Publishes post-commit timing before the inbox emits its readiness wake.</summary>
    /// <param name="sequence">Internal SQLite inbox sequence allocated by the existing successful insertion.</param>
    /// <param name="acceptedAtUtc">Existing UTC receipt timestamp assigned before SaveChanges, not proof of commit completion.</param>
    /// <remarks>Only the current receiver is attached. Direct durable inserts without a receiver later receive explicitly incomplete timing.</remarks>
    public void Persisted(long sequence, DateTime acceptedAtUtc)
    {
        var timeline = Ambient.Value;
        if (timeline == null) return;
        timeline.MarkPersisted(sequence, acceptedAtUtc);
        lock (_gate)
        {
            if (timeline.IsClaimed) return;
            if (_waiting.Count < _capacity || _waiting.ContainsKey(sequence))
                _waiting[sequence] = timeline;
            else
                timeline.Emit("telegram_timeline_metadata_lost", "capacity_exceeded");
        }
    }

    /// <summary>Transfers an accepted timeline to one claimed execution without changing inbox ownership.</summary>
    /// <param name="item">Required detached claimed work item; only its coarse identity and stored timestamps are read.</param>
    /// <param name="claimStarted">Monotonic Stopwatch timestamp immediately before ClaimAsync.</param>
    /// <param name="readyObserved">Monotonic timestamp after the scheduler's ready query, not when the lane first became eligible.</param>
    /// <returns>Execution metadata, or null when collection is disabled.</returns>
    /// <remarks>Missing process-local timing is marked explicitly; recovered queue time is a UTC estimate from persisted acceptance.</remarks>
    /// <example><code>var timeline = tracker.Claimed(item, claimStarted, readyObserved);</code></example>
    public UpdateTelemetryTimeline Claimed(TelegramUpdateWorkItem item, long claimStarted, long readyObserved)
    {
        if (!_telemetry.Enabled) return null;
        lock (_gate)
        {
            _waiting.Remove(item.Sequence, out var timeline);
            if (timeline == null) _receiving.TryGetValue((item.Key.BotId, item.Update.Id), out timeline);
            timeline ??= new UpdateTelemetryTimeline(this, _telemetry, item.Key.BotId, item.Update.Id,
                item.Update.Type.ToString(), null, recovered: true);
            // Claim ownership and publication use the same short metadata lock, never the database transaction.
            timeline.MarkClaimed(item, claimStarted, readyObserved);
            if (_executing.Count < _capacity) _executing[item.Sequence] = timeline;
            else timeline.Emit("telegram_timeline_metadata_lost", "capacity_exceeded");
            return timeline;
        }
    }

    /// <summary>Emits a metadata-only terminal summary for a claim interrupted by process exit.</summary>
    /// <param name="sequence">Existing internal durable receipt sequence.</param>
    /// <param name="botId">Existing canonical runtime bot id, not a token.</param>
    /// <param name="updateId">Existing Telegram update id.</param>
    /// <param name="updateType">Existing SDK enum category only.</param>
    /// <param name="acceptedAtUtc">Existing persisted pre-commit acceptance timestamp.</param>
    /// <param name="startedAtUtc">Existing UTC claim timestamp, or null for legacy uncertain rows.</param>
    /// <param name="completedAtUtc">UTC terminal recovery timestamp assigned by the unchanged recovery operation.</param>
    /// <param name="preRestart">True for startup's previous-process receipts; false reconciles only known live shutdown executions.</param>
    /// <remarks>Previous-process handler clocks remain null. Live recovery shares the exact-once terminal guard with normal completion and never replays an external effect.</remarks>
    public void RecordRecovered(long sequence, string botId, long updateId, string updateType,
        DateTime acceptedAtUtc, DateTime? startedAtUtc, DateTime completedAtUtc, bool preRestart = true)
    {
        if (!_telemetry.Enabled) return;
        UpdateTelemetryTimeline live;
        lock (_gate) _executing.TryGetValue(sequence, out live);
        if (live != null)
        {
            live.Interrupted(completedAtUtc);
            return;
        }
        if (!preRestart) return;
        _telemetry.TryRecord(new LatencyTelemetryEvent
        {
            EventType = "telegram_update_completed", BotId = botId, UpdateId = updateId, Sequence = sequence,
            TraceId = TraceIdentity(botId, updateId), UpdateType = updateType, Recovered = true,
            TimingQuality = "process_interrupted_pre_restart_timing_unavailable", Outcome = "process_interrupted",
            PersistenceOutcome = "completed", InboxAcceptedAtUtc = DateTime.SpecifyKind(acceptedAtUtc, DateTimeKind.Utc),
            ClaimedAtUtc = startedAtUtc.HasValue ? DateTime.SpecifyKind(startedAtUtc.Value, DateTimeKind.Utc) : null,
            InboxCompletedAtUtc = DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc)
        });
    }

    /// <summary>Releases only this receiver's bounded metadata after admission/control handling leaves its async flow.</summary>
    /// <param name="timeline">Exact receiver owner, not another duplicate delivery.</param>
    /// <remarks>Committed waiting metadata remains until claim; duplicate disposal cannot remove the original receiver.</remarks>
    private void ForgetReceiver(UpdateTelemetryTimeline timeline)
    {
        lock (_gate)
        {
            var key = (timeline.BotId, timeline.UpdateId.GetValueOrDefault());
            if (_receiving.TryGetValue(key, out var current) && ReferenceEquals(current, timeline)) _receiving.Remove(key);
        }
    }

    /// <summary>Releases metadata after actual live execution unwinding, including a summary already emitted by shutdown recovery.</summary>
    /// <param name="timeline">Exact payload-free execution owner.</param>
    /// <remarks>No receipt or business state is changed.</remarks>
    private void ForgetExecution(UpdateTelemetryTimeline timeline)
    {
        lock (_gate)
        {
            if (timeline.Sequence is long sequence && _executing.TryGetValue(sequence, out var current)
                && ReferenceEquals(current, timeline)) _executing.Remove(sequence);
        }
    }

    /// <summary>Derives a restart-stable correlation id from the exact durable deduplication identity.</summary>
    /// <param name="botId">Required canonical runtime bot id, not a token or user-controlled label.</param>
    /// <param name="updateId">Telegram update id used by BotId + UpdateId durable deduplication.</param>
    /// <returns>A lowercase 128-bit hexadecimal trace id containing no raw bot id.</returns>
    /// <remarks>This identifies deduplicated delivery, not a user's physical action. Sequence additionally disambiguates
    /// historical receipts. Canonical runtime ids use stack storage; no intermediate id or UTF-8 array is allocated.</remarks>
    /// <example><code>var traceId = UpdateTelemetryTracker.TraceIdentity(bot.Id, update.Id);</code></example>
    public static string TraceIdentity(string botId, long updateId)
    {
        var maximumBytes = Encoding.UTF8.GetMaxByteCount(botId?.Length ?? 0) + 21;
        Span<byte> identity = maximumBytes <= 512 ? stackalloc byte[maximumBytes] : new byte[maximumBytes];
        var length = Encoding.UTF8.GetBytes(botId.AsSpan(), identity);
        identity[length++] = (byte)'\n';
        Utf8Formatter.TryFormat(updateId, identity[length..], out var idBytes);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity[..(length + idBytes)], hash);
        return Convert.ToHexStringLower(hash[..16]);
    }

    /// <summary>Holds one payload-free update's milestones from reception through final receipt persistence.</summary>
    /// <remarks>Receiver and scheduler briefly overlap after commit. A small metadata lock protects their shared clocks; no I/O occurs under it.</remarks>
    public sealed class UpdateTelemetryTimeline : IDisposable
    {
        private readonly UpdateTelemetryTracker _owner;
        private readonly LatencyTelemetryService _telemetry;
        private readonly UpdateTelemetryTimeline _previous;
        private readonly Lock _gate = new();
        private readonly long _receivedTick;
        private long _admissionTick;
        private long _attemptTick;
        private long _persistedTick;
        private long _handlerTick;
        private long _handlerEndTick;
        private long _claimEndTick;
        private double _persistenceAttemptsMs;
        private int _completed;
        private int _disposed;
        private int _claimed;
        private LatencyTelemetryEvent _state;

        /// <summary>Initializes a new receiver or reconstructed execution's metadata.</summary>
        /// <param name="owner">Bounded receiver/execution metadata owner, used only for deterministic release.</param>
        /// <param name="telemetry">Nonblocking event writer.</param>
        /// <param name="botId">Canonical runtime bot id.</param>
        /// <param name="updateId">Telegram update identifier.</param>
        /// <param name="updateType">SDK enum name; no payload.</param>
        /// <param name="previous">Enclosing receiver metadata restored on disposal, normally null.</param>
        /// <param name="recovered">True when no receiver monotonic timeline survived until claim.</param>
        /// <remarks>Reconstruction never invents receiver or persistence timestamps.</remarks>
        internal UpdateTelemetryTimeline(UpdateTelemetryTracker owner, LatencyTelemetryService telemetry,
            string botId, long updateId, string updateType, UpdateTelemetryTimeline previous, bool recovered = false)
        {
            _owner = owner;
            _telemetry = telemetry;
            _previous = previous;
            _receivedTick = Stopwatch.GetTimestamp();
            _state = new LatencyTelemetryEvent
            {
                BotId = botId, UpdateId = updateId, UpdateType = updateType,
                TraceId = TraceIdentity(botId, updateId), Recovered = recovered,
                ReceivedAtUtc = recovered ? null : DateTime.UtcNow,
                TimingQuality = recovered ? "pre_claim_timing_unavailable" : "monotonic"
            };
        }

        /// <summary>Gets the stable payload-free correlation id used by HTTP, stage and lifecycle observations.</summary>
        public string TraceId => _state.TraceId;
        /// <summary>Gets the internal bot id for receiver HTTP correlation before a handler scope exists.</summary>
        public string BotId => _state.BotId;
        /// <summary>Gets Telegram's update id, never a message or chat id.</summary>
        public long? UpdateId => _state.UpdateId;
        /// <summary>Gets the durable sequence once committed, or null before admission.</summary>
        public long? Sequence => _state.Sequence;
        /// <summary>Whether claim ownership has been transferred, preventing late publication from retaining completed metadata.</summary>
        internal bool IsClaimed => Volatile.Read(ref _claimed) != 0;

        /// <summary>Marks admission entry independently of receiver-side admin routing.</summary>
        /// <remarks>Measured after receiver control-path checks; capacity backpressure belongs to admission, not queue waiting.</remarks>
        public void BeginAdmission()
        {
            lock (_gate)
            {
                _admissionTick = Stopwatch.GetTimestamp();
                _state = _state with { AdmissionStartedAtUtc = DateTime.UtcNow,
                    ReceiverToAdmissionMs = Stopwatch.GetElapsedTime(_receivedTick, _admissionTick).TotalMilliseconds };
            }
            Emit("telegram_update_admission_started");
        }

        /// <summary>Starts one durable admission attempt, including SQLite retries but excluding capacity sleeps.</summary>
        /// <remarks>No business write or retry is introduced; the scheduler invokes this before its existing TryAcceptAsync await.</remarks>
        public void BeginAdmissionAttempt() { lock (_gate) _attemptTick = Stopwatch.GetTimestamp(); }

        /// <summary>Accumulates a capacity-refused attempt after its existing database await returns.</summary>
        /// <remarks>Successful insertion records this interval at commit, before the scheduler can claim the row.</remarks>
        public void AdmissionRefused()
        {
            lock (_gate)
            {
                if (_attemptTick != 0) _persistenceAttemptsMs += Stopwatch.GetElapsedTime(_attemptTick).TotalMilliseconds;
                _attemptTick = 0;
            }
        }

        /// <summary>Marks successful durable insertion and scheduler waiting at the same post-commit observation.</summary>
        /// <param name="sequence">Existing internal SQLite receipt sequence.</param>
        /// <param name="acceptedAtUtc">Existing persisted pre-commit acceptance timestamp, preserved exactly.</param>
        /// <remarks>Timing is metadata-only and precedes NotifyReady. An early periodic claim retains receiver origin;
        /// the unavailable post-commit-to-claim queue interval is null rather than a fabricated duration.</remarks>
        internal void MarkPersisted(long sequence, DateTime acceptedAtUtc)
        {
            lock (_gate)
            {
                _persistedTick = Stopwatch.GetTimestamp();
                if (_attemptTick != 0) _persistenceAttemptsMs += Stopwatch.GetElapsedTime(_attemptTick, _persistedTick).TotalMilliseconds;
                _attemptTick = 0;
                var utc = DateTime.UtcNow;
                _state = _state with { Sequence = sequence, InboxAcceptedAtUtc = AsUtc(acceptedAtUtc), PersistedAtUtc = utc,
                    SchedulerWaitingAtUtc = IsClaimed ? null : utc, AdmissionMs = _admissionTick == 0 ? null : Stopwatch.GetElapsedTime(_admissionTick, _persistedTick).TotalMilliseconds,
                    AdmissionPersistenceMs = _persistenceAttemptsMs };
            }
            Emit("telegram_update_persisted");
            if (!IsClaimed) Emit("telegram_update_waiting");
        }

        /// <summary>Records a deduplicated receiver delivery without inventing a second handler execution.</summary>
        /// <remarks>The existing receipt remains authoritative; this summary is excluded from executed-update percentiles.</remarks>
        public void CompleteDuplicate() => CompleteWithoutInbox("duplicate", "deduplicated_delivery");

        /// <summary>Records receiver control commands that intentionally bypass durable customer admission.</summary>
        /// <param name="snapshot">Optional frozen scope around the actual receiver admin handler, never its input or output.</param>
        /// <remarks>These commands remain reachable under capacity pressure. Inbox/admission measurements remain null;
        /// their actual handler, callback acknowledgment and visible-response measurements are preserved when available.</remarks>
        public void CompleteControlPath(LatencyTelemetryEvent snapshot = null)
        {
            if (snapshot != null)
            {
                HandlerCompleted(snapshot);
                lock (_gate) _state = _state with { HandlerMs = snapshot.HandlerMs,
                    HandlerStartedAtUtc = snapshot.HandlerStartedAtUtc, HandlerCompletedAtUtc = snapshot.HandlerCompletedAtUtc };
            }
            CompleteWithoutInbox("receiver_control_path", snapshot == null ? "receiver_control_path_no_handler_scope" : "receiver_control_path_no_inbox");
        }

        /// <summary>Emits one terminal summary for delivery paths with no scheduled handler.</summary>
        /// <param name="outcome">Closed internal result classification.</param>
        /// <param name="quality">Closed explanation of unavailable scheduled timing.</param>
        /// <remarks>No inbox row, handler, or notification is created.</remarks>
        private void CompleteWithoutInbox(string outcome, string quality)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            lock (_gate) _state = _state with { Outcome = outcome, TimingQuality = quality,
                ApplicationMs = Stopwatch.GetElapsedTime(_receivedTick).TotalMilliseconds };
            Emit("telegram_update_completed");
        }

        /// <summary>Marks the observed completion of an unchanged durable claim.</summary>
        /// <param name="item">Claimed detached work item; its private update is never retained in metadata.</param>
        /// <param name="claimStarted">Stopwatch timestamp before ClaimAsync.</param>
        /// <param name="readyObserved">Stopwatch timestamp after ready-query completion.</param>
        /// <remarks>Queue timing excludes claim duration in-process; recovered UTC queue estimates end at the existing stored claim time.</remarks>
        internal void MarkClaimed(TelegramUpdateWorkItem item, long claimStarted, long readyObserved)
        {
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                _claimEndTick = now;
                Volatile.Write(ref _claimed, 1);
                _state = _state with { Sequence = item.Sequence, InboxAcceptedAtUtc = AsUtc(item.AcceptedAtUtc), ClaimedAtUtc = DateTime.UtcNow,
                    ClaimMs = Stopwatch.GetElapsedTime(claimStarted, now).TotalMilliseconds,
                    DispatchDelayMs = readyObserved == 0 ? null : Stopwatch.GetElapsedTime(readyObserved, claimStarted).TotalMilliseconds,
                    TimingQuality = _persistedTick == 0 && _state.Recovered != true ? "commit_observation_after_claim" : _state.TimingQuality,
                    QueueWaitMs = _persistedTick == 0
                        ? _state.Recovered == true ? Math.Max(0, (item.StartedAtUtc - item.AcceptedAtUtc).TotalMilliseconds) : null
                        : claimStarted < _persistedTick ? null : Stopwatch.GetElapsedTime(_persistedTick, claimStarted).TotalMilliseconds };
            }
            Emit("telegram_update_claimed");
        }

        /// <summary>Marks the actual handler invocation after optional queue diagnostics.</summary>
        /// <param name="scopeStartedAtUtc">Optional UTC anchor from the authoritative latency scope; null uses the current UTC instant.</param>
        /// <remarks>The scope owns stage clocks; no claim or queue time is included in handler duration.</remarks>
        public void HandlerStarted(DateTime? scopeStartedAtUtc = null)
        {
            lock (_gate)
            {
                _handlerTick = Stopwatch.GetTimestamp();
                _state = _state with { HandlerStartedAtUtc = scopeStartedAtUtc ?? DateTime.UtcNow,
                    ClaimedToHandlerMs = _claimEndTick == 0 ? null : Stopwatch.GetElapsedTime(_claimEndTick, _handlerTick).TotalMilliseconds };
            }
            Emit("telegram_update_handler_started");
        }

        /// <summary>Combines measured stage and response metadata with a precise handler endpoint.</summary>
        /// <param name="snapshot">Optional frozen latency-scope snapshot, never a payload.</param>
        /// <param name="completedTimestamp">Optional Stopwatch endpoint captured immediately after the executor await, before diagnostic snapshot construction.</param>
        /// <remarks>This runs when ExecuteAsync exits, including exceptions; post-handler business-review reads are measured separately.</remarks>
        public void HandlerCompleted(LatencyTelemetryEvent snapshot, long? completedTimestamp = null)
        {
            lock (_gate)
            {
                _handlerEndTick = completedTimestamp ?? Stopwatch.GetTimestamp();
                _state = _state with { HandlerStartedAtUtc = snapshot?.HandlerStartedAtUtc ?? _state.HandlerStartedAtUtc,
                    HandlerCompletedAtUtc = snapshot?.HandlerCompletedAtUtc ?? DateTime.UtcNow,
                    HandlerMs = snapshot?.HandlerMs ?? (_handlerTick == 0 ? null : Stopwatch.GetElapsedTime(_handlerTick, _handlerEndTick).TotalMilliseconds),
                    TimingQuality = snapshot?.TimingQuality == "metadata_capacity_exceeded" ? "metadata_capacity_exceeded" : _state.TimingQuality,
                    StageMs = snapshot?.StageMs, InclusiveStageMs = snapshot?.InclusiveStageMs,
                    UnattributedHandlerMs = snapshot?.UnattributedHandlerMs, MaxUnattributedGapMs = snapshot?.MaxUnattributedGapMs,
                    MaxGapStartedMs = snapshot?.MaxGapStartedMs, MaxGapEndedMs = snapshot?.MaxGapEndedMs,
                    GapBeforeStage = snapshot?.GapBeforeStage, GapAfterStage = snapshot?.GapAfterStage, SlowestStage = snapshot?.SlowestStage,
                    FirstResponseAttemptMs = snapshot?.FirstResponseAttemptMs, FirstResponseCompletedMs = snapshot?.FirstResponseCompletedMs,
                    FirstResponseAcknowledgedMs = snapshot?.FirstResponseAcknowledgedMs,
                    FirstResponseAttemptAtUtc = snapshot?.FirstResponseAttemptAtUtc, FirstResponseCompletedAtUtc = snapshot?.FirstResponseCompletedAtUtc,
                    CallbackAckMs = snapshot?.CallbackAckMs, CallbackAckAttemptMs = snapshot?.CallbackAckAttemptMs,
                    CallbackAckAcknowledgedMs = snapshot?.CallbackAckAcknowledgedMs, TotalTelegramMs = snapshot?.TotalTelegramMs,
                    TelegramRequestCount = snapshot?.TelegramRequestCount, ForegroundTimeoutCount = snapshot?.ForegroundTimeoutCount,
                    BusyRetryCount = snapshot?.BusyRetryCount, BusyWaitMs = snapshot?.BusyWaitMs };
            }
            Emit("telegram_update_handler_completed");
        }

        /// <summary>Emits the terminal summary after the independent final receipt-persistence attempt.</summary>
        /// <param name="failure">Existing closed scheduler failure code, or null for a completed handler.</param>
        /// <param name="persistenceStarted">Stopwatch timestamp before the existing FinishAsync await.</param>
        /// <param name="persistenceSucceeded">True when the running receipt transitioned; false for a persistence failure; null when no running claim remained and no transition occurred.</param>
        /// <remarks>No database change is retried by telemetry. One compact summary is emitted even for a fast successful update.</remarks>
        public void Completed(string failure, long persistenceStarted, bool? persistenceSucceeded)
        {
            try
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0) return;
                lock (_gate)
                {
                    var now = Stopwatch.GetTimestamp();
                    _state = _state with { Outcome = failure ?? "completed",
                        PersistenceOutcome = persistenceSucceeded == true ? "completed" : persistenceSucceeded == false ? "failed" : "no_running_claim",
                        InboxCompletedAtUtc = persistenceSucceeded == true ? DateTime.UtcNow : null,
                        FinalPersistenceMs = Stopwatch.GetElapsedTime(persistenceStarted, now).TotalMilliseconds,
                        PostHandlerMs = _handlerEndTick == 0 ? null : Stopwatch.GetElapsedTime(_handlerEndTick, persistenceStarted).TotalMilliseconds,
                        ApplicationMs = _state.Recovered == true ? null : Stopwatch.GetElapsedTime(_receivedTick, now).TotalMilliseconds };
                }
                Emit("telegram_update_inbox_completed");
                Emit("telegram_update_completed");
            }
            finally { _owner.ForgetExecution(this); }
        }

        /// <summary>Emits one durable terminal summary when shutdown recovery wins a race with live handler unwinding.</summary>
        /// <param name="assignedAtUtc">Existing recovery's assigned UTC terminal timestamp; not an invented handler endpoint.</param>
        /// <remarks>The still-running handler's total duration, final-persistence attempt and stage snapshot remain null.
        /// Later real handler completion can emit detail but cannot emit another terminal summary or replay delivery.</remarks>
        internal void Interrupted(DateTime assignedAtUtc)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            lock (_gate)
                _state = _state with { Outcome = "process_interrupted", PersistenceOutcome = "recovered_during_shutdown",
                    TimingQuality = "live_handler_interrupted_timing_incomplete", InboxCompletedAtUtc = AsUtc(assignedAtUtc),
                    ApplicationMs = _state.Recovered == true ? null : Stopwatch.GetElapsedTime(_receivedTick).TotalMilliseconds };
            Emit("telegram_update_inbox_completed");
            Emit("telegram_update_completed");
        }

        /// <summary>Attempts a payload-free observation without blocking on writer or channel capacity.</summary>
        /// <param name="eventType">Compile-time event family supplied by internal instrumentation.</param>
        /// <param name="outcome">Optional compile-time outcome override.</param>
        /// <remarks>Only immutable metadata snapshots reach the writer; sink failure cannot change the caller's result.</remarks>
        internal void Emit(string eventType, string outcome = null)
        {
            LatencyTelemetryEvent snapshot;
            lock (_gate) snapshot = _state with { EventType = eventType, TimestampUtc = DateTime.UtcNow, Outcome = outcome ?? _state.Outcome };
            _telemetry.TryRecord(snapshot);
        }

        /// <summary>Marks EF-rehydrated UTC timestamps as UTC without converting their already-UTC tick values.</summary>
        /// <param name="value">UTC database timestamp whose Kind may be unspecified after SQLite materialization.</param>
        /// <returns>The same ticks with UTC Kind, safe for JSON UTC serialization.</returns>
        /// <remarks>No local-time conversion is performed.</remarks>
        private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        /// <summary>Restores receiver context while retaining committed metadata until scheduler claim.</summary>
        /// <remarks>A receiver exiting before commit emits a quality-visible aborted delivery; it never discards durable work.</remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Ambient.Value == this) Ambient.Value = _previous;
            _owner.ForgetReceiver(this);
            if (_state.Sequence == null && Volatile.Read(ref _completed) == 0)
                CompleteWithoutInbox("receiver_aborted", "admission_incomplete");
        }
    }
}
