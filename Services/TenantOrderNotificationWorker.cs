using System.Diagnostics;
using System.Globalization;
using System.Net;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

internal interface ITenantOrderNotificationSender
{
    Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken);
}

internal sealed class TenantOrderNotificationSender : ITenantOrderNotificationSender
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TenantOrderNotificationSender(IServiceScopeFactory scopeFactory) =>
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TenantOrderNotificationDeliveryService>()
            .SendAsync(order, kind, cancellationToken);
    }
}
/// <summary>Delivers only post-fulfillment Telegram notifications from persisted tenant orders.</summary>
public sealed class TenantOrderNotificationDeliveryService
{
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly CredentialsStore _credentialsStore;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly SalesAssistantService _salesAssistantService;

    public TenantOrderNotificationDeliveryService(
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        CredentialsStore credentialsStore,
        XuiV3PurchaseService purchaseService,
        SalesAssistantService salesAssistantService)
    {
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _credentialsStore = credentialsStore;
        _purchaseService = purchaseService;
        _salesAssistantService = salesAssistantService;
    }

    public Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken) => kind switch
    {
        TenantOrderNotificationKinds.CustomerAccountDelivery => SendCustomerAccountAsync(order, cancellationToken),
        TenantOrderNotificationKinds.OwnerSaleNotification => SendOwnerSaleAsync(order, cancellationToken),
        TenantOrderNotificationKinds.SalesAssistantSaleNotification =>
            _salesAssistantService.SENDTENANTSALEASYNC(order, order.OwnerBalanceBefore ?? 0, order.OwnerBalanceAfter ?? 0, cancellationToken),
        TenantOrderNotificationKinds.OwnerAccountDetailsAfterAssistantFinal => SendOwnerAccountDetailsAsync(order, cancellationToken),
        _ => throw new TenantOrderNotificationPermanentException("unknown_notification_kind")
    };
    private async Task<int?> SendCustomerAccountAsync(TenantBotOrder order, CancellationToken cancellationToken)
    {
        var bot = _botRegistry.GetById(order.TenantBotId);
        if (bot == null || !string.Equals(bot.Id, order.TenantBotId, StringComparison.OrdinalIgnoreCase) ||
            !bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
            return null;

        var created = BuildCreatedAccount(order);
        if (created == null)
            throw new TenantOrderNotificationPermanentException("stored_account_details_missing");

        var client = _botClientProvider.GetClient(order.TenantBotId);
        var chatId = order.CustomerChatId == 0 ? order.CustomerTelegramUserId : order.CustomerChatId;
        if (string.Equals(order.OrderKind, TenantBotOrderKinds.Renew, StringComparison.OrdinalIgnoreCase))
        {
            var renewalText = BuildRenewalSuccessText(order, created);
            var sent = await client.SendTextMessageAsync(chatId, renewalText, parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return sent.MessageId;
        }

        var text = _purchaseService.BuildCreatedAccountText(created);
        if (!string.IsNullOrWhiteSpace(created.SubLink))
        {
            using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(created.SubLink, 200));
            var sent = await client.SendPhotoAsync(chatId, InputFile.FromStream(qrStream, "subscription-qr.png"),
                caption: text, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return sent.MessageId;
        }

        return (await client.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html,
            cancellationToken: cancellationToken)).MessageId;
    }
    private async Task<int?> SendOwnerSaleAsync(TenantBotOrder order, CancellationToken cancellationToken)
    {
        var bot = _botRegistry.DefaultBot;
        if (bot == null || string.IsNullOrWhiteSpace(bot.Token))
            return null;
        var owner = await _credentialsStore.GetUserStatusWithId(order.OwnerTelegramUserId);
        var chatId = owner?.ChatID > 0 ? owner.ChatID : order.OwnerTelegramUserId;
        var text =
            "✅ فروش ربات فروشگاهی انجام شد.\n\n" +
            $"ربات: @{Html(order.TenantBotUsername)}\n" +
            $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
            $"مبلغ فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"هزینه همکار: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
            $"سود/تغییر موجودی: <code>{Html(order.OwnerWalletDelta.FormatCurrency())}</code>\n" +
            $"موجودی قبل: <code>{Html(order.OwnerBalanceBefore?.FormatCurrency())}</code>\n" +
            $"موجودی بعد: <code>{Html(order.OwnerBalanceAfter?.FormatCurrency())}</code>";
        return (await _botClientProvider.GetClient(bot.Id).SendTextMessageAsync(
            chatId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken)).MessageId;
    }

    private async Task<int?> SendOwnerAccountDetailsAsync(TenantBotOrder order, CancellationToken cancellationToken)
    {
        var assistant = _botRegistry.Bots.FirstOrDefault(x =>
            string.Equals(x.Type, BotInstanceTypes.SalesAssistant, StringComparison.OrdinalIgnoreCase));
        if (assistant == null || !assistant.Enabled || string.IsNullOrWhiteSpace(assistant.Token))
            return null;
        var created = BuildCreatedAccount(order);
        if (created == null)
            throw new TenantOrderNotificationPermanentException("stored_account_details_missing");
        var text = "مشخصات اکانت ساخته‌شده:\n\n" + _purchaseService.BuildCreatedAccountText(created);
        return (await _botClientProvider.GetClient(assistant.Id).SendTextMessageAsync(
            order.OwnerTelegramUserId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken)).MessageId;
    }
    private static XuiV3AccountCreationResult BuildCreatedAccount(TenantBotOrder order)
    {
        XuiV3AccountCreationResult created = null;
        if (!string.IsNullOrWhiteSpace(order?.CreatedAccountJson))
        {
            try { created = JsonConvert.DeserializeObject<XuiV3AccountCreationResult>(order.CreatedAccountJson); }
            catch { created = null; }
        }
        created ??= new XuiV3AccountCreationResult();
        created.Success = true;
        created.Email ??= order?.CreatedAccountEmail;
        created.SubLink ??= order?.CreatedSubLink;
        return string.IsNullOrWhiteSpace(created.Email) && string.IsNullOrWhiteSpace(created.SubLink) ? null : created;
    }

    private static string BuildRenewalSuccessText(TenantBotOrder order, XuiV3AccountCreationResult created)
    {
        var duration = created.DurationDays <= 0 ? "نامحدود" : created.DurationDays + " روز";
        return "✅ <b>اکانت شما با موفقیت تمدید شد.</b>\n\n" +
               $"اکانت: <code>{Html(created.Email)}</code>\n" +
               $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
               $"مبلغ پرداختی: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
               (created.TrafficGb > 0 ? $"حجم تمدید: <code>{created.TrafficGb.ToString(CultureInfo.InvariantCulture)} GB</code>\n" : string.Empty) +
               $"مدت نهایی: <code>{Html(duration)}</code>\n" +
               $"ساب‌لینک: <code>{Html(created.SubLink)}</code>";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

internal sealed class TenantOrderNotificationPermanentException(string safeCode) : Exception(safeCode)
{
    public string SafeCode { get; } = safeCode;
}
/// <summary>Claims and delivers tenant post-fulfillment notifications outside Telegram update lanes.</summary>
public sealed class TenantOrderNotificationWorker : BackgroundService
{
    private const int MaximumAttempts = 6;
    private const int MaximumBatchSize = 10;
    private const int MaximumCompactionBatch = 100;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(15);
    private readonly UserDbContextFactory _contextFactory;
    private readonly ITenantOrderNotificationSender _sender;
    private readonly ILogger<TenantOrderNotificationWorker> _logger;
    private readonly int _retentionDays;

    public TenantOrderNotificationWorker(
        UserDbContextFactory contextFactory,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TenantOrderNotificationWorker> logger)
        : this(
            contextFactory,
            new TenantOrderNotificationSender(scopeFactory),
            logger,
            (configuration.Get<AppConfig>() ?? new AppConfig()).TenantOrderNotificationRetentionDays) { }

    internal TenantOrderNotificationWorker(
        UserDbContextFactory contextFactory,
        ITenantOrderNotificationSender sender,
        ILogger<TenantOrderNotificationWorker> logger,
        int retentionDays = 30)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retentionDays = retentionDays;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError("Tenant order notification scan failed. ErrorType={ErrorType}", ex.GetType().Name);
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        await RecoverExpiredClaimsBeforeSendAsync(cancellationToken);
        await MarkExpiredClaimsUncertainAsync(cancellationToken);
        var rows = await ClaimDueBatchAsync(cancellationToken);
        foreach (var row in rows)
            await DeliverAsync(row, cancellationToken);
        await CompactDeliveredAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// Returns expired Processing rows whose Telegram transport was never invoked to Pending for retry.
    /// </summary>
    /// <param name="cancellationToken">Cancellation of the short SQLite update.</param>
    /// <returns>A task completing after the conditional update.</returns>
    /// <remarks>
    /// A crash right after the atomic claim but before <see cref="SendStartedAtUtc"/> is persisted means no Telegram
    /// request could have been sent, so recycling the row to Pending is safe and cannot duplicate delivery.
    /// </remarks>
    private async Task RecoverExpiredClaimsBeforeSendAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var count = await db.TenantOrderNotifications
            .Where(x => x.Status == TenantOrderNotificationStatuses.Processing &&
                        x.SendStartedAtUtc == null &&
                        x.LeaseUntilUtc.HasValue && x.LeaseUntilUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantOrderNotificationStatuses.Pending)
                .SetProperty(x => x.LastError, "processing_lease_expired_before_send_started")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (count > 0)
            _logger.LogWarning("Tenant order notification claims expired before any send started; recycled for retry. Count={Count}", count);
    }

    /// <summary>
    /// Marks expired Processing rows whose Telegram transport may have been invoked as DeliveryUncertain.
    /// </summary>
    /// <param name="cancellationToken">Cancellation of the short SQLite update.</param>
    /// <returns>A task completing after the conditional update.</returns>
    /// <remarks>
    /// <see cref="SendStartedAtUtc"/> was persisted before the transport call, so the remote outcome is ambiguous
    /// and the row must never be replayed automatically; it is retained for operator review.
    /// </remarks>
    private async Task MarkExpiredClaimsUncertainAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var count = await db.TenantOrderNotifications
            .Where(x => x.Status == TenantOrderNotificationStatuses.Processing &&
                        x.SendStartedAtUtc != null &&
                        x.LeaseUntilUtc.HasValue && x.LeaseUntilUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantOrderNotificationStatuses.DeliveryUncertain)
                .SetProperty(x => x.LastError, "processing_lease_expired_after_possible_delivery")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (count > 0)
            _logger.LogError("Tenant order notifications became delivery-uncertain. Count={Count}", count);
    }

    /// <summary>
    /// Deletes a bounded batch of old delivered rows whose delivery is fully terminal and never replayable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation of the short SQLite delete.</param>
    /// <returns>The number of removed rows, at most <see cref="MaximumCompactionBatch"/>.</returns>
    /// <remarks>
    /// Only rows that are Delivered, older than the configured retention, and carrying no claim or lease are removed.
    /// Pending, Processing, DeliveryUncertain, ManualReview, and FailedPermanent rows are never candidates so they
    /// stay available for diagnostics and manual handling. The batch limit keeps one maintenance cycle bounded and
    /// the (Status, DeliveredAtUtc) index keeps the scan narrow.
    /// </remarks>
    internal async Task<int> CompactDeliveredAsync(CancellationToken cancellationToken = default)
    {
        if (_retentionDays <= 0)
            return 0;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Min(_retentionDays, 36500));
        return await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _contextFactory.CreateDbContext();
            var candidates = db.TenantOrderNotifications
                .Where(x => x.Status == TenantOrderNotificationStatuses.Delivered &&
                            x.DeliveredAtUtc != null && x.DeliveredAtUtc < cutoff &&
                            x.ClaimToken == null && x.LeaseUntilUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).Take(MaximumCompactionBatch);
            return await db.TenantOrderNotifications.Where(x => candidates.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }, cancellationToken);
    }
    private async Task<IReadOnlyList<TenantOrderNotification>> ClaimDueBatchAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var ids = await db.TenantOrderNotifications.AsNoTracking()
            .Where(x => x.Status == TenantOrderNotificationStatuses.Pending &&
                        (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now))
            .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(MaximumBatchSize).ToListAsync(cancellationToken);

        var claimed = new List<TenantOrderNotification>();
        foreach (var id in ids)
        {
            var claimToken = Guid.NewGuid().ToString("N");
            var updated = await db.TenantOrderNotifications
                .Where(x => x.Id == id && x.Status == TenantOrderNotificationStatuses.Pending &&
                            (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, TenantOrderNotificationStatuses.Processing)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.ClaimToken, claimToken)
                    .SetProperty(x => x.LeaseUntilUtc, now.Add(ClaimLease))
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            if (updated != 1) continue;
            claimed.Add(await db.TenantOrderNotifications.AsNoTracking()
                .SingleAsync(x => x.Id == id && x.ClaimToken == claimToken, cancellationToken));
        }
        return claimed;
    }
    private async Task DeliverAsync(TenantOrderNotification notification, CancellationToken cancellationToken)
    {
        TenantBotOrder order;
        await using (var db = _contextFactory.CreateDbContext())
            order = await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == notification.TenantBotOrderId, cancellationToken);

        if (order == null || !order.IsFulfilled)
        {
            await MarkTerminalAsync(notification, TenantOrderNotificationStatuses.FailedPermanent,
                order == null ? "tenant_order_not_found" : "tenant_order_not_fulfilled", cancellationToken);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            // Persist the send phase BEFORE invoking the Telegram transport: a crash after this commit but before
            // the HTTP call may still become DeliveryUncertain (conservative), but the row is never re-sent
            // blindly because the claim is retained until the lease expires.
            if (!await MarkSendStartedAsync(notification, cancellationToken))
            {
                // The claim was lost (another worker recycled or took over); do not send from a stale claim.
                LogDuration(order, notification.Kind, started, "claim_lost_before_send");
                return;
            }
            var messageId = await _sender.SendAsync(order, notification.Kind, cancellationToken);
            if (messageId.HasValue)
            {
                await MarkDeliveredAsync(notification, messageId.Value, cancellationToken);
                LogDuration(order, notification.Kind, started, "delivered");
                return;
            }

            await RetryOrExhaustAsync(notification, "telegram_transport_unavailable", cancellationToken);
            LogDuration(order, notification.Kind, started, "deferred");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogDuration(order, notification.Kind, started, "shutdown_claim_retained");
            throw;
        }
        catch (TenantOrderNotificationPermanentException ex)
        {
            await MarkTerminalAsync(notification, TenantOrderNotificationStatuses.FailedPermanent, ex.SafeCode, cancellationToken);
            LogDuration(order, notification.Kind, started, "failed_permanent");
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            await RetryOrExhaustAsync(notification, "telegram_api_429", cancellationToken);
            LogDuration(order, notification.Kind, started, "deferred");
        }
        catch (ApiRequestException ex) when (ex.ErrorCode >= 500)
        {
            await MarkDeliveryUncertainAsync(notification, $"telegram_api_{ex.ErrorCode}_outcome_uncertain", cancellationToken);
            LogDuration(order, notification.Kind, started, "delivery_uncertain");
        }
        catch (BotTransportUnavailableException)
        {
            await RetryOrExhaustAsync(notification, "bot_transport_unavailable", cancellationToken);
            LogDuration(order, notification.Kind, started, "deferred");
        }
        catch (ApiRequestException ex)
        {
            await MarkTerminalAsync(notification, TenantOrderNotificationStatuses.ManualReview,
                $"telegram_api_{ex.ErrorCode}", cancellationToken);
            LogDuration(order, notification.Kind, started, "failed");
        }
        catch (Exception ex) when (IsAmbiguousTransportException(ex))
        {
            await MarkDeliveryUncertainAsync(notification, "telegram_send_outcome_uncertain", cancellationToken);
            LogDuration(order, notification.Kind, started, "delivery_uncertain");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Tenant fulfillment notification outcome became uncertain. orderId={OrderId} kind={Kind} ErrorType={ErrorType}",
                order.OrderId, notification.Kind, ex.GetType().Name);
            await MarkDeliveryUncertainAsync(notification, "unexpected_send_outcome_uncertain", cancellationToken);
            LogDuration(order, notification.Kind, started, "delivery_uncertain");
        }
    }

    private static bool IsAmbiguousTransportException(Exception ex) =>
        ex is RequestException or TimeoutException or HttpRequestException or TaskCanceledException;

    private void LogDuration(TenantBotOrder order, string kind, long startedTimestamp, string outcome)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "Tenant fulfillment post-commit notification completed. orderId={OrderId} kind={Kind} elapsedMs={ElapsedMs:0} outcome={Outcome}",
            order.OrderId, kind, elapsed, outcome);
    }
    private async Task MarkDeliveredAsync(TenantOrderNotification notification, int messageId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var updated =        await db.TenantOrderNotifications
            .Where(x => x.Id == notification.Id && x.Status == TenantOrderNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantOrderNotificationStatuses.Delivered)
                .SetProperty(x => x.TelegramMessageId, messageId)
                .SetProperty(x => x.DeliveredAtUtc, now)
                .SetProperty(x => x.LastError, (string)null)
                .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (updated != 1)
            await MarkDeliveryUncertainAsync(notification, "telegram_ack_persistence_uncertain", cancellationToken, requireClaim: false);
    }

    private async Task RetryOrExhaustAsync(TenantOrderNotification notification, string safeError, CancellationToken cancellationToken)
    {
        if (notification.AttemptCount >= MaximumAttempts)
        {
            await MarkTerminalAsync(notification, TenantOrderNotificationStatuses.ManualReview, safeError, cancellationToken);
            return;
        }

        var seconds = Math.Min(MaximumRetryDelay.TotalSeconds,
            InitialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, notification.AttemptCount - 1)));
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        await db.TenantOrderNotifications
            .Where(x => x.Id == notification.Id && x.Status == TenantOrderNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantOrderNotificationStatuses.Pending)
                .SetProperty(x => x.NextAttemptAtUtc, now.AddSeconds(seconds))
                .SetProperty(x => x.LastError, safeError)
                .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }
    private async Task MarkTerminalAsync(
        TenantOrderNotification notification,
        string status,
        string safeError,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        await db.TenantOrderNotifications
            .Where(x => x.Id == notification.Id && x.Status == TenantOrderNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.LastError, safeError)
                .SetProperty(x => x.SendStartedAtUtc, (DateTime?)null)
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    /// <summary>
    /// Persists the durable send phase immediately before the Telegram transport is invoked.
    /// </summary>
    /// <param name="notification">Claimed row whose claim token guards the update.</param>
    /// <param name="cancellationToken">Cancellation of the short SQLite update.</param>
    /// <returns>
    /// <c>true</c> when this worker still owns the claim and the send phase was persisted; <c>false</c> when the
    /// claim was lost and the caller must not invoke the Telegram transport.
    /// </returns>
    private async Task<bool> MarkSendStartedAsync(TenantOrderNotification notification, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var updated = await db.TenantOrderNotifications
            .Where(x => x.Id == notification.Id && x.Status == TenantOrderNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken && x.SendStartedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.SendStartedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        return updated == 1;
    }

    private async Task MarkDeliveryUncertainAsync(
        TenantOrderNotification notification,
        string safeError,
        CancellationToken cancellationToken,
        bool requireClaim = true)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var query = db.TenantOrderNotifications.Where(x => x.Id == notification.Id &&
            x.Status != TenantOrderNotificationStatuses.Delivered);
        if (requireClaim)
            query = query.Where(x => x.Status == TenantOrderNotificationStatuses.Processing &&
                                     x.ClaimToken == notification.ClaimToken);
        await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, TenantOrderNotificationStatuses.DeliveryUncertain)
            .SetProperty(x => x.LastError, safeError)
            .SetProperty(x => x.ClaimToken, (string)null)
            .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
            .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
            .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }
}
