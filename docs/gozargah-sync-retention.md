# Gozargah sync growth fix

## Cause and behavior

The manual `Sync Gozargah Site` message enters `XuiV3AdminFlowService.HandleGozargahHistoricalSyncAsync`,
reads all configured panel clients and calls `QueueUpdateAsync` once per eligible account. This explains how one
message can produce the supplied 604-account burst. The provided snapshots show 109/119 comparable accounts changed
only tracking_code. Current source used an operation/identity-only succeeded lookup: create history did not suppress
equivalent updates, while a previous update could incorrectly suppress a later real state change.

Queue admission now compares parsed canonical request state with the most recently completed successful account state,
preferring UUID and falling back to email only when UUID is absent. Ownership changes do not change the gate key;
BotId, TenantBotId, owner, buyer, account email, UUID, SubId and subscription link participate in comparison.
All payload fields participate except top-level tracking_code: plan, arranged plan, inbound set, name, UUID, comment,
price, volume, duration/date, username, subscription, trial and bot flags. Object keys and structured comments are sorted
recursively; array order and arbitrary text are preserved. Rename state is compared using its final name. Malformed
historical payloads do not suppress a new event. Historical sync generates tracking identity only when sending is needed.

A per-account gate spans queue decision and send; retries take the same gate before the per-event gate. No global
network lock or SQLite write transaction spans HTTP. Equivalent unresolved requests reuse their event; retry sends also
check successful state before HTTP. Successful and skipped deletes preserve tombstones so an absent website account
cannot be mistaken for an unchanged existing account. The deployment continues to require one polling process.

## Retention and indexes

`GozargahSiteSyncRetentionDays` defaults to 30 and must be positive. Every two-minute retry cycle may delete at most
100 expired terminal rows in one short statement. Pending, failed and unknown nonterminal statuses are never deleted.
The latest successful state per UUID/email is retained indefinitely, plus a later skipped-delete tombstone where needed.
Storage therefore follows distinct account identities plus retained history, rather than repeated unchanged bulk runs.
No wallet, order, ledger or other tables are cleaned. No schema change or migration is needed; WasUnchanged is not mapped.

Index audit against production source queries: Id supports reload/send; Uuid and Email support account state lookup and
retention correlations; Status supports retry/terminal selection. The old Operation/Email/PreviousEmail/Uuid/SubId index
served the replaced coarse dedupe query. BotId, BuyerTelegramUserId, OwnerTelegramUserId, SubId, TelegramUserId and
TenantBotId have no additional direct production outbox query consumers in this inspection. All ten secondary indexes
are retained: no production query plans or external/operator query inventory were supplied to justify their removal.

Bulk completion logs scanned, changed, skippedUnchanged, succeeded, failed, skipped and elapsedMs once. Unchanged
accounts do not create per-account Telegram logs, events or HTTP updates. A fully unchanged repeated run should produce
zero new update rows; this is an expected effect, not a new measured production result.

## Operations and verification

No startup migration behavior, production databases, payment or financial semantics are changed. Deleted pages are
reused by SQLite. Optional physical reclamation: schedule downtime, stop every database writer, take coordinated
backups of both databases, ensure free disk space, then run `sqlite3 users.db 'VACUUM;'` manually and restart normally.
Never run VACUUM as part of startup or periodic cleanup. No production operation was performed for this task.

The user's latest instruction excludes test creation and execution. Existing unrelated working-tree changes, including
test and migration edits, are preserved. Build and model-check results are reported separately in the delivery response.

Verification for this increment: Release solution build passed with zero warnings/errors. Both UserDbContext and
CredentialsDbContext `has-pending-model-changes --configuration Release --no-build` reported no model changes.
Diff whitespace and strict UTF-8 checks passed. No tests were added or run; no publish/deployment/database mutation.
