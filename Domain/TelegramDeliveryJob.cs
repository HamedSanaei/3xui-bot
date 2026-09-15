namespace Adminbot.Domain;

/// <summary>Durable output intent, separate from financial operations and their existing delivery outboxes.</summary>
/// <remarks>Contains sensitive Bot API JSON but never a bot token. Terminal payloads are erased immediately.
/// An interrupted send is uncertain and is never automatically replayed; queued jobs survive restart.</remarks>
public sealed class TelegramDeliveryJob
{
    /// <summary>Monotonic local acceptance sequence; also orders same-bot output.</summary>
    public long Id { get; set; }
    /// <summary>Random correlation for a live waiter; never used as a financial operation key.</summary>
    public string CompletionKey { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>True when a workflow requires the real response; a restart cancels an unstarted attempt.</summary>
    public bool RequiresLiveCaller { get; set; }
    /// <summary>Internal runtime bot identity resolved afresh for delivery.</summary>
    public string BotId { get; set; }
    /// <summary>Allowlisted Bot API request type, never an arbitrary runtime type name.</summary>
    public string Kind { get; set; }
    /// <summary>Private request JSON; excluded from diagnostics and cleared at terminal state.</summary>
    public string Payload { get; set; }
    /// <summary>0 critical, 1 normal, 2 low; FIFO is maintained within each bot/priority.</summary>
    public int Priority { get; set; } = 1;
    /// <summary>queued, sending, sent, failed or uncertain; only queued may be retried.</summary>
    public string Status { get; set; } = "queued";
    /// <summary>UTC time of successful local admission.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Earliest UTC attempt time after a definite Telegram 429 rejection.</summary>
    public DateTime NotBeforeUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Number of requests actually attempted, bounded to three for 429 responses.</summary>
    public int Attempts { get; set; }
}
