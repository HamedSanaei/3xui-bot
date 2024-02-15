using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public static class TelegramBotClientExtensions
{
    public static async Task CustomSendTextMessageAsync(
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
        IReplyMarkup replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await botClient.SendTextMessageAsync(
                chatId,
                text,
                messageThreadId,
                parseMode,
                entities,
                disableWebPagePreview,
                disableNotification,
                protectContent,
                replyToMessageId,
                allowSendingWithoutReply,
                replyMarkup,
                cancellationToken);
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
    }




    public static async Task CustomForwardMessage(
            this ITelegramBotClient botClient,
            long chatId,
            string fromChatId,
            int messageId)
    {

        try
        {
            await botClient.ForwardMessageAsync(
                chatId: chatId,
                fromChatId: $"@{fromChatId}",
                messageId: messageId
            );
            // Console.WriteLine("Message forwarded successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while forwarding the message: {ex.Message}");
        }
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
            await botClient.SendMediaGroupAsync(chatId, mediaGroup);
        }
        catch (System.Exception ex)
        {

            Console.WriteLine(ex.Message);
        }
        // Send the media group
    }
}
