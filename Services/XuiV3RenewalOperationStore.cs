using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

    /// <summary>Lease duration for one background GET-only reconciliation attempt.</summary>
    public static readonly TimeSpan RecoveryLease = TimeSpan.FromMinutes(2);

    /// <summary>Maximum automatic GET-only attempts before an inconclusive operation requires manual review.</summary>
    public const int MaximumAutomaticReconcileAttempts = 12;

    /// <summary>Maximum age of an ambiguous operation before automatic recovery escalates it to manual review.</summary>
    public static readonly TimeSpan MaximumAutomaticReconcileAge = TimeSpan.FromHours(24);

    /// <summary>Minimum successful unchanged observations before a timed-out mutation can be declared not applied.</summary>
    public const int MinimumPreMutationObservations = 3;

    /// <summary>Conservative time over which an unchanged client must be observed before releasing its renewal lock.</summary>
    public static readonly TimeSpan PreMutationCommitGrace = TimeSpan.FromMinutes(10);

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

        /// <summary>
        /// Snapshot of the renewal-controlled fields read before the mutation. It must be produced by
        /// <see cref="BuildPreMutationSnapshotJson"/> from the same fresh client used to calculate the target.
        /// </summary>
        public string PreMutationSnapshotJson { get; set; }

        /// <summary>Whether traffic counters must be reset after the panel update.</summary>
        public bool ShouldResetTraffic { get; set; }

        /// <summary>Whether unlimited renewal arithmetic was applied.</summary>
        public bool IsUnlimited { get; set; }

        /// <summary>
        /// Positive inbound ids observed before renewal for audit compatibility. Renewal does not modify attachment
        /// membership, so this collection is never a mandatory reconciliation success criterion.
        /// </summary>
        public IReadOnlyCollection<int> ExpectedInboundIds { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// Detailed result of comparing one fresh panel client with the durable pre-mutation snapshot and absolute target.
    /// </summary>
    public sealed class RenewalComparisonResult
    {
        /// <summary>High-level state proven by the available read-only evidence.</summary>
        public RecoveryOutcome Outcome { get; init; }

        /// <summary>
        /// Sanitized semicolon-separated field relations suitable for users.db and operational logs.
        /// </summary>
        public string Summary { get; init; }
    }

    /// <summary>
    /// Minimal renewal-controlled panel state retained before mutation so a delayed timeout can be classified safely.
    /// </summary>
    private sealed class RenewalClientSnapshot
    {
        /// <summary>Quota in bytes before mutation.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Expiry representation in panel milliseconds before mutation.</summary>
        public long ExpiryTime { get; set; }

        /// <summary>Client enabled state before mutation.</summary>
        public bool Enable { get; set; }

        /// <summary>Panel Telegram owner before mutation; compared only when renewal intentionally changes it.</summary>
        public long TgId { get; set; }

        /// <summary>Raw metadata before mutation; comparisons parse JSON semantically when possible.</summary>
        public string Comment { get; set; }
    }

    /// <summary>
    /// Raised when another unresolved mutation or settlement already owns the account-level renewal lock.
    /// </summary>
    /// <remarks>
    /// The exception carries only a non-secret operation id. Callers should show a generic temporary-lock message and
    /// must not retry <c>POST /UpdateClient</c> for the newly requested renewal.
    /// </remarks>
    public sealed class AccountRenewalLockedException : InvalidOperationException
    {
        /// <summary>Creates an account-lock exception for the existing unresolved operation.</summary>
        /// <param name="operationId">Non-secret durable operation id that currently owns or represents the lock.</param>
        public AccountRenewalLockedException(string operationId)
            : base("The XUI account has an unresolved renewal operation.")
        {
            OperationId = operationId;
        }

        /// <summary>Non-secret durable id of the operation that blocks the new renewal.</summary>
        public string OperationId { get; }
    }

    /// <summary>
    /// Normalizes an XUI account email for durable lock comparison.
    /// </summary>
    /// <param name="email">Panel client email; null and whitespace are allowed and normalize to an empty string.</param>
    /// <returns>Trimmed lowercase invariant email, or an empty string when no usable email was supplied.</returns>
    /// <remarks>The normalized value is database-only identity data and must not be written to callbacks or logs.</remarks>
    /// <example><code>NormalizeEmail(" Demo@Example ") == "demo@example"</code></example>
    public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Normalizes a panel UUID for primary account-lock comparison.
    /// </summary>
    /// <param name="uuid">Raw UUID from the freshly loaded panel client; null or invalid values are allowed.</param>
    /// <returns>Canonical lowercase UUID, or an empty string when the value is not a valid UUID.</returns>
    /// <remarks>UUID is preferred over email because account email may be edited without rebuilding the client.</remarks>
    /// <example><code>NormalizeUuid("550E8400-E29B-41D4-A716-446655440000")</code></example>
    public static string NormalizeUuid(string uuid) =>
        Guid.TryParse((uuid ?? string.Empty).Trim(), out var parsed)
            ? parsed.ToString("D").ToLowerInvariant()
            : string.Empty;

    /// <summary>
    /// Builds the nullable unique lock key held by an unresolved renewal operation.
    /// </summary>
    /// <param name="uuid">Raw or normalized panel UUID; preferred when valid.</param>
    /// <param name="email">Raw or normalized client email used only when UUID is unavailable.</param>
    /// <returns>A stable UUID/email-prefixed key, or null when neither identity is usable.</returns>
    /// <remarks>The returned value is sensitive database identity data and must never be logged.</remarks>
    /// <example><code>BuildAccountLockKey(client.Uuid, client.Email)</code></example>
    public static string BuildAccountLockKey(string uuid, string email)
    {
        var normalizedUuid = NormalizeUuid(uuid);
        if (!string.IsNullOrEmpty(normalizedUuid))
            return "uuid:" + normalizedUuid;

        var normalizedEmail = NormalizeEmail(email);
        return string.IsNullOrEmpty(normalizedEmail) ? null : "email:" + normalizedEmail;
    }

    /// <summary>
    /// Outcome of a detailed read-only comparison between a panel client, its pre-mutation snapshot, and target.
    /// </summary>
    public enum RecoveryOutcome
    {
        /// <summary>The panel provably holds the absolute target values; the operation may be marked applied.</summary>
        Applied,

        /// <summary>
        /// Every controlled field still exactly matches the stored pre-mutation snapshot. Several observations across
        /// the commit-grace window are required before the operation can be failed and unlocked.
        /// </summary>
        DefinitelyPreMutation,

        /// <summary>
        /// At least one controlled field shows the target mutation while another required field does not. Settlement
        /// is forbidden and the account must stay locked for manual review.
        /// </summary>
        PartiallyApplied,

        /// <summary>
        /// Identity or controlled state differs from both the stored pre-mutation snapshot and complete target without
        /// proving this mutation. The account remains locked for manual review.
        /// </summary>
        Drifted,

        /// <summary>The panel or required comparison evidence was unavailable; the operation stays locked.</summary>
        Unavailable
    }

    /// <summary>Durable action taken after persisting one background comparison result.</summary>
    public enum ReconciliationDisposition
    {
        /// <summary>Another GET-only attempt was scheduled and the account remains locked.</summary>
        RetryScheduled,

        /// <summary>The operation moved to manual review and remains locked.</summary>
        ManualReview,

        /// <summary>Repeated unchanged observations proved NotApplied; settlement stayed untouched and lock cleared.</summary>
        DefinitivelyFailed,

        /// <summary>The recovery lease was lost or the row was no longer eligible; no transition occurred.</summary>
        NoChange
    }

    /// <summary>
    /// Serializes the exact renewal-controlled pre-mutation state without retaining passwords, SubIds, inbound
    /// attachments, links, or other credentials.
    /// </summary>
    /// <param name="client">
    /// Fresh panel client used to calculate the renewal. It is required and must already have exact UUID/email identity.
    /// </param>
    /// <returns>Compact JSON suitable for <see cref="XuiV3RenewalOperation.PreMutationSnapshotJson"/>.</returns>
    /// <remarks>
    /// The snapshot has no external side effects. Recovery may use it to release a lock only after several successful
    /// GETs over <see cref="PreMutationCommitGrace"/> all prove the controlled state is unchanged.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    /// <example><code>draft.PreMutationSnapshotJson = BuildPreMutationSnapshotJson(client);</code></example>
    public static string BuildPreMutationSnapshotJson(XuiV3Client client)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));

        return JsonConvert.SerializeObject(new RenewalClientSnapshot
        {
            TotalBytes = ReadTotalBytes(client),
            ExpiryTime = ReadExpiryTime(client),
            Enable = client.Enable,
            TgId = client.TgId,
            Comment = client.Comment
        }, Formatting.None);
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
        var normalizedUuid = NormalizeUuid(draft.TargetUuid);
        var normalizedEmail = NormalizeEmail(draft.TargetEmail);
        var accountLockKey = BuildAccountLockKey(normalizedUuid, normalizedEmail);
        if (string.IsNullOrEmpty(accountLockKey))
            throw new ArgumentException("A renewal operation requires a valid target UUID or email.", nameof(draft));

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
            NormalizedTargetEmail = normalizedEmail,
            NormalizedTargetUuid = normalizedUuid,
            AccountLockKey = accountLockKey,
            RecoveryEligible = true,
            ExpectedInboundIdsJson = JsonConvert.SerializeObject(
                (draft.ExpectedInboundIds ?? Array.Empty<int>())
                    .Where(x => x > 0)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray()),
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
            PreMutationSnapshotJson = draft.PreMutationSnapshotJson,
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
            // Either the same confirmation key or the same account lock won concurrently. Resolve both cases from
            // users.db; never infer that a failed insert permits a panel mutation.
            context.ChangeTracker.Clear();
            var existing = await context.XuiV3RenewalOperations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OperationKey == draft.OperationKey, cancellationToken);
            if (existing != null)
                return (existing, false);

            var blocker = await FindBlockingOperationCoreAsync(
                context,
                normalizedUuid,
                normalizedEmail,
                excludedOperationKey: null,
                cancellationToken);
            throw new AccountRenewalLockedException(blocker?.OperationId ?? "unresolved");
        }
    }

    /// <summary>
    /// Finds an unresolved renewal for the exact UUID, with normalized email as a compatibility fallback.
    /// </summary>
    /// <param name="targetUuid">Fresh panel UUID. Invalid or empty values cause email-only matching.</param>
    /// <param name="targetEmail">Fresh panel email used as fallback and to cover historical rows.</param>
    /// <param name="excludedOperationKey">Optional current confirmation key that must not block itself.</param>
    /// <param name="cancellationToken">Token that cancels the users.db read.</param>
    /// <returns>
    /// The oldest detached unresolved operation for the account, or null when a new renewal may acquire the lock.
    /// </returns>
    /// <remarks>
    /// Pending, processing, ambiguous, manual-review, and applied-but-unsettled rows block. Applied-and-settled and
    /// definitively failed rows do not. The database unique lock remains the final concurrency guard after this read.
    /// </remarks>
    /// <example><code>await store.FindBlockingOperationAsync(client.Uuid, client.Email, key, token)</code></example>
    public async Task<XuiV3RenewalOperation> FindBlockingOperationAsync(
        string targetUuid,
        string targetEmail,
        string excludedOperationKey = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        return await FindBlockingOperationCoreAsync(
            context,
            NormalizeUuid(targetUuid),
            NormalizeEmail(targetEmail),
            excludedOperationKey,
            cancellationToken);
    }

    /// <summary>
    /// Executes the account-level unresolved lookup on an existing users.db context.
    /// </summary>
    /// <param name="context">Independent users.db context owned by the caller.</param>
    /// <param name="normalizedUuid">Canonical UUID or empty string.</param>
    /// <param name="normalizedEmail">Canonical email or empty string.</param>
    /// <param name="excludedOperationKey">Optional operation key excluded from the result.</param>
    /// <param name="cancellationToken">Token that cancels the query.</param>
    /// <returns>The oldest detached blocking row, or null.</returns>
    /// <remarks>Called by both the preflight read and the unique-index collision path on independent contexts.</remarks>
    private static Task<XuiV3RenewalOperation> FindBlockingOperationCoreAsync(
        UserDbContext context,
        string normalizedUuid,
        string normalizedEmail,
        string excludedOperationKey,
        CancellationToken cancellationToken)
    {
        return context.XuiV3RenewalOperations
            .AsNoTracking()
            .Where(x => excludedOperationKey == null || x.OperationKey != excludedOperationKey)
            .Where(x =>
                x.Status == XuiV3RenewalOperationStatuses.Pending ||
                x.Status == XuiV3RenewalOperationStatuses.Processing ||
                x.Status == XuiV3RenewalOperationStatuses.Ambiguous ||
                x.Status == XuiV3RenewalOperationStatuses.ManualReview ||
                (x.Status == XuiV3RenewalOperationStatuses.Applied &&
                 x.SettlementStatus != XuiV3RenewalSettlementStatuses.Settled))
            .Where(x =>
                (!string.IsNullOrEmpty(normalizedUuid) && x.NormalizedTargetUuid == normalizedUuid) ||
                (!string.IsNullOrEmpty(normalizedEmail) &&
                 (string.IsNullOrEmpty(normalizedUuid) || string.IsNullOrEmpty(x.NormalizedTargetUuid)) &&
                 x.NormalizedTargetEmail == normalizedEmail))
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
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
    /// Claims an abandoned pending operation that has never started its panel mutation.
    /// </summary>
    /// <param name="operation">Pending operation whose mutation-start marker is still null.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor now holds the processing lease; otherwise <c>false</c> because another
    /// executor already claimed it or the mutation-start marker proves the POST may have begun.
    /// </returns>
    /// <remarks>
    /// Processing operations are deliberately excluded even after lease expiry: the previous POST outcome may be
    /// delayed, so replaying it would risk a duplicate renewal. Such rows are recovered by GET only.
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
                        x.Status == XuiV3RenewalOperationStatuses.Pending &&
                        x.MutationStartedAtUtc == null &&
                        x.LeaseUntilUtc < now)
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
    /// Persists the irreversible boundary immediately before the one allowed renewal mutation is sent.
    /// </summary>
    /// <param name="operation">Processing operation whose claim token is held by the current executor.</param>
    /// <param name="cancellationToken">Token that cancels the conditional users.db update.</param>
    /// <returns>
    /// <c>true</c> only for the executor that records the first mutation start and may call
    /// <c>POST /UpdateClient</c>; <c>false</c> means no mutation may be sent.
    /// </returns>
    /// <remarks>
    /// The marker is written before network I/O. A crash after this write is treated as ambiguous even if the request
    /// may not have left the process; safety prefers a locked manual review over a possible duplicate mutation.
    /// </remarks>
    /// <example><code>if (await store.MarkMutationStartedAsync(operation, token)) await SendOnceAsync();</code></example>
    public async Task<bool> MarkMutationStartedAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.Status == XuiV3RenewalOperationStatuses.Processing &&
                        x.ClaimToken == operation.ClaimToken &&
                        x.MutationStartedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.MutationStartedAtUtc, now)
                    .SetProperty(x => x.NextReconcileAtUtc, now.AddSeconds(15))
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        if (updated == 1)
        {
            operation.MutationStartedAtUtc = now;
            operation.NextReconcileAtUtc = now.AddSeconds(15);
        }

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
    /// Atomically transitions an ambiguous or stale-processing operation to applied after GET confirmed the target.
    /// </summary>
    /// <param name="operation">Ambiguous or stale-processing operation whose panel read-back matched the target.</param>
    /// <param name="comparison">Applied comparison whose sanitized field summary is persisted with the transition.</param>
    /// <param name="cancellationToken">Token that cancels the conditional update.</param>
    /// <returns>
    /// <c>true</c> when this executor performed the ambiguous to applied transition and is therefore the only
    /// executor allowed to run settlement and success logging; <c>false</c> when another reconciler already applied it.
    /// </returns>
    /// <remarks>
    /// The conditional update accepts ambiguous rows and stale processing rows claimed by background recovery. Callers
    /// must have just performed a read-only read-back proving the absolute target; this method never sends a mutation.
    /// </remarks>
    public async Task<bool> ResolveAmbiguousToAppliedAsync(
        XuiV3RenewalOperation operation,
        RenewalComparisonResult comparison,
        CancellationToken cancellationToken = default)
    {
        if (comparison?.Outcome != RecoveryOutcome.Applied)
            throw new ArgumentException("An Applied comparison is required.", nameof(comparison));

        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        (x.Status == XuiV3RenewalOperationStatuses.Ambiguous ||
                         x.Status == XuiV3RenewalOperationStatuses.Processing))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Applied)
                    .SetProperty(x => x.LastComparisonOutcome, comparison.Outcome.ToString())
                    .SetProperty(x => x.LastMismatchSummary, comparison.Summary)
                    .SetProperty(x => x.LastReconcileAtUtc, now)
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
    /// <param name="comparison">Optional immediate GET-only comparison whose sanitized evidence should be retained.</param>
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
                    .SetProperty(x => x.AccountLockKey, (string)null)
                    .SetProperty(x => x.RecoveryLeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.RecoveryClaimToken, (string)null)
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
        CancellationToken cancellationToken = default,
        RenewalComparisonResult comparison = null)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.Status == XuiV3RenewalOperationStatuses.Processing &&
                        x.ClaimToken == operation.ClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Ambiguous)
                    .SetProperty(x => x.NextReconcileAtUtc, now.AddSeconds(15))
                    .SetProperty(x => x.RecoveryLeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.RecoveryClaimToken, (string)null)
                    .SetProperty(x => x.LastComparisonOutcome, comparison == null ? null : comparison.Outcome.ToString())
                    .SetProperty(x => x.LastMismatchSummary, comparison == null ? null : comparison.Summary)
                    .SetProperty(x => x.LastError, Truncate(error))
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
    }

    /// <summary>
    /// Reads an ambiguous renewal target using GET only and returns a detailed, identity-safe comparison.
    /// </summary>
    /// <param name="operation">Recovery-eligible operation holding pre-mutation and absolute target evidence.</param>
    /// <param name="serverInfo">Configured XUI v3 panel descriptor used only for authenticated read requests.</param>
    /// <param name="configuration">Runtime timeout and read-only retry configuration.</param>
    /// <param name="cancellationToken">Token that cancels panel reads.</param>
    /// <returns>
    /// A sanitized comparison and the exact fresh client when available. The client is null for unavailable or
    /// ambiguous identity results. Callers must persist the comparison before deciding settlement or backoff.
    /// </returns>
    /// <remarks>
    /// The direct email endpoint is accepted only when its returned identity matches the operation. A mismatched or
    /// absent direct result falls back to the complete client list and requires exactly one UUID match (email fallback
    /// only for operations without UUID). A single same-email/different-UUID row is classified Drifted as a rebuilt
    /// account. This protects against panels that return an unrelated client with HTTP 200. No path sends, reconstructs,
    /// or replays a mutation.
    /// </remarks>
    /// <example><code>var (comparison, client) = await store.RecoverByReadBackAsync(op, panel, config, token);</code></example>
    public async Task<(RenewalComparisonResult Comparison, XuiV3Client Client)> RecoverByReadBackAsync(
        XuiV3RenewalOperation operation,
        ServerInfo serverInfo,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await ApiServicev3.GetClientAsync(serverInfo, configuration, operation.TargetEmail, cancellationToken);
            var directReturnedClient = response.Success && response.Obj != null;
            var directIdentityMatched = directReturnedClient && ResponseIdentityMatches(response.Obj, operation);
            var client = directIdentityMatched
                ? response.Obj
                : null;
            var readSource = directIdentityMatched
                ? "direct"
                : directReturnedClient
                    ? "list-after-direct-identity-mismatch"
                    : "list-after-direct-miss";
            if (client == null)
            {
                // A successful direct GET can still contain an unrelated client on affected panel builds. Never feed
                // that object into target comparison; resolve the stored UUID from the complete read-only list.
                var listResponse = await ApiServicev3.GetClientsAsync(serverInfo, configuration, cancellationToken);
                if (!listResponse.Success)
                    return (Unavailable("identity=unavailable;read=list-failed"), null);

                var normalizedUuid = NormalizeUuid(operation.TargetUuid);
                var normalizedEmail = NormalizeEmail(operation.TargetEmail);
                var candidates = listResponse.Obj ?? new List<XuiV3Client>();
                var matches = candidates
                    .Where(x => !string.IsNullOrEmpty(normalizedUuid)
                        ? string.Equals(NormalizeUuid(x.Uuid), normalizedUuid, StringComparison.Ordinal)
                        : string.Equals(NormalizeEmail(x.Email), normalizedEmail, StringComparison.Ordinal))
                    .Take(2)
                    .ToList();
                if (matches.Count == 0 && !string.IsNullOrEmpty(normalizedUuid))
                {
                    var recreatedEmailMatches = candidates
                        .Where(x => string.Equals(NormalizeEmail(x.Email), normalizedEmail, StringComparison.Ordinal))
                        .Take(2)
                        .ToList();
                    if (recreatedEmailMatches.Count == 1)
                    {
                        var drifted = CompareRenewalState(recreatedEmailMatches[0], operation);
                        return (Result(drifted.Outcome, "read=list-recreated-email-match;" + drifted.Summary), recreatedEmailMatches[0]);
                    }
                }

                if (matches.Count != 1)
                    return (Unavailable(matches.Count == 0
                        ? "identity=unavailable;read=list-no-match"
                        : "identity=ambiguous;read=list-multiple-matches"), null);

                client = matches[0];
            }

            var compared = CompareRenewalState(client, operation);
            var comparison = Result(compared.Outcome, "read=" + readSource + ";" + compared.Summary);
            _logger.LogInformation(
                "XUI v3 renewal read-back compared controlled fields. renewalOperationId={RenewalOperationId}, outcome={Outcome}, mismatchSummary={MismatchSummary}",
                operation.OperationId,
                comparison.Outcome,
                comparison.Summary);
            return (comparison, client);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Renewal operation read-back failed; keeping the operation ambiguous. operationId={OperationId}",
                operation.OperationId);
            return (Unavailable("identity=unavailable;read=transport-failure"), null);
        }
    }

    /// <summary>
    /// Compares a fresh exact-identity client with the durable renewal pre-state and target field by field.
    /// </summary>
    /// <param name="client">Fresh exact-identity client read from the panel; null produces Unavailable.</param>
    /// <param name="operation">Recovery-eligible operation containing the immutable target and optional pre-snapshot.</param>
    /// <returns>
    /// Detailed state plus a sanitized per-field summary. No returned text contains email, UUID, metadata, quota,
    /// expiry timestamp, URL, token, or response body.
    /// </returns>
    /// <remarks>
    /// UUID (email only for UUID-less legacy rows), quota, expiry, enabled state, and renewal metadata are authoritative.
    /// Telegram owner is mandatory only when the target intentionally changes it. LimitIp, password, SubId, protocol
    /// extras, traffic-row enable, and inbound membership are preserved rather than changed by renewal and therefore
    /// cannot reject an otherwise complete target. JSON metadata is compared semantically so object property order and
    /// whitespace do not create false mismatches.
    /// </remarks>
    /// <example><code>var result = CompareRenewalState(freshClient, operation);</code></example>
    public static RenewalComparisonResult CompareRenewalState(
        XuiV3Client client,
        XuiV3RenewalOperation operation)
    {
        if (client == null || operation == null || !operation.RecoveryEligible)
            return Unavailable("identity=unavailable;evidence=missing");

        var operationUuid = NormalizeUuid(operation.TargetUuid);
        var identityMatches = !string.IsNullOrEmpty(operationUuid)
            ? string.Equals(NormalizeUuid(client.Uuid), operationUuid, StringComparison.Ordinal)
            : string.Equals(NormalizeEmail(client.Email), NormalizeEmail(operation.TargetEmail), StringComparison.Ordinal);
        if (!identityMatches)
            return Result(RecoveryOutcome.Drifted, "identity=drifted;quota=unchecked;expiry=unchecked;enable=unchecked;metadata=unchecked;owner=unchecked");

        XuiV3ClientPayload targetPayload;
        RenewalClientSnapshot preSnapshot;
        try
        {
            targetPayload = string.IsNullOrWhiteSpace(operation.MutationPayloadJson)
                ? null
                : JsonConvert.DeserializeObject<XuiV3ClientPayload>(operation.MutationPayloadJson);
            preSnapshot = string.IsNullOrWhiteSpace(operation.PreMutationSnapshotJson)
                ? null
                : JsonConvert.DeserializeObject<RenewalClientSnapshot>(operation.PreMutationSnapshotJson);
        }
        catch (JsonException)
        {
            return Unavailable("identity=target;evidence=malformed");
        }

        if (targetPayload == null)
            return Unavailable("identity=target;evidence=target-missing");

        var totalBytes = ReadTotalBytes(client);
        var expiry = ReadExpiryTime(client);
        var quotaAtTarget = totalBytes >= operation.TargetTotalBytes;
        var expiryAtTarget = ExpiryReached(expiry, operation.TargetExpiryTime);
        var enableAtTarget = client.Enable == targetPayload.Enable;
        var metadataAtTarget = SemanticJsonEquals(client.Comment, targetPayload.Comment);
        var ownerChanged = preSnapshot != null && preSnapshot.TgId != targetPayload.TgId;
        var ownerAtTarget = !ownerChanged || client.TgId == targetPayload.TgId;

        var quotaAtPre = preSnapshot != null && totalBytes == preSnapshot.TotalBytes;
        var expiryAtPre = preSnapshot != null && expiry == preSnapshot.ExpiryTime;
        var enableAtPre = preSnapshot != null && client.Enable == preSnapshot.Enable;
        var metadataAtPre = preSnapshot != null && SemanticJsonEquals(client.Comment, preSnapshot.Comment);
        var ownerAtPre = preSnapshot != null && client.TgId == preSnapshot.TgId;

        var summary = string.Join(";", new[]
        {
            "identity=target",
            "quota=" + Relation(quotaAtTarget, quotaAtPre),
            "expiry=" + Relation(expiryAtTarget, expiryAtPre),
            "enable=" + Relation(enableAtTarget, enableAtPre),
            "metadata=" + Relation(metadataAtTarget, metadataAtPre),
            "owner=" + (ownerChanged ? Relation(ownerAtTarget, ownerAtPre) : "not-controlled")
        });

        if (quotaAtTarget && expiryAtTarget && enableAtTarget && metadataAtTarget && ownerAtTarget)
            return Result(RecoveryOutcome.Applied, summary);

        var completePreState = preSnapshot != null && quotaAtPre && expiryAtPre && enableAtPre && metadataAtPre && ownerAtPre;
        if (completePreState)
            return Result(RecoveryOutcome.DefinitelyPreMutation, summary);

        var changedFieldShowsTarget =
            (preSnapshot == null || operation.TargetTotalBytes != preSnapshot.TotalBytes) &&
                (quotaAtTarget || HasMovedTowardTarget(totalBytes, preSnapshot?.TotalBytes, operation.TargetTotalBytes)) ||
            (preSnapshot == null || operation.TargetExpiryTime != preSnapshot.ExpiryTime) &&
                (expiryAtTarget || HasMovedTowardTarget(expiry, preSnapshot?.ExpiryTime, operation.TargetExpiryTime)) ||
            (preSnapshot == null || targetPayload.Enable != preSnapshot.Enable) && enableAtTarget ||
            (preSnapshot == null || !SemanticJsonEquals(targetPayload.Comment, preSnapshot.Comment)) && metadataAtTarget ||
            ownerChanged && ownerAtTarget;
        return Result(
            changedFieldShowsTarget ? RecoveryOutcome.PartiallyApplied : RecoveryOutcome.Drifted,
            summary);
    }

    /// <summary>Detects a numeric value moving away from pre-state in the target direction without reaching target.</summary>
    /// <param name="actual">Fresh observed quota or expiry representation.</param>
    /// <param name="pre">Stored pre-mutation value, or null when historical evidence is insufficient.</param>
    /// <param name="target">Absolute mutation target.</param>
    /// <returns><c>true</c> only when the value moved from pre toward target.</returns>
    private static bool HasMovedTowardTarget(long actual, long? pre, long target)
    {
        if (!pre.HasValue || pre.Value == target)
            return false;

        return target > pre.Value
            ? actual > pre.Value
            : actual < pre.Value;
    }

    /// <summary>Checks exact response identity before a direct GET result is trusted.</summary>
    /// <param name="client">Direct endpoint response, which may be null or unrelated on affected panels.</param>
    /// <param name="operation">Operation supplying UUID-first identity and email fallback.</param>
    /// <returns><c>true</c> only when the response identifies the operation target.</returns>
    private static bool ResponseIdentityMatches(XuiV3Client client, XuiV3RenewalOperation operation)
    {
        if (client == null || operation == null)
            return false;

        var uuid = NormalizeUuid(operation.TargetUuid);
        return !string.IsNullOrEmpty(uuid)
            ? string.Equals(NormalizeUuid(client.Uuid), uuid, StringComparison.Ordinal)
            : string.Equals(NormalizeEmail(client.Email), NormalizeEmail(operation.TargetEmail), StringComparison.Ordinal);
    }

    /// <summary>Compares expiry using XUI's negative-duration, zero-lifetime, and positive absolute-time semantics.</summary>
    /// <param name="actual">Fresh panel expiry in milliseconds.</param>
    /// <param name="target">Durable target expiry in milliseconds.</param>
    /// <returns><c>true</c> when the authoritative target representation has been reached.</returns>
    private static bool ExpiryReached(long actual, long target) => target < 0 ? actual == target : target == 0 || actual >= target;

    /// <summary>Semantically compares JSON metadata while retaining ordinal comparison for non-JSON legacy comments.</summary>
    /// <param name="left">Observed metadata or legacy text.</param>
    /// <param name="right">Stored target/pre metadata or legacy text.</param>
    /// <returns><c>true</c> for semantically equal JSON regardless of whitespace/property order, or exact legacy text.</returns>
    private static bool SemanticJsonEquals(string left, string right)
    {
        try
        {
            return JToken.DeepEquals(
                CanonicalizeJson(JToken.Parse(left ?? "null")),
                CanonicalizeJson(JToken.Parse(right ?? "null")));
        }
        catch (JsonReaderException)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>Recursively sorts JSON object properties while preserving array order and scalar values.</summary>
    /// <param name="token">Parsed metadata token.</param>
    /// <returns>A detached canonical token suitable for semantic equality.</returns>
    private static JToken CanonicalizeJson(JToken token)
    {
        if (token is JObject obj)
        {
            var canonical = new JObject();
            foreach (var property in obj.Properties().OrderBy(x => x.Name, StringComparer.Ordinal))
                canonical.Add(property.Name, CanonicalizeJson(property.Value));
            return canonical;
        }

        if (token is JArray array)
            return new JArray(array.Select(CanonicalizeJson));

        return token.DeepClone();
    }

    /// <summary>Maps target/pre booleans to a non-sensitive field relation label.</summary>
    /// <param name="target">Whether the observed value satisfies target semantics.</param>
    /// <param name="pre">Whether the observed value equals the pre-mutation snapshot.</param>
    /// <returns><c>target</c>, <c>pre</c>, or <c>other</c>.</returns>
    private static string Relation(bool target, bool pre) => target ? "target" : pre ? "pre" : "other";

    /// <summary>Creates a detailed result with bounded sanitized summary text.</summary>
    /// <param name="outcome">Comparison outcome.</param>
    /// <param name="summary">Per-field relation labels without sensitive values.</param>
    /// <returns>Immutable result safe for persistence and logs.</returns>
    private static RenewalComparisonResult Result(RecoveryOutcome outcome, string summary) =>
        new()
        {
            Outcome = outcome,
            Summary = string.IsNullOrWhiteSpace(summary)
                ? null
                : summary.Length <= 1000 ? summary : summary[..1000]
        };

    /// <summary>Creates an unavailable comparison result.</summary>
    /// <param name="summary">Sanitized reason labels.</param>
    /// <returns>Unavailable result safe for persistence and logs.</returns>
    private static RenewalComparisonResult Unavailable(string summary) => Result(RecoveryOutcome.Unavailable, summary);

    /// <summary>
    /// Claims due mutation reconciliation and applied-but-unsettled rows for the durable background worker.
    /// </summary>
    /// <param name="maximumCount">Maximum rows to claim in one scan; must be between 1 and 100.</param>
    /// <param name="cancellationToken">Application shutdown token that cancels users.db work.</param>
    /// <returns>A detached list of rows whose independent recovery leases were acquired by this call.</returns>
    /// <remarks>
    /// Ambiguous and stale processing rows are eligible only after their backoff time. Applied-but-unsettled rows are
    /// also returned so settlement can finish after a restart. Claiming never sends a panel mutation.
    /// </remarks>
    /// <example><code>var due = await store.ClaimDueReconciliationAsync(10, stoppingToken);</code></example>
    public async Task<IReadOnlyList<XuiV3RenewalOperation>> ClaimDueReconciliationAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 100);
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();

        // A pending row with no mutation marker cannot have called UpdateClient. Once its original lease is stale it
        // is safe to fail and unlock; processing/mutation-started rows are never handled this way.
        await context.XuiV3RenewalOperations
            .Where(x => x.RecoveryEligible &&
                        x.Status == XuiV3RenewalOperationStatuses.Pending &&
                        x.MutationStartedAtUtc == null &&
                        x.LeaseUntilUtc < now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, XuiV3RenewalOperationStatuses.Failed)
                    .SetProperty(x => x.AccountLockKey, (string)null)
                    .SetProperty(x => x.LastError, "Pending renewal expired before the panel mutation started.")
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        var candidateIds = await context.XuiV3RenewalOperations
            .AsNoTracking()
            .Where(x => x.RecoveryLeaseUntilUtc == null || x.RecoveryLeaseUntilUtc < now)
            .Where(x =>
                x.RecoveryEligible &&
                ((x.Status == XuiV3RenewalOperationStatuses.Ambiguous &&
                  (x.NextReconcileAtUtc == null || x.NextReconcileAtUtc <= now)) ||
                 (x.Status == XuiV3RenewalOperationStatuses.Processing &&
                  x.MutationStartedAtUtc != null &&
                  x.LeaseUntilUtc < now &&
                  (x.NextReconcileAtUtc == null || x.NextReconcileAtUtc <= now)) ||
                 (x.Status == XuiV3RenewalOperationStatuses.Applied &&
                  x.SettlementStatus != XuiV3RenewalSettlementStatuses.Settled &&
                  x.SettlementStatus != XuiV3RenewalSettlementStatuses.ManualReview &&
                  (x.NextReconcileAtUtc == null || x.NextReconcileAtUtc <= now))))
            .OrderBy(x => x.NextReconcileAtUtc ?? x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(maximumCount * 2)
            .ToListAsync(cancellationToken);

        var claimed = new List<XuiV3RenewalOperation>(maximumCount);
        foreach (var id in candidateIds)
        {
            if (claimed.Count >= maximumCount)
                break;

            var claimToken = Guid.NewGuid().ToString("N");
            var updated = await context.XuiV3RenewalOperations
            .Where(x => x.Id == id &&
                            x.RecoveryEligible &&
                            (x.RecoveryLeaseUntilUtc == null || x.RecoveryLeaseUntilUtc < now) &&
                            (x.Status == XuiV3RenewalOperationStatuses.Ambiguous ||
                             (x.Status == XuiV3RenewalOperationStatuses.Processing &&
                              x.MutationStartedAtUtc != null && x.LeaseUntilUtc < now) ||
                             (x.Status == XuiV3RenewalOperationStatuses.Applied &&
                              x.SettlementStatus != XuiV3RenewalSettlementStatuses.Settled &&
                              x.SettlementStatus != XuiV3RenewalSettlementStatuses.ManualReview)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.RecoveryClaimToken, claimToken)
                        .SetProperty(x => x.RecoveryLeaseUntilUtc, now.Add(RecoveryLease))
                        .SetProperty(x => x.UpdatedAtUtc, now),
                    cancellationToken);
            if (updated != 1)
                continue;

            var row = await context.XuiV3RenewalOperations
                .AsNoTracking()
                .FirstAsync(x => x.Id == id, cancellationToken);
            claimed.Add(row);
        }

        return claimed;
    }

    /// <summary>
    /// Persists one detailed GET-only comparison and chooses safe retry, failure, or manual-review handling.
    /// </summary>
    /// <param name="operation">Claimed ambiguous/processing operation.</param>
    /// <param name="comparison">Detailed comparison containing only a sanitized per-field summary.</param>
    /// <param name="cancellationToken">Token that cancels the conditional users.db update.</param>
    /// <returns>
    /// The durable disposition. DefinitivelyFailed means repeated GETs over the grace window proved the mutation was
    /// never applied and the account lock was released without touching settlement.
    /// </returns>
    /// <remarks>
    /// PartiallyApplied and Drifted results immediately require manual review and retain the lock. Unavailable results
    /// use bounded backoff. DefinitelyPreMutation releases the lock only after at least
    /// <see cref="MinimumPreMutationObservations"/> successful observations span <see cref="PreMutationCommitGrace"/>.
    /// The method never invokes or authorizes another panel mutation and never changes settlement status.
    /// </remarks>
    /// <example><code>await store.PersistReconciliationResultAsync(operation, comparison, token);</code></example>
    public async Task<ReconciliationDisposition> PersistReconciliationResultAsync(
        XuiV3RenewalOperation operation,
        RenewalComparisonResult comparison,
        CancellationToken cancellationToken = default)
    {
        if (comparison == null || comparison.Outcome == RecoveryOutcome.Applied)
            throw new ArgumentException("A non-applied comparison is required.", nameof(comparison));

        var now = DateTime.UtcNow;
        var attempt = operation.ReconcileAttemptCount + 1;
        var preObservation = comparison.Outcome == RecoveryOutcome.DefinitelyPreMutation;
        var preservePreEvidence = comparison.Outcome == RecoveryOutcome.Unavailable;
        var preCount = preObservation
            ? operation.PreMutationObservationCount + 1
            : preservePreEvidence ? operation.PreMutationObservationCount : 0;
        var firstPreObservedAt = preObservation
            ? operation.FirstPreMutationObservedAtUtc ?? now
            : preservePreEvidence ? operation.FirstPreMutationObservedAtUtc : (DateTime?)null;
        var graceElapsed = preObservation &&
                           preCount >= MinimumPreMutationObservations &&
                           firstPreObservedAt.HasValue &&
                           now - firstPreObservedAt.Value >= PreMutationCommitGrace &&
                           operation.MutationStartedAtUtc.HasValue &&
                           now - operation.MutationStartedAtUtc.Value >= PreMutationCommitGrace;
        var manualReview = comparison.Outcome is RecoveryOutcome.PartiallyApplied or RecoveryOutcome.Drifted ||
                           (!graceElapsed &&
                            (attempt >= MaximumAutomaticReconcileAttempts ||
                             operation.CreatedAtUtc <= now.Subtract(MaximumAutomaticReconcileAge)));
        var delaySeconds = Math.Min(1800, 15 * Math.Pow(2, Math.Min(attempt - 1, 7)));
        await using var context = _userDbContextFactory.CreateDbContext();
        var updated = await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.RecoveryEligible &&
                        (x.Status == XuiV3RenewalOperationStatuses.Ambiguous ||
                         x.Status == XuiV3RenewalOperationStatuses.Processing) &&
                        x.RecoveryClaimToken == operation.RecoveryClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, graceElapsed
                        ? XuiV3RenewalOperationStatuses.Failed
                        : manualReview
                            ? XuiV3RenewalOperationStatuses.ManualReview
                            : XuiV3RenewalOperationStatuses.Ambiguous)
                    .SetProperty(x => x.AccountLockKey, graceElapsed ? null : operation.AccountLockKey)
                    .SetProperty(x => x.ReconcileAttemptCount, attempt)
                    .SetProperty(x => x.LastReconcileAtUtc, now)
                    .SetProperty(x => x.NextReconcileAtUtc, manualReview || graceElapsed
                        ? (DateTime?)null
                        : now.AddSeconds(delaySeconds))
                    .SetProperty(x => x.ManualReviewAtUtc, manualReview ? now : (DateTime?)null)
                    .SetProperty(x => x.PreMutationObservationCount, preCount)
                    .SetProperty(x => x.FirstPreMutationObservedAtUtc, firstPreObservedAt)
                    .SetProperty(x => x.LastComparisonOutcome, comparison.Outcome.ToString())
                    .SetProperty(x => x.LastMismatchSummary, comparison.Summary)
                    .SetProperty(x => x.RecoveryLeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.RecoveryClaimToken, (string)null)
                    .SetProperty(x => x.LastError, graceElapsed
                        ? "Repeated GET-only observations proved the renewal mutation was not applied."
                        : manualReview
                            ? "Renewal reconciliation requires manual review; see sanitized mismatch summary."
                            : "Renewal reconciliation remains pending; see sanitized mismatch summary.")
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
        if (updated != 1)
            return ReconciliationDisposition.NoChange;
        if (graceElapsed)
            return ReconciliationDisposition.DefinitivelyFailed;
        return manualReview ? ReconciliationDisposition.ManualReview : ReconciliationDisposition.RetryScheduled;
    }

    /// <summary>
    /// Releases a background recovery lease after settlement completed or another executor made the row terminal.
    /// </summary>
    /// <param name="operation">Operation carrying the current recovery claim token.</param>
    /// <param name="cancellationToken">Token that cancels the conditional users.db update.</param>
    /// <returns>A task that completes after the lease is cleared when still owned by this executor.</returns>
    /// <remarks>The account lock and mutation/settlement statuses are deliberately unchanged.</remarks>
    /// <example><code>await store.ReleaseRecoveryClaimAsync(operation, stoppingToken)</code></example>
    public async Task ReleaseRecoveryClaimAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.RecoveryClaimToken == operation.RecoveryClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RecoveryLeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.RecoveryClaimToken, (string)null)
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    /// <summary>
    /// Releases an applied operation's recovery lease and delays another settlement attempt.
    /// </summary>
    /// <param name="operation">Applied operation carrying the current recovery claim token.</param>
    /// <param name="reason">Sanitized reason why settlement could not complete.</param>
    /// <param name="cancellationToken">Token that cancels the conditional users.db update.</param>
    /// <returns>A task that completes after a one-minute retry is scheduled.</returns>
    /// <remarks>
    /// This method does not change mutation status, release the account lock, or perform any financial operation.
    /// The separate settlement guard decides whether a stale financial claim requires manual review.
    /// </remarks>
    /// <example><code>await store.ScheduleAppliedSettlementRetryAsync(operation, "payer unavailable", token)</code></example>
    public async Task ScheduleAppliedSettlementRetryAsync(
        XuiV3RenewalOperation operation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var context = _userDbContextFactory.CreateDbContext();
        await context.XuiV3RenewalOperations
            .Where(x => x.OperationKey == operation.OperationKey &&
                        x.Status == XuiV3RenewalOperationStatuses.Applied &&
                        x.RecoveryClaimToken == operation.RecoveryClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.NextReconcileAtUtc, now.AddMinutes(1))
                    .SetProperty(x => x.RecoveryLeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.RecoveryClaimToken, (string)null)
                    .SetProperty(x => x.LastError, Truncate(reason))
                    .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);
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
                        x.Status == XuiV3RenewalOperationStatuses.Applied &&
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
                    .SetProperty(x => x.AccountLockKey, (string)null)
                    .SetProperty(x => x.RecoveryLeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.RecoveryClaimToken, (string)null)
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
