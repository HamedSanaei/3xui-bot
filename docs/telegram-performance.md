# Telegram delivery and admission

## Runtime boundaries

The polling callback records reception, offers a blank callback acknowledgement to the realtime lane, and commits
the update to `TelegramUpdateInbox`. It does not await XUI, payment settlement, or customer business execution.
`AdmissionMs` measures this boundary. `TotalHandlerMs` measures the separate background business execution; it is
not interchangeable with receiver latency. A ten-second panel call can still make background execution take ten
seconds without keeping the receiver in that business call.

Existing bot/user FIFO ordering, financial operations, owner-wallet coordination and XUI recovery remain in the
durable update scheduler. Its global execution limit remains `telegramUpdateMaxConcurrency` (16 by default).
Each bot can occupy at most `Performance:telegram:workerCount` business slots (4 by default). Saturated and disabled
bots are filtered before loading the bounded ready window, so their backlog does not hide eligible other bots.

`TelegramSenderService` resolves runtime clients only when executing. Each bot has one active ordinary output job.
Ready bots rotate; only eligible heads enter the bounded `TelegramWorkQueue` channel. Existing financial workers
use Critical priority, interactions use Normal, and the operator log dispatcher uses Low. FIFO applies within a
bot/priority; higher priority may overtake queued lower priority, but never an active send. Delayed 429 jobs retain
their position without occupying a worker. No database transaction spans network I/O.

Text, text edits, keyboard/caption edits and deletes persist private request JSON in `TelegramDeliveryJobs`.
`TelegramQueuedDelivery.EnqueueAsync` explicitly returns after persistence for selected home menus, admin/status
panels, owner store lists, shared account navigation edits and download menus. It must never enclose a business
workflow or a call whose returned message id is used. Other callers still await a real Telegram response through
the sender. In particular, receipt routing, payment delivery and account/renewal results retain their existing
durable workflow and outbox boundaries; no fabricated message id is supplied.

Photos, albums and documents retain live streams while their original caller awaits actual delivery. Only scheduling
metadata is stored for these jobs. Cancellation before execution releases the caller immediately; cancellation after
execution starts waits for HTTP to release stream ownership. Restart never replays a lost live stream or a request
whose original caller needed a response. Existing financial notification records own that recovery.

## Configuration and overload

Optional configuration (shown in `Data/configuration.example.json`):

```json
"Performance": {
  "telegram": {
    "workerCount": 4,
    "sendTimeoutSeconds": 5,
    "queueSize": 1000,
    "callbackAckImmediately": true
  }
}
```

Startup validates workers 1..64, timeout 1..30 seconds and queue size 1..10000. `queueSize` bounds output memory;
`telegramUpdateQueueCapacity` bounds the input ready-read window. Neither is now a hard durable backlog limit.
Overflow remains in SQLite rather than blocking polling on worker capacity or dropping financial inputs. Database
commit latency and database outages can still delay/reject admission: nothing is acknowledged as saved before commit.
One Persian pressure notice per bot/chat/minute is eligible only after a successful input commit, and is itself
queued. It cannot bypass a slow Telegram send. Disk space and queue age require monitoring during sustained overload.

Global send pacing is 25 requests/second including the realtime lane. Text/edit attempts have the configured timeout;
stream-backed media attempts have 60 seconds, downloads 120 seconds, metadata calls 30 seconds and polling 90 seconds.
429 text responses get at most three attempts, honoring RetryAfter and exponential backoff. Live stream-backed 429
responses return to their existing caller's delivery policy; the sender never retries consumed streams.
Transport timeouts, ambiguous 5xx responses and process interruption become uncertain and are not replayed by the sender.
A caller's own deadline or lane token is not one of those cases: the worker owns the attempt, so a caller that stops waiting
after durable admission returns without a delivery failure, the in-flight snapshot send still completes, and a cancellation
that happened before the HTTP call leaves the job queued for a first attempt. Uncertainty is therefore reserved for an
attempt whose call had started and whose answer was never seen; the attributing source is the closed vocabulary in
`Domain/TelegramCancellationSources.cs`, exported as `telegram_delivery_cancellation_total{source}` together with
`enqueue_wait_ms`, `worker_start_delay_ms` and `telegram_api_ms`. A failed terminal status write no longer relabels a
confirmed send as uncertain.
Owned settlement and tenant receipt notification workers retain this uncertainty; the Sales Assistant does not send
a text fallback after an ambiguous receipt-photo result. Existing operator-log outbox delivery remains at-least-once
under its separate recovery policy; duplicate audit messages do not replay a financial operation.

Only `AnswerCallbackQuery` bypasses normal per-bot output ordering. Its independent bounded lane makes one-second
attempts; stale blank acknowledgements may be skipped. When a handler later produces authorization/error alert text
after early acknowledgement, the text is preserved as a normal queued chat message when the chat identity is known.
The immediate blank acknowledgement says nothing about business acceptance or success. Under full realtime capacity
acknowledgement is best effort; the financial request is still retained by the durable input path.

