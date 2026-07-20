using System.ComponentModel.DataAnnotations;

namespace Adminbot.Domain;

/// <summary>
/// Stable lifecycle values for one persisted XUI v3 account link-change operation.
/// </summary>
/// <remarks>
/// Active values participate in the database filtered unique index on panel plus numeric client id. This prevents
/// duplicate identity mutations across owned bots, tenant bots, concurrent callbacks, and application restarts.
/// </remarks>
public static class XuiV3LinkChangeStatuses
{
    /// <summary>The user has opened the confirmation screen but has not approved the mutation.</summary>
    public const string AwaitingConfirmation = "awaiting_confirmation";

    /// <summary>A foreground callback or background worker currently owns the operation lease.</summary>
    public const string Processing = "processing";

    /// <summary>The panel result is temporarily unknown and the same saved operation must be retried.</summary>
    public const string RecoveryPending = "recovery_pending";

    /// <summary>The new identity and every required preservation invariant were verified.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The operation failed before any identity mutation could be committed.</summary>
    public const string FailedBeforeMutation = "failed_before_mutation";

    /// <summary>Automatic retries were exhausted and an administrator must inspect the same locked operation.</summary>
    public const string ManualReview = "manual_review";

    /// <summary>The user cancelled the operation before confirmation and before any panel mutation.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The confirmation window elapsed before the user approved the operation.</summary>
    public const string Expired = "expired";

    /// <summary>
    /// Gets the statuses that keep a panel client locked against a second link change.
    /// </summary>
    /// <returns>A stable set used by stores, recovery workers, and UI status rendering.</returns>
    public static IReadOnlySet<string> Active { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AwaitingConfirmation,
        Processing,
        RecoveryPending,
        ManualReview
    };
}

/// <summary>
/// Persists one idempotent XUI v3 identity-change saga in <c>users.db</c>.
/// </summary>
/// <remarks>
/// The row owns the only generated replacement email, UUID, and subscription id for the operation. Snapshot and
/// payload JSON are captured before the first mutating panel request so recovery never derives a new identity from
/// a partially renamed account. The panel URL is represented only by <see cref="PanelKey"/>, a SHA-256 fingerprint.
/// </remarks>
public sealed class XuiV3LinkChangeOperation
{
    /// <summary>Database-generated primary key in <c>users.db</c>.</summary>
    public int Id { get; set; }

    /// <summary>Public random operation key carried by Telegram callbacks; it contains no panel secret.</summary>
    [Required]
    public string OperationKey { get; set; }

    /// <summary>SHA-256 fingerprint of the normalized panel base URL and root path.</summary>
    [Required]
    public string PanelKey { get; set; }

    /// <summary>Internal owned or tenant bot id that created the operation.</summary>
    [Required]
    public string BotId { get; set; }

    /// <summary>Telegram bot username retained for progress and audit attribution.</summary>
    public string BotUsername { get; set; }

    /// <summary>Owned or tenant runtime type retained for isolation and recovery.</summary>
    public string BotType { get; set; }

    /// <summary>Numeric Telegram user id that owns the selected account and operation callbacks.</summary>
    public long TelegramUserId { get; set; }

    /// <summary>Telegram chat id containing the editable progress message.</summary>
    public long ChatId { get; set; }

    /// <summary>Telegram message id edited as the operation progresses.</summary>
    public int MessageId { get; set; }

    /// <summary>Stable numeric XUI client id; changing link identity must never change this value.</summary>
    public int ClientId { get; set; }

    /// <summary>UI source, either <c>list</c> or <c>search</c>, used to build the final navigation keyboard.</summary>
    public string Source { get; set; }

    /// <summary>Zero-based account-list or search-results page restored after completion.</summary>
    public int Page { get; set; }

    /// <summary>Current value from <see cref="XuiV3LinkChangeStatuses"/>.</summary>
    [Required]
    public string Status { get; set; }

    /// <summary>Stable stage key used by progress UI, diagnostics, and recovery.</summary>
    public string Stage { get; set; }

    /// <summary>Original panel email captured before confirmation.</summary>
    public string OldEmail { get; set; }

    /// <summary>Original protocol UUID captured before confirmation.</summary>
    public string OldUuid { get; set; }

    /// <summary>Original subscription id captured before confirmation.</summary>
    public string OldSubId { get; set; }

    /// <summary>Single replacement email generated for this operation and reused by every retry.</summary>
    public string NewEmail { get; set; }

    /// <summary>Single replacement protocol UUID generated for this operation and reused by every retry.</summary>
    public string NewUuid { get; set; }

    /// <summary>Single replacement subscription id generated for this operation.</summary>
    public string NewSubId { get; set; }

    /// <summary>Immutable live pre-change snapshot serialized as JSON before the first mutation.</summary>
    public string SnapshotJson { get; set; }

    /// <summary>Complete update payload serialized as JSON before the first mutation.</summary>
    public string PayloadJson { get; set; }

    /// <summary>Whether read-back proved that the panel committed the saved replacement identity.</summary>
    public bool IdentityCommitted { get; set; }

    /// <summary>Number of processing attempts, including foreground confirmation and background recovery.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Earliest UTC time at which the recovery worker may claim this row.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>UTC lease expiry that prevents two workers from mutating the same client simultaneously.</summary>
    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>Non-secret latest diagnostic error. Raw panel URLs, tokens, and request bodies are forbidden.</summary>
    public string LastError { get; set; }

    /// <summary>UTC deadline for accepting the explicit user confirmation.</summary>
    public DateTime ConfirmationExpiresAtUtc { get; set; }

    /// <summary>UTC time at which the user explicitly confirmed the mutation.</summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>UTC creation time of the confirmation operation.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC time of the most recent persisted transition.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>UTC completion time for succeeded, cancelled, expired, or pre-mutation-failed operations.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Whether the Gozargah rename outbox action was queued after final verification.</summary>
    public bool SiteSyncQueued { get; set; }

    /// <summary>Whether the normal success audit and private payment-style log were emitted.</summary>
    public bool SuccessAuditLogged { get; set; }

    /// <summary>Whether the final success message was sent or edited for the customer.</summary>
    public bool SuccessNotificationSent { get; set; }
}

/// <summary>
/// Classifies whether a panel read observed authoritative data, proved absence, or could not determine either state.
/// </summary>
public enum XuiV3LinkChangeObservationStatus
{
    /// <summary>Required panel endpoints returned enough authoritative data to continue.</summary>
    Observed,

    /// <summary>Successful panel reads proved that the requested numeric client or identity does not exist.</summary>
    NotFound,

    /// <summary>A timeout, transport error, HTTP 520, or incomplete response prevented a safe conclusion.</summary>
    Unknown
}
