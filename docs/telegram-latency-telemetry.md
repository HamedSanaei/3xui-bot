# Telegram latency telemetry

## Architecture and guarantees

Telemetry is enabled by default for owned bots, tenant bots and the Sales Assistant. It extends `TelegramUpdateLatencyScope`; it does not introduce a second handler tracer. `UpdateTelemetryTracker` links receiver timestamps to the existing durable inbox sequence without adding a database column or migration. The receiver's super-admin control path remains usable when customer admission is full and has a separate, explicitly inbox-free summary.

`LatencyTelemetryService` admits immutable, payload-free observations with a bounded `Channel.TryWrite`. Producers never wait for capacity, serialize, write files, write telemetry to SQLite, log an incident or contact Telegram. One asynchronous writer performs JSON serialization, buffering, rotation, retention, process sampling and incident aggregation. There is no task per observation. Storage errors are contained; failed or uncertain observations are counted, not replayed after ambiguous file writes.

The existing durable inbox, strict FIFO for each bot/user lane, fair bot dispatch, concurrency limit, financial transactions, delivery idempotency, foreground budgets, callback budget, SDK retry setting and polling backoff remain unchanged. Instrumentation is not a speculative latency fix.

## Storage and deployment

The absolute telemetry directory is `Telemetry` below the directory containing the resolved `UserDatabasePath`. `Program.ConfigureDatabasePaths` resolves the database configuration against the application content root; collection does not use the developer's current shell directory.

With the existing production deployment layout, the default path is:

```text
/root/vpnetiran/bin/Release/net10.0/linux-x64/publish/Data/Telemetry/
```

Immutable deployments retain it through the shared Data directory:

```text
/opt/vpnetiran/shared/Data/Telemetry/
```

A custom users database directory changes this location. Normal startup prints the resolved path. The reporting command deliberately does not read private application configuration; use `--directory` for custom paths.

Files are `latency-<UTC time>-<session suffix>-<rotation>.jsonl`, UTF-8 without a BOM, append-only until retention deletes a complete old file. A new process or storage recovery starts a new file; old uncertain partial lines are never appended to. Linux creates the dedicated directory with mode **0700** and files with mode **0600**, rejects telemetry-directory/file symlink redirection, and secures existing owned files. The parent persistent Data directory is not altered. Windows inherits the application's directory ACL; restrict it to the service owner and authorized operators.

`.gitignore` and the project exclude `Data/Telemetry` from source/build/publish artifacts. Production source synchronization protects and excludes checkout telemetry; publish synchronization already protects all persistent Data. Immutable releases link the shared Data directory rather than copying it. `scripts/deploy-production.tests.sh` exercises real rsync preservation and staged telemetry rejection. Do not copy telemetry into a release archive or publicly serve it.

## Configuration

Add the optional root `latencyTelemetry` object to persistent `Data/configuration.json`; `Data/configuration.example.json` contains the defaults. Configuration is snapshotted at startup, not dynamically reloaded.

| Setting | Default | Unit / purpose |
| --- | ---: | --- |
| `enabled` | `true` | Collection enabled; false allocates no channel/writer/sampler |
| `retentionDays` | 14 | UTC age retention |
| `maxFileBytes` | 26214400 | 25 MiB, rotate before the next line would exceed it |
| `maxTotalBytes` | 524288000 | 500 MiB across owned files |
| `channelCapacity` | 8192 | Maximum queued observations, not updates |
| `sampleIntervalSeconds` | 30 | Process sampling and bounded incident evaluation |
| `flushIntervalSeconds` | 2 | Normal buffered flush interval |
| `shutdownFlushSeconds` | 5 | Maximum writer drain/flush budget |
| `slowOperationMs` | 2000 | Detailed slow stage/gap and incident threshold |
| `slowUpdateMs` | 5000 | Runtime P95 incident threshold |

