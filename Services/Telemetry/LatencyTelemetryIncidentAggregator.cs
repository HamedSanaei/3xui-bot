using System;
using System.Collections.Generic;
using Adminbot.Domain.Logging;

namespace Adminbot.Services.Telemetry;

/// <summary>Writer-only bounded five-minute incident windows with ten-minute operator cooldowns.</summary>
/// <remarks>At most 256 bot windows plus one marked global overflow window are retained; events and traces are never retained. Histogram quantiles are approximate upper bucket bounds. At most eight incident summaries are emitted per health tick. Isolated timeouts, raw requests and successful updates never become operator logs.</remarks>
internal sealed class LatencyTelemetryIncidentAggregator
{
    /// <summary>Maximum individually tracked bot ids.</summary>
    private const int MaximumBots = 256;
    /// <summary>Aggregation window length in monotonic milliseconds.</summary>
    private const long WindowMs = 5 * 60 * 1000;
    /// <summary>Minimum interval between notifications for one bot and incident family.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    /// <summary>Closed numeric histogram upper bounds shared by every bot.</summary>
    private static readonly double[] HistogramBounds = CreateBounds();
    /// <summary>Existing repository limiter governing operator-channel summary admission.</summary>
    private readonly TelegramOperatorNotificationLimiter _limiter = new();
    /// <summary>Bounded canonical bot-to-window state.</summary>
    private readonly Dictionary<string, BotWindow> _bots = new(StringComparer.Ordinal);
    /// <summary>Global combined state for bot ids exceeding individual cardinality limits.</summary>
    private readonly BotWindow _overflow = new(null, true);
    /// <summary>Explicit global background window, including botless notification/recovery SQLite contention.</summary>
    private readonly BotWindow _global = new(null);
    /// <summary>Reusable maximum-eight summary list, consumed before the next tick.</summary>
    private readonly List<LatencyTelemetryEvent> _summaries = new(8);
    /// <summary>Validated slow-update and operation thresholds.</summary>
    private readonly LatencyTelemetryOptions _options;
    /// <summary>Last global cumulative loss counter that contributed to a delivered incident.</summary>
    private long _notifiedLoss;
    /// <summary>Last global cumulative failure counter that contributed to a delivered incident.</summary>
    private long _notifiedFailures;
    /// <summary>Start of sustained severe scheduler queue pressure, or zero.</summary>
    private long _queuePressureSince;
    /// <summary>Start of sustained channel pressure, or zero.</summary>
    private long _channelPressureSince;

    /// <summary>Creates the bounded aggregator without allocating per-update windows.</summary>
    /// <param name="options">Validated startup thresholds used for regression classification.</param>
    public LatencyTelemetryIncidentAggregator(LatencyTelemetryOptions options) => _options = options;

