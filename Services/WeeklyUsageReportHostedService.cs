using System.Globalization;
using System.Net;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>
/// Persists claims and outcomes for scheduled usage-report delivery in <c>users.db</c>.
/// </summary>
/// <remarks>
/// The database unique key is the final concurrency guard. The service never uses <c>credentials.db</c> and every
/// operation owns an independent EF context so Telegram receivers and hosted workers do not share tracking state.
/// </remarks>
public sealed class UsageReportDispatchStore
{
    private readonly UserDbContextFactory _contextFactory;

    /// <summary>
    /// Creates the dispatch store.
    /// </summary>
    /// <param name="contextFactory">Per-operation users.db context factory.</param>
    public UsageReportDispatchStore(UserDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Atomically creates and claims one scheduled report unless it was already sent or has a live lease.
    /// </summary>
    /// <param name="reportKey">Stable global key such as <c>weekly:20260718</c>; must not be empty.</param>
    /// <param name="periodStartUtc">Inclusive UTC reporting boundary.</param>
    /// <param name="periodEndUtc">Exclusive UTC reporting boundary.</param>
    /// <param name="cancellationToken">Token used for all users.db operations.</param>
    /// <returns><c>true</c> when this caller owns the processing lease; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Failed generation or delivery attempts release their lease and can be reclaimed later with the same unique
    /// report key. The unique key and atomic update prevent concurrent workers from sending the period together.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (!await store.TryClaimAsync("weekly:20260718", startUtc, endUtc, cancellationToken))
    ///     return;
    /// </code>
    /// </example>
    public async Task<bool> TryClaimAsync(
        string reportKey,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportKey))
            throw new ArgumentException("Usage report key is required.", nameof(reportKey));

        var now = DateTime.UtcNow;
        await EnsureRowExistsAsync(reportKey, periodStartUtc, periodEndUtc, now, cancellationToken);

        await using var context = _contextFactory.CreateDbContext();
        var leaseUntil = now.AddMinutes(10);
        var affected = await context.UsageReportDispatches
            .Where(x => x.ReportKey == reportKey &&
                        (x.Status == UsageReportDispatchStatuses.Pending ||
                         x.Status == UsageReportDispatchStatuses.Failed ||
                         (x.Status == UsageReportDispatchStatuses.Processing &&
                          (x.LeaseUntilUtc == null || x.LeaseUntilUtc < now))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, UsageReportDispatchStatuses.Processing)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LeaseUntilUtc, leaseUntil)
                .SetProperty(x => x.LastError, (string)null)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Marks a claimed report as delivered after Telegram returns a concrete message.
    /// </summary>
    /// <param name="reportKey">Stable report key previously claimed by the worker.</param>
    /// <param name="telegramMessageId">Positive Telegram message id returned from the logger channel.</param>
    /// <param name="cancellationToken">Token used for the users.db update.</param>
    /// <returns>A task that completes after the sent state is durable.</returns>
    public async Task MarkSentAsync(string reportKey, int telegramMessageId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        await context.UsageReportDispatches
            .Where(x => x.ReportKey == reportKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, UsageReportDispatchStatuses.Sent)
                .SetProperty(x => x.TelegramMessageId, telegramMessageId)
                .SetProperty(x => x.SentAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, (string)null)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
    }

    /// <summary>
    /// Releases a report for a later retry after a definite generation or pre-delivery failure.
    /// </summary>
    /// <param name="reportKey">Stable report key previously claimed by the worker.</param>
    /// <param name="error">Sanitized diagnostic text; it is truncated before persistence.</param>
    /// <param name="cancellationToken">Token used for the users.db update.</param>
    /// <returns>A task that completes after the retryable failure is durable.</returns>
    public Task MarkFailedAsync(string reportKey, string error, CancellationToken cancellationToken)
    {
        return MarkFailureStateAsync(reportKey, UsageReportDispatchStatuses.Failed, error, cancellationToken);
    }

