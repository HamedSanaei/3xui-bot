using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Mirrors a confirmed central tenant-customer wallet top-up into the immutable tenant owner's global bot wallet.
/// </summary>
/// <remarks>
/// Personal tenant card payments never call this service. A deterministic credentials receipt key makes retries,
/// duplicate IPNs, webhook recovery, and process restarts idempotent.
/// </remarks>
public sealed class TenantWalletOwnerTopUpMirrorService
{
    private readonly CredentialsStore _credentials;
    private readonly WalletLedgerService _ledger;
    private readonly UserDbContextFactory _users;
    private readonly ILogger<TenantWalletOwnerTopUpMirrorService> _logger;

    /// <summary>Creates the shared central-top-up mirror and receipt-backed reporting service.</summary>
    /// <param name="credentials">Global wallet receipt store in credentials.db.</param>
    /// <param name="ledger">Idempotent users.db ledger writer.</param>
    /// <param name="users">Independent users.db contexts for attribution and durable notifications.</param>
    /// <param name="logger">Central payment logger; no provider credentials are included.</param>
    public TenantWalletOwnerTopUpMirrorService(
        CredentialsStore credentials,
        WalletLedgerService ledger,
        UserDbContextFactory users,
        ILogger<TenantWalletOwnerTopUpMirrorService> logger)
    {
        _credentials = credentials;
        _ledger = ledger;
        _users = users;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the tenant owner receives exactly one credit equal to the customer's confirmed central top-up.
    /// </summary>
    /// <param name="provider">Stable central gateway label, such as atlaspay; never a credential.</param>
    /// <param name="paymentId">Positive local provider payment row id in users.db.</param>
    /// <param name="botId">Immutable originating storefront runtime id.</param>
    /// <param name="botUsername">Optional originating storefront username for display.</param>
    /// <param name="botType">Persisted invoice origin; non-tenant origins are ignored.</param>
    /// <param name="tenantOwnerTelegramUserId">Owner captured on the invoice; legacy null values resolve from the store.</param>
    /// <param name="customerTelegramUserId">Positive global Telegram identity credited by the gateway.</param>
    /// <param name="amountToman">Positive confirmed customer credit in Iranian toman.</param>
    /// <param name="cancellationToken">Cancellation for receipt, ledger and outbox persistence.</param>
    /// <returns>Detached owner receipt, or null for owned invoices and owner/customer identity equality.</returns>
    /// <remarks>Existing financial keys are unchanged. After both receipts exist, one owner report is persisted for
    /// Sales Assistant delivery. Replays recover missing reports without another credit. Telegram runs only in the
    /// delivery worker. Equal customer/owner identities reuse the original credit receipt.</remarks>
    /// <exception cref="InvalidOperationException">Attribution or committed financial evidence is missing or inconsistent.</exception>
    /// <example><code>await service.EnsureAsync("atlaspay", invoice.Id, invoice.BotId, invoice.BotUsername,
    /// invoice.WalletOriginBotType, invoice.TenantOwnerTelegramUserId, invoice.TelegramUserId, invoice.BaseAmountToman, token);</code></example>
    public async Task<WalletOperation> EnsureAsync(
        string provider,
        int paymentId,
        string botId,
        string botUsername,
        string botType,
        long? tenantOwnerTelegramUserId,
        long customerTelegramUserId,
        long amountToman,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(botType, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(botId) || amountToman <= 0)
            throw new InvalidOperationException("Tenant wallet top-up is missing storefront attribution.");
        if (tenantOwnerTelegramUserId is not > 0)
        {
            await using var db = _users.CreateDbContext();
            tenantOwnerTelegramUserId = await db.BotInstances.AsNoTracking()
                .Where(x => x.Id == botId && x.Type == BotInstanceTypes.Tenant)
                .Select(x => x.OwnerTelegramUserId)
                .SingleOrDefaultAsync(cancellationToken);
            if (tenantOwnerTelegramUserId is > 0)
                _logger.LogWarning(
                    "Recovered legacy tenant wallet top-up owner attribution from storefront. Provider={Provider} PaymentId={PaymentId} BotId={BotId}",
                    provider, paymentId, botId);
        }
        if (tenantOwnerTelegramUserId is not > 0)
            throw new InvalidOperationException("Tenant wallet top-up is missing owner attribution.");
        if (string.IsNullOrWhiteSpace(provider) || paymentId <= 0 || customerTelegramUserId <= 0)
            throw new InvalidOperationException("Tenant wallet top-up mirror identity is invalid.");

        // Customer and owner use the same canonical credentials wallet store. If they are the same Telegram identity,
        // the customer's original payment credit already is the owner's credit and must never be duplicated.
        if (tenantOwnerTelegramUserId.Value == customerTelegramUserId)
        {
            await EnsureReportAsync(provider, paymentId, botId, botUsername, customerTelegramUserId,
                tenantOwnerTelegramUserId.Value, amountToman, cancellationToken);
            return null;
        }

        var key = $"tenant-wallet-topup:{provider.ToLowerInvariant()}:{paymentId}:owner-credit";
        // The persisted storefront owner is authoritative even for migrated tenants whose global wallet row
        // has not been materialized yet. Create the zero-balance identity idempotently before applying the mirror.
        await _credentials.AddEmptyUser(tenantOwnerTelegramUserId.Value);
        var receipt = await _credentials.MutateWalletAsync(
            tenantOwnerTelegramUserId.Value,
            amountToman,
            key,
            cancellationToken,
            botId);
        if (receipt == null)
            throw new InvalidOperationException("Tenant owner mirror credit receipt was not created.");

        await _ledger.RecordAsync(
            tenantOwnerTelegramUserId.Value,
            WalletLedgerDirections.Credit,
            amountToman,
            receipt.BeforeBalance,
            receipt.AfterBalance,
            WalletLedgerReasons.TenantWalletTopUpMirror,
            provider: provider,
            referenceType: "tenant-wallet-topup",
            referenceId: paymentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            description: "Tenant customer central wallet top-up mirrored to storefront owner",
            ownerTelegramUserId: tenantOwnerTelegramUserId,
            counterpartyTelegramUserId: customerTelegramUserId,
            botId: botId,
            botUsername: botUsername,
            botType: BotInstanceTypes.Tenant,
            idempotencyKey: key,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Tenant wallet top-up owner mirror ensured. Provider={Provider} PaymentId={PaymentId} BotId={BotId} Owner={OwnerTelegramUserId} Customer={CustomerTelegramUserId} AmountToman={AmountToman}",
            provider, paymentId, botId, tenantOwnerTelegramUserId.Value, customerTelegramUserId, amountToman);
        await EnsureReportAsync(provider, paymentId, botId, botUsername, customerTelegramUserId,
            tenantOwnerTelegramUserId.Value, amountToman, cancellationToken);
        return receipt;
    }

    /// <summary>Persists a unique owner report using only immutable receipts, then emits the same central audit.</summary>
    /// <param name="provider">Stable central gateway label without credentials.</param>
    /// <param name="paymentId">Local provider payment row id.</param>
    /// <param name="botId">Originating tenant runtime id, retained for attribution rather than delivery.</param>
    /// <param name="botUsername">Optional store username, HTML encoded before display.</param>
    /// <param name="customerId">Global Telegram identity from the invoice.</param>
    /// <param name="ownerId">Global owner identity captured by settlement.</param>
    /// <param name="amount">Confirmed positive credit in toman.</param>
    /// <param name="token">Cancellation for local receipt and outbox work.</param>
    /// <returns>A task completing after the unique delivery intent exists; it does not call Telegram.</returns>
    /// <remarks>No current balance is used. A short users.db transaction serializes duplicate enqueue; no wallet
    /// mutation or network request runs in that transaction. Existing outbox keys default to customer delivery.</remarks>
    /// <exception cref="InvalidOperationException">Required receipts do not match the settled identities or amount.</exception>
    private async Task EnsureReportAsync(string provider, int paymentId, string botId, string botUsername,
        long customerId, long ownerId, long amount, CancellationToken token)
    {
        provider = provider.ToLowerInvariant();
        var key = $"{PaymentSettlementNotification.TenantOwnerReportPrefix}{provider}:{paymentId}:{ownerId}";
        await using (var existing = _users.CreateDbContext())
            if (await existing.PaymentSettlementNotifications.AnyAsync(x => x.NotificationKey == key, token)) return;

        var customer = await _credentials.GetWalletOperationAsync($"payment:{provider}:{paymentId}:credit", token);
        var owner = customerId == ownerId ? customer : await _credentials.GetWalletOperationAsync(
            $"tenant-wallet-topup:{provider}:{paymentId}:owner-credit", token);
        if (customer == null || owner == null || customer.TelegramUserId != customerId || owner.TelegramUserId != ownerId ||
            customer.AmountToman != amount || owner.AmountToman != amount)
            throw new InvalidOperationException("Tenant top-up report requires matching committed receipts.");

        var name = provider switch { "hooshpay" => "هوش‌پی", "tetraminator" => "تترامیناتور",
            "uniquepay" => "یونیک‌پی", "atlaspay" => "AtlasPay", "nowpayments" => "NOWPayments", _ => provider };
        var profile = await _credentials.GetUserStatusWithId(customerId);
        var report = $"✅ شارژ کیف پول از {System.Net.WebUtility.HtmlEncode(name)} تایید شد\n\n" +
            TelegramUserLinkFormatter.HtmlSummary(profile) + "\n\n" +
            $"💰 مبلغ شارژ: <code>{amount.FormatCurrency()}</code>\n" +
            $"🧾 شناسه پرداخت: <code>{provider}:{paymentId}</code>\n" +
            await ReadPaymentDetailsAsync(provider, paymentId, token) +
            $"🤖 فروشگاه: <code>{System.Net.WebUtility.HtmlEncode(botId)} / {System.Net.WebUtility.HtmlEncode(botUsername)}</code>\n\n" +
            $"👤 مشتری\n🆔 آیدی عددی: <code>{customerId}</code>\n" +
            $"💳 موجودی قبل: <code>{customer.BeforeBalance.FormatCurrency()}</code>\n" +
            $"💳 موجودی بعد: <code>{customer.AfterBalance.FormatCurrency()}</code>\n\n" +
            $"👤 مالک فروشگاه\n🆔 آیدی عددی: <code>{ownerId}</code>\n" +
            $"💳 موجودی قبل: <code>{owner.BeforeBalance.FormatCurrency()}</code>\n" +
            $"💳 موجودی بعد: <code>{owner.AfterBalance.FormatCurrency()}</code>\n" +
            $"🕒 زمان ثبت: <code>{customer.CreatedAtUtc.AddMinutes(210).ConvertToHijriShamsi()}</code>";
        var inserted = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.PaymentSettlementNotifications.AnyAsync(x => x.NotificationKey == key, ct)) return false;
            db.PaymentSettlementNotifications.Add(new PaymentSettlementNotification
            {
                NotificationKey = key, Provider = provider, ProviderPaymentId = paymentId,
                BotId = botId, WalletOriginBotType = BotInstanceTypes.Tenant,
                TelegramUserId = ownerId, ChatId = ownerId, AmountToman = amount, MessageText = report,
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }, token);
        if (inserted) _logger.LogPayment(report);
    }

