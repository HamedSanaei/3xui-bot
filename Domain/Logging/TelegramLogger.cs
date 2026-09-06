using System;
using Microsoft.Extensions.Logging;
using Adminbot.Domain;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Routes Microsoft.Extensions.Logging events to the central Telegram logger channel with explicit plain-text,
    /// HTML audit, and payment/backup delivery modes.
    /// </summary>
    /// <remarks>
    /// The default owned bot performs channel delivery so tenant and secondary owned bots need no direct access to the
    /// private logger channel. Payment and HTML audit events are committed to the durable SQLite outbox before this
    /// method returns (crash-, outage-, and restart-safe), while ordinary plain-text events stay in a bounded
    /// best-effort memory queue. Logger failures are contained locally and must never fail the originating bot
    /// operation or payment settlement.
    /// </remarks>
    public class TelegramLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly Func<string, LogLevel, bool> _filter;
        private readonly BotRegistry _botRegistry;
        private readonly BotContextAccessor _botContextAccessor;
        private readonly string _fallbackChannelId;
        private readonly string _fallbackBackupChannelId;
        private readonly TelegramLogDispatcher _dispatcher;

        /// <summary>
        /// Creates a Telegram-backed logger that can post operational logs and request database backups.
        /// </summary>
        /// <param name="categoryName">Logger category name supplied by Microsoft.Extensions.Logging.</param>
        /// <param name="filter">Provider-level filter that decides whether a log level/category should be sent.</param>
        /// <param name="botRegistry">Runtime registry used to resolve logger and backup channels per bot context.</param>
        /// <param name="botContextAccessor">Async-local bot context accessor for owned/tenant logging routes.</param>
        /// <param name="fallbackChannelId">Fallback private logger channel id from legacy configuration.</param>
        /// <param name="fallbackBackupChannelId">Fallback backup channel id used when the current bot has no backup channel.</param>
        /// <param name="dispatcher">Shared durable outbox dispatcher; must not be null.</param>
        /// <remarks>
        /// Payment logs retain their logger destination; backup intents coalesce into the configured global destination.
        /// Both operations are best-effort at the Telegram layer and durable for Payment/Html at the outbox layer;
        /// they must never fail payment settlement or Telegram update handling.
        /// </remarks>
        internal TelegramLogger(
            string categoryName,
            Func<string, LogLevel, bool> filter,
            BotRegistry botRegistry,
            BotContextAccessor botContextAccessor,
            string fallbackChannelId,
            string fallbackBackupChannelId,
            TelegramLogDispatcher dispatcher)
        {
            _categoryName = categoryName;
            _filter = filter;
            _botRegistry = botRegistry;
            _botContextAccessor = botContextAccessor;
            _fallbackChannelId = fallbackChannelId;
            _fallbackBackupChannelId = fallbackBackupChannelId;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public IDisposable BeginScope<TState>(TState state) => default;

        public bool IsEnabled(LogLevel logLevel) => (_filter == null || _filter(_categoryName, logLevel));

        /// <summary>
        /// Formats one Microsoft logger event and dispatches it through the event-specific Telegram delivery path.
        /// </summary>
        /// <typeparam name="TState">Structured logger state type supplied by Microsoft.Extensions.Logging.</typeparam>
        /// <param name="logLevel">Severity evaluated against the configured category filter before delivery.</param>
        /// <param name="eventId">
        /// Event identity selecting payment HTML with backups (<c>1000/Payment</c>), operational HTML
        /// (<c>1001/TelegramHtml</c>), or ordinary plain text.
        /// </param>
        /// <param name="state">Structured event state passed to <paramref name="formatter"/>; it may be null.</param>
        /// <param name="exception">Optional exception used by channel-noise suppression and the formatter.</param>
        /// <param name="formatter">Required formatter that produces the final channel message from state and exception.</param>
        /// <remarks>
        /// Event 1000/Payment and 1001/TelegramHtml are committed to the durable SQLite outbox synchronously —
        /// the SQLite INSERT/COMMIT completes before this method returns — so the record survives process crash,
        /// systemctl restart, reboot, Telegram outage, and lost in-memory wake-ups. Delivery itself is asynchronous
        /// and serialized by <see cref="TelegramLogDispatcher"/>. All other events stay plain text in a bounded
        /// memory-only queue so arbitrary application logs cannot be interpreted as Telegram markup and cannot grow
        /// disk usage. A failed outbox commit is counted and printed to console/file only; it never crashes the
        /// caller and is never re-logged through Telegram.
        /// </remarks>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            // Telegram logging is best-effort. Never block or fail the bot update pipeline because
            // a log channel is missing, the logger bot cannot post, or Telegram rejects an entity.
            if (ShouldSuppressChannelDelivery(message, exception))
            {
                return;
            }

            var delivery = eventId.Id == 1000 && eventId.Name == "Payment"
                ? TelegramLogDeliveryKind.Payment
                : eventId.Id == 1001 && eventId.Name == "TelegramHtml"
                    ? TelegramLogDeliveryKind.Html
                    : TelegramLogDeliveryKind.Plain;

            if (delivery == TelegramLogDeliveryKind.Plain)
            {
                _dispatcher.EnqueueNormal(new TelegramLogItem(
                    delivery, message, CurrentLoggingBotConfig?.Id ?? string.Empty,
                    CurrentLoggerChannelId, CurrentBackupChannelId));
                return;
            }

            _dispatcher.EnqueueDurable(new TelegramLogItem(
                delivery, message, CurrentLoggingBotConfig?.Id ?? string.Empty,
                CurrentLoggerChannelId, CurrentBackupChannelId));
        }

        /// <summary>
        /// Determines whether a provider log should be kept out of the private Telegram logger channel.
        /// </summary>
        /// <param name="message">
        /// Formatted log message produced by the calling logger category. The value can contain only the summary
        /// text or the provider's compact error text.
        /// </param>
        /// <param name="exception">
        /// Optional exception supplied to the logger. Its message is inspected only for known transient Telegram
        /// polling noise and never sent to Telegram when suppression matches.
        /// </param>
        /// <returns>
        /// <c>true</c> when the message is operational noise that should stay in local logs only; otherwise
        /// <c>false</c> so payment, audit, token, XUI, and settlement failures still reach the private channel.
        /// </returns>
        /// <remarks>
        /// Category-level filtering cannot see the final message text, so this method performs a second
        /// message-level check inside the Telegram provider. It intentionally suppresses only known noisy patterns:
        /// stale callbacks, unchanged Telegram edits, receipt-photo relay failures that have a text fallback,
        /// repeated tenant forced-join probes, routine XUI v3 volume-reminder scan summaries, and Telegram polling
        /// 5xx/429/timeouts. Business failures such as invalid tokens, duplicate tokens, XUI scan/delivery failures,
        /// and payment settlement errors are not suppressed.
        ///
        /// A Telegram 429 exception is suppressed structurally before any message text is inspected: the failure
        /// being reported is Telegram rate limiting, so sending a Telegram notification about it would trigger
        /// another send under the same rate limit and amplify the storm.
        /// </remarks>
        private bool ShouldSuppressChannelDelivery(string message, Exception exception)
        {
            return TelegramLogSuppression.ShouldSuppress(message, exception);
        }

        /// <summary>
        /// Gets the bot whose update is currently being handled, or the default owned bot when no context exists.
        /// </summary>
        private BotInstanceConfig CurrentBotConfig => _botContextAccessor.Current?.Config ?? _botRegistry.DefaultBot;

        /// <summary>
        /// Gets the bot configuration that should be used for Telegram log delivery.
        /// </summary>
        /// <remarks>
        /// Operational logs are intentionally routed through the default owned bot because the central logger channel is
        /// managed by the project owner, not by each brand bot or colleague storefront bot.
        /// </remarks>
        private BotInstanceConfig CurrentLoggingBotConfig => _botRegistry.DefaultBot ?? CurrentBotConfig;

        /// <summary>
        /// Gets the logger channel id for the selected logging bot, falling back to the legacy configured channel.
        /// </summary>
        private string CurrentLoggerChannelId => string.IsNullOrWhiteSpace(CurrentLoggingBotConfig?.LoggerChannel)
            ? _fallbackChannelId
            : CurrentLoggingBotConfig.LoggerChannel;

        /// <summary>
        /// Gets the backup channel id for the selected logging bot, falling back to the legacy configured channel.
        /// </summary>
        private string CurrentBackupChannelId => string.IsNullOrWhiteSpace(CurrentLoggingBotConfig?.BackupChannel)
            ? _fallbackBackupChannelId
            : CurrentLoggingBotConfig.BackupChannel;
    }
}
