using Adminbot.Domain;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Distinguishes two offers with the same quota and verifies that the role-based package audience remains enforced.</summary>
    /// <returns>A task that verifies the public captions and stable ready-package callback keys.</returns>
    [Fact]
    public async Task Customer_service_labels_distinguish_equal_volume_and_keep_existing_package_visibility()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        await using var scope = provider.CreateAsyncScope();
        var purchases = scope.ServiceProvider.GetRequiredService<XuiV3PurchaseService>();
        var ready = new XuiV3PurchaseSelection { ServiceKey = "unlimited", UnlimitedPlanKey = "eco-m1", AccountCount = 1 };
        var metered = new XuiV3PurchaseSelection { ServiceKey = "normal", DurationKey = "m1", TrafficGb = 50, AccountCount = 1 };
        Assert.Contains("نوع سرویس: بسته نامحدود با سقف مصرف", purchases.BuildSummaryText(ready, isColleague: true));
        Assert.Contains("نوع سرویس: بسته حجمی عادی", purchases.BuildSummaryText(metered, isColleague: true));

        var payload = XuiV3PurchaseCallbacks.UnlimitedPlan(ready.ServiceKey, ready.UnlimitedPlanKey);
        var partnerButtons = purchases.BuildUnlimitedPlanKeyboard(ready.ServiceKey, isColleague: true).InlineKeyboard.SelectMany(row => row);
        var eco = Assert.Single(partnerButtons, button => button.CallbackData == payload);
        Assert.Contains("بسته نامحدود با سقف مصرف", eco.Text);
        Assert.Contains("50 گیگ", eco.Text);
        Assert.Contains("31 روز", eco.Text);
        Assert.DoesNotContain(purchases.BuildUnlimitedPlanKeyboard(ready.ServiceKey, isColleague: false).InlineKeyboard.SelectMany(row => row),
            button => button.CallbackData == payload);

        var normal = purchases.GetEnabledServices().Single(service => service.Key == "normal");
        Assert.Same(normal, XuiCustomerServiceText.FindCategory("نت عادی [normal]", new[] { normal }));
        Assert.Same(normal, XuiCustomerServiceText.FindCategory("بسته حجمی عادی [normal]", new[] { normal }));
        Assert.Null(XuiCustomerServiceText.FindCategory("بسته حجمی عادی [unlimited]", new[] { normal }));
    }

    /// <summary>An ambiguous owned renewal takes an explicit compatible choice and discards it if authoritative detail later disagrees.</summary>
    /// <returns>A task verifying real bot-scoped state, visible prompts and the absence of any paid renewal or panel write by the handler.</returns>
    /// <remarks>Both catalog families share inbound 200 in this loopback fixture. A 50 GB client with a running expiry cannot be identified by volume or term.</remarks>
    [Fact]
    public async Task Owned_renewal_requires_explicit_legacy_family_and_restarts_preview_if_panel_type_changes()
    {
        const long customerId = 7100456;
        const string email = "service-family-renew";
        var uuid = Guid.NewGuid().ToString();
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        await using var provider = StorefrontProvider(databases, panelUrl: panel.Url, configureServices: services =>
        {
            var original = (IConfiguration)services.Last(x => x.ServiceType == typeof(IConfiguration)).ImplementationInstance!;
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddConfiguration(original)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XuiApiVersionMode"] = "v3" }).Build());
        });
        var configuration = provider.GetRequiredService<IConfiguration>();
        panel.SeedClient(email, up: 0, down: 0);
        panel.SetClientIdentity(email, uuid, "family-test-sub");
        panel.AttachInbounds(email, 200);
        var payload = new XuiV3ClientPayload
        {
            Email = email, Uuid = uuid, SubId = "family-test-sub", TgId = customerId, Enable = true,
            TotalGB = 50L * 1024 * 1024 * 1024, ExpiryTime = DateTimeOffset.UtcNow.AddDays(31).ToUnixTimeMilliseconds()
        };
        Assert.True((await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, email, payload)).Success);

        var client = new StorefrontClient();
        using var bot = new BotContextAccessor().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = "owned-renew-test", Type = BotInstanceTypes.Owned }, Client = client });
        await using var scope = provider.CreateAsyncScope();
        var state = scope.ServiceProvider.GetRequiredService<UserStateStore>();
        var flow = scope.ServiceProvider.GetRequiredService<XuiV3BotFlowService>();
        var customer = new CredUser { TelegramUserId = customerId, PhoneNumber = "+989123456789", AccountBalance = 1_000_000 };
        await state.SaveUserStatus(new User { Id = customerId, Flow = "xui-v3-renew", LastStep = "renew-account" });

        await flow.TryHandleRenewAsync(client, ServiceFamilyMessage(customerId, email), customer,
            await state.GetUserStatus(customerId), null, default);
        var saved = await state.GetUserStatus(customerId);
        Assert.Equal("renew-service-category", saved.LastStep);
        Assert.Contains("بسته نامحدود با سقف مصرف [unlimited]", client.ReplyButtons);
        Assert.Contains("بسته حجمی عادی [normal]", client.ReplyButtons);
        Assert.DoesNotContain("نت ملی [national]", client.ReplyButtons);

        await flow.TryHandleRenewAsync(client, ServiceFamilyMessage(customerId, "بسته نامحدود با سقف مصرف [unlimited]"),
            customer, saved, null, default);
        saved = await state.GetUserStatus(customerId);
        Assert.Equal("renew-unlimited-plan", saved.LastStep);
        Assert.Equal("unlimited", saved.SelectedCountry);
        Assert.Equal(TenantRenewalServiceResolutionModes.CustomerSelectedLegacy, saved.RenewalServiceResolutionMode);

        payload.Comment = "{\"telegramUserId\":7100456,\"serviceKey\":\"normal\",\"serviceKind\":\"metered\"}";
        Assert.True((await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, email, payload)).Success);
        var writesBefore = panel.UpdateRequestCount;
        client.ReplyButtons.Clear();
        await flow.TryHandleRenewAsync(client, ServiceFamilyMessage(customerId, "u2-m1"), customer, saved, null, default);
        saved = await state.GetUserStatus(customerId);
        Assert.Equal("renew-same-service", saved.LastStep);
        Assert.Equal("normal", saved.SelectedCountry);
        Assert.Contains("تمدید همین نوع سرویس", client.ReplyButtons);
        Assert.DoesNotContain("تایید تمدید", client.ReplyButtons);
        Assert.Equal(writesBefore, panel.UpdateRequestCount);

        // After a valid same-family preview, a second change must still be caught at the payment boundary.
        foreach (var input in new[] { "تمدید همین نوع سرویس", "50", "m1" })
            await flow.TryHandleRenewAsync(client, ServiceFamilyMessage(customerId, input), customer,
                await state.GetUserStatus(customerId), null, default);
        Assert.Equal("renew-confirm", (await state.GetUserStatus(customerId)).LastStep);
        Assert.Contains(client.Texts, text => text.Contains("بسته حجمی عادی") && text.Contains("وضعیت تمدید"));

        payload.Comment = "{\"telegramUserId\":7100456,\"serviceKey\":\"unlimited\",\"serviceKind\":\"unlimited\"}";
        Assert.True((await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, email, payload)).Success);
        var writesBeforePayment = panel.UpdateRequestCount;
        await flow.TryHandleRenewAsync(client, ServiceFamilyMessage(customerId, "تایید تمدید"), customer,
            await state.GetUserStatus(customerId), null, default);
        saved = await state.GetUserStatus(customerId);
        Assert.Equal("renew-same-service", saved.LastStep);
        Assert.Equal("unlimited", saved.SelectedCountry);
        Assert.Equal(writesBeforePayment, panel.UpdateRequestCount);
        await using var db = databases.Users.CreateDbContext();
        Assert.False(await db.XuiV3RenewalOperations.AnyAsync());

        // The same 50 GB target in a storefront offers the existing eligible Eco package after same-family confirmation.
        var tenant = new BotInstance { Id = "tenant-renew-test", Type = BotInstanceTypes.Tenant, Enabled = true, OwnerTelegramUserId = 711 };
        var storefront = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var tenantFlow = typeof(TenantBotService).GetMethod("TRYHANDLETENANTRENEWASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var tenantScope = new BotContextAccessor().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client });
        client.ReplyButtons.Clear();
        foreach (var input in new[] { "تمدید اکانت", email, "تمدید همین نوع سرویس" })
        {
            var handled = (Task<bool>)tenantFlow.Invoke(storefront, new object?[]
            { client, ServiceFamilyMessage(customerId, input), tenant, customer, await state.GetUserStatus(customerId), null, CancellationToken.None })!;
            Assert.True(await handled);
        }
        saved = await state.GetUserStatus(customerId);
        Assert.Equal("renew-unlimited-plan", saved.LastStep);
        Assert.Contains(client.ReplyButtons, text => text.Contains("بسته نامحدود با سقف مصرف") && text.Contains("50 گیگ") &&
            text.Contains("[eco-m1]"));
    }

    /// <summary>Creates a synthetic private message for a service-family choice.</summary>
    /// <param name="sender">Numeric Telegram id of the test customer.</param>
    /// <param name="text">Exact renewal input sent to the production handler.</param>
    /// <returns>A test Telegram message carrying no real user or bot credentials.</returns>
    private static Message ServiceFamilyMessage(long sender, string text) => new()
    {
        Id = 1, Chat = new Chat { Id = sender }, From = new Telegram.Bot.Types.User { Id = sender }, Text = text
    };
}
