using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Detached identity-scoped endpoint persistence used by runtime and protocol tests.</summary>
/// <remarks>All results are detached and identity-isolated. Only endpoint metadata is writable; no credentials database or financial entity is involved.</remarks>
public interface ITelegramEndpointStateStore
{
    /// <summary>Loads exact identity metadata while fencing Cloud reads or creation when historical aliases cannot prove Cloud reuse safe.</summary>
    /// <param name="botId">Required exact internal registry identifier, at most 64 characters.</param>
    /// <param name="identity">Required positive numeric BotFather identity.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>A detached secret-free state; creation never starts a migration.</returns>
    /// <exception cref="ArgumentException">The exact bot id or positive BotFather identity is invalid.</exception>
    /// <remarks>Token rotation and returning to the same numeric identity retain exact route state. A fresh alias or an existing active Cloud alias with historical Local, uncertainty, or cooldown receives fenced ManualInterventionRequired metadata; an existing Cloud conflict advances revision/control revision by conditional CAS. Local routes are not overwritten, and session proof is never transferred from another alias. No Telegram or financial operation occurs at this hydration boundary.</remarks>
    /// <example><code>var state = await store.GetOrCreateAsync(bot.Id, identity, token);</code></example>
    Task<TelegramEndpointState> GetOrCreateAsync(string botId, long identity, CancellationToken token);
    /// <summary>Reads all stored bot identities using bounded database pages.</summary>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>Detached states, including historical replaced bot identities.</returns>
    /// <remarks>Includes disabled, missing, and replaced identities for restart reconciliation; inventory callers must join the current registry separately.</remarks>
    /// <example><code>var savedRoutes = await store.ReadAllAsync(token);</code></example>
    Task<IReadOnlyList<TelegramEndpointState>> ReadAllAsync(CancellationToken token);
    /// <summary>Reads durable routes for one BotFather identity across current, removed, and replaced internal aliases.</summary>
    /// <param name="identity">Positive numeric identity extracted from the token before any identity-bearing HTTP request.</param>
    /// <param name="token">Cancellation of the bounded database query.</param>
    /// <returns>At most 128 detached alias states; an empty result proves no saved endpoint session exists.</returns>
    /// <remarks>This read never creates Cloud metadata. Incomplete authority fails closed rather than allowing a historical Local identity to log into Cloud.</remarks>
    /// <exception cref="BotTransportUnavailableException">The implementation cannot establish complete bounded identity authority.</exception>
    /// <example><code>var aliases = await store.ReadIdentityStatesAsync(identity, cancellationToken);</code></example>
    Task<IReadOnlyList<TelegramEndpointState>> ReadIdentityStatesAsync(long identity, CancellationToken token) =>
        Task.FromException<IReadOnlyList<TelegramEndpointState>>(new BotTransportUnavailableException("endpoint_identity_authority_unavailable"));
    /// <summary>CAS-commits a state and optional append-only history and incident-only logger intent atomically.</summary>
    /// <param name="state">Required detached proposed state; secrets and raw errors are forbidden.</param>
    /// <param name="expectedRevision">Nonnegative revision read before proposing the transition.</param>
    /// <param name="historyReason">Optional closed internal transition reason.</param>
    /// <param name="alertCategory">Optional closed independent-alert category.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>True on commit; false on CAS conflict, requiring reload without replaying external mutations.</returns>
    /// <exception cref="ArgumentException">The proposal decreases a generation/control revision or contains a non-allowlisted persisted category.</exception>
    /// <remarks>Updates the proposal's Revision and ControlRevision only after commit. Health-only writes advance Revision but not ControlRevision. SQLite-only retries never replay an HTTP request.</remarks>
    /// <example><code>if (!await store.TrySaveAsync(proposed, current.Revision, "migration_requested", "migration_started", token)) current = await store.GetOrCreateAsync(proposed.BotId, proposed.TelegramBotId, token);</code></example>
    Task<bool> TrySaveAsync(TelegramEndpointState state, long expectedRevision, string historyReason = null, string alertCategory = null, CancellationToken token = default);
    /// <summary>Reads recent immutable migration receipts for one exact identity.</summary>
    /// <param name="botId">Required internal bot id.</param>
    /// <param name="identity">Positive BotFather identity.</param>
    /// <param name="limit">Maximum receipt count, clamped to 1 through 100.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>Detached newest-first receipts, possibly empty.</returns>
    /// <remarks>Receipts are append-only and survive registry deletion, replacement, and restart; the limit bounds the panel read, not durable retention.</remarks>
    /// <example><code>var recent = await store.ReadHistoryAsync(bot.Id, identity, 20, token);</code></example>
    Task<IReadOnlyList<TelegramEndpointHistory>> ReadHistoryAsync(string botId, long identity, int limit, CancellationToken token);
    /// <summary>Counts undelivered incidents including uncertain and manual-review rows.</summary>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>Visible outstanding incident count; uncertainty is never silently excluded.</returns>
    /// <remarks>Missing logger prerequisites, uncertain sends, and exhausted real safe attempts remain visible until reviewed.</remarks>
    /// <example><code>var outstanding = await store.CountPendingAlertsAsync(token);</code></example>
    Task<int> CountPendingAlertsAsync(CancellationToken token);
}

