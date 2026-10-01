using System.Buffers;
using System.Security.Cryptography;
using System.Text;
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
    /// <remarks>Does not change a storefront or retry failed Telegram requests.</remarks>
    /// <exception cref="OperationCanceledException">The caller's probe scope was canceled.</exception>
    /// <exception cref="ApiRequestException">Telegram rejects getMe; callers must distinguish authoritative token rejection from transient API failures.</exception>
    /// <exception cref="ArgumentException">The SDK rejects the supplied token's format before contacting Telegram.</exception>
    /// <example><code>var identity = await probe.GetMeAsync(savedToken, probeCancellation.Token);</code></example>
    Task<Telegram.Bot.Types.User> GetMeAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Non-retrying Telegram getMe transport shared by foreground owner validation and token registration.</summary>
public sealed class TelegramTokenProbe : ITelegramTokenProbe
{
    /// <inheritdoc />
    public Task<Telegram.Bot.Types.User> GetMeAsync(string token, CancellationToken cancellationToken) =>
        new TelegramBotClient(new TelegramBotClientOptions(token) { RetryCount = 0 }).GetMe(cancellationToken);
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
/// Production registers one singleton across scoped handlers. Keys combine the internal storefront id with SHA-256
/// token fingerprints, never raw secrets. Success lives three minutes and non-authoritative failures twenty seconds.
/// Invalid results are not retained. Eviction and explicit invalidation detach old flights, preventing late completion
/// from repopulating a replaced identity. Callers must still guard persisted identity/revision before applying results.
/// Canceling the final waiter removes and cancels its flight rather than caching handler shutdown as a transient failure.
/// </remarks>
public sealed class TenantOwnerTokenProbeCache
{
    /// <summary>Protects the bounded entry map and waiter ownership; never held across an await.</summary>
    private readonly object _sync = new();
    /// <summary>Retained results and shared flights keyed solely by internal id and cryptographic fingerprint.</summary>
    private readonly Dictionary<(string TenantId, string Fingerprint), Entry> _entries = new();
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
    /// <remarks>One canceled waiter cannot cancel another handler. If all waiters leave, the flight is discarded and canceled.</remarks>
    /// <exception cref="OperationCanceledException">The calling handler was canceled, including on a cache hit.</exception>
    /// <example><code>var result = await cache.ProbeAsync(store.Id, store.Token, updateCancellation);</code></example>
    public async Task<TenantOwnerTokenProbeResult> ProbeAsync(string tenantBotId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantBotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var key = (tenantBotId, Fingerprint(token));
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
            var result = await entry.Completion.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
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
    /// <param name="key">Internal tenant id and SHA-256 token fingerprint.</param>
    /// <param name="token">Raw secret retained only for the outstanding transport call.</param>
    /// <param name="entry">Shared flight whose waiter count controls shutdown cancellation.</param>
    /// <returns>A task completing after the secret-free result is published to waiting handlers.</returns>
    /// <remarks>Every exception is classified internally; no exception message or raw token enters logs or cached keys.</remarks>
    private async Task RunProbeAsync((string TenantId, string Fingerprint) key, string token, Entry entry)
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
    private static string Fingerprint(string token)
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
