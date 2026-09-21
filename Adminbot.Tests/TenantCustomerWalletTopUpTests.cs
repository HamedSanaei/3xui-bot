using System.Net;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>All central gateways persist tenant origin before I/O and recover exactly one customer credit after revocation.</summary>
    /// <param name="gateway">One of the five supported central automatic providers.</param>
    /// <param name="providerKey">Stable key used by existing provider wallet receipts.</param>
    /// <returns>A task completing after fake HTTP creation and real two-database recovery checks.</returns>
    [Theory]
    [InlineData(PaymentGateway.HooshPay, "hooshpay")]
    [InlineData(PaymentGateway.Tetraminator, "tetraminator")]
    [InlineData(PaymentGateway.UniquePay, "uniquepay")]
    [InlineData(PaymentGateway.AtlasPay, "atlaspay")]
    [InlineData(PaymentGateway.NowPayments, "nowpayments")]
    public async Task TenantCustomerWallet_Central_charge_origin_and_crash_credit_survive_revocation(PaymentGateway gateway, string providerKey)
    {
        using var databases = new Databases();
        var (wallet, _, _) = await SeedCustomerWalletOrderAsync(databases);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["hooshPayApiKey"] = "test-only", ["hooshPayBaseUrl"] = "https://hoosh.example/", ["hooshPayIpnUrl"] = "https://merchant.example/hp",
            ["tetraminatorApiKey"] = "test-only", ["tetraminatorApiBaseUrl"] = "https://tetra.example/", ["tetraminatorCallbackUrl"] = "https://merchant.example/tm",
            ["uniquePayBusinessToken"] = "test-only", ["uniquePayApiBaseUrl"] = "https://unique.example/", ["uniquePayReturnUrl"] = "https://merchant.example/up-return",
            ["uniquePayCallbackUrl"] = "https://merchant.example/up", ["atlasPayApiKey"] = "test-only", ["atlasPayBaseUrl"] = "https://atlas.example/api/v1",
            ["nowPaymentApiKey"] = "test-only", ["nowpaymentIpnUrl"] = "https://merchant.example/np", ["nowpaymentPriceCurrency"] = "usdtbsc",
            ["tetraminatorMinimumAmountToman"] = "10000"
        }).Build();
        var posts = 0;
        var handler = new AtlasHttpHandler(async (_, request, body, _) =>
        {
            Interlocked.Increment(ref posts);
            await using var db = databases.Users.CreateDbContext();
            var count = gateway switch
            {
                PaymentGateway.HooshPay => await db.HooshPayPaymentInfos.CountAsync(x => x.BotId == "tenant-a" && x.WalletOriginBotType == "tenant" && x.WalletOriginTelegramBotId == 789),
                PaymentGateway.Tetraminator => await db.TetraminatorPaymentInfos.CountAsync(x => x.BotId == "tenant-a" && x.WalletOriginBotType == "tenant" && x.WalletOriginTelegramBotId == 789),
                PaymentGateway.UniquePay => await db.UniquePayPaymentInfos.CountAsync(x => x.BotId == "tenant-a" && x.WalletOriginBotType == "tenant" && x.WalletOriginTelegramBotId == 789),
                PaymentGateway.AtlasPay => await db.AtlasPayPaymentInfos.CountAsync(x => x.BotId == "tenant-a" && x.WalletOriginBotType == "tenant" && x.WalletOriginTelegramBotId == 789),
                _ => await db.SwapinoPaymentInfos.CountAsync(x => x.BotId == "tenant-a" && x.WalletOriginBotType == "tenant" && x.WalletOriginTelegramBotId == 789)
            };
            Assert.Equal(1, count);
            var hash = gateway == PaymentGateway.UniquePay ? (await db.UniquePayPaymentInfos.SingleAsync()).HashId : "test";
            return JsonResponse(HttpStatusCode.OK, new JObject {
                ["success"] = true, ["status"] = true, ["code"] = 200, ["hashId"] = hash, ["refId"] = "fixture-ref",
                ["data"] = new JObject { ["uid"] = "fixture-id", ["amount"] = 100000, ["payment_url"] = "https://pay.example/" },
                ["pay_id"] = "fixture-id", ["payment_link"] = "https://pay.example/", ["paymentLink"] = "https://pay.example/",
                ["id"] = 1, ["invoice_url"] = "https://pay.example/", ["price_amount"] = 1, ["price_currency"] = "usdtbsc",
                ["orderId"] = 1, ["trackingCode"] = "fixture-track", ["totalAmountToman"] = 100000,
                ["cardNumberMasked"] = "****", ["paymentDeadlineAt"] = "2027-01-01T00:00:00Z", ["customerStartLink"] = "https://t.me/fixture_bot?start=test"
            }.ToString());
        });
        HttpClient Client() => new(handler, disposeHandler: false);
        var availability = new GatewayAvailabilityProbe();
        await availability.SetEnabledAsync(PaymentGateway.Tetraminator, true, availability.Snapshot.Revision);
        await availability.SetEnabledAsync(PaymentGateway.NowPayments, true, availability.Snapshot.Revision);
        var policy = new TenantCustomerWalletPolicy(databases.Users, config.Get<AppConfig>()!, NullLogger<TenantCustomerWalletPolicy>.Instance);
        var workflow = new UserWorkflowStore(databases.Users);
        var charges = new WalletChargeApplicationService(workflow, config.Get<AppConfig>()!, availability, policy,
            new HooshPay(config, Client()), new Tetraminator(config, Client()), new UniquePay(config, Client()),
            new AtlasPay(config, Client()), new NowPayments(config, Client(), new TenantWalletQuote()));
        var invoice = await charges.CreateTenantAsync("tenant-a", 123, 123, 100000, gateway, default);
        Assert.NotEmpty(invoice.Url); Assert.Equal(1, posts);
        await using (var db = databases.Users.CreateDbContext())
        { var store = await db.BotInstances.SingleAsync(); TenantCustomerWalletPolicy.Revoke(store); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => charges.CreateTenantAsync("tenant-a", 123, 123, 100000, gateway, default));
        // The provider's committed credit receipt is authoritative even if users.db did not yet record settlement.
        var key = $"payment:{providerKey}:1:credit";
        await wallet.MutateWalletAsync(123, 100000, key, botId: "tenant-a");
        await using (var db = databases.Credentials.CreateDbContext())
            await db.WalletOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var recovery = new WalletOperationReconciliationService(databases.Credentials, databases.Users,
            new WalletLedgerService(databases.Users, wallet), NullLogger<WalletOperationReconciliationService>.Instance);
        await recovery.ReconcileAsync(); await recovery.ReconcileAsync();
        Assert.Equal(600000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Users.CreateDbContext();
        var entry = await verify.WalletLedgerEntries.SingleAsync(x => x.IdempotencyKey == key);
        Assert.Equal("tenant", entry.BotType); Assert.Equal("tenant-a", entry.BotId); Assert.Equal(providerKey, entry.Provider);
        var notification = await verify.PaymentSettlementNotifications.SingleAsync();
        Assert.Equal($"tenant-wallet:{providerKey}:1", notification.NotificationKey);
        Assert.Equal("tenant-a", notification.BotId); Assert.Equal(BotInstanceTypes.Tenant, notification.WalletOriginBotType);
        Assert.Equal(789, notification.WalletOriginTelegramBotId); Assert.Equal(123, notification.TelegramUserId);
        Assert.Equal(123, notification.ChatId); Assert.Equal(1, posts);
    }

    /// <summary>Supplies a deterministic canonical IRT exchange rate without contacting a live pricing service.</summary>
    private sealed class TenantWalletQuote : IDollarPriceQuoteProvider
    {
        /// <summary>Returns one fixed canonical quote for the fake central crypto gateway.</summary>
        /// <param name="cancellationToken">Unused because no external work is performed.</param>
        /// <returns>A non-secret test rate of 100,000 toman per USDT.</returns>
        public Task<DollarPriceQuote> NobitexUSDTIRTQuote(CancellationToken cancellationToken = default)
            => Task.FromResult(new DollarPriceQuote { Price = 100000, NormalizedUnit = "IRT", Source = "fixture" });
    }
}
