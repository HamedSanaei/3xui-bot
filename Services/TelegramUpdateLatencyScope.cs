using System;
using System.Threading;

/// <summary>
/// Closed vocabulary of execution stages inside one Telegram update handler.
/// </summary>
/// <remarks>
/// The vocabulary exists so per-stage latency can be attributed without ever putting customer or provider data into a
/// log line. Stage names are rendered from the enum member name only, so no email, UUID, subscription id, URL,
/// callback payload, order id, token, or customer text can ever become part of a stage string.
/// </remarks>
public enum TelegramUpdateStage
{
    /// <summary>A user-facing XUI panel read (account list, search, detail reload, renewal target discovery).</summary>
    XuiRead,

    /// <summary>A non-durable interactive Telegram message send.</summary>
    TelegramSend,

    /// <summary>A non-durable interactive Telegram message edit.</summary>
    TelegramEdit,

    /// <summary>A Telegram membership probe (mandatory-join verification or chat lookup).</summary>
    TelegramMembership,

    /// <summary>A Gozargah site API lookup.</summary>
    SiteLookup,

    /// <summary>A payment provider inquiry or reconciliation read.</summary>
    ProviderRead,

    /// <summary>Time blocked on a local database operation.</summary>
    DatabaseWait,

    /// <summary>Durable business recovery work performed inside the handler.</summary>
    BusinessRecovery,

    /// <summary>A Telegram token identity/GetMe probe; token and returned identity are never stage metadata.</summary>
    TelegramProbe,

    /// <summary>One awaited XUI mutation attempt; measurement never changes retry or financial authorization.</summary>
    XuiMutation
}

/// <summary>
/// Carries the identity of the currently executing Telegram update and accumulates per-stage latency observations for
/// it, using <see cref="AsyncLocal{T}"/> so concurrent lanes never share measurements.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// Production evidence could prove that a handler ran for roughly 104 seconds and that later updates from the same
/// bot/user key waited behind it, but it could not attribute the time to one external call. This scope adds a
/// lightweight, closed-vocabulary stage timer so a slow handler can be attributed to <c>xui_read</c>,
/// <c>telegram_send</c>, <c>telegram_membership</c>, <c>site_lookup</c>, or another known stage.
/// </para>
/// <para>
/// The scope owns reusable request-state storage; it grows only when the update reaches a new concurrency high.
/// Stage and request timers are structs; normal sequential requests do not allocate diagnostic objects.
/// Slow stages and all foreground request completions are reported without retaining payloads.
/// </para>
/// <para>
/// Lifetime:
/// The scheduler pushes one scope around the handler execution and disposes it afterwards. Stage timers created by
/// nested calls complete before that disposal, because they are created and disposed inline around a single awaited
/// call.
/// </para>
/// </remarks>
public sealed class TelegramUpdateLatencyScope : IDisposable
{
    /// <summary>Holds the scope for the current asynchronous update execution context.</summary>
    private static readonly AsyncLocal<TelegramUpdateLatencyScope> Ambient = new();

    /// <summary>Enclosing scope restored after this scope finishes.</summary>
    private readonly TelegramUpdateLatencyScope _previous;

    /// <summary>Monotonic scope creation timestamp used for whole-handler attribution.</summary>
    private readonly long _startedTimestamp;

    /// <summary>Idempotency flag preventing double disposal from restoring the ambient scope twice.</summary>
    private int _disposed;

    /// <summary>Serializes request-state changes and watchdog snapshots without locking around network operations.</summary>
    private readonly Lock _telegramRequestGate = new();

    /// <summary>Inline slot for the ordinary sequential request path; no request-state array allocation is needed.</summary>
    private TelegramRequestState _primaryTelegramRequest;

    /// <summary>Reusable additional slots, allocated only when requests actually overlap.</summary>
    private TelegramRequestState[] _telegramRequests = Array.Empty<TelegramRequestState>();

    /// <summary>Number of occupied request slots, guarded by the request metadata lock.</summary>
    private int _activeTelegramRequestCount;

    /// <summary>Number of foreground attempts started in this update, also used as unique slot ownership ids.</summary>
    private long _requestCount;

    /// <summary>Sum of completed foreground request durations in milliseconds.</summary>
    private double _completedTelegramElapsedMs;

    /// <summary>Monotonic time source; the system provider is used unless a controlled provider is supplied.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Local metadata-only completion recorder; never used for operator incidents.</summary>
    private readonly Action<TelegramForegroundRequestObservation> _onTelegramRequest;

