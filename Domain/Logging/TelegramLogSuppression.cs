using System;
using System.Linq;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Shared decision logic that keeps known operational noise out of the private Telegram logger channel.
    /// </summary>
    /// <remarks>
    /// <see cref="TelegramLogger"/> cannot see the final formatted message at category-filter time, so this helper
    /// performs a second message-level check inside the Telegram provider. Keeping the decision here makes the policy
    /// unit-testable without a live Telegram client and guarantees that every logger category applies exactly the same
    /// suppression rules.
    /// </remarks>
    public static class TelegramLogSuppression
    {
        /// <summary>
        /// Operation names of the two UX latency guards whose controlled outcomes are not application failures.
        /// </summary>
        private static readonly string[] ControlledLatencyOperations = { "telegram_callback_ack", "telegram_mandatory_join" };

        /// <summary>
        /// Minimum interval between operator-channel notifications for the same bot, operation, and outcome.
        /// </summary>
        private static readonly TimeSpan ControlledLatencyOperatorWindow = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Process-local timestamp of the last operator notification for each controlled latency event key.
        /// </summary>
        /// <remarks>
        /// Keys are <c>BotId|Operation|Outcome</c>. Only controlled local-timeout events are recorded, so the map grows
        /// with distinct bot/operation pairs rather than with traffic, and it is cleared if it ever exceeds a small
        /// bound. This is deliberately not a general log-suppression framework: only the two documented latency guards
        /// are affected, and every Warning, Error, Critical, delivery-uncertain, manual-review, transport, XUI, payment,
        /// and provider failure still reaches the channel unchanged.
        /// </remarks>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> ControlledLatencyNotifications
            = new(StringComparer.Ordinal);

        /// <summary>Upper bound on retained controlled-latency keys before the map is reset.</summary>
        private const int MaxControlledLatencyKeys = 4096;

        /// <summary>
        /// Determines whether a formatted log entry must stay out of the Telegram logger channel.
        /// </summary>
        /// <param name="message">
        /// Formatted log message produced by the calling logger category. The value can contain only the summary
        /// text or the provider's compact error text.
        /// </param>
        /// <param name="exception">
        /// Optional exception supplied to the logger. A Telegram <see cref="Telegram.Bot.Exceptions.ApiRequestException"/>
        /// with error code 429 suppresses the entry structurally, regardless of message text.
        /// </param>
        /// <returns>
        /// <c>true</c> when the entry is operational noise that should stay in local logs only; otherwise <c>false</c>
        /// so payment, audit, token, XUI, and settlement failures still reach the private channel.
        /// </returns>
        /// <remarks>
        /// The method intentionally suppresses only known noisy patterns: stale callbacks, unchanged Telegram edits,
        /// receipt-photo relay failures that have a text fallback, repeated tenant forced-join probes, routine XUI v3
        /// volume-reminder scan summaries, per-attempt UniquePay GET-reconciliation diagnostics, the compact
        /// <c>Telegram polling degraded</c> transient-backoff summary, and Telegram polling 5xx/429/timeouts. The first
        /// ambiguous UniquePay create and the terminal recovery/manual-review transition
        /// use different messages and remain visible. Business failures such as invalid tokens, duplicate tokens, XUI
        /// scan/delivery failures, and payment settlement errors are not suppressed.
        ///
        /// A Telegram 429 exception suppresses the entry before any message text is inspected: the failure being
        /// reported is Telegram rate limiting, so sending a Telegram notification about it would trigger another send
        /// under the same rate limit and amplify the storm.
        /// </remarks>
        public static bool ShouldSuppress(string message, Exception exception)
        {
            // A Telegram 429 is itself the failure being reported. Forwarding it to the Telegram channel would issue
            // another send that is subject to the same rate limit, amplifying the 429 storm instead of quieting it.
            if (TelegramRateLimitPolicy.IsRateLimited(exception))
                return true;

            // Controlled UX latency-guard outcomes are expected results, not incidents. They stay fully visible in the
            // daily diagnostic file, the structured logger, and the stage instruments, but they must not flood the
            // operator channel. Real guard failures (transport, API, channel-access) are never suppressed here.
            if (ShouldSuppressControlledLatencyEvent(message))
                return true;

            var combined = string.Join(
                "\n",
                new[]
                {
                    message ?? string.Empty,
                    exception?.Message ?? string.Empty
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(combined))
                return false;

            if (ContainsOrdinalIgnoreCase(combined, "Ignoring stale sales-assistant callback answer") ||
                ContainsOrdinalIgnoreCase(combined, "IGNORING STALE Telegram callback answer") ||
                ContainsOrdinalIgnoreCase(combined, "Ignoring unchanged sales-assistant reply markup") ||
                ContainsOrdinalIgnoreCase(combined, "Ignoring unchanged sales-assistant receipt caption") ||
                ContainsOrdinalIgnoreCase(combined, "sales assistant receipt notification failed") ||
                ContainsOrdinalIgnoreCase(combined, "tenant forced-join validation failed") ||
                ContainsOrdinalIgnoreCase(combined, "tenant forced-join check failed") ||
                ContainsOrdinalIgnoreCase(combined, "UniquePay inquiry retry detail:") ||
                ContainsOrdinalIgnoreCase(combined, "Tenant UniquePay customer inquiry failed") ||
                ContainsOrdinalIgnoreCase(combined, "UniquePay HTTP trigger inquiry failed") ||
                ContainsOrdinalIgnoreCase(combined, "XUI v3 volume reminder scan finished.") ||
                ContainsOrdinalIgnoreCase(combined, "Telegram polling degraded.") ||
                ContainsOrdinalIgnoreCase(combined, "Transient Telegram polling gateway error ignored") ||
                ContainsOrdinalIgnoreCase(combined, "Gozargah site wallet debit response received."))
            {
                return true;
            }

            var isTelegramPollingNoise =
                ContainsOrdinalIgnoreCase(combined, "Telegram polling") ||
                ContainsOrdinalIgnoreCase(combined, "polling delivery") ||
                ContainsOrdinalIgnoreCase(combined, "getUpdates");

            if (!isTelegramPollingNoise)
                return false;

            return ContainsOrdinalIgnoreCase(combined, "Bad Gateway") ||
                   ContainsOrdinalIgnoreCase(combined, "gateway timeout") ||
                   ContainsOrdinalIgnoreCase(combined, "service unavailable") ||
                   ContainsOrdinalIgnoreCase(combined, "Too Many Requests") ||
                   ContainsOrdinalIgnoreCase(combined, "Request timed out");
        }

        /// <summary>
        /// Decides whether one controlled latency-guard event must be kept out of the operator Telegram channel.
        /// </summary>
        /// <param name="message">
        /// Formatted log message. Only the closed <c>Slow Telegram operation.</c> family is inspected.
        /// </param>
        /// <returns>
        /// <c>true</c> when the event is a successful guard completion or a repeat of a rate-limited local timeout;
        /// <c>false</c> for every other outcome, so real transport, API, and channel-access failures are still delivered.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Classification follows the production log review that separated three message families:
        /// </para>
        /// <list type="bullet">
        /// <item><c>Outcome=completed</c> — the request finished inside its own hard deadline. This is a success
        /// (for example mandatory join at 2.2 seconds inside its 5 second budget), so it is metric/file telemetry only
        /// and never an operator alert.</item>
        /// <item><c>Outcome=local_timeout</c> — the guard worked exactly as designed and released the lane. It is kept in
        /// the daily file and the structured logger but limited to one operator notification per ten minutes per
        /// bot/operation/outcome key, so a slow Telegram API can no longer fill the operator channel.</item>
        /// <item>Every other outcome — <c>transport_error</c>, <c>telegram_api_error</c>, <c>channel_access_error</c>,
        /// <c>telegram_timeout</c>, and Telegram API errors — is a real operational signal and is never suppressed.</item>
        /// </list>
        /// </remarks>
        private static bool ShouldSuppressControlledLatencyEvent(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || !ContainsOrdinalIgnoreCase(message, "Slow Telegram operation."))
                return false;

            var operation = TryReadMessageToken(message, "Operation=");
            if (operation == null || Array.IndexOf(ControlledLatencyOperations, operation) < 0)
                return false;

            var outcome = TryReadMessageToken(message, "Outcome=");
            if (string.Equals(outcome, "completed", StringComparison.Ordinal))
                return true;

            if (!string.Equals(outcome, "local_timeout", StringComparison.Ordinal))
                return false;

            var key = (TryReadMessageToken(message, "BotId=") ?? "-") + "|" + operation + "|" + outcome;
            var now = Environment.TickCount64;
            var windowMilliseconds = (long)ControlledLatencyOperatorWindow.TotalMilliseconds;
            while (true)
            {
                if (!ControlledLatencyNotifications.TryGetValue(key, out var previous))
                {
                    if (ControlledLatencyNotifications.Count >= MaxControlledLatencyKeys)
                        ControlledLatencyNotifications.Clear();
                    if (ControlledLatencyNotifications.TryAdd(key, now))
                        return false;
                    continue;
                }

                if (now - previous < windowMilliseconds)
                    return true;

                // Only one concurrent caller wins the slot refresh, so a burst cannot produce several notifications.
                if (ControlledLatencyNotifications.TryUpdate(key, now, previous))
                    return false;
            }
        }

        /// <summary>
        /// Reads the single whitespace-delimited token that follows a structured marker in a formatted message.
        /// </summary>
        /// <param name="message">Formatted log message produced by the structured formatter.</param>
        /// <param name="marker">Marker including its trailing equals sign, for example <c>Operation=</c>.</param>
        /// <returns>The token value, or <c>null</c> when the marker is absent or carries no value.</returns>
        /// <remarks>
        /// This is a narrow textual lookup for closed-vocabulary fields only; it never parses free-form text and never
        /// returns customer data.
        /// </remarks>
        private static string TryReadMessageToken(string message, string marker)
        {
            var start = message.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return null;

            var valueStart = start + marker.Length;
            var valueEnd = valueStart;
            while (valueEnd < message.Length && !char.IsWhiteSpace(message[valueEnd]))
                valueEnd++;

            return valueEnd > valueStart ? message[valueStart..valueEnd] : null;
        }

        /// <summary>
        /// Checks whether a string contains another string using ordinal, case-insensitive comparison.
        /// </summary>
        /// <param name="source">Text to inspect. A null value is treated as no match.</param>
        /// <param name="value">Needle to find. A null or empty value is treated as no match.</param>
        /// <returns><c>true</c> when <paramref name="value"/> appears in <paramref name="source"/>; otherwise <c>false</c>.</returns>
        private static bool ContainsOrdinalIgnoreCase(string source, string value)
        {
            return !string.IsNullOrEmpty(source) &&
                   !string.IsNullOrEmpty(value) &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
