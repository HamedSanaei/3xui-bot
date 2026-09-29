using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Exercises signed callback admission through the real wallet settlement path, including duplicate delivery.</summary>
    /// <returns>A task verifying that only the official provider inquiry credits the wallet once.</returns>
    /// <remarks>The callback arrives before the scheduled fallback inquiry; the signed payload is a hint, not financial evidence.</remarks>
    [Fact]
    public async Task AtlasPay_signed_callback_confirms_owned_wallet_via_provider_inquiry_once()
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            await using var scope = provider.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
            await credentials.AddEmptyUser(99002);
            int paymentId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = VerifiedAtlasPayment();
                payment.TelegramUserId = 99002;
                payment.ChatId = 99002;
                payment.BotId = "main";
                payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
                payment.NextInquiryAtUtc = DateTime.UtcNow.AddHours(1);
                db.AtlasPayPaymentInfos.Add(payment);
                await db.SaveChangesAsync();
                paymentId = payment.Id;
            }

            var config = AtlasWebhookConfiguration();
            var handler = new AtlasHttpHandler((_, _, _, _) =>
                Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK, StatusJson("confirmed", paid: true, actual: 250000))));
            var reconciler = new AtlasPayReconciliationHostedService(
                config, databases.Users, new AtlasPay(config, new HttpClient(handler)),
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AtlasPayReconciliationHostedService>.Instance);
            var controller = new PaymentController(
                databases.Users, config, null!, null!, null!, null!, null!, reconciler, null!,
                NullLogger<PaymentController>.Instance);
            const string body = "{\"event\":\"order.confirmed\",\"orderId\":77,\"merchantOrderRef\":\"AtlasPay-test\",\"totalAmountToman\":250123,\"status\":\"confirmed\",\"timestamp\":\"2026-09-23T12:00:00Z\"}";
            AttachAtlasWebhook(controller, body, SignAtlasWebhook(body));
            Assert.IsType<OkObjectResult>(await controller.ReceiveAtlasPayWebhook(default));
            Assert.Equal(0, await credentials.GetAccountBalance(99002));

            await reconciler.ReconcileDueAsync();
            Assert.Equal(250000, await credentials.GetAccountBalance(99002));
            Assert.Single(handler.Captures);
            await using (var db = databases.Users.CreateDbContext())
            {
                var saved = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
                Assert.True(saved.IsAddedToBalance);
                Assert.Equal(AtlasPaySettlementStates.Settled, saved.SettlementState);
                Assert.NotNull(saved.WebhookProcessedAtUtc);
                Assert.True(saved.WebhookProcessedAtUtc >= saved.WebhookReceivedAtUtc);
                Assert.Equal(1, await db.WalletLedgerEntries.CountAsync(x => x.Provider == "atlaspay" && x.ReferenceId == paymentId.ToString()));
            }

            AttachAtlasWebhook(controller, body, SignAtlasWebhook(body));
            Assert.IsType<OkObjectResult>(await controller.ReceiveAtlasPayWebhook(default));
            await reconciler.ReconcileDueAsync();
            Assert.Equal(250000, await credentials.GetAccountBalance(99002));
            Assert.Single(handler.Captures);
        }
    }

    /// <summary>Accepts an authentic documented rejection as a durable hint without treating it as financial proof.</summary>
    /// <returns>A task checking exact-body HMAC admission and unchanged wallet/settlement state.</returns>
    /// <remarks>The optional rejection reason is untrusted payment data; only a later provider inquiry can settle an order.</remarks>
    [Fact]
    public async Task AtlasPay_signed_rejected_event_persists_hint_without_financial_effect()
    {
        using var databases = new Databases();
        var config = AtlasWebhookConfiguration();
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.ProviderStatus = "awaiting_payment";
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var reconciler = new AtlasPayReconciliationHostedService(
            config, databases.Users, new AtlasPay(config, new HttpClient(new AtlasHttpHandler(
                (_, _, _, _) => throw new InvalidOperationException("webhook must not inquire inline")))),
            null!, NullLogger<AtlasPayReconciliationHostedService>.Instance);
        var controller = new PaymentController(
            databases.Users, config, null!, null!, null!, null!, null!, reconciler, null!,
            NullLogger<PaymentController>.Instance);
        const string body = "{ \"timestamp\":\"2026-09-29T12:11:15.000Z\", \"reason\":\"invalid receipt\", \"status\":\"rejected\", \"totalAmountToman\":250123, \"merchantOrderRef\":\"AtlasPay-test\", \"orderId\":77, \"event\":\"order.rejected\" }";
        AttachAtlasWebhook(controller, body, SignAtlasWebhook(body));

        Assert.IsType<OkObjectResult>(await controller.ReceiveAtlasPayWebhook(default));

        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.Equal("order.rejected", saved.WebhookEvent);
        Assert.NotNull(saved.WebhookReceivedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 29, 12, 11, 15, DateTimeKind.Utc), saved.WebhookProviderTimestampUtc);
        Assert.Equal("awaiting_payment", saved.ProviderStatus);
        Assert.False(saved.IsAddedToBalance);
        Assert.Equal(AtlasPaySettlementStates.Pending, saved.SettlementState);
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
        var diagnostics = new DiagnosticLogger<PaymentController>();
        var controller = new PaymentController(
            databases.Users, config, null!, null!, null!, null!, null!, reconciler, null!,
            diagnostics);

        const string validBody = "{\"event\":\"order.confirmed\",\"orderId\":77,\"merchantOrderRef\":\"AtlasPay-test\",\"totalAmountToman\":250123,\"status\":\"confirmed\",\"timestamp\":\"2026-09-23T12:00:00Z\"}";
        AttachAtlasWebhook(controller, validBody, "");
        Assert.IsType<UnauthorizedObjectResult>(await controller.ReceiveAtlasPayWebhook(default));
        AttachAtlasWebhook(controller, validBody, "00");
        Assert.IsType<UnauthorizedObjectResult>(await controller.ReceiveAtlasPayWebhook(default));
        Assert.Contains(diagnostics.Messages(Microsoft.Extensions.Logging.LogLevel.Warning),
            warning => warning.Contains("SignatureHeaderPresent=False", StringComparison.Ordinal));
        Assert.Contains(diagnostics.Messages(Microsoft.Extensions.Logging.LogLevel.Warning),
            warning => warning.Contains("SignatureHeaderPresent=True", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Messages(Microsoft.Extensions.Logging.LogLevel.Warning),
            warning => warning.Contains("test-atlas-webhook-secret", StringComparison.Ordinal));

        const string mismatchedBody = "{\"event\":\"order.confirmed\",\"orderId\":77,\"merchantOrderRef\":\"wrong-ref\",\"totalAmountToman\":250123,\"status\":\"confirmed\",\"timestamp\":\"2026-09-23T12:00:00Z\"}";
        AttachAtlasWebhook(controller, mismatchedBody, SignAtlasWebhook(mismatchedBody));
        Assert.IsType<ConflictObjectResult>(await controller.ReceiveAtlasPayWebhook(default));

        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync();
        Assert.Null(saved.WebhookReceivedAtUtc);
        Assert.Null(saved.WebhookEvent);
        Assert.False(saved.IsAddedToBalance);
        Assert.Equal(AtlasPaySettlementStates.Pending, saved.SettlementState);
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
