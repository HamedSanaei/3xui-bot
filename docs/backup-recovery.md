# Payment backup restart regression

The old dispatcher requested a backup after every successful Payment log delivery. Recovered pending/sending
logs therefore generated fresh backup side effects, including when replay lasted beyond the five-second cap.
The `_backupPending` flag also remained set throughout debounce: requests already covered by the upcoming
snapshot forced a redundant follow-up and continually reset the quiet timestamp.

Payment insertion now increments a global durable Requested generation in the same outbox transaction.
Delivery, retries, and ACK recovery do not increment it. The additive outbox schema bootstraps all historical
Payment rows into one generation only once. No rows are deleted to suppress backup behavior.

The tracked backup worker waits for quiet or the existing maximum delay, captures Requested immediately before
snapshot creation, uploads users.db and credentials.db, and acknowledges that captured generation as Covered.
Requests committed after capture stay pending and collapse into a follow-up. Bot identity does not partition
the watermark; configured backup destination and default sender are authoritative. Conflicts produce a warning
without exposing destination values or credentials.

SQLite online backup replaces raw file copying so each document includes committed WAL pages. The two files
are individually consistent snapshots, not a distributed transaction or a coordinated point-in-time pair.
For disaster recovery requiring cross-database consistency, stop all writers before taking the coordinated pair.

Crash before log delivery: pending backup intent survives independently. Crash after Telegram log acceptance but
before ACK: the log may duplicate; Covered prevents a new backup. Crash after uploading documents but before
Covered commit: one additional coalesced pair may upload on restart. Partial upload failure retains intent and
retries after 30 seconds; an already uploaded document can repeat. Telegram has no atomic two-document upload.

Shutdown rejects new durable enqueue requests, allows pending backup completion for ten seconds, then cancels
and awaits the tracked backup worker for another five seconds. A transport ignoring cancellation retains its
tracked task/resources until process exit. The host remains the dispatcher disposal owner.

Expected recovered startup burst: one users.db document and one credentials.db document (two total), regardless
of the number or duration of recovered Payment log deliveries. If that generation was already covered before
restart, no redundant backup is needed. Genuine new payments still request a fresh snapshot.

Regression coverage includes ten persisted startup payments, long blocked backlog, rapid live and multi-bot
bursts, controlled debounce, upload-time requests, transient log retry, covered ACK uncertainty and a new payment
after recovery. Existing delivery priorities, leases, 429 handling, fairness and dead-letter behavior are unchanged.

Deployment: preserve the outbox database, deploy one polling process, and monitor requested/covered generations,
backupRuns, backupRequestsCoalesced and backupFollowUps. Do not remove the new watermark table during rollback.
No production deployment or Git history rewrite is part of this change.

## Focused logging cleanup

Tenant runtime lifecycle events now use `LogTelegramHtml` (durable `Html`, EventId 1001/TelegramHtml), preserving
HTML, tenant identifiers, escaped errors, ambient bot attribution and the central logger route. Twenty lifecycle
events request zero backup generations; a genuine `LogPayment` still increments Requested once.

The backup worker reads durable state immediately on startup. A capacity-one semaphore wakes it only after
Payment insertion and Requested commit successfully. Idle waits use a ten-second recovery timeout, covering
crash-before-signal and direct durable writes. Debounce also waits on signals or its remaining quiet/max window;
it no longer polls every 25ms. Coalesced/lost/stale signals never replace the SQLite watermark.

Channel precedence is nonblank global `backupChannel`, then nonblank default-owned `BackupChannel`, then persisted
`ChannelId`. Sender precedence is the registry's default-owned id, then persisted `BotId`; no separate global
backup-bot setting currently exists. Blank means null, empty or whitespace. A missing sender or channel sends no
document, preserves pending intent, emits a destination-free warning, and waits ten seconds before retrying even
if producers keep signaling. Existing partial-upload/crash duplicate windows described above remain unchanged.

Focused tests cover lifecycle HTML and twenty-event generation isolation, financial intent, controlled idle
wait/read counts, timeout recovery after direct commit without a signal, startup recovery, all three blank global
channel forms, owned versus persisted destinations, and missing-destination pending/warning/no-send behavior.

Logging classification contract (all call sites audited):
- `LogPayment` (EventId 1000/Payment) = financial audit only: gateway settlement and official confirmation after
  provisional credit (HooshPay, NOWPayments, Tetraminator, UniquePay, Zibal wallet charge, UniquePay tenant
  fulfillment), admin wallet adjustment, and the owned-bot v3 purchase/renewal log. Each one either mutates or
  confirms money/balance/payment state, so requesting a database backup is appropriate.
- `LogTelegramHtml` (EventId 1001/TelegramHtml) = important non-financial operational/security/admin audit:
  tenant lifecycle, admin phone verification, admin role changes, colleague/cooperation requests, XUI link
  changes, account deletion, and XUI operation outcomes. All are committed to the durable outbox and reach the
  private logger channel with HTML formatting, but none increments the backup generation.
- `LogInformation`/ordinary logger = ordinary operational logging, bounded memory-only best-effort behavior.

The former exceptions are fixed: the five non-financial admin/colleague/link/delete call sites were converted
from `LogPayment` to `LogTelegramHtml` with their message content preserved verbatim.

No wallet, settlement, provisioning, update scheduler, inbox, context-lifetime or catalog changes are included.
