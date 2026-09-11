using System;

namespace Adminbot.Domain
{
    /// <summary>
    /// Identifies which post-receipt lifecycle operation one durable provisional saga row represents.
    /// </summary>
    /// <remarks>
    /// The two kinds share one table because both are order-scoped, mutually exclusive for a single order, and must never
    /// run at the same time. A finalize and a revoke for the same order are different operation keys, so the durable
    /// claim on one kind never authorizes the other.
    /// </remarks>
    public static class TenantCardProvisionalOperationKinds
    {
        /// <summary>The receipt was approved and the same provisional client is being upgraded to the purchased entitlement.</summary>
        public const string Finalize = "finalize";

        /// <summary>The receipt was rejected and the exact provisional client is being disabled.</summary>
        public const string Revoke = "revoke";
    }

    /// <summary>
    /// Ordered saga steps recorded for a provisional finalize or revoke operation.
    /// </summary>
    /// <remarks>
    /// Ordering is meaningful and forward-only. Each value below is reached only after the corresponding external effect
    /// has been proven by a panel read-back, so a restart that observes step <c>N</c> knows exactly which effects are
    /// durable and which must still be proven before any further mutation.
    ///
    /// The ordering exists because 3x-ui's official traffic reset force-enables a disabled client. The saga therefore
    /// disables first, resets, re-disables, zeroes the shared row again, writes the exact quota with the enable flag, and
    /// proves the final state — in that order. See <see cref="TenantCardProvisionalOperation"/> for the recorded proof.
    /// </remarks>
    public static class TenantCardProvisionalSteps
    {
        /// <summary>The operation key is durably claimed and no external mutation has been attempted yet.</summary>
        public const string Claimed = "claimed";

        /// <summary>The exact client was disabled and the disabled state was proven by read-back.</summary>
        public const string Disabled = "disabled";

        /// <summary>The official traffic reset was issued to clear every usage source.</summary>
        public const string Reset = "reset";

        /// <summary>The client was re-disabled after the reset re-enabled it, and the disabled state was proven.</summary>
        public const string ReDisabled = "re_disabled";

        /// <summary>The shared usage row was zeroed while the client was proven disabled; no traffic can have accrued.</summary>
        public const string Zeroed = "zeroed";

        /// <summary>The exact purchased quota, final expiry, and enabled flag were written to the same client.</summary>
        public const string QuotaWritten = "quota_written";

        /// <summary>The final panel state was proven by read-back and the financial settlement boundary was crossed.</summary>
        public const string Proven = "proven";

        /// <summary>Revoke only: the exact provisional client is proven disabled and the customer no longer has access.</summary>
        public const string Revoked = "revoked";

        /// <summary>An external outcome stayed ambiguous and a human must reconcile it before any further panel mutation.</summary>
        public const string ManualReview = "manual_review";

        /// <summary>Numeric order of a step, used to enforce forward-only progress that a restart cannot rewind.</summary>
        /// <param name="step">One of the step constants declared on this type.</param>
        /// <returns>
        /// A monotonic ordinal for known steps, or <see cref="int.MaxValue" /> for an unrecognized value so unknown state
        /// can never be silently treated as an early step and replayed.
        /// </returns>
        /// <remarks>
        /// Terminal steps (<see cref="ManualReview" />) sit above every mutable step so no later call can advance past
        /// them and no earlier step can be re-entered after they are recorded.
        /// </remarks>
        public static int OrderOf(string step) => step switch
        {
            Claimed => 0,
            Disabled => 1,
            Reset => 2,
            ReDisabled => 3,
            Zeroed => 4,
            QuotaWritten => 5,
            Proven => 6,
            Revoked => 6,
            ManualReview => 7,
            _ => int.MaxValue
        };

