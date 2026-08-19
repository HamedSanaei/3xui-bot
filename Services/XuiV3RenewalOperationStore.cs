using System;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable users.db store that makes XUI v3 renewal processing exactly-once.
/// </summary>
/// <remarks>
/// The store owns the atomic transitions of <see cref="XuiV3RenewalOperation"/> rows: unique-key creation,
/// lease-bound processing claims, the single pending/processing to applied transition, the settlement guard, and the
/// read-only panel read-back that resolves ambiguous timeouts. Every transition is a conditional SQL UPDATE so
/// concurrent Telegram receivers, update redelivery, repeated confirm presses, and process restarts converge on one
/// mutation and one settlement. The store never sends another renewal mutation by itself; it only reports whether the
/// panel already holds the absolute target.
/// </remarks>
public class XuiV3RenewalOperationStore
{
    /// <summary>Lease duration for a processing claim, well above any single panel request timeout.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>Lease duration for a settlement claim before a crashed executor is treated as stale.</summary>
    private static readonly TimeSpan SettlementClaimLease = TimeSpan.FromMinutes(2);

    private readonly UserDbContextFactory _userDbContextFactory;
    private readonly ILogger<XuiV3RenewalOperationStore> _logger;

    /// <summary>
    /// Creates the renewal operation store.
    /// </summary>
    /// <param name="userDbContextFactory">
    /// Per-operation users.db context factory so each atomic transition owns an independent EF change tracker.
    /// </param>
    /// <param name="logger">Logger used only for local reconciliation diagnostics; nothing is forwarded to Telegram.</param>
    public XuiV3RenewalOperationStore(
        UserDbContextFactory userDbContextFactory,
        ILogger<XuiV3RenewalOperationStore> logger)
    {
        _userDbContextFactory = userDbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Draft values used to create a new renewal operation row.
    /// </summary>
    public sealed class RenewalOperationDraft
    {
        /// <summary>Stable deduplication key for this confirmation session or tenant order.</summary>
        public string OperationKey { get; set; }

        /// <summary>Runtime bot id owning the flow.</summary>
        public string BotId { get; set; }

        /// <summary>Tenant storefront id, or null for owned bots.</summary>
        public string TenantBotId { get; set; }

        /// <summary>Tenant order id, or null for owned bots.</summary>
        public string TenantBotOrderId { get; set; }

        /// <summary>Telegram user id of the payer or actor.</summary>
        public long TelegramUserId { get; set; }

        /// <summary>XUI client email being renewed.</summary>
        public string TargetEmail { get; set; }

        /// <summary>Normalized panel UUID lock when present; otherwise empty.</summary>
        public string TargetUuid { get; set; }

        /// <summary>Resolved service key.</summary>
        public string ServiceKey { get; set; }

        /// <summary>Exact traffic added in binary gigabytes.</summary>
        public int AddedTrafficGb { get; set; }

        /// <summary>Exact traffic added in bytes.</summary>
        public long AddedTrafficBytes { get; set; }

        /// <summary>Exact duration added in days.</summary>
        public int AddedDurationDays { get; set; }

        /// <summary>Renewal price in Iranian toman.</summary>
        public long PriceToman { get; set; }

        /// <summary>Selected payment method.</summary>
        public string PaymentMethod { get; set; }

        /// <summary>Expected pre-renewal TotalGB in bytes.</summary>
        public long ExpectedTotalBytesBefore { get; set; }

        /// <summary>Expected pre-renewal expiry in milliseconds.</summary>
        public long ExpectedExpiryTimeBefore { get; set; }

        /// <summary>Absolute XUI TotalGB target in bytes.</summary>
        public long TargetTotalBytes { get; set; }

        /// <summary>Absolute XUI expiry target in milliseconds.</summary>
        public long TargetExpiryTime { get; set; }

        /// <summary>Full replacement payload JSON sent to the panel.</summary>
        public string MutationPayloadJson { get; set; }

        /// <summary>Whether traffic counters must be reset after the panel update.</summary>
        public bool ShouldResetTraffic { get; set; }

        /// <summary>Whether unlimited renewal arithmetic was applied.</summary>
        public bool IsUnlimited { get; set; }
    }

    /// <summary>
    /// Outcome of a read-only panel read-back used to reconcile an ambiguous renewal.
    /// </summary>
    public enum RecoveryOutcome
    {
        /// <summary>The panel provably holds the absolute target values; the operation may be marked applied.</summary>
        Applied,

        /// <summary>The panel clearly does not hold the target yet; the mutation may be sent (take-over only).</summary>
        NotApplied,

        /// <summary>The panel could not be read; the operation must stay ambiguous and must not be mutated.</summary>
        Unavailable
    }

    /// <summary>
    /// Atomically inserts a new operation row or returns the existing row for the same key.
    /// </summary>
    /// <param name="draft">Draft values computed once before the XUI mutation.</param>
    /// <param name="cancellationToken">Token that cancels the users.db insert.</param>
    /// <returns>
    /// The persisted or existing operation and whether this call created it. When <c>Created</c> is false the caller
    /// must treat the returned row as a duplicate and branch on its status instead of mutating XUI again.
    /// </returns>
    /// <remarks>
    /// The unique index on <see cref="XuiV3RenewalOperation.OperationKey"/> is the database-level duplicate guard:
    /// concurrent duplicate confirmations race here and only one insert wins.
    /// </remarks>
    public async Task<(XuiV3RenewalOperation Operation, bool Created)> CreateOrGetAsync(
        RenewalOperationDraft draft,
        CancellationToken cancellationToken = default)
    {
        var operationId = "renew-" + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var row = new XuiV3RenewalOperation
        {
            OperationKey = draft.OperationKey,
            OperationId = operationId,
            BotId = draft.BotId,
            TenantBotId = draft.TenantBotId,
            TenantBotOrderId = draft.TenantBotOrderId,
            TelegramUserId = draft.TelegramUserId,
            TargetEmail = draft.TargetEmail,
            TargetUuid = draft.TargetUuid,
            ServiceKey = draft.ServiceKey,
            AddedTrafficGb = draft.AddedTrafficGb,
            AddedTrafficBytes = draft.AddedTrafficBytes,
            AddedDurationDays = draft.AddedDurationDays,
            PriceToman = draft.PriceToman,
            PaymentMethod = draft.PaymentMethod,
            ExpectedTotalBytesBefore = draft.ExpectedTotalBytesBefore,
            ExpectedExpiryTimeBefore = draft.ExpectedExpiryTimeBefore,
            TargetTotalBytes = draft.TargetTotalBytes,
            TargetExpiryTime = draft.TargetExpiryTime,
            MutationPayloadJson = draft.MutationPayloadJson,
            ShouldResetTraffic = draft.ShouldResetTraffic,
            IsUnlimited = draft.IsUnlimited,
            Status = XuiV3RenewalOperationStatuses.Pending,
            SettlementStatus = XuiV3RenewalSettlementStatuses.Pending,
            LeaseUntilUtc = now.Add(ClaimLease),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await using var context = _userDbContextFactory.CreateDbContext();
        context.XuiV3RenewalOperations.Add(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return (row, true);
        }
        catch (DbUpdateException)
        {
            // Unique OperationKey violation: a concurrent duplicate inserted first.
            context.ChangeTracker.Clear();
            var existing = await context.XuiV3RenewalOperations
                .AsNoTracking()
                .FirstAsync(x => x.OperationKey == draft.OperationKey, cancellationToken);
            return (existing, false);
        }
    }

    /// <summary>
    /// Loads one operation by its stable key.
    /// </summary>
    /// <param name="operationKey">Stable deduplication key.</param>
    /// <param name="cancellationToken">Token that cancels the users.db read.</param>
    /// <returns>The operation row, or null when no operation exists for the key.</returns>
    public async Task<XuiV3RenewalOperation> GetByKeyAsync(
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        return await context.XuiV3RenewalOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken);
    }

    /// <summary>
    /// Claims a freshly created pending operation for the creating executor.
    /// </summary>
    /// <param name="operation">Operation just created by this executor.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor now holds the processing lease and may send the XUI mutation; otherwise
    /// <c>false</c> because another executor already claimed the row.
    /// </returns>
    /// <remarks>
    /// A fresh row is only claimable while it is pending, so the creating executor wins unless a concurrent
    /// duplicate took over an already-expired pending lease first.
    /// </remarks>
    public async Task<bool> TryClaimFreshAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var claimToken = Guid.NewGuid().ToString("N");
        var leaseUntil = DateTime.UtcNow.Add(ClaimLease);
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.Status == XuiV3RenewalOperationStatuses.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Processing)
                    .SetProperty(x => x.LeaseUntilUtc, leaseUntil)
                    .SetProperty(x => x.ClaimToken, claimToken)
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);

        operation.Status = XuiV3RenewalOperationStatuses.Processing;
        operation.LeaseUntilUtc = leaseUntil;
        operation.ClaimToken = claimToken;
        operation.UpdatedAtUtc = DateTime.UtcNow;
        return updated == 1;
    }

    /// <summary>
    /// Claims an operation whose previous lease expired (crashed or abandoned executor).
    /// </summary>
    /// <param name="operation">Operation whose current status is pending or processing with an expired lease.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor now holds the processing lease; otherwise <c>false</c> because another
    /// executor holds a live lease.
    /// </returns>
    /// <remarks>
    /// The conditional update only matches pending rows and processing rows whose lease has expired, so two
    /// take-overs cannot both win. The caller must perform a read-only read-back before sending any mutation.
    /// </remarks>
    public async Task<bool> TryClaimStaleAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var claimToken = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var leaseUntil = now.Add(ClaimLease);
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        (x.Status == XuiV3RenewalOperationStatuses.Pending ||
                         (x.Status == XuiV3RenewalOperationStatuses.Processing &&
                          x.LeaseUntilUtc < now)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Processing)
                    .SetProperty(x => x.LeaseUntilUtc, leaseUntil)
                    .SetProperty(x => x.ClaimToken, claimToken)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        operation.Status = XuiV3RenewalOperationStatuses.Processing;
        operation.LeaseUntilUtc = leaseUntil;
        operation.ClaimToken = claimToken;
        operation.UpdatedAtUtc = now;
        return updated == 1;
    }

    /// <summary>
    /// Atomically transitions a claimed operation to applied.
    /// </summary>
    /// <param name="operation">Operation whose claim token this executor holds.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor performed the pending/processing to applied transition and is therefore the
    /// only executor allowed to run settlement and success logging; <c>false</c> when another executor already
    /// applied the operation.
    /// </returns>
    /// <remarks>
    /// The transition is guarded by the claim token and the pending/processing status, so at most one executor in
    /// the whole process (and across restarts) observes <c>true</c>.
    /// </remarks>
    public async Task<bool> MarkAppliedAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.ClaimToken == operation.ClaimToken &&
                        (x.Status == XuiV3RenewalOperationStatuses.Pending ||
                         x.Status == XuiV3RenewalOperationStatuses.Processing))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Applied)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        if (updated == 1)
        {
            operation.Status = XuiV3RenewalOperationStatuses.Applied;
            operation.UpdatedAtUtc = now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Atomically transitions an ambiguous operation to applied after a read-only read-back confirmed the target.
    /// </summary>
    /// <param name="operation">Operation currently in the ambiguous state whose panel read-back matched the target.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor performed the ambiguous to applied transition and is therefore the only
    /// executor allowed to run settlement and success logging; <c>false</c> when another reconciler already applied it.
    /// </returns>
    /// <remarks>
    /// Ambiguous operations carry no live processing claim, so this transition is guarded solely by the status value:
    /// the conditional update only matches rows that are still ambiguous, which makes the transition atomic across
    /// concurrent reconcilers. Callers must have just performed a read-only read-back that proved the panel holds
    /// the absolute target; this method never sends a mutation.
    /// </remarks>
    public async Task<bool> ResolveAmbiguousToAppliedAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.Status == XuiV3RenewalOperationStatuses.Ambiguous)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Applied)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        if (updated == 1)
        {
            operation.Status = XuiV3RenewalOperationStatuses.Applied;
            operation.UpdatedAtUtc = now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Marks an operation failed after a definitive, non-transient panel rejection.
    /// </summary>
    /// <param name="operation">Operation whose mutation was definitively rejected.</param>
    /// <param name="error">Sanitized error text without panel secrets.</param>
    /// <param name="cancellationToken">Token that cancels the update.</param>
    /// <returns>A task that completes after the failed status is persisted.</returns>
    /// <remarks>
    /// Failed operations are terminal: no settlement occurred and the mutation is never replayed. The user must
    /// start a fresh renewal flow for a new attempt.
    /// </remarks>
    public async Task MarkFailedAsync(
        XuiV3RenewalOperation operation,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.ClaimToken == operation.ClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Failed)
                    .SetProperty(x => x.LastError, Truncate(error))
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    /// <summary>
    /// Marks an operation ambiguous because the panel outcome could not be determined.
    /// </summary>
    /// <param name="operation">Operation whose mutation outcome is unknown.</param>
    /// <param name="error">Sanitized error text without panel secrets.</param>
    /// <param name="cancellationToken">Token that cancels the update.</param>
    /// <returns>A task that completes after the ambiguous status is persisted.</returns>
    /// <remarks>
    /// Ambiguous operations are never mutated again. A later confirmation performs a read-only read-back and either
    /// resolves the operation to applied or leaves it ambiguous for operator reconciliation.
    /// </remarks>
    public async Task MarkAmbiguousAsync(
        XuiV3RenewalOperation operation,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Ambiguous)
                    .SetProperty(x => x.LastError, Truncate(error))
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    /// <summary>
    /// Reads the panel client back by email and compares it with the operation's absolute target.
    /// </summary>
    /// <param name="operation">Operation holding the absolute target values.</param>
    /// <param name="serverInfo">Configured XUI v3 panel descriptor used only for the read-only request.</param>
    /// <param name="configuration">Runtime timeout and authentication configuration for the panel read.</param>
    /// <param name="cancellationToken">Token that cancels the read-only panel request.</param>
    /// <returns>
    /// The recovery outcome plus the fresh panel client when the read succeeded. The client is
    /// <see cref="RecoveryOutcome.Applied"/> only when the panel holds at least the target quota and the expected
    /// expiry representation; <see cref="RecoveryOutcome.NotApplied"/> when the panel is clearly below the target;
    /// <see cref="RecoveryOutcome.Unavailable"/> when the panel could not be read (client is null).
    /// </returns>
    /// <remarks>
    /// This is the only recovery mechanism: it never sends another mutation. A timeout or failed read is always
    /// <see cref="RecoveryOutcome.Unavailable"/>, which keeps the operation ambiguous.
    /// </remarks>
    public async Task<(RecoveryOutcome Outcome, XuiV3Client Client)> RecoverByReadBackAsync(
        XuiV3RenewalOperation operation,
        ServerInfo serverInfo,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await ApiServicev3.GetClientAsync(serverInfo, configuration, operation.TargetEmail, cancellationToken);
            if (!response.Success || response.Obj == null)
            {
                _logger.LogWarning(
                    "Renewal operation read-back found no client. operationId={OperationId}, success={Success}",
                    operation.OperationId,
                    response.Success);
                return (RecoveryOutcome.Unavailable, null);
            }

            return IsTargetReached(response.Obj, operation)
                ? (RecoveryOutcome.Applied, response.Obj)
                : (RecoveryOutcome.NotApplied, response.Obj);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Renewal operation read-back failed; keeping the operation ambiguous. operationId={OperationId}",
                operation.OperationId);
            return (RecoveryOutcome.Unavailable, null);
        }
    }

    /// <summary>
    /// Determines whether a panel client already holds the absolute target of a renewal operation.
    /// </summary>
    /// <param name="client">Fresh client read from the panel.</param>
    /// <param name="operation">Operation whose target quota and expiry are compared.</param>
    /// <returns>
    /// <c>true</c> when the client's total quota equals or exceeds the target and its expiry matches or exceeds the
    /// target in the expected representation; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Negative expiries represent first-connection durations and are compared exactly; positive expiries are
    /// absolute timestamps and are compared as greater-or-equal. The quota check is the primary signal: once the
    /// absolute target quota is present, the renewal is considered applied and is never added to again.
    /// </remarks>
    public static bool IsTargetReached(XuiV3Client client, XuiV3RenewalOperation operation)
    {
        if (client == null || operation == null)
            return false;

        var totalBytes = ReadTotalBytes(client);
        if (totalBytes < operation.TargetTotalBytes)
            return false;

        var expiry = ReadExpiryTime(client);
        if (operation.TargetExpiryTime < 0)
            return expiry == operation.TargetExpiryTime;

        if (operation.TargetExpiryTime == 0)
            return true;

        return expiry >= operation.TargetExpiryTime;
    }

    // ------------------------------------------------------------------
    // Settlement guards
    // ------------------------------------------------------------------

    /// <summary>
    /// Atomically claims the settlement for an operation.
    /// </summary>
    /// <param name="operation">Operation whose settlement status is still pending.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor holds the settlement claim and may debit the wallet; <c>false</c> when another
    /// executor is settling or settlement already finished.
    /// </returns>
    /// <remarks>
    /// Only one executor can ever observe <c>true</c> for the same operation, which is what makes the wallet debit
    /// exactly-once across concurrent duplicates and take-overs.
    /// </remarks>
    public async Task<bool> TryClaimSettlementAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.SettlementStatus == XuiV3RenewalSettlementStatuses.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.SettlementStatus, XuiV3RenewalSettlementStatuses.Settling)
                    .SetProperty(x => x.SettlementStartedAtUtc, now)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Resolves the settlement state when the settlement claim could not be acquired.
    /// </summary>
    /// <param name="operation">Operation whose settlement is being reconciled.</param>
    /// <param name="cancellationToken">Token that cancels the users.db read.</param>
    /// <returns>
    /// The current persisted settlement status. A status of <see cref="XuiV3RenewalSettlementStatuses.Settling"/>
    /// whose claim started more than <see cref="SettlementClaimLease"/> ago is treated as a crashed executor and is
    /// parked in <see cref="XuiV3RenewalSettlementStatuses.ManualReview"/> so it is never automatically resumed.
    /// </returns>
    /// <remarks>
    /// A stale settling claim is never auto-resumed: the previous executor may already have debited the wallet, and
    /// automatically debiting again would double-charge the customer. The operation is parked for operator review.
    /// </remarks>
    public async Task<string> ResolveSettlementStateAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        var persisted = await context.XuiV3RenewalOperations
            .AsNoTracking()
            .FirstAsync(x => x.OperationKey == operation.OperationKey, cancellationToken);

        if (persisted.SettlementStatus == XuiV3RenewalSettlementStatuses.Settling &&
            persisted.SettlementStartedAtUtc.HasValue &&
            persisted.SettlementStartedAtUtc.Value < DateTime.UtcNow.Subtract(SettlementClaimLease))
        {
            await context.XuiV3RenewalOperations
                .Where(x => x.OperationKey == operation.OperationKey &&
                            x.SettlementStatus == XuiV3RenewalSettlementStatuses.Settling)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.SettlementStatus, XuiV3RenewalSettlementStatuses.ManualReview)
                        .SetProperty(x => x.LastError, "Settlement executor crashed mid-settlement; manual review required.")
                        .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken);
            return XuiV3RenewalSettlementStatuses.ManualReview;
        }

        return persisted.SettlementStatus;
    }

    /// <summary>
    /// Marks the settlement of an operation as completed.
    /// </summary>
    /// <param name="operation">Operation whose wallet debit and ledger write finished.</param>
    /// <param name="cancellationToken">Token that cancels the update.</param>
    /// <returns>A task that completes after the settled status is persisted.</returns>
    public async Task MarkSettledAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.SettlementStatus, XuiV3RenewalSettlementStatuses.Settled)
                    .SetProperty(x => x.SettledAtUtc, now)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        operation.SettlementStatus = XuiV3RenewalSettlementStatuses.Settled;
        operation.SettledAtUtc = now;
    }

    /// <summary>
    /// Marks the single central success log as sent for an operation.
    /// </summary>
    /// <param name="operation">Operation whose renewal succeeded or was recovered.</param>
    /// <param name="cancellationToken">Token that cancels the update.</param>
    /// <returns>
    /// <c>true</c> when this executor is the first to mark the success log as sent and may therefore emit the single
    /// Telegram logger entry; <c>false</c> when the log was already sent by a previous executor.
    /// </returns>
    /// <remarks>
    /// Used together with the atomic applied transition to guarantee exactly one central Telegram success log per
    /// renewal operation even when a take-over resolves the operation after a crash.
    /// </remarks>
    public async Task<bool> MarkSuccessLogSentAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey && x.SuccessLogSentAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.SuccessLogSentAtUtc, now)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Creates or returns the wallet ledger row for one renewal settlement using the operation id as the
    /// idempotency key.
    /// </summary>
    /// <param name="operation">Operation whose settlement is being recorded.</param>
    /// <param name="telegramUserId">Telegram user id of the wallet owner being debited.</param>
    /// <param name="amountToman">Positive debit amount in Iranian toman.</param>
    /// <param name="provider">Ledger provider such as <c>wallet</c> or <c>gozargah_site_wallet_fallback_bot_wallet</c>.</param>
    /// <param name="referenceId">XUI client email used as the ledger reference.</param>
    /// <param name="description">Human-readable ledger description.</param>
    /// <param name="beforeBalance">Wallet balance in toman immediately before the debit.</param>
    /// <param name="afterBalance">
    /// Expected wallet balance after the debit. For the deterministic <c>Pay</c> mutation this is exactly
    /// <c>beforeBalance - amountToman</c> and is written before the debit so a crash can never double-charge.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the users.db insert.</param>
    /// <returns>
    /// The ledger row and whether this call inserted it. When <c>Existed</c> is true a previous executor already
    /// created the row and the caller must reconcile the wallet balance instead of debiting blindly.
    /// </returns>
    /// <remarks>
    /// The unique wallet-ledger idempotency index is the final duplicate guard shared with
    /// <see cref="WalletLedgerService"/>: writing the final-form row before the wallet debit makes a second debit
    /// impossible because any later executor sees the row and reconciles instead of paying again.
    /// </remarks>
    public async Task<(WalletLedgerEntry Row, bool Existed)> EnsureSettlementLedgerAsync(
        XuiV3RenewalOperation operation,
        long telegramUserId,
        long amountToman,
        string provider,
        string referenceId,
        string description,
        long beforeBalance,
        long afterBalance,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = BuildSettlementLedgerKey(operation);
        await using var context = _userDbContextFactory.CreateDbContext();
        var existing = await context.WalletLedgerEntries
            .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing != null)
            return (existing, true);

        var row = new WalletLedgerEntry
        {
            BotId = BotContextAccessor.CurrentBotId,
            BotUsername = BotContextAccessor.CurrentBotUsername,
            BotType = BotContextAccessor.CurrentBotType,
            TelegramUserId = telegramUserId,
            Direction = WalletLedgerDirections.Debit,
            AmountToman = amountToman,
            BalanceBefore = beforeBalance,
            BalanceAfter = afterBalance,
            Reason = WalletLedgerReasons.AccountRenew,
            Provider = provider,
            ReferenceType = "xui-v3-client",
            ReferenceId = referenceId,
            Description = description,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = DateTime.UtcNow
        };

        context.WalletLedgerEntries.Add(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return (row, false);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            var concurrent = await context.WalletLedgerEntries
                .AsNoTracking()
                .FirstAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            return (concurrent, true);
        }
    }

    /// <summary>
    /// Writes the real post-debit balance onto a settlement ledger row created by
    /// <see cref="EnsureSettlementLedgerAsync"/>.
    /// </summary>
    /// <param name="ledgerRowId">Internal users.db id of the pre-inserted ledger row.</param>
    /// <param name="afterBalance">Actual wallet balance after the debit completed.</param>
    /// <param name="cancellationToken">Token that cancels the update.</param>
    /// <returns>A task that completes after the balance update is persisted.</returns>
    /// <remarks>
    /// The deterministic <c>Pay</c> debit always results in exactly <c>before - amount</c>, so this update is a
    /// verification write; the row is already in final form, which is what makes the pre-insert crash-safe.
    /// </remarks>
    public async Task FinalizeSettlementLedgerAsync(
        int ledgerRowId,
        long afterBalance,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.WalletLedgerEntries
            .Where(x => x.Id == ledgerRowId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.BalanceAfter, afterBalance),
                cancellationToken);
    }

    /// <summary>
    /// Builds the stable wallet-ledger idempotency key for one renewal operation.
    /// </summary>
    /// <param name="operation">Operation whose ledger key is requested.</param>
    /// <returns>A stable key shorter than the wallet-ledger column limit.</returns>
    public static string BuildSettlementLedgerKey(XuiV3RenewalOperation operation)
    {
        return "renew:" + (operation?.OperationId ?? string.Empty);
    }

    private static long ReadTotalBytes(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.TotalGB > 0)
            return client.TotalGB;

        if (client.Traffic?.TotalGB > 0)
            return client.Traffic.TotalGB;

        if (client.Traffic?.Total > 0)
            return client.Traffic.Total;

        return ReadLongExtra(client, "totalGB");
    }

    private static long ReadExpiryTime(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        if (client.Traffic?.ExpiryTime != 0)
            return client.Traffic?.ExpiryTime ?? 0;

        return ReadLongExtra(client, "expiryTime");
    }

    private static long ReadLongExtra(XuiV3Client client, string key)
    {
        if (client?.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null)
            return 0;

        try
        {
            return token.ToObject<long>();
        }
        catch
        {
            return 0;
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Length <= 2000 ? value : value[..2000];
    }
}
