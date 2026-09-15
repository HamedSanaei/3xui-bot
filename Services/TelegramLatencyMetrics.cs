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
