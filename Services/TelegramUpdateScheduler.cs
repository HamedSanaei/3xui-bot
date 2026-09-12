using Adminbot.Domain;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>Accepts durable bounded updates independently of handler execution.</summary>
public interface ITelegramUpdateScheduler
{
    /// <summary>Closes new admission and wakes blocked receivers before shutdown draining starts.</summary>
    /// <remarks>Called by the receiver host before it cancels and joins its tracked polling generations.</remarks>
    void StopAdmission();
    /// <summary>Waits for capacity and durable acceptance, never for the customer's full handler.</summary>
    /// <param name="botId">Required configured runtime bot id.</param>
    /// <param name="update">Required private Telegram update.</param>
    /// <param name="cancellationToken">Receiver cancellation before durable acceptance.</param>
    /// <returns>A task completing only after persistence or duplicate recognition.</returns>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    Task EnqueueAsync(string botId, Update update, CancellationToken cancellationToken);
}

/// <summary>Resolves current bot runtime state and executes one isolated update.</summary>
public interface ITelegramUpdateExecutor
{
    /// <summary>Determines whether a configured bot can currently execute accepted work.</summary>
    /// <param name="botId">Canonical internal bot id.</param>
    /// <returns>False for disabled or unavailable bots; their queued work remains deferred.</returns>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    bool IsAvailable(string botId);
    /// <summary>Executes one claimed update with its own bot context.</summary>
    /// <param name="item">Private claimed update and its durable identity.</param>
    /// <param name="cancellationToken">Handler cancellation after the drain deadline.</param>
    /// <returns>A task representing all handler work; exceptions must be observed by the scheduler.</returns>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    Task ExecuteAsync(TelegramUpdateWorkItem item, CancellationToken cancellationToken);
}

