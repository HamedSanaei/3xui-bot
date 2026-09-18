using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Safe administrator workflow for XUI v3 renewal operations that automatic reconciliation parked in manual review.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Bounded automatic reconciliation escalates an inconclusive renewal to <c>manual_review</c>
/// and keeps its account lock, which is correct: the panel outcome is unknown, so neither another renewal nor another
/// wallet debit may happen. The problem was that nothing could ever release that lock. The operation stayed locked
/// forever, the customer kept seeing a temporary "under review" message, and the only possible remedy was editing the
/// database by hand.
/// </para>
/// <para>
/// <b>What this service will never do.</b> It never sends <c>POST /UpdateClient</c>, never replays a stored payload,
/// never unlocks an operation merely because time passed, never deletes an operation row, and never implements a second
/// debit or ledger path. Every resolution either proves the panel already holds the target and then continues through the
/// pre-existing settlement implementation, or proves nothing was charged and then releases the lock.
/// </para>
/// <para>
/// <b>Concurrency.</b> Every durable transition is a conditional SQL UPDATE performed on an independent
/// <c>UserDbContext</c>, and the operation is re-read before each decision. Two simultaneous resolutions therefore
/// cannot both continue into settlement, and a repeated confirmation is idempotent.
/// </para>
/// </remarks>
public sealed class XuiV3RenewalManualReviewService
{
    private readonly XuiV3RenewalOperationStore _operationStore;
    private readonly XuiV3RenewalAppliedSettlementRouter _settlementRouter;
    private readonly CredentialsStore _credentialsStore;
    private readonly WalletLedgerService _walletLedgerService;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly ILogger<XuiV3RenewalManualReviewService> _logger;

    /// <summary>
    /// Creates the manual-review resolution service.
    /// </summary>
    /// <param name="operationStore">Durable users.db store owning every conditional renewal transition.</param>
    /// <param name="settlementRouter">
    /// Router to the existing owned/tenant exactly-once settlement implementation. It is the only financial entry point
    /// this service is allowed to use.
    /// </param>
    /// <param name="credentialsStore">
    /// credentials.db reader used to detect a durable bot-wallet receipt for the operation's settlement key before any
    /// abandonment is allowed.
    /// </param>
    /// <param name="walletLedgerService">users.db ledger reader used for the same pre-abandonment proof.</param>
    /// <param name="configuration">Runtime configuration supplying the XUI v3 base URL, root path, token, and timeouts.</param>
    /// <param name="logger">
    /// Local operational logger. It records operation ids and fixed state categories only; account identity, payloads,
    /// tokens, and panel URLs are never written.
    /// </param>
    public XuiV3RenewalManualReviewService(
        XuiV3RenewalOperationStore operationStore,
        XuiV3RenewalAppliedSettlementRouter settlementRouter,
        CredentialsStore credentialsStore,
        WalletLedgerService walletLedgerService,
        IConfiguration configuration,
        ILogger<XuiV3RenewalManualReviewService> logger)
    {
        _operationStore = operationStore;
        _settlementRouter = settlementRouter;
        _credentialsStore = credentialsStore;
        _walletLedgerService = walletLedgerService;
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
    }

    /// <summary>
    /// Result category of one administrator action on a manual-review operation.
    /// </summary>
    public enum ManualReviewOutcome
    {
        /// <summary>The read-only review was loaded successfully.</summary>
        ReviewLoaded,

        /// <summary>A read-only re-check ran and its sanitized evidence was persisted.</summary>
        ReprobeCompared,

        /// <summary>The panel was proven to hold the target and settlement completed.</summary>
        ConfirmedApplied,

        /// <summary>
        /// The panel was proven to hold the target and the operation moved to applied, but settlement is still
        /// incomplete or parked for financial review.
        /// </summary>
        ConfirmedAppliedSettlementPending,

        /// <summary>The operation was abandoned as not applied and its account lock was released.</summary>
        Abandoned,

        /// <summary>No operation exists for the supplied local key.</summary>
        NotFound,