    /// <summary>
    /// Records a terminal, non-retryable state when Telegram accepted the report but normal sent-state persistence failed.
    /// </summary>
    /// <param name="reportKey">Stable report key previously claimed by the worker.</param>
    /// <param name="telegramMessageId">Positive message id already returned by Telegram for the logger-channel photo.</param>
    /// <param name="error">Sanitized persistence error retained for operator reconciliation.</param>
    /// <param name="cancellationToken">Token used for this best-effort users.db update.</param>
    /// <returns>A task that completes after the duplicate-prevention state is persisted.</returns>
    /// <remarks>
    /// The row is removed from the retryable state set because Telegram delivery is known to have succeeded. Retrying
    /// the send after a database-only failure would duplicate the weekly report in the private logger channel.
    /// </remarks>
    /// <example>
    /// <code>
    /// await store.MarkDeliveryRecordedWithErrorAsync(
    ///     "weekly:20260718", telegramMessageId, persistenceError.Message, CancellationToken.None);
    /// </code>
    /// </example>
    public async Task MarkDeliveryRecordedWithErrorAsync(
        string reportKey,
        int telegramMessageId,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var safeError = string.IsNullOrWhiteSpace(error)
            ? "Weekly usage report was delivered but sent-state persistence failed."
            : error.Length <= 1800 ? error : error[..1800];
        await using var context = _contextFactory.CreateDbContext();
        await context.UsageReportDispatches
            .Where(x => x.ReportKey == reportKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, UsageReportDispatchStatuses.DeliveryRecordedWithError)
                .SetProperty(x => x.TelegramMessageId, telegramMessageId)
                .SetProperty(x => x.SentAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, safeError)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
    }

