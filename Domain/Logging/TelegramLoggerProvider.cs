using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Adminbot.Domain;

namespace Adminbot.Domain.Logging
{

    /// <summary>
    /// Creates Telegram loggers and applies the final category-level forwarding guard for the Telegram log channel.
    /// </summary>
    /// <remarks>
    /// The application-level filter decides which app logs are interesting, but this provider also enforces a
    /// framework-noise guard so HTTP scanner/request logs from <c>Microsoft.*</c> and <c>System.*</c> categories
    /// are forwarded only at <see cref="LogLevel.Error"/> or higher. That keeps the Telegram channel useful while
    /// preserving application <see cref="LogLevel.Information"/> logs.
    /// </remarks>
    public class TelegramLoggerProvider : ILoggerProvider
    {
        private readonly Func<string, LogLevel, bool> _filter;
        private readonly BotClientProvider _botClientProvider;
        private readonly BotRegistry _botRegistry;
        private readonly BotContextAccessor _botContextAccessor;
        private readonly string _fallbackChannelId;
        private readonly string _fallbackBackupChannelId;

        /// <summary>
        /// Initializes a provider that creates <see cref="TelegramLogger"/> instances.
        /// </summary>
        /// <param name="filter">
        /// Optional application filter. It is evaluated after the built-in framework-noise guard.
        /// Return <c>true</c> to allow the log entry to be posted to Telegram.
        /// </param>
        /// <param name="botClientProvider">Provider used by each logger to select the current bot client.</param>
        /// <param name="botRegistry">Runtime registry used to resolve logger channels for default and tenant contexts.</param>
        /// <param name="botContextAccessor">Async-local bot context accessor used while logging tenant/owned bot work.</param>
        /// <param name="fallbackChannelId">Fallback Telegram log channel id when the active bot has no logger channel.</param>
        /// <param name="fallbackBackupChannelId">Fallback Telegram backup channel id used by payment logs.</param>
        public TelegramLoggerProvider(
            Func<string, LogLevel, bool> filter,
            BotClientProvider botClientProvider,
            BotRegistry botRegistry,
            BotContextAccessor botContextAccessor,
            string fallbackChannelId,
            string fallbackBackupChannelId)
        {
            _filter = filter;
            _botClientProvider = botClientProvider;
            _botRegistry = botRegistry;
            _botContextAccessor = botContextAccessor;
            _fallbackChannelId = fallbackChannelId;
            _fallbackBackupChannelId = fallbackBackupChannelId;

        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TelegramLogger(
                categoryName,
                ShouldForwardToTelegram,
                _botClientProvider,
                _botRegistry,
                _botContextAccessor,
                _fallbackChannelId,
                _fallbackBackupChannelId);
        }

        public void Dispose()
        {
            // Clean up here if needed
        }

        /// <summary>
        /// Applies the provider-level Telegram forwarding policy for one log entry.
        /// </summary>
        /// <param name="categoryName">
        /// Logger category supplied by Microsoft.Extensions.Logging. Framework categories usually start with
        /// <c>Microsoft.</c> or <c>System.</c>.
        /// </param>
        /// <param name="logLevel">Severity of the log entry.</param>
        /// <returns>
        /// <c>true</c> when the entry should be sent to Telegram; otherwise <c>false</c>.
        /// </returns>
        /// <remarks>
        /// Application logs remain allowed at <see cref="LogLevel.Information"/> and above through the injected
        /// filter. Framework categories are allowed only at <see cref="LogLevel.Error"/> and above, which prevents
        /// noisy ASP.NET request/scanner probes such as <c>/.env</c>, <c>/.git/*</c>, and <c>/wp-config.php</c>
        /// from being posted while still surfacing real framework failures.
        /// </remarks>
        private bool ShouldForwardToTelegram(string categoryName, LogLevel logLevel)
        {
            if (logLevel == LogLevel.None)
                return false;

            if (IsFrameworkCategory(categoryName) && logLevel < LogLevel.Error)
                return false;

            return _filter == null || _filter(categoryName, logLevel);
        }

        /// <summary>
        /// Detects framework logging categories that should not be posted to Telegram at information level.
        /// </summary>
        /// <param name="categoryName">Logger category name; null or empty values are treated as application logs.</param>
        /// <returns>
        /// <c>true</c> when the category belongs to Microsoft or System framework logging; otherwise <c>false</c>.
        /// </returns>
        private static bool IsFrameworkCategory(string categoryName)
        {
            return !string.IsNullOrWhiteSpace(categoryName) &&
                   (categoryName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                    categoryName.StartsWith("System.", StringComparison.Ordinal));
        }
    }

}
