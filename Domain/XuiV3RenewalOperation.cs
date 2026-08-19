using System;

namespace Adminbot.Domain
{
    /// <summary>
    /// Lifecycle statuses for one durable XUI v3 renewal operation.
    /// </summary>
    /// <remarks>
    /// The operation row is the exactly-once anchor for a single intended renewal. Only one executor may hold the
    /// processing claim, and only the pending/processing to applied transition may trigger wallet settlement.
    /// </remarks>
    public static class XuiV3RenewalOperationStatuses
    {
        /// <summary>
        /// The operation was created and is waiting for an executor to claim it before the XUI mutation is sent.
        /// </summary>
        public const string Pending = "pending";

        /// <summary>
        /// An executor holds the lease and the XUI update request is in flight or the post-update read-back is running.
        /// </summary>
        public const string Processing = "processing";

        /// <summary>
        /// The panel provably holds the absolute target values; settlement and success logging may proceed exactly once.
        /// </summary>
        public const string Applied = "applied";

        /// <summary>
        /// The mutation outcome cannot be determined (for example a timeout followed by a failed read-back). The
        /// mutation is never replayed automatically; a later confirmation performs a read-only reconciliation.
        /// </summary>
        public const string Ambiguous = "ambiguous";

        /// <summary>
        /// The panel definitively rejected the update (non-transient business failure). No settlement occurred and the
        /// mutation is never replayed; the user must start a fresh renewal flow.
        /// </summary>
        public const string Failed = "failed";
    }

    /// <summary>
    /// Settlement states for one durable XUI v3 renewal operation.
    /// </summary>
    /// <remarks>
    /// Settlement is guarded separately from the XUI mutation because the wallet debit in <c>credentials.db</c> and
    /// the users.db ledger are separate databases. A settling state is never automatically resumed after a crash;
    /// it is parked in <see cref="ManualReview"/> so an operator can verify the wallet without ever debiting twice.
    /// </remarks>
    public static class XuiV3RenewalSettlementStatuses
    {
        /// <summary>The renewal was applied but no settlement has started yet.</summary>
        public const string Pending = "pending";

        /// <summary>One executor is performing the wallet/site debit and ledger write.</summary>
        public const string Settling = "settling";

        /// <summary>The wallet was debited and the ledger row was written exactly once.</summary>
        public const string Settled = "settled";

        /// <summary>
        /// A previous executor crashed mid-settlement. The operation is parked for operator review and is never
        /// automatically resumed, so a wallet can never be debited twice for the same operation.
        /// </summary>
        public const string ManualReview = "manual_review";
    }

    /// <summary>
    /// Durable exactly-once record for one intended XUI v3 renewal.
    /// </summary>
    /// <remarks>
    /// The row is written to <c>users.db</c> before the XUI mutation and survives process restarts. Its
    /// <see cref="OperationKey"/> is stable for one confirmation session (owned bots) or one tenant order, so
    /// Telegram redelivery, repeated confirm presses, and concurrent duplicate requests all resolve to the same row.
    /// The absolute <see cref="TargetTotalBytes"/> and <see cref="TargetExpiryTime"/> are computed once from the
    /// expected pre-renewal state and are the values sent to the panel; recovery only ever re-reads the panel and
    /// compares, it never re-computes another increment.
    /// </remarks>
    public class XuiV3RenewalOperation
    {
        /// <summary>Internal users.db primary key.</summary>
        public int Id { get; set; }

        /// <summary>
        /// Stable deduplication key. Owned renewals use <c>renew-{botId}-{renewalSessionId}</c>; tenant renewals use
        /// <c>tenant-renew-{tenantBotOrderId}</c>; legacy in-flight confirmations fall back to an intent hash.
        /// </summary>
        public string OperationKey { get; set; }

        /// <summary>
        /// Human-readable stable identifier such as <c>renew-{guid}</c> used in logs and the wallet ledger
        /// idempotency key. It is unique per operation and never contains secrets.
        /// </summary>
        public string OperationId { get; set; }

        /// <summary>Runtime bot id that owns the renewal flow; for tenant renewals this is the tenant storefront id.</summary>
        public string BotId { get; set; }

        /// <summary>Tenant storefront id when the renewal belongs to a tenant order; otherwise null for owned bots.</summary>
        public string TenantBotId { get; set; }

