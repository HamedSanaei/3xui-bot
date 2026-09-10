using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

public sealed record TenantOwnerNotificationTransportResolution(
    BotInstanceConfig Bot,
    string ResolutionSource)
{
    public bool IsAvailable => Bot != null;
}

public sealed class OwnerNotificationTransportUnavailableException : InvalidOperationException
{
    public const string SafeReason = "owner_notification_transport_unavailable";

    public OwnerNotificationTransportUnavailableException()
        : base(SafeReason) { }
}

public sealed class TenantOwnerNotificationTransportResolver
{
    private readonly BotRegistry _botRegistry;
    private readonly UserDbContextFactory _factory;
    private readonly BotClientProvider _clients;

    public TenantOwnerNotificationTransportResolver(
        BotRegistry botRegistry,
        UserDbContextFactory factory,
        BotClientProvider clients)
    {
        _botRegistry = botRegistry;
        _factory = factory;
        _clients = clients;
    }

    public async Task<TenantOwnerNotificationTransportResolution> ResolveAsync(
        BotInstance tenant,
        long ownerTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        if (tenant == null ||
            !string.Equals(tenant.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase) ||
            tenant.OwnerTelegramUserId != ownerTelegramUserId || ownerTelegramUserId <= 0)
            return new TenantOwnerNotificationTransportResolution(null, "unavailable");

        var validOwnedBots = _botRegistry.Bots
            .Where(IsValidOwnedBot)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(tenant.TenantOwnerNotificationBotId) &&
            validOwnedBots.TryGetValue(tenant.TenantOwnerNotificationBotId, out var explicitBot))
            return new TenantOwnerNotificationTransportResolution(explicitBot, "explicit");

        if (validOwnedBots.Count == 0)
            return new TenantOwnerNotificationTransportResolution(null, "unavailable");

        var validIds = validOwnedBots.Keys.ToArray();
        await using var db = _factory.CreateDbContext();
        var state = await db.BotUserStates.AsNoTracking()
            .Where(x => x.TelegramUserId == ownerTelegramUserId && validIds.Contains(x.BotId))
            .OrderByDescending(x => x.UpdatedAtUtc ?? DateTime.MinValue)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.BotId)
            .FirstOrDefaultAsync(cancellationToken);

        return state != null && validOwnedBots.TryGetValue(state.BotId, out var historicalBot)
            ? new TenantOwnerNotificationTransportResolution(historicalBot, "historical_bot_user_state")
            : new TenantOwnerNotificationTransportResolution(null, "unavailable");
    }

    public async Task<(ITelegramBotClient Client, TenantOwnerNotificationTransportResolution Resolution)> ResolveClientAsync(
        BotInstance tenant,
        long ownerTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(tenant, ownerTelegramUserId, cancellationToken);
        if (!resolution.IsAvailable)
            throw new OwnerNotificationTransportUnavailableException();
        return (_clients.GetClient(resolution.Bot.Id), resolution);
    }

    private static bool IsValidOwnedBot(BotInstanceConfig bot) =>
        bot != null &&
        string.Equals(bot.Type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) &&
        bot.Enabled &&
        !string.IsNullOrWhiteSpace(bot.Token) &&
        !string.IsNullOrWhiteSpace(bot.Id);
}
