namespace Adminbot.Domain;

/// <summary>
/// Configures the global referral program shared by all owned Telegram bots.
/// </summary>
/// <remarks>
/// Tenant storefront payments are never eligible. When <see cref="Enabled"/> is <c>true</c>, startup validation
/// requires every amount and percentage to be explicitly valid; the service does not substitute business defaults.
/// </remarks>
public sealed class ReferralOptions
{
    /// <summary>
    /// Enables referral registration, reporting, and reward settlement for owned bots.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Smallest final wallet payment, in Iranian toman, that consumes referral payment eligibility.
    /// </summary>
    public long MinimumEligiblePaymentAmountToman { get; set; }

    /// <summary>
    /// Reward rules applied to the first eligible payment made after a referral relationship is created.
    /// </summary>
    public ReferralFirstPaymentOptions FirstPayment { get; set; } = new();

    /// <summary>
    /// Reward rules applied to every eligible payment after the first eligible payment.
    /// </summary>
    public ReferralSubsequentPaymentOptions SubsequentPayments { get; set; } = new();
}

/// <summary>
/// Defines percentage and floor/cap rules for the first eligible referral payment.
/// </summary>
public sealed class ReferralFirstPaymentOptions
{
    /// <summary>
    /// Percentage of the source payment credited to the referrer on the first eligible payment.
    /// </summary>
    public decimal ReferrerRewardPercent { get; set; }

    /// <summary>
    /// Percentage of the source payment used to calculate the referred user's first-payment reward.
    /// </summary>
    public decimal ReferredRewardPercent { get; set; }

    /// <summary>
    /// Minimum referred-user reward in Iranian toman after percentage calculation.
    /// </summary>
    public long ReferredMinimumRewardToman { get; set; }

    /// <summary>
    /// Optional maximum referred-user reward in Iranian toman; zero disables the cap.
    /// </summary>
    public long ReferredMaximumRewardToman { get; set; }
}

/// <summary>
/// Defines the referrer percentage used after a referred user has consumed first-payment eligibility.
/// </summary>
public sealed class ReferralSubsequentPaymentOptions
{
    /// <summary>
    /// Percentage of each subsequent eligible source payment credited to the referrer.
    /// </summary>
    public decimal ReferrerRewardPercent { get; set; }
}

/// <summary>
/// Stores the immutable, system-wide relationship between one referred Telegram user and the first referrer.
/// </summary>
/// <remarks>
/// The database has a unique constraint on <see cref="ReferredTelegramUserId"/> and a check constraint preventing
/// self-referral. <see cref="AttributionBotId"/> is reporting metadata only and never participates in uniqueness.
/// </remarks>
public sealed class ReferralRelationship
{
    /// <summary>Internal users.db identity key.</summary>
    public long Id { get; set; }

    /// <summary>Numeric Telegram user id of the immutable referrer.</summary>
    public long ReferrerTelegramUserId { get; set; }

    /// <summary>Numeric Telegram user id that can have only one referrer globally.</summary>
    public long ReferredTelegramUserId { get; set; }

    /// <summary>Owned bot id where the referral payload was first accepted, retained only for attribution.</summary>
    public string AttributionBotId { get; set; }

    /// <summary>Stable referral code decoded from the owned-bot <c>/start ref_...</c> payload.</summary>
    public string ReferralCode { get; set; }

    /// <summary>UTC time when the immutable relationship was persisted.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents one eligible owned-bot wallet payment being processed by the referral engine.
/// </summary>
/// <remarks>
/// The source key is globally unique. A filtered unique index also allows only one first-eligible event for each
/// referred user, which is the final database protection when several owned bots settle payments concurrently.
/// </remarks>
public sealed class ReferralPaymentEvent
{
    /// <summary>Internal users.db identity key.</summary>
    public long Id { get; set; }

    /// <summary>Relationship that existed before the source payment was settled.</summary>
    public long ReferralRelationshipId { get; set; }

    /// <summary>Globally stable provider/payment-type/provider-id idempotency key.</summary>
    public string SourcePaymentKey { get; set; }

    /// <summary>Normalized real payment provider name, for example <c>hooshpay</c>.</summary>
    public string Provider { get; set; }

    /// <summary>Normalized payment type; referral settlement currently accepts only <c>wallet_charge</c>.</summary>
    public string PaymentType { get; set; }

    /// <summary>Provider-owned payment, invoice, or track identifier without secrets.</summary>
    public string ProviderPaymentId { get; set; }

    /// <summary>Owned bot id that accepted the source payment, retained for attribution only.</summary>
    public string BotId { get; set; }

    /// <summary>Numeric Telegram user id whose successful wallet charge caused this event.</summary>
    public long ReferredTelegramUserId { get; set; }

