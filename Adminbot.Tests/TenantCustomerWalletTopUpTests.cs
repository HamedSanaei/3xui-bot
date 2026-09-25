using System.Net;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        await wallet.AddEmptyUser(456);
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
                ["cardNumberMasked"] = "****", ["cardNumber"] = "6037999999991234",
                ["cardHolderName"] = "Fixture Holder", ["bankName"] = "Fixture Bank",
                ["paymentDeadlineAt"] = "2027-01-01T00:00:00Z", ["customerStartLink"] = "https://t.me/fixture_bot?start=test"
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
        if (gateway == PaymentGateway.AtlasPay)
        {
            Assert.NotNull(invoice.DirectPayment);
            Assert.Equal("6037999999991234", invoice.DirectPayment!.CardNumber);
            Assert.Equal("Fixture Holder", invoice.DirectPayment.CardHolderName);
            Assert.Equal("Fixture Bank", invoice.DirectPayment.BankName);
            Assert.Equal(100000, invoice.DirectPayment.TotalAmountToman);
        }
        else
        {
            Assert.Null(invoice.DirectPayment);
        }
        await using (var db = databases.Users.CreateDbContext())
        { var store = await db.BotInstances.SingleAsync(); TenantCustomerWalletPolicy.Revoke(store); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => charges.CreateTenantAsync("tenant-a", 123, 123, 100000, gateway, default));
        // The provider's committed credit receipt is authoritative even if users.db did not yet record settlement.
        var key = $"payment:{providerKey}:1:credit";
        await wallet.MutateWalletAsync(123, 100000, key, botId: "tenant-a");
        await using (var db = databases.Credentials.CreateDbContext())
            await db.WalletOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var ledger = new WalletLedgerService(databases.Users, wallet);
        var mirror = new TenantWalletOwnerTopUpMirrorService(
            wallet, ledger, databases.Users, NullLogger<TenantWalletOwnerTopUpMirrorService>.Instance);
        var recovery = new WalletOperationReconciliationService(
            databases.Credentials, databases.Users, ledger, mirror,
            NullLogger<WalletOperationReconciliationService>.Instance);
        await recovery.ReconcileAsync(); await recovery.ReconcileAsync();
        Assert.Equal(600000, await wallet.GetAccountBalance(123));
        Assert.Equal(100000, await wallet.GetAccountBalance(456));
        await using var verify = databases.Users.CreateDbContext();
        var entry = await verify.WalletLedgerEntries.SingleAsync(x => x.IdempotencyKey == key);
        Assert.Equal("tenant", entry.BotType); Assert.Equal("tenant-a", entry.BotId); Assert.Equal(providerKey, entry.Provider);
        var mirrorKey = $"tenant-wallet-topup:{providerKey}:1:owner-credit";
        var mirrorEntry = await verify.WalletLedgerEntries.SingleAsync(x => x.IdempotencyKey == mirrorKey);
        Assert.Equal(WalletLedgerReasons.TenantWalletTopUpMirror, mirrorEntry.Reason);
        Assert.Equal(456, mirrorEntry.TelegramUserId); Assert.Equal(123, mirrorEntry.CounterpartyTelegramUserId);
        var notificationKey = $"tenant-wallet:{providerKey}:1";
        var notification = await verify.PaymentSettlementNotifications
            .SingleAsync(x => x.NotificationKey == notificationKey);
        Assert.Equal(notificationKey, notification.NotificationKey);
        Assert.Equal("tenant-a", notification.BotId); Assert.Equal(BotInstanceTypes.Tenant, notification.WalletOriginBotType);
        Assert.Equal(789, notification.WalletOriginTelegramBotId); Assert.Equal(123, notification.TelegramUserId);
        Assert.Equal(123, notification.ChatId); Assert.Equal(1, posts);
    }

    [Fact]
    public async Task TenantCustomerWallet_AtlasPay_400_persists_provider_reason_without_retry()
    {
        using var databases = new Databases();
        await SeedCustomerWalletOrderAsync(databases);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["atlasPayApiKey"] = "test-only",
            ["atlasPayBaseUrl"] = "https://atlas.example/api/v1"
        }).Build();
        var posts = 0;
        var handler = new AtlasHttpHandler((_, _, _, _) =>
        {
            Interlocked.Increment(ref posts);
            return Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, "{\"error\":\"fixture invalid amount\"}"));
        });
        HttpClient Client() => new(handler, disposeHandler: false);
        var availability = new GatewayAvailabilityProbe();
        var policy = new TenantCustomerWalletPolicy(
            databases.Users,
            config.Get<AppConfig>()!,
            NullLogger<TenantCustomerWalletPolicy>.Instance);
        var charges = new WalletChargeApplicationService(
            new UserWorkflowStore(databases.Users),
            config.Get<AppConfig>()!,
            availability,
            policy,
            new HooshPay(config, Client()),
            new Tetraminator(config, Client()),
            new UniquePay(config, Client()),
            new AtlasPay(config, Client()),
            new NowPayments(config, Client(), new TenantWalletQuote()));

        var ex = await Assert.ThrowsAsync<AtlasPayApiException>(() =>
            charges.CreateTenantAsync("tenant-a", 123, 123, 50_000, PaymentGateway.AtlasPay, default));

        Assert.Equal(1, posts);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("fixture invalid amount", AtlasPay.SafeProviderErrorMessage(ex));
        await using var db = databases.Users.CreateDbContext();
        var payment = await db.AtlasPayPaymentInfos.SingleAsync();
        Assert.Equal(AtlasPayCreationStates.Failed, payment.CreationState);
        Assert.Equal("400", payment.CreationErrorCode);
        Assert.Equal("400", payment.ErrorCode);
        Assert.Contains("fixture invalid amount", payment.ErrorMessage);
    }

    [Fact]
    public async Task TenantCustomerWallet_HooshPay_provisional_credit_mirrors_owner_only_after_official_confirmation()
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration), out _, out _);
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await SeedCustomerWalletOrderAsync(databases);
        await wallet.AddEmptyUser(456);

        var payment = HooshPayPaymentInfo.CreateWalletCharge(
            123, 100_000, "https://merchant.example/hp", "https://merchant.example/return", 123);
        payment.BotId = "tenant-a";
        payment.BotUsername = "wallet_test";
        payment.WalletOriginBotType = BotInstanceTypes.Tenant;
        payment.WalletOriginTelegramBotId = 789;
        payment.TenantOwnerTelegramUserId = 456;
        payment.PaymentStatus = HooshPayStatuses.Pending;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.HooshPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var settlement = scope.ServiceProvider.GetRequiredService<HooshPaySettlementService>();
            var provisional = await settlement.ApplyProvisionalWalletPaymentAsync(payment, 999, 123);
            Assert.Equal(NowPaymentsSettlementStatus.Applied, provisional.Status);
        }
        Assert.Equal(600_000, await wallet.GetAccountBalance(123));
        Assert.Equal(0, await wallet.GetAccountBalance(456));

        HooshPayPaymentInfo official;
        await using (var db = databases.Users.CreateDbContext())
        {
            official = await db.HooshPayPaymentInfos.SingleAsync(x => x.Id == payment.Id);
            Assert.True(official.IsProvisionallyApproved);
            official.PaymentStatus = HooshPayStatuses.Paid;
            await db.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var settlement = scope.ServiceProvider.GetRequiredService<HooshPaySettlementService>();
            Assert.True(await settlement.RecordProviderConfirmationAfterProvisionalAsync(official, "fixture-official"));
            Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded,
                (await settlement.ApplyFinishedPaymentAsync(official, "fixture-official")).Status);
            Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded,
                (await settlement.ApplyFinishedPaymentAsync(official, "fixture-replay")).Status);
        }

        Assert.Equal(600_000, await wallet.GetAccountBalance(123));
        Assert.Equal(100_000, await wallet.GetAccountBalance(456));
        await using var credentials = databases.Credentials.CreateDbContext();
        var mirrorKey = $"tenant-wallet-topup:hooshpay:{payment.Id}:owner-credit";
        Assert.Equal(1, await credentials.WalletOperations.CountAsync(x => x.OperationKey == mirrorKey));
        await using var users = databases.Users.CreateDbContext();
        Assert.Equal(1, await users.WalletLedgerEntries.CountAsync(x =>
            x.IdempotencyKey == mirrorKey && x.Reason == WalletLedgerReasons.TenantWalletTopUpMirror));
    }

    [Fact]
    public async Task TenantCustomerWallet_Personal_card_approval_credits_only_customer_once()
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration), out var registry, out _);
        var wallet = provider.GetRequiredService<CredentialsStore>();
        await wallet.AddEmptyUser(123);
        await wallet.MutateWalletAsync(123, 10_000, "fixture:card-customer");
        await wallet.AddEmptyUser(456);
        await wallet.MutateWalletAsync(456, 70_000, "fixture:card-owner");

        var store = new BotInstance
        {
            Id = "tenant-card-wallet",
            Username = "tenant_card_wallet",
            Token = "789:" + new string('a', 35),
            Type = BotInstanceTypes.Tenant,
            Enabled = true,
            OwnerTelegramUserId = 456,
            TelegramBotId = 789,
            TenantStoreNumber = 1,
            TenantCardPaymentEnabled = true,
            TenantCardNumber = "6037991234567890",
            TenantCardHolderName = "Fixture Owner",
            CreatedAtUtc = DateTime.UtcNow
        };

        int receiptId;
        int orderId;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(store);
            var order = new TenantBotOrder
            {
                OrderId = "wallet-card-topup-1",
                TenantBotId = store.Id,
                TenantBotUsername = store.Username,
                OwnerTelegramUserId = 456,
                CustomerTelegramUserId = 123,
                CustomerChatId = 123,
                OrderKind = TenantBotOrderKinds.WalletCharge,
                AccountCount = 0,
                SalePriceToman = 100_000,
                BaseCostToman = 0,
                ProfitToman = 0,
                PaymentProvider = "tenant_card",
                PaymentStatus = TenantBotOrderStatuses.ReceiptSubmitted,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.TenantBotOrders.Add(order);
            await db.SaveChangesAsync();
            var receipt = new TenantManualPaymentReceipt
            {
                TenantBotOrderId = order.Id,
                OrderId = order.OrderId,
                TenantBotId = order.TenantBotId,
                TenantBotUsername = order.TenantBotUsername,
                OwnerTelegramUserId = 456,
                CustomerTelegramUserId = 123,
                CustomerChatId = 123,
                PhotoFileId = "fixture-photo",
                AmountToman = 100_000,
                Status = TenantManualPaymentReceiptStatuses.Pending,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.TenantManualPaymentReceipts.Add(receipt);
            await db.SaveChangesAsync();
            order.ManualReceiptId = receipt.Id;
            await db.SaveChangesAsync();
            receiptId = receipt.Id;
            orderId = order.Id;
        }
        registry.Upsert(store);

        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var first = await service.APPROVEMANUALRECEIPTASYNC(receiptId, 456, default);
        var second = await service.APPROVEMANUALRECEIPTASYNC(receiptId, 456, default);

        Assert.Contains("100,000", first);
        Assert.Contains("100,000", second);
        Assert.Equal(110_000, await wallet.GetAccountBalance(123));
        Assert.Equal(70_000, await wallet.GetAccountBalance(456));

        await using (var credentials = databases.Credentials.CreateDbContext())
        {
            var key = $"tenant-card-wallet-charge:{orderId}:credit";
            Assert.Equal(1, await credentials.WalletOperations.CountAsync(x => x.OperationKey == key));
            var credit = await credentials.WalletOperations.SingleAsync(x => x.OperationKey == key);
            Assert.Equal(100_000, credit.AmountToman);
            Assert.Equal(123, credit.TelegramUserId);
        }
        await using (var verify = databases.Users.CreateDbContext())
        {
            var key = $"tenant-card-wallet-charge:{orderId}:credit";
            Assert.Equal(1, await verify.WalletLedgerEntries.CountAsync(x => x.IdempotencyKey == key));
            var entry = await verify.WalletLedgerEntries.SingleAsync(x => x.IdempotencyKey == key);
            Assert.Equal(WalletLedgerReasons.WalletCharge, entry.Reason);
            Assert.Equal("tenant_card", entry.Provider);
            Assert.Equal(123, entry.TelegramUserId);
            Assert.Equal(456, entry.OwnerTelegramUserId);
            Assert.Empty(await verify.TenantBotLedgerEntries.Where(x => x.TenantBotOrderId == orderId).ToListAsync());
            Assert.False(await verify.WalletLedgerEntries.AnyAsync(x =>
                x.ReferenceId == orderId.ToString() && x.Reason == WalletLedgerReasons.TenantWalletTopUpMirror));
            var order = await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId);
            Assert.True(order.IsFulfilled);
            Assert.False(order.IsOwnerCredited);
            Assert.Equal(0, order.OwnerWalletDelta);
        }
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
