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

    /// <summary>Checks whether this exact storefront still has a valid identity-bound super-admin wallet grant.</summary>
    /// <param name="store">Detached persisted storefront snapshot, nullable.</param>
    /// <returns>True only when platform approval exists and still matches the current BotFather and owner identities.</returns>
    /// <remarks>This does not imply the owner opted in or that the storefront itself is currently enabled.</remarks>
    public static bool HasValidGrant(BotInstance store) => store is { Type: BotInstanceTypes.Tenant,
        TenantCustomerWalletEnabled: true, TelegramBotId: > 0, OwnerTelegramUserId: > 0,
        TenantCustomerWalletApprovedAtUtc: not null, TenantCustomerWalletApprovedByTelegramUserId: > 0 }
        && store.TelegramBotId == store.TenantCustomerWalletApprovedBotId
        && store.OwnerTelegramUserId == store.TenantCustomerWalletApprovedOwnerId;

    /// <summary>Checks the final customer-wallet admission state for this exact storefront.</summary>
    /// <param name="store">Detached persisted storefront snapshot, nullable.</param>
    /// <returns>True only when the store is enabled, the grant is valid, and the owner explicitly opted in.</returns>
    /// <remarks>All new wallet UI, top-up, purchase and renewal admission must use this final gate.</remarks>
    public static bool IsApproved(BotInstance store)
        => store is { Enabled: true, TenantCustomerWalletOwnerEnabled: true } && HasValidGrant(store);

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

    /// <summary>Grants or revokes platform trust after authenticating the configured super administrator and validating the displayed identity.</summary>
    /// <param name="actor">Telegram callback sender id, never an id trusted from callback data.</param>
    /// <param name="botId">Internal storefront id selected in the administrator panel.</param>
    /// <param name="expectedBotId">Numeric BotFather identity displayed in the confirmation.</param>
    /// <param name="expectedOwnerId">Owner Telegram identity displayed in the confirmation.</param>
    /// <param name="enabled">True grants owner eligibility but leaves owner activation off; false revokes all evidence and owner activation.</param>
    /// <param name="token">Cancellation of the short SQLite transaction.</param>
    /// <param name="expectedRevision">Optional persisted storefront update ticks displayed by the confirmation panel.</param>
    /// <returns>The detached updated storefront, safe for administrator display after HTML escaping.</returns>
    /// <exception cref="UnauthorizedAccessException">The actor is not a configured super administrator.</exception>
    /// <exception cref="InvalidOperationException">The store or identity no longer matches the confirmation.</exception>
    /// <remarks>A fresh grant never activates customer wallet by itself. The exact storefront owner must opt in separately.</remarks>
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
        _logger.LogWarning("Tenant customer wallet grant changed. BotId={BotId} OwnerId={OwnerId} ActorId={ActorId} Granted={Granted}",
            botId, expectedOwnerId, actor, enabled);
        return result;
    }

    /// <summary>Applies the tenant owner's explicit wallet opt-in without granting platform trust.</summary>
    /// <param name="actorOwnerId">Authenticated Telegram callback sender; must equal the persisted storefront owner.</param>
    /// <param name="botId">Internal tenant storefront id selected by the owner's management panel.</param>
    /// <param name="enabled">Desired owner preference. Enabling requires a still-valid super-admin grant; disabling is always allowed.</param>
    /// <param name="token">Cancellation of the short users.db transaction.</param>
    /// <param name="expectedRevision">Optional storefront update ticks rendered into the owner panel.</param>
    /// <returns>The detached updated storefront.</returns>
    /// <exception cref="UnauthorizedAccessException">The callback sender is not the current persisted owner.</exception>
    /// <exception cref="InvalidOperationException">The panel is stale or no valid identity-bound super-admin grant exists.</exception>
    /// <remarks>This method can never create approval metadata. It only changes the owner's opt-in bit.</remarks>
    public async Task<BotInstance> SetOwnerEnabledAsync(long actorOwnerId, string botId, bool enabled,
        CancellationToken token = default, long? expectedRevision = null)
    {
        if (actorOwnerId <= 0) throw new UnauthorizedAccessException();
        var result = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.BotInstances.SingleOrDefaultAsync(x => x.Id == botId, ct);
            if (row?.Type != BotInstanceTypes.Tenant || row.OwnerTelegramUserId != actorOwnerId)
                throw new UnauthorizedAccessException();
            if (expectedRevision.HasValue && (row.UpdatedAtUtc ?? row.CreatedAtUtc).Ticks != expectedRevision.Value)
                throw new InvalidOperationException("Storefront panel changed; reopen the owner panel.");
            if (enabled && !HasValidGrant(row))
                throw new InvalidOperationException("A valid super-admin customer-wallet grant is required.");

            row.TenantCustomerWalletOwnerEnabled = enabled;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return row;
        }, token);

        _logger.LogWarning("Tenant customer wallet owner preference changed. BotId={BotId} OwnerId={OwnerId} OwnerEnabled={OwnerEnabled}",
            botId, actorOwnerId, enabled);
        return result;
    }

    /// <summary>Clears all trust evidence when a storefront is reset, replaced, reassigned or explicitly revoked.</summary>
    /// <param name="store">Required mutable storefront loaded by the caller's write operation.</param>
    /// <remarks>The caller must persist these fields together with the identity change. Historical financial records remain intact.</remarks>
    public static void Revoke(BotInstance store)
    {
        store.TenantCustomerWalletEnabled = false;
        store.TenantCustomerWalletOwnerEnabled = false;
        store.TenantCustomerWalletApprovedAtUtc = null;
        store.TenantCustomerWalletApprovedByTelegramUserId = null;
        store.TenantCustomerWalletApprovedBotId = null;
        store.TenantCustomerWalletApprovedOwnerId = null;
    }
}
