using System.Collections.Concurrent;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Xunit;

/// <summary>Behavioral coverage for safe scheduler failures and token-identity stage attribution.</summary>
/// <remarks>Exercises durable scheduling and the production operator provider using in-process delivery only.</remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Runs real scheduler failures through the operator provider and proves numeric, payload-free diagnostics.</summary>
    /// <param name="code">Telegram response status, or zero to exercise a generic handler exception.</param>
    /// <param name="reason">Expected closed reason; null for the unchanged generic diagnostic.</param>
    /// <param name="noOp">Whether Telegram reports an unchanged message rather than another bad request.</param>
    /// <returns>A task completing after terminal persistence and actual in-process operator delivery.</returns>
    /// <remarks>No Telegram calls occur. Sensitive exception messages must never enter the operator logger, even for a no-op escaping its caller.</remarks>
    [Theory]
    [InlineData(400, "telegram_api_400_other", false)]
    [InlineData(400, "telegram_message_not_modified", true)]
    [InlineData(401, "telegram_api_401", false)]
    [InlineData(403, "telegram_forbidden", false)]
    [InlineData(404, "telegram_api_404", false)]
    [InlineData(409, "telegram_api_409", false)]
    [InlineData(429, "telegram_api_429", false)]
    [InlineData(500, "telegram_api_5xx", false)]
    [InlineData(503, "telegram_api_5xx", false)]
    [InlineData(599, "telegram_api_5xx", false)]
    [InlineData(418, "telegram_api_418", false)]
    [InlineData(0, null, false)]
    public async Task Scheduler_failure_metadata_is_safe_and_operator_visible(int code, string? reason, bool noOp)
    {
        using var databases = new Databases();
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var channel = new TelegramLogger("scheduler-diagnostics", null,
            new BotRegistry(new ConfigurationBuilder().Build()), new BotContextAccessor(),
            "-1001234567890", "-1001234567891", dispatcher);
        var logs = new SchedulerRoutingLogger(channel);
        const string sensitive = "token=123456:secret https://example.test/private callback=secret-message card=1234 payment=private";
        Exception failure = code == 0 ? new InvalidOperationException(sensitive) :
            new ApiRequestException((noOp ? "Bad Request: message is not modified " : "Bad Request: ") + sensitive, code);
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox,
            new Executor((_, _) => Task.FromException(failure)),
            new AppConfig { TelegramUpdateMaxConcurrency = 1, TelegramUpdateQueueCapacity = 4, TelegramUpdateShutdownDrainSeconds = 2 }, logs);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("diagnostic-bot", Update(1, 92001), default);
            await Until(() => sender.Texts.Any(x => x.Contains("FailureCode=execution_failed", StringComparison.Ordinal)));
        }
        finally { await scheduler.StopAsync(default); }

        var record = Assert.Single(logs.Records, x => x.Message.Contains("failed and was released", StringComparison.Ordinal));
        Assert.Null(record.Exception);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal(failure.GetType().Name, record.State["ErrorType"]);
        Assert.Equal("execution_failed", record.State["FailureCode"]);
        if (reason != null)
        {
            Assert.Equal(code, Assert.IsType<int>(record.State["ErrorCode"]));
            Assert.Equal(reason, record.State["ReasonCode"]);
        }
        else Assert.False(record.State.ContainsKey("ErrorCode"));
        var delivered = Assert.Single(sender.Texts, x => x.Contains("failed and was released", StringComparison.Ordinal));
        Assert.Equal(record.Message, delivered);
        Assert.All(sender.Texts, text => Assert.DoesNotContain(sensitive, text, StringComparison.Ordinal));
        Assert.DoesNotContain("secret", delivered, StringComparison.Ordinal);
        await using var db = databases.Users.CreateDbContext();
        var row = await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 1);
        Assert.Equal("execution_failed", row.FailureCode);
        Assert.Null(row.Payload);
    }

    /// <summary>Exercises a blocked GetMe stage through the live watchdog, local telemetry, and real operator routing.</summary>
    /// <returns>A task completing after a controlled probe is released and all diagnostics are observed.</returns>
    /// <remarks>The release barrier keeps the stage active until the watchdog actually fires; short thresholds avoid real network timeouts.</remarks>
    [Fact]
    public async Task Telegram_identity_probe_attributes_live_warning_and_keeps_stage_telemetry_local()
    {
        using var databases = new Databases();
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logs = new SchedulerRoutingLogger(new TelegramLogger("probe-diagnostics", null,
            new BotRegistry(new ConfigurationBuilder().Build()), new BotContextAccessor(),
            "-1001234567890", "-1001234567891", dispatcher));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, new Executor(async (_, token) =>
        {
            using (TelegramUpdateLatencyScope.Current!.Measure(TelegramUpdateStage.TelegramProbe))
                await release.Task.WaitAsync(token);
        }), new AppConfig { TelegramUpdateMaxConcurrency = 1, TelegramUpdateQueueCapacity = 4, TelegramUpdateShutdownDrainSeconds = 2 }, logs)
        { LongHandlerWarningThreshold = TimeSpan.FromMilliseconds(40), SlowStageThreshold = TimeSpan.FromMilliseconds(10) };
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("identity-probe-routing", Update(1, 92002), default);
            await Until(() => sender.Texts.Any(x => x.Contains("running unusually long", StringComparison.Ordinal)));
            release.SetResult();
            await Until(() => logs.Records.Any(x => x.Message.Contains("slow update stage", StringComparison.Ordinal)) && scheduler.ActiveHandlerCount == 0);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        var warning = Assert.Single(sender.Texts, x => x.Contains("running unusually long", StringComparison.Ordinal));
        Assert.Contains("Stage=TelegramProbe", warning, StringComparison.Ordinal);
        var stage = Assert.Single(logs.Records, x => x.Message.Contains("slow update stage", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, stage.Level);
        Assert.Equal("TelegramProbe", stage.State["Stage"]?.ToString());
        Assert.True(Assert.IsType<double>(stage.State["ElapsedMs"]) >= 10);
        Assert.DoesNotContain(sender.Texts, x => x.Contains("slow update stage", StringComparison.Ordinal));
    }

    /// <summary>Separates a live request from whole-handler timing and keeps completed request telemetry local.</summary>
    /// <param name="rejectSecond">Whether the controlled second send finishes with a real Telegram API rejection.</param>
    /// <returns>A task completing after live watchdog delivery, request release, durable completion and operator drain.</returns>
    /// <remarks>
    /// Two sequential requests protect against diagnosing a ten-second handler as one ten-second send. The second
    /// stays active behind a barrier until the watchdog observes it; request text and API descriptions must never leak.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreground_request_watchdog_distinguishes_current_attempt_and_keeps_completions_local(bool rejectSecond)
    {
        using var databases = new Databases();
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logs = new SchedulerRoutingLogger(new TelegramLogger("foreground-request-routing", null,
            new BotRegistry(new ConfigurationBuilder().Build()), new BotContextAccessor(),
            "-1001234567890", "-1001234567891", dispatcher));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ForegroundBoundedTelegramBotClient(
            new WatchdogRequestClient(release, rejectSecond), TelegramForegroundDeliveryPolicy.Production);
        var botId = "request-watchdog-" + Guid.NewGuid().ToString("N");
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, new Executor(async (_, token) =>
        {
            await client.SendMessage(92003, "private-customer-receipt", cancellationToken: token);
            await client.SendMessage(92003, "private-customer-receipt", cancellationToken: token);
        }), new AppConfig { TelegramUpdateMaxConcurrency = 1, TelegramUpdateQueueCapacity = 4, TelegramUpdateShutdownDrainSeconds = 2 }, logs)
        { LongHandlerWarningThreshold = TimeSpan.FromMilliseconds(40), SlowStageThreshold = TimeSpan.FromMilliseconds(10) };
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync(botId, Update(1, 92003), default);
            await Until(() => sender.Texts.Any(x => x.Contains("running unusually long", StringComparison.Ordinal)));
            release.SetResult();
            await Until(() => logs.Records.Count(x => x.Message.StartsWith("Telegram foreground request completed.", StringComparison.Ordinal)) == 2 &&
                scheduler.ActiveHandlerCount == 0);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
        var warning = Assert.Single(logs.Records, x => x.Message.Contains("running unusually long", StringComparison.Ordinal));
        Assert.Equal("TextSend", warning.State["RequestKind"]);
        Assert.Equal("InProgress", warning.State["RequestOutcome"]);
        Assert.Equal(2L, Assert.IsType<long>(warning.State["TelegramRequestCount"]));
        var requestElapsed = Assert.IsType<double>(warning.State["RequestElapsedMs"]);
        Assert.True(requestElapsed > 0);
        Assert.True(Assert.IsType<double>(warning.State["HandlerElapsedMs"]) >= requestElapsed);
        var completions = logs.Records.Where(x => x.Message.StartsWith("Telegram foreground request completed.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(TelegramForegroundRequestOutcome.Completed, completions[0].State["Outcome"]);
        Assert.Equal(rejectSecond ? TelegramForegroundRequestOutcome.TelegramApiError : TelegramForegroundRequestOutcome.Completed,
            completions[1].State["Outcome"]);
        Assert.Equal(rejectSecond ? 403 : (int?)null, completions[1].State["ErrorCode"]);
        Assert.All(completions, record => Assert.Null(record.Exception));
        Assert.DoesNotContain(sender.Texts, text => text.StartsWith("Telegram foreground request completed.", StringComparison.Ordinal));
        Assert.All(logs.Records, record =>
        {
            Assert.DoesNotContain("private-customer-receipt", record.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-api-description", record.Message, StringComparison.Ordinal);
        });
        await using var db = databases.Users.CreateDbContext();
        var row = await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 1);
        Assert.Equal(rejectSecond ? "execution_failed" : null, row.FailureCode);
        Assert.Equal(rejectSecond ? "completed_with_error" : "completed", row.Status);
        Assert.Null(row.Payload);
    }

    /// <summary>Captures original structured local records and forwards the same event into the production operator logger.</summary>
    /// <param name="channel">Required production operator logger receiving the unchanged structured event.</param>
    private sealed class SchedulerRoutingLogger(ILogger channel) : ILogger<TelegramUpdateScheduler>
    {
        /// <summary>Original severity, rendered text, structured metadata and attached exception received by local providers.</summary>
        public ConcurrentQueue<(LogLevel Level, string Message, Dictionary<string, object?> State, Exception? Exception)> Records { get; } = new();
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var metadata = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(x => x.Key, x => x.Value) : new Dictionary<string, object?>();
            Records.Enqueue((logLevel, formatter(state, exception), metadata, exception));
            channel.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>Holds the second real foreground attempt active until the live watchdog has observed it.</summary>
    /// <param name="release">Required test-owned completion barrier, released after operator delivery.</param>
    /// <param name="rejectSecond">True makes the released second send throw an API error with deliberately sensitive text.</param>
    private sealed class WatchdogRequestClient(TaskCompletionSource release, bool rejectSecond) : StorefrontClient
    {
        /// <summary>Number of sequential message attempts observed by this isolated client.</summary>
        private int _attempts;
        /// <inheritdoc />
        public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SendMessageRequest && ++_attempts == 2)
            {
                await release.Task.WaitAsync(cancellationToken);
                if (rejectSecond) throw new ApiRequestException("private-api-description", 403);
            }
            return await base.SendRequest(request, cancellationToken);
        }
    }
}
