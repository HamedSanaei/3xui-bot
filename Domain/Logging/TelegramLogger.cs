using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System;
using Telegram.Bot;
using Telegram.Bot.Types;
using Adminbot.Utils;
using Adminbot.Domain;




namespace Adminbot.Domain.Logging
{

    public class TelegramLogger : ILogger
    {
        /// <summary>
        /// Maximum plain-text log length sent to Telegram, kept below Telegram's hard 4096-character limit.
        /// </summary>
        private const int MaxTelegramLogMessageLength = 3900;
        private readonly string _categoryName;
        private readonly Func<string, LogLevel, bool> _filter;
        private readonly BotClientProvider _botClientProvider;
        private readonly BotRegistry _botRegistry;
        private readonly BotContextAccessor _botContextAccessor;
        private readonly string _fallbackChannelId;
        private readonly string _fallbackBackupChannelId;
        private readonly AppConfig _appConfig;

        /// <summary>
        /// Creates a Telegram-backed logger that can post operational logs and database backups.
        /// </summary>
        /// <param name="categoryName">Logger category name supplied by Microsoft.Extensions.Logging.</param>
        /// <param name="filter">Provider-level filter that decides whether a log level/category should be sent.</param>
        /// <param name="botClientProvider">Provider used to resolve the current/default Telegram bot client.</param>
        /// <param name="botRegistry">Runtime registry used to resolve logger and backup channels per bot context.</param>
        /// <param name="botContextAccessor">Async-local bot context accessor for owned/tenant logging routes.</param>
        /// <param name="fallbackChannelId">Fallback private logger channel id from legacy configuration.</param>
        /// <param name="fallbackBackupChannelId">Fallback backup channel id used when the current bot has no backup channel.</param>
        /// <param name="appConfig">Application configuration containing the resolved database paths to back up.</param>
        /// <remarks>
        /// Payment logs are routed to the logger channel while database documents are routed to the backup channel.
        /// Both operations are best-effort and must never fail payment settlement or Telegram update handling.
        /// </remarks>
        public TelegramLogger(
            string categoryName,
            Func<string, LogLevel, bool> filter,
            BotClientProvider botClientProvider,
            BotRegistry botRegistry,
            BotContextAccessor botContextAccessor,
            string fallbackChannelId,
            string fallbackBackupChannelId,
            AppConfig appConfig)
        {
            _categoryName = categoryName;
            _filter = filter;
            _botClientProvider = botClientProvider;
            _botRegistry = botRegistry;
            _botContextAccessor = botContextAccessor;
            _fallbackChannelId = fallbackChannelId;
            _fallbackBackupChannelId = fallbackBackupChannelId;
            _appConfig = appConfig ?? new AppConfig();
        }

        public IDisposable BeginScope<TState>(TState state) => default;

        public bool IsEnabled(LogLevel logLevel) => (_filter == null || _filter(_categoryName, logLevel));

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

            if (eventId.Id == 1000 && eventId.Name == "Payment")
            {
                _ = Task.Run(() => LogPayment(message));
            }
            else
            {
                _ = Task.Run(() => SendMessageToChannelAsync(message));
            }

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
        /// repeated tenant forced-join probes, and Telegram polling 5xx/429/timeouts. Business failures such as
        /// invalid tokens, duplicate tokens, XUI delivery failures, and payment settlement errors are not suppressed.
        /// </remarks>
        private bool ShouldSuppressChannelDelivery(string message, Exception exception)
        {
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
                ContainsOrdinalIgnoreCase(combined, "tenant forced-join check failed"))
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


        /// <summary>
        /// Creates and sends best-effort copies of all configured runtime databases to the backup channel.
        /// </summary>
        /// <returns>A task that completes after each configured database has been copied and sent or skipped.</returns>
        /// <remarks>
        /// SQLite keeps both databases open while the bot is running. Each backup is copied with read/write/delete
        /// sharing into a temporary file before upload. Failures are isolated per database so a locked
        /// <c>credentials.db</c> does not prevent <c>users.db</c> from being sent, and backup failures never block
        /// payment settlement or crash a Telegram receiver.
        /// </remarks>
        private async Task BackupDatabasesAsync()
        {
            foreach (var database in GetDatabaseBackupTargets())
                await BackupDatabaseAsync(database.SourcePath, database.TempPath, database.FileName);
        }

