using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;
using System.Net;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task AtlasPay_create_uses_documented_contract_and_parses_safe_fields()
    {
        var handler = new AtlasHttpHandler((_, request, body, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            "{\"success\":true,\"orderId\":42,\"trackingCode\":\"TRK-42\",\"totalAmountToman\":250123," +
            "\"cardNumberMasked\":\"6037-****-1234\",\"cardNumber\":\"6037999999991234\"," +
            "\"paymentDeadlineAt\":\"2026-09-10T12:00:00Z\",\"customerStartLink\":\"https://t.me/atlaspay_bot/start?start=x\"}")));
        var atlas = new AtlasPay(AtlasConfiguration(), new HttpClient(handler));
        var result = await atlas.CreateOrderAsync("AtlasPay-abc", 250000, 123456789);
        var request = Assert.Single(handler.Captures);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.atlaspay.space/api/v1/orders", request.Uri);
        Assert.Equal("test-atlas-key", request.ApiKey);
        Assert.DoesNotContain("test-atlas-key", request.Body, StringComparison.Ordinal);
        var json = JObject.Parse(request.Body!);
        Assert.Equal(250000, json.Value<long>("baseAmountToman"));
        Assert.Equal(123456789, json.Value<long>("customerTelegramId"));
        Assert.Equal("AtlasPay-abc", json.Value<string>("merchantOrderRef"));
        Assert.Equal(42, result.OrderId); Assert.Equal("TRK-42", result.TrackingCode);
        Assert.Equal(250123, result.TotalAmountToman); Assert.Equal("6037-****-1234", result.CardNumberMasked);
        Assert.Null(typeof(AtlasPayPaymentInfo).GetProperty("CardNumber"));
    }
    [Theory]
    [InlineData(500, false)]
    [InlineData(400, true)]
    [InlineData(401, true)]
    public async Task AtlasPay_create_http_failure_is_single_attempt_and_classified(int status, bool definitive)
    {
        var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse((HttpStatusCode)status, "{\"error\":\"x\"}")));
        var atlas = new AtlasPay(AtlasConfiguration(), new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AtlasPayApiException>(() => atlas.CreateOrderAsync("AtlasPay-one", 250000, 123));
        Assert.Single(handler.Captures);
        Assert.Equal(definitive, AtlasPay.IsDefinitiveCreateFailure(ex));
        Assert.DoesNotContain("test-atlas-key", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtlasPay_create_transport_failure_is_not_retried()
    {
        var handler = new AtlasHttpHandler((_, _, _, _) => throw new HttpRequestException("offline"));
        var atlas = new AtlasPay(AtlasConfiguration(retries: 5), new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AtlasPayApiException>(() => atlas.CreateOrderAsync("AtlasPay-net", 250000, 123));
        Assert.Single(handler.Captures);
        Assert.False(AtlasPay.IsDefinitiveCreateFailure(ex));
    }

    [Fact]
    public async Task AtlasPay_inquiry_retries_transient_get_only()
    {
        var handler = new AtlasHttpHandler((attempt, _, _, _) => Task.FromResult(attempt < 3
            ? JsonResponse(HttpStatusCode.ServiceUnavailable, "{}")
            : JsonResponse(HttpStatusCode.OK, StatusJson("awaiting_payment"))));
        var atlas = new AtlasPay(AtlasConfiguration(retries: 2), new HttpClient(handler));
        var result = await atlas.GetOrderAsync(77);
        Assert.Equal("awaiting_payment", result.Status);
        Assert.Equal(3, handler.Captures.Count);
        Assert.All(handler.Captures, x => Assert.Equal(HttpMethod.Get, x.Method));
    }
    [Fact]
    public async Task AtlasPay_malformed_inquiry_never_produces_payment_evidence()
    {
        var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{")));
        var atlas = new AtlasPay(AtlasConfiguration(), new HttpClient(handler));
        await Assert.ThrowsAsync<AtlasPayApiException>(() => atlas.GetOrderAsync(77));
        Assert.Single(handler.Captures);
    }

    [Theory]
    [InlineData("awaiting_payment")]
    [InlineData("admin_review")]
    [InlineData("underpaid_review")]
    [InlineData("underpaid_awaiting_remainder")]
    [InlineData("rejected")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public void AtlasPay_non_success_statuses_never_auto_settle(string status)
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse(status, paid: false, manual: false);
        Assert.False(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out var manual));
        Assert.False(manual); Assert.Equal("provider_not_paid", error);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("settled")]
    public void AtlasPay_full_success_statuses_are_eligible_when_identity_matches(string status)
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse(status, paid: true, manual: false);
        Assert.True(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out var manual));
        Assert.Null(error); Assert.False(manual);
    }
    [Fact]
    public void AtlasPay_requires_manual_delivery_blocks_even_paid_success()
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse("confirmed", paid: true, manual: true);
        response.ActualReceivedAmountToman = 200000;
        Assert.False(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out var manual));
        Assert.True(manual); Assert.Equal("requires_manual_delivery", error);
    }

    [Theory]
    [InlineData("provider_order_id_mismatch")]
    [InlineData("merchant_order_ref_mismatch")]
    [InlineData("total_amount_mismatch")]
    public void AtlasPay_identity_mismatch_never_settles(string expected)
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse("confirmed", paid: true, manual: false);
        if (expected == "provider_order_id_mismatch") response.Id++;
        if (expected == "merchant_order_ref_mismatch") response.MerchantOrderRef = "other";
        if (expected == "total_amount_mismatch") response.TotalAmountToman++;
        Assert.False(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out _));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void AtlasPay_tenant_message_uses_provider_total_and_tracking_not_provider_id()
    {
        var order = new TenantBotOrder { SalePriceToman = 250000, OrderId = "tenant-order" };
        var payment = VerifiedAtlasPayment(); payment.ProviderOrderId = 987654321; payment.BaseAmountToman = 250000; payment.TotalAmountToman = 250123;
        payment.PaymentDeadlineAtUtc = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var text = TenantBotService.BuildTenantAtlasPayPaymentText(order, payment);
        Assert.Contains(250123L.FormatCurrency(), text);
        Assert.Contains(payment.TrackingCode!, text);
        Assert.DoesNotContain(payment.ProviderOrderId!.Value.ToString(), text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task AtlasPay_owned_settlement_credits_base_amount_exactly_once_with_ledger_and_notification()
    {
        using var databases = new Databases();
        var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            await using var scope = provider.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
            await credentials.AddEmptyUser(7711);
            int paymentId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = VerifiedAtlasPayment();
                payment.TelegramUserId = 7711; payment.ChatId = 7711; payment.BotId = "main";
                payment.BaseAmountToman = 250000; payment.TotalAmountToman = 250123;
                payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
                payment.ProviderStatus = "confirmed"; payment.PaidAtUtc = DateTime.UtcNow;
                payment.SettlementState = AtlasPaySettlementStates.Pending;
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId = payment.Id;
            }
            var settlement = scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>();
            var calls = Enumerable.Range(0, 8).Select(_ => settlement.ApplyOfficialPaymentAsync(new AtlasPayPaymentInfo { Id = paymentId }, "test"));
            await Task.WhenAll(calls);
            Assert.Equal(250000, await credentials.GetAccountBalance(7711));
            await using var users = databases.Users.CreateDbContext();
            var saved = await users.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
            Assert.True(saved.IsAddedToBalance); Assert.Equal(250000, saved.BalanceAfter);
            Assert.Equal(1, await users.WalletLedgerEntries.CountAsync(x => x.Provider == "atlaspay" && x.ReferenceId == paymentId.ToString()));
            Assert.Equal(1, await users.PaymentSettlementNotifications.CountAsync(x => x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
            await using var creds = databases.Credentials.CreateDbContext();
            Assert.Equal(1, await creds.WalletOperations.CountAsync(x => x.OperationKey == $"payment:atlaspay:{paymentId}:credit"));
        }
    }
    [Fact]
    public async Task AtlasPay_manual_delivery_never_credits_owned_wallet()
    {
        using var databases = new Databases(); var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            await using var scope = provider.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>(); await credentials.AddEmptyUser(7712);
            int id;
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = VerifiedAtlasPayment(); payment.TelegramUserId = 7712; payment.ChatId = 7712; payment.BotId = "main";
                payment.BaseAmountToman = 250000; payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
                payment.ProviderStatus = "confirmed"; payment.PaidAtUtc = DateTime.UtcNow; payment.RequiresManualDelivery = true;
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); id = payment.Id;
            }
            var result = await scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>()
                .ApplyOfficialPaymentAsync(new AtlasPayPaymentInfo { Id = id }, "test");
            Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
            Assert.Equal(0, await credentials.GetAccountBalance(7712));
            await using var verify = databases.Users.CreateDbContext(); Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
        }
    }

    [Fact]
    public async Task AtlasPay_global_availability_requires_key_https_and_persists_only_enabled_flag()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AtlasPayToggle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "configuration.json"); await System.IO.File.WriteAllTextAsync(path, "{\"atlasPayEnabled\":false,\"untouched\":true}");
            var configured = new AppConfig { AtlasPayEnabled = false, AtlasPayApiKey = "test-atlas-key", AtlasPayBaseUrl = "https://api.atlaspay.space/api/v1" };
            var service = new PaymentGatewayAvailabilityService(configured, path, NullLogger<PaymentGatewayAvailabilityService>.Instance);
            Assert.True(service.IsConfigured(PaymentGateway.AtlasPay)); Assert.False(service.Snapshot.AtlasPayEnabled);
            var result = await service.SetEnabledAsync(PaymentGateway.AtlasPay, true, service.Snapshot.Revision);
            Assert.True(result.Applied); Assert.True(result.Snapshot.AtlasPayEnabled);
            var file = await System.IO.File.ReadAllTextAsync(path); Assert.Contains("\"atlasPayEnabled\":true", file); Assert.Contains("\"untouched\":true", file);
            Assert.DoesNotContain("test-atlas-key", file);
            Assert.False(new PaymentGatewayAvailabilityService(new AppConfig { AtlasPayEnabled=true, AtlasPayApiKey="x", AtlasPayBaseUrl="http://insecure" }, path, NullLogger<PaymentGatewayAvailabilityService>.Instance).IsConfigured(PaymentGateway.AtlasPay));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task AtlasPay_reconciliation_persists_underpayment_and_enters_manual_review_without_credit()
    {
        using var databases = new Databases(); var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int id;
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = VerifiedAtlasPayment(); payment.TelegramUserId = 8801; payment.ChatId = 8801; payment.BotId = "main";
                payment.BaseAmountToman = 250000; payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
                payment.ProviderStatus = "awaiting_payment"; payment.NextInquiryAtUtc = DateTime.UtcNow.AddMinutes(-1);
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); id = payment.Id;
            }
            var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
                StatusJson("confirmed", paid:true, manual:true, actual:200000))));
            var config = AtlasConfiguration(); var atlas = new AtlasPay(config, new HttpClient(handler));
            var reconciler = new AtlasPayReconciliationHostedService(config, databases.Users, atlas,
                provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AtlasPayReconciliationHostedService>.Instance);
            var result = await reconciler.ReconcilePaymentAsync(id, "test", false);
            Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
            await using var verify = databases.Users.CreateDbContext(); var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == id);
            Assert.Equal(200000, saved.ActualReceivedAmountToman); Assert.True(saved.RequiresManualDelivery);
            Assert.Equal(AtlasPaySettlementStates.ManualReview, saved.SettlementState); Assert.False(saved.IsAddedToBalance);
            await using var creds = databases.Credentials.CreateDbContext(); Assert.Empty(await creds.WalletOperations.ToListAsync());
        }
    }
    [Fact]
    public async Task AtlasPay_existing_payment_inquiry_ignores_current_global_and_tenant_creation_switches()
    {
        using var databases = new Databases(); var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int id;
            await using (var db = databases.Users.CreateDbContext())
            {
                db.BotInstances.Add(new BotInstance { Id="tenant-atlas-off", Type=BotInstanceTypes.Tenant, Enabled=true,
                    OwnerTelegramUserId=1, TenantAtlasPayEnabled=false, CreatedAtUtc=DateTime.UtcNow });
                var payment = VerifiedAtlasPayment(); payment.TelegramUserId=8802; payment.ChatId=8802; payment.BotId="tenant-atlas-off";
                payment.PaymentPurpose=TenantBotPaymentPurposes.TenantOrder; payment.TenantBotOrderId=123;
                payment.ProviderStatus="awaiting_payment"; payment.NextInquiryAtUtc=DateTime.UtcNow.AddMinutes(-1);
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); id=payment.Id;
            }
            var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, StatusJson("awaiting_payment"))));
            var config = AtlasConfiguration(enabled:false); var reconciler = new AtlasPayReconciliationHostedService(config, databases.Users,
                new AtlasPay(config, new HttpClient(handler)), provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AtlasPayReconciliationHostedService>.Instance);
            await reconciler.ReconcilePaymentAsync(id, "switches-off", false);
            Assert.Single(handler.Captures);
            await using var verify = databases.Users.CreateDbContext(); var saved=await verify.AtlasPayPaymentInfos.SingleAsync(x=>x.Id==id);
            Assert.Equal(1, saved.InquiryAttemptCount); Assert.Equal("awaiting_payment", saved.ProviderStatus);
        }
    }

    [Fact]
    public void AtlasPay_configuration_defaults_and_tenant_flag_match_contract()
    {
        var config = new AppConfig();
        Assert.False(config.AtlasPayEnabled); Assert.Equal("https://api.atlaspay.space/api/v1", config.AtlasPayBaseUrl);
        Assert.Equal(15, config.AtlasPayRequestTimeoutSeconds); Assert.Equal(3, config.AtlasPayInquiryRetryCount);
        Assert.Equal(30, config.AtlasPayReconciliationIntervalSeconds); Assert.Equal(50, config.AtlasPayReconciliationMaxAttempts);
        Assert.Equal(50, config.AtlasPayReconciliationBatchSize); Assert.True(new BotInstance().TenantAtlasPayEnabled);
    }    private static IConfiguration AtlasConfiguration(bool enabled = true, int retries = 0)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["atlasPayEnabled"] = enabled ? "true" : "false",
            ["atlasPayApiKey"] = "test-atlas-key",
            ["atlasPayBaseUrl"] = "https://api.atlaspay.space/api/v1",
            ["atlasPayRequestTimeoutSeconds"] = "15",
            ["atlasPayInquiryRetryCount"] = retries.ToString(),
            ["atlasPayReconciliationIntervalSeconds"] = "30",
            ["atlasPayReconciliationMaxAttempts"] = "50",
            ["atlasPayReconciliationBatchSize"] = "50"
        }).Build();

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string StatusJson(string status, bool? paid = false, bool manual = false, long? actual = null)
        => Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            success = true, id = 77, trackingCode = "TRK-77", merchantOrderRef = "AtlasPay-test",
            status, paid, totalAmountToman = 250123, actualReceivedAmountToman = actual,
            requiresManualDelivery = manual, createdAt = "2026-09-10T12:00:00Z"
        });
    private static AtlasPayPaymentInfo VerifiedAtlasPayment()
        => new()
        {
            MerchantOrderRef = "AtlasPay-test",
            ProviderOrderId = 77,
            TrackingCode = "TRK-77",
            CustomerStartLink = "https://t.me/atlaspay_bot/start?start=test",
            BaseAmountToman = 250000,
            TotalAmountToman = 250123,
            ProviderStatus = "awaiting_payment",
            CreationState = AtlasPayCreationStates.Created,
            SettlementState = AtlasPaySettlementStates.Pending,
            CreationAttemptCount = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static AtlasPayOrderStatusResponse VerifiedAtlasResponse(string status, bool? paid, bool manual)
        => new()
        {
            Success = true,
            Id = 77,
            TrackingCode = "TRK-77",
            MerchantOrderRef = "AtlasPay-test",
            Status = status,
            Paid = paid,
            TotalAmountToman = 250123,
            RequiresManualDelivery = manual
        };
    private sealed record AtlasCapture(HttpMethod Method, string Uri, string? ApiKey, string? Body);

    private sealed class AtlasHttpHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> _handler;
        private int _attempt;
        public List<AtlasCapture> Captures { get; } = new();

        public AtlasHttpHandler(Func<int, HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempt);
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("X-API-Key", out var values) ? values.SingleOrDefault() : null;
            lock (Captures)
                Captures.Add(new AtlasCapture(request.Method, request.RequestUri!.AbsoluteUri, apiKey, body));
            return await _handler(attempt, request, body, cancellationToken);
        }
    }
}

