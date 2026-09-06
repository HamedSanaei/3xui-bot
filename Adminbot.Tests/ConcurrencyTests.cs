using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Xunit;
using Xunit.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Exercises durable scheduling and wallet atomicity against the actual SQLite provider.</summary>
public sealed partial class ConcurrencyTests
{
    private readonly ITestOutputHelper _output;
    /// <summary>Captures only non-secret workload metrics.</summary>
    /// <param name="output">xUnit result output.</param>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    public ConcurrencyTests(ITestOutputHelper output) => _output = output;

    /// <summary>The actual production registrations must reject captive contexts and provide isolated execution graphs.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Production_service_graph_has_no_singleton_capturing_a_scoped_context"</code></example>
    [Fact]
    public async Task Production_service_graph_has_no_singleton_capturing_a_scoped_context()
    {
        using var databases = new Databases();
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["bots:0:id"] = "test-bot", ["bots:0:username"] = "test_bot", ["bots:0:enabled"] = "false",
            ["bots:0:token"] = "12345:" + new string('a', 35)
        }).Build();
        var settings = config.Get<AppConfig>()!;
        settings.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db");
        settings.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection();
        Program.RegisterApplicationServices(services, config, settings, databases.DirectoryPath);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var first = provider.CreateAsyncScope(); await using var second = provider.CreateAsyncScope();
        Assert.NotSame(first.ServiceProvider.GetRequiredService<UserDbContext>(), second.ServiceProvider.GetRequiredService<UserDbContext>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<UserWorkflowStore>(), second.ServiceProvider.GetRequiredService<UserWorkflowStore>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<TelegramBotService>(), second.ServiceProvider.GetRequiredService<TelegramBotService>());
        Assert.IsType<CredentialsStore>(provider.GetRequiredService<CredentialsStore>());
        var executor = provider.GetRequiredService<ITelegramUpdateExecutor>();
        Assert.False(executor.IsAvailable("test-bot"));
        provider.GetRequiredService<BotRegistry>().Upsert(new BotInstance
        { Id = "active", Enabled = true, Token = "12345:" + new string('a', 35) });
        Assert.True(executor.IsAvailable("active"));
        Assert.False(executor.IsAvailable("absent-bot"));
    }

    /// <summary>Only one concurrent creation claimant may POST; all later claimants retain the first immutable identity.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Duplicate_purchase_reservation_authorizes_exactly_one_provisioning_call"</code></example>
    [Fact]
    public async Task Duplicate_purchase_reservation_authorizes_exactly_one_provisioning_call()
    {
        using var databases = new Databases();
        var store = new XuiV3CreationOperationStore(databases.Users);
        var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => store.ReserveAsync(new XuiV3CreationOperation
        {
            OperationKey = "purchase:example:1", TelegramUserId = 123, PanelKey = "panel-hash",
            ClientJson = "private-test-identity-" + i, InboundIdsJson = "[1]", CreatedAtUtc = DateTime.UtcNow
        }, default))));
        Assert.All(claims, x => Assert.True(x.MayCreate));
        var grants = await Task.WhenAll(claims.Select(x => store.TryStartPostAsync(x.Operation.OperationKey, default)));
        Assert.Single(grants, x => x);
        Assert.Single(claims.Select(x => x.Operation.ClientJson).Distinct());
        var restarted = new XuiV3CreationOperationStore(databases.Users);
        var recovered = await restarted.ReserveAsync(new XuiV3CreationOperation
        {
            OperationKey = "purchase:example:1", TelegramUserId = 123, PanelKey = "panel-hash", ClientJson = "replacement-must-not-win", InboundIdsJson = "[1]"
        }, default);
        Assert.False(recovered.MayCreate);
        Assert.Equal(claims[0].Operation.ClientJson, recovered.Operation.ClientJson);
    }

    /// <summary>A crash between wallet and ledger commits is recoverable without another credit or losing its approval mode.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Wallet_reconciliation_is_idempotent_after_the_first_database_commit"</code></example>
    [Fact]
    public async Task Wallet_reconciliation_is_idempotent_after_the_first_database_commit()
    {
        using var databases = new Databases();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(123);
        await credentials.MutateWalletAsync(123, 100, "admin:example:1:1:credit", approvalKind: "provisional", approvedByTelegramUserId: 456);
        await using (var db = databases.Credentials.CreateDbContext())
            await db.WalletOperations.ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var ledger = new WalletLedgerService(databases.Users, credentials);
        var worker = new WalletOperationReconciliationService(databases.Credentials, databases.Users, ledger, NullLogger<WalletOperationReconciliationService>.Instance);
        Assert.Equal(1, await worker.ReconcileAsync());
        Assert.Equal(0, await worker.ReconcileAsync());
        Assert.Equal(100, await credentials.GetAccountBalance(123));
        await using var users = databases.Users.CreateDbContext();
        Assert.Single(await users.WalletLedgerEntries.ToListAsync());
        var receipt = await credentials.GetWalletOperationAsync("admin:example:1:1:credit");
        Assert.Equal("provisional", receipt.ApprovalKind);
        Assert.Equal(456, receipt.ApprovedByTelegramUserId);
        Assert.NotNull(receipt.ReconciledAtUtc);
    }

    /// <summary>A controllably slow external request cannot block another user or another bot.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Slow_user_does_not_block_other_users_or_same_user_in_another_bot"</code></example>
    [Fact]
    public async Task Slow_user_does_not_block_other_users_or_same_user_in_another_bot()
    {
        using var databases = new Databases();
        var entered = Signal(); var release = Signal(); var others = new ConcurrentBag<string>();
        var executor = new Executor(async (item, token) =>
        {
            if (item.Key == new TelegramUpdateExecutionKey("a", 123))
            { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            else others.Add(item.Key.BotId + ":" + item.Key.TelegramUserId);
        });
        using var scheduler = Create(databases, executor);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("a", Update(1, 123), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await scheduler.EnqueueAsync("a", Update(2, 456), default);
            await scheduler.EnqueueAsync("b", Update(1, 123), default);
            await Until(() => others.Count == 2);
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
    }

    /// <summary>Repeated bursts never reorder or overlap one user's lane while global concurrency remains bounded.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Repeated_same_user_bursts_are_fifo_and_global_concurrency_is_bounded"</code></example>
    [Fact]
    public async Task Repeated_same_user_bursts_are_fifo_and_global_concurrency_is_bounded()
    {
        using var databases = new Databases();
        var orders = new ConcurrentDictionary<long, ConcurrentQueue<int>>();
        var current = 0; var maximum = 0;
        var executor = new Executor(async (item, token) =>
        {
            var count = Interlocked.Increment(ref current);
            InterlockedMax(ref maximum, count);
            try
            {
                orders.GetOrAdd(item.Key.TelegramUserId, _ => new()).Enqueue(item.Update.Id);
                await Task.Delay(30, token);
            }
            finally { Interlocked.Decrement(ref current); }
        });
        using var scheduler = Create(databases, executor, concurrency: 4);
        await scheduler.StartAsync(default);
        for (var round = 0; round < 3; round++)
            for (var user = 1; user <= 12; user++)
                await scheduler.EnqueueAsync("a", Update(round * 100 + user, user), default);
        await scheduler.StopAsync(default);
        Assert.InRange(maximum, 2, 4);
        foreach (var user in Enumerable.Range(1, 12))
            Assert.Equal(new[] { user, 100 + user, 200 + user }, orders[user]);
    }

    /// <summary>Full admission blocks deterministically, accepts duplicates without capacity, and releases after commit.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Capacity_applies_backpressure_without_dropping_or_duplicating_updates"</code></example>
    [Fact]
    public async Task Capacity_applies_backpressure_without_dropping_or_duplicating_updates()
    {
        using var databases = new Databases();
        var entered = Signal(); var release = Signal(); var processed = 0;
        var executor = new Executor(async (_, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); Interlocked.Increment(ref processed); });
        using var scheduler = Create(databases, executor, concurrency: 1, capacity: 1);
        await scheduler.StartAsync(default);
        await scheduler.EnqueueAsync("a", Update(1, 1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.EnqueueAsync("a", Update(1, 1), default);
        var waiting = scheduler.EnqueueAsync("a", Update(2, 2), default);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, await databases.Inbox.CountPendingAsync(default));
        release.TrySetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.StopAsync(default);
        Assert.Equal(2, processed);
    }

    /// <summary>An interrupted claim becomes terminal without replay and later same-user work resumes across restart.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Restart_recovers_queued_work_but_never_replays_uncertain_execution"</code></example>
    [Fact]
    public async Task Restart_recovers_queued_work_but_never_replays_uncertain_execution()
    {
        using var databases = new Databases();
        await databases.Inbox.TryAcceptAsync("a", Update(1, 1), 10, default);
        await databases.Inbox.TryAcceptAsync("a", Update(2, 1), 10, default);
        await databases.Inbox.TryAcceptAsync("a", Update(3, 2), 10, default);
        var head = (await databases.Inbox.ReadReadyAsync(10, default)).First();
        Assert.NotNull(await databases.Inbox.ClaimAsync(head.Sequence, default));
        var seen = new ConcurrentBag<int>();
        using var scheduler = Create(databases, new Executor((item, _) => { seen.Add(item.Update.Id); return Task.CompletedTask; }));
        await scheduler.StartAsync(default);
        await scheduler.StopAsync(default);
        Assert.Equal(new[] { 2, 3 }, seen.Order());
        await using var db = databases.Users.CreateDbContext();
        var interrupted = await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 1);
        Assert.Equal("completed_with_review", interrupted.Status);
        Assert.Null(interrupted.Payload);
        Assert.Equal("completed", (await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 2)).Status);
        Assert.Null((await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 3)).Payload);
    }

    /// <summary>Drain expiry cancels active execution while retaining blocked queued work and rejecting new admission.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Shutdown_cancels_and_retains_durable_work"</code></example>
    [Fact]
    public async Task Shutdown_cancels_and_retains_durable_work()
    {
        using var databases = new Databases();
        var entered = Signal();
        using var scheduler = Create(databases, new Executor(async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }), concurrency: 1, drainSeconds: 1);
        await scheduler.StartAsync(default);
        await scheduler.EnqueueAsync("a", Update(1, 1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.EnqueueAsync("a", Update(2, 1), default);
        await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.EnqueueAsync("a", Update(3, 2), default));
        await using var db = databases.Users.CreateDbContext();
        Assert.Equal("completed_with_review", (await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 1)).Status);
        Assert.Null((await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 1)).Payload);
        Assert.Equal("queued", (await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 2)).Status);
    }

    /// <summary>Same-wallet duplicate events across bots commit exactly once, even using a recreated store.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Wallet_receipt_and_balance_commit_atomically_and_survive_restart"</code></example>
    [Fact]
    public async Task Wallet_receipt_and_balance_commit_atomically_and_survive_restart()
    {
        using var databases = new Databases();
        var store = new CredentialsStore(databases.Credentials);
        await store.AddEmptyUser(123);
        await store.MutateWalletAsync(123, 1000, "funding:1");
        var receipts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => new CredentialsStore(databases.Credentials).MutateWalletAsync(123, -100, "purchase:1:debit"))));
        Assert.All(receipts, r => { Assert.Equal(1000, r.BeforeBalance); Assert.Equal(900, r.AfterBalance); });
        var restarted = new CredentialsStore(databases.Credentials);
        await restarted.MutateWalletAsync(123, -100, "purchase:1:debit");
        Assert.Equal(900, await restarted.GetAccountBalance(123));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.MutateWalletAsync(123, -200, "purchase:1:debit"));
        await using var db = databases.Credentials.CreateDbContext();
        Assert.Equal(2, await db.WalletOperations.CountAsync());
    }

    /// <summary>Concurrent real-provider writes use independent trackers and preserve every committed operation.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Twenty_users_write_without_context_or_tracking_conflicts"</code></example>
    [Fact]
    public async Task Twenty_users_write_without_context_or_tracking_conflicts()
    {
        using var databases = new Databases();
        var store = new CredentialsStore(databases.Credentials);
        await Task.WhenAll(Enumerable.Range(1, 20).Select(user => Task.Run(async () =>
        {
            await store.GetUserStatus(new CredUser { TelegramUserId = user, FirstName = "test" });
            for (var operation = 0; operation < 5; operation++)
                await store.MutateWalletAsync(user, 10, $"load:{user}:{operation}");
        })));
        await using var db = databases.Credentials.CreateDbContext();
        Assert.Equal(100, await db.WalletOperations.CountAsync());
        Assert.All(await db.Users.AsNoTracking().ToListAsync(), user => Assert.Equal(50, user.AccountBalance));
        var first = await store.GetUserStatusWithId(1); var second = await store.GetUserStatusWithId(1);
        Assert.NotSame(first, second);
        first.AccountBalance = 999999;
        Assert.Equal(50, await store.GetAccountBalance(1));
    }

    /// <summary>A blocked fake HTTP call holds no inbox write transaction and unrelated writers can commit.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Network_wait_does_not_hold_sqlite_writer_transaction"</code></example>
    [Fact]
    public async Task Network_wait_does_not_hold_sqlite_writer_transaction()
    {
        using var databases = new Databases();
        var entered = Signal(); var release = Signal();
        using var scheduler = Create(databases, new Executor(async (_, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); }));
        await scheduler.StartAsync(default);
        await scheduler.EnqueueAsync("a", Update(1, 1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await databases.Inbox.TryAcceptAsync("a", Update(2, 2), 10, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult();
        await scheduler.StopAsync(default);
    }

    /// <summary>AsyncLocal scopes preserve client/config identity through concurrent awaits and exceptions.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Bot_context_is_isolated_and_restored"</code></example>
    [Fact]
    public async Task Bot_context_is_isolated_and_restored()
    {
        var accessor = new BotContextAccessor();
        var before = accessor.Current;
        var barrier = Signal(); var count = 0;
        await Task.WhenAll(Enumerable.Range(1, 2).Select(async id =>
        {
            var client = new Telegram.Bot.TelegramBotClient((12345 + id) + ":" + new string('a', 35));
            var context = new BotRuntimeContext { Config = new BotInstanceConfig { Id = "bot" + id, Username = "user" + id }, Client = client };
            try
            {
                using (accessor.Push(context))
                {
                    if (Interlocked.Increment(ref count) == 2) barrier.TrySetResult();
                    await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Same(context, accessor.Current);
                    Assert.Same(client, accessor.Current.Client);
                    Assert.Equal("bot" + id, BotContextAccessor.CurrentBotId);
                    using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "nested" } })) { }
                    Assert.Same(context, accessor.Current);
                    throw new InvalidOperationException("expected test exit");
                }
            }
            catch (InvalidOperationException) { Assert.Same(before, accessor.Current); }
        }));
        Assert.Same(before, accessor.Current);
    }

    /// <summary>Reports a 50-user mixed-latency integration workload without exact-millisecond assertions.</summary>
    /// <returns>A task completing after the documented regression invariant has been verified.</returns>
    /// <example><code>dotnet test Adminbot.Tests/Adminbot.Tests.csproj --filter "FullyQualifiedName~Fifty_user_workload_reports_concurrency_and_latency"</code></example>
    [Fact]
    public async Task Fifty_user_workload_reports_concurrency_and_latency()
    {
        using var databases = new Databases();
        var wallets = new CredentialsStore(databases.Credentials);
        for (var user = 1; user <= 50; user++) await wallets.AddEmptyUser(user);
        long retries = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, source) =>
        { if (instrument.Name == "sqlite.busy.retries") source.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref retries, value));
        listener.Start();
        var waits = new ConcurrentBag<double>(); var durations = new ConcurrentBag<double>();
        var active = 0; var maximum = 0;
        var executor = new Executor(async (item, token) =>
        {
            waits.Add((DateTime.UtcNow - item.AcceptedAtUtc).TotalMilliseconds);
            var started = Stopwatch.GetTimestamp();
            InterlockedMax(ref maximum, Interlocked.Increment(ref active));
            try
            {
                await wallets.MutateWalletAsync(item.Key.TelegramUserId, 1, $"load:{item.Update.Id}", token);
                await Task.Delay(item.Key.TelegramUserId % 5 == 0 ? 200 : 20, token);
            }
            finally { durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); Interlocked.Decrement(ref active); }
        });
        using var scheduler = Create(databases, executor, concurrency: 16);
        await scheduler.StartAsync(default);
        for (var user = 1; user <= 50; user++) await scheduler.EnqueueAsync("load", Update(user, user), default);
        await scheduler.StopAsync(default);
        Assert.Equal(50, durations.Count);
        Assert.InRange(maximum, 2, 16);
        await using var context = databases.Credentials.CreateDbContext();
        var effects = await context.WalletOperations.CountAsync();
        Assert.Equal(50, effects);
        Assert.All(await context.Users.ToListAsync(), user => Assert.Equal(1, user.AccountBalance));
        _output.WriteLine($"processed={durations.Count}; maxConcurrency={maximum}; p50HandlerMs={durations.Order().ElementAt(24):F1}; p95QueueWaitMs={waits.Order().ElementAt(47):F1}; sqliteRetries={retries}; duplicateEffects={effects - 50}; failures=0");
    }

    /// <summary>Creates a deterministic barrier whose continuation cannot run inline on the signaling thread.</summary>
    /// <returns>An unsignaled asynchronous barrier.</returns>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Builds a serializable private-message update with a reliable actor.</summary>
    /// <param name="id">Synthetic Telegram update id, unique within the test bot.</param>
    /// <param name="user">Positive synthetic Telegram sender and private chat id.</param>
    /// <returns>A complete private-message update accepted by the real Telegram JSON serializer.</returns>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    private static Update Update(int id, long user) => new() { Id = id, Message = new Message { Date = DateTime.UtcNow, Chat = new Chat { Id = user, Type = Telegram.Bot.Types.Enums.ChatType.Private }, From = new Telegram.Bot.Types.User { Id = user, FirstName = "test" } } };
    /// <summary>Creates a scheduler whose wait seam records every wake or timeout for deterministic wake assertions.</summary>
    /// <param name="db">Temporary database fixture owned by this test.</param>
    /// <param name="executor">Handler delegate that owns the test's external-I/O barriers.</param>
    /// <param name="wakes">Unbounded record of wait outcomes; a false outcome is a periodic-scan timeout.</param>
    /// <param name="concurrency">Positive active-handler limit.</param>
    /// <returns>An unstarted scheduler with a sixty-second wait bound; the test must stop it before disposing its databases.</returns>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    private static TelegramUpdateScheduler Create(Databases db, Executor executor, Channel<bool> wakes, int concurrency = 4) =>
        new(db.Inbox, executor, new AppConfig { TelegramUpdateMaxConcurrency = concurrency, TelegramUpdateQueueCapacity = 1000, TelegramUpdateShutdownDrainSeconds = 10 }, NullLogger<TelegramUpdateScheduler>.Instance)
        { WaitAsync = (signal, _, token) => RecordWakeAsync(signal, wakes, token) };

    /// <summary>Creates the actual scheduler with isolated persistence and controlled handlers.</summary>
    /// <param name="db">Temporary database fixture owned by this test.</param>
    /// <param name="executor">Handler delegate that owns the test's external-I/O barriers.</param>
    /// <param name="concurrency">Positive active-handler limit.</param>
    /// <param name="capacity">Positive unfinished-inbox admission limit.</param>
    /// <param name="drainSeconds">Positive shutdown drain interval in seconds.</param>
    /// <returns>An unstarted scheduler; the test must stop it before disposing its databases.</returns>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    private static TelegramUpdateScheduler Create(Databases db, Executor executor, int concurrency = 4, int capacity = 1000, int drainSeconds = 10) =>
        new(db.Inbox, executor, new AppConfig { TelegramUpdateMaxConcurrency = concurrency, TelegramUpdateQueueCapacity = capacity, TelegramUpdateShutdownDrainSeconds = drainSeconds }, NullLogger<TelegramUpdateScheduler>.Instance);
    /// <summary>Observes an eventual diagnostic condition with a bounded failure deadline.</summary>
    /// <param name="predicate">Thread-safe condition expected to become true.</param>
    /// <returns>A task completing when the condition is true; throws after ten seconds.</returns>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    /// <summary>Records an observed concurrency maximum without racing concurrent test handlers.</summary>
    /// <param name="location">Shared maximum storage.</param>
    /// <param name="value">Nonnegative concurrently observed active count.</param>
    /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
    private static void InterlockedMax(ref int location, int value)
    {
        int before;
        do { before = Volatile.Read(ref location); if (before >= value) return; }
        while (Interlocked.CompareExchange(ref location, value, before) != before);
    }

    /// <summary>Routes scheduled work into a controlled asynchronous test handler.</summary>
    /// <param name="execute">Required handler delegate; cancellation and failure behavior belong to the scenario.</param>
    /// <param name="available">Optional per-bot availability predicate; defaults to always available.</param>
    private sealed class Executor(Func<TelegramUpdateWorkItem, CancellationToken, Task> execute, Func<string, bool>? available = null) : ITelegramUpdateExecutor
    {
        /// <inheritdoc />
        public bool IsAvailable(string botId) => available?.Invoke(botId) ?? true;
        /// <inheritdoc />
        public Task ExecuteAsync(TelegramUpdateWorkItem item, CancellationToken cancellationToken) => execute(item, cancellationToken);
    }

    /// <summary>Owns two isolated real SQLite files and removes them after all test work has stopped.</summary>
    private sealed class Databases : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdminbotConcurrency-" + Guid.NewGuid().ToString("N"));
        /// <summary>Exact temporary directory owned by this fixture.</summary>
        public string DirectoryPath => _directory;
        /// <summary>Factory for independent users.db operations.</summary>
        public UserDbContextFactory Users { get; }
        /// <summary>Factory for the shared-wallet test database.</summary>
        public CredentialsDbContextFactory Credentials { get; }
        /// <summary>Production inbox store using the temporary users database.</summary>
        public TelegramUpdateInboxStore Inbox { get; }
        /// <summary>Creates isolated WAL databases without using production configuration.</summary>
        /// <param name="initialize">True creates the current model; false leaves schema creation to migration tests.</param>
        /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
        public Databases(bool initialize = true)
        {
            Directory.CreateDirectory(_directory);
            Users = new(new DbContextOptionsBuilder<UserDbContext>().UseSqlite(SqliteOperation.ConnectionString(Path.Combine(_directory, "users.db"))).Options);
            Credentials = new(new DbContextOptionsBuilder<CredentialsDbContext>().UseSqlite(SqliteOperation.ConnectionString(Path.Combine(_directory, "credentials.db"))).Options);
            using var users = Users.CreateDbContext(); using var credentials = Credentials.CreateDbContext();
            if (initialize) { users.Database.EnsureCreated(); credentials.Database.EnsureCreated(); }
            users.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); credentials.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            Inbox = new(Users, Credentials);
        }
        /// <summary>Closes SQLite pools and removes only this fixture's validated temporary directory.</summary>
        /// <remarks>Test callers own all barriers and lifetimes; dispose database fixtures only after every asynchronous operation has stopped.</remarks>
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // Only delete the exact random directory created by this fixture under the OS temp directory.
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory");
            Directory.Delete(_directory, recursive: true);
        }
    }
}
