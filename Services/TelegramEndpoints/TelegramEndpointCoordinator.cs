using System.Collections.Concurrent;
using Adminbot.Domain;
using Adminbot.Services.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Owns durable, bot-specific endpoint intent and serialized migration/health state.</summary>
/// <remarks>Callbacks only commit intent and wake bounded hosted work. No callback drains its own handler.
/// Lifecycle is resolved lazily to avoid the provider/receiver-host dependency cycle. Every irreversible
/// logout has a durable marker first; pending or ambiguous markers are never replayed after a crash.</remarks>
public sealed partial class TelegramEndpointCoordinator : ITelegramEndpointAdministration, IDisposable
{
    /// <summary>Detached compare-and-swap persistence; no network occurs inside store transactions.</summary>
    private readonly ITelegramEndpointStateStore _store;
    /// <summary>Per-bot request/handler admission and generation fence.</summary>
    private readonly TelegramEndpointRuntimeGate _gate;
    /// <summary>Current canonical identities across owned, tenant and assistant bots.</summary>
    private readonly BotRegistry _registry;
    /// <summary>Live global administrator allowlist; tenant ownership grants no migration authority.</summary>
    private readonly IConfiguration _configuration;
    /// <summary>Validated startup snapshot, including conservative protocol and resource bounds.</summary>
    private readonly TelegramEndpointRoutingOptions _options;
    /// <summary>Lazy receiver lifecycle resolver; never resolved in the constructor.</summary>
    private readonly IServiceProvider _services;
    /// <summary>Metadata-only local diagnostics; external errors are never passed to the logger.</summary>
    private readonly ILogger<TelegramEndpointCoordinator> _logger;
    /// <summary>Optional nonblocking, payload-free endpoint observations.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>UTC, monotonic and budget clocks, replaceable by deterministic tests.</summary>
    private readonly TimeProvider _time;
    /// <summary>Non-retrying transport; Local getMe is authorized only by durable source state.</summary>
    private readonly ITelegramEndpointProtocol _protocol;
    /// <summary>Whether this coordinator owns the default protocol's root HTTP transport.</summary>
    private readonly bool _ownsProtocol;
    /// <summary>Serializes health, operator intent and migration for each canonical internal bot id.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _botLocks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One monotonic current-process operation clock per current bot; restart durations remain unknown.</summary>
    private readonly ConcurrentDictionary<string, (string OperationId, long Started)> _operationClocks =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Serializes initialization, including crash reconciliation before receiver startup.</summary>
    private readonly SemaphoreSlim _initialization = new(1, 1);
    /// <summary>Serializes bounded worker scans; tests may invoke scans directly without starting a host.</summary>
    private readonly SemaphoreSlim _workScan = new(1, 1);
    /// <summary>Capacity-one wake signal; durable states, not this signal, are the work queue.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);
    /// <summary>Serializes bounded tokenless root checks so concurrent loops cannot lose the latest success metadata.</summary>
    private readonly SemaphoreSlim _sharedProbe = new(1, 1);
    /// <summary>Consecutive verified tokenless root checks, saturated at recovery threshold and protected by shared probe serialization.</summary>
    private int _sharedRootSuccesses;
    /// <summary>One successfully persisted shared-server recovery alert outage id per current bot; durable outbox deduplicates restart.</summary>
    private readonly ConcurrentDictionary<string, string> _sharedRecoveryAlertMemo = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Indicates that all saved routes have been hydrated before receivers may start.</summary>
    private bool _initialized;
    /// <summary>Latest immutable shared-process root health; no token or bot identity is involved.</summary>
    private TelegramEndpointSharedHealth _sharedHealth = new();
    /// <summary>Cached durable notification backlog for synchronous status displays.</summary>
    private int _pendingAlerts;

