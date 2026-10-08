using Adminbot.Domain;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Tokenless shared health, already-local bot identity hysteresis and conservative outage recovery.</summary>
public sealed partial class TelegramEndpointCoordinator
{
    /// <summary>Checks tokenless Local reachability, already-local bot identity and manual/automatic failback recovery notices.</summary>
    /// <param name="cancellationToken">Hosted worker shutdown token for short, bounded read budgets.</param>
    /// <returns>A task completing after the inventory scan; Cloud bots are never Local-probed by health.</returns>
    /// <remarks>Defaults are 15-second cadence, 3-second budgets, three local transport failures and two recovery successes.
    /// CloudRecovered bots receive a deduplicated tokenless server-recovered alert even when failback remains manual.</remarks>
    public async Task RunHealthCycleAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_options.Enabled) return;
        var currentIds = _registry.Bots.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in _sharedRecoveryAlertMemo.Keys.Where(x => !currentIds.Contains(x)))
            _sharedRecoveryAlertMemo.TryRemove(obsolete, out _);
        var root = await ProbeSharedAsync(cancellationToken);
        foreach (var state in await GetInventoryAsync(cancellationToken))
        {
            var bot = FindBot(state.BotId);
            if (bot == null || !bot.Enabled || TelegramBotTokenIdentity.ExtractBotId(bot.Token) != state.TelegramBotId ||
                IsReservedIdentity(state.TelegramBotId)) continue;
            if (state.EffectiveEndpoint == TelegramEndpointType.Local)
                await RefreshBotHealthAsync(state.BotId, root, cancellationToken);
            else if (state.DesiredEndpoint == TelegramEndpointType.Local &&
                state.MigrationState == TelegramEndpointMigrationState.CloudRecovered)
            {
                await ObserveSharedLocalRecoveryAsync(state.BotId, root.Success, cancellationToken);
                if (_options.AutomaticFailback && HasLocalFileMapping)
                    await ConsiderAutomaticFailbackAsync(state.BotId, root.Success, cancellationToken);
            }
        }
        await GetPendingAlertCountAsync(cancellationToken);
    }

    /// <summary>Refreshes shared reachability and an authorized current-endpoint identity without running a migration inline.</summary>
    /// <param name="botId">Exact internal selected bot id; current identity is rechecked under its lock.</param>
    /// <param name="cancellationToken">Foreground token used only for safe bounded health reads and persistence.</param>
    /// <returns>A task completing after safe metadata is committed; it never drains the caller's handler.</returns>
    /// <remarks>Active eligible Cloud bots use Cloud getMe; already-local bots use Local getMe. Pending, cooldown and
    /// uncertain phases never create a destination session or probe Cloud before acknowledged cleanup.</remarks>
    public async Task RefreshHealthAsync(string botId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_options.Enabled) return;
        var root = await ProbeSharedAsync(cancellationToken);
        await RefreshBotHealthAsync(botId, root, cancellationToken);
        await GetPendingAlertCountAsync(cancellationToken);
    }

    /// <summary>Updates one already-local bot's outage counters and durable incident intent under its bot lock.</summary>
    /// <param name="botId">Exact current internal bot id.</param>
    /// <param name="root">Tokenless shared root result from this health cycle.</param>
    /// <param name="token">Caller-owned cancellation for safe probes and persistence.</param>
    /// <returns>A task completing after metadata or a deduplicated outage/recovery transition.</returns>
    private async Task RefreshBotHealthAsync(string botId, TelegramEndpointProbeResult root, CancellationToken token)
    {
        var bot = FindBot(botId);
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        if (bot == null || !bot.Enabled || identity is not > 0) return;
        var mutex = GetLock(bot.Id);
        if (!await mutex.WaitAsync(0, token)) return;
        try
        {
            var state = await _store.GetOrCreateAsync(bot.Id, identity.Value, token);
            if (state.EffectiveEndpoint == TelegramEndpointType.Cloud &&
                (state.MigrationState is TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered) &&
                (!state.CloudReuseEligibleAtUtc.HasValue || state.CloudReuseEligibleAtUtc <= UtcNow) && !IsUncertain(state))
            {
                var started = _time.GetTimestamp();
                var cloud = await ProbeIdentityAsync(state, TelegramEndpointType.Cloud, token);
                state.LastHealthCheckAtUtc = UtcNow;
                state.LastFailureCategory = TelegramEndpointHealthPolicy.Code(cloud.Failure);
                if (cloud.Success)
                {
                    state.LastSuccessfulHealthAtUtc = UtcNow;
                    state.ConsecutiveSuccesses = Math.Min(_options.RecoveryThreshold, state.ConsecutiveSuccesses + 1);
                    state.ConsecutiveFailures = 0;
                }
                else
                {
                    state.ConsecutiveSuccesses = 0;
                    state.LastFailureAtUtc = UtcNow;
                    state.ConsecutiveFailures = Math.Min(_options.FailureThreshold, state.ConsecutiveFailures + 1);
                }
                await SaveAsync(state, null, null, token);
                Record(state, "telegram_endpoint_health", cloud.Success ? "success" : "failure",
                    _time.GetElapsedTime(started).TotalMilliseconds);
                return;
            }
            if (state.EffectiveEndpoint != TelegramEndpointType.Local || IsUncertain(state) ||
                state.LogoutEndpoint == TelegramEndpointType.Local && state.LogoutAcknowledgedAtUtc.HasValue ||
                state.MigrationState is not (TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded or
                    TelegramEndpointMigrationState.LocalUnavailable or TelegramEndpointMigrationState.FallbackPending or
                    TelegramEndpointMigrationState.ManualInterventionRequired)) return;
            // A failed destination activation is not a previously healthy Local receiver to resume.
            if (state.MigrationState == TelegramEndpointMigrationState.ManualInterventionRequired && state.LogoutAttemptedAtUtc.HasValue)
                return;
            var timestamp = _time.GetTimestamp();
            var probe = root.Success ? await ProbeIdentityAsync(state, TelegramEndpointType.Local, token) : root;
            state.LastHealthCheckAtUtc = UtcNow;
            if (probe.Success)
            {
                state.LastSuccessfulHealthAtUtc = UtcNow;
                state.ConsecutiveSuccesses = Math.Min(_options.RecoveryThreshold, state.ConsecutiveSuccesses + 1);
                state.ConsecutiveFailures = 0;
                state.LastFailureCategory = null;
                if (state.ConsecutiveSuccesses >= _options.RecoveryThreshold &&
                    state.MigrationState is TelegramEndpointMigrationState.LocalDegraded or TelegramEndpointMigrationState.LocalUnavailable or
                        TelegramEndpointMigrationState.FallbackPending or TelegramEndpointMigrationState.ManualInterventionRequired)
                {
                    // Callback health must not self-drain/start. Queue a safe same-Local recovery for the worker.
                    state.NextAttemptAtUtc = UtcNow;
                    if (!await SaveAsync(state, null, null, token)) return;
                    SignalWork();
                    Record(state, "telegram_endpoint_health", "success", _time.GetElapsedTime(timestamp).TotalMilliseconds);
                    return;
                }
                await SaveAsync(state, null, null, token);
            }
            else
            {
                state.ConsecutiveSuccesses = 0;
                state.LastFailureAtUtc = UtcNow;
                state.LastFailureCategory = TelegramEndpointHealthPolicy.Code(probe.Failure);
                if (probe.Failure is TelegramEndpointFailure.TokenRejected or TelegramEndpointFailure.IdentityMismatch)
                {
                    _gate.Fence(state.BotId, state.TelegramBotId);
                    state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
                    state.NextAttemptAtUtc = null;
                    await SaveAsync(state, "manual_intervention", "manual_intervention", token);
                }
                else if (TelegramEndpointHealthPolicy.IsServerOutage(probe.Failure))
                {
                    state.ConsecutiveFailures = Math.Min(_options.FailureThreshold, state.ConsecutiveFailures + 1);
                    if (state.ConsecutiveFailures >= _options.FailureThreshold)
                    {
                        _gate.Fence(state.BotId, state.TelegramBotId);
                        var first = state.OutageId == null || IsActive(state);
                        if (first)
                        {
                            state.OutageId = Guid.NewGuid().ToString("N");
                            state.LastOutageNotifiedAtUtc = UtcNow;
                            state.MigrationState = TelegramEndpointMigrationState.LocalUnavailable;
                            state.NextAttemptAtUtc = UtcNow;
                            var aliased = HasIdentityAlias(state.BotId, state.TelegramBotId);
                            if (state.AutoFailoverEnabled && !aliased)
                                BeginIntent(state, TelegramEndpointType.Cloud, null, "automatic_outage", preserveDesired: true);
                            state.LastFailureCategory = aliased ? "identity_alias_conflict" : TelegramEndpointHealthPolicy.Code(probe.Failure);
                        }
                        if (await SaveAsync(state, first ? "local_outage" : null, first ? "local_outage" : null, token))
                        {
                            if (first) Record(state, "telegram_endpoint_outage", "failure");
                            SignalWork();
                        }
                    }
                    else
                    {
                        if (state.MigrationState == TelegramEndpointMigrationState.Local)
                            state.MigrationState = TelegramEndpointMigrationState.LocalDegraded;
                        await SaveAsync(state, null, null, token);
                    }
                }
                else
                {
                    // Telegram 429/5xx are upstream degradation, never evidence that the local process failed.
                    state.ConsecutiveFailures = 0;
                    if (state.MigrationState == TelegramEndpointMigrationState.Local)
                        state.MigrationState = TelegramEndpointMigrationState.LocalDegraded;
                    await SaveAsync(state, null, null, token);
                }
            }
            Record(state, "telegram_endpoint_health", probe.Success ? "success" : "failure",
                _time.GetElapsedTime(timestamp).TotalMilliseconds);
        }
        finally { mutex.Release(); }
    }

    /// <summary>Persists one tokenless shared-server recovery alert without opening a Local bot session or changing failback policy.</summary>
    /// <param name="botId">Current exact CloudRecovered internal bot id retaining desired Local.</param>
    /// <param name="reachable">Whether this cycle's tokenless root check succeeded.</param>
    /// <param name="token">Safe metadata/outbox persistence cancellation.</param>
    /// <returns>A task completing after recovery hysteresis or one durable alert for the retained outage.</returns>
    /// <remarks>The memory memo is set only after CAS success; durable incident-key uniqueness deduplicates restart.
    /// This never calls Local getMe, starts a receiver, changes endpoint desire/effect or enables automatic failback.</remarks>
    private async Task ObserveSharedLocalRecoveryAsync(string botId, bool reachable, CancellationToken token)
    {
        if (!reachable || Volatile.Read(ref _sharedRootSuccesses) < _options.RecoveryThreshold) return;
        var mutex = GetLock(botId);
        if (!await mutex.WaitAsync(0, token)) return;
        try
        {
            var state = await GetStatusAsync(botId, token);
            if (state.MigrationState != TelegramEndpointMigrationState.CloudRecovered ||
                state.DesiredEndpoint != TelegramEndpointType.Local || state.OutageId == null ||
                _sharedRecoveryAlertMemo.TryGetValue(botId, out var incident) && incident == state.OutageId) return;
            if (await SaveAsync(state, "local_recovered", "local_recovered", token))
                _sharedRecoveryAlertMemo[botId] = state.OutageId;
        }
        finally { mutex.Release(); }
    }

    /// <summary>Queues opt-in failback after sustained tokenless reachability, without prelogout Local getMe.</summary>
    /// <param name="botId">Exact recovered Cloud bot whose desired endpoint remains Local.</param>
    /// <param name="reachable">Whether this cycle's tokenless root check succeeded; failure resets success hysteresis.</param>
    /// <param name="token">Safe persistence cancellation.</param>
    /// <returns>A task completing after bounded success hysteresis or a durable automatic intent.</returns>
    /// <remarks>Automatic failback retains a separate enabled owned Cloud administration path; healthy Local reachability cannot move the last control host.</remarks>
    private async Task ConsiderAutomaticFailbackAsync(string botId, bool reachable, CancellationToken token)
    {
        var mutex = GetLock(botId);
        if (!await mutex.WaitAsync(0, token)) return;
        try
        {
            var state = await GetStatusAsync(botId, token);
            if (state.MigrationState != TelegramEndpointMigrationState.CloudRecovered ||
                state.DesiredEndpoint != TelegramEndpointType.Local || !_options.AutomaticFailback) return;
            if (HasIdentityAlias(state.BotId, state.TelegramBotId) || state.LastFailureCategory == "identity_alias_conflict")
            {
                state.LastFailureCategory = "identity_alias_conflict";
                await SaveAsync(state, null, null, token);
                return;
            }
            if (!HasIndependentCloudControl(state.TelegramBotId))
            {
                state.LastFailureCategory = "operator_control_missing";
                await SaveAsync(state, null, null, token);
                return;
            }
            if (!reachable || Volatile.Read(ref _sharedRootSuccesses) < _options.RecoveryThreshold) return;
            BeginIntent(state, TelegramEndpointType.Local, null, "automatic_failback", preserveDesired: true);
            if (await SaveAsync(state, "automatic_failback", "migration_started", token)) SignalWork();
        }
        finally { mutex.Release(); }
    }

    /// <summary>Runs one short tokenless root read and atomically publishes immutable shared-process metadata.</summary>
    /// <param name="token">Caller lifetime; the configured short health budget is added independently.</param>
    /// <returns>A typed official-root verification outcome; no bot token is used.</returns>
    private async Task<TelegramEndpointProbeResult> ProbeSharedAsync(CancellationToken token)
    {
        await _sharedProbe.WaitAsync(token);
        try
        {
            var result = await BoundedProbeAsync(_protocol.ProbeLocalServerAsync, token);
            Volatile.Write(ref _sharedRootSuccesses, result.Success
                ? Math.Min(_options.RecoveryThreshold, _sharedRootSuccesses + 1) : 0);
            var previous = SharedLocalHealth;
            Volatile.Write(ref _sharedHealth, new TelegramEndpointSharedHealth(result.Success, UtcNow,
                result.Success ? UtcNow : previous.LastSuccessAtUtc, TelegramEndpointHealthPolicy.Code(result.Failure)));
            return result;
        }
        finally { _sharedProbe.Release(); }
    }

    /// <summary>Runs a short identity read on an endpoint already authorized by the durable protocol phase.</summary>
    /// <param name="state">Exact bot identity and current positive runtime generation.</param>
    /// <param name="endpoint">Explicit authorized source or destination.</param>
    /// <param name="token">Whole-operation or safe foreground lifetime.</param>
    /// <returns>A bounded exact-identity outcome; the transport never retries the request.</returns>
    private Task<TelegramEndpointProbeResult> ProbeIdentityAsync(TelegramEndpointState state,
        TelegramEndpointType endpoint, CancellationToken token) => BoundedProbeAsync(
            budget => _protocol.VerifyIdentityAsync(state.BotId, state.TelegramBotId, endpoint, state.Generation, budget), token);

    /// <summary>Enforces a short safe-read budget even when an injected transport ignores cancellation.</summary>
    /// <param name="probe">Non-retrying safe root or authorized identity read.</param>
    /// <param name="token">Caller cancellation; genuine host/caller cancellation is propagated.</param>
    /// <returns>Success or a typed timeout/transport classification without retaining error text.</returns>
    private async Task<TelegramEndpointProbeResult> BoundedProbeAsync(
        Func<CancellationToken, Task<TelegramEndpointProbeResult>> probe, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HealthCheckTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await probe(linked.Token).WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(TelegramEndpointHealthPolicy.Classify(exception)); }
    }
}
