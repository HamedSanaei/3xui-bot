using Adminbot.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

/// <summary>
/// Resumes XUI v3 link changes whose panel result remained ambiguous after a timeout or process restart.
/// </summary>
/// <remarks>
/// The worker claims rows through a users.db lease, restores the original bot runtime context, and calls the same
/// processor used by the confirmation callback. It never replays Telegram updates and never generates new identity
/// values. A missing or disabled bot delays recovery without unlocking the protected panel client.
/// </remarks>
public sealed class XuiV3LinkChangeRecoveryService : BackgroundService
{
    private readonly XuiV3LinkChangeOperationStore _operationStore;
    private readonly XuiV3BotFlowService _flowService;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly ILogger<XuiV3LinkChangeRecoveryService> _logger;
    private readonly AppConfig _appConfig;

    /// <summary>
    /// Creates the recovery worker and its dependencies.
    /// </summary>
    /// <param name="operationStore">Durable users.db operation and lease store.</param>
    /// <param name="flowService">Shared owned/tenant account processor used for safe read-back and reconciliation.</param>
    /// <param name="botRegistry">Registry used to resolve the exact bot that created each operation.</param>
    /// <param name="botClientProvider">Provider of the matching Telegram client for progress notifications.</param>
    /// <param name="botContextAccessor">Accessor used to restore bot and tenant attribution during recovery.</param>
    /// <param name="credentialsDbContext">Shared credentials store used only to load the original account owner.</param>
    /// <param name="logger">Operational logger for recovery failures and missing runtime dependencies.</param>
    /// <param name="configuration">Runtime configuration containing the recovery polling interval.</param>
    public XuiV3LinkChangeRecoveryService(
        XuiV3LinkChangeOperationStore operationStore,
        XuiV3BotFlowService flowService,
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        BotContextAccessor botContextAccessor,
        CredentialsDbContext credentialsDbContext,
        ILogger<XuiV3LinkChangeRecoveryService> logger,
        IConfiguration configuration)
    {
        _operationStore = operationStore;
        _flowService = flowService;
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _botContextAccessor = botContextAccessor;
        _credentialsDbContext = credentialsDbContext;
        _logger = logger;
        _appConfig = configuration?.Get<AppConfig>() ?? new AppConfig();
    }

    /// <summary>
    /// Polls due recovery rows until application shutdown and processes each claimed client independently.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token that stops new claims and panel work.</param>
    /// <returns>A task that runs for the lifetime of the application.</returns>
    /// <remarks>
    /// One failed bot, customer, Telegram notification, or panel call is isolated to its operation. The outer loop
    /// remains alive and the store schedules bounded exponential retry or manual review.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Clamp(_appConfig.XuiV3LinkChangeRecoveryPollSeconds, 5, 3600);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var operations = await _operationStore.ClaimDueRecoveryAsync(10, stoppingToken);
                foreach (var operation in operations)
                    await RecoverOneAsync(operation, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XUI link-change recovery scan failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }

    /// <summary>
    /// Restores one operation's bot and customer context and invokes the shared idempotent processor.
    /// </summary>
    /// <param name="operation">Claimed operation whose lease prevents concurrent processing.</param>
    /// <param name="cancellationToken">Application shutdown token.</param>
    /// <returns>A task that completes after processing or durable rescheduling.</returns>
    private async Task RecoverOneAsync(
        XuiV3LinkChangeOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var bot = _botRegistry.Bots.FirstOrDefault(x =>
                string.Equals(x.Id, operation.BotId, StringComparison.OrdinalIgnoreCase));
            if (bot == null || !bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
            {
                await _operationStore.ScheduleRecoveryAsync(
                    operation.OperationKey,
                    "recovery-bot-unavailable",
                    "The originating Telegram bot is unavailable for recovery.",
                    cancellationToken);
                return;
            }

            var credUser = await _credentialsDbContext.GetUserStatusWithId(operation.TelegramUserId);
            if (credUser == null)
            {
                await _operationStore.ScheduleRecoveryAsync(
                    operation.OperationKey,
                    "recovery-user-unavailable",
                    "The credentials owner could not be loaded for recovery.",
                    cancellationToken);
                return;
            }

            var botClient = _botClientProvider.GetClient(bot.Id);
            using (_botContextAccessor.Push(new BotRuntimeContext { Config = bot, Client = botClient }))
            {
                await _flowService.RecoverAccountChangeLinkAsync(
                    botClient,
                    operation,
                    credUser,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "XUI link-change recovery attempt failed. operationKey={OperationKey}, botId={BotId}, clientId={ClientId}",
                operation.OperationKey,
                operation.BotId,
                operation.ClientId);
            await _operationStore.ScheduleRecoveryAsync(
                operation.OperationKey,
                "recovery-worker-exception",
                "The recovery worker encountered an unexpected error.",
                cancellationToken);
        }
    }
}
