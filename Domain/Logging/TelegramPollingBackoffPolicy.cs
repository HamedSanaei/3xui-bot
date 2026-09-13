using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Exceptions;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Central classification and delay calculation for transient Telegram long-polling failures in the multi-bot runtime.
    /// </summary>
    /// <remarks>
    /// Telegram occasionally returns edge/gateway failures such as <c>502 Bad Gateway</c>, <c>503 Service Unavailable</c>,
    /// or <c>504 Gateway Timeout</c> from <c>getUpdates</c> for every bot receiver at the same moment. Those responses are
    /// provider-side and temporary: they must never stop a receiver, disable a tenant storefront, or be treated as a
    /// per-user delivery failure. Without a delay, every enabled owned and tenant receiver retries in lockstep and turns
    /// one short Telegram outage into a synchronized retry and log storm.
    ///
    /// This type therefore owns two things:
    /// <list type="bullet">
    /// <item>the shared exception classification used by the polling error handler, so owned and tenant receivers agree
    /// on what is transient;</item>
    /// <item>a bounded exponential delay with jitter, computed from a failure count so unit tests can pass a
    /// deterministic jitter sample instead of relying on real random timing.</item>
    /// </list>
    ///
    /// HTTP 429 keeps its own authoritative path through <see cref="TelegramRateLimitPolicy.GetRetryDelay"/>; this policy
    /// never stacks its delay on top of Telegram's <c>RetryAfter</c>.
    /// </remarks>
    public static class TelegramPollingBackoffPolicy
    {
        /// <summary>
        /// Base delay in seconds applied after the first transient polling failure. Each subsequent failure doubles it.
        /// </summary>
        public const int BaseDelaySeconds = 1;

        /// <summary>
        /// Hard maximum delay in seconds for a single transient polling failure. A sustained outage converges on this
        /// value so receivers keep probing Telegram periodically without hammering it.
        /// </summary>
        public const int MaximumDelaySeconds = 30;

        /// <summary>
        /// Maximum relative jitter applied to the calculated delay. <c>0.2</c> means the final delay stays within
        /// ±20% of the exponential value, so many bots recovering from the same outage do not retry at the same instant.
        /// </summary>
        public const double JitterFraction = 0.2;

        /// <summary>
        /// Absolute floor in milliseconds for a jittered delay so a delay is never zero, negative, or a hot loop.
        /// </summary>
        public const int MinimumDelayMilliseconds = 100;

        /// <summary>
        /// Quiet period after which the next transient failure is treated as a new incident instead of a continuation.
        /// </summary>
        /// <remarks>
        /// Telegram.Bot 22.10.3 only invokes the polling error handler on failure and still exposes no success
        /// callback for an empty <c>getUpdates</c> response, so this healthy-period decay is the earliest reliable
        /// evidence that polling recovered: when failures stop, the elapsed gap eventually exceeds this window and the
        /// counter restarts at the first step. The state is also cleared explicitly when a receiver stops or when
        /// Telegram proves reachability with a 429.
        /// </remarks>
        public const int HealthyResetSeconds = 60;

        /// <summary>
        /// Upper bound for the tracked failure count so an unbounded outage cannot overflow the exponential expression.
        /// </summary>
        private const int MaximumTrackedFailures = 20;

        /// <summary>
        /// Maximum number of <see cref="Exception.InnerException"/> levels inspected before classification gives up.
        /// </summary>
        /// <remarks>
        /// The real Telegram.Bot 22.10.3 transport shape is only three levels deep
        /// (<c>RequestException -&gt; HttpRequestException -&gt; IOException -&gt; SocketException</c>). The bound protects
        /// the polling error loop from an unexpectedly deep or self-referencing exception chain.
        /// </remarks>
        private const int MaximumExceptionChainDepth = 10;

        /// <summary>
        /// Determines whether an exception represents a transient Telegram polling/gateway failure that deserves a
        /// bounded backoff instead of a permanent failure decision.
        /// </summary>
        /// <param name="exception">
        /// Exception raised by the Telegram long-polling receiver, an update handler, or a receiver startup probe. May be
        /// <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> for Telegram request timeouts, HTTP 5xx gateway/server responses, HTTP 429 rate limits, and
        /// network-level transport failures, including transport failures nested inside a Telegram.Bot
        /// <see cref="RequestException"/>; otherwise <c>false</c> so invalid tokens, duplicate conflicts, and
        /// per-user delivery failures keep their existing dedicated handling.
        /// </returns>
        /// <remarks>
        /// Both the owned/tenant polling error handler and the shared dispatcher use this single classifier so a
        /// <c>502</c> is never transient in one place and fatal in another.
        ///
        /// Telegram.Bot 22.10.3 wraps a failed <c>HttpClient.SendAsync</c> as
        /// <c>RequestException -&gt; HttpRequestException -&gt; IOException -&gt; SocketException</c>, for example
        /// <c>Bot API Service Failure: HttpRequestException: The SSL connection could not be established</c>. A
        /// <see cref="RequestException"/> therefore cannot be judged from its own status code and message alone: when it
        /// carries no permanent Telegram/API evidence, its inner exception chain is inspected for transport failures.
        /// Classification prefers exception types over message text, and permanent evidence such as an HTTP 4xx status
        /// or an explicit non-transient Telegram error code always wins over a coincidental inner exception.
        /// </remarks>
        /// <example>
        /// <code>
        /// if (TelegramPollingBackoffPolicy.IsTransientGatewayFailure(exception))
        /// {
        ///     var delay = TelegramPollingBackoffPolicy.CalculateDelay(consecutiveFailures);
        /// }
        /// </code>
        /// </example>
        public static bool IsTransientGatewayFailure(Exception exception)
        {
            return exception is not null && ClassifyTransientFailure(exception, depth: 0);
        }

        /// <summary>
        /// Classifies one level of an exception chain and recurses into its inner exception when no decision is possible.
        /// </summary>
        /// <param name="exception">Current exception to classify; never <c>null</c> when called.</param>
        /// <param name="depth">
        /// Zero-based chain depth already inspected. Must stay below <see cref="MaximumExceptionChainDepth"/> so a
        /// malformed chain cannot recurse without bound.
        /// </param>
        /// <returns>
        /// <c>true</c> when this exception or a nested transport failure proves the failure is transient; <c>false</c>
        /// when permanent evidence was found or when the chain contains no transport failure.
        /// </returns>
        /// <remarks>
        /// Order matters: Telegram API error codes and HTTP status codes are authoritative and are evaluated before the
        /// inner chain, so a permanent 400/401/403/409 response can never be reclassified as transient by an unrelated
        /// nested exception.
        /// </remarks>
        private static bool ClassifyTransientFailure(Exception exception, int depth)
        {
            if (depth > MaximumExceptionChainDepth)
                return false;

            // An ApiRequestException carries Telegram's own error code, which is the most precise signal available.
            if (exception is ApiRequestException apiException)
            {
                if (IsTransientStatusCode(apiException.ErrorCode) ||
                    ContainsGatewayText(apiException.Message ?? string.Empty))
                    return true;

                // An explicit, non-transient Telegram error code such as 400/401/403/409 is permanent evidence: a nested
                // exception must not turn an invalid token or a duplicate getUpdates conflict into a retry loop.
                return false;
            }

            // A plain RequestException can be Telegram's transport wrapper (Telegram.Bot 22.10.3) or can carry the raw
            // edge HTTP status when the response body is not parseable Telegram JSON.
            if (exception is RequestException requestException)
            {
                var hasPermanentClientStatus = false;

                if (requestException.HttpStatusCode is { } status)
                {
                    var statusCode = (int)status;
                    if (statusCode is >= 500 and <= 599)
                        return true;

                    // 408 Request Timeout and 425 Too Early are timing/transport conditions a gateway can return during
                    // a short outage rather than a permanent rejection.
                    if (statusCode is 408 or 425)
                        return true;

                    hasPermanentClientStatus = statusCode is >= 400 and <= 499;
                }

                // Message evidence is evaluated before the permanent-status decision so the pre-existing timeout and
                // gateway handling keeps exactly its previous precedence.
                if (ContainsTransientRequestText(requestException.Message ?? string.Empty))
                    return true;

                // An explicit 4xx edge status is a server-side rejection: permanent evidence wins over the inner chain so
                // a revoked token, an invalid request, or a duplicate conflict cannot become a retry loop.
                if (hasPermanentClientStatus)
                    return false;

                // No permanent status, timeout, or gateway evidence: continue into the inner chain, which is where
                // Telegram.Bot 22.10.3 nests HttpRequestException -> IOException -> SocketException for TLS handshake
                // and connection-reset failures. This is the production shape that previously fell through to the noisy
                // legacy polling logger.
                return requestException.InnerException is not null &&
                       ClassifyTransientFailure(requestException.InnerException, depth + 1);
            }

            // Transport-level failures are always transient regardless of which layer wrapped them.
            if (exception is HttpRequestException || exception is IOException || exception is SocketException)
                return true;

            // Unwrap unrelated wrappers (for example AggregateException) so a nested transport failure is still seen.
            return exception.InnerException is not null &&
                   ClassifyTransientFailure(exception.InnerException, depth + 1);
        }

        /// <summary>
        /// Determines whether a Telegram API error code represents a temporary provider-side condition.
        /// </summary>
        /// <param name="errorCode">Telegram <c>error_code</c> value from the Bot API response body.</param>
        /// <returns>
        /// <c>true</c> for HTTP 429 rate limits and every HTTP 5xx provider failure; <c>false</c> for permanent client
        /// errors such as 400, 401, 403, and 409.
        /// </returns>
        private static bool IsTransientStatusCode(int errorCode)
        {
            return errorCode is 429 || errorCode >= 500;
        }

        /// <summary>
        /// Checks whether a RequestException message names a timeout, gateway, or network transport condition.
        /// </summary>
        /// <param name="message">Telegram request exception message text; null or empty returns <c>false</c>.</param>
        /// <returns><c>true</c> when the text names a known transient polling condition; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Message matching is a secondary signal for the rare case where Telegram returns a plain
        /// <see cref="RequestException"/> without a typed transport inner exception. Type-based classification in
        /// <see cref="ClassifyTransientFailure"/> remains the primary path.
        /// </remarks>
        private static bool ContainsTransientRequestText(string message)
        {
            return message.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   ContainsGatewayText(message) ||
                   ContainsTransportText(message);
        }

        /// <summary>
        /// Checks whether a message names a network transport failure such as a reset or unestablished TLS connection.
        /// </summary>
        /// <param name="message">Exception message text to inspect; null or empty returns <c>false</c>.</param>
        /// <returns><c>true</c> when the text names a known transient transport condition; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// This is the message-level fallback for a transport failure that arrives without the usual typed
        /// <see cref="HttpRequestException"/>/<see cref="IOException"/>/<see cref="SocketException"/> chain. It is only
        /// consulted after API error codes and HTTP statuses produced no decision.
        /// </remarks>
        private static bool ContainsTransportText(string message)
        {
            return message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("connection closed", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("connection could not be established", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("network unreachable", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Computes the bounded exponential delay for a transient polling failure using the production random jitter source.
        /// </summary>
        /// <param name="consecutiveFailures">
        /// One-based count of consecutive transient polling failures for a single bot; values below one are clamped to one.
        /// </param>
        /// <returns>
        /// A delay between <see cref="MinimumDelayMilliseconds"/> and <see cref="MaximumDelaySeconds"/> seconds inclusive.
        /// Never negative and never unbounded.
        /// </returns>
        /// <remarks>
        /// Production callers use this overload. Unit tests should call
        /// <see cref="CalculateDelay(int, double)"/> with an explicit jitter sample so results are deterministic.
        /// </remarks>
        public static TimeSpan CalculateDelay(int consecutiveFailures)
        {
            return CalculateDelay(consecutiveFailures, NextJitterSample());
        }

        /// <summary>
        /// Computes the bounded exponential delay for a transient polling failure with an explicit jitter sample.
        /// </summary>
        /// <param name="consecutiveFailures">
        /// One-based count of consecutive transient polling failures for a single bot. Values below one are clamped to
        /// one and values above the internal cap are clamped so the exponential expression can never overflow.
        /// </param>
        /// <param name="jitterSample">
        /// Deterministic jitter input in the range <c>-1</c> to <c>1</c>. A value of <c>0</c> returns the exact
        /// exponential delay, which is what tests assert; values outside the range are clamped.
        /// </param>
        /// <returns>
        /// The exponential delay (<c>1s, 2s, 4s, 8s, 16s, …</c>) multiplied by <c>1 ± 0.2</c> and clamped to
        /// <see cref="MinimumDelayMilliseconds"/>…<see cref="MaximumDelaySeconds"/>.
        /// </returns>
        /// <remarks>
        /// The delay is intentionally independent of any Telegram <c>RetryAfter</c> value: callers that handle a 429 use
        /// <see cref="TelegramRateLimitPolicy.GetRetryDelay"/> instead, so the two delays are never stacked.
        /// </remarks>
        /// <example>
        /// <code>
        /// var exact = TelegramPollingBackoffPolicy.CalculateDelay(1, 0d);   // 00:00:01
        /// var slow = TelegramPollingBackoffPolicy.CalculateDelay(2, 1d);    // 00:00:02.4
        /// </code>
        /// </example>
        public static TimeSpan CalculateDelay(int consecutiveFailures, double jitterSample)
        {
            var failures = Math.Clamp(consecutiveFailures, 1, MaximumTrackedFailures);
            var exponent = failures - 1;
            var baseSeconds = Math.Min(BaseDelaySeconds * Math.Pow(2, exponent), MaximumDelaySeconds);

            var sample = Math.Clamp(jitterSample, -1d, 1d);
            var jitteredSeconds = baseSeconds * (1d + (sample * JitterFraction));

            var clampedSeconds = Math.Clamp(
                jitteredSeconds,
                MinimumDelayMilliseconds / 1000d,
                MaximumDelaySeconds);

            return TimeSpan.FromSeconds(clampedSeconds);
        }

        /// <summary>
        /// Returns a thread-safe random jitter sample in the half-open range <c>-1</c> to <c>+1</c>.
        /// </summary>
        /// <returns>
        /// A random value where <c>-1</c> is the lowest allowed delay multiplier and <c>+1</c> the highest. The value
        /// range is deliberately signed so one bot can retry slightly early and another slightly late.
        /// </returns>
        /// <remarks>
        /// Uses <see cref="Random.Shared"/>, which is thread-safe, because the multi-bot runtime calls this from many
        /// receiver tasks at once. No mutable static counter is kept for backoff state.
        /// </remarks>
        public static double NextJitterSample()
        {
            return (Random.Shared.NextDouble() * 2d) - 1d;
        }

        /// <summary>
        /// Awaits a transient polling backoff delay while honoring the bot receiver cancellation token.
        /// </summary>
        /// <param name="delay">
        /// Non-negative delay computed by <see cref="CalculateDelay(int)"/>. A zero or negative value returns immediately.
        /// </param>
        /// <param name="cancellationToken">
        /// The affected bot receiver's cancellation token. Cancellation is the normal shutdown path and must never be
        /// reported as a failure.
        /// </param>
        /// <returns>
        /// <c>true</c> when the full delay completed; <c>false</c> when the delay was skipped or cancelled, which means
        /// the caller should return without logging an error, marking a bot failed, or restarting a receiver.
        /// </returns>
        /// <remarks>
        /// Telegram.Bot 22.10.3 awaits the polling error handler before issuing the next <c>getUpdates</c>, which is
        /// exactly how the existing 429 pause already works. Callers must not hold the registry lock, a lifecycle gate,
        /// or a database lock while awaiting this method.
        /// </remarks>
        /// <example>
        /// <code>
        /// await TelegramPollingBackoffPolicy.DelayAsync(delay, cancellationToken);
        /// </code>
        /// </example>
        public static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
                return !cancellationToken.IsCancellationRequested;

            try
            {
                await Task.Delay(delay, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Receiver shutdown or a replaced receiver generation during the backoff window is the normal stop
                // path. It is not an error, must not mark the bot failed, and must not restart anything.
                return false;
            }
        }

        /// <summary>
        /// Checks whether a message contains one of the recognized Telegram edge/gateway failure phrases.
        /// </summary>
        /// <param name="message">Exception message text to inspect; null or empty returns <c>false</c>.</param>
        /// <returns><c>true</c> when the text names a known transient gateway condition; otherwise <c>false</c>.</returns>
        private static bool ContainsGatewayText(string message)
        {
            return message.Contains("bad gateway", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Immutable outcome of one recorded transient polling failure for a single bot.
    /// </summary>
    /// <remarks>
    /// Returned by <see cref="TelegramPollingBackoffTracker.RegisterTransientFailure"/>. It carries only counts, a
    /// delay, and logging flags; no Telegram payload, token, or response body is ever stored or exposed.
    /// </remarks>
    public readonly struct TelegramPollingBackoffDecision
    {
        /// <summary>
        /// One-based count of consecutive transient polling failures recorded for the bot after this failure.
        /// </summary>
        public int ConsecutiveFailures { get; init; }

        /// <summary>
        /// Bounded, jittered delay the caller must await before the next <c>getUpdates</c> for this bot only.
        /// </summary>
        public TimeSpan Delay { get; init; }

        /// <summary>
        /// Indicates whether this failure started a new incident, either because it was the first failure for the bot or
        /// because the previous failure is older than the healthy-reset window.
        /// </summary>
        public bool IsNewIncident { get; init; }

        /// <summary>
        /// Indicates whether the caller should write one operational summary line for this failure. Repeated failures in
        /// the same incident stay at debug level so a multi-bot Telegram outage cannot flood the journal.
        /// </summary>
        public bool ShouldLogOperational { get; init; }
    }

    /// <summary>
    /// Thread-safe, process-local transient polling failure state for every bot receiver.
    /// </summary>
    /// <remarks>
    /// State is keyed by the internal runtime bot id (owned, assistant, and tenant storefront ids share the namespace) so
    /// a failure on one tenant bot never delays another bot. Everything here is in memory only and is never persisted, so
    /// this type adds no database table, column, or migration.
    ///
    /// The tracker is designed to be called from the polling error handler: <see cref="RegisterTransientFailure"/> only
    /// mutates state under a short private lock and returns the computed decision. Callers await the returned delay after
    /// releasing all synchronization, never while holding this tracker's lock.
    /// </remarks>
    public sealed class TelegramPollingBackoffTracker
    {
        /// <summary>
        /// Default minimum spacing between two operational (non-debug) transient-failure log lines for the same bot.
        /// </summary>
        private static readonly TimeSpan DefaultOperationalLogInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Per-bot failure state. Keys are compared case-insensitively because runtime bot ids are not case-sensitive.
        /// </summary>
        private readonly ConcurrentDictionary<string, BotFailureState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clock seam used by tests to advance time deterministically without waiting for real elapsed time.
        /// </summary>
        private readonly Func<DateTime> _utcNow;

        /// <summary>
        /// Minimum interval between two operational log lines for the same bot.
        /// </summary>
        private readonly TimeSpan _operationalLogInterval;

        /// <summary>
        /// Creates a tracker with the production clock and logging interval.
        /// </summary>
        /// <remarks>
        /// Production registers one instance per runtime, so failures recorded for a bot id are isolated per bot while
        /// remaining visible to the receiver lifecycle that owns that bot.
        /// </remarks>
        public TelegramPollingBackoffTracker()
            : this(() => DateTime.UtcNow, DefaultOperationalLogInterval)
        {
        }

        /// <summary>
        /// Creates a tracker with explicit clock and logging interval seams for deterministic tests.
        /// </summary>
        /// <param name="utcNow">Clock returning the current UTC time; must never return null.</param>
        /// <param name="operationalLogInterval">
        /// Minimum spacing between two operational log lines for one bot. Must be greater than zero.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="utcNow"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="operationalLogInterval"/> is zero or negative.
        /// </exception>
        internal TelegramPollingBackoffTracker(Func<DateTime> utcNow, TimeSpan operationalLogInterval)
        {
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            if (operationalLogInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(operationalLogInterval));

            _operationalLogInterval = operationalLogInterval;
        }

        /// <summary>
        /// Records one transient polling failure for a bot and returns the bounded delay and logging decision.
        /// </summary>
        /// <param name="botId">
        /// Internal runtime bot id whose receiver reported the transient failure. Tenant ids use the
        /// <c>tenant-{ownerId}</c> or <c>tenant-{ownerId}-{storeNumber}</c> format. A null or whitespace value is ignored
        /// and returns a single-failure decision with the base delay.
        /// </param>
        /// <returns>
        /// A decision carrying the consecutive failure count, the jittered delay to await, whether this started a new
        /// incident, and whether an operational log line is due.
        /// </returns>
        /// <remarks>
        /// This method is deliberately fast and never awaits. It increments the per-bot counter, applies the
        /// healthy-period decay when the previous failure is older than
        /// <see cref="TelegramPollingBackoffPolicy.HealthyResetSeconds"/>, and rate-limits the operational log signal so
        /// a burst from one bot produces at most one summary line per window. Callers must await
        /// <see cref="TelegramPollingBackoffDecision.Delay"/> outside any lock.
        /// </remarks>
        /// <example>
        /// <code>
        /// var decision = tracker.RegisterTransientFailure(botId);
        /// if (decision.ShouldLogOperational)
        ///     logger.LogInformation("Telegram polling degraded. botId={BotId} consecutiveFailures={N}", botId, decision.ConsecutiveFailures);
        /// await TelegramPollingBackoffPolicy.DelayAsync(decision.Delay, cancellationToken);
        /// </code>
        /// </example>
        public TelegramPollingBackoffDecision RegisterTransientFailure(string botId)
        {
            var now = _utcNow();

            if (string.IsNullOrWhiteSpace(botId))
            {
                return new TelegramPollingBackoffDecision
                {
                    ConsecutiveFailures = 1,
                    Delay = TelegramPollingBackoffPolicy.CalculateDelay(1),
                    IsNewIncident = true,
                    ShouldLogOperational = true
                };
            }

            var state = _states.GetOrAdd(botId, _ => new BotFailureState());

            lock (state.SyncRoot)
            {
                var resetWindow = TimeSpan.FromSeconds(TelegramPollingBackoffPolicy.HealthyResetSeconds);
                var isNewIncident = state.LastFailureAtUtc is null ||
                                    now - state.LastFailureAtUtc.Value >= resetWindow;

                state.ConsecutiveFailures = isNewIncident
                    ? 1
                    : Math.Min(state.ConsecutiveFailures + 1, int.MaxValue);

                state.LastFailureAtUtc = now;

                var shouldLogOperational = isNewIncident ||
                                           state.LastOperationalLogAtUtc is null ||
                                           now - state.LastOperationalLogAtUtc.Value >= _operationalLogInterval;

                if (shouldLogOperational)
                    state.LastOperationalLogAtUtc = now;

                var delay = TelegramPollingBackoffPolicy.CalculateDelay(state.ConsecutiveFailures);

                return new TelegramPollingBackoffDecision
                {
                    ConsecutiveFailures = state.ConsecutiveFailures,
                    Delay = delay,
                    IsNewIncident = isNewIncident,
                    ShouldLogOperational = shouldLogOperational
                };
            }
        }

        /// <summary>
        /// Clears the transient failure state for a bot because polling health is proven again.
        /// </summary>
        /// <param name="botId">Internal runtime bot id whose transient incident ended.</param>
        /// <remarks>
        /// Called when Telegram returns 429 (proving the HTTP path to Telegram works) and when a bot receiver is
        /// stopped, so a later unrelated transient failure starts again at the first backoff step instead of inheriting
        /// an old incident's maximum delay.
        /// </remarks>
        /// <example>
        /// <code>
        /// tracker.RecordHealthyPolling(botId);
        /// </code>
        /// </example>
        public void RecordHealthyPolling(string botId)
        {
            if (string.IsNullOrWhiteSpace(botId))
                return;

            _states.TryRemove(botId, out _);
        }

        /// <summary>
        /// Removes the transient failure state for a bot that is being permanently stopped or disabled.
        /// </summary>
        /// <param name="botId">Internal runtime bot id whose receiver lifecycle ended.</param>
        /// <returns><c>true</c> when state existed and was removed; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// This is the lifecycle cleanup hook that prevents unbounded growth from historical tenant bot ids. It is
        /// idempotent and has no side effects beyond removing the in-memory entry.
        /// </remarks>
        public bool Remove(string botId)
        {
            if (string.IsNullOrWhiteSpace(botId))
                return false;

            return _states.TryRemove(botId, out _);
        }

        /// <summary>
        /// Reads the current consecutive transient failure count for a bot without changing it.
        /// </summary>
        /// <param name="botId">Internal runtime bot id to inspect.</param>
        /// <param name="consecutiveFailures">
        /// When the method returns, the tracked consecutive failure count, or <c>0</c> when no state exists.
        /// </param>
        /// <returns><c>true</c> when tracked state exists for the bot; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Exposed for diagnostics and deterministic tests. It never records, resets, or delays anything.
        /// </remarks>
        internal bool TryGetConsecutiveFailures(string botId, out int consecutiveFailures)
        {
            consecutiveFailures = 0;
            if (string.IsNullOrWhiteSpace(botId))
                return false;

            if (!_states.TryGetValue(botId, out var state))
                return false;

            lock (state.SyncRoot)
            {
                consecutiveFailures = state.ConsecutiveFailures;
                return true;
            }
        }

        /// <summary>
        /// Mutable per-bot transient failure state protected by its own lock.
        /// </summary>
        /// <remarks>
        /// A private lock per entry keeps updates short and lets unrelated bots register failures concurrently. Nothing
        /// here is ever awaited while the lock is held.
        /// </remarks>
        private sealed class BotFailureState
        {
            /// <summary>
            /// Synchronization object protecting this bot's counters.
            /// </summary>
            public object SyncRoot { get; } = new();

            /// <summary>
            /// Consecutive transient polling failures since the last healthy reset.
            /// </summary>
            public int ConsecutiveFailures { get; set; }

            /// <summary>
            /// UTC time of the most recent transient failure, used for healthy-period decay.
            /// </summary>
            public DateTime? LastFailureAtUtc { get; set; }

            /// <summary>
            /// UTC time of the most recent operational (non-debug) log line for this bot.
            /// </summary>
            public DateTime? LastOperationalLogAtUtc { get; set; }
        }
    }
}
