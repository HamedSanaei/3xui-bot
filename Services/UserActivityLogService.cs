using System.Diagnostics;
using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Newtonsoft.Json;
using Telegram.Bot.Types;

/// <summary>
/// Writes compact JSONL audit events for Telegram user activity.
/// </summary>
/// <remarks>
/// The log is intentionally file-based and includes current bot metadata from <see cref="BotContextAccessor"/>
/// so multi-brand and tenant-bot actions can be traced back to the exact bot that handled the update.
/// </remarks>
public class UserActivityLogService
{
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool? _runtimeEnabled;
    private UserActivityLogLevel? _runtimeLevel;

    /// <summary>
    /// Creates the activity logger with application configuration for file path, enabled state, and log level.
    /// </summary>
    /// <param name="configuration">Application configuration containing user activity log settings.</param>
    public UserActivityLogService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Indicates whether runtime and configured settings allow activity logging.
    /// </summary>
    public bool IsEnabled => GetSettings().Enabled;

    /// <summary>
    /// Current effective activity log level after applying runtime overrides.
    /// </summary>
    public string CurrentLevel => GetSettings().Level.ToString();

    /// <summary>
    /// Resolved file path where the current day's JSONL events are written.
    /// </summary>
    public string CurrentFilePath => ResolveFilePath(GetSettings());

    /// <summary>
    /// Overrides the configured enabled state at runtime.
    /// </summary>
    /// <param name="enabled">Whether user activity logging should be enabled.</param>
    public void SetEnabled(bool enabled)
    {
        _runtimeEnabled = enabled;
    }

    /// <summary>
    /// Attempts to override the activity log level at runtime.
    /// </summary>
    /// <param name="level">Text level such as off, error, warning, information, or debug.</param>
    /// <param name="normalizedLevel">Normalized enum name when parsing succeeds.</param>
    /// <returns><c>true</c> when the level is accepted; otherwise <c>false</c>.</returns>
    public bool TrySetLevel(string level, out string normalizedLevel)
    {
        if (!TryParseLevel(level, out var parsed))
        {
            normalizedLevel = null;
            return false;
        }

        _runtimeLevel = parsed;
        normalizedLevel = parsed.ToString();
        return true;
    }

    /// <summary>
    /// Logs an incoming Telegram message with the compact command/body marker and current bot metadata.
    /// </summary>
    /// <param name="message">Telegram message update.</param>
    /// <param name="credUser">Credential user associated with the sender, when known.</param>
    /// <param name="isSuperAdmin">Whether the sender is currently treated as a super admin.</param>
    /// <param name="cancellationToken">Cancellation token for file writing.</param>
    /// <returns>A task that completes after the event is written or skipped.</returns>
    public Task LogMessageAsync(
        Message message,
        CredUser credUser,
        bool isSuperAdmin,
        CancellationToken cancellationToken)
    {
        if (message == null)
            return Task.CompletedTask;

        var details = new Dictionary<string, object>
        {
            ["command"] = ExtractMessageCommand(message)
        };

        return WriteAsync(
            UserActivityLogLevel.Information,
            "telegram_message",
            credUser,
            isSuperAdmin,
            details,
            cancellationToken);
    }

    /// <summary>
    /// Logs an incoming Telegram callback query with callback data and current bot metadata.
    /// </summary>
    /// <param name="callbackQuery">Telegram callback query update.</param>
    /// <param name="credUser">Credential user associated with the sender, when known.</param>
    /// <param name="isSuperAdmin">Whether the sender is currently treated as a super admin.</param>
    /// <param name="cancellationToken">Cancellation token for file writing.</param>
    /// <returns>A task that completes after the event is written or skipped.</returns>
    public Task LogCallbackAsync(
        CallbackQuery callbackQuery,
        CredUser credUser,
        bool isSuperAdmin,
        CancellationToken cancellationToken)
    {
        if (callbackQuery == null)
            return Task.CompletedTask;

        var details = new Dictionary<string, object>
        {
            ["command"] = callbackQuery.Data ?? string.Empty
        };

        return WriteAsync(
            UserActivityLogLevel.Information,
            "telegram_callback",
            credUser,
            isSuperAdmin,
            details,
            cancellationToken);
    }

