using Adminbot.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durably reconciles ambiguous XUI v3 renewals and completes settlement after delayed panel commits or restarts.
/// </summary>
/// <remarks>
/// Mutation recovery is strictly read-only: this worker calls <c>GET client</c> and compares the stored absolute
/// target through <see cref="XuiV3RenewalOperationStore.CompareRenewalState"/>. It never calls or delegates a call to
/// <c>POST /UpdateClient</c>. Applied operations reuse the callback settlement guards so financial effects remain
/// exactly once. Partial/drift evidence stays locked for manual review; repeated exact pre-state evidence can release
/// the lock as definitively not applied only after the conservative observation window.
/// </remarks>
public sealed class XuiV3RenewalRecoveryService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Maximum manual-review rows announced by one notification sweep.</summary>
    private const int ManualReviewNotificationBatchSize = 25;

    private readonly XuiV3RenewalOperationStore _operationStore;
    private readonly XuiV3RenewalAppliedSettlementRouter _settlementRouter;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly ILogger<XuiV3RenewalRecoveryService> _logger;

    /// <summary>
    /// Creates the renewal reconciliation worker.
    /// </summary>
    /// <param name="operationStore">Durable users.db operation, account-lock, lease, and backoff store.</param>
    /// <param name="settlementRouter">
    /// Router to the existing owned/tenant exactly-once settlement implementation. The worker never settles directly,
    /// so an operator-confirmed operation and a worker-recovered operation always reach the same settlement code.
    /// </param>
    /// <param name="configuration">Runtime XUI base URL, root path, token, and request timeout configuration.</param>
    /// <param name="logger">Local operational logger; UUID, normalized email, token, payload, and response body are omitted.</param>
    /// <remarks>Existing durable leases and GET-only recovery rules remain authoritative.</remarks>
    public XuiV3RenewalRecoveryService(
        XuiV3RenewalOperationStore operationStore,
        XuiV3RenewalAppliedSettlementRouter settlementRouter,
        IConfiguration configuration,
        ILogger<XuiV3RenewalRecoveryService> logger)
    {
        _operationStore = operationStore;
        _settlementRouter = settlementRouter;
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
    }

    /// <summary>
    /// Polls due operations for the lifetime of the host and isolates failures to each durable row.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token that stops new claims and GET/settlement work.</param>
    /// <returns>A task that runs until application shutdown.</returns>
    /// <remarks>
    /// Recovery leases make concurrent service instances safe. A process crash leaves the account locked and another
    /// instance may resume after lease expiry without replaying the panel mutation.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var operations = await _operationStore.ClaimDueReconciliationAsync(10, stoppingToken);
                foreach (var operation in operations)
                    await RecoverOneAsync(operation, stoppingToken);

                // Announce manual reviews that were escalated by an earlier process or already existed before this
                // deployment. The durable notification marker makes the sweep safe to run on every scan.
                await AnnounceUnnotifiedManualReviewsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XUI v3 renewal reconciliation scan failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Reconciles one claimed mutation by GET or settles one already-applied operation.
    /// </summary>
    /// <param name="operation">Detached operation carrying the current recovery claim token.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A task that completes after durable apply, settlement, backoff, or manual-review state is written.</returns>
    /// <remarks>
    /// Applied permits settlement; repeated exact pre-mutation observations can safely fail and unlock after the grace
    /// window. Partial application or drift moves to locked manual review, while unavailable reads back off.
    /// </remarks>
    private async Task RecoverOneAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Status != XuiV3RenewalOperationStatuses.Applied)
            {
                var (comparison, _) = await _operationStore.RecoverByReadBackAsync(
                    operation,
                    BuildConfiguredPanelServerInfo(),
                    _configuration,
                    cancellationToken);
                if (comparison.Outcome != XuiV3RenewalOperationStore.RecoveryOutcome.Applied)
                {
                    var disposition = await _operationStore.PersistReconciliationResultAsync(
                        operation,
                        comparison,
                        cancellationToken);
                    if (disposition == XuiV3RenewalOperationStore.ReconciliationDisposition.ManualReview)
                    {
                        _logger.LogError(
                            "XUI v3 renewal moved to locked manual review. renewalOperationId={RenewalOperationId}, outcome={Outcome}, mismatchSummary={MismatchSummary}",
                            operation.OperationId,
                            comparison.Outcome,
                            comparison.Summary);

                        // Durable one-shot notification: the condition guarantees exactly one operator alert per
                        // escalation even though every subsequent scan sees the same locked row.
                        if (await _operationStore.TryClaimManualReviewNotificationAsync(operation, cancellationToken))
                            LogManualReviewOperatorAlert(operation);
                    }
                    else if (disposition == XuiV3RenewalOperationStore.ReconciliationDisposition.DefinitivelyFailed)
                    {
                        _logger.LogWarning(
                            "XUI v3 renewal was proven not applied and its account lock was released without settlement. renewalOperationId={RenewalOperationId}, mismatchSummary={MismatchSummary}",
                            operation.OperationId,
                            comparison.Summary);
                    }

                    return;
                }

                if (!await _operationStore.ResolveAmbiguousToAppliedAsync(operation, comparison, cancellationToken))
                {
                    await _operationStore.ReleaseRecoveryClaimAsync(operation, cancellationToken);
                    return;
                }

                _logger.LogInformation(
                    "XUI v3 renewal target appeared during GET-only reconciliation. renewalOperationId={RenewalOperationId}",
                    operation.OperationId);
            }

            var settled = await SettleAppliedAsync(operation, cancellationToken);
            if (settled)
            {
                await _operationStore.ReleaseRecoveryClaimAsync(operation, cancellationToken);
                return;
            }

            await _operationStore.ScheduleAppliedSettlementRetryAsync(
                operation,
                "Applied renewal settlement could not complete during this recovery attempt.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "XUI v3 renewal reconciliation attempt failed. renewalOperationId={RenewalOperationId}",
                operation.OperationId);

            if (operation.Status == XuiV3RenewalOperationStatuses.Applied)
            {
                await _operationStore.ScheduleAppliedSettlementRetryAsync(
                    operation,
                    "Unexpected settlement recovery failure.",
                    cancellationToken);
            }
            else
            {
                await _operationStore.PersistReconciliationResultAsync(
                    operation,
                    new XuiV3RenewalOperationStore.RenewalComparisonResult
                    {
                        Outcome = XuiV3RenewalOperationStore.RecoveryOutcome.Unavailable,
                        Summary = "identity=unavailable;read=unexpected-failure"
                    },
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Routes one applied operation to the shared owned or tenant exactly-once settlement implementation.
    /// </summary>
    /// <param name="operation">Applied operation that remains account-locked until settlement completes.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns><c>true</c> when settlement is durably complete; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Delegating to <see cref="XuiV3RenewalAppliedSettlementRouter" /> keeps a single settlement implementation shared
    /// with the administrator manual-review confirmation path, so neither caller can debit twice.
    /// </remarks>
    private Task<bool> SettleAppliedAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
        => _settlementRouter.SettleAppliedAsync(operation, cancellationToken);

    /// <summary>
    /// Emits the single operator alert for one newly escalated manual-review operation.
    /// </summary>
    /// <param name="operation">Operation whose notification marker this process just claimed.</param>
    /// <returns>A task that completes after the alert is written.</returns>
    /// <remarks>
    /// The alert carries only internal ids and fixed state categories. Account email, UUID, panel URL, token, mutation
    /// payload, and raw panel error text are deliberately absent, because the operator channel is a Telegram surface.
    /// </remarks>
    private void LogManualReviewOperatorAlert(XuiV3RenewalOperation operation)
    {
        _logger.LogError(
            "XUI v3 renewal requires administrator manual review. renewalOperationId={RenewalOperationId}, botId={BotId}, telegramUserId={TelegramUserId}, settlementStatus={SettlementStatus}, recoveryEligible={RecoveryEligible}, reconcileAttempts={ReconcileAttempts}, comparisonOutcome={ComparisonOutcome}",
            operation.OperationId,
            operation.BotId,
            operation.TelegramUserId,
            operation.SettlementStatus,
            operation.RecoveryEligible,
            operation.ReconcileAttemptCount,
            operation.LastComparisonOutcome);
    }

    /// <summary>
    /// Announces every manual-review operation whose notification was never delivered.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token that cancels the sweep.</param>
    /// <returns>A task that completes after one bounded sweep.</returns>
    /// <remarks>
    /// This covers the two cases the escalation-time alert cannot: a process that stopped between escalating an
    /// operation and announcing it, and historical rows that were already locked in manual review before this feature
    /// existed. It includes recovery-ineligible rows, which no other code path can ever release, so an operator is told
    /// about every account lock that needs a human. Repeated sweeps produce no duplicate alert because the marker is
    /// claimed atomically.
    /// </remarks>
    private async Task AnnounceUnnotifiedManualReviewsAsync(CancellationToken cancellationToken)
    {
        var unannounced = await _operationStore.ClaimUnnotifiedManualReviewsAsync(
            ManualReviewNotificationBatchSize,
            cancellationToken);
        foreach (var operation in unannounced)
            LogManualReviewOperatorAlert(operation);
    }

    /// <summary>
    /// Builds the global XUI v3 panel descriptor used solely for recovery GET requests.
    /// </summary>
    /// <returns>The configured panel URL, root path, API token, and subscription base.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <c>XuiV3ApiBaseUrl</c> is absent.</exception>
    /// <remarks>The descriptor is used only by authenticated GET reconciliation and is never logged or user-visible.</remarks>
    private ServerInfo BuildConfiguredPanelServerInfo() => XuiV3RenewalPanelDescriptor.Build(_appConfig);
}
