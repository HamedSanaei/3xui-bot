using Adminbot.Domain;
using Adminbot.Domain.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;
using Adminbot.Services.Telemetry;

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
    /// <summary>Nonblocking payload-free event writer, shared by all bot families.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Bounded reception clocks transferred at durable claim; never owns scheduler state.</summary>
    private readonly UpdateTelemetryTracker _timelineTracker;
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
    ///
    /// This same boundary separates performance telemetry from an operator incident in
    /// <see cref="TelegramLogSuppression"/>: a completed handler above the interactive five-second threshold but below
    /// this value is recorded locally and is withheld from the central Telegram logger channel, while a handler at or
    /// above it remains channel-visible. The initializer reads the shared constant so the two policies cannot drift.
    /// </remarks>
    internal TimeSpan LongHandlerWarningThreshold { get; init; } =
        TimeSpan.FromMilliseconds(TelegramLogSuppression.LongHandlerOperatorThresholdMilliseconds);

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

    /// <summary>Instance-owned, bounded queue incidents; observations never perform operator delivery.</summary>
    private readonly TelegramQueueDelayIncidentAggregator _queueIncidents = new();
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
    /// <param name="telemetry">Optional persistent JSONL writer; failure or a full buffer cannot affect scheduling.</param>
    /// <param name="timelineTracker">Optional bounded metadata tracker connecting receiver admission with claims.</param>
    /// <remarks>The host owns scheduler lifetime. FIFO, capacity and concurrency are unchanged. Metadata clocks measure admission,
    /// ready-snapshot dispatch and actual handler boundaries; no telemetry write occurs in users.db.</remarks>
    public TelegramUpdateScheduler(TelegramUpdateInboxStore store, ITelegramUpdateExecutor executor, AppConfig config,
        ILogger<TelegramUpdateScheduler> logger, LatencyTelemetryService telemetry = null, UpdateTelemetryTracker timelineTracker = null)
    {
        ValidateConfiguration(config);
        _store = store; _executor = executor; _logger = logger;
        _telemetry = telemetry;
        _timelineTracker = timelineTracker;
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

    /// <summary>Persists one receiver update under existing capacity backpressure while recording admission clocks.</summary>
    /// <param name="botId">Canonical configured runtime bot id, not a token or user identity.</param>
    /// <param name="update">Required private SDK update; only its id and enum category reach telemetry.</param>
    /// <param name="cancellationToken">Existing receiver cancellation before acceptance.</param>
    /// <returns>The original durable-admission completion, including deduplicated delivery.</returns>
    /// <remarks>Direct scheduler callers receive a reception clock at method entry. Production receivers start it earlier,
    /// before receiver control-path checks. Capacity sleeps are admission latency, never scheduler queue latency.</remarks>
    /// <exception cref="OperationCanceledException">Admission or the receiver was stopped before durable acceptance.</exception>
    /// <example><code>await scheduler.EnqueueAsync(bot.Id, update, receiverToken);</code></example>
    public async Task EnqueueAsync(string botId, Update update, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botId);
        ArgumentNullException.ThrowIfNull(update);
        using var reception = UpdateTelemetryTracker.Current == null
            ? _timelineTracker?.Receive(botId, update.Id, update.Type.ToString()) : null;
        var timeline = UpdateTelemetryTracker.Current;
        timeline?.BeginAdmission();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _admission.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        while (true)
        {
            timeline?.BeginAdmissionAttempt();
            if (await _store.TryAcceptAsync(botId, update, _capacity, token)) break;
            timeline?.AdmissionRefused();
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
    /// <remarks>The ready query excludes only earlier queued or running work. Round-robin bots cannot reorder live work in a lane. Queue diagnostics are flushed on the existing coordinator wake/recovery path; their failures are isolated.</remarks>
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
                FlushQueueIncidents();
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
                    var readyObservedTimestamp = Stopwatch.GetTimestamp();
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
                        _telemetry?.UpdateSchedulerHealth(ActiveHandlerCount, depth);
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
                        var execution = ProcessAsync(head.Sequence, _handlers.Token, readyObservedTimestamp);
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
            try { await Task.WhenAll(active.Values); }
            finally { FlushQueueIncidents(shutdown: true); }
        }
    }

    /// <summary>Claims and executes one update, isolating exceptions and recording a durable terminal outcome.</summary>
    /// <param name="sequence">Internal inbox sequence; no secret data.</param>
    /// <param name="token">Cancellation propagated to the handler after drain expiry.</param>
    /// <param name="readyObservedTimestamp">Stopwatch timestamp after the ready query; zero denotes unavailable dispatch timing.</param>
    /// <returns>A tracked task that observes handler failures and attempts independent final persistence.</returns>
    /// <remarks>The host owns scheduler lifetime and FIFO/concurrency are unchanged. Correlated summaries distinguish claim,
    /// queue, actual ExecuteAsync, post-handler review and final persistence. Foreground API observations and watchdogs
    /// retain their original routing and budgets; private payloads and exception bodies never enter JSONL.</remarks>
    private async Task ProcessAsync(long sequence, CancellationToken token, long readyObservedTimestamp = 0)
    {
        _store.Executing.TryAdd(sequence, 0);
        TelegramUpdateWorkItem item = null;
        var started = Stopwatch.GetTimestamp();
        string failure = null;
        var counted = false;
        TelegramUpdateLatencyScope latencyScope = null;
        UpdateTelemetryTracker.UpdateTelemetryTimeline timeline = null;
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
            timeline = _timelineTracker?.Claimed(item, started, readyObservedTimestamp);
            var wait = Math.Max(0, (item.StartedAtUtc - item.AcceptedAtUtc).TotalMilliseconds);
            QueueWait.Record(wait);
            var active = Interlocked.Increment(ref _activeCount);
            counted = true;
            _telemetry?.UpdateActiveHandlers(ActiveHandlerCount);
            _logger.LogDebug("Telegram update started. BotId={BotId} TelegramUserId={TelegramUserId} UpdateId={UpdateId} UpdateType={UpdateType} QueueWaitMs={QueueWaitMs} ActiveHandlers={ActiveHandlers} MaxConcurrency={MaxConcurrency}",
                item.Key.BotId, item.Key.TelegramUserId, item.Update.Id, item.Update.Type, wait, active, _concurrency);
            if (wait > LongQueueWaitThreshold.TotalMilliseconds)
                await ReportLongQueueWaitAsync(item, sequence, wait, token);

            using (TelegramUpdateExecutionScope.Push(sequence))
            {
                // The watchdog fires once at the threshold. It reports the root blocker instead of the victims that
                // merely waited behind it, and it never runs when the handler already finished.
                handlerWarningTimer = new CancellationTokenSource();
                var claimedItem = item;
                handlerWarningRegistration = handlerWarningTimer.Token.Register(() =>
                {
                    if (System.Threading.Volatile.Read(ref handlerFinished.Value) != 0)
                        return;
                    var observedScope = latencyScope;
                    if (observedScope == null) return; // No handler scope exists during pre-invocation setup.
                    System.Threading.Volatile.Write(ref handlerWarningEmitted.Value, 1);
                    try
                    {
                        // Snapshot values are enums and timings only. Handler age and this one request's age are
                        // distinct: several sequential requests can cross the watchdog without one ten-second send.
                        var request = observedScope.CurrentTelegramRequest;
                        _logger.LogWarning(
                            "Telegram update handler running unusually long. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} UpdateType={UpdateType} HandlerElapsedMs={HandlerElapsedMs} Stage={Stage} RequestKind={RequestKind} RequestOutcome={RequestOutcome} RequestElapsedMs={RequestElapsedMs} TelegramRequestCount={TelegramRequestCount} TotalTelegramElapsedMs={TotalTelegramElapsedMs} ActiveHandlers={ActiveHandlers} MaxConcurrency={MaxConcurrency}",
                            claimedItem.Key.BotId, sequence, claimedItem.Update.Id, claimedItem.Update.Type,
                            request?.HandlerElapsedMs ?? observedScope.Elapsed.TotalMilliseconds, observedScope.CurrentStageName,
                            request?.Kind.ToString() ?? "none", request?.Outcome.ToString() ?? "none", request?.RequestElapsedMs ?? 0,
                            request?.RequestCount ?? observedScope.RequestCount, request?.TotalTelegramElapsedMs ?? observedScope.TotalTelegramElapsedMs,
                            ActiveHandlerCount, _concurrency);
                    }
                    catch
                    {
                        // A diagnostics callback must never fault the handler it is observing.
                    }
                });

                using (latencyScope = TelegramUpdateLatencyScope.Push(
                    sequence,
                    item.Key.BotId,
                    item.Update.Id,
                    SlowStageThreshold,
                    (stage, elapsedMs) => ReportSlowStage(item.Key.BotId, sequence, item.Update.Id, item.Update.Type, stage, elapsedMs),
                    observation => ReportTelegramRequest(item.Key.BotId, sequence, item.Update.Id, item.Update.Type, observation),
                    telemetry: _telemetry, traceId: timeline?.TraceId))
                {
                    // Scope and lifecycle use one clock origin/end; watchdog setup cannot be charged to handler work.
                    handlerWarningTimer.CancelAfter(LongHandlerWarningThreshold);
                    timeline?.HandlerStarted(latencyScope.HandlerStartedAtUtc);
                    try { await _executor.ExecuteAsync(item, token); }
                    finally
                    {
                        var handlerCompletedTimestamp = Stopwatch.GetTimestamp();
                        // Freeze the authoritative handler clock before constructing diagnostic dictionaries.
                        latencyScope.Dispose();
                        timeline?.HandlerCompleted(latencyScope.CaptureTelemetry(), handlerCompletedTimestamp);
                    }
                }
            }
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
        catch (ApiRequestException ex)
        {
            failure = "execution_failed";
            _logger.LogError(
                "Telegram update failed and was released. Sequence={Sequence} FailureCode={FailureCode} ErrorType={ErrorType} ErrorCode={ErrorCode} ReasonCode={ReasonCode}",
                sequence, failure, ex.GetType().Name, ex.ErrorCode, GetTelegramFailureReason(ex));
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
            if (counted)
            {
                Interlocked.Decrement(ref _activeCount);
                _telemetry?.UpdateActiveHandlers(ActiveHandlerCount);
            }
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Duration.Record(duration);
            RecordHandlerDurationDiagnostic(
                duration,
                failure,
                item,
                sequence,
                System.Threading.Volatile.Read(ref handlerWarningEmitted.Value) != 0,
                latencyScope?.RequestCount ?? 0,
                latencyScope?.TotalTelegramElapsedMs ?? 0);
            // Also resolves a committed claim whose payload could not be deserialized before ClaimAsync returned.
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var persistenceStarted = Stopwatch.GetTimestamp();
            bool? persisted = false;
            try
            {
                if (item != null || failure != null)
                    persisted = await _store.FinishAsync(sequence, failure, persistence.Token) ? true : null;
            }
            catch (Exception ex) { _logger.LogError("Telegram completion persistence failed; claim remains recoverable. Sequence={Sequence} ErrorType={ErrorType}", sequence, ex.GetType().Name); }
            timeline?.Completed(failure, persistenceStarted, persisted);
            _store.Executing.TryRemove(sequence, out _);
            Wake(); // Completion also releases an active-task slot even when final persistence failed.
            _logger.LogDebug("Telegram update finished. Sequence={Sequence} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome}", sequence, duration, failure ?? "completed");
        }
    }

    /// <summary>Classifies a Telegram API failure into safe, closed operator metadata.</summary>
    /// <param name="exception">Required Telegram exception; existing safe classifiers inspect its description locally, never returning or logging arbitrary text.</param>
    /// <returns>A shared Telegram failure reason safe for local and operator logs, with server failures grouped and numeric fallback bounded.</returns>
    /// <remarks>Classification is diagnostic only: even an uncaught no-op remains execution_failed here. Semantic no-op handling belongs to the edit caller.</remarks>
    /// <example><code>var reason = GetTelegramFailureReason(new ApiRequestException("Forbidden", 403)); // telegram_forbidden</code></example>
    private static string GetTelegramFailureReason(ApiRequestException exception)
    {
        if (TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(exception.ErrorCode, exception.Message))
            return "telegram_message_not_modified";
        return TelegramDeliveryFailureClassifier.Classify(exception);
    }

    /// <summary>Retains full local wait detail and observes one connected operator incident without delivering it.</summary>
    /// <param name="item">Claimed private work item; only identity and timing metadata are used.</param>
    /// <param name="sequence">Internal inbox sequence of the victim.</param>
    /// <param name="waitMilliseconds">Queue wait in milliseconds, ending at the persisted UTC claim time.</param>
    /// <param name="token">Execution cancellation for the metadata-only predecessor read.</param>
    /// <returns>A task completing after safe local diagnostics and memory-only aggregation.</returns>
    /// <remarks>
    /// Each delayed update retains a Warning detail locally, even if its attributed sequence changes. Operator
    /// summaries are emitted separately by the coordinator. Ordinary lookup/aggregation/logger failures are
    /// diagnostic-only. Cancellation still propagates to the existing execution_cancelled terminal policy before
    /// business execution. No payload is loaded for attribution.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The predecessor read was cancelled; business execution must not begin.</exception>
    /// <example><code>await ReportLongQueueWaitAsync(item, item.Sequence, waitMilliseconds, token);</code></example>
    private async Task ReportLongQueueWaitAsync(TelegramUpdateWorkItem item, long sequence, double waitMilliseconds, CancellationToken token)
    {
        TelegramLaneExecutionSummary blocker = null;
        try
        {
            blocker = await _store.FindPreviousLaneExecutionAsync(
                sequence, item.Key.BotId, item.Key.TelegramUserId, item.AcceptedAtUtc, item.StartedAtUtc, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Attribution failure is optional; cancellation must still prevent business effects.
        }
        try
        {
            _queueIncidents.Observe(item.Key, item.AcceptedAtUtc, item.StartedAtUtc, waitMilliseconds, blocker, DateTime.UtcNow);
            Wake();
            _logger.LogWarning(
                "Telegram update waited unusually long. BotId={BotId} TelegramUserId={TelegramUserId} WaitingSequence={WaitingSequence} WaitingUpdateId={WaitingUpdateId} WaitingUpdateType={WaitingUpdateType} QueueWaitMs={QueueWaitMs} AcceptedAtUtc={AcceptedAtUtc} StartedAtUtc={StartedAtUtc} PreviousSequence={PreviousSequence} PreviousUpdateId={PreviousUpdateId} PreviousUpdateType={PreviousUpdateType} PreviousHandlerDurationMs={PreviousHandlerDurationMs} BlockingOverlapMs={BlockingOverlapMs}",
                item.Key.BotId, item.Key.TelegramUserId, sequence, item.Update.Id, item.Update.Type, waitMilliseconds,
                item.AcceptedAtUtc, item.StartedAtUtc, blocker?.Sequence ?? 0, blocker?.UpdateId ?? 0,
                blocker?.UpdateType ?? string.Empty, blocker?.HandlerDurationMs ?? 0, blocker?.BlockingOverlapMs ?? 0);
        }
        catch
        {
            // Memory-only diagnostics and local logging never participate in business success.
        }
    }

    /// <summary>Drains bounded incident summaries on the tracked coordinator, never on the handler path.</summary>
    /// <param name="shutdown">True closes all pending windows before the coordinator exits.</param>
    /// <remarks>The ordinary structured logger uses best-effort operator delivery; it is not durable financial logging. Sink exceptions are isolated and never retried.</remarks>
    private void FlushQueueIncidents(bool shutdown = false)
    {
        try
        {
            _queueIncidents.Flush(DateTime.UtcNow, summary =>
                _logger.LogWarning(
                    "Telegram queue-delay incident. BotId={BotId} TelegramUserId={TelegramUserId} AffectedCount={AffectedCount} MaxQueueWaitMs={MaxQueueWaitMs} DominantBlockerSequence={DominantBlockerSequence} DominantBlockerUpdateId={DominantBlockerUpdateId} DominantBlockerType={DominantBlockerType} DominantBlockerDurationMs={DominantBlockerDurationMs} DominantBlockerOverlapMs={DominantBlockerOverlapMs} StartedAtUtc={StartedAtUtc} EndedAtUtc={EndedAtUtc} WindowStartedAtUtc={WindowStartedAtUtc} WindowEndedAtUtc={WindowEndedAtUtc} WindowMs={WindowMs} Reason={Reason} Overflow={Overflow}",
                    summary.BotId, summary.TelegramUserId, summary.AffectedCount, summary.MaxQueueWaitMs,
                    summary.DominantBlocker?.Sequence ?? 0, summary.DominantBlocker?.UpdateId ?? 0,
                    summary.DominantBlocker?.UpdateType ?? string.Empty, summary.DominantBlocker?.HandlerDurationMs ?? 0,
                    summary.DominantBlocker?.BlockingOverlapMs ?? 0, summary.StartedAtUtc, summary.EndedAtUtc,
                    summary.WindowStartedAtUtc, summary.WindowEndedAtUtc, TelegramQueueDelayIncidentAggregator.Window.TotalMilliseconds,
                    summary.Reason, summary.Overflow), shutdown);
        }
        catch
        {
            // A diagnostic maintenance fault cannot pause scheduling or weaken completion persistence.
        }
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
    /// not itself an alert, and <see cref="TelegramLogSuppression"/> withholds the family from the central Telegram
    /// logger channel so per-stage measurements cannot look like incidents to operators.
    /// </remarks>
    private void ReportSlowStage(string botId, long sequence, int updateId, UpdateType updateType, TelegramUpdateStage stage, double elapsedMilliseconds)
    {
        _logger.LogInformation(
            "Telegram slow update stage. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} UpdateType={UpdateType} Stage={Stage} ElapsedMs={ElapsedMs}",
            botId, sequence, updateId, updateType, stage, elapsedMilliseconds);
    }

    /// <summary>Records one completed foreground Telegram attempt as local-only, payload-free timing telemetry.</summary>
    /// <param name="botId">Canonical runtime bot id owning this update; never a token.</param>
    /// <param name="sequence">Internal durable inbox sequence of the executing handler.</param>
    /// <param name="updateId">Telegram update id, not a message or chat id.</param>
    /// <param name="updateType">Telegram SDK update category; no user content.</param>
    /// <param name="observation">Closed request kind/outcome, millisecond timings, counts and optional numeric API code only.</param>
    /// <remarks>
    /// The scope isolates recorder failures from delivery. The operator provider withholds this exact family; original
    /// API failures and budget incidents still use their existing routing. Healthy attempts are retained so sequential
    /// calls can be distinguished from one slow call. File persistence follows the configured local minimum level.
    /// </remarks>
    /// <example><code>ReportTelegramRequest(item.Key.BotId, sequence, item.Update.Id, item.Update.Type, observation);</code></example>
    private void ReportTelegramRequest(string botId, long sequence, int updateId, UpdateType updateType,
        TelegramForegroundRequestObservation observation)
    {
        _logger.LogInformation(
            "Telegram foreground request completed. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} UpdateType={UpdateType} RequestKind={RequestKind} Outcome={Outcome} RequestElapsedMs={RequestElapsedMs} HandlerElapsedMs={HandlerElapsedMs} ErrorCode={ErrorCode} TelegramRequestCount={TelegramRequestCount} TotalTelegramElapsedMs={TotalTelegramElapsedMs}",
            botId, sequence, updateId, updateType, observation.Kind, observation.Outcome, observation.RequestElapsedMs,
            observation.HandlerElapsedMs, observation.ApiErrorCode, observation.RequestCount, observation.TotalTelegramElapsedMs);
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
    /// <param name="telegramRequestCount">Number of mapped foreground attempts started, including failed or cancelled calls; zero if no scope ran.</param>
    /// <param name="totalTelegramElapsedMs">Sum of measured request durations in milliseconds; concurrent calls may exceed handler wall time.</param>
    /// <remarks>
    /// Exactly one Warning is emitted per slow root handler: the live watchdog normally fires while it is still
    /// running, and this completion record is then logged at Information. When the watchdog could not fire — for example
    /// the handler finished in the same instant the threshold crossed — the completion record itself carries the
    /// Warning, so a long handler is never invisible. Handlers between the interactive and long thresholds are recorded
    /// at Information because a production sample contained several of them and they are not individually actionable.
    ///
    /// Channel routing follows the same split: the interactive-threshold line below the long-handler value and the
    /// long-handler completion echo are performance telemetry and stay out of the central Telegram logger channel, while
    /// the completion line at or above the long-handler value remains channel-visible when the live watchdog did not
    /// already report it. See <see cref="TelegramLogSuppression"/>.
    /// </remarks>
    private void RecordHandlerDurationDiagnostic(
        double durationMilliseconds,
        string failureCode,
        TelegramUpdateWorkItem item,
        long sequence,
        bool liveWarningEmitted,
        long telegramRequestCount,
        double totalTelegramElapsedMs)
    {
        if (durationMilliseconds < InteractiveHandlerThreshold.TotalMilliseconds)
            return;

        var botId = item?.Key.BotId ?? "unknown";
        var updateId = item?.Update.Id ?? 0;
        var outcome = failureCode ?? "completed";
        var isLongHandler = durationMilliseconds >= LongHandlerWarningThreshold.TotalMilliseconds;
        if (isLongHandler && !liveWarningEmitted)
            _logger.LogWarning(
                "Telegram update handler exceeded the interactive latency threshold. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome} TelegramRequestCount={TelegramRequestCount} TotalTelegramElapsedMs={TotalTelegramElapsedMs}",
                botId, sequence, updateId, durationMilliseconds, outcome, telegramRequestCount, totalTelegramElapsedMs);
        else if (isLongHandler)
            _logger.LogInformation(
                "Telegram long update handler completed. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome} TelegramRequestCount={TelegramRequestCount} TotalTelegramElapsedMs={TotalTelegramElapsedMs}",
                botId, sequence, updateId, durationMilliseconds, outcome, telegramRequestCount, totalTelegramElapsedMs);
        else
            _logger.LogInformation(
                "Telegram update handler exceeded the interactive latency threshold. BotId={BotId} Sequence={Sequence} UpdateId={UpdateId} HandlerDurationMs={HandlerDurationMs} Outcome={Outcome} TelegramRequestCount={TelegramRequestCount} TotalTelegramElapsedMs={TotalTelegramElapsedMs}",
                botId, sequence, updateId, durationMilliseconds, outcome, telegramRequestCount, totalTelegramElapsedMs);
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
            try { await _store.RecoverAsync(persistence.Token, preRestartTelemetry: false); }
            catch (Exception failure) { _logger.LogError("Shutdown recovery persistence failed; running claims will be finalized on restart. ErrorType={ErrorType}", failure.GetType().Name); }
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
            {
                _logger.LogCritical("Telegram handlers did not stop within cancellation grace. ActiveHandlers={ActiveHandlers}", ActiveHandlerCount);
            }
        }
        finally { FlushQueueIncidents(shutdown: true); }
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
