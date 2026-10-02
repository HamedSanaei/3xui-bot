using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;

public partial class TenantBotService
{
    /// <summary>Checks the live global operation switch without replacing tenant catalog, audience or pricing validation.</summary>
    /// <param name="serviceKey">Global catalog key from authorized state, a stored quote or an order.</param>
    /// <param name="operation">Sale or renewal admission; these switches are independent.</param>
    /// <param name="serviceKind">Optional frozen order kind used when a catalog entry has since been removed.</param>
    /// <returns>True only when the selected global category permits new unpaid work.</returns>
    /// <remarks>Global policy is shared by all storefronts and cannot be overridden by owner preferences. Never use this check in paid settlement.</remarks>
    /// <example><code>if (!IsTenantSalesEnabled(order.ServiceKey, ServiceSalesOperation.Renewal)) return;</code></example>
    private bool IsTenantSalesEnabled(string serviceKey, ServiceSalesOperation operation, string serviceKind = null)
    {
        var service = _purchaseService.GetEnabledServices().FirstOrDefault(candidate =>
            string.Equals(candidate.Key, serviceKey, StringComparison.OrdinalIgnoreCase));
        var category = service != null ? ServiceSalesPolicy.GetCategory(service)
            : ServiceSalesPolicy.GetCategory(serviceKey, serviceKind);
        return _purchaseService.SalesAvailability.Snapshot.IsEnabled(category, operation);
    }

    /// <summary>Rejects closed global admission with a category-specific customer-safe notice before financial side effects.</summary>
    /// <param name="client">Transport for the active tenant storefront.</param>
    /// <param name="chat">Telegram customer chat, not a tenant database id.</param>
    /// <param name="serviceKey">Selected global catalog key; callers retain all normal authorization checks.</param>
    /// <param name="operation">Explicit operation being admitted, never inferred from the customer role.</param>
    /// <param name="token">Cancellation of Telegram notice delivery.</param>
    /// <param name="callback">Optional stale callback to answer with an alert instead of a new message.</param>
    /// <param name="serviceKind">Optional stored catalog kind for an admitted order.</param>
    /// <returns>False when closed and notified; true when admission may continue to its remaining checks.</returns>
    /// <remarks>Reads live permission on each call. Does not expire quotes, release reservations or alter payments; reopening restores valid choices without restart.</remarks>
    /// <example><code>if (!await EnsureTenantSalesEnabledAsync(client, chat, selection.ServiceKey, ServiceSalesOperation.Sale, token, callback)) return;</code></example>
    private async Task<bool> EnsureTenantSalesEnabledAsync(ITelegramBotClient client, ChatId chat,
        string serviceKey, ServiceSalesOperation operation, CancellationToken token,
        CallbackQuery callback = null, string serviceKind = null)
    {
        if (IsTenantSalesEnabled(serviceKey, operation, serviceKind)) return true;
        var service = _purchaseService.GetEnabledServices().FirstOrDefault(candidate =>
            string.Equals(candidate.Key, serviceKey, StringComparison.OrdinalIgnoreCase));
        var category = service != null ? ServiceSalesPolicy.GetCategory(service)
            : ServiceSalesPolicy.GetCategory(serviceKey, serviceKind);
        var text = ServiceSalesPolicy.GetDisabledMessage(category, operation);
        if (callback != null)
            await SafeAnswerCallbackQueryAsync(client, callback.Id, text, showAlert: true, cancellationToken: token);
        else
            await client.SendMessage(chat, text, cancellationToken: token);
        return false;
    }

    /// <summary>Rechecks explicit stored operation kind before activating an unpaid tenant order.</summary>
    /// <param name="client">Current tenant Telegram transport.</param>
    /// <param name="callback">Authenticated customer callback; its chat receives the denial.</param>
    /// <param name="order">Authorized unpaid order with its immutable service and operation identity.</param>
    /// <param name="token">Cancellation of the customer alert.</param>
    /// <returns>True only while this category and operation allow new activation.</returns>
    /// <remarks>Call only after linked invoices, committed debit receipts and started work have taken their recovery branch. This helper never authorizes settlement.</remarks>
    /// <example><code>if (!await EnsureTenantOrderSalesEnabledAsync(client, callback, order, token)) return;</code></example>
    private Task<bool> EnsureTenantOrderSalesEnabledAsync(ITelegramBotClient client, CallbackQuery callback,
        TenantBotOrder order, CancellationToken token) =>
        EnsureTenantSalesEnabledAsync(client, callback.Message?.Chat.Id ?? callback.From.Id, order.ServiceKey,
            order.OrderKind == TenantBotOrderKinds.Renew ? ServiceSalesOperation.Renewal : ServiceSalesOperation.Sale,
            token, callback, string.IsNullOrWhiteSpace(order.UnlimitedPlanKey) ? null : XuiV3ServiceKinds.Unlimited);