/// <summary>Short-context SQLite endpoint persistence with CAS state and transactional alert deduplication.</summary>
/// <remarks>Only endpoint tables are written. No network request or financial mutation occurs inside a transaction.</remarks>
public sealed class TelegramEndpointStore : ITelegramEndpointStateStore
{
    /// <summary>Independent users.db context factory.</summary>
    private readonly UserDbContextFactory _factory;
    /// <summary>Startup logger configuration; never used as private incident recipient authority.</summary>
    private readonly AppConfig _configuration;
    /// <summary>Optional live root logger configuration, authoritative over the startup snapshot.</summary>
    private readonly Microsoft.Extensions.Configuration.IConfiguration _liveConfiguration;
    /// <summary>Validated startup resource bounds.</summary>
    private readonly TelegramEndpointRoutingOptions _options;
    /// <summary>Finite safe failure vocabulary, including the shared catalog's closed migration boundary combinations; never exception messages.</summary>
    private static readonly HashSet<string> FailureCategories = new(TelegramEndpointDiagnosticCatalog.MigrationFailureCategories, StringComparer.Ordinal)
    {
        "connection_refused", "timeout", "network", "invalid_response", "rate_limited", "telegram_upstream", "token_rejected",
        "identity_mismatch", "receiver_not_ready", "logout_refused", "logout_uncertain", "configuration_missing", "drain_timeout",
        "persistence_conflict", "unsafe_cleanup", "recovery_exhausted", "receiver_start_failed", "lifecycle_unavailable", "identity_alias_conflict", "operator_control_missing"
    };
    /// <summary>Finite operator alert categories rendered into safe runtime text.</summary>
    private static readonly HashSet<string> AlertCategories = new(StringComparer.Ordinal)
    {
        "migration_started", "migration_failed", "migration_succeeded", "local_outage", "local_recovered", "fallback_pending",
        "cloud_recovered", "manual_intervention"
    };
    /// <summary>Finite safe worker result categories.</summary>
    private static readonly HashSet<string> DeliveryCategories = new(StringComparer.Ordinal)
    {
        "logger_unconfigured", "logger_unavailable", "transport_unavailable", "pre_send_identity_mismatch",
        "pre_send_timeout", "pre_send_failure", "send_uncertain", "worker_interrupted", "retry_exhausted", "send_rejected", "rate_limited"
    };

    /// <summary>Finite internal migration history reasons; never arbitrary request or response payloads.</summary>
    private static readonly HashSet<string> HistoryReasons = new(StringComparer.Ordinal)
    {
        "migration_requested", "migration_admission_failed", "auto_failover_changed", "startup_reconciled", "cloud_logout_intent", "local_logout_intent",
        "logout_acknowledged", "logout_uncertain", "logout_refused", "destination_starting", "migration_succeeded",
        "migration_failed", "local_outage", "local_recovered", "fallback_pending", "cloud_wait", "cloud_recovered",
        "manual_intervention", "safe_retry_scheduled", "automatic_failback"
    };
    /// <summary>Creates the endpoint-only durable store.</summary>
    /// <param name="factory">Required users.db factory; every operation owns and disposes its context.</param>
    /// <param name="configuration">Required application logger configuration; tokens are never persisted.</param>
    /// <param name="options">Optional trusted startup settings; omission uses safe defaults.</param>
    /// <param name="liveConfiguration">Optional live root logger configuration; when supplied it replaces startup logger authority.</param>
    /// <remarks>No schema creation, network calls, or migrations run in this constructor.</remarks>
    public TelegramEndpointStore(UserDbContextFactory factory, AppConfig configuration, TelegramEndpointRoutingOptions options = null,
        Microsoft.Extensions.Configuration.IConfiguration liveConfiguration = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = (options ?? new TelegramEndpointRoutingOptions()).ValidateAndSnapshot();
        _liveConfiguration = liveConfiguration;
    }

