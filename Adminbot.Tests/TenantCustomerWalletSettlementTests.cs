using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>All five actual settlement services retain tenant origin, exclude referrals and deliver through the tenant after revocation.</summary>
    /// <returns>A task completing after duplicate settlements and delivery-only outbox claims against real SQLite.</returns>
    /// <remarks>The customer already has an owned referral relationship, so an accidental owned attribution would create a reward.</remarks>
    [Fact]
    public async Task TenantCustomerWallet_All_settlements_exclude_referrals_and_deliver_through_origin()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder().AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:1"))
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["referral:enabled"] = "true", ["referral:minimumEligiblePaymentAmountToman"] = "1",
                ["referral:firstPayment:referrerRewardPercent"] = "10", ["referral:firstPayment:referredRewardPercent"] = "10"
            }).Build();
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out var registry, out var clients);
        var (wallet, _, _) = await SeedCustomerWalletOrderAsync(databases);
        await wallet.AddEmptyUser(999);
        var referrals = provider.GetRequiredService<ReferralService>();
        await referrals.RegisterRelationshipAsync(123, ReferralCodeCodec.Encode(999), "main", BotInstanceTypes.Owned);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var hp = HooshPayPaymentInfo.CreateWalletCharge(123, 100000, "https://merchant.example/hp", "https://merchant.example/return", 321);
        hp.BotId = "tenant-a"; hp.WalletOriginBotType = BotInstanceTypes.Tenant; hp.WalletOriginTelegramBotId = 789; hp.PaymentStatus = "paid";
        var tm = TetraminatorPaymentInfo.CreateWalletCharge(123, 100000, "https://merchant.example/tm", 321);
        tm.BotId = "tenant-a"; tm.WalletOriginBotType = BotInstanceTypes.Tenant; tm.WalletOriginTelegramBotId = 789; tm.PaymentStatus = "paid"; tm.PayId = "test-payment"; tm.PaidAtUtc = DateTime.UtcNow;
        var up = UniquePayPaymentInfo.CreateWalletCharge(123, 321, 100000, 0);
        up.BotId = "tenant-a"; up.WalletOriginBotType = BotInstanceTypes.Tenant; up.WalletOriginTelegramBotId = 789; up.PaymentStatus = "paid"; up.PaidAtUtc = DateTime.UtcNow;
        var ap = AtlasPayPaymentInfo.CreateWalletCharge(123, 321, 100000);
        ap.BotId = "tenant-a"; ap.WalletOriginBotType = BotInstanceTypes.Tenant; ap.WalletOriginTelegramBotId = 789; ap.ProviderStatus = "confirmed"; ap.PaidAtUtc = DateTime.UtcNow;
        var np = SwapinoPaymentInfo.CreateCryptoCharge(123, 100000, "https://merchant.example/np", chatId: 321);
        np.BotId = "tenant-a"; np.WalletOriginBotType = BotInstanceTypes.Tenant; np.WalletOriginTelegramBotId = 789; np.PaymentStatus = "finished";
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.Token = "789:" + new string('a', 35); store.Username = "wallet_test";
            TenantCustomerWalletPolicy.Revoke(store); registry.Upsert(store);
            db.AddRange(hp, tm, up, ap, np); await db.SaveChangesAsync();
        }
        for (var replay = 0; replay < 2; replay++)
        {
            await services.GetRequiredService<HooshPaySettlementService>().ApplyFinishedPaymentAsync(hp, "fixture");
            await services.GetRequiredService<TetraminatorSettlementService>().ApplyOfficialPaymentAsync(tm, "fixture");
            await services.GetRequiredService<UniquePaySettlementService>().ApplyOfficialPaymentAsync(up, "fixture");
            await services.GetRequiredService<AtlasPaySettlementService>().ApplyOfficialPaymentAsync(ap, "fixture");
            await services.GetRequiredService<NowPaymentsSettlementService>().ApplyFinishedPaymentAsync(np, "fixture");
        }
        Assert.Equal(1000000, await wallet.GetAccountBalance(123));
        Assert.Equal(0, await wallet.GetAccountBalance(999));
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Single(await db.ReferralRelationships.ToListAsync());
            Assert.Empty(await db.ReferralPaymentEvents.ToListAsync()); Assert.Empty(await db.ReferralRewards.ToListAsync());
            Assert.Equal(5, await db.WalletLedgerEntries.CountAsync(x => x.BotType == "tenant" && x.BotId == "tenant-a"));
            var notifications = await db.PaymentSettlementNotifications.ToListAsync(); Assert.Equal(5, notifications.Count);
            Assert.All(notifications, n =>
            {
                Assert.Equal(321, n.ChatId);
                Assert.Equal(BotInstanceTypes.Tenant, n.WalletOriginBotType);
                Assert.Equal(789, n.WalletOriginTelegramBotId);
                Assert.Contains("موجودی جدید", n.MessageText);
            });
        }
        // Exercise production claim/delivery once without starting a timed background polling loop.
        using var worker = new PaymentSettlementNotificationWorker(databases.Users, services.GetRequiredService<BotClientProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentSettlementNotificationWorker>.Instance);
        var claim = typeof(PaymentSettlementNotificationWorker).GetMethod("ClaimDueBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliver = typeof(PaymentSettlementNotificationWorker).GetMethod("DeliverClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batch = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(worker, new object[] { CancellationToken.None })!;
        foreach (var notification in batch) await (Task)deliver.Invoke(worker, new object[] { notification, CancellationToken.None })!;
        Assert.Equal(5, clients["tenant-a"].Texts.Count);
        Assert.False(clients.TryGetValue("main", out var owned) && owned.Texts.Count > 0);
        Assert.Equal(1000000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(5, await verify.PaymentSettlementNotifications.CountAsync(x => x.Status == PaymentSettlementNotificationStatuses.Delivered));
    }
}
