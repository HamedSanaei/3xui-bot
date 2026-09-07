using System.Net;
using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Adds the selected storefront name and addressed callbacks to owner-only panel responses.</summary>
/// <remarks>Execution-scoped decorator; shares the transport without changing its lifetime or customer messages.</remarks>
internal sealed class TenantOwnerPanelClient : ITelegramBotClient
{
    private readonly ITelegramBotClient _inner;
    private readonly Func<BotInstance> _store;
    private readonly long _chatId;

    /// <summary>Wraps the current owned-bot client for one authenticated owner conversation.</summary>
    /// <param name="inner">Existing runtime client; never disposed by this decorator.</param>
    /// <param name="store">Current detached selection, updated after settings saves.</param>
    /// <param name="chatId">Owner panel Telegram chat id; other chats are forwarded untouched.</param>
    public TenantOwnerPanelClient(ITelegramBotClient inner, Func<BotInstance> store, long chatId)
    { _inner = inner; _store = store; _chatId = chatId; }

    /// <inheritdoc />
    public bool LocalBotServer => _inner.LocalBotServer;
    /// <inheritdoc />
    public long? BotId => _inner.BotId;
    /// <inheritdoc />
    public TimeSpan Timeout { get => _inner.Timeout; set => _inner.Timeout = value; }
    /// <inheritdoc />
    public IExceptionParser ExceptionsParser { get => _inner.ExceptionsParser; set => _inner.ExceptionsParser = value; }
    /// <inheritdoc />
    public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest { add => _inner.OnMakingApiRequest += value; remove => _inner.OnMakingApiRequest -= value; }
    /// <inheritdoc />
    public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived { add => _inner.OnApiResponseReceived += value; remove => _inner.OnApiResponseReceived -= value; }
    /// <inheritdoc />
    public Task<bool> TestApiAsync(CancellationToken cancellationToken = default) => _inner.TestApiAsync(cancellationToken);
    /// <inheritdoc />
    public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default) => _inner.DownloadFileAsync(filePath, destination, cancellationToken);

    /// <summary>Addresses owner keyboards and prefixes owner text with the selected store name before sending.</summary>
    /// <typeparam name="TResponse">Telegram API response type.</typeparam>
    /// <param name="request">Outgoing request from the owner handler; payloads are never logged.</param>
    /// <param name="cancellationToken">Cancellation of the original Telegram request.</param>
    /// <returns>The unchanged underlying Telegram response.</returns>
    public Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        switch (request)
        {
            case SendMessageRequest send when send.ChatId.Identifier == _chatId:
                Address(send.ReplyMarkup as InlineKeyboardMarkup);
                request = (IRequest<TResponse>)(object)new SendMessageRequest(send.ChatId, Prefix(send.Text, send.ParseMode))
                {
                    ParseMode = send.ParseMode, ReplyMarkup = send.ReplyMarkup, MessageThreadId = send.MessageThreadId,
                    DisableWebPagePreview = send.DisableWebPagePreview, DisableNotification = send.DisableNotification,
                    ProtectContent = send.ProtectContent, ReplyToMessageId = send.ReplyToMessageId,
                    AllowSendingWithoutReply = send.AllowSendingWithoutReply, IsWebhookResponse = send.IsWebhookResponse,
                    Entities = ShiftEntities(send.Entities, Prefix("", send.ParseMode).Length)
                };
                break;
            case EditMessageTextRequest edit when edit.ChatId?.Identifier == _chatId:
                Address(edit.ReplyMarkup);
                request = (IRequest<TResponse>)(object)new EditMessageTextRequest(edit.ChatId, edit.MessageId, Prefix(edit.Text, edit.ParseMode))
                {
                    ParseMode = edit.ParseMode, ReplyMarkup = edit.ReplyMarkup, DisableWebPagePreview = edit.DisableWebPagePreview,
                    IsWebhookResponse = edit.IsWebhookResponse, Entities = ShiftEntities(edit.Entities, Prefix("", edit.ParseMode).Length)
                };
                break;
        }
        return _inner.MakeRequestAsync(request, cancellationToken);
    }

    /// <summary>Labels a response using the store's current name and stable number.</summary>
    /// <param name="text">Original user-visible message.</param>
    /// <param name="mode">Telegram markup mode; HTML names are escaped.</param>
    /// <returns>Message with a plain or HTML-safe storefront heading.</returns>
    private string Prefix(string text, ParseMode? mode)
    {
        var store = _store();
        var title = $"🏪 {store.BrandName ?? "فروشگاه"} · {store.TenantStoreNumber}";
        return (mode == ParseMode.Html ? WebUtility.HtmlEncode(title) : title) + "\n\n" + text;
    }

    /// <summary>Copies explicit Telegram entities with UTF-16 offsets adjusted for the storefront heading.</summary>
    /// <param name="entities">Optional entities belonging to the original text.</param>
    /// <param name="offset">Heading length in UTF-16 code units.</param>
    /// <returns>Detached adjusted entities, or null when the request uses parse mode.</returns>
    private static IEnumerable<Telegram.Bot.Types.MessageEntity> ShiftEntities(IEnumerable<Telegram.Bot.Types.MessageEntity> entities, int offset)
        => entities?.Select(x => new Telegram.Bot.Types.MessageEntity
        { Type = x.Type, Offset = x.Offset + offset, Length = x.Length, Url = x.Url, User = x.User, Language = x.Language, CustomEmojiId = x.CustomEmojiId }).ToArray();

    /// <summary>Converts only internal owner buttons to store-addressed callbacks.</summary>
    /// <param name="keyboard">Optional owner keyboard; customer and URL buttons are preserved.</param>
    private void Address(InlineKeyboardMarkup keyboard)
    {
        if (keyboard == null) return;
        foreach (var button in keyboard.InlineKeyboard.SelectMany(x => x))
            if (button.CallbackData?.StartsWith("TBM:", StringComparison.Ordinal) == true && button.CallbackData != "TBM:list"
                && !TenantOwnerCallback.TryDecode(button.CallbackData, out _, out _, out _))
                button.CallbackData = TenantOwnerCallback.Encode(_store(), button.CallbackData);
    }
}