    /// <summary>Checks whether a personal-card order already has submitted money evidence or an admitted panel mutation.</summary>
    /// <param name="order">Already authorized storefront/customer order; this helper grants no identity access.</param>
    /// <param name="token">Cancellation of read-only receipt and operation queries.</param>
    /// <returns>True for a submitted receipt or started/applied/ambiguous provisional operation, never just an unpaid pending order.</returns>
    /// <remarks>Preserves receipt replacement/recovery after closure without permitting a new unsubmitted card activation. Actual receipt images remain ingestible as proof of an external transfer.</remarks>
    /// <example><code>var accepted = await HasAcceptedTenantCardWorkAsync(order, token);</code></example>
    private Task<bool> HasAcceptedTenantCardWorkAsync(TenantBotOrder order, CancellationToken token) =>
        _workflow.ReadAsync(async db =>
            await db.TenantManualPaymentReceipts.AsNoTracking().AnyAsync(receipt => receipt.TenantBotOrderId == order.Id, token)
            || await db.XuiV3CreationOperations.AsNoTracking().AnyAsync(operation =>
                operation.OperationKey == "tenant-card-provisional-create:" + order.OrderId &&
                (operation.PostStartedAtUtc != null || operation.Outcome == XuiV3CreationOutcome.PostStarted
                    || operation.Outcome == XuiV3CreationOutcome.Applied || operation.Outcome == XuiV3CreationOutcome.Ambiguous), token));

