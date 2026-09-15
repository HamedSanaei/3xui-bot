using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Adminbot.Domain;
using Xunit;

/// <summary>
/// Regression coverage for the interactive Telegram latency work: acknowledgement capability must not be consumed by
/// bulk output, and a full lane must refuse rather than block the producer.
/// </summary>
/// <remarks>
/// Production evidence behind these tests: <c>telegram_callback_ack</c> operations above two seconds, handler warnings
/// above the interactive latency threshold, and degraded polling <c>RequestException</c> bursts. The root shape was that
/// every kind of Telegram output shared one bounded resource, so operator logs, reminder scans, and broadcasts competed
/// with a customer's button tap. These tests pin the separation and the refusal semantics, and they deliberately start no
/// listener, no polling loop, and no Telegram call.
/// </remarks>
public sealed class TelegramRuntimeLatencyIsolationTests
{
    /// <summary>
    /// The three output lanes must hold the documented production bounds, and the acknowledgement lane must have its own
    /// bound rather than inheriting the bulk handoff size.
    /// </summary>
    /// <remarks>
    /// This protects against the original defect returning through configuration drift: if the interactive lane silently
    /// became as large as the background lane, or if acknowledgements started sharing the bulk capacity, bulk traffic
    /// could again occupy the room a customer's tap needs.
    /// </remarks>
    [Fact]
    public void Production_lane_bounds_match_the_documented_values()
    {
        var options = new TelegramPerformanceOptions();

        Assert.Equal(32, options.CriticalLaneCapacity);
        Assert.Equal(256, options.InteractiveLaneCapacity);
        Assert.Equal(1000, options.BackgroundLaneCapacity);
        Assert.Equal(256, options.CallbackAcknowledgementCapacity);
        Assert.Equal(25, options.CallbackAcknowledgementPerSecond);

        options.Validate();
    }

    /// <summary>
    /// Every lane bound must be validated at startup instead of silently clamped.
    /// </summary>
    /// <param name="critical">Candidate critical-lane capacity.</param>
    /// <param name="interactive">Candidate interactive-lane capacity.</param>
    /// <param name="background">Candidate background-lane capacity.</param>
    /// <param name="acknowledgement">Candidate acknowledgement-lane capacity.</param>
    /// <param name="acknowledgementRate">Candidate acknowledgement requests per second.</param>
    /// <remarks>
    /// A zero-capacity lane would disable one class of customer output while the process still reported itself healthy, so
    /// rejecting the configuration is safer than clamping it.
    /// </remarks>
    [Theory]
    [InlineData(0, 256, 1000, 256, 25)]
    [InlineData(32, 0, 1000, 256, 25)]
    [InlineData(32, 256, 0, 256, 25)]
    [InlineData(32, 256, 1000, 0, 25)]
    [InlineData(32, 256, 1000, 8193, 25)]
    [InlineData(32, 256, 1000, 256, 0)]
    [InlineData(32, 256, 1000, 256, 31)]
    public void Invalid_lane_bounds_are_rejected_at_startup(
        int critical,
        int interactive,
        int background,
        int acknowledgement,
        int acknowledgementRate)
    {
        var options = new TelegramPerformanceOptions
        {
            CriticalLaneCapacity = critical,
            InteractiveLaneCapacity = interactive,
            BackgroundLaneCapacity = background,
            CallbackAcknowledgementCapacity = acknowledgement,
            CallbackAcknowledgementPerSecond = acknowledgementRate
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    /// <summary>
    /// The durable job priority must select its own lane, so a caller never has to know about lanes.
    /// </summary>
    /// <remarks>Priority 0 is the existing financial outbox class, 1 is ordinary interactive output, 2 is background.</remarks>
    [Theory]
    [InlineData((int)TelegramWorkPriority.Critical, TelegramOutputLane.Critical)]
    [InlineData((int)TelegramWorkPriority.Normal, TelegramOutputLane.Interactive)]
    [InlineData((int)TelegramWorkPriority.Low, TelegramOutputLane.Background)]
    public void Job_priority_selects_its_output_lane(int priority, TelegramOutputLane expected)
    {
        var queue = new TelegramWorkQueue(new TelegramPerformanceOptions());

        var admission = queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Priority = priority });

        Assert.True(admission.Accepted);
        Assert.Equal(expected, admission.Lane);
        Assert.Empty(admission.RejectionReason);
    }

