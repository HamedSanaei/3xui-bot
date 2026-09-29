using Adminbot.Domain;
using Adminbot.Services;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;

public partial class TenantBotService
{
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
    /// <remarks>No catalog/provider error is suppressed; this only evaluates saved manual rates.</remarks>
    private static string TenantRenewPricingPrompt(BotInstance tenant, XuiV3ServiceDefinition service, string prompt) =>
        IsTenantServicePriced(tenant, service, TenantUnlimitedPricesForList(tenant))
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
