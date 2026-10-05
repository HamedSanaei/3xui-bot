using System.Globalization;
using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Owned-bot super-admin controls for the global test-account logger preference.</summary>
/// <remarks>The preference is global, not tenant state; callbacks never create accounts or modify financial records.</remarks>
public partial class TelegramBotService
{
    /// <summary>Reply action shown in the configured super-admin panel.</summary>
    private const string AdminTrialAccountLoggingAction = "🔔 لاگ اکانت تست";
    /// <summary>Closed callback namespace for explicit target-state logging changes.</summary>
    private const string TrialAccountLoggingCallbackPrefix = "triallog:";
    /// <summary>Maximum lifetime of an administrator button in seconds.</summary>
    private const long TrialAccountLoggingCallbackLifetimeSeconds = 600;

    /// <summary>Opens the logging control before stale customer/admin conversation flows can consume the command.</summary>
    /// <param name="client">Required current owned-bot Telegram transport.</param>
    /// <param name="message">Original sender message; only the exact panel action is consumed.</param>
    /// <param name="isOwnedBot">Whether the runtime dispatcher resolved an owned bot, not a customer assertion.</param>
    /// <param name="token">Cancellation of panel delivery.</param>
    /// <returns>True for the panel action, including rejected unauthorized users; false for unrelated text.</returns>
    /// <remarks>Only the configured global super-admin allow-list grants authority. Opening the panel performs no configuration or conversation writes.</remarks>
    /// <exception cref="OperationCanceledException">The update is cancelled.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects delivery.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">Panel delivery exceeds the foreground budget without automatic resend.</exception>
    /// <example><code>if (await TryHandleTrialAccountLoggingMessageAsync(client, message, isOwnedBot, token)) return;</code></example>
    private async Task<bool> TryHandleTrialAccountLoggingMessageAsync(ITelegramBotClient client, Message message, bool isOwnedBot, CancellationToken token)
    {
        if (message.Text != AdminTrialAccountLoggingAction) return false;
        if (!isOwnedBot || !IsSuperAdminUser(message.From.Id))
        {
            await client.SendMessage(message.Chat.Id, "این بخش فقط برای سوپرادمین‌هاست.", cancellationToken: token);
            return true;
        }
        var snapshot = _trialAccountLogging.Snapshot;
        await client.SendMessage(message.Chat.Id, BuildTrialAccountLoggingText(snapshot), parseMode: ParseMode.Html,
            replyMarkup: BuildTrialAccountLoggingMarkup(snapshot), cancellationToken: token);
        return true;
    }

    /// <summary>Renders the current global logger state and its issuance/financial boundaries.</summary>
    /// <param name="snapshot">Required immutable state shared with the keyboard for consistent rendering.</param>
    /// <returns>Safe Persian HTML for the admin panel, containing no identifiers or secrets.</returns>
    /// <remarks>Previously enqueued logs are not recalled; this setting governs newly emitted trial acquisition audits.</remarks>
    /// <example><code>var text = BuildTrialAccountLoggingText(settings.Snapshot);</code></example>
    private static string BuildTrialAccountLoggingText(TrialAccountLoggingSnapshot snapshot) =>
        "🔔 <b>لاگ اکانت تست</b>\n\n" +
        $"وضعیت ارسال به کانال لاگر: {(snapshot.Enabled ? "✅ روشن" : "❌ خاموش")}\n\n" +
        "این تنظیم برای همه ربات‌هاست و فوراً اعمال و ذخیره می‌شود. فقط ارسال گزارش دریافت اکانت تست به کانال لاگر تغییر می‌کند؛ دریافت اکانت تست، لاگ‌های دیگر، خطاهای فنی و بکاپ پرداخت‌ها تغییری نمی‌کنند.\n\n" +
        "گزارش‌هایی که قبلاً در صف ارسال بوده‌اند ممکن است همچنان ارسال شوند.";