    /// <inheritdoc />
    public Task<TelegramEndpointState> GetOrCreateAsync(string botId, long identity, CancellationToken token)
    {
        ValidateIdentity(botId, identity);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var current = await db.TelegramEndpointStates.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == botId && x.TelegramBotId == identity, ct);
            if (current != null && (current.EffectiveEndpoint != TelegramEndpointType.Cloud ||
                current.MigrationState is not (TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered)))
                return current;
            var aliases = await db.TelegramEndpointStates.AsNoTracking()
                .Where(x => x.TelegramBotId == identity && x.BotId != botId).OrderBy(x => x.BotId).Take(129).ToListAsync(ct);
            var now = DateTime.UtcNow;
            var conflict = aliases.Count > 128 || aliases.Any(x =>
                x.EffectiveEndpoint != TelegramEndpointType.Cloud ||
                x.MigrationState is not (TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered) ||
                x.CloudReuseEligibleAtUtc > now ||
                x.LogoutAttemptedAtUtc.HasValue &&
                (!x.LogoutAcknowledgedAtUtc.HasValue || x.LogoutAcknowledgedAtUtc < x.LogoutAttemptedAtUtc));
            if (current != null)
            {
                if (!conflict) return current;
                var revision = checked(current.Revision + 1);
                var controlRevision = checked(current.ControlRevision + 1);
                await db.TelegramEndpointStates
                    .Where(x => x.BotId == botId && x.TelegramBotId == identity && x.Revision == current.Revision &&
                        x.EffectiveEndpoint == TelegramEndpointType.Cloud &&
                        (x.MigrationState == TelegramEndpointMigrationState.Cloud || x.MigrationState == TelegramEndpointMigrationState.CloudRecovered))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.MigrationState, TelegramEndpointMigrationState.ManualInterventionRequired)
                        .SetProperty(x => x.LastFailureCategory, "identity_alias_conflict")
                        .SetProperty(x => x.Revision, revision)
                        .SetProperty(x => x.ControlRevision, controlRevision), ct);
                var fenced = await db.TelegramEndpointStates.AsNoTracking().SingleAsync(x => x.BotId == botId && x.TelegramBotId == identity, ct);
                await transaction.CommitAsync(ct);
                return fenced;
            }
            var migrationState = conflict ? TelegramEndpointMigrationState.ManualInterventionRequired : TelegramEndpointMigrationState.Cloud;
            var failureCategory = conflict ? "identity_alias_conflict" : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO TelegramEndpointStates (BotId, TelegramBotId, DesiredEndpoint, EffectiveEndpoint, MigrationState, Generation, Revision, ControlRevision, AutoFailoverEnabled, ConsecutiveFailures, ConsecutiveSuccesses, RecoveryAttempts, LastFailureCategory) VALUES ({botId}, {identity}, {0}, {0}, {(int)migrationState}, {1L}, {0L}, {0L}, {true}, {0}, {0}, {0}, {failureCategory})", ct);
            var created = await db.TelegramEndpointStates.AsNoTracking().SingleAsync(x => x.BotId == botId && x.TelegramBotId == identity, ct);
            await transaction.CommitAsync(ct);
            return created;
        }, token);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TelegramEndpointState>> ReadAllAsync(CancellationToken token) => SqliteOperation.RunAsync<IReadOnlyList<TelegramEndpointState>>(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var result = new List<TelegramEndpointState>();
        for (var offset = 0; ; offset += 128)
        {
            var page = await db.TelegramEndpointStates.AsNoTracking().OrderBy(x => x.BotId).ThenBy(x => x.TelegramBotId).Skip(offset).Take(128).ToListAsync(ct);
            result.AddRange(page);
            if (page.Count < 128) return result;
        }
    }, token);

    /// <inheritdoc />
    public Task<IReadOnlyList<TelegramEndpointState>> ReadIdentityStatesAsync(long identity, CancellationToken token)
    {
        if (identity <= 0) throw new ArgumentOutOfRangeException(nameof(identity));
        return SqliteOperation.RunAsync<IReadOnlyList<TelegramEndpointState>>(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var aliases = await db.TelegramEndpointStates.AsNoTracking()
                .Where(x => x.TelegramBotId == identity).OrderBy(x => x.BotId).Take(129).ToListAsync(ct);
            if (aliases.Count > 128)
                throw new BotTransportUnavailableException("endpoint_identity_authority_overflow");
            return aliases;
        }, token);
    }

    /// <inheritdoc />
    /// <remarks>Failure, uncertainty, refusal, startup reconciliation and safe-retry history preserve the already-validated closed failure category in Outcome. Successful/requested transitions retain migration-state names even if health metadata still has an older failure; no new persistence boundary or schema is introduced.</remarks>
    public async Task<bool> TrySaveAsync(TelegramEndpointState state, long expectedRevision, string historyReason = null, string alertCategory = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var proposed = state.Copy();
        ValidateState(proposed, expectedRevision);
        if (historyReason != null && !HistoryReasons.Contains(historyReason)) throw new ArgumentException("Unknown endpoint history reason.", nameof(historyReason));
        if (alertCategory != null && !AlertCategories.Contains(alertCategory)) throw new ArgumentException("Unknown endpoint alert category.", nameof(alertCategory));
        var committed = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // The same SQLite transaction fences the CAS proposal, immutable history, and one permanent incident receipt.
            var existing = await db.TelegramEndpointStates.SingleOrDefaultAsync(x => x.BotId == proposed.BotId && x.TelegramBotId == proposed.TelegramBotId, ct);
            if (existing == null || existing.Revision != expectedRevision) return (Success: false, Revision: 0L, Control: 0L);
            if (proposed.Generation < existing.Generation) throw new ArgumentException("Endpoint generation cannot decrease.", nameof(state));
            if (proposed.ControlRevision < existing.ControlRevision || proposed.ControlRevision > existing.ControlRevision + 1)
                throw new ArgumentException("Control revision cannot decrease or skip an intent.", nameof(state));
            var next = proposed.Copy();
            next.Revision = checked(expectedRevision + 1);
            next.ControlRevision = HasControlChange(existing, next) || next.ControlRevision > existing.ControlRevision
                ? checked(existing.ControlRevision + 1) : existing.ControlRevision;
            var now = DateTime.UtcNow;
            if (historyReason != null)
                db.TelegramEndpointHistory.Add(new TelegramEndpointHistory
                {
                    BotId = next.BotId, TelegramBotId = next.TelegramBotId, OperationId = next.OperationId, ActorTelegramUserId = next.ActorTelegramUserId,
                    FromDesiredEndpoint = existing.DesiredEndpoint, ToDesiredEndpoint = next.DesiredEndpoint,
                    FromEffectiveEndpoint = existing.EffectiveEndpoint, ToEffectiveEndpoint = next.EffectiveEndpoint,
                    MigrationState = next.MigrationState, Reason = historyReason,
                    Outcome = next.LastFailureCategory != null &&
                        historyReason is "migration_failed" or "migration_admission_failed" or "logout_refused" or "logout_uncertain" or "manual_intervention" or "safe_retry_scheduled" or "startup_reconciled"
                        ? next.LastFailureCategory : next.MigrationState.ToString(),
                    CreatedAtUtc = now, Revision = next.Revision
                });
            db.Entry(existing).CurrentValues.SetValues(next);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return (Success: false, Revision: 0L, Control: 0L); }
            if (alertCategory != null)
            {
                var incident = alertCategory is "local_outage" or "local_recovered" or "fallback_pending" or "cloud_recovered"
                    ? next.OutageId ?? next.OperationId : next.OperationId ?? next.OutageId;
                incident ??= $"revision:{next.Revision}";
                var key = $"{next.BotId}:{next.TelegramBotId}:{incident}:{alertCategory}";
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT OR IGNORE INTO TelegramEndpointAlertReceipts (IncidentKey) VALUES ({key})", ct);
                if (inserted == 1)
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TelegramEndpointAlerts (IncidentKey, BotId, TelegramBotId, Category, MigrationState, DesiredEndpoint, EffectiveEndpoint, Generation, CreatedAtUtc, Status, Attempts, NextAttemptAtUtc) VALUES ({key}, {next.BotId}, {next.TelegramBotId}, {alertCategory}, {(int)next.MigrationState}, {(int)next.DesiredEndpoint}, {(int)next.EffectiveEndpoint}, {next.Generation}, {now}, {(int)TelegramEndpointAlertStatus.Pending}, {0}, {now})", ct);
            }
            await transaction.CommitAsync(ct);
            return (Success: true, Revision: next.Revision, Control: next.ControlRevision);
        }, token);
        if (committed.Success) { state.Revision = committed.Revision; state.ControlRevision = committed.Control; }
        return committed.Success;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TelegramEndpointHistory>> ReadHistoryAsync(string botId, long identity, int limit, CancellationToken token)
    {
        ValidateIdentity(botId, identity);
        return SqliteOperation.RunAsync<IReadOnlyList<TelegramEndpointHistory>>(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.TelegramEndpointHistory.AsNoTracking().Where(x => x.BotId == botId && x.TelegramBotId == identity)
                .OrderByDescending(x => x.Id).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        }, token);
    }

    /// <inheritdoc />
    public Task<int> CountPendingAlertsAsync(CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramEndpointAlerts.CountAsync(x => x.Status != TelegramEndpointAlertStatus.Delivered, ct);
    }, token);

    /// <summary>Claims one due alert after safely reconciling expired worker claims.</summary>
    /// <param name="now">Current UTC instant supplied by the worker clock.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>A detached exclusive claim, or null when no safe intent is due.</returns>
    /// <remarks>Interrupted post-boundary sends become DeliveryUncertain and are never replayed. Pre-send claims safely resume.</remarks>
    public Task<TelegramEndpointAlert> ClaimAlertAsync(DateTime now, CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var uncertainIds = await db.TelegramEndpointAlerts.Where(x => x.Status == TelegramEndpointAlertStatus.Processing &&
                x.SendStartedAtUtc != null && (x.SendDeadlineAtUtc ?? x.LeaseUntilUtc) <= now)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(128).ToListAsync(ct);
        await db.TelegramEndpointAlerts.Where(x => uncertainIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TelegramEndpointAlertStatus.DeliveryUncertain).SetProperty(x => x.ErrorCategory, "worker_interrupted")
                .SetProperty(x => x.ClaimId, (string)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
        var safeIds = await db.TelegramEndpointAlerts.Where(x => x.Status == TelegramEndpointAlertStatus.Processing &&
                x.SendStartedAtUtc == null && x.LeaseUntilUtc <= now)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(128).ToListAsync(ct);
        await db.TelegramEndpointAlerts.Where(x => safeIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TelegramEndpointAlertStatus.Pending).SetProperty(x => x.ClaimId, (string)null).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
        var exhaustedIds = await db.TelegramEndpointAlerts.Where(x => x.Status == TelegramEndpointAlertStatus.Pending && x.Attempts >= _options.NotificationMaxAttempts)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(128).ToListAsync(ct);
        await db.TelegramEndpointAlerts.Where(x => exhaustedIds.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TelegramEndpointAlertStatus.ManualReview).SetProperty(x => x.ErrorCategory, "retry_exhausted"), ct);
        var row = await db.TelegramEndpointAlerts.Where(x => x.Status == TelegramEndpointAlertStatus.Pending && x.Attempts < _options.NotificationMaxAttempts && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (row == null) { await tx.CommitAsync(ct); return null; }
        row.Status = TelegramEndpointAlertStatus.Processing;
        row.ClaimId = Guid.NewGuid().ToString("N");
        row.LeaseUntilUtc = now.AddSeconds(_options.MigrationTimeoutSeconds + 30);
        row.Attempts++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }, token);

    /// <summary>Atomically freezes the verified channel destination and persists the at-most-once send boundary.</summary>
    /// <param name="alert">Exclusive detached claim containing its id and claim identity.</param>
    /// <param name="destinationChatId">Negative Telegram channel chat id verified by the worker using current bot post permissions.</param>
    /// <param name="now">Current UTC instant.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>True only when the live pre-send claim and unchanged destination were fenced durably; false prohibits sending.</returns>
    /// <remarks>The worker must retain a normal Cloud request lease through this write and send completion. A frozen destination cannot change after a definitive 429. No Telegram request occurs here.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The destination is not negative.</exception>
    /// <example><code>if (await store.MarkAlertSendStartedAsync(alert, channel.Id, now, token)) await client.SendMessage(channel.Id, text, cancellationToken: token);</code></example>
    public Task<bool> MarkAlertSendStartedAsync(TelegramEndpointAlert alert, long destinationChatId, DateTime now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (destinationChatId >= 0) throw new ArgumentOutOfRangeException(nameof(destinationChatId));
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var updated = await db.TelegramEndpointAlerts.Where(x => x.Id == alert.Id && x.ClaimId == alert.ClaimId &&
                    x.Status == TelegramEndpointAlertStatus.Processing && x.SendStartedAtUtc == null && x.LeaseUntilUtc > now &&
                    (x.DestinationChatId == null || x.DestinationChatId == destinationChatId))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DestinationChatId, (long?)destinationChatId)
                    .SetProperty(x => x.SendStartedAtUtc, (DateTime?)now)
                    .SetProperty(x => x.SendDeadlineAtUtc, (DateTime?)now.AddSeconds(_options.MigrationTimeoutSeconds + 30)), ct) == 1;
            if (updated) alert.DestinationChatId = destinationChatId;
            return updated;
        }, token);
    }

    /// <summary>Commits a safe pre-send retry or terminal delivery outcome without clearing an ambiguous send boundary.</summary>
    /// <param name="alert">Exclusive detached claim.</param>
    /// <param name="status">Pending only before the send boundary; Delivered requires successful provider acknowledgment.</param>
    /// <param name="category">Optional closed worker category; never raw exception text.</param>
    /// <param name="now">Current UTC instant.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>A task completing after the owned claim is updated; a superseded claim changes nothing.</returns>
    /// <remarks>Missing logger/sender prerequisites refund the pre-send claim and remain Pending indefinitely with capped scheduling. Real read failures consume the finite attempt budget. Unresolved rows and compact receipts are never pruned.</remarks>
    /// <exception cref="ArgumentException">The result status or diagnostic category is not part of the closed delivery protocol.</exception>
    /// <exception cref="InvalidOperationException">Acknowledged delivery is proposed without a persisted send boundary.</exception>
    public Task FinishAlertAsync(TelegramEndpointAlert alert, TelegramEndpointAlertStatus status, string category, DateTime now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (status is not (TelegramEndpointAlertStatus.Pending or TelegramEndpointAlertStatus.Delivered or TelegramEndpointAlertStatus.DeliveryUncertain or TelegramEndpointAlertStatus.ManualReview))
            throw new ArgumentException("Invalid notification finish status.", nameof(status));
        if (category != null && !DeliveryCategories.Contains(category)) throw new ArgumentException("Unknown notification result category.", nameof(category));
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var row = await db.TelegramEndpointAlerts.SingleOrDefaultAsync(x => x.Id == alert.Id && x.ClaimId == alert.ClaimId && x.Status == TelegramEndpointAlertStatus.Processing, ct);
            if (row == null) return false;
            if (status == TelegramEndpointAlertStatus.Delivered && row.SendStartedAtUtc == null)
                throw new InvalidOperationException("Delivery acknowledgment requires a durable send boundary.");
            if (status == TelegramEndpointAlertStatus.Pending && row.SendStartedAtUtc != null) status = TelegramEndpointAlertStatus.DeliveryUncertain;
            var prerequisiteMissing = status == TelegramEndpointAlertStatus.Pending &&
                category is "logger_unconfigured" or "logger_unavailable" or "transport_unavailable";
            if (prerequisiteMissing) row.Attempts = Math.Max(0, row.Attempts - 1);
            else if (status == TelegramEndpointAlertStatus.Pending && row.Attempts >= _options.NotificationMaxAttempts) status = TelegramEndpointAlertStatus.ManualReview;
            row.Status = status;
            row.ErrorCategory = category;
            row.NextAttemptAtUtc = now.AddSeconds(Math.Min(_options.RetryMaxSeconds,
                prerequisiteMissing ? _options.RetryMaxSeconds : 5 * Math.Pow(2, Math.Clamp(row.Attempts - 1, 0, 10))));
            row.ClaimId = null;
            row.LeaseUntilUtc = null;
            if (status == TelegramEndpointAlertStatus.Delivered) row.DeliveredAtUtc = now;
            await db.SaveChangesAsync(ct);
            return true;
        }, token);
    }

    /// <summary>Schedules bounded redelivery only after Telegram explicitly rejected an independent alert with HTTP/API 429.</summary>
    /// <param name="alert">Exclusive claim whose send-start marker preceded the definitive rate-limit response.</param>
    /// <param name="now">Current UTC scheduling instant.</param>
    /// <param name="retryAfterSeconds">Provider retry-after seconds, optional; bounded by the configured finite retry cap.</param>
    /// <param name="token">Local persistence cancellation, never a mutation retry token.</param>
    /// <returns>A task completing after the matching claim is released; stale claims cannot clear another send boundary.</returns>
    /// <remarks>Only a definitive 429 proves this message was not accepted. The frozen DestinationChatId remains unchanged, preventing retry into a newly configured channel. Timeouts/5xx/lost replies remain uncertain through FinishAlertAsync. Maximum attempts still produce visible manual review.</remarks>
    /// <example><code>await store.RetryRateLimitedAlertAsync(alert, DateTime.UtcNow, api.Parameters?.RetryAfter, token);</code></example>
    public Task RetryRateLimitedAlertAsync(TelegramEndpointAlert alert, DateTime now, int? retryAfterSeconds, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var row = await db.TelegramEndpointAlerts.SingleOrDefaultAsync(x => x.Id == alert.Id && x.ClaimId == alert.ClaimId &&
                x.Status == TelegramEndpointAlertStatus.Processing && x.SendStartedAtUtc != null, ct);
            if (row == null) return false;
            row.Status = row.Attempts >= _options.NotificationMaxAttempts ? TelegramEndpointAlertStatus.ManualReview : TelegramEndpointAlertStatus.Pending;
            row.ErrorCategory = "rate_limited";
            row.NextAttemptAtUtc = now.AddSeconds(Math.Clamp(retryAfterSeconds ?? 5 * (1 << Math.Min(row.Attempts - 1, 10)), 1, _options.RetryMaxSeconds));
            row.SendStartedAtUtc = row.SendDeadlineAtUtc = null;
            row.ClaimId = null;
            row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct);
            return true;
        }, token);
    }

    /// <summary>Releases a marked claim only when the worker proves that it never invoked the SDK send method.</summary>
    /// <param name="alert">Exclusive detached Processing claim whose send marker was persisted but no send was dispatched.</param>
    /// <param name="category">Closed logger_unavailable or transport_unavailable prerequisite category; never an exception message.</param>
    /// <param name="now">Current UTC scheduling instant.</param>
    /// <param name="token">Cancellation of the local persistence write.</param>
    /// <returns>A task completing after the matching marked claim returns to Pending; stale claims change nothing.</returns>
    /// <remarks>Only the immediate post-marker/pre-SDK admission fence may call this method. Never call after invoking SendMessage, including on cancellation, timeout, or lost response. The negative destination remains frozen, while the unused attempt is refunded and scheduling is capped.</remarks>
    /// <exception cref="ArgumentException">The category is not one of the two supported pre-dispatch prerequisites.</exception>
    /// <example><code>if (!routeStillAvailable) await store.DeferUnsentAlertAsync(alert, "transport_unavailable", now, token); // Before any SDK dispatch only.</code></example>
    public Task DeferUnsentAlertAsync(TelegramEndpointAlert alert, string category, DateTime now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (category is not ("logger_unavailable" or "transport_unavailable"))
            throw new ArgumentException("Invalid pre-dispatch prerequisite category.", nameof(category));
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var row = await db.TelegramEndpointAlerts.SingleOrDefaultAsync(x => x.Id == alert.Id && x.ClaimId == alert.ClaimId &&
                x.Status == TelegramEndpointAlertStatus.Processing && x.SendStartedAtUtc != null, ct);
            if (row == null) return false;
            row.Status = TelegramEndpointAlertStatus.Pending;
            row.ErrorCategory = category;
            row.Attempts = Math.Max(0, row.Attempts - 1);
            row.NextAttemptAtUtc = now.AddSeconds(_options.RetryMaxSeconds);
            row.SendStartedAtUtc = row.SendDeadlineAtUtc = null;
            row.ClaimId = null;
            row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct);
            return true;
        }, token);
    }

    /// <summary>Prunes a bounded page of old acknowledged alerts; unresolved incidents and transition history are never deleted.</summary>
    /// <param name="now">Current UTC instant; delivered receipts are retained at least 90 days.</param>
    /// <param name="token">Cancellation of local database work.</param>
    /// <returns>A task completing after at most 128 old delivered receipts are deleted.</returns>
    public Task PruneDeliveredAlertsAsync(DateTime now, CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var ids = await db.TelegramEndpointAlerts.Where(x => x.Status == TelegramEndpointAlertStatus.Delivered && x.DeliveredAtUtc < now.AddDays(-90))
            .OrderBy(x => x.Id).Select(x => x.Id).Take(128).ToListAsync(ct);
        return await db.TelegramEndpointAlerts.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }, token);

    /// <summary>Resolves the current root logger destination with the current registry-default fallback.</summary>
    /// <param name="fallback">Optional current default bot logger destination, supplied by the worker; never a bot id or token.</param>
    /// <returns>Canonical destination text, or an empty string when neither configured source is valid. The worker must still prove a negative Channel and actual post permission.</returns>
    /// <remarks>Live configuration, when supplied, is authoritative even when blank; a removed live value never revives startup configuration. This performs no Telegram or database operation and never authorizes a private recipient.</remarks>
    /// <example><code>var destination = store.ResolveLoggerChannel(registry.DefaultBot?.LoggerChannel);</code></example>
    public string ResolveLoggerChannel(string fallback) =>
        TelegramDestination.SelectValid(_liveConfiguration != null ? _liveConfiguration["loggerChannel"] : _configuration.LoggerChannel, fallback);

    /// <summary>Identifies migration/control intent changes while ignoring ordinary health hysteresis.</summary>
    /// <param name="before">Durable state before CAS.</param>
    /// <param name="after">Proposed detached state.</param>
    /// <returns>True for an operator, migration, or failover control change.</returns>
    private static bool HasControlChange(TelegramEndpointState before, TelegramEndpointState after) =>
        before.DesiredEndpoint != after.DesiredEndpoint || before.EffectiveEndpoint != after.EffectiveEndpoint || before.Generation != after.Generation ||
        before.AutoFailoverEnabled != after.AutoFailoverEnabled || before.OperationId != after.OperationId || before.Trigger != after.Trigger ||
        before.LogoutAttemptedAtUtc != after.LogoutAttemptedAtUtc || before.LogoutAcknowledgedAtUtc != after.LogoutAcknowledgedAtUtc ||
        before.CloudReuseEligibleAtUtc != after.CloudReuseEligibleAtUtc ||
        (before.MigrationState != after.MigrationState && !(IsHealthPhase(before.MigrationState) && IsHealthPhase(after.MigrationState)));

    /// <summary>Recognizes Local normal/degraded health-only states.</summary>
    /// <param name="state">Durable phase to classify.</param>
    /// <returns>True only for normal or degraded Local health.</returns>
    private static bool IsHealthPhase(TelegramEndpointMigrationState state) => state is TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded;

    /// <summary>Validates a secret-free proposed state before acquiring a database context.</summary>
    /// <param name="state">Required detached state proposal.</param>
    /// <param name="revision">Expected nonnegative CAS version.</param>
    /// <exception cref="ArgumentException">An identity, enum, counter, incident key, or safe category is invalid.</exception>
    private static void ValidateState(TelegramEndpointState state, long revision)
    {
        ValidateIdentity(state.BotId, state.TelegramBotId);
        if (revision < 0 || state.Generation < 1 || state.ControlRevision < 0 || state.ConsecutiveFailures < 0 || state.ConsecutiveSuccesses < 0 || state.RecoveryAttempts < 0 ||
            !Enum.IsDefined(state.DesiredEndpoint) || !Enum.IsDefined(state.EffectiveEndpoint) || !Enum.IsDefined(state.MigrationState) ||
            (state.LogoutEndpoint.HasValue && !Enum.IsDefined(state.LogoutEndpoint.Value))) throw new ArgumentException("Invalid endpoint state.");
        foreach (var id in new[] { state.OperationId, state.OutageId })
            if (id != null && (id.Length != 32 || !id.All(Uri.IsHexDigit))) throw new ArgumentException("Invalid endpoint incident identity.");
        if (state.LastFailureCategory != null && !FailureCategories.Contains(state.LastFailureCategory)) throw new ArgumentException("Unknown endpoint failure category.");
        if (state.Trigger != null && state.Trigger is not ("manual" or "automatic_outage" or "automatic_failback" or "startup_recovery")) throw new ArgumentException("Unknown endpoint trigger.");
    }

    /// <summary>Validates an exact internal id and numeric identity without normalizing historical keys.</summary>
    /// <param name="botId">Required exact internal registry id.</param>
    /// <param name="identity">Positive BotFather identity.</param>
    /// <exception cref="ArgumentException">The id is blank, padded, oversized, contains controls, or the identity is nonpositive.</exception>
    private static void ValidateIdentity(string botId, long identity)
    {
        if (string.IsNullOrWhiteSpace(botId) || botId.Length > 64 || botId != botId.Trim() || botId.Any(char.IsControl) || identity <= 0)
            throw new ArgumentException("Invalid endpoint bot identity.");
    }

}
