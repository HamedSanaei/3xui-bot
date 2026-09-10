using System.Net;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

public sealed class TenantStorefrontFundingAlertService
{
    private static readonly AsyncKeyedGate Storefronts = new();
    private readonly UserDbContextFactory _factory;
    private readonly int _cooldownMinutes;
    private readonly ILogger<TenantStorefrontFundingAlertService> _logger;

    public TenantStorefrontFundingAlertService(UserDbContextFactory factory, IConfiguration configuration,
        ILogger<TenantStorefrontFundingAlertService> logger)
    {
        _factory = factory;
        _cooldownMinutes = (configuration.Get<AppConfig>() ?? new AppConfig()).TenantUnderfundedCustomerAttemptNotificationCooldownMinutes;
        _logger = logger;
    }

    public async Task ObserveAsync(BotInstance tenant, TenantAccessEvaluation evaluation, bool customerAttempt,
        bool allowUnderfundedAlerts, CancellationToken cancellationToken)
    {
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.Id) || evaluation == null) return;
        using var gate = await Storefronts.EnterAsync(tenant.Id, cancellationToken);
        var now = DateTime.UtcNow;
        var queued = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var state = await db.TenantStorefrontFundingAlertStates.SingleOrDefaultAsync(x => x.TenantBotId == tenant.Id, ct);

            if (evaluation.Decision == TenantAccessDecision.Allowed)
            {
                if (state != null)
                {
                    state.IsUnderfunded = false;
                    state.UnderfundedSinceUtc = null;
                    state.UnderfundedEpisodeNotifiedAtUtc = null;
                    state.LastCustomerAttemptAlertAtUtc = null;
                    ApplySnapshot(state, evaluation, now);
                    await db.SaveChangesAsync(ct);
                }
                // Funding recovered: obsolete alerts from the previous underfunded episode must never reach the owner.
                // Pending rows never started a Telegram request. Processing rows whose send never started are also
                // safe to cancel: the worker's conditional send-start update still requires Status == Processing, so a
                // live claim simply stops without sending. Possible-send rows are left untouched and stay conservative.
                await CancelSupersededAlertsAsync(db, tenant.Id, now, ct);
                await transaction.CommitAsync(ct);
                return (false, false);
            }

            if (evaluation.Decision != TenantAccessDecision.InsufficientFunding || !allowUnderfundedAlerts)
            {
                if (state != null)
                {
                    ApplySnapshot(state, evaluation, now);
                    await db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return (false, false);
            }

            if (state == null)
            {
                state = new TenantStorefrontFundingAlertState { TenantBotId = tenant.Id };
                db.TenantStorefrontFundingAlertStates.Add(state);
            }

            var transition = !state.IsUnderfunded;
            var transitionAlertQueued = false;
            var customerAlertQueued = false;
            if (transition)
            {
                state.IsUnderfunded = true;
                state.EpisodeNumber++;
                state.UnderfundedSinceUtc = now;
                state.UnderfundedEpisodeNotifiedAtUtc = now;
                if (customerAttempt)
                {
                    state.LastCustomerAttemptAlertAtUtc = now;
                    db.TenantStorefrontFundingAlerts.Add(BuildAlert(tenant, evaluation,
                        TenantStorefrontFundingAlertKinds.CustomerAttempt, state.EpisodeNumber,
                        $"tenant-funding:{tenant.Id}:episode:{state.EpisodeNumber}:customer-entry", now));
                    customerAlertQueued = true;
                }
                else
                {
                    db.TenantStorefrontFundingAlerts.Add(BuildAlert(tenant, evaluation,
                        TenantStorefrontFundingAlertKinds.UnderfundedTransition, state.EpisodeNumber,
                        $"tenant-funding:{tenant.Id}:episode:{state.EpisodeNumber}:transition", now));
                    transitionAlertQueued = true;
                }
            }
            else if (customerAttempt &&
                     (!state.LastCustomerAttemptAlertAtUtc.HasValue ||
                      state.LastCustomerAttemptAlertAtUtc.Value <= now.AddMinutes(-_cooldownMinutes)))
            {
                state.LastCustomerAttemptAlertAtUtc = now;
                db.TenantStorefrontFundingAlerts.Add(BuildAlert(tenant, evaluation,
                    TenantStorefrontFundingAlertKinds.CustomerAttempt, state.EpisodeNumber,
                    $"tenant-funding:{tenant.Id}:attempt:{now.Ticks}", now));
                customerAlertQueued = true;
            }

            ApplySnapshot(state, evaluation, now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (transitionAlertQueued, customerAlertQueued);
        }, cancellationToken);

        if (queued.Item1)
            _logger.LogInformation("Tenant storefront became underfunded. tenantBotId={TenantBotId} ownerTelegramUserId={OwnerTelegramUserId} botBalanceToman={BotBalanceToman} siteWalletToman={SiteWalletToman} minimumSiteWalletToman={MinimumSiteWalletToman}",
                tenant.Id, tenant.OwnerTelegramUserId, evaluation.BotBalanceToman, evaluation.SiteWalletToman, evaluation.MinimumSiteWalletToman);
        if (queued.Item2)
            _logger.LogInformation("Underfunded tenant storefront customer-attempt alert queued. tenantBotId={TenantBotId} ownerTelegramUserId={OwnerTelegramUserId} cooldownMinutes={CooldownMinutes}",
                tenant.Id, tenant.OwnerTelegramUserId, _cooldownMinutes);
        if (queued.Item1 || queued.Item2) TenantStorefrontFundingAlertWorker.Wake();
    }

    public async Task ObserveSettlementAsync(BotInstance tenant, TenantAccessEvaluation before,
        TenantAccessEvaluation after, CancellationToken cancellationToken)
    {
        if (before?.Decision == TenantAccessDecision.Allowed && after?.Decision == TenantAccessDecision.InsufficientFunding)
            await ObserveAsync(tenant, before, false, true, cancellationToken);
        if (after != null)
            await ObserveAsync(tenant, after, false, true, cancellationToken);
    }

    private static void ApplySnapshot(TenantStorefrontFundingAlertState state, TenantAccessEvaluation evaluation, DateTime now)
    {
        state.LastObservedBotBalanceToman = evaluation.BotBalanceToman ?? 0;
        state.LastObservedSiteWalletToman = evaluation.SiteWalletToman;
        state.UpdatedAtUtc = now;
    }

    private static TenantStorefrontFundingAlert BuildAlert(BotInstance tenant, TenantAccessEvaluation evaluation,
        string kind, int episodeNumber, string businessKey, DateTime now) => new()
    {
        BusinessKey = businessKey,
        TenantBotId = tenant.Id,
        OwnerTelegramUserId = tenant.OwnerTelegramUserId ?? 0,
        TenantBotUsername = tenant.Username ?? tenant.Id,
        Kind = kind,
        EpisodeNumber = episodeNumber,
        BotBalanceToman = evaluation.BotBalanceToman ?? 0,
        SiteWalletToman = evaluation.SiteWalletToman,
        MinimumSiteWalletToman = evaluation.MinimumSiteWalletToman,
        Status = TenantStorefrontFundingAlertStatuses.Pending,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    /// <summary>
    /// Terminates obsolete funding alerts for a storefront that just became funded, inside the recovery transaction.
    /// </summary>
    /// <param name="db">Users.db context participating in the caller's active transaction.</param>
    /// <param name="tenantBotId">Storefront bot id whose alerts are superseded.</param>
    /// <param name="now">Current UTC timestamp written to the cancelled rows.</param>
    /// <param name="ct">Transaction cancellation token.</param>
    /// <returns>A task completing when the cancellation updates are applied.</returns>
    /// <remarks>
    /// Only rows that provably never started a Telegram request are cancelled: Pending rows and Processing rows with
    /// <see cref="TenantStorefrontFundingAlert.SendStartedAtUtc"/> still null. Possible-send rows keep their
    /// conservative outcome so delivery uncertainty is never silently converted into a false "not sent".
    /// </remarks>
    private static async Task CancelSupersededAlertsAsync(UserDbContext db, string tenantBotId, DateTime now, CancellationToken ct)
    {
        await db.TenantStorefrontFundingAlerts
            .Where(x => x.TenantBotId == tenantBotId && x.Status == TenantStorefrontFundingAlertStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Cancelled)
                .SetProperty(x => x.LastError, "superseded_by_funding_recovery")
                .SetProperty(x => x.UpdatedAtUtc, now), ct);
        await db.TenantStorefrontFundingAlerts
            .Where(x => x.TenantBotId == tenantBotId && x.Status == TenantStorefrontFundingAlertStatuses.Processing
                        && x.SendStartedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Cancelled)
                .SetProperty(x => x.LastError, "superseded_by_funding_recovery")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), ct);
    }
}

