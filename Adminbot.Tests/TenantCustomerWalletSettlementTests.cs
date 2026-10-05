using System.Globalization;
using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>All five settlement services retain tenant origin and deliver one receipt-backed combined charge notice after revocation.</summary>
    /// <returns>A task completing after duplicate settlements and delivery-only outbox claims against real SQLite.</returns>
    /// <remarks>The customer already has an owned referral relationship, so an accidental owned attribution would create a reward. Delivery must preserve each historical post-credit balance, not the final live balance.</remarks>
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
        await wallet.AddEmptyUser(456);
        await wallet.AddEmptyUser(999);
        var referrals = provider.GetRequiredService<ReferralService>();
        await referrals.RegisterRelationshipAsync(123, ReferralCodeCodec.Encode(999), "main", BotInstanceTypes.Owned);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var hp = HooshPayPaymentInfo.CreateWalletCharge(123, 100000, "https://merchant.example/hp", "https://merchant.example/return", 321);
        hp.BotId = "tenant-a"; hp.WalletOriginBotType = BotInstanceTypes.Tenant; hp.WalletOriginTelegramBotId = 789; hp.TenantOwnerTelegramUserId = 456; hp.PaymentStatus = "paid";
        var tm = TetraminatorPaymentInfo.CreateWalletCharge(123, 100000, "https://merchant.example/tm", 321);
        tm.BotId = "tenant-a"; tm.WalletOriginBotType = BotInstanceTypes.Tenant; tm.WalletOriginTelegramBotId = 789; tm.TenantOwnerTelegramUserId = 456; tm.PaymentStatus = "paid"; tm.PayId = "test-payment"; tm.PaidAtUtc = DateTime.UtcNow;
        var up = UniquePayPaymentInfo.CreateWalletCharge(123, 321, 100000, 0);
        up.BotId = "tenant-a"; up.WalletOriginBotType = BotInstanceTypes.Tenant; up.WalletOriginTelegramBotId = 789; up.TenantOwnerTelegramUserId = 456; up.PaymentStatus = "paid"; up.PaidAtUtc = DateTime.UtcNow;
        var ap = AtlasPayPaymentInfo.CreateWalletCharge(123, 321, 100000);
        ap.BotId = "tenant-a"; ap.WalletOriginBotType = BotInstanceTypes.Tenant; ap.WalletOriginTelegramBotId = 789; ap.TenantOwnerTelegramUserId = 456; ap.ProviderStatus = "confirmed"; ap.PaidAtUtc = DateTime.UtcNow;
        var np = SwapinoPaymentInfo.CreateCryptoCharge(123, 100000, "https://merchant.example/np", chatId: 321);
        np.BotId = "tenant-a"; np.WalletOriginBotType = BotInstanceTypes.Tenant; np.WalletOriginTelegramBotId = 789; np.TenantOwnerTelegramUserId = 456; np.PaymentStatus = "finished";
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
        Assert.Equal(500000, await wallet.GetAccountBalance(456));
        Assert.Equal(0, await wallet.GetAccountBalance(999));
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Single(await db.ReferralRelationships.ToListAsync());
            Assert.Empty(await db.ReferralPaymentEvents.ToListAsync()); Assert.Empty(await db.ReferralRewards.ToListAsync());
            Assert.Equal(10, await db.WalletLedgerEntries.CountAsync(x => x.BotType == "tenant" && x.BotId == "tenant-a"));
            Assert.Equal(5, await db.WalletLedgerEntries.CountAsync(x => x.Reason == WalletLedgerReasons.WalletCharge));
            Assert.Equal(5, await db.WalletLedgerEntries.CountAsync(x => x.Reason == WalletLedgerReasons.TenantWalletTopUpMirror));
            var notifications = await db.PaymentSettlementNotifications.ToListAsync();
            Assert.Equal(10, notifications.Count);
            var customerNotifications = notifications.Where(x => !x.IsTenantOwnerReport).ToList();
            var ownerReports = notifications.Where(x => x.IsTenantOwnerReport).ToList();
            Assert.Equal(5, customerNotifications.Count);
            Assert.Equal(5, ownerReports.Count);
            Assert.Equal(5, customerNotifications.Select(x => x.NotificationKey).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(5, ownerReports.Select(x => x.NotificationKey).Distinct(StringComparer.Ordinal).Count());
            Assert.All(customerNotifications, n =>
            {
                Assert.Equal(321, n.ChatId);
                Assert.Equal(BotInstanceTypes.Tenant, n.WalletOriginBotType);
                Assert.Equal(789, n.WalletOriginTelegramBotId);
                AssertCombinedWalletChargeNotice(n.MessageText);
            });
            foreach (var notification in customerNotifications)
            {
                var receipt = await wallet.GetWalletOperationAsync($"payment:{notification.Provider}:{notification.ProviderPaymentId}:credit");
                Assert.Contains(receipt.AfterBalance.ToString("N0", CultureInfo.CurrentCulture), notification.MessageText);
            }
        }
        // Exercise production claim/delivery once without starting a timed background polling loop.
        using var worker = new PaymentSettlementNotificationWorker(databases.Users, services.GetRequiredService<BotClientProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentSettlementNotificationWorker>.Instance);
        var claim = typeof(PaymentSettlementNotificationWorker).GetMethod("ClaimDueBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliver = typeof(PaymentSettlementNotificationWorker).GetMethod("DeliverClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batch = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(worker, new object[] { CancellationToken.None })!;
        foreach (var notification in batch) await (Task)deliver.Invoke(worker, new object[] { notification, CancellationToken.None })!;
        Assert.Equal(5, clients["tenant-a"].Texts.Count);
        Assert.All(clients["tenant-a"].Texts, AssertCombinedWalletChargeNotice);
        Assert.False(clients.TryGetValue("main", out var owned) && owned.Texts.Count > 0);
        Assert.Equal(1000000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(5, await verify.PaymentSettlementNotifications.CountAsync(x => x.Status == PaymentSettlementNotificationStatuses.Delivered));
    }

    /// <summary>Owned and approved tenant charges deliver one combined notice using the receipt even after later wallet activity.</summary>
    /// <param name="tenant">True selects a tenant admitted by the production wallet policy; false selects the configured owned bot.</param>
    /// <param name="recover">True models a crash after credentials.db credit but before settlement metadata and notification commit.</param>
    /// <returns>A task completing after production settlement or recovery, two delivery batches, and financial/state assertions.</returns>
    /// <remarks>Uses isolated real SQLite databases, the production DI graph and Telegram SDK requests captured by the existing StorefrontClient. No timed workers or external provider calls are started.</remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WalletCharge_Combined_notice_preserves_receipt_balance_and_state_on_replay(bool tenant, bool recover)
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out var registry, out var clients);
        var (wallet, _, admittedOrder) = await SeedCustomerWalletOrderAsync(databases);
        await wallet.AddEmptyUser(456);
        var botId = tenant ? "tenant-a" : "main";
        var payment = AtlasPayPaymentInfo.CreateWalletCharge(123, 321, 100_000);
        payment.BotId = botId;
        payment.WalletOriginBotType = tenant ? BotInstanceTypes.Tenant : BotInstanceTypes.Owned;
        payment.WalletOriginTelegramBotId = tenant ? 789 : null;
        payment.TenantOwnerTelegramUserId = tenant ? 456 : null;
        payment.ProviderStatus = "confirmed";
        payment.PaidAtUtc = DateTime.UtcNow;
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.Token = "789:" + new string('a', 35);
            store.Username = "wallet_test";
            store.Enabled = true;
            registry.Upsert(store);
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
        }
        if (tenant)
            Assert.True(TenantCustomerWalletPolicy.IsApproved(await provider.GetRequiredService<TenantCustomerWalletPolicy>().RequireAsync(botId)));

        await using var scope = provider.CreateAsyncScope();
        var settlement = scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>();
        var operationKey = $"payment:atlaspay:{payment.Id}:credit";
        if (recover)
        {
            await wallet.MutateWalletAsync(123, 100_000, operationKey, botId: botId);
            await using (var credentials = databases.Credentials.CreateDbContext())
            {
                var operation = await credentials.WalletOperations.SingleAsync(x => x.OperationKey == operationKey);
                operation.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
                await credentials.SaveChangesAsync();
            }
            await wallet.MutateWalletAsync(123, -7_000, "fixture:later-wallet-activity", botId: botId);
            using var recovery = new WalletOperationReconciliationService(databases.Credentials, databases.Users,
                provider.GetRequiredService<WalletLedgerService>(), provider.GetRequiredService<TenantWalletOwnerTopUpMirrorService>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WalletOperationReconciliationService>.Instance);
            await recovery.ReconcileAsync();
            await recovery.ReconcileAsync();
        }
        else
        {
            var first = await settlement.ApplyOfficialPaymentAsync(payment, "fixture");
            Assert.Equal(NowPaymentsSettlementStatus.Applied, first.Status);
            await wallet.MutateWalletAsync(123, -7_000, "fixture:later-wallet-activity", botId: botId);
        }
        await settlement.ApplyOfficialPaymentAsync(payment, "fixture-replay");
        var receipt = await wallet.GetWalletOperationAsync(operationKey);
        Assert.Equal(600_000, receipt.AfterBalance);
        Assert.Equal(593_000, await wallet.GetAccountBalance(123));
        Assert.Equal(tenant ? 100_000 : 0, await wallet.GetAccountBalance(456));

        await DeliverWalletChargeNoticesAsync(databases, scope.ServiceProvider);
        await settlement.ApplyOfficialPaymentAsync(payment, "fixture-delivered-replay");
        await DeliverWalletChargeNoticesAsync(databases, scope.ServiceProvider);
        var text = Assert.Single(clients[botId].Texts);
        AssertCombinedWalletChargeNotice(text);
        Assert.Contains(receipt.AfterBalance.ToString("N0", CultureInfo.CurrentCulture), text);
        Assert.DoesNotContain(593_000L.ToString("N0", CultureInfo.CurrentCulture), text);
        await using (var credentials = databases.Credentials.CreateDbContext())
            Assert.Single(await credentials.WalletOperations.Where(x => x.OperationKey == operationKey).ToListAsync());
        await using var verify = databases.Users.CreateDbContext();
        var notice = Assert.Single(await verify.PaymentSettlementNotifications
            .Where(x => !x.NotificationKey.StartsWith(PaymentSettlementNotification.TenantOwnerReportPrefix)).ToListAsync());
        Assert.Equal(PaymentSettlementNotificationStatuses.Delivered, notice.Status);
        Assert.Equal(1, await verify.WalletLedgerEntries.CountAsync(x => x.Reason == WalletLedgerReasons.WalletCharge));
        Assert.Equal(tenant ? 1 : 0, await verify.WalletLedgerEntries.CountAsync(x => x.Reason == WalletLedgerReasons.TenantWalletTopUpMirror));
        var order = await verify.TenantBotOrders.SingleAsync(x => x.Id == admittedOrder.Id);
        Assert.Equal(admittedOrder.PaymentStatus, order.PaymentStatus);
        Assert.Equal(admittedOrder.CustomerWalletState, order.CustomerWalletState);
        Assert.False(order.IsFulfilled);
        Assert.False(order.IsOwnerCredited);
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
        Assert.Empty(await verify.XuiV3RenewalOperations.ToListAsync());
        Assert.Equal(593_000, await wallet.GetAccountBalance(123));
    }

    /// <summary>Checks required charge guidance in the delivered body without pinning provider confirmation wording.</summary>
    /// <param name="text">Customer-visible plain-text body captured from a production Telegram send request.</param>
    /// <remarks>These are product requirements: credit is usable for purchase/renewal, funding is not activation, and account status stays unchanged.</remarks>
    private static void AssertCombinedWalletChargeNotice(string text)
    {
        Assert.Contains("خرید یا تمدید", text);
        Assert.Contains("وضعیت اکانت شما مانند قبل باقی می‌ماند", text);
        Assert.Contains("شارژ حساب به‌تنهایی به معنی فعال شدن اکانت نیست", text);
        Assert.Equal(1, text.Split("موجودی حساب شما پس از شارژ", StringSplitOptions.None).Length - 1);
    }

    /// <summary>Runs one production outbox claim/delivery batch without starting a timed background loop.</summary>
    /// <param name="databases">Disposable real SQLite fixture containing durable notification claims.</param>
    /// <param name="services">Production scoped service provider with the existing captured Telegram clients.</param>
    /// <returns>A task completing after all customer claims due in this batch have been sent and marked delivered.</returns>
    /// <remarks>Owner reports retain their separate notification path; the settlement worker only delivers its supported customer claims.</remarks>
    private static async Task DeliverWalletChargeNoticesAsync(Databases databases, IServiceProvider services)
    {
        using var worker = new PaymentSettlementNotificationWorker(databases.Users, services.GetRequiredService<BotClientProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentSettlementNotificationWorker>.Instance);
        var claim = typeof(PaymentSettlementNotificationWorker).GetMethod("ClaimDueBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliver = typeof(PaymentSettlementNotificationWorker).GetMethod("DeliverClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batch = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(worker, new object[] { CancellationToken.None })!;
        foreach (var notification in batch)
            await (Task)deliver.Invoke(worker, new object[] { notification, CancellationToken.None })!;
    }
}
