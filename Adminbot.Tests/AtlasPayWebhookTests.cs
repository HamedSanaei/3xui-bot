using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed partial class ConcurrencyTests
{
    [Fact]
    public void AtlasPay_webhook_signature_is_raw_body_sensitive_and_fails_closed()
    {
        const string secret = "test-atlas-webhook-secret";
        const string body = "{\"event\":\"order.confirmed\",\"orderId\":77}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        Assert.True(AtlasPayWebhookSignature.Verify(body, signature, secret));
        Assert.False(AtlasPayWebhookSignature.Verify(body + " ", signature, secret));
        Assert.False(AtlasPayWebhookSignature.Verify(body, "not-hex", secret));
        Assert.False(AtlasPayWebhookSignature.Verify(body, signature, ""));
    }

    [Fact]
    public async Task AtlasPay_webhook_accepts_valid_identity_but_never_trusts_payload_as_payment_proof()
    {
        using var databases = new Databases();
        var config = AtlasWebhookConfiguration();
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId = 99001;
            payment.ChatId = 99001;
            payment.BotId = "main";
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus = "awaiting_payment";
            payment.NextInquiryAtUtc = DateTime.UtcNow.AddHours(1);
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var reconciler = new AtlasPayReconciliationHostedService(
            config, databases.Users, new AtlasPay(config, new HttpClient(new AtlasHttpHandler(
                (_, _, _, _) => throw new InvalidOperationException("webhook action must not call provider inline")))),
            null!, NullLogger<AtlasPayReconciliationHostedService>.Instance);
        var controller = new PaymentController(
            databases.Users, config, null!, null!, null!, null!, null!, reconciler, null!,
            NullLogger<PaymentController>.Instance);
        const string body = "{\"event\":\"order.confirmed\",\"orderId\":77,\"merchantOrderRef\":\"AtlasPay-test\",\"totalAmountToman\":250123,\"status\":\"confirmed\",\"timestamp\":\"2026-09-23T12:00:00Z\"}";
        AttachAtlasWebhook(controller, body, SignAtlasWebhook(body));

        var result = await controller.ReceiveAtlasPayWebhook(default);
        Assert.IsType<OkObjectResult>(result);

        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.False(saved.IsAddedToBalance);
        Assert.Equal("awaiting_payment", saved.ProviderStatus);
        Assert.Equal(AtlasPaySettlementStates.Pending, saved.SettlementState);
        Assert.Equal("order.confirmed", saved.WebhookEvent);
        Assert.NotNull(saved.WebhookReceivedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc), saved.WebhookProviderTimestampUtc);
        Assert.Null(saved.WebhookProcessedAtUtc);
    }

    [Fact]
    public async Task AtlasPay_webhook_rejects_invalid_signature_and_identity_mismatch()
    {
        using var databases = new Databases();
        var config = AtlasWebhookConfiguration();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.AtlasPayPaymentInfos.Add(VerifiedAtlasPayment());
            await db.SaveChangesAsync();
        }
        var reconciler = new AtlasPayReconciliationHostedService(
            config, databases.Users, new AtlasPay(config, new HttpClient(new AtlasHttpHandler(
                (_, _, _, _) => throw new InvalidOperationException()))), null!,
            NullLogger<AtlasPayReconciliationHostedService>.Instance);
        var controller = new PaymentController(
            databases.Users, config, null!, null!, null!, null!, null!, reconciler, null!,
            NullLogger<PaymentController>.Instance);

        const string validBody = "{\"event\":\"order.confirmed\",\"orderId\":77,\"merchantOrderRef\":\"AtlasPay-test\",\"totalAmountToman\":250123,\"status\":\"confirmed\",\"timestamp\":\"2026-09-23T12:00:00Z\"}";
        AttachAtlasWebhook(controller, validBody, "00");
        Assert.IsType<UnauthorizedObjectResult>(await controller.ReceiveAtlasPayWebhook(default));

        const string mismatchedBody = "{\"event\":\"order.confirmed\",\"orderId\":77,\"merchantOrderRef\":\"wrong-ref\",\"totalAmountToman\":250123,\"status\":\"confirmed\",\"timestamp\":\"2026-09-23T12:00:00Z\"}";
        AttachAtlasWebhook(controller, mismatchedBody, SignAtlasWebhook(mismatchedBody));
        Assert.IsType<ConflictObjectResult>(await controller.ReceiveAtlasPayWebhook(default));
    }

    [Fact]
    public async Task AtlasPay_webhook_queue_wakes_reconciliation_before_next_poll_is_due()
    {
        using var databases = new Databases();
        var config = AtlasWebhookConfiguration();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AtlasHttpHandler((_, _, _, _) =>
        {
            requested.TrySetResult();
            return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK, StatusJson("awaiting_payment")));
        });
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.NextInquiryAtUtc = DateTime.UtcNow.AddHours(1);
            payment.WebhookEvent = "order.confirmed";
            payment.WebhookReceivedAtUtc = DateTime.UtcNow;
            payment.WebhookProviderTimestampUtc = DateTime.UtcNow;
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var worker = new AtlasPayReconciliationHostedService(
            config, databases.Users, new AtlasPay(config, new HttpClient(handler)), null!,
            NullLogger<AtlasPayReconciliationHostedService>.Instance);
        await worker.StartAsync(default);
        try
        {
            Assert.True(worker.TryQueueWebhookTrigger(paymentId));
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(handler.Captures);
            Assert.Equal(HttpMethod.Get, handler.Captures[0].Method);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task AtlasPay_webhook_primary_due_scan_recovers_missing_webhook_with_bounded_polling()
    {
        using var databases = new Databases();
        var config = AtlasWebhookConfiguration();
        var handler = new AtlasHttpHandler((_, _, _, _) =>
            Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK, StatusJson("awaiting_payment"))));

        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.NextInquiryAtUtc = null; // legacy webhook-only rows were stranded in exactly this state
            payment.WebhookEvent = null;
            payment.WebhookReceivedAtUtc = null;
            payment.WebhookProcessedAtUtc = null;
            payment.ReconciliationState = AtlasPayReconciliationStates.Active;
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var worker = new AtlasPayReconciliationHostedService(
            config,
            databases.Users,
            new AtlasPay(config, new HttpClient(handler)),
            null!,
            NullLogger<AtlasPayReconciliationHostedService>.Instance);

        await worker.ReconcileDueAsync();

        var capture = Assert.Single(handler.Captures);
        Assert.Equal(HttpMethod.Get, capture.Method);
        Assert.Contains("/orders/77", capture.Uri, StringComparison.Ordinal);

        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.Equal(1, saved.InquiryAttemptCount);
        Assert.NotNull(saved.LastInquiryAtUtc);
        Assert.NotNull(saved.NextInquiryAtUtc);
        Assert.Equal(AtlasPayReconciliationStates.Active, saved.ReconciliationState);
        Assert.Null(saved.WebhookReceivedAtUtc);
    }

    [Fact]
    public void AtlasPay_webhook_primary_still_schedules_polling_fallback()
    {
        var config = AtlasWebhookConfiguration().Get<AppConfig>()!;
        var now = new DateTime(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);
        var next = AtlasPayPollingPolicy.GetInitialNextInquiryUtc(config, now);

        Assert.NotNull(next);
        Assert.Equal(now.AddSeconds(30), next);
        Assert.True(AtlasPayPollingPolicy.UsesWebhookPrimary(config));
    }

    [Fact]
    public async Task AtlasPay_webhook_source_without_durable_receipt_cannot_call_provider()
    {
        using var databases = new Databases();
        var config = AtlasWebhookConfiguration();
        var handler = new AtlasHttpHandler((_, _, _, _) =>
            throw new InvalidOperationException("provider must not be called without a durable signed webhook receipt"));

        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.NextInquiryAtUtc = DateTime.UtcNow.AddMinutes(-1);
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var worker = new AtlasPayReconciliationHostedService(
            config,
            databases.Users,
            new AtlasPay(config, new HttpClient(handler)),
            null!,
            NullLogger<AtlasPayReconciliationHostedService>.Instance);

        var result = await worker.ReconcilePaymentAsync(paymentId, "atlaspay-webhook");

        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
        Assert.Empty(handler.Captures);
    }

    private static IConfiguration AtlasWebhookConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["atlasPayEnabled"] = "true",
            ["atlasPayApiKey"] = "test-atlas-key",
            ["atlasPayWebhookSecret"] = "test-atlas-webhook-secret",
            ["atlasPayBaseUrl"] = "https://api.atlaspay.space/api/v1",
            ["atlasPayReconciliationIntervalSeconds"] = "30",
            ["atlasPayReconciliationMaxAttempts"] = "50",
            ["atlasPayReconciliationBatchSize"] = "50"
        }).Build();

    private static void AttachAtlasWebhook(PaymentController controller, string body, string signature)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["X-Webhook-Signature"] = signature;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private static string SignAtlasWebhook(string body)
        => Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test-atlas-webhook-secret"),
            Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
