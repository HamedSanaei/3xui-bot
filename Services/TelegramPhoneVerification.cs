using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Applies the same sender-owned Iranian mobile verification to owned and tenant bots.</summary>
internal static class TelegramPhoneVerification
{
    /// <summary>Validates a contact and sends rejection feedback through the receiving bot.</summary>
    /// <param name="botClient">Client of the bot receiving the contact.</param>
    /// <param name="message">Required Telegram contact message; contact user id must equal its sender id.</param>
    /// <param name="supportHtml">Optional HTML-escaped support link from this bot's configuration.</param>
    /// <param name="mainKeyboard">This bot's menu to restore after an invalid mobile number.</param>
    /// <param name="cancellationToken">Cancellation for Telegram feedback.</param>
    /// <returns>Canonical Iranian mobile number, or null after rejection; never log the returned phone.</returns>
    /// <remarks>Does not persist profiles or change trial quotas. Callers save a valid number to the shared profile.</remarks>
    /// <example><code>var phone = await TelegramPhoneVerification.ValidateAsync(client, message, support, menu, token);</code></example>
    internal static async Task<string> ValidateAsync(ITelegramBotClient botClient, Message message,
        string supportHtml, IReplyMarkup mainKeyboard, CancellationToken cancellationToken)
    {
        if (message.From?.Id == null || message.Contact?.UserId == null || message.From.Id != message.Contact.UserId)
        {
            await botClient.SendTextMessageAsync(message.Chat.Id,
                "شماره ارسالی باید متعلق به همین حساب تلگرام باشد. لطفاً دوباره از دکمه «ارسال شماره تلفن» استفاده کنید.",
                replyMarkup: new ReplyKeyboardMarkup(new[]
                {
                    new[] { KeyboardButton.WithRequestContact("ارسال شماره تلفن") },
                    new KeyboardButton[] { "لغو" }
                }) { ResizeKeyboard = true, OneTimeKeyboard = true }, cancellationToken: cancellationToken);
            return null;
        }

        if (IranianPhoneNumberNormalizer.TryNormalize(message.Contact.PhoneNumber, out var phone))
            return phone;

        var supportText = string.IsNullOrWhiteSpace(supportHtml)
            ? "پشتیبانی این ربات هنوز تنظیم نشده است."
            : $"برای بررسی دستی به پشتیبانی همین ربات پیام بدهید: {supportHtml}";
        await botClient.SendTextMessageAsync(message.Chat.Id,
            "شماره‌های غیرایرانی به‌صورت خودکار تأیید نمی‌شوند. فقط شماره موبایل ایران قابل تأیید خودکار است.\n\n" + supportText,
            parseMode: ParseMode.Html, replyMarkup: mainKeyboard, cancellationToken: cancellationToken);
        return null;
    }
}
