using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Adminbot.Domain.Logging
{
    /// <summary>Durable status of a Telegram payment or audit log row.</summary>
    /// <remarks>
    /// State lifecycle: <see cref="Pending"/> (eligible or scheduled retry) → <see cref="Sending"/>
    /// (atomically claimed by a worker under a lease) → deleted on successful Telegram acknowledgement,
    /// back to <see cref="Pending"/> after a failed attempt or expired lease, or terminal
    /// <see cref="DeadLetter"/> after a permanent failure threshold. Records are never silently dropped:
    /// every terminal or retry transition is persisted before the send path returns.
    /// </remarks>
    internal enum TelegramLogOutboxStatus
    {
        /// <summary>Ready for delivery or scheduled for a future retry via <c>NextAttemptAtUtc</c>.</summary>
        Pending,
        /// <summary>Atomically claimed by a worker; only expired leases may return it to Pending.</summary>
        Sending,
        /// <summary>Retained for manual inspection after permanent failures exhausted the retry threshold.</summary>
        DeadLetter
    }

    /// <summary>
    /// Durable delivery record persisted before any Telegram I/O happens. Contains identifiers only:
    /// <c>BotId</c> is resolved to a live client at delivery time, so no runtime object survives restarts.
    /// </summary>
    /// <param name="Id">SQLite autoincrement row id; zero for rows not yet inserted.</param>
    /// <param name="CreatedAtUtc">UTC creation time used for dead-letter retention.</param>
    /// <param name="Priority">Delivery priority; Payment ranks above Html. Stored separately so a future
    /// reprioritization never rewrites <see cref="DeliveryKind"/>.</param>
    /// <param name="DeliveryKind">Message representation: Payment (HTML + backup), Html (HTML), or Plain (text).
    /// ParseMode is derived deterministically from this value and is not stored.</param>
    /// <param name="BotId">Internal bot registry id of the sender; resolved to an <c>ITelegramBotClient</c> at
    /// delivery time. Never a token and never a client object.</param>
    /// <param name="LoggerChannelId">Telegram chat id that receives this log.</param>
    /// <param name="BackupChannelId">Telegram chat id that receives database backup documents for Payment rows;
    /// empty for non-Payment rows.</param>
    /// <param name="Message">Bounded, already HTML-safe (for HTML kinds) or plain text (for Plain) message body.</param>
    /// <param name="AttemptCount">Number of completed or in-flight send attempts; incremented atomically at claim.</param>
    /// <param name="NextAttemptAtUtc">UTC time at which the record becomes eligible again. For Sending rows it
    /// doubles as the lease expiry written by the claim.</param>
    /// <param name="LastError">Bounded (max 1000 chars) last failure summary; null when never failed.</param>
    /// <param name="Status">Current row state per <see cref="TelegramLogOutboxStatus"/>.</param>
    /// <param name="LeaseUntilUtc">UTC lease expiry of the active Sending claim; null when not claimed.</param>
    /// <param name="LastAttemptAtUtc">UTC time of the most recent claim or persisted failure; null before the first
    /// attempt.</param>
    internal sealed record TelegramLogOutboxItem(
        long Id,
        DateTime CreatedAtUtc,
        TelegramLogDeliveryKind Priority,
        TelegramLogDeliveryKind DeliveryKind,
        string BotId,
        string LoggerChannelId,
        string BackupChannelId,
        string Message,
        int AttemptCount,
        DateTime NextAttemptAtUtc,
        string LastError,
        TelegramLogOutboxStatus Status,
        DateTime? LeaseUntilUtc,
        DateTime? LastAttemptAtUtc);

    /// <summary>Persists payment and HTML audit logs in a crash-recoverable SQLite outbox.</summary>
    /// <remarks>
    /// The outbox database is the source of truth for durable delivery: producers commit rows here before their
    /// logger call returns, and the dispatcher drains directly from SQLite. The store owns no in-memory queue and
    /// holds no Telegram client, so a process restart cannot lose or forget a committed record.
    ///
    /// Concurrency: WAL journal mode plus a 5-second busy timeout allow concurrent producer inserts and worker
    /// claims. Every public method executes one short reader/writer on a fresh pooled connection; Telegram network
    /// I/O never happens inside any transaction. <see cref="LoadCandidatesAsync"/> performs lease recovery and its
    /// eligibility SELECT inside one transaction, and <see cref="ClaimAsync"/> is a single conditional UPDATE, so
    /// two workers can never claim the same row.
    /// </remarks>
    internal sealed class TelegramLogOutbox : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        /// <summary>
        /// Creates or opens the outbox SQLite database and applies the current schema.
        /// </summary>
        /// <param name="databasePath">
        /// Absolute runtime path such as <c>&lt;contentRoot&gt;/Data/telegram-log-outbox.db</c>. The parent
        /// directory is created when missing; the path is never relative to the shell current directory.
        /// </param>
        /// <remarks>
        /// Initialization sets WAL journal mode, FULL synchronous durability, and a 5-second busy timeout. Legacy
        /// databases created by the development iteration are migrated by adding the missing <c>LeaseUntilUtc</c> and
        /// <c>LastAttemptAtUtc</c> columns; a legacy unused <c>ParseMode</c> column is tolerated and simply no longer
        /// read or written.
        /// </remarks>
        public TelegramLogOutbox(string databasePath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5
            }.ToString();
            Initialize();
        }

        /// <summary>
        /// Commits a durable item before Telegram delivery begins.
        /// </summary>
        /// <param name="item">
        /// Delivery snapshot with <c>Id=0</c> and <c>AttemptCount=0</c>. The <c>Priority</c> and
        /// <c>DeliveryKind</c> values must match the enqueued kind.
        /// </param>
        /// <returns>The SQLite row id assigned to the persisted item.</returns>
        /// <remarks>
        /// This is the durability barrier of the logger pipeline: the caller must not return from the logger
        /// operation until this method completes. The insert is serialized by an in-process gate and runs on a
        /// WAL database, so concurrent producers never see <c>SQLITE_BUSY</c> under normal load.
        /// </remarks>
        public async Task<long> EnqueueAsync(TelegramLogOutboxItem item)
        {
            await _writeGate.WaitAsync();
            try
            {
                await using var connection = Open();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO TelegramLogOutbox
                    (CreatedAtUtc, Priority, DeliveryKind, BotId, LoggerChannelId, BackupChannelId, Message, AttemptCount, NextAttemptAtUtc, LastError, Status)
                    VALUES ($created, $priority, $kind, $bot, $logger, $backup, $message, $attempt, $next, $error, $status);
                    SELECT last_insert_rowid();";
                Bind(command, item);
                return (long)(await command.ExecuteScalarAsync() ?? 0L);
            }
            finally { _writeGate.Release(); }
        }

        /// <summary>
        /// Force-expires every Sending lease, which mirrors what a fresh process must do on startup.
        /// </summary>
        /// <param name="nowUtc">Current UTC time also written as the new <c>NextAttemptAtUtc</c> so reset rows are immediately eligible.</param>
        /// <returns>The number of rows returned to <see cref="TelegramLogOutboxStatus.Pending"/>.</returns>
        /// <remarks>
        /// This host is single-instance, so a newly started process is proof that the previous owner of every
        /// Sending lease is gone (crash, systemctl restart, or reboot). Running this once at startup makes lease
        /// recovery immediate instead of waiting out the lease duration. It is safe even when the previous process
        /// is still alive only if the deployment never runs two instances; the periodic
        /// <see cref="LoadCandidatesAsync"/> lease recovery remains the multi-worker safety net.
        /// </remarks>
        public async Task<int> ResetAllSendingAsync(DateTime nowUtc)
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE TelegramLogOutbox SET Status=$pending, NextAttemptAtUtc=$now, LastError='lease reset at startup' WHERE Status=$sending";
            command.Parameters.AddWithValue("$pending", (int)TelegramLogOutboxStatus.Pending);
            command.Parameters.AddWithValue("$sending", (int)TelegramLogOutboxStatus.Sending);
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            return await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Reclaims expired Sending leases and returns a bounded page of due Pending rows.
        /// </summary>
        /// <param name="nowUtc">Current UTC time used for lease-expiry and eligibility comparisons.</param>
        /// <param name="limit">Maximum row count per page; keeps backlog memory bounded.</param>
        /// <returns>
        /// Due rows ordered by priority (Payment first) then row id. Rows are returned as Pending and are not yet
        /// claimed: the caller must atomically claim each one before sending.
        /// </returns>
        /// <remarks>
        /// Lease recovery and the SELECT run inside one transaction. A Sending row whose
        /// <c>LeaseUntilUtc</c> is in the past is returned to Pending with <c>NextAttemptAtUtc</c> reset to now so
        /// it is immediately eligible again; this is the periodic recovery path that also covers multi-worker
        /// deployments and lost wake-ups. The lease grace period is implicit in the claim duration itself.
        /// </remarks>
        public async Task<IReadOnlyList<TelegramLogOutboxItem>> LoadCandidatesAsync(DateTime nowUtc, int limit = 96)
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            await using (var recover = connection.CreateCommand())
            {
                recover.Transaction = transaction;
                recover.CommandText = @"UPDATE TelegramLogOutbox
                    SET Status=$pending, NextAttemptAtUtc=$now, LastError='lease expired'
                    WHERE Status=$sending AND LeaseUntilUtc IS NOT NULL AND LeaseUntilUtc < $now";
                recover.Parameters.AddWithValue("$pending", (int)TelegramLogOutboxStatus.Pending);
                recover.Parameters.AddWithValue("$sending", (int)TelegramLogOutboxStatus.Sending);
                recover.Parameters.AddWithValue("$now", Format(nowUtc));
                await recover.ExecuteNonQueryAsync();
            }
            var result = new List<TelegramLogOutboxItem>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT Id, CreatedAtUtc, Priority, DeliveryKind, BotId, LoggerChannelId, BackupChannelId, Message, AttemptCount, NextAttemptAtUtc, LastError, Status, LeaseUntilUtc, LastAttemptAtUtc
                    FROM TelegramLogOutbox
                    WHERE Status=$pending AND NextAttemptAtUtc <= $now
                    ORDER BY Priority DESC, Id
                    LIMIT $limit";
                command.Parameters.AddWithValue("$pending", (int)TelegramLogOutboxStatus.Pending);
                command.Parameters.AddWithValue("$now", Format(nowUtc));
                command.Parameters.AddWithValue("$limit", limit);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    result.Add(Read(reader));
            }
            await transaction.CommitAsync();
            return result;
        }

        /// <summary>
        /// Atomically claims one due record with an incremented attempt count and a lease expiry.
        /// </summary>
        /// <param name="id">Durable outbox row id returned by <see cref="EnqueueAsync"/>.</param>
        /// <param name="attempt">New one-based attempt number for this send.</param>
        /// <param name="leaseUntilUtc">UTC lease expiry; the row stays Sending until then even if the process dies.</param>
        /// <param name="nowUtc">Current UTC time recorded as <c>LastAttemptAtUtc</c>.</param>
        /// <returns>
        /// <c>true</c> when this caller won the claim and may send; <c>false</c> when another worker claimed the row
        /// first or the row is no longer eligible.
        /// </returns>
        /// <remarks>
        /// The single conditional UPDATE is the atomic claim: two workers racing for the same id can never both win.
        /// The guard <c>NextAttemptAtUtc &lt;= now</c> additionally prevents claiming a row another worker already
        /// rescheduled into the future (for example after a 429).
        /// </remarks>
        public async Task<bool> ClaimAsync(long id, int attempt, DateTime leaseUntilUtc, DateTime nowUtc)
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE TelegramLogOutbox
                SET Status=$sending, AttemptCount=$attempt, NextAttemptAtUtc=$lease, LeaseUntilUtc=$lease, LastAttemptAtUtc=$now
                WHERE Id=$id AND Status=$pending AND NextAttemptAtUtc <= $now";
            command.Parameters.AddWithValue("$sending", (int)TelegramLogOutboxStatus.Sending);
            command.Parameters.AddWithValue("$pending", (int)TelegramLogOutboxStatus.Pending);
            command.Parameters.AddWithValue("$attempt", attempt);
            command.Parameters.AddWithValue("$lease", Format(leaseUntilUtc));
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync() == 1;
        }

        /// <summary>
        /// Acknowledges successful Telegram delivery by deleting the row, bounding database growth.
        /// </summary>
        /// <param name="id">Outbox row id delivered successfully.</param>
        /// <remarks>
        /// Delivered rows are deleted rather than retained with <c>Status=Delivered</c> because no business reporting
        /// consumes them and the at-least-once guarantee is preserved either way. The known crash window between
        /// Telegram acceptance and this DELETE may produce a duplicate after restart, never a loss. Retention of the
        /// failure/DeadLetter side of the table is handled by <see cref="PurgeDeadLettersAsync"/>.
        /// </remarks>
        public async Task AcknowledgeAsync(long id)
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TelegramLogOutbox WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Persists the outcome of a failed attempt: a retry schedule or a terminal DeadLetter state.
        /// </summary>
        /// <param name="id">Outbox row id that failed.</param>
        /// <param name="nextAttemptUtc">UTC time the row becomes eligible again; for DeadLetter rows this is ignored.</param>
        /// <param name="error">Bounded error summary (truncated to 1000 characters), never containing secrets.</param>
        /// <param name="nowUtc">Current UTC time recorded as <c>LastAttemptAtUtc</c>.</param>
        /// <param name="deadLetter">
        /// <c>true</c> moves the row to <see cref="TelegramLogOutboxStatus.DeadLetter"/> (retained for inspection,
        /// never retried, never deleted implicitly); <c>false</c> returns it to Pending with the retry time.
        /// </param>
        /// <remarks>
        /// This is the durable 429/transient/permanent failure barrier: the row must never remain Sending after a
        /// failed attempt, otherwise an idle SQLite row would wait for lease expiry instead of its real retry time.
        /// The UPDATE is guarded on <c>Status=Sending</c> so a stale worker can never overwrite a newer state.
        /// </remarks>
        public async Task FailAsync(long id, DateTime nextAttemptUtc, string error, DateTime nowUtc, bool deadLetter)
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE TelegramLogOutbox
                SET Status=$status, NextAttemptAtUtc=$next, LastError=$error, LastAttemptAtUtc=$now
                WHERE Id=$id AND Status=$sending";
            command.Parameters.AddWithValue("$status", (int)(deadLetter ? TelegramLogOutboxStatus.DeadLetter : TelegramLogOutboxStatus.Pending));
            command.Parameters.AddWithValue("$sending", (int)TelegramLogOutboxStatus.Sending);
            command.Parameters.AddWithValue("$next", Format(nextAttemptUtc));
            command.Parameters.AddWithValue("$error", string.IsNullOrEmpty(error) ? string.Empty : (error.Length > 1000 ? error[..1000] : error));
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Reads live Pending and DeadLetter row counts for backlog warnings and statistics.
        /// </summary>
        /// <returns>
        /// A tuple with <c>Pending</c> = not-yet-delivered scheduled rows and <c>DeadLetter</c> = retained permanent
        /// failures. Both counts are small integers; the table is tiny because delivered rows are deleted.
        /// </returns>
        public async Task<(int Pending, int DeadLetter)> GetCountsAsync()
        {
            await using var connection = Open();
            await connection.OpenAsync();
            var pending = await ScalarCountAsync(connection, (int)TelegramLogOutboxStatus.Pending);
            var deadLetter = await ScalarCountAsync(connection, (int)TelegramLogOutboxStatus.DeadLetter);
            return (pending, deadLetter);
        }

        /// <summary>
        /// Deletes DeadLetter rows older than the retention window during periodic maintenance.
        /// </summary>
        /// <param name="cutoffUtc">UTC cutoff; rows with <c>CreatedAtUtc &lt; cutoff</c> are removed.</param>
        /// <returns>The number of purged rows.</returns>
        /// <remarks>
        /// DeadLetter evidence stays inspectable for <see cref="TelegramRateLimitPolicy.DeadLetterRetention"/>
        /// (90 days) and is then discarded so a permanently failing channel cannot grow the database forever.
        /// </remarks>
        public async Task<int> PurgeDeadLettersAsync(DateTime cutoffUtc)
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TelegramLogOutbox WHERE Status=$dead AND CreatedAtUtc < $cutoff";
            command.Parameters.AddWithValue("$dead", (int)TelegramLogOutboxStatus.DeadLetter);
            command.Parameters.AddWithValue("$cutoff", Format(cutoffUtc));
            return await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Runs lightweight SQLite maintenance: WAL truncation checkpoint and statistics refresh.
        /// </summary>
        /// <remarks>
        /// Called at most every 10 minutes from the dispatcher. WAL checkpoints shrink the WAL file without a
        /// VACUUM; <c>PRAGMA optimize</c> refreshes internal statistics. A busy result is ignored because the next
        /// maintenance pass retries. This deliberately never issues a per-message VACUUM.
        /// </remarks>
        public async Task CheckpointAsync()
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync();
            await using var optimize = connection.CreateCommand();
            optimize.CommandText = "PRAGMA optimize";
            await optimize.ExecuteNonQueryAsync();
        }

        private static async Task<int> ScalarCountAsync(SqliteConnection connection, int status)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM TelegramLogOutbox WHERE Status=$status";
            command.Parameters.AddWithValue("$status", status);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        private void Initialize()
        {
            using var connection = Open();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS TelegramLogOutbox (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAtUtc TEXT NOT NULL,
                    Priority INTEGER NOT NULL,
                    DeliveryKind INTEGER NOT NULL,
                    BotId TEXT NOT NULL,
                    LoggerChannelId TEXT NOT NULL,
                    BackupChannelId TEXT NOT NULL,
                    Message TEXT NOT NULL,
                    AttemptCount INTEGER NOT NULL,
                    NextAttemptAtUtc TEXT NOT NULL,
                    LastError TEXT NULL,
                    Status INTEGER NOT NULL,
                    LeaseUntilUtc TEXT NULL,
                    LastAttemptAtUtc TEXT NULL);
                CREATE INDEX IF NOT EXISTS IX_TelegramLogOutbox_Due ON TelegramLogOutbox(Status, NextAttemptAtUtc, Priority, Id);";
            command.ExecuteNonQuery();
            EnsureColumn(connection, "LeaseUntilUtc");
            EnsureColumn(connection, "LastAttemptAtUtc");
        }

        private static void EnsureColumn(SqliteConnection connection, string columnName)
        {
            // Migrates development-iteration databases that predate the lease columns. ALTER is guarded by
            // PRAGMA table_info so it runs at most once per missing column.
            var exists = false;
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(TelegramLogOutbox)";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }
            if (exists)
                return;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE TelegramLogOutbox ADD COLUMN {columnName} TEXT NULL";
            alter.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            // Per-connection durability settings: SQLite pool connections start with the database's WAL mode but
            // their synchronous level is a connection property (WAL defaults to NORMAL), so FULL + busy timeout are
            // reapplied on every opened connection instead of only during Initialize.
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        private static string Format(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

        private static void Bind(SqliteCommand command, TelegramLogOutboxItem item)
        {
            command.Parameters.AddWithValue("$created", Format(item.CreatedAtUtc));
            command.Parameters.AddWithValue("$priority", (int)item.Priority);
            command.Parameters.AddWithValue("$kind", (int)item.DeliveryKind);
            command.Parameters.AddWithValue("$bot", item.BotId ?? string.Empty);
            command.Parameters.AddWithValue("$logger", item.LoggerChannelId ?? string.Empty);
            command.Parameters.AddWithValue("$backup", item.BackupChannelId ?? string.Empty);
            command.Parameters.AddWithValue("$message", item.Message ?? string.Empty);
            command.Parameters.AddWithValue("$attempt", item.AttemptCount);
            command.Parameters.AddWithValue("$next", Format(item.NextAttemptAtUtc));
            command.Parameters.AddWithValue("$error", (object)item.LastError ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", (int)item.Status);
        }

        private static DateTime ReadDate(SqliteDataReader reader, int ordinal) =>
            DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        private static TelegramLogOutboxItem Read(SqliteDataReader reader)
        {
            var leaseOrdinal = 12;
            var lastAttemptOrdinal = 13;
            return new TelegramLogOutboxItem(
                reader.GetInt64(0),
                ReadDate(reader, 1),
                (TelegramLogDeliveryKind)reader.GetInt32(2),
                (TelegramLogDeliveryKind)reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                ReadDate(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                (TelegramLogOutboxStatus)reader.GetInt32(11),
                reader.IsDBNull(leaseOrdinal) ? (DateTime?)null : ReadDate(reader, leaseOrdinal),
                reader.IsDBNull(lastAttemptOrdinal) ? (DateTime?)null : ReadDate(reader, lastAttemptOrdinal));
        }

        /// <summary>Releases the in-process write gate used by <see cref="EnqueueAsync"/>.</summary>
        public ValueTask DisposeAsync()
        {
            _writeGate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}