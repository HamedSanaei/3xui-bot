using System;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

namespace Adminbot.Services
{
    /// <summary>
    /// Persists the restart-safe step state of a provisional finalize or revoke saga in users.db.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is deliberately narrow: it records what has been durably proven. It performs no panel I/O, holds no
    /// lock across a network call, and never decides business policy. The saga service reads the recorded step, performs
    /// one external mutation or read-back, then calls <see cref="AdvanceAsync" /> to record the proof.
    /// </para>
    /// <para>
    /// Every method runs inside its own short users.db transaction through <see cref="SqliteOperation" />, so a saga that
    /// is interrupted between two steps resumes from the last durably recorded step rather than from an in-memory count.
    /// </para>
    /// <para>
    /// Steps are strictly forward-only. <see cref="AdvanceAsync" /> refuses to rewind a row, and a row that reached a
    /// terminal step cannot be advanced again, which is what prevents an ambiguous restart from replaying a panel
    /// mutation that may already have been applied.
    /// </para>
    /// </remarks>
    public sealed class TenantCardProvisionalOperationStore
    {
        private readonly UserDbContextFactory _factory;

        /// <summary>Creates the store with independently scoped persistence contexts.</summary>
        /// <param name="factory">Required users.db context factory used for every short saga-state transaction.</param>
        /// <exception cref="ArgumentNullException"><paramref name="factory" /> is <c>null</c>.</exception>
        public TenantCardProvisionalOperationStore(UserDbContextFactory factory)
            => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        /// <summary>
        /// Durably claims the saga row for one order and operation kind, or returns the existing row.
        /// </summary>
        /// <param name="operationKey">
        /// Stable key in <c>tenant-card-{kind}:{publicOrderId}</c> form. It must reference the same order on every call;
        /// reusing a key for another order, kind, or database row is rejected.
        /// </param>
        /// <param name="tenantBotOrderId">Internal database id of the tenant order being finalized or revoked.</param>
        /// <param name="orderId">Public order id used to rebuild the key after a restart and to audit the row.</param>
        /// <param name="kind">One of the <see cref="TenantCardProvisionalOperationKinds" /> values.</param>
        /// <param name="token">Cancellation token for the short claim transaction.</param>
        /// <returns>
        /// The detached saga row plus <c>true</c> when this call created it. A <c>false</c> second value means the row
        /// already existed and the caller must resume from the recorded <see cref="TenantCardProvisionalOperation.Step" />
        /// instead of restarting the mutation sequence.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The key already exists but is bound to a different order, kind, or database row.
        /// </exception>
        /// <remarks>
        /// The claim is the first durable boundary of the saga. No panel mutation may be attempted before it commits,
        /// because only a committed claim tells a restarted process that a saga is in flight for this order.
        /// </remarks>
        /// <example>
        /// <code>
        /// var claim = await store.ClaimAsync($"tenant-card-finalize:{order.OrderId}", order.Id, order.OrderId,
        ///     TenantCardProvisionalOperationKinds.Finalize, token);
        /// if (!claim.Created &amp;&amp; TenantCardProvisionalSteps.HasReached(claim.Operation.Step, TenantCardProvisionalSteps.Proven))
        ///     return AlreadyFinalized();
        /// </code>
        /// </example>
        public Task<(TenantCardProvisionalOperation Operation, bool Created)> ClaimAsync(
            string operationKey,
            int tenantBotOrderId,
            string orderId,
            string kind,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(operationKey))
                throw new ArgumentException("A provisional operation key is required.", nameof(operationKey));
            if (string.IsNullOrWhiteSpace(orderId))
                throw new ArgumentException("A public order id is required.", nameof(orderId));
            if (kind is not (TenantCardProvisionalOperationKinds.Finalize or TenantCardProvisionalOperationKinds.Revoke))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported provisional operation kind.");

            return SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var row = await db.TenantCardProvisionalOperations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct);
                if (row != null)
                {
                    // A key is bound to exactly one order and kind for its lifetime. Rejecting a mismatch keeps a
                    // historical row from authorizing a mutation against a different order after a data import.
                    if (row.TenantBotOrderId != tenantBotOrderId || !string.Equals(row.Kind, kind, StringComparison.Ordinal))
                        throw new InvalidOperationException("Provisional operation identity does not match its claim.");
                    return (row, false);
                }

