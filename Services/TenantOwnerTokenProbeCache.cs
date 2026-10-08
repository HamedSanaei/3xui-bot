using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

/// <summary>Reads Telegram bot identity without owning panel state, caching, or failure classification.</summary>
/// <remarks>The production transport disables SDK retries; callers own cancellation and the applicable foreground or startup budget.</remarks>
public interface ITelegramTokenProbe
{
    /// <summary>Queries getMe for a single BotFather token without logging or persisting the secret.</summary>
    /// <param name="token">Required trimmed BotFather secret; never use it as a diagnostic or cache key.</param>
    /// <param name="cancellationToken">Caller-owned cancellation including the budget for this particular probe.</param>
    /// <returns>The Telegram bot identity, for internal synchronization only.</returns>
    /// <remarks>Does not change a storefront or retry failed Telegram requests. Production counts a numeric-identity lease before reading durable authority and retains it through the complete SDK await and final authority check, so registration cannot bypass migration draining. Registered routes include disabled pre-enable tenants; historical aliases must permit any unregistered Cloud login.</remarks>
    /// <exception cref="OperationCanceledException">The caller's probe scope was canceled.</exception>
    /// <exception cref="ApiRequestException">Telegram rejects getMe; callers must distinguish authoritative token rejection from transient API failures.</exception>
    /// <exception cref="ArgumentException">The token has no valid positive BotFather identity, or the SDK rejects its format before contacting Telegram.</exception>
    /// <exception cref="BotTransportUnavailableException">Identity authority is incomplete, migration is fenced, or saved Local/session history prevents safe Cloud reuse.</exception>
    /// <example><code>var identity = await probe.GetMeAsync(savedToken, probeCancellation.Token);</code></example>
    Task<Telegram.Bot.Types.User> GetMeAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Allows cached identity results to revalidate endpoint authority without an identity-bearing Telegram request.</summary>
public interface ITelegramTokenProbeAuthority
{
    /// <summary>Checks durable identity aliases and current endpoint admission, returning a secret-free cache epoch.</summary>
    /// <param name="token">Supplied secret, used privately for exact identity and rotation checks.</param>
    /// <param name="cancellationToken">Caller-owned complete foreground or registration budget.</param>
    /// <returns>A secret-free fingerprint of current registry token and endpoint generation.</returns>
    /// <remarks>A short counted numeric-identity lease precedes durable reads, including unregistered tokens. A cached success is usable only while this authority epoch remains equal; a pre-existing migration fence rejects authority before any Telegram request.</remarks>
    /// <exception cref="BotTransportUnavailableException">Durable authority is missing, incomplete, suspect, or fenced.</exception>
    /// <example><code>var epoch = await authority.GetAuthorityAsync(savedToken, cancellationToken);</code></example>
    Task<string> GetAuthorityAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Non-retrying identity transport that preserves exact registered routes and rejects unsafe historical Cloud login.</summary>
/// <remarks>No probe logs or persists a secret; cache keys use fingerprints. Numeric-identity admission precedes durable authority reads and remains counted through SDK completion. Cloud reads require complete durable alias authority, and every SDK transport shares the provider socket pool.</remarks>
public sealed class TelegramTokenProbe : ITelegramTokenProbe, ITelegramTokenProbeAuthority
{
    /// <summary>Current exact registered identity authority, including disabled pre-enable tenants.</summary>
    private readonly BotRegistry _registry;
    /// <summary>Read-only routed transport provider; control clients are never used here.</summary>
    private readonly BotClientProvider _clients;
    /// <summary>Durable cross-alias session authority, including removed registry identities.</summary>
    private readonly ITelegramEndpointStateStore _store;
    /// <summary>Current per-identity migration admission and generation authority.</summary>
    private readonly TelegramEndpointRuntimeGate _gate;

    /// <summary>Creates a fail-closed directly constructed probe when durable endpoint authority is unavailable.</summary>
    /// <remarks>Legacy direct construction is safe: it cannot assume an unknown token has never used Local.</remarks>
    public TelegramTokenProbe() { }

