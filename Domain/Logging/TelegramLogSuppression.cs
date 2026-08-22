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
        /// volume-reminder scan summaries, and Telegram polling 5xx/429/timeouts. Business failures such as invalid
        /// tokens, duplicate tokens, XUI scan/delivery failures, and payment settlement errors are not suppressed.
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
                ContainsOrdinalIgnoreCase(combined, "Ignoring unchanged sales-assistant reply markup") ||
                ContainsOrdinalIgnoreCase(combined, "Ignoring unchanged sales-assistant receipt caption") ||
                ContainsOrdinalIgnoreCase(combined, "sales assistant receipt notification failed") ||
                ContainsOrdinalIgnoreCase(combined, "tenant forced-join validation failed") ||
                ContainsOrdinalIgnoreCase(combined, "tenant forced-join check failed") ||
                ContainsOrdinalIgnoreCase(combined, "XUI v3 volume reminder scan finished."))
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
