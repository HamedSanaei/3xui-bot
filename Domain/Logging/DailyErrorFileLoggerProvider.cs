using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Adminbot.Domain.Logging;

/// <summary>
/// Creates loggers that append warning, error, and critical diagnostics to one UTF-8 file per Tehran calendar day.
/// </summary>
/// <remarks>
/// The provider is intentionally fail-soft: logging must never stop Telegram polling, payment settlement, or XUI
/// account operations. It captures the complete exception chain from <see cref="Exception.ToString"/> while masking
/// obvious Telegram token, authorization, cookie, and secret values before writing to disk.
/// </remarks>
public sealed class DailyErrorFileLoggerProvider : ILoggerProvider
{
    private static readonly object ExternalWriteLock = new();
    private readonly IConfiguration _configuration;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly object _writeLock = new();

    /// <summary>
    /// Creates the daily diagnostic logger provider.
    /// </summary>
    /// <param name="configuration">
    /// Application configuration that controls whether the logger is enabled, its minimum level, and the dated file
    /// path. Values are read for each entry so configuration reloads take effect without restarting the process.
    /// </param>
    /// <param name="botContextAccessor">
    /// Async-local runtime context used to include the current owned, tenant, or assistant bot identity when an error
    /// happens while processing a Telegram update.
    /// </param>
    /// <remarks>
    /// The provider does not own a Telegram client and never forwards file-log entries to any Telegram channel.
    /// </remarks>
    public DailyErrorFileLoggerProvider(IConfiguration configuration, BotContextAccessor botContextAccessor)
    {
        _configuration = configuration;
        _botContextAccessor = botContextAccessor;
    }

    /// <summary>
    /// Creates a category-specific daily diagnostic logger.
    /// </summary>
    /// <param name="categoryName">Microsoft.Extensions.Logging category that produced the entry.</param>
    /// <returns>A logger that writes qualifying entries to the configured daily file.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        return new DailyErrorFileLogger(categoryName, _configuration, _botContextAccessor, _writeLock);
    }

    /// <summary>
    /// Writes one full diagnostic entry for static legacy code that cannot receive an injected <see cref="ILogger"/>.
    /// </summary>
    /// <param name="configuration">
    /// Application configuration used to resolve the live daily-file policy. Static XUI helpers pass their existing
    /// configuration argument so no global secret-bearing singleton is introduced.
    /// </param>
    /// <param name="logLevel">Warning, error, or critical severity to write when enabled by configuration.</param>
    /// <param name="categoryName">Stable component name, such as <c>ApiServicev3</c>.</param>
    /// <param name="message">Formatted operational diagnostic without secrets.</param>
    /// <param name="exception">Optional exception whose complete chain is written after masking.</param>
    /// <remarks>
    /// This bridge exists for static legacy API helpers. It is fail-soft and never forwards output to Telegram.
    /// </remarks>
    public static void WriteExternalDiagnostic(
        IConfiguration configuration,
        LogLevel logLevel,
        string categoryName,
        string message,
        Exception exception = null)
    {
        var logger = new DailyErrorFileLogger(categoryName, configuration, null, ExternalWriteLock);
        logger.Log(
            logLevel,
            new EventId(0, "external-diagnostic"),
            message ?? string.Empty,
            exception,
            static (state, _) => state);
    }

    /// <summary>
    /// Releases provider resources.
    /// </summary>
    /// <remarks>
    /// File streams are opened only while one entry is appended, so the provider has no persistent disposable handle.
    /// </remarks>
    public void Dispose()
    {
    }
}

/// <summary>
/// Writes one fully detailed, masked diagnostic entry per qualifying application log event.
/// </summary>
/// <remarks>
/// Entries include the active bot context when available. User and chat identifiers are preserved when callers put
/// them in the structured log message, while secret-like values are redacted before persistence.
/// </remarks>
internal sealed class DailyErrorFileLogger : ILogger
{
    private static readonly Regex TelegramTokenPattern = new(@"\b\d{6,}:[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled);
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?im)(authorization|x-api-key|api[_-]?key|token|secret(?:[_-]?key)?|ipn[_-]?secret[_-]?key|cookie|set-cookie|password)(?:[\""']?)(\s*[:=]\s*)(?:[\""']?)([^\r\n\s,;\""'}]+)",
        RegexOptions.Compiled);

    private readonly string _categoryName;
    private readonly IConfiguration _configuration;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly object _writeLock;

    /// <summary>
    /// Creates a category-scoped daily file logger.
    /// </summary>
    /// <param name="categoryName">Source logging category; an empty value is written as <c>unknown</c>.</param>
    /// <param name="configuration">Configuration used to resolve the live file logging policy.</param>
    /// <param name="botContextAccessor">Current bot context accessor used for multi-bot diagnostics.</param>
    /// <param name="writeLock">Shared in-process lock that prevents concurrent log entries from interleaving.</param>
    /// <remarks>
    /// All instances created by one provider share <paramref name="writeLock"/> so a stack trace remains contiguous.
    /// </remarks>
    public DailyErrorFileLogger(
        string categoryName,
        IConfiguration configuration,
        BotContextAccessor botContextAccessor,
        object writeLock)
    {
        _categoryName = string.IsNullOrWhiteSpace(categoryName) ? "unknown" : categoryName;
        _configuration = configuration;
        _botContextAccessor = botContextAccessor;
        _writeLock = writeLock;
    }

    /// <summary>
    /// Begins a structured logging scope.
    /// </summary>
    /// <typeparam name="TState">State type supplied by Microsoft.Extensions.Logging.</typeparam>
    /// <param name="state">Scope state, retained by the caller but not separately persisted by this lightweight provider.</param>
    /// <returns>A no-op disposable scope because the formatted message already carries current operation details.</returns>
    public IDisposable BeginScope<TState>(TState state)
    {
        return NoopScope.Instance;
    }

    /// <summary>
    /// Checks whether the current configuration allows one log level to be written to the diagnostic file.
    /// </summary>
    /// <param name="logLevel">Severity supplied by Microsoft.Extensions.Logging.</param>
    /// <returns><c>true</c> when the daily diagnostic file is enabled and accepts the specified severity.</returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        var config = _configuration.Get<AppConfig>() ?? new AppConfig();
        return config.ErrorFileLogEnabled && logLevel >= ParseMinimumLevel(config.ErrorFileLogMinimumLevel);
    }

