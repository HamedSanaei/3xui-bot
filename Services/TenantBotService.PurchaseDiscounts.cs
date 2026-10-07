using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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
    /// <remarks>Checks the live global sale permission before creating a quote. Creates no order, invoice or reservation. Existing quoted messages are never overwritten by another quote; their buttons remain tied to their original message.</remarks>
    private async Task ShowCustomerDiscountConfirmAsync(ITelegramBotClient botClient, ChatId chatId, int? messageId,
        BotInstance tenant, XuiV3PurchaseSelection selection, long customerTelegramUserId, CancellationToken token)
    {
        if (!await EnsureTenantSalesEnabledAsync(botClient, chatId, selection.ServiceKey,
                ServiceSalesOperation.Sale, token)) return;
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
    /// <remarks>Global sale closure blocks stale quote actions without expiring a valid quote. No payment admission occurs here. Expired and admitted quotes cannot be reactivated by old buttons.</remarks>
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
        if (!await EnsureTenantSalesEnabledAsync(botClient, chat, selection?.ServiceKey,
                ServiceSalesOperation.Sale, token, callback)) return;
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
    /// <remarks>Restored input rechecks live global sale permission and read-only exact-owner funding for payable methods.
    /// Insufficient funding overrides provider opt-outs and excludes personal card. Invalid codes do not replace an existing discount or create an order.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels funding, quote validation, state, or Telegram delivery.</exception>
    /// <example><code>await HandlePurchaseDiscountTextAsync(client, message, tenant, customer, state, token);</code></example>
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
        if (!await EnsureTenantSalesEnabledAsync(botClient, message.Chat.Id, selection?.ServiceKey,
                ServiceSalesOperation.Sale, token)) return;
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
        if (!HasPurchaseDiscountPaymentMethod(tenant, resolved.Value.Price.NetToman, await GetTenantPaymentAccessAsync(tenant, token)))
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
    /// <param name="funding">Required exact-owner snapshot shared by all choices; insufficient funding uses live central gateways only.</param>
    /// <returns>Independent callback-code/caption pairs; an empty list means the customer cannot pay this quote.</returns>
    /// <remarks>The eventual first invoice/card admission re-evaluates funding against the persisted net; this is a read-only preview.
    /// Saved preferences remain unchanged. Blocked or missing owner evaluations never offer a payment route.</remarks>
    /// <example><code>var methods = PurchaseDiscountPaymentMethods(tenant, quote.NetToman, funding);</code></example>
    private List<(string Provider, string Caption)> PurchaseDiscountPaymentMethods(BotInstance tenant, long netToman, TenantAccessEvaluation funding)
    {
        if (funding?.IsAllowed != true) return new List<(string, string)>();
        var methods = new List<(string, string)>();
        if (TenantCustomerWalletPolicy.IsApproved(tenant)) methods.Add(("W", "💰 کیف پول مشتری"));
        if (IsTenantHooshPayAvailable(tenant, netToman, funding)) methods.Add(("HP", "⚡ هوش‌پی آنی | کارمزد ۱۵٪ | ریالی"));
        if (IsTenantTetraminatorAvailable(tenant, netToman, funding)) methods.Add(("TM", "⚡ تترامیناتور آنی | کارمزد ۱۲٪ | ریالی"));
        if (IsTenantUniquePayAvailable(tenant, netToman, funding)) methods.Add(("UP", "⚡ یونیک‌پی آنی | کارمزد ۱۲٪ | ریالی"));
        if (IsTenantAtlasPayAvailable(tenant, funding) && AtlasPay.IsSupportedBaseAmount(netToman))
            methods.Add(("AP", "💳 اطلس‌پی | کارت‌به‌کارت آنی | کارمزد ۱۲٪ | ریالی"));
        if (TenantPaymentGatewayPolicy.IsEnabled(tenant, PaymentGateway.NowPayments, _gatewayAvailability.Snapshot, funding))
            methods.Add(("NP", "⚡ ارز دیجیتال آنی | کارمزد ۰٪"));
        if (TenantPaymentGatewayPolicy.IsPersonalCardEnabled(tenant, funding))
            methods.Add(("CARD", "🧾 کارت‌به‌کارت به فروشگاه | ریالی"));
        return methods;
    }

    /// <summary>Checks if a valid discount can actually be purchased through any currently available net-priced method.</summary>
    /// <param name="tenant">Current storefront preferences.</param>
    /// <param name="netToman">Whole-toman payable amount after the actual capped discount.</param>
    /// <param name="funding">Required exact-owner funding snapshot; null and owner restrictions fail closed.</param>
    /// <returns>True if at least one method is offered at this net amount.</returns>
    /// <remarks>Preview only; first payment admission requires another fresh funding evaluation.</remarks>
    /// <example><code>HasPurchaseDiscountPaymentMethod(tenant, netToman, funding);</code></example>
    private bool HasPurchaseDiscountPaymentMethod(BotInstance tenant, long netToman, TenantAccessEvaluation funding) =>
        PurchaseDiscountPaymentMethods(tenant, netToman, funding).Count != 0;

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
    /// warning, order, or reservation. Only definitive Telegram 4xx edit rejections permit a replacement send/rebind;
    /// foreground deadlines, transport cancellation without caller cancellation, 5xx and other uncertain failures
    /// tombstone the original message and expire the quote before propagating the original exception. No replacement
    /// is sent when Telegram may already have applied the edit, and neither DQ nor legacy PAY* buttons can admit it.
    /// One fresh exact-owner funding snapshot drives all payment choices; debt mode overrides saved opt-outs and omits personal card.
    /// If delivery cannot be bound, the quote expires without admission. Explicit caller cancellation propagates unchanged.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels Telegram delivery or local quote binding, or transport cancellation has an uncertain edit outcome.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The edit exceeded its foreground budget; the quote is retired without a replacement send.</exception>
    /// <exception cref="ApiRequestException">Telegram returned an uncertain non-4xx edit failure; the quote is retired before the exception propagates.</exception>
    /// <exception cref="RequestException">The Telegram SDK could not confirm the edit; the quote is retired without retrying.</exception>
    /// <exception cref="HttpRequestException">The transport could not confirm the edit; the quote is retired without retrying.</exception>
    /// <exception cref="IOException">The edit response stream failed; the quote is retired without a replacement send.</exception>
    /// <exception cref="TimeoutException">The edit timed out without confirmation; the quote is retired without a replacement send.</exception>
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
        var funding = await GetTenantPaymentAccessAsync(tenant, token);
        foreach (var (provider, caption) in PurchaseDiscountPaymentMethods(tenant, quote.NetToman, funding))
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
            catch (ApiRequestException ex) when (ex.ErrorCode is >= 400 and < 500)
            {
                _logger.LogWarning(ex, "Tenant discount quote edit rejected. QuoteId={QuoteId}", quote.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant discount quote edit outcome uncertain; expiring without replacement. QuoteId={QuoteId}", quote.Id);
                await ExpireUndeliveredPurchaseQuoteAsync(discounts, quote, messageId, token);
                throw;
            }
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
        await ExpireUndeliveredPurchaseQuoteAsync(discounts, quote, messageId, token);
        return false;
    }

    /// <summary>Retires an unconfirmed purchase render while blocking the original message's legacy payment keyboard.</summary>
    /// <param name="discounts">Required quote store for this tenant's users.db.</param>
    /// <param name="quote">Unadmitted quote being retired; existing admitted orders are never changed.</param>
    /// <param name="messageId">Original positive Telegram edit target, or null when no known message exists.</param>
    /// <param name="token">Caller-owned cancellation of the short local binding and expiration operations.</param>
    /// <returns>A task after the quote is expired and any previously unbound legacy edit target is bound as a tombstone.</returns>
    /// <remarks>No Telegram request, wallet mutation, order or discount reservation occurs. The bot/customer/chat/message
    /// binding remains queryable after expiration so legacy PAY* callbacks cannot silently bypass a failed discounted render.
    /// Called before an uncertain edit exception propagates or when definitive-rejection replacement delivery cannot bind.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the local state transition.</exception>
    /// <example><code>await ExpireUndeliveredPurchaseQuoteAsync(discounts, quote, originalMessageId, token);</code></example>
    private static async Task ExpireUndeliveredPurchaseQuoteAsync(TenantDiscountService discounts,
        TenantDiscountQuote quote, int? messageId, CancellationToken token)
    {
        if (quote.MessageId is not > 0 && messageId is > 0)
        {
            // An ambiguous edit may leave the old legacy PAY* keyboard or the new DQ keyboard visible.
            // Keep the known original message bound so neither version can bypass the retired quote.
            var old = await discounts.BindQuoteMessageAsync(quote.Id, quote.TenantBotId, quote.CustomerTelegramUserId,
                quote.ChatId, messageId.Value, token);
            if (old.Success) quote.MessageId = messageId.Value;
        }
        await discounts.ExpireQuoteAsync(quote.Id, quote.TenantBotId, quote.CustomerTelegramUserId, quote.ChatId, token);
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
