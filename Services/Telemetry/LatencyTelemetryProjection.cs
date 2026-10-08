using System;
using System.Text.Json.Serialization.Metadata;

namespace Adminbot.Services.Telemetry;

/// <summary>Cached metadata projection retaining common identity and each event family's relevant measurements.</summary>
/// <remarks>Relevant unavailable values remain explicit JSON null. Milestones retain their own boundaries instead of duplicating the complete final timeline; completed update summaries retain the whole timeline, stages and request totals. Bitmasks are calculated once per observation and property, not by reserializing into a DOM.</remarks>
internal static class LatencyTelemetryProjection
{
    /// <summary>Common schema, session and payload-free correlation dimensions.</summary>
    private const ulong Core = 1UL;
    /// <summary>Individual Telegram request metadata.</summary>
    private const ulong Request = 1UL << 1;
    /// <summary>SQLite command, transaction and busy retry metadata.</summary>
    private const ulong Database = 1UL << 2;
    /// <summary>Receiver and polling health metadata.</summary>
    private const ulong Polling = 1UL << 3;
    /// <summary>Process, CPU, GC and thread-pool health measurements.</summary>
    private const ulong Health = 1UL << 4;
    /// <summary>Writer, channel and scheduler cumulative health gauges.</summary>
    private const ulong Counters = 1UL << 5;
    /// <summary>Bounded incident window measurements.</summary>
    private const ulong Incident = 1UL << 6;
    /// <summary>Individual named stage completion.</summary>
    private const ulong Stage = 1UL << 7;
    /// <summary>Exclusive/inclusive handler stage and uncovered gap summary.</summary>
    private const ulong StageSummary = 1UL << 8;
    /// <summary>Handler request totals and callback/visible-response timing summary.</summary>
    private const ulong RequestSummary = 1UL << 9;
    /// <summary>Safe optional exception, cancellation and failure classification.</summary>
    private const ulong Errors = 1UL << 10;
    /// <summary>Complete final update timeline, including every unavailable boundary explicitly.</summary>
    private const ulong AllTimeline = 1UL << 11;
    /// <summary>Receiver-arrival milestone.</summary>
    private const ulong Received = 1UL << 12;
    /// <summary>Admission-start milestone.</summary>
    private const ulong AdmissionStarted = 1UL << 13;
    /// <summary>Initial durable commit milestone.</summary>
    private const ulong Persisted = 1UL << 14;
    /// <summary>Scheduler-wait milestone.</summary>
    private const ulong Waiting = 1UL << 15;
    /// <summary>Durable claim milestone.</summary>
    private const ulong Claimed = 1UL << 16;
    /// <summary>Application handler-start milestone.</summary>
    private const ulong HandlerStarted = 1UL << 17;
    /// <summary>First visible response attempt milestone.</summary>
    private const ulong FirstAttempt = 1UL << 18;
    /// <summary>First visible response completion milestone, including failed completion.</summary>
    private const ulong FirstCompleted = 1UL << 19;
    /// <summary>First successful visible response acknowledgment milestone.</summary>
    private const ulong FirstAcknowledged = 1UL << 20;
    /// <summary>Application handler completion milestone.</summary>
    private const ulong HandlerCompleted = 1UL << 21;
    /// <summary>Final inbox persistence milestone.</summary>
    private const ulong InboxCompleted = 1UL << 22;
    /// <summary>Bounded timeline-origin loss metadata.</summary>
    private const ulong TimelineLost = 1UL << 23;
    /// <summary>Identity-free endpoint health, migration and outage measurements.</summary>
    private const ulong Endpoint = 1UL << 24;

