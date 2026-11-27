using System.Globalization;
using Adminbot.Domain;
using Adminbot.Utils;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Platform super-admin controls and customer-facing text for the owned wallet-charge campaign.</summary>
public partial class TelegramBotService
{
    /// <summary>Administrative reply action exposed only in the owned super-admin panel.</summary>
    private const string AdminWalletChargeCampaignAction = "🎁 مدیریت هدیه شارژ";
    /// <summary>Bot-scoped admin draft flow; only the platform settings written at the end are global.</summary>
    private const string WalletChargeCampaignEditFlow = "admin-wallet-charge-campaign";
    /// <summary>Opens the name edit step.</summary>
    private const string CampaignEditNameAction = "✏️ ویرایش نام کمپین";
    /// <summary>Opens the whole bonus percentage edit step.</summary>
    private const string CampaignEditPercentAction = "🔢 تغییر درصد هدیه";
    /// <summary>Explicitly enables future capture, never toggling stale controls.</summary>
    private const string CampaignEnableAction = "▶️ فعال‌سازی هدیه شارژ";
    /// <summary>Explicitly pauses future capture while preserving all existing promises.</summary>
    private const string CampaignDisableAction = "⏸ غیرفعال‌سازی هدیه شارژ";

    /// <summary>Handles campaign administration before other conversational flows can consume the message.</summary>
    /// <param name="client">Current owned-bot Telegram transport.</param>
    /// <param name="message">Required incoming update message; only a configured super-admin's private chat is accepted.</param>
    /// <param name="token">Cancellation for settings reads/writes and Telegram replies.</param>
    /// <returns>True for consumed administrative actions or edit input; false for other routes and unauthorized senders.</returns>
    /// <remarks>Bot/user drafts cannot leak across storefronts. Explicit enable/pause commands are retry-safe.
    /// No edit changes a previously issued invoice, balance, ledger entry or order.</remarks>
    private async Task<bool> TryHandleWalletChargeCampaignAdminAsync(ITelegramBotClient client, Message message, CancellationToken token)
    {
        if ((CurrentBot?.Type ?? BotContextAccessor.CurrentBotType) != BotInstanceTypes.Owned ||
            message.Chat.Type != ChatType.Private || message.From == null || message.Chat.Id != message.From.Id ||
            string.IsNullOrWhiteSpace(message.Text) || !IsSuperAdminUser(message.From.Id))
            return false;

        var input = message.Text.Trim();
        var actor = message.From.Id;
        var state = await _state.GetUserStatus(actor);
        string name = null;
        int? percent = null;
        bool? enabled = null;
        switch (input)
        {
            case AdminWalletChargeCampaignAction:
                break;
            case CampaignEditNameAction:
            case CampaignEditPercentAction:
                var editingName = input == CampaignEditNameAction;
                await ResetCurrentBotConversationAsync(actor, token);
                await _state.SaveUserStatus(new User { Id = actor, Flow = WalletChargeCampaignEditFlow, LastStep = editingName ? "name" : "percent" });
                await client.SendMessage(message.Chat.Id, editingName ? "نام کمپین را وارد کنید (۱ تا ۶۰ نویسه در یک خط)." :
                    "درصد هدیه را به‌صورت عدد صحیح از ۱ تا ۱۰۰ وارد کنید.", cancellationToken: token);
                return true;
            case CampaignEnableAction:
            case CampaignDisableAction:
                enabled = input == CampaignEnableAction;
                break;
            default:
                if (state?.Flow != WalletChargeCampaignEditFlow) return false;
                if (input.StartsWith('/') || GetAdminActions().Contains(input, StringComparer.Ordinal))
                {
                    await ResetCurrentBotConversationAsync(actor, token);
                    return false;
                }
                if (state.LastStep == "name" && input.Length is >= 1 and <= 60 && !input.Any(char.IsControl))
                    name = input;
                else if (state.LastStep == "percent" && int.TryParse(input.PersianNumbersToEnglish(), NumberStyles.None,
                             CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 100)
                    percent = value;
                else
                {
                    await client.SendMessage(message.Chat.Id, "ورودی نامعتبر است؛ دوباره وارد کنید یا به پنل برگردید.", cancellationToken: token);
                    return true;
                }
                break;
        }

        await ResetCurrentBotConversationAsync(actor, token);
        WalletChargeCampaign settings;
        if (name != null || percent.HasValue || enabled.HasValue)
        {
            settings = await _workflow.WriteAsync(async db =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var row = await db.WalletChargeCampaigns.FindAsync([1], token);
                if (row == null) db.WalletChargeCampaigns.Add(row = new WalletChargeCampaign());
                row.Name = name ?? row.Name;
                row.Percent = percent ?? row.Percent;
                row.Enabled = enabled ?? row.Enabled;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return row;
            }, token);
        }
        else
            settings = await ReadWalletChargeCampaignAsync(token);

        var keyboard = new ReplyKeyboardMarkup(new KeyboardButton[][]
        {
            [CampaignEditNameAction, CampaignEditPercentAction],
            [settings.Enabled ? CampaignDisableAction : CampaignEnableAction],
            [AdminPanelEntryAction]
        }) { ResizeKeyboard = true };
        await client.SendMessage(message.Chat.Id,
            $"🎁 مدیریت هدیه شارژ\n\nنام کمپین: {settings.Name}\nدرصد هدیه: {settings.Percent}٪\n" +
            $"وضعیت: {(settings.Enabled ? "✅ فعال" : "⏸ غیرفعال")}\n\nتغییرات فقط برای فاکتورهای جدید اعمال می‌شوند.",
            replyMarkup: keyboard, cancellationToken: token);
        return true;
    }

