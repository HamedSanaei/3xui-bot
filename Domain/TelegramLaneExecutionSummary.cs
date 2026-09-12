using System;

/// <summary>
/// Payload-free metadata for one previously executed Telegram update in the same bot/user FIFO lane.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// A queue-wait warning only names the waiting update, so production diagnostics showed four victim updates waiting
/// 84, 43, 23, and 19 seconds without naming the single slow handler that caused them. Because a waiting row only
/// becomes eligible after its predecessor turns terminal, the blocker is usually already completed by the time the
/// victim starts, so querying currently running work is not enough. This summary lets the scheduler correlate a wait
/// with the exact previous execution whose interval overlapped it.
/// </para>
/// <para>
/// Privacy:
/// The projection deliberately excludes the serialized Telegram payload, message text, callback data, and any
/// customer or financial value. Only identifiers already present in scheduler telemetry are exposed.
/// </para>
/// </remarks>
public sealed class TelegramLaneExecutionSummary
{
    /// <summary>Gets the internal inbox sequence of the previous execution.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets the Telegram update id of the previous execution.</summary>
    public int UpdateId { get; set; }

    /// <summary>Gets the Telegram update type label of the previous execution.</summary>
    public string UpdateType { get; set; }

    /// <summary>Gets the UTC claim time at which the previous execution started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Gets the UTC terminal time of the previous execution, or null when it is still running.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Gets the previous execution's handler duration in milliseconds, computed from its claim and terminal times.
    /// </summary>
    /// <remarks>
    /// The value is zero when the previous execution has not reached a terminal state yet; callers should treat that as
    /// "still running" and report the live blocker sequence without a duration.
    /// </remarks>
    public double HandlerDurationMs => CompletedAtUtc is { } completed
        ? Math.Max(0, (completed - StartedAtUtc).TotalMilliseconds)
        : 0;
}
