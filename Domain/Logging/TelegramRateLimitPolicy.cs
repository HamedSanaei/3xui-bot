using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Exceptions;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Central classification and backoff policy for Telegram rate limits, transient network failures,
    /// permanent API errors, and all durable-outbox retry schedules.
    /// </summary>
    /// <remarks>
    /// Telegram.Bot 19.x does not pause its polling loop or honor <c>RetryAfter</c> after a 429, and a 429 raised while
    /// sending a message can otherwise escape update handling and stop a receiver. Every Telegram caller that can
    /// observe a 429 (polling error handlers, update wrappers, and the Telegram log channel) routes through this policy
    /// so the whole process back offs together, never tight-loops, and never reports a rate-limit failure back through
    /// Telegram itself.
    ///
    /// The durable Telegram log outbox treats this type as the single source of retry schedules: lease duration,
    /// transient backoff, permanent-error thresholds, send pacing, scan cadence, backlog warnings, and dead-letter
    /// retention are all defined here so magic numbers are never scattered across delivery files.
    /// </remarks>
    public static class TelegramRateLimitPolicy
    {
        /// <summary>
        /// Backoff in seconds used when Telegram returns 429 without a <c>RetryAfter</c> parameter.
        /// </summary>
        private const int DefaultRetryAfterSeconds = 5;

        /// <summary>
        /// Maximum single backoff in seconds. Telegram global rate limits normally specify 30-60 seconds; the cap
        /// keeps one receiver from sleeping for an unbounded period. A second 429 after the cap simply re-applies
        /// backoff, so the loop converges instead of hammering Telegram.
        /// </summary>
        private const int MaxRetryAfterSeconds = 60;

        /// <summary>
        /// Extra delay added after Telegram's <c>RetryAfter</c> so the next request is never sent at the exact
        /// rate-limit boundary.
        /// </summary>
        private static readonly TimeSpan RetryAfterBuffer = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Minimum spacing between two Telegram sends performed by the outbox dispatcher. Telegram's per-bot
        /// message throughput is far below one message every 350 ms, so this pacing plus 429 handling keeps the
        /// logger channel safely inside rate limits without artificial delay.
        /// </summary>
        public static readonly TimeSpan MinimumSendInterval = TimeSpan.FromMilliseconds(350);

        /// <summary>
        /// Duration of a Sending lease claimed before a Telegram send. A send completes in seconds, so two minutes
        /// is long enough to never expire a healthy in-flight send and short enough that a crashed worker's record
        /// becomes eligible again quickly. Startup additionally force-resets every Sending row because this host is
        /// single-instance: a fresh process is proof the previous owner is gone.
        /// </summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Interval between periodic outbox scans while the dispatcher is idle. The scan covers lost wake-up signals
        /// (persist succeeded but the process crashed before signaling) and expired leases. Five seconds keeps the
        /// crash-recovery latency in seconds while adding one not-yet-due COUNT/checkpoint query per idle period.
        /// </summary>
        public static readonly TimeSpan PeriodicScanInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Number of failed attempts after which a permanent Telegram error (400/401/403/410) becomes a DeadLetter
        /// row instead of being retried forever. Three attempts allow a transient misconfiguration window to clear
        /// while guaranteeing a malformed message stops hitting Telegram quickly.
        /// </summary>
        public const int PermanentFailureMaxAttempts = 3;

        /// <summary>
        /// Base exponential-backoff delay for transient failures at attempt one. Doubles per attempt:
        /// 5s, 10s, 20s, 40s, … capped at <see cref="TransientMaxDelay"/> (5 minutes).
        /// </summary>
        private static readonly TimeSpan TransientBaseDelay = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Maximum single transient backoff (5 minutes). Capped so one unlucky channel still gets periodic retries
        /// while a long Telegram outage drains over time instead of piling all retries at once.
        /// </summary>
        private static readonly TimeSpan TransientMaxDelay = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Pending-row count above which the dispatcher prints a console backlog warning. Warnings are Telegram-free
        /// by design so a logger outage can never be reported through the channel that is down.
        /// </summary>
        public const int BacklogWarningPendingThreshold = 200;

        /// <summary>
        /// Minimum spacing between two console backlog warnings so a long outage cannot flood the file log.
        /// </summary>
        public static readonly TimeSpan BacklogWarningInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Cadence of lightweight outbox maintenance: dead-letter retention purge, WAL checkpoint, and SQLite
        /// statistics refresh. This is never a per-message VACUUM; SQLite keeps free pages internally.
        /// </summary>
        public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Retention for DeadLetter rows. Older rows are purged during periodic maintenance so evidence stays
        /// inspectable for 90 days while bounded disk growth is guaranteed.
        /// </summary>
        public static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(90);

        /// <summary>
        /// Determines whether an exception is a Telegram <c>429 Too Many Requests</c> response.
        /// </summary>
        /// <param name="exception">
        /// Exception raised by Telegram polling, update handling, or Telegram log delivery. May be null.
        /// </param>
        /// <returns>
        /// <c>true</c> when the exception is an <see cref="ApiRequestException"/> with error code 429; otherwise
        /// <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This check is used to pause receivers, to swallow update-handler rate limits without killing the receiver,
        /// and to keep rate-limit failures out of the Telegram log channel.
        /// </remarks>
        public static bool IsRateLimited(Exception exception)
        {
            return exception is ApiRequestException { ErrorCode: 429 };
        }

        /// <summary>
        /// Computes the backoff delay to respect after a Telegram 429 response.
        /// </summary>
        /// <param name="exception">
        /// The rate-limit exception raised by Telegram. Exceptions that are not a 429 return the default delay so
        /// callers can use the same helper unconditionally.
        /// </param>
        /// <returns>
        /// Telegram's <c>RetryAfter</c> plus one second when the parameter is present and positive, otherwise the
        /// default five seconds plus one second. The returned value never exceeds
        /// <see cref="MaxRetryAfterSeconds"/> plus the one-second buffer.
        /// </returns>
        /// <remarks>
        /// The durable outbox persists <c>now + the returned delay</c> into <c>NextAttemptAtUtc</c> before returning,
        /// so a process restart in the middle of a cooldown still respects the full wait. Callers that only wait
        /// in-memory should pass the value to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
        /// </remarks>
        public static TimeSpan GetRetryDelay(Exception exception)
        {
            var retryAfterSeconds = (exception as ApiRequestException)?.Parameters?.RetryAfter;
            var baseSeconds = retryAfterSeconds.HasValue && retryAfterSeconds.Value > 0
                ? retryAfterSeconds.Value
                : DefaultRetryAfterSeconds;

            return TimeSpan.FromSeconds(Math.Min(baseSeconds, MaxRetryAfterSeconds)) + RetryAfterBuffer;
        }

        /// <summary>
        /// Computes the exponential backoff delay for a transient delivery failure.
        /// </summary>
        /// <param name="attemptCount">
        /// One-based attempt number that just failed (1 = first send attempt). Must be greater than zero.
        /// </param>
        /// <returns>
        /// <c>5s * 2^(attemptCount-1)</c> capped at <see cref="TransientMaxDelay"/> of five minutes.
        /// </returns>
        /// <remarks>
        /// No jitter is applied because the durable outbox uses a single serialized sender: records wake up at their
        /// persisted <c>NextAttemptAtUtc</c> instead of thundering, so randomizing delays would only make recovery
        /// timing nondeterministic without preventing hot loops.
        /// </remarks>
        public static TimeSpan GetTransientRetryDelay(int attemptCount)
        {
            var exponent = Math.Clamp(attemptCount - 1, 0, 20);
            var delay = TransientBaseDelay * Math.Pow(2, exponent);
            return delay > TransientMaxDelay ? TransientMaxDelay : delay;
        }

        /// <summary>
        /// Classifies an exception as a transient network/Telegram failure that deserves exponential backoff.
        /// </summary>
        /// <param name="exception">Exception raised during Telegram delivery; null is not transient.</param>
        /// <param name="cancellationRequested">
        /// Whether the outbox shutdown token is cancelling. A cancellation caused by shutdown is never a transient
        /// failure: the in-flight record keeps its Sending lease and is recovered after restart.
        /// </param>
        /// <returns>
        /// <c>true</c> for <see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/>, network-level
        /// <see cref="IOException"/>, or a Telegram 5xx <see cref="ApiRequestException"/>, unless the operation was
        /// cancelled by shutdown; otherwise <c>false</c>.
        /// </returns>
        public static bool IsTransientFailure(Exception exception, bool cancellationRequested)
        {
            if (exception == null || (exception is OperationCanceledException && cancellationRequested))
                return false;

            return exception is HttpRequestException ||
                   exception is TaskCanceledException ||
                   exception is IOException ||
                   (exception is OperationCanceledException && !cancellationRequested) ||
                   (exception is ApiRequestException api && api.ErrorCode >= 500);
        }

        /// <summary>
        /// Classifies an exception as a permanent Telegram API rejection that should eventually dead-letter.
        /// </summary>
        /// <param name="exception">Exception raised during Telegram delivery; may be null.</param>
        /// <returns>
        /// <c>true</c> for <see cref="ApiRequestException"/> codes 400 (bad request, chat not found, can't parse
        /// entities), 401 (invalid token), 403 (bot blocked), or 410 (channel removed); otherwise <c>false</c>.
        /// </returns>
        /// <remarks>
        /// Permanent errors are retried with short backoff up to <see cref="PermanentFailureMaxAttempts"/> attempts
        /// and then become inspectable DeadLetter rows; they are never deleted and never retried forever.
        /// </remarks>
        public static bool IsPermanentFailure(Exception exception)
        {
            return exception is ApiRequestException api && api.ErrorCode is 400 or 401 or 403 or 410;
        }
    }
}