    /// <summary>Loads a saved manual plan map once per customer list; corrupt pricing hides only unlimited choices.</summary>
    /// <param name="tenant">Current tenant row whose fixed-plan prices are store-scoped.</param>
    /// <returns>Decoded map, or empty map if its saved manual plan JSON is corrupt; null for percentage mode.</returns>
    /// <remarks>Metered checkout never needs this map. Catalog and provider failures are not caught here.</remarks>
    private static IReadOnlyDictionary<string, Dictionary<string, long>> TenantUnlimitedPricesForList(BotInstance tenant)
    {
        if (tenant?.TenantPricingMode != TenantPricingModes.Manual) return null;
        try { return TenantStorefrontPricing.ParseUnlimitedPlanPrices(tenant.TenantUnlimitedPlanPricesJson); }
        catch (TenantPriceUnavailableException) { return new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Returns only the sale price of a currently priced choice for customer-facing selectors.</summary>
    /// <param name="tenant">Tenant owning the currently enabled choice.</param>
    /// <param name="selection">Catalog service, duration/traffic or fixed plan identity.</param>
    /// <param name="unlimitedPrices">Already decoded map, or null for single-choice resolution.</param>
    /// <param name="sale">Customer whole-toman price if available; zero otherwise.</param>
    /// <returns>False only for a tenant pricing violation, not for a broken global catalog or database operation.</returns>
    /// <remarks>Buyer lists omit a missing/under-cost manual offering, but the calculator still validates checkout.</remarks>
    private bool TryGetTenantSalePrice(BotInstance tenant, XuiV3PurchaseSelection selection,
        IReadOnlyDictionary<string, Dictionary<string, long>> unlimitedPrices, out long sale)
    {
        try
        {
            sale = CalculateTenantPrice(tenant, selection, unlimitedPrices).SalePriceToman;
            return true;
        }
        catch (TenantPriceUnavailableException)
        {
            sale = 0;
            return false;
        }
    }

    /// <summary>Checks current manual unit rates before exposing an enabled metered service to customers.</summary>
    /// <param name="tenant">Storefront containing nullable manual GB/day prices.</param>
    /// <param name="service">Enabled metered catalog entry with current colleague unit floors.</param>
    /// <returns>True when this service can be priced; unknown metered keys fail closed in manual mode.</returns>
    /// <remarks>Rates can become stale after a catalog change without hiding another service or changing existing orders.</remarks>
    private static bool IsTenantMeteredServicePriced(BotInstance tenant, XuiV3ServiceDefinition service)
    {
        if (tenant?.TenantPricingMode == TenantPricingModes.Percent) return true;
        if (tenant?.TenantPricingMode != TenantPricingModes.Manual) return false;
        var normal = string.Equals(service.Key, "normal", StringComparison.OrdinalIgnoreCase);
        var national = string.Equals(service.Key, "national", StringComparison.OrdinalIgnoreCase);
        if (!normal && !national) return false;
        var gb = normal ? tenant.TenantNormalPricePerGbToman : tenant.TenantNationalPricePerGbToman;
        var daily = normal ? tenant.TenantNormalPricePerDayToman : service.GetPricePerDay(true);
        return gb.HasValue && daily.HasValue && gb.Value > 0 &&
            gb.Value >= service.GetPricePerGb(true) && daily.Value >= service.GetPricePerDay(true);
    }

    /// <summary>Checks if an enabled service offers at least one currently priced tenant customer choice.</summary>
    /// <param name="tenant">Storefront owner of the chosen pricing mode.</param>
    /// <param name="service">Enabled global catalog service; unlimited visibility is rechecked.</param>
    /// <param name="unlimitedPrices">Parsed fixed-price map reused for all plan checks.</param>
    /// <returns>False for a missing or below-cost manual offering; percent mode retains existing visibility.</returns>
    /// <remarks>This check only filters menus. Typed input and admission repeat live price resolution.</remarks>
    private static bool IsTenantServicePriced(BotInstance tenant, XuiV3ServiceDefinition service,
        IReadOnlyDictionary<string, Dictionary<string, long>> unlimitedPrices)
    {
        if (!service.IsUnlimited) return IsTenantMeteredServicePriced(tenant, service);
        if (tenant?.TenantPricingMode == TenantPricingModes.Percent) return true;
        if (tenant?.TenantPricingMode != TenantPricingModes.Manual) return false;
        return XuiV3PurchaseService.GetUnlimitedPlansForTenant(service).Any(plan =>
            unlimitedPrices != null && unlimitedPrices.TryGetValue(service.Key, out var plans) &&
            plans.TryGetValue(plan.Key, out var price) && price > 0 && price >= plan.Price.Colleague);
    }
    /// <summary>Appends an explicit customer-safe notice when a renewed service has no priced choices.</summary>
    /// <param name="tenant">Storefront containing current rates.</param>
    /// <param name="service">Enabled service authorized for the renewal target.</param>
    /// <param name="prompt">Current customer selection prompt without owner costs.</param>
    /// <returns>The prompt, or an unavailable-price notice with cancellation guidance.</returns>
    /// <remarks>Global renewal closure takes precedence over a pricing notice, independently of sale permission. Catalog/provider errors are not suppressed.</remarks>
    private string TenantRenewPricingPrompt(BotInstance tenant, XuiV3ServiceDefinition service, string prompt) =>
        !_purchaseService.SalesAvailability.IsEnabled(service, ServiceSalesOperation.Renewal)
            ? ServiceSalesPolicy.GetDisabledMessage(service, ServiceSalesOperation.Renewal)
            : IsTenantServicePriced(tenant, service, TenantUnlimitedPricesForList(tenant))
            ? prompt : "قیمت گزینه‌های این سرویس در حال تنظیم است. می‌توانید انصراف دهید و بعداً دوباره تلاش کنید.";

    /// <summary>Restores a renewal selector when its live manual price is missing or below colleague cost.</summary>
    /// <param name="client">Current storefront Telegram transport.</param>
    /// <param name="chat">Customer chat for the safe unavailable-price notice.</param>
    /// <param name="tenant">Tenant whose current prices and catalog are checked.</param>
    /// <param name="service">Authorized current service for the locked renewal target.</param>
    /// <param name="state">Bot-scoped renewal state; target email and UUID remain intact.</param>
    /// <param name="token">Cancellation of state persistence and Telegram delivery.</param>
    /// <returns>A task after saving the refreshed step and displaying available options or cancel.</returns>
    /// <remarks>No order, invoice, wallet debit, or account renewal is created. Other services remain available.</remarks>
    private async Task RecoverUnavailableTenantRenewPriceAsync(ITelegramBotClient client, ChatId chat,
        BotInstance tenant, XuiV3ServiceDefinition service, User state, CancellationToken token)
    {
        state.SelectedPeriod = string.Empty;
        state.Type = string.Empty;
        state._ConfigPrice = "";
        var traffic = int.TryParse(state.TotoalGB, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedTraffic)
            ? selectedTraffic : 0;
        state.LastStep = service.IsUnlimited ? TENANTRENEWSTEPUNLIMITEDPLAN
            : traffic > 0 && IsTenantMeteredServicePriced(tenant, service)
                ? TENANTRENEWSTEPDURATION : TENANTRENEWSTEPTRAFFIC;
        await _state.SaveUserStatus(state);
        await client.SendMessage(chat, "قیمت این گزینه در حال تنظیم است. گزینه دیگری را انتخاب کنید یا انصراف دهید.",
            replyMarkup: service.IsUnlimited ? BuildTenantRenewUnlimitedKeyboard(service, tenant)
                : state.LastStep == TENANTRENEWSTEPDURATION
                    ? BuildTenantRenewDurationKeyboard(service, tenant, traffic)
                    : BuildTenantRenewTrafficKeyboard(service, tenant),
            cancellationToken: token);
    }
}
