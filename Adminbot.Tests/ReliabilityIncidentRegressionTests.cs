using System.Collections.Concurrent;
using System.Reflection;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Xunit;

public sealed partial class ConcurrencyTests
{
    [Fact]
    public void Bot_client_provider_requires_exact_enabled_transport()
    {
        var configuration = IncidentConfiguration();
        var registry = new BotRegistry(configuration);
        registry.Upsert(new BotInstance { Id = "tenant-good", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20001) });
        registry.Upsert(new BotInstance { Id = "tenant-disabled", Type = BotInstanceTypes.Tenant, Enabled = false, Token = Token(20002) });
        registry.Upsert(new BotInstance { Id = "tenant-tokenless", Type = BotInstanceTypes.Tenant, Enabled = true, Token = null });
        var clients = new ConcurrentDictionary<string, StorefrontClient>(StringComparer.OrdinalIgnoreCase);
        var provider = new BotClientProvider(registry, bot => clients.GetOrAdd(bot.Id, _ => new StorefrontClient()));

        Assert.Same(provider.GetDefaultClient(), provider.GetClient("main"));
        Assert.Same(provider.GetClient("tenant-good"), provider.GetClient("TENANT-GOOD"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("missing"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("tenant-disabled"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("tenant-tokenless"));
        Assert.False(clients.ContainsKey("missing"));
    }

    [Fact]
    public async Task Reset_storefront_historical_callbacks_are_safe_and_preserve_business_state()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance
            {
                Id = "tenant-reset", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 711,
                Enabled = false, Token = null
            });

            int fulfilledReceiptId;
            int pendingReceiptId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var fulfilled = HistoricalOrder("fulfilled-old", fulfilled: true);
                var pending = HistoricalOrder("pending-old", fulfilled: false);
                db.TenantBotOrders.AddRange(fulfilled, pending);
                await db.SaveChangesAsync();
                var fulfilledReceipt = ReceiptFor(fulfilled);
                var pendingReceipt = ReceiptFor(pending);
                db.TenantManualPaymentReceipts.AddRange(fulfilledReceipt, pendingReceipt);
                await db.SaveChangesAsync();
                fulfilledReceiptId = fulfilledReceipt.Id;
                pendingReceiptId = pendingReceipt.Id;
            }

            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var resend = await service.RESENDMANUALRECEIPTACCOUNTASYNC(fulfilledReceiptId, 711, default);
            Assert.NotEmpty(resend);
            Assert.True(clients.TryGetValue("assistant", out var assistantClient));
            Assert.Contains(assistantClient.Texts, text => text.Contains("stored@example.test", StringComparison.Ordinal));
            Assert.False(clients.ContainsKey("tenant-reset"));

            var final = await service.APPROVEMANUALRECEIPTASYNC(pendingReceiptId, 711, default);
            Assert.NotEmpty(final);
            Assert.DoesNotContain("No Telegram bot token is configured.", final, StringComparison.Ordinal);

