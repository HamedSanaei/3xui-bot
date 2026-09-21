using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Fail-closed, persisted trust boundary for exposing the global customer wallet through a third-party storefront.</summary>
/// <remarks>This policy gates admission only. Paid invoice settlement and durable debit recovery must not call RequireAsync.</remarks>
public sealed class TenantCustomerWalletPolicy
{
    private readonly UserDbContextFactory _users;
    private readonly AppConfig _config;
    private readonly ILogger<TenantCustomerWalletPolicy> _logger;

    /// <summary>Creates the policy without retaining a context or caching approval.</summary>
    /// <param name="users">Factory for users.db storefront permissions.</param>
    /// <param name="config">Platform configuration containing the authorized super-admin Telegram ids.</param>
    /// <param name="logger">Security audit logger; never receives credentials or customer financial details.</param>
    public TenantCustomerWalletPolicy(UserDbContextFactory users, AppConfig config, ILogger<TenantCustomerWalletPolicy> logger)
    { _users = users; _config = config; _logger = logger; }

    /// <summary>Checks permission against the exact current storefront identity.</summary>
    /// <param name="store">Detached persisted storefront snapshot, nullable.</param>
    /// <returns>True only for an enabled tenant with complete approval evidence matching both bot and owner.</returns>
    /// <remarks>Use a fresh database read for admission; runtime configuration and callback payloads are not authority.</remarks>
    public static bool IsApproved(BotInstance store) => store is { Enabled: true, Type: BotInstanceTypes.Tenant,
        TenantCustomerWalletEnabled: true, TelegramBotId: > 0, OwnerTelegramUserId: > 0,
        TenantCustomerWalletApprovedAtUtc: not null, TenantCustomerWalletApprovedByTelegramUserId: > 0 }
        && store.TelegramBotId == store.TenantCustomerWalletApprovedBotId
        && store.OwnerTelegramUserId == store.TenantCustomerWalletApprovedOwnerId;

    /// <summary>Reads the current permission, rejecting disabled, replaced, unapproved or absent stores.</summary>
    /// <param name="botId">Internal tenant BotInstance.Id, never a username or customer-supplied owner id.</param>
    /// <param name="token">Cancellation of the local database read.</param>
    /// <returns>A detached approved storefront; never null.</returns>
    /// <exception cref="InvalidOperationException">The storefront cannot admit new wallet work.</exception>
    /// <remarks>No balance, order or invoice is changed. Recheck immediately before admitting financial work.</remarks>
    /// <example><code>var store = await policy.RequireAsync(BotContextAccessor.CurrentBotId, token);</code></example>
    public async Task<BotInstance> RequireAsync(string botId, CancellationToken token = default)
    {
        await using var db = _users.CreateDbContext();
        var store = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == botId, token);
        if (!IsApproved(store)) throw new InvalidOperationException("Customer wallet is not approved for this storefront.");
        return store;
    }

    /// <summary>Changes trust only after authenticating the configured super administrator and validating the displayed identity.</summary>
    /// <param name="actor">Telegram callback sender id, never an id trusted from callback data.</param>
    /// <param name="botId">Internal storefront id selected in the administrator panel.</param>
    /// <param name="expectedBotId">Numeric BotFather identity displayed in the confirmation.</param>
    /// <param name="expectedOwnerId">Owner Telegram identity displayed in the confirmation.</param>
    /// <param name="enabled">True only after explicit confirmation; false revokes all evidence.</param>
    /// <param name="token">Cancellation of the short SQLite transaction.</param>
    /// <param name="expectedRevision">Optional persisted storefront update ticks displayed by the confirmation panel.</param>
    /// <returns>The detached updated storefront, safe for administrator display after HTML escaping.</returns>
    /// <exception cref="UnauthorizedAccessException">The actor is not a configured super administrator.</exception>
    /// <exception cref="InvalidOperationException">The store or identity no longer matches the confirmation.</exception>
    /// <remarks>Only permission metadata changes. No customer balance is owned or controlled by the storefront.</remarks>
    public async Task<BotInstance> SetAsync(long actor, string botId, long expectedBotId, long expectedOwnerId,
        bool enabled, CancellationToken token = default, long? expectedRevision = null)
    {
        if (actor <= 0 || _config.AdminsUserIds?.Contains(actor) != true) throw new UnauthorizedAccessException();
        var result = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.BotInstances.SingleOrDefaultAsync(x => x.Id == botId, ct);
            if (row?.Type != BotInstanceTypes.Tenant || row.TelegramBotId != expectedBotId || expectedBotId <= 0
                || row.OwnerTelegramUserId != expectedOwnerId || expectedOwnerId <= 0)
                throw new InvalidOperationException("Storefront identity changed; reopen the panel.");
            if (expectedRevision.HasValue && (row.UpdatedAtUtc ?? row.CreatedAtUtc).Ticks != expectedRevision.Value)
                throw new InvalidOperationException("Storefront panel changed; reopen the confirmation.");
            Revoke(row);
            if (enabled)
            {
                row.TenantCustomerWalletEnabled = true;
                row.TenantCustomerWalletApprovedAtUtc = DateTime.UtcNow;
                row.TenantCustomerWalletApprovedByTelegramUserId = actor;
                row.TenantCustomerWalletApprovedBotId = expectedBotId;
                row.TenantCustomerWalletApprovedOwnerId = expectedOwnerId;
            }
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return row;
        }, token);
        _logger.LogWarning("Tenant customer wallet approval changed. BotId={BotId} OwnerId={OwnerId} ActorId={ActorId} Enabled={Enabled}",
            botId, expectedOwnerId, actor, enabled);
        return result;
    }

    /// <summary>Clears all trust evidence when a storefront is reset, replaced, reassigned or explicitly revoked.</summary>
    /// <param name="store">Required mutable storefront loaded by the caller's write operation.</param>
    /// <remarks>The caller must persist these fields together with the identity change. Historical financial records remain intact.</remarks>
    public static void Revoke(BotInstance store)
    {
        store.TenantCustomerWalletEnabled = false;
        store.TenantCustomerWalletApprovedAtUtc = null;
        store.TenantCustomerWalletApprovedByTelegramUserId = null;
        store.TenantCustomerWalletApprovedBotId = null;
        store.TenantCustomerWalletApprovedOwnerId = null;
    }
}
