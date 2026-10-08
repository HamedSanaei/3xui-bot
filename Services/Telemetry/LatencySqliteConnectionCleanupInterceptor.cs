using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Adminbot.Services.Telemetry;

/// <summary>Ends orphaned SQLite begin and lifetime measurements after EF closes or disposes their connection.</summary>
/// <param name="observer">Bounded provider-boundary observer shared with its transaction interceptor.</param>
/// <param name="lifetime">Enabled-only identity/clock lifetime observer shared with the same transaction interceptor.</param>
/// <remarks>EF can omit begin failure notifications. Connection completion releases diagnostic ownership only and explicitly marks any unavailable exact transaction endpoint; intercepted resource cleanup is never suppressed.</remarks>
internal sealed class LatencySqliteConnectionCleanupInterceptor(LatencySqliteObserver observer,
    LatencySqliteTransactionLifetimeObserver lifetime) : DbConnectionInterceptor
{
    /// <summary>Releases diagnostic handles after synchronous connection closure actually completes.</summary>
    /// <param name="connection">Existing provider connection, never inspected or retained.</param>
    /// <param name="eventData">EF-generated connection completion ownership metadata.</param>
    /// <remarks>A suppressed or failed closing attempt does not fabricate a transaction lifetime endpoint.</remarks>
    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {
        observer.AbortConnection(eventData.ConnectionId);
        lifetime?.CompleteConnection(eventData.ConnectionId);
    }

    /// <summary>Releases diagnostic handles after asynchronous connection closure actually completes.</summary>
    /// <param name="connection">Existing provider connection, never inspected or retained.</param>
    /// <param name="eventData">EF-generated connection completion ownership metadata.</param>
    /// <returns>A completed notification task, without altering provider cancellation or ownership.</returns>
    /// <remarks>Only diagnostic state for this exact opaque connection id is released.</remarks>
    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        observer.AbortConnection(eventData.ConnectionId);
        lifetime?.CompleteConnection(eventData.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Releases diagnostic handles after synchronous connection disposal actually completes.</summary>
    /// <param name="connection">Existing provider connection, never inspected or retained.</param>
    /// <param name="eventData">EF-generated connection completion ownership metadata.</param>
    /// <remarks>Repeated close/dispose and precise earlier transaction-disposed notifications are idempotent.</remarks>
    public override void ConnectionDisposed(DbConnection connection, ConnectionEndEventData eventData)
    {
        observer.AbortConnection(eventData.ConnectionId);
        lifetime?.CompleteConnection(eventData.ConnectionId);
    }

    /// <summary>Releases diagnostic handles after asynchronous connection disposal actually completes.</summary>
    /// <param name="connection">Existing provider connection, never inspected or retained.</param>
    /// <param name="eventData">EF-generated connection completion ownership metadata.</param>
    /// <returns>A completed notification task; financial and provider cleanup behavior is unchanged.</returns>
    /// <remarks>A failed or suppressed disposal is not reported as a completed lifetime.</remarks>
    public override Task ConnectionDisposedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        observer.AbortConnection(eventData.ConnectionId);
        lifetime?.CompleteConnection(eventData.ConnectionId);
        return Task.CompletedTask;
    }
}