    /// <summary>Creates the production singleton with complete registry, persistence, and routed transport authority.</summary>
    /// <param name="registry">Current exact bot identities, including disabled tenants.</param>
    /// <param name="clients">Provider of pooled read-only capability transports.</param>
    /// <param name="store">Durable identity routes across all internal aliases.</param>
    /// <param name="gate">Shared migration fences and hydrated route generations.</param>
    /// <remarks>No network operation runs during construction; caller-owned budgets cover all durable authority reads and HTTP. Every SDK probe reuses the provider socket pool.</remarks>
    /// <exception cref="ArgumentNullException">An authority dependency is missing.</exception>
    /// <exception cref="BotTransportUnavailableException">The provider is not bound to the same migration gate, so it cannot guarantee the selected route.</exception>
    /// <example><code>var probe = new TelegramTokenProbe(registry, clients, store, gate);</code></example>
    public TelegramTokenProbe(BotRegistry registry, BotClientProvider clients, ITelegramEndpointStateStore store,
        TelegramEndpointRuntimeGate gate)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        if (!ReferenceEquals(_clients.EndpointGate, _gate))
            throw new BotTransportUnavailableException("endpoint_identity_authority_unavailable");
    }

    /// <inheritdoc />
    public async Task<Telegram.Bot.Types.User> GetMeAsync(string token, CancellationToken cancellationToken)
    {
        using var admission = AcquireIdentityProbe(token, cancellationToken, out var expectedIdentity);
        var before = await ResolveAsync(token, expectedIdentity, cancellationToken);
        var identity = before.BotId == null
            ? await _clients.ProbeUnregisteredTokenAsync(token, admission, cancellationToken)
            : await _clients.GetClientForCapabilityProbe(before.BotId, token, before.Identity).GetMe(cancellationToken);
        if (identity.Id != before.Identity)
            throw new BotTransportUnavailableException("telegram_identity_changed");
        var after = await ResolveAsync(token, expectedIdentity, cancellationToken);
        if (!string.Equals(before.Epoch, after.Epoch, StringComparison.Ordinal))
            throw new BotTransportUnavailableException("endpoint_migration_pending");
        return identity;
    }

    /// <inheritdoc />
    public async Task<string> GetAuthorityAsync(string token, CancellationToken cancellationToken)
    {
        using var admission = AcquireIdentityProbe(token, cancellationToken, out var identity);
        return (await ResolveAsync(token, identity, cancellationToken)).Epoch;
    }

    /// <summary>Counts a probe before asynchronous authority reads, even when its BotFather identity has no registered alias yet.</summary>
    /// <param name="token">Required privately supplied BotFather secret; only its positive numeric identity enters the gate.</param>
    /// <param name="cancellationToken">Complete caller-owned probe budget, checked before admission.</param>
    /// <param name="identity">The validated numeric BotFather identity, never an internal tenant, chat, or customer id.</param>
    /// <returns>An owned identity-only lease; dispose after all authority checks and the complete SDK operation.</returns>
    /// <remarks>Missing durable authority fails closed. A concurrent registration shares this count, so its migration cannot log out until this lease ends.</remarks>
    /// <exception cref="ArgumentException">The supplied secret does not contain a valid positive BotFather identity.</exception>
    /// <exception cref="OperationCanceledException">The caller's probe scope is already canceled.</exception>
    /// <exception cref="BotTransportUnavailableException">Durable authority is unavailable or migration has already fenced the numeric identity.</exception>
    /// <example><code>using var admission = AcquireIdentityProbe(token, cancellationToken, out var identity);</code></example>
    private TelegramEndpointRuntimeGate.IdentityProbeLease AcquireIdentityProbe(
        string token, CancellationToken cancellationToken, out long identity)
    {
        cancellationToken.ThrowIfCancellationRequested();
        identity = TelegramBotTokenIdentity.ExtractBotId(token)
            ?? throw new ArgumentException("Invalid bot token format.", nameof(token));
        if (_store == null || _gate == null)
            throw new BotTransportUnavailableException("endpoint_identity_authority_unavailable");
        return _gate.AcquireIdentityProbe(identity);
    }

