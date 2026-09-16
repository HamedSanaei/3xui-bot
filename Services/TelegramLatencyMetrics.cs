using System.Collections.Generic;
using System.Diagnostics.Metrics;

/// <summary>
/// Process-wide instruments for the four interactive Telegram latency numbers an operator must be able to read without
/// correlating several log families.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// production latency complaints were triaged from differently named log fields, and the same value was reported under a
/// different key depending on which component observed it. Each instrument below carries exactly one name, so
/// <c>callback_received_ms</c>, <c>callback_ack_ms</c>, <c>handler_total_ms</c>, and <c>queue_wait_ms</c> mean the same
/// thing everywhere they are queried.
/// </para>
/// <para>
/// Units are milliseconds for every instrument. The names are intentionally snake_case with an explicit <c>_ms</c>
/// suffix so an exported dashboard series is unambiguous without a separate unit lookup.
/// </para>
/// <para>
/// Privacy and payload safety:
/// no instrument carries a tag or dimension. Nothing in this type can therefore expose a bot token, chat id, callback
/// payload, Telegram user id, or customer text. Per-bot and per-update attribution stays in structured log fields where
/// it is already redacted by the existing logging policy.
/// </para>
/// <para>
/// Cost and failure containment:
/// recording is a monotonic timestamp difference and a numeric observation. Every record call is wrapped so an
/// instrumentation listener fault can never surface as a customer-visible failure.
/// </para>
/// </remarks>
public static class TelegramLatencyMetrics
{
    /// <summary>Shared meter for the interactive Telegram latency family.</summary>
    private static readonly Meter Meter = new("Adminbot.TelegramLatency");

    /// <summary>
    /// Milliseconds between the receiver observing a callback update and offering its acknowledgement to the realtime
    /// lane.
    /// </summary>
    /// <remarks>
    /// This must stay sub-millisecond in production. A growing value means the acknowledgement is being delayed before it
    /// is even queued, which is the shape of the reported <c>telegram_callback_ack &gt; 2000ms</c> incident.
    /// </remarks>
    public static readonly Histogram<double> CallbackReceivedMs =
        Meter.CreateHistogram<double>("callback_received_ms", "ms");

    /// <summary>
    /// Milliseconds between the realtime lane accepting one callback acknowledgement and the Telegram call completing.
    /// </summary>
    /// <remarks>
    /// The lane is paced by its own transport budget, so this value is dominated by Telegram round-trip time rather than
    /// by ordinary output backlog. The target is under 500ms; the attempt itself is capped at one second.
    /// </remarks>
    public static readonly Histogram<double> CallbackAckMs =
        Meter.CreateHistogram<double>("callback_ack_ms", "ms");

    /// <summary>
    /// Milliseconds one Telegram update handler spent in business execution, measured from handler start to completion.
    /// </summary>
    /// <remarks>
    /// This is the whole-handler number, so it includes every nested database, panel, and delivery stage. A slow handler
    /// at this value is attributed by the closed-vocabulary stage log line, never by this instrument.
    /// </remarks>
    public static readonly Histogram<double> HandlerTotalMs =
        Meter.CreateHistogram<double>("handler_total_ms", "ms");

    /// <summary>
    /// Milliseconds a durably accepted Telegram update waited before its handler started.
    /// </summary>
    /// <remarks>
    /// Because each bot/user lane is strict FIFO, a high value means a predecessor handler in the same lane is still
    /// running. It is the signal that tells an operator whether a slow callback is caused by work or by waiting.
    /// </remarks>
    public static readonly Histogram<double> QueueWaitMs =
        Meter.CreateHistogram<double>("queue_wait_ms", "ms");

    /// <summary>
    /// Milliseconds a caller waited for its real Telegram result after the output job was durably committed.
    /// </summary>
    /// <remarks>
    /// This is a post-admission wait, not an admission cost: the durable write has already succeeded when the timer
    /// starts. It is the number that separates "the job could not be stored" from "the job was stored and the response
    /// was slow", which is what the uncertain-delivery complaint needed.
    /// </remarks>
    public static readonly Histogram<double> EnqueueWaitMs =
        Meter.CreateHistogram<double>("enqueue_wait_ms", "ms");

    /// <summary>
    /// Milliseconds between an output job being durably committed and a sender worker starting its attempt.
    /// </summary>
    /// <remarks>
    /// A high value means the job is waiting for a worker, a per-bot serialization slot, or the global transport pace,
    /// so it is the queue-side half of delivery latency and the value a caller deadline competes against.
    /// </remarks>
    public static readonly Histogram<double> WorkerStartDelayMs =
        Meter.CreateHistogram<double>("worker_start_delay_ms", "ms");

    /// <summary>
    /// Milliseconds spent inside the Telegram HTTP call itself, measured around the send and excluding claim, pacing,
    /// and status persistence.
    /// </summary>
    /// <remarks>
    /// Only this instrument measures Telegram's own latency. Comparing it with <see cref="WorkerStartDelayMs"/> tells an
    /// operator whether a slow delivery was Telegram's fault or the sender's queue.
    /// </remarks>
    public static readonly Histogram<double> TelegramApiMs =
        Meter.CreateHistogram<double>("telegram_api_ms", "ms");

    /// <summary>
    /// Counts which token ended an output attempt that did not reach a definite outcome.
    /// </summary>
    /// <remarks>
    /// The only dimension is a <c>source</c> tag drawn from the closed vocabulary in
    /// <see cref="TelegramCancellationSources"/>: no bot id, chat id, update id, payload, or token ever appears on this
    /// instrument. It exists because "the send was cancelled" is not actionable until an operator can tell a caller
    /// deadline from host shutdown from the sender's own transport deadline.
    /// </remarks>
    public static readonly Counter<long> CancellationSource =
        Meter.CreateCounter<long>("telegram_delivery_cancellation_total");

    /// <summary>Records one cancellation attribution against the closed-vocabulary source.</summary>
    /// <param name="source">
    /// One value of <see cref="TelegramCancellationSources"/>; an unmapped value is recorded as <c>unknown</c> so the
    /// vocabulary cannot be widened by a caller typo or a future code path.
    /// </param>
    /// <remarks>
    /// Recording is best-effort and never throws, exactly like <see cref="Record"/>.
    /// </remarks>
    public static void RecordCancellation(string source)
    {
        try
        {
            CancellationSource.Add(1, new KeyValuePair<string, object>("source",
                TelegramCancellationSources.IsKnown(source) ? source : TelegramCancellationSources.Unknown));
        }
        catch
        {
            // A diagnostics listener must never break the delivery path it is measuring.
        }
    }

    /// <summary>Records one observation while guaranteeing that instrumentation can never fail a caller.</summary>
    /// <param name="instrument">One of the four latency instruments declared by this type; null is ignored.</param>
    /// <param name="milliseconds">Observed duration in milliseconds. Negative values are ignored as impossible input.</param>
    /// <remarks>
    /// Every recorder funnels through here so a metrics listener that throws cannot abort a customer's callback lane or a
    /// handler's final persistence step.
    /// </remarks>
    /// <example>
    /// <code>
    /// TelegramLatencyMetrics.Record(TelegramLatencyMetrics.CallbackAckMs, elapsed.TotalMilliseconds);
    /// </code>
    /// </example>
    public static void Record(Histogram<double> instrument, double milliseconds)
    {
        if (instrument == null || milliseconds < 0)
            return;

        try
        {
            instrument.Record(milliseconds);
        }
        catch
        {
            // A diagnostics listener must never break the interactive path it is measuring.
        }
    }
}
