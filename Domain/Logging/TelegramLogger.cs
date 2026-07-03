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


        public TelegramLogger(
            string categoryName,
            Func<string, LogLevel, bool> filter,
            BotClientProvider botClientProvider,
            BotRegistry botRegistry,
            BotContextAccessor botContextAccessor,
            string fallbackChannelId,
            string fallbackBackupChannelId)
        {
            _categoryName = categoryName;
            _filter = filter;
            _botClientProvider = botClientProvider;
            _botRegistry = botRegistry;
            _botContextAccessor = botContextAccessor;
            _fallbackChannelId = fallbackChannelId;
            _fallbackBackupChannelId = fallbackBackupChannelId;
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
            if (eventId.Id == 1000 && eventId.Name == "Payment")
            {
                _ = Task.Run(() => LogPayment(message));
                // BackupDatabas().Wait();
            }
            else
            {
                _ = Task.Run(() => SendMessageToChannelAsync(message));
                //BackupDatabas().Wait();

            }

        }


        /// <summary>
        /// Creates and sends a best-effort copy of <c>credentials.db</c> to the configured backup channel.
        /// </summary>
        /// <returns>A task that completes after the backup is sent or skipped because the file could not be copied.</returns>
        /// <remarks>
        /// SQLite keeps the credentials database open while the bot is running. The backup copy therefore opens
        /// the source file with read/write sharing and returns immediately when copying fails; logging must never
        /// block payment processing or crash the Telegram receiver.
        /// </remarks>
        private async Task BackupDatabas()
        {

            string sourceDbPath = "./Data/credentials.db";
            string backupDbPath = "./Data/credentials_backup.db";

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
                Console.WriteLine("An error occurred while copying the database: " + ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine("An unexpected error occurred while copying the database: " + ex.Message);
                return;
            }

            try
            {
                await using Stream stream = System.IO.File.OpenRead("./Data/credentials_backup.db");
                await CurrentBotClient.SendDocumentAsync(
                    chatId: CurrentBackupChannelId,
                    document: InputFile.FromStream(stream: stream, fileName: "credentials.db"),
                    caption: DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi());
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred while sending the database backup: " + ex.Message);
            }
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

                _ = Task.Run(BackupDatabas);
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
