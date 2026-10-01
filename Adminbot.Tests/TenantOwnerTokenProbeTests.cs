using Adminbot.Domain;
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
        Assert.Contains("فعلاً امکان بررسی توکن", client.Texts.Last());
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
        Assert.Contains("فعلاً امکان بررسی توکن", client.Texts.Last());
        Assert.DoesNotContain("از تنظیمات پاک شد", client.Texts.Last());
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
        Assert.Contains("از تنظیمات پاک شد", client.Texts.Last());
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
        Assert.Equal(1, probe.Calls); Assert.Contains("@fresh_store", client.Texts.Last());
        Assert.DoesNotContain("فعلاً امکان بررسی توکن", client.Texts.Last());
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
        Assert.DoesNotContain("از تنظیمات پاک شد", client.Texts.Last());
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
        Assert.Equal(2, probe.Calls); Assert.DoesNotContain("فعلاً امکان بررسی توکن", client.Texts.Last());
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
        Assert.DoesNotContain(client.Texts, text => text.Contains("اعتبارسنجی توکن ناموفق بود"));
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
    /// <remarks>Exercises persisted owner selection and input state rather than invoking private save methods.</remarks>
    /// <example><code>await SaveOwnerProbeToken(provider, client, selected, ProbeToken(60002));</code></example>
    private static async Task SaveOwnerProbeToken(ServiceProvider provider, StorefrontClient client, BotInstance tenant, string token)
    {
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
