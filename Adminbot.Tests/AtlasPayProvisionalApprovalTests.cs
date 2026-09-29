using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
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

    /// <summary>Regression: an expired AtlasPay order paid outside its original window needs a deliberate admin-only wallet decision.</summary>
    /// <returns>A task proving that a fresh expired verification can lead to one provisional receipt, not repeated credit.</returns>
    /// <remarks>The bank receipt remains an operator precondition; an expired provider response never authorizes automatic settlement. Later official proof must not repeat credit.</remarks>
    [Fact]
    public async Task AtlasPay_expired_owned_wallet_charge_can_be_provisionally_approved_once_after_fresh_verify()
    {
        using var databases = new Databases();
        const long adminId = 990102;
        const long customerId = 7718;
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["adminsUserIds:0"] = adminId.ToString()
            })
            .Build();
        var providerPaid = false;
        var handler = new AtlasHttpHandler((_, request, _, _) =>
        {
            Assert.EndsWith("/orders/77/verify", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(
                System.Net.HttpStatusCode.OK,
                providerPaid
                    ? StatusJson("confirmed", paid: true, manual: false, actual: 250123)
                    : StatusJson("expired", paid: false, manual: false)));
        });
        await using var provider = AtlasTenantProvider(databases, configuration,
            new AtlasPay(configuration, new HttpClient(handler)), out _, out _);
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
            payment.WalletOriginBotType = BotInstanceTypes.Owned;
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus = "expired";
            payment.ErrorCode = "provider_expired";
            payment.LastInquiryAtUtc = DateTime.UtcNow.AddDays(-2);
            payment.NextInquiryAtUtc = null;
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        var reconciler = scope.ServiceProvider.GetRequiredService<AtlasPayReconciliationHostedService>();
        // A terminal cache must not be mistaken for fresh payment evidence by the background worker.
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcilePaymentAsync(paymentId, "reconciliation-worker")).Status);
        Assert.Empty(handler.Captures);
        Assert.Equal(0, await credentials.GetAccountBalance(customerId));

        // The second-stage super-admin action verifies again before using the explicit human bank decision.
        var first = await reconciler.ReconcileAndApplyProvisionalAsync(
            paymentId, adminId, customerId, "admin-provisional-confirm-refresh");
        Assert.Equal(NowPaymentsSettlementStatus.Applied, first.Status);
        Assert.Single(handler.Captures);
        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));

        var again = await reconciler.ReconcileAndApplyProvisionalAsync(
            paymentId, adminId, customerId, "admin-provisional-confirm-refresh");
        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));
        Assert.True(again.Status is NowPaymentsSettlementStatus.AlreadyAdded or NowPaymentsSettlementStatus.ProviderNotPaid);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.Equal("expired", saved.ProviderStatus);
        Assert.True(saved.IsProvisionallyApproved);
        Assert.Equal(adminId, saved.ProvisionalApprovedByTelegramUserId);
        Assert.Null(saved.ProviderConfirmedAfterProvisionalAtUtc);
        Assert.Null(saved.NextInquiryAtUtc);
        Assert.Equal(1, await verify.WalletLedgerEntries.CountAsync(x =>
            x.IdempotencyKey == $"payment:atlaspay:{paymentId}:credit" &&
            x.Provider == "atlaspay_provisional_admin"));
        Assert.Equal(1, await verify.PaymentSettlementNotifications.CountAsync(x =>
            x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
        await using var receipts = databases.Credentials.CreateDbContext();
        Assert.Equal(1, await receipts.WalletOperations.CountAsync(x =>
            x.OperationKey == $"payment:atlaspay:{paymentId}:credit" &&
            x.ApprovalKind == "provisional"));

        providerPaid = true;
        var confirmed = await reconciler.ReconcilePaymentAsync(
            paymentId, "superadmin-verify", useVerify: true, terminalReviewAdminId: adminId);
        Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded, confirmed.Status);
        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));
        await using var confirmedDb = databases.Users.CreateDbContext();
        var confirmedRow = await confirmedDb.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.Equal("confirmed", confirmedRow.ProviderStatus);
        Assert.NotNull(confirmedRow.ProviderConfirmedAfterProvisionalAtUtc);
        Assert.Equal(1, await confirmedDb.WalletLedgerEntries.CountAsync(x =>
            x.IdempotencyKey == $"payment:atlaspay:{paymentId}:credit"));
        Assert.Equal(1, await confirmedDb.PaymentSettlementNotifications.CountAsync(x =>
            x.Provider == "atlaspay" && x.ProviderPaymentId == paymentId));
    }

    /// <summary>Regression: cached expiration cannot prevent a configured admin from discovering later official payment.</summary>
    /// <returns>A task verifying official settlement uses the saved base amount once and no manual override.</returns>
    /// <remarks>Customer/worker requests and unauthorized admin ids still skip the terminal cache without contacting AtlasPay.</remarks>
    [Fact]
    public async Task AtlasPay_expired_order_later_confirmed_is_officially_settled_only_after_admin_recheck()
    {
        using var databases = new Databases();
        const long adminId = 990103;
        const long customerId = 7719;
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["adminsUserIds:0"] = adminId.ToString() })
            .Build();
        var handler = new AtlasHttpHandler((_, request, _, _) =>
        {
            Assert.EndsWith("/orders/77/verify", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK,
                StatusJson("confirmed", paid: true, manual: false, actual: 250123)));
        });
        await using var provider = AtlasTenantProvider(databases, configuration,
            new AtlasPay(configuration, new HttpClient(handler)), out _, out _);
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
            payment.WalletOriginBotType = BotInstanceTypes.Owned;
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus = "expired";
            payment.ErrorCode = "provider_expired";
            payment.LastInquiryAtUtc = DateTime.UtcNow.AddDays(-2);
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }
        var reconciler = scope.ServiceProvider.GetRequiredService<AtlasPayReconciliationHostedService>();
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcilePaymentAsync(paymentId, "customer-check", useVerify: true)).Status);
        Assert.Equal(NowPaymentsSettlementStatus.InvalidAmount,
            (await reconciler.ReconcilePaymentAsync(paymentId, "superadmin-verify", useVerify: true,
                terminalReviewAdminId: adminId + 1)).Status);
        Assert.Empty(handler.Captures);

        var applied = await reconciler.ReconcilePaymentAsync(paymentId, "superadmin-verify", useVerify: true,
            terminalReviewAdminId: adminId);
        Assert.Equal(NowPaymentsSettlementStatus.Applied, applied.Status);
        Assert.Single(handler.Captures);
        Assert.Equal(250000, await credentials.GetAccountBalance(customerId));
        Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded,
            (await reconciler.ReconcilePaymentAsync(paymentId, "superadmin-verify", useVerify: true,
                terminalReviewAdminId: adminId)).Status);
        Assert.Single(handler.Captures);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.Equal("confirmed", saved.ProviderStatus);
        Assert.True(saved.IsAddedToBalance);
        Assert.False(saved.IsProvisionallyApproved);
        Assert.Equal(AtlasPaySettlementStates.Settled, saved.SettlementState);
        Assert.Equal(1, await verify.WalletLedgerEntries.CountAsync(x =>
            x.IdempotencyKey == $"payment:atlaspay:{paymentId}:credit" && x.Provider == "atlaspay"));
    }

    /// <summary>A mismatched expired response cannot become an eligible manual bank-verified exception.</summary>
    /// <returns>A task verifying that the local charge enters review and neither wallet nor ledger is credited.</returns>
    /// <remarks>This guards the provider id, merchant reference and amount boundary even for terminal statuses.</remarks>
    [Fact]
    public async Task AtlasPay_expired_response_with_wrong_merchant_reference_blocks_manual_credit()
    {
        using var databases = new Databases();
        const long adminId = 990104;
        const long customerId = 7720;
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["adminsUserIds:0"] = adminId.ToString() })
            .Build();
        var handler = new AtlasHttpHandler((_, request, _, _) =>
        {
            Assert.EndsWith("/orders/77/verify", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK,
                StatusJson("expired", paid: false).Replace("AtlasPay-test", "AtlasPay-different",
                    StringComparison.Ordinal)));
        });
        await using var provider = AtlasTenantProvider(databases, configuration,
            new AtlasPay(configuration, new HttpClient(handler)), out _, out _);
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
            payment.WalletOriginBotType = BotInstanceTypes.Owned;
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus = "expired";
            payment.ErrorCode = "provider_expired";
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }
        var reconciler = scope.ServiceProvider.GetRequiredService<AtlasPayReconciliationHostedService>();
        Assert.Equal(NowPaymentsSettlementStatus.ProviderNotPaid,
            (await reconciler.ReconcileAndApplyProvisionalAsync(
                paymentId, adminId, customerId, "admin-provisional-confirm-refresh")).Status);
        Assert.Single(handler.Captures);
        Assert.Equal(0, await credentials.GetAccountBalance(customerId));
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.AtlasPayPaymentInfos.SingleAsync(x => x.Id == paymentId);
        Assert.Equal("merchant_order_ref_mismatch", saved.ErrorCode);
        Assert.Equal(AtlasPaySettlementStates.ManualReview, saved.SettlementState);
        Assert.False(AtlasPaySettlementService.CanApplyProvisionalCredit(saved));
        Assert.False(saved.IsAddedToBalance);
        Assert.Empty(verify.WalletLedgerEntries.Where(x => x.ReferenceId == paymentId.ToString()));
    }

    /// <summary>Terminal override never admits stale, failed, cancelled or known short-paid orders.</summary>
    /// <param name="status">Provider status returned by the latest local observation.</param>
    /// <param name="errorCode">Stable diagnostic associated with that observation.</param>
    /// <param name="minutesSinceInquiry">Age of the official verification in minutes; two-minute freshness is required.</param>
    /// <param name="actualToman">Optional amount actually received by AtlasPay, in toman.</param>
    [Theory]
    [InlineData("expired", "provider_expired", 5, null)]
    [InlineData("expired", "provider_unavailable", 0, null)]
    [InlineData("cancelled", "provider_cancelled", 0, null)]
    [InlineData("rejected", "provider_rejected", 0, null)]
    [InlineData("expired", "provider_expired", 0, 250050L)]
    public void AtlasPay_expired_admin_exception_rejects_unproven_or_underpaid_charge(
        string status, string errorCode, int minutesSinceInquiry, long? actualToman)
    {
        var payment = VerifiedAtlasPayment();
        payment.WalletOriginBotType = BotInstanceTypes.Owned;
        payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
        payment.ProviderStatus = status;
        payment.ErrorCode = errorCode;
        payment.LastInquiryAtUtc = DateTime.UtcNow.AddMinutes(-minutesSinceInquiry);
        payment.ActualReceivedAmountToman = actualToman;

        Assert.False(AtlasPaySettlementService.CanApplyProvisionalCredit(payment));
    }

    /// <summary>Simulates the actual admin message and both Telegram buttons for a still-expired owned-wallet charge.</summary>
    /// <returns>A task verifying visible bank-evidence warning, final official recheck, and one durable credit.</returns>
    /// <remarks>The first button alone must never change wallet or ledger; only the authenticated second decision may do so.</remarks>
    [Fact]
    public async Task AtlasPay_admin_expired_payment_screen_requires_two_confirmations_before_wallet_credit()
    {
        using var databases = new Databases();
        const long adminId = 990105;
        const long customerId = 7721;
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(AtlasTenantConfiguration(databases, "http://127.0.0.1:59999/"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["adminsUserIds:0"] = adminId.ToString(),
                ["xuiApiVersionMode"] = "v3"
            })
            .Build();
        var handler = new AtlasHttpHandler((_, request, _, _) =>
        {
            Assert.EndsWith("/orders/77/verify", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK,
                StatusJson("expired", paid: false, manual: false)));
        });
        await using var provider = AtlasTenantProvider(databases, configuration,
            new AtlasPay(configuration, new HttpClient(handler)), out _, out _);
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
            payment.WalletOriginBotType = BotInstanceTypes.Owned;
            payment.PaymentPurpose = TenantBotPaymentPurposes.WalletCharge;
            payment.ProviderStatus = "expired";
            payment.ErrorCode = "provider_expired";
            payment.LastInquiryAtUtc = DateTime.UtcNow.AddDays(-2);
            db.AtlasPayPaymentInfos.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }
        var client = new StorefrontClient();
        var accessor = new BotContextAccessor();
        using (accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "main", Type = BotInstanceTypes.Owned },
            Client = client
        }))
        {
            var state = scope.ServiceProvider.GetRequiredService<UserStateStore>();
            await state.ResetUserStatus(new global::User
            {
                Id = adminId, Flow = "xui-v3-admin", LastStep = "get-nowpayment-status"
            });
            var flow = scope.ServiceProvider.GetRequiredService<XuiV3AdminFlowService>();
            var message = new Message
            {
                From = new Telegram.Bot.Types.User { Id = adminId },
                Chat = new Chat { Id = adminId },
                Text = $"AP:{paymentId}"
            };
            var keyboard = new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("Home") } });
            Assert.True(await flow.TryHandleMessageAsync(
                client, message, await state.GetUserStatus(adminId), keyboard, CancellationToken.None));
            Assert.Single(handler.Captures);
            Assert.Contains(client.Texts, text =>
                text.Contains("وضعیت expired", StringComparison.Ordinal) &&
                text.Contains("حساب بانکی", StringComparison.Ordinal));
            Assert.Contains($"x3admin:ap:provisional:{paymentId}", client.Callbacks);
            Assert.Equal(0, await credentials.GetAccountBalance(customerId));

            /// <summary>Creates a Telegram callback against the shown payment without trusting its sender.</summary>
            /// <param name="action">The exact protected first- or second-stage callback prefix.</param>
            /// <param name="senderId">Telegram user id claiming to press the button; defaults to the configured admin.</param>
            /// <returns>A test callback addressed to the current local payment and admin chat.</returns>
            CallbackQuery Callback(string action, long senderId = adminId) => new()
            {
                Id = "admin-expired-" + action,
                From = new Telegram.Bot.Types.User { Id = senderId },
                Data = action + paymentId,
                Message = new Message { Id = 21, Chat = new Chat { Id = adminId } }
            };
            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback("x3admin:ap:provisional:"), keyboard, CancellationToken.None));
            Assert.Contains(client.Texts, text => text.Contains("این وضعیت و Webhook به‌تنهایی پرداخت را ثابت نمی‌کنند", StringComparison.Ordinal));
            Assert.Contains($"x3admin:ap:provisional-confirm:{paymentId}", client.Callbacks);
            Assert.Equal(0, await credentials.GetAccountBalance(customerId));
            // The callback payload alone cannot authorize a different Telegram sender to credit this wallet.
            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback("x3admin:ap:provisional-confirm:", adminId + 1),
                keyboard, CancellationToken.None));
            Assert.Equal(0, await credentials.GetAccountBalance(customerId));
            Assert.Single(handler.Captures);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback("x3admin:ap:provisional-confirm:"), keyboard, CancellationToken.None));
            Assert.Equal(250000, await credentials.GetAccountBalance(customerId));
            Assert.Equal(2, handler.Captures.Count);
            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback("x3admin:ap:provisional-confirm:"), keyboard, CancellationToken.None));
            Assert.Equal(250000, await credentials.GetAccountBalance(customerId));
        }
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(1, await verify.WalletLedgerEntries.CountAsync(x =>
            x.IdempotencyKey == $"payment:atlaspay:{paymentId}:credit"));
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
