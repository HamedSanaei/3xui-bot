/// <summary>Ensures a stopped polling generation terminates before its replacement can start.</summary>
public static class TelegramReceiverLifetime
{
    /// <summary>Observes termination without treating the previous generation's failure as the new one's failure.</summary>
    /// <param name="previous">Previous receiver task for the same bot, or null for first startup.</param>
    /// <param name="token">Cancellation of the replacement startup, independent of the previous receiver's token.</param>
    /// <returns>A task completing only after the predecessor terminates; startup cancellation is propagated.</returns>
    /// <remarks>The caller must hold its existing per-bot lifecycle gate throughout observation and receiver registration.</remarks>
    /// <example><code>await TelegramReceiverLifetime.ObservePreviousAsync(previous, token);</code></example>
    public static async Task ObservePreviousAsync(Task previous, CancellationToken token)
    {
        if (previous != null)
        {
            try { await previous.WaitAsync(token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception) when (previous.IsFaulted) { }
        }
        token.ThrowIfCancellationRequested();
    }
}