    /// <summary>
    /// Logs a named application action at information level.
    /// </summary>
    /// <param name="action">Action name; empty values become <c>bot_action</c>.</param>
    /// <param name="credUser">Credential user related to the action, when known.</param>
    /// <param name="isSuperAdmin">Whether the related user is a super admin.</param>
    /// <param name="details">Optional structured details; unsupported keys are dropped.</param>
    /// <param name="cancellationToken">Cancellation token for file writing.</param>
    /// <returns>A task that completes after the event is written or skipped.</returns>
    public Task LogBotActionAsync(
        string action,
        CredUser credUser,
        bool isSuperAdmin,
        IDictionary<string, object> details,
        CancellationToken cancellationToken)
    {
        return WriteAsync(
            UserActivityLogLevel.Information,
            string.IsNullOrWhiteSpace(action) ? "bot_action" : action,
            credUser,
            isSuperAdmin,
            details,
            cancellationToken);
    }

    /// <summary>
    /// Logs a named application warning.
    /// </summary>
    /// <param name="action">Warning action name; empty values become <c>bot_warning</c>.</param>
    /// <param name="credUser">Credential user related to the warning, when known.</param>
    /// <param name="isSuperAdmin">Whether the related user is a super admin.</param>
    /// <param name="details">Optional structured details; unsupported keys are dropped.</param>
    /// <param name="cancellationToken">Cancellation token for file writing.</param>
    /// <returns>A task that completes after the event is written or skipped.</returns>
    public Task LogWarningAsync(
        string action,
        CredUser credUser,
        bool isSuperAdmin,
        IDictionary<string, object> details,
        CancellationToken cancellationToken)
    {
        return WriteAsync(
            UserActivityLogLevel.Warning,
            string.IsNullOrWhiteSpace(action) ? "bot_warning" : action,
            credUser,
            isSuperAdmin,
            details,
            cancellationToken);
    }

    /// <summary>
    /// Logs an application error with a compact stack-frame summary.
    /// </summary>
    /// <param name="action">Error action name; empty values become <c>bot_error</c>.</param>
    /// <param name="exception">Exception whose type, message, file, line, and method are recorded.</param>
    /// <param name="credUser">Credential user related to the error, when known.</param>
    /// <param name="isSuperAdmin">Whether the related user is a super admin.</param>
    /// <param name="details">Optional structured details merged with exception data.</param>
    /// <param name="cancellationToken">Cancellation token for file writing.</param>
    /// <returns>A task that completes after the event is written or skipped.</returns>
    public Task LogErrorAsync(
        string action,
        Exception exception,
        CredUser credUser,
        bool isSuperAdmin,
        IDictionary<string, object> details,
        CancellationToken cancellationToken)
    {
        var mergedDetails = details == null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(details);

        foreach (var item in BuildErrorInfo(exception))
            mergedDetails[item.Key] = item.Value;

        return WriteAsync(
            UserActivityLogLevel.Error,
            string.IsNullOrWhiteSpace(action) ? "bot_error" : action,
            credUser,
            isSuperAdmin,
            mergedDetails,
            cancellationToken);
    }

    /// <summary>
    /// Builds, filters, serializes, and appends one JSONL activity event.
    /// </summary>
    /// <param name="level">Event level used for filtering.</param>
    /// <param name="eventName">Stable event name.</param>
    /// <param name="credUser">Credential user related to the event, when known.</param>
    /// <param name="isSuperAdmin">Whether the related user is a super admin.</param>
    /// <param name="details">Optional structured details that are compacted before writing.</param>
    /// <param name="cancellationToken">Cancellation token for file writing.</param>
    private async Task WriteAsync(
        UserActivityLogLevel level,
        string eventName,
        CredUser credUser,
        bool isSuperAdmin,
        IDictionary<string, object> details,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (!settings.Enabled || level > settings.Level)
            return;

        var tehranNow = GetTehranNow(DateTime.UtcNow);
        var entry = new Dictionary<string, object>
        {
            ["time"] = tehranNow.ConvertToHijriShamsi(),
            ["level"] = level.ToString(),
            ["event"] = eventName,
            // Bot fields make multi-brand and tenant activity traceable in the JSONL log.
            ["botId"] = BotContextAccessor.CurrentBotId,
            ["botUsername"] = NormalizeUsername(BotContextAccessor.CurrentBotUsername),
            ["botType"] = BotContextAccessor.CurrentBotType,
            ["tenantOwnerTelegramUserId"] = BotContextAccessor.CurrentBotOwnerTelegramUserId,
            ["userId"] = credUser?.TelegramUserId ?? 0,
            ["username"] = NormalizeUsername(credUser?.Username),
            ["role"] = GetRole(credUser, isSuperAdmin)
        };

        foreach (var item in CompactDetails(details))
            entry[item.Key] = item.Value;

        var json = JsonConvert.SerializeObject(entry, Formatting.None);
        var path = ResolveFilePath(settings);
        var lockTaken = false;

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            lockTaken = true;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                var preamble = encoding.GetPreamble();
                if (preamble.Length > 0)
                    await stream.WriteAsync(preamble, cancellationToken);
            }

