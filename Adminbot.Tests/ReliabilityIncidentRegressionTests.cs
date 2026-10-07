using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Xunit;

/// <summary>Protects exact storefront transports and durable historical business state across runtime changes.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Allows pre-activation probes without unlocking ordinary or historical delivery for a disabled tenant.</summary>
    /// <remarks>Regression: caching a probe client must not bypass the enabled-state guard or substitute the owned bot.</remarks>
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

        provider.GetDefaultClient();
        provider.GetClient("TENANT-GOOD");
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("missing"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("tenant-disabled"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("tenant-tokenless"));
        provider.GetClientForCapabilityProbe("tenant-disabled");
        Assert.False(registry.GetById("tenant-disabled").Enabled);
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("tenant-disabled"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClient("tenant-disabled", 20002));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClientForCapabilityProbe("missing"));
        Assert.Throws<BotTransportUnavailableException>(() => provider.GetClientForCapabilityProbe("tenant-tokenless"));
        Assert.Throws<ArgumentException>(() => provider.GetClientForCapabilityProbe(""));
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

    /// <summary>An idle receipt scan reads terminal, future and unexpired rows without contending with a real WAL writer.</summary>
    /// <returns>A task verifying zero sends and zero claimed rows while another connection owns the writer lock.</returns>
    [Fact]
    public async Task Idle_receipt_scan_completes_while_sqlite_writer_is_held()
    {
        using var databases = new Databases();
        await SeedReceiptAcknowledgementAsync(databases);
        await using (var db = databases.Users.CreateDbContext())
        {
            await db.TenantManualReceiptNotifications.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantManualReceiptNotificationStatuses.Delivered));
            db.TenantManualReceiptNotifications.AddRange(
                new TenantManualReceiptNotification
                {
                    ReceiptId = 91002, Status = TenantManualReceiptNotificationStatuses.Pending,
                    NextAttemptAtUtc = DateTime.UtcNow.AddHours(1)
                },
                new TenantManualReceiptNotification
                {
                    ReceiptId = 91003, Status = TenantManualReceiptNotificationStatuses.Processing,
                    ClaimToken = "unexpired-claim", LeaseUntilUtc = DateTime.UtcNow.AddHours(1)
                });
            await db.SaveChangesAsync();
        }

        var factory = ReceiptContentionFactory(databases);
        var sender = new CountingReceiptSender();
        using var worker = new TenantManualReceiptNotificationWorker(
            factory, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        await using var writer = new SqliteConnection(ReceiptConnectionString(databases));
        await writer.OpenAsync();
        using var transaction = writer.BeginTransaction(deferred: false);
        Assert.Equal(0, await Task.Run(() => worker.ProcessOnceAsync()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, sender.SuccessCount);
        transaction.Rollback();
    }

    /// <summary>A real accepted receipt never returns to pending when SQLite blocks acknowledgement, even across lease expiry and restart.</summary>
    /// <param name="releaseBeforeQuarantine">Whether the writer is released after the actual BUSY failure or remains held through quarantine.</param>
    /// <returns>A task verifying the sender is invoked exactly once and persistence remains non-retryable.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Receipt_acknowledgement_busy_never_authorizes_another_send(bool releaseBeforeQuarantine)
    {
        using var databases = new Databases();
        await SeedReceiptAcknowledgementAsync(databases);
        var busy = new ReceiptAcknowledgementBusyBarrier();
        var factory = ReceiptContentionFactory(databases, busy);
        var sender = new BlockingReceiptSender();
        using var worker = new TenantManualReceiptNotificationWorker(
            factory, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        var scan = Task.Run(() => worker.ProcessOnceAsync());
        try
        {
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var writer = new SqliteConnection(ReceiptConnectionString(databases));
            await writer.OpenAsync();
            using var transaction = writer.BeginTransaction(deferred: false);
            sender.Release.TrySetResult();
            await busy.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (releaseBeforeQuarantine)
                transaction.Commit();
            busy.Continue.TrySetResult();
            Assert.Equal(1, await scan.WaitAsync(TimeSpan.FromSeconds(10)));
            if (!releaseBeforeQuarantine)
                transaction.Rollback();
        }
        finally
        {
            sender.Release.TrySetResult();
            busy.Continue.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantManualReceiptNotifications.SingleAsync();
            Assert.Equal(releaseBeforeQuarantine
                ? TenantManualReceiptNotificationStatuses.DeliveryUncertain
                : TenantManualReceiptNotificationStatuses.Processing, row.Status);
            Assert.Null(row.NextAttemptAtUtc);
            if (!releaseBeforeQuarantine)
            {
                Assert.NotNull(row.ClaimToken);
                Assert.NotNull(row.LeaseUntilUtc);
                await db.TenantManualReceiptNotifications.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
            }
        }

        using var restarted = new TenantManualReceiptNotificationWorker(
            factory, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(1, sender.SuccessCount);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(TenantManualReceiptNotificationStatuses.DeliveryUncertain,
            (await verify.TenantManualReceiptNotifications.SingleAsync()).Status);
    }

    /// <summary>A provider failure after the real acknowledgement UPDATE cannot downgrade its already committed delivered row.</summary>
    /// <returns>A task verifying the applied acknowledgement survives quarantine and restart without a second send.</returns>
    [Fact]
    public async Task Applied_receipt_acknowledgement_failure_preserves_delivered_status()
    {
        using var databases = new Databases();
        await SeedReceiptAcknowledgementAsync(databases);
        var cleanupFailure = new ReceiptAppliedAcknowledgementFailure();
        var factory = ReceiptContentionFactory(databases, cleanupFailure);
        var sender = new CountingReceiptSender();
        using var worker = new TenantManualReceiptNotificationWorker(
            factory, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, cleanupFailure.Failures);
        using var restarted = new TenantManualReceiptNotificationWorker(
            factory, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(1, sender.SuccessCount);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantManualReceiptNotifications.SingleAsync();
        Assert.Equal(TenantManualReceiptNotificationStatuses.Delivered, row.Status);
        Assert.Equal(4242, row.TelegramMessageId);
        Assert.NotNull(row.DeliveredAtUtc);
        Assert.Null(row.LastError);
    }

    /// <summary>An accepted receipt cannot quarantine a processing claim that no longer belongs to its send attempt.</summary>
    /// <returns>A task verifying an acknowledgement mismatch preserves the replacement token and never releases pending.</returns>
    [Fact]
    public async Task Receipt_acknowledgement_mismatch_preserves_another_claim()
    {
        using var databases = new Databases();
        await SeedReceiptAcknowledgementAsync(databases);
        var sender = new BlockingReceiptSender();
        using var worker = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        var scan = worker.ProcessOnceAsync();
        try
        {
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var db = databases.Users.CreateDbContext();
            await db.TenantManualReceiptNotifications.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ClaimToken, "replacement-claim")
                .SetProperty(x => x.LastError, "replacement_claim_owned_elsewhere")
                .SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow.AddHours(1)));
            sender.Release.TrySetResult();
            Assert.Equal(1, await scan.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            sender.Release.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using var restarted = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(1, sender.SuccessCount);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantManualReceiptNotifications.SingleAsync();
        Assert.Equal(TenantManualReceiptNotificationStatuses.Processing, row.Status);
        Assert.Equal("replacement-claim", row.ClaimToken);
        Assert.Equal("replacement_claim_owned_elsewhere", row.LastError);
    }

    /// <summary>Shutdown after the sender returns an accepted message does not release the receipt for another attempt.</summary>
    /// <returns>A task verifying post-acceptance cancellation is quarantined independently of the host token.</returns>
    [Fact]
    public async Task Receipt_cancellation_after_acceptance_does_not_authorize_retry()
    {
        using var databases = new Databases();
        await SeedReceiptAcknowledgementAsync(databases);
        using var cancellation = new CancellationTokenSource();
        var sender = new AcceptedThenCancelledReceiptSender(cancellation);
        using var worker = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync(cancellation.Token));
        using var restarted = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(1, sender.SuccessCount);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(TenantManualReceiptNotificationStatuses.DeliveryUncertain,
            (await verify.TenantManualReceiptNotifications.SingleAsync()).Status);
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
    /// <param name="extraConfiguration">
    /// Optional additional configuration keys layered above the incident configuration, for example enabling the
    /// global latest-client download switch for one test. When null the incident configuration is used unchanged.
    /// These keys change feature availability only; they never alter purchase, wallet, or settlement semantics.
    /// </param>
    /// <returns>
    /// A provider exposing the registered production services, the configured <see cref="BotRegistry"/>, and the
    /// per-bot fake Telegram clients keyed by bot id. The caller owns and must dispose the provider.
    /// </returns>
    private static (ServiceProvider Provider, BotRegistry Registry, ConcurrentDictionary<string, StorefrontClient> Clients)
        IncidentProvider(
            Databases databases,
            bool? fundingMonitorEnabled = null,
            TelegramInteractionTimeouts? interactionTimeouts = null,
            IReadOnlyDictionary<string, string?>? extraConfiguration = null)
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
        if (extraConfiguration != null)
        {
            configuration = new ConfigurationBuilder().AddConfiguration(configuration)
                .AddInMemoryCollection(extraConfiguration).Build();
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

    /// <summary>Persists a receipt and its real pending notification in the fixture's isolated WAL database.</summary>
    /// <param name="databases">Fixture owning the database files.</param>
    /// <returns>A task that completes after both rows are durable.</returns>
    /// <remarks>Creates only isolated test receipt/outbox records; no wallet, provider, panel or Telegram side effect occurs.</remarks>
    private static async Task SeedReceiptAcknowledgementAsync(Databases databases)
    {
        await using var db = databases.Users.CreateDbContext();
        var receipt = new TenantManualPaymentReceipt
        {
            TenantBotOrderId = 91001, OrderId = "acknowledgement-order", TenantBotId = "tenant-reset",
            OwnerTelegramUserId = 711, CustomerTelegramUserId = 7468859738,
            CustomerChatId = 7468859738, PhotoFileId = "accepted-receipt", AmountToman = 1000
        };
        db.TenantManualPaymentReceipts.Add(receipt);
        await db.SaveChangesAsync();
        db.TenantManualReceiptNotifications.Add(new TenantManualReceiptNotification { ReceiptId = receipt.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>Uses the existing WAL fixture with a one-second native SQLite timeout and no shared pool.</summary>
    /// <param name="databases">Fixture whose users.db connection string is reused.</param>
    /// <returns>A connection string scoped exclusively to the fixture's temporary file.</returns>
    private static string ReceiptConnectionString(Databases databases)
    {
        using var db = databases.Users.CreateDbContext();
        return new SqliteConnectionStringBuilder(db.Database.GetConnectionString())
        {
            DefaultTimeout = 1, Pooling = false
        }.ToString();
    }

    /// <summary>Creates independent production contexts with a bounded native lock wait and optional provider-boundary interception.</summary>
    /// <param name="databases">Fixture containing the already-created WAL schema.</param>
    /// <param name="interceptors">Interceptors observing or faulting actual executed commands, never replacing their result.</param>
    /// <returns>A factory using the real Microsoft SQLite provider against the isolated file.</returns>
    private static UserDbContextFactory ReceiptContentionFactory(Databases databases, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(ReceiptConnectionString(databases), sqlite => sqlite.CommandTimeout(1))
            .AddInterceptors(interceptors).Options);

    /// <summary>Identifies the acknowledgement command by its real table and delivered-timestamp assignment.</summary>
    /// <param name="command">SQL command emitted by the receipt worker.</param>
    /// <returns>True only for the receipt acknowledgement UPDATE, excluding claims and quarantine.</returns>
    private static bool IsReceiptAcknowledgement(DbCommand command) =>
        command.CommandText.StartsWith("UPDATE \"TenantManualReceiptNotifications\"", StringComparison.Ordinal) &&
        command.CommandText.Contains("\"DeliveredAtUtc\"", StringComparison.Ordinal);

    /// <summary>Pauses propagation of an actual native BUSY acknowledgement so the test controls quarantine lock availability.</summary>
    private sealed class ReceiptAcknowledgementBusyBarrier : DbCommandInterceptor
    {
        /// <summary>Completes only after the real provider reports SQLITE_BUSY for the acknowledgement.</summary>
        public TaskCompletionSource Observed { get; } = Signal();
        /// <summary>Allows the failed command to unwind after the test chooses whether to release its writer.</summary>
        public TaskCompletionSource Continue { get; } = Signal();

        /// <summary>Observes native acknowledgement failure without suppressing or replacing it.</summary>
        /// <param name="command">Real failed SQL command.</param>
        /// <param name="eventData">Provider exception and execution metadata.</param>
        /// <param name="cancellationToken">Original command token.</param>
        /// <returns>A task completing once the test permits failure propagation.</returns>
        public override async Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (IsReceiptAcknowledgement(command) &&
                eventData.Exception is SqliteException { SqliteErrorCode: 5 })
            {
                Observed.TrySetResult();
                await Continue.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await base.CommandFailedAsync(command, eventData, cancellationToken);
        }
    }

    /// <summary>Raises a deterministic provider-boundary failure only after the real acknowledgement UPDATE has applied.</summary>
    private sealed class ReceiptAppliedAcknowledgementFailure : DbCommandInterceptor
    {
        private int _failures;
        /// <summary>Number of successfully applied acknowledgements followed by an injected failure.</summary>
        public int Failures => Volatile.Read(ref _failures);

        /// <summary>Models an applied-command cleanup ambiguity while preserving the actual SQLite mutation.</summary>
        /// <param name="command">Command that has already completed against SQLite.</param>
        /// <param name="eventData">Real command execution metadata.</param>
        /// <param name="result">Affected-row count from the real database.</param>
        /// <param name="cancellationToken">Original command token.</param>
        /// <returns>The unchanged result for all commands other than the first applied acknowledgement.</returns>
        /// <exception cref="SqliteException">The acknowledgement applied, but its provider completion is ambiguous to the caller.</exception>
        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (result == 1 && IsReceiptAcknowledgement(command) &&
                Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
                throw new SqliteException("Injected failure after receipt acknowledgement applied.", 5);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Returns one accepted message while requesting host shutdown before acknowledgement persistence.</summary>
    /// <param name="cancellation">Test-owned host lifetime source cancelled immediately after the local sender accepts the receipt.</param>
    private sealed class AcceptedThenCancelledReceiptSender(CancellationTokenSource cancellation) : ITenantManualReceiptNotificationSender
    {
        private int _successCount;
        /// <summary>Number of accepted receipt sends.</summary>
        public int SuccessCount => Volatile.Read(ref _successCount);

        /// <summary>Accepts the receipt, then cancels the host without throwing a transport failure.</summary>
        /// <param name="receipt">Real receipt loaded by the worker.</param>
        /// <param name="cancellationToken">Host token cancelled immediately after acceptance.</param>
        /// <returns>The acknowledged message id.</returns>
        public Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _successCount);
            cancellation.Cancel();
            return Task.FromResult<int?>(5151);
        }
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

    /// <summary>Waits at the external-send boundary so a test can acquire a writer after claim persistence.</summary>
    private sealed class BlockingReceiptSender : ITenantManualReceiptNotificationSender
    {
        private int _successCount;
        /// <summary>Number of receipt messages accepted after the send barrier.</summary>
        public int SuccessCount => Volatile.Read(ref _successCount);
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();

        /// <summary>Accepts a receipt only after the test releases its send barrier.</summary>
        /// <param name="receipt">Real receipt loaded by the worker.</param>
        /// <param name="cancellationToken">Token for the pre-acceptance wait.</param>
        /// <returns>The accepted message id.</returns>
        public async Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _successCount);
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

