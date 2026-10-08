using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Runs bounded durable migrations and independent periodic health on two tracked asynchronous loops.</summary>
/// <remarks>State rows are the durable queue. No Task.Run is created per callback, cooldown is scheduled rather than
/// slept under a bot lock, and slow migrations cannot starve root or another bot's health. Both loops are joined
/// at shutdown; diagnostics contain no token, URL, external exception or customer content.</remarks>
public sealed class TelegramEndpointCoordinatorWorker : BackgroundService
{
    /// <summary>Singleton durable controller shared with the administration panel and startup hydration.</summary>
    private readonly TelegramEndpointCoordinator _coordinator;
    /// <summary>Metadata-only worker diagnostics; no exception object is logged.</summary>
    private readonly ILogger<TelegramEndpointCoordinatorWorker> _logger;
    /// <summary>Clock controlling monitor cadence independently of operator wake signals.</summary>
    private readonly TimeProvider _time;
    /// <summary>Validated configured health cadence in seconds.</summary>
    private readonly int _healthIntervalSeconds;

    /// <summary>Creates the one tracked endpoint worker without resolving any bot receiver host.</summary>
    /// <param name="coordinator">Required singleton controller; startup must hydrate it before receivers.</param>
    /// <param name="options">Required validated endpoint monitor cadence.</param>
    /// <param name="logger">Required metadata-only operational logger.</param>
    /// <param name="timeProvider">Optional controlled UTC clock for deterministic hosting tests.</param>
    /// <remarks>Construction performs no network, SQL or lifecycle work.</remarks>
    public TelegramEndpointCoordinatorWorker(TelegramEndpointCoordinator coordinator, TelegramEndpointRoutingOptions options,
        ILogger<TelegramEndpointCoordinatorWorker> logger, TimeProvider timeProvider = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _healthIntervalSeconds = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot().HealthCheckIntervalSeconds;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Joins the independent bounded migration and periodic-health loops for the hosted lifetime.</summary>
    /// <param name="stoppingToken">Host shutdown cancellation, never a callback's foreground deadline.</param>
    /// <returns>The tracked hosted-service lifetime; both loops finish before shutdown completes.</returns>
    /// <remarks>Neither loop creates per-event tasks. Per-bot nonwaiting coordinator locks isolate migration from
    /// another bot's health, and shared root probes serialize only their short transport/metadata boundary.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _coordinator.InitializeAsync(stoppingToken);
        await Task.WhenAll(RunMigrationLoopAsync(stoppingToken), RunHealthLoopAsync(stoppingToken));
    }

    /// <summary>Processes bounded due intent scans and a coalesced wake signal independently of monitor cadence.</summary>
    /// <param name="token">Tracked host shutdown cancellation.</param>
    /// <returns>The migration-loop lifetime, including bounded cleanup of any in-flight lifecycle lease.</returns>
    private async Task RunMigrationLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await _coordinator.RunPendingOperationsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception)
            {
                _logger.LogWarning("Telegram endpoint worker scan deferred. Category={Category}", "worker_failure");
            }
            try { await _coordinator.WaitForWorkAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Observes tokenless Local and independent bot health even while a migration waits on drain or Telegram.</summary>
    /// <param name="token">Tracked host shutdown cancellation.</param>
    /// <returns>The monitor-loop lifetime, controlled by the injected TimeProvider rather than migration completion.</returns>
    private async Task RunHealthLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await _coordinator.RunHealthCycleAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception)
            {
                _logger.LogWarning("Telegram endpoint health scan deferred. Category={Category}", "health_worker_failure");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_healthIntervalSeconds), _time, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