    /// <summary>
    /// Closed-vocabulary stage currently being executed by this scope, or <c>null</c> while the handler is between
    /// instrumented stages.
    /// </summary>
    /// <remarks>
    /// Only an enum member name can ever be exposed here, so a callback payload, chat id, bot token, URL, account id,
    /// or customer text can never reach a diagnostic line through this value. The field records the innermost
    /// instrumented stage and is written only by <see cref="EnterStage"/> and <see cref="ExitStage"/>.
    /// </remarks>
    private TelegramUpdateStage? _currentStage;

    /// <summary>
    /// Initializes one scope for a single Telegram update execution and remembers the enclosing ambient scope.
    /// </summary>
    /// <param name="sequence">Internal inbox sequence of the update being executed; never a secret.</param>
    /// <param name="botId">Canonical runtime bot id of the update being executed; never a token.</param>
    /// <param name="updateId">Telegram update id of the update being executed; never customer content.</param>
    /// <param name="stageThreshold">
    /// Minimum stage duration that is reported. Using a threshold keeps a healthy handler completely silent.
    /// </param>
    /// <param name="onSlowStage">
    /// Callback invoked with the closed-vocabulary stage and its elapsed milliseconds when a stage exceeds
    /// <paramref name="stageThreshold"/>. It must not throw and must not send Telegram messages.
    /// </param>
    /// <param name="onTelegramRequest">Optional local recorder for every foreground completion; failures are swallowed.</param>
    /// <param name="timeProvider">Optional monotonic clock for deterministic measurements; null uses the system clock.</param>
    private TelegramUpdateLatencyScope(
        long sequence,
        string botId,
        long updateId,
        TimeSpan stageThreshold,
        Action<TelegramUpdateStage, double> onSlowStage,
        Action<TelegramForegroundRequestObservation> onTelegramRequest,
        TimeProvider timeProvider)
    {
        Sequence = sequence;
        BotId = botId;
        UpdateId = updateId;
        StageThreshold = stageThreshold;
        OnSlowStage = onSlowStage;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onTelegramRequest = onTelegramRequest;
        _startedTimestamp = _timeProvider.GetTimestamp();
        _previous = Ambient.Value;
    }

    /// <summary>Gets the internal inbox sequence of the update that owns this scope.</summary>
    public long Sequence { get; }

    /// <summary>Gets the canonical runtime bot id of the update that owns this scope.</summary>
    public string BotId { get; }

    /// <summary>Gets the Telegram update id of the update that owns this scope.</summary>
    public long UpdateId { get; }

    /// <summary>Gets the minimum stage duration that is reported.</summary>
    public TimeSpan StageThreshold { get; }

    /// <summary>Gets the closed-vocabulary slow-stage recorder supplied by the scheduler.</summary>
    private Action<TelegramUpdateStage, double> OnSlowStage { get; }

    /// <summary>
    /// Gets the scope active in the current asynchronous execution context, or <c>null</c> when none is pushed.
    /// </summary>
    /// <remarks>
    /// Shared helper code — the foreground Telegram client decorator and the XUI transport — reads this property and
    /// simply does nothing when no scope is active, so the instrumentation can never break a background caller.
    /// </remarks>
    public static TelegramUpdateLatencyScope Current => Ambient.Value;

    /// <summary>
    /// Gets the closed-vocabulary stage this handler is currently executing, or <c>null</c> between instrumented stages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scheduler reads this value when its live long-handler watchdog fires so an operator alert can name the stage
    /// that is actually blocking the lane (<c>TelegramMembership</c>, <c>TelegramSend</c>, <c>XuiRead</c>, and so on)
    /// instead of only reporting the whole-handler duration. Without it, a slow probe and a slow panel read are
    /// indistinguishable at the alert level.
    /// </para>
    /// <para>
    /// Safety:
    /// the value is an enum member name only. No callback text, chat id, bot token, URL, account id, or payload is ever
    /// stored, and the property is never used for authorization.
    /// </para>
    /// <para>
    /// Precision:
    /// when a handler runs instrumented stages concurrently the value reflects the most recently entered stage, which is
    /// sufficient for attribution but must not be treated as an exact nesting stack.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var stage = TelegramUpdateLatencyScope.Current?.CurrentStage?.ToString() ?? "none";
    /// </code>
    /// </example>
    public TelegramUpdateStage? CurrentStage => _currentStage;

