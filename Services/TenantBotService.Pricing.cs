using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Provides an authenticated, bot-scoped draft editor for tenant storefront prices.</summary>
/// <remarks>The owned-bot owner routes callbacks here only after verifying the sender, selected store and callback revision. Drafts never affect customer checkout until committed in users.db.</remarks>
public partial class TenantBotService
{
    /// <summary>One stable catalog identity displayed in the owner's unlimited-price list.</summary>
    private sealed class OwnerPricingPlanKey
    {
        /// <summary>Exact catalog service key; display names are never persisted as identities.</summary>
        public string ServiceKey { get; set; }
        /// <summary>Exact catalog plan key inside its service.</summary>
        public string PlanKey { get; set; }
    }

    /// <summary>Unsaved, selected-store owner prices with a revision and short callback nonce.</summary>
    /// <remarks>Ordered plan identities are frozen for the lifetime of this draft so catalog reorder cannot redirect an old button to a different plan.</remarks>
    private sealed class OwnerPricingDraft
    {
        /// <summary>Internal id of the selected tenant bot, never a Telegram bot or user id.</summary>
        public string TenantBotId { get; set; }
        /// <summary>Original effective store revision in UTC ticks, checked inside the write transaction.</summary>
        public long RevisionTicks { get; set; }
        /// <summary>Eight hexadecimal characters binding edit and save callbacks to this draft.</summary>
        public string Nonce { get; set; }
        /// <summary>Staged normal-service whole-toman price per GB, or null if unconfigured.</summary>
        public long? NormalGb { get; set; }
        /// <summary>Staged normal-service whole-toman price per day, or null if unconfigured.</summary>
        public long? NormalDay { get; set; }
        /// <summary>Staged national-service whole-toman price per GB, or null if unconfigured.</summary>
        public long? NationalGb { get; set; }
        /// <summary>Staged unlimited prices in whole toman, keyed first by service and then plan.</summary>
        public Dictionary<string, Dictionary<string, long>> Unlimited { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Catalog-ordered, tenant-visible plan identities captured when the draft opened.</summary>
        public List<OwnerPricingPlanKey> Plans { get; set; } = new();
        /// <summary>Identifiers edited in this session; percent saves check these without invalidating old inactive prices.</summary>
        public HashSet<string> EditedRateKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Fixed rate code ng/nd/ig, or null for an unlimited plan input.</summary>
        public string SelectedRate { get; set; }
        /// <summary>Selected unlimited service key, retained independently of its former list index.</summary>
        public string SelectedServiceKey { get; set; }
        /// <summary>Selected unlimited plan key, retained independently of its former list index.</summary>
        public string SelectedPlanKey { get; set; }
        /// <summary>Zero-based eight-plan page currently shown to the owner.</summary>
        public int Page { get; set; }
    }

