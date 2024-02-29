using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System;
using Telegram.Bot;
using Telegram.Bot.Types;
using Adminbot.Utils;




namespace Adminbot.Domain.Logging
{

    public class TelegramLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly Func<string, LogLevel, bool> _filter;
        private readonly ITelegramBotClient _botClient;
        private readonly string _channelId;
        private readonly string _backupChannelId;


        public TelegramLogger(string categoryName, Func<string, LogLevel, bool> filter, ITelegramBotClient botClient, string channelId, string backupChannelId)
        {
            _categoryName = categoryName;
            _filter = filter;
            _botClient = botClient;
            _channelId = channelId;
            _backupChannelId = backupChannelId;
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
            SendMessageToChannelAsync(message).Wait();
            BackupDatabas().Wait();


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
            Message message = await _botClient.SendDocumentAsync(
                chatId: _backupChannelId,
                document: InputFile.FromStream(stream: stream, fileName: "credentials.db"),
                caption: DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi());
        }

        private async Task SendMessageToChannelAsync(string message)
        {
            try
            {
                await _botClient.SendTextMessageAsync(_channelId, message, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception caught in logger: {ex.Message}");
                // You might want to log to a local file here as a fallback
            }
        }

    }
}