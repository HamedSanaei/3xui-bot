/// <summary>Defines non-waiting admission before a scheduler assigns an update worker to panel work.</summary>
/// <remarks>Deferred from runtime activation. Callers must leave rejected work in a ready queue and wake it on release;
/// awaiting a panel semaphore inside an update handler would still consume a worker and defeat this contract.</remarks>
public interface IXuiPanelAdmission
{
    /// <summary>Attempts to reserve one of a stable panel's proposed four execution slots.</summary>
    /// <param name="panelKey">Required credential-free panel identity, shared by every bot targeting that panel.</param>
    /// <returns>A lease to dispose when panel work ends, or null immediately when the panel is full.</returns>
    /// <remarks>This policy is deliberately not registered in production. A future scheduler must reserve a panel slot before assigning a worker and reschedule rejected work on release.</remarks>
    IDisposable TryAcquire(string panelKey);
}

/// <summary>Provides bounded panel admission without storing waiters or holding update workers.</summary>
/// <remarks>This standalone implementation is tested but intentionally absent from production DI and HTTP transport.</remarks>
public sealed class XuiPanelAdmission : IXuiPanelAdmission
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
    private readonly int _limit;

    /// <summary>Creates an inactive admission policy with the proposed per-panel limit.</summary>
    /// <param name="limit">Maximum concurrent reservations per stable panel, from one to 256; defaults to four.</param>
    /// <exception cref="ArgumentOutOfRangeException">The proposed limit is outside the supported range.</exception>
    /// <remarks>This policy is deliberately not registered in production. A future scheduler must reserve a panel slot before assigning a worker and reschedule rejected work on release.</remarks>
    public XuiPanelAdmission(int limit = 4)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
    }

    /// <summary>Counts panels with live leases; historical panel keys are removed after release.</summary>
    public int ActivePanelCount { get { lock (_sync) return _active.Count; } }

    /// <inheritdoc />
    public IDisposable TryAcquire(string panelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelKey);
        lock (_sync)
        {
            var count = _active.GetValueOrDefault(panelKey);
            if (count >= _limit) return null;
            _active[panelKey] = count + 1;
            return new Lease(this, panelKey);
        }
    }

    /// <summary>Releases one acquired slot and removes the panel when it becomes idle.</summary>
    /// <param name="key">Exact non-secret panel key belonging to the acquired lease.</param>
    /// <remarks>This policy is deliberately not registered in production. A future scheduler must reserve a panel slot before assigning a worker and reschedule rejected work on release.</remarks>
    private void Release(string key)
    {
        lock (_sync)
        {
            if (_active[key] == 1) _active.Remove(key);
            else _active[key]--;
        }
    }

    /// <summary>Owns a panel slot and releases it at most once.</summary>
    /// <param name="owner">Admission instance that granted the slot.</param>
    /// <param name="key">Stable panel identity of the granted slot.</param>
    private sealed class Lease(XuiPanelAdmission owner, string key) : IDisposable
    {
        private int _disposed;
        /// <summary>Returns the reservation exactly once, including repeated cleanup paths.</summary>
        /// <remarks>This policy is deliberately not registered in production. A future scheduler must reserve a panel slot before assigning a worker and reschedule rejected work on release.</remarks>
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key); }
    }
}
