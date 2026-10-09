using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Durable migration phases and at-most-once session cleanup for the endpoint coordinator.</summary>
public sealed partial class TelegramEndpointCoordinator
{
    /// <summary>Processes at most sixteen due durable operations, serially per bot, without detached tasks.</summary>
    /// <param name="cancellationToken">Hosted worker shutdown token, never a management callback token.</param>
    /// <returns>A task completing after the bounded scan; cooldown and safe retries remain scheduled durably.</returns>
    /// <remarks>Only safe reads and receiver readiness may retry. A persisted logout attempt is never repeated.</remarks>
    public async Task RunPendingOperationsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!_options.Enabled || !await _workScan.WaitAsync(0, cancellationToken)) return;
        try
        {
            var due = (await ReadCurrentStatesAsync(cancellationToken, enabledOnly: true)).Where(x => (IsPending(x) || CanRecoverLocal(x)) &&
                (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= UtcNow))
                .OrderBy(x => x.NextAttemptAtUtc ?? DateTime.MinValue).Take(16);
            foreach (var pending in due)
            {
                var bot = FindBot(pending.BotId);
                if (bot == null || !bot.Enabled || TelegramBotTokenIdentity.ExtractBotId(bot.Token) != pending.TelegramBotId)
                    continue; // Old/replaced identity receipts cannot become a current route.
                var mutex = GetLock(pending.BotId);
                if (!await mutex.WaitAsync(0, cancellationToken)) continue;
                try
                {
                    var state = await _store.GetOrCreateAsync(pending.BotId, pending.TelegramBotId, cancellationToken);
                    if ((!IsPending(state) && !CanRecoverLocal(state)) || state.NextAttemptAtUtc > UtcNow) continue;
                    await ExecuteOperationAsync(state, cancellationToken);
                }
                finally { mutex.Release(); }
            }
            await GetPendingAlertCountAsync(cancellationToken);
        }
        finally { _workScan.Release(); }
    }

    /// <summary>Executes a due phase while owning the bot's coordinator lock and the receiver lifecycle lease.</summary>
    /// <param name="state">Current detached durable intent for an exact configured identity.</param>
    /// <param name="shutdownToken">Host lifetime, independent of the originating admin callback.</param>
    /// <returns>A task completing after one bounded phase; no cooldown sleep occurs under a bot lock.</returns>
    /// <remarks>The marker commits before mutation. Failure after that boundary stays fenced and uncertain.
    /// Cloud-to-Local also retains independent owned Cloud control before intent execution, before marker commit
    /// and immediately before dispatch. Losing control before HTTP restores the verified source without logout.</remarks>
    private async Task ExecuteOperationAsync(TelegramEndpointState state, CancellationToken shutdownToken)
    {
        if (CanRecoverLocal(state))
        {
            try { await RecoverLocalAsync(state, shutdownToken); }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (IsActive(state))
                    state = await _store.GetOrCreateAsync(state.BotId, state.TelegramBotId, CancellationToken.None);
                if (state.MigrationState == TelegramEndpointMigrationState.Local)
                    state.MigrationState = TelegramEndpointMigrationState.LocalUnavailable;
                await ScheduleSafeRetryAsync(state, TelegramEndpointHealthPolicy.Classify(exception), CancellationToken.None);
            }
            return;
        }
        if (state.MigrationState is TelegramEndpointMigrationState.CloudLogoutPending or TelegramEndpointMigrationState.LocalLogoutPending)
        {
            await MarkUncertainAsync(state);
            return;
        }
        if (state.MigrationState == TelegramEndpointMigrationState.CloudWait && state.CloudReuseEligibleAtUtc > UtcNow)
        {
            state.NextAttemptAtUtc = state.CloudReuseEligibleAtUtc;
            await SaveAsync(state, null, null, shutdownToken);
            return;
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(_options.MigrationTimeoutSeconds), _time);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, budget.Token);
        ITelegramEndpointReceiverLease lease = null;
        var stopped = false;
        try
        {
            if (!state.LogoutAttemptedAtUtc.HasValue && HasIdentityAlias(state.BotId, state.TelegramBotId))
            {
                if (state.MigrationState == TelegramEndpointMigrationState.FallbackPending)
                {
                    state.LastFailureCategory = "identity_alias_conflict";
                    state.MigrationState = TelegramEndpointMigrationState.LocalUnavailable;
                    state.NextAttemptAtUtc = UtcNow;
                    await SaveAsync(state, "migration_failed", "migration_failed", shutdownToken);
                }
                else if (state.MigrationState == TelegramEndpointMigrationState.CheckingLocal)
                    await FailBeforeLogoutAsync(state, "identity_alias_conflict", shutdownToken);
                return;
            }
            if (!state.LogoutAttemptedAtUtc.HasValue && state.Trigger == "manual" &&
                _configuration.GetSection(nameof(AppConfig.AdminsUserIds)).Get<List<long>>()?.Contains(state.ActorTelegramUserId ?? 0) != true)
            {
                await FailBeforeLogoutAsync(state, "configuration_missing", shutdownToken);
                return;
            }
            if (state.MigrationState == TelegramEndpointMigrationState.CheckingLocal)
            {
                if (!HasIndependentCloudControl(state.TelegramBotId))
                {
                    await FailBeforeLogoutAsync(state, "operator_control_missing", shutdownToken);
                    return;
                }
                if (!HasLocalFileMapping)
                {
                    await FailBeforeLogoutAsync(state, "configuration_missing", shutdownToken);
                    return;
                }
                var root = await ProbeSharedAsync(scope.Token);
                if (!root.Success)
                {
                    await FailBeforeLogoutAsync(state, TelegramEndpointHealthPolicy.Code(root.Failure), shutdownToken);
                    return;
                }
                // Never call Local getMe here: even getMe can create a Local session before Cloud logout.
                var source = await ProbeIdentityAsync(state, TelegramEndpointType.Cloud, scope.Token);
                if (!source.Success)
                {
                    await FailBeforeLogoutAsync(state, TelegramEndpointHealthPolicy.Code(source.Failure), shutdownToken);
                    return;
                }
            }
            else if (state.MigrationState == TelegramEndpointMigrationState.FallbackPending)
            {
                if (state.LogoutAttemptedAtUtc.HasValue)
                {
                    await MarkUncertainAsync(state);
                    return;
                }
            }

            var lifecycle = _services.GetRequiredService<ITelegramEndpointReceiverLifecycle>();
            lease = await lifecycle.AcquireAsync(state.BotId, scope.Token).WaitAsync(scope.Token);
            _gate.Fence(state.BotId, state.TelegramBotId);
            await lease.StopAndWaitAsync(scope.Token).WaitAsync(scope.Token);
            stopped = true;
            await _gate.DrainAsync(state.BotId, state.TelegramBotId,
                TimeSpan.FromSeconds(_options.MigrationDrainSeconds), scope.Token);
            if (state.MigrationState == TelegramEndpointMigrationState.LocalUnavailable)
            {
                state.NextAttemptAtUtc = null;
                await SaveAsync(state, null, null, shutdownToken);
                return;
            }
            if (state.MigrationState == TelegramEndpointMigrationState.FallbackPending)
            {
                var root = await ProbeSharedAsync(scope.Token);
                var source = root.Success ? await ProbeIdentityAsync(state, TelegramEndpointType.Local, scope.Token) : root;
                if (!source.Success)
                {
                    await ScheduleSafeRetryAsync(state, source.Failure, shutdownToken);
                    return;
                }
            }

            if (state.MigrationState is TelegramEndpointMigrationState.CheckingLocal or TelegramEndpointMigrationState.FallbackPending)
            {
                var endpoint = state.MigrationState == TelegramEndpointMigrationState.CheckingLocal
                    ? TelegramEndpointType.Cloud : TelegramEndpointType.Local;
                // Recheck current registry authority immediately before committing the mutation boundary.
                if (HasIdentityAlias(state.BotId, state.TelegramBotId))
                {
                    state.LastFailureCategory = "identity_alias_conflict";
                    state.LastFailureAtUtc = UtcNow;
                    await RestoreSourceAsync(state, lease, scope.Token, "migration_failed");
                    return;
                }
                if (endpoint == TelegramEndpointType.Cloud && !HasIndependentCloudControl(state.TelegramBotId))
                {
                    state.LastFailureCategory = "operator_control_missing";
                    await RestoreSourceAsync(state, lease, scope.Token, "migration_failed");
                    return;
                }
                var bot = FindBot(state.BotId);
                if (bot == null || !bot.Enabled || TelegramBotTokenIdentity.ExtractBotId(bot.Token) != state.TelegramBotId ||
                    (endpoint == TelegramEndpointType.Cloud && !HasLocalFileMapping) ||
                    (state.Trigger == "manual" && _configuration.GetSection(nameof(AppConfig.AdminsUserIds))
                        .Get<List<long>>()?.Contains(state.ActorTelegramUserId ?? 0) != true))
                {
                    state.LastFailureCategory = "configuration_missing";
                    state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
                    state.NextAttemptAtUtc = null;
                    await SaveAsync(state, "manual_intervention", "manual_intervention", CancellationToken.None);
                    return;
                }
                state.LogoutEndpoint = endpoint;
                state.LogoutAttemptedAtUtc = UtcNow;
                state.LogoutAcknowledgedAtUtc = null;
                state.MigrationState = endpoint == TelegramEndpointType.Cloud
                    ? TelegramEndpointMigrationState.CloudLogoutPending : TelegramEndpointMigrationState.LocalLogoutPending;
                state.NextAttemptAtUtc = null;
                if (!await SaveAsync(state, endpoint == TelegramEndpointType.Cloud ? "cloud_logout_intent" : "local_logout_intent",
                    null, scope.Token)) return;
                var controlMissing = endpoint == TelegramEndpointType.Cloud && !HasIndependentCloudControl(state.TelegramBotId);
                if (HasIdentityAlias(state.BotId, state.TelegramBotId) || controlMissing)
                {
                    // The marker committed, but no transport was dispatched: this live path can safely clear it.
                    state.LogoutAttemptedAtUtc = null;
                    state.LogoutEndpoint = null;
                    state.LastFailureCategory = controlMissing ? "operator_control_missing" : "identity_alias_conflict";
                    await RestoreSourceAsync(state, lease, scope.Token, "migration_failed");
                    return;
                }

                TelegramEndpointLogoutResult result;
                try
                {
                    result = await _protocol.LogOutOnceAsync(state.BotId, state.TelegramBotId, endpoint, state.Generation, scope.Token)
                        .WaitAsync(scope.Token);
                }
                catch (Exception) { result = new(false, true, TelegramEndpointFailure.LogoutUncertain); }
                // Even host cancellation must not erase an acknowledgement/uncertainty already observed.
                if (result.Uncertain || (!result.Acknowledged && result.Failure == TelegramEndpointFailure.None))
                {
                    await MarkUncertainAsync(state);
                    return;
                }
                if (!result.Acknowledged)
                {
                    state.LastFailureCategory = "logout_refused";
                    state.LastFailureAtUtc = UtcNow;
                    state.LogoutAttemptedAtUtc = null;
                    state.LogoutEndpoint = null;
                    await RestoreSourceAsync(state, lease, scope.Token);
                    return;
                }
                state.LogoutAcknowledgedAtUtc = UtcNow;
                // Official Cloud logOut imposes ten minutes; Local logOut gets a fresh conservative wait too.
                state.CloudReuseEligibleAtUtc = UtcNow.AddMinutes(10);
                state.LastFailureCategory = null;
                state.RecoveryAttempts = 0;
                state.MigrationState = endpoint == TelegramEndpointType.Cloud
                    ? TelegramEndpointMigrationState.SwitchingToLocal : TelegramEndpointMigrationState.CloudWait;
                state.NextAttemptAtUtc = endpoint == TelegramEndpointType.Local ? state.CloudReuseEligibleAtUtc : UtcNow;
                if (!await SaveAsync(state, "logout_acknowledged", endpoint == TelegramEndpointType.Local ? "fallback_pending" : null,
                    CancellationToken.None)) return;
                if (endpoint == TelegramEndpointType.Local) return;
            }

            if (state.MigrationState == TelegramEndpointMigrationState.SwitchingToLocal)
            {
                if (state.LogoutEndpoint != TelegramEndpointType.Cloud || !state.LogoutAcknowledgedAtUtc.HasValue)
                {
                    await MarkUncertainAsync(state);
                    return;
                }
                await ActivateAsync(state, TelegramEndpointType.Local, lease, scope.Token);
            }
            else if (state.MigrationState is TelegramEndpointMigrationState.CloudWait or TelegramEndpointMigrationState.SwitchingToCloud)
            {
                if (state.LogoutEndpoint != TelegramEndpointType.Local || !state.LogoutAcknowledgedAtUtc.HasValue ||
                    !state.CloudReuseEligibleAtUtc.HasValue)
                {
                    state.LastFailureCategory = "unsafe_cleanup";
                    state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
                    state.NextAttemptAtUtc = null;
                    await SaveAsync(state, "manual_intervention", "manual_intervention", CancellationToken.None);
                    return;
                }
                if (state.CloudReuseEligibleAtUtc > UtcNow)
                {
                    state.MigrationState = TelegramEndpointMigrationState.CloudWait;
                    state.NextAttemptAtUtc = state.CloudReuseEligibleAtUtc;
                    await SaveAsync(state, "cloud_wait", null, CancellationToken.None);
                    return;
                }
                await ActivateAsync(state, TelegramEndpointType.Cloud, lease, scope.Token);
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            if (state.LogoutAttemptedAtUtc.HasValue && !state.LogoutAcknowledgedAtUtc.HasValue)
                await MarkUncertainAsync(state);
            throw;
        }
        catch (Exception exception)
        {
            if (lease != null) await StopReceiverBoundedAsync(state, lease);
            var failure = TelegramEndpointHealthPolicy.Classify(exception);
            if (state.LogoutAttemptedAtUtc.HasValue && !state.LogoutAcknowledgedAtUtc.HasValue)
                await MarkUncertainAsync(state);
            else if (state.MigrationState == TelegramEndpointMigrationState.LocalUnavailable)
            {
                state.LastFailureCategory = "drain_timeout";
                state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
                state.NextAttemptAtUtc = null;
                await SaveAsync(state, "manual_intervention", "manual_intervention", CancellationToken.None);
            }
            else if (state.LogoutAcknowledgedAtUtc.HasValue)
            {
                if (IsActive(state))
                    state = await _store.GetOrCreateAsync(state.BotId, state.TelegramBotId, CancellationToken.None);
                state.MigrationState = state.LogoutEndpoint == TelegramEndpointType.Cloud
                    ? TelegramEndpointMigrationState.SwitchingToLocal : TelegramEndpointMigrationState.SwitchingToCloud;
                await ScheduleSafeRetryAsync(state, failure, CancellationToken.None);
            }
            else if (state.MigrationState == TelegramEndpointMigrationState.FallbackPending)
                await ScheduleSafeRetryAsync(state, failure, CancellationToken.None);
            else if (stopped && lease != null)
            {
                state.LastFailureCategory = failure == TelegramEndpointFailure.Timeout ? "drain_timeout" : "lifecycle_unavailable";
                // A failed drain cannot replace the old generation; leave an explicit fenced state.
                state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
                state.NextAttemptAtUtc = null;
                await SaveAsync(state, "manual_intervention", "manual_intervention", CancellationToken.None);
            }
            else
                await FailBeforeLogoutAsync(state, "lifecycle_unavailable", CancellationToken.None);
            _logger.LogWarning("Telegram endpoint operation deferred. Category={Category}", state.LastFailureCategory);
        }
        finally
        {
            if (lease != null)
            {
                if (!_gate.IsAvailable(state.BotId, state.TelegramBotId))
                    await StopReceiverBoundedAsync(state, lease);
                await lease.DisposeAsync();
            }
        }
    }

    /// <summary>Validates destination identity and actual receiver readiness before publishing ordinary traffic.</summary>
    /// <param name="state">Durable acknowledged cleanup and due destination phase.</param>
    /// <param name="endpoint">Explicit destination permitted by logout proof and cooldown.</param>
    /// <param name="lease">Exclusive stopped receiver lifecycle lease.</param>
    /// <param name="token">Whole operation budget, separate from the admin callback.</param>
    /// <returns>A task completing with an active committed route or a fenced bounded safe retry.</returns>
    /// <remarks>The parent lifecycle additionally checks webhook absence and nondropping short getUpdates.</remarks>
    private async Task ActivateAsync(TelegramEndpointState state, TelegramEndpointType endpoint,
        ITelegramEndpointReceiverLease lease, CancellationToken token)
    {
        var identity = await ProbeIdentityAsync(state, endpoint, token);
        if (!identity.Success)
        {
            await ScheduleSafeRetryAsync(state, identity.Failure, CancellationToken.None);
            return;
        }
        state.Generation++;
        state.MigrationState = endpoint == TelegramEndpointType.Local
            ? TelegramEndpointMigrationState.SwitchingToLocal : TelegramEndpointMigrationState.SwitchingToCloud;
        if (!await SaveAsync(state, "destination_starting", null, token)) return;
        _gate.PrepareActivation(state.BotId, state.TelegramBotId, endpoint, state.Generation);
        if (!await lease.StartValidatedAsync(token).WaitAsync(token))
        {
            await StopReceiverBoundedAsync(state, lease);
            await ScheduleSafeRetryAsync(state, TelegramEndpointFailure.ReceiverNotReady, CancellationToken.None);
            return;
        }
        state.EffectiveEndpoint = endpoint;
        state.MigrationState = endpoint == TelegramEndpointType.Local ? TelegramEndpointMigrationState.Local :
            state.DesiredEndpoint == TelegramEndpointType.Local ? TelegramEndpointMigrationState.CloudRecovered : TelegramEndpointMigrationState.Cloud;
        state.LastMigrationAtUtc = UtcNow;
        state.LastSuccessfulHealthAtUtc = UtcNow;
        state.LastHealthCheckAtUtc = UtcNow;
        state.LastFailureCategory = null;
        state.ConsecutiveFailures = 0;
        state.ConsecutiveSuccesses = 0;
        state.RecoveryAttempts = 0;
        state.NextAttemptAtUtc = null;
        var recovered = state.MigrationState == TelegramEndpointMigrationState.CloudRecovered;
        if (!await SaveAsync(state, recovered ? "cloud_recovered" : "migration_succeeded",
            recovered ? "cloud_recovered" : "migration_succeeded", CancellationToken.None))
        {
            _gate.Fence(state.BotId, state.TelegramBotId);
            await StopReceiverBoundedAsync(state, lease);
            return;
        }
    }

    /// <summary>Restarts only the known source after a definitive logout refusal or a newly detected identity alias.</summary>
    /// <param name="state">Source state with a safe refusal category and no uncertain mutation boundary.</param>
    /// <param name="lease">Exclusive stopped and drained source receiver lease.</param>
    /// <param name="token">Remaining whole-operation budget.</param>
    /// <param name="reason">Closed logout_refused or migration_failed history reason for this safe source restoration.</param>
    /// <returns>A task completing with the verified original route or explicit intervention.</returns>
    private async Task RestoreSourceAsync(TelegramEndpointState state, ITelegramEndpointReceiverLease lease, CancellationToken token,
        string reason = "logout_refused")
    {
        _gate.PrepareActivation(state.BotId, state.TelegramBotId, state.EffectiveEndpoint, state.Generation);
        if (!await lease.StartValidatedAsync(token).WaitAsync(token))
        {
            state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
            state.NextAttemptAtUtc = null;
            await SaveAsync(state, reason, "manual_intervention", CancellationToken.None);
            return;
        }
        state.MigrationState = state.EffectiveEndpoint == TelegramEndpointType.Local
            ? TelegramEndpointMigrationState.Local : TelegramEndpointMigrationState.Cloud;
        state.NextAttemptAtUtc = null;
        if (!await SaveAsync(state, reason, "migration_failed", CancellationToken.None))
        {
            _gate.Fence(state.BotId, state.TelegramBotId);
            await StopReceiverBoundedAsync(state, lease);
        }
    }

    /// <summary>Records a safe prelogout failure while preserving the original effective service.</summary>
    /// <param name="state">Intent that has not crossed the irreversible boundary.</param>
    /// <param name="category">Closed configuration or probe failure code.</param>
    /// <param name="token">Persistence cancellation.</param>
    /// <returns>A task completing after active-source failure history and alert have committed.</returns>
    private async Task FailBeforeLogoutAsync(TelegramEndpointState state, string category, CancellationToken token)
    {
        state.LastFailureCategory = category;
        state.LastFailureAtUtc = UtcNow;
        state.MigrationState = state.EffectiveEndpoint == TelegramEndpointType.Local
            ? TelegramEndpointMigrationState.Local : TelegramEndpointMigrationState.Cloud;
        state.NextAttemptAtUtc = null;
        await SaveAsync(state, "migration_failed", "migration_failed", token);
    }

    /// <summary>Schedules bounded safe reads/readiness or exposes exhausted recovery without replaying cleanup.</summary>
    /// <param name="state">Current fenced phase; acknowledged cleanup is never cleared.</param>
    /// <param name="failure">Typed safe-read or readiness failure.</param>
    /// <param name="token">Persistence cancellation.</param>
    /// <returns>A task completing after a durable due time or explicit manual-intervention state.</returns>
    private async Task ScheduleSafeRetryAsync(TelegramEndpointState state, TelegramEndpointFailure failure, CancellationToken token)
    {
        state.RecoveryAttempts++;
        state.LastFailureCategory = TelegramEndpointHealthPolicy.Code(failure);
        state.LastFailureAtUtc = UtcNow;
        _gate.Fence(state.BotId, state.TelegramBotId);
        if (failure is TelegramEndpointFailure.IdentityMismatch or TelegramEndpointFailure.TokenRejected or TelegramEndpointFailure.ConfigurationMissing ||
            state.RecoveryAttempts >= _options.RecoveryMaxAttempts)
        {
            if (state.RecoveryAttempts >= _options.RecoveryMaxAttempts) state.LastFailureCategory = "recovery_exhausted";
            state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
            state.NextAttemptAtUtc = null;
            await SaveAsync(state, "manual_intervention", "manual_intervention", token);
            return;
        }
        state.NextAttemptAtUtc = UtcNow + TelegramEndpointHealthPolicy.RetryDelay(state.RecoveryAttempts, _options.RetryMaxSeconds);
        await SaveAsync(state, "safe_retry_scheduled", state.RecoveryAttempts == 1 ? "fallback_pending" : null, token);
    }

    /// <summary>Commits non-replayable uncertainty after a pending, interrupted or ambiguously acknowledged logout.</summary>
    /// <param name="state">State whose durable attempt boundary may already have changed a bot session.</param>
    /// <returns>A task completing after explicit uncertainty and a deduplicated independent alert are committed.</returns>
    private async Task MarkUncertainAsync(TelegramEndpointState state)
    {
        _gate.Fence(state.BotId, state.TelegramBotId);
        state.MigrationState = state.LogoutEndpoint == TelegramEndpointType.Local
            ? TelegramEndpointMigrationState.LocalLogoutUncertain : TelegramEndpointMigrationState.CloudLogoutUncertain;
        state.LastFailureCategory = "logout_uncertain";
        state.LastFailureAtUtc = UtcNow;
        state.NextAttemptAtUtc = null;
        await SaveAsync(state, "logout_uncertain", "manual_intervention", CancellationToken.None);
    }

    /// <summary>Resumes a previously healthy Local session only after recovery hysteresis and without any logout.</summary>
    /// <param name="state">Fenced or degraded Local state with two safe identity successes and no cleanup uncertainty.</param>
    /// <param name="token">Worker lifetime, never the health-refresh callback token.</param>
    /// <returns>A task completing after same-session receiver readiness or a bounded safe retry.</returns>
    private async Task RecoverLocalAsync(TelegramEndpointState state, CancellationToken token)
    {
        if (state.MigrationState == TelegramEndpointMigrationState.LocalDegraded)
        {
            state.MigrationState = TelegramEndpointMigrationState.Local;
            state.NextAttemptAtUtc = null;
            await SaveAsync(state, null, null, token);
            return;
        }
        var root = await ProbeSharedAsync(token);
        var identity = root.Success ? await ProbeIdentityAsync(state, TelegramEndpointType.Local, token) : root;
        if (!identity.Success)
        {
            state.ConsecutiveSuccesses = 0;
            await ScheduleSafeRetryAsync(state, identity.Failure, token);
            return;
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(_options.MigrationTimeoutSeconds), _time);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        var lifecycle = _services.GetRequiredService<ITelegramEndpointReceiverLifecycle>();
        await using var lease = await lifecycle.AcquireAsync(state.BotId, scope.Token).WaitAsync(scope.Token);
        try
        {
            _gate.Fence(state.BotId, state.TelegramBotId);
            await lease.StopAndWaitAsync(scope.Token).WaitAsync(scope.Token);
            await _gate.DrainAsync(state.BotId, state.TelegramBotId, TimeSpan.FromSeconds(_options.MigrationDrainSeconds), scope.Token);
            state.Generation++;
            if (!await SaveAsync(state, "destination_starting", null, scope.Token)) return;
            _gate.PrepareActivation(state.BotId, state.TelegramBotId, TelegramEndpointType.Local, state.Generation);
            if (!await lease.StartValidatedAsync(scope.Token).WaitAsync(scope.Token))
            {
                await StopReceiverBoundedAsync(state, lease);
                state.ConsecutiveSuccesses = 0;
                await ScheduleSafeRetryAsync(state, TelegramEndpointFailure.ReceiverNotReady, CancellationToken.None);
                return;
            }
            state.MigrationState = TelegramEndpointMigrationState.Local;
            state.LastFailureCategory = null;
            state.RecoveryAttempts = 0;
            state.ConsecutiveFailures = 0;
            state.NextAttemptAtUtc = null;
            if (!await SaveAsync(state, "local_recovered", "local_recovered", CancellationToken.None))
            {
                _gate.Fence(state.BotId, state.TelegramBotId);
                await StopReceiverBoundedAsync(state, lease);
                return;
            }
        }
        finally
        {
            if (!_gate.IsAvailable(state.BotId, state.TelegramBotId))
                await StopReceiverBoundedAsync(state, lease);
        }
    }

    /// <summary>Fences and joins any partially started receiver within an independent bounded cleanup budget.</summary>
    /// <param name="state">Exact durable bot identity, still owned by its coordinator/lifecycle locks.</param>
    /// <param name="lease">Current exclusive receiver lifecycle lease, never reacquired recursively.</param>
    /// <returns>True when polling joined; false leaves the route fenced for later safe recovery/manual review.</returns>
    /// <remarks>Does not cancel or replay accepted ordinary sends. Shutdown cannot make this receiver join indefinite.</remarks>
    private async Task<bool> StopReceiverBoundedAsync(TelegramEndpointState state, ITelegramEndpointReceiverLease lease)
    {
        try
        {
            _gate.Fence(state.BotId, state.TelegramBotId);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_options.MigrationTimeoutSeconds, 5, 30)), _time);
            await lease.StopAndWaitAsync(budget.Token).WaitAsync(budget.Token);
            return true;
        }
        catch (Exception)
        {
            _logger.LogWarning("Telegram endpoint receiver cleanup deferred. Category={Category}", "receiver_join_timeout");
            return false;
        }
    }

    /// <summary>Checks that safe health recovery can resume the original Local session without bypassing cleanup proof.</summary>
    /// <param name="state">Current detached local-health state.</param>
    /// <returns>True only after recovery hysteresis and before any Local logout attempt or uncertainty.</returns>
    private bool CanRecoverLocal(TelegramEndpointState state) => state.EffectiveEndpoint == TelegramEndpointType.Local &&
        state.ConsecutiveSuccesses >= _options.RecoveryThreshold && !IsUncertain(state) &&
        !(state.LogoutEndpoint == TelegramEndpointType.Local && state.LogoutAttemptedAtUtc.HasValue) &&
        (state.MigrationState is TelegramEndpointMigrationState.LocalDegraded or TelegramEndpointMigrationState.LocalUnavailable ||
         state.MigrationState == TelegramEndpointMigrationState.FallbackPending && state.Trigger == "automatic_outage" ||
         state.MigrationState == TelegramEndpointMigrationState.ManualInterventionRequired &&
            state.DesiredEndpoint == TelegramEndpointType.Local && !state.LogoutAttemptedAtUtc.HasValue);

    /// <summary>Initializes a new authorized operation without changing automatic fallback's desired Local intent.</summary>
    /// <param name="state">Detached current identity state, serialized under its bot lock.</param>
    /// <param name="target">Explicit operation destination, not a URL.</param>
    /// <param name="actor">Operator Telegram user id, or null for system intent.</param>
    /// <param name="trigger">Closed manual/automatic trigger.</param>
    /// <param name="preserveDesired">True for automatic fallback so desired Local remains visible.</param>
    private void BeginIntent(TelegramEndpointState state, TelegramEndpointType target, long? actor, string trigger, bool preserveDesired)
    {
        if (!preserveDesired) state.DesiredEndpoint = target;
        state.ControlRevision++;
        state.OperationId = Guid.NewGuid().ToString("N");
        state.ActorTelegramUserId = actor;
        state.Trigger = trigger;
        if (trigger != "automatic_outage") state.OutageId = null;
        state.MigrationStartedAtUtc = UtcNow;
        foreach (var obsolete in _operationClocks.Keys.Where(x => FindBot(x) == null))
            _operationClocks.TryRemove(obsolete, out _);
        _operationClocks[state.BotId] = (state.OperationId, _time.GetTimestamp());
        state.LogoutAttemptedAtUtc = null;
        state.LogoutAcknowledgedAtUtc = null;
        state.LogoutEndpoint = null;
        state.LastFailureCategory = null;
        state.ConsecutiveSuccesses = 0;
        state.RecoveryAttempts = 0;
        state.NextAttemptAtUtc = UtcNow;
        state.MigrationState = target == TelegramEndpointType.Local
            ? TelegramEndpointMigrationState.CheckingLocal : TelegramEndpointMigrationState.FallbackPending;
    }

    /// <summary>Identifies durable work phases eligible for safe worker execution.</summary>
    /// <param name="state">Current detached state.</param>
    /// <returns>True for scheduled migration phases; uncertainty and manual review are never automatic mutation work.</returns>
    private static bool IsPending(TelegramEndpointState state) => state.MigrationState is
        TelegramEndpointMigrationState.CheckingLocal or TelegramEndpointMigrationState.FallbackPending or
        TelegramEndpointMigrationState.CloudLogoutPending or TelegramEndpointMigrationState.LocalLogoutPending or
        TelegramEndpointMigrationState.SwitchingToLocal or TelegramEndpointMigrationState.CloudWait or TelegramEndpointMigrationState.SwitchingToCloud ||
        state.MigrationState == TelegramEndpointMigrationState.LocalUnavailable && state.NextAttemptAtUtc.HasValue;

    /// <summary>Identifies active receiver phases, including degradation below the sustained-outage threshold.</summary>
    /// <param name="state">Current detached durable state.</param>
    /// <returns>True only when the durable phase normally admits new handlers.</returns>
    private static bool IsActive(TelegramEndpointState state) => state.MigrationState is TelegramEndpointMigrationState.Cloud or
        TelegramEndpointMigrationState.CloudRecovered or TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded;

    /// <summary>Identifies explicit uncertainty that no manual target selection may bypass.</summary>
    /// <param name="state">Current detached durable state.</param>
    /// <returns>True for an unknown cleanup result, including a contradictory attempted marker.</returns>
    private static bool IsUncertain(TelegramEndpointState state) => state.MigrationState is
        TelegramEndpointMigrationState.CloudLogoutUncertain or TelegramEndpointMigrationState.LocalLogoutUncertain ||
        (state.LogoutAttemptedAtUtc.HasValue && !state.LogoutAcknowledgedAtUtc.HasValue && state.LastFailureCategory != "logout_refused");
}
