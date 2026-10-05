using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Exceptions;

/// <summary>Delivers each configured storefront's completed weekly dashboard to its exact owner via Sales Assistant.</summary>
/// <remarks>
/// Starts at Tehran Saturday 00:00, the midnight ending Friday. Startup catches up only the latest completed week,
/// not older missed weeks. Disabled configured storefronts remain eligible when created before the week ended;
/// reset/unconfigured stores and stores created at/after that boundary do not receive retrospective reports.
/// No platform toggle, tenant Telegram probe or financial mutation is used. A durable SendStarted boundary prevents
/// blind resend after concurrent cycles, restart, shutdown or ambiguous HTTP delivery.
/// </remarks>
public sealed class TenantWeeklyUsageReportHostedService : BackgroundService
{
    private readonly UserDbContextFactory _contextFactory;
    private readonly UsageAnalyticsService _analytics;
    private readonly UsageReportChartRenderer _chartRenderer;
    private readonly UsageReportDispatchStore _dispatchStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BotRegistry _botRegistry;
    private readonly ILogger<TenantWeeklyUsageReportHostedService> _logger;

    /// <summary>Creates the singleton worker without capturing a scoped Sales Assistant service.</summary>
    /// <param name="contextFactory">Per-operation users.db factory for durable tenant ownership reads.</param>
    /// <param name="analytics">Shared batched tenant analytics and Tehran timezone conversion.</param>
    /// <param name="chartRenderer">Existing global-report comparison chart renderer, used unchanged.</param>
    /// <param name="dispatchStore">Shared users.db durable report state store.</param>
    /// <param name="scopeFactory">Creates an independently disposed scope for each owner delivery.</param>
    /// <param name="botRegistry">Runtime registry used only to check Sales Assistant availability.</param>
    /// <param name="logger">Structured logger; diagnostics contain codes/types, never exception messages or secrets.</param>
    public TenantWeeklyUsageReportHostedService(
        UserDbContextFactory contextFactory, UsageAnalyticsService analytics, UsageReportChartRenderer chartRenderer,
        UsageReportDispatchStore dispatchStore, IServiceScopeFactory scopeFactory, BotRegistry botRegistry,
        ILogger<TenantWeeklyUsageReportHostedService> logger)
    {
        _contextFactory = contextFactory;
        _analytics = analytics;
        _chartRenderer = chartRenderer;
        _dispatchStore = dispatchStore;
        _scopeFactory = scopeFactory;
        _botRegistry = botRegistry;
        _logger = logger;
    }

