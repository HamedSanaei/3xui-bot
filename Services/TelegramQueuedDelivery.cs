/// <summary>Explicit admission-only scope for replies whose Telegram response is deliberately unused.</summary>
/// <remarks>The builder executes immediately, including owner-panel transformations; no scoped handler or closure
/// escapes to a worker. Only allowlisted request payloads are snapshotted. Financial delivery must not use this API.</remarks>
public static class TelegramQueuedDelivery
{
    private static readonly AsyncLocal<bool> Admission = new();
    /// <summary>Whether the current explicit reply builder needs durable admission rather than a Telegram result.</summary>
    internal static bool AdmissionOnly => Admission.Value;

    /// <summary>Persists a normal menu reply and returns without waiting for Telegram networking.</summary>
    /// <param name="buildRequest">Immediate SendMessage/EditMessage/DeleteMessage invocation; must not inspect its result.</param>
    /// <returns>Completion after durable output admission, or ordinary delivery for clients outside the scheduler.</returns>
    /// <remarks>Never wrap an entire business workflow. The caller must not depend on delivery success or a message id.</remarks>
    /// <example><code>await TelegramQueuedDelivery.EnqueueAsync(() => client.SendMessage(chatId, "Menu"));</code></example>
    public static async Task EnqueueAsync(Func<Task> buildRequest)
    {
        var previous = Admission.Value;
        Admission.Value = true;
        try { await buildRequest(); }
        finally { Admission.Value = previous; }
    }
}
