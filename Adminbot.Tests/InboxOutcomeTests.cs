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
    /// <summary>Each durable creation state has explicit POST authorization and recovery behavior after restart.</summary>
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
    public async Task Creation_state_controls_post_and_business_recovery_after_restart(XuiV3CreationOutcome outcome, bool mayPost, bool blocks)
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
    public async Task Definitive_rejection_keeps_user_lane_available()
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

    /// <summary>An ordinary handler exception becomes terminal and releases the same user's next message automatically.</summary>
    /// <returns>A task completing after the failure receipt and the next same-user execution are observed.</returns>
    /// <remarks>No administrator action is required when a handler never created a durable business recovery incident.</remarks>
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
        await scheduler.StartAsync(default);
        await Until(() => seen.Contains(2));
        await scheduler.StopAsync(default);
        Assert.Equal(new[] { 2 }, seen);
        await using var db = databases.Users.CreateDbContext();
        var failure = await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 1);
        Assert.Equal("completed_with_error", failure.Status); Assert.Null(failure.Payload);
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

    /// <summary>A terminal review receipt never delays a later same-user message or require an admin wake.</summary>
    /// <returns>A task completing after the queued successor executes while review remains pending.</returns>
    /// <remarks>This directly protects /start and main-menu navigation from historical recovery records.</remarks>
    [Fact]
    public async Task Terminal_review_receipt_never_requires_admin_resolution_for_progress()
    {
        using var databases = new Databases();
        var secondRan = Signal();
        var wakes = Channel.CreateUnbounded<bool>();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry { BotId = "a", UpdateId = 1, TelegramUserId = 123,
                Status = "completed_with_review", FailureCode = "creation_requires_review", AcceptedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var scheduler = Create(databases, new Executor((_, _) => { secondRan.TrySetResult(); return Task.CompletedTask; }), wakes);
        await scheduler.EnqueueAsync("a", Update(2, 123), default);
        await scheduler.StartAsync(default);
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

    /// <summary>Legacy recovery rows consume no admission capacity and unchanged scans emit no repeated alerts.</summary>
    /// <returns>A task completing after legacy conversion, one recovery alert, one hundred scans, and authorized inspection.</returns>
    /// <remarks>Recovery is observable while remaining independent of customer admission and Telegram execution.</remarks>
    [Fact]
    public async Task Recovery_rows_do_not_consume_capacity_or_repeat_scheduler_alerts()
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
        using (var scheduler = new TelegramUpdateScheduler(databases.Inbox,
            new Executor((_, _) => Task.CompletedTask, _ => false),
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 5 }, logs)
            { RecoveryInterval = TimeSpan.FromMilliseconds(1) })
        {
            await scheduler.StartAsync(default);
            Assert.Single(logs.Messages, message => message.Contains("telegramRecoveryIncidentCount=8"));
            Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(1, 901), 10, default));
            Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(2, 902), 10, default));
            Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(3, 903), 10, default));
            Assert.Equal(3, await databases.Inbox.CountPendingAsync(default));
            await Until(() => scheduler.ReadyQueryCount >= 100);
            Assert.Single(logs.Messages);
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

    /// <summary>Only the exact authenticated not-found envelope permits reviewed rejection, and the command stays GET-only.</summary>
    /// <param name="mode">Controlled panel behavior: authoritative absence, server failure, or unknown API refusal.</param>
    /// <param name="rejected">Whether the linked ambiguous operation may become definitively rejected.</param>
    /// <returns>A task completing after authorization, HTTP-method, audit, and state assertions.</returns>
    /// <remarks>
    /// Regression coverage for the production repair command: customers, tenant bots, and groups receive no evidence;
    /// 5xx and unfamiliar provider messages preserve ambiguity and can never authorize another creation POST.
    /// </remarks>
    [Theory]
    [InlineData("not-found", true)]
    [InlineData("server", false)]
    [InlineData("unknown", false)]
    public async Task Reviewed_creation_rejection_requires_authoritative_absence_and_never_posts(string mode, bool rejected)
    {
        using var databases = new Databases();
        var gets = 0; var posts = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST") { Interlocked.Increment(ref posts); context.Response.StatusCode = 500; return; }
            Interlocked.Increment(ref gets);
            if (mode == "server") { context.Response.StatusCode = 503; return; }
            await context.Response.WriteAsJsonAsync(mode == "not-found"
                ? new { success = false, msg = "Obtain (record not found)" }
                : new { success = false, msg = "unrecognized provider response" });
        });
        await app.StartAsync();
        try
        {
            await databases.Inbox.TryAcceptAsync("owned", Update(1, 123), 10, default);
            var sequence = (await databases.Inbox.ReadReadyAsync(10, default)).Single().Sequence;
            await databases.Inbox.ClaimAsync(sequence, default); await databases.Inbox.RecoverAsync(default);
            var panel = new ServerInfo { ApiVersion = "v3", Url = app.Urls.Single(), RootPath = "", ApiToken = "test-only" };
            await using (var db = databases.Users.CreateDbContext())
            {
                db.XuiV3CreationOperations.Add(new XuiV3CreationOperation
                {
                    OperationKey = "reviewed-absence:1", TelegramUserId = 123,
                    PanelKey = XuiV3LinkChangeOperationStore.BuildPanelKey(panel),
                    ClientJson = JsonConvert.SerializeObject(new XuiV3ClientPayload { Email = "persisted@example", TgId = 123 }),
                    InboundIdsJson = "[1]", Outcome = XuiV3CreationOutcome.Ambiguous,
                    InboxSequence = sequence, CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["bots:0:id"] = "owned", ["bots:0:token"] = "12345:" + new string('a', 35), ["bots:0:isDefault"] = "true" }).Build();
            var service = new TelegramInboxAdminService(databases.Inbox, databases.Users,
                new AppConfig { AdminsUserIds = [456], XuiV3ApiBaseUrl = app.Urls.Single() }, configuration, new BotRegistry(configuration));

            Assert.Equal("Denied.", await service.ExecuteAuthorizedAsync("owned", 123, true,
                ["/inbox_reject_creation", sequence.ToString(), "review-7"], default));
            Assert.Equal("Denied.", await service.ExecuteAuthorizedAsync("owned", 456, false,
                ["/inbox_reject_creation", sequence.ToString(), "review-7"], default));
            Assert.Equal("Denied.", await service.ExecuteAuthorizedAsync("tenant", 456, true,
                ["/inbox_reject_creation", sequence.ToString(), "review-7"], default));

            var result = await service.ExecuteAuthorizedAsync("owned", 456, true,
                ["/inbox_reject_creation", sequence.ToString(), "review-7"], default);
            await using var verify = databases.Users.CreateDbContext();
            var operation = await verify.XuiV3CreationOperations.SingleAsync();
            Assert.Equal(rejected ? XuiV3CreationOutcome.DefinitiveRejected : XuiV3CreationOutcome.Ambiguous, operation.Outcome);
            Assert.Equal(0, posts); Assert.True(gets >= 1);
            if (rejected)
            {
                Assert.Contains("definitiveRejected=1", result);
                var inbox = await verify.TelegramUpdateInbox.SingleAsync();
                Assert.Equal(456, inbox.ReviewedByTelegramUserId); Assert.Equal("review-7", inbox.ReviewReference);
                Assert.True((await databases.Inbox.CanResolveAsync(sequence, default)).Allowed);
                Assert.True(await databases.Inbox.ResolveReviewedAsync(sequence, 456, "review-7", default));
                await verify.Entry(inbox).ReloadAsync(); Assert.Equal("completed", inbox.Status); Assert.Null(inbox.Payload);
            }
            else Assert.Contains("inconclusive", result);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>A timeout during the exact panel read preserves ambiguity and never reaches a POST endpoint.</summary>
    /// <returns>A task completing after cooperative cancellation and durable-state verification.</returns>
    /// <remarks>Elapsed time is never treated as evidence that the reserved client is absent.</remarks>
    [Fact]
    public async Task Reviewed_creation_rejection_timeout_remains_ambiguous()
    {
        using var databases = new Databases(); var posts = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST") Interlocked.Increment(ref posts);
            await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
        });
        await app.StartAsync();
        try
        {
            await databases.Inbox.TryAcceptAsync("owned", Update(1, 123), 10, default);
            var sequence = (await databases.Inbox.ReadReadyAsync(10, default)).Single().Sequence;
            await databases.Inbox.ClaimAsync(sequence, default); await databases.Inbox.RecoverAsync(default);
            var panel = new ServerInfo { ApiVersion = "v3", Url = app.Urls.Single(), ApiToken = "test-only" };
            await using (var db = databases.Users.CreateDbContext())
            {
                db.XuiV3CreationOperations.Add(new XuiV3CreationOperation { OperationKey = "timeout:1", TelegramUserId = 123,
                    PanelKey = XuiV3LinkChangeOperationStore.BuildPanelKey(panel), ClientJson = JsonConvert.SerializeObject(
                        new XuiV3ClientPayload { Email = "persisted@example", TgId = 123 }), InboundIdsJson = "[1]",
                    Outcome = XuiV3CreationOutcome.Ambiguous, InboxSequence = sequence, CreatedAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["bots:0:id"] = "owned", ["bots:0:token"] = "12345:" + new string('a', 35), ["bots:0:isDefault"] = "true" }).Build();
            var service = new TelegramInboxAdminService(databases.Inbox, databases.Users,
                new AppConfig { AdminsUserIds = [456], XuiV3ApiBaseUrl = app.Urls.Single() }, configuration, new BotRegistry(configuration));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAuthorizedAsync("owned", 456, true,
                ["/inbox_reject_creation", sequence.ToString(), "review-8"], timeout.Token));
            await using var verify = databases.Users.CreateDbContext();
            Assert.Equal(XuiV3CreationOutcome.Ambiguous, (await verify.XuiV3CreationOperations.SingleAsync()).Outcome);
            Assert.Equal(0, posts);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>The reviewed store transition is exact and monotonic, so Applied can never be downgraded.</summary>
    /// <returns>A task completing after exact-sequence, exact-key, and terminal-state guards are verified.</returns>
    [Fact]
    public async Task Reviewed_absence_transition_cannot_modify_applied_or_unrelated_creation()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry { Sequence = 41, BotId = "owned", UpdateId = 1,
                TelegramUserId = 123, Status = "uncertain", AcceptedAtUtc = DateTime.UtcNow });
            db.XuiV3CreationOperations.AddRange(
                new XuiV3CreationOperation { OperationKey = "applied", TelegramUserId = 123, PanelKey = "p", ClientJson = "{}",
                    InboundIdsJson = "[]", InboxSequence = 41, Outcome = XuiV3CreationOutcome.Applied },
                new XuiV3CreationOperation { OperationKey = "other", TelegramUserId = 123, PanelKey = "p", ClientJson = "{}",
                    InboundIdsJson = "[]", InboxSequence = 42, Outcome = XuiV3CreationOutcome.Ambiguous });
            await db.SaveChangesAsync();
        }
        var store = new XuiV3CreationOperationStore(databases.Users);
        Assert.False(await store.MarkOperatorProvenAbsentAsync(41, "applied", 456, "review-9", default));
        Assert.False(await store.MarkOperatorProvenAbsentAsync(41, "other", 456, "review-9", default));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(XuiV3CreationOutcome.Applied, (await verify.XuiV3CreationOperations.SingleAsync(x => x.OperationKey == "applied")).Outcome);
        Assert.Equal(XuiV3CreationOutcome.Ambiguous, (await verify.XuiV3CreationOperations.SingleAsync(x => x.OperationKey == "other")).Outcome);
    }

    /// <summary>Legacy link fallback is time-bounded while an exact inbox sequence always remains authoritative.</summary>
    /// <param name="ageMinutes">Age of a same-bot/user legacy row relative to inbox acceptance.</param>
    /// <param name="exact">Whether the operation carries the exact modern inbox sequence.</param>
    /// <param name="blocks">Expected conservative resolution result.</param>
    /// <returns>A task completing after evidence and non-mutation assertions.</returns>
    /// <remarks>A July manual-review row cannot block a September update; a recent nullable row still can.</remarks>
    [Theory]
    [InlineData(69120, false, false)]
    [InlineData(1, false, true)]
    [InlineData(69120, true, true)]
    public async Task Inbox_link_correlation_bounds_legacy_history_but_preserves_exact_links(int ageMinutes, bool exact, bool blocks)
    {
        using var databases = new Databases(); var accepted = DateTime.UtcNow;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry { Sequence = 51, BotId = "owned", UpdateId = 1,
                TelegramUserId = 123, Status = "uncertain", FailureCode = "process_interrupted", AcceptedAtUtc = accepted });
            db.XuiV3LinkChangeOperations.Add(new XuiV3LinkChangeOperation { InboxSequence = exact ? 51 : null,
                OperationKey = Guid.NewGuid().ToString("N"), PanelKey = "safe-panel-fingerprint", BotId = "owned",
                TelegramUserId = 123, ClientId = 17, Status = XuiV3LinkChangeStatuses.ManualReview,
                CreatedAtUtc = accepted.AddMinutes(-ageMinutes), UpdatedAtUtc = accepted.AddMinutes(-ageMinutes) });
            await db.SaveChangesAsync();
        }
        var evidence = await databases.Inbox.CanResolveAsync(51, default);
        Assert.Equal(!blocks, evidence.Allowed);
        Assert.Contains(exact ? "exactLinkPending=1" : blocks ? "legacyCorrelatedLinkPending=1" : "legacyCorrelatedLinkPending=0", evidence.Evidence);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(XuiV3LinkChangeStatuses.ManualReview, (await verify.XuiV3LinkChangeOperations.SingleAsync()).Status);
    }

    /// <summary>Renewal fallback uses the same bounded legacy window as link changes.</summary>
    /// <returns>A task completing after historical and recent nullable renewal evidence is classified.</returns>
    /// <remarks>July manual-review renewals remain untouched and cannot block a September inbox lane.</remarks>
    [Fact]
    public async Task Inbox_renewal_correlation_does_not_block_old_nullable_history()
    {
        using var databases = new Databases(); var accepted = DateTime.UtcNow;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry { Sequence = 61, BotId = "owned", UpdateId = 1,
                TelegramUserId = 123, Status = "uncertain", AcceptedAtUtc = accepted });
            db.XuiV3RenewalOperations.Add(new XuiV3RenewalOperation { OperationKey = "legacy-renewal",
                OperationId = "legacy-renewal", BotId = "owned", TelegramUserId = 123, TargetEmail = "old@example",
                Status = XuiV3RenewalOperationStatuses.ManualReview, SettlementStatus = XuiV3RenewalSettlementStatuses.Pending,
                CreatedAtUtc = accepted.AddDays(-48), UpdatedAtUtc = accepted.AddDays(-48) });
            await db.SaveChangesAsync();
        }
        var evidence = await databases.Inbox.CanResolveAsync(61, default);
        Assert.True(evidence.Allowed); Assert.Contains("legacyCorrelatedRenewalPending=0", evidence.Evidence);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, (await verify.XuiV3RenewalOperations.SingleAsync()).Status);
    }

    /// <summary>Every supported business recovery category remains durable while a later same-user message executes.</summary>
    /// <param name="incident">Recovery category representing creation, renewal, wallet settlement, link change, or tenant fulfillment.</param>
    /// <returns>A task completing after the later update executes and the selected recovery record survives.</returns>
    /// <remarks>
    /// Regression invariant: recovery scopes one immutable business operation. It never scopes the customer's
    /// <c>BotId + TelegramUserId</c> lane, so /start and ordinary navigation remain available.
    /// </remarks>
    [Theory]
    [InlineData("creation_ambiguous")]
    [InlineData("renewal_ambiguous")]
    [InlineData("wallet_settlement_pending")]
    [InlineData("link_manual_review")]
    [InlineData("tenant_order_unfulfilled")]
    public async Task Business_recovery_never_blocks_later_same_user_messages(string incident)
    {
        using var databases = new Databases();
        long sequence;
        await using (var db = databases.Users.CreateDbContext())
        {
            var receipt = new TelegramUpdateInboxEntry { BotId = "a", UpdateId = 1, TelegramUserId = 123,
                Status = "completed_with_review", FailureCode = "process_interrupted", AcceptedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow };
            db.TelegramUpdateInbox.Add(receipt); await db.SaveChangesAsync(); sequence = receipt.Sequence;
            switch (incident)
            {
                case "creation_ambiguous":
                    db.XuiV3CreationOperations.Add(new XuiV3CreationOperation { OperationKey = "availability-create",
                        PanelKey = "panel", TelegramUserId = 123, ClientJson = "private", InboundIdsJson = "[1]",
                        Outcome = XuiV3CreationOutcome.Ambiguous, InboxSequence = sequence });
                    break;
                case "renewal_ambiguous":
                    db.XuiV3RenewalOperations.Add(new XuiV3RenewalOperation { OperationKey = "availability-renew",
                        OperationId = "availability-renew", BotId = "a", TelegramUserId = 123, TargetEmail = "private@example",
                        Status = XuiV3RenewalOperationStatuses.Ambiguous,
                        SettlementStatus = XuiV3RenewalSettlementStatuses.Pending, InboxSequence = sequence });
                    break;
                case "link_manual_review":
                    db.XuiV3LinkChangeOperations.Add(new XuiV3LinkChangeOperation { OperationKey = "availability-link",
                        PanelKey = "panel", BotId = "a", TelegramUserId = 123, ClientId = 7,
                        Status = XuiV3LinkChangeStatuses.ManualReview, InboxSequence = sequence,
                        CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
                    break;
                case "tenant_order_unfulfilled":
                    db.TenantBotOrders.Add(new TenantBotOrder { OrderId = "availability-order", TenantBotId = "a",
                        OwnerTelegramUserId = 456, CustomerTelegramUserId = 123, CustomerChatId = 123,
                        PaymentStatus = TenantBotOrderStatuses.Paid, IsFulfilled = false });
                    break;
            }
            await db.SaveChangesAsync();
        }
        if (incident == "wallet_settlement_pending")
        {
            await using var credentials = databases.Credentials.CreateDbContext();
            credentials.WalletOperations.Add(new WalletOperation { OperationKey = "availability-wallet", TelegramUserId = 123,
                AmountToman = -10, BeforeBalance = 100, AfterBalance = 90, BotId = "a", InboxSequence = sequence,
                CreatedAtUtc = DateTime.UtcNow, ReconciledAtUtc = null });
            await credentials.SaveChangesAsync();
        }

        var ran = Signal();
        using var scheduler = Create(databases, new Executor((item, _) => { ran.TrySetResult(); return Task.CompletedTask; }));
        await scheduler.EnqueueAsync("a", Update(2, 123), default);
        await scheduler.StartAsync(default); await ran.Task.WaitAsync(TimeSpan.FromSeconds(5)); await scheduler.StopAsync(default);
        Assert.Equal(1, (await databases.Inbox.ReadUncertainSummaryAsync(default)).Count);
        Assert.Equal(0, await databases.Inbox.CountPendingAsync(default));
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