    /// <summary>
    /// Inserts the unique report row, tolerating a concurrent insert by another process.
    /// </summary>
    /// <param name="reportKey">Unique report key.</param>
    /// <param name="periodStartUtc">Inclusive UTC report boundary.</param>
    /// <param name="periodEndUtc">Exclusive UTC report boundary.</param>
    /// <param name="nowUtc">UTC creation timestamp.</param>
    /// <param name="cancellationToken">Token used for the insert.</param>
    /// <returns>A task that completes when the row exists.</returns>
    private async Task EnsureRowExistsAsync(
        string reportKey,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var context = _contextFactory.CreateDbContext();
        if (await context.UsageReportDispatches.AsNoTracking().AnyAsync(x => x.ReportKey == reportKey, cancellationToken))
            return;

        context.UsageReportDispatches.Add(new UsageReportDispatch
        {
            ReportKey = reportKey,
            PeriodStartUtc = periodStartUtc,
            PeriodEndUtc = periodEndUtc,
            Status = UsageReportDispatchStatuses.Pending,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent process may insert the same unique ReportKey first. Verify that exact row before treating
            // the exception as a harmless race; storage failures must remain visible to the worker.
            await using var verificationContext = _contextFactory.CreateDbContext();
            if (!await verificationContext.UsageReportDispatches
                    .AsNoTracking()
                    .AnyAsync(x => x.ReportKey == reportKey, cancellationToken))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Persists a retryable or ambiguous failure state and releases the processing lease.
    /// </summary>
    /// <param name="reportKey">Stable report key.</param>
    /// <param name="status">Failure status from <see cref="UsageReportDispatchStatuses"/>.</param>
    /// <param name="error">Sanitized diagnostic text.</param>
    /// <param name="cancellationToken">Token used for the users.db update.</param>
    /// <returns>A task that completes after persistence.</returns>
    private async Task MarkFailureStateAsync(
        string reportKey,
        string status,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var safeError = string.IsNullOrWhiteSpace(error)
            ? "Unknown usage report failure."
            : error.Length <= 1800 ? error : error[..1800];
        await using var context = _contextFactory.CreateDbContext();
        await context.UsageReportDispatches
            .Where(x => x.ReportKey == reportKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, safeError)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
    }
}

/// <summary>
/// Sends one aggregate owned-plus-tenant usage dashboard after each completed Tehran week.
/// </summary>
/// <remarks>
/// The latest due Saturday boundary is evaluated immediately at startup and every minute, so a service restart after
/// 00:01 catches up the most recently completed week. Delivery uses the default owned bot and central logger channel;
/// it does not call the payment logger and therefore does not trigger database backups.
/// </remarks>
public sealed class WeeklyUsageReportHostedService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly UsageAnalyticsService _analytics;
    private readonly UsageReportChartRenderer _chartRenderer;
    private readonly UsageReportDispatchStore _dispatchStore;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly ILogger<WeeklyUsageReportHostedService> _logger;

    /// <summary>
    /// Creates the weekly report worker and its delivery dependencies.
    /// </summary>
    /// <param name="configuration">Reloadable configuration containing the enable flag and logger fallback.</param>
    /// <param name="analytics">Shared log/database usage aggregator.</param>
    /// <param name="chartRenderer">Cross-platform PNG renderer.</param>
    /// <param name="dispatchStore">Durable users.db claim and delivery store.</param>
    /// <param name="botRegistry">Registry used to select the default owned logging bot.</param>
    /// <param name="botClientProvider">Provider used to obtain that bot's Telegram client.</param>
    /// <param name="logger">Structured operational logger.</param>
    public WeeklyUsageReportHostedService(
        IConfiguration configuration,
        UsageAnalyticsService analytics,
        UsageReportChartRenderer chartRenderer,
        UsageReportDispatchStore dispatchStore,
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        ILogger<WeeklyUsageReportHostedService> logger)
    {
        _configuration = configuration;
        _analytics = analytics;
        _chartRenderer = chartRenderer;
        _dispatchStore = dispatchStore;
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _logger = logger;
    }

    /// <summary>
    /// Polls the latest due Tehran-week report and exits only when the host is stopping.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    /// <returns>A task representing the worker lifetime.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _configuration.Get<AppConfig>() ?? new AppConfig();
                if (config.WeeklyUsageReportEnabled)
                    await TrySendLatestDueReportAsync(config, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Weekly usage report worker iteration failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>
    /// Claims, builds, and sends the latest Saturday-to-Saturday report whose 00:01 due time has passed.
    /// </summary>
    /// <param name="config">Current application configuration snapshot.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A task that completes after the report is skipped, sent, or recorded for retry.</returns>
    private async Task TrySendLatestDueReportAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var nowIran = _analytics.GetIranNow();
        var periodEndIran = GetLatestDueSaturday(nowIran);
        var periodStartIran = periodEndIran.AddDays(-7);
        var reportKey = "weekly:" + periodEndIran.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var periodStartUtc = _analytics.ConvertIranTimeToUtc(periodStartIran);
        var periodEndUtc = _analytics.ConvertIranTimeToUtc(periodEndIran);

        if (!await _dispatchStore.TryClaimAsync(
                reportKey,
                periodStartUtc,
                periodEndUtc,
                cancellationToken))
        {
            return;
        }

        int? deliveredMessageId = null;
        try
        {
            var currentWeek = await _analytics.GetReportAsync(
                periodStartIran,
                7,
                botIdFilter: null,
                includeSales: true,
                cancellationToken);
            var previousWeek = await _analytics.GetReportAsync(
                periodStartIran.AddDays(-7),
                7,
                botIdFilter: null,
                includeSales: true,
                cancellationToken);
            var png = _chartRenderer.RenderWeeklyComparison(currentWeek, previousWeek);
            var caption = BuildCaption(currentWeek, previousWeek);
            var defaultBot = _botRegistry.DefaultBot
                             ?? throw new InvalidOperationException("Default owned bot is not available for weekly report delivery.");
            var loggerChannel = string.IsNullOrWhiteSpace(defaultBot.LoggerChannel)
                ? config.LoggerChannel
                : defaultBot.LoggerChannel;
            if (string.IsNullOrWhiteSpace(loggerChannel))
                throw new InvalidOperationException("Central logger channel is not configured for weekly usage reports.");

            var botClient = _botClientProvider.GetClient(defaultBot.Id);
            await using var imageStream = new MemoryStream(png, writable: false);
            var sentMessage = await botClient.SendPhotoAsync(
                chatId: new ChatId(loggerChannel),
                photo: InputFile.FromStream(imageStream, $"weekly-usage-{periodEndIran:yyyyMMdd}.png"),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (sentMessage == null || sentMessage.MessageId <= 0)
                throw new InvalidOperationException("Telegram returned no valid message for the weekly usage report.");

            deliveredMessageId = sentMessage.MessageId;
            await _dispatchStore.MarkSentAsync(reportKey, sentMessage.MessageId, cancellationToken);
            _logger.LogInformation(
                "Weekly usage report sent. reportKey={ReportKey}, messageId={MessageId}, periodStartIran={PeriodStartIran}, periodEndIran={PeriodEndIran}",
                reportKey,
                sentMessage.MessageId,
                UsageAnalyticsService.FormatPersianDate(periodStartIran),
                UsageAnalyticsService.FormatPersianDate(periodEndIran));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (deliveredMessageId.HasValue)
            {
                await TryMarkDeliveredReportForManualReconciliationAsync(
                    reportKey,
                    deliveredMessageId.Value,
                    "Host cancellation interrupted sent-state persistence after Telegram delivery.");
            }
            throw;
        }
        catch (Exception ex)
        {
            if (deliveredMessageId.HasValue)
            {
                await TryMarkDeliveredReportForManualReconciliationAsync(
                    reportKey,
                    deliveredMessageId.Value,
                    ex.Message);
                _logger.LogError(
                    ex,
                    "Weekly usage report reached Telegram but final users.db state needs reconciliation. reportKey={ReportKey}, messageId={MessageId}",
                    reportKey,
                    deliveredMessageId.Value);
            }
            else
            {
                await _dispatchStore.MarkFailedAsync(reportKey, ex.Message, CancellationToken.None);
                _logger.LogError(ex, "Weekly usage report generation or delivery failed. reportKey={ReportKey}", reportKey);
            }
        }
    }

    /// <summary>
    /// Best-effort persists a terminal duplicate-prevention state after Telegram has already accepted the report image.
    /// </summary>
    /// <param name="reportKey">Stable completed-week report key.</param>
    /// <param name="telegramMessageId">Positive Telegram message id proving that delivery occurred.</param>
    /// <param name="error">Persistence or shutdown error that prevented the normal sent transition.</param>
    /// <returns>A task that completes after the reconciliation state is saved or its failure is logged.</returns>
    /// <remarks>
    /// This helper never attempts another Telegram send. If users.db itself is unavailable, the existing processing
    /// lease remains the last guard and operators must reconcile the channel message before the lease expires.
    /// </remarks>
    private async Task TryMarkDeliveredReportForManualReconciliationAsync(
        string reportKey,
        int telegramMessageId,
        string error)
    {
        try
        {
            await _dispatchStore.MarkDeliveryRecordedWithErrorAsync(
                reportKey,
                telegramMessageId,
                error,
                CancellationToken.None);
        }
        catch (Exception persistenceException)
        {
            _logger.LogCritical(
                persistenceException,
                "Weekly usage report was delivered but duplicate-prevention state could not be persisted. reportKey={ReportKey}, messageId={MessageId}",
                reportKey,
                telegramMessageId);
        }
    }

    /// <summary>
    /// Finds the latest Saturday midnight whose 00:01 Tehran due time has passed.
    /// </summary>
    /// <param name="nowIran">Current Tehran-local time.</param>
    /// <returns>Tehran-local Saturday midnight that ends the latest due completed week.</returns>
    private static DateTime GetLatestDueSaturday(DateTime nowIran)
    {
        var daysSinceSaturday = ((int)nowIran.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        var saturday = nowIran.Date.AddDays(-daysSinceSaturday);
        if (nowIran < saturday.AddMinutes(1))
            saturday = saturday.AddDays(-7);
        return saturday;
    }

    /// <summary>
    /// Builds the HTML-safe Persian comparison caption sent with the weekly PNG.
    /// </summary>
    /// <param name="currentWeek">Latest completed week.</param>
    /// <param name="previousWeek">Preceding comparison week.</param>
    /// <returns>Telegram HTML caption containing totals, changes, and data-quality warnings.</returns>
    private static string BuildCaption(UsageAnalyticsReport currentWeek, UsageAnalyticsReport previousWeek)
    {
        var start = UsageAnalyticsService.FormatPersianDate(currentWeek.StartDateIran);
        var end = UsageAnalyticsService.FormatPersianDate(currentWeek.EndDateIran.AddDays(-1));
        var warningParts = new List<string>();
        if (currentWeek.MissingActivityLogDays > 0)
            warningParts.Add($"{currentWeek.MissingActivityLogDays} روز فایل فعالیت موجود نبود");
        if (currentWeek.MalformedLines > 0)
            warningParts.Add($"{currentWeek.MalformedLines} خط خراب نادیده گرفته شد");
        var warning = warningParts.Count == 0
            ? string.Empty
            : "\n⚠️ " + WebUtility.HtmlEncode(string.Join("؛ ", warningParts));

        return
            "📊 <b>گزارش هفتگی مصرف کل مجموعه</b>\n" +
            $"بازه: <code>{start}</code> تا <code>{end}</code>\n\n" +
            $"👤 مجموع کاربران یکتای روزانه: <code>{currentWeek.TotalDailyUniqueUsers:N0}</code> " +
            $"({BuildChangeText(currentWeek.TotalDailyUniqueUsers, previousWeek.TotalDailyUniqueUsers)})\n" +
            $"💬 تعامل‌ها: <code>{currentWeek.TotalInteractions:N0}</code> " +
            $"({BuildChangeText(currentWeek.TotalInteractions, previousWeek.TotalInteractions)})\n" +
            $"💰 فروش موفق: <code>{currentWeek.TotalSalesToman:N0}</code> تومان " +
            $"({BuildChangeText(currentWeek.TotalSalesToman, previousWeek.TotalSalesToman)})" +
            warning;
    }

    /// <summary>
    /// Formats growth or decline against a prior-period value without dividing by zero.
    /// </summary>
    /// <param name="current">Current-period non-negative total.</param>
    /// <param name="previous">Previous-period non-negative total.</param>
    /// <returns>Persian growth, decline, unchanged, or growth-from-zero text.</returns>
    private static string BuildChangeText(long current, long previous)
    {
        if (previous == 0)
            return current == 0 ? "بدون تغییر" : "رشد از صفر";

        var percent = Math.Abs((current - previous) * 100d / previous);
        if (Math.Abs(current - previous) == 0)
            return "بدون تغییر";
        return current > previous
            ? $"رشد {percent:0.#}٪ نسبت به هفته قبل"
            : $"کاهش {percent:0.#}٪ نسبت به هفته قبل";
    }

}