    /// <summary>Numeric Telegram user id that owns the referral link.</summary>
    public long ReferrerTelegramUserId { get; set; }

    /// <summary>Final source wallet-credit amount in Iranian toman.</summary>
    public long SourceAmountToman { get; set; }

    /// <summary>Indicates this event consumed the referred user's first eligible payment.</summary>
    public bool IsFirstEligiblePayment { get; set; }

    /// <summary>Processing state such as pending, completed, or failed.</summary>
    public string Status { get; set; } = ReferralProcessingStatuses.Pending;

    /// <summary>Number of reconciliation attempts made for this event.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last non-secret processing error retained for admin diagnostics.</summary>
    public string LastError { get; set; }

    /// <summary>UTC provider settlement time used to reject payments predating the relationship.</summary>
    public DateTime SourceSettledAtUtc { get; set; }

    /// <summary>UTC time when the event was first persisted.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC time of the latest processing change.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>UTC time when every planned reward reached the applied state.</summary>
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>
/// Stores one beneficiary reward and the exact configuration snapshot used to calculate it.
/// </summary>
/// <remarks>
/// A composite unique constraint on source payment, beneficiary, and reward kind prevents duplicate credits.
/// The row remains retryable when reward planning or ledger persistence is interrupted. An ambiguous interruption
/// during the unchanged credentials wallet call is failed closed for manual review.
/// </remarks>
public sealed class ReferralReward
{
    /// <summary>Internal users.db identity key.</summary>
    public long Id { get; set; }

    /// <summary>Referral payment event that planned this reward.</summary>
    public long ReferralPaymentEventId { get; set; }

    /// <summary>Immutable relationship used for this reward.</summary>
    public long ReferralRelationshipId { get; set; }

    /// <summary>Stable source payment key copied from the parent event.</summary>
    public string SourcePaymentKey { get; set; }

    /// <summary>Numeric Telegram user id whose bot wallet receives the reward.</summary>
    public long BeneficiaryTelegramUserId { get; set; }

    /// <summary>Numeric Telegram user id of the referrer for reporting.</summary>
    public long ReferrerTelegramUserId { get; set; }

    /// <summary>Numeric Telegram user id of the referred payer for reporting.</summary>
    public long ReferredTelegramUserId { get; set; }

    /// <summary>Owned bot id retained for attribution and notification routing.</summary>
    public string BotId { get; set; }

    /// <summary>Reward classification from <see cref="ReferralRewardKinds"/>.</summary>
    public string RewardKind { get; set; }

    /// <summary>Positive reward amount in Iranian toman.</summary>
    public long RewardAmountToman { get; set; }

    /// <summary>Original eligible payment amount in Iranian toman.</summary>
    public long SourceAmountToman { get; set; }

    /// <summary>Percentage snapshot used for this reward.</summary>
    public decimal RewardPercentSnapshot { get; set; }

    /// <summary>Minimum reward snapshot in Iranian toman; zero means no floor.</summary>
    public long MinimumRewardTomanSnapshot { get; set; }

    /// <summary>Maximum reward snapshot in Iranian toman; zero means no cap.</summary>
    public long MaximumRewardTomanSnapshot { get; set; }

    /// <summary>Reward processing state such as pending, applied, failed, or reversed.</summary>
    public string Status { get; set; } = ReferralProcessingStatuses.Pending;

    /// <summary>Stable users.db ledger/idempotency key for this reward.</summary>
    public string WalletMutationKey { get; set; }

    /// <summary>Optional users.db wallet ledger row linked after successful audit persistence.</summary>
    public int? WalletLedgerEntryId { get; set; }

    /// <summary>Bot-wallet balance in Iranian toman before the reward credit.</summary>
    public long? BalanceBefore { get; set; }

    /// <summary>Bot-wallet balance in Iranian toman after the reward credit.</summary>
    public long? BalanceAfter { get; set; }

    /// <summary>Number of reward application attempts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last non-secret failure retained for reconciliation diagnostics.</summary>
    public string LastError { get; set; }

    /// <summary>UTC time when the reward plan was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC time when the wallet and matching ledger entry were confirmed.</summary>
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>UTC time when the beneficiary successfully received the Telegram reward notification.</summary>
    public DateTime? NotifiedAtUtc { get; set; }

    /// <summary>UTC time of the latest reward state change.</summary>
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Reward-kind constants persisted in referral reward rows and ledger descriptions.
/// </summary>
public static class ReferralRewardKinds
{
    /// <summary>Referrer reward from the referred user's first eligible payment.</summary>
    public const string ReferrerFirstPayment = "referrer_first_payment";

    /// <summary>Referred-user reward from their own first eligible payment.</summary>
    public const string ReferredFirstPayment = "referred_first_payment";

