using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// Measures one user-visible XUI activity and accumulates the wall-clock time spent inside panel API calls.
/// </summary>
/// <remarks>
/// Each Telegram operation creates one scope after its final validation and before its first execution step.
/// <see cref="ApiServicev3"/> records every logical HTTP request, including retries and retry delays, against the
/// ambient scope. Database, wallet, website, Telegram, and intentional bulk-create delays affect
/// <see cref="TotalElapsed"/> but do not affect <see cref="PanelApiElapsed"/>.
///
/// The timer is monotonic and therefore is not affected by wall-clock or timezone changes. The ambient value is
/// carried by <see cref="AsyncLocal{T}"/>, so simultaneous updates handled by different asynchronous execution
/// contexts do not share measurements.
/// </remarks>
public sealed class XuiOperationTiming : IDisposable
{
    /// <summary>
    /// Holds the operation timer for the current asynchronous update execution context.
    /// </summary>
    private static readonly AsyncLocal<XuiOperationTiming> AmbientTiming = new();

    /// <summary>
    /// Enclosing ambient timer restored after this scope finishes.
    /// </summary>
    private readonly XuiOperationTiming _previous;

    /// <summary>
    /// Monotonic timestamp captured when actual operation execution begins.
    /// </summary>
    private readonly long _startedTimestamp;

    /// <summary>
    /// Monotonic completion timestamp, or zero while the scope is active.
    /// </summary>
    private long _completedTimestamp;

    /// <summary>
    /// Atomic sum of monotonic ticks spent in logical panel API calls.
    /// </summary>
    private long _panelApiStopwatchTicks;

    /// <summary>
    /// Idempotency flag preventing a timing scope from being completed twice.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Initializes one monotonic operation timer and remembers the enclosing ambient scope.
    /// </summary>
    /// <param name="previous">
    /// The timing scope previously active in the current asynchronous execution context, or <c>null</c> when this
    /// operation is the outermost scope. The value is restored when this instance is disposed.
    /// </param>
    private XuiOperationTiming(XuiOperationTiming previous)
    {
        _previous = previous;
        _startedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Gets the total monotonic elapsed time since this operation scope was created.
    /// </summary>
    /// <remarks>
    /// The value includes panel calls plus local persistence, wallet settlement, Telegram delivery, website sync,
    /// and intentional delays performed before the final audit log. It freezes when the scope is disposed.
    /// </remarks>
    public TimeSpan TotalElapsed => Stopwatch.GetElapsedTime(
        _startedTimestamp,
        Volatile.Read(ref _completedTimestamp) is var completed && completed > 0
            ? completed
            : Stopwatch.GetTimestamp());

    /// <summary>
    /// Gets the accumulated elapsed time of logical XUI panel calls made inside this operation.
    /// </summary>
    /// <remarks>
    /// For XUI v3, one logical call includes all retry attempts and retry backoff. Multiple sequential calls are
    /// summed. It excludes Telegram, database, wallet, and website requests.
    /// </remarks>
    public TimeSpan PanelApiElapsed => StopwatchTicksToTimeSpan(Interlocked.Read(ref _panelApiStopwatchTicks));

    /// <summary>
    /// Starts a new ambient timing scope for one XUI create, renew, delete, or account-edit activity.
    /// </summary>
    /// <returns>
    /// A new timing scope. The caller must dispose it after emitting the final success or failure audit so the
    /// previous ambient scope is restored.
    /// </returns>
    /// <remarks>
    /// Start the scope only after input, authorization, plan, and payment eligibility checks have passed. Waiting for
    /// a customer to pay or confirm must never be included in the operation duration.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var timing = XuiOperationTiming.Start();
    /// var result = await ApiServicev3.UpdateClientAsync(server, configuration, email, payload, cancellationToken);
    /// logger.LogTelegramHtml(XuiOperationTiming.BuildHtmlLines(timing.Snapshot()));
    /// </code>
    /// </example>
    public static XuiOperationTiming Start()
    {
        var timing = new XuiOperationTiming(AmbientTiming.Value);
        AmbientTiming.Value = timing;
        return timing;
    }

    /// <summary>
    /// Captures the panel and total durations at one consistent point for a final audit message.
    /// </summary>
    /// <returns>An immutable duration snapshot safe to pass to log builders.</returns>
    public XuiOperationTimingSnapshot Snapshot()
    {
        return new XuiOperationTimingSnapshot(PanelApiElapsed, TotalElapsed);
    }

    /// <summary>
    /// Measures one legacy XUI panel operation that does not use the shared XUI v3 HTTP transport.
    /// </summary>
    /// <typeparam name="T">Return type produced by the legacy panel operation.</typeparam>
    /// <param name="operation">
    /// Required asynchronous legacy panel operation. It may include legacy login and read-back calls and is measured
    /// as one logical panel interaction.
    /// </param>
    /// <returns>The value returned by <paramref name="operation"/>.</returns>
    /// <remarks>
    /// XUI v3 callers must not use this wrapper because <see cref="ApiServicev3"/> records its own logical HTTP calls;
    /// wrapping those calls here would double-count API time.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="operation"/> is null.</exception>
    /// <example>
    /// <code>
    /// var succeeded = await timing.MeasureLegacyPanelCallAsync(() => CreateAccount(accountDto));
    /// </code>
    /// </example>
    public async Task<T> MeasureLegacyPanelCallAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var measurement = BeginPanelCall(this);
        return await operation();
    }

