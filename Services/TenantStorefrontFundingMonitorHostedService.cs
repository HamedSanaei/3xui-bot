using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Periodically evaluates enabled tenant storefront owners with a read-only funding snapshot so external changes to
/// the Gozargah website wallet are detected between customer interactions.
/// </summary>
/// <remarks>
/// The monitor is a pure observer: it never debits wallets, never creates debt transfers, never repays debt, and never
/// mutates XUI, orders, or payments. Each cycle performs at most one read-only website wallet lookup per owner and
/// feeds the same <see cref="TenantStorefrontFundingAlertService"/> used by the customer lane, so the existing funding
/// formula, episode numbering, and recovery cancellation rules all apply unchanged.
/// </remarks>
public sealed class TenantStorefrontFundingMonitorHostedService : BackgroundService
{
    private const int MaximumStorefrontsPerCycle = 20;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly int _intervalMinutes;
    private readonly ILogger<TenantStorefrontFundingMonitorHostedService> _logger;
    private readonly SemaphoreSlim _cycleGuard = new(1, 1);
    private long _cycleNumber;

    /// <summary>
    /// Creates the periodic funding monitor.
    /// </summary>
    /// <param name="scopeFactory">
    /// Required service-scope factory used to resolve short-lived <see cref="UserDbContextFactory"/>,
    /// <see cref="TenantAccessService"/>, and <see cref="TenantStorefrontFundingAlertService"/> instances per cycle.
    /// </param>
    /// <param name="appConfig">
    /// Validated application configuration supplying <see cref="AppConfig.TenantStorefrontFundingMonitorIntervalMinutes"/>.
    /// </param>
    /// <param name="logger">Operational diagnostics with storefront ids, never credentials or payloads.</param>
    public TenantStorefrontFundingMonitorHostedService(IServiceScopeFactory scopeFactory, AppConfig appConfig,
        ILogger<TenantStorefrontFundingMonitorHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _intervalMinutes = appConfig.TenantStorefrontFundingMonitorIntervalMinutes;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs one cycle immediately after startup, then sleeps for the configured interval between cycles. The
    /// non-overlap guard skips a scheduled cycle when the previous one is still running, so slow website lookups
    /// never pile up against the Gozargah API.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleGuardedAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task RunCycleGuardedAsync(CancellationToken cancellationToken)
    {
        if (!await _cycleGuard.WaitAsync(0, cancellationToken))
        {
            _logger.LogInformation("Tenant storefront funding monitor cycle skipped because the previous cycle is still running.");
            return;
        }
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<UserDbContextFactory>();
            var access = scope.ServiceProvider.GetRequiredService<TenantAccessService>();
            var alerts = scope.ServiceProvider.GetRequiredService<TenantStorefrontFundingAlertService>();
            var evaluated = await RunCycleAsync(factory, access, alerts, cancellationToken);
            _logger.LogInformation("Tenant storefront funding monitor cycle completed. evaluatedStorefronts={EvaluatedStorefronts}",
                evaluated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Tenant storefront funding monitor cycle failed. ErrorType={ErrorType}", ex.GetType().Name);
        }
        finally
        {
            _cycleGuard.Release();
        }
    }

    /// <summary>
    /// Runs one read-only monitoring cycle over a bounded batch of enabled tenant storefronts.
    /// </summary>
    /// <param name="factory">Users.db factory used to load candidate storefronts.</param>
    /// <param name="access">Tenant access service; only the read-only snapshot method is invoked.</param>
    /// <param name="alerts">Funding alert service that persists any observed transition or recovery.</param>
    /// <param name="cancellationToken">Cycle cancellation token.</param>
    /// <returns>The number of storefronts evaluated by this cycle, at most <see cref="MaximumStorefrontsPerCycle"/>.</returns>
    /// <remarks>
    /// Owners with multiple storefronts are deduplicated: the read-only snapshot is evaluated once per owner and each
    /// storefront state row is updated individually with the same snapshot. The batch cursor rotates across cycles so
    /// a growing storefront list is eventually covered instead of always re-reading the same first rows.
    /// </remarks>
    internal async Task<int> RunCycleAsync(UserDbContextFactory factory, TenantAccessService access,
        TenantStorefrontFundingAlertService alerts, CancellationToken cancellationToken = default)
    {
        List<BotInstance> candidates;
        await using (var db = factory.CreateDbContext())
        {
            var total = await db.BotInstances.AsNoTracking()
                .CountAsync(x => x.Type == BotInstanceTypes.Tenant && x.Enabled && x.OwnerTelegramUserId > 0, cancellationToken);
            if (total == 0) return 0;
            var batchCount = Math.Max(1, (int)Math.Ceiling(total / (double)MaximumStorefrontsPerCycle));
            var offset = ((int)((uint)Interlocked.Increment(ref _cycleNumber) - 1) % batchCount) * MaximumStorefrontsPerCycle;
            var rows = await db.BotInstances.AsNoTracking()
                .Where(x => x.Type == BotInstanceTypes.Tenant && x.Enabled && x.OwnerTelegramUserId > 0)
                .OrderBy(x => x.Id).Skip(offset).Take(MaximumStorefrontsPerCycle).ToListAsync(cancellationToken);
            candidates = rows.Where(TenantBotService.CANQUEUEUNDERFUNDEDALERT).ToList();
        }

        var evaluated = 0;
        foreach (var ownerGroup in candidates.GroupBy(x => x.OwnerTelegramUserId!.Value))
        {
            var evaluation = await access.EvaluateFundingSnapshotAsync(ownerGroup.Key, cancellationToken);
            foreach (var storefront in ownerGroup)
            {
                await alerts.ObserveAsync(storefront, evaluation, customerAttempt: false, allowUnderfundedAlerts: true, cancellationToken);
                evaluated++;
            }
        }
        return evaluated;
    }
}