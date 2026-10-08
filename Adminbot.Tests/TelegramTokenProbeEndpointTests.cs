using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Xunit;

/// <summary>Real-SDK, isolated-SQLite regressions proving identity validation never invents an unsafe Cloud session.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Registered disabled storefronts validate their exact persisted Local route without enabling delivery.</summary>
    /// <returns>A task after the real getMe origin and unchanged disabled setting are asserted.</returns>
    [Fact]
    public async Task Token_probe_registered_disabled_local_uses_routed_capability_transport()
    {
        using var fixture = new TokenEndpointFixture();
        fixture.Registry.Upsert(new BotInstance { Id = "tenant-local", Token = EndpointRuntimeToken(777888), Type = BotInstanceTypes.Tenant, Enabled = false });
        await fixture.SaveAsync("tenant-local", 777888, TelegramEndpointType.Local, TelegramEndpointMigrationState.Local);
        var identity = await fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default);
        Assert.Equal(777888, identity.Id);
        Assert.Equal("local", Assert.Single(fixture.Http.Calls).Endpoint);
        Assert.False(fixture.Registry.GetById("tenant-local").Enabled);
        Assert.Throws<BotTransportUnavailableException>(() => fixture.Clients.GetClient("tenant-local"));
    }

    /// <summary>Pending or ambiguous logout cannot validate either current or replacement secrets at Cloud.</summary>
    /// <param name="state">Durable fenced protocol state.</param>
    /// <returns>A task after rejection of both secrets before any SDK HTTP request.</returns>
    [Theory]
    [InlineData(TelegramEndpointMigrationState.CloudLogoutPending)]
    [InlineData(TelegramEndpointMigrationState.CloudLogoutUncertain)]
    [InlineData(TelegramEndpointMigrationState.LocalLogoutUncertain)]
    [InlineData(TelegramEndpointMigrationState.CloudWait)]
    public async Task Token_probe_pending_or_uncertain_cloud_rejects_before_http(TelegramEndpointMigrationState state)
    {
        using var fixture = new TokenEndpointFixture();
        await fixture.SaveAsync("endpoint-a", 123456, TelegramEndpointType.Cloud, state);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(EndpointRuntimeToken(123456), default));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(ProbeToken(123456), default));
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>A removed internal alias remains authoritative for unregistered identity tokens, including uncertain cleanup and cooldown.</summary>
    /// <param name="history">Historical Local, uncertain, cooldown, or stale logout acknowledgment state.</param>
    /// <returns>A task after both unregistered Cloud login and fresh-alias Cloud default are denied.</returns>
    [Theory]
    [InlineData("local")]
    [InlineData("uncertain")]
    [InlineData("cooldown")]
    [InlineData("stale_ack")]
    public async Task Token_probe_unregistered_historical_identity_alias_prevents_cloud_login(string history)
    {
        using var fixture = new TokenEndpointFixture();
        var endpoint = history == "local" ? TelegramEndpointType.Local : TelegramEndpointType.Cloud;
        var phase = history == "uncertain" ? TelegramEndpointMigrationState.LocalLogoutUncertain :
            history == "local" ? TelegramEndpointMigrationState.Local : TelegramEndpointMigrationState.Cloud;
        await fixture.SaveAsync("removed-alias", 777888, endpoint, phase, history == "cooldown" ? DateTime.UtcNow.AddMinutes(10) : null);
        if (history == "stale_ack")
        {
            var state = await fixture.Store.GetOrCreateAsync("removed-alias", 777888, default);
            state.LogoutAttemptedAtUtc = DateTime.UtcNow;
            state.LogoutAcknowledgedAtUtc = state.LogoutAttemptedAtUtc.Value.AddMinutes(-1);
            Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision));
        }
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default));
        var freshAlias = await fixture.Store.GetOrCreateAsync("renamed-alias", 777888, default);
        Assert.Equal(TelegramEndpointMigrationState.ManualInterventionRequired, freshAlias.MigrationState);
        Assert.Equal("identity_alias_conflict", freshAlias.LastFailureCategory);
        var old = await fixture.Store.GetOrCreateAsync("removed-alias", 777888, default);
        Assert.Equal(phase == TelegramEndpointMigrationState.Cloud ?
            TelegramEndpointMigrationState.ManualInterventionRequired : phase, old.MigrationState);
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>An existing dormant Cloud alias cannot reopen Cloud after another alias established a Local session.</summary>
    /// <returns>A task after hydration fences the old Cloud row exactly once and preserves the separate Local session metadata.</returns>
    [Fact]
    public async Task Token_probe_returning_existing_cloud_alias_is_fenced_by_historical_local_session()
    {
        using var fixture = new TokenEndpointFixture();
        var oldCloud = await fixture.Store.GetOrCreateAsync("returning-cloud", 777888, default);
        await fixture.SaveAsync("active-local-alias", 777888, TelegramEndpointType.Local, TelegramEndpointMigrationState.Local);
        fixture.Registry.Upsert(new BotInstance
        { Id = "returning-cloud", Token = EndpointRuntimeToken(777888), Type = BotInstanceTypes.Owned, Enabled = true });
        await fixture.Gate.HydrateAsync("returning-cloud", 777888, default);
        Assert.False(fixture.Gate.GetRoute("returning-cloud", 777888).Available);
        var fenced = await fixture.Store.GetOrCreateAsync("returning-cloud", 777888, default);
        Assert.Equal(TelegramEndpointMigrationState.ManualInterventionRequired, fenced.MigrationState);
        Assert.Equal("identity_alias_conflict", fenced.LastFailureCategory);
        Assert.Equal(oldCloud.Revision + 1, fenced.Revision);
        Assert.Equal(oldCloud.ControlRevision + 1, fenced.ControlRevision);
        Assert.Equal(oldCloud.Generation, fenced.Generation);
        Assert.Equal(TelegramEndpointMigrationState.Local,
            (await fixture.Store.GetOrCreateAsync("active-local-alias", 777888, default)).MigrationState);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() =>
            fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default));
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>Same-identity replacement probes use the supplied secret at Local, while token rotation invalidates the owner cache epoch.</summary>
    /// <returns>A task after safe fingerprint observations prove replacement and current-token behavior without retaining secrets.</returns>
    [Fact]
    public async Task Token_probe_same_identity_replacement_and_rotation_preserve_local_authority()
    {
        using var fixture = new TokenEndpointFixture();
        await fixture.SaveAsync("endpoint-a", 123456, TelegramEndpointType.Local, TelegramEndpointMigrationState.Local);
        var original = EndpointRuntimeToken(123456);
        var replacement = ProbeToken(123456);
        var cache = new TenantOwnerTokenProbeCache(fixture.Probe);
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await cache.ProbeAsync("endpoint-a", original, default)).Status);
        Assert.Equal(123456, (await fixture.Probe.GetMeAsync(replacement, default)).Id);
        Assert.Equal(TenantOwnerTokenProbeCache.Fingerprint(replacement), fixture.Http.Calls.Last().Fingerprint);
        fixture.Registry.Upsert(new BotInstance { Id = "endpoint-a", Token = replacement, Type = BotInstanceTypes.Owned, Enabled = true });
        fixture.Http.RejectedFingerprint = TenantOwnerTokenProbeCache.Fingerprint(original);
        Assert.Equal(TenantOwnerTokenProbeStatus.Invalid, (await cache.ProbeAsync("endpoint-a", original, default)).Status);
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await cache.ProbeAsync("endpoint-a", replacement, default)).Status);
        Assert.Equal(4, fixture.Http.Calls.Count);
        Assert.All(fixture.Http.Calls, call => Assert.Equal("local", call.Endpoint));
    }

    /// <summary>Numeric identity replacement cannot erase the formerly Local route when the original BotFather identity returns.</summary>
    /// <returns>A task after Local-to-fresh-Cloud-to-Local reads and unregistered former-Local rejection are asserted.</returns>
    [Fact]
    public async Task Token_probe_identity_replacement_then_return_restores_durable_local_route()
    {
        using var fixture = new TokenEndpointFixture();
        await fixture.SaveAsync("endpoint-a", 123456, TelegramEndpointType.Local, TelegramEndpointMigrationState.Local);
        await fixture.Probe.GetMeAsync(EndpointRuntimeToken(123456), default);
        fixture.Registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(777888), Type = BotInstanceTypes.Owned, Enabled = true });
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(EndpointRuntimeToken(123456), default));
        await fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default);
        fixture.Registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        await fixture.Probe.GetMeAsync(EndpointRuntimeToken(123456), default);
        Assert.Equal(new[] { "local", "cloud", "local" }, fixture.Http.Calls.Select(x => x.Endpoint));
    }

    /// <summary>Active-looking Cloud metadata with a future reuse deadline cannot authorize a registered identity read.</summary>
    /// <returns>A task after rejection before any HTTP, even though the gate phase is nominally active.</returns>
    [Fact]
    public async Task Token_probe_registered_active_cloud_cooldown_rejects_before_http()
    {
        using var fixture = new TokenEndpointFixture();
        await fixture.SaveAsync("endpoint-a", 123456, TelegramEndpointType.Cloud, TelegramEndpointMigrationState.Cloud, DateTime.UtcNow.AddMinutes(10));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(EndpointRuntimeToken(123456), default));
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>Fresh registration and provably eligible historical Cloud aliases retain ordinary Cloud validation behavior.</summary>
    /// <returns>A task after unregistered and renamed safe identities return valid real-SDK getMe results.</returns>
    [Fact]
    public async Task Token_probe_fresh_and_safe_historical_cloud_registration_remain_eligible()
    {
        using var fixture = new TokenEndpointFixture();
        Assert.Equal(777888, (await fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default)).Id);
        await fixture.SaveAsync("former-cloud-alias", 888999, TelegramEndpointType.Cloud, TelegramEndpointMigrationState.CloudRecovered, DateTime.UnixEpoch);
        Assert.Equal(888999, (await fixture.Probe.GetMeAsync(EndpointRuntimeToken(888999), default)).Id);
        var newAlias = await fixture.Store.GetOrCreateAsync("renamed-safe-cloud", 888999, default);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, newAlias.MigrationState);
        Assert.Equal(2, fixture.Http.Calls.Count);
        Assert.All(fixture.Http.Calls, call => Assert.Equal("cloud", call.Endpoint));
    }

    /// <summary>A positive owner cache cannot authorize a fenced migration or survive a route-generation change without a new request.</summary>
    /// <returns>A task after cache reuse, no-HTTP fence rejection, and new-generation HTTP are distinguished.</returns>
    [Fact]
    public async Task Token_probe_owner_cache_rechecks_fence_and_generation_before_success()
    {
        using var fixture = new TokenEndpointFixture();
        await fixture.SaveAsync("endpoint-a", 123456, TelegramEndpointType.Local, TelegramEndpointMigrationState.Local);
        var cache = new TenantOwnerTokenProbeCache(fixture.Probe);
        var token = EndpointRuntimeToken(123456);
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await cache.ProbeAsync("endpoint-a", token, default)).Status);
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await cache.ProbeAsync("endpoint-a", token, default)).Status);
        Assert.Single(fixture.Http.Calls);
        fixture.Gate.Fence("endpoint-a", 123456);
        Assert.Equal(TenantOwnerTokenProbeStatus.Unavailable, (await cache.ProbeAsync("endpoint-a", token, default)).Status);
        Assert.Single(fixture.Http.Calls);
        await fixture.Gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(1), default);
        var state = await fixture.Store.GetOrCreateAsync("endpoint-a", 123456, default);
        state.Generation++;
        Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision));
        fixture.Gate.Publish(state);
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await cache.ProbeAsync("endpoint-a", token, default)).Status);
        Assert.Equal(2, fixture.Http.Calls.Count);
        Assert.All(fixture.Http.Calls, call => Assert.Equal("local", call.Endpoint));
    }

    /// <summary>An unregistered getMe remains counted through SDK response consumption when its numeric identity registers and starts migrating.</summary>
    /// <returns>A task after migration drain and fake irreversible logout wait for the complete original probe.</returns>
    /// <remarks>The held response body distinguishes complete SDK admission from response-header admission; no live endpoint or session is touched.</remarks>
    [Fact]
    public async Task Token_probe_unregistered_sdk_completion_blocks_same_identity_migration_logout()
    {
        using var fixture = new TokenEndpointFixture();
        fixture.Http.HeldIdentity = 777888;
        var probing = fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default);
        try
        {
            await fixture.Http.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Registry.Upsert(new BotInstance
            { Id = "new-registration", Token = EndpointRuntimeToken(777888), Type = BotInstanceTypes.Owned, Enabled = true });
            using var migration = new TokenEndpointMigrationFixture(fixture);
            await migration.Coordinator.InitializeAsync(default);
            var state = await fixture.Store.GetOrCreateAsync("new-registration", 777888, default);
            Assert.Equal("accepted", await migration.Coordinator.RequestMigrationAsync(
                state.BotId, TelegramEndpointType.Local, 7, state.ControlRevision, state.TelegramBotId, default));
            var migrating = migration.Coordinator.RunPendingOperationsAsync(default);
            await migration.ReceiverStopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var draining = fixture.Gate.DrainAsync("new-registration", 777888, TimeSpan.FromSeconds(3), default);
            Assert.False(probing.IsCompleted);
            Assert.False(draining.IsCompleted);
            Assert.False(migrating.IsCompleted);
            Assert.Equal(0, migration.LogoutCalls);
            Assert.Single(fixture.Http.Calls);
            fixture.Http.BodyRelease.TrySetResult();
            await Assert.ThrowsAsync<BotTransportUnavailableException>(() => probing);
            await draining;
            await migrating;
            Assert.Equal(1, migration.LogoutCalls);
            Assert.Equal(TelegramEndpointMigrationState.Local,
                (await fixture.Store.GetOrCreateAsync("new-registration", 777888, default)).MigrationState);
            Assert.Equal("cloud", Assert.Single(fixture.Http.Calls).Endpoint);
        }
        finally { fixture.Http.BodyRelease.TrySetResult(); }
    }

    /// <summary>A durable migration fence survives loss of its current registry alias and rejects a now-unregistered probe before Cloud HTTP.</summary>
    /// <returns>A task after both direct and cached authority paths reject the fenced former identity without a request.</returns>
    [Fact]
    public async Task Token_probe_fenced_identity_without_current_alias_rejects_before_http()
    {
        using var fixture = new TokenEndpointFixture();
        await fixture.Gate.HydrateAsync("endpoint-a", 123456, default);
        fixture.Gate.Fence("endpoint-a", 123456);
        var state = await fixture.Store.GetOrCreateAsync("endpoint-a", 123456, default);
        state.DesiredEndpoint = TelegramEndpointType.Local;
        state.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
        Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision));
        fixture.Registry.Upsert(new BotInstance
        { Id = "endpoint-a", Token = EndpointRuntimeToken(777888), Type = BotInstanceTypes.Owned, Enabled = true });
        var token = EndpointRuntimeToken(123456);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(token, default));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetAuthorityAsync(token, default));
        var cache = new TenantOwnerTokenProbeCache(fixture.Probe);
        Assert.Equal(TenantOwnerTokenProbeStatus.Unavailable, (await cache.ProbeAsync("endpoint-a", token, default)).Status);
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>The pooled unregistered provider seam cannot dispatch with absent, foreign, mismatched, or disposed identity admission.</summary>
    /// <returns>A task after every invalid proof is rejected without SDK HTTP.</returns>
    [Fact]
    public async Task Token_probe_unregistered_provider_requires_active_matching_gate_admission()
    {
        using var fixture = new TokenEndpointFixture();
        var token = EndpointRuntimeToken(777888);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() =>
            fixture.Clients.ProbeUnregisteredTokenAsync(token, null!, default));
        using var foreign = new TelegramEndpointRuntimeGate(fixture.Registry).AcquireIdentityProbe(777888);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() =>
            fixture.Clients.ProbeUnregisteredTokenAsync(token, foreign, default));
        using var different = fixture.Gate.AcquireIdentityProbe(888999);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() =>
            fixture.Clients.ProbeUnregisteredTokenAsync(token, different, default));
        var disposed = fixture.Gate.AcquireIdentityProbe(777888);
        disposed.Dispose();
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() =>
            fixture.Clients.ProbeUnregisteredTokenAsync(token, disposed, default));
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>Cross-alias authority refuses incomplete oversized results rather than assuming the first page is safe.</summary>
    /// <returns>A task after bounded query overflow prevents fresh token Cloud login.</returns>
    [Fact]
    public async Task Token_probe_identity_alias_authority_overflow_fails_closed()
    {
        using var fixture = new TokenEndpointFixture();
        await using (var db = fixture.Databases.Users.CreateDbContext())
        {
            for (var index = 0; index < 129; index++)
                db.TelegramEndpointStates.Add(new TelegramEndpointState { BotId = "alias-" + index, TelegramBotId = 777888 });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Store.ReadIdentityStatesAsync(777888, default));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => fixture.Probe.GetMeAsync(EndpointRuntimeToken(777888), default));
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>Direct legacy construction has no durable proof of Cloud safety and therefore never opens a Telegram connection.</summary>
    /// <returns>A task after the safe unavailable-domain result is asserted.</returns>
    [Fact]
    public async Task Token_probe_direct_construction_without_authority_fails_closed() =>
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => new TelegramTokenProbe().GetMeAsync(EndpointRuntimeToken(123456), default));

    /// <summary>Direct construction cannot pair a Local authority gate with a legacy Cloud-only provider.</summary>
    /// <remarks>A mismatched provider must fail before any token or endpoint request is issued.</remarks>
    [Fact]
    public void Token_probe_direct_construction_rejects_mismatched_routing_authority()
    {
        using var fixture = new TokenEndpointFixture();
        using var legacy = new BotClientProvider(fixture.Registry);
        Assert.Throws<BotTransportUnavailableException>(() =>
            new TelegramTokenProbe(fixture.Registry, legacy, fixture.Store, fixture.Gate));
        Assert.Empty(fixture.Http.Calls);
    }

    /// <summary>Owns isolated SQLite and real routing/SDK components over one shared fake HTTP transport.</summary>
    private sealed class TokenEndpointFixture : IDisposable
    {
        /// <summary>Independent disposable databases; financial data remains unused.</summary>
        public readonly Databases Databases = new();
        /// <summary>Current synthetic registry identities.</summary>
        public readonly BotRegistry Registry = EndpointRuntimeRegistry();
        /// <summary>Durable exact-identity and cross-alias authority.</summary>
        public readonly TelegramEndpointStore Store;
        /// <summary>Real migration and hydration authority.</summary>
        public readonly TelegramEndpointRuntimeGate Gate;
        /// <summary>Fake socket boundary retaining only secret-free observations.</summary>
        public readonly TokenEndpointHttp Http = new();
        /// <summary>Production routed provider using the real SDK over fake HTTP.</summary>
        public readonly BotClientProvider Clients;
        /// <summary>Production authority and identity implementation with only HTTP substituted.</summary>
        public readonly TelegramTokenProbe Probe;

        /// <summary>Constructs the complete offline endpoint identity fixture without hosted processes.</summary>
        public TokenEndpointFixture()
        {
            Store = new TelegramEndpointStore(Databases.Users, new AppConfig());
            Gate = new TelegramEndpointRuntimeGate(Registry, Store);
            Clients = new BotClientProvider(Registry, Gate, new TelegramEndpointRoutingOptions(), Http);
            Probe = new TelegramTokenProbe(Registry, Clients, Store, Gate);
        }

        /// <summary>Persists controlled endpoint metadata without any migration or provider API call.</summary>
        /// <param name="bot">Synthetic exact internal id, including historical aliases.</param>
        /// <param name="identity">Synthetic BotFather identity.</param>
        /// <param name="endpoint">Effective trusted endpoint.</param>
        /// <param name="phase">Durable protocol phase.</param>
        /// <param name="eligibleAt">Optional exact Cloud reuse instant.</param>
        /// <returns>A task after the durable state commits.</returns>
        /// <remarks>The runtime reads this metadata through real hydration on its first probe.</remarks>
        public async Task SaveAsync(string bot, long identity, TelegramEndpointType endpoint, TelegramEndpointMigrationState phase, DateTime? eligibleAt = null)
        {
            var state = await Store.GetOrCreateAsync(bot, identity, default);
            state.DesiredEndpoint = endpoint;
            state.EffectiveEndpoint = endpoint;
            state.MigrationState = phase;
            state.CloudReuseEligibleAtUtc = eligibleAt;
            state.Generation = 2;
            Assert.True(await Store.TrySaveAsync(state, state.Revision));
        }

        /// <inheritdoc />
        public void Dispose() { Clients.Dispose(); Http.Dispose(); Databases.Dispose(); }
    }

    /// <summary>Runs the real durable migration coordinator with observable offline logout and receiver boundaries.</summary>
    /// <remarks>Only protocol and receiver I/O are substituted; the existing real SQLite store, provider and numeric-identity gate remain authoritative.</remarks>
    private sealed class TokenEndpointMigrationFixture : ITelegramEndpointProtocol, ITelegramEndpointReceiverLifecycle, IDisposable
    {
        /// <summary>Real isolated endpoint authority shared with the in-flight getMe.</summary>
        private readonly TokenEndpointFixture _fixture;
        /// <summary>Lazy receiver lifecycle registration owned only by this fixture.</summary>
        private readonly ServiceProvider _services;
        /// <summary>Real durable intent, fencing and drain implementation.</summary>
        public TelegramEndpointCoordinator Coordinator { get; }
        /// <summary>Signals source receiver join before the coordinator waits on numeric-identity draining.</summary>
        public TaskCompletionSource ReceiverStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Number of acknowledged fake irreversible logout attempts; never a production API call.</summary>
        public int LogoutCalls { get; private set; }

        /// <summary>Constructs an offline coordinator over the probe fixture's existing durable and runtime identity authority.</summary>
        /// <param name="fixture">Required isolated real-SDK fixture containing the held unregistered probe.</param>
        /// <remarks>Trusted temporary mapping roots satisfy the real migration precondition; no host process or network starts.</remarks>
        public TokenEndpointMigrationFixture(TokenEndpointFixture fixture)
        {
            _fixture = fixture;
            _services = new ServiceCollection().AddSingleton<ITelegramEndpointReceiverLifecycle>(this).BuildServiceProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AdminsUserIds:0"] = "7" }).Build();
            var options = new TelegramEndpointRoutingOptions
            { LocalFileServerRoot = "/data/local", LocalFileHostRoot = Path.GetTempPath() };
            Coordinator = new TelegramEndpointCoordinator(fixture.Store, fixture.Gate, fixture.Registry,
                fixture.Clients, configuration, options, _services, NullLogger<TelegramEndpointCoordinator>.Instance, protocol: this);
        }

        /// <inheritdoc />
        public Task<TelegramEndpointProbeResult> ProbeLocalServerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TelegramEndpointProbeResult());
        }

        /// <inheritdoc />
        public Task<TelegramEndpointProbeResult> VerifyIdentityAsync(string botId, long identity,
            TelegramEndpointType endpoint, long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(777888, identity);
            return Task.FromResult(new TelegramEndpointProbeResult());
        }

        /// <inheritdoc />
        public async Task<TelegramEndpointLogoutResult> LogOutOnceAsync(string botId, long identity,
            TelegramEndpointType endpoint, long generation, CancellationToken cancellationToken)
        {
            var state = await _fixture.Store.GetOrCreateAsync(botId, identity, cancellationToken);
            Assert.Equal(TelegramEndpointMigrationState.CloudLogoutPending, state.MigrationState);
            Assert.True(state.LogoutAttemptedAtUtc.HasValue);
            Assert.True(_fixture.Http.BodyRelease.Task.IsCompleted);
            LogoutCalls++;
            return new(true, false);
        }

        /// <inheritdoc />
        public Task<ITelegramEndpointReceiverLease> AcquireAsync(string botId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("new-registration", botId);
            return Task.FromResult<ITelegramEndpointReceiverLease>(new ReceiverLease(this));
        }

        /// <inheritdoc />
        public void Dispose() { Coordinator.Dispose(); _services.Dispose(); }

        /// <summary>Exposes deterministic source join and validated destination startup without creating a receiver.</summary>
        /// <param name="owner">Offline lifecycle fixture whose source-join signal is observed by the regression.</param>
        private sealed class ReceiverLease(TokenEndpointMigrationFixture owner) : ITelegramEndpointReceiverLease
        {
            /// <inheritdoc />
            public Task StopAndWaitAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                owner.ReceiverStopped.TrySetResult();
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task<bool> StartValidatedAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            }
            /// <inheritdoc />
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Returns real getMe JSON to the pinned SDK while retaining no token-bearing URLs or secrets.</summary>
    private sealed class TokenEndpointHttp : HttpMessageHandler
    {
        /// <summary>Numeric identity, trusted endpoint family, and cryptographic token fingerprint only.</summary>
        public readonly List<(long Identity, string Endpoint, string Fingerprint)> Calls = [];
        /// <summary>Optional revoked synthetic token fingerprint; never a raw token.</summary>
        public string? RejectedFingerprint;
        /// <summary>Optional synthetic numeric identity whose SDK response body remains held after HTTP headers arrive.</summary>
        public long? HeldIdentity;
        /// <summary>Signals actual SDK consumption of the held response body, not merely HTTP dispatch.</summary>
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Releases the held JSON body so the original SDK await and authority recheck can finish.</summary>
        public TaskCompletionSource BodyRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Answers real SDK getMe with optional held body consumption or authoritative token rejection, without a network request.</summary>
        /// <param name="request">Actual SDK request examined only within this call.</param>
        /// <param name="cancellationToken">Caller-owned real SDK cancellation.</param>
        /// <returns>A Telegram-shaped success or 401 envelope; selected identity bodies await explicit release.</returns>
        /// <remarks>No URL, token, or message payload is retained or logged. Holding the body after headers tests the full SDK lease lifetime.</remarks>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var segments = uri.AbsolutePath.Split('/');
            Assert.Equal("getMe", segments[^1]);
            var token = segments[1][3..];
            var identity = TelegramBotTokenIdentity.ExtractBotId(token)!.Value;
            var fingerprint = TenantOwnerTokenProbeCache.Fingerprint(token);
            Calls.Add((identity, uri.Host == "api.telegram.org" ? "cloud" : "local", fingerprint));
            var rejected = fingerprint == RejectedFingerprint;
            var json = rejected ? "{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized\"}" :
                JsonSerializer.Serialize(new { ok = true, result = new { id = identity, is_bot = true, first_name = "synthetic", username = "synthetic_bot" } });
            return Task.FromResult(new HttpResponseMessage(rejected ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = identity == HeldIdentity ? new HeldTokenEndpointContent(json, BodyStarted, BodyRelease) :
                new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Holds real SDK JSON consumption after response headers to expose premature request-lease release.</summary>
    private sealed class HeldTokenEndpointContent : HttpContent
    {
        /// <summary>Synthetic UTF-8 identity response; contains no token, URL or customer payload.</summary>
        private readonly byte[] _body;
        /// <summary>Signals the SDK has begun reading response content.</summary>
        private readonly TaskCompletionSource _started;
        /// <summary>Explicit regression-owned release barrier.</summary>
        private readonly TaskCompletionSource _release;

        /// <summary>Creates held identity JSON without changing the provider or SDK transport implementation.</summary>
        /// <param name="body">Synthetic Telegram success envelope without secrets.</param>
        /// <param name="started">Signal set when SDK content consumption begins.</param>
        /// <param name="release">Explicit barrier that keeps complete SDK processing in flight.</param>
        public HeldTokenEndpointContent(string body, TaskCompletionSource started, TaskCompletionSource release)
        {
            _body = Encoding.UTF8.GetBytes(body);
            _started = started;
            _release = release;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        /// <inheritdoc />
        protected override bool TryComputeLength(out long length) { length = _body.Length; return true; }
        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            WriteBodyAsync(stream, CancellationToken.None);
        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            WriteBodyAsync(stream, cancellationToken);

        /// <summary>Signals SDK content consumption and writes the synthetic body only after the regression releases it.</summary>
        /// <param name="stream">SDK-owned destination for the isolated response bytes.</param>
        /// <param name="cancellationToken">Original SDK content-read cancellation when supplied by HttpClient.</param>
        /// <returns>A task remaining incomplete until the explicit body-release barrier opens.</returns>
        /// <remarks>The response headers are already available while this body operation remains admitted.</remarks>
        private async Task WriteBodyAsync(Stream stream, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync(_body, cancellationToken);
        }
    }
}
