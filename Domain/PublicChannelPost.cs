#nullable enable
namespace Adminbot.Domain;

/// <summary>Identifies one private super-admin composition within an exact source bot.</summary>
/// <param name="SourceBotId">Exact internal Owned bot id; never a Telegram numeric bot id.</param>
/// <param name="TelegramUserId">Configured super-admin's positive Telegram sender id.</param>
/// <param name="ChatId">Positive private chat id, which must equal the sender id.</param>
public sealed record PublicChannelPostKey(string SourceBotId, long TelegramUserId, long ChatId);

/// <summary>Closed lifecycle of a process-local composition or its one-shot publication.</summary>
public enum PublicChannelPostPhase
{
    /// <summary>Content may be collected; no publication permission exists.</summary>
    Editing,
    /// <summary>Background preparation or an actual private preview is in progress.</summary>
    PreparingPreview,
    /// <summary>The actual preview and its exact control keyboard both succeeded.</summary>
    PreviewReady,
    /// <summary>An immutable publication was admitted once; editing cannot change it.</summary>
    PublishQueued,
    /// <summary>The worker is processing the frozen destination inventory.</summary>
    Publishing,
    /// <summary>All frozen targets have a terminal result; progress remains briefly available.</summary>
    Completed,
    /// <summary>An unpublished composition was abandoned.</summary>
    Cancelled,
    /// <summary>An unpublished composition or retained result exceeded its lifetime.</summary>
    Expired
}

/// <summary>Detached, immutable state safe for update handlers; no media, clients or locks escape.</summary>
/// <param name="Key">Exact bot, actor and private-chat ownership.</param>
/// <param name="Id">Ten-character process-local draft identity used in callback data.</param>
/// <param name="Revision">Current callback revision; rendered as hexadecimal in callbacks.</param>
/// <param name="Phase">Current lifecycle phase.</param>
/// <param name="PhotoCount">Number of ordered photo occurrences, from zero to ten.</param>
/// <param name="HasText">Whether the original common text/caption is nonempty.</param>
/// <param name="HasCaptionConflict">Whether a separate text message must resolve conflicting captions.</param>
/// <param name="ControlMessageId">Exact bound private control message, or zero before binding.</param>
/// <param name="ExpiresAtUtc">Editing/preview expiry or retained completed-result expiry in UTC.</param>
public sealed record PublicChannelPostDraftSnapshot(PublicChannelPostKey Key, string Id, long Revision,
    PublicChannelPostPhase Phase, int PhotoCount, bool HasText, bool HasCaptionConflict,
    int ControlMessageId, DateTimeOffset ExpiresAtUtc);

/// <summary>Reports atomic admission without exposing mutable composition content.</summary>
/// <param name="Accepted">True only when the requested state change or queue admission succeeded.</param>
/// <param name="ReasonCode">Closed sanitized reason code; empty on success.</param>
/// <param name="Draft">Current snapshot, or null for absent, expired or unauthorized drafts.</param>
public sealed record PublicChannelPostCommandResult(bool Accepted, string ReasonCode, PublicChannelPostDraftSnapshot? Draft);

/// <summary>One sanitized excluded or unsuccessful bot/channel result; never a raw provider error.</summary>
/// <param name="BotId">Sanitized internal bot label, not a credential.</param>
/// <param name="Channel">Validated public username/numeric id, or a generic invalid-setting label.</param>
/// <param name="Outcome">Closed category: skipped, failed or uncertain.</param>
/// <param name="ReasonCode">Sanitized machine-readable failure reason.</param>
public sealed record PublicChannelPostTargetResult(string BotId, string Channel, string Outcome, string ReasonCode);

/// <summary>Immutable publication accounting; preparation exclusions never inflate the frozen denominator.</summary>
/// <param name="EligibleTotal">Frozen prepared target count.</param>
/// <param name="Processed">Terminal attempts/skips within the frozen target set.</param>
/// <param name="Sent">Targets whose actual content request succeeded.</param>
/// <param name="SkippedDuringPreparation">Candidates excluded before confirmation.</param>
/// <param name="SkippedAfterPreparation">Frozen targets suppressed by final live checks.</param>
/// <param name="Failed">Authoritatively rejected content requests.</param>
/// <param name="Uncertain">Ambiguous requests that must not be automatically replayed.</param>
/// <param name="Details">Read-only sanitized excluded/unsuccessful target details; may be empty.</param>
public sealed record PublicChannelPostProgressSnapshot(int EligibleTotal, int Processed, int Sent,
    int SkippedDuringPreparation, int SkippedAfterPreparation, int Failed, int Uncertain,
    IReadOnlyList<PublicChannelPostTargetResult> Details);
