using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace Adminbot.Domain.Logging
{

    public class TelegramLoggerProvider : ILoggerProvider
    {
        private readonly Func<string, LogLevel, bool> _filter;
        private readonly ITelegramBotClient _botClient;
        private readonly string _channelId;

        public TelegramLoggerProvider(Func<string, LogLevel, bool> filter, ITelegramBotClient botClient, string channelId)
        {
            _filter = filter;
            _botClient = botClient;
            _channelId = channelId;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TelegramLogger(categoryName, _filter, _botClient, _channelId);
        }

        public void Dispose()
        {
            // Clean up here if needed
        }
    }

}