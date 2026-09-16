using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Xunit;

/// <summary>
/// Regression tests for Telegram long-polling instability in the multi-bot runtime.
/// </summary>
/// <remarks>
/// Production evidence for this fixture: several unrelated tenant receivers entered
/// <c>"Telegram polling degraded" errorType=RequestException</c> shortly after startup, and the log line carried no
/// status code, no inner exception, and no token or loop identity, so the only actionable cause - one Telegram token
/// being polled by more than one loop - could not be confirmed from the journal.
///
/// These tests protect two invariants:
/// <list type="number">
/// <item>exactly one <c>getUpdates</c> loop may exist per internal bot id and per Telegram token, and a replacement may
/// only begin after its predecessor's loop has actually ended;</item>
/// <item>a polling conflict is identified from the error code <em>or</em> the HTTP status plus Telegram's documented
/// conflict text, is never answered with a retry delay, and is always logged with the identity and status fields an
/// operator needs.</item>
/// </list>
///
/// No test opens a real socket, contacts Telegram, or starts a receiver.
/// </remarks>
public sealed class TelegramPollingSingleFlightTests
{
    /// <summary>
    /// Regression: acquiring the same internal bot twice must be refused, because two loops over one bot token is exactly
    /// what Telegram answers with its 409 conflict.
    /// </summary>
    /// <remarks>
    /// The refusal must also name the holder, so an operator reading the journal can tell which internal bot is already
    /// polling instead of seeing unrelated degraded-polling lines.
    /// </remarks>
    [Fact]
    public void Second_polling_attempt_for_one_bot_is_rejected_and_names_the_holder()
    {
        var registry = new TelegramPollingLeaseRegistry();

        var first = registry.TryAcquire("tenant-8161518157", "abc123", generation: 1, processId: 4242, reason: "startup");
        var second = registry.TryAcquire("tenant-8161518157", "abc123", generation: 2, processId: 4242, reason: "owner_start");

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
        Assert.Equal(TelegramPollingLeaseRejection.BotAlreadyPolling, second.RejectionReason);
        Assert.Equal("tenant-8161518157", second.HeldByBotId);
        Assert.Equal(1, second.HeldByGeneration);
        Assert.Equal(1, registry.ActiveCount);
    }

    /// <summary>
    /// Regression: two different internal bots configured with the same Telegram token must not poll simultaneously.
    /// </summary>
    /// <remarks>
    /// This is the configuration mistake that produced the production report: several storefronts failing at once were
    /// actually one credential being polled twice. Only the first attempt owns the token, and the refusal reports the
    /// first bot's id and token fingerprint prefix so the duplicate configuration is directly findable.
    /// </remarks>
    [Fact]
    public void Two_bots_sharing_one_token_cannot_both_poll_it()
    {
        var registry = new TelegramPollingLeaseRegistry();

        var owner = registry.TryAcquire("VPNetiranAssistantBot", "shared9f", generation: 10, processId: 99, reason: "startup");
        var duplicate = registry.TryAcquire("tenant-7385810997", "shared9f", generation: 11, processId: 99, reason: "startup");

        Assert.True(owner.Acquired);
        Assert.False(duplicate.Acquired);
        Assert.Equal(TelegramPollingLeaseRejection.TokenAlreadyPolling, duplicate.RejectionReason);
        Assert.Equal("VPNetiranAssistantBot", duplicate.HeldByBotId);
        Assert.Equal("shared9f", duplicate.HeldByTokenFingerprint);
        Assert.Equal(1, registry.ActiveCount);
    }

