using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public partial class TenantBotService
{
    /// <summary>Admits a saved discounted renewal at its freshly verified net amount before offering payment.</summary>
    /// <param name="client">Tenant Telegram transport for the payment choice or a code re-entry prompt.</param>
    /// <param name="chatId">The customer's numeric chat id, which must match the pending order.</param>
    /// <param name="tenant">Storefront whose tariff and enabled payment methods were just rechecked.</param>
    /// <param name="customer">Authenticated Telegram customer for this bot-scoped renewal.</param>
    /// <param name="user">Saved renewal target, plan, and displayed code selection; failures retain these fields.</param>
    /// <param name="order">Unsaved, target-authorized renewal order with pending payment provider.</param>
    /// <param name="grossToman">Fresh undiscounted tenant sale amount in whole toman.</param>
    /// <param name="baseCostToman">Fresh colleague cost used only for discount validation.</param>
    /// <param name="token">Cancellation of the users.db transaction and Telegram delivery.</param>
    /// <returns><c>false</c> only if no code was selected; otherwise <c>true</c> after admission or an actionable failure.</returns>
    /// <remarks>Admission rechecks the global renewal permission and reserves capacity with the pending order in one users.db commit. This method never invokes a provider, wallet, or XUI; any invalid selected code fails closed instead of charging the gross amount.</remarks>
    private async Task<bool> AdmitDiscountedRenewalFromStateAsync(
        ITelegramBotClient client, ChatId chatId, BotInstance tenant, CredUser customer, User user,
        TenantBotOrder order, long grossToman, long baseCostToman, CancellationToken token)
    {
        if (string.IsNullOrEmpty(user?.RenewalDiscountSelectionJson)) return false;

        var validIdentity = tenant != null && customer != null && order != null && chatId.Identifier.HasValue
            && string.Equals(BotContextAccessor.CurrentBotId, tenant.Id, StringComparison.Ordinal)
            && user.Id == customer.TelegramUserId && order.Id == 0
            && order.TenantBotId == tenant.Id && order.OwnerTelegramUserId == tenant.OwnerTelegramUserId
            && order.CustomerTelegramUserId == customer.TelegramUserId && order.CustomerChatId == chatId.Identifier.Value
            && order.OrderKind == TenantBotOrderKinds.Renew && order.PaymentProvider == "pending"
            && order.SalePriceToman == grossToman && order.BaseCostToman == baseCostToman;
        if (!validIdentity)
        {
            await SendDiscountedRenewalAdmissionFailureAsync(client, chatId, user, order, grossToman,
                "اطلاعات هویت یا تعرفه تمدید تغییر کرده است؛ کد را دوباره وارد کنید.", token);
            return true;
        }

        var selection = BuildTenantRenewSelectionFromState(user);
        var saved = ReadTenantRenewDiscountSelection(user, tenant, selection, grossToman, baseCostToman);
        if (!saved.Success || saved.Value == null)
        {
            await SendDiscountedRenewalAdmissionFailureAsync(client, chatId, user, order, grossToman,
                TenantRenewDiscountFailureText(saved.Success ? TenantDiscountFailure.ChangedQuote : saved.Failure), token);
            return true;
        }

        if (PurchaseDiscountPaymentMethods(tenant, saved.Value.Displayed.NetToman).Count == 0)
        {
            await SendDiscountedRenewalAdmissionFailureAsync(client, chatId, user, order, grossToman,
                "هیچ روش پرداخت فعالی مبلغ نهایی این کد را نمی‌پذیرد؛ کد دیگری وارد کنید یا «ادامه بدون کد تخفیف» را بزنید.", token);
            return true;
        }

        if (!await EnsureTenantSalesEnabledAsync(client, chatId, order.ServiceKey,
                ServiceSalesOperation.Renewal, token)) return true;
        var admitted = await _serviceProvider.GetRequiredService<TenantDiscountService>()
            .AdmitRenewalOrderAsync(order, saved.Value, grossToman, baseCostToman, token);
        if (!admitted.Success)
        {
            await SendDiscountedRenewalAdmissionFailureAsync(client, chatId, user, order, grossToman,
                TenantRenewDiscountFailureText(admitted.Failure), token);
            return true;
        }

        await _state.ClearUserStatus(user);
        await client.SendMessage(chatId, BuildTenantRenewOrderPaymentChoiceText(admitted.Value),
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantRenewPaymentProviderKeyboard(admitted.Value, tenant),
            cancellationToken: token);
        return true;
    }

    /// <summary>Resumes the same authorized renewal at its gross tariff only after an explicit customer choice.</summary>
    /// <param name="client">Current storefront's Telegram transport.</param>
    /// <param name="chatId">Authenticated customer conversation.</param>
    /// <param name="tenant">Tenant storefront owning the pending renewal.</param>
    /// <param name="customer">Customer matched to the bot-scoped renewal state.</param>
    /// <param name="user">Renewal state retaining target email, UUID, service and plan.</param>
    /// <param name="text">Current customer reply; all other text remains eligible for code entry.</param>
    /// <param name="token">Cancellation of state persistence and refreshed summary delivery.</param>
    /// <returns><c>true</c> only for the explicit gross retry button, including a rejected identity mismatch.</returns>
    /// <remarks>The refreshed gross summary still requires a separate confirmation before any order is inserted.</remarks>
    private async Task<bool> TryResumeTenantRenewalWithoutDiscountAsync(
        ITelegramBotClient client, ChatId chatId, BotInstance tenant, CredUser customer, User user,
        string text, CancellationToken token)
    {
        if (text != "ادامه بدون کد تخفیف") return false;
        if (tenant == null || customer == null || user == null
            || user.Id != customer.TelegramUserId
            || user.Flow != TENANTRENEWFLOW || user.LastStep != TenantRenewDiscountEntryStep
            || !string.Equals(BotContextAccessor.CurrentBotId, tenant.Id, StringComparison.Ordinal))
        {
            await client.SendMessage(chatId, "این درخواست تمدید دیگر معتبر نیست. فرایند را دوباره آغاز کنید.",
                cancellationToken: token);
            return true;
        }

        user.RenewalDiscountSelectionJson = string.Empty;
        user.LastStep = TENANTRENEWSTEPCONFIRM;
        await _state.SaveUserStatus(user);
        await SendTenantRenewSummaryAsync(client, chatId, tenant, customer, user, token);
        return true;
    }

    /// <summary>Offers explicit gross-price retry without treating a failed code as permission to charge full price.</summary>
    /// <returns>A reply keyboard for gross retry or cancellation; customers may also enter another code directly.</returns>
    private static ReplyKeyboardMarkup BuildDiscountedRenewalAdmissionRetryKeyboard() =>
        new(new[] { new[] { new KeyboardButton("ادامه بدون کد تخفیف") }, new[] { new KeyboardButton("❌ انصراف") } })
        { ResizeKeyboard = true };

    /// <summary>Returns a failed discounted renewal to code entry without losing its selected account or plan.</summary>
    /// <param name="client">Telegram client for the current storefront.</param>
    /// <param name="chatId">Chat containing the earlier renewal summary.</param>
    /// <param name="user">Bot-scoped customer state; its saved code snapshot remains unchanged.</param>
    /// <param name="order">Unsaved renewal order carrying the authorized target and selected service.</param>
    /// <param name="grossToman">Fresh undiscounted sale amount to show in the retry summary.</param>
    /// <param name="reason">Customer-safe, specific validation failure.</param>
    /// <param name="token">Cancellation of state persistence and Telegram delivery.</param>
    /// <returns>A task completing after the customer can enter another code, explicitly retry gross, or cancel.</returns>
    /// <remarks>Neither the state snapshot nor the account email, UUID, service, or plan is cleared. No confirmation/payment button is presented for an invalid discounted price.</remarks>
    private async Task SendDiscountedRenewalAdmissionFailureAsync(
        ITelegramBotClient client, ChatId chatId, User user, TenantBotOrder order,
        long grossToman, string reason, CancellationToken token)
    {
        user.LastStep = TenantRenewDiscountEntryStep;
        await _state.SaveUserStatus(user);
        var target = order?.TargetAccountEmail ?? user.ConfigLink ?? string.Empty;
        var service = order?.ServiceKey ?? user.SelectedCountry ?? string.Empty;
        var plan = !string.IsNullOrWhiteSpace(order?.UnlimitedPlanKey)
            ? order.UnlimitedPlanKey
            : $"{order?.TrafficGb?.ToString() ?? user.TotoalGB} GB / {order?.DurationKey ?? user.SelectedPeriod}";
        await client.SendMessage(chatId,
            "⚠️ <b>خلاصه تمدید؛ سفارش ساخته نشد</b>\n\n" +
            $"اکانت: <code>{Html(target)}</code>\n" +
            $"سرویس: <code>{Html(service)}</code>\n" +
            $"پلن: <code>{Html(plan)}</code>\n" +
            $"تعرفه فعلی قبل از تخفیف: <b>{Html(grossToman.FormatCurrency())}</b>\n\n" +
            Html(reason) + "\nکد تخفیف را دوباره وارد کنید، «ادامه بدون کد تخفیف» را بزنید یا «❌ انصراف» را انتخاب کنید.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildDiscountedRenewalAdmissionRetryKeyboard(),
            cancellationToken: token);
    }
}
