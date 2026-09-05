namespace Adminbot.Domain;

/// <summary>Immutable receipt of one committed global wallet mutation in credentials.db.</summary>
/// <remarks>
/// The key and balance change commit together. The receipt survives restarts and bridges eventual users.db ledger
/// reconciliation. User ids refer to Telegram users globally; BotId records origin only. Never delete receipts.
/// </remarks>
public sealed class WalletOperation
{
    /// <summary>Optional users.db inbox sequence that initiated this event; webhook/background events may have none.</summary>
    /// <remarks>Diagnostic cross-database reference only; the business key remains the unique financial identity.</remarks>
    public long? InboxSequence { get; set; }
    /// <summary>Globally unique, non-secret business event key; retries must reuse it.</summary>
    public string OperationKey { get; set; }
    /// <summary>Global Telegram user id owning the credentials wallet.</summary>
    public long TelegramUserId { get; set; }
    /// <summary>Signed amount in Iranian toman; positive credits and negative debits.</summary>
    public long AmountToman { get; set; }
    /// <summary>Persisted balance immediately before this mutation, in toman.</summary>
    public long BeforeBalance { get; set; }
    /// <summary>Persisted balance immediately after this mutation, in toman.</summary>
    public long AfterBalance { get; set; }
    /// <summary>Non-secret runtime bot id that originated the event, when available.</summary>
    public string BotId { get; set; }
    /// <summary>Credit authority: official, provisional, or partial; retained so recovery never promotes an exception into provider confirmation.</summary>
    public string ApprovalKind { get; set; } = "official";
    /// <summary>Authorizing Telegram administrator for a provisional credit, otherwise null.</summary>
    public long? ApprovedByTelegramUserId { get; set; }
    /// <summary>UTC commit preparation time; amounts and identities are immutable after insertion.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>UTC time when the matching users.db ledger receipt was verified; null means reconciliation is pending.</summary>
    public DateTime? ReconciledAtUtc { get; set; }
}
