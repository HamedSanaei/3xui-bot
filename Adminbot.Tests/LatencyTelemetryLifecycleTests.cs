using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Services.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;

/// <summary>Exercises real durable scheduling and SDK transport correlation without contacting Telegram.</summary>
/// <remarks>Temporary databases/files belong to each fixture. Barriers prove FIFO while the same user executes independently in another bot.</remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>One received update keeps its trace through admission, claim, ACK, visible send and final persistence.</summary>
    /// <returns>A task verifying durable execution and JSONL evidence for three isolated updates.</returns>
    /// <remarks>The first lane performs a real contended SQLite write, then is deliberately held while its successor waits;
    /// another bot must respond before that lane is released. The terminal summary must retain retry evidence and
    /// partition measured stage/gap wall time using the same handler origin as response milestones.</remarks>
    [Fact]
    public async Task Latency_timeline_correlates_real_inbox_and_http_while_preserving_bot_user_FIFO()
    {
        using var databases = new Databases();
        using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions(), databases.DirectoryPath,
            NullLogger<LatencyTelemetryService>.Instance);
        var tracker = new UpdateTelemetryTracker(telemetry, 100);
        var inbox = new TelegramUpdateInboxStore(databases.Users, databases.Credentials, tracker);
        await telemetry.StartAsync(default);
        var enteredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherBotResponded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = 0;
        using var wireA = new HttpClient(new TelegramTelemetryHttpHandler("a", telemetry, new TimelineWireHandler()));
        using var wireB = new HttpClient(new TelegramTelemetryHttpHandler("b", telemetry, new TimelineWireHandler()));
        var poll = new TelegramPollingTelemetryTracker(telemetry);
        var sdkA = new TelegramTelemetryBotClient(new TelegramBotClientOptions("123:test-only-secret") { RetryCount = 0 }, wireA, "a", telemetry, poll);
        var sdkB = new TelegramTelemetryBotClient(new TelegramBotClientOptions("124:test-only-secret") { RetryCount = 0 }, wireB, "b", telemetry, poll);
        var executor = new Executor(async (item, token) =>
        {
            var client = new ForegroundBoundedTelegramBotClient(item.Key.BotId == "a" ? sdkA : sdkB,
                TelegramForegroundDeliveryPolicy.Production, telemetry);
            if (item.Update.Id == 1)
            {
                Assert.Equal(2L, await ContendedTimelineWriteAsync(databases.DirectoryPath, token));
                await client.AnswerCallbackQuery("private-callback-data", cancellationToken: token);
                enteredFirst.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            if (item.Update.Id == 2) Interlocked.Exchange(ref secondEntered, 1);
            await client.SendMessage(11, "private-customer-message", cancellationToken: token);
            if (item.Key.BotId == "b") otherBotResponded.TrySetResult();
        });
        using var scheduler = new TelegramUpdateScheduler(inbox, executor,
            new AppConfig { TelegramUpdateQueueCapacity = 100, TelegramUpdateMaxConcurrency = 2, TelegramUpdateShutdownDrainSeconds = 5 },
            NullLogger<TelegramUpdateScheduler>.Instance, telemetry, tracker);
        try
        {
            await scheduler.StartAsync(default);
            using (tracker.Receive("a", 1, "Message")) await scheduler.EnqueueAsync("a", Update(1, 11), default);
            await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (tracker.Receive("a", 2, "Message")) await scheduler.EnqueueAsync("a", Update(2, 11), default);
            using (tracker.Receive("b", 3, "Message")) await scheduler.EnqueueAsync("b", Update(3, 11), default);
            await otherBotResponded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, Volatile.Read(ref secondEntered));
            releaseFirst.TrySetResult();
            await Until(() => Volatile.Read(ref secondEntered) == 1);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await scheduler.StopAsync(default);
            await telemetry.StopAsync(default);
        }
        var events = await ReadTimelineEventsAsync(telemetry.StorageDirectory);
        var summaries = events.Where(x => x.EventType == "telegram_update_completed" && x.Outcome == "completed").OrderBy(x => x.UpdateId).ToArray();
        Assert.Equal(new long?[] { 1, 2, 3 }, summaries.Select(x => x.UpdateId));
        Assert.Equal(0, telemetry.DroppedEvents);
        Assert.All(summaries, summary =>
        {
            Assert.NotNull(summary.Sequence);
            Assert.Equal(UpdateTelemetryTracker.TraceIdentity(summary.BotId, summary.UpdateId!.Value), summary.TraceId);
            Assert.False(summary.Recovered);
            Assert.NotNull(summary.ReceivedAtUtc);
            Assert.NotNull(summary.AdmissionStartedAtUtc);
            Assert.NotNull(summary.PersistedAtUtc);
            Assert.NotNull(summary.SchedulerWaitingAtUtc);
            Assert.NotNull(summary.ClaimedAtUtc);
            Assert.NotNull(summary.HandlerStartedAtUtc);
            Assert.NotNull(summary.FirstResponseAttemptAtUtc);
            Assert.NotNull(summary.FirstResponseCompletedAtUtc);
            Assert.NotNull(summary.HandlerCompletedAtUtc);
            Assert.NotNull(summary.InboxCompletedAtUtc);
            Assert.Equal("completed", summary.PersistenceOutcome);
            Assert.NotNull(summary.ApplicationMs);
            Assert.InRange(Math.Abs(summary.StageMs!.Values.Sum() + summary.UnattributedHandlerMs!.Value - summary.HandlerMs!.Value), 0, 0.000001);
            Assert.InRange(Math.Abs((summary.FirstResponseAttemptAtUtc!.Value - summary.HandlerStartedAtUtc!.Value).TotalMilliseconds
                - summary.FirstResponseAttemptMs!.Value), 0, 0.001);
            var sdkRequests = events.Where(x => x.EventType == "telegram_api_request_completed" && x.TraceId == summary.TraceId).ToArray();
            Assert.Contains(sdkRequests, request => request.Method == "sendMessage" && request.Sequence == summary.Sequence);
            Assert.All(sdkRequests, request => Assert.Equal(summary.BotId, request.BotId));
        });
        var first = summaries[0];
        Assert.NotNull(first.CallbackAckMs);
        Assert.True(first.FirstResponseAttemptMs > first.CallbackAckAcknowledgedMs);
        Assert.Equal(2, first.TelegramRequestCount);
        Assert.Equal(1, first.BusyRetryCount);
        Assert.True(first.BusyWaitMs >= 50);
        Assert.Contains(events, item => item.EventType == "sqlite_busy_retry" && item.TraceId == first.TraceId && item.SqliteErrorCode == 5);
        Assert.True(summaries[1].HandlerStartedAtUtc >= first.HandlerCompletedAtUtc);
        Assert.True(summaries[2].FirstResponseCompletedAtUtc <= first.FirstResponseAttemptAtUtc);
        Assert.True(summaries[1].QueueWaitMs > summaries[2].QueueWaitMs);
        var json = string.Join('\n', Directory.EnumerateFiles(telemetry.StorageDirectory, "*.jsonl").Select(File.ReadAllText));
        Assert.DoesNotContain("test-only-secret", json);
        Assert.DoesNotContain("private-customer-message", json);
        Assert.DoesNotContain("private-callback-data", json);
        await using var verify = databases.Users.CreateDbContext();
        Assert.All(await verify.TelegramUpdateInbox.ToListAsync(), row => { Assert.Equal("completed", row.Status); Assert.Null(row.Payload); });
    }

    /// <summary>Restart preserves queued execution and terminalizes a claimed update without fabricating pre-restart durations.</summary>
    /// <returns>A task verifying recovered timing provenance and stable bot/update trace identity.</returns>
    /// <remarks>The interrupted update must never execute again; its successor remains runnable under the unchanged inbox rule.</remarks>
    [Fact]
    public async Task Latency_restart_reports_missing_clocks_without_replaying_interrupted_claims()
    {
        using var databases = new Databases();
        Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(21, 11), 100, default));
        var interrupted = await databases.Inbox.ClaimAsync((await databases.Inbox.ReadReadyAsync(100, default))[0].Sequence, default);
        Assert.NotNull(interrupted);
        Assert.True(await databases.Inbox.TryAcceptAsync("a", Update(22, 11), 100, default));
        using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions(), databases.DirectoryPath, NullLogger<LatencyTelemetryService>.Instance);
        var tracker = new UpdateTelemetryTracker(telemetry, 100);
        var inbox = new TelegramUpdateInboxStore(databases.Users, databases.Credentials, tracker);
        var seen = new ConcurrentQueue<int>();
        var executor = new Executor((item, token) => { seen.Enqueue(item.Update.Id); return Task.CompletedTask; });
        using var scheduler = new TelegramUpdateScheduler(inbox, executor,
            new AppConfig { TelegramUpdateQueueCapacity = 100, TelegramUpdateShutdownDrainSeconds = 5 },
            NullLogger<TelegramUpdateScheduler>.Instance, telemetry, tracker);
        await telemetry.StartAsync(default);
        await scheduler.StartAsync(default);
        await Until(() => seen.Count == 1);
        await scheduler.StopAsync(default);
        await telemetry.StopAsync(default);
        Assert.Equal(new[] { 22 }, seen);
        var summaries = (await ReadTimelineEventsAsync(telemetry.StorageDirectory)).Where(x => x.EventType == "telegram_update_completed").ToArray();
        var recovered = Assert.Single(summaries, x => x.UpdateId == 21);
        Assert.Equal("process_interrupted", recovered.Outcome);
        Assert.Equal(UpdateTelemetryTracker.TraceIdentity("a", 21), recovered.TraceId);
        Assert.Equal(interrupted!.Sequence, recovered.Sequence);
        Assert.True(recovered.Recovered);
        Assert.Null(recovered.HandlerMs);
        Assert.Null(recovered.ReceivedAtUtc);
        Assert.Null(recovered.AdmissionMs);
        Assert.Null(recovered.ApplicationMs);
        Assert.NotNull(recovered.InboxAcceptedAtUtc);
        Assert.NotNull(recovered.ClaimedAtUtc);
        var queued = Assert.Single(summaries, x => x.UpdateId == 22);
        Assert.True(queued.Recovered);
        Assert.Null(queued.ReceivedAtUtc);
        Assert.NotNull(queued.QueueWaitMs);
        Assert.NotNull(queued.HandlerMs);
    }

    /// <summary>A claim racing the receiver's post-commit continuation cannot leak bounded metadata or lose subsequent origins.</summary>
    /// <returns>A task checking persisted summaries from three transitions through a capacity-one tracker.</returns>
    /// <remarks>The first claim deliberately precedes metadata publication; later ordinary claims must still have receiver timing.</remarks>
    [Fact]
    public async Task Latency_late_commit_observation_preserves_origin_and_releases_claimed_metadata()
    {
        using var databases = new Databases();
        using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions(), databases.DirectoryPath, NullLogger<LatencyTelemetryService>.Instance);
        var tracker = new UpdateTelemetryTracker(telemetry, 1);
        await telemetry.StartAsync(default);
        for (var id = 31; id <= 33; id++)
        {
            using var received = tracker.Receive("a", id, "Message");
            received.BeginAdmission();
            received.BeginAdmissionAttempt();
            var utc = DateTime.UtcNow;
            var item = new TelegramUpdateWorkItem(id, new("a", 11), Update(id, 11), utc, utc);
            if (id != 31) tracker.Persisted(id, utc);
            var claim = tracker.Claimed(item, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
            if (id == 31) tracker.Persisted(id, utc);
            using var scope = TelegramUpdateLatencyScope.Push(id, "a", id, TimeSpan.FromSeconds(2), null, telemetry: telemetry, traceId: claim.TraceId);
            claim.HandlerStarted();
            scope.Dispose();
            claim.HandlerCompleted(scope.CaptureTelemetry());
            claim.Completed(null, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        }
        await telemetry.StopAsync(default);
        var summaries = (await ReadTimelineEventsAsync(telemetry.StorageDirectory)).Where(x => x.EventType == "telegram_update_completed").OrderBy(x => x.UpdateId).ToArray();
        Assert.Equal(3, summaries.Length);
        Assert.All(summaries, x => { Assert.False(x.Recovered); Assert.NotNull(x.ReceivedAtUtc); Assert.NotNull(x.AdmissionMs); });
        Assert.Null(summaries[0].QueueWaitMs);
        Assert.NotNull(summaries[1].QueueWaitMs);
        Assert.NotNull(summaries[2].QueueWaitMs);
    }

    /// <summary>Drain-expiry recovery and a still-unwinding live handler must produce exactly one terminal summary.</summary>
    /// <returns>A task verifying persisted cancellation recovery without duplicate executions or fabricated final commits.</returns>
    /// <remarks>A real durable handler stays alive until its row has been terminalized by the existing shutdown recovery.</remarks>
    [Fact]
    public async Task Latency_shutdown_recovery_does_not_duplicate_live_terminal_summary()
    {
        using var databases = new Databases();
        using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions(), databases.DirectoryPath, NullLogger<LatencyTelemetryService>.Instance);
        var tracker = new UpdateTelemetryTracker(telemetry, 100);
        var inbox = new TelegramUpdateInboxStore(databases.Users, databases.Credentials, tracker);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                await unwinding.Task;
                throw;
            }
        });
        using var scheduler = new TelegramUpdateScheduler(inbox, executor,
            new AppConfig { TelegramUpdateQueueCapacity = 100, TelegramUpdateShutdownDrainSeconds = 1 },
            NullLogger<TelegramUpdateScheduler>.Instance, telemetry, tracker);
        await telemetry.StartAsync(default);
        await scheduler.StartAsync(default);
        await scheduler.EnqueueAsync("a", Update(41, 11), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = scheduler.StopAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var db = databases.Users.CreateDbContext();
                if (await db.TelegramUpdateInbox.AnyAsync(x => x.UpdateId == 41 && x.Status == "completed_with_review", deadline.Token)) break;
                await Task.Delay(10, deadline.Token);
            }
        }
        finally { unwinding.TrySetResult(); await stopping; await telemetry.StopAsync(default); }
        var summary = Assert.Single(await ReadTimelineEventsAsync(telemetry.StorageDirectory), x => x.EventType == "telegram_update_completed");
        Assert.Contains(summary.Outcome, new[] { "process_interrupted", "execution_cancelled" });
        Assert.NotEqual("completed", summary.PersistenceOutcome);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TelegramUpdateInbox.SingleAsync();
        Assert.Equal("completed_with_review", row.Status);
        Assert.Equal("process_interrupted", row.FailureCode);
        Assert.Null(row.Payload);
    }

    /// <summary>Exercises the existing real BUSY retry under an update scope without touching inbox or financial tables.</summary>
    /// <param name="directory">Fixture-owned absolute temporary directory, not production Data.</param>
    /// <param name="token">The executing lane's cancellation token.</param>
    /// <returns>The persisted probe counter, exactly two after holder commit and one successful retried write.</returns>
    /// <remarks>The holder releases only after the provider actually reports SQLite BUSY. Retry policy and delay are production code.</remarks>
    private static async Task<long> ContendedTimelineWriteAsync(string directory, CancellationToken token)
    {
        var connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "timeline_busy.db"), Pooling = false, DefaultTimeout = 1 }.ToString();
        await using var holder = new SqliteConnection(connectionString);
        await holder.OpenAsync(token);
        using (var setup = holder.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE diag_counter(value INTEGER NOT NULL); INSERT INTO diag_counter VALUES(0);";
            await setup.ExecuteNonQueryAsync(token);
        }
        await using var transaction = holder.BeginTransaction();
        using (var locked = holder.CreateCommand())
        {
            locked.Transaction = transaction;
            locked.CommandText = "UPDATE diag_counter SET value=1";
            await locked.ExecuteNonQueryAsync(token);
        }
        await SqliteOperation.RunAsync(async cancellation =>
        {
            using var stage = TelegramUpdateLatencyScope.Current?.Measure(TelegramUpdateStage.SqliteWrite) ?? default;
            await using var contender = new SqliteConnection(connectionString);
            await contender.OpenAsync(cancellation);
            using var command = contender.CreateCommand();
            command.CommandText = "UPDATE diag_counter SET value=value+1";
            try { return await command.ExecuteNonQueryAsync(cancellation); }
            catch (SqliteException error) when (error.SqliteErrorCode == 5)
            {
                await transaction.CommitAsync(cancellation);
                throw;
            }
        }, token);
        using var verify = holder.CreateCommand();
        verify.CommandText = "SELECT value FROM diag_counter";
        return (long)(await verify.ExecuteScalarAsync(token))!;
    }

    /// <summary>Reads fixture-owned JSONL records after writer shutdown.</summary>
    /// <param name="directory">Absolute temporary telemetry directory.</param>
    /// <returns>Schema-one records from the bounded test fixture, including lifecycle evidence.</returns>
    /// <remarks>Tests only use small controlled fixtures; production reporting remains streaming and bounded.</remarks>
    private static async Task<List<LatencyTelemetryEvent>> ReadTimelineEventsAsync(string directory)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var records = new List<LatencyTelemetryEvent>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
            foreach (var line in await File.ReadAllLinesAsync(file))
                records.Add(JsonSerializer.Deserialize<LatencyTelemetryEvent>(line, options)!);
        return records;
    }

    /// <summary>Produces valid SDK acknowledgments without recording request URL, body or customer content.</summary>
    private sealed class TimelineWireHandler : HttpMessageHandler
    {
        /// <summary>Returns Telegram-shaped callback and send responses for the actual SDK request pipeline.</summary>
        /// <param name="request">Private SDK request; its final method alone selects the controlled response.</param>
        /// <param name="cancellationToken">Original linked request cancellation.</param>
        /// <returns>An SDK-valid response that acknowledges a single attempted operation.</returns>
        /// <remarks>No remote service or persisted private request content is involved.</remarks>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var callback = request.RequestUri!.AbsolutePath.EndsWith("/answerCallbackQuery", StringComparison.Ordinal);
            var json = callback ? "{\"ok\":true,\"result\":true}"
                : "{\"ok\":true,\"result\":{\"message_id\":1,\"date\":1,\"chat\":{\"id\":11,\"type\":\"private\"}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
