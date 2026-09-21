using Microsoft.EntityFrameworkCore;

/// <summary>Resumes receipt-backed tenant orders without ever initiating a new customer debit.</summary>
/// <param name="users">Factory for durable tenant order metadata.</param>
/// <param name="scopes">Creates independent workflow scopes per order.</param>
/// <param name="logger">Coarse recovery diagnostics without payloads or credentials.</param>
/// <remarks>Paid work survives permission revocation. Existing creation and renewal coordinators remain the only XUI mutation authority.</remarks>
public sealed class TenantCustomerWalletRecoveryWorker(UserDbContextFactory users, IServiceScopeFactory scopes,
    ILogger<TenantCustomerWalletRecoveryWorker> logger) : BackgroundService
{
    /// <summary>Periodically scans bounded pages, fairly advancing past unpaid or unresolved orders.</summary>
    /// <param name="stoppingToken">Host lifetime cancellation.</param>
    /// <returns>The tracked worker lifetime.</returns>
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
                    var cutoff = DateTime.UtcNow.AddMinutes(-1);
                    ids = await db.TenantBotOrders.AsNoTracking().Where(x => x.PaymentProvider == "wallet" && !x.IsFulfilled
                        && x.CustomerWalletState != "refunded" && x.CreatedAtUtc < cutoff && x.Id > after)
                        .OrderBy(x => x.Id).Select(x => x.Id).Take(50).ToListAsync(stoppingToken);
                }
                after = ids.Count == 0 ? 0 : ids[^1];
                foreach (var id in ids)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<TenantBotService>().RecoverCustomerWalletOrderAsync(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning("Customer wallet order recovery pending. OrderId={OrderId} ErrorType={ErrorType}", id, ex.GetType().Name); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Customer wallet recovery scan failed. ErrorType={ErrorType}", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
