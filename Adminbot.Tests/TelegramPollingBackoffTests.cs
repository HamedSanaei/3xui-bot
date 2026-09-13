using Adminbot.Domain;
using Adminbot.Domain.Logging;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;

/// <summary>
/// Focused reliability tests for transient Telegram long-polling backoff in the multi-bot runtime.
/// </summary>
/// <remarks>
/// These tests protect the invariant that a Telegram edge outage such as a burst of <c>502 Bad Gateway</c> responses
/// is treated as transient, backed off independently per bot with bounded jitter, and never turned into a permanent
/// bot failure, a 409 conflict decision, or a Telegram-log feedback loop. They exercise the deterministic seams
/// (<see cref="TelegramPollingBackoffPolicy.CalculateDelay(int, double)"/> and the tracker's injectable clock) so no
/// test depends on real random timing or on waiting for a real multi-second production delay.
/// </remarks>
public sealed class TelegramPollingBackoffTests
{
    /// <summary>
    /// Regression: the first transient failure must produce roughly one second, and each further failure in the same
    /// incident must double the delay until the documented cap.
    /// </summary>
    /// <remarks>
    /// A zero jitter sample is used so the exponential schedule is asserted exactly. This protects against a future
    /// edit silently flattening or unboundedly growing the retry schedule.
    /// </remarks>
    [Fact]
    public void Transient_gateway_backoff_starts_at_one_second_and_doubles_until_the_cap()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), TelegramPollingBackoffPolicy.CalculateDelay(1, 0d));
        Assert.Equal(TimeSpan.FromSeconds(2), TelegramPollingBackoffPolicy.CalculateDelay(2, 0d));
        Assert.Equal(TimeSpan.FromSeconds(4), TelegramPollingBackoffPolicy.CalculateDelay(3, 0d));
        Assert.Equal(TimeSpan.FromSeconds(8), TelegramPollingBackoffPolicy.CalculateDelay(4, 0d));
        Assert.Equal(TimeSpan.FromSeconds(16), TelegramPollingBackoffPolicy.CalculateDelay(5, 0d));
        Assert.Equal(TimeSpan.FromSeconds(30), TelegramPollingBackoffPolicy.CalculateDelay(6, 0d));
    }

    /// <summary>
    /// Regression: the backoff is capped at the configured maximum and never overflows for a very long outage.
    /// </summary>
    [Fact]
    public void Transient_gateway_backoff_is_capped_and_never_overflows()
    {
        foreach (var failures in new[] { 6, 7, 12, 40, 400, int.MaxValue })
        {
            var delay = TelegramPollingBackoffPolicy.CalculateDelay(failures, 0d);
            Assert.Equal(TimeSpan.FromSeconds(TelegramPollingBackoffPolicy.MaximumDelaySeconds), delay);
        }

        // A failure count below one is clamped to the first step instead of producing a zero or negative delay.
        Assert.Equal(TimeSpan.FromSeconds(1), TelegramPollingBackoffPolicy.CalculateDelay(0, 0d));
        Assert.Equal(TimeSpan.FromSeconds(1), TelegramPollingBackoffPolicy.CalculateDelay(-5, 0d));
    }

    /// <summary>
    /// Regression: jitter stays inside the configured ±20% band and can never produce a zero, negative, or hot-loop delay.
    /// </summary>
    [Fact]
    public void Transient_gateway_backoff_jitter_stays_inside_range_and_is_never_negative()
    {
        for (var failures = 1; failures <= 8; failures++)
        {
            var exact = TelegramPollingBackoffPolicy.CalculateDelay(failures, 0d);
            var low = TelegramPollingBackoffPolicy.CalculateDelay(failures, -1d);
            var high = TelegramPollingBackoffPolicy.CalculateDelay(failures, 1d);

            Assert.True(low > TimeSpan.Zero, "A jittered delay must never be zero or negative.");
            Assert.True(low <= exact, "The lowest jitter sample must not increase the delay.");
            Assert.True(high >= exact, "The highest jitter sample must not decrease the delay.");
            Assert.True(high <= TimeSpan.FromSeconds(TelegramPollingBackoffPolicy.MaximumDelaySeconds));
            Assert.True(low.TotalSeconds >= exact.TotalSeconds * (1d - TelegramPollingBackoffPolicy.JitterFraction) - 0.001);
            Assert.True(high.TotalSeconds <= exact.TotalSeconds * (1d + TelegramPollingBackoffPolicy.JitterFraction) + 0.001);
        }

        // The very first step with the most aggressive negative jitter is the worst realistic case.
        Assert.Equal(TimeSpan.FromMilliseconds(800), TelegramPollingBackoffPolicy.CalculateDelay(1, -1d));

        // Out-of-range samples are clamped instead of producing an extreme delay.
        Assert.Equal(TelegramPollingBackoffPolicy.CalculateDelay(3, 1d), TelegramPollingBackoffPolicy.CalculateDelay(3, 5d));
    }

    /// <summary>
    /// Regression: a transient failure on one bot must not change the delay or counters of any other bot.
    /// </summary>
    /// <remarks>
    /// This is the core multi-bot isolation requirement: Tenant bot ids and owned bot ids share one runtime namespace,
    /// so state must be keyed by the internal bot id and never stored in one global counter.
    /// </remarks>
    [Fact]
    public void Transient_failure_state_is_isolated_per_bot_id()
    {
        var tracker = new TelegramPollingBackoffTracker();

        tracker.RegisterTransientFailure("tenant-111-1");
        tracker.RegisterTransientFailure("tenant-111-1");
        tracker.RegisterTransientFailure("tenant-111-1");
        var otherBot = tracker.RegisterTransientFailure("tenant-222-1");

        Assert.True(tracker.TryGetConsecutiveFailures("tenant-111-1", out var ownerBotFailures));
        Assert.Equal(3, ownerBotFailures);

        Assert.True(tracker.TryGetConsecutiveFailures("tenant-222-1", out var otherBotFailures));
        Assert.Equal(1, otherBotFailures);
        Assert.Equal(1, otherBot.ConsecutiveFailures);
        Assert.True(otherBot.Delay < TimeSpan.FromSeconds(2));

        // Bot ids are not case-sensitive; the same bot must not accumulate two independent counters.
        tracker.RegisterTransientFailure("TENANT-222-1");
        Assert.True(tracker.TryGetConsecutiveFailures("tenant-222-1", out var caseInsensitiveFailures));
        Assert.Equal(2, caseInsensitiveFailures);
    }

    /// <summary>
    /// Regression: after a healthy period the next transient failure returns to the first backoff step instead of
    /// staying at the maximum delay for the rest of the process lifetime.
    /// </summary>
    /// <remarks>
    /// Telegram.Bot 22.10.3 only invokes the polling error handler on failure and still exposes no callback for a
    /// successful empty <c>getUpdates</c> response, so the tracker decays by
    /// elapsed time. This test advances an injected clock rather than sleeping.
    /// </remarks>
    [Fact]
    public void Transient_failure_state_resets_after_a_healthy_period()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracker = new TelegramPollingBackoffTracker(() => now, TimeSpan.FromMinutes(1));

        for (var i = 0; i < 8; i++)
            tracker.RegisterTransientFailure("tenant-1-1");

        Assert.True(tracker.TryGetConsecutiveFailures("tenant-1-1", out var saturated));
        Assert.Equal(8, saturated);

        // Still inside the healthy window: the incident continues.
        now = now.AddSeconds(TelegramPollingBackoffPolicy.HealthyResetSeconds - 1);
        Assert.Equal(9, tracker.RegisterTransientFailure("tenant-1-1").ConsecutiveFailures);

        // A gap at least as long as the healthy window proves polling recovered.
        now = now.AddSeconds(TelegramPollingBackoffPolicy.HealthyResetSeconds + 1);
        var afterRecovery = tracker.RegisterTransientFailure("tenant-1-1");
        Assert.Equal(1, afterRecovery.ConsecutiveFailures);
        Assert.True(afterRecovery.IsNewIncident);

        // The tracker uses the production jitter source, so the first step is the base delay plus at most ±20%.
        Assert.InRange(
            afterRecovery.Delay.TotalSeconds,
            TelegramPollingBackoffPolicy.BaseDelaySeconds * (1d - TelegramPollingBackoffPolicy.JitterFraction) - 0.001,
            TelegramPollingBackoffPolicy.BaseDelaySeconds * (1d + TelegramPollingBackoffPolicy.JitterFraction) + 0.001);
    }

    /// <summary>
    /// Regression: repeated transient failures inside one incident must not each request an operational log line.
    /// </summary>
    /// <remarks>
    /// The production log flood was one warning/console line per failed getUpdates per bot. The tracker must expose a
    /// rate-limited logging decision so a burst produces at most one summary line per window.
    /// </remarks>
    [Fact]
    public void Repeated_transient_failures_do_not_request_one_operational_log_each()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracker = new TelegramPollingBackoffTracker(() => now, TimeSpan.FromMinutes(1));

        var operationalLogs = 0;
        for (var i = 0; i < 20; i++)
        {
            if (tracker.RegisterTransientFailure("tenant-5-1").ShouldLogOperational)
                operationalLogs++;
        }

        Assert.Equal(1, operationalLogs);

        // A later window emits one more compact summary instead of staying silent forever.
        now = now.AddMinutes(2);
        Assert.True(tracker.RegisterTransientFailure("tenant-5-1").ShouldLogOperational);
    }

    /// <summary>
    /// Regression: a 429 keeps using Telegram's Retry-After delay and never uses, or stacks with, the exponential
    /// 5xx backoff.
    /// </summary>
    [Fact]
    public void Rate_limit_uses_retry_after_and_not_the_transient_gateway_backoff()
    {
        var rateLimited = new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 7 });

        Assert.True(TelegramRateLimitPolicy.IsRateLimited(rateLimited));

        var retryAfterDelay = TelegramRateLimitPolicy.GetRetryDelay(rateLimited);
        Assert.Equal(TimeSpan.FromSeconds(8), retryAfterDelay);

        // The two delays are never combined: the effective wait for a 429 is at least Telegram's Retry-After and is
        // strictly larger than a first-step transient gateway delay.
        Assert.True(retryAfterDelay >= TimeSpan.FromSeconds(7));
        Assert.True(retryAfterDelay > TelegramPollingBackoffPolicy.CalculateDelay(1, 0d));

        // A 429 with no RetryAfter still uses the documented default plus buffer.
        var defaultDelay = TelegramRateLimitPolicy.GetRetryDelay(new ApiRequestException("Too Many Requests", 429));
        Assert.Equal(TimeSpan.FromSeconds(6), defaultDelay);
    }

    /// <summary>
    /// Regression: cancelling the bot receiver during a backoff window exits cleanly and quickly.
    /// </summary>
    /// <remarks>
    /// Shutdown during backoff is the normal stop path. It must not surface an exception, mark a bot failed, or make
    /// the test wait for the real delay.
    /// </remarks>
    [Fact]
    public async Task Cancellation_during_backoff_exits_cleanly()
    {
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            var completed = await TelegramPollingBackoffPolicy
                .DelayAsync(TimeSpan.FromSeconds(30), alreadyCancelled.Token);
            Assert.False(completed);
        }

        using (var cts = new CancellationTokenSource())
        {
            var startedAt = DateTime.UtcNow;
            var delay = TelegramPollingBackoffPolicy.DelayAsync(TimeSpan.FromSeconds(30), cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(20));

            Assert.False(await delay);
            Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(5), "Cancellation must end the wait promptly.");
        }

        var finished = await TelegramPollingBackoffPolicy.DelayAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.True(finished);
        Assert.True(await TelegramPollingBackoffPolicy.DelayAsync(TimeSpan.Zero, CancellationToken.None));
    }

    /// <summary>
    /// Regression: the shared classifier recognizes Telegram 5xx gateway responses, timeouts, and transport failures
    /// while leaving permanent failures to their dedicated handling.
    /// </summary>
    /// <remarks>
    /// A Telegram edge 502 can arrive either as an <see cref="ApiRequestException"/> with error code 502 or as a plain
    /// <see cref="RequestException"/> carrying only an HTTP status. Both must be transient, otherwise the failure falls
    /// through to the legacy noisy polling logger instead of the bounded backoff path.
    /// </remarks>
    [Fact]
    public void Transient_classification_covers_gateway_statuses_and_excludes_permanent_failures()
    {
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Bad Gateway", 502)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Service Unavailable", 503)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Gateway Timeout", 504)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Internal Server Error", 500)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Too Many Requests", 429)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new RequestException("Bad Gateway", HttpStatusCode.BadGateway)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new RequestException("Service Unavailable", HttpStatusCode.ServiceUnavailable)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new RequestException("Bad Gateway")));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new RequestException("Request timed out")));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new HttpRequestException("connection reset")));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new IOException("network unreachable")));

        // Permanent and per-user failures keep their dedicated handling and must never be classified as transient.
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Unauthorized", 401)));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Bad Request: chat not found", 400)));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Forbidden: bot was blocked by the user", 403)));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(null));
    }

    /// <summary>
    /// Regression: duplicate-poller and webhook-active 409 conflicts must not be swallowed by the transient gateway
    /// backoff, so they still reach their existing stop and webhook-recovery handling.
    /// </summary>
    /// <remarks>
    /// A real second <c>getUpdates</c> poller is an operator incident that stops the conflicting receiver, and an active
    /// webhook schedules one bot-scoped recovery generation. Treating either as a transient 5xx would hide those
    /// decisions behind silent retries.
    /// </remarks>
    [Fact]
    public void Duplicate_and_webhook_conflicts_are_not_transient_gateway_failures()
    {
        var duplicate = new ApiRequestException("Conflict: terminated by other getUpdates request", 409);
        var webhook = new ApiRequestException("Conflict: can't use getUpdates method while webhook is active", 409);

        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(duplicate));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(webhook));

        // Invalid-token handling also stays distinct from transient transport noise.
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Unauthorized", 401)));
    }

    /// <summary>
    /// Regression: the compact transient-backoff summary must never be forwarded to the private Telegram logger
    /// channel, while genuine polling failures such as an invalid token still are.
    /// </summary>
    /// <remarks>
    /// This is the guard that prevents a Telegram outage feedback loop: polling fails, the failure is logged to
    /// Telegram, that send fails, and more logs are produced. The transient summary stays local; real token and
    /// settlement failures stay visible to operators.
    /// </remarks>
    [Fact]
    public void Transient_polling_summary_is_suppressed_from_the_telegram_log_channel()
    {
        var summary = "Telegram polling degraded. botId=tenant-123 consecutiveFailures=6 delaySeconds=30 errorType=RequestException";
        Assert.True(TelegramLogSuppression.ShouldSuppress(summary, null));
        Assert.True(TelegramLogSuppression.ShouldSuppress("Transient Telegram polling gateway error ignored.", null));

        // A real invalid-token polling failure must still reach the operational channel.
        var invalidToken = new ApiRequestException("Unauthorized", 401);
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram polling error. Telegram API Error: [401] Unauthorized",
            invalidToken));
    }

    /// <summary>
    /// Regression: many bots failing at the same instant must receive non-identical delays that all remain inside the
    /// documented bounds, so the fleet does not retry in lockstep.
    /// </summary>
    [Fact]
    public void Many_bots_receive_non_identical_jittered_delays_within_bounds()
    {
        var delays = new HashSet<double>();
        for (var i = 0; i < 200; i++)
            delays.Add(Math.Round(TelegramPollingBackoffPolicy.CalculateDelay(6).TotalSeconds, 4));

        Assert.True(delays.Count > 1, "Jitter must produce different delays across bots at the same failure count.");
        Assert.All(delays, seconds => Assert.InRange(
            seconds,
            TelegramPollingBackoffPolicy.MaximumDelaySeconds * (1d - TelegramPollingBackoffPolicy.JitterFraction) - 0.001,
            TelegramPollingBackoffPolicy.MaximumDelaySeconds));
    }

    /// <summary>
    /// Regression: tracker state is cleaned up when a receiver lifecycle ends so historical tenant bot ids cannot
    /// accumulate unbounded in-memory state.
    /// </summary>
    [Fact]
    public void Backoff_state_is_removed_with_the_receiver_lifecycle()
    {
        var tracker = new TelegramPollingBackoffTracker();
        tracker.RegisterTransientFailure("tenant-9-1");
        Assert.True(tracker.TryGetConsecutiveFailures("tenant-9-1", out _));

        Assert.True(tracker.Remove("tenant-9-1"));
        Assert.False(tracker.TryGetConsecutiveFailures("tenant-9-1", out _));
        Assert.False(tracker.Remove("tenant-9-1"));

        tracker.RegisterTransientFailure("tenant-9-1");
        tracker.RecordHealthyPolling("tenant-9-1");
        Assert.False(tracker.TryGetConsecutiveFailures("tenant-9-1", out _));

        // Blank or null ids never create state.
        tracker.RegisterTransientFailure(null);
        tracker.RegisterTransientFailure("   ");
        Assert.False(tracker.TryGetConsecutiveFailures(null, out _));
    }

    /// <summary>
    /// Regression: the exact Telegram.Bot 22.10.3 production transport chain must classify as transient.
    /// </summary>
    /// <remarks>
    /// Telegram.Bot 22.10.3 wraps a failed <c>HttpClient.SendAsync</c> as
    /// <c>RequestException("Bot API Service Failure: ...", inner)</c> with a null <c>HttpStatusCode</c>. The previous
    /// classifier returned unconditionally from its <c>RequestException</c> branch without inspecting the inner chain,
    /// so this connection-reset failure fell through to the noisy legacy "Telegram polling error" logger instead of the
    /// bounded per-bot backoff.
    /// </remarks>
    [Fact]
    public void Nested_connection_reset_transport_failure_is_transient()
    {
        var production = ProductionConnectionResetException();

        // Guard the fixture itself so the regression cannot silently stop covering the real production shape.
        Assert.IsType<RequestException>(production);
        Assert.IsType<HttpRequestException>(production.InnerException);
        Assert.IsType<IOException>(production.InnerException!.InnerException);
        Assert.IsType<SocketException>(production.InnerException.InnerException!.InnerException);

        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(production));
    }

    /// <summary>
    /// Regression: a RequestException wrapping a TLS/transport failure is transient, and plain transport exceptions are
    /// transient without any wrapper at all.
    /// </summary>
    /// <remarks>
    /// Classification must be type-driven rather than message-driven, because Telegram.Bot 22.10.3 only nests the real
    /// transport failure inside the wrapper exception.
    /// </remarks>
    [Fact]
    public void Transport_failures_are_transient_inside_and_outside_a_request_exception()
    {
        var tls = new RequestException(
            "Bot API Service Failure: HttpRequestException: The SSL connection could not be established",
            new HttpRequestException("The SSL connection could not be established"));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(tls));

        var ioWrapped = new RequestException(
            "Bot API Service Failure: IOException: Connection reset by peer",
            new IOException("Connection reset by peer"));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(ioWrapped));

        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new HttpRequestException("The SSL connection could not be established")));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new IOException("Connection reset by peer")));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new SocketException((int)SocketError.ConnectionReset)));

        // A transport failure that arrives with no typed inner chain and only its text is still transient.
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new RequestException("Bot API Service Failure: HttpRequestException: connection reset by peer")));
    }

    /// <summary>
    /// Regression: HTTP/API 502, 503 and 504 stay transient whether they arrive as a Telegram error code or as a raw
    /// edge HTTP status, because the Telegram edge can return a status without a parseable JSON body.
    /// </summary>
    [Fact]
    public void Gateway_statuses_remain_transient()
    {
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Bad Gateway", 502)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Service Unavailable", 503)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Gateway Timeout", 504)));

        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new RequestException("Bad Gateway", HttpStatusCode.BadGateway)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new RequestException("Service Unavailable", HttpStatusCode.ServiceUnavailable)));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new RequestException("Gateway Timeout", HttpStatusCode.GatewayTimeout)));
    }

    /// <summary>
    /// Regression: permanent Telegram/API rejections stay permanent even when they nest a transport-looking inner
    /// exception, so the inner-chain inspection added for the transport regression cannot swallow real failures.
    /// </summary>
    /// <remarks>
    /// This protects the invariant that a revoked token, a blocked bot, an invalid request, and a duplicate getUpdates
    /// conflict still reach their dedicated stop, disable, or recovery handling.
    /// </remarks>
    [Fact]
    public void Permanent_api_rejections_are_never_reclassified_as_transient()
    {
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new ApiRequestException("Bad Request: chat not found", 400)));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(new ApiRequestException("Unauthorized", 401)));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new ApiRequestException("Forbidden: bot was blocked by the user", 403)));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new ApiRequestException("Conflict: terminated by other getUpdates request", 409)));

        // Permanent Telegram/API evidence wins over a coincidental nested transport exception.
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new ApiRequestException("Unauthorized", 401, new HttpRequestException("connection reset"))));

        // The same applies to a raw 4xx edge status on a plain RequestException.
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new RequestException("Bad Request", HttpStatusCode.BadRequest, new HttpRequestException("connection reset"))));
        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(
            new RequestException("Conflict", HttpStatusCode.Conflict)));
    }

    /// <summary>
    /// Regression: a 429 keeps its dedicated Telegram <c>RetryAfter</c> policy and is never re-routed through the
    /// exponential gateway backoff.
    /// </summary>
    /// <remarks>
    /// The dispatcher checks <see cref="TelegramRateLimitPolicy.IsRateLimited"/> before the shared classifier, so the two
    /// delays are never combined even though a 429 is also transient for the classifier.
    /// </remarks>
    [Fact]
    public void Rate_limit_keeps_its_dedicated_policy()
    {
        var rateLimited = new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 7 });

        Assert.True(TelegramRateLimitPolicy.IsRateLimited(rateLimited));
        Assert.Equal(TimeSpan.FromSeconds(8), TelegramRateLimitPolicy.GetRetryDelay(rateLimited));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(rateLimited));
    }

    /// <summary>
    /// Regression: the dispatcher must route the nested connection-reset transport failure into the existing bounded
    /// per-bot backoff and must not reach the legacy "Telegram polling error" logger or mark the receiver failed.
    /// </summary>
    /// <returns>A task that completes after one dispatcher backoff step and its assertions.</returns>
    /// <remarks>
    /// The production receiver did not stop, so the handler must return normally after one bounded delay. The scope
    /// factory is a tripwire: the legacy path is the only code that resolves the polling logger from a scope, so a single
    /// scope creation (or any error-level log) means the failure was misrouted back to the noisy path.
    /// </remarks>
    [Fact]
    public async Task Dispatcher_routes_nested_connection_reset_into_bounded_backoff_without_the_legacy_logger()
    {
        const string botId = "tenant-4242-1";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:id"] = "main",
                ["bots:0:username"] = "main_bot",
                ["bots:0:token"] = "10001:" + new string('a', 35),
                ["bots:0:enabled"] = "true",
                ["bots:0:isDefault"] = "true"
            }).Build();

        var registry = new BotRegistry(configuration);
        registry.Upsert(new BotInstance
        {
            Id = botId,
            Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 4242,
            Enabled = true,
            Token = "4242:" + new string('b', 35)
        });

        var statusStore = new BotRuntimeStatusStore();
        var scopeFactory = new RecordingScopeFactory();
        var logger = new RecordingLogger();
        var runtime = new MultiBotHostedService(
            registry,
            new BotClientProvider(registry),
            null,
            scopeFactory,
            new BotContextAccessor(),
            statusStore,
            configuration,
            logger);

        var handler = typeof(MultiBotHostedService).GetMethod(
            "HandleBotPollingErrorAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        var stopwatch = Stopwatch.StartNew();
        await (Task)handler!.Invoke(runtime, new object[] { botId, ProductionConnectionResetException(), CancellationToken.None })!;
        stopwatch.Stop();

        // The legacy full polling-error logger must never run for a transient transport failure.
        Assert.Equal(0, scopeFactory.ScopeCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("Telegram polling error", StringComparison.OrdinalIgnoreCase));

        // The bounded per-bot backoff actually ran: the first step is one second plus/minus the documented jitter, so the
        // handler must neither return instantly nor hot-loop.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(5));

        // Failure state is recorded for this bot only, and the receiver is never marked failed or stopped.
        var tracker = TransientPollingBackoff(runtime);
        Assert.True(tracker.TryGetConsecutiveFailures(botId, out var failures));
        Assert.Equal(1, failures);
        Assert.DoesNotContain(statusStore.GetSnapshots(registry.Bots), snapshot =>
            string.Equals(snapshot.BotId, botId, StringComparison.OrdinalIgnoreCase) &&
            snapshot.Status is "failed" or "startup_failed" or "stopped" or "invalid_token" or "duplicate");

        // The receiver may continue polling afterward: the incident state survives, and the next failure keeps growing the
        // bounded per-bot delay instead of resetting or stopping the receiver.
        var next = tracker.RegisterTransientFailure(botId);
        Assert.Equal(2, next.ConsecutiveFailures);
        Assert.InRange(next.Delay, TimeSpan.FromMilliseconds(1_600), TimeSpan.FromMilliseconds(2_400));
    }

    /// <summary>
    /// Builds the exact transport exception chain Telegram.Bot 22.10.3 raises when TLS is reset mid-request.
    /// </summary>
    /// <returns>
    /// <c>RequestException -&gt; HttpRequestException -&gt; IOException(Connection reset by peer) -&gt;
    /// SocketException(104)</c>, matching the production <c>Bot API Service Failure: HttpRequestException: The SSL
    /// connection could not be established</c> shape.
    /// </returns>
    private static RequestException ProductionConnectionResetException()
    {
        var socket = new SocketException((int)SocketError.ConnectionReset);
        var io = new IOException("Connection reset by peer", socket);
        var transport = new HttpRequestException("The SSL connection could not be established", io);
        return new RequestException(
            "Bot API Service Failure: HttpRequestException: The SSL connection could not be established",
            transport);
    }

    /// <summary>
    /// Reads the dispatcher's per-bot transient backoff tracker for assertions.
    /// </summary>
    /// <param name="runtime">Dispatcher instance whose tracker state is inspected.</param>
    /// <returns>The live tracker owned by the dispatcher instance.</returns>
    private static TelegramPollingBackoffTracker TransientPollingBackoff(MultiBotHostedService runtime)
    {
        var field = typeof(MultiBotHostedService).GetField("_transientPollingBackoff", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TelegramPollingBackoffTracker)field!.GetValue(runtime)!;
    }

    /// <summary>
    /// Tripwire scope factory proving the dispatcher never resolved the legacy polling-error logger for a transient
    /// transport failure.
    /// </summary>
    /// <remarks>
    /// Creating a scope is the first thing the legacy path does, so a non-zero count means the failure was misrouted.
    /// </remarks>
    private sealed class RecordingScopeFactory : IServiceScopeFactory
    {
        /// <summary>Number of scope creations observed; the transient path must keep this at zero.</summary>
        public int ScopeCount { get; private set; }

        /// <summary>Records the call and fails loudly because the legacy logging path must not be reached.</summary>
        /// <returns>The method never returns; the caller is on the wrong classification branch.</returns>
        /// <exception cref="InvalidOperationException">Always thrown to fail the misrouted legacy path.</exception>
        public IServiceScope CreateScope()
        {
            ScopeCount++;
            throw new InvalidOperationException(
                "The legacy polling-error logger must not be used for a transient transport failure.");
        }
    }

    /// <summary>
    /// Captures every structured log entry so a test can prove which dispatcher branch ran.
    /// </summary>
    private sealed class RecordingLogger : ILogger<MultiBotHostedService>
    {
        /// <summary>Captured entries in log order.</summary>
        private readonly List<(LogLevel Level, string Message, Exception? Error)> _entries = new();

        /// <summary>Snapshot copy of the captured entries, safe to read after concurrent writes.</summary>
        public IReadOnlyList<(LogLevel Level, string Message, Exception? Error)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NopDisposable.Instance;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, error), error));
        }
    }

    /// <summary>No-op scope handle returned by <see cref="RecordingLogger"/>.</summary>
    private sealed class NopDisposable : IDisposable
    {
        /// <summary>Shared instance whose disposal has no effect.</summary>
        public static readonly NopDisposable Instance = new();

        /// <inheritdoc />
        public void Dispose() { }
    }
}
