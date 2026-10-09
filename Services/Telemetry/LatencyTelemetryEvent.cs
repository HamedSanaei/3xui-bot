using System;
using System.Collections.Generic;

namespace Adminbot.Services.Telemetry;

/// <summary>Payload-free versioned observation shared by every owned, tenant and Sales Assistant bot.</summary>
/// <remarks>Times are UTC; durations are monotonic milliseconds. Null means unavailable, never zero. Dimensions must be internal bounded identifiers, not user/chat ids, URLs, text, SQL or exception messages. Inclusive stages overlap and must not be summed.</remarks>
public sealed record LatencyTelemetryEvent
{
    /// <summary>JSONL schema version; this implementation writes version one.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Writer-owned random process session id for aggregating cumulative health gauges across restarts; never a customer identifier.</summary>
    public string SessionId { get; init; }
    /// <summary>Writer-only cached projection mask; not part of JSON schema and never calculated by producers.</summary>
    internal ulong SerializationFields { get; init; }
    /// <summary>UTC instant when this observation was made.</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Closed record family identifying the measurement boundary.</summary>
    public string EventType { get; init; }
    /// <summary>Opaque internal correlation id, never a Telegram user or chat identifier.</summary>
    public string TraceId { get; init; }
    /// <summary>Canonical runtime bot id, including tenant-prefixed ids; null for global background work.</summary>
    public string BotId { get; init; }
    /// <summary>Telegram update id, scoped to BotId; not a customer identifier.</summary>
    public long? UpdateId { get; init; }
    /// <summary>Durable inbox ordering sequence, scoped to BotId.</summary>
    public long? Sequence { get; init; }
    /// <summary>Internal update kind, without update content.</summary>
    public string UpdateType { get; init; }
    /// <summary>Closed stage name describing the measured wait or execution boundary.</summary>
    public string Stage { get; init; }
    /// <summary>Internal operation identifier, never SQL, URLs or customer input.</summary>
    public string Operation { get; init; }
    /// <summary>Allowed Telegram method identifier, without its request payload or URL.</summary>
    public string Method { get; init; }
    /// <summary>Closed cloud/local route label captured for the actual request; null identifies legacy or unrouted observations.</summary>
    public string EndpointType { get; init; }
    /// <summary>Positive identity-scoped route generation captured at admission; never a Telegram identifier.</summary>
    public long? EndpointGeneration { get; init; }
    /// <summary>Closed endpoint migration-state enum name, never the raw persisted state.</summary>
    public string MigrationState { get; init; }
    /// <summary>Monotonic endpoint health probe milliseconds; null when not measured.</summary>
    public double? HealthCheckDurationMs { get; init; }
    /// <summary>Closed manual/automatic/startup migration trigger, without actor identifiers or explanations.</summary>
    public string FailoverTrigger { get; init; }
    /// <summary>Monotonic migration or outage-to-recovery milliseconds, when measured by its lifecycle owner.</summary>
    public double? FailoverDurationMs { get; init; }
    /// <summary>Nonnegative milliseconds remaining before acknowledged logout permits Cloud reuse.</summary>
    public double? CloudReuseRemainingMs { get; init; }
    /// <summary>UTC instant of the last successful endpoint health observation; null when unavailable.</summary>
    public DateTime? LastSuccessUtc { get; init; }
    /// <summary>Closed diagnostic category, including the incident family for summary records.</summary>
    public string Category { get; init; }
    /// <summary>Monotonic elapsed milliseconds for this observation.</summary>
    public double? DurationMs { get; init; }
    /// <summary>Exact successful background SQLite boundaries represented by one fixed histogram snapshot; null for individual events.</summary>
    public long? ObservationCount { get; init; }
    /// <summary>Thirty fixed logarithmic duration-bin counts for background aggregates, with upper bounds of 0.001 * 2^index milliseconds; immutable after admission.</summary>
    public long[] DurationBucketCounts { get; init; }
    /// <summary>Numeric HTTP response code when response headers were received.</summary>
    public int? HttpStatusCode { get; init; }
    /// <summary>Numeric Telegram API rejection code when the SDK supplied one.</summary>
    public int? ApiErrorCode { get; init; }
    /// <summary>Safe exception class category, never its message or stack.</summary>
    public string ExceptionCategory { get; init; }
    /// <summary>Closed origin of cancellation, or null if not known.</summary>
    public string CancellationSource { get; init; }
    /// <summary>Closed timeout budget category, or null if no known timeout occurred.</summary>
    public string TimeoutCategory { get; init; }
    /// <summary>Closed transport/API/persistence failure classification.</summary>
    public string FailureClassification { get; init; }
    /// <summary>Operation attempt number supplied by the existing retry owner.</summary>
    public int? Attempt { get; init; }
    /// <summary>Closed outcome such as success, failure, cancelled, uncertain or degraded.</summary>
    public string Outcome { get; init; }
    /// <summary>UTC receiver arrival; unavailable for pre-telemetry recovered updates.</summary>
    public DateTime? ReceivedAtUtc { get; init; }
    /// <summary>UTC start of admission to the durable inbox.</summary>
    public DateTime? AdmissionStartedAtUtc { get; init; }
    /// <summary>UTC completion of initial inbox persistence.</summary>
    public DateTime? PersistedAtUtc { get; init; }
    /// <summary>UTC start of scheduler waiting.</summary>
    public DateTime? SchedulerWaitingAtUtc { get; init; }
    /// <summary>UTC completion of the durable claim.</summary>
    public DateTime? ClaimedAtUtc { get; init; }
    /// <summary>UTC accepted instant assigned before inbox commit; recovered queue estimates use this persisted origin.</summary>
    public DateTime? InboxAcceptedAtUtc { get; init; }
    /// <summary>UTC start of the application handler.</summary>
    public DateTime? HandlerStartedAtUtc { get; init; }
    /// <summary>UTC start of the first visible response attempt, including an unsuccessful attempt.</summary>
    public DateTime? FirstResponseAttemptAtUtc { get; init; }
    /// <summary>UTC completion of the first visible response attempt, not necessarily acknowledgment.</summary>
    public DateTime? FirstResponseCompletedAtUtc { get; init; }
    /// <summary>UTC end of application handling.</summary>
    public DateTime? HandlerCompletedAtUtc { get; init; }
    /// <summary>UTC end of final inbox persistence.</summary>
    public DateTime? InboxCompletedAtUtc { get; init; }
    /// <summary>Monotonic receiver-to-admission milliseconds.</summary>
    public double? ReceiverToAdmissionMs { get; init; }
    /// <summary>Monotonic complete admission milliseconds.</summary>
    public double? AdmissionMs { get; init; }
    /// <summary>Sum of monotonic inbox admission database attempts, including capacity refusals and retries but excluding capacity-wait sleeps.</summary>
    public double? AdmissionPersistenceMs { get; init; }
    /// <summary>Durable queued milliseconds; recovered timing is labeled by TimingQuality.</summary>
    public double? QueueWaitMs { get; init; }
    /// <summary>Monotonic ready-query snapshot to claim-start milliseconds for a row observed as ready.</summary>
    public double? DispatchDelayMs { get; init; }
    /// <summary>Monotonic completed-claim to handler-start milliseconds, including pre-handler queue diagnostics.</summary>
    public double? ClaimedToHandlerMs { get; init; }
    /// <summary>Monotonic inbox claim milliseconds.</summary>
    public double? ClaimMs { get; init; }
    /// <summary>Monotonic handler wall-clock milliseconds, including awaited work.</summary>
    public double? HandlerMs { get; init; }
    /// <summary>Handler-relative milliseconds until the first visible response attempt.</summary>
    public double? FirstResponseAttemptMs { get; init; }
    /// <summary>Handler-relative milliseconds until that first visible attempt completed, even if it failed.</summary>
    public double? FirstResponseCompletedMs { get; init; }
    /// <summary>Handler-relative milliseconds until the first successful visible response completed.</summary>
    public double? FirstResponseAcknowledgedMs { get; init; }
    /// <summary>Monotonic final inbox persistence milliseconds.</summary>
    public double? FinalPersistenceMs { get; init; }
    /// <summary>Receiver-to-final-persistence milliseconds when the complete origin is available.</summary>
    public double? ApplicationMs { get; init; }
    /// <summary>Monotonic post-handler decision milliseconds before final inbox persistence.</summary>
    public double? PostHandlerMs { get; init; }
    /// <summary>Whether no current-process receiver origin is available; TimingQuality distinguishes recovered persistence and direct-enqueue timing.</summary>
    public bool? Recovered { get; init; }
    /// <summary>Closed timing provenance, distinguishing monotonic live and recovered wall-clock timing.</summary>
    public string TimingQuality { get; init; }
    /// <summary>Closed final persistence outcome, independent of handler outcome.</summary>
    public string PersistenceOutcome { get; init; }
    /// <summary>Exclusive non-double-counting stage milliseconds; bounded to internal stage names.</summary>
    public Dictionary<string, double> StageMs { get; init; }
    /// <summary>Inclusive overlapping stage milliseconds; these values must not be summed.</summary>
    public Dictionary<string, double> InclusiveStageMs { get; init; }
    /// <summary>Handler wall-clock milliseconds not covered by named stages; not a CPU measurement.</summary>
    public double? UnattributedHandlerMs { get; init; }
    /// <summary>Longest contiguous gap without a named stage, in milliseconds.</summary>
    public double? MaxUnattributedGapMs { get; init; }
    /// <summary>Handler-relative start of the longest uncovered gap, in milliseconds.</summary>
    public double? MaxGapStartedMs { get; init; }
    /// <summary>Handler-relative end of the longest uncovered gap, in milliseconds.</summary>
    public double? MaxGapEndedMs { get; init; }
    /// <summary>Closed stage immediately before the longest uncovered gap.</summary>
    public string GapBeforeStage { get; init; }
    /// <summary>Closed stage immediately after the longest uncovered gap.</summary>
    public string GapAfterStage { get; init; }
    /// <summary>Closed stage with greatest exclusive elapsed time.</summary>
    public string SlowestStage { get; init; }
    /// <summary>Callback acknowledgment request milliseconds, separate from visible responses.</summary>
    public double? CallbackAckMs { get; init; }
    /// <summary>Handler-relative milliseconds to the first callback acknowledgment attempt.</summary>
    public double? CallbackAckAttemptMs { get; init; }
    /// <summary>Handler-relative milliseconds to a successful callback acknowledgment.</summary>
    public double? CallbackAckAcknowledgedMs { get; init; }
    /// <summary>Sum of logical Telegram request milliseconds; concurrent requests may overlap.</summary>
    public double? TotalTelegramMs { get; init; }
    /// <summary>Number of logical Telegram requests in this handler.</summary>
    public long? TelegramRequestCount { get; init; }
    /// <summary>Completed interactive foreground or best-effort callback deadline outcomes; caller shutdown is excluded.</summary>
    public int? ForegroundTimeoutCount { get; init; }
    /// <summary>SQLite busy retries performed by the existing operation policy.</summary>
    public int? BusyRetryCount { get; init; }
    /// <summary>Existing SQLite retry-backoff milliseconds.</summary>
    public double? BusyWaitMs { get; init; }
    /// <summary>SQLite numeric error code, without SQL or error text.</summary>
    public int? SqliteErrorCode { get; init; }
    /// <summary>UTC instant of the last successful poll.</summary>
    public DateTime? LastSuccessfulPollAtUtc { get; init; }
    /// <summary>UTC instant of the last received update, independent of empty successful polls.</summary>
    public DateTime? LastReceivedUpdateAtUtc { get; init; }
    /// <summary>UTC instant when the current polling degradation began.</summary>
    public DateTime? DegradedSinceUtc { get; init; }
    /// <summary>Consecutive polling failures since the last successful poll.</summary>
    public int? ConsecutiveFailures { get; init; }
    /// <summary>Monotonic polling degradation-to-recovery milliseconds.</summary>
    public double? RecoveryMs { get; init; }
    /// <summary>Existing poll retry backoff milliseconds; telemetry never changes it.</summary>
    public double? BackoffMs { get; init; }
    /// <summary>Whether a receiver is running according to its lifecycle owner.</summary>
    public bool? ReceiverRunning { get; init; }
    /// <summary>Cumulative lost observations in this process, including full channel, invalid records, write and shutdown loss.</summary>
    public long? DroppedEvents { get; init; }
    /// <summary>Cumulative failed writer/flush/recovery operations in this process.</summary>
    public long? WriterFailures { get; init; }
    /// <summary>Current queued observation count, excluding the record being written.</summary>
    public int? ChannelDepth { get; init; }
    /// <summary>Configured maximum queued observation count.</summary>
    public int? ChannelCapacity { get; init; }
    /// <summary>Active scheduler handlers across all bot families.</summary>
    public int? ActiveHandlers { get; init; }
    /// <summary>Pending scheduler updates across all bot families.</summary>
    public int? PendingUpdates { get; init; }
    /// <summary>Process CPU percentage normalized by logical processor count over the sample interval.</summary>
    public double? CpuPercent { get; init; }
    /// <summary>Estimated current managed allocation bytes; no forced collection is performed.</summary>
    public long? ManagedHeapBytes { get; init; }
    /// <summary>OS available memory bytes when a reliable source is supported; otherwise null.</summary>
    public long? AvailableMemoryBytes { get; init; }
    /// <summary>Resident process working-set bytes.</summary>
    public long? WorkingSetBytes { get; init; }
    /// <summary>Runtime pending thread-pool work item count.</summary>
    public long? ThreadPoolPendingWorkItems { get; init; }
    /// <summary>Current runtime thread-pool thread count.</summary>
    public int? ThreadPoolThreads { get; init; }
    /// <summary>Cumulative GC collection counts for generations zero, one and two.</summary>
    public int[] GcCollections { get; init; }
    /// <summary>GC pause milliseconds since the preceding health sample; null for the first sample.</summary>
    public double? GcPauseMs { get; init; }
    /// <summary>Cumulative producer loss because the bounded channel was full.</summary>
    public long? FullChannelDroppedEvents { get; init; }
    /// <summary>Cumulative records lost or uncertain because writes or flushes failed.</summary>
    public long? WriteDroppedEvents { get; init; }
    /// <summary>Cumulative observations rejected because schema, size or dimensions were unsafe.</summary>
    public long? RejectedEvents { get; init; }
    /// <summary>Cumulative records discarded after shutdown admission closed or its drain deadline expired.</summary>
    public long? ShutdownDroppedEvents { get; init; }
    /// <summary>Cumulative records whose complete line was successfully flushed in this process.</summary>
    public long? TotalWrittenEvents { get; init; }
    /// <summary>Cumulative operator summaries omitted by the independent bounded notification queue; persisted observations are unaffected.</summary>
    public long? DroppedIncidentNotifications { get; init; }
    /// <summary>Cumulative incident logger-provider failures; these never increment writer failures or cause recursive alerts.</summary>
    public long? IncidentNotificationFailures { get; init; }
    /// <summary>Pending cooldown summaries in the independent notification queue, bounded to sixteen.</summary>
    public int? IncidentNotificationQueueDepth { get; init; }
    /// <summary>Number of contributing observations in a bounded incident window.</summary>
    public long? IncidentCount { get; init; }
    /// <summary>Incident aggregation window duration in seconds.</summary>
    public double? WindowSeconds { get; init; }
    /// <summary>Approximate 95th-percentile elapsed milliseconds for an incident window.</summary>
    public double? P95Ms { get; init; }
    /// <summary>Approximate prior-window 95th-percentile milliseconds used as the regression baseline.</summary>
    public double? BaselineP95Ms { get; init; }
}