/// <summary>Runs bounded, fair, per-bot/user FIFO scheduling over the durable inbox.</summary>
/// <remarks>
/// Only the coordinator owns the active-task collection. FIFO ordering applies only while work is queued or running.
/// A terminal failure or business recovery incident never prevents the same user from executing a later update.
/// </remarks>
public sealed class TelegramUpdateScheduler : ITelegramUpdateScheduler, IHostedService, IDisposable
{
    private static readonly Meter Meter = new("Adminbot.TelegramUpdates");
    private static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>("telegram.update.queue.wait", "ms");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("telegram.update.handler.duration", "ms");
    private static readonly Histogram<int> QueueDepth = Meter.CreateHistogram<int>("telegram.update.queue.depth", "updates");
    private readonly TelegramUpdateInboxStore _store;
    /// <summary>Coalesced readiness optimization; the durable ready query remains authoritative.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);
    private static readonly Counter<long> Wakeups = Meter.CreateCounter<long>("telegram.update.scheduler.wakeups");
    private static readonly Counter<long> RecoveryScans = Meter.CreateCounter<long>("telegram.update.scheduler.recovery_scans");
    private static readonly Counter<long> ReadyQueries = Meter.CreateCounter<long>("telegram.update.scheduler.ready_queries");
    private long _readyQueryCount;
    /// <summary>Total durable ready queries for diagnostics and deterministic idle tests.</summary>
    public long ReadyQueryCount => Interlocked.Read(ref _readyQueryCount);
    /// <summary>Recovery timeout used only when no post-commit/runtime wake arrives.</summary>
    internal TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Handler duration that counts as a genuinely slow interactive update. Production value: five seconds.
    /// </summary>
    /// <remarks>
    /// The value is recorded as a diagnostic at completion. It is intentionally not a scheduling limit: strict FIFO is
    /// unchanged. Tests override it with a millisecond value so a slow-handler diagnostic can be proven without making
    /// a unit test sleep for seconds.
    /// </remarks>
    internal TimeSpan InteractiveHandlerThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Threshold at which one live warning is emitted while a handler is still running. Production value: ten seconds.
    /// </summary>
    /// <remarks>
    /// The warning fires at most once per execution, using a cancellation timer rather than a polling loop, so a slow
    /// handler cannot produce one warning per second. Tests override it with a millisecond value.
    /// </remarks>
    internal TimeSpan LongHandlerWarningThreshold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Queue wait that counts as an unusually long wait. Production value: five seconds.
    /// </summary>
    /// <remarks>
    /// The threshold is injectable so a regression test can prove lane-blocker correlation without making a real
    /// update wait five seconds, while production keeps the documented five-second value.
    /// </remarks>
    internal TimeSpan LongQueueWaitThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum duration of one closed-vocabulary execution stage that is reported. Production value: two seconds.
    /// </summary>
    /// <remarks>
    /// Keeping the threshold at two seconds means a healthy handler is completely silent while a slow one is
    /// attributed to <c>xui_read</c>, <c>telegram_send</c>, <c>telegram_membership</c>, or <c>site_lookup</c>.
    /// </remarks>
    internal TimeSpan SlowStageThreshold { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Per-lane record of the last queue-wait blocker that was reported, keyed by bot id and Telegram user id.
    /// </summary>
    /// <remarks>
    /// One slow handler makes every later update in the same lane wait, so without this map a single incident produced
    /// four or more warnings. Entries are written only for waits above the long-wait threshold, so the map grows with
    /// distinct incidents rather than with traffic, and it is bounded explicitly to prevent unbounded growth.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _reportedQueueBlockers = new(StringComparer.Ordinal);

    /// <summary>Maximum lane entries retained for queue-wait deduplication before the map is reset.</summary>
    private const int MaxReportedQueueBlockers = 20000;
    /// <summary>Deterministic wait seam; production waits on the coalesced signal or the recovery timeout.</summary>
    internal Func<SemaphoreSlim, TimeSpan, CancellationToken, Task<bool>> WaitAsync { get; init; }
        = (signal, interval, token) => signal.WaitAsync(interval, token);

    /// <summary>Wakes the coordinator after durable state or bot availability changes.</summary>
    /// <remarks>One queued token covers every committed change. A lost wake is recovered within the scan interval.</remarks>
    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    private readonly ITelegramUpdateExecutor _executor;
    private readonly ILogger<TelegramUpdateScheduler> _logger;
    private readonly int _concurrency;
    private readonly int _capacity;
    private readonly TimeSpan _drainTimeout;
    private readonly CancellationTokenSource _admission = new();
    private readonly CancellationTokenSource _handlers = new();
    private readonly CancellationTokenSource _coordinator = new();
    private Task _loop;
    private volatile bool _draining;
    private string _lastBot;
    private int _activeCount;
    private long _lastPressureWarning;
    private int _disposed;

    /// <summary>Builds a scheduler using validated application limits.</summary>
    /// <param name="store">Durable users.db inbox store; no live context is captured.</param>
    /// <param name="executor">Runtime resolution and handler execution boundary.</param>
    /// <param name="config">Application settings: positive concurrency, capacity, and drain duration in seconds.</param>
    /// <param name="logger">Structured logger; update payloads and exception messages are excluded.</param>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    public TelegramUpdateScheduler(TelegramUpdateInboxStore store, ITelegramUpdateExecutor executor, AppConfig config, ILogger<TelegramUpdateScheduler> logger)
    {
        ValidateConfiguration(config);
        _store = store; _executor = executor; _logger = logger;
        _store.ReadyChanged += Wake;
        _concurrency = config.TelegramUpdateMaxConcurrency;
        _capacity = config.TelegramUpdateQueueCapacity;
        _drainTimeout = TimeSpan.FromSeconds(config.TelegramUpdateShutdownDrainSeconds);
    }

    /// <summary>Returns the current executing handler count for diagnostics and regression verification.</summary>
    public int ActiveHandlerCount => Volatile.Read(ref _activeCount);

    /// <inheritdoc />
    public void StopAdmission() => _admission.Cancel();

    /// <summary>Rejects configuration that defeats bounded execution or admission.</summary>
    /// <param name="config">Required application configuration.</param>
    /// <exception cref="ArgumentOutOfRangeException">A scheduler limit is outside the supported range.</exception>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    public static void ValidateConfiguration(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.TelegramUpdateMaxConcurrency is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(config.TelegramUpdateMaxConcurrency));
        if (config.TelegramUpdateQueueCapacity is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(config.TelegramUpdateQueueCapacity));
        if (config.TelegramUpdateShutdownDrainSeconds is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(config.TelegramUpdateShutdownDrainSeconds));
    }

