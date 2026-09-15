using System.Diagnostics;
using System.Linq.Expressions;
using Adminbot.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Result of the retention work performed for one database table during a single cleanup pass.
/// </summary>
/// <param name="Table">Exact database table name the counters belong to.</param>
/// <param name="DeletedRows">Number of terminal rows physically removed in this pass.</param>
/// <param name="CompactedRows">Number of rows whose large JSON payload columns were replaced with null.</param>
/// <param name="Failure">
/// Safe failure label when the table step could not complete, or null on success. The value is a fixed category such
/// as <c>sqlite_error</c> or <c>cleanup_failed</c>; it never contains SQL text, a row payload, or provider data.
/// </param>
/// <remarks>
/// A failed table never aborts the remaining tables. Every counter is exact for committed statements only, so a
/// rolled-back or busy batch reports zero rows rather than a partial count.
/// </remarks>
public sealed record DatabaseCleanupTableResult(string Table, int DeletedRows, int CompactedRows, string Failure);

/// <summary>
/// Outcome of one complete retention and maintenance pass over the application databases.
/// </summary>
/// <param name="DeletedRows">Total terminal rows removed across every table.</param>
/// <param name="CompactedRows">Total rows whose JSON payload columns were cleared across every table.</param>
/// <param name="UsersDatabaseBytesBefore">Total users.db footprint in bytes before the pass, including WAL and shm.</param>
/// <param name="UsersDatabaseBytesAfter">Total users.db footprint in bytes after the pass and any maintenance.</param>
/// <param name="CredentialsDatabaseBytesBefore">Total credentials.db footprint in bytes before the pass.</param>
/// <param name="CredentialsDatabaseBytesAfter">Total credentials.db footprint in bytes after the pass.</param>
/// <param name="MaintenanceRan">
/// True when <c>VACUUM</c> and <c>ANALYZE</c> actually completed on users.db during this pass.
/// </param>
/// <param name="Tables">Per-table counters in a stable order.</param>
/// <remarks>
/// <see cref="UsersDatabaseBytesAfter" /> may be greater than <see cref="UsersDatabaseBytesBefore" />, for example
/// when a busy database refused the maintenance step and the write-ahead log grew during the pass. The report is
/// diagnostic data for the operator and never a settlement or accounting record.
/// </remarks>
public sealed record DatabaseCleanupReport(
    int DeletedRows,
    int CompactedRows,
    long UsersDatabaseBytesBefore,
    long UsersDatabaseBytesAfter,
    long CredentialsDatabaseBytesBefore,
    long CredentialsDatabaseBytesAfter,
    bool MaintenanceRan,
    IReadOnlyList<DatabaseCleanupTableResult> Tables);

/// <summary>
/// Performs the retention, payload compaction, and file maintenance that keep the application SQLite databases
/// proportional to the "live" workload instead of to all history ever recorded.
/// </summary>
/// <remarks>
/// <para>
/// The runner is a singleton and is safe to call repeatedly; every statement is idempotent, so two passes in a row
/// delete nothing the second time. It is deliberately separated from the hosted service that schedules it, so the
/// retention policy can be exercised directly against a temporary database in tests without starting a host, a
/// listener, or a Telegram client.
/// </para>
/// <para>
/// Two invariants govern every statement:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// Only terminal rows are candidates. A queued Telegram update, a reserved or ambiguous panel operation, a renewal
/// that is still settling, a link change awaiting confirmation or manual review, a retryable website outbox event,
/// and a payment row whose money has not reached the customer are all excluded at any age.
/// </description>
/// </item>
/// <item>
/// <description>
/// Deletions and compactions are bounded per batch and per table per pass, so a large backlog is drained over
/// consecutive passes rather than in one long write lock that would stall Telegram handlers.
/// </description>
/// </item>
/// </list>
/// <para>
/// Row deletion and payload compaction use different windows: rows are audit evidence and live for
/// <see cref="DatabaseCleanupOptions.RetentionDays" />, while the bulky JSON copies inside them are only reachable
/// while a recovery path can still read them and are cleared after
/// <see cref="DatabaseCleanupOptions.PayloadRetentionDays" />.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var report = await runner.RunOnceAsync(DateTime.UtcNow, siteCompactor, stoppingToken);
/// logger.LogInformation("Deleted {DeletedRows} rows", report.DeletedRows);
/// </code>
/// </example>
public sealed class DatabaseCleanupRunner
{
    /// <summary>Safe failure label recorded when a table step throws an unexpected database error.</summary>
    private const string SqliteFailure = "sqlite_error";

