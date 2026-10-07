using System.Net;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

/// <summary>Persists owner-scoped underfunding episodes and durable notifications while storefront customer access remains available.</summary>
/// <remarks>
/// Classifies episodes by Decision, not IsAllowed: InsufficientFunding is accessible but still requires central
/// payments and owner funding alerts. Cooldowns, recovery cancellation, and delivery idempotency are unchanged.
/// </remarks>
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

    /// <summary>Observes the storefront's financial mode and queues an owner alert without blocking customer navigation.</summary>
    /// <param name="tenant">Resolved tenant storefront; its internal id scopes episodes and its stored owner id addresses notifications.</param>
    /// <param name="evaluation">Snapshot for this exact stored owner; null is ignored and must never be borrowed from another owner.</param>
    /// <param name="customerAttempt">Whether an accessible customer interaction should trigger the persisted attempt cooldown.</param>
    /// <param name="allowUnderfundedAlerts">Whether this enabled, usable storefront may queue financial alerts; false suppresses new alerts.</param>
    /// <param name="cancellationToken">Cancellation for storefront admission and durable database writes.</param>
    /// <returns>A task completing after state and any notification intent are committed; no Telegram send occurs inline.</returns>
    /// <remarks>
    /// Allowed ends an underfunded episode; InsufficientFunding tracks central-payment fallback even though IsAllowed
    /// is true. Blocked or missing owners do not queue funding alerts. Customer cooldowns and transition business keys
    /// remain durable; recovery cancels only superseded alerts whose send has not started. No wallet, order, or payment
    /// changes are made, and the owner's notification transport does not hold the customer handler lane.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Storefront admission or database work is cancelled.</exception>
    /// <exception cref="DbUpdateException">Funding state or notification intent cannot be persisted.</exception>
    /// <example><code>await alerts.ObserveAsync(tenant, evaluation, customerAttempt: true, allowUnderfundedAlerts: true, token);</code></example>
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

    /// <summary>Observes post-settlement funding recovery or entry into central-payment fallback independently of monitoring.</summary>
    /// <param name="tenant">Settled tenant storefront whose internal id and exact stored owner scope notification state.</param>
    /// <param name="before">Optional pre-settlement evaluation for this same owner.</param>
    /// <param name="after">Optional post-settlement evaluation for this same owner; null produces no post-settlement observation.</param>
    /// <param name="cancellationToken">Cancellation for durable episode and alert writes.</param>
    /// <returns>A task completing the existing financial transition observations, without inline Telegram delivery.</returns>
    /// <remarks>Both funding decisions permit access; their distinction still drives durable alert episodes and recovery cancellation. No repayment or wallet mutation occurs here.</remarks>
    /// <exception cref="OperationCanceledException">The financial observation is cancelled.</exception>
    /// <exception cref="DbUpdateException">The financial observation cannot be persisted.</exception>
    /// <example><code>await alerts.ObserveSettlementAsync(tenant, before, after, token);</code></example>
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
    private readonly TenantOwnerNotificationTransportResolver _ownerTransportResolver;
    private readonly UserDbContextFactory _factory;
    private readonly CredentialsStore _credentials;

    public TenantStorefrontFundingAlertDeliveryService(
        TenantOwnerNotificationTransportResolver ownerTransportResolver,
        UserDbContextFactory factory,
        CredentialsStore credentials)
    {
        _ownerTransportResolver = ownerTransportResolver;
        _factory = factory;
        _credentials = credentials;
    }

    public async Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        var tenant = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == alert.TenantBotId && x.Type == BotInstanceTypes.Tenant &&
            x.OwnerTelegramUserId == alert.OwnerTelegramUserId, cancellationToken);
        if (tenant == null)
            throw new OwnerNotificationTransportUnavailableException();
        var resolved = await _ownerTransportResolver.ResolveClientAsync(tenant, alert.OwnerTelegramUserId, cancellationToken);
        var owner = await _credentials.GetUserStatusWithId(alert.OwnerTelegramUserId);
        var chatId = owner?.ChatID > 0 ? owner.ChatID : alert.OwnerTelegramUserId;
        var text = BuildMessage(alert);
        return (await resolved.Client.SendMessage(chatId, text,
            parseMode: ParseMode.Html, cancellationToken: cancellationToken)).MessageId;
    }

    /// <summary>Builds the owner-facing explanation of active storefront central-payment fallback and funding recovery.</summary>
    /// <param name="alert">Required durable financial alert with tenant username and owner-wallet snapshots in toman.</param>
    /// <returns>Persian Telegram HTML with escaped storefront identifiers and configured threshold, never a suspension or lost-sale claim.</returns>
    /// <remarks>
    /// Both alert kinds explain the same financial mode: only live central owned-bot gateways admit new payments,
    /// saved provider opt-outs are ignored, and personal-card admission pauses. Central gateway owner profit credits
    /// the shared bot wallet and reduces negative debt; the unchanged OR funding rule restores saved preferences.
    /// Formatting has no financial or durable-delivery side effects.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The persisted alert kind is not a supported financial notification.</exception>
    /// <example><code>var text = TenantStorefrontFundingAlertDeliveryService.BuildMessage(alert);</code></example>
    internal static string BuildMessage(TenantStorefrontFundingAlert alert)
    {
        var username = (alert.TenantBotUsername ?? alert.TenantBotId).Trim().TrimStart('@');
        var minimum = alert.MinimumSiteWalletToman.FormatCurrency();
        var botBalance = alert.BotBalanceToman.FormatCurrency();
        var siteBalance = alert.SiteWalletToman.HasValue
            ? alert.SiteWalletToman.Value.FormatCurrency()
            : "نامشخص / در دسترس نیست";
        var heading = alert.Kind switch
        {
            TenantStorefrontFundingAlertKinds.CustomerAttempt =>
                "🛒 <b>یک مشتری در حال استفاده از ربات فروشگاهی شماست.</b>\n\n",
            TenantStorefrontFundingAlertKinds.UnderfundedTransition =>
                "⚠️ <b>ربات فروشگاهی شما با درگاه‌های مرکزی فعال می‌ماند.</b>\n\n",
            _ => throw new InvalidOperationException("unknown_storefront_funding_alert_kind")
        };
        return heading +
               $"🤖 فروشگاه: <code>@{Html(username)}</code>\n\n" +
               "به دلیل موجودی ناکافی، ربات همچنان فعال است و پرداخت‌های جدید فقط از درگاه‌های مرکزی فعال ربات‌های اصلی انجام می‌شوند؛ تنظیمات خاموش‌کردن این درگاه‌ها در فروشگاه موقتاً اعمال نمی‌شوند.\n" +
               "کارت‌به‌کارت شخصی تا زمان تأمین موجودی موقتاً در دسترس نیست.\n\n" +
               "سود مالک از فروش‌های درگاه مرکزی به کیف پول مشترک شما در ربات اضافه می‌شود و بدهی منفی را کاهش می‌دهد.\n\n" +
               "برای بازگشت خودکار به روش‌های پرداخت ذخیره‌شده، یکی از شرایط زیر کافی است:\n" +
               "• موجودی کیف پول شما در ربات بیشتر از صفر باشد.\n" +
               $"• یا موجودی قابل‌استفاده کیف پول سایت گذرگاه حداقل <code>{Html(minimum)}</code> باشد.\n\n" +
               $"💳 موجودی فعلی ربات: <code>{Html(botBalance)}</code>\n" +
               $"🌐 موجودی فعلی گذرگاه: <code>{Html(siteBalance)}</code>\n\n" +
               "پس از تأمین موجودی، تنظیمات ذخیره‌شده درگاه‌ها و کارت‌به‌کارت شخصی، در صورت فعال بودن، به‌صورت خودکار دوباره اعمال می‌شوند.";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

/// <summary>Delivers tenant owner funding alerts with bounded conditional maintenance outside customer update lanes.</summary>
/// <remarks>
/// Stored tenant identity and funding episode remain authoritative; claim tokens serialize attempts, and a durable
/// send-start phase distinguishes retryable pre-send expiry from uncertain delivery. Idle scans remain read-only.
/// This worker changes only alert delivery state, never owner/customer balances, payments or ledger entries.
/// </remarks>
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

    /// <summary>Runs one recovery, retention, and delivery cycle without waiting for the polling interval.</summary>
    /// <param name="cancellationToken">Cancellation propagated to database commands and delivery.</param>
    /// <returns>The number of notification rows claimed during this cycle.</returns>
    /// <remarks>Idle maintenance uses read-only admission checks; claims and Telegram sends are never replayed by this method.</remarks>
    /// <exception cref="OperationCanceledException">The scan cancellation token is cancelled.</exception>
    /// <example>An empty cycle returns zero even while a different WAL connection holds a writer transaction.</example>
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
    /// <returns>A task completing after eligible recovery updates, or read-only checks when neither phase is due.</returns>
    /// <remarks>
    /// An expired claim whose <see cref="TenantStorefrontFundingAlert.SendStartedAtUtc"/> is null proves no Telegram
    /// request was started, so the row returns to Pending with an immediate retry. An expired claim whose send phase
    /// is set may have invoked Telegram, so it is conservatively marked DeliveryUncertain and never replayed.
    /// Each send-phase predicate is reused for its admission read and conditional update. Claims becoming eligible
    /// after an empty check wait for the next cycle; a concurrent state change is still protected by the update predicate.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The scan cancellation token is cancelled.</exception>
    /// <example>An expired pre-send claim returns to Pending; an expired possible-send claim becomes DeliveryUncertain.</example>
    internal async Task RecoverExpiredClaimsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _factory.CreateDbContext();
        var beforeSend = db.TenantStorefrontFundingAlerts
            .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.LeaseUntilUtc <= now
                        && x.SendStartedAtUtc == null);
        if (await beforeSend.AnyAsync(cancellationToken))
            await beforeSend.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TenantStorefrontFundingAlertStatuses.Pending)
                .SetProperty(x => x.NextAttemptAtUtc, now)
                .SetProperty(x => x.LastError, "processing_lease_expired_before_send")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        var possibleSend = db.TenantStorefrontFundingAlerts
            .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Processing && x.LeaseUntilUtc <= now
                        && x.SendStartedAtUtc != null);
        if (await possibleSend.AnyAsync(cancellationToken))
            await possibleSend.ExecuteUpdateAsync(s => s
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
    /// An empty eligibility check never issues DELETE. The bounded candidate query is evaluated again by DELETE,
    /// not materialized into stale ids, so concurrent state changes remain protected by the full retention predicate.
    /// Deletion is issued only once: a SQLite failure after execution must not replay another retention batch.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The scan cancellation token is cancelled.</exception>
    /// <example>With 150 eligible Delivered rows, one call deletes at most 100; a later call may delete the remainder.</example>
    internal async Task<int> CompactDeliveredAsync(CancellationToken cancellationToken = default)
    {
        if (_retentionDays <= 0)
            return 0;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Min(_retentionDays, 36500));
        await using var db = _factory.CreateDbContext();
        var eligible = db.TenantStorefrontFundingAlerts
            .Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Delivered &&
                        x.DeliveredAtUtc != null && x.DeliveredAtUtc < cutoff &&
                        x.ClaimToken == null && x.LeaseUntilUtc == null);
        if (!await eligible.AnyAsync(cancellationToken))
            return 0;

        var candidates = eligible.OrderBy(x => x.Id).Select(x => x.Id).Take(MaximumCleanupBatch);
        return await db.TenantStorefrontFundingAlerts
            .Where(x => candidates.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
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
        catch (OwnerNotificationTransportUnavailableException) { await RetryAsync(alert, OwnerNotificationTransportUnavailableException.SafeReason, cancellationToken); }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429) { await RetryAsync(alert, TelegramDeliveryFailureClassifier.Classify(ex), cancellationToken); }
        catch (BotTransportUnavailableException) { await RetryAsync(alert, "bot_transport_unavailable", cancellationToken); }
        catch (ApiRequestException ex) when (ex.ErrorCode >= 500) { await MarkUncertainAsync(alert, TelegramDeliveryFailureClassifier.Classify(ex), cancellationToken); }
        catch (ApiRequestException ex) { await MarkManualReviewAsync(alert, TelegramDeliveryFailureClassifier.Classify(ex), cancellationToken); }
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
