namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Freezes one configured inventory entry for an explicitly confirmed bulk endpoint request.</summary>
/// <param name="BotId">Exact internal registry id shown in the confirmation, at most 64 characters; case variants identify the same bot.</param>
/// <param name="Identity">Numeric BotFather identity extracted from the displayed configuration, or zero for a token-missing entry; never a Telegram user or chat id.</param>
/// <param name="Revision">Nonnegative operator ControlRevision displayed in the confirmation, not the health-only persistence Revision.</param>
/// <remarks>Missing, disabled, tenant and assistant entries remain in the frozen inventory. Later additions must not be appended and replacement identities must not be substituted.</remarks>
public sealed record TelegramEndpointBulkTarget(string BotId, long Identity, long Revision);

/// <summary>Reports one frozen entry's intent admission without claiming its migration has completed.</summary>
/// <param name="BotId">Internal registry id copied from the frozen request, including a removed or replaced entry.</param>
/// <param name="Identity">BotFather identity copied from the frozen request; zero remains a visible missing-configuration result.</param>
/// <param name="ResultCode">Closed single-command result, or retained_cloud_control, not_submitted, registration_uncertain or batch_busy.</param>
/// <param name="OperationId">Exact committed operation id captured under the bot lock only for accepted or unchanged; null for other results and for an unchanged bot with no previous operation.</param>
/// <remarks>accepted means durable individual intent, not successful activation. registration_uncertain means persistence may have committed and must not be retried automatically. not_submitted means this batch never invoked migration admission for that entry. The aggregate is session-local; individual state/history survive restart.</remarks>
public sealed record TelegramEndpointBulkResult(string BotId, long Identity, string ResultCode, string OperationId);

/// <summary>Extends private owned-host administration with explicitly confirmed, frozen-inventory bulk admission.</summary>
public partial interface ITelegramEndpointAdministration
{
    /// <summary>Sequentially admits explicit endpoint intents for the entire confirmed inventory without draining receivers or calling Telegram.</summary>
    /// <param name="hostingBotId">Exact enabled nonassistant owned internal bot id from the authenticated private panel update, never a default-bot fallback.</param>
    /// <param name="hostingIdentity">Positive BotFather identity bound into that private confirmation; the current registry is rechecked before admission.</param>
    /// <param name="target">Explicit Cloud or Local destination, never a URL or an inferred toggle.</param>
    /// <param name="actor">Positive authenticated sender's Telegram user id, rechecked against the live global Super Admin allowlist; tenant ownership grants no authority.</param>
    /// <param name="targets">Required complete frozen inventory, in reporting order, with unique internal ids and the displayed identities/control revisions; an empty inventory returns an empty result.</param>
    /// <param name="cancellationToken">Private callback persistence budget; cancellation never cancels already admitted durable migrations.</param>
    /// <returns>One non-null result per frozen entry in the original order, including refusals and untouched tail entries. No added registry entry joins this batch.</returns>
    /// <remarks>The panel must authenticate the private chat, actor and single-use confirmation before calling. This boundary additionally authenticates the current owned host identity and live global actor. Only one bulk registration runs per coordinator; contenders return batch_busy without waiting.
    /// Under each existing bot lock, an active effective endpoint matching the destination returns unchanged without updating desired intent, revisions, operation metadata or the receiver; this includes recovered Cloud and degraded Local states.
    /// Local retains one eligible frozen owned Cloud control, preferring the host, and reports retained_cloud_control explicitly; individual safety checks remain authoritative. Cancellation or persistence exceptions stop submission, report a possibly committed current admission as registration_uncertain and leave the tail not_submitted. Never replay uncertain registration or describe accepted as completed. Aggregate reporting is not durable, but each accepted intent and its outcomes are durable.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
    /// <exception cref="ArgumentException">A frozen entry is null or has an empty or overlong internal id.</exception>
    /// <example><code>var results = await coordinator.RequestBulkMigrationAsync(host.Id, confirmedHostIdentity, TelegramEndpointType.Local, sender.Id, confirmedInventory, callbackToken);</code></example>
    Task<IReadOnlyList<TelegramEndpointBulkResult>> RequestBulkMigrationAsync(string hostingBotId,
        long hostingIdentity, TelegramEndpointType target, long actor,
        IReadOnlyList<TelegramEndpointBulkTarget> targets, CancellationToken cancellationToken);
}
