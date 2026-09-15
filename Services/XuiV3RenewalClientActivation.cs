using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

/// <summary>
/// Closed vocabulary describing how the shared post-renewal activation step ended.
/// </summary>
/// <remarks>
/// The values separate the three questions a caller must be able to answer after a renewal: was the client already
/// active, did this step mutate the panel, and is the final panel state proven. Callers use them for audit fields and
/// operator warnings only; they never change financial or Telegram outcomes.
/// </remarks>
public enum XuiV3RenewalActivationStatus
{
    /// <summary>
    /// The renewal mutation was not accepted by the panel, so the activation step was skipped and nothing was mutated.
    /// This is the guard that stops a failed renewal from switching a customer's disabled account back on.
    /// </summary>
    SkippedRenewalNotApplied,

    /// <summary>The panel already reported the client as enabled, so no enable mutation was issued.</summary>
    AlreadyEnabled,

    /// <summary>The client was disabled, was enabled through the ordinary client-update path, and the read-back proved it.</summary>
    Enabled,

    /// <summary>
    /// The panel client could not be read when the activation step needed it: either before deciding to enable it or
    /// while verifying the final state. The step fails closed and never mutates a client whose state it cannot read.
    /// </summary>
    ClientUnreadable,

    /// <summary>
    /// A panel call ended in an unexpected transport, protocol, or timeout failure, so the client's enable state is
    /// unknown. Nothing further was attempted and the caller keeps its normal renewal and settlement behavior.
    /// </summary>
    ActivationUnverified,

    /// <summary>The panel rejected the enable update, so the client is known to still be disabled.</summary>
    EnableRejected,

    /// <summary>
    /// The panel accepted the enable update but the read-back still reports the client as disabled; the final state is
    /// not restored and an operator must look at the account.
    /// </summary>
    EnableNotReflected
}

/// <summary>
/// Inputs for the shared post-renewal activation step.
/// </summary>
/// <param name="ServerInfo">
/// Configured XUI v3 panel descriptor (URL, root path, API token) of the panel that accepted the renewal. It must be
/// the same descriptor used for the renewal mutation; <c>ApiVersion</c> is irrelevant to this step.
/// </param>
/// <param name="Configuration">
/// Runtime configuration supplying the panel transport, timeout, and retry policy. It never carries per-order state.
/// </param>
/// <param name="Email">
/// XUI client email of the renewed account. It is the panel's stable client key and is URL-escaped by the API layer.
/// An empty value is rejected without any panel request.
/// </param>
/// <param name="RenewalApplied">
/// <c>true</c> only when the renewal mutation was accepted by the panel (or was proven applied by a read-back after an
/// ambiguous timeout). When <c>false</c> the activation step performs no panel request at all, which is what keeps a
/// rejected renewal from enabling an account.
/// </param>
/// <param name="RenewalKind">
/// Short audit label of the renewal entry point, such as <c>user-renew</c>, <c>tenant-renew</c>, <c>admin-renew</c>, or
/// a recovery label. It is written to logs and must never contain secrets, callback text, or payload data.
/// </param>
/// <param name="ActorTelegramUserId">
/// Numeric Telegram id of the customer or administrator whose renewal triggered the step. Pass <c>0</c> when the actor
/// is unknown (for example a background recovery worker). It is logged for audit only and never replaces ownership.
/// </param>
/// <param name="Logger">
/// Logger of the calling service. Its category is preserved so operator channels keep attributing the activation
/// outcome to the flow that performed the renewal.
/// </param>
/// <param name="ExpectedOwnerTelegramUserId">
/// Optional numeric Telegram id that may be used when the panel client carries no owner identity of its own. It keeps
/// an enable write from erasing or inventing ownership; every renewal caller leaves it <c>null</c> so paying for a
/// renewal can never transfer an account to the payer.
/// </param>
public sealed record XuiV3RenewalActivationRequest(
    ServerInfo ServerInfo,
    IConfiguration Configuration,
    string Email,
    bool RenewalApplied,
    string RenewalKind,
    long ActorTelegramUserId,
    ILogger Logger,
    long? ExpectedOwnerTelegramUserId = null);

