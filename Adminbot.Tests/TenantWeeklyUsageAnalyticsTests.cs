using System.Data.Common;
using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>Exercises real daily JSONL parsing and SQLite analytics without touching production files or finances.</summary>
/// <remarks>Store and owner are independent isolation dimensions; distinct users are counted per store/date, not per owner/week.</remarks>
public sealed class TenantWeeklyUsageAnalyticsTests
{
    /// <summary>Stable Saturday-midnight boundary used to exercise contiguous completed Tehran weeks.</summary>
    private static readonly DateTime PeriodEndIran = new(2026, 10, 3);

    /// <summary>Legacy and flat events share parsing, while repeated users, unattributed traffic and super-admins remain isolated.</summary>
    /// <returns>A task completing after batched and existing single-report behavior is asserted.</returns>
    /// <remarks>The same owner has two stores, and the same customer contributes independently to each store and date.</remarks>
    [Fact]
    public async Task Batch_isolates_stores_and_daily_users_with_shared_legacy_and_flat_parsing()
    {
        using var fixture = new AnalyticsFixture();
        var currentStart = PeriodEndIran.AddDays(-7);
        var previousStart = PeriodEndIran.AddDays(-14);
        await fixture.WriteDayAsync(currentStart,
            Flat(currentStart, "telegram_message", "store-a", 301),
            Flat(currentStart, "TELEGRAM_MESSAGE", "STORE-A", 301),
            "\uFEFF" + Legacy(currentStart, "telegram_callback", "store-a", 302),
            Flat(currentStart, "telegram_message", "store-b", 301),
            Legacy(currentStart, "telegram_message", "store-c", 401),
            Flat(currentStart, "telegram_message", "unselected-store", 501),
            Flat(currentStart, "telegram_callback", null, 601),
            Flat(currentStart, "telegram_message", "owned-store", 701),
            Flat(currentStart, "telegram_message", "store-a", 900),
            Flat(currentStart, "telegram_callback", "store-b", 900),
            Flat(currentStart, "telegram_message", "store-a", 0),
            Flat(currentStart, "unrelated_event", "store-a", 303),
            Flat(currentStart, "xui_v3_account_created", "store-a", 301, 99999));
        await fixture.WriteDayAsync(currentStart.AddDays(1),
            Flat(currentStart.AddDays(1), "telegram_message", "store-a", 301));
        await fixture.WriteDayAsync(previousStart,
            Legacy(previousStart, "telegram_message", "store-a", 301),
            Flat(previousStart, "telegram_callback", "store-b", 303));

        var readsBefore = fixture.Reads.Count;
        var reports = await fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, Targets());
        Assert.Equal(1, fixture.Reads.Count - readsBefore);
        Assert.Equal(3, reports.Count);
        var a = reports["STORE-A"];
        Assert.Equal((3L, 2), (a.CurrentWeek.Days[0].Interactions, a.CurrentWeek.Days[0].UniqueUsers));
        Assert.Equal((1L, 1), (a.CurrentWeek.Days[1].Interactions, a.CurrentWeek.Days[1].UniqueUsers));
        Assert.Equal(3, a.CurrentWeek.TotalDailyUniqueUsers);
        Assert.Equal(4, a.CurrentWeek.TotalInteractions);
        Assert.Equal(1, a.PreviousWeek.TotalInteractions);
        Assert.Equal(1, reports["store-b"].CurrentWeek.TotalDailyUniqueUsers);
        Assert.Equal(1, reports["store-c"].CurrentWeek.TotalInteractions);
        Assert.All(reports.Values, report =>
        {
            Assert.Equal(7, report.CurrentWeek.Days.Count);
            Assert.Equal(7, report.PreviousWeek.Days.Count);
            Assert.Equal(currentStart, report.CurrentWeek.StartDateIran);
            Assert.Equal(PeriodEndIran, report.CurrentWeek.EndDateIran);
            Assert.Equal(previousStart, report.PreviousWeek.StartDateIran);
            Assert.Equal(currentStart, report.PreviousWeek.EndDateIran);
            Assert.Equal(5, report.CurrentWeek.MissingActivityLogDays);
            Assert.Equal(6, report.PreviousWeek.MissingActivityLogDays);
            Assert.Equal(0, report.CurrentWeek.TotalSalesToman);
            Assert.Equal(0, report.PreviousWeek.TotalSalesToman);
        });

