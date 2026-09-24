using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task AtlasPay_superadmin_provisional_credit_is_exactly_once_and_later_confirmation_is_audit_only()
    {
        using var databases = new Databases();
        const long adminId = 990099;
        const long customerId = 7715;
        var providerPaid = false;

        var baseConfiguration = AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["adminsUserIds:0"] = adminId.ToString(),
                ["atlasPayWebhookSecret"] = "test-atlas-webhook-secret"
            })
            .Build();

        var handler = new AtlasHttpHandler((_, request, _, _) =>
        {
            Assert.EndsWith("/orders/77/verify", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(
                System.Net.HttpStatusCode.OK,
                providerPaid
                    ? StatusJson("confirmed", paid: true, manual: false, actual: 250123)
                    : StatusJson("awaiting_payment", paid: false, manual: false)));
        });
        var atlas = new AtlasPay(configuration, new HttpClient(handler));

        await using var provider = AtlasTenantProvider(
            databases,
            configuration,
            atlas,
            out _,
            out _);
        await using var scope = provider.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
        await credentials.AddEmptyUser(customerId);

        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId = customerId;
            payment.ChatId = customerId;
            payment.BotId = "main";
            payment.BotUsername = "main_bot";
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus = "awaiting_payment";
            payment.PaidAtUtc = null;
            payment.NextInquiryAtUtc = null;
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var reconciler = scope.ServiceProvider.GetRequiredService<AtlasPayReconciliationHostedService>();
        var first = await reconciler.ReconcileAndApplyProvisionalAsync(
            paymentId,
            adminId,
            customerId,
            "admin-provisional-confirm-refresh");

        Assert.Equal(NowPaymentsSettlementStatus.Applied, first.Status);
        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));

        // Replay the final admin action. It may re-check the provider, but it can never repeat the balance mutation.
        await reconciler.ReconcileAndApplyProvisionalAsync(
            paymentId,
            adminId,
            customerId,
            "admin-provisional-confirm-refresh");

        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));

        await using (var users = databases.Users.CreateDbContext())
        {
            var saved = await users.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
            Assert.True(saved.IsProvisionallyApproved);
            Assert.Equal(adminId, saved.ProvisionalApprovedByTelegramUserId);
            Assert.NotNull(saved.ProvisionalApprovedAtUtc);
            Assert.Null(saved.ProviderConfirmedAfterProvisionalAtUtc);
            Assert.Equal("awaiting_payment", saved.ProviderStatus);
            Assert.True(saved.IsAddedToBalance);

            Assert.Equal(1, await users.WalletLedgerEntries.CountAsync(x =>
                x.ReferenceId == paymentId.ToString() &&
                x.IdempotencyKey == $"payment:atlaspay:{paymentId}:credit"));
            Assert.Equal(1, await users.PaymentSettlementNotifications.CountAsync(x =>
                x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
            Assert.Equal(0, await users.ReferralPaymentEvents.CountAsync(x =>
                x.SourcePaymentKey.StartsWith("atlaspay:")));
        }

        await using (var creds = databases.Credentials.CreateDbContext())
        {
            var receipt = await creds.WalletOperations.SingleAsync(x =>
                x.OperationKey == $"payment:atlaspay:{paymentId}:credit");
            Assert.Equal("provisional", receipt.ApprovalKind);
            Assert.Equal(adminId, receipt.ApprovedByTelegramUserId);
            Assert.Equal(250000, receipt.AmountToman);
        }

        providerPaid = true;
        var official = await reconciler.ReconcilePaymentAsync(
            paymentId,
            "superadmin-verify",
            useVerify: true);

        Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded, official.Status);
        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));

        await using (var users = databases.Users.CreateDbContext())
        {
            var saved = await users.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
            Assert.Equal("confirmed", saved.ProviderStatus);
            Assert.NotNull(saved.ProviderConfirmedAfterProvisionalAtUtc);
            Assert.True(saved.IsProvisionallyApproved);

            Assert.Equal(1, await users.WalletLedgerEntries.CountAsync(x =>
                x.IdempotencyKey == $"payment:atlaspay:{paymentId}:credit"));
            Assert.Equal(1, await users.PaymentSettlementNotifications.CountAsync(x =>
                x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
            Assert.Equal(0, await users.ReferralPaymentEvents.CountAsync(x =>
                x.SourcePaymentKey.StartsWith("atlaspay:")));
        }

        await using (var creds = databases.Credentials.CreateDbContext())
        {
            Assert.Equal(1, await creds.WalletOperations.CountAsync(x =>
                x.OperationKey == $"payment:atlaspay:{paymentId}:credit"));
        }
    }

    [Fact]
    public async Task AtlasPay_provisional_credit_rejects_tenant_order_even_for_configured_superadmin()
    {
        using var databases = new Databases();
        const long adminId = 990100;
        var baseConfiguration = AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["adminsUserIds:0"] = adminId.ToString()
            })
            .Build();

        await using var provider = AtlasTenantProvider(
            databases,
            configuration,
            new AtlasPay(configuration),
            out _,
            out _);
        await using var scope = provider.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
        await credentials.AddEmptyUser(7716);

        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId = 7716;
            payment.ChatId = 7716;
            payment.BotId = "tenant-test";
            payment.PaymentPurpose = TenantBotPaymentPurposes.TenantOrder;
            payment.ProviderStatus = "awaiting_payment";
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var settlement = scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>();
        var result = await settlement.ApplyProvisionalPaymentAsync(
            new AtlasPayPaymentInfo { Id = paymentId },
            adminId,
            7716);

        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
        Assert.Equal(0, await credentials.GetAccountBalance(7716));

        await using var users = databases.Users.CreateDbContext();
        var saved = await users.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.False(saved.IsProvisionallyApproved);
        Assert.False(saved.IsAddedToBalance);
        Assert.Equal(0, await users.WalletLedgerEntries.CountAsync(x =>
            x.ReferenceId == paymentId.ToString()));
    }

    [Fact]
    public async Task AtlasPay_provisional_credit_rejects_tenant_origin_wallet_charge()
    {
        using var databases = new Databases();
        const long adminId = 990101;
        const long customerId = 7717;
        var baseConfiguration = AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["adminsUserIds:0"] = adminId.ToString()
            })
            .Build();

        await using var provider = AtlasTenantProvider(
            databases,
            configuration,
            new AtlasPay(configuration),
            out _,
            out _);
        await using var scope = provider.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
        await credentials.AddEmptyUser(customerId);

        int paymentId;
        await using (var db = databases.Users.CreateDbContext())
        {
            var payment = VerifiedAtlasPayment();
            payment.TelegramUserId = customerId;
            payment.ChatId = customerId;
            payment.BotId = "tenant-wallet-origin";
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.WalletOriginBotType = BotInstanceTypes.Tenant;
            payment.ProviderStatus = "awaiting_payment";
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var settlement = scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>();
        var result = await settlement.ApplyProvisionalPaymentAsync(
            new AtlasPayPaymentInfo { Id = paymentId },
            adminId,
            customerId);

        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid, result.Status);
        Assert.Equal(0, await credentials.GetAccountBalance(customerId));

        await using var users = databases.Users.CreateDbContext();
        var saved = await users.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.False(saved.IsProvisionallyApproved);
        Assert.False(saved.IsAddedToBalance);
        Assert.Empty(await users.WalletLedgerEntries
            .Where(x => x.ReferenceId == paymentId.ToString())
            .ToListAsync());
    }
}
