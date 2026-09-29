using System.Text.Json;
using Adminbot.Domain;

namespace Adminbot.Services;

/// <summary>Identifies an unavailable storefront price without masking catalog, database, or provider failures.</summary>
public sealed class TenantPriceUnavailableException : InvalidOperationException
{
    /// <summary>Describes a missing, corrupt, or below-cost tenant price.</summary>
    /// <param name="message">Safe diagnostic for an owner; never includes credentials or customer identity.</param>
    public TenantPriceUnavailableException(string message) : base(message) { }
}

/// <summary>Validates tenant-owned whole-toman prices against the current colleague catalog before sale.</summary>
/// <remarks>Manual prices belong to one BotInstance; no method here writes a wallet, order, or catalog.</remarks>
public static class TenantStorefrontPricing
{
    /// <summary>Decodes saved service-key/plan-key prices using case-insensitive catalog identities.</summary>
    /// <param name="json">Storefront-owned JSON; null or empty means no configured plans.</param>
    /// <returns>A mutable nested map of nonnegative whole-toman fixed prices; it may be empty.</returns>
    /// <exception cref="TenantPriceUnavailableException">Malformed JSON, case-colliding keys, or invalid amounts.</exception>
    /// <remarks>Keys are exact catalog identities, not customer-facing names. Never deserialize owner callback data as pricing JSON.</remarks>
    /// <example><code>var prices = TenantStorefrontPricing.ParseUnlimitedPlanPrices(tenant.TenantUnlimitedPlanPricesJson);</code></example>
    public static Dictionary<string, Dictionary<string, long>> ParseUnlimitedPlanPrices(string json)
    {
        var prices = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return prices;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new TenantPriceUnavailableException("قیمت پلن‌های نامحدود قالب معتبری ندارد.");
            foreach (var service in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(service.Name) || service.Value.ValueKind != JsonValueKind.Object ||
                    !prices.TryAdd(service.Name, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)))
                    throw new TenantPriceUnavailableException("کلید سرویس نامحدود تکراری یا نامعتبر است.");
                var plans = prices[service.Name];
                foreach (var plan in service.Value.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(plan.Name) || plan.Value.ValueKind != JsonValueKind.Number ||
                        !plan.Value.TryGetInt64(out var amount) || amount < 0 || !plans.TryAdd(plan.Name, amount))
                        throw new TenantPriceUnavailableException("کلید یا مبلغ پلن نامحدود تکراری یا نامعتبر است.");
                }
            }
            return prices;
        }
        catch (JsonException exception)
        {
            throw new TenantPriceUnavailableException("قیمت پلن‌های نامحدود قالب معتبری ندارد: " + exception.Message);
        }
    }

    /// <summary>Checks whether the selected colleague offering has complete, at-least-cost manual rates.</summary>
    /// <param name="tenant">Storefront owner row containing nullable manual whole-toman rates.</param>
    /// <param name="colleague">Current tenant-authorized colleague selection and its current cost.</param>
    /// <param name="unlimitedPrices">Previously decoded service/plan map; unused for metered selections.</param>
    /// <param name="perGbToman">Manual per-GB rate, or zero on failure/unlimited plans.</param>
    /// <param name="perDayToman">Manual daily rate (national uses current colleague daily rate), or zero on failure.</param>
    /// <param name="fixedPriceToman">Manual fixed plan rate, or zero on failure/metered plans.</param>
    /// <returns>True only when all applicable rates are configured and meet their current colleague floors.</returns>
    /// <remarks>A false result is a pricing-availability decision, not catalog authorization. No JSON parse or database read occurs.</remarks>
    /// <example><code>if (!TenantStorefrontPricing.TryResolveManualPrice(tenant, colleague, prices, out var gb, out var day, out var fixedPrice)) return;</code></example>
    public static bool TryResolveManualPrice(BotInstance tenant, XuiV3ResolvedPurchase colleague,
        IReadOnlyDictionary<string, Dictionary<string, long>> unlimitedPrices,
        out long perGbToman, out long perDayToman, out long fixedPriceToman)
    {
        perGbToman = perDayToman = fixedPriceToman = 0;
        if (tenant == null || colleague?.Service == null) return false;
        var service = colleague.Service;
        if (colleague.IsUnlimited)
        {
            return unlimitedPrices != null && colleague.UnlimitedPlan != null &&
                unlimitedPrices.TryGetValue(service.Key, out var plans) &&
                plans.TryGetValue(colleague.UnlimitedPlan.Key, out fixedPriceToman) &&
                fixedPriceToman > 0 && fixedPriceToman >= colleague.PriceToman;
        }
        var normal = string.Equals(service.Key, "normal", StringComparison.OrdinalIgnoreCase);
        var national = string.Equals(service.Key, "national", StringComparison.OrdinalIgnoreCase);
        if (!normal && !national) return false;
        var gb = normal ? tenant.TenantNormalPricePerGbToman : tenant.TenantNationalPricePerGbToman;
        var daily = normal ? tenant.TenantNormalPricePerDayToman : service.GetPricePerDay(isColleague: true);
        if (!gb.HasValue || !daily.HasValue || gb.Value <= 0 ||
            gb.Value < service.GetPricePerGb(isColleague: true) ||
            daily.Value < service.GetPricePerDay(isColleague: true)) return false;
        perGbToman = gb.Value;
        perDayToman = daily.Value;
        return true;
    }

    /// <summary>Lists enabled tenant offerings whose manual prices are missing, invalid, or below current cost.</summary>
    /// <param name="tenant">Exact storefront whose saved or staged prices are checked.</param>
    /// <param name="catalog">Current validated global catalog; disabled services and hidden plans are retained but not mandatory.</param>
    /// <param name="unlimitedPrices">Decoded prices keyed by service and plan, including any inactive saved keys.</param>
    /// <returns>Owner-readable diagnostics, empty only when manual mode can be activated now.</returns>
    /// <remarks>Checks unit floors before whole-order calculations; a new unknown metered service fails closed.</remarks>
    /// <example><code>var errors = TenantStorefrontPricing.ValidateManualCompleteness(tenant, purchaseService.LoadCatalog(), prices);</code></example>
    public static IReadOnlyList<string> ValidateManualCompleteness(BotInstance tenant, XuiV3ServicePlanCatalog catalog,
        IReadOnlyDictionary<string, Dictionary<string, long>> unlimitedPrices)
    {
        var errors = new List<string>();
        foreach (var service in catalog.Services.Where(x => x.IsEnabled))
        {
            if (service.IsUnlimited)
            {
                foreach (var plan in XuiV3PurchaseService.GetUnlimitedPlansForTenant(service))
                {
                    var rate = unlimitedPrices != null && unlimitedPrices.TryGetValue(service.Key, out var plans) &&
                        plans.TryGetValue(plan.Key, out var saved) ? saved : 0;
                    if (rate <= 0 || rate < plan.Price.Colleague)
                        errors.Add($"{service.DisplayName} / {plan.DisplayName}: قیمت مشتری برای پلن ثبت نشده یا از قیمت همکار {plan.Price.Colleague} تومان کمتر است.");
                }
                continue;
            }
            if (string.Equals(service.Key, "normal", StringComparison.OrdinalIgnoreCase))
            {
                CheckRate(errors, service.DisplayName, "هر GB", tenant.TenantNormalPricePerGbToman, service.GetPricePerGb(true), true);
                CheckRate(errors, service.DisplayName, "هر روز", tenant.TenantNormalPricePerDayToman, service.GetPricePerDay(true), false);
            }
            else if (string.Equals(service.Key, "national", StringComparison.OrdinalIgnoreCase))
                CheckRate(errors, service.DisplayName, "هر GB", tenant.TenantNationalPricePerGbToman, service.GetPricePerGb(true), true);
            else errors.Add($"سرویس {service.DisplayName}: قیمت‌گذاری دستی برای این نوع سرویس پشتیبانی نمی‌شود.");
        }
        return errors;
    }

    /// <summary>Calculates the final manual sale once, preserving whole-order ceiling and the colleague floor.</summary>
    /// <param name="tenant">Storefront with active manual prices, scoped to this tenant only.</param>
    /// <param name="colleague">Current authorized offering with original colleague whole-order cost.</param>
    /// <param name="unlimitedPrices">Decoded map for unlimited plans; may be null for a metered selection.</param>
    /// <returns>Positive whole-toman sale price; never a fallback to public or percentage pricing.</returns>
    /// <exception cref="TenantPriceUnavailableException">Missing or under-cost rate/plan, or sale below current colleague total.</exception>
    /// <exception cref="OverflowException">A component or final total exceeds the whole-toman long range.</exception>
    /// <remarks>Finite orders add traffic and duration; lifetime multiplies only the traffic subtotal. No financial side effects.</remarks>
    /// <example><code>var sale = TenantStorefrontPricing.CalculateManualSale(tenant, colleague, prices);</code></example>
    public static long CalculateManualSale(BotInstance tenant, XuiV3ResolvedPurchase colleague,
        IReadOnlyDictionary<string, Dictionary<string, long>> unlimitedPrices)
    {
        if (!TryResolveManualPrice(tenant, colleague, unlimitedPrices, out var gb, out var day, out var fixedPrice))
            throw new TenantPriceUnavailableException($"قیمت {colleague?.Service?.DisplayName ?? "سرویس"} / {colleague?.UnlimitedPlan?.DisplayName ?? "نرخ"} ثبت نشده یا از قیمت همکار کمتر است.");
        if (colleague.IsUnlimited) return fixedPrice;
        var traffic = (decimal)colleague.TrafficGb * gb;
        if (traffic > long.MaxValue) throw new OverflowException("Traffic subtotal exceeds the supported toman range.");
        decimal raw;
        if (colleague.DurationDays == 0)
        {
            var multiplier = colleague.Service.LifetimePriceMultiplier;
            if (!double.IsFinite(multiplier) || multiplier <= 0)
                throw new InvalidOperationException("Lifetime multiplier must be finite and positive.");
            raw = traffic * Convert.ToDecimal(multiplier);
        }
        else
        {
            var duration = (decimal)colleague.DurationDays * day;
            if (duration > long.MaxValue) throw new OverflowException("Duration subtotal exceeds the supported toman range.");
            raw = traffic + duration;
        }
        var rounded = Math.Ceiling(raw);
        if (rounded > long.MaxValue) throw new OverflowException("Sale exceeds the supported toman range.");
        if (rounded <= 0 || rounded < colleague.PriceToman)
            throw new TenantPriceUnavailableException("قیمت کل مشتری از قیمت همکار کمتر است.");
        return (long)rounded;
    }

    /// <summary>Appends a precise active unit-rate violation for owner review.</summary>
    /// <param name="errors">Current mutable owner diagnostics.</param>
    /// <param name="service">Catalog service display label.</param>
    /// <param name="unit">GB or day, in whole units.</param>
    /// <param name="sale">Nullable tenant-owned whole-toman rate.</param>
    /// <param name="cost">Current colleague whole-toman rate.</param>
    /// <param name="positive">Whether zero is prohibited even when the colleague rate is zero.</param>
    /// <remarks>Used only in completeness validation; does not modify the storefront.</remarks>
    private static void CheckRate(List<string> errors, string service, string unit, long? sale, long cost, bool positive)
    {
        if (!sale.HasValue || sale.Value < cost || (positive && sale.Value <= 0))
            errors.Add($"{service} / {unit}: قیمت مشتری ثبت نشده یا از قیمت همکار {cost} تومان کمتر است.");
    }
}
