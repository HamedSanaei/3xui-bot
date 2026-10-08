using System;
using System.Threading;
using Adminbot.Services.Telemetry;
using System.Collections.Generic;

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
    XuiMutation,

    /// <summary>A callback acknowledgement, which is not a visible message response.</summary>
    TelegramCallbackAck,
    /// <summary>A Telegram long-poll request; not a foreground visible response.</summary>
    TelegramPolling,
    /// <summary>A local SQLite read command or transaction boundary.</summary>
    SqliteRead,
    /// <summary>A local SQLite write command or transaction boundary.</summary>
    SqliteWrite,
    /// <summary>The existing bounded SQLite BUSY/LOCKED retry delay.</summary>
    SqliteBusyRetry,
    /// <summary>An external HTTP operation outside Telegram, XUI, site, and payment providers.</summary>
    ExternalHttp,
    /// <summary>Waiting for an in-process business resource gate; resource identifiers are never retained.</summary>
    LockWait,
    /// <summary>Explicitly identified local dispatch or business work, not an assumed CPU measurement.</summary>
    BusinessProcessing
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
/// Active request/stage storage is reused and capped at 1,024 overlapping handles, with explicit overflow provenance.
/// Timers are structs; enabled telemetry emits detached metadata records, while the original local recorder remains
/// compatible. Exclusive stage wall time and unattributed gaps partition handler duration; inclusive timings overlap.
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

    /// <summary>Limits retained overlapping diagnostic slots; overflow is explicitly marked, never silently attributed.</summary>
    private const int MaximumActiveSlots = 1024;
    /// <summary>Closed contiguous enum size, avoiding temporary Enum.GetValues allocations per update.</summary>
    private const int StageCount = (int)TelegramUpdateStage.BusinessProcessing + 1;
    /// <summary>Serializes stage transitions and gap accounting without holding a lock during business work.</summary>
    private readonly Lock _stageGate = new();
    /// <summary>Reusable active stage slots; the ordinary sequential case uses the inline slot.</summary>
    private StageState _primaryStage;
    /// <summary>Additional stage slots allocated only on a new concurrency high, up to the fixed limit.</summary>
    private StageState[] _stages = Array.Empty<StageState>();
    /// <summary>Unique timer ownership counter, protecting copied and late-disposed timers.</summary>
    private long _nextStageId;
    /// <summary>Exclusive milliseconds by enum value; every elapsed interval has exactly one owner.</summary>
    private readonly double[] _exclusiveStageMs = new double[StageCount];
    /// <summary>Inclusive completed milliseconds; nested and overlapping values must not be summed as wall time.</summary>
    private readonly double[] _inclusiveStageMs = new double[StageCount];
    /// <summary>Last timestamp at which exclusive wall time was assigned.</summary>
    private long _accountedTimestamp;
    /// <summary>Start of the currently open uninstrumented wall-time interval.</summary>
    private long _gapStartedTimestamp;
    /// <summary>Safe preceding stage label for the currently open gap.</summary>
    private string _gapBeforeStage = "handler_start";
    /// <summary>Safe label of the last completed attribution interval.</summary>
    private string _lastStage = "handler_start";
    /// <summary>Total uninstrumented handler wall time; not a measurement of CPU execution.</summary>
    private double _unattributedMs;
    /// <summary>Longest closed unattributed gap, retained without keeping a history of intervals.</summary>
    private double _maxGapMs;
    /// <summary>Handler-relative start offset of the longest closed gap.</summary>
    private double _maxGapStartedMs;
    /// <summary>Handler-relative end offset of the longest closed gap.</summary>
    private double _maxGapEndedMs;
    /// <summary>Safe preceding and following labels of the longest closed gap.</summary>
    private string _maxGapBeforeStage = "handler_start", _maxGapAfterStage = "handler_end";
    /// <summary>Whether diagnostic concurrency exceeded bounded active storage.</summary>
    private volatile bool _metadataCapacityExceeded;
    /// <summary>Frozen handler end timestamp; null while the scope is active.</summary>
    private long? _endedTimestamp;
    /// <summary>UTC anchor paired with the monotonic handler start.</summary>
    private readonly DateTime _handlerStartedUtc;
    /// <summary>Optional nonblocking metadata recorder; no file I/O happens in this scope.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>First visible attempt ownership id, distinguished from callback acknowledgements.</summary>
    private long _firstResponseId, _firstCallbackId;
    /// <summary>Handler-relative first visible attempt, first attempt completion, and first successful completion.</summary>
    private double? _firstResponseAttemptMs, _firstResponseCompletedMs, _firstResponseAcknowledgedMs;
    /// <summary>Handler-relative first callback attempt/success offsets and the first callback request's own duration.</summary>
    private double? _callbackAttemptMs, _callbackDurationMs, _callbackAcknowledgedMs;
    /// <summary>Completed foreground or callback-policy local deadline outcomes, excluding caller shutdown.</summary>
    private int _foregroundTimeoutCount;
    /// <summary>Actual bounded BUSY/LOCKED retries observed inside this update.</summary>
    private int _sqliteBusyRetryCount;
    /// <summary>Measured wall time spent in existing SQLite retry delays.</summary>
    private double _sqliteBusyWaitMs;
    /// <summary>Telegram inclusive elapsed total frozen at handler end while legacy late-completion totals remain compatible.</summary>
    private double? _handlerTelegramTotalMs;

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
    /// <param name="telemetry">Optional nonblocking telemetry writer; null preserves existing local diagnostics.</param>
    /// <param name="traceId">Optional opaque lifecycle trace id supplied by the inbox tracker; never customer input.</param>
    private TelegramUpdateLatencyScope(
        long sequence,
        string botId,
        long updateId,
        TimeSpan stageThreshold,
        Action<TelegramUpdateStage, double> onSlowStage,
        Action<TelegramForegroundRequestObservation> onTelegramRequest,
        TimeProvider timeProvider,
        LatencyTelemetryService telemetry,
        string traceId)
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
        _telemetry = telemetry;
        TraceId = traceId ?? Guid.NewGuid().ToString("N");
        _handlerStartedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _accountedTimestamp = _gapStartedTimestamp = _startedTimestamp;
    }

    /// <summary>Gets the internal inbox sequence of the update that owns this scope.</summary>
    public long Sequence { get; }

    /// <summary>Gets the canonical runtime bot id of the update that owns this scope.</summary>
    public string BotId { get; }

    /// <summary>Gets the Telegram update id of the update that owns this scope.</summary>
    public long UpdateId { get; }

    /// <summary>Gets the opaque payload-free id shared by all lifecycle observations of this update.</summary>
    public string TraceId { get; }

    /// <summary>Gets the UTC anchor paired with this scope's authoritative monotonic handler clock.</summary>
    /// <remarks>The scheduler uses this same anchor for lifecycle milestones; response offsets and stage totals share it.</remarks>
    public DateTime HandlerStartedAtUtc => _handlerStartedUtc;

    /// <summary>Gets the explicit update-local writer for diagnostic helpers without requiring a process-global binding.</summary>
    internal LatencyTelemetryService Telemetry => _telemetry;

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
    /// When instrumented stages overlap, the newest still-active timer owns exclusive wall time. Completion restores
    /// only another still-active timer, never a completed enclosing stage. Uninstrumented time is measured as gaps,
    /// without assuming that the handler was using CPU.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var stage = TelegramUpdateLatencyScope.Current?.CurrentStageName ?? "scope_unavailable";
    /// </code>
    /// </example>
    public TelegramUpdateStage? CurrentStage { get { lock (_stageGate) return _currentStage; } }

    /// <summary>Gets the safe current stage label, including an explicit uninstrumented-gap explanation.</summary>
    /// <remarks>Watchdogs should use this instead of rendering null as an unexplained <c>none</c>.</remarks>
    public string CurrentStageName => CurrentStage is { } stage ? StageName(stage) : "unattributed";

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
    /// <param name="telemetry">Optional writer for stage, gap, request, and first-visible-response milestones.</param>
    /// <param name="traceId">Optional lifecycle trace id from the update tracker; not a user or chat identifier.</param>
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
        TimeProvider timeProvider = null,
        LatencyTelemetryService telemetry = null,
        string traceId = null)
    {
        var scope = new TelegramUpdateLatencyScope(sequence, botId, updateId, stageThreshold, onSlowStage,
            onTelegramRequest, timeProvider, telemetry, traceId);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Starts a stage measurement that reports itself when disposed.
    /// </summary>
    /// <param name="stage">Closed-vocabulary stage being measured.</param>
    /// <returns>
    /// A struct timer disposed around the measured call. Enabled telemetry records each completion; the original
    /// slow-stage callback runs only above StageThreshold. Suppressed, disposed, or failed diagnostics return a no-op.
    /// </returns>
    /// <remarks>
    /// Callers use <c>using</c> around the awaited operation so the measurement also completes on an exception path,
    /// including foreground budget expiry. Telemetry suppression excludes logger/operator notification work.
    /// </remarks>
    /// <example><code>using var timer = scope.Measure(TelegramUpdateStage.XuiRead); await ReadPanelAsync();</code></example>
    public StageTimer Measure(TelegramUpdateStage stage)
    {
        if (LatencyTelemetrySuppression.IsActive) return default;
        try
        {
            return EnterStage(stage);
        }
        catch
        {
            // A diagnostic clock failure cannot prevent the measured business operation.
            return default;
        }
    }

    /// <summary>Maps both original and added stages to a closed, payload-free telemetry vocabulary.</summary>
    /// <param name="stage">Compile-time execution classification; unknown enum values map to unattributed.</param>
    /// <returns>A bounded snake-case category, never derived from customer or provider data.</returns>
    /// <remarks>Legacy values remain source-compatible while sharing the canonical telemetry categories.</remarks>
    /// <example><code>var label = TelegramUpdateLatencyScope.StageName(TelegramUpdateStage.XuiMutation);</code></example>
    public static string StageName(TelegramUpdateStage stage) => stage switch
    {
        TelegramUpdateStage.XuiRead => "xui_read",
        TelegramUpdateStage.XuiMutation => "xui_write",
        TelegramUpdateStage.TelegramSend => "telegram_send",
        TelegramUpdateStage.TelegramEdit => "telegram_edit",
        TelegramUpdateStage.TelegramMembership => "telegram_membership",
        TelegramUpdateStage.TelegramProbe => "telegram_probe",
        TelegramUpdateStage.TelegramCallbackAck => "telegram_callback_ack",
        TelegramUpdateStage.TelegramPolling => "telegram_polling",
        TelegramUpdateStage.SiteLookup => "site_lookup",
        TelegramUpdateStage.ProviderRead => "payment_gateway",
        TelegramUpdateStage.DatabaseWait or TelegramUpdateStage.SqliteRead => "sqlite_read",
        TelegramUpdateStage.SqliteWrite => "sqlite_write",
        TelegramUpdateStage.SqliteBusyRetry => "sqlite_busy_retry",
        TelegramUpdateStage.ExternalHttp => "external_http",
        TelegramUpdateStage.LockWait => "lock_wait",
        TelegramUpdateStage.BusinessRecovery or TelegramUpdateStage.BusinessProcessing => "business_processing",
        _ => "unattributed"
    };

    /// <summary>Registers one unique stage owner and closes any preceding uninstrumented gap.</summary>
    /// <param name="stage">Closed stage category for one awaited operation.</param>
    /// <returns>An idempotent timer, or a no-op if disposed or bounded metadata capacity is exhausted.</returns>
    /// <remarks>Only diagnostic metadata is locked. The newest active timer owns each exclusive interval.</remarks>
    private StageTimer EnterStage(TelegramUpdateStage stage)
    {
        lock (_stageGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !Enum.IsDefined(stage)) return default;
            var now = _timeProvider.GetTimestamp();
            AccountUntil(now);
            var slot = 0;
            while (slot <= _stages.Length && GetStageSlot(slot).Id != 0) slot++;
            if (slot == MaximumActiveSlots)
            {
                _metadataCapacityExceeded = true;
                return default;
            }
            if (slot > _stages.Length)
                Array.Resize(ref _stages, Math.Min(MaximumActiveSlots - 1, Math.Max(1, _stages.Length * 2)));
            if (_currentStage == null) CloseGap(now, StageName(stage));
            var id = ++_nextStageId;
            GetStageSlot(slot) = new StageState { Id = id, Stage = stage, StartedTimestamp = now };
            _currentStage = stage;
            return new StageTimer(this, slot, id);
        }
    }

    /// <summary>Completes exactly one owned stage slot and restores the newest remaining active owner.</summary>
    /// <param name="slot">Reusable metadata slot assigned at stage start.</param>
    /// <param name="id">Unique ownership id; duplicate/copy disposal is ignored.</param>
    /// <remarks>Inclusive diagnostics are separate from exclusive wall-time accounting.</remarks>
    private void ExitStage(int slot, long id)
    {
        try
        {
            TelegramUpdateStage stage;
            double elapsed;
            lock (_stageGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || slot > _stages.Length || GetStageSlot(slot).Id != id) return;
                var now = _timeProvider.GetTimestamp();
                AccountUntil(now);
                var state = GetStageSlot(slot);
                stage = state.Stage;
                elapsed = _timeProvider.GetElapsedTime(state.StartedTimestamp, now).TotalMilliseconds;
                _inclusiveStageMs[(int)stage] += elapsed;
                RemoveStage(slot, now);
            }
            if (_telemetry?.Enabled == true)
                Record(CreateEvent("latency_stage_completed") with
                {
                    Stage = StageName(stage), DurationMs = elapsed, Outcome = "completed", TimingQuality = "inclusive_wall_time"
                });
            Report(stage, elapsed);
        }
        catch
        {
            // A failed diagnostic clock must not leave a completed operation published as the active stage.
            lock (_stageGate)
                if (slot <= _stages.Length && GetStageSlot(slot).Id == id) RemoveStage(slot, _accountedTimestamp);
        }
    }

    /// <summary>Releases stage ownership and publishes only the newest still-active stage.</summary>
    /// <param name="slot">Owned metadata slot being released while the stage lock is held.</param>
    /// <param name="now">Last reliable monotonic timestamp, used to anchor a newly open unattributed gap.</param>
    /// <remarks>Used on normal completion and clock-failure cleanup; it does not call clocks, observers, or business code.</remarks>
    private void RemoveStage(int slot, long now)
    {
        _lastStage = StageName(GetStageSlot(slot).Stage);
        GetStageSlot(slot) = default;
        _currentStage = null;
        long newest = 0;
        for (var index = 0; index <= _stages.Length; index++)
        {
            ref readonly var active = ref GetStageSlot(index);
            if (active.Id > newest) { newest = active.Id; _currentStage = active.Stage; }
        }
        if (_currentStage == null) { _gapStartedTimestamp = now; _gapBeforeStage = _lastStage; }
    }

    /// <summary>Assigns the elapsed interval to exactly one active stage or the unattributed bucket.</summary>
    /// <param name="now">Monotonic timestamp; the caller holds the stage metadata lock.</param>
    /// <remarks>No intervals are stored: completed durations and the longest gap use constant aggregate storage.</remarks>
    private void AccountUntil(long now)
    {
        var elapsed = _timeProvider.GetElapsedTime(_accountedTimestamp, now).TotalMilliseconds;
        if (_currentStage is { } stage) _exclusiveStageMs[(int)stage] += elapsed;
        else _unattributedMs += elapsed;
        _accountedTimestamp = now;
    }

    /// <summary>Finalizes an unattributed interval with safe preceding/following stage labels.</summary>
    /// <param name="now">Monotonic gap end timestamp.</param>
    /// <param name="afterStage">Closed following stage or handler_end label.</param>
    /// <remarks>Reports slow gaps as measured wall time, never CPU time, sleep time, or an inferred cause.</remarks>
    private void CloseGap(long now, string afterStage)
    {
        var elapsed = _timeProvider.GetElapsedTime(_gapStartedTimestamp, now).TotalMilliseconds;
        var start = _timeProvider.GetElapsedTime(_startedTimestamp, _gapStartedTimestamp).TotalMilliseconds;
        var end = _timeProvider.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds;
        if (elapsed > _maxGapMs)
        {
            _maxGapMs = elapsed; _maxGapStartedMs = start; _maxGapEndedMs = end;
            _maxGapBeforeStage = _gapBeforeStage; _maxGapAfterStage = afterStage;
        }
        if (_telemetry?.Enabled == true && elapsed >= StageThreshold.TotalMilliseconds)
            Record(CreateEvent("unattributed_handler_time") with
            {
                Stage = "unattributed", DurationMs = elapsed, MaxUnattributedGapMs = elapsed,
                MaxGapStartedMs = start, MaxGapEndedMs = end, GapBeforeStage = _gapBeforeStage,
                GapAfterStage = afterStage, TimingQuality = "measured_wall_time_not_cpu", Outcome = "completed"
            });
    }

    /// <summary>Addresses reusable stage metadata without copying or retaining payload objects.</summary>
    /// <param name="slot">Zero selects the inline slot; positive values select the bounded array.</param>
    /// <returns>The slot reference, usable only while holding the stage metadata lock.</returns>
    private ref StageState GetStageSlot(int slot)
    {
        if (slot == 0) return ref _primaryStage;
        return ref _stages[slot - 1];
    }

    /// <summary>Bounded active ownership metadata for a stage; never retains an operation or customer value.</summary>
    private struct StageState
    {
        /// <summary>Unique ownership; zero means the slot is free.</summary>
        internal long Id;
        /// <summary>Closed execution category.</summary>
        internal TelegramUpdateStage Stage;
        /// <summary>Monotonic start for inclusive timing.</summary>
        internal long StartedTimestamp;
    }

    /// <summary>Gets the elapsed time since this scope was created.</summary>
    /// <returns>The monotonic elapsed time of the whole update execution, frozen when the scope is disposed.</returns>
    /// <remarks>Uses only the diagnostic clock; budgets and scheduling remain unchanged.</remarks>
    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startedTimestamp, _endedTimestamp ?? _timeProvider.GetTimestamp());

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
        if (LatencyTelemetrySuppression.IsActive) return default;
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
                var id = ++_requestCount;
                if (slot == MaximumActiveSlots) { _metadataCapacityExceeded = true; return default; }
                if (slot > _telegramRequests.Length)
                    Array.Resize(ref _telegramRequests, Math.Min(MaximumActiveSlots - 1, Math.Max(1, _telegramRequests.Length * 2)));

                GetTelegramRequestSlot(slot) = new TelegramRequestState
                {
                    Id = id,
                    Kind = kind,
                    StartedTimestamp = started
                };
                _activeTelegramRequestCount++;
                var offset = _timeProvider.GetElapsedTime(_startedTimestamp, started).TotalMilliseconds;
                if (IsVisibleResponse(kind) && _firstResponseId == 0)
                {
                    _firstResponseId = id;
                    _firstResponseAttemptMs = offset;
                    if (_telemetry?.Enabled == true)
                        Record(CreateEvent("telegram_update_first_response_attempt") with
                        {
                            TimestampUtc = AtOffset(offset), FirstResponseAttemptAtUtc = AtOffset(offset),
                            FirstResponseAttemptMs = offset, Category = RequestCategory(kind), Outcome = "attempted"
                        });
                }
                if (kind == TelegramForegroundRequestKind.CallbackAcknowledgement && _firstCallbackId == 0)
                {
                    _firstCallbackId = id;
                    _callbackAttemptMs = offset;
                }
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
                if (Volatile.Read(ref _disposed) != 0) return;
                var offset = _timeProvider.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds;
                if (outcome == TelegramForegroundRequestOutcome.ForegroundBudgetExpired || IsCallbackPolicyCancellation(outcome))
                    _foregroundTimeoutCount++;
                if (id == _firstResponseId) _firstResponseCompletedMs = offset;
                var firstAcknowledged = IsVisibleResponse(state.Kind) && outcome == TelegramForegroundRequestOutcome.Completed
                    && _firstResponseAcknowledgedMs == null;
                if (firstAcknowledged) _firstResponseAcknowledgedMs = offset;
                if (id == _firstCallbackId) _callbackDurationMs = elapsed;
                if (state.Kind == TelegramForegroundRequestKind.CallbackAcknowledgement && outcome == TelegramForegroundRequestOutcome.Completed && _callbackAcknowledgedMs == null)
                    _callbackAcknowledgedMs = offset;
                observation = new TelegramForegroundRequestObservation(
                    state.Kind, outcome, elapsed, offset, apiErrorCode, _requestCount, GetTotalTelegramElapsedMs(now));
                if (_telemetry?.Enabled == true && id == _firstResponseId)
                    Record(CreateEvent("telegram_update_first_response_completed") with
                    {
                        TimestampUtc = AtOffset(offset), FirstResponseCompletedAtUtc = AtOffset(offset),
                        FirstResponseAttemptMs = _firstResponseAttemptMs, FirstResponseCompletedMs = offset,
                        FirstResponseAcknowledgedMs = _firstResponseAcknowledgedMs, ApiErrorCode = apiErrorCode,
                        Category = RequestCategory(state.Kind), Outcome = RequestOutcome(outcome)
                    });
                if (_telemetry?.Enabled == true && firstAcknowledged)
                    Record(CreateEvent("telegram_update_first_response_acknowledged") with
                    {
                        TimestampUtc = AtOffset(offset), FirstResponseAttemptMs = _firstResponseAttemptMs,
                        FirstResponseCompletedMs = _firstResponseCompletedMs, FirstResponseAcknowledgedMs = offset,
                        Category = RequestCategory(state.Kind), Outcome = "completed"
                    });
            }
            if (Volatile.Read(ref _disposed) == 0)
            {
                if (_telemetry?.Enabled == true)
                    Record(CreateEvent("telegram_foreground_request_completed") with
                    {
                        Stage = observation.Kind == TelegramForegroundRequestKind.CallbackAcknowledgement
                            ? "telegram_callback_ack" : observation.Kind == TelegramForegroundRequestKind.MessageEdit
                                ? "telegram_edit" : IsVisibleResponse(observation.Kind) ? "telegram_send" : "telegram_membership",
                        Category = RequestCategory(observation.Kind),
                        Outcome = IsCallbackPolicyCancellation(outcome) ? "callback_policy_timeout" : RequestOutcome(outcome),
                        DurationMs = observation.RequestElapsedMs, ApiErrorCode = apiErrorCode,
                        TelegramRequestCount = observation.RequestCount, TotalTelegramMs = observation.TotalTelegramElapsedMs,
                        FailureClassification = IsCallbackPolicyCancellation(outcome)
                            ? "callback_policy_timeout" : outcome == TelegramForegroundRequestOutcome.ForegroundBudgetExpired
                                ? "foreground_budget_timeout" : outcome == TelegramForegroundRequestOutcome.TransportError
                                    ? "transport_failure" : outcome == TelegramForegroundRequestOutcome.TelegramApiError ? "telegram_api_error" : null,
                        CancellationSource = TelegramRequestCancellationScope.Current?.CancellationSource,
                        TimeoutCategory = TelegramRequestCancellationScope.Current?.TimeoutCategory
                    });
                _onTelegramRequest?.Invoke(observation);
            }
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


    /// <summary>Adds one actually executed SQLite contention backoff to the update summary.</summary>
    /// <param name="elapsedMs">Measured retry-delay milliseconds, including partial waits cancelled by the caller.</param>
    /// <remarks>Called only by the existing retry helper; no retries or delays are introduced by measurement.</remarks>
    internal void RecordSqliteBusyRetry(double elapsedMs)
    {
        try { lock (_stageGate) { _sqliteBusyRetryCount++; _sqliteBusyWaitMs += elapsedMs; } }
        catch { /* Diagnostics must not replace persistence failures. */ }
    }
    /// <summary>Captures bounded update-local wall-time, gap, and request metadata without retaining payloads.</summary>
    /// <returns>A detached snapshot; exclusive stages plus unattributed time partition HandlerMs, inclusive stages do not.</returns>
    /// <remarks>
    /// Safe after disposal: handler accounting is frozen at its endpoint. Active snapshots include open stage/gap time.
    /// Capacity overflow is explicit in TimingQuality. Gaps are measured waits or work with an unknown cause, not CPU.
    /// </remarks>
    /// <example><code>var summary = scope.CaptureTelemetry() with { EventType = "telegram_update_handler_completed" };</code></example>
    public LatencyTelemetryEvent CaptureTelemetry()
    {
        lock (_stageGate)
        {
            var now = _endedTimestamp ?? _timeProvider.GetTimestamp();
            if (_endedTimestamp == null) AccountUntil(now);
            var exclusive = new Dictionary<string, double>(StringComparer.Ordinal);
            var inclusive = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var index = 0; index < _exclusiveStageMs.Length; index++)
            {
                var name = StageName((TelegramUpdateStage)index);
                if (_exclusiveStageMs[index] > 0)
                    exclusive[name] = exclusive.GetValueOrDefault(name) + _exclusiveStageMs[index];
                if (_inclusiveStageMs[index] > 0)
                    inclusive[name] = inclusive.GetValueOrDefault(name) + _inclusiveStageMs[index];
            }
            if (_endedTimestamp == null)
                for (var index = 0; index <= _stages.Length; index++)
                {
                    ref readonly var active = ref GetStageSlot(index);
                    if (active.Id == 0) continue;
                    var name = StageName(active.Stage);
                    inclusive[name] = inclusive.GetValueOrDefault(name)
                        + _timeProvider.GetElapsedTime(active.StartedTimestamp, now).TotalMilliseconds;
                }
            var gapMs = _maxGapMs; var gapStart = _maxGapStartedMs; var gapEnd = _maxGapEndedMs;
            var gapBefore = _maxGapBeforeStage; var gapAfter = _maxGapAfterStage;
            if (_endedTimestamp == null && _currentStage == null)
            {
                var openGap = _timeProvider.GetElapsedTime(_gapStartedTimestamp, now).TotalMilliseconds;
                if (openGap > gapMs)
                {
                    gapMs = openGap; gapStart = _timeProvider.GetElapsedTime(_startedTimestamp, _gapStartedTimestamp).TotalMilliseconds;
                    gapEnd = _timeProvider.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds;
                    gapBefore = _gapBeforeStage; gapAfter = "in_progress";
                }
            }
            string slowest = "unattributed"; double slowestMs = _unattributedMs;
            foreach (var pair in exclusive)
                if (pair.Value > slowestMs) { slowest = pair.Key; slowestMs = pair.Value; }
            lock (_telegramRequestGate)
                return CreateEvent("telegram_update_handler_completed") with
                {
                    HandlerStartedAtUtc = _handlerStartedUtc,
                    HandlerCompletedAtUtc = _endedTimestamp == null ? null : AtOffset(Elapsed.TotalMilliseconds),
                    HandlerMs = _timeProvider.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds,
                    StageMs = exclusive, InclusiveStageMs = inclusive, UnattributedHandlerMs = _unattributedMs,
                    MaxUnattributedGapMs = gapMs, MaxGapStartedMs = gapStart, MaxGapEndedMs = gapEnd,
                    GapBeforeStage = gapBefore, GapAfterStage = gapAfter, SlowestStage = slowest,
                    FirstResponseAttemptMs = _firstResponseAttemptMs, FirstResponseCompletedMs = _firstResponseCompletedMs,
                    FirstResponseAcknowledgedMs = _firstResponseAcknowledgedMs,
                    FirstResponseAttemptAtUtc = _firstResponseAttemptMs is { } attempted ? AtOffset(attempted) : null,
                    FirstResponseCompletedAtUtc = _firstResponseCompletedMs is { } completed ? AtOffset(completed) : null,
                    CallbackAckAttemptMs = _callbackAttemptMs, CallbackAckMs = _callbackDurationMs,
                    CallbackAckAcknowledgedMs = _callbackAcknowledgedMs, TelegramRequestCount = _requestCount,
                    BusyRetryCount = _sqliteBusyRetryCount, BusyWaitMs = _sqliteBusyWaitMs,
                    TotalTelegramMs = _handlerTelegramTotalMs ?? GetTotalTelegramElapsedMs(now), ForegroundTimeoutCount = _foregroundTimeoutCount,
                    TimingQuality = _metadataCapacityExceeded ? "metadata_capacity_exceeded" : "measured_wall_time_not_cpu"
                };
        }
    }

    /// <summary>Builds the common metadata envelope for one update observation.</summary>
    /// <param name="eventType">Compile-time event family selected only by instrumentation code.</param>
    /// <returns>A metadata-only record with opaque update identity, never customer identity or content.</returns>
    private LatencyTelemetryEvent CreateEvent(string eventType) => new()
    {
        EventType = eventType, TraceId = TraceId, BotId = BotId, UpdateId = UpdateId, Sequence = Sequence > 0 ? Sequence : null
    };

    /// <summary>Enqueues a diagnostic observation without allowing any writer failure to affect business work.</summary>
    /// <param name="observation">Detached payload-free observation assembled from controlled fields.</param>
    private void Record(LatencyTelemetryEvent observation)
    {
        try { if (!LatencyTelemetrySuppression.IsActive && _telemetry?.Enabled == true) _telemetry.TryRecord(observation); }
        catch { /* Telemetry remains best effort even during shutdown. */ }
    }

    /// <summary>Converts a monotonic handler-relative offset to an anchored UTC milestone.</summary>
    /// <param name="milliseconds">Measured nonnegative handler-relative milliseconds.</param>
    /// <returns>UTC milestone without consulting a potentially adjusted wall clock.</returns>
    private DateTime AtOffset(double milliseconds) => _handlerStartedUtc.AddMilliseconds(milliseconds);

    /// <summary>Determines whether one explicitly mapped SDK request can be a visible response.</summary>
    /// <param name="kind">Closed foreground request kind.</param>
    /// <returns>True only for sends and edits; callbacks, deletes, and lookups are excluded.</returns>
    private static bool IsVisibleResponse(TelegramForegroundRequestKind kind) => kind is
        TelegramForegroundRequestKind.TextSend or TelegramForegroundRequestKind.MessageEdit
        or TelegramForegroundRequestKind.PhotoSend or TelegramForegroundRequestKind.DocumentUpload
        or TelegramForegroundRequestKind.MediaGroup;

    /// <summary>Maps request kinds to a bounded safe category without inspecting SDK objects.</summary>
    /// <param name="kind">Compile-time foreground request classification.</param>
    /// <returns>A safe snake-case category, or unknown for an unsupported enum value.</returns>
    private static string RequestCategory(TelegramForegroundRequestKind kind) => kind switch
    {
        TelegramForegroundRequestKind.TextSend => "text_send",
        TelegramForegroundRequestKind.MessageEdit => "message_edit",
        TelegramForegroundRequestKind.CallbackAcknowledgement => "callback_acknowledgement",
        TelegramForegroundRequestKind.DocumentUpload => "document_upload",
        TelegramForegroundRequestKind.MediaGroup => "media_group",
        TelegramForegroundRequestKind.PhotoSend => "photo_send",
        TelegramForegroundRequestKind.DeleteMessage => "delete_message",
        TelegramForegroundRequestKind.ChatLookup => "chat_lookup",
        TelegramForegroundRequestKind.MembershipLookup => "membership_lookup",
        _ => "unknown"
    };

    /// <summary>Distinguishes an actual best-effort callback cancellation from a successful result or API rejection.</summary>
    /// <param name="outcome">Actual closed result of the request, not inferred from token state alone.</param>
    /// <returns>True only for cancellation outcomes while the original callback policy deadline owns cancellation.</returns>
    /// <remarks>A deadline token can race with a successful response; success must never be relabeled as timeout.</remarks>
    private static bool IsCallbackPolicyCancellation(TelegramForegroundRequestOutcome outcome)
        => (outcome is TelegramForegroundRequestOutcome.CallerCancellation or TelegramForegroundRequestOutcome.ForegroundBudgetExpired)
            && TelegramRequestCancellationScope.Current?.CancellationSource == "callback_policy";

    /// <summary>Maps completion outcomes to a bounded safe category without exception text.</summary>
    /// <param name="outcome">Actual terminal result of the awaited request.</param>
    /// <returns>The diagnostic outcome; no message, stack trace, or provider payload is retained.</returns>
    private static string RequestOutcome(TelegramForegroundRequestOutcome outcome) => outcome switch
    {
        TelegramForegroundRequestOutcome.Completed => "completed",
        TelegramForegroundRequestOutcome.ForegroundBudgetExpired => "foreground_budget_expired",
        TelegramForegroundRequestOutcome.TelegramApiError => "telegram_api_error",
        TelegramForegroundRequestOutcome.CallerCancellation => "caller_cancellation",
        TelegramForegroundRequestOutcome.TransportError => "transport_error",
        TelegramForegroundRequestOutcome.UnexpectedError => "unexpected_error",
        _ => "in_progress"
    };

    /// <summary>Freezes handler stage/gap accounting and restores the enclosing asynchronous scope.</summary>
    /// <remarks>Idempotent; later snapshots use this endpoint, not post-handler persistence time.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            lock (_stageGate)
            {
                var now = _timeProvider.GetTimestamp();
                AccountUntil(now);
                if (_currentStage == null) CloseGap(now, "handler_end");
                for (var index = 0; index <= _stages.Length; index++)
                {
                    ref var active = ref GetStageSlot(index);
                    if (active.Id == 0) continue;
                    _inclusiveStageMs[(int)active.Stage] += _timeProvider.GetElapsedTime(active.StartedTimestamp, now).TotalMilliseconds;
                    active = default;
                }
                _endedTimestamp = now;
                _currentStage = null;
                lock (_telegramRequestGate) _handlerTelegramTotalMs = GetTotalTelegramElapsedMs(now);
            }
        }
        catch { /* A failing diagnostic clock cannot prevent ambient cleanup. */ }
        finally { Ambient.Value = _previous; }
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

        /// <summary>Reusable stage metadata slot.</summary>
        private readonly int _slot;
        /// <summary>Unique stage ownership protecting copied timers from duplicate completion.</summary>
        private readonly long _id;

        /// <summary>Creates a nonallocating completion handle for registered stage metadata.</summary>
        /// <param name="scope">Owning update-local stage registry.</param>
        /// <param name="slot">Reusable stage slot assigned by the registry.</param>
        /// <param name="id">Unique ownership id assigned at stage start.</param>
        /// <remarks>Only the registry creates non-default handles; copies remain safe to dispose.</remarks>
        internal StageTimer(TelegramUpdateLatencyScope scope, int slot, long id)
        {
            _scope = scope;
            _slot = slot;
            _id = id;
        }

        /// <summary>Completes this stage once and restores only another still-active stage.</summary>
        /// <remarks>Default, copied, late, and repeatedly disposed handles cannot change business results.</remarks>
        public void Dispose() => _scope?.ExitStage(_slot, _id);
    }
}
