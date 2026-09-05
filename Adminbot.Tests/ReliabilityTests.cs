using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Telegram.Bot.Types;
using Xunit;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Extends the regression suite with recovery, migration, and real HTTP failure boundaries.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>An immediate website send and recovery of its same saved event issue only one successful order request.</summary>
    /// <returns>A task completing after both callers finish and the outbox is marked succeeded.</returns>
    /// <remarks>The real HTTP barrier also proves an unrelated users.db writer can commit during website I/O.</remarks>
    [Fact]
    public async Task Website_immediate_and_recovery_send_share_event_admission()
    {
        using var databases = new Databases(); var entered = Signal(); var release = Signal(); var posts = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.GetProperty("action").GetString() == "get_user")
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "test", ban = 0 } });
            else { Interlocked.Increment(ref posts); entered.TrySetResult(); await release.Task; await context.Response.WriteAsJsonAsync(new { success = true, id = 1 }); }
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteApiBaseUrl"] = app.Urls.Single(), ["gozargahSiteApiKey"] = "test-only" }).Build();
        var sync = new GozargahSiteSyncService(databases.Users, new CredentialsStore(databases.Credentials),
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance), configuration, NullLogger<GozargahSiteSyncService>.Instance);
        var row = new GozargahSiteSyncEvent { TelegramUserId = 123, Operation = "create", Email = "test", Uuid = "test-uuid",
            Status = "pending", RequestJson = "{\"name\":\"test\",\"uuid\":\"test-uuid\"}" };
        await using (var db = databases.Users.CreateDbContext()) { db.GozargahSiteSyncEvents.Add(row); await db.SaveChangesAsync(); }
        var first = sync.TrySendEventAsync(row);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = sync.TrySendEventAsync(new GozargahSiteSyncEvent { Id = row.Id });
        try { await databases.Inbox.TryAcceptAsync("a", Update(1, 123), 10, default); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.True(await first); Assert.True(await second); Assert.Equal(1, posts);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal("succeeded", (await verify.GozargahSiteSyncEvents.SingleAsync()).Status);
        await app.StopAsync();
    }

    /// <summary>A blocked callback inquiry cannot serialize an unrelated invoice's callback.</summary>
    /// <returns>A task completing after the unrelated callback responds while the first inquiry remains blocked.</returns>
    /// <remarks>Both callbacks use actual controller routing and HTTP inquiry; unsigned pending results never credit wallets.</remarks>
    [Fact]
    public async Task Unrelated_gateway_callbacks_progress_during_blocked_inquiry()
    {
        using var databases = new Databases(); var entered = Signal(); var release = Signal();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/one", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; }
            await context.Response.WriteAsJsonAsync(new { status = true, payment_status = "pending", amount = 100, pay_id = context.Request.Path.Value!.Split('/').Last() });
        });
        await app.StartAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["tetraminatorApiBaseUrl"] = app.Urls.Single(), ["tetraminatorApiKey"] = "test-only" }).Build();
        var gateway = new Tetraminator(config);
        await using (var db = databases.Users.CreateDbContext())
        { db.TetraminatorPaymentInfos.AddRange(new TetraminatorPaymentInfo { OrderId = "one", PayId = "one", AmountToman = 100 }, new TetraminatorPaymentInfo { OrderId = "two", PayId = "two", AmountToman = 100 }); await db.SaveChangesAsync(); }
        var firstController = new PaymentController(databases.Users, config, null!, null!, gateway, null!, null!, null!, NullLogger<PaymentController>.Instance);
        var secondController = new PaymentController(databases.Users, config, null!, null!, gateway, null!, null!, null!, NullLogger<PaymentController>.Instance);
        var first = firstController.ReceiveTetraminator("one", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await secondController.ReceiveTetraminator("two", default).WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(first.IsCompleted); }
        finally { release.TrySetResult(); }
        await first; await app.StopAsync();
        await using var verify = databases.Credentials.CreateDbContext(); Assert.Empty(await verify.WalletOperations.ToListAsync());
    }

    /// <summary>Detached workflow persistence cannot overwrite a row changed while its external request was waiting.</summary>
    /// <returns>A task completing after independent-writer progress, conflict rejection, and explicit reload are verified.</returns>
    /// <remarks>Only the saving transaction has tracked entities. A conflict never replays the simulated network request.</remarks>
    [Fact]
    public async Task Detached_workflow_reloads_write_targets_and_rejects_concurrent_changes()
    {
        using var databases = new Databases();
        var first = new UserWorkflowStore(databases.Users);
        var payment = new HooshPayPaymentInfo { TelegramUserId = 123, ChatId = 123, AmountToman = 100, OrderId = "detached", PaymentStatus = "pending" };
        first.Add(payment); Assert.Equal(1, await first.SaveAsync()); Assert.True(payment.Id > 0);
        var release = Signal(); var entered = Signal(); var calls = 0;
        var external = Task.Run(async () => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; });
        await entered.Task;
        try
        {
            var second = new UserWorkflowStore(databases.Users);
            var newer = await second.ReadAsync(db => db.HooshPayPaymentInfos.SingleAsync(x => x.Id == payment.Id));
            newer.PaymentStatus = "paid"; Assert.Equal(1, await second.SaveAsync());
            Assert.False(external.IsCompleted);
            payment.ErrorCode = "stale-attempt";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveAsync());
            await first.ReloadAsync(payment); Assert.Equal("paid", payment.PaymentStatus); Assert.Null(payment.ErrorCode);
            payment.ErrorCode = "reviewed"; Assert.Equal(1, await first.SaveAsync()); Assert.Equal(0, await first.SaveAsync());
        }
        finally { release.TrySetResult(); await external; }
        Assert.Equal(1, calls);
        await using var verify = databases.Users.CreateDbContext();
        var saved = await verify.HooshPayPaymentInfos.SingleAsync(); Assert.Equal("paid", saved.PaymentStatus); Assert.Equal("reviewed", saved.ErrorCode);
    }

    /// <summary>A conflicted workflow batch must not commit a new ledger row before its related order update fails.</summary>
    /// <returns>A task completing after a real SQLite rollback leaves both the ledger and stored payment unchanged.</returns>
    [Fact]
    public async Task Detached_workflow_batch_is_atomic_and_never_attaches_the_input_snapshot()
    {
        using var databases = new Databases(); var store = new UserWorkflowStore(databases.Users);
        var payment = new HooshPayPaymentInfo { TelegramUserId = 123, AmountToman = 100, OrderId = "batch", PaymentStatus = "pending" };
        store.Add(payment); await store.SaveAsync();
        await using (var writer = databases.Users.CreateDbContext())
            await writer.HooshPayPaymentInfos.ExecuteUpdateAsync(set => set.SetProperty(x => x.PaymentStatus, "paid"));
        payment.PaymentStatus = "failed";
        store.Add(new WalletLedgerEntry { TelegramUserId = 123, AmountToman = 100, IdempotencyKey = "batch-test", Direction = "credit", Reason = "test" });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.SaveAsync());
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
        Assert.Equal("paid", (await verify.HooshPayPaymentInfos.SingleAsync()).PaymentStatus);
    }

    /// <summary>Reliable Telegram actors own user lanes; anonymous senders and aggregate updates use the fallback lane.</summary>
    /// <remarks>Callback authors override the bot author of the callback's source message.</remarks>
    [Fact]
    public void Actor_resolution_covers_queries_membership_and_anonymous_fallbacks()
    {
        var actor = new Telegram.Bot.Types.User { Id = 123 };
        var updates = new Update[]
        {
            new() { Message = new() { From = actor } }, new() { EditedMessage = new() { From = actor } },
            new() { CallbackQuery = new() { From = actor, Message = new() { From = new() { Id = 999, IsBot = true } } } },
            new() { InlineQuery = new() { From = actor } }, new() { ChosenInlineResult = new() { From = actor } },
            new() { ShippingQuery = new() { From = actor } }, new() { PreCheckoutQuery = new() { From = actor } },
            new() { MyChatMember = new() { From = actor } }, new() { ChatMember = new() { From = actor } },
            new() { ChatJoinRequest = new() { From = actor } }, new() { PollAnswer = new() { User = actor } }
        };
        Assert.All(updates, update => Assert.Equal(123, TelegramUpdateIdentity.ResolveUserId(update)));
        Assert.Equal(0, TelegramUpdateIdentity.ResolveUserId(new() { Message = new() { From = actor, SenderChat = new() { Id = -123 } } }));
        Assert.Equal(0, TelegramUpdateIdentity.ResolveUserId(new() { ChannelPost = new() { From = actor } }));
        Assert.Equal(0, TelegramUpdateIdentity.ResolveUserId(new()));
    }

    /// <summary>Cancelled same-invoice waiters release their references without blocking other invoices or leaking idle keys.</summary>
    /// <returns>A task completing after cancellation, immediate admission, and double-disposal invariants are verified.</returns>
    /// <remarks>The nonwaiting admission probe returns null while a key is held; no handler slot waits inside that probe.</remarks>
    [Fact]
    public async Task Keyed_gate_cleans_cancelled_waiters_and_preserves_independent_admission()
    {
        var gate = new AsyncKeyedGate(); var held = await gate.EnterAsync("invoice:1");
        using var cancelled = new CancellationTokenSource();
        var waiting = gate.EnterAsync("invoice:1", cancelled.Token);
        Assert.Null(gate.TryEnter("invoice:1"));
        using (var other = gate.TryEnter("invoice:2")) { Assert.NotNull(other); Assert.Equal(2, gate.ActiveResourceCount); }
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, gate.ActiveResourceCount);
        held.Dispose(); held.Dispose(); Assert.Equal(0, gate.ActiveResourceCount);
        using (var next = gate.TryEnter("invoice:1")) Assert.NotNull(next);
        Assert.Equal(0, gate.ActiveResourceCount);
    }

    /// <summary>A committed credit referencing a missing payment cannot be declared fully reconciled.</summary>
    /// <returns>A task completing after repeated recovery preserves the pending receipt and one ledger row.</returns>
    /// <remarks>This protects the two-database boundary when a backup mismatch or damaged payment record requires review.</remarks>
    [Fact]
    public async Task Missing_payment_target_retains_reconciliation_backlog_without_duplicate_credit()
    {
        using var databases = new Databases(); var wallets = new CredentialsStore(databases.Credentials);
        await wallets.AddEmptyUser(123); await wallets.MutateWalletAsync(123, 100, "payment:hooshpay:999:credit");
        await using (var context = databases.Credentials.CreateDbContext())
            await context.WalletOperations.ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var worker = new WalletOperationReconciliationService(databases.Credentials, databases.Users,
            new WalletLedgerService(databases.Users, wallets), NullLogger<WalletOperationReconciliationService>.Instance);
        Assert.Equal(0, await worker.ReconcileAsync()); Assert.Equal(0, await worker.ReconcileAsync());
        Assert.Null((await wallets.GetWalletOperationAsync("payment:hooshpay:999:credit")).ReconciledAtUtc);
        Assert.Equal(100, await wallets.GetAccountBalance(123));
        await using var users = databases.Users.CreateDbContext(); Assert.Single(await users.WalletLedgerEntries.ToListAsync());
    }

    /// <summary>Sixteen blocked users consume exactly sixteen slots while additional accepted users remain queued.</summary>
    /// <returns>A task completing after all twenty updates drain without exceeding the configured limit.</returns>
    /// <remarks>The barrier proves the limit while every admitted handler remains active.</remarks>
    [Fact]
    public async Task Sixteen_workers_are_bounded_even_when_every_external_call_is_blocked()
    {
        using var databases = new Databases(); var full = Signal(); var release = Signal();
        var entered = 0;
        using var scheduler = Create(databases, new Executor(async (_, token) =>
        { if (Interlocked.Increment(ref entered) == 16) full.TrySetResult(); await release.Task.WaitAsync(token); }), concurrency: 16);
        for (var i = 1; i <= 20; i++) await scheduler.EnqueueAsync("a", Update(i, i), default);
        await scheduler.StartAsync(default);
        try
        {
            await full.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(16, Volatile.Read(ref entered)); Assert.Equal(16, scheduler.ActiveHandlerCount);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        Assert.Equal(20, entered);
    }

    /// <summary>A handled ambiguous creation still quarantines its update; operator review releases later work without replay.</summary>
    /// <returns>A task completing after linked-effect lookup and explicit lane resolution are verified.</returns>
    /// <remarks>The business key is independent of the inbox sequence, which exists only as an audit reference.</remarks>
    [Fact]
    public async Task Linked_ambiguous_creation_requires_review_and_cannot_be_replayed()
    {
        using var databases = new Databases(); var creations = new XuiV3CreationOperationStore(databases.Users);
        using var scheduler = Create(databases, new Executor(async (_, token) =>
        {
            await creations.ReserveAsync(new XuiV3CreationOperation
            { OperationKey = "purchase:review:1", TelegramUserId = 123, PanelKey = "test-panel", ClientJson = "private", InboundIdsJson = "[1]" }, token);
        }));
        await scheduler.EnqueueAsync("a", Update(1, 123), default);
        await scheduler.EnqueueAsync("a", Update(2, 123), default);
        await scheduler.StartAsync(default); await scheduler.StopAsync(default);
        await using var context = databases.Users.CreateDbContext();
        var row = await context.TelegramUpdateInbox.AsNoTracking().SingleAsync(x => x.UpdateId == 1);
        Assert.Equal("uncertain", row.Status);
        Assert.Equal(row.Sequence, (await context.XuiV3CreationOperations.SingleAsync()).InboxSequence);
        Assert.Empty(await databases.Inbox.ReadReadyAsync(10, default));
        Assert.True(await databases.Inbox.ResolveReviewedAsync(row.Sequence, 456, "review-001", default));
        Assert.False(await databases.Inbox.ResolveReviewedAsync(row.Sequence, 456, "review-001", default));
        Assert.Equal(2, (await databases.Inbox.ReadReadyAsync(10, default)).Single().UpdateId);
        Assert.Null(TelegramUpdateExecutionScope.CurrentSequence);
    }

    /// <summary>A handler ignoring cancellation cannot keep a host deadline open or overwrite its uncertain status afterward.</summary>
    /// <returns>A task completing after the noncooperative handler is released and its tracked task is joined.</returns>
    /// <remarks>A cancelled host deadline shortens the final grace period; accepted payloads remain durable.</remarks>
    [Fact]
    public async Task Host_deadline_quarantines_noncooperative_work_before_returning()
    {
        using var databases = new Databases(); var entered = Signal(); var release = Signal();
        using var scheduler = Create(databases, new Executor(async (_, _) => { entered.TrySetResult(); await release.Task; }));
        await scheduler.StartAsync(default); await scheduler.EnqueueAsync("a", Update(1, 1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await scheduler.StopAsync(new CancellationToken(true)).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        await using var context = databases.Users.CreateDbContext();
        Assert.Equal("uncertain", (await context.TelegramUpdateInbox.SingleAsync()).Status);
    }

    /// <summary>Runtime copies keep nested protocol templates private to a customer's execution.</summary>
    /// <remarks>Mutating an account-specific VMess name previously modified the configured template shared by other users.</remarks>
    [Fact]
    public void Runtime_snapshots_do_not_share_mutable_protocol_templates()
    {
        var original = Guid.NewGuid();
        var configured = new ServerInfo { VmessTemplate = new() { Ps = "base", Id = original } };
        var first = RuntimeSnapshot.Copy(configured); var second = RuntimeSnapshot.Copy(configured);
        first.VmessTemplate.Ps += "-customer"; first.VmessTemplate.Id = Guid.NewGuid();
        Assert.Equal("base", second.VmessTemplate.Ps); Assert.Equal(original, configured.VmessTemplate.Id);
    }

    /// <summary>An actual provisional settlement crash preserves its credentials receipt and repairs users.db without a second credit.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Payment_metadata_commit_failure_preserves_provisional_authority_and_exactly_one_credit"</code></example>
    [Fact]
    public async Task Payment_metadata_commit_failure_preserves_provisional_authority_and_exactly_one_credit()
    {
        using var databases = new Databases();
        var wallets = new CredentialsStore(databases.Credentials);
        await wallets.AddEmptyUser(123);
        var payment = new HooshPayPaymentInfo { TelegramUserId = 123, ChatId = 123, AmountToman = 100,
            PaymentStatus = "pending", OrderId = "test-order", InvoiceUid = "test-invoice", BotId = "a" };
        await using (var context = databases.Users.CreateDbContext())
        { context.HooshPayPaymentInfos.Add(payment); await context.SaveChangesAsync(); }
        var ledger = new WalletLedgerService(databases.Users, wallets);
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(SqliteOperation.ConnectionString(Path.Combine(databases.DirectoryPath, "users.db")))
            .AddInterceptors(new RejectWalletMetadataCommit()).Options;
        await using (var failing = new UserDbContext(options))
        {
            // Notification/client/referral dependencies are unreachable before this injected database failure.
            var settlement = new HooshPaySettlementService(new UserDbContextFactory(options), wallets, null!, null!, new BotContextAccessor(), ledger, null!, NullLogger<HooshPaySettlementService>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => settlement.ApplyProvisionalWalletPaymentAsync(payment, 456));
        }
        Assert.Equal(100, await wallets.GetAccountBalance(123));
        await using (var credentials = databases.Credentials.CreateDbContext())
            await credentials.WalletOperations.ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var recovery = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger, NullLogger<WalletOperationReconciliationService>.Instance);
        Assert.Equal(1, await recovery.ReconcileAsync());
        await using var verified = databases.Users.CreateDbContext();
        var row = await verified.HooshPayPaymentInfos.AsNoTracking().SingleAsync();
        Assert.True(row.IsAddedToBalance); Assert.True(row.IsProvisionallyApproved);
        Assert.Equal(456, row.ProvisionalApprovedByTelegramUserId);
        Assert.Equal("pending", row.PaymentStatus);
        Assert.Single(await verified.PaymentSettlementNotifications.ToListAsync());
        var retry = new HooshPaySettlementService(databases.Users, wallets, null!, null!, new BotContextAccessor(), ledger, null!, NullLogger<HooshPaySettlementService>.Instance);
        Assert.Equal(NowPaymentsSettlementStatus.AlreadyAdded, (await retry.ApplyProvisionalWalletPaymentAsync(payment, 456)).Status);
        Assert.Equal(100, await wallets.GetAccountBalance(123));
        Assert.Single(await verified.WalletLedgerEntries.ToListAsync());
    }

    /// <summary>Distinct business events from different bots serialize on one global wallet without lost balance increments.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Cross_bot_wallet_races_and_legacy_overdraft_rules_are_preserved"</code></example>
    [Fact]
    public async Task Cross_bot_wallet_races_and_legacy_overdraft_rules_are_preserved()
    {
        using var databases = new Databases(); var wallets = new CredentialsStore(databases.Credentials);
        await wallets.AddEmptyUser(123);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            wallets.MutateWalletAsync(123, i % 2 == 0 ? 10 : -5, $"cross-bot:{i}", botId: i % 2 == 0 ? "a" : "b"))));
        Assert.Equal(50, await wallets.GetAccountBalance(123));
        var profile = await wallets.GetUserStatusWithId(123);
        Assert.False(await wallets.Pay(profile, 100, "overdraft:1"));
        Assert.Equal(-50, await wallets.GetAccountBalance(123));
        Assert.False(await wallets.Pay(profile, 100, "overdraft:1"));
        Assert.Equal(-50, await wallets.GetAccountBalance(123));
    }

    /// <summary>Separate durable renewals for one client reference produce separate ledger entries.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Distinct_financial_keys_cannot_collapse_into_one_legacy_reference_row"</code></example>
    [Fact]
    public async Task Distinct_financial_keys_cannot_collapse_into_one_legacy_reference_row()
    {
        using var databases = new Databases(); var wallets = new CredentialsStore(databases.Credentials);
        await wallets.AddEmptyUser(123); var ledger = new WalletLedgerService(databases.Users, wallets);
        for (var i = 1; i <= 2; i++)
        {
            await wallets.MutateWalletAsync(123, -10, $"renew:{i}");
            await ledger.RecordAsync(123, WalletLedgerDirections.Debit, 10, 999, 999, WalletLedgerReasons.AccountRenew,
                provider: "wallet", referenceType: "xui-v3-client", referenceId: "same-client", idempotencyKey: $"renew:{i}");
        }
        await using var context = databases.Users.CreateDbContext();
        Assert.Equal(2, await context.WalletLedgerEntries.CountAsync());
        Assert.Equal(-20, (await ledger.GetByKeyAsync("renew:2"))!.BalanceAfter);
    }

    /// <summary>Fails the users.db save immediately after the actual settlement has committed credentials.db.</summary>
    private sealed class RejectWalletMetadataCommit : SaveChangesInterceptor
    {
        /// <summary>Injects a non-retryable crash between the two database commits.</summary>
        /// <param name="eventData">The users.db save containing a payment balance marker.</param>
        /// <param name="result">Original interception result.</param>
        /// <param name="cancellationToken">Test operation cancellation.</param>
        /// <returns>The unchanged interception result for unrelated writes.</returns>
        /// <exception cref="InvalidOperationException">The payment save reaches the injected failure boundary.</exception>
        /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<HooshPayPaymentInfo>().Any(x => x.Entity.IsAddedToBalance))
                throw new InvalidOperationException("Injected users.db settlement commit failure");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Anonymous updates share a separate FIFO lane and do not block identified customers.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Fallback_lane_is_fifo_and_separate_from_user_lanes"</code></example>
    [Fact]
    public async Task Fallback_lane_is_fifo_and_separate_from_user_lanes()
    {
        using var databases = new Databases();
        var entered = Signal(); var release = Signal(); var independent = Signal();
        var seen = new ConcurrentQueue<int>();
        using var scheduler = Create(databases, new Executor(async (item, token) =>
        {
            if (item.Update.Id == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            if (item.Key.TelegramUserId == 0) seen.Enqueue(item.Update.Id);
            else independent.TrySetResult();
        }));
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("a", new Update { Id = 1 }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await scheduler.EnqueueAsync("a", new Update { Id = 2 }, default);
            await scheduler.EnqueueAsync("a", Update(3, 123), default);
            await independent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(seen);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.Equal(0, scheduler.ActiveHandlerCount);
    }

    /// <summary>A failed lane is quarantined while unrelated lane heads continue on the same worker pool.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Exception_isolation_blocks_only_the_uncertain_lane"</code></example>
    [Fact]
    public async Task Exception_isolation_blocks_only_the_uncertain_lane()
    {
        using var databases = new Databases();
        var seen = new ConcurrentQueue<int>();
        using var scheduler = Create(databases, new Executor((item, _) =>
        {
            if (item.Update.Id == 1) throw new InvalidOperationException("expected failure");
            seen.Enqueue(item.Update.Id); return Task.CompletedTask;
        }));
        await scheduler.EnqueueAsync("a", Update(1, 1), default);
        await scheduler.EnqueueAsync("a", Update(2, 1), default);
        await scheduler.EnqueueAsync("a", Update(3, 2), default);
        await scheduler.StartAsync(default); await scheduler.StopAsync(default);
        Assert.Equal(new[] { 3 }, seen);
        Assert.Equal(2, await databases.Inbox.CountPendingAsync(default));
    }

    /// <summary>One heavily queued customer cannot consume every turn ahead of other ready bots and users.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Ready_bots_and_users_receive_round_robin_turns"</code></example>
    [Fact]
    public async Task Ready_bots_and_users_receive_round_robin_turns()
    {
        using var databases = new Databases();
        var seen = new ConcurrentQueue<int>();
        using var scheduler = Create(databases, new Executor((item, _) => { seen.Enqueue(item.Update.Id); return Task.CompletedTask; }), concurrency: 1);
        for (var i = 1; i <= 5; i++) await scheduler.EnqueueAsync("a", Update(i, 1), default);
        await scheduler.EnqueueAsync("a", Update(6, 2), default);
        await scheduler.EnqueueAsync("b", Update(7, 1), default);
        await scheduler.StartAsync(default); await scheduler.StopAsync(default);
        Assert.Equal(new[] { 1, 7, 6 }, seen.Take(3));
    }

    /// <summary>Completed payloads disappear immediately, deduplication expires after seven days, and uncertain rows remain.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Retention_erases_completed_payloads_and_never_removes_uncertain_work"</code></example>
    [Fact]
    public async Task Retention_erases_completed_payloads_and_never_removes_uncertain_work()
    {
        using var databases = new Databases();
        await databases.Inbox.TryAcceptAsync("a", Update(1, 1), 10, default);
        await databases.Inbox.TryAcceptAsync("a", Update(2, 2), 10, default);
        var heads = await databases.Inbox.ReadReadyAsync(10, default);
        await databases.Inbox.ClaimAsync(heads[0].Sequence, default);
        await databases.Inbox.ClaimAsync(heads[1].Sequence, default);
        await databases.Inbox.FinishAsync(heads[0].Sequence, null!, default);
        await databases.Inbox.FinishAsync(heads[1].Sequence, "expected_test_failure", default);
        await using var context = databases.Users.CreateDbContext();
        Assert.Null((await context.TelegramUpdateInbox.AsNoTracking().SingleAsync(x => x.UpdateId == 1)).Payload);
        await context.TelegramUpdateInbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow.AddDays(-8)));
        Assert.Equal(1, await databases.Inbox.PruneAsync(default));
        Assert.Equal("uncertain", (await context.TelegramUpdateInbox.SingleAsync()).Status);
    }

    /// <summary>Cancelled and faulted receivers allow replacement only after termination; startup cancellation still wins.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Receiver_replacement_waits_for_prior_generation_and_observes_failures"</code></example>
    [Fact]
    public async Task Receiver_replacement_waits_for_prior_generation_and_observes_failures()
    {
        var prior = Signal();
        var replacement = TelegramReceiverLifetime.ObservePreviousAsync(prior.Task, default);
        Assert.False(replacement.IsCompleted);
        prior.TrySetCanceled(); await replacement;
        await TelegramReceiverLifetime.ObservePreviousAsync(Task.FromException(new InvalidOperationException("test")), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TelegramReceiverLifetime.ObservePreviousAsync(Task.CompletedTask, new CancellationToken(true)));
    }

    /// <summary>Two bot states for one Telegram sender remain independent and detached through partial updates and clears.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Conversation_stores_preserve_bot_isolation_and_partial_state"</code></example>
    [Fact]
    public async Task Conversation_stores_preserve_bot_isolation_and_partial_state()
    {
        using var databases = new Databases();
        var state = new UserStateStore(databases.Users); var accessor = new BotContextAccessor();
        foreach (var bot in new[] { "a", "b" })
            using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = bot } }))
                await state.SaveUserStatus(new global::User { Id = 123, LastStep = bot, PurchaseSessionId = bot });
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "a" } }))
        {
            await state.SaveUserStatus(new global::User { Id = 123, SelectedCountry = "test" });
            Assert.Equal("a", (await state.GetUserStatus(123)).PurchaseSessionId);
            await state.ClearUserStatus(new global::User { Id = 123 });
            Assert.True(string.IsNullOrEmpty((await state.GetUserStatus(123)).PurchaseSessionId));
        }
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "b" } }))
            Assert.Equal("b", (await state.GetUserStatus(123)).LastStep);
    }

    /// <summary>A real SQLite writer lock exhausts exactly three fresh contexts; constraint errors receive no retry.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Sqlite_retries_are_bounded_and_exclude_non_contention_errors"</code></example>
    [Fact]
    public async Task Sqlite_retries_are_bounded_and_exclude_non_contention_errors()
    {
        using var databases = new Databases();
        await using var blocker = databases.Users.CreateDbContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("UPDATE Users SET LastStep = LastStep");
        var attempts = new List<Guid>();
        var options = new DbContextOptionsBuilder<UserDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(databases.DirectoryPath, "users.db"), DefaultTimeout = 1 }.ToString()).Options;
        var failure = await Assert.ThrowsAsync<SqliteException>(() => SqliteOperation.RunAsync(async token =>
        {
            await using var context = new UserDbContext(options); attempts.Add(context.ContextId.InstanceId);
            return await context.Database.ExecuteSqlRawAsync("UPDATE Users SET LastStep = LastStep", token);
        }));
        Assert.True(SqliteOperation.IsBusy(failure));
        Assert.Equal(3, attempts.Distinct().Count());
        var nonRetryAttempts = 0;
        await Assert.ThrowsAsync<SqliteException>(() => SqliteOperation.RunAsync<int>(_ =>
        { nonRetryAttempts++; throw new SqliteException("synthetic constraint error", 19); }));
        Assert.Equal(1, nonRetryAttempts);
    }

    /// <summary>The proposed panel policy admits four slots immediately, isolates panels, and removes all idle state.</summary>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Deferred_panel_admission_is_non_waiting_bounded_and_cleans_idle_keys"</code></example>
    [Fact]
    public void Deferred_panel_admission_is_non_waiting_bounded_and_cleans_idle_keys()
    {
        var policy = new XuiPanelAdmission();
        var leases = Enumerable.Range(0, 4).Select(_ => policy.TryAcquire("panel-a")).ToArray();
        Assert.All(leases, Assert.NotNull);
        Assert.Null(policy.TryAcquire("panel-a"));
        using (var other = policy.TryAcquire("panel-b")) Assert.NotNull(other);
        foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        Assert.Equal(0, policy.ActivePanelCount);
    }

    /// <summary>Existing balances survive additive migrations; migration replay creates no financial receipts.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Additive_migrations_preserve_historical_wallet_and_upgrade_both_databases"</code></example>
    [Fact]
    public async Task Additive_migrations_preserve_historical_wallet_and_upgrade_both_databases()
    {
        using var databases = new Databases(initialize: false);
        await using var credentials = databases.Credentials.CreateDbContext();
        await credentials.GetService<IMigrator>().MigrateAsync("20260611010000_AddBlockedUsers");
        credentials.Users.Add(new CredUser { TelegramUserId = 123, AccountBalance = 777 });
        await credentials.SaveChangesAsync();
        await credentials.Database.MigrateAsync(); await credentials.Database.MigrateAsync();
        Assert.Equal(777, (await credentials.Users.AsNoTracking().SingleAsync()).AccountBalance);
        Assert.Empty(await credentials.WalletOperations.ToListAsync());
        await using var users = databases.Users.CreateDbContext();
        await users.GetService<IMigrator>().MigrateAsync("20260901234439_AddTenantRenewalServiceResolutionMode");
        await users.Database.MigrateAsync();
        Assert.Empty(await users.TelegramUpdateInbox.ToListAsync());
        Assert.Empty(await users.XuiV3CreationOperations.ToListAsync());
    }

    /// <summary>A blocked real HTTP provisioning attempt holds no database write lock and can never authorize another POST after interruption.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Ambiguous_creation_uses_readback_without_a_second_network_mutation"</code></example>
    [Fact]
    public async Task Ambiguous_creation_uses_readback_without_a_second_network_mutation()
    {
        using var databases = new Databases();
        var entered = Signal(); var release = Signal(); var posts = 0; var reads = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path == "/panel/api/clients/add")
            { Interlocked.Increment(ref posts); entered.TrySetResult(); await release.Task; context.Abort(); }
            else { Interlocked.Increment(ref reads); await context.Response.WriteAsJsonAsync(new { success = false, msg = "not yet found" }); }
        });
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["xuiV3TransientRetryCount"] = "0", ["xuiV3RequestTimeoutSeconds"] = "5" }).Build();
        var request = new AccountDto { TelegramUserId = 123, TotoalGB = "10", ServerInfo = new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" } };
        var options = new XuiV3CreateAccountOptions { InboundIds = new[] { 1 }, DurationDays = 30, TrafficGb = 10, SaveUserStatus = false,
            OperationKey = "purchase:http:1", OperationStore = new(databases.Users) };
        var first = ApiServicev3.CreateUserAccountAsync(request, configuration, options, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await databases.Inbox.TryAcceptAsync("a", Update(1, 123), 10, deadline.Token);
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.False((await first).Success);
        options.OperationStore = new(databases.Users);
        Assert.False((await ApiServicev3.CreateUserAccountAsync(request, configuration, options, deadline.Token)).Success);
        Assert.Equal(1, posts); Assert.True(reads >= 2);
        options.TrafficGb = 20;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiServicev3.CreateUserAccountAsync(request, configuration, options, deadline.Token));
        await app.StopAsync();
    }
}
