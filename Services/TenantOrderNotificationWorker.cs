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
using Telegram.Bot.Types.ReplyMarkups;

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
    private readonly TenantOwnerNotificationTransportResolver _ownerTransportResolver;
    private readonly UserDbContextFactory _userDbContextFactory;

    public TenantOrderNotificationDeliveryService(
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        CredentialsStore credentialsStore,
        XuiV3PurchaseService purchaseService,
        SalesAssistantService salesAssistantService,
        TenantOwnerNotificationTransportResolver ownerTransportResolver,
        UserDbContextFactory userDbContextFactory)
    {
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _credentialsStore = credentialsStore;
        _purchaseService = purchaseService;
        _salesAssistantService = salesAssistantService;
        _ownerTransportResolver = ownerTransportResolver;
        _userDbContextFactory = userDbContextFactory;
    }

    public Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken) => kind switch
    {
        TenantOrderNotificationKinds.CustomerAccountDelivery => SendCustomerAccountAsync(order, cancellationToken),
        TenantOrderNotificationKinds.OwnerSaleNotification => SendOwnerSaleAsync(order, cancellationToken),
        TenantOrderNotificationKinds.SalesAssistantSaleNotification =>
            _salesAssistantService.SENDTENANTSALEASYNC(order, order.OwnerBalanceBefore ?? 0, order.OwnerBalanceAfter ?? 0, cancellationToken),
        TenantOrderNotificationKinds.OwnerAccountDetailsAfterAssistantFinal => SendOwnerAccountDetailsAsync(order, cancellationToken),
        TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery =>
            SendTenantCardReceiptReuploadRecoveryAsync(order, cancellationToken),
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
        await using var db = _userDbContextFactory.CreateDbContext();
        var tenant = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == order.TenantBotId && x.Type == BotInstanceTypes.Tenant &&
            x.OwnerTelegramUserId == order.OwnerTelegramUserId, cancellationToken);
        if (tenant == null)
            throw new OwnerNotificationTransportUnavailableException();
        var resolved = await _ownerTransportResolver.ResolveClientAsync(tenant, order.OwnerTelegramUserId, cancellationToken);
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
        return (await resolved.Client.SendTextMessageAsync(
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
    /// <summary>
    /// Sends the pre-fulfillment reminder that asks a tenant card-to-card customer to re-upload a receipt that was
    /// never persisted, through the exact storefront bot that owns the order.
    /// </summary>
    /// <param name="order">Detached order snapshot loaded by the worker for the claimed notification row.</param>
    /// <param name="cancellationToken">Cancellation token for the database re-check and the Telegram call.</param>
    /// <returns>
    /// The Telegram message id of the delivered reminder, which the worker records as the durable delivered
    /// acknowledgement. Never null on success.
    /// </returns>
    /// <exception cref="TenantOrderNotificationSupersededException">
    /// The order changed between enqueue and send, so the reminder is no longer needed and nothing may be sent.
    /// </exception>
    /// <exception cref="TenantOrderNotificationRequiresReviewException">
    /// The exact storefront transport is unusable, so the reminder cannot be delivered automatically.
    /// </exception>
    /// <remarks>
    /// This is the only pre-fulfillment notification kind. It re-reads the order immediately before the Telegram call,
    /// because the customer may have already re-uploaded the receipt while the intent waited in the outbox; sending
    /// "please upload again" to a customer whose receipt is already under owner review would be wrong, so that outcome
    /// terminates the row as Superseded instead. The reminder is delivered only by <c>order.TenantBotId</c>; the
    /// default owned bot and the Sales Assistant are never used as a fallback. This method never creates a receipt,
    /// never changes payment or fulfillment state, never touches a wallet or ledger, and never provisions an XUI client.
    /// </remarks>
    private async Task<int?> SendTenantCardReceiptReuploadRecoveryAsync(
        TenantBotOrder order,
        CancellationToken cancellationToken)
    {
        // Fresh durable re-check immediately before the send. The reminder is only meaningful while the order still
        // has no receipt at all and is still waiting for one.
        await using (var db = _userDbContextFactory.CreateDbContext())
        {
            var orderDbId = order.Id;
            var current = await db.TenantBotOrders.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == orderDbId, cancellationToken);
            if (current == null || !IsRecoveryReminderStillNeeded(current))
                throw new TenantOrderNotificationSupersededException();
            if (await db.TenantManualPaymentReceipts.AsNoTracking()
                    .AnyAsync(x => x.TenantBotOrderId == orderDbId, cancellationToken))
                throw new TenantOrderNotificationSupersededException();
        }

        var bot = _botRegistry.GetById(order.TenantBotId);
        if (bot == null ||
            !string.Equals(bot.Id, order.TenantBotId, StringComparison.OrdinalIgnoreCase) ||
            !bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
            throw new TenantOrderNotificationRequiresReviewException("tenant_transport_unavailable");

        var client = _botClientProvider.GetClient(order.TenantBotId);
        var chatId = order.CustomerChatId > 0 ? order.CustomerChatId : order.CustomerTelegramUserId;
        var sent = await client.SendTextMessageAsync(
            chatId,
            BuildReceiptReuploadRecoveryText(order),
            parseMode: ParseMode.Html,
            replyMarkup: BuildReceiptReuploadRecoveryKeyboard(order.Id),
            cancellationToken: cancellationToken);
        return sent.MessageId;
    }

    /// <summary>
    /// Decides whether a queued recovery reminder still applies to the freshly reloaded order.
    /// </summary>
    /// <param name="order">Order reloaded from users.db immediately before the send.</param>
    /// <returns><c>true</c> only while the order still needs the reminder.</returns>
    /// <remarks>
    /// Fulfillment, a linked receipt, or any payment status other than <c>awaiting_receipt</c> means the customer flow
    /// already moved on, so the reminder must be suppressed rather than delivered.
    /// </remarks>
    internal static bool IsRecoveryReminderStillNeeded(TenantBotOrder order) =>
        order != null &&
        !order.IsFulfilled &&
        !order.ManualReceiptId.HasValue &&
        string.Equals(order.PaymentStatus, TenantBotOrderStatuses.AwaitingReceipt, StringComparison.Ordinal);

    /// <summary>
    /// Builds the Persian recovery reminder that does not claim the customer has paid.
    /// </summary>
    /// <param name="order">Order whose public order number is embedded in the message.</param>
    /// <returns>HTML-encoded Telegram message text.</returns>
    internal static string BuildReceiptReuploadRecoveryText(TenantBotOrder order) =>
        "⚠️ <b>بررسی سفارش کارت‌به‌کارت</b>\n\n" +
        "برای این سفارش هنوز رسیدی در سیستم ثبت نشده است.\n\n" +
        "اگر قبلاً تصویر رسید را ارسال کرده‌اید، ممکن است به دلیل یک مشکل فنی ثبت نشده باشد. " +
        "لطفاً روی دکمه زیر بزنید و تصویر رسید را مجدداً ارسال کنید.\n\n" +
        $"شماره سفارش:\n<code>{Html(order.OrderId)}</code>\n\n" +
        "ارسال مجدد رسید هیچ پرداخت جدیدی ایجاد نمی‌کند.";

    /// <summary>
    /// Builds the inline keyboard that re-opens the exact order's receipt upload target.
    /// </summary>
    /// <param name="orderDbId">Internal users.db id of the tenant order.</param>
    /// <returns>
    /// A keyboard whose button uses the same <c>TN:receipt:{orderId}</c> callback as the original order message, so
    /// the durable bot-scoped upload target binds the next image to this exact order.
    /// </returns>
    internal static InlineKeyboardMarkup BuildReceiptReuploadRecoveryKeyboard(int orderDbId) =>
        new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🧾 ارسال مجدد رسید", "TN:receipt:" + orderDbId) }
        });

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