                var now = DateTime.UtcNow;
                var candidate = new TenantCardProvisionalOperation
                {
                    OperationKey = operationKey,
                    TenantBotOrderId = tenantBotOrderId,
                    OrderId = orderId,
                    Kind = kind,
                    Step = TenantCardProvisionalSteps.Claimed,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.TenantCardProvisionalOperations.Add(candidate);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (candidate, true);
            }, token);
        }

        /// <summary>
        /// Records that a saga step's external effect is durably proven, advancing the row forward only.
        /// </summary>
        /// <param name="operationKey">Exact key of the claimed saga row.</param>
        /// <param name="step">Proven <see cref="TenantCardProvisionalSteps" /> value for this row.</param>
        /// <param name="evidenceJson">
        /// Optional sanitized read-back evidence. Must never contain panel tokens, raw response bodies, or customer
        /// credentials; the saga passes only comparison values such as proven quota, expiry, and enabled state.
        /// </param>
        /// <param name="token">Cancellation token for the short update transaction.</param>
        /// <returns>
        /// <c>true</c> when the row advanced to <paramref name="step" />; <c>false</c> when the row was already at or past
        /// that step, meaning the caller replays nothing because the effect is already recorded.
        /// </returns>
        /// <remarks>
        /// Forward-only semantics matter for restart safety: a replayed step that is already recorded returns
        /// <c>false</c>, which lets the saga skip the mutation and continue from the furthest proven step. Terminal steps
        /// such as manual review can never be advanced past.
        /// </remarks>
        public Task<bool> AdvanceAsync(string operationKey, string step, string evidenceJson, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(operationKey))
                throw new ArgumentException("A provisional operation key is required.", nameof(operationKey));
            if (TenantCardProvisionalSteps.OrderOf(step) == int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(step), step, "Unsupported provisional saga step.");

            return SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var row = await db.TenantCardProvisionalOperations
                    .SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct);
                if (row == null)
                    return false;

                var current = TenantCardProvisionalSteps.OrderOf(row.Step);
                var target = TenantCardProvisionalSteps.OrderOf(step);
                // Never rewind, never advance past a terminal step, and never regress manual review.
                if (target <= current || current == TenantCardProvisionalSteps.OrderOf(TenantCardProvisionalSteps.ManualReview))
                    return false;

                var now = DateTime.UtcNow;
                row.Step = step;
                row.UpdatedAtUtc = now;
                if (!string.IsNullOrWhiteSpace(evidenceJson))
                    row.EvidenceJson = evidenceJson;

                switch (step)
                {
                    case TenantCardProvisionalSteps.Reset:
                        row.ResetAtUtc = now;
                        break;
                    case TenantCardProvisionalSteps.Zeroed:
                        row.ZeroedAtUtc = now;
                        break;
                    case TenantCardProvisionalSteps.QuotaWritten:
                        row.QuotaWrittenAtUtc = now;
                        break;
                    case TenantCardProvisionalSteps.Proven:
                    case TenantCardProvisionalSteps.Revoked:
                        row.ProvenAtUtc = now;
                        row.CompletedAtUtc = now;
                        break;
                    case TenantCardProvisionalSteps.ManualReview:
                        row.CompletedAtUtc = now;
                        break;
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return true;
            }, token);
        }

        /// <summary>
        /// Moves a saga row to terminal manual review with a stable safe reason code.
        /// </summary>
        /// <param name="operationKey">Exact key of the claimed saga row.</param>
        /// <param name="errorCode">
        /// Closed-vocabulary code describing why automated reconciliation stopped, for example
        /// <c>provisional_identity_ambiguous</c>. Never raw exception text.
        /// </param>
        /// <param name="token">Cancellation token for the short update transaction.</param>
        /// <returns>
        /// <c>true</c> when this call moved the row to manual review; <c>false</c> when it was already terminal.
        /// </returns>
        /// <remarks>
        /// Manual review is the fail-closed outcome. Once recorded, every later call to <see cref="AdvanceAsync" /> is
        /// refused, so an ambiguous panel outcome can never be resolved by blind replay and must be reviewed by a human.
        /// </remarks>
        public Task<bool> MarkManualReviewAsync(string operationKey, string errorCode, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(operationKey))
                throw new ArgumentException("A provisional operation key is required.", nameof(operationKey));

            return SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                var changed = await db.TenantCardProvisionalOperations
                    .Where(x => x.OperationKey == operationKey &&
                                x.Step != TenantCardProvisionalSteps.ManualReview)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(x => x.Step, TenantCardProvisionalSteps.ManualReview)
                        .SetProperty(x => x.ErrorCode, errorCode)
                        .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow)
                        .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), ct);
                return changed == 1;
            }, token);
        }

        /// <summary>
        /// Freezes the final entitlement once so every later retry writes the same quota and the same expiry.
        /// </summary>
        /// <param name="operationKey">Exact key of the claimed saga row.</param>
        /// <param name="effectiveAtUtc">
        /// The UTC instant the purchased subscription period starts from. Pass the current UTC time on the first call;
        /// later calls pass nothing because the stored value wins.
        /// </param>
        /// <param name="quotaBytes">
        /// Exact purchased quota in bytes, taken from the commercial order rather than from measured usage. Zero means
        /// an unlimited/lifetime order and is stored as-is.
        /// </param>
        /// <param name="expiryTimeMs">
        /// Absolute expiry in Unix milliseconds derived once from <paramref name="effectiveAtUtc" /> plus the purchased
        /// duration, or <c>0</c> for a lifetime order. Must never be recomputed from the current time on a retry.
        /// </param>
        /// <param name="token">Cancellation token for the short update transaction.</param>
        /// <returns>
        /// The durable row after the write. When the row already carried a frozen value the stored values are returned
        /// unchanged, which is what stops a restart from extending a paid period or drifting the quota upward.
        /// </returns>
        /// <remarks>
        /// Idempotent by construction: the first caller to succeed freezes the values and every later caller observes
        /// them. The write is deliberately separate from step advancement so a crash between freezing and the first panel
        /// mutation still resumes with a stable, restart-safe final entitlement.
        /// </remarks>
        public Task<TenantCardProvisionalOperation> FreezeFinalEntitlementAsync(
            string operationKey,
            DateTime effectiveAtUtc,
            long quotaBytes,
            long expiryTimeMs,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(operationKey))
                throw new ArgumentException("A provisional operation key is required.", nameof(operationKey));
            if (quotaBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(quotaBytes), quotaBytes, "A purchased quota cannot be negative.");
            if (expiryTimeMs < 0)
                throw new ArgumentOutOfRangeException(nameof(expiryTimeMs), expiryTimeMs, "A lifetime expiry is expressed as zero, never as a negative absolute time.");

            return SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var row = await db.TenantCardProvisionalOperations
                    .SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct)
                    ?? throw new InvalidOperationException("The provisional saga must be claimed before its final entitlement is frozen.");

                // First writer wins. A restart must never re-derive the expiry from the current clock, because that
                // would silently extend the customer's paid period by the duration of the outage.
                if (row.FinalizationEffectiveAtUtc == null)
                {
                    row.FinalizationEffectiveAtUtc = effectiveAtUtc;
                    row.FinalQuotaBytes = quotaBytes;
                    row.FinalExpiryTimeMs = expiryTimeMs;
                    row.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return row;
            }, token);
        }

        /// <summary>Loads the durable saga row for an operation key, if one exists.</summary>
        /// <param name="operationKey">Exact key of the saga row.</param>
        /// <param name="token">Cancellation token for the short read.</param>
        /// <returns>
        /// The detached row, or <c>null</c> when no saga was ever claimed for this key. A <c>null</c> result means no
        /// panel mutation has been authorized for this key yet.
        /// </returns>
        public Task<TenantCardProvisionalOperation> FindAsync(string operationKey, CancellationToken token)
            => SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                return await db.TenantCardProvisionalOperations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct);
            }, token);
    }
}
