using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>
/// Sends operational notifications to every owned bot where a Telegram user has existing bot-scoped state.
/// </summary>
/// <remarks>
/// This service is used for super-admin actions that affect a shared credentials user, such as manual wallet
/// adjustments or account operations. Tenant bots are intentionally excluded so operational messages do not leak
/// into partner storefronts.
/// </remarks>
public class OwnedBotNotificationService
{
    private readonly UserDbContextFactory _contextFactory;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly ILogger<OwnedBotNotificationService> _logger;

    /// <summary>
    /// Creates the owned-bot notification service.
    /// </summary>
    /// <param name="userDbContext">Factory for detached users.db audience reads; no live context survives into delivery.</param>
    /// <param name="botRegistry">Runtime registry containing owned, tenant, and assistant bot definitions.</param>
    /// <param name="botClientProvider">Provider used to send Telegram messages through each resolved owned bot.</param>
    /// <param name="logger">Logger used for best-effort delivery failures.</param>
    /// <remarks>Audience discovery disposes its read context before any Telegram delivery; one bot failure does not change the other recipients.</remarks>
    public OwnedBotNotificationService(
        UserDbContextFactory userDbContext,
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        ILogger<OwnedBotNotificationService> logger)
    {
        _contextFactory = userDbContext;
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sends a best-effort Telegram notification to a user through all relevant owned bots.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram user id of the shared credentials user who should be notified.</param>
    /// <param name="text">Notification text to send; callers must ensure it is safe for the selected parse mode.</param>
    /// <param name="parseMode">Optional Telegram parse mode; null sends plain text and avoids entity parsing errors.</param>
    /// <param name="cancellationToken">Cancellation token for users.db lookup and Telegram sends.</param>
    /// <remarks>
    /// Delivery is best-effort. If one owned bot is blocked, not started, or cannot send to the user, the error is logged
    /// and the service continues with the remaining owned bots.
    /// </remarks>
    /// <returns>A task completing after all relevant owned bots have been attempted; delivery failures are logged individually.</returns>
    public async Task NotifyUserAcrossOwnedBotsAsync(
        long telegramUserId,
        string text,
        ParseMode? parseMode = null,
        CancellationToken cancellationToken = default)
    {
        var ownedBotIds = _botRegistry.Bots
            .Where(x => string.Equals(x.Type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> botIds;
        await using (var context = _contextFactory.CreateDbContext())
            botIds = await context.BotUserStates.AsNoTracking()
                .Where(x => x.TelegramUserId == telegramUserId && ownedBotIds.Contains(x.BotId))
                .Select(x => x.BotId).Distinct().ToListAsync(cancellationToken);

        if (botIds.Count == 0 && _botRegistry.DefaultBot != null)
            botIds.Add(_botRegistry.DefaultBot.Id);

        foreach (var botId in botIds)
        {
            try
            {
                var client = _botClientProvider.GetClient(botId);
                await client.SendTextMessageAsync(
                    chatId: new ChatId(telegramUserId),
                    text: text,
                    parseMode: parseMode,
                    cancellationToken: cancellationToken);
            }
            catch (ApiRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Owned bot notification skipped. botId={BotId}, user={TelegramUserId}, telegramErrorCode={ErrorCode}, message={Message}",
                    botId,
                    telegramUserId,
                    ex.ErrorCode,
                    ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Owned bot notification failed. botId={BotId}, user={TelegramUserId}",
                    botId,
                    telegramUserId);
            }
        }
    }
}
