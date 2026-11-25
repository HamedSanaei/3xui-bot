using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Resumes revocation of courtesy accounts after a tenant receipt rejection, including after restart.</summary>
/// <param name="factory">Singleton factory for short users.db reads of durable orders.</param>
/// <param name="scopes">Scope factory for a fresh tenant workflow per attempted cleanup.</param>
/// <param name="logger">Operational logger. No panel client credentials or receipt images are included.</param>
/// <remarks>Only cleanup is attempted. The worker never issues a create, confirms a receipt or performs wallet settlement.</remarks>
public sealed class TenantRejectedCourtesyWorker(
    UserDbContextFactory factory, IServiceScopeFactory scopes, ILogger<TenantRejectedCourtesyWorker> logger) : BackgroundService
{
    /// <summary>Polls durable revocation orders and resumes a bounded batch at each interval.</summary>
    /// <param name="stoppingToken">Cancellation from the host during shutdown.</param>
    /// <returns>A task that runs until host shutdown.</returns>
    /// <remarks>Oldest pending work goes first. The existing XUI revoke protocol reads back identity and outcome.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var db = factory.CreateDbContext();
                var cutoff = DateTime.UtcNow.AddSeconds(-30);
                var orders = await db.TenantBotOrders.AsNoTracking()
                    .Where(x => x.ProvisionalDeliveryState == TenantCardProvisionalStates.Revoking &&
                        !x.IsFulfilled && x.PaidAtUtc == null && x.PaymentProvider == "tenant_card" &&
                        x.PaymentStatus != TenantBotOrderStatuses.ReceiptApproved && x.UpdatedAtUtc <= cutoff)
                    .OrderBy(x => x.UpdatedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).Take(12)
                    .ToListAsync(stoppingToken);

                foreach (var orderId in orders)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<TenantBotService>()
                        .REVOKETENANTCARDPROVISIONALASYNC(orderId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Rejected courtesy cleanup batch failed. ErrorType={ErrorType}", exception.GetType().Name);
            }
        }
    }
}