/// <summary>
/// Signals that a queued notification became unnecessary before any Telegram request was made.
/// </summary>
/// <remarks>
/// Raised by the pre-fulfillment receipt recovery reminder when the customer's receipt arrived, the order was
/// fulfilled, or the payment status moved on between enqueue and delivery. The worker records the row as
/// <see cref="TenantOrderNotificationStatuses.Superseded" />, which is terminal and never replayable, and never treats
/// the outcome as a delivery failure.
/// </remarks>
internal sealed class TenantOrderNotificationSupersededException() : Exception("superseded");

/// <summary>
/// Signals that a notification cannot be delivered automatically and needs an operator.
/// </summary>
/// <remarks>
/// Used when the exact tenant storefront transport is missing, disabled, or tokenless, in which case cross-sending
/// through another bot is never allowed. The worker records the row as
/// <see cref="TenantOrderNotificationStatuses.ManualReview" /> with the safe code from <see cref="SafeCode" />.
/// </remarks>
internal sealed class TenantOrderNotificationRequiresReviewException(string safeCode) : Exception(safeCode)
{
    /// <summary>Gets the stable, secret-free reason code persisted for operator review.</summary>
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

        // Every post-fulfillment kind requires a fulfilled order. The single pre-fulfillment recovery reminder is
        // intentionally delivered before fulfillment, so it is exempt from that gate.
        if (order == null || (!order.IsFulfilled && !IsPreFulfillmentKind(notification.Kind)))
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
        catch (TenantOrderNotificationSupersededException)
        {
            // The order moved on before the send. This is not a delivery failure: nothing was sent and nothing is
            // retried, so the row is terminally Superseded and the customer is never asked for an unneeded receipt.
            await MarkTerminalAsync(notification, TenantOrderNotificationStatuses.Superseded,
                "superseded_by_current_order_state", cancellationToken);
            LogDuration(order, notification.Kind, started, "superseded");
        }
        catch (TenantOrderNotificationRequiresReviewException ex)
        {
            // Cross-sending through another bot is never allowed, so an unusable storefront transport is a terminal
            // operator-visible state instead of a retry loop.
            await MarkTerminalAsync(notification, TenantOrderNotificationStatuses.ManualReview, ex.SafeCode, cancellationToken);
            LogDuration(order, notification.Kind, started, "manual_review");
        }
        catch (OwnerNotificationTransportUnavailableException)
        {
            await RetryOrExhaustAsync(notification, OwnerNotificationTransportUnavailableException.SafeReason, cancellationToken);
            LogDuration(order, notification.Kind, started, "pre_send_route_unavailable");
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            await RetryOrExhaustAsync(notification, TelegramDeliveryFailureClassifier.Classify(ex), cancellationToken);
            LogDuration(order, notification.Kind, started, "deferred");
        }
        catch (ApiRequestException ex) when (ex.ErrorCode >= 500)
        {
            await MarkDeliveryUncertainAsync(notification, TelegramDeliveryFailureClassifier.Classify(ex), cancellationToken);
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
                TelegramDeliveryFailureClassifier.Classify(ex), cancellationToken);
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

    /// <summary>
    /// Reports whether a notification kind is deliberately delivered before the order is fulfilled.
    /// </summary>
    /// <param name="kind">Persisted notification kind.</param>
    /// <returns><c>true</c> only for <see cref="TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery" />.</returns>
    /// <remarks>
    /// This single exemption keeps the fulfillment gate meaningful for every other kind while allowing the missed
    /// receipt reminder, which by definition targets an unfulfilled <c>awaiting_receipt</c> order.
    /// </remarks>
    internal static bool IsPreFulfillmentKind(string kind) =>
        string.Equals(kind, TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery, StringComparison.Ordinal);

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
