using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System;
using Telegram.Bot;


namespace Adminbot.Domain.Logging
{

    public class TelegramLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly Func<string, LogLevel, bool> _filter;
        private readonly ITelegramBotClient _botClient;
        private readonly string _channelId;

        public TelegramLogger(string categoryName, Func<string, LogLevel, bool> filter, ITelegramBotClient botClient, string channelId)
        {
            _categoryName = categoryName;
            _filter = filter;
            _botClient = botClient;
            _channelId = channelId;
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