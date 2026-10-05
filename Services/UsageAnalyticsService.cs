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
    /// Builds isolated current/previous completed-week comparisons for all requested storefronts in one batch.
    /// </summary>
    /// <param name="periodEndIran">
    /// Exclusive Tehran-local end at Saturday 00:00 after the completed Friday; its Kind is ignored. The caller
    /// selects the latest completed week, not a future boundary.
    /// </param>
    /// <param name="targets">
    /// Persisted tenant internal bot ids and current positive owner Telegram user ids. An empty collection returns
    /// no reports; duplicate bot/owner pairs coalesce case-insensitively, but conflicting owners are rejected.
    /// </param>
    /// <param name="cancellationToken">Token cancelling activity-file and read-only users.db queries.</param>
    /// <returns>
    /// A non-null dictionary keyed by internal bot id with ordinal-ignore-case lookup. Each value contains exactly
    /// seven current and seven previous daily buckets, including zero days and file diagnostics.
    /// </returns>
    /// <remarks>
    /// Reads each of the fourteen JSONL files once, sharing the global report's parser, date/path normalization,
    /// interaction rules, and super-admin exclusion. Unattributed and other-bot events never count. Gross sales
    /// come only from fulfilled orders matching BOTH the target bot and current owner, using the same completion
    /// timestamp fallback as the global report. Owned-sale activity events are never added, even with bad bot-type
    /// metadata. This operation changes no orders, wallets, ledgers, or delivery state.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when targets is null.</exception>
    /// <exception cref="ArgumentException">Thrown for a non-Saturday-midnight boundary or invalid/conflicting targets.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the caller cancels file or database reads.</exception>
    /// <example>
    /// <code>
    /// var reports = await analytics.GetTenantWeeklyReportsAsync(
    ///     completedSaturday, new[] { new TenantWeeklyReportTarget(store.Id, store.OwnerTelegramUserId.Value) }, token);
    /// </code>
    /// </example>
    public async Task<IReadOnlyDictionary<string, TenantWeeklyUsageComparison>> GetTenantWeeklyReportsAsync(
        DateTime periodEndIran,
        IReadOnlyCollection<TenantWeeklyReportTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (periodEndIran.DayOfWeek != DayOfWeek.Saturday || periodEndIran.TimeOfDay != TimeSpan.Zero)
            throw new ArgumentException("The tenant weekly boundary must be Tehran Saturday midnight.", nameof(periodEndIran));

        var periodStartIran = periodEndIran.Date.AddDays(-14);
        var byBotId = new Dictionary<string, TenantWeeklyBuckets>(targets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.BotId) || target.OwnerTelegramUserId <= 0)
                throw new ArgumentException("A tenant target requires an internal bot id and a positive owner id.", nameof(targets));

            if (byBotId.TryGetValue(target.BotId, out var existing))
            {
                if (existing.Target.OwnerTelegramUserId != target.OwnerTelegramUserId)
                    throw new ArgumentException("A tenant bot cannot have conflicting report owners.", nameof(targets));
                continue;
            }

            byBotId.Add(target.BotId, new TenantWeeklyBuckets(target, periodStartIran));
        }

        var results = new Dictionary<string, TenantWeeklyUsageComparison>(byBotId.Count, StringComparer.OrdinalIgnoreCase);
        if (byBotId.Count == 0)
            return results;

        var appConfig = _configuration.Get<AppConfig>() ?? new AppConfig();
        var superAdmins = (appConfig.AdminsUserIds ?? new List<long>()).ToHashSet();
        var diagnosticSource = byBotId.Values.First();
        var activityDayIndex = 0;
        Action<NormalizedActivityEvent> consumeEvent = activityEvent =>
        {
            if (activityEvent.TelegramUserId <= 0 ||
                superAdmins.Contains(activityEvent.TelegramUserId) ||
                !IsInteractionEvent(activityEvent.EventName) ||
                !byBotId.TryGetValue(activityEvent.BotId, out var tenant))
            {
                return;
            }

            // A customer's same-day presence is distinct independently in each storefront, not by owner.
            tenant.Days[activityDayIndex].Interactions++;
            tenant.DailyUsers.Add(activityEvent.TelegramUserId);
        };
        for (; activityDayIndex < 14; activityDayIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = diagnosticSource.Days[activityDayIndex];
            await ReadActivityEventsAsync(diagnostics, appConfig, consumeEvent, cancellationToken);

            foreach (var tenant in byBotId.Values)
            {
                tenant.Days[activityDayIndex].UniqueUsers = tenant.DailyUsers.Count;
                // Only daily counts survive in the chart; reuse each store's set instead of retaining fourteen user sets.
                tenant.DailyUsers.Clear();
                tenant.Days[activityDayIndex].ActivityLogMissing = diagnostics.ActivityLogMissing;
                tenant.Days[activityDayIndex].MalformedLines = diagnostics.MalformedLines;
            }
        }

        var startUtc = ConvertIranTimeToUtc(periodStartIran);
        var endUtc = ConvertIranTimeToUtc(periodEndIran);
        var botIds = byBotId.Keys.ToArray();
        await using var context = _userDbContextFactory.CreateDbContext();
        var orders = await context.TenantBotOrders
            .AsNoTracking()
            .Where(x => x.IsFulfilled &&
                        botIds.Contains(EF.Functions.Collate(x.TenantBotId, "NOCASE")) &&
                        (x.FulfilledAtUtc ?? x.UpdatedAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc) >= startUtc &&
                        (x.FulfilledAtUtc ?? x.UpdatedAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc) < endUtc)
            .Select(x => new
            {
                x.TenantBotId,
                x.OwnerTelegramUserId,
                x.CustomerTelegramUserId,
                x.SalePriceToman,
                CompletedAtUtc = x.FulfilledAtUtc ?? x.UpdatedAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        foreach (var order in orders)
        {
            if (order.SalePriceToman <= 0 ||
                superAdmins.Contains(order.CustomerTelegramUserId) ||
                !byBotId.TryGetValue(order.TenantBotId, out var tenant) ||
                order.OwnerTelegramUserId != tenant.Target.OwnerTelegramUserId)
            {
                continue;
            }

            // Matching the owner as well as the store prevents historical ownership crossing the report boundary.
            var dayIndex = (ConvertUtcToIranTime(order.CompletedAtUtc).Date - periodStartIran).Days;
            if (dayIndex is >= 0 and < 14)
                tenant.Days[dayIndex].SalesToman += order.SalePriceToman;
        }

        foreach (var tenant in byBotId.Values)
        {
            results.Add(tenant.Target.BotId, new TenantWeeklyUsageComparison(
                new UsageAnalyticsReport(periodEndIran.Date.AddDays(-7), new ArraySegment<UsageDailyStat>(tenant.Days, 7, 7)),
                new UsageAnalyticsReport(periodStartIran, new ArraySegment<UsageDailyStat>(tenant.Days, 0, 7))));
        }

        return results;
    }

    /// <summary>Owns fourteen daily metric buckets and one reusable daily distinct-user set for a storefront-owner pair.</summary>
    /// <remarks>Only bot-scoped interaction events and current-owner-matched fulfilled orders populate these buckets.</remarks>
    private sealed class TenantWeeklyBuckets
    {
        /// <summary>Persisted storefront and current owner used to isolate authoritative sales.</summary>
        public TenantWeeklyReportTarget Target { get; }

        /// <summary>Chronological previous/current week buckets; never shared with a different storefront.</summary>
        public UsageDailyStat[] Days { get; } = new UsageDailyStat[14];

        /// <summary>Distinct Telegram users for the current streaming date; cleared after its count is captured.</summary>
        public HashSet<long> DailyUsers { get; } = new();

        /// <summary>Creates zero-valued buckets for one tenant across both completed weeks.</summary>
        /// <param name="target">Required internal bot id and current owner Telegram user id.</param>
        /// <param name="startDateIran">Inclusive Tehran-local midnight fourteen days before the reporting boundary.</param>
        /// <remarks>The two weeks share one chronological array so chart/report segments do not copy daily buckets.</remarks>
        public TenantWeeklyBuckets(TenantWeeklyReportTarget target, DateTime startDateIran)
        {
            Target = target;
            for (var index = 0; index < Days.Length; index++)
            {
                Days[index] = new UsageDailyStat { DateIran = startDateIran.AddDays(index) };
            }
        }
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
    /// <remarks>Delegates streaming/parsing to the shared single-pass reader while retaining the existing global/filter semantics.</remarks>
    private Task ReadActivityDayAsync(
        UsageDailyStat bucket,
        HashSet<long> dailyUsers,
        AppConfig appConfig,
        HashSet<long> superAdmins,
        string botIdFilter,
        bool includeSales,
        CancellationToken cancellationToken)
    {
        return ReadActivityEventsAsync(bucket, appConfig, activityEvent =>
        {
            if (!string.IsNullOrWhiteSpace(botIdFilter) &&
                !string.Equals(activityEvent.BotId, botIdFilter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (activityEvent.TelegramUserId <= 0 || superAdmins.Contains(activityEvent.TelegramUserId))
                return;

            if (IsInteractionEvent(activityEvent.EventName))
            {
                bucket.Interactions++;
                dailyUsers.Add(activityEvent.TelegramUserId);
            }

            if (includeSales && IsOwnedSaleEvent(activityEvent) && activityEvent.PriceToman > 0)
                bucket.SalesToman += activityEvent.PriceToman;
        }, cancellationToken);
    }

    /// <summary>
    /// Streams and normalizes one daily activity file once, sharing exact legacy/flat parsing and diagnostics.
    /// </summary>
    /// <param name="diagnosticBucket">Daily bucket whose date resolves the file and receives missing/malformed diagnostics.</param>
    /// <param name="appConfig">Configuration snapshot containing the shared activity-log path template.</param>
    /// <param name="consumeEvent">Synchronous consumer of each valid date-matching normalized event; applies report scope and exclusions.</param>
    /// <param name="cancellationToken">Token that cancels asynchronous UTF-8 file reads.</param>
    /// <returns>A task completing after the file is streamed or marked unreadable; valid earlier events remain counted.</returns>
    /// <remarks>
    /// A tenant batch fans events out by exact internal bot id through one consumer rather than reopening each file
    /// per storefront. Diagnostics describe the shared file and must be copied to every participating daily bucket.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when the supplied cancellation token stops the read.</exception>
    private async Task ReadActivityEventsAsync(
        UsageDailyStat diagnosticBucket,
        AppConfig appConfig,
        Action<NormalizedActivityEvent> consumeEvent,
        CancellationToken cancellationToken)
    {
        var path = ResolveActivityLogPath(appConfig, diagnosticBucket.DateIran);
        if (!File.Exists(path))
        {
            diagnosticBucket.ActivityLogMissing = true;
            return;
        }

        var expectedPersianDate = FormatPersianDate(diagnosticBucket.DateIran);
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
                    diagnosticBucket.MalformedLines++;
                    continue;
                }

                consumeEvent(activityEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnosticBucket.ActivityLogMissing = true;
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
