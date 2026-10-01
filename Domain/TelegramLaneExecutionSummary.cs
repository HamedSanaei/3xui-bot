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
/// with the execution contributing the largest positive overlap to its accepted-to-started wait.
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

    /// <summary>Gets the victim's actual UTC execution start at which this predecessor was observed.</summary>
    /// <remarks>Required diagnostic snapshot endpoint; supplies a truthful elapsed duration when completion is still null.</remarks>
    public required DateTime ObservedAtUtc { get; set; }

    /// <summary>Gets the positive overlap with the victim's accepted-to-actual-start wait, in milliseconds.</summary>
    /// <remarks>Both execution endpoints are clipped to the victim's wait; a running execution ends at the victim's start for this comparison.</remarks>
    public double BlockingOverlapMs { get; set; }

    /// <summary>
    /// Gets the previous execution's duration in milliseconds, ending at its terminal time or this observation.
    /// </summary>
    /// <remarks>
    /// Completed receipts retain their full duration. A still-running predecessor reports elapsed time up to
    /// <see cref="ObservedAtUtc" />, not a misleading zero. Blocking overlap is separately clipped to the victim wait.
    /// </remarks>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public double HandlerDurationMs => Math.Max(0, ((CompletedAtUtc ?? ObservedAtUtc) - StartedAtUtc).TotalMilliseconds);
}