    /// <summary>Reads the current global settings, using disabled defaults before the first admin edit.</summary>
    /// <param name="token">Cancellation of the users.db read.</param>
    /// <returns>Detached settings or the disabled Black Friday defaults.</returns>
    private async Task<WalletChargeCampaign> ReadWalletChargeCampaignAsync(CancellationToken token) =>
        await _workflow.ReadAsync(async db => await db.WalletChargeCampaigns.FindAsync([1], token)) ?? new WalletChargeCampaign();

    /// <summary>Previews the current owned wallet offer before the customer confirms issuance.</summary>
    /// <param name="user">Current Telegram customer's credentials profile; only its fresh role is relevant.</param>
    /// <param name="principal">Proposed wallet credit in positive whole toman before fees.</param>
    /// <param name="token">Cancellation of the read-only settings lookup.</param>
    /// <returns>Optional safe Persian Telegram HTML, with an explicit preview versus issued-price distinction.</returns>
    private async Task<string> DescribeWalletChargePreviewAsync(CredUser user, long principal, CancellationToken token)
    {
        if (user == null || !WalletChargeCampaignService.IsEligible(TenantBotPaymentPurposes.WalletCharge,
                CurrentBot?.Type ?? BotContextAccessor.CurrentBotType, CurrentBot?.Id ?? BotContextAccessor.CurrentBotId,
                _botRegistry?.DefaultBot?.Id, user.IsColleague))
            return string.Empty;
        var preview = WalletChargeCampaignService.Describe(WalletChargeCampaignService.Quote(await ReadWalletChargeCampaignAsync(token), principal), html: true);
        return preview.Length == 0 ? preview : preview + "\nهدیه قطعی در فاکتور صادرشده نمایش داده می‌شود.";
    }

    /// <summary>Displays the frozen invoice promise before the associated payment link is exposed.</summary>
    /// <param name="client">Current originating owned-bot transport.</param>
    /// <param name="chatId">Destination Telegram customer chat from the current flow.</param>
    /// <param name="message">Existing provider-specific Telegram HTML invoice text; never used as financial proof.</param>
    /// <param name="provider">Canonical provider key from the invoice creator.</param>
    /// <param name="paymentId">Local id of this provider payment, not a gateway id.</param>
    /// <param name="photoCaption">Whether the invoice text is used as a Telegram media caption.</param>
    /// <param name="token">Cancellation of the promise read and any overflow text delivery.</param>
    /// <returns>HTML invoice text including the promise unless a separate preceding message was required.</returns>
    /// <remarks>Never re-quotes a live campaign. A long QR caption is not expanded beyond Telegram's text limit.</remarks>
    private async Task<string> AppendIssuedWalletChargeBonusAsync(ITelegramBotClient client, long chatId, string message,
        string provider, int paymentId, bool photoCaption, CancellationToken token)
    {
        var bonus = await _workflow.ReadAsync(db => WalletChargeCampaignService.FindAsync(db, provider, paymentId, token));
        var details = WalletChargeCampaignService.Describe(bonus, html: true);
        if (details.Length != 0 && photoCaption && message.Length + details.Length > 1024)
        {
            await client.SendMessage(chatId, details.TrimStart(), parseMode: ParseMode.Html, cancellationToken: token);
            return message;
        }
        return message + details;
    }
}