    /// <summary>Resolves complete bounded durable identity authority before either Cloud login or a cached result is admitted.</summary>
    /// <param name="token">Supplied BotFather secret; replacement secrets must retain the registered numeric identity.</param>
    /// <param name="identity">Positive numeric BotFather identity extracted from the supplied secret before acquiring its identity-only lease.</param>
    /// <param name="cancellationToken">Complete caller probe budget, unchanged across durable authority and SDK operations.</param>
    /// <returns>Exact canonical registry route or an eligible unregistered Cloud route, plus a secret-free epoch.</returns>
    /// <remarks>The caller holds numeric-identity admission across this read and any SDK operation. Local/session uncertainty under any alias denies Cloud even after internal rename or removal; a final read rejects results whose registration, token, or endpoint epoch changed.</remarks>
    /// <exception cref="BotTransportUnavailableException">Authority is unavailable, Cloud reuse is unsafe, or migration is fenced.</exception>
    /// <example><code>var authority = await ResolveAsync(savedToken, admittedIdentity, cancellationToken);</code></example>
    private async Task<(string BotId, long Identity, string Epoch)> ResolveAsync(
        string token, long identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var aliases = await _store.ReadIdentityStatesAsync(identity, cancellationToken);
        if (aliases.Count > 128)
            throw new BotTransportUnavailableException("endpoint_identity_authority_overflow");
        BotInstanceConfig bot = null;
        var registered = _registry.Bots;
        for (var index = 0; index < registered.Count; index++)
        {
            var candidate = registered[index];
            if (TelegramBotTokenIdentity.ExtractBotId(candidate.Token) != identity) continue;
            bot ??= candidate;
            if (string.Equals(candidate.Token, token, StringComparison.Ordinal))
            {
                bot = candidate;
                break;
            }
        }
        TelegramEndpointRoute? route = null;
        if (bot != null)
        {
            await _gate.HydrateAsync(bot.Id, identity, cancellationToken);
            route = _gate.GetRoute(bot.Id, identity);
            if (!route.Value.Available)
                throw new BotTransportUnavailableException("endpoint_migration_pending");
        }
        var endpoint = route?.Endpoint ?? TelegramEndpointType.Cloud;
        foreach (var state in aliases)
        {
            var active = state.MigrationState is TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered
                or TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded;
            if (!active || state.TelegramBotId != identity || state.LogoutAttemptedAtUtc.HasValue &&
                (!state.LogoutAcknowledgedAtUtc.HasValue || state.LogoutAcknowledgedAtUtc < state.LogoutAttemptedAtUtc))
                throw new BotTransportUnavailableException("endpoint_migration_pending");
            if (endpoint == TelegramEndpointType.Cloud &&
                (state.EffectiveEndpoint != TelegramEndpointType.Cloud ||
                 state.MigrationState is not (TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered) ||
                 state.CloudReuseEligibleAtUtc > DateTime.UtcNow))
                throw new BotTransportUnavailableException("endpoint_cloud_reuse_restricted");
        }
        var epoch = bot == null ? $"unregistered:{identity}" :
            $"{bot.Id}:{identity}:{endpoint}:{route.Value.Generation}:{TenantOwnerTokenProbeCache.Fingerprint(bot.Token)}";
        return (bot?.Id, identity, epoch);
    }
}

/// <summary>Closed, secret-free outcomes of an owner-panel identity probe.</summary>
public enum TenantOwnerTokenProbeStatus
{
    /// <summary>Telegram returned a usable identity.</summary>
    Valid,
    /// <summary>Telegram or the SDK authoritatively rejected the token under the existing invalid-token rule.</summary>
    Invalid,
    /// <summary>A short budget, network failure, rate limit, or server failure prevented verification.</summary>
    Transient,
    /// <summary>An ambiguous failure prevented verification and must never authorize cleanup.</summary>
    Unavailable
}

/// <summary>Internal identity or secret-free failure classification; never a persistent token-health flag.</summary>
/// <param name="Status">Whether Telegram verified, rejected, or could not check the token.</param>
/// <param name="Identity">Telegram identity only for a valid result; null on failures.</param>
/// <remarks>Only the matching saved storefront identity may consume this result; the cache never changes users.db.</remarks>
public sealed record TenantOwnerTokenProbeResult(TenantOwnerTokenProbeStatus Status, Telegram.Bot.Types.User Identity = null);

/// <summary>Instance-owned bounded single-flight cache for short foreground owner-panel getMe probes.</summary>
/// <remarks>
/// Production registers one singleton across scoped handlers. Keys combine the internal storefront id, SHA-256
/// token fingerprint, and revalidated endpoint/token authority epoch, never raw secrets. Success lives three minutes and non-authoritative failures twenty seconds.
/// Invalid results are not retained. Eviction and explicit invalidation detach old flights, preventing late completion
/// from repopulating a replaced identity. Callers must still guard persisted identity/revision before applying results.
/// Canceling the final waiter removes and cancels its flight rather than caching handler shutdown as a transient failure.
/// </remarks>
public sealed class TenantOwnerTokenProbeCache
{
    /// <summary>Protects the bounded entry map and waiter ownership; never held across an await.</summary>
    private readonly object _sync = new();
    /// <summary>Bounded results and flights keyed by internal id, token fingerprint, and revalidated endpoint authority epoch.</summary>
    private readonly Dictionary<(string TenantId, string Fingerprint, string Authority), Entry> _entries = new();
    /// <summary>Non-retrying injectable getMe transport.</summary>
    private readonly ITelegramTokenProbe _probe;
    /// <summary>Dedicated foreground timeout, independent of the twelve-second startup configuration.</summary>
    private readonly TimeSpan _probeTimeout;
    /// <summary>Lifetime of verified identity results.</summary>
    private readonly TimeSpan _positiveLifetime;
    /// <summary>Short memory-only lifetime of non-authoritative failures.</summary>
    private readonly TimeSpan _transientLifetime;
    /// <summary>Clock for deterministic expiry and foreground-budget tests.</summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>Maximum retained entries, including flights.</summary>
    private readonly int _capacity;
    /// <summary>Monotonic access sequence used for bounded least-recently-used eviction.</summary>
    private long _accessSequence;