internal interface ITenantStorefrontFundingAlertSender
{
    Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken);
}

internal sealed class TenantStorefrontFundingAlertSender(IServiceScopeFactory scopeFactory) : ITenantStorefrontFundingAlertSender
{
    public async Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TenantStorefrontFundingAlertDeliveryService>()
            .SendAsync(alert, cancellationToken);
    }
}

public sealed class TenantStorefrontFundingAlertDeliveryService
{
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly CredentialsStore _credentials;

    public TenantStorefrontFundingAlertDeliveryService(BotRegistry botRegistry, BotClientProvider botClientProvider,
        CredentialsStore credentials)
    {
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _credentials = credentials;
    }

    public async Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
    {
        var bot = _botRegistry.DefaultBot;
        if (bot == null || string.IsNullOrWhiteSpace(bot.Token)) return null;
        var owner = await _credentials.GetUserStatusWithId(alert.OwnerTelegramUserId);
        var chatId = owner?.ChatID > 0 ? owner.ChatID : alert.OwnerTelegramUserId;
        var text = BuildMessage(alert);
        return (await _botClientProvider.GetClient(bot.Id).SendTextMessageAsync(chatId, text,
            parseMode: ParseMode.Html, cancellationToken: cancellationToken)).MessageId;
    }

    internal static string BuildMessage(TenantStorefrontFundingAlert alert)
    {
        var username = (alert.TenantBotUsername ?? alert.TenantBotId).Trim().TrimStart('@');
        var minimum = alert.MinimumSiteWalletToman.FormatCurrency();
        var botBalance = alert.BotBalanceToman.FormatCurrency();
        var siteBalance = alert.SiteWalletToman.HasValue
            ? alert.SiteWalletToman.Value.FormatCurrency()
            : "نامشخص / در دسترس نیست";
        if (alert.Kind == TenantStorefrontFundingAlertKinds.CustomerAttempt)
            return "🛒 <b>یک مشتری در حال تلاش برای استفاده از ربات فروشگاهی شماست.</b>\n\n" +
                   $"🤖 فروشگاه: <code>@{Html(username)}</code>\n\n" +
                   "در حال حاضر به دلیل موجودی ناکافی، فروشگاه امکان ارائه سرویس ندارد.\n\n" +
                   "برای فعال شدن مجدد:\n" +
                   "• موجودی حساب ربات خود را افزایش دهید\n" +
                   $"• یا کیف پول گذرگاه را حداقل به <code>{Html(minimum)}</code> برسانید.\n\n" +
                   "لطفاً برای جلوگیری از از دست رفتن فروش، موجودی را شارژ کنید.";
        if (alert.Kind != TenantStorefrontFundingAlertKinds.UnderfundedTransition)
            throw new InvalidOperationException("unknown_storefront_funding_alert_kind");
        return "⚠️ <b>ربات فروشگاهی شما به دلیل موجودی ناکافی غیرفعال شده است.</b>\n\n" +
               $"🤖 فروشگاه: <code>@{Html(username)}</code>\n\n" +
               "برای فعال شدن مجدد ربات، یکی از شرایط زیر را فراهم کنید:\n\n" +
               "• موجودی حساب شما در ربات را افزایش دهید.\n" +
               $"• یا موجودی کیف پول سایت گذرگاه را به حداقل <code>{Html(minimum)}</code> برسانید.\n\n" +
               $"💳 موجودی فعلی ربات: <code>{Html(botBalance)}</code>\n" +
               $"🌐 موجودی فعلی گذرگاه: <code>{Html(siteBalance)}</code>\n\n" +
               "پس از تأمین موجودی، دسترسی فروشگاه به‌صورت خودکار طبق قوانین فعلی سیستم برقرار خواهد شد.";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

public sealed class TenantStorefrontFundingAlertWorker : BackgroundService
{
    private const int MaximumAttempts = 6;
    private const int MaximumBatchSize = 10;
    private const int MaximumCleanupBatch = 100;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim WakeSignal = new(0, 1);
    private readonly UserDbContextFactory _factory;
    private readonly ITenantStorefrontFundingAlertSender _sender;
    private readonly int _retentionDays;
    private readonly ILogger<TenantStorefrontFundingAlertWorker> _logger;

    public TenantStorefrontFundingAlertWorker(UserDbContextFactory factory, IServiceScopeFactory scopeFactory,
        IConfiguration configuration, ILogger<TenantStorefrontFundingAlertWorker> logger)
        : this(factory, new TenantStorefrontFundingAlertSender(scopeFactory), logger,
            (configuration.Get<AppConfig>() ?? new AppConfig()).TenantStorefrontFundingAlertRetentionDays) { }

    internal TenantStorefrontFundingAlertWorker(UserDbContextFactory factory, ITenantStorefrontFundingAlertSender sender,
        ILogger<TenantStorefrontFundingAlertWorker> logger, int retentionDays = 30)
    {
        _factory = factory; _sender = sender; _retentionDays = retentionDays; _logger = logger;
    }

    internal static void Wake()
    {
        try { WakeSignal.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError("Tenant storefront funding alert scan failed. ErrorType={ErrorType}", ex.GetType().Name); }
            try { await WakeSignal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        await RecoverExpiredClaimsAsync(cancellationToken);
        await CompactDeliveredAsync(cancellationToken);
        var rows = await ClaimDueBatchAsync(cancellationToken);
        foreach (var row in rows) await DeliverAsync(row, cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// Resolves expired processing leases using the durable send phase so a pre-send crash retries instead of
    /// becoming DeliveryUncertain.
    /// </summary>
    /// <param name="cancellationToken">Scan cancellation token.</param>
    /// <returns>A task completing after both recovery updates are applied.</returns>
    /// <remarks>
    /// An expired claim whose <see cref="TenantStorefrontFundingAlert.SendStartedAtUtc"/> is null proves no Telegram
    /// request was started, so the row returns to Pending with an immediate retry. An expired claim whose send phase
    /// is set may have invoked Telegram, so it is conservatively marked DeliveryUncertain and never replayed.
    /// </remarks>
    internal async Task RecoverExpiredClaimsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        await db.TenantStorefrontFundingAlerts
            .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.LeaseUntilUtc <= now
                        && x.SendStartedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Pending)
                .SetProperty(x => x.NextAttemptAtUtc, now)
                .SetProperty(x => x.LastError, "processing_lease_expired_before_send")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        await db.TenantStorefrontFundingAlerts
            .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.LeaseUntilUtc <= now
                        && x.SendStartedAtUtc != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.DeliveryUncertain)
                .SetProperty(x => x.LastError, "processing_lease_expired_after_possible_delivery")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    /// <summary>
    /// Deletes Delivered funding alerts older than the configured retention, at most one bounded batch per cycle.
    /// </summary>
    /// <param name="cancellationToken">Scan cancellation token.</param>
    /// <returns>The number of deleted rows, at most <see cref="MaximumCleanupBatch"/>.</returns>
    /// <remarks>
    /// Only rows that are Delivered, delivered before the cutoff, and free of any claim or lease are candidates.
    /// Pending, Processing, DeliveryUncertain, ManualReview, and Cancelled rows are never deleted automatically.
    /// </remarks>
    internal async Task<int> CompactDeliveredAsync(CancellationToken cancellationToken = default)
    {
        if (_retentionDays <= 0)
            return 0;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Min(_retentionDays, 36500));
        return await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var candidates = db.TenantStorefrontFundingAlerts
                .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Delivered &&
                            x.DeliveredAtUtc != null && x.DeliveredAtUtc < cutoff &&
                            x.ClaimToken == null && x.LeaseUntilUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).Take(MaximumCleanupBatch);
            return await db.TenantStorefrontFundingAlerts.Where(x => candidates.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }, cancellationToken);
    }