Validation rejects unsafe limits rather than silently clamping them: retention 1–365 days; individual files 1–256 MiB; total storage at least two file limits and at most 2 GiB; channel 64–65536; sample 5–300 seconds; flush 1–30 seconds; shutdown 1–30 seconds; slow thresholds 100–300000 ms. The total byte cap can evict files younger than fourteen days: retention is an upper age, not a promise to retain fourteen days at every traffic rate. Bounded maintenance can require several passes; an oversized/inaccessible directory causes counted writer loss rather than blocking handlers.

No production secrets, extra monitoring dependency or telemetry database migration is required. Ensure the existing service user can write its persistent Data directory and disk capacity can accommodate the configured cap.

## Schema version 1

One JSON object per complete line. `schemaVersion=1` is mandatory. Every record has `timestampUtc` in UTC and `eventType`. `sessionId` is a random writer-process identifier, used to distinguish cumulative counters across restarts. `traceId` is a stable opaque 128-bit hash of canonical BotId plus UpdateId; `sequence` is the existing inbox receipt sequence. `botId`, `updateId` and `updateType` are operational identities/categories, not message content. Uncommitted/control requests have a null sequence.

The writer projects fields by event family. Relevant unavailable measurements are explicit **null**, never zero; unrelated fields are omitted. Durations are milliseconds measured with monotonic Stopwatch/TimeProvider timestamps. Stored SQLite UTC timestamps can only provide a separately identified recovery estimate; they cannot reconstruct pre-restart monotonic clocks.

### Update timeline

Every terminal scheduled execution, including fast successful handlers, attempts a `telegram_update_completed` summary. Intermediate event families identify reception, admission start, persistence, waiting, claim, handler start, first visible response attempt/completion/acknowledgment, handler completion and inbox completion. Correlation is identical across those events, stage/SQLite observations and HTTP requests.

| Field | Exact meaning |
| --- | --- |
| `receivedAtUtc` | Application receiver callback entry; not the user's physical action |
| `admissionStartedAtUtc`, `receiverToAdmissionMs` | Durable admission entry and interval since receiver entry, including receiver admin-path checks |
| `inboxAcceptedAtUtc` | Existing stored timestamp assigned before the insertion commit; not proof of commit completion |
| `persistedAtUtc`, `admissionMs` | Observed insertion commit completion and complete admission interval, including capacity backpressure |
| `admissionPersistenceMs` | Sum of actual database admission attempts/retries; excludes scheduler capacity sleeps |
| `schedulerWaitingAtUtc`, `queueWaitMs` | Post-commit scheduler wait to claim start; recovered rows use stored acceptance-to-claim UTC estimate |
| `claimedAtUtc`, `claimMs` | Observed claim completion and actual claim operation duration |
| `dispatchDelayMs` | Ready-query snapshot to claim start; not the unknowable original instant a lane became eligible |
| `claimedToHandlerMs` | Claim completion to actual handler invocation, including existing queue diagnostics |
| `handlerStartedAtUtc`, `handlerCompletedAtUtc`, `handlerMs` | Actual awaited handler invocation and completion, excluding final persistence/review |
| `firstResponseAttemptAtUtc`, `firstResponseAttemptMs` | First actual message/edit/media attempt, relative to handler start |
| `firstResponseCompletedAtUtc`, `firstResponseCompletedMs` | Completion of that first visible attempt, including failure |
| `firstResponseAcknowledgedMs` | First successful visible API response, possibly a later attempt; not proof the user read it |
| `callbackAckMs`, `callbackAckAttemptMs`, `callbackAckAcknowledgedMs` | Separate first callback ACK request duration, attempt offset and successful offset; ACK is not a visible message |
| `postHandlerMs` | Existing post-handler business-review decision interval before final receipt persistence |
| `inboxCompletedAtUtc`, `finalPersistenceMs` | Actual successful Running-row terminal transition timestamp and persistence-attempt duration; no owned running claim means a null endpoint and `persistenceOutcome=no_running_claim` |
| `applicationMs` | Locally observed receiver-to-final-persistence-attempt endpoint; origin unavailable after restart yields null |
| `outcome`, `persistenceOutcome` | Handler/control/recovery result and independent final inbox persistence result |
| `recovered`, `timingQuality` | No receiver monotonic metadata survived; provenance explains the missing origin or restart interruption |