    /// <summary>Creates the shared cache with a two-second production foreground budget.</summary>
    /// <param name="probe">Required injectable getMe transport; production uses the non-retrying Telegram implementation.</param>
    /// <param name="probeTimeout">Optional positive foreground duration; defaults to two seconds, not the startup budget.</param>
    /// <param name="positiveLifetime">Optional positive success lifetime; defaults to three minutes.</param>
    /// <param name="transientLifetime">Optional positive failure lifetime; defaults to twenty seconds.</param>
    /// <param name="capacity">Maximum retained storefront/token entries; must be positive and defaults to 512.</param>
    /// <param name="timeProvider">Optional clock and budget timer provider; defaults to the system clock.</param>
    /// <remarks>Tests may supply millisecond budgets and a controlled expiry clock without contacting Telegram.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A duration or capacity is not positive.</exception>
    /// <exception cref="ArgumentNullException">The required probe transport is null.</exception>
    /// <example><code>var cache = new TenantOwnerTokenProbeCache(probe, probeTimeout: TimeSpan.FromMilliseconds(30));</code></example>
    public TenantOwnerTokenProbeCache(ITelegramTokenProbe probe, TimeSpan? probeTimeout = null,
        TimeSpan? positiveLifetime = null, TimeSpan? transientLifetime = null, int capacity = 512,
        TimeProvider timeProvider = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(2);
        _positiveLifetime = positiveLifetime ?? TimeSpan.FromMinutes(3);
        _transientLifetime = transientLifetime ?? TimeSpan.FromSeconds(20);
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_probeTimeout <= TimeSpan.Zero || _positiveLifetime <= TimeSpan.Zero || _transientLifetime <= TimeSpan.Zero || capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Probe durations and cache capacity must be positive.");
    }