            await using var verify = databases.Users.CreateDbContext();
            var reloadedPendingReceipt = await verify.TenantManualPaymentReceipts.SingleAsync(x => x.Id == pendingReceiptId);
            var pendingOrder = await verify.TenantBotOrders.SingleAsync(x => x.OrderId == "pending-old");
            Assert.Equal(TenantManualPaymentReceiptStatuses.Pending, reloadedPendingReceipt.Status);
            Assert.Null(reloadedPendingReceipt.ReviewerTelegramUserId);
            Assert.False(pendingOrder.IsFulfilled);
            Assert.Equal(TenantBotOrderStatuses.ReceiptSubmitted, pendingOrder.PaymentStatus);
            Assert.Empty(await verify.TenantBotLedgerEntries.Where(x => x.TenantBotOrderId == pendingOrder.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task Blocked_receipt_relay_does_not_hold_same_user_scheduler_lane()
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int orderId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = HistoricalOrder("lane-order", fulfilled: false);
                db.TenantBotOrders.Add(order);
                await db.SaveChangesAsync();
                orderId = order.Id;
            }

            var committed = Signal();
            var secondStarted = Signal();
            var sender = new BlockingReceiptSender();
            var worker = new TenantManualReceiptNotificationWorker(
                databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);

            var persist = typeof(TenantBotService).GetMethod(
                "PERSISTTENANTMANUALRECEIPTASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var executor = new Executor(async (item, token) =>
            {
                if (item.Update.Id == 1)
                {
                    await using var scope = provider.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
                    var task = (Task<int>)persist.Invoke(service, new object[] { orderId, "photo-file", token })!;
                    await task;
                    committed.TrySetResult();
                    return;
                }

                secondStarted.TrySetResult();
            });

            using var scheduler = Create(databases, executor, concurrency: 1);
            await scheduler.StartAsync(default);
            try
            {
                await scheduler.EnqueueAsync("tenant-reset", Update(1, 7468859738), default);
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => IsInboxCompletedAsync(databases, 1));

                var workerTask = worker.ProcessOnceAsync();
                await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(workerTask.IsCompleted);

                await scheduler.EnqueueAsync("tenant-reset", Update(2, 7468859738), default);
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(sender.Release.Task.IsCompleted);

                sender.Release.TrySetResult();
                Assert.Equal(1, await workerTask.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                sender.Release.TrySetResult();
                await scheduler.StopAsync(default);
            }
        }
    }

    [Fact]
    public async Task Receipt_outbox_survives_restart_and_delivers_at_most_once()
    {
        using var databases = new Databases();
        int receiptId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var receipt = new TenantManualPaymentReceipt
            {
                TenantBotOrderId = 9101, OrderId = "restart-order", TenantBotId = "tenant-reset",
                OwnerTelegramUserId = 711, CustomerTelegramUserId = 7468859738,
                CustomerChatId = 7468859738, PhotoFileId = "photo-file", AmountToman = 1000
            };
            db.TenantManualPaymentReceipts.Add(receipt);
            await db.SaveChangesAsync();
            receiptId = receipt.Id;
            db.TenantManualReceiptNotifications.Add(new TenantManualReceiptNotification { ReceiptId = receiptId });
            await db.SaveChangesAsync();
        }

        var sender = new CountingReceiptSender();
        var first = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        var second = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        await Task.WhenAll(first.ProcessOnceAsync(), second.ProcessOnceAsync());

        var restarted = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(1, sender.SuccessCount);

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantManualReceiptNotifications.SingleAsync(x => x.ReceiptId == receiptId);
        Assert.Equal(TenantManualReceiptNotificationStatuses.Delivered, row.Status);
        Assert.Equal(4242, row.TelegramMessageId);
        Assert.NotNull(row.DeliveredAtUtc);
    }

    [Fact]
    public void Tenant_stale_callback_noise_is_suppressed_without_hiding_business_failures()
    {
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "IGNORING STALE Telegram callback answer. callbackId=example", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant payment settlement failed", new InvalidOperationException("wallet mutation failed")));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant XUI creation failed", new InvalidOperationException("panel rejected request")));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant bot token validation failed", new InvalidOperationException("invalid token")));
    }

    [Fact]
    public async Task Scheduler_persists_safe_bot_transport_failure_code()
    {
        using var databases = new Databases();
        using var scheduler = Create(databases, new Executor((_, _) =>
            throw new BotTransportUnavailableException("bot_not_found")), concurrency: 1);
        await scheduler.StartAsync(default);
        await scheduler.EnqueueAsync("main", Update(7001, 6693295925), default);
        await scheduler.StopAsync(default);

        await using var db = databases.Users.CreateDbContext();
        var row = await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 7001);
        Assert.Equal("completed_with_error", row.Status);
        Assert.Equal(BotTransportUnavailableException.FailureCode, row.FailureCode);
    }

    [Fact]
    public async Task Rejected_receipt_resubmission_gets_a_new_deduplicated_outbox_key()
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int orderId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = HistoricalOrder("receipt-retry", fulfilled: false);
                db.TenantBotOrders.Add(order);
                await db.SaveChangesAsync();
                orderId = order.Id;
            }

            var persist = typeof(TenantBotService).GetMethod(
                "PERSISTTENANTMANUALRECEIPTASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<int> SaveAsync(string photo)
            {
                await using var scope = provider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
                return await (Task<int>)persist.Invoke(service, new object[] { orderId, photo, CancellationToken.None })!;
            }

            var firstId = await SaveAsync("photo-1");
            await using (var db = databases.Users.CreateDbContext())
            {
                var first = await db.TenantManualPaymentReceipts.SingleAsync(x => x.Id == firstId);
                first.Status = TenantManualPaymentReceiptStatuses.Rejected;
                first.RejectedAtUtc = DateTime.UtcNow;
                var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
                order.PaymentStatus = TenantBotOrderStatuses.ReceiptRejected;
                await db.SaveChangesAsync();
            }

            var secondId = await SaveAsync("photo-2");
            Assert.NotEqual(firstId, secondId);
            await using var verify = databases.Users.CreateDbContext();
            Assert.Equal(TenantManualPaymentReceiptStatuses.Rejected,
                (await verify.TenantManualPaymentReceipts.SingleAsync(x => x.Id == firstId)).Status);
            Assert.Equal(TenantManualPaymentReceiptStatuses.Pending,
                (await verify.TenantManualPaymentReceipts.SingleAsync(x => x.Id == secondId)).Status);
            Assert.Equal(2, await verify.TenantManualReceiptNotifications.CountAsync());
        }
    }

    private static TenantBotOrder HistoricalOrder(string orderId, bool fulfilled)
    {
        return new TenantBotOrder
        {
            OrderId = orderId,
            TenantBotId = "tenant-reset",
            TenantBotUsername = "reset_store",
            OwnerTelegramUserId = 711,
            CustomerTelegramUserId = 7468859738,
            CustomerChatId = 7468859738,
            SalePriceToman = 150_000,
            BaseCostToman = 100_000,
            ProfitToman = 50_000,
            PaymentProvider = "tenant_card",
            PaymentStatus = fulfilled ? TenantBotOrderStatuses.Fulfilled : TenantBotOrderStatuses.ReceiptSubmitted,
            IsFulfilled = fulfilled,
            CreatedAccountEmail = fulfilled ? "stored@example.test" : null,
            CreatedSubLink = fulfilled ? "https://example.invalid/sub/stored" : null,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static TenantManualPaymentReceipt ReceiptFor(TenantBotOrder order)
    {
        return new TenantManualPaymentReceipt
        {
            TenantBotOrderId = order.Id, OrderId = order.OrderId, TenantBotId = order.TenantBotId,
            TenantBotUsername = order.TenantBotUsername, OwnerTelegramUserId = order.OwnerTelegramUserId,
            CustomerTelegramUserId = order.CustomerTelegramUserId, CustomerChatId = order.CustomerChatId,
            PhotoFileId = "historical-photo", AmountToman = order.SalePriceToman,
            Status = TenantManualPaymentReceiptStatuses.Pending, CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static IConfiguration IncidentConfiguration()
    {
        return new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:id"] = "main",
                ["bots:0:username"] = "main_bot",
                ["bots:0:token"] = Token(10001),
                ["bots:0:enabled"] = "true",
                ["bots:0:isDefault"] = "true",
                ["salesAssistantBot:id"] = "assistant",
                ["salesAssistantBot:username"] = "assistant_bot",
                ["salesAssistantBot:token"] = Token(10002),
                ["salesAssistantBot:enabled"] = "true",
                ["GozargahSiteSyncEnabled"] = "false",
                ["GozargahSiteWalletPaymentsEnabled"] = "false"
            }).Build();
    }

    /// <summary>
    /// Builds a full production-shaped service provider for incident regression tests on top of a temporary
    /// users.db/credentials.db fixture directory.
    /// </summary>
    /// <param name="databases">Temporary database fixture that owns the users.db and credentials.db files used by the provider.</param>
    /// <param name="fundingMonitorEnabled">
    /// Optional override for the tenant storefront funding monitor flag. When null the incident configuration default is used.
    /// </param>
    /// <param name="interactionTimeouts">
    /// Optional immutable override for the UX-only Telegram latency budgets. When null production defaults apply, so
    /// callback acknowledgement is bounded at two seconds and mandatory-join verification at one overall five-second
    /// budget. Tests that assert bounded-timeout behaviour pass millisecond values so they do not wait the real
    /// production budgets. This override cannot change purchase, wallet, or settlement semantics.
    /// </param>
    /// <returns>
    /// A provider exposing the registered production services, the configured <see cref="BotRegistry"/>, and the
    /// per-bot fake Telegram clients keyed by bot id. The caller owns and must dispose the provider.
    /// </returns>
    private static (ServiceProvider Provider, BotRegistry Registry, ConcurrentDictionary<string, StorefrontClient> Clients)
        IncidentProvider(
            Databases databases,
            bool? fundingMonitorEnabled = null,
            TelegramInteractionTimeouts? interactionTimeouts = null)
    {
        IConfiguration configuration = IncidentConfiguration();
        if (fundingMonitorEnabled.HasValue)
        {
            configuration = new ConfigurationBuilder().AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tenantStorefrontFundingMonitorEnabled"] = fundingMonitorEnabled.Value.ToString(),
                    ["tenantStorefrontFundingMonitorIntervalMinutes"] = fundingMonitorEnabled.Value ? "5" : "0"
                }).Build();
        }
        var appConfig = configuration.Get<AppConfig>()!;
        appConfig.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db");
        appConfig.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection();
        Program.RegisterApplicationServices(services, configuration, appConfig, databases.DirectoryPath);

        // Registered after production so the last registration wins for a single-service resolve. Tests use this
        // to shrink the UX-only Telegram budgets to milliseconds without touching production latency policy.
        if (interactionTimeouts != null)
            services.AddSingleton(interactionTimeouts);

        var registry = new BotRegistry(configuration);
        var clients = new ConcurrentDictionary<string, StorefrontClient>(StringComparer.OrdinalIgnoreCase);
        var botClientProvider = new BotClientProvider(
            registry,
            bot => clients.GetOrAdd(bot.Id, _ => new StorefrontClient()));
        services.AddSingleton(registry);
        services.AddSingleton(botClientProvider);
        return (services.BuildServiceProvider(), registry, clients);
    }

    private static async Task<bool> IsInboxCompletedAsync(Databases databases, int updateId)
    {
        await using var db = databases.Users.CreateDbContext();
        var row = await db.TelegramUpdateInbox.AsNoTracking().SingleOrDefaultAsync(x => x.UpdateId == updateId);
        return row?.Status == "completed";
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate())
            await Task.Delay(10, deadline.Token);
    }

    private static string Token(int botId) => botId + ":" + new string('a', 35);

    private sealed class BlockingReceiptSender : ITenantManualReceiptNotificationSender
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();

        public async Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return 3131;
        }
    }

    private sealed class CountingReceiptSender : ITenantManualReceiptNotificationSender
    {
        private int _successCount;
        public int SuccessCount => Volatile.Read(ref _successCount);

        public async Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _successCount);
            await Task.Delay(75, cancellationToken);
            return 4242;
        }
    }
}

