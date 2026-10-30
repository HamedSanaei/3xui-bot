namespace Adminbot.Domain;

/// <summary>Durable actor-bound second confirmation and audit for one provisional purchase recovery, not a payment.</summary>
/// <remarks>Retain with financial/order audit history. One row authorizes at most one execution; a new attempt requires fresh GET proof and a new row.
/// Foreign keys to order/saga are logical, not cascade deletes. Migration creates no rows and performs no settlement/backfill.</remarks>
public sealed class TenantCardProvisionalReview
{
    /// <summary>Internal users.db audit id; the only execution identifier carried in callback data.</summary>
    public long Id { get; set; }
    /// <summary>Internal tenant purchase order id, immutable.</summary>
    public int TenantBotOrderId { get; set; }
    /// <summary>Exact finalize saga key, never a revoke or renewal operation.</summary>
    public string OperationKey { get; set; }
    /// <summary>Authorized global administrator's Telegram user id, not the tenant owner.</summary>
    public long ActorTelegramUserId { get; set; }
    /// <summary>Owned bot in which the review was prepared; confirmations cannot cross bots.</summary>
    public string BotId { get; set; }
    /// <summary>Private Telegram chat that owns the confirmation.</summary>
    public long ChatId { get; set; }
    /// <summary>Credential-free SHA256 of immutable order, receipt, identity and frozen entitlement bindings.</summary>
    public string BindingHash { get; set; }
    /// <summary>SHA256 of the binding and mutable saga/order/receipt versions reviewed before approval.</summary>
    public string SnapshotHash { get; set; }
    /// <summary>Exact catalog inbound ids approved by this review, as a sorted JSON array; not a request payload.</summary>
    public string ExpectedInboundIdsJson { get; set; }
    /// <summary>Only claimed (proven pre-mutation) or proven (GET-only applied reconciliation) may be authorized.</summary>
    public string ResumeStep { get; set; }
    /// <summary>Original closed manual-review reason retained even after recovery succeeds.</summary>
    public string OriginalReason { get; set; }
    /// <summary>Sanitized boolean/numeric proof summary; no UUID, email, SubId, token or raw response.</summary>
    public string EvidenceJson { get; set; }
    /// <summary>reviewed, authorized, fulfilled or refused; authorization is consumed once and never automatically reset.</summary>
    public string Status { get; set; } = "reviewed";
    /// <summary>Safe durable outcome/refusal code; never an exception message.</summary>
    public string OutcomeCode { get; set; }
    /// <summary>UTC creation time of the second-confirmation audit.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Absolute UTC ten-minute confirmation expiry; refresh never extends this row.</summary>
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>UTC execution claim time; a crash never clears or auto-replays this authorization.</summary>
    public DateTime? AuthorizedAtUtc { get; set; }
    /// <summary>UTC time a durable result was recorded; null may mean an interrupted authorized attempt requiring fresh review.</summary>
    public DateTime? CompletedAtUtc { get; set; }
}
