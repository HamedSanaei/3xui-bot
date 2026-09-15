using System.Threading.Channels;
using Adminbot.Domain;

/// <summary>Bounded asynchronous handoff of eligible per-bot output heads to sender workers.</summary>
/// <remarks>SQLite owns the backlog. Only eligible heads enter this channel; bot waiters never occupy workers.</remarks>
public sealed class TelegramWorkQueue
{
    private readonly Channel<TelegramDeliveryJob> _channel;

    /// <summary>Creates the bounded memory handoff.</summary>
    /// <param name="options">Validated global Telegram performance configuration.</param>
    public TelegramWorkQueue(TelegramPerformanceOptions options)
        => _channel = Channel.CreateBounded<TelegramDeliveryJob>(new BoundedChannelOptions(options.QueueSize)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, AllowSynchronousContinuations = false });

    /// <summary>Hands one already-persisted eligible job to a worker.</summary>
    /// <param name="job">Detached private output row, containing no runtime client or token.</param>
    /// <param name="token">Coordinator shutdown cancellation.</param>
    /// <returns>Completion when capacity accepts the job; cancellation never removes its durable row.</returns>
    public ValueTask EnqueueAsync(TelegramDeliveryJob job, CancellationToken token) => _channel.Writer.WriteAsync(job, token);

    /// <summary>Streams eligible jobs until shutdown.</summary>
    /// <param name="token">Worker shutdown cancellation.</param>
    /// <returns>A cancellable sequence with no polling or busy waiting.</returns>
    public IAsyncEnumerable<TelegramDeliveryJob> ReadAllAsync(CancellationToken token) => _channel.Reader.ReadAllAsync(token);
}
