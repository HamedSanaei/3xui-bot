using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Queries storefront-local customer restrictions and submitted personal-card receipt evidence.</summary>
internal static class TenantCustomerAdmission
{
    /// <summary>Message for a second image or another personal-card checkout while an earlier receipt is under review.</summary>
    internal const string PendingReceiptMessage = "رسید قبلی شما در انتظار بررسی است. لطفاً تا اعلام نتیجه صبر کنید.";

    /// <summary>Message for denied new order or receipt admission in a single storefront.</summary>
    internal const string BlockedMessage = "در این فروشگاه امکان ثبت سفارش یا ارسال رسید برای شما مسدود شده است.";

    /// <summary>Checks whether a storefront owner has restricted this Telegram customer.</summary>
    /// <param name="db">Current users.db read context or order admission writer context.</param>
    /// <param name="storeId">Internal BotInstances id of this tenant, never the bot's Telegram id.</param>
    /// <param name="customerId">Numeric Telegram user id of the authenticated customer.</param>
    /// <param name="token">Cancellation of the database query.</param>
    /// <returns>True only while a block for this exact storefront and customer exists.</returns>
    internal static Task<bool> IsBlockedAsync(UserDbContext db, string storeId, long customerId, CancellationToken token) =>
        db.TenantCustomerBlocks.AnyAsync(x => x.TenantBotId == storeId && x.CustomerTelegramUserId == customerId, token);

    /// <summary>Enforces a storefront block immediately before persisting newly admitted customer work.</summary>
    /// <param name="db">Users.db writer context participating in the order transaction.</param>
    /// <param name="order">New tenant order containing the authoritative storefront and Telegram customer ids.</param>
    /// <param name="token">Cancellation of the local admission query.</param>
    /// <remarks>Never call from paid settlement or recovery. No wallet, provider, or Telegram effects occur here.</remarks>
    /// <exception cref="TenantCustomerBlockedException">The customer is blocked within this order's storefront.</exception>
    /// <example><code>await TenantCustomerAdmission.RequireUnblockedAsync(db, order, token);</code></example>
    internal static async Task RequireUnblockedAsync(UserDbContext db, TenantBotOrder order, CancellationToken token)
    {
        if (await IsBlockedAsync(db, order.TenantBotId, order.CustomerTelegramUserId, token))
            throw new TenantCustomerBlockedException();
    }

    /// <summary>Finds existing submitted personal-card evidence that still awaits a final review decision.</summary>
    /// <param name="db">Users.db context. Use the receipt writer's transaction when this check authorizes a new receipt.</param>
    /// <param name="storeId">Internal BotInstances id for the tenant receiving the receipt.</param>
    /// <param name="customerId">Telegram user id of the customer in this storefront.</param>
    /// <param name="token">Cancellation of the evidence query.</param>
    /// <returns>True if at least one real image is pending in this storefront, including historical duplicate submissions.</returns>
    /// <remarks>Order drafts and owner-entered receipt records without images do not use the slot. No amount or provisional expiry enters this decision.</remarks>
    /// <example><code>if (await TenantCustomerAdmission.HasPendingReceiptAsync(db, order.TenantBotId, order.CustomerTelegramUserId, token)) return;</code></example>
    internal static Task<bool> HasPendingReceiptAsync(UserDbContext db, string storeId, long customerId, CancellationToken token) =>
        db.TenantManualPaymentReceipts.AnyAsync(r => r.TenantBotId == storeId && r.CustomerTelegramUserId == customerId &&
            r.Status == TenantManualPaymentReceiptStatuses.Pending && r.PhotoFileId != null && r.PhotoFileId.Trim() != "" &&
            db.TenantBotOrders.Any(o => o.Id == r.TenantBotOrderId && o.TenantBotId == storeId &&
                o.CustomerTelegramUserId == customerId && o.PaymentProvider == "tenant_card" && !o.IsFulfilled), token);

    /// <summary>Tests whether the exact receipt still authorizes courtesy work for the corresponding order.</summary>
    /// <param name="db">Users.db context used for the last eligibility check before courtesy work or notification.</param>
    /// <param name="orderId">Internal users.db id of the exact tenant order.</param>
    /// <param name="receiptId">Internal users.db receipt id returned by the successful image writer.</param>
    /// <param name="token">Cancellation of the database query.</param>
    /// <returns>True for the current pending receipt of an unfulfilled personal-card order with the same scoped customer.</returns>
    internal static Task<bool> IsPendingReceiptAsync(UserDbContext db, int orderId, int receiptId, CancellationToken token) =>
        db.TenantBotOrders.AnyAsync(o => o.Id == orderId && o.ManualReceiptId == receiptId && !o.IsFulfilled &&
            o.PaymentProvider == "tenant_card" && db.TenantManualPaymentReceipts.Any(r => r.Id == receiptId &&
                r.TenantBotOrderId == o.Id && r.TenantBotId == o.TenantBotId && r.CustomerTelegramUserId == o.CustomerTelegramUserId &&
                r.Status == TenantManualPaymentReceiptStatuses.Pending && r.PhotoFileId != null && r.PhotoFileId.Trim() != ""), token);
}

/// <summary>Signals that newly admitted storefront work lost the race to a customer block.</summary>
internal sealed class TenantCustomerBlockedException : Exception
{
    /// <summary>Creates the admission failure with text suitable for the affected tenant customer.</summary>
    internal TenantCustomerBlockedException() : base(TenantCustomerAdmission.BlockedMessage) { }
}
