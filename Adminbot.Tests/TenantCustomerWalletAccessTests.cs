using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Store approval gates one global balance; concurrent duplicate admissions reuse a durable order and debit.</summary>
    /// <returns>A task completing after cross-store and competing confirmation transactions.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Cross_store_admission_and_duplicate_confirmation_fail_closed()
    {
        using var databases = new Databases();
        var (wallet, funding, first) = await SeedCustomerWalletOrderAsync(databases);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.AddRange(new BotInstance { Id = "tenant-b", Type = "tenant", TelegramBotId = 790, OwnerTelegramUserId = 457 },
                new BotInstance { Id = "tenant-c", Type = "tenant", TelegramBotId = 791, OwnerTelegramUserId = 458 });
            await db.SaveChangesAsync();
        }
        var policy = new TenantCustomerWalletPolicy(databases.Users, new AppConfig { AdminsUserIds = new() { 999 } }, NullLogger<TenantCustomerWalletPolicy>.Instance);
        await policy.SetAsync(999, "tenant-c", 791, 458, true);
        await policy.SetOwnerEnabledAsync(458, "tenant-c", true);
        TenantBotOrder Candidate(string bot, long owner) => new() { TenantBotId = bot, OwnerTelegramUserId = owner,
            CustomerTelegramUserId = 123, CustomerChatId = 123, OrderId = Guid.NewGuid().ToString("N"),
            ServiceKey = "normal", SalePriceToman = 100000, BaseCostToman = 80000, ProfitToman = 20000 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => funding.AdmitAsync(Candidate("tenant-b", 457), "blocked"));
        Assert.True(await funding.DebitAsync(first));
        var start = Signal();
        var admissions = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        { await start.Task; return await funding.AdmitAsync(Candidate("tenant-c", 458), "same-confirmation"); })).ToArray();
        start.SetResult(); var orders = await Task.WhenAll(admissions);
        Assert.Equal(orders[0].Id, orders[1].Id);
        Assert.All(await Task.WhenAll(orders.Select(x => funding.DebitAsync(x))), Assert.True);
        Assert.Equal(300000, await wallet.GetAccountBalance(123));
        var unfunded = await funding.AdmitAsync(Candidate("tenant-c", 458), "stale-confirmation");
        await policy.SetAsync(999, "tenant-c", 791, 458, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => funding.DebitAsync(unfunded));
        Assert.Null(await wallet.GetWalletOperationAsync(TenantCustomerWalletFunding.DebitKey(unfunded.Id)));
        Assert.Equal(300000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(1, await verify.TenantBotOrders.CountAsync(x => x.CustomerWalletAdmissionKey == "same-confirmation"));
    }

    /// <summary>Real wallet menus and admin callbacks require fresh approval, actor authority and the displayed store revision.</summary>
    /// <returns>A task completing after recorded Telegram UI and persisted authorization assertions.</returns>
    [Fact]
    public async Task TenantCustomerWallet_UI_admin_confirmation_and_stale_customer_actions_are_guarded()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder().AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:1"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["adminsUserIds:0"] = "999" }).Build();
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        var (wallet, _, _) = await SeedCustomerWalletOrderAsync(databases);
        var client = new StorefrontClient();
        var context = new BotContextAccessor();
        var state = provider.GetRequiredService<UserStateStore>();
        Update Callback(long actor, string data) => new() { CallbackQuery = new CallbackQuery { Id = Guid.NewGuid().ToString("N"),
            Data = data, From = new Telegram.Bot.Types.User { Id = actor }, Message = new Message { Id = 17, Chat = new Chat { Id = actor } } } };
        var policy = provider.GetRequiredService<TenantCustomerWalletPolicy>();
        await using var scope = provider.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var handler = typeof(TenantBotService).GetMethod("TryHandleCustomerWalletAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var keyboard = typeof(TenantBotService).GetMethod("BuildTenantReplyKeyboardForStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (context.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "tenant-a", Type = "tenant" }, Client = client }))
        {
            var approved = await policy.RequireAsync("tenant-a");
            var menu = (ReplyKeyboardMarkup)keyboard.Invoke(tenant, new object[] { approved })!;
            Assert.Contains(menu.Keyboard.SelectMany(x => x), x => x.Text == "💰 کیف پول");
            var customer = await wallet.GetUserStatusWithId(123);
            async Task Route(Update update) => await (Task<bool>)handler.Invoke(tenant, new object[] { client, update, customer, await state.GetUserStatus(123), CancellationToken.None })!;
            await Route(Callback(123, "TCW:home")); Assert.Contains(client.Texts, x => x.Contains("500,000"));
            Assert.Contains("TCW:h:0", client.Callbacks); Assert.Contains("TCW:charge", client.Callbacks);
            await Route(Callback(123, "TCW:h:0")); Assert.Contains(client.Texts, x => x.Contains("تراکنش‌های کیف پول"));
            await Route(Callback(123, "TCW:charge"));
            await Route(new Update { Message = new Message { From = new Telegram.Bot.Types.User { Id = 123 }, Chat = new Chat { Id = 123 }, Text = "100000" } });
            Assert.DoesNotContain(client.Callbacks, x => x.Contains("CARD", StringComparison.OrdinalIgnoreCase));
            await policy.SetAsync(999, "tenant-a", 789, 456, false);
            var count = client.Callbacks.Count;
            await Route(Callback(123, "TCW:charge")); Assert.Equal(count, client.Callbacks.Count);
            Assert.Contains(client.Answers, x => x == "کیف پول مشتری در این فروشگاه فعال نیست.");
            await using var db = databases.Users.CreateDbContext();
            menu = (ReplyKeyboardMarkup)keyboard.Invoke(tenant, new object[] { await db.BotInstances.SingleAsync() })!;
            Assert.DoesNotContain(menu.Keyboard.SelectMany(x => x), x => x.Text.Contains("کیف پول") || x.Text.Contains("تراکنش"));
        }
        using (context.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "main", Type = "owned" }, Client = client }))
        {
            await tenant.TryHandleWalletAdminAsync(client, Callback(456, "TWA:o:456:1"), default);
            Assert.Equal("دسترسی مجاز نیست.", client.Texts.Last());
            await tenant.TryHandleWalletAdminAsync(client, Callback(999, "TWA:o:456:1"), default);
            var confirm = client.Callbacks.Last(x => x.StartsWith("TWA:y:"));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(confirm) <= 64);
            await tenant.TryHandleWalletAdminAsync(client, Callback(999, confirm), default);
            await using (var grantDb = databases.Users.CreateDbContext())
            {
                var granted = await grantDb.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-a");
                Assert.True(TenantCustomerWalletPolicy.HasValidGrant(granted));
                Assert.False(granted.TenantCustomerWalletOwnerEnabled);
                Assert.False(TenantCustomerWalletPolicy.IsApproved(granted));
            }
            await policy.SetOwnerEnabledAsync(456, "tenant-a", true);
            Assert.True(TenantCustomerWalletPolicy.IsApproved(await policy.RequireAsync("tenant-a")));
            await tenant.TryHandleWalletAdminAsync(client, Callback(999, "TWA:o:456:1"), default);
            var stale = client.Callbacks.Last(x => x.StartsWith("TWA:y:"));
            await policy.SetAsync(999, "tenant-a", 789, 456, false);
            await tenant.TryHandleWalletAdminAsync(client, Callback(999, stale), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => policy.RequireAsync("tenant-a"));
        }
        Assert.Equal(500000, await wallet.GetAccountBalance(123));
    }

    /// <summary>The real tenant-owner callback cannot enable customer wallet before a super-admin grant.</summary>
    /// <returns>A task completing after blocked, granted, active and revoked owner-panel transitions.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Owner_panel_toggle_requires_live_super_admin_grant()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var store = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        await using (var db = databases.Users.CreateDbContext())
        {
            var tracked = await db.BotInstances.SingleAsync(x => x.Id == store.Id);
            tracked.TelegramBotId = 812345;
            tracked.Enabled = true;
            await db.SaveChangesAsync();
            store = tracked;
        }

        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var client = new StorefrontClient();
        var policy = new TenantCustomerWalletPolicy(databases.Users,
            new AppConfig { AdminsUserIds = new() { 999 } }, NullLogger<TenantCustomerWalletPolicy>.Instance);

        using (new BotContextAccessor().Push(new BotRuntimeContext
               { Config = new BotInstanceConfig { Id = "owned-wallet-panel", Type = BotInstanceTypes.Owned }, Client = client }))
        {
            await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"));
            await OwnerCallback(provider, client, owner, await FreshWalletToggleAsync(databases, store.Id, true));
            Assert.Contains(client.Answers, x => x.Contains("سوپرادمین", StringComparison.Ordinal));
            await using (var blockedDb = databases.Users.CreateDbContext())
                Assert.False((await blockedDb.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id)).TenantCustomerWalletOwnerEnabled);

            await policy.SetAsync(999, store.Id, 812345, 711, true);
            await OwnerCallback(provider, client, owner, await FreshWalletToggleAsync(databases, store.Id, true));
            await using (var activeDb = databases.Users.CreateDbContext())
            {
                var active = await activeDb.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id);
                Assert.True(active.TenantCustomerWalletOwnerEnabled);
                Assert.True(TenantCustomerWalletPolicy.IsApproved(active));
            }

            await policy.SetAsync(999, store.Id, 812345, 711, false);
            await OwnerCallback(provider, client, owner, await FreshWalletToggleAsync(databases, store.Id, true));
            await using var revokedDb = databases.Users.CreateDbContext();
            var revoked = await revokedDb.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id);
            Assert.False(revoked.TenantCustomerWalletOwnerEnabled);
            Assert.False(TenantCustomerWalletPolicy.IsApproved(revoked));
        }
    }

    private static async Task<string> FreshWalletToggleAsync(Databases databases, string storeId, bool enabled)
    {
        await using var db = databases.Users.CreateDbContext();
        var store = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == storeId);
        return TenantOwnerCallback.Encode(store, "set-setting:wallet:" + (enabled ? 1 : 0));
    }
}
