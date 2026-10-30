using System.Globalization;
using System.Net;
using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Services;

/// <summary>Private owned-host global administrator UI for reviewed provisional purchases, separate from renewal review.</summary>
/// <remarks>Callbacks contain internal ids only. Every entry rechecks authority; execution confirmations are durable,
/// actor/host/chat bound and ten-minute single use. No customer credentials or panel response is displayed.</remarks>
public sealed class TenantCardProvisionalManualReviewAdminService
{
    /// <summary>Global super-admin menu action; not a tenant-owner action.</summary>
    public const string Action = "🛠 بررسی دستی سفارش‌های موقت";
    /// <summary>Closed namespace separate from XUI renewal and customer callback contracts.</summary>
    private const string Prefix = "tcpm:";
    /// <summary>Bot-scoped administrator search state; holds no account credential.</summary>
    private const string SearchStep = "admin-provisional-exact-order";
    private readonly TenantCardProvisionalManualReviewService _review;
    private readonly UserStateStore _state;
    private readonly XuiV3PurchaseSessionStore _sessions;

    /// <summary>Creates the UI over the durable reviewer and existing bot-scoped temporary state stores.</summary>
    /// <param name="review">Authoritative GET-only proof and conditional recovery service.</param>
    /// <param name="state">Current BotId + TelegramUserId conversation store.</param>
    /// <param name="sessions">Current bot's memory purchase sessions, cleared on explicit menu/search navigation only.</param>
    public TenantCardProvisionalManualReviewAdminService(TenantCardProvisionalManualReviewService review,
        UserStateStore state, XuiV3PurchaseSessionStore sessions)
    { _review = review; _state = state; _sessions = sessions; }

    /// <summary>Identifies only this review namespace, without granting authority.</summary>
    /// <param name="data">Untrusted nullable Telegram callback.</param><returns>True for this namespace, including malformed verbs.</returns>
    public static bool IsCallback(string data) => data?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Opens the list or consumes one exact-order search after rechecking private owned-host authority.</summary>
    /// <param name="client">Current host Telegram client.</param><param name="message">Actual Telegram message/sender.</param>
    /// <param name="token">Read/send cancellation.</param><returns>True only for this menu action or its bot-scoped search state.</returns>
    /// <remarks>Clears only the administrator's current bot conversation/session; never payment/order/account data.</remarks>
    public async Task<bool> TryHandleMessageAsync(ITelegramBotClient client, Message message, CancellationToken token)
    {
        if (message?.From == null || message.Text == null) return false;
        var open = message.Text == Action;
        // Ordinary customers/tenant traffic must not incur an extra conversation read for this administrator-only search.
        var authorized = message.Chat.Type == ChatType.Private && _review.IsAuthorized(message.From.Id, BotContextAccessor.CurrentBotId, message.Chat.Id);
        if (!open && !authorized) return false;
        var searching = !open && (await _state.GetUserStatus(message.From.Id)).LastStep == SearchStep;
        if (!open && !searching) return false;
        if (!authorized)
        { await client.SendMessage(message.Chat.Id, "این بخش فقط برای سوپرادمین در گفت‌وگوی خصوصی ربات اصلی است.", cancellationToken: token); return true; }
        await _state.ClearUserStatus(new global::User { Id = message.From.Id });
        _sessions.Clear(message.From.Id);
        await ShowListAsync(client, message.From.Id, message.Chat.Id, searching ? message.Text.Trim() : null, 0, token);
        return true;
    }