    private async Task<List<TenantStorefrontFundingAlert>> ClaimDueBatchAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        var ids = await db.TenantStorefrontFundingAlerts.AsNoTracking()
            .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Pending &&
                        (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now))
            .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.Id).Select(x => x.Id).Take(MaximumBatchSize)
            .ToListAsync(cancellationToken);
        var claimed = new List<TenantStorefrontFundingAlert>();
        foreach (var id in ids)
        {
            var token = Guid.NewGuid().ToString("N");
            var updated = await db.TenantStorefrontFundingAlerts
                .Where(x => x.Id == id && x.Status == TenantStorefrontFundingAlertStatuses.Pending &&
                            (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Processing)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.ClaimToken, token)
                    .SetProperty(x => x.LeaseUntilUtc, now.Add(ClaimLease))
                    .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            if (updated == 1)
                claimed.Add(await db.TenantStorefrontFundingAlerts.AsNoTracking().SingleAsync(x => x.Id == id && x.ClaimToken == token, cancellationToken));
        }
        return claimed;
    }

    /// <summary>
    /// Persists the durable send-start phase immediately before the Telegram transport is invoked.
    /// </summary>
    /// <param name="alert">The claimed, detached alert row being delivered.</param>
    /// <param name="cancellationToken">Scan cancellation token.</param>
    /// <returns>
    /// <c>true</c> only when exactly one row was updated; <c>false</c> when the claim was cancelled or superseded,
    /// in which case the caller must not invoke Telegram.
    /// </returns>
    /// <remarks>
    /// The conditional update verifies Id, Processing status, the exact claim token, and that the send phase is still
    /// null. A storefront recovery that cancelled the claim between admission and here makes the update affect zero
    /// rows, which prevents a stale alert from ever reaching the owner. The tiny crash window after this persist and
    /// before the HTTP call is acceptable: the row may become DeliveryUncertain but is never replayed.
    /// </remarks>
    internal async Task<bool> MarkSendStartedAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        var updated = await db.TenantStorefrontFundingAlerts
            .Where(x => x.Id == alert.Id && x.Status == TenantStorefrontFundingAlertStatuses.Processing
                        && x.ClaimToken == alert.ClaimToken && x.SendStartedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SendStartedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Verifies the storefront is still underfunded in the same episode before a queued alert may be delivered.
    /// </summary>
    /// <param name="alert">The claimed, detached alert row being delivered.</param>
    /// <param name="cancellationToken">Scan cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the storefront state exists, is underfunded, and its episode matches the alert (a zero episode
    /// marks a legacy row persisted before episode tracking and is accepted while underfunded).
    /// </returns>
    /// <remarks>
    /// This is the second line of defense after the recovery-time cancellation: rows that were cancelled while Pending
    /// are never claimed, and rows cancelled while Processing fail the <see cref="MarkSendStartedAsync"/> conditional.
    /// </remarks>
    private async Task<bool> IsCurrentEpisodeAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        var state = await db.TenantStorefrontFundingAlertStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantBotId == alert.TenantBotId, cancellationToken);
        if (state == null || !state.IsUnderfunded) return false;
        return alert.EpisodeNumber == 0 || alert.EpisodeNumber == state.EpisodeNumber;
    }

    private async Task DeliverAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsCurrentEpisodeAsync(alert, cancellationToken))
            {
                await MarkCancelledAsync(alert, "superseded_by_funding_recovery", cancellationToken);
                return;
            }
            if (!await MarkSendStartedAsync(alert, cancellationToken)) return;
            var messageId = await _sender.SendAsync(alert, cancellationToken);
            if (messageId.HasValue) { await MarkDeliveredAsync(alert, messageId.Value, cancellationToken); return; }
            await RetryAsync(alert, "owner_transport_unavailable", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429) { await RetryAsync(alert, "telegram_api_429", cancellationToken); }
        catch (BotTransportUnavailableException) { await RetryAsync(alert, "bot_transport_unavailable", cancellationToken); }
        catch (ApiRequestException ex) when (ex.ErrorCode >= 500) { await MarkUncertainAsync(alert, $"telegram_api_{ex.ErrorCode}", cancellationToken); }
        catch (ApiRequestException ex) { await MarkManualReviewAsync(alert, $"telegram_api_{ex.ErrorCode}", cancellationToken); }
        catch (Exception ex) when (ex is Telegram.Bot.Exceptions.RequestException or TimeoutException or HttpRequestException or TaskCanceledException)
        { await MarkUncertainAsync(alert, "telegram_send_outcome_uncertain", cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning("Tenant storefront funding alert outcome became uncertain. tenantBotId={TenantBotId} kind={Kind} ErrorType={ErrorType}", alert.TenantBotId, alert.Kind, ex.GetType().Name);
            await MarkUncertainAsync(alert, "unexpected_send_outcome_uncertain", cancellationToken);
        }
    }

    private async Task MarkDeliveredAsync(TenantStorefrontFundingAlert alert, int messageId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        await db.TenantStorefrontFundingAlerts.Where(x => x.Id == alert.Id && x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.ClaimToken == alert.ClaimToken)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Delivered)
                .SetProperty(x => x.TelegramMessageId, messageId).SetProperty(x => x.DeliveredAtUtc, now)
                .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, (string)null).SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null).SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    private async Task RetryAsync(TenantStorefrontFundingAlert alert, string error, CancellationToken cancellationToken)
    {
        if (alert.AttemptCount >= MaximumAttempts) { await MarkManualReviewAsync(alert, error, cancellationToken); return; }
        var seconds = Math.Min(MaximumRetryDelay.TotalSeconds, InitialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, alert.AttemptCount - 1)));
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        await db.TenantStorefrontFundingAlerts.Where(x => x.Id == alert.Id && x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.ClaimToken == alert.ClaimToken)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Pending)
                .SetProperty(x => x.NextAttemptAtUtc, now.AddSeconds(seconds)).SetProperty(x => x.LastError, error)
                .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                .SetProperty(x => x.ClaimToken, (string)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    private Task MarkManualReviewAsync(TenantStorefrontFundingAlert alert, string error, CancellationToken cancellationToken) =>
        MarkTerminalAsync(alert, TenantStorefrontFundingAlertStatuses.ManualReview, error, cancellationToken);
    private Task MarkUncertainAsync(TenantStorefrontFundingAlert alert, string error, CancellationToken cancellationToken) =>
        MarkTerminalAsync(alert, TenantStorefrontFundingAlertStatuses.DeliveryUncertain, error, cancellationToken);
    private Task MarkCancelledAsync(TenantStorefrontFundingAlert alert, string error, CancellationToken cancellationToken) =>
        MarkTerminalAsync(alert, TenantStorefrontFundingAlertStatuses.Cancelled, error, cancellationToken);

    private async Task MarkTerminalAsync(TenantStorefrontFundingAlert alert, string status, string error, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        await db.TenantStorefrontFundingAlerts.Where(x => x.Id == alert.Id && x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.ClaimToken == alert.ClaimToken)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.LastError, error)
                .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                .SetProperty(x => x.ClaimToken, (string)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null).SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }
}
