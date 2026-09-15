namespace Adminbot.Domain;

/// <summary>Startup limits for Telegram output; independent of durable business execution limits.</summary>
public sealed class TelegramPerformanceOptions
{
    /// <summary>Maximum simultaneous ordinary sends across bots; each bot has one active send.</summary>
    public int WorkerCount { get; set; } = 4;
    /// <summary>Per-attempt send/edit deadline in seconds, excluding durable queue wait.</summary>
    public int SendTimeoutSeconds { get; set; } = 5;
    /// <summary>Maximum output jobs materialized in the scheduling channel, not a durable retention limit.</summary>
    public int QueueSize { get; set; } = 1000;
    /// <summary>Enables a separate, bounded realtime lane exclusively for callback acknowledgements.</summary>
    public bool CallbackAckImmediately { get; set; } = true;

    /// <summary>Rejects invalid startup limits before any receiver starts.</summary>
    /// <exception cref="InvalidOperationException">A configured limit is outside its supported range.</exception>
    public void Validate()
    {
        if (WorkerCount is < 1 or > 64 || SendTimeoutSeconds is < 1 or > 30 || QueueSize is < 1 or > 10000)
            throw new InvalidOperationException("Invalid Performance:telegram limits (workers 1..64, timeout 1..30 seconds, queue 1..10000).");
    }
}

/// <summary>Groups optional performance settings without copying them into individual storefronts.</summary>
public sealed class PerformanceOptions
{
    /// <summary>Telegram delivery limits shared by all owned and tenant bots.</summary>
    public TelegramPerformanceOptions Telegram { get; set; } = new();
}
