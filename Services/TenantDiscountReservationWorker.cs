using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Reconciles reserved claims and refunded-wallet claims after callbacks, restarts or delivery failures.</summary>
/// <param name="users">Factory for the authoritative users.db claim and quote index.</param>
/// <param name="scopes">Creates an independent tenant service scope for each claim's proof check.</param>
/// <param name="logger">Records non-secret claim ids and exception types for operational recovery.</param>
/// <remarks>Unknown provider outcomes remain reserved for manual review. A clock alone never frees an attempted gateway payment.</remarks>
public sealed class TenantDiscountReservationWorker(UserDbContextFactory users, IServiceScopeFactory scopes,
    ILogger<TenantDiscountReservationWorker> logger) : BackgroundService
{
    /// <summary>Scans claim ids in bounded pages and expires unadmitted quotes without touching payment rows.</summary>
    /// <param name="stoppingToken">Host lifetime cancellation, propagated to each local read and claim reconciliation.</param>
    /// <returns>The worker lifetime task; individual claim failures do not stop later claims.</returns>
    /// <remarks>Each cycle advances past unresolved claims, wrapping to the first id only after a complete pass. Refunded wallet claims remain scanned even if previously consumed; an exact committed refund receipt must be verified before release. Exactly-once transitions occur inside users.db write transactions.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var after = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<int> ids;
                await using (var db = users.CreateDbContext())
                {
                    var now = DateTime.UtcNow;
                    await db.TenantDiscountQuotes.Where(x => x.State == TenantDiscountQuoteStates.Open
                        && x.ExpiresAtUtc <= now).ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.State, TenantDiscountQuoteStates.Expired)
                            .SetProperty(x => x.UpdatedAtUtc, now), stoppingToken);
                    ids = await (from claim in db.TenantDiscountRedemptions.AsNoTracking()
                        join order in db.TenantBotOrders.AsNoTracking() on claim.TenantBotOrderId equals order.Id
                        where claim.Id > after && (claim.State == TenantDiscountRedemptionStates.Reserved
                            || claim.State == TenantDiscountRedemptionStates.Consumed
                                && order.PaymentProvider == "wallet" && order.CustomerWalletState == "refunded")
                        orderby claim.Id
                        select claim.Id).Take(50).ToListAsync(stoppingToken);
                }
                after = ids.Count == 0 ? 0 : ids[^1];
                foreach (var id in ids)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<TenantBotService>()
                            .ReconcileTenantDiscountReservationAsync(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Tenant discount claim reconciliation pending. RedemptionId={RedemptionId} ErrorType={ErrorType}",
                            id, ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Tenant discount reservation scan failed. ErrorType={ErrorType}", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
