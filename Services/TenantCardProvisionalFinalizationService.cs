using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services
{
    /// <summary>
    /// Terminal or interim outcome of one provisional same-client finalization attempt.
    /// </summary>
    /// <remarks>
    /// The vocabulary is deliberately closed. Callers map <see cref="Finalized" /> and
    /// <see cref="AlreadyFinalized" /> to the financial settlement boundary, and both <see cref="ManualReview" /> and
    /// <see cref="Retryable" /> to "do not settle". Nothing in this enumeration authorizes a wallet debit, a ledger
    /// entry, an order-fulfillment flag, or a customer sale notification.
    /// </remarks>
    public enum TenantCardProvisionalFinalizationStatus
    {
        /// <summary>The exact purchased entitlement was applied to the same client and proven by read-back.</summary>
        Finalized,

        /// <summary>The durable saga already recorded the proven final state, so nothing was mutated.</summary>
        AlreadyFinalized,

        /// <summary>An automated outcome stayed ambiguous; a human must reconcile before any further panel mutation.</summary>
        ManualReview,

        /// <summary>A transient failure left the saga in a safe, resumable state; no settlement may happen yet.</summary>
        Retryable
    }

    /// <summary>
    /// Everything the finalizer needs to upgrade one provisional client, all of it authoritative rather than inferred.
    /// </summary>
    /// <param name="TenantBotOrderId">Internal database id of the tenant card-to-card order being finalized.</param>
    /// <param name="OrderId">
    /// Public order id. It is the only part of the operation key derived from the order, so it must be the same value the
    /// provisional create used; reusing a key for a different order is rejected by the durable store.
    /// </param>
    /// <param name="ServerInfo">
    /// Authenticated panel connection settings resolved by the caller from the order's own server placement. The saga never
    /// guesses a panel and never reads it from a Telegram payload. Tokens inside this value must never be logged.
    /// </param>
    /// <param name="ExpectedEmail">Exact panel email of the provisional client that must be finalized.</param>
    /// <param name="ExpectedUuid">
    /// Exact protocol UUID recorded when the provisional client was created. A same-email client carrying a different UUID
    /// is a different credential and must never be finalized.
    /// </param>
    /// <param name="ExpectedSubId">Exact subscription id recorded at provisional creation; empty when the client has none.</param>
    /// <param name="ExpectedInboundIds">
    /// Inbound placement recorded at provisional creation. When non-empty the panel client's placement must equal this set
    /// exactly, because upgrading a client that drifted onto different inbounds would change the delivered subscription.
    /// </param>
    /// <param name="PurchasedQuotaBytes">
    /// Exact quota the customer paid for, in bytes, taken from the commercial order and never from measured usage. Zero
    /// means an unlimited/lifetime order and is written as zero.
    /// </param>
    /// <param name="PurchasedDurationDays">
    /// Purchased duration in whole days, or <c>null</c> for a lifetime order. It is converted to an absolute expiry exactly
    /// once, from a frozen timestamp, so retries cannot extend the paid period.
    /// </param>
    public sealed record TenantCardProvisionalFinalizationRequest(
        int TenantBotOrderId,
        string OrderId,
        ServerInfo ServerInfo,
        string ExpectedEmail,
        string ExpectedUuid,
        string ExpectedSubId,
        IReadOnlyList<int> ExpectedInboundIds,
        long PurchasedQuotaBytes,
        int? PurchasedDurationDays);

    /// <summary>
    /// Result of one finalization attempt, including the durable step the saga is proven to have reached.
    /// </summary>
    /// <param name="Status">Closed-vocabulary outcome; only the two finalized statuses may cross the settlement boundary.</param>
    /// <param name="OperationKey">Durable saga key, for audit and for operator tooling that resolves the row.</param>
    /// <param name="Step">Furthest durably proven <see cref="TenantCardProvisionalSteps" /> value after this attempt.</param>
    /// <param name="ReasonCode">
    /// Stable safe reason code when the outcome is <see cref="TenantCardProvisionalFinalizationStatus.ManualReview" /> or
    /// <see cref="TenantCardProvisionalFinalizationStatus.Retryable" />. Never raw panel or transport exception text.
    /// </param>
    public sealed record TenantCardProvisionalFinalizationResult(
        TenantCardProvisionalFinalizationStatus Status,
        string OperationKey,
        string Step,
        string ReasonCode);

    /// <summary>
    /// Upgrades the SAME provisional tenant card-to-card client to the exact purchased entitlement, crash-safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope.</b> This service mutates exactly one panel client and its durable saga row. It never creates a second
    /// account, never rotates a credential, never debits an owner wallet, never writes a ledger entry, never marks the order
    /// fulfilled, and never sends a customer notification. Financial settlement is a separate tail that may only run after
    /// this service reports <see cref="TenantCardProvisionalFinalizationStatus.Finalized" />.
    /// </para>
    /// <para>
    /// <b>The quiescence problem and the proven answer.</b> 3x-ui's official traffic reset
    /// (<c>POST /panel/api/clients/resetTraffic/{email}</c>) is the only operation that clears every usage source: the
    /// shared <c>client_traffics</c> row, master-pushed <c>client_global_traffics</c> rows, and per-inbound
    /// <c>node_client_traffics</c> rows. It also force-enables a client that is disabled when the reset runs. Proved
    /// against MHSanaei/3x-ui v3.7.0 source, both re-admission paths are guarded on the client being disabled:
    /// <c>ClientService.ResetTrafficByEmail</c> calls its update only under <c>if !rec.Enable</c>, and
    /// <c>InboundService.resetClientTrafficLocked</c> builds its runtime <c>AddUser</c> plan only under
    /// <c>if !traffic.Enable</c>. Because <c>enable</c> is the only instantaneous admission gate in 3x-ui — quota and
    /// expiry are enforced by the periodic traffic poll, not by Xray when a connection is opened — presenting a disabled
    /// client to the reset is precisely what would re-admit a credential the customer is not paying for during that
    /// window.
    /// </para>
    /// <para>
    /// The saga therefore does the opposite of the intuitive ordering. It never disables the client, so both upstream
    /// guards stay false, the reset performs no enable mutation, and the running Xray user set is not touched by the
    /// reset at all. Quota is written LAST, after the counters are cleared, so no intermediate state can pair the large
    /// purchased quota with a stale depletion baseline. The largest extra service any intermediate state can expose is
    /// therefore the provisional courtesy allowance, never the purchased entitlement, and the common path exposes none at
    /// all.
    /// </para>
    /// <para>
    /// <b>Restart safety.</b> Every mutation is preceded by a read and followed by a read-back; the durable step advances
    /// only after the read-back proved the effect. Mutations here are absolute-value writes (reset, counter write, quota
    /// write, enable), which the contract tests prove value-idempotent, so an ambiguous replay converges instead of
    /// compounding. Identity is re-proven before the first mutation, and a mismatch fails closed into manual review
    /// instead of mutating a client that may belong to someone else.
    /// </para>
    /// </remarks>
    public sealed class TenantCardProvisionalFinalizationService
    {
        /// <summary>Maximum extra counter-clear attempts after the final quota write before failing closed.</summary>
        /// <remarks>
        /// Each attempt is a side-effect-free absolute counter write. The bound exists so a client that keeps accruing
        /// usage (for example because a plan-level auto-renew or a remote node is still reporting) is escalated to a human
        /// rather than looping forever on the shared traffic writer.
        /// </remarks>
        private const int MaxPostQuotaCounterClears = 2;

        private readonly TenantCardProvisionalOperationStore _store;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TenantCardProvisionalFinalizationService> _logger;

        /// <summary>Creates the finalizer over the durable saga store and the runtime panel configuration.</summary>
        /// <param name="store">Required durable step store; the saga is never authorized by in-memory progress.</param>
        /// <param name="configuration">
        /// Runtime configuration supplying panel timeouts and retry policy. It never contains per-order state.
        /// </param>
        /// <param name="logger">Operational logger; entries are sanitized and never carry tokens or client credentials.</param>
        /// <exception cref="ArgumentNullException">Any required dependency is <c>null</c>.</exception>
        public TenantCardProvisionalFinalizationService(
            TenantCardProvisionalOperationStore store,
            IConfiguration configuration,
            ILogger<TenantCardProvisionalFinalizationService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Runs (or resumes) the same-client finalization saga for one approved tenant card-to-card order.
        /// </summary>
        /// <param name="request">Authoritative order, panel, and expected-identity inputs. See the record's own docs.</param>
        /// <param name="cancellationToken">Token that aborts the saga between steps without recording an unproven step.</param>
        /// <returns>
        /// A closed-vocabulary result. <see cref="TenantCardProvisionalFinalizationStatus.Finalized" /> and
        /// <see cref="TenantCardProvisionalFinalizationStatus.AlreadyFinalized" /> are the only statuses that permit the
        /// caller to cross the financial settlement boundary; both <see cref="TenantCardProvisionalFinalizationStatus.Retryable" />
        /// and <see cref="TenantCardProvisionalFinalizationStatus.ManualReview" /> require the order to stay unsettled.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request" /> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
        /// <remarks>
        /// Safe to call repeatedly for the same order: a saga that already reached
        /// <see cref="TenantCardProvisionalSteps.Proven" /> re-proves the panel state by read-back and returns
        /// <see cref="TenantCardProvisionalFinalizationStatus.AlreadyFinalized" /> without issuing any mutation, so a
        /// duplicate approval callback cannot reset traffic again, extend the expiry, rewrite the quota, or create a
        /// second client.
        /// </remarks>
        public async Task<TenantCardProvisionalFinalizationResult> FinalizeAsync(
            TenantCardProvisionalFinalizationRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ValidateRequest(request);
            var operationKey = BuildOperationKey(request.OrderId);

            var claim = await _store.ClaimAsync(
                operationKey,
                request.TenantBotOrderId,
                request.OrderId,
                TenantCardProvisionalOperationKinds.Finalize,
                cancellationToken).ConfigureAwait(false);
            var step = claim.Operation.Step;

            if (string.Equals(step, TenantCardProvisionalSteps.ManualReview, StringComparison.Ordinal))
                return Result(TenantCardProvisionalFinalizationStatus.ManualReview, operationKey, step, claim.Operation.ErrorCode);

            // Freeze the paid period before the first mutation. The store keeps the first writer's values, so a restart
            // resumes with the same quota and the same expiry instead of deriving a later expiry from the current clock.
            var frozen = await _store.FreezeFinalEntitlementAsync(
                operationKey,
                DateTime.UtcNow,
                request.PurchasedQuotaBytes,
                ComputeFrozenExpiry(DateTime.UtcNow, request.PurchasedDurationDays),
                cancellationToken).ConfigureAwait(false);
            var quotaBytes = frozen.FinalQuotaBytes ?? request.PurchasedQuotaBytes;
            var expiryTimeMs = frozen.FinalExpiryTimeMs ?? ComputeFrozenExpiry(DateTime.UtcNow, request.PurchasedDurationDays);

            // An already-proven saga is never re-mutated; it only re-reads and re-proves. Usage that appeared after the
            // quota write is still erased rather than absorbed, because erasing it grants nothing: the quota is already
            // the exact purchased amount and the customer is simply being given the fresh period they paid for.
            if (TenantCardProvisionalSteps.HasReached(step, TenantCardProvisionalSteps.QuotaWritten))
            {
                var reproof = await ProveFinalStateWithCounterRecoveryAsync(
                    request, quotaBytes, expiryTimeMs, cancellationToken).ConfigureAwait(false);
                if (reproof.Proven)
                {
                    if (!TenantCardProvisionalSteps.HasReached(step, TenantCardProvisionalSteps.Proven))
                        await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.Proven, reproof.Evidence, cancellationToken).ConfigureAwait(false);

                    return Result(TenantCardProvisionalFinalizationStatus.AlreadyFinalized, operationKey,
                        TenantCardProvisionalSteps.Proven, null);
                }

                return await ManualReviewAsync(operationKey, reproof.ReasonCode, cancellationToken).ConfigureAwait(false);
            }

            // Step 1: prove identity and reach the only state from which the reset is safe to issue.
            if (!TenantCardProvisionalSteps.HasReached(step, TenantCardProvisionalSteps.QuiescenceConfirmed))
            {
                var quiescence = await EnsureQuiescenceAsync(request, cancellationToken).ConfigureAwait(false);
                if (!quiescence.Proven)
                    return await FailAsync(operationKey, quiescence, cancellationToken).ConfigureAwait(false);

                await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.QuiescenceConfirmed, quiescence.Evidence, cancellationToken).ConfigureAwait(false);
                step = TenantCardProvisionalSteps.QuiescenceConfirmed;
            }

            // Step 2: the official reset, which is the only operation that clears master-pushed and node usage rows.
            if (!TenantCardProvisionalSteps.HasReached(step, TenantCardProvisionalSteps.Reset))
            {
                var reset = await EnsureTrafficResetAsync(request, cancellationToken).ConfigureAwait(false);
                if (!reset.Proven)
                    return await FailAsync(operationKey, reset, cancellationToken).ConfigureAwait(false);

                await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.Reset, reset.Evidence, cancellationToken).ConfigureAwait(false);
                step = TenantCardProvisionalSteps.Reset;
            }

            // Step 3: re-zero the shared counter with a side-effect-free absolute write, erasing anything that accrued
            // between the reset read-back and now.
            if (!TenantCardProvisionalSteps.HasReached(step, TenantCardProvisionalSteps.Zeroed))
            {
                var zeroed = await EnsureCountersZeroAsync(request, cancellationToken).ConfigureAwait(false);
                if (!zeroed.Proven)
                    return await FailAsync(operationKey, zeroed, cancellationToken).ConfigureAwait(false);

                await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.Zeroed, zeroed.Evidence, cancellationToken).ConfigureAwait(false);
                step = TenantCardProvisionalSteps.Zeroed;
            }

            // Step 4: write the exact purchased entitlement onto the SAME client, preserving its identity verbatim.
            var written = await EnsureFinalEntitlementAsync(request, quotaBytes, expiryTimeMs, cancellationToken).ConfigureAwait(false);
            if (!written.Proven)
                return await FailAsync(operationKey, written, cancellationToken).ConfigureAwait(false);

            await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.QuotaWritten, written.Evidence, cancellationToken).ConfigureAwait(false);

            // Step 5: prove the whole final state before the caller may settle.
            var final = await ProveFinalStateWithCounterRecoveryAsync(
                request, quotaBytes, expiryTimeMs, cancellationToken).ConfigureAwait(false);
            if (!final.Proven)
            {
                if (final.RequiresReview)
                    return await ManualReviewAsync(operationKey, final.ReasonCode, cancellationToken).ConfigureAwait(false);

                return await FailAsync(operationKey, final, cancellationToken).ConfigureAwait(false);
            }

            await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.Proven, final.Evidence, cancellationToken).ConfigureAwait(false);
            return Result(TenantCardProvisionalFinalizationStatus.Finalized, operationKey, TenantCardProvisionalSteps.Proven, null);
        }

        /// <summary>Rejects inputs that could otherwise target the wrong client or an invalid entitlement.</summary>
        /// <param name="request">Request to validate.</param>
        /// <exception cref="ArgumentException">A required identity value is missing, or the order id is unusable.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The purchased quota or duration is invalid.</exception>
        /// <remarks>
        /// Validation is deliberately strict and runs before the durable claim, so a malformed request cannot leave a
        /// claimed saga row behind for an order that was never legitimately finalizable.
        /// </remarks>
        private static void ValidateRequest(TenantCardProvisionalFinalizationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.OrderId))
                throw new ArgumentException("A public order id is required to build the provisional operation key.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ExpectedEmail))
                throw new ArgumentException("The exact provisional client email is required for identity verification.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ExpectedUuid))
                throw new ArgumentException("The exact provisional client UUID is required for identity verification.", nameof(request));
            if (request.ServerInfo == null)
                throw new ArgumentException("The panel connection for the order's own server placement is required.", nameof(request));
            if (request.PurchasedQuotaBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(request), "A purchased quota cannot be negative; zero means unlimited.");
            if (request.PurchasedDurationDays is <= 0)
                throw new ArgumentOutOfRangeException(nameof(request), "A purchased duration must be a positive number of days, or null for a lifetime order.");
        }

        /// <summary>Builds the durable saga key for one order's finalize operation.</summary>
        /// <param name="orderId">Public tenant order id.</param>
        /// <returns>The stable key in <c>tenant-card-finalize:{orderId}</c> form.</returns>
        private static string BuildOperationKey(string orderId)
            => $"tenant-card-{TenantCardProvisionalOperationKinds.Finalize}:{orderId}";

        /// <summary>Converts the purchased duration into the absolute expiry written to the panel.</summary>
        /// <param name="effectiveAtUtc">Frozen instant the purchased period starts from.</param>
        /// <param name="purchasedDurationDays">Purchased whole days, or <c>null</c> for a lifetime order.</param>
        /// <returns>
        /// Absolute expiry in Unix milliseconds, or <c>0</c> for a lifetime order. Zero is the panel's own lifetime
        /// representation and is never replaced with a computed date.
        /// </returns>
        private static long ComputeFrozenExpiry(DateTime effectiveAtUtc, int? purchasedDurationDays)
            => purchasedDurationDays is null
                ? 0L
                : new DateTimeOffset(DateTime.SpecifyKind(effectiveAtUtc, DateTimeKind.Utc))
                    .AddDays(purchasedDurationDays.Value)
                    .ToUnixTimeMilliseconds();

        /// <summary>Builds the immutable result value for one outcome.</summary>
        /// <param name="status">Closed-vocabulary outcome.</param>
        /// <param name="operationKey">Durable saga key.</param>
        /// <param name="step">Furthest durably proven step.</param>
        /// <param name="reasonCode">Safe reason code, or <c>null</c> on success.</param>
        /// <returns>The result returned to the caller.</returns>
        private static TenantCardProvisionalFinalizationResult Result(
            TenantCardProvisionalFinalizationStatus status, string operationKey, string step, string reasonCode)
            => new(status, operationKey, step, reasonCode);

        /// <summary>Escalates a row to terminal manual review and returns the manual-review outcome.</summary>
        /// <param name="operationKey">Durable saga key.</param>
        /// <param name="reasonCode">Stable safe reason code; never raw exception text.</param>
        /// <param name="cancellationToken">Cancellation token for the short update transaction.</param>
        /// <returns>The manual-review result, carrying the terminal step.</returns>
        private async Task<TenantCardProvisionalFinalizationResult> ManualReviewAsync(
            string operationKey, string reasonCode, CancellationToken cancellationToken)
        {
            await _store.MarkManualReviewAsync(operationKey, reasonCode, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Provisional finalization requires manual review. operationKey={OperationKey} reason={ReasonCode}",
                operationKey, reasonCode);
            return Result(TenantCardProvisionalFinalizationStatus.ManualReview, operationKey,
                TenantCardProvisionalSteps.ManualReview, reasonCode);
        }

        /// <summary>
        /// Converts a failed step probe into either a retryable or a terminal manual-review outcome.
        /// </summary>
        /// <param name="operationKey">Durable saga key.</param>
        /// <param name="probe">Failed step probe carrying its stable reason code.</param>
        /// <param name="cancellationToken">Cancellation token for the short update transaction.</param>
        /// <returns>The outcome the caller must honor; no settlement is ever authorized here.</returns>
        /// <remarks>
        /// A panel/transport failure is retryable because no unproven step is recorded and the next attempt re-reads
        /// before mutating. An identity or placement mismatch is terminal because it means the saga is pointed at a
        /// client that is not provably this order's provisional client, which a retry cannot resolve.
        /// </remarks>
        private Task<TenantCardProvisionalFinalizationResult> FailAsync(
            string operationKey, StepProbe probe, CancellationToken cancellationToken)
            => probe.RequiresReview
                ? ManualReviewAsync(operationKey, probe.ReasonCode, cancellationToken)
                : Task.FromResult(Result(TenantCardProvisionalFinalizationStatus.Retryable, operationKey,
                    TenantCardProvisionalSteps.Claimed, probe.ReasonCode));

        /// <summary>
        /// Proves the exact client identity and reaches the only state from which the official reset is safe to issue.
        /// </summary>
        /// <param name="request">Authoritative expected identity and panel connection.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>
        /// A probe whose <c>Proven</c> flag is set when identity matched and the client is enabled. The evidence records
        /// only non-secret comparison values.
        /// </returns>
        /// <remarks>
        /// When the client is found disabled it is enabled through the ordinary update path while keeping whatever quota
        /// and expiry the panel already had for it. That is what keeps the later reset from firing its auto-enable branch:
        /// the reset must be presented with an enabled client for its re-admission guards to stay false. The step is
        /// skipped when the panel already reports the client enabled, so the common path performs no mutation at all.
        /// </remarks>
        private async Task<StepProbe> EnsureQuiescenceAsync(
            TenantCardProvisionalFinalizationRequest request, CancellationToken cancellationToken)
        {
            var clientProbe = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!clientProbe.Proven)
                return clientProbe;

            var identity = VerifyIdentity(request, clientProbe.Client);
            if (!identity.Proven)
                return identity;

            if (clientProbe.Client.Enable)
                return StepProbe.Success(Evidence("identity", "matched", "enable", "true", "mutation", "none"));

            var enableResponse = await ApiServicev3.SetClientEnabledAsync(
                request.ServerInfo, _configuration, request.ExpectedEmail, true, cancellationToken).ConfigureAwait(false);

            if (!IsSuccess(enableResponse))
                return StepProbe.Transport("provisional_enable_request_failed");

            var readBack = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!readBack.Proven)
                return readBack;

            return readBack.Client.Enable
                ? StepProbe.Success(Evidence("identity", "matched", "enable", "true", "mutation", "enable_preserving_provisional_quota"))
                : StepProbe.FailClosed("provisional_enable_not_applied");
        }

        /// <summary>Issues the official traffic reset and proves every usage source was cleared.</summary>
        /// <param name="request">Authoritative panel connection and expected identity.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>
        /// A probe whose <c>Proven</c> flag is set when the client is enabled, the shared counters are zero, and no
        /// master-pushed or per-inbound node usage row remains.
        /// </returns>
        /// <remarks>
        /// The reset is proven value-idempotent by the contract tests, so an ambiguous response is reconciled by
        /// re-reading first and only re-issuing when the read-back does not already show the cleared state. That read-back
        /// requirement is what makes the replay a reconciliation rather than a blind retry.
        /// </remarks>
        private async Task<StepProbe> EnsureTrafficResetAsync(
            TenantCardProvisionalFinalizationRequest request, CancellationToken cancellationToken)
        {
            var before = await ReadTrafficAsync(request, cancellationToken).ConfigureAwait(false);
            if (!before.Proven)
                return before;

            // Identity and enable state are re-proven immediately before the reset, because a concurrent operator action
            // between two steps must not turn this into a mutation against a client that no longer matches.
            var clientProbe = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!clientProbe.Proven)
                return clientProbe;

            var identity = VerifyIdentity(request, clientProbe.Client);
            if (!identity.Proven)
                return identity;

            if (!clientProbe.Client.Enable)
                return StepProbe.FailClosed("provisional_client_disabled_before_reset");

            var alreadyCleared = before.Traffic.Up == 0 && before.Traffic.Down == 0;

            if (!alreadyCleared)
            {
                var resetResponse = await ApiServicev3.ResetClientTrafficAsync(
                    request.ServerInfo, _configuration, request.ExpectedEmail, cancellationToken).ConfigureAwait(false);

                if (!IsSuccess(resetResponse))
                    return StepProbe.Transport("provisional_reset_request_failed");
            }

            var after = await ReadTrafficAsync(request, cancellationToken).ConfigureAwait(false);
            if (!after.Proven)
                return after;

            if (after.Traffic.Up != 0 || after.Traffic.Down != 0)
                return StepProbe.Transport("provisional_reset_not_reflected");

            return StepProbe.Success(Evidence(
                "up", after.Traffic.Up.ToString(CultureInfo.InvariantCulture),
                "down", after.Traffic.Down.ToString(CultureInfo.InvariantCulture),
                "enable", after.Traffic.Enable ? "true" : "false",
                "resetIssued", alreadyCleared ? "false" : "true"));
        }

        /// <summary>Writes absolute zero counters and proves the shared usage row is empty.</summary>
        /// <param name="request">Authoritative panel connection.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>A probe whose <c>Proven</c> flag is set when both counters read back as zero.</returns>
        /// <remarks>
        /// This is deliberately not a substitute for the official reset: it cannot clear master-pushed global rows or
        /// per-inbound node rows. It exists to erase whatever accrued inside the reset window, and it has no enable,
        /// runtime, or inbound-settings side effect, so it can never re-admit a credential.
        /// </remarks>
        private async Task<StepProbe> EnsureCountersZeroAsync(
            TenantCardProvisionalFinalizationRequest request, CancellationToken cancellationToken)
        {
            var before = await ReadTrafficAsync(request, cancellationToken).ConfigureAwait(false);
            if (!before.Proven)
                return before;

            if (before.Traffic.Up != 0 || before.Traffic.Down != 0)
            {
                var response = await ApiServicev3.UpdateClientTrafficAsync(
                    request.ServerInfo, _configuration, request.ExpectedEmail, 0, 0, cancellationToken).ConfigureAwait(false);

                if (!IsSuccess(response))
                    return StepProbe.Transport("provisional_counter_clear_failed");
            }

            var after = await ReadTrafficAsync(request, cancellationToken).ConfigureAwait(false);
            if (!after.Proven)
                return after;

            return after.Traffic.Up == 0 && after.Traffic.Down == 0
                ? StepProbe.Success(Evidence("up", "0", "down", "0"))
                : StepProbe.Transport("provisional_counters_not_quiescent");
        }

        /// <summary>Writes the frozen purchased entitlement onto the same client, preserving its identity verbatim.</summary>
        /// <param name="request">Authoritative panel connection and expected identity.</param>
        /// <param name="quotaBytes">Frozen exact purchased quota in bytes.</param>
        /// <param name="expiryTimeMs">Frozen absolute expiry, or <c>0</c> for a lifetime order.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>A probe whose <c>Proven</c> flag is set when the write was accepted and read back.</returns>
        /// <remarks>
        /// The payload is the panel's own client record with only <c>totalGB</c>, <c>expiryTime</c>, and <c>enable</c>
        /// replaced, so the UUID, password, subId, flow, group, and every extension field are carried through unchanged.
        /// Writing the panel record back is also why a previously applied write is detected by read-back instead of being
        /// re-issued.
        /// </remarks>
        private async Task<StepProbe> EnsureFinalEntitlementAsync(
            TenantCardProvisionalFinalizationRequest request,
            long quotaBytes,
            long expiryTimeMs,
            CancellationToken cancellationToken)
        {
            var clientProbe = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!clientProbe.Proven)
                return clientProbe;

            var identity = VerifyIdentity(request, clientProbe.Client);
            if (!identity.Proven)
                return identity;

            var client = clientProbe.Client;
            var alreadyApplied = client.TotalGB == quotaBytes
                                 && client.ExpiryTime == expiryTimeMs
                                 && client.Enable;

            if (!alreadyApplied)
            {
                client.TotalGB = quotaBytes;
                client.ExpiryTime = expiryTimeMs;
                client.Enable = true;

                var response = await ApiServicev3.UpdateClientAsync(
                    request.ServerInfo, _configuration, request.ExpectedEmail, client, cancellationToken).ConfigureAwait(false);

                if (!IsSuccess(response))
                    return StepProbe.Transport("provisional_final_quota_write_failed");
            }

            var readBack = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!readBack.Proven)
                return readBack;

            // Identity is re-verified after the write: an update that silently replaced the credential would break the
            // customer's existing subscription link, which is not an outcome a settlement tail may paper over.
            var postIdentity = VerifyIdentity(request, readBack.Client);
            if (!postIdentity.Proven)
                return postIdentity;

            return readBack.Client.TotalGB == quotaBytes
                   && readBack.Client.ExpiryTime == expiryTimeMs
                   && readBack.Client.Enable
                ? StepProbe.Success(Evidence(
                    "totalBytes", readBack.Client.TotalGB.ToString(CultureInfo.InvariantCulture),
                    "expiryTimeMs", readBack.Client.ExpiryTime.ToString(CultureInfo.InvariantCulture),
                    "enable", "true",
                    "writeIssued", alreadyApplied ? "false" : "true"))
                : StepProbe.Transport("provisional_final_quota_not_reflected");
        }

        /// <summary>
        /// Proves the final state, re-zeroing usage that appeared after the quota write a bounded number of times.
        /// </summary>
        /// <param name="request">Authoritative panel connection and expected identity.</param>
        /// <param name="quotaBytes">Frozen exact purchased quota in bytes.</param>
        /// <param name="expiryTimeMs">Frozen absolute expiry, or <c>0</c> for a lifetime order.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>The probe for the final proof, or for the last failed re-clear attempt.</returns>
        /// <remarks>
        /// A customer still using the provisional client can accrue usage between the quota write and the final read-back.
        /// Re-zeroing that usage is the correct response and never inflates the entitlement: the quota is already the
        /// exact purchased amount, so erasing stray usage only restores the fresh period the customer paid for. The loop
        /// is bounded so a client that keeps accruing — a plan-level auto-renew, or a remote node still reporting — is
        /// escalated to a human instead of holding the shared traffic writer indefinitely.
        /// </remarks>
        private async Task<StepProbe> ProveFinalStateWithCounterRecoveryAsync(
            TenantCardProvisionalFinalizationRequest request,
            long quotaBytes,
            long expiryTimeMs,
            CancellationToken cancellationToken)
        {
            var proof = await ProveFinalStateAsync(request, quotaBytes, expiryTimeMs, cancellationToken).ConfigureAwait(false);
            if (proof.Proven || proof.ReasonCode != FinalizationReasonCodes.CountersNotQuiescent)
                return proof;

            for (var attempt = 0; attempt < MaxPostQuotaCounterClears; attempt++)
            {
                var reclear = await EnsureCountersZeroAsync(request, cancellationToken).ConfigureAwait(false);
                if (!reclear.Proven)
                    return reclear;

                proof = await ProveFinalStateAsync(request, quotaBytes, expiryTimeMs, cancellationToken).ConfigureAwait(false);
                if (proof.Proven)
                    return proof;

                if (proof.ReasonCode != FinalizationReasonCodes.CountersNotQuiescent)
                    return proof;
            }

            return proof;
        }

        /// <summary>Proves the complete final state of the same client before any settlement is permitted.</summary>
        /// <param name="request">Authoritative panel connection and expected identity.</param>
        /// <param name="quotaBytes">Frozen exact purchased quota in bytes.</param>
        /// <param name="expiryTimeMs">Frozen absolute expiry, or <c>0</c> for a lifetime order.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>
        /// A probe whose <c>Proven</c> flag is set only when quota, expiry, enable state, identity, placement, and zero
        /// provisional usage are all proven at once.
        /// </returns>
        /// <remarks>
        /// This read-back is the boundary the caller's settlement tail must gate on. It re-reads rather than trusting the
        /// write response, so a write that the panel accepted but did not persist cannot authorize a debit.
        /// </remarks>
        private async Task<StepProbe> ProveFinalStateAsync(
            TenantCardProvisionalFinalizationRequest request,
            long quotaBytes,
            long expiryTimeMs,
            CancellationToken cancellationToken)
        {
            var clientProbe = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!clientProbe.Proven)
                return clientProbe;

            var identity = VerifyIdentity(request, clientProbe.Client);
            if (!identity.Proven)
                return identity;

            var trafficProbe = await ReadTrafficAsync(request, cancellationToken).ConfigureAwait(false);
            if (!trafficProbe.Proven)
                return trafficProbe;

            if (clientProbe.Client.TotalGB != quotaBytes)
                return StepProbe.FailClosed("provisional_final_quota_mismatch");

            if (clientProbe.Client.ExpiryTime != expiryTimeMs)
                return StepProbe.FailClosed("provisional_final_expiry_mismatch");

            if (!clientProbe.Client.Enable)
                return StepProbe.FailClosed("provisional_final_client_disabled");

            // The purchased allowance must start from zero usage. It is never inflated to absorb provisional usage, which
            // is why a non-zero counter escalates to a bounded re-clear and then to manual review.
            if (trafficProbe.Traffic.Up != 0 || trafficProbe.Traffic.Down != 0)
                return StepProbe.FailClosed(FinalizationReasonCodes.CountersNotQuiescent);

            return StepProbe.Success(Evidence(
                "identity", "matched",
                "totalBytes", clientProbe.Client.TotalGB.ToString(CultureInfo.InvariantCulture),
                "expiryTimeMs", clientProbe.Client.ExpiryTime.ToString(CultureInfo.InvariantCulture),
                "enable", "true",
                "up", trafficProbe.Traffic.Up.ToString(CultureInfo.InvariantCulture),
                "down", trafficProbe.Traffic.Down.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>Reads the panel client and converts transport failures into a retryable probe.</summary>
        /// <param name="request">Authoritative panel connection.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>A probe carrying the client record when the read succeeded.</returns>
        private async Task<StepProbe> ReadClientAsync(
            TenantCardProvisionalFinalizationRequest request, CancellationToken cancellationToken)
        {
            var response = await ApiServicev3.GetClientAsync(
                request.ServerInfo, _configuration, request.ExpectedEmail, cancellationToken).ConfigureAwait(false);

            return IsSuccess(response) && response.Obj != null
                ? StepProbe.Success(null, response.Obj)
                : StepProbe.Transport("provisional_client_read_failed");
        }

        /// <summary>Reads the shared traffic row and converts transport failures into a retryable probe.</summary>
        /// <param name="request">Authoritative panel connection.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>A probe carrying the traffic record when the read succeeded.</returns>
        private async Task<StepProbe> ReadTrafficAsync(
            TenantCardProvisionalFinalizationRequest request, CancellationToken cancellationToken)
        {
            var response = await ApiServicev3.GetClientTrafficAsync(
                request.ServerInfo, _configuration, request.ExpectedEmail, cancellationToken).ConfigureAwait(false);

            return IsSuccess(response) && response.Obj != null
                ? StepProbe.Success(null, null, response.Obj)
                : StepProbe.Transport("provisional_traffic_read_failed");
        }

        /// <summary>Verifies that a panel client really is the provisional client this order expects.</summary>
        /// <param name="request">Authoritative expected identity and placement.</param>
        /// <param name="client">Panel client read back from the panel.</param>
        /// <returns>A proven probe on success, or a fail-closed probe naming the mismatched field.</returns>
        /// <remarks>
        /// Email comparison is case-insensitive because 3x-ui itself resolves panel clients by
        /// <c>strings.EqualFold</c>; UUID, subId, and placement are compared exactly. Any mismatch is terminal rather than
        /// retryable: a same-email client with a different credential is a different customer's account, and mutating it
        /// would corrupt an unrelated subscription.
        /// </remarks>
        private static StepProbe VerifyIdentity(TenantCardProvisionalFinalizationRequest request, XuiV3Client client)
        {
            if (client == null)
                return StepProbe.FailClosed("provisional_identity_missing");

            if (!string.Equals(client.Email, request.ExpectedEmail, StringComparison.OrdinalIgnoreCase))
                return StepProbe.FailClosed("provisional_identity_email_mismatch");

            if (!string.Equals(client.Uuid, request.ExpectedUuid, StringComparison.Ordinal))
                return StepProbe.FailClosed("provisional_identity_uuid_mismatch");

            if (!string.IsNullOrEmpty(request.ExpectedSubId)
                && !string.Equals(client.SubId, request.ExpectedSubId, StringComparison.Ordinal))
                return StepProbe.FailClosed("provisional_identity_subid_mismatch");

            if (request.ExpectedInboundIds.Count > 0)
            {
                var expected = request.ExpectedInboundIds.Distinct().OrderBy(value => value).ToArray();
                var actual = (client.InboundIds ?? new List<int>()).Distinct().OrderBy(value => value).ToArray();
                if (!expected.SequenceEqual(actual))
                    return StepProbe.FailClosed("provisional_identity_placement_mismatch");
            }

            return StepProbe.Success(Evidence("identity", "matched"));
        }

        /// <summary>Builds a compact sanitized evidence string for one proven step.</summary>
        /// <param name="pairs">Alternating key/value pairs of non-secret comparison values.</param>
        /// <returns>A JSON object string safe to persist on the saga row.</returns>
        /// <remarks>
        /// Only comparison values are accepted here. Panel tokens, response bodies, subscription links, and passwords are
        /// deliberately never passed to this helper.
        /// </remarks>
        private static string Evidence(params string[] pairs)
        {
            var builder = new System.Text.StringBuilder("{");
            for (var index = 0; index + 1 < pairs.Length; index += 2)
            {
                if (index > 0)
                    builder.Append(',');
                builder.Append('"').Append(pairs[index]).Append("\":\"");
                builder.Append((pairs[index + 1] ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\""));
                builder.Append('"');
            }

            return builder.Append('}').ToString();
        }

        /// <summary>Determines whether an API envelope represents an accepted request.</summary>
        /// <param name="response">Response envelope returned by the panel client.</param>
        /// <returns><c>true</c> when the panel reported success; otherwise <c>false</c>.</returns>
        private static bool IsSuccess<T>(XuiV3ApiResponse<T> response)
            => response != null && response.Success;

        /// <summary>Outcome of one saga step probe.</summary>
        /// <remarks>
        /// A probe carries no policy: it only reports whether the step's effect is proven, whether a failure must be
        /// escalated to a human, and a stable reason code. Keeping policy in the saga method is what makes the
        /// read-decide-mutate-prove order auditable in one place.
        /// </remarks>
        private sealed class StepProbe
        {
            private StepProbe()
            {
            }

            /// <summary>Gets a value indicating whether the step's effect is durably proven.</summary>
            public bool Proven { get; private init; }

            /// <summary>Gets a value indicating whether a failure must be escalated instead of retried.</summary>
            public bool RequiresReview { get; private init; }

            /// <summary>Gets the stable safe reason code for a failed probe.</summary>
            public string ReasonCode { get; private init; }

            /// <summary>Gets the sanitized evidence recorded when the step is proven.</summary>
            public string Evidence { get; private init; }

            /// <summary>Gets the panel client read during this probe, when the probe performed a client read.</summary>
            public XuiV3Client Client { get; private init; }

            /// <summary>Gets the panel traffic row read during this probe, when the probe performed a traffic read.</summary>
            public XuiV3ClientTraffic Traffic { get; private init; }

            /// <summary>Creates a proven probe.</summary>
            /// <param name="evidence">Sanitized evidence for the proven step.</param>
            /// <param name="client">Panel client read during the probe, when applicable.</param>
            /// <param name="traffic">Panel traffic row read during the probe, when applicable.</param>
            /// <returns>A proven probe.</returns>
            public static StepProbe Success(string evidence, XuiV3Client client = null, XuiV3ClientTraffic traffic = null)
                => new() { Proven = true, Evidence = evidence, Client = client, Traffic = traffic };

            /// <summary>Creates a retryable probe for a transient panel or transport failure.</summary>
            /// <param name="reasonCode">Stable safe reason code.</param>
            /// <returns>A probe that keeps the saga resumable and authorizes no settlement.</returns>
            public static StepProbe Transport(string reasonCode)
                => new() { Proven = false, RequiresReview = false, ReasonCode = reasonCode };

            /// <summary>Creates a terminal probe requiring human reconciliation.</summary>
            /// <param name="reasonCode">Stable safe reason code.</param>
            /// <returns>A probe that moves the row to manual review.</returns>
            public static StepProbe FailClosed(string reasonCode)
                => new() { Proven = false, RequiresReview = true, ReasonCode = reasonCode };
        }
    }

    /// <summary>
    /// Closed-vocabulary reason codes recorded when a provisional finalization cannot proceed automatically.
    /// </summary>
    /// <remarks>
    /// The codes are persisted on the saga row and surfaced to operators, so they must stay stable and must never carry
    /// panel or transport exception text.
    /// </remarks>
    public static class FinalizationReasonCodes
    {
        /// <summary>The purchased allowance could not be proven to start from zero usage.</summary>
        public const string CountersNotQuiescent = "provisional_counters_not_quiescent";
    }
}
