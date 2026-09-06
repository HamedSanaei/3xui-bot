using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Xunit;

/// <summary>Regression coverage for explicit creation boundaries, reviewed inbox resolution and event-driven scheduling.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Each durable creation state has an explicit authorization and quarantine policy after restart.</summary>
    /// <param name="outcome">Persisted creation outcome at the simulated crash boundary.</param>
    /// <param name="mayPost">Whether exactly one unused POST claim is permitted.</param>
    /// <param name="blocks">Whether the linked update requires XUI reconciliation.</param>
    /// <returns>A task completing after restart and repeated authorization checks.</returns>
    [Theory]
    [InlineData(XuiV3CreationOutcome.Reserved, true, false)]
    [InlineData(XuiV3CreationOutcome.PostStarted, false, true)]
    [InlineData(XuiV3CreationOutcome.Applied, false, false)]
    [InlineData(XuiV3CreationOutcome.DefinitiveRejected, false, false)]
    [InlineData(XuiV3CreationOutcome.Ambiguous, false, true)]
    public async Task Creation_state_controls_post_and_lane_after_restart(XuiV3CreationOutcome outcome, bool mayPost, bool blocks)
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(new() { OperationKey = "event-1", PanelKey = "panel", TelegramUserId = 123,
                ClientJson = "private", InboundIdsJson = "[1]", Outcome = outcome, InboxSequence = 10 });
            await db.SaveChangesAsync();
        }
        var store = new XuiV3CreationOperationStore(databases.Users);
        Assert.Equal(blocks, await databases.Inbox.HasUnresolvedCreationAsync(10, default));
        var claim = await store.ReserveAsync(new() { OperationKey = "event-1", PanelKey = "panel", TelegramUserId = 123,
            ClientJson = "different-generated-identity", InboundIdsJson = "[1]" }, default);
        Assert.Equal(mayPost, claim.MayCreate); Assert.Equal("private", claim.Operation.ClientJson);
        Assert.Equal(mayPost, await store.TryStartPostAsync("event-1", default));
        Assert.False(await store.TryStartPostAsync("event-1", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveAsync(new() { OperationKey = "event-1",
            PanelKey = "other-panel", TelegramUserId = 123, InboundIdsJson = "[1]" }, default));
    }

    /// <summary>Real HTTP outcomes preserve one POST, terminal rejection and identity-safe read-back recovery.</summary>
    /// <param name="mode">Controlled provider response/crash window.</param>
    /// <param name="expected">Expected durable classification after the original invocation.</param>
    /// <returns>A task completing after a restarted call and exact POST-count assertions.</returns>
    [Theory]
    [InlineData("reject", XuiV3CreationOutcome.DefinitiveRejected)]
    [InlineData("lost-success", XuiV3CreationOutcome.Applied)]
    [InlineData("success-get-fails", XuiV3CreationOutcome.Applied)]
    [InlineData("duplicate-match", XuiV3CreationOutcome.Applied)]
    [InlineData("duplicate-mismatch", XuiV3CreationOutcome.Ambiguous)]
    [InlineData("timeout", XuiV3CreationOutcome.Ambiguous)]
    public async Task Real_creation_outcomes_never_blindly_repeat_post(string mode, XuiV3CreationOutcome expected)
    {
        using var databases = new Databases(); var posts = 0; JObject? identity = null;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref posts);
                identity = (JObject)JObject.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync())["client"]!;
                identity["inboundIds"] = new JArray(1);
                if (mode is "lost-success" or "timeout") { context.Abort(); return; }
                await context.Response.WriteAsJsonAsync(new { success = mode == "success-get-fails",
                    msg = mode == "reject" ? "at least one inbound is required" : "client already exists" }); return;
            }
            if (context.Request.Path.Value!.Contains("links")) { await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}"); return; }
            if (mode == "success-get-fails") { context.Response.StatusCode = 503; return; }
            if (mode is "reject" or "timeout") { await context.Response.WriteAsync("{\"success\":false}"); return; }
            var found = (JObject)identity!.DeepClone();
            if (mode == "duplicate-mismatch") found["subId"] = "different-identity";
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = found }.ToString());
        });
        await app.StartAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["xuiV3TransientRetryCount"] = "0", ["xuiV3RequestTimeoutSeconds"] = "5" }).Build();
            var request = new AccountDto { TelegramUserId = 123, TotoalGB = "10",
                ServerInfo = new() { Url = app.Urls.Single(), ApiToken = "test-only" } };
            var options = new XuiV3CreateAccountOptions { InboundIds = [1], DurationDays = 30, TrafficGb = 10,
                SaveUserStatus = false, OperationKey = "outcome-test", OperationStore = new(databases.Users) };
            using (TelegramUpdateExecutionScope.Push(10))
                await ApiServicev3.CreateUserAccountAsync(request, configuration, options);
            await using var db = databases.Users.CreateDbContext();
            Assert.Equal(expected, (await db.XuiV3CreationOperations.AsNoTracking().SingleAsync()).Outcome);
            Assert.Equal(expected == XuiV3CreationOutcome.Ambiguous, await databases.Inbox.HasUnresolvedCreationAsync(10, default));
            options.OperationStore = new(databases.Users);
            await ApiServicev3.CreateUserAccountAsync(request, configuration, options);
            Assert.Equal(1, posts);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Old nullable timestamps migrate conservatively and new empty databases reach the same current schema.</summary>
    /// <returns>A task completing after pre-change data and all migrations have been verified.</returns>
    [Fact]
    public async Task Creation_migration_preserves_historical_unknowns()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260905220449_LinkCreationOperationsToInbox");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO XuiV3CreationOperations(OperationKey,PanelKey,TelegramUserId,ClientJson,InboundIdsJson,CreatedAtUtc) VALUES ('historical','panel',123,'private','[1]','2026-01-01')");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO XuiV3CreationOperations(OperationKey,PanelKey,TelegramUserId,ClientJson,InboundIdsJson,CreatedAtUtc,AppliedAtUtc) VALUES ('historical-applied','panel',123,'private','[1]','2026-01-01','2026-01-02')");
        await migrator.MigrateAsync();
        var migrated = (await db.XuiV3CreationOperations.OrderBy(x => x.OperationKey).ToListAsync());
        Assert.Equal(XuiV3CreationOutcome.Ambiguous, migrated.Single(x => x.OperationKey == "historical").Outcome);
        Assert.Equal(XuiV3CreationOutcome.Applied, migrated.Single(x => x.OperationKey == "historical-applied").Outcome);
        using var fresh = new Databases(initialize: false);
        await using var freshDb = fresh.Users.CreateDbContext(); await freshDb.Database.MigrateAsync();
        Assert.False(freshDb.Database.HasPendingModelChanges());
    }

    /// <summary>A handled definitive rejection completes its lane so the next accepted user update runs.</summary>
    /// <returns>A task completing after two FIFO handlers and the terminal durable receipt are verified.</returns>
    [Fact]
    public async Task Definitive_rejection_does_not_quarantine_lane()
    {
        using var databases = new Databases(); var seen = new ConcurrentQueue<int>(); var creations = new XuiV3CreationOperationStore(databases.Users);
        using var scheduler = Create(databases, new Executor(async (item, token) =>
        {
            seen.Enqueue(item.Update.Id);
            if (item.Update.Id != 1) return;
            await creations.ReserveAsync(new() { OperationKey = "rejected", PanelKey = "panel", TelegramUserId = 123,
                ClientJson = "private", InboundIdsJson = "[1]" }, token);
            Assert.True(await creations.TryStartPostAsync("rejected", token));
            await creations.MarkFailureAsync("rejected", XuiV3CreationOutcome.DefinitiveRejected, token);
        }));
        await scheduler.EnqueueAsync("a", Update(1, 123), default); await scheduler.EnqueueAsync("a", Update(2, 123), default);
        await scheduler.StartAsync(default); await scheduler.StopAsync(default);
        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.Equal(0, (await databases.Inbox.ReadUncertainSummaryAsync(default)).Count);
    }

    /// <summary>Only the private-chat global super-admin can inspect or resolve; proof gates and audit fields survive restart.</summary>
    /// <returns>A task completing after denied access, ambiguous refusal, proven resolution and payload erasure.</returns>
    [Fact]
    public async Task Admin_review_requires_authority_and_terminal_effects()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["bots:0:id"] = "owned", ["bots:0:token"] = "12345:" + new string('a',35), ["bots:0:isDefault"] = "true" }).Build();
        var registry = new BotRegistry(configuration); var config = new AppConfig { AdminsUserIds = [456] };
        var service = new TelegramInboxAdminService(databases.Inbox, databases.Users, config, configuration, registry);
        await databases.Inbox.TryAcceptAsync("owned", Update(1,123), 10, default);
        var sequence = (await databases.Inbox.ReadReadyAsync(10, default)).Single().Sequence;
        await databases.Inbox.ClaimAsync(sequence, default); await databases.Inbox.RecoverAsync(default);
        var creations = new XuiV3CreationOperationStore(databases.Users);
        using (TelegramUpdateExecutionScope.Push(sequence)) await creations.ReserveAsync(new() { OperationKey = "admin-test",
            PanelKey = "panel", TelegramUserId = 123, ClientJson = "SECRET_PAYLOAD", InboundIdsJson = "[1]" }, default);
        await creations.TryStartPostAsync("admin-test", default);
        Assert.Equal("Denied.", await service.ExecuteAuthorizedAsync("owned",123,true,["/inbox_uncertain"],default));
        Assert.Equal("Denied.", await service.ExecuteAuthorizedAsync("owned",123,true,["/inbox_resolve",sequence.ToString(),"review-1"],default));
        Assert.False(service.IsAuthorized("tenant",456,true)); Assert.False(service.IsAuthorized("owned",456,false));
        var inspect = await service.ExecuteAuthorizedAsync("owned",456,true,["/inbox_uncertain",sequence.ToString()],default);
        Assert.Contains("PostStarted",inspect); Assert.DoesNotContain("SECRET_PAYLOAD",inspect);
        Assert.Contains("refused",await service.ExecuteAuthorizedAsync("owned",456,true,["/inbox_resolve",sequence.ToString(),"review-1"],default));
        await creations.MarkAppliedAsync("admin-test",default);
        Assert.Contains("completed",await service.ExecuteAuthorizedAsync("owned",456,true,["/inbox_resolve",sequence.ToString(),"review-1"],default));
        await using var db=databases.Users.CreateDbContext(); var row=await db.TelegramUpdateInbox.SingleAsync();
        Assert.Null(row.Payload); Assert.Equal(456,row.ReviewedByTelegramUserId); Assert.NotNull(row.ReviewedAtUtc);
        Assert.Equal("review-1",row.ReviewReference); Assert.Equal("process_interrupted",row.FailureCode);
    }

    /// <summary>Committed but unreconciled money prevents review until the existing idempotent worker repairs the second database.</summary>
    /// <returns>A task completing after refusal, wallet reconciliation, and safe lane release.</returns>
    [Fact]
    public async Task Review_refuses_unreconciled_wallet_receipt()
    {
        using var databases = new Databases();
        await databases.Inbox.TryAcceptAsync("a",Update(1,123),10,default);
        var sequence=(await databases.Inbox.ReadReadyAsync(10,default)).Single().Sequence;
        await databases.Inbox.ClaimAsync(sequence,default); await databases.Inbox.RecoverAsync(default);
        var wallets=new CredentialsStore(databases.Credentials); await wallets.AddEmptyUser(123);
        using(TelegramUpdateExecutionScope.Push(sequence)) await wallets.MutateWalletAsync(123,100,"admin:review:credit");
        Assert.False((await databases.Inbox.CanResolveAsync(sequence,default)).Allowed);
        await using(var db=databases.Credentials.CreateDbContext()) await db.WalletOperations.ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAtUtc,DateTime.UtcNow.AddMinutes(-2)));
        var worker=new WalletOperationReconciliationService(databases.Credentials,databases.Users,new WalletLedgerService(databases.Users,wallets),NullLogger<WalletOperationReconciliationService>.Instance);
        Assert.Equal(1,await worker.ReconcileAsync());
        Assert.True(await databases.Inbox.ResolveReviewedAsync(sequence,456,"review-2",default));
        Assert.Equal(100,await wallets.GetAccountBalance(123));
    }

    /// <summary>Three controlled idle intervals perform one query each; direct durable admission is recovered without a signal.</summary>
    /// <returns>A task completing after deterministic fallback and post-commit wake assertions.</returns>
    [Fact]
    public async Task Scheduler_idle_queries_follow_recovery_boundaries()
    {
        using var databases=new Databases(); var waits=Channel.CreateUnbounded<TaskCompletionSource>(); var executed=Signal();
        var controlled=1;
        using var scheduler=new TelegramUpdateScheduler(databases.Inbox,new Executor((_,_)=>{executed.TrySetResult();return Task.CompletedTask;}),new AppConfig(),NullLogger<TelegramUpdateScheduler>.Instance)
        {
            WaitAsync=async(signal,interval,token)=>
            {
                if(Volatile.Read(ref controlled)==0) return await signal.WaitAsync(interval,token);
                Assert.Equal(TimeSpan.FromSeconds(2),interval);
                var gate=Signal(); await waits.Writer.WriteAsync(gate,token); await gate.Task.WaitAsync(token); return false;
            }
        };
        await scheduler.StartAsync(default);
        for(var i=1;i<=3;i++)
        {
            var gate=await waits.Reader.ReadAsync(); Assert.Equal(i,scheduler.ReadyQueryCount); gate.TrySetResult();
        }
        var last=await waits.Reader.ReadAsync(); Assert.Equal(4,scheduler.ReadyQueryCount);
        var direct=new TelegramUpdateInboxStore(databases.Users,databases.Credentials); // no scheduler subscription
        await direct.TryAcceptAsync("a",Update(1,123),10,default);
        Assert.False(executed.Task.IsCompleted); Assert.Equal(4,scheduler.ReadyQueryCount);
        Volatile.Write(ref controlled,0); last.TrySetResult();
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await scheduler.StopAsync(default);
        _output.WriteLine("idle simulatedElapsedSeconds=6 readyQueries=4 initialQuery=1 readyQueriesPerSecondIdle=0.5 previousApproximate=40");
    }

    /// <summary>Multiple bots and fifty FIFO users share sixteen workers under coalesced concurrent admissions.</summary>
    /// <returns>A task completing after exact effect/order assertions and measured scheduler diagnostics.</returns>
    [Fact]
    public async Task Scheduler_fifty_users_multiple_updates_benchmark()
    {
        using var databases=new Databases(); var effects=new ConcurrentDictionary<int,byte>(); var last=new ConcurrentDictionary<long,int>();
        var waits=new ConcurrentBag<double>(); var active=0; var maximum=0; var violations=0; long retries=0;
        using var listener=new MeterListener();
        listener.InstrumentPublished=(instrument,meter)=>{if(instrument.Name=="sqlite.busy.retries")meter.EnableMeasurementEvents(instrument);};
        listener.SetMeasurementEventCallback<long>((_,value,_,_)=>Interlocked.Add(ref retries,value)); listener.Start();
        var release=Signal(); var saturated=Signal();
        using var scheduler=Create(databases,new Executor(async(item,token)=>
        {
            var running=Interlocked.Increment(ref active); InterlockedMax(ref maximum,running); if(running==16)saturated.TrySetResult();
            waits.Add((DateTime.UtcNow-item.AcceptedAtUtc).TotalMilliseconds);
            try
            {
                await release.Task.WaitAsync(token); await Task.Delay(10,token);
                var ordinal=item.Update.Id%100; var previous=last.GetValueOrDefault(item.Key.TelegramUserId);
                if(ordinal!=previous+1) Interlocked.Increment(ref violations);
                last[item.Key.TelegramUserId]=ordinal; Assert.True(effects.TryAdd(item.Update.Id,0));
            }
            finally{Interlocked.Decrement(ref active);}
        }),concurrency:16);
        // Per-user acceptance is ordered; producers for distinct users run concurrently and wakes coalesce.
        await Task.WhenAll(Enumerable.Range(1,50).Select(user=>Task.Run(async()=>
        {for(var ordinal=1;ordinal<=3;ordinal++)await scheduler.EnqueueAsync(user%2==0?"a":"b",Update(user*100+ordinal,user),default);} )));
        await scheduler.StartAsync(default); await saturated.Task.WaitAsync(TimeSpan.FromSeconds(10)); release.TrySetResult();
        await scheduler.StopAsync(default);
        Assert.Equal(150,effects.Count); Assert.Equal(0,violations); Assert.Equal(16,maximum);
        var sorted=waits.Order().ToArray(); var uncertain=(await databases.Inbox.ReadUncertainSummaryAsync(default)).Count; Assert.Equal(0,uncertain);
        _output.WriteLine($"totalUpdates=150 completed={effects.Count} uncertain={uncertain} duplicateEffects={150-effects.Count} fifoViolations={violations} maxObservedConcurrency={maximum} p50QueueWaitMs={sorted[74]:F1} p95QueueWaitMs={sorted[142]:F1} p99QueueWaitMs={sorted[148]:F1} readyQueryCount={scheduler.ReadyQueryCount} sqliteBusyRetryCount={retries}");
    }

    /// <summary>An ordinary handler exception is reviewable without wallet, XUI, order or settlement evidence.</summary>
    /// <returns>A task completing after quarantine, safe review and lane release.</returns>
    /// <remarks>The evaluator must not require financial evidence for an update that never touched financial systems.</remarks>
    [Fact]
    public async Task Ordinary_non_financial_exception_is_reviewable_without_wallet_or_xui_evidence()
    {
        using var databases = new Databases();
        var seen = new ConcurrentQueue<int>();
        using var scheduler = Create(databases, new Executor((item, _) =>
        {
            if (item.Update.Id == 1) throw new InvalidOperationException("ordinary handler failure");
            seen.Enqueue(item.Update.Id); return Task.CompletedTask;
        }));
        await scheduler.EnqueueAsync("a", Update(1, 123), default);
        await scheduler.EnqueueAsync("a", Update(2, 123), default);
        var sequence = (await databases.Inbox.ReadReadyAsync(10, default)).Single().Sequence;
        await scheduler.StartAsync(default);
        await WaitForAsync(async () => (await databases.Inbox.CanResolveAsync(sequence, default)).Allowed);
        Assert.True(await databases.Inbox.ResolveReviewedAsync(sequence, 456, "review-4", default));
        await Until(() => seen.Contains(2));
        await scheduler.StopAsync(default);
        Assert.Equal(new[] { 2 }, seen);
    }

    /// <summary>Same-lane completion wakes the next update without waiting for the periodic scan.</summary>
    /// <returns>A task completing after the second update ran on the completion wake.</returns>
    /// <remarks>The wait seam has a sixty-second timeout, so any progress inside the five-second deadline is wake-driven.</remarks>
    [Fact]
    public async Task Same_lane_completion_wakes_next_update_without_periodic_scan()
    {
        using var databases = new Databases();
        var entered = Signal(); var release = Signal(); var secondRan = Signal();
        var wakes = Channel.CreateUnbounded<bool>();
        using var scheduler = Create(databases, new Executor(async (item, token) =>
        {
            if (item.Update.Id == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            else secondRan.TrySetResult();
        }), wakes);
        await scheduler.EnqueueAsync("a", Update(1, 123), default);
        await scheduler.EnqueueAsync("a", Update(2, 123), default);
        await scheduler.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(default);
        Assert.DoesNotContain(false, Drain(wakes));
    }

    /// <summary>An admin resolution commit wakes the blocked lane without waiting for the periodic scan.</summary>
    /// <returns>A task completing after the resolved lane's next update ran on the resolution wake.</returns>
    /// <remarks>Resolution is safe here because the quarantined update never touched wallet or XUI state.</remarks>
    [Fact]
    public async Task Admin_resolution_wakes_blocked_lane_without_periodic_scan()
    {
        using var databases = new Databases();
        var secondRan = Signal();
        var wakes = Channel.CreateUnbounded<bool>();
        using var scheduler = Create(databases, new Executor((item, _) =>
        {
            if (item.Update.Id == 1) throw new InvalidOperationException("ordinary handler failure");
            secondRan.TrySetResult(); return Task.CompletedTask;
        }), wakes);
        await scheduler.EnqueueAsync("a", Update(1, 123), default);
        await scheduler.EnqueueAsync("a", Update(2, 123), default);
        var sequence = (await databases.Inbox.ReadReadyAsync(10, default)).Single().Sequence;
        await scheduler.StartAsync(default);
        await WaitForAsync(async () => (await databases.Inbox.CanResolveAsync(sequence, default)).Allowed);
        Assert.True(await databases.Inbox.ResolveReviewedAsync(sequence, 456, "review-5", default));
        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(default);
        Assert.DoesNotContain(false, Drain(wakes));
    }

    /// <summary>Durable work for an unavailable bot resumes promptly when runtime availability changes.</summary>
    /// <returns>A task completing after the deferred update ran on the availability wake.</returns>
    /// <remarks>Mirrors production wiring where the registry availability event feeds the inbox readiness signal.</remarks>
    [Fact]
    public async Task Bot_availability_change_wakes_deferred_durable_work()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["bots:0:id"] = "owned", ["bots:0:token"] = "12345:" + new string('a', 35), ["bots:0:isDefault"] = "true", ["bots:0:enabled"] = "false" }).Build();
        var registry = new BotRegistry(configuration);
        registry.AvailabilityChanged += databases.Inbox.NotifyReady;
        var available = 0; var ran = Signal(); var wakes = Channel.CreateUnbounded<bool>();
        using var scheduler = Create(databases, new Executor((item, _) => { ran.TrySetResult(); return Task.CompletedTask; },
            bot => Volatile.Read(ref available) == 1), wakes);
        await scheduler.EnqueueAsync("owned", Update(1, 123), default);
        await scheduler.StartAsync(default);
        await wakes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(ran.Task.IsCompleted);
        Volatile.Write(ref available, 1);
        registry.Upsert(new BotInstance { Id = "owned", Enabled = true, Token = "12345:" + new string('a', 35) });
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(default);
        Assert.DoesNotContain(false, Drain(wakes));
    }

    /// <summary>Uncertain rows consume admission capacity, emit sanitized warnings, and the admin plane stays reachable.</summary>
    /// <returns>A task completing after bounded admission, warning capture and an authorized listing.</returns>
    /// <remarks>Quarantine pressure is observable but never exempt from capacity; the receiver-owned control path is independent of customer admission.</remarks>
    [Fact]
    public async Task Uncertain_rows_consume_capacity_and_admin_control_remains_reachable()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            for (var i = 1; i <= 8; i++)
                db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry { BotId = "a", UpdateId = 1000 + i, TelegramUserId = i,
                    Status = "uncertain", FailureCode = "process_interrupted", AcceptedAtUtc = DateTime.UtcNow.AddMinutes(-30) });
            await db.SaveChangesAsync();
        }
        var logs = new ListLogger<TelegramUpdateScheduler>();
        using (var scheduler = new TelegramUpdateScheduler(databases.Inbox, new Executor((_, _) => Task.CompletedTask),
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 5 }, logs))
        {
            await scheduler.StartAsync(default);
            Assert.Contains(logs.Messages, message => message.Contains("telegramInboxUncertainCount=8"));
            Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(1, 901), 10, default));
            Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(2, 902), 10, default));
            Assert.False(await databases.Inbox.TryAcceptAsync("a", Update(3, 903), 10, default));
            Assert.Equal(10, await databases.Inbox.CountPendingAsync(default));
            await scheduler.StopAsync(default);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["bots:0:id"] = "owned", ["bots:0:token"] = "12345:" + new string('a', 35), ["bots:0:isDefault"] = "true" }).Build();
        var service = new TelegramInboxAdminService(databases.Inbox, databases.Users, new AppConfig { AdminsUserIds = [456] }, configuration, new BotRegistry(configuration));
        var listing = await service.ExecuteAuthorizedAsync("owned", 456, true, ["/inbox_uncertain"], default);
        Assert.Contains("Sequence=", listing);
        Assert.Contains("Failure=process_interrupted", listing);
        Assert.Equal("Denied.", await service.ExecuteAuthorizedAsync("owned", 123, true, ["/inbox_uncertain"], default));
    }

    /// <summary>Reconciliation performs identity-safe GET only and never issues a replacement POST.</summary>
    /// <returns>A task completing after the linked creation is proven by read-back and the lane is resolved.</returns>
    /// <remarks>No customer handler runs; wallet, renewal and link evidence remain gated by the conservative evaluator.</remarks>
    [Fact]
    public async Task Inbox_reconcile_reads_back_linked_creations_without_posting()
    {
        using var databases = new Databases();
        var gets = 0; var posts = 0; JObject? identity = null;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST") { Interlocked.Increment(ref posts); context.Response.StatusCode = 500; return; }
            Interlocked.Increment(ref gets);
            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { success = true, obj = identity }));
        });
        await app.StartAsync();
        try
        {
            await databases.Inbox.TryAcceptAsync("owned", Update(1, 123), 10, default);
            var sequence = (await databases.Inbox.ReadReadyAsync(10, default)).Single().Sequence;
            await databases.Inbox.ClaimAsync(sequence, default);
            await databases.Inbox.RecoverAsync(default);
            var panel = new ServerInfo { ApiVersion = "v3", Url = app.Urls.Single(), RootPath = "", ApiToken = "test-only" };
            var reserved = new XuiV3ClientPayload { Email = "reserved@test", SubId = "reserved-sub", TgId = 123, Uuid = Guid.NewGuid().ToString() };
            identity = (JObject)JObject.Parse(JsonConvert.SerializeObject(reserved))!; identity["inboundIds"] = new JArray(1);
            await using (var db = databases.Users.CreateDbContext())
            {
                db.XuiV3CreationOperations.Add(new XuiV3CreationOperation
                {
                    OperationKey = "reconcile:1", TelegramUserId = 123, PanelKey = XuiV3LinkChangeOperationStore.BuildPanelKey(panel),
                    ClientJson = JsonConvert.SerializeObject(reserved), InboundIdsJson = "[1]", Outcome = XuiV3CreationOutcome.PostStarted,
                    InboxSequence = sequence, CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["bots:0:id"] = "owned", ["bots:0:token"] = "12345:" + new string('a', 35), ["bots:0:isDefault"] = "true" }).Build();
            var service = new TelegramInboxAdminService(databases.Inbox, databases.Users,
                new AppConfig { AdminsUserIds = [456], XuiV3ApiBaseUrl = app.Urls.Single() }, configuration, new BotRegistry(configuration));
            var reconcile = await service.ExecuteAuthorizedAsync("owned", 456, true, ["/inbox_reconcile", sequence.ToString()], default);
            Assert.Contains("creation=[Applied:1]", reconcile);
            Assert.Equal(0, posts); Assert.True(gets >= 1);
            Assert.Contains("completed", await service.ExecuteAuthorizedAsync("owned", 456, true,
                ["/inbox_resolve", sequence.ToString(), "review-6"], default));
            await using var verify = databases.Users.CreateDbContext();
            var row = await verify.TelegramUpdateInbox.SingleAsync();
            Assert.Null(row.Payload); Assert.Equal("review-6", row.ReviewReference);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Polls an eventual durable condition with a bounded deadline.</summary>
    /// <param name="condition">Thread-safe condition expected to become true.</param>
    /// <returns>A task completing when the condition is true; throws after ten seconds.</returns>
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, deadline.Token);
    }

    /// <summary>Waits on the coalesced signal with a sixty-second bound and records whether a wake fired.</summary>
    /// <param name="signal">Scheduler wake signal; a true result means a committed state change was observed.</param>
    /// <param name="wakes">Unbounded record of wait outcomes for deterministic assertions.</param>
    /// <param name="token">Coordinator cancellation.</param>
    /// <returns>The observed wait outcome, also recorded in the channel.</returns>
    /// <remarks>The long bound makes periodic-scan timeouts impossible inside the five-second test deadlines.</remarks>
    private static async Task<bool> RecordWakeAsync(SemaphoreSlim signal, Channel<bool> wakes, CancellationToken token)
    {
        var result = await signal.WaitAsync(TimeSpan.FromSeconds(60), token);
        await wakes.Writer.WriteAsync(result, token);
        return result;
    }

    /// <summary>Returns every recorded wait outcome observed so far without consuming future signals.</summary>
    /// <param name="wakes">Unbounded wake record channel.</param>
    /// <returns>All buffered outcomes; an empty list means no wait has completed yet.</returns>
    private static List<bool> Drain(Channel<bool> wakes)
    {
        var values = new List<bool>();
        while (wakes.Reader.TryRead(out var value)) values.Add(value);
        return values;
    }

    /// <summary>Captures warning-level scheduler messages without exposing payloads.</summary>
    /// <typeparam name="T">Logger category; only the formatted message is retained.</typeparam>
    private sealed class ListLogger<T> : ILogger<T>, IDisposable
    {
        /// <summary>Formatted warning or error messages observed by the logger.</summary>
        public ConcurrentQueue<string> Messages { get; } = new();
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        /// <inheritdoc />
        public void Dispose() { }
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) Messages.Enqueue(formatter(state, exception)); }
    }
}
