using System.Globalization;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public partial class TenantBotService
{
    /// <summary>Rejects full-price callbacks on a message that was bound to a purchase quote, including expired quotes.</summary>
    /// <param name="botClient">Current tenant Telegram client for a stale-button alert.</param>
    /// <param name="callback">Authenticated sender and original quoted message.</param>
    /// <param name="tenant">Current internal storefront bot whose quote is queried.</param>
    /// <param name="token">Cancellation of the quote lookup and Telegram answer.</param>
    /// <returns>True if this exact sender/chat/message has a quote and the legacy payment must stop.</returns>
    private async Task<bool> RejectLegacyQuotedPurchaseAsync(ITelegramBotClient botClient, CallbackQuery callback,
        BotInstance tenant, CancellationToken token)
    {
        if (callback.Message == null || !await _serviceProvider.GetRequiredService<TenantDiscountService>()
                .HasQuoteForMessageAsync(tenant.Id, callback.From.Id, callback.Message.Chat.Id,
                    callback.Message.MessageId, token)) return false;
        await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
            "این پیش‌فاکتور دکمه پرداخت اختصاصی دارد؛ از همان دکمه استفاده کنید یا خرید تازه‌ای شروع کنید.",
            showAlert: true, cancellationToken: token);
        return true;
    }

    /// <summary>Admits exactly the displayed net and routes its one frozen method without rebuilding an order.</summary>
    /// <param name="botClient">Tenant Telegram client for invoice, card instruction or stale-button notice.</param>
    /// <param name="callback">Authenticated customer callback from the original quoted message.</param>
    /// <param name="tenant">Current tenant bot whose price and gateway settings apply.</param>
    /// <param name="customer">Shared credentials profile of the callback sender.</param>
    /// <param name="action">Compact server-issued DQ quote id and first payment method.</param>
    /// <param name="token">Cancellation of users.db admission, provider and Telegram operations.</param>
    /// <returns>A task after the original order's frozen payment path or actionable error.</returns>
    /// <remarks>The transaction reserves one code use before gateway I/O. An open quote with changed gross/base
    /// is expired before replying so restoring an old rate cannot revive a stale preview. Admitted replay retains
    /// its original stored order and payment method without repricing. Live global sale permission is required
    /// for an open quote and again for an admitted but unstarted payment. Linked provider rows are replayed/recovered
    /// without applying the new-admission gate.</remarks>
    private async Task HandleQuotedPurchasePaymentAsync(ITelegramBotClient botClient, CallbackQuery callback,
        BotInstance tenant, CredUser customer, string action, CancellationToken token)
    {
        var parts = action.Split(':');
        if (parts.Length != 3 || !ParseDiscountId(parts[1], out var quoteId) || quoteId <= 0
            || parts[2] is not ("HP" or "TM" or "UP" or "AP" or "NP" or "CARD")
            || callback.Message == null || callback.From.Id != customer.TelegramUserId)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "پیش‌فاکتور نامعتبر است؛ دوباره خرید کنید.",
                showAlert: true, cancellationToken: token);
            return;
        }
        var provider = parts[2];
        var chatId = callback.Message.Chat.Id;
        var quote = await LoadPurchaseDiscountQuoteAsync(quoteId, tenant.Id, customer.TelegramUserId,
            chatId, callback.Message.MessageId, token);
        if (quote == null || (quote.State == TenantDiscountQuoteStates.Admitted && quote.SelectedProvider != provider))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "این پیش‌فاکتور منقضی شده یا روش پرداخت دیگری ثبت شده است.",
                showAlert: true, cancellationToken: token);
            return;
        }
        if (quote.State != TenantDiscountQuoteStates.Open && quote.State != TenantDiscountQuoteStates.Admitted)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "پیش‌فاکتور منقضی شده است.",
                showAlert: true, cancellationToken: token);
            return;
        }
        TenantBotOrder newOrder = null;
        if (quote.State == TenantDiscountQuoteStates.Open)
        {
            var selection = PARSESELECTIONFROMPAYACTION(quote.SelectionKey);
            if (!await EnsureTenantSalesEnabledAsync(botClient, chatId, selection?.ServiceKey,
                    ServiceSalesOperation.Sale, token, callback)) return;
            if (!TryCurrentPurchaseQuotePrice(tenant, selection, quote, out var price)
                || quote.ExpiresAtUtc <= DateTime.UtcNow)
            {
                await _serviceProvider.GetRequiredService<TenantDiscountService>()
                    .ExpireQuoteAsync(quoteId, tenant.Id, customer.TelegramUserId, chatId, token);
                await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                    "تعرفه یا مهلت پیش‌فاکتور تغییر کرده است؛ دوباره خرید کنید.",
                    showAlert: true, cancellationToken: token);
                return;
            }
            if (!PurchaseDiscountPaymentMethods(tenant, quote.NetToman).Any(method => method.Provider == provider))
            {
                await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                    "این روش پرداخت برای مبلغ نهایی در دسترس نیست؛ روش دیگری انتخاب کنید.",
                    showAlert: true, cancellationToken: token);
                return;
            }
            if (!await EnsureTenantSalesEnabledAsync(botClient, chatId, selection.ServiceKey,
                    ServiceSalesOperation.Sale, token, callback)) return;
            newOrder = CreateTenantOrder(tenant, customer, chatId, selection, price,
                QuotedPurchaseProvider(provider));
        }
        var admitted = await _serviceProvider.GetRequiredService<TenantDiscountService>()
            .AdmitQuotedOrderAsync(quoteId, tenant.Id, customer.TelegramUserId, chatId,
                callback.Message.MessageId, quote.SelectionKey, provider, quote.GrossToman, quote.BaseCostToman,
                newOrder, token);
        if (!admitted.Success || admitted.Value.PaymentProvider != QuotedPurchaseProvider(provider))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                admitted.Success ? "روش پرداخت این پیش‌فاکتور قبلاً ثبت شده است." : PurchaseDiscountError(admitted.Failure),
                showAlert: true, cancellationToken: token);
            return;
        }
        var order = await _workflow.ReadAsync(db => db.TenantBotOrders.SingleAsync(x => x.Id == admitted.Value.Id, token));
        if ((order.PaymentStatus != TenantBotOrderStatuses.Pending &&
             !(provider == "CARD" && order.PaymentStatus == TenantBotOrderStatuses.AwaitingReceipt))
            || order.TenantDiscountCodeId.HasValue && !await HasReservedDiscountClaimAsync(order, token))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "این سفارش دیگر قابل پرداخت نیست؛ دوباره خرید کنید.",
                showAlert: true, cancellationToken: token);
            return;
        }
        await _state.ClearUserStatus(new User { Id = callback.From.Id });
        switch (provider)
        {
            case "HP": await CreateTenantHooshPayInvoiceCoreAsync(botClient, callback, tenant, customer, order, token); break;
            case "TM": await CreateTenantTetraminatorInvoiceCoreAsync(botClient, callback, tenant, customer, order, token); break;
            case "NP": await CreateTenantNowPaymentsInvoiceCoreAsync(botClient, callback, tenant, customer, order, token); break;
            case "UP": await CreateTenantUniquePayInvoiceCoreAsync(botClient, callback, tenant, customer, order, token); break;
            case "AP": await CreateTenantAtlasPayInvoiceCoreAsync(botClient, callback, tenant, customer, order, token); break;
            case "CARD": await SendTenantCardOrderInstructionsAsync(botClient, callback, tenant, order, token); break;
        }
    }

    /// <summary>Maps the compact quote method to the persisted provider name used by ordinary tenant settlement.</summary>
    /// <param name="provider">Compact HP, TM, UP, AP, NP or CARD quote button method.</param>
    /// <returns>Canonical persisted provider used for payment rows and settlement.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The button method is unsupported.</exception>
    private static string QuotedPurchaseProvider(string provider) => provider switch
    {
        "HP" => "HooshPay", "TM" => "Tetraminator", "UP" => "UniquePay", "AP" => "atlaspay",
        "NP" => "NowPayments", "CARD" => "tenant_card", _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    /// <summary>Checks the exact reserved claim without revalidating mutable owner code configuration.</summary>
    /// <param name="order">Preadmitted tenant order with a selected code and unique users.db id.</param>
    /// <param name="token">Cancellation of the read-only claim check.</param>
    /// <returns>True only while this exact order still owns an unreleased reservation.</returns>
    private Task<bool> HasReservedDiscountClaimAsync(TenantBotOrder order, CancellationToken token) =>
        _workflow.ReadAsync(db => db.TenantDiscountRedemptions.AsNoTracking().AnyAsync(x =>
            x.TenantBotOrderId == order.Id && x.CodeId == order.TenantDiscountCodeId
            && x.State == TenantDiscountRedemptionStates.Reserved, token));

    /// <summary>Claims the first method for a discounted renewal in a users.db-only atomic write.</summary>
    /// <param name="botClient">Tenant Telegram client for a rejected method-switch alert.</param>
    /// <param name="callback">Authenticated customer callback selecting the first renewal method.</param>
    /// <param name="order">Persisted discounted renewal order with a pending provider.</param>
    /// <param name="provider">Canonical persisted provider, checked against any previous selection.</param>
    /// <param name="token">Cancellation of the users.db writer transaction.</param>
    /// <returns>True if the order is still payable through this exact provider; false otherwise.</returns>
    /// <remarks>Locking the order writer row prevents concurrent first-choice callbacks from switching methods. A pending first choice requires live global renewal permission; same-method replay still delegates invoice reuse/creation to its guarded core.</remarks>
    private async Task<bool> ClaimDiscountRenewalMethodAsync(ITelegramBotClient botClient, CallbackQuery callback,
        TenantBotOrder order, string provider, CancellationToken token)
    {
        if (!order.TenantDiscountCodeId.HasValue) return true;
        var claimed = await _workflow.WriteAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var locked = await db.TenantBotOrders.Where(x => x.Id == order.Id
                && x.TenantBotId == order.TenantBotId
                && x.CustomerTelegramUserId == order.CustomerTelegramUserId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaymentProvider, x => x.PaymentProvider), token);
            if (locked != 1) return false;
            var saved = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.Id == order.Id
                && x.TenantBotId == order.TenantBotId && x.CustomerTelegramUserId == order.CustomerTelegramUserId, token);
            if (saved == null || saved.IsFulfilled || saved.PaymentStatus != TenantBotOrderStatuses.Pending
                || (saved.PaymentProvider != "pending" && saved.PaymentProvider != provider)
                || !await db.TenantDiscountRedemptions.AnyAsync(x => x.TenantBotOrderId == saved.Id
                    && x.CodeId == saved.TenantDiscountCodeId && x.State == TenantDiscountRedemptionStates.Reserved, token)
                || (saved.PaymentProvider == "pending" && saved.CreatedAtUtc.AddHours(2) <= DateTime.UtcNow))
                return false;
            if (saved.PaymentProvider == "pending")
            {
                if (!IsTenantSalesEnabled(saved.ServiceKey, ServiceSalesOperation.Renewal,
                        string.IsNullOrWhiteSpace(saved.UnlimitedPlanKey) ? null : XuiV3ServiceKinds.Unlimited)) return false;
                saved.PaymentProvider = provider;
                saved.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
            }
            await tx.CommitAsync(token);
            return true;
        }, token);
        if (claimed) await _workflow.ReloadAsync(order, token);
        else await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
            "روش پرداخت این سفارش قفل شده یا ظرفیت تخفیف آزاد شده است؛ تمدید تازه‌ای شروع کنید.",
            showAlert: true, cancellationToken: token);
        return claimed;
    }

    /// <summary>Stages one locally linked HooshPay attempt before the one external POST; replay only resends its link.</summary>
    /// <param name="botClient">Tenant client used to deliver the original invoice URL.</param>
    /// <param name="callback">Customer checkout callback and Telegram chat.</param>
    /// <param name="tenant">Storefront with enabled gateway and return URL.</param>
    /// <param name="customer">Authenticated Telegram payer.</param>
    /// <param name="order">Persisted frozen-net purchase or renewal order.</param>
    /// <param name="token">Cancellation of users.db, HooshPay and Telegram calls.</param>
    /// <returns>A task after one staged attempt or a replay-safe status alert.</returns>
    /// <remarks>The linked local payment row and started marker commit before HTTP POST. Global sale/renewal permission is re-read only for a first attempt; linked invoices and ambiguous attempts never permit another POST.</remarks>
    private async Task CreateTenantHooshPayInvoiceCoreAsync(ITelegramBotClient botClient, CallbackQuery callback,
        BotInstance tenant, CredUser customer, TenantBotOrder order, CancellationToken token)
    {
        var chat = callback.Message?.Chat.Id ?? callback.From.Id;
        using var gate = await TenantHooshPayDiscountCreationGate.EnterAsync(order.Id.ToString(CultureInfo.InvariantCulture), token);
        await _workflow.ReloadAsync(order, token);
        if (order.PaymentStatus != TenantBotOrderStatuses.Pending || order.PaymentProvider != "HooshPay"
            || order.TenantDiscountCodeId.HasValue && !await HasReservedDiscountClaimAsync(order, token))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "این سفارش دیگر قابل پرداخت نیست.", showAlert: true, cancellationToken: token);
            return;
        }
        var payment = await _workflow.ReadAsync(db => db.HooshPayPaymentInfos.FirstOrDefaultAsync(x => x.TenantBotOrderId == order.Id, token));
        if (payment != null || order.DiscountInvoiceAttemptState is not (null or "none"))
        {
            if (!string.IsNullOrWhiteSpace(order.PaymentUrl) && payment != null)
                await botClient.SendMessage(chat, BuildTenantPaymentText(order, payment), parseMode: ParseMode.Html,
                    replyMarkup: BuildTenantPaymentKeyboard(order, payment), cancellationToken: token);
            else await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                "نتیجه ساخت فاکتور قبلی نامشخص است؛ درخواست دوباره ارسال نمی‌شود.", showAlert: true, cancellationToken: token);
            return;
        }
        if (!await EnsureTenantOrderSalesEnabledAsync(botClient, callback, order, token)) return;
        if (!IsTenantHooshPayAvailable(tenant, order.SalePriceToman))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, BuildTenantHooshPayUnavailableMessage(tenant, order.SalePriceToman),
                showAlert: true, cancellationToken: token);
            return;
        }
        payment = new HooshPayPaymentInfo
        {
            OrderId = order.OrderId, AmountToman = order.SalePriceToman, FeeMode = HooshPayFeeModes.Buyer,
            IpnCallbackUrl = _appConfig.HooshPayIpnUrl,
            ReturnUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_success"),
            TelegramUserId = customer.TelegramUserId, ChatId = chat, BotId = tenant.Id, BotUsername = tenant.Username,
            PaymentPurpose = TenantBotPaymentPurposes.TenantOrder, TenantBotOrderId = order.Id,
            TenantOwnerTelegramUserId = tenant.OwnerTelegramUserId, PaymentStatus = HooshPayStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
        payment.RawRequestJson = JsonConvert.SerializeObject(new { amount = payment.AmountToman,
            fee_mode = HooshPayFeeModes.Buyer, description = $"tenant Bot order {order.OrderId}",
            order_id = order.OrderId, callback_url = payment.IpnCallbackUrl, return_url = payment.ReturnUrl });
        _workflow.Add(payment);
        order.DiscountInvoiceAttemptState = "started";
        order.DiscountInvoiceAttemptedAtUtc = DateTime.UtcNow;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _workflow.SaveAsync(token);
        order.HooshPayPaymentInfoId = payment.Id;
        await _workflow.SaveAsync(token);
        try
        {
            var invoice = await _hooshPay.CreateInvoiceAsync(payment.AmountToman, payment.OrderId,
                $"tenant Bot order {order.OrderId}", payment.IpnCallbackUrl, payment.ReturnUrl, token);
            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            payment.Apply(invoice?.data);
            if (string.IsNullOrWhiteSpace(payment.PaymentUrl))
                throw new InvalidOperationException("HooshPay returned no payable invoice URL.");
            order.HooshPayInvoiceUid = payment.InvoiceUid;
            order.PaymentUrl = payment.PaymentUrl;
            order.DiscountInvoiceAttemptState = "created";
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _workflow.SaveAsync(token);
            await botClient.SendMessage(chat, BuildTenantPaymentText(order, payment), parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, payment), cancellationToken: token);
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "فاکتور پرداخت ساخته شد.", cancellationToken: token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            await _workflow.ReloadAsync(order, token);
            if (order.DiscountInvoiceAttemptState == "started")
            {
                order.DiscountInvoiceAttemptState = "ambiguous";
                order.ErrorMessage = "HooshPay invoice creation outcome is ambiguous.";
                order.UpdatedAtUtc = DateTime.UtcNow;
                payment.ErrorMessage = ex.Message;
                payment.UpdatedAtUtc = DateTime.UtcNow;
                await _workflow.SaveAsync(token);
            }
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                order.DiscountInvoiceAttemptState == "created"
                    ? "فاکتور ساخته شده است؛ دکمه پرداخت همین سفارش را دوباره بزنید تا لینک نمایش داده شود."
                    : "وضعیت ساخت فاکتور نامشخص است؛ برای جلوگیری از فاکتور تکراری درخواست دوباره ارسال نمی‌شود.",
                showAlert: true, cancellationToken: token);
        }
    }

    /// <summary>Stages one locally linked NOWPayments attempt before the one external POST; ambiguous results retain capacity.</summary>
    /// <param name="botClient">Tenant client used to deliver the original crypto URL.</param>
    /// <param name="callback">Customer checkout callback and Telegram chat.</param>
    /// <param name="tenant">Current storefront with gateway enablement.</param>
    /// <param name="customer">Authenticated numeric Telegram payer.</param>
    /// <param name="order">Persisted frozen-net purchase or renewal order.</param>
    /// <param name="token">Cancellation of local, NOWPayments and Telegram operations.</param>
    /// <returns>A task after one staged provider attempt or a replay-safe alert.</returns>
    /// <remarks>The users.db attempt and payment row precede the external create request. Global sale/renewal permission gates only new attempts; existing invoice recovery remains independent and timeout never creates a second invoice.</remarks>
    private async Task CreateTenantNowPaymentsInvoiceCoreAsync(ITelegramBotClient botClient, CallbackQuery callback,
        BotInstance tenant, CredUser customer, TenantBotOrder order, CancellationToken token)
    {
        var chat = callback.Message?.Chat.Id ?? callback.From.Id;
        using var gate = await TenantNowPaymentsDiscountCreationGate.EnterAsync(order.Id.ToString(CultureInfo.InvariantCulture), token);
        await _workflow.ReloadAsync(order, token);
        if (order.PaymentStatus != TenantBotOrderStatuses.Pending || order.PaymentProvider != "NowPayments"
            || order.TenantDiscountCodeId.HasValue && !await HasReservedDiscountClaimAsync(order, token))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "این سفارش دیگر قابل پرداخت نیست.", showAlert: true, cancellationToken: token);
            return;
        }
        var payment = await _workflow.ReadAsync(db => db.SwapinoPaymentInfos.FirstOrDefaultAsync(x => x.TenantBotOrderId == order.Id, token));
        if (payment != null || order.DiscountInvoiceAttemptState is not (null or "none"))
        {
            if (!string.IsNullOrWhiteSpace(order.PaymentUrl))
                await botClient.SendMessage(chat, BUILDTENANTGATEWAYPAYMENTTEXT(order, "ارز دیجیتال"), parseMode: ParseMode.Html,
                    replyMarkup: BuildTenantPaymentKeyboard(order, order.PaymentUrl), cancellationToken: token);
            else await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                "نتیجه ساخت فاکتور قبلی نامشخص است؛ درخواست دوباره ارسال نمی‌شود.", showAlert: true, cancellationToken: token);
            return;
        }
        if (!await EnsureTenantOrderSalesEnabledAsync(botClient, callback, order, token)) return;
        if (!TenantPaymentGatewayPolicy.IsEnabled(tenant, PaymentGateway.NowPayments, _gatewayAvailability.Snapshot))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "درگاه ارز دیجیتال غیرفعال است.", showAlert: true, cancellationToken: token);
            return;
        }
        payment = SwapinoPaymentInfo.CreateCryptoCharge(customer.TelegramUserId, order.SalePriceToman,
            _appConfig.NowpaymentIpnUrl, chat, _appConfig.NowpaymentPayCurrency);
        payment.OrderId = order.OrderId;
        payment.SuccessUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_success");
        payment.CancelUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_cancel");
        payment.BotId = tenant.Id;
        payment.BotUsername = tenant.Username;
        payment.PaymentPurpose = TenantBotPaymentPurposes.TenantOrder;
        payment.TenantBotOrderId = order.Id;
        payment.TenantOwnerTelegramUserId = tenant.OwnerTelegramUserId;
        _workflow.Add(payment);
        order.DiscountInvoiceAttemptState = "started";
        order.DiscountInvoiceAttemptedAtUtc = DateTime.UtcNow;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _workflow.SaveAsync(token);
        order.NowPaymentsPaymentInfoId = payment.Id;
        await _workflow.SaveAsync(token);
        try
        {
            var invoice = await _nowPayments.CreateInvoiceAsync(order.SalePriceToman, order.OrderId,
                $"tenant Bot order {order.OrderId}", successUrl: payment.SuccessUrl,
                cancelUrl: payment.CancelUrl, cancellationToken: token);
            var data = NowPaymentsPaymentRecordData.FromInvoiceResponse(invoice);
            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            payment.SetNowPaymentsData(data);
            if (string.IsNullOrWhiteSpace(payment.InvoiceUrl))
                throw new InvalidOperationException("NOWPayments returned no payable invoice URL.");
            order.PaymentUrl = payment.InvoiceUrl;
            order.DiscountInvoiceAttemptState = "created";
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _workflow.SaveAsync(token);
            await botClient.SendMessage(chat, BUILDTENANTGATEWAYPAYMENTTEXT(order, "ارز دیجیتال"), parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, payment.InvoiceUrl), cancellationToken: token);
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "فاکتور پرداخت ساخته شد.", cancellationToken: token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            await _workflow.ReloadAsync(order, token);
            if (order.DiscountInvoiceAttemptState == "started")
            {
                order.DiscountInvoiceAttemptState = "ambiguous";
                order.ErrorMessage = "NOWPayments invoice creation outcome is ambiguous.";
                order.UpdatedAtUtc = DateTime.UtcNow;
                payment.ErrorMessage = ex.Message;
                payment.UpdatedAtUtc = DateTime.UtcNow;
                await _workflow.SaveAsync(token);
            }
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id,
                order.DiscountInvoiceAttemptState == "created"
                    ? "فاکتور ساخته شده است؛ دکمه پرداخت همین سفارش را دوباره بزنید تا لینک نمایش داده شود."
                    : "وضعیت ساخت فاکتور نامشخص است؛ برای جلوگیری از فاکتور تکراری درخواست دوباره ارسال نمی‌شود.",
                showAlert: true, cancellationToken: token);
        }
    }

    /// <summary>Activates a preadmitted personal-card order once and resends its unchanged card instructions on replay.</summary>
    /// <param name="botClient">Tenant Telegram client for card details.</param>
    /// <param name="callback">Authenticated customer's original quoted message.</param>
    /// <param name="tenant">Storefront with the owner's currently enabled card settings.</param>
    /// <param name="order">Persisted first-method card order with its immutable final net.</param>
    /// <param name="token">Cancellation of the local order update and Telegram delivery.</param>
    /// <returns>A task after instructions or an expiry/released-claim alert.</returns>
    /// <remarks>Rechecks global sale/renewal permission before exposing personal-card payment. Existing submitted receipt confirmation and paid/provisional finalization use their original settlement paths, not this unpaid activation method.</remarks>
    private async Task SendTenantCardOrderInstructionsAsync(ITelegramBotClient botClient, CallbackQuery callback,
        BotInstance tenant, TenantBotOrder order, CancellationToken token)
    {
        if (!await EnsureTenantOrderSalesEnabledAsync(botClient, callback, order, token)) return;
        if ((order.PaymentStatus == TenantBotOrderStatuses.Pending &&
             !TenantPaymentGatewayPolicy.IsPersonalCardEnabled(tenant))
            || order.PaymentProvider != "tenant_card"
            || order.PaymentStatus is not (TenantBotOrderStatuses.Pending or TenantBotOrderStatuses.AwaitingReceipt)
            || order.TenantDiscountCodeId.HasValue
                && (order.CreatedAtUtc.AddHours(2) <= DateTime.UtcNow
                    || !await HasReservedDiscountClaimAsync(order, token)))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "این سفارش کارت‌به‌کارت دیگر قابل پرداخت نیست.",
                showAlert: true, cancellationToken: token);
            return;
        }
        if (order.PaymentStatus == TenantBotOrderStatuses.Pending)
        {
            order.PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _workflow.SaveAsync(token);
        }
        await botClient.SendMessage(callback.Message?.Chat.Id ?? callback.From.Id,
            "💳 <b>پرداخت کارت‌به‌کارت فروشگاه</b>\n\n" +
            $"مبلغ دقیق: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            BuildTenantDiscountOrderPriceText(order) +
            $"شماره کارت: <code>{Html(tenant.TenantCardNumber)}</code>\n" +
            $"نام صاحب کارت: <b>{Html(tenant.TenantCardHolderName)}</b>\n" +
            $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n\n" +
            "بعد از پرداخت، عکس رسید را همینجا ارسال کنید تا همکار آن را تایید کند.",
            parseMode: ParseMode.Html, replyMarkup: BuildTenantCardPaymentKeyboard(order), cancellationToken: token);
        await SafeAnswerCallbackQueryAsync(botClient, callback.Id, "سفارش کارت‌به‌کارت ثبت شد.", cancellationToken: token);
    }

    /// <summary>Shows stored code, gross, realized reduction and final net without consulting mutable code settings.</summary>
    /// <param name="order">Persisted discounted tenant order with code, gross, reduction and net snapshots.</param>
    /// <returns>Escaped HTML audit lines for a selected code, or empty text for an undiscounted order.</returns>
    private static string BuildTenantDiscountOrderPriceText(TenantBotOrder order) =>
        order.TenantDiscountCodeId.HasValue
            ? $"کد تخفیف: <code>{Html(order.AppliedDiscountCode)}</code>\n" +
              $"تعرفه قبل از تخفیف: <code>{Html(order.OriginalSalePriceToman!.Value.FormatCurrency())}</code>\n" +
              $"تخفیف اعمال‌شده: <code>{Html(order.DiscountAmountToman!.Value.FormatCurrency())}</code>\n" +
              $"مبلغ نهایی سفارش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n"
            : string.Empty;

    /// <summary>Blocks a discounted card approval after expiry, rejected receipt or released/missing claim.</summary>
    /// <param name="order">Tenant card order whose discount claim must still be valid.</param>
    /// <param name="receipt">Receipt being confirmed, or null to load the order's submitted receipt.</param>
    /// <param name="token">Cancellation of users.db status and claim reads.</param>
    /// <returns>True when the order expired, its claim was released or its receipt was rejected.</returns>
    /// <remarks>A photo submitted before the two-hour deadline remains eligible after the deadline; fulfilled replays are untouched.</remarks>
    private async Task<bool> IsDiscountCardApprovalBlockedAsync(TenantBotOrder order,
        TenantManualPaymentReceipt receipt, CancellationToken token)
    {
        if (!order.TenantDiscountCodeId.HasValue || order.IsFulfilled ||
            order.PaymentProvider != "tenant_card") return false;
        var live = await _workflow.ReadAsync(db => db.TenantBotOrders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == order.Id, token));
        if (live == null || live.PaymentStatus == TenantBotOrderStatuses.DiscountExpired)
            return true;
        var claim = await _workflow.ReadAsync(db => db.TenantDiscountRedemptions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantBotOrderId == order.Id
                && x.CodeId == order.TenantDiscountCodeId, token));
        if (claim is not { State: TenantDiscountRedemptionStates.Reserved or TenantDiscountRedemptionStates.Consumed })
            return true;
        receipt ??= await _workflow.ReadAsync(db => db.TenantManualPaymentReceipts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantBotOrderId == order.Id, token));
        if (receipt?.Status == TenantManualPaymentReceiptStatuses.Rejected ||
            live.PaymentStatus == TenantBotOrderStatuses.ReceiptRejected) return true;
        return live.CreatedAtUtc.AddHours(2) <= DateTime.UtcNow && string.IsNullOrWhiteSpace(receipt?.PhotoFileId)
            && receipt?.Status != TenantManualPaymentReceiptStatuses.Approved;
    }

    /// <summary>Per-order creation gates prevent a duplicate callback from racing the durable invoice attempt record.</summary>
    private static readonly AsyncKeyedGate TenantHooshPayDiscountCreationGate = new();
    private static readonly AsyncKeyedGate TenantNowPaymentsDiscountCreationGate = new();
}
