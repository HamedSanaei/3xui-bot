using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Owns the sales bots' phone-verification panel and entry prompt.</summary>
public partial class TelegramBotService
{
    /// <summary>Reply-keyboard action that opens the owned super-admin phone settings.</summary>
    private const string AdminPhoneVerificationAction = "📱 الزام تأیید شماره تلفن";

    /// <summary>Inline callback namespace reserved for the owned phone settings panel.</summary>
    private const string PhoneVerificationCallbackPrefix = "phoneverify:";

    /// <summary>Shows the current verification requirement in the authorized administrator's private chat.</summary>
    /// <param name="client">Telegram transport for the active owned bot.</param>
    /// <param name="message">Original private Telegram message with the administrator's sender id.</param>
    /// <param name="cancellationToken">Cancellation of the Telegram delivery.</param>
    /// <returns>True when the settings action was consumed, including a denied request.</returns>
    /// <remarks>The caller routes only owned-bot messages here. Opening the panel does not write configuration.</remarks>
    /// <exception cref="OperationCanceledException">The Telegram operation is cancelled.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the panel message.</exception>
    private async Task<bool> TryHandlePhoneVerificationSettingsMessageAsync(
        ITelegramBotClient client, Message message, CancellationToken cancellationToken)
    {
        if (message.Text != AdminPhoneVerificationAction)
            return false;
        if (message.Chat.Type != ChatType.Private || !IsSuperAdminUser(message.From.Id))
        {
            await client.SendMessage(message.Chat.Id, "این بخش فقط برای سوپرادمین‌ها در گفت‌وگوی خصوصی در دسترس است.",
                cancellationToken: cancellationToken);
            return true;
        }

        var panel = BuildPhoneVerificationSettingsPanel(_phoneVerification.Snapshot);
        await client.SendMessage(message.Chat.Id, panel.Text, replyMarkup: panel.Markup, cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>Authorizes a phone settings callback, persists its target and refreshes the panel.</summary>
    /// <param name="client">Telegram transport for the current bot.</param>
    /// <param name="callback">Original sender and panel button with the expected revision and target.</param>
    /// <param name="cancellationToken">Cancellation of the persistence or Telegram requests.</param>
    /// <returns>True for this callback namespace even when the request is rejected.</returns>
    /// <remarks>Only the private panel of a configured super-admin in an owned bot can change the shared setting. A failed panel refresh cannot roll back a persisted change.</remarks>
    /// <exception cref="OperationCanceledException">The write or Telegram operation is cancelled.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the panel refresh, except when the panel is already identical.</exception>
    private async Task<bool> TryHandlePhoneVerificationSettingsCallbackAsync(
        ITelegramBotClient client, CallbackQuery callback, CancellationToken cancellationToken)
    {
        if (callback.Data?.StartsWith(PhoneVerificationCallbackPrefix, StringComparison.Ordinal) != true)
            return false;
        if (!string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) ||
            !IsSuperAdminUser(callback.From.Id) ||
            callback.Message is not { Chat.Type: ChatType.Private } panel || panel.Chat.Id != callback.From.Id)
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "این بخش فقط برای سوپرادمین‌هاست.",
                showAlert: true, cancellationToken: cancellationToken);
            return true;
        }

