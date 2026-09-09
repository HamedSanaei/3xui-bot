using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task Historical_assistant_final_queues_only_owner_details_idempotently()
    {
        using var databases = new Databases();
        var (provider, _, clients) = IncidentProvider(databases);
        await using (provider)
        {
            int receiptId;
            int orderId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = HistoricalOrder("historical-final-outbox", fulfilled: true);
                db.TenantBotOrders.Add(order);
                await db.SaveChangesAsync();
                orderId = order.Id;
                var receipt = ReceiptFor(order);
                db.TenantManualPaymentReceipts.Add(receipt);
                await db.SaveChangesAsync();
                receiptId = receipt.Id;
            }

            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var first = await service.APPROVEMANUALRECEIPTASYNC(receiptId, 711, default);
            var second = await service.APPROVEMANUALRECEIPTASYNC(receiptId, 711, default);
            Assert.NotEmpty(first);
            Assert.NotEmpty(second);
            Assert.False(clients.ContainsKey("assistant"));

            await using var verify = databases.Users.CreateDbContext();
            var rows = await verify.TenantOrderNotifications.Where(x => x.TenantBotOrderId == orderId).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(TenantOrderNotificationKinds.OwnerAccountDetailsAfterAssistantFinal, row.Kind);
            Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
            Assert.Empty(await verify.TenantBotLedgerEntries.Where(x => x.TenantBotOrderId == orderId).ToListAsync());
            Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
            Assert.Empty(await verify.Set<SiteWalletDebitOperation>().ToListAsync());
            Assert.Empty(await verify.GozargahSiteSyncEvents.ToListAsync());
            await using var credentials = databases.Credentials.CreateDbContext();
            Assert.Empty(await credentials.WalletOperations.ToListAsync());
        }
    }

    [Theory]
    [InlineData(TenantOrderNotificationKinds.CustomerAccountDelivery)]
    [InlineData(TenantOrderNotificationKinds.OwnerSaleNotification)]
    [InlineData(TenantOrderNotificationKinds.SalesAssistantSaleNotification)]
    [InlineData(TenantOrderNotificationKinds.OwnerAccountDetailsAfterAssistantFinal)]
    public async Task Blocked_post_fulfillment_delivery_does_not_block_assistant_final_lane(string kind)
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            var (orderId, receiptId) = await SeedFulfilledOrderWithNotificationAsync(databases, "blocked-" + kind, kind);
            var sender = new BlockingOrderNotificationSender();
            var worker = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
            var delivery = worker.ProcessOnceAsync();
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var callback = service.APPROVEMANUALRECEIPTASYNC(receiptId, 711, default);
            var result = await callback.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEmpty(result);
            Assert.False(delivery.IsCompleted);

            sender.Release.TrySetResult();
            Assert.Equal(1, await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
            await using var verify = databases.Users.CreateDbContext();
            Assert.Equal(TenantOrderNotificationStatuses.Delivered,
                (await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId && x.Kind == kind)).Status);
            Assert.Equal(1, await verify.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId && x.Kind == kind));
            Assert.Equal(1, await verify.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId &&
                x.Kind == TenantOrderNotificationKinds.OwnerAccountDetailsAfterAssistantFinal));
        }
    }

    [Fact]
    public async Task Fulfilled_order_ledger_and_required_notification_intents_commit_atomically()
    {
        using var databases = new Databases();
        int orderId;
        await using (var seed = databases.Users.CreateDbContext())
        {
            var order = HistoricalOrder("atomic-intents", fulfilled: false);
            seed.TenantBotOrders.Add(order);
            await seed.SaveChangesAsync();
            orderId = order.Id;
        }

        var workflow = new UserWorkflowStore(databases.Users);
        var target = await workflow.ReadAsync(db => db.TenantBotOrders.SingleAsync(x => x.Id == orderId));
        target.IsFulfilled = true;
        target.PaymentStatus = TenantBotOrderStatuses.Fulfilled;
        workflow.Add(new TenantBotLedgerEntry { TenantBotId = target.TenantBotId, TenantBotUsername = target.TenantBotUsername, TenantBotOrderId = target.Id, OrderId = target.OrderId, OwnerTelegramUserId = target.OwnerTelegramUserId, CustomerTelegramUserId = target.CustomerTelegramUserId, Description = "atomic-test" });
        foreach (var kind in new[] { TenantOrderNotificationKinds.CustomerAccountDelivery, TenantOrderNotificationKinds.OwnerSaleNotification, TenantOrderNotificationKinds.SalesAssistantSaleNotification, TenantOrderNotificationKinds.OwnerAccountDetailsAfterAssistantFinal })
            workflow.Add(new TenantOrderNotification { TenantBotOrderId = target.Id, Kind = kind });
        Assert.Equal(6, await workflow.SaveAsync());

        await using var verify = databases.Users.CreateDbContext();
        Assert.True((await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId)).IsFulfilled);
        Assert.Equal(1, await verify.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
        Assert.Equal(4, await verify.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId));
    }

    [Fact]
    public async Task Fulfillment_atomic_write_rolls_back_order_and_ledger_when_required_intent_insert_fails()
    {
        using var databases = new Databases();
        int orderId;
        await using (var seed = databases.Users.CreateDbContext())
        {
            var order = HistoricalOrder("atomic-rollback", fulfilled: false);
            seed.TenantBotOrders.Add(order);
            await seed.SaveChangesAsync();
            orderId = order.Id;
        }
        var workflow = new UserWorkflowStore(databases.Users);
        var target = await workflow.ReadAsync(db => db.TenantBotOrders.SingleAsync(x => x.Id == orderId));
        target.IsFulfilled = true;
        target.PaymentStatus = TenantBotOrderStatuses.Fulfilled;
        workflow.Add(new TenantBotLedgerEntry { TenantBotId = target.TenantBotId, TenantBotUsername = target.TenantBotUsername, TenantBotOrderId = target.Id, OrderId = target.OrderId, OwnerTelegramUserId = target.OwnerTelegramUserId, CustomerTelegramUserId = target.CustomerTelegramUserId, Description = "rollback-test" });
        workflow.Add(new TenantOrderNotification { TenantBotOrderId = target.Id, Kind = TenantOrderNotificationKinds.CustomerAccountDelivery });
        workflow.Add(new TenantOrderNotification { TenantBotOrderId = target.Id, Kind = TenantOrderNotificationKinds.CustomerAccountDelivery });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => workflow.SaveAsync());

        await using var verify = databases.Users.CreateDbContext();
        var persisted = await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId);
        Assert.False(persisted.IsFulfilled);
        Assert.NotEqual(TenantBotOrderStatuses.Fulfilled, persisted.PaymentStatus);
        Assert.Empty(await verify.TenantBotLedgerEntries.Where(x => x.TenantBotOrderId == orderId).ToListAsync());
        Assert.Empty(await verify.TenantOrderNotifications.Where(x => x.TenantBotOrderId == orderId).ToListAsync());
    }

    [Fact]
    public async Task Delivered_notification_survives_restart_and_is_not_sent_twice()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(
            databases, "notification-restart", TenantOrderNotificationKinds.CustomerAccountDelivery);
        var sender = new CountingOrderNotificationSender();
        var first = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await first.ProcessOnceAsync());
        var restarted = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, row.Status);
        Assert.Equal(7007, row.TelegramMessageId);
    }

    [Fact]
    public async Task Ambiguous_send_becomes_uncertain_and_never_replays_after_restart()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(
            databases, "notification-uncertain", TenantOrderNotificationKinds.CustomerAccountDelivery, addLedger: true);
        var first = new TenantOrderNotificationWorker(
            databases.Users, new ThrowingOrderNotificationSender(new TimeoutException("synthetic timeout")),
            NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await first.ProcessOnceAsync());

        var success = new CountingOrderNotificationSender();
        var restarted = new TenantOrderNotificationWorker(databases.Users, success, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        Assert.Equal(0, success.Count);

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        var order = await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.DeliveryUncertain, row.Status);
        Assert.True(order.IsFulfilled);
        Assert.Equal(TenantBotOrderStatuses.Fulfilled, order.PaymentStatus);
        Assert.Equal(1, await verify.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
    }

    [Fact]
    public async Task Definite_pre_send_transport_failure_is_bounded_retryable()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(
            databases, "notification-retry", TenantOrderNotificationKinds.CustomerAccountDelivery);
        var first = new TenantOrderNotificationWorker(
            databases.Users, new ThrowingOrderNotificationSender(new BotTransportUnavailableException("bot_not_found")),
            NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await first.ProcessOnceAsync());

        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
            Assert.NotNull(row.NextAttemptAtUtc);
            Assert.True(row.NextAttemptAtUtc > DateTime.UtcNow);
        }

        var success = new CountingOrderNotificationSender();
        var restarted = new TenantOrderNotificationWorker(databases.Users, success, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(0, await restarted.ProcessOnceAsync());
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await restarted.ProcessOnceAsync());
        Assert.Equal(1, success.Count);
        await using var verify = databases.Users.CreateDbContext();
        var delivered = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, delivered.Status);
        Assert.Equal(2, delivered.AttemptCount);
    }

    [Fact]
    public async Task Concurrent_workers_atomically_claim_one_pending_notification()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(databases, "claim-race", TenantOrderNotificationKinds.OwnerSaleNotification);
        var sender = new CountingOrderNotificationSender();
        var first = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        var second = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        var processed = await Task.WhenAll(first.ProcessOnceAsync(), second.ProcessOnceAsync());
        Assert.Equal(1, processed.Sum());
        Assert.Equal(1, sender.Count);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, row.Status);
        Assert.Equal(1, row.AttemptCount);
    }

    [Fact]
    public async Task Live_lease_is_not_stolen_and_expired_claimed_row_is_retryable()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(databases, "lease-guard", TenantOrderNotificationKinds.OwnerSaleNotification);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            row.Status = TenantOrderNotificationStatuses.Processing;
            row.ClaimToken = "live-claim";
            row.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
            row.AttemptCount = 1;
            await db.SaveChangesAsync();
        }
        var sender = new CountingOrderNotificationSender();
        var worker = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);

        // The claim expired before the durable send phase was persisted: no Telegram request could have been
        // sent, so the same scan recycles the row to Pending and redelivers it exactly once instead of parking
        // it as DeliveryUncertain.
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            row.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);
        await using var verify = databases.Users.CreateDbContext();
        var recycled = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, recycled.Status);
        Assert.Null(recycled.SendStartedAtUtc);
        Assert.Equal(2, recycled.AttemptCount);
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, recycled.Status);
        Assert.NotEqual(TenantOrderNotificationStatuses.DeliveryUncertain, recycled.Status);
    }

    [Fact]
    public async Task Retry_attempt_budget_stops_at_manual_review()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(databases, "retry-budget", TenantOrderNotificationKinds.OwnerSaleNotification);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            row.AttemptCount = 5;
            await db.SaveChangesAsync();
        }
        var worker = new TenantOrderNotificationWorker(databases.Users, new ThrowingOrderNotificationSender(new BotTransportUnavailableException("bot_not_found")), NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(0, await worker.ProcessOnceAsync());
        await using var verify = databases.Users.CreateDbContext();
        var finalRow = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(6, finalRow.AttemptCount);
        Assert.Equal(TenantOrderNotificationStatuses.ManualReview, finalRow.Status);
        Assert.Equal("bot_transport_unavailable", finalRow.LastError);
        Assert.True(finalRow.LastError.Length <= 256);
    }

    [Fact]
    public async Task Reset_tenant_transport_never_falls_back_to_default_bot()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance { Id = "tenant-reset", Type = BotInstanceTypes.Tenant, Enabled = false, Token = null });
            var order = HistoricalOrder("reset-transport-outbox", fulfilled: true);
            await using var scope = provider.CreateAsyncScope();
            var delivery = scope.ServiceProvider.GetRequiredService<TenantOrderNotificationDeliveryService>();
            Assert.Null(await delivery.SendAsync(order, TenantOrderNotificationKinds.CustomerAccountDelivery, default));
            Assert.False(clients.ContainsKey("tenant-reset"));
            Assert.False(clients.ContainsKey("main"));
        }
    }

    [Fact]
    public async Task Owner_sale_delivery_uses_token_valid_default_bot_even_when_receiver_is_disabled()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            var main = registry.DefaultBot;
            main.Enabled = false;
            var order = HistoricalOrder("owner-default-send", fulfilled: true);
            await using var scope = provider.CreateAsyncScope();
            var delivery = scope.ServiceProvider.GetRequiredService<TenantOrderNotificationDeliveryService>();
            Assert.NotNull(await delivery.SendAsync(order, TenantOrderNotificationKinds.OwnerSaleNotification, default));
            Assert.True(clients.ContainsKey("main"));
        }
    }

    [Fact]
    public void Telegram_edit_policy_distinguishes_noop_missing_target_and_other_errors()
    {
        Assert.True(TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(
            400, "Bad Request: message is not modified: specified new message content is the same"));
        Assert.False(TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(
            400, "Bad Request: message to edit not found"));
        Assert.True(TenantBotService.ISTELEGRAMEDITTARGETMISSING(
            400, "Bad Request: message to edit not found"));
        Assert.False(TenantBotService.ISTELEGRAMEDITTARGETMISSING(
            400, "Bad Request: chat not found"));
        Assert.False(TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(
            500, "message is not modified"));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram message edit target was not found; edit was swallowed to keep the receiver alive.", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram Message edit failed but was SWALLOWED to Keep the receiver ALIVE.", null));
    }

    [Fact]
    public void Gozargah_success_noise_is_suppressed_without_hiding_financial_or_xui_failures()
    {
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "Gozargah site wallet debit response received. telegramUserId=1 amountToman=100", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Gozargah site wallet debit failed", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Gozargah site wallet insufficient wallet", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Gozargah site wallet idempotency conflict", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("SiteWalletDebitUncertainException reconciliation required", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Gozargah site wallet timeout network failure", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Tenant XUI creation failed", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Tenant bot token validation failed", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Payment settlement failed", null));
    }

    [Fact]
    public void Tenant_timing_reports_core_boundary_not_background_delivery_latency()
    {
        var html = TenantBotService.BUILDTENANTFULFILLMENTTIMINGHTML(
            new XuiOperationTimingSnapshot(TimeSpan.FromMilliseconds(4142), TimeSpan.FromMilliseconds(6111)),
            TimeSpan.FromMilliseconds(18));
        Assert.Contains("00:04.142", html);
        Assert.Contains("00:06.111", html);
        Assert.Contains("00:00.018", html);
        Assert.DoesNotContain("01:50", html);
    }

    [Fact]
    public void Gozargah_retry_wake_is_idempotent_under_concurrency()
    {
        Parallel.For(0, 1000, _ => GozargahSiteSyncRetryService.Wake());
    }

    [Fact]
    public void Deferred_gozargah_semantics_ignore_website_owned_username_enrichment()
    {
        var previous = new GozargahSiteSyncEvent
        {
            BotId = "main", TenantBotId = "tenant-1", TelegramUserId = 711, BuyerTelegramUserId = 722,
            Email = "same@example.test", Uuid = "uuid-1", SubId = "sub-1", SubLink = "https://example.invalid/sub/1",
            Operation = GozargahSiteSyncOperations.Create,
            RequestJson = "{\"name\":\"same@example.test\",\"uuid\":\"uuid-1\",\"username\":\"old-owner\",\"tracking_code\":\"old\"}"
        };
        var desired = new GozargahSiteSyncEvent
        {
            BotId = previous.BotId, TenantBotId = previous.TenantBotId, TelegramUserId = previous.TelegramUserId, BuyerTelegramUserId = previous.BuyerTelegramUserId,
            Email = previous.Email, Uuid = previous.Uuid, SubId = previous.SubId, SubLink = previous.SubLink,
            Operation = previous.Operation,
            RequestJson = "{\"name\":\"same@example.test\",\"uuid\":\"uuid-1\",\"username\":null,\"tracking_code\":\"new\"}"
        };
        Assert.True(GozargahSyncSemantics.Equivalent(previous, desired));
    }

    [Fact]
    public async Task Deferred_gozargah_account_sync_persists_without_waiting_for_remote_http_and_deduplicates()
    {
        using var databases = new Databases();
        var getUserEntered = Signal(); var release = Signal(); var getUserCalls = 0; var createCalls = 0;
        string? createUsername = null;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var action = body.RootElement.GetProperty("action").GetString();
            if (action == "get_user")
            {
                Interlocked.Increment(ref getUserCalls); getUserEntered.TrySetResult(); await release.Task;
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", ban = 0, wallet = 500000 } });
                return;
            }
            createUsername = body.RootElement.TryGetProperty("username", out var username) ? username.GetString() : null;
            Interlocked.Increment(ref createCalls);
            await context.Response.WriteAsJsonAsync(new { success = true, id = 77 });
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteRealtimeCreateSyncEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = app.Urls.Single(), ["gozargahSiteApiKey"] = "test-only"
        }).Build();

        var sync = new GozargahSiteSyncService(databases.Users, new CredentialsStore(databases.Credentials),
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration, NullLogger<GozargahSiteSyncService>.Instance);
        var created = new XuiV3AccountCreationResult
        {
            Success = true, Email = "deferred@example.test", Uuid = Guid.NewGuid().ToString(),
            SubId = "sub-test", SubLink = "https://example.invalid/sub/test", TrafficGb = 10, DurationDays = 30
        };

        var first = await sync.QueueCreateAsync(711, 722, created, "tenant-order-deferred", "tenant-1", default, deferSend: true);
        var duplicate = await sync.QueueCreateAsync(711, 722, created, "tenant-order-deferred", "tenant-1", default, deferSend: true);
        Assert.NotNull(first); Assert.NotNull(duplicate); Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(0, Volatile.Read(ref getUserCalls)); Assert.Equal(0, Volatile.Read(ref createCalls));

        var send = sync.TrySendEventAsync(first);
        await getUserEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(send.IsCompleted);
        release.TrySetResult();
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref getUserCalls)); Assert.Equal(1, Volatile.Read(ref createCalls));
        Assert.Equal("owner", createUsername);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Single(await verify.GozargahSiteSyncEvents.ToListAsync());
        Assert.Equal(GozargahSiteSyncStatuses.Succeeded, (await verify.GozargahSiteSyncEvents.SingleAsync()).Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task Gozargah_site_wallet_debit_remains_synchronous_and_exactly_once()
    {
        using var databases = new Databases();
        var debitEntered = Signal(); var release = Signal(); var debitCalls = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var action = body.RootElement.GetProperty("action").GetString();
            if (action == "get_user")
            {
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", ban = 0, wallet = 500000 } });
                return;
            }
            if (action == "deduct_wallet")
            {
                Interlocked.Increment(ref debitCalls); debitEntered.TrySetResult(); await release.Task;
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { telegram_id = "711", amount = 190000, previous_wallet = 500000, current_wallet = 310000 } });
                return;
            }
            await context.Response.WriteAsJsonAsync(new { success = false, message = "unexpected" });
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteWalletPaymentsEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = app.Urls.Single(), ["gozargahSiteApiKey"] = "test-only"
        }).Build();
        var sync = new GozargahSiteSyncService(databases.Users, new CredentialsStore(databases.Credentials),
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration, NullLogger<GozargahSiteSyncService>.Instance);
        var first = sync.DeductSiteWalletAfterPanelSuccessAsync(
            711, 190_000, "tenant-order", "financial-exactly-once",
            "tenant fulfillment test", default);
        await debitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref debitCalls));

        release.TrySetResult();
        var applied = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(applied.Success);
        Assert.Equal(500_000, applied.BeforeWallet);
        Assert.Equal(310_000, applied.AfterWallet);

        var replay = await sync.DeductSiteWalletAfterPanelSuccessAsync(
            711, 190_000, "tenant-order", "financial-exactly-once",
            "tenant fulfillment replay", default);
        Assert.True(replay.Success);
        Assert.Equal(500_000, replay.BeforeWallet);
        Assert.Equal(310_000, replay.AfterWallet);
        Assert.Equal(1, Volatile.Read(ref debitCalls));

        await app.StopAsync();
    }

    private static async Task<(int OrderId, int ReceiptId)> SeedFulfilledOrderWithNotificationAsync(
        Databases databases, string publicOrderId, string kind, bool addLedger = false)
    {
        await using var db = databases.Users.CreateDbContext();
        var order = HistoricalOrder(publicOrderId, fulfilled: true);
        db.TenantBotOrders.Add(order);
        await db.SaveChangesAsync();
        var receipt = ReceiptFor(order);
        db.TenantManualPaymentReceipts.Add(receipt);
        await db.SaveChangesAsync();
        order.ManualReceiptId = receipt.Id;
        db.TenantOrderNotifications.Add(new TenantOrderNotification
        {
            TenantBotOrderId = order.Id,
            Kind = kind,
            Status = TenantOrderNotificationStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        if (addLedger)
            db.TenantBotLedgerEntries.Add(new TenantBotLedgerEntry
            {
                TenantBotId = order.TenantBotId,
                TenantBotUsername = order.TenantBotUsername,
                TenantBotOrderId = order.Id,
                OrderId = order.OrderId,
                OwnerTelegramUserId = order.OwnerTelegramUserId,
                CustomerTelegramUserId = order.CustomerTelegramUserId,
                SalePriceToman = order.SalePriceToman,
                BaseCostToman = order.BaseCostToman,
                ProfitToman = order.ProfitToman,
                OwnerBalanceBefore = order.OwnerBalanceBefore,
                OwnerBalanceAfter = order.OwnerBalanceAfter,
                Description = "test tenant fulfillment",
                CreatedAtUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        return (order.Id, receipt.Id);
    }

    private sealed class BlockingOrderNotificationSender : ITenantOrderNotificationSender
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();

        public async Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return 7007;
        }
    }

    private sealed class CountingOrderNotificationSender : ITenantOrderNotificationSender
    {
        public int Count;
        public Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult<int?>(7007);
        }
    }

    private sealed class ThrowingOrderNotificationSender : ITenantOrderNotificationSender
    {
        private readonly Exception _exception;
        public ThrowingOrderNotificationSender(Exception exception) => _exception = exception;
        public Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken)
            => Task.FromException<int?>(_exception);
    }

}
