using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Application message helpers preserving the existing delivery and formatting policies.</summary>
public static class TelegramBotClientExtensions
{
    /// <summary>Sends a message using legacy application defaults through the v22 Telegram API.</summary>
    /// <param name="botClient">Resolved bot client; its runtime owns its lifetime.</param>
    /// <param name="chatId">Destination Telegram chat id.</param>
    /// <param name="text">Required message text; preserve its selected markup.</param>
    /// <param name="messageThreadId">Optional forum topic id.</param>
    /// <param name="parseMode">Markup mode; defaults to the existing Markdown behavior.</param>
    /// <param name="entities">Optional explicit Telegram text entities.</param>
    /// <param name="disableWebPagePreview">Whether link previews are suppressed.</param>
    /// <param name="disableNotification">Whether delivery is silent.</param>
    /// <param name="protectContent">Whether Telegram prevents forwarding/saving the content.</param>
    /// <param name="replyToMessageId">Message being replied to; zero means no reply.</param>
    /// <param name="allowSendingWithoutReply">Whether a missing reply target permits delivery.</param>
    /// <param name="replyMarkup">Optional reply or inline keyboard.</param>
    /// <param name="cancellationToken">Cancellation forwarded to the Telegram request.</param>
    /// <returns>The accepted Telegram message or null after a caught delivery failure.</returns>
    /// <remarks>Maps legacy preview and reply parameters to v22 options. Existing exception swallowing is unchanged.</remarks>
    public static async Task<Message> CustomSendTextMessageAsync(
        this ITelegramBotClient botClient,
        ChatId chatId,
        string text,
        int? messageThreadId = null,
        ParseMode parseMode = ParseMode.Markdown, // Default value, adjust if needed
        IEnumerable<MessageEntity> entities = null,
        bool disableWebPagePreview = false,
        bool disableNotification = false,
        bool protectContent = false,
        int replyToMessageId = 0,
        bool allowSendingWithoutReply = false,
        ReplyMarkup replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        Message result = null;
        try
        {
            result = await botClient.SendMessage(
                chatId,
                text,
                messageThreadId: messageThreadId,
                parseMode: parseMode,
                entities: entities,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = disableWebPagePreview },
                disableNotification: disableNotification,
                protectContent: protectContent,
                replyParameters: replyToMessageId == 0 ? null : new ReplyParameters
                { MessageId = replyToMessageId, AllowSendingWithoutReply = allowSendingWithoutReply },
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {

            // Handle the case when the bot is blocked by the user
            Console.WriteLine("The bot has been blocked by the user.");
        }
        catch (Exception ex)
        {
            // Log other exceptions
            Console.WriteLine($"An error occurred: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Sends one referral dashboard message as plain Telegram text and propagates delivery failures to the caller.
    /// </summary>
    /// <param name="botClient">
    /// Telegram client for the owned bot handling the referral command. The client token is managed by the runtime and
    /// must never be included in <paramref name="text"/> or application logs.
    /// </param>
    /// <param name="chatId">
    /// Telegram chat identifier that requested the referral dashboard. This is normally the incoming private chat id.
    /// </param>
    /// <param name="text">
    /// Complete plain-text referral message, including the <c>?start=ref_...</c> URL. The value is required and is sent
    /// without Markdown or HTML entity parsing.
    /// </param>
    /// <param name="replyMarkup">
    /// Optional Telegram keyboard displayed with the message. Pass the current owned-bot main keyboard for dashboard
    /// responses, or <c>null</c> when no keyboard is required.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that cancels the Telegram request when polling shuts down or the current update is abandoned.
    /// </param>
    /// <returns>
    /// The non-null Telegram <see cref="Message"/> returned after the API accepts the dashboard. Callers may treat this
    /// result as proof of delivery acceptance and must not record success before it is returned.
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="CustomSendTextMessageAsync"/>, this strict sender intentionally does not catch Telegram or
    /// transport exceptions. The referral route needs the real exception and Telegram response text for structured
    /// logging, and the plain-text mode prevents the underscore in <c>ref_...</c> from being parsed as Markdown.
    /// </remarks>
    /// <exception cref="ApiRequestException">
    /// Telegram rejected the request, for example because the chat is unavailable or the payload is invalid.
    /// </exception>
    /// <exception cref="HttpRequestException">The Telegram transport failed before a valid response was received.</exception>
    /// <exception cref="InvalidOperationException">The Telegram client completed without returning a message.</exception>
    /// <example>
    /// <code>
    /// var sent = await botClient.SendReferralTextMessageAsync(
    ///     chatId: message.Chat.Id,
    ///     text: "https://t.me/example_bot?start=ref_abc123",
    ///     replyMarkup: mainKeyboard,
    ///     cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public static async Task<Message> SendReferralTextMessageAsync(
        this ITelegramBotClient botClient,
        ChatId chatId,
        string text,
        ReplyMarkup replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        var result = await botClient.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.None,
            replyMarkup: replyMarkup,
            cancellationToken: cancellationToken);

        return result ?? throw new InvalidOperationException(
            "Telegram accepted the referral send call without returning a message.");
    }




    public static async Task CustomForwardMessage(
            this ITelegramBotClient botClient,
            long chatId,
            string fromChatId,
            int messageId)
    {

        try
        {
            var normalizedFromChatId = NormalizeForwardSourceChatId(fromChatId);
            await botClient.ForwardMessage(
                chatId: chatId,
                fromChatId: normalizedFromChatId,
                messageId: messageId
            );
            // Console.WriteLine("Message forwarded successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while forwarding the message: {ex.Message}");
        }
    }

    private static ChatId NormalizeForwardSourceChatId(string fromChatId)
    {
        var value = (fromChatId ?? string.Empty).Trim();
        if (long.TryParse(value, out var numericChatId))
            return new ChatId(numericChatId);

        if (!value.StartsWith("@", StringComparison.Ordinal))
            value = "@" + value;

        return new ChatId(value);
    }


    public static async Task SendImagesWithCaptionAsync(this ITelegramBotClient botClient, ChatId chatId, List<Stream> imageStreams, string caption)
    {
        // Prepare a list of InputMediaPhoto
        var mediaGroup = new List<IAlbumInputMedia>();

        // Create the first photo with a caption
        var firstImage = new InputMediaPhoto(new InputFileStream(imageStreams[0]))
        {
            Caption = caption,
            ParseMode = ParseMode.Markdown
        };
        mediaGroup.Add(firstImage);

        // Add the rest of the images without captions
        for (int i = 1; i < imageStreams.Count; i++)
        {
            var image = new InputMediaPhoto(new InputFileStream(imageStreams[i]));
            mediaGroup.Add(image);
        }

        try
        {
            await botClient.SendMediaGroup(chatId, mediaGroup);
        }
        catch (System.Exception ex)
        {

            Console.WriteLine(ex.Message);
        }
        // Send the media group
    }
}
