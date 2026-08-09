using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

/// <summary>
/// Persists XUI v3 volume-reminder observations, renewal cycles, and exclusive Telegram delivery claims.
/// </summary>
/// <remarks>
/// Every database operation uses an independent <see cref="UserDbContext"/> created by
/// <see cref="UserDbContextFactory"/>. A process-local gate prevents a periodic reconciliation from overwriting a
/// simultaneous owned, tenant, or admin renewal hook; database predicates remain the final multi-instance send guard.
/// No wallet, payment, ledger, account, or credentials row is modified by this store.
/// </remarks>
public sealed class XuiV3VolumeReminderStateStore
{
    /// <summary>
    /// Duration of a pre-delivery claim before crash recovery suppresses its ambiguous threshold.
    /// </summary>
    private static readonly TimeSpan DeliveryLease = TimeSpan.FromMinutes(15);
    private readonly UserDbContextFactory _contextFactory;
    private readonly ILogger<XuiV3VolumeReminderStateStore> _logger;
    /// <summary>Serializes batch reconciliation, send claims, outcomes, and renewal hooks within this process.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates the durable volume-reminder state store.
    /// </summary>
    /// <param name="contextFactory">Factory for independent contexts targeting the migrated <c>users.db</c>.</param>
    /// <param name="logger">Structured logger for non-customer-facing persistence failures.</param>
    public XuiV3VolumeReminderStateStore(
        UserDbContextFactory contextFactory,
        ILogger<XuiV3VolumeReminderStateStore> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reconciles one complete panel-list scan and returns only thresholds that may currently be delivered.
    /// </summary>
    /// <param name="panelKey">
    /// Credential-free SHA-256 panel identity produced by <see cref="XuiV3ClientUsageResolver.BuildPanelKey"/>.
    /// </param>
    /// <param name="observations">
    /// Existing finite-quota XUI clients from one successful complete list response. The collection may be empty and
    /// must contain at most one observation per numeric client id.
    /// </param>
    /// <param name="nowUtc">Current UTC scan time used for material observation timestamps and stale-claim handling.</param>
    /// <param name="cancellationToken">Host shutdown token for the users.db read and write transaction.</param>
    /// <returns>
    /// Detached candidates whose highest reached threshold exceeds the durable handled threshold. The collection may
    /// be empty; callers must still verify bot/user eligibility and successfully claim each candidate before sending.
    /// </returns>
    /// <remarks>
    /// A cycle advances only for a recreated client, a counter drop, a quota increase, a newer bot renewal marker, or
    /// an explicit renewal hook. A changed panel <c>updatedAt</c> without one of those business signals never resets
    /// thresholds. Stale processing claims are marked ambiguous and suppressed because duplicate prevention is the
    /// selected failure policy.
    /// </remarks>
    /// <example>
    /// <code>
    /// var candidates = await store.ReconcileAsync(panelKey, observations, DateTime.UtcNow, cancellationToken);
    /// </code>
    /// </example>
    public async Task<IReadOnlyList<XuiV3VolumeReminderCandidate>> ReconcileAsync(
        string panelKey,
        IReadOnlyCollection<XuiV3VolumeReminderObservation> observations,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(panelKey))
            throw new ArgumentException("Volume reminder panel key is required.", nameof(panelKey));

        observations ??= Array.Empty<XuiV3VolumeReminderObservation>();
        var utcNow = NormalizeUtc(nowUtc);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = _contextFactory.CreateDbContext();
            var states = await context.XuiV3VolumeReminderStates
                .Where(x => x.PanelKey == panelKey)
                .ToDictionaryAsync(x => x.ClientId, cancellationToken);
            var candidates = new List<XuiV3VolumeReminderCandidate>();

            foreach (var observation in observations
                         .Where(x => x != null && x.ClientId > 0)
                         .GroupBy(x => x.ClientId)
                         .Select(group => group.Last()))
            {
                if (!states.TryGetValue(observation.ClientId, out var state))
                {
                    state = CreateState(panelKey, observation, utcNow);
                    states[state.ClientId] = state;
                    context.XuiV3VolumeReminderStates.Add(state);
                }
                else
                {
                    ApplyObservation(state, observation, utcNow);
                }

                if (observation.CanNotify &&
                    observation.HighestReachedThreshold > state.HighestHandledThreshold &&
                    state.ClaimedThreshold == null)
                {
                    candidates.Add(new XuiV3VolumeReminderCandidate
                    {
                        PanelKey = panelKey,
                        ClientId = state.ClientId,
                        CycleNumber = state.CycleNumber,
                        Threshold = observation.HighestReachedThreshold,
                        Email = observation.Email,
                        BotId = observation.BotId,
                        TelegramUserId = observation.TelegramUserId,
                        UsedBytes = observation.UsedBytes,
                        TotalBytes = observation.TotalBytes
                    });
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            return candidates;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads bot-scoped start evidence for candidate recipients in one users.db query.
    /// </summary>
    /// <param name="candidates">
    /// Detached reminder candidates containing runtime bot ids and numeric Telegram owner ids. Invalid ids are ignored.
    /// </param>
    /// <param name="cancellationToken">Host shutdown token for the users.db query.</param>
    /// <returns>
    /// Keys in the form <c>botId:telegramUserId</c> for candidates that have a persistent
    /// <see cref="BotUserState"/> row in that exact bot. The set may be empty.
    /// </returns>
    /// <remarks>
    /// The bot id is part of the eligibility key so starting one owned or tenant bot never authorizes another bot to
    /// message the same Telegram user.
    /// </remarks>
    public async Task<IReadOnlySet<string>> GetStartedBotUserKeysAsync(
        IReadOnlyCollection<XuiV3VolumeReminderCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates == null || candidates.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var userIds = candidates
            .Where(x => x.TelegramUserId > 0)
            .Select(x => x.TelegramUserId)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var context = _contextFactory.CreateDbContext();
        var rows = await context.BotUserStates
            .AsNoTracking()
            .Where(x => userIds.Contains(x.TelegramUserId))
            .Select(x => new { x.BotId, x.TelegramUserId })
            .ToListAsync(cancellationToken);
        return rows
            .Select(x => BuildBotUserKey(x.BotId, x.TelegramUserId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Atomically claims one threshold before any Telegram API call.
    /// </summary>
    /// <param name="candidate">Current-cycle candidate returned by <see cref="ReconcileAsync"/>.</param>
    /// <param name="nowUtc">Current UTC claim time.</param>
    /// <param name="cancellationToken">Host shutdown token for the atomic users.db update.</param>
    /// <returns>
    /// <c>true</c> only when this caller acquired the exact client/cycle/threshold claim; <c>false</c> when another
    /// worker handled it, the cycle changed, or a claim already exists.
    /// </returns>
    public async Task<bool> TryClaimAsync(
        XuiV3VolumeReminderCandidate candidate,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var utcNow = NormalizeUtc(nowUtc);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = _contextFactory.CreateDbContext();
            var affected = await context.XuiV3VolumeReminderStates
                .Where(x => x.PanelKey == candidate.PanelKey &&
                            x.ClientId == candidate.ClientId &&
                            x.CycleNumber == candidate.CycleNumber &&
                            x.HighestHandledThreshold < candidate.Threshold &&
                            x.ClaimedThreshold == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ClaimedThreshold, candidate.Threshold)
                    .SetProperty(x => x.DeliveryStatus, XuiV3VolumeReminderDeliveryStatuses.Processing)
                    .SetProperty(x => x.LeaseUntilUtc, utcNow.Add(DeliveryLease))
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.LastError, (string)null)
                    .SetProperty(x => x.UpdatedAtUtc, utcNow),
                    cancellationToken);
            return affected == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks a claimed threshold delivered after Telegram returns a concrete message id.
    /// </summary>
    /// <param name="candidate">Exact claimed client, cycle, and threshold.</param>
    /// <param name="telegramMessageId">Positive Telegram message id returned by the originating bot.</param>
    /// <param name="nowUtc">UTC delivery completion time.</param>
    /// <param name="cancellationToken">Token for the users.db update.</param>
    /// <returns>A task that completes after duplicate-prevention state is durable.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the exact claim no longer exists; callers must then record an ambiguous delivered state rather than
    /// resend the accepted Telegram message.
    /// </exception>
    public async Task MarkSentAsync(
        XuiV3VolumeReminderCandidate candidate,
        int telegramMessageId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var affected = await CompleteClaimAsync(
            candidate,
            XuiV3VolumeReminderDeliveryStatuses.Sent,
            Math.Max(1, telegramMessageId),
            error: null,
            advanceThreshold: true,
            NormalizeUtc(nowUtc),
            cancellationToken);
        if (affected != 1)
            throw new InvalidOperationException("The accepted XUI volume reminder claim could not be marked sent.");
    }

    /// <summary>
    /// Marks a claimed threshold terminal for the current cycle after Telegram reports that the bot is blocked.
    /// </summary>
    /// <param name="candidate">Exact claimed client, cycle, and threshold.</param>
    /// <param name="error">Sanitized Telegram 403 diagnostic text; bot tokens must never be included.</param>
    /// <param name="nowUtc">UTC failure time.</param>
    /// <param name="cancellationToken">Token for the users.db update.</param>
    /// <returns>A task that completes after this threshold is suppressed for the current cycle.</returns>
    public Task MarkTelegramBlockedAsync(
        XuiV3VolumeReminderCandidate candidate,
        string error,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => CompleteClaimAsync(
            candidate,
            XuiV3VolumeReminderDeliveryStatuses.TelegramBlocked,
            telegramMessageId: null,
            error,
            advanceThreshold: true,
            NormalizeUtc(nowUtc),
            cancellationToken);

    /// <summary>
    /// Releases a claim after a definite retryable Telegram failure.
    /// </summary>
    /// <param name="candidate">Exact claimed client, cycle, and threshold.</param>
    /// <param name="error">Sanitized delivery failure retained up to 2000 characters.</param>
    /// <param name="nowUtc">UTC failure time.</param>
    /// <param name="cancellationToken">Token for the users.db update.</param>
    /// <returns>A task that completes after the threshold becomes eligible for a later interval retry.</returns>
    public Task MarkFailedAsync(
        XuiV3VolumeReminderCandidate candidate,
        string error,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => CompleteClaimAsync(
            candidate,
            XuiV3VolumeReminderDeliveryStatuses.Failed,
            telegramMessageId: null,
            error,
            advanceThreshold: false,
            NormalizeUtc(nowUtc),
            cancellationToken);

    /// <summary>
    /// Suppresses an accepted or otherwise ambiguous claim so it cannot produce a duplicate message.
    /// </summary>
    /// <param name="candidate">Exact claimed client, cycle, and threshold.</param>
    /// <param name="telegramMessageId">Known Telegram message id, or null when the process outcome is uncertain.</param>
    /// <param name="error">Sanitized persistence or crash-recovery diagnostic.</param>
    /// <param name="nowUtc">UTC reconciliation time.</param>
    /// <param name="cancellationToken">Best-effort token for the users.db update.</param>
    /// <returns>A task that completes after the threshold is marked handled and non-retryable.</returns>
    public Task MarkAmbiguousAsync(
        XuiV3VolumeReminderCandidate candidate,
        int? telegramMessageId,
        string error,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => CompleteClaimAsync(
            candidate,
            XuiV3VolumeReminderDeliveryStatuses.Ambiguous,
            telegramMessageId,
            error,
            advanceThreshold: true,
            NormalizeUtc(nowUtc),
            cancellationToken);

    /// <summary>
    /// Best-effort hook that starts a fresh volume cycle immediately after a confirmed panel renewal.
    /// </summary>
    /// <param name="serverInfo">Configured panel descriptor used only to derive a credential-free panel key.</param>
    /// <param name="clientBeforeRenewal">Authoritative client snapshot read before the successful panel update.</param>
    /// <param name="renewal">Shared renewal calculation whose payload was accepted by the panel.</param>
    /// <param name="trafficResetApplied">
    /// Whether a required panel traffic reset was confirmed. When false, pre-renewal usage remains the conservative
    /// observation and may correctly trigger a final warning if the panel still reports exhausted traffic.
    /// </param>
    /// <param name="cancellationToken">Current owned, admin, or tenant operation cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the cycle reset was persisted or already recorded idempotently; <c>false</c> when reminder-state
    /// persistence failed. A false result must never roll back the successful panel renewal or financial settlement.
    /// </returns>
    /// <remarks>
    /// The bot metadata renewal timestamp makes repeated settlement callbacks idempotent. Periodic reconciliation can
    /// still recover through quota/counter changes if this best-effort users.db write fails.
    /// </remarks>
    /// <example>
    /// <code>
    /// await store.TryBeginNewCycleAfterRenewalAsync(
    ///     serverInfo, client, renewal, trafficResetApplied, cancellationToken);
    /// </code>
    /// </example>
    public async Task<bool> TryBeginNewCycleAfterRenewalAsync(
        ServerInfo serverInfo,
        XuiV3Client clientBeforeRenewal,
        XuiV3RenewalCalculation renewal,
        bool trafficResetApplied,
        CancellationToken cancellationToken)
    {
        if (serverInfo == null || clientBeforeRenewal == null || renewal?.Payload == null || clientBeforeRenewal.Id <= 0)
            return false;

        try
        {
            var panelKey = XuiV3ClientUsageResolver.BuildPanelKey(serverInfo);
            var before = XuiV3ClientUsageResolver.Resolve(clientBeforeRenewal);
            var metadata = TryReadMetadata(renewal.Payload.Comment);
            var renewalMarker = NormalizeUtc(metadata?.LastRenewedAtUtc);
            var nowUtc = DateTime.UtcNow;
            var usedAfter = renewal.ShouldResetTraffic && trafficResetApplied ? 0 : renewal.UsedBytes;
            var botId = string.IsNullOrWhiteSpace(metadata?.CreatedByBotId)
                ? before.CreatedByBotId
                : metadata.CreatedByBotId;
            var telegramUserId = renewal.Payload.TgId > 0
                ? renewal.Payload.TgId
                : before.OwnerTelegramUserId;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var context = _contextFactory.CreateDbContext();
                var state = await context.XuiV3VolumeReminderStates.FirstOrDefaultAsync(
                    x => x.PanelKey == panelKey && x.ClientId == clientBeforeRenewal.Id,
                    cancellationToken);
                if (state == null)
                {
                    state = new XuiV3VolumeReminderState
                    {
                        PanelKey = panelKey,
                        ClientId = clientBeforeRenewal.Id,
                        CycleNumber = 1,
                        CreatedAtUtc = nowUtc
                    };
                    context.XuiV3VolumeReminderStates.Add(state);
                }
                else if (!renewalMarker.HasValue ||
                         !state.LastRenewedAtUtc.HasValue ||
                         renewalMarker.Value > state.LastRenewedAtUtc.Value)
                {
                    ResetCycle(state);
                }

                state.ClientCreatedAt = before.ClientCreatedAt;
                state.Email = renewal.Payload.Email ?? before.Email;
                state.BotId = string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.DefaultBotId : botId;
                state.TelegramUserId = telegramUserId;
                state.PanelUpdatedAt = before.PanelUpdatedAt;
                state.TotalBytes = Math.Max(0, renewal.TotalBytesAfterRenew);
                state.UsedBytes = Math.Max(0, usedAfter);
                state.LastRenewedAtUtc = renewalMarker ?? nowUtc;
                state.LastObservedAtUtc = nowUtc;
                state.UpdatedAtUtc = nowUtc;
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "XUI volume reminder cycle could not be advanced after confirmed renewal. clientId={ClientId}",
                clientBeforeRenewal.Id);
            return false;
        }
    }

    /// <summary>
    /// Creates the first durable state row for a newly observed finite-quota client.
    /// </summary>
    /// <param name="panelKey">Credential-free panel hash.</param>
    /// <param name="observation">Current client observation.</param>
    /// <param name="nowUtc">UTC row creation time.</param>
    /// <returns>A tracked state initialized to cycle one with no handled threshold.</returns>
    private static XuiV3VolumeReminderState CreateState(
        string panelKey,
        XuiV3VolumeReminderObservation observation,
        DateTime nowUtc)
    {
        return new XuiV3VolumeReminderState
        {
            PanelKey = panelKey,
            ClientId = observation.ClientId,
            ClientCreatedAt = observation.ClientCreatedAt,
            Email = observation.Email,
            BotId = observation.BotId,
            TelegramUserId = observation.TelegramUserId,
            CycleNumber = 1,
            PanelUpdatedAt = observation.PanelUpdatedAt,
            TotalBytes = observation.TotalBytes,
            UsedBytes = observation.UsedBytes,
            LastRenewedAtUtc = NormalizeUtc(observation.LastRenewedAtUtc),
            DeliveryStatus = XuiV3VolumeReminderDeliveryStatuses.Idle,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            LastObservedAtUtc = nowUtc
        };
    }

    /// <summary>
    /// Applies one current observation and advances the cycle only for renewal-relevant changes.
    /// </summary>
    /// <param name="state">Tracked durable state for the same panel and numeric client id.</param>
    /// <param name="observation">Current complete-list observation.</param>
    /// <param name="nowUtc">UTC reconciliation time.</param>
    private static void ApplyObservation(
        XuiV3VolumeReminderState state,
        XuiV3VolumeReminderObservation observation,
        DateTime nowUtc)
    {
        var newerRevision = observation.PanelUpdatedAt <= 0 ||
                            state.PanelUpdatedAt <= 0 ||
                            observation.PanelUpdatedAt > state.PanelUpdatedAt;
        var recreated = observation.ClientCreatedAt > 0 &&
                        state.ClientCreatedAt > 0 &&
                        observation.ClientCreatedAt != state.ClientCreatedAt;
        var countersReset = observation.UsedBytes < state.UsedBytes && newerRevision;
        var quotaIncreased = observation.TotalBytes > state.TotalBytes && newerRevision;
        var observedRenewal = observation.LastRenewedAtUtc.HasValue &&
                              (!state.LastRenewedAtUtc.HasValue ||
                               NormalizeUtc(observation.LastRenewedAtUtc).Value > state.LastRenewedAtUtc.Value);
        var newCycle = recreated || countersReset || quotaIncreased || observedRenewal;
        var staleClaimSuppressed = false;

        if (newCycle)
        {
            ResetCycle(state);
        }
        else if (state.DeliveryStatus == XuiV3VolumeReminderDeliveryStatuses.Processing &&
                 state.ClaimedThreshold.HasValue &&
                 state.LeaseUntilUtc.HasValue &&
                 state.LeaseUntilUtc.Value <= nowUtc)
        {
            // A hard crash can happen before or after Telegram accepts a message. The selected policy suppresses the
            // stale claim instead of retrying an outcome that Telegram cannot idempotently identify.
            state.HighestHandledThreshold = Math.Max(state.HighestHandledThreshold, state.ClaimedThreshold.Value);
            state.DeliveryStatus = XuiV3VolumeReminderDeliveryStatuses.Ambiguous;
            state.LastError = "Stale delivery claim was suppressed to prevent a duplicate Telegram reminder.";
            state.ClaimedThreshold = null;
            state.LeaseUntilUtc = null;
            staleClaimSuppressed = true;
        }

        var materiallyChanged = newCycle || staleClaimSuppressed ||
                                state.ClientCreatedAt != observation.ClientCreatedAt ||
                                state.Email != observation.Email ||
                                state.BotId != observation.BotId ||
                                state.TelegramUserId != observation.TelegramUserId ||
                                state.PanelUpdatedAt != observation.PanelUpdatedAt ||
                                state.TotalBytes != observation.TotalBytes ||
                                state.UsedBytes != observation.UsedBytes ||
                                NormalizeUtc(state.LastRenewedAtUtc) != NormalizeUtc(observation.LastRenewedAtUtc);

        state.ClientCreatedAt = observation.ClientCreatedAt;
        state.Email = observation.Email;
        state.BotId = observation.BotId;
        state.TelegramUserId = observation.TelegramUserId;
        state.PanelUpdatedAt = observation.PanelUpdatedAt;
        state.TotalBytes = observation.TotalBytes;
        state.UsedBytes = observation.UsedBytes;
        state.LastRenewedAtUtc = NormalizeUtc(observation.LastRenewedAtUtc);
        if (materiallyChanged || nowUtc - state.LastObservedAtUtc >= TimeSpan.FromHours(24))
        {
            state.LastObservedAtUtc = nowUtc;
            state.UpdatedAtUtc = nowUtc;
        }
    }

    /// <summary>
    /// Advances a tracked row to the next volume cycle and clears every prior-cycle delivery claim and threshold.
    /// </summary>
    /// <param name="state">Tracked client state that received a confirmed renewal-relevant change.</param>
    private static void ResetCycle(XuiV3VolumeReminderState state)
    {
        state.CycleNumber = Math.Max(1, state.CycleNumber + 1);
        state.HighestHandledThreshold = 0;
        state.ClaimedThreshold = null;
        state.DeliveryStatus = XuiV3VolumeReminderDeliveryStatuses.Idle;
        state.LeaseUntilUtc = null;
        state.AttemptCount = 0;
        state.TelegramMessageId = null;
        state.LastError = null;
        state.LastDeliveredAtUtc = null;
    }

    /// <summary>
    /// Completes or releases one exact claimed threshold through a guarded users.db update.
    /// </summary>
    /// <param name="candidate">Claimed client/cycle/threshold identity.</param>
    /// <param name="status">Terminal or retryable delivery status.</param>
    /// <param name="telegramMessageId">Known Telegram message id, or null when none was accepted.</param>
    /// <param name="error">Sanitized diagnostic text.</param>
    /// <param name="advanceThreshold">Whether this outcome must suppress the threshold for the current cycle.</param>
    /// <param name="nowUtc">UTC completion time.</param>
    /// <param name="cancellationToken">Token for the guarded users.db update.</param>
    /// <returns>Number of rows updated; one means the exact claim was found.</returns>
    private async Task<int> CompleteClaimAsync(
        XuiV3VolumeReminderCandidate candidate,
        string status,
        int? telegramMessageId,
        string error,
        bool advanceThreshold,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var safeError = SanitizeError(error);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = _contextFactory.CreateDbContext();
            var state = await context.XuiV3VolumeReminderStates.FirstOrDefaultAsync(
                x => x.PanelKey == candidate.PanelKey &&
                     x.ClientId == candidate.ClientId &&
                     x.CycleNumber == candidate.CycleNumber &&
                     x.ClaimedThreshold == candidate.Threshold,
                cancellationToken);
            if (state == null)
                return 0;

            if (advanceThreshold)
                state.HighestHandledThreshold = Math.Max(state.HighestHandledThreshold, candidate.Threshold);
            state.ClaimedThreshold = null;
            state.DeliveryStatus = status;
            state.LeaseUntilUtc = null;
            state.TelegramMessageId = telegramMessageId;
            state.LastError = safeError;
            state.UpdatedAtUtc = nowUtc;
            if (status == XuiV3VolumeReminderDeliveryStatuses.Sent)
                state.LastDeliveredAtUtc = nowUtc;
            await context.SaveChangesAsync(cancellationToken);
            return 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Parses the renewal timestamp and origin metadata from a replacement client payload.
    /// </summary>
    /// <param name="comment">Bot JSON metadata stored in the XUI comment field.</param>
    /// <returns>Parsed metadata, or null for an absent/malformed historical comment.</returns>
    private static XuiV3ClientMetadata TryReadMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the ordinal composite key used to prove that a user started one exact bot.
    /// </summary>
    /// <param name="botId">Owned or tenant runtime bot id.</param>
    /// <param name="telegramUserId">Numeric Telegram owner id.</param>
    /// <returns>Stable bot-scoped recipient key; callers compare the bot-id portion case-insensitively.</returns>
    public static string BuildBotUserKey(string botId, long telegramUserId)
        => $"{(string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.DefaultBotId : botId)}:{telegramUserId}";

    /// <summary>
    /// Normalizes a nullable timestamp to UTC without changing its clock value.
    /// </summary>
    /// <param name="value">Timestamp read from bot metadata or null.</param>
    /// <returns>UTC-kind timestamp, or null.</returns>
    private static DateTime? NormalizeUtc(DateTime? value)
        => value.HasValue ? NormalizeUtc(value.Value) : null;

    /// <summary>
    /// Normalizes a timestamp to UTC without a local-time conversion for unspecified persisted values.
    /// </summary>
    /// <param name="value">Timestamp used by state transitions.</param>
    /// <returns>A UTC-kind timestamp.</returns>
    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// Truncates operational errors before users.db persistence and supplies a safe fallback.
    /// </summary>
    /// <param name="error">Raw exception or Telegram error text; it must not include bot or panel secrets.</param>
    /// <returns>Null for an empty error, otherwise at most 2000 characters.</returns>
    private static string SanitizeError(string error)
        => string.IsNullOrWhiteSpace(error) ? null : error.Length <= 2000 ? error : error[..2000];
}

/// <summary>
/// Current normalized panel observation used to reconcile one durable client reminder state.
/// </summary>
public sealed class XuiV3VolumeReminderObservation
{
    /// <summary>Numeric XUI client id.</summary>
    public int ClientId { get; init; }
    /// <summary>Panel creation timestamp in Unix milliseconds.</summary>
    public long ClientCreatedAt { get; init; }
    /// <summary>Current email shown only to the resolved account owner.</summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>Originating owned or tenant runtime bot id.</summary>
    public string BotId { get; init; } = BotContextAccessor.DefaultBotId;
    /// <summary>Numeric Telegram id of the account owner.</summary>
    public long TelegramUserId { get; init; }
    /// <summary>Panel <c>updatedAt</c> Unix-millisecond revision.</summary>
    public long PanelUpdatedAt { get; init; }
    /// <summary>Finite traffic quota in bytes.</summary>
    public long TotalBytes { get; init; }
    /// <summary>Upload plus download consumption in bytes.</summary>
    public long UsedBytes { get; init; }
    /// <summary>Latest bot-recorded renewal timestamp, when present.</summary>
    public DateTime? LastRenewedAtUtc { get; init; }
    /// <summary>Highest of 80, 90, or 99 reached by raw bytes, or zero.</summary>
    public int HighestReachedThreshold { get; init; }
    /// <summary>Whether current time/enable/service rules allow this threshold to be sent.</summary>
    public bool CanNotify { get; init; }
}

/// <summary>
/// Detached current-cycle volume threshold eligible for bot/user validation and an atomic send claim.
/// </summary>
public sealed class XuiV3VolumeReminderCandidate
{
    /// <summary>Credential-free panel hash.</summary>
    public string PanelKey { get; init; }
    /// <summary>Numeric XUI panel client id.</summary>
    public int ClientId { get; init; }
    /// <summary>Durable local cycle number that must still match at claim time.</summary>
    public long CycleNumber { get; init; }
    /// <summary>Highest reached threshold selected for this message.</summary>
    public int Threshold { get; init; }
    /// <summary>Current account email used in the message and renewal button label.</summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>Originating owned or tenant bot id.</summary>
    public string BotId { get; init; } = BotContextAccessor.DefaultBotId;
    /// <summary>Numeric Telegram recipient id.</summary>
    public long TelegramUserId { get; init; }
    /// <summary>Observed upload plus download bytes.</summary>
    public long UsedBytes { get; init; }
    /// <summary>Observed finite quota bytes.</summary>
    public long TotalBytes { get; init; }
}
