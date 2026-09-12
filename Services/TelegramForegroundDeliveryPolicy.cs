using System;

/// <summary>
/// Immutable latency budget for ONE non-durable interactive Telegram delivery performed while a customer is waiting
/// inside an update lane.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// Telegram.Bot's default transport timeout is provider-oriented and far too long for an interactive response. A
/// navigation reply, a menu, an ordinary prompt, or a membership probe that waits on that default keeps the customer's
/// strict FIFO lane occupied and makes every later update from the same bot/user key wait, which is exactly the
/// head-of-line blocking seen in production (one tenant <c>/start</c> handler held its lane for about 61 seconds).
/// </para>
/// <para>
/// Scope:
/// This policy deliberately covers only the interactive UX surface. It is NOT a transport-wide timeout and must never
/// be applied globally to <see cref="Telegram.Bot.TelegramBotClient"/>, because the same client instance also serves
/// long-polling receivers and durable background workers. Durable business delivery — account delivery, payment
/// settlement notices, owner receipt notifications, and the durable tenant notification outbox — keeps its own
/// outbox and <c>delivery_uncertain</c> semantics and is never converted into best-effort foreground sending.
/// </para>
/// <para>
/// Ambiguous-send rule:
/// When this budget expires the request may still have been accepted by Telegram. A timed-out foreground send must
/// therefore NEVER be retried automatically: the decorator abandons the call, surfaces
/// <see cref="TelegramForegroundDeliveryTimeoutException"/>, and the handler records a stable non-error outcome. The
/// customer retries by pressing the button again, which produces a new update and a new decision.
/// </para>
/// <para>
/// Production value:
/// <see cref="Production"/> uses a single overall eight-second budget. The value is intentionally not bound to
/// <c>configuration.json</c>: it is a UX guarantee, not an operator-tunable provider setting, and the hosting
/// environment must not be able to raise interactive replies back to pathological values. Tests construct their own
/// instance with millisecond budgets so timeout behaviour is deterministic.
/// </para>
/// </remarks>
public sealed class TelegramForegroundDeliveryPolicy
{
    /// <summary>
    /// Gets the shared production instance. One overall eight-second budget covers a single interactive Telegram
    /// delivery, including connection setup, upload, and response reading.
    /// </summary>
    public static TelegramForegroundDeliveryPolicy Production { get; } = new();

    /// <summary>
    /// Gets the overall wall-clock budget for one interactive foreground Telegram delivery. Production value: eight
    /// seconds.
    /// </summary>
    /// <remarks>
    /// This is one budget per delivery, not per retry, and no retry is performed when it expires. Only
    /// <see cref="TelegramForegroundDeliveryPolicy"/>-aware interactive calls are affected; durable outbox delivery
    /// and receiver polling keep their own configured behavior.
    /// </remarks>
    public TimeSpan OverallBudget { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>
/// Raised when one non-durable interactive Telegram delivery exceeded
/// <see cref="TelegramForegroundDeliveryPolicy.OverallBudget"/> before Telegram answered.
/// </summary>
/// <remarks>
/// The send is abandoned and never re-sent automatically because Telegram may already have accepted the request.
/// The type derives from <see cref="TimeoutException"/> so generic update handling already recognises it as an
/// external timeout, and the message carries only a closed-vocabulary request kind — never a chat id, customer text,
/// callback payload, bot token, or Telegram response body.
/// </remarks>
public sealed class TelegramForegroundDeliveryTimeoutException : TimeoutException
{
    /// <summary>
    /// Creates the typed foreground delivery timeout for one closed-vocabulary request kind.
    /// </summary>
    /// <param name="requestKind">
    /// Closed-vocabulary classification of the abandoned request, for example <c>send_message</c> or
    /// <c>edit_message_text</c>. It is derived from the request type, never from customer input.
    /// </param>
    /// <param name="budget">The overall budget that expired. Exposed only as a duration, never as a secret.</param>
    public TelegramForegroundDeliveryTimeoutException(string requestKind, TimeSpan budget)
        : base($"Telegram foreground delivery '{requestKind}' exceeded its overall {budget.TotalSeconds:0.###} second budget.")
    {
        RequestKind = requestKind;
        Budget = budget;
    }

    /// <summary>Gets the closed-vocabulary request kind that was abandoned.</summary>
    public string RequestKind { get; }

    /// <summary>Gets the overall foreground delivery budget that expired.</summary>
    public TimeSpan Budget { get; }
}
