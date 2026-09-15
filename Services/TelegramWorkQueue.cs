using System.Collections.Concurrent;
using System.Threading.Channels;
using Adminbot.Domain;

/// <summary>Names the three bounded in-memory output lanes one delivery job can be offered to.</summary>
/// <remarks>
/// The lane is derived from <see cref="TelegramWorkPriority"/>, so callers keep using the existing priority concept and
/// only the transport decides how much of each kind of work may be held in memory.
/// </remarks>
public enum TelegramOutputLane
{
    /// <summary>Financial outbox results that need a real Telegram acknowledgement. Smallest and never blocked by others.</summary>
    Critical = 0,
    /// <summary>Menus, status replies and panel edits produced by a customer's interactive handler.</summary>
    Interactive = 1,
    /// <summary>Operator logs, bulk broadcasts and best-effort notifications.</summary>
    Background = 2
}

/// <summary>Outcome of offering one already-persisted job to a bounded output lane.</summary>
/// <param name="Accepted">True when the lane accepted the job; false means the job stays durably queued.</param>
/// <param name="Lane">Bounded lane the job was offered to, derived from its priority.</param>
/// <param name="RejectionReason">
/// Closed-vocabulary reason when <paramref name="Accepted"/> is false, otherwise an empty string. It never contains a
/// payload, chat id, callback text, or token, so it is safe to log verbatim.
/// </param>
public readonly record struct TelegramQueueAdmission(bool Accepted, TelegramOutputLane Lane, string RejectionReason)
{
    /// <summary>Creates an accepted admission for one lane.</summary>
    /// <param name="lane">Lane that accepted the job.</param>
    /// <returns>An accepted admission result.</returns>
    public static TelegramQueueAdmission AcceptedFor(TelegramOutputLane lane) => new(true, lane, string.Empty);

    /// <summary>Creates a refused admission carrying its closed-vocabulary reason.</summary>
    /// <param name="lane">Lane that refused the job.</param>
    /// <param name="reason">Closed-vocabulary rejection reason, never payload data.</param>
    /// <returns>A refused admission result.</returns>
    public static TelegramQueueAdmission RefusedFor(TelegramOutputLane lane, string reason) => new(false, lane, reason);
}

/// <summary>
/// Bounded asynchronous handoff of eligible per-bot output heads to sender workers, split into three isolated lanes.
/// </summary>
/// <remarks>
/// <para>
/// SQLite owns the backlog. Only eligible heads enter these channels; bot waiters never occupy workers. Splitting the
/// handoff into lanes is what keeps one class of traffic from consuming another's capacity: a broadcast storm fills the
/// background lane only, while a customer's menu reply and an existing financial outbox still have their own room.
/// </para>
/// <para>
/// Admission never blocks. Every offer is a non-blocking write, so a full lane can never stall the pump, the scheduler,
/// or the Telegram update handler. A refused job is not lost: its durable row remains <c>queued</c> in users.db and the
/// next scheduling pass offers it again once the lane drains.
/// </para>
/// <para>
/// Fairness: higher lanes are drained first, but the background lane is guaranteed a turn after a bounded run of
/// interactive jobs, so a sustained interactive burst cannot starve operator logs and bulk notifications forever.
/// </para>
/// </remarks>
public sealed class TelegramWorkQueue
{
    /// <summary>Interactive jobs served before the background lane is checked once, preventing permanent starvation.</summary>
    private const int InteractiveJobsPerBackgroundTurn = 8;

    private readonly Channel<TelegramDeliveryJob> _critical;
    private readonly Channel<TelegramDeliveryJob> _interactive;
    private readonly Channel<TelegramDeliveryJob> _background;

    /// <summary>Wake signal for blocked readers; one release per accepted job.</summary>
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>Refused offers per lane, exposed for diagnostics and regression verification.</summary>
    private readonly ConcurrentDictionary<TelegramOutputLane, long> _rejections = new();

    /// <summary>Configured in-memory capacity per lane, reported with every refusal so an operator sees the exact bound.</summary>
    private readonly IReadOnlyDictionary<TelegramOutputLane, int> _capacities;

    /// <summary>Creates the bounded per-lane memory handoff.</summary>
    /// <param name="options">
    /// Validated global Telegram performance configuration. Lane capacities are validated at startup by
    /// <see cref="TelegramPerformanceOptions.Validate"/>, so an invalid capacity is rejected before any receiver starts
    /// instead of silently disabling a lane.
    /// </param>
    /// <remarks>
    /// Each lane is a bounded channel with no waiting writers. <c>SingleWriter</c> is not asserted because callers may
    /// offer from several concurrent scopes, and <c>AllowSynchronousContinuations</c> stays off so a reader continuation
    /// can never run on a producer's thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The supplied options instance is null.</exception>
    public TelegramWorkQueue(TelegramPerformanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _critical = CreateLane(options.CriticalLaneCapacity);
        _interactive = CreateLane(options.InteractiveLaneCapacity);
        _background = CreateLane(options.BackgroundLaneCapacity);
        _capacities = new Dictionary<TelegramOutputLane, int>
        {
            [TelegramOutputLane.Critical] = options.CriticalLaneCapacity,
            [TelegramOutputLane.Interactive] = options.InteractiveLaneCapacity,
            [TelegramOutputLane.Background] = options.BackgroundLaneCapacity
        };
    }

