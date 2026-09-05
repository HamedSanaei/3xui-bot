/// <summary>Links durable business operations to an inbox execution without storing Telegram payloads or clients.</summary>
public static class TelegramUpdateExecutionScope
{
    private static readonly AsyncLocal<long?> Sequence = new();
    /// <summary>Current users.db inbox sequence, or null for independent webhook and background work.</summary>
    public static long? CurrentSequence => Sequence.Value;

    /// <summary>Establishes an execution reference and restores the previous reference when disposed.</summary>
    /// <param name="sequence">Positive internal inbox sequence; this is not a Telegram update or user id.</param>
    /// <returns>A scope that the scheduler must dispose in every success, failure, and cancellation path.</returns>
    /// <remarks>The reference is diagnostic linkage only. Wallet idempotency still uses the stable business event key.</remarks>
    /// <example><code>using var scope = TelegramUpdateExecutionScope.Push(item.Sequence);</code></example>
    public static IDisposable Push(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        var previous = Sequence.Value; Sequence.Value = sequence;
        return new Restore(previous);
    }

    /// <summary>Restores the parent async execution reference once.</summary>
    /// <param name="previous">Nullable parent inbox sequence captured by Push.</param>
    private sealed class Restore(long? previous) : IDisposable
    {
        private int _disposed;
        /// <summary>Restores the previous async-local reference; repeated cleanup is harmless.</summary>
        /// <remarks>Dispose in the same asynchronous execution that established the scope.</remarks>
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Sequence.Value = previous; }
    }
}