Receiver control commands use `outcome=receiver_control_path` and `timingQuality=receiver_control_path_no_inbox`; their real handler/response clocks remain available but inbox milestones are null. Duplicate deliveries use `outcome=duplicate`; they close correlation records but do not count as another execution in reports. Aborted reception is explicit. Restart recovery retains `completed_with_review`, never replays external effects, and records pre-restart durations as null. In-process forced shutdown shares the live terminal-summary guard: recovery and a still-unwinding handler cannot count the same execution twice. Its partial summary can have incomplete handler timing (`live_handler_interrupted_timing_incomplete`), not fabricated completion. Claim/publication synchronization retains receiver origin even when a periodic scan claims before the post-commit continuation; that case has null queue time and `commit_observation_after_claim`. Capacity loss remains visible through `telegram_timeline_metadata_lost`.

### Handler stages and gaps

`stageMs` partitions covered handler wall time into exclusive stages; `inclusiveStageMs` separately records nested/overlapping operation duration and **must not be summed**. Concurrent operations cannot double-count exclusive wall time: each interval belongs to the newest still-active stage timer. Metadata is bounded to 1024 overlapping handles; overflow is quality-visible.

Controlled stages include `telegram_polling`, `telegram_send`, `telegram_edit`, `telegram_callback_ack`, `telegram_membership`, `sqlite_read`, `sqlite_write`, `sqlite_busy_retry`, `xui_read`, `xui_write`, `site_lookup`, `payment_gateway`, `external_http`, `lock_wait` and `business_processing`. Existing recovery/probe stages remain supported through the same scope. Timers surround meaningful existing awaits, not every helper. SQLite command/transaction boundaries, keyed semaphore waits, XUI v2/v3 requests, site lookups, common gateway requests/authentication, release lookups, concurrent currency-quote HTTP calls and activity-file mutex/append are covered.

`unattributedHandlerMs` is uncovered **wall time**, not CPU time. `maxUnattributedGapMs`, `maxGapStartedMs`, `maxGapEndedMs`, `gapBeforeStage` and `gapAfterStage` identify the largest remaining interval. Slow uncovered time emits `unattributed_handler_time`; the watchdog names `unattributed`, not unexplained `none`. Such gaps can be uninstrumented awaits, scheduler/thread-pool delay, a lock or synchronous work; use surrounding milestones and process samples before naming a cause.

`totalTelegramMs` sums logical request durations and can exceed wall time when requests overlap. `telegramRequestCount`, `foregroundTimeoutCount` and `slowestStage` summarize the handler; the timeout count includes existing foreground and best-effort callback deadlines, not caller shutdown. Never sum logical requests, SDK requests, HTTP headers and polling records: those are different views of the same work.

