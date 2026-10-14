using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using Telegram.Bot.Types;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Five distinct rapid renewal clicks execute one selector and retain five terminal inbox receipts.</summary>
    /// <returns>Completion after original handler, four ACKs and all durable receipts are checked.</returns>
    /// <remarks>Regression for the October 13 FIFO incident. Later deliberate clicks and intervening navigation must still execute.</remarks>
    [Fact]
    public async Task Renewal_selector_burst_is_acknowledged_without_repeating_business_handlers()
    {
        using var databases = new Databases();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new SelectorBurstExecutor(async (item, token) =>
        {
            if (item.Update.Id == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        });
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 4, TelegramUpdateQueueCapacity = 20, TelegramUpdateShutdownDrainSeconds = 2 },
            NullLogger<TelegramUpdateScheduler>.Instance);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", SelectorUpdate(1), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var id = 2; id <= 5; id++) await scheduler.EnqueueAsync("owned", SelectorUpdate(id), default);
            release.TrySetResult();
            await UntilAsync(async () => { await using var db = databases.Users.CreateDbContext();
                return await db.TelegramUpdateInbox.CountAsync(x => x.Status == "completed") == 5; });
            Assert.Equal(new[] { 1 }, executor.Executed.ToArray());
            Assert.Equal(new[] { 2, 3, 4, 5 }, executor.Acknowledged.ToArray());

            // A fresh click after completion is intentional and must revalidate the live account.
            await scheduler.EnqueueAsync("owned", SelectorUpdate(6), default);
            await Until(() => executor.Executed.Contains(6));
            var navigation = SelectorUpdate(7, "x3:home");
            await scheduler.EnqueueAsync("owned", navigation, default);
            await scheduler.EnqueueAsync("owned", SelectorUpdate(8), default);
            await Until(() => executor.Executed.Contains(8));
            Assert.Contains(7, executor.Executed);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        await using var verify = databases.Users.CreateDbContext();
        Assert.All(await verify.TelegramUpdateInbox.ToListAsync(), row =>
        { Assert.Equal("completed", row.Status); Assert.Null(row.Payload); Assert.Null(row.FailureCode); });
        Assert.Empty(await verify.XuiV3RenewalOperations.ToListAsync());
    }

    /// <summary>A failed original selector cannot suppress the customer's queued retry.</summary>
    /// <returns>Completion after both business calls run and only the first has an error receipt.</returns>
    /// <remarks>Failure does not establish a usable selector; bypassing the second attempt would strand the user.</remarks>
    [Fact]
    public async Task Renewal_selector_failed_original_keeps_queued_retry_runnable()
    {
        using var databases = new Databases();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new SelectorBurstExecutor(async (item, token) =>
        {
            if (item.Update.Id == 1)
            { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new InvalidOperationException("test failure"); }
        });
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 4, TelegramUpdateShutdownDrainSeconds = 2 },
            NullLogger<TelegramUpdateScheduler>.Instance);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("tenant-test", SelectorUpdate(1), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scheduler.EnqueueAsync("tenant-test", SelectorUpdate(2), default);
            release.TrySetResult();
            await UntilAsync(async () => { await using var db = databases.Users.CreateDbContext();
                return await db.TelegramUpdateInbox.CountAsync(x => x.Status.StartsWith("completed")) == 2; });
            Assert.Equal(new[] { 1, 2 }, executor.Executed.ToArray());
            Assert.Empty(executor.Acknowledged);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
    }

    /// <summary>Only exact same-message read-only selectors can be suppressed; money, navigation and identity boundaries cannot.</summary>
    /// <param name="difference">One input boundary varied from the original selector.</param>
    /// <remarks>A repeated physical click has a new update id; equality of callback routing data alone is insufficient.</remarks>
    [Theory]
    [InlineData("bot")]
    [InlineData("user")]
    [InlineData("message")]
    [InlineData("chat")]
    [InlineData("target")]
    [InlineData("page")]
    [InlineData("origin")]
    [InlineData("payment")]
    [InlineData("external")]
    [InlineData("later")]
    [InlineData("restart")]
    public void Renewal_selector_coalescing_preserves_scope_and_financial_callbacks(string difference)
    {
        var now = DateTime.UtcNow;
        var original = new TelegramUpdateWorkItem(1, new("owned", 123), SelectorUpdate(1), now.AddSeconds(-1), now);
        var tracker = new TelegramRenewalSelectorCoalescer();
        tracker.RecordSuccess(original);
        var repeat = original with { Sequence = 2, Update = SelectorUpdate(2) };
        Assert.True(tracker.ShouldCoalesce(repeat));
        if (difference == "bot") repeat = repeat with { Key = new("tenant", 123) };
        if (difference == "user") { repeat.Update.CallbackQuery!.From.Id = 456; repeat = repeat with { Key = new("owned", 456) }; }
        if (difference == "message") repeat.Update.CallbackQuery!.Message!.Id++;
        if (difference == "chat") repeat.Update.CallbackQuery!.Message!.Chat.Id++;
        if (difference == "target") repeat.Update.CallbackQuery!.Data = "x3:aren:18:0";
        if (difference == "page") repeat.Update.CallbackQuery!.Data = "x3:aren:17:1";
        if (difference == "origin") repeat.Update.CallbackQuery!.Data = "x3:asren:17:0";
        if (difference == "payment") repeat.Update.CallbackQuery!.Data = "x3:ok:unlimited:u:u2-m1";
        if (difference == "external") repeat.Update.CallbackQuery!.Data = "x3:rgo";
        if (difference == "later") repeat = repeat with { AcceptedAtUtc = now.AddSeconds(1) };
        if (difference == "restart") tracker = new TelegramRenewalSelectorCoalescer();
        Assert.False(tracker.ShouldCoalesce(repeat));
    }

    /// <summary>A post-update observation is reused for pure comparisons; every repair still requires a new read-back.</summary>
    /// <param name="mode">Healthy state, ignored inbound/traffic repair, or conflicting panel identity.</param>
    /// <returns>Completion after real loopback HTTP verifies request counts and fail-closed outcomes.</returns>
    /// <remarks>Ignored repairs deliberately answer success without applying changes; this must never produce a verified result.</remarks>
    [Theory]
    [InlineData("healthy")]
    [InlineData("inbound_partial")]
    [InlineData("traffic_partial")]
    [InlineData("identity_mismatch")]
    [InlineData("recreated")]
    [InlineData("field_partial")]
    public async Task Link_change_reconciliation_reuses_reads_and_rejects_partial_repairs(string mode)
    {
        var payload = new XuiV3ClientPayload
        {
            Email = "new-test", Uuid = "00000000-0000-0000-0000-000000000017", SubId = "new-sub",
            TgId = 123, TotalGB = 1000, ExpiryTime = 2000000000000, Enable = true, LimitIp = 1,
            Comment = "{}", Flow = "", Group = "", Password = ""
        };
        var client = JObject.FromObject(payload);
        client["id"] = mode == "recreated" ? 99 : 17;
        if (mode == "field_partial") client["limitIp"] = 7;
        client["inboundIds"] = mode == "inbound_partial" ? new JArray() : new JArray(1);
        var reads = new ConcurrentDictionary<string, int>();
        var posts = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            var path = context.Request.Path.Value!;
            reads.AddOrUpdate(path, 1, (_, count) => count + 1);
            JToken? obj;
            if (context.Request.Method == "POST") { Interlocked.Increment(ref posts); obj = new JObject(); }
            else if (path.EndsWith("/list")) obj = new JArray(client.DeepClone());
            else if (path.EndsWith("/get/new-test"))
            {
                obj = client.DeepClone();
                if (mode == "identity_mismatch") obj["id"] = 99;
            }
            else if (path.Contains("/traffic/")) obj = new JObject
                { ["email"] = "new-test", ["up"] = mode == "traffic_partial" ? 1 : 100, ["down"] = 200, ["total"] = 1000 };
            else obj = null; // Old email is authoritatively absent; no orphan delete is needed.
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = obj }.ToString(Formatting.None));
        });
        await app.StartAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["XuiV3TransientRetryCount"] = "1", ["XuiV3ApiToken"] = "test-only" }).Build();
            var service = new XuiV3BotFlowService(null!, null!, null!, null!, configuration,
                NullLogger<XuiV3BotFlowService>.Instance, null!, null!, null!, null!, null!, null!, null!, null!, null!, new AppConfig());
            var snapshotType = typeof(XuiV3BotFlowService).GetNestedType("XuiV3LinkChangeSnapshot", BindingFlags.NonPublic)!;
            var oldClient = client.ToObject<XuiV3Client>()!; oldClient.Email = "old-test"; oldClient.Id = 17; oldClient.LimitIp = 1;
            var snapshot = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new
                { ClientId = 17, Client = oldClient, InboundIds = new[] { 1 }, Up = 100L, Down = 200L }), snapshotType)!;
            var method = typeof(XuiV3BotFlowService).GetMethod("ReconcileChangedLinkStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var latency = TelegramUpdateLatencyScope.Push(1, "test", 1, TimeSpan.FromSeconds(2), (_, _) => { });
            var task = (Task)method.Invoke(service, new object[]
                { new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" }, snapshot, payload, "new-test", "test-operation", CancellationToken.None })!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var success = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;
            Assert.Equal(mode == "healthy", success);
            if (mode == "healthy")
            {
                Assert.Equal(0, posts);
                Assert.Equal(5, reads.Values.Sum()); // One full post-update read plus old-identity absence proof.
                var summary = latency.CaptureTelemetry();
                Assert.Contains("xui_response_parse", summary.StageMs!.Keys);
                Assert.Contains("xui_snapshot_processing", summary.StageMs.Keys);

                // Identity-only probes no longer download all clients or traffic when the direct id is authoritative.
                reads.Clear();
                var identityMethod = typeof(XuiV3BotFlowService).GetMethod("ObserveChangedLinkIdentityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var identityTask = (Task)identityMethod.Invoke(service, new object[]
                    { new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" }, "new-test", 17, CancellationToken.None })!;
                await identityTask;
                Assert.Equal("Observed", identityTask.GetType().GetProperty("Result")!.GetValue(identityTask)!.ToString());
                Assert.Equal(1, reads.Values.Sum());
            }
            else if (mode is "identity_mismatch" or "recreated") Assert.Equal(0, posts);
            else
            {
                Assert.Equal(1, posts);
                Assert.True(reads["/panel/api/clients/get/new-test"] >= 2);
            }
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>An unavailable direct identity GET remains Unknown and cannot authorize another mutation.</summary>
    /// <returns>Completion after the real adapter consumes a temporary panel failure using GET only.</returns>
    /// <remarks>Regression against treating timeout/5xx as proof that a saved rename never committed.</remarks>
    [Fact]
    public async Task Link_change_identity_probe_unavailable_stays_unknown_and_read_only()
    {
        var methods = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(context => { methods.Enqueue(context.Request.Method); context.Response.StatusCode = 503; return Task.CompletedTask; });
        await app.StartAsync();
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["XuiV3TransientRetryCount"] = "1", ["XuiV3TransientRetryBaseDelayMs"] = "1" }).Build();
            var service = new XuiV3BotFlowService(null!, null!, null!, null!, config,
                NullLogger<XuiV3BotFlowService>.Instance, null!, null!, null!, null!, null!, null!, null!, null!, null!, new AppConfig());
            var method = typeof(XuiV3BotFlowService).GetMethod("ObserveChangedLinkIdentityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(service, new object[]
                { new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" }, "new-test", 17, CancellationToken.None })!;
            await task;
            Assert.Equal("Unknown", task.GetType().GetProperty("Result")!.GetValue(task)!.ToString());
            Assert.NotEmpty(methods);
            Assert.All(methods, methodName => Assert.Equal("GET", methodName));
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Constructs a new physical callback click with a stable bot-message and account target.</summary>
    /// <param name="id">Distinct Telegram update/callback identity.</param>
    /// <param name="data">Optional exact callback payload; defaults to the owner-only renewal selector.</param>
    /// <returns>An isolated SDK update whose routing data is safe synthetic test data.</returns>
    private static Telegram.Bot.Types.Update SelectorUpdate(int id, string data = "x3:aren:17:0") => new()
    {
        Id = id,
        CallbackQuery = new CallbackQuery
        {
            Id = "test-click-" + id, Data = data, From = new Telegram.Bot.Types.User { Id = 123, FirstName = "test" },
            Message = new Message { Id = 50, Date = DateTime.UtcNow,
                Chat = new Chat { Id = 123, Type = Telegram.Bot.Types.Enums.ChatType.Private } }
        }
    };

    /// <summary>Counts real selector executions separately from coalesced callback acknowledgements.</summary>
    /// <param name="execute">Controlled non-financial selector body; the original uses a barrier to queue a burst.</param>
    private sealed class SelectorBurstExecutor(Func<TelegramUpdateWorkItem, CancellationToken, Task> execute) : ITelegramUpdateExecutor
    {
        /// <summary>Update ids entering the business handler, in lane order.</summary>
        public ConcurrentQueue<int> Executed { get; } = new();
        /// <summary>Distinct repeat update ids receiving only callback acknowledgement.</summary>
        public ConcurrentQueue<int> Acknowledged { get; } = new();
        /// <inheritdoc />
        public bool IsAvailable(string botId) => true;
        /// <inheritdoc />
        public async Task ExecuteAsync(TelegramUpdateWorkItem item, CancellationToken cancellationToken)
        { Executed.Enqueue(item.Update.Id); await execute(item, cancellationToken); }
        /// <inheritdoc />
        public Task AcknowledgeRenewalSelectorAsync(TelegramUpdateWorkItem item, CancellationToken cancellationToken)
        { Acknowledged.Enqueue(item.Update.Id); return Task.CompletedTask; }
    }
}