        /// <summary>Determines whether a recorded step has already reached or passed a required step.</summary>
        /// <param name="currentStep">Step currently stored on the durable operation row.</param>
        /// <param name="requiredStep">Step whose effect the caller wants to skip or repeat.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="currentStep" /> is at or beyond <paramref name="requiredStep" />; otherwise
        /// <c>false</c>.
        /// </returns>
        /// <remarks>
        /// Callers use this to decide whether a read-back is still required before skipping a mutation. A terminal manual
        /// review always reports as past every mutable step, which forces human reconciliation instead of a replay.
        /// </remarks>
        public static bool HasReached(string currentStep, string requiredStep)
            => OrderOf(currentStep) >= OrderOf(requiredStep);
    }

    /// <summary>
    /// Durable restart-safe state for one provisional finalize or revoke saga run against a tenant card-to-card order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This row is the only authority for whether an external mutation may be replayed. Every mutating step in
    /// <see cref="TenantCardProvisionalSteps" /> is written only after the panel read-back proved its effect, so a process
    /// restart re-derives the next action from durable evidence rather than from an in-memory idea of progress.
    /// </para>
    /// <para>
    /// The row is tenant-scoped through <see cref="TenantBotOrderId" /> and never shared across orders. It carries no
    /// tokens, panel credentials, or raw Telegram payloads; <see cref="EvidenceJson" /> stores only sanitized comparison
    /// values such as the proven quota, expiry, and enabled flag.
    /// </para>
    /// <para>
    /// Records are effectively append-only in business terms: steps advance forward and are never rewound. A row that
    /// reaches <see cref="TenantCardProvisionalSteps.ManualReview" /> stays there until an operator reconciles it.
    /// </para>
    /// </remarks>
    public sealed class TenantCardProvisionalOperation
    {
        /// <summary>Gets or sets the internal identity of this saga row.</summary>
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the stable business operation key, for example <c>tenant-card-finalize:{OrderId}</c> or
        /// <c>tenant-card-revoke:{OrderId}</c>.
        /// </summary>
        /// <remarks>
        /// Unique across the table. The key is derived from the public order id rather than the database id so a
        /// re-imported or re-created database cannot silently reuse a historical key for a different order.
        /// </remarks>
        public string OperationKey { get; set; }

        /// <summary>Gets or sets the internal database id of the tenant order this saga mutates.</summary>
        public int TenantBotOrderId { get; set; }

        /// <summary>Gets or sets the public tenant order id used to rebuild the operation key after a restart.</summary>
        public string OrderId { get; set; }

        /// <summary>Gets or sets one of the <see cref="TenantCardProvisionalOperationKinds" /> values.</summary>
        public string Kind { get; set; } = TenantCardProvisionalOperationKinds.Finalize;

        /// <summary>Gets or sets the furthest durably proven <see cref="TenantCardProvisionalSteps" /> value.</summary>
        public string Step { get; set; } = TenantCardProvisionalSteps.Claimed;

        /// <summary>Gets or sets the UTC time the client was proven disabled.</summary>
        public DateTime? DisabledAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the official traffic reset request completed successfully.</summary>
        public DateTime? ResetAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the client was proven disabled again after the reset re-enabled it.</summary>
        public DateTime? ReDisabledAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the shared usage row was proven zero while the client was disabled.</summary>
        public DateTime? ZeroedAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the exact purchased quota and final expiry were written.</summary>
        public DateTime? QuotaWrittenAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the final panel state was proven by read-back.</summary>
        public DateTime? ProvenAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the saga reached a terminal step, whether proven or manual review.</summary>
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets sanitized read-back evidence recorded for the last completed step.
        /// </summary>
        /// <remarks>
        /// Contains only non-secret comparison values, never panel tokens, response bodies, or customer credentials.
        /// </remarks>
        public string EvidenceJson { get; set; }

        /// <summary>Gets or sets a stable safe error code when the saga stopped for manual review.</summary>
        /// <remarks>
        /// Uses the same closed reason-code vocabulary as the notification workers so operators see a stable identifier
        /// rather than a raw panel or transport exception message.
        /// </remarks>
        public string ErrorCode { get; set; }

        /// <summary>Gets or sets the UTC time the saga row was created.</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the saga row was last advanced.</summary>
        public DateTime UpdatedAtUtc { get; set; }
    }
}
