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
/// so the instrumentation is negligible next to a network call. Only stages that exceed the configured threshold are
/// reported, and the scope deliberately does not persist or log payloads.
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
    private long _startedTimestamp;

    /// <summary>Idempotency flag preventing double disposal from restoring the ambient scope twice.</summary>
    private int _disposed;

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

        /// <summary>Creates a measurement timer for one stage of one scope.</summary>
        /// <param name="scope">Owning scope; required.</param>
        /// <param name="stage">Closed-vocabulary stage being measured.</param>
        internal StageTimer(TelegramUpdateLatencyScope scope, TelegramUpdateStage stage)
        {
            _scope = scope;
            _stage = stage;
            _startedTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Completes the measurement and reports it to the owning scope when it exceeded the stage threshold.
        /// </summary>
        /// <remarks>A timer created by a null scope does nothing, so shared code can always dispose it safely.</remarks>
        public void Dispose()
        {
            if (_scope == null)
                return;

            _scope.Report(_stage, Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds);
        }
    }
}