SDK integration was checked against the installed package's pinned
[TelegramBotClient source](https://github.com/TelegramBots/Telegram.Bot/blob/0fc69091287e0656d5b22ef2ebf829288e85cd2e/src/Telegram.Bot/TelegramBotClient.cs)
and [polling loop](https://github.com/TelegramBots/Telegram.Bot/blob/0fc69091287e0656d5b22ef2ebf829288e85cd2e/src/Telegram.Bot/TelegramBotClientExtensions.Polling.cs).

### Transport, polling and SQLite

`telegram_request_completed` measures transport through response headers (`operation=http_response_headers`). `telegram_api_request_completed` measures the entire SDK await, including buffering, deserialization and Telegram API validation (`operation=sdk_validated_request`). Telegram.Bot 22.10.3.2 fires `OnApiResponseReceived` before that validation; only the successful virtual `SendRequest` completion marks a successful poll. The implementation retains cached per-bot/endpoint-generation HttpClients over the same shared socket pool with the SDK's three-minute connection lifetime and RetryCount zero. A started request is never rerouted or replayed during endpoint migration.

Request fields: `method`, `category`, `durationMs`, numeric nullable `httpStatusCode`/`apiErrorCode`, `exceptionCategory`, `failureClassification`, `cancellationSource`, `timeoutCategory` and existing-owner `attempt`. Typed classifications distinguish DNS, TLS, sockets/connection transport, API rejection, 429, gateway 5xx, caller cancellation, HTTP timeout and existing foreground/callback/startup deadlines. Unknown inner transport details remain unknown; exception messages are never used to guess a classification.

Polling records (`telegram_poll_completed`, `telegram_poll_failed`, `telegram_poll_recovered`, `telegram_poll_backoff`) carry `lastSuccessfulPollAtUtc`, `lastReceivedUpdateAtUtc`, `degradedSinceUtc`, `consecutiveFailures`, `recoveryMs`, `backoffMs` and `receiverRunning` where available. Backoff is the exact delay selected by the unchanged owner policy, not a new wait or a promise that cancellation allowed the entire delay. Startup/start/stop events distinguish preflight, active receiver generations and stopped receivers. Successful empty long polls are healthy; their expected duration does not contribute to foreground slow-request incidents. No updates on an idle bot is not evidence of an unhealthy receiver.

SQLite records contain only fixed operation categories, duration, `busyRetryCount`, `busyWaitMs`, numeric `sqliteErrorCode` and failure category. The existing `SqliteOperation` retry policy remains three attempts with its original jitter/delays. Each retry's `durationMs` is its individual actual delay; `busyWaitMs` is cumulative and must not be added repeatedly. EF read timing ends at reader acquisition, not row materialization. Transaction begin/commit/rollback/savepoint boundaries and separately labelled transaction lifetime are independent diagnostic views; do not sum lifetime with its boundaries. No SQL, parameters, connection strings, customer or financial values are recorded.

Fast successful **uncorrelated background** SQLite command, transaction and logical-operation boundaries no longer
create individual JSONL records. They increment fixed lock-free histograms before event allocation/channel admission.
`sqlite_background_aggregate` records are drained at health intervals and shutdown, with `observationCount`,
`durationBucketCounts` (30 bins; upper bound `0.001 × 2^index` milliseconds), `windowSeconds` and
`timingQuality=aggregated_histogram_upper_bounds`. Worker/operation series are bounded; unknown dimensions collapse to
the global background category. Counts are exact; percentile/max duration bounds are approximate, not reconstructed
individual operations. Different command/logical/transaction boundaries remain separate and must not be summed.
Correlated receiver/handler work, slow boundaries, errors, cancellations and retried operations keep detailed events.
Aggregation never waits on the writer and never reads or writes SQLite.

### Endpoint routing observations

Request records additionally carry nullable `endpointType` (`cloud`/`local`), positive `endpointGeneration` and closed `migrationState`, captured at actual request admission. Legacy records have unavailable endpoint metadata and report as `unknown`, not inferred Cloud. Receiver callbacks never inherit a long-poll route scope. Foreground completion captures the actual admitted epoch even after the inner SDK scope exits.

`telegram_endpoint_health`, `telegram_endpoint_migration`, `telegram_endpoint_outage` and `telegram_endpoint_recovered` add `healthCheckDurationMs`, `failoverTrigger` (`manual`, `automatic_outage`, `automatic_failback`, `startup_recovery`), `failoverDurationMs`, `cloudReuseRemainingMs`, `lastSuccessUtc`, `consecutiveFailures` and safe closed failure `category`. Durations are monotonic where observed in one process; a pre-restart failover origin is null. Remaining cooldown is a persisted UTC eligibility countdown, not a transport duration or recovery guarantee. Selected/effective identity-bound state and authenticated actor history live in private users.db, never raw JSONL actor fields.

Migration diagnostics additionally retain uppercase closed `failureClassification` and controlled `stage`/`operation` (for example `local_file_mapping`, `local_root`, `cloud_source_identity`, `cloud_logout`, `local_destination_identity`, `local_destination_receiver`, `source_drain`, `cloud_cooldown`). These identify protocol boundaries, not exclusive handler stage totals; do not add them to `stageMs`. Intermediate migration records use `in_progress`, not success. Inspect these fields in the private JSONL and the authenticated technical panel for exact refusal/phase/action; the existing bounded analyzer keeps its stable projected report vocabulary. Historical generic errors cannot reconstruct a missing phase. See the [Gozargah configuration diagnosis](telegram-api-endpoints.md#authoritative-configuration-and-gozargah-blocker).

Local `--local` file copying uses `telegram_request_completed` with `operation=local_file_download`, `category=file_download`, `stage=business_processing` and no API method: it is filesystem I/O through the validated existing host volume, not a Telegram HTTP request. Its paths/content are never retained in telemetry. See [endpoint protocols, prerequisites and rollout](telegram-api-endpoints.md).


### Runtime health, loss and incidents

Fixed background SQLite categories identify the five incident workers: `tenant_manual_receipt_notification`, `tenant_storefront_funding_alert`, `tenant_discount_reservation`, `payment_settlement_notification` and `xui_renewal_recovery`. These carry no tenant/customer/financial dimensions and are not another tracing clock. Bot-filtered reports retain global database records because shared SQLite contention may affect that bot. `transaction_lifetime` is inclusive begin-to-commit/rollback/disposal wall time, including awaited external work, and is not added to exclusive handler SQLite stages.

`process_health` samples process-normalized CPU percentage, managed allocation/heap estimate, working set, GC collection counts and pause delta, ThreadPool threads/pending work, reliable available memory where supported, exact active handlers, latest pending-inbox sample, channel depth/capacity and cumulative writer/loss counters. Pending inbox depth is sampled by the existing scheduler about every ten seconds, not queried by telemetry. Unreliable connection-pool metrics are intentionally not invented.

Loss counters are `droppedEvents`, `fullChannelDroppedEvents`, `writeDroppedEvents`, `rejectedEvents`, `shutdownDroppedEvents` and `writerFailures`; these are cumulative within a `sessionId`, not per-event deltas. The analyzer sums the maximum recorded gauge independently for each admitted session. Legacy missing-session maxima are reported separately because restart provenance cannot be reconstructed. A filesystem outage/process kill can prevent the newest counter snapshot from reaching JSONL; in-memory counters and bounded existing operator logging remain available while the process is alive.

Only cooldown incident summaries reach the existing plain Warning operator logger. Raw observations stay local. Five-minute bounded windows, a ten-minute per-bot/category cooldown and at most eight incidents per health tick cover sustained polling degradation, repeated foreground timeouts, significant P95 regression, severe queue buildup, repeated SQLite contention (including botless workers), writer failure/loss and sustained channel pressure. A separate capacity-16 notification queue/worker isolates collection from all logger-provider I/O. `droppedIncidentNotifications`, `incidentNotificationFailures` and `incidentNotificationQueueDepth` are independent health gauges, not raw-event loss. Logger sends are telemetry-suppressed to prevent recursive self-observation. Logger channel availability does not govern collection.

Default incident thresholds: at least three polling failures sustained for sixty seconds; three foreground timeouts per five-minute window; twenty updates in both current/prior P95 windows, current P95 at least 5000 ms and twice baseline; three queue waits of thirty seconds, or 1000 pending sustained thirty seconds; ten SQLite busy observations or 2000 ms accumulated retry wait; eighty-percent channel depth sustained sixty seconds. The first observed writer failure/event loss is incident-eligible, then cooldown applies. Unsupported/overflow bot windows remain explicitly global rather than creating unlimited labels.

Storage retry deadlines begin after failure/abort completes. A successful flush with no open file does not mark
storage recovered or clear its backoff. Slow failing I/O previously could consume its backoff before the error was
observed, allowing the next storage attempt without the intended recovery interval.
Recovery creates a new file, never replays uncertain writes. The 500 MiB limit is a
retention ceiling, not a target allocation; aggregation reduces healthy background volume without changing that cap.
Repeated `sqlite_busy`/`telemetry_writer_loss` operator summaries have an additional per-bot/category ten-minute
delivery guard. Scheduler queue-delay summaries are limited per bot/user lane; first alerts, other service failures,
payment/order failures and events with attached exceptions remain visible. Local incident evidence is retained.

## Read-only reporting

These modes return before configuration loading, migration, HTTP hosting, receivers or hosted workers:

```bash
dotnet run --project Adminbot.csproj -c Release -- telemetry-report --hours 24 --bot GozargahNetwork_Bot --directory /absolute/persistent/Data/Telemetry
./Adminbot telemetry-report --hours 24 --directory /root/vpnetiran/bin/Release/net10.0/linux-x64/publish/Data/Telemetry
```

Reports show per-bot update counts/P50/P95/P99/max/slow/interactive-timeout/polling-failure/bottleneck, twenty slowest updates, Telegram API performance by **bot/endpoint/method/boundary**, polling degraded/recovery sequences, SQLite operation/transaction contention, data quality, and bounded endpoint health/migration history. The most frequent bottleneck counts the dominant category per update, not summed time from one outlier. HTTP-header, SDK and foreground series stay separate. Quantiles are labelled approximate: fixed logarithmic buckets have at most about 5% upper-bound resolution above 0.1 ms. Maxima/top twenty are exact for accepted records. Application quantiles use available local `applicationMs` only; recovered handler-only entries are explicitly labelled `rankingBasis=handler_only`. Duplicate admissions are excluded from execution counts. Endpoint history retains the newest 100 observations and counts omissions; it is not the private authenticated-actor migration audit.

The scanner snapshots file lengths, uses fixed buffers, drains oversized lines and reports malformed/truncated/unknown-version records, missing stages/origins/traces, incomplete traces and cardinality limits. Memory is bounded for large archives. Report thresholds are fixed at 5000 ms/update and 2000 ms/interactive operation, independently of runtime incident configuration. Filters retain global loss/health/SQLite context. Exit codes: 0 complete, 1 inaccessible/partially unreadable storage, 2 invalid arguments, 130 cancellation. Only fixed known `vpnetiranbot`, `GozargahNetwork_Bot` and `sales-assistant` labels remain readable; other identities use stable `bot#` aliases even with `--bot` (filter by the original stored id). Unknown bot-like ASCII keys or customer-number labels are never echoed. Links/unsafe entries and zero-length regular/special files are skipped before opening, so a named FIFO cannot hang a stable archive scan; protected archive ownership remains the trust boundary against concurrent filesystem replacement.

## Diagnose a slow update

1. Check data quality first. Event loss, missing pre-restart clocks or truncated records limit causal certainty.
2. Locate its `traceId`, bot, update and sequence in the top twenty; read the matching JSONL metadata locally with an authorized operator.
3. Separate receiver/admin checks, admission/capacity waits, actual SQL attempts, durable queue time, dispatch/claim delay, handler work and final persistence. A queue wait behind a slow predecessor does not imply the concurrency setting is wrong; FIFO remains deliberate.
4. Compare exclusive stages and the longest unattributed gap. A slow request's full SDK duration versus header duration separates response/body/API processing from connection/header wait where those observations are available. Check failure/cancellation provenance instead of assuming every cancellation is an HTTP timeout.
5. Separate callback ACK from first visible message. An ACK plus an eight-second edit can consume independent existing budgets; telemetry observes this accumulation but does not change it.
6. Compare timestamp-adjacent process health: CPU, GC pauses, pending ThreadPool work, channel loss and actual handler/pending counts. Background SQLite BUSY events can explain simultaneous inbox/handler delays without adding telemetry writes to SQLite.
7. If named stages explain little of a long handler, retain `unattributed_handler_time` as the diagnosis and use its measured endpoints to choose the next actual boundary to instrument. Do not label it CPU without evidence.

## Diagnose Gozargah polling degradation

Run the bot-filtered report and inspect `telegram_poll_failed` classifications, consecutive failure sequences, selected backoff, `telegram_poll_recovered.recoveryMs` and receiver generation/startup/stop timestamps. An HTTP 200 is insufficient: Telegram rejection/deserialization can still fail the SDK request. Typed inner DNS/TLS/socket/timeout metadata explains failures that the old generic RequestException line could not distinguish.

Compare the degraded window with the next application's received timestamp. It measures polling availability/recovery and local delivery timing, **not** how long an individual update waited inside Telegram. Existing startup/webhook/409 duplicate-poller handling and authoritative 429 RetryAfter remain in control. No successful poll plus explicit failures is different from healthy empty polling with no customer activity. These local changes alone cannot establish whether production's historical SQLite contention or network incidents persist; production JSONL evidence is required.

## Controlled overhead benchmark and verification

Initial target: **less than 1 ms additional producer P95 per update** in a controlled local Release benchmark. Run:

```bash
dotnet run --project Adminbot.csproj -c Release -- telemetry-benchmark --iterations 10000
./Adminbot telemetry-benchmark --iterations 10000
```

The benchmark warms both enabled/disabled production scope paths, executes correlated lifecycle/stage/request summaries with a live writer, prints P50/P95/P99 and allocation deltas, verifies zero measured/warmup loss and writer faults, and separately probes full-channel admission. It uses unique scratch directories and removes them. Linux checks actual 0700/0600 permissions. The extra-P95 target is a measurement, not a timing assertion in the shared regression suite. Producer latency is not whole-process CPU/GC or real-network latency; process allocation output includes background writer work. Local synthetic workloads do not guarantee identical production percentiles.

Measured on October 8, 2026, .NET 10 Release x64, 10,000 updates/mode after 512 warmup updates, with the actual writer active:

| Platform / mode | P50 ms | P95 ms | P99 ms | Mean producer bytes/update | Mean process bytes/update |
| --- | ---: | ---: | ---: | ---: | ---: |
| Windows / disabled | 0.001200 | 0.001900 | 0.004000 | 1072.12 | 1073.31 |
| Windows / enabled | 0.007800 | 0.021500 | 0.049800 | 41681.00 | 53669.11 |
| WSL Linux / disabled | 0.001800 | 0.003100 | 0.032400 | 1072.12 | 1073.31 |
| WSL Linux / enabled | 0.009200 | 0.039100 | 0.483800 | 41681.25 | 69779.72 |

Windows additional producer P95: **0.019600 ms**; measured-batch/warmup drops and writer faults: **zero**. The separate full-channel probe accepted 64/256 and counted the other 192 exactly. These are producer-only local measurements, not production latency or a claim that writer CPU/allocation is free; process-wide allocation includes asynchronous serialization and drain work.

WSL Linux additional producer P95: **0.036000 ms**, also with zero warmup/batch drops or writer faults and the same exact full-channel probe. The self-contained Linux executable wrote to a native `/tmp` filesystem and passed actual directory/file **0700/0600** checks. WSL lacked ICU; this diagnostic-only process used `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. No production application configuration was changed.

The native executable also ran the read-only analyzer against a regular archive, a symlink and a FIFO: the regular record was read, the symlink and zero-length special file were skipped without blocking, and the unexpected secret-shaped bot label was printed only as a stable alias. Isolated migration preflight completed for both database contexts on Windows and Linux.

The endpoint-routing Release recheck used the same 10,000-update Windows workload, with 512 warmup updates per mode and the actual JSONL writer:

| Mode | P50 ms | P95 ms | P99 ms | Mean producer bytes/update | Mean process bytes/update |
| --- | ---: | ---: | ---: | ---: | ---: |
| Disabled | 0.001100 | 0.001700 | 0.003600 | 1072.12 | 1073.31 |
| Enabled | 0.007900 | 0.021100 | 0.044800 | 44801.57 | 57741.13 |

Additional producer P95 was **0.019400 ms**, below the initial 1 ms target; warmup/batch loss and writer faults were zero. The separate full-channel probe again accepted 64/256 and counted 192 drops exactly. This scope-heavy synthetic workload allocates approximately 43.7 KB more on the producer per update; meeting the timing target does not make those allocations free. Endpoint migration/network/file transfer time is outside this producer benchmark.

Observed release gates on October 8, 2026:

| Check | Observed result |
| --- | --- |
| `dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build --nologo` | 1278 passed, zero failed/skipped |
| Same command with `--filter "FullyQualifiedName~LatencyTelemetry\|FullyQualifiedName~LatencySqliteTransactionTelemetryTests"` | 47 focused telemetry cases passed |
| `dotnet build Adminbot.sln -c Release --nologo` | Success; zero errors, 12 warnings in existing test files |
| `dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false` (fresh external output) | Success; no test project, private Data or telemetry archive in publish |
| EF `migrations has-pending-model-changes` for `UserDbContext` and `CredentialsDbContext` | No pending model changes |
| Isolated `--migration-check`, Windows and native Linux | Both contexts OK, no production database touched |
| WSL `bash scripts/deploy-production.tests.sh` | Actual rsync preservation/exclusion checks passed, no deployment |

Permanent coverage stays in the existing application test project:

- `LatencyTelemetryLifecycleTests.cs`: receiver/inbox/scheduler/handler/HTTP correlation; concurrent bots and real FIFO; restart origins/no replay; late commit publication; exact-once shutdown recovery.
- `LatencyTelemetryReliabilityTests.cs`: fast success, slow Telegram versus slow internal gaps, nested stages, callback ACK versus visible failure/later success, typed SDK transport and healthy long-poll/degradation, real SQLite BUSY retries, bounded full channel, I/O fault/recovery, rotation/retention, restart/shutdown, secrets exclusion, and incident aggregation.
- `LatencySqliteTransactionTelemetryTests.cs`: actual sync/async commit/rollback/disposal lifetimes, no exclusive-stage double-counting, concurrent background categories and global contention in a filtered report.
- `LatencyTelemetryReportTests.cs`: bounded/hostile/truncated archives, accurate bot/session/duplicate/top-twenty/quantile accounting, deadline/polling semantics, missing instrumentation and cancellation.

Obsolete watchdog formatter-spelling assertions were removed, not re-pinned; their live operator-warning, privacy and local-only stage-routing behavior remains covered. No auxiliary production project or telemetry migration is introduced.

## Disable safely and read-only production validation

Set `latencyTelemetry.enabled=false` in persistent configuration and use the normal approved deployment/restart procedure separately. Disabling collects no new events but does not delete existing sensitive files. The existing local latency/watchdog instrumentation, inbox, timeouts, retries and customer behavior remain intact. Do not use public HTTP access for archived telemetry.

After a separately approved deployment, validate without changing production state:

1. Read the service startup log to confirm the resolved directory and enabled state; inspect ownership/modes and file growth as the service owner. Do not print private configuration or token-bearing URLs.
2. Run the published read-only report with `--directory` explicitly; it must not start receivers or workers. Start with `--hours 1 --bot GozargahNetwork_Bot`, then compare all bots over 24 hours.
3. Confirm normal completed updates include correlation, admission/queue/handler/visible-response/final-persistence measurements and the data-quality section reports no sustained loss. Treat restart gaps as explicit unavailable timing.
4. Correlate any new Gozargah polling failure/recovery with received timestamps and process/SQLite samples. Observe contention categories for the notification/recovery workers before claiming historical locks are resolved.
5. Inspect storage cap/rotation over time and bounded incident summaries. If storage/log-channel faults occur, verify customer processing continues and local loss/recovery is visible; do not inject faults into production to test this.

Limitations: user-action-to-Telegram delivery is outside observation; API acknowledgment is not visual receipt/read confirmation; EF reader materialization and some business awaits remain attributable only as gaps; HTTP timing does not separately expose DNS/connect/TLS phase durations on success; SQLite native busy wait is included in operation duration but cannot always be isolated from provider internals; abrupt exit/power loss can lose the last flush interval (no fsync guarantee); directory/file I/O cancellation cannot guarantee preemption of a stuck filesystem syscall; incomplete/lost records limit statistical confidence.
