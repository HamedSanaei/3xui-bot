using System;
using System.Collections.Generic;

/// <summary>
/// Closed vocabulary for the token that ended one Telegram output attempt without a definite outcome.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// a delivery log line that only says <c>Status=uncertain</c> cannot be acted on, because the sender is cancelled by
/// several unrelated tokens. The value recorded here is the difference between "a customer's own request was abandoned
/// while the durable job stayed alive", "the host is shutting down and Telegram's answer was genuinely never seen", and
/// "the sender's own transport deadline expired". Only the middle case is real delivery uncertainty.
/// </para>
/// <para>
/// Privacy:
/// every member is a fixed compile-time string with no bot id, chat id, user id, update id, callback payload, URL, or
/// token. This value is safe to use as a metric dimension and as a structured log field.
/// </para>
/// <para>
/// Extensibility:
/// the set is validated at runtime so a caller typo or a future code path cannot silently widen the vocabulary an
/// operator already dashboards.
/// </para>
/// </remarks>
public static class TelegramCancellationSources
{
    /// <summary>The attempt observed no cancellation at all; used when an outcome is definite.</summary>
    public const string None = "none";

    /// <summary>
    /// The caller that admitted the job stopped waiting (its lane token, or a handler-scoped deadline) after the durable
    /// write. The job itself is unaffected and its delivery may still succeed.
    /// </summary>
    public const string CallerToken = "caller_token";

    /// <summary>The host is stopping, so the sender's own shutdown token ended the attempt.</summary>
    public const string HostShutdown = "host_shutdown";

    /// <summary>The sender's per-attempt transport deadline expired before Telegram answered.</summary>
    public const string SendTimeout = "send_timeout";

    /// <summary>The job was abandoned before it reached Telegram because its caller owned streams that were released.</summary>
    public const string LiveCallerReleased = "live_caller_released";

    /// <summary>
    /// The attempt ended for a reason outside the vocabulary above, including an unmapped or null input.
    /// </summary>
    /// <remarks>
    /// A dedicated member is deliberate: an operator must never see an unlabeled dimension, and a new code path that
    /// forgets to classify itself is visible as a growing <c>unknown</c> series instead of disappearing into silence.
    /// </remarks>
    public const string Unknown = "unknown";

    /// <summary>All recognized values, used to keep metric dimensions inside the documented vocabulary.</summary>
    private static readonly HashSet<string> Recognized = new(StringComparer.Ordinal)
    {
        None, CallerToken, HostShutdown, SendTimeout, LiveCallerReleased, Unknown
    };

    /// <summary>Reports whether a value belongs to the closed vocabulary.</summary>
    /// <param name="source">Candidate value taken from a cancellation path; null and unknown spellings are rejected.</param>
    /// <returns>
    /// <c>true</c> only for an exactly matching member of this vocabulary, so a caller cannot add a metric dimension.
    /// </returns>
    /// <remarks>
    /// The comparison is ordinal, never culture-aware, because these values are identifiers rather than display text.
    /// </remarks>
    public static bool IsKnown(string source) => source != null && Recognized.Contains(source);
}