    /// <summary>Handles one previously authorized pricing callback for the selected owner's storefront.</summary>
    /// <param name="client">Owned-bot transport decorated with the selected storefront's owner panel.</param>
    /// <param name="callback">Authenticated owner's Telegram callback with a fresh revision-bound envelope.</param>
    /// <param name="owner">Colleague credentials whose Telegram user id owns the selected tenant bot.</param>
    /// <param name="state">Current bot/user conversation; never a customer conversation.</param>
    /// <param name="action">Decoded short p: action; it is not an authorization grant.</param>
    /// <param name="token">Cancellation of state, catalog, database and Telegram operations.</param>
    /// <returns>A task after replying with an edit prompt, result or owner-only pricing menu.</returns>
    /// <remarks>Store membership and revision are checked again before committing. Cancel discards only unsaved owner state.</remarks>
    /// <example><code>await HandleOwnerPricingCallbackAsync(client, callback, owner, state, "p:open", token);</code></example>
    private async Task HandleOwnerPricingCallbackAsync(ITelegramBotClient client, CallbackQuery callback,
        CredUser owner, User state, string action, CancellationToken token)
    {
        var store = await RequireSelectedOwnerStoreAsync(owner, token);
        var chat = callback.Message?.Chat.Id ?? callback.From.Id;
        var messageId = callback.Message?.MessageId;
        if (action == "p:cancel")
        {
            await _state.SaveUserStatus(new User { Id = owner.TelegramUserId, OwnerPricingDraftJson = "", LastStep = "" });
            await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
            await SHOWOWNERPANELASYNC(client, chat, owner, null, token);
            return;
        }
        var catalog = _purchaseService.LoadCatalog();
        OwnerPricingDraft draft;
        string error = null;
        if (action == "p:open")
        {
            try
            {
                draft = new OwnerPricingDraft
                {
                    TenantBotId = store.Id, RevisionTicks = (store.UpdatedAtUtc ?? store.CreatedAtUtc).Ticks,
                    Nonce = Guid.NewGuid().ToString("N")[..8], NormalGb = store.TenantNormalPricePerGbToman,
                    NormalDay = store.TenantNormalPricePerDayToman, NationalGb = store.TenantNationalPricePerGbToman,
                    Unlimited = TenantStorefrontPricing.ParseUnlimitedPlanPrices(store.TenantUnlimitedPlanPricesJson)
                };
            }
            catch (TenantPriceUnavailableException ex)
            {
                await SafeAnswerCallbackQueryAsync(client, callback.Id, "قیمت‌های ذخیره‌شده نامعتبر است.", showAlert: true, cancellationToken: token);
                await PricingMessageAsync(client, chat, messageId, $"⚠️ تنظیمات قیمت ذخیره‌شده قابل خواندن نیست: {Html(ex.Message)}", store, null, token);
                return;
            }
            draft.Plans = CatalogPricingPlans(catalog);
            if (JsonConvert.SerializeObject(draft).Length > 16384)
            {
                await SafeAnswerCallbackQueryAsync(client, callback.Id, "فهرست قیمت‌ها از ظرفیت پیش‌نویس بیشتر است.", showAlert: true, cancellationToken: token);
                await PricingMessageAsync(client, chat, messageId, "⚠️ فهرست قیمت‌ها از ظرفیت پیش‌نویس بیشتر است؛ تنظیمات ذخیره‌شده تغییر نکرد.", store, null, token);
                return;
            }
            await SavePricingDraftAsync(owner.TelegramUserId, draft, "", token);
        }
        else
        {
            draft = ReadPricingDraft(await _state.GetUserStatus(owner.TelegramUserId), store);
            var parts = action.Split(':');
            if (draft == null || draft.RevisionTicks != (store.UpdatedAtUtc ?? store.CreatedAtUtc).Ticks)
            {
                draft = null;
                error = "پیش‌نویس یا نسخه فروشگاه قدیمی است؛ قیمت‌گذاری را دوباره باز کنید.";
            }
            else if (parts.Length == 4 && parts[1] == "r" && parts[3] == draft.Nonce &&
                parts[2] is "ng" or "nd" or "ig")
            {
                var service = PricingService(catalog, parts[2] == "ig" ? "national" : "normal");
                if (service == null || service.IsUnlimited)
                {
                    error = "این خدمت دیگر فعال نیست؛ فهرست قیمت‌ها تازه‌سازی شد.";
                    draft.Plans = CatalogPricingPlans(catalog);
                    draft.Nonce = Guid.NewGuid().ToString("N")[..8];
                    draft.Page = 0;
                    await SavePricingDraftAsync(owner.TelegramUserId, draft, "", token);
                }
                else
                {
                    draft.SelectedRate = parts[2]; draft.SelectedServiceKey = null; draft.SelectedPlanKey = null;
                    await SavePricingDraftAsync(owner.TelegramUserId, draft, "pricing-rate", token);
                    await PricingPromptAsync(client, chat, service.DisplayName, parts[2] == "nd" ? "روز" : "GB",
                        parts[2] == "nd" ? service.GetPricePerDay(true) : service.GetPricePerGb(true), token);
                    await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
                    return;
                }
            }
            else if (parts.Length == 4 && parts[1] == "u" && parts[3] == draft.Nonce &&
                ParsePricingIndex(parts[2], out var index) && index < draft.Plans.Count)
            {
                var key = draft.Plans[index];
                var plan = PricingPlan(catalog, key);
                if (plan == null)
                {
                    error = "این پلن حذف یا پنهان شده است؛ فهرست قیمت‌ها تازه‌سازی شد.";
                    draft.Plans = CatalogPricingPlans(catalog);
                    draft.Nonce = Guid.NewGuid().ToString("N")[..8];
                    draft.Page = 0;
                    await SavePricingDraftAsync(owner.TelegramUserId, draft, "", token);
                }
                else
                {
                    draft.SelectedRate = null; draft.SelectedServiceKey = key.ServiceKey; draft.SelectedPlanKey = key.PlanKey;
                    draft.Page = index / 8;
                    await SavePricingDraftAsync(owner.TelegramUserId, draft, "pricing-rate", token);
                    await PricingPromptAsync(client, chat, plan.DisplayName, "پلن", plan.Price.Colleague, token);
                    await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
                    return;
                }
            }
            else if (parts.Length == 4 && parts[1] == "page" && parts[3] == draft.Nonce &&
                ParsePricingIndex(parts[2], out var page) && page < Math.Max(1, (draft.Plans.Count + 7) / 8))
            {
                draft.Page = page;
                await SavePricingDraftAsync(owner.TelegramUserId, draft, "", token);
            }
            else if (parts.Length == 4 && parts[1] == "save" && parts[3] == draft.Nonce && parts[2] is "p" or "m")
            {
                error = await CommitPricingDraftAsync(owner, store, draft, parts[2] == "m", token);
                if (error == null)
                {
                    await _state.SaveUserStatus(new User { Id = owner.TelegramUserId, OwnerPricingDraftJson = "", LastStep = "" });
                    await _workflow.ReloadAsync(store, token);
                    _selectedOwnerStore = store;
                    _botRegistry.Upsert(store);
                    await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
                    await client.SendMessage(chat, parts[2] == "m" ? "✅ قیمت‌گذاری دستی فعال شد." : "✅ قیمت‌گذاری درصدی ذخیره شد.", cancellationToken: token);
                    await SHOWOWNERPANELASYNC(client, chat, owner, null, token);
                    return;
                }
            }
            else error = "این دکمه قدیمی یا نامعتبر است؛ ویرایشگر را دوباره باز کنید.";
        }
        await SafeAnswerCallbackQueryAsync(client, callback.Id, error, showAlert: error != null, cancellationToken: token);
        await ShowPricingDraftAsync(client, chat, messageId, store, draft, catalog, error, token);
    }