        /// <summary>Tenant order id when the renewal belongs to a paid tenant order; otherwise null.</summary>
        public string TenantBotOrderId { get; set; }

        /// <summary>Telegram user id of the payer or actor who confirmed the renewal.</summary>
        public long TelegramUserId { get; set; }

        /// <summary>XUI client email that is the panel target of the renewal.</summary>
        public string TargetEmail { get; set; }

        /// <summary>Normalized panel UUID lock when the renewal used an exact target; otherwise empty.</summary>
        public string TargetUuid { get; set; }

        /// <summary>Resolved service key that priced the renewal.</summary>
        public string ServiceKey { get; set; }

        /// <summary>Exact traffic added by this renewal in binary gigabytes.</summary>
        public int AddedTrafficGb { get; set; }

        /// <summary>Exact traffic added by this renewal in bytes.</summary>
        public long AddedTrafficBytes { get; set; }

        /// <summary>Exact duration added by this renewal in days; zero means lifetime for metered plans.</summary>
        public int AddedDurationDays { get; set; }

        /// <summary>Renewal price in Iranian toman used by settlement.</summary>
        public long PriceToman { get; set; }

        /// <summary>Selected payment method such as <c>credit</c> or <c>gozargah_site_wallet</c>.</summary>
        public string PaymentMethod { get; set; }

        /// <summary>Expected panel TotalGB in bytes read immediately before the mutation.</summary>
        public long ExpectedTotalBytesBefore { get; set; }

        /// <summary>Expected panel expiry in milliseconds (or negative first-connection duration) before the mutation.</summary>
        public long ExpectedExpiryTimeBefore { get; set; }

        /// <summary>Absolute XUI TotalGB target in bytes sent to the panel exactly once.</summary>
        public long TargetTotalBytes { get; set; }

        /// <summary>Absolute XUI expiry target in milliseconds (or negative first-connection duration) sent to the panel.</summary>
        public long TargetExpiryTime { get; set; }

        /// <summary>
        /// Full replacement payload JSON that was sent to the panel. A crash take-over replays this exact payload
        /// after a read-back proves the target is absent, so recovery never recomputes another increment.
        /// </summary>
        public string MutationPayloadJson { get; set; }

        /// <summary>Whether the renewal requires traffic counters to be reset after the panel update.</summary>
        public bool ShouldResetTraffic { get; set; }

        /// <summary>Whether unlimited renewal arithmetic was applied.</summary>
        public bool IsUnlimited { get; set; }

        /// <summary>Current lifecycle status from <see cref="XuiV3RenewalOperationStatuses"/>.</summary>
        public string Status { get; set; } = XuiV3RenewalOperationStatuses.Pending;

        /// <summary>Current settlement status from <see cref="XuiV3RenewalSettlementStatuses"/>.</summary>
        public string SettlementStatus { get; set; } = XuiV3RenewalSettlementStatuses.Pending;

        /// <summary>UTC time when the current settlement claim started; used to detect crashed settlement executors.</summary>
        public DateTime? SettlementStartedAtUtc { get; set; }

        /// <summary>UTC time when settlement completed; non-null means the wallet was debited and the ledger written.</summary>
        public DateTime? SettledAtUtc { get; set; }

        /// <summary>UTC time when the single central success log entry was sent for this renewal.</summary>
        public DateTime? SuccessLogSentAtUtc { get; set; }

        /// <summary>UTC lease deadline of the current processing claim; expired leases may be taken over.</summary>
        public DateTime LeaseUntilUtc { get; set; }

        /// <summary>
        /// Random claim token bound to the executor that currently holds the processing lease. The applied
        /// transition requires this token so a slow executor cannot mark applied after a crashed lease was taken over.
        /// </summary>
        public string ClaimToken { get; set; }

        /// <summary>UTC creation time of the operation row.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC time of the last status, lease, or settlement update.</summary>
        public DateTime? UpdatedAtUtc { get; set; }

        /// <summary>
        /// Last sanitized error kept for reconciliation. Panel URLs, response bodies, tokens, UUIDs, and raw
        /// exception messages are never stored here.
        /// </summary>
        public string LastError { get; set; }
    }
}
