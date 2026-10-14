# users.db retention and compaction

`AppConfig.DatabaseCleanup` binds the existing section (`Enabled`, `RetentionDays`,
`PayloadRetentionDays`, `InboxRetentionDays`, `BatchSize`, `MaxBatchesPerTable`,
`MaintenanceEnabled`, `InitialDelay`, `Interval`). Missing configuration disables the
additional worker; the existing hourly seven-day inbox maintenance remains enabled.
The supplied 30/2/7-day, 2000-row, 20-batch, ten-minute-startup, daily settings are supported.
`InboxRetentionDays` cannot be shorter than seven days. Bounds are validated before hosting.

## What may expire

- Leftover private payloads of explicitly terminal inbox records (not queued/running/unknown).
- Old completed inbox receipts. Unresolved review, recently resolved review, and any
  receipt referenced by a durable XUI create/renew/link-change operation are protected.
- Old delivered endpoint-alert rows with no claim/lease and an existing permanent
  incident receipt. That permanent dedup key is **never** deleted.
- Endpoint history older than `RetentionDays`, excluding the newest twenty rows per
  exact BotId/BotFather identity and all rows belonging to its current operation.

No generic table-by-age cleaner exists. Wallets, wallet receipts/ledgers, payments,
orders, financial settlement notifications, XUI operation snapshots/locks, referrals,
trial grants, bot state and reminder cycles are not deleted by this worker. Existing
feature-specific retention for site sync, tenant delivery and vanished reminder clients
keeps its own rules. Routine cleanup counts are Debug; failures are sanitized warnings.
Expired temporary records are intentionally deleted, not archived; make a backup first
if those operational records are needed for historical support investigation.

## Preventing growth at ingestion

Owned, tenant and assistant receivers subscribe to `Message` and `CallbackQuery` only.
The business dispatcher has no handler for channel posts, edited channel posts, boosts,
membership bookkeeping or inline queries. Old subscription batches are also filtered
before inbox admission, avoiding pointless serialized payloads, receipts and indexes.
Private forwarded messages, photo/card receipts, admin controls, channel-publication
composition and every supported callback still use their existing durable paths.

## Physical size is different from retained row count

SQLite reuses freed space; ordinary DELETE does not shrink a file using `auto_vacuum=0`.
Erasing an inbox payload can leave mostly empty **allocated** pages, even when
`freelist_count` is small. `dbstat` (when available) measures that fragmentation.
The worker performs only PASSIVE WAL checkpoint and, if already configured for
`auto_vacuum=2`, up to `BatchSize` incremental-vacuum pages per scan. It never forces
full VACUUM, changes durability settings, or manually removes WAL/SHM files.
Long-lived readers can delay WAL reuse; a checkpoint is not permission to delete WAL.

## One-time full compaction (operator-owned, offline)

Confirm the database path from the actual service WorkingDirectory and startup output.
Schedule downtime. Stop **every** process using that database, including backup tools.
Do not copy only users.db while a live WAL contains committed changes. Use SQLite's
backup API / `.backup` into a new, private, unique path and verify it before compaction.
Do not overwrite a previous backup or swap a compacted live snapshot over newer writes.

Example after all consumers are stopped (replace the two explicit paths first):

```sh
sqlite3 /absolute/Data/users.db ".backup '/absolute/private/users-before-cleanup-UNIQUE.db'"
sqlite3 /absolute/private/users-before-cleanup-UNIQUE.db 'PRAGMA integrity_check;'
sqlite3 /absolute/Data/users.db 'PRAGMA integrity_check;'
# Require both integrity checks to return exactly "ok" before continuing.
# Keep enough free space for the backup and up to twice the original DB size for VACUUM.
sqlite3 /absolute/Data/users.db 'PRAGMA auto_vacuum=INCREMENTAL; VACUUM;'
sqlite3 /absolute/Data/users.db 'PRAGMA integrity_check; PRAGMA auto_vacuum;'
# Require "ok" and auto_vacuum=2, verify protected table counts, then restart consumers.
```

Full VACUUM compacts sparse allocated pages without deleting logical rows; enabling
incremental mode allows later bounded free-page reclamation. It does not compact
partially filled pages on every subsequent scan. Never run this full conversion as a
schema migration or automatically during a live bot handler. Do not delete the backup
until the deployment, financial data and recovery flows have been independently checked.

Official references: [SQLite VACUUM](https://www.sqlite.org/lang_vacuum.html),
[auto_vacuum](https://www.sqlite.org/pragma.html#pragma_auto_vacuum),
[WAL checkpointing](https://www.sqlite.org/wal.html#checkpointing).
