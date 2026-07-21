using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

/// <summary>
/// Aggregates completed-day Telegram usage and successful account sales across owned and tenant bots.
/// </summary>
/// <remarks>
/// Incoming messages and callbacks plus owned-bot sales come from the append-only daily activity JSONL files.
/// Fulfilled tenant sales come from <c>users.db</c>, where the order is the authoritative idempotent record.
/// Super-admin ids configured in <see cref="AppConfig.AdminsUserIds"/> are excluded from both usage and sales.
/// </remarks>
public sealed class UsageAnalyticsService
{
    private static readonly HashSet<string> OwnedSaleEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "xui_v3_account_created",
        "xui_v3_bulk_accounts_created",
        "xui_v3_account_renewed",
        "legacy_account_purchased",
        "legacy_account_renewed"
    };

    private readonly IConfiguration _configuration;
    private readonly UserDbContextFactory _userDbContextFactory;
    private readonly ILogger<UsageAnalyticsService> _logger;
    private readonly TimeZoneInfo _iranTimeZone;

    /// <summary>
    /// Creates the shared usage aggregator.
    /// </summary>
    /// <param name="configuration">
    /// Reloadable application configuration containing activity-log paths and global super-admin Telegram ids.
    /// </param>
    /// <param name="userDbContextFactory">
    /// Per-operation context factory for reading fulfilled tenant orders from <c>users.db</c> without sharing an EF
    /// change tracker with Telegram receivers.
    /// </param>
    /// <param name="logger">Structured application logger used for malformed-file and database diagnostics.</param>
    public UsageAnalyticsService(
        IConfiguration configuration,
        UserDbContextFactory userDbContextFactory,
        ILogger<UsageAnalyticsService> logger)
    {
        _configuration = configuration;
        _userDbContextFactory = userDbContextFactory;
        _logger = logger;
        _iranTimeZone = ResolveIranTimeZone();
    }

    /// <summary>
    /// Gets the current Tehran-local wall-clock time.
    /// </summary>
    /// <returns>Current time converted from UTC with Linux and Windows timezone compatibility.</returns>
    public DateTime GetIranNow()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);
    }

    /// <summary>
    /// Converts a Tehran-local date/time into UTC for users.db range queries.
    /// </summary>
    /// <param name="iranLocalTime">
    /// Tehran-local wall-clock value. Its <see cref="DateTime.Kind"/> is ignored and treated as unspecified.
    /// </param>
    /// <returns>The corresponding UTC timestamp.</returns>
    public DateTime ConvertIranTimeToUtc(DateTime iranLocalTime)
    {
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(iranLocalTime, DateTimeKind.Unspecified), _iranTimeZone);
    }

    /// <summary>
    /// Converts a UTC timestamp into Tehran-local time.
    /// </summary>
    /// <param name="utcTime">UTC timestamp read from users.db; unspecified values are interpreted as UTC.</param>
    /// <returns>Tehran-local date and time.</returns>
    public DateTime ConvertUtcToIranTime(DateTime utcTime)
    {
        var normalizedUtc = utcTime.Kind == DateTimeKind.Utc
            ? utcTime
            : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, _iranTimeZone);
    }

    /// <summary>
    /// Formats a Tehran-local date as a Persian calendar date.
    /// </summary>
    /// <param name="iranDate">Tehran-local date whose time component is ignored.</param>
    /// <returns>Date text in <c>yyyy/MM/dd</c> form.</returns>
    public static string FormatPersianDate(DateTime iranDate)
    {
        var calendar = new PersianCalendar();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{calendar.GetYear(iranDate):0000}/{calendar.GetMonth(iranDate):00}/{calendar.GetDayOfMonth(iranDate):00}");
    }

    /// <summary>
    /// Builds daily usage and optional gross-sale statistics for a completed Tehran-local date range.
    /// </summary>
    /// <param name="startDateIran">Inclusive Tehran-local start date; its time component is discarded.</param>
    /// <param name="dayCount">Number of complete daily buckets to return, between 1 and 366.</param>
    /// <param name="botIdFilter">
    /// Optional internal bot id. Pass <c>null</c> for the global owned-plus-tenant report, or a tenant bot id for the
    /// owner-facing tenant report. Events without bot attribution cannot satisfy a bot-specific filter.
    /// </param>
    /// <param name="includeSales">
    /// Whether successful owned-bot activity events and fulfilled tenant orders should contribute gross toman sales.
    /// Admin usage summaries can pass <c>false</c>; the scheduled chart passes <c>true</c>.
    /// </param>
    /// <param name="cancellationToken">Token that cancels file and users.db reads when the host is stopping.</param>
    /// <returns>
    /// Ordered daily buckets. Missing files and malformed lines are reported on the result and do not throw.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="dayCount"/> is outside the supported range of 1 through 366.
    /// </exception>
    /// <remarks>
    /// Callers are responsible for passing completed days. Main admin commands use yesterday as the final day, while
    /// the scheduled report passes a completed Saturday-to-Saturday period.
    /// </remarks>
    /// <example>
    /// <code>
    /// var yesterday = analytics.GetIranNow().Date.AddDays(-1);
    /// var weekly = await analytics.GetReportAsync(
    ///     yesterday.AddDays(-6), 7, botIdFilter: null, includeSales: false, cancellationToken);
    /// </code>
    /// </example>
    public async Task<UsageAnalyticsReport> GetReportAsync(
        DateTime startDateIran,
        int dayCount,
        string botIdFilter,
        bool includeSales,
        CancellationToken cancellationToken = default)
    {
        if (dayCount is < 1 or > 366)
            throw new ArgumentOutOfRangeException(nameof(dayCount), dayCount, "Usage report day count must be between 1 and 366.");

        var normalizedStart = startDateIran.Date;
        var buckets = Enumerable.Range(0, dayCount)
            .Select(offset => new UsageDailyStat { DateIran = normalizedStart.AddDays(offset) })
            .ToList();
        var bucketsByDate = buckets.ToDictionary(x => x.DateIran.Date);
        var usersByDate = buckets.ToDictionary(x => x.DateIran.Date, _ => new HashSet<long>());
        var appConfig = _configuration.Get<AppConfig>() ?? new AppConfig();
        var superAdmins = (appConfig.AdminsUserIds ?? new List<long>()).ToHashSet();

        foreach (var bucket in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadActivityDayAsync(
                bucket,
                usersByDate[bucket.DateIran.Date],
                appConfig,
                superAdmins,
                botIdFilter,
                includeSales,
                cancellationToken);
        }

        foreach (var bucket in buckets)
            bucket.UniqueUsers = usersByDate[bucket.DateIran.Date].Count;

        if (includeSales)
        {
            await AddTenantSalesAsync(
                normalizedStart,
                dayCount,
                botIdFilter,
                superAdmins,
                bucketsByDate,
                cancellationToken);
        }

        return new UsageAnalyticsReport(normalizedStart, buckets);
    }

    /// <summary>
    /// Reads one activity JSONL file and updates its daily bucket without failing the whole report for bad lines.
    /// </summary>
    /// <param name="bucket">Target completed-day bucket.</param>
    /// <param name="dailyUsers">Mutable distinct-user set for the target date.</param>
    /// <param name="appConfig">Current configuration snapshot containing the activity file template.</param>
    /// <param name="superAdmins">Global Telegram ids excluded from usage and sales.</param>
    /// <param name="botIdFilter">Optional internal bot id used by tenant-specific reports.</param>
    /// <param name="includeSales">Whether successful owned-bot account sale events should be summed.</param>
    /// <param name="cancellationToken">Token that cancels asynchronous file reads.</param>
    /// <returns>A task that completes after the file is read or marked missing.</returns>
    private async Task ReadActivityDayAsync(
        UsageDailyStat bucket,
        HashSet<long> dailyUsers,
        AppConfig appConfig,
        HashSet<long> superAdmins,
        string botIdFilter,
        bool includeSales,
        CancellationToken cancellationToken)
    {
        var path = ResolveActivityLogPath(appConfig, bucket.DateIran);
        if (!File.Exists(path))
        {
            bucket.ActivityLogMissing = true;
            return;
        }

        var expectedPersianDate = FormatPersianDate(bucket.DateIran);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseActivityEvent(line, expectedPersianDate, out var activityEvent))
                {
                    bucket.MalformedLines++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(botIdFilter) &&
                    !string.Equals(activityEvent.BotId, botIdFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (activityEvent.TelegramUserId <= 0 || superAdmins.Contains(activityEvent.TelegramUserId))
                    continue;

                if (IsInteractionEvent(activityEvent.EventName))
                {
                    bucket.Interactions++;
                    dailyUsers.Add(activityEvent.TelegramUserId);
                }

                if (includeSales &&
                    IsOwnedSaleEvent(activityEvent) &&
                    activityEvent.PriceToman > 0)
                {
                    bucket.SalesToman += activityEvent.PriceToman;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            bucket.ActivityLogMissing = true;
            _logger.LogWarning(
                ex,
                "Usage activity log could not be read. dateIran={DateIran}, path={Path}",
                expectedPersianDate,
                path);
        }
    }

    /// <summary>
    /// Adds authoritative fulfilled tenant order values to matching Tehran-local daily buckets.
    /// </summary>
    /// <param name="startDateIran">Inclusive Tehran-local start date.</param>
    /// <param name="dayCount">Number of daily buckets in the report.</param>
    /// <param name="botIdFilter">Optional tenant id; an owned id naturally matches no tenant orders.</param>
    /// <param name="superAdmins">Telegram ids whose test or administrative orders must not count as sales.</param>
    /// <param name="bucketsByDate">Mutable report buckets keyed by Tehran-local date.</param>
    /// <param name="cancellationToken">Token used for the users.db query.</param>
    /// <returns>A task that completes after every matching order is assigned to its local date.</returns>
    private async Task AddTenantSalesAsync(
        DateTime startDateIran,
        int dayCount,
        string botIdFilter,
        HashSet<long> superAdmins,
        Dictionary<DateTime, UsageDailyStat> bucketsByDate,
        CancellationToken cancellationToken)
    {
        var startUtc = ConvertIranTimeToUtc(startDateIran.Date);
        var endUtc = ConvertIranTimeToUtc(startDateIran.Date.AddDays(dayCount));

        await using var context = _userDbContextFactory.CreateDbContext();
        var query = context.TenantBotOrders
            .AsNoTracking()
            .Where(x => x.IsFulfilled &&
                        (x.FulfilledAtUtc ?? x.UpdatedAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc) >= startUtc &&
                        (x.FulfilledAtUtc ?? x.UpdatedAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc) < endUtc);

        if (!string.IsNullOrWhiteSpace(botIdFilter))
            query = query.Where(x => x.TenantBotId == botIdFilter);

        var orders = await query
            .Select(x => new
            {
                x.CustomerTelegramUserId,
                x.SalePriceToman,
                CompletedAtUtc = x.FulfilledAtUtc ?? x.UpdatedAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        foreach (var order in orders)
        {
            if (order.SalePriceToman <= 0 || superAdmins.Contains(order.CustomerTelegramUserId))
                continue;

            var dateIran = ConvertUtcToIranTime(order.CompletedAtUtc).Date;
            if (bucketsByDate.TryGetValue(dateIran, out var bucket))
                bucket.SalesToman += order.SalePriceToman;
        }
    }

    /// <summary>
    /// Parses both the legacy nested JSONL schema and the current compact flat schema.
    /// </summary>
    /// <param name="line">One UTF-8 JSONL line, optionally beginning with a BOM marker.</param>
    /// <param name="expectedPersianDate">Persian <c>yyyy/MM/dd</c> date expected for the file being read.</param>
    /// <param name="activityEvent">Normalized event returned when parsing succeeds.</param>
    /// <returns>
    /// <c>true</c> when the JSON is valid and belongs to <paramref name="expectedPersianDate"/>; otherwise <c>false</c>.
    /// </returns>
    private static bool TryParseActivityEvent(
        string line,
        string expectedPersianDate,
        out NormalizedActivityEvent activityEvent)
    {
        activityEvent = default;
        try
        {
            var obj = JObject.Parse(line.TrimStart('\uFEFF'));
            var timestamp = obj.Value<string>("time") ?? obj.Value<string>("timestampShamsi") ?? string.Empty;
            var datePart = timestamp.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.Equals(datePart, expectedPersianDate, StringComparison.Ordinal))
                return false;

            activityEvent = new NormalizedActivityEvent(
                EventName: obj.Value<string>("event") ?? string.Empty,
                BotId: obj.Value<string>("botId") ?? string.Empty,
                BotType: obj.Value<string>("botType") ?? string.Empty,
                TelegramUserId: ReadLong(obj["userId"]) ?? ReadLong(obj.SelectToken("user.telegramUserId")) ?? 0,
                PriceToman: ReadLong(obj["priceToman"]) ?? ReadLong(obj.SelectToken("details.priceToman")) ?? 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a JSON integer or numeric string without throwing for old activity-log representations.
    /// </summary>
    /// <param name="token">Optional JSON token containing a number.</param>
    /// <returns>The parsed 64-bit value, or <c>null</c> when the token is absent or non-numeric.</returns>
    private static long? ReadLong(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return null;

        if (token.Type == JTokenType.Integer)
            return token.Value<long>();

        return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Checks whether an event represents one incoming user interaction counted by usage reports.
    /// </summary>
    /// <param name="eventName">Normalized activity event name.</param>
    /// <returns><c>true</c> for incoming messages or callback queries; otherwise <c>false</c>.</returns>
    private static bool IsInteractionEvent(string eventName)
    {
        return string.Equals(eventName, "telegram_message", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(eventName, "telegram_callback", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a normalized event is one successful owned-bot purchase or renewal.
    /// </summary>
    /// <param name="activityEvent">Parsed activity event including bot attribution and gross price.</param>
    /// <returns>
    /// <c>true</c> only for approved success event names outside tenant bot scope; wallet charges and payment events
    /// return <c>false</c>.
    /// </returns>
    private static bool IsOwnedSaleEvent(NormalizedActivityEvent activityEvent)
    {
        if (!OwnedSaleEvents.Contains(activityEvent.EventName))
            return false;

        if (string.Equals(activityEvent.BotType, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(activityEvent.BotId) ||
               !activityEvent.BotId.StartsWith("tenant-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the expected activity JSONL path for an arbitrary Tehran-local date.
    /// </summary>
    /// <param name="appConfig">Configuration snapshot containing the path template.</param>
    /// <param name="dateIran">Tehran-local date used for Gregorian and Persian placeholders.</param>
    /// <returns>Absolute path after expanding <c>{shamsiDate}</c> and <c>{date}</c>.</returns>
    private static string ResolveActivityLogPath(AppConfig appConfig, DateTime dateIran)
    {
        var template = string.IsNullOrWhiteSpace(appConfig.UserActivityLogFilePath)
            ? "./Data/Logs/user-activity-{shamsiDate}.jsonl"
            : appConfig.UserActivityLogFilePath;
        var shamsiDate = FormatPersianDate(dateIran).Replace("/", string.Empty, StringComparison.Ordinal);
        var path = template
            .Replace("{shamsiDate}", shamsiDate, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", dateIran.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Resolves the Iran timezone on Linux and Windows, with a fixed modern offset fallback.
    /// </summary>
    /// <returns>A timezone suitable for converting current and 2026-era reporting timestamps.</returns>
    private static TimeZoneInfo ResolveIranTimeZone()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Iran Fixed", TimeSpan.FromMinutes(210), "Iran Fixed", "Iran Fixed");
    }

    /// <summary>
    /// Compact normalized representation shared by legacy and current activity log schemas.
    /// </summary>
    /// <param name="EventName">Stable activity event name.</param>
    /// <param name="BotId">Internal bot id when present in the source log.</param>
    /// <param name="BotType">Owned or tenant type when present in the source log.</param>
    /// <param name="TelegramUserId">Telegram sender or buyer id.</param>
    /// <param name="PriceToman">Gross successful sale value in Iranian toman.</param>
    private readonly record struct NormalizedActivityEvent(
        string EventName,
        string BotId,
        string BotType,
        long TelegramUserId,
        long PriceToman);
}
