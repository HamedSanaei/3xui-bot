using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>Exercises real owner and tenant Telegram handlers against isolated users.db and a mutable catalog.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Owner edits remain drafts until complete; manual checkout and national/unlimited previews use saved sale rates.</summary>
    /// <returns>A task after owner/customer messages, quote admission, and persisted order and plan values are checked.</returns>
    /// <remarks>
    /// Regression: missing plans, below-cost rates, changed catalog costs, and stale quotes must not admit a payment.
    /// An incomplete draft must keep every staged rate and use an accepted callback alert within Telegram's 200-character limit.
    /// </remarks>
    [Fact]
    public async Task Storefront_manual_prices_require_complete_owner_draft_and_charge_current_quote()
    {
        using var databases = new Databases();
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json"));
        var path = Path.Combine(databases.DirectoryPath, "tenant-prices.json");
        var catalog = JObject.Parse(File.ReadAllText(source, Encoding.UTF8));
        var plans = (JArray)catalog["services"]!;
        var unlimited = (JObject)plans.Single(s => (string?)s["key"] == "unlimited");
        ((JObject)((JArray)unlimited["unlimitedPlans"]!).Single(p => (string?)p["key"] == "u2-m1"))["tenantUsesUserPrice"] = true;
        File.WriteAllText(path, catalog.ToString(), Encoding.UTF8);
        await using var provider = StorefrontProvider(databases, catalogPath: path);
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var store = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var state = provider.GetRequiredService<UserStateStore>();
        var ownerClient = new StorefrontClient();
        using (provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = "owned-a" }, Client = ownerClient }))
        {
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store, "panel"));
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store, "p:open"));
            var nonce = JObject.Parse((await state.GetUserStatus(711)).OwnerPricingDraftJson!)["Nonce"]!.Value<string>()!;
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store, $"p:r:ng:{nonce}"));
            await PricingOwnerTextAsync(provider, ownerClient, owner, state, "3499");
            Assert.Null((await stores.ListAsync(711))[0].TenantNormalPricePerGbToman);
            foreach (var (key, amount) in new[] { ("ng", "4200"), ("nd", "600"), ("ig", "110000") })
            {
                await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store, $"p:r:{key}:{nonce}"));
                await PricingOwnerTextAsync(provider, ownerClient, owner, state, amount);
            }
            var answerCount = ownerClient.Answers.Count;
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store, $"p:save:m:{nonce}"));
            Assert.Equal(TenantPricingModes.Percent, (await stores.ListAsync(711))[0].TenantPricingMode);
            var incompleteAlert = Assert.Single(ownerClient.Answers.Skip(answerCount));
            Assert.InRange(incompleteAlert.Length, 1, 200);
            var draft = JObject.Parse((await state.GetUserStatus(711)).OwnerPricingDraftJson!);
            Assert.Equal(4200, draft["NormalGb"]!.Value<long>());
            Assert.Equal(600, draft["NormalDay"]!.Value<long>());
            Assert.Equal(110000, draft["NationalGb"]!.Value<long>());
            var ordered = (JArray)draft["Plans"]!;
            for (var index = 0; index < ordered.Count; index++)
            {
                var key = ordered[index];
                var service = plans.Single(s => (string?)s["key"] == (string?)key["ServiceKey"]);
                var plan = ((JArray)service["unlimitedPlans"]!).Single(p => (string?)p["key"] == (string?)key["PlanKey"]);
                var rate = (long)plan["price"]!["colleague"]! + 1000;
                await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store,
                    $"p:u:{index.ToString(CultureInfo.InvariantCulture)}:{nonce}"));
                await PricingOwnerTextAsync(provider, ownerClient, owner, state, rate.ToString(CultureInfo.InvariantCulture));
            }
            await OwnerCallback(provider, ownerClient, owner, TenantOwnerCallback.Encode(store, $"p:save:m:{nonce}"));
            Assert.All(ownerClient.Callbacks, value => Assert.InRange(Encoding.UTF8.GetByteCount(value), 1, 64));
        }
        store = (await stores.ListAsync(711))[0];
        Assert.Equal(TenantPricingModes.Manual, store.TenantPricingMode);
        Assert.Equal(4200, store.TenantNormalPricePerGbToman);
        Assert.Equal(600, store.TenantNormalPricePerDayToman);
        Assert.Equal(110000, store.TenantNationalPricePerGbToman);
        Assert.Equal(256_000, TenantStorefrontPricing.ParseUnlimitedPlanPrices(store.TenantUnlimitedPlanPricesJson)["unlimited"]["u2-m1"]);

        // Owner access remains local; no payment provider or external XUI panel is contacted by this card order.
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.MutateWalletAsync(711, 500_000, "tenant-pricing-owner-test");
        await using (var db = databases.Users.CreateDbContext())
        {
            var persisted = await db.BotInstances.SingleAsync(x => x.Id == store.Id);
            persisted.Enabled = true;
            persisted.TenantCardPaymentEnabled = true;
            persisted.TenantCardNumber = "6037991234567890";
            persisted.TenantCardHolderName = "Test";
            await db.SaveChangesAsync();
        }
        var customerClient = new StorefrontClient();
        using (provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = store.Id, Type = BotInstanceTypes.Tenant }, Client = customerClient }))
        {
            async Task Press(string action, int messageId = 1)
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleTenantUpdateAsync(customerClient,
                    new Update { CallbackQuery = new CallbackQuery
                    {
                        Id = Guid.NewGuid().ToString("N"), Data = action,
                        From = new Telegram.Bot.Types.User { Id = 912 }, Message = new Message { Id = messageId, Chat = new Chat { Id = 912 } }
                    } }, new CredUser { TelegramUserId = 912 }, await state.GetUserStatus(912), default);
            }
            await Press("TN:services");
            await Press("TN:svc:normal");
            await Press("TN:GB:normal:10");
            await Press("TN:dur:normal:10:m1");
            Assert.Contains(customerClient.Texts, text => text.Contains(60_000L.FormatCurrency()) && !text.Contains("قیمت همکار"));
            await using (var db = databases.Users.CreateDbContext())
            {
                var quote = await db.TenantDiscountQuotes.AsNoTracking().SingleAsync();
                Assert.Equal(60_000, quote.GrossToman);
                Assert.Equal(50_000, quote.BaseCostToman);
            }
            var cardAction = customerClient.Callbacks.Last(x => x.StartsWith("TN:DQ:", StringComparison.Ordinal) && x.EndsWith(":CARD", StringComparison.Ordinal));
            await Press(cardAction);
            await using var orderDb = databases.Users.CreateDbContext();
            var order = await orderDb.TenantBotOrders.AsNoTracking().SingleAsync();
            Assert.Equal(60_000, order.SalePriceToman);
            Assert.Equal(50_000, order.BaseCostToman);
            Assert.Equal(10_000, order.ProfitToman);
            Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, order.PaymentStatus);
            await Press("TN:dur:national:2:life", messageId: 2);
            await Press("TN:upl:unlimited:u2-m1", messageId: 3);
            Assert.DoesNotContain(customerClient.Texts, text => text.Contains("قیمت همکار"));
            await using var previewDb = databases.Users.CreateDbContext();
            var national = await previewDb.TenantDiscountQuotes.AsNoTracking().SingleAsync(x => x.MessageId == 2);
            var unlimitedQuote = await previewDb.TenantDiscountQuotes.AsNoTracking().SingleAsync(x => x.MessageId == 3);
            Assert.Equal(220_000, national.GrossToman);
            Assert.Equal(192_000, national.BaseCostToman);
            Assert.Equal(256_000, unlimitedQuote.GrossToman);
            Assert.Equal(255_000, unlimitedQuote.BaseCostToman);
        }
    }

    /// <summary>Manual lifetime money is rounded once; duplicate JSON keys and below-cost plans fail closed.</summary>
    /// <remarks>Regression: double precision drift or a case-colliding saved plan key must not become a cheaper checkout.</remarks>
    [Fact]
    public void Manual_lifetime_ceiling_and_plan_json_identity_are_strict()
    {
        var tenant = new BotInstance { TenantPricingMode = TenantPricingModes.Manual,
            TenantNationalPricePerGbToman = 3501 };
        var service = new XuiV3ServiceDefinition { Key = "national", LifetimePriceMultiplier = 1.125,
            PricePerGb = new XuiV3RolePrice { Colleague = 3500 } };
        var lifetime = new XuiV3ResolvedPurchase { Service = service, TrafficGb = 1, DurationDays = 0,
            PriceToman = 3938 };
        Assert.Equal(3939, TenantStorefrontPricing.CalculateManualSale(tenant, lifetime, null));
        tenant.TenantNationalPricePerGbToman = 3499;
        Assert.Throws<TenantPriceUnavailableException>(() =>
            TenantStorefrontPricing.CalculateManualSale(tenant, lifetime, null));
        Assert.Throws<TenantPriceUnavailableException>(() =>
            TenantStorefrontPricing.ParseUnlimitedPlanPrices("{\"unlimited\":{\"u2-m1\":256000,\"U2-M1\":300000}}"));
        Assert.Throws<TenantPriceUnavailableException>(() =>
            TenantStorefrontPricing.ParseUnlimitedPlanPrices("{\"unlimited\":{\"u2-m1\":9223372036854775808}}"));
        Assert.Throws<TenantPriceUnavailableException>(() =>
            TenantStorefrontPricing.ParseUnlimitedPlanPrices("{\"unlimited\":{},\"UNLIMITED\":{}}"));
    }

    /// <summary>Changing an owner rate or global colleague floor invalidates old quotes without creating an order.</summary>
    /// <returns>A task after the real customer callback rejects a stale card action and hides only under-cost normal.</returns>
    /// <remarks>Regression: an old preview must never become a different payable amount after the owner edits pricing.</remarks>
    [Fact]
    public async Task Storefront_manual_old_quote_and_new_wholesale_floor_reject_only_affected_choice()
    {
        using var databases = new Databases();
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json"));
        var path = Path.Combine(databases.DirectoryPath, "stale-catalog.json");
        var catalog = JObject.Parse(File.ReadAllText(source, Encoding.UTF8));
        File.WriteAllText(path, catalog.ToString(), Encoding.UTF8);
        await using var provider = StorefrontProvider(databases, catalogPath: path);
        var tenant = new BotInstance { Id = "tenant-price-stale", Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 711, TenantStoreNumber = 1, Enabled = true,
            TenantPricingMode = TenantPricingModes.Manual, TenantNormalPricePerGbToman = 4200,
            TenantNormalPricePerDayToman = 600, TenantNationalPricePerGbToman = 110000,
            TenantCardPaymentEnabled = true, TenantCardNumber = "6037991234567890", TenantCardHolderName = "Test" };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.SaveUserStatus(new CredUser { TelegramUserId = 711, IsColleague = true });
        await credentials.MutateWalletAsync(711, 500_000, "tenant-stale-owner-test");
        var state = provider.GetRequiredService<UserStateStore>();
        var client = new StorefrontClient();
        using (provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client }))
        {
            async Task Press(string action)
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleTenantUpdateAsync(client,
                    new Update { CallbackQuery = new CallbackQuery { Id = Guid.NewGuid().ToString("N"),
                        Data = action, From = new Telegram.Bot.Types.User { Id = 912 },
                        Message = new Message { Id = 1, Chat = new Chat { Id = 912 } } } },
                    new CredUser { TelegramUserId = 912 }, await state.GetUserStatus(912), default);
            }
            await Press("TN:svc:normal");
            await Press("TN:GB:normal:10");
            await Press("TN:dur:normal:10:m1");
            var oldAction = client.Callbacks.Last(x => x.StartsWith("TN:DQ:", StringComparison.Ordinal) &&
                x.EndsWith(":CARD", StringComparison.Ordinal));
            await using (var db = databases.Users.CreateDbContext())
            {
                var current = await db.BotInstances.SingleAsync(x => x.Id == tenant.Id);
                current.TenantNormalPricePerGbToman = 4300;
                current.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            await Press(oldAction);
            await using (var db = databases.Users.CreateDbContext())
            {
                Assert.Empty(await db.TenantBotOrders.ToListAsync());
                Assert.Equal(TenantDiscountQuoteStates.Expired, (await db.TenantDiscountQuotes.SingleAsync()).State);
            }
            var normal = (JObject)((JArray)catalog["services"]!).Single(x => (string?)x["key"] == "normal");
            normal["pricePerGb"]!["colleague"] = 4500;
            File.WriteAllText(path, catalog.ToString(), Encoding.UTF8);
            var before = client.Callbacks.Count;
            await Press("TN:services");
            Assert.DoesNotContain(client.Callbacks.Skip(before), x => x == "TN:svc:normal");
            Assert.Contains(client.Callbacks.Skip(before), x => x == "TN:svc:national");
            await Press("TN:svc:normal");
            Assert.Contains("قیمت این گزینه در حال تنظیم است", client.Texts.Last());
            await using var orders = databases.Users.CreateDbContext();
            Assert.Empty(await orders.TenantBotOrders.ToListAsync());
        }
    }

    /// <summary>Legacy percent pricing preserves the public zero-rate and fixed-public unlimited precedence.</summary>
    /// <returns>A task after real customer previews show 65000, 60000 and the unchanged 319000 plan price.</returns>
    /// <remarks>The manual-mode cutover must not change existing tenant percentage orders or TenantUsesUserPrice plans.</remarks>
    [Fact]
    public async Task Storefront_percent_mode_retains_zero_markup_and_user_price_override()
    {
        using var databases = new Databases();
        var catalog = JObject.Parse(File.ReadAllText(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json")), Encoding.UTF8));
        var plans = (JArray)catalog["services"]!;
        var unlimited = (JObject)plans.Single(s => (string?)s["key"] == "unlimited");
        ((JObject)((JArray)unlimited["unlimitedPlans"]!).Single(p => (string?)p["key"] == "u2-m1"))["tenantUsesUserPrice"] = true;
        var path = Path.Combine(databases.DirectoryPath, "percent-catalog.json");
        File.WriteAllText(path, catalog.ToString(), Encoding.UTF8);
        await using var provider = StorefrontProvider(databases, catalogPath: path);
        var tenant = new BotInstance { Id = "tenant-price-percent", Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 711, TenantStoreNumber = 1, Enabled = true };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.SaveUserStatus(new CredUser { TelegramUserId = 711, IsColleague = true });
        await credentials.MutateWalletAsync(711, 500_000, "tenant-percent-owner-test");
        var client = new StorefrontClient();
        using (provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client }))
        {
            async Task Press(string action)
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleTenantUpdateAsync(client,
                    new Update { CallbackQuery = new CallbackQuery { Id = Guid.NewGuid().ToString("N"),
                        Data = action, From = new Telegram.Bot.Types.User { Id = 912 },
                        Message = new Message { Id = 1, Chat = new Chat { Id = 912 } } } },
                    new CredUser { TelegramUserId = 912 },
                    await provider.GetRequiredService<UserStateStore>().GetUserStatus(912), default);
            }
            await Press("TN:dur:normal:10:m1");
            Assert.Contains(65_000L.FormatCurrency(), client.Texts.Last());
            await using (var db = databases.Users.CreateDbContext())
            {
                var current = await db.BotInstances.SingleAsync(x => x.Id == tenant.Id);
                current.TenantPriceMarkupPercent = 20;
                await db.SaveChangesAsync();
            }
            await Press("TN:dur:normal:10:m1");
            Assert.Contains(60_000L.FormatCurrency(), client.Texts.Last());
            await Press("TN:upl:unlimited:u2-m1");
            Assert.Contains(319_000L.FormatCurrency(), client.Texts.Last());
        }
    }

    /// <summary>Manual custom-day renewals re-prompt after a rate edit and create only the newly confirmed order.</summary>
    /// <returns>A task after the panel-backed renewal preview, stale confirmation, and saved order are inspected.</returns>
    /// <remarks>Regression: an unpaid renewal summary cannot silently become an invoice for a new amount.</remarks>
    [Fact]
    public async Task Storefront_manual_renewal_reprompts_after_price_edit_before_order()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "renew-price@example.test";
        const string uuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        panel.SeedClient(email, up: 0, down: 0);
        panel.SetClientIdentity(email, uuid, "renew-price");
        panel.SetClientQuota(email, 10L * 1024 * 1024 * 1024, DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds());
        panel.AttachInbounds(email, 200);
        var metadata = new XuiV3ClientMetadata { ServiceKey = "normal", ServiceKind = "metered",
            TelegramUserId = 912, TenantBotId = "tenant-renew-price" };
        var updated = await ApiServicev3.UpdateClientAsync(panel.ServerInfo,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), email,
            new XuiV3ClientPayload { Email = email, Uuid = uuid, SubId = "renew-price",
                TotalGB = 10L * 1024 * 1024 * 1024, ExpiryTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                Enable = true, Comment = Newtonsoft.Json.JsonConvert.SerializeObject(metadata) });
        Assert.True(updated.Success, updated.Msg);
        await using var provider = StorefrontProvider(databases, panelUrl: panel.Url);
        var tenant = new BotInstance { Id = "tenant-renew-price", Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 711, TenantStoreNumber = 1, Enabled = true, TenantPricingMode = TenantPricingModes.Manual,
            TenantNormalPricePerGbToman = 4200, TenantNormalPricePerDayToman = 600 };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.SaveUserStatus(new CredUser { TelegramUserId = 711, IsColleague = true });
        await credentials.MutateWalletAsync(711, 500_000, "renew-price-owner-test");
        var state = provider.GetRequiredService<UserStateStore>();
        var client = new StorefrontClient();
        using (provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client }))
        {
            await state.SaveUserStatus(new global::User { Id = 912, Flow = "TENANTBOT-renew",
                LastStep = "renew-duration", ConfigLink = email, RenewTargetUuid = uuid,
                SelectedCountry = "normal", TotoalGB = "10" });
            async Task Send(string text)
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleTenantUpdateAsync(client,
                    new Update { Message = new Message { From = new Telegram.Bot.Types.User { Id = 912 },
                        Chat = new Chat { Id = 912 }, Text = text } },
                    new CredUser { TelegramUserId = 912 }, await state.GetUserStatus(912), default);
            }
            await Send("3");
            Assert.Contains(client.Texts, text => text.Contains("خلاصه تمدید") && text.Contains(43_800L.FormatCurrency()));
            Assert.Equal("43800:36500", (await state.GetUserStatus(912))._ConfigPrice);
            await using (var db = databases.Users.CreateDbContext())
            {
                var current = await db.BotInstances.SingleAsync(x => x.Id == tenant.Id);
                current.TenantNormalPricePerGbToman = 4300;
                await db.SaveChangesAsync();
            }
            await Send("✅ تایید");
            await using (var db = databases.Users.CreateDbContext())
                Assert.Empty(await db.TenantBotOrders.ToListAsync());
            Assert.Contains(client.Texts, text => text.Contains("قیمت تمدید تغییر کرده است"));
            Assert.Equal("44800:36500", (await state.GetUserStatus(912))._ConfigPrice);
            await Send("✅ تایید");
            await using var orders = databases.Users.CreateDbContext();
            var order = await orders.TenantBotOrders.AsNoTracking().SingleAsync();
            Assert.Equal(TenantBotOrderKinds.Renew, order.OrderKind);
            Assert.Equal(44_800, order.SalePriceToman);
            Assert.Equal(36_500, order.BaseCostToman);
            Assert.Equal(8_300, order.ProfitToman);
        }
    }

    /// <summary>Runs the owner's actual text handler with the current bot-scoped draft after an edit callback.</summary>
    /// <param name="provider">Isolated production service graph.</param>
    /// <param name="client">Recording Telegram owner transport.</param>
    /// <param name="owner">Colleague with the selected store.</param>
    /// <param name="state">Bot-scoped conversation store.</param>
    /// <param name="text">One owner-entered whole-toman price as invariant digits.</param>
    /// <returns>A task after state and owner message are processed by the real handler.</returns>
    private static async Task PricingOwnerTextAsync(ServiceProvider provider, StorefrontClient client,
        CredUser owner, UserStateStore state, string text)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerMessageAsync(client,
            new Message { From = new Telegram.Bot.Types.User { Id = owner.TelegramUserId },
                Chat = new Chat { Id = owner.TelegramUserId }, Text = text }, owner,
            await state.GetUserStatus(owner.TelegramUserId), new ReplyKeyboardRemove(), default);
    }
}
