using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>A tenant settlement notification still delivers when the persisted and live BotFather identity is unchanged.</summary>
    [Fact]
    public async Task TenantCustomerWallet_Notification_unchanged_identity_delivers_normally()
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration), out var registry, out var clients);
        await SeedCustomerWalletOrderAsync(databases);

        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.Enabled = true;
            store.Token = "789:" + new string('a', 35);
            store.Username = "wallet_a";
            await db.SaveChangesAsync();
            registry.Upsert(store);
        }
        await using (var db = databases.Users.CreateDbContext())
        {
            db.PaymentSettlementNotifications.Add(PaymentSettlementNotification.CreateWalletCredit(
                "atlaspay", 4242, "tenant-a", 123, 123, 100000,
                "wallet credited", DateTime.UtcNow, BotInstanceTypes.Tenant, 600000, 789));
            await db.SaveChangesAsync();
        }

        using var worker = new PaymentSettlementNotificationWorker(
            databases.Users,
            provider.GetRequiredService<BotClientProvider>(),
            NullLogger<PaymentSettlementNotificationWorker>.Instance);
        var claim = typeof(PaymentSettlementNotificationWorker).GetMethod(
            "ClaimDueBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliver = typeof(PaymentSettlementNotificationWorker).GetMethod(
            "DeliverClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batch = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(
            worker, new object[] { CancellationToken.None })!;
        var notification = Assert.Single(batch);
        await (Task)deliver.Invoke(worker, new object[] { notification, CancellationToken.None })!;

        Assert.True(clients.TryGetValue("tenant-a", out var client));
        Assert.Single(client!.Texts);
        Assert.StartsWith("wallet credited", client.Texts.Single(), StringComparison.Ordinal);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.PaymentSettlementNotifications.SingleAsync();
        Assert.Equal(PaymentSettlementNotificationStatuses.Delivered, saved.Status);
        Assert.Null(saved.LastError);
        Assert.Equal(789, saved.WalletOriginTelegramBotId);
        await using var creds = databases.Credentials.CreateDbContext();
        Assert.Single(await creds.WalletOperations.ToListAsync());
        Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
        Assert.Empty(await verify.ReferralPaymentEvents.ToListAsync());
        Assert.Empty(await verify.ReferralRewards.ToListAsync());
    }
}