    /// <summary>Creates the durable endpoint controller without resolving the receiver host.</summary>
    /// <param name="store">Required detached CAS state/history/alert store.</param>
    /// <param name="gate">Required shared bot generation and request-admission gate.</param>
    /// <param name="registry">Required current runtime bot identity registry.</param>
    /// <param name="clients">Required exact endpoint control provider; used only to create the default protocol.</param>
    /// <param name="configuration">Required live global administrator configuration.</param>
    /// <param name="options">Required startup-bound endpoint options; copied and validated.</param>
    /// <param name="services">Required lazy service resolver for receiver lifecycle execution, not construction.</param>
    /// <param name="logger">Required metadata-only operational logger.</param>
    /// <param name="telemetry">Optional nonblocking endpoint telemetry writer.</param>
    /// <param name="timeProvider">Optional deterministic clock and budget timers.</param>
    /// <param name="protocol">Optional fake or production non-retrying transport; URLs are never test overrides.</param>
    /// <remarks>The host calls InitializeAsync before starting any receiver. No network is performed by construction.</remarks>
    /// <exception cref="ArgumentNullException">A required dependency is missing.</exception>
    /// <example><code>await coordinator.InitializeAsync(shutdownToken); // Before receiver hosted services start.</code></example>
    public TelegramEndpointCoordinator(ITelegramEndpointStateStore store, TelegramEndpointRuntimeGate gate,
        BotRegistry registry, BotClientProvider clients, IConfiguration configuration,
        TelegramEndpointRoutingOptions options, IServiceProvider services,
        ILogger<TelegramEndpointCoordinator> logger, LatencyTelemetryService telemetry = null,
        TimeProvider timeProvider = null, ITelegramEndpointProtocol protocol = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot();
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry;
        _time = timeProvider ?? TimeProvider.System;
        _ownsProtocol = protocol == null;
        _protocol = protocol ?? new TelegramEndpointProtocol(clients, _options);
    }

    /// <summary>Gets tokenless shared Local reachability, not bot authentication or readiness.</summary>
    public TelegramEndpointSharedHealth SharedLocalHealth => Volatile.Read(ref _sharedHealth);
    /// <summary>Gets the latest observed number of durable pending/manual-review alert rows.</summary>
    public int PendingAlertCount => Volatile.Read(ref _pendingAlerts);

