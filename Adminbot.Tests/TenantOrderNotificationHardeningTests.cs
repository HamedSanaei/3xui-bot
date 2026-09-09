using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;

/// <summary>
/// Regression coverage for the tenant notification outbox and Gozargah sync hardening.
/// </summary>
/// <remarks>
/// These tests protect: (1) deferred Gozargah enqueue never waiting behind an active remote send of the same
/// account; (2) transient get_user failures retrying instead of being terminally skipped; (3) the tenant renewal
/// post-durable-commit failure boundary; (4) bounded retention for Delivered notification rows; (5) the durable
/// send phase that distinguishes pre-send crashes from possible-send crashes; and (6) ENSURE helpers that only
/// swallow DbUpdateException when a fresh read proves the concurrent insert actually landed.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>A deferred enqueue for an account whose send is blocked on website HTTP must complete immediately.</summary>
    /// <returns>A task completing after the enqueue finished before the blocked HTTP was released.</returns>
    /// <remarks>Protects fix 1: queue admission must never wait behind get_user/create_order/update_order/delete_order.
    /// Before the fix the deferred path entered the same account gate held during the blocked remote send and would
    /// time out here; after the fix only the short database-only queue gate is used for admission.</remarks>
    [Fact]
    public async Task Deferred_gozargah_enqueue_does_not_wait_behind_blocked_remote_send()
    {
        using var databases = new Databases();
        var getUserEntered = Signal(); var release = Signal();
        var getUserCalls = 0; var createCalls = 0;
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
            Success = true, Email = "blocked@example.test", Uuid = Guid.NewGuid().ToString(),
            SubId = "sub-blocked", SubLink = "https://example.invalid/sub/blocked", TrafficGb = 10, DurationDays = 30
        };

        // Persist the event, then start a real remote send that blocks inside get_user.
        var row = await sync.QueueCreateAsync(711, 722, created, "blocked-send", "tenant-1", default, deferSend: true);
        Assert.NotNull(row);
        var send = sync.TrySendEventAsync(row);
        await getUserEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the worker holds the send-side account gate, a deferred enqueue for the same account must
        // complete without waiting for the blocked website HTTP.
        var enqueue = sync.QueueCreateAsync(711, 722, created, "blocked-send", "tenant-1", default, deferSend: true);
        var enqueued = await enqueue.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(enqueued);
        Assert.Equal(row.Id, enqueued.Id);

        release.TrySetResult();
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref getUserCalls));
        Assert.Equal(1, Volatile.Read(ref createCalls));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Single(await verify.GozargahSiteSyncEvents.ToListAsync());
        await app.StopAsync();
    }

    /// <summary>An HTTP 500 from get_user marks the durable event Failed, never Skipped.</summary>
    /// <returns>A task completing after the retryable classification is asserted.</returns>
    /// <remarks>Protects fix 2: operational 5xx responses must not terminally skip the website mirror event.</remarks>
    [Fact]
    public async Task Gozargah_get_user_http_500_marks_event_failed_not_skipped()
    {
        using var databases = new Databases();
        var app = await GozargahServerAsync(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("upstream exploded");
        });
        await using (app.App)
        {
            var sync = GozargahSync(databases, app.Url);
            var row = await QueueDeferredCreateAsync(sync);
            Assert.False(await sync.TrySendEventAsync(row));
            await using var verify = databases.Users.CreateDbContext();
            var persisted = await verify.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(GozargahSiteSyncStatuses.Failed, persisted.Status);
            Assert.StartsWith("HTTP 500", persisted.LastError, StringComparison.Ordinal);
            Assert.True(persisted.LastError.Length <= 512);
        }
    }

    /// <summary>Invalid JSON or an HTML proxy page from get_user marks the event Failed, never Skipped.</summary>
    /// <returns>A task completing after the retryable classification is asserted.</returns>
    /// <remarks>Protects fix 2: malformed and non-JSON upstream responses are transient, not terminal.</remarks>
    [Fact]
    public async Task Gozargah_get_user_invalid_json_marks_event_failed_not_skipped()
    {
        using var databases = new Databases();
        var app = await GozargahServerAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("not-json-{");
        });
        await using (app.App)
        {
            var sync = GozargahSync(databases, app.Url);
            var row = await QueueDeferredCreateAsync(sync);
            Assert.False(await sync.TrySendEventAsync(row));
            await using var verify = databases.Users.CreateDbContext();
            var persisted = await verify.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(GozargahSiteSyncStatuses.Failed, persisted.Status);
            Assert.Contains("Invalid JSON", persisted.LastError, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The documented missing-user result of get_user stays a terminal Skipped event.</summary>
    /// <returns>A task completing after both documented missing-user shapes are asserted as Skipped.</returns>
    /// <remarks>Protects fix 2: the HTTP 404 "not found" response and the plain business not-found message are the
    /// only permanent missing-user results; everything else stays retryable.</remarks>
    [Fact]
    public async Task Gozargah_get_user_documented_missing_user_marks_event_skipped()
    {
        using var databases = new Databases();
        var calls = 0;
        var app = await GozargahServerAsync(context =>
        {
            Interlocked.Increment(ref calls);
            if (Volatile.Read(ref calls) == 1)
            {
                context.Response.StatusCode = 404;
                return context.Response.WriteAsync("user not found");
            }
            return context.Response.WriteAsJsonAsync(new { success = false, message = "not found" });
        });
        await using (app.App)
        {
            var sync = GozargahSync(databases, app.Url);

            var httpRow = await QueueDeferredCreateAsync(sync, suffix: "http-404");
            Assert.True(await sync.TrySendEventAsync(httpRow));
            await using (var verify = databases.Users.CreateDbContext())
            {
                var persisted = await verify.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == httpRow.Id);
                Assert.Equal(GozargahSiteSyncStatuses.Skipped, persisted.Status);
                Assert.Contains("not found", persisted.LastError, StringComparison.OrdinalIgnoreCase);
            }

            var businessRow = await QueueDeferredCreateAsync(sync, suffix: "business-not-found");
            Assert.True(await sync.TrySendEventAsync(businessRow));
            await using var final = databases.Users.CreateDbContext();
            var business = await final.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == businessRow.Id);
            Assert.Equal(GozargahSiteSyncStatuses.Skipped, business.Status);
        }
    }

    /// <summary>A banned site user makes the event Skipped, never retried forever.</summary>
    /// <returns>A task completing after the terminal ban classification is asserted.</returns>
    /// <remarks>Protects fix 2: an explicit ban is a permanent business outcome.</remarks>
    [Fact]
    public async Task Gozargah_get_user_banned_user_marks_event_skipped()
    {
        using var databases = new Databases();
        var app = await GozargahServerAsync(context =>
            context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", ban = 1, wallet = 0 } }));
        await using (app.App)
        {
            var sync = GozargahSync(databases, app.Url);
            var row = await QueueDeferredCreateAsync(sync);
            Assert.True(await sync.TrySendEventAsync(row));
            await using var verify = databases.Users.CreateDbContext();
            var persisted = await verify.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(GozargahSiteSyncStatuses.Skipped, persisted.Status);
            Assert.Contains("banned", persisted.LastError, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>After a transient get_user failure, a later successful lookup reaches Succeeded exactly once.</summary>
    /// <returns>A task completing after the retry-then-success lifecycle is asserted.</returns>
    /// <remarks>Protects fix 2 requirement 5: transient failures keep the event durable and the worker's later retry
    /// completes the create exactly once.</remarks>
    [Fact]
    public async Task Gozargah_transient_get_user_failure_recovers_to_succeeded_on_retry()
    {
        using var databases = new Databases();
        var getUserCalls = 0;
        var app = await GozargahServerAsync(context =>
        {
            // The outer server lambda already parsed the request body and only routes get_user here, so the
            // response depends only on the call count.
            if (Interlocked.Increment(ref getUserCalls) == 1)
            {
                context.Response.StatusCode = 500;
                return context.Response.WriteAsync("temporary upstream outage");
            }
            return context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", ban = 0, wallet = 500000 } });
        });
        await using (app.App)
        {
            var sync = GozargahSync(databases, app.Url);
            var row = await QueueDeferredCreateAsync(sync);
            Assert.False(await sync.TrySendEventAsync(row));
            await using (var verify = databases.Users.CreateDbContext())
            {
                var failed = await verify.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == row.Id);
                Assert.Equal(GozargahSiteSyncStatuses.Failed, failed.Status);
            }

            // The worker's later retry completes the create exactly once; Succeeded is only reachable after a
            // successful create_order response.
            Assert.True(await sync.TrySendEventAsync(row));
            await using var final = databases.Users.CreateDbContext();
            var succeeded = await final.GozargahSiteSyncEvents.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(GozargahSiteSyncStatuses.Succeeded, succeeded.Status);
            Assert.Null(succeeded.LastError);
            Assert.Equal(2, Volatile.Read(ref getUserCalls));
        }
    }

    /// <summary>A failure after the durable renewal commit must keep the order fulfilled with exactly one ledger row.</summary>
    /// <returns>A task completing after the post-commit boundary, no-second-mutation, and resume assertions.</returns>
    /// <remarks>
    /// Protects fix 3: the tenant renewal flow mirrors the purchase <c>fulfillmentCommitted</c> boundary. The XUI
    /// mutation and durable users.db commit (order fulfilled + ledger + notification intents) happen first; a later
    /// MarkSettledAsync failure must return Applied semantics, never revert the order, never repeat a mutation, and
    /// the recovery worker must later settle the operation idempotently from its durable Applied state.
    /// </remarks>
    [Fact]
    public async Task Renewal_post_commit_settlement_failure_keeps_fulfilled_order_exactly_once()
    {
        using var databases = new Databases();
        var failingStore = new FailingRenewalOperationStore(databases.Users) { FailSettlement = true };
        var provider = RenewalProvider(databases, failingStore);
        await using (provider)
        {
            var wallet = provider.GetRequiredService<CredentialsStore>();
            await wallet.AddEmptyUser(711);
            await wallet.AddFund(711, 200_000, "setup");
            await wallet.AddEmptyUser(7468859738);

            var orderId = await SeedRenewalOrderAsync(databases, "renew-post-commit");
            var operationKey = "tenant-renew-renew-post-commit";
            await SeedAppliedRenewalOperationAsync(databases, operationKey, "renew-post-commit");

            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var workflow = scope.ServiceProvider.GetRequiredService<UserWorkflowStore>();
            var order = await workflow.ReadAsync(db => db.TenantBotOrders.SingleAsync(x => x.Id == orderId));
            var owner = await wallet.GetUserStatusWithId(711);
            var customer = await wallet.GetUserStatusWithId(7468859738);
            Assert.NotNull(owner);
            Assert.NotNull(customer);
            var renewalOperation = await workflow.ReadAsync(db => db.XuiV3RenewalOperations.SingleAsync(x => x.OperationKey == operationKey));

            var tenant = new BotInstance { Id = "tenant-reset", Type = BotInstanceTypes.Tenant, Enabled = true };
            var payload = new XuiV3ClientPayload
            {
                Email = "client@example.test", SubId = "sub-1", Uuid = "uuid-1",
                TotalGB = 10L * 1024 * 1024 * 1024, ExpiryTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                Enable = true
            };
            var client = new XuiV3Client
            {
                Id = 0, // Id <= 0 makes the volume-reminder cycle a no-op so no panel state is required.
                Email = payload.Email, SubId = payload.SubId, Uuid = payload.Uuid,
                TotalGB = payload.TotalGB, ExpiryTime = payload.ExpiryTime, Enable = true
            };
            var renewal = new XuiV3RenewalCalculation
            {
                Payload = payload, ShouldResetTraffic = false, UsedBytes = 0,
                RenewedTrafficGb = 10, RenewedTrafficBytes = 10L * 1024 * 1024 * 1024,
                TotalBytesAfterRenew = payload.TotalGB, UpdatedExpiryTime = payload.ExpiryTime,
                AddedDurationDays = 30, FinalDurationDays = 30, IsUnlimited = false,
                CurrentTotalBytes = 0, CurrentExpiryTime = 0,
                TargetAvailableTrafficGb = 10, TargetAvailableTrafficBytes = 0
            };
            var selection = new XuiV3PurchaseSelection { ServiceKey = order.ServiceKey, TrafficGb = order.TrafficGb, DurationKey = order.DurationKey, AccountCount = 1 };

            using var timing = XuiOperationTiming.Start();
            var method = typeof(TenantBotService).GetMethod("CompleteTenantRenewalFulfillmentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<NowPaymentsSettlementResult>)method.Invoke(service, new object[]
            {
                order, owner, customer, tenant, selection, "ipn", false, client, renewal, renewalOperation, timing, CancellationToken.None
            })!;
            Assert.Equal(NowPaymentsSettlementStatus.Applied, result.Status);

            // Durable outcome: fulfilled order, one ledger row, required notification intents, and the operation
            // still locked (settlement failed) so no second renewal could ever be authorized.
            await using (var verify = databases.Users.CreateDbContext())
            {
                var persisted = await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId);
                Assert.True(persisted.IsFulfilled);
                Assert.Equal(TenantBotOrderStatuses.Fulfilled, persisted.PaymentStatus);
                Assert.Equal(1, await verify.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
                Assert.Equal(3, await verify.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId));
                var op = await verify.XuiV3RenewalOperations.SingleAsync(x => x.OperationKey == operationKey);
                Assert.Equal(XuiV3RenewalOperationStatuses.Applied, op.Status);
                Assert.Equal(XuiV3RenewalSettlementStatuses.Pending, op.SettlementStatus);
                Assert.NotNull(op.AccountLockKey);
                Assert.Empty(await verify.GozargahSiteSyncEvents.ToListAsync());
            }

            // Recovery resumes the one-time settlement from the durable Applied operation: no second mutation,
            // no second ledger row, and the account lock is finally released.
            failingStore.FailSettlement = false;
            var refreshedOperation = await workflow.ReadAsync(db => db.XuiV3RenewalOperations.SingleAsync(x => x.OperationKey == operationKey));
            Assert.True(await service.SettleRecoveredTenantRenewalAsync(refreshedOperation, default));
            await using var final = databases.Users.CreateDbContext();
            Assert.Equal(1, await final.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
            var settled = await final.XuiV3RenewalOperations.SingleAsync(x => x.OperationKey == operationKey);
            Assert.Equal(XuiV3RenewalSettlementStatuses.Settled, settled.SettlementStatus);
            Assert.Null(settled.AccountLockKey);
        }
    }

    /// <summary>Retention deletes only old Delivered rows; every unsafe status is preserved.</summary>
    /// <returns>A task completing after the bounded deletion policy is asserted.</returns>
    /// <remarks>Protects fix 4: Pending, Processing, DeliveryUncertain, ManualReview, and FailedPermanent rows are
    /// never candidates, and recently delivered rows stay until the configured cutoff.</remarks>
    [Fact]
    public async Task Notification_retention_deletes_only_old_delivered_rows()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            var order = HistoricalOrder("retention-order", fulfilled: true);
            db.TenantBotOrders.Add(order);
            await db.SaveChangesAsync();
            var old = DateTime.UtcNow.AddDays(-40);
            db.TenantOrderNotifications.AddRange(
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "old-delivered", Status = TenantOrderNotificationStatuses.Delivered, DeliveredAtUtc = old, CreatedAtUtc = old, UpdatedAtUtc = old },
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "recent-delivered", Status = TenantOrderNotificationStatuses.Delivered, DeliveredAtUtc = DateTime.UtcNow.AddDays(-1), CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow },
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "old-pending", Status = TenantOrderNotificationStatuses.Pending, CreatedAtUtc = old, UpdatedAtUtc = old },
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "old-processing", Status = TenantOrderNotificationStatuses.Processing, CreatedAtUtc = old, UpdatedAtUtc = old },
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "old-uncertain", Status = TenantOrderNotificationStatuses.DeliveryUncertain, CreatedAtUtc = old, UpdatedAtUtc = old },
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "old-review", Status = TenantOrderNotificationStatuses.ManualReview, CreatedAtUtc = old, UpdatedAtUtc = old },
                new TenantOrderNotification { TenantBotOrderId = order.Id, Kind = "old-permanent", Status = TenantOrderNotificationStatuses.FailedPermanent, CreatedAtUtc = old, UpdatedAtUtc = old });
            await db.SaveChangesAsync();
        }

        var worker = new TenantOrderNotificationWorker(databases.Users, new CountingOrderNotificationSender(), NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await worker.CompactDeliveredAsync());
        await using var verify = databases.Users.CreateDbContext();
        var kinds = await verify.TenantOrderNotifications.AsNoTracking().Select(x => x.Kind).ToListAsync();
        Assert.DoesNotContain("old-delivered", kinds);
        Assert.Contains("recent-delivered", kinds);
        Assert.Contains("old-pending", kinds);
        Assert.Contains("old-processing", kinds);
        Assert.Contains("old-uncertain", kinds);
        Assert.Contains("old-review", kinds);
        Assert.Contains("old-permanent", kinds);
        // Unrelated business tables are untouched by compaction.
        Assert.Single(await verify.TenantBotOrders.ToListAsync());
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
    }

    /// <summary>One maintenance cycle removes at most 100 old delivered rows.</summary>
    /// <returns>A task completing after both bounded batches are asserted.</returns>
    /// <remarks>Protects fix 4: cleanup is batch-limited per worker cycle so a single scan stays bounded.</remarks>
    [Fact]
    public async Task Notification_retention_batch_is_limited_to_100_per_cycle()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            var order = HistoricalOrder("retention-batch", fulfilled: true);
            db.TenantBotOrders.Add(order);
            await db.SaveChangesAsync();
            var old = DateTime.UtcNow.AddDays(-40);
            for (var i = 0; i < 150; i++)
                db.TenantOrderNotifications.Add(new TenantOrderNotification
                {
                    TenantBotOrderId = order.Id, Kind = "bulk-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Status = TenantOrderNotificationStatuses.Delivered, DeliveredAtUtc = old, CreatedAtUtc = old, UpdatedAtUtc = old
                });
            await db.SaveChangesAsync();
        }

        var worker = new TenantOrderNotificationWorker(databases.Users, new CountingOrderNotificationSender(), NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(100, await worker.CompactDeliveredAsync());
        Assert.Equal(50, await worker.CompactDeliveredAsync());
        Assert.Equal(0, await worker.CompactDeliveredAsync());
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());
    }

    /// <summary>A crash after claim but before the send phase is retried promptly, never parked uncertain.</summary>
    /// <returns>A task completing after the retryable recovery and successful redelivery are asserted.</returns>
    /// <remarks>Protects fix 5: <c>SendStartedAtUtc == null</c> proves no Telegram request could have been sent, so the
    /// expired claim is recycled to Pending within the same scan and delivered exactly once. The companion
    /// send-started test proves the contrasting conservative classification.</remarks>
    [Fact]
    public async Task Notification_claim_crash_before_send_started_is_retryable()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(databases, "pre-send-crash", TenantOrderNotificationKinds.OwnerSaleNotification);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            row.Status = TenantOrderNotificationStatuses.Processing;
            row.ClaimToken = "crashed-claim";
            row.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(-1);
            row.AttemptCount = 1;
            row.SendStartedAtUtc = null;
            await db.SaveChangesAsync();
        }

        // One scan recycles the expired pre-send claim and redelivers it: no Telegram request could have been
        // sent, so the row must not be parked as DeliveryUncertain.
        var sender = new CountingOrderNotificationSender();
        var worker = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);
        await using var verify = databases.Users.CreateDbContext();
        var delivered = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, delivered.Status);
        Assert.Null(delivered.SendStartedAtUtc);
        Assert.Equal(2, delivered.AttemptCount);
        Assert.NotEqual(TenantOrderNotificationStatuses.DeliveryUncertain, delivered.Status);
    }

    /// <summary>A crash after the send phase was persisted becomes DeliveryUncertain and is never replayed.</summary>
    /// <returns>A task completing after the conservative uncertain classification is asserted.</returns>
    /// <remarks>Protects fix 5: once <c>SendStartedAtUtc</c> is set the remote outcome may be ambiguous; the row must
    /// be parked for review, not automatically re-sent.</remarks>
    [Fact]
    public async Task Notification_send_started_crash_becomes_delivery_uncertain()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(databases, "send-started-crash", TenantOrderNotificationKinds.OwnerSaleNotification);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
            row.Status = TenantOrderNotificationStatuses.Processing;
            row.ClaimToken = "send-started-claim";
            row.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(-1);
            row.AttemptCount = 1;
            row.SendStartedAtUtc = DateTime.UtcNow.AddSeconds(-30);
            await db.SaveChangesAsync();
        }

        var sender = new CountingOrderNotificationSender();
        var worker = new TenantOrderNotificationWorker(databases.Users, sender, NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);
        await using var verify = databases.Users.CreateDbContext();
        var persisted = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.DeliveryUncertain, persisted.Status);
        Assert.Equal("processing_lease_expired_after_possible_delivery", persisted.LastError);
        Assert.Null(persisted.ClaimToken);
    }

    /// <summary>A concurrent duplicate insert through the ENSURE helper converges on one row without failure.</summary>
    /// <returns>A task completing after the benign unique-key race is asserted.</returns>
    /// <remarks>Protects fix 6: the only DbUpdateException the helper may swallow is one where a fresh read proves the
    /// required (orderId, kind) row now exists.</remarks>
    [Fact]
    public async Task Notification_ensure_duplicate_race_converges_on_single_row()
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int orderId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = HistoricalOrder("ensure-race", fulfilled: true);
                db.TenantBotOrders.Add(order);
                await db.SaveChangesAsync();
                orderId = order.Id;
            }
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var results = await Task.WhenAll(
                service.ENSURETENANTORDERNOTIFICATIONINTENTASYNC(orderId, TenantOrderNotificationKinds.OwnerSaleNotification, default),
                service.ENSURETENANTORDERNOTIFICATIONINTENTASYNC(orderId, TenantOrderNotificationKinds.OwnerSaleNotification, default));
            Assert.Equal(1, results.Sum());
            await using var verify = databases.Users.CreateDbContext();
            Assert.Equal(1, await verify.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId && x.Kind == TenantOrderNotificationKinds.OwnerSaleNotification));
        }
    }

    /// <summary>A real persistence failure inside the ENSURE helper propagates; it is never reported as queued.</summary>
    /// <returns>A task completing after the injected insert failure is asserted to propagate.</returns>
    /// <remarks>
    /// Protects fix 6: a trigger aborts the insert with a genuine DbUpdateException while a fresh read finds no row,
    /// so the helper must rethrow instead of returning 0. This guards the user-facing contract that the assistant
    /// final flow never says the details were durably queued when persistence actually failed.
    /// </remarks>
    [Fact]
    public async Task Notification_ensure_real_persistence_failure_propagates()
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int orderId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = HistoricalOrder("ensure-failure", fulfilled: true);
                db.TenantBotOrders.Add(order);
                await db.SaveChangesAsync();
                orderId = order.Id;
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER fail_notification_insert BEFORE INSERT ON TenantOrderNotifications " +
                    "BEGIN SELECT RAISE(ABORT, 'injected-insert-failure'); END");
            }
            try
            {
                await using var scope = provider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    service.ENSURETENANTORDERNOTIFICATIONINTENTASYNC(orderId, TenantOrderNotificationKinds.OwnerSaleNotification, default));
                await using var verify = databases.Users.CreateDbContext();
                Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());
            }
            finally
            {
                await using var cleanup = databases.Users.CreateDbContext();
                await cleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_notification_insert");
            }
        }
    }

    // ---------- Gozargah helpers ----------

    private static async Task<(WebApplication App, string Url)> GozargahServerAsync(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var action = body.RootElement.GetProperty("action").GetString();
            if (action == "get_user")
            {
                await handler(context);
                return;
            }
            await context.Response.WriteAsJsonAsync(new { success = true, id = 77 });
        });
        await app.StartAsync();
        return (app, app.Urls.Single());
    }

    private static GozargahSiteSyncService GozargahSync(Databases databases, string url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteRealtimeCreateSyncEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = url, ["gozargahSiteApiKey"] = "test-only"
        }).Build();
        return new GozargahSiteSyncService(databases.Users, new CredentialsStore(databases.Credentials),
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration, NullLogger<GozargahSiteSyncService>.Instance);
    }

    private static async Task<GozargahSiteSyncEvent> QueueDeferredCreateAsync(GozargahSiteSyncService sync, string suffix = "classify")
    {
        var created = new XuiV3AccountCreationResult
        {
            Success = true, Email = suffix + "@example.test", Uuid = Guid.NewGuid().ToString(),
            SubId = "sub-" + suffix, SubLink = "https://example.invalid/sub/" + suffix, TrafficGb = 10, DurationDays = 30
        };
        return await sync.QueueCreateAsync(711, 722, created, "classify-" + suffix, "tenant-1", default, deferSend: true);
    }

    // ---------- Renewal helpers ----------

    private sealed class FailingRenewalOperationStore : XuiV3RenewalOperationStore
    {
        public bool FailSettlement { get; set; }

        public FailingRenewalOperationStore(UserDbContextFactory factory)
            : base(factory, NullLogger<XuiV3RenewalOperationStore>.Instance)
        {
        }

        public override async Task MarkSettledAsync(XuiV3RenewalOperation operation, CancellationToken cancellationToken = default)
        {
            if (FailSettlement)
                throw new InvalidOperationException("synthetic post-commit settlement failure");
            await base.MarkSettledAsync(operation, cancellationToken);
        }
    }

    private static ServiceProvider RenewalProvider(Databases databases, XuiV3RenewalOperationStore renewalStore)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:id"] = "main",
                ["bots:0:username"] = "main_bot",
                ["bots:0:token"] = "10001:" + new string('a', 35),
                ["bots:0:enabled"] = "true",
                ["bots:0:isDefault"] = "true",
                ["salesAssistantBot:id"] = "assistant",
                ["salesAssistantBot:username"] = "assistant_bot",
                ["salesAssistantBot:token"] = "10002:" + new string('a', 35),
                ["salesAssistantBot:enabled"] = "true",
                ["GozargahSiteSyncEnabled"] = "false",
                ["GozargahSiteWalletPaymentsEnabled"] = "false",
                ["XuiV3ApiBaseUrl"] = "http://127.0.0.1:1",
                ["XuiV3ApiToken"] = "test-only"
            }).Build();
        var appConfig = configuration.Get<AppConfig>()!;
        appConfig.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db");
        appConfig.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection();
        Program.RegisterApplicationServices(services, configuration, appConfig, databases.DirectoryPath);
        var registry = new BotRegistry(configuration);
        var clients = new ConcurrentDictionary<string, StorefrontClient>(StringComparer.OrdinalIgnoreCase);
        var botClientProvider = new BotClientProvider(registry, bot => clients.GetOrAdd(bot.Id, _ => new StorefrontClient()));
        services.AddSingleton(registry);
        services.AddSingleton(botClientProvider);
        services.AddSingleton(renewalStore);
        return services.BuildServiceProvider();
    }

    private static async Task<int> SeedRenewalOrderAsync(Databases databases, string publicOrderId)
    {
        await using var db = databases.Users.CreateDbContext();
        var order = new TenantBotOrder
        {
            OrderId = publicOrderId,
            TenantBotId = "tenant-reset",
            TenantBotUsername = "reset_store",
            OwnerTelegramUserId = 711,
            CustomerTelegramUserId = 7468859738,
            CustomerChatId = 7468859738,
            SalePriceToman = 150_000,
            BaseCostToman = 100_000,
            ProfitToman = 50_000,
            PaymentProvider = "hooshpay",
            PaymentStatus = TenantBotOrderStatuses.Paid,
            IsFulfilled = false,
            OrderKind = TenantBotOrderKinds.Renew,
            ServiceKey = "tenant-service",
            TargetAccountEmail = "client@example.test",
            TargetAccountUuid = "uuid-1",
            CreatedAtUtc = DateTime.UtcNow
        };
        db.TenantBotOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task SeedAppliedRenewalOperationAsync(Databases databases, string operationKey, string publicOrderId)
    {
        await using var db = databases.Users.CreateDbContext();
        db.XuiV3RenewalOperations.Add(new XuiV3RenewalOperation
        {
            OperationKey = operationKey,
            OperationId = operationKey,
            BotId = "tenant-reset",
            TenantBotId = "tenant-reset",
            TenantBotOrderId = publicOrderId,
            TelegramUserId = 7468859738,
            TargetEmail = "client@example.test",
            TargetUuid = "uuid-1",
            ServiceKey = "tenant-service",
            AddedTrafficGb = 10,
            AddedTrafficBytes = 10L * 1024 * 1024 * 1024,
            AddedDurationDays = 30,
            PriceToman = 150_000,
            PaymentMethod = "tenant_order",
            Status = XuiV3RenewalOperationStatuses.Applied,
            SettlementStatus = XuiV3RenewalSettlementStatuses.Pending,
            AccountLockKey = "lock-" + operationKey,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}