    /// <summary>Determines the fixed relevant-field mask for one closed event family.</summary>
    /// <param name="eventType">Internal closed schema-one record family, never arbitrary customer input.</param>
    /// <returns>Nonzero relevant-field mask; a newly introduced family conservatively retains the full schema until explicitly categorized.</returns>
    /// <remarks>The writer calculates this once when attaching its process session id.</remarks>
    public static ulong FieldsForEvent(string eventType) => Core | (eventType switch
    {
        "telegram_update_received" => Received,
        "telegram_update_admission_started" => AdmissionStarted | Errors,
        "telegram_update_persisted" => Persisted | Errors,
        "telegram_update_waiting" => Waiting,
        "telegram_update_claimed" => Claimed | Errors,
        "telegram_update_handler_started" => HandlerStarted,
        "telegram_update_first_response_attempt" => FirstAttempt | Errors,
        "telegram_update_first_response_completed" => FirstCompleted | Errors,
        "telegram_update_first_response_acknowledged" => FirstAcknowledged | Errors,
        "telegram_update_handler_completed" => HandlerCompleted | StageSummary | RequestSummary | Errors,
        "telegram_update_inbox_completed" => InboxCompleted | Errors,
        "telegram_update_completed" => AllTimeline | StageSummary | RequestSummary | Errors,
        "telegram_timeline_metadata_lost" => TimelineLost | Errors,
        "telegram_request_completed" or "telegram_api_request_completed" or "telegram_foreground_request_completed" => Request | Errors,
        "telegram_endpoint_health" or "telegram_endpoint_migration" or "telegram_endpoint_outage" or "telegram_endpoint_recovered" => Endpoint | Errors,
        "latency_stage_completed" => Stage | Errors,
        "unattributed_handler_time" => StageSummary,
        "sqlite_operation_completed" or "sqlite_busy_retry" or "sqlite_transaction_completed" => Database | Errors,
        "telegram_poll_completed" or "telegram_poll_failed" or "telegram_poll_recovered" or "telegram_poll_backoff"
            or "telegram_receiver_started" or "telegram_receiver_stopped" or "telegram_receiver_health" or "telegram_receiver_startup" => Polling | Errors,
        "process_health" => Health | Counters,
        "telemetry_started" or "telemetry_stopped" or "telemetry_loss" or "telemetry_writer_failure" => Counters | Errors,
        "telemetry_incident" => Incident | Counters,
        _ => ulong.MaxValue
    });