    /// <summary>Validates and stages a single owner-entered whole-toman rate without touching live prices.</summary>
    /// <param name="client">Authenticated owned-bot panel transport.</param>
    /// <param name="message">Owner's text update containing digits only, no separators or sign.</param>
    /// <param name="owner">Colleague owner; the selected tenant bot must still belong to this Telegram user id.</param>
    /// <param name="state">Original bot/user-scoped conversation used by the owner dispatcher; the step is reread before applying input.</param>
    /// <param name="token">Cancellation of state, catalog and Telegram operations.</param>
    /// <returns>A task after an error prompt or updated owner-only draft is delivered.</returns>
    /// <remarks>Invalid input retains the step. Changed catalog identities never assign a selected index to a new plan.</remarks>
    /// <example><code>await HandleOwnerPricingTextAsync(client, message, owner, state, token);</code></example>
    private async Task HandleOwnerPricingTextAsync(ITelegramBotClient client, Message message, CredUser owner,
        User state, CancellationToken token)
    {
        var store = await RequireSelectedOwnerStoreAsync(owner, token);
        var currentState = await _state.GetUserStatus(owner.TelegramUserId);
        if (currentState?.LastStep != "pricing-rate")
        {
            await client.SendMessage(message.Chat.Id, "مرحله ورود قیمت تغییر کرده است؛ ویرایشگر را دوباره باز کنید.", cancellationToken: token);
            return;
        }
        var draft = ReadPricingDraft(currentState, store);
        if (draft == null || draft.RevisionTicks != (store.UpdatedAtUtc ?? store.CreatedAtUtc).Ticks)
        {
            await client.SendMessage(message.Chat.Id, "پیش‌نویس قیمت قدیمی است؛ ویرایشگر را دوباره باز کنید.", cancellationToken: token);
            await _state.SaveUserStatus(new User { Id = owner.TelegramUserId, OwnerPricingDraftJson = "", LastStep = "" });
            return;
        }
        var catalog = _purchaseService.LoadCatalog();
        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(c => c is < '0' or > '9') ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            await client.SendMessage(message.Chat.Id, "فقط عدد صحیح نامنفی با رقم لاتین و بدون جداکننده به تومان وارد کنید.", cancellationToken: token);
            return;
        }
        var rate = draft.SelectedRate;
        var service = PricingService(catalog, rate == "ig" ? "national" : "normal");
        XuiV3UnlimitedPlan plan = null;
        if (rate == null && draft.SelectedServiceKey != null && draft.SelectedPlanKey != null)
            plan = PricingPlan(catalog, new OwnerPricingPlanKey { ServiceKey = draft.SelectedServiceKey, PlanKey = draft.SelectedPlanKey });
        if (rate is not ("ng" or "nd" or "ig") && plan == null || rate != null && (service == null || service.IsUnlimited))
        {
            await client.SendMessage(message.Chat.Id, "این خدمت یا پلن دیگر فعال نیست؛ فهرست قیمت‌ها تازه‌سازی شد.", cancellationToken: token);
            draft.Plans = CatalogPricingPlans(catalog);
            draft.Nonce = Guid.NewGuid().ToString("N")[..8];
            draft.Page = 0;
            draft.SelectedRate = null; draft.SelectedServiceKey = null; draft.SelectedPlanKey = null;
            await SavePricingDraftAsync(owner.TelegramUserId, draft, "", token);
            await ShowPricingDraftAsync(client, message.Chat.Id, null, store, draft, catalog, null, token);
            return;
        }
        var colleague = rate == "nd" ? service.GetPricePerDay(true) : rate == "ng" || rate == "ig"
            ? service.GetPricePerGb(true) : plan.Price.Colleague;
        if (amount < colleague || amount == 0 && rate != "nd")
        {
            await client.SendMessage(message.Chat.Id,
                $"قیمت مشتری باید {(rate == "nd" && colleague == 0 ? "نامنفی" : "مثبت")} و حداقل قیمت همکار ({colleague:N0} تومان) باشد.", cancellationToken: token);
            return;
        }
        if (rate == "ng") draft.NormalGb = amount;
        else if (rate == "nd") draft.NormalDay = amount;
        else if (rate == "ig") draft.NationalGb = amount;
        else
        {
            if (!draft.Unlimited.TryGetValue(draft.SelectedServiceKey, out var plans))
                draft.Unlimited[draft.SelectedServiceKey] = plans = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            plans[draft.SelectedPlanKey] = amount;
        }
        draft.EditedRateKeys.Add(PricingEditedKey(rate, draft.SelectedServiceKey, draft.SelectedPlanKey));
        draft.SelectedRate = null; draft.SelectedServiceKey = null; draft.SelectedPlanKey = null;
        if (JsonConvert.SerializeObject(draft).Length > 16384)
        {
            await client.SendMessage(message.Chat.Id, "حجم پیش‌نویس از ظرفیت مجاز بیشتر است؛ قیمت‌های ذخیره‌شده تغییر نکرد.", cancellationToken: token);
            return;
        }
        await SavePricingDraftAsync(owner.TelegramUserId, draft, "", token);
        await client.SendMessage(message.Chat.Id,
            $"قیمت همکار: {colleague:N0} تومان | قیمت مشتری در پیش‌نویس: {amount:N0} تومان. تا ذخیره نهایی اعمال نمی‌شود.",
            replyMarkup: new ReplyKeyboardRemove(), cancellationToken: token);
        await ShowPricingDraftAsync(client, message.Chat.Id, null, store, draft, catalog, null, token);
    }

