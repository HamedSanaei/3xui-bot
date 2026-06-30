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


        private async Task BackupDatabas()
        {

            string sourceDbPath = "./Data/credentials.db";
            string backupDbPath = "./Data/credentials_backup.db";

            try
            {
                // This will create a copy of your database file.
                System.IO.File.Copy(sourceDbPath, backupDbPath, overwrite: true);

                // Now you can send the backup file to Telegram as needed.
                // ... (code to send the file) ...
            }
            catch (IOException ex)
            {
                // Handle the case where the file could not be copied.
                Console.WriteLine("An error occurred while copying the database: " + ex.Message);
                // You may choose to log the error or inform the user.
            }


            await using Stream stream = System.IO.File.OpenRead("./Data/credentials_backup.db");
            Message message = await CurrentBotClient.SendDocumentAsync(
                chatId: CurrentBackupChannelId,
                document: InputFile.FromStream(stream: stream, fileName: "credentials.db"),
                caption: DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi());
        }

        public async Task LogPayment(string message)
        {
            try
            {
                // Escape HTML-sensitive characters for proper rendering in HTML mode
                ;

                // Send message using ParseMode.Html
                await CurrentBotClient.SendTextMessageAsync(
                    CurrentLoggerChannelId,
                    message,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
                );

                BackupDatabas().Wait();
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
                await CurrentBotClient.SendTextMessageAsync(CurrentLoggerChannelId, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception caught in logger: {ex.Message}");
                // You might want to log to a local file here as a fallback
            }
        }

        private ITelegramBotClient CurrentBotClient => _botClientProvider.GetClient(CurrentBotConfig?.Id);

        private BotInstanceConfig CurrentBotConfig => _botContextAccessor.Current?.Config ?? _botRegistry.DefaultBot;

        private string CurrentLoggerChannelId => string.IsNullOrWhiteSpace(CurrentBotConfig?.LoggerChannel)
            ? _fallbackChannelId
            : CurrentBotConfig.LoggerChannel;

        private string CurrentBackupChannelId => string.IsNullOrWhiteSpace(CurrentBotConfig?.BackupChannel)
            ? _fallbackBackupChannelId
            : CurrentBotConfig.BackupChannel;

    }
}
