using System.Reflection;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Telegram.Bot.Types;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>The owned home and a purchase guide callback reach the same chooser, and download uses the existing resolver.</summary>
    /// <returns>A task that verifies the visible platform actions and stale-download behavior using fake Telegram and release services.</returns>
    [Fact]
    public async Task Owned_connection_guide_uses_one_chooser_and_existing_download_callback()
    {
        const long customerId = 723;
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: true);
        var releases = new CountingReleaseService();
        var downloadClient = new GatewayTelegramClient();
        var guideClient = new StorefrontClient();
        var service = BuildClientDownloadTelegramService(databases, flag, releases, downloadClient);
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-guide", Type = BotInstanceTypes.Owned },
            Client = guideClient
        });
        var handleMessage = typeof(TelegramBotService).GetMethod("TryHandleOwnedTutorialMessageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handleCallback = typeof(TelegramBotService).GetMethod("TryHandleOwnedTutorialCallbackAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var home = new Message { Chat = new Chat { Id = customerId }, Text = ConnectionGuideFlow.MenuCommand };
        Assert.True(await (Task<bool>)handleMessage.Invoke(service, new object[] { guideClient, home, CancellationToken.None })!);
        var fromHome = guideClient.Callbacks.ToArray();
        Assert.Equal(new[] { "owned:tutorial:guide:android", "owned:tutorial:guide:ios", "owned:tutorial:guide:windows" }, fromHome);
        guideClient.Callbacks.Clear();

        foreach (var action in new[] { "owned:tutorial:guide:open", fromHome[0], "owned:tutorial:guide:lesson:android" })
            Assert.True(await (Task<bool>)handleCallback.Invoke(service, new object[]
                { guideClient, ConnectionGuideCallback(customerId, action), CancellationToken.None })!);
        Assert.Equal(fromHome, guideClient.Callbacks.Take(3).ToArray());
        Assert.Contains("APPDL:android", guideClient.Callbacks);
        Assert.Contains("آموزش اتصال", guideClient.Labels);
        Assert.Contains(guideClient.MediaGroupCaptions, caption => caption.Contains("v2rayNG"));
        Assert.Equal(0, releases.Calls);

        await InvokeOwnedPlatformCallbackAsync(service, downloadClient, "APPDL:android");
        Assert.Equal(1, releases.Calls);

        flag.SetEnabled(false);
        await InvokeOwnedPlatformCallbackAsync(service, downloadClient, "APPDL:android");
        Assert.Equal(1, releases.Calls);
        Assert.Contains(ClientDownloadCallbacks.DisabledMessage, guideClient.Answers);
    }

    /// <summary>A successful owned panel purchase keeps its subscription link and adds the guide in the same QR message.</summary>
    /// <returns>A task exercising purchase confirmation, local wallet settlement and Telegram delivery through production handlers.</returns>
    [Fact]
    public async Task Paid_owned_purchase_delivers_the_connection_guide_with_its_subscription_link()
    {
        const long customerId = 725;
        const string subscriptionBase = "https://example.invalid/sub";
        using var databases = new Databases();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var panel = builder.Build();
        JObject? created = null;
        panel.Run(async context =>
        {
            var path = context.Request.Path.Value ?? "";
            JToken result = new JArray();
            if (path == "/panel/api/clients/add")
            {
                using var reader = new StreamReader(context.Request.Body);
                var request = JObject.Parse(await reader.ReadToEndAsync());
                created = new JObject { ["client"] = request["client"]!.DeepClone(), ["inboundIds"] = request["inboundIds"]!.DeepClone() };
                result = new JObject();
            }
            else if (path.Contains("/clients/get/", StringComparison.OrdinalIgnoreCase) && created != null)
            {
                result = created.DeepClone();
            }
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = result }.ToString());
        });
        await panel.StartAsync();
        var flag = new ClientDownloadAvailabilityProbe(enabled: true);
        await using var provider = StorefrontProvider(databases, panelUrl: panel.Urls.Single(), configureServices: services =>
        {
            var current = (IConfiguration)services.Last(service => service.ServiceType == typeof(IConfiguration)).ImplementationInstance!;
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddConfiguration(current)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XuiApiVersionMode"] = "v3", ["XuiV3SubLinkBaseUrl"] = subscriptionBase
                }).Build());
            services.AddSingleton<IClientDownloadAvailability>(flag);
        });
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-guide-delivery", Type = BotInstanceTypes.Owned }, Client = client
        });
        await using var scope = provider.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
        var state = scope.ServiceProvider.GetRequiredService<UserStateStore>();
        var flow = scope.ServiceProvider.GetRequiredService<XuiV3BotFlowService>();
        await credentials.AddEmptyUser(customerId);
        await credentials.SavePhoneNumber(customerId, "+989123456789");
        await credentials.MutateWalletAsync(customerId, 1_000_000, "connection-guide-test:owned-funding");
        var customer = await credentials.GetUserStatusWithId(customerId);
        await state.SaveUserStatus(new User
        {
            Id = customerId, Flow = "xui-v3", LastStep = "confirm", SelectedCountry = "normal",
            SelectedPeriod = "m1", TotoalGB = "10", PendingAccountCount = 1
        });
        var plan = new XuiV3PurchaseSelection { ServiceKey = "normal", DurationKey = "m1", TrafficGb = 10 };
        // The legacy panel result adapter also saves account state through the configured static database path.
        var originalDatabasePath = UserDbContext.DatabasePath;
        UserDbContext.ConfigureDatabasePath(Path.Combine(databases.DirectoryPath, "users.db"));
        try
        {
            Assert.True(await flow.TryHandleCallbackAsync(client, ConnectionGuideCallback(customerId, XuiV3PurchaseCallbacks.Confirm(plan)),
                customer, await state.GetUserStatus(customerId), null, default));
        }
        finally
        {
            UserDbContext.ConfigureDatabasePath(originalDatabasePath);
        }
        Assert.True(client.SinglePhotoCaptions.Count == 1, string.Join(Environment.NewLine, client.Texts));
        Assert.Contains(subscriptionBase, client.SinglePhotoCaptions[0]);
        Assert.Contains("owned:tutorial:guide:open", client.Callbacks);
        Assert.Contains("📲 دانلود نرم‌افزار و راهنمای اتصال", client.Labels);
        Assert.True(await credentials.GetAccountBalance(customerId) < 1_000_000);
    }

    /// <summary>The tenant home guide wins over pending wallet input and still offers the matching lesson with downloads disabled.</summary>
    /// <returns>A task proving navigation through the production storefront dispatcher preserves the bot-scoped input step.</returns>
    [Fact]
    public async Task Storefront_connection_guide_preserves_wallet_input_when_download_is_disabled()
    {
        const long customerId = 724;
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: false);
        await using var provider = StorefrontProvider(databases, configureServices: services =>
            services.AddSingleton<IClientDownloadAvailability>(flag));
        var tenant = new BotInstance { Id = "tenant-guide", Type = BotInstanceTypes.Tenant, Enabled = true, OwnerTelegramUserId = 711 };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(new CredUser { TelegramUserId = 711, IsColleague = true });
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant },
            Client = client
        });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var state = scope.ServiceProvider.GetRequiredService<UserStateStore>();
        var customer = new CredUser { TelegramUserId = customerId };
        await state.SaveUserStatus(new User { Id = customerId, Flow = "tenant-wallet-charge", LastStep = "amount" });
        var home = new Message
        {
            Chat = new Chat { Id = customerId }, From = new Telegram.Bot.Types.User { Id = customerId },
            Text = ConnectionGuideFlow.MenuCommand
        };
        Assert.True(await service.TryHandleTenantUpdateAsync(client, new Update { Message = home }, customer,
            await state.GetUserStatus(customerId), default));
        Assert.Equal(new[] { "TN:guide:android", "TN:guide:ios", "TN:guide:windows" }, client.Callbacks);
        foreach (var action in new[] { "TN:guide:ios", "TN:guide:lesson:ios" })
        {
            Assert.True(await service.TryHandleTenantUpdateAsync(client,
                new Update { CallbackQuery = ConnectionGuideCallback(customerId, action) }, customer,
                await state.GetUserStatus(customerId), default));
        }
        Assert.DoesNotContain("APPDL:ios", client.Callbacks);
        Assert.Contains("آموزش اتصال", client.Labels);
        Assert.Contains(client.MediaGroupCaptions, caption => caption.Contains("V2Box"));
        Assert.Equal("amount", (await state.GetUserStatus(customerId)).LastStep);
    }

    /// <summary>Tenant fulfillment and resend place the guide only on the buyer's new purchase message.</summary>
    /// <param name="hasLink">True to exercise QR delivery, false to exercise the text fallback.</param>
    /// <param name="downloadsEnabled">Live global setting used to label the purchase guide button.</param>
    /// <returns>A task verifying purchase and renewal sends with the real outbox and resend renderers.</returns>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Storefront_connection_guide_is_on_purchase_delivery_and_resend_only(bool hasLink, bool downloadsEnabled)
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases, extraConfiguration: new Dictionary<string, string?>
        {
            ["latestClientDownloadEnabled"] = downloadsEnabled.ToString()
        });
        await using (provider)
        {
            var order = HistoricalOrder("connection-guide-delivery", fulfilled: true);
            order.OrderKind = TenantBotOrderKinds.Purchase;
            if (!hasLink)
                order.CreatedSubLink = null;
            registry.Upsert(new BotInstance { Id = order.TenantBotId, Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(10021) });
            await using var scope = provider.CreateAsyncScope();
            var delivery = scope.ServiceProvider.GetRequiredService<TenantOrderNotificationDeliveryService>();
            var tenant = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var resend = typeof(TenantBotService).GetMethod("SENDTENANTORDERACCOUNTDETAILSASYNC",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.NotNull(await delivery.SendAsync(order, TenantOrderNotificationKinds.CustomerAccountDelivery, default));
            var client = clients[order.TenantBotId];
            Assert.True(await (Task<bool>)resend.Invoke(tenant, new object[] { order, true, false, CancellationToken.None })!);
            Assert.Equal(new[] { "TN:guide:open", "TN:guide:open" }, client.Callbacks);
            Assert.All(client.Labels, label => Assert.Equal(downloadsEnabled
                ? "📲 دانلود نرم‌افزار و راهنمای اتصال" : "📲 راهنمای اتصال", label));
            Assert.Equal(hasLink ? 2 : 0, client.SinglePhotoSends);
            if (hasLink)
                Assert.All(client.SinglePhotoCaptions, caption => Assert.Contains(order.CreatedSubLink!, caption));
            else
                Assert.Equal(2, client.Texts.Count);

            order.OrderKind = TenantBotOrderKinds.Renew;
            Assert.NotNull(await delivery.SendAsync(order, TenantOrderNotificationKinds.CustomerAccountDelivery, default));
            Assert.True(await (Task<bool>)resend.Invoke(tenant, new object[] { order, true, false, CancellationToken.None })!);
            Assert.Equal(2, client.Callbacks.Count);
        }
    }

    /// <summary>Creates a synthetic guide callback carrying a private Telegram chat and no account identifiers.</summary>
    /// <param name="customerId">Numeric Telegram id of the test sender and their private chat.</param>
    /// <param name="action">Exact inline action from the purchase or connection guide.</param>
    /// <returns>A Telegram-shaped callback used with the recording transport.</returns>
    private static CallbackQuery ConnectionGuideCallback(long customerId, string action) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Data = action, From = new Telegram.Bot.Types.User { Id = customerId },
        Message = new Message { Id = 1, Chat = new Chat { Id = customerId } }
    };
}