        /// <summary>The operation is not currently in manual review, so no manual action applies.</summary>
        NotUnderManualReview,

        /// <summary>A fresh read-only comparison did not prove the renewal was applied.</summary>
        ComparisonNotApplied,

        /// <summary>A fresh read-only comparison did not prove the panel still holds the pre-mutation state.</summary>
        ComparisonNotPreMutation,

        /// <summary>A durable financial artifact exists for this operation, so it must never be abandoned.</summary>
        SettlementArtifactExists,

        /// <summary>The operation predates the recovery protocol and needs an explicit super-admin override.</summary>
        LegacyOverrideRequired,

        /// <summary>Settlement is no longer pending, so the account lock must not be released.</summary>
        SettlementNotPending,

        /// <summary>Another administrator or executor resolved the operation first; the durable state is authoritative.</summary>
        AlreadyResolved
    }

    /// <summary>
    /// Sanitized, non-secret view of one manual-review operation for administrator display.
    /// </summary>
    /// <remarks>
    /// Every field is either an internal numeric identifier, a fixed state category, a timestamp in UTC, or a bounded
    /// comparison summary produced by the reconciliation engine. The type deliberately has no property for the account
    /// email, UUID, panel URL, API token, mutation payload, or raw error text, so an administrator screen cannot leak
    /// them even by accident.
    /// </remarks>
    public sealed class ManualReviewReview
    {
        /// <summary>Internal users.db primary key, also the compact callback token.</summary>
        public int OperationId { get; init; }

        /// <summary>Runtime bot id that owns the renewal flow.</summary>
        public string BotId { get; init; }

        /// <summary>Telegram user id of the payer or actor; used to correlate with support reports.</summary>
        public long TelegramUserId { get; init; }

        /// <summary>UTC creation time of the operation row.</summary>
        public DateTime CreatedAtUtc { get; init; }

        /// <summary>UTC time when automatic reconciliation escalated the operation to manual review.</summary>
        public DateTime? ManualReviewAtUtc { get; init; }

        /// <summary>Current mutation lifecycle status.</summary>
        public string Status { get; init; }

        /// <summary>Current settlement lifecycle status.</summary>
        public string SettlementStatus { get; init; }

        /// <summary>
        /// Whether the operation was created under the recovery protocol. False means no pre-mutation evidence exists and
        /// the panel outcome can never be re-proven automatically.
        /// </summary>
        public bool RecoveryEligible { get; init; }

        /// <summary>Number of completed GET-only reconciliation attempts.</summary>
        public int ReconcileAttemptCount { get; init; }

        /// <summary>Fixed comparison outcome category such as Applied, Drifted, or Unavailable; never a payload.</summary>
        public string LastComparisonOutcome { get; init; }

        /// <summary>Per-field relation labels without any account value, panel value, or response body.</summary>
        public string LastMismatchSummary { get; init; }

        /// <summary>Renewal price in Iranian toman, needed to judge the financial exposure of the decision.</summary>
        public long PriceToman { get; init; }

        /// <summary>
        /// Whether the operation still holds an account lock. A resolved operation no longer blocks a new renewal.
        /// </summary>
        public bool HoldsAccountLock { get; init; }
    }

    /// <summary>
    /// Structured result of one administrator manual-review action.
    /// </summary>
    /// <remarks>
    /// The result is safe to render directly in the administrator chat: it carries fixed categories, internal numeric
    /// identifiers, and sanitized comparison labels only.
    /// </remarks>
    public sealed class ManualReviewActionResult
    {
        /// <summary>Action category describing exactly what happened.</summary>
        public ManualReviewOutcome Outcome { get; init; }

        /// <summary>Internal users.db key of the affected operation, or zero when it was not found.</summary>
        public int OperationId { get; init; }

        /// <summary>Durable mutation status after the action.</summary>
        public string Status { get; init; }

        /// <summary>Durable settlement status after the action.</summary>
        public string SettlementStatus { get; init; }

