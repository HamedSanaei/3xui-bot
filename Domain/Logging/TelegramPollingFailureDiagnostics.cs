using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Exceptions;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Closed vocabulary describing why one Telegram long-polling attempt failed.
    /// </summary>
    /// <remarks>
    /// The production incident was triaged incorrectly because every failure shape reached the operator as the same
    /// <c>errorType=RequestException</c> text. A value from this vocabulary tells an operator whether the failure was a
    /// duplicate-poller conflict, an active webhook, a rate limit, a provider-side gateway problem, or a per-user
    /// delivery rejection without reading the raw exception.
    /// </remarks>
    public enum TelegramPollingFailureKind
    {
        /// <summary>Telegram reported that another <c>getUpdates</c> poller is already using this bot token.</summary>
        GetUpdatesConflict,

        /// <summary>Telegram refused <c>getUpdates</c> because a webhook is configured for this bot token.</summary>
        WebhookConflict,

        /// <summary>Telegram returned HTTP 429 and the receiver must wait for its <c>RetryAfter</c> window.</summary>
        RateLimited,

        /// <summary>Provider-side or transport condition that is worth retrying with the bounded backoff.</summary>
        Transient,

        /// <summary>A definitive per-user delivery failure such as a blocked bot or an unreachable chat.</summary>
        Delivery,

        /// <summary>Anything not covered above, including permanent token or request rejections.</summary>
        Unknown
    }

    /// <summary>
    /// Structured, payload-safe description of one Telegram polling failure.
    /// </summary>
    /// <param name="Kind">Closed-vocabulary failure kind decided by <see cref="TelegramPollingFailureDiagnostics" />.</param>
    /// <param name="ExceptionType">Runtime type name of the observed exception, for example <c>RequestException</c>.</param>
    /// <param name="InnerExceptionType">
    /// Runtime type name of the innermost inner exception when the failure was wrapped, for example
    /// <c>SocketException</c>; empty when the chain has no inner exception.
    /// </param>
    /// <param name="StatusCode">
    /// Telegram API error code when the exception carried one, otherwise the HTTP status code when the transport
    /// reported one, otherwise <c>null</c>. This is the value that distinguishes a 409 conflict from a 502 gateway
    /// failure, and it is what was missing from the original operational log line.
    /// </param>
    /// <param name="ResponseText">
    /// Sanitized and length-capped response text taken from the exception chain. Bot tokens and control characters are
    /// removed before this value is stored, so it is safe to log and never contains a credential.
    /// </param>
    /// <remarks>
    /// A record is used so callers can log individual fields as structured properties instead of formatting one opaque
    /// string. Instances are immutable and hold no live exception reference, so nothing here keeps a failed HTTP
    /// connection or its response buffer alive.
    /// </remarks>
    public sealed record TelegramPollingFailure(
        TelegramPollingFailureKind Kind,
        string ExceptionType,
        string InnerExceptionType,
        int? StatusCode,
        string ResponseText)
    {
        /// <summary>Whether this failure is a Telegram polling conflict of either documented shape.</summary>
        /// <remarks>
        /// Both conflict kinds stop or repair the affected receiver, so callers that only need that decision use this
        /// property instead of comparing the enum themselves.
        /// </remarks>
        public bool IsConflict => Kind is TelegramPollingFailureKind.GetUpdatesConflict
            or TelegramPollingFailureKind.WebhookConflict;
    }

    /// <summary>
    /// Classifies one Telegram polling exception and produces the redacted diagnostic fields an operator needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this type exists:
    /// conflict detection previously required a typed <see cref="ApiRequestException" /> with error code 409. A 409 that
    /// arrived as a plain <see cref="RequestException" /> - which happens when the edge response body is not parseable
    /// Telegram JSON - silently escaped both conflict classifiers and reached the generic polling logger, so a duplicate
    /// poller was never reported as a duplicate poller. The classifier below treats the HTTP status and the documented
    /// conflict text as equally authoritative, and it is the single place that decides the failure kind so the receiver
    /// and the shared backoff policy cannot disagree.
    /// </para>
    /// <para>
    /// Privacy:
    /// no field can carry a bot token, chat id, or user id. Telegram's own conflict descriptions contain only the method
    /// name, and any token-shaped substring is replaced before the text leaves this type.
    /// </para>
    /// </remarks>
    public static class TelegramPollingFailureDiagnostics
    {
        /// <summary>Maximum number of characters of response text retained for diagnostics.</summary>
        private const int MaximumResponseTextLength = 400;

        /// <summary>Maximum depth of the exception chain inspected while collecting description text.</summary>
        private const int MaximumChainDepth = 6;

        /// <summary>
        /// Matches a BotFather token shape (<c>123456789:AA...</c>) so it can be removed from any diagnostic text.
        /// </summary>
        /// <remarks>
        /// The boundaries are explicit lookarounds rather than <c>\b</c> because the most common leak shape is a token
        /// concatenated directly onto a letter, as in the Telegram API URL path <c>/bot123456789:AA.../getUpdates</c>. A
        /// word boundary does not exist between <c>t</c> and <c>1</c>, so the earlier pattern silently failed to redact
        /// exactly the shape that appears in a wrapped transport exception. The leading check only rejects a preceding
        /// digit, which keeps the match from starting inside a longer number while still allowing a letter prefix, and the
        /// trailing check keeps it from ending inside a longer word.
        /// </remarks>
        private static readonly Regex TokenShape = new(
            @"(?<![0-9])\d{5,12}:[A-Za-z0-9_\-]{10,64}(?![0-9A-Za-z_\-])",
            RegexOptions.Compiled);

        /// <summary>Classifies one polling exception into the closed-vocabulary kind and diagnostics payload.</summary>
        /// <param name="exception">
        /// Exception reported by the Telegram polling receiver or its error handler. A null value is reported as
        /// <see cref="TelegramPollingFailureKind.Unknown" /> rather than throwing, because this method runs inside error
        /// handling where throwing would lose the original failure.
        /// </param>
        /// <returns>
        /// A description whose <see cref="TelegramPollingFailure.Kind" /> never defaults to a retryable decision for a
        /// permanent rejection, and whose <see cref="TelegramPollingFailure.ResponseText" /> is always safe to log.
        /// </returns>
        /// <remarks>
        /// Classification order is deliberate: webhook and duplicate-poller conflicts are permanent and authoritative,
        /// rate limits have their own delay policy, delivery rejections are per user, and only then is the shared
        /// transient classifier consulted. A conflict can therefore never be downgraded to a transient retry.
        /// </remarks>
        /// <example>
        /// <code>
        /// var failure = TelegramPollingFailureDiagnostics.Describe(exception);
        /// logger.LogWarning("Telegram polling degraded. botId={BotId} failureKind={FailureKind} statusCode={StatusCode}",
        ///     botId, failure.Kind, failure.StatusCode);
        /// </code>
        /// </example>
        public static TelegramPollingFailure Describe(Exception exception)
        {
            if (exception == null)
                return new TelegramPollingFailure(TelegramPollingFailureKind.Unknown, "none", string.Empty, null, string.Empty);

            var statusCode = ResolveStatusCode(exception);
            var responseText = CollectResponseText(exception);
            var kind = ResolveKind(exception, statusCode, responseText);

            return new TelegramPollingFailure(
                kind,
                exception.GetType().Name,
                ResolveInnermost(exception)?.GetType().Name ?? string.Empty,
                statusCode,
                responseText);
        }

        /// <summary>
        /// Reports whether the failure means another <c>getUpdates</c> poller owns this bot token.
        /// </summary>
        /// <param name="exception">Polling exception to inspect; null returns <c>false</c>.</param>
        /// <returns><c>true</c> for the duplicate-poller conflict only, never for the separately recoverable webhook case.</returns>
        /// <remarks>
        /// A plain <see cref="RequestException" /> carrying status 409 and Telegram's documented conflict text is
        /// recognized here, because that is the production shape that previously escaped detection.
        /// </remarks>
        public static bool IsGetUpdatesConflict(Exception exception)
        {
            if (exception == null)
                return false;

            var message = CollectResponseText(exception);
            return !IsWebhookText(message) && IsGetUpdatesText(message, ResolveStatusCode(exception));
        }

        /// <summary>
        /// Reports whether Telegram refused polling because a webhook is configured for this bot token.
        /// </summary>
        /// <param name="exception">Polling exception to inspect; null returns <c>false</c>.</param>
        /// <returns><c>true</c> only for the webhook-versus-long-polling conflict.</returns>
        /// <remarks>
        /// Only this conflict is safe to recover automatically by deleting the webhook, so it is evaluated before the
        /// duplicate-poller classification.
        /// </remarks>
        public static bool IsWebhookConflict(Exception exception)
            => exception != null && IsWebhookText(CollectResponseText(exception));

        /// <summary>Reports whether response text names the webhook-versus-long-polling conflict.</summary>
        /// <param name="message">Sanitized diagnostic text; null or empty returns <c>false</c>.</param>
        /// <returns><c>true</c> only when Telegram said a webhook is active for this token.</returns>
        /// <remarks>Text-based so it can be evaluated with an already-collected body without re-walking the chain.</remarks>
        private static bool IsWebhookText(string message)
            => !string.IsNullOrEmpty(message) &&
               (message.Contains("webhook is active", StringComparison.OrdinalIgnoreCase)
                || message.Contains("use deleteWebhook", StringComparison.OrdinalIgnoreCase)
                || message.Contains("can't use getUpdates method while webhook", StringComparison.OrdinalIgnoreCase)
                || message.Contains("can not use getUpdates method while webhook", StringComparison.OrdinalIgnoreCase));

        /// <summary>Reports whether response text and status name a duplicate <c>getUpdates</c> poller.</summary>
        /// <param name="message">Sanitized diagnostic text collected from the exception chain.</param>
        /// <param name="statusCode">Resolved Telegram error code or HTTP status code, when one was present.</param>
        /// <returns><c>true</c> for Telegram's duplicate-poller conflict only.</returns>
        /// <remarks>
        /// Telegram's documented conflict text is authoritative, and a bare 409 status with a conflict description is
        /// accepted as well because that is the shape the edge produces when the body is not parseable Telegram JSON.
        /// </remarks>
        private static bool IsGetUpdatesText(string message, int? statusCode)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            return message.Contains("terminated by other getUpdates request", StringComparison.OrdinalIgnoreCase)
                || message.Contains("terminated by other getUpdates", StringComparison.OrdinalIgnoreCase)
                || message.Contains("only one bot instance is running", StringComparison.OrdinalIgnoreCase)
                || (statusCode == 409
                    && message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                    && !message.Contains("webhook", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Decides the closed-vocabulary kind for one exception and its resolved status code.</summary>
        /// <param name="exception">Exception being classified; never null.</param>
        /// <param name="statusCode">Telegram error code or HTTP status code already resolved from the chain.</param>
        /// <returns>One <see cref="TelegramPollingFailureKind" /> value.</returns>
        private static TelegramPollingFailureKind ResolveKind(Exception exception, int? statusCode, string responseText)
        {
            if (IsWebhookText(responseText))
                return TelegramPollingFailureKind.WebhookConflict;

            if (IsGetUpdatesText(responseText, statusCode))
                return TelegramPollingFailureKind.GetUpdatesConflict;

            if (statusCode == 429 || TelegramRateLimitPolicy.IsRateLimited(exception))
                return TelegramPollingFailureKind.RateLimited;

            if (IsUserDeliveryFailure(responseText, statusCode))
                return TelegramPollingFailureKind.Delivery;

            if (TelegramPollingBackoffPolicy.IsTransientGatewayFailure(exception))
                return TelegramPollingFailureKind.Transient;

            return TelegramPollingFailureKind.Unknown;
        }

        /// <summary>Detects definitive per-user delivery rejections that must not affect the receiver.</summary>
        /// <param name="exception">Exception being classified.</param>
        /// <param name="statusCode">Resolved status code, used for the 403 shape.</param>
        /// <returns><c>true</c> for a blocked bot or an unreachable chat.</returns>
        private static bool IsUserDeliveryFailure(string responseText, int? statusCode)
        {
            var message = responseText ?? string.Empty;
            return statusCode == 403
                || message.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase)
                || message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase)
                || message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the most precise status available in the chain: Telegram's own error code first, then the HTTP status.
        /// </summary>
        /// <param name="exception">Exception chain to inspect.</param>
        /// <returns>The resolved status code, or <c>null</c> when the chain carries no status at all.</returns>
        /// <remarks>
        /// The Telegram API code is preferred because a gateway can wrap a 409 in a 502 envelope; the API code names the
        /// real rejection.
        /// </remarks>
        private static int? ResolveStatusCode(Exception exception)
        {
            var current = exception;
            for (var depth = 0; current != null && depth < MaximumChainDepth; depth++, current = current.InnerException)
            {
                if (current is ApiRequestException apiException && apiException.ErrorCode != 0)
                    return apiException.ErrorCode;

                if (current is RequestException requestException && requestException.HttpStatusCode is { } status)
                    return (int)status;
            }

            return null;
        }

        /// <summary>Finds the wrapped transport cause of the chain so it can be named separately from the outer wrapper.</summary>
        /// <param name="exception">Exception chain to walk.</param>
        /// <returns>
        /// The deepest inner exception found within the bounded depth, or <c>null</c> when the chain has no inner
        /// exception at all.
        /// </returns>
        /// <remarks>
        /// Returning <c>null</c> for a single-element chain matters for the logged field: reporting the outer type as its
        /// own "inner exception" would make a genuinely unwrapped failure look like a wrapped one and hide the distinction
        /// an operator is reading the field for.
        /// </remarks>
        private static Exception ResolveInnermost(Exception exception)
        {
            var current = exception?.InnerException;
            for (var depth = 0; current?.InnerException != null && depth < MaximumChainDepth; depth++)
                current = current.InnerException;

            return current;
        }

        /// <summary>
        /// Collects the exception text chain into one sanitized, length-capped diagnostic string.
        /// </summary>
        /// <param name="exception">Exception chain whose messages describe the failure.</param>
        /// <returns>
        /// A single line containing the outer message followed by inner messages, with control characters collapsed,
        /// token-shaped substrings redacted, and the total length capped.
        /// </returns>
        /// <remarks>
        /// Telegram's conflict and rate-limit descriptions arrive inside the exception message, which is why this value is
        /// logged as the response text. It is deliberately flattened to one line so a multi-line provider payload cannot
        /// forge additional log records.
        /// </remarks>
        private static string CollectResponseText(Exception exception)
        {
            var builder = new StringBuilder();
            var current = exception;
            for (var depth = 0; current != null && depth < MaximumChainDepth; depth++, current = current.InnerException)
            {
                if (string.IsNullOrWhiteSpace(current.Message))
                    continue;

                if (builder.Length > 0)
                    builder.Append(" | ");

                builder.Append(current.Message);
                if (builder.Length >= MaximumResponseTextLength)
                    break;
            }

            return Sanitize(builder.ToString());
        }

        /// <summary>Removes credentials and control characters from diagnostic text and caps its length.</summary>
        /// <param name="text">Raw exception text that may contain a token or multi-line payload.</param>
        /// <returns>A single-line, token-free, length-capped string; never null.</returns>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var replaced = TokenShape.Replace(text, "<redacted-token>");
            var collapsed = new StringBuilder(replaced.Length);
            foreach (var character in replaced)
            {
                // Control characters are replaced so a provider payload cannot inject log line breaks or terminal escapes.
                collapsed.Append(char.IsControl(character) ? ' ' : character);
            }

            var result = collapsed.ToString().Trim();
            return result.Length <= MaximumResponseTextLength
                ? result
                : result[..MaximumResponseTextLength] + "...";
        }

        /// <summary>Formats a resolved status code for fixed-width diagnostics.</summary>
        /// <param name="statusCode">Resolved status code, or null when the failure carried none.</param>
        /// <returns>The numeric code, or <c>none</c> so a log field is never empty.</returns>
        /// <remarks>Exists so logging call sites do not repeat the null check and cannot print an empty field.</remarks>
        public static string FormatStatusCode(int? statusCode) => statusCode?.ToString(CultureInfo.InvariantCulture) ?? "none";

        /// <summary>Enumerates the vocabulary so a test or diagnostic screen can assert the closed set.</summary>
        /// <returns>Every <see cref="TelegramPollingFailureKind" /> value in declaration order.</returns>
        public static IReadOnlyList<TelegramPollingFailureKind> AllKinds { get; } = (TelegramPollingFailureKind[])
            Enum.GetValues(typeof(TelegramPollingFailureKind));
    }
}
