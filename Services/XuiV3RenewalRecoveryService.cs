using Adminbot.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durably reconciles ambiguous XUI v3 renewals and completes settlement after delayed panel commits or restarts.
/// </summary>
/// <remarks>
/// Mutation recovery is strictly read-only: this worker calls <c>GET client</c> and compares the stored absolute
/// target through <see cref="XuiV3RenewalOperationStore.IsTargetReached"/>. It never calls or delegates a call to
/// <c>POST /UpdateClient</c>. Applied operations reuse the callback settlement guards so financial effects remain
/// exactly once. Inconclusive rows keep their account lock through exponential backoff and eventual manual review.
/// </remarks>
public sealed class XuiV3RenewalRecoveryService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly XuiV3RenewalOperationStore _operationStore;
    private readonly XuiV3BotFlowService _ownedFlowService;
    private readonly TenantBotService _tenantBotService;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly ILogger<XuiV3RenewalRecoveryService> _logger;

    /// <summary>
    /// Creates the renewal reconciliation worker.
    /// </summary>
    /// <param name="operationStore">Durable users.db operation, account-lock, lease, and backoff store.</param>
    /// <param name="ownedFlowService">Owned-bot settlement entry point that reuses wallet idempotency guards.</param>
    /// <param name="tenantBotService">Tenant settlement entry point that reuses the tenant fulfillment gate.</param>
    /// <param name="botRegistry">Runtime registry used to restore the operation's originating owned bot.</param>
    /// <param name="botClientProvider">Provider used to notify the payer after recovered owned settlement.</param>
    /// <param name="botContextAccessor">Accessor that scopes recovered logs and ledger metadata to the original bot.</param>
    /// <param name="configuration">Runtime XUI base URL, root path, token, and request timeout configuration.</param>
    /// <param name="logger">Local operational logger; UUID, normalized email, token, payload, and response body are omitted.</param>
    public XuiV3RenewalRecoveryService(
        XuiV3RenewalOperationStore operationStore,
        XuiV3BotFlowService ownedFlowService,
        TenantBotService tenantBotService,
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        BotContextAccessor botContextAccessor,
        IConfiguration configuration,
        ILogger<XuiV3RenewalRecoveryService> logger)
    {
        _operationStore = operationStore;
        _ownedFlowService = ownedFlowService;
        _tenantBotService = tenantBotService;
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _botContextAccessor = botContextAccessor;
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
    /// A GET result below target is not definitive failure because the original POST may still commit. Both unavailable
    /// and below-target results therefore retain the lock and back off. Only absolute-target success permits settlement.
    /// </remarks>
    private async Task RecoverOneAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Status != XuiV3RenewalOperationStatuses.Applied)
            {
                var (outcome, _) = await _operationStore.RecoverByReadBackAsync(
                    operation,
                    BuildConfiguredPanelServerInfo(),
                    _configuration,
                    cancellationToken);
                if (outcome != XuiV3RenewalOperationStore.RecoveryOutcome.Applied)
                {
                    var manual = await _operationStore.ScheduleInconclusiveReconciliationAsync(
                        operation,
                        outcome == XuiV3RenewalOperationStore.RecoveryOutcome.NotApplied
                            ? "The absolute renewal target is not visible yet; delayed commit remains possible."
                            : "The panel read-back was unavailable.",
                        cancellationToken);
                    if (manual)
                    {
                        _logger.LogError(
                            "XUI v3 renewal moved to manual review after bounded GET-only reconciliation. renewalOperationId={RenewalOperationId}",
                            operation.OperationId);
                    }

                    return;
                }

                if (!await _operationStore.ResolveAmbiguousToAppliedAsync(operation, cancellationToken))
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
                await _operationStore.ScheduleInconclusiveReconciliationAsync(
                    operation,
                    "Unexpected GET-only reconciliation failure.",
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Routes one applied operation to its existing owned or tenant exactly-once settlement boundary.
    /// </summary>
    /// <param name="operation">Applied operation that remains account-locked until settlement completes.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns><c>true</c> when settlement is durably complete; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Tenant operations retain the order-level fulfillment gate; owned operations restore the originating bot context
    /// before the shared settlement service writes ledger/log metadata.
    /// </remarks>
    private async Task<bool> SettleAppliedAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(operation.TenantBotOrderId))
            return await _tenantBotService.SettleRecoveredTenantRenewalAsync(operation, cancellationToken);

        var bot = _botRegistry.Bots.FirstOrDefault(x =>
            string.Equals(x.Id, operation.BotId, StringComparison.OrdinalIgnoreCase));
        if (bot == null || !bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
            return false;

        var botClient = _botClientProvider.GetClient(bot.Id);
        using (_botContextAccessor.Push(new BotRuntimeContext { Config = bot, Client = botClient }))
        {
            return await _ownedFlowService.SettleRecoveredOwnedRenewalAsync(
                botClient,
                operation,
                cancellationToken);
        }
    }

    /// <summary>
    /// Builds the global XUI v3 panel descriptor used solely for recovery GET requests.
    /// </summary>
    /// <returns>The configured panel URL, root path, API token, and subscription base.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <c>XuiV3ApiBaseUrl</c> is absent.</exception>
    /// <remarks>The descriptor is used only by authenticated GET reconciliation and is never logged or user-visible.</remarks>
    private ServerInfo BuildConfiguredPanelServerInfo()
    {
        if (string.IsNullOrWhiteSpace(_appConfig.XuiV3ApiBaseUrl))
            throw new InvalidOperationException("XuiV3ApiBaseUrl is not configured.");

        return new ServerInfo
        {
            ApiVersion = "v3",
            ApiToken = _appConfig.XuiV3ApiToken,
            Url = _appConfig.XuiV3ApiBaseUrl.TrimEnd('/'),
            RootPath = (_appConfig.XuiV3ApiRootPath ?? string.Empty).Trim('/'),
            SubLinkUrl = string.IsNullOrWhiteSpace(_appConfig.XuiV3SubLinkBaseUrl)
                ? null
                : _appConfig.XuiV3SubLinkBaseUrl.TrimEnd('/'),
            Name = "Configured V3 Panel"
        };
    }
}