    /// <summary>Reads the settled invoice's audit identifiers without exposing credentials or raw provider payloads.</summary>
    /// <param name="provider">Normalized central provider label from the receipt key.</param>
    /// <param name="paymentId">Local provider payment row id.</param>
    /// <param name="token">Cancellation for the detached users.db read.</param>
    /// <returns>HTML-encoded invoice details for the identical central and owner reports.</returns>
    /// <remarks>Balances never come from these invoice rows; both balance sections use committed wallet receipts.</remarks>
    private async Task<string> ReadPaymentDetailsAsync(string provider, int paymentId, CancellationToken token)
    {
        await using var db = _users.CreateDbContext();
        switch (provider)
        {
            case "atlaspay":
                var atlas = await db.AtlasPayPaymentInfos.AsNoTracking().SingleAsync(x => x.Id == paymentId, token);
                return AuditLine("💳 مبلغ نهایی فاکتور", atlas.TotalAmountToman?.FormatCurrency()) +
                    AuditLine("🧾 کارمزد/اختلاف فاکتور", Math.Max(0, (atlas.TotalAmountToman ?? atlas.BaseAmountToman) - atlas.BaseAmountToman).FormatCurrency()) +
                    AuditLine("💵 مبلغ دریافتی provider", atlas.ActualReceivedAmountToman?.FormatCurrency()) +
                    AuditLine("📌 وضعیت provider", atlas.ProviderStatus) + AuditLine("🧾 Provider Order ID", atlas.ProviderOrderId) +
                    AuditLine("🧾 Merchant Ref", atlas.MerchantOrderRef) + AuditLine("🔎 Tracking code", atlas.TrackingCode) +
                    AuditLine("🎯 هدف پرداخت", atlas.PaymentPurpose) + AuditLine("📨 Webhook event", atlas.WebhookEvent) +
                    AuditLine("📥 زمان دریافت Webhook", atlas.WebhookReceivedAtUtc?.AddMinutes(210).ConvertToHijriShamsi());
            case "hooshpay":
                var hoosh = await db.HooshPayPaymentInfos.AsNoTracking().SingleAsync(x => x.Id == paymentId, token);
                return AuditLine("💳 مبلغ پرداختی کاربر", hoosh.PayableAmountToman.FormatCurrency()) +
                    AuditLine("🧾 کارمزد", hoosh.FeeAmountToman.FormatCurrency()) + AuditLine("📌 وضعیت", hoosh.PaymentStatus) +
                    AuditLine("🧾 Order ID", hoosh.OrderId) + AuditLine("🧾 Invoice UID", hoosh.InvoiceUid) +
                    AuditLine("🔎 Tracking code", hoosh.TrackingCode);
            case "tetraminator":
                var tetra = await db.TetraminatorPaymentInfos.AsNoTracking().SingleAsync(x => x.Id == paymentId, token);
                return AuditLine("🧾 Order ID", tetra.OrderId) + AuditLine("🧾 Pay ID", tetra.PayId);
            case "uniquepay":
                var unique = await db.UniquePayPaymentInfos.AsNoTracking().SingleAsync(x => x.Id == paymentId, token);
                return AuditLine("💸 کارمزد درگاه", (unique.ProviderFeeToman ?? 0).FormatCurrency()) +
                    AuditLine("👤 پرداخت‌کننده کارمزد", unique.FeePayer) + AuditLine("💱 واحد", unique.Currency) +
                    AuditLine("🧾 Hash ID", unique.HashId) + AuditLine("🧾 Ref ID", unique.RefId);
            case "nowpayments":
                var now = await db.SwapinoPaymentInfos.AsNoTracking().SingleAsync(x => x.Id == paymentId, token);
                var data = now.GetNowPaymentsData();
                return AuditLine("📌 وضعیت", data.PaymentStatus ?? now.PaymentStatus) + AuditLine("🧾 Order ID", now.OrderId) +
                    AuditLine("🧾 Invoice ID", now.InvoiceId ?? data.InvoiceId) + AuditLine("🧾 Payment ID", now.PaymentId ?? data.PaymentId) +
                    AuditLine("💵 ارز مبنا", $"{(now.BaseAmount == 0 ? data.PriceAmount : now.BaseAmount)} {now.BaseCurrency ?? data.PriceCurrency}") +
                    AuditLine("🪙 ارز پرداختی", $"{(data.PayAmount == 0 ? now.OutcomeAmount : data.PayAmount)} {now.PayCurrency ?? data.PayCurrency}") +
                    AuditLine("✅ مقدار پرداخت‌شده واقعی", now.ActuallyPaid == 0 ? data.ActuallyPaid : now.ActuallyPaid) +
                    AuditLine("💲 معادل فیات پرداخت‌شده", now.ActuallyPaidAtFiat == 0 ? data.ActuallyPaidAtFiat : now.ActuallyPaidAtFiat) +
                    AuditLine("📤 خروجی NOWPayments", $"{(now.OutcomeAmount == 0 ? data.OutcomeAmount : now.OutcomeAmount)} {now.OutcomeCurrency ?? data.OutcomeCurrency}") +
                    AuditLine("🔗 Invoice URL", now.InvoiceUrl ?? data.InvoiceUrl) + AuditLine("🔐 Pay address", now.PayAddress ?? data.PayAddress) +
                    AuditLine("📥 Payin hash", now.PayinHash ?? data.PayinHash) + AuditLine("📤 Payout hash", now.PayoutHash ?? data.PayoutHash);
            default:
                throw new InvalidOperationException("Unsupported tenant top-up report provider.");
        }
    }

    /// <summary>Encodes one allow-listed invoice audit field for Telegram HTML.</summary>
    /// <param name="label">Trusted display label; never user-provided markup.</param>
    /// <param name="value">Non-secret invoice field, nullable; null displays a dash.</param>
    /// <returns>One HTML-safe line including a trailing newline.</returns>
    /// <remarks>Do not pass tokens, credentials or raw provider response objects.</remarks>
    /// <example><code>AuditLine("Order ID", invoice.OrderId)</code></example>
    private static string AuditLine(string label, object value) =>
        $"{label}: <code>{System.Net.WebUtility.HtmlEncode(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "-")}</code>\n";
}
