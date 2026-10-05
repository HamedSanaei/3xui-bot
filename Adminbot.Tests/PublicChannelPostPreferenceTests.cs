using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

/// <summary>Protects persisted per-store public-channel consent through migration and real owner callbacks.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Upgrading legacy rows enrolls stores without altering identity/settings; explicit false survives EF and runtime reload.</summary>
    /// <returns>A task after historical migration, raw-SQL/EF inserts and detached registry reads are checked.</returns>
    /// <remarks>Regression: EF's generated boolean default must not replace an explicit false with true.</remarks>
    [Fact]
    public async Task Public_channel_posts_preference_upgrade_preserves_legacy_data_and_explicit_opt_out()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261001120000_AddColleagueTrialGrants");
        const string insert = """
            INSERT INTO BotInstances (Id, Type, OwnerTelegramUserId, Enabled, IsDefault, TenantPriceMarkupPercent,
                TenantMandatoryJoinEnabled, TenantChannelIdsJson, TenantCardPaymentEnabled, TenantHooshPayEnabled,
                TenantNowPaymentsEnabled, CreatedAtUtc)
            VALUES ({0}, 'tenant', 711, 0, 0, 23, 0, '["@legacy_channel"]', 0, 1, 1, '2025-01-01 00:00:00');
            """;
        await db.Database.ExecuteSqlRawAsync(insert, "legacy");
        await db.Database.MigrateAsync();
        var legacy = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "legacy");
        Assert.True(legacy.TenantPublicChannelPostsEnabled);
        Assert.Equal(711, legacy.OwnerTelegramUserId);
        Assert.Equal(23, legacy.TenantPriceMarkupPercent);
        Assert.Equal("[\"@legacy_channel\"]", legacy.TenantChannelIdsJson);
        Assert.False(legacy.Enabled);
        await db.Database.ExecuteSqlRawAsync(insert, "omitted");
        db.BotInstances.Add(new BotInstance { Id = "ef-default", Type = BotInstanceTypes.Tenant });
        db.BotInstances.Add(new BotInstance { Id = "ef-false", Type = BotInstanceTypes.Tenant, TenantPublicChannelPostsEnabled = false });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True((await db.BotInstances.SingleAsync(x => x.Id == "omitted")).TenantPublicChannelPostsEnabled);
        Assert.True((await db.BotInstances.SingleAsync(x => x.Id == "ef-default")).TenantPublicChannelPostsEnabled);
        var optedOut = await db.BotInstances.SingleAsync(x => x.Id == "ef-false");
        optedOut.Token = "88001:" + new string('a', 35);
        optedOut.SupportAccount = "@support_changed";
        await db.SaveChangesAsync();
        var reset = typeof(TenantBotService).GetMethod("ResetTenantStorefrontSettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        reset.Invoke(null, new object[] { optedOut, false });
        Assert.False(optedOut.TenantPublicChannelPostsEnabled);
        reset.Invoke(null, new object[] { optedOut, true });
        await db.SaveChangesAsync();
        var registry = new BotRegistry(IncidentConfiguration());
        await registry.LoadTenantBotsFromDatabaseAsync(db);
        Assert.False(registry.Bots.Single(x => x.Id == "ef-false").TenantPublicChannelPostsEnabled);
        registry.Upsert(optedOut);
        Assert.False(registry.Bots.Single(x => x.Id == "ef-false").TenantPublicChannelPostsEnabled);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    /// <summary>Actual owner callbacks change only the selected store and reject actor, expiry, revision and selection replays.</summary>
    /// <returns>A task after isolated scoped owner callbacks and persistent consent/activation invariants are checked.</returns>
    /// <remarks>The independent consent toggle must not enable a store or change forced-join enforcement.</remarks>
    [Fact]
    public async Task Public_channel_posts_owner_toggle_is_selected_store_scoped_and_replay_safe()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var creationKey = Guid.NewGuid().ToString("N");
        var a = await stores.CreateAsync(711, creationKey);
        var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var client = new StorefrontClient();
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = "owned-a" }, Client = client });
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "panel"));
        var disable = TenantOwnerCallback.Encode(a, "set-setting:channelposts:0");
        await OwnerCallback(provider, client, owner, disable);
        a = (await stores.ListAsync(711))[0];
        Assert.False(a.TenantPublicChannelPostsEnabled);
        Assert.True((await stores.ListAsync(711))[1].TenantPublicChannelPostsEnabled);
        Assert.False(a.Enabled);
        Assert.False(a.TenantMandatoryJoinEnabled);
        Assert.False((await stores.CreateAsync(711, creationKey)).TenantPublicChannelPostsEnabled);
        var savedRevision = a.UpdatedAtUtc;
        await OwnerCallback(provider, client, owner, disable);
        var expired = TenantOwnerCallback.Encode(a, "set-setting:channelposts:1").Split(':');
        expired[3] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1200).ToString("X");
        await OwnerCallback(provider, client, owner, string.Join(':', expired));
        await OwnerCallback(provider, client, new CredUser { TelegramUserId = 999, IsColleague = true },
            TenantOwnerCallback.Encode(a, "set-setting:channelposts:1"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(b, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "set-setting:channelposts:1"));
        Assert.False((await stores.ListAsync(711))[0].TenantPublicChannelPostsEnabled);
        Assert.Equal(savedRevision, (await stores.ListAsync(711))[0].UpdatedAtUtc);
        Assert.True((await stores.ListAsync(711))[1].TenantPublicChannelPostsEnabled);
        a = (await stores.ListAsync(711))[0];
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "set-setting:channelposts:1"));
        Assert.True((await stores.ListAsync(711))[0].TenantPublicChannelPostsEnabled);
    }

    /// <summary>Changing consent on a token-bearing store never invokes token validation or mutates activation.</summary>
    /// <returns>A task after both directions use actual owner callbacks and leave identity/activation intact.</returns>
    /// <remarks>A failing token probe would clean up identity; consent changes must not depend on Telegram availability.</remarks>
    [Fact]
    public async Task Public_channel_posts_owner_toggle_does_not_probe_or_reset_token_identity()
    {
        using var databases = new Databases();
        var probe = new OwnerIdentityProbe((_, _) => Task.FromException<Telegram.Bot.Types.User>(
            new Telegram.Bot.Exceptions.ApiRequestException("Unauthorized", 401)));
        await using var provider = StorefrontProvider(databases, tokenProbe: probe);
        var store = await SeedOwnerProbeStore(databases, provider, 60001);
        var state = provider.GetRequiredService<UserStateStore>();
        var owner = ProbeOwner();
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var client = new StorefrontClient();
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = "owned-a" }, Client = client });
        await state.SaveUserStatus(new User { Id = 711, OwnerStoreId = store.Id });
        foreach (var enabled in new[] { false, true })
        {
            await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store,
                $"set-setting:channelposts:{(enabled ? 1 : 0)}"));
            store = (await provider.GetRequiredService<TenantStoreStore>().ListAsync(711)).Single();
            Assert.Equal(enabled, store.TenantPublicChannelPostsEnabled);
            Assert.True(store.Enabled);
            Assert.Equal(60001, store.TelegramBotId);
            Assert.Equal(ProbeToken(60001), store.Token);
        }
        Assert.Equal(0, probe.Calls);
    }
}
