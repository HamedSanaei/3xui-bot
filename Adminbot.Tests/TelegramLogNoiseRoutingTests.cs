using System.Collections.Concurrent;
using System.Globalization;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>
/// Regression coverage for the operator-channel noise cleanup: the private Telegram logger channel is an actionable
/// incident stream, not a raw telemetry mirror.
/// </summary>
/// <remarks>
/// Production evidence showed the channel carrying three families that are not incidents: per-stage
/// <c>Telegram slow update stage.</c> measurements at three to five seconds, <c>outcome=completed</c> handler
/// diagnostics just above the five-second interactive threshold, and <c>Underfunded tenant storefront customer-attempt
/// alert queued.</c> bookkeeping. The database row for sequence 4311 proves that shape: <c>completed</c>, no failure
/// code, handler duration about 5.38 seconds. Those lines stay fully visible in the daily diagnostic file, the
/// console/structured logger, and the metrics instruments, but they must not reach the private channel.
///
/// These tests never contact Telegram, XUI, Gozargah, or a payment provider. The provider-level test uses an
/// in-process fake sender, and every schedulable threshold is a millisecond value so no test waits for a production
/// timeout.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Verbatim production message shapes that are telemetry or routine bookkeeping and must be withheld from the
    /// operator channel.
    /// </summary>
    /// <remarks>
    /// The values are copied from the reported incident so a future change to the scheduler's rendering, the parsing
    /// helper, or the funding bookkeeping line fails this contract instead of silently reopening the channel flood.
    /// </remarks>
    private static readonly string[] SuppressedProductionMessages =
    [
        "Telegram update handler exceeded the interactive latency threshold. BotId=KingoFilter_Bot Sequence=4311 UpdateId=271122415 HandlerDurationMs=5373.7144 Outcome=completed",
        "Telegram slow update stage. BotId=tenant-7405716665 Sequence=4353 UpdateId=326098444 UpdateType=Message Stage=TelegramSend ElapsedMs=3150.3417",
        "Telegram slow update stage. BotId=tenant-7405716665 Sequence=4354 UpdateId=326098445 UpdateType=Message Stage=TelegramSend ElapsedMs=4931.9286",
        "Telegram update handler exceeded the interactive latency threshold. BotId=tenant-7405716665 Sequence=4354 UpdateId=326098445 HandlerDurationMs=5087.01 Outcome=completed",
        "Underfunded tenant storefront customer-attempt alert queued. tenantBotId=tenant-7405716665 ownerTelegramUserId=7405716665 cooldownMinutes=15",
        "Tenant storefront became underfunded. tenantBotId=tenant-7405716665 ownerTelegramUserId=7405716665 botBalanceToman=0 siteWalletToman=0 minimumSiteWalletToman=500000",
        "Telegram long update handler completed. BotId=test Sequence=1 UpdateId=1 HandlerDurationMs=12000 Outcome=completed"
    ];

    /// <summary>
    /// Every reported production telemetry shape is withheld from the operator channel.
    /// </summary>
    /// <remarks>
    /// A successful five-second handler is a performance observation. Treating it as an incident both misled operators
    /// and buried the genuinely actionable messages that share the same channel.
    /// </remarks>
    [Fact]
    public void Production_shaped_latency_and_funding_telemetry_is_withheld_from_the_operator_channel()
    {
        foreach (var message in SuppressedProductionMessages)
            Assert.True(TelegramLogSuppression.ShouldSuppress(message, null), message);
    }

    /// <summary>
    /// Genuine latency incidents, delivery failures, and financial failures still reach the operator channel.
    /// </summary>
    /// <remarks>
    /// Hardening the routing must leave the channel actionable rather than silent: the live long-handler watchdog, a
    /// handler at or above the ten-second incident threshold, any non-completed outcome, lane-delay proof, the real
    /// foreground delivery budget expiry, funding delivery uncertainty, and payment/XUI/transport failures are all
    /// delivered.
    /// </remarks>
    [Fact]
    public void Actionable_latency_and_failure_signals_remain_operator_visible()
    {
        // The live root-handler watchdog is the actionable alert and must never be hidden.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram update handler running unusually long. BotId=vpnetiranbot Sequence=3545 UpdateId=916840327 " +
            "UpdateType=Message HandlerElapsedMs=10000 ActiveHandlers=1 MaxConcurrency=16", null));

        // A completed handler at or above the ten-second incident threshold stays visible, including when the live
        // watchdog could not fire because the handler finished in the same instant.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 " +
            "HandlerDurationMs=12000 Outcome=completed", null));

        // A failing outcome is never telemetry, whatever its duration.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 " +
            "HandlerDurationMs=5500 Outcome=handler_exception", null));

        // Lane-delay proof with its correlated root blocker remains useful to operators.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram update waited unusually long. BotId=vpnetiranbot TelegramUserId=916840327 WaitingSequence=3546 " +
            "WaitingUpdateId=916840329 WaitingUpdateType=Message QueueWaitMs=83616 PreviousSequence=3545 " +
            "PreviousUpdateId=916840327 PreviousUpdateType=Message PreviousHandlerDurationMs=103780", null));

        // The real foreground delivery budget expiry is an operational signal.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram foreground delivery exceeded its interactive budget; the update was released without resending. " +
            "botId=tenant-7405716665, userId=5, requestKind=SendMessage, budgetSeconds=8", null));

        // Funding delivery uncertainty and worker failure stay visible; only the successful bookkeeping line is noise.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant storefront funding alert outcome became uncertain. tenantBotId=tenant-7405716665 " +
            "kind=customer_attempt ErrorType=TimeoutException", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant storefront funding alert scan failed. ErrorType=SqliteException", null));

        // Business failures are untouched by this policy.
        Assert.False(TelegramLogSuppression.ShouldSuppress("Payment settlement failed", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Tenant XUI creation failed", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram transport_error while sending the navigation menu", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("channel_access_error for the logger channel", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant manual receipt notification entered DeliveryUncertain state", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "ManualReview required for tenant card order 241", null));
    }

    /// <summary>
    /// An unreadable or failing-outcome handler diagnostic is never suppressed.
    /// </summary>
    /// <param name="message">Production-shaped diagnostic whose duration or outcome is ambiguous or not successful.</param>
    /// <remarks>
    /// Suppression must fail open: a missing, malformed, localized, <c>NaN</c>, <c>Infinity</c>, or negative duration
    /// is unreadable evidence, so the event is delivered instead of being hidden by an accidental match.
    /// </remarks>
    [Theory]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs= Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=abc Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=NaN Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=Infinity Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=-1 Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=5,3 Outcome=completed")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=5373.7")]
    [InlineData("Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 HandlerDurationMs=5373.7 Outcome=completed_by_replay")]
    public void Ambiguous_or_failed_handler_diagnostics_fail_open(string message)
        => Assert.False(TelegramLogSuppression.ShouldSuppress(message, null));

    /// <summary>
    /// The telemetry/incident boundary is exactly the live long-handler threshold, and the scheduler shares it.
    /// </summary>
    /// <param name="handlerDurationMs">Completed handler duration in milliseconds.</param>
    /// <param name="suppressed">Whether the operator channel must withhold the diagnostic.</param>
    /// <remarks>
    /// A float boundary is used because the scheduler renders the duration from a <see cref="double"/> measurement, so
    /// a value just below the threshold is telemetry while the threshold itself and anything above it is an incident.
    /// </remarks>
    [Theory]
    [InlineData(9999.9999, true)]
    [InlineData(10000, false)]
    [InlineData(10000.5, false)]
    public void Operator_threshold_boundary_is_exact(double handlerDurationMs, bool suppressed)
    {
        var message =
            "Telegram update handler exceeded the interactive latency threshold. BotId=b Sequence=1 UpdateId=1 " +
            $"HandlerDurationMs={handlerDurationMs.ToString(CultureInfo.InvariantCulture)} Outcome=completed";

        Assert.Equal(suppressed, TelegramLogSuppression.ShouldSuppress(message, null));
    }

    /// <summary>
    /// The channel policy and the live watchdog read the same threshold instead of two drifting copies.
    /// </summary>
    /// <remarks>
    /// The scheduler initializes its long-handler warning threshold from the shared
    /// <see cref="TelegramLogSuppression.LongHandlerOperatorThresholdMilliseconds"/> value, so this asserts the real
    /// production default rather than a duplicate literal.
    /// </remarks>
    [Fact]
    public void Suppression_threshold_is_structurally_tied_to_the_live_long_handler_watchdog()
    {
        Assert.Equal(10_000, TelegramLogSuppression.LongHandlerOperatorThresholdMilliseconds);

        using var databases = new Databases();
        using var scheduler = new TelegramUpdateScheduler(
            databases.Inbox,
            new Executor((item, token) => Task.CompletedTask),
            new AppConfig { TelegramUpdateMaxConcurrency = 1, TelegramUpdateQueueCapacity = 4, TelegramUpdateShutdownDrainSeconds = 1 },
            new DiagnosticLogger<TelegramUpdateScheduler>());

        Assert.Equal(
            TimeSpan.FromMilliseconds(TelegramLogSuppression.LongHandlerOperatorThresholdMilliseconds),
            scheduler.LongHandlerWarningThreshold);
    }

    /// <summary>
    /// The real scheduler emits exactly the message families the channel policy withholds, and its one live warning
    /// survives.
    /// </summary>
    /// <returns>A task completing after two lanes finish and the captured records are classified.</returns>
    /// <remarks>
    /// Unit tests on the policy cannot detect a change to the rendered message shape. This test runs the production
    /// scheduler with millisecond thresholds, drives one slow closed-vocabulary stage, produces one completed handler
    /// between the interactive and incident thresholds, and produces one handler above the incident threshold whose
    /// live warning must stay channel-visible.
    /// </remarks>
    [Fact]
    public async Task Scheduler_emits_exactly_the_families_the_channel_policy_withholds()
    {
        using var databases = new Databases();
        var logs = new DiagnosticLogger<TelegramUpdateScheduler>();
        var executor = new Executor(async (item, token) =>
        {
            // Lane A: one stage above the slow-stage threshold inside a handler that stays far below the incident
            // threshold, so the handler produces telemetry only. The real work is deliberately tiny so a loaded test
            // machine cannot push it over the incident threshold and change its classification.
            if (item.Update.Id == 1)
            {
                var scope = TelegramUpdateLatencyScope.Current!;
                using (scope.Measure(TelegramUpdateStage.XuiRead))
                    await Task.Delay(TimeSpan.FromMilliseconds(40), token);
                return;
            }

            // Lane B: a handler long enough for the live watchdog to fire while it is still running.
            await Task.Delay(TimeSpan.FromMilliseconds(1400), token);
        });
        using var scheduler = new TelegramUpdateScheduler(
            databases.Inbox,
            executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 16, TelegramUpdateShutdownDrainSeconds = 5 },
            logs)
        {
            // The interactive and incident thresholds are kept an order of magnitude apart so the two lanes keep
            // their intended classification even on a heavily loaded machine.
            InteractiveHandlerThreshold = TimeSpan.FromMilliseconds(10),
            LongHandlerWarningThreshold = TimeSpan.FromMilliseconds(900),
            SlowStageThreshold = TimeSpan.FromMilliseconds(10)
        };

        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", Update(1, 7001), default);
            await scheduler.EnqueueAsync("owned", Update(2, 7002), default);
            await Until(() => scheduler.ActiveHandlerCount == 0 && logs.Count(LogLevel.Warning, "handler running unusually long") == 1);
        }
        finally { await scheduler.StopAsync(default); }

        // The three telemetry families the incident reported are produced by real scheduler code.
        Assert.Equal(1, logs.Count(LogLevel.Information, "slow update stage"));
        Assert.Equal(1, logs.Count(LogLevel.Information, "exceeded the interactive latency threshold"));
        Assert.Equal(1, logs.Count(LogLevel.Information, "long update handler completed"));

        // Classifying the real records through the production channel policy keeps the single live warning and
        // withholds every telemetry family.
        var channelVisible = logs.Records
            .Where(record => !TelegramLogSuppression.ShouldSuppress(record.Message, null))
            .Select(record => record.Message)
            .ToList();

        Assert.Contains(channelVisible, message => message.Contains("handler running unusually long", StringComparison.Ordinal));
        Assert.DoesNotContain(channelVisible, message => message.Contains("slow update stage", StringComparison.Ordinal));
        Assert.DoesNotContain(channelVisible, message => message.Contains("exceeded the interactive latency threshold", StringComparison.Ordinal));
        Assert.DoesNotContain(channelVisible, message => message.Contains("long update handler completed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Withholding a message from the Telegram channel does not remove it from the other logging providers.
    /// </summary>
    /// <returns>A task completing after the fake channel sender and the local sink are inspected.</returns>
    /// <remarks>
    /// This is the distinction the incident required: the same five-second completed handler event must still be
    /// written by the daily diagnostic file, the console/structured logger, and the metrics instruments, while the
    /// private channel receives only the genuine lane-delay incident. The channel queue is FIFO, so observing the
    /// incident alone proves the telemetry line was never enqueued for Telegram delivery.
    /// </remarks>
    /// <example>
    /// <code>
    /// localSink: Information|Telegram update handler exceeded ... HandlerDurationMs=5373.7144 Outcome=completed
    ///             Warning|Telegram update waited unusually long. ... QueueWaitMs=83616
    /// channel  : Telegram update waited unusually long. ... QueueWaitMs=83616
    /// </code>
    /// </example>
    [Fact]
    public async Task Suppressed_telemetry_reaches_local_providers_but_not_the_telegram_channel()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = new TelegramLogger(
            "noise-routing-test",
            null,
            new BotRegistry(new ConfigurationBuilder().Build()),
            new BotContextAccessor(),
            "log",
            "backup",
            dispatcher);
        var localSink = new ConcurrentQueue<string>();

        var telemetry = SuppressedProductionMessages[0];
        var incident =
            "Telegram update waited unusually long. BotId=vpnetiranbot TelegramUserId=916840327 WaitingSequence=3546 " +
            "WaitingUpdateId=916840329 QueueWaitMs=83616 PreviousUpdateId=916840327 PreviousHandlerDurationMs=103780";

        // A local provider sees both events at their original levels.
        LogLocally(localSink, LogLevel.Information, telemetry);
        LogLocally(localSink, LogLevel.Warning, incident);

        // The production Telegram logger withholds only the telemetry line.
        logger.LogInformation("{Message}", telemetry);
        logger.LogWarning("{Message}", incident);

        await Until(() => sender.Texts.Count >= 1);
        // Allow any late enqueue to arrive before proving the telemetry line never reached the channel.
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.Equal(new[] { incident }, sender.Texts.ToArray());
        // The local providers kept both events, each at its original severity.
        Assert.Contains(LogLevel.Information + "|" + telemetry, localSink);
        Assert.Contains(LogLevel.Warning + "|" + incident, localSink);
    }

    /// <summary>Records one locally-formatted log line the way a console/file provider would receive it.</summary>
    /// <param name="sink">Shared local capture queue representing the non-Telegram providers.</param>
    /// <param name="level">Level the caller used; recorded so the assertions prove the original level is preserved.</param>
    /// <param name="message">Formatted message text.</param>
    /// <remarks>
    /// Test-only helper that records the level together with the text, because the incident requirement is that the
    /// other providers keep both the message and its severity. It performs no routing of its own, so the Telegram
    /// channel decision stays isolated in <see cref="TelegramLogSuppression"/>.
    /// </remarks>
    private static void LogLocally(ConcurrentQueue<string> sink, LogLevel level, string message)
        => sink.Enqueue(level + "|" + message);

    /// <summary>Fake Telegram log sender that records delivered text instead of contacting Telegram.</summary>
    /// <remarks>Used to prove which events the production Telegram logger actually enqueued for channel delivery.</remarks>
    private sealed class RecordingLogSender : ITelegramLogSender
    {
        /// <summary>Text bodies delivered to the private channel, in delivery order.</summary>
        public ConcurrentQueue<string> Texts { get; } = new();

        /// <inheritdoc />
        public Task SendMessage(string channelId, string message, ParseMode? parseMode, CancellationToken cancellationToken)
        {
            Texts.Enqueue(message);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SendDocument(string channelId, string fileName, Stream content, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
