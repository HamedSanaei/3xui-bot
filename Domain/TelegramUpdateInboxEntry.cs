namespace Adminbot.Domain;

/// <summary>Durable acceptance and execution receipt for one Telegram update.</summary>
/// <remarks>
/// BotId plus UpdateId deduplicates receiver redelivery. Sequence orders accepted work, including updates whose
/// Telegram ids have gaps. Payload contains private customer data and is erased on every terminal outcome. Business
/// recovery may outlive this receipt, but it never blocks later updates for the same bot/user execution key.
/// </remarks>
public sealed class TelegramUpdateInboxEntry
{
    /// <summary>Database-assigned acceptance sequence; never a Telegram identifier.</summary>
    public long Sequence { get; set; }
    /// <summary>Required runtime bot identity, independent of user id and excluding credentials.</summary>
    public string BotId { get; set; }
    /// <summary>Telegram update id, unique within this bot.</summary>
    public int UpdateId { get; set; }
    /// <summary>Telegram actor id, or zero for the bot-scoped no-actor fallback lane.</summary>
    public long TelegramUserId { get; set; }
    /// <summary>Telegram update type label for payload-free diagnostics.</summary>
    public string UpdateType { get; set; }
    /// <summary>Private serialized Telegram update; null after every success, failure, interruption, or review outcome.</summary>
    public string Payload { get; set; }
    /// <summary>Execution state: queued, running, completed, completed_with_error, or completed_with_review.</summary>
    public string Status { get; set; } = "queued";
    /// <summary>UTC acceptance time used for queue latency and deduplication retention.</summary>
    public DateTime AcceptedAtUtc { get; set; }
    /// <summary>UTC execution claim time, or null while queued.</summary>
    public DateTime? StartedAtUtc { get; set; }
    /// <summary>UTC terminal completion or review time; completed receipts expire after seven days.</summary>
    public DateTime? CompletedAtUtc { get; set; }
    /// <summary>Authenticated super-admin Telegram id that explicitly reviewed this row; null until reviewed.</summary>
    public long? ReviewedByTelegramUserId { get; set; }
    /// <summary>UTC explicit resolution time; retained with deduplication metadata for seven days.</summary>
    public DateTime? ReviewedAtUtc { get; set; }
    /// <summary>Restricted review-N numeric ticket reference; never arbitrary operator text.</summary>
    public string ReviewReference { get; set; }
    /// <summary>Coarse failure classification only; never an exception message or customer payload.</summary>
    public string FailureCode { get; set; }
}