    /// <summary>Handles closed review verbs with actual sender/private-host reauthorization and separate recovery confirmation.</summary>
    /// <param name="client">Current owned-host Telegram client, not the tenant bot.</param>
    /// <param name="query">Untrusted callback containing only numeric order/review ids.</param><param name="token">Proof/transition/send cancellation.</param>
    /// <returns>True for this namespace, including refused or forged controls; false for unrelated callbacks.</returns>
    /// <remarks>Reprobe only reads. Prepare writes an audit, not an approval. Execute delegates to the central order gate and existing settlement.
    /// Callback acknowledgement uses the existing bounded best-effort policy and never grants business authority.</remarks>
    public async Task<bool> TryHandleCallbackAsync(ITelegramBotClient client, CallbackQuery query, CancellationToken token)
    {
        if (!IsCallback(query?.Data)) return false;
        var actor = query.From.Id;
        var chat = query.Message?.Chat;
        if (chat?.Type != ChatType.Private || !_review.IsAuthorized(actor, BotContextAccessor.CurrentBotId, chat.Id))
        { await TelegramCallbackAnswerPolicy.TryAnswerAsync(client, query.Id, "اجازه انجام این عملیات را ندارید.", showAlert: true, cancellationToken: token); return true; }
        await TelegramCallbackAnswerPolicy.TryAnswerAsync(client, query.Id, cancellationToken: token);
        var parts = query.Data.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
        { await SendResultAsync(client, chat.Id, "confirmation_invalid", token); return true; }
        switch (parts[1])
        {
            case "l" when id <= 10000:
                await _state.ClearUserStatus(new global::User { Id = actor });
                await ShowListAsync(client, actor, chat.Id, null, (int)id, token); break;
            case "s" when id == 0:
                _sessions.Clear(actor);
                await _state.ResetUserStatus(new global::User { Id = actor, Flow = "admin", LastStep = SearchStep });
                await client.SendMessage(chat.Id, "🔎 شناسه کامل و دقیق OrderId را ارسال کنید.", replyMarkup: Back(), cancellationToken: token); break;
            case "r" when id > 0 && id <= int.MaxValue:
                var p = await _review.ProbeAsync((int)id, actor, BotContextAccessor.CurrentBotId, chat.Id, token);
                var rows = new List<InlineKeyboardButton[]>();
                if (p.Eligible) rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ بازیابی و تکمیل سفارش", $"{Prefix}a:{id}") });
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData("↩️ فهرست", Prefix + "l:0") });
                await client.SendMessage(chat.Id, $"🔍 نتیجه بررسی سفارش #{id}\n{(p.Eligible ? "✅ بازیابی ایمن قابل تأیید است." : "⛔ بازیابی مجاز نیست.")}\nکد: {p.Reason}",
                    replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: token); break;
            case "a" when id > 0 && id <= int.MaxValue:
                var confirmation = await _review.PrepareAsync((int)id, actor, BotContextAccessor.CurrentBotId, chat.Id, token);
                if (confirmation == null) { await SendResultAsync(client, chat.Id, "fresh_probe_refused", token); break; }
                await client.SendMessage(chat.Id, $"⚠️ تأیید دوم برای سفارش #{id}\nهمان اکانت با سهمیه و تاریخ منجمد قبلی تکمیل و سپس تسویه مالی رسمی سفارش انجام می‌شود. اکانت جدیدی ساخته نمی‌شود.\nآیا تأیید می‌کنید؟ (اعتبار: ۱۰ دقیقه)",
                    replyMarkup: new InlineKeyboardMarkup(new[] {
                        new[] { InlineKeyboardButton.WithCallbackData("✅ تأیید نهایی بازیابی", $"{Prefix}y:{confirmation}") },
                        new[] { InlineKeyboardButton.WithCallbackData("انصراف / فهرست", $"{Prefix}x:{confirmation}") } }), cancellationToken: token); break;
            case "x" when id > 0:
                if (await _review.CancelAsync(new(id, actor, BotContextAccessor.CurrentBotId, chat.Id), token))
                    await ShowListAsync(client, actor, chat.Id, null, 0, token);
                else await SendResultAsync(client, chat.Id, "confirmation_stale", token);
                break;
            case "y" when id > 0:
                var outcome = await _review.ExecuteAsync(new(id, actor, BotContextAccessor.CurrentBotId, chat.Id), token);
                await SendResultAsync(client, chat.Id, outcome, token); break;
            default: await SendResultAsync(client, chat.Id, "confirmation_invalid", token); break;
        }
        return true;
    }

    /// <summary>Renders at most ten matching unresolved orders without customer identity or raw panel errors.</summary>
    /// <param name="client">Owned Telegram sender.</param><param name="actor">Authorized sender id.</param><param name="chatId">Private sender chat.</param>
    /// <param name="exact">Optional exact public order search.</param><param name="page">Zero-based page.</param><param name="token">Read/send cancellation.</param>
    /// <returns>A task after one sanitized list message is sent.</returns>
    private async Task ShowListAsync(ITelegramBotClient client, long actor, long chatId, string exact, int page, CancellationToken token)
    {
        var orders = await _review.ListAsync(actor, BotContextAccessor.CurrentBotId, chatId, exact, page, token);
        var text = Action + $"\nصفحه {page + 1}\n\n";
        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var item in orders)
        {
            var o = item.Order;
            text += $"🧾 #{o.Id} | {WebUtility.HtmlEncode(o.OrderId)}\n🤖 {WebUtility.HtmlEncode(o.TenantBotId)}\n📦 {WebUtility.HtmlEncode(o.ServiceKey)} / {WebUtility.HtmlEncode(o.UnlimitedPlanKey ?? o.DurationKey)}\nوضعیت: {WebUtility.HtmlEncode(o.PaymentStatus)}؛ تحویل: خیر\nمرحله: {item.Step}\nکد خطا: {item.Error}\n\n";
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData($"🔍 بررسی مجدد پنل #{o.Id}", $"{Prefix}r:{o.Id}") });
        }
        if (orders.Count == 0) text += "موردی یافت نشد.";
        if (page > 0) buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ صفحه قبل", $"{Prefix}l:{page - 1}") });
        if (orders.Count == 10) buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("صفحه بعد ▶️", $"{Prefix}l:{page + 1}") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 جستجوی OrderId دقیق", Prefix + "s:0") });
        await client.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: token);
    }

    /// <summary>Builds a navigation-only return button, with no operation authorization.</summary>
    /// <returns>Reusable list callback; authority is rechecked when clicked.</returns>
    private static InlineKeyboardMarkup Back() => new(InlineKeyboardButton.WithCallbackData("↩️ فهرست سفارش‌های موقت", Prefix + "l:0"));

    /// <summary>Displays the actual durable recovery outcome, never mislabels it as an invalid amount.</summary>
    /// <param name="client">Owned Telegram sender.</param><param name="chatId">Authorized private chat.</param>
    /// <param name="code">Closed service outcome, never an upstream exception.</param><param name="token">Send cancellation.</param>
    /// <returns>A task after safe result delivery; no financial changes.</returns>
    private static Task<Message> SendResultAsync(ITelegramBotClient client, long chatId, string code, CancellationToken token)
        => client.SendMessage(chatId, (code is "fulfilled" or "already_fulfilled"
            ? "✅ سفارش به‌صورت پایدار تکمیل و تسویه شد؛ اجرای دوباره اثر مالی ندارد."
            : "⛔ بازیابی تکمیل نشد. درخواست قبلی تکرار نمی‌شود؛ وضعیت را مجدداً بررسی کنید.") + $"\nکد نتیجه: {code}",
            replyMarkup: Back(), cancellationToken: token);
}
