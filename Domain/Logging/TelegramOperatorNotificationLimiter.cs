using System;
using System.Collections.Concurrent;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Bounded process-local limiter that allows at most one operator-channel notification per key inside a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this type exists:
    /// several Warning families are individually meaningful but repeat for as long as one underlying condition lasts.
    /// Without a limiter the operator channel shows the same sentence dozens of times and stops being an incident
    /// stream. The limiter is therefore a routing policy for the private Telegram channel only; every call still
    /// reaches the daily diagnostic file, the console logger, and the metrics instruments.
    /// </para>
    /// <para>
    /// First-occurrence policy:
    /// the first notification for a key is always delivered, because one genuine incident must stay visible. Only the
    /// repeats inside the window are withheld.
    /// </para>
    /// <para>
    /// Bounding:
    /// the map grows with distinct keys rather than with traffic and is cleared wholesale once it exceeds
    /// <see cref="MaxKeys"/>. Clearing can only cause an extra notification, never a hidden incident.
    /// </para>
    /// </remarks>
    public sealed class TelegramOperatorNotificationLimiter
    {
        /// <summary>Maximum number of distinct keys retained before the dictionary is cleared.</summary>
        public const int MaxKeys = 4096;

        /// <summary>Monotonic timestamp of the last delivered notification for each key.</summary>
        private readonly ConcurrentDictionary<string, long> _lastNotificationByKey = new(StringComparer.Ordinal);

        /// <summary>
        /// Determines whether an operator notification is allowed for one key at the supplied monotonic timestamp.
        /// </summary>
        /// <param name="key">
        /// Non-secret composite key describing the repeated condition, for example a bot id combined with a closed
        /// vocabulary request kind or execution stage. Customer text, chat ids, tokens, URLs, and payloads must never
        /// be part of the key.
        /// </param>
        /// <param name="window">
        /// Minimum interval between two delivered notifications for the same key. Must be positive.
        /// </param>
        /// <param name="nowTicks">
        /// Monotonic millisecond timestamp supplied by the caller so the decision is deterministic in tests.
        /// </param>
        /// <returns>
        /// <c>true</c> when this occurrence may be delivered to the operator channel; <c>false</c> when an earlier
        /// occurrence for the same key was already delivered inside <paramref name="window"/>.
        /// </returns>
        /// <remarks>
        /// Exactly one concurrent caller wins a slot refresh, so a burst of parallel handlers produces at most one
        /// notification instead of one per handler.
        /// </remarks>
        /// <example>
        /// <code>
        /// var allowed = limiter.ShouldNotify("owned-noise|SendMessage", window, Environment.TickCount64);
        /// </code>
        /// </example>
        public bool ShouldNotify(string key, TimeSpan window, long nowTicks)
        {
            if (string.IsNullOrWhiteSpace(key) || window <= TimeSpan.Zero)
                return true;

            var windowMilliseconds = (long)window.TotalMilliseconds;
            while (true)
            {
                if (!_lastNotificationByKey.TryGetValue(key, out var previous))
                {
                    if (_lastNotificationByKey.Count >= MaxKeys)
                        _lastNotificationByKey.Clear();
                    if (_lastNotificationByKey.TryAdd(key, nowTicks))
                        return true;
                    continue;
                }

                if (nowTicks - previous < windowMilliseconds)
                    return false;

                if (_lastNotificationByKey.TryUpdate(key, nowTicks, previous))
                    return true;
            }
        }
    }
}
