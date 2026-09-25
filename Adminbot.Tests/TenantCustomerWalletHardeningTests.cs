using System.Net;
using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Mandatory join gates every new tenant-wallet admission step but never becomes financial settlement policy.</summary>
    [Fact]
    public async Task TenantCustomerWallet_MandatoryJoin_gates_new_wallet_admission_until_membership_is_verified()
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var createCalls = 0;
        var atlasHandler = new AtlasHttpHandler((_, _, _, _) =>
        {
            Interlocked.Increment(ref createCalls);
            var json = JsonConvert.SerializeObject(new
            {
                success = true, orderId = 77, trackingCode = "TRK-WALLET",
                totalAmountToman = 100000, cardNumberMasked = "****",
                paymentDeadlineAt = "2027-01-01T00:00:00Z",
                customerStartLink = "https://t.me/atlaspay_bot?start=wallet"
            });
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, json));
        });        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration, new HttpClient(atlasHandler)), out _, out _);
        var (wallet, _, _) = await SeedCustomerWalletOrderAsync(databases);
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.TenantMandatoryJoinEnabled = true;
            store.TenantChannelIdsJson = JsonConvert.SerializeObject(new[] { "@required" });
            store.TenantAtlasPayEnabled = true;
            await db.SaveChangesAsync();
        }

        var state = provider.GetRequiredService<UserStateStore>();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var method = typeof(TenantBotService).GetMethod(
            "TryHandleCustomerWalletAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var customer = await wallet.GetUserStatusWithId(123);
        Update Callback(string data, int messageId = 17) => new()
        {
            CallbackQuery = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"), Data = data,
                From = new Telegram.Bot.Types.User { Id = 123 },
                Message = new Message { Id = messageId, Chat = new Chat { Id = 123 } }
            }
        };
        Update Text(string value) => new()
        {
            Message = new Message
            {
                From = new Telegram.Bot.Types.User { Id = 123 },
                Chat = new Chat { Id = 123 }, Text = value
            }
        };        var context = provider.GetRequiredService<BotContextAccessor>();
        async Task Route(StorefrontClient client, Update update)
        {
            var scopedState = await state.GetUserStatus(123);
            var handled = await (Task<bool>)method.Invoke(
                service, new object[] { client, update, customer, scopedState, CancellationToken.None })!;
            Assert.True(handled);
        }

        var blocked = new MembershipProbeClient(ChatMemberStatus.Left);
        using (context.Push(new BotRuntimeContext
               { Config = new BotInstanceConfig { Id = "tenant-a", Type = BotInstanceTypes.Tenant }, Client = blocked }))
        {
            await Route(blocked, Callback("TCW:home"));
            await Route(blocked, Callback("TCW:charge"));
            await Route(blocked, Callback("TCW:h:0"));

            await state.SaveUserStatus(new User { Id = 123, Flow = "tenant-wallet-charge", LastStep = "amount" });
            await Route(blocked, Text("100000"));

            const string blockedNonce = "blocked-invoice";
            await state.SaveUserStatus(new User
            {
                Id = 123, Flow = "tenant-wallet-charge", LastStep = "gateway",
                ConfigLink = "100000", SubLink = blockedNonce
            });
            await Route(blocked, Callback($"TCW:g:{(int)PaymentGateway.AtlasPay}:{blockedNonce}"));
            await Route(blocked, Callback("TN:PAYWALLET:normal:10:m1:1"));
            await Route(blocked, Callback("TN:RNWALLET:1"));
        }

        Assert.True(blocked.GetChatMemberCalls >= 7);
        Assert.Contains(blocked.Answers, answer => answer.Contains("ابتدا در کانال عضو شوید"));
        Assert.DoesNotContain(blocked.Texts, text => text.Contains("کیف پول سراسری پلتفرم"));
        await using (var db = databases.Users.CreateDbContext())
            Assert.Empty(await db.AtlasPayPaymentInfos.ToListAsync());
        Assert.Equal(0, createCalls);        var joined = new MembershipProbeClient(ChatMemberStatus.Member);
        using (context.Push(new BotRuntimeContext
               { Config = new BotInstanceConfig { Id = "tenant-a", Type = BotInstanceTypes.Tenant }, Client = joined }))
        {
            await Route(joined, Callback("TCW:home"));
            Assert.Contains(joined.Texts, text =>
                text.Replace('٬', ',').Contains("500,000", StringComparison.Ordinal));

            await Route(joined, Callback("TCW:charge"));
            await Route(joined, Text("100000"));
            var invoiceState = await state.GetUserStatus(123);
            Assert.Equal("gateway", invoiceState.LastStep);
            await Route(joined, Callback($"TCW:g:{(int)PaymentGateway.AtlasPay}:{invoiceState.SubLink}"));

            var joinRejectsBeforeSpendActions = joined.Answers.Count(answer => answer.Contains("ابتدا در کانال عضو شوید"));
            await Route(joined, Callback("TN:PAYWALLET:normal:10:m1:1"));
            await Route(joined, Callback("TN:RNWALLET:1"));
            Assert.Equal(joinRejectsBeforeSpendActions, joined.Answers.Count(answer => answer.Contains("ابتدا در کانال عضو شوید")));
        }

        Assert.True(joined.GetChatMemberCalls >= 1);
        Assert.Equal(1, createCalls);
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = Assert.Single(await db.AtlasPayPaymentInfos.ToListAsync());
            Assert.Equal(BotInstanceTypes.Tenant, payment.WalletOriginBotType);
            Assert.Equal(789, payment.WalletOriginTelegramBotId);
            Assert.Equal("tenant-a", payment.BotId);
        }
    }
    /// <summary>An invoice created earlier may still be checked and settled after mandatory-join membership is lost.</summary>
    [Fact]
    public async Task TenantCustomerWallet_Existing_payment_check_settles_after_membership_is_lost()
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlasHandler = new AtlasHttpHandler((_, _, _, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, StatusJson("confirmed", paid: true, actual: 250000))));
        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration, new HttpClient(atlasHandler)), out _, out _);
        var (wallet, _, _) = await SeedCustomerWalletOrderAsync(databases);

        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.TenantMandatoryJoinEnabled = true;
            store.TenantChannelIdsJson = JsonConvert.SerializeObject(new[] { "@required" });
            store.Token = "789:" + new string('a', 35);
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId = 123;
            payment.ChatId = 123;
            payment.BotId = store.Id;
            payment.BotUsername = "wallet_test";
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.WalletOriginBotType = BotInstanceTypes.Tenant;
            payment.WalletOriginTelegramBotId = 789;
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }        var lostMembership = new MembershipProbeClient(ChatMemberStatus.Left);
        var botContext = provider.GetRequiredService<BotContextAccessor>();
        using (botContext.Push(new BotRuntimeContext
               { Config = new BotInstanceConfig { Id = "tenant-a", Type = BotInstanceTypes.Tenant }, Client = lostMembership }))
        {
            await using var scope = provider.CreateAsyncScope();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
            var callback = new CallbackQuery
            {
                Id = "lost-membership-check",
                Data = $"apchk_{paymentId}",
                From = new Telegram.Bot.Types.User { Id = 123 },
                Message = new Message { Id = 19, Chat = new Chat { Id = 123 } }
            };
            Assert.True(await telegram.TryHandleTenantWalletPaymentCheckAsync(callback, CancellationToken.None));
        }

        Assert.Equal(0, lostMembership.GetChatMemberCalls);
        Assert.Equal(750000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.True(saved.IsAddedToBalance);
        Assert.Equal(AtlasPaySettlementStates.Settled, saved.SettlementState);
        Assert.Single(await verify.WalletLedgerEntries.Where(x =>
            x.Provider == "atlaspay" && x.BotId == "tenant-a" && x.TelegramUserId == 123).ToListAsync());
        Assert.Empty(await verify.ReferralPaymentEvents.ToListAsync());
        Assert.Empty(await verify.ReferralRewards.ToListAsync());
    }
    /// <summary>Settlement survives a BotFather replacement while the historical notification is parked instead of rerouted.</summary>
    [Fact]
    public async Task TenantCustomerWallet_Notification_identity_change_never_reroutes_or_repeats_finance()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:1"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["referral:enabled"] = "true",
                ["referral:minimumEligiblePaymentAmountToman"] = "1",
                ["referral:firstPayment:referrerRewardPercent"] = "10",
                ["referral:firstPayment:referredRewardPercent"] = "10"
            }).Build();
        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration), out var registry, out var clients);
        var (wallet, _, _) = await SeedCustomerWalletOrderAsync(databases);
        await wallet.AddEmptyUser(999);
        await provider.GetRequiredService<ReferralService>()
            .RegisterRelationshipAsync(123, ReferralCodeCodec.Encode(999), "main", BotInstanceTypes.Owned);

        var payment = AtlasPayPaymentInfo.CreateWalletCharge(123, 321, 100000);
        payment.BotId = "tenant-a";
        payment.BotUsername = "wallet_a";
        payment.WalletOriginBotType = BotInstanceTypes.Tenant;
        payment.WalletOriginTelegramBotId = 789;
        payment.ProviderStatus = "confirmed";
        payment.PaidAtUtc = DateTime.UtcNow;        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.Token = "789:" + new string('a', 35);
            store.Username = "wallet_a";
            registry.Upsert(store);
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var settlement = scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>();
            Assert.Equal(NowPaymentsSettlementStatus.Applied,
                (await settlement.ApplyOfficialPaymentAsync(payment, "identity-fixture")).Status);
            Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded,
                (await settlement.ApplyOfficialPaymentAsync(payment, "identity-fixture-replay")).Status);
        }

        Assert.Equal(600000, await wallet.GetAccountBalance(123));
        Assert.Equal(0, await wallet.GetAccountBalance(999));

        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.TelegramBotId = 790;
            store.Token = "790:" + new string('b', 35);
            store.Username = "wallet_b";
            TenantCustomerWalletPolicy.Revoke(store);
            await db.SaveChangesAsync();
            registry.Upsert(store);
        }        using var worker = new PaymentSettlementNotificationWorker(
            databases.Users,
            provider.GetRequiredService<BotClientProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentSettlementNotificationWorker>.Instance);
        var claim = typeof(PaymentSettlementNotificationWorker).GetMethod(
            "ClaimDueBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliver = typeof(PaymentSettlementNotificationWorker).GetMethod(
            "DeliverClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batch = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(
            worker, new object[] { CancellationToken.None })!;
        var notification = Assert.Single(batch, x => !x.IsTenantOwnerReport);
        Assert.Single(batch, x => x.IsTenantOwnerReport);
        Assert.Equal(789, notification.WalletOriginTelegramBotId);
        await (Task)deliver.Invoke(worker, new object[] { notification, CancellationToken.None })!;

        Assert.False(clients.TryGetValue("tenant-a", out var replacementClient) && replacementClient.Texts.Count > 0);
        Assert.Equal(600000, await wallet.GetAccountBalance(123));
        Assert.Equal(0, await wallet.GetAccountBalance(999));

        await using (var db = databases.Users.CreateDbContext())
        {
            var savedPayment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == payment.Id);
            Assert.True(savedPayment.IsAddedToBalance);
            Assert.Equal(AtlasPaySettlementStates.Settled, savedPayment.SettlementState);
            Assert.Single(await db.WalletLedgerEntries.Where(x =>
                x.Provider == "atlaspay" && x.TelegramUserId == 123).ToListAsync());
            Assert.Empty(await db.ReferralPaymentEvents.ToListAsync());
            Assert.Empty(await db.ReferralRewards.ToListAsync());
            var savedNotification = await db.PaymentSettlementNotifications
                .SingleAsync(x => !x.NotificationKey.StartsWith(PaymentSettlementNotification.TenantOwnerReportPrefix));
            Assert.Equal(PaymentSettlementNotificationStatuses.ManualReview, savedNotification.Status);
            Assert.Equal("bot_identity_changed", savedNotification.LastError);
            Assert.Equal(789, savedNotification.WalletOriginTelegramBotId);
        }        await using (var replayScope = provider.CreateAsyncScope())
        {
            var settlement = replayScope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>();
            Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded,
                (await settlement.ApplyOfficialPaymentAsync(payment, "after-delivery-block")).Status);
        }
        Assert.Equal(600000, await wallet.GetAccountBalance(123));

        await using (var credentials = databases.Credentials.CreateDbContext())
            Assert.Equal(1, await credentials.WalletOperations.CountAsync(x =>
                x.OperationKey == $"payment:atlaspay:{payment.Id}:credit"));

        var noRetry = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(
            worker, new object[] { CancellationToken.None })!;
        Assert.Empty(noRetry);
    }
}

