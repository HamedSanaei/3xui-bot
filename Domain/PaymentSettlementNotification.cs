namespace Adminbot.Domain;

/// <summary>
/// Defines the durable delivery states of an owned-wallet settlement notification.
/// </summary>
/// <remarks>
/// These states govern Telegram delivery only. They never authorize, repeat, reverse, or otherwise mutate a wallet,
/// provider payment, ledger entry, tenant order, or XUI operation.
/// </remarks>
public static class PaymentSettlementNotificationStatuses
{
    /// <summary>The notification is due for its first delivery attempt or a bounded transient retry.</summary>
    public const string Pending = "pending";

    /// <summary>A worker owns a short lease and may currently be calling Telegram.</summary>
    public const string Processing = "processing";

    /// <summary>Telegram returned a message id and delivery was durably acknowledged.</summary>
    public const string Delivered = "delivered";

    /// <summary>Telegram definitively rejected the destination or request and automatic retries stopped.</summary>
    public const string FailedPermanent = "failed_permanent";

    /// <summary>An unknown failure or retry exhaustion requires an administrator decision.</summary>
    public const string ManualReview = "manual_review";

    /// <summary>
    /// A prior processing lease expired, so delivery may have succeeded even though acknowledgement was not saved.
    /// </summary>
    public const string DeliveryUncertain = "delivery_uncertain";
}

/// <summary>
/// Persists one customer-facing Telegram notification created by a successful owned-wallet settlement.
/// </summary>
/// <remarks>
/// The row is inserted into <c>users.db</c> in the same save that records the provider payment's first-credit marker.
/// A unique notification key prevents duplicate enqueue on callback replay. The background worker knows only this
/// table and Telegram, so retrying delivery can never re-enter financial settlement or credit a wallet twice.
///
/// Lifecycle: pending → processing → delivered, bounded transient retry back to pending, definitive failure to
/// failed-permanent, unknown/exhausted delivery to manual-review, or an abandoned processing lease to
/// delivery-uncertain. Delivery-uncertain rows are never resent automatically because Telegram may already have
/// accepted the message before the process stopped.
/// </remarks>
public sealed class PaymentSettlementNotification
{
    /// <summary>Auto-generated users.db primary key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Globally unique idempotency key formed from the owned-wallet provider and local payment-row id.
    /// </summary>
    public string NotificationKey { get; set; }

    /// <summary>Non-secret provider label such as nowpayments, hooshpay, tetraminator, or uniquepay.</summary>
    public string Provider { get; set; }

    /// <summary>Local users.db payment-row id; this is not a provider token, hash, or remote invoice secret.</summary>
    public int ProviderPaymentId { get; set; }

    /// <summary>
    /// Internal runtime bot id whose Telegram token must deliver the notification. Empty values resolve to the
    /// default owned bot for legacy payment rows.
    /// </summary>
    public string BotId { get; set; }

    /// <summary>Numeric Telegram user id whose owned wallet received the credit.</summary>
    public long TelegramUserId { get; set; }

    /// <summary>Numeric Telegram chat id that receives the settlement message.</summary>
    public long ChatId { get; set; }

    /// <summary>Credited wallet amount in Iranian toman, stored as a major currency unit.</summary>
    public long AmountToman { get; set; }

    /// <summary>Final plain-text Persian notification body, safe to send without a Telegram parse mode.</summary>
    public string MessageText { get; set; }

    /// <summary>Current value from <see cref="PaymentSettlementNotificationStatuses"/>.</summary>
    public string Status { get; set; } = PaymentSettlementNotificationStatuses.Pending;

    /// <summary>Total number of claimed Telegram delivery attempts, including the current processing attempt.</summary>
    public int AttemptCount { get; set; }

    /// <summary>UTC time when a pending row becomes eligible for its next delivery attempt.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>UTC expiry of the active processing claim; an expired claim is delivery-uncertain, not retryable.</summary>
    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>Opaque process-local claim id used to prevent an older worker attempt from updating a newer claim.</summary>
    public string ClaimToken { get; set; }

    /// <summary>Telegram message id returned after successful delivery; null until Telegram acknowledges a message.</summary>
    public int? TelegramMessageId { get; set; }

    /// <summary>Sanitized failure category/message containing no bot token, provider secret, or response body.</summary>
    public string LastError { get; set; }

    /// <summary>UTC creation time written with the original settlement marker.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC time of the latest delivery-state transition.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>UTC time when Telegram delivery was durably acknowledged.</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>
    /// Creates the durable notification that accompanies one first-time owned-wallet credit.
    /// </summary>
    /// <param name="provider">
    /// Stable non-secret provider key selected by the settlement service. It must not contain a token, hash, URL, or
    /// remote response body.
    /// </param>
    /// <param name="providerPaymentId">Positive local users.db payment-row id used for durable idempotency.</param>
    /// <param name="botId">Internal runtime bot id saved on the payment; null/empty legacy values use the default bot.</param>
    /// <param name="telegramUserId">Numeric Telegram id of the wallet owner from the settled payment row.</param>
    /// <param name="chatId">Numeric destination chat id chosen by settlement or the saved user profile.</param>
    /// <param name="amountToman">Positive wallet credit amount in Iranian toman.</param>
    /// <param name="messageText">Final plain-text Persian message to deliver; it must contain no provider secret.</param>
    /// <param name="createdAtUtc">UTC settlement time used as the initial due time.</param>
    /// <returns>
    /// A new untracked pending entity. The caller must add it to the same <c>UserDbContext</c> change set that records
    /// the first-credit marker and then call <c>SaveChanges</c> once.
    /// </returns>
    /// <remarks>
    /// The notification key intentionally omits settlement source and provisional/final labels. Only the first wallet
    /// credit for a provider payment may enqueue a customer message; later official reconciliation of a provisional
    /// credit cannot enqueue a second message.
    /// </remarks>
    /// <example>
    /// <code>
    /// context.PaymentSettlementNotifications.Add(
    ///     PaymentSettlementNotification.CreateOwnedWalletCredit(
    ///         "hooshpay", payment.Id, payment.BotId, payment.TelegramUserId,
    ///         payment.ChatId, payment.AmountToman, messageText, DateTime.UtcNow));
    /// await context.SaveChangesAsync(cancellationToken);
    /// </code>
    /// </example>
    public static PaymentSettlementNotification CreateOwnedWalletCredit(
        string provider,
        int providerPaymentId,
        string botId,
        long telegramUserId,
        long chatId,
        long amountToman,
        string messageText,
        DateTime createdAtUtc)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider)
            ? "unknown"
            : provider.Trim().ToLowerInvariant();

        return new PaymentSettlementNotification
        {
            NotificationKey = $"owned-wallet:{normalizedProvider}:{providerPaymentId}",
            Provider = normalizedProvider,
            ProviderPaymentId = providerPaymentId,
            BotId = botId,
            TelegramUserId = telegramUserId,
            ChatId = chatId,
            AmountToman = amountToman,
            MessageText = messageText ?? string.Empty,
            Status = PaymentSettlementNotificationStatuses.Pending,
            AttemptCount = 0,
            NextAttemptAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }
}
