using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Services.Telemetry;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Xunit;

/// <summary>Isolated behavioral regressions for persisted latency, real SDK boundaries and recoverable bounded storage.</summary>
/// <remarks>Every test owns temporary files, explicit service instances and ambient scopes. No global telemetry binding, real network, application configuration or production database is used.</remarks>
public sealed class LatencyTelemetryReliabilityTests
{
    /// <summary>A full unstarted writer accepts only its bounded capacity; concurrent producers return without filesystem work.</summary>
    /// <returns>A task asserting exact rejected-write accounting and no created storage directory.</returns>
    [Fact]
    public async Task Full_channel_is_nonblocking_and_counts_every_rejected_producer()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service(capacity: 64);
        var observation = Summary();
        for (var index = 0; index < 64; index++) Assert.True(service.TryRecord(observation));
        var producers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++) Assert.False(service.TryRecord(observation));
        }));
        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(64, service.ChannelDepth);
        Assert.Equal(16000, service.DroppedEvents);
        Assert.Equal(0, service.WriterFailures);
        Assert.False(Directory.Exists(service.StorageDirectory));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(16064, service.DroppedEvents);
        Assert.Equal(0, service.ChannelDepth);
    }

    /// <summary>Disabled collection creates no directory or losses even when supplied unsafe observations.</summary>
    /// <returns>A task asserting the true no-writer path across startup, producers and shutdown.</returns>
    [Fact]
    public async Task Disabled_collection_has_no_storage_or_loss_side_effects()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service(enabled: false);
        await service.StartAsync(CancellationToken.None);
        for (var index = 0; index < 1000; index++) Assert.False(service.TryRecord(Summary() with { Operation = "private-body" }));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, service.DroppedEvents);
        Assert.Equal(0, service.ChannelDepth);
        Assert.Equal(0, service.WriterFailures);
        Assert.False(Directory.Exists(service.StorageDirectory));
    }

    /// <summary>Operator contention summaries use individual retry waits and never report caller shutdown as a foreground incident.</summary>
    /// <returns>A task checking actual incident eligibility before and after crossing a measured wait threshold.</returns>
    /// <remarks>Two waits total 200 ms, not 250 ms from summing cumulative gauges. Three caller cancellations
    /// must not produce the repeated-foreground-timeout alert even when another layer retained a timeout label.</remarks>
    [Fact]
    public Task Incident_thresholds_do_not_double_count_cumulative_wait_or_alert_for_caller_shutdown()
    {
        var incidents = new LatencyTelemetryIncidentAggregator(new LatencyTelemetryOptions { SlowOperationMs = 225 });
        incidents.Observe(new LatencyTelemetryEvent { EventType = "sqlite_busy_retry", BotId = "GozargahNetwork_Bot", DurationMs = 50, BusyWaitMs = 50 }, 1000);
        incidents.Observe(new LatencyTelemetryEvent { EventType = "sqlite_busy_retry", BotId = "GozargahNetwork_Bot", DurationMs = 150, BusyWaitMs = 200 }, 1001);
        for (var index = 0; index < 3; index++)
            incidents.Observe(new LatencyTelemetryEvent { EventType = "telegram_foreground_request_completed", BotId = "GozargahNetwork_Bot",
                Outcome = "timeout", TimeoutCategory = "foreground", CancellationSource = "caller" }, 1002 + index);
        Assert.DoesNotContain(incidents.Evaluate(new LatencyTelemetryEvent(), 1005),
            item => item.Category is "sqlite_busy" or "foreground_timeouts");
        incidents.Observe(new LatencyTelemetryEvent { EventType = "sqlite_busy_retry", BotId = "GozargahNetwork_Bot", DurationMs = 50, BusyWaitMs = 50 }, 1006);
        var summary = Assert.Single(incidents.Evaluate(new LatencyTelemetryEvent(), 1007), item => item.Category == "sqlite_busy");
        Assert.Equal(3, summary.IncidentCount);
        Assert.DoesNotContain(incidents.Evaluate(new LatencyTelemetryEvent(), 1008), item => item.Category == "sqlite_busy");
        return Task.CompletedTask;
    }

    /// <summary>A queue/admission regression is a real end-to-end P95 incident even when handlers themselves remain fast.</summary>
    /// <returns>A task verifying eligible consecutive windows using application latency rather than handler-only time.</returns>
    /// <remarks>Both windows have twenty observed receiver-origin updates. Recovered missing-origin handlers are not mixed into this baseline.</remarks>
    [Fact]
    public Task P95_incident_includes_queue_and_admission_time_not_only_handler_duration()
    {
        var incidents = new LatencyTelemetryIncidentAggregator(new LatencyTelemetryOptions());
        for (var index = 0; index < 20; index++)
            incidents.Observe(new LatencyTelemetryEvent { EventType = "telegram_update_completed", BotId = "GozargahNetwork_Bot",
                ApplicationMs = 1000, HandlerMs = 100 }, 1000 + index);
        for (var index = 0; index < 20; index++)
            incidents.Observe(new LatencyTelemetryEvent { EventType = "telegram_update_completed", BotId = "GozargahNetwork_Bot",
                ApplicationMs = 10000, HandlerMs = 100 }, 301001 + index);
        var summary = Assert.Single(incidents.Evaluate(new LatencyTelemetryEvent(), 301030), item => item.Category == "p95_regression");
        Assert.True(summary.P95Ms >= 10000);
        Assert.True(summary.BaselineP95Ms < summary.P95Ms);
        return Task.CompletedTask;
    }

    /// <summary>A successful HTTP status with an invalid Telegram API envelope is a degraded poll, not an idle healthy receiver.</summary>
    /// <returns>A task verifying the actual pinned SDK rejects the response and polling health records failure only.</returns>
    [Fact]
    public async Task Http_200_with_invalid_sdk_envelope_does_not_mark_polling_healthy()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var polling = new TelegramPollingTelemetryTracker(service);
        polling.Started("GozargahNetwork_Bot");
        using var terminal = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"private-response-body\"}", Encoding.UTF8, "application/json") }));
        using var http = Http(service, "GozargahNetwork_Bot", terminal);
        var sdk = Sdk(service, "GozargahNetwork_Bot", http, polling);
        await Assert.ThrowsAsync<RequestException>(() => sdk.GetUpdates(timeout: 50));
        await service.StartAsync(default);
        await service.StopAsync(default);
        var events = ReadEvents(files.TelemetryDirectory);
        var failure = Assert.Single(events, item => item.EventType == "telegram_poll_failed");
        Assert.Equal(1, failure.ConsecutiveFailures);
        Assert.Null(failure.LastSuccessfulPollAtUtc);
        Assert.DoesNotContain(events, item => item.EventType == "telegram_poll_completed");
        Assert.Contains(events, item => item.EventType == "telegram_request_completed" && item.HttpStatusCode == 200);
    }

    /// <summary>An actual file occupying the storage directory cannot fail producers; removing it lets the same writer recover.</summary>
    /// <returns>A task asserting counted I/O faults, successful later persistence and explicit writer health.</returns>
    [Fact]
    public async Task Writer_io_fault_is_isolated_and_same_instance_recovers()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        File.WriteAllText(service.StorageDirectory, "blocked", Encoding.UTF8);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryRecord(Summary() with { UpdateId = 11 }));
        await UntilAsync(() => service.WriterFailures > 0 && service.ChannelDepth == 0);
        Assert.True(service.DroppedEvents > 0);
        File.Delete(service.StorageDirectory);
        await UntilAsync(() => Directory.Exists(service.StorageDirectory));
        var recovered = false;
        await UntilAsync(() =>
        {
            recovered |= service.TryRecord(Summary() with { UpdateId = 22 });
            return recovered && ReadEvents(service.StorageDirectory, allowPartialTail: true).Any(item => item.UpdateId == 22);
        });
        await service.StopAsync(CancellationToken.None);
        var events = ReadEvents(service.StorageDirectory);
        Assert.Contains(events, item => item.UpdateId == 22 && item.Outcome == "completed");
        Assert.Contains(events, item => item.EventType == "telemetry_stopped" && item.WriterFailures > 0 && item.DroppedEvents > 0);
    }

    /// <summary>Graceful shutdown flushes every queued tail record, and a restarted writer never truncates prior files.</summary>
    /// <returns>A task asserting exact update persistence and byte-for-byte preservation after restart.</returns>
    [Fact]
    public async Task Shutdown_flushes_tail_and_restart_preserves_previous_files()
    {
        using var files = new TemporaryFiles();
        using (var first = files.Service())
        {
            for (var index = 1; index <= 40; index++) Assert.True(first.TryRecord(Summary() with { UpdateId = index }));
            await first.StartAsync(CancellationToken.None);
            await first.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, first.DroppedEvents);
            Assert.Equal(0, first.ChannelDepth);
        }
        var before = Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl").ToDictionary(path => path, File.ReadAllBytes);
        using (var second = files.Service())
        {
            Assert.True(second.TryRecord(Summary() with { UpdateId = 41 }));
            await second.StartAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.Equal(Enumerable.Range(1, 41).Select(id => (long?)id), ReadEvents(files.TelemetryDirectory)
            .Where(item => item.EventType == "telegram_update_completed").Select(item => item.UpdateId).Order());
    }

    /// <summary>An already-cancelled stop returns promptly even during a real directory outage; remaining losses eventually settle.</summary>
    /// <returns>A task asserting bounded cancellation, closed admission and released test-owned resources.</returns>
    [Fact]
    public async Task Cancelled_shutdown_is_bounded_and_closes_producer_admission()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        File.WriteAllText(service.StorageDirectory, "blocked", Encoding.UTF8);
        for (var index = 0; index < 64; index++) Assert.True(service.TryRecord(Summary()));
        await service.StartAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await service.StopAsync(cancelled.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.TryRecord(Summary()));
        await UntilAsync(() => service.ChannelDepth == 0);
        Assert.True(service.DroppedEvents >= 65);
    }

    /// <summary>UTC midnight rotates files even when events carry historical timestamps, and file size never exceeds its configured cap.</summary>
    /// <returns>A task asserting real JSONL file boundaries and complete persisted records.</returns>
    [Fact]
    public async Task Date_and_size_rotation_use_writer_clock_not_event_time()
    {
        using var files = new TemporaryFiles();
        var clock = new ControlledClock();
        var options = Options();
        options.MaxTotalBytes = 16 * 1024 * 1024;
        var writer = new LatencyTelemetryFileWriter(options, files.TelemetryDirectory, clock);
        try
        {
            var observation = Summary() with { TimestampUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            Assert.True(await writer.AppendAsync(observation, CancellationToken.None));
            await writer.FlushAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromDays(1));
            Assert.True(await writer.AppendAsync(observation, CancellationToken.None));
            await writer.FlushAsync(CancellationToken.None);
            Assert.Equal(2, Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl").Length);
            for (var index = 0; index < 1200; index++) Assert.True(await writer.AppendAsync(observation with { UpdateId = index }, CancellationToken.None));
            await writer.CloseAsync(CancellationToken.None);
            var owned = Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl");
            Assert.True(owned.Length > 2);
            Assert.All(owned, path => Assert.InRange(new FileInfo(path).Length, 1, options.MaxFileBytes));
            Assert.Equal(1202, ReadEvents(files.TelemetryDirectory).Count);
        }
        finally { writer.Abort(); }
    }

    /// <summary>Retention deletes only owned expired or oldest over-budget files, never unrelated files or the active writer.</summary>
    /// <returns>A task asserting actual age and byte-limit enforcement using temporary real files.</returns>
    [Fact]
    public async Task Retention_enforces_age_and_total_storage_without_touching_unowned_files()
    {
        using var files = new TemporaryFiles();
        Directory.CreateDirectory(files.TelemetryDirectory);
        var clock = new ControlledClock();
        var options = Options();
        var expired = Path.Combine(files.TelemetryDirectory, "latency-20000101-expired.jsonl");
        var oldest = Path.Combine(files.TelemetryDirectory, "latency-20260101-oldest.jsonl");
        var newest = Path.Combine(files.TelemetryDirectory, "latency-20260102-newest.jsonl");
        var unrelated = Path.Combine(files.TelemetryDirectory, "customer-owned.txt");
        File.WriteAllText(expired, "expired");
        File.SetLastWriteTimeUtc(expired, clock.GetUtcNow().UtcDateTime.AddDays(-15));
        File.WriteAllBytes(oldest, new byte[1024 * 1024]);
        File.WriteAllBytes(newest, new byte[1024 * 1024]);
        File.SetLastWriteTimeUtc(oldest, clock.GetUtcNow().UtcDateTime);
        File.SetLastWriteTimeUtc(newest, clock.GetUtcNow().UtcDateTime);
        File.WriteAllText(unrelated, "do-not-delete");
        var writer = new LatencyTelemetryFileWriter(options, files.TelemetryDirectory, clock);
        try
        {
            Assert.True(await writer.AppendAsync(Summary(), CancellationToken.None));
            await writer.FlushAsync(CancellationToken.None);
            writer.Maintain();
            Assert.False(File.Exists(expired));
            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(newest));
            Assert.Equal("do-not-delete", File.ReadAllText(unrelated));
            Assert.InRange(Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl").Sum(path => new FileInfo(path).Length), 1, options.MaxTotalBytes);
        }
        finally { await writer.CloseAsync(CancellationToken.None); }
    }

    /// <summary>Callback ACK, first failed send, and later successful edit have separate persisted clocks.</summary>
    /// <returns>A task asserting real SDK responses and exact monotonic first-attempt, first-completion and first-success semantics.</returns>
    [Fact]
    public async Task Callback_ack_is_not_visible_response_and_failed_first_attempt_does_not_hide_later_success()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var clock = new ControlledClock();
        using var transport = new ScriptedHandler((request, _) =>
        {
            var method = request.RequestUri!.Segments[^1];
            clock.Advance(TimeSpan.FromMilliseconds(method == "answerCallbackQuery" ? 5 : method == "sendMessage" ? 20 : 30));
            return Task.FromResult(Response(method == "answerCallbackQuery" ? "true" : method == "sendMessage" ? null : MessageJson, method == "sendMessage" ? 400 : null));
        });
        using var http = Http(service, "owned", transport);
        var client = Foreground(service, "owned", http);
        using var scope = Push(service, clock);
        await client.AnswerCallbackQuery("secret-callback");
        Assert.Null(scope.CaptureTelemetry().FirstResponseAttemptMs);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<ApiRequestException>(() => client.SendMessage(7, "secret-customer-content"));
        clock.Advance(TimeSpan.FromMilliseconds(40));
        await client.EditMessageText(7, 17, "secret-edited-content");
        scope.Dispose();
        Assert.True(service.TryRecord(scope.CaptureTelemetry()));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var summary = Assert.Single(ReadEvents(files.TelemetryDirectory), item => item.EventType == "telegram_update_handler_completed");
        Assert.Equal(0d, summary.CallbackAckAttemptMs);
        Assert.Equal(5d, summary.CallbackAckMs);
        Assert.Equal(5d, summary.CallbackAckAcknowledgedMs);
        Assert.Equal(15d, summary.FirstResponseAttemptMs);
        Assert.Equal(35d, summary.FirstResponseCompletedMs);
        Assert.Equal(105d, summary.FirstResponseAcknowledgedMs);
        Assert.Equal(40d, summary.MaxUnattributedGapMs);
        Assert.Equal(3L, summary.TelegramRequestCount);
        Assert.Equal(55d, summary.TotalTelegramMs);
        Assert.Equal(105d, summary.HandlerMs);
    }

    /// <summary>Work preceding a callback ACK cannot be charged to the ACK request duration.</summary>
    /// <returns>A task verifying the request duration separately from handler-relative ACK milestones.</returns>
    /// <remarks>A two-second internal wait followed by a 50 ms ACK reproduces delayed tenant/admin acknowledgement.</remarks>
    [Fact]
    public async Task Delayed_callback_ack_reports_request_duration_not_handler_completion_offset()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var clock = new ControlledClock();
        using var transport = new ScriptedHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return Task.FromResult(Response("true"));
        });
        using var http = Http(service, "owned", transport);
        using var scope = Push(service, clock);
        clock.Advance(TimeSpan.FromMilliseconds(2000));
        await Foreground(service, "owned", http).AnswerCallbackQuery("private-callback");
        scope.Dispose();
        var snapshot = scope.CaptureTelemetry();
        Assert.Equal(50d, snapshot.CallbackAckMs);
        Assert.Equal(2000d, snapshot.CallbackAckAttemptMs);
        Assert.Equal(2050d, snapshot.CallbackAckAcknowledgedMs);
        Assert.Null(snapshot.FirstResponseAttemptMs);
    }

    /// <summary>Fast success remains persisted, and equally slow handlers distinguish Telegram time from a long internal gap.</summary>
    /// <param name="telegramMs">Controlled actual SDK boundary duration in milliseconds.</param>
    /// <param name="gapMs">Controlled uninstrumented handler interval in milliseconds.</param>
    /// <param name="slowest">Expected closed dominant stage or unattributed label.</param>
    /// <returns>A task asserting persisted summary duration and exclusive non-double-counting decomposition.</returns>
    [Theory]
    [InlineData(2, 3, "unattributed")]
    [InlineData(8000, 10, "telegram_send")]
    [InlineData(10, 8000, "unattributed")]
    public async Task Fast_and_slow_successes_preserve_telegram_versus_internal_gap(double telegramMs, double gapMs, string slowest)
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var clock = new ControlledClock();
        using var handler = new ScriptedHandler((_, _) => { clock.Advance(TimeSpan.FromMilliseconds(telegramMs)); return Task.FromResult(Response(MessageJson)); });
        using var http = Http(service, "owned", handler);
        using var scope = Push(service, clock);
        clock.Advance(TimeSpan.FromMilliseconds(gapMs));
        await Foreground(service, "owned", http).SendMessage(7, "private");
        scope.Dispose();
        Assert.True(service.TryRecord(scope.CaptureTelemetry()));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var summary = Assert.Single(ReadEvents(files.TelemetryDirectory), item => item.EventType == "telegram_update_handler_completed");
        Assert.Equal(telegramMs + gapMs, summary.HandlerMs);
        Assert.Equal(telegramMs, summary.TotalTelegramMs);
        Assert.Equal(telegramMs, summary.StageMs["telegram_send"]);
        Assert.Equal(gapMs, summary.UnattributedHandlerMs);
        Assert.Equal(gapMs, summary.MaxUnattributedGapMs);
        Assert.Equal(slowest, summary.SlowestStage);
        Assert.Equal(summary.HandlerMs, summary.StageMs.Values.Sum() + summary.UnattributedHandlerMs);
        Assert.Equal(gapMs, summary.FirstResponseAttemptMs);
        Assert.Equal(telegramMs + gapMs, summary.FirstResponseAcknowledgedMs);
    }

    /// <summary>Nested and overlapping timers do not double-count handler coverage and preserve inclusive diagnostics separately.</summary>
    [Fact]
    public void Nested_and_overlapping_stage_accounting_is_exclusive_and_frozen()
    {
        var clock = new ControlledClock();
        using var scope = Push(null, clock);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        var outer = scope.Measure(TelegramUpdateStage.BusinessProcessing);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var inner = scope.Measure(TelegramUpdateStage.SqliteRead);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        inner.Dispose();
        clock.Advance(TimeSpan.FromMilliseconds(5));
        var overlapping = scope.Measure(TelegramUpdateStage.XuiRead);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        outer.Dispose();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        overlapping.Dispose();
        clock.Advance(TimeSpan.FromMilliseconds(7));
        scope.Dispose();
        clock.Advance(TimeSpan.FromDays(1));
        var summary = scope.CaptureTelemetry();
        Assert.Equal(67d, summary.HandlerMs);
        Assert.Equal(15d, summary.StageMs["business_processing"]);
        Assert.Equal(20d, summary.StageMs["sqlite_read"]);
        Assert.Equal(20d, summary.StageMs["xui_read"]);
        Assert.Equal(45d, summary.InclusiveStageMs["business_processing"]);
        Assert.Equal(12d, summary.UnattributedHandlerMs);
        Assert.Equal(7d, summary.MaxUnattributedGapMs);
        Assert.Equal("xui_read", summary.GapBeforeStage);
        Assert.Equal("handler_end", summary.GapAfterStage);
        Assert.Equal(summary.HandlerMs, summary.StageMs.Values.Sum() + summary.UnattributedHandlerMs);
        Assert.Null(TelegramUpdateLatencyScope.Current);
    }

    /// <summary>Concurrent bot SDK calls retain independent ambient traces, exclusive stages and actual HTTP correlation.</summary>
    /// <returns>A task asserting persisted HTTP/API/foreground correlation for two genuinely overlapping asynchronous calls.</returns>
    [Fact]
    public async Task Multiple_bots_keep_ambient_stage_and_http_trace_isolation()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async Task Run(string bot, int elapsed)
        {
            var clock = new ControlledClock();
            using var handler = new ScriptedHandler(async (_, token) =>
            {
                if (Interlocked.Increment(ref count) == 2) entered.SetResult();
                await release.Task.WaitAsync(token);
                Assert.Equal(bot, TelegramUpdateLatencyScope.Current.BotId);
                clock.Advance(TimeSpan.FromMilliseconds(elapsed));
                return Response(MessageJson);
            });
            using var http = Http(service, bot, handler);
            using var scope = Push(service, clock, bot);
            await Foreground(service, bot, http).SendMessage(7, "private");
            scope.Dispose();
            Assert.True(service.TryRecord(scope.CaptureTelemetry()));
        }
        var tasks = new[] { Run("owned", 10), Run("tenant-297967493", 30) };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(TelegramUpdateLatencyScope.Current);
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var events = ReadEvents(files.TelemetryDirectory);
        foreach (var bot in new[] { "owned", "tenant-297967493" })
        {
            var summary = Assert.Single(events, item => item.BotId == bot && item.EventType == "telegram_update_handler_completed");
            Assert.Equal(bot == "owned" ? 10d : 30d, summary.StageMs["telegram_send"]);
            foreach (var family in new[] { "telegram_request_completed", "telegram_api_request_completed", "telegram_foreground_request_completed" })
            {
                var observation = Assert.Single(events, item => item.BotId == bot && item.EventType == family);
                Assert.Equal(summary.TraceId, observation.TraceId);
                Assert.Equal(summary.UpdateId, observation.UpdateId);
                Assert.Equal(summary.Sequence, observation.Sequence);
            }
        }
        Assert.Null(TelegramUpdateLatencyScope.Current);
    }

    /// <summary>Long healthy empty polls are idle time; only an actual SDK rejection starts degradation and a validated response ends it.</summary>
    /// <returns>A task asserting exact failure/recovery counts, deterministic degraded duration and bot isolation.</returns>
    [Fact]
    public async Task Long_poll_is_healthy_until_actual_sdk_failure_then_recovers()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var clock = new ControlledClock();
        var polling = new TelegramPollingTelemetryTracker(service, clock);
        var generation = polling.Started("owned");
        polling.Started("tenant-297967493");
        var attempts = 0;
        using var handler = new ScriptedHandler((_, _) =>
        {
            attempts++;
            if (attempts == 1) clock.Advance(TimeSpan.FromSeconds(50));
            return Task.FromResult(attempts == 2 ? Response(null, 503) : Response("[]"));
        });
        using var http = Http(service, "owned", handler, clock);
        var sdk = Sdk(service, "owned", http, polling, clock);
        Assert.Empty(await sdk.GetUpdates(timeout: 50));
        await Assert.ThrowsAsync<ApiRequestException>(() => sdk.GetUpdates(timeout: 50));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Empty(await sdk.GetUpdates(timeout: 50));
        polling.Stopped("owned", generation);
        polling.Stopped("tenant-297967493");
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var events = ReadEvents(files.TelemetryDirectory);
        var healthy = Assert.Single(events, item => item.EventType == "telegram_poll_completed" && item.DurationMs == 50000);
        Assert.Equal(0, healthy.ConsecutiveFailures);
        Assert.Null(healthy.DegradedSinceUtc);
        var healthyApi = Assert.Single(events, item => item.EventType == "telegram_api_request_completed" && item.DurationMs == 50000);
        Assert.Equal("polling", healthyApi.Category);
        Assert.Equal("telegram_polling", healthyApi.Stage);
        Assert.DoesNotContain(events, item => item.EventType == "telegram_foreground_request_completed");
        var failure = Assert.Single(events, item => item.EventType == "telegram_poll_failed");
        Assert.Equal(1, failure.ConsecutiveFailures);
        Assert.Equal(503, failure.ApiErrorCode);
        Assert.NotNull(failure.DegradedSinceUtc);
        var recovery = Assert.Single(events, item => item.EventType == "telegram_poll_recovered");
        Assert.Equal(250d, recovery.RecoveryMs);
        var completed = events.Last(item => item.EventType == "telegram_poll_completed");
        Assert.Equal(0, completed.ConsecutiveFailures);
        Assert.Null(completed.DegradedSinceUtc);
        Assert.DoesNotContain(events, item => item.BotId == "tenant-297967493" && item.DegradedSinceUtc != null);
    }

    /// <summary>A real SQLite write lock causes one unchanged busy retry and wait, correlated without static telemetry registration.</summary>
    /// <returns>A task asserting actual persisted mutation, exact attempts and measured existing retry waits.</returns>
    [Fact]
    public async Task Actual_sqlite_busy_lock_preserves_retry_count_wait_and_scoped_correlation()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(files.Root, "busy.db"), Pooling = false, DefaultTimeout = 1 }.ToString();
        using var holder = new SqliteConnection(connectionString);
        holder.Open();
        using (var setup = holder.CreateCommand()) { setup.CommandText = "CREATE TABLE counter(value INTEGER NOT NULL); INSERT INTO counter VALUES(0);"; setup.ExecuteNonQuery(); }
        using var transaction = holder.BeginTransaction();
        using (var locked = holder.CreateCommand()) { locked.Transaction = transaction; locked.CommandText = "UPDATE counter SET value=1"; locked.ExecuteNonQuery(); }
        using var scope = Push(service, TimeProvider.System);
        var attempts = 0;
        var changed = await SqliteOperation.RunAsync(async token =>
        {
            attempts++;
            await using var contender = new SqliteConnection(connectionString);
            await contender.OpenAsync(token);
            using var command = contender.CreateCommand();
            command.CommandText = "UPDATE counter SET value=value+1";
            try { return await command.ExecuteNonQueryAsync(token); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 5)
            {
                transaction.Commit();
                throw;
            }
        }, operationName: "unsafe-customer-token-url");
        Assert.Equal(1, changed);
        Assert.Equal(2, attempts);
        using (var read = holder.CreateCommand()) { read.CommandText = "SELECT value FROM counter"; Assert.Equal(2L, read.ExecuteScalar()); }
        scope.Dispose();
        Assert.True(service.TryRecord(scope.CaptureTelemetry()));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var events = ReadEvents(files.TelemetryDirectory);
        var retry = Assert.Single(events, item => item.EventType == "sqlite_busy_retry");
        var operation = Assert.Single(events, item => item.EventType == "sqlite_operation_completed");
        Assert.Equal(1, operation.BusyRetryCount);
        Assert.Equal(2, operation.Attempt);
        Assert.True(operation.BusyWaitMs >= 50);
        Assert.Equal(retry.DurationMs, operation.BusyWaitMs);
        Assert.Equal("sqlite_local", operation.Operation);
        Assert.Equal(scope.TraceId, operation.TraceId);
        Assert.Equal(5, retry.SqliteErrorCode);
        Assert.Equal("completed", operation.Outcome);
        var summary = Assert.Single(events, item => item.EventType == "telegram_update_handler_completed");
        Assert.Equal(1, summary.BusyRetryCount);
        Assert.Equal(operation.BusyWaitMs, summary.BusyWaitMs);
    }

    /// <summary>Actual SDK error descriptions and request payloads never reach persisted JSON; unsafe explicit metadata is rejected at the writer.</summary>
    /// <returns>A task asserting typed failure categories, numeric status and absence of tokens, URLs, bodies or customer text.</returns>
    [Fact]
    public async Task Persisted_json_contains_only_safe_metadata_after_real_sdk_and_unsafe_input_failures()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        const string secret = "customer-private-body-token-url";
        var attempts = 0;
        using var handler = new ScriptedHandler((_, _) => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"https://private.invalid/bot123456789:TEST_TOKEN/" + secret + "\"}")
            } : Response(MessageJson)));
        using var http = Http(service, "owned", handler);
        using var scope = Push(service, new ControlledClock());
        await Assert.ThrowsAsync<ApiRequestException>(() => Foreground(service, "owned", http).SendMessage(987654321, secret));
        var delivered = await Foreground(service, "owned", http).SendMessage(987654321, secret);
        Assert.Equal("secret-response-body", delivered.Text);
        scope.Dispose();
        Assert.True(service.TryRecord(scope.CaptureTelemetry()));
        var unsafeDimension = "https://private.invalid/" + secret;
        var malicious = new[]
        {
            Summary() with { EventType = unsafeDimension }, Summary() with { BotId = unsafeDimension },
            Summary() with { TraceId = unsafeDimension }, Summary() with { Stage = unsafeDimension }, Summary() with { Method = unsafeDimension },
            Summary() with { Operation = unsafeDimension }, Summary() with { ExceptionCategory = unsafeDimension },
            Summary() with { StageMs = new Dictionary<string, double> { [unsafeDimension] = 1 } }
        };
        foreach (var observation in malicious) Assert.True(service.TryRecord(observation));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(malicious.Length, service.DroppedEvents);
        var json = string.Join("\n", Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl").Select(File.ReadAllText));
        foreach (var forbidden in new[] { secret, "secret-response-body", "private.invalid", "TEST_TOKEN", "987654321", "description", "https://" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        var persisted = ReadEvents(files.TelemetryDirectory);
        Assert.Contains(persisted, item => item.EventType == "telegram_api_request_completed" && item.Outcome == "completed");
        var api = Assert.Single(persisted, item => item.EventType == "telegram_api_request_completed" && item.Outcome == "failed");
        Assert.Equal("telegram_api_rejection", api.ExceptionCategory);
        Assert.Equal(400, api.ApiErrorCode);
        Assert.Equal(400, api.HttpStatusCode);
        Assert.Equal("failed", api.Outcome);
    }

    /// <summary>Real SDK transport faults use runtime error types, not secret exception text, for persisted diagnoses.</summary>
    /// <param name="failureKind">Controlled typed HTTP failure emitted by the in-memory transport.</param>
    /// <param name="category">Expected compile-time category after SDK wrapping.</param>
    /// <returns>A task asserting safe correlated HTTP/API observations without a fabricated numeric status.</returns>
    [Theory]
    [InlineData("dns", "dns")]
    [InlineData("tls", "tls")]
    [InlineData("http", "transport")]
    public async Task Actual_sdk_transport_failures_preserve_typed_categories_without_sensitive_error_text(string failureKind, string category)
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        const string secret = "https://private.invalid/bot123456789:TEST_TOKEN/customer-body";
        var failure = failureKind switch
        {
            "dns" => new HttpRequestException(HttpRequestError.NameResolutionError, secret),
            "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, secret),
            _ => new HttpRequestException(secret)
        };
        using var handler = new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(failure));
        using var http = Http(service, "owned", handler);
        using var scope = Push(service, new ControlledClock());
        Assert.NotNull(await Record.ExceptionAsync(() => Foreground(service, "owned", http).SendMessage(7, secret)));
        scope.Dispose();
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var events = ReadEvents(files.TelemetryDirectory);
        foreach (var family in new[] { "telegram_request_completed", "telegram_api_request_completed" })
        {
            var observation = Assert.Single(events, item => item.EventType == family);
            Assert.Equal(category, observation.ExceptionCategory);
            Assert.Equal(category, observation.FailureClassification);
            Assert.Null(observation.HttpStatusCode);
            Assert.Null(observation.ApiErrorCode);
            Assert.Equal(scope.TraceId, observation.TraceId);
        }
        var json = string.Join("\n", Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl").Select(File.ReadAllText));
        foreach (var forbidden in new[] { "private.invalid", "TEST_TOKEN", "customer-body", "https://" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
    }
    /// <summary>Actual concurrent SDK and HTTP requests retain the exact admitted endpoint generation and correlation without cross-request ambient leakage.</summary>
    /// <returns>A task verifying both transport boundaries and context restoration using only in-process HTTP.</returns>
    [Fact]
    public async Task Concurrent_endpoint_requests_share_http_sdk_generation_and_isolate_context()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return Response(MessageJson);
        });
        using var http = Http(service, "owned", handler);
        var sdk = Sdk(service, "owned", http);
        var cloud = Task.Run(async () =>
        {
            using var route = TelegramEndpointTelemetryContext.Push(TelegramEndpointType.Cloud, 1, TelegramEndpointMigrationState.Cloud);
            using var scope = Push(service, TimeProvider.System, "owned");
            await sdk.SendMessage(7, "private-cloud-text");
            Assert.Equal("cloud", TelegramEndpointTelemetryContext.Current.EndpointType);
            return scope.TraceId;
        });
        var local = Task.Run(async () =>
        {
            using var route = TelegramEndpointTelemetryContext.Push(TelegramEndpointType.Local, 2, TelegramEndpointMigrationState.Local);
            using var scope = Push(service, TimeProvider.System, "owned");
            await sdk.SendMessage(7, "private-local-text");
            Assert.Equal("local", TelegramEndpointTelemetryContext.Current.EndpointType);
            return scope.TraceId;
        });
        var traces = await Task.WhenAll(cloud, local);
        Assert.Null(TelegramEndpointTelemetryContext.Current);
        await service.StartAsync(default);
        await service.StopAsync(default);
        var events = ReadEvents(files.TelemetryDirectory);
        foreach (var endpoint in new[] { "cloud", "local" })
        {
            var requests = events.Where(item => item.EndpointType == endpoint).ToArray();
            Assert.Equal(2, requests.Length);
            var headers = Assert.Single(requests, item => item.EventType == "telegram_request_completed");
            var validated = Assert.Single(requests, item => item.EventType == "telegram_api_request_completed");
            Assert.Equal(endpoint == "cloud" ? 1L : 2L, headers.EndpointGeneration);
            Assert.Equal(headers.EndpointGeneration, validated.EndpointGeneration);
            Assert.Equal(headers.MigrationState, validated.MigrationState);
            Assert.Equal(headers.TraceId, validated.TraceId);
            Assert.Equal(traces[endpoint == "cloud" ? 0 : 1], validated.TraceId);
            Assert.Equal(headers.UpdateId, validated.UpdateId);
            Assert.Equal(headers.Sequence, validated.Sequence);
            Assert.Equal("sendMessage", validated.Method);
        }
    }

    /// <summary>Nested request route scopes restore their enclosing route and do not leave polling metadata on subsequent callbacks.</summary>
    [Fact]
    public void Endpoint_context_restores_nested_routes_before_receiver_callbacks()
    {
        Assert.Null(TelegramEndpointTelemetryContext.Current);
        using (var cloud = TelegramEndpointTelemetryContext.Push(TelegramEndpointType.Cloud, 1, TelegramEndpointMigrationState.Cloud))
        {
            using (TelegramEndpointTelemetryContext.Push(TelegramEndpointType.Local, 2, TelegramEndpointMigrationState.Local))
                Assert.Equal("local", TelegramEndpointTelemetryContext.Current!.EndpointType);
            Assert.Same(cloud, TelegramEndpointTelemetryContext.Current);
        }
        Assert.Null(TelegramEndpointTelemetryContext.Current);
        Assert.Throws<ArgumentOutOfRangeException>(() => TelegramEndpointTelemetryContext.Push(TelegramEndpointType.Cloud, 0, TelegramEndpointMigrationState.Cloud));
        Assert.Throws<ArgumentOutOfRangeException>(() => TelegramEndpointTelemetryContext.Push((TelegramEndpointType)99, 1, TelegramEndpointMigrationState.Cloud));
        Assert.Throws<ArgumentOutOfRangeException>(() => TelegramEndpointTelemetryContext.Push(TelegramEndpointType.Cloud, 1, (TelegramEndpointMigrationState)99));
        Assert.Null(TelegramEndpointTelemetryContext.Current);
    }

    /// <summary>Endpoint schema projection persists every supported measurement while rejecting arbitrary labels, invalid numbers and non-UTC success timestamps.</summary>
    /// <returns>A task verifying the real version-one writer rejects leakage without recursion or writer failures.</returns>
    /// <remarks>Migration diagnosis must survive file serialization: retaining only an error code while dropping its phase would hide whether admission or an irreversible operation failed.</remarks>
    [Fact]
    public async Task Endpoint_schema_projects_measurements_and_rejects_uncontrolled_state_or_secrets()
    {
        using var files = new TemporaryFiles();
        using var service = files.Service();
        var now = DateTime.UtcNow;
        var safe = new LatencyTelemetryEvent
        {
            EventType = "telegram_endpoint_migration", BotId = "owned", EndpointType = "local",
            EndpointGeneration = 2, MigrationState = "CloudWait", HealthCheckDurationMs = 12.5,
            FailoverTrigger = "automatic_outage", FailoverDurationMs = 100, CloudReuseRemainingMs = 600000,
            LastSuccessUtc = now, Outcome = "uncertain", ConsecutiveFailures = 3,
            Stage = "local_file_mapping", Operation = "migration_admission", FailureClassification = "LOCAL_FILE_MAPPING_MISSING"
        };
        foreach (var family in new[] { "telegram_endpoint_health", "telegram_endpoint_migration", "telegram_endpoint_outage", "telegram_endpoint_recovered" })
            Assert.True(service.TryRecord(safe with { EventType = family }));
        var unsafeRecords = new[]
        {
            safe with { EndpointType = "private-token" }, safe with { MigrationState = "raw_state_private" },
            safe with { FailoverTrigger = "987654321" }, safe with { EndpointGeneration = 0 },
            safe with { HealthCheckDurationMs = double.NaN }, safe with { FailoverDurationMs = double.PositiveInfinity },
            safe with { CloudReuseRemainingMs = -1 }, safe with { LastSuccessUtc = DateTime.SpecifyKind(now, DateTimeKind.Unspecified) },
            safe with { EndpointType = "http://127.0.0.1:8081/bot123:SECRET" }
        };
        foreach (var row in unsafeRecords) Assert.True(service.TryRecord(row));
        await service.StartAsync(default);
        await service.StopAsync(default);
        Assert.Equal(unsafeRecords.Length, service.DroppedEvents);
        Assert.Equal(0, service.WriterFailures);
        var persisted = ReadEvents(files.TelemetryDirectory).Where(row => row.EventType.StartsWith("telegram_endpoint_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, persisted.Length);
        Assert.All(persisted, row =>
        {
            Assert.Equal(1, row.SchemaVersion);
            Assert.Equal(safe.EndpointType, row.EndpointType);
            Assert.Equal(safe.EndpointGeneration, row.EndpointGeneration);
            Assert.Equal(safe.MigrationState, row.MigrationState);
            Assert.Equal(safe.Stage, row.Stage);
            Assert.Equal(safe.Operation, row.Operation);
            Assert.Equal(safe.FailureClassification, row.FailureClassification);
            Assert.Equal(safe.HealthCheckDurationMs, row.HealthCheckDurationMs);
            Assert.Equal(safe.FailoverTrigger, row.FailoverTrigger);
            Assert.Equal(safe.FailoverDurationMs, row.FailoverDurationMs);
            Assert.Equal(safe.CloudReuseRemainingMs, row.CloudReuseRemainingMs);
            Assert.Equal(safe.LastSuccessUtc, row.LastSuccessUtc);
            Assert.Equal(safe.ConsecutiveFailures, row.ConsecutiveFailures);
        });
        var json = string.Join("\n", Directory.GetFiles(files.TelemetryDirectory, "latency-*.jsonl").Select(File.ReadAllText));
        foreach (var forbidden in new[] { "private-token", "raw_state", "987654321", "127.0.0.1", "SECRET", "actorTelegramUserId", "telegramBotId", "token", "url" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
    }


    /// <summary>Creates the accepted safe event used for storage-only tests.</summary>
    /// <returns>A new payload-free summary with a canonical bot and opaque trace.</returns>
    private static LatencyTelemetryEvent Summary() => new() { EventType = "telegram_update_completed", BotId = "owned", TraceId = new string('a', 32), Outcome = "completed" };

    /// <summary>Creates minimum bounded file and channel settings without changing production defaults.</summary>
    /// <returns>Independent validated-range settings for isolated tests.</returns>
    private static LatencyTelemetryOptions Options() => new() { ChannelCapacity = 64, MaxFileBytes = 1024 * 1024, MaxTotalBytes = 2 * 1024 * 1024, FlushIntervalSeconds = 1, ShutdownFlushSeconds = 1 };

    /// <summary>Installs deterministic update-local timing metadata.</summary>
    /// <param name="service">Optional explicit writer; never assigned globally.</param>
    /// <param name="clock">Test monotonic clock or system provider for actual waits.</param>
    /// <param name="bot">Canonical synthetic bot id shared with its SDK transport.</param>
    /// <returns>A disposable isolated scope with a distinct per-bot opaque trace.</returns>
    private static TelegramUpdateLatencyScope Push(LatencyTelemetryService? service, TimeProvider clock, string bot = "owned") =>
        TelegramUpdateLatencyScope.Push(1, bot, 2, TimeSpan.FromDays(1), null, timeProvider: clock, telemetry: service, traceId: UpdateTelemetryTracker.TraceIdentity(bot, 2));

    /// <summary>Builds an actual SDK client over the test's in-memory HTTP pipeline.</summary>
    /// <param name="service">Explicit nonblocking writer.</param>
    /// <param name="bot">Canonical internal bot id, never parsed from the token URL.</param>
    /// <param name="http">Owned per-test HTTP client.</param>
    /// <param name="polling">Optional explicit receiver tracker.</param>
    /// <param name="clock">Optional deterministic diagnostic clock; real SDK and HttpClient cancellation budgets are unchanged.</param>
    /// <returns>The real telemetry SDK subclass with SDK retries disabled.</returns>
    private static TelegramTelemetryBotClient Sdk(LatencyTelemetryService service, string bot, HttpClient http, TelegramPollingTelemetryTracker? polling = null, TimeProvider? clock = null) =>
        new(new TelegramBotClientOptions("123456789:TEST_TOKEN") { RetryCount = 0 }, http, bot, service, polling ?? new TelegramPollingTelemetryTracker(service, clock), clock);

    /// <summary>Wraps the real SDK with unchanged generous foreground budgets for deterministic fake-clock tests.</summary>
    /// <param name="service">Explicit writer.</param>
    /// <param name="bot">Canonical synthetic runtime bot id.</param>
    /// <param name="http">Test-owned real HTTP pipeline.</param>
    /// <returns>The production foreground wrapper, not an ITelegramBotClient mock.</returns>
    private static ForegroundBoundedTelegramBotClient Foreground(LatencyTelemetryService service, string bot, HttpClient http) =>
        new(Sdk(service, bot, http), new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMinutes(1), MediaGroupBudget = TimeSpan.FromMinutes(1) }, service);

    /// <summary>Creates real SDK HTTP correlation over an in-memory response handler.</summary>
    /// <param name="service">Explicit writer.</param>
    /// <param name="bot">Canonical bot id owned by this transport.</param>
    /// <param name="handler">Test-owned scripted terminal handler.</param>
    /// <param name="clock">Optional diagnostic-only deterministic clock, shared with the SDK subclass when requested.</param>
    /// <returns>A client owning only its lightweight correlation wrapper.</returns>
    private static HttpClient Http(LatencyTelemetryService service, string bot, HttpMessageHandler handler, TimeProvider? clock = null) => new(new TelegramTelemetryHttpHandler(bot, service, handler, clock));

    /// <summary>Constructs a Telegram API envelope consumed and validated by the real SDK.</summary>
    /// <param name="result">Raw SDK result JSON, or null for an API rejection.</param>
    /// <param name="error">Optional numeric Telegram API failure code, mirrored by the controlled HTTP status.</param>
    /// <returns>A real SDK-valid successful envelope or non-200 API rejection envelope.</returns>
    private static HttpResponseMessage Response(string? result, int? error = null) => new(error.HasValue ? (HttpStatusCode)error.Value : HttpStatusCode.OK)
    {
        Content = new StringContent(error.HasValue ? $"{{\"ok\":false,\"error_code\":{error},\"description\":\"private-error-body\"}}" : "{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json")
    };

    /// <summary>Minimal valid Telegram message response; customer text is retained only inside the SDK, never telemetry.</summary>
    private const string MessageJson = "{\"message_id\":17,\"date\":1,\"chat\":{\"id\":7,\"type\":\"private\"},\"text\":\"secret-response-body\"}";

    /// <summary>Reads complete actual JSONL records, allowing only a currently unflushed partial final line during recovery observation.</summary>
    /// <param name="directory">Temporary dedicated telemetry directory.</param>
    /// <param name="allowPartialTail">True only while observing a live writer; false makes malformed shutdown output fail the test.</param>
    /// <returns>Deserialized persisted records, empty before storage recovery.</returns>
    private static List<LatencyTelemetryEvent> ReadEvents(string directory, bool allowPartialTail = false)
    {
        var observations = new List<LatencyTelemetryEvent>();
        if (!Directory.Exists(directory)) return observations;
        foreach (var path in Directory.GetFiles(directory, "latency-*.jsonl"))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                try { observations.Add(JsonSerializer.Deserialize<LatencyTelemetryEvent>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!); }
                catch (JsonException) when (allowPartialTail && reader.EndOfStream) { }
            }
        }
        return observations;
    }

    /// <summary>Waits for an observable asynchronous writer condition without asserting latency thresholds on noisy CI.</summary>
    /// <param name="condition">Actual counter or persisted-record predicate.</param>
    /// <returns>A task completing when the condition holds, failing after a generous finite resource deadline.</returns>
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    /// <summary>Owns a unique temporary data root and creates no static application services.</summary>
    private sealed class TemporaryFiles : IDisposable
    {
        /// <summary>Unique absolute scratch root, never a production Data directory.</summary>
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "adminbot-latency-tests-" + Guid.NewGuid().ToString("N"));
        /// <summary>Dedicated telemetry child used by the actual production writer.</summary>
        public string TelemetryDirectory => Path.Combine(Root, "Telemetry");
        /// <summary>Creates the fixture root before tests place files or SQLite locks inside it.</summary>
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        /// <summary>Creates an explicit unstarted production service owned by the calling test.</summary>
        /// <param name="enabled">Collection flag for disabled-path tests.</param>
        /// <param name="capacity">Validated event queue capacity.</param>
        /// <returns>An independent writer whose disposal precedes fixture cleanup.</returns>
        public LatencyTelemetryService Service(bool enabled = true, int capacity = 64)
        {
            var options = Options();
            options.Enabled = enabled;
            options.ChannelCapacity = capacity;
            return new LatencyTelemetryService(options, Root, NullLogger<LatencyTelemetryService>.Instance);
        }
        /// <summary>Deletes only this fixture's temporary files after explicitly owned resources have stopped.</summary>
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    /// <summary>Controls UTC and monotonic durations without sleeping or affecting real foreground deadlines.</summary>
    private sealed class ControlledClock : TimeProvider
    {
        /// <summary>Test-local elapsed ticks; no static clock state crosses fixtures.</summary>
        private long _ticks;
        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        /// <inheritdoc />
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        /// <summary>Advances both diagnostic UTC and monotonic time by an exact duration.</summary>
        /// <param name="duration">Nonnegative controlled elapsed TimeSpan.</param>
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    /// <summary>Provides real SDK HTTP responses and controlled asynchronous boundaries without a network socket.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        /// <summary>Fixture-local HTTP script; request data never crosses into telemetry.</summary>
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;
        /// <summary>Stores a script invoked once per actual SDK HTTP attempt.</summary>
        /// <param name="response">Required asynchronous response producer; exceptions propagate through the actual SDK.</param>
        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _response = response;
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _response(request, cancellationToken);
    }
}