public sealed partial class ConcurrencyTests
{
    /// <summary>Every tenant home-style reply uses the fresh persisted wallet approval instead of the runtime snapshot.</summary>
    [Fact]
    public async Task TenantCustomerWallet_Approved_reply_keyboards_remain_visible_across_navigation_and_revocation_hides_them()
    {
        using var databases = new Databases();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        await using var provider = AtlasTenantProvider(
            databases, configuration, new AtlasPay(configuration), out var registry, out _);
        var (wallet, _, seededOrder) = await SeedCustomerWalletOrderAsync(databases);
        var customer = await wallet.GetUserStatusWithId(123);
        var botContext = provider.GetRequiredService<BotContextAccessor>();
        var stateStore = provider.GetRequiredService<UserStateStore>();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var handle = typeof(TenantBotService).GetMethod(
            "HANDLECUSTOMERMESSAGEASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task<StorefrontClient> SendAsync(string text, User state, Document? document = null)
        {
            var client = new StorefrontClient();
            using var current = botContext.Push(new BotRuntimeContext
            {
                Config = new BotInstanceConfig { Id = "tenant-a", Type = BotInstanceTypes.Tenant },
                Client = client
            });
            var message = new Message
            {
                Id = 41,
                From = new Telegram.Bot.Types.User { Id = 123 },
                Chat = new Chat { Id = 123 },
                Text = document == null ? text : null,
                Document = document
            };
            await (Task)handle.Invoke(service, new object[]
            {
                client, message, customer, state, CancellationToken.None
            })!;
            return client;
        }        static void AssertWalletVisible(StorefrontClient client)
        {
            Assert.Contains("💰 کیف پول", client.ReplyButtons);
            Assert.Contains("📒 تراکنش‌های من", client.ReplyButtons);
        }
        static void AssertWalletHidden(StorefrontClient client)
        {
            Assert.DoesNotContain("💰 کیف پول", client.ReplyButtons);
            Assert.DoesNotContain("📒 تراکنش‌های من", client.ReplyButtons);
        }

        AssertWalletVisible(await SendAsync("/start", new User { Id = 123 }));
        AssertWalletVisible(await SendAsync("💬 پشتیبانی", new User { Id = 123 }));
        AssertWalletVisible(await SendAsync("❌ انصراف", new User
        {
            Id = 123, Flow = "TENANTBOT-purchase", LastStep = "purchase-traffic", SelectedCountry = "normal"
        }));
        AssertWalletVisible(await SendAsync("❌ انصراف", new User
        {
            Id = 123, Flow = "TENANTBOT-renew", LastStep = "renew-account"
        }));
        AssertWalletVisible(await SendAsync("اکانت‌های من", new User { Id = 123 }));

        using (botContext.Push(new BotRuntimeContext
               { Config = new BotInstanceConfig { Id = "tenant-a", Type = BotInstanceTypes.Tenant }, Client = new StorefrontClient() }))
            await stateStore.SetPendingReceiptTargetAsync(123, seededOrder.Id);
        var invalidReceipt = new Document
        {
            FileId = "receipt-pdf", FileUniqueId = "receipt-pdf-unique",
            FileName = "receipt.pdf", MimeType = "application/pdf"
        };
        AssertWalletVisible(await SendAsync("", new User { Id = 123 }, invalidReceipt));
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync(x => x.Id == "tenant-a");
            TenantCustomerWalletPolicy.Revoke(store);
            await db.SaveChangesAsync();
            // Deliberately do not refresh BotRegistry: stale runtime configuration must not become UI authority.
        }

        AssertWalletHidden(await SendAsync("/start", new User { Id = 123 }));
        AssertWalletHidden(await SendAsync("💬 پشتیبانی", new User { Id = 123 }));
        AssertWalletHidden(await SendAsync("❌ انصراف", new User
        {
            Id = 123, Flow = "TENANTBOT-purchase", LastStep = "purchase-traffic", SelectedCountry = "normal"
        }));
        AssertWalletHidden(await SendAsync("❌ انصراف", new User
        {
            Id = 123, Flow = "TENANTBOT-renew", LastStep = "renew-account"
        }));
        AssertWalletHidden(await SendAsync("اکانت‌های من", new User { Id = 123 }));
        AssertWalletHidden(await SendAsync("", new User { Id = 123 }, invalidReceipt));
    }
}
