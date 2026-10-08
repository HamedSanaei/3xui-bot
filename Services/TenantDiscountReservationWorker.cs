using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Adminbot.Services.Telemetry;

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
    /// <remarks>Each cycle advances past unresolved claims, wrapping to the first id only after a complete pass. Refunded wallet claims remain scanned even if previously consumed; an exact committed refund receipt must be verified before release. Exactly-once transitions occur inside users.db write transactions.
    /// Fixed payload-free SQLite telemetry identifies this worker's contention; tenant/customer ids, refund amounts
    /// and provider data remain private. No claim, transaction, retry or financial rule is changed.</remarks>
    /// <example>After scanning ids 1 through 50, the next cycle starts after 50; an empty page resets the cursor to zero.</example>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sqliteDiagnostics = LatencySqliteOperationScope.Push(LatencySqliteOperationCategory.TenantDiscountReservation);
        var after = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                after = await ProcessOnceAsync(after, stoppingToken);
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

    /// <summary>Expires eligible open quotes and reconciles one bounded page using the production scan path.</summary>
    /// <param name="after">Exclusive redemption-id cursor from the preceding successful cycle.</param>
    /// <param name="cancellationToken">Cancellation propagated to local reads, expiry, and individual reconciliation.</param>
    /// <returns>The final id in this page, or zero when the page is empty so the next cycle wraps around.</returns>
    /// <remarks>
    /// Quote expiry reuses its exact predicate for a read-only eligibility check and conditional update. An idle
    /// cycle does not request a SQLite writer; quotes becoming eligible after an empty check wait for the next cycle.
    /// Each claim is reconciled in a separate service scope, and a failed claim does not prevent cursor progress.
    /// Refunded-wallet consumed claims remain eligible for their existing committed-refund proof checks.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The cycle cancellation token is cancelled.</exception>
    /// <example>An empty WAL scan returns zero while another connection holds the writer transaction.</example>
    internal async Task<int> ProcessOnceAsync(int after = 0, CancellationToken cancellationToken = default)
    {
        List<int> ids;
        await using (var db = users.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var expiredQuotes = db.TenantDiscountQuotes
                .Where(x => x.State == TenantDiscountQuoteStates.Open && x.ExpiresAtUtc <= now);
            if (await expiredQuotes.AnyAsync(cancellationToken))
                await expiredQuotes.ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, TenantDiscountQuoteStates.Expired)
                        .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            ids = await (from claim in db.TenantDiscountRedemptions.AsNoTracking()
                join order in db.TenantBotOrders.AsNoTracking() on claim.TenantBotOrderId equals order.Id
                where claim.Id > after && (claim.State == TenantDiscountRedemptionStates.Reserved
                    || claim.State == TenantDiscountRedemptionStates.Consumed
                        && order.PaymentProvider == "wallet" && order.CustomerWalletState == "refunded")
                orderby claim.Id
                select claim.Id).Take(50).ToListAsync(cancellationToken);
        }
        foreach (var id in ids)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TenantBotService>()
                    .ReconcileTenantDiscountReservationAsync(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Tenant discount claim reconciliation pending. RedemptionId={RedemptionId} ErrorType={ErrorType}",
                    id, ex.GetType().Name);
            }
        }
        return ids.Count == 0 ? 0 : ids[^1];
    }
}
