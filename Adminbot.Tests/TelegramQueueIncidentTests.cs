using System.Data.Common;
using System.Collections.Concurrent;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>Provider-backed dominant-overlap regressions and deterministic bounded operator incident behavior.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>UTC baseline with fractional ticks, exercising SQLite's exact timestamp conversion.</summary>
    private static readonly DateTime QueueEpoch = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    /// <summary>An obvious predecessor is clipped to the victim's acceptance rather than using its whole run.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public Task Dominant_predecessor_identifies_the_obvious_blocker()
        => AssertDominantAsync(1, 6000, (-2000, 6000));

    /// <summary>Several serial and overlapping candidates compete by overlap, not sequence or recency.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public Task Dominant_predecessor_compares_serial_and_overlapping_candidates()
        => AssertDominantAsync(3, 7000, (0, 1000), (1000, 3000), (2000, 9000), (9000, 10000));

    /// <summary>A short early operation must not hide a later long operation.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public Task Dominant_predecessor_selects_long_after_short()
        => AssertDominantAsync(2, 8000, (0, 300), (1000, 9000));

    /// <summary>A completed receipt remains attributable after its payload was erased.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public Task Dominant_predecessor_includes_completed_execution()
        => AssertDominantAsync(1, 5000, (1000, 6000));

    /// <summary>A running predecessor has its comparison endpoint set to the victim's actual start.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public Task Dominant_predecessor_clips_still_running_execution()
        => AssertDominantAsync(2, 8000, (0, 200), (2000, null));

    /// <summary>Touching, reversed, and wholly outside execution intervals do not produce a false blocker.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public Task Dominant_predecessor_returns_null_without_meaningful_overlap()
        => AssertDominantAsync(null, 0, (-2000, 0), (10000, 11000), (3000, 2000));

    /// <summary>The production-shaped 200 ms predecessor cannot beat the dominant blocker in a roughly six-second wait.</summary>
    /// <returns>The provider-backed assertion task, using an actual 6160 ms victim interval.</returns>
    [Fact]
    public async Task Dominant_predecessor_does_not_prefer_200ms_over_six_seconds()
    {
        using var databases = new Databases();
        await using var db = databases.Users.CreateDbContext();
        db.TelegramUpdateInbox.AddRange(
            QueueCandidate(1, QueueEpoch, QueueEpoch.AddMilliseconds(200)),
            QueueCandidate(2, QueueEpoch.AddMilliseconds(200), QueueEpoch.AddMilliseconds(6000)));
        await db.SaveChangesAsync();
        var result = await databases.Inbox.FindPreviousLaneExecutionAsync(
            3, "owned", 711, QueueEpoch, QueueEpoch.AddMilliseconds(6160), default);
        Assert.Equal(2, result!.Sequence);
        Assert.Equal(5800, result.BlockingOverlapMs);
        Assert.Equal(5800, result.HandlerDurationMs);
    }

    /// <summary>Exact equal overlaps choose earlier sequence; a one-tick larger overlap wins even when later.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public async Task Dominant_predecessor_uses_exact_ticks_and_deterministic_ties()
    {
        await AssertDominantAsync(1, 6000, (0, 6000), (2000, 8000));
        using var databases = new Databases();
        await using var db = databases.Users.CreateDbContext();
        db.TelegramUpdateInbox.AddRange(
            QueueCandidate(1, QueueEpoch.AddTicks(1), QueueEpoch.AddSeconds(6)),
            QueueCandidate(2, QueueEpoch, QueueEpoch.AddSeconds(6)));
        await db.SaveChangesAsync();
        var result = await databases.Inbox.FindPreviousLaneExecutionAsync(10, "owned", 711, QueueEpoch, QueueEpoch.AddSeconds(10), default);
        Assert.Equal(2, result!.Sequence);
        Assert.Equal(6000, result.BlockingOverlapMs);
    }

    /// <summary>The same user on another bot, another user on the same bot, and later rows are excluded.</summary>
    /// <returns>The provider-backed assertion task.</returns>
    [Fact]
    public async Task Dominant_predecessor_isolates_lanes_and_requires_earlier_sequence()
    {
        using var databases = new Databases();
        await using var db = databases.Users.CreateDbContext();
        var otherBot = QueueCandidate(1, QueueEpoch, null); otherBot.BotId = "tenant";
        var otherUser = QueueCandidate(2, QueueEpoch, null); otherUser.TelegramUserId = 722;
        var correct = QueueCandidate(3, QueueEpoch, QueueEpoch.AddSeconds(1));
        var later = QueueCandidate(11, QueueEpoch, null);
        var unstarted = QueueCandidate(4, QueueEpoch, null); unstarted.StartedAtUtc = null;
        db.TelegramUpdateInbox.AddRange(otherBot, otherUser, correct, later, unstarted);
        await db.SaveChangesAsync();
        var result = await databases.Inbox.FindPreviousLaneExecutionAsync(10, "owned", 711, QueueEpoch, QueueEpoch.AddSeconds(10), default);
        Assert.Equal(3, result!.Sequence);
        Assert.Null(await databases.Inbox.FindPreviousLaneExecutionAsync(10, "owned", 711, QueueEpoch, QueueEpoch, default));
        Assert.Null(await databases.Inbox.FindPreviousLaneExecutionAsync(10, "owned", 711, QueueEpoch.AddSeconds(1), QueueEpoch, default));
    }

    /// <summary>Seeds exact intervals into real SQLite and asserts the selected metadata and clipping.</summary>
    /// <param name="expectedSequence">Expected predecessor sequence, or null for no positive contribution.</param>
    /// <param name="overlapMs">Expected positive overlap in milliseconds.</param>
    /// <param name="candidates">Earlier executions as start/end milliseconds relative to the victim acceptance; null end is running.</param>
    /// <returns>A task completing after provider translation and durable metadata assertions.</returns>
    /// <remarks>The victim waits ten seconds; private candidate payloads deliberately exist but must remain unchanged by diagnosis.</remarks>
    private static async Task AssertDominantAsync(long? expectedSequence, double overlapMs, params (double Start, double? End)[] candidates)
    {
        using var databases = new Databases();
        await using var db = databases.Users.CreateDbContext();
        for (var i = 0; i < candidates.Length; i++)
            db.TelegramUpdateInbox.Add(QueueCandidate(i + 1, QueueEpoch.AddMilliseconds(candidates[i].Start),
                candidates[i].End is { } end ? QueueEpoch.AddMilliseconds(end) : null));
        await db.SaveChangesAsync();
        var result = await databases.Inbox.FindPreviousLaneExecutionAsync(100, "owned", 711, QueueEpoch, QueueEpoch.AddSeconds(10), default);
        if (expectedSequence == null) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.Equal(expectedSequence.Value, result.Sequence);
            Assert.Equal(overlapMs, result.BlockingOverlapMs);
            var selected = candidates[(int)expectedSequence.Value - 1];
            Assert.Equal(QueueEpoch.AddMilliseconds(selected.Start), result.StartedAtUtc);
            Assert.Equal(selected.End is { } end ? QueueEpoch.AddMilliseconds(end) : (DateTime?)null, result.CompletedAtUtc);
            Assert.Equal(QueueEpoch.AddSeconds(10), result.ObservedAtUtc);
            Assert.Equal(selected.End is { } completed ? Math.Max(0, completed - selected.Start) : 10000 - selected.Start,
                result.HandlerDurationMs);
        }
        Assert.All(await db.TelegramUpdateInbox.AsNoTracking().ToListAsync(), row => Assert.Equal("private-not-for-diagnostics", row.Payload));
    }

    /// <summary>Creates a candidate with explicit identity and private sentinel payload.</summary>
    /// <param name="sequence">Internal candidate sequence and synthetic Telegram update id.</param>
    /// <param name="start">UTC execution start.</param>
    /// <param name="end">Nullable UTC completion.</param>
    /// <returns>A detached entity for the isolated test database.</returns>
    private static TelegramUpdateInboxEntry QueueCandidate(long sequence, DateTime start, DateTime? end) => new()
    {
        Sequence = sequence,
        BotId = "owned",
        TelegramUserId = 711,
        UpdateId = (int)sequence,
        UpdateType = "Message",
        AcceptedAtUtc = start,
        StartedAtUtc = start,
        CompletedAtUtc = end,
        Status = end == null ? "running" : "completed",
        Payload = "private-not-for-diagnostics"
    };

    /// <summary>A burst remains one incident when per-victim attribution changes, with new-count and extrema accuracy.</summary>
    [Fact]
    public void Queue_incident_coalesces_connected_burst_despite_changed_blocker()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        var key = new TelegramUpdateExecutionKey("owned", 711);
        ObserveQueue(incidents, key, 0, 6000, QueueBlocker(1, 200));
        incidents.Flush(QueueEpoch, reports.Add, nowTimestampMs: 0);
        Assert.Equal("first", Assert.Single(reports).Reason);
        ObserveQueue(incidents, key, 1000, 8000, QueueBlocker(2, 6000));
        ObserveQueue(incidents, key, 2000, 7000, QueueBlocker(3, 1000));
        incidents.Flush(QueueEpoch.AddSeconds(9.999), reports.Add, nowTimestampMs: 9999);
        Assert.Single(reports);
        incidents.Flush(QueueEpoch.AddSeconds(10), reports.Add, nowTimestampMs: 10000);
        Assert.Equal(2, reports.Count);
        var report = reports[1];
        Assert.Equal("periodic", report.Reason);
        Assert.Equal(2, report.AffectedCount);
        Assert.Equal(8000, report.MaxQueueWaitMs);
        Assert.Equal("owned", report.BotId);
        Assert.Equal(711, report.TelegramUserId);
        Assert.Equal(2, report.DominantBlocker.Sequence);
        Assert.Equal("CallbackQuery", report.DominantBlocker.UpdateType);
        Assert.Equal(6500, report.DominantBlocker.HandlerDurationMs);
        Assert.Equal(6000, report.DominantBlocker.BlockingOverlapMs);
        Assert.Equal(QueueEpoch.AddSeconds(-7), report.StartedAtUtc);
        Assert.Equal(QueueEpoch.AddSeconds(2), report.EndedAtUtc);
        Assert.Equal(QueueEpoch, report.WindowStartedAtUtc);
        Assert.Equal(QueueEpoch.AddSeconds(10), report.WindowEndedAtUtc);
        incidents.Flush(QueueEpoch.AddSeconds(11), reports.Add, shutdown: true, nowTimestampMs: 11000);
        Assert.Equal(3, reports.Sum(x => x.AffectedCount));
    }

    /// <summary>Disjoint intervals are immediately visible; touching intervals remain connected; expiry is inclusive.</summary>
    [Fact]
    public void Queue_incident_respects_disjoint_touching_and_idle_window_boundaries()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        var key = new TelegramUpdateExecutionKey("owned", 711);
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        incidents.Observe(key, QueueEpoch, QueueEpoch.AddSeconds(1), 1000, null, QueueEpoch, 0);
        incidents.Observe(key, QueueEpoch.AddSeconds(1), QueueEpoch.AddSeconds(2), 1000, null, QueueEpoch.AddSeconds(1), 1000);
        incidents.Observe(key, QueueEpoch.AddSeconds(2).AddTicks(1), QueueEpoch.AddSeconds(3), 1000, null, QueueEpoch.AddSeconds(2), 2000);
        incidents.Flush(QueueEpoch.AddSeconds(2), reports.Add, nowTimestampMs: 2000);
        Assert.Equal(new[] { "first", "disjoint", "first" }, reports.Select(x => x.Reason));
        Assert.Equal(3, reports.Sum(x => x.AffectedCount));
        incidents.Observe(key, QueueEpoch.AddSeconds(2), QueueEpoch.AddSeconds(4), 2000, null, QueueEpoch.AddSeconds(12), 12000);
        incidents.Flush(QueueEpoch.AddSeconds(12), reports.Add, nowTimestampMs: 12000);
        Assert.Equal("first", reports.Last().Reason);
        Assert.Equal(4, reports.Sum(x => x.AffectedCount));
    }

    /// <summary>Bot and user isolation applies to coalescing as well as predecessor queries.</summary>
    [Fact]
    public void Queue_incident_isolates_bots_and_users()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        foreach (var key in new[] { new TelegramUpdateExecutionKey("owned", 711), new("tenant", 711), new("owned", 722) })
        {
            ObserveQueue(incidents, key, 0, 6000, QueueBlocker(key.TelegramUserId, 5000));
            ObserveQueue(incidents, key, 1000, 7000, QueueBlocker(key.TelegramUserId + 1, 6000));
        }
        incidents.Flush(QueueEpoch.AddSeconds(11), reports.Add, shutdown: true, nowTimestampMs: 11000);
        Assert.Equal(6, reports.Count);
        foreach (var group in reports.GroupBy(x => (x.BotId, x.TelegramUserId)))
        {
            Assert.Equal(2, group.Sum(x => x.AffectedCount));
            Assert.Equal(7000, group.Max(x => x.MaxQueueWaitMs));
        }
        Assert.Equal(3, reports.GroupBy(x => (x.BotId, x.TelegramUserId)).Count());
        Assert.Equal(0, incidents.LaneCount);
    }

    /// <summary>Pending observations survive LRU eviction and bounded cross-lane overflow without double counting.</summary>
    [Fact]
    public void Queue_incident_eviction_and_overflow_preserve_pending_counts_and_extrema()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator(capacity: 2);
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        for (var user = 1; user <= 10; user++)
        {
            var key = new TelegramUpdateExecutionKey("owned", user);
            ObserveQueue(incidents, key, 0, 6000 + user, QueueBlocker(user, 5000 + user));
            ObserveQueue(incidents, key, 1000, 7000 + user, QueueBlocker(user + 100, 6000 + user));
            Assert.InRange(incidents.LaneCount, 1, 2);
        }
        incidents.Flush(QueueEpoch.AddSeconds(11), reports.Add, shutdown: true, nowTimestampMs: 11000);
        Assert.Equal(20, reports.Sum(x => x.AffectedCount));
        Assert.Equal(7010, reports.Max(x => x.MaxQueueWaitMs));
        var overflow = Assert.Single(reports, x => x.Overflow);
        Assert.Equal("multiple", overflow.BotId);
        Assert.Equal(0, overflow.TelegramUserId);
        Assert.Equal("overflow", overflow.Reason);
        Assert.Equal(110, overflow.DominantBlocker.Sequence);
        Assert.Equal(6010, overflow.DominantBlocker.BlockingOverlapMs);
        Assert.Equal(0, incidents.LaneCount);
    }

    /// <summary>Expiry flushes pending observations once and forgets inactive lanes without needing more traffic.</summary>
    [Fact]
    public void Queue_incident_expiry_flushes_pending_and_releases_state()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        var key = new TelegramUpdateExecutionKey("owned", 711);
        ObserveQueue(incidents, key, 0, 6000, null);
        ObserveQueue(incidents, key, 1000, 7000, null);
        incidents.Flush(QueueEpoch.AddSeconds(11), reports.Add, nowTimestampMs: 11000);
        Assert.Equal(new[] { "first", "expired" }, reports.Select(x => x.Reason));
        Assert.Equal(2, reports.Sum(x => x.AffectedCount));
        Assert.Equal(0, incidents.LaneCount);
        incidents.Flush(QueueEpoch.AddSeconds(12), reports.Add, nowTimestampMs: 12000);
        Assert.Equal(2, reports.Count);
    }

    /// <summary>Fixed deadlines defeat sliding debounce; thirty-second and doubling escalations are immediately queued.</summary>
    [Fact]
    public void Queue_incident_reports_periodically_and_exposes_severe_escalations()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        var key = new TelegramUpdateExecutionKey("owned", 711);
        ObserveQueue(incidents, key, 0, 20000, null);
        ObserveQueue(incidents, key, 1000, 29999, null);
        ObserveQueue(incidents, key, 2000, 30000, QueueBlocker(2, 29000));
        incidents.Flush(QueueEpoch.AddSeconds(2), reports.Add, nowTimestampMs: 2000);
        Assert.Equal(new[] { "first", "severe" }, reports.Select(x => x.Reason));
        Assert.Equal(2, reports[1].AffectedCount);
        ObserveQueue(incidents, key, 9000, 59999, null);
        incidents.Flush(QueueEpoch.AddSeconds(10), reports.Add, nowTimestampMs: 10000);
        Assert.Equal("periodic", reports.Last().Reason);
        ObserveQueue(incidents, key, 11000, 60000, QueueBlocker(3, 50000));
        incidents.Flush(QueueEpoch.AddSeconds(11), reports.Add, nowTimestampMs: 11000);
        Assert.Equal("severe", reports.Last().Reason);
        Assert.Equal(60000, reports.Last().MaxQueueWaitMs);
        incidents.Flush(QueueEpoch.AddSeconds(11), reports.Add, shutdown: true, nowTimestampMs: 11000);
        Assert.Equal(5, reports.Sum(x => x.AffectedCount));
    }

    /// <summary>Backward wall-clock movement cannot extend the fixed monotonic reporting window.</summary>
    [Fact]
    public void Queue_incident_expiry_uses_monotonic_time_despite_utc_jump()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        var reports = new List<TelegramQueueDelayIncidentSummary>();
        var key = new TelegramUpdateExecutionKey("owned", 711);
        ObserveQueue(incidents, key, 0, 6000, null);
        ObserveQueue(incidents, key, 1000, 7000, null);
        incidents.Flush(QueueEpoch.AddHours(-1), reports.Add, nowTimestampMs: 11000);
        Assert.Equal(2, reports.Sum(x => x.AffectedCount));
        Assert.Equal("expired", reports.Last().Reason);
        Assert.Equal(0, incidents.LaneCount);
    }

    /// <summary>Sink failures are isolated per summary and never stop draining other lane incidents.</summary>
    [Fact]
    public void Queue_incident_sink_exceptions_do_not_discard_other_reports_or_escape()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator();
        ObserveQueue(incidents, new("owned", 1), 0, 6000, null);
        ObserveQueue(incidents, new("tenant", 2), 0, 7000, null);
        var attempts = new List<TelegramQueueDelayIncidentSummary>();
        incidents.Flush(QueueEpoch, report => { attempts.Add(report); throw new InvalidOperationException("sink unavailable"); }, nowTimestampMs: 0);
        Assert.Equal(new[] { "owned", "tenant" }, attempts.Select(x => x.BotId));
        var afterFailure = new List<TelegramQueueDelayIncidentSummary>();
        ObserveQueue(incidents, new("owned", 1), 1000, 8000, null);
        incidents.Flush(QueueEpoch.AddSeconds(1), afterFailure.Add, shutdown: true, nowTimestampMs: 1000);
        Assert.Equal(1, Assert.Single(afterFailure).AffectedCount);
        Assert.Equal(8000, afterFailure[0].MaxQueueWaitMs);
    }

    /// <summary>Concurrent lane workers conserve every observed victim even while the coordinator drains.</summary>
    /// <returns>A task completing after concurrent observations and coordinator-style draining finish.</returns>
    [Fact]
    public async Task Queue_incident_concurrent_observation_and_flushing_conserve_counts()
    {
        var incidents = new TelegramQueueDelayIncidentAggregator(capacity: 8);
        var reports = new ConcurrentQueue<TelegramQueueDelayIncidentSummary>();
        var observation = Task.WhenAll(Enumerable.Range(1, 16).Select(user => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) ObserveQueue(incidents, new("owned", user), 0, 6000 + i, null);
        })));
        while (!observation.IsCompleted)
        {
            incidents.Flush(QueueEpoch, reports.Enqueue, nowTimestampMs: 0);
            await Task.Yield();
        }
        await observation;
        incidents.Flush(QueueEpoch.AddSeconds(1), reports.Enqueue, shutdown: true, nowTimestampMs: 1000);
        Assert.Equal(800, reports.Sum(x => x.AffectedCount));
        Assert.Equal(6049, reports.Max(x => x.MaxQueueWaitMs));
        Assert.Equal(0, incidents.LaneCount);
    }

    /// <summary>Queued operator delivery wakes the coordinator while a victim handler is still running.</summary>
    /// <returns>A task completing after immediate summary delivery and durable handler completion.</returns>
    [Fact]
    public async Task Queue_incident_first_report_does_not_wait_for_handler_completion()
    {
        using var databases = new Databases();
        await databases.Inbox.TryAcceptAsync("owned", Update(1, 711), 10, default);
        await using (var db = databases.Users.CreateDbContext())
            await db.TelegramUpdateInbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.AcceptedAtUtc, DateTime.UtcNow.AddSeconds(-6)));
        var entered = Signal();
        var release = Signal();
        var logs = new DiagnosticLogger<TelegramUpdateScheduler>();
        var executor = new Executor(async (item, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 5 }, logs);
        await scheduler.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => logs.Count(LogLevel.Warning, "queue-delay incident") == 1);
            Assert.Equal(1, scheduler.ActiveHandlerCount);
            Assert.Single(logs.Messages(LogLevel.Warning), message => message.Contains("waited unusually long", StringComparison.Ordinal));
            var summary = Assert.Single(logs.Messages(LogLevel.Warning), message => message.Contains("queue-delay incident", StringComparison.Ordinal));
            Assert.Contains("AffectedCount=1 ", summary, StringComparison.Ordinal);
            Assert.Contains("DominantBlockerSequence=0 ", summary, StringComparison.Ordinal);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal("completed", (await verify.TelegramUpdateInbox.SingleAsync()).Status);
    }

    /// <summary>Failing detail/incident sinks cannot change FIFO execution or erase durable success receipts.</summary>
    /// <returns>A task completing after both real inbox claims and terminal outcomes.</returns>
    [Fact]
    public async Task Queue_incident_logger_failures_preserve_fifo_and_durable_success()
    {
        using var databases = new Databases();
        await databases.Inbox.TryAcceptAsync("owned", Update(1, 711), 10, default);
        await databases.Inbox.TryAcceptAsync("owned", Update(2, 711), 10, default);
        await using (var db = databases.Users.CreateDbContext())
            await db.TelegramUpdateInbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.AcceptedAtUtc, DateTime.UtcNow.AddSeconds(-6)));
        var started = new ConcurrentQueue<TelegramUpdateWorkItem>();
        var logs = new QueueFailingLogger();
        var executor = new Executor((item, token) => { started.Enqueue(item); return Task.CompletedTask; });
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 4, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 5 }, logs);
        await scheduler.StartAsync(default);
        try { await Until(() => started.Count == 2); }
        finally { await scheduler.StopAsync(default); }
        Assert.Equal(new[] { 1, 2 }, started.Select(x => x.Update.Id));
        await using var verify = databases.Users.CreateDbContext();
        var rows = await verify.TelegramUpdateInbox.OrderBy(x => x.Sequence).ToListAsync();
        Assert.All(rows, row =>
        {
            Assert.Equal("completed", row.Status);
            Assert.Null(row.FailureCode);
            Assert.Null(row.Payload);
        });
        foreach (var item in started)
            Assert.Equal(rows.Single(x => x.Sequence == item.Sequence).StartedAtUtc, item.StartedAtUtc);
        Assert.Equal(2, logs.DetailsAttempted);
        Assert.Equal(2, logs.SummariesAttempted);
    }

    /// <summary>A cancelled diagnostic read must retain the original terminal cancellation and never enter business execution.</summary>
    /// <returns>A task verifying real SQLite claim, diagnostic cancellation and a payload-free cancelled receipt.</returns>
    /// <remarks>Cancellation is control flow, not an optional attribution failure; swallowing it would permit account/payment effects after cancellation. Existing execution_cancelled receipts are terminal completed_with_review for reconciliation.</remarks>
    [Fact]
    public async Task Queue_diagnostic_read_cancellation_does_not_start_business_effects()
    {
        using var databases = new Databases();
        await databases.Inbox.TryAcceptAsync("owned", Update(1, 711), 10, default);
        await using var db = databases.Users.CreateDbContext();
        await db.TelegramUpdateInbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.AcceptedAtUtc, DateTime.UtcNow.AddSeconds(-6)));
        var cancellation = new QueueReadCancellationInterceptor();
        var factory = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(db.Database.GetConnectionString())
            .AddInterceptors(cancellation).Options);
        var started = new ConcurrentQueue<int>();
        using var scheduler = new TelegramUpdateScheduler(new TelegramUpdateInboxStore(factory),
            new Executor((item, _) => { started.Enqueue(item.Update.Id); return Task.CompletedTask; }),
            new AppConfig { TelegramUpdateMaxConcurrency = 1, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 5 },
            new DiagnosticLogger<TelegramUpdateScheduler>());
        await scheduler.StartAsync(default);
        try { await Until(() => Volatile.Read(ref cancellation.Cancellations) == 1 && scheduler.ActiveHandlerCount == 0); }
        finally { await scheduler.StopAsync(default); }
        Assert.Empty(started);
        var receipt = await db.TelegramUpdateInbox.AsNoTracking().SingleAsync();
        Assert.Equal("completed_with_review", receipt.Status);
        Assert.Equal("execution_cancelled", receipt.FailureCode);
        Assert.Null(receipt.Payload);
    }

    /// <summary>Cancels only the real predecessor metadata read after durable claim, leaving admission and final persistence usable.</summary>
    private sealed class QueueReadCancellationInterceptor : DbCommandInterceptor
    {
        /// <summary>Number of predecessor reads cancelled, used to await the actual cancellation boundary.</summary>
        internal int Cancellations;

        /// <summary>Raises cancellation at the diagnostic query boundary without simulating business execution or persistence.</summary>
        /// <param name="command">EF-built reader command; only the overlap metadata projection is cancelled.</param>
        /// <param name="eventData">Real EF execution context forwarded unchanged for ordinary reads.</param>
        /// <param name="result">Original interception result for admission, claim and completion reads.</param>
        /// <param name="cancellationToken">Scheduler query token, forwarded unchanged when the command is not the diagnostic read.</param>
        /// <returns>The base result for every non-diagnostic read.</returns>
        /// <remarks>The synthetic cancellation tests the existing OperationCanceledException policy even when cancellation originates inside the provider.</remarks>
        /// <exception cref="OperationCanceledException">The real predecessor overlap projection is about to execute.</exception>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"OverlapTicks\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Cancellations);
                throw new OperationCanceledException("Synthetic metadata-read cancellation.");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Throws only for the queue diagnostic families, keeping ordinary scheduler telemetry usable.</summary>
    private sealed class QueueFailingLogger : ILogger<TelegramUpdateScheduler>
    {
        /// <summary>Number of local detail records reaching the failing sink.</summary>
        internal int DetailsAttempted;
        /// <summary>Number of coalesced reports reaching the failing sink.</summary>
        internal int SummariesAttempted;
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("Telegram update waited unusually long.", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref DetailsAttempted);
                throw new InvalidOperationException("detail sink unavailable");
            }
            if (message.StartsWith("Telegram queue-delay incident.", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref SummariesAttempted);
                throw new InvalidOperationException("incident sink unavailable");
            }
        }
    }

    /// <summary>Supplies connected waits with independent deterministic UTC and monotonic clocks.</summary>
    /// <param name="incidents">Isolated aggregator under test.</param>
    /// <param name="key">Exact test bot/actor lane identity.</param>
    /// <param name="timestampMs">Monotonic observation milliseconds; UTC endpoints share the same relative offset.</param>
    /// <param name="waitMs">Exact wait length in milliseconds.</param>
    /// <param name="blocker">Nullable payload-free predecessor metadata.</param>
    private static void ObserveQueue(TelegramQueueDelayIncidentAggregator incidents, TelegramUpdateExecutionKey key,
        long timestampMs, double waitMs, TelegramLaneExecutionSummary? blocker)
    {
        var start = QueueEpoch.AddMilliseconds(timestampMs);
        incidents.Observe(key, start.AddMilliseconds(-waitMs), start, waitMs, blocker!, start, timestampMs);
    }

    /// <summary>Creates safe metadata with a known handler duration larger than its victim-specific overlap.</summary>
    /// <param name="sequence">Internal predecessor sequence and synthetic update id.</param>
    /// <param name="overlapMs">Positive contribution to a victim wait, in milliseconds.</param>
    /// <returns>Metadata-only predecessor fixture.</returns>
    private static TelegramLaneExecutionSummary QueueBlocker(long sequence, double overlapMs) => new()
    {
        Sequence = sequence,
        UpdateId = (int)sequence,
        UpdateType = "CallbackQuery",
        StartedAtUtc = QueueEpoch,
        CompletedAtUtc = QueueEpoch.AddMilliseconds(overlapMs + 500),
        BlockingOverlapMs = overlapMs,
        ObservedAtUtc = QueueEpoch.AddMilliseconds(overlapMs + 500)
    };
}