    /// <summary>Recovers interrupted claims before starting the tracked coordinator.</summary>
    /// <param name="cancellationToken">Host startup cancellation.</param>
    /// <returns>A task completing after startup recovery and scheduler readiness.</returns>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = await _store.RecoverAsync(cancellationToken);
        if (recovered > 0)
            _logger.LogWarning("Telegram executions recovered without replay. telegramRecoveryIncidentCount={Count}", recovered);
        _loop = RunAsync(_coordinator.Token);
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(string botId, Update update, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botId);
        ArgumentNullException.ThrowIfNull(update);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _admission.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        while (!await _store.TryAcceptAsync(botId, update, _capacity, token))
        {
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastPressureWarning);
            if (now - previous > 10000 && Interlocked.CompareExchange(ref _lastPressureWarning, now, previous) == previous)
                _logger.LogWarning("Telegram admission waiting for capacity. BotId={BotId} QueueDepth={QueueDepth} MaxConcurrency={MaxConcurrency}", botId, _capacity, _concurrency);
            await Task.Delay(100, token);
        }
    }

    /// <summary>Runs eligible lane heads while maintaining a fixed upper bound on tracked handler tasks.</summary>
    /// <param name="token">Coordinator cancellation after shutdown draining has ended.</param>
    /// <returns>The complete coordinator lifetime, including observation of every started handler.</returns>
    /// <remarks>The ready query excludes only earlier queued or running work. Round-robin bots cannot reorder live work in a lane.</remarks>
    private async Task RunAsync(CancellationToken token)
    {
        var active = new Dictionary<long, Task>();
        var userCursors = new Dictionary<string, long>(StringComparer.Ordinal);
        var nextMaintenance = DateTime.UtcNow;
        var nextPressureSample = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var pair in active.Where(x => x.Value.IsCompleted).ToArray())
                {
                    await pair.Value;
                    active.Remove(pair.Key);
                }
                try
                {
                    Interlocked.Increment(ref _readyQueryCount);
                    ReadyQueries.Add(1);
                    var ready = await _store.ReadReadyAsync(_capacity, token);
                    foreach (var idleBot in userCursors.Keys.Where(bot => !ready.Any(x => x.BotId == bot)).ToArray())
                        userCursors.Remove(idleBot);
                    if (DateTime.UtcNow >= nextMaintenance)
                    {
                        await _store.PruneAsync(token);
                        nextMaintenance = DateTime.UtcNow.AddHours(1);
                    }
                    if (DateTime.UtcNow >= nextPressureSample)
                    {
                        var depth = await _store.CountPendingAsync(token);
                        QueueDepth.Record(depth);
                        await RecordRecoveryMetricsAsync(token);
                        if (depth >= _capacity * 0.8)
                            _logger.LogWarning("Telegram queue pressure. QueueDepth={QueueDepth} Capacity={Capacity} ActiveHandlers={ActiveHandlers} MaxConcurrency={MaxConcurrency}", depth, _capacity, ActiveHandlerCount, _concurrency);
                        nextPressureSample = DateTime.UtcNow.AddSeconds(10);
                    }
                    var eligible = ready.Where(x => !active.ContainsKey(x.Sequence) && _executor.IsAvailable(x.BotId)).ToList();
                    while (active.Count < _concurrency && eligible.Count > 0)
                    {
                        var bots = eligible.Select(x => x.BotId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                        var next = bots.FindIndex(x => string.CompareOrdinal(x, _lastBot) > 0);
                        var bot = bots[next < 0 ? 0 : next];
                        var heads = eligible.Where(x => x.BotId == bot).OrderBy(x => x.TelegramUserId).ToList();
                        var lastUser = userCursors.GetValueOrDefault(bot, -1);
                        var head = heads.FirstOrDefault(x => x.TelegramUserId > lastUser) ?? heads[0];
                        userCursors[bot] = head.TelegramUserId;
                        eligible.Remove(head);
                        _lastBot = bot;
                        var execution = ProcessAsync(head.Sequence, _handlers.Token);
                        active.Add(head.Sequence, execution);
                        // Notify after task completion, not merely after its final DB write, so slot reaping cannot miss a wake.
                        execution.GetAwaiter().OnCompleted(Wake);
                    }
                    if (_draining && active.Count == 0 && eligible.Count == 0) break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Database outages pause admission/execution, rather than losing the tracked scheduler lifetime.
                    _logger.LogError("Telegram scheduler persistence failure. ErrorType={ErrorType}", ex.GetType().Name);
                    await Task.Delay(500, token);
                }
                if (await WaitAsync(_wake, RecoveryInterval, token)) Wakeups.Add(1);
                else RecoveryScans.Add(1);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(active.Values);
        }
    }

    /// <summary>Claims and executes one update, isolating exceptions and recording a durable terminal outcome.</summary>
    /// <param name="sequence">Internal inbox sequence; no secret data.</param>
    /// <param name="token">Cancellation propagated to the handler after drain expiry.</param>
    /// <returns>A tracked task that observes handler failures and attempts independent final persistence.</returns>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    private async Task ProcessAsync(long sequence, CancellationToken token)
    {
        _store.Executing.TryAdd(sequence, 0);
        TelegramUpdateWorkItem item = null;
        var started = Stopwatch.GetTimestamp();
        string failure = null;
        var counted = false;
        // Live long-handler diagnostics: one timer per execution and one flag that proves the handler was still
        // running when it fired. A StrongBox gives the timer callback a reference it can read atomically.
        var handlerFinished = new System.Runtime.CompilerServices.StrongBox<int>(0);
        var handlerWarningEmitted = new System.Runtime.CompilerServices.StrongBox<int>(0);
        CancellationTokenSource handlerWarningTimer = null;
        CancellationTokenRegistration handlerWarningRegistration = default;
        try
        {
            item = await _store.ClaimAsync(sequence, token);
            if (item == null) return;
            var wait = (DateTime.UtcNow - item.AcceptedAtUtc).TotalMilliseconds;
            QueueWait.Record(wait);
            var active = Interlocked.Increment(ref _activeCount);
            counted = true;
            _logger.LogDebug("Telegram update started. BotId={BotId} TelegramUserId={TelegramUserId} UpdateId={UpdateId} UpdateType={UpdateType} QueueWaitMs={QueueWaitMs} ActiveHandlers={ActiveHandlers} MaxConcurrency={MaxConcurrency}",
                item.Key.BotId, item.Key.TelegramUserId, item.Update.Id, item.Update.Type, wait, active, _concurrency);
            if (wait > LongQueueWaitThreshold.TotalMilliseconds)
                await ReportLongQueueWaitAsync(item, sequence, wait, token);

            // The watchdog fires once at the threshold. It reports the root blocker instead of the victims that merely
            // waited behind it, and it never runs when the handler already finished.
            handlerWarningTimer = new CancellationTokenSource(LongHandlerWarningThreshold);
            var claimedItem = item;
            handlerWarningRegistration = handlerWarningTimer.Token.Register(() =>
            {
                if (System.Threading.Volatile.Read(ref handlerFinished.Value) != 0)
                    return;
                System.Threading.Volatile.Write(ref handlerWarningEmitted.Value, 1);
                try
                {
                    _logger.LogWarning(
                        "Telegram update handler running unusually long. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} UpdateType={UpdateType} HandlerElapsedMs={HandlerElapsedMs} ActiveHandlers={ActiveHandlers} MaxConcurrency={MaxConcurrency}",
                        claimedItem.Key.BotId, sequence, claimedItem.Update.Id, claimedItem.Update.Type,
                        LongHandlerWarningThreshold.TotalMilliseconds, ActiveHandlerCount, _concurrency);
                }
                catch
                {
                    // A diagnostics callback must never fault the handler it is observing.
                }
            });

            using (TelegramUpdateExecutionScope.Push(sequence))
            using (TelegramUpdateLatencyScope.Push(
                sequence,
                item.Key.BotId,
                item.Update.Id,
                SlowStageThreshold,
                (stage, elapsedMs) => ReportSlowStage(item.Key.BotId, sequence, item.Update.Id, item.Update.Type, stage, elapsedMs)))
                await _executor.ExecuteAsync(item, token);
            token.ThrowIfCancellationRequested();
            if (await _store.HasUnresolvedCreationAsync(sequence, token))
            {
                failure = "creation_requires_review";
                _logger.LogWarning("Telegram execution completed with business recovery pending. Sequence={Sequence} RecoveryType={RecoveryType}",
                    sequence, "xui_creation");
            }
        }
        catch (OperationCanceledException) { failure = "execution_cancelled"; }
        catch (BotTransportUnavailableException ex)
        {
            failure = BotTransportUnavailableException.FailureCode;
            _logger.LogWarning(
                "Telegram update ended with unavailable bot transport. Sequence={Sequence} FailureCode={FailureCode} ReasonCode={ReasonCode}",
                sequence, failure, ex.ReasonCode);
        }
        catch (Exception ex)
        {
            failure = "execution_failed";
            _logger.LogError(
                "Telegram update failed and was released. Sequence={Sequence} FailureCode={FailureCode} ErrorType={ErrorType}",
                sequence, failure, ex.GetType().Name);
        }
        finally
        {
            // Stop the watchdog first so a finished handler can never be reported as still running.
            System.Threading.Volatile.Write(ref handlerFinished.Value, 1);
            handlerWarningRegistration.Dispose();
            handlerWarningTimer?.Dispose();
            if (counted) Interlocked.Decrement(ref _activeCount);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Duration.Record(duration);
            RecordHandlerDurationDiagnostic(
                duration,
                failure,
                item,
                sequence,
                System.Threading.Volatile.Read(ref handlerWarningEmitted.Value) != 0);
            // Also resolves a committed claim whose payload could not be deserialized before ClaimAsync returned.
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { if (item != null || failure != null) await _store.FinishAsync(sequence, failure, persistence.Token); }
            catch (Exception ex) { _logger.LogError("Telegram completion persistence failed; claim remains recoverable. Sequence={Sequence} ErrorType={ErrorType}", sequence, ex.GetType().Name); }
            _store.Executing.TryRemove(sequence, out _);
            Wake(); // Completion also releases an active-task slot even when final persistence failed.
            _logger.LogDebug("Telegram update finished. Sequence={Sequence} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome}", sequence, duration, failure ?? "completed");
        }
    }

    /// <summary>
    /// Reports one long queue wait together with the previous execution that most likely caused it.
    /// </summary>
    /// <param name="item">The claimed work item whose wait is being reported; its payload is never logged.</param>
    /// <param name="sequence">Internal inbox sequence of the waiting update.</param>
    /// <param name="waitMilliseconds">Observed wait between durable acceptance and handler start.</param>
    /// <param name="token">Coordinator/execution cancellation for the metadata-only blocker lookup.</param>
    /// <returns>A task completing after the diagnostic (or its suppression) has been logged.</returns>
    /// <remarks>
    /// Strict FIFO means one slow predecessor makes every later update in the same lane wait, so the raw
    /// "waited unusually long" warning was pure alert amplification: one incident produced four warnings naming only
    /// the victims. This method names the predecessor instead and reports each lane/blocker pair once; further waits
    /// behind the same blocker stay visible through the queue-wait histogram and a payload-free Debug line, while a
    /// genuinely different blocker warns again. The lookup is diagnostics only and never affects scheduling.
    /// </remarks>
    private async Task ReportLongQueueWaitAsync(TelegramUpdateWorkItem item, long sequence, double waitMilliseconds, CancellationToken token)
    {
        TelegramLaneExecutionSummary blocker = null;
        try
        {
            blocker = await _store.FindPreviousLaneExecutionAsync(
                sequence, item.Key.BotId, item.Key.TelegramUserId, item.AcceptedAtUtc, DateTime.UtcNow, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diagnostics must never delay or fail the update that is about to run.
            _logger.LogDebug("Telegram lane blocker lookup failed. ErrorType={ErrorType}", ex.GetType().Name);
        }

        var blockerSequence = blocker?.Sequence ?? 0;
        var laneKey = item.Key.BotId + "\u001f" + item.Key.TelegramUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var alreadyReported = _reportedQueueBlockers.TryGetValue(laneKey, out var previous) && previous == blockerSequence;
        if (_reportedQueueBlockers.Count >= MaxReportedQueueBlockers)
            _reportedQueueBlockers.Clear();
        _reportedQueueBlockers[laneKey] = blockerSequence;

        if (alreadyReported)
        {
            // Same lane incident, already reported once: keep the metric and a file diagnostic instead of another alert.
            _logger.LogDebug(
                "Telegram update waited behind an already reported lane blocker. BotId={BotId} UpdateId={UpdateId} QueueWaitMs={QueueWaitMs} PreviousSequence={PreviousSequence}",
                item.Key.BotId, item.Update.Id, waitMilliseconds, blockerSequence);
            return;
        }

        _logger.LogWarning(
            "Telegram update waited unusually long. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} UpdateType={UpdateType} QueueWaitMs={QueueWaitMs} PreviousSequence={PreviousSequence} PreviousUpdateId={PreviousUpdateId} PreviousHandlerDurationMs={PreviousHandlerDurationMs}",
            item.Key.BotId, sequence, item.Update.Id, item.Update.Type, waitMilliseconds,
            blockerSequence, blocker?.UpdateId ?? 0, blocker?.HandlerDurationMs ?? 0);
    }

    /// <summary>
    /// Reports one closed-vocabulary execution stage that exceeded the slow-stage threshold.
    /// </summary>
    /// <param name="botId">Canonical runtime bot id of the update being executed; never a token.</param>
    /// <param name="sequence">Internal inbox sequence of the update being executed.</param>
    /// <param name="updateId">Telegram update id of the update being executed.</param>
    /// <param name="updateType">Telegram update type label of the update being executed.</param>
    /// <param name="stage">Closed-vocabulary stage that exceeded the threshold.</param>
    /// <param name="elapsedMilliseconds">Monotonic elapsed milliseconds for that stage.</param>
    /// <remarks>
    /// This is the attribution signal the production incident lacked: it names the exact stage rather than only the
    /// whole-handler duration. Stage names come from a closed enumeration, so no email, order id, callback payload, or
    /// customer text can appear here. It is logged at Information because it explains one already-counted handler and is
    /// not itself an alert.
    /// </remarks>
    private void ReportSlowStage(string botId, long sequence, int updateId, UpdateType updateType, TelegramUpdateStage stage, double elapsedMilliseconds)
    {
        _logger.LogInformation(
            "Telegram slow update stage. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} UpdateType={UpdateType} Stage={Stage} ElapsedMs={ElapsedMs}",
            botId, sequence, updateId, updateType, stage, elapsedMilliseconds);
    }

    /// <summary>
    /// Records the completed handler duration so long updates stay visible even when they eventually succeed.
    /// </summary>
    /// <param name="durationMilliseconds">Total handler wall-clock duration in milliseconds.</param>
    /// <param name="failureCode">Null on success; otherwise the coarse durable failure classification.</param>
    /// <param name="item">The claimed work item, or null when the claim never completed.</param>
    /// <param name="sequence">Internal inbox sequence of the update.</param>
    /// <param name="liveWarningEmitted">
    /// True when the live long-handler watchdog already produced the single Warning for this execution, so the
    /// completion record must not add a second alert for the same root blocker.
    /// </param>
    /// <remarks>
    /// Exactly one Warning is emitted per slow root handler: the live watchdog normally fires while it is still
    /// running, and this completion record is then logged at Information. When the watchdog could not fire — for example
    /// the handler finished in the same instant the threshold crossed — the completion record itself carries the
    /// Warning, so a long handler is never invisible. Handlers between the interactive and long thresholds are recorded
    /// at Information because a production sample contained several of them and they are not individually actionable.
    /// </remarks>
    private void RecordHandlerDurationDiagnostic(
        double durationMilliseconds,
        string failureCode,
        TelegramUpdateWorkItem item,
        long sequence,
        bool liveWarningEmitted)
    {
        if (durationMilliseconds < InteractiveHandlerThreshold.TotalMilliseconds)
            return;

        var botId = item?.Key.BotId ?? "unknown";
        var updateId = item?.Update.Id ?? 0;
        var outcome = failureCode ?? "completed";
        var isLongHandler = durationMilliseconds >= LongHandlerWarningThreshold.TotalMilliseconds;
        if (isLongHandler && !liveWarningEmitted)
            _logger.LogWarning(
                "Telegram update handler exceeded the interactive latency threshold. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome}",
                botId, sequence, updateId, durationMilliseconds, outcome);
        else if (isLongHandler)
            _logger.LogInformation(
                "Telegram long update handler completed. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome}",
                botId, sequence, updateId, durationMilliseconds, outcome);
        else
            _logger.LogInformation(
                "Telegram update handler exceeded the interactive latency threshold. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome}",
                botId, sequence, updateId, durationMilliseconds, outcome);
    }

    private static readonly Histogram<int> RecoveryPending = Meter.CreateHistogram<int>("telegram.update.recovery.pending");
    private static readonly Histogram<double> OldestRecovery = Meter.CreateHistogram<double>("telegram.update.recovery.oldest_age", "s");

    /// <summary>Records the business-review backlog without producing periodic alerts or affecting scheduling.</summary>
    /// <param name="token">Coordinator cancellation for metadata-only queries.</param>
    /// <returns>A task completing after local count and age instruments are updated.</returns>
    /// <remarks>New incidents are logged once at creation or restart recovery. Unchanged backlog samples never generate Telegram logger traffic.</remarks>
    private async Task RecordRecoveryMetricsAsync(CancellationToken token)
    {
        var summary = await _store.ReadUncertainSummaryAsync(token);
        RecoveryPending.Record(summary.Count);
        OldestRecovery.Record(summary.AgeSeconds);
    }

    /// <summary>Closes admission, drains runnable work, then cooperatively cancels unfinished handlers.</summary>
    /// <param name="cancellationToken">Host deadline; accepted queued updates remain stored if it expires.</param>
    /// <returns>A task completing after draining or a bounded cancellation grace period.</returns>
    /// <remarks>After the configured drain, active Telegram receipts are made terminal before waiting up to fifteen seconds for cooperative
    /// cancellation. A handler ignoring cancellation is logged and remains tracked until process exit; it cannot mark its
    /// terminal receipt later. The deployment must terminate the old process before starting a replacement.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        StopAdmission();
        _draining = true;
        Wake();
        if (_loop == null) return;
        try { await _loop.WaitAsync(_drainTimeout, cancellationToken); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _handlers.Cancel();
            _coordinator.Cancel();
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _store.RecoverAsync(persistence.Token); }
            catch (Exception failure) { _logger.LogError("Shutdown recovery persistence failed; running claims will be finalized on restart. ErrorType={ErrorType}", failure.GetType().Name); }
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
            {
                _logger.LogCritical("Telegram handlers did not stop within cancellation grace. ActiveHandlers={ActiveHandlers}", ActiveHandlerCount);
            }
        }
    }

    /// <summary>Disposes scheduler cancellation resources after the host has awaited StopAsync.</summary>
    /// <remarks>The host owns the scheduler lifetime. Only eligible bot/user lane heads enter the bounded worker set; full durable admission applies explicit backpressure.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _store.ReadyChanged -= Wake;
        _admission.Dispose(); _handlers.Dispose(); _coordinator.Dispose();
    }
}