    /// <summary>Creates one bounded lane that refuses rather than waits when full.</summary>
    /// <param name="capacity">Positive maximum number of jobs held in memory by the lane.</param>
    /// <returns>A bounded channel whose writer never blocks a caller.</returns>
    /// <remarks>
    /// <c>Wait</c> is the correct full mode here even though nothing ever waits: it is the only mode in which
    /// <c>TryWrite</c> reports refusal by returning <c>false</c>. The drop modes would return <c>true</c> while silently
    /// discarding the job, which would make a refused admission indistinguishable from an accepted one.
    /// </remarks>
    private static Channel<TelegramDeliveryJob> CreateLane(int capacity) =>
        Channel.CreateBounded<TelegramDeliveryJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    /// <summary>Maps a durable job priority to its bounded output lane.</summary>
    /// <param name="priority">Existing priority value: 0 critical, 1 normal/interactive, everything else background.</param>
    /// <returns>The lane that owns this job's memory capacity.</returns>
    private static TelegramOutputLane ResolveLane(int priority) => priority switch
    {
        (int)TelegramWorkPriority.Critical => TelegramOutputLane.Critical,
        (int)TelegramWorkPriority.Normal => TelegramOutputLane.Interactive,
        _ => TelegramOutputLane.Background
    };

    /// <summary>Gets the number of offers refused by one lane since process start.</summary>
    /// <param name="lane">Lane to inspect.</param>
    /// <returns>Refused offer count; zero when the lane never refused anything.</returns>
    /// <remarks>Used by diagnostics and regression tests to prove a full lane refuses instead of blocking.</remarks>
    public long RejectionCount(TelegramOutputLane lane) => _rejections.GetValueOrDefault(lane);

    /// <summary>Reads the configured bounded capacity of one lane.</summary>
    /// <param name="lane">Lane to inspect.</param>
    /// <returns>Maximum number of jobs the lane may hold in memory before it refuses further offers.</returns>
    public int CapacityOf(TelegramOutputLane lane) => _capacities[lane];

    /// <summary>Reads how many jobs one lane currently holds in memory.</summary>
    /// <param name="lane">Lane to inspect.</param>
    /// <returns>Current in-memory job count for the lane; zero when the lane is drained.</returns>
    /// <remarks>
    /// This is a bounded-channel count, not a durable backlog. The authoritative backlog depth is the queued row count in
    /// users.db, which the sender reports separately.
    /// </remarks>
    public int PendingCount(TelegramOutputLane lane) => Lane(lane).Reader.Count;

    /// <summary>Returns the channel that owns one lane's memory capacity.</summary>
    /// <param name="lane">Lane to resolve.</param>
    /// <returns>The bounded channel backing the lane.</returns>
    private Channel<TelegramDeliveryJob> Lane(TelegramOutputLane lane) => lane switch
    {
        TelegramOutputLane.Critical => _critical,
        TelegramOutputLane.Interactive => _interactive,
        _ => _background
    };

    /// <summary>Hands one already-persisted eligible job to its bounded lane without ever blocking the caller.</summary>
    /// <param name="job">
    /// Detached private output row already committed to users.db. It contains no runtime client or token; only its
    /// priority is read here.
    /// </param>
    /// <returns>
    /// An accepted admission, or a refusal carrying the closed-vocabulary rejection reason <c>lane_full</c>. A refused job
    /// is never dropped from the durable queue and is offered again by a later scheduling pass.
    /// </returns>
    /// <remarks>
    /// This method is synchronous by design: the recovery guarantee is the durable row, so the pump must not be able to
    /// block on backpressure. Waiting here would let an output backlog delay the scheduler that owns interactive work.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The supplied job is null.</exception>
    /// <example>
    /// <code>
    /// var admission = queue.TryEnqueue(job);
    /// if (!admission.Accepted) logger.LogWarning("Telegram output lane refused a job. Lane={Lane} Reason={Reason}", admission.Lane, admission.RejectionReason);
    /// </code>
    /// </example>
    public TelegramQueueAdmission TryEnqueue(TelegramDeliveryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var lane = ResolveLane(job.Priority);
        if (Lane(lane).Writer.TryWrite(job))
        {
            // A reader that missed the window re-checks its lanes after the wait, so a lost signal can delay a job but
            // can never strand it in the durable queue.
            try { _available.Release(); } catch (SemaphoreFullException) { }
            return TelegramQueueAdmission.AcceptedFor(lane);
        }

        _rejections.AddOrUpdate(lane, 1, (_, current) => current + 1);
        return TelegramQueueAdmission.RefusedFor(lane, "lane_full");
    }

    /// <summary>Streams eligible jobs until shutdown, draining higher lanes first with a bounded background guarantee.</summary>
    /// <param name="token">Worker shutdown cancellation.</param>
    /// <returns>A cancellable sequence with no polling or busy waiting.</returns>
    /// <remarks>
    /// Readers find work by draining the lanes in priority order and only then waiting on the wake signal, so a burst
    /// costs no additional scheduling. Because the signal is a counted semaphore, N accepted jobs wake N readers without
    /// an allocation per wait.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller's shutdown token was cancelled.</exception>
    public async IAsyncEnumerable<TelegramDeliveryJob> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var interactiveStreak = 0;
        while (!token.IsCancellationRequested)
        {
            if (_critical.Reader.TryRead(out var critical))
            {
                yield return critical;
                continue;
            }

            if (interactiveStreak < InteractiveJobsPerBackgroundTurn && _interactive.Reader.TryRead(out var interactive))
            {
                interactiveStreak++;
                yield return interactive;
                continue;
            }

            if (_background.Reader.TryRead(out var background))
            {
                interactiveStreak = 0;
                yield return background;
                continue;
            }

            // Nothing lower is pending, so the remaining interactive backlog is served even past the fairness cap.
            if (_interactive.Reader.TryRead(out var remaining))
            {
                interactiveStreak++;
                yield return remaining;
                continue;
            }

            await _available.WaitAsync(token);
        }
    }
}
