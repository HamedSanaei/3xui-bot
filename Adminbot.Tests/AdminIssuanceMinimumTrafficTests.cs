using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Proves every configured administrator can issue sub-minimum accounts to an ordinary recipient.</summary>
    /// <param name="actorId">Synthetic authenticated Telegram sender id from the configured super-admin list.</param>
    /// <returns>A task completing after single and bulk panel requests have been checked.</returns>
    /// <remarks>Uses isolated SQLite and loopback HTTP only. Customer and colleague minimums must remain enforced;
    /// neither a recipient role nor audit metadata may grant the administrator exemption.</remarks>
    [Theory]
    [InlineData(7001L)]
    [InlineData(7002L)]
    public async Task Admin_issuance_bypasses_only_customer_minimum(long actorId)
    {
        using var databases = new Databases();
        var catalog = WriteTenantCatalog(databases, root =>
        {
            var normal = root["services"]!.Single(s => (string?)s["key"] == "normal");
            normal["minimumTrafficGb"] = 10;
        });
        await using var panel = await ProvisionalPanel.StartAsync();
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(BuildConfiguration(databases, catalog, panel.Url))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminsUserIds:0"] = "7001", ["AdminsUserIds:1"] = "7002"
            }).Build();
        var service = new XuiV3PurchaseService(configuration, databases.Users);
        var selection = new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 1, DurationKey = "days-1" };
        Assert.Throws<InvalidOperationException>(() => service.ResolveOwnedPurchase(selection, false));
        Assert.Throws<InvalidOperationException>(() => service.ResolveOwnedPurchase(selection, true));
        Assert.Throws<InvalidOperationException>(() => service.ResolveTenantPurchase(selection, false));
        Assert.Throws<UnauthorizedAccessException>(() => service.ResolvePurchase(selection, false, 7003));
        var resolved = service.ResolvePurchase(selection, false, actorId);
        Assert.Equal(OneGibBytes, resolved.TrafficBytes);
        Assert.Equal(1, resolved.DurationDays);
        selection.TrafficGb = 0;
        Assert.Throws<InvalidOperationException>(() => service.ResolvePurchase(selection, false, actorId));
        selection.TrafficGb = 10;
        Assert.Equal(10, service.ResolveOwnedPurchase(selection, false).TrafficGb);
        selection.TrafficGb = 1;

        var recipient = new CredUser { TelegramUserId = 5150, IsColleague = false };
        var server = new ServerInfo { Url = panel.Url, ApiToken = "test-only" };
        var single = await service.CreateAccountAsync(recipient, server, selection, panel.Url,
            metadataOptions: new XuiV3AccountMetadataOptions
            {
                OperationKey = $"admin-minimum:{actorId}:single", SaveUserStatus = false,
                CreatedByTelegramUserId = actorId, LastAction = "admin-create"
            }, adminActorTelegramUserId: actorId);
        Assert.True(single.Success, single.Message);
        Assert.Equal(OneGibBytes, panel.LastClient!["totalGB"]!.Value<long>());
        Assert.Equal(5150, panel.LastClient["tgId"]!.Value<long>());

        var bulk = await service.CreateBulkAccountsAsync(recipient, server, selection, panel.Url,
            new XuiV3BulkCreateOptions
            {
                BulkOrderId = $"admin-minimum:{actorId}:bulk", AccountCount = 2,
                SaveUserStatus = false, DelayBetweenCreatesMs = 0, CreatedByTelegramUserId = actorId
            }, adminActorTelegramUserId: actorId);
        Assert.Equal(2, bulk.SuccessfulCount);
        Assert.Empty(bulk.Failures);
        Assert.Equal(3, panel.PostCount);
        Assert.Equal(OneGibBytes, panel.LastClient!["totalGB"]!.Value<long>());
        Assert.Equal(5150, panel.LastClient["tgId"]!.Value<long>());
    }
}
