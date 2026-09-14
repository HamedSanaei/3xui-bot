using System;
using System.Globalization;
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
        /// Minimum interval between operator-channel notifications for a repeated Warning family whose first
        /// occurrence is an incident and whose later occurrences are the same incident repeated.
        /// </summary>
        private static readonly TimeSpan RepeatedIncidentOperatorWindow = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Process-local limiter for Warning families that repeat for as long as one condition lasts.
        /// </summary>
        /// <remarks>
        /// Only the documented repeated families use this limiter, and the first occurrence of each key is always
        /// delivered. Every occurrence still reaches the daily diagnostic file, the console logger, and the metrics
        /// instruments at its original level.
        /// </remarks>
        private static readonly TelegramOperatorNotificationLimiter RepeatedIncidentNotifications = new();

        /// <summary>
        /// Process-local aggregator that converts repeated mandatory-join timeouts into one bounded operator incident.
        /// </summary>
        /// <remarks>
        /// A single isolated <c>telegram_mandatory_join</c> local timeout is a controlled guard outcome and stays out
        /// of the operator channel. Only sustained degradation for the same bot can produce a notification, and the
        /// aggregator itself bounds how often that can happen.
        /// </remarks>
        private static readonly TelegramMandatoryJoinIncidentAggregator MandatoryJoinIncidentAggregation = new();

        /// <summary>Closed-vocabulary operation name of the callback-acknowledgement UX guard.</summary>
        private const string CallbackAcknowledgementOperation = "telegram_callback_ack";

        /// <summary>
        /// Exact summary family emitted when one interactive foreground Telegram delivery exceeded its budget.
        /// </summary>
        private const string ForegroundDeliveryBudgetMessage = "Telegram foreground delivery exceeded its interactive budget";

        /// <summary>Exact summary family emitted when a running handler crosses the live long-handler threshold.</summary>
        private const string LiveLongHandlerMessage = "Telegram update handler running unusually long.";

        /// <summary>Exact summary family emitted for successful tenant fulfillment timing telemetry.</summary>
        private const string TenantFulfillmentTimingMessage = "Tenant fulfillment timing.";

        /// <summary>Exact summary family emitted when one tenant post-commit notification attempt finished.</summary>
        private const string TenantFulfillmentNotificationMessage = "Tenant fulfillment post-commit notification completed.";

        /// <summary>Exact summary family emitted by the routine XUI volume-reminder state prune.</summary>
        private const string PrunedVolumeReminderStateMessage = "Pruned expired missing XUI volume reminder state.";

        /// <summary>Exact summary family emitted after an XUI v3 renewal was applied exactly once.</summary>
        private const string XuiRenewalAppliedOnceMessage = "XUI v3 renewal applied exactly once.";

        /// <summary>Exact structured outcome value that marks a tenant post-commit notification as delivered.</summary>
        private const string DeliveredOutcome = "delivered";

        /// <summary>
        /// Handler duration at which a completed handler stops being performance telemetry and becomes an operator
        /// incident. Production value: ten thousand milliseconds (ten seconds).
        /// </summary>
        /// <remarks>
        /// This is the same boundary the scheduler uses for its live long-handler watchdog. The scheduler initializes
        /// its warning threshold from this constant, so the channel policy and the watchdog can never disagree about
        /// where telemetry ends and an incident begins.
        /// </remarks>
        public const double LongHandlerOperatorThresholdMilliseconds = 10_000;

        /// <summary>Exact summary family emitted for one closed-vocabulary stage above the slow-stage threshold.</summary>
        private const string SlowStageMessage = "Telegram slow update stage.";

        /// <summary>Exact summary family emitted when a handler ends above the interactive five-second threshold.</summary>
        private const string InteractiveThresholdMessage = "Telegram update handler exceeded the interactive latency threshold.";

        /// <summary>Exact summary family emitted when a handler above the live long-handler warning threshold ended.</summary>
        private const string LongHandlerCompletedMessage = "Telegram long update handler completed.";

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
        /// <c>Telegram polling degraded</c> transient-backoff summary, Telegram polling 5xx/429/timeouts, the closed
        /// latency-telemetry families handled by <see cref="ShouldSuppressLatencyTelemetry"/>, the controlled guard
        /// outcomes handled by <see cref="ShouldSuppressControlledLatencyEvent"/>, the routine success families handled
        /// by <see cref="ShouldSuppressRoutineSuccessTelemetry"/>, the repeated Warning families handled by
        /// <see cref="ShouldSuppressRepeatedIncidentFamily"/>, and the two routine tenant storefront funding bookkeeping
        /// successes (the durable owner notification is unaffected). The first ambiguous UniquePay create and the
        /// terminal recovery/manual-review transition use different messages and remain visible. Business failures such
        /// as invalid tokens, duplicate tokens, XUI scan/delivery failures, funding delivery uncertainty, payment
        /// settlement errors, payment/XUI/manual-review transitions, and mandatory-join transport, API, or channel-access
        /// failures are not suppressed.
        ///
        /// A Telegram 429 exception suppresses the entry before any message text is inspected: the failure being
        /// reported is Telegram rate limiting, so sending a Telegram notification about it would trigger another send
        /// under the same rate limit and amplify the storm.
        ///
        /// The channel is an actionable incident stream, not a raw telemetry mirror. The daily diagnostic file, the
        /// console/structured logger, and the metrics instruments continue to receive every one of these events at its
        /// existing level, so local diagnosis loses nothing when a message is withheld from Telegram.
        /// </remarks>
        public static bool ShouldSuppress(string message, Exception exception)
            => ShouldSuppress(message, exception, Environment.TickCount64);

        /// <summary>
        /// Determines whether a formatted log entry must stay out of the Telegram logger channel, evaluated against an
        /// explicit monotonic timestamp.
        /// </summary>
        /// <param name="message">Formatted log message produced by the calling logger category.</param>
        /// <param name="exception">Optional exception supplied to the logger.</param>
        /// <param name="nowTicks">
        /// Monotonic millisecond timestamp used by the bounded rate limiters, supplied by the caller so regression
        /// tests can prove windowing without waiting for real time to pass.
        /// </param>
        /// <returns>
        /// <c>true</c> when the entry is operational noise that should stay in local logs only; otherwise <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This is the deterministic entry point behind <see cref="ShouldSuppress(string, Exception)"/>. Production code
        /// always calls the two-argument overload; the timestamp parameter exists only so the rate-limited families can
        /// be verified without sleeping.
        /// </remarks>
        internal static bool ShouldSuppress(string message, Exception exception, long nowTicks)
        {
            // A Telegram 429 is itself the failure being reported. Forwarding it to the Telegram channel would issue
            // another send that is subject to the same rate limit, amplifying the 429 storm instead of quieting it.
            if (TelegramRateLimitPolicy.IsRateLimited(exception))
                return true;

            // Controlled UX latency-guard outcomes are expected results, not incidents. They stay fully visible in the
            // daily diagnostic file, the structured logger, and the stage instruments, but they must not flood the
            // operator channel. Real guard failures (transport, API, channel-access) are never suppressed here.
            if (ShouldSuppressControlledLatencyEvent(message, nowTicks))
                return true;

            // Latency measurements and successful bookkeeping are telemetry, not incidents. They stay fully visible in
            // the daily diagnostic file, the console/structured logger, and the stage instruments, but the operator
            // channel must not be used as a raw performance stream.
            if (ShouldSuppressLatencyTelemetry(message))
                return true;

            // Routine success and bookkeeping telemetry: a successful fulfillment timing sample, a completed
            // post-commit notification that was actually delivered, a routine reminder-state prune, and a renewal that
            // was applied exactly once are all expected results. They stay in the daily diagnostic file, the console
            // logger, and the metrics instruments but must not be treated as operator incidents.
            if (ShouldSuppressRoutineSuccessTelemetry(message))
                return true;

            // Repeated Warning families: the first occurrence of a condition is delivered as one bounded incident, and
            // the repeats that describe the same still-unresolved condition are withheld until its window elapses.
            // Every occurrence remains fully visible locally.
            if (ShouldSuppressRepeatedIncidentFamily(message, nowTicks))
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
                // Routine internal bookkeeping successes: the tenant owner already receives the durable funding
                // notification, so the bookkeeping line that merely says the outbox intent was queued or that the
                // storefront crossed the underfunded threshold is not an operator incident. Genuine delivery failures
                // ("...became uncertain.", worker scan failures, transport exhaustion) keep their own messages and are
                // never matched here.
                ContainsOrdinalIgnoreCase(combined, "Underfunded tenant storefront customer-attempt alert queued.") ||
                ContainsOrdinalIgnoreCase(combined, "Tenant storefront became underfunded.") ||
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
        /// Decides whether one latency-telemetry or routine-success event must be kept out of the operator Telegram
        /// channel.
        /// </summary>
        /// <param name="message">
        /// Formatted log message. Only the three closed scheduler message families are inspected: the slow-stage
        /// attribution line, the interactive-threshold completion line, and the long-handler completion line.
        /// </param>
        /// <returns>
        /// <c>true</c> when the entry is a measurement or a completion echo rather than an incident; <c>false</c> for
        /// the live long-handler warning, for any handler that ended in a failure outcome, and whenever the duration
        /// cannot be read unambiguously.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The operator channel is meant to carry actionable incidents, not raw telemetry. Three distinct latency
        /// families are classified here:
        /// </para>
        /// <list type="bullet">
        /// <item>The <c>Telegram slow update stage.</c> line attributes one already-counted handler to a closed stage
        /// (<c>xui_read</c>, <c>telegram_send</c>, <c>telegram_membership</c>, <c>site_lookup</c>). It is valuable for
        /// local diagnosis and metrics and is never an incident by itself, so it always stays out of the channel.</item>
        /// <item>A <c>completed</c> handler above the five-second interactive threshold but below the ten-second live
        /// warning threshold is performance telemetry. Production proved this shape repeatedly: a successful 5.3 second
        /// handler is not a failure.</item>
        /// <item>The <c>Telegram long update handler completed.</c> echo only repeats a >= ten-second execution for
        /// which the live watchdog already delivered the single operator alert.</item>
        /// </list>
        /// <para>
        /// The live <c>Telegram update handler running unusually long.</c> warning, any <c>Outcome</c> other than
        /// <c>completed</c>, a duration at or above the long-handler threshold, and every unparseable or non-finite
        /// duration remain visible, so hardening the channel routing can never hide a genuine root blocker. The live
        /// warning is the only member of this family that is additionally rate-limited, and it is limited per bot and
        /// per closed-vocabulary execution stage by <see cref="ShouldSuppressRepeatedIncidentFamily"/> so one stuck stage
        /// reports once instead of once per watchdog interval.
        /// </para>
        /// </remarks>
        private static bool ShouldSuppressLatencyTelemetry(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            // Case A: per-stage attribution telemetry. Always local/metrics only.
            if (ContainsOrdinalIgnoreCase(message, SlowStageMessage))
                return true;

            // Case C: the completion echo of a handler the live watchdog already reported once.
            if (ContainsOrdinalIgnoreCase(message, LongHandlerCompletedMessage))
                return true;

            // Case B: the interactive-threshold completion line. Suppression requires all three of an explicit
            // completed outcome, an unambiguous duration, and a duration strictly below the incident threshold.
            if (!ContainsOrdinalIgnoreCase(message, InteractiveThresholdMessage))
                return false;

            if (!string.Equals(TryReadMessageToken(message, "Outcome="), "completed", StringComparison.Ordinal))
                return false;

            return TryReadNonNegativeFiniteNumber(message, "HandlerDurationMs=", out var handlerDurationMs) &&
                   handlerDurationMs < LongHandlerOperatorThresholdMilliseconds;
        }

        /// <summary>
        /// Decides whether one controlled latency-guard event must be kept out of the operator Telegram channel.
        /// </summary>
        /// <param name="message">
        /// Formatted log message. Only the closed <c>Slow Telegram operation.</c> family is inspected, and only the
        /// two documented guard operations <c>telegram_callback_ack</c> and <c>telegram_mandatory_join</c> are affected.
        /// </param>
        /// <param name="nowTicks">
        /// Monotonic millisecond timestamp used by the mandatory-join incident aggregation, supplied explicitly so the
        /// decision is deterministic in tests.
        /// </param>
        /// <returns>
        /// <c>true</c> when the event is a successful guard completion, a callback-acknowledgement timeout, or an
        /// isolated mandatory-join timeout; <c>false</c> for sustained mandatory-join degradation and for every other
        /// outcome, so real transport, API, and channel-access failures are still delivered.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Classification follows the production log review that separated the guard message families, extended by the
        /// later operator-channel cleanup that stopped two remaining noise shapes:
        /// </para>
        /// <list type="bullet">
        /// <item><c>Outcome=completed</c> — the request finished inside its own hard deadline. This is a success
        /// (for example mandatory join at 2.2 seconds inside its 5 second budget), so it is metric/file telemetry only
        /// and never an operator alert.</item>
        /// <item><c>telegram_callback_ack</c> with <c>Outcome=local_timeout</c> — the acknowledgement is a pure UX
        /// operation whose only cost is a spinner that stays visible on the tapped button. Production showed one such
        /// line per bot per callback, and the business handler always continues, so every occurrence stays in the daily
        /// file and the metrics instruments and none of them reach the operator channel.</item>
        /// <item><c>telegram_mandatory_join</c> with <c>Outcome=local_timeout</c> — the guard released the lane exactly
        /// as designed. A single isolated timeout is a controlled outcome and stays local; only repeated degradation for
        /// the same bot, aggregated by <see cref="TelegramMandatoryJoinIncidentAggregator"/>, produces one bounded
        /// operator incident.</item>
        /// <item>Every other outcome — <c>transport_error</c>, <c>telegram_api_error</c>, <c>channel_access_error</c>,
        /// <c>telegram_timeout</c>, and Telegram API errors — is a real operational signal and is never suppressed.</item>
        /// </list>
        /// </remarks>
        private static bool ShouldSuppressControlledLatencyEvent(string message, long nowTicks)
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

            // Callback acknowledgement is a pure UX operation, so every one of its local timeouts is noise.
            if (string.Equals(operation, CallbackAcknowledgementOperation, StringComparison.Ordinal))
                return true;

            // Mandatory-join timeouts are only noise while they are isolated. Sustained degradation for one bot is
            // promoted to one bounded operator incident.
            var botId = TryReadMessageToken(message, "BotId=") ?? "-";
            return !MandatoryJoinIncidentAggregation.ShouldNotifyOperator(botId, nowTicks);
        }

        /// <summary>
        /// Decides whether one routine success or bookkeeping success must be kept out of the operator Telegram channel.
        /// </summary>
        /// <param name="message">Formatted log message. Only exact closed-vocabulary success families are inspected.</param>
        /// <returns>
        /// <c>true</c> when the entry reports an expected success rather than an incident; <c>false</c> for every other
        /// message, and for a tenant post-commit notification whose outcome is not <c>delivered</c>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Four production families are classified here:
        /// </para>
        /// <list type="bullet">
        /// <item><c>Tenant fulfillment timing.</c> — a successful end-to-end duration sample. The durable financial and
        /// owner-facing evidence of the same sale is the payment audit log, so a timing measurement is never an
        /// incident.</item>
        /// <item><c>Tenant fulfillment post-commit notification completed.</c> with <c>outcome=delivered</c> — the
        /// notification reached the customer. Any other outcome (<c>deferred</c>, <c>delivery_uncertain</c>,
        /// <c>failed</c>, <c>manual_review</c>, <c>pre_send_route_unavailable</c>) is a real delivery problem and stays
        /// visible, because the worker also raises a separate Warning for the uncertain cases.</item>
        /// <item><c>Pruned expired missing XUI volume reminder state.</c> — routine retention housekeeping that runs on a
        /// schedule.</item>
        /// <item><c>XUI v3 renewal applied exactly once.</c> — the success echo of the renewal idempotency guard. The
        /// renewal is already recorded in the payment audit log and the renewal operation store.</item>
        /// </list>
        /// <para>
        /// Every family stays fully visible in the daily diagnostic file, the console/structured logger, and the metrics
        /// instruments. Only the operator channel routing changes.
        /// </para>
        /// </remarks>
        private static bool ShouldSuppressRoutineSuccessTelemetry(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            if (ContainsOrdinalIgnoreCase(message, TenantFulfillmentTimingMessage) ||
                ContainsOrdinalIgnoreCase(message, PrunedVolumeReminderStateMessage) ||
                ContainsOrdinalIgnoreCase(message, XuiRenewalAppliedOnceMessage))
            {
                return true;
            }

            // The post-commit notification family is only telemetry when the message proves the customer was reached.
            // Every other outcome keeps its own message and remains channel-visible so a stuck notification is noticed.
            return ContainsOrdinalIgnoreCase(message, TenantFulfillmentNotificationMessage) &&
                   string.Equals(TryReadMessageToken(message, "outcome="), DeliveredOutcome, StringComparison.Ordinal);
        }

        /// <summary>
        /// Decides whether one repeated Warning family must be withheld because the same condition was already reported.
        /// </summary>
        /// <param name="message">Formatted log message. Only exact closed-vocabulary Warning families are inspected.</param>
        /// <param name="nowTicks">Monotonic millisecond timestamp used by the bounded rate limiter.</param>
        /// <returns>
        /// <c>true</c> when an identical alert for the same bot and closed-vocabulary detail was already delivered inside
        /// the window; <c>false</c> otherwise, including for every unrelated message.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Two production families repeat for as long as one condition lasts:
        /// </para>
        /// <list type="bullet">
        /// <item><c>Telegram foreground delivery exceeded its interactive budget</c> — one customer interaction whose
        /// reply was abandoned. The warning is keyed by bot and request kind, so the first abandoned send of each kind
        /// per bot is delivered and a repeated pattern is summarized instead of repeated line by line. The ambiguous
        /// send is still never retried.</item>
        /// <item><c>Telegram update handler running unusually long</c> — the live watchdog alert. It is keyed by bot and
        /// by the closed-vocabulary execution stage the handler is currently inside, so a handler that is genuinely
        /// stuck in a different stage still reports, while the same stuck stage does not repeat once a minute.</item>
        /// </list>
        /// <para>
        /// Keys are built only from identifiers already present in scheduler telemetry plus a closed enumeration member
        /// name, so no callback text, chat id, token, URL, account id, or payload can enter the key. Every occurrence
        /// keeps reaching the daily diagnostic file, the console logger, and the metrics instruments.
        /// </para>
        /// </remarks>
        private static bool ShouldSuppressRepeatedIncidentFamily(string message, long nowTicks)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            if (ContainsOrdinalIgnoreCase(message, ForegroundDeliveryBudgetMessage))
            {
                var foregroundKey = "foreground-budget|" +
                                    (TryReadMessageToken(message, "botId=") ?? "-") + "|" +
                                    (TryReadMessageToken(message, "requestKind=") ?? "-");
                return !RepeatedIncidentNotifications.ShouldNotify(foregroundKey, RepeatedIncidentOperatorWindow, nowTicks);
            }

            if (ContainsOrdinalIgnoreCase(message, LiveLongHandlerMessage))
            {
                var longHandlerKey = "long-handler|" +
                                     (TryReadMessageToken(message, "BotId=") ?? "-") + "|" +
                                     (TryReadMessageToken(message, "Stage=") ?? "none");
                return !RepeatedIncidentNotifications.ShouldNotify(longHandlerKey, RepeatedIncidentOperatorWindow, nowTicks);
            }

            return false;
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
        /// <summary>
        /// Reads one structured numeric token from a formatted message and requires an unambiguous value.
        /// </summary>
        /// <param name="message">Formatted log message produced by the structured formatter.</param>
        /// <param name="marker">Marker including its trailing equals sign, for example <c>HandlerDurationMs=</c>.</param>
        /// <param name="value">
        /// The parsed value when the token is present and finite; zero when the token is absent or malformed. A caller
        /// must treat <c>false</c> as "unknown" and must never read the zero placeholder as a real measurement.
        /// </param>
        /// <returns>
        /// <c>true</c> only when the token parses with the invariant culture into a finite, non-negative number.
        /// </returns>
        /// <remarks>
        /// Channel suppression must fail open. A missing, localized, <c>NaN</c>, <c>Infinity</c>, or negative duration
        /// is unreadable evidence, so the event is delivered instead of being hidden by an accidental match.
        /// </remarks>
        private static bool TryReadNonNegativeFiniteNumber(string message, string marker, out double value)
        {
            value = 0;
            var token = TryReadMessageToken(message, marker);
            if (token == null)
                return false;

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return false;

            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0)
                return false;

            value = parsed;
            return true;
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

            // Comma-separated structured lines such as "botId=tenant-1, userId=5" leave a trailing separator on the
            // token. It is trimmed so the value matches the rendered field exactly instead of only by prefix.
            while (valueEnd > valueStart && (message[valueEnd - 1] == ',' || message[valueEnd - 1] == ';'))
                valueEnd--;

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