    /// <summary>Polls immediately and every minute, shortening the final delay to exact Saturday midnight.</summary>
    /// <param name="stoppingToken">Host shutdown token for reads, delivery and scheduling.</param>
    /// <returns>A task representing the hosted worker lifetime.</returns>
    /// <remarks>
    /// Computes the next scan deadline before generation, so a cycle crossing Saturday midnight immediately scans
    /// the newly completed week rather than adding another minute. The global 00:01 schedule/flag are unaffected.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIran = _analytics.GetIranNow();
            var nextScanIran = nowIran + GetNextScanDelay(nowIran);
            try { await ProcessOnceAsync(nowIran, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Tenant weekly cycle failed. failureType={FailureType}", ex.GetType().Name);
            }
            var delay = nextScanIran - _analytics.GetIranNow();
            if (delay <= TimeSpan.Zero)
                continue;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Processes the latest completed week at a supplied Tehran wall clock, independently per storefront.</summary>
    /// <param name="nowIran">Tehran-local time from analytics in production or a deterministic test clock; Kind is ignored.</param>
    /// <param name="cancellationToken">Host cancellation token; after SendStarted shutdown never releases the send for retry.</param>
    /// <returns>Number of photos positively acknowledged in this cycle, possibly zero.</returns>
    /// <remarks>
    /// Queries consumed keys once before a single batched 14-day scan. Generates each chart and verifies ownership
    /// before the atomic send barrier. The scoped assistant repeats ownership validation before its one HTTP call.
    /// Known 429/no-route outcomes may retry next cycle; permanent rejection and ambiguous outcomes cannot.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Host shutdown interrupted the cycle.</exception>
    /// <example><code>await worker.ProcessOnceAsync(new DateTime(2026, 10, 10, 0, 0, 0), token);</code></example>
    internal async Task<int> ProcessOnceAsync(DateTime nowIran, CancellationToken cancellationToken = default)
    {
        if (!_botRegistry.Bots.Any(x => x.Type == BotInstanceTypes.SalesAssistant && x.Enabled &&
                                      !string.IsNullOrWhiteSpace(x.Token)))
            return 0;
        var periodEndIran = GetLatestCompletedSaturday(nowIran);
        var endUtc = _analytics.ConvertIranTimeToUtc(periodEndIran);
        List<BotInstance> inventory;
        await using (var context = _contextFactory.CreateDbContext())
        {
            inventory = await context.BotInstances.AsNoTracking()
                .Where(x => x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId > 0 && x.TelegramBotId > 0 &&
                            x.CreatedAtUtc < endUtc && x.Token != null && x.Token != "" && x.Username != null && x.Username != "")
                .Select(x => new BotInstance
                {
                    Id = x.Id, Username = x.Username, Token = x.Token,
                    OwnerTelegramUserId = x.OwnerTelegramUserId, TelegramBotId = x.TelegramBotId
                })
                .ToListAsync(cancellationToken);
        }
        var targets = inventory.Where(x => !string.IsNullOrWhiteSpace(x.Id) &&
                                           !string.IsNullOrWhiteSpace(x.Token) && !string.IsNullOrWhiteSpace(x.Username))
            .Select(x => (Bot: x, Key: CreateReportKey(periodEndIran, x.Id, x.OwnerTelegramUserId.Value, x.TelegramBotId.Value)))
            .ToList();
        var consumed = await _dispatchStore.GetNonRetryableKeysAsync(targets.Select(x => x.Key).ToArray(), cancellationToken);
        targets.RemoveAll(x => consumed.Contains(x.Key));
        if (targets.Count == 0)
            return 0;
        var comparisons = await _analytics.GetTenantWeeklyReportsAsync(periodEndIran,
            targets.Select(x => new TenantWeeklyReportTarget(x.Bot.Id, x.Bot.OwnerTelegramUserId.Value)).ToArray(), cancellationToken);
        var startUtc = _analytics.ConvertIranTimeToUtc(periodEndIran.AddDays(-7));
        var delivered = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = false;
            int? messageId = null;
            try
            {
                var comparison = comparisons[target.Bot.Id];
                var png = _chartRenderer.RenderWeeklyComparison(comparison.CurrentWeek, comparison.PreviousWeek);
                var caption = WeeklyUsageReportContent.BuildCaption(comparison.CurrentWeek, comparison.PreviousWeek,
                    target.Bot.Username, target.Bot.Id);
                await using var image = new MemoryStream(png, writable: false);
                using var scope = _scopeFactory.CreateScope();
                var assistant = scope.ServiceProvider.GetRequiredService<SalesAssistantService>();
                await using (var context = _contextFactory.CreateDbContext())
                {
                    // Match all three captured identities, not just an owner who may own several stores.
                    var current = await context.BotInstances.AsNoTracking().Where(x =>
                        x.Id == target.Bot.Id && x.Type == BotInstanceTypes.Tenant &&
                        x.OwnerTelegramUserId == target.Bot.OwnerTelegramUserId &&
                        x.TelegramBotId == target.Bot.TelegramBotId)
                        .Select(x => new { x.Token, x.Username }).SingleOrDefaultAsync(cancellationToken);
                    if (current == null || string.IsNullOrWhiteSpace(current.Token) || string.IsNullOrWhiteSpace(current.Username))
                        continue;
                }
                started = await _dispatchStore.TryStartTenantSendAsync(target.Key, startUtc, endUtc, cancellationToken);
                if (!started)
                    continue;
                messageId = await assistant.SendTenantWeeklyReportPhotoAsync(target.Bot.Id,
                    target.Bot.OwnerTelegramUserId.Value, target.Bot.TelegramBotId.Value, image, caption, cancellationToken);
                if (!messageId.HasValue)
                {
                    await RecordOutcomeAsync(target.Key, UsageReportDispatchStatuses.Failed, "assistant_route_or_target_unavailable");
                    continue;
                }
                await _dispatchStore.MarkSentAsync(target.Key, messageId.Value, cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (messageId.HasValue)
                    await RecordKnownDeliveryAsync(target.Key, messageId.Value, "host_cancelled_after_delivery");
                // If HTTP may have started, retain SendStarted: cancellation cannot prove that nothing was sent.
                throw;
            }
            catch (Exception ex)
            {
                var code = ex is ApiRequestException api ? "telegram_" + api.ErrorCode.ToString(CultureInfo.InvariantCulture) : ex.GetType().Name;
                if (messageId.HasValue)
                    await RecordKnownDeliveryAsync(target.Key, messageId.Value, code);
                else if (started)
                {
                    var status = ex is ApiRequestException { ErrorCode: 429 } ? UsageReportDispatchStatuses.Failed :
                        ex is ApiRequestException { ErrorCode: >= 400 and < 500 and not 408 } ? UsageReportDispatchStatuses.Rejected :
                        UsageReportDispatchStatuses.DeliveryUncertain;
                    await RecordOutcomeAsync(target.Key, status, code);
                }
                _logger.LogWarning("Tenant weekly target failed. reportKey={ReportKey}, failureCode={FailureCode}", target.Key, code);
            }
        }
        return delivered;
    }

    /// <summary>Finds Saturday 00:00 at or before a Tehran wall clock, with no one-minute offset.</summary>
    /// <param name="nowIran">Tehran-local date/time, including exactly midnight.</param>
    /// <returns>Exclusive end of the latest completed Saturday-through-Friday week.</returns>
    /// <example><code>var end = GetLatestCompletedSaturday(new DateTime(2026, 10, 10));</code></example>
    internal static DateTime GetLatestCompletedSaturday(DateTime nowIran)
    {
        var daysSinceSaturday = ((int)nowIran.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        return DateTime.SpecifyKind(nowIran.Date.AddDays(-daysSinceSaturday), DateTimeKind.Unspecified);
    }

    /// <summary>Aligns a minute poll to the upcoming Tehran Saturday boundary instead of drifting to 00:01.</summary>
    /// <param name="nowIran">Current Tehran wall clock at the start of the cycle, before report generation.</param>
    /// <returns>Positive delay no longer than one minute, shortened when midnight is nearer.</returns>
    /// <example><code>await Task.Delay(GetNextScanDelay(analytics.GetIranNow()), token);</code></example>
    internal static TimeSpan GetNextScanDelay(DateTime nowIran)
    {
        var remaining = GetLatestCompletedSaturday(nowIran).AddDays(7) - DateTime.SpecifyKind(nowIran, DateTimeKind.Unspecified);
        return remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1);
    }

    /// <summary>Builds a bounded dispatch identity without storing raw owner ids or storefront credentials in its key.</summary>
    /// <param name="periodEndIran">Completed Saturday midnight identifying the week.</param>
    /// <param name="botId">Exact stable internal users.db storefront id, never normalized.</param>
    /// <param name="ownerId">Positive persisted Telegram owner user id.</param>
    /// <param name="telegramBotId">Positive persisted BotFather storefront identity.</param>
    /// <returns>A deterministic 54-character unique-purpose key within the existing 64-character database limit.</returns>
    /// <remarks>Length-prefixing the bot id makes the hash input unambiguous. Separate stores for one owner never share a key.</remarks>
    /// <example><code>var key = CreateReportKey(end, tenant.Id, ownerId, identity);</code></example>
    internal static string CreateReportKey(DateTime periodEndIran, string botId, long ownerId, long telegramBotId)
    {
        var identity = string.Create(CultureInfo.InvariantCulture, $"{botId.Length}:{botId}:{ownerId}:{telegramBotId}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return "tenantweekly:" + periodEndIran.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ":" + hash;
    }

    /// <summary>Persists a tenant send failure, retaining SendStarted when the database itself is unavailable.</summary>
    /// <param name="key">Exact bounded dispatch key whose barrier was crossed.</param>
    /// <param name="status">Retryable definite failure, permanent rejection or delivery uncertainty.</param>
    /// <param name="code">Sanitized diagnostic code or exception type.</param>
    /// <returns>A task completing after best-effort persistence without stopping other tenants.</returns>
    private async Task RecordOutcomeAsync(string key, string status, string code)
    {
        try { await _dispatchStore.MarkTenantSendOutcomeAsync(key, status, code, CancellationToken.None); }
        catch (Exception ex)
        {
            _logger.LogWarning("Tenant weekly outcome persistence failed. reportKey={ReportKey}, failureType={FailureType}", key, ex.GetType().Name);
        }
    }

    /// <summary>Records a known delivered message when the normal Sent update failed, preventing duplicate delivery.</summary>
    /// <param name="key">Exact bounded dispatch key.</param>
    /// <param name="messageId">Positive Telegram acknowledgement already observed.</param>
    /// <param name="code">Sanitized persistence-failure type or shutdown code.</param>
    /// <returns>A task completing after best-effort reconciliation persistence; SendStarted remains safe on failure.</returns>
    private async Task RecordKnownDeliveryAsync(string key, int messageId, string code)
    {
        try { await _dispatchStore.MarkDeliveryRecordedWithErrorAsync(key, messageId, code, CancellationToken.None); }
        catch (Exception ex)
        {
            _logger.LogWarning("Tenant weekly reconciliation persistence failed. reportKey={ReportKey}, failureType={FailureType}", key, ex.GetType().Name);
        }
    }
}
