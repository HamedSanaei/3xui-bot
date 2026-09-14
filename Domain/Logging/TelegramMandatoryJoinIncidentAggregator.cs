using System;
using System.Collections.Concurrent;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Bounded process-local aggregator that turns repeated mandatory-join timeouts into at most one operator incident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this type exists:
    /// a single mandatory-join membership probe that hits its own five-second guard is a controlled outcome: the guard
    /// released the update lane exactly as designed and the customer simply repeats the action. Reporting every such
    /// event to the operator channel produced one alert per bot per probe and buried real incidents.
    /// </para>
    /// <para>
    /// Incident rule:
    /// the first isolated timeouts stay local. Once <see cref="DegradationThreshold"/> timeouts accumulate inside
    /// <see cref="DegradationWindow"/> for the same bot, the condition is no longer an isolated hiccup and one bounded
    /// operator incident is emitted. Further events for the same bot are withheld for
    /// <see cref="IncidentCooldown"/> so one sustained degradation cannot produce an alert storm.
    /// </para>
    /// <para>
    /// Local visibility:
    /// this type only decides channel routing. Every event still reaches the daily diagnostic file, the console
    /// logger, and the metrics instruments at its original level.
    /// </para>
    /// <para>
    /// Scope:
    /// aggregation is per exact runtime bot id, so a degraded storefront cannot mask or trigger another bot's
    /// incident. The map holds only bot ids and counters, grows with distinct bots rather than with traffic, and is
    /// cleared once it exceeds <see cref="MaxBots"/>.
    /// </para>
    /// </remarks>
    public sealed class TelegramMandatoryJoinIncidentAggregator
    {
        /// <summary>
        /// Number of mandatory-join timeouts for one bot inside <see cref="DegradationWindow"/> that proves sustained
        /// degradation rather than an isolated hiccup.
        /// </summary>
        public const int DegradationThreshold = 3;

        /// <summary>Sliding window in which repeated timeouts are counted as one sustained condition.</summary>
        public static readonly TimeSpan DegradationWindow = TimeSpan.FromMinutes(10);

        /// <summary>Minimum interval between two operator incidents for the same bot.</summary>
        public static readonly TimeSpan IncidentCooldown = TimeSpan.FromMinutes(30);

        /// <summary>Maximum number of bots tracked before the aggregation state is cleared.</summary>
        public const int MaxBots = 4096;

        /// <summary>Per-bot admission counters for one degradation window.</summary>
        private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

        /// <summary>
        /// Determines whether one mandatory-join timeout for a bot should become a bounded operator incident.
        /// </summary>
        /// <param name="botId">
        /// Exact runtime bot id whose mandatory-join probe timed out, for example <c>vpnetiranbot</c> or
        /// <c>tenant-7405716665</c>. This is the internal configured id, never a chat id or token. A blank value is
        /// grouped under a single unknown bucket so the policy still applies instead of disabling itself.
        /// </param>
        /// <param name="nowTicks">
        /// Monotonic millisecond timestamp of the timeout event, supplied by the caller so the decision is deterministic
        /// in tests.
        /// </param>
        /// <returns>
        /// <c>true</c> when this occurrence is the bounded operator incident for sustained degradation; <c>false</c>
        /// for isolated timeouts and for every event withheld by the incident cooldown.
        /// </returns>
        /// <remarks>
        /// The counter resets whenever more than <see cref="DegradationWindow"/> has elapsed since the first counted
        /// timeout, so occasional timeouts spread over hours never accumulate into an incident.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Isolated: false. The third event inside ten minutes for the same bot: true.
        /// var notify = aggregator.ShouldNotifyOperator("tenant-7405716665", Environment.TickCount64);
        /// </code>
        /// </example>
        public bool ShouldNotifyOperator(string botId, long nowTicks)
        {
            var key = (botId ?? string.Empty).Trim();
            if (key.Length == 0)
                key = "-";

            if (_buckets.Count >= MaxBots)
                _buckets.Clear();

            var bucket = _buckets.GetOrAdd(key, static _ => new Bucket());
            var windowMilliseconds = (long)DegradationWindow.TotalMilliseconds;
            var cooldownMilliseconds = (long)IncidentCooldown.TotalMilliseconds;

            lock (bucket)
            {
                // A counter that has aged out of the window restarts, so isolated timeouts spread over hours are never
                // aggregated into a false sustained incident.
                if (bucket.Count == 0 || nowTicks - bucket.WindowStartTicks >= windowMilliseconds)
                {
                    bucket.WindowStartTicks = nowTicks;
                    bucket.Count = 0;
                }

                bucket.Count++;
                if (bucket.Count < DegradationThreshold)
                    return false;

                if (bucket.HasNotified && nowTicks - bucket.LastNotificationTicks < cooldownMilliseconds)
                    return false;

                bucket.HasNotified = true;
                bucket.LastNotificationTicks = nowTicks;
                return true;
            }
        }

        /// <summary>Mutable per-bot aggregation state; mutated only under its own lock.</summary>
        private sealed class Bucket
        {
            /// <summary>Monotonic timestamp at which the current counting window started.</summary>
            public long WindowStartTicks;

            /// <summary>Number of timeouts counted since <see cref="WindowStartTicks"/>.</summary>
            public int Count;

            /// <summary>Monotonic timestamp of the last operator incident emitted for this bot.</summary>
            public long LastNotificationTicks;

            /// <summary>Whether an operator incident was already emitted for this bot.</summary>
            public bool HasNotified;
        }
    }
}
