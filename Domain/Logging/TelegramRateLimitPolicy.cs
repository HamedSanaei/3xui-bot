using System;
using Telegram.Bot.Exceptions;

namespace Adminbot.Domain.Logging
{
    /// <summary>
    /// Central classification and backoff policy for Telegram HTTP <c>429 Too Many Requests</c> responses.
    /// </summary>
    /// <remarks>
    /// Telegram.Bot 19.x does not pause its polling loop or honor <c>RetryAfter</c> after a 429, and a 429 raised while
    /// sending a message can otherwise escape update handling and stop a receiver. Every Telegram caller that can
    /// observe a 429 (polling error handlers, update wrappers, and the Telegram log channel) routes through this policy
    /// so the whole process back offs together, never tight-loops, and never reports a rate-limit failure back through
    /// Telegram itself.
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
        /// Callers should pass this value to <see cref="System.Threading.Tasks.Task.Delay(TimeSpan,
        /// System.Threading.CancellationToken)"/> while holding the receiver or update flow, so the next Telegram
        /// request starts only after the rate-limit window has passed.
        /// </remarks>
        public static TimeSpan GetRetryDelay(Exception exception)
        {
            var retryAfterSeconds = (exception as ApiRequestException)?.Parameters?.RetryAfter;
            var baseSeconds = retryAfterSeconds.HasValue && retryAfterSeconds.Value > 0
                ? retryAfterSeconds.Value
                : DefaultRetryAfterSeconds;

            return TimeSpan.FromSeconds(Math.Min(baseSeconds, MaxRetryAfterSeconds)) + RetryAfterBuffer;
        }
    }
}