        /// <summary>
        /// Copies one SQLite database and sends it to the configured backup channel.
        /// </summary>
        /// <param name="sourceDbPath">Source database path resolved from configuration.</param>
        /// <param name="backupDbPath">Temporary backup path used only for Telegram upload.</param>
        /// <param name="fileName">Document file name shown in Telegram, such as <c>users.db</c>.</param>
        /// <returns>A task that completes after the copy/send attempt finishes.</returns>
        /// <remarks>
        /// The method is fail-soft by design. It logs copy or Telegram upload failures to console and returns so
        /// the payment log path can continue without surfacing database lock errors to customers or admins.
        /// </remarks>
        private async Task BackupDatabaseAsync(string sourceDbPath, string backupDbPath, string fileName)
        {
            try
            {
                await using var source = new System.IO.FileStream(
                    sourceDbPath,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                await using var destination = new System.IO.FileStream(
                    backupDbPath,
                    System.IO.FileMode.Create,
                    System.IO.FileAccess.Write,
                    System.IO.FileShare.None);
                await source.CopyToAsync(destination);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"An error occurred while copying {fileName}: {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred while copying {fileName}: {ex.Message}");
                return;
            }

            try
            {
                await using Stream stream = System.IO.File.OpenRead(backupDbPath);
                await CurrentBotClient.SendDocumentAsync(
                    chatId: CurrentBackupChannelId,
                    document: InputFile.FromStream(stream: stream, fileName: fileName),
                    caption: $"{fileName} - {DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while sending {fileName} backup: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the database files that should be sent after financial logs.
        /// </summary>
        /// <returns>
        /// Backup targets for <c>credentials.db</c> and <c>users.db</c>, using configured paths when available.
        /// </returns>
        /// <remarks>
        /// The backup channel receives both the shared credentials wallet/profile database and the users runtime
        /// database because payments, tenant orders, invoices, states, and ledgers now live in <c>users.db</c>.
        /// </remarks>
        private IEnumerable<(string SourcePath, string TempPath, string FileName)> GetDatabaseBackupTargets()
        {
            yield return (
                string.IsNullOrWhiteSpace(_appConfig.CredentialsDatabasePath) ? "./Data/credentials.db" : _appConfig.CredentialsDatabasePath,
                "./Data/credentials_backup.db",
                "credentials.db");
            yield return (
                string.IsNullOrWhiteSpace(_appConfig.UserDatabasePath) ? "./Data/users.db" : _appConfig.UserDatabasePath,
                "./Data/users_backup.db",
                "users.db");
        }

        /// <summary>
        /// Sends an HTML payment/audit log to the selected operational logger channel and starts a non-blocking database backup.
        /// </summary>
        /// <param name="message">
        /// HTML-safe log text prepared by the payment or tenant flow. The method sends it with
        /// <see cref="Telegram.Bot.Types.Enums.ParseMode.Html"/>.
        /// </param>
        /// <returns>A task that completes after the log message has been sent.</returns>
        /// <remarks>
        /// The credentials backup is intentionally fire-and-forget. A locked database file, missing backup
        /// channel, or Telegram document failure must not delay or fail the payment settlement path. Logs from
        /// non-default owned bots and tenant storefronts are routed through the default owned bot because only that
        /// bot is guaranteed to post to the private central logger channel.
        /// </remarks>
        public async Task LogPayment(string message)
        {
            try
            {
                await CurrentBotClient.SendTextMessageAsync(
                    CurrentLoggerChannelId,
                    message,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
                );

                _ = Task.Run(BackupDatabasesAsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in LogPayment: {ex.Message}");
            }
        }

        private async Task SendMessageToChannelAsync(string message)
        {
            try
            {
                await CurrentBotClient.SendTextMessageAsync(CurrentLoggerChannelId, TruncateForTelegramLog(message));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception caught in logger: {ex.Message}");
                // You might want to log to a local file here as a fallback
            }
        }

        /// <summary>
        /// Truncates a plain-text application log so Telegram accepts it as one message.
        /// </summary>
        /// <param name="message">
        /// Plain-text log message generated by Microsoft.Extensions.Logging. The value may be empty or may include
        /// a full exception stack trace.
        /// </param>
        /// <returns>
        /// The original message when it fits Telegram's message size limit; otherwise a shortened message with a
        /// marker that tells admins the stack was truncated.
        /// </returns>
        /// <remarks>
        /// Telegram rejects text messages above its size limit with <c>message is too long</c>. Logger failures must
        /// never create a second noisy exception while the bot is already handling another failure.
        /// </remarks>
        private static string TruncateForTelegramLog(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= MaxTelegramLogMessageLength)
                return message ?? string.Empty;

            return message[..MaxTelegramLogMessageLength] + "\n...[log truncated for Telegram]";
        }

        /// <summary>
        /// Gets the Telegram client that is allowed to post operational logs.
        /// </summary>
        /// <remarks>
        /// Non-default owned bots and tenant storefront bots are not guaranteed to be members of the private central
        /// operational log channel. The default owned bot sends every operational log so successful purchases from any
        /// brand or tenant storefront reach the same private channel.
        /// </remarks>
        private ITelegramBotClient CurrentBotClient => _botClientProvider.GetClient(CurrentLoggingBotConfig?.Id);

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
