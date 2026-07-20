using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Owns durable state transitions and database-level concurrency control for XUI v3 link changes.
/// </summary>
/// <remarks>
/// Every method creates an independent <see cref="UserDbContext"/> through <see cref="UserDbContextFactory"/>.
/// The filtered unique index on panel fingerprint plus numeric client id is the final duplicate-prevention guard;
/// callers must not rely only on Telegram callback timing or in-memory locks.
/// </remarks>
public sealed class XuiV3LinkChangeOperationStore
{
    private readonly UserDbContextFactory _contextFactory;
    private readonly AppConfig _appConfig;

    /// <summary>
    /// Creates the operation store for the migrated <c>users.db</c> database.
    /// </summary>
    /// <param name="contextFactory">Factory that creates one EF Core context per state transition.</param>
    /// <param name="configuration">Runtime configuration containing confirmation and recovery limits.</param>
    public XuiV3LinkChangeOperationStore(
        UserDbContextFactory contextFactory,
        IConfiguration configuration)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _appConfig = configuration?.Get<AppConfig>() ?? new AppConfig();
    }

    /// <summary>
    /// Produces a non-reversible panel fingerprint suitable for the global active-operation uniqueness key.
    /// </summary>
    /// <param name="serverInfo">
    /// Configured XUI panel. Its base URL and root path are normalized only in memory and are never persisted or
    /// returned to Telegram.
    /// </param>
    /// <returns>A lowercase SHA-256 hexadecimal fingerprint.</returns>
    /// <remarks>
    /// Scheme and host are normalized case-insensitively, but URL and root-path casing is preserved because Linux
    /// reverse-proxy paths can be case-sensitive. The source endpoint cannot be reconstructed from the returned hash.
    /// </remarks>
    /// <example>
    /// <code>
    /// var panelKey = XuiV3LinkChangeOperationStore.BuildPanelKey(serverInfo);
    /// </code>
    /// </example>
    public static string BuildPanelKey(ServerInfo serverInfo)
    {
        var rawUrl = (serverInfo?.Url ?? string.Empty).Trim().TrimEnd('/');
        var normalizedUrl = rawUrl;
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            // URI scheme and host are case-insensitive, while a panel path can be case-sensitive on Linux.
            var authority = uri.IsDefaultPort
                ? uri.Host.ToLowerInvariant()
                : $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
            normalizedUrl = $"{uri.Scheme.ToLowerInvariant()}://{authority}{uri.AbsolutePath.TrimEnd('/')}";
        }

        var normalizedRoot = (serverInfo?.RootPath ?? string.Empty).Trim().Trim('/');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedUrl}/{normalizedRoot}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a confirmation operation or returns the active operation that already locks the same panel client.
    /// </summary>
    /// <param name="operation">New operation candidate containing bot, user, Telegram message, and client identity.</param>
    /// <param name="cancellationToken">Token that cancels SQLite reads and writes.</param>
    /// <returns>
    /// The newly inserted operation, or the existing active operation when another callback or bot already created it.
    /// The returned entity is detached from its disposed context.
    /// </returns>
    /// <remarks>
    /// Expired unconfirmed rows are closed before insertion. A database uniqueness violation is treated as a normal
    /// concurrency race and resolved by loading the winner rather than surfacing an error to the customer.
    /// </remarks>
    public async Task<XuiV3LinkChangeOperation> CreateOrGetActiveAsync(
        XuiV3LinkChangeOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();

        await context.XuiV3LinkChangeOperations
            .Where(x => x.PanelKey == operation.PanelKey &&
                        x.ClientId == operation.ClientId &&
                        x.Status == XuiV3LinkChangeStatuses.AwaitingConfirmation &&
                        x.ConfirmationExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.Expired)
                .SetProperty(x => x.Stage, "confirmation-expired")
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.CompletedAtUtc, now), cancellationToken);

        var existing = await FindActiveAsync(context, operation.PanelKey, operation.ClientId, cancellationToken);
        if (existing != null)
            return existing;

        operation.OperationKey = string.IsNullOrWhiteSpace(operation.OperationKey)
            ? Guid.NewGuid().ToString("N")
            : operation.OperationKey;
        operation.Status = XuiV3LinkChangeStatuses.AwaitingConfirmation;
        operation.Stage = "awaiting-confirmation";
        operation.CreatedAtUtc = now;
        operation.UpdatedAtUtc = now;
        operation.ConfirmationExpiresAtUtc = now.AddMinutes(GetConfirmationMinutes());
        context.XuiV3LinkChangeOperations.Add(operation);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            var winner = await FindActiveAsync(context, operation.PanelKey, operation.ClientId, cancellationToken);
            if (winner != null)
                return winner;
            throw;
        }
    }

    /// <summary>
    /// Loads one operation by its callback-safe random key.
    /// </summary>
    /// <param name="operationKey">Thirty-two-character random operation key received from Telegram or recovery.</param>
    /// <param name="cancellationToken">Token that cancels the database query.</param>
    /// <returns>The detached operation, or <c>null</c> when no row has that key.</returns>
    public async Task<XuiV3LinkChangeOperation> GetAsync(string operationKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
            return null;

        await using var context = _contextFactory.CreateDbContext();
        return await context.XuiV3LinkChangeOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken);
    }

    /// <summary>
    /// Atomically confirms an unexpired operation and grants the foreground callback an exclusive processing lease.
    /// </summary>
    /// <param name="operationKey">Operation key carried by the confirmation callback.</param>
    /// <param name="botId">Internal bot id that originally displayed the confirmation.</param>
    /// <param name="telegramUserId">Numeric Telegram user id that owns the operation.</param>
    /// <param name="cancellationToken">Token that cancels the atomic update.</param>
    /// <returns>
    /// A detached claim result. <see cref="XuiV3LinkChangeClaimResult.Claimed"/> is true only for the callback that won
    /// the atomic transition; all stale, unauthorized, expired, and duplicate callbacks receive false and must not
    /// start another mutation.
    /// </returns>
    public async Task<XuiV3LinkChangeClaimResult> ConfirmAsync(
        string operationKey,
        string botId,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.AddSeconds(GetLeaseSeconds());
        await using var context = _contextFactory.CreateDbContext();

        var affected = await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey &&
                        x.BotId == botId &&
                        x.TelegramUserId == telegramUserId &&
                        x.Status == XuiV3LinkChangeStatuses.AwaitingConfirmation &&
                        x.ConfirmationExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.Processing)
                .SetProperty(x => x.Stage, "confirmed")
                .SetProperty(x => x.ConfirmedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, leaseUntil)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);

        if (affected == 0)
        {
            await context.XuiV3LinkChangeOperations
                .Where(x => x.OperationKey == operationKey &&
                            x.Status == XuiV3LinkChangeStatuses.AwaitingConfirmation &&
                            x.ConfirmationExpiresAtUtc <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.Expired)
                    .SetProperty(x => x.Stage, "confirmation-expired")
                    .SetProperty(x => x.UpdatedAtUtc, now)
                    .SetProperty(x => x.CompletedAtUtc, now), cancellationToken);
        }

        var operation = await context.XuiV3LinkChangeOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken);
        return new XuiV3LinkChangeClaimResult(operation, affected == 1);
    }

    /// <summary>
    /// Cancels an unconfirmed operation owned by the current Telegram user and bot.
    /// </summary>
    /// <param name="operationKey">Operation key carried by the cancel callback.</param>
    /// <param name="botId">Internal bot id that owns the confirmation message.</param>
    /// <param name="telegramUserId">Numeric Telegram user id that requested cancellation.</param>
    /// <param name="cancellationToken">Token that cancels the database update.</param>
    /// <returns><c>true</c> only when an awaiting-confirmation row was closed without any panel mutation.</returns>
    public async Task<bool> CancelAsync(
        string operationKey,
        string botId,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var affected = await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey &&
                        x.BotId == botId &&
                        x.TelegramUserId == telegramUserId &&
                        x.Status == XuiV3LinkChangeStatuses.AwaitingConfirmation)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.Cancelled)
                .SetProperty(x => x.Stage, "cancelled")
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.CompletedAtUtc, now), cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Persists the immutable snapshot and one replacement identity before the first panel mutation.
    /// </summary>
    /// <param name="operationKey">Operation currently holding a processing lease.</param>
    /// <param name="oldEmail">Original XUI email captured from the live snapshot.</param>
    /// <param name="oldUuid">Original protocol UUID captured from the live snapshot.</param>
    /// <param name="oldSubId">Original subscription id captured from the live snapshot.</param>
    /// <param name="newEmail">Single generated replacement email reused by all retries.</param>
    /// <param name="newUuid">Single generated replacement UUID reused by all retries.</param>
    /// <param name="newSubId">Single generated replacement subscription id reused by all retries.</param>
    /// <param name="snapshotJson">Serialized immutable pre-change state; must not contain panel credentials.</param>
    /// <param name="payloadJson">Serialized complete update payload; must not contain panel credentials.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>The updated detached operation.</returns>
    /// <remarks>
    /// Existing generated values are never overwritten. This makes a retry after an ambiguous timeout target the same
    /// identity instead of renaming the account again.
    /// </remarks>
    public async Task<XuiV3LinkChangeOperation> SavePreparedMutationAsync(
        string operationKey,
        string oldEmail,
        string oldUuid,
        string oldSubId,
        string newEmail,
        string newUuid,
        string newSubId,
        string snapshotJson,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var operation = await context.XuiV3LinkChangeOperations
            .FirstOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken)
            ?? throw new InvalidOperationException("Link-change operation was not found.");

        if (string.IsNullOrWhiteSpace(operation.SnapshotJson))
        {
            operation.OldEmail = oldEmail;
            operation.OldUuid = oldUuid;
            operation.OldSubId = oldSubId;
            operation.NewEmail = newEmail;
            operation.NewUuid = newUuid;
            operation.NewSubId = newSubId;
            operation.SnapshotJson = snapshotJson;
            operation.PayloadJson = payloadJson;
        }

        operation.Stage = "snapshot-saved";
        operation.UpdatedAtUtc = now;
        operation.LeaseUntilUtc = now.AddSeconds(GetLeaseSeconds());
        await context.SaveChangesAsync(cancellationToken);
        return operation;
    }

    /// <summary>
    /// Updates one processing stage and optionally records that the saved identity was committed by XUI.
    /// </summary>
    /// <param name="operationKey">Persisted operation key.</param>
    /// <param name="stage">Non-secret stable stage key.</param>
    /// <param name="identityCommitted">Whether read-back proved the new identity belongs to the same numeric client.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>A task that completes after the stage and renewed lease are saved.</returns>
    public async Task UpdateStageAsync(
        string operationKey,
        string stage,
        bool identityCommitted,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Stage, stage)
                .SetProperty(x => x.IdentityCommitted, x => x.IdentityCommitted || identityCommitted)
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(GetLeaseSeconds())), cancellationToken);
    }

    /// <summary>
    /// Renews the exclusive lease of one processing operation without changing its stage or retry count.
    /// </summary>
    /// <param name="operationKey">Random users.db operation key currently being processed.</param>
    /// <param name="cancellationToken">Token that cancels the lightweight SQLite update.</param>
    /// <returns>
    /// <c>true</c> when the row still had <c>processing</c> status and its lease was renewed; <c>false</c> after the
    /// operation moved to recovery, success, cancellation, or another terminal status.
    /// </returns>
    /// <remarks>
    /// The foreground processor and recovery worker call this as a heartbeat while slow XUI reads are in progress.
    /// Keeping the lease alive prevents a second worker from claiming the same client during a long panel timeout.
    /// </remarks>
    public async Task<bool> RenewLeaseAsync(string operationKey, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var affected = await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey && x.Status == XuiV3LinkChangeStatuses.Processing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(GetLeaseSeconds()))
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Releases a failed or unknown operation for bounded background recovery without unlocking the client.
    /// </summary>
    /// <param name="operationKey">Persisted operation key.</param>
    /// <param name="stage">Stage that could not be completed.</param>
    /// <param name="safeError">Redacted diagnostic text that must not expose panel URLs or credentials.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>The updated detached operation, including its next retry time or manual-review status.</returns>
    public async Task<XuiV3LinkChangeOperation> ScheduleRecoveryAsync(
        string operationKey,
        string stage,
        string safeError,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var operation = await context.XuiV3LinkChangeOperations
            .FirstOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken)
            ?? throw new InvalidOperationException("Link-change operation was not found.");

        operation.Stage = stage;
        operation.LastError = Truncate(safeError, 2000);
        operation.UpdatedAtUtc = now;
        operation.LeaseUntilUtc = null;
        if (operation.AttemptCount >= GetRecoveryMaxAttempts())
        {
            operation.Status = XuiV3LinkChangeStatuses.ManualReview;
            operation.NextAttemptAtUtc = null;
        }
        else
        {
            operation.Status = XuiV3LinkChangeStatuses.RecoveryPending;
            operation.NextAttemptAtUtc = now.Add(GetRecoveryDelay(operation.AttemptCount));
        }

        await context.SaveChangesAsync(cancellationToken);
        return operation;
    }

    /// <summary>
    /// Closes an operation that could not reach the first panel mutation and therefore does not need recovery.
    /// </summary>
    /// <param name="operationKey">Persisted operation key.</param>
    /// <param name="stage">Pre-mutation stage that failed.</param>
    /// <param name="safeError">Redacted failure text safe for users.db diagnostics.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>A task that completes after the client lock is released.</returns>
    public async Task MarkFailedBeforeMutationAsync(
        string operationKey,
        string stage,
        string safeError,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.FailedBeforeMutation)
                .SetProperty(x => x.Stage, stage)
                .SetProperty(x => x.LastError, Truncate(safeError, 2000))
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.CompletedAtUtc, now), cancellationToken);
    }

    /// <summary>
    /// Marks final preservation success and releases the database client lock.
    /// </summary>
    /// <param name="operationKey">Persisted operation key whose final panel state was verified.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>A task that completes after the success transition is durable.</returns>
    public async Task MarkSucceededAsync(string operationKey, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.Succeeded)
                .SetProperty(x => x.Stage, "verified")
                .SetProperty(x => x.LastError, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.CompletedAtUtc, now), cancellationToken);
    }

    /// <summary>
    /// Claims a bounded page of due recovery rows with an exclusive lease.
    /// </summary>
    /// <param name="take">Maximum rows to claim in one worker pass; must be between one and fifty.</param>
    /// <param name="cancellationToken">Token that stops recovery during application shutdown.</param>
    /// <returns>Detached operations successfully claimed by this worker; the list may be empty.</returns>
    public async Task<IReadOnlyList<XuiV3LinkChangeOperation>> ClaimDueRecoveryAsync(
        int take,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var candidateCount = Math.Clamp(take, 1, 50);
        await using var context = _contextFactory.CreateDbContext();
        var keys = await context.XuiV3LinkChangeOperations
            .AsNoTracking()
            .Where(x =>
                (x.Status == XuiV3LinkChangeStatuses.RecoveryPending &&
                 (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now) &&
                 (!x.LeaseUntilUtc.HasValue || x.LeaseUntilUtc <= now)) ||
                (x.Status == XuiV3LinkChangeStatuses.Processing &&
                 x.LeaseUntilUtc.HasValue && x.LeaseUntilUtc <= now))
            .OrderBy(x => x.NextAttemptAtUtc ?? x.UpdatedAtUtc)
            .Select(x => x.OperationKey)
            .Take(candidateCount)
            .ToListAsync(cancellationToken);

        var claimed = new List<XuiV3LinkChangeOperation>();
        foreach (var key in keys)
        {
            var affected = await context.XuiV3LinkChangeOperations
                .Where(x => x.OperationKey == key &&
                            ((x.Status == XuiV3LinkChangeStatuses.RecoveryPending &&
                              (!x.LeaseUntilUtc.HasValue || x.LeaseUntilUtc <= now)) ||
                             (x.Status == XuiV3LinkChangeStatuses.Processing &&
                              x.LeaseUntilUtc.HasValue && x.LeaseUntilUtc <= now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.Processing)
                    .SetProperty(x => x.Stage, "recovery-claimed")
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(GetLeaseSeconds()))
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            if (affected != 1)
                continue;

            var operation = await context.XuiV3LinkChangeOperations
                .AsNoTracking()
                .FirstAsync(x => x.OperationKey == key, cancellationToken);
            claimed.Add(operation);
        }

        return claimed;
    }

    /// <summary>
    /// Requeues a manual-review or pending operation after the owner explicitly requests another status check.
    /// </summary>
    /// <param name="operationKey">Persisted operation key displayed in the progress message.</param>
    /// <param name="telegramUserId">Numeric Telegram owner id used as the authorization guard.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns><c>true</c> when the same operation was requeued; otherwise <c>false</c>.</returns>
    public async Task<bool> RequeueAsync(
        string operationKey,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var affected = await context.XuiV3LinkChangeOperations
            .Where(x => x.OperationKey == operationKey &&
                        x.TelegramUserId == telegramUserId &&
                        (x.Status == XuiV3LinkChangeStatuses.RecoveryPending ||
                         x.Status == XuiV3LinkChangeStatuses.ManualReview))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, XuiV3LinkChangeStatuses.RecoveryPending)
                .SetProperty(x => x.Stage, "manual-recheck-requested")
                .SetProperty(x => x.NextAttemptAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Marks one post-success side effect as completed so recovery cannot repeat it after a crash.
    /// </summary>
    /// <param name="operationKey">Succeeded operation key.</param>
    /// <param name="siteSyncQueued">Set to true after the website rename outbox has been queued.</param>
    /// <param name="auditLogged">Set to true after activity and private-channel success logs are emitted.</param>
    /// <param name="notificationSent">Set to true after the customer receives the final message.</param>
    /// <param name="cancellationToken">Token that cancels persistence.</param>
    /// <returns>A task that completes after the supplied flags are merged with existing true values.</returns>
    public async Task MarkPostActionsAsync(
        string operationKey,
        bool siteSyncQueued,
        bool auditLogged,
        bool notificationSent,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var operation = await context.XuiV3LinkChangeOperations
            .FirstOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken);
        if (operation == null)
            return;

        operation.SiteSyncQueued |= siteSyncQueued;
        operation.SuccessAuditLogged |= auditLogged;
        operation.SuccessNotificationSent |= notificationSent;
        operation.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Finds the active lock row for one panel client inside an existing EF context.
    /// </summary>
    /// <param name="context">Per-operation users.db context.</param>
    /// <param name="panelKey">Non-secret panel fingerprint.</param>
    /// <param name="clientId">Stable numeric XUI client id.</param>
    /// <param name="cancellationToken">Token that cancels the query.</param>
    /// <returns>The tracked active operation or <c>null</c>.</returns>
    private static Task<XuiV3LinkChangeOperation> FindActiveAsync(
        UserDbContext context,
        string panelKey,
        int clientId,
        CancellationToken cancellationToken)
    {
        return context.XuiV3LinkChangeOperations.FirstOrDefaultAsync(
            x => x.PanelKey == panelKey &&
                 x.ClientId == clientId &&
                 (x.Status == XuiV3LinkChangeStatuses.AwaitingConfirmation ||
                  x.Status == XuiV3LinkChangeStatuses.Processing ||
                  x.Status == XuiV3LinkChangeStatuses.RecoveryPending ||
                  x.Status == XuiV3LinkChangeStatuses.ManualReview),
            cancellationToken);
    }

    /// <summary>Gets the configured confirmation lifetime in minutes with safe bounds.</summary>
    /// <returns>A value between one and sixty minutes.</returns>
    private int GetConfirmationMinutes() => Math.Clamp(_appConfig.XuiV3LinkChangeConfirmationMinutes, 1, 60);

    /// <summary>Gets the configured operation lease in seconds with safe bounds.</summary>
    /// <returns>A value between sixty and 1800 seconds.</returns>
    private int GetLeaseSeconds() => Math.Clamp(_appConfig.XuiV3LinkChangeLeaseSeconds, 60, 1800);

    /// <summary>Gets the configured maximum number of foreground and recovery processing attempts.</summary>
    /// <returns>A value between one and one hundred.</returns>
    private int GetRecoveryMaxAttempts() => Math.Clamp(_appConfig.XuiV3LinkChangeRecoveryMaxAttempts, 1, 100);

    /// <summary>
    /// Calculates bounded exponential recovery delay from the persisted attempt count.
    /// </summary>
    /// <param name="attemptCount">One-based number of processing attempts already made.</param>
    /// <returns>Delay between thirty seconds and the configured maximum.</returns>
    private TimeSpan GetRecoveryDelay(int attemptCount)
    {
        var maximum = Math.Clamp(_appConfig.XuiV3LinkChangeRecoveryMaxDelaySeconds, 30, 86400);
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var seconds = Math.Min(maximum, 30 * (1 << exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Truncates persisted diagnostics to the configured column limit.</summary>
    /// <param name="value">Potentially long non-secret diagnostic text.</param>
    /// <param name="maximumLength">Maximum number of UTF-16 characters to retain.</param>
    /// <returns>The original string, a prefix, or <c>null</c>.</returns>
    private static string Truncate(string value, int maximumLength)
        => string.IsNullOrEmpty(value) || value.Length <= maximumLength ? value : value[..maximumLength];
}

/// <summary>
/// Reports the current persisted operation together with whether the caller won an atomic processing transition.
/// </summary>
/// <param name="Operation">Current detached operation, or <c>null</c> when the key does not exist.</param>
/// <param name="Claimed">
/// <c>true</c> only for the callback that changed awaiting-confirmation to processing; duplicate callbacks receive
/// <c>false</c> and must only display status.
/// </param>
public sealed record XuiV3LinkChangeClaimResult(
    XuiV3LinkChangeOperation Operation,
    bool Claimed);
