namespace Adminbot.Domain;

/// <summary>Durable identity reserved before one non-repeatable XUI client creation request.</summary>
/// <remarks>Private client JSON contains configuration credentials; never log it. Existing reservations authorize GET-only recovery.</remarks>
public sealed class XuiV3CreationOperation
{
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
    /// <summary>UTC time a successful read-back/result proved creation; null remains GET-only recoverable.</summary>
    public DateTime? AppliedAtUtc { get; set; }
}
