using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services
{
    /// <summary>
    /// Closed-vocabulary outcome of one provisional tenant card-to-card revocation attempt.
    /// </summary>
    /// <remarks>
    /// Revocation never settles money in either direction. A rejected receipt leaves the order unfulfilled and the
    /// courtesy client unusable, and no wallet debit, ledger row, or final-sale notification is produced.
    /// </remarks>
    public enum TenantCardProvisionalRevocationStatus
    {
        /// <summary>The exact provisional client is proven disabled and the customer no longer has access.</summary>
        Revoked,

        /// <summary>The durable saga already recorded the revocation; the panel was re-proven disabled.</summary>
        AlreadyRevoked,

        /// <summary>The order has no provisional client, so there is nothing to revoke.</summary>
        NoProvisionalClient,

        /// <summary>An automated outcome stayed ambiguous; a human must reconcile before any further panel mutation.</summary>
        ManualReview,

        /// <summary>A transient failure left the saga in a safe, resumable state.</summary>
        Retryable
    }

    /// <summary>
    /// Result of one provisional revocation attempt, including the durable saga step that is proven to hold.
    /// </summary>
    /// <param name="Status">Closed-vocabulary outcome of this attempt.</param>
    /// <param name="OperationKey">Durable revoke saga key, in <c>tenant-card-revoke:{orderId}</c> form.</param>
    /// <param name="Step">Furthest durably proven <see cref="TenantCardProvisionalSteps" /> value after this attempt.</param>
    /// <param name="ReasonCode">Stable secret-free reason code for a non-terminal outcome; <c>null</c> on success.</param>
    public sealed record TenantCardProvisionalRevocationResult(
        TenantCardProvisionalRevocationStatus Status,
        string OperationKey,
        string Step,
        string ReasonCode);

    /// <summary>
    /// Everything the revoker needs to disable one provisional client, all of it authoritative rather than inferred.
    /// </summary>
    /// <param name="TenantBotOrderId">Internal database id of the tenant card-to-card order being revoked.</param>
    /// <param name="OrderId">Public order id; the only part of the durable operation key derived from the order.</param>
    /// <param name="ServerInfo">
    /// Authenticated panel connection resolved by the caller from the order's own server placement. Never logged.
    /// </param>
    /// <param name="ExpectedEmail">Exact panel email of the provisional client that must be disabled.</param>
    /// <param name="ExpectedUuid">
    /// Exact protocol UUID recorded at provisional creation. A same-email client with a different UUID is a different
    /// credential and must never be disabled.
    /// </param>
    /// <param name="ExpectedSubId">Exact subscription id recorded at creation; empty when the client has none.</param>
    /// <param name="ExpectedInboundIds">
    /// Inbound placement recorded at creation. When non-empty the panel client's placement must equal this set exactly.
    /// </param>
    public sealed record TenantCardProvisionalRevocationRequest(
        int TenantBotOrderId,
        string OrderId,
        ServerInfo ServerInfo,
        string ExpectedEmail,
        string ExpectedUuid,
        string ExpectedSubId,
        IReadOnlyList<int> ExpectedInboundIds);

    /// <summary>
    /// Disables the SAME provisional tenant card-to-card client after the owner rejects the receipt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a dedicated saga.</b> Marking the receipt rejected is not enough once a courtesy client exists: the
    /// customer would keep a working credential for a payment the owner refused. Revocation is therefore a durable,
    /// restart-safe mutation of exactly one panel client, recorded under the operation key
    /// <c>tenant-card-revoke:{orderId}</c> so it can never collide with the finalize or create keys.
    /// </para>
    /// <para>
    /// <b>Why this direction is safe.</b> The finalize saga must never disable the client, because a disabled client is
    /// exactly the state that makes 3x-ui's official traffic reset re-admit the credential. Revoke is the opposite case:
    /// it wants the client disabled and never issues a traffic reset, so no upstream guard can undo its effect.
    /// </para>
    /// <para>
    /// <b>Identity first.</b> Email, UUID, subId, and placement are verified against the live panel before the mutation.
    /// A mismatch is terminal and produces no panel change, because disabling a same-email client that belongs to another
    /// customer would cut off an unrelated subscription.
    /// </para>
    /// <para>
    /// <b>Scope.</b> This service never debits or credits a wallet, never writes a ledger row, never marks an order
    /// fulfilled, and never sends a final-sale notification.
    /// </para>
    /// </remarks>
    public sealed class TenantCardProvisionalRevocationService
    {
        private readonly TenantCardProvisionalOperationStore _store;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TenantCardProvisionalRevocationService> _logger;

        /// <summary>Creates the revoker over the durable saga store and the runtime panel configuration.</summary>
        /// <param name="store">Required durable step store; revocation is never authorized by in-memory progress.</param>
        /// <param name="configuration">Runtime configuration supplying panel timeouts and retry policy.</param>
        /// <param name="logger">Operational logger; entries never carry tokens or client credentials.</param>
        /// <exception cref="ArgumentNullException">Any required dependency is <c>null</c>.</exception>
        public TenantCardProvisionalRevocationService(
            TenantCardProvisionalOperationStore store,
            IConfiguration configuration,
            ILogger<TenantCardProvisionalRevocationService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Builds the durable revoke saga key for one order.</summary>
        /// <param name="orderId">Public tenant order id.</param>
        /// <returns>The stable key in <c>tenant-card-revoke:{orderId}</c> form.</returns>
        public static string BuildOperationKey(string orderId)
            => $"tenant-card-{TenantCardProvisionalOperationKinds.Revoke}:{orderId}";

        /// <summary>
        /// Runs (or resumes) the revocation saga for one rejected tenant card-to-card order.
        /// </summary>
        /// <param name="request">Authoritative order, panel, and expected-identity inputs. See the record's own docs.</param>
        /// <param name="cancellationToken">Token that aborts the saga between steps without recording an unproven step.</param>
        /// <returns>
        /// A closed-vocabulary result. Only <see cref="TenantCardProvisionalRevocationStatus.Revoked" /> and
        /// <see cref="TenantCardProvisionalRevocationStatus.AlreadyRevoked" /> prove the customer no longer has access.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request" /> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
        /// <remarks>
        /// Safe to call repeatedly: a saga that already reached <see cref="TenantCardProvisionalSteps.Revoked" /> only
        /// re-reads and re-proves the disabled state, so a duplicate reject callback cannot issue another mutation.
        /// </remarks>
        public async Task<TenantCardProvisionalRevocationResult> RevokeAsync(
            TenantCardProvisionalRevocationRequest request,
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
                TenantCardProvisionalOperationKinds.Revoke,
                cancellationToken).ConfigureAwait(false);
            var step = claim.Operation.Step;

            if (string.Equals(step, TenantCardProvisionalSteps.ManualReview, StringComparison.Ordinal))
                return Result(TenantCardProvisionalRevocationStatus.ManualReview, operationKey, step, claim.Operation.ErrorCode);

            if (TenantCardProvisionalSteps.HasReached(step, TenantCardProvisionalSteps.Revoked))
            {
                var reproof = await ProveDisabledAsync(request, cancellationToken).ConfigureAwait(false);
                return reproof.Proven
                    ? Result(TenantCardProvisionalRevocationStatus.AlreadyRevoked, operationKey,
                        TenantCardProvisionalSteps.Revoked, null)
                    : await FailAsync(operationKey, reproof, cancellationToken).ConfigureAwait(false);
            }

            var clientProbe = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!clientProbe.Proven)
                return await FailAsync(operationKey, clientProbe, cancellationToken).ConfigureAwait(false);

            var identity = VerifyIdentity(request, clientProbe.Client);
            if (!identity.Proven)
                return await FailAsync(operationKey, identity, cancellationToken).ConfigureAwait(false);

            if (clientProbe.Client.Enable)
            {
                var disableResponse = await ApiServicev3.SetClientEnabledAsync(
                    request.ServerInfo, _configuration, request.ExpectedEmail, false, cancellationToken).ConfigureAwait(false);

                if (!IsSuccess(disableResponse))
                {
                    // An ambiguous disable is never blindly replayed: the read-back below decides whether it took effect.
                    _logger.LogWarning(
                        "Provisional revoke disable outcome was ambiguous; reconciling by read-back. operationKey={OperationKey}",
                        operationKey);
                }
            }

            var disabled = await ProveDisabledAsync(request, cancellationToken).ConfigureAwait(false);
            if (!disabled.Proven)
                return await FailAsync(operationKey, disabled, cancellationToken).ConfigureAwait(false);

            await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.Disabled, disabled.Evidence, cancellationToken).ConfigureAwait(false);
            await _store.AdvanceAsync(operationKey, TenantCardProvisionalSteps.Revoked, disabled.Evidence, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Provisional tenant card account revoked. operationKey={OperationKey}", operationKey);
            return Result(TenantCardProvisionalRevocationStatus.Revoked, operationKey, TenantCardProvisionalSteps.Revoked, null);
        }

        /// <summary>Rejects inputs that could otherwise target the wrong client.</summary>
        /// <param name="request">Request to validate.</param>
        /// <exception cref="ArgumentException">A required identity value is missing.</exception>
        private static void ValidateRequest(TenantCardProvisionalRevocationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.OrderId))
                throw new ArgumentException("A public order id is required to build the revoke operation key.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ExpectedEmail))
                throw new ArgumentException("The exact provisional client email is required for identity verification.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.ExpectedUuid))
                throw new ArgumentException("The exact provisional client UUID is required for identity verification.", nameof(request));
            if (request.ServerInfo == null)
                throw new ArgumentException("The panel connection for the order's own server placement is required.", nameof(request));
        }

        /// <summary>Reads the panel client and proves it is disabled.</summary>
        /// <param name="request">Authoritative panel connection and expected identity.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>A probe whose <c>Proven</c> flag is set when identity matched and the client reads back disabled.</returns>
        private async Task<StepProbe> ProveDisabledAsync(
            TenantCardProvisionalRevocationRequest request, CancellationToken cancellationToken)
        {
            var clientProbe = await ReadClientAsync(request, cancellationToken).ConfigureAwait(false);
            if (!clientProbe.Proven)
                return clientProbe;

            var identity = VerifyIdentity(request, clientProbe.Client);
            if (!identity.Proven)
                return identity;

            return clientProbe.Client.Enable
                ? StepProbe.Transport("provisional_revoke_not_reflected")
                : StepProbe.Success(Evidence("identity", "matched", "enable", "false"));
        }

        /// <summary>Reads one panel client and converts transport failures into a retryable probe.</summary>
        /// <param name="request">Authoritative panel connection.</param>
        /// <param name="cancellationToken">Cancellation token for panel I/O.</param>
        /// <returns>A probe carrying the client record when the read succeeded.</returns>
        private async Task<StepProbe> ReadClientAsync(
            TenantCardProvisionalRevocationRequest request, CancellationToken cancellationToken)
        {
            var response = await ApiServicev3.GetClientAsync(
                request.ServerInfo, _configuration, request.ExpectedEmail, cancellationToken).ConfigureAwait(false);

            return IsSuccess(response) && response.Obj != null
                ? StepProbe.Success(null, response.Obj)
                : StepProbe.Transport("provisional_revoke_read_failed");
        }

        /// <summary>Verifies that a panel client really is the provisional client this order expects.</summary>
        /// <param name="request">Authoritative expected identity and placement.</param>
        /// <param name="client">Panel client read back from the panel.</param>
        /// <returns>A proven probe on success, or a fail-closed probe naming the mismatched field.</returns>
        /// <remarks>
        /// Email comparison is case-insensitive because 3x-ui resolves panel clients by <c>strings.EqualFold</c>; UUID,
        /// subId, and placement are compared exactly. Any mismatch is terminal: disabling a same-email client that
        /// belongs to another customer would cut off an unrelated subscription.
        /// </remarks>
        private static StepProbe VerifyIdentity(TenantCardProvisionalRevocationRequest request, XuiV3Client client)
        {
            if (client == null)
                return StepProbe.FailClosed("provisional_revoke_identity_missing");

            if (!string.Equals(client.Email, request.ExpectedEmail, StringComparison.OrdinalIgnoreCase))
                return StepProbe.FailClosed("provisional_revoke_identity_email_mismatch");

            if (!string.Equals(client.Uuid, request.ExpectedUuid, StringComparison.Ordinal))
                return StepProbe.FailClosed("provisional_revoke_identity_uuid_mismatch");

            if (!string.IsNullOrEmpty(request.ExpectedSubId)
                && !string.Equals(client.SubId, request.ExpectedSubId, StringComparison.Ordinal))
                return StepProbe.FailClosed("provisional_revoke_identity_subid_mismatch");

            if (request.ExpectedInboundIds.Count > 0)
            {
                var expected = request.ExpectedInboundIds.Distinct().OrderBy(value => value).ToArray();
                var actual = (client.InboundIds ?? new List<int>()).Distinct().OrderBy(value => value).ToArray();
                if (!expected.SequenceEqual(actual))
                    return StepProbe.FailClosed("provisional_revoke_identity_placement_mismatch");
            }

            return StepProbe.Success(Evidence("identity", "matched"));
        }

        /// <summary>Builds a compact sanitized evidence string for one proven step.</summary>
        /// <param name="pairs">Alternating key/value pairs of non-secret comparison values.</param>
        /// <returns>A JSON object string safe to persist on the saga row.</returns>
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

        /// <summary>Builds the immutable result value for one outcome.</summary>
        /// <param name="status">Closed-vocabulary outcome.</param>
        /// <param name="operationKey">Durable saga key.</param>
        /// <param name="step">Furthest durably proven step.</param>
        /// <param name="reasonCode">Safe reason code, or <c>null</c> on success.</param>
        /// <returns>The result returned to the caller.</returns>
        private static TenantCardProvisionalRevocationResult Result(
            TenantCardProvisionalRevocationStatus status, string operationKey, string step, string reasonCode)
            => new(status, operationKey, step, reasonCode);

        /// <summary>Escalates a row to terminal manual review and returns the manual-review outcome.</summary>
        /// <param name="operationKey">Durable saga key.</param>
        /// <param name="reasonCode">Stable safe reason code; never raw exception text.</param>
        /// <param name="cancellationToken">Cancellation token for the short update transaction.</param>
        /// <returns>The manual-review result, carrying the terminal step.</returns>
        private async Task<TenantCardProvisionalRevocationResult> ManualReviewAsync(
            string operationKey, string reasonCode, CancellationToken cancellationToken)
        {
            await _store.MarkManualReviewAsync(operationKey, reasonCode, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Provisional revoke requires manual review. operationKey={OperationKey} reason={ReasonCode}",
                operationKey, reasonCode);
            return Result(TenantCardProvisionalRevocationStatus.ManualReview, operationKey,
                TenantCardProvisionalSteps.ManualReview, reasonCode);
        }

        /// <summary>Converts a failed step probe into either a retryable or a terminal manual-review outcome.</summary>
        /// <param name="operationKey">Durable saga key.</param>
        /// <param name="probe">Failed step probe carrying its stable reason code.</param>
        /// <param name="cancellationToken">Cancellation token for the short update transaction.</param>
        /// <returns>The outcome the caller must honor; the receipt stays rejected either way.</returns>
        private Task<TenantCardProvisionalRevocationResult> FailAsync(
            string operationKey, StepProbe probe, CancellationToken cancellationToken)
            => probe.RequiresReview
                ? ManualReviewAsync(operationKey, probe.ReasonCode, cancellationToken)
                : Task.FromResult(Result(TenantCardProvisionalRevocationStatus.Retryable, operationKey,
                    TenantCardProvisionalSteps.Claimed, probe.ReasonCode));

        /// <summary>Outcome of one revocation step probe.</summary>
        /// <remarks>Carries no policy: only whether the effect is proven, whether a human must intervene, and a reason code.</remarks>
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

            /// <summary>Creates a proven probe.</summary>
            /// <param name="evidence">Sanitized evidence for the proven step.</param>
            /// <param name="client">Panel client read during the probe, when applicable.</param>
            /// <returns>A proven probe.</returns>
            public static StepProbe Success(string evidence, XuiV3Client client = null)
                => new() { Proven = true, Evidence = evidence, Client = client };

            /// <summary>Creates a retryable probe for a transient panel or transport failure.</summary>
            /// <param name="reasonCode">Stable safe reason code.</param>
            /// <returns>A probe that keeps the saga resumable.</returns>
            public static StepProbe Transport(string reasonCode)
                => new() { Proven = false, RequiresReview = false, ReasonCode = reasonCode };

            /// <summary>Creates a terminal probe requiring human reconciliation.</summary>
            /// <param name="reasonCode">Stable safe reason code.</param>
            /// <returns>A probe that moves the row to manual review.</returns>
            public static StepProbe FailClosed(string reasonCode)
                => new() { Proven = false, RequiresReview = true, ReasonCode = reasonCode };
        }
    }
}
