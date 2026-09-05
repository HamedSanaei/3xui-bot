using Telegram.Bot.Types;

/// <summary>Separates one bot's user conversation from every other bot and user.</summary>
/// <param name="BotId">Required internal runtime bot id.</param>
/// <param name="TelegramUserId">Telegram actor id; zero denotes the separate bot fallback lane.</param>
public readonly record struct TelegramUpdateExecutionKey(string BotId, long TelegramUserId);

/// <summary>Owns the input of one scheduled handler; contains no stored client or token.</summary>
/// <param name="Sequence">Internal durable acceptance sequence.</param>
/// <param name="Key">Bot/user serialization identity.</param>
/// <param name="Update">Private Telegram update; never log this object.</param>
/// <param name="AcceptedAtUtc">UTC time when the update was durably accepted.</param>
public sealed record TelegramUpdateWorkItem(long Sequence, TelegramUpdateExecutionKey Key, Update Update, DateTime AcceptedAtUtc);

/// <summary>Determines stable actor identity for the Telegram.Bot version used by the application.</summary>
public static class TelegramUpdateIdentity
{
    /// <summary>Resolves the sender rather than the callback message's bot author or a group chat id.</summary>
    /// <param name="update">Required incoming Telegram update.</param>
    /// <returns>Actor Telegram user id, or zero when no reliable user is present.</returns>
    /// <remarks>Anonymous channel posts and poll totals intentionally use the fallback lane.</remarks>
    public static long ResolveUserId(Update update) =>
        update.CallbackQuery?.From?.Id ?? update.InlineQuery?.From?.Id ?? update.ChosenInlineResult?.From?.Id
        ?? update.ShippingQuery?.From?.Id ?? update.PreCheckoutQuery?.From?.Id ?? update.PollAnswer?.User?.Id
        ?? update.MyChatMember?.From?.Id ?? update.ChatMember?.From?.Id ?? update.ChatJoinRequest?.From?.Id
        ?? ResolveMessageActor(update.Message) ?? ResolveMessageActor(update.EditedMessage) ?? 0;

    /// <summary>Rejects the placeholder sender used for anonymous or channel-authored messages.</summary>
    /// <param name="message">Nullable received or edited message.</param>
    /// <returns>A reliable Telegram sender id, or null so the bot-level fallback lane is selected.</returns>
    /// <remarks>SenderChat identifies a chat, not the person responsible for the update.</remarks>
    private static long? ResolveMessageActor(Message message) => message?.SenderChat == null ? message?.From?.Id : null;
}
