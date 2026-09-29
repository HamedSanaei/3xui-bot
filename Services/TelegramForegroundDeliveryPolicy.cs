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
/// Production values:
/// Ordinary foreground calls keep the eight-second UX budget. Media-group and document uploads have a separate
/// 24-second ceiling: multipart files need time to transfer before Telegram can acknowledge them. Neither deadline
/// triggers an automatic resend. Tests can set both budgets independently without waiting for production deadlines.
/// </para>
/// </remarks>
public sealed class TelegramForegroundDeliveryPolicy
{
    /// <summary>
    /// Gets the shared production instance. Ordinary interactive calls have eight seconds; multipart uploads
    /// have 24 seconds to transfer the files and receive Telegram's response.
    /// </summary>
    public static TelegramForegroundDeliveryPolicy Production { get; } = new();

    /// <summary>
    /// Gets the overall wall-clock budget for one ordinary interactive foreground Telegram delivery. Production
    /// value: eight seconds; media groups and documents use <see cref="MediaGroupBudget"/> instead.
    /// </summary>
    /// <remarks>
    /// This is one budget per delivery, not per retry, and no retry is performed when it expires. Only
    /// foreground-decorated calls are affected; durable outbox delivery and receiver polling are unchanged.
    /// </remarks>
    public TimeSpan OverallBudget { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Gets the bounded wall-clock budget for a foreground media-group or document upload, including transfer
    /// and response. Production value: 24 seconds.
    /// </summary>
    /// <remarks>
    /// Multipart uploads need longer than a text message, but still have a finite deadline so the customer's FIFO
    /// update lane cannot wait on the transport indefinitely. A timed-out upload may have been accepted by Telegram
    /// and must not be resent automatically.
    /// </remarks>
    public TimeSpan MediaGroupBudget { get; init; } = TimeSpan.FromSeconds(24);
}

/// <summary>
/// Raised when one non-durable interactive Telegram delivery exceeded its selected
/// <see cref="TelegramForegroundDeliveryPolicy"/> budget before Telegram answered.
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