    /// <summary>
    /// Regression: a late predecessor must not be able to release the lease its replacement already owns.
    /// </summary>
    /// <remarks>
    /// Without the generation check, the old loop's shutdown could free the replacement's lease and reopen the
    /// duplicate-poller window while the replacement was still polling.
    /// </remarks>
    [Fact]
    public void Lease_release_is_generation_checked()
    {
        var registry = new TelegramPollingLeaseRegistry();
        registry.TryAcquire("tenant-6459198363", "fp645", generation: 7, processId: 1, reason: "startup");

        // A stale generation (the previous loop exiting late) is refused and leaves the lease intact.
        Assert.Null(registry.Release("tenant-6459198363", generation: 6, reason: "loop_ended"));
        Assert.NotNull(registry.Current("tenant-6459198363"));

        // The owning generation releases it, and the bot can poll again afterwards.
        var released = registry.Release("tenant-6459198363", generation: 7, reason: "loop_ended");
        Assert.NotNull(released);
        Assert.Equal("loop_ended", released!.Reason);
        Assert.True(registry.TryAcquire("tenant-6459198363", "fp645", generation: 8, processId: 1, reason: "restart").Acquired);
    }

    /// <summary>
    /// Regression: a legitimate restart must be able to wait for its predecessor, while a live duplicate stays a hard
    /// rejection.
    /// </summary>
    /// <remarks>
    /// A lease is held for the whole lifetime of its loop, so a restart briefly sees the predecessor's lease. The
    /// stopping mark is the signal that lets the restarter distinguish "leaving getUpdates" from "another live poller",
    /// and only a marked lease may be waited for.
    /// </remarks>
    [Fact]
    public void Stopping_mark_tells_a_restart_apart_from_a_live_duplicate()
    {
        var registry = new TelegramPollingLeaseRegistry();
        registry.TryAcquire("tenant-934171994", "fp934", generation: 20, processId: 5, reason: "startup");

        var liveDuplicate = registry.TryAcquire("tenant-934171994", "fp934", generation: 21, processId: 5, reason: "restart");
        Assert.False(liveDuplicate.Acquired);
        Assert.False(liveDuplicate.Lease!.Stopping);

        // The stop is requested, and the mark is generation-checked like every other mutation.
        Assert.False(registry.MarkStopping("tenant-934171994", generation: 19));
        Assert.True(registry.MarkStopping("tenant-934171994", generation: 20));
        Assert.True(registry.Current("tenant-934171994")!.Stopping);

        var restart = registry.TryAcquire("tenant-934171994", "fp934", generation: 21, processId: 5, reason: "restart");
        Assert.False(restart.Acquired);
        Assert.True(restart.Lease!.Stopping);

        // Only after the predecessor's own release can the replacement own the lease.
        registry.Release("tenant-934171994", generation: 20, reason: "receiver restart");
        Assert.True(registry.TryAcquire("tenant-934171994", "fp934", generation: 21, processId: 5, reason: "restart").Acquired);
        Assert.Single(registry.Snapshot());
    }

    /// <summary>
    /// Regression: an attempt that cannot identify its bot or its credential must be refused rather than leased.
    /// </summary>
    /// <remarks>
    /// A lease that cannot name its own identity cannot prevent a duplicate poller, so a missing id or fingerprint is a
    /// rejection instead of a silently unenforceable grant.
    /// </remarks>
    [Fact]
    public void Missing_identity_is_rejected_instead_of_granted()
    {
        var registry = new TelegramPollingLeaseRegistry();

        Assert.Equal(
            TelegramPollingLeaseRejection.MissingBotId,
            registry.TryAcquire("  ", "fp", generation: 1, processId: 1, reason: "startup").RejectionReason);
        Assert.Equal(
            TelegramPollingLeaseRejection.MissingTokenFingerprint,
            registry.TryAcquire("tenant-1", string.Empty, generation: 1, processId: 1, reason: "startup").RejectionReason);
        Assert.Equal(0, registry.ActiveCount);
        Assert.Null(registry.Current("tenant-1"));
    }

