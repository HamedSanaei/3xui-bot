namespace Adminbot.Domain;

public static class TenantOrderNotificationKinds
{
    /// <summary>Delivers stored account credentials to the buyer through the exact tenant bot.</summary>
    public const string CustomerAccountDelivery = "customer_account_delivery";
    /// <summary>Sends the tenant owner the normal sale summary through the owned/default bot transport.</summary>
    public const string OwnerSaleNotification = "owner_sale_notification";
    /// <summary>Sends the tenant owner the distinct sale-success summary through the Sales Assistant bot.</summary>
    public const string SalesAssistantSaleNotification = "sales_assistant_sale_notification";
    /// <summary>Sends stored account details through Sales Assistant after the owner explicitly confirms FINAL.</summary>
    public const string OwnerAccountDetailsAfterAssistantFinal = "owner_account_details_after_assistant_final";
    /// <summary>
    /// Asks a tenant card-to-card customer to re-upload a receipt that was never persisted.
    /// </summary>
    /// <remarks>
    /// This is the only pre-fulfillment kind: it is queued by the missed-receipt recovery command for orders that are
    /// still <c>awaiting_receipt</c> with no receipt row, and it deliberately creates no receipt, no payment state
    /// change, no wallet effect, no ledger row, and no XUI client. Adding another kind value needs no migration because
    /// <see cref="TenantOrderNotification.Kind" /> is persisted as a string with a unique (order, kind) index.
    /// </remarks>
    public const string TenantCardReceiptReuploadRecovery = "tenant_card_receipt_reupload_recovery";
}

public static class TenantOrderNotificationStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string FailedPermanent = "failed_permanent";
    public const string ManualReview = "manual_review";
    public const string DeliveryUncertain = "delivery_uncertain";
    /// <summary>
    /// Terminal state for a queued intent that became unnecessary before any Telegram request was made.
    /// </summary>
    /// <remarks>
    /// Used by the missed-receipt recovery reminder when the customer's receipt arrived (or the order was fulfilled or
    /// left <c>awaiting_receipt</c>) between enqueue and delivery. It is not a delivery failure: no message was sent and
    /// nothing is retried, so it must never be reported as FailedPermanent or DeliveryUncertain.
    /// </remarks>
    public const string Superseded = "superseded";
}

/// <summary>Durable Telegram delivery intent for one tenant order and logical notification kind.</summary>
/// <remarks>
/// Almost every kind is post-fulfillment and is delivered only for a fulfilled order. The single exception is
/// <see cref="TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery" />, which is intentionally queued before
/// fulfillment so a customer whose pre-fix receipt image was dropped can be asked to re-upload it. Delivery state never
/// authorizes receipt approval, payment, wallet mutation, order fulfillment, or any XUI operation.
/// </remarks>
public sealed class TenantOrderNotification
{
    public int Id { get; set; }
    public int TenantBotOrderId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = TenantOrderNotificationStatuses.Pending;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    /// <summary>
    /// Moment the worker persisted that it is about to invoke the Telegram transport for this claimed row.
    /// </summary>
    /// <remarks>
    /// Durable send phase used to distinguish a crash before any Telegram request (null) from a crash where the
    /// remote outcome may be ambiguous (non-null). Set only while the row is claimed as Processing, and cleared on
    /// terminal transitions such as Delivered, Pending retry, ManualReview, or FailedPermanent.
    /// </remarks>
    public DateTime? SendStartedAtUtc { get; set; }
    public string ClaimToken { get; set; }
    public int? TelegramMessageId { get; set; }
    public string LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
}