    /// <summary>Safe failure label recorded when the optional website outbox compactor threw.</summary>
    private const string OutboxFailure = "outbox_failed";

    /// <summary>Safe failure label recorded when a table step threw an unexpected database error.</summary>
    private const string CleanupFailure = "cleanup_failed";

    private readonly UserDbContextFactory _users;
    private readonly TelegramUpdateInboxStore _inbox;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseCleanupRunner> _logger;

    /// <summary>
    /// Creates the retention runner.
    /// </summary>
    /// <param name="users">
    /// Factory for independent short-lived <c>users.db</c> contexts. The runner never holds a context across an await
    /// boundary that performs external work, and it disposes each context before file maintenance starts.
    /// </param>
    /// <param name="inbox">
    /// Inbox store that owns the Telegram receipt retention rule. Reusing it keeps one deduplication window for
    /// receiver redelivery and cleanup instead of two competing definitions.
    /// </param>
    /// <param name="configuration">
    /// Application configuration read at the start of every pass, so an operator can raise or lower the retention
    /// window without introducing a second source of truth.
    /// </param>
    /// <param name="logger">Structured logger for retention counters, size metrics, and deferred maintenance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null.</exception>
    public DatabaseCleanupRunner(
        UserDbContextFactory users,
        TelegramUpdateInboxStore inbox,
        IConfiguration configuration,
        ILogger<DatabaseCleanupRunner> logger)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one complete retention and maintenance pass over users.db and reports the size change.
    /// </summary>
    /// <param name="nowUtc">
    /// Current UTC instant used as the reference for every retention window. A non-UTC value is converted, and the
    /// parameter exists so tests can evaluate windows deterministically without touching the system clock.
    /// </param>
    /// <param name="siteCompactor">
    /// Optional compactor for the Gozargah website outbox. It is supplied by the hosted service from a per-pass
    /// dependency-injection scope because the outbox service is scoped. Pass null to skip that table, for example in a
    /// test that does not exercise website synchronization.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for host shutdown; a cancelled pass stops between tables.</param>
    /// <returns>
    /// A report with exact deleted and compacted row totals, before and after file sizes for both application
    /// databases, and the per-table counters. The returned report is diagnostic and never null.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured retention windows are invalid, which is the same failure the host raises at startup.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Order of work: validate configuration, honor the enable switch, measure sizes, run every table step (each
    /// isolated so one failure cannot stop the others), then run <c>VACUUM</c> and <c>ANALYZE</c> on users.db only when
    /// rows actually changed, and finally measure sizes again.
    /// </para>
    /// <para>
    /// When <c>DatabaseCleanup:Enabled</c> is false the method returns immediately with zero counters and the current
    /// file sizes. The switch is checked here rather than only in the scheduler so no caller can bypass it.
    /// </para>
    /// <para>
    /// VACUUM is skipped when the pass changed nothing, because it rewrites the whole file and takes an exclusive
    /// lock; paying that cost on a no-op day would be pure overhead. It runs after every Entity Framework context has
    /// been disposed, which is what allows it to take the exclusive lock at all.
    /// </para>
    /// <para>
    /// Side effects: deletes terminal rows, clears large JSON payload columns on terminal rows, rewrites users.db, and
    /// emits retention diagnostics. It never writes to credentials.db, never mutates a wallet, ledger, order, or
    /// panel state, and never performs a network call.
    /// </para>
    /// <example>
    /// <code>
    /// using var scope = scopeFactory.CreateScope();
    /// var report = await runner.RunOnceAsync(
    ///     DateTime.UtcNow,
    ///     scope.ServiceProvider.GetService&lt;GozargahSiteSyncService&gt;(),
    ///     stoppingToken);
    /// </code>
    /// </example>
    public async Task<DatabaseCleanupReport> RunOnceAsync(
        DateTime nowUtc,
        ITerminalOutboxCompactor siteCompactor,
        CancellationToken cancellationToken)
    {
        var options = DatabaseCleanupOptions.FromConfiguration(_configuration);
        var now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        var cutoff = now.AddDays(-options.RetentionDays);
        var payloadCutoff = now.AddDays(-options.PayloadRetentionDays);

        var stoppedWatch = Stopwatch.StartNew();
        var usersPath = ResolveDatabasePath(_configuration.Get<AppConfig>()?.UserDatabasePath);
        var credentialsPath = ResolveDatabasePath(_configuration.Get<AppConfig>()?.CredentialsDatabasePath);
        var usersBefore = SqliteDatabaseMaintenance.Measure(usersPath);
        var credentialsBefore = SqliteDatabaseMaintenance.Measure(credentialsPath);

        // The switch is honored here as well as in the hosted service so a direct caller (tests, a future manual
        // trigger, or an operator command) can never bypass it and delete rows while cleanup is parked.
        if (!options.Enabled)
        {
            _logger.LogInformation("Database cleanup is disabled by configuration. No retention work will run.");
            return new DatabaseCleanupReport(
                0,
                0,
                usersBefore.TotalBytes,
                usersBefore.TotalBytes,
                credentialsBefore.TotalBytes,
                credentialsBefore.TotalBytes,
                false,
                Array.Empty<DatabaseCleanupTableResult>());
        }

        _logger.LogInformation(
            "Database cleanup started. retentionDays={RetentionDays}, payloadRetentionDays={PayloadRetentionDays}, inboxRetentionDays={InboxRetentionDays}, usersBytes={UsersBytes}, credentialsBytes={CredentialsBytes}",
            options.RetentionDays,
            options.PayloadRetentionDays,
            options.InboxRetentionDays,
            usersBefore.TotalBytes,
            credentialsBefore.TotalBytes);

        var results = new List<DatabaseCleanupTableResult>
        {
            await RunTableAsync("TelegramUpdateInbox", CleanupFailure, () => PruneInboxAsync(options, now, cancellationToken), cancellationToken),
            await RunTableAsync("GozargahSiteSyncEvents", OutboxFailure, () => PruneSiteOutboxAsync(options, payloadCutoff, siteCompactor, cancellationToken), cancellationToken),
            await RunTableAsync("XuiV3CreationOperations", CleanupFailure, () => PruneCreationOperationsAsync(options, cutoff, payloadCutoff, cancellationToken), cancellationToken),
            await RunTableAsync("XuiV3RenewalOperations", CleanupFailure, () => CompactRenewalOperationsAsync(options, payloadCutoff, cancellationToken), cancellationToken),
            await RunTableAsync("XuiV3LinkChangeOperations", CleanupFailure, () => PruneLinkChangeOperationsAsync(options, cutoff, payloadCutoff, cancellationToken), cancellationToken),
            await RunTableAsync("HooshPayPaymentInfos", CleanupFailure, () => CompactHooshPayAsync(options, cutoff, cancellationToken), cancellationToken),
            await RunTableAsync("SwapinoPaymentInfos", CleanupFailure, () => CompactSwapinoAsync(options, cutoff, cancellationToken), cancellationToken),
            await RunTableAsync("UniquePayPaymentInfos", CleanupFailure, () => CompactUniquePayAsync(options, cutoff, cancellationToken), cancellationToken),
            await RunTableAsync("TetraminatorPaymentInfos", CleanupFailure, () => CompactTetraminatorAsync(options, cutoff, cancellationToken), cancellationToken)
        };

        var deleted = results.Sum(result => result.DeletedRows);
        var compacted = results.Sum(result => result.CompactedRows);

        // File maintenance only matters when the pass actually freed pages. Running it on a no-op pass would take an
        // exclusive lock on users.db and rewrite the file for nothing.
        var maintenanceRan = false;
        if (options.MaintenanceEnabled && deleted + compacted > 0)
        {
            maintenanceRan = await SqliteDatabaseMaintenance.VacuumAndAnalyzeAsync(usersPath, cancellationToken);
            if (!maintenanceRan)
                _logger.LogWarning(
                    "Database cleanup maintenance deferred. database={Database}, reason=busy_or_unavailable",
                    "users.db");
        }

        var usersAfter = SqliteDatabaseMaintenance.Measure(usersPath);
        var credentialsAfter = SqliteDatabaseMaintenance.Measure(credentialsPath);
        stoppedWatch.Stop();

        foreach (var result in results.Where(result => result.DeletedRows > 0 || result.CompactedRows > 0))
        {
            _logger.LogInformation(
                "Database cleanup applied retention. table={Table}, deletedRows={DeletedRows}, compactedRows={CompactedRows}",
                result.Table,
                result.DeletedRows,
                result.CompactedRows);
        }

        foreach (var result in results.Where(result => result.Failure != null))
            _logger.LogError(
                "Database cleanup table step failed and was skipped. table={Table}, failure={Failure}",
                result.Table,
                result.Failure);

        _logger.LogInformation(
            "Database cleanup finished. deletedRows={DeletedRows}, compactedRows={CompactedRows}, usersBytesBefore={UsersBytesBefore}, usersBytesAfter={UsersBytesAfter}, usersBytesFreed={UsersBytesFreed}, credentialsBytesBefore={CredentialsBytesBefore}, credentialsBytesAfter={CredentialsBytesAfter}, maintenanceRan={MaintenanceRan}, elapsedMs={ElapsedMs}",
            deleted,
            compacted,
            usersBefore.TotalBytes,
            usersAfter.TotalBytes,
            usersBefore.TotalBytes - usersAfter.TotalBytes,
            credentialsBefore.TotalBytes,
            credentialsAfter.TotalBytes,
            maintenanceRan,
            stoppedWatch.ElapsedMilliseconds);

        return new DatabaseCleanupReport(
            deleted,
            compacted,
            usersBefore.TotalBytes,
            usersAfter.TotalBytes,
            credentialsBefore.TotalBytes,
            credentialsAfter.TotalBytes,
            maintenanceRan,
            results);
    }

