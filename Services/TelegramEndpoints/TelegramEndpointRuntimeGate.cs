using Adminbot.Domain;
using System.Diagnostics;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Immutable endpoint generation selected for one exact BotFather identity.</summary>
/// <param name="BotId">Configured internal owned, tenant, or assistant bot identifier.</param>
/// <param name="TelegramBotId">Positive numeric BotFather identity extracted from the configured token; never a user or chat id.</param>
/// <param name="Endpoint">Trusted Cloud or Local endpoint, without its token-bearing URL.</param>
/// <param name="Generation">Positive durable endpoint epoch; retained clients cannot cross epochs.</param>
/// <param name="Available">Whether new handlers and ordinary API operations may start.</param>
/// <param name="MigrationState">Durable state explaining endpoint availability.</param>
public readonly record struct TelegramEndpointRoute(string BotId, long TelegramBotId, TelegramEndpointType Endpoint,
    long Generation, bool Available, TelegramEndpointMigrationState MigrationState);

/// <summary>Atomically fences endpoint requests and durable inbox claims while existing handlers drain.</summary>
/// <remarks>
/// Current entries belong to internal scheduler ids, while migration fences and drain counters belong to
/// numeric BotFather identities across every alias. Returning identities reattach outstanding exact leases
/// and reload saved authority before admitting new work. Retired metadata survives only while leased or
/// hydrating; idle obsolete fences remain durable-store authority. No database or network call occurs under the lock.
/// An admitted handler may finish its original endpoint generation after fencing, but unrelated background sends
/// and new claims cannot start. A new generation cannot activate until every same-identity handler and request ends.
/// </remarks>
public sealed class TelegramEndpointRuntimeGate
{
    /// <summary>Registry authority used to reject unknown, disabled, or identity-replaced bot transports.</summary>
    private readonly BotRegistry _registry;
    /// <summary>Optional durable authority; production resolves it through DI, while store-free fixtures retain lazy Cloud defaults.</summary>
    private readonly ITelegramEndpointStateStore _store;
    /// <summary>Short process-local metadata lock, never held across an await.</summary>
    private readonly object _sync = new();
    /// <summary>At most one currently registered identity entry per internal bot id.</summary>
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Shared session admission for numeric BotFather identities; idle unreferenced identities are removed.</summary>
    private readonly Dictionary<long, IdentityAdmission> _identities = new();
    /// <summary>Handler-owned endpoint epoch inherited only by operations executing inside that handler.</summary>
    private static readonly AsyncLocal<TelegramEndpointExecutionLease> Execution = new();
    /// <summary>Signals the scheduler after routing availability changes; no receiver is started by this event.</summary>
    public event Action AvailabilityChanged;

    /// <summary>Constructs the shared admission gate without reading databases or starting any Telegram session.</summary>
    /// <param name="registry">Required exact bot identity authority; customer-supplied ids must not populate the gate.</param>
    /// <param name="store">Production durable route authority; null is only for store-free explicit fixtures.</param>
    /// <exception cref="ArgumentNullException">The registry is absent.</exception>
    /// <remarks>Production unseen identities stay closed until hydration. Registry changes prune idle obsolete metadata; leased returning identities retain counters and reload authority. Startup publishes saved rows before hosted work.</remarks>
    public TelegramEndpointRuntimeGate(BotRegistry registry, ITelegramEndpointStateStore store = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store;
        _registry.AvailabilityChanged += OnRegistryAvailabilityChanged;
    }

