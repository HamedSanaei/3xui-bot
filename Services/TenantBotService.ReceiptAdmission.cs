using System.Globalization;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;

/// <summary>Tenant-local receipt admission and owner-managed customer restrictions.</summary>
public partial class TenantBotService
{
    /// <summary>Orders receipt decisions against delivery of completed courtesy credentials within the running host.</summary>
    private static readonly AsyncKeyedGate ReceiptDeliveryGate = new();

    /// <summary>Serializes inline and background revocation attempts for the same durable courtesy account.</summary>
    private static readonly AsyncKeyedGate ReceiptRevocationGate = new();

    /// <summary>Gets the customer-safe reason for refusing new personal-card work.</summary>
    /// <param name="storeId">Internal id of the active tenant storefront.</param>
    /// <param name="customerId">Authenticated sender's numeric Telegram id.</param>
    /// <param name="token">Cancellation of users.db reads.</param>
    /// <returns>Localized refusal text, or null when fresh personal-card work can continue to other admission checks.</returns>
    /// <remarks>UI checks are advisory. The receipt writer rechecks both rules in its transaction.</remarks>
    /// <example><code>var refusal = await ReadTenantCardAdmissionRefusalAsync(store.Id, sender.Id, token);</code></example>
    private Task<string> ReadTenantCardAdmissionRefusalAsync(string storeId, long customerId, CancellationToken token) =>
        _workflow.ReadAsync(async db =>
        {
            if (await TenantCustomerAdmission.IsBlockedAsync(db, storeId, customerId, token))
                return TenantCustomerAdmission.BlockedMessage;
            return await TenantCustomerAdmission.HasPendingReceiptAsync(db, storeId, customerId, token)
                ? TenantCustomerAdmission.PendingReceiptMessage : null;
        });

    /// <summary>Rejects a pending personal-card receipt and blocks new orders and receipts in its own storefront.</summary>
    /// <param name="receiptId">Exact internal users.db receipt id selected by the Sales Assistant review button.</param>
    /// <param name="ownerId">Numeric Telegram id of the authenticated acting storefront owner.</param>
    /// <param name="token">Cancellation of review and the existing XUI revocation path.</param>
    /// <returns>Persian acknowledgement of the local decision and, when applicable, the outcome of provisional revocation.</returns>
    /// <remarks>This performs no payment, wallet, or ledger operation. Replaying an old rejection cannot recreate a lifted block.</remarks>
    /// <example><code>var message = await tenantService.RejectAndBlockManualReceiptAsync(receiptId, callback.From.Id, token);</code></example>
    public Task<string> RejectAndBlockManualReceiptAsync(int receiptId, long ownerId, CancellationToken token) =>
        RejectTenantReceiptAsync(receiptId, ownerId, blockCustomer: true, token);

