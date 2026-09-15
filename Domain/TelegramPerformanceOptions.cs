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

    /// <summary>
    /// Maximum critical-priority output jobs held in memory: financial outbox results that need a real Telegram message.
    /// Production default: 32.
    /// </summary>
    /// <remarks>
    /// The lane is deliberately the smallest of the three. Financial delivery work is rare and must never be queued
    /// behind interactive menu traffic, so a tight bound keeps it isolated instead of letting a burst of menus occupy it.
    /// A rejected job is never lost: its durable SQLite row stays queued and is offered again by the next scheduling pass.
    /// </remarks>
    public int CriticalLaneCapacity { get; set; } = 32;

    /// <summary>
    /// Maximum ordinary interactive output jobs held in memory: menus, status replies and panel edits produced by
    /// customer handlers. Production default: 256.
    /// </summary>
    /// <remarks>
    /// Interactive replies are the customer-visible path, so this lane is larger than the critical lane and is preferred
    /// over the background lane whenever capacity exists.
    /// </remarks>
    public int InteractiveLaneCapacity { get; set; } = 256;

    /// <summary>
    /// Maximum background output jobs held in memory: operator logs, bulk broadcasts and best-effort notifications.
    /// Production default: 1000.
    /// </summary>
    /// <remarks>
    /// This is the only lane allowed to be large. Its jobs are the slowest to matter, so bulk sends cannot consume the
    /// interactive lane's capacity and delay a customer's button response.
    /// </remarks>
    public int BackgroundLaneCapacity { get; set; } = 1000;

    /// <summary>
    /// Maximum callback acknowledgements held in the dedicated realtime lane before further acknowledgements are
    /// refused. Production default: 256.
    /// </summary>
    /// <remarks>
    /// The acknowledgement lane is independent of every ordinary output lane. A refusal is a best-effort UX miss (the
    /// user keeps a spinner), never a business failure, and is reported with its rejection reason so a sustained overload
    /// is visible instead of silent.
    /// </remarks>
    public int CallbackAcknowledgementCapacity { get; set; } = 256;

    /// <summary>
    /// Transport budget in requests per second reserved exclusively for callback acknowledgements.
    /// Production default: 25, which is Telegram's documented per-bot ceiling.
    /// </summary>
    /// <remarks>
    /// The acknowledgement lane does not share the ordinary send pacing gate, so a large broadcast or reminder scan
    /// cannot consume the acknowledgement budget and delay a visible button response.
    /// </remarks>
    public int CallbackAcknowledgementPerSecond { get; set; } = 25;

    /// <summary>Rejects invalid startup limits before any receiver starts.</summary>
    /// <exception cref="InvalidOperationException">A configured limit is outside its supported range.</exception>
    /// <remarks>
    /// Every bound is validated at startup rather than clamped, because a typo that silently produced a zero-capacity
    /// lane would disable customer output while the process still looked healthy.
    /// </remarks>
    public void Validate()
    {
        if (WorkerCount is < 1 or > 64 || SendTimeoutSeconds is < 1 or > 30 || QueueSize is < 1 or > 10000)
            throw new InvalidOperationException("Invalid Performance:telegram limits (workers 1..64, timeout 1..30 seconds, queue 1..10000).");
        if (CriticalLaneCapacity is < 1 or > 4096)
            throw new InvalidOperationException("Invalid Performance:telegram critical lane capacity (1..4096).");
        if (InteractiveLaneCapacity is < 1 or > 65536)
            throw new InvalidOperationException("Invalid Performance:telegram interactive lane capacity (1..65536).");
        if (BackgroundLaneCapacity is < 1 or > 65536)
            throw new InvalidOperationException("Invalid Performance:telegram background lane capacity (1..65536).");
        if (CallbackAcknowledgementCapacity is < 1 or > 8192)
            throw new InvalidOperationException("Invalid Performance:telegram callback acknowledgement capacity (1..8192).");
        if (CallbackAcknowledgementPerSecond is < 1 or > 30)
            throw new InvalidOperationException("Invalid Performance:telegram callback acknowledgement rate (1..30 per second).");
    }
}

/// <summary>Groups optional performance settings without copying them into individual storefronts.</summary>
public sealed class PerformanceOptions
{
    /// <summary>Telegram delivery limits shared by all owned and tenant bots.</summary>
    public TelegramPerformanceOptions Telegram { get; set; } = new();
}