public sealed partial class ConcurrencyTests
{
    [Fact]
    public void AtlasPay_tracking_mismatch_never_settles()
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse("confirmed", true, false);
        response.TrackingCode = "TRK-other";
        Assert.False(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out _));
        Assert.Equal("tracking_code_mismatch", error);
    }

    [Fact]
    public void AtlasPay_unknown_status_never_settles()
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse("mystery_paid", true, false);
        Assert.False(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out _));
        Assert.Equal("unknown_provider_status", error);
    }

    [Fact]
    public async Task AtlasPay_verify_uses_documented_endpoint_and_is_not_retried()
    {
        var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "{}")));
        var atlas = new AtlasPay(AtlasConfiguration(retries: 5), new HttpClient(handler));
        await Assert.ThrowsAsync<AtlasPayApiException>(() => atlas.VerifyOrderAsync(77));
        var capture = Assert.Single(handler.Captures);
        Assert.Equal(HttpMethod.Post, capture.Method);
        Assert.Equal("https://api.atlaspay.space/api/v1/orders/77/verify", capture.Uri);
    }
    [Fact]
    public async Task AtlasPay_create_timeout_is_ambiguous_and_never_retried()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["atlasPayApiKey"] = "test-atlas-key",
            ["atlasPayBaseUrl"] = "https://api.atlaspay.space/api/v1",
            ["atlasPayRequestTimeoutSeconds"] = "1",
            ["atlasPayInquiryRetryCount"] = "5"
        }).Build();
        var handler = new AtlasHttpHandler(async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });
        var atlas = new AtlasPay(config, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AtlasPayApiException>(() => atlas.CreateOrderAsync("AtlasPay-timeout", 250000, 123));
        Assert.Single(handler.Captures);
        Assert.False(AtlasPay.IsDefinitiveCreateFailure(ex));
        var payment = AtlasPayPaymentInfo.CreateWalletCharge(123, 123, 250000);
        payment.BeginCreationAttempt(DateTime.UtcNow);
        payment.RecordCreationFailure(false, "ambiguous", DateTime.UtcNow);
        Assert.Equal(1, payment.CreationAttemptCount);
        Assert.Equal(AtlasPayCreationStates.Ambiguous, payment.CreationState);
    }

    [Fact]
    public async Task AtlasPay_malformed_successful_create_is_ambiguous_and_single_attempt()
    {
        var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"success\":true}")));
        var atlas = new AtlasPay(AtlasConfiguration(retries: 5), new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AtlasPayApiException>(() => atlas.CreateOrderAsync("AtlasPay-malformed", 250000, 123));
        Assert.Single(handler.Captures);
        Assert.False(AtlasPay.IsDefinitiveCreateFailure(ex));
    }
    [Fact]
    public async Task AtlasPay_due_worker_is_get_only_and_stops_at_attempt_cap()
    {
        using var databases = new Databases(); var (provider, _, _) = IncidentProvider(databases);
        await using (provider)
        {
            int dueId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = VerifiedAtlasPayment();
                payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
                payment.TelegramUserId = 9911; payment.ChatId = 9911; payment.BotId = "main";
                payment.NextInquiryAtUtc = DateTime.UtcNow.AddMinutes(-1); payment.InquiryAttemptCount = 49;
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); dueId = payment.Id;
            }
            var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, StatusJson("awaiting_payment"))));
            var config = AtlasConfiguration();
            var worker = new AtlasPayReconciliationHostedService(config, databases.Users, new AtlasPay(config, new HttpClient(handler)),
                provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AtlasPayReconciliationHostedService>.Instance);
            await worker.ReconcileDueAsync();
            var capture = Assert.Single(handler.Captures);
            Assert.Equal(HttpMethod.Get, capture.Method);
            Assert.EndsWith("/orders/77", capture.Uri, StringComparison.Ordinal);
            await using var verify = databases.Users.CreateDbContext();
            var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == dueId);
            Assert.Equal(50, saved.InquiryAttemptCount); Assert.Null(saved.NextInquiryAtUtc);
            await worker.ReconcileDueAsync(); Assert.Single(handler.Captures);
        }
    }

    [Fact]
    public void AtlasPay_startup_validation_allows_disabled_missing_secret_but_rejects_enabled_invalid_config()
    {
        var method = typeof(Program).GetMethod("ValidateAtlasPayConfiguration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, new object[] { new AppConfig { AtlasPayEnabled = false, AtlasPayApiKey = "" } });
        var missing = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { new AppConfig { AtlasPayEnabled = true, AtlasPayApiKey = "" } }));
        Assert.IsType<InvalidOperationException>(missing.InnerException);
        var http = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { new AppConfig { AtlasPayEnabled = true, AtlasPayApiKey = "x", AtlasPayBaseUrl = "http://unsafe" } }));
        Assert.IsType<InvalidOperationException>(http.InnerException);
        method.Invoke(null, new object[] { new AppConfig { AtlasPayEnabled = true, AtlasPayApiKey = "x", AtlasPayBaseUrl = "https://safe.example/api/v1" } });
    }
}

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task AtlasPay_tenant_purchase_persists_before_create_and_fulfills_exactly_once()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var xuiPosts = 0; JObject? xuiIdentity = null;
        var xuiBuilder = WebApplication.CreateBuilder();
        xuiBuilder.Logging.ClearProviders(); xuiBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var xui = xuiBuilder.Build();
        xui.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref xuiPosts);
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                xuiIdentity = (JObject?)JObject.Parse(body)["client"] ?? new JObject();
                xuiIdentity["inboundIds"] = new JArray(200);
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}"); return;
            }
            if (context.Request.Path.Value?.Contains("links", StringComparison.OrdinalIgnoreCase) == true)
            { await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}"); return; }
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = xuiIdentity ?? new JObject() }.ToString());
        });
        await xui.StartAsync();
        try
        {
            await RunAtlasTenantPurchaseScenarioAsync(databases, xui.Urls.Single(), () => Volatile.Read(ref xuiPosts));
        }
        finally { await xui.StopAsync(); }
    }
    private static async Task RunAtlasTenantPurchaseScenarioAsync(Databases databases, string xuiUrl, Func<int> xuiPosts)
    {
        var providerOrderId = 9001; var createPosts = 0; var prePersisted = false;
        string? merchantRef = null; long baseAmount = 0; long totalAmount = 0;
        var atlasHandler = new AtlasHttpHandler(async (_, request, body, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/orders", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref createPosts);
                var json = JObject.Parse(body!); merchantRef = json.Value<string>("merchantOrderRef");
                baseAmount = json.Value<long>("baseAmountToman"); totalAmount = baseAmount + 123;
                await using var db = databases.Users.CreateDbContext();
                var payment = await db.AtlasPayPaymentInfos.AsNoTracking().SingleOrDefaultAsync(x => x.MerchantOrderRef == merchantRef);
                var order = payment?.TenantBotOrderId is int orderId ? await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId) : null;
                prePersisted = payment != null && order?.AtlasPayPaymentInfoId == payment.Id && order.PaymentProvider == "atlaspay";
                return JsonResponse(HttpStatusCode.OK, Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success=true, orderId=providerOrderId, trackingCode="TRK-9001", totalAmountToman=totalAmount,
                    cardNumberMasked="6037-****-9001", paymentDeadlineAt="2026-09-10T12:00:00Z",
                    customerStartLink="https://t.me/atlaspay_bot/start?start=tenant"
                }));
            }
            return JsonResponse(HttpStatusCode.OK, Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success=true, id=providerOrderId, trackingCode="TRK-9001", merchantOrderRef=merchantRef,
                status="confirmed", paid=true, totalAmountToman=totalAmount, actualReceivedAmountToman=totalAmount,
                requiresManualDelivery=false, createdAt="2026-09-10T12:00:00Z"
            }));
        });
        var configuration = AtlasTenantConfiguration(databases, xuiUrl);
        var atlas = new AtlasPay(configuration, new HttpClient(atlasHandler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out var clients);
        await AssertAtlasDatabaseCoherenceAsync(databases, provider);
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.AddEmptyUser(711); await credentials.PromotOrDemote(711, true); await credentials.AddEmptyUser(722);
        var owner = await credentials.GetUserStatusWithId(711); var customer = await credentials.GetUserStatusWithId(722);
        var tenant = new BotInstance
        {
            Id="tenant-atlas-1", Username="atlas_store", Token="60001:" + new string('a',35), TelegramBotId=60001,
            Type=BotInstanceTypes.Tenant, Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=1,
            TenantPriceMarkupPercent=20, TenantAtlasPayEnabled=true, CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow
        };
        await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); await db.SaveChangesAsync(); }
        registry.Upsert(tenant);
        var client = clients.GetOrAdd(tenant.Id, _ => new StorefrontClient());
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var method = typeof(TenantBotService).GetMethod("CreateTenantAtlasPayInvoiceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var callback = new CallbackQuery { Id="atlas-create", From=new Telegram.Bot.Types.User { Id=722 }, Message=new Message { MessageId=1, Chat=new Chat { Id=722 } } };
        var selection = new XuiV3PurchaseSelection { ServiceKey="normal", TrafficGb=10, DurationKey="m1", AccountCount=1 };
        await (Task)method.Invoke(service, new object[] { client, callback, tenant, customer!, selection, CancellationToken.None })!;

        Assert.True(prePersisted); Assert.Equal(1, createPosts);
        int paymentId; int orderId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = await db.AtlasPayPaymentInfos.SingleAsync(); var order = await db.TenantBotOrders.SingleAsync();
            paymentId=payment.Id; orderId=order.Id;
            Assert.Equal(payment.Id, order.AtlasPayPaymentInfoId); Assert.Equal("atlaspay", order.PaymentProvider);
            Assert.Equal(TenantBotPaymentPurposes.TenantOrder, payment.PaymentPurpose); Assert.Equal(tenant.Id, payment.BotId);
            Assert.Equal(722, payment.TelegramUserId); Assert.Equal(order.Id, payment.TenantBotOrderId);
        }
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        var first = await reconciler.ReconcilePaymentAsync(paymentId, "atlas-tenant-test", false);
        await using (var diagnostic = databases.Users.CreateDbContext())
        {
            var d = await diagnostic.TenantBotOrders.AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.True(first.Status == NowPaymentsSettlementStatus.Applied, $"status={first.Status}; orderStatus={d.PaymentStatus}; error={d.ErrorMessage}; xuiPosts={xuiPosts()}");
        }
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
            var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
            Assert.True(order.IsFulfilled); Assert.Equal(TenantBotOrderStatuses.Fulfilled, order.PaymentStatus);
            Assert.True(payment.IsAddedToBalance); Assert.Equal(AtlasPaySettlementStates.Settled, payment.SettlementState);
            Assert.Equal(1, await db.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
            Assert.Equal(3, await db.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId));
            Assert.Empty(await db.Set<SiteWalletDebitOperation>().ToListAsync());
        }
        await using (var db = databases.Credentials.CreateDbContext())
        {
            Assert.Equal(1, await db.WalletOperations.CountAsync(x => x.OperationKey == $"tenant:{orderId}:profit"));
        }
        Assert.Equal(1, xuiPosts());
        var repeat1 = await reconciler.ReconcilePaymentAsync(paymentId, "worker-repeat", false);
        var repeat2 = await service.ApplyPaidTenantOrderAsync(new AtlasPayPaymentInfo { Id=paymentId, PaymentPurpose=TenantBotPaymentPurposes.TenantOrder,
            ProviderStatus="confirmed", PaidAtUtc=DateTime.UtcNow }, "customer-repeat");
        Assert.True(repeat1.Status is NowPaymentsSettlementStatus.AlreadyAdded or NowPaymentsSettlementStatus.ProviderNotPaid);
        Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded, repeat2.Status);
        await reconciler.ReconcileDueAsync();
        Assert.Equal(1, createPosts); Assert.Equal(1, xuiPosts());
        await using var final = databases.Users.CreateDbContext();
        Assert.Equal(1, await final.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
        Assert.Equal(3, await final.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId));
    }
    private static IConfiguration AtlasTenantConfiguration(Databases databases, string xuiUrl)
        => new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:id"]="main", ["bots:0:username"]="main_bot", ["bots:0:token"]="10001:" + new string('a',35),
                ["bots:0:enabled"]="true", ["bots:0:isDefault"]="true",
                ["userDatabasePath"]=Path.Combine(databases.DirectoryPath, "users.db"), ["credentialsDatabasePath"]=Path.Combine(databases.DirectoryPath, "credentials.db"),
                ["atlasPayEnabled"]="true", ["atlasPayApiKey"]="test-atlas-key", ["atlasPayBaseUrl"]="https://api.atlaspay.space/api/v1",
                ["XuiV3ApiBaseUrl"]=xuiUrl, ["XuiV3ApiToken"]="test-only",
                ["XuiV3ServicePlansPath"]=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json")),
                ["GozargahSiteSyncEnabled"]="false", ["GozargahSiteWalletPaymentsEnabled"]="false"
            }).Build();

    private static ServiceProvider AtlasTenantProvider(Databases databases, IConfiguration configuration, AtlasPay atlas,
        out BotRegistry registry, out System.Collections.Concurrent.ConcurrentDictionary<string, StorefrontClient> clients)
    {
        var appConfig = configuration.Get<AppConfig>()!;
        var expectedUsers = Path.GetFullPath(Path.Combine(databases.DirectoryPath, "users.db"));
        var expectedCredentials = Path.GetFullPath(Path.Combine(databases.DirectoryPath, "credentials.db"));
        Assert.Equal(expectedUsers, Path.GetFullPath(appConfig.UserDatabasePath));
        Assert.Equal(expectedCredentials, Path.GetFullPath(appConfig.CredentialsDatabasePath));
        UserDbContext.ConfigureDatabasePath(appConfig.UserDatabasePath);
        var services = new ServiceCollection(); Program.RegisterApplicationServices(services, configuration, appConfig, databases.DirectoryPath);
        services.AddSingleton(atlas);
        registry = new BotRegistry(configuration);
        var localClients = new System.Collections.Concurrent.ConcurrentDictionary<string, StorefrontClient>(StringComparer.OrdinalIgnoreCase);
        clients = localClients;
        var clientProvider = new BotClientProvider(registry, bot => localClients.GetOrAdd(bot.Id, _ => new StorefrontClient()));
        services.AddSingleton(registry); services.AddSingleton(clientProvider);
        return services.BuildServiceProvider();
    }
}