    /// <summary>Loads the exact saved endpoint once when a registry identity becomes current, before any ordinary request.</summary>
    /// <param name="botId">Required canonical configured bot id, not a username or customer id.</param>
    /// <param name="identity">Expected positive BotFather id, checked before and after the asynchronous database boundary.</param>
    /// <param name="token">Caller-owned hydration cancellation; cancellation never opens an unverified route.</param>
    /// <returns>A task completing after safe current-identity publication; already hydrated requests perform no database I/O.</returns>
    /// <remarks>Concurrent operations in one registry incarnation share a hydration flight. Returning identities retain outstanding counters but start a fresh authority read; obsolete A→B→A flights cannot publish. No alias read reopens another alias's fence.</remarks>
    /// <exception cref="BotTransportUnavailableException">The current registry identity changes, disappears, or the hydration flight belongs to an obsolete incarnation.</exception>
    /// <exception cref="InvalidOperationException">Saved active authority would replace a route still owned by any same-identity operation.</exception>
    /// <example><code>await gate.HydrateAsync(bot.Id, expectedIdentity, cancellationToken);</code></example>
    public async Task HydrateAsync(string botId, long identity, CancellationToken token)
    {
        ValidateIdentity(botId, identity, requireEnabled: false);
        Entry entry;
        TaskCompletionSource flight;
        bool ownsFlight;
        long authorityVersion;
        lock (_sync)
        {
            entry = GetEntry(botId, identity);
            authorityVersion = entry.AuthorityVersion;
            if (entry.Hydrated) return;
            ownsFlight = entry.Hydration == null;
            flight = entry.Hydration ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ownsFlight) entry.HydrationFlights++;
        }
        if (ownsFlight)
        {
            try
            {
                var state = await _store.GetOrCreateAsync(botId, identity, token);
                PublishCore(state, onlyIfUnhydrated: true, entry, authorityVersion);
                flight.TrySetResult();
            }
            catch (Exception error) { flight.TrySetException(error); }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(entry.Hydration, flight)) entry.Hydration = null;
                    entry.HydrationFlights--;
                    Debug.Assert(entry.HydrationFlights >= 0);
                    PruneRetired(entry);
                }
            }
        }
        await flight.Task.WaitAsync(token);
        ValidateIdentity(botId, identity, requireEnabled: false);
    }

    /// <summary>Reads the current route without changing endpoint selection or issuing an API request.</summary>
    /// <param name="botId">Required canonical registry id, not a Telegram username.</param>
    /// <param name="identity">Positive BotFather numeric identity that must still match the configured token.</param>
    /// <returns>An immutable route; unavailable states preserve their real effective endpoint rather than guessing Cloud.</returns>
    /// <exception cref="BotTransportUnavailableException">The bot is missing, identity-replaced, or has an invalid token.</exception>
    /// <remarks>Any alias's migration fence closes availability without rerouting original admitted work. Disabled bots remain inspectable; handlers require enabled bots, while owned delivery and strictly read-only disabled-tenant probes preserve provider policy.</remarks>
    /// <example><code>var route = gate.GetRoute(bot.Id, TelegramBotTokenIdentity.ExtractBotId(bot.Token).Value);</code></example>
    public TelegramEndpointRoute GetRoute(string botId, long identity)
    {
        ValidateIdentity(botId, identity, requireEnabled: false);
        lock (_sync) return Route(GetEntry(botId, identity));
    }

    /// <summary>Checks whether new ordinary requests or durable update claims may start for an exact identity.</summary>
    /// <param name="botId">Required configured internal bot id.</param>
    /// <param name="identity">Positive BotFather id, never a customer or tenant database id.</param>
    /// <returns>False for disabled, missing, replaced, fenced, or non-active migration states; existing inbox work remains queued.</returns>
    /// <remarks>This is advisory; <see cref="TryAcquireExecution"/> makes the atomic pre-claim decision.</remarks>
    public bool IsAvailable(string botId, long identity)
    {
        try
        {
            ValidateIdentity(botId, identity, requireEnabled: true);
            lock (_sync) return Route(GetEntry(botId, identity)).Available;
        }
        catch (BotTransportUnavailableException) { return false; }
    }

    /// <summary>Publishes a committed state and wakes queued work when its exact identity becomes active.</summary>
    /// <param name="state">Required detached durable row for the currently configured BotFather identity; contains no token.</param>
    /// <remarks>
    /// The caller persists before publishing. Older revisions cannot reopen a fenced generation. Active states are
    /// Cloud, Local, CloudRecovered and pre-threshold LocalDegraded; all other states close every numeric-identity alias.
    /// Active publication clears only this entry's lifecycle fence, never another alias's fence. Active replacement
    /// routes require every same-identity operation drained. Restrictive rows close admission immediately but defer
    /// replacing an occupied original route until drained publication. Financial state and inbox receipts remain unchanged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An active replacement route still has admitted same-identity operations.</exception>
    /// <example><code>gate.Publish(await store.GetOrCreateAsync(bot.Id, identity, token));</code></example>
    public void Publish(TelegramEndpointState state)
        => PublishCore(state, onlyIfUnhydrated: false);

    /// <summary>Hydrates saved authority without reopening a lifecycle fence or ignoring a newer restrictive alias state.</summary>
    /// <param name="state">Detached exact-identity saved state read by startup, current inventory or a first request.</param>
    /// <remarks>Active ordinary reads never overwrite hydrated lifecycle state. Restrictive committed rows close every alias immediately; if original operations remain, their endpoint and epoch survive until a drained restrictive publication adopts the saved replacement.</remarks>
    public void PublishIfUnhydrated(TelegramEndpointState state) => PublishCore(state, onlyIfUnhydrated: true);

    /// <summary>Applies exact committed authority while preserving identity-wide lifecycle fence ownership.</summary>
    /// <param name="state">Required current identity row; never raw customer input.</param>
    /// <param name="onlyIfUnhydrated">True for ordinary reads, which may close but never reopen an existing lifecycle fence.</param>
    /// <param name="hydratingEntry">Original exact entry for an asynchronous hydration flight, or null for ordinary committed publication.</param>
    /// <param name="authorityVersion">Registry-incarnation counter captured before hydration I/O; stale A→B→A reads cannot publish after return.</param>
    /// <remarks>Notifications occur outside the lock. Real route replacement drains all same-identity work; an initial unknown saved epoch may load only without conflicting leased routes. Restrictive authority closes admission immediately while preserving an occupied original route. Returning identities reject obsolete hydration flights.</remarks>
    /// <exception cref="InvalidOperationException">An active different route is published before same-identity work drains.</exception>
    /// <exception cref="BotTransportUnavailableException">An asynchronous hydration belongs to an obsolete registry incarnation.</exception>
    private void PublishCore(TelegramEndpointState state, bool onlyIfUnhydrated, Entry hydratingEntry = null, long authorityVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateIdentity(state.BotId, state.TelegramBotId, requireEnabled: false);
        bool changed;
        lock (_sync)
        {
            var entry = GetEntry(state.BotId, state.TelegramBotId);
            if (hydratingEntry != null && (!ReferenceEquals(entry, hydratingEntry) || entry.AuthorityVersion != authorityVersion))
                throw new BotTransportUnavailableException("telegram_identity_changed");
            var active = IsActive(state.MigrationState);
            if (onlyIfUnhydrated && entry.Hydrated && active) return;
            if (state.Revision < entry.Revision || state.Generation < entry.Generation) return;
            var bindingChanged = entry.Generation != state.Generation || entry.Endpoint != state.EffectiveEndpoint;
            var deferBinding = bindingChanged && (entry.Admission.Handlers != 0 || entry.Admission.Requests != 0) &&
                (entry.Published || !onlyIfUnhydrated || HasLeasedBindingConflict(entry.Admission, state));
            if (deferBinding && active)
                throw new InvalidOperationException("An endpoint route cannot change before its identity operations drain.");
            var identityFenced = IsIdentityFenced(entry.Admission);
            var available = Route(entry).Available;
            changed = bindingChanged && !deferBinding;
            if (!deferBinding)
            {
                entry.Endpoint = state.EffectiveEndpoint;
                entry.Generation = state.Generation;
            }
            entry.Revision = state.Revision;
            entry.State = state.MigrationState;
            if (!onlyIfUnhydrated) entry.LifecycleFenced = false;
            entry.Fenced = !active || entry.LifecycleFenced;
            entry.DurableFenced = !active;
            entry.Published = true;
            entry.Hydrated = true;
            if (!onlyIfUnhydrated || !active) entry.Starting = false;
            changed |= available != Route(entry).Available;
            changed |= identityFenced != IsIdentityFenced(entry.Admission);
            SignalDrained(entry.Admission);
            if (!IsIdentityFenced(entry.Admission)) entry.Admission.Drained = null;
        }
        if (changed) AvailabilityChanged?.Invoke();
    }

    /// <summary>Rejects initial alias hydration that would admit a different route while another exact alias is still executing.</summary>
    /// <param name="admission">Shared validated numeric identity protected by the metadata lock.</param>
    /// <param name="state">Detached saved exact-alias authority whose endpoint and epoch are being loaded.</param>
    /// <returns>True when a retained handler or routed request pins an endpoint or epoch different from the saved row.</returns>
    /// <remarks>Pre-authority identity-only probes do not pin a route, so they can load initial Local authority; all probes remain counted for real migration drains.</remarks>
    private static bool HasLeasedBindingConflict(IdentityAdmission admission, TelegramEndpointState state)
    {
        foreach (var entry in admission.Entries)
            if ((entry.Handlers != 0 || entry.Requests != 0) &&
                (entry.Generation != state.Generation || entry.Endpoint != state.EffectiveEndpoint)) return true;
        return false;
    }

    /// <summary>Closes new handler, request and probe admissions for every alias sharing a BotFather session.</summary>
    /// <param name="botId">Required current internal bot id that owns this lifecycle fence.</param>
    /// <param name="identity">Current positive numeric BotFather identity; unrelated identities remain available.</param>
    /// <remarks>Disabled aliases and leased retired entries share the fence. Admitted handlers may finish their original epoch without replay.</remarks>
    public void Fence(string botId, long identity)
    {
        ValidateIdentity(botId, identity, requireEnabled: false);
        bool changed;
        lock (_sync)
        {
            var entry = GetEntry(botId, identity);
            changed = !IsIdentityFenced(entry.Admission);
            entry.LifecycleFenced = entry.Fenced = true;
            entry.Starting = false;
            EnsureDrainSignal(entry.Admission);
            SignalDrained(entry.Admission);
        }
        if (changed) AvailabilityChanged?.Invoke();
    }

    /// <summary>Waits for all fenced numeric-identity aliases, retired handlers, API calls and read-only probes to finish.</summary>
    /// <param name="botId">Required configured internal bot id whose receiver must already be cancelled by the lifecycle owner.</param>
    /// <param name="identity">Positive BotFather identity whose complete session must drain before logout.</param>
    /// <param name="timeout">Positive bounded drain budget; expiration leaves migration pending and never cancels or replays admitted work.</param>
    /// <param name="token">Host or migration cancellation, never a management callback's short foreground deadline.</param>
    /// <returns>A task completing only after every original same-identity operation ends.</returns>
    /// <exception cref="InvalidOperationException">The numeric identity has not been fenced.</exception>
    /// <exception cref="TimeoutException">Outstanding work did not drain within the budget.</exception>
    /// <exception cref="OperationCanceledException">The migration wait is cancelled.</exception>
    /// <remarks>No lock is held across the wait; independent identities and queued inbox claims remain untouched.</remarks>
    public async Task DrainAsync(string botId, long identity, TimeSpan timeout, CancellationToken token)
    {
        Task drained;
        lock (_sync)
        {
            var admission = GetEntry(botId, identity).Admission;
            if (!IsIdentityFenced(admission)) throw new InvalidOperationException("Endpoint admission must be fenced before draining.");
            if (admission.Handlers == 0 && admission.Requests == 0) return;
            EnsureDrainSignal(admission);
            drained = admission.Drained.Task;
        }
        await drained.WaitAsync(timeout, token);
    }

    /// <summary>Stages a completely drained identity's destination solely for strict receiver startup.</summary>
    /// <param name="botId">Exact configured internal bot id owning destination receiver startup.</param>
    /// <param name="identity">Positive current BotFather identity, including its disabled aliases and retired leases.</param>
    /// <param name="endpoint">Trusted destination enum; arbitrary endpoint URLs cannot be selected.</param>
    /// <param name="generation">Positive persisted destination epoch greater than or equal to the current epoch.</param>
    /// <exception cref="InvalidOperationException">Identity admission is not fenced, any same-identity operation remains, or the epoch would decrease.</exception>
    /// <remarks>Only this entry's pinned receiver may start; aliases and ordinary work remain fenced until owning committed publication.</remarks>
    public void PrepareActivation(string botId, long identity, TelegramEndpointType endpoint, long generation)
    {
        ValidateIdentity(botId, identity, requireEnabled: true);
        lock (_sync)
        {
            var entry = GetEntry(botId, identity);
            if (!IsIdentityFenced(entry.Admission) || entry.Admission.Handlers != 0 || entry.Admission.Requests != 0 || generation < entry.Generation)
                throw new InvalidOperationException("Destination activation requires a drained, nondecreasing endpoint epoch.");
            entry.Endpoint = endpoint;
            entry.Generation = generation;
            entry.LifecycleFenced = entry.Fenced = true;
            entry.Starting = true;
            entry.State = endpoint == TelegramEndpointType.Local ? TelegramEndpointMigrationState.SwitchingToLocal : TelegramEndpointMigrationState.SwitchingToCloud;
        }
    }

    /// <summary>Atomically admits one update before the scheduler persists its durable claim.</summary>
    /// <param name="botId">Canonical registry id from a durable ready lane head.</param>
    /// <param name="lease">Handler lease when admitted; null when the update must remain queued.</param>
    /// <returns>True only when a currently enabled identity has an available endpoint.</returns>
    /// <remarks>The scheduler owns disposal through final inbox persistence. Admission checks every numeric-identity alias atomically; enter the lease flow before executing the handler.</remarks>
    /// <example><code>if (!gate.TryAcquireExecution(head.BotId, out var lease)) continue;</code></example>
    public bool TryAcquireExecution(string botId, out TelegramEndpointExecutionLease lease)
    {
        lease = null;
        var bot = _registry.GetById(botId);
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) || !bot.Enabled || !identity.HasValue) return false;
        lock (_sync)
        {
            var entry = GetEntry(bot.Id, identity.Value);
            var route = Route(entry);
            if (!route.Available) return false;
            entry.Handlers++;
            entry.Admission.Handlers++;
            lease = new(this, entry, route);
            return true;
        }
    }

    /// <summary>Acquires a current or explicitly pinned API/download epoch while checking exact live registry identity.</summary>
    /// <param name="botId">Configured internal bot id.</param>
    /// <param name="identity">Positive BotFather id bound to the routing facade.</param>
    /// <param name="generation">Null for a reusable facade; a positive epoch for a retained receiver client.</param>
    /// <param name="receiver">Whether this is a controlled receiver request, permitted during staged strict startup.</param>
    /// <param name="allowDisabledTenantProbe">True only for a separately classified getMe/getChat/getChatAdministrators capability request; it never enables delivery.</param>
    /// <returns>An owned lease with its immutable route; dispose after the complete SDK operation or file copy.</returns>
    /// <exception cref="BotTransportUnavailableException">New admissions are fenced, the bot is disabled/replaced, or the pinned generation is obsolete.</exception>
    /// <remarks>Identity-wide fencing includes disabled aliases. Only an admitted handler for this exact entry may finish during draining; only the staged owner receiver may start. No request is retried.</remarks>
    internal RequestLease AcquireRequest(string botId, long identity, long? generation = null, bool receiver = false,
        bool allowDisabledTenantProbe = false)
    {
        ValidateIdentity(botId, identity, requireEnabled: receiver);
        var bot = _registry.GetById(botId);
        if (!bot.Enabled && string.Equals(bot.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase) && !allowDisabledTenantProbe)
            throw new BotTransportUnavailableException("disabled_tenant_delivery");
        lock (_sync)
        {
            var entry = GetEntry(botId, identity);
            if (generation.HasValue && generation.Value != entry.Generation)
                throw new BotTransportUnavailableException("obsolete_endpoint_generation");
            var handler = Execution.Value;
            var finishingHandler = handler?.IsActive == true && ReferenceEquals(handler.OwnedEntry, entry) && handler.Route.Generation == entry.Generation;
            if ((entry.Fenced || IsIdentityFenced(entry.Admission)) && !finishingHandler && !(receiver && entry.Starting))
                throw new BotTransportUnavailableException("endpoint_migration_pending");
            entry.Requests++;
            entry.Admission.Requests++;
            if (IsIdentityFenced(entry.Admission)) EnsureDrainSignal(entry.Admission);
            return new(this, entry, Route(entry));
        }
    }

    /// <summary>Counts a validated read-only token probe before any asynchronous registry or durable authority resolution.</summary>
    /// <param name="identity">Positive global BotFather id parsed from the already validated token; never an internal bot, user or chat id.</param>
    /// <returns>An owned admission proof held through authority resolution, getMe and final completion; dispose exactly once.</returns>
    /// <exception cref="BotTransportUnavailableException">The identity is invalid or any alias has fenced its Telegram session.</exception>
    /// <remarks>Unregistered probes cannot bypass migration drains. Admission neither selects an endpoint nor authorizes delivery; callers must still resolve durable endpoint authority.</remarks>
    /// <example><code>using var probe = gate.AcquireIdentityProbe(identity); await ResolveAndProbeAsync(probe, token);</code></example>
    internal IdentityProbeLease AcquireIdentityProbe(long identity)
    {
        if (identity <= 0) throw new BotTransportUnavailableException("telegram_identity_changed");
        lock (_sync)
        {
            var admission = GetIdentityAdmission(identity);
            if (IsIdentityFenced(admission)) throw new BotTransportUnavailableException("endpoint_migration_pending");
            admission.Requests++;
            return new(this, admission);
        }
    }

    /// <summary>Determines whether ordinary startup may create a receiver for a saved active endpoint.</summary>
    /// <param name="botId">Required configured internal bot id.</param>
    /// <returns>False for disabled identities, all fenced states, and staged destinations not yet accepted by the coordinator.</returns>
    /// <remarks>Migration startup uses its lifecycle lease and a pinned receiver transport rather than this advisory check.</remarks>
    public bool CanReceive(string botId)
    {
        var bot = _registry.GetById(botId);
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        return identity.HasValue && bot != null && string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) && IsAvailable(botId, identity.Value);
    }

    /// <summary>Resolves the current exact entry, reattaching retained leases instead of forgetting returning identity counters.</summary>
    /// <param name="botId">Validated canonical internal runtime id.</param><param name="identity">Validated positive global BotFather id.</param>
    /// <returns>The current exact entry attached to its shared numeric-identity admission.</returns>
    /// <remarks>Caller holds the metadata lock. Returning production identities reload durable authority; idle obsolete aliases are pruned by exact live registry identity.</remarks>
    private Entry GetEntry(string botId, long identity)
    {
        if (_entries.TryGetValue(botId, out var entry) && entry.Identity == identity)
        {
            GetIdentityAdmission(identity);
            if (!entry.Current) throw new BotTransportUnavailableException("telegram_identity_changed");
            return entry;
        }
        if (entry != null)
        {
            PruneRetired(entry);
        }
        ValidateIdentity(botId, identity, requireEnabled: false);
        var admission = GetIdentityAdmission(identity);
        entry = null;
        foreach (var retained in admission.Entries)
        {
            if (!string.Equals(retained.BotId, botId, StringComparison.OrdinalIgnoreCase)) continue;
            entry = retained;
            if (_store != null)
            {
                entry.Hydrated = false;
                entry.Fenced = true;
            }
            break;
        }
        if (entry == null)
        {
            entry = new(botId, admission, _store == null);
            admission.Entries.Add(entry);
        }
        entry.Current = true;
        _entries[botId] = entry;
        return entry;
    }

    /// <summary>Gets bounded session metadata for an exact numeric identity without registering an internal bot alias.</summary>
    /// <param name="identity">Validated positive global BotFather numeric id.</param>
    /// <returns>The shared admission entry, created only while a current alias, fence, hydration or lease needs it.</returns>
    /// <remarks>Caller holds the metadata lock; this method performs no durable or Telegram I/O.</remarks>
    private IdentityAdmission GetIdentityAdmission(long identity)
    {
        if (!_identities.TryGetValue(identity, out var admission))
            _identities.Add(identity, admission = new(identity));
        else PruneObsoleteAliases(admission);
        return admission;
    }

    /// <summary>Evicts idle obsolete aliases immediately after registry identity changes rather than retaining churn until another request.</summary>
    /// <remarks>Registry notifications run outside its lock. Only short metadata work occurs here; admitted leases retain original ownership and durable fences remain in storage.</remarks>
    private void OnRegistryAvailabilityChanged()
    {
        lock (_sync)
        {
            foreach (var admission in _identities.Values)
            {
                PruneObsoleteAliases(admission);
                PruneIdentity(admission);
            }
        }
        AvailabilityChanged?.Invoke();
    }

    /// <summary>Prunes only one numeric identity's obsolete aliases without scanning historical identities on request hot paths.</summary>
    /// <param name="admission">Shared identity protected by the metadata lock.</param>
    /// <remarks>Reverse traversal permits allocation-free removals. Fences needed by any outstanding same-identity operation remain until that session drains.</remarks>
    private void PruneObsoleteAliases(IdentityAdmission admission)
    {
        for (var index = admission.Entries.Count - 1; index >= 0; index--)
            PruneRetired(admission.Entries[index], pruneIdentity: false);
    }

    /// <summary>Removes an obsolete exact alias once neither a lease nor hydration requires its process-local metadata.</summary>
    /// <param name="entry">Exact entry protected by the metadata lock; still-current identities retain their fences.</param>
    /// <param name="pruneIdentity">False while refreshing an identity immediately before attaching another alias or probe.</param>
    /// <remarks>Obsolete fences survive any outstanding session lease, including identity-only probes; once idle they remain durable authority, not permanent churn records.</remarks>
    private void PruneRetired(Entry entry, bool pruneIdentity = true)
    {
        if (entry.Current)
        {
            var bot = _registry.GetById(entry.BotId);
            if (bot != null && string.Equals(bot.Id, entry.BotId, StringComparison.OrdinalIgnoreCase) &&
                TelegramBotTokenIdentity.ExtractBotId(bot.Token) == entry.Identity) return;
            entry.Current = false;
            entry.AuthorityVersion++;
            entry.Hydration = null;
            if (_entries.TryGetValue(entry.BotId, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.BotId);
        }
        if (entry.Handlers != 0 || entry.Requests != 0 || entry.HydrationFlights != 0 ||
            (entry.LifecycleFenced || entry.DurableFenced) && (entry.Admission.Handlers != 0 || entry.Admission.Requests != 0)) return;
        entry.Admission.Entries.Remove(entry);
        if (pruneIdentity) PruneIdentity(entry.Admission);
    }

    /// <summary>Removes numeric session metadata after its last alias and counted probe are gone.</summary>
    /// <param name="admission">Shared identity protected by the metadata lock.</param>
    /// <remarks>Active or fenced entries remain referenced; unregistered completed probes leave no permanent metadata.</remarks>
    private void PruneIdentity(IdentityAdmission admission)
    {
        if (admission.Entries.Count == 0 && admission.Handlers == 0 && admission.Requests == 0)
            _identities.Remove(admission.Identity);
    }

    /// <summary>Rejects fallback registry lookups and identity/token replacement before any API use.</summary>
    /// <param name="botId">Exact internal configured id.</param><param name="identity">Expected positive BotFather id.</param>
    /// <param name="requireEnabled">True for receiver/handler admission; false for metadata, fencing and legacy owned-background/read-only-probe policy.</param>
    /// <exception cref="BotTransportUnavailableException">Configuration no longer identifies the expected usable bot.</exception>
    /// <remarks>No token or configured URL enters the exception text.</remarks>
    private void ValidateIdentity(string botId, long identity, bool requireEnabled)
    {
        var bot = _registry.GetById(botId);
        if (string.IsNullOrWhiteSpace(botId) || bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase))
            throw new BotTransportUnavailableException("unknown_bot");
        if (identity <= 0 || TelegramBotTokenIdentity.ExtractBotId(bot.Token) != identity)
            throw new BotTransportUnavailableException("telegram_identity_changed");
        if (requireEnabled && !bot.Enabled) throw new BotTransportUnavailableException("disabled_bot");
    }

    /// <summary>Projects exact routing metadata with admission closed by any same-identity lifecycle or durable fence.</summary>
    /// <param name="entry">Exact entry protected by this gate's metadata lock.</param><returns>Immutable route without counters, locks or token.</returns>
    /// <remarks>Unhydrated aliases block their own requests but do not fabricate a session-wide durable migration barrier.</remarks>
    private static TelegramEndpointRoute Route(Entry entry) => new(entry.BotId, entry.Identity, entry.Endpoint, entry.Generation,
        !entry.Fenced && !IsIdentityFenced(entry.Admission), entry.State);
    /// <summary>Defines migration states that may admit new work, including pre-outage transient degradation.</summary>
    /// <param name="state">Persisted closed migration vocabulary.</param><returns>True only for active usable states.</returns>
    private static bool IsActive(TelegramEndpointMigrationState state) => state is TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.Local
        or TelegramEndpointMigrationState.CloudRecovered or TelegramEndpointMigrationState.LocalDegraded;
    /// <summary>Checks actual session fences without treating an unhydrated alias placeholder as durable authority.</summary>
    /// <param name="admission">Shared identity protected by the metadata lock.</param>
    /// <returns>True when any current or retained exact entry owns a lifecycle or restrictive saved-state fence.</returns>
    /// <remarks>Active publication and ordinary hydration cannot clear another alias's fence.</remarks>
    private static bool IsIdentityFenced(IdentityAdmission admission)
    {
        foreach (var entry in admission.Entries)
            if (entry.LifecycleFenced || entry.DurableFenced) return true;
        return false;
    }

    /// <summary>Ensures a fresh waiter when admitted startup work follows an earlier completed drain.</summary>
    /// <param name="admission">Shared fenced identity protected by the metadata lock.</param>
    /// <remarks>Strict receiver startup can add requests after an earlier drain; an already completed signal must not hide those requests.</remarks>
    private static void EnsureDrainSignal(IdentityAdmission admission)
    {
        if (admission.Drained == null || admission.Drained.Task.IsCompleted)
            admission.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes the bounded session drain after the last same-identity operation ends.</summary>
    /// <param name="admission">Shared identity protected by the metadata lock.</param><remarks>Continuations run asynchronously outside the lock.</remarks>
    private static void SignalDrained(IdentityAdmission admission)
    {
        if (IsIdentityFenced(admission) && admission.Handlers == 0 && admission.Requests == 0)
            admission.Drained?.TrySetResult();
    }

    /// <summary>Releases an exact handler or request and its shared identity count exactly once.</summary>
    /// <param name="entry">Original exact entry retained by the lease.</param><param name="handler">True for a whole handler; false for one API request or download.</param>
    /// <remarks>Replacement and returning identities cannot release or forget another exact lease; idle retired metadata is removed after completion.</remarks>
    private void Release(Entry entry, bool handler)
    {
        lock (_sync)
        {
            if (handler) { entry.Handlers--; entry.Admission.Handlers--; }
            else { entry.Requests--; entry.Admission.Requests--; }
            Debug.Assert(entry.Handlers >= 0 && entry.Requests >= 0 && entry.Admission.Handlers >= 0 && entry.Admission.Requests >= 0);
            SignalDrained(entry.Admission);
            PruneObsoleteAliases(entry.Admission);
            PruneIdentity(entry.Admission);
        }
    }

    /// <summary>Releases one identity-only probe after authority and SDK completion.</summary>
    /// <param name="admission">Original counted global BotFather admission.</param>
    /// <remarks>Disposal never cancels, reroutes or repeats the probe and removes unregistered idle metadata.</remarks>
    private void ReleaseProbe(IdentityAdmission admission)
    {
        lock (_sync)
        {
            admission.Requests--;
            Debug.Assert(admission.Requests >= 0);
            SignalDrained(admission);
            PruneObsoleteAliases(admission);
            PruneIdentity(admission);
        }
    }

    /// <summary>Session-wide metadata shared by all internal aliases and unregistered probes of one BotFather identity.</summary>
    internal sealed class IdentityAdmission
    {
        /// <summary>Global positive BotFather id, never an internal scheduler id.</summary>
        internal readonly long Identity;
        /// <summary>Current aliases plus exact retired entries still leased or hydrating; idle obsolete fences remain durable authority only.</summary>
        internal readonly List<Entry> Entries = new();
        /// <summary>All admitted same-identity handlers and requests, including identity-only probes.</summary>
        internal int Handlers, Requests;
        /// <summary>At most one current identity-wide drain waiter; startup admissions refresh completed waiters.</summary>
        internal TaskCompletionSource Drained;
        /// <summary>Constructs empty session metadata before its first alias or probe is attached.</summary>
        /// <param name="identity">Validated positive global BotFather numeric id.</param>
        internal IdentityAdmission(long identity) => Identity = identity;
    }

    /// <summary>Exact alias metadata retained only while current, leased or hydrating; obsolete idle saved fences remain in durable storage.</summary>
    internal sealed class Entry
    {
        /// <summary>Canonical registry id; no customer identifier.</summary>
        internal readonly string BotId;
        /// <summary>Exact configured BotFather numeric identity.</summary>
        internal readonly long Identity;
        /// <summary>Session-wide admission and drain owner shared with every alias and token probe.</summary>
        internal readonly IdentityAdmission Admission;
        /// <summary>Whether this entry is the current observed registry identity for its internal id.</summary>
        internal bool Current;
        /// <summary>Current effective or staged endpoint.</summary>
        internal TelegramEndpointType Endpoint;
        /// <summary>Positive endpoint epoch and last published optimistic state revision.</summary>
        internal long Generation = 1, Revision;
        /// <summary>Closed durable migration state retained while an exact leased identity rehydrates.</summary>
        internal TelegramEndpointMigrationState State = TelegramEndpointMigrationState.Cloud;
        /// <summary>Local closure, lifecycle and restrictive saved-state ownership, and strict staged receiver permission.</summary>
        internal bool Fenced, LifecycleFenced, DurableFenced, Starting;
        /// <summary>Exact-entry counts retained independently of the shared session counters.</summary>
        internal int Handlers, Requests;
        /// <summary>Whether current exact durable authority has been loaded; returning production identities reset this flag.</summary>
        internal bool Hydrated;
        /// <summary>Whether this entry has ever represented real authority; a new placeholder's epoch is not a session transition.</summary>
        internal bool Published;
        /// <summary>Current registry incarnation's shared hydration flight; retired flights cannot refill a returning identity.</summary>
        internal TaskCompletionSource Hydration;
        /// <summary>All outstanding current or retired hydration reads retained until completion.</summary>
        internal int HydrationFlights;
        /// <summary>Process-local registry-incarnation epoch, incremented on retirement independently of the durable endpoint generation.</summary>
        internal long AuthorityVersion;
        /// <summary>Attaches an exact alias to its numeric session; only explicit store-free fixtures default to Cloud.</summary>
        /// <param name="botId">Validated canonical internal runtime id.</param>
        /// <param name="admission">Shared validated positive BotFather session admission.</param>
        /// <param name="hydrated">True only for store-free fixtures; production must load saved exact authority.</param>
        internal Entry(string botId, IdentityAdmission admission, bool hydrated)
        {
            BotId = botId;
            Admission = admission;
            Identity = admission.Identity;
            Hydrated = hydrated;
            Published = hydrated;
            Fenced = !hydrated;
            if (!hydrated) State = TelegramEndpointMigrationState.ManualInterventionRequired;
        }
    }

    /// <summary>Counted proof that an identity-only read probe was admitted before authority resolution or migration fencing.</summary>
    internal sealed class IdentityProbeLease : IDisposable
    {
        /// <summary>Original gate released once after the complete read-only operation.</summary>
        private TelegramEndpointRuntimeGate _owner;
        /// <summary>Exact shared identity counted even before any internal alias is registered.</summary>
        private readonly IdentityAdmission _admission;
        /// <summary>Constructs already-counted admission; only the owning gate can authorize a probe.</summary>
        /// <param name="owner">Shared owning runtime gate.</param><param name="admission">Already-counted positive BotFather identity.</param>
        internal IdentityProbeLease(TelegramEndpointRuntimeGate owner, IdentityAdmission admission) { _owner = owner; _admission = admission; }
        /// <summary>Checks a provider's proof without granting another admission or bypassing a disposed lease.</summary>
        /// <param name="gate">Expected shared runtime gate instance.</param><param name="identity">Validated global BotFather id being probed.</param>
        /// <returns>True only for this gate's still-owned exact identity admission.</returns>
        /// <remarks>A fence arriving after admission does not invalidate completion permission; disposal does.</remarks>
        internal bool IsActiveFor(TelegramEndpointRuntimeGate gate, long identity)
            => ReferenceEquals(Volatile.Read(ref _owner), gate) && _admission.Identity == identity;
        /// <summary>Releases the counted probe exactly once and may complete a waiting migration drain.</summary>
        /// <remarks>No database or HTTP operation is performed.</remarks>
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseProbe(_admission);
    }

    /// <summary>Exactly-once disposal of one complete SDK operation or file transfer.</summary>
    internal sealed class RequestLease : IDisposable
    {
        /// <summary>Original gate released atomically once.</summary>
        private TelegramEndpointRuntimeGate _owner;
        /// <summary>Original identity entry kept alive through the operation.</summary>
        private readonly Entry _entry;
        /// <summary>Immutable epoch selected under the admission lock.</summary>
        internal TelegramEndpointRoute Route { get; }
        /// <summary>Creates an already-counted lease; callers cannot manufacture one without admission.</summary>
        /// <param name="owner">Owning metadata gate.</param><param name="entry">Counted identity entry.</param><param name="route">Snapshot selected atomically.</param>
        internal RequestLease(TelegramEndpointRuntimeGate owner, Entry entry, TelegramEndpointRoute route) { _owner = owner; _entry = entry; Route = route; }
        /// <summary>Releases one request count; repeated disposal has no effect.</summary>
        /// <remarks>Disposal performs no I/O and may complete a waiting migration drain.</remarks>
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_entry, handler: false);
    }

    /// <summary>One scheduler admission held from before durable claim until final inbox persistence completes.</summary>
    public sealed class TelegramEndpointExecutionLease : IDisposable
    {
        /// <summary>Original metadata gate released exactly once.</summary>
        private TelegramEndpointRuntimeGate _owner;
        /// <summary>Original counted identity entry.</summary>
        internal Entry OwnedEntry { get; }
        /// <summary>Immutable bot identity and endpoint epoch selected before claiming an update.</summary>
        public TelegramEndpointRoute Route { get; }
        /// <summary>Whether this handler still owns permission to finish its original epoch.</summary>
        internal bool IsActive => Volatile.Read(ref _owner) != null;
        /// <summary>Creates an already-counted handler lease under the metadata lock.</summary>
        /// <param name="owner">Owning gate.</param><param name="entry">Counted identity entry.</param><param name="route">Selected endpoint epoch.</param>
        internal TelegramEndpointExecutionLease(TelegramEndpointRuntimeGate owner, Entry entry, TelegramEndpointRoute route) { _owner = owner; OwnedEntry = entry; Route = route; }
        /// <summary>Enters this handler's endpoint epoch in its isolated async execution flow.</summary>
        /// <returns>A scope that restores the enclosing flow on disposal; it does not itself release handler admission.</returns>
        /// <remarks>Only the scheduler's claimed handler may inherit this permission. Never enter around a polling loop or migration worker.</remarks>
        /// <example><code>using var flow = executionLease.Enter(); await executor.ExecuteAsync(item, token);</code></example>
        public IDisposable Enter() => new ExecutionFlow(this);
        /// <summary>Releases whole-handler admission exactly once after final persistence.</summary>
        /// <remarks>Outstanding request leases remain counted independently.</remarks>
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(OwnedEntry, handler: true);
    }

    /// <summary>Restores the preceding handler epoch when an isolated execution flow leaves its scope.</summary>
    private sealed class ExecutionFlow : IDisposable
    {
        /// <summary>Enclosing flow restored without sharing customer state between bots.</summary>
        private readonly TelegramEndpointExecutionLease _previous;
        /// <summary>Installs one scheduler-owned handler's routing permission.</summary>
        /// <param name="lease">Active admitted handler lease.</param>
        internal ExecutionFlow(TelegramEndpointExecutionLease lease) { _previous = Execution.Value; Execution.Value = lease; }
        /// <summary>Restores the enclosing routing flow; no I/O occurs.</summary>
        public void Dispose() => Execution.Value = _previous;
    }
}