    /// <summary>Folds a validated observation into bounded numeric bot windows.</summary>
    /// <param name="observation">Payload-free event successfully read by the single telemetry writer.</param>
    /// <param name="nowTicks">Current monotonic milliseconds, independent of the event's historical UTC timestamp.</param>
    /// <remarks>Aggregation does not log, send, write, retry or retain the event.</remarks>
    public void Observe(LatencyTelemetryEvent observation, long nowTicks)
    {
        BotWindow window;
        if (string.IsNullOrEmpty(observation.BotId)) window = _global;
        else if (!_bots.TryGetValue(observation.BotId, out window))
        {
            if (_bots.Count >= MaximumBots) window = _overflow;
            else _bots.Add(observation.BotId, window = new BotWindow(observation.BotId));
        }
        window.Advance(nowTicks);
        switch (observation.EventType)
        {
            case "telegram_update_completed":
                if (observation.Outcome != "duplicate" && observation.ApplicationMs is double elapsed && double.IsFinite(elapsed) && elapsed >= 0)
                {
                    var bucket = Array.BinarySearch(HistogramBounds, elapsed);
                    if (bucket < 0) bucket = ~bucket;
                    window.Histogram[Math.Min(bucket, HistogramBounds.Length - 1)]++;
                    window.UpdateCount++;
                }
                if (observation.QueueWaitMs >= 30000) window.SevereQueueCount++;
                break;
            case "telegram_foreground_request_completed":
                if (IsTimeout(observation)) window.ForegroundTimeouts++;
                break;
            case "sqlite_busy_retry":
                window.BusyCount++;
                // BusyWaitMs is cumulative per logical operation; only DurationMs is this retry's measured wait.
                window.BusyWaitMs += observation.DurationMs ?? 0;
                break;
            case "sqlite_operation_completed":
            case "sqlite_transaction_completed":
                if (observation.SqliteErrorCode is 5 or 6) window.BusyCount++;
                break;
            case "telegram_poll_failed":
                if (window.PollDegradedSince == 0) window.PollDegradedSince = nowTicks;
                window.PollFailures = Math.Max(window.PollFailures + 1, observation.ConsecutiveFailures ?? 0);
                break;
            case "telegram_poll_recovered":
            case "telegram_poll_completed":
                if (!window.MixedBots)
                {
                    window.PollDegradedSince = 0;
                    window.PollFailures = 0;
                }
                break;
            case "telegram_receiver_health":
                if (observation.ConsecutiveFailures > 0)
                {
                    if (window.PollDegradedSince == 0) window.PollDegradedSince = nowTicks;
                    window.PollFailures = Math.Max(window.PollFailures, observation.ConsecutiveFailures.Value);
                }
                else if (observation.ConsecutiveFailures == 0 && !window.MixedBots)
                {
                    window.PollDegradedSince = 0;
                    window.PollFailures = 0;
                }
                break;
        }
    }

    /// <summary>Returns at most eight newly admitted incident summaries for this health sample.</summary>
    /// <param name="health">Global cumulative counters and latest scheduler/channel health.</param>
    /// <param name="nowTicks">Current monotonic milliseconds for windows and cooldowns.</param>
    /// <returns>Reusable bounded list valid until the next call; the writer persists and routes these summaries only.</returns>
    /// <remarks>Suppressed observations remain in their finite five-minute window; later cooldown summaries describe the current window, not expired events. A first observed loss or writer failure is significant and immediately eligible; cumulative gauges must never be summed by reports.</remarks>
    public IReadOnlyList<LatencyTelemetryEvent> Evaluate(LatencyTelemetryEvent health, long nowTicks)
    {
        _summaries.Clear();
        var loss = health.DroppedEvents ?? 0;
        var failures = health.WriterFailures ?? 0;
        if ((loss > _notifiedLoss || failures > _notifiedFailures)
            && Add(null, "telemetry_writer_loss", loss - _notifiedLoss + failures - _notifiedFailures, nowTicks))
        {
            _notifiedLoss = loss;
            _notifiedFailures = failures;
        }
        if (health.PendingUpdates >= 1000)
        {
            if (_queuePressureSince == 0) _queuePressureSince = nowTicks;
            if (nowTicks - _queuePressureSince >= 30000) Add(null, "severe_queue", health.PendingUpdates.Value, nowTicks);
        }
        else _queuePressureSince = 0;
        if (health.ChannelDepth >= _options.ChannelCapacity * 0.8)
        {
            if (_channelPressureSince == 0) _channelPressureSince = nowTicks;
            if (nowTicks - _channelPressureSince >= 60000) Add(null, "telemetry_channel_pressure", health.ChannelDepth.Value, nowTicks);
        }
        else _channelPressureSince = 0;
        foreach (var window in _bots.Values) EvaluateBot(window, nowTicks);
        EvaluateBot(_global, nowTicks);
        EvaluateBot(_overflow, nowTicks);
        return _summaries;
    }