public sealed partial class ConcurrencyTests
{
    private static async Task AssertAtlasDatabaseCoherenceAsync(Databases databases, ServiceProvider provider)
    {
        var expectedUsers = Path.GetFullPath(Path.Combine(databases.DirectoryPath, "users.db"));
        var expectedCredentials = Path.GetFullPath(Path.Combine(databases.DirectoryPath, "credentials.db"));
        await using var fixtureUsers = databases.Users.CreateDbContext();
        Assert.Equal(expectedUsers, Path.GetFullPath(fixtureUsers.Database.GetDbConnection().DataSource));
        Assert.Equal(expectedUsers, Path.GetFullPath(await ReadMainDatabasePathAsync(fixtureUsers)));
        var applied = (await fixtureUsers.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Contains("20260625000000_AddMultiBotState", applied);
        Assert.Equal("20260910012628_AddAtlasPayGateway", applied[^1]);
        var connection = fixtureUsers.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='BotUserStates';";
        Assert.Equal(1L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync()));
        Assert.Equal(expectedUsers, Path.GetFullPath(UserDbContext.DatabasePath));
        await using var scope = provider.CreateAsyncScope();
        var diContext = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        Assert.Equal(expectedUsers, Path.GetFullPath(diContext.Database.GetDbConnection().DataSource));
        Assert.True(await diContext.BotUserStates.CountAsync() >= 0);

        var diFactory = provider.GetRequiredService<UserDbContextFactory>();
        await using var factoryContext = diFactory.CreateDbContext();
        Assert.Equal(expectedUsers, Path.GetFullPath(factoryContext.Database.GetDbConnection().DataSource));

        var workflow = scope.ServiceProvider.GetRequiredService<UserWorkflowStore>();
        var workflowFactory = ReadUsersFactory(workflow);
        await using var workflowContext = workflowFactory.CreateDbContext();
        Assert.Equal(expectedUsers, Path.GetFullPath(workflowContext.Database.GetDbConnection().DataSource));
        Assert.True(await workflow.ReadAsync(db => db.BotUserStates.CountAsync()) >= 0);

        var stateStore = provider.GetRequiredService<UserStateStore>();
        var stateFactory = ReadUsersFactory(stateStore);
        await using var stateContext = stateFactory.CreateDbContext();
        Assert.Equal(expectedUsers, Path.GetFullPath(stateContext.Database.GetDbConnection().DataSource));
        Assert.True(await stateContext.BotUserStates.CountAsync() >= 0);
        var credentialsFactory = provider.GetRequiredService<CredentialsDbContextFactory>();
        await using var credentials = credentialsFactory.CreateDbContext();
        Assert.Equal(expectedCredentials, Path.GetFullPath(credentials.Database.GetDbConnection().DataSource));
    }