/// <summary>
/// Result of the shared post-renewal activation step.
/// </summary>
/// <param name="Status">Closed-vocabulary outcome; see <see cref="XuiV3RenewalActivationStatus" /> for each meaning.</param>
/// <param name="Email">Trimmed client email the step acted on; empty when the request carried no usable email.</param>
/// <param name="IsActive">
/// <c>true</c> only when the panel was proven to hold the client enabled at the end of the step (either because it was
/// already enabled or because the enable write was verified). It is <c>false</c> for every unproven or skipped outcome.
/// </param>
/// <param name="MutationIssued">
/// <c>true</c> when this step started an enable update against the panel, including the case where that update ended in
/// an unverified failure. It is <c>false</c> for the already-enabled, skipped, and unreadable-before-mutation outcomes,
/// so an audit trail can tell a no-op apart from a repair attempt.
/// </param>
public sealed record XuiV3RenewalActivationResult(
    XuiV3RenewalActivationStatus Status,
    string Email,
    bool IsActive,
    bool MutationIssued);

/// <summary>
/// Guarantees that a renewal accepted by 3x-ui leaves the panel client enabled, by checking the live client state after
/// the renewal mutation and repairing a still-disabled client through the panel's ordinary enable update.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this step exists.</b> 3x-ui disables a client when it expires or exhausts its quota, and <c>enable</c> is the
/// panel's only instantaneous admission gate: quota and expiry are enforced by the periodic traffic poll, not when a
/// connection is opened. A renewal that extends expiry and raises the quota therefore does not by itself guarantee that
/// the customer can connect again; if the panel keeps the client disabled, the customer pays, the panel records the new
/// quota, and the account stays off. This step reads the state the panel actually holds after the mutation and, only
/// when the renewal was accepted, clears a remaining disable flag and proves the end state by read-back.
/// </para>
/// <para>
/// <b>Ordering and safety of the surrounding flow.</b> Call it on the renewal path where the panel client has just been
/// written and before the financial tail. A traffic reset that runs afterwards can only ever enable or re-admit a
/// disabled client, so a proven-active client cannot regress behind this step.
/// </para>
/// <para>
/// <b>Idempotency.</b> The step short-circuits when the panel already reports the client enabled, so it issues no
/// mutation on the common path and a second call for the same renewal performs no work. When it does enable, it sends
/// an absolute <c>enable = true</c> write through <see cref="ApiServicev3.SetClientEnabledAsync" />, which re-reads the
/// client first and preserves its quota, expiry, UUID, password, subscription id, and ownership, so replaying the step
/// converges instead of compounding and never resets the renewed entitlement.
/// </para>
/// <para>
/// <b>Fail-closed rules.</b> A renewal that was not accepted never enables anything, an unreadable client is never
/// mutated, and an enable whose effect cannot be read back is reported as unproven instead of being assumed. The step
/// never disables a client, never touches traffic counters, never writes wallet, ledger, order, reminder, or
/// notification state, and never calls Telegram.
/// </para>
/// <para>
/// <b>Failure containment.</b> A panel transport, protocol, or timeout failure inside this step is reported as
/// <see cref="XuiV3RenewalActivationStatus.ActivationUnverified" /> and never thrown, because the step runs after the
/// renewal is already durable and the customer already paid: a panel hiccup must not turn an applied renewal into a
/// customer-visible failure, and an operator can see the unproven state in the warning log and audit fields.
/// Caller-initiated cancellation is still propagated.
/// </para>
/// <para>
/// <b>Cost.</b> The already-enabled path costs one panel read. The repair path costs the state read, the read-then-update
/// pair inside <see cref="ApiServicev3.SetClientEnabledAsync" />, and one verification read, so callers on an interactive
/// Telegram budget should place it after the renewal is already durable.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var activation = await XuiV3RenewalClientActivation.EnsureClientEnabledAfterRenewalAsync(
///     new XuiV3RenewalActivationRequest(
///         serverInfo,
///         _configuration,
///         client.Email,
///         RenewalApplied: updateResponse.Success,
///         RenewalKind: "user-renew",
///         ActorTelegramUserId: credUser.TelegramUserId,
///         Logger: _logger));
///
/// if (!activation.IsActive)
/// {
///     // The renewal applied but the account is not proven active; keep the audit trail and warn the operator.
///     _logger.LogWarning("Renewal left the client inactive. status={Status}", activation.Status);
/// }
/// </code>
/// </example>
public static class XuiV3RenewalClientActivation
{
    /// <summary>
    /// Checks the live panel client after a renewal and enables it when the panel still holds it disabled.
    /// </summary>
    /// <param name="request">Renewal, panel, and logging inputs described by <see cref="XuiV3RenewalActivationRequest" />.</param>
    /// <param name="cancellationToken">
    /// Token that cancels the panel reads and the enable update. An already-issued update is never re-sent.
    /// </param>
    /// <returns>
    /// A <see cref="XuiV3RenewalActivationResult" /> describing the closed-vocabulary outcome, whether an enable update
    /// was started, and whether the panel was proven to hold the client enabled. A <c>false</c>
    /// <see cref="XuiV3RenewalActivationResult.IsActive" /> means the final state is unproven or still disabled, never
    /// that the renewal itself failed.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request" /> is <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is cancelled.</exception>
    /// <remarks>
    /// This method is the single shared implementation used by owned-bot customer renewals, tenant storefront renewals,
    /// super-admin renewals, and their crash-recovery settlement paths, so the "renewal implies an active account"
    /// property cannot drift between entry points. It performs no financial, Telegram, reminder, or order writes and
    /// leaves the caller responsible for its own success message and settlement.
    /// </remarks>
    public static async Task<XuiV3RenewalActivationResult> EnsureClientEnabledAfterRenewalAsync(
        XuiV3RenewalActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var email = (request.Email ?? string.Empty).Trim();
        var kind = string.IsNullOrWhiteSpace(request.RenewalKind) ? "renew" : request.RenewalKind.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            request.Logger?.LogWarning(
                "XUI v3 renewal client activation was skipped because the target email was empty. renewalKind={RenewalKind}, actorTelegramUserId={ActorTelegramUserId}",
                kind,
                request.ActorTelegramUserId);
            return new XuiV3RenewalActivationResult(
                XuiV3RenewalActivationStatus.ClientUnreadable,
                email,
                IsActive: false,
                MutationIssued: false);
        }

