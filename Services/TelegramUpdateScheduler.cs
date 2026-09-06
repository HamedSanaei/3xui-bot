using Adminbot.Domain;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Telegram.Bot.Types;

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
/// Only the coordinator owns the active-task collection. A blocked lane occupies no worker, and historical users
/// create no permanent locks. Shutdown drains runnable work; disabled and uncertain lanes remain durable.
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
        var uncertain = await _store.RecoverAsync(cancellationToken);
        await ReportUncertainAsync(cancellationToken);
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
    /// <remarks>The ready query excludes lanes with earlier running or uncertain rows. Round-robin bots cannot reorder a lane.</remarks>
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
                        await ReportUncertainAsync(token);
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
            if (wait > 5000) _logger.LogWarning("Telegram update waited unusually long. BotId={BotId} UpdateId={UpdateId} QueueWaitMs={QueueWaitMs}", item.Key.BotId, item.Update.Id, wait);
            using (TelegramUpdateExecutionScope.Push(sequence))
                await _executor.ExecuteAsync(item, token);
            token.ThrowIfCancellationRequested();
            if (await _store.HasUnresolvedCreationAsync(sequence, token)) failure = "creation_requires_review";
        }
        catch (OperationCanceledException) { failure = "execution_cancelled"; }
        catch (Exception ex)
        {
            failure = "execution_failed";
            _logger.LogError("Telegram update quarantined. Sequence={Sequence} ErrorType={ErrorType}", sequence, ex.GetType().Name);
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref _activeCount);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Duration.Record(duration);
            // Also resolves a committed claim whose payload could not be deserialized before ClaimAsync returned.
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { if (item != null || failure != null) await _store.FinishAsync(sequence, failure, persistence.Token); }
            catch (Exception ex) { _logger.LogError("Telegram completion persistence failed; claim remains recoverable. Sequence={Sequence} ErrorType={ErrorType}", sequence, ex.GetType().Name); }
            _store.Executing.TryRemove(sequence, out _);
            Wake(); // Completion also releases an active-task slot even when final persistence failed.
            _logger.LogDebug("Telegram update finished. Sequence={Sequence} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome}", sequence, duration, failure ?? "completed");
        }
    }

    private static readonly Histogram<int> Uncertain = Meter.CreateHistogram<int>("telegram.update.inbox.uncertain");
    private static readonly Histogram<int> BlockedLanes = Meter.CreateHistogram<int>("telegram.update.inbox.blocked_lanes");
    private static readonly Histogram<double> OldestUncertain = Meter.CreateHistogram<double>("telegram.update.inbox.oldest_uncertain_age", "s");

    /// <summary>Reports existing and newly recovered quarantine pressure without reading private payloads.</summary>
    /// <param name="token">Startup or coordinator cancellation for metadata queries.</param>
    /// <returns>A task completing after count/age instruments and sanitized warnings are emitted.</returns>
    /// <remarks>Called at startup and at most once per ten-second pressure sample. Uncertain rows still consume capacity.</remarks>
    private async Task ReportUncertainAsync(CancellationToken token)
    {
        var summary = await _store.ReadUncertainSummaryAsync(token);
        Uncertain.Record(summary.Count); BlockedLanes.Record(summary.Lanes); OldestUncertain.Record(summary.AgeSeconds);
        if (summary.Count > 0) _logger.LogWarning("Telegram inbox quarantine. telegramInboxUncertainCount={Count} telegramInboxBlockedLaneCount={Lanes} telegramInboxOldestUncertainAge={AgeSeconds} Capacity={Capacity} SignificantPressure={Pressure}",
            summary.Count, summary.Lanes, summary.AgeSeconds, _capacity, summary.Count >= _capacity * 0.2);
    }

    /// <summary>Closes admission, drains runnable work, then cooperatively cancels unfinished handlers.</summary>
    /// <param name="cancellationToken">Host deadline; accepted queued updates remain stored if it expires.</param>
    /// <returns>A task completing after draining or a bounded cancellation grace period.</returns>
    /// <remarks>After the configured drain, active work is quarantined before waiting up to fifteen seconds for cooperative
    /// cancellation. A handler ignoring cancellation is logged and remains tracked until process exit; it cannot mark its
    /// quarantined row completed later. The deployment must terminate the old process before starting a replacement.</remarks>
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
            catch (Exception failure) { _logger.LogError("Shutdown quarantine failed; running claims will be recovered on restart. ErrorType={ErrorType}", failure.GetType().Name); }
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
