using System.Net;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Every active gateway freezes the same base-only offer for both eligible owned audiences before HTTP.</summary>
    /// <param name="gateway">Configured central payment gateway; only fake HTTP is issued.</param>
    /// <param name="provider">Canonical durable provider key.</param>
    /// <returns>Verifies a single official principal and gift, even after campaign pause, restart, and concurrent recovery.</returns>
    [Theory]
    [InlineData(PaymentGateway.HooshPay, "hooshpay")]
    [InlineData(PaymentGateway.Tetraminator, "tetraminator")]
    [InlineData(PaymentGateway.UniquePay, "uniquepay")]
    [InlineData(PaymentGateway.AtlasPay, "atlaspay")]
    [InlineData(PaymentGateway.NowPayments, "nowpayments")]
    public async Task WalletCampaign_Official_owned_charge_is_frozen_and_applied_for_both_audiences(PaymentGateway gateway, string provider)
    {
        using var databases = new Databases();
        var wallet = new CredentialsStore(databases.Credentials);
        var ledger = new WalletLedgerService(databases.Users, wallet);
        var bots = new BotContextAccessor();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["hooshPayApiKey"] = "fixture", ["hooshPayBaseUrl"] = "https://hoosh.example/",
            ["tetraminatorApiKey"] = "fixture", ["tetraminatorApiBaseUrl"] = "https://tetra.example/",
            ["uniquePayBusinessToken"] = "fixture", ["uniquePayApiBaseUrl"] = "https://unique.example/",
            ["atlasPayApiKey"] = "fixture", ["atlasPayBaseUrl"] = "https://atlas.example/api/v1",
            ["nowPaymentApiKey"] = "fixture", ["nowpaymentIpnUrl"] = "https://merchant.example/np",
            ["nowpaymentPriceCurrency"] = "usdtbsc",
            ["bots:0:id"] = "main", ["bots:0:token"] = "111111:fixture-main", ["bots:0:isDefault"] = "true",
            ["bots:1:id"] = "brand-b", ["bots:1:token"] = "222222:fixture-brand",
            ["referral:enabled"] = "false", ["referral:minimumEligiblePaymentAmountToman"] = "0",
            ["referral:firstPayment:referrerRewardPercent"] = "0", ["referral:firstPayment:referredRewardPercent"] = "0",
            ["referral:firstPayment:referredMinimumRewardToman"] = "0", ["referral:firstPayment:referredMaximumRewardToman"] = "0",
            ["referral:subsequentPayments:referrerRewardPercent"] = "0"
        }).Build();
        var registry = new BotRegistry(config);
        var clients = new BotClientProvider(registry, _ => new StorefrontClient());
        var referrals = new ReferralService(config, databases.Users, wallet, ledger, null!, NullLogger<ReferralService>.Instance);
        const long principal = 100000;
        const string callback = "https://merchant.example/return";
        foreach (var isColleague in new[] { false, true })
        {
            const string partnerBot = "brand-b";
            var botId = isColleague ? partnerBot : registry.DefaultBot.Id;
            var userId = isColleague ? 456L : 123L;
            await wallet.AddEmptyUser(userId);
            if (isColleague) await wallet.PromotOrDemote(userId, true);
            using var botScope = bots.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = botId, Type = BotInstanceTypes.Owned } });
            await using var db = databases.Users.CreateDbContext();
            var campaign = await db.WalletChargeCampaigns.FindAsync(1);
            if (campaign == null) db.WalletChargeCampaigns.Add(campaign = new WalletChargeCampaign());
            campaign.Enabled = true;
            campaign.Percent = 20;
            campaign.Name = "بلک‌فرایدی";
            await db.SaveChangesAsync();
            var posts = 0;
            var handler = new AtlasHttpHandler(async (_, request, _, _) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Interlocked.Increment(ref posts);
                await using var read = databases.Users.CreateDbContext();
                var saved = await read.WalletChargeBonuses.SingleAsync(x => x.Provider == provider && x.TelegramUserId == userId);
                Assert.Equal(principal, saved.PrincipalToman);
                Assert.Equal(20000, saved.BonusToman);
                Assert.Null(saved.NextAttemptAtUtc);
                var hash = gateway == PaymentGateway.UniquePay
                    ? (await read.UniquePayPaymentInfos.SingleAsync(x => x.TelegramUserId == userId)).HashId : "fixture";
                return JsonResponse(HttpStatusCode.OK, new JObject
                {
                    ["success"] = true, ["status"] = true, ["code"] = 200, ["hashId"] = hash, ["refId"] = "fixture-ref",
                    ["data"] = new JObject { ["uid"] = "fixture-id", ["amount"] = principal, ["payment_url"] = "https://pay.example/" },
                    ["pay_id"] = "fixture-id", ["payment_link"] = "https://pay.example/", ["paymentLink"] = "https://pay.example/",
                    ["id"] = 1, ["invoice_url"] = "https://pay.example/", ["price_amount"] = 1, ["price_currency"] = "usdtbsc",
                    ["orderId"] = userId, ["trackingCode"] = "fixture-track", ["totalAmountToman"] = 112000,
                    ["paymentDeadlineAt"] = "2027-01-01T00:00:00Z", ["customerStartLink"] = "https://t.me/fixture_bot?start=test"
                }.ToString());
            });
            using var http = new HttpClient(handler);
            var availability = new GatewayAvailabilityProbe();
            await availability.SetEnabledAsync(gateway, true, availability.Snapshot.Revision);
            var now = new NowPayments(config, http, new TenantWalletQuote());
            var charges = new WalletChargeApplicationService(new UserWorkflowStore(databases.Users), config.Get<AppConfig>()!,
                availability, null!, null!, new HooshPay(config, http), new Tetraminator(config, http), new UniquePay(config, http),
                new AtlasPay(config, http), now, wallet, registry);
            Func<Task<NowPaymentsSettlementResult>> settle;
            switch (gateway)
            {
                case PaymentGateway.HooshPay:
                    var hp = HooshPayPaymentInfo.CreateWalletCharge(userId, principal, callback, callback, userId);
                    db.Add(hp); await db.SaveChangesAsync();
                    await charges.CreateHooshPayAsync(hp, default);
                    hp.PaymentStatus = HooshPayStatuses.Paid;
                    settle = () => new HooshPaySettlementService(databases.Users, wallet, clients, registry, bots, ledger, referrals,
                        NullLogger<HooshPaySettlementService>.Instance).ApplyFinishedPaymentAsync(hp, "fixture", userId);
                    break;
                case PaymentGateway.Tetraminator:
                    var tm = TetraminatorPaymentInfo.CreateWalletCharge(userId, principal, callback, userId);
                    db.Add(tm); await db.SaveChangesAsync();
                    await charges.CreateTetraminatorAsync(tm, default);
                    tm.PaymentStatus = TetraminatorStatuses.Paid; tm.PaidAtUtc = DateTime.UtcNow; tm.PayId = "fixture-" + userId;
                    settle = () => new TetraminatorSettlementService(databases.Users, wallet, ledger, referrals, clients, registry, bots,
                        NullLogger<TetraminatorSettlementService>.Instance).ApplyOfficialPaymentAsync(tm, "fixture", userId);
                    break;
                case PaymentGateway.UniquePay:
                    var up = UniquePayPaymentInfo.CreateWalletCharge(userId, userId, principal, 12);
                    db.Add(up); await db.SaveChangesAsync();
                    await charges.CreateUniquePayAsync(up, callback, callback, default);
                    up.PaymentStatus = UniquePayStatuses.Paid; up.PaidAtUtc = DateTime.UtcNow;
                    settle = () => new UniquePaySettlementService(config, databases.Users, wallet, ledger, referrals,
                        NullLogger<UniquePaySettlementService>.Instance).ApplyOfficialPaymentAsync(up, "fixture", userId);
                    break;
                case PaymentGateway.AtlasPay:
                    var ap = AtlasPayPaymentInfo.CreateWalletCharge(userId, userId, principal);
                    ap.BotId = botId;
                    ap.BeginCreationAttempt(DateTime.UtcNow);
                    db.Add(ap); await db.SaveChangesAsync();
                    await charges.CreateAtlasPayAsync(ap, default);
                    ap.ProviderStatus = "settled"; ap.PaidAtUtc = DateTime.UtcNow; ap.TotalAmountToman = 112000;
                    settle = () => new AtlasPaySettlementService(databases.Users, wallet, ledger, referrals, config,
                        NullLogger<AtlasPaySettlementService>.Instance).ApplyOfficialPaymentAsync(ap, "fixture");
                    break;
                default:
                    var np = SwapinoPaymentInfo.CreateCryptoCharge(userId, principal, callback, userId);
                    db.Add(np); await db.SaveChangesAsync();
                    await charges.CreateNowPaymentsAsync(np, "usdtbsc", callback, callback, default);
                    np.PaymentStatus = NowPaymentsStatuses.Finished;
                    settle = () => new NowPaymentsSettlementService(databases.Users, wallet, clients, registry, bots, ledger, referrals, now,
                        NullLogger<NowPaymentsSettlementService>.Instance).ApplyFinishedPaymentAsync(np, "fixture", userId);
                    break;
            }

            // A real provider's validated paid state is now persisted. Different live settings must not replace the promise.
            campaign.Enabled = false;
            campaign.Percent = 15;
            campaign.Name = "کمپین بعدی";
            await db.SaveChangesAsync();
            await Task.WhenAll(settle(), settle());
            Assert.Equal(1, posts);
            Assert.Equal(principal, await wallet.GetAccountBalance(userId));
            var recovered = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger,
                NullLogger<WalletOperationReconciliationService>.Instance);
            var restarted = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger,
                NullLogger<WalletOperationReconciliationService>.Instance);
            await Task.WhenAll(recovered.ReconcileAsync(), restarted.ReconcileAsync());
            await settle();
            await restarted.ReconcileAsync();
            Assert.Equal(120000, await wallet.GetAccountBalance(userId));
            var entries = await db.WalletLedgerEntries.AsNoTracking().Where(x => x.TelegramUserId == userId).ToListAsync();
            Assert.Equal(principal, Assert.Single(entries, x => x.Reason == WalletLedgerReasons.WalletCharge).AmountToman);
            Assert.Equal(20000, Assert.Single(entries, x => x.Reason == WalletLedgerReasons.WalletChargeBonus).AmountToman);
            var notice = await db.PaymentSettlementNotifications.SingleAsync(x => x.TelegramUserId == userId);
            Assert.Equal(120000, notice.AmountToman);
            Assert.Contains("بلک‌فرایدی", notice.MessageText);
            Assert.Contains("120,000", notice.MessageText);
        }
    }

    /// <summary>Only the correct owned audience and charge purpose may capture a campaign.</summary>
    [Fact]
    public void WalletCampaign_Eligibility_rate_rounding_and_safe_preview()
    {
        const string charge = TenantBotPaymentPurposes.WalletCharge;
        Assert.True(WalletChargeCampaignService.IsEligible(charge, "owned", "main", "main", false));
        Assert.True(WalletChargeCampaignService.IsEligible(charge, "owned", "brand", "main", true));
        Assert.False(WalletChargeCampaignService.IsEligible(charge, "owned", "brand", "main", false));
        Assert.False(WalletChargeCampaignService.IsEligible(charge, "tenant", "main", "main", true));
        foreach (var purpose in new[] { "purchase", "renew", "admin", "refund", "referral" })
            Assert.False(WalletChargeCampaignService.IsEligible(purpose, "owned", "main", "main", true));
        var settings = new WalletChargeCampaign { Name = "<هدیه>", Percent = 15 };
        Assert.Equal(0, WalletChargeCampaignService.Quote(settings, 100000).BonusToman);
        settings.Enabled = true;
        var captured = WalletChargeCampaignService.Quote(settings, 100001);
        Assert.Equal(15000, captured.BonusToman);
        Assert.Contains("&lt;", WalletChargeCampaignService.Describe(captured, html: true));
    }

    /// <summary>Exercises principal-only provisional approval, later provider proof and restart after the gift ledger commit window.</summary>
    /// <returns>Verifies one bonus and one final receipt while the prior principal-only notice remains immutable.</returns>
    [Fact]
    public async Task WalletCampaign_Provisional_requires_official_proof_and_recovers_committed_gift()
    {
        using var databases = new Databases();
        const long actor = 123;
        const string botId = "main";
        const string provider = "hooshpay";
        var wallet = new CredentialsStore(databases.Credentials);
        await wallet.AddEmptyUser(actor);
        var botContext = new BotContextAccessor();
        using var scope = botContext.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = botId, Type = BotInstanceTypes.Owned } });
        var ledger = new WalletLedgerService(databases.Users, wallet);
        var payment = HooshPayPaymentInfo.CreateWalletCharge(actor, 100000, "https://merchant.example/ipn", "https://merchant.example/return", actor);
        var bonus = WalletChargeCampaignService.Quote(new WalletChargeCampaign { Enabled = true, Percent = 20 }, 100000);
        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.Add(payment); await seed.SaveChangesAsync();
            bonus.Provider = provider; bonus.PaymentId = payment.Id; bonus.TelegramUserId = actor; bonus.ChatId = actor; bonus.BotId = botId;
            seed.Add(bonus); await seed.SaveChangesAsync();
        }
        var registry = new BotRegistry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["bots:0:id"] = botId, ["bots:0:token"] = "111111:fixture-main"
        }).Build());
        var settlement = new HooshPaySettlementService(databases.Users, wallet, new BotClientProvider(registry, _ => new StorefrontClient()), registry,
            botContext, ledger, null!, NullLogger<HooshPaySettlementService>.Instance);
        await settlement.ApplyProvisionalWalletPaymentAsync(payment, 999, actor);
        var worker = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger,
            NullLogger<WalletOperationReconciliationService>.Instance);
        await worker.ReconcileAsync();
        Assert.Equal(100000, await wallet.GetAccountBalance(actor));
        await using (var db = databases.Users.CreateDbContext())
        {
            payment = await db.HooshPayPaymentInfos.SingleAsync();
            Assert.Null((await db.WalletChargeBonuses.SingleAsync()).NextAttemptAtUtc);
            Assert.Single(await db.PaymentSettlementNotifications.ToListAsync());
            payment.PaymentStatus = HooshPayStatuses.Paid;
            await db.SaveChangesAsync();
        }
        Assert.True(await settlement.RecordProviderConfirmationAfterProvisionalAsync(payment, "fixture"));
        // Simulate a process exit after credentials.db commits the exact bonus, before users.db ledger/outbox completion.
        var key = $"wallet-charge-bonus:{provider}:{payment.Id}";
        await wallet.MutateWalletAsync(actor, 20000, key, botId: botId);
        await worker.ReconcileAsync();
        await worker.ReconcileAsync();
        Assert.Equal(120000, await wallet.GetAccountBalance(actor));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Single(await verify.WalletLedgerEntries.Where(x => x.IdempotencyKey == key).ToListAsync());
        Assert.Equal(2, await verify.PaymentSettlementNotifications.CountAsync());
        Assert.Equal(120000, (await verify.PaymentSettlementNotifications.SingleAsync(x => x.NotificationKey.EndsWith(":campaign-confirmed"))).AmountToman);
    }

    /// <summary>Checks a real upgrade leaves historic payments alone and a new principal-only crash recovers safely.</summary>
    /// <returns>Verifies a pre-migration charge, pending/expired invoice, partial credit and new official credit are distinguished.</returns>
    [Fact]
    public async Task WalletCampaign_Migration_and_principal_recovery_only_award_new_official_promise()
    {
        using var databases = new Databases(initialize: false);
        const long actor = 123;
        const string botId = BotContextAccessor.DefaultBotId;
        const string endpoint = "https://merchant.example/return";
        await using var db = databases.Users.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261126145038_AddVerifiedPanelUuidToXuiCreation");
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var wallet = new CredentialsStore(databases.Credentials);
        await wallet.AddEmptyUser(actor);
        var historical = HooshPayPaymentInfo.CreateWalletCharge(actor, 100000, endpoint, endpoint, actor);
        db.Add(historical); await db.SaveChangesAsync();
        await wallet.MutateWalletAsync(actor, 100000, $"payment:hooshpay:{historical.Id}:credit", botId: botId);

        await db.Database.MigrateAsync();
        Assert.Empty(await db.WalletChargeBonuses.ToListAsync());
        Assert.Empty(await db.WalletChargeCampaigns.ToListAsync());
        Assert.Equal(100000, await wallet.GetAccountBalance(actor));
        foreach (var status in new[] { "waiting", "expired", "partially_paid", "finished" })
        {
            var payment = SwapinoPaymentInfo.CreateCryptoCharge(actor, 100000, endpoint, actor);
            payment.PaymentStatus = status;
            db.Add(payment); await db.SaveChangesAsync();
            var bonus = WalletChargeCampaignService.Quote(new WalletChargeCampaign { Enabled = true }, 100000);
            bonus.Provider = "nowpayments"; bonus.PaymentId = payment.Id; bonus.BotId = botId; bonus.TelegramUserId = actor; bonus.ChatId = actor;
            db.Add(bonus);
            await db.SaveChangesAsync();
            if (status == "partially_paid")
                await wallet.MutateWalletAsync(actor, 70000, $"payment:nowpayments:{payment.Id}:credit", botId: botId, approvalKind: "partial");
            if (status == "finished")
                await wallet.MutateWalletAsync(actor, 100000, $"payment:nowpayments:{payment.Id}:credit", botId: botId);
        }
        await using (var credentials = databases.Credentials.CreateDbContext())
            await credentials.WalletOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var ledger = new WalletLedgerService(databases.Users, wallet);
        var worker = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger,
            NullLogger<WalletOperationReconciliationService>.Instance);
        await worker.ReconcileAsync();
        await worker.ReconcileAsync();
        Assert.Equal(290000, await wallet.GetAccountBalance(actor));
        Assert.Equal(1, await db.WalletChargeBonuses.CountAsync(x => x.CompletedAtUtc != null));
        Assert.Equal(1, await db.PaymentSettlementNotifications.CountAsync(x => x.AmountToman == 120000));
    }
}