    /// <summary>Hydrates all saved identities and converts crash-time logout intent into non-replayable uncertainty.</summary>
    /// <param name="cancellationToken">Startup/shutdown token for bounded persistence reads.</param>
    /// <returns>A task completing only after persisted routes have been published before any receiver starts.</returns>
    /// <remarks>Saved Local and uncertain states never receive a guessed Cloud route. Interrupted safe reads resume,
    /// acknowledged cleanup resumes destination/cooldown, and an unacknowledged mutation requires intervention.</remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            foreach (var state in await ReadCurrentStatesAsync(cancellationToken))
            {
                var currentBot = FindBot(state.BotId);
                if (currentBot == null || TelegramBotTokenIdentity.ExtractBotId(currentBot.Token) != state.TelegramBotId)
                    continue;
                _gate.Publish(state);
                if (state.MigrationState is TelegramEndpointMigrationState.CloudLogoutPending or
                    TelegramEndpointMigrationState.LocalLogoutPending)
                {
                    state.MigrationState = state.LogoutEndpoint == TelegramEndpointType.Local
                        ? TelegramEndpointMigrationState.LocalLogoutUncertain
                        : TelegramEndpointMigrationState.CloudLogoutUncertain;
                    state.LastFailureCategory = "logout_uncertain";
                    state.LastFailureAtUtc = UtcNow;
                    state.NextAttemptAtUtc = null;
                    if (!await SaveAsync(state, "startup_reconciled", "manual_intervention", cancellationToken))
                        throw new InvalidOperationException("Endpoint startup reconciliation conflicted.");
                }
                else if (state.LogoutAttemptedAtUtc.HasValue && !state.LogoutAcknowledgedAtUtc.HasValue &&
                    state.LastFailureCategory != "logout_refused" && state.MigrationState is not
                        (TelegramEndpointMigrationState.CloudLogoutUncertain or TelegramEndpointMigrationState.LocalLogoutUncertain))
                {
                    state.MigrationState = state.LogoutEndpoint == TelegramEndpointType.Local
                        ? TelegramEndpointMigrationState.LocalLogoutUncertain
                        : TelegramEndpointMigrationState.CloudLogoutUncertain;
                    state.LastFailureCategory = "logout_uncertain";
                    state.NextAttemptAtUtc = null;
                    if (!await SaveAsync(state, "startup_reconciled", "manual_intervention", cancellationToken))
                        throw new InvalidOperationException("Endpoint startup reconciliation conflicted.");
                }
            }
            await GetPendingAlertCountAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    /// <summary>Lists current registry identities, including disabled and token-missing panel entries.</summary>
    /// <param name="cancellationToken">Caller persistence-read cancellation token.</param>
    /// <returns>Detached bot states ordered by canonical id; missing configuration is represented explicitly, never routed.</returns>
    /// <remarks>Historical rows for a different BotFather identity do not become the current bot's endpoint.</remarks>
    public async Task<IReadOnlyList<TelegramEndpointState>> GetInventoryAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var result = new List<TelegramEndpointState>();
        foreach (var bot in _registry.Bots.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var identity = TelegramBotTokenIdentity.ExtractBotId(bot.Token);
            if (identity is > 0)
                result.Add(await _store.GetOrCreateAsync(bot.Id, identity.Value, cancellationToken));
            else
                result.Add(new TelegramEndpointState
                {
                    BotId = bot.Id, TelegramBotId = 0,
                    MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired,
                    LastFailureCategory = "configuration_missing"
                });
        }
        return result;
    }

    /// <summary>Reads the current exact bot's detached durable status without probing Telegram.</summary>
    /// <param name="botId">Required canonical internal registry id, not a numeric Telegram bot or user id.</param>
    /// <param name="cancellationToken">Caller persistence-read cancellation.</param>
    /// <returns>The current identity's state, including disabled bots; no alternate default bot is substituted.</returns>
    /// <remarks>A dynamically reintroduced identity hydrates from its saved row only once; status reads never reopen a temporary runtime fence.</remarks>
    /// <exception cref="ArgumentException">The requested bot is missing or has no valid configured identity.</exception>
    public async Task<TelegramEndpointState> GetStatusAsync(string botId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var bot = FindBot(botId);
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        if (identity is not > 0) throw new ArgumentException("Endpoint bot configuration is unavailable.", nameof(botId));
        var state = await _store.GetOrCreateAsync(bot.Id, identity.Value, cancellationToken);
        if (TelegramBotTokenIdentity.ExtractBotId(FindBot(bot.Id)?.Token) == state.TelegramBotId)
            _gate.PublishIfUnhydrated(state);
        return state;
    }

    /// <summary>Commits an authorized operator's explicit migration intent and returns before any receiver drain.</summary>
    /// <param name="botId">Exact canonical internal bot selected by the bounded admin confirmation.</param>
    /// <param name="target">Explicit Cloud or Local enum, never an arbitrary endpoint URL.</param>
    /// <param name="actor">Positive Telegram user id checked against the current global administrator allowlist.</param>
    /// <param name="expectedControlRevision">Nonnegative control revision shown in the one-use confirmation.</param>
    /// <param name="expectedTelegramBotId">Positive BotFather identity frozen by the displayed confirmation; replacement identities are stale.</param>
    /// <param name="cancellationToken">Callback token used only for authorization/persistence, not background migration.</param>
    /// <returns>A fixed status code: accepted, unchanged, stale, busy, denied, aliased, unavailable, disabled, unsafe or control_path_missing.</returns>
    /// <remarks>Bot-specific nonwaiting locks prevent concurrent intents. Uncertain logout cannot be manually overridden.
    /// Cloud-to-Local intent retains another enabled owned Cloud identity for independent private administration.
    /// The hosted worker owns lifecycle drain; this method never waits for the callback's own handler to finish.</remarks>
    /// <example><code>var outcome = await coordinator.RequestMigrationAsync(botId, TelegramEndpointType.Local, adminId, revision, identity, token);</code></example>
    public async Task<string> RequestMigrationAsync(string botId, TelegramEndpointType target, long actor,
        long expectedControlRevision, long expectedTelegramBotId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var status = ValidateCommand(botId, actor, out var bot, out var identity);
        if (status != null) return status;
        if (!Enum.IsDefined(target) || expectedControlRevision < 0) return "denied";
        if (target == TelegramEndpointType.Local && !HasLocalFileMapping) return "unavailable";
        var mutex = GetLock(bot.Id);
        if (!await mutex.WaitAsync(0, cancellationToken)) return "busy";
        try
        {
            status = ValidateCommand(botId, actor, out bot, out identity);
            if (status != null) return status;
            if (identity != expectedTelegramBotId || expectedTelegramBotId <= 0) return "stale";
            var state = await _store.GetOrCreateAsync(bot.Id, identity, cancellationToken);
            status = ValidateCommand(botId, actor, out bot, out identity);
            if (status != null) return status;
            if (identity != expectedTelegramBotId || state.TelegramBotId != identity) return "stale";
            if (state.ControlRevision != expectedControlRevision) return "stale";
            if (state.LastFailureCategory == "identity_alias_conflict") return "aliased";
            if (IsUncertain(state)) return "unsafe";
            if (IsPending(state)) return "busy";
            if (!IsActive(state) && state.LogoutAcknowledgedAtUtc.HasValue) return "unsafe";
            if (!IsActive(state) && state.EffectiveEndpoint == target) return "unsafe";
            if (state.EffectiveEndpoint == target && IsActive(state))
            {
                if (state.DesiredEndpoint == target) return "unchanged";
                state.DesiredEndpoint = target;
                state.ControlRevision++;
                state.OperationId = Guid.NewGuid().ToString("N");
                state.ActorTelegramUserId = actor;
                state.Trigger = "manual";
                return await SaveAsync(state, "migration_requested", null, cancellationToken) ? "accepted" : "stale";
            }
            if (target == TelegramEndpointType.Local && !HasIndependentCloudControl(identity)) return "control_path_missing";
            BeginIntent(state, target, actor, "manual", preserveDesired: false);
            if (!await SaveAsync(state, "migration_requested", "migration_started", cancellationToken)) return "stale";
            SignalWork();
            return "accepted";
        }
        finally { mutex.Release(); }
    }

    /// <summary>Persists the operator's explicit automatic-fallback switch without migrating or probing the bot.</summary>
    /// <param name="botId">Exact internal selected bot id.</param>
    /// <param name="enabled">Explicit desired switch, not a toggle inferred from stale UI.</param>
    /// <param name="actor">Telegram user id rechecked against current global administrators.</param>
    /// <param name="expectedControlRevision">Control revision shown in the bounded one-use UI session.</param>
    /// <param name="expectedTelegramBotId">Positive BotFather identity frozen by the displayed confirmation, checked again under the bot lock.</param>
    /// <param name="cancellationToken">Caller-owned persistence cancellation.</param>
    /// <returns>A fixed authorization/concurrency code or accepted/unchanged.</returns>
    /// <remarks>Health counters alone do not stale this confirmation. Logger notification sender selection does not reserve or modify this bot's migration policy.</remarks>
    public async Task<string> SetAutoFailoverAsync(string botId, bool enabled, long actor,
        long expectedControlRevision, long expectedTelegramBotId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var status = ValidateCommand(botId, actor, out var bot, out var identity);
        if (status != null) return status;
        var mutex = GetLock(bot.Id);
        if (!await mutex.WaitAsync(0, cancellationToken)) return "busy";
        try
        {
            status = ValidateCommand(botId, actor, out bot, out identity);
            if (status != null) return status;
            if (identity != expectedTelegramBotId || expectedTelegramBotId <= 0) return "stale";
            var state = await _store.GetOrCreateAsync(bot.Id, identity, cancellationToken);
            status = ValidateCommand(botId, actor, out bot, out identity);
            if (status != null) return status;
            if (identity != expectedTelegramBotId || state.TelegramBotId != identity) return "stale";
            if (state.ControlRevision != expectedControlRevision) return "stale";
            if (state.LastFailureCategory == "identity_alias_conflict") return "aliased";
            if (IsPending(state)) return "busy";
            if (state.AutoFailoverEnabled == enabled) return "unchanged";
            state.AutoFailoverEnabled = enabled;
            state.ControlRevision++;
            state.ActorTelegramUserId = actor;
            return await SaveAsync(state, "auto_failover_changed", null, cancellationToken) ? "accepted" : "stale";
        }
        finally { mutex.Release(); }
    }

    /// <summary>Reads bounded private history for the currently configured BotFather identity.</summary>
    /// <param name="botId">Exact internal registry bot id; no historical identity substitution.</param>
    /// <param name="cancellationToken">Caller persistence cancellation.</param>
    /// <returns>At most twenty private durable history entries; actor ids stay out of public telemetry.</returns>
    public async Task<IReadOnlyList<TelegramEndpointHistory>> GetHistoryAsync(string botId, CancellationToken cancellationToken)
    {
        var state = await GetStatusAsync(botId, cancellationToken);
        return await _store.ReadHistoryAsync(state.BotId, state.TelegramBotId, 20, cancellationToken);
    }

    /// <summary>Refreshes and returns the durable undelivered alert count for explicit notification guarantees.</summary>
    /// <param name="cancellationToken">Caller persistence cancellation.</param>
    /// <returns>The durable number of pending, uncertain or manual-review alert rows, never a delivery guarantee.</returns>
    public async Task<int> GetPendingAlertCountAsync(CancellationToken cancellationToken)
    {
        var count = await _store.CountPendingAlertsAsync(cancellationToken);
        Volatile.Write(ref _pendingAlerts, count);
        return count;
    }

    /// <summary>Gets the injected UTC clock's current instant.</summary>
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;
    /// <summary>Whether explicit trusted roots are configured and the existing host mapping is accessible and link-free.</summary>
    private bool HasLocalFileMapping => TelegramLocalFileMapper.IsReady(_options);

    /// <summary>Finds an exact registry id without its ordinary default-bot fallback.</summary>
    /// <param name="botId">Required internal id selected by an authorized caller.</param>
    /// <returns>The current exact bot or null, never the default bot.</returns>
    private BotInstanceConfig FindBot(string botId) => string.IsNullOrWhiteSpace(botId) ? null :
        _registry.Bots.FirstOrDefault(x => string.Equals(x.Id, botId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads only current configured identities, with one detached row per live registry bot.</summary>
    /// <param name="token">Caller-owned bounded local persistence cancellation.</param>
    /// <param name="enabledOnly">True for worker scans; false retains disabled but identity-bound startup/panel states.</param>
    /// <returns>Current exact-identity states; historical orphan rows are never accumulated or revived.</returns>
    /// <remarks>GetOrCreate supplies Cloud only for identities with no saved row, never for saved Local/uncertain state.
    /// PublishIfUnhydrated restores a new runtime identity once, without reopening an existing generation fence.</remarks>
    private async Task<IReadOnlyList<TelegramEndpointState>> ReadCurrentStatesAsync(CancellationToken token, bool enabledOnly = false)
    {
        var bots = _registry.Bots;
        var result = new List<TelegramEndpointState>(bots.Count);
        foreach (var bot in bots)
        {
            if (enabledOnly && !bot.Enabled) continue;
            var identity = TelegramBotTokenIdentity.ExtractBotId(bot.Token);
            if (identity is not > 0) continue;
            var state = await _store.GetOrCreateAsync(bot.Id, identity.Value, token);
            var current = FindBot(bot.Id);
            if (current != null && TelegramBotTokenIdentity.ExtractBotId(current.Token) == state.TelegramBotId)
            {
                _gate.PublishIfUnhydrated(state);
                result.Add(state);
            }
        }
        return result;
    }

    /// <summary>Rechecks current global authority and exact enabled bot identity.</summary>
    /// <param name="botId">Exact selected internal id.</param>
    /// <param name="actor">Telegram user id asserted by the authenticated update.</param>
    /// <param name="bot">Resolved bot configuration only if present.</param>
    /// <param name="identity">Positive configured BotFather id, or zero when unavailable.</param>
    /// <returns>Null for an authorized current bot, otherwise a fixed safe refusal code.</returns>
    private string ValidateCommand(string botId, long actor, out BotInstanceConfig bot, out long identity)
    {
        bot = FindBot(botId);
        identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token) ?? 0;
        if (actor <= 0 || _configuration.GetSection(nameof(AppConfig.AdminsUserIds)).Get<List<long>>()?.Contains(actor) != true)
            return "denied";
        if (!_options.Enabled) return "disabled";
        if (bot == null || !bot.Enabled || identity <= 0) return "unavailable";
        if (HasIdentityAlias(bot.Id, identity)) return "aliased";
        return null;
    }


    /// <summary>Retains a separately admitted owned Cloud identity so a shared Local outage cannot remove all private administration paths.</summary>
    /// <param name="identity">Positive BotFather identity about to leave Cloud; its aliases cannot serve as independent control.</param>
    /// <returns>True when another enabled non-assistant owned identity has hydrated active Cloud admission.</returns>
    /// <remarks>Uses only trusted current registry and gate metadata, never network I/O. Rechecked before irreversible logout; an unhydrated, fenced, Local or disabled host is not a recovery path. This is routing availability, not a promise of Cloud network health.</remarks>
    /// <example><code>if (!HasIndependentCloudControl(identity)) return "control_path_missing";</code></example>
    private bool HasIndependentCloudControl(long identity)
    {
        foreach (var candidate in _registry.Bots)
        {
            var other = TelegramBotTokenIdentity.ExtractBotId(candidate.Token);
            if (!candidate.Enabled || candidate.Type != BotInstanceTypes.Owned || candidate.IsSalesAssistant ||
                !other.HasValue || other.Value == identity) continue;
            try
            {
                var route = _gate.GetRoute(candidate.Id, other.Value);
                if (route.Available && route.Endpoint == TelegramEndpointType.Cloud) return true;
            }
            catch (BotTransportUnavailableException) { }
        }
        return false;
    }

    /// <summary>Detects another current token-bearing internal id sharing the same irreversible BotFather session.</summary>
    /// <param name="botId">Canonical internal id whose migration intent is being evaluated.</param>
    /// <param name="identity">Positive exact BotFather identity frozen by the operation.</param>
    /// <returns>True when another configured alias could issue a send or read-only probe, even while disabled.</returns>
    /// <remarks>Disabled owned aliases retain background delivery and disabled tenants retain capability reads; neither is isolated from identity-wide logout.</remarks>
    private bool HasIdentityAlias(string botId, long identity) => _registry.Bots.Any(x =>
        !string.Equals(x.Id, botId, StringComparison.OrdinalIgnoreCase) && TelegramBotTokenIdentity.ExtractBotId(x.Token) == identity);

    /// <summary>Gets the one serialization gate shared by this bot's health and operator workflows.</summary>
    /// <param name="botId">Canonical internal registry id; case variants share a lock.</param>
    /// <returns>An instance-owned semaphore, never disposed while operations are in flight.</returns>
    private SemaphoreSlim GetLock(string botId) => _botLocks.GetOrAdd(botId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>Commits one detached CAS transition and publishes only the committed generation.</summary>
    /// <param name="state">Detached transition for the same immutable internal/BotFather identity.</param>
    /// <param name="reason">Closed history reason, or null for health metadata only.</param>
    /// <param name="alert">Closed durable incident category, or null when no alert is needed.</param>
    /// <param name="token">Persistence cancellation token; mutation acknowledgement callers use an independent token.</param>
    /// <returns>True only after the transition and any alert committed. A conflict never authorizes external replay.</returns>
    private async Task<bool> SaveAsync(TelegramEndpointState state, string reason, string alert, CancellationToken token)
    {
        if (!await _store.TrySaveAsync(state, state.Revision, reason, alert, token)) return false;
        var bot = FindBot(state.BotId);
        if (bot != null && TelegramBotTokenIdentity.ExtractBotId(bot.Token) == state.TelegramBotId)
            _gate.Publish(state);
        if (reason != null)
        {
            var terminal = reason is "migration_succeeded" or "cloud_recovered" or "local_recovered" or
                "migration_failed" or "manual_intervention" or "logout_uncertain" or "logout_refused";
            Record(state, reason is "cloud_recovered" or "local_recovered" ? "telegram_endpoint_recovered" : "telegram_endpoint_migration",
                state.LastFailureCategory == null ? "success" : "failure", terminal: terminal);
        }
        return true;
    }

    /// <summary>Wakes hosted work without allocating a task or dropping durable operation intent.</summary>
    private void SignalWork()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Waits for a bounded periodic scan or a capacity-one operator wake signal.</summary>
    /// <param name="cancellationToken">Hosted worker shutdown token.</param>
    /// <returns>A task completing at the next scan; no callback or polling-handler lifetime is retained.</returns>
    internal async Task WaitForWorkAsync(CancellationToken cancellationToken) =>
        await _wake.WaitAsync(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds), cancellationToken);

    /// <summary>Emits only closed endpoint metadata through the existing nonblocking telemetry writer.</summary>
    /// <param name="state">Current detached bot state; actor and BotFather ids are deliberately not emitted.</param>
    /// <param name="family">Closed endpoint health/migration/outage/recovered event family.</param>
    /// <param name="outcome">Closed operation outcome.</param>
    /// <param name="durationMs">Optional monotonic health duration in milliseconds.</param>
    /// <param name="terminal">True only for the committed operation terminal event; its monotonic clock is consumed once.</param>
    private void Record(TelegramEndpointState state, string family, string outcome, double? durationMs = null, bool terminal = false)
    {
        if (_telemetry?.Enabled != true) return;
        double? operationDuration = null;
        if (terminal && _operationClocks.TryRemove(state.BotId, out var clock) && clock.OperationId == state.OperationId)
            operationDuration = _time.GetElapsedTime(clock.Started).TotalMilliseconds;
        _telemetry.TryRecord(new LatencyTelemetryEvent
        {
            TimestampUtc = UtcNow, EventType = family, BotId = state.BotId,
            EndpointType = state.EffectiveEndpoint == TelegramEndpointType.Local ? "local" : "cloud",
            EndpointGeneration = state.Generation, MigrationState = state.MigrationState.ToString(),
            Outcome = outcome, Category = state.LastFailureCategory,
            ConsecutiveFailures = state.ConsecutiveFailures,
            HealthCheckDurationMs = durationMs, FailoverTrigger = state.Trigger,
            FailoverDurationMs = operationDuration,
            CloudReuseRemainingMs = state.CloudReuseEligibleAtUtc.HasValue
                ? Math.Max(0, (state.CloudReuseEligibleAtUtc.Value - UtcNow).TotalMilliseconds) : null,
            // SQLite stores UTC wall-clock values but materializes DateTime.Kind as Unspecified; schema success timestamps remain explicit UTC.
            LastSuccessUtc = state.LastSuccessfulHealthAtUtc.HasValue
                ? DateTime.SpecifyKind(state.LastSuccessfulHealthAtUtc.Value, DateTimeKind.Utc) : null
        });
    }

    /// <summary>Releases only coordinator-owned protocol resources after hosted work has stopped.</summary>
    public void Dispose()
    {
        if (_ownsProtocol && _protocol is IDisposable disposable) disposable.Dispose();
    }
}
