using Adminbot.Domain.Logging;
using System.IO;
using System.Net;
using System.Net.Http;
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
    /// Telegram.Bot 19 exposes no callback for a successful empty <c>getUpdates</c> response, so the tracker decays by
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
}
