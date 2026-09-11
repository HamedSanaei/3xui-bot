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
    /// <para>
    /// Ordering is meaningful and forward-only. Each value below is reached only after the corresponding external effect
    /// has been proven by a panel read-back, so a restart that observes step <c>N</c> knows exactly which effects are
    /// durable and which must still be proven before any further mutation.
    /// </para>
    /// <para>
    /// <b>Why the order is counter-reset-before-quota.</b> The final client must carry exactly the ordered quota with
    /// zero provisional usage counted against it. If the purchased quota were written before the counters were cleared,
    /// the customer would hold the large purchased quota while their provisional usage was still the depletion baseline,
    /// so they could consume the remainder of the purchased quota and then consume it again after the reset — an
    /// over-grant bounded by the purchased entitlement rather than by the courtesy allowance. Writing the quota last
    /// means the largest extra allowance any intermediate state can expose is the provisional one, never the purchased
    /// one.
    /// </para>
    /// <para>
    /// <b>Why the sequence never disables before the counter reset.</b> Proved against MHSanaei/3x-ui v3.7.0 source:
    /// <c>ClientService.ResetTrafficByEmail</c> re-enables a disabled client through the normal update path only when
    /// <c>!rec.Enable</c>, and <c>InboundService.resetClientTrafficLocked</c> builds a runtime <c>AddUser</c> plan when
    /// the shared traffic row is not enabled. Both guards are therefore keyed on the client being disabled at the moment
    /// of the reset. Because <c>enable</c> is the only instantaneous admission gate in 3x-ui (expiry and quota are
    /// enforced by the periodic traffic poll, not by Xray at connect time), disabling the client before the reset would
    /// make the reset's own auto-enable the one mutation that re-admits a revoked credential. The saga therefore reaches
    /// the reset with an enabled client, which provably suppresses both guards and keeps the running Xray user set
    /// untouched, and then re-zeros the shared counter through a side-effect-free counter write.
    /// </para>
    /// </remarks>
    public static class TenantCardProvisionalSteps
    {
        /// <summary>The operation key is durably claimed and no external mutation has been attempted yet.</summary>
        public const string Claimed = "claimed";

        /// <summary>
        /// Identity was proven and the client is in the only state from which the reset is safe to issue.
        /// </summary>
        /// <remarks>
        /// The step is reached after the panel read-back proved the exact email, UUID, subId, and placement, and after the
        /// client was confirmed enabled. When the client was found disabled it is first enabled through the ordinary
        /// update path while keeping its existing provisional quota and expiry, so the following reset cannot fire its
        /// auto-enable branch. No quota, duration, or credential is changed by reaching this step.
        /// </remarks>
        public const string QuiescenceConfirmed = "quiescence_confirmed";

        /// <summary>Revoke only: the exact provisional client was proven disabled and is no longer admitted.</summary>
        /// <remarks>
        /// The finalize saga never records this step, because a disabled client is exactly the state that makes the
        /// official traffic reset re-admit the credential. Revoke deliberately does disable the client, and a later reset
        /// on that client would undo the revoke, which is why the revoke tail must complete without a reset.
        /// </remarks>
        public const string Disabled = "disabled";

        /// <summary>The official traffic reset was issued and proven to clear every usage source.</summary>
        public const string Reset = "reset";

        /// <summary>
        /// The shared usage row was re-proven zero by a side-effect-free counter write after the reset.
        /// </summary>
        /// <remarks>
        /// The counter write cannot clear master-pushed global rows or per-inbound node rows, so it does not replace the
        /// official reset; it erases whatever accrued between the reset read-back and this step. It changes no enable
        /// flag and touches neither the Xray runtime nor any inbound setting.
        /// </remarks>
        public const string Zeroed = "zeroed";

        /// <summary>The exact purchased quota, frozen final expiry, and enabled flag were written to the same client.</summary>
        public const string QuotaWritten = "quota_written";

        /// <summary>The final panel state was proven by read-back; the financial settlement boundary may be crossed.</summary>
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
        /// them and no earlier step can be re-entered after they are recorded. The numeric value is not persisted; the
        /// stored step is the string, and the ordinal exists only to compare two stored strings.
        /// </remarks>
        public static int OrderOf(string step) => step switch
        {
            Claimed => 0,
            QuiescenceConfirmed => 1,
            Disabled => 1,
            Reset => 2,
            Zeroed => 3,
            QuotaWritten => 4,
            Proven => 5,
            Revoked => 5,
            ManualReview => 6,
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
        /// Callers use this to decide whether a panel mutation is still required or whether the durable row already
        /// records its proof. A terminal manual review always reports as past every mutable step, which forces human
        /// reconciliation instead of a replay.
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

        /// <summary>
        /// Gets or sets the UTC instant the purchased subscription period starts from, captured exactly once.
        /// </summary>
        /// <remarks>
        /// This is the frozen clock of the saga. The final expiry is derived from it and persisted separately in
        /// <see cref="FinalExpiryTimeMs" />, so a restart minutes or hours later can never extend the customer's paid
        /// period by recomputing the expiry from the current time. Null until the saga freezes the final entitlement.
        /// </remarks>
        public DateTime? FinalizationEffectiveAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the exact purchased quota in bytes that the final panel client must carry.
        /// </summary>
        /// <remarks>
        /// Frozen from the commercial order (never from a measured post-reset usage value), so a retried final write
        /// cannot drift upward. Zero is a legitimate value meaning an unlimited/lifetime order and must not be confused
        /// with "not yet frozen"; that distinction is carried by <see cref="FinalizationEffectiveAtUtc" />.
        /// </remarks>
        public long? FinalQuotaBytes { get; set; }

        /// <summary>
        /// Gets or sets the frozen absolute expiry in Unix milliseconds written to the final client.
        /// </summary>
        /// <remarks>
        /// Zero means a lifetime order and must be written as zero, never replaced by a computed date. Negative values
        /// are never produced here because the saga writes absolute expiry rather than a first-use duration.
        /// </remarks>
        public long? FinalExpiryTimeMs { get; set; }

        /// <summary>
        /// Gets or sets the UTC time the exact provisional client was proven disabled.
        /// </summary>
        /// <remarks>
        /// Written by the revoke saga only. The finalize saga deliberately never disables the client, because a disabled
        /// client is the precondition that makes the official traffic reset re-admit the credential.
        /// </remarks>
        public DateTime? DisabledAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the official traffic reset request completed successfully.</summary>
        public DateTime? ResetAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC time the shared usage row was re-proven zero after the reset.
        /// </summary>
        /// <remarks>
        /// Retained from the landed schema. The column is not written by either current saga; the post-reset counter
        /// proof is recorded by <see cref="TenantCardProvisionalSteps.Zeroed" /> in <see cref="Step" /> instead, and the
        /// timestamp it would have carried is superseded by <see cref="UpdatedAtUtc" />. It is kept because dropping a
        /// deployed column is a destructive schema change that this task does not require.
        /// </remarks>
        public DateTime? ReDisabledAtUtc { get; set; }

        /// <summary>Gets or sets the UTC time the shared usage row was re-proven zero after the reset.</summary>
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
