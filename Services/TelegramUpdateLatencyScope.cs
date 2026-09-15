using System;
using System.Diagnostics;
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
    /// <summary>Local durable output admission, excluding subsequent Telegram delivery and queue wait.</summary>
    TelegramEnqueue,
    /// <summary>Total background business execution; nested database/XUI/output stages explain its components.</summary>
    BusinessLogic,
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
    BusinessRecovery
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
/// Cost:
/// Creating the scope is one object allocation per update and taking a stage measurement is a monotonic timestamp pair,
/// so the instrumentation is small next to a network call. All stages contribute cumulative timing; only stages
/// exceeding the configured threshold raise individual warnings. The scope never records payloads.
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TelegramUpdateStage, double> _totals = new();

    /// <summary>Reads cumulative elapsed time for a stage, including fast calls below warning thresholds.</summary>
    /// <param name="stage">Closed-vocabulary stage to inspect.</param>
    /// <returns>Elapsed milliseconds, or zero when no such stage ran. Nested stages may overlap.</returns>
    public double TotalMilliseconds(TelegramUpdateStage stage) => _totals.GetValueOrDefault(stage);
    /// <summary>Holds the scope for the current asynchronous update execution context.</summary>
    private static readonly AsyncLocal<TelegramUpdateLatencyScope> Ambient = new();

    /// <summary>Enclosing scope restored after this scope finishes.</summary>
    private readonly TelegramUpdateLatencyScope _previous;

    /// <summary>Monotonic scope creation timestamp used for whole-handler attribution.</summary>
    private long _startedTimestamp;

    /// <summary>Idempotency flag preventing double disposal from restoring the ambient scope twice.</summary>
    private int _disposed;

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
    private TelegramUpdateLatencyScope(
        long sequence,
        string botId,
        long updateId,
        TimeSpan stageThreshold,
        Action<TelegramUpdateStage, double> onSlowStage)
    {
        Sequence = sequence;
        BotId = botId;
        UpdateId = updateId;
        StageThreshold = stageThreshold;
        OnSlowStage = onSlowStage;
        _startedTimestamp = Stopwatch.GetTimestamp();
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
    /// <returns>A disposable scope that restores the previous ambient scope when disposed.</returns>
    /// <remarks>
    /// The scheduler is the only production caller. Disposing the returned scope while its stage timers are still
    /// being disposed is harmless: a completed timer only reports, and reporting after disposal is a no-op that the
    /// scheduler's recorder tolerates.
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
        Action<TelegramUpdateStage, double> onSlowStage)
    {
        var scope = new TelegramUpdateLatencyScope(sequence, botId, updateId, stageThreshold, onSlowStage);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Starts a stage measurement that reports itself when disposed.
    /// </summary>
    /// <param name="stage">Closed-vocabulary stage being measured.</param>
    /// <returns>
    /// A struct timer that must be disposed around the measured call. Disposing reports the stage only when its elapsed
    /// time exceeds <see cref="StageThreshold"/>. A default instance (no ambient scope) reports nothing.
    /// </returns>
    /// <remarks>
    /// Callers use <c>using</c> around the awaited operation so the measurement also completes on an exception path,
    /// including a foreground budget expiry.
    /// </remarks>
    public StageTimer Measure(TelegramUpdateStage stage) => new(this, stage);

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
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedTimestamp);

    /// <summary>Reports one completed stage measurement against this scope.</summary>
    /// <param name="stage">Closed-vocabulary stage that completed.</param>
    /// <param name="elapsedMilliseconds">Monotonic elapsed milliseconds for the stage.</param>
    /// <remarks>
    /// The recorder is invoked only above the configured threshold, and a recorder failure is swallowed so
    /// instrumentation can never fail a customer operation.
    /// </remarks>
    private void Report(TelegramUpdateStage stage, double elapsedMilliseconds)
    {
        _totals.AddOrUpdate(stage, elapsedMilliseconds, (_, total) => total + elapsedMilliseconds);
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
            _startedTimestamp = Stopwatch.GetTimestamp();
            _previousStage = scope.EnterStage(stage);
        }

        /// <summary>
        /// Completes the measurement, reports it to the owning scope when it exceeded the stage threshold, and restores
        /// the previously current stage.
        /// </summary>
        /// <remarks>A timer created by a null scope does nothing, so shared code can always dispose it safely.</remarks>
        public void Dispose()
        {
            if (_scope == null)
                return;

            _scope.Report(_stage, Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds);
            _scope.ExitStage(_stage, _previousStage);
        }
    }
}
