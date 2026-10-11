using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using System.Reflection;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>Owner-panel regressions through real addressed callbacks and isolated SQLite, with no Telegram connections.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>A slow getMe cannot inherit the twelve-second startup budget or erase saved storefront settings.</summary>
    /// <returns>A task completing after the bounded callback, visible warning, saved identity and closed-stage attribution checks.</returns>
    [Fact]
    public async Task Owner_panel_probe_times_out_shortly_preserves_settings_and_attributes_actual_wait()
    {
        using var databases = new Databases();
        var entered = Signal(); var canceled = Signal(); var release = new TaskCompletionSource<Telegram.Bot.Types.User>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new OwnerIdentityProbe((_, token) => { token.Register(() => canceled.TrySetResult()); entered.TrySetResult(); return release.Task; });
        var clock = new OwnerProbeClock();
        var cache = new TenantOwnerTokenProbeCache(probe, probeTimeout: TimeSpan.FromMilliseconds(40), timeProvider: clock);
        await using var provider = StorefrontProvider(databases, tokenProbe: probe, ownerTokenProbeCache: cache);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        var client = new StorefrontClient(); var stages = new List<TelegramUpdateStage>();
        using var latency = TelegramUpdateLatencyScope.Push(1, "owned-probe", 1, TimeSpan.FromTicks(1), (stage, _) => stages.Add(stage));
        var callback = OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TelegramUpdateStage.TelegramProbe, latency.CurrentStage);
        clock.Advance(TimeSpan.FromMilliseconds(39));
        Assert.False(canceled.Task.IsCompleted);
        Assert.False(callback.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(TelegramUpdateStage.TelegramProbe, stages);
        Assert.Null(latency.CurrentStage);
        await AssertOwnerProbeSettingsPreserved(databases, tenant);
        release.TrySetResult(ProbeIdentity(60001));
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>Network/server errors cannot authorize cleanup even when their exception text mentions invalid tokens.</summary>
    /// <param name="failure">Network, poisoned 5xx, or ambiguous non-Telegram failure under the production classifier.</param>
    /// <returns>A task after repeated real taps render warnings while every identity and owner setting survives.</returns>
    [Theory]
    [InlineData("network")]
    [InlineData("server")]
    [InlineData("ambiguous")]
    public async Task Owner_panel_non_authoritative_failures_preserve_identity_and_use_transient_cache(string failure)
    {
        using var databases = new Databases();
        Exception exception = failure switch
        {
            "network" => new HttpRequestException("Upstream unauthorized invalid token during network timeout"),
            "server" => new ApiRequestException("Upstream unauthorized invalid token", 503),
            _ => new InvalidOperationException("Unauthorized invalid token from an ambiguous local operation")
        };
        var probe = new OwnerIdentityProbe((_, _) => Task.FromException<Telegram.Bot.Types.User>(exception));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001); var client = new StorefrontClient();
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        Assert.Equal(1, probe.Calls);
        await AssertOwnerProbeSettingsPreserved(databases, tenant);
    }

    /// <summary>Authoritative rejection cleans only the selected identity and wallet trust, never prices, history or sibling stores.</summary>
    /// <returns>A task after real cleanup and preserved historical/settings invariants have been checked.</returns>
    [Fact]
    public async Task Owner_panel_authoritative_invalid_token_cleans_identity_not_history_or_settings()
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((_, _) => Task.FromException<Telegram.Bot.Types.User>(new ApiRequestException("Unauthorized", 401)));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        var sibling = await SeedOwnerProbeStore(databases, provider, 60002);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TenantBotOrders.Add(new TenantBotOrder { OrderId = "owner-probe-history", TenantBotId = tenant.Id, OwnerTelegramUserId = 711, SalePriceToman = 123000 });
            await db.SaveChangesAsync();
        }
        var client = new StorefrontClient();
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.BotInstances.SingleAsync(x => x.Id == tenant.Id);
        Assert.Null(saved.Token); Assert.Null(saved.TelegramBotId); Assert.Null(saved.Username); Assert.False(saved.Enabled);
        Assert.False(saved.TenantCustomerWalletEnabled); Assert.False(saved.TenantCustomerWalletOwnerEnabled); Assert.Null(saved.TenantCustomerWalletApprovedBotId);
        Assert.Equal(tenant.BrandName, saved.BrandName); Assert.Equal(tenant.SupportAccount, saved.SupportAccount);
        Assert.Equal(tenant.TenantCardNumber, saved.TenantCardNumber); Assert.Equal(tenant.TenantWelcomeText, saved.TenantWelcomeText);
        Assert.Equal(tenant.TenantPricingMode, saved.TenantPricingMode); Assert.Equal(tenant.TenantNormalPricePerGbToman, saved.TenantNormalPricePerGbToman);
        Assert.Equal(123000, (await verify.TenantBotOrders.SingleAsync()).SalePriceToman);
        Assert.Equal(sibling.Token, (await verify.BotInstances.SingleAsync(x => x.Id == sibling.Id)).Token);
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(saved, "panel"));
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>Successful checks synchronize usernames and missing brands, then reuse positive cache across new service scopes.</summary>
    /// <returns>A task after persisted Telegram identity and repeated callback behavior are verified.</returns>
    [Fact]
    public async Task Owner_panel_positive_cache_synchronizes_username_and_missing_brand_across_scopes()
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((_, _) => Task.FromResult(ProbeIdentity(60001, "fresh_store", "نام تازه")));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        await using (var db = databases.Users.CreateDbContext()) { var row = await db.BotInstances.SingleAsync(); row.BrandName = null; await db.SaveChangesAsync(); }
        var client = new StorefrontClient();
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        tenant = (await provider.GetRequiredService<TenantStoreStore>().ListAsync(711)).Single();
        Assert.Equal("fresh_store", tenant.Username); Assert.Equal("نام تازه", tenant.BrandName);
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>Token replacement uses the actual owner input flow and invalidates both old and replacement foreground identities.</summary>
    /// <returns>A task after same-numeric-bot secret replacement, full reset and subsequent cache misses are checked.</returns>
    [Fact]
    public async Task Owner_token_save_and_reset_invalidate_cached_identities_through_actual_handlers()
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((token, _) => Task.FromResult(ProbeIdentity(TelegramBotTokenIdentity.ExtractBotId(token)!.Value)));
        var cache = new TenantOwnerTokenProbeCache(probe);
        await using var provider = StorefrontProvider(databases, tokenProbe: probe, ownerTokenProbeCache: cache);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001); var original = tenant.Token; var client = new StorefrontClient();
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        await SaveOwnerProbeToken(provider, client, tenant, "60001:" + new string('z', 35));
        Assert.Equal(3, probe.Calls); // Initial panel, uncached registration, replacement's fresh panel.
        tenant = (await provider.GetRequiredService<TenantStoreStore>().ListAsync(711)).Single();
        Assert.NotEqual(original, tenant.Token); Assert.Equal(60001, tenant.TelegramBotId); Assert.False(tenant.Enabled);
        await cache.ProbeAsync(tenant.Id, original, default);
        Assert.Equal(4, probe.Calls); // Same bot id does not preserve the old-secret positive entry.
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "reset-confirm"));
        var reset = (await provider.GetRequiredService<TenantStoreStore>().ListAsync(711)).Single();
        Assert.Null(reset.Token); Assert.Null(reset.SupportAccount); Assert.Null(reset.TenantNormalPricePerGbToman);
        await cache.ProbeAsync(tenant.Id, tenant.Token, default);
        Assert.Equal(5, probe.Calls); // Reset removes the replacement identity, not just the old secret.
    }

    /// <summary>Real first registration atomically seeds Local-first preference for only the selected store and immutable identity.</summary>
    /// <returns>A task after persisted registration, sibling/history isolation and later same-identity rotation are checked.</returns>
    [Fact]
    public async Task Owner_first_token_registration_seeds_exact_local_preference_and_rotation_preserves_it()
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((token, _) => Task.FromResult(ProbeIdentity(TelegramBotTokenIdentity.ExtractBotId(token)!.Value)));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var sibling = await SeedOwnerProbeStore(databases, provider, 60002);
        var historical = new TelegramEndpointState
        {
            BotId = tenant.Id, TelegramBotId = 60003, DesiredEndpoint = TelegramEndpointType.Cloud,
            EffectiveEndpoint = TelegramEndpointType.Local, MigrationState = TelegramEndpointMigrationState.Local,
            Trigger = "manual", Revision = 7, Generation = 9, AutoFailoverEnabled = false
        };
        var siblingEndpoint = new TelegramEndpointState
        {
            BotId = sibling.Id, TelegramBotId = 60002, DesiredEndpoint = TelegramEndpointType.Cloud,
            Trigger = "manual", Revision = 4
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramEndpointStates.AddRange(historical, siblingEndpoint);
            await db.SaveChangesAsync();
        }
        var client = new StorefrontClient();
        await SaveOwnerProbeToken(provider, client, tenant, ProbeToken(60001));
        await using (var db = databases.Users.CreateDbContext())
        {
            var saved = await db.BotInstances.SingleAsync(x => x.Id == tenant.Id);
            Assert.Equal(ProbeToken(60001), saved.Token); Assert.Equal(60001, saved.TelegramBotId);
            Assert.Equal("store_60001", saved.Username); Assert.False(saved.Enabled);
            var endpoint = await db.TelegramEndpointStates.SingleAsync(x => x.BotId == tenant.Id && x.TelegramBotId == 60001);
            Assert.Equal(TelegramEndpointType.Local, endpoint.DesiredEndpoint);
            Assert.Equal(TelegramEndpointType.Cloud, endpoint.EffectiveEndpoint);
            Assert.Equal(TelegramEndpointMigrationState.Cloud, endpoint.MigrationState);
            Assert.Equal("tenant_startup", endpoint.Trigger); Assert.True(endpoint.AutoFailoverEnabled);
            Assert.Null(endpoint.OperationId); Assert.Null(endpoint.LogoutAttemptedAtUtc);
            Assert.Equal(3, await db.TelegramEndpointStates.CountAsync());
            Assert.Equal(sibling.Token, (await db.BotInstances.SingleAsync(x => x.Id == sibling.Id)).Token);
            Assert.Equal(7, (await db.TelegramEndpointStates.SingleAsync(x => x.BotId == tenant.Id && x.TelegramBotId == 60003)).Revision);
            Assert.Equal(4, (await db.TelegramEndpointStates.SingleAsync(x => x.BotId == sibling.Id)).Revision);
            tenant = saved;
        }
        var replacement = "60001:" + new string('z', 35);
        await SaveOwnerProbeToken(provider, client, tenant, replacement);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(replacement, (await verify.BotInstances.SingleAsync(x => x.Id == tenant.Id)).Token);
        var rotated = await verify.TelegramEndpointStates.SingleAsync(x => x.BotId == tenant.Id && x.TelegramBotId == 60001);
        Assert.Equal(TelegramEndpointType.Local, rotated.DesiredEndpoint);
        Assert.Equal(TelegramEndpointType.Cloud, rotated.EffectiveEndpoint);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, rotated.MigrationState);
        Assert.Equal("tenant_startup", rotated.Trigger); Assert.Null(rotated.OperationId);
        Assert.Equal(3, await verify.TelegramEndpointStates.CountAsync());
    }

    /// <summary>Registration and subsequent token rotation never replace an exact saved Cloud or Local route, even at revision zero.</summary>
    /// <param name="local">Whether the saved authoritative route is Local instead of Cloud.</param>
    /// <param name="pending">Whether saved Local intent is waiting for safe Cloud reuse rather than an idle route.</param>
    /// <returns>A task after real owner handlers persist both secrets while preserving saved migration evidence.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Owner_token_registration_preserves_saved_exact_route_and_later_rotation(bool local, bool pending)
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((token, _) => Task.FromResult(ProbeIdentity(TelegramBotTokenIdentity.ExtractBotId(token)!.Value)));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        var savedEndpoint = new TelegramEndpointState
        {
            BotId = tenant.Id, TelegramBotId = 60001,
            DesiredEndpoint = local ? TelegramEndpointType.Local : TelegramEndpointType.Cloud,
            EffectiveEndpoint = local ? TelegramEndpointType.Local : TelegramEndpointType.Cloud,
            MigrationState = pending ? TelegramEndpointMigrationState.CloudWait :
                local ? TelegramEndpointMigrationState.Local : TelegramEndpointMigrationState.Cloud,
            Trigger = "manual", AutoFailoverEnabled = false, Revision = pending ? 8 : 0,
            ControlRevision = pending ? 3 : 0, Generation = 11,
            OperationId = pending ? new string('b', 32) : null,
            LogoutEndpoint = pending ? TelegramEndpointType.Local : null,
            LogoutAttemptedAtUtc = pending ? DateTime.UtcNow.AddMinutes(-1) : null,
            LogoutAcknowledgedAtUtc = pending ? DateTime.UtcNow : null,
            CloudReuseEligibleAtUtc = pending ? DateTime.UtcNow.AddMinutes(10) : null,
            NextAttemptAtUtc = pending ? DateTime.UtcNow.AddMinutes(10) : null
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramEndpointStates.Add(savedEndpoint);
            await db.SaveChangesAsync();
        }
        var client = new StorefrontClient();
        foreach (var secret in new[] { 'z', 'y' })
        {
            var replacement = "60001:" + new string(secret, 35);
            await SaveOwnerProbeToken(provider, client, tenant, replacement);
            await using var verify = databases.Users.CreateDbContext();
            tenant = await verify.BotInstances.SingleAsync(x => x.Id == tenant.Id);
            Assert.Equal(replacement, tenant.Token); Assert.Equal(60001, tenant.TelegramBotId); Assert.False(tenant.Enabled);
            var endpoint = await verify.TelegramEndpointStates.SingleAsync(x => x.BotId == tenant.Id && x.TelegramBotId == 60001);
            Assert.Equal(savedEndpoint.DesiredEndpoint, endpoint.DesiredEndpoint);
            Assert.Equal(savedEndpoint.EffectiveEndpoint, endpoint.EffectiveEndpoint);
            Assert.Equal(savedEndpoint.MigrationState, endpoint.MigrationState);
            Assert.Equal(savedEndpoint.Trigger, endpoint.Trigger); Assert.False(endpoint.AutoFailoverEnabled);
            Assert.Equal(savedEndpoint.Revision, endpoint.Revision); Assert.Equal(savedEndpoint.ControlRevision, endpoint.ControlRevision);
            Assert.Equal(savedEndpoint.Generation, endpoint.Generation); Assert.Equal(savedEndpoint.OperationId, endpoint.OperationId);
            Assert.Equal(savedEndpoint.LogoutEndpoint, endpoint.LogoutEndpoint);
            Assert.Equal(savedEndpoint.LogoutAttemptedAtUtc, endpoint.LogoutAttemptedAtUtc);
            Assert.Equal(savedEndpoint.LogoutAcknowledgedAtUtc, endpoint.LogoutAcknowledgedAtUtc);
            Assert.Equal(savedEndpoint.CloudReuseEligibleAtUtc, endpoint.CloudReuseEligibleAtUtc);
            Assert.Equal(savedEndpoint.NextAttemptAtUtc, endpoint.NextAttemptAtUtc);
        }
    }

    /// <summary>An endpoint insert failure atomically preserves the original bot identity and leaves registration in its token-input state.</summary>
    /// <returns>A task after a real SQLite constraint failure preserves the original identity, registry, endpoint isolation and input flow.</returns>
    [Fact]
    public async Task Owner_token_registration_endpoint_insert_failure_preserves_identity_and_input_state()
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((token, _) => Task.FromResult(ProbeIdentity(TelegramBotTokenIdentity.ExtractBotId(token)!.Value)));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        provider.GetRequiredService<BotRegistry>().Upsert(tenant);
        await using (var db = databases.Users.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_tenant_endpoint_insert BEFORE INSERT ON TelegramEndpointStates " +
                "BEGIN SELECT RAISE(ABORT, 'endpoint insert rejected'); END;");
        }
        var client = new StorefrontClient();
        await SaveOwnerProbeToken(provider, client, tenant, ProbeToken(60002));
        await AssertOwnerProbeSettingsPreserved(databases, tenant);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TelegramEndpointStates.ToListAsync());
        var registered = provider.GetRequiredService<BotRegistry>().Bots.Single(x => x.Id == tenant.Id);
        Assert.Equal(tenant.Token, registered.Token); Assert.Equal(60001, TelegramBotTokenIdentity.ExtractBotId(registered.Token));
        Assert.Equal("Token", (await provider.GetRequiredService<UserStateStore>().GetUserStatus(711)).LastStep);
    }

    /// <summary>Real enable callbacks keep first startup enabled only for the enumerated durable pending phases, never uncertainty or unrelated migration.</summary>
    /// <param name="phase">Saved endpoint phase that fences the ordinary receiver before any Telegram call.</param>
    /// <param name="trigger">Closed startup or manual provenance on the saved operation.</param>
    /// <param name="pending">Whether the owner must see queued startup with Enabled retained.</param>
    /// <returns>A task after the real runtime refusal, persisted owner enabled intent and receiver admission fencing are checked.</returns>
    [Theory]
    [InlineData(TelegramEndpointMigrationState.CheckingLocal, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.SwitchingToLocal, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.CloudLogoutPending, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.FallbackPending, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.LocalLogoutPending, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.CloudWait, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.SwitchingToCloud, "tenant_startup", true)]
    [InlineData(TelegramEndpointMigrationState.CloudLogoutUncertain, "tenant_startup", false)]
    [InlineData(TelegramEndpointMigrationState.LocalLogoutUncertain, "tenant_startup", false)]
    [InlineData(TelegramEndpointMigrationState.ManualInterventionRequired, "tenant_startup", false)]
    [InlineData(TelegramEndpointMigrationState.MigrationFailed, "tenant_startup", false)]
    [InlineData(TelegramEndpointMigrationState.CheckingLocal, "manual", false)]
    public async Task Owner_enable_preserves_only_safe_pending_first_startup(
        TelegramEndpointMigrationState phase, string trigger, bool pending)
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((_, _) => Task.FromResult(ProbeIdentity(60001)));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        tenant.Enabled = false;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.Update(tenant); await db.SaveChangesAsync();
        }
        var coordinator = provider.GetRequiredService<TelegramEndpointCoordinator>();
        // This is a running-process pending operation, not a crashed unacknowledged logout to reconcile.
        await coordinator.InitializeAsync(default);
        var endpoint = new TelegramEndpointState
        {
            BotId = tenant.Id, TelegramBotId = 60001, DesiredEndpoint = TelegramEndpointType.Local,
            EffectiveEndpoint = TelegramEndpointType.Cloud, MigrationState = phase,
            Trigger = trigger, OperationId = new string('c', 32), Revision = 2
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramEndpointStates.Add(endpoint); await db.SaveChangesAsync();
        }
        var client = new StorefrontClient();
        await provider.GetRequiredService<UserStateStore>().SaveUserStatus(new User { Id = 711, OwnerStoreId = tenant.Id });
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "set-enabled:1"));
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.BotInstances.SingleAsync(x => x.Id == tenant.Id);
        Assert.Equal(pending, saved.Enabled); Assert.Equal(tenant.Token, saved.Token);
        Assert.Equal(pending, provider.GetRequiredService<BotRegistry>().Bots.Single(x => x.Id == tenant.Id).Enabled);
        Assert.Equal(phase, (await verify.TelegramEndpointStates.SingleAsync(x =>
            x.BotId == tenant.Id && x.TelegramBotId == 60001)).MigrationState);
        Assert.False(provider.GetRequiredService<TelegramEndpointRuntimeGate>().CanReceive(tenant.Id));
    }

    /// <summary>Worker completion between runtime refusal and status observation retains enabled intent only after real receiver readiness.</summary>
    /// <param name="phase">Worker-completed usable Local, Cloud or recovered-Cloud endpoint phase.</param>
    /// <param name="workerStartedReceiver">Whether the worker already registered the receiver before the owner observes its completed state.</param>
    /// <param name="releaseGate">Whether activation publishes usable admission; false proves durable active state alone cannot claim readiness.</param>
    /// <returns>A task after the real callback's bounded handshake, consumer-visible result and duplicate-polling checks.</returns>
    [Theory]
    [InlineData(TelegramEndpointMigrationState.Local, true, true)]
    [InlineData(TelegramEndpointMigrationState.Cloud, true, true)]
    [InlineData(TelegramEndpointMigrationState.CloudRecovered, true, true)]
    [InlineData(TelegramEndpointMigrationState.Local, false, true)]
    [InlineData(TelegramEndpointMigrationState.Cloud, false, true)]
    [InlineData(TelegramEndpointMigrationState.CloudRecovered, false, true)]
    [InlineData(TelegramEndpointMigrationState.CloudRecovered, false, false)]
    public async Task Owner_enable_completed_first_startup_rechecks_real_receiver_readiness(
        TelegramEndpointMigrationState phase, bool workerStartedReceiver, bool releaseGate)
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((_, _) => Task.FromResult(ProbeIdentity(60001)));
        var polling = new PollingStorefrontClient();
        OwnerStartupCompletionStore? boundary = null;
        await using var provider = StorefrontProvider(databases, tokenProbe: probe, configureServices: services =>
        {
            services.AddSingleton<ITelegramEndpointStateStore>(sp => boundary = new OwnerStartupCompletionStore(
                sp.GetRequiredService<TelegramEndpointStore>(),
                sp.GetRequiredService<BotRegistry>(), sp.GetRequiredService<BotRuntimeStatusStore>()));
            services.AddSingleton<BotClientProvider>(sp => (BotClientProvider)Activator.CreateInstance(
                typeof(BotClientProvider), BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[]
                {
                    sp.GetRequiredService<BotRegistry>(), sp.GetRequiredService<TelegramEndpointRuntimeGate>(),
                    sp.GetRequiredService<TelegramEndpointRoutingOptions>(),
                    (Func<BotInstanceConfig, TelegramEndpointRoute, ITelegramBotClient>)((_, _) => polling), null!
                }, null)!);
        });
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001);
        tenant.Enabled = false;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.Update(tenant); await db.SaveChangesAsync();
        }
        var coordinator = provider.GetRequiredService<TelegramEndpointCoordinator>();
        await coordinator.InitializeAsync(default);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramEndpointStates.Add(new TelegramEndpointState
            {
                BotId = tenant.Id, TelegramBotId = 60001, DesiredEndpoint = TelegramEndpointType.Local,
                EffectiveEndpoint = TelegramEndpointType.Cloud, MigrationState = TelegramEndpointMigrationState.CheckingLocal,
                Trigger = "tenant_startup", OperationId = new string('d', 32), Revision = 2
            });
            await db.SaveChangesAsync();
        }
        var runtime = provider.GetRequiredService<MultiBotHostedService>();
        var gate = provider.GetRequiredService<TelegramEndpointRuntimeGate>();
        boundary!.CompleteAfterRefusal = async token =>
        {
            await using var db = databases.Users.CreateDbContext();
            var state = await db.TelegramEndpointStates.SingleAsync(x =>
                x.BotId == tenant.Id && x.TelegramBotId == 60001, token);
            state.MigrationState = phase;
            state.EffectiveEndpoint = phase == TelegramEndpointMigrationState.Local
                ? TelegramEndpointType.Local : TelegramEndpointType.Cloud;
            state.Revision++; state.Generation++;
            await db.SaveChangesAsync(token);
            if (releaseGate) gate.Publish(state);
            if (workerStartedReceiver)
                Assert.True(await runtime.StartBotAsync(tenant.Id, token));
        };
        var client = new StorefrontClient();
        try
        {
            await provider.GetRequiredService<UserStateStore>().SaveUserStatus(new User { Id = 711, OwnerStoreId = tenant.Id });
            await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "set-enabled:1"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (releaseGate) await polling.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(releaseGate ? 1 : 0, polling.Starts);
            await using var verify = databases.Users.CreateDbContext();
            Assert.Equal(releaseGate, (await verify.BotInstances.SingleAsync(x => x.Id == tenant.Id)).Enabled);
            Assert.Equal(phase, (await verify.TelegramEndpointStates.SingleAsync(x =>
                x.BotId == tenant.Id && x.TelegramBotId == 60001)).MigrationState);
            Assert.Equal(releaseGate, provider.GetRequiredService<BotRuntimeStatusStore>().GetSnapshots(
                provider.GetRequiredService<BotRegistry>().Bots).Single(x => x.BotId == tenant.Id).IsReceiverRunning);
        }
        finally { await runtime.StopAsync(default); }
    }

    /// <summary>An old authoritative failure finishing after a real replacement cannot erase or resynchronize the new bot.</summary>
    /// <returns>A task after barrier-controlled stale callback completion and invalidated old-flight cache population are checked.</returns>
    [Fact]
    public async Task Owner_panel_stale_inflight_invalid_result_cannot_mutate_replacement_or_repopulate_cache()
    {
        using var databases = new Databases(); var entered = Signal();
        var old = new TaskCompletionSource<Telegram.Bot.Types.User>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        var probe = new OwnerIdentityProbe((token, _) =>
        {
            if (Interlocked.Increment(ref first) == 1) { entered.TrySetResult(); return old.Task; }
            return Task.FromResult(ProbeIdentity(TelegramBotTokenIdentity.ExtractBotId(token)!.Value));
        });
        var cache = new TenantOwnerTokenProbeCache(probe);
        await using var provider = StorefrontProvider(databases, tokenProbe: probe, ownerTokenProbeCache: cache);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001); var client = new StorefrontClient();
        var originalCallback = OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SaveOwnerProbeToken(provider, new StorefrontClient(), tenant, "60002:" + new string('b', 35));
        old.SetException(new ApiRequestException("Unauthorized", 401));
        await originalCallback.WaitAsync(TimeSpan.FromSeconds(2));
        var saved = (await provider.GetRequiredService<TenantStoreStore>().ListAsync(711)).Single();
        Assert.Equal(60002, saved.TelegramBotId); Assert.Equal("store_60002", saved.Username); Assert.NotNull(saved.Token);
        await cache.ProbeAsync(tenant.Id, tenant.Token, default);
        Assert.Equal(4, probe.Calls);
    }

    /// <summary>Caller shutdown propagates from real owner callbacks and is not reused as a cached transient warning.</summary>
    /// <returns>A task after canceled callback, fresh retry and explicit canceled cache-hit paths are exercised.</returns>
    [Fact]
    public async Task Owner_panel_outer_cancellation_propagates_and_does_not_cache_transient_failure()
    {
        using var databases = new Databases(); var entered = Signal(); var aborted = Signal(); var attempt = 0;
        var probe = new OwnerIdentityProbe(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { aborted.TrySetResult(); }
            }
            return ProbeIdentity(60001);
        });
        var cache = new TenantOwnerTokenProbeCache(probe);
        await using var provider = StorefrontProvider(databases, tokenProbe: probe, ownerTokenProbeCache: cache);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001); var client = new StorefrontClient();
        using var cancellation = new CancellationTokenSource();
        var callback = OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "panel"));
        Assert.Equal(2, probe.Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ProbeAsync(tenant.Id, tenant.Token, cancellation.Token));
        Assert.Equal(2, probe.Calls);
    }

    /// <summary>Token registration's catch path must propagate canceled updates rather than sending a misleading validation warning.</summary>
    /// <returns>A task after the actual owner-input handler propagates cancellation and preserves the existing token.</returns>
    [Fact]
    public async Task Owner_token_registration_catch_does_not_swallow_outer_cancellation()
    {
        using var databases = new Databases(); using var cancellation = new CancellationTokenSource();
        var probe = new OwnerIdentityProbe((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<Telegram.Bot.Types.User>(new HttpRequestException("Connection reset"));
        });
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var tenant = await SeedOwnerProbeStore(databases, provider, 60001); var client = new StorefrontClient();
        await provider.GetRequiredService<UserStateStore>().SaveUserStatus(new User { Id = 711, OwnerStoreId = tenant.Id });
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "set:Token"));
        await using var scope = provider.CreateAsyncScope();
        var state = await provider.GetRequiredService<UserStateStore>().GetUserStatus(711);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerMessageAsync(client,
                new Message { From = new Telegram.Bot.Types.User { Id = 711 }, Chat = new Chat { Id = 711 }, Text = ProbeToken(60002) },
                ProbeOwner(), state,
                new ReplyKeyboardRemove(), cancellation.Token));
        await AssertOwnerProbeSettingsPreserved(databases, tenant);
    }

    /// <summary>Single-flight waiter cancellation is isolated; an active second waiter still receives and caches the verified identity.</summary>
    /// <returns>A task after concurrent waiter transitions, preserved result and cache reuse are checked.</returns>
    [Fact]
    public async Task Owner_probe_single_flight_survives_one_canceled_waiter()
    {
        var completion = new TaskCompletionSource<Telegram.Bot.Types.User>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new OwnerIdentityProbe((_, _) => completion.Task); var cache = new TenantOwnerTokenProbeCache(probe);
        using var cancellation = new CancellationTokenSource();
        var first = cache.ProbeAsync("tenant-a", ProbeToken(60001), cancellation.Token);
        var second = cache.ProbeAsync("tenant-a", ProbeToken(60001), default);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted); Assert.Equal(1, probe.Calls);
        completion.SetResult(ProbeIdentity(60001));
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await second).Status);
        Assert.Equal(60001, (await cache.ProbeAsync("tenant-a", ProbeToken(60001), default)).Identity.Id);
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>Expiry, LRU capacity, tenant isolation and secret replacement prevent unrelated or stale result reuse without sleeps.</summary>
    /// <returns>A task after deterministic key and freshness boundaries have been exercised through the production cache.</returns>
    [Fact]
    public async Task Owner_probe_cache_isolated_by_tenant_and_token_expires_and_is_capacity_bounded()
    {
        var clock = new OwnerProbeClock(); var transient = false;
        var probe = new OwnerIdentityProbe((_, _) => transient
            ? Task.FromException<Telegram.Bot.Types.User>(new HttpRequestException("Connection reset")) : Task.FromResult(ProbeIdentity(60001)));
        var cache = new TenantOwnerTokenProbeCache(probe, capacity: 2, timeProvider: clock);
        await cache.ProbeAsync("tenant-a", ProbeToken(60001), default);
        await cache.ProbeAsync("tenant-b", ProbeToken(60001), default);
        Assert.Equal(2, probe.Calls); // Sharing the secret does not share storefront health.
        await cache.ProbeAsync("tenant-a", ProbeToken(60001), default);
        await cache.ProbeAsync("tenant-a", "60001:" + new string('z', 35), default);
        Assert.Equal(3, probe.Calls);
        await cache.ProbeAsync("tenant-b", ProbeToken(60001), default);
        Assert.Equal(4, probe.Calls); // B was the least recently used retained identity.
        clock.Advance(TimeSpan.FromMinutes(3));
        await cache.ProbeAsync("tenant-b", ProbeToken(60001), default);
        Assert.Equal(5, probe.Calls);
        transient = true; cache.Invalidate("tenant-b");
        Assert.Equal(TenantOwnerTokenProbeStatus.Transient, (await cache.ProbeAsync("tenant-b", ProbeToken(60001), default)).Status);
        clock.Advance(TimeSpan.FromSeconds(19));
        await cache.ProbeAsync("tenant-b", ProbeToken(60001), default); Assert.Equal(6, probe.Calls);
        transient = false; clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TenantOwnerTokenProbeStatus.Valid, (await cache.ProbeAsync("tenant-b", ProbeToken(60001), default)).Status);
        Assert.Equal(7, probe.Calls);
    }

    /// <summary>Seeds one real owner store with identity, pricing, card and wallet-trust settings that failures must preserve.</summary>
    /// <param name="databases">Isolated migrated databases for this regression.</param>
    /// <param name="provider">Production graph with an injectable test transport.</param>
    /// <param name="botId">Distinct numeric Telegram bot identity, not the internal store id.</param>
    /// <returns>The saved detached store with a valid addressed callback revision.</returns>
    /// <remarks>No financial operations or historical rows are created by this fixture.</remarks>
    private static async Task<BotInstance> SeedOwnerProbeStore(Databases databases, ServiceProvider provider, long botId)
    {
        var tenant = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        tenant!.Token = ProbeToken(botId); tenant.TelegramBotId = botId; tenant.Username = "store_" + botId; tenant.Enabled = true;
        tenant.SupportAccount = "@owner_support"; tenant.TenantWelcomeText = "خوش آمدید 🌷"; tenant.TenantCardPaymentEnabled = true;
        tenant.TenantCardNumber = "6037997512345678"; tenant.TenantCardHolderName = "صاحب فروشگاه";
        tenant.TenantPricingMode = TenantPricingModes.Manual; tenant.TenantNormalPricePerGbToman = 4200;
        tenant.TenantNormalPricePerDayToman = 600; tenant.TenantNationalPricePerGbToman = 110000;
        tenant.TenantUnlimitedPlanPricesJson = "{\"unlimited\":{\"u2-m1\":256000}}";
        tenant.TenantCustomerWalletEnabled = true; tenant.TenantCustomerWalletOwnerEnabled = true;
        tenant.TenantCustomerWalletApprovedBotId = botId; tenant.TenantCustomerWalletApprovedOwnerId = 711;
        tenant.TenantCustomerWalletApprovedByTelegramUserId = 85758085; tenant.TenantCustomerWalletApprovedAtUtc = DateTime.UtcNow;
        await using var db = databases.Users.CreateDbContext(); db.Update(tenant); await db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>Asserts the consumer-visible transient-failure invariants against a freshly read database row.</summary>
    /// <param name="databases">Isolated users.db factory.</param>
    /// <param name="expected">Saved storefront state before the probe.</param>
    /// <returns>A task after identity, enabled, settings, price and wallet-approval checks.</returns>
    /// <remarks>Does not attach expected snapshots or mutate any row.</remarks>
    private static async Task AssertOwnerProbeSettingsPreserved(Databases databases, BotInstance expected)
    {
        await using var db = databases.Users.CreateDbContext(); var saved = await db.BotInstances.SingleAsync(x => x.Id == expected.Id);
        Assert.Equal(expected.Token, saved.Token); Assert.Equal(expected.TelegramBotId, saved.TelegramBotId); Assert.Equal(expected.Username, saved.Username);
        Assert.True(saved.Enabled); Assert.Equal(expected.BrandName, saved.BrandName); Assert.Equal(expected.SupportAccount, saved.SupportAccount);
        Assert.Equal(expected.TenantWelcomeText, saved.TenantWelcomeText); Assert.True(saved.TenantCardPaymentEnabled);
        Assert.Equal(expected.TenantCardNumber, saved.TenantCardNumber); Assert.Equal(expected.TenantCardHolderName, saved.TenantCardHolderName);
        Assert.Equal(expected.TenantPricingMode, saved.TenantPricingMode); Assert.Equal(expected.TenantNormalPricePerGbToman, saved.TenantNormalPricePerGbToman);
        Assert.Equal(expected.TenantUnlimitedPlanPricesJson, saved.TenantUnlimitedPlanPricesJson);
        Assert.True(saved.TenantCustomerWalletEnabled); Assert.True(saved.TenantCustomerWalletOwnerEnabled);
        Assert.Equal(expected.TenantCustomerWalletApprovedBotId, saved.TenantCustomerWalletApprovedBotId);
    }

    /// <summary>Starts the real addressed token-input callback and consumes its next message in a fresh handler scope.</summary>
    /// <param name="provider">Production graph with controlled getMe.</param>
    /// <param name="client">Recording owner-facing transport.</param>
    /// <param name="tenant">Current saved store and callback revision.</param>
    /// <param name="token">Synthetic replacement secret for the test only.</param>
    /// <returns>A task after production registration and its refreshed owner panel.</returns>
    /// <remarks>Persists the explicit selected-store prerequisite, then exercises the real addressed callback and token-input state rather than invoking private save methods.</remarks>
    /// <example><code>await SaveOwnerProbeToken(provider, client, selected, ProbeToken(60002));</code></example>
    private static async Task SaveOwnerProbeToken(ServiceProvider provider, StorefrontClient client, BotInstance tenant, string token)
    {
        await provider.GetRequiredService<UserStateStore>().SaveUserStatus(new User { Id = 711, OwnerStoreId = tenant.Id });
        await OwnerCallback(provider, client, ProbeOwner(), TenantOwnerCallback.Encode(tenant, "set:Token"));
        await using var scope = provider.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerMessageAsync(client,
            new Message { From = new Telegram.Bot.Types.User { Id = 711 }, Chat = new Chat { Id = 711 }, Text = token },
            ProbeOwner(), await provider.GetRequiredService<UserStateStore>().GetUserStatus(711), new ReplyKeyboardRemove(), default));
    }

    /// <summary>Returns the authenticated colleague used by the owner-management regressions.</summary>
    /// <returns>A test-only profile; no wallet effects are implied.</returns>
    private static CredUser ProbeOwner() => new() { TelegramUserId = 711, IsColleague = true };

    /// <summary>Builds a syntactically valid synthetic Telegram token without production credentials.</summary>
    /// <param name="botId">Synthetic numeric Telegram bot id.</param>
    /// <returns>A private test secret, never emitted in diagnostics.</returns>
    private static string ProbeToken(long botId) => botId + ":" + new string('a', 35);

    /// <summary>Builds Telegram-shaped verified bot identity for an injected probe.</summary>
    /// <param name="botId">Synthetic Telegram numeric bot identity.</param>
    /// <param name="username">Optional verified username without a leading at sign.</param>
    /// <param name="brand">Verified first name used only to fill a missing storefront brand.</param>
    /// <returns>A synthetic bot identity, never a persisted settings entity.</returns>
    private static Telegram.Bot.Types.User ProbeIdentity(long botId, string? username = null, string brand = "Verified brand") =>
        new() { Id = botId, IsBot = true, Username = username ?? "store_" + botId, FirstName = brand };

    /// <summary>Wraps real SQLite persistence with one deterministic running-worker completion at the post-refusal status boundary.</summary>
    /// <param name="inner">Real detached endpoint persistence against isolated users.db.</param>
    /// <param name="registry">Actual registry used to find the owner-enabled tenant.</param>
    /// <param name="statuses">Actual runtime observations proving the first receiver attempt was fenced.</param>
    private sealed class OwnerStartupCompletionStore(
        ITelegramEndpointStateStore inner, BotRegistry registry, BotRuntimeStatusStore statuses) : ITelegramEndpointStateStore
    {
        /// <summary>One controlled worker completion, consumed before invoking it so nested startup reads cannot replay it.</summary>
        public Func<CancellationToken, Task>? CompleteAfterRefusal { get; set; }
        /// <inheritdoc />
        public async Task<TelegramEndpointState> GetOrCreateAsync(string botId, long identity, CancellationToken token)
        {
            if (CompleteAfterRefusal is { } complete && statuses.GetSnapshots(registry.Bots).Any(x =>
                x.BotId == botId && x.Status == "skipped" && x.LastError == "endpoint migration pending"))
            {
                CompleteAfterRefusal = null;
                await complete(token);
            }
            return await inner.GetOrCreateAsync(botId, identity, token);
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointState>> ReadAllAsync(CancellationToken token) => inner.ReadAllAsync(token);
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointState>> ReadIdentityStatesAsync(long identity, CancellationToken token) =>
            inner.ReadIdentityStatesAsync(identity, token);
        /// <inheritdoc />
        public Task<bool> TrySaveAsync(TelegramEndpointState state, long expectedRevision, string historyReason = null!,
            string alertCategory = null!, CancellationToken token = default) =>
            inner.TrySaveAsync(state, expectedRevision, historyReason, alertCategory, token);
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointHistory>> ReadHistoryAsync(string botId, long identity, int limit,
            CancellationToken token) => inner.ReadHistoryAsync(botId, identity, limit, token);
        /// <inheritdoc />
        public Task<int> CountPendingAlertsAsync(CancellationToken token) => inner.CountPendingAlertsAsync(token);
    }

    /// <summary>Controlled getMe transport with a thread-safe call counter and no sockets.</summary>
    /// <param name="handler">Controlled transport behavior; may use barriers but must never open a socket.</param>
    private sealed class OwnerIdentityProbe(Func<string, CancellationToken, Task<Telegram.Bot.Types.User>> handler) : ITelegramTokenProbe
    {
        /// <summary>Total underlying calls, distinguishing reuse and deduplication from fresh Telegram attempts.</summary>
        public int Calls;
        /// <inheritdoc />
        public Task<Telegram.Bot.Types.User> GetMeAsync(string token, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return handler(token, cancellationToken); }
    }

    /// <summary>Explicitly advanced clock and deadline timers; probe timeout and cache expiry never require sleeps.</summary>
    private sealed class OwnerProbeClock : TimeProvider
    {
        /// <summary>Synchronizes controlled timers with their async disposal after a probe completes.</summary>
        private readonly object _sync = new();
        /// <summary>Timers registered by production cancellation sources, retained only for the lifetime of this test.</summary>
        private readonly List<OwnerProbeTimer> _timers = [];
        /// <summary>Controlled UTC instant for expiry and timer decisions.</summary>
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new OwnerProbeTimer(this, callback, state);
            lock (_sync) { timer.Change(dueTime, period); _timers.Add(timer); }
            return timer;
        }
        /// <summary>Advances virtual time and synchronously triggers due cancellation callbacks.</summary>
        /// <param name="duration">Nonnegative virtual duration for deadline or expiration boundaries.</param>
        /// <remarks>Callbacks execute outside the timer-list lock and may complete an awaiting handler asynchronously.</remarks>
        /// <example><code>clock.Advance(TimeSpan.FromMilliseconds(40));</code></example>
        public void Advance(TimeSpan duration)
        {
            OwnerProbeTimer[] timers; DateTimeOffset now;
            lock (_sync) { _now += duration; now = _now; timers = _timers.ToArray(); }
            foreach (var timer in timers) timer.Trigger(now);
        }

        /// <summary>Controlled timer that delivers the actual cancellation source callback only after virtual advancement.</summary>
        /// <param name="clock">Owning test clock and synchronization boundary.</param>
        /// <param name="callback">Production deadline cancellation callback.</param>
        /// <param name="state">Private callback state supplied by the cancellation source.</param>
        private sealed class OwnerProbeTimer(OwnerProbeClock clock, TimerCallback callback, object? state) : ITimer
        {
            /// <summary>Next due instant, or null for a disabled/disposed timer.</summary>
            private DateTimeOffset? _due;
            /// <summary>Configured repeat period; deadline cancellation uses an infinite period.</summary>
            private TimeSpan _period;
            /// <summary>Whether production released the timer after probe completion.</summary>
            private bool _disposed;
            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._sync)
                {
                    if (_disposed) return false;
                    _period = period;
                    _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                    return true;
                }
            }
            /// <summary>Delivers a due callback once, respecting disposal and the configured period.</summary>
            /// <param name="now">New virtual UTC instant after the owning clock advances.</param>
            /// <remarks>Executes the callback outside the clock lock to permit cancellation and async timer disposal.</remarks>
            public void Trigger(DateTimeOffset now)
            {
                lock (clock._sync)
                {
                    if (_disposed || !_due.HasValue || _due.Value > now) return;
                    _due = _period > TimeSpan.Zero ? now + _period : null;
                }
                callback(state);
            }
            /// <inheritdoc />
            public void Dispose() { lock (clock._sync) { _disposed = true; _due = null; } }
            /// <inheritdoc />
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
