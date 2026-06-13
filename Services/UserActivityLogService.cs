using System.Diagnostics;
using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Newtonsoft.Json;
using Telegram.Bot.Types;

public class UserActivityLogService
{
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool? _runtimeEnabled;
    private UserActivityLogLevel? _runtimeLevel;

    public UserActivityLogService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsEnabled => GetSettings().Enabled;

    public string CurrentLevel => GetSettings().Level.ToString();

    public string CurrentFilePath => ResolveFilePath(GetSettings());

    public void SetEnabled(bool enabled)
    {
        _runtimeEnabled = enabled;
    }

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

    private static string GetRole(CredUser credUser, bool isSuperAdmin)
    {
        return isSuperAdmin ? "super-admin" : credUser?.IsColleague == true ? "colleague" : "customer";
    }

    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return string.Empty;

        username = username.Trim();
        return username.StartsWith("@", StringComparison.Ordinal) ? username : "@" + username;
    }

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

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value ?? string.Empty;

        return value.Substring(0, maxLength) + "...";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var fileName = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(parent) ? fileName : $"{parent}/{fileName}";
    }

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

public enum UserActivityLogLevel
{
    Off = 0,
    Error = 1,
    Warning = 2,
    Information = 3,
    Debug = 4
}

public class UserActivityLogSettings
{
    public bool Enabled { get; set; } = true;
    public UserActivityLogLevel Level { get; set; } = UserActivityLogLevel.Information;
    public string FilePath { get; set; } = "./Data/Logs/user-activity-{shamsiDate}.jsonl";
    public int MaxExceptionDepth { get; set; } = 3;
}
