using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Recovers existing tenant gateway orders from authoritative payment evidence, without provisional approval.</summary>
public partial class TenantBotService
{
    /// <summary>Internal audit label; it conveys no payment or provisioning authorization.</summary>
    internal const string OwnerGatewayRecoverySource = "owner-orderid-gateway";

    /// <summary>Checks an existing storefront purchase or renewal on behalf of its freshly authenticated owner.</summary>
    /// <param name="tenantBotId">Required internal id of the explicitly selected tenant store, not a Telegram bot id.</param>
    /// <param name="orderId">Required exact public TenantBotOrder.OrderId, not a database id or provider tracking code.</param>
    /// <param name="ownerTelegramUserId">Positive authenticated Message.From.Id; never a customer-supplied actor id.</param>
    /// <param name="cancellationToken">Cancels database, official provider inquiry, and existing fulfillment work.</param>
    /// <returns>Non-null Persian Telegram HTML with only the public order, known provider, and safe fulfillment outcome.</returns>
    /// <remarks>
    /// Fresh store ownership and reciprocal payment/customer/amount links precede any provider or financial work.
    /// Only the original issued invoice is checked; current sale/gateway enablement is deliberately irrelevant.
    /// Personal-card approvals and wallet funding retain their dedicated flows. Existing order gates, durable XUI
    /// attempts, wallet receipts and ledger keys own idempotency. A durable inbox confirmation may advance only a
    /// definitively rejected provisioning attempt; ambiguous attempts never receive a blind new POST.
    /// SalePriceToman, BaseCostToman and ProfitToman come from the existing frozen tenant order in Iranian toman,
    /// not current tariffs, exchange rates or message text. Platform-gateway fulfillment credits the original owner's
    /// profit once; it does not debit the customer wallet or the owner's base-cost wallet and creates no card receipt.
    /// Provider rows link their original invoice to TenantBotOrderId; no new invoice is issued. Provider verification,
    /// XUI delivery and the two SQLite databases are not one distributed transaction: the existing receipt-backed wallet
    /// transactions and durable order/operation keys resume each persisted step. If payment succeeds but delivery does
    /// not, the result remains incomplete and financial/provisioning ambiguity stays for reviewed recovery. An already
    /// completed financial delivery is not rolled back or repeated because sending the owner result later fails.
    /// </remarks>
    /// <example><code>await service.ConfirmTenantGatewayOrderByOwnerAsync(selectedStore.Id, message.Text, message.From.Id, token);</code></example>
    /// <exception cref="OperationCanceledException">The caller cancels verification or settlement.</exception>
    public async Task<string> ConfirmTenantGatewayOrderByOwnerAsync(
        string tenantBotId, string orderId, long ownerTelegramUserId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string notFound = "سفارش قابل بررسی در فروشگاه انتخاب‌شده پیدا نشد. شناسه عمومی سفارش را دقیق وارد کنید.";
        TenantBotOrder order = null;
        try
        {
            order = await _workflow.ReadAsync(db => ReadOwnerGatewayOrderAsync(
                db, tenantBotId, orderId, ownerTelegramUserId, cancellationToken));
            if (order == null) return notFound;
            await _workflow.ReloadAsync(order, cancellationToken);
            // Recheck the exact selected store after refreshing a possibly already-captured workflow snapshot.
            if (order.TenantBotId != tenantBotId || order.OrderId != orderId || order.OwnerTelegramUserId != ownerTelegramUserId ||
                !await _workflow.ReadAsync(db => db.BotInstances.AsNoTracking().AnyAsync(x =>
                    x.Id == tenantBotId && x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == ownerTelegramUserId,
                    cancellationToken))) return notFound;
            if (!IsGatewayRecoveryOrder(order))
                return BuildOwnerGatewayRecoveryResult(order, "این نوع سفارش در این بخش قابل بررسی نیست؛ از مسیر اختصاصی کارت‌به‌کارت یا کیف پول استفاده کنید.");

            NowPaymentsSettlementResult result;
            switch (order.PaymentProvider)
            {
                case var _ when string.Equals(order.PaymentProvider, "atlaspay", StringComparison.OrdinalIgnoreCase):
                    result = await _atlasPayReconciliation.ReconcileTenantOrderByOwnerAsync(
                        order.AtlasPayPaymentInfoId ?? 0, tenantBotId, orderId, ownerTelegramUserId, cancellationToken);
                    break;
                case var _ when string.Equals(order.PaymentProvider, "uniquepay", StringComparison.OrdinalIgnoreCase):
                    result = await _serviceProvider.GetRequiredService<UniquePayReconciliationHostedService>()
                        .ReconcileTenantOrderByOwnerAsync(order.UniquePayPaymentInfoId ?? 0, tenantBotId, orderId,
                            ownerTelegramUserId, cancellationToken);
                    break;
                case var _ when string.Equals(order.PaymentProvider, "hooshpay", StringComparison.OrdinalIgnoreCase):
                    result = await RecoverOwnerHooshPayOrderAsync(order, cancellationToken);
                    break;
                case var _ when string.Equals(order.PaymentProvider, "tetraminator", StringComparison.OrdinalIgnoreCase):
                    result = await RecoverOwnerTetraminatorOrderAsync(order, cancellationToken);
                    break;
                case var _ when string.Equals(order.PaymentProvider, "nowpayments", StringComparison.OrdinalIgnoreCase):
                    result = await RecoverOwnerNowPaymentsOrderAsync(order, cancellationToken);
                    break;
                default:
                    return BuildOwnerGatewayRecoveryResult(order, "درگاه این سفارش برای بررسی رسمی پشتیبانی نمی‌شود.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await _workflow.ReloadAsync(order, cancellationToken);
            // A provider result alone is not fulfillment proof, and a concurrent fulfillment does not turn
            // ProviderNotPaid/InvalidAmount into an owner-facing success for this verification attempt.
            var settled = result.Status is NowPaymentsSettlementStatus.Applied or NowPaymentsSettlementStatus.AlreadyAdded;
            if (settled && order.IsFulfilled)
                return BuildOwnerGatewayRecoveryResult(order, result.Status == NowPaymentsSettlementStatus.AlreadyAdded
                    ? "✅ سفارش قبلاً تکمیل شده است؛ ایجاد، تمدید یا تسویه دوباره انجام نشد."
                    : "✅ پرداخت رسمی تأیید و سفارش تکمیل شد.");
            if (settled)
                return BuildOwnerGatewayRecoveryResult(order, "⚠️ وضعیت پرداخت و تحویل نیازمند بررسی مدیر اصلی است؛ تکمیل سفارش تأیید نشده است.");
            return BuildOwnerGatewayRecoveryResult(order, result.Status == NowPaymentsSettlementStatus.PaymentNotFound
                ? "⚠️ ارتباط سفارش و فاکتور قابل تأیید نیست؛ بررسی مدیر اصلی لازم است."
                : "⏳ پرداخت کامل و تحویل سفارش تأیید نشد. ممکن است پرداخت نشده، درگاه در دسترس نباشد یا پرونده نیازمند بررسی مدیر اصلی باشد؛ تأیید موقت انجام نمی‌شود.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Owner gateway recovery failed safely. tenantBotId={TenantBotId}, tenantOrderId={TenantOrderId}, actor={Actor}",
                tenantBotId, order?.Id, ownerTelegramUserId);
            return order == null ? notFound : BuildOwnerGatewayRecoveryResult(order,
                "⚠️ بررسی رسمی درگاه یا بازیابی تحویل کامل نشد. جزئیات فقط برای مدیر اصلی ثبت شد؛ در صورت ابهام، بررسی مدیر اصلی لازم است.");
        }
    }

    /// <summary>Loads only the exact public order in a currently owned tenant store.</summary>
    /// <param name="db">Short-lived users.db context; returned order is detached.</param>
    /// <param name="tenantBotId">Explicit internal tenant-store id, required.</param>
    /// <param name="orderId">Exact public storefront order id, required.</param>
    /// <param name="ownerTelegramUserId">Authenticated positive Telegram actor id.</param>
    /// <param name="token">Cancellation of read-only authorization queries.</param>
    /// <returns>The detached exact-store order, or null without revealing sibling or foreign order existence.</returns>
    /// <remarks>No provider request, mutation or global/same-owner fallback is permitted by this lookup.</remarks>
    /// <example><code>var order = await ReadOwnerGatewayOrderAsync(db, storeId, publicOrderId, actorId, token);</code></example>
    /// <exception cref="OperationCanceledException">Database reads are cancelled.</exception>
    internal static async Task<TenantBotOrder> ReadOwnerGatewayOrderAsync(
        UserDbContext db, string tenantBotId, string orderId, long ownerTelegramUserId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(tenantBotId) || string.IsNullOrWhiteSpace(orderId) || ownerTelegramUserId <= 0)
            return null;
        return await (from order in db.TenantBotOrders.AsNoTracking()
                      join bot in db.BotInstances.AsNoTracking() on order.TenantBotId equals bot.Id
                      where bot.Id == tenantBotId && bot.Type == BotInstanceTypes.Tenant &&
                            bot.OwnerTelegramUserId == ownerTelegramUserId && order.OwnerTelegramUserId == ownerTelegramUserId &&
                            order.OrderId == orderId
                      select order).SingleOrDefaultAsync(token);
    }

    /// <summary>Restricts recovery to positive-priced external-gateway purchase/renewal order kinds.</summary>
    /// <param name="order">Exact-store detached order, or null.</param>
    /// <returns>True for a positive-priced purchase or renewal with one of the five supported providers.</returns>
    /// <remarks>Wallet, manual card, unknown provider and standalone wallet-invoice flows are not broadened.</remarks>
    /// <example><code>if (!IsGatewayRecoveryOrder(order)) return NowPaymentsSettlementResult.NotFound();</code></example>
    internal static bool IsGatewayRecoveryOrder(TenantBotOrder order) => order != null && order.CustomerTelegramUserId > 0 &&
        order.SalePriceToman > 0 && (order.OrderKind == TenantBotOrderKinds.Purchase || order.OrderKind == TenantBotOrderKinds.Renew) &&
        (string.Equals(order.PaymentProvider, "atlaspay", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(order.PaymentProvider, "uniquepay", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(order.PaymentProvider, "hooshpay", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(order.PaymentProvider, "tetraminator", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(order.PaymentProvider, "nowpayments", StringComparison.OrdinalIgnoreCase));

    /// <summary>Validates the reciprocal, immutable AtlasPay tenant invoice identity before official verification.</summary>
    /// <param name="order">Freshly authorized exact-store order.</param>
    /// <param name="payment">Invoice selected solely by the order's AtlasPay primary key; nullable.</param>
    /// <returns>True only for matching purpose, order/store/owner/customer ids, positive provider identity and original amount.</returns>
    /// <remarks>MerchantOrderRef is provider-specific and is not the public tenant OrderId.</remarks>
    /// <example><code>if (!IsOwnerAtlasPayLinkValid(order, payment)) return NowPaymentsSettlementResult.NotFound();</code></example>
    internal static bool IsOwnerAtlasPayLinkValid(TenantBotOrder order, AtlasPayPaymentInfo payment) =>
        IsGatewayRecoveryOrder(order) && string.Equals(order.PaymentProvider, "atlaspay", StringComparison.OrdinalIgnoreCase) &&
        payment != null && order.AtlasPayPaymentInfoId == payment.Id && payment.TenantBotOrderId == order.Id &&
        payment.PaymentPurpose == TenantBotPaymentPurposes.TenantOrder && payment.BotId == order.TenantBotId &&
        payment.TenantOwnerTelegramUserId == order.OwnerTelegramUserId && payment.TelegramUserId == order.CustomerTelegramUserId &&
        payment.BaseAmountToman == order.SalePriceToman && payment.TotalAmountToman >= payment.BaseAmountToman &&
        payment.ProviderOrderId > 0 && !string.IsNullOrWhiteSpace(payment.MerchantOrderRef) && !payment.IsProvisionallyApproved;

    /// <summary>Validates the reciprocal UniquePay tenant invoice identity before serialized official inquiry.</summary>
    /// <param name="order">Freshly authorized exact-store order.</param>
    /// <param name="payment">Invoice selected solely by the order's UniquePay primary key; nullable.</param>
    /// <returns>True only for matching purpose, order/store/owner/customer ids, merchant hash and original toman amount.</returns>
    /// <remarks>HashId is independent of public OrderId; settlement uses only the exact persisted TenantBotOrderId.</remarks>
    /// <example><code>if (!IsOwnerUniquePayLinkValid(order, payment)) return NowPaymentsSettlementResult.NotFound();</code></example>
    internal static bool IsOwnerUniquePayLinkValid(TenantBotOrder order, UniquePayPaymentInfo payment) =>
        IsGatewayRecoveryOrder(order) && string.Equals(order.PaymentProvider, "uniquepay", StringComparison.OrdinalIgnoreCase) &&
        payment != null && order.UniquePayPaymentInfoId == payment.Id && payment.TenantBotOrderId == order.Id &&
        payment.PaymentPurpose == TenantBotPaymentPurposes.TenantOrder && payment.BotId == order.TenantBotId &&
        payment.TenantOwnerTelegramUserId == order.OwnerTelegramUserId && payment.TelegramUserId == order.CustomerTelegramUserId &&
        payment.BaseAmountToman == order.SalePriceToman && !string.IsNullOrWhiteSpace(payment.HashId) && !payment.IsProvisionallyApproved;

    /// <summary>Proves that a quarantined gateway fulfillment tail is a definitive rejected purchase, not an uncertain claim.</summary>
    /// <param name="factory">Existing users.db context factory, required.</param>
    /// <param name="order">Fresh exact-store purchase order whose current owner authorized the request.</param>
    /// <param name="authorization">Typed durable current-inbox owner authorization, or null to fail closed.</param>
    /// <param name="token">Cancellation of read-only attempt and ledger checks.</param>
    /// <returns>True only if the durable coordinator permits a new generation and there is no fulfillment/account/owner-credit/ledger evidence.</returns>
    /// <remarks>Does not reset an XUI operation or grant a generation. The existing coordinator consumes the authorization
    /// under fulfillment serialization. Reserved/PostStarted/Ambiguous/Applied and reused inbox authorizations fail closed here.</remarks>
    /// <example><code>var rejected = await CanRecoverOwnerRejectedGatewayClaimAsync(factory, order, authorization, token);</code></example>
    /// <exception cref="OperationCanceledException">Database reads are cancelled.</exception>
    internal static async Task<bool> CanRecoverOwnerRejectedGatewayClaimAsync(UserDbContextFactory factory,
        TenantBotOrder order, TenantProvisioningRetryAuthorization authorization, CancellationToken token)
    {
        if (authorization?.Kind != TenantProvisioningRetryAuthorizationKind.OwnerExplicit ||
            authorization.ActorTelegramUserId != order.OwnerTelegramUserId || order.OrderKind != TenantBotOrderKinds.Purchase ||
            order.IsFulfilled || order.IsOwnerCredited || !string.IsNullOrWhiteSpace(order.CreatedAccountEmail) ||
            !string.IsNullOrWhiteSpace(order.CreatedAccountJson)) return false;
        await using var db = factory.CreateDbContext();
        if (await db.TenantBotLedgerEntries.AsNoTracking().AnyAsync(x => x.TenantBotOrderId == order.Id, token)) return false;
        var resolution = await new TenantProvisioningAttemptCoordinator(factory).ResolvePurchaseAttemptAsync(order.Id, authorization, token);
        if (!resolution.Allowed || resolution.Generation <= 0 || resolution.AuthorizedByKey != authorization.DurableKey) return false;
        var previousKey = resolution.Generation == 1
            ? TenantProvisioningAttemptCoordinator.BuildBaseOperationKey(order.Id)
            : TenantProvisioningAttemptCoordinator.BuildRetryOperationKey(order.Id, resolution.Generation - 1);
        // Require the exact durable classification, not merely an unknown enum value treated as terminal.
        return await db.XuiV3CreationOperations.AsNoTracking().AnyAsync(
            x => x.OperationKey == previousKey && x.Outcome == XuiV3CreationOutcome.DefinitiveRejected, token);
    }

    /// <summary>Verifies HooshPay officially and enters the exact order's existing one-time settlement.</summary>
    /// <param name="order">Freshly authorized purchase or renewal.</param>
    /// <param name="token">Cancellation of provider, persistence and fulfillment work.</param>
    /// <returns>Existing settlement result; no unpaid, mismatched or provisional row reaches fulfillment.</returns>
    /// <remarks>The UID, original amount and original fee contract are checked before applying provider data. Only proven
    /// payment constructs the durable owner retry authorization and enters the exact shared fulfillment core, avoiding
    /// the historical OR order lookup in the provider overload.</remarks>
    /// <example><code>var result = await RecoverOwnerHooshPayOrderAsync(order, token);</code></example>
    /// <exception cref="OperationCanceledException">Provider or settlement work is cancelled.</exception>
    private async Task<NowPaymentsSettlementResult> RecoverOwnerHooshPayOrderAsync(TenantBotOrder order, CancellationToken token)
    {
        var payment = await _workflow.ReadAsync(db => db.HooshPayPaymentInfos.SingleOrDefaultAsync(x => x.Id == order.HooshPayPaymentInfoId, token));
        if (payment == null) return NowPaymentsSettlementResult.NotFound();
        await _workflow.ReloadAsync(payment, token);
        if (payment.TenantBotOrderId != order.Id || payment.OrderId != order.OrderId || payment.BotId != order.TenantBotId ||
            payment.TenantOwnerTelegramUserId != order.OwnerTelegramUserId || payment.TelegramUserId != order.CustomerTelegramUserId ||
            payment.PaymentPurpose != TenantBotPaymentPurposes.TenantOrder || payment.AmountToman != order.SalePriceToman ||
            string.IsNullOrWhiteSpace(payment.InvoiceUid) || payment.IsProvisionallyApproved ||
            (!string.IsNullOrWhiteSpace(order.HooshPayInvoiceUid) && payment.InvoiceUid != order.HooshPayInvoiceUid))
            return NowPaymentsSettlementResult.NotFound();
        if (order.IsFulfilled) return NowPaymentsSettlementResult.AlreadyAdded(order.OwnerBalanceAfter ?? 0);
        var response = await _hooshPay.VerifyInvoiceAsync(payment.InvoiceUid, token);
        var data = response?.data;
        if (data == null || response.success != true || data.uid != payment.InvoiceUid || data.amount != payment.AmountToman ||
            !string.Equals(data.fee_mode, payment.FeeMode, StringComparison.OrdinalIgnoreCase) ||
            data.payable_amount != payment.PayableAmountToman || data.merchant_credit != payment.MerchantCreditToman ||
            data.fee_amount != payment.FeeAmountToman || data.fee_percent != payment.FeePercent)
            return NowPaymentsSettlementResult.InvalidAmount();
        if (!response.paid || !HooshPayStatuses.IsPaid(data.status)) return NowPaymentsSettlementResult.ProviderNotPaid();
        payment.Apply(data);
        payment.PaidAtUtc ??= DateTime.UtcNow;
        await _workflow.SaveAsync(token);
        var authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
            TenantProvisioningRetryAuthorizationKind.OwnerExplicit, order.OwnerTelegramUserId, order.Id);
        return await FULFILLPAIDTENANTORDERASYNC(order, OwnerGatewayRecoverySource, payment, null, false, token, authorization);
    }

    /// <summary>Reuses the official Tetraminator verifier and exact-order fulfillment rather than callback or cached paid flags.</summary>
    /// <param name="order">Freshly authorized purchase or renewal.</param>
    /// <param name="token">Cancellation of provider, persistence and fulfillment work.</param>
    /// <returns>Existing settlement result after full pay-id and toman-amount proof.</returns>
    /// <remarks>Only verified payment constructs the durable owner retry authorization. Provider settlement markers are
    /// written only after the existing financial core succeeds; no card receipt is created.</remarks>
    /// <example><code>var result = await RecoverOwnerTetraminatorOrderAsync(order, token);</code></example>
    /// <exception cref="OperationCanceledException">Provider or settlement work is cancelled.</exception>
    private async Task<NowPaymentsSettlementResult> RecoverOwnerTetraminatorOrderAsync(TenantBotOrder order, CancellationToken token)
    {
        var payment = await _workflow.ReadAsync(db => db.TetraminatorPaymentInfos.SingleOrDefaultAsync(x => x.Id == order.TetraminatorPaymentInfoId, token));
        if (payment == null) return NowPaymentsSettlementResult.NotFound();
        await _workflow.ReloadAsync(payment, token);
        if (payment.TenantBotOrderId != order.Id || payment.OrderId != order.OrderId || payment.BotId != order.TenantBotId ||
            payment.TenantOwnerTelegramUserId != order.OwnerTelegramUserId || payment.TelegramUserId != order.CustomerTelegramUserId ||
            payment.PaymentPurpose != TenantBotPaymentPurposes.TenantOrder || payment.AmountToman != order.SalePriceToman ||
            string.IsNullOrWhiteSpace(payment.PayId) || payment.IsProvisionallyApproved) return NowPaymentsSettlementResult.NotFound();
        if (order.IsFulfilled) return NowPaymentsSettlementResult.AlreadyAdded(order.OwnerBalanceAfter ?? 0);
        var response = await _tetraminator.InquiryAsync(payment.PayId, token);
        if (!TetraminatorPaymentVerifier.IsVerifiedPaid(payment, response, out _)) return NowPaymentsSettlementResult.ProviderNotPaid();
        payment.Apply(response);
        payment.PaidAtUtc ??= DateTime.UtcNow;
        payment.ErrorCode = null;
        payment.ErrorMessage = null;
        await _workflow.SaveAsync(token);
        var authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
            TenantProvisioningRetryAuthorizationKind.OwnerExplicit, order.OwnerTelegramUserId, order.Id);
        var result = await FULFILLPAIDTENANTORDERASYNC(order, OwnerGatewayRecoverySource, null, null, false, token, authorization);
        if (result.Status is NowPaymentsSettlementStatus.Applied or NowPaymentsSettlementStatus.AlreadyAdded)
        {
            payment.IsAddedToBalance = true;
            payment.SettledAtUtc ??= order.FulfilledAtUtc ?? DateTime.UtcNow;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _workflow.SaveAsync(token);
        }
        return result;
    }

    /// <summary>Refreshes NOWPayments and proves original invoice identity and immutable quoted currency/amount before settlement.</summary>
    /// <param name="order">Freshly authorized purchase or renewal.</param>
    /// <param name="token">Cancellation of provider, persistence and fulfillment work.</param>
    /// <returns>Existing exact-order settlement result; partially-paid, unrelated and changed-price responses fail closed.</returns>
    /// <remarks>Uses the original saved quote, never today's exchange rate. Provider lookup may find a payment for an
    /// invoice without a saved payment id, but the returned order, invoice and price must still match before any write.
    /// Only proven payment constructs the durable owner retry authorization.</remarks>
    /// <example><code>var result = await RecoverOwnerNowPaymentsOrderAsync(order, token);</code></example>
    /// <exception cref="OperationCanceledException">Provider or settlement work is cancelled.</exception>
    private async Task<NowPaymentsSettlementResult> RecoverOwnerNowPaymentsOrderAsync(TenantBotOrder order, CancellationToken token)
    {
        var payment = await _workflow.ReadAsync(db => db.SwapinoPaymentInfos.SingleOrDefaultAsync(x => x.Id == order.NowPaymentsPaymentInfoId, token));
        if (payment == null) return NowPaymentsSettlementResult.NotFound();
        await _workflow.ReloadAsync(payment, token);
        var data = payment.GetNowPaymentsData();
        if (payment.TenantBotOrderId != order.Id || payment.OrderId != order.OrderId || payment.BotId != order.TenantBotId ||
            payment.TenantOwnerTelegramUserId != order.OwnerTelegramUserId || payment.TelegramUserId != order.CustomerTelegramUserId ||
            payment.PaymentPurpose != TenantBotPaymentPurposes.TenantOrder || payment.AmountToman != order.SalePriceToman ||
            string.IsNullOrWhiteSpace(payment.InvoiceId) || data.InvoiceId != payment.InvoiceId || data.OrderId != order.OrderId ||
            payment.BaseAmount <= 0 || data.PriceAmount != payment.BaseAmount || string.IsNullOrWhiteSpace(data.PriceCurrency) ||
            !string.Equals(data.PriceCurrency, payment.BaseCurrency, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(payment.PaymentId) && payment.PaymentId != data.PaymentId)) return NowPaymentsSettlementResult.NotFound();
        if (order.IsFulfilled) return NowPaymentsSettlementResult.AlreadyAdded(order.OwnerBalanceAfter ?? 0);
        var remote = await REFRESHTENANTNOWPAYMENTSSTATUSASYNC(payment, data, token);
        if (remote == null || remote.order_id != order.OrderId || remote.invoice_id != payment.InvoiceId ||
            string.IsNullOrWhiteSpace(remote.payment_id) || (!string.IsNullOrWhiteSpace(payment.PaymentId) && remote.payment_id != payment.PaymentId) ||
            remote.price_amount != data.PriceAmount || !string.Equals(remote.price_currency, data.PriceCurrency, StringComparison.OrdinalIgnoreCase))
            return NowPaymentsSettlementResult.InvalidAmount();
        if (!NowPaymentsStatuses.IsPaid(remote.payment_status) || remote.pay_amount <= 0 || remote.actually_paid < remote.pay_amount)
            return NowPaymentsSettlementResult.ProviderNotPaid();
        data.Apply(remote);
        payment.SetNowPaymentsData(data);
        payment.PaidAtUtc ??= DateTime.UtcNow;
        payment.ErrorCode = null;
        payment.ErrorMessage = null;
        await _workflow.SaveAsync(token);
        var authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
            TenantProvisioningRetryAuthorizationKind.OwnerExplicit, order.OwnerTelegramUserId, order.Id);
        return await FULFILLPAIDTENANTORDERASYNC(order, OwnerGatewayRecoverySource, null, payment, false, token, authorization);
    }

    /// <summary>Builds a safe owner-facing result without provider, panel or exception payloads.</summary>
    /// <param name="order">Authorized order supplying only its public id and allow-listed provider label.</param>
    /// <param name="outcome">Fixed application-owned Persian HTML outcome, never raw exception or provider text.</param>
    /// <returns>Non-null Telegram HTML; untrusted public order id is escaped.</returns>
    /// <remarks>Does not disclose account credentials, payment URLs, merchant/provider references or panel diagnostics.</remarks>
    /// <example><code>return BuildOwnerGatewayRecoveryResult(order, "پرداخت کامل تأیید نشد.");</code></example>
    private static string BuildOwnerGatewayRecoveryResult(TenantBotOrder order, string outcome)
    {
        var provider = order.PaymentProvider switch
        {
            var name when string.Equals(name, "atlaspay", StringComparison.OrdinalIgnoreCase) => "AtlasPay",
            var name when string.Equals(name, "uniquepay", StringComparison.OrdinalIgnoreCase) => "UniquePay",
            var name when string.Equals(name, "hooshpay", StringComparison.OrdinalIgnoreCase) => "HooshPay",
            var name when string.Equals(name, "tetraminator", StringComparison.OrdinalIgnoreCase) => "Tetraminator",
            var name when string.Equals(name, "nowpayments", StringComparison.OrdinalIgnoreCase) => "NOWPayments",
            _ => "مسیر اختصاصی"
        };
        return $"🧾 سفارش: <code>{Html(order.OrderId)}</code>\n💳 درگاه: <b>{provider}</b>\n\n{outcome}";
    }
}