    /// <summary>
    /// Formats, masks, and appends one diagnostic log entry.
    /// </summary>
    /// <typeparam name="TState">Structured state type supplied by the logging framework.</typeparam>
    /// <param name="logLevel">Severity of the entry.</param>
    /// <param name="eventId">Optional event id supplied by the caller.</param>
    /// <param name="state">Structured state used by <paramref name="formatter"/>.</param>
    /// <param name="exception">Exception to record in full, when the entry represents a failure.</param>
    /// <param name="formatter">Message formatter supplied by Microsoft.Extensions.Logging.</param>
    /// <remarks>
    /// Exceptions are stored with the complete inner-exception chain and stack trace. File I/O failures are swallowed
    /// to preserve the caller's primary operation.
    /// </remarks>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        try
        {
            var config = _configuration.Get<AppConfig>() ?? new AppConfig();
            var now = GetTehranNow(DateTime.UtcNow);
            var context = _botContextAccessor?.Current;
            var message = formatter?.Invoke(state, exception) ?? state?.ToString() ?? string.Empty;
            var entry = new StringBuilder()
                .Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] ")
                .Append(logLevel).Append(' ').Append(_categoryName)
                .Append(" eventId=").Append(eventId.Id)
                .Append(" botId=").Append(context?.Config?.Id ?? BotContextAccessor.CurrentBotId ?? "-")
                .Append(" botUsername=").Append(context?.Config?.Username ?? BotContextAccessor.CurrentBotUsername ?? "-")
                .Append(" botType=").Append(context?.Config?.Type ?? BotContextAccessor.CurrentBotType ?? "-")
                .AppendLine()
                .AppendLine(MaskSensitiveData(message));

            if (exception != null)
                entry.AppendLine(MaskSensitiveData(exception.ToString()));

            entry.AppendLine(new string('-', 96));
            var path = ResolveFilePath(config.ErrorFileLogFilePath, now);
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.AppendAllText(path, entry.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostic persistence is intentionally isolated from the business operation that attempted to log.
        }
    }

    /// <summary>
    /// Parses the configured file-log threshold into a valid logging level.
    /// </summary>
    /// <param name="configuredLevel">Configured level text; empty or invalid values use <see cref="LogLevel.Warning"/>.</param>
    /// <returns>A valid diagnostic file log level.</returns>
    private static LogLevel ParseMinimumLevel(string configuredLevel)
    {
        return Enum.TryParse(configuredLevel, ignoreCase: true, out LogLevel parsed) && parsed != LogLevel.None
            ? parsed
            : LogLevel.Warning;
    }

    /// <summary>
    /// Expands the Tehran Shamsi-date placeholder in the configured diagnostic file path.
    /// </summary>
    /// <param name="configuredPath">Configured relative or absolute path that may include <c>{shamsiDate}</c>.</param>
    /// <param name="tehranNow">Current Tehran-local time used to choose the daily file.</param>
    /// <returns>Absolute path for the current daily diagnostic file.</returns>
    private static string ResolveFilePath(string configuredPath, DateTime tehranNow)
    {
        var calendar = new PersianCalendar();
        var shamsiDate = string.Format(
            CultureInfo.InvariantCulture,
            "{0:0000}{1:00}{2:00}",
            calendar.GetYear(tehranNow),
            calendar.GetMonth(tehranNow),
            calendar.GetDayOfMonth(tehranNow));
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "./Data/Logs/errors-{shamsiDate}.log"
            : configuredPath;
        return Path.GetFullPath(path.Replace("{shamsiDate}", shamsiDate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Converts UTC to Tehran local time with a fixed-offset fallback for minimal Linux installations.
    /// </summary>
    /// <param name="utcNow">UTC timestamp to convert.</param>
    /// <returns>Tehran-local timestamp used for diagnostic timestamps and file naming.</returns>
    private static DateTime GetTehranNow(DateTime utcNow)
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        }
        catch
        {
            return utcNow.AddMinutes(210);
        }
    }

    /// <summary>
    /// Masks common secret representations before a diagnostic entry is persisted.
    /// </summary>
    /// <param name="value">Raw message or exception text that may contain secret-like values.</param>
    /// <returns>Text with recognizable Telegram tokens and credential assignments replaced by <c>[REDACTED]</c>.</returns>
    private static string MaskSensitiveData(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        var masked = TelegramTokenPattern.Replace(value, "[REDACTED_TELEGRAM_TOKEN]");
        return SensitiveAssignmentPattern.Replace(masked, "$1$2[REDACTED]");
    }

    /// <summary>
    /// No-op scope used because this provider records formatted log messages directly.
    /// </summary>
    private sealed class NoopScope : IDisposable
    {
        /// <summary>
        /// Shared no-op scope instance.
        /// </summary>
        public static readonly NoopScope Instance = new();

        /// <summary>
        /// Releases no resources.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
