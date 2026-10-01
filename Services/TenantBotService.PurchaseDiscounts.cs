using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public partial class TenantBotService
{
    /// <summary>Displays a message-bound, unadmitted purchase quote with compact discount and net-priced payment buttons.</summary>
    /// <param name="botClient">Transport for the tenant bot serving this checkout.</param>
    /// <param name="chatId">Actual Telegram chat receiving this customer's preview; may differ from the sender id.</param>
    /// <param name="messageId">Existing selection message to edit, or null to send a new preview.</param>
    /// <param name="tenant">Current tenant storefront; its id scopes the quote.</param>
    /// <param name="selection">Current validated purchase selection, never a callback-provided price.</param>
    /// <param name="customerTelegramUserId">Authenticated Telegram sender id for this checkout, not the chat id.</param>
    /// <param name="token">Cancellation of local database and Telegram operations.</param>
    /// <returns>A task after the preview is bound to the real delivered Telegram message or safely expired.</returns>
    /// <remarks>Creates no order, invoice or reservation. Existing quoted messages are never overwritten by another quote; their buttons remain tied to their original message.</remarks>
    private async Task ShowCustomerDiscountConfirmAsync(ITelegramBotClient botClient, ChatId chatId, int? messageId,
        BotInstance tenant, XuiV3PurchaseSelection selection, long customerTelegramUserId, CancellationToken token)
    {
        var chat = chatId.Identifier ?? throw new InvalidOperationException("A numeric Telegram chat id is required for a purchase quote.");
        var discounts = _serviceProvider.GetRequiredService<TenantDiscountService>();
        var price = CalculateTenantPrice(tenant, selection);
        var key = BUILDPAYACTION(selection);
        TenantDiscountQuote quote = null;
        if (messageId is > 0 && await discounts.HasQuoteForMessageAsync(tenant.Id, customerTelegramUserId, chat, messageId.Value, token))
        {
            var existing = await _workflow.ReadAsync(db => db.TenantDiscountQuotes.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantBotId == tenant.Id && x.CustomerTelegramUserId == customerTelegramUserId
                    && x.ChatId == chat && x.MessageId == messageId.Value, token));
            if (existing?.State == TenantDiscountQuoteStates.Open && existing.ExpiresAtUtc > DateTime.UtcNow
                && existing.SelectionKey == key && existing.GrossToman == price.SalePriceToman
                && existing.BaseCostToman == price.BaseCostToman)
                quote = existing;
            else
                messageId = null; // Never overwrite a bound/admitted/expired quote or its tombstone.
        }
        quote ??= await discounts.CreateQuoteAsync(tenant.Id, customerTelegramUserId, chat, key,
            price.SalePriceToman, price.BaseCostToman, token);
        await RenderPurchaseDiscountQuoteAsync(botClient, chatId, messageId, tenant, selection, quote, token);
    }

    /// <summary>Starts code entry or removes a code only for the sender's live, exact-message purchase quote.</summary>
    /// <param name="botClient">Tenant storefront Telegram transport.</param>
    /// <param name="callback">Sender and original message of the pressed inline button.</param>
    /// <param name="tenant">Storefront that owns the quote.</param>
    /// <param name="customer">Authenticated customer whose Telegram id must match the callback sender.</param>
    /// <param name="action">Customer action without TN: prefix, either DC:&lt;id36&gt; or DR:&lt;id36&gt;.</param>
    /// <param name="token">Cancellation of database and Telegram work.</param>
    /// <returns>A task after the entry prompt, undiscounted re-render or stale-button alert.</returns>
    /// <remarks>No payment admission occurs here. Expired and admitted quotes cannot be reactivated by old buttons.</remarks>
    private async Task HandlePurchaseDiscountCallbackAsync(ITelegramBotClient botClient, CallbackQuery callback,
        BotInstance tenant, CredUser customer, string action, CancellationToken token)
    {
        var parts = action?.Split(':');
        if (parts is not { Length: 2 } || parts[0] is not ("DC" or "DR")
            || !ParseDiscountId(parts[1], out var id) || id <= 0
            || callback.From.Id != customer.TelegramUserId || callback.Message == null)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "پیش‌فاکتور معتبر نیست؛ خرید را دوباره آغاز کنید.", showAlert: true, cancellationToken: token);
            return;
        }
        var chat = callback.Message.Chat.Id;
        var messageId = callback.Message.MessageId;
        var quote = await LoadPurchaseDiscountQuoteAsync(id, tenant.Id, customer.TelegramUserId, chat, messageId, token);
        if (quote == null || quote.State != TenantDiscountQuoteStates.Open || quote.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "این پیش‌فاکتور منقضی شده یا قبلاً ثبت شده است؛ خرید را دوباره آغاز کنید.", showAlert: true, cancellationToken: token);
            return;
        }
        var selection = PARSESELECTIONFROMPAYACTION(quote.SelectionKey);
        if (!TryCurrentPurchaseQuotePrice(tenant, selection, quote, out var price))
        {
            await _serviceProvider.GetRequiredService<TenantDiscountService>()
                .ExpireQuoteAsync(id, tenant.Id, customer.TelegramUserId, chat, token);
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "تعرفه تغییر کرده است؛ خرید را دوباره آغاز کنید.", showAlert: true, cancellationToken: token);
            return;
        }
        var discounts = _serviceProvider.GetRequiredService<TenantDiscountService>();
        if (parts[0] == "DC")
        {
            await _state.SaveUserStatus(new User { Id = customer.TelegramUserId, Flow = TENANTPURCHASEFLOW,
                LastStep = "purchase-discount-entry", PurchaseDiscountQuoteId = id });
            await botClient.SendMessage(chat, "کد تخفیف را با حروف لاتین وارد کنید. برای حذف کد از دکمه پیش‌فاکتور استفاده کنید.", cancellationToken: token);
        }
        else
        {
            var removed = await discounts.SelectQuoteCodeAsync(id, tenant.Id, customer.TelegramUserId, chat,
                messageId, quote.SelectionKey, null, price.SalePriceToman, price.BaseCostToman, token);
            if (!removed.Success)
            {
                await SafeAnswerCallbackQueryAsync(botClient, callback.Id, PurchaseDiscountError(removed.Failure), showAlert: true, cancellationToken: token);
                return;
            }
            var state = await _state.GetUserStatus(customer.TelegramUserId);
            if (state.PurchaseDiscountQuoteId == id)
                await _state.SaveUserStatus(new User { Id = customer.TelegramUserId, Flow = "", LastStep = "", PurchaseDiscountQuoteId = 0 });
            await RenderPurchaseDiscountQuoteAsync(botClient, chat, messageId, tenant, selection, removed.Value, token);
        }
        await SafeAnswerCallbackQueryAsync(botClient, callback.Id, cancellationToken: token);
    }

    /// <summary>Applies entered text to the exact bot/customer/chat/message-bound quote and re-renders its realized net.</summary>
    /// <param name="botClient">Tenant bot transport for rendering the quote or a concrete retry reason.</param>
    /// <param name="message">Customer's text message in the quote's Telegram chat.</param>
    /// <param name="tenant">Current tenant bot controlling the quoted purchase.</param>
    /// <param name="customer">Authenticated shared customer, never inferred from entered text.</param>
    /// <param name="user">Current bot-scoped conversation snapshot containing the pending quote id.</param>
    /// <param name="token">Cancellation of local state and Telegram work.</param>
    /// <returns>A task after displaying a validated quote or inviting a retry without reserving capacity.</returns>
    /// <remarks>An invalid code does not replace a previous displayed discount or create an order. Navigation is routed before this step by the caller.</remarks>
    private async Task HandlePurchaseDiscountTextAsync(ITelegramBotClient botClient, Message message, BotInstance tenant,
        CredUser customer, User user, CancellationToken token)
    {
        if (user.LastStep != "purchase-discount-entry" || user.PurchaseDiscountQuoteId is not > 0
            || message.From?.Id != customer.TelegramUserId || message.Text == null)
            return;
        var discounts = _serviceProvider.GetRequiredService<TenantDiscountService>();
        var quote = await LoadPurchaseDiscountQuoteAsync(user.PurchaseDiscountQuoteId.Value, tenant.Id,
            customer.TelegramUserId, message.Chat.Id, null, token);
        if (quote == null || quote.State != TenantDiscountQuoteStates.Open || quote.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await _state.SaveUserStatus(new User { Id = customer.TelegramUserId, Flow = "", LastStep = "", PurchaseDiscountQuoteId = 0 });
            await botClient.SendMessage(message.Chat.Id, "این پیش‌فاکتور منقضی شده است؛ خرید را دوباره آغاز کنید.", cancellationToken: token);
            return;
        }
        var selection = PARSESELECTIONFROMPAYACTION(quote.SelectionKey);
        if (!TryCurrentPurchaseQuotePrice(tenant, selection, quote, out var price))
        {
            await discounts.ExpireQuoteAsync(quote.Id, tenant.Id, customer.TelegramUserId, message.Chat.Id, token);
            await _state.SaveUserStatus(new User { Id = customer.TelegramUserId, Flow = "", LastStep = "", PurchaseDiscountQuoteId = 0 });
            await botClient.SendMessage(message.Chat.Id, "تعرفه این پیش‌فاکتور تغییر کرده است؛ خرید را دوباره آغاز کنید.", cancellationToken: token);
            return;
        }
        var code = TenantDiscountService.NormalizeCode(message.Text);
        var resolved = await discounts.QuoteAsync(tenant.Id, code, price.SalePriceToman, price.BaseCostToman,
            TenantDiscountScopes.Purchase, token);
        if (!resolved.Success)
        {
            await botClient.SendMessage(message.Chat.Id, PurchaseDiscountError(resolved.Failure) + " کد دیگری وارد کنید یا از دکمه پیش‌فاکتور کد را حذف کنید.", cancellationToken: token);
            return;
        }
        if (!HasPurchaseDiscountPaymentMethod(tenant, resolved.Value.Price.NetToman))
        {
            await botClient.SendMessage(message.Chat.Id,
                $"کد <code>{Html(code)}</code> معتبر است اما مبلغ پس از تخفیف با هیچ روش پرداخت فعلی این فروشگاه قابل پرداخت نیست. کد دیگری وارد کنید یا از دکمه حذف کد روی پیش‌فاکتور استفاده کنید.",
                parseMode: ParseMode.Html, cancellationToken: token);
            return;
        }
        var selected = await discounts.SelectQuoteCodeAsync(quote.Id, tenant.Id, customer.TelegramUserId,
            message.Chat.Id, quote.MessageId.Value, quote.SelectionKey,
            new TenantDiscountSelection(resolved.Value.Code.Id, resolved.Value.Code.UpdatedAtUtc, resolved.Value.Price),
            price.SalePriceToman, price.BaseCostToman, token);
        if (!selected.Success)
        {
            await botClient.SendMessage(message.Chat.Id, PurchaseDiscountError(selected.Failure) + " کد را دوباره وارد کنید.", cancellationToken: token);
            return;
        }
        await _state.SaveUserStatus(new User { Id = customer.TelegramUserId, Flow = "", LastStep = "", PurchaseDiscountQuoteId = 0 });
        await RenderPurchaseDiscountQuoteAsync(botClient, message.Chat.Id, quote.MessageId, tenant, selection, selected.Value, token, code);
    }

    /// <summary>Finds a quote by exact scoped identity, then verifies its stored selection through the discount service.</summary>
    /// <param name="id">Positive internal quote id decoded from callback or saved bot-scoped state.</param>
    /// <param name="tenantId">Storefront database id.</param>
    /// <param name="senderId">Authenticated customer Telegram user id.</param>
    /// <param name="chatId">Actual Telegram chat id, not necessarily sender id.</param>
    /// <param name="messageId">Callback's Telegram message id, or null when looking up pending text entry.</param>
    /// <param name="token">Cancellation of users.db lookups.</param>
    /// <returns>The detached quote only when every supplied identity matches a bound message; otherwise null.</returns>
    private async Task<TenantDiscountQuote> LoadPurchaseDiscountQuoteAsync(int id, string tenantId, long senderId,
        long chatId, int? messageId, CancellationToken token)
    {
        var raw = await _workflow.ReadAsync(db => db.TenantDiscountQuotes.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == id && x.TenantBotId == tenantId && x.CustomerTelegramUserId == senderId && x.ChatId == chatId
            && x.MessageId != null && (!messageId.HasValue || x.MessageId == messageId), token));
        if (raw?.MessageId is not > 0) return null;
        var checkedQuote = await _serviceProvider.GetRequiredService<TenantDiscountService>()
            .GetQuoteAsync(id, tenantId, senderId, chatId, raw.MessageId.Value, raw.SelectionKey, token);
        return checkedQuote.Success ? checkedQuote.Value : null;
    }

    /// <summary>Checks that the stored displayed tariff still describes the current, enabled purchase selection.</summary>
    /// <param name="tenant">Current storefront price policy.</param>
    /// <param name="selection">Selection parsed from the server-stored quote key; null means invalid.</param>
    /// <param name="quote">Previously displayed gross and colleague base snapshots.</param>
    /// <param name="price">Current tariff when unchanged; otherwise null.</param>
    /// <returns>True only for an unchanged authoritative tariff; no financial effects occur.</returns>
    private bool TryCurrentPurchaseQuotePrice(BotInstance tenant, XuiV3PurchaseSelection selection,
        TenantDiscountQuote quote, out TenantPriceResult price)
    {
        price = null;
        if (selection == null) return false;
        try
        {
            var fresh = CalculateTenantPrice(tenant, selection);
            if (fresh.SalePriceToman != quote.GrossToman || fresh.BaseCostToman != quote.BaseCostToman) return false;
            price = fresh;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Determines which currently enabled purchase payment buttons can accept the actual final net amount.</summary>
    /// <param name="tenant">Current tenant gateway preferences and customer-wallet approval.</param>
    /// <param name="netToman">Final discounted or undiscounted payable amount in whole toman.</param>
    /// <returns>Independent callback-code/caption pairs; an empty list means the customer cannot pay this quote.</returns>
    /// <remarks>The eventual admission must recheck each gateway against the persisted net; this is only a preview.</remarks>
    private List<(string Provider, string Caption)> PurchaseDiscountPaymentMethods(BotInstance tenant, long netToman)
    {
        var methods = new List<(string, string)>();
        if (TenantCustomerWalletPolicy.IsApproved(tenant)) methods.Add(("W", "💰 کیف پول مشتری"));
        if (IsTenantHooshPayAvailable(tenant, netToman)) methods.Add(("HP", "⚡ هوش‌پی آنی | کارمزد ۱۵٪ | ریالی"));
        if (IsTenantTetraminatorAvailable(tenant, netToman)) methods.Add(("TM", "⚡ تترامیناتور آنی | کارمزد ۱۲٪ | ریالی"));
        if (IsTenantUniquePayAvailable(tenant, netToman)) methods.Add(("UP", "⚡ یونیک‌پی آنی | کارمزد ۱۲٪ | ریالی"));
        if (IsTenantAtlasPayAvailable(tenant) && AtlasPay.IsSupportedBaseAmount(netToman))
            methods.Add(("AP", "💳 اطلس‌پی | کارت‌به‌کارت آنی | کارمزد ۱۲٪ | ریالی"));
        if (TenantPaymentGatewayPolicy.IsEnabled(tenant, PaymentGateway.NowPayments, _gatewayAvailability.Snapshot))
            methods.Add(("NP", "⚡ ارز دیجیتال آنی | کارمزد ۰٪"));
        if (TenantPaymentGatewayPolicy.IsPersonalCardEnabled(tenant))
            methods.Add(("CARD", "🧾 کارت‌به‌کارت به فروشگاه | ریالی"));
        return methods;
    }

    /// <summary>Checks if a valid discount can actually be purchased through any currently available net-priced method.</summary>
    /// <param name="tenant">Current storefront preferences.</param>
    /// <param name="netToman">Whole-toman payable amount after the actual capped discount.</param>
    /// <returns>True if at least one method is offered at this net amount.</returns>
    private bool HasPurchaseDiscountPaymentMethod(BotInstance tenant, long netToman) =>
        PurchaseDiscountPaymentMethods(tenant, netToman).Count != 0;

    /// <summary>Renders the persisted quote and binds a newly delivered or Telegram-confirmed already-visible message.</summary>
    /// <param name="botClient">Required transport for the tenant bot that owns this quote and its Telegram message.</param>
    /// <param name="chatId">Original Telegram chat of this quote.</param>
    /// <param name="messageId">Positive Telegram edit target in the quote's chat, or null to send a new message.</param>
    /// <param name="tenant">Storefront controlling gateway availability.</param>
    /// <param name="selection">Stored and freshly priced checkout selection.</param>
    /// <param name="quote">Open quote containing the actual displayed net; never a client-supplied price.</param>
    /// <param name="token">Cancellation of Telegram and users.db operations.</param>
    /// <param name="displayCode">Optional normalized entered code to HTML-escape in this render only.</param>
    /// <returns>True only when the successfully displayed Telegram message is bound to the open quote.</returns>
    /// <remarks>Manual pricing also uses an undiscounted quote to bind the displayed rate to this exact message;
    /// discount controls appear only when a code is active or already selected. Telegram's status 400/message-not-modified
    /// confirms the existing text and keyboard, so it preserves or completes that binding without a replacement message,
    /// warning, order, or reservation. Other edit failures retain the send/rebind fallback and expired old-message marker;
    /// if delivery cannot be bound, the quote expires without admission.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels Telegram delivery or local quote binding.</exception>
    /// <example><code>await RenderPurchaseDiscountQuoteAsync(client, customerChatId, quote.MessageId, tenant, selection, quote, token);</code></example>
    private async Task<bool> RenderPurchaseDiscountQuoteAsync(ITelegramBotClient botClient, ChatId chatId, int? messageId,
        BotInstance tenant, XuiV3PurchaseSelection selection, TenantDiscountQuote quote, CancellationToken token, string displayCode = null)
    {
        var discounts = _serviceProvider.GetRequiredService<TenantDiscountService>();
        var resolved = _purchaseService.ResolveTenantPurchase(selection, false);
        var breakdown = BuildTenantMeteredPriceBreakdownText(tenant, resolved, quote.GrossToman);
        var text = "📌 <b>پیش‌فاکتور خرید</b>\n\n" +
            $"سرویس: <b>{Html(resolved.Service.DisplayName)}</b>\n" +
            (resolved.IsUnlimited ? $"حد مصرف منصفانه: <code>{resolved.TrafficGb} GB</code>\n" : $"حجم: <code>{resolved.TrafficGb} GB</code>\n") +
            $"مدت: <code>{(resolved.DurationDays <= 0 ? "نامحدود" : resolved.DurationDays + " روز")}</code>\n" +
            (string.IsNullOrWhiteSpace(breakdown) ? "" : $"\n{breakdown}\n") +
            $"تعرفه قبل از تخفیف: <b>{Html(quote.GrossToman.FormatCurrency())}</b>\n" +
            (quote.CodeId.HasValue
                ? $"{(displayCode == null ? "تخفیف اعمال‌شده" : $"تخفیف کد <code>{Html(displayCode)}</code>")}: <b>{Html(quote.DiscountAmountToman.FormatCurrency())}</b>\n"
                : "") +
            $"مبلغ قابل پرداخت: <b>{Html(quote.NetToman.FormatCurrency())}</b>\n\n" +
            BuildTenantPaymentTimingNotice(isRenewal: false);
        var id36 = DiscountId(quote.Id);
        var rows = new List<InlineKeyboardButton[]>();
        if (quote.CodeId.HasValue ||
            await discounts.HasActiveScopeAsync(tenant.Id, TenantDiscountScopes.Purchase, token))
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🎟 ثبت کد تخفیف", CUSTOMERCALLBACKPREFIX + "DC:" + id36) });
        if (quote.CodeId.HasValue)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("حذف کد تخفیف", CUSTOMERCALLBACKPREFIX + "DR:" + id36) });
        foreach (var (provider, caption) in PurchaseDiscountPaymentMethods(tenant, quote.NetToman))
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(caption, CUSTOMERCALLBACKPREFIX + "DQ:" + id36 + ":" + provider) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", CUSTOMERCALLBACKPREFIX + "services") });
        var keyboard = new InlineKeyboardMarkup(rows);
        if (messageId is > 0)
        {
            try
            {
                await EditMessageTextAllowNoOpAsync(botClient, chatId, messageId.Value, text,
                    parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: token);
                // An identical-edit confirmation belongs to this exact message, not a new checkout or payment event.
                if (quote.MessageId == messageId.Value) return true;
                var bound = await discounts.BindQuoteMessageAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId,
                    quote.ChatId, messageId.Value, token);
                if (bound.Success) return true;
                await discounts.ExpireQuoteAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId, quote.ChatId, token);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "Tenant discount quote edit failed. QuoteId={QuoteId}", quote.Id); }
        }
        try
        {
            var sent = await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html,
                replyMarkup: keyboard, cancellationToken: token);
            if (sent?.MessageId is > 0)
            {
                // A failed edit can leave the old legacy PAY* keyboard visible. Bind then rebind to
                // preserve an expired marker on that old message before exposing the new checkout.
                if (quote.MessageId is not > 0 && messageId is > 0)
                {
                    var old = await discounts.BindQuoteMessageAsync(quote.Id, tenant.Id,
                        quote.CustomerTelegramUserId, quote.ChatId, messageId.Value, token);
                    if (!old.Success)
                    {
                        await discounts.ExpireQuoteAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId, quote.ChatId, token);
                        return false;
                    }
                    quote.MessageId = messageId.Value;
                }
                var bound = quote.MessageId is > 0
                    ? await discounts.RebindQuoteMessageAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId,
                        quote.ChatId, quote.MessageId.Value, sent.MessageId, token)
                    : await discounts.BindQuoteMessageAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId,
                        quote.ChatId, sent.MessageId, token);
                if (bound.Success) return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Tenant discount quote send failed. QuoteId={QuoteId}", quote.Id); }
        if (quote.MessageId is not > 0 && messageId is > 0)
        {
            // Even if Telegram cannot render either new quote, the original legacy preview must
            // not remain a path to a silent full-price order after the discount attempt.
            var old = await discounts.BindQuoteMessageAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId,
                quote.ChatId, messageId.Value, token);
            if (old.Success) quote.MessageId = messageId.Value;
        }
        await discounts.ExpireQuoteAsync(quote.Id, tenant.Id, quote.CustomerTelegramUserId, quote.ChatId, token);
        return false;
    }

    /// <summary>Translates a named tenant discount rejection into a customer-visible reason without silently reverting to full price.</summary>
    /// <param name="failure">Business failure returned by the discount service.</param>
    /// <returns>Safe, concise explanation suitable for Telegram text or callback alert.</returns>
    private static string PurchaseDiscountError(TenantDiscountFailure failure) => failure switch
    {
        TenantDiscountFailure.InvalidCode => "کد تخفیف نامعتبر است یا پیدا نشد.",
        TenantDiscountFailure.Disabled => "این کد تخفیف غیرفعال شده است.",
        TenantDiscountFailure.Scope => "این کد برای خرید اکانت قابل استفاده نیست.",
        TenantDiscountFailure.MinimumOrder => "مبلغ تعرفه قبل از تخفیف به حداقل لازم این کد نمی‌رسد.",
        TenantDiscountFailure.Exhausted => "ظرفیت استفاده از این کد به پایان رسیده است.",
        TenantDiscountFailure.NoAvailableMargin => "برای این تعرفه امکان اعمال تخفیف وجود ندارد.",
        TenantDiscountFailure.ChangedQuote or TenantDiscountFailure.Conflict => "تعرفه یا کد تغییر کرده است؛ پیش‌فاکتور را دوباره باز کنید.",
        _ => "امکان اعمال این کد وجود ندارد؛ پیش‌فاکتور را دوباره باز کنید."
    };
}