These are resource limits, not measured latency guarantees. One hundred acknowledgements cannot all complete within
one second at a global 25/second rate, and no network deadline guarantees delivery during a Telegram outage. Cached
navigation avoids selected send/read waits; fresh validation, phone/mandatory-join checks and result-dependent
background business flows may exceed 100 or 500 milliseconds. No claim is made that every background handler meets
those targets. Per-bot isolation prevents a single bot taking all normal slots at default settings; simultaneous
slow calls from four different bots can still occupy all four output workers.

## Storage, recovery and database audit

- Additive migration `20260915221456_TelegramDeliveryQueue` creates an empty output table and a
  `(Status, BotId, Priority, Id)` index. No historical payment or balance is replayed or modified.
- `sending` at restart becomes `uncertain`. Unstarted rows are only discarded when another component owns their recovery:
  critical claims the financial outboxes re-send, and live stream jobs whose caller released its streams. A queued
  normal-priority reply whose caller disappeared is delivered, because nothing else would deliver it. Queued
  admission-only UI resumes. Terminal payloads are immediately cleared. Sent/failed metadata is pruned in bounded batches
  after seven days; uncertain metadata is retained for operator review.
- The sender is registered before business workers and receivers, so it stops after them. Existing input/business
  shutdown drains first; output then gets up to twenty seconds within the remaining host deadline. Queued output
  remains durable; active interrupted output is uncertain.
- Both database factories use 64-context pools with exclusively leased trackers. The legacy parameterless users.db
  constructor is internal; short-lived compatibility factories do not create a pool per call. WAL, five-second SQLite
  busy timeouts and three-attempt BUSY/LOCKED-only local retries remain in place.
- Existing profile primary keys and bot/user state keys cover Telegram user lookup. Payment/order tables already
  index user, bot and order identities; website sync has email indexes and panel operation tables have client/normalized
  email composite indexes. No redundant universal `ClientEmail` column/index was invented: display clients live on XUI.
- Reviewed tenant receipt persistence intentionally saves twice inside **one short transaction**: the first obtains the
  receipt id, the second links the order and notification. It is not a per-row commit loop. Core XUI flow services do
  not directly call SaveChanges in customer loops. Existing financial/saga commits remain separate across external
  effects; combining them into a transaction around XUI would be incorrect.

`XuiClientCache` holds immutable display JSON for ten seconds, bounded to 16 MiB. Readers deserialize independent
objects. Its key includes panel URL/root, exact query and hashed authorization identity. Caching is explicitly opt-in
(the account-list display uses it); renewal eligibility, account ownership/action validation, creation read-back,
financial decisions and recovery remain fresh. JSON mutations invalidate before and after HTTP, including failures;
a generation check prevents a pre-mutation read repopulating the cache. Existing transport retry modes are unchanged.
Other fresh client/inbound/subscription reads were intentionally not broadly cached without proving their call-site
role. Concurrent cache misses may still issue independent reads.

## Diagnostics, verification and rollout

Local structured diagnostics include receive UTC time, bot/update/user correlation, admission duration, DB/XUI/enqueue/
business durations, output job id, queue wait, send duration, active bots and configured limits. Nested stage durations
can overlap and must not be summed as independent elapsed time. Sender telemetry is excluded from the Telegram logger
itself to prevent recursive output-log amplification. Payloads, callback data and credentials are never logged by this
infrastructure. Monitor durable depth/oldest age, uncertain output, financial reconciliation and `sqlite.busy.retries`.

Verification for this change: Release compilation and source/diff review only. No new tests, full test suite, load
scenario, live Telegram/XUI call, migration execution or deployment was performed. Earlier test results predate this
patch. The requested 100-user and slow-provider latency acceptance scenarios remain unmeasured.
The final `dotnet build Adminbot.csproj -c Release --no-restore -v quiet` succeeded with zero errors and zero warnings.
The solution also compiled during implementation with seven warnings in unchanged test sources; tests were not run.

Source review compared the UTF-8/non-ASCII character sequences and word diffs for the changed existing message files:
`ApiServicev3`, `BotRuntimeServices`, `ClientDownloadFlow`, `TelegramBotService`, `TenantBotService`,
`XuiV3BotFlowService` and `SalesAssistantService`. Their Persian/emoji strings are unchanged. The new sender's pressure
notice matches the requested text. Existing question-mark placeholders in `TenantBotService` were already present and were
left unchanged; they still warrant a separate human review of those historical messages. No new mojibake was introduced.

Before deployment, stop the single polling process and take a coordinated backup of `users.db` and `credentials.db`
(including correct SQLite/WAL backup handling; do not copy only active database main files). Preserve the existing
Telegram log outbox backup too. Run the normal migration/preflight deployment gate before any receiver starts. Review
new output uncertainty and historical financial outboxes before any rollback; do not drop pending jobs or blindly
replay uncertain deliveries. No commit, push, publish or deployment is part of this change.