    /// <summary>
    /// Deletes terminal Telegram update receipts older than the configured inbox retention window.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="nowUtc">UTC reference instant for the inbox window.</param>
    /// <param name="token">Cancellation token for the batched delete.</param>
    /// <returns>Deleted and compacted counters for the inbox table; compaction is always zero because payloads are already erased at completion.</returns>
    /// <remarks>
    /// Queued, running, and uncertain receipts are excluded by the inbox store's own predicate, so a message that is
    /// still being executed or is waiting for operator review can never be removed here.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> PruneInboxAsync(DatabaseCleanupOptions options, DateTime nowUtc, CancellationToken token)
    {
        var deleted = await _inbox.PruneAsync(options.InboxRetentionDays, nowUtc, options.BatchSize, token);
        return (deleted, 0);
    }

    /// <summary>
    /// Compacts and removes terminal rows of the Gozargah website outbox.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="payloadCutoff">UTC instant before which stored request and response JSON is cleared.</param>
    /// <param name="compactor">Scoped website outbox compactor, or null when website sync is not exercised.</param>
    /// <param name="token">Cancellation token for the batched statements.</param>
    /// <returns>Deleted and compacted counters for the website outbox table.</returns>
    /// <remarks>
    /// Deletion reuses the outbox service's own compaction rule, which retains each account's latest successful state
    /// as a tombstone so a returning account is never re-created on the website. Payload trimming targets only
    /// <c>succeeded</c> and <c>skipped</c> rows: a <c>pending</c> or <c>failed</c> row still needs its request JSON to
    /// be retried.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> PruneSiteOutboxAsync(
        DatabaseCleanupOptions options,
        DateTime payloadCutoff,
        ITerminalOutboxCompactor compactor,
        CancellationToken token)
    {
        if (compactor == null)
            return (0, 0);

        var deleted = 0;
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            // The outbox compactor is itself bounded per call, so the loop drains a backlog over several batches
            // instead of holding one long write transaction.
            var removed = await compactor.CompactTerminalEventsAsync(token);
            deleted += removed;
            if (removed == 0)
                break;
        }

        var compacted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.GozargahSiteSyncEvents
                .Where(row => (row.Status == GozargahSiteSyncStatuses.Succeeded || row.Status == GozargahSiteSyncStatuses.Skipped)
                    && (row.UpdatedAtUtc ?? row.CreatedAtUtc) < payloadCutoff
                    && (row.RequestJson != null || row.ResponseJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.GozargahSiteSyncEvents
                .Where(BuildKeyPredicate<GozargahSiteSyncEvent, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.RequestJson, (string)null).SetProperty(row => row.ResponseJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (deleted, compacted);
    }

    /// <summary>
    /// Compacts and removes terminal XUI v3 account-creation reservations.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="cutoff">UTC instant before which terminal reservations are deleted.</param>
    /// <param name="payloadCutoff">UTC instant before which stored client JSON is cleared.</param>
    /// <param name="token">Cancellation token for the batched statements.</param>
    /// <returns>Deleted and compacted counters for the creation-operation table.</returns>
    /// <remarks>
    /// Only <c>applied</c> and <c>definitively rejected</c> rows qualify. A reserved, post-started, or ambiguous row is
    /// either an active claim or unresolved external evidence and is never touched, at any age. The private client JSON
    /// is cleared first because only a pre-mutation or ambiguous operation can still be recovered from it, which also
    /// removes credential material from the long-lived row.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> PruneCreationOperationsAsync(
        DatabaseCleanupOptions options,
        DateTime cutoff,
        DateTime payloadCutoff,
        CancellationToken token)
    {
        var compacted = 0;
        var deleted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.XuiV3CreationOperations
                .Where(row => (row.Outcome == XuiV3CreationOutcome.Applied || row.Outcome == XuiV3CreationOutcome.DefinitiveRejected)
                    && (row.AppliedAtUtc ?? row.CreatedAtUtc) < payloadCutoff
                    && (row.ClientJson != null || row.InboundIdsJson != null || row.BusinessParametersJson != null))
                .OrderBy(row => row.OperationKey)
                .Select(row => row.OperationKey)
                .Take(options.BatchSize);
            var affected = await db.XuiV3CreationOperations
                .Where(BuildKeyPredicate<XuiV3CreationOperation, string>(keys, row => row.OperationKey))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(row => row.ClientJson, (string)null)
                        .SetProperty(row => row.InboundIdsJson, (string)null)
                        .SetProperty(row => row.BusinessParametersJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.XuiV3CreationOperations
                .Where(row => (row.Outcome == XuiV3CreationOutcome.Applied || row.Outcome == XuiV3CreationOutcome.DefinitiveRejected)
                    && (row.AppliedAtUtc ?? row.CreatedAtUtc) < cutoff)
                .OrderBy(row => row.OperationKey)
                .Select(row => row.OperationKey)
                .Take(options.BatchSize);
            var affected = await db.XuiV3CreationOperations
                .Where(BuildKeyPredicate<XuiV3CreationOperation, string>(keys, row => row.OperationKey))
                .ExecuteDeleteAsync(token);
            deleted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (deleted, compacted);
    }

    /// <summary>
    /// Clears the heavy JSON evidence columns of settled renewal operations while keeping the financial audit row.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="payloadCutoff">UTC instant before which stored mutation payloads and snapshots are cleared.</param>
    /// <param name="token">Cancellation token for the batched update.</param>
    /// <returns>Deleted and compacted counters for the renewal-operation table; deletion is always zero.</returns>
    /// <remarks>
    /// <para>
    /// Renewal rows are never deleted here. Each row is the exactly-once anchor and the audit record of a wallet debit,
    /// so the retention policy keeps the identifiers, target panel identity, absolute quota and expiry targets, added
    /// traffic and duration, price, payment method, statuses, and timestamps.
    /// </para>
    /// <para>
    /// Only rows that are both <c>applied</c> and <c>settled</c> are compacted, because those are the only rows no
    /// reconciliation path can read again. The full replacement payload that was sent to the panel and the
    /// pre-mutation snapshot are the two largest columns and are the ones cleared.
    /// </para>
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> CompactRenewalOperationsAsync(
        DatabaseCleanupOptions options,
        DateTime payloadCutoff,
        CancellationToken token)
    {
        var compacted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.XuiV3RenewalOperations
                .Where(row => row.Status == XuiV3RenewalOperationStatuses.Applied
                    && row.SettlementStatus == XuiV3RenewalSettlementStatuses.Settled
                    && (row.UpdatedAtUtc ?? row.CreatedAtUtc) < payloadCutoff
                    && (row.MutationPayloadJson != null || row.PreMutationSnapshotJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.XuiV3RenewalOperations
                .Where(BuildKeyPredicate<XuiV3RenewalOperation, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(row => row.MutationPayloadJson, (string)null)
                        .SetProperty(row => row.PreMutationSnapshotJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (0, compacted);
    }

    /// <summary>
    /// Compacts and removes finished XUI v3 account link-change sagas.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="cutoff">UTC instant before which finished link-change rows are deleted.</param>
    /// <param name="payloadCutoff">UTC instant before which stored snapshot and payload JSON is cleared.</param>
    /// <param name="token">Cancellation token for the batched statements.</param>
    /// <returns>Deleted and compacted counters for the link-change table.</returns>
    /// <remarks>
    /// Only the four finished statuses qualify. An operation awaiting confirmation, currently processing, awaiting
    /// recovery, or escalated to manual review stays forever until an operator resolves it, because deleting it would
    /// release the account lock that prevents two identity mutations on the same panel client.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> PruneLinkChangeOperationsAsync(
        DatabaseCleanupOptions options,
        DateTime cutoff,
        DateTime payloadCutoff,
        CancellationToken token)
    {
        var compacted = 0;
        var deleted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            // The finished-status set is written out as literal comparisons rather than a helper call: the predicate is
            // translated to SQL, so a C# method invocation would fail to translate and would disable this table step.
            var keys = db.XuiV3LinkChangeOperations
                .Where(row => (row.Status == XuiV3LinkChangeStatuses.Succeeded
                        || row.Status == XuiV3LinkChangeStatuses.FailedBeforeMutation
                        || row.Status == XuiV3LinkChangeStatuses.Cancelled
                        || row.Status == XuiV3LinkChangeStatuses.Expired)
                    && (row.CompletedAtUtc ?? row.UpdatedAtUtc) < payloadCutoff
                    && (row.SnapshotJson != null || row.PayloadJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.XuiV3LinkChangeOperations
                .Where(BuildKeyPredicate<XuiV3LinkChangeOperation, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.SnapshotJson, (string)null).SetProperty(row => row.PayloadJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.XuiV3LinkChangeOperations
                .Where(row => (row.Status == XuiV3LinkChangeStatuses.Succeeded
                        || row.Status == XuiV3LinkChangeStatuses.FailedBeforeMutation
                        || row.Status == XuiV3LinkChangeStatuses.Cancelled
                        || row.Status == XuiV3LinkChangeStatuses.Expired)
                    && (row.CompletedAtUtc ?? row.UpdatedAtUtc) < cutoff)
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.XuiV3LinkChangeOperations
                .Where(BuildKeyPredicate<XuiV3LinkChangeOperation, int>(keys, row => row.Id))
                .ExecuteDeleteAsync(token);
            deleted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (deleted, compacted);
    }

    /// <summary>
    /// Clears the duplicated provider request, response, and IPN JSON of old HooshPay payment rows.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="cutoff">UTC instant before which provider payload copies may be cleared.</param>
    /// <param name="token">Cancellation token for the batched update.</param>
    /// <returns>Deleted and compacted counters for the HooshPay table; deletion is always zero because payment rows are permanent.</returns>
    /// <remarks>
    /// A row qualifies only when the money has reached the customer (<c>IsAddedToBalance</c>) or the provider status is
    /// final and unpayable (<c>expired</c>, <c>cancelled</c>, <c>failed</c>). A <c>paid</c> row that was never added to
    /// a balance is exactly the case that still needs its stored provider response, so it is excluded.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> CompactHooshPayAsync(DatabaseCleanupOptions options, DateTime cutoff, CancellationToken token)
    {
        var compacted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.HooshPayPaymentInfos
                .Where(row => row.CreatedAtUtc < cutoff
                    && (row.IsAddedToBalance
                        || row.PaymentStatus == HooshPayStatuses.Expired
                        || row.PaymentStatus == HooshPayStatuses.Cancelled
                        || row.PaymentStatus == HooshPayStatuses.Failed)
                    && (row.RawRequestJson != null || row.RawResponseJson != null || row.RawIpnJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.HooshPayPaymentInfos
                .Where(BuildKeyPredicate<HooshPayPaymentInfo, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(row => row.RawRequestJson, (string)null)
                        .SetProperty(row => row.RawResponseJson, (string)null)
                        .SetProperty(row => row.RawIpnJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (0, compacted);
    }

    /// <summary>
    /// Clears the duplicated NOWPayments request, response, and IPN JSON of old swap/NOWPayments payment rows.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="cutoff">UTC instant before which provider payload copies may be cleared.</param>
    /// <param name="token">Cancellation token for the batched update.</param>
    /// <returns>Deleted and compacted counters for the Swapino table; deletion is always zero because payment rows are permanent.</returns>
    /// <remarks>
    /// A row qualifies when the amount reached the customer's balance or the provider reported a final failure
    /// (<c>failed</c>, <c>refunded</c>, <c>expired</c>), which mirrors <c>NowPaymentsStatuses.IsFinalFailure</c>. Rows
    /// that are still <c>waiting</c>, <c>confirming</c>, or <c>partially_paid</c> keep their stored evidence because a
    /// later IPN or a manual review can still settle them.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> CompactSwapinoAsync(DatabaseCleanupOptions options, DateTime cutoff, CancellationToken token)
    {
        var compacted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.SwapinoPaymentInfos
                .Where(row => row.CreatedAtUtc < cutoff
                    && (row.IsAddedToBalance
                        || row.PaymentStatus == NowPaymentsStatuses.Failed
                        || row.PaymentStatus == NowPaymentsStatuses.Refunded
                        || row.PaymentStatus == NowPaymentsStatuses.Expired)
                    && (row.RawRequestJson != null || row.RawResponseJson != null || row.RawIpnJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.SwapinoPaymentInfos
                .Where(BuildKeyPredicate<SwapinoPaymentInfo, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(row => row.RawRequestJson, (string)null)
                        .SetProperty(row => row.RawResponseJson, (string)null)
                        .SetProperty(row => row.RawIpnJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (0, compacted);
    }

    /// <summary>
    /// Clears the duplicated provider request and response JSON of old UniquePay payment rows.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="cutoff">UTC instant before which provider payload copies may be cleared.</param>
    /// <param name="token">Cancellation token for the batched update.</param>
    /// <returns>Deleted and compacted counters for the UniquePay table; deletion is always zero because payment rows are permanent.</returns>
    /// <remarks>
    /// A row qualifies when the amount reached the customer's balance or the invoice terminally failed. Rows whose
    /// settlement state is still pending, processing, or in manual review keep their stored evidence, because the
    /// reconciliation worker may still need to prove the provider's answer.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> CompactUniquePayAsync(DatabaseCleanupOptions options, DateTime cutoff, CancellationToken token)
    {
        var compacted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.UniquePayPaymentInfos
                .Where(row => row.CreatedAtUtc < cutoff
                    && (row.IsAddedToBalance || row.PaymentStatus == UniquePayStatuses.Failed)
                    && (row.RawRequestJson != null || row.RawResponseJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.UniquePayPaymentInfos
                .Where(BuildKeyPredicate<UniquePayPaymentInfo, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.RawRequestJson, (string)null).SetProperty(row => row.RawResponseJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (0, compacted);
    }

    /// <summary>
    /// Clears the duplicated provider request and response JSON of old Tetraminator payment rows.
    /// </summary>
    /// <param name="options">Validated retention and batching options for this pass.</param>
    /// <param name="cutoff">UTC instant before which provider payload copies may be cleared.</param>
    /// <param name="token">Cancellation token for the batched update.</param>
    /// <returns>Deleted and compacted counters for the Tetraminator table; deletion is always zero because payment rows are permanent.</returns>
    /// <remarks>
    /// Only credited rows are compacted. Tetraminator has no terminal failure status in its stored vocabulary, so a row
    /// that is still <c>pending</c> keeps its request JSON for the callback that may yet arrive.
    /// </remarks>
    private async Task<(int Deleted, int Compacted)> CompactTetraminatorAsync(DatabaseCleanupOptions options, DateTime cutoff, CancellationToken token)
    {
        var compacted = 0;
        await using var db = _users.CreateDbContext();
        for (var batch = 0; batch < options.MaxBatchesPerTable; batch++)
        {
            var keys = db.TetraminatorPaymentInfos
                .Where(row => row.CreatedAtUtc < cutoff
                    && row.IsAddedToBalance
                    && (row.RawRequestJson != null || row.RawResponseJson != null))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .Take(options.BatchSize);
            var affected = await db.TetraminatorPaymentInfos
                .Where(BuildKeyPredicate<TetraminatorPaymentInfo, int>(keys, row => row.Id))
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.RawRequestJson, (string)null).SetProperty(row => row.RawResponseJson, (string)null),
                    token);
            compacted += affected;
            if (affected < options.BatchSize)
                break;
        }

        return (0, compacted);
    }

    /// <summary>
    /// Builds the key-membership predicate that restricts a batched delete or update to the bounded key set.
    /// </summary>
    /// <typeparam name="TEntity">Tracked entity type whose rows are being modified.</typeparam>
    /// <typeparam name="TKey">Primary key type of the entity; <c>string</c> for operation-keyed tables and <c>int</c> for identity-keyed tables.</typeparam>
    /// <param name="keys">
    /// Bounded key query built from the retention predicate with an explicit <c>Take</c> limit. It must remain an
    /// <see cref="IQueryable{TKey}" /> so Entity Framework translates it into a subquery instead of expanding every key
    /// into a separate SQL parameter.
    /// </param>
    /// <param name="selector">Expression that reads the entity's primary key.</param>
    /// <returns>A predicate suitable for <c>ExecuteDeleteAsync</c> and <c>ExecuteUpdateAsync</c>.</returns>
    /// <remarks>
    /// Keep the parameter type as a queryable: passing a materialized list would turn the statement into one
    /// <c>IN (?, ?, ...)</c> with thousands of parameters, which both exceeds SQLite's parameter limit and loses the
    /// single bounded subquery that keeps the write lock short.
    /// </remarks>
    private static Expression<Func<TEntity, bool>> BuildKeyPredicate<TEntity, TKey>(
        IQueryable<TKey> keys,
        Expression<Func<TEntity, TKey>> selector)
    {
        var parameter = selector.Parameters[0];
        var contains = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            new[] { typeof(TKey) },
            keys.Expression,
            selector.Body);
        return Expression.Lambda<Func<TEntity, bool>>(contains, parameter);
    }

    /// <summary>
    /// Runs one table's retention step and converts an unexpected failure into a reported label.
    /// </summary>
    /// <param name="table">Database table name used for the failure label and the final counters.</param>
    /// <param name="failureLabel">Fixed category written into the report when this table's step fails.</param>
    /// <param name="work">Retention work for that table, returning deleted and compacted counters.</param>
    /// <param name="token">Cancellation token; cancellation propagates instead of being reported as a table failure.</param>
    /// <returns>The table counters, or a result carrying a safe failure label and zero counters.</returns>
    /// <remarks>
    /// A failure is contained deliberately: the remaining tables still run, and the failure is logged with its table
    /// name. Nothing is swallowed silently, and no failure can enable or disable a business feature.
    /// </remarks>
    private static async Task<DatabaseCleanupTableResult> RunTableAsync(
        string table,
        string failureLabel,
        Func<Task<(int Deleted, int Compacted)>> work,
        CancellationToken token)
    {
        try
        {
            var (deleted, compacted) = await work();
            return new DatabaseCleanupTableResult(table, deleted, compacted, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException || ex is DbUpdateException || ex is InvalidOperationException || ex is NotSupportedException)
        {
            return new DatabaseCleanupTableResult(table, 0, 0, failureLabel);
        }
    }

    /// <summary>
    /// Resolves a configured database path to the absolute path the file-system metrics and maintenance helper need.
    /// </summary>
    /// <param name="configuredPath">
    /// Value of <c>userDatabasePath</c> or <c>credentialsDatabasePath</c>. It is normally relative to the process
    /// working directory, which is also how the Entity Framework connection strings resolve it.
    /// </param>
    /// <returns>
    /// The absolute path of the database file, or null when the configuration value is missing so the caller reports
    /// zero sizes instead of measuring an unintended file.
    /// </returns>
    private static string ResolveDatabasePath(string configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath) ? null : Path.GetFullPath(configuredPath);
}

/// <summary>
/// Compactable view over the terminal Gozargah website outbox that the cleanup pass can drive without depending on the
/// scoped website-sync service's full surface.
/// </summary>
/// <remarks>
/// The interface exists so the singleton cleanup runner can ask a per-pass dependency-injection scope for the outbox
/// compaction without capturing a scoped service. The outbox service implements it directly, which keeps a single
/// definition of the retention rule for website events.
/// </remarks>
public interface ITerminalOutboxCompactor
{
    /// <summary>
    /// Compacts one bounded batch of expired terminal website outbox events.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the short SQLite cleanup statement.</param>
    /// <returns>
    /// The number of rows removed, at most the outbox service's own batch size. Pending and failed events are never
    /// candidates, and each account's latest successful state is retained as a tombstone.
    /// </returns>
    /// <remarks>
    /// The call is idempotent and safe to repeat: a second call with nothing left to compact returns zero.
    /// </remarks>
    Task<int> CompactTerminalEventsAsync(CancellationToken cancellationToken);
}