    /// <summary>Commits a selected owner's draft atomically after reloading store and catalog.</summary>
    /// <param name="owner">Authenticated colleague whose Telegram user id owns the selected store.</param>
    /// <param name="store">Selected tenant bot carrying the revision presented to this owner.</param>
    /// <param name="draft">Unsaved owner state with original revision and edited-rate identities.</param>
    /// <param name="manual">True to activate fully priced manual mode; false to save partial rates in percent mode.</param>
    /// <param name="token">Cancellation of the users.db writer and catalog load.</param>
    /// <returns>Null after a committed save, or a precise owner-visible error with no store changes.</returns>
    /// <remarks>The same users.db transaction compares the revision, checks all edited rates against current costs and writes all fields. An incomplete manual draft never changes the active mode.</remarks>
    /// <example><code>var error = await CommitPricingDraftAsync(owner, store, draft, manual: true, token);</code></example>
    private Task<string> CommitPricingDraftAsync(CredUser owner, BotInstance store, OwnerPricingDraft draft, bool manual, CancellationToken token) =>
        _workflow.WriteAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // Check the exact tenant and authenticated owner inside the writer transaction; callback envelopes alone cannot authorize writes.
            var current = await db.BotInstances.SingleOrDefaultAsync(x => x.Id == store.Id && x.Type == BotInstanceTypes.Tenant &&
                x.OwnerTelegramUserId == owner.TelegramUserId, token);
            if (current == null) return "دسترسی به این فروشگاه تأیید نشد.";
            if (draft.TenantBotId != current.Id || draft.RevisionTicks != (current.UpdatedAtUtc ?? current.CreatedAtUtc).Ticks)
                return "تنظیمات فروشگاه در جای دیگری تغییر کرده است؛ ویرایشگر را دوباره باز کنید.";
            var catalog = _purchaseService.LoadCatalog();
            foreach (var key in draft.EditedRateKeys)
            {
                var error = ValidateEditedPricingKey(draft, catalog, key);
                if (error != null) return error;
            }
            var json = JsonConvert.SerializeObject(draft.Unlimited);
            if (json.Length > 8192) return "حجم قیمت‌های پلن‌ها از ظرفیت مجاز بیشتر است؛ قیمت‌ها ذخیره نشد.";
            if (manual)
            {
                var probe = new BotInstance
                {
                    TenantNormalPricePerGbToman = draft.NormalGb, TenantNormalPricePerDayToman = draft.NormalDay,
                    TenantNationalPricePerGbToman = draft.NationalGb
                };
                var missing = TenantStorefrontPricing.ValidateManualCompleteness(probe, catalog, draft.Unlimited);
                if (missing.Count > 0) return "قیمت‌گذاری دستی کامل نیست: " + string.Join("؛ ", missing);
            }
            current.TenantPricingMode = manual ? TenantPricingModes.Manual : TenantPricingModes.Percent;
            current.TenantNormalPricePerGbToman = draft.NormalGb;
            current.TenantNormalPricePerDayToman = draft.NormalDay;
            current.TenantNationalPricePerGbToman = draft.NationalGb;
            current.TenantUnlimitedPlanPricesJson = json;
            // Advance the revision even if this writer observes the same clock tick as a previous store edit.
            var now = DateTime.UtcNow;
            var previous = current.UpdatedAtUtc ?? current.CreatedAtUtc;
            current.UpdatedAtUtc = now > previous ? now : previous.AddTicks(1);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return null;
        }, token);

    /// <summary>Checks one edited field against its current colleague floor and catalog identity.</summary>
    /// <param name="draft">Owner's unsaved prices and stable selected plan keys.</param>
    /// <param name="catalog">Fresh validated global catalog, never a callback-provided plan.</param>
    /// <param name="key">Fixed rate code or service/plan identity edited in this session.</param>
    /// <returns>A precise owner-facing error, or null when this edited value remains valid.</returns>
    /// <remarks>Only newly edited fields are required for percent saves; old inactive stale values remain visible for later correction.</remarks>
    /// <example><code>var error = ValidateEditedPricingKey(draft, catalog, "ng");</code></example>
    private static string ValidateEditedPricingKey(OwnerPricingDraft draft, XuiV3ServicePlanCatalog catalog, string key)
    {
        if (key is "ng" or "nd" or "ig")
        {
            var service = PricingService(catalog, key == "ig" ? "national" : "normal");
            if (service == null || service.IsUnlimited) return "خدمت ویرایش‌شده دیگر فعال نیست؛ ویرایشگر را دوباره باز کنید.";
            var floor = key == "nd" ? service.GetPricePerDay(true) : service.GetPricePerGb(true);
            var amount = key == "ng" ? draft.NormalGb : key == "nd" ? draft.NormalDay : draft.NationalGb;
            if (amount is null || amount < floor || amount == 0 && key != "nd")
                return $"نرخ {service.DisplayName} ({(key == "nd" ? "روز" : "GB")}) باید حداقل قیمت همکار {floor:N0} تومان باشد.";
            return null;
        }
        var identity = draft.Plans.FirstOrDefault(p => PricingEditedKey(null, p.ServiceKey, p.PlanKey) == key);
        if (identity == null) return "شناسه پلن ویرایش‌شده نامعتبر است؛ ویرایشگر را دوباره باز کنید.";
        var plan = PricingPlan(catalog, identity);
        if (plan == null) return $"پلن {identity.ServiceKey}/{identity.PlanKey} حذف یا پنهان شده است؛ ویرایشگر را دوباره باز کنید.";
        if (!draft.Unlimited.TryGetValue(identity.ServiceKey, out var prices) ||
            !prices.TryGetValue(identity.PlanKey, out var amountPlan) || amountPlan <= 0 || amountPlan < plan.Price.Colleague)
            return $"قیمت پلن {plan.DisplayName} باید حداقل قیمت همکار {plan.Price.Colleague:N0} تومان باشد.";
        return null;
    }

    /// <summary>Renders the current owner-only pricing draft and eight-plan page.</summary>
    /// <param name="client">Owned-bot owner transport; never tenant customer transport.</param>
    /// <param name="chat">Authenticated owner's Telegram chat id.</param>
    /// <param name="messageId">Existing editor message to edit, or null to send another message.</param>
    /// <param name="store">Selected tenant bot whose revision addresses all generated callback buttons.</param>
    /// <param name="draft">Staged rates, or null when no valid current draft exists.</param>
    /// <param name="catalog">Current globally validated catalog with colleague and public rates.</param>
    /// <param name="error">Optional owner-only validation failure displayed above prices.</param>
    /// <param name="token">Cancellation of Telegram delivery.</param>
    /// <returns>A task after displaying both colleague and customer amounts to the owner.</returns>
    /// <remarks>Draft values are labelled as proposed manual prices. In percentage mode current customer amounts use public rates at zero and colleague markup otherwise.</remarks>
    /// <example><code>await ShowPricingDraftAsync(client, chat, null, store, draft, catalog, null, token);</code></example>
    private async Task ShowPricingDraftAsync(ITelegramBotClient client, long chat, int? messageId, BotInstance store,
        OwnerPricingDraft draft, XuiV3ServicePlanCatalog catalog, string error, CancellationToken token)
    {
        if (draft == null)
        {
            await PricingMessageAsync(client, chat, messageId, "⚠️ " + Html(error ?? "پیش‌نویس پیدا نشد؛ دوباره باز کنید."), store, null, token);
            return;
        }
        var text = new StringBuilder("💰 <b>قیمت‌گذاری فروشگاه</b>\nتا ذخیره نهایی قیمت مشتری تغییر نمی‌کند.\n");
        text.Append("حالت فعال: ").Append(store.TenantPricingMode switch
        {
            TenantPricingModes.Manual => "دستی",
            TenantPricingModes.Percent => "درصدی",
            _ => "نامعتبر؛ قیمت مشتری در دسترس نیست"
        }).Append(" | درصد فعلی: ").Append(store.TenantPriceMarkupPercent).Append("٪\n");
        if (error != null) text.Append("⚠️ ").Append(Html(error)).Append('\n');
        var savedUnlimited = store.TenantPricingMode == TenantPricingModes.Manual
            ? TenantStorefrontPricing.ParseUnlimitedPlanPrices(store.TenantUnlimitedPlanPricesJson)
            : new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var (code, label, serviceKey, day, staged) in new (string, string, string, bool, long?)[]
        {
            ("ng", "عادی / GB", "normal", false, draft.NormalGb),
            ("nd", "عادی / روز", "normal", true, draft.NormalDay),
            ("ig", "ملی / GB", "national", false, draft.NationalGb)
        })
        {
            var service = PricingService(catalog, serviceKey);
            if (service == null || service.IsUnlimited) continue;
            var colleague = day ? service.GetPricePerDay(true) : service.GetPricePerGb(true);
            var publicRate = day ? service.GetPricePerDay(false) : service.GetPricePerGb(false);
            var currentText = store.TenantPricingMode == TenantPricingModes.Manual
                ? PricingAmount(code == "ng" ? store.TenantNormalPricePerGbToman
                    : code == "nd" ? store.TenantNormalPricePerDayToman : store.TenantNationalPricePerGbToman)
                : store.TenantPricingMode == TenantPricingModes.Percent
                    ? store.TenantPriceMarkupPercent == 0 ? PricingAmount(publicRate)
                        : FormatTenantPriceAmount(colleague * (1M + store.TenantPriceMarkupPercent / 100M))
                    : "در دسترس نیست";
            text.Append(Html(service.DisplayName ?? label)).Append(" / ").Append(day ? "روز" : "GB")
                .Append(": قیمت همکار ").Append(colleague.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" تومان | قیمت مشتری ").Append(currentText).Append(" | پیش‌نویس دستی ").Append(PricingAmount(staged));
            if (staged.HasValue && (staged < colleague || staged == 0 && !day)) text.Append(" ⚠️ کمتر از قیمت همکار");
            text.Append('\n');
            rows.Add(new[] { PricingButton(store, label, $"p:r:{code}:{draft.Nonce}") });
        }
        var first = draft.Page * 8;
        for (var i = first; i < Math.Min(first + 8, draft.Plans.Count); i++)
        {
            var identity = draft.Plans[i];
            var plan = PricingPlan(catalog, identity);
            if (plan == null)
            {
                text.Append(Html(identity.ServiceKey)).Append('/').Append(Html(identity.PlanKey)).Append(": پلن حذف یا پنهان شده؛ دوباره باز کنید.\n");
                continue;
            }
            draft.Unlimited.TryGetValue(identity.ServiceKey, out var stagedPlans);
            long stagedPrice = 0;
            var hasStage = stagedPlans != null && stagedPlans.TryGetValue(identity.PlanKey, out stagedPrice);
            savedUnlimited.TryGetValue(identity.ServiceKey, out var savedPlans);
            long? current = store.TenantPricingMode == TenantPricingModes.Manual
                ? savedPlans != null && savedPlans.TryGetValue(identity.PlanKey, out var savedPrice) ? savedPrice : null
                : store.TenantPricingMode == TenantPricingModes.Percent
                    ? plan.TenantUsesUserPrice || store.TenantPriceMarkupPercent == 0
                        ? plan.Price.User : (long)decimal.Ceiling(plan.Price.Colleague * (1M + store.TenantPriceMarkupPercent / 100M))
                    : null;
            text.Append(Html(PricingService(catalog, identity.ServiceKey)?.DisplayName ?? identity.ServiceKey)).Append(" / ")
                .Append(Html(plan.DisplayName ?? identity.PlanKey)).Append(" / پلن: قیمت همکار ")
                .Append(plan.Price.Colleague.ToString("N0", CultureInfo.InvariantCulture)).Append(" تومان | قیمت مشتری ")
                .Append(PricingAmount(current)).Append(" | پیش‌نویس دستی ")
                .Append(PricingAmount(hasStage ? stagedPrice : null));
            if (hasStage && stagedPrice < plan.Price.Colleague) text.Append(" ⚠️ کمتر از قیمت همکار");
            text.Append('\n');
            rows.Add(new[] { PricingButton(store, "✏️ " + (plan.DisplayName ?? identity.PlanKey), $"p:u:{PricingIndex(i)}:{draft.Nonce}") });
        }
        if (draft.Page > 0) rows.Add(new[] { PricingButton(store, "قبلی", $"p:page:{PricingIndex(draft.Page - 1)}:{draft.Nonce}") });
        if (first + 8 < draft.Plans.Count) rows.Add(new[] { PricingButton(store, "بعدی", $"p:page:{PricingIndex(draft.Page + 1)}:{draft.Nonce}") });
        rows.Add(new[] { PricingButton(store, "✅ ذخیره درصدی", $"p:save:p:{draft.Nonce}"), PricingButton(store, "✅ فعال‌سازی دستی", $"p:save:m:{draft.Nonce}") });
        rows.Add(new[] { PricingButton(store, "انصراف و بازگشت به پنل", "p:cancel") });
        await PricingMessageAsync(client, chat, messageId, text.ToString(), store, rows, token);
    }

    /// <summary>Delivers an owner-only input prompt with the current colleague floor in whole toman.</summary>
    /// <param name="client">Owned-bot Telegram transport.</param>
    /// <param name="chat">Authenticated owner's Telegram chat id.</param>
    /// <param name="name">Current catalog service or plan display name, HTML-escaped before delivery.</param>
    /// <param name="unit">GB, day or plan; a unit is never a price identifier.</param>
    /// <param name="colleague">Fresh colleague unit/plan price in whole toman.</param>
    /// <param name="token">Cancellation of Telegram delivery.</param>
    /// <returns>A task after sending the private rate-entry prompt.</returns>
    /// <remarks>The prompt is sent only to the authenticated owner; colleague rates must not appear in tenant customer messages.</remarks>
    /// <example><code>await PricingPromptAsync(client, chat, "عادی", "GB", 3500, token);</code></example>
    private static Task PricingPromptAsync(ITelegramBotClient client, long chat, string name, string unit, long colleague, CancellationToken token) =>
        client.SendMessage(chat, $"{Html(name)} / {unit}: قیمت همکار {colleague:N0} تومان است. قیمت مشتری برای هر {unit} را با رقم لاتین و بدون جداکننده وارد کنید:",
            parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("بازگشت به پنل") } }) { ResizeKeyboard = true }, cancellationToken: token);

    /// <summary>Sends or edits a revision-addressed private owner menu.</summary>
    /// <param name="client">Decorated owner transport.</param>
    /// <param name="chat">Authenticated owner's Telegram chat id.</param>
    /// <param name="messageId">Message to edit, or null to send a new owner message.</param>
    /// <param name="text">Already HTML-escaped dynamic values and owner-only pricing comparisons.</param>
    /// <param name="store">Selected tenant bot used to address a fallback reopen button.</param>
    /// <param name="rows">Optional callback rows; null offers only the reopen and owner-panel actions.</param>
    /// <param name="token">Cancellation of Telegram delivery.</param>
    /// <returns>A task after delivering the owner menu.</returns>
    /// <remarks>The fallback buttons still require the owner/store/revision checks of the parent callback router.</remarks>
    /// <example><code>await PricingMessageAsync(client, chat, null, text, store, rows, token);</code></example>
    private static Task PricingMessageAsync(ITelegramBotClient client, long chat, int? messageId, string text,
        BotInstance store, IEnumerable<InlineKeyboardButton[]> rows, CancellationToken token)
    {
        var keyboard = new InlineKeyboardMarkup(rows ?? new[]
        {
            new[] { PricingButton(store, "بازکردن دوباره قیمت‌گذاری", "p:open") },
            new[] { PricingButton(store, "بازگشت به پنل", "p:cancel") }
        });
        return messageId.HasValue
            ? client.EditMessageText(chat, messageId.Value, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: token)
            : client.SendMessage(chat, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: token);
    }

    /// <summary>Wraps an owner pricing action in the selected store's bounded revision callback.</summary>
    /// <param name="store">Authenticated selected tenant bot with positive store number.</param>
    /// <param name="caption">Owner-only inline button label.</param>
    /// <param name="action">Short p: action without free-form catalog names.</param>
    /// <returns>Telegram inline callback button tied to this store's revision.</returns>
    /// <remarks>Never put free-form catalog keys into callback data; Telegram limits it to 64 UTF-8 bytes.</remarks>
    /// <example><code>var button = PricingButton(store, "ویرایش", "p:open");</code></example>
    private static InlineKeyboardButton PricingButton(BotInstance store, string caption, string action) =>
        InlineKeyboardButton.WithCallbackData(caption, TenantOwnerCallback.Encode(store, action));

    /// <summary>Reads a bot/user draft only if it belongs to this selected tenant and has a valid nonce.</summary>
    /// <param name="state">Current owner's bot-scoped state, possibly without a pricing draft.</param>
    /// <param name="store">Selected tenant bot id for draft isolation.</param>
    /// <returns>The selected-store draft or null for absent, corrupt or other-store state.</returns>
    /// <remarks>A draft is not authorization: every callback and save checks owner and live store revision separately.</remarks>
    /// <example><code>var draft = ReadPricingDraft(await _state.GetUserStatus(ownerId), store);</code></example>
    private static OwnerPricingDraft ReadPricingDraft(User state, BotInstance store)
    {
        if (string.IsNullOrWhiteSpace(state?.OwnerPricingDraftJson)) return null;
        try
        {
            var draft = JsonConvert.DeserializeObject<OwnerPricingDraft>(state.OwnerPricingDraftJson);
            if (draft?.TenantBotId != store.Id || draft.Nonce is not { Length: 8 } ||
                !draft.Nonce.All(Uri.IsHexDigit) || draft.Unlimited == null || draft.Plans == null || draft.EditedRateKeys == null)
                return null;
            // JSON dictionary constructors do not promise to retain ordinal-ignore-case comparers.
            draft.Unlimited = TenantStorefrontPricing.ParseUnlimitedPlanPrices(JsonConvert.SerializeObject(draft.Unlimited));
            draft.EditedRateKeys = new HashSet<string>(draft.EditedRateKeys, StringComparer.OrdinalIgnoreCase);
            draft.Page = Math.Clamp(draft.Page, 0, Math.Max(0, (draft.Plans.Count - 1) / 8));
            return draft;
        }
        catch (JsonException) { return null; }
        catch (TenantPriceUnavailableException) { return null; }
    }

    /// <summary>Persists unsaved pricing state only in the authenticated owner's current bot/user conversation.</summary>
    /// <param name="ownerId">Authenticated colleague's Telegram user id, not tenant bot id.</param>
    /// <param name="draft">Selected tenant bot's unsaved prices and callback nonce.</param>
    /// <param name="step">Empty menu step or pricing-rate when one number is expected.</param>
    /// <param name="token">Caller cancellation token; the bot-state store currently has no cancellation overload.</param>
    /// <returns>A task after saving the draft, without changing live storefront prices.</returns>
    /// <remarks>Uses the current owned-bot conversation; a different owner bot can hold an independent state for the same Telegram user.</remarks>
    /// <example><code>await SavePricingDraftAsync(ownerId, draft, "pricing-rate", token);</code></example>
    private Task SavePricingDraftAsync(long ownerId, OwnerPricingDraft draft, string step, CancellationToken token) =>
        _state.SaveUserStatus(new User { Id = ownerId, Flow = OWNERFLOW, OwnerStoreId = draft.TenantBotId,
            OwnerPricingDraftJson = JsonConvert.SerializeObject(draft), LastStep = step });

    /// <summary>Gets tenant-visible unlimited keys in the catalog's current display order.</summary>
    /// <param name="catalog">Fresh validated global catalog.</param>
    /// <returns>Stable service/plan identities captured by the draft; can be empty.</returns>
    /// <remarks>Capture these identities once when opening a draft so later catalog reordering cannot repurpose a callback index.</remarks>
    /// <example><code>draft.Plans = CatalogPricingPlans(catalog);</code></example>
    private static List<OwnerPricingPlanKey> CatalogPricingPlans(XuiV3ServicePlanCatalog catalog) =>
        catalog.Services.Where(s => s.IsEnabled && s.IsUnlimited)
            .SelectMany(s => ((IEnumerable<XuiV3UnlimitedPlan>)s.UnlimitedPlans ?? Array.Empty<XuiV3UnlimitedPlan>())
                .Where(XuiV3PurchaseService.IsUnlimitedPlanAvailableForTenant)
                .Select(p => new OwnerPricingPlanKey { ServiceKey = s.Key, PlanKey = p.Key })).ToList();

    /// <summary>Finds an enabled metered or unlimited service by exact catalog key, ignoring case.</summary>
    /// <param name="catalog">Current global catalog.</param>
    /// <param name="key">Stable service key, not a Telegram display name.</param>
    /// <returns>Enabled catalog service or null when removed or disabled.</returns>
    /// <remarks>Only current enabled services are editable; inactive saved values remain stored without enabling checkout.</remarks>
    /// <example><code>var normal = PricingService(catalog, "normal");</code></example>
    private static XuiV3ServiceDefinition PricingService(XuiV3ServicePlanCatalog catalog, string key) =>
        catalog.Services.FirstOrDefault(s => s.IsEnabled && string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the same enabled tenant-visible unlimited plan originally selected by the owner.</summary>
    /// <param name="catalog">Fresh globally validated service catalog.</param>
    /// <param name="identity">Original service and plan keys, never a callback-supplied display name.</param>
    /// <returns>Still-visible plan or null when its service/plan was removed, disabled or hidden.</returns>
    /// <remarks>Both service and plan must still be enabled and tenant-visible; an old list index cannot select another plan.</remarks>
    /// <example><code>var plan = PricingPlan(catalog, draft.Plans[index]);</code></example>
    private static XuiV3UnlimitedPlan PricingPlan(XuiV3ServicePlanCatalog catalog, OwnerPricingPlanKey identity)
    {
        var service = PricingService(catalog, identity.ServiceKey);
        return service?.IsUnlimited == true ? service.UnlimitedPlans?
            .FirstOrDefault(p => XuiV3PurchaseService.IsUnlimitedPlanAvailableForTenant(p) &&
                string.Equals(p.Key, identity.PlanKey, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>Formats a nullable whole-toman owner price without implying that null is zero.</summary>
    /// <param name="price">Optional staged or current price in whole toman.</param>
    /// <returns>Localized unconfigured label or invariant-grouped amount with toman unit.</returns>
    /// <remarks>Used only in authenticated owner messages; missing manual prices are never displayed as free.</remarks>
    /// <example><code>var label = PricingAmount(draft.NormalGb);</code></example>
    private static string PricingAmount(long? price) => price.HasValue
        ? price.Value.ToString("N0", CultureInfo.InvariantCulture) + " تومان" : "ثبت نشده";

    /// <summary>Creates an unambiguous edited-rate identity for fixed or service/plan keys.</summary>
    /// <param name="fixedCode">ng, nd or ig for a metered rate; null for unlimited.</param>
    /// <param name="serviceKey">Unlimited catalog service key when fixedCode is null.</param>
    /// <param name="planKey">Unlimited catalog plan key when fixedCode is null.</param>
    /// <returns>Stable comparison key never sent through Telegram callback data.</returns>
    /// <remarks>The control-character separator is never sent to Telegram; fixed rate codes cannot collide with plan identities.</remarks>
    /// <example><code>var key = PricingEditedKey(null, service.Key, plan.Key);</code></example>
    private static string PricingEditedKey(string fixedCode, string serviceKey, string planKey) =>
        fixedCode ?? "u\u001f" + serviceKey + "\u001f" + planKey;

    /// <summary>Encodes a nonnegative page or plan index in compact lowercase base 36.</summary>
    /// <param name="index">Nonnegative catalog-list index or page index.</param>
    /// <returns>Short callback suffix without service or plan names.</returns>
    /// <remarks>The counterpart parser rejects overflowing or signed callback indexes.</remarks>
    /// <example><code>var suffix = PricingIndex(8);</code></example>
    private static string PricingIndex(int index)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[7];
        var length = 0;
        do { buffer[buffer.Length - ++length] = digits[index % 36]; index /= 36; } while (index > 0);
        return new string(buffer[^length..]);
    }

    /// <summary>Parses a callback index without accepting signs, uppercase aliases or integer overflow.</summary>
    /// <param name="text">Untrusted base-36 callback component of at most seven ASCII characters.</param>
    /// <param name="index">Nonnegative parsed list index on success; zero otherwise.</param>
    /// <returns>True only for a complete nonnegative 32-bit base-36 value.</returns>
    /// <remarks>The index is useful only with the original draft's ordered identities and matching nonce.</remarks>
    /// <example><code>if (ParsePricingIndex(text, out var index) &amp;&amp; index &lt; draft.Plans.Count) { var key = draft.Plans[index]; }</code></example>
    private static bool ParsePricingIndex(string text, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 7) return false;
        foreach (var c in text)
        {
            var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : -1;
            if (digit < 0 || index > (int.MaxValue - digit) / 36) return false;
            index = index * 36 + digit;
        }
        return true;
    }
}