    /// <summary>Records a guarded rejection and optional storefront restriction before attempting any XUI work.</summary>
    /// <param name="receiptId">Internal id of the exact existing receipt.</param>
    /// <param name="ownerId">Telegram sender id that must match the current owner of the receipt's storefront.</param>
    /// <param name="blockCustomer">Whether this fresh rejection also creates a local customer restriction.</param>
    /// <param name="token">Cancellation of the local transaction and subsequent provisional revocation.</param>
    /// <returns>Safe Persian review result. A canceled or failed panel call does not undo the local transaction.</returns>
    /// <remarks>Ownership, receipt identity and unpaid state are reloaded in one users.db write transaction. Approved payment and accepted fulfillment stay independently recoverable.</remarks>
    /// <exception cref="OperationCanceledException">The review or panel operation was cancelled.</exception>
    private async Task<string> RejectTenantReceiptAsync(int receiptId, long ownerId, bool blockCustomer, CancellationToken token)
    {
        var deliveryLease = await ReceiptDeliveryGate.EnterAsync(receiptId.ToString(CultureInfo.InvariantCulture), token);
        (int OrderId, string Message) decision;
        using (deliveryLease)
        {
            decision = await _workflow.WriteAsync<(int OrderId, string Message)>(async db =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var receipt = await db.TenantManualPaymentReceipts.SingleOrDefaultAsync(x => x.Id == receiptId, token);
                if (receipt == null)
                    return (OrderId: 0, Message: "رسید پیدا نشد.");

                var order = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.Id == receipt.TenantBotOrderId &&
                    x.TenantBotId == receipt.TenantBotId && x.CustomerTelegramUserId == receipt.CustomerTelegramUserId &&
                    x.PaymentProvider == "tenant_card", token);
                if (order == null)
                    return (0, "سفارش مرتبط با رسید پیدا نشد.");
                if (receipt.OwnerTelegramUserId != ownerId || order.OwnerTelegramUserId != ownerId ||
                    !await db.BotInstances.AnyAsync(x => x.Id == order.TenantBotId && x.Type == BotInstanceTypes.Tenant &&
                        x.OwnerTelegramUserId == ownerId, token))
                    return (0, "فقط صاحب همین ربات فروشگاهی می‌تواند این رسید را رد کند.");

                if (await db.TenantCardProvisionalOperations.AnyAsync(x => x.TenantBotOrderId == order.Id && x.ReviewedRecoveryId != null, token))
                    return (0, "این رسید برای بازیابی دستی پشتیبانی قفل شده است؛ رد یا تغییر آن مجاز نیست.");

                if (order.IsFulfilled || order.PaidAtUtc != null || order.PaymentStatus == TenantBotOrderStatuses.ReceiptApproved ||
                    receipt.FinalConfirmedAtUtc != null || (order.ManualReceiptId.HasValue && order.ManualReceiptId != receipt.Id))
                    return (0, "این رسید دیگر در انتظار بررسی نیست.");

                // A retry of an ordinary rejection may resume cleanup, but never recreates a lifted block.
                if (!blockCustomer && receipt.Status == TenantManualPaymentReceiptStatuses.Rejected)
                    return (order.Id, "رسید رد شد.");
                if (receipt.Status != TenantManualPaymentReceiptStatuses.Pending ||
                    (blockCustomer && string.IsNullOrWhiteSpace(receipt.PhotoFileId)))
                    return (0, "این رسید دیگر در انتظار بررسی نیست.");

                var now = DateTime.UtcNow;
                receipt.Status = TenantManualPaymentReceiptStatuses.Rejected;
                receipt.ReviewerTelegramUserId = ownerId;
                receipt.RejectedAtUtc = now;
                receipt.UpdatedAtUtc = now;
                order.PaymentStatus = TenantBotOrderStatuses.ReceiptRejected;
                order.UpdatedAtUtc = now;
                // Keep cleanup durable if review wins during an in-flight create or before the callback can reach XUI.
                if (order.ProvisionalDeliveryState is TenantCardProvisionalStates.Provisioning or TenantCardProvisionalStates.Delivered)
                    order.ProvisionalDeliveryState = TenantCardProvisionalStates.Revoking;
                if (blockCustomer && !await TenantCustomerAdmission.IsBlockedAsync(db, order.TenantBotId, order.CustomerTelegramUserId, token))
                {
                    db.TenantCustomerBlocks.Add(new TenantCustomerBlock
                    {
                        TenantBotId = order.TenantBotId,
                        CustomerTelegramUserId = order.CustomerTelegramUserId,
                        ReceiptId = receipt.Id,
                        BlockedByTelegramUserId = ownerId,
                        BlockedAtUtc = now
                    });
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return (order.Id, blockCustomer ? "رسید رد شد و مشتری در همین فروشگاه مسدود شد." : "رسید رد شد.");
            }, token);
        }

        if (decision.OrderId == 0)
            return decision.Message;
        var revoke = await REVOKETENANTCARDPROVISIONALASYNC(decision.OrderId, token);
        return revoke == null ? decision.Message : blockCustomer ? $"{revoke}\nمشتری در همین فروشگاه مسدود شد." : revoke;
    }

    /// <summary>Lifts the exact restriction shown in order details and refreshes the selected store's panel.</summary>
    /// <param name="client">Owned bot transport serving the authorized owner panel.</param>
    /// <param name="callback">Original owner callback from the selected storefront.</param>
    /// <param name="owner">Shared profile of the authenticated current owner.</param>
    /// <param name="payload">Internal order id and block receipt id in hexadecimal, separated by a colon.</param>
    /// <param name="token">Cancellation of the delete and panel refresh.</param>
    /// <remarks>Uses the order's buyer and current storefront ownership in the delete predicate. Stale buttons cannot lift a newer block.</remarks>
    private async Task LiftTenantCustomerBlockAsync(ITelegramBotClient client, CallbackQuery callback, CredUser owner,
        string payload, CancellationToken token)
    {
        var parts = payload.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var orderId) ||
            !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var receiptId) || orderId <= 0 || receiptId <= 0)
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "درخواست نامعتبر است.", showAlert: true, cancellationToken: token);
            return;
        }

        var storeId = _selectedOwnerStore.Id;
        var removed = await _workflow.WriteAsync(db => db.TenantCustomerBlocks.Where(b =>
            b.TenantBotId == storeId && b.ReceiptId == receiptId &&
            db.TenantBotOrders.Any(o => o.Id == orderId && o.TenantBotId == storeId &&
                o.CustomerTelegramUserId == b.CustomerTelegramUserId) &&
            db.BotInstances.Any(s => s.Id == storeId && s.Type == BotInstanceTypes.Tenant && s.OwnerTelegramUserId == owner.TelegramUserId))
            .ExecuteDeleteAsync(token), token);

        if (removed == 0)
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "مسدودی تغییر کرده است؛ جزئیات سفارش را دوباره باز کنید.",
                showAlert: true, cancellationToken: token);
            return;
        }
        await SHOWOWNERORDERDETAILASYNC(client, callback, owner, orderId.ToString(CultureInfo.InvariantCulture), token);
    }
}