    /// <summary>Referrer reward from a subsequent eligible payment.</summary>
    public const string ReferrerRecurringPayment = "referrer_recurring_payment";

    /// <summary>Audit kind reserved for a future reward reversal entry.</summary>
    public const string ReferralRewardReversal = "referral_reward_reversal";
}

/// <summary>
/// Processing-state constants shared by referral events and rewards.
/// </summary>
public static class ReferralProcessingStatuses
{
    /// <summary>Persisted work that has not completed yet.</summary>
    public const string Pending = "pending";

    /// <summary>
    /// Reward intent persisted in users.db immediately before calling the unchanged credentials wallet API.
    /// </summary>
    public const string Crediting = "crediting";

    /// <summary>
    /// Wallet credit completed and the reward is waiting only for its idempotent users.db ledger entry.
    /// </summary>
    public const string Credited = "credited";

    /// <summary>Reward wallet mutation and ledger entry have both completed.</summary>
    public const string Applied = "applied";

    /// <summary>All rewards associated with an event have completed.</summary>
    public const string Completed = "completed";

    /// <summary>
    /// Work that failed before a wallet mutation, or an ambiguous interrupted wallet call that requires review.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>A previously applied reward was reversed by an explicit future reversal flow.</summary>
    public const string Reversed = "reversed";
}

/// <summary>
/// Registration outcomes returned to the owned-bot <c>/start ref_...</c> flow.
/// </summary>
public enum ReferralRegistrationStatus
{
    /// <summary>A new global relationship was persisted.</summary>
    Created,

    /// <summary>The referred user already had the same immutable referrer.</summary>
    AlreadyRegistered,

    /// <summary>The referred user already had another referrer and the first relationship won.</summary>
    DifferentReferrerAlreadyRegistered,

    /// <summary>The decoded referrer id equals the referred Telegram user id.</summary>
    SelfReferralRejected,

    /// <summary>The referral code was malformed or did not map to an existing credentials user.</summary>
    InvalidCode,

    /// <summary>The feature is disabled or the request did not originate from an owned bot.</summary>
    NotAvailable
}

/// <summary>
/// Result returned after attempting to persist an immutable referral relationship.
/// </summary>
/// <param name="Status">Exact registration outcome.</param>
/// <param name="Relationship">Persisted relationship when one exists; otherwise <c>null</c>.</param>
public sealed record ReferralRegistrationResult(
    ReferralRegistrationStatus Status,
    ReferralRelationship Relationship);

/// <summary>
/// Describes a final owned-bot wallet payment presented to referral settlement.
/// </summary>
/// <param name="Provider">Normalized real provider name such as <c>nowpayments</c>, <c>hooshpay</c>, or <c>zibal</c>.</param>
/// <param name="PaymentType">Payment purpose; only <c>wallet_charge</c> is eligible.</param>
/// <param name="ProviderPaymentId">Stable provider payment, invoice, or track id used in the source key.</param>
/// <param name="BotId">Owned bot id that originated the payment, retained only for attribution.</param>
/// <param name="BotType">Runtime bot type; values other than <c>owned</c> are rejected.</param>
/// <param name="TelegramUserId">Numeric Telegram id of the wallet owner and referred payer.</param>
/// <param name="AmountToman">Final original wallet credit amount in Iranian toman.</param>
/// <param name="SettledAtUtc">UTC time when the original provider payment was finally settled.</param>
/// <param name="OriginalWalletCreditApplied">Whether the original wallet mutation completed successfully.</param>
/// <param name="IsProviderFinal">Whether the real provider reported a final successful state.</param>
/// <param name="IsProvisional">Whether the credit was a temporary admin exception; provisional values are excluded.</param>
public sealed record ReferralPaymentSource(
    string Provider,
    string PaymentType,
    string ProviderPaymentId,
    string BotId,
    string BotType,
    long TelegramUserId,
    long AmountToman,
    DateTime SettledAtUtc,
    bool OriginalWalletCreditApplied,
    bool IsProviderFinal,
    bool IsProvisional);

/// <summary>
/// Summary shown to an owned-bot user on the referral screen.
/// </summary>
/// <param name="InvitedCount">Total immutable relationships where the user is the referrer.</param>
/// <param name="EligibleReferralCount">Invited users that completed a first eligible payment.</param>
/// <param name="TotalAppliedRewardToman">Total applied rewards credited to this user's bot wallet.</param>
/// <param name="PendingRewardCount">Number of planned rewards still awaiting successful application.</param>
/// <param name="FailedRewardCount">Number of retryable rewards whose latest attempt failed.</param>
public sealed record ReferralUserStats(
    int InvitedCount,
    int EligibleReferralCount,
    long TotalAppliedRewardToman,
    int PendingRewardCount,
    int FailedRewardCount);
