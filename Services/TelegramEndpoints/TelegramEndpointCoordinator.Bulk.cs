using Adminbot.Domain;
using Microsoft.Extensions.Configuration;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Registers confirmed frozen-inventory endpoint intents without running the individual migration protocol.</summary>
public sealed partial class TelegramEndpointCoordinator
{
    /// <summary>Nonwaiting coordinator-wide admission lock; individual intents remain durably serialized by their existing bot locks.</summary>
    private readonly SemaphoreSlim _bulkIntents = new(1, 1);
    /// <summary>Call-local receipt capture used only while a bulk target invokes the existing individual command.</summary>
    private readonly AsyncLocal<BulkAdmissionCapture> _bulkAdmission = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TelegramEndpointBulkResult>> RequestBulkMigrationAsync(string hostingBotId,
        long hostingIdentity, TelegramEndpointType target, long actor,
        IReadOnlyList<TelegramEndpointBulkTarget> targets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var frozen = targets.ToArray();
        if (frozen.Length == 0) return Array.Empty<TelegramEndpointBulkResult>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = !Enum.IsDefined(target);
        foreach (var entry in frozen)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.BotId) || entry.BotId.Length > 64)
                throw new ArgumentException("A frozen endpoint entry requires a valid internal bot id.", nameof(targets));
            invalid |= !ids.Add(entry.BotId) || entry.Identity < 0 || entry.Revision < 0;
        }
        var results = new TelegramEndpointBulkResult[frozen.Length];
        var hostRefusal = ValidateBulkHost(hostingBotId, hostingIdentity, actor);
        if (invalid || hostRefusal != null)
        {
            FillBulkResults(frozen, results, 0, hostRefusal ?? "denied");
            return results;
        }
        if (cancellationToken.IsCancellationRequested) return CompleteBulkResults(frozen, results);
        if (!_bulkIntents.Wait(0))
        {
            FillBulkResults(frozen, results, 0, "batch_busy");
            return results;
        }

        SemaphoreSlim retainedLock = null;
        try
        {
            int retainedIndex;
            TelegramEndpointState retainedState;
            try
            {
                await InitializeAsync(cancellationToken);
                (retainedIndex, retainedLock, retainedState) = target == TelegramEndpointType.Local
                    ? await SelectBulkCloudControlAsync(frozen, hostingBotId, cancellationToken)
                    : (-1, null, null);
            }
            catch (Exception)
            {
                // No migration admission has started, so even a failed persistence read cannot make an intent uncertain.
                return CompleteBulkResults(frozen, results);
            }

            for (var index = 0; index < frozen.Length; index++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                hostRefusal = ValidateBulkHost(hostingBotId, hostingIdentity, actor);
                if (hostRefusal != null)
                {
                    FillBulkResults(frozen, results, index, hostRefusal);
                    break;
                }
                var entry = frozen[index];
                if (index == retainedIndex)
                {
                    var refusal = ValidateRetainedBulkControl(entry, retainedState, actor);
                    results[index] = new(entry.BotId, entry.Identity, refusal ?? "retained_cloud_control", null);
                    continue;
                }

                var capture = new BulkAdmissionCapture(entry.BotId, entry.Identity);
                var previousCapture = _bulkAdmission.Value;
                _bulkAdmission.Value = capture;
                try
                {
                    var code = await RequestMigrationAsync(entry.BotId, target, actor,
                        entry.Revision, entry.Identity, cancellationToken);
                    if (code is "accepted" or "unchanged")
                    {
                        if (!capture.Captured)
                        {
                            results[index] = new(entry.BotId, entry.Identity, "registration_uncertain", null);
                            break;
                        }
                        results[index] = new(entry.BotId, entry.Identity, code, capture.OperationId);
                    }
                    else results[index] = new(entry.BotId, entry.Identity, code, null);
                }
                catch (Exception)
                {
                    // A store may have committed before throwing or observing cancellation. Never replay this target.
                    results[index] = new(entry.BotId, entry.Identity, "registration_uncertain", null);
                    break;
                }
                finally
                {
                    capture.Active = false;
                    _bulkAdmission.Value = previousCapture;
                }
            }
            return CompleteBulkResults(frozen, results);
        }
        finally
        {
            retainedLock?.Release();
            _bulkIntents.Release();
        }
    }

    /// <summary>Rechecks the current owned private-panel host and live global authority without requiring a post-intent route.</summary>
    /// <param name="hostingBotId">Exact internal hosting id supplied by the authenticated panel.</param>
    /// <param name="hostingIdentity">Positive BotFather identity frozen by the confirmation.</param>
    /// <param name="actor">Authenticated Telegram sender's positive user id, not tenant ownership.</param>
    /// <returns>Null for an eligible current host, otherwise a closed authorization/configuration/identity refusal.</returns>
    /// <remarks>Private chat/session authentication belongs to the Telegram panel because this API accepts no chat or message metadata. Cloud bulk admission may fence the host; ordinary route availability is deliberately not an authorization prerequisite.</remarks>
    private string ValidateBulkHost(string hostingBotId, long hostingIdentity, long actor)
    {
        if (actor <= 0 || _configuration.GetSection(nameof(AppConfig.AdminsUserIds)).Get<List<long>>()?.Contains(actor) != true)
            return "denied";
        if (!_options.Enabled) return "disabled";
        var host = FindBot(hostingBotId);
        if (host == null || !host.Enabled) return "unavailable";
        if (host.Type != BotInstanceTypes.Owned || host.IsSalesAssistant) return "denied";
        var identity = TelegramBotTokenIdentity.ExtractBotId(host.Token);
        if (identity is not > 0) return "unavailable";
        if (hostingIdentity <= 0 || identity.Value != hostingIdentity) return "stale";
        return HasIdentityAlias(host.Id, identity.Value) ? "aliased" : null;
    }

    /// <summary>Selects and temporarily locks one eligible frozen Cloud control, preferring the host then ordinal internal id.</summary>
    /// <param name="frozen">Complete immutable inventory copied from the confirmation; later additions are not candidates.</param>
    /// <param name="hostingBotId">Current owned hosting id preferred when its frozen state remains eligible.</param>
    /// <param name="token">Callback persistence budget; no network or migration is invoked.</param>
    /// <returns>The frozen index, held existing bot lock and exact state, or index -1 and nulls when no frozen control qualifies.</returns>
    /// <remarks>The caller releases the returned lock in finally. Holding it only for bounded intent admission makes concurrent individual intents return busy rather than moving this control mid-batch. The individual protocol still rechecks independent control before logout; no route is reopened.</remarks>
    private async Task<(int Index, SemaphoreSlim Mutex, TelegramEndpointState State)> SelectBulkCloudControlAsync(
        TelegramEndpointBulkTarget[] frozen, string hostingBotId, CancellationToken token)
    {
        var ordered = Enumerable.Range(0, frozen.Length)
            .OrderBy(index => string.Equals(frozen[index].BotId, hostingBotId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(index => frozen[index].BotId, StringComparer.Ordinal);
        foreach (var index in ordered)
        {
            token.ThrowIfCancellationRequested();
            var entry = frozen[index];
            var bot = FindBot(entry.BotId);
            if (bot == null || !bot.Enabled || bot.Type != BotInstanceTypes.Owned || bot.IsSalesAssistant ||
                entry.Identity <= 0 || TelegramBotTokenIdentity.ExtractBotId(bot.Token) != entry.Identity ||
                HasIdentityAlias(bot.Id, entry.Identity)) continue;
            var mutex = GetLock(bot.Id);
            if (!mutex.Wait(0)) continue;
            var retained = false;
            try
            {
                var state = await _store.GetOrCreateAsync(bot.Id, entry.Identity, token);
                if (!IsEligibleBulkCloudControl(entry, state)) continue;
                retained = true;
                return (index, mutex, state);
            }
            finally { if (!retained) mutex.Release(); }
        }
        return (-1, null, null);
    }

    /// <summary>Checks actual Cloud admission and the exact frozen owned control without a network health probe.</summary>
    /// <param name="entry">Frozen internal/BotFather identity and operator control revision.</param>
    /// <param name="state">Detached current state read while holding that bot's existing coordinator lock.</param>
    /// <returns>True only for an enabled nonassistant unaliased owned bot with matching revision and active available Cloud admission.</returns>
    /// <remarks>Routing availability is not evidence that a receiver or Telegram is healthy. A fenced or unhydrated route never serves as retained control.</remarks>
    private bool IsEligibleBulkCloudControl(TelegramEndpointBulkTarget entry, TelegramEndpointState state)
    {
        var bot = FindBot(entry.BotId);
        if (bot == null || !bot.Enabled || bot.Type != BotInstanceTypes.Owned || bot.IsSalesAssistant ||
            TelegramBotTokenIdentity.ExtractBotId(bot.Token) != entry.Identity ||
            state.TelegramBotId != entry.Identity || state.ControlRevision != entry.Revision ||
            state.EffectiveEndpoint != TelegramEndpointType.Cloud || !IsActive(state) ||
            HasIdentityAlias(bot.Id, entry.Identity)) return false;
        try
        {
            var route = _gate.GetRoute(bot.Id, entry.Identity);
            return route.Available && route.Endpoint == TelegramEndpointType.Cloud;
        }
        catch (BotTransportUnavailableException) { return false; }
    }

    /// <summary>Reports a retained entry only while its locked snapshot and current registry/gate still justify the decision.</summary>
    /// <param name="entry">Exact frozen target deliberately omitted from Local migration.</param>
    /// <param name="state">Current detached state protected by its held coordinator lock.</param>
    /// <param name="actor">Authenticated global administrator, rechecked by the existing command validation.</param>
    /// <returns>Null for explicit retention, otherwise the actual closed refusal, never an operation id.</returns>
    private string ValidateRetainedBulkControl(TelegramEndpointBulkTarget entry, TelegramEndpointState state, long actor)
    {
        var refusal = ValidateCommand(entry.BotId, actor, out _, out var identity);
        if (refusal != null) return refusal;
        if (identity != entry.Identity || state.ControlRevision != entry.Revision) return "stale";
        return IsEligibleBulkCloudControl(entry, state) ? null : "control_path_missing";
    }

    /// <summary>Captures the exact committed operation metadata under the individual command's existing bot lock.</summary>
    /// <param name="state">Committed accepted state, or exact current unchanged state, never an uncommitted proposal.</param>
    /// <remarks>Only accepted/unchanged callsites invoke this helper. Single commands have no capture context and allocate nothing. The holder is inactive after its originating bulk call, so inherited execution contexts cannot overwrite a completed result.</remarks>
    private void CaptureBulkAdmission(TelegramEndpointState state)
    {
        var capture = _bulkAdmission.Value;
        if (capture?.Active != true || capture.Identity != state.TelegramBotId ||
            !string.Equals(capture.BotId, state.BotId, StringComparison.OrdinalIgnoreCase)) return;
        capture.OperationId = state.OperationId;
        capture.Captured = true;
    }

    /// <summary>Completes only untouched entries as not_submitted without allocating outcomes that would be discarded after admission.</summary>
    /// <param name="frozen">Exact confirmation snapshots in original reporting order.</param>
    /// <param name="results">Fixed-size aggregate containing the already observed individual outcomes and null untouched entries.</param>
    /// <returns>The same array with a non-null truthful result for every frozen entry and all committed receipts retained.</returns>
    private static TelegramEndpointBulkResult[] CompleteBulkResults(TelegramEndpointBulkTarget[] frozen,
        TelegramEndpointBulkResult[] results)
    {
        for (var index = 0; index < frozen.Length; index++)
            results[index] ??= new(frozen[index].BotId, frozen[index].Identity, "not_submitted", null);
        return results;
    }

    /// <summary>Fills an untouched result tail with a uniform authorization or nonwaiting-batch refusal.</summary>
    /// <param name="frozen">Exact immutable identity snapshots.</param>
    /// <param name="results">Fixed-size aggregate with any earlier individual outcomes retained.</param>
    /// <param name="start">Zero-based first untouched entry, inclusive.</param>
    /// <param name="code">Closed refusal code; operation metadata is always null.</param>
    private static void FillBulkResults(TelegramEndpointBulkTarget[] frozen, TelegramEndpointBulkResult[] results,
        int start, string code)
    {
        for (var index = start; index < frozen.Length; index++)
            results[index] = new(frozen[index].BotId, frozen[index].Identity, code, null);
    }

    /// <summary>Call-local exact-state receipt, mutated only by the locked individual admission and cleared in finally.</summary>
    /// <param name="BotId">Canonical frozen internal id whose receipt may be captured.</param>
    /// <param name="Identity">Frozen positive BotFather identity, never a replacement identity.</param>
    private sealed class BulkAdmissionCapture(string BotId, long Identity)
    {
        /// <summary>Frozen internal id used to reject unrelated captures in an inherited execution context.</summary>
        internal string BotId { get; } = BotId;
        /// <summary>Frozen BotFather identity used to reject replacement-state metadata.</summary>
        internal long Identity { get; } = Identity;
        /// <summary>Whether this originating call still owns the context; false after any exit.</summary>
        internal bool Active { get; set; } = true;
        /// <summary>Whether a committed accepted/unchanged state was observed under the bot lock.</summary>
        internal bool Captured { get; set; }
        /// <summary>Exact observed operation id, which may be null for an unchanged never-migrated bot.</summary>
        internal string OperationId { get; set; }
    }
}
