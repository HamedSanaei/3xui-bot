namespace Adminbot.Domain;

/// <summary>Classifies the exact stored owner's access restrictions and shared-wallet financial mode.</summary>
/// <remarks>
/// Funding eligibility is a positive shared bot wallet OR a usable Gozargah wallet at the configured minimum.
/// Insufficient funding changes new payment admission, not customer access; owner restrictions still deny access.
/// The tenant's manual Enabled switch is enforced separately by the storefront handler.
/// </remarks>
public enum TenantAccessDecision
{
    /// <summary>The owner is present, unblocked, and meets either side of the existing OR funding rule.</summary>
    Allowed,
    /// <summary>The shared owner is blocked; customers must not access any of that owner's storefronts.</summary>
    OwnerBlocked,
    /// <summary>The exact stored owner's credentials are missing; customer access fails closed.</summary>
    OwnerMissing,
    /// <summary>
    /// The unblocked owner meets neither funding condition. Customer access remains available, but new payments
    /// require the live central owned-bot gateways and must not use the tenant's personal card.
    /// </summary>
    InsufficientFunding
}

/// <summary>Immutable owner-scoped funding classification used for customer access, payment admission, and alerts.</summary>
/// <param name="Decision">The financial mode or owner restriction for the exact stored tenant owner.</param>
/// <param name="BotBalanceToman">Shared owner bot-wallet balance in toman, or null when the owner is missing.</param>
/// <param name="SiteWalletToman">Observed usable owner Gozargah balance in toman, or null when not read or unusable.</param>
/// <param name="SiteWalletUsable">Whether the observed Gozargah wallet is eligible for website funding.</param>
/// <param name="MinimumSiteWalletToman">Configured nonnegative website funding threshold in toman.</param>
/// <remarks>
/// Never reuse a snapshot for another owner. Allowed means bot balance &gt; 0 OR usable website balance at least
/// MinimumSiteWalletToman; it does not require both wallets to be positive. InsufficientFunding remains distinct
/// for payment fallback and alert recovery, although both financial modes permit navigation.
/// </remarks>
/// <example><code>var useCentralOnly = evaluation.RequiresPlatformPayments;</code></example>
public sealed record TenantAccessEvaluation(
    TenantAccessDecision Decision,
    long? BotBalanceToman,
    long? SiteWalletToman,
    bool SiteWalletUsable,
    long MinimumSiteWalletToman)
{
    /// <summary>Whether owner status permits customer access, including the central-payment fallback mode.</summary>
    /// <remarks>Does not override a tenant's manual Enabled switch or authorize personal-card payments.</remarks>
    public bool IsAllowed => Decision is TenantAccessDecision.Allowed or TenantAccessDecision.InsufficientFunding;

    /// <summary>Whether new payments must use live central gateways and exclude the tenant's personal card.</summary>
    /// <remarks>True exactly for InsufficientFunding; blocked or missing owners must instead be denied access.</remarks>
    public bool RequiresPlatformPayments => Decision == TenantAccessDecision.InsufficientFunding;

    /// <summary>Customer-facing owner restriction, or null in either accessible financial mode.</summary>
    /// <remarks>Insufficient funding never emits a debt refusal; missing and unknown owner states fail closed.</remarks>
    public string RestrictionMessage => Decision switch
    {
        TenantAccessDecision.Allowed or TenantAccessDecision.InsufficientFunding => null,
        TenantAccessDecision.OwnerBlocked => TenantAccessService.BlockedMessage,
        _ => "فروشگاه در حال حاضر غیرفعال است."
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
