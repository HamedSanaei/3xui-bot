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