    private static UserDbContextFactory ReadUsersFactory(object store)
    {
        var field = store.GetType().GetField("_factory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<UserDbContextFactory>(field!.GetValue(store));
    }

    private static async Task<string> ReadMainDatabasePathAsync(UserDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA database_list;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (string.Equals(reader.GetString(1), "main", StringComparison.OrdinalIgnoreCase))
                return reader.GetString(2);
        throw new InvalidOperationException("SQLite main database was not found.");
    }
}

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task AtlasPay_tenant_manual_delivery_never_fulfills_or_mutates_financial_state()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        const int providerOrderId = 77;
        var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            StatusJson("confirmed", paid: true, manual: true, actual: 200000))));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out _);
        await AssertAtlasDatabaseCoherenceAsync(databases, provider);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(711); await credentialsStore.AddEmptyUser(722);
        var tenant = new BotInstance { Id="tenant-atlas-manual", Username="atlas_manual", Type=BotInstanceTypes.Tenant,
            Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=2, TenantAtlasPayEnabled=true,
            CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
        int orderId; int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant); await db.SaveChangesAsync(); registry.Upsert(tenant);
            var order = new TenantBotOrder { OrderId="atlas-manual-order", TenantBotId=tenant.Id, TenantBotUsername=tenant.Username,
                OwnerTelegramUserId=711, CustomerTelegramUserId=722, CustomerChatId=722, SalePriceToman=250000,
                BaseCostToman=200000, ProfitToman=50000, PaymentProvider="atlaspay", PaymentStatus=TenantBotOrderStatuses.Pending,
                OrderKind=TenantBotOrderKinds.Purchase, ServiceKey="normal", TrafficGb=10, DurationKey="m1", AccountCount=1,
                CreatedAtUtc=DateTime.UtcNow };
            db.TenantBotOrders.Add(order); await db.SaveChangesAsync(); orderId=order.Id;
            var payment = VerifiedAtlasPayment(); payment.ProviderOrderId=providerOrderId;
            payment.TelegramUserId=722; payment.ChatId=722; payment.BotId=tenant.Id;
            payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.TenantOrder;
            payment.TenantBotOrderId=order.Id; payment.ProviderStatus="awaiting_payment"; payment.PaidAtUtc=null;
            payment.NextInquiryAtUtc=DateTime.UtcNow.AddMinutes(-1); db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
            order.AtlasPayPaymentInfoId=payment.Id; await db.SaveChangesAsync();
        }
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcilePaymentAsync(paymentId, "manual-test", false)).Status);
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcilePaymentAsync(paymentId, "manual-test-repeat", true)).Status);
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
            var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
            Assert.False(order.IsFulfilled); Assert.Equal("atlaspay", order.PaymentProvider);
            Assert.False(payment.IsAddedToBalance); Assert.True(payment.RequiresManualDelivery);
            Assert.Equal(200000, payment.ActualReceivedAmountToman); Assert.Equal(AtlasPaySettlementStates.ManualReview, payment.SettlementState);
            Assert.Empty(await db.XuiV3CreationOperations.ToListAsync()); Assert.Empty(await db.XuiV3RenewalOperations.ToListAsync());
            Assert.Empty(await db.Set<SiteWalletDebitOperation>().ToListAsync());
            Assert.Empty(await db.TenantBotLedgerEntries.ToListAsync()); Assert.Empty(await db.TenantOrderNotifications.ToListAsync());
        }
        await using var creds = databases.Credentials.CreateDbContext();
        Assert.Empty(await creds.WalletOperations.Where(x => x.OperationKey == $"tenant:{orderId}:profit").ToListAsync());
    }
}

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task AtlasPay_tenant_renewal_persists_before_create_and_mutates_xui_exactly_once()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var updatePosts = 0;
        const string uuid = "11111111-1111-1111-1111-111111111111";
        var client = new JObject
        {
            ["id"] = 41, ["email"] = "renew-atlas@example.test", ["uuid"] = uuid,
            ["totalGB"] = 10L * 1024 * 1024 * 1024,
            ["expiryTime"] = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            ["tgId"] = 722, ["limitIp"] = 0, ["enable"] = true, ["subId"] = "sub-atlas-renew",
            ["comment"] = Newtonsoft.Json.JsonConvert.SerializeObject(new XuiV3ClientMetadata
            { TelegramUserId = 722, TenantBotId = "tenant-atlas-renew", ServiceKey = "normal", ServiceKind = XuiV3ServiceKinds.Metered }),
            ["inboundIds"] = new JArray(200)
        };
        var xuiBuilder = WebApplication.CreateBuilder();
        xuiBuilder.Logging.ClearProviders(); xuiBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var xui = xuiBuilder.Build();
        xui.Run(async context =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (context.Request.Method == "POST" && path.Contains("/clients/update/", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref updatePosts);
                var body = JObject.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync());
                Assert.Equal(uuid, body.Value<string>("id")); Assert.Null(body["uuid"]);
                foreach (var property in body.Properties()) client[property.Name] = property.Value.DeepClone();
                client["uuid"] = uuid; client["id"] = 41; client["inboundIds"] = new JArray(200);
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}"); return;
            }
            if (path.Contains("/clients/get/", StringComparison.OrdinalIgnoreCase))
            {
                var wrapper = new JObject { ["client"] = client.DeepClone(), ["inboundIds"] = new JArray(200) };
                await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = wrapper }.ToString()); return;
            }
            await context.Response.WriteAsync(new JObject { ["success"] = true,
                ["obj"] = new JArray(client.DeepClone()) }.ToString());
        });
        await xui.StartAsync();
        try
        {
            var atlasCreatePosts = 0; var prePersisted = false; string? merchantRef = null; long totalAmount = 0;
            var atlasHandler = new AtlasHttpHandler(async (_, request, body, _) =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/orders", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref atlasCreatePosts); var json = JObject.Parse(body!);
                    merchantRef = json.Value<string>("merchantOrderRef"); totalAmount = json.Value<long>("baseAmountToman") + 123;
                    await using var check = databases.Users.CreateDbContext();
                    var payment = await check.AtlasPayPaymentInfos.AsNoTracking().SingleOrDefaultAsync(x => x.MerchantOrderRef == merchantRef);
                    var order = payment?.TenantBotOrderId is int oid ? await check.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == oid) : null;
                    prePersisted = payment != null && order?.AtlasPayPaymentInfoId == payment.Id && order.PaymentProvider == "atlaspay";
                    return JsonResponse(HttpStatusCode.OK, Newtonsoft.Json.JsonConvert.SerializeObject(new
                    { success=true, orderId=9201, trackingCode="TRK-9201", totalAmountToman=totalAmount,
                        cardNumberMasked="6037-****-9201", paymentDeadlineAt="2026-09-10T12:00:00Z",
                        customerStartLink="https://t.me/atlaspay_bot/start?start=renew" }));
                }
                return JsonResponse(HttpStatusCode.OK, Newtonsoft.Json.JsonConvert.SerializeObject(new
                { success=true, id=9201, trackingCode="TRK-9201", merchantOrderRef=merchantRef,
                    status="confirmed", paid=true, totalAmountToman=totalAmount, actualReceivedAmountToman=totalAmount,
                    requiresManualDelivery=false, createdAt="2026-09-10T12:00:00Z" }));
            });
            var configuration = AtlasTenantConfiguration(databases, xui.Urls.Single());
            var atlas = new AtlasPay(configuration, new HttpClient(atlasHandler));
            await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out var clients);
            await AssertAtlasDatabaseCoherenceAsync(databases, provider);
            var credentials = provider.GetRequiredService<CredentialsStore>();
            await credentials.AddEmptyUser(711); await credentials.PromotOrDemote(711, true); await credentials.AddEmptyUser(722);
            var customer = await credentials.GetUserStatusWithId(722);
            var tenant = new BotInstance { Id="tenant-atlas-renew", Username="atlas_renew", Token="60002:" + new string('a',35), TelegramBotId=60002,
                Type=BotInstanceTypes.Tenant, Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=3,
                TenantPriceMarkupPercent=20, TenantAtlasPayEnabled=true, CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
            int orderId;
            await using (var db = databases.Users.CreateDbContext())
            {
                db.BotInstances.Add(tenant); await db.SaveChangesAsync(); registry.Upsert(tenant);
                var order = new TenantBotOrder { OrderId="atlas-renew-order", TenantBotId=tenant.Id, TenantBotUsername=tenant.Username,
                    OwnerTelegramUserId=711, CustomerTelegramUserId=722, CustomerChatId=722, OrderKind=TenantBotOrderKinds.Renew,
                    TargetAccountEmail="renew-atlas@example.test", TargetAccountUuid=uuid, ServiceKey="normal", TrafficGb=10, DurationKey="m1",
                    AccountCount=1, SalePriceToman=60000, BaseCostToman=50000, ProfitToman=10000,
                    PaymentProvider="tenant_card", PaymentStatus=TenantBotOrderStatuses.Pending, CreatedAtUtc=DateTime.UtcNow };
                db.TenantBotOrders.Add(order); await db.SaveChangesAsync(); orderId=order.Id;
            }
            var telegram = clients.GetOrAdd(tenant.Id, _ => new StorefrontClient());
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var method = typeof(TenantBotService).GetMethod("CreateTenantAtlasPayInvoiceForExistingOrderAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var callback = new CallbackQuery { Id="atlas-renew-create", From=new Telegram.Bot.Types.User { Id=722 },
                Message=new Message { MessageId=1, Chat=new Chat { Id=722 } } };
            await (Task)method.Invoke(service, new object[] { telegram, callback, tenant, customer!, orderId, CancellationToken.None })!;
            Assert.True(prePersisted); Assert.Equal(1, atlasCreatePosts);
            int paymentId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
                var payment = await db.AtlasPayPaymentInfos.SingleAsync(); paymentId=payment.Id;
                Assert.Equal("atlaspay", order.PaymentProvider); Assert.Equal(payment.Id, order.AtlasPayPaymentInfoId);
                Assert.Equal(order.Id, payment.TenantBotOrderId); Assert.Equal(TenantBotPaymentPurposes.TenantOrder, payment.PaymentPurpose);
                Assert.Equal(60000, payment.BaseAmountToman); Assert.Equal("metadata", order.RenewalServiceResolutionMode);
            }
            var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
            var first = await reconciler.ReconcilePaymentAsync(paymentId, "atlas-renew-test", false);
            Assert.Equal(NowPaymentsSettlementStatus.Applied, first.Status); Assert.Equal(1, Volatile.Read(ref updatePosts));
            await using (var db = databases.Users.CreateDbContext())
            {
                var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
                var payment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
                var renewal = await db.XuiV3RenewalOperations.SingleAsync(x => x.TenantBotOrderId == order.OrderId);
                Assert.True(order.IsFulfilled); Assert.Equal(TenantBotOrderStatuses.Fulfilled, order.PaymentStatus);
                Assert.True(payment.IsAddedToBalance); Assert.Equal(AtlasPaySettlementStates.Settled, payment.SettlementState);
                Assert.Equal(XuiV3RenewalOperationStatuses.Applied, renewal.Status);
                Assert.Equal(XuiV3RenewalSettlementStatuses.Settled, renewal.SettlementStatus);
                Assert.Equal(1, await db.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
                Assert.Equal(3, await db.TenantOrderNotifications.CountAsync(x => x.TenantBotOrderId == orderId));
                Assert.Empty(await db.Set<SiteWalletDebitOperation>().ToListAsync());
            }
            await using (var db = databases.Credentials.CreateDbContext())
                Assert.Equal(1, await db.WalletOperations.CountAsync(x => x.OperationKey == $"tenant:{orderId}:profit"));
            Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded,
                (await reconciler.ReconcilePaymentAsync(paymentId, "atlas-renew-repeat", false)).Status);
            await reconciler.ReconcileDueAsync();
            Assert.Equal(1, atlasCreatePosts); Assert.Equal(1, Volatile.Read(ref updatePosts));
            await using var final = databases.Users.CreateDbContext();
            Assert.Equal(1, await final.XuiV3RenewalOperations.CountAsync(x => x.TenantBotOrderId == "atlas-renew-order"));
            Assert.Equal(1, await final.TenantBotLedgerEntries.CountAsync(x => x.TenantBotOrderId == orderId));
        }
        finally { await xui.StopAsync(); }
    }
}

