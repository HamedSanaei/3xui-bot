using System;
using System.Collections.Generic;

namespace Adminbot.Services.Telemetry;

/// <summary>Tracks metadata-only polling health independently for every active owned, tenant and assistant receiver.</summary>
/// <remarks>State exists only between receiver start and stop. SDK-validated empty polls count as healthy; HTTP headers alone never reset an incident. No backoff or runtime decision is changed.</remarks>
public sealed class TelegramPollingTelemetryTracker
{
    /// <summary>Nonblocking telemetry writer; absent writers make tracking a no-op.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Serializes bounded receiver state without holding a lock across network operations.</summary>
    private readonly object _gate = new();
    /// <summary>UTC/monotonic clock; defaults to the system provider and permits deterministic incident timing.</summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>Active receiver state keyed by canonical internal bot id, never tokens or usernames.</summary>
    private readonly Dictionary<string, PollState> _receivers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Distinguishes a replacement receiver from a predecessor's late completion callback.</summary>
    private long _generation;

    /// <summary>Creates polling diagnostics sharing the application's nonblocking writer.</summary>
    /// <param name="telemetry">Optional singleton writer; null disables all tracking and allocation.</param>
    /// <param name="timeProvider">Optional controlled UTC/monotonic clock; null uses TimeProvider.System.</param>
    /// <remarks>No timers, receiver tasks, database operations or retries are created.</remarks>
    public TelegramPollingTelemetryTracker(LatencyTelemetryService telemetry = null, TimeProvider timeProvider = null)
    {
        _telemetry = telemetry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Reports a startup/restart preflight result without retaining an offline bot in memory.</summary>
    /// <param name="botId">Canonical configured internal bot id, shared across all bot families.</param>
    /// <param name="outcome">Compile-time starting, completed or failed label from the runtime.</param>
    /// <param name="exception">Optional actual preflight failure; only typed diagnoses and numeric codes are recorded.</param>
    /// <remarks>Does not mark a receiver running or mistake getMe connectivity for a successful getUpdates poll.</remarks>
    /// <example><code>tracker.Startup(bot.Id, "starting");</code></example>
    public void Startup(string botId, string outcome, Exception exception = null)
    {
        if (!CanRecord) return;
        _telemetry.TryRecord(TelegramTransportDiagnostics.Classify(exception, null, default) with
        { EventType = "telegram_receiver_startup", BotId = botId, Operation = "startup", Outcome = outcome, ReceiverRunning = false });
    }

    /// <summary>Starts one receiver generation and emits its initial health snapshot.</summary>
    /// <param name="botId">Canonical registry id of the receiver, never numeric Telegram/chat/user identity.</param>
    /// <returns>Generation ownership id used by the receiver's terminal callback, or zero when disabled.</returns>
    /// <remarks>Call immediately before ReceiveAsync; a later replacement overwrites only this bot's state.</remarks>
    /// <example><code>var generation = tracker.Started(bot.Id);</code></example>
    public long Started(string botId)
    {
        if (!CanRecord) return 0;
        lock (_gate)
        {
            var state = new PollState { Generation = ++_generation };
            _receivers[botId] = state;
            Record(botId, state, "telegram_receiver_started", "started");
            return state.Generation;
        }
    }

    /// <summary>Records a poll that the SDK fully deserialized and validated, including an empty updates array.</summary>
    /// <param name="botId">Canonical internal bot id owning this SDK client.</param>
    /// <param name="durationMs">Whole SDK getUpdates duration in milliseconds, including normal long-poll wait.</param>
    /// <remarks>Normal long-poll duration is expected idle time, not slow foreground latency. Recovery duration uses a monotonic clock.</remarks>
    /// <example><code>tracker.Success(bot.Id, elapsed.TotalMilliseconds);</code></example>
    public void Success(string botId, double durationMs)
    {
        if (!CanRecord) return;
        lock (_gate)
        {
            if (!_receivers.TryGetValue(botId, out var state)) return;
            state.LastSuccessfulPollAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (state.DegradedSinceUtc.HasValue)
            {
                Record(botId, state, "telegram_poll_recovered", "recovered", durationMs,
                    recoveryMs: _timeProvider.GetElapsedTime(state.DegradedTimestamp).TotalMilliseconds);
            }
            state.ConsecutiveFailures = 0;
            state.DegradedSinceUtc = null;
            state.DegradedTimestamp = 0;
            Record(botId, state, "telegram_poll_completed", "completed", durationMs);
        }
    }

    /// <summary>Records an actual failed SDK poll and opens/extends its bot-scoped degraded window.</summary>
    /// <param name="botId">Canonical internal bot id owning this failed getUpdates request.</param>
    /// <param name="durationMs">Whole SDK getUpdates duration in milliseconds before failure.</param>
    /// <param name="diagnosis">Payload-free typed failure classification computed from the original exception.</param>
    /// <remarks>Caller shutdown is not a degraded poll. API rejection, including 429, remains failed until a validated poll succeeds.</remarks>
    /// <example><code>tracker.Failure(bot.Id, elapsedMs, diagnosis);</code></example>
    internal void Failure(string botId, double durationMs, LatencyTelemetryEvent diagnosis)
    {
        if (!CanRecord || diagnosis.FailureClassification == "caller_cancellation") return;
        lock (_gate)
        {
            if (!_receivers.TryGetValue(botId, out var state)) return;
            if (!state.DegradedSinceUtc.HasValue)
            {
                state.DegradedSinceUtc = _timeProvider.GetUtcNow().UtcDateTime;
                state.DegradedTimestamp = _timeProvider.GetTimestamp();
            }
            if (state.ConsecutiveFailures < int.MaxValue) state.ConsecutiveFailures++;
            Record(botId, state, "telegram_poll_failed", "failed", durationMs, diagnosis: diagnosis);
        }
    }

    /// <summary>Records the arrival of an update without treating it as a substitute for SDK poll validation.</summary>
    /// <param name="botId">Canonical internal bot id passed by its receiver callback.</param>
    /// <remarks>No update body or customer identity is retained; the timestamp is updated only for active receivers.</remarks>
    /// <example><code>tracker.ReceivedUpdate(bot.Id);</code></example>
    public void ReceivedUpdate(string botId)
    {
        if (!CanRecord) return;
        lock (_gate)
        {
            if (!_receivers.TryGetValue(botId, out var state)) return;
            state.LastReceivedUpdateAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            Record(botId, state, "telegram_receiver_health", "update_received");
        }
    }

    /// <summary>Reports the exact delay already selected by the existing receiver error/recovery policy.</summary>
    /// <param name="botId">Internal bot id receiving the existing delay.</param>
    /// <param name="delay">Existing delay in TimeSpan units; this method never waits or changes it.</param>
    /// <param name="operation">Compile-time rate_limit, transient_gateway, startup_recovery or webhook_recovery label.</param>
    /// <remarks>Unregistered startup receivers emit a stateless snapshot, avoiding historical offline-bot accumulation.</remarks>
    /// <example><code>tracker.Backoff(bot.Id, decision.Delay, "transient_gateway");</code></example>
    public void Backoff(string botId, TimeSpan delay, string operation)
    {
        if (!CanRecord) return;
        lock (_gate)
        {
            _receivers.TryGetValue(botId, out var state);
            if (state != null)
                Record(botId, state, "telegram_poll_backoff", "backoff", backoffMs: delay.TotalMilliseconds, operation: operation);
            else
                _telemetry.TryRecord(new LatencyTelemetryEvent
                { EventType = "telegram_poll_backoff", BotId = botId, Outcome = "backoff", Operation = operation, BackoffMs = delay.TotalMilliseconds, ReceiverRunning = false });
        }
    }

    /// <summary>Emits the final receiver snapshot and removes its entire health history from memory.</summary>
    /// <param name="botId">Canonical internal bot id being stopped or invalidated.</param>
    /// <param name="generation">Optional receiver generation; zero stops the current generation unconditionally.</param>
    /// <remarks>A predecessor's terminal callback cannot erase a newly started receiver. Safe to call repeatedly.</remarks>
    /// <example><code>tracker.Stopped(bot.Id, generation);</code></example>
    public void Stopped(string botId, long generation = 0)
    {
        if (_telemetry?.Enabled != true) return;
        lock (_gate)
        {
            if (!_receivers.TryGetValue(botId, out var state) || (generation != 0 && state.Generation != generation)) return;
            if (CanRecord) Record(botId, state, "telegram_receiver_stopped", "stopped", running: false);
            _receivers.Remove(botId);
        }
    }

    /// <summary>Gets whether recording is enabled and not suppressed for operator telemetry delivery.</summary>
    private bool CanRecord => _telemetry?.Enabled == true && !LatencyTelemetrySuppression.IsActive;

    /// <summary>Builds one compact health snapshot; unavailable polling timestamps remain null.</summary>
    /// <param name="botId">Canonical internal bot id.</param>
    /// <param name="state">Active receiver metadata guarded by the short tracker lock.</param>
    /// <param name="eventType">Compile-time polling/receiver record family.</param>
    /// <param name="outcome">Compile-time completion/transition label.</param>
    /// <param name="durationMs">Optional observed SDK poll duration in milliseconds.</param>
    /// <param name="recoveryMs">Optional monotonic degraded-window duration in milliseconds.</param>
    /// <param name="backoffMs">Optional policy-selected delay in milliseconds.</param>
    /// <param name="operation">Optional compile-time delay category.</param>
    /// <param name="diagnosis">Optional safe typed exception metadata.</param>
    /// <param name="running">Whether this snapshot precedes or represents a stopped receiver.</param>
    /// <remarks>Only the nonblocking channel writer is called while locked; no I/O or Telegram sends occur.</remarks>
    private void Record(string botId, PollState state, string eventType, string outcome, double? durationMs = null,
        double? recoveryMs = null, double? backoffMs = null, string operation = null, LatencyTelemetryEvent diagnosis = null, bool running = true)
        => _telemetry.TryRecord((diagnosis ?? new LatencyTelemetryEvent()) with
        {
            EventType = eventType, BotId = botId, Method = "getUpdates", Category = "polling", Stage = "telegram_polling",
            Outcome = outcome, DurationMs = durationMs, LastSuccessfulPollAtUtc = state.LastSuccessfulPollAtUtc,
            LastReceivedUpdateAtUtc = state.LastReceivedUpdateAtUtc, DegradedSinceUtc = state.DegradedSinceUtc,
            ConsecutiveFailures = state.ConsecutiveFailures, RecoveryMs = recoveryMs, BackoffMs = backoffMs,
            ReceiverRunning = running, Operation = operation
        });

    /// <summary>Fixed-size health metadata for a single currently active receiver generation.</summary>
    private sealed class PollState
    {
        /// <summary>Ownership id preventing late stop callbacks from affecting a replacement.</summary>
        internal long Generation;
        /// <summary>UTC time of the last fully validated poll, including empty responses.</summary>
        internal DateTime? LastSuccessfulPollAtUtc;
        /// <summary>UTC time of the last receiver update callback.</summary>
        internal DateTime? LastReceivedUpdateAtUtc;
        /// <summary>UTC start of the currently open failure incident, or null while healthy.</summary>
        internal DateTime? DegradedSinceUtc;
        /// <summary>Monotonic start of the currently open failure incident.</summary>
        internal long DegradedTimestamp;
        /// <summary>Failures since the last validated poll, saturating at Int32.MaxValue.</summary>
        internal int ConsecutiveFailures;
    }
}
