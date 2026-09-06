using Microsoft.Extensions.Logging;
namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Provides structured logger events whose Telegram delivery semantics differ from ordinary plain-text logs.
    /// </summary>
    /// <remarks>
    /// Payment events use Telegram HTML and trigger fail-soft database backups. HTML audit events use the same private
    /// logger channel and parsing mode without starting a financial backup. Ordinary information messages remain plain text.
    /// </remarks>
    public static class LoggerExtensions
    {
        /// <summary>
        /// Records a financial payment event as Telegram HTML and starts the logger's fail-soft database backup path.
        /// </summary>
        /// <param name="logger">Microsoft logger configured with the Telegram provider; it must not be null.</param>
        /// <param name="message">
        /// HTML-safe payment text. Every external or user-controlled value must be encoded before inclusion.
        /// </param>
        /// <remarks>
        /// This is the only logger event that requests a database backup: the durable outbox increments the global
        /// backup generation atomically with the Payment insertion. Use it only for financial events (settlement,
        /// wallet credit/debit/refund, purchase, renewal, referral reward, admin wallet adjustment) and never for
        /// operational audits such as phone verification, role changes, colleague requests, link changes, or account
        /// deletion, which must use <see cref="LogTelegramHtml"/> instead.
        /// </remarks>
        public static void LogPayment(this ILogger logger, string message)
        {
            logger.Log(LogLevel.Information, new EventId(1000, "Payment"), message, null, (msg, ex) => msg);
        }

        /// <summary>
        /// Records a non-financial operational audit that requires Telegram HTML formatting in the central logger channel.
        /// </summary>
        /// <param name="logger">Microsoft logger configured with the Telegram provider; it must not be null.</param>
        /// <param name="message">
        /// Bounded HTML-safe audit text. Callers must encode every dynamic value and may use only Telegram-supported
        /// HTML elements such as <c>code</c>, <c>b</c>, and <c>a</c>.
        /// </param>
        /// <remarks>
        /// Unlike <see cref="LogPayment"/>, this event never sends database backup documents: it commits a durable
        /// HTML row (EventId 1001/TelegramHtml) without touching the backup generation. It exists for account, admin,
        /// colleague, link-change, delete, and other operational audits where plain-text logging would expose markup
        /// characters instead of entities. The message is as durable as a payment log (SQLite outbox, retries, restart
        /// recovery); only the backup side effect differs.
        /// </remarks>
        /// <example>
        /// <code>
        /// logger.LogTelegramHtml("اکانت &lt;code&gt;example-user&lt;/code&gt; ساخته شد.");
        /// </code>
        /// </example>
        public static void LogTelegramHtml(this ILogger logger, string message)
        {
            logger.Log(LogLevel.Information, new EventId(1001, "TelegramHtml"), message, null, (msg, ex) => msg);
        }
    }

}