    /// <summary>Shares one short identity check or returns a still-fresh memory-only result for this exact storefront/token.</summary>
    /// <param name="tenantBotId">Required internal database storefront id; not a Telegram bot or user id.</param>
    /// <param name="token">Required saved BotFather secret; hashed before dictionary lookup and never logged.</param>
    /// <param name="cancellationToken">This handler's cancellation; checked on cache hits and independently while waiting.</param>
    /// <returns>Verified Telegram identity or a non-authoritative/invalid classification; no persistent state is changed.</returns>
    /// <remarks>One canceled waiter cannot cancel another handler. Final-waiter departure cancels the flight.
    /// Production authority checks share the two-second foreground budget, including cache hits, and prevent a fenced or rotated endpoint from authorizing a retained success.</remarks>
    /// <exception cref="OperationCanceledException">The calling handler was canceled, including on a cache hit.</exception>
    /// <example><code>var result = await cache.ProbeAsync(store.Id, store.Token, updateCancellation);</code></example>
    public async Task<TenantOwnerTokenProbeResult> ProbeAsync(string tenantBotId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantBotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var authority = _probe as ITelegramTokenProbeAuthority;
        using var deadline = authority == null ? null : new CancellationTokenSource(_probeTimeout, _timeProvider);
        using var budget = deadline == null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var waitToken = budget?.Token ?? cancellationToken;
        string epoch;
        try
        {
            epoch = authority == null ? string.Empty : await authority.GetAuthorityAsync(token, waitToken).WaitAsync(waitToken);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(IsInvalid(exception) ? TenantOwnerTokenProbeStatus.Invalid :
                IsTransient(exception) ? TenantOwnerTokenProbeStatus.Transient : TenantOwnerTokenProbeStatus.Unavailable);
        }
        var key = (tenantBotId, Fingerprint(token), epoch);
        Entry entry;
        var start = false;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out entry) && entry.Completion.Task.IsCompleted && entry.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _entries.Remove(key);
                entry = null;
            }
            if (entry == null)
            {
                if (_entries.Count >= _capacity)
                {
                    var oldest = _entries.MinBy(x => x.Value.LastAccess);
                    _entries.Remove(oldest.Key);
                }
                entry = new Entry();
                _entries.Add(key, entry);
                start = true;
            }
            entry.LastAccess = ++_accessSequence;
            entry.Waiters++;
        }
        if (start)
            _ = RunProbeAsync(key, token, entry);
        try
        {
            var result = await entry.Completion.Task.WaitAsync(waitToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (authority != null && result.Status is TenantOwnerTokenProbeStatus.Valid or TenantOwnerTokenProbeStatus.Invalid)
            {
                var current = await authority.GetAuthorityAsync(token, waitToken).WaitAsync(waitToken);
                if (!string.Equals(epoch, current, StringComparison.Ordinal))
                    return new(TenantOwnerTokenProbeStatus.Unavailable);
            }
            return result;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(IsTransient(exception) ? TenantOwnerTokenProbeStatus.Transient : TenantOwnerTokenProbeStatus.Unavailable);
        }
        finally
        {
            lock (_sync)
            {
                entry.Waiters--;
                if (entry.Waiters == 0 && !entry.Completion.Task.IsCompleted)
                {
                    entry.Abandoned = true;
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                        _entries.Remove(key);
                    entry.Cancellation.Cancel();
                }
            }
        }
    }

    /// <summary>Immediately removes all retained token identities for one storefront, including detached old flights.</summary>
    /// <param name="tenantBotId">Internal database storefront id being reset, replaced, or authoritatively cleaned.</param>
    /// <remarks>Existing waiters finish their bounded probe, but late completion cannot repopulate the cache. Other tenants are untouched.</remarks>
    /// <example><code>cache.Invalidate(store.Id);</code></example>
    public void Invalidate(string tenantBotId)
    {
        lock (_sync)
            foreach (var key in _entries.Keys.Where(x => x.TenantId == tenantBotId).ToArray())
                _entries.Remove(key);
    }

    /// <summary>Completes one independently budgeted flight and retains only a still-owned result.</summary>
    /// <param name="key">Internal tenant id, SHA-256 token fingerprint, and checked endpoint authority epoch.</param>
    /// <param name="token">Raw secret retained only for the outstanding transport call.</param>
    /// <param name="entry">Shared flight whose waiter count controls shutdown cancellation.</param>
    /// <returns>A task completing after the secret-free result is published to waiting handlers.</returns>
    /// <remarks>Every exception is classified internally; no exception message or raw token enters logs or cached keys.</remarks>
    private async Task RunProbeAsync((string TenantId, string Fingerprint, string Authority) key, string token, Entry entry)
    {
        TenantOwnerTokenProbeResult result;
        try
        {
            using var deadline = new CancellationTokenSource(_probeTimeout, _timeProvider);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, deadline.Token);
            var identity = await _probe.GetMeAsync(token, budget.Token).WaitAsync(budget.Token);
            result = new(TenantOwnerTokenProbeStatus.Valid, identity);
        }
        catch (Exception exception)
        {
            result = new(IsInvalid(exception) ? TenantOwnerTokenProbeStatus.Invalid
                : IsTransient(exception) ? TenantOwnerTokenProbeStatus.Transient : TenantOwnerTokenProbeStatus.Unavailable);
        }
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                if (entry.Abandoned || result.Status == TenantOwnerTokenProbeStatus.Invalid)
                    _entries.Remove(key);
                else
                    entry.ExpiresAt = _timeProvider.GetUtcNow() + (result.Status == TenantOwnerTokenProbeStatus.Valid ? _positiveLifetime : _transientLifetime);
            }
            entry.Completion.TrySetResult(result);
        }
        entry.Cancellation.Dispose();
    }

    /// <summary>Hashes one token with stack-owned normal-token buffers and cleared pooled storage for oversized inputs.</summary>
    /// <param name="token">Required private saved secret; never returned or logged.</param>
    /// <returns>A SHA-256 hexadecimal fingerprint safe for an internal tenant-scoped cache key, not for public diagnostics.</returns>
    /// <remarks>Only the fingerprint string is allocated on ordinary repeated panel taps.</remarks>
    /// <example><code>var key = (store.Id, Fingerprint(savedToken));</code></example>
    internal static string Fingerprint(string token)
    {
        var byteCount = Encoding.UTF8.GetByteCount(token);
        byte[] rented = null;
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            Encoding.UTF8.GetBytes(token, bytes);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(bytes, hash);
            return Convert.ToHexString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Accepts authoritative Telegram rejection or the SDK's explicit local token-format rejection only.</summary>
    /// <param name="exception">Transport or SDK exception inspected privately without exposing its text.</param>
    /// <returns>True for Telegram 401, explicit invalid-token Telegram client responses, or a token-format argument failure.</returns>
    /// <remarks>Network, cancellation, rate-limit and 5xx text can never authorize cleanup, even if it contains invalid-token keywords.</remarks>
    /// <example><code>var mayClearIdentity = IsInvalid(new ApiRequestException("Unauthorized", 401));</code></example>
    private static bool IsInvalid(Exception exception) =>
        exception is ApiRequestException api &&
        (api.ErrorCode == 401 || api.ErrorCode is >= 400 and < 500 && api.ErrorCode != 429 && ContainsInvalidText(api.Message)) ||
        exception is ArgumentException { ParamName: "token" } argument && ContainsInvalidText(argument.Message);

    /// <summary>Matches the existing explicit revoked/invalid token wording.</summary>
    /// <param name="message">Private SDK/Telegram exception text, never emitted to diagnostics.</param>
    /// <returns>True when the message states unauthorized, invalid token, or an invalid bot token.</returns>
    /// <remarks>Preserves the owner's prior authoritative cleanup semantics.</remarks>
    /// <example><code>var explicitRejection = ContainsInvalidText(telegramException.Message);</code></example>
    private static bool ContainsInvalidText(string message) => !string.IsNullOrWhiteSpace(message) &&
        (message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("invalid token", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("bot token", StringComparison.OrdinalIgnoreCase) && message.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    /// <summary>Recognizes bounded cancellation and temporary Telegram/network failures.</summary>
    /// <param name="exception">Private probe exception; caller cancellation is handled separately before any result is used.</param>
    /// <returns>True for budget expiry, network errors, rate limits, server failures, or existing temporary-error wording.</returns>
    /// <remarks>The returned classification is memory-only and must not disable or erase any storefront setting.</remarks>
    /// <example><code>var temporary = IsTransient(new HttpRequestException("Connection reset"));</code></example>
    private static bool IsTransient(Exception exception) => exception is TimeoutException or OperationCanceledException or HttpRequestException ||
        exception is ApiRequestException api && (api.ErrorCode == 429 || api.ErrorCode >= 500) ||
        !string.IsNullOrWhiteSpace(exception.Message) &&
        (exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("temporarily", StringComparison.OrdinalIgnoreCase));

    /// <summary>One bounded retained flight/result; no raw secret or provider payload is stored in its key.</summary>
    private sealed class Entry
    {
        /// <summary>Shared outcome with asynchronous continuations so handlers never run under the cache lock.</summary>
        public TaskCompletionSource<TenantOwnerTokenProbeResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Independent flight cancellation, canceled only after its final pending waiter leaves.</summary>
        public CancellationTokenSource Cancellation { get; } = new();
        /// <summary>Expiry assigned only when the transport finishes.</summary>
        public DateTimeOffset ExpiresAt;
        /// <summary>Monotonic LRU access order.</summary>
        public long LastAccess;
        /// <summary>Number of currently awaiting handlers.</summary>
        public int Waiters;
        /// <summary>Whether handler cancellation removed the final waiter before completion.</summary>
        public bool Abandoned;
    }
}
