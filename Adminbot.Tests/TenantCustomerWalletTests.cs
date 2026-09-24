using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>Real SQLite trust and no-overdraft regressions for the shared global customer wallet.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Different storefronts cannot spend the same available funds twice, including after recreating the store.</summary>
    /// <returns>A task completing after concurrent transactions and immutable receipts are checked.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Concurrent_orders_never_overdraw_and_restart_reuses_receipt()
    {
        using var databases = new Databases();
        var store = new CredentialsStore(databases.Credentials);
        await store.AddEmptyUser(123);
        await store.MutateWalletAsync(123, 500_000, "fixture:fund");
        var start = Signal();
        var tasks = Enumerable.Range(1, 2).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return await new CredentialsStore(databases.Credentials).TryDebitWalletIfSufficientAsync(123, 400_000,
                $"tenant-customer-wallet:{i}:debit", $"tenant-{i}");
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        var committed = Assert.Single(results, x => x != null);
        Assert.Equal(100_000, await store.GetAccountBalance(123));
        var recovered = await new CredentialsStore(databases.Credentials).TryDebitWalletIfSufficientAsync(123, 400_000,
            committed.OperationKey, committed.BotId);
        Assert.Equal(committed.AfterBalance, recovered.AfterBalance);
        Assert.Equal(100_000, await store.GetAccountBalance(123));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryDebitWalletIfSufficientAsync(123, 1, committed.OperationKey, committed.BotId));
        await using var db = databases.Credentials.CreateDbContext();
        Assert.Equal(1, await db.WalletOperations.CountAsync(x => x.AmountToman < 0));
    }

    /// <summary>Concurrent delivery of one business key creates one debit; insufficient funds leave neither debit nor receipt.</summary>
    /// <returns>A task completing after the independently opened SQLite connections finish.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Duplicate_debit_and_insufficient_balance_are_atomic()
    {
        using var databases = new Databases();
        var store = new CredentialsStore(databases.Credentials);
        await store.AddEmptyUser(123);
        Assert.Null(await store.TryDebitWalletIfSufficientAsync(123, 1, "insufficient", "tenant-a"));
        Assert.Null(await store.GetWalletOperationAsync("insufficient"));
        await store.MutateWalletAsync(123, 100, "fixture:fund");
        var start = Signal();
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        { await start.Task; return await store.TryDebitWalletIfSufficientAsync(123, 100, "tenant-customer-wallet:1:debit", "tenant-a"); })).ToArray();
        start.SetResult();
        Assert.All(await Task.WhenAll(tasks), receipt => Assert.Equal(0, receipt.AfterBalance));
        Assert.Equal(0, await store.GetAccountBalance(123));
    }

    /// <summary>Only configured super admins grant exact-store permission; revocation and identity mismatch fail closed.</summary>
    /// <returns>A task completing after persisted permission and unauthorized-write assertions.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Approval_requires_super_admin_and_exact_store_identity()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance { Id = "tenant-a", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 123, TelegramBotId = 456 });
            await db.SaveChangesAsync();
        }
        var policy = new TenantCustomerWalletPolicy(databases.Users, new AppConfig { AdminsUserIds = new() { 999 } },
            NullLogger<TenantCustomerWalletPolicy>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.RequireAsync("tenant-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetOwnerEnabledAsync(123, "tenant-a", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => policy.SetAsync(123, "tenant-a", 456, 123, true));
        await policy.SetAsync(999, "tenant-a", 456, 123, true);
        await using (var grantedDb = databases.Users.CreateDbContext())
        {
            var granted = await grantedDb.BotInstances.AsNoTracking().SingleAsync();
            Assert.True(TenantCustomerWalletPolicy.HasValidGrant(granted));
            Assert.False(granted.TenantCustomerWalletOwnerEnabled);
            Assert.False(TenantCustomerWalletPolicy.IsApproved(granted));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => policy.SetOwnerEnabledAsync(555, "tenant-a", true));
        await policy.SetOwnerEnabledAsync(123, "tenant-a", true);
        Assert.True(TenantCustomerWalletPolicy.IsApproved(await policy.RequireAsync("tenant-a")));
        await using (var db = databases.Users.CreateDbContext())
            await db.BotInstances.Where(x => x.Id == "tenant-a").ExecuteUpdateAsync(s => s.SetProperty(x => x.TelegramBotId, (long?)457));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.RequireAsync("tenant-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetAsync(999, "tenant-a", 456, 123, true));
        await policy.SetAsync(999, "tenant-a", 457, 123, false);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.BotInstances.SingleAsync();
        Assert.False(row.TenantCustomerWalletEnabled);
        Assert.False(row.TenantCustomerWalletOwnerEnabled);
        Assert.Null(row.TenantCustomerWalletApprovedAtUtc);
        Assert.Null(row.TenantCustomerWalletApprovedByTelegramUserId);
        Assert.Null(row.TenantCustomerWalletApprovedBotId);
        Assert.Null(row.TenantCustomerWalletApprovedOwnerId);
    }

    /// <summary>Owner activation is impossible before a super-admin grant, while revocation clears both sides of consent.</summary>
    /// <returns>A task completing after policy, persisted-state, and owner-panel button assertions.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Owner_activation_requires_grant_and_revocation_clears_opt_in()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance { Id = "tenant-owner-optin", Type = BotInstanceTypes.Tenant,
                OwnerTelegramUserId = 456, TelegramBotId = 789, TenantStoreNumber = 1 });
            await db.SaveChangesAsync();
        }

        var policy = new TenantCustomerWalletPolicy(databases.Users, new AppConfig { AdminsUserIds = new() { 999 } },
            NullLogger<TenantCustomerWalletPolicy>.Instance);
        var keyboardMethod = typeof(TenantBotService).GetMethod("BUILDOWNERPANELKEYBOARD",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.AsNoTracking().SingleAsync();
            var keyboard = (Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup)keyboardMethod.Invoke(null, new object[] { store })!;
            var walletButton = Assert.Single(keyboard.InlineKeyboard.SelectMany(x => x),
                x => x.CallbackData?.Contains("set-setting:wallet:", StringComparison.Ordinal) == true);
            Assert.Contains("🔒", walletButton.Text);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.SetOwnerEnabledAsync(456, "tenant-owner-optin", true));
        await policy.SetAsync(999, "tenant-owner-optin", 789, 456, true);
        await using (var db = databases.Users.CreateDbContext())
        {
            var granted = await db.BotInstances.AsNoTracking().SingleAsync();
            Assert.True(TenantCustomerWalletPolicy.HasValidGrant(granted));
            Assert.False(granted.TenantCustomerWalletOwnerEnabled);
            Assert.False(TenantCustomerWalletPolicy.IsApproved(granted));
            var keyboard = (Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup)keyboardMethod.Invoke(null, new object[] { granted })!;
            var walletButton = Assert.Single(keyboard.InlineKeyboard.SelectMany(x => x),
                x => x.CallbackData?.Contains("set-setting:wallet:", StringComparison.Ordinal) == true);
            Assert.Contains("فعال‌سازی", walletButton.Text);
        }

        await policy.SetOwnerEnabledAsync(456, "tenant-owner-optin", true);
        Assert.True(TenantCustomerWalletPolicy.IsApproved(await policy.RequireAsync("tenant-owner-optin")));
        await policy.SetAsync(999, "tenant-owner-optin", 789, 456, false);
        await using var verify = databases.Users.CreateDbContext();
        var revoked = await verify.BotInstances.AsNoTracking().SingleAsync();
        Assert.False(TenantCustomerWalletPolicy.HasValidGrant(revoked));
        Assert.False(revoked.TenantCustomerWalletOwnerEnabled);
        Assert.False(TenantCustomerWalletPolicy.IsApproved(revoked));
    }

    /// <summary>A committed debit repairs users.db after restart and remains recoverable after approval is revoked.</summary>
    /// <returns>A task completing after receipt-only crash recovery and owner-scoped history checks.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Debit_crash_reconciles_exact_tenant_ledger_after_revocation()
    {
        using var databases = new Databases();
        var (wallet, funding, order) = await SeedCustomerWalletOrderAsync(databases);
        var receipt = await wallet.TryDebitWalletIfSufficientAsync(123, order.SalePriceToman,
            TenantCustomerWalletFunding.DebitKey(order.Id), order.TenantBotId);
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.TenantMandatoryJoinEnabled = true;
            store.TenantChannelIdsJson = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { "@required" });
            TenantCustomerWalletPolicy.Revoke(store);
            await db.SaveChangesAsync();
            Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
        }
        await using (var db = databases.Credentials.CreateDbContext())
            await db.WalletOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var ledger = new WalletLedgerService(databases.Users, wallet);
        var recovery = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger,
            NullLogger<WalletOperationReconciliationService>.Instance);
        await recovery.ReconcileAsync(); await recovery.ReconcileAsync();
        await using var verify = databases.Users.CreateDbContext();
        var entry = await verify.WalletLedgerEntries.SingleAsync(x => x.IdempotencyKey == receipt.OperationKey);
        Assert.Equal(BotInstanceTypes.Tenant, entry.BotType); Assert.Equal("wallet", entry.Provider);
        Assert.Equal("tenant-order", entry.ReferenceType); Assert.Equal(order.TenantBotId, entry.BotId);
        Assert.Equal("paid", (await verify.TenantBotOrders.SingleAsync()).CustomerWalletState);
        Assert.Equal(400_000, await wallet.GetAccountBalance(123));
        Assert.Empty((await ledger.GetPageAsync(999, 0, 8)).Items);
        Assert.NotNull(await funding.ReadPaidEvidenceAsync(await verify.TenantBotOrders.SingleAsync()));
    }

    /// <summary>Every unresolved/applied creation outcome forbids compensation; proven rejection compensates exactly once.</summary>
    /// <param name="outcome">Durable creation outcome from the existing XUI coordinator.</param>
    /// <param name="canRefund">Whether the external mutation is definitively known not to have happened.</param>
    /// <returns>A task completing after duplicate compensation and receipt assertions.</returns>
    [Theory]
    [InlineData(XuiV3CreationOutcome.Reserved, false)]
    [InlineData(XuiV3CreationOutcome.PostStarted, false)]
    [InlineData(XuiV3CreationOutcome.Ambiguous, false)]
    [InlineData(XuiV3CreationOutcome.Applied, false)]
    [InlineData(XuiV3CreationOutcome.DefinitiveRejected, true)]
    public async Task TenantCustomerWallet_Creation_compensation_requires_definitive_rejection(XuiV3CreationOutcome outcome, bool canRefund)
    {
        using var databases = new Databases();
        var (wallet, funding, order) = await SeedCustomerWalletOrderAsync(databases);
        Assert.True(await funding.DebitAsync(order));
        await using (var db = databases.Users.CreateDbContext())
        {
            await db.TenantBotOrders.ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentStatus, TenantBotOrderStatuses.Failed));
            db.XuiV3CreationOperations.Add(new XuiV3CreationOperation { OperationKey = $"tenant-create:{order.Id}", Outcome = outcome,
                PanelKey = "test", TelegramUserId = 123, ClientJson = "{}", InboundIdsJson = "[]", BusinessParametersJson = "{}" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(canRefund, await funding.RefundRejectedAsync(order.Id));
        await funding.RefundRejectedAsync(order.Id);
        Assert.Equal(canRefund ? 500_000 : 400_000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Credentials.CreateDbContext();
        Assert.Equal(canRefund ? 1 : 0, await verify.WalletOperations.CountAsync(x => x.OperationKey.EndsWith(":refund")));
        if (canRefund) Assert.False(await funding.DebitAsync(order));
    }

    /// <summary>Renewal compensation never treats ambiguous, applied or manual-review operations as rejected.</summary>
    /// <param name="status">Persisted renewal mutation state.</param>
    /// <param name="canRefund">Expected permission to issue one exact compensation credit.</param>
    /// <returns>A task completing after real SQLite compensation.</returns>
    [Theory]
    [InlineData(XuiV3RenewalOperationStatuses.Pending, false)]
    [InlineData(XuiV3RenewalOperationStatuses.Processing, false)]
    [InlineData(XuiV3RenewalOperationStatuses.Ambiguous, false)]
    [InlineData(XuiV3RenewalOperationStatuses.ManualReview, false)]
    [InlineData(XuiV3RenewalOperationStatuses.Applied, false)]
    [InlineData(XuiV3RenewalOperationStatuses.Failed, true)]
    public async Task TenantCustomerWallet_Renewal_compensation_requires_definitive_rejection(string status, bool canRefund)
    {
        using var databases = new Databases();
        var (wallet, funding, order) = await SeedCustomerWalletOrderAsync(databases);
        Assert.True(await funding.DebitAsync(order));
        await using (var db = databases.Users.CreateDbContext())
        {
            await db.TenantBotOrders.ExecuteUpdateAsync(s => s.SetProperty(x => x.OrderKind, TenantBotOrderKinds.Renew)
                .SetProperty(x => x.PaymentStatus, TenantBotOrderStatuses.Failed));
            db.XuiV3RenewalOperations.Add(new XuiV3RenewalOperation { OperationKey = "tenant-renew-" + order.OrderId,
                OperationId = "fixture-renewal", Status = status, TargetEmail = "test", TargetUuid = Guid.NewGuid().ToString() });
            await db.SaveChangesAsync();
        }
        Assert.Equal(canRefund, await funding.RefundRejectedAsync(order.Id));
        await funding.RefundRejectedAsync(order.Id);
        Assert.Equal(canRefund ? 500_000 : 400_000, await wallet.GetAccountBalance(123));
    }

    /// <summary>A crash after refund credit repairs only metadata and one notification, never another credit or provisioning attempt.</summary>
    /// <returns>A task completing after repeated receipt reconciliation across both databases.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Refund_credit_crash_recovers_terminal_marker_and_notification_once()
    {
        using var databases = new Databases();
        var (wallet, funding, order) = await SeedCustomerWalletOrderAsync(databases);
        Assert.True(await funding.DebitAsync(order));
        await using (var db = databases.Users.CreateDbContext())
            await db.TenantBotOrders.ExecuteUpdateAsync(s => s.SetProperty(x => x.CustomerWalletState, "refund_pending")
                .SetProperty(x => x.PaymentStatus, TenantBotOrderStatuses.Failed));
        await wallet.MutateWalletAsync(123, order.SalePriceToman, $"tenant-customer-wallet:{order.Id}:refund", botId: order.TenantBotId);
        await using (var db = databases.Credentials.CreateDbContext())
            await db.WalletOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var recovery = new WalletOperationReconciliationService(databases.Credentials, databases.Users,
            new WalletLedgerService(databases.Users, wallet), NullLogger<WalletOperationReconciliationService>.Instance);
        await recovery.ReconcileAsync(); await recovery.ReconcileAsync();
        Assert.False(await funding.DebitAsync(order));
        Assert.Equal(500000, await wallet.GetAccountBalance(123));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal("refunded", (await verify.TenantBotOrders.SingleAsync()).CustomerWalletState);
        Assert.Single(await verify.WalletLedgerEntries.Where(x => x.Reason == "account_refund").ToListAsync());
        Assert.Single(await verify.PaymentSettlementNotifications.Where(x => x.NotificationKey == $"tenant-wallet-refund:{order.Id}").ToListAsync());
    }

    /// <summary>Both destructive reset and invalid-token cleanup erase approval rather than transferring trust to a replacement identity.</summary>
    /// <param name="fullReset">Whether the production helper clears all owner settings or only token identity.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TenantCustomerWallet_Reset_erases_identity_bound_trust(bool fullReset)
    {
        var store = new BotInstance { TenantCustomerWalletEnabled = true, TenantCustomerWalletOwnerEnabled = true,
            TenantCustomerWalletApprovedAtUtc = DateTime.UtcNow, TenantCustomerWalletApprovedByTelegramUserId = 999,
            TenantCustomerWalletApprovedBotId = 789, TenantCustomerWalletApprovedOwnerId = 456 };
        typeof(TenantBotService).GetMethod("ResetTenantStorefrontSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { store, fullReset });
        Assert.False(store.TenantCustomerWalletEnabled); Assert.False(store.TenantCustomerWalletOwnerEnabled);
        Assert.Null(store.TenantCustomerWalletApprovedAtUtc);
        Assert.Null(store.TenantCustomerWalletApprovedByTelegramUserId); Assert.Null(store.TenantCustomerWalletApprovedBotId);
        Assert.Null(store.TenantCustomerWalletApprovedOwnerId);
    }

    /// <summary>Builds an approved store and an admitted order using production admission, without external services.</summary>
    /// <param name="databases">Disposable real SQLite fixture owned by this test.</param>
    /// <returns>The canonical wallet, financial bridge and persisted detached order.</returns>
    private static async Task<(CredentialsStore Wallet, TenantCustomerWalletFunding Funding, TenantBotOrder Order)> SeedCustomerWalletOrderAsync(Databases databases)
    {
        var wallet = new CredentialsStore(databases.Credentials);
        await wallet.AddEmptyUser(123); await wallet.MutateWalletAsync(123, 500_000, "fixture:fund");
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance { Id = "tenant-a", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 456,
                TelegramBotId = 789, TenantStoreNumber = 1, TenantCustomerWalletEnabled = true, TenantCustomerWalletOwnerEnabled = true,
                TenantCustomerWalletApprovedAtUtc = DateTime.UtcNow, TenantCustomerWalletApprovedByTelegramUserId = 999,
                TenantCustomerWalletApprovedOwnerId = 456, TenantCustomerWalletApprovedBotId = 789 });
            await db.SaveChangesAsync();
        }
        var funding = new TenantCustomerWalletFunding(databases.Users, wallet, new WalletLedgerService(databases.Users, wallet));
        var order = await funding.AdmitAsync(new TenantBotOrder { TenantBotId = "tenant-a", OwnerTelegramUserId = 456,
            CustomerTelegramUserId = 123, CustomerChatId = 123, OrderId = "fixture-order", OrderKind = TenantBotOrderKinds.Purchase,
            SalePriceToman = 100_000, BaseCostToman = 80_000, ProfitToman = 20_000, ServiceKey = "normal", AccountCount = 1 }, "fixture-admission");
        return (wallet, funding, order);
    }
}