    /// <summary>
    /// Pushes one update-local latency scope and returns it for disposal after the handler finishes.
    /// </summary>
    /// <param name="sequence">Internal inbox sequence of the update being executed.</param>
    /// <param name="botId">Canonical runtime bot id of the update being executed; never a token.</param>
    /// <param name="updateId">Telegram update id of the update being executed.</param>
    /// <param name="stageThreshold">Minimum stage duration that is reported; must be positive.</param>
    /// <param name="onSlowStage">Closed-vocabulary recorder invoked only for stages above the threshold.</param>
    /// <param name="onTelegramRequest">
    /// Optional local metadata-only recorder for every foreground request completion. Recorder exceptions cannot alter delivery.
    /// </param>
    /// <param name="timeProvider">Optional monotonic time source; null uses TimeProvider.System and does not affect delivery budgets.</param>
    /// <returns>A disposable scope that restores the previous ambient scope when disposed.</returns>
    /// <remarks>
    /// The scheduler owns scope lifetime. Request snapshots disappear at disposal; late request completion still
    /// releases its metadata slot and updates elapsed totals, but no longer invokes the request recorder.
    /// The optional clock changes diagnostic measurements only, never transport deadlines or lane scheduling.
    /// </remarks>
    /// <example>
    /// <code>
    /// using (TelegramUpdateLatencyScope.Push(item.Sequence, item.Key.BotId, item.Update.Id, threshold, Report))
    ///     await _executor.ExecuteAsync(item, token);
    /// </code>
    /// </example>
    public static TelegramUpdateLatencyScope Push(
        long sequence,
        string botId,
        long updateId,
        TimeSpan stageThreshold,
        Action<TelegramUpdateStage, double> onSlowStage,
        Action<TelegramForegroundRequestObservation> onTelegramRequest = null,
        TimeProvider timeProvider = null)
    {
        var scope = new TelegramUpdateLatencyScope(sequence, botId, updateId, stageThreshold, onSlowStage,
            onTelegramRequest, timeProvider);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Starts a stage measurement that reports itself when disposed.
    /// </summary>
    /// <param name="stage">Closed-vocabulary stage being measured.</param>
    /// <returns>
    /// A struct timer that must be disposed around the measured call. Disposing reports the stage only when its elapsed
    /// time exceeds <see cref="StageThreshold"/>. A default instance or a diagnostic clock failure reports nothing.
    /// </returns>
    /// <remarks>
    /// Callers use <c>using</c> around the awaited operation so the measurement also completes on an exception path,
    /// including a foreground budget expiry.
    /// </remarks>
    /// <example><code>using var timer = scope.Measure(TelegramUpdateStage.XuiRead); await ReadPanelAsync();</code></example>
    public StageTimer Measure(TelegramUpdateStage stage)
    {
        try
        {
            return new StageTimer(this, stage);
        }
        catch
        {
            // A diagnostic clock failure cannot prevent the measured business operation.
            return default;
        }
    }

    /// <summary>
    /// Records that a closed-vocabulary stage started and returns the stage that was current before it.
    /// </summary>
    /// <param name="stage">Closed-vocabulary stage that is starting.</param>
    /// <returns>
    /// The stage that was current before this one started, so the caller can restore it when the measurement completes.
    /// </returns>
    /// <remarks>
    /// Called by <see cref="StageTimer"/> creation. It performs no logging and no I/O, so it can be used around every
    /// awaited external call without measurable cost.
    /// </remarks>
    internal TelegramUpdateStage? EnterStage(TelegramUpdateStage stage)
    {
        var previous = _currentStage;
        _currentStage = stage;
        return previous;
    }

    /// <summary>
    /// Restores the previously current stage after a completed measurement.
    /// </summary>
    /// <param name="stage">Stage whose measurement just completed.</param>
    /// <param name="previous">Stage returned by <see cref="EnterStage"/> when that measurement started.</param>
    /// <remarks>
    /// The restore is skipped when a different stage became current in the meantime, which is what happens when a
    /// handler overlaps two instrumented stages. In that case the later stage keeps ownership of the value.
    /// </remarks>
    internal void ExitStage(TelegramUpdateStage stage, TelegramUpdateStage? previous)
    {
        if (_currentStage == stage)
            _currentStage = previous;
    }

    /// <summary>Gets the elapsed time since this scope was created.</summary>
    /// <returns>The monotonic elapsed time of the whole update execution.</returns>
    /// <remarks>Uses the scope's monotonic provider; request budgets and scheduling never use this diagnostic clock.</remarks>
    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startedTimestamp);

