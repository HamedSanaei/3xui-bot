using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>Exercises owned-colleague paid-test consent, live eligibility and exact receipt-backed compensation in isolated SQLite fixtures.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Zero free allowance still offers the identical small three-day test at live colleague rates.</summary>
    /// <param name="serviceKey">Normal one-GiB or national 100-MiB test selection.</param>
    /// <param name="expectedPrice">Whole-toman payable amount after actual traffic plus three daily charges and one upward rounding.</param>
    /// <returns>A task completing after paid preview and no-premature-debit invariants are verified.</returns>
    /// <remarks>Protects against hard denial after quota, billing national traffic as a full GiB, public rates, or silently bypassing confirmation. Regular ten-GiB minimum remains intact.</remarks>
    [Theory]
    [InlineData("normal", 3900L)]
    [InlineData("national", 1096L)]
    public async Task ColleagueTrialFlow_Zero_allowance_offers_same_test_at_colleague_price_without_debit(
        string serviceKey, long expectedPrice)
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var (profile, pending, grant) = await PrepareColleaguePaidTrialAsync(provider, new StorefrontClient(), serviceKey);
        Assert.Equal(ColleagueTrialGrantState.Denied, grant.State);
        Assert.Equal(expectedPrice, grant.PaidQuoteToman);
        Assert.Equal("trial-confirm-paid", pending.LastStep);
        Assert.Equal(grant.Id, pending.PurchaseSessionId);
        Assert.Equal(20000, await provider.GetRequiredService<CredentialsStore>().GetAccountBalance(profile.TelegramUserId));
        await using var db = databases.Users.CreateDbContext();
        Assert.Empty(await db.XuiV3CreationOperations.ToListAsync());
        Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<XuiV3PurchaseService>()
            .ResolveOwnedPurchase(new XuiV3PurchaseSelection { ServiceKey = serviceKey, TrafficGb = 1, DurationKey = "m1" }, true));
    }

    /// <summary>A removed persisted sender cannot reuse its detached active-colleague snapshot for a new test.</summary>
    /// <returns>A task completing after new delivery admission and panel mutation are both rejected.</returns>
    /// <remarks>Protects the live-role boundary when profile removal races a queued update; existing financial receipts are not rewritten.</remarks>
    [Fact]
    public async Task ColleagueTrialFlow_Removed_profile_cannot_admit_from_stale_colleague_snapshot()
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var client = new StorefrontClient();
        var (profile, pending, original) = await PrepareColleaguePaidTrialAsync(provider, client, "normal");
        await using (var wallets = databases.Credentials.CreateDbContext())
            await wallets.Users.Where(x => x.TelegramUserId == profile.TelegramUserId).ExecuteDeleteAsync();
        var flow = provider.GetRequiredService<XuiV3BotFlowService>();
        await flow.TryHandleTrialAsync(client, ColleagueTrialMessage(profile.TelegramUserId, 3, "🌟اکانت تست"),
            profile, pending, null, default);
        await flow.TryHandleTrialAsync(client, ColleagueTrialMessage(profile.TelegramUserId, 4, "[normal]"),
            profile, pending, null, default);
        await using var db = databases.Users.CreateDbContext();
        Assert.False(await db.ColleagueTrialGrants.AnyAsync(x => x.Id != original.Id));
        Assert.Empty(await db.XuiV3CreationOperations.ToListAsync());
    }

    /// <summary>A role revoked after preview never permits a new colleague debit from a stale sender snapshot.</summary>
    /// <returns>A task completing after persisted eligibility and no-financial-effect assertions.</returns>
    /// <remarks>The same actor may retain ordinary-customer trial eligibility; this scenario only forbids using its old colleague quote for a new debit.</remarks>
    [Fact]
    public async Task ColleagueTrialFlow_Paid_confirmation_reloads_revoked_role_before_debit()
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var client = new StorefrontClient();
        var (profile, pending, grant) = await PrepareColleaguePaidTrialAsync(provider, client, "normal");
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.PromotOrDemote(profile.TelegramUserId, false);
        await ConfirmColleagueTrialFixtureAsync(provider, client, profile, pending, grant, 3900);
        Assert.Equal(20000, await credentials.GetAccountBalance(profile.TelegramUserId));
        await using var db = databases.Credentials.CreateDbContext();
        Assert.Empty(await db.WalletOperations.Where(x => x.OperationKey.StartsWith("colleague-paid-trial:")).ToListAsync());
    }

    /// <summary>A catalog change between preview and approval requires another confirmation without a silent higher debit.</summary>
    /// <returns>A task completing after the original confirmation refreshes the persisted price only.</returns>
    /// <remarks>Rates are live; the first approval belongs to its displayed 3,900-toman quote, not the newly configured 4,900-toman quote.</remarks>
    [Fact]
    public async Task ColleagueTrialFlow_Changed_price_refreshes_quote_without_charging_old_confirmation()
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var client = new StorefrontClient();
        var (profile, pending, grant) = await PrepareColleaguePaidTrialAsync(provider, client, "normal");
        await WriteColleagueTrialCatalogAsync(databases, normalRate: 4000);
        await ConfirmColleagueTrialFixtureAsync(provider, client, profile, pending, grant, 3900);
        await ConfirmColleagueTrialFixtureAsync(provider, client, profile, pending, grant, 3900);
        var refreshed = await provider.GetRequiredService<ColleagueTrialQuotaStore>().FindAsync(grant.Id,
            profile.TelegramUserId, grant.BotId);
        Assert.Equal(4900, refreshed.PaidQuoteToman);
        Assert.Equal(ColleagueTrialPaidCreationState.NotStarted, refreshed.PaidCreationState);
        Assert.Equal(20000, await provider.GetRequiredService<CredentialsStore>().GetAccountBalance(profile.TelegramUserId));
        await using var db = databases.Credentials.CreateDbContext();
        Assert.Empty(await db.WalletOperations.Where(x => x.OperationKey.StartsWith("colleague-paid-trial:")).ToListAsync());
    }

    /// <summary>Duplicate paid confirmations compensate once when Telegram fails after debit but before panel provisioning.</summary>
    /// <returns>A task completing after the original balance, one debit/refund and no panel-operation invariants hold.</returns>
    /// <remarks>Regression boundary: progress delivery can fail between the committed sufficient-balance receipt and the first creation reservation. A sole executor makes absence definitive for that winner only.</remarks>
    [Fact]
    public async Task ColleagueTrialFlow_Concurrent_progress_failure_refunds_once_without_creation_or_replay()
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var client = new PaidTrialFailureClient();
        var (profile, pending, grant) = await PrepareColleaguePaidTrialAsync(provider, client, "normal");
        var credentials = provider.GetRequiredService<CredentialsStore>();
        var quotas = provider.GetRequiredService<ColleagueTrialQuotaStore>();
        client.FailWhen = async () => (await quotas.FindAsync(grant.Id, profile.TelegramUserId, grant.BotId))
            .PaidCreationState == ColleagueTrialPaidCreationState.Started;
        var flow = provider.GetRequiredService<XuiV3BotFlowService>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                await ConfirmColleagueTrialFixtureAsync(provider, client, profile, pending, grant, 3900);
            }
            catch (HttpRequestException) { /* Controlled Telegram outage; financial proof must still settle. */ }
        }));
        Assert.Equal(20000, await credentials.GetAccountBalance(profile.TelegramUserId));
        await using var users = databases.Users.CreateDbContext();
        var settled = await users.ColleagueTrialGrants.SingleAsync();
        Assert.Equal(ColleagueTrialPaidCreationState.Rejected, settled.PaidCreationState);
        Assert.Empty(await users.XuiV3CreationOperations.ToListAsync());
        Assert.Equal(new[] { -3900L, 3900L }, (await ReadColleagueTrialReceiptsAsync(databases)).Order().ToArray());
        var rows = await users.WalletLedgerEntries.Where(x => x.ReferenceId == grant.Id).ToListAsync();
        Assert.Equal(3900, Assert.Single(rows, x => x.Direction == WalletLedgerDirections.Debit).AmountToman);
        Assert.Equal(3900, Assert.Single(rows, x => x.Direction == WalletLedgerDirections.Credit).AmountToman);
        Assert.Equal(WalletLedgerReasons.ColleagueTrialRefund,
            Assert.Single(rows, x => x.Direction == WalletLedgerDirections.Credit).Reason);
        // A fresh service execution must not replay the old refunded request, even with its stale conversation snapshot.
        await ConfirmColleagueTrialFixtureAsync(provider, new StorefrontClient(), profile, pending, grant, 3900);
        Assert.Equal(new[] { -3900L, 3900L }, (await ReadColleagueTrialReceiptsAsync(databases)).Order().ToArray());
        Assert.Equal(20000, await credentials.GetAccountBalance(profile.TelegramUserId));
    }

    /// <summary>Cancellation cannot forget or refund an already funded Started request whose panel outcome is unknown.</summary>
    /// <returns>A task completing after the unchanged debit and resumable paid-session invariants.</returns>
    /// <remarks>A crash after the sole-executor claim but before a creation row exists is review-only; absence does not authorize a losing cancellation to refund or POST.</remarks>
    [Fact]
    public async Task ColleagueTrialFlow_Funded_cancel_preserves_session_and_unknown_debit()
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var client = new StorefrontClient();
        var (profile, pending, grant) = await PrepareColleaguePaidTrialAsync(provider, client, "normal");
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.TryDebitWalletIfSufficientAsync(profile.TelegramUserId, 3900,
            $"colleague-paid-trial:{grant.Id}:debit", grant.BotId);
        await provider.GetRequiredService<ColleagueTrialQuotaStore>().TryStartPaidCreationAsync(grant.Id,
            profile.TelegramUserId, grant.BotId, 3900, DateTime.UtcNow);
        await provider.GetRequiredService<XuiV3BotFlowService>().TryHandleTrialAsync(client,
            ColleagueTrialMessage(profile.TelegramUserId, 3, "انصراف"), profile, pending, null, default);
        Assert.Equal(16100, await credentials.GetAccountBalance(profile.TelegramUserId));
        Assert.Equal(grant.Id, (await provider.GetRequiredService<UserStateStore>()
            .GetUserStatus(profile.TelegramUserId)).PurchaseSessionId);
        Assert.Equal(new[] { -3900L }, await ReadColleagueTrialReceiptsAsync(databases));
        await using var db = databases.Users.CreateDbContext();
        Assert.Empty(await db.XuiV3CreationOperations.ToListAsync());
    }

    /// <summary>Receipt reconciliation repairs a paid-test rejection-before-refund crash even after all conversation state is cleared.</summary>
    /// <returns>A task completing after the exact grant-linked sale/refund audit and original balance are restored.</returns>
    /// <remarks>The preview is deliberately changed before executor freeze so recovery must trust the committed 3,900-toman receipt, not a mutable 4,900-toman offer.</remarks>
    [Fact]
    public async Task ColleagueTrialFlow_Receipt_recovery_preserves_paid_test_financial_attribution()
    {
        using var databases = new Databases();
        var catalog = await WriteColleagueTrialCatalogAsync(databases);
        await using var provider = StorefrontProvider(databases, catalogPath: catalog);
        var (profile, _, grant) = await PrepareColleaguePaidTrialAsync(provider, new StorefrontClient(), "normal");
        var credentials = provider.GetRequiredService<CredentialsStore>();
        var quotas = provider.GetRequiredService<ColleagueTrialQuotaStore>();
        var key = $"colleague-paid-trial:{grant.Id}";
        await credentials.TryDebitWalletIfSufficientAsync(profile.TelegramUserId, 3900, key + ":debit", grant.BotId);
        await quotas.SetPaidQuoteAsync(grant.Id, profile.TelegramUserId, grant.BotId, 4900, DateTime.UtcNow);
        await quotas.TryStartPaidCreationAsync(grant.Id, profile.TelegramUserId, grant.BotId, 3900, DateTime.UtcNow);
        await quotas.SettlePaidCreationAsync(grant.Id, profile.TelegramUserId, grant.BotId, DateTime.UtcNow);
        await provider.GetRequiredService<UserStateStore>().ClearUserStatus(new User { Id = profile.TelegramUserId });
        await using (var db = databases.Users.CreateDbContext())
            await db.ColleagueTrialGrants.ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        await using (var db = databases.Credentials.CreateDbContext())
            await db.WalletOperations.ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var worker = new WalletOperationReconciliationService(databases.Credentials, databases.Users,
            provider.GetRequiredService<WalletLedgerService>(), NullLogger<WalletOperationReconciliationService>.Instance);
        await worker.ReconcileAsync();
        await worker.ReconcileAsync();
        await using var users = databases.Users.CreateDbContext();
        var rows = await users.WalletLedgerEntries.Where(x => x.ReferenceId == grant.Id).ToListAsync();
        var debit = Assert.Single(rows, x => x.Direction == WalletLedgerDirections.Debit);
        var refund = Assert.Single(rows, x => x.Direction == WalletLedgerDirections.Credit);
        Assert.Equal((3900L, WalletLedgerReasons.AccountPurchase, "colleague_trial", grant.BotId),
            (debit.AmountToman, debit.Reason, debit.ReferenceType, debit.BotId));
        Assert.Equal((3900L, WalletLedgerReasons.ColleagueTrialRefund, "colleague_trial", grant.BotId),
            (refund.AmountToman, refund.Reason, refund.ReferenceType, refund.BotId));
        Assert.Equal(20000, await credentials.GetAccountBalance(profile.TelegramUserId));
        Assert.NotNull((await users.ColleagueTrialGrants.SingleAsync()).PaidRefundRecordedAtUtc);
    }

    /// <summary>Writes a private enabled metered fixture with distinct colleague/public rates and ordinary ten-GiB minimums.</summary>
    /// <param name="databases">Required isolated fixture owning the temporary catalog.</param>
    /// <param name="normalRate">Normal colleague volume rate in positive whole toman per GiB; default 3,000.</param>
    /// <returns>The absolute fixture catalog path, never a production configuration or data path.</returns>
    /// <remarks>Three-day test pricing uses exact bytes while regular purchases still enforce the configured traffic minimum.</remarks>
    /// <example><code>var path = await WriteColleagueTrialCatalogAsync(databases, normalRate: 4000);</code></example>
    private static async Task<string> WriteColleagueTrialCatalogAsync(Databases databases, long normalRate = 3000)
    {
        var path = Path.Combine(databases.DirectoryPath, "trial-plans.json");
        var catalog = new XuiV3ServicePlanCatalog
        {
            Services = new List<XuiV3ServiceDefinition>
            {
                TrialService("normal", normalRate), TrialService("national", 2000)
            }
        };
        await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(catalog), new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>Creates one enabled metered test service for the isolated finite-price scenarios.</summary>
    /// <param name="key">Required global service key, normal or national.</param>
    /// <param name="rate">Positive colleague rate in Iranian toman per GiB.</param>
    /// <returns>A detached fixture service with a distinct public tariff and unchanged regular ten-GiB minimum.</returns>
    /// <remarks>The catalog contains only fake panel inbound ids and no credentials.</remarks>
    /// <example><code>var normal = TrialService("normal", 3000);</code></example>
    private static XuiV3ServiceDefinition TrialService(string key, long rate) => new()
    {
        Key = key, DisplayName = key, Kind = XuiV3ServiceKinds.Metered, IsEnabled = true,
        InboundIds = new List<int> { 1 }, MinimumTrafficGb = 10,
        PricePerGb = new XuiV3RolePrice { Colleague = rate, User = 9000 },
        PricePerDay = new XuiV3RolePrice { Colleague = 300, User = 1000 },
        DurationOptions = new List<XuiV3DurationOption> { new() { Key = "m1", Days = 30, DisplayName = "30 days", IsEnabled = true } }
    };

    /// <summary>Drives the real zero-allowance entry and service selection to an unfunded paid quote.</summary>
    /// <param name="provider">Required isolated production registrations; no host/receiver is started.</param>
    /// <param name="client">Required in-memory Telegram transport used only before panel provisioning.</param>
    /// <param name="serviceKey">Required normal/national service for the identical three-day paid test.</param>
    /// <returns>The detached active profile, pending bot/user session and exact persisted denied grant.</returns>
    /// <remarks>Sets the startup option before resolving the scoped flow, funds 20,000 fake toman and preserves all receipts for the scenario.</remarks>
    /// <example><code>var prepared = await PrepareColleaguePaidTrialAsync(provider, client, "normal");</code></example>
    private static async Task<(CredUser Profile, User Pending, ColleagueTrialGrant Grant)> PrepareColleaguePaidTrialAsync(
        ServiceProvider provider, StorefrontClient client, string serviceKey)
    {
        provider.GetRequiredService<AppConfig>().ColleagueDailyFreeTrialLimit = 0;
        provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["xuiApiVersionMode"] = "v3";
        const long actor = 817001;
        var credentials = provider.GetRequiredService<CredentialsStore>();
        await credentials.AddEmptyUser(actor);
        await credentials.PromotOrDemote(actor, true);
        await credentials.MutateWalletAsync(actor, 20000, "funding:trial-fixture");
        var profile = (await credentials.GetUserStatusWithId(actor))!;
        var state = provider.GetRequiredService<UserStateStore>();
        var flow = provider.GetRequiredService<XuiV3BotFlowService>();
        await flow.TryHandleTrialAsync(client, ColleagueTrialMessage(actor, 1, "🌟اکانت تست"), profile,
            await state.GetUserStatus(actor), null, default);
        await flow.TryHandleTrialAsync(client, ColleagueTrialMessage(actor, 2, $"[{serviceKey}]"), profile,
            await state.GetUserStatus(actor), null, default);
        var pending = await state.GetUserStatus(actor);
        var grant = (await provider.GetRequiredService<ColleagueTrialQuotaStore>().FindAsync(pending.PurchaseSessionId,
            actor, BotContextAccessor.CurrentBotId))!;
        return (profile, pending, grant);
    }

    /// <summary>Creates one original private-chat Telegram message for the authenticated fake colleague.</summary>
    /// <param name="actor">Positive fake Telegram user/chat id.</param>
    /// <param name="id">Positive message id used in the stable delivery request identity.</param>
    /// <param name="text">Required trial keyboard action or service selector.</param>
    /// <returns>A detached original message with matching actor/chat identity.</returns>
    /// <remarks>No bot token, account credentials or price is encoded in this input.</remarks>
    /// <example><code>var message = ColleagueTrialMessage(817001, 2, "[normal]");</code></example>
    private static Message ColleagueTrialMessage(long actor, int id, string text) => new()
    {
        Id = id, Text = text, From = new Telegram.Bot.Types.User { Id = actor }, Chat = new Chat { Id = actor }
    };

    /// <summary>Approves exactly the callback's displayed paid-test amount through the real owned callback handler.</summary>
    /// <param name="provider">Required isolated production registrations.</param>
    /// <param name="client">Required captured Telegram transport.</param>
    /// <param name="profile">Detached original fake sender snapshot; production reloads its persisted role.</param>
    /// <param name="state">Current bot/user state, possibly already reset or stale.</param>
    /// <param name="grant">Exact durable denied receipt addressed by the callback.</param>
    /// <param name="price">Positive originally displayed amount in whole toman, not a mutable re-read quote.</param>
    /// <returns>A task completing after approval, safe rejection or compensation.</returns>
    /// <remarks>The source message is in the actor's private chat; no account or provider secrets are encoded.</remarks>
    /// <example><code>await ConfirmColleagueTrialFixtureAsync(provider, client, profile, state, grant, 3900);</code></example>
    private static async Task ConfirmColleagueTrialFixtureAsync(ServiceProvider provider, StorefrontClient client,
        CredUser profile, User state, ColleagueTrialGrant grant, long price)
    {
        await provider.GetRequiredService<XuiV3BotFlowService>().TryHandleCallbackAsync(client, new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"), Data = $"x3:ct:{grant.Id}:{price}",
            From = new Telegram.Bot.Types.User { Id = profile.TelegramUserId },
            Message = new Message { Id = 40, Chat = new Chat { Id = profile.TelegramUserId, Type = Telegram.Bot.Types.Enums.ChatType.Private } }
        }, profile, state, null, default);
    }

    /// <summary>Reads only the signed financial effects of paid-test receipts from an isolated credentials database.</summary>
    /// <param name="databases">Required owning real-SQLite fixture.</param>
    /// <returns>Signed whole-toman receipt amounts; may be empty and contain no private account data.</returns>
    /// <remarks>Funding receipts are excluded so assertions prove the specific debit/refund boundary.</remarks>
    /// <example><code>var amounts = await ReadColleagueTrialReceiptsAsync(databases);</code></example>
    private static async Task<long[]> ReadColleagueTrialReceiptsAsync(Databases databases)
    {
        await using var db = databases.Credentials.CreateDbContext();
        return await db.WalletOperations.Where(x => x.OperationKey.StartsWith("colleague-paid-trial:"))
            .Select(x => x.AmountToman).ToArrayAsync();
    }

    /// <summary>Injects a Telegram outage only when the durable paid executor has already claimed the debit.</summary>
    private sealed class PaidTrialFailureClient : StorefrontClient
    {
        /// <summary>Optional real-database predicate selecting the financial boundary; never matches incidental message wording.</summary>
        public Func<Task<bool>>? FailWhen { get; set; }
        /// <inheritdoc />
        public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SendMessageRequest && FailWhen != null && await FailWhen())
                throw new HttpRequestException("Controlled Telegram outage after the paid executor claim.");
            return await base.SendRequest(request, cancellationToken);
        }
    }
}
