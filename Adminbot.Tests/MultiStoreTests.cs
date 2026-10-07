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

    /// <summary>Globally disabled gateways retain exact-store owner opt-in but cannot admit new customer payments.</summary>
    /// <param name="gateway">Each platform provider, exercising the same persisted preference and live permission contract.</param>
    /// <param name="method">Stable customer callback suffix for purchase and renewal.</param>
    /// <returns>A task after real owner/customer handlers and isolated persisted financial state are checked.</returns>
    /// <remarks>
    /// Regression: management disablement must not reject owner saves or be bypassed by a saved preference or old button.
    /// Re-enabling management restores funded customer visibility without another owner edit. Debt ignores saved opt-outs,
    /// stale personal-card callbacks create nothing, and owner recovery restores unchanged saved preferences.
    /// </remarks>
    [Theory]
    [InlineData(PaymentGateway.HooshPay, "HP")]
    [InlineData(PaymentGateway.Tetraminator, "TM")]
    [InlineData(PaymentGateway.UniquePay, "UP")]
    [InlineData(PaymentGateway.AtlasPay, "AP")]
    [InlineData(PaymentGateway.NowPayments, "NP")]
    public async Task Store_gateway_preference_saves_while_global_off_and_customer_admission_tracks_live_permission(
        PaymentGateway gateway, string method)
    {
        using var databases = new Databases();
        var availability = new GatewayAvailabilityProbe();
        await availability.SetEnabledAsync(gateway, false, availability.Snapshot.Revision);
        await using var provider = StorefrontProvider(databases, gatewayAvailability: availability);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        await using (var db = databases.Users.CreateDbContext())
        {
            foreach (var row in await db.BotInstances.ToListAsync())
            {
                row.TenantHooshPayEnabled = row.TenantTetraminatorEnabled = row.TenantUniquePayEnabled =
                    row.TenantAtlasPayEnabled = row.TenantNowPaymentsEnabled = false;
                row.Enabled = true;
            }
            var selected = await db.BotInstances.SingleAsync(x => x.Id == a.Id);
            selected.TelegramBotId = 60001;
            selected.TenantCustomerWalletEnabled = true;
            selected.TenantCustomerWalletOwnerEnabled = true;
            selected.TenantCustomerWalletApprovedAtUtc = DateTime.UtcNow;
            selected.TenantCustomerWalletApprovedByTelegramUserId = 85758085;
            selected.TenantCustomerWalletApprovedBotId = selected.TelegramBotId;
            selected.TenantCustomerWalletApprovedOwnerId = 711;
            await db.SaveChangesAsync();
        }
        a = (await stores.ListAsync(711))[0];
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await wallet.SaveUserStatus(owner);
        await wallet.MutateWalletAsync(711, 500000, "gateway-preference-owner-access");
        var funded = await provider.GetRequiredService<TenantAccessService>().EvaluateFundingSnapshotAsync(711, default);
        var state = provider.GetRequiredService<UserStateStore>();
        var ownerClient = new StorefrontClient();
        var context = provider.GetRequiredService<BotContextAccessor>();
        using (context.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-a" }, Client = ownerClient }))
        {
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(a, "panel"));
            var enable = TenantOwnerCallback.Encode(a, $"set-setting:{gateway}:1");
            await OwnerCallback(provider, ownerClient, owner, enable);
            a = (await stores.ListAsync(711))[0];
            // A fully enabled platform snapshot isolates which persisted storefront switch was changed.
            var globallyEnabled = new PaymentGatewayAvailabilitySnapshot(true, true, true, true, true, 1);
            Assert.True(TenantPaymentGatewayPolicy.IsEnabled(a, gateway, globallyEnabled, funded));
            Assert.False(TenantPaymentGatewayPolicy.IsEnabled(a, gateway, availability.Snapshot, funded));
            var savedRevision = a.UpdatedAtUtc;
            await OwnerCallback(provider, ownerClient, owner, enable);
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(a, "panel"));
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(a, $"set-setting:{gateway}:1"));
            Assert.Equal(savedRevision, (await stores.ListAsync(711))[0].UpdatedAtUtc);

            // Expiry and exact selected-store ownership still protect mutations while the global switch is off.
            var expired = TenantOwnerCallback.Encode(a, $"set-setting:{gateway}:0").Split(':');
            expired[3] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1200).ToString("X");
            await OwnerCallback(provider, ownerClient, owner, string.Join(":", expired));
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(b, "panel"));
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(a, $"set-setting:{gateway}:0"));
            await OwnerCallback(provider, ownerClient, new CredUser { TelegramUserId = 999, IsColleague = true },
                TenantOwnerCallback.Encode(a, $"set-setting:{gateway}:0"));
            var persisted = await stores.ListAsync(711);
            Assert.Equal(savedRevision, persisted[0].UpdatedAtUtc);
            Assert.True(TenantPaymentGatewayPolicy.IsEnabled(persisted[0], gateway, globallyEnabled, funded));
            Assert.All(Enum.GetValues<PaymentGateway>(), g =>
                Assert.False(TenantPaymentGatewayPolicy.IsEnabled(persisted[1], g, globallyEnabled, funded)));
        }

        var customerClient = new StorefrontClient();
        using (context.Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = a.Id, Type = BotInstanceTypes.Tenant }, Client = customerClient }))
        {
            async Task Press(Update update)
            {
                await using var scope = provider.CreateAsyncScope();
                Assert.True(await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleTenantUpdateAsync(
                    customerClient, update, new CredUser { TelegramUserId = 912 }, await state.GetUserStatus(912), default));
            }
            Update Callback(string data) => new() { CallbackQuery = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"), Data = data,
                From = new Telegram.Bot.Types.User { Id = 912 }, Message = new Message { Id = 1, Chat = new Chat { Id = 912 } }
            } };
            async Task RenderPaymentChoices()
            {
                customerClient.Callbacks.Clear();
                await Press(Callback("TN:dur:normal:10:m1"));
                await state.SaveUserStatus(new User { Id = 912, Flow = "tenant-wallet-charge", LastStep = "amount" });
                await Press(new Update { Message = new Message
                {
                    From = new Telegram.Bot.Types.User { Id = 912 }, Chat = new Chat { Id = 912 }, Text = "100000"
                } });
            }
            async Task AssertRenewalVisibility(bool visible)
            {
                await using var scope = provider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
                var keyboard = (InlineKeyboardMarkup)typeof(TenantBotService).GetMethod(
                    "BuildTenantRenewPaymentProviderKeyboard", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, new object[] { new TenantBotOrder { Id = 42, SalePriceToman = 100000 }, a,
                        await scope.ServiceProvider.GetRequiredService<TenantAccessService>().EvaluateFundingSnapshotAsync(711, default) })!;
                Assert.Equal(visible, keyboard.InlineKeyboard.SelectMany(x => x).Any(x => x.CallbackData == $"TN:RN{method}:42"));
            }
            await RenderPaymentChoices();
            Assert.DoesNotContain(customerClient.Callbacks, x => x.StartsWith($"TN:PAY{method}:", StringComparison.Ordinal));
            Assert.DoesNotContain(customerClient.Callbacks, x => x.StartsWith($"TCW:g:{(int)gateway}:", StringComparison.Ordinal));
            await AssertRenewalVisibility(false);

            await availability.SetEnabledAsync(gateway, true, availability.Snapshot.Revision);
            await RenderPaymentChoices();
            var purchase = Assert.Single(customerClient.Callbacks, x => x.StartsWith($"TN:PAY{method}:", StringComparison.Ordinal));
            var topUp = Assert.Single(customerClient.Callbacks, x => x.StartsWith($"TCW:g:{(int)gateway}:", StringComparison.Ordinal));
            await AssertRenewalVisibility(true);

            // Disable after the keyboard was emitted: neither stale customer action may create a local invoice or order.
            await availability.SetEnabledAsync(gateway, false, availability.Snapshot.Revision);
            await Press(Callback(topUp));
            await Press(Callback(purchase));
            await using var verify = databases.Users.CreateDbContext();
            Assert.Empty(await verify.TenantBotOrders.ToListAsync());
            Assert.Empty(await verify.HooshPayPaymentInfos.ToListAsync());
            Assert.Empty(await verify.TetraminatorPaymentInfos.ToListAsync());
            Assert.Empty(await verify.UniquePayPaymentInfos.ToListAsync());
            Assert.Empty(await verify.AtlasPayPaymentInfos.ToListAsync());
            Assert.Empty(await verify.SwapinoPaymentInfos.ToListAsync());
            Assert.Equal(500000, await wallet.GetAccountBalance(711));

            // Debt overrides saved opt-outs; recovery must immediately restore them without rewriting preferences.
            await using (var db = databases.Users.CreateDbContext())
            {
                var store = await db.BotInstances.SingleAsync(x => x.Id == a.Id);
                store.TenantHooshPayEnabled = store.TenantTetraminatorEnabled = store.TenantUniquePayEnabled =
                    store.TenantAtlasPayEnabled = store.TenantNowPaymentsEnabled = false;
                store.TenantCardPaymentEnabled = true;
                store.TenantCardNumber = "6037991234567890";
                await db.SaveChangesAsync();
            }
            a = (await stores.ListAsync(711))[0];
            await RenderPaymentChoices();
            var stalePersonalCard = Assert.Single(customerClient.Callbacks, x => x.StartsWith("TN:PAYCARD:", StringComparison.Ordinal));
            await wallet.MutateWalletAsync(711, -600000, "gateway-preference-owner-debt");
            await availability.SetEnabledAsync(gateway, true, availability.Snapshot.Revision);
            await RenderPaymentChoices();
            Assert.Contains(customerClient.Callbacks, x => x.StartsWith($"TN:PAY{method}:", StringComparison.Ordinal));
            Assert.Contains(customerClient.Callbacks, x => x.StartsWith($"TCW:g:{(int)gateway}:", StringComparison.Ordinal));
            Assert.DoesNotContain(customerClient.Callbacks, x => x.StartsWith("TN:PAYCARD:", StringComparison.Ordinal) || x.StartsWith("TCW:card:", StringComparison.Ordinal));
            await AssertRenewalVisibility(true);
            await Press(Callback(stalePersonalCard));
            Assert.Empty(await verify.TenantBotOrders.AsNoTracking().ToListAsync());
            Assert.Equal(-100000, await wallet.GetAccountBalance(711));
            await wallet.MutateWalletAsync(711, 100001, "gateway-preference-owner-recovery");
            await RenderPaymentChoices();
            Assert.DoesNotContain(customerClient.Callbacks, x => x.StartsWith($"TN:PAY{method}:", StringComparison.Ordinal));
            Assert.DoesNotContain(customerClient.Callbacks, x => x.StartsWith($"TCW:g:{(int)gateway}:", StringComparison.Ordinal));
            Assert.Contains(customerClient.Callbacks, x => x.StartsWith("TN:PAYCARD:", StringComparison.Ordinal));
            await AssertRenewalVisibility(false);
            var recovered = (await stores.ListAsync(711))[0];
            Assert.False(TenantPaymentGatewayPolicy.IsEnabled(recovered, gateway, availability.Snapshot,
                await provider.GetRequiredService<TenantAccessService>().EvaluateFundingSnapshotAsync(711, default)));
            Assert.Equal(a.UpdatedAtUtc, recovered.UpdatedAtUtc);
        }
    }

    /// <summary>Message-bound manual purchase quotes use exactly the live central set in debt and resume saved card preferences after funding recovers.</summary>
    /// <returns>A task after real quote rendering verifies provider choices and unchanged persisted switches across the funding transition.</returns>
    /// <remarks>Exercises the consumer keyboard, not policy implementation or incidental customer wording.</remarks>
    [Fact]
    public async Task Store_manual_purchase_quote_uses_exact_live_gateway_set_during_debt()
    {
        using var databases = new Databases();
        var availability = new GatewayAvailabilityProbe();
        foreach (var gateway in Enum.GetValues<PaymentGateway>())
            await availability.SetEnabledAsync(gateway, gateway is not (PaymentGateway.Tetraminator or PaymentGateway.NowPayments),
                availability.Snapshot.Revision);
        await using var provider = StorefrontProvider(databases, gatewayAvailability: availability);
        var tenant = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        tenant.Enabled = true;
        tenant.TenantPricingMode = TenantPricingModes.Manual;
        tenant.TenantNormalPricePerGbToman = 10000; tenant.TenantNormalPricePerDayToman = 1000;
        tenant.TenantHooshPayEnabled = tenant.TenantTetraminatorEnabled = tenant.TenantUniquePayEnabled =
            tenant.TenantAtlasPayEnabled = tenant.TenantNowPaymentsEnabled = false;
        tenant.TenantCardPaymentEnabled = true; tenant.TenantCardNumber = "6037991234567890";
        await using (var db = databases.Users.CreateDbContext()) { db.Update(tenant); await db.SaveChangesAsync(); }
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await wallet.AddEmptyUser(711);
        await wallet.MutateWalletAsync(711, -1, "quote-owner-debt");
        var client = new StorefrontClient();
        var selection = new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 10, DurationKey = "m1", AccountCount = 1 };
        async Task<string[]> RenderMethods()
        {
            client.Callbacks.Clear();
            await using var scope = provider.CreateAsyncScope();
            await (Task)typeof(TenantBotService).GetMethod("SHOWCUSTOMERCONFIRMASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scope.ServiceProvider.GetRequiredService<TenantBotService>(),
                    new object[] { client, new ChatId(912), 912L, null!, tenant, selection, CancellationToken.None })!;
            return client.Callbacks.Where(x => x.StartsWith("TN:DQ:", StringComparison.Ordinal)).Select(x => x.Split(':')[3]).Order().ToArray();
        }
        Assert.Equal(new[] { "AP", "HP", "UP" }, await RenderMethods());
        await wallet.MutateWalletAsync(711, 2, "quote-owner-recovery");
        Assert.Equal(new[] { "CARD" }, await RenderMethods());
        await using var verify = databases.Users.CreateDbContext();
        var unchanged = await verify.BotInstances.SingleAsync();
        Assert.All(Enum.GetValues<PaymentGateway>(), gateway => Assert.False(TenantPaymentGatewayPolicy.IsEnabled(unchanged,
            gateway, new PaymentGatewayAvailabilitySnapshot(true, true, true, true, true, 1),
            new TenantAccessEvaluation(TenantAccessDecision.Allowed, 1, null, false, 200000))));
        Assert.True(unchanged.TenantCardPaymentEnabled);
        Assert.Empty(await verify.TenantBotOrders.ToListAsync());
        Assert.Equal(1, await wallet.GetAccountBalance(711));
    }

    /// <summary>Every direct first-invoice core rechecks recovered owner funding before admitting an opted-out provider.</summary>
    /// <param name="gateway">Central provider whose live switch was available during the prior debt-mode menu.</param>
    /// <param name="providerName">Canonical persisted provider of the already admitted unpaid order.</param>
    /// <param name="coreName">Existing invoice boundary exercised through the production scoped service.</param>
    /// <returns>A task after fresh funded-mode rejection leaves every payment table and the pending attempt untouched.</returns>
    /// <remarks>Protects the insufficient-to-funded transition between selection and provider creation without relying on alert wording.</remarks>
    [Theory]
    [InlineData(PaymentGateway.HooshPay, "HooshPay", "CreateTenantHooshPayInvoiceCoreAsync")]
    [InlineData(PaymentGateway.Tetraminator, "Tetraminator", "CreateTenantTetraminatorInvoiceCoreAsync")]
    [InlineData(PaymentGateway.UniquePay, "UniquePay", "CreateTenantUniquePayInvoiceCoreAsync")]
    [InlineData(PaymentGateway.AtlasPay, "atlaspay", "CreateTenantAtlasPayInvoiceCoreAsync")]
    [InlineData(PaymentGateway.NowPayments, "NowPayments", "CreateTenantNowPaymentsInvoiceCoreAsync")]
    public async Task Store_first_invoice_boundary_restores_opt_out_after_owner_recovery(
        PaymentGateway gateway, string providerName, string coreName)
    {
        using var databases = new Databases();
        var availability = new GatewayAvailabilityProbe();
        await availability.SetEnabledAsync(gateway, true, availability.Snapshot.Revision);
        await using var provider = StorefrontProvider(databases, gatewayAvailability: availability);
        var tenant = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        tenant.Enabled = true;
        tenant.TenantHooshPayEnabled = tenant.TenantTetraminatorEnabled = tenant.TenantUniquePayEnabled =
            tenant.TenantAtlasPayEnabled = tenant.TenantNowPaymentsEnabled = false;
        var order = new TenantBotOrder
        {
            OrderId = "admitted-before-owner-recovery", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
            CustomerTelegramUserId = 912, CustomerChatId = 912, SalePriceToman = 100000, ServiceKey = "normal",
            PaymentProvider = providerName, PaymentStatus = TenantBotOrderStatuses.Pending, DiscountInvoiceAttemptState = "none"
        };
        await using (var db = databases.Users.CreateDbContext())
        { db.Update(tenant); db.Add(order); await db.SaveChangesAsync(); }
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await wallet.AddEmptyUser(711);
        await wallet.MutateWalletAsync(711, -1, "invoice-menu-owner-debt");
        var access = provider.GetRequiredService<TenantAccessService>();
        Assert.True(TenantPaymentGatewayPolicy.IsEnabled(tenant, gateway, availability.Snapshot,
            await access.EvaluateFundingSnapshotAsync(711, default)));
        await wallet.MutateWalletAsync(711, 2, "invoice-admission-owner-recovery");
        await using var scope = provider.CreateAsyncScope();
        var callback = new CallbackQuery
        {
            Id = "recovered-owner-stale-invoice", From = new Telegram.Bot.Types.User { Id = 912 },
            Message = new Message { Id = 1, Chat = new Chat { Id = 912 } }
        };
        await (Task)typeof(TenantBotService).GetMethod(coreName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scope.ServiceProvider.GetRequiredService<TenantBotService>(), new object[]
                { new StorefrontClient(), callback, tenant, new CredUser { TelegramUserId = 912 }, order, CancellationToken.None })!;
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.HooshPayPaymentInfos.ToListAsync());
        Assert.Empty(await verify.TetraminatorPaymentInfos.ToListAsync());
        Assert.Empty(await verify.UniquePayPaymentInfos.ToListAsync());
        Assert.Empty(await verify.AtlasPayPaymentInfos.ToListAsync());
        Assert.Empty(await verify.SwapinoPaymentInfos.ToListAsync());
        Assert.Equal("none", (await verify.TenantBotOrders.SingleAsync()).DiscountInvoiceAttemptState);
        Assert.Equal(1, await wallet.GetAccountBalance(711));
    }

    /// <summary>A previously pending quoted personal-card route cannot expose fresh instructions after owner debt begins.</summary>
    /// <returns>A task after the direct AwaitingReceipt replay is denied without changing the saved card preference or issuing work.</returns>
    /// <remarks>AwaitingReceipt without actual submitted evidence is not paid recovery and cannot bypass funding admission.</remarks>
    [Fact]
    public async Task Store_stale_awaiting_receipt_instructions_do_not_admit_personal_card_in_debt()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        tenant.Enabled = true; tenant.TenantCardPaymentEnabled = true; tenant.TenantCardNumber = "6037991234567890";
        var order = new TenantBotOrder
        {
            OrderId = "unpaid-quoted-card-before-debt", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
            CustomerTelegramUserId = 912, CustomerChatId = 912, SalePriceToman = 100000, ServiceKey = "normal",
            PaymentProvider = "tenant_card", PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt
        };
        await using (var db = databases.Users.CreateDbContext())
        { db.Update(tenant); db.Add(order); await db.SaveChangesAsync(); }
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await wallet.AddEmptyUser(711);
        await wallet.MutateWalletAsync(711, -100000, "stale-card-owner-debt");
        var client = new StorefrontClient();
        var callback = new CallbackQuery
        { Id = "stale-quoted-card", From = new Telegram.Bot.Types.User { Id = 912 }, Message = new Message { Id = 1, Chat = new Chat { Id = 912 } } };
        await using var scope = provider.CreateAsyncScope();
        await (Task)typeof(TenantBotService).GetMethod("SendTenantCardOrderInstructionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scope.ServiceProvider.GetRequiredService<TenantBotService>(),
                new object[] { client, callback, tenant, order, CancellationToken.None })!;
        Assert.Empty(client.Texts);
        Assert.NotEmpty(client.Answers);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantManualPaymentReceipts.ToListAsync());
        Assert.True((await verify.BotInstances.SingleAsync()).TenantCardPaymentEnabled);
        Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, (await verify.TenantBotOrders.SingleAsync()).PaymentStatus);
        Assert.Equal(-100000, await wallet.GetAccountBalance(711));
    }

    /// <summary>An admitted Tetraminator order without a started invoice must recheck live permission before creating a payment.</summary>
    /// <returns>A task after the last creation boundary rejects the globally disabled provider without consuming the attempt.</returns>
    /// <remarks>Regression: admitted quote replay bypasses the open-quote method list, so the invoice core is the final gate.</remarks>
    [Fact]
    public async Task Store_admitted_Tetraminator_order_without_invoice_cannot_bypass_global_disablement()
    {
        using var databases = new Databases();
        var availability = new GatewayAvailabilityProbe();
        await availability.SetEnabledAsync(PaymentGateway.Tetraminator, false, availability.Snapshot.Revision);
        await using var provider = StorefrontProvider(databases, gatewayAvailability: availability);
        var tenant = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        tenant.TenantTetraminatorEnabled = true;
        tenant.Enabled = true;
        var ownerWallet = provider.GetRequiredService<CredentialsStore>();
        await ownerWallet.AddEmptyUser(711);
        await ownerWallet.MutateWalletAsync(711, 1, "disabled-provider-funded-owner");
        var order = new TenantBotOrder
        {
            OrderId = "admitted-before-global-disable", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
            CustomerTelegramUserId = 912, SalePriceToman = 100000, ServiceKey = "normal", PaymentProvider = "Tetraminator",
            PaymentStatus = TenantBotOrderStatuses.Pending, DiscountInvoiceAttemptState = "none"
        };
        await using (var db = databases.Users.CreateDbContext()) { db.Update(tenant); db.Add(order); await db.SaveChangesAsync(); }
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var callback = new CallbackQuery
        {
            Id = "disabled-admitted-invoice", From = new Telegram.Bot.Types.User { Id = 912 },
            Message = new Message { Id = 1, Chat = new Chat { Id = 912 } }
        };
        await (Task)typeof(TenantBotService).GetMethod("CreateTenantTetraminatorInvoiceCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[]
            { new StorefrontClient(), callback, tenant, new CredUser { TelegramUserId = 912 }, order, CancellationToken.None })!;
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TetraminatorPaymentInfos.ToListAsync());
        var saved = await verify.TenantBotOrders.SingleAsync();
        Assert.Null(saved.TetraminatorPaymentInfoId);
        Assert.Equal("none", saved.DiscountInvoiceAttemptState);
        Assert.Equal(TenantBotOrderStatuses.Pending, saved.PaymentStatus);
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

    /// <summary>Online gateway sales in two stores reduce one shared owner's negative wallet debt once per order.</summary>
    /// <returns>A task completing after duplicate settlements and immutable receipt verification.</returns>
    [Fact]
    public async Task Two_store_gateway_profits_share_one_wallet_with_distinct_order_receipts()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var wallet = provider.GetRequiredService<CredentialsStore>(); await wallet.AddEmptyUser(711);
        await wallet.MutateWalletAsync(711, -500, "gateway-profit-existing-owner-debt");
        var a = new TenantBotOrder { Id = 801, OrderId = "gateway-a", TenantBotId = "tenant-711-1", OwnerTelegramUserId = 711, ProfitToman = 120, PaymentProvider = "hooshpay" };
        var b = new TenantBotOrder { Id = 802, OrderId = "gateway-b", TenantBotId = "tenant-711-2", OwnerTelegramUserId = 711, ProfitToman = 230, PaymentProvider = "hooshpay" };
        await Task.WhenAll(SettleStoreOrder(provider, a, false), SettleStoreOrder(provider, b, false));
        await Task.WhenAll(SettleStoreOrder(provider, a, false), SettleStoreOrder(provider, b, false));
        Assert.Equal(-150, await wallet.GetAccountBalance(711));
        Assert.Equal(120, (await wallet.GetWalletOperationAsync("tenant:801:profit")).AmountToman);
        Assert.Equal(230, (await wallet.GetWalletOperationAsync("tenant:802:profit")).AmountToman);
    }

    /// <summary>Full reset clears the selected store's prices alongside its settings; invalid-token cleanup retains prices.</summary>
    /// <returns>A task completing after reset, token and independent store-price checks.</returns>
    [Fact]
    public async Task Store_reset_and_token_identity_are_isolated_within_one_owner()
    {
        using var databases = new Databases(); await using var provider = StorefrontProvider(databases);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var a = await stores.CreateAsync(711, Guid.NewGuid().ToString("N")); var b = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        a.Token = "60001:" + new string('a', 35); a.TelegramBotId = 60001; a.Enabled = true; a.TenantWelcomeText = "فروشگاه اول";
        a.TenantPricingMode = TenantPricingModes.Manual; a.TenantNormalPricePerGbToman = 4200;
        a.TenantNormalPricePerDayToman = 600; a.TenantNationalPricePerGbToman = 110000;
        a.TenantUnlimitedPlanPricesJson = "{\"unlimited\":{\"u2-m1\":256000}}";
        b.Token = "60002:" + new string('b', 35); b.TelegramBotId = 60002; b.Enabled = true; b.TenantWelcomeText = "فروشگاه دوم";
        b.TenantPricingMode = TenantPricingModes.Manual; b.TenantNormalPricePerGbToman = 4800;
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
        // Invalid-token cleanup cannot silently replace tenant-owned prices with percentage/public prices.
        typeof(TenantBotService).GetMethod("ResetTenantStorefrontSettings", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { a, false });
        Assert.Equal(TenantPricingModes.Manual, a.TenantPricingMode);
        Assert.Equal(4200, a.TenantNormalPricePerGbToman);
        Assert.Equal(600, a.TenantNormalPricePerDayToman);
        Assert.Equal(110000, a.TenantNationalPricePerGbToman);
        Assert.Contains("u2-m1", a.TenantUnlimitedPlanPricesJson);
        // A full reset must not erase B's independent rates.
        typeof(TenantBotService).GetMethod("ResetTenantStorefrontSettings", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { a, true });
        await using (var db = databases.Users.CreateDbContext()) { db.Update(a); await db.SaveChangesAsync(); }
        var rows = await stores.ListAsync(711);
        Assert.False(rows[0].Enabled); Assert.Null(rows[0].Token); Assert.Equal(a.TenantCreationKey, rows[0].TenantCreationKey);
        Assert.Equal(TenantPricingModes.Percent, rows[0].TenantPricingMode);
        Assert.Equal(0, rows[0].TenantPriceMarkupPercent);
        Assert.Null(rows[0].TenantNormalPricePerGbToman);
        Assert.Null(rows[0].TenantNormalPricePerDayToman);
        Assert.Null(rows[0].TenantNationalPricePerGbToman);
        Assert.Null(rows[0].TenantUnlimitedPlanPricesJson);
        Assert.Equal(TenantPricingModes.Manual, rows[1].TenantPricingMode);
        Assert.Equal(4800, rows[1].TenantNormalPricePerGbToman);
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
    /// <param name="catalogPath">Optional isolated XUI plan catalog used to exercise changed wholesale prices.</param>
    /// <param name="panelUrl">Optional isolated XUI panel URL for renewal handler scenarios.</param>
    /// <param name="gatewayAvailability">Optional live in-memory global switch store for invoice-admission transitions.</param>
    /// <param name="tokenProbe">Optional controlled getMe transport; owner-panel tests never contact Telegram.</param>
    /// <param name="ownerTokenProbeCache">Optional short-budget cache shared by the test's actual scoped handlers.</param>
    /// <returns>A provider that the test must asynchronously dispose.</returns>
    /// <remarks>Each fixture supplies its own database paths. Inject a probe before using saved tokens so owner regressions remain network-free.</remarks>
    /// <example><code>await using var provider = StorefrontProvider(databases, tokenProbe: probe);</code></example>
    private static ServiceProvider StorefrontProvider(Databases databases, string? websiteUrl = null, string? catalogPath = null,
        string? panelUrl = null, IPaymentGatewayAvailability? gatewayAvailability = null,
        ITelegramTokenProbe? tokenProbe = null, TenantOwnerTokenProbeCache? ownerTokenProbeCache = null)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:enabled"] = "false", ["bots:0:token"] = "12345:" + new string('a', 35),
                ["GozargahSiteSyncEnabled"] = (websiteUrl != null).ToString(), ["GozargahSiteWalletPaymentsEnabled"] = (websiteUrl != null).ToString(),
                ["GozargahSiteApiBaseUrl"] = websiteUrl, ["GozargahSiteApiKey"] = "test-only",
                ["XuiV3ApiBaseUrl"] = panelUrl, ["XuiV3ApiToken"] = panelUrl == null ? null : "test-only",
                ["XuiV3ServicePlansPath"] = catalogPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json"))
            }).Build();
        var config = configuration.Get<AppConfig>()!; config.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db"); config.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection(); Program.RegisterApplicationServices(services, configuration, config, databases.DirectoryPath);
        if (gatewayAvailability != null) services.AddSingleton<IPaymentGatewayAvailability>(gatewayAvailability);
        if (tokenProbe != null) services.AddSingleton<ITelegramTokenProbe>(tokenProbe);
        if (ownerTokenProbeCache != null) services.AddSingleton(ownerTokenProbeCache);
        return services.BuildServiceProvider();
    }

    /// <summary>Executes one owner callback in a fresh scope, as the durable update executor does.</summary>
    /// <param name="provider">Test-only production service provider.</param>
    /// <param name="client">Recording Telegram transport.</param>
    /// <param name="owner">Authenticated colleague profile.</param>
    /// <param name="data">Untrusted management callback under test.</param>
    /// <param name="cancellationToken">Optional handler cancellation, including foreground probe waits and cache hits.</param>
    /// <returns>A task completing after the handler and state writes.</returns>
    /// <remarks>The real handler still validates callback addressing, revision, owner identity and persisted store selection.</remarks>
    /// <exception cref="OperationCanceledException">The handler cancellation token is canceled during the callback.</exception>
    /// <example><code>await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), token);</code></example>
    private static async Task OwnerCallback(ServiceProvider provider, StorefrontClient client, CredUser owner, string data,
        CancellationToken cancellationToken = default)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerCallbackAsync(client,
            new CallbackQuery { Id = Guid.NewGuid().ToString("N"), Data = data, From = new Telegram.Bot.Types.User { Id = owner.TelegramUserId }, Message = new Message { Id = 1, Chat = new Chat { Id = owner.TelegramUserId } } },
            owner, await provider.GetRequiredService<UserStateStore>().GetUserStatus(owner.TelegramUserId), cancellationToken);
    }

    /// <summary>Records owner responses without creating any network connection or changing client lifetimes.</summary>
    private class StorefrontClient : ITelegramBotClient
    {
        /// <summary>Owner messages captured for visible-label assertions.</summary>
        public List<string> Texts { get; } = new();
        /// <summary>Callback payloads captured for addressing and byte-limit assertions.</summary>
        public List<string> Callbacks { get; } = new();
        /// <summary>
        /// Customer-visible inline-button captions captured alongside the callback payloads.
        /// </summary>
        /// <remarks>
        /// Labels and callback data are captured from the same keyboard so a test can prove that a display-label change
        /// left the routing payload untouched.
        /// </remarks>
        public List<string> Labels { get; } = new();
        /// <summary>Callback alerts captured for authorization and stale-button assertions.</summary>
        public List<string> Answers { get; } = new();
        /// <summary>Reply-keyboard button labels captured from customer-facing messages.</summary>
        public List<string> ReplyButtons { get; } = new();
        /// <summary>
        /// Ordered request-kind log used to prove that a callback is acknowledged before slower work starts.
        /// </summary>
        /// <remarks>
        /// Entries are short stable markers such as <c>answer</c>, <c>media-group</c>, <c>photo</c>, and <c>text</c>, so a
        /// test can assert relative ordering instead of wall-clock timing.
        /// </remarks>
        public List<string> Events { get; } = new();
        /// <summary>Item count of each media-group (album) request, in send order.</summary>
        public List<int> MediaGroupSizes { get; } = new();
        /// <summary>Captions supplied on media-group items, in send order. A null slide caption is not recorded.</summary>
        public List<string> MediaGroupCaptions { get; } = new();
        /// <summary>Number of single-photo sends, used to prove the one-image path avoids an invalid media group.</summary>
        public int SinglePhotoSends { get; private set; }
        /// <summary>Captions supplied on single-photo sends, in send order.</summary>
        public List<string> SinglePhotoCaptions { get; } = new();
        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long BotId => 12345;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFile(Telegram.Bot.Types.TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => DownloadFile(file.FilePath!, destination, cancellationToken);

        /// <inheritdoc />
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <summary>Captures panel text and addressed callbacks and returns Telegram-shaped successful responses.</summary>
        /// <typeparam name="TResponse">Response type required by Telegram's request.</typeparam>
        /// <param name="request">Request generated by the real handler.</param>
        /// <param name="cancellationToken">Test cancellation.</param>
        /// <returns>A synthetic response with no external effects.</returns>
        public virtual Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            InlineKeyboardMarkup? keyboard = null;
            if (request is AnswerCallbackQueryRequest answer) { Events.Add("answer"); if (answer.Text != null) Answers.Add(answer.Text); }
            if (request is SendMessageRequest send)
            {
                Events.Add("text");
                Texts.Add(send.Text);
                keyboard = send.ReplyMarkup as InlineKeyboardMarkup;
                if (send.ReplyMarkup is ReplyKeyboardMarkup replyKeyboard)
                    ReplyButtons.AddRange(replyKeyboard.Keyboard.SelectMany(row => row).Select(button => button.Text));
            }
            if (request is EditMessageTextRequest edit) { Events.Add("text"); Texts.Add(edit.Text); keyboard = edit.ReplyMarkup; }
            if (request is SendMediaGroupRequest mediaGroup)
            {
                // Album sends are recorded by size and caption so a test can prove batching and one-caption placement.
                Events.Add("media-group");
                var items = mediaGroup.Media.ToList();
                MediaGroupSizes.Add(items.Count);
                MediaGroupCaptions.AddRange(items.OfType<InputMediaPhoto>().Select(x => x.Caption).Where(x => x != null)!);
            }
            if (request is SendPhotoRequest photoRequest)
            {
                Events.Add("photo");
                SinglePhotoSends++;
                if (photoRequest.Caption != null) SinglePhotoCaptions.Add(photoRequest.Caption);
            }
            if (keyboard != null)
            {
                var flat = keyboard.InlineKeyboard.SelectMany(x => x).ToList();
                Callbacks.AddRange(flat.Select(x => x.CallbackData).Where(x => x != null)!);
                Labels.AddRange(flat.Select(x => x.Text));
            }
            object result = typeof(TResponse) == typeof(bool)
                ? true
                // Album sends return Message[], so an empty array is returned instead of a single message.
                : typeof(TResponse).IsArray
                    ? Array.CreateInstance(typeof(TResponse).GetElementType()!, 0)
                    : new Message { Id = 1, Chat = new Chat { Id = 711 } };
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
        public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetUpdatesRequest)
            {
                Interlocked.Increment(ref Starts); Started.TrySetResult();
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { Stopped.TrySetResult(); }
            }
            if (typeof(TResponse) == typeof(WebhookInfo)) return (TResponse)(object)new WebhookInfo { Url = "" };
            if (typeof(TResponse) == typeof(Telegram.Bot.Types.User)) return (TResponse)(object)new Telegram.Bot.Types.User { Id = 60001, IsBot = true, Username = "fake_store" };
            return await base.SendRequest(request, cancellationToken);
        }
    }
}
