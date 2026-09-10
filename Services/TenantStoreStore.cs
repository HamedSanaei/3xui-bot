using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Allocates independent storefronts while keeping financial ownership at the colleague level.</summary>
/// <remarks>Only short users.db transactions are held. No Telegram, wallet or website account is created here.</remarks>
public sealed class TenantStoreStore
{
    private readonly UserDbContextFactory _factory;
    private readonly int _limit;

    /// <summary>Creates the storefront allocator.</summary>
    /// <param name="factory">Factory for short-lived users.db contexts.</param>
    /// <param name="configuration">Global configuration containing the positive storefront limit.</param>
    public TenantStoreStore(UserDbContextFactory factory, IConfiguration configuration)
    {
        _factory = factory;
        var options = configuration.Get<AppConfig>() ?? new AppConfig();
        ValidateConfiguration(options);
        _limit = options.TenantMaxStoresPerOwner;
    }

    /// <summary>Rejects invalid storefront limits before receivers start.</summary>
    /// <param name="options">Required global application options.</param>
    /// <exception cref="ArgumentOutOfRangeException">The configured store count is not positive.</exception>
    public static void ValidateConfiguration(AppConfig options)
    {
        if (options.TenantMaxStoresPerOwner <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.TenantMaxStoresPerOwner));
    }

    /// <summary>Lists every store belonging to the authenticated colleague, including disabled drafts.</summary>
    /// <param name="ownerId">Authenticated colleague's Telegram user id, never the customer's id.</param>
    /// <param name="token">Cancellation of the database read.</param>
    /// <returns>Detached stores in stable number order; empty before the first allocation. Tokens must not be logged.</returns>
    public async Task<List<BotInstance>> ListAsync(long ownerId, CancellationToken token = default)
    {
        await using var db = _factory.CreateDbContext();
        return await db.BotInstances.AsNoTracking().Where(x => x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == ownerId)
            .OrderBy(x => x.TenantStoreNumber).ToListAsync(token);
    }

    /// <summary>Allocates one disabled store atomically with its per-owner number and limit check.</summary>
    /// <param name="ownerId">Positive authenticated colleague Telegram user id.</param>
    /// <param name="creationKey">Random add-button identity retained across redelivery; must not contain secrets.</param>
    /// <param name="token">Cancellation of local database work and bounded BUSY retries.</param>
    /// <returns>Detached new or previously allocated store, or null when the owner is at the configured limit.</returns>
    /// <remarks>All rows count, even after reset. Reusing a button returns its original store. Existing ids and wallets are untouched.</remarks>
    /// <exception cref="ArgumentException">The owner or creation key is invalid.</exception>
    /// <example><code>var store = await stores.CreateAsync(owner.TelegramUserId, Guid.NewGuid().ToString("N"), token);</code></example>
    public Task<BotInstance> CreateAsync(long ownerId, string creationKey, CancellationToken token = default)
    {
        if (ownerId <= 0 || !Guid.TryParseExact(creationKey, "N", out _)) throw new ArgumentException("Invalid storefront allocation identity.");
        var ownerNotificationBotId = GetCurrentOwnedBotId();
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            // SQLite's immediate write transaction serializes allocation across owned bots and connections.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var stores = await db.BotInstances.Where(x => x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == ownerId).ToListAsync(ct);
            var existing = stores.SingleOrDefault(x => x.TenantCreationKey == creationKey);
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.TenantOwnerNotificationBotId) && !string.IsNullOrWhiteSpace(ownerNotificationBotId))
                {
                    existing.TenantOwnerNotificationBotId = ownerNotificationBotId;
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                return existing;
            }
            if (stores.Count >= _limit) return null;
            var number = checked(stores.Select(x => x.TenantStoreNumber ?? 0).DefaultIfEmpty().Max() + 1);
            var store = new BotInstance
            {
                Id = $"tenant-{ownerId}-{number}", OwnerTelegramUserId = ownerId, TenantStoreNumber = number,
                TenantCreationKey = creationKey, Type = BotInstanceTypes.Tenant, Enabled = false,
                TenantOwnerNotificationBotId = ownerNotificationBotId,
                BrandName = $"فروشگاه {number}", CreatedAtUtc = DateTime.UtcNow
            };
            db.BotInstances.Add(store);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return store;
        }, token);
    }

    public async Task<bool> EstablishOwnerNotificationRouteFromCurrentOwnedBotAsync(
        string tenantBotId, long ownerId, CancellationToken token = default)
    {
        var ownedBotId = GetCurrentOwnedBotId();
        if (string.IsNullOrWhiteSpace(tenantBotId) || ownerId <= 0 || string.IsNullOrWhiteSpace(ownedBotId))
            return false;
        await using var db = _factory.CreateDbContext();
        var updated = await db.BotInstances
            .Where(x => x.Id == tenantBotId && x.Type == BotInstanceTypes.Tenant &&
                        x.OwnerTelegramUserId == ownerId && x.TenantOwnerNotificationBotId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.TenantOwnerNotificationBotId, ownedBotId), token);
        return updated == 1;
    }

    private static string GetCurrentOwnedBotId() =>
        string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(BotContextAccessor.CurrentBotId)
            ? BotContextAccessor.CurrentBotId
            : null;
}
