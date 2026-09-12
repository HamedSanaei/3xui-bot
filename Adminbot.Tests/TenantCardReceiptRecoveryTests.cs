using System.Collections.Concurrent;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

public sealed partial class ConcurrencyTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Regression context
    // ---------------------------------------------------------------------------------------------------------------
    // Before the receipt-ingestion fix, a tenant card-to-card customer could send the receipt as a Telegram document
    // (send without compression). The legacy handler only read Message.Photo, so no TenantManualPaymentReceipt and no
    // TenantManualReceiptNotification were persisted and the order stayed awaiting_receipt with ManualReceiptId null.
    // The dropped image cannot be reconstructed, so recovery must ask the customer to re-upload it. These tests protect
    // the three invariants that matter: only genuinely affected orders are selected, duplicate invocations cannot spam
    // a customer, and a reminder that became unnecessary is never sent.

    [Fact]
    public void Recovery_reminder_needs_an_order_that_still_has_no_receipt()
    {
        var eligible = RecoveryOrder("recovery-eligible");
        Assert.True(TenantOrderNotificationDeliveryService.IsRecoveryReminderStillNeeded(eligible));

        var fulfilled = RecoveryOrder("recovery-fulfilled");
        fulfilled.IsFulfilled = true;
        Assert.False(TenantOrderNotificationDeliveryService.IsRecoveryReminderStillNeeded(fulfilled));

        var linked = RecoveryOrder("recovery-linked");
        linked.ManualReceiptId = 42;
        Assert.False(TenantOrderNotificationDeliveryService.IsRecoveryReminderStillNeeded(linked));

        var movedOn = RecoveryOrder("recovery-moved-on");
        movedOn.PaymentStatus = TenantBotOrderStatuses.Pending;
        Assert.False(TenantOrderNotificationDeliveryService.IsRecoveryReminderStillNeeded(movedOn));
    }

    [Fact]
    public void Recovery_reminder_text_and_button_target_the_exact_order()
    {
        var order = RecoveryOrder("recovery-text");
        order.Id = 241;

        var text = TenantOrderNotificationDeliveryService.BuildReceiptReuploadRecoveryText(order);
        Assert.Contains("بررسی سفارش کارت‌به‌کارت", text, StringComparison.Ordinal);
        Assert.Contains("recovery-text", text, StringComparison.Ordinal);
        Assert.Contains("ارسال مجدد رسید هیچ پرداخت جدیدی ایجاد نمی‌کند.", text, StringComparison.Ordinal);
        // The reminder must never assert that the customer actually paid.
        Assert.DoesNotContain("پرداخت شد", text, StringComparison.Ordinal);
        Assert.DoesNotContain("تایید شد", text, StringComparison.Ordinal);

        var keyboard = TenantOrderNotificationDeliveryService.BuildReceiptReuploadRecoveryKeyboard(241);
        var buttons = keyboard.InlineKeyboard.SelectMany(x => x).ToList();
        var button = Assert.Single(buttons);
        Assert.Equal("TN:receipt:241", button.CallbackData);
        Assert.Contains("ارسال مجدد رسید", button.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Candidate classification
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dry_run_identifies_the_dropped_receipt_order_and_writes_nothing()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("incident-order"));

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var options = new TenantCardReceiptRecoveryOptions { UntilUtc = DateTime.UtcNow.AddMinutes(1) };
        var scan = await service.ScanAsync(options, default);

        var candidate = Assert.Single(scan.Candidates);
        Assert.Equal("incident-order", candidate.OrderId);
        Assert.Equal("tenant-recovery", candidate.TenantBotId);
        Assert.Equal(7468859738, candidate.CustomerTelegramUserId);
        Assert.Equal(TenantBotOrderKinds.Purchase, candidate.OrderKind);
        Assert.Equal(1, scan.Eligible);
        Assert.Equal(0, scan.AlreadyRecoveryQueued);
        Assert.Equal(0, scan.ExistingReceipt);

        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());
        Assert.Empty(await verify.TenantManualPaymentReceipts.ToListAsync());
        var stored = await verify.TenantBotOrders.SingleAsync();
        Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, stored.PaymentStatus);
        Assert.False(stored.IsFulfilled);
        Assert.Null(stored.ManualReceiptId);
    }

    [Fact]
    public async Task Recovery_never_targets_an_order_that_already_has_a_receipt()
    {
        using var databases = new Databases();
        var order = RecoveryOrder("receipt-present");
        await SeedAsync(databases, order);
        await SeedReceiptAsync(databases, order, TenantManualReceiptNotificationStatuses.Delivered);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        Assert.Empty(scan.Candidates);
        Assert.Equal(1, scan.ExistingReceipt);
        Assert.Equal(0, scan.ReceiptDeliveryNeedsReview);
        var diagnostic = Assert.Single(scan.ReceiptDeliveryDiagnostics);
        Assert.Equal(TenantManualReceiptNotificationStatuses.Delivered, diagnostic.NotificationStatus);
    }

    [Theory]
    [InlineData(TenantManualReceiptNotificationStatuses.Pending)]
    [InlineData(TenantManualReceiptNotificationStatuses.Processing)]
    [InlineData(TenantManualReceiptNotificationStatuses.Delivered)]
    public async Task Healthy_receipt_relay_is_not_a_recovery_candidate(string relayStatus)
    {
        using var databases = new Databases();
        var order = RecoveryOrder("relay-" + relayStatus);
        await SeedAsync(databases, order);
        await SeedReceiptAsync(databases, order, relayStatus);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        Assert.Empty(scan.Candidates);
        Assert.Equal(0, scan.ReceiptDeliveryNeedsReview);
    }

    [Theory]
    [InlineData(TenantManualReceiptNotificationStatuses.ManualReview)]
    [InlineData(TenantManualReceiptNotificationStatuses.DeliveryUncertain)]
    [InlineData(TenantManualReceiptNotificationStatuses.FailedPermanent)]
    [InlineData(null)]
    public async Task Broken_owner_receipt_relay_is_reported_instead_of_asking_the_customer(string relayStatus)
    {
        using var databases = new Databases();
        var order = RecoveryOrder("relay-broken-" + (relayStatus ?? "missing"));
        await SeedAsync(databases, order);
        await SeedReceiptAsync(databases, order, relayStatus);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        // This is the second, different incident: the receipt exists, so only an operator can recover the owner relay.
        Assert.Empty(scan.Candidates);
        Assert.Equal(1, scan.ReceiptDeliveryNeedsReview);
        Assert.Equal(0, scan.ReceiptDeliveryDiagnostics[0].AttemptCount);
    }

    [Fact]
    public async Task A_manual_receipt_link_without_any_row_is_never_selected()
    {
        using var databases = new Databases();
        var order = RecoveryOrder("linked-manual");
        order.ManualReceiptId = 77;
        await SeedAsync(databases, order);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        // A linked receipt id means the order already has a receipt, even when the row cannot be read back.
        Assert.Empty(scan.Candidates);
        Assert.Equal(1, scan.ExistingReceipt);
        Assert.Empty(scan.ReceiptDeliveryDiagnostics);
    }

    [Fact]
    public async Task A_receipt_row_that_the_order_does_not_link_is_reported_as_inconsistent()
    {
        using var databases = new Databases();
        var order = RecoveryOrder("unlinked-receipt");
        await SeedAsync(databases, order);
        await SeedReceiptAsync(databases, order, TenantManualReceiptNotificationStatuses.Delivered);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        Assert.Empty(scan.Candidates);
        Assert.Equal(1, scan.ExistingReceipt);
        Assert.Equal(1, scan.ReceiptLinkInconsistent);
    }

    [Fact]
    public async Task Orders_without_a_usable_customer_or_tenant_identity_are_excluded()
    {
        using var databases = new Databases();
        var noCustomer = RecoveryOrder("no-customer");
        noCustomer.CustomerTelegramUserId = 0;
        var noTenant = RecoveryOrder("no-tenant");
        noTenant.TenantBotId = " ";
        await SeedAsync(databases, noCustomer, noTenant);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        Assert.Empty(scan.Candidates);
        Assert.Equal(1, scan.InvalidCustomer);
        Assert.Equal(1, scan.InvalidTenant);
    }

    [Fact]
    public async Task Non_card_providers_fulfilled_orders_and_receipt_states_are_excluded()
    {
        using var databases = new Databases();
        var online = RecoveryOrder("online-provider");
        online.PaymentProvider = "hooshpay";
        var alreadyFulfilled = RecoveryOrder("already-fulfilled");
        alreadyFulfilled.IsFulfilled = true;
        alreadyFulfilled.PaymentStatus = TenantBotOrderStatuses.Fulfilled;
        var submitted = RecoveryOrder("already-submitted");
        submitted.PaymentStatus = TenantBotOrderStatuses.ReceiptSubmitted;
        await SeedAsync(databases, online, alreadyFulfilled, submitted);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        Assert.Empty(scan.Candidates);
        Assert.Equal(0, scan.Eligible);
    }

    [Fact]
    public async Task The_recovery_window_is_intersected_with_every_supplied_filter()
    {
        using var databases = new Databases();
        var now = DateTime.UtcNow;
        var old = RecoveryOrder("window-old");
        old.CreatedAtUtc = now.AddHours(-6);
        var recent = RecoveryOrder("window-recent");
        recent.CreatedAtUtc = now.AddMinutes(-5);
        await SeedAsync(databases, old, recent);

        var service = new TenantCardReceiptRecoveryService(databases.Users);

        // The explicit cutoff is the operator's incident boundary; anything newer is an ordinary unpaid order.
        var beforeCutoff = await service.ScanAsync(
            new TenantCardReceiptRecoveryOptions { UntilUtc = now.AddHours(-1) }, default);
        Assert.Equal("window-old", Assert.Single(beforeCutoff.Candidates).OrderId);

        // A relative staleness filter can only narrow the window, never widen it past an explicit cutoff.
        var narrowed = await service.ScanAsync(
            new TenantCardReceiptRecoveryOptions { UntilUtc = now.AddHours(-1), OlderThanMinutes = 24 * 60 }, default);
        Assert.Empty(narrowed.Candidates);

        var sinceBound = await service.ScanAsync(
            new TenantCardReceiptRecoveryOptions { SinceUtc = now.AddHours(-1) }, default);
        Assert.Equal("window-recent", Assert.Single(sinceBound.Candidates).OrderId);
    }

    [Fact]
    public async Task Tenant_bot_filter_scopes_the_scan_and_the_limit_bounds_the_candidates()
    {
        using var databases = new Databases();
        var first = RecoveryOrder("tenant-one");
        var second = RecoveryOrder("tenant-two");
        second.TenantBotId = "tenant-other";
        await SeedAsync(databases, first, second);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scoped = await service.ScanAsync(
            new TenantCardReceiptRecoveryOptions { TenantBotId = "tenant-other" }, default);
        Assert.Equal("tenant-two", Assert.Single(scoped.Candidates).OrderId);

        var limited = await service.ScanAsync(new TenantCardReceiptRecoveryOptions { Limit = 1 }, default);
        Assert.Single(limited.Candidates);
        Assert.Equal(2, limited.Eligible);
        Assert.Equal(1, limited.LimitApplied);
    }

    [Fact]
    public async Task Eligible_orders_without_a_usable_storefront_transport_are_still_listed_with_a_counter()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("no-transport"));

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions(), default);

        Assert.Single(scan.Candidates);
        Assert.Equal(1, scan.TransportUnavailable);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Apply idempotency
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_queues_exactly_one_recovery_intent_per_order()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("apply-once"));

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var options = new TenantCardReceiptRecoveryOptions { Apply = true, UntilUtc = DateTime.UtcNow.AddMinutes(1) };
        var scan = await service.ScanAsync(options, default);
        Assert.Equal(1, await service.ApplyAsync(scan, options, default));

        await using var verify = databases.Users.CreateDbContext();
        var row = Assert.Single(await verify.TenantOrderNotifications.ToListAsync());
        Assert.Equal(TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery, row.Kind);
        Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        // Recovery itself must never produce a receipt, a payment change, a wallet effect, or an XUI client.
        Assert.Empty(await verify.TenantManualPaymentReceipts.ToListAsync());
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
        Assert.Empty(await verify.TenantCardProvisionalOperations.ToListAsync());
        var order = await verify.TenantBotOrders.SingleAsync();
        Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, order.PaymentStatus);
        Assert.False(order.IsFulfilled);
        Assert.Null(order.PaidAtUtc);
    }

    [Fact]
    public async Task Repeated_apply_and_an_already_queued_order_never_produce_a_second_intent()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("apply-twice"));

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var options = new TenantCardReceiptRecoveryOptions { Apply = true, UntilUtc = DateTime.UtcNow.AddMinutes(1) };
        var first = await service.ScanAsync(options, default);
        Assert.Equal(1, await service.ApplyAsync(first, options, default));

        var second = await service.ScanAsync(options, default);
        Assert.Empty(second.Candidates);
        Assert.Equal(1, second.AlreadyRecoveryQueued);
        Assert.Equal(0, await service.ApplyAsync(second, options, default));

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(1, await verify.TenantOrderNotifications.CountAsync());
    }

    [Fact]
    public async Task Concurrent_apply_from_many_callers_still_queues_one_intent()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("apply-concurrent"));

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var options = new TenantCardReceiptRecoveryOptions { Apply = true, UntilUtc = DateTime.UtcNow.AddMinutes(1) };
        var scans = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.ScanAsync(options, default)));

        var results = await Task.WhenAll(scans.Select(scan => service.ApplyAsync(scan, options, default)));

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(1, await verify.TenantOrderNotifications.CountAsync());
        // Every caller must treat the race as "already queued", so at most one reports a new insertion and no caller
        // throws a unique-constraint failure out of the command.
        Assert.True(results.Sum() <= 1);
    }

    [Fact]
    public async Task A_receipt_arriving_before_apply_skips_the_candidate()
    {
        using var databases = new Databases();
        var order = RecoveryOrder("apply-raced");
        await SeedAsync(databases, order);

        var service = new TenantCardReceiptRecoveryService(databases.Users);
        var options = new TenantCardReceiptRecoveryOptions { Apply = true, UntilUtc = DateTime.UtcNow.AddMinutes(1) };
        var scan = await service.ScanAsync(options, default);
        Assert.Single(scan.Candidates);

        // The customer re-uploads the image (or the owner flow links it) between the scan and the apply.
        await SeedReceiptAsync(databases, order, TenantManualReceiptNotificationStatuses.Pending);
        Assert.Equal(0, await service.ApplyAsync(scan, options, default));

        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Delivery
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recovery_reminder_is_delivered_by_the_exact_tenant_bot_and_is_never_cross_sent()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance
            {
                Id = "tenant-recovery", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20011)
            });
            registry.Upsert(new BotInstance
            {
                Id = "tenant-other", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20012)
            });
            var order = RecoveryOrder("deliver-order");
            await SeedAsync(databases, order);
            await SeedQueuedRecoveryAsync(databases, order.Id);

            var worker = new TenantOrderNotificationWorker(
                databases.Users,
                provider.GetRequiredService<IServiceScopeFactory>(),
                IncidentConfiguration(),
                NullLogger<TenantOrderNotificationWorker>.Instance);
            Assert.Equal(1, await worker.ProcessOnceAsync());

            Assert.True(clients.TryGetValue("tenant-recovery", out var client));
            Assert.Contains(client!.Texts, text => text.Contains("بررسی سفارش کارت‌به‌کارت", StringComparison.Ordinal));
            Assert.Contains("TN:receipt:" + order.Id, client.Callbacks);
            Assert.Contains(client.Labels, label => label.Contains("ارسال مجدد رسید", StringComparison.Ordinal));
            // The default owned bot and the Sales Assistant must never carry a storefront recovery reminder.
            Assert.False(clients.ContainsKey("main"));
            Assert.False(clients.ContainsKey("assistant"));
            Assert.False(clients.ContainsKey("tenant-other"));

            await using var verify = databases.Users.CreateDbContext();
            var row = await verify.TenantOrderNotifications.SingleAsync();
            Assert.Equal(TenantOrderNotificationStatuses.Delivered, row.Status);
            Assert.Equal(1, row.TelegramMessageId);
            Assert.NotNull(row.DeliveredAtUtc);
            // A courtesy reminder must not settle anything: no fulfillment, no ledger, no provisional XUI client.
            var stored = await verify.TenantBotOrders.SingleAsync();
            Assert.False(stored.IsFulfilled);
            Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, stored.PaymentStatus);
            Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
            Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
            Assert.Empty(await verify.TenantCardProvisionalOperations.ToListAsync());
        }
    }

    [Fact]
    public async Task Renewal_recovery_reminder_does_not_mutate_any_xui_state()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance
            {
                Id = "tenant-recovery", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20011)
            });
            var order = RecoveryOrder("renewal-order", orderKind: TenantBotOrderKinds.Renew);
            order.TargetAccountEmail = "renew@example.test";
            order.TargetAccountUuid = "11111111-2222-3333-4444-555555555555";
            await SeedAsync(databases, order);
            await SeedQueuedRecoveryAsync(databases, order.Id);

            var worker = new TenantOrderNotificationWorker(
                databases.Users,
                provider.GetRequiredService<IServiceScopeFactory>(),
                IncidentConfiguration(),
                NullLogger<TenantOrderNotificationWorker>.Instance);
            Assert.Equal(1, await worker.ProcessOnceAsync());

            // Receipt recovery is valid for a renewal, but it must never touch the panel or a wallet.
            Assert.True(clients.ContainsKey("tenant-recovery"));
            await using var verify = databases.Users.CreateDbContext();
            Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
            Assert.Empty(await verify.TenantCardProvisionalOperations.ToListAsync());
            Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
            var stored = await verify.TenantBotOrders.SingleAsync();
            Assert.Equal("renew@example.test", stored.TargetAccountEmail);
            Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, stored.PaymentStatus);
        }
    }

    [Fact]
    public async Task A_receipt_arriving_before_the_send_supersedes_the_reminder_without_any_send()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance
            {
                Id = "tenant-recovery", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20011)
            });
            var order = RecoveryOrder("superseded-by-receipt");
            await SeedAsync(databases, order);
            await SeedQueuedRecoveryAsync(databases, order.Id);
            // The customer re-uploads while the intent waits in the outbox.
            await SeedReceiptAsync(databases, order, TenantManualReceiptNotificationStatuses.Pending);

            var worker = new TenantOrderNotificationWorker(
                databases.Users,
                provider.GetRequiredService<IServiceScopeFactory>(),
                IncidentConfiguration(),
                NullLogger<TenantOrderNotificationWorker>.Instance);
            Assert.Equal(1, await worker.ProcessOnceAsync());

            await using var verify = databases.Users.CreateDbContext();
            var row = await verify.TenantOrderNotifications.SingleAsync();
            Assert.Equal(TenantOrderNotificationStatuses.Superseded, row.Status);
            Assert.Null(row.TelegramMessageId);
            Assert.Null(row.DeliveredAtUtc);
            // Superseded is not a failure: it must not be reported as a permanent failure or an uncertain delivery.
            Assert.NotEqual(TenantOrderNotificationStatuses.FailedPermanent, row.Status);
            Assert.NotEqual(TenantOrderNotificationStatuses.DeliveryUncertain, row.Status);
            Assert.False(clients.ContainsKey("tenant-recovery"));
            // The receipt the customer already uploaded keeps its own owner relay untouched by the recovery path.
            var relay = await verify.TenantManualReceiptNotifications.SingleAsync();
            Assert.Equal(TenantManualReceiptNotificationStatuses.Pending, relay.Status);
            Assert.Equal(1, await verify.TenantManualPaymentReceipts.CountAsync());
        }
    }

    [Theory]
    [InlineData(true, TenantBotOrderStatuses.AwaitingReceipt)]
    [InlineData(false, TenantBotOrderStatuses.Pending)]
    public async Task An_order_that_moved_on_before_the_send_supersedes_the_reminder(
        bool fulfilled, string paymentStatus)
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance
            {
                Id = "tenant-recovery", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20011)
            });
            var order = RecoveryOrder("superseded-by-state");
            await SeedAsync(databases, order);
            await SeedQueuedRecoveryAsync(databases, order.Id);

            await using (var db = databases.Users.CreateDbContext())
            {
                var stored = await db.TenantBotOrders.SingleAsync();
                stored.IsFulfilled = fulfilled;
                stored.PaymentStatus = paymentStatus;
                await db.SaveChangesAsync();
            }

            var worker = new TenantOrderNotificationWorker(
                databases.Users,
                provider.GetRequiredService<IServiceScopeFactory>(),
                IncidentConfiguration(),
                NullLogger<TenantOrderNotificationWorker>.Instance);
            Assert.Equal(1, await worker.ProcessOnceAsync());

            await using var verify = databases.Users.CreateDbContext();
            Assert.Equal(TenantOrderNotificationStatuses.Superseded,
                (await verify.TenantOrderNotifications.SingleAsync()).Status);
            Assert.False(clients.ContainsKey("tenant-recovery"));
        }
    }

    [Theory]
    [InlineData(false, "token")]
    [InlineData(true, null)]
    public async Task An_unusable_storefront_transport_requires_review_and_never_cross_sends(
        bool enabled, string token)
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            // The storefront exists but cannot deliver: disabled, or missing its token.
            registry.Upsert(new BotInstance
            {
                Id = "tenant-recovery", Type = BotInstanceTypes.Tenant, Enabled = enabled,
                Token = token == null ? null : Token(20011)
            });
            var order = RecoveryOrder("no-transport-delivery");
            await SeedAsync(databases, order);
            await SeedQueuedRecoveryAsync(databases, order.Id);

            var worker = new TenantOrderNotificationWorker(
                databases.Users,
                provider.GetRequiredService<IServiceScopeFactory>(),
                IncidentConfiguration(),
                NullLogger<TenantOrderNotificationWorker>.Instance);
            Assert.Equal(1, await worker.ProcessOnceAsync());

            await using var verify = databases.Users.CreateDbContext();
            var row = await verify.TenantOrderNotifications.SingleAsync();
            Assert.Equal(TenantOrderNotificationStatuses.ManualReview, row.Status);
            Assert.Equal("tenant_transport_unavailable", row.LastError);
            Assert.Null(row.TelegramMessageId);
            // No fallback to the default owned bot, the Sales Assistant, or any other storefront.
            Assert.False(clients.ContainsKey("main"));
            Assert.False(clients.ContainsKey("assistant"));
            Assert.False(clients.ContainsKey("tenant-recovery"));
            await using var credentials = databases.Credentials.CreateDbContext();
            Assert.Empty(await credentials.WalletOperations.ToListAsync());
        }
    }

    [Fact]
    public async Task An_unregistered_storefront_transport_requires_review()
    {
        using var databases = new Databases();
        var (provider, _, clients) = IncidentProvider(databases);
        await using (provider)
        {
            // The order references a storefront that is not registered in the runtime at all.
            var order = RecoveryOrder("missing-transport", tenantBotId: "tenant-absent");
            await SeedAsync(databases, order);
            await SeedQueuedRecoveryAsync(databases, order.Id);

            var worker = new TenantOrderNotificationWorker(
                databases.Users,
                provider.GetRequiredService<IServiceScopeFactory>(),
                IncidentConfiguration(),
                NullLogger<TenantOrderNotificationWorker>.Instance);
            Assert.Equal(1, await worker.ProcessOnceAsync());

            await using var verify = databases.Users.CreateDbContext();
            var row = await verify.TenantOrderNotifications.SingleAsync();
            Assert.Equal(TenantOrderNotificationStatuses.ManualReview, row.Status);
            Assert.Equal("tenant_transport_unavailable", row.LastError);
            Assert.Empty(clients);
        }
    }

    [Fact]
    public async Task A_transient_transport_failure_keeps_the_recovery_reminder_pending_for_bounded_retry()
    {
        using var databases = new Databases();
        var order = RecoveryOrder("retry-order");
        await SeedAsync(databases, order);
        await SeedQueuedRecoveryAsync(databases, order.Id);

        var sender = new CountingFailingOrderNotificationSender(new BotTransportUnavailableException("transport_unavailable"));
        var worker = new TenantOrderNotificationWorker(
            databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantOrderNotifications.SingleAsync();
        Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
        Assert.Equal("bot_transport_unavailable", row.LastError);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.NextAttemptAtUtc);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task An_ambiguous_send_outcome_is_never_replayed_for_a_recovery_reminder()
    {
        using var databases = new Databases();
        var order = RecoveryOrder("uncertain-order");
        await SeedAsync(databases, order);
        await SeedQueuedRecoveryAsync(databases, order.Id);

        var sender = new CountingFailingOrderNotificationSender(new TimeoutException());
        var worker = new TenantOrderNotificationWorker(
            databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());

        await using (var verify = databases.Users.CreateDbContext())
            Assert.Equal(TenantOrderNotificationStatuses.DeliveryUncertain,
                (await verify.TenantOrderNotifications.SingleAsync()).Status);

        // DeliveryUncertain is terminal for automation, so a later scan must not send the reminder again.
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Command line
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Recovery_mode_is_only_selected_by_its_exact_switch()
    {
        Assert.True(TenantCardReceiptRecoveryCli.IsRequested(new[] { TenantCardReceiptRecoveryCli.Mode }));
        Assert.False(TenantCardReceiptRecoveryCli.IsRequested(new[] { "--migration-check" }));
        Assert.False(TenantCardReceiptRecoveryCli.IsRequested(Array.Empty<string>()));
    }

    [Fact]
    public void Recovery_mode_defaults_to_a_dry_run()
    {
        Assert.True(TenantCardReceiptRecoveryCli.TryParse(
            new[] { TenantCardReceiptRecoveryCli.Mode }, out var options, out var error));
        Assert.Null(error);
        Assert.False(options.Apply);
    }

    [Fact]
    public void Recovery_apply_without_an_explicit_cutoff_is_refused()
    {
        // awaiting_receipt alone is never proof of payment, so mutation requires the operator's incident window.
        Assert.False(TenantCardReceiptRecoveryCli.TryParse(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--apply" }, out var options, out var error));
        Assert.Equal("apply_requires_until", error);
        Assert.False(options.Apply);
    }

    [Fact]
    public void Recovery_apply_with_a_cutoff_is_accepted()
    {
        Assert.True(TenantCardReceiptRecoveryCli.TryParse(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--apply", "--until", "2026-09-12T02:30:00Z" },
            out var options, out var error));
        Assert.Null(error);
        Assert.True(options.Apply);
        Assert.Equal(new DateTime(2026, 9, 12, 2, 30, 0, DateTimeKind.Utc), options.UntilUtc);
    }

    [Theory]
    [InlineData("--until", "not-a-date", "invalid_until")]
    [InlineData("--since", "yesterday-ish", "invalid_since")]
    [InlineData("--older-than-minutes", "-5", "invalid_older_than_minutes")]
    [InlineData("--limit", "-1", "invalid_limit")]
    [InlineData("--limit", "0", "invalid_limit")]
    [InlineData("--limit", "999999999", "invalid_limit")]
    [InlineData("--tenant-bot-id", "", "missing_option_value")]
    [InlineData("--users-db", null, "missing_option_value")]
    [InlineData("--until", null, "missing_option_value")]
    [InlineData("--nonsense", "1", "unknown_argument")]
    public void Recovery_refuses_invalid_arguments(string option, string? value, string expectedError)
    {
        var args = value == null
            ? new[] { TenantCardReceiptRecoveryCli.Mode, option }
            : new[] { TenantCardReceiptRecoveryCli.Mode, option, value };
        Assert.False(TenantCardReceiptRecoveryCli.TryParse(args, out _, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Recovery_refuses_duplicate_options_and_an_inverted_window()
    {
        Assert.False(TenantCardReceiptRecoveryCli.TryParse(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--until", "2026-09-12T02:30:00Z", "--until", "2026-09-12T03:30:00Z" },
            out _, out var duplicate));
        Assert.Equal("duplicate_option", duplicate);

        Assert.False(TenantCardReceiptRecoveryCli.TryParse(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--since", "2026-09-13T00:00:00Z", "--until", "2026-09-12T00:00:00Z" },
            out _, out var inverted));
        Assert.Equal("since_after_until", inverted);
    }

    [Fact]
    public void Recovery_accepts_every_supported_bounded_filter()
    {
        Assert.True(TenantCardReceiptRecoveryCli.TryParse(new[]
        {
            TenantCardReceiptRecoveryCli.Mode,
            "--apply",
            "--until", "2026-09-12T02:30:00Z",
            "--since", "2026-09-10T00:00:00Z",
            "--older-than-minutes", "60",
            "--tenant-bot-id", "tenant-recovery",
            "--limit", "25",
            "--users-db", "./Data/users.db"
        }, out var options, out var error));
        Assert.Null(error);
        Assert.True(options.Apply);
        Assert.Equal(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), options.SinceUtc);
        Assert.Equal(60, options.OlderThanMinutes);
        Assert.Equal("tenant-recovery", options.TenantBotId);
        Assert.Equal(25, options.Limit);
        Assert.Equal("./Data/users.db", options.UsersDatabasePath);
    }

    [Fact]
    public async Task Recovery_command_dry_run_writes_nothing_and_apply_only_enqueues()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("cli-order"));
        var databasePath = Path.Combine(databases.DirectoryPath, "users.db");
        var until = DateTime.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ssZ");

        var dryRunOutput = new StringWriter();
        var dryRunExit = await TenantCardReceiptRecoveryCli.RunAsync(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--users-db", databasePath, "--until", until },
            dryRunOutput, default);
        Assert.Equal(0, dryRunExit);
        var dryRunText = dryRunOutput.ToString();
        Assert.Contains("Mode: DRY-RUN", dryRunText, StringComparison.Ordinal);
        Assert.Contains("Eligible: 1", dryRunText, StringComparison.Ordinal);
        Assert.Contains("orderId=cli-order", dryRunText, StringComparison.Ordinal);
        // No receipt image id, token, or card data may ever appear in the operator report.
        Assert.DoesNotContain("PhotoFileId", dryRunText, StringComparison.OrdinalIgnoreCase);

        await using (var verify = databases.Users.CreateDbContext())
            Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());

        var applyOutput = new StringWriter();
        var applyExit = await TenantCardReceiptRecoveryCli.RunAsync(
            new[]
            {
                TenantCardReceiptRecoveryCli.Mode, "--users-db", databasePath,
                "--until", until, "--apply"
            },
            applyOutput, default);
        Assert.Equal(0, applyExit);
        Assert.Contains("Mode: APPLY", applyOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Enqueued: 1", applyOutput.ToString(), StringComparison.Ordinal);

        await using (var verify = databases.Users.CreateDbContext())
        {
            var row = Assert.Single(await verify.TenantOrderNotifications.ToListAsync());
            Assert.Equal(TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery, row.Kind);
            Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
        }
    }

    [Fact]
    public async Task Recovery_command_refuses_apply_without_a_cutoff_and_exits_non_zero()
    {
        using var databases = new Databases();
        await SeedAsync(databases, RecoveryOrder("cli-refused"));
        var databasePath = Path.Combine(databases.DirectoryPath, "users.db");

        var output = new StringWriter();
        var exit = await TenantCardReceiptRecoveryCli.RunAsync(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--users-db", databasePath, "--apply" }, output, default);

        Assert.NotEqual(0, exit);
        Assert.Contains("INVALID_ARGUMENTS", output.ToString(), StringComparison.Ordinal);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());
    }

    [Fact]
    public async Task Recovery_command_reports_a_missing_database_without_throwing()
    {
        using var databases = new Databases();
        var missingPath = Path.Combine(databases.DirectoryPath, "does-not-exist.db");

        var output = new StringWriter();
        var exit = await TenantCardReceiptRecoveryCli.RunAsync(
            new[] { TenantCardReceiptRecoveryCli.Mode, "--users-db", missingPath }, output, default);

        Assert.NotEqual(0, exit);
        Assert.Contains("USERS_DATABASE_NOT_FOUND", output.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Test doubles and seeding
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds one card-to-card order shaped exactly like the production incident: awaiting the receipt, unfulfilled,
    /// and not linked to any receipt row.
    /// </summary>
    private static TenantBotOrder RecoveryOrder(
        string orderId,
        string tenantBotId = "tenant-recovery",
        string orderKind = TenantBotOrderKinds.Purchase)
    {
        return new TenantBotOrder
        {
            OrderId = orderId,
            TenantBotId = tenantBotId,
            TenantBotUsername = "recovery_store",
            OwnerTelegramUserId = 711,
            CustomerTelegramUserId = 7468859738,
            CustomerChatId = 7468859738,
            CustomerUsername = "recovery_customer",
            OrderKind = orderKind,
            ServiceKey = "m1",
            TrafficGb = 10,
            DurationKey = "normal:10",
            SalePriceToman = 150_000,
            BaseCostToman = 100_000,
            ProfitToman = 50_000,
            PaymentProvider = "tenant_card",
            PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt,
            IsFulfilled = false,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30)
        };
    }

    private static async Task SeedAsync(Databases databases, params TenantBotOrder[] orders)
    {
        await using var db = databases.Users.CreateDbContext();
        db.TenantBotOrders.AddRange(orders);
        await db.SaveChangesAsync();
    }

    /// <summary>Persists a real receipt row and, when a status is supplied, its owner relay intent.</summary>
    private static async Task SeedReceiptAsync(Databases databases, TenantBotOrder order, string? relayStatus)
    {
        await using var db = databases.Users.CreateDbContext();
        var receipt = new TenantManualPaymentReceipt
        {
            TenantBotOrderId = order.Id,
            OrderId = order.OrderId,
            TenantBotId = order.TenantBotId,
            TenantBotUsername = order.TenantBotUsername,
            OwnerTelegramUserId = order.OwnerTelegramUserId,
            CustomerTelegramUserId = order.CustomerTelegramUserId,
            CustomerChatId = order.CustomerChatId,
            PhotoFileId = "recovery-photo",
            AmountToman = order.SalePriceToman,
            Status = TenantManualPaymentReceiptStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.TenantManualPaymentReceipts.Add(receipt);
        await db.SaveChangesAsync();
        if (relayStatus != null)
        {
            db.TenantManualReceiptNotifications.Add(new TenantManualReceiptNotification
            {
                ReceiptId = receipt.Id,
                Status = relayStatus,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Seeds the durable recovery intent the command would have queued.</summary>
    private static async Task SeedQueuedRecoveryAsync(Databases databases, int orderDbId)
    {
        await using var db = databases.Users.CreateDbContext();
        db.TenantOrderNotifications.Add(new TenantOrderNotification
        {
            TenantBotOrderId = orderDbId,
            Kind = TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery,
            Status = TenantOrderNotificationStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Fake notification sender that always fails and records its call count, so the worker's retry and
    /// no-blind-replay policy can be asserted without a network call.
    /// </summary>
    private sealed class CountingFailingOrderNotificationSender : ITenantOrderNotificationSender
    {
        private readonly Exception _failure;
        private int _calls;

        public CountingFailingOrderNotificationSender(Exception failure) => _failure = failure;

        /// <summary>Number of times the worker invoked the transport.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw _failure;
        }
    }
}
