using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Super-admin global sale/renewal controls and legacy owned customer admission guards.</summary>
/// <remarks>The commercial policy is shared by all bots, not saved in an administrator's conversation or a tenant's settings.</remarks>
public partial class TelegramBotService
{
    /// <summary>Owned super-admin reply action opening six independent global commercial switches.</summary>
    private const string AdminSalesControlAction = "📊 کنترل فروش و تمدید";
    /// <summary>Closed super-admin callback namespace, separate from customer purchase/renewal actions.</summary>
    private const string SalesControlCallbackPrefix = "sales:";
    /// <summary>Maximum age, in seconds, of a global control button; stale revisions are rejected independently.</summary>
    private const long SalesControlCallbackLifetimeSeconds = 600;
    /// <summary>Stable category order for both the status text and keyboard; never derived from customer input.</summary>
    private static readonly ServiceSalesCategory[] SalesControlCategories =
        { ServiceSalesCategory.Normal, ServiceSalesCategory.National, ServiceSalesCategory.Unlimited };

    /// <summary>Opens global commercial controls before a stale admin/customer flow can consume the command.</summary>
    /// <param name="client">Required current owned-bot transport.</param>
    /// <param name="message">Original Telegram message; only the exact administrative action is consumed.</param>
    /// <param name="isOwnedBot">Whether the outer dispatcher resolved an owned bot, never a tenant owner's assertion.</param>
    /// <param name="token">Cancellation of Telegram panel delivery.</param>
    /// <returns>True for the panel action, including a rejected unauthorized actor; false for other text.</returns>
    /// <remarks>Checks the configured super-admin allow-list, not colleague status. Showing the panel does not write state, configuration, wallets or orders.</remarks>
    /// <exception cref="OperationCanceledException">The current update is cancelled.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The interactive panel send exceeds its budget without resend.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the panel or unauthorized-actor notice.</exception>
    /// <example><code>if (await TryHandleSalesControlMessageAsync(client, message, isOwnedBot, token)) return;</code></example>
    private async Task<bool> TryHandleSalesControlMessageAsync(ITelegramBotClient client, Message message, bool isOwnedBot, CancellationToken token)
    {
        if (message.Text != AdminSalesControlAction) return false;
        if (!isOwnedBot || !IsSuperAdminUser(message.From.Id))
        {
            await client.SendMessage(message.Chat.Id, "این بخش فقط برای سوپرادمین‌هاست.", cancellationToken: token);
            return true;
        }
        var snapshot = _xuiV3PurchaseService.SalesAvailability.Snapshot;
        await client.SendMessage(message.Chat.Id, BuildSalesControlText(snapshot), parseMode: ParseMode.Html,
            replyMarkup: BuildSalesControlMarkup(snapshot), cancellationToken: token);
        return true;
    }

    /// <summary>Renders safe current global commercial states and the accepted-payment settlement boundary.</summary>
    /// <param name="snapshot">Required immutable global state captured once for consistent text and keyboard rendering.</param>
    /// <returns>Persian HTML describing all six flags, with no customer identifiers, secrets or configuration content.</returns>
    /// <remarks>A closure applies to new unpaid admission. Previously issued invoices, committed debits and started operations retain their existing settlement/recovery path.</remarks>
    /// <example><code>var text = BuildSalesControlText(availability.Snapshot);</code></example>
    private static string BuildSalesControlText(ServiceSalesAvailabilitySnapshot snapshot)
    {
        var text = new StringBuilder("📊 <b>کنترل سراسری فروش و تمدید</b>\n\n");
        foreach (var category in SalesControlCategories)
            text.AppendLine($"<b>{ServiceSalesPolicy.GetCategoryLabel(category)}</b>: فروش {(snapshot.IsEnabled(category, ServiceSalesOperation.Sale) ? "✅ باز" : "❌ بسته")} | تمدید {(snapshot.IsEnabled(category, ServiceSalesOperation.Renewal) ? "✅ باز" : "❌ بسته")}");
        text.Append("\nهر دکمه فقط همان عملیات را باز یا بسته می‌کند. تغییر فوراً روی همه ربات‌های owned و tenant اعمال و ذخیره می‌شود.\n\n" +
            "بستن، درخواست‌های جدید و دکمه‌های قدیمیِ بدون پرداخت را متوقف می‌کند. فاکتورهای قبلاً صادرشده، پرداخت‌های ثبت‌شده و عملیات شروع‌شده برای حفظ حق مشتری تکمیل می‌شوند.");
        return text.ToString();
    }