            await using var writer = new StreamWriter(stream, encoding);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UserActivityLog] write failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (lockTaken)
                _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads effective logging settings from configuration and runtime overrides.
    /// </summary>
    /// <returns>Resolved activity log settings.</returns>
    private UserActivityLogSettings GetSettings()
    {
        var config = _configuration.Get<AppConfig>() ?? new AppConfig();
        var level = _runtimeLevel
                    ?? (TryParseLevel(config.UserActivityLogLevel, out var configuredLevel)
                        ? configuredLevel
                        : UserActivityLogLevel.Information);

        return new UserActivityLogSettings
        {
            Enabled = _runtimeEnabled ?? config.UserActivityLogEnabled,
            Level = level,
            FilePath = string.IsNullOrWhiteSpace(config.UserActivityLogFilePath)
                ? "./Data/Logs/user-activity-{shamsiDate}.jsonl"
                : config.UserActivityLogFilePath,
            MaxExceptionDepth = config.UserActivityLogMaxExceptionDepth <= 0 ? 1 : config.UserActivityLogMaxExceptionDepth
        };
    }

    /// <summary>
    /// Converts user flags into the role label written to the JSONL event.
    /// </summary>
    /// <param name="credUser">Credential user being logged.</param>
    /// <param name="isSuperAdmin">Whether the sender has super-admin privileges in the current flow.</param>
    /// <returns>Role label: super-admin, colleague, or customer.</returns>
    private static string GetRole(CredUser credUser, bool isSuperAdmin)
    {
        return isSuperAdmin ? "super-admin" : credUser?.IsColleague == true ? "colleague" : "customer";
    }

    /// <summary>
    /// Normalizes a Telegram username to a clickable <c>@username</c> format for logs.
    /// </summary>
    /// <param name="username">Raw username that may or may not start with @.</param>
    /// <returns>Normalized username, or an empty string when unavailable.</returns>
    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return string.Empty;

        username = username.Trim();
        return username.StartsWith("@", StringComparison.Ordinal) ? username : "@" + username;
    }

    /// <summary>
    /// Extracts a compact command marker from a Telegram message.
    /// </summary>
    /// <param name="message">Telegram message to inspect.</param>
    /// <returns>Text, caption, contact marker, or message-type marker.</returns>
    private static string ExtractMessageCommand(Message message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
            return message.Text;

        if (message.Contact != null)
            return "[contact]";

        if (!string.IsNullOrWhiteSpace(message.Caption))
            return message.Caption;

        return $"[{message.Type}]";
    }

    /// <summary>
    /// Builds the compact exception metadata stored in activity logs.
    /// </summary>
    /// <param name="exception">Exception to summarize.</param>
    /// <returns>Dictionary containing error type, message, file, line, and method.</returns>
    private static Dictionary<string, object> BuildErrorInfo(Exception exception)
    {
        var frame = GetFirstUsefulFrame(exception);
        return new Dictionary<string, object>
        {
            ["errorType"] = exception?.GetType().Name ?? "Exception",
            ["errorMessage"] = Truncate(exception?.Message, 300),
            ["errorFile"] = frame.File,
            ["errorLine"] = frame.Line,
            ["errorMethod"] = frame.Method
        };
    }

    /// <summary>
    /// Finds the first stack frame with file information for readable error diagnostics.
    /// </summary>
    /// <param name="exception">Exception whose stack trace should be inspected.</param>
    /// <returns>Normalized file, source line, and method name; empty values when no frame is available.</returns>
    private static (string File, int Line, string Method) GetFirstUsefulFrame(Exception exception)
    {
        if (exception == null || string.IsNullOrWhiteSpace(exception.StackTrace))
            return (string.Empty, 0, string.Empty);

        var frames = new StackTrace(exception, true).GetFrames() ?? Array.Empty<StackFrame>();
        var frame = frames.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.GetFileName()))
                    ?? frames.FirstOrDefault();

        if (frame == null)
            return (string.Empty, 0, string.Empty);

        var method = frame.GetMethod();
        return (
            NormalizePath(frame.GetFileName()),
            frame.GetFileLineNumber(),
            $"{method?.DeclaringType?.Name}.{method?.Name}");
    }

    /// <summary>
    /// Keeps only approved detail keys and truncates long strings before writing JSONL.
    /// </summary>
    /// <param name="details">Raw detail dictionary supplied by callers.</param>
    /// <returns>Filtered and compacted detail dictionary.</returns>
    private static Dictionary<string, object> CompactDetails(IDictionary<string, object> details)
    {
        var compact = new Dictionary<string, object>();
        if (details == null)
            return compact;

        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "command",
            "accountEmail",
            "targetTelegramUserId",
            "serviceKey",
            "trafficGb",
            "trafficAddedGb",
            "durationDays",
            "durationAddedDays",
            "priceToman",
            "balanceToman",
            "expiryShamsi",
            "requestedAction",
            "message",
            "errorType",
            "errorMessage",
            "errorFile",
            "errorLine",
            "errorMethod",
            "enabled",
            "level"
        };

        foreach (var pair in details)
        {
            if (!allowedKeys.Contains(pair.Key) || pair.Value == null)
                continue;

            var value = pair.Value is string text ? Truncate(text, 180) : pair.Value;
            if (value is string stringValue && string.IsNullOrWhiteSpace(stringValue))
                continue;

            compact[pair.Key] = value;
        }

        return compact;
    }

    /// <summary>
    /// Truncates a string for compact log output.
    /// </summary>
    /// <param name="value">Input text.</param>
    /// <param name="maxLength">Maximum allowed length before suffixing with ellipsis.</param>
    /// <returns>Original or truncated text.</returns>
    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value ?? string.Empty;

        return value.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// Shortens an absolute source path to <c>parent/file</c> for log readability.
    /// </summary>
    /// <param name="path">Absolute or relative source file path.</param>
    /// <returns>Compact path string.</returns>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var fileName = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(parent) ? fileName : $"{parent}/{fileName}";
    }

    /// <summary>
    /// Parses user/config text into a <see cref="UserActivityLogLevel"/>.
    /// </summary>
    /// <param name="value">Raw text level.</param>
    /// <param name="level">Parsed level, or Information when empty.</param>
    /// <returns><c>true</c> when parsing succeeds.</returns>
    private static bool TryParseLevel(string value, out UserActivityLogLevel level)
    {
        level = UserActivityLogLevel.Information;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "off":
                level = UserActivityLogLevel.Off;
                return true;
            case "error":
            case "errors":
                level = UserActivityLogLevel.Error;
                return true;
            case "warn":
            case "warning":
            case "warnings":
                level = UserActivityLogLevel.Warning;
                return true;
            case "info":
            case "information":
                level = UserActivityLogLevel.Information;
                return true;
            case "debug":
                level = UserActivityLogLevel.Debug;
                return true;
            default:
                return Enum.TryParse(value, ignoreCase: true, out level);
        }
    }

    /// <summary>
    /// Resolves the configured log file path and expands date placeholders.
    /// </summary>
    /// <param name="settings">Effective activity log settings.</param>
    /// <returns>Absolute JSONL file path.</returns>
    private static string ResolveFilePath(UserActivityLogSettings settings)
    {
        var now = GetTehranNow(DateTime.UtcNow);
        var persianCalendar = new PersianCalendar();
        var shamsiDate = string.Format(
            CultureInfo.InvariantCulture,
            "{0:0000}{1:00}{2:00}",
            persianCalendar.GetYear(now),
            persianCalendar.GetMonth(now),
            persianCalendar.GetDayOfMonth(now));

        var filePath = settings.FilePath
            .Replace("{shamsiDate}", shamsiDate, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        return Path.GetFullPath(filePath);
    }

    /// <summary>
    /// Converts UTC time to Tehran time for date placeholders and Persian timestamps.
    /// </summary>
    /// <param name="utcNow">UTC timestamp.</param>
    /// <returns>Tehran-local timestamp, with a fixed-offset fallback when the OS timezone is missing.</returns>
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
}

/// <summary>
/// Activity log severity levels. Lower numeric values are more important.
/// </summary>
public enum UserActivityLogLevel
{
    Off = 0,
    Error = 1,
    Warning = 2,
    Information = 3,
    Debug = 4
}

/// <summary>
/// Effective settings used by <see cref="UserActivityLogService"/> for JSONL output.
/// </summary>
public class UserActivityLogSettings
{
    public bool Enabled { get; set; } = true;
    public UserActivityLogLevel Level { get; set; } = UserActivityLogLevel.Information;
    public string FilePath { get; set; } = "./Data/Logs/user-activity-{shamsiDate}.jsonl";
    public int MaxExceptionDepth { get; set; } = 3;
}