    /// <summary>Installs fixed per-property predicates on cached telemetry serializer metadata.</summary>
    /// <param name="typeInfo">Serializer type metadata supplied once by the shared resolver.</param>
    /// <remarks>No per-record dictionaries, DOMs, reflection or temporary JSON copies are created by this projection.</remarks>
    public static void Configure(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(LatencyTelemetryEvent)) return;
        foreach (var property in typeInfo.Properties)
        {
            var fields = FieldsForProperty(property.Name);
            property.ShouldSerialize = (instance, _) => (((LatencyTelemetryEvent)instance).SerializationFields & fields) != 0;
        }
    }

    /// <summary>Maps one cached camel-case schema property to its relevant event-family masks.</summary>
    /// <param name="name">Serializer-owned property name, calculated once during metadata initialization.</param>
    /// <returns>Relevant mask; unknown newly added properties conservatively remain available in every family.</returns>
    private static ulong FieldsForProperty(string name) => name switch
    {
        "schemaVersion" or "sessionId" or "timestampUtc" or "eventType" or "traceId" or "botId" or "updateId"
            or "sequence" or "updateType" or "operation" or "category" or "outcome" or "timingQuality" => Core,
        "endpointType" or "endpointGeneration" or "migrationState" => Request | Endpoint,
        "healthCheckDurationMs" or "failoverTrigger" or "failoverDurationMs" or "cloudReuseRemainingMs" or "lastSuccessUtc" => Endpoint,
        "stage" => Request | Database | Polling | Stage | StageSummary | AllTimeline | HandlerStarted | FirstAttempt | FirstCompleted | FirstAcknowledged,
        "method" => Request | FirstAttempt | FirstCompleted | FirstAcknowledged,
        "durationMs" => Request | Endpoint | Database | Polling | Stage | StageSummary | AllTimeline | HandlerCompleted | TimelineLost,
        "httpStatusCode" or "apiErrorCode" => Request | Endpoint | Polling | AllTimeline | HandlerCompleted | FirstCompleted | FirstAcknowledged,
        "exceptionCategory" or "cancellationSource" or "timeoutCategory" or "failureClassification" => Errors,
        "attempt" => Request | Database | Polling,
        "receivedAtUtc" => AllTimeline | Received,
        "admissionStartedAtUtc" => AllTimeline | AdmissionStarted | Persisted,
        "persistedAtUtc" => AllTimeline | Persisted | Waiting,
        "schedulerWaitingAtUtc" => AllTimeline | Waiting | Claimed,
        "claimedAtUtc" => AllTimeline | Claimed | HandlerStarted,
        "inboxAcceptedAtUtc" => AllTimeline | Persisted | Waiting | Claimed,
        "handlerStartedAtUtc" => AllTimeline | HandlerStarted | FirstAttempt | FirstCompleted | FirstAcknowledged | HandlerCompleted,
        "firstResponseAttemptAtUtc" => AllTimeline | FirstAttempt,
        "firstResponseCompletedAtUtc" => AllTimeline | FirstCompleted,
        "handlerCompletedAtUtc" => AllTimeline | HandlerCompleted | InboxCompleted,
        "inboxCompletedAtUtc" => AllTimeline | InboxCompleted,
        "receiverToAdmissionMs" => AllTimeline | AdmissionStarted,
        "admissionMs" or "admissionPersistenceMs" => AllTimeline | Persisted,
        "queueWaitMs" => AllTimeline | Claimed | HandlerStarted,
        "dispatchDelayMs" or "claimMs" => AllTimeline | Claimed,
        "claimedToHandlerMs" => AllTimeline | HandlerStarted,
        "handlerMs" => AllTimeline | HandlerCompleted,
        "firstResponseAttemptMs" => AllTimeline | FirstAttempt | RequestSummary,
        "firstResponseCompletedMs" => AllTimeline | FirstCompleted | RequestSummary,
        "firstResponseAcknowledgedMs" => AllTimeline | FirstAcknowledged | RequestSummary,
        "finalPersistenceMs" or "postHandlerMs" or "applicationMs" => AllTimeline | InboxCompleted,
        "recovered" => AllTimeline | Waiting | Claimed | HandlerStarted | TimelineLost,
        "persistenceOutcome" => AllTimeline | Persisted | InboxCompleted,
        "stageMs" or "inclusiveStageMs" or "unattributedHandlerMs" or "maxUnattributedGapMs" or "maxGapStartedMs"
            or "maxGapEndedMs" or "gapBeforeStage" or "gapAfterStage" or "slowestStage" => StageSummary,
        "callbackAckMs" or "callbackAckAttemptMs" or "callbackAckAcknowledgedMs" or "totalTelegramMs"
            or "telegramRequestCount" or "foregroundTimeoutCount" => RequestSummary,
        "busyRetryCount" or "busyWaitMs" or "sqliteErrorCode" => Database | AllTimeline | HandlerCompleted,
        "consecutiveFailures" => Polling | Endpoint,
        "lastSuccessfulPollAtUtc" or "lastReceivedUpdateAtUtc" or "degradedSinceUtc"
            or "recoveryMs" or "backoffMs" or "receiverRunning" => Polling,
        "cpuPercent" or "managedHeapBytes" or "availableMemoryBytes" or "workingSetBytes" or "threadPoolPendingWorkItems"
            or "threadPoolThreads" or "gcCollections" or "gcPauseMs" => Health,
        "droppedEvents" or "writerFailures" or "channelDepth" or "channelCapacity" or "activeHandlers" or "pendingUpdates"
            or "fullChannelDroppedEvents" or "writeDroppedEvents" or "rejectedEvents" or "shutdownDroppedEvents" or "totalWrittenEvents"
            or "droppedIncidentNotifications" or "incidentNotificationFailures" or "incidentNotificationQueueDepth" => Counters,
        "incidentCount" or "windowSeconds" or "p95Ms" or "baselineP95Ms" => Incident,
        _ => ulong.MaxValue
    };
}
