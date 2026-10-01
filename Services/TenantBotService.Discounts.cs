using System.Globalization;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public partial class TenantBotService
{
    /// <summary>Bot-scoped unsaved owner configuration; all fields are validated again at the users.db commit.</summary>
    private sealed class OwnerDiscountDraft
    {
        /// <summary>Exact selected storefront, not an authorization grant.</summary>
        public string TenantBotId { get; set; }
        /// <summary>Existing code database id or null for a new definition.</summary>
        public int? CodeId { get; set; }
        /// <summary>Original optimistic revision of an existing definition.</summary>
        public DateTime? Revision { get; set; }
        /// <summary>Short session binding for the save button.</summary>
        public string Nonce { get; set; }
        /// <summary>Owner-entered code, normalized only when persisted.</summary>
        public string Code { get; set; }
        /// <summary>Fixed or percentage calculation.</summary>
        public string Kind { get; set; }
        /// <summary>Purchase, renewal, or both.</summary>
        public string Scope { get; set; }
        /// <summary>Positive fixed discount in toman, if selected.</summary>
        public long? FixedAmountToman { get; set; }
        /// <summary>Percent integer from 1 to 100, if selected.</summary>
        public int? Percent { get; set; }
        /// <summary>Optional whole-toman limit on the discount.</summary>
        public long? MaxDiscountToman { get; set; }
        /// <summary>Minimum undiscounted price in toman; null until entered.</summary>
        public long? MinimumOrderToman { get; set; }
        /// <summary>Maximum paid or reserved uses; null until entered.</summary>
        public int? MaxUses { get; set; }
        /// <summary>Activation target on save; new definitions default active.</summary>
        public bool IsActive { get; set; } = true;
    }

    /// <summary>Handles only the previously authenticated selected-store owner discount callback.</summary>
    /// <param name="client">Owned-bot transport wrapped with the selected-store owner panel decorator.</param>
    /// <param name="callback">Authenticated owner's callback with a fresh store-number and revision envelope.</param>
    /// <param name="owner">Colleague credentials whose Telegram id owns the selected storefront.</param>
    /// <param name="state">Bot-scoped owner state; persisted draft is reloaded before each mutation.</param>
    /// <param name="action">Decoded short discount action, not an authorization token.</param>
    /// <param name="token">Cancellation of local state, database, and Telegram operations.</param>
    /// <returns>A task after the nested menu or prompt is delivered.</returns>
    /// <remarks>Never reachable through tenant customer updates. Every read and commit additionally checks exact store ownership.</remarks>
    private async Task HandleOwnerDiscountCallbackAsync(ITelegramBotClient client, CallbackQuery callback,
        CredUser owner, User state, string action, CancellationToken token)
    {
        var discounts = _serviceProvider.GetRequiredService<TenantDiscountService>();
        var store = await RequireSelectedOwnerStoreAsync(owner, token);
        var parts = action.Split(':');
        var draft = ReadOwnerDiscountDraft(await _state.GetUserStatus(owner.TelegramUserId), store.Id);
        var messageId = callback.Message?.MessageId;
        var chat = callback.Message?.Chat.Id ?? callback.From.Id;
        var error = (string)null;
        if (parts.Length >= 2 && parts[1] == "l")
        {
            var page = parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var p)
                ? Math.Clamp(p, 0, 10000) : 0;
            await ShowOwnerDiscountListAsync(client, callback, owner, store, page, token);
            return;
        }
        if (action == "d:c")
        {
            draft = new OwnerDiscountDraft { TenantBotId = store.Id, Nonce = Guid.NewGuid().ToString("N")[..8] };
            await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "", token);
        }
        else if (parts.Length == 4 && parts[1] == "e" && ParseDiscountId(parts[2], out var editId))
        {
            if (draft == null || draft.CodeId.GetValueOrDefault() != editId)
            {
                if (editId == 0) { error = "ابتدا ساخت کد جدید را شروع کنید."; }
                else
                {
                    var existing = await discounts.GetCodeAsync(store.Id, owner.TelegramUserId, editId, token);
                    if (!existing.Success) error = DiscountOwnerError(existing.Failure);
                    else
                    {
                        var code = existing.Value;
                        draft = new OwnerDiscountDraft
                        {
                            TenantBotId = store.Id, CodeId = code.Id, Revision = code.UpdatedAtUtc,
                            Nonce = Guid.NewGuid().ToString("N")[..8], Code = code.Code, Kind = code.Kind,
                            Scope = code.Scope, FixedAmountToman = code.FixedAmountToman, Percent = code.Percent,
                            MaxDiscountToman = code.MaxDiscountToman, MinimumOrderToman = code.MinimumOrderToman,
                            MaxUses = code.MaxUses, IsActive = code.IsActive
                        };
                        await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "", token);
                    }
                }
            }
            if (error == null && parts[3] != "menu")
            {
                var field = parts[3];
                if (field is "code" or "value" or "cap" or "min" or "limit")
                {
                    await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "discount-" + field, token);
                    await client.SendMessage(chat, field switch
                    {
                        "code" => "کد لاتین (۳ تا ۳۲ حرف/عدد، _ یا -) را وارد کنید:",
                        "value" => draft.Kind == TenantDiscountKinds.Percent ? "درصد تخفیف (۱ تا ۱۰۰) را وارد کنید:" : "مبلغ تخفیف به تومان را وارد کنید:",
                        "cap" => "سقف تخفیف به تومان را وارد کنید؛ برای حذف سقف از دکمه «بدون سقف» استفاده کنید:",
                        "min" => "حداقل مبلغ سفارش قبل از تخفیف به تومان را وارد کنید (صفر مجاز است):",
                        _ => "حداکثر تعداد استفاده (۱ تا ۱٬۰۰۰٬۰۰۰) را وارد کنید:"
                    }, replyMarkup: new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("بازگشت به پنل") } })
                    { ResizeKeyboard = true }, cancellationToken: token);
                    await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
                    return;
                }
                error = "دکمه ویرایش معتبر نیست.";
            }
        }
        else if (draft != null && parts.Length == 3 && parts[1] == "t" && parts[2] is "f" or "p")
        {
            draft.Kind = parts[2] == "f" ? TenantDiscountKinds.Fixed : TenantDiscountKinds.Percent;
            draft.FixedAmountToman = null;
            draft.Percent = null;
            await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "", token);
        }
        else if (draft != null && parts.Length == 3 && parts[1] == "a" && parts[2] is "p" or "r" or "b")
        {
            draft.Scope = parts[2] == "p" ? TenantDiscountScopes.Purchase
                : parts[2] == "r" ? TenantDiscountScopes.Renew : TenantDiscountScopes.Both;
            await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "", token);
        }
        else if (draft != null && action == "d:cap:none")
        {
            draft.MaxDiscountToman = null;
            await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "", token);
        }
        else if (parts.Length == 4 && parts[1] == "s" && ParseDiscountId(parts[2], out var activeId)
            && parts[3] is "0" or "1")
        {
            var changed = await discounts.SetActiveAsync(store.Id, owner.TelegramUserId, activeId, parts[3] == "1", token);
            error = changed.Success ? null : DiscountOwnerError(changed.Failure);
            if (changed.Success) await _workflow.ReloadAsync(store, token);
            await ShowOwnerDiscountDetailAsync(client, callback, owner, store, activeId, error, token);
            return;
        }
        else if (parts.Length == 3 && parts[1] == "x" && ParseDiscountId(parts[2], out var deleteId))
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
            await ShowOwnerDiscountMessageAsync(client, chat, messageId,
                "حذف کد فقط ورودی‌های جدید را می‌بندد؛ سفارش‌ها و استفاده‌های قبلی باقی می‌مانند. حذف شود؟",
                new[]
                {
                    new[] { DiscountOwnerButton(store, "حذف کد", $"d:xc:{DiscountId(deleteId)}"), DiscountOwnerButton(store, "انصراف", $"d:v:{DiscountId(deleteId)}") }
                }, token);
            return;
        }
        else if (parts.Length == 3 && parts[1] == "xc" && ParseDiscountId(parts[2], out var confirmId))
        {
            var deleted = await discounts.DeleteAsync(store.Id, owner.TelegramUserId, confirmId, token);
            if (deleted.Success) await _workflow.ReloadAsync(store, token);
            await ShowOwnerDiscountListAsync(client, callback, owner, store, 0, token, deleted.Success ? null : DiscountOwnerError(deleted.Failure));
            return;
        }
        else if (parts.Length == 3 && parts[1] == "v" && ParseDiscountId(parts[2], out var detailId))
        {
            await ShowOwnerDiscountDetailAsync(client, callback, owner, store, detailId, null, token);
            return;
        }
        else if (draft != null && parts.Length == 3 && parts[1] == "save" && draft.Nonce == parts[2])
        {
            var input = new TenantDiscountCodeInput(draft.Code, draft.Kind, draft.Scope, draft.FixedAmountToman,
                draft.Percent, draft.MaxDiscountToman, draft.MinimumOrderToman ?? -1, draft.MaxUses ?? 0, draft.IsActive);
            var result = await discounts.SaveCodeAsync(store.Id, owner.TelegramUserId, input, draft.CodeId, draft.Revision, token);
            if (result.Success)
            {
                await _state.SaveUserStatus(new User { Id = owner.TelegramUserId, OwnerDiscountDraftJson = "", LastStep = "" });
                await _workflow.ReloadAsync(store, token);
                await ShowOwnerDiscountDetailAsync(client, callback, owner, store, result.Value.Id, null, token);
                return;
            }
            error = DiscountOwnerError(result.Failure);
            if (result.Failure == TenantDiscountFailure.Conflict && draft.CodeId.HasValue)
            {
                await _state.SaveUserStatus(new User { Id = owner.TelegramUserId, OwnerDiscountDraftJson = "", LastStep = "" });
                await ShowOwnerDiscountDetailAsync(client, callback, owner, store, draft.CodeId.Value, error, token);
                return;
            }
        }
        else error = "این دکمه قدیمی یا نامعتبر است؛ دوباره از فهرست شروع کنید.";

        await SafeAnswerCallbackQueryAsync(client, callback.Id, error, showAlert: error != null, cancellationToken: token);
        if (draft == null) await ShowOwnerDiscountListAsync(client, callback, owner, store, 0, token, error);
        else await ShowOwnerDiscountDraftAsync(client, chat, messageId, store, draft, error, token);
    }

    /// <summary>Validates one numeric/code owner input without writing the live definition.</summary>
    /// <param name="client">Owned-bot panel transport for the selected store.</param>
    /// <param name="message">Owner's Telegram text, not a callback or financial instruction.</param>
    /// <param name="owner">Authenticated colleague whose store must still be selected.</param>
    /// <param name="state">Bot-scoped draft and input step.</param>
    /// <param name="token">Cancellation of state and Telegram operations.</param>
    /// <returns>A task after saving the draft or showing an explicit input error.</returns>
    /// <remarks>Invalid input leaves the draft and step intact; owner panel cancellation clears both.</remarks>
    private async Task HandleOwnerDiscountTextAsync(ITelegramBotClient client, Message message, CredUser owner,
        User state, CancellationToken token)
    {
        var store = await RequireSelectedOwnerStoreAsync(owner, token);
        var draft = ReadOwnerDiscountDraft(state, store.Id);
        if (draft == null)
        {
            await client.SendMessage(message.Chat.Id, "پیش‌نویس تخفیف پیدا نشد؛ از فهرست کدها دوباره شروع کنید.", cancellationToken: token);
            await _state.SaveUserStatus(new User { Id = owner.TelegramUserId, LastStep = "" });
            return;
        }
        var text = message.Text.Trim();
        string error = null;
        switch (state.LastStep)
        {
            case "discount-code":
                var normalized = TenantDiscountService.NormalizeCode(text);
                if (normalized == null) error = "کد باید ۳ تا ۳۲ نویسه لاتین، عدد، _ یا - باشد.";
                else draft.Code = normalized;
                break;
            case "discount-value":
                if (draft.Kind == TenantDiscountKinds.Percent)
                {
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent is < 1 or > 100)
                        error = "درصد باید عدد صحیح بین ۱ و ۱۰۰ باشد.";
                    else draft.Percent = percent;
                }
                else if (draft.Kind == TenantDiscountKinds.Fixed)
                {
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                        error = "مبلغ تخفیف باید عدد صحیح مثبت به تومان باشد.";
                    else draft.FixedAmountToman = amount;
                }
                else error = "ابتدا نوع تخفیف را انتخاب کنید.";
                break;
            case "discount-cap":
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var cap) || cap <= 0)
                    error = "سقف باید عدد صحیح مثبت به تومان باشد؛ حذف سقف از منوی ویرایش انجام می‌شود.";
                else draft.MaxDiscountToman = cap;
                break;
            case "discount-min":
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minimum) || minimum < 0)
                    error = "حداقل سفارش باید عدد صحیح نامنفی به تومان باشد.";
                else draft.MinimumOrderToman = minimum;
                break;
            case "discount-limit":
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 1_000_000)
                    error = "حداکثر استفاده باید عدد صحیح بین ۱ و ۱٬۰۰۰٬۰۰۰ باشد.";
                else draft.MaxUses = limit;
                break;
            default: error = "مرحله ویرایش معتبر نیست."; break;
        }
        if (error != null)
        {
            await client.SendMessage(message.Chat.Id, error, cancellationToken: token);
            return;
        }
        await SaveOwnerDiscountDraftAsync(owner.TelegramUserId, draft, "", token);
        await client.SendMessage(message.Chat.Id, "مقدار در پیش‌نویس ثبت شد؛ تا ذخیره نهایی روی کد فعال اعمال نمی‌شود.",
            replyMarkup: new ReplyKeyboardRemove(), cancellationToken: token);
        await ShowOwnerDiscountDraftAsync(client, message.Chat.Id, null, store, draft, null, token);
    }

    /// <summary>Renders one page of this authenticated storefront's nondeleted codes.</summary>
    /// <param name="client">Owned-bot Telegram transport for the authenticated owner.</param>
    /// <param name="callback">Owner callback whose chat and message receive the current page.</param>
    /// <param name="owner">Credentials identity used to enforce exact storefront ownership.</param>
    /// <param name="store">Selected tenant storefront, not an arbitrary owner-owned store.</param>
    /// <param name="page">Zero-based page index; one page contains at most ten live definitions.</param>
    /// <param name="token">Cancellation for users.db reads and Telegram delivery.</param>
    /// <param name="error">Optional customer-safe operation failure to render above the list.</param>
    /// <returns>A task after the list or an ownership error is rendered.</returns>
    private async Task ShowOwnerDiscountListAsync(ITelegramBotClient client, CallbackQuery callback, CredUser owner,
        BotInstance store, int page, CancellationToken token, string error = null)
    {
        var listed = await _serviceProvider.GetRequiredService<TenantDiscountService>()
            .ListCodesAsync(store.Id, owner.TelegramUserId, page, token);
        var rows = new List<InlineKeyboardButton[]>();
        if (listed.Success)
            foreach (var code in listed.Value)
                rows.Add(new[] { DiscountOwnerButton(store, $"{(code.IsActive ? "✅" : "⛔")} {code.Code}", $"d:v:{DiscountId(code.Id)}") });
        if (page > 0) rows.Add(new[] { DiscountOwnerButton(store, "قبلی", $"d:l:{page - 1}") });
        if (listed.Success && listed.Value.Count == 10) rows.Add(new[] { DiscountOwnerButton(store, "بعدی", $"d:l:{page + 1}") });
        rows.Add(new[] { DiscountOwnerButton(store, "➕ ساخت کد", "d:c") });
        rows.Add(new[] { DiscountOwnerButton(store, "بازگشت به پنل", "panel") });
        await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
        var text = $"🎟 <b>کدهای تخفیف فروشگاه</b>\n{Html(error ?? (listed.Success ? "کدها فقط در همین فروشگاه قابل استفاده‌اند." : DiscountOwnerError(listed.Failure)))}";
        await ShowOwnerDiscountMessageAsync(client, callback.Message?.Chat.Id ?? callback.From.Id,
            callback.Message?.MessageId, text, rows, token);
    }

    /// <summary>Shows current definition and capacity without exposing another storefront's codes.</summary>
    /// <param name="client">Owned-bot Telegram transport for this owner.</param>
    /// <param name="callback">Original callback, including its chat and message id.</param>
    /// <param name="owner">Authenticated credentials owner of the selected store.</param>
    /// <param name="store">Current tenant storefront whose code must be loaded.</param>
    /// <param name="codeId">Internal users.db code id, rechecked against store and owner.</param>
    /// <param name="error">Optional validation failure displayed without changing the definition.</param>
    /// <param name="token">Cancellation of local reads and Telegram delivery.</param>
    /// <returns>A task after the detail or fallback list has been rendered.</returns>
    private async Task ShowOwnerDiscountDetailAsync(ITelegramBotClient client, CallbackQuery callback, CredUser owner,
        BotInstance store, int codeId, string error, CancellationToken token)
    {
        var discounts = _serviceProvider.GetRequiredService<TenantDiscountService>();
        var found = await discounts.GetCodeAsync(store.Id, owner.TelegramUserId, codeId, token);
        if (!found.Success)
        {
            await ShowOwnerDiscountListAsync(client, callback, owner, store, 0, token, error ?? DiscountOwnerError(found.Failure));
            return;
        }
        var code = found.Value;
        var usage = await discounts.GetUsageAsync(store.Id, owner.TelegramUserId, codeId, token);
        var id = DiscountId(codeId);
        var text = $"🎟 <b>{Html(code.Code)}</b>\nوضعیت: {(code.IsActive ? "فعال" : "غیرفعال")}\n" +
            $"نوع: {(code.Kind == TenantDiscountKinds.Fixed ? $"{code.FixedAmountToman:N0} تومان" : $"{code.Percent}%")}\n" +
            $"سقف: {(code.MaxDiscountToman.HasValue ? $"{code.MaxDiscountToman:N0} تومان" : "بدون سقف")}\n" +
            $"حداقل سفارش: {code.MinimumOrderToman:N0} تومان\nکاربرد: {DiscountScopeText(code.Scope)}\n" +
            $"مصرف‌شده: {usage.Value.Consumed} | رزروشده: {usage.Value.Reserved} | باقی‌مانده: {usage.Value.Remaining}";
        if (error != null) text = $"⚠️ {Html(error)}\n\n" + text;
        await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
        await ShowOwnerDiscountMessageAsync(client, callback.Message?.Chat.Id ?? callback.From.Id, callback.Message?.MessageId,
            text, new[]
            {
                new[] { DiscountOwnerButton(store, "✏️ ویرایش", $"d:e:{id}:menu") },
                new[] { DiscountOwnerButton(store, code.IsActive ? "⛔ غیرفعال" : "✅ فعال", $"d:s:{id}:{(code.IsActive ? 0 : 1)}") },
                new[] { DiscountOwnerButton(store, "🗑 حذف", $"d:x:{id}"), DiscountOwnerButton(store, "بازگشت", "d:l") }
            }, token);
    }

    /// <summary>Shows unsaved draft choices and the exact validation reason before committing any owner edit.</summary>
    /// <param name="client">Owned-bot transport for the draft preview.</param>
    /// <param name="chat">Authenticated owner's numeric Telegram chat id.</param>
    /// <param name="messageId">Existing menu message id, or null to send a new draft message.</param>
    /// <param name="store">Selected tenant storefront whose revision is embedded in buttons.</param>
    /// <param name="draft">Bot-scoped unsaved definition; no live code changes until save.</param>
    /// <param name="error">Optional customer-safe validation or concurrency failure.</param>
    /// <param name="token">Cancellation for the Telegram message operation.</param>
    /// <returns>A task after the draft controls are displayed.</returns>
    private async Task ShowOwnerDiscountDraftAsync(ITelegramBotClient client, long chat, int? messageId,
        BotInstance store, OwnerDiscountDraft draft, string error, CancellationToken token)
    {
        var id = DiscountId(draft.CodeId ?? 0);
        var input = new TenantDiscountCodeInput(draft.Code, draft.Kind, draft.Scope, draft.FixedAmountToman,
            draft.Percent, draft.MaxDiscountToman, draft.MinimumOrderToman ?? -1, draft.MaxUses ?? 0, draft.IsActive);
        var validation = TenantDiscountService.ValidateOwnerInput(input);
        var text = "🎟 <b>پیش‌نویس کد تخفیف</b>\nتا ذخیره نهایی هیچ تنظیمی تغییر نمی‌کند.\n" +
            $"کد: <code>{Html(draft.Code ?? "ثبت نشده")}</code>\n" +
            $"نوع: {Html(draft.Kind ?? "ثبت نشده")} | مقدار: {Html(draft.Kind == TenantDiscountKinds.Percent ? draft.Percent?.ToString() ?? "ثبت نشده" : draft.FixedAmountToman?.ToString("N0") ?? "ثبت نشده")}\n" +
            $"سقف: {(draft.MaxDiscountToman.HasValue ? $"{draft.MaxDiscountToman:N0} تومان" : "بدون سقف")}\n" +
            $"حداقل سفارش: {Html(draft.MinimumOrderToman?.ToString("N0") ?? "ثبت نشده")} تومان\n" +
            $"حداکثر استفاده: {Html(draft.MaxUses?.ToString("N0") ?? "ثبت نشده")}\n" +
            $"کاربرد: {DiscountScopeText(draft.Scope)}";
        if (error != null) text += $"\n\n⚠️ {Html(error)}";
        else if (validation != TenantDiscountFailure.None) text += $"\n\nبرای ذخیره: {Html(DiscountOwnerError(validation))}";
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { DiscountOwnerButton(store, "کد", $"d:e:{id}:code"), DiscountOwnerButton(store, "مقدار", $"d:e:{id}:value") },
            new[] { DiscountOwnerButton(store, "مبلغ ثابت", "d:t:f"), DiscountOwnerButton(store, "درصدی", "d:t:p") },
            new[] { DiscountOwnerButton(store, "سقف", $"d:e:{id}:cap"), DiscountOwnerButton(store, "بدون سقف", "d:cap:none") },
            new[] { DiscountOwnerButton(store, "حداقل سفارش", $"d:e:{id}:min"), DiscountOwnerButton(store, "حداکثر استفاده", $"d:e:{id}:limit") },
            new[] { DiscountOwnerButton(store, "خرید", "d:a:p"), DiscountOwnerButton(store, "تمدید", "d:a:r"), DiscountOwnerButton(store, "هر دو", "d:a:b") }
        };
        if (validation == TenantDiscountFailure.None)
            rows.Add(new[] { DiscountOwnerButton(store, "✅ ذخیره نهایی", $"d:save:{draft.Nonce}") });
        rows.Add(new[] { DiscountOwnerButton(store, "بازگشت به پنل", "panel") });
        await ShowOwnerDiscountMessageAsync(client, chat, messageId, text, rows, token);
    }

    /// <summary>Edits an owner menu when possible or sends it as a new selected-store-labelled message.</summary>
    /// <param name="client">Selected owner's Telegram bot transport.</param>
    /// <param name="chat">Numeric Telegram chat id for the owner menu.</param>
    /// <param name="messageId">Message to edit, or null when the menu needs a new message.</param>
    /// <param name="text">HTML-escaped owner-facing details and warnings.</param>
    /// <param name="rows">Owner-addressed inline buttons for the rendered menu.</param>
    /// <param name="token">Cancellation for Telegram delivery.</param>
    /// <returns>A task after Telegram accepts the edit, confirms identical content/markup, or accepts the new message.</returns>
    /// <remarks>Only message-not-modified is a successful no-op. No draft, revision, discount definition, or financial state is changed here.</remarks>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects delivery for a reason other than an identical edit.</exception>
    /// <exception cref="OperationCanceledException">The outer handler or Telegram delivery is cancelled.</exception>
    /// <example><code>await ShowOwnerDiscountMessageAsync(client, ownerChatId, menuMessageId, text, addressedRows, token);</code></example>
    private static async Task ShowOwnerDiscountMessageAsync(ITelegramBotClient client, long chat, int? messageId,
        string text, IEnumerable<InlineKeyboardButton[]> rows, CancellationToken token)
    {
        var keyboard = new InlineKeyboardMarkup(rows);
        if (messageId.HasValue)
            await EditMessageTextAllowNoOpAsync(client, chat, messageId.Value, text, parseMode: ParseMode.Html,
                replyMarkup: keyboard, cancellationToken: token);
        else await client.SendMessage(chat, text, parseMode: ParseMode.Html, replyMarkup: keyboard,
            cancellationToken: token);
    }

    /// <summary>Constructs a short addressed owner button; checked against Telegram's 64-byte UTF-8 callback limit.</summary>
    /// <param name="store">Current selected tenant bot, encoded with its store number and revision.</param>
    /// <param name="caption">Human-readable owner button text, not part of authorization.</param>
    /// <param name="action">Short callback action validated again on receipt.</param>
    /// <returns>An inline button addressed to this storefront's owner panel.</returns>
    private static InlineKeyboardButton DiscountOwnerButton(BotInstance store, string caption, string action) =>
        InlineKeyboardButton.WithCallbackData(caption, TenantOwnerCallback.Encode(store, action));

    /// <summary>Reads only the selected-store draft from the current owned-bot/user state.</summary>
    /// <param name="state">Current bot-scoped owner conversation, which may have no draft.</param>
    /// <param name="storeId">Internal id of the currently selected tenant bot.</param>
    /// <returns>The draft only if its stored tenant and nonce match, otherwise null.</returns>
    private static OwnerDiscountDraft ReadOwnerDiscountDraft(User state, string storeId)
    {
        if (string.IsNullOrWhiteSpace(state?.OwnerDiscountDraftJson)) return null;
        try
        {
            var draft = JsonConvert.DeserializeObject<OwnerDiscountDraft>(state.OwnerDiscountDraftJson);
            return draft?.TenantBotId == storeId && draft.Nonce is { Length: 8 } ? draft : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Saves an unsent, unsaved owner draft in the selected bot/user conversation.</summary>
    /// <param name="ownerId">Numeric Telegram user id of the authenticated storefront owner.</param>
    /// <param name="draft">Unsaved draft scoped to the owner's selected bot.</param>
    /// <param name="step">Expected next owner input, empty when the draft menu is displayed.</param>
    /// <param name="token">Cancellation token for the bot-state persistence operation.</param>
    /// <returns>A task after the owner conversation state is saved.</returns>
    private Task SaveOwnerDiscountDraftAsync(long ownerId, OwnerDiscountDraft draft, string step, CancellationToken token) =>
        _state.SaveUserStatus(new User { Id = ownerId, Flow = OWNERFLOW, OwnerStoreId = draft.TenantBotId,
            OwnerDiscountDraftJson = JsonConvert.SerializeObject(draft), LastStep = step });

    /// <summary>Encodes a positive internal code id in base 36; zero is reserved for a new draft.</summary>
    /// <param name="id">Positive internal code or quote database id; zero denotes a new draft.</param>
    /// <returns>Compact lowercase ASCII base-36 identity for Telegram callback data.</returns>
    private static string DiscountId(int id)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (id <= 0) return "0";
        Span<char> buffer = stackalloc char[7];
        var length = 0;
        while (id > 0) { buffer[buffer.Length - ++length] = digits[id % 36]; id /= 36; }
        return new string(buffer[^length..]);
    }

    /// <summary>Parses a compact callback id without allowing signed or overflowing integers.</summary>
    /// <param name="text">Untrusted lowercase base-36 callback identity, at most seven characters.</param>
    /// <param name="id">Receives a nonnegative internal code or quote id on success.</param>
    /// <returns>True only if the complete string fits a nonnegative 32-bit id.</returns>
    private static bool ParseDiscountId(string text, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 7) return false;
        foreach (var c in text)
        {
            var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : -1;
            if (digit < 0 || id > (int.MaxValue - digit) / 36) return false;
            id = id * 36 + digit;
        }
        return true;
    }

    /// <summary>Converts a typed validation result into an owner-visible, non-sensitive Persian explanation.</summary>
    /// <param name="failure">Named discount validation, capacity, ownership, or concurrency failure.</param>
    /// <returns>An owner-visible Persian explanation without customer or credential details.</returns>
    private static string DiscountOwnerError(TenantDiscountFailure failure) => failure switch
    {
        TenantDiscountFailure.InvalidCode => "کد باید ۳ تا ۳۲ حرف لاتین، عدد، _ یا - باشد.",
        TenantDiscountFailure.InvalidDefinition => "نوع، مقدار، حداقل سفارش، حداکثر استفاده و کاربرد را کامل و معتبر وارد کنید.",
        TenantDiscountFailure.DuplicateCode => "این کد در همین فروشگاه وجود دارد.",
        TenantDiscountFailure.Exhausted => "حداکثر استفاده نمی‌تواند کمتر از تعداد مصرف‌شده و رزروشده باشد.",
        TenantDiscountFailure.Conflict or TenantDiscountFailure.ChangedQuote => "کد در جای دیگری تغییر کرده است؛ داده‌های جدید را ببینید.",
        TenantDiscountFailure.NotFound => "این کد دیگر در فروشگاه وجود ندارد.",
        TenantDiscountFailure.Unauthorized => "دسترسی به این فروشگاه تأیید نشد.",
        _ => "درخواست تخفیف معتبر نیست."
    };

    /// <summary>Formats a definition's purchase/renew eligibility for the owner without exposing code internals.</summary>
    /// <param name="scope">Persisted purchase, renewal, or both eligibility value.</param>
    /// <returns>Localized owner-panel label or an unconfigured placeholder.</returns>
    private static string DiscountScopeText(string scope) => scope switch
    {
        TenantDiscountScopes.Purchase => "خرید", TenantDiscountScopes.Renew => "تمدید",
        TenantDiscountScopes.Both => "خرید و تمدید", _ => "ثبت نشده"
    };
}