        // Refactoring file streaming must leave the existing global and filtered read contract unchanged.
        var filtered = await fixture.Analytics.GetReportAsync(currentStart, 1, "store-a", false);
        var global = await fixture.Analytics.GetReportAsync(currentStart, 1, null!, false);
        Assert.Equal((3L, 2L), (filtered.TotalInteractions, filtered.TotalDailyUniqueUsers));
        Assert.Equal((8L, 6L), (global.TotalInteractions, global.TotalDailyUniqueUsers));
    }

    /// <summary>Daily log files split precisely across the contiguous fourteen-day window without reading adjacent weeks.</summary>
    /// <returns>A task completing after inclusive first days and final Fridays are assigned to their correct seven-day reports.</returns>
    /// <remarks>Outside-window events cannot contribute usage or malformed-line diagnostics to the completed comparisons.</remarks>
    [Fact]
    public async Task Batch_assigns_log_usage_to_previous_and_current_week_boundary_dates()
    {
        using var fixture = new AnalyticsFixture();
        var previousStart = PeriodEndIran.AddDays(-14);
        var currentStart = PeriodEndIran.AddDays(-7);
        foreach (var date in new[] { previousStart, currentStart.AddDays(-1), currentStart, PeriodEndIran.AddDays(-1) })
            await fixture.WriteDayAsync(date, Flat(date, "telegram_message", "store-a", 301));
        await fixture.WriteDayAsync(previousStart.AddDays(-1),
            "{ outside-window corruption", Flat(previousStart.AddDays(-1), "telegram_message", "store-a", 302));
        await fixture.WriteDayAsync(PeriodEndIran,
            "{ incomplete-week corruption", Flat(PeriodEndIran, "telegram_message", "store-a", 302));

        var reports = await fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, Targets());
        var a = reports["store-a"];
        Assert.Equal(new long[] { 1, 0, 0, 0, 0, 0, 1 }, a.PreviousWeek.Days.Select(x => x.Interactions));
        Assert.Equal(new long[] { 1, 0, 0, 0, 0, 0, 1 }, a.CurrentWeek.Days.Select(x => x.Interactions));
        Assert.Equal(2, a.PreviousWeek.TotalDailyUniqueUsers);
        Assert.Equal(2, a.CurrentWeek.TotalDailyUniqueUsers);
        Assert.Equal(0, a.PreviousWeek.MalformedLines);
        Assert.Equal(0, a.CurrentWeek.MalformedLines);
        Assert.Equal(5, a.PreviousWeek.MissingActivityLogDays);
        Assert.Equal(5, a.CurrentWeek.MissingActivityLogDays);
    }

    /// <summary>Only fulfilled gross sales with matching store AND current owner enter inclusive/exclusive Tehran buckets.</summary>
    /// <returns>A task completing after one real SQLite batch query and financial read-only invariants are asserted.</returns>
    /// <remarks>Each completion fallback level is covered; paid-but-unfulfilled orders and historical-owner sales are excluded.</remarks>
    [Fact]
    public async Task Batch_counts_only_owner_matched_fulfilled_gross_sales_at_exact_week_boundaries()
    {
        using var fixture = new AnalyticsFixture();
        var previousStart = PeriodEndIran.AddDays(-14);
        var currentStart = PeriodEndIran.AddDays(-7);
        var analytics = fixture.Analytics;
        var previousStartUtc = analytics.ConvertIranTimeToUtc(previousStart);
        var currentStartUtc = analytics.ConvertIranTimeToUtc(currentStart);
        var endUtc = analytics.ConvertIranTimeToUtc(PeriodEndIran);
        var orders = new List<TenantBotOrder>
        {
            Order("store-a", 101, 301, 9000, previousStartUtc.AddTicks(-1)),
            Order("store-a", 101, 301, 100, previousStartUtc),
            Order("store-a", 101, 301, 200, currentStartUtc.AddTicks(-1)),
            Order("store-a", 101, 301, 300, currentStartUtc),
            Order("store-a", 101, 301, 400, endUtc.AddTicks(-1)),
            Order("store-a", 101, 301, 9000, endUtc),
            Order("store-b", 101, 301, 1000, currentStartUtc),
            Order("store-c", 202, 401, 2000, currentStartUtc),
            Order("store-a", 202, 301, 9000, currentStartUtc),
            Order("store-b", 202, 301, 9000, currentStartUtc),
            Order("unselected-store", 101, 301, 9000, currentStartUtc),
            Order("store-a", 101, 900, 9000, currentStartUtc),
            Order("store-a", 101, 301, 0, currentStartUtc),
            Order("store-a", 101, 301, -1, currentStartUtc)
        };
        var pending = Order("store-a", 101, 301, 9000, currentStartUtc);
        pending.IsFulfilled = false;
        pending.PaidAtUtc = currentStartUtc;
        pending.PaymentStatus = TenantBotOrderStatuses.Paid;
        orders.Add(pending);

        var updatedFallback = Order("store-a", 101, 301, 11, previousStartUtc.AddDays(-1));
        updatedFallback.FulfilledAtUtc = null;
        updatedFallback.UpdatedAtUtc = currentStartUtc.AddDays(1);
        updatedFallback.PaidAtUtc = previousStartUtc;
        orders.Add(updatedFallback);
        var paidFallback = Order("store-a", 101, 301, 12, previousStartUtc.AddDays(-1));
        paidFallback.FulfilledAtUtc = null;
        paidFallback.PaidAtUtc = currentStartUtc.AddDays(2);
        orders.Add(paidFallback);
        var createdFallback = Order("store-a", 101, 301, 13, currentStartUtc.AddDays(3));
        createdFallback.FulfilledAtUtc = null;
        orders.Add(createdFallback);
        var fulfilledPrecedence = Order("store-a", 101, 301, 14, previousStartUtc.AddDays(1));
        fulfilledPrecedence.UpdatedAtUtc = currentStartUtc;
        fulfilledPrecedence.PaidAtUtc = currentStartUtc;
        orders.Add(fulfilledPrecedence);
        var renewal = Order("STORE-A", 101, 301, 17, currentStartUtc.AddDays(4));
        renewal.OrderKind = TenantBotOrderKinds.Renew;
        orders.Add(renewal);
        await using (var db = fixture.Users.CreateDbContext())
        {
            db.TenantBotOrders.AddRange(orders);
            await db.SaveChangesAsync();
        }

        var readsBefore = fixture.Reads.Count;
        var reports = await analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, Targets());
        Assert.Equal(1, fixture.Reads.Count - readsBefore);
        var a = reports["store-a"];
        Assert.Equal(314, a.PreviousWeek.TotalSalesToman);
        Assert.Equal(753, a.CurrentWeek.TotalSalesToman);
        Assert.Equal(new long[] { 100, 14, 0, 0, 0, 0, 200 }, a.PreviousWeek.Days.Select(x => x.SalesToman));
        Assert.Equal(new long[] { 300, 11, 12, 13, 17, 0, 400 }, a.CurrentWeek.Days.Select(x => x.SalesToman));
        Assert.Equal(1000, reports["store-b"].CurrentWeek.TotalSalesToman);
        Assert.Equal(2000, reports["store-c"].CurrentWeek.TotalSalesToman);
        Assert.All(reports.Values, report =>
        {
            Assert.Equal(7, report.CurrentWeek.MissingActivityLogDays);
            Assert.Equal(7, report.PreviousWeek.MissingActivityLogDays);
            Assert.Equal(0, report.CurrentWeek.TotalInteractions);
        });
        await using var check = fixture.Users.CreateDbContext();
        var saved = await check.TenantBotOrders.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(orders.Count, saved.Count);
        foreach (var pair in orders.Zip(saved))
        {
            Assert.Equal(pair.First.SalePriceToman, pair.Second.SalePriceToman);
            Assert.Equal(pair.First.IsFulfilled, pair.Second.IsFulfilled);
            Assert.Equal(pair.First.OwnerWalletDelta, pair.Second.OwnerWalletDelta);
            Assert.False(pair.Second.IsOwnerCredited);
        }
    }

    /// <summary>All stores retain identical shared-file diagnostics without losing valid lines or treating empty files as missing.</summary>
    /// <returns>A task completing after malformed, wrong-date, missing and invalid-UTF-8 file outcomes are asserted.</returns>
    /// <remarks>Malformed lines cannot be attributed safely to one store, so their daily diagnostics belong to every report.</remarks>
    [Fact]
    public async Task Batch_preserves_missing_corrupt_and_wrong_date_diagnostics_per_week_and_store()
    {
        using var fixture = new AnalyticsFixture();
        var previousStart = PeriodEndIran.AddDays(-14);
        var currentStart = PeriodEndIran.AddDays(-7);
        await fixture.WriteDayAsync(previousStart,
            "{ broken json",
            Flat(previousStart.AddDays(1), "telegram_message", "store-a", 301),
            "",
            "   ",
            Flat(previousStart, "telegram_message", "store-a", 301),
            Legacy(previousStart, "telegram_callback", "store-b", 301));
        await fixture.WriteDayAsync(previousStart.AddDays(1));
        await File.WriteAllBytesAsync(fixture.DayPath(currentStart), new byte[] { 0xFF, 0x0A });

        var reports = await fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, Targets());
        Assert.All(reports.Values, report =>
        {
            Assert.Equal(2, report.PreviousWeek.MalformedLines);
            Assert.Equal(5, report.PreviousWeek.MissingActivityLogDays);
            Assert.False(report.PreviousWeek.Days[0].ActivityLogMissing);
            Assert.False(report.PreviousWeek.Days[1].ActivityLogMissing);
            Assert.Equal(2, report.PreviousWeek.Days[0].MalformedLines);
            Assert.Equal(7, report.CurrentWeek.MissingActivityLogDays);
            Assert.True(report.CurrentWeek.Days[0].ActivityLogMissing);
            Assert.Equal(0, report.CurrentWeek.MalformedLines);
        });
        Assert.Equal(1, reports["store-a"].PreviousWeek.TotalInteractions);
        Assert.Equal(1, reports["store-b"].PreviousWeek.TotalInteractions);
        Assert.Equal(0, reports["store-c"].PreviousWeek.TotalInteractions);
    }

    /// <summary>Empty inventory performs no database read; duplicate targets coalesce and conflicting ownership fails before analytics.</summary>
    /// <returns>A task completing after boundary, target and cancellation behavior is asserted.</returns>
    /// <remarks>Rejecting conflicting owners prevents a case-insensitive result key from silently selecting the wrong recipient's sales.</remarks>
    [Fact]
    public async Task Batch_validates_scope_and_coalesces_duplicate_targets_without_empty_inventory_io()
    {
        using var fixture = new AnalyticsFixture();
        var readsBefore = fixture.Reads.Count;
        var empty = await fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, Array.Empty<TenantWeeklyReportTarget>());
        Assert.Empty(empty);
        Assert.Equal(readsBefore, fixture.Reads.Count);
        var duplicate = await fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, new[]
        {
            new TenantWeeklyReportTarget("store-a", 101), new TenantWeeklyReportTarget("STORE-A", 101)
        });
        Assert.Single(duplicate);
        Assert.True(duplicate.ContainsKey("STORE-A"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, new[]
        {
            new TenantWeeklyReportTarget("store-a", 101), new TenantWeeklyReportTarget("STORE-A", 202)
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran.AddMinutes(1), Targets()));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran.AddDays(-1), Targets()));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, new[] { new TenantWeeklyReportTarget(" ", 101) }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, new[] { new TenantWeeklyReportTarget("store-a", 0) }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, null!));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Analytics.GetTenantWeeklyReportsAsync(PeriodEndIran, Targets(), cancellation.Token));
    }

    /// <summary>The shared-reader refactor preserves global owned-sale normalization and tenant-order aggregation.</summary>
    /// <returns>A task completing after the pre-existing global reporting API is exercised against real logs and SQLite.</returns>
    /// <remarks>Tenant-tagged log sale events are excluded; owned legacy/flat successes remain counted exactly once.</remarks>
    [Fact]
    public async Task Existing_global_report_retains_owned_sales_and_authoritative_tenant_sales()
    {
        using var fixture = new AnalyticsFixture();
        var date = PeriodEndIran.AddDays(-7);
        await fixture.WriteDayAsync(date,
            Flat(date, "xui_v3_account_created", "owned-store", 301, 100),
            Legacy(date, "legacy_account_renewed", "owned-store", 301, 200),
            Flat(date, "xui_v3_account_created", "tenant-store", 301, 9000),
            Flat(date, "xui_v3_account_created", "owned-store", 900, 9000),
            Flat(date, "payment_received", "owned-store", 301, 9000));
        await using (var db = fixture.Users.CreateDbContext())
        {
            db.TenantBotOrders.Add(Order("tenant-store", 101, 301, 400, fixture.Analytics.ConvertIranTimeToUtc(date)));
            await db.SaveChangesAsync();
        }
        var report = await fixture.Analytics.GetReportAsync(date, 1, null!, true);
        Assert.Equal(700, report.TotalSalesToman);
        Assert.Equal(0, report.TotalInteractions);
        Assert.Equal(0, report.MalformedLines);
        Assert.Equal(0, report.MissingActivityLogDays);
    }

    /// <summary>Creates three independent storefront targets, including two stores owned by the same Telegram user.</summary>
    /// <returns>Stable internal-id/current-owner pairs used by the batch-isolation scenarios.</returns>
    private static TenantWeeklyReportTarget[] Targets() => new[]
    {
        new TenantWeeklyReportTarget("store-a", 101),
        new TenantWeeklyReportTarget("store-b", 101),
        new TenantWeeklyReportTarget("store-c", 202)
    };

    /// <summary>Serializes the production compact activity schema with a deliberately owned bot type for sale-leak regressions.</summary>
    /// <param name="date">Tehran-local file date.</param>
    /// <param name="eventName">Incoming interaction or account-sale event name.</param>
    /// <param name="botId">Optional internal bot id; null exercises unattributed legacy-compatible traffic.</param>
    /// <param name="userId">Telegram sender or purchaser id; 900 is the configured super-admin.</param>
    /// <param name="priceToman">Optional gross sale value in whole Iranian toman.</param>
    /// <returns>One compact JSONL line accepted by the existing parser.</returns>
    private static string Flat(DateTime date, string eventName, string? botId, long userId, long priceToman = 0) => new JObject
    {
        ["time"] = UsageAnalyticsService.FormatPersianDate(date) + " 12:00:00",
        ["event"] = eventName,
        ["botId"] = botId,
        ["botType"] = BotInstanceTypes.Owned,
        ["userId"] = userId,
        ["priceToman"] = priceToman
    }.ToString(Newtonsoft.Json.Formatting.None);

    /// <summary>Serializes nested legacy activity with numeric-string ids/prices to exercise exact normalization.</summary>
    /// <param name="date">Tehran-local file date.</param>
    /// <param name="eventName">Incoming interaction or legacy account-sale name.</param>
    /// <param name="botId">Required internal bot id of this event.</param>
    /// <param name="userId">Telegram sender or buyer represented as a numeric string.</param>
    /// <param name="priceToman">Gross sale value in whole Iranian toman represented as a numeric string.</param>
    /// <returns>One nested legacy JSONL line accepted by the existing parser.</returns>
    private static string Legacy(DateTime date, string eventName, string botId, long userId, long priceToman = 0) => new JObject
    {
        ["timestampShamsi"] = UsageAnalyticsService.FormatPersianDate(date) + " 12:00:00",
        ["event"] = eventName,
        ["botId"] = botId,
        ["user"] = new JObject { ["telegramUserId"] = userId.ToString(CultureInfo.InvariantCulture) },
        ["details"] = new JObject { ["priceToman"] = priceToman.ToString(CultureInfo.InvariantCulture) }
    }.ToString(Newtonsoft.Json.Formatting.None);

    /// <summary>Creates a fulfilled order whose gross differs from base, profit and original-price snapshots.</summary>
    /// <param name="botId">Internal tenant storefront id.</param>
    /// <param name="ownerId">Telegram owner id stored on the order.</param>
    /// <param name="customerId">Telegram buyer id used for super-admin exclusion.</param>
    /// <param name="grossToman">Authoritative SalePriceToman in whole Iranian toman, including exclusion-test zero/negative values.</param>
    /// <param name="completedUtc">UTC creation/fulfillment timestamp; callers can clear fulfillment to test fallback.</param>
    /// <returns>A detached fixture order with no wallet credit or other financial mutation.</returns>
    private static TenantBotOrder Order(string botId, long ownerId, long customerId, long grossToman, DateTime completedUtc) => new()
    {
        OrderId = Guid.NewGuid().ToString("N"), TenantBotId = botId, OwnerTelegramUserId = ownerId,
        CustomerTelegramUserId = customerId, CustomerChatId = customerId, SalePriceToman = grossToman,
        BaseCostToman = 5, ProfitToman = 7, OriginalSalePriceToman = 99999,
        IsFulfilled = true, CreatedAtUtc = completedUtc, FulfilledAtUtc = completedUtc
    };

    /// <summary>Counts actual database reader executions so one batch cannot regress into one order query per storefront.</summary>
    private sealed class ReadCounter : DbCommandInterceptor
    {
        /// <summary>Total asynchronous reader executions, including fixture writes that return generated ids.</summary>
        public int Count { get; private set; }

        /// <summary>Records a real asynchronous SQLite reader without changing its result or query.</summary>
        /// <param name="command">EF-generated command against the fixture's users.db.</param>
        /// <param name="eventData">Diagnostic context for this reader execution.</param>
        /// <param name="result">Unmodified interception result supplied by EF.</param>
        /// <param name="cancellationToken">EF's query cancellation token.</param>
        /// <returns>The original interception result, allowing the real command to execute.</returns>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Owns isolated UTF-8 log files and a real unpooled SQLite users database under one random temporary directory.</summary>
    private sealed class AnalyticsFixture : IDisposable
    {
        /// <summary>Exact test-owned directory; never resolved from application configuration.</summary>
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdminbotUsage-" + Guid.NewGuid().ToString("N"));

        /// <summary>Factory creating independent contexts for the fixture database.</summary>
        public UserDbContextFactory Users { get; }

        /// <summary>Real asynchronous reader counter shared by fixture contexts.</summary>
        public ReadCounter Reads { get; } = new();

        /// <summary>Production aggregator using the isolated database and combined date-placeholder log template.</summary>
        public UsageAnalyticsService Analytics { get; }

        /// <summary>Creates the fixture schema and configures one global excluded super-admin without production configuration.</summary>
        /// <remarks>Pooling is disabled, and only the test-owned directory is removed after all operations finish.</remarks>
        public AnalyticsFixture()
        {
            Directory.CreateDirectory(_directory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "users.db"), Pooling = false
            }.ToString();
            Users = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>()
                .UseSqlite(connectionString).AddInterceptors(Reads).Options);
            using (var db = Users.CreateDbContext())
                db.Database.EnsureCreated();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserActivityLogFilePath"] = Path.Combine(_directory, "activity-{date}-{shamsiDate}.jsonl"),
                ["AdminsUserIds:0"] = "900"
            }).Build();
            Analytics = new UsageAnalyticsService(configuration, Users, NullLogger<UsageAnalyticsService>.Instance);
        }

        /// <summary>Resolves the fixture's combined Gregorian/Persian placeholder path for a Tehran date.</summary>
        /// <param name="dateIran">Tehran-local date represented by the daily log.</param>
        /// <returns>Absolute test-owned path matching the production path-expansion conventions.</returns>
        public string DayPath(DateTime dateIran) => Path.Combine(_directory,
            "activity-" + dateIran.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
            UsageAnalyticsService.FormatPersianDate(dateIran).Replace("/", string.Empty, StringComparison.Ordinal) + ".jsonl");

        /// <summary>Writes UTF-8 activity lines, or a valid present-but-empty file, for one completed Tehran day.</summary>
        /// <param name="dateIran">Tehran-local date used by the file path.</param>
        /// <param name="lines">Serialized JSONL lines or deliberate malformed cases; empty means a present empty file.</param>
        /// <returns>A task completing once this test-owned daily file is written.</returns>
        public Task WriteDayAsync(DateTime dateIran, params string[] lines) =>
            File.WriteAllLinesAsync(DayPath(dateIran), lines, new UTF8Encoding(false));

        /// <summary>Removes only this fixture's random temporary directory after its contexts and file streams are disposed.</summary>
        /// <remarks>No production path or global SQLite pool is touched.</remarks>
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
