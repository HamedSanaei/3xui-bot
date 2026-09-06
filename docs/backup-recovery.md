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