    /// <summary>
    /// Regression: Telegram's duplicate-poller conflict must be recognized even when it arrives as a plain
    /// <see cref="RequestException" /> carrying only the HTTP status and the documented conflict text.
    /// </summary>
    /// <remarks>
    /// Both local conflict classifiers previously required a typed <see cref="ApiRequestException" />, so this production
    /// shape escaped conflict handling entirely and surfaced as generic degraded-polling noise with no status code.
    /// </remarks>
    [Fact]
    public void Conflict_arriving_as_a_plain_request_exception_is_still_a_conflict()
    {
        var conflict = new RequestException(
            "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running",
            HttpStatusCode.Conflict);

        var failure = TelegramPollingFailureDiagnostics.Describe(conflict);

        Assert.Equal(TelegramPollingFailureKind.GetUpdatesConflict, failure.Kind);
        Assert.True(failure.IsConflict);
        Assert.True(TelegramPollingFailureDiagnostics.IsGetUpdatesConflict(conflict));
        Assert.False(TelegramPollingFailureDiagnostics.IsWebhookConflict(conflict));
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal("409", TelegramPollingFailureDiagnostics.FormatStatusCode(failure.StatusCode));
        Assert.Contains("terminated by other getUpdates request", failure.ResponseText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: a webhook conflict must stay distinguishable from a duplicate-poller conflict, because only the
    /// webhook case is safe to repair automatically.
    /// </summary>
    /// <remarks>
    /// Misclassifying the webhook conflict as a duplicate poller would stop a healthy receiver instead of clearing the
    /// webhook that is blocking it.
    /// </remarks>
    [Fact]
    public void Webhook_conflict_is_never_reported_as_a_duplicate_poller()
    {
        var webhook = new ApiRequestException(
            "Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first",
            409);

        var failure = TelegramPollingFailureDiagnostics.Describe(webhook);

        Assert.Equal(TelegramPollingFailureKind.WebhookConflict, failure.Kind);
        Assert.True(TelegramPollingFailureDiagnostics.IsWebhookConflict(webhook));
        Assert.False(TelegramPollingFailureDiagnostics.IsGetUpdatesConflict(webhook));
        Assert.True(failure.IsConflict);
    }

    /// <summary>
    /// Regression: a conflict must never be answered with a retry delay, and a genuine gateway failure must stay
    /// transient.
    /// </summary>
    /// <remarks>
    /// Retrying a duplicate poller is what converts one conflict into a sustained conflict loop across every bot sharing
    /// the token, so the shared transient classifier must exclude conflicts while keeping a real edge failure on the
    /// bounded backoff path.
    /// </remarks>
    [Fact]
    public void Conflict_is_never_transient_while_a_gateway_failure_stays_transient()
    {
        var conflict = new RequestException(
            "Conflict: terminated by other getUpdates request",
            HttpStatusCode.Conflict);
        var gateway = new RequestException("Bad Gateway", HttpStatusCode.BadGateway);

        Assert.False(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(conflict));
        Assert.True(TelegramPollingBackoffPolicy.IsTransientGatewayFailure(gateway));
        Assert.Equal(TelegramPollingFailureKind.Transient, TelegramPollingFailureDiagnostics.Describe(gateway).Kind);
    }

    /// <summary>
    /// Regression: a 429 must be classified as a rate limit with its status recorded, not as an unknown failure.
    /// </summary>
    /// <remarks>
    /// A 429 proves the HTTP path to Telegram works, so it keeps its own dedicated retry-after policy rather than the
    /// exponential gateway backoff.
    /// </remarks>
    [Fact]
    public void Too_many_requests_is_classified_as_a_rate_limit()
    {
        var rateLimited = new RequestException("Too Many Requests: retry after 3", HttpStatusCode.TooManyRequests);

        var failure = TelegramPollingFailureDiagnostics.Describe(rateLimited);

        Assert.Equal(TelegramPollingFailureKind.RateLimited, failure.Kind);
        Assert.Equal(429, failure.StatusCode);
        Assert.False(failure.IsConflict);
        Assert.True(TelegramRateLimitPolicy.IsRateLimited(rateLimited));
    }

    /// <summary>
    /// Regression: diagnostic text must name the inner transport exception and never leak a bot token.
    /// </summary>
    /// <remarks>
    /// The production line reported only <c>errorType=RequestException</c>, which cannot distinguish a TLS reset from an
    /// edge rejection. The inner type and status are therefore captured, while any BotFather token shape in the text is
    /// replaced so the diagnostic stays safe for the journal, the daily diagnostic file, and the forwarded operator
    /// channel.
    /// </remarks>
    [Fact]
    public void Diagnostics_report_the_inner_transport_cause_without_leaking_a_token()
    {
        const string token = "8123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";
        var socket = new SocketException((int)SocketError.ConnectionReset);
        var transport = new HttpRequestException("The SSL connection could not be established", socket);
        var failure = TelegramPollingFailureDiagnostics.Describe(
            new RequestException($"Bot API Service Failure for https://api.telegram.org/bot{token}/getUpdates", transport));

        Assert.Equal("RequestException", failure.ExceptionType);
        Assert.Contains("SocketException", failure.InnerExceptionType, StringComparison.Ordinal);
        Assert.DoesNotContain(token, failure.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain("8123456789:", failure.ResponseText, StringComparison.Ordinal);
        Assert.Contains("<redacted-token>", failure.ResponseText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: the dispatcher must stop the affected receiver when a duplicate-poller conflict arrives as a plain
    /// <see cref="RequestException" />, instead of applying a retry delay and logging generic instability.
    /// </summary>
    /// <returns>A task that completes after one dispatcher invocation and its assertions.</returns>
    /// <remarks>
    /// This is the production regression end to end: the previous classifiers missed this shape, so the conflict was
    /// retried with bounded backoff and never reported as a duplicate poller. The scope factory is a tripwire proving the
    /// generic polling logger was not reached, and the elapsed time proves no backoff delay ran.
    /// </remarks>
    [Fact]
    public async Task Dispatcher_stops_the_receiver_for_a_conflict_that_arrived_as_a_plain_request_exception()
    {
        const string botId = "tenant-7385810997";
        var fixture = CreateDispatcher(botId);

        var conflict = new RequestException(
            "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running",
            HttpStatusCode.Conflict);

        var handler = typeof(MultiBotHostedService).GetMethod(
            "HandleBotPollingErrorAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await (Task)handler!.Invoke(fixture.Runtime, new object[] { botId, conflict, CancellationToken.None })!;
        stopwatch.Stop();

        // A conflict is permanent for this generation: no retry delay, no generic polling logger, and one critical line
        // that names the token fingerprint, the loop generation, and the resolved status code.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, fixture.ScopeFactory.ScopeCount);
        var critical = Assert.Single(fixture.Logger.Entries, entry =>
            entry.Level == LogLevel.Critical &&
            entry.Message.Contains("another getUpdates poller", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("statusCode=409", critical.Message, StringComparison.Ordinal);
        Assert.Contains("tokenHashPrefix=", critical.Message, StringComparison.Ordinal);
        Assert.Contains("pollingGeneration=", critical.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", critical.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: a failure shape with no dedicated branch must still be logged once with the identity, inner
    /// exception, status code, and response text, because that opaque line is what the production report could not
    /// triage.
    /// </summary>
    /// <returns>A task that completes after one dispatcher invocation and its assertions.</returns>
    /// <remarks>
    /// A non-conflict Telegram API rejection reaches the shared fallback. The assertion targets the diagnostic line
    /// rather than the fallback logger itself, whose scope is intentionally stubbed out here.
    /// </remarks>
    [Fact]
    public async Task Dispatcher_logs_identity_and_status_for_an_unclassified_failure()
    {
        const string botId = "tenant-934171994";
        var fixture = CreateDispatcher(botId);

        var handler = typeof(MultiBotHostedService).GetMethod(
            "HandleBotPollingErrorAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        await (Task)handler!.Invoke(
            fixture.Runtime,
            new object[] { botId, new ApiRequestException("Bad Request: message is too long", 400), CancellationToken.None })!;

        var diagnostic = Assert.Single(fixture.Logger.Entries, entry =>
            entry.Message.Contains("outcome=unclassified", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, diagnostic.Level);
        Assert.Contains($"botId={botId}", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("errorType=ApiRequestException", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("innerException=none", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("statusCode=400", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("responseText=Bad Request: message is too long", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("cancellationReason=not_requested", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: a token shared by two configured runtime bots must be reported at startup as one concrete finding.
    /// </summary>
    /// <remarks>
    /// The runtime lease rejects the duplicate loop, and this configuration audit explains it before any receiver starts,
    /// so a burst of unrelated tenants failing at once is attributable to one shared credential rather than investigated
    /// bot by bot.
    /// </remarks>
    [Fact]
    public void Startup_identity_audit_warns_when_two_configured_bots_share_one_token()
    {
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
            Id = "tenant-8161518157",
            Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 8161518157,
            Enabled = true,
            Token = "8161518157:" + new string('c', 35)
        });
        registry.Upsert(new BotInstance
        {
            Id = "tenant-6459198363",
            Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 6459198363,
            Enabled = true,
            Token = "8161518157:" + new string('c', 35)
        });

        var logger = new RecordingLogger();
        var runtime = new MultiBotHostedService(
            registry,
            new BotClientProvider(registry),
            null,
            new ThrowingScopeFactory(),
            new BotContextAccessor(),
            new BotRuntimeStatusStore(),
            configuration,
            logger);

        var audit = typeof(MultiBotHostedService).GetMethod(
            "LogConfiguredPollingIdentityAudit", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(audit);

        audit!.Invoke(runtime, null);

        var warning = Assert.Single(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("share one Telegram token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("tenant-8161518157", warning.Message, StringComparison.Ordinal);
        Assert.Contains("tenant-6459198363", warning.Message, StringComparison.Ordinal);
        Assert.Contains("tokenHashPrefix=", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a receiver manager with one enabled tenant bot and recording test doubles.
    /// </summary>
    /// <param name="botId">Internal runtime bot id to register, for example <c>tenant-7385810997</c>.</param>
    /// <returns>The dispatcher, its recording logger, and its tripwire scope factory.</returns>
    /// <remarks>
    /// No receiver is started, so the dispatcher exercises only its error classification and logging paths. The scope
    /// factory throws when reached, which is how a test proves a failure never fell through to the generic logger.
    /// </remarks>
    private static DispatcherFixture CreateDispatcher(string botId)
    {
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
            OwnerTelegramUserId = 424242,
            Enabled = true,
            Token = "424242:" + new string('d', 35)
        });

        var logger = new RecordingLogger();
        var scopeFactory = new ThrowingScopeFactory();
        var runtime = new MultiBotHostedService(
            registry,
            new BotClientProvider(registry),
            null,
            scopeFactory,
            new BotContextAccessor(),
            new BotRuntimeStatusStore(),
            configuration,
            logger);

        return new DispatcherFixture(runtime, logger, scopeFactory);
    }

    /// <summary>Receiver manager plus the recording doubles a dispatcher test asserts against.</summary>
    /// <param name="Runtime">Dispatched instance whose private error handler is invoked by reflection.</param>
    /// <param name="Logger">Captures the structured log entries the dispatcher produced.</param>
    /// <param name="ScopeFactory">Counts scope creations so a test can prove the generic logger was not reached.</param>
    private sealed record DispatcherFixture(
        MultiBotHostedService Runtime,
        RecordingLogger Logger,
        ThrowingScopeFactory ScopeFactory);

    /// <summary>
    /// Scope factory that counts its calls and then fails, so resolving the generic polling logger is detectable.
    /// </summary>
    /// <remarks>
    /// The generic path resolves its logger from a scope as its first action, so a non-zero count means a failure was
    /// misrouted away from its dedicated branch.
    /// </remarks>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        /// <summary>Number of scope creations observed; dedicated branches must keep this at zero.</summary>
        public int ScopeCount { get; private set; }

        /// <summary>Records the call and fails, because the caller is on the wrong classification branch.</summary>
        /// <returns>The method never returns.</returns>
        /// <exception cref="InvalidOperationException">Always thrown to fail the misrouted generic path.</exception>
        public IServiceScope CreateScope()
        {
            ScopeCount++;
            throw new InvalidOperationException(
                "The generic polling-error logger must not be reached for this failure shape.");
        }
    }

    /// <summary>Captures every structured log entry so a test can prove which dispatcher branch ran.</summary>
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

    /// <summary>No-op scope handle returned by <see cref="RecordingLogger" />.</summary>
    private sealed class NopDisposable : IDisposable
    {
        /// <summary>Shared instance whose disposal has no effect.</summary>
        public static readonly NopDisposable Instance = new();

        /// <inheritdoc />
        public void Dispose() { }
    }
}
