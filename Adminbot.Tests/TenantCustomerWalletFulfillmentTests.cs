using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Actual tenant fulfillment recovers a pre-crash debit without repeating customer debit, owner profit or XUI mutation.</summary>
    /// <param name="renew">True exercises the exact-account renewal saga; false exercises the creation coordinator.</param>
    /// <param name="outcome">Fake panel outcome: success, definitive rejection, unknown transport result, or simultaneous successful customer callbacks.</param>
    /// <returns>A task completing after two fresh recovery scopes and durable receipt/account assertions.</returns>
    /// <remarks>No mock balance or process-local-only financial guard is used; both databases are real temporary SQLite files.</remarks>
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "rejected")]
    [InlineData(true, "rejected")]
    [InlineData(false, "ambiguous")]
    [InlineData(true, "ambiguous")]
    [InlineData(false, "callbacks")]
    [InlineData(false, "applied-ambiguous")]
    [InlineData(true, "applied-ambiguous")]
    public async Task TenantCustomerWallet_Actual_saga_restart_preserves_financial_and_xui_effects(bool renew, string outcome)
    {
        using var databases = new Databases();
        var posts = 0;
        const string uuid = "11111111-1111-1111-1111-111111111111";
        JObject? client = renew ? new JObject
        {
            ["id"] = 41, ["email"] = "wallet-renew@example.test", ["uuid"] = uuid,
            ["totalGB"] = 10L * 1024 * 1024 * 1024, ["expiryTime"] = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            ["tgId"] = 123, ["limitIp"] = 0, ["enable"] = true, ["subId"] = "test-sub", ["inboundIds"] = new JArray(200),
            ["comment"] = Newtonsoft.Json.JsonConvert.SerializeObject(new XuiV3ClientMetadata
            { TelegramUserId = 123, TenantBotId = "tenant-a", ServiceKey = "normal", ServiceKind = XuiV3ServiceKinds.Metered })
        } : null;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref posts);
                var body = JObject.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync());
                if (outcome == "ambiguous") { context.Abort(); return; }
                // Creation only accepts the panel's documented pre-mutation validation errors as definitive proof.
                if (outcome == "rejected") { await context.Response.WriteAsync("{\"success\":false,\"msg\":\"client email is required\"}"); return; }
                if (renew)
                {
                    foreach (var property in body.Properties()) client![property.Name] = property.Value.DeepClone();
                    client!["uuid"] = uuid; client["id"] = 41;
                }
                else client = (JObject?)body["client"] ?? new JObject();
                client!["inboundIds"] = renew ? new JArray(200) : body["inboundIds"]!.DeepClone();
                // The panel applied the mutation but its HTTP acknowledgement was lost before local settlement.
                if (outcome == "applied-ambiguous") { context.Abort(); return; }
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}"); return;
            }
            var path = context.Request.Path.Value ?? "";
            if (path.Contains("links", StringComparison.OrdinalIgnoreCase))
            { await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}"); return; }
            if (renew && path.Contains("/clients/get/", StringComparison.OrdinalIgnoreCase))
            {
                await context.Response.WriteAsync(new JObject { ["success"] = true,
                    ["obj"] = new JObject { ["client"] = client!.DeepClone(), ["inboundIds"] = new JArray(200) } }.ToString()); return;
            }
            JToken obj = renew ? new JArray(client!.DeepClone()) : client?.DeepClone() ?? new JObject();
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = obj }.ToString());
        });
        await app.StartAsync();
        try
        {
            var configuration = AtlasTenantConfiguration(databases, app.Urls.Single());
            await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out var registry, out var clients);
            var (wallet, funding, order) = await SeedCustomerWalletOrderAsync(databases);
            await wallet.AddEmptyUser(456); await wallet.PromotOrDemote(456, true);
            await using (var db = databases.Users.CreateDbContext())
            {
                var store = await db.BotInstances.SingleAsync(); store.Token = "789:" + new string('a', 35); store.Username = "wallet_test";
                store.TenantPriceMarkupPercent = 20;
                store.TenantOwnerNotificationBotId = "main";
                var row = await db.TenantBotOrders.SingleAsync();
                row.TrafficGb = 10; row.DurationKey = "m1"; row.SalePriceToman = 60000; row.BaseCostToman = 50000; row.ProfitToman = 10000;
                if (renew) { row.OrderKind = TenantBotOrderKinds.Renew; row.TargetAccountEmail = "wallet-renew@example.test"; row.TargetAccountUuid = uuid; row.RenewalServiceResolutionMode = "metadata"; }
                if (outcome == "callbacks") db.TenantBotOrders.Remove(row);
                await db.SaveChangesAsync(); registry.Upsert(store); order = row;
            }
            if (outcome == "callbacks")
            {
                var start = Signal();
                var clicks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    await using var scope = provider.CreateAsyncScope();
                    var transport = new StorefrontClient();
                    using var context = new BotContextAccessor().Push(new BotRuntimeContext
                    { Config = new BotInstanceConfig { Id = "tenant-a", Type = "tenant" }, Client = transport });
                    var callback = new Telegram.Bot.Types.Update { CallbackQuery = new Telegram.Bot.Types.CallbackQuery {
                        Id = Guid.NewGuid().ToString("N"), Data = "TN:PAYWALLET:Pay:m:normal:10:m1",
                        From = new Telegram.Bot.Types.User { Id = 123 },
                        Message = new Telegram.Bot.Types.Message { Id = 55, Chat = new Telegram.Bot.Types.Chat { Id = 123 } } } };
                    var method = typeof(TenantBotService).GetMethod("TryHandleCustomerWalletAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    await (Task<bool>)method.Invoke(scope.ServiceProvider.GetRequiredService<TenantBotService>(), new object[] {
                        transport, callback, await wallet.GetUserStatusWithId(123), new global::User { Id = 123 }, CancellationToken.None })!;
                })).ToArray();
                start.SetResult(); await Task.WhenAll(clicks);
                await using var db = databases.Users.CreateDbContext();
                order = await db.TenantBotOrders.SingleAsync();
            }
            else
            {
                // Simulate process death after credentials.db committed but before users.db recorded payment or audit.
                await wallet.TryDebitWalletIfSufficientAsync(123, 60000, TenantCustomerWalletFunding.DebitKey(order.Id), "tenant-a");
            }
            await using (var db = databases.Users.CreateDbContext())
            { var store = await db.BotInstances.SingleAsync(); TenantCustomerWalletPolicy.Revoke(store); await db.SaveChangesAsync(); }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TenantBotService>().RecoverCustomerWalletOrderAsync(order.Id, default);
            }
            await using var users = databases.Users.CreateDbContext();
            var result = await users.TenantBotOrders.SingleAsync();
            Assert.Equal(1, posts);
            var success = outcome is "success" or "callbacks" or "applied-ambiguous";
            Assert.Equal(success, result.IsFulfilled);
            Assert.Equal(outcome == "rejected" ? 500000 : 440000, await wallet.GetAccountBalance(123));
            Assert.Equal(success ? 10000 : 0, await wallet.GetAccountBalance(456));
            await using var credentials = databases.Credentials.CreateDbContext();
            Assert.Equal(1, await credentials.WalletOperations.CountAsync(x => x.OperationKey == TenantCustomerWalletFunding.DebitKey(order.Id)));
            Assert.Equal(success ? 1 : 0, await credentials.WalletOperations.CountAsync(x => x.OperationKey == $"tenant:{order.Id}:profit"));
            Assert.False(await credentials.WalletOperations.AnyAsync(x => x.OperationKey == $"tenant:{order.Id}:base-cost"));
            Assert.Equal(outcome == "rejected" ? 1 : 0, await credentials.WalletOperations.CountAsync(x => x.OperationKey.EndsWith(":refund")));
            Assert.Equal(success ? 1 : 0, await users.TenantBotLedgerEntries.CountAsync());
            if (outcome == "callbacks")
            {
                await using var deliveryScope = provider.CreateAsyncScope();
                await deliveryScope.ServiceProvider.GetRequiredService<TenantOrderNotificationDeliveryService>()
                    .SendAsync(result, TenantOrderNotificationKinds.OwnerSaleNotification, default);
                var ownerMessage = Assert.Single(clients["main"].Texts);
                Assert.Contains("کیف پول مشتری", ownerMessage);
                Assert.DoesNotContain("440,000", ownerMessage);
                Assert.Equal(440000, await wallet.GetAccountBalance(123));
            }
        }
        finally { await app.StopAsync(); }
    }
}