    /// <summary>Builds a revision-bound explicit on/off button and a refresh action.</summary>
    /// <param name="snapshot">Current global state captured with panel text.</param>
    /// <returns>Nonempty inline keyboard with ASCII callback payloads below Telegram's size limit.</returns>
    /// <remarks>Explicit targets prevent duplicate callbacks from blindly toggling twice; revisions reject conflicting administrator changes.</remarks>
    /// <example><code>var markup = BuildTrialAccountLoggingMarkup(settings.Snapshot);</code></example>
    private static InlineKeyboardMarkup BuildTrialAccountLoggingMarkup(TrialAccountLoggingSnapshot snapshot)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(snapshot.Enabled ? "🔕 خاموش کردن لاگ اکانت تست" : "🔔 روشن کردن لاگ اکانت تست",
                FormattableString.Invariant($"{TrialAccountLoggingCallbackPrefix}{snapshot.Revision}:{issued}:{(snapshot.Enabled ? 0 : 1)}")) },
            new[] { InlineKeyboardButton.WithCallbackData("🔄 تازه‌سازی",
                FormattableString.Invariant($"{TrialAccountLoggingCallbackPrefix}{snapshot.Revision}:{issued}:refresh")) }
        });
    }

    /// <summary>Authenticates and applies one live global trial-channel preference callback.</summary>
    /// <param name="client">Required current owned-bot transport, never a tenant customer's chosen bot.</param>
    /// <param name="callback">Original callback with global Telegram sender id and revision/timestamp/target payload.</param>
    /// <param name="token">Cancellation of configuration persistence, acknowledgement and panel refresh.</param>
    /// <returns>True for this callback namespace, including unauthorized, expired and malformed requests; false for unrelated callbacks.</returns>
    /// <remarks>
    /// Tenant routing precedes this handler. Authorization is rechecked even for forged callbacks. Persistence failure leaves
    /// the live flag unchanged and is shown as an alert. A failed Telegram refresh never undoes or repeats a committed write.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The update is cancelled.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects acknowledgement or refresh, except an unchanged message.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">Panel refresh exceeds its budget; a committed preference remains effective.</exception>
    /// <example><code>if (await TryHandleTrialAccountLoggingCallbackAsync(client, callback, token)) return;</code></example>
    private async Task<bool> TryHandleTrialAccountLoggingCallbackAsync(ITelegramBotClient client, CallbackQuery callback, CancellationToken token)
    {
        if (callback.Data?.StartsWith(TrialAccountLoggingCallbackPrefix, StringComparison.Ordinal) != true) return false;
        var type = CurrentBot?.Type ?? BotContextAccessor.CurrentBotType;
        if ((!string.IsNullOrEmpty(type) && !string.Equals(type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
            || !IsSuperAdminUser(callback.From.Id))
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "این بخش فقط برای سوپرادمین‌هاست.", showAlert: true, cancellationToken: token);
            return true;
        }
        var parts = callback.Data.Split(':');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parts.Length != 4 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
            || issued < now - TrialAccountLoggingCallbackLifetimeSeconds || issued > now + 5 || parts[3] is not ("0" or "1" or "refresh"))
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "این دکمه نامعتبر یا منقضی شده است؛ پنل را تازه کنید.", showAlert: true, cancellationToken: token);
            return true;
        }
        if (parts[3] == "refresh")
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "وضعیت به‌روز شد.", cancellationToken: token);
        else
        {
            var result = await _trialAccountLogging.SetEnabledAsync(parts[3] == "1", revision, token);
            await SafeAnswerCallbackQueryAsync(client, callback.Id, result.Message, showAlert: !result.Applied, cancellationToken: token);
        }
        if (callback.Message is { } panel)
        {
            var snapshot = _trialAccountLogging.Snapshot;
            try
            {
                await client.EditMessageText(panel.Chat.Id, panel.MessageId, BuildTrialAccountLoggingText(snapshot),
                    parseMode: ParseMode.Html, replyMarkup: BuildTrialAccountLoggingMarkup(snapshot), cancellationToken: token);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(ex.ErrorCode, ex.Message))
            {
                // An already-identical visible panel needs neither another write nor another message.
            }
        }
        return true;
    }
}
