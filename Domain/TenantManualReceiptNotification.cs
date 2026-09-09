namespace Adminbot.Domain;

/// <summary>Durable delivery states for relaying one tenant receipt to the Sales Assistant.</summary>
public static class TenantManualReceiptNotificationStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string FailedPermanent = "failed_permanent";
    public const string ManualReview = "manual_review";
    public const string DeliveryUncertain = "delivery_uncertain";
}

/// <summary>
/// Durable users.db outbox row for one manual tenant receipt notification.
/// </summary>
/// <remarks>
/// The unique ReceiptId is the business idempotency key. Delivery state never authorizes
/// receipt approval, wallet mutation, order fulfillment, or any XUI operation.
/// </remarks>
public sealed class TenantManualReceiptNotification
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    public string Status { get; set; } = TenantManualReceiptNotificationStatuses.Pending;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string ClaimToken { get; set; }
    public int? TelegramMessageId { get; set; }
    public string LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
}