/// <summary>Serializes endpoint migration with the existing per-bot start/stop/recovery lifecycle gate.</summary>
public interface ITelegramEndpointReceiverLifecycle
{
    /// <summary>Acquires the existing runtime lifecycle semaphore for exactly one bot.</summary>
    /// <param name="botId">Configured internal bot id; unknown ids are rejected.</param>
    /// <param name="token">Host/migration cancellation while waiting for the gate.</param>
    /// <returns>An owned asynchronous lease; dispose after destination validation/state publication.</returns>
    /// <remarks>No other bot or the shared Local API container is stopped.</remarks>
    Task<ITelegramEndpointReceiverLease> AcquireAsync(string botId, CancellationToken token);
}

/// <summary>Bot-specific receiver control while the caller holds its existing lifecycle semaphore.</summary>
public interface ITelegramEndpointReceiverLease : IAsyncDisposable
{
    /// <summary>Cancels and joins this bot's registered polling generation without dropping durable inbox updates.</summary>
    /// <param name="token">Bounded migration/host wait cancellation.</param><returns>A task completing only after the original receiver exits.</returns>
    /// <remarks>Cancellation does not replay accepted Telegram sends or discard pending inbox work.</remarks>
    Task StopAndWaitAsync(CancellationToken token);
    /// <summary>Authenticates the staged destination identity and validates a receiving path before starting exactly one receiver.</summary>
    /// <param name="token">Bounded migration/host cancellation.</param><returns>True only for strict validated startup, never optimistic getMe recovery.</returns>
    /// <remarks>The coordinator stages a drained generation first and publishes effective state only after this succeeds.</remarks>
    Task<bool> StartValidatedAsync(CancellationToken token);
}