public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Renewal manual-delivery E2E: a confirmed, paid AtlasPay renewal that reports requiresManualDelivery=true
    /// must persist provider evidence and actual received amount, but never fulfill the order, touch XUI, credit
    /// the owner, write a ledger row, create a notification intent, or settle the payment financially.
    /// </summary>
    /// <returns>A task completing after repeated reconciliation attempts remain non-mutating.</returns>
    /// <remarks>
    /// XUI points at an unreachable address so any accidental XUI mutation fails loudly. The manual-review state
    /// is terminal for automatic settlement: the second reconciliation returns before any further provider inquiry.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_tenant_renewal_manual_delivery_never_fulfills_or_mutates_financial_state()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, _, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            StatusJson("confirmed", paid: true, manual: true, actual: 45000))));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out _);
        await AssertAtlasDatabaseCoherenceAsync(databases, provider);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(711); await credentialsStore.AddEmptyUser(722);
        var tenant = new BotInstance { Id="tenant-atlas-renew-manual", Username="atlas_renew_manual", Type=BotInstanceTypes.Tenant,
            Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=4, TenantAtlasPayEnabled=true,
            CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
        int orderId; int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant); await db.SaveChangesAsync(); registry.Upsert(tenant);
            var order = new TenantBotOrder { OrderId="atlas-renew-manual-order", TenantBotId=tenant.Id, TenantBotUsername=tenant.Username,
                OwnerTelegramUserId=711, CustomerTelegramUserId=722, CustomerChatId=722, OrderKind=TenantBotOrderKinds.Renew,
                TargetAccountEmail="renew-manual@example.test", TargetAccountUuid="22222222-2222-2222-2222-222222222222",
                ServiceKey="normal", TrafficGb=10, DurationKey="m1", AccountCount=1, SalePriceToman=60000,
                BaseCostToman=50000, ProfitToman=10000, PaymentProvider="atlaspay", PaymentStatus=TenantBotOrderStatuses.Pending,
                CreatedAtUtc=DateTime.UtcNow };
            db.TenantBotOrders.Add(order); await db.SaveChangesAsync(); orderId=order.Id;
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId=722; payment.ChatId=722; payment.BotId=tenant.Id;
            payment.BaseAmountToman=60000; payment.TotalAmountToman=60123; payment.PaymentPurpose=TenantBotPaymentPurposes.TenantOrder;
            payment.TenantBotOrderId=order.Id; payment.ProviderStatus="awaiting_payment"; payment.PaidAtUtc=null;
            payment.NextInquiryAtUtc=DateTime.UtcNow.AddMinutes(-1); db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
            order.AtlasPayPaymentInfoId=payment.Id; await db.SaveChangesAsync();
        }
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcilePaymentAsync(paymentId, "renew-manual-test", false)).Status);
        // The second attempt must be blocked by the terminal manual-review state without another provider call.
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcilePaymentAsync(paymentId, "renew-manual-repeat", true)).Status);
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
            var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
            Assert.True(payment.RequiresManualDelivery); Assert.Equal(45000, payment.ActualReceivedAmountToman);
            Assert.Equal(AtlasPaySettlementStates.ManualReview, payment.SettlementState); Assert.False(payment.IsAddedToBalance);
            Assert.False(order.IsFulfilled); Assert.Equal(TenantBotOrderStatuses.Pending, order.PaymentStatus);
            Assert.Equal("atlaspay", order.PaymentProvider);
            Assert.Empty(await db.XuiV3CreationOperations.ToListAsync());
            Assert.Empty(await db.XuiV3RenewalOperations.ToListAsync());
            Assert.Empty(await db.Set<SiteWalletDebitOperation>().ToListAsync());
            Assert.Empty(await db.TenantBotLedgerEntries.ToListAsync());
            Assert.Empty(await db.TenantOrderNotifications.ToListAsync());
        }            await using (var creds = databases.Credentials.CreateDbContext())
            {
                // No owner-profit credit, no Gozargah site-wallet debit, and no wallet mutation of any kind.
                Assert.Empty(await creds.WalletOperations.ToListAsync());
            }
        // Exactly one GET inquiry produced the manual-review evidence; the repeat made no provider call.
        Assert.Single(handler.Captures);
    }

    /// <summary>
    /// Callback authorization A: customer B forging apchk_{paymentIdOfCustomerA} must be rejected before any
    /// provider inquiry or financial mutation, leaving the payment row and wallet untouched.
    /// </summary>
    /// <returns>A task completing after the forged callback is rejected with no database effect.</returns>
    /// <remarks>
    /// The callback handler filters the payment row by the sender TelegramUserId and the current bot id, so the
    /// forged check is invisible and no settlement service is ever reached.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_callback_cross_user_rejected_without_mutation()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, _, _, _) => throw new InvalidOperationException("no provider call may occur"));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out _, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(722); await credentialsStore.AddEmptyUser(723);
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId=722; payment.ChatId=722; payment.BotId="main";
            payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus="confirmed"; payment.PaidAtUtc=DateTime.UtcNow;
            db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
        }
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id="main", Type=BotInstanceTypes.Owned }, Client = client });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
        var method = typeof(TelegramBotService).GetMethod("ProcessAtlasPayPaymentCallbackAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var callback = new CallbackQuery { Id="apchk-cross-user", Data=$"apchk_{paymentId}",
            From=new Telegram.Bot.Types.User { Id=723 }, Message=new Message { MessageId=1, Chat=new Chat { Id=723 } } };
        await (Task)method.Invoke(service, new object[] { callback, CancellationToken.None })!;
        Assert.Contains("فاکتور اطلس‌پی پیدا نشد.", client.Answers);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.False(saved.IsAddedToBalance); Assert.Equal(AtlasPaySettlementStates.Pending, saved.SettlementState);
        Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
        await using var creds = databases.Credentials.CreateDbContext();
        Assert.Equal(0, await credentialsStore.GetAccountBalance(722));
        Assert.Empty(await creds.WalletOperations.ToListAsync());
    }

    /// <summary>
    /// Callback authorization B/F: a payment created on owned bot A checked through owned bot B, or through a
    /// different bot identity, must be invisible to the callback and must not trigger any financial mutation.
    /// </summary>
    /// <returns>A task completing after the cross-bot callback is rejected with no provider call or credit.</returns>
    /// <remarks>
    /// The same sender presses the check button, but the active bot context differs from payment.BotId, so the
    /// row is filtered out before reconciliation can run.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_callback_cross_bot_rejected_without_mutation()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, _, _, _) => throw new InvalidOperationException("no provider call may occur"));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out _, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(722);
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId=722; payment.ChatId=722; payment.BotId="main";
            payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus="confirmed"; payment.PaidAtUtc=DateTime.UtcNow;
            db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
        }
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id="tenant-b", Type=BotInstanceTypes.Tenant }, Client = client });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
        var method = typeof(TelegramBotService).GetMethod("ProcessAtlasPayPaymentCallbackAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var callback = new CallbackQuery { Id="apchk-cross-bot", Data=$"apchk_{paymentId}",
            From=new Telegram.Bot.Types.User { Id=722 }, Message=new Message { MessageId=1, Chat=new Chat { Id=722 } } };
        await (Task)method.Invoke(service, new object[] { callback, CancellationToken.None })!;
        Assert.Contains("فاکتور اطلس‌پی پیدا نشد.", client.Answers);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.False(saved.IsAddedToBalance); Assert.Equal(AtlasPaySettlementStates.Pending, saved.SettlementState);
        Assert.Equal(0, await credentialsStore.GetAccountBalance(722));
        await using var creds = databases.Credentials.CreateDbContext();
        Assert.Empty(await creds.WalletOperations.ToListAsync());
    }

    /// <summary>
    /// Callback authorization C: an Atlas tenant payment belonging to tenant A checked through tenant B must be
    /// rejected because the active bot id does not match payment.BotId; the order stays unfulfilled.
    /// </summary>
    /// <returns>A task completing after the cross-tenant callback is rejected with no fulfillment.</returns>
    /// <remarks>
    /// Both the payment-row filter and the linked-order filter require the current bot id, so a tenant B check of
    /// a tenant A payment is invisible before any reconciliation can run.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_callback_cross_tenant_rejected_without_mutation()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, _, _, _) => throw new InvalidOperationException("no provider call may occur"));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(711); await credentialsStore.AddEmptyUser(722);
        var tenantA = new BotInstance { Id="tenant-atlas-a", Username="atlas_a", Type=BotInstanceTypes.Tenant,
            Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=5, TenantAtlasPayEnabled=true,
            CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
        int orderId; int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenantA); await db.SaveChangesAsync(); registry.Upsert(tenantA);
            var order = new TenantBotOrder { OrderId="atlas-tenant-a-order", TenantBotId=tenantA.Id, TenantBotUsername=tenantA.Username,
                OwnerTelegramUserId=711, CustomerTelegramUserId=722, CustomerChatId=722, SalePriceToman=250000,
                BaseCostToman=200000, ProfitToman=50000, PaymentProvider="atlaspay", PaymentStatus=TenantBotOrderStatuses.Pending,
                CreatedAtUtc=DateTime.UtcNow };
            db.TenantBotOrders.Add(order); await db.SaveChangesAsync(); orderId=order.Id;
            var payment = VerifiedAtlasPayment(); payment.TelegramUserId=722; payment.ChatId=722; payment.BotId=tenantA.Id;
            payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.TenantOrder;
            payment.TenantBotOrderId=order.Id; payment.ProviderStatus="confirmed"; payment.PaidAtUtc=DateTime.UtcNow;
            db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
            order.AtlasPayPaymentInfoId=payment.Id; await db.SaveChangesAsync();
        }
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext
        { Config = new BotInstanceConfig { Id="tenant-b", Type=BotInstanceTypes.Tenant }, Client = client });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
        var method = typeof(TelegramBotService).GetMethod("ProcessAtlasPayPaymentCallbackAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var callback = new CallbackQuery { Id="apchk-cross-tenant", Data=$"apchk_{paymentId}",
            From=new Telegram.Bot.Types.User { Id=722 }, Message=new Message { MessageId=1, Chat=new Chat { Id=722 } } };
        await (Task)method.Invoke(service, new object[] { callback, CancellationToken.None })!;
        Assert.Contains("فاکتور اطلس‌پی پیدا نشد.", client.Answers);
        await using var verify = databases.Users.CreateDbContext();
        var savedOrder = await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId);
        var savedPayment = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.False(savedOrder.IsFulfilled); Assert.Equal(TenantBotOrderStatuses.Pending, savedOrder.PaymentStatus);
        Assert.False(savedPayment.IsAddedToBalance); Assert.Equal(AtlasPaySettlementStates.Pending, savedPayment.SettlementState);
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await verify.TenantOrderNotifications.ToListAsync());
    }

    /// <summary>
    /// Callback authorization D/E: an Atlas payment pointing at a tenant order whose customer or tenant bot does
    /// not match the payment row must never fulfill, regardless of provider success status.
    /// </summary>
    /// <returns>A task completing after both linkage mismatches are rejected with the payment quarantined.</returns>
    /// <remarks>
    /// The linkage checks run before any XUI, ledger, owner-profit, notification, or Gozargah mutation. The
    /// resulting manual-review state also blocks repeated reconciliation from fulfilling later.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_tenant_payment_linkage_mismatch_never_fulfills()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, _, _, _) => throw new InvalidOperationException("no provider call may occur"));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(711); await credentialsStore.AddEmptyUser(722);
        var scenarios = new[]
        {
            new { OrderId="atlas-link-customer-mismatch", BotId="tenant-link-d", TenantStore=6, WrongCustomer=888L, ProviderOrderId=77 },
            new { OrderId="atlas-link-tenant-mismatch", BotId="tenant-link-e", TenantStore=7, WrongCustomer=0L, ProviderOrderId=78 }
        };
        var service = provider.GetRequiredService<TenantBotService>();
        foreach (var scenario in scenarios)
        {
            var tenant = new BotInstance { Id=scenario.BotId, Username=scenario.BotId, Type=BotInstanceTypes.Tenant,
                Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=scenario.TenantStore, TenantAtlasPayEnabled=true,
                CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
            int orderId; int paymentId;
            await using (var db = databases.Users.CreateDbContext())
            {
                db.BotInstances.Add(tenant); await db.SaveChangesAsync(); registry.Upsert(tenant);
                var order = new TenantBotOrder { OrderId=scenario.OrderId, TenantBotId=tenant.Id, TenantBotUsername=tenant.Username,
                    OwnerTelegramUserId=711, CustomerTelegramUserId=722, CustomerChatId=722, SalePriceToman=250000,
                    BaseCostToman=200000, ProfitToman=50000, PaymentProvider="atlaspay", PaymentStatus=TenantBotOrderStatuses.Pending,
                    CreatedAtUtc=DateTime.UtcNow };
                db.TenantBotOrders.Add(order); await db.SaveChangesAsync(); orderId=order.Id;
                var payment = VerifiedAtlasPayment(); payment.TelegramUserId=722; payment.ChatId=722; payment.BotId=tenant.Id;
                payment.ProviderOrderId=scenario.ProviderOrderId; payment.MerchantOrderRef="AtlasPay-link-" + scenario.ProviderOrderId;
                payment.TrackingCode="TRK-" + scenario.ProviderOrderId;
                payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.TenantOrder;
                payment.TenantBotOrderId=order.Id; payment.ProviderStatus="confirmed"; payment.PaidAtUtc=DateTime.UtcNow;
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
                order.AtlasPayPaymentInfoId=payment.Id; await db.SaveChangesAsync();
                // D: the order belongs to a different customer than the payer; E: the order belongs to a different tenant bot.
                if (scenario.WrongCustomer != 0) order.CustomerTelegramUserId = scenario.WrongCustomer;
                else order.TenantBotId = "tenant-other";
                await db.SaveChangesAsync();
            }
            var result = await service.ApplyPaidTenantOrderAsync(new AtlasPayPaymentInfo { Id=paymentId,
                PaymentPurpose=TenantBotPaymentPurposes.TenantOrder, ProviderStatus="confirmed", PaidAtUtc=DateTime.UtcNow }, "linkage-test");
            Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
                var order = await db.TenantBotOrders.SingleAsync(x => x.Id == orderId);
                Assert.Equal("tenant_payment_link_mismatch", payment.ErrorCode);
                Assert.Equal(AtlasPaySettlementStates.ManualReview, payment.SettlementState);
                Assert.False(payment.IsAddedToBalance); Assert.False(order.IsFulfilled);
                Assert.Empty(await db.XuiV3CreationOperations.ToListAsync());
                Assert.Empty(await db.XuiV3RenewalOperations.ToListAsync());
                Assert.Empty(await db.Set<SiteWalletDebitOperation>().ToListAsync());
                Assert.Empty(await db.TenantBotLedgerEntries.ToListAsync());
                Assert.Empty(await db.TenantOrderNotifications.ToListAsync());
            }
            await using (var creds = databases.Credentials.CreateDbContext())
                Assert.Empty(await creds.WalletOperations.Where(x => x.OperationKey == $"tenant:{orderId}:profit").ToListAsync());
        }
        // No provider call happened at all; repeated reconciliation cannot reverse the quarantine.
        Assert.Empty(handler.Captures);
    }

    /// <summary>
    /// Provider identity mismatch E2E: when the provider reports success but the inquiry identity fields do not
    /// match the saved payment (wrong ProviderOrderId, MerchantOrderRef, TrackingCode, or TotalAmountToman),
    /// no wallet credit, ledger row, or notification may be created.
    /// </summary>
    /// <returns>A task completing after each of the four mismatches leaves the database unchanged.</returns>
    /// <remarks>
    /// Provider success status alone never overrides an identity mismatch; the verifier returns a specific error
    /// and the reconciliation path quarantines the row for manual review without settling.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_identity_mismatch_owned_settlement_leaves_database_unchanged()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var mode = "none"; var currentProviderOrderId = 77; var currentMerchantRef = "AtlasPay-test"; var currentTrackingCode = "TRK-77";
        var handler = new AtlasHttpHandler((_, _, _, _) =>
        {
            var response = VerifiedAtlasResponse("confirmed", paid: true, manual: false);
            response.Id = currentProviderOrderId; response.MerchantOrderRef = currentMerchantRef; response.TrackingCode = currentTrackingCode;
            if (mode == "provider_order_id_mismatch") response.Id = currentProviderOrderId + 1;
            if (mode == "merchant_order_ref_mismatch") response.MerchantOrderRef = "other";
            if (mode == "tracking_code_mismatch") response.TrackingCode = "TRK-other";
            if (mode == "total_amount_mismatch") response.TotalAmountToman = 250124;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, Newtonsoft.Json.JsonConvert.SerializeObject(response)));
        });
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out _, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        var userIndex = 0;
        foreach (var mismatch in new[] { "provider_order_id_mismatch", "merchant_order_ref_mismatch", "tracking_code_mismatch", "total_amount_mismatch" })
        {
            var userId = 8841L + userIndex++; mode = mismatch;
            var providerOrderId = 77 + userIndex;
            currentProviderOrderId = providerOrderId; currentMerchantRef = "AtlasPay-mismatch-" + providerOrderId; currentTrackingCode = "TRK-" + providerOrderId;
            await credentialsStore.AddEmptyUser(userId);
            int paymentId;
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = VerifiedAtlasPayment();
                payment.ProviderOrderId=providerOrderId; payment.MerchantOrderRef=currentMerchantRef; payment.TrackingCode=currentTrackingCode;
                payment.TelegramUserId=userId; payment.ChatId=userId; payment.BotId="main";
                payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.WalletCharge;
                payment.ProviderStatus="awaiting_payment"; payment.NextInquiryAtUtc=DateTime.UtcNow.AddMinutes(-1);
                db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
            }
            var result = await reconciler.ReconcilePaymentAsync(paymentId, "identity-mismatch", false);
            Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
            await using (var db = databases.Users.CreateDbContext())
            {
                var payment = await db.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
                Assert.False(payment.IsAddedToBalance);
                Assert.Equal(AtlasPaySettlementStates.ManualReview, payment.SettlementState);
                Assert.Equal(mismatch, payment.ErrorCode);
            }
            Assert.Equal(0, await credentialsStore.GetAccountBalance(userId));
            await using (var creds = databases.Credentials.CreateDbContext())
                Assert.Empty(await creds.WalletOperations.ToListAsync());
        }
        await using var final = databases.Users.CreateDbContext();
        Assert.Empty(await final.WalletLedgerEntries.ToListAsync());
        Assert.Empty(await final.PaymentSettlementNotifications.ToListAsync());
    }

    /// <summary>
    /// InvalidAmount audit regression: a users.db infrastructure failure (corrupted database) with a valid payment
    /// amount must never be reported as InvalidAmount by the Atlas settlement or reconciliation paths.
    /// </summary>
    /// <returns>A task completing after both services propagate the infrastructure failure instead of misclassifying it.</returns>
    /// <remarks>
    /// InvalidAmount is reserved for explicit amount/business validation. Database failures surface as exceptions
    /// before any success claim is made, which keeps reconciliation safe to retry later.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_users_database_failure_is_never_reported_as_invalid_amount()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId=8899; payment.ChatId=8899; payment.BotId="main";
            payment.BaseAmountToman=250000; payment.PaymentPurpose=TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus="confirmed"; payment.PaidAtUtc=DateTime.UtcNow;
            db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
        }
        // Close SQLite pools and overwrite the users.db file so every later read fails with SqliteException.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await System.IO.File.WriteAllBytesAsync(Path.Combine(databases.DirectoryPath, "users.db"),
            new byte[] { 0x44, 0x45, 0x41, 0x44, 0x42, 0x45, 0x45, 0x46, 0x00, 0x01, 0x02, 0x03 });
        await using var provider = IncidentProvider(databases).Provider;
        var settlement = provider.GetRequiredService<AtlasPaySettlementService>();
        var settlementException = await Record.ExceptionAsync(() =>
            settlement.ApplyOfficialPaymentAsync(new AtlasPayPaymentInfo { Id=paymentId }, "infra-test"));
        // An infrastructure failure must propagate as an exception; it is never converted into an InvalidAmount
        // (or any other settlement status) result.
        Assert.NotNull(settlementException);
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        var reconcileException = await Record.ExceptionAsync(() =>
            reconciler.ReconcilePaymentAsync(paymentId, "infra-test", false));
        Assert.NotNull(reconcileException);
    }

    /// <summary>
    /// Migration compatibility: upgrading a legacy users.db (migrated only up to the migration immediately before
    /// AddAtlasPayGateway) preserves legacy BotInstance and TenantBotOrder rows, applies the Atlas default switch,
    /// leaves old orders unlinked, and keeps BotUserStates and the migration history intact.
    /// </summary>
    /// <returns>A task completing after the legacy upgrade and all post-migration assertions.</returns>
    /// <remarks>
    /// No EnsureCreated is used and no historical migration is edited; the test migrates a real SQLite file to the
    /// pre-Atlas snapshot, inserts legacy data, then migrates through AddAtlasPayGateway.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_migration_preserves_legacy_tenant_rows_and_creates_table()
    {
        const string atlasMigration = "20260910012628_AddAtlasPayGateway";
        const string beforeAtlas = "20260910001835_HardenTenantStorefrontFundingAlertDelivery";
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext())
        {
            var migrations = users.Database.GetMigrations().ToArray();
            var atlasIndex = Array.FindIndex(migrations, x => x == atlasMigration);
            Assert.True(atlasIndex > 0, "AddAtlasPayGateway migration must exist after at least one earlier migration.");
            Assert.Equal(beforeAtlas, migrations[atlasIndex - 1]);
            await users.GetService<IMigrator>().MigrateAsync(beforeAtlas);
            await users.Database.ExecuteSqlRawAsync("""
                INSERT INTO BotInstances (Id, Type, OwnerTelegramUserId, Enabled, IsDefault, TenantPriceMarkupPercent,
                    TenantMandatoryJoinEnabled, TenantCardPaymentEnabled, TenantHooshPayEnabled, TenantNowPaymentsEnabled,
                    TenantTetraminatorEnabled, TenantUniquePayEnabled, CreatedAtUtc)
                VALUES ('tenant-legacy-atlas', 'tenant', 711, 0, 0, 20, 0, 0, 1, 1, 1, 1, '2026-09-01 00:00:00');
                """);
            await users.Database.ExecuteSqlRawAsync("""
                INSERT INTO TenantBotOrders (OrderId, TenantBotId, TenantBotUsername, OwnerTelegramUserId,
                    CustomerTelegramUserId, CustomerChatId, OrderKind, PaymentProvider, PaymentStatus,
                    AccountCount, IsFulfilled, IsOwnerCredited, OwnerWalletDelta,
                    SalePriceToman, BaseCostToman, ProfitToman, CreatedAtUtc)
                VALUES ('legacy-atlas-order', 'tenant-legacy-atlas', 'atlas_legacy', 711, 55, 55, 'purchase',
                    'hooshpay', 'pending', 1, 0, 0, 0, 100000, 80000, 20000, '2026-09-01 00:00:00');
                """);
            await users.Database.MigrateAsync();
            var bot = await users.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-legacy-atlas");
            Assert.True(bot.TenantAtlasPayEnabled, "Legacy tenants must inherit the Atlas default switch.");
            Assert.Equal(20, bot.TenantPriceMarkupPercent); Assert.Equal(711, bot.OwnerTelegramUserId);
            var order = await users.TenantBotOrders.AsNoTracking().SingleAsync(x => x.OrderId == "legacy-atlas-order");
            Assert.Null(order.AtlasPayPaymentInfoId);
            Assert.Equal("hooshpay", order.PaymentProvider); Assert.Equal("pending", order.PaymentStatus);
            Assert.Equal(80000, order.BaseCostToman); Assert.Equal(20000, order.ProfitToman);
            Assert.False(order.IsFulfilled);
            Assert.Empty(await users.AtlasPayPaymentInfos.ToListAsync());
            var applied = (await users.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Equal(atlasMigration, applied[^1]);
            var multiBotIndex = applied.FindIndex(x => x == "20260625000000_AddMultiBotState");
            Assert.True(multiBotIndex >= 0 && multiBotIndex < applied.Count - 1);
            var connection = users.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='BotUserStates';";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AtlasPayPaymentInfos';";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            var history = (await users.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains("20260625000000_AddMultiBotState", history);
            Assert.Equal(atlasMigration, history[^1]);
        }
    }

    /// <summary>
    /// Create ambiguity: a payment whose creation ended ambiguous, failed, or still attempting must never cause a
    /// second POST /orders; reconciliation stays HTTP-free for such rows and the due worker skips them.
    /// </summary>
    /// <returns>A task completing after all non-created rows are reconciled without a single provider call.</returns>
    /// <remarks>
    /// The reconciliation worker only inquires payments whose creation state is Created and which already carry a
    /// provider order id; any other row is left for manual review without network traffic.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_ambiguous_create_never_retries_and_reconciliation_stays_http_free()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, _, _, _) => throw new InvalidOperationException("no provider call may occur"));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out _, out _);
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        var userId = 9901;
        var states = new[] { AtlasPayCreationStates.Ambiguous, AtlasPayCreationStates.Failed, AtlasPayCreationStates.Attempting };
        foreach (var state in states)
        {
            var payment = VerifiedAtlasPayment(); payment.ProviderOrderId = null; payment.TrackingCode = null; payment.CustomerStartLink = null;
            payment.MerchantOrderRef = "AtlasPay-ambiguous-" + state;
            payment.TelegramUserId = userId++; payment.ChatId = payment.TelegramUserId; payment.BotId = "main";
            payment.BaseAmountToman = 250000; payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.CreationState = state; payment.CreationAttemptCount = 1; payment.CreationResolvedAtUtc = state == AtlasPayCreationStates.Attempting ? null : DateTime.UtcNow;
            payment.NextInquiryAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await using (var db = databases.Users.CreateDbContext()) { db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); }
            var result = await reconciler.ReconcilePaymentAsync(payment.Id, "ambiguous-test", false);
            Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
        }
        await reconciler.ReconcileDueAsync();
        Assert.Empty(handler.Captures);
    }

    /// <summary>
    /// Status matrix: a settled + paid AtlasPay response that requires manual delivery must never auto-settle,
    /// exactly like the confirmed case.
    /// </summary>
    [Fact]
    public void AtlasPay_settled_manual_delivery_never_auto_settles()
    {
        var payment = VerifiedAtlasPayment();
        var response = VerifiedAtlasResponse("settled", paid: true, manual: true);
        response.ActualReceivedAmountToman = 200000;
        Assert.False(AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out var manual));
        Assert.True(manual); Assert.Equal("requires_manual_delivery", error);
    }

    /// <summary>
    /// /verify behavior: POST /orders/{id}/verify must feed the same common Atlas reconciliation and settlement
    /// path as the GET inquiry; a successful verify result must never directly credit the wallet or fulfill an order.
    /// </summary>
    /// <returns>A task completing after a verify-based reconciliation settles exactly once through the common path.</returns>
    /// <remarks>
    /// The verify response passes through the identical identity/status/manual-delivery checks and exactly-once
    /// settlement claim; the second verify-based call returns AlreadyAdded without another provider request.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_verify_endpoint_feeds_common_settlement_path_exactly_once()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var handler = new AtlasHttpHandler((_, request, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            StatusJson("confirmed", paid: true, manual: false, actual: 250123))));
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(handler));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out _, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(8845);
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId=8845; payment.ChatId=8845; payment.BotId="main";
            payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus="awaiting_payment"; payment.NextInquiryAtUtc=DateTime.UtcNow.AddMinutes(-1);
            db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
        }
        var reconciler = provider.GetRequiredService<AtlasPayReconciliationHostedService>();
        var first = await reconciler.ReconcilePaymentAsync(paymentId, "customer-check", useVerify: true);
        Assert.Equal(NowPaymentsSettlementStatus.Applied, first.Status);
        Assert.Equal(250000, await credentialsStore.GetAccountBalance(8845));
        var repeat = await reconciler.ReconcilePaymentAsync(paymentId, "customer-check-repeat", useVerify: true);
        Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded, repeat.Status);
        var capture = Assert.Single(handler.Captures);
        Assert.Equal(HttpMethod.Post, capture.Method);
        Assert.EndsWith("/orders/77/verify", capture.Uri, StringComparison.Ordinal);
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Equal(1, await db.WalletLedgerEntries.CountAsync(x => x.Provider == "atlaspay" && x.ReferenceId == paymentId.ToString()));
            Assert.Equal(1, await db.PaymentSettlementNotifications.CountAsync(x => x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
        }
        await using (var creds = databases.Credentials.CreateDbContext())
            Assert.Equal(1, await creds.WalletOperations.CountAsync(x => x.OperationKey == $"payment:atlaspay:{paymentId}:credit"));
    }

    /// <summary>
    /// Owned exactly-once: after a wallet charge with an existing referral relationship, concurrent settlement
    /// attempts must produce exactly one wallet credit, one ledger row, one referral payment event with its
    /// rewards, and one notification intent.
    /// </summary>
    /// <returns>A task completing after repeated concurrent settlements leave all counters at exactly one.</returns>
    /// <remarks>
    /// The referral source key is derived from the stable Atlas payment id, so replay cannot create a second event
    /// or reward row even when the settlement gate is contended.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_owned_settlement_applies_referral_exactly_once()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var configuration = AtlasTenantConfiguration(databases, "http://127.0.0.1:1");
        var atlas = new AtlasPay(configuration, new HttpClient(new AtlasHttpHandler((_, _, _, _) =>
            throw new InvalidOperationException("no provider call may occur"))));
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out _, out _);
        var credentialsStore = provider.GetRequiredService<CredentialsStore>();
        await credentialsStore.AddEmptyUser(8805); await credentialsStore.AddEmptyUser(8806);
        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.ReferralRelationships.Add(new ReferralRelationship
            {
                ReferrerTelegramUserId=8805, ReferredTelegramUserId=8806, AttributionBotId="main",
                ReferralCode="atlas-ref-test", CreatedAtUtc=DateTime.UtcNow
            });
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId=8806; payment.ChatId=8806; payment.BotId="main";
            payment.BaseAmountToman=250000; payment.TotalAmountToman=250123; payment.PaymentPurpose=TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus="confirmed"; payment.PaidAtUtc=DateTime.UtcNow;
            db.AtlasPayPaymentInfos.Add(payment); await db.SaveChangesAsync(); paymentId=payment.Id;
        }
        var settlement = provider.GetRequiredService<AtlasPaySettlementService>();
        var calls = Enumerable.Range(0, 6).Select(_ => settlement.ApplyOfficialPaymentAsync(new AtlasPayPaymentInfo { Id=paymentId }, "referral-concurrent"));
        await Task.WhenAll(calls);
        // 250000 original credit plus the first-payment referred reward (10%, floored to the 50000 minimum).
        Assert.Equal(300000, await credentialsStore.GetAccountBalance(8806));
        Assert.Equal(50000, await credentialsStore.GetAccountBalance(8805));
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Equal(1, await db.WalletLedgerEntries.CountAsync(x => x.Provider == "atlaspay" && x.ReferenceId == paymentId.ToString()));
            Assert.Equal(1, await db.PaymentSettlementNotifications.CountAsync(x => x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
            var events = await db.ReferralPaymentEvents.Where(x => x.SourcePaymentKey == "atlaspay:wallet_charge:77").ToListAsync();
            Assert.Single(events);
            Assert.Equal(2, await db.ReferralRewards.CountAsync(x => x.ReferralPaymentEventId == events[0].Id));
        }
        await using (var creds = databases.Credentials.CreateDbContext())
            Assert.Equal(1, await creds.WalletOperations.CountAsync(x => x.OperationKey == $"payment:atlaspay:{paymentId}:credit"));
        // Replay must not duplicate the referral event or its rewards.
        await settlement.ApplyOfficialPaymentAsync(new AtlasPayPaymentInfo { Id=paymentId }, "referral-replay");
        await using var final = databases.Users.CreateDbContext();
        Assert.Single(await final.ReferralPaymentEvents.Where(x => x.SourcePaymentKey == "atlaspay:wallet_charge:77").ToListAsync());
        Assert.Equal(1, await final.WalletLedgerEntries.CountAsync(x => x.Provider == "atlaspay" && x.ReferenceId == paymentId.ToString()));
    }

    /// <summary>
    /// Global + tenant creation switch matrix: both the global AtlasPay switch and the tenant-scoped
    /// TenantAtlasPayEnabled must be on before a new tenant AtlasPay order can be created; either switch off
    /// rejects creation before any payment row or provider POST exists.
    /// </summary>
    /// <returns>A task completing after both rejection combinations leave no payment row and no HTTP traffic.</returns>
    /// <remarks>
    /// The switches govern creation only; this test proves no new Atlas tenant order or payment is persisted when
    /// either switch is off.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_tenant_creation_requires_global_and_tenant_switches()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var scenarios = new[]
        {
            new { Name="global-off", AtlasEnabled=false, TenantEnabled=true, Expected="سراسری" },
            new { Name="tenant-off", AtlasEnabled=true, TenantEnabled=false, Expected="فروشگاه" }
        };
        var tenantNumber = 10;
        foreach (var scenario in scenarios)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["bots:0:id"]="main", ["bots:0:username"]="main_bot", ["bots:0:token"]="10001:" + new string('a',35),
                    ["bots:0:enabled"]="true", ["bots:0:isDefault"]="true",
                    ["userDatabasePath"]=Path.Combine(databases.DirectoryPath, "users.db"),
                    ["credentialsDatabasePath"]=Path.Combine(databases.DirectoryPath, "credentials.db"),
                    ["atlasPayEnabled"]=scenario.AtlasEnabled ? "true" : "false",
                    ["atlasPayApiKey"]="test-atlas-key", ["atlasPayBaseUrl"]="https://api.atlaspay.space/api/v1",
                    ["XuiV3ApiBaseUrl"]="http://127.0.0.1:1", ["XuiV3ApiToken"]="test-only",
                    ["XuiV3ServicePlansPath"]=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json")),
                    ["GozargahSiteSyncEnabled"]="false", ["GozargahSiteWalletPaymentsEnabled"]="false"
                }).Build();
            var handler = new AtlasHttpHandler((_, _, _, _) => throw new InvalidOperationException("no provider call may occur"));
            var atlas = new AtlasPay(configuration, new HttpClient(handler));
            await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out var clients);
            var credentialsStore = provider.GetRequiredService<CredentialsStore>();
            await credentialsStore.AddEmptyUser(711); await credentialsStore.AddEmptyUser(722);
            var customer = await credentialsStore.GetUserStatusWithId(722);
            var tenant = new BotInstance { Id="tenant-switch-" + scenario.Name, Username="switch_" + scenario.Name, Type=BotInstanceTypes.Tenant,
                Enabled=true, OwnerTelegramUserId=711, TenantStoreNumber=tenantNumber++, TenantAtlasPayEnabled=scenario.TenantEnabled,
                CreatedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
            await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); await db.SaveChangesAsync(); }
            registry.Upsert(tenant);
            var client = clients.GetOrAdd(tenant.Id, _ => new StorefrontClient());
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var method = typeof(TenantBotService).GetMethod("CreateTenantAtlasPayInvoiceAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var callback = new CallbackQuery { Id="switch-reject", From=new Telegram.Bot.Types.User { Id=722 },
                Message=new Message { MessageId=1, Chat=new Chat { Id=722 } } };
            var selection = new XuiV3PurchaseSelection { ServiceKey="normal", TrafficGb=10, DurationKey="m1", AccountCount=1 };
            await (Task)method.Invoke(service, new object[] { client, callback, tenant, customer!, selection, CancellationToken.None })!;
            Assert.Contains(client.Answers, x => x.Contains(scenario.Expected, StringComparison.Ordinal));
            await using (var db = databases.Users.CreateDbContext())
            {
                Assert.Empty(await db.AtlasPayPaymentInfos.ToListAsync());
                Assert.Empty(await db.TenantBotOrders.ToListAsync());
            }
            Assert.Empty(handler.Captures);
        }
    }

    /// <summary>
    /// Tenant owner panel: the AtlasPay toggle participates in the panel setting flow. Enabling is rejected when
    /// the global switch is off; with the global switch on the owner can enable it; a stale revision callback is
    /// rejected by the existing revision/freshness mechanism.
    /// </summary>
    /// <returns>A task completing after global-off rejection, global-on application, and stale-revision rejection.</returns>
    /// <remarks>
    /// Uses the real owner callback pipeline (TenantOwnerCallback/SetEnabled-style panel revision) against the
    /// real tenant storefront provider so the persisted BotInstance flag is verified.
    /// </remarks>
    [Fact]
    public async Task AtlasPay_tenant_owner_panel_toggle_respects_global_switch_and_revision()
    {
        using var databases = new Databases();
        // Global OFF: example configuration keeps atlasPayEnabled=false, so the local enable must be rejected.
        await using (var provider = StorefrontProvider(databases))
        {
            var stores = provider.GetRequiredService<TenantStoreStore>();
            var store = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
            var owner = new CredUser { TelegramUserId=711, IsColleague=true };
            await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
            var client = new StorefrontClient();
            using (new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id="owned-panel" }, Client = client }))
            {
                await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"));
                // Disabling a tenant gateway is always allowed, proving the panel toggle path itself works.
                await OwnerCallback(provider, client, owner, await FreshAtlasToggleAsync(databases, store.Id, false));
                await using (var db = databases.Users.CreateDbContext())
                    Assert.False((await db.BotInstances.SingleAsync(x => x.Id == store.Id)).TenantAtlasPayEnabled);
                // Enabling with the global switch off must be rejected and must not flip the flag back on.
                await OwnerCallback(provider, client, owner, await FreshAtlasToggleAsync(databases, store.Id, true));
            }
            Assert.Contains(client.Answers, x => x.Contains("سراسری", StringComparison.Ordinal));
            await using (var db = databases.Users.CreateDbContext())
                Assert.False((await db.BotInstances.SingleAsync(x => x.Id == store.Id)).TenantAtlasPayEnabled);
        }
        // Global ON: a provider with a valid AtlasPay key and HTTPS URL accepts the local toggle and persists it.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:enabled"]="false", ["bots:0:token"]="12345:" + new string('a',35),
                ["atlasPayEnabled"]="true", ["atlasPayApiKey"]="test-atlas-key",
                ["atlasPayBaseUrl"]="https://api.atlaspay.space/api/v1",
                ["GozargahSiteSyncEnabled"]="false", ["GozargahSiteWalletPaymentsEnabled"]="false"
            }).Build();
        var appConfig = configuration.Get<AppConfig>()!;
        appConfig.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db");
        appConfig.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection();
        Program.RegisterApplicationServices(services, configuration, appConfig, databases.DirectoryPath);
        await using var enabledProvider = services.BuildServiceProvider();
        {
            var stores = enabledProvider.GetRequiredService<TenantStoreStore>();
            var store = await stores.CreateAsync(711, Guid.NewGuid().ToString("N"));
            var owner = new CredUser { TelegramUserId=711, IsColleague=true };
            await enabledProvider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
            var client = new StorefrontClient();
            using (new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id="owned-panel" }, Client = client }))
            {
                await OwnerCallback(enabledProvider, client, owner, TenantOwnerCallback.Encode(store, "panel"));
                await OwnerCallback(enabledProvider, client, owner, await FreshAtlasToggleAsync(databases, store.Id, false));
                await using (var db = databases.Users.CreateDbContext())
                    Assert.False((await db.BotInstances.SingleAsync(x => x.Id == store.Id)).TenantAtlasPayEnabled);
                // With the global switch on, the owner can enable AtlasPay locally and it persists.
                await OwnerCallback(enabledProvider, client, owner, await FreshAtlasToggleAsync(databases, store.Id, true));
                await using (var db = databases.Users.CreateDbContext())
                    Assert.True((await db.BotInstances.SingleAsync(x => x.Id == store.Id)).TenantAtlasPayEnabled);
                // Stale revision: a forged callback with revision zero but a fresh issue bucket must be rejected
                // by the panel revision check and leave the flag unchanged.
                await using (var db = databases.Users.CreateDbContext())
                    store = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id);
                var staleIssued = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300 * 300).ToString("X", CultureInfo.InvariantCulture);
                await OwnerCallback(enabledProvider, client, owner, $"TBM:{store.TenantStoreNumber:X}:0:{staleIssued}:s:AtlasPay:0");
            }
            // The forged revision-zero envelope is rejected by the panel revision/freshness mechanism before
            // the setting switch, so the persisted flag remains enabled.
            Assert.Contains(client.Answers, x => x.Contains("قدیمی", StringComparison.Ordinal));
            await using (var db = databases.Users.CreateDbContext())
                Assert.True((await db.BotInstances.SingleAsync(x => x.Id == store.Id)).TenantAtlasPayEnabled);
        }
    }

    /// <summary>
    /// Builds a fresh addressed AtlasPay tenant-setting callback carrying the current persisted panel revision.
    /// </summary>
    /// <param name="databases">Isolated database fixture owning users.db.</param>
    /// <param name="storeId">Persisted BotInstance id of the tenant storefront.</param>
    /// <param name="enabled">Desired final TenantAtlasPayEnabled state encoded in the callback.</param>
    /// <returns>Compact callback data valid for the current panel revision and issue bucket.</returns>
    /// <remarks>
    /// The store row is reloaded so the revision embedded by the envelope matches the row that the panel handler
    /// reloads; every applied toggle changes UpdatedAtUtc and therefore the next revision.
    /// </remarks>
    private static async Task<string> FreshAtlasToggleAsync(Databases databases, string storeId, bool enabled)
    {
        await using var db = databases.Users.CreateDbContext();
        var store = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == storeId);
        return TenantOwnerCallback.Encode(store, "set-setting:AtlasPay:" + (enabled ? 1 : 0));
    }
}
