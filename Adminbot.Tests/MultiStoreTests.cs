using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>Real SQLite regressions for owner-shared money and explicitly addressed storefront management.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Two owned bots cannot race past five stores, and redelivery or resetting cannot allocate another slot.</summary>
    /// <returns>A task completing after independent allocator connections have committed and been checked.</returns>
    [Fact]
    public async Task Multiple_owned_bots_share_atomic_store_limit_and_allocation_identity()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder().Build();
        var allocators = new[] { new TenantStoreStore(databases.Users, configuration), new TenantStoreStore(databases.Users, configuration) };
        var start = Signal();
        var keys = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var tasks = keys.Select((key, i) => Task.Run(async () => { await start.Task; return await allocators[i % 2].CreateAsync(711, key); })).ToArray();
        start.SetResult();
        var rows = (await Task.WhenAll(tasks)).Where(x => x != null).ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, rows.Select(x => x.TenantStoreNumber).Order());
        Assert.Equal(5, rows.Select(x => x.Id).Distinct().Count());
        Assert.All(rows, x => Assert.StartsWith("tenant-711-", x.Id));
        var replay = await allocators[1].CreateAsync(711, rows[0].TenantCreationKey);
        Assert.Equal(rows[0].Id, replay.Id);
        var reduced = new TenantStoreStore(databases.Users, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TenantMaxStoresPerOwner"] = "1" }).Build());
        Assert.Null(await reduced.CreateAsync(711, Guid.NewGuid().ToString("N")));
        Assert.Equal(5, (await reduced.ListAsync(711)).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => TenantStoreStore.ValidateConfiguration(new AppConfig { TenantMaxStoresPerOwner = 0 }));
    }

    /// <summary>Upgrade assigns number one without renaming the deployed store or replaying financial history.</summary>
    /// <returns>A task completing after both real migration histories and the preserved wallet have been checked.</returns>
    [Fact]
    public async Task Store_migration_preserves_old_ids_balances_and_new_model_has_no_drift()
    {
        using var databases = new Databases(initialize: false);
        await using var users = databases.Users.CreateDbContext();
        await using var credentials = databases.Credentials.CreateDbContext();
        await credentials.Database.MigrateAsync();
        var wallet = new CredentialsStore(databases.Credentials);
        await wallet.AddEmptyUser(711);
        await wallet.MutateWalletAsync(711, 45000, "historical-fixture");
        var migrations = users.Database.GetMigrations().ToArray();
        var multiStoreIndex = Array.FindIndex(migrations, x => x.EndsWith("_MultipleOwnerStorefronts", StringComparison.Ordinal));
        Assert.True(multiStoreIndex > 0);
        await users.GetService<IMigrator>().MigrateAsync(migrations[multiStoreIndex - 1]);
        await users.Database.ExecuteSqlRawAsync("""
            INSERT INTO BotInstances (Id, Type, OwnerTelegramUserId, Enabled, IsDefault, TenantPriceMarkupPercent,
                TenantMandatoryJoinEnabled, TenantCardPaymentEnabled, TenantHooshPayEnabled, TenantNowPaymentsEnabled, CreatedAtUtc)
            VALUES ('tenant-711', 'tenant', 711, 0, 0, 23, 0, 0, 1, 1, '2025-01-01 00:00:00');
            """);
        // The legacy order must be inserted with only the columns that existed at this intermediate migration.
        // Using the current EF model here would also write later columns such as AtlasPayPaymentInfoId that the
        // intermediate schema does not have yet.
        await users.Database.ExecuteSqlRawAsync("""
            INSERT INTO TenantBotOrders (OrderId, TenantBotId, OwnerTelegramUserId, CustomerTelegramUserId,
                CustomerChatId, AccountCount, IsFulfilled, IsOwnerCredited, OwnerWalletDelta,
                PaymentProvider, PaymentStatus, SalePriceToman, BaseCostToman, ProfitToman,
                CreatedAccountEmail, CreatedAtUtc)
            VALUES ('legacy-pending-settlement', 'tenant-711', 711, 0, 0, 1, 0, 0, 0,
                'tenant_card', 'pending', 0, 500, 0, 'legacy-created', '2025-01-01 00:00:00');
            """);
        await users.Database.MigrateAsync();
        var old = await users.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-711");
        Assert.Equal("tenant-711", old.Id); Assert.Equal(1, old.TenantStoreNumber); Assert.Equal(23, old.TenantPriceMarkupPercent);
        var next = await new TenantStoreStore(databases.Users, new ConfigurationBuilder().Build()).CreateAsync(711, Guid.NewGuid().ToString("N"));
        Assert.Equal("tenant-711-2", next.Id); Assert.Equal(0, next.TenantPriceMarkupPercent);
        Assert.Equal(45000, await wallet.GetAccountBalance(711)); Assert.Single(await credentials.WalletOperations.ToListAsync());
        Assert.Equal("review", (await users.Set<TenantWalletRoute>().SingleAsync()).Source);
        Assert.False(users.Database.HasPendingModelChanges()); Assert.False(credentials.Database.HasPendingModelChanges());
    }

    /// <summary>Owner buttons and persisted text target the selected store, including after a new service scope simulates restart.</summary>
    /// <returns>A task completing after writes, stale-button rejection and ownership checks.</returns>
    [Fact]
    public async Task Store_selection_survives_restart_and_old_A_button_cannot_edit_B()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var state = provider.GetRequiredService<UserStateStore>();
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-a" }, Client = client });
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "set:WELCOME"));
        Assert.Equal(a.Id, (await state.GetUserStatus(711)).OwnerStoreId);
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(b, "panel"));
        Assert.True(string.IsNullOrEmpty((await state.GetUserStatus(711)).Flow));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "set:WELCOME"));
        Assert.True(string.IsNullOrEmpty((await state.GetUserStatus(711)).Flow));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(b, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(b, "set:WELCOME"));
        // New execution scope must recover B from the database rather than any service field.
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerMessageAsync(client,
                new Message { From = new Telegram.Bot.Types.User { Id = 711 }, Chat = new Chat { Id = 711 }, Text = "خوش آمدید 🌷" },
                owner, await state.GetUserStatus(711), new ReplyKeyboardRemove(), default);
        var saved = await stores.ListAsync(711);
        Assert.Null(saved[0].TenantWelcomeText); Assert.Equal("خوش آمدید 🌷", saved[1].TenantWelcomeText);
        await OwnerCallback(provider, client, owner, "TBM:set-setting:card:1:0:0");
        Assert.False((await stores.ListAsync(711))[1].TenantCardPaymentEnabled);
        await OwnerCallback(provider, client, new CredUser { TelegramUserId = 999, IsColleague = true }, TenantOwnerCallback.Encode(b, "panel"));
        Assert.DoesNotContain(client.Texts.TakeLast(1), x => x.Contains("خوش آمدید"));
        Assert.Contains(client.Texts, x => x.Contains("فروشگاه 2"));
        Assert.All(client.Callbacks, x => Assert.InRange(Encoding.UTF8.GetByteCount(x), 1, 64));
    }

    /// <summary>Customer state and broadcast recipients remain partitioned by store even for the same owner and customer.</summary>
    /// <returns>A task completing after state and actual broadcast-audience queries.</returns>
    [Fact]
    public async Task Shared_owner_stores_keep_customer_state_and_broadcast_audiences_separate()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N")); var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var states = new UserStateStore(databases.Users); var accessor = new BotContextAccessor();
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = a.Id } }))
        { await states.SaveUserStatus(new User { Id = 10, Flow = "purchase-a" }); await states.SaveUserStatus(new User { Id = 11 }); }
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = b.Id } }))
        { await states.SaveUserStatus(new User { Id = 10, Flow = "purchase-b" }); Assert.Equal("purchase-b", (await states.GetUserStatus(10)).Flow); }
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = a.Id } })) Assert.Equal("purchase-a", (await states.GetUserStatus(10)).Flow);
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-one" } }))
            await states.SaveUserStatus(new User { Id = 711, OwnerStoreId = a.Id, Flow = "owner-input-a" });
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-two" } }))
            await states.SaveUserStatus(new User { Id = 711, OwnerStoreId = b.Id, Flow = "owner-input-b" });
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-one" } }))
            Assert.Equal(a.Id, (await new UserStateStore(databases.Users).GetUserStatus(711)).OwnerStoreId);
        await using var scope = provider.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var method = typeof(TenantBotService).GetMethod("GetTenantBroadcastRecipientsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(new long[] { 10, 11 }, await (Task<List<long>>)method.Invoke(service, new object[] { a, CancellationToken.None })!);
        Assert.Equal(new long[] { 10 }, await (Task<List<long>>)method.Invoke(service, new object[] { b, CancellationToken.None })!);
    }

    /// <summary>The same owner cannot use a selected store's order panel to read a sibling store's order.</summary>
    /// <returns>A task completing after the real list and detail handlers reject a cross-store order id.</returns>
    [Fact]
    public async Task Selected_store_order_list_and_details_exclude_sibling_orders()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N")); var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var orderA = new TenantBotOrder { OrderId = "only-store-a-order", TenantBotId = a.Id, OwnerTelegramUserId = 711 };
        var orderB = new TenantBotOrder { OrderId = "only-store-b-order", TenantBotId = b.Id, OwnerTelegramUserId = 711 };
        await using (var db = databases.Users.CreateDbContext()) { db.Add(orderA); db.Add(orderB); await db.SaveChangesAsync(); }
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true }; var client = new StorefrontClient();
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "panel"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, "orders"));
        Assert.DoesNotContain("only-store-b-order", client.Texts.Last());
        var count = client.Texts.Count;
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(a, $"order:{orderB.Id}:0"));
        Assert.Equal(count, client.Texts.Count);
        Assert.Contains("سفارش پیدا نشد.", client.Answers);
    }

    /// <summary>Two stores share owner-level website admission, while unrelated database writers and other owners progress.</summary>
    /// <returns>A task completing after barrier-controlled debits and restart replay.</returns>
    [Fact]
    public async Task Website_owner_admission_uses_fresh_balance_and_persists_non_replayable_receipts()
    {
        using var databases = new Databases(); var store = new SiteWalletDebitStore(databases.Users);
        var entered = Signal(); var release = Signal(); var wallet = 100L; var posts = 0;
        Task<GozargahSiteWalletEligibility> Eligible(CancellationToken _) => Task.FromResult(wallet >= 70 ? new GozargahSiteWalletEligibility { CanUse = true, WalletToman = wallet } : GozargahSiteWalletEligibility.Insufficient(wallet));
        async Task<GozargahSiteWalletDebitResult> Debit(CancellationToken _)
        { Interlocked.Increment(ref posts); entered.SetResult(); await release.Task; var before = wallet; wallet -= 70; return GozargahSiteWalletDebitResult.Applied(before, wallet); }
        var a = store.ExecuteAsync("order-store-a", 711, 70, Eligible, Debit, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = new SiteWalletDebitStore(databases.Users).ExecuteAsync("order-store-b", 711, 70, Eligible, Debit, default);
        try
        {
            await databases.Inbox.TryAcceptAsync("unrelated", Update(1, 12), 10, default);
            Assert.False(b.IsCompleted);
            Assert.True((await store.ExecuteAsync("other-owner", 712, 1,
                _ => Task.FromResult(new GozargahSiteWalletEligibility { CanUse = true }),
                _ => Task.FromResult(GozargahSiteWalletDebitResult.Applied(10, 9)), default)).Success);
        }
        finally { release.SetResult(); }
        Assert.True((await a).Success); Assert.False((await b).Success); Assert.Equal(1, posts);
        Assert.True((await new SiteWalletDebitStore(databases.Users).ExecuteAsync("order-store-a", 711, 70, Eligible, Debit, default)).Success);
        Assert.Equal(1, posts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync("order-store-a", 711, 71, Eligible, Debit, default));
        await Assert.ThrowsAsync<SiteWalletDebitUncertainException>(() => store.ExecuteAsync("uncertain", 713, 1,
            _ => Task.FromResult(new GozargahSiteWalletEligibility { CanUse = true }), _ => throw new HttpRequestException("response lost"), default));
        await Assert.ThrowsAsync<SiteWalletDebitUncertainException>(() => new SiteWalletDebitStore(databases.Users).ExecuteAsync("uncertain", 713, 1,
            _ => throw new Exception("must not check or retry"), _ => throw new Exception("must not debit"), default));
    }

    /// <summary>Every owner action remains within Telegram's byte limit and malformed or expired legacy data cannot mutate.</summary>
    [Fact]
    public void Owner_callback_envelopes_are_compact_and_fail_closed()
    {
        var store = new BotInstance { TenantStoreNumber = int.MaxValue, CreatedAtUtc = DateTime.UtcNow };
        foreach (var action in new[] { "set:mandatory-channel", "set-setting:Tetraminator:1:0:0", "set-enabled:1:0:0", "broadcast-send:0123456789", "manual-card-confirm" })
        {
            var data = TenantOwnerCallback.Encode(store, action);
            Assert.True(TenantOwnerCallback.TryDecode(data, out var number, out _, out _)); Assert.Equal(int.MaxValue, number);
            Assert.True(Encoding.UTF8.GetByteCount(data) <= 64);
        }
        Assert.False(TenantOwnerCallback.TryDecode("TBM:reset-confirm", out _, out _, out _));
        Assert.False(TenantOwnerCallback.TryDecode("TBM:1:0:0:reset-confirm", out _, out _, out _));
        Assert.False(TenantOwnerCallback.TryDecode("TBM:1:0:7FFFFFFFFFFFFFFF:reset-confirm", out _, out _, out _));
    }

    /// <summary>Two tenant orders charge the same real HTTP fake website owner and replay independently across service restarts.</summary>
    /// <returns>A task completing after actual settlement, insufficient-funds fallback and uncertain-response checks.</returns>
    [Fact]
    public async Task Two_store_orders_share_website_owner_without_duplicate_or_cross_wallet_debits()
    {
        using var databases = new Databases(); var wallet = 5000L; var debits = 0; var ambiguous = false;
        var identities = new List<string>();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var json = body.RootElement; identities.Add(json.GetProperty("telegram_id").GetString()!);
            if (json.GetProperty("action").GetString() == "get_user")
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "shared-owner", telegram_id = "711", wallet, ban = 0 } });
            else
            {
                debits++; var before = wallet; wallet -= json.GetProperty("amount").GetInt64();
                if (ambiguous) { context.Response.StatusCode = 502; await context.Response.WriteAsync("response lost"); }
                else await context.Response.WriteAsJsonAsync(new { success = true, data = new { previous_wallet = before, current_wallet = wallet } });
            }
        });
        await app.StartAsync();
        await using var provider = StorefrontProvider(databases, app.Urls.Single());
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.SaveUserStatus(new CredUser { TelegramUserId = 711, IsColleague = true });
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N")); var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var orders = new[]
        {
            new TenantBotOrder { Id = 101, OrderId = "store-a-order", TenantBotId = a.Id, OwnerTelegramUserId = 711, CustomerTelegramUserId = 51, BaseCostToman = 700, PaymentProvider = "tenant_card" },
            new TenantBotOrder { Id = 102, OrderId = "store-b-order", TenantBotId = b.Id, OwnerTelegramUserId = 711, CustomerTelegramUserId = 52, BaseCostToman = 700, PaymentProvider = "tenant_card" }
        };
        await Task.WhenAll(orders.Select(x => SettleStoreOrder(provider, x)));
        Assert.Equal(3600, wallet); Assert.Equal(2, debits); Assert.All(identities, x => Assert.Equal("711", x));
        await credentials.AddFund(711, 9000, "owner-top-up-after-site-debit");
        await Task.WhenAll(orders.Select(x => SettleStoreOrder(provider, x)));
        Assert.Equal(2, debits); Assert.Equal(9000, await credentials.GetAccountBalance(711));
        await credentials.Pay(new CredUser { TelegramUserId = 711 }, 9000, "remove-test-top-up");
        wallet = 0;
        var insufficient = new TenantBotOrder { Id = 103, OrderId = "insufficient", TenantBotId = a.Id, OwnerTelegramUserId = 711, BaseCostToman = 100, PaymentProvider = "tenant_card" };
        await SettleStoreOrder(provider, insufficient); await SettleStoreOrder(provider, insufficient);
        Assert.Equal(-100, await credentials.GetAccountBalance(711)); Assert.Equal(2, debits);
        wallet = 1000; ambiguous = true;
        var uncertain = new TenantBotOrder { Id = 104, OrderId = "ambiguous", TenantBotId = b.Id, OwnerTelegramUserId = 711, BaseCostToman = 100, PaymentProvider = "tenant_card" };
        await Assert.ThrowsAsync<SiteWalletDebitUncertainException>(() => SettleStoreOrder(provider, uncertain));
        await credentials.AddFund(711, 10000, "top-up-during-review");
        await Assert.ThrowsAsync<SiteWalletDebitUncertainException>(() => SettleStoreOrder(provider, uncertain));
        Assert.Equal(3, debits); Assert.Equal(9900, await credentials.GetAccountBalance(711));
        await app.StopAsync();
    }

    /// <summary>A local wallet receipt committed before users.db finalization prevents switching to website money on restart.</summary>
    /// <returns>A task completing after replaying the actual tenant settlement.</returns>
    [Fact]
    public async Task Tenant_settlement_recovers_local_receipt_after_cross_database_failure()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var wallet = provider.GetRequiredService<CredentialsStore>(); await wallet.AddEmptyUser(711);
        await wallet.AddFund(711, 100, "setup"); await wallet.Pay(new CredUser { TelegramUserId = 711 }, 100, "tenant:901:base-cost");
        // Simulate process loss after credentials.db committed, before users.db route/ledger commit.
        var order = new TenantBotOrder { Id = 901, OrderId = "restart-order", TenantBotId = "tenant-711-2", OwnerTelegramUserId = 711, BaseCostToman = 100, PaymentProvider = "tenant_card" };
        await using (var db = databases.Users.CreateDbContext())
        { db.Add(new TenantWalletRoute { Id = 901, OwnerTelegramUserId = 711, AmountToman = 100, Source = "review" }); await db.SaveChangesAsync(); }
        await SettleStoreOrder(provider, order); await SettleStoreOrder(provider, order);
        Assert.Equal(0, await wallet.GetAccountBalance(711));
        await using var users = databases.Users.CreateDbContext();
        Assert.Equal("bot", (await users.Set<TenantWalletRoute>().SingleAsync()).Source);
        Assert.Empty(await users.Set<SiteWalletDebitOperation>().ToListAsync());
    }

    /// <summary>Invokes the actual financial boundary in a new scope without XUI provisioning.</summary>
    /// <param name="provider">Production graph with isolated database and website configuration.</param>
    /// <param name="order">Stable tenant order carrying the shared owner and immutable base cost.</param>
    /// <param name="debitBaseCost">True tests card funding; false tests online-gateway owner profit.</param>
    /// <returns>A task completing after the durable funding route and wallet ledger settle.</returns>
    private static async Task SettleStoreOrder(ServiceProvider provider, TenantBotOrder order, bool debitBaseCost = true)
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var owner = await provider.GetRequiredService<CredentialsStore>().GetUserStatusWithId(order.OwnerTelegramUserId);
        var method = typeof(TenantBotService).GetMethod("SETTLETENANTOWNERWALLETASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(service, new object[] { order, owner, debitBaseCost, "tenant-order", CancellationToken.None })!;
    }

    /// <summary>Online gateway sales in two stores credit profit to one owner wallet once per order.</summary>
    /// <returns>A task completing after duplicate settlements and immutable receipt verification.</returns>
    [Fact]
    public async Task Two_store_gateway_profits_share_one_wallet_with_distinct_order_receipts()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var wallet = provider.GetRequiredService<CredentialsStore>(); await wallet.AddEmptyUser(711);
        var a = new TenantBotOrder { Id = 801, OrderId = "gateway-a", TenantBotId = "tenant-711-1", OwnerTelegramUserId = 711, ProfitToman = 120, PaymentProvider = "hooshpay" };
        var b = new TenantBotOrder { Id = 802, OrderId = "gateway-b", TenantBotId = "tenant-711-2", OwnerTelegramUserId = 711, ProfitToman = 230, PaymentProvider = "hooshpay" };
        await Task.WhenAll(SettleStoreOrder(provider, a, false), SettleStoreOrder(provider, b, false));
        await Task.WhenAll(SettleStoreOrder(provider, a, false), SettleStoreOrder(provider, b, false));
        Assert.Equal(350, await wallet.GetAccountBalance(711));
        Assert.Equal(120, (await wallet.GetWalletOperationAsync("tenant:801:profit")).AmountToman);
        Assert.Equal(230, (await wallet.GetWalletOperationAsync("tenant:802:profit")).AmountToman);
    }

    /// <summary>Reset preserves store identity and history, while repeated numeric Telegram identity is rejected even for the same owner.</summary>
    /// <returns>A task completing after the real token guard and unique-index checks.</returns>
    [Fact]
    public async Task Store_reset_and_token_identity_are_isolated_within_one_owner()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N")); var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        a.Token = "60001:" + new string('a', 35); a.TelegramBotId = 60001; a.Enabled = true; a.TenantWelcomeText = "فروشگاه اول";
        b.Token = "60002:" + new string('b', 35); b.TelegramBotId = 60002; b.Enabled = true; b.TenantWelcomeText = "فروشگاه دوم";
        await using (var db = databases.Users.CreateDbContext()) { db.Update(a); db.Update(b); await db.SaveChangesAsync(); }
        await using var scope = provider.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        typeof(TenantBotService).GetField("_selectedOwnerStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, a);
        var guard = typeof(TenantBotService).GetMethod("FindTenantTokenConflictAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var token in new[] { b.Token, "60002:" + new string('c', 35), "12345:" + new string('d', 35) })
        {
            var task = (Task)guard.Invoke(service, new object[] { token, "unused_username", 711L, CancellationToken.None })!; await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.True((bool)result.GetType().GetProperty("HasConflict")!.GetValue(result)!);
        }
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == a.Id); row.TelegramBotId = b.TelegramBotId;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        // Use the exact runtime reset transformation, retaining the allocation identity and slot.
        typeof(TenantBotService).GetMethod("ResetTenantStorefrontSettings", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { a, true });
        await using (var db = databases.Users.CreateDbContext()) { db.Update(a); await db.SaveChangesAsync(); }
        var rows = await stores.ListAsync(711);
        Assert.False(rows[0].Enabled); Assert.Null(rows[0].Token); Assert.Equal(a.TenantCreationKey, rows[0].TenantCreationKey);
        Assert.True(rows[1].Enabled); Assert.Equal("فروشگاه دوم", rows[1].TenantWelcomeText); Assert.Equal(2, rows.Count);
    }

    /// <summary>Actual receiver generations can stop and restart one storefront without cancelling its owner's second receiver.</summary>
    /// <returns>A task completing after two live fake polling loops and an independent restart.</returns>
    [Fact]
    public async Task Same_owner_store_receivers_stop_and_restart_independently()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var registry = provider.GetRequiredService<BotRegistry>();
        registry.Upsert(new BotInstance { Id = "tenant-711-1", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 711, Token = "60001:" + new string('a', 35), Enabled = true });
        registry.Upsert(new BotInstance { Id = "tenant-711-2", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 711, Token = "60002:" + new string('b', 35), Enabled = true });
        var clients = new System.Collections.Concurrent.ConcurrentDictionary<string, PollingStorefrontClient>();
        var clientProvider = (BotClientProvider)Activator.CreateInstance(typeof(BotClientProvider), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { registry, (Func<BotInstanceConfig, ITelegramBotClient>)(bot => clients.GetOrAdd(bot.Id, _ => new PollingStorefrontClient())) }, null)!;
        var runtime = new MultiBotHostedService(registry, clientProvider, provider.GetRequiredService<ITelegramUpdateScheduler>(),
            provider.GetRequiredService<IServiceScopeFactory>(), new BotContextAccessor(), provider.GetRequiredService<BotRuntimeStatusStore>(),
            provider.GetRequiredService<IConfiguration>(), NullLogger<MultiBotHostedService>.Instance);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Assert.True(await runtime.StartBotAsync("tenant-711-1", lifetime.Token));
            Assert.True(await runtime.StartBotAsync("tenant-711-2", lifetime.Token));
            await Task.WhenAll(clients.Values.Select(x => x.Started.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.StopBotAsync("tenant-711-1");
            await clients["tenant-711-1"].Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(clients["tenant-711-2"].Stopped.Task.IsCompleted);
            Assert.True(await runtime.StartBotAsync("tenant-711-1", lifetime.Token));
            Assert.Equal(2, clients["tenant-711-1"].Starts); Assert.Equal(1, clients["tenant-711-2"].Starts);
        }
        finally { lifetime.Cancel(); await runtime.StopAsync(default); }
    }

    /// <summary>Builds the real dependency graph against isolated databases without starting hosted services.</summary>
    /// <param name="databases">Fixture that owns all database paths and cleanup.</param>
    /// <param name="websiteUrl">Optional loopback fake website endpoint; remote site sync is otherwise disabled.</param>
    /// <returns>A provider that the test must asynchronously dispose.</returns>
    private static ServiceProvider StorefrontProvider(Databases databases, string? websiteUrl = null)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:enabled"] = "false", ["bots:0:token"] = "12345:" + new string('a', 35),
                ["GozargahSiteSyncEnabled"] = (websiteUrl != null).ToString(), ["GozargahSiteWalletPaymentsEnabled"] = (websiteUrl != null).ToString(),
                ["GozargahSiteApiBaseUrl"] = websiteUrl, ["GozargahSiteApiKey"] = "test-only"
            }).Build();
        var config = configuration.Get<AppConfig>()!; config.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db"); config.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection(); Program.RegisterApplicationServices(services, configuration, config, databases.DirectoryPath);
        return services.BuildServiceProvider();
    }

    /// <summary>Executes one owner callback in a fresh scope, as the durable update executor does.</summary>
    /// <param name="provider">Test-only production service provider.</param>
    /// <param name="client">Recording Telegram transport.</param>
    /// <param name="owner">Authenticated colleague profile.</param>
    /// <param name="data">Untrusted management callback under test.</param>
    /// <returns>A task completing after the handler and state writes.</returns>
    private static async Task OwnerCallback(ServiceProvider provider, StorefrontClient client, CredUser owner, string data)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerCallbackAsync(client,
            new CallbackQuery { Id = Guid.NewGuid().ToString("N"), Data = data, From = new Telegram.Bot.Types.User { Id = owner.TelegramUserId }, Message = new Message { MessageId = 1, Chat = new Chat { Id = owner.TelegramUserId } } },
            owner, await provider.GetRequiredService<UserStateStore>().GetUserStatus(owner.TelegramUserId), default);
    }

    /// <summary>Records owner responses without creating any network connection or changing client lifetimes.</summary>
    private class StorefrontClient : ITelegramBotClient
    {
        /// <summary>Owner messages captured for visible-label assertions.</summary>
        public List<string> Texts { get; } = new();
        /// <summary>Callback payloads captured for addressing and byte-limit assertions.</summary>
        public List<string> Callbacks { get; } = new();
        /// <summary>Callback alerts captured for authorization and stale-button assertions.</summary>
        public List<string> Answers { get; } = new();
        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long? BotId => 12345;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApiAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <summary>Captures panel text and addressed callbacks and returns Telegram-shaped successful responses.</summary>
        /// <typeparam name="TResponse">Response type required by Telegram's request.</typeparam>
        /// <param name="request">Request generated by the real handler.</param>
        /// <param name="cancellationToken">Test cancellation.</param>
        /// <returns>A synthetic response with no external effects.</returns>
        public virtual Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            InlineKeyboardMarkup? keyboard = null;
            if (request is AnswerCallbackQueryRequest answer && answer.Text != null) Answers.Add(answer.Text);
            if (request is SendMessageRequest send) { Texts.Add(send.Text); keyboard = send.ReplyMarkup as InlineKeyboardMarkup; }
            if (request is EditMessageTextRequest edit) { Texts.Add(edit.Text); keyboard = edit.ReplyMarkup; }
            if (keyboard != null) Callbacks.AddRange(keyboard.InlineKeyboard.SelectMany(x => x).Select(x => x.CallbackData).Where(x => x != null)!);
            object result = typeof(TResponse) == typeof(bool) ? true : new Message { MessageId = 1, Chat = new Chat { Id = 711 } };
            return Task.FromResult((TResponse)result);
        }
    }

    /// <summary>Barrier-driven Telegram polling transport with no sockets; cancellation is observable per receiver.</summary>
    private sealed class PollingStorefrontClient : StorefrontClient
    {
        /// <summary>Signals entry into the first polling generation.</summary>
        public TaskCompletionSource Started { get; } = Signal();
        /// <summary>Signals cancellation of a polling generation.</summary>
        public TaskCompletionSource Stopped { get; } = Signal();
        /// <summary>Total generations observed for this one store.</summary>
        public int Starts;
        /// <inheritdoc />
        public override async Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetUpdatesRequest)
            {
                Interlocked.Increment(ref Starts); Started.TrySetResult();
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { Stopped.TrySetResult(); }
            }
            if (typeof(TResponse) == typeof(WebhookInfo)) return (TResponse)(object)new WebhookInfo { Url = "" };
            if (typeof(TResponse) == typeof(Telegram.Bot.Types.User)) return (TResponse)(object)new Telegram.Bot.Types.User { Id = 60001, IsBot = true, Username = "fake_store" };
            return await base.MakeRequestAsync(request, cancellationToken);
        }
    }
}
