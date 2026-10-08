using System;
using System.Threading;

namespace Adminbot.Services.Telemetry;

/// <summary>Suppresses instrumentation of the telemetry writer and operator/logger notification sends.</summary>
/// <remarks>The scope flows across awaits but must be entered in an asynchronous dispatcher when work was queued elsewhere. Nesting is supported; suppression never changes the send itself or its retry policy.</remarks>
public static class LatencyTelemetrySuppression
{
    /// <summary>Ambient suppression depth for the current asynchronous execution flow.</summary>
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>Whether collection is suppressed for the current asynchronous execution flow.</summary>
    public static bool IsActive => Depth.Value != 0;

    /// <summary>Enters a nested instrumentation-suppression scope.</summary>
    /// <returns>A scope which must be disposed in the originating async flow after its awaits complete.</returns>
    /// <remarks>This prevents operator-send failures from creating more telemetry incidents about themselves.</remarks>
    /// <example><code>using (LatencyTelemetrySuppression.Enter()) { await SendOperatorSummaryAsync(); }</code></example>
    public static IDisposable Enter()
    {
        var previous = Depth.Value;
        Depth.Value = previous + 1;
        return new Scope(previous);
    }

    /// <summary>Restores the ambient depth once without owning any resources.</summary>
    private sealed class Scope : IDisposable
    {
        /// <summary>Depth observed before this scope was entered.</summary>
        private readonly int _previous;
        /// <summary>One-time disposal guard for this scope.</summary>
        private bool _disposed;
        /// <summary>Creates a scope restoring the previous ambient nesting depth.</summary>
        /// <param name="previous">Nonnegative depth from the originating asynchronous execution flow.</param>
        public Scope(int previous) => _previous = previous;
        /// <summary>Restores the previous depth; repeated disposal has no effect.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Depth.Value = _previous;
        }
    }
}
