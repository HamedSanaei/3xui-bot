namespace Adminbot.Domain;

/// <summary>Durable identity reserved before one non-repeatable XUI client creation request.</summary>
/// <remarks>Private client JSON contains configuration credentials; never log it. Only a durable Reserved-to-PostStarted claim authorizes POST; all uncertain states permit GET-only recovery.</remarks>
public sealed class XuiV3CreationOperation
{
    /// <summary>Durable mutation outcome; historical unknown attempts migrate to Ambiguous.</summary>
    public XuiV3CreationOutcome Outcome { get; set; } = XuiV3CreationOutcome.Reserved;
    /// <summary>UTC time the sole POST was durably authorized; never cleared to permit replay.</summary>
    public DateTime? PostStartedAtUtc { get; set; }
    /// <summary>Optional inbox execution that reserved this identity, retained for uncertainty review.</summary>
    public long? InboxSequence { get; set; }
    /// <summary>Stable purchase/order and account-index key; immutable and globally unique.</summary>
    public string OperationKey { get; set; }
    /// <summary>Credential-free hash of the panel endpoint; prevents cross-panel reuse.</summary>
    public string PanelKey { get; set; }
    /// <summary>Telegram account owner, globally identified.</summary>
    public long TelegramUserId { get; set; }
    /// <summary>Private immutable client payload, persisted before the first POST.</summary>
    public string ClientJson { get; set; }
    /// <summary>Immutable ordered inbound ids supplied to the only permitted creation attempt.</summary>
    public string InboundIdsJson { get; set; }
    /// <summary>Immutable quota, duration, IP-limit and flow inputs; excludes generated identities and timestamps.</summary>
    public string BusinessParametersJson { get; set; }
    /// <summary>UTC reservation time. A reservation may represent an ambiguous committed external effect.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>UTC time a successful read-back/result proved creation; Outcome determines whether an unproven result requires recovery.</summary>
    public DateTime? AppliedAtUtc { get; set; }
}

/// <summary>Durable creation boundary states; terminal rejection never grants another attempt for the same business key.</summary>
public enum XuiV3CreationOutcome
{
    /// <summary>Identity saved, but no POST authorized; exactly one atomic claimant may proceed.</summary>
    Reserved = 0,
    /// <summary>POST authorization committed; a crash even before socket send requires GET-only recovery.</summary>
    PostStarted = 1,
    /// <summary>Authoritative success or identity-safe read-back proves the account exists.</summary>
    Applied = 2,
    /// <summary>Definitive validation rejection; terminal for this key, a new explicit business event is required to retry.</summary>
    DefinitiveRejected = 3,
    /// <summary>Possible external effect, including unknown historical attempts; never retry POST by time expiry.</summary>
    Ambiguous = 4
}