    /// <summary>Builds target-state controls, not blind toggles, bound to one snapshot revision and ten-minute lifetime.</summary>
    /// <param name="snapshot">Required immutable state shared with the panel text.</param>
    /// <returns>Three two-column category rows and one refresh row; all callback payloads contain only closed ASCII values.</returns>
    /// <remarks>Any intervening global sales change rejects an old button, so another admin cannot overwrite a newer decision accidentally.</remarks>
    /// <example><code>var markup = BuildSalesControlMarkup(snapshot);</code></example>
    private static InlineKeyboardMarkup BuildSalesControlMarkup(ServiceSalesAvailabilitySnapshot snapshot)
    {
        var rows = new InlineKeyboardButton[SalesControlCategories.Length + 1][];
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var index = 0; index < SalesControlCategories.Length; index++)
        {
            var category = SalesControlCategories[index];
            rows[index] = new[] { BuildSalesControlButton(snapshot, category, ServiceSalesOperation.Sale, issued),
                BuildSalesControlButton(snapshot, category, ServiceSalesOperation.Renewal, issued) };
        }
        rows[^1] = new[] { InlineKeyboardButton.WithCallbackData("🔄 تازه‌سازی", FormattableString.Invariant($"{SalesControlCallbackPrefix}{snapshot.Revision}:{issued}:refresh")) };
        return new(rows);
    }

    /// <summary>Builds one explicit open/close button for a category and operation.</summary>
    /// <param name="snapshot">Current global flags and revision.</param>
    /// <param name="category">Closed global category, not a per-tenant plan choice.</param>
    /// <param name="operation">Sale or renewal whose opposite remains unchanged.</param>
    /// <param name="issued">UTC Unix seconds at panel rendering, used only for callback expiry.</param>
    /// <returns>An inline button with the target state, revision and expiry origin, safe within Telegram's callback size limit.</returns>
    /// <remarks>Labels describe the action that pressing the button will perform, rather than ambiguously displaying a checkbox.</remarks>
    /// <example><code>var button = BuildSalesControlButton(snapshot, ServiceSalesCategory.Normal, ServiceSalesOperation.Sale, issued);</code></example>
    private static InlineKeyboardButton BuildSalesControlButton(ServiceSalesAvailabilitySnapshot snapshot,
        ServiceSalesCategory category, ServiceSalesOperation operation, long issued)
    {
        var enabled = snapshot.IsEnabled(category, operation);
        var categoryToken = category switch { ServiceSalesCategory.Normal => "normal", ServiceSalesCategory.National => "national", _ => "unlimited" };
        return InlineKeyboardButton.WithCallbackData(
            $"{(enabled ? "❌ بستن" : "✅ باز کردن")} {(operation == ServiceSalesOperation.Sale ? "فروش" : "تمدید")} {ServiceSalesPolicy.GetCategoryLabel(category)}",
            FormattableString.Invariant($"{SalesControlCallbackPrefix}{snapshot.Revision}:{issued}:{categoryToken}:{(operation == ServiceSalesOperation.Sale ? "sale" : "renew")}:{(enabled ? 0 : 1)}"));
    }

    /// <summary>Authenticates and processes only the global commercial-control callback namespace.</summary>
    /// <param name="client">Required current owned-bot transport.</param>
    /// <param name="callback">Original callback sender; authority is never inferred from its attached message.</param>
    /// <param name="token">Cancellation of configuration persistence, acknowledgement and panel edit.</param>
    /// <returns>True for a consumed control callback, including rejected unauthorized/stale/malformed requests.</returns>
    /// <remarks>Tenant routing must precede this handler. Both this handler and the persistent policy verify global super-admin authority. Target writes precede UI success; edit failure cannot undo or repeat a committed control change.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels processing.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">Panel refresh exceeds the foreground budget; committed permissions remain effective.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects acknowledgement or refresh for a reason other than an already-identical message; a durable change is not rolled back.</exception>
    /// <example><code>if (await TryHandleSalesControlCallbackAsync(client, callback, token)) return;</code></example>
    private async Task<bool> TryHandleSalesControlCallbackAsync(ITelegramBotClient client, CallbackQuery callback, CancellationToken token)
    {
        if (callback.Data?.StartsWith(SalesControlCallbackPrefix, StringComparison.Ordinal) != true) return false;
        var type = CurrentBot?.Type ?? BotContextAccessor.CurrentBotType;
        if ((!string.IsNullOrEmpty(type) && !string.Equals(type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
            || !IsSuperAdminUser(callback.From.Id))
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "این بخش فقط برای سوپرادمین‌هاست.", showAlert: true, cancellationToken: token);
            return true;
        }
        var parts = callback.Data.Split(':');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parts.Length is not (4 or 6) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
            || issued < now - SalesControlCallbackLifetimeSeconds || issued > now + 5)
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "این دکمه نامعتبر یا منقضی شده است؛ پنل را تازه کنید.", showAlert: true, cancellationToken: token);
            return true;
        }
        var availability = _xuiV3PurchaseService.SalesAvailability;
        if (parts.Length == 4 && parts[3] == "refresh")
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "وضعیت به‌روز شد.", cancellationToken: token);
        else
        {
            ServiceSalesCategory? category = parts[3] switch { "normal" => ServiceSalesCategory.Normal, "national" => ServiceSalesCategory.National, "unlimited" => ServiceSalesCategory.Unlimited, _ => null };
            ServiceSalesOperation? operation = parts.Length == 6 ? parts[4] switch { "sale" => ServiceSalesOperation.Sale, "renew" => ServiceSalesOperation.Renewal, _ => null } : null;
            if (parts.Length != 6 || category == null || operation == null || parts[5] is not ("0" or "1"))
            {
                await SafeAnswerCallbackQueryAsync(client, callback.Id, "دکمه نامعتبر است.", showAlert: true, cancellationToken: token);
                return true;
            }
            var result = await availability.SetEnabledAsync(category.Value, operation.Value, parts[5] == "1", revision, callback.From.Id, token);
            await SafeAnswerCallbackQueryAsync(client, callback.Id, result.Message, showAlert: !result.Applied, cancellationToken: token);
        }
        if (callback.Message is { } panel)
        {
            var snapshot = availability.Snapshot;
            try
            {
                await client.EditMessageText(panel.Chat.Id, panel.MessageId, BuildSalesControlText(snapshot),
                    parseMode: ParseMode.Html, replyMarkup: BuildSalesControlMarkup(snapshot), cancellationToken: token);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (TenantBotService.ISTELEGRAMMESSAGENOTMODIFIED(ex.ErrorCode, ex.Message))
            {
                // Reuse the storefront classifier: an identical visible panel is success, never another control write.
            }
        }
        return true;
    }

    /// <summary>Stops an unpaid legacy owned customer purchase or renewal before panel mutation and wallet settlement.</summary>
    /// <param name="client">Required current owned-bot transport.</param>
    /// <param name="user">Current bot/user legacy conversation; its historical account type selects the global service family.</param>
    /// <param name="message">Original customer chat and sender whose state may be cleared after rejection.</param>
    /// <param name="operation">Sale or renewal being admitted; callers do not use this for funded recovery.</param>
    /// <returns>True when closed and a safe notice was sent; false while the category/operation is open.</returns>
    /// <remarks>Legacy v2 mutation precedes its wallet debit. Check before the panel call, not between accepted panel mutation and settlement, or an already renewed customer account could escape its debit.</remarks>
    /// <exception cref="OperationCanceledException">The notice or state write is cancelled.</exception>
    /// <example><code>if (await RejectClosedLegacySaleAsync(client, user, message, ServiceSalesOperation.Renewal)) return;</code></example>
    private async Task<bool> RejectClosedLegacySaleAsync(ITelegramBotClient client, User user, Message message, ServiceSalesOperation operation)
    {
        var category = ServiceSalesPolicy.GetCategory(user.Type);
        if (_xuiV3PurchaseService.SalesAvailability.Snapshot.IsEnabled(category, operation)) return false;
        await _state.ClearUserStatus(user);
        await client.SendMessage(message.Chat.Id, ServiceSalesPolicy.GetDisabledMessage(category, operation),
            replyMarkup: MainReplyMarkupKeyboardFa());
        return true;
    }
}