    /// <summary>
    /// A full lane must refuse immediately with a closed-vocabulary reason instead of waiting.
    /// </summary>
    /// <remarks>
    /// Blocking here would stall the output pump and, through it, the durable scheduling pass that also serves interactive
    /// updates. The refusal is safe because the job's SQLite row stays queued and is offered again later.
    /// </remarks>
    [Fact]
    public void Full_lane_refuses_without_blocking_and_reports_its_reason()
    {
        var queue = new TelegramWorkQueue(new TelegramPerformanceOptions
        {
            CriticalLaneCapacity = 1,
            InteractiveLaneCapacity = 1,
            BackgroundLaneCapacity = 1
        });

        var first = queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Priority = (int)TelegramWorkPriority.Critical });
        var second = queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Priority = (int)TelegramWorkPriority.Critical });

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(TelegramOutputLane.Critical, second.Lane);
        Assert.Equal("lane_full", second.RejectionReason);
        Assert.DoesNotContain("owned", second.RejectionReason, StringComparison.Ordinal);
        Assert.Equal(1, queue.RejectionCount(TelegramOutputLane.Critical));
        Assert.Equal(1, queue.CapacityOf(TelegramOutputLane.Critical));
        Assert.Equal(1, queue.PendingCount(TelegramOutputLane.Critical));
    }

    /// <summary>
    /// A refused job must not be counted as admitted and must not disturb the capacity already in use.
    /// </summary>
    /// <remarks>
    /// This is the invariant that makes refusal safe: the lane holds exactly the accepted jobs, the durable queue still
    /// owns the refused one, and re-offering it after the lane drains succeeds.
    /// </remarks>
    [Fact]
    public async Task Refused_job_is_left_to_the_durable_queue_and_can_be_offered_again()
    {
        var queue = new TelegramWorkQueue(new TelegramPerformanceOptions
        {
            CriticalLaneCapacity = 1,
            InteractiveLaneCapacity = 1,
            BackgroundLaneCapacity = 1
        });
        var refused = new TelegramDeliveryJob { BotId = "owned", Priority = (int)TelegramWorkPriority.Critical };

        Assert.True(queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Priority = (int)TelegramWorkPriority.Critical }).Accepted);
        Assert.False(queue.TryEnqueue(refused).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token))
        {
            Assert.True(await reader.MoveNextAsync());
        }

        Assert.True(queue.TryEnqueue(refused).Accepted);
        Assert.Equal(1, queue.RejectionCount(TelegramOutputLane.Critical));
    }

    /// <summary>
    /// Critical output must be drained before background output even when it arrived later.
    /// </summary>
    /// <remarks>
    /// Financial outbox results are the only critical jobs, and they are rare. Serving them first keeps a payment
    /// confirmation from sitting behind a broadcast that was queued earlier.
    /// </remarks>
    [Fact]
    public async Task Critical_output_is_drained_before_background_output()
    {
        var queue = new TelegramWorkQueue(new TelegramPerformanceOptions());

        Assert.True(queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Kind = "background", Priority = (int)TelegramWorkPriority.Low }).Accepted);
        Assert.True(queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Kind = "critical", Priority = (int)TelegramWorkPriority.Critical }).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("critical", reader.Current.Kind);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("background", reader.Current.Kind);
    }

    /// <summary>
    /// A sustained interactive burst must not starve the background lane permanently.
    /// </summary>
    /// <remarks>
    /// Operator logs and bulk notifications share the background lane. Strict priority would let a busy storefront starve
    /// them indefinitely, so the queue guarantees the background lane a turn after a bounded run of interactive jobs.
    /// </remarks>
    [Fact]
    public async Task Background_output_is_not_starved_by_a_sustained_interactive_burst()
    {
        var queue = new TelegramWorkQueue(new TelegramPerformanceOptions());
        for (var index = 0; index < 40; index++)
            Assert.True(queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Kind = "interactive", Priority = (int)TelegramWorkPriority.Normal }).Accepted);
        Assert.True(queue.TryEnqueue(new TelegramDeliveryJob { BotId = "owned", Kind = "background", Priority = (int)TelegramWorkPriority.Low }).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        var backgroundPosition = -1;
        for (var served = 1; served <= 9; served++)
        {
            Assert.True(await reader.MoveNextAsync());
            if (reader.Current.Kind == "background")
            {
                backgroundPosition = served;
                break;
            }
        }

        Assert.Equal(9, backgroundPosition);
    }

    /// <summary>
    /// Lane capacities must be independent of the bulk handoff size and of each other.
    /// </summary>
    /// <remarks>
    /// This is the regression guard for the reported defect: previously one <c>queueSize</c> governed all output, so any
    /// setting large enough for broadcasts also let broadcasts consume the interactive budget.
    /// </remarks>
    [Fact]
    public void Lane_capacities_are_independent_of_the_bulk_handoff_size()
    {
        var options = new TelegramPerformanceOptions { QueueSize = 1 };
        options.Validate();

        var queue = new TelegramWorkQueue(options);

        Assert.Equal(32, queue.CapacityOf(TelegramOutputLane.Critical));
        Assert.Equal(256, queue.CapacityOf(TelegramOutputLane.Interactive));
        Assert.Equal(1000, queue.CapacityOf(TelegramOutputLane.Background));
    }

    /// <summary>
    /// The four canonical latency instruments must exist under exactly the names an operator was told to query.
    /// </summary>
    /// <remarks>
    /// The names are part of the operator contract: <c>callback_received_ms</c>, <c>callback_ack_ms</c>,
    /// <c>handler_total_ms</c>, and <c>queue_wait_ms</c>. Renaming one would silently break the dashboard that replaced
    /// manual log correlation.
    /// </remarks>
    [Fact]
    public void Canonical_latency_instruments_are_published_under_their_exact_names()
    {
        var published = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, self) =>
        {
            if (string.Equals(instrument.Meter.Name, "Adminbot.TelegramLatency", StringComparison.Ordinal))
            {
                published[instrument.Name] = true;
                self.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        TelegramLatencyMetrics.Record(TelegramLatencyMetrics.CallbackReceivedMs, 1);
        TelegramLatencyMetrics.Record(TelegramLatencyMetrics.CallbackAckMs, 2);
        TelegramLatencyMetrics.Record(TelegramLatencyMetrics.HandlerTotalMs, 3);
        TelegramLatencyMetrics.Record(TelegramLatencyMetrics.QueueWaitMs, 4);

        Assert.Contains("callback_received_ms", published.Keys);
        Assert.Contains("callback_ack_ms", published.Keys);
        Assert.Contains("handler_total_ms", published.Keys);
        Assert.Contains("queue_wait_ms", published.Keys);
    }

    /// <summary>
    /// Instrumentation must never be able to fail the interactive path it observes.
    /// </summary>
    /// <remarks>
    /// A null instrument or an impossible negative duration is ignored instead of throwing, because these recorders sit
    /// inside the callback lane and inside the handler's final persistence step.
    /// </remarks>
    [Fact]
    public void Recording_an_impossible_observation_is_ignored_instead_of_throwing()
    {
        TelegramLatencyMetrics.Record(null, 10);
        TelegramLatencyMetrics.Record(TelegramLatencyMetrics.CallbackAckMs, -1);
    }
}