        var parts = callback.Data.Split(':');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var revision) ||
            parts[2] is not ("0" or "1"))
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "این دکمه نامعتبر است. پنل را دوباره باز کنید.",
                showAlert: true, cancellationToken: cancellationToken);
            return true;
        }

        // An explicit target and its revision prevent retries or old panels from reversing a newer choice.
        var applied = await _phoneVerification.SetRequiredAsync(parts[2] == "1", revision, cancellationToken);
        await SafeAnswerCallbackQueryAsync(client, callback.Id,
            applied ? "تنظیم ذخیره شد." : "تغییر انجام نشد. وضعیت فعلی نمایش داده شد.",
            showAlert: !applied, cancellationToken: cancellationToken);

        var current = BuildPhoneVerificationSettingsPanel(_phoneVerification.Snapshot);
        try
        {
            await client.EditMessageText(panel.Chat.Id, panel.MessageId, current.Text,
                replyMarkup: current.Markup, cancellationToken: cancellationToken);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException exception) when (
            TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(exception.ErrorCode, exception.Message))
        {
            // Telegram already shows the current state, for example after a failed write.
        }
        return true;
    }

    /// <summary>Builds the status text and matching revision-bound button from one snapshot.</summary>
    /// <param name="snapshot">Current shared owned-bot preference.</param>
    /// <returns>Persian status text and a keyboard carrying the opposite target and current revision.</returns>
    /// <example><code>var panel = BuildPhoneVerificationSettingsPanel(settings.Snapshot);</code></example>
    private static (string Text, InlineKeyboardMarkup Markup) BuildPhoneVerificationSettingsPanel(PhoneVerificationSnapshot snapshot)
    {
        var status = snapshot.Enabled ? "✅ روشن" : "❌ خاموش";
        var button = snapshot.Enabled ? "خاموش کردن الزام تأیید شماره" : "روشن کردن الزام تأیید شماره";
        var data = $"{PhoneVerificationCallbackPrefix}{snapshot.Revision:N}:{(snapshot.Enabled ? "0" : "1")}";
        return ($"{AdminPhoneVerificationAction}\n\nوضعیت: {status}\n\nاین تنظیم برای ورود و عملیات نیازمند شماره در همهٔ ربات‌های فروش اعمال می‌شود.",
            new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(button, data)));
    }

    /// <summary>Checks whether an owned customer still needs to share a verified number.</summary>
    /// <param name="profile">Shared Telegram credentials profile for the sender. A missing profile has no verified number.</param>
    /// <returns>True when the live requirement is enabled and the sender has no stored verified number.</returns>
    /// <remarks>Call only in the owned customer handler, after tenant and administrator routing.</remarks>
    /// <example><code>if (NeedsOwnedPhoneVerification(profile)) return;</code></example>
    private bool NeedsOwnedPhoneVerification(CredUser profile) =>
        _phoneVerification.Snapshot.Enabled && string.IsNullOrWhiteSpace(profile?.PhoneNumber);

    /// <summary>Prompts an unverified owned customer for their own contact before opening the main menu.</summary>
    /// <param name="client">Telegram transport for the active owned bot.</param>
    /// <param name="chatId">Private Telegram chat id of the customer.</param>
    /// <param name="cancellationToken">Cancellation of the Telegram delivery.</param>
    /// <param name="referralMessage">Optional result of registering the referral payload before checking entry.</param>
    /// <remarks>The Telegram contact handler verifies ownership and persists valid numbers. No separate conversation state is created by the prompt.</remarks>
    /// <exception cref="OperationCanceledException">The Telegram operation is cancelled.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the contact prompt.</exception>
    private Task SendOwnedPhoneEntryPromptAsync(
        ITelegramBotClient client, long chatId, CancellationToken cancellationToken, string referralMessage) =>
        client.SendMessage(chatId,
            "برای ورود به ربات، شماره تلفن خود را با دکمهٔ زیر ارسال کنید. تأیید خودکار فقط با شماره موبایل ایران و متعلق به همین حساب تلگرام انجام می‌شود." +
            (string.IsNullOrWhiteSpace(referralMessage) ? string.Empty : "\n\n" + referralMessage),
            replyMarkup: GetPhoneNumber(), cancellationToken: cancellationToken);

    /// <summary>Requests the customer's contact before selecting or confirming an unpaid legacy trial.</summary>
    /// <param name="client">Telegram transport for the active owned bot.</param>
    /// <param name="chatId">Private Telegram chat id of the customer requesting the trial.</param>
    /// <param name="cancellationToken">Cancellation of the Telegram delivery.</param>
    /// <remarks>The prompt leaves the customer's current trial selection and cooldown intact.</remarks>
    /// <exception cref="OperationCanceledException">The Telegram operation is cancelled.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the contact prompt.</exception>
    private Task SendLegacyTrialPhonePromptAsync(ITelegramBotClient client, long chatId, CancellationToken cancellationToken) =>
        client.SendMessage(chatId,
            "برای تأیید خودکار، فقط شماره موبایل ایران پذیرفته می‌شود و شماره ارسالی باید متعلق به همین حساب تلگرام باشد.\n\n" +
            "لطفاً شماره خود را با دکمه زیر ارسال کنید و سپس دوباره روی دریافت اکانت تست بزنید. " +
            "برای انصراف می‌توانید /start را ارسال کنید.",
            replyMarkup: GetPhoneNumber(), cancellationToken: cancellationToken);
}
