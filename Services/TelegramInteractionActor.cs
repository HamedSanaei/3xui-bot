using System;
using System.Threading;

/// <summary>
/// Carries the Telegram user id of the actor whose interaction is currently being handled, so UX-only telemetry can
/// name the person waiting without every call site having to thread the id through.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// Production latency telemetry contained entries such as
/// <c>Slow Telegram operation. BotId=vpnetiranbot TelegramUserId=(null) Operation=telegram_callback_ack</c>, because a
/// callback acknowledgement wrapper only ever received the opaque callback id. The sender id was known at the update
/// dispatch boundary but not at the acknowledgement, which made the most frequent controlled latency event
/// unattributable.
/// </para>
/// <para>
/// How it works:
/// <see cref="TelegramUpdateExecutor" /> pushes one scope per scheduled update execution with the sender resolved from
/// the update itself (callback, message, inline query, and the other Telegram actor-bearing shapes). Shared UX helpers
/// read <see cref="Current" /> as a fallback when their caller did not pass an explicit id. The value is carried by
/// <see cref="AsyncLocal{T}"/>, so concurrent lanes never see each other's actor, and it is restored on disposal.
/// </para>
/// <para>
/// Safety:
/// The scope carries only a numeric Telegram user id for diagnostics. It is not an authentication mechanism, never
/// grants authorization, and is never used to resolve bot identity — bot identity continues to come from
/// <c>BotContextAccessor</c> and the scheduler's work item.
/// </para>
/// </remarks>
public static class TelegramInteractionActor
{
    /// <summary>Holds the current actor for the asynchronous execution context of one update.</summary>
    private static readonly AsyncLocal<long?> Ambient = new();

    /// <summary>
    /// Gets the Telegram user id of the actor currently being handled, or <c>null</c> when no actor is known
    /// (for example a background worker, a channel post, or an update shape that genuinely has no sender).
    /// </summary>
    public static long? Current => Ambient.Value;

    /// <summary>
    /// Sets the actor for the lifetime of a using block and restores the previous value on disposal.
    /// </summary>
    /// <param name="telegramUserId">
    /// Numeric Telegram user id taken from the incoming update sender. Pass <c>null</c> when the update genuinely has no
    /// sender; a non-positive value is normalized to <c>null</c> so telemetry never reports an invalid identity.
    /// </param>
    /// <returns>A disposable scope that restores the previous actor.</returns>
    /// <remarks>
    /// The scheduler's update executor is the only production caller. Nested scopes are supported and restore in order.
    /// </remarks>
    /// <example>
    /// <code>
    /// using (TelegramInteractionActor.Push(update.CallbackQuery?.From?.Id))
    ///     await handler.HandleAsync(update, cancellationToken);
    /// </code>
    /// </example>
    public static IDisposable Push(long? telegramUserId)
    {
        var previous = Ambient.Value;
        Ambient.Value = telegramUserId is > 0 ? telegramUserId : null;
        return new PopWhenDisposed(previous);
    }

    /// <summary>Restores the enclosing actor for this asynchronous execution context.</summary>
    private sealed class PopWhenDisposed : IDisposable
    {
        /// <summary>Actor that was ambient before this scope was pushed.</summary>
        private readonly long? _previous;

        /// <summary>Idempotency flag preventing double restoration.</summary>
        private int _disposed;

        /// <summary>Creates the scope that restores the enclosing actor.</summary>
        /// <param name="previous">Actor that was ambient before this scope was pushed.</param>
        public PopWhenDisposed(long? previous) => _previous = previous;

        /// <summary>Restores the previous actor exactly once.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Ambient.Value = _previous;
        }
    }
}
