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
        /// <remarks>Use this only for financial events whose normal audit policy includes database backups.</remarks>
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
        /// Unlike <see cref="LogPayment"/>, this event does not send database backup documents. It exists for account,
        /// admin, and operational audits where plain-text logging would expose markup characters instead of entities.
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