    /// <summary>Evaluates one bounded bot window against sustained incident rules.</summary>
    /// <param name="window">Individually tracked or global overflow numeric state.</param>
    /// <param name="nowTicks">Current monotonic milliseconds.</param>
    private void EvaluateBot(BotWindow window, long nowTicks)
    {
        window.Advance(nowTicks);
        if (window.PollFailures >= 3 && window.PollDegradedSince != 0 && nowTicks - window.PollDegradedSince >= 60000)
            Add(window.BotId, "polling_degradation", window.PollFailures, nowTicks, mixedBots: window.MixedBots);
        if (window.ForegroundTimeouts >= 3 && Add(window.BotId, "foreground_timeouts", window.ForegroundTimeouts, nowTicks, mixedBots: window.MixedBots))
            window.ForegroundTimeouts = 0;
        if (window.SevereQueueCount >= 3 && Add(window.BotId, "severe_queue", window.SevereQueueCount, nowTicks, mixedBots: window.MixedBots))
            window.SevereQueueCount = 0;
        if ((window.BusyCount >= 10 || window.BusyWaitMs >= _options.SlowOperationMs)
            && Add(window.BotId, "sqlite_busy", window.BusyCount, nowTicks, mixedBots: window.MixedBots))
        {
            window.BusyCount = 0;
            window.BusyWaitMs = 0;
        }
        var p95 = Quantile(window.Histogram, window.UpdateCount);
        if (window.UpdateCount >= 20 && window.BaselineCount >= 20 && p95 >= _options.SlowUpdateMs
            && p95 >= window.BaselineP95 * 2)
            Add(window.BotId, "p95_regression", window.UpdateCount, nowTicks, p95, window.BaselineP95, window.MixedBots);
    }

    /// <summary>Admits one closed-vocabulary summary through the existing cooldown limiter.</summary>
    /// <param name="botId">Canonical bot id or null for global/overflow state; never a user/chat identifier.</param>
    /// <param name="category">Internal incident family from this class's closed constants.</param>
    /// <param name="count">Contributing observations or current queued item count.</param>
    /// <param name="nowTicks">Current monotonic milliseconds.</param>
    /// <param name="p95">Optional approximate current-window milliseconds.</param>
    /// <param name="baseline">Optional approximate prior-window milliseconds.</param>
    /// <param name="mixedBots">Whether the summary combines bot ids beyond the bounded individual-window limit.</param>
    /// <returns>True only when the summary was newly admitted; false for cooldown or the per-tick cap.</returns>
    private bool Add(string botId, string category, long count, long nowTicks, double? p95 = null, double? baseline = null, bool mixedBots = false)
    {
        if (_summaries.Count >= 8 || !_limiter.ShouldNotify((botId ?? (mixedBots ? "overflow" : "global")) + "|" + category, Cooldown, nowTicks)) return false;
        _summaries.Add(new LatencyTelemetryEvent
        {
            EventType = "telemetry_incident", BotId = botId, Category = category, Outcome = "degraded",
            Operation = mixedBots ? "mixed_bot_window" : "incident_window",
            IncidentCount = count, WindowSeconds = WindowMs / 1000d, P95Ms = p95, BaselineP95Ms = baseline
        });
        return true;
    }

    /// <summary>Recognizes closed interactive deadline outcomes with caller shutdown taking precedence.</summary>
    /// <param name="observation">Validated foreground request event; only controlled metadata is examined.</param>
    /// <returns>True for foreground/callback deadlines, false for explicit caller/host shutdown or successful responses.</returns>
    /// <remarks>Cumulative update summaries are not recounted. A retained lower-layer timeout label cannot override the caller token owner.</remarks>
    private static bool IsTimeout(LatencyTelemetryEvent observation)
        => observation.CancellationSource is not ("caller" or "shutdown" or "host_shutdown")
            && observation.Outcome is not ("success" or "succeeded" or "completed")
            && (observation.Outcome is "foreground_budget_expired" or "callback_policy_timeout" or "timeout" or "timed_out" or "foreground_timeout"
                || observation.FailureClassification is "foreground_budget_timeout" or "callback_policy_timeout"
                || observation.TimeoutCategory is "foreground" or "callback_best_effort" or "foreground_budget" or "callback_policy");