        /// <summary>Sanitized comparison outcome observed during this action, or null when none was produced.</summary>
        public string ComparisonOutcome { get; init; }

        /// <summary>Sanitized per-field comparison summary, or null when none was produced.</summary>
        public string ComparisonSummary { get; init; }

        /// <summary>Sanitized review view after the action.</summary>
        public ManualReviewReview Review { get; init; }
    }

    /// <summary>
    /// Loads one operation for administrator inspection without changing any state.
    /// </summary>
    /// <param name="operationId">
    /// Internal users.db primary key as shown in the administrator list. It is not a Telegram update id, an order id, or
    /// an account identity.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the users.db read.</param>
    /// <returns>
    /// The sanitized review view, or null when no operation has that key.
    /// </returns>
    /// <remarks>
    /// This method performs no panel call, no financial read, and no write. It exists so the administrator screen can be
    /// rendered from durable state alone.
    /// </remarks>
    /// <example><code>var review = await service.GetReviewAsync(42, cancellationToken);</code></example>
    public async Task<ManualReviewReview> GetReviewAsync(
        int operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        return operation == null ? null : Map(operation);
    }

    /// <summary>
    /// Lists the operations currently waiting for an administrator decision.
    /// </summary>
    /// <param name="maximumCount">Maximum rows to return; clamped by the store to 1 through 50.</param>
    /// <param name="cancellationToken">Token that cancels the users.db read.</param>
    /// <returns>
    /// Sanitized review views ordered by escalation time, oldest first. The collection is empty when nothing is pending,
    /// which is the normal steady state.
    /// </returns>
    /// <remarks>
    /// Recovery-ineligible historical rows are included, because they hold a real account lock and can only be released
    /// by an explicit administrator override.
    /// </remarks>
    /// <example><code>var pending = await service.ListPendingAsync(10, cancellationToken);</code></example>
    public async Task<IReadOnlyList<ManualReviewReview>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        var operations = await _operationStore.ListPendingManualReviewsAsync(maximumCount, cancellationToken);
        return operations.Select(Map).ToList();
    }

    /// <summary>
    /// Performs a read-only panel re-check of one manual-review operation and persists its sanitized evidence.
    /// </summary>
    /// <param name="operationId">Internal users.db primary key of the operation to re-check.</param>
    /// <param name="cancellationToken">Token that cancels the panel reads and the evidence write.</param>
    /// <returns>
    /// A result carrying the fresh comparison category. The operation status, settlement status, and account lock are
    /// never modified by a re-check, so this action can never move money or unlock an account.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The re-check reuses <see cref="XuiV3RenewalOperationStore.RecoverByReadBackAsync" /> and therefore
    /// <see cref="XuiV3RenewalOperationStore.CompareRenewalState" />, which is the same read-only comparison the
    /// background worker uses. A recovery-ineligible historical operation has no pre-mutation evidence and is therefore
    /// always reported as Unavailable; that is the honest answer, not an error.
    /// </para>
    /// <para>
    /// Side effects: one conditional UPDATE that records the comparison outcome, the relation summary, and the
    /// observation time. No status, settlement, lock, or financial column is touched.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await service.ReprobeAsync(operationId, cancellationToken);
    /// if (result.Outcome == ManualReviewOutcome.ReprobeCompared) ShowEvidence(result);
    /// </code>
    /// </example>
    public async Task<ManualReviewActionResult> ReprobeAsync(
        int operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        if (operation == null)
            return Result(ManualReviewOutcome.NotFound, operationId);
        if (!IsManualReview(operation))
            return Result(ManualReviewOutcome.NotUnderManualReview, operationId, operation);

        var comparison = await RunReadOnlyComparisonAsync(operation, cancellationToken);
        await _operationStore.RecordManualReviewReprobeAsync(operation, comparison, cancellationToken);

        _logger.LogInformation(
            "XUI v3 manual-review re-check completed. renewalOperationId={RenewalOperationId}, outcome={Outcome}, mismatchSummary={MismatchSummary}",
            operation.OperationId,
            comparison.Outcome,
            comparison.Summary);

        var refreshed = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        return new ManualReviewActionResult
        {
            Outcome = ManualReviewOutcome.ReprobeCompared,
            OperationId = operationId,
            Status = refreshed?.Status ?? operation.Status,
            SettlementStatus = refreshed?.SettlementStatus ?? operation.SettlementStatus,
            ComparisonOutcome = comparison.Outcome.ToString(),
            ComparisonSummary = comparison.Summary,
            Review = refreshed == null ? null : Map(refreshed)
        };
    }

    /// <summary>
    /// Confirms that the panel already holds the renewal target and continues the existing exactly-once settlement.
    /// </summary>
    /// <param name="operationId">Internal users.db primary key of the manual-review operation.</param>
    /// <param name="adminTelegramUserId">
    /// Numeric Telegram id of the configured super-admin performing the confirmation. It is persisted for audit only and
    /// must already have been authorized by the caller against the configured super-admin allow-list.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the panel read, the conditional transition, and settlement.</param>
    /// <returns>
    /// <see cref="ManualReviewOutcome.ConfirmedApplied" /> when the transition succeeded and settlement completed;
    /// <see cref="ManualReviewOutcome.ConfirmedAppliedSettlementPending" /> when the transition succeeded but settlement
    /// still needs to finish; <see cref="ManualReviewOutcome.ComparisonNotApplied" /> when a fresh read-only comparison
    /// could not prove the target; otherwise the reason no transition was attempted.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A fresh GET comparison is mandatory on every call. There is no cached or assumed result, so a stale
    /// administrator message can never apply an operation the panel does not actually hold.
    /// </para>
    /// <para>
    /// The mutation is never replayed. Confirming an operation proves the earlier <c>POST /UpdateClient</c> already
    /// took effect, and settlement then runs through the pre-existing tenant or owned path, which keeps the same
    /// settlement claim and wallet-ledger idempotency key. Repeated confirmations therefore cannot debit twice: the
    /// second call fails the conditional transition and reports the already-resolved state.
    /// </para>
    /// <para>
    /// A settlement that was already parked for financial review stays parked. Moving the mutation to applied never
    /// resumes a crashed settlement claim, because the previous executor may already have debited the wallet.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await service.ConfirmAppliedAsync(operationId, callbackQuery.From.Id, cancellationToken);
    /// </code>
    /// </example>
    public async Task<ManualReviewActionResult> ConfirmAppliedAsync(
        int operationId,
        long adminTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var operation = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        if (operation == null)
            return Result(ManualReviewOutcome.NotFound, operationId);
        if (!IsManualReview(operation))
            return Result(ManualReviewOutcome.NotUnderManualReview, operationId, operation);

        var comparison = await RunReadOnlyComparisonAsync(operation, cancellationToken);
        if (comparison.Outcome != XuiV3RenewalOperationStore.RecoveryOutcome.Applied)
        {
            await _operationStore.RecordManualReviewReprobeAsync(operation, comparison, cancellationToken);
            return new ManualReviewActionResult
            {
                Outcome = ManualReviewOutcome.ComparisonNotApplied,
                OperationId = operationId,
                Status = operation.Status,
                SettlementStatus = operation.SettlementStatus,
                ComparisonOutcome = comparison.Outcome.ToString(),
                ComparisonSummary = comparison.Summary,
                Review = Map(operation)
            };
        }

        if (!await _operationStore.ResolveManualReviewAsAppliedAsync(
                operation, comparison, adminTelegramUserId, cancellationToken))
        {
            // Another administrator or the worker already resolved this row. Report the durable state instead of
            // assuming this call applied anything, so no caller can continue into settlement on a lost race.
            var raced = await _operationStore.GetByIdAsync(operationId, cancellationToken);
            return new ManualReviewActionResult
            {
                Outcome = ManualReviewOutcome.AlreadyResolved,
                OperationId = operationId,
                Status = raced?.Status,
                SettlementStatus = raced?.SettlementStatus,
                ComparisonOutcome = comparison.Outcome.ToString(),
                ComparisonSummary = comparison.Summary,
                Review = raced == null ? null : Map(raced)
            };
        }

        _logger.LogWarning(
            "XUI v3 manual-review operation confirmed as applied by an administrator. renewalOperationId={RenewalOperationId}, resumedSettlement={ResumedSettlement}",
            operation.OperationId,
            operation.SettlementStatus == XuiV3RenewalSettlementStatuses.Pending);

        // The operation is applied now; the shared router reaches the single owned/tenant settlement implementation.
        var settled = await _settlementRouter.SettleAppliedAsync(operation, cancellationToken);
        var finalState = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        return new ManualReviewActionResult
        {
            Outcome = settled
                ? ManualReviewOutcome.ConfirmedApplied
                : ManualReviewOutcome.ConfirmedAppliedSettlementPending,
            OperationId = operationId,
            Status = finalState?.Status,
            SettlementStatus = finalState?.SettlementStatus,
            ComparisonOutcome = comparison.Outcome.ToString(),
            ComparisonSummary = comparison.Summary,
            Review = finalState == null ? null : Map(finalState)
        };
    }

    /// <summary>
    /// Abandons a manual-review operation as definitively not applied and releases its account lock.
    /// </summary>
    /// <param name="operationId">Internal users.db primary key of the manual-review operation.</param>
    /// <param name="adminTelegramUserId">
    /// Numeric Telegram id of the configured super-admin performing the abandonment. It is persisted for audit and must
    /// already have been authorized by the caller.
    /// </param>
    /// <param name="legacyOverrideConfirmed">
    /// Explicit super-admin override for a recovery-ineligible historical operation, whose panel state can never be
    /// re-proven by a read-only comparison. It must be <c>true</c> only after the administrator has confirmed a second,
    /// separate prompt; it has no effect on a recovery-eligible operation.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the panel read, the financial receipt checks, and the transition.</param>
    /// <returns>
    /// <see cref="ManualReviewOutcome.Abandoned" /> when the lock was released; otherwise the specific reason the
    /// abandonment was refused, with the operation left exactly as it was.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Preconditions, all mandatory.</b> The operation must still be <c>manual_review</c> with <c>pending</c>
    /// settlement, no wallet ledger row and no durable bot-wallet or website receipt may exist under the operation's
    /// settlement key, and the mutation must be proven not applied. For a recovery-eligible operation that proof is a
    /// fresh <see cref="XuiV3RenewalOperationStore.RecoveryOutcome.DefinitelyPreMutation" /> comparison. For a
    /// recovery-ineligible historical operation no such proof is possible, so an explicit
    /// <paramref name="legacyOverrideConfirmed" /> override is required instead and is recorded as such.
    /// </para>
    /// <para>
    /// <b>Why the financial check comes first.</b> Releasing the account lock lets the customer start a fresh renewal.
    /// If money had already moved for the abandoned operation, that fresh renewal would charge a second time for one
    /// panel account, so a detected artifact refuses the abandonment instead of unlocking.
    /// </para>
    /// <para>
    /// <b>What is written.</b> One conditional UPDATE sets <c>failed</c>, clears the account lock, clears the recovery
    /// claim and its schedule, and records the administrator id and the resolution category. The row is never deleted,
    /// so the audit trail and the settlement key remain inspectable.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await service.AbandonAsNotAppliedAsync(
    ///     operationId, adminId, legacyOverrideConfirmed: false, cancellationToken);
    /// </code>
    /// </example>
    public async Task<ManualReviewActionResult> AbandonAsNotAppliedAsync(
        int operationId,
        long adminTelegramUserId,
        bool legacyOverrideConfirmed,
        CancellationToken cancellationToken = default)
    {
        var operation = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        if (operation == null)
            return Result(ManualReviewOutcome.NotFound, operationId);
        if (!IsManualReview(operation))
            return Result(ManualReviewOutcome.NotUnderManualReview, operationId, operation);

        // A settlement that left pending means a wallet debit may already exist for this operation. The lock is the only
        // thing preventing a second charge, so this path refuses and leaves the row for financial reconciliation.
        if (!string.Equals(
                operation.SettlementStatus,
                XuiV3RenewalSettlementStatuses.Pending,
                StringComparison.Ordinal))
        {
            return Result(ManualReviewOutcome.SettlementNotPending, operationId, operation);
        }

        // Durable financial artifacts are checked before any panel work so a slow or unavailable panel cannot turn into
        // an unlocked, already-charged operation.
        if (await HasFinancialArtifactAsync(operation, cancellationToken))
        {
            _logger.LogError(
                "XUI v3 manual-review abandonment refused because a durable financial artifact exists. renewalOperationId={RenewalOperationId}",
                operation.OperationId);
            return Result(ManualReviewOutcome.SettlementArtifactExists, operationId, operation);
        }

        var resolution = XuiV3RenewalManualReviewResolutions.AbandonedNotApplied;
        string reason;
        if (operation.RecoveryEligible)
        {
            var comparison = await RunReadOnlyComparisonAsync(operation, cancellationToken);
            if (comparison.Outcome != XuiV3RenewalOperationStore.RecoveryOutcome.DefinitelyPreMutation)
            {
                await _operationStore.RecordManualReviewReprobeAsync(operation, comparison, cancellationToken);
                return new ManualReviewActionResult
                {
                    Outcome = ManualReviewOutcome.ComparisonNotPreMutation,
                    OperationId = operationId,
                    Status = operation.Status,
                    SettlementStatus = operation.SettlementStatus,
                    ComparisonOutcome = comparison.Outcome.ToString(),
                    ComparisonSummary = comparison.Summary,
                    Review = Map(operation)
                };
            }

            reason = "Administrator abandoned this renewal as not applied after a fresh pre-mutation comparison.";
        }
        else
        {
            if (!legacyOverrideConfirmed)
                return Result(ManualReviewOutcome.LegacyOverrideRequired, operationId, operation);

            resolution = XuiV3RenewalManualReviewResolutions.AbandonedNotAppliedLegacyOverride;
            reason = "Super-admin override abandoned this historical renewal whose panel state can never be re-proven.";
        }

        if (!await _operationStore.AbandonManualReviewAsNotAppliedAsync(
                operation, adminTelegramUserId, resolution, reason, cancellationToken))
        {
            var raced = await _operationStore.GetByIdAsync(operationId, cancellationToken);
            return new ManualReviewActionResult
            {
                Outcome = ManualReviewOutcome.AlreadyResolved,
                OperationId = operationId,
                Status = raced?.Status,
                SettlementStatus = raced?.SettlementStatus,
                Review = raced == null ? null : Map(raced)
            };
        }

        _logger.LogWarning(
            "XUI v3 manual-review operation abandoned as not applied and its account lock was released. renewalOperationId={RenewalOperationId}, resolution={Resolution}",
            operation.OperationId,
            resolution);

        var finalState = await _operationStore.GetByIdAsync(operationId, cancellationToken);
        return new ManualReviewActionResult
        {
            Outcome = ManualReviewOutcome.Abandoned,
            OperationId = operationId,
            Status = finalState?.Status,
            SettlementStatus = finalState?.SettlementStatus,
            Review = finalState == null ? null : Map(finalState)
        };
    }

    /// <summary>
    /// Runs the shared GET-only read-back comparison for one operation.
    /// </summary>
    /// <param name="operation">Detached operation supplying the immutable target and pre-mutation evidence.</param>
    /// <param name="cancellationToken">Token that cancels the panel reads.</param>
    /// <returns>A sanitized comparison; the exact client reference is intentionally discarded.</returns>
    /// <remarks>
    /// The configured panel descriptor is built through <see cref="XuiV3RenewalPanelDescriptor" /> so an administrator
    /// re-check reads exactly the panel and credentials the background worker uses. No request other than a read is ever
    /// issued from this path.
    /// </remarks>
    private async Task<XuiV3RenewalOperationStore.RenewalComparisonResult> RunReadOnlyComparisonAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
    {
        var (comparison, _) = await _operationStore.RecoverByReadBackAsync(
            operation,
            XuiV3RenewalPanelDescriptor.Build(_appConfig),
            _configuration,
            cancellationToken);
        return comparison;
    }

    /// <summary>
    /// Detects any durable financial artifact already recorded for one renewal operation.
    /// </summary>
    /// <param name="operation">Operation whose settlement idempotency key identifies its financial records.</param>
    /// <param name="cancellationToken">Token that cancels the users.db and credentials.db reads.</param>
    /// <returns>
    /// <c>true</c> when a users.db wallet ledger row, a users.db website wallet debit receipt, or a credentials.db
    /// bot-wallet receipt already exists for this operation.
    /// </returns>
    /// <remarks>
    /// The credentials.db receipt is keyed by the same generated key the debit path writes, so a receipt proves a
    /// wallet change for this exact operation even when the ledger write never completed. All three sources are checked
    /// because they are written by different steps and any one of them alone is enough to forbid an unlock.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when credentials.db cannot be read, so the caller fails closed instead of unlocking on unknown evidence.
    /// </exception>
    private async Task<bool> HasFinancialArtifactAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
    {
        if (await _operationStore.HasSettlementArtifactAsync(operation, cancellationToken))
            return true;

        var ledgerKey = XuiV3RenewalOperationStore.BuildSettlementLedgerKey(operation);
        if (await _walletLedgerService.GetByKeyAsync(ledgerKey, cancellationToken) != null)
            return true;

        var walletReceipt = await _credentialsStore.GetWalletOperationAsync(ledgerKey, cancellationToken);
        return walletReceipt != null;
    }

    /// <summary>
    /// Reports whether one operation is currently parked in manual review.
    /// </summary>
    /// <param name="operation">Detached operation to classify.</param>
    /// <returns><c>true</c> only for an operation whose persisted status is manual review.</returns>
    private static bool IsManualReview(XuiV3RenewalOperation operation) =>
        string.Equals(operation.Status, XuiV3RenewalOperationStatuses.ManualReview, StringComparison.Ordinal);

    /// <summary>
    /// Projects one durable operation onto the sanitized administrator view.
    /// </summary>
    /// <param name="operation">Detached operation loaded from users.db.</param>
    /// <returns>A review view containing no account identity, payload, panel reference, or token.</returns>
    private static ManualReviewReview Map(XuiV3RenewalOperation operation) => new()
    {
        OperationId = operation.Id,
        BotId = operation.BotId,
        TelegramUserId = operation.TelegramUserId,
        CreatedAtUtc = operation.CreatedAtUtc,
        ManualReviewAtUtc = operation.ManualReviewAtUtc,
        Status = operation.Status,
        SettlementStatus = operation.SettlementStatus,
        RecoveryEligible = operation.RecoveryEligible,
        ReconcileAttemptCount = operation.ReconcileAttemptCount,
        LastComparisonOutcome = operation.LastComparisonOutcome,
        LastMismatchSummary = operation.LastMismatchSummary,
        PriceToman = operation.PriceToman,
        HoldsAccountLock = !string.IsNullOrEmpty(operation.AccountLockKey)
    };

    /// <summary>
    /// Creates a result for an outcome that produced no comparison evidence.
    /// </summary>
    /// <param name="outcome">Result category.</param>
    /// <param name="operationId">Internal users.db key supplied by the caller.</param>
    /// <param name="operation">Optional operation whose current state should be reported.</param>
    /// <returns>A structured result carrying the current durable state when it is known.</returns>
    private static ManualReviewActionResult Result(
        ManualReviewOutcome outcome,
        int operationId,
        XuiV3RenewalOperation operation = null) => new()
    {
        Outcome = outcome,
        OperationId = operationId,
        Status = operation?.Status,
        SettlementStatus = operation?.SettlementStatus,
        Review = operation == null ? null : Map(operation)
    };
}
