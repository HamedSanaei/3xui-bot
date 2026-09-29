using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Reconciles tenant discount capacity only after durable payment, refund or nonpayment evidence.</summary>
public partial class TenantBotService
{
    /// <summary>Checks one active or refunded paid tenant discount claim and records a proven terminal outcome.</summary>
    /// <param name="redemptionId">Internal users.db redemption id selected by the bounded background scan.</param>
    /// <param name="cancellationToken">Host shutdown token for local reads and commits.</param>
    /// <returns><c>true</c> if the claim was consumed or released; <c>false</c> if unresolved or already terminal.</returns>
    /// <remarks>Paid provider rows and exact wallet debit receipts consume capacity even if XUI delivery or Telegram notification fails. Uncertain invoice attempts and submitted card receipts keep their reservation. A verified wallet refund, explicit insufficient-wallet terminal decision, rejected card receipt, or two-hour payment-method/card expiry can release it. All final state checks and the claim update share one users.db write transaction; no gateway call runs under that lock.</remarks>
    public async Task<bool> ReconcileTenantDiscountReservationAsync(int redemptionId, CancellationToken cancellationToken = default)
    {
        var factory = _serviceProvider.GetRequiredService<UserDbContextFactory>();
        await using var view = factory.CreateDbContext();
        var candidate = await (from claim in view.TenantDiscountRedemptions.AsNoTracking()
            join order in view.TenantBotOrders.AsNoTracking() on claim.TenantBotOrderId equals order.Id
            where claim.Id == redemptionId
                && (claim.State == TenantDiscountRedemptionStates.Reserved
                    || claim.State == TenantDiscountRedemptionStates.Consumed)
            select order).SingleOrDefaultAsync(cancellationToken);
        if (candidate == null) return false;

        // Cross-database wallet receipts are read before the users.db write lock. Terminal wallet markers
        // prevent later debits; the transaction below rechecks the marker before freeing capacity.
        var wallet = string.Equals(candidate.PaymentProvider, "wallet", StringComparison.OrdinalIgnoreCase);
        var refunded = wallet && candidate.CustomerWalletState == "refunded"
            && await MatchingTenantDiscountWalletReceiptAsync(candidate, refund: true, cancellationToken);
        var debitPresent = wallet && await MatchingTenantDiscountWalletReceiptAsync(candidate, refund: false, cancellationToken);
        var insufficient = wallet && candidate.CustomerWalletState == "definitive_failed" && !debitPresent;

        return await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // A no-op writer statement serializes this decision with owner edits, admissions and payment rows.
            var locked = await db.TenantDiscountRedemptions.Where(x => x.Id == redemptionId
                    && (x.State == TenantDiscountRedemptionStates.Reserved
                        || x.State == TenantDiscountRedemptionStates.Consumed))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, x => x.State), ct);
            if (locked == 0) return false;
            var claim = await db.TenantDiscountRedemptions.SingleAsync(x => x.Id == redemptionId, ct);
            var order = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.Id == claim.TenantBotOrderId, ct);
            if (order == null || order.TenantDiscountCodeId != claim.CodeId || order.SalePriceToman <= 0
                || order.OriginalSalePriceToman <= 0 || order.DiscountAmountToman <= 0
                || order.OriginalSalePriceToman - order.DiscountAmountToman != order.SalePriceToman)
                return false;
            if (claim.State == TenantDiscountRedemptionStates.Consumed && !refunded) return false;
            var isWallet = string.Equals(order.PaymentProvider, "wallet", StringComparison.OrdinalIgnoreCase);
            // Recheck provider proof after taking the writer lock: a paid callback may have committed
            // between the scan and this transaction, and capacity must never be released in that race.
            var paid = !refunded && !insufficient && await HasVerifiedTenantDiscountPaymentAsync(order, db, ct);
            if (refunded && isWallet && order.CustomerWalletState == "refunded")
            {
                claim.State = TenantDiscountRedemptionStates.Released;
                claim.ReleasedAtUtc = DateTime.UtcNow;
            }
            else if (paid && (!isWallet || order.CustomerWalletState is not ("refund_pending" or "refunded" or "definitive_failed")))
            {
                claim.State = TenantDiscountRedemptionStates.Consumed;
                claim.ConsumedAtUtc = order.PaidAtUtc ?? DateTime.UtcNow;
            }
            else
            {
                // No known paid proof permits a release only when the stored order cannot initiate or receive payment.
                var hasPaymentRow = await db.HooshPayPaymentInfos.AnyAsync(x => x.TenantBotOrderId == order.Id, ct)
                    || await db.SwapinoPaymentInfos.AnyAsync(x => x.TenantBotOrderId == order.Id, ct)
                    || await db.TetraminatorPaymentInfos.AnyAsync(x => x.TenantBotOrderId == order.Id, ct)
                    || await db.UniquePayPaymentInfos.AnyAsync(x => x.TenantBotOrderId == order.Id, ct)
                    || await db.AtlasPayPaymentInfos.AnyAsync(x => x.TenantBotOrderId == order.Id, ct);
                var hasUnresolvedCardReceipt = await db.TenantManualPaymentReceipts.AnyAsync(x => x.TenantBotOrderId == order.Id
                    && x.Status != TenantManualPaymentReceiptStatuses.Rejected, ct);
                var rejectedCardReceipt = await db.TenantManualPaymentReceipts.AnyAsync(x => x.TenantBotOrderId == order.Id
                    && x.Status == TenantManualPaymentReceiptStatuses.Rejected && x.RejectedAtUtc != null, ct);
                var old = order.CreatedAtUtc <= DateTime.UtcNow.AddHours(-2);
                var finalUnpaid = hasPaymentRow && !paid
                    && await IsAuthoritativelyUnpaidTenantDiscountInvoiceAsync(db, order, ct);
                var noPayment = (!hasPaymentRow || finalUnpaid) && !hasUnresolvedCardReceipt
                    && order.PaidAtUtc == null && !order.IsFulfilled;
                var methodUnselected = order.OrderKind == TenantBotOrderKinds.Renew && order.PaymentProvider == "pending"
                    && order.DiscountInvoiceAttemptState == "none" && order.PaymentStatus == TenantBotOrderStatuses.Pending && old;
                var cardSafe = order.ProvisionalDeliveryState is null or TenantCardProvisionalStates.None or TenantCardProvisionalStates.Revoked;
                var cardExpired = order.PaymentProvider == "tenant_card"
                    && order.PaymentStatus == TenantBotOrderStatuses.AwaitingReceipt
                    && order.DiscountInvoiceAttemptState is ("none" or "created") && old && cardSafe;
                var cardRejected = order.PaymentProvider == "tenant_card" && rejectedCardReceipt && cardSafe;
                var definitive = order.DiscountInvoiceAttemptState == "definitive_failed"
                    && order.PaymentStatus == TenantBotOrderStatuses.Failed;
                var walletFailed = insufficient && isWallet && order.CustomerWalletState == "definitive_failed"
                    && order.PaymentStatus == TenantBotOrderStatuses.Failed;
                if (!noPayment || !(methodUnselected || cardExpired || cardRejected || definitive || walletFailed || finalUnpaid)) return false;
                if (isWallet && (!walletFailed || debitPresent)) return false;
                order.PaymentStatus = TenantBotOrderStatuses.DiscountExpired;
                order.UpdatedAtUtc = DateTime.UtcNow;
                claim.State = TenantDiscountRedemptionStates.Released;
                claim.ReleasedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);
    }

    /// <summary>Requires a final, tenant-order-linked provider row or exact wallet debit before consuming capacity.</summary>
    /// <param name="order">Stored order carrying the immutable payable amount and provider identity.</param>
    /// <param name="db">Current users.db writer context so the decision sees payment rows under the claim lock.</param>
    /// <param name="cancellationToken">Cancellation of the local payment evidence lookup.</param>
    /// <returns><c>true</c> for durable paid proof even when fulfillment failed before the order's paid timestamp was repaired.</returns>
    /// <remarks>A pending, timed-out or malformed provider creation never proves payment. This deliberately does not
    /// depend on XUI delivery or Telegram notification, and refuses wallet debit proof after refund authorization.</remarks>
    private async Task<bool> HasVerifiedTenantDiscountPaymentAsync(TenantBotOrder order, UserDbContext db, CancellationToken cancellationToken)
    {
        if (string.Equals(order.PaymentProvider, "wallet", StringComparison.OrdinalIgnoreCase))
            return order.CustomerWalletState is not ("refund_pending" or "refunded" or "definitive_failed")
                && await MatchingTenantDiscountWalletReceiptAsync(order, refund: false, cancellationToken);
        if (string.Equals(order.PaymentProvider, "tenant_card", StringComparison.OrdinalIgnoreCase))
            return await db.TenantManualPaymentReceipts.AsNoTracking().AnyAsync(x => x.TenantBotOrderId == order.Id
                && x.AmountToman == order.SalePriceToman && x.CustomerTelegramUserId == order.CustomerTelegramUserId
                && x.OwnerTelegramUserId == order.OwnerTelegramUserId
                && x.Status == TenantManualPaymentReceiptStatuses.Approved && x.FinalConfirmedAtUtc != null, cancellationToken);
        if (string.Equals(order.PaymentProvider, "hooshpay", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.HooshPayPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.AmountToman, x.PaymentStatus, x.PaidAtUtc, x.InvoiceUid }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.AmountToman == order.SalePriceToman && row.PaidAtUtc != null
                && !string.IsNullOrWhiteSpace(row.InvoiceUid) && HooshPayStatuses.IsPaid(row.PaymentStatus);
        }
        if (string.Equals(order.PaymentProvider, "nowpayments", StringComparison.OrdinalIgnoreCase)
            || string.Equals(order.PaymentProvider, "swapino", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.SwapinoPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.AmountToman, x.PaymentStatus, x.PaidAtUtc, x.InvoiceId, x.PaymentId }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.AmountToman == order.SalePriceToman && row.PaidAtUtc != null
                && (!string.IsNullOrWhiteSpace(row.InvoiceId) || !string.IsNullOrWhiteSpace(row.PaymentId))
                && NowPaymentsStatuses.IsPaid(row.PaymentStatus);
        }
        if (string.Equals(order.PaymentProvider, "tetraminator", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.TetraminatorPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.AmountToman, x.PaymentStatus, x.PaidAtUtc, x.PayId }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.AmountToman == order.SalePriceToman && row.PaidAtUtc != null
                && !string.IsNullOrWhiteSpace(row.PayId) && TetraminatorStatuses.IsPaid(row.PaymentStatus);
        }
        if (string.Equals(order.PaymentProvider, "uniquepay", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.UniquePayPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.BaseAmountToman, x.PaymentStatus, x.PaidAtUtc, x.RefId }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.BaseAmountToman == order.SalePriceToman && row.PaidAtUtc != null
                && !string.IsNullOrWhiteSpace(row.RefId) && UniquePayStatuses.IsPaid(row.PaymentStatus);
        }
        if (string.Equals(order.PaymentProvider, "atlaspay", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.AtlasPayPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.BaseAmountToman, x.ProviderStatus, x.PaidAtUtc, x.RequiresManualDelivery, x.ProviderOrderId })
                .SingleOrDefaultAsync(cancellationToken);
            return row != null && row.BaseAmountToman == order.SalePriceToman && row.PaidAtUtc != null
                && row.ProviderOrderId > 0 && !row.RequiresManualDelivery && AtlasPayStatuses.IsSuccess(row.ProviderStatus);
        }
        return false;
    }

    /// <summary>Recognizes only provider-reported terminal nonpayment for the order's frozen first method.</summary>
    /// <param name="db">Users.db context already inside the reservation write transaction.</param>
    /// <param name="order">Tracked tenant order; its provider and payable amount identify the expected invoice.</param>
    /// <param name="cancellationToken">Cancellation of the local provider-row lookup.</param>
    /// <returns><c>true</c> for an exact terminal unpaid invoice with no recorded paid timestamp; uncertain and pending statuses return <c>false</c>.</returns>
    /// <remarks>Network errors and locally failed creates are not provider cancellation proof. Tetraminator exposes no
    /// documented final-failure status and therefore remains reserved for manual review when an attempt exists.</remarks>
    private static async Task<bool> IsAuthoritativelyUnpaidTenantDiscountInvoiceAsync(
        UserDbContext db, TenantBotOrder order, CancellationToken cancellationToken)
    {
        if (string.Equals(order.PaymentProvider, "hooshpay", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.HooshPayPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.AmountToman, x.PaymentStatus, x.PaidAtUtc, x.InvoiceUid }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.AmountToman == order.SalePriceToman && row.PaidAtUtc == null
                && !string.IsNullOrWhiteSpace(row.InvoiceUid) && HooshPayStatuses.IsFinalFailure(row.PaymentStatus);
        }
        if (string.Equals(order.PaymentProvider, "nowpayments", StringComparison.OrdinalIgnoreCase)
            || string.Equals(order.PaymentProvider, "swapino", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.SwapinoPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.AmountToman, x.PaymentStatus, x.PaidAtUtc, x.InvoiceId, x.PaymentId }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.AmountToman == order.SalePriceToman && row.PaidAtUtc == null
                && (!string.IsNullOrWhiteSpace(row.InvoiceId) || !string.IsNullOrWhiteSpace(row.PaymentId))
                && NowPaymentsStatuses.IsFinalFailure(row.PaymentStatus);
        }
        if (string.Equals(order.PaymentProvider, "uniquepay", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.UniquePayPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.BaseAmountToman, x.PaymentStatus, x.PaidAtUtc, x.RefId }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.BaseAmountToman == order.SalePriceToman && row.PaidAtUtc == null
                && !string.IsNullOrWhiteSpace(row.RefId)
                && row.PaymentStatus is (UniquePayStatuses.Expired or UniquePayStatuses.Cancelled);
        }
        if (string.Equals(order.PaymentProvider, "atlaspay", StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.AtlasPayPaymentInfos.AsNoTracking().Where(x => x.TenantBotOrderId == order.Id)
                .Select(x => new { x.BaseAmountToman, x.ProviderStatus, x.PaidAtUtc, x.ProviderOrderId }).SingleOrDefaultAsync(cancellationToken);
            return row != null && row.BaseAmountToman == order.SalePriceToman && row.PaidAtUtc == null
                && row.ProviderOrderId > 0 && AtlasPayStatuses.IsTerminal(row.ProviderStatus);
        }
        return false;
    }

    /// <summary>Matches the immutable wallet debit or refund proof to the exact tenant order and whole-toman amount.</summary>
    /// <param name="order">Persisted tenant-scoped order; its id supplies the idempotency key.</param>
    /// <param name="refund"><c>true</c> for a credit receipt, <c>false</c> for the original debit.</param>
    /// <param name="cancellationToken">Cancellation of the credentials.db lookup.</param>
    /// <returns><c>true</c> only when the receipt's customer, tenant and signed toman amount all match.</returns>
    /// <remarks>A refund authorization alone is not a refund; the credit receipt must exist before capacity is freed.</remarks>
    private async Task<bool> MatchingTenantDiscountWalletReceiptAsync(TenantBotOrder order, bool refund, CancellationToken cancellationToken)
    {
        var operationKey = refund ? $"tenant-customer-wallet:{order.Id}:refund" : $"tenant-customer-wallet:{order.Id}:debit";
        var receipt = await _credentialsDbContext.GetWalletOperationAsync(operationKey, cancellationToken);
        return receipt != null && receipt.TelegramUserId == order.CustomerTelegramUserId
            && receipt.BotId == order.TenantBotId
            && receipt.AmountToman == (refund ? order.SalePriceToman : -order.SalePriceToman);
    }
}