    /// <summary>Creates fixed logarithmic histogram upper bounds with approximately 20 percent resolution.</summary>
    /// <returns>Sixty-four positive ordered bounds in milliseconds.</returns>
    private static double[] CreateBounds()
    {
        var bounds = new double[64];
        for (var index = 0; index < bounds.Length; index++) bounds[index] = Math.Pow(1.25, index) * 10;
        return bounds;
    }

    /// <summary>Finds the histogram's approximate 95th-percentile upper bucket bound.</summary>
    /// <param name="histogram">Fixed-size numeric duration buckets.</param>
    /// <param name="count">Total contributing observations; zero represents unavailable data.</param>
    /// <returns>Approximate elapsed milliseconds, or zero when no observations exist.</returns>
    private static double Quantile(long[] histogram, long count)
    {
        var target = (long)Math.Ceiling(count * 0.95);
        long cumulative = 0;
        for (var index = 0; index < histogram.Length; index++)
        {
            cumulative += histogram[index];
            if (target > 0 && cumulative >= target) return HistogramBounds[index];
        }
        return 0;
    }

    /// <summary>Fixed-memory numeric state; no raw observation or customer identifier is retained.</summary>
    private sealed class BotWindow
    {
        /// <summary>Canonical runtime bot id, or null for combined overflow state.</summary>
        public readonly string BotId;
        /// <summary>Whether this window combines multiple overflow bot ids and cannot attribute an individual recovery.</summary>
        public readonly bool MixedBots;
        /// <summary>Current duration histogram.</summary>
        public readonly long[] Histogram = new long[64];
        /// <summary>Monotonic beginning of the current aggregation window.</summary>
        private long _started;
        /// <summary>Current terminal-update count with observed receiver-origin application latency.</summary>
        public long UpdateCount;
        /// <summary>Previous complete-window receiver-origin update count for regression eligibility.</summary>
        public long BaselineCount;
        /// <summary>Previous complete-window approximate 95th-percentile milliseconds.</summary>
        public double BaselineP95;
        /// <summary>Foreground timeout observations not yet represented by an admitted summary.</summary>
        public long ForegroundTimeouts;
        /// <summary>Severe queue-delay observations not yet represented by an admitted summary.</summary>
        public long SevereQueueCount;
        /// <summary>SQLite busy retry observations.</summary>
        public long BusyCount;
        /// <summary>Accumulated SQLite busy backoff milliseconds.</summary>
        public double BusyWaitMs;
        /// <summary>Monotonic origin of sustained polling degradation, independent of window rollover.</summary>
        public long PollDegradedSince;
        /// <summary>Consecutive polling failures, reset by observed success.</summary>
        public int PollFailures;
        /// <summary>Creates a fixed-memory window for a canonical bot.</summary>
        /// <param name="botId">Canonical bot id or null for global overflow aggregation.</param>
        /// <param name="mixedBots">True for the capped-cardinality overflow window; false for one bot or explicit global background work.</param>
        public BotWindow(string botId, bool mixedBots = false) { BotId = botId; MixedBots = mixedBots; }
        /// <summary>Advances finite numeric windows without carrying obsolete baselines across idle periods.</summary>
        /// <param name="nowTicks">Current monotonic milliseconds.</param>
        public void Advance(long nowTicks)
        {
            if (_started == 0) _started = nowTicks;
            if (nowTicks - _started < WindowMs) return;
            BaselineP95 = nowTicks - _started < 2 * WindowMs ? Quantile(Histogram, UpdateCount) : 0;
            BaselineCount = nowTicks - _started < 2 * WindowMs ? UpdateCount : 0;
            _started = nowTicks;
            Array.Clear(Histogram);
            UpdateCount = ForegroundTimeouts = SevereQueueCount = BusyCount = 0;
            BusyWaitMs = 0;
            if (MixedBots) { PollDegradedSince = 0; PollFailures = 0; }
        }
    }
}
