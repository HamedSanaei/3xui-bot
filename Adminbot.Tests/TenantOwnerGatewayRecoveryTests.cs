using System.Net;
using System.Text;
using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>Protects exact-store owner payment recovery without granting provisional financial authority.</summary>
/// <remarks>Real isolated SQLite databases and real provider clients exercise ownership, fresh proof and exactly-once fulfillment.</remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>A sibling-store selection or another owner cannot inquire or mutate a merchant's payment.</summary>
    /// <param name="gateway">One of the five supported third-party providers, selected independently of request text.</param>
    /// <returns>A task completing after unauthorized lookups and both databases are checked.</returns>
    /// <remarks>Regression: filtering only by owner would expose sibling-store orders; filtering only by OrderId would expose every merchant.</remarks>
    [Theory]
    [InlineData("hooshpay")]
    [InlineData("tetraminator")]
    [InlineData("nowpayments")]
    [InlineData("uniquepay")]
    [InlineData("atlaspay")]
    public async Task Owner_gateway_recovery_rejects_sibling_store_and_foreign_owner_before_inquiry(string gateway)
    {
        using var databases = new Databases();
        using var transport = new OwnerGatewayTransport(gateway, "paid");
        await using var provider = OwnerGatewayProvider(databases, transport);
        var order = await SeedOwnerGatewayOrderAsync(databases, provider, gateway);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance { Id = "tenant-owner-recovery-b", Type = BotInstanceTypes.Tenant,
                OwnerTelegramUserId = 711, TenantStoreNumber = 2 });
            await db.SaveChangesAsync();
        }
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var sibling = await service.ConfirmTenantGatewayOrderByOwnerAsync("tenant-owner-recovery-b", order.OrderId, 711, default);
        var foreign = await service.ConfirmTenantGatewayOrderByOwnerAsync(order.TenantBotId, order.OrderId, 999, default);
        var missing = await service.ConfirmTenantGatewayOrderByOwnerAsync(order.TenantBotId, "TENANTBOT-missing", 711, default);
        Assert.Equal(missing, sibling);
        Assert.Equal(missing, foreign);
        Assert.Equal(0, transport.RequestCount);
        await AssertOwnerGatewayUnspentAsync(databases, provider, order.Id);
    }

    /// <summary>Unpaid, conflicting and failed official inquiries cannot turn a stale local paid flag into an account or credit.</summary>
    /// <param name="gateway">Provider whose real HTTP parser receives the controlled response.</param>
    /// <param name="outcome">Pending status, mismatched invoice identity, or HTTP failure; none proves full payment.</param>
    /// <returns>A task after the actual recovery boundary and unchanged money/fulfillment state are checked.</returns>
    /// <remarks>The local invoice is deliberately cached as paid to catch trusting that flag instead of a fresh provider response.</remarks>
    [Theory]
    [InlineData("hooshpay", "pending")]
    [InlineData("hooshpay", "mismatch")]
    [InlineData("hooshpay", "error")]
    [InlineData("tetraminator", "pending")]
    [InlineData("tetraminator", "mismatch")]
    [InlineData("tetraminator", "error")]
    [InlineData("nowpayments", "pending")]
    [InlineData("nowpayments", "mismatch")]
    [InlineData("nowpayments", "error")]
    [InlineData("uniquepay", "pending")]
    [InlineData("uniquepay", "mismatch")]
    [InlineData("uniquepay", "error")]
    [InlineData("atlaspay", "pending")]
    [InlineData("atlaspay", "mismatch")]
    [InlineData("atlaspay", "error")]
    public async Task Owner_gateway_recovery_requires_fresh_provider_proof_before_financial_effects(string gateway, string outcome)
    {
        using var databases = new Databases();
        using var transport = new OwnerGatewayTransport(gateway, outcome);
        await using var provider = OwnerGatewayProvider(databases, transport);
        var order = await SeedOwnerGatewayOrderAsync(databases, provider, gateway, cachedPaid: true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantBotService>()
            .ConfirmTenantGatewayOrderByOwnerAsync(order.TenantBotId, order.OrderId, 711, default);
        Assert.True(transport.RequestCount > 0);
        await AssertOwnerGatewayUnspentAsync(databases, provider, order.Id);
    }

    /// <summary>An explicit owner recovery advances only a rejected provisioning generation and settles one paid purchase.</summary>
    /// <param name="gateway">Provider officially confirming the original immutable invoice.</param>
    /// <returns>A task completing after concurrent re-entry, saved fulfillment and immutable profit receipts are checked.</returns>
    /// <remarks>
    /// New sales and gateway admission are disabled after invoice issue. Those switches cannot forfeit the customer's paid order.
    /// A definitively rejected original attempt must use retry:1; replay of one durable Telegram update cannot allocate retry:2.
    /// AtlasPay and UniquePay also start in manual review from that rejected fulfillment; fresh official proof may
    /// reopen only that exact local claim, never an ambiguous or applied XUI operation.
    /// </remarks>
    [Theory]
    [InlineData("hooshpay")]
    [InlineData("tetraminator")]
    [InlineData("nowpayments")]
    [InlineData("uniquepay")]
    [InlineData("atlaspay")]
    public async Task Owner_gateway_recovery_resumes_rejected_paid_purchase_exactly_once_when_admission_closed(string gateway)
    {
        using var databases = new Databases();
        using var transport = new OwnerGatewayTransport(gateway, "paid");
        var posts = 0;
        JObject? identity = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var panel = builder.Build();
        panel.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref posts);
                identity = (JObject?)JObject.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync())["client"] ?? new JObject();
                identity["inboundIds"] = new JArray(200);
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}");
                return;
            }
            if (context.Request.Path.Value?.Contains("links", StringComparison.OrdinalIgnoreCase) == true)
            {
                await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}");
                return;
            }
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = identity ?? new JObject() }.ToString());
        });
        await panel.StartAsync();
        try
        {
            await using var provider = OwnerGatewayProvider(databases, transport, panel.Urls.Single());
            var order = await SeedOwnerGatewayOrderAsync(databases, provider, gateway, stopped: true);
            await using (var db = databases.Users.CreateDbContext())
            {
                db.XuiV3CreationOperations.Add(TenantAttempt(order.Id, 0, XuiV3CreationOutcome.DefinitiveRejected));
                if (gateway == "atlaspay")
                {
                    var payment = await db.AtlasPayPaymentInfos.SingleAsync();
                    payment.SettlementState = AtlasPaySettlementStates.ManualReview;
                    payment.ErrorCode = "tenant_fulfillment_ambiguous";
                    payment.SettlementAttemptId = "rejected-atlas-claim";
                    payment.SettlementStartedAtUtc = DateTime.UtcNow;
                }
                else if (gateway == "uniquepay")
                {
                    var payment = await db.UniquePayPaymentInfos.SingleAsync();
                    payment.SettlementState = UniquePaySettlementStates.ManualReview;
                    payment.ErrorCode = "tenant_fulfillment_ambiguous";
                    payment.SettlementAttemptId = "rejected-unique-claim";
                    payment.SettlementStartedAtUtc = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
            }
            using (TelegramUpdateExecutionScope.Push(901))
            {
                await using (var scope = provider.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<TenantBotService>()
                        .ConfirmTenantGatewayOrderByOwnerAsync(order.TenantBotId, order.OrderId, 711, default);
                await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
                {
                    await using var scope = provider.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<TenantBotService>()
                        .ConfirmTenantGatewayOrderByOwnerAsync(order.TenantBotId, order.OrderId, 711, default);
                }));
            }
            await using var users = databases.Users.CreateDbContext();
            var saved = await users.TenantBotOrders.AsNoTracking().SingleAsync(x => x.Id == order.Id);
            Assert.True(saved.IsFulfilled, $"provider={gateway}; status={saved.PaymentStatus}; error={saved.ErrorMessage}");
            Assert.Equal(1, Volatile.Read(ref posts));
            Assert.Single(await users.TenantBotLedgerEntries.Where(x => x.TenantBotOrderId == order.Id).ToListAsync());
            Assert.Equal(2, await users.XuiV3CreationOperations.CountAsync());
            var retry = await users.XuiV3CreationOperations.SingleAsync(x => x.OperationKey == $"tenant-create:{order.Id}:retry:1");
            Assert.Equal(XuiV3CreationOutcome.Applied, retry.Outcome);
            Assert.Equal($"tenant-retry:{order.Id}:tg:901", retry.AuthorizedByKey);
            Assert.Empty(await users.TenantManualPaymentReceipts.ToListAsync());
            var wallet = provider.GetRequiredService<CredentialsStore>();
            Assert.Equal(210_000, await wallet.GetAccountBalance(711));
            Assert.Equal(0, await wallet.GetAccountBalance(722));
            Assert.Equal(10_000, (await wallet.GetWalletOperationAsync($"tenant:{order.Id}:profit")).AmountToman);
            Assert.Null(await wallet.GetWalletOperationAsync($"tenant:{order.Id}:base-cost"));
            await using var credentials = databases.Credentials.CreateDbContext();
            Assert.Single(await credentials.WalletOperations.Where(x => x.OperationKey == $"tenant:{order.Id}:profit").ToListAsync());
        }
        finally { await panel.StopAsync(); }
    }

    /// <summary>Registers the production graph with fake-key provider transports and no external Telegram connections.</summary>
    /// <param name="databases">Required fixture owning both private SQLite databases.</param>
    /// <param name="transport">Required HTTP transport supplying official inquiry-shaped responses, never mocked settlement.</param>
    /// <param name="panelUrl">Optional real loopback XUI fixture URL; null leaves account mutation unavailable.</param>
    /// <returns>A production service provider owned and asynchronously disposed by the caller.</returns>
    /// <remarks>Closed new-admission flags are intentional. Existing issued-payment recovery must remain independent of them.</remarks>
    /// <example><code>await using var provider = OwnerGatewayProvider(databases, transport, panel.Urls.Single());</code></example>
    private static ServiceProvider OwnerGatewayProvider(Databases databases, OwnerGatewayTransport transport, string? panelUrl = null)
    {
        var configuration = new ConfigurationBuilder().AddConfiguration(AtlasTenantConfiguration(databases, panelUrl ?? "http://127.0.0.1:9"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HooshPayApiKey"] = "test-only", ["TetraminatorApiKey"] = "test-only",
                ["UniquePayBusinessToken"] = "test-only", ["NowpaymentApikey"] = "test-only",
                ["TetraminatorInquiryRetryCount"] = "0", ["UniquePayInquiryRetryCount"] = "0", ["AtlasPayInquiryRetryCount"] = "0",
                ["XuiV3TransientRetryCount"] = "0", ["XuiV3RequestTimeoutSeconds"] = "5",
                ["NormalSaleEnabled"] = "false", ["NormalRenewalEnabled"] = "false", ["NationalSaleEnabled"] = "false",
                ["NationalRenewalEnabled"] = "false", ["UnlimitedSaleEnabled"] = "false", ["UnlimitedRenewalEnabled"] = "false",
                ["HooshPayEnabled"] = "false", ["TetraminatorEnabled"] = "false", ["UniquePayEnabled"] = "false",
                ["NowPaymentsEnabled"] = "false", ["AtlasPayEnabled"] = "false"
            }).Build();
        var config = configuration.Get<AppConfig>()!;
        UserDbContext.ConfigureDatabasePath(config.UserDatabasePath);
        var services = new ServiceCollection();
        Program.RegisterApplicationServices(services, configuration, config, databases.DirectoryPath);
        services.AddSingleton(new HooshPay(configuration, new HttpClient(transport, disposeHandler: false)));
        services.AddSingleton(new Tetraminator(configuration, new HttpClient(transport, disposeHandler: false)));
        services.AddSingleton(new NowPayments(configuration, new HttpClient(transport, disposeHandler: false)));
        services.AddSingleton(new UniquePay(configuration, new HttpClient(transport, disposeHandler: false)));
        services.AddSingleton(new AtlasPay(configuration, new HttpClient(transport, disposeHandler: false)));
        var registry = new BotRegistry(configuration);
        services.AddSingleton(registry);
        services.AddSingleton(new BotClientProvider(registry, _ => new StorefrontClient()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Persists one reciprocal tenant order/provider pair with immutable sale/base/profit amounts.</summary>
    /// <param name="databases">Private users.db fixture used to persist the linked records.</param>
    /// <param name="provider">Production provider whose private credentials store receives the owner/customer profiles.</param>
    /// <param name="gateway">Known gateway discriminator for the order and matching payment table.</param>
    /// <param name="cachedPaid">True seeds a stale paid observation that must not replace fresh provider proof.</param>
    /// <param name="stopped">True seeds cached AtlasPay expiration or UniquePay validation failure for explicit recovery.</param>
    /// <returns>A detached exact-store order with its real database id and public OrderId populated.</returns>
    /// <remarks>The active colleague owner starts with 200,000 toman; a legitimate platform-gateway settlement credits only the frozen 10,000-toman profit.</remarks>
    /// <example><code>var order = await SeedOwnerGatewayOrderAsync(databases, provider, "atlaspay", stopped: true);</code></example>
    private static async Task<TenantBotOrder> SeedOwnerGatewayOrderAsync(Databases databases, ServiceProvider provider,
        string gateway, bool cachedPaid = false, bool stopped = false)
    {
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await wallet.SaveUserStatus(new CredUser { TelegramUserId = 711 });
        await wallet.PromotOrDemote(711, true);
        await wallet.AddEmptyUser(722);
        await wallet.MutateWalletAsync(711, 200_000, "owner-gateway-fixture-fund");
        var store = new BotInstance { Id = "tenant-owner-recovery-a", Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 711, TenantStoreNumber = 1, Enabled = true, Username = "test_store",
            Token = "60001:" + new string('a', 35), TelegramBotId = 60001, TenantPriceMarkupPercent = 20,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        provider.GetRequiredService<BotRegistry>().Upsert(store);
        var order = new TenantBotOrder { OrderId = "TENANTBOT-owner-gateway-" + gateway, TenantBotId = store.Id,
            TenantBotUsername = store.Username, OwnerTelegramUserId = 711, CustomerTelegramUserId = 722,
            CustomerChatId = 722, OrderKind = TenantBotOrderKinds.Purchase, ServiceKey = "normal", TrafficGb = 10,
            DurationKey = "m1", AccountCount = 1, SalePriceToman = 60_000, BaseCostToman = 50_000,
            ProfitToman = 10_000, PaymentProvider = gateway, PaymentStatus = TenantBotOrderStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow };
        await using var db = databases.Users.CreateDbContext();
        db.BotInstances.Add(store); db.TenantBotOrders.Add(order); await db.SaveChangesAsync();
        switch (gateway)
        {
            case "hooshpay":
                var hoosh = new HooshPayPaymentInfo { OrderId = order.OrderId, InvoiceUid = "hp-owner", AmountToman = 60_000,
                    PayableAmountToman = 67_200, MerchantCreditToman = 60_000, FeeAmountToman = 7_200, FeePercent = 12,
                    FeeMode = HooshPayFeeModes.Buyer, BotId = store.Id, BotUsername = store.Username, TelegramUserId = 722,
                    ChatId = 722, PaymentPurpose = TenantBotPaymentPurposes.TenantOrder, TenantBotOrderId = order.Id,
                    TenantOwnerTelegramUserId = 711, PaymentStatus = cachedPaid ? HooshPayStatuses.Paid : HooshPayStatuses.Pending };
                db.HooshPayPaymentInfos.Add(hoosh); await db.SaveChangesAsync();
                order.HooshPayPaymentInfoId = hoosh.Id; order.HooshPayInvoiceUid = hoosh.InvoiceUid;
                break;
            case "tetraminator":
                var tetra = new TetraminatorPaymentInfo { OrderId = order.OrderId, PayId = "tm-owner", AmountToman = 60_000,
                    BotId = store.Id, BotUsername = store.Username, TelegramUserId = 722, ChatId = 722,
                    PaymentPurpose = TenantBotPaymentPurposes.TenantOrder, TenantBotOrderId = order.Id,
                    TenantOwnerTelegramUserId = 711, PaymentStatus = cachedPaid ? TetraminatorStatuses.Paid : TetraminatorStatuses.Pending };
                db.TetraminatorPaymentInfos.Add(tetra); await db.SaveChangesAsync(); order.TetraminatorPaymentInfoId = tetra.Id;
                break;
            case "nowpayments":
                var now = new SwapinoPaymentInfo { OrderId = order.OrderId, InvoiceId = "2001", PaymentId = "1001", AmountToman = 60_000,
                    BaseAmount = 2, BaseCurrency = "usdt", PayCurrency = "usdttrc20", BotId = store.Id, BotUsername = store.Username,
                    TelegramUserId = 722, ChatId = 722, PaymentPurpose = TenantBotPaymentPurposes.TenantOrder,
                    TenantBotOrderId = order.Id, TenantOwnerTelegramUserId = 711 };
                now.SetNowPaymentsData(new NowPaymentsPaymentRecordData { OrderId = order.OrderId, InvoiceId = now.InvoiceId,
                    PaymentId = now.PaymentId, PriceAmount = 2, PriceCurrency = "usdt", PayCurrency = "usdttrc20", PayAmount = 2,
                    PaymentStatus = cachedPaid ? NowPaymentsStatuses.Finished : "waiting" });
                db.SwapinoPaymentInfos.Add(now); await db.SaveChangesAsync(); order.NowPaymentsPaymentInfoId = now.Id;
                break;
            case "uniquepay":
                var unique = new UniquePayPaymentInfo { HashId = "up-owner", RefId = "up-ref", BaseAmountToman = 60_000,
                    FeePercent = 12, BotId = store.Id, BotUsername = store.Username, TelegramUserId = 722, ChatId = 722,
                    PaymentPurpose = TenantBotPaymentPurposes.TenantOrder, TenantBotOrderId = order.Id, TenantOwnerTelegramUserId = 711,
                    CreationState = UniquePayCreationStates.Created, SettlementState = UniquePaySettlementStates.Pending,
                    PaymentStatus = cachedPaid ? UniquePayStatuses.Paid : stopped ? UniquePayStatuses.Failed : UniquePayStatuses.Pending };
                db.UniquePayPaymentInfos.Add(unique); await db.SaveChangesAsync(); order.UniquePayPaymentInfoId = unique.Id;
                break;
            case "atlaspay":
                var atlas = new AtlasPayPaymentInfo { MerchantOrderRef = "AtlasPay-owner", ProviderOrderId = 42,
                    BaseAmountToman = 60_000, TotalAmountToman = 60_123, BotId = store.Id, BotUsername = store.Username,
                    TelegramUserId = 722, ChatId = 722, PaymentPurpose = TenantBotPaymentPurposes.TenantOrder,
                    TenantBotOrderId = order.Id, TenantOwnerTelegramUserId = 711, CreationState = AtlasPayCreationStates.Created,
                    SettlementState = AtlasPaySettlementStates.Pending,
                    ProviderStatus = cachedPaid ? "confirmed" : stopped ? "expired" : "awaiting_payment",
                    ReconciliationState = stopped ? AtlasPayReconciliationStates.Exhausted : AtlasPayReconciliationStates.Active };
                db.AtlasPayPaymentInfos.Add(atlas); await db.SaveChangesAsync(); order.AtlasPayPaymentInfoId = atlas.Id;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(gateway));
        }
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>Checks that a rejected inquiry neither approved an order nor changed either party's money.</summary>
    /// <param name="databases">Private database fixture containing the original order.</param>
    /// <param name="provider">Provider containing the real credentials store, not a mocked balance.</param>
    /// <param name="orderId">Positive internal users.db tenant-order id created by the fixture.</param>
    /// <returns>A task completing after payment, provisioning, ledger and wallet assertions.</returns>
    /// <remarks>One fixture funding receipt is expected; every order-specific financial or panel mutation is forbidden.</remarks>
    /// <example><code>await AssertOwnerGatewayUnspentAsync(databases, provider, order.Id);</code></example>
    private static async Task AssertOwnerGatewayUnspentAsync(Databases databases, ServiceProvider provider, int orderId)
    {
        await using var db = databases.Users.CreateDbContext();
        var order = await db.TenantBotOrders.AsNoTracking().SingleAsync(x => x.Id == orderId);
        Assert.False(order.IsFulfilled); Assert.Null(order.PaidAtUtc);
        Assert.Empty(await db.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await db.XuiV3CreationOperations.ToListAsync());
        Assert.Empty(await db.TenantManualPaymentReceipts.ToListAsync());
        var wallet = provider.GetRequiredService<CredentialsStore>();
        Assert.Equal(200_000, await wallet.GetAccountBalance(711)); Assert.Equal(0, await wallet.GetAccountBalance(722));
        await using var credentials = databases.Credentials.CreateDbContext();
        Assert.Single(await credentials.WalletOperations.ToListAsync());
    }

    /// <summary>Supplies deterministic official provider responses while retaining real HTTP deserialization and verification.</summary>
    /// <param name="gateway">Known provider discriminator selecting its official inquiry-response JSON schema.</param>
    /// <param name="outcome">Paid, pending, identity mismatch, or HTTP error response scenario.</param>
    /// <remarks>All amounts are public fixture values. No production API key, Telegram token or network request is used.</remarks>
    private sealed class OwnerGatewayTransport(string gateway, string outcome) : HttpMessageHandler
    {
        /// <summary>Number of actual provider requests, proving unauthorized orders never cross the network boundary.</summary>
        public int RequestCount;

        /// <summary>Returns the selected provider's official inquiry shape without accepting a caller-supplied paid flag.</summary>
        /// <param name="request">Actual provider-client request; the stored invoice identity must be used.</param>
        /// <param name="cancellationToken">Caller cancellation propagated through the HTTP pipeline.</param>
        /// <returns>A disposable JSON HTTP response interpreted by the real production client.</returns>
        /// <remarks>Responses traverse the real production provider clients; no outbound provider network is used.</remarks>
        /// <exception cref="OperationCanceledException">The caller cancels the provider inquiry.</exception>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref RequestCount);
            if (outcome == "error")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{\"error\":\"private-fixture-secret\"}", Encoding.UTF8, "application/json") });
            var paid = outcome != "pending";
            var mismatch = outcome == "mismatch";
            object body = gateway switch
            {
                "hooshpay" => new { success = true, paid, status = paid ? "paid" : "pending", data = new
                {
                    uid = mismatch ? "wrong-hp" : "hp-owner", amount = 60_000, fee_mode = "buyer", fee_percent = 12,
                    fee_amount = 7_200, payable_amount = 67_200, merchant_credit = 60_000,
                    status = paid ? "paid" : "pending"
                } },
                "tetraminator" => new { status = true, pay_id = mismatch ? "wrong-tm" : "tm-owner",
                    amount = 60_000, payment_status = paid ? "paid" : "pending" },
                "nowpayments" => new { payment_id = mismatch ? "wrong-np" : "1001", invoice_id = "2001",
                    order_id = "TENANTBOT-owner-gateway-nowpayments", payment_status = paid ? "finished" : "waiting",
                    price_amount = 2m, price_currency = "usdt", pay_amount = 2m, pay_currency = "usdttrc20", actually_paid = paid ? 2m : 0m },
                "uniquepay" => new { status = true, code = 200, hashId = mismatch ? "wrong-up" : "up-owner", refId = "up-ref", invoice = new
                {
                    id = "up-ref", amount = 67_200, fee = 7_200, feePayer = "buyer", currency = "IRT", isPaid = paid
                } },
                "atlaspay" => new { success = true, id = mismatch ? 99 : 42, merchantOrderRef = "AtlasPay-owner",
                    status = paid ? "confirmed" : "awaiting_payment", paid, totalAmountToman = 60_123,
                    actualReceivedAmountToman = paid ? 60_123 : 0, requiresManualDelivery = false },
                _ => throw new ArgumentOutOfRangeException(nameof(gateway))
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") });
        }
    }
}