        // Guard for the rule "never enable a client whose renewal failed": a renewal the panel rejected, or one that is
        // still ambiguous, must leave the account exactly as the panel has it. No request is sent here at all.
        if (!request.RenewalApplied)
        {
            request.Logger?.LogDebug(
                "XUI v3 renewal client activation skipped because the renewal was not accepted by the panel. renewalKind={RenewalKind}, accountEmail={AccountEmail}",
                kind,
                email);
            return new XuiV3RenewalActivationResult(
                XuiV3RenewalActivationStatus.SkippedRenewalNotApplied,
                email,
                IsActive: false,
                MutationIssued: false);
        }

        XuiV3Client currentClient;
        try
        {
            var currentResponse = await ApiServicev3.GetClientAsync(
                request.ServerInfo,
                request.Configuration,
                email,
                cancellationToken);

            if (!currentResponse.Success || currentResponse.Obj == null)
            {
                // An unreadable client cannot be classified as active or disabled, so nothing is mutated. The renewal
                // itself already applied; this only means the enable state was not proven.
                request.Logger?.LogWarning(
                    "XUI v3 renewal could not read the client to verify its enable state; no enable update was sent. renewalKind={RenewalKind}, accountEmail={AccountEmail}",
                    kind,
                    email);
                return new XuiV3RenewalActivationResult(
                    XuiV3RenewalActivationStatus.ClientUnreadable,
                    email,
                    IsActive: false,
                    MutationIssued: false);
            }

            currentClient = currentResponse.Obj;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UnverifiedAfterPanelFailure(ex, email, kind, mutationIssued: false, request.Logger);
        }

        if (currentClient.Enable)
        {
            request.Logger?.LogDebug(
                "XUI v3 renewal client is already enabled; no enable mutation was issued. renewalKind={RenewalKind}, accountEmail={AccountEmail}",
                kind,
                email);
            return new XuiV3RenewalActivationResult(
                XuiV3RenewalActivationStatus.AlreadyEnabled,
                email,
                IsActive: true,
                MutationIssued: false);
        }

        // The panel disabled this client when it expired or exhausted its quota, and the renewal did not clear the flag.
        // The enable update preserves whatever quota and expiry the renewal just wrote, because the shared enable
        // primitive re-reads the client and replaces only the enable field.
        request.Logger?.LogInformation(
            "Client was disabled after previous expiration. Re-enabling client. renewalKind={RenewalKind}, accountEmail={AccountEmail}, actorTelegramUserId={ActorTelegramUserId}",
            kind,
            email,
            request.ActorTelegramUserId);