    /// <summary>Gets the number of foreground Telegram attempts started in this update, including active requests.</summary>
    /// <remarks>The count is update-local and includes failures and cancellations; it is never a retry count.</remarks>
    public long RequestCount
    {
        get
        {
            lock (_telegramRequestGate)
                return _requestCount;
        }
    }

    /// <summary>Gets summed foreground Telegram milliseconds, including elapsed time of currently active requests.</summary>
    /// <remarks>Overlapping durations are summed and can exceed the handler's wall-clock elapsed time.</remarks>
    public double TotalTelegramElapsedMs
    {
        get
        {
            lock (_telegramRequestGate)
                return _activeTelegramRequestCount == 0
                    ? _completedTelegramElapsedMs
                    : GetTotalTelegramElapsedMs(_timeProvider.GetTimestamp());
        }
    }

    /// <summary>Gets a consistent metadata-only snapshot of the most recently started active foreground request.</summary>
    /// <remarks>
    /// Safe to read from a watchdog thread. Returns null between requests or after scope disposal. Removing one
    /// completed request reveals only another still-active request, never a completed enclosing or overlapping call.
    /// The snapshot's outcome is always InProgress and its API error code is always null.
    /// </remarks>
    /// <example><code>var request = scope.CurrentTelegramRequest; // Safe on a timer thread.</code></example>
    public TelegramForegroundRequestObservation? CurrentTelegramRequest
    {
        get
        {
            lock (_telegramRequestGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _activeTelegramRequestCount == 0)
                    return null;

                var current = -1;
                for (var index = 0; index <= _telegramRequests.Length; index++)
                {
                    if (GetTelegramRequestSlot(index).Id != 0 &&
                        (current < 0 || GetTelegramRequestSlot(index).Id > GetTelegramRequestSlot(current).Id))
                        current = index;
                }
                if (current < 0)
                    return null;

                var now = _timeProvider.GetTimestamp();
                ref readonly var state = ref GetTelegramRequestSlot(current);
                return new TelegramForegroundRequestObservation(
                    state.Kind, TelegramForegroundRequestOutcome.InProgress,
                    _timeProvider.GetElapsedTime(state.StartedTimestamp, now).TotalMilliseconds,
                    _timeProvider.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds,
                    null, _requestCount, GetTotalTelegramElapsedMs(now));
            }
        }
    }

    /// <summary>Starts one metadata-only request timer using a reusable active slot.</summary>
    /// <param name="kind">Explicit closed-vocabulary mapping of the actual bounded request.</param>
    /// <returns>A non-allocating timer, or a no-op timer if the scope is disposed or diagnostics cannot start.</returns>
    /// <remarks>
    /// Called immediately around the decorator's single inner SendRequest await. The lock protects only metadata,
    /// never network work. Unique slot ownership prevents late or duplicate completion from removing a newer request.
    /// </remarks>
    /// <example><code>var timer = scope.MeasureTelegramRequest(kind); // Complete once in the request's finally block.</code></example>
    internal TelegramRequestTimer MeasureTelegramRequest(TelegramForegroundRequestKind kind)
    {
        try
        {
            lock (_telegramRequestGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return default;

                var started = _timeProvider.GetTimestamp();
                var slot = 0;
                while (slot <= _telegramRequests.Length && GetTelegramRequestSlot(slot).Id != 0)
                    slot++;
                if (slot > _telegramRequests.Length)
                    Array.Resize(ref _telegramRequests, Math.Max(1, _telegramRequests.Length * 2));

                var id = ++_requestCount;
                GetTelegramRequestSlot(slot) = new TelegramRequestState
                {
                    Id = id,
                    Kind = kind,
                    StartedTimestamp = started
                };
                _activeTelegramRequestCount++;
                return new TelegramRequestTimer(this, slot, id);
            }
        }
        catch
        {
            // Diagnostics failure must never prevent the actual Telegram attempt.
            return default;
        }
    }

    /// <summary>Completes one still-owned active slot and invokes the local recorder outside the metadata lock.</summary>
    /// <param name="slot">Reusable slot allocated when the request started.</param>
    /// <param name="id">Unique request ownership id, preventing stale timer completion.</param>
    /// <param name="outcome">Closed-vocabulary terminal result of the single attempt.</param>
    /// <param name="apiErrorCode">Numeric Telegram API error code, or null for a non-API result.</param>
    /// <remarks>All instrumentation and recorder failures are swallowed; original delivery results remain unchanged.</remarks>
    /// <example><code>CompleteTelegramRequest(slot, requestId, TelegramForegroundRequestOutcome.TelegramApiError, 403);</code></example>
    private void CompleteTelegramRequest(int slot, long id, TelegramForegroundRequestOutcome outcome, int? apiErrorCode)
    {
        try
        {
            TelegramForegroundRequestObservation observation;
            lock (_telegramRequestGate)
            {
                if (slot > _telegramRequests.Length || GetTelegramRequestSlot(slot).Id != id)
                    return;

                var state = GetTelegramRequestSlot(slot);
                GetTelegramRequestSlot(slot) = default;
                _activeTelegramRequestCount--;
                // Remove ownership before reading the diagnostic clock so a clock failure cannot leave a completed call active.
                var now = _timeProvider.GetTimestamp();
                var elapsed = _timeProvider.GetElapsedTime(state.StartedTimestamp, now).TotalMilliseconds;
                _completedTelegramElapsedMs += elapsed;
                if (_onTelegramRequest == null || Volatile.Read(ref _disposed) != 0)
                    return;
                observation = new TelegramForegroundRequestObservation(
                    state.Kind, outcome, elapsed,
                    _timeProvider.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds,
                    apiErrorCode, _requestCount, GetTotalTelegramElapsedMs(now));
            }
            if (Volatile.Read(ref _disposed) == 0)
                _onTelegramRequest?.Invoke(observation);
        }
        catch
        {
            // Neither recorder failure nor metadata failure may change delivery or exception identity.
        }
    }

    /// <summary>Sums completed request durations and active elapsed durations at one consistent timestamp.</summary>
    /// <param name="now">Monotonic timestamp from this scope's provider; the caller must hold the metadata lock.</param>
    /// <returns>The summed Telegram request milliseconds; concurrent durations intentionally overlap.</returns>
    /// <remarks>The caller holds the metadata lock and supplies one timestamp for a consistent aggregate.</remarks>
    /// <example><code>var totalMs = GetTotalTelegramElapsedMs(_timeProvider.GetTimestamp());</code></example>
    private double GetTotalTelegramElapsedMs(long now)
    {
        var elapsed = _completedTelegramElapsedMs;
        for (var index = 0; index <= _telegramRequests.Length; index++)
        {
            ref readonly var state = ref GetTelegramRequestSlot(index);
            if (state.Id != 0)
                elapsed += _timeProvider.GetElapsedTime(state.StartedTimestamp, now).TotalMilliseconds;
        }
        return elapsed;
    }

    /// <summary>Addresses the inline sequential slot or an additional overlapping-request slot without copying state.</summary>
    /// <param name="slot">Zero for the inline slot; positive values address the secondary array at slot minus one.</param>
    /// <returns>A reference to request metadata owned by this scope; callers must not retain it across lock release or array growth.</returns>
    /// <remarks>Called only under the metadata lock. The common single-request path never allocates a slot array.</remarks>
    /// <example><code>GetTelegramRequestSlot(slot) = default; // While holding the metadata lock.</code></example>
    private ref TelegramRequestState GetTelegramRequestSlot(int slot)
    {
        if (slot == 0) return ref _primaryTelegramRequest;
        return ref _telegramRequests[slot - 1];
    }

    /// <summary>Reusable active-request metadata with no reference to the Telegram request or its payload.</summary>
    private struct TelegramRequestState
    {
        /// <summary>Unique ownership id; zero identifies an available slot.</summary>
        internal long Id;
        /// <summary>Explicit foreground operation classification.</summary>
        internal TelegramForegroundRequestKind Kind;
        /// <summary>Monotonic request-start timestamp from the scope's provider.</summary>
        internal long StartedTimestamp;
    }

    /// <summary>Non-allocating completion handle for one reusable request slot.</summary>
    /// <remarks>A default handle is a no-op, and completing a copied handle cannot report the same request twice.</remarks>
    internal readonly struct TelegramRequestTimer
    {
        /// <summary>Owning update scope, or null for a no-op handle.</summary>
        private readonly TelegramUpdateLatencyScope _scope;
        /// <summary>Reusable metadata slot index.</summary>
        private readonly int _slot;
        /// <summary>Unique ownership id assigned at request start.</summary>
        private readonly long _id;

        /// <summary>Creates a non-allocating handle for an already registered request.</summary>
        /// <param name="scope">Update-local metadata owner.</param>
        /// <param name="slot">Active slot index owned by this request.</param>
        /// <param name="id">Unique ownership id assigned by the scope.</param>
        internal TelegramRequestTimer(TelegramUpdateLatencyScope scope, int slot, long id)
        {
            _scope = scope;
            _slot = slot;
            _id = id;
        }

        /// <summary>Reports the terminal result once and clears only this request's active slot.</summary>
        /// <param name="outcome">Closed terminal outcome of the actual inner SendRequest await.</param>
        /// <param name="apiErrorCode">Optional numeric API status; never exception text or response content.</param>
        /// <remarks>Never throws because the owning scope isolates diagnostic and recorder failures.</remarks>
        internal void Complete(TelegramForegroundRequestOutcome outcome, int? apiErrorCode)
            => _scope?.CompleteTelegramRequest(_slot, _id, outcome, apiErrorCode);
    }

    /// <summary>Reports one completed stage measurement against this scope.</summary>
    /// <param name="stage">Closed-vocabulary stage that completed.</param>
    /// <param name="elapsedMilliseconds">Monotonic elapsed milliseconds for the stage.</param>
    /// <remarks>
    /// The recorder is invoked only above the configured threshold, and a recorder failure is swallowed so
    /// instrumentation can never fail a customer operation.
    /// </remarks>
    private void Report(TelegramUpdateStage stage, double elapsedMilliseconds)
    {
        if (elapsedMilliseconds < StageThreshold.TotalMilliseconds)
            return;

        try
        {
            OnSlowStage?.Invoke(stage, elapsedMilliseconds);
        }
        catch
        {
            // Instrumentation must never surface as a customer-visible failure.
        }
    }

    /// <summary>Restores the enclosing ambient scope for this asynchronous execution context.</summary>
    /// <remarks>Safe to call more than once; only the first call restores the previous scope.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Ambient.Value = _previous;
    }

    /// <summary>
    /// Measures exactly one stage of an update and reports it on disposal.
    /// </summary>
    /// <remarks>
    /// This is a readonly struct so that taking a measurement costs no heap allocation. A default instance is a no-op,
    /// which lets a caller write <c>using var timer = TelegramUpdateLatencyScope.Current?.Measure(stage) ?? default;</c>
    /// in cold paths as well as in shared transport code.
    /// </remarks>
    public readonly struct StageTimer : IDisposable
    {
        /// <summary>Scope that owns the measurement, or <c>null</c> for a no-op timer.</summary>
        private readonly TelegramUpdateLatencyScope _scope;

        /// <summary>Closed-vocabulary stage being measured.</summary>
        private readonly TelegramUpdateStage _stage;

        /// <summary>Monotonic start timestamp of the measurement.</summary>
        private readonly long _startedTimestamp;

        /// <summary>Stage that was current before this measurement started, restored on disposal.</summary>
        private readonly TelegramUpdateStage? _previousStage;

        /// <summary>Creates a measurement timer for one stage of one scope.</summary>
        /// <param name="scope">Owning scope; required.</param>
        /// <param name="stage">Closed-vocabulary stage being measured.</param>
        /// <remarks>
        /// Creation also publishes the stage as the scope's current stage, so a live long-handler watchdog firing while
        /// this call is in flight can name the exact stage that is blocking the lane.
        /// </remarks>
        internal StageTimer(TelegramUpdateLatencyScope scope, TelegramUpdateStage stage)
        {
            _scope = scope;
            _stage = stage;
            _startedTimestamp = scope._timeProvider.GetTimestamp();
            _previousStage = scope.EnterStage(stage);
        }

        /// <summary>
        /// Completes the measurement, reports it to the owning scope when it exceeded the stage threshold, and restores
        /// the previously current stage.
        /// </summary>
        /// <remarks>No-op timers and diagnostic clock failures cannot change the operation's result or exception.</remarks>
        public void Dispose()
        {
            if (_scope == null)
                return;

            try
            {
                _scope.Report(_stage, _scope._timeProvider.GetElapsedTime(_startedTimestamp).TotalMilliseconds);
            }
            catch
            {
                // A diagnostic clock failure cannot replace an operation's response or exception.
            }
            finally
            {
                _scope.ExitStage(_stage, _previousStage);
            }
        }
    }
}
