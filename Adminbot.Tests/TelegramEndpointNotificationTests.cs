using System.Net;
using System.Reflection;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Repeated real-facade fences retain a durable wallet notification without HTTP or retry-budget loss.</summary>
    /// <returns>A task completing after one resumed Local delivery and a non-replaying subsequent scan.</returns>
    [Fact]
    public async Task Endpoint_payment_notification_fence_defers_without_http_then_delivers_once()
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        await SeedEndpointPaymentNotificationAsync(databases);
        using var worker = new PaymentSettlementNotificationWorker(databases.Users, clients,
            NullLogger<PaymentSettlementNotificationWorker>.Instance);
        gate.Fence("endpoint-a", 123456);
        for (var pause = 0; pause < 8; pause++)
        {
            Assert.Equal(1, await ProcessEndpointPaymentNotificationsAsync(worker));
            await using var db = databases.Users.CreateDbContext();
            var row = await db.PaymentSettlementNotifications.SingleAsync();
            Assert.Equal(PaymentSettlementNotificationStatuses.Pending, row.Status);
            Assert.Equal("endpoint_migration_pending", row.LastError);
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.ClaimToken);
            Assert.Null(row.LeaseUntilUtc);
            Assert.NotNull(row.NextAttemptAtUtc);
            Assert.Null(row.TelegramMessageId);
            row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Empty(http.Calls);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
        Assert.Equal(1, await ProcessEndpointPaymentNotificationsAsync(worker));
        Assert.Equal(0, await ProcessEndpointPaymentNotificationsAsync(worker));
        Assert.Single(http.Calls, x => x.Method == "sendMessage" && x.Endpoint == "local");
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.PaymentSettlementNotifications.SingleAsync();
        Assert.Equal(PaymentSettlementNotificationStatuses.Delivered, saved.Status);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Null(saved.LastError);
        Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
        Assert.Empty(await verify.ReferralPaymentEvents.ToListAsync());
    }

    /// <summary>A dispatch followed by a lost HTTP reply or server failure is durable uncertainty, never a retry.</summary>
    /// <param name="serverFailure">Whether Telegram returns a server-error envelope instead of losing the response.</param>
    /// <returns>A task completing after the second scan issues no further request.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_payment_notification_ambiguous_http_never_replays(bool serverFailure)
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        using var http = new EndpointNotificationAmbiguousHttp(serverFailure);
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        await SeedEndpointPaymentNotificationAsync(databases);
        using var worker = new PaymentSettlementNotificationWorker(databases.Users, clients,
            NullLogger<PaymentSettlementNotificationWorker>.Instance);
        Assert.Equal(1, await ProcessEndpointPaymentNotificationsAsync(worker));
        Assert.Equal(0, await ProcessEndpointPaymentNotificationsAsync(worker));
        Assert.Equal(1, http.Dispatches);
        await using var db = databases.Users.CreateDbContext();
        var saved = await db.PaymentSettlementNotifications.SingleAsync();
        Assert.Equal(PaymentSettlementNotificationStatuses.DeliveryUncertain, saved.Status);
        Assert.Null(saved.NextAttemptAtUtc);
        Assert.Null(saved.ClaimToken);
    }

    /// <summary>A tenant identity replacement remains terminal manual review even while its endpoint is fenced.</summary>
    /// <returns>A task completing after the real durable row is quarantined without HTTP.</returns>
    [Fact]
    public async Task Endpoint_payment_notification_replaced_identity_is_not_deferred()
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(999888),
            Type = BotInstanceTypes.Tenant, Enabled = true });
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Fence("endpoint-a", 999888);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance { Id = "endpoint-a", Type = BotInstanceTypes.Tenant,
                TelegramBotId = 999888, Token = EndpointRuntimeToken(999888), Enabled = true });
            db.PaymentSettlementNotifications.Add(PaymentSettlementNotification.CreateWalletCredit(
                "atlaspay", 4242, "endpoint-a", 711, 711, 100000, "synthetic wallet notification",
                DateTime.UtcNow, BotInstanceTypes.Tenant, 600000, 123456));
            await db.SaveChangesAsync();
        }
        using var worker = new PaymentSettlementNotificationWorker(databases.Users, clients,
            NullLogger<PaymentSettlementNotificationWorker>.Instance);
        Assert.Equal(1, await ProcessEndpointPaymentNotificationsAsync(worker));
        Assert.Equal(0, await ProcessEndpointPaymentNotificationsAsync(worker));
        Assert.Empty(http.Calls);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.PaymentSettlementNotifications.SingleAsync();
        Assert.Equal(PaymentSettlementNotificationStatuses.ManualReview, saved.Status);
        Assert.Equal("bot_identity_changed", saved.LastError);
        Assert.Null(saved.NextAttemptAtUtc);
    }

    /// <summary>The durable order send marker can only be reset after a proven no-HTTP endpoint refusal.</summary>
    /// <returns>A task completing after repeated pauses refund attempts and exactly one resumed send is acknowledged.</returns>
    [Fact]
    public async Task Endpoint_order_notification_fence_refunds_budget_and_clears_pre_dispatch_marker()
    {
        using var databases = new Databases();
        await SeedFulfilledOrderWithNotificationAsync(databases, "endpoint-order", TenantOrderNotificationKinds.CustomerAccountDelivery);
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        using var worker = new TenantOrderNotificationWorker(databases.Users,
            new EndpointNotificationSender(clients), NullLogger<TenantOrderNotificationWorker>.Instance);
        gate.Fence("endpoint-a", 123456);
        for (var pause = 0; pause < 8; pause++)
        {
            Assert.Equal(1, await worker.ProcessOnceAsync());
            await using var db = databases.Users.CreateDbContext();
            var row = await db.TenantOrderNotifications.SingleAsync();
            Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.SendStartedAtUtc);
            Assert.Null(row.ClaimToken);
            Assert.Null(row.LeaseUntilUtc);
            row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Empty(http.Calls);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Single(http.Calls, x => x.Method == "sendMessage");
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(TenantOrderNotificationStatuses.Delivered, (await verify.TenantOrderNotifications.SingleAsync()).Status);
        Assert.True((await verify.TenantBotOrders.SingleAsync()).IsFulfilled);
    }

    /// <summary>The durable operator receipt intent survives repeated actual facade fences without exhausting attempts.</summary>
    /// <returns>A task completing after one resumed message acknowledges the same durable intent.</returns>
    [Fact]
    public async Task Endpoint_receipt_notification_fence_refunds_budget_and_resumes_once()
    {
        using var databases = new Databases();
        var (_, receiptId) = await SeedFulfilledOrderWithNotificationAsync(databases, "endpoint-receipt", TenantOrderNotificationKinds.CustomerAccountDelivery);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TenantManualReceiptNotifications.Add(new TenantManualReceiptNotification { ReceiptId = receiptId,
                Status = TenantManualReceiptNotificationStatuses.Pending, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        using var worker = new TenantManualReceiptNotificationWorker(databases.Users,
            new EndpointNotificationSender(clients), NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        gate.Fence("endpoint-a", 123456);
        for (var pause = 0; pause < 8; pause++)
        {
            Assert.Equal(1, await worker.ProcessOnceAsync());
            await using var db = databases.Users.CreateDbContext();
            var row = await db.TenantManualReceiptNotifications.SingleAsync();
            Assert.Equal(TenantManualReceiptNotificationStatuses.Pending, row.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.ClaimToken);
            Assert.Null(row.LeaseUntilUtc);
            row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Empty(http.Calls);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Single(http.Calls, x => x.Method == "sendMessage");
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(TenantManualReceiptNotificationStatuses.Delivered, (await verify.TenantManualReceiptNotifications.SingleAsync()).Status);
    }

    /// <summary>The real receipt sender propagates fences without fallback and never replays ambiguous photo or text sends.</summary>
    /// <param name="mode">Zero pauses the photo route; one loses the photo response; two loses the fallback response.</param>
    /// <returns>A task completing after durable pending/delivered or terminal uncertain state is observed.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Endpoint_real_receipt_sender_preserves_fence_and_ambiguity(int mode)
    {
        using var databases = new Databases();
        var (_, receiptId) = await SeedFulfilledOrderWithNotificationAsync(databases,
            "endpoint-real-receipt", TenantOrderNotificationKinds.CustomerAccountDelivery);
        await using (var db = databases.Users.CreateDbContext())
        {
            var receipt = await db.TenantManualPaymentReceipts.SingleAsync(x => x.Id == receiptId);
            receipt.TenantBotId = "endpoint-a";
            db.TenantManualReceiptNotifications.Add(new TenantManualReceiptNotification { ReceiptId = receiptId,
                Status = TenantManualReceiptNotificationStatuses.Pending, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "endpoint-b", Token = EndpointRuntimeToken(654321),
            Type = BotInstanceTypes.SalesAssistant, Enabled = true });
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        gate.Publish(EndpointRuntimeState("endpoint-b", 654321, TelegramEndpointType.Cloud, 1));
        using var http = new EndpointReceiptHttp(mode);
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var services = new ServiceCollection();
        services.AddScoped(serviceProvider => new SalesAssistantService(databases.Users, registry, clients,
            serviceProvider, null!, NullLogger<SalesAssistantService>.Instance));
        using var provider = services.BuildServiceProvider();
        using var worker = new TenantManualReceiptNotificationWorker(databases.Users,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TenantManualReceiptNotificationWorker>.Instance);
        if (mode == 0) gate.Fence("endpoint-b", 654321);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.TenantManualReceiptNotifications.SingleAsync();
            if (mode == 0)
            {
                Assert.Equal(TenantManualReceiptNotificationStatuses.Pending, row.Status);
                Assert.Equal(0, row.AttemptCount);
                Assert.Equal(0, http.PhotoSends);
                Assert.Equal(0, http.TextSends);
                row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(TenantManualReceiptNotificationStatuses.DeliveryUncertain, row.Status);
                Assert.Null(row.NextAttemptAtUtc);
            }
        }
        gate.Publish(EndpointRuntimeState("endpoint-b", 654321, TelegramEndpointType.Local, 2));
        Assert.Equal(mode == 0 ? 1 : 0, await worker.ProcessOnceAsync());
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(1, http.PhotoSends);
        Assert.Equal(mode == 2 ? 1 : 0, http.TextSends);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(mode == 0 ? TenantManualReceiptNotificationStatuses.Delivered :
            TenantManualReceiptNotificationStatuses.DeliveryUncertain,
            (await verify.TenantManualReceiptNotifications.SingleAsync()).Status);
    }

    /// <summary>Creates a delivery-only owned-wallet outbox row without any financial settlement.</summary>
    /// <param name="databases">Independent SQLite fixture contexts.</param>
    /// <returns>A task completing after the pending row is durable.</returns>
    private static async Task SeedEndpointPaymentNotificationAsync(Databases databases)
    {
        await using var db = databases.Users.CreateDbContext();
        db.PaymentSettlementNotifications.Add(PaymentSettlementNotification.CreateWalletCredit(
            "atlaspay", 4242, "endpoint-a", 711, 711, 100000, "synthetic wallet notification",
            DateTime.UtcNow, BotInstanceTypes.Owned, 600000));
        await db.SaveChangesAsync();
    }

    /// <summary>Runs the existing payment worker claim/delivery methods without starting a background timer.</summary>
    /// <param name="worker">Worker using the real routed facade and durable SQLite outbox.</param>
    /// <returns>The number of due rows claimed and delivered or deferred.</returns>
    private static async Task<int> ProcessEndpointPaymentNotificationsAsync(PaymentSettlementNotificationWorker worker)
    {
        var claim = typeof(PaymentSettlementNotificationWorker).GetMethod("ClaimDueBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliver = typeof(PaymentSettlementNotificationWorker).GetMethod("DeliverClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rows = await (Task<IReadOnlyList<PaymentSettlementNotification>>)claim.Invoke(worker, new object[] { CancellationToken.None })!;
        foreach (var row in rows)
            await (Task)deliver.Invoke(worker, new object[] { row, CancellationToken.None })!;
        return rows.Count;
    }

    /// <summary>Exercises actual SDK dispatch through the same facade contract for both durable worker types.</summary>
    private sealed class EndpointNotificationSender(BotClientProvider clients) : ITenantOrderNotificationSender, ITenantManualReceiptNotificationSender
    {
        /// <summary>Sends one synthetic message through the real routed facade.</summary>
        /// <param name="order">Persisted fulfilled fixture order; not mutated.</param><param name="kind">Durable notification kind.</param>
        /// <param name="cancellationToken">Actual transport cancellation.</param><returns>The real fake-HTTP acknowledgement.</returns>
        public async Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken) =>
            (await clients.GetClient("endpoint-a").SendMessage(711, "synthetic order notification", cancellationToken: cancellationToken)).MessageId;
        /// <summary>Sends one synthetic operator notification through the real routed facade.</summary>
        /// <param name="receipt">Persisted fixture receipt; not mutated.</param><param name="cancellationToken">Actual transport cancellation.</param>
        /// <returns>The real fake-HTTP acknowledgement.</returns>
        public async Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken) =>
            (await clients.GetClient("endpoint-a").SendMessage(711, "synthetic receipt notification", cancellationToken: cancellationToken)).MessageId;
    }
    /// <summary>Provides real SDK file replies and controlled receipt notification ambiguity without network access.</summary>
    /// <param name="mode">Zero succeeds, one loses the photo reply, two definitively rejects photo then loses text reply.</param>
    private sealed class EndpointReceiptHttp(int mode) : HttpMessageHandler
    {
        /// <summary>Actual photo and fallback text request counts.</summary>
        public int PhotoSends, TextSends;
        /// <summary>Returns fixture downloads and Bot API envelopes, or loses an actual send acknowledgement.</summary>
        /// <param name="request">Actual SDK request, never logged.</param><param name="cancellationToken">Request cancellation.</param>
        /// <returns>A deterministic reply or post-dispatch failed task.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/file/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
            var method = path.Split('/')[^1];
            if (method == "getFile")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"ok\":true,\"result\":{\"file_id\":\"fixture\",\"file_unique_id\":\"fixture-unique\",\"file_path\":\"receipt.jpg\"}}",
                    Encoding.UTF8, "application/json") });
            if (method == "sendPhoto")
            {
                Interlocked.Increment(ref PhotoSends);
                if (mode == 1) return Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic photo acknowledgement lost"));
                if (mode == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(
                    "{\"ok\":false,\"error_code\":400,\"description\":\"synthetic definite photo rejection\"}", Encoding.UTF8, "application/json") });
            }
            else if (method == "sendMessage")
            {
                Interlocked.Increment(ref TextSends);
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic fallback acknowledgement lost"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"ok\":true,\"result\":{\"message_id\":4242,\"date\":1,\"chat\":{\"id\":711,\"type\":\"private\"}}}",
                Encoding.UTF8, "application/json") });
        }
    }


    /// <summary>Records a real SDK dispatch then simulates an ambiguous response, without contacting any service.</summary>
    /// <param name="serverFailure">Whether to return a Bot API 500 envelope instead of losing the HTTP response.</param>
    private sealed class EndpointNotificationAmbiguousHttp(bool serverFailure) : HttpMessageHandler
    {
        /// <summary>Number of actual requests received, including any forbidden retry.</summary>
        public int Dispatches;
        /// <summary>Simulates a request that may have reached Telegram before its response failed.</summary>
        /// <param name="request">Actual SDK HTTP request.</param><param name="cancellationToken">Actual request cancellation.</param>
        /// <returns>A server-error envelope or a failed task after dispatch.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Dispatches);
            return serverFailure
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(
                    "{\"ok\":false,\"error_code\":500,\"description\":\"synthetic ambiguous server failure\"}", Encoding.UTF8, "application/json") })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic lost acknowledgement"));
        }
    }
}
