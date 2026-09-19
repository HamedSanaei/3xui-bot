using System;
using System.Globalization;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Identifies how a configured Telegram logger/backup destination is addressed.
    /// </summary>
    /// <remarks>
    /// The value describes the accepted wire form only. It never carries the destination text, an internal bot id,
    /// or a bot token, so it stays safe to write into diagnostics, metrics and outbox failure rows.
    /// </remarks>
    public enum TelegramDestinationKind
    {
        /// <summary>No usable destination exists; nothing may be sent and no durable row should be created.</summary>
        None,

        /// <summary>
        /// Numeric Telegram chat id, including negative supergroup and channel ids such as <c>-1001234567890</c>.
        /// </summary>
        ChatId,

        /// <summary>
        /// Public channel or group username written with a leading <c>@</c>. Telegram does not accept a bare
        /// username without <c>@</c>, so a bare word is deliberately rejected instead of being repaired.
        /// </summary>
        Username
    }

    /// <summary>
    /// Raised when a durable Telegram log row carries a destination that can never be delivered.
    /// </summary>
    /// <remarks>
    /// This type exists so an invalid-destination failure is classified as a deterministic configuration error
    /// instead of a transient delivery error that retries forever. Every other <see cref="ArgumentException"/> keeps
    /// its normal meaning, because an <see cref="ArgumentException"/> raised elsewhere may be a programming bug and
    /// must never be hidden behind a permanent Telegram delivery classification.
    ///
    /// The exception message is the stable, non-secret <see cref="FailureCode"/> only, so persisting
    /// <see cref="Exception.Message"/> onto a dead-lettered outbox row can never store arbitrary configured text.
    /// The parse reason is available separately through <see cref="Reason"/> and comes from a closed vocabulary.
    /// </remarks>
    /// <example>
    /// <code>
    /// try
    /// {
    ///     await sender.SendMessage(row.LoggerChannelId, row.Message, ParseMode.Html, cancellationToken);
    /// }
    /// catch (TelegramDestinationInvalidException ex)
    /// {
    ///     await outbox.FailAsync(row.Id, now, ex.FailureCode, now, deadLetter: true);
    /// }
    /// </code>
    /// </example>
    public sealed class TelegramDestinationInvalidException : Exception
    {
        /// <summary>
        /// Stable sanitized failure code persisted on the outbox row, for example as its <c>LastError</c> value.
        /// </summary>
        public const string FailureCode = "invalid_logger_destination";

        /// <summary>
        /// Creates the exception for a destination that failed local validation.
        /// </summary>
        /// <param name="reason">
        /// Non-secret closed-vocabulary reason produced by <see cref="TelegramDestination"/>:
        /// <see cref="TelegramDestination.ReasonBlank"/>, <see cref="TelegramDestination.ReasonZero"/>, or
        /// <see cref="TelegramDestination.ReasonMalformed"/>. Pass an empty string when no reason is available.
        /// Never pass the rejected destination text or any credential.
        /// </param>
        public TelegramDestinationInvalidException(string reason = "")
            : base(FailureCode)
        {
            Reason = reason ?? string.Empty;
        }

        /// <summary>
        /// Gets the closed-vocabulary reason explaining why the destination was rejected.
        /// </summary>
        /// <remarks>
        /// Safe for logs, metrics and dead-letter error text: it never contains the rejected destination text,
        /// a chat id, a bot id, or a bot token.
        /// </remarks>
        public string Reason { get; }
    }

    /// <summary>
    /// Single shared parser and validator for every configured Telegram log destination.
    /// </summary>
    /// <remarks>
    /// Both the Telegram logger producer and the durable outbox dispatcher use this type, so a destination accepted
    /// once is accepted everywhere and a destination rejected here is never handed to <c>Telegram.Bot</c>.
    ///
    /// Accepted forms are exactly the two forms the Telegram client itself understands:
    /// <list type="bullet">
    /// <item><description>a numeric chat id, including negative supergroup/channel ids produced by Telegram;</description></item>
    /// <item><description>a public channel or group username starting with <c>@</c>.</description></item>
    /// </list>
    ///
    /// Blank text and the numeric zero sentinel are reported as missing rather than invalid, because
    /// <see cref="AppConfig.BackupChannel"/> historically defaulted to <c>0</c> for "not configured".
    /// Arbitrary text is never repaired: this method deliberately does not prepend <c>@</c> to a bare word, because
    /// inventing a destination would silently send operational logs to a channel the operator never configured.
    ///
    /// Validation never performs any network call and never reads or writes a bot token, so it is safe to run at
    /// startup, on the logging hot path, and inside tests.
    /// </remarks>
    public static class TelegramDestination
    {
        /// <summary>Reason code for missing, empty, or whitespace-only configuration.</summary>
        public const string ReasonBlank = "blank";

        /// <summary>Reason code for the numeric zero sentinel, which is never a real Telegram chat id.</summary>
        public const string ReasonZero = "zero";

        /// <summary>Reason code for text that is neither a numeric chat id nor an <c>@username</c>.</summary>
        public const string ReasonMalformed = "malformed";

        /// <summary>Shortest accepted Telegram public username length.</summary>
        private const int MinimumUsernameLength = 5;

        /// <summary>Longest accepted Telegram public username length.</summary>
        private const int MaximumUsernameLength = 32;

        /// <summary>
        /// Validates one configured destination and returns the canonical text that must be given to Telegram.
        /// </summary>
        /// <param name="value">
        /// Raw destination text from configuration (<c>loggerChannel</c> / <c>backupChannel</c>), from a bot's
        /// <c>LoggerChannel</c> / <c>BackupChannel</c> field, or from a persisted outbox row. May be null, blank,
        /// a numeric chat id, or an <c>@username</c>. Never a bot token and never an internal bot id: a bot id such
        /// as <c>vpnetiranbot</c> is intentionally rejected here because it is not a Telegram destination.
        /// </param>
        /// <param name="normalized">
        /// Canonical destination to pass to Telegram when this method returns <c>true</c>. Whitespace is trimmed and a
        /// numeric id is reformatted invariantly (so <c>"00123"</c> becomes <c>"123"</c>). The value is empty when the
        /// method returns <c>false</c> and must not be sent.
        /// </param>
        /// <param name="failureReason">
        /// Closed-vocabulary reason when this method returns <c>false</c>: <see cref="ReasonBlank"/>,
        /// <see cref="ReasonZero"/>, or <see cref="ReasonMalformed"/>. Empty on success. Safe for logs and metrics.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="normalized"/> is a destination the Telegram client accepts; <c>false</c>
        /// when the value is missing or malformed, in which case the caller must not send and must not create a
        /// durable delivery row.
        /// </returns>
        /// <remarks>
        /// The numeric zero sentinel is reported as missing, not as malformed, so existing
        /// <c>BackupChannel = 0</c> configurations keep meaning "not configured" instead of producing an error.
        /// </remarks>
        /// <example>
        /// <code>
        /// if (!TelegramDestination.TryNormalize(bot.BackupChannel, out var channel, out _))
        ///     return; // no destination: leave the durable generation pending.
        ///
        /// await client.SendDocument(channel, fileName, content, cancellationToken);
        /// </code>
        /// </example>
        public static bool TryNormalize(string value, out string normalized, out string failureReason)
        {
            normalized = string.Empty;
            var candidate = value?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                failureReason = ReasonBlank;
                return false;
            }

            // A leading @ is the only accepted username form. Telegram rejects a bare word, so repairing
            // "mychannel" into "@mychannel" here would invent a destination the operator never configured.
            if (candidate[0] == '@')
            {
                if (!IsPlausibleUsername(candidate.AsSpan(1)))
                {
                    failureReason = ReasonMalformed;
                    return false;
                }

                normalized = candidate;
                failureReason = string.Empty;
                return true;
            }

            if (!long.TryParse(candidate, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId))
            {
                failureReason = ReasonMalformed;
                return false;
            }

            // Zero is never a real Telegram chat id; it is the historical "not configured" sentinel.
            if (chatId == 0L)
            {
                failureReason = ReasonZero;
                return false;
            }

            normalized = chatId.ToString(CultureInfo.InvariantCulture);
            failureReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Returns the canonical destination text when a value is deliverable, otherwise an empty string.
        /// </summary>
        /// <param name="value">Raw configured destination text; may be null, blank, zero, or malformed.</param>
        /// <returns>
        /// The trimmed/canonical destination, or an empty string when the value cannot be delivered. Callers must
        /// treat the empty result as "not configured".
        /// </returns>
        /// <remarks>
        /// Use this when a single value must be passed into code that expects either a valid destination or nothing.
        /// It never falls back to a second source: use <see cref="SelectValid"/> when precedence between a global key
        /// and a per-bot value matters.
        /// </remarks>
        /// <example>
        /// <code>
        /// var fallback = TelegramDestination.Sanitize(configuration["loggerChannel"]);
        /// </code>
        /// </example>
        public static string Sanitize(string value) => TryNormalize(value, out var normalized, out _) ? normalized : string.Empty;

        /// <summary>
        /// Returns the destination kind for diagnostics without exposing the destination text.
        /// </summary>
        /// <param name="value">Raw destination text; null and blank classify as <see cref="TelegramDestinationKind.None"/>.</param>
        /// <returns>
        /// <see cref="TelegramDestinationKind.ChatId"/> for an accepted numeric id,
        /// <see cref="TelegramDestinationKind.Username"/> for an accepted <c>@username</c>, otherwise
        /// <see cref="TelegramDestinationKind.None"/>.
        /// </returns>
        public static TelegramDestinationKind Classify(string value)
        {
            if (!TryNormalize(value, out var normalized, out _))
                return TelegramDestinationKind.None;

            return normalized[0] == '@' ? TelegramDestinationKind.Username : TelegramDestinationKind.ChatId;
        }

        /// <summary>
        /// Selects the first valid destination from a primary candidate and a fallback, in precedence order.
        /// </summary>
        /// <param name="configured">
        /// Higher-precedence value, normally the global <c>loggerChannel</c>/<c>backupChannel</c> configuration key.
        /// May be null or invalid, in which case the fallback is considered.
        /// </param>
        /// <param name="fallback">
        /// Lower-precedence value, normally the default owned bot's per-bot logger or backup channel. May be null.
        /// </param>
        /// <returns>
        /// The canonical text of the first valid candidate, or an empty string when neither candidate is a usable
        /// Telegram destination.
        /// </returns>
        /// <remarks>
        /// This helper validates instead of merely returning the first non-blank value, so a malformed global value
        /// can never shadow a correct per-bot value. Callers must treat the empty result as "logging is not
        /// configured" and must not create a durable delivery row.
        /// </remarks>
        /// <example>
        /// <code>
        /// var channel = TelegramDestination.SelectValid(globalLoggerChannel, defaultBot.LoggerChannel);
        /// </code>
        /// </example>
        public static string SelectValid(string configured, string fallback)
        {
            if (TryNormalize(configured, out var normalized, out _))
                return normalized;
            return TryNormalize(fallback, out normalized, out _) ? normalized : string.Empty;
        }

        /// <summary>
        /// Applies the stored-channel sentinel rule to a value read from configuration or from the database.
        /// </summary>
        /// <param name="stored">
        /// Persisted or configured channel text. Historically a missing backup channel was written as the string
        /// <c>"0"</c>, and an unset logger channel as an empty string.
        /// </param>
        /// <returns>
        /// An empty string when the value is blank or the exact zero sentinel; otherwise the trimmed original value
        /// unchanged.
        /// </returns>
        /// <remarks>
        /// This method is deliberately <em>not</em> a validator: it never rewrites or discards an unrecognised value,
        /// because doing so inside a configuration-to-database sync could silently destroy an operator's setting.
        /// Full validation happens at the point of use through <see cref="TryNormalize"/>.
        /// </remarks>
        public static string NormalizeStoredChannel(string stored)
        {
            var candidate = stored?.Trim();
            if (string.IsNullOrEmpty(candidate))
                return string.Empty;
            return candidate == "0" ? string.Empty : candidate;
        }

        /// <summary>
        /// Checks the character set and length rules Telegram applies to public usernames.
        /// </summary>
        /// <param name="username">Username text without the leading <c>@</c>.</param>
        /// <returns>
        /// <c>true</c> when the text is 5-32 characters long and contains only ASCII letters, digits, or underscores.
        /// </returns>
        private static bool IsPlausibleUsername(ReadOnlySpan<char> username)
        {
            if (username.Length < MinimumUsernameLength || username.Length > MaximumUsernameLength)
                return false;

            foreach (var character in username)
            {
                var allowed = (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '_';
                if (!allowed)
                    return false;
            }

            return true;
        }
    }
}
