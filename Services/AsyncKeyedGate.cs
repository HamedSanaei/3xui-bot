/// <summary>Provides short-lived in-process coordination for one business resource, never the whole application.</summary>
/// <remarks>Durable database operation keys remain the correctness boundary across restarts. Idle keys are removed.</remarks>
public sealed class AsyncKeyedGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Current resources with holders or waiters; idle entries are removed immediately.</summary>
    public int ActiveResourceCount { get { lock (_sync) return _entries.Count; } }

    /// <summary>Attempts to acquire one resource immediately without creating a waiting task.</summary>
    /// <param name="key">Required stable non-secret resource identity.</param>
    /// <returns>An exclusive disposable lease, or null when that resource is already held.</returns>
    /// <remarks>The zero-timeout semaphore probe never waits; unrelated invoice keys remain eligible.</remarks>
    /// <example><code>using var lease = gate.TryEnter(paymentKey); if (lease == null) return;</code></example>
    public IDisposable TryEnter(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry)) _entries.Add(key, entry = new());
            if (!entry.Gate.Wait(0)) return null;
            entry.References++;
            return new Lease(this, key, entry);
        }
    }

    /// <summary>Acquires the named resource without retaining a lock for historical users or orders.</summary>
    /// <param name="key">Required non-secret stable resource id, such as provider/payment or tenant/order id.</param>
    /// <param name="token">Cancellation while waiting for another operation on the same resource.</param>
    /// <returns>An exclusive lease that must be disposed after the operation, including exceptional exits.</returns>
    /// <remarks>Different keys proceed independently; no ordering claim is made beyond mutual exclusion.</remarks>
    /// <example><code>using var lease = await gate.EnterAsync(orderId, token);</code></example>
    public async Task<IDisposable> EnterAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry)) _entries.Add(key, entry = new());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(token); }
        catch { Release(key, entry, acquired: false); throw; }
        return new Lease(this, key, entry);
    }

    /// <summary>Releases a waiter reference and removes an idle resource under the registry lock.</summary>
    /// <param name="key">Resource id belonging to the lease.</param>
    /// <param name="entry">Exact registry entry whose reference is released.</param>
    /// <param name="acquired">Whether this reference acquired the semaphore.</param>
    /// <remarks>Registry mutation is protected by a short in-memory lock. The last holder or waiter removes and disposes the idle entry.</remarks>
    private void Release(string key, Entry entry, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) entry.Gate.Release();
            if (--entry.References == 0) { _entries.Remove(key); entry.Gate.Dispose(); }
        }
    }
    /// <summary>Tracks holders and waiters so cancellation cannot remove a semaphore still in use.</summary>
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int References;
    }
    /// <summary>Idempotently releases one acquired resource reference.</summary>
    private sealed class Lease(AsyncKeyedGate owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;
        /// <summary>Releases at most once, including repeated cleanup paths.</summary>
        /// <remarks>Registry mutation is protected by a short in-memory lock. The last holder or waiter removes and disposes the idle entry.</remarks>
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key, entry, true); }
    }
}
