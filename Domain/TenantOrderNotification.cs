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
}

public static class TenantOrderNotificationStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string FailedPermanent = "failed_permanent";
    public const string ManualReview = "manual_review";
    public const string DeliveryUncertain = "delivery_uncertain";
}

/// <summary>Durable post-fulfillment Telegram delivery intent for one tenant order and logical notification kind.</summary>
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
