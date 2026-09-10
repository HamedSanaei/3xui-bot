namespace Adminbot.Domain;

public enum TenantAccessDecision
{
    Allowed,
    OwnerBlocked,
    OwnerMissing,
    InsufficientFunding
}

public sealed record TenantAccessEvaluation(
    TenantAccessDecision Decision,
    long? BotBalanceToman,
    long? SiteWalletToman,
    bool SiteWalletUsable,
    long MinimumSiteWalletToman)
{
    public bool IsAllowed => Decision == TenantAccessDecision.Allowed;
    public string RestrictionMessage => Decision switch
    {
        TenantAccessDecision.Allowed => null,
        TenantAccessDecision.OwnerBlocked => TenantAccessService.BlockedMessage,
        _ => TenantAccessService.DebtMessage
    };
}

public static class TenantStorefrontFundingAlertKinds
{
    public const string UnderfundedTransition = "underfunded_transition";
    public const string CustomerAttempt = "customer_attempt";
}

public static class TenantStorefrontFundingAlertStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string ManualReview = "manual_review";
    public const string DeliveryUncertain = "delivery_uncertain";
    /// <summary>Terminal status for alerts superseded by a storefront funding recovery before any Telegram send.</summary>
    public const string Cancelled = "cancelled";
}

public sealed class TenantStorefrontFundingAlertState
{
    public string TenantBotId { get; set; } = string.Empty;
    public bool IsUnderfunded { get; set; }
    public int EpisodeNumber { get; set; }
    public DateTime? UnderfundedSinceUtc { get; set; }
    public DateTime? UnderfundedEpisodeNotifiedAtUtc { get; set; }
    public DateTime? LastCustomerAttemptAlertAtUtc { get; set; }
    public long LastObservedBotBalanceToman { get; set; }
    public long? LastObservedSiteWalletToman { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TenantStorefrontFundingAlert
{
    public int Id { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public string TenantBotId { get; set; } = string.Empty;
    public long OwnerTelegramUserId { get; set; }
    public string TenantBotUsername { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    /// <summary>
    /// Underfunded episode number this alert belongs to; zero marks rows persisted before episode tracking existed.
    /// </summary>
    /// <remarks>
    /// Matches <see cref="TenantStorefrontFundingAlertState.EpisodeNumber"/> at queue time. Before delivery the worker
    /// requires the storefront to still be underfunded in the same episode; a zero (legacy) episode is accepted while
    /// the storefront is underfunded because recovery cancellation already removes superseded rows.
    /// </remarks>
    public int EpisodeNumber { get; set; }
    public long BotBalanceToman { get; set; }
    public long? SiteWalletToman { get; set; }
    public long MinimumSiteWalletToman { get; set; }
    public string Status { get; set; } = TenantStorefrontFundingAlertStatuses.Pending;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string ClaimToken { get; set; }
    /// <summary>
    /// Durable send phase marker: null until the Telegram transport is invoked, set immediately before the first
    /// send attempt. An expired claim with this marker null is safely retried; an expired claim with it set is
    /// conservatively marked DeliveryUncertain because the remote outcome may be ambiguous.
    /// </summary>
    public DateTime? SendStartedAtUtc { get; set; }
    public int? TelegramMessageId { get; set; }
    public string LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
}
