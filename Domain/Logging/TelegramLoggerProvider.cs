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
        private readonly string _backupChannelId;

        public TelegramLoggerProvider(Func<string, LogLevel, bool> filter, ITelegramBotClient botClient, string channelId, string backupChannelId)
        {
            _filter = filter;
            _botClient = botClient;
            _channelId = channelId;
            _backupChannelId = backupChannelId;

        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TelegramLogger(categoryName, _filter, _botClient, _channelId, _backupChannelId);
        }

        public void Dispose()
        {
            // Clean up here if needed
        }
    }

}