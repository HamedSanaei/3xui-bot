using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services.Telemetry;

/// <summary>Bounded nonblocking latency collection with one recoverable asynchronous JSONL writer.</summary>
/// <remarks>Producers only inspect flags, increment atomic counters and call TryWrite: they never log, perform disk/network I/O, wait for channel capacity or touch SQL. StartAsync never opens the directory. Filesystem outages discard explicitly counted observations while bounded recovery continues. Register this hosted service before receivers so it stops after their drains.</remarks>
public sealed class LatencyTelemetryService : IHostedService, IDisposable
{
    /// <summary>Closed accepted schema-one event families.</summary>
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "telegram_update_received", "telegram_update_admission_started", "telegram_update_persisted",
        "telegram_update_waiting", "telegram_update_claimed", "telegram_update_handler_started",
        "telegram_update_first_response_attempt", "telegram_update_first_response_completed",
        "telegram_update_first_response_acknowledged",
        "telegram_update_handler_completed", "telegram_update_inbox_completed", "telegram_update_completed",
        "telegram_request_completed", "telegram_api_request_completed", "telegram_foreground_request_completed",
        "latency_stage_completed", "unattributed_handler_time", "sqlite_operation_completed", "sqlite_busy_retry",
        "sqlite_transaction_completed", "telegram_poll_completed", "telegram_poll_failed", "telegram_poll_recovered",
        "telegram_poll_backoff", "telegram_receiver_started", "telegram_receiver_stopped", "telegram_receiver_health",
        "telegram_receiver_startup", "process_health", "telemetry_loss", "telemetry_writer_failure", "telemetry_started",
        "telemetry_stopped", "telemetry_incident", "telegram_timeline_metadata_lost"
    };
    /// <summary>Private validated configuration snapshot.</summary>
    private readonly LatencyTelemetryOptions _options;
    /// <summary>Injected UTC clock for lifecycle timestamps and file rotation; production uses the system clock.</summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>Bounded multi-producer/single-consumer channel; absent when disabled.</summary>
    private readonly Channel<LatencyTelemetryEvent> _channel;
    /// <summary>Independent bounded summary-notification queue; logger providers never block the JSONL writer.</summary>
    private readonly Channel<LatencyTelemetryEvent> _incidentNotifications;
    /// <summary>Writer deadline cancellation, independent of handler/request cancellation.</summary>
    private readonly CancellationTokenSource _shutdown = new();
    /// <summary>Late-bound incident-only logger, avoiding transport/logger dependency cycles.</summary>
    private ILogger<LatencyTelemetryService> _logger;
    /// <summary>Single writer task, assigned once at hosted startup.</summary>
    private Task _writerTask = Task.CompletedTask;
    /// <summary>Bounded summary-notification worker lifetime, separate from the single file writer.</summary>
    private Task _notificationTask = Task.CompletedTask;
    /// <summary>Combined writer and notifier lifetime used for one bounded service shutdown.</summary>
    private Task _completion = Task.CompletedTask;
    /// <summary>Atomic hosted-start guard.</summary>
    private int _started;
    /// <summary>Atomic producer-admission flag, closed before shutdown draining.</summary>
    private int _accepting = 1;
    /// <summary>Atomic disposal guard.</summary>
    private int _disposed;
    /// <summary>Atomic total event-loss count.</summary>
    private long _dropped;
    /// <summary>Atomic full-channel loss count.</summary>
    private long _fullChannelDropped;
    /// <summary>Atomic write/flush uncertainty and loss count.</summary>
    private long _writeDropped;
    /// <summary>Atomic unsafe-schema/size rejection count.</summary>
    private long _rejected;
    /// <summary>Atomic shutdown-admission and drain-deadline loss count.</summary>
    private long _shutdownDropped;
    /// <summary>Atomic write/flush/recovery failure count.</summary>
    private long _writerFailures;
    /// <summary>Latest successfully flushed record count published by the writer.</summary>
    private long _written;
    /// <summary>Latest active scheduler handler count, published in memory only.</summary>
    private int _activeHandlers;
    /// <summary>Latest pending scheduler update count, published in memory only.</summary>
    private int _pendingUpdates;
    /// <summary>Writer-only next monotonic recovery opportunity.</summary>
    private long _nextRecoveryAt;
    /// <summary>Writer-only bounded recovery burst index, capped at four.</summary>
    private int _recoveryAttempt;
    /// <summary>Writer-only latest recorded cumulative loss gauge.</summary>
    private long _reportedLoss;
    /// <summary>Writer-only latest recorded cumulative writer-failure gauge.</summary>
    private long _reportedFailures;
    /// <summary>Random writer-owned process session id, independent of customer and bot identities.</summary>
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    /// <summary>Cumulative cooldown summaries omitted from notification because its bounded queue was full or stopped.</summary>
    private long _droppedIncidentNotifications;
    /// <summary>Cumulative logger-provider notification failures, never reclassified as writer failures.</summary>
    private long _incidentNotificationFailures;

    /// <summary>Creates collection state without filesystem or network access.</summary>
    /// <param name="options">Required startup-bound limits, validated without silent clamps.</param>
    /// <param name="dataDirectory">Required absolute persistent application Data directory; Telemetry is appended, never resolved relative to the working directory.</param>
    /// <param name="logger">Required incident-only logger; NullLogger may be supplied until SetIncidentLogger is called after host construction.</param>
    /// <param name="timeProvider">Optional UTC clock for deterministic file rotation and lifecycle tests; null uses the system clock.</param>
    /// <remarks>All bot families share this process service; identifiers remain scoped in individual records. Disabled collection allocates no channel, file buffers or sampler.</remarks>
    /// <exception cref="ArgumentNullException">Options or logger is null.</exception>
    /// <exception cref="ArgumentException">The persistent Data path is not absolute.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Configured resource bounds are unsafe.</exception>
    /// <example><code>var telemetry = new LatencyTelemetryService(options, Path.Combine(contentRoot, "Data"), logger);</code></example>
    public LatencyTelemetryService(LatencyTelemetryOptions options, string dataDirectory, ILogger<LatencyTelemetryService> logger, TimeProvider timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("Telemetry requires an absolute persistent Data directory.", nameof(dataDirectory));
        _options = options.ValidateAndSnapshot();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        StorageDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "Telemetry");
        if (_options.Enabled)
        {
            _channel = Channel.CreateBounded<LatencyTelemetryEvent>(new BoundedChannelOptions(_options.ChannelCapacity)
            { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
            _incidentNotifications = Channel.CreateBounded<LatencyTelemetryEvent>(new BoundedChannelOptions(16)
            { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        }
    }

    /// <summary>Whether startup configuration enabled collection; disposal/admission state is checked separately.</summary>
    public bool Enabled => _options.Enabled;
    /// <summary>Cumulative observations lost or uncertain in this process.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);
    /// <summary>Current queued observations, excluding the record being processed by the writer.</summary>
    public int ChannelDepth => _channel?.Reader.Count ?? 0;
    /// <summary>Cumulative writer, flush, maintenance and recovery failures.</summary>
    public long WriterFailures => Interlocked.Read(ref _writerFailures);
    /// <summary>Absolute persistent Telemetry directory, safe to expose in operator configuration.</summary>
    public string StorageDirectory { get; }

    /// <summary>Binds the incident logger after the host graph has been constructed.</summary>
    /// <param name="logger">Required application logger receiving cooldown summaries only, never raw observations or exception messages.</param>
    /// <remarks>Program can construct the service with NullLogger to prevent ILoggerFactory-to-transport cycles, then bind the real logger before hosted startup.</remarks>
    /// <exception cref="ArgumentNullException">The logger is null.</exception>
    /// <example><code>telemetry.SetIncidentLogger(services.GetRequiredService&lt;ILogger&lt;LatencyTelemetryService&gt;&gt;());</code></example>
    public void SetIncidentLogger(ILogger<LatencyTelemetryService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Volatile.Write(ref _logger, logger);
    }

    /// <summary>Attempts immediate nonblocking admission of one payload-free event.</summary>
    /// <param name="observation">Required immutable version-one snapshot with bounded safe dimensions; dictionaries must not be mutated after admission.</param>
    /// <returns>True when queued; false when disabled/suppressed, closed, invalid-null or full. Enabled admission losses are explicitly counted.</returns>
    /// <remarks>No producer-side validation, serialization, logging, SQL, disk/network I/O or waiting occurs. Writer-side validation rejects unsafe data before persistence.</remarks>
    /// <example><code>if (telemetry.Enabled) telemetry.TryRecord(new LatencyTelemetryEvent { EventType = "latency_stage_completed", Stage = "sqlite_read", DurationMs = elapsedMs });</code></example>
    public bool TryRecord(LatencyTelemetryEvent observation)
    {
        if (!Enabled || LatencyTelemetrySuppression.IsActive) return false;
        if (observation == null) { AddLoss(ref _rejected, 1); return false; }
        if (Volatile.Read(ref _accepting) == 0) { AddLoss(ref _shutdownDropped, 1); return false; }
        if (_channel.Writer.TryWrite(observation)) return true;
        if (Volatile.Read(ref _accepting) == 0) AddLoss(ref _shutdownDropped, 1);
        else AddLoss(ref _fullChannelDropped, 1);
        return false;
    }

    /// <summary>Publishes latest aggregate scheduler health without logging or I/O.</summary>
    /// <param name="activeHandlers">Nonnegative active handler count across all bot families.</param>
    /// <param name="pendingUpdates">Nonnegative pending update count across all bot families.</param>
    /// <remarks>The writer samples these gauges; publishing health never emits a raw Telegram log.</remarks>
    public void UpdateSchedulerHealth(int activeHandlers, int pendingUpdates)
    {
        if (!Enabled) return;
        Volatile.Write(ref _activeHandlers, activeHandlers);
        Volatile.Write(ref _pendingUpdates, pendingUpdates);
    }

    /// <summary>Publishes the immediate active-handler count without changing the sampled pending-update gauge.</summary>
    /// <param name="activeHandlers">Nonnegative active handler count supplied after the scheduler's existing atomic increment or decrement, across every bot family.</param>
    /// <remarks>This is an in-memory volatile setter only; it never logs, reads the database or performs file/network I/O.</remarks>
    /// <example><code>telemetry.UpdateActiveHandlers(Volatile.Read(ref activeHandlers));</code></example>
    public void UpdateActiveHandlers(int activeHandlers)
    {
        if (Enabled) Volatile.Write(ref _activeHandlers, activeHandlers);
    }

    /// <summary>Starts one asynchronous writer without opening storage on the host-start path.</summary>
    /// <param name="cancellationToken">Host startup token; a cancelled startup does not start collection.</param>
    /// <returns>An already-completed task; storage outages never fail application startup.</returns>
    /// <remarks>Startup events are retried with bounded backoff until storage recovers; producer handlers remain independent.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            _notificationTask = Task.Run(NotificationLoopAsync);
            _writerTask = Task.Run(WriterLoopAsync);
            _completion = Task.WhenAll(_writerTask, _notificationTask);
        }
        return Task.CompletedTask;
    }

    /// <summary>Closes admission and allows a bounded graceful drain, flush and shutdown record.</summary>
    /// <param name="cancellationToken">Host stop token; an earlier cancellation shortens the configured drain deadline.</param>
    /// <returns>A task completing within the configured shutdown seconds or host cancellation; discarded data is counted.</returns>
    /// <remarks>Telemetry never delays receiver/handler shutdown beyond its own configured budget. Cancellation aborts the unbuffered stream and records uncertain pending loss in memory.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        Interlocked.Exchange(ref _accepting, 0);
        _channel.Writer.TryComplete();
        if (Volatile.Read(ref _started) == 0)
        {
            DiscardRemaining();
            return;
        }
        if (_completion.IsCompleted) return;
        _shutdown.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownFlushSeconds));
        try { await _completion.WaitAsync(TimeSpan.FromSeconds(_options.ShutdownFlushSeconds), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { _shutdown.Cancel(); }
    }

    /// <summary>Stops admission and requests cancellation without blocking application disposal.</summary>
    /// <remarks>The writer alone closes its file. Cancellation resources are disposed after the task ends, including a shortened drain.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _accepting, 0);
        _channel?.Writer.TryComplete();
        _shutdown.Cancel();
        if (_completion.IsCompleted) { DiscardRemaining(); _shutdown.Dispose(); }
        else _ = _completion.ContinueWith(static (_, state) => ((CancellationTokenSource)state).Dispose(), _shutdown,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Owns all sampling, aggregation, file I/O, maintenance, retry and operator notification work.</summary>
    /// <returns>The single writer lifetime task; expected storage failures are contained and counted.</returns>
    /// <remarks>A 100 ms periodic tick bounds idle wake-up work; batches contain at most 256 observations so health/flush/shutdown work cannot starve behind traffic.</remarks>
    private async Task WriterLoopAsync()
    {
        using var suppression = LatencyTelemetrySuppression.Enter();
        var writer = new LatencyTelemetryFileWriter(_options, StorageDirectory, _timeProvider);
        using var sampler = new LatencyRuntimeSampler();
        var incidents = new LatencyTelemetryIncidentAggregator(_options);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var token = _shutdown.Token;
        var nextSample = 0L;
        var nextFlush = 0L;
        var nextMaintenance = 0L;
        var startupRecorded = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                if (!startupRecorded && now >= _nextRecoveryAt)
                {
                    if (await PersistAsync(writer, Counters(new LatencyTelemetryEvent { EventType = "telemetry_started", Outcome = "enabled" }), now, token).ConfigureAwait(false))
                        startupRecorded = await FlushAsync(writer, now, token).ConfigureAwait(false);
                }
                for (var read = 0; read < 256 && _channel.Reader.TryRead(out var observation); read++)
                {
                    if (!IsSafe(observation)) { AddLoss(ref _rejected, 1); continue; }
                    incidents.Observe(observation, now);
                    await PersistAsync(writer, observation, now, token).ConfigureAwait(false);
                }
                if (now >= nextSample)
                {
                    var health = Counters(sampler.Capture());
                    await PersistAsync(writer, health, now, token).ConfigureAwait(false);
                    if (DroppedEvents != _reportedLoss)
                    {
                        if (await PersistAsync(writer, Counters(new LatencyTelemetryEvent { EventType = "telemetry_loss", Outcome = "loss" }), now, token).ConfigureAwait(false))
                            _reportedLoss = DroppedEvents;
                    }
                    if (WriterFailures != _reportedFailures)
                    {
                        if (await PersistAsync(writer, Counters(new LatencyTelemetryEvent { EventType = "telemetry_writer_failure", FailureClassification = "storage", Outcome = "failure" }), now, token).ConfigureAwait(false))
                            _reportedFailures = WriterFailures;
                    }
                    foreach (var incident in incidents.Evaluate(Counters(health), now))
                    {
                        await PersistAsync(writer, Counters(incident), now, token).ConfigureAwait(false);
                        if (!_incidentNotifications.Writer.TryWrite(Counters(incident))) Interlocked.Increment(ref _droppedIncidentNotifications);
                    }
                    nextSample = now + _options.SampleIntervalSeconds * 1000L;
                }
                if (writer.IsOpen && now >= nextFlush)
                {
                    await FlushAsync(writer, now, token).ConfigureAwait(false);
                    nextFlush = now + _options.FlushIntervalSeconds * 1000L;
                }
                if (writer.IsOpen && now >= nextMaintenance)
                {
                    try { writer.Maintain(); }
                    catch (Exception exception) when (IsStorageFailure(exception)) { Recover(writer, now, false); }
                    nextMaintenance = now + 30000;
                }
                if (Volatile.Read(ref _accepting) == 0 && ChannelDepth == 0)
                {
                    await PersistAsync(writer, Counters(new LatencyTelemetryEvent { EventType = "telemetry_stopped", Outcome = "drained" }), now, token).ConfigureAwait(false);
                    await FlushAsync(writer, now, token).ConfigureAwait(false);
                    break;
                }
                if (ChannelDepth > 0) continue;
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) break;
            }
            try { await writer.CloseAsync(token).ConfigureAwait(false); }
            catch (Exception exception) when (IsStorageFailure(exception)) { Recover(writer, Environment.TickCount64, false); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            var uncertain = writer.Abort();
            if (uncertain > 0) AddLoss(ref _shutdownDropped, uncertain);
            Volatile.Write(ref _written, writer.FlushedRecords);
            DiscardRemaining();
            _incidentNotifications.Writer.TryComplete();
        }
    }

    /// <summary>Routes only bounded cooldown summaries without making file collection wait on logger providers.</summary>
    /// <returns>The independent notification worker task; provider failures never fault the writer or recurse into telemetry.</returns>
    /// <remarks>At most sixteen pending summaries plus the active one exist. Normal shutdown drains within the same service deadline; queued notifications omitted after cancellation are counted separately from persisted observation loss.</remarks>
    private async Task NotificationLoopAsync()
    {
        using var suppression = LatencyTelemetrySuppression.Enter();
        try
        {
            await foreach (var incident in _incidentNotifications.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                ReportIncident(incident);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            while (_incidentNotifications.Reader.TryRead(out _)) Interlocked.Increment(ref _droppedIncidentNotifications);
        }
    }

    /// <summary>Attempts one bounded-buffer admission or explicitly accounts a unavailable-storage observation.</summary>
    /// <param name="writer">Single-owner storage writer.</param>
    /// <param name="observation">Already validated payload-free record or writer-created summary.</param>
    /// <param name="nowTicks">Monotonic current time for recovery admission.</param>
    /// <param name="token">Writer shutdown-deadline token.</param>
    /// <returns>True if buffered, false for bounded rejection or recoverable loss.</returns>
    private async ValueTask<bool> PersistAsync(LatencyTelemetryFileWriter writer, LatencyTelemetryEvent observation, long nowTicks, CancellationToken token)
    {
        if (nowTicks < _nextRecoveryAt) { AddLoss(ref _writeDropped, 1); return false; }
        try
        {
            var record = observation with
            {
                SessionId = _sessionId,
                SerializationFields = LatencyTelemetryProjection.FieldsForEvent(observation.EventType)
            };
            if (await writer.AppendAsync(record, token).ConfigureAwait(false)) return true;
            AddLoss(ref _rejected, 1);
        }
        catch (JsonException) { AddLoss(ref _rejected, 1); }
        catch (ArgumentException) { AddLoss(ref _rejected, 1); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { AddLoss(ref _shutdownDropped, 1); throw; }
        catch (Exception exception) when (IsStorageFailure(exception)) { Recover(writer, nowTicks, true); }
        return false;
    }

    /// <summary>Flushes pending records once and publishes confirmed written counts.</summary>
    /// <param name="writer">Single-owner file writer.</param>
    /// <param name="nowTicks">Current monotonic milliseconds.</param>
    /// <param name="token">Writer shutdown-deadline token.</param>
    /// <returns>True after a successful flush, false for recoverable storage failure.</returns>
    private async ValueTask<bool> FlushAsync(LatencyTelemetryFileWriter writer, long nowTicks, CancellationToken token)
    {
        try
        {
            await writer.FlushAsync(token).ConfigureAwait(false);
            Volatile.Write(ref _written, writer.FlushedRecords);
            _recoveryAttempt = 0;
            _nextRecoveryAt = 0;
            return true;
        }
        catch (Exception exception) when (IsStorageFailure(exception)) { Recover(writer, nowTicks, false); return false; }
    }

    /// <summary>Accounts a storage failure and schedules a bounded recovery opportunity without recursion.</summary>
    /// <param name="writer">Failed file/buffer owner, aborted before the next new file.</param>
    /// <param name="nowTicks">Current monotonic milliseconds.</param>
    /// <param name="currentRecordLost">Whether a not-yet-buffered current line also failed.</param>
    /// <remarks>Recovery attempts are spaced by one, two, five, then thirty seconds. No original record is replayed after an ambiguous write, and no logger/network call occurs here.</remarks>
    private void Recover(LatencyTelemetryFileWriter writer, long nowTicks, bool currentRecordLost)
    {
        Interlocked.Increment(ref _writerFailures);
        AddLoss(ref _writeDropped, writer.Abort() + (currentRecordLost ? 1 : 0));
        _recoveryAttempt = Math.Min(4, _recoveryAttempt + 1);
        _nextRecoveryAt = nowTicks + (_recoveryAttempt switch { 1 => 1000, 2 => 2000, 3 => 5000, _ => 30000 });
    }

    /// <summary>Attaches cumulative counters and latest in-memory scheduler health to a writer-created record.</summary>
    /// <param name="observation">Immutable writer-created health, lifecycle, loss or incident observation.</param>
    /// <returns>An independent summary snapshot with explicit cumulative loss categories.</returns>
    private LatencyTelemetryEvent Counters(LatencyTelemetryEvent observation) => observation with
    {
        SessionId = _sessionId,
        TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
        DroppedEvents = DroppedEvents, WriterFailures = WriterFailures, ChannelDepth = ChannelDepth,
        ChannelCapacity = _options.ChannelCapacity, ActiveHandlers = Volatile.Read(ref _activeHandlers),
        PendingUpdates = Volatile.Read(ref _pendingUpdates), FullChannelDroppedEvents = Interlocked.Read(ref _fullChannelDropped),
        WriteDroppedEvents = Interlocked.Read(ref _writeDropped), RejectedEvents = Interlocked.Read(ref _rejected),
        ShutdownDroppedEvents = Interlocked.Read(ref _shutdownDropped), TotalWrittenEvents = Interlocked.Read(ref _written),
        DroppedIncidentNotifications = Interlocked.Read(ref _droppedIncidentNotifications),
        IncidentNotificationFailures = Interlocked.Read(ref _incidentNotificationFailures),
        IncidentNotificationQueueDepth = _incidentNotifications?.Reader.Count ?? 0
    };

    /// <summary>Routes one already-cooldown-limited summary through existing plain operator logging.</summary>
    /// <param name="incident">Closed incident family and safe bot/numeric dimensions; no raw event, exception text or customer identifier.</param>
    /// <remarks>Ordinary Warning routing uses the existing bounded in-memory logger queue, not its SQL Payment/HTML outbox. Logger failures are swallowed and never counted as storage failures or reinstrumented.</remarks>
    private void ReportIncident(LatencyTelemetryEvent incident)
    {
        try
        {
            var logger = Volatile.Read(ref _logger);
            if (!logger.IsEnabled(LogLevel.Warning)) return;
            using var suppression = LatencyTelemetrySuppression.IsActive ? null : LatencyTelemetrySuppression.Enter();
            logger.LogWarning(
                "Latency telemetry incident. Category={Category} BotId={BotId} Aggregation={Aggregation} Count={Count} WindowSeconds={WindowSeconds} P95Ms={P95Ms} BaselineP95Ms={BaselineP95Ms} DroppedEvents={DroppedEvents} WriterFailures={WriterFailures}",
                incident.Category, incident.BotId ?? "global", incident.Operation, incident.IncidentCount, incident.WindowSeconds,
                incident.P95Ms, incident.BaselineP95Ms, incident.DroppedEvents, incident.WriterFailures);
        }
        catch (Exception) { Interlocked.Increment(ref _incidentNotificationFailures); }
    }

    /// <summary>Atomically adds one explicit category of observation loss and its global total.</summary>
    /// <param name="category">Process-owned atomic counter for this loss category.</param>
    /// <param name="count">Nonnegative number of lost or uncertain complete observations.</param>
    private void AddLoss(ref long category, long count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref category, count);
        Interlocked.Add(ref _dropped, count);
    }

    /// <summary>Discards remaining queued references after admission closes and accounts shutdown loss.</summary>
    /// <remarks>The bounded channel limits this operation to ChannelCapacity records; no disk or logger is touched.</remarks>
    private void DiscardRemaining()
    {
        if (_channel == null) return;
        long discarded = 0;
        while (_channel.Reader.TryRead(out _)) discarded++;
        AddLoss(ref _shutdownDropped, discarded);
    }

    /// <summary>Recognizes expected recoverable local storage failures without exposing their messages.</summary>
    /// <param name="exception">Caught writer-only failure.</param>
    /// <returns>True for local I/O, permissions, platform or bounded-accounting failures.</returns>
    private static bool IsStorageFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or OverflowException;

    /// <summary>Checks schema family and bounded safe dimensions before any serialization or persistence.</summary>
    /// <param name="observation">Channel observation supplied by trusted internal instrumentation.</param>
    /// <returns>True for version-one, payload-free, bounded dimensions and finite stage values.</returns>
    /// <remarks>ASCII tokens admit tenant-prefixed and underscore bot ids while excluding URLs, message text, SQL and control characters. Arbitrary customer or chat ids must never be supplied by callers.</remarks>
    private static bool IsSafe(LatencyTelemetryEvent observation)
        => observation.SchemaVersion == 1 && observation.EventType != null && EventTypes.Contains(observation.EventType)
            && IsToken(observation.TraceId) && IsToken(observation.BotId) && IsToken(observation.UpdateType)
            && IsToken(observation.Stage) && IsToken(observation.Operation) && IsToken(observation.Method)
            && IsToken(observation.Category) && IsToken(observation.ExceptionCategory) && IsToken(observation.CancellationSource)
            && IsToken(observation.TimeoutCategory) && IsToken(observation.FailureClassification) && IsToken(observation.Outcome)
            && IsToken(observation.TimingQuality) && IsToken(observation.PersistenceOutcome) && IsToken(observation.GapBeforeStage)
            && IsToken(observation.GapAfterStage) && IsToken(observation.SlowestStage)
            && IsStageMap(observation.StageMs) && IsStageMap(observation.InclusiveStageMs)
            && (observation.GcCollections == null || observation.GcCollections.Length == 3)
            && IsFinite(observation.DurationMs) && IsFinite(observation.ReceiverToAdmissionMs) && IsFinite(observation.AdmissionMs)
            && IsFinite(observation.AdmissionPersistenceMs) && IsFinite(observation.QueueWaitMs) && IsFinite(observation.DispatchDelayMs)
            && IsFinite(observation.ClaimedToHandlerMs)
            && IsFinite(observation.ClaimMs) && IsFinite(observation.HandlerMs) && IsFinite(observation.FirstResponseAttemptMs)
            && IsFinite(observation.FirstResponseCompletedMs) && IsFinite(observation.FirstResponseAcknowledgedMs)
            && IsFinite(observation.FinalPersistenceMs) && IsFinite(observation.PostHandlerMs) && IsFinite(observation.ApplicationMs)
            && IsFinite(observation.UnattributedHandlerMs) && IsFinite(observation.MaxUnattributedGapMs)
            && IsFinite(observation.MaxGapStartedMs) && IsFinite(observation.MaxGapEndedMs) && IsFinite(observation.CallbackAckMs)
            && IsFinite(observation.CallbackAckAttemptMs) && IsFinite(observation.CallbackAckAcknowledgedMs)
            && IsFinite(observation.TotalTelegramMs) && IsFinite(observation.BusyWaitMs) && IsFinite(observation.RecoveryMs)
            && IsFinite(observation.BackoffMs) && IsFinite(observation.CpuPercent) && IsFinite(observation.GcPauseMs)
            && IsFinite(observation.WindowSeconds) && IsFinite(observation.P95Ms) && IsFinite(observation.BaselineP95Ms);

    /// <summary>Checks an optional internal dimension against a 96-character ASCII token bound.</summary>
    /// <param name="value">Optional non-secret internal identifier, never arbitrary external input.</param>
    /// <returns>True for null or a bounded token; false for whitespace, controls, URLs or oversized strings.</returns>
    private static bool IsToken(string value)
    {
        if (value == null) return true;
        if (value.Length == 0 || value.Length > 96) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.') return false;
        return true;
    }

    /// <summary>Validates a fixed-cardinality named-stage map without copying it.</summary>
    /// <param name="stages">Optional immutable-after-admission stage milliseconds dictionary.</param>
    /// <returns>True for no map or at most thirty-two safe nonnegative finite stage durations.</returns>
    private static bool IsStageMap(Dictionary<string, double> stages)
    {
        if (stages == null) return true;
        if (stages.Count > 32) return false;
        foreach (var stage in stages)
            if (!IsToken(stage.Key) || !double.IsFinite(stage.Value) || stage.Value < 0) return false;
        return true;
    }

    /// <summary>Validates an optional nonnegative measured duration or health value.</summary>
    /// <param name="value">Optional numeric measurement; null means unavailable.</param>
    /// <returns>True for unavailable or finite nonnegative data, false for NaN, infinity or negative values.</returns>
    private static bool IsFinite(double? value) => value == null || (double.IsFinite(value.Value) && value.Value >= 0);
}