        XuiV3ApiResponse<JToken> enableResponse;
        try
        {
            enableResponse = await ApiServicev3.SetClientEnabledAsync(
                request.ServerInfo,
                request.Configuration,
                email,
                enable: true,
                request.ExpectedOwnerTelegramUserId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The update may or may not have reached the panel, so the state is unproven and the write is never replayed
            // here; the panel already holds an absolute "enabled" intent that any later read can confirm.
            return UnverifiedAfterPanelFailure(ex, email, kind, mutationIssued: true, request.Logger);
        }

        if (!enableResponse.Success)
        {
            request.Logger?.LogWarning(
                "XUI v3 renewal enable update was rejected by the panel; the client remains disabled. renewalKind={RenewalKind}, accountEmail={AccountEmail}, panelMessage={PanelMessage}",
                kind,
                email,
                ShortenPanelMessage(enableResponse.Msg));
            return new XuiV3RenewalActivationResult(
                XuiV3RenewalActivationStatus.EnableRejected,
                email,
                IsActive: false,
                MutationIssued: true);
        }

        try
        {
            var readBackResponse = await ApiServicev3.GetClientAsync(
                request.ServerInfo,
                request.Configuration,
                email,
                cancellationToken);

            if (!readBackResponse.Success || readBackResponse.Obj == null)
            {
                request.Logger?.LogWarning(
                    "XUI v3 renewal enable update was accepted but the panel state could not be re-read, so the account is not proven active. renewalKind={RenewalKind}, accountEmail={AccountEmail}",
                    kind,
                    email);
                return new XuiV3RenewalActivationResult(
                    XuiV3RenewalActivationStatus.ClientUnreadable,
                    email,
                    IsActive: false,
                    MutationIssued: true);
            }

            if (!readBackResponse.Obj.Enable)
            {
                request.Logger?.LogWarning(
                    "XUI v3 renewal enable update was accepted but the read-back still reports the client as disabled. renewalKind={RenewalKind}, accountEmail={AccountEmail}",
                    kind,
                    email);
                return new XuiV3RenewalActivationResult(
                    XuiV3RenewalActivationStatus.EnableNotReflected,
                    email,
                    IsActive: false,
                    MutationIssued: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UnverifiedAfterPanelFailure(ex, email, kind, mutationIssued: true, request.Logger);
        }

        request.Logger?.LogInformation(
            "Client enable status restored successfully. renewalKind={RenewalKind}, accountEmail={AccountEmail}",
            kind,
            email);
        return new XuiV3RenewalActivationResult(
            XuiV3RenewalActivationStatus.Enabled,
            email,
            IsActive: true,
            MutationIssued: true);
    }

    /// <summary>
    /// Converts an unexpected panel failure inside the activation step into an unproven, non-throwing result.
    /// </summary>
    /// <param name="exception">The transport, protocol, or timeout failure raised by the panel call.</param>
    /// <param name="email">Trimmed client email the step was working on; safe for logs.</param>
    /// <param name="kind">Short renewal audit label of the calling entry point.</param>
    /// <param name="mutationIssued">
    /// Whether an enable update had already been started when the failure was raised. It states intent, not success, so
    /// the caller never treats an unverified failure as a repaired account.
    /// </param>
    /// <param name="logger">Caller logger; a null value skips only the log line.</param>
    /// <returns>
    /// A result with <see cref="XuiV3RenewalActivationStatus.ActivationUnverified" /> and
    /// <see cref="XuiV3RenewalActivationResult.IsActive" /> always <c>false</c>, because nothing was proven.
    /// </returns>
    /// <remarks>
    /// Used only after the renewal is durable. The exception is logged with its type so a panel outage stays diagnosable
    /// without leaking panel bodies, and the method never inspects or re-sends the failed request.
    /// </remarks>
    private static XuiV3RenewalActivationResult UnverifiedAfterPanelFailure(
        Exception exception,
        string email,
        string kind,
        bool mutationIssued,
        ILogger logger)
    {
        logger?.LogWarning(
            exception,
            "XUI v3 renewal client activation could not verify the enable state because a panel call failed; the renewal itself is unaffected. renewalKind={RenewalKind}, accountEmail={AccountEmail}, enableUpdateStarted={EnableUpdateStarted}",
            kind,
            email,
            mutationIssued);

        return new XuiV3RenewalActivationResult(
            XuiV3RenewalActivationStatus.ActivationUnverified,
            email,
            IsActive: false,
            MutationIssued: mutationIssued);
    }

    /// <summary>
    /// Bounds a panel message before it reaches a log entry.
    /// </summary>
    /// <param name="panelMessage">Raw panel message; it may be null, empty, or unexpectedly long.</param>
    /// <returns>
    /// The trimmed message truncated to a fixed length, or an empty string when the panel supplied none. The value is
    /// for diagnostics only and must never be shown to a customer.
    /// </returns>
    /// <remarks>
    /// Panel messages can echo request bodies on validation failures, so the length bound keeps an enable-rejection log
    /// line readable without copying an unbounded payload into journald.
    /// </remarks>
    private static string ShortenPanelMessage(string panelMessage)
    {
        var value = (panelMessage ?? string.Empty).Trim();
        return value.Length <= 200 ? value : value[..200];
    }
}