    /// <summary>
    /// Formats a measured duration as unbounded minutes, seconds, and milliseconds.
    /// </summary>
    /// <param name="duration">Non-negative elapsed time produced by a monotonic timer.</param>
    /// <returns>
    /// A culture-invariant value such as <c>00:03.217</c>. Durations longer than one hour retain total minutes, for
    /// example <c>75:04.009</c>, so the format always remains <c>Minute:Second.millisecond</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// var text = XuiOperationTiming.Format(TimeSpan.FromMilliseconds(3217)); // 00:03.217
    /// </code>
    /// </example>
    public static string Format(TimeSpan duration)
    {
        var totalMilliseconds = Math.Max(0L, (long)Math.Floor(duration.TotalMilliseconds));
        var minutes = totalMilliseconds / 60000L;
        var seconds = totalMilliseconds / 1000L % 60L;
        var milliseconds = totalMilliseconds % 1000L;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{seconds:00}.{milliseconds:000}");
    }

    /// <summary>
    /// Builds the two HTML-safe duration lines appended to central Telegram XUI audit messages.
    /// </summary>
    /// <param name="snapshot">Immutable operation timing captured immediately before logging.</param>
    /// <returns>
    /// Two Persian HTML lines containing accumulated panel API time and total operation time. The returned text
    /// contains only numeric timer output and supported Telegram <c>code</c> tags.
    /// </returns>
    /// <example>
    /// <code>
    /// builder.Append(XuiOperationTiming.BuildHtmlLines(timing.Snapshot()));
    /// </code>
    /// </example>
    public static string BuildHtmlLines(XuiOperationTimingSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"زمان API پنل: <code>{Format(snapshot.PanelApiElapsed)}</code>");
        builder.Append($"زمان کل عملیات: <code>{Format(snapshot.TotalElapsed)}</code>");
        return builder.ToString();
    }

    /// <summary>
    /// Starts one logical panel-call measurement for the current ambient XUI operation.
    /// </summary>
    /// <returns>
    /// A disposable measurement that adds its elapsed monotonic ticks to the current operation, or a no-op disposable
    /// when the caller is not running inside an instrumented activity.
    /// </returns>
    /// <remarks>
    /// This member is used by the shared XUI v3 HTTP transport. It intentionally does not expose the current scope so
    /// API code cannot mutate total-operation lifecycle state.
    /// </remarks>
    internal static IDisposable BeginCurrentPanelCall()
    {
        return BeginPanelCall(AmbientTiming.Value);
    }

    /// <summary>
    /// Stops the monotonic timer and restores the ambient timing scope that was active before this operation.
    /// </summary>
    /// <remarks>Disposal is idempotent and never changes application, wallet, order, or panel state.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _completedTimestamp, Stopwatch.GetTimestamp());
        if (ReferenceEquals(AmbientTiming.Value, this))
            AmbientTiming.Value = _previous;
    }

    /// <summary>
    /// Creates a logical panel-call measurement bound to the supplied operation scope.
    /// </summary>
    /// <param name="timing">
    /// Ambient operation timer that receives the measured stopwatch ticks, or <c>null</c> when instrumentation is
    /// intentionally inactive for the current API call.
    /// </param>
    /// <returns>An idempotent measurement disposable, or a shared no-op disposable when no scope is active.</returns>
    private static IDisposable BeginPanelCall(XuiOperationTiming timing)
    {
        return timing == null
            ? NoopDisposable.Instance
            : new PanelCallMeasurement(timing);
    }

    /// <summary>
    /// Converts raw monotonic stopwatch ticks into a non-negative <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="stopwatchTicks">Accumulated ticks produced by <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <returns>The corresponding elapsed duration, or zero when the supplied tick count is not positive.</returns>
    private static TimeSpan StopwatchTicksToTimeSpan(long stopwatchTicks)
    {
        if (stopwatchTicks <= 0)
            return TimeSpan.Zero;

        var timeSpanTicks = stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        return TimeSpan.FromTicks((long)Math.Round(timeSpanTicks, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Adds one logical panel request duration to its owning operation exactly once.
    /// </summary>
    private sealed class PanelCallMeasurement : IDisposable
    {
        /// <summary>
        /// Operation timer that receives this call's elapsed ticks.
        /// </summary>
        private readonly XuiOperationTiming _owner;

        /// <summary>
        /// Monotonic timestamp captured immediately before the logical panel call.
        /// </summary>
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();

        /// <summary>
        /// Idempotency flag preventing a logical panel call from being counted twice.
        /// </summary>
        private int _disposed;

        /// <summary>
        /// Starts measuring a panel call for the specified operation scope.
        /// </summary>
        /// <param name="owner">Required operation scope that receives the elapsed monotonic ticks.</param>
        public PanelCallMeasurement(XuiOperationTiming owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Stops this panel-call timer and atomically adds its duration to the owning operation.
        /// </summary>
        /// <remarks>Repeated disposal is ignored so retries and exception unwinding cannot double-count a call.</remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var elapsed = Stopwatch.GetTimestamp() - _startedTimestamp;
            Interlocked.Add(ref _owner._panelApiStopwatchTicks, Math.Max(0L, elapsed));
        }
    }

    /// <summary>
    /// Represents an allocation-free panel measurement when no ambient operation timer is active.
    /// </summary>
    private sealed class NoopDisposable : IDisposable
    {
        /// <summary>
        /// Gets the shared no-op measurement instance.
        /// </summary>
        public static readonly NoopDisposable Instance = new();

        /// <summary>
        /// Completes without changing timing or application state.
        /// </summary>
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Immutable panel-API and end-to-end durations captured for one XUI activity log.
/// </summary>
/// <param name="PanelApiElapsed">
/// Sum of logical XUI panel-call durations, including retries and retry delays but excluding Telegram, database,
/// wallet, website, and deliberate bulk pacing.
/// </param>
/// <param name="TotalElapsed">
/// End-to-end monotonic duration from actual operation execution until the result audit is prepared.
/// </param>
public readonly record struct XuiOperationTimingSnapshot(
    TimeSpan PanelApiElapsed,
    TimeSpan TotalElapsed);
