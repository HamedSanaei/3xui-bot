using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using Telegram.Bot.Types.ReplyMarkups;

public class XuiV3PurchaseService
{
    /// <summary>Maximum custom duration that configuration and customer input may request, in whole days.</summary>
    public const int MaxCustomDurationDays = 365;

    public const int MaxBulkAccountCount = 10;

    /// <summary>Stable catalog key of the only metered service that may accept typed custom durations.</summary>
    private const string NormalServiceKey = "normal";

    /// <summary>Reserved prefix used by canonical custom-duration callback and persisted-state keys.</summary>
    private const string CustomDurationKeyPrefix = "days-";

    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;

    public XuiV3PurchaseService(IConfiguration configuration)
    {
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
    }

    /// <summary>
    /// Loads and validates the global XUI v3 service-plan catalog used by owned and tenant bots.
    /// </summary>
    /// <returns>
    /// A detached in-memory catalog loaded from the configured JSON path. The returned catalog is safe for pricing
    /// only after this method's metered-price validation succeeds.
    /// </returns>
    /// <remarks>
    /// The file is re-read on every call, so operational changes to duration availability and prices take effect
    /// without mutating database state. Invalid financial values fail before a wallet debit, tenant order, or XUI
    /// account creation can use them.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown when the configured catalog file does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a metered daily price, lifetime multiplier, duration-day value, or custom-duration policy is invalid.
    /// </exception>
    /// <example>
    /// <code>
    /// var catalog = purchaseService.LoadCatalog();
    /// var normalService = catalog.Services.First(service => service.Key == "normal");
    /// </code>
    /// </example>
    public XuiV3ServicePlanCatalog LoadCatalog()
    {
        var catalog = XuiV3ServicePlanCatalog.Load(_appConfig.XuiV3ServicePlansPath);
        ValidateMeteredPricingConfiguration(catalog);
        return catalog;
    }

    /// <summary>
    /// Gets services that are globally enabled in the current XUI v3 catalog.
    /// </summary>
    /// <returns>
    /// A detached list of enabled service definitions. The list can be empty and must not be persisted directly.
    /// </returns>
    /// <remarks>
    /// Duration-level availability is handled separately because a metered service can expose some durations while
    /// hiding others. The catalog is reloaded and validated before this list is built.
    /// </remarks>
    /// <example>
    /// <code>
    /// var services = purchaseService.GetEnabledServices();
    /// </code>
    /// </example>
    public IReadOnlyList<XuiV3ServiceDefinition> GetEnabledServices()
    {
        return LoadCatalog().Services
            .Where(s => s.IsEnabled)
            .ToList();
    }

    /// <summary>
    /// Resolves a raw Telegram purchase or renewal selection into the concrete XUI v3 plan, price, traffic, and duration.
    /// </summary>
    /// <param name="selection">
    /// The selected service and either a metered traffic/duration pair or an unlimited plan key.
    /// Metered traffic is expressed in GB and is validated against the service's configured minimum. Duration keys may
    /// reference an enabled preset or a canonical custom-day value such as <c>days-3</c>.
    /// </param>
    /// <param name="isColleague">
    /// Whether colleague base pricing should be used. Tenant storefronts pass <c>false</c> for public sale
    /// pricing and call the same method again with <c>true</c> when calculating owner base cost.
    /// </param>
    /// <returns>
    /// A normalized purchase result containing the enabled service and plan, traffic bytes, duration days, limit IP,
    /// and whole-toman unit price. Metered results also contain the authoritative component breakdown used to explain
    /// that same unit price. The returned object is safe to use for account creation and invoice totals.
    /// </returns>
    /// <remarks>
    /// This method is the shared financial policy gate for owned bots and tenant bots. Metered finite durations add
    /// role-specific daily cost to traffic cost; zero-day durations multiply only the traffic cost. Custom-day keys are
    /// revalidated against the current normal-service policy. Disabled presets, stale callbacks, and invalid typed
    /// values are rejected before wallet, tenant-order, ledger, or XUI side effects.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="selection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the service, unlimited plan, duration, traffic, configured minimum, or financial setting is invalid.
    /// </exception>
    /// <exception cref="OverflowException">
    /// Thrown when a valid metered formula produces an amount outside the supported signed 64-bit toman range.
    /// </exception>
    /// <example>
    /// <code>
    /// var resolved = purchaseService.ResolvePurchase(
    ///     new XuiV3PurchaseSelection
    ///     {
    ///         ServiceKey = "normal",
    ///         TrafficGb = 10,
    ///         DurationKey = "m2"
    ///     },
    ///     isColleague: false);
    /// </code>
    /// </example>
    public XuiV3ResolvedPurchase ResolvePurchase(XuiV3PurchaseSelection selection, bool isColleague)
    {
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        var catalog = LoadCatalog();
        var service = catalog.Services.FirstOrDefault(s =>
            string.Equals(s.Key, selection.ServiceKey, StringComparison.OrdinalIgnoreCase) && s.IsEnabled);

        if (service == null)
            throw new InvalidOperationException($"Service plan '{selection.ServiceKey}' was not found or is disabled.");

        if (service.IsUnlimited)
        {
            var unlimitedPlan = service.UnlimitedPlans.FirstOrDefault(p =>
                p.IsEnabled &&
                string.Equals(p.Key, selection.UnlimitedPlanKey, StringComparison.OrdinalIgnoreCase));

            if (unlimitedPlan == null)
                throw new InvalidOperationException($"Unlimited plan '{selection.UnlimitedPlanKey}' was not found or is disabled.");

            return new XuiV3ResolvedPurchase
            {
                Service = service,
                UnlimitedPlan = unlimitedPlan,
                TrafficGb = unlimitedPlan.FairUsageGb,
                TrafficBytes = ApiService.ConvertGBToBytes(unlimitedPlan.FairUsageGb),
                DurationDays = unlimitedPlan.Days,
                LimitIp = unlimitedPlan.MaxUsers,
                PriceToman = unlimitedPlan.Price.GetForRole(isColleague),
                IsUnlimited = true
            };
        }

        if (selection.TrafficGb == null || selection.TrafficGb <= 0)
            throw new InvalidOperationException("TrafficGb is required for metered plans.");

        var minimumTrafficGb = GetMinimumTrafficGb(service);
        if (selection.TrafficGb.Value < minimumTrafficGb)
            throw new InvalidOperationException($"Minimum traffic for service '{service.Key}' is {minimumTrafficGb} GB.");

        if (!TryResolveDurationKey(service, selection.DurationKey, out var duration))
            throw new InvalidOperationException($"Duration '{selection.DurationKey}' is not configured or is disabled for service '{service.Key}'.");

        var priceBreakdown = CalculateMeteredPriceBreakdown(
            service,
            duration,
            selection.TrafficGb.Value,
            isColleague);

        return new XuiV3ResolvedPurchase
        {
            Service = service,
            Duration = duration,
            TrafficGb = selection.TrafficGb.Value,
            TrafficBytes = ApiService.ConvertGBToBytes(selection.TrafficGb.Value),
            DurationDays = duration.Days,
            LimitIp = 0,
            PriceToman = priceBreakdown.TotalPriceToman,
            MeteredPriceBreakdown = priceBreakdown,
            IsUnlimited = false
        };
    }

    public InlineKeyboardMarkup BuildServiceKeyboard()
    {
        var rows = GetEnabledServices()
            .Select(service => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    service.DisplayName,
                    XuiV3PurchaseCallbacks.Service(service.Key))
            })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Builds the metered traffic selection keyboard for an enabled XUI v3 service.
    /// </summary>
    /// <param name="serviceKey">Configured service key from <c>xui-v3-service-plans.json</c>.</param>
    /// <returns>
    /// An inline keyboard containing only configured traffic options that satisfy the service minimum, plus a back button.
    /// </returns>
    /// <remarks>
    /// The minimum filter keeps owned-bot callback options consistent with typed traffic validation and tenant
    /// storefront pricing. Custom typed traffic may still exceed the shown options.
    /// </remarks>
    public InlineKeyboardMarkup BuildTrafficKeyboard(string serviceKey)
    {
        var service = FindService(serviceKey);
        var rows = GetVisibleTrafficOptions(service)
            .Chunk(2)
            .Select(chunk => chunk
                .Select(gb => InlineKeyboardButton.WithCallbackData(
                    $"{gb} GB",
                    XuiV3PurchaseCallbacks.Traffic(service.Key, gb)))
                .ToArray())
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices()) })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Gets the effective minimum metered traffic for a service.
    /// </summary>
    /// <param name="service">
    /// Service definition loaded from the plan file. Unlimited services may be passed, but the result is used
    /// only for metered services.
    /// </param>
    /// <returns>
    /// Minimum traffic in GB. Missing, zero, or negative configuration values fall back to <c>1</c> GB.
    /// </returns>
    /// <remarks>
    /// This helper is static so owned-bot and tenant-bot state machines can use the same policy before calling
    /// <see cref="ResolvePurchase"/> and can show a friendly Persian error instead of surfacing an exception.
    /// </remarks>
    public static int GetMinimumTrafficGb(XuiV3ServiceDefinition service)
    {
        return Math.Max(1, service?.MinimumTrafficGb ?? 1);
    }

    /// <summary>
    /// Returns traffic options that should be shown to customers for a metered service.
    /// </summary>
    /// <param name="service">Metered service definition loaded from the plan file.</param>
    /// <returns>
    /// Configured traffic options in ascending order after removing values below the service minimum. The
    /// collection can be empty when the plan file has no visible preset values.
    /// </returns>
    /// <remarks>
    /// The method does not limit custom typed traffic; it only controls preset keyboard buttons.
    /// </remarks>
    public static IReadOnlyList<int> GetVisibleTrafficOptions(XuiV3ServiceDefinition service)
    {
        var minimumTrafficGb = GetMinimumTrafficGb(service);
        return service?.TrafficOptionsGb?
            .Where(gb => gb >= minimumTrafficGb)
            .Distinct()
            .OrderBy(gb => gb)
            .ToList() ?? new List<int>();
    }

    /// <summary>
    /// Returns the metered duration options that can currently be displayed and selected.
    /// </summary>
    /// <param name="service">
    /// Metered service definition loaded from the global XUI v3 catalog. A null service is allowed and produces an
    /// empty collection.
    /// </param>
    /// <returns>
    /// Enabled, non-null duration definitions in their configured order. The collection can be empty and is detached
    /// configuration data that callers must not persist.
    /// </returns>
    /// <remarks>
    /// Owned purchase, owned renewal, super-admin creation, and tenant purchase/renewal flows must use this helper so
    /// a disabled option cannot remain visible in one Telegram surface. <see cref="ResolvePurchase" /> repeats the
    /// check as the final financial policy gate for stale callbacks.
    /// </remarks>
    /// <example>
    /// <code>
    /// foreach (var duration in XuiV3PurchaseService.GetEnabledDurationOptions(service))
    /// {
    ///     // Build one Telegram button for the enabled duration.
    /// }
    /// </code>
    /// </example>
    public static IReadOnlyList<XuiV3DurationOption> GetEnabledDurationOptions(XuiV3ServiceDefinition service)
    {
        return service?.DurationOptions?
            .Where(duration => duration?.IsEnabled == true)
            .ToList() ?? new List<XuiV3DurationOption>();
    }

    /// <summary>
    /// Determines whether the current service policy allows customers to type a custom finite duration.
    /// </summary>
    /// <param name="service">
    /// Service definition loaded from the validated global catalog. Null, national, unlimited, and disabled policies
    /// return <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> only for the metered <c>normal</c> service when <c>customDurationDays.isEnabled</c> is enabled.
    /// </returns>
    /// <remarks>
    /// Catalog loading validates the configured range separately. This helper only answers whether customer-facing
    /// prompts and parsers should expose the capability; it has no financial or persistence side effects.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (XuiV3PurchaseService.SupportsCustomDurationDays(service))
    ///     prompt = XuiV3PurchaseService.BuildDurationSelectionText(service, prompt);
    /// </code>
    /// </example>
    public static bool SupportsCustomDurationDays(XuiV3ServiceDefinition service)
    {
        return service != null &&
               string.Equals(service.Kind, XuiV3ServiceKinds.Metered, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(service.Key, NormalServiceKey, StringComparison.OrdinalIgnoreCase) &&
               service.CustomDurationDays?.IsEnabled == true;
    }

    /// <summary>
    /// Resolves a configured duration key or canonical <c>days-N</c> key against the current service policy.
    /// </summary>
    /// <param name="service">Enabled metered service loaded from the current global catalog.</param>
    /// <param name="durationKey">
    /// Configured duration key such as <c>m1</c>, or canonical custom key such as <c>days-3</c>. Null and empty values
    /// are invalid.
    /// </param>
    /// <param name="duration">
    /// The enabled configured duration or a detached custom duration when resolution succeeds; otherwise <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> when the key is currently allowed for the service; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Configured keys honor their own <c>isEnabled</c> value. Custom keys are independent of preset availability and
    /// are accepted only when their day count remains inside the current custom-duration range. This method has no
    /// wallet, database, Telegram, tenant-order, ledger, or XUI side effects.
    /// </remarks>
    /// <example>
    /// <code>
    /// var valid = XuiV3PurchaseService.TryResolveDurationKey(service, "days-3", out var duration);
    /// </code>
    /// </example>
    public static bool TryResolveDurationKey(
        XuiV3ServiceDefinition service,
        string durationKey,
        out XuiV3DurationOption duration)
    {
        duration = null;
        if (service == null || string.IsNullOrWhiteSpace(durationKey))
            return false;

        var normalizedKey = durationKey.Trim();
        duration = GetEnabledDurationOptions(service).FirstOrDefault(option =>
            string.Equals(option.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (duration != null)
            return true;

        if (!TryParseCanonicalCustomDurationKey(normalizedKey, out var customDays) ||
            !SupportsCustomDurationDays(service))
        {
            return false;
        }

        var policy = service.CustomDurationDays;
        if (customDays < policy.MinimumDays || customDays > policy.MaximumDays)
            return false;

        duration = new XuiV3DurationOption
        {
            Key = BuildCustomDurationKey(customDays),
            DisplayName = $"{customDays} روز (دلخواه)",
            Days = customDays,
            IsEnabled = true
        };
        return true;
    }

    /// <summary>
    /// Resolves a Telegram duration reply as either an enabled preset or a custom whole-day number.
    /// </summary>
    /// <param name="service">Enabled metered service that owns the duration selection step.</param>
    /// <param name="text">
    /// Raw Telegram text. Preset keys, labels, bracketed keys, and digit-only Latin, Persian, or Arabic-Indic custom
    /// day values are accepted. Negative, decimal, unit-suffixed, and free-form values are rejected.
    /// </param>
    /// <param name="duration">
    /// The resolved configured or detached custom duration when valid; otherwise <c>null</c>.
    /// </param>
    /// <returns><c>true</c> when the input represents a currently allowed duration; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// A digit-only reply is always interpreted as an independent custom selection, even when the same day count has
    /// a disabled preset. Final purchase resolution repeats the policy check before any financial side effect.
    /// </remarks>
    /// <example>
    /// <code>
    /// var valid = XuiV3PurchaseService.TryResolveDurationInput(service, "۳", out var duration);
    /// // duration.Key == "days-3" and duration.Days == 3
    /// </code>
    /// </example>
    public static bool TryResolveDurationInput(
        XuiV3ServiceDefinition service,
        string text,
        out XuiV3DurationOption duration)
    {
        duration = null;
        var trimmed = text?.Trim();
        if (service == null || string.IsNullOrWhiteSpace(trimmed))
            return false;

        var normalizedDigits = NormalizeUnicodeDigits(trimmed);
        if (normalizedDigits.All(character => character is >= '0' and <= '9') &&
            int.TryParse(
                normalizedDigits,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var customDays))
        {
            return TryResolveDurationKey(service, BuildCustomDurationKey(customDays), out duration);
        }

        var bracketStart = trimmed.LastIndexOf('[');
        var bracketEnd = trimmed.LastIndexOf(']');
        if (bracketStart >= 0 && bracketEnd > bracketStart + 1)
        {
            var bracketedKey = trimmed.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();
            if (TryResolveDurationKey(service, bracketedKey, out duration))
                return true;
        }

        if (TryResolveDurationKey(service, trimmed, out duration))
            return true;

        duration = GetEnabledDurationOptions(service).FirstOrDefault(option =>
            string.Equals(option.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        return duration != null;
    }

    /// <summary>
    /// Appends custom-day instructions and the configured range to a duration-selection Telegram prompt.
    /// </summary>
    /// <param name="service">Current service whose validated custom-duration policy controls the guidance.</param>
    /// <param name="heading">Existing Persian prompt or validation message shown before the guidance.</param>
    /// <returns>
    /// The original heading when custom input is disabled; otherwise plain Persian text that explains digit-only input,
    /// includes the example “number 3 means 3 days,” and shows the current inclusive range.
    /// </returns>
    /// <remarks>The returned text contains no Telegram markup and is safe in plain-text or HTML messages.</remarks>
    /// <example>
    /// <code>
    /// var text = XuiV3PurchaseService.BuildDurationSelectionText(service, "مدت را انتخاب کنید:");
    /// </code>
    /// </example>
    public static string BuildDurationSelectionText(XuiV3ServiceDefinition service, string heading)
    {
        var prompt = heading?.TrimEnd() ?? string.Empty;
        if (!SupportsCustomDurationDays(service))
            return prompt;

        var policy = service.CustomDurationDays;
        return prompt +
               "\n\nیا تعداد روز دلخواه را فقط به‌صورت عدد بفرستید." +
               "\nمثلاً عدد 3 یعنی 3 روز." +
               $"\nبازه مجاز: {policy.MinimumDays} تا {policy.MaximumDays} روز.";
    }

    /// <summary>
    /// Formats a persisted duration key for user-visible order details without exposing an internal custom key.
    /// </summary>
    /// <param name="durationKey">Configured duration key or canonical custom key stored in state or an order.</param>
    /// <returns>
    /// A Persian custom-day label such as <c>3 روز (دلخواه)</c> for <c>days-3</c>; otherwise the original key. Null and
    /// empty input returns an empty string.
    /// </returns>
    /// <remarks>This helper only formats persisted data and does not validate current purchase eligibility.</remarks>
    /// <example>
    /// <code>
    /// var label = XuiV3PurchaseService.FormatDurationSelectionKey("days-3");
    /// </code>
    /// </example>
    public static string FormatDurationSelectionKey(string durationKey)
    {
        if (TryParseCanonicalCustomDurationKey(durationKey, out var days))
            return $"{days} روز (دلخواه)";

        return durationKey ?? string.Empty;
    }

    /// <summary>
    /// Calculates every authoritative price component for one metered service account.
    /// </summary>
    /// <param name="service">
    /// Enabled metered service loaded from the global catalog. Its prices are Iranian toman major units and must be
    /// non-negative for the selected role.
    /// </param>
    /// <param name="duration">
    /// Enabled duration owned by <paramref name="service" />. Zero days means lifetime; positive values are charged
    /// per day; negative values are invalid.
    /// </param>
    /// <param name="trafficGb">Purchased traffic in whole GB. The value must be greater than zero.</param>
    /// <param name="isColleague">
    /// <c>true</c> to use colleague per-GB and per-day rates; <c>false</c> to use normal-customer rates.
    /// </param>
    /// <returns>
    /// A detached breakdown containing role rates, traffic and duration subtotals, the exact pre-rounding total, and
    /// the final whole-toman unit price. It is safe to expose in owned-bot confirmation messages but must not be
    /// independently persisted as a ledger movement.
    /// </returns>
    /// <remarks>
    /// Finite formula: <c>trafficGb * pricePerGb + durationDays * pricePerDay</c>.
    /// Lifetime formula: <c>trafficGb * pricePerGb * lifetimePriceMultiplier</c>; no daily fee is charged.
    /// This method has no database, wallet, Telegram, or XUI side effects.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="service" /> or <paramref name="duration" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown for an unlimited service, non-positive traffic, negative price, negative duration, or invalid lifetime multiplier.
    /// </exception>
    /// <exception cref="OverflowException">Thrown when the calculated amount cannot fit in a signed 64-bit toman value.</exception>
    /// <example>
    /// <code>
    /// var breakdown = XuiV3PurchaseService.CalculateMeteredPriceBreakdown(
    ///     service,
    ///     duration,
    ///     trafficGb: 10,
    ///     isColleague: false);
    ///
    /// var payableUnitPrice = breakdown.TotalPriceToman;
    /// </code>
    /// </example>
    public static XuiV3MeteredPriceBreakdown CalculateMeteredPriceBreakdown(
        XuiV3ServiceDefinition service,
        XuiV3DurationOption duration,
        int trafficGb,
        bool isColleague)
    {
        if (service == null)
            throw new ArgumentNullException(nameof(service));
        if (duration == null)
            throw new ArgumentNullException(nameof(duration));
        if (service.IsUnlimited)
            throw new InvalidOperationException($"Service '{service.Key}' uses fixed unlimited-plan pricing.");
        if (trafficGb <= 0)
            throw new InvalidOperationException("Metered traffic must be greater than zero GB.");
        if (duration.Days < 0)
            throw new InvalidOperationException($"Duration '{duration.Key}' cannot have negative days.");

        var pricePerGb = service.GetPricePerGb(isColleague);
        if (pricePerGb < 0)
            throw new InvalidOperationException($"Service '{service.Key}' cannot have a negative per-GB price.");

        var trafficSubtotal = (decimal)trafficGb * pricePerGb;
        if (trafficSubtotal > long.MaxValue)
            throw new OverflowException($"Traffic subtotal for service '{service.Key}' exceeds the supported toman range.");

        var isLifetime = duration.Days == 0;
        var pricePerDay = 0L;
        var durationSubtotal = 0M;
        decimal rawTotal;
        if (isLifetime)
        {
            if (!double.IsFinite(service.LifetimePriceMultiplier) || service.LifetimePriceMultiplier <= 0D)
                throw new InvalidOperationException($"Service '{service.Key}' must have a positive finite lifetime multiplier.");

            // Convert the validated JSON double to decimal before multiplying money so binary floating-point error
            // never changes the whole-toman amount charged to a wallet or tenant order.
            var multiplier = Convert.ToDecimal(service.LifetimePriceMultiplier);
            rawTotal = trafficSubtotal * multiplier;
        }
        else
        {
            pricePerDay = service.GetPricePerDay(isColleague);
            if (pricePerDay < 0)
                throw new InvalidOperationException($"Service '{service.Key}' cannot have a negative daily price.");

            durationSubtotal = (decimal)duration.Days * pricePerDay;
            if (durationSubtotal > long.MaxValue)
                throw new OverflowException($"Duration subtotal for service '{service.Key}' exceeds the supported toman range.");

            rawTotal = trafficSubtotal + durationSubtotal;
        }

        var roundedPrice = Math.Ceiling(rawTotal);
        if (roundedPrice > long.MaxValue)
            throw new OverflowException($"Calculated price for service '{service.Key}' exceeds the supported toman range.");

        return new XuiV3MeteredPriceBreakdown
        {
            TrafficGb = trafficGb,
            PricePerGbToman = pricePerGb,
            TrafficSubtotalToman = (long)trafficSubtotal,
            DurationDays = duration.Days,
            PricePerDayToman = pricePerDay,
            DurationSubtotalToman = (long)durationSubtotal,
            LifetimePriceMultiplier = isLifetime ? service.LifetimePriceMultiplier : 1D,
            IsLifetime = isLifetime,
            RawTotalToman = rawTotal,
            TotalPriceToman = (long)roundedPrice
        };
    }

    /// <summary>
    /// Calculates the final whole-toman unit price for a metered service selection.
    /// </summary>
    /// <param name="service">Enabled global metered service containing role-specific rates.</param>
    /// <param name="duration">Enabled configured duration; zero days represents lifetime.</param>
    /// <param name="trafficGb">Purchased traffic in whole GB; must be greater than zero.</param>
    /// <param name="isColleague"><c>true</c> for colleague rates; <c>false</c> for normal-customer rates.</param>
    /// <returns>The final upward-rounded unit price in Iranian toman.</returns>
    /// <remarks>
    /// This compatibility helper delegates to <see cref="CalculateMeteredPriceBreakdown" /> so pricing and displayed
    /// breakdowns cannot diverge. It has no database, wallet, Telegram, ledger, or XUI side effects.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="service" /> or <paramref name="duration" /> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the metered selection or configured rates are invalid.</exception>
    /// <exception cref="OverflowException">Thrown when the price exceeds the supported signed 64-bit toman range.</exception>
    /// <example>
    /// <code>
    /// var unitPrice = XuiV3PurchaseService.CalculateMeteredPriceToman(
    ///     service,
    ///     duration,
    ///     trafficGb: 10,
    ///     isColleague: false);
    /// </code>
    /// </example>
    public static long CalculateMeteredPriceToman(
        XuiV3ServiceDefinition service,
        XuiV3DurationOption duration,
        int trafficGb,
        bool isColleague)
    {
        return CalculateMeteredPriceBreakdown(service, duration, trafficGb, isColleague).TotalPriceToman;
    }

    /// <summary>
    /// Formats an authoritative metered unit-price breakdown for a Telegram confirmation message.
    /// </summary>
    /// <param name="resolved">
    /// Purchase result returned by <see cref="ResolvePurchase" />. Unlimited results and null values are allowed and
    /// produce an empty string.
    /// </param>
    /// <returns>
    /// Plain Persian text listing GB quantity, per-GB rate, traffic subtotal, and either day-rate subtotal or lifetime
    /// multiplier, followed by the unit total. The text contains no Telegram markup and is safe to embed in both plain
    /// messages and HTML messages.
    /// </returns>
    /// <remarks>
    /// This method only formats <see cref="XuiV3ResolvedPurchase.MeteredPriceBreakdown" /> and never recalculates a
    /// payable amount. It has no wallet, database, ledger, Telegram-send, tenant, or XUI side effects.
    /// </remarks>
    /// <example>
    /// <code>
    /// var resolved = purchaseService.ResolvePurchase(selection, isColleague: false);
    /// var details = XuiV3PurchaseService.BuildMeteredPriceBreakdownText(resolved);
    /// </code>
    /// </example>
    public static string BuildMeteredPriceBreakdownText(XuiV3ResolvedPurchase resolved)
    {
        var breakdown = resolved?.MeteredPriceBreakdown;
        if (breakdown == null)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("جزئیات محاسبه قیمت هر اکانت:");
        builder.AppendLine(
            $"• حجم: {breakdown.TrafficGb} GB × نرخ هر گیگ {breakdown.PricePerGbToman.FormatCurrency()} " +
            $"= جمع حجم {breakdown.TrafficSubtotalToman.FormatCurrency()}");

        if (breakdown.IsLifetime)
        {
            var roundingText = breakdown.RawTotalToman == breakdown.TotalPriceToman
                ? string.Empty
                : " (گرد شده رو به بالا)";
            builder.AppendLine(
                $"• زمان نامحدود: جمع حجم × ضریب {FormatLifetimeMultiplier(breakdown.LifetimePriceMultiplier)} " +
                $"= {breakdown.TotalPriceToman.FormatCurrency()}{roundingText}");
        }
        else
        {
            builder.AppendLine(
                $"• زمان: {breakdown.DurationDays} روز × نرخ هر روز {breakdown.PricePerDayToman.FormatCurrency()} " +
                $"= جمع زمان {breakdown.DurationSubtotalToman.FormatCurrency()}");
        }

        builder.Append($"• جمع قیمت هر اکانت: {breakdown.TotalPriceToman.FormatCurrency()}");
        return builder.ToString();
    }

    /// <summary>
    /// Checks whether a metered traffic amount satisfies the configured service minimum.
    /// </summary>
    /// <param name="service">Metered service definition that owns the traffic policy.</param>
    /// <param name="trafficGb">Customer-selected traffic amount in GB.</param>
    /// <returns>
    /// <c>true</c> when <paramref name="trafficGb"/> is greater than or equal to the configured minimum; otherwise <c>false</c>.
    /// </returns>
    public static bool MeetsMinimumTraffic(XuiV3ServiceDefinition service, int trafficGb)
    {
        return trafficGb >= GetMinimumTrafficGb(service);
    }

    /// <summary>
    /// Builds the owned-bot duration keyboard for a metered service and traffic selection.
    /// </summary>
    /// <param name="serviceKey">Global service key from <c>xui-v3-service-plans.json</c>.</param>
    /// <param name="trafficGb">Previously selected traffic in whole GB, encoded into each callback.</param>
    /// <returns>An inline keyboard containing only enabled durations plus a back button.</returns>
    /// <remarks>
    /// This method only builds Telegram callback data. The callback handler and <see cref="ResolvePurchase" /> must
    /// still revalidate the duration because the catalog can change after the message is sent.
    /// </remarks>
    /// <example>
    /// <code>
    /// var keyboard = purchaseService.BuildDurationKeyboard("normal", 30);
    /// </code>
    /// </example>
    public InlineKeyboardMarkup BuildDurationKeyboard(string serviceKey, int trafficGb)
    {
        var service = FindService(serviceKey);
        var rows = GetEnabledDurationOptions(service)
            .Select(duration => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    duration.DisplayName,
                    XuiV3PurchaseCallbacks.Duration(service.Key, trafficGb, duration.Key))
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.Service(service.Key)) })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildUnlimitedPlanKeyboard(string serviceKey, bool isColleague)
    {
        var service = FindService(serviceKey);
        var rows = service.UnlimitedPlans
            .Where(p => p.IsEnabled && p.Price.GetForRole(isColleague) > 0)
            .Select(plan => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{plan.DisplayName} - {plan.Price.GetForRole(isColleague).FormatCurrency()}",
                    XuiV3PurchaseCallbacks.UnlimitedPlan(service.Key, plan.Key))
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices()) })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildConfirmKeyboard(XuiV3PurchaseSelection selection)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("تایید نهایی", XuiV3PurchaseCallbacks.Confirm(selection)),
                InlineKeyboardButton.WithCallbackData("انصراف", XuiV3PurchaseCallbacks.Cancel())
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices())
            }
        });
    }

    /// <summary>
    /// Builds the final owned-bot purchase confirmation shown before any wallet debit or XUI account creation.
    /// </summary>
    /// <param name="selection">
    /// Current owned-bot purchase selection containing the global service key, metered traffic/duration or unlimited
    /// plan key, account count, and optional user comment.
    /// </param>
    /// <param name="isColleague">
    /// <c>true</c> when the current credentials profile receives colleague rates; otherwise <c>false</c> for public rates.
    /// </param>
    /// <returns>
    /// Plain Persian Telegram text containing the selected plan, authoritative per-account and order totals, and a
    /// detailed metered price breakdown when applicable. Dynamic user comments remain plain text.
    /// </returns>
    /// <remarks>
    /// The message is preview-only. It reads the current global catalog through <see cref="ResolvePurchase" /> but does
    /// not debit a wallet, write a ledger row, create an XUI account, or send Telegram content itself.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the selected service, duration, or pricing configuration is invalid.</exception>
    /// <exception cref="OverflowException">Thrown when the configured price exceeds the supported toman range.</exception>
    /// <example>
    /// <code>
    /// var text = purchaseService.BuildSummaryText(selection, credUser.IsColleague);
    /// </code>
    /// </example>
    public string BuildSummaryText(XuiV3PurchaseSelection selection, bool isColleague)
    {
        var resolved = ResolvePurchase(selection, isColleague);
        return BuildSummaryText(selection, resolved);
    }

    /// <summary>
    /// Builds the final owned-bot purchase confirmation from an already resolved, immutable price snapshot.
    /// </summary>
    /// <param name="selection">
    /// Current owned-bot selection. Its account count and optional comment are presentation values; its plan fields
    /// must be the same values that produced <paramref name="resolved"/>.
    /// </param>
    /// <param name="resolved">
    /// Authoritative result returned by <see cref="ResolvePurchase(XuiV3PurchaseSelection, bool)"/> for the same
    /// selection and buyer role. The value contains the per-account toman price and metered price breakdown.
    /// </param>
    /// <returns>
    /// Plain Persian Telegram text containing the selected plan, per-account and total prices, and the detailed
    /// metered calculation. The method does not reread the catalog or recalculate the price.
    /// </returns>
    /// <remarks>
    /// This overload lets a caller use one catalog snapshot for the summary, total payable amount, and payment-method
    /// eligibility. Final purchase confirmation must still call the central resolver again so catalog changes made
    /// after the preview fail closed before any wallet, ledger, order, or XUI effect.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="selection"/> or <paramref name="resolved"/> is null.
    /// </exception>
    /// <example>
    /// <code>
    /// var resolved = purchaseService.ResolvePurchase(selection, credUser.IsColleague);
    /// var text = purchaseService.BuildSummaryText(selection, resolved);
    /// </code>
    /// </example>
    public string BuildSummaryText(
        XuiV3PurchaseSelection selection,
        XuiV3ResolvedPurchase resolved)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(resolved);

        var accountCount = NormalizeAccountCount(selection.AccountCount);
        var totalPrice = resolved.PriceToman * accountCount;
        var text = "سفارش جدید\n";
        text += $"نوع سرویس: {resolved.Service.DisplayName}\n";
        text += $"تعداد اکانت: {accountCount}\n";
        text += resolved.IsUnlimited
            ? $"حد مصرف منصفانه هر اکانت: {FormatTrafficSize(resolved.TrafficBytes, resolved.TrafficGb)}\n"
            : $"حجم هر اکانت: {FormatTrafficSize(resolved.TrafficBytes, resolved.TrafficGb)}\n";

        text += resolved.DurationDays <= 0
            ? "مدت: نامحدود\n"
            : $"مدت: {resolved.DurationDays} روز\n";

        if (resolved.IsUnlimited)
            text += $"تعداد کاربر مجاز: {resolved.LimitIp}\n";

        var priceBreakdownText = BuildMeteredPriceBreakdownText(resolved);
        if (!string.IsNullOrWhiteSpace(priceBreakdownText))
            text += $"\n{priceBreakdownText}\n";

        text += $"قیمت هر اکانت: {resolved.PriceToman.FormatCurrency()}\n";
        text += $"قیمت کل: {totalPrice.FormatCurrency()}\n";
        if (!string.IsNullOrWhiteSpace(selection.UserComment))
            text += $"کامنت: {selection.UserComment}\n";

        text += "\nبرای ساخت اکانت، تایید نهایی را بزنید.";
        return text;
    }

    /// <summary>
    /// Builds the HTML tariff message shown to owned-bot customers and colleagues.
    /// </summary>
    /// <param name="isColleague">
    /// <c>true</c> when the caller is a colleague and colleague prices should be shown;
    /// <c>false</c> when normal customer prices should be shown.
    /// </param>
    /// <returns>
    /// HTML-formatted Persian text that is safe to send with <c>ParseMode.Html</c>. The text includes only enabled
    /// plans, role-specific per-GB/per-day prices, applicable lifetime multipliers, and valid traffic presets.
    /// </returns>
    /// <remarks>
    /// The tariff message is derived from <c>xui-v3-service-plans.json</c> and omits disabled durations. This method
    /// does not persist data or calculate a payable invoice; <see cref="ResolvePurchase" /> remains authoritative.
    /// </remarks>
    /// <example>
    /// <code>
    /// var text = purchaseService.BuildTariffsText(credUser.IsColleague);
    /// await botClient.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html);
    /// </code>
    /// </example>
    public string BuildTariffsText(bool isColleague)
    {
        var roleText = isColleague ? "همکار" : "کاربر عادی";
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("📋 <b>تعرفه سرویس‌ها</b>");
        builder.AppendLine($"نوع حساب شما: <code>{Html(roleText)}</code>");
        builder.AppendLine();
        builder.AppendLine("⭐ پیشنهاد ما برای استفاده روزمره، پلن‌های نامحدود با حد مصرف منصفانه است.");
        builder.AppendLine("🛡 برای شرایط قطعی اینترنت، اختلال شدید یا شرایط جنگی، حتماً یک کانفیگ <b>نت ملی</b> با زمان انقضای <b>نامحدود</b> هم داشته باشید.");
        builder.AppendLine("📍 در حال حاضر لوکیشن‌های آلمان، آمریکا و فنلاند فعال هستند و به‌زودی لوکیشن‌های بیشتری مثل ترکیه هم اضافه می‌شود.");

        foreach (var service in GetEnabledServices())
        {
            builder.AppendLine();
            builder.AppendLine("━━━━━━━━━━━━");

            if (service.IsUnlimited)
            {
                builder.AppendLine($"♾ <b>{Html(service.DisplayName)}</b>");
                var plans = service.UnlimitedPlans?
                    .Where(plan => plan.IsEnabled)
                    .OrderBy(plan => plan.Days)
                    .ToList() ?? new List<XuiV3UnlimitedPlan>();

                foreach (var plan in plans)
                {
                    builder.AppendLine($"• <b>{Html(plan.DisplayName)}</b>");
                    builder.AppendLine($"  مدت: <code>{plan.Days} روز</code> | حد مصرف منصفانه: <code>{plan.FairUsageGb} GB</code>");
                    builder.AppendLine($"  کاربران مجاز: <code>{plan.MaxUsers}</code> | قیمت: <code>{Html(plan.Price.GetForRole(isColleague).FormatCurrency())}</code>");
                }
            }
            else
            {
                var titleIcon = string.Equals(service.Key, "national", StringComparison.OrdinalIgnoreCase) ? "🛡" : "🌐";
                builder.AppendLine($"{titleIcon} <b>{Html(service.DisplayName)}</b>");
                builder.AppendLine($"قیمت هر گیگ: <code>{Html(service.GetPricePerGb(isColleague).FormatCurrency())}</code>");

                var visibleTrafficOptions = GetVisibleTrafficOptions(service);
                if (visibleTrafficOptions.Count > 0)
                    builder.AppendLine($"حجم‌ها: <code>{Html(string.Join(" / ", visibleTrafficOptions.Select(x => $"{x}GB")))}</code>");

                var enabledDurations = GetEnabledDurationOptions(service);
                var hasDailyPrice = (service.PricePerDay?.User ?? 0L) > 0 ||
                                    (service.PricePerDay?.Colleague ?? 0L) > 0;
                if (hasDailyPrice && enabledDurations.Any(duration => duration.Days > 0))
                    builder.AppendLine($"هزینه هر روز: <code>{Html(service.GetPricePerDay(isColleague).FormatCurrency())}</code>");
                if ((hasDailyPrice || service.LifetimePriceMultiplier != 1D) &&
                    enabledDurations.Any(duration => duration.Days == 0))
                    builder.AppendLine($"ضریب مدت نامحدود: <code>{Html(FormatLifetimeMultiplier(service.LifetimePriceMultiplier))}</code>");

                var durations = enabledDurations
                    .OrderBy(duration => duration.Days)
                    .Select(duration => duration.Days <= 0
                        ? duration.DisplayName
                        : $"{duration.DisplayName} ({duration.Days} روز)")
                    .ToList() ?? new List<string>();

                if (durations.Count > 0)
                    builder.AppendLine($"مدت‌ها: <code>{Html(string.Join(" / ", durations))}</code>");
            }
        }

        builder.AppendLine();
        builder.AppendLine("برای خرید از «💳خرید اکانت جدید» و برای افزایش موجودی از «💰شارژ حساب کاربری» استفاده کنید.");
        return builder.ToString();
    }

    public bool CanAfford(CredUser user, XuiV3PurchaseSelection selection)
    {
        var resolved = ResolvePurchase(selection, user.IsColleague);
        return user.AccountBalance >= resolved.PriceToman * NormalizeAccountCount(selection.AccountCount);
    }

    private static string Html(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }

    public async Task<XuiV3AccountCreationResult> CreateAccountAsync(
        CredUser user,
        ServerInfo serverInfo,
        XuiV3PurchaseSelection selection,
        string selectedCountry,
        CancellationToken cancellationToken = default,
        XuiV3AccountMetadataOptions metadataOptions = null)
    {
        metadataOptions ??= new XuiV3AccountMetadataOptions();
        metadataOptions.AccountCounter = await ResolveAccountCounterAsync(user, metadataOptions);
        var resolved = ResolvePurchase(selection, user.IsColleague);
        var inboundIds = ResolveInboundIds(resolved.Service);
        var trafficBytes = metadataOptions.TrafficBytes > 0 ? metadataOptions.TrafficBytes : resolved.TrafficBytes;
        var priceToman = metadataOptions.PriceTomanOverride ?? resolved.PriceToman;
        Console.WriteLine(
            $"[XUIv3] create account target panel url={serverInfo?.Url}, rootPath={serverInfo?.RootPath}, panelTag={selectedCountry}, service={resolved.Service.Key}, inboundIds=[{string.Join(",", inboundIds)}]");

        var accountDto = new AccountDto
        {
            TelegramUserId = user.TelegramUserId,
            SelectedCountry = selectedCountry,
            SelectedPeriod = resolved.DurationDays <= 0 ? "Unlimited" : $"{resolved.DurationDays} Days",
            TotoalGB = resolved.TrafficGb.ToString(),
            ServerInfo = serverInfo,
            AccType = resolved.Service.Key,
            IsColleague = user.IsColleague,
            AccountCounter = metadataOptions.AccountCounter
        };

        return await ApiServicev3.CreateUserAccountAsync(
            accountDto,
            _configuration,
            new XuiV3CreateAccountOptions
            {
                InboundIds = inboundIds,
                TrafficGb = resolved.TrafficGb,
                TrafficBytes = trafficBytes,
                DurationDays = resolved.DurationDays,
                LimitIp = resolved.LimitIp,
                StartExpiryAfterFirstUse = resolved.IsUnlimited,
                Comment = BuildClientComment(user, resolved, inboundIds, serverInfo, metadataOptions, trafficBytes, priceToman),
                SaveUserStatus = metadataOptions.SaveUserStatus
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates one or more XUI v3 accounts and returns a partial-success result instead of throwing panel failures.
    /// </summary>
    /// <param name="user">
    /// Credentials profile of the Telegram user who owns the created accounts. The Telegram id is stored in panel
    /// metadata and, when <see cref="XuiV3BulkCreateOptions.SaveUserStatus"/> is enabled, in users.db state rows.
    /// </param>
    /// <param name="serverInfo">Configured XUI v3 panel endpoint, credentials, root path, and optional API token.</param>
    /// <param name="selection">Resolved purchase selection requested by the user or admin flow.</param>
    /// <param name="selectedCountry">Panel tag or URL stored in the legacy user state for display and audit.</param>
    /// <param name="options">Optional bulk metadata, account count, override price, and audit settings.</param>
    /// <param name="cancellationToken">Cancellation token for panel calls, users.db writes, and inter-account delay.</param>
    /// <returns>
    /// A bulk creation result containing every successfully created account and the first failure that stopped the
    /// loop. Panel HTTP timeouts and API exceptions are converted to <see cref="XuiV3BulkCreationFailure"/> so owned,
    /// tenant, and super-admin flows can show a clean error without crashing the Telegram receiver.
    /// </returns>
    /// <remarks>
    /// The method preserves partial success. If account 1 is created and account 2 times out, callers must charge or
    /// deliver only the successful accounts and show the failure list for the rest. Shutdown cancellation is not
    /// swallowed and still propagates through <see cref="OperationCanceledException"/>. Full panel exceptions are
    /// retained only in the private daily diagnostic log; every failure returned to callers contains fixed,
    /// Telegram-safe text and must not be replaced with <see cref="Exception.Message"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await purchaseService.CreateBulkAccountsAsync(
    ///     user,
    ///     serverInfo,
    ///     selection,
    ///     selectedCountry: "default",
    ///     cancellationToken: cancellationToken);
    ///
    /// if (result.SuccessfulCount == 0)
    ///     await bot.SendTextMessageAsync(chatId, result.Failures[0].Message, cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public async Task<XuiV3BulkCreationResult> CreateBulkAccountsAsync(
        CredUser user,
        ServerInfo serverInfo,
        XuiV3PurchaseSelection selection,
        string selectedCountry,
        XuiV3BulkCreateOptions options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new XuiV3BulkCreateOptions();
        var resolved = ResolvePurchase(selection, user.IsColleague);
        var accountCount = NormalizeAccountCount(options.AccountCount > 0 ? options.AccountCount : selection.AccountCount);
        var bulkOrderId = string.IsNullOrWhiteSpace(options.BulkOrderId)
            ? $"x3-{user.TelegramUserId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : options.BulkOrderId;

        var result = new XuiV3BulkCreationResult
        {
            BulkOrderId = bulkOrderId,
            RequestedCount = accountCount,
            UnitPriceToman = options.PriceTomanOverride ?? resolved.PriceToman,
            TotalRequestedPriceToman = (options.PriceTomanOverride ?? resolved.PriceToman) * accountCount,
            ServiceKey = resolved.Service.Key,
            ServiceName = resolved.Service.DisplayName,
            IsUnlimited = resolved.IsUnlimited,
            TrafficGb = resolved.TrafficGb,
            TrafficBytes = options.TrafficBytes > 0 ? options.TrafficBytes : resolved.TrafficBytes,
            DurationDays = resolved.DurationDays
        };

        for (var i = 1; i <= accountCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var createOptions = new XuiV3AccountMetadataOptions
            {
                UserComment = options.UserComment,
                BulkOrderId = bulkOrderId,
                BulkIndex = i,
                BulkTotal = accountCount,
                IsTrial = options.IsTrial,
                TrialKey = options.TrialKey,
                TrafficBytes = options.TrafficBytes,
                PriceTomanOverride = options.PriceTomanOverride,
                CreatedByTelegramUserId = options.CreatedByTelegramUserId,
                LastUpdatedByTelegramUserId = options.LastUpdatedByTelegramUserId,
                LastAction = options.LastAction,
                AccountCounter = options.NextAccountCounter > 0 ? options.NextAccountCounter + i - 1 : 0,
                SaveUserStatus = options.SaveUserStatus
            };

            XuiV3AccountCreationResult created;
            try
            {
                created = await CreateAccountAsync(
                    user,
                    serverInfo,
                    selection,
                    selectedCountry,
                    cancellationToken,
                    createOptions);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Preserve the full exception only in internal diagnostics. Telegram receives a fixed safe message.
                DailyErrorFileLoggerProvider.WriteExternalDiagnostic(
                    _configuration,
                    LogLevel.Error,
                    nameof(XuiV3PurchaseService),
                    $"XUI v3 bulk account creation failed. bulkOrderId={bulkOrderId}, index={i}",
                    ex);
                result.Failures.Add(new XuiV3BulkCreationFailure
                {
                    Index = i,
                    Message = XuiV3UserSafeError.ForAccountCreation(ex)
                });
                break;
            }

            if (!created.Success)
            {
                result.Failures.Add(new XuiV3BulkCreationFailure
                {
                    Index = i,
                    Email = created.Email,
                    Message = XuiV3UserSafeError.ForAccountCreation(created.Message)
                });
                break;
            }

            result.CreatedAccounts.Add(created);

            if (i < accountCount && options.DelayBetweenCreatesMs > 0)
                await Task.Delay(options.DelayBetweenCreatesMs, cancellationToken);
        }

        result.SuccessfulCount = result.CreatedAccounts.Count;
        result.TotalSuccessfulPriceToman = result.UnitPriceToman * result.SuccessfulCount;
        return result;
    }

    public async Task<XuiV3AccountCreationResult> CreateTrialAccountAsync(
        CredUser user,
        ServerInfo serverInfo,
        string serviceKey,
        int displayTrafficGb,
        long trafficBytes,
        int durationDays,
        string trialKey,
        CancellationToken cancellationToken = default)
    {
        var service = FindService(serviceKey);
        var inboundIds = ResolveInboundIds(service);
        var resolved = new XuiV3ResolvedPurchase
        {
            Service = service,
            Duration = new XuiV3DurationOption
            {
                Key = $"trial-{durationDays}d",
                DisplayName = $"تست {durationDays} روزه",
                Days = durationDays
            },
            TrafficGb = displayTrafficGb,
            TrafficBytes = trafficBytes,
            DurationDays = durationDays,
            LimitIp = 0,
            PriceToman = 0,
            IsUnlimited = false
        };

        Console.WriteLine(
            $"[XUIv3] create trial account target panel url={serverInfo?.Url}, rootPath={serverInfo?.RootPath}, service={service.Key}, trialKey={trialKey}, trafficBytes={trafficBytes}, inboundIds=[{string.Join(",", inboundIds)}]");

        var accountDto = new AccountDto
        {
            TelegramUserId = user.TelegramUserId,
            SelectedCountry = serverInfo?.Url,
            SelectedPeriod = $"{durationDays} Days",
            TotoalGB = displayTrafficGb.ToString(),
            ServerInfo = serverInfo,
            AccType = service.Key,
            IsColleague = user.IsColleague
        };

        return await ApiServicev3.CreateUserAccountAsync(
            accountDto,
            _configuration,
            new XuiV3CreateAccountOptions
            {
                InboundIds = inboundIds,
                TrafficGb = displayTrafficGb,
                TrafficBytes = trafficBytes,
                DurationDays = durationDays,
                LimitIp = 0,
                Comment = BuildClientComment(
                    user,
                    resolved,
                    inboundIds,
                    serverInfo,
                    new XuiV3AccountMetadataOptions
                    {
                        IsTrial = true,
                        TrialKey = trialKey,
                        TrafficBytes = trafficBytes,
                        PriceTomanOverride = 0,
                        CreatedByTelegramUserId = user.TelegramUserId,
                        LastUpdatedByTelegramUserId = user.TelegramUserId,
                        LastAction = "trial-create",
                        SaveUserStatus = true
                    },
                    trafficBytes,
                    0),
                SaveUserStatus = true
            },
            cancellationToken);
    }

    private static async Task<int> ResolveAccountCounterAsync(CredUser user, XuiV3AccountMetadataOptions metadataOptions)
    {
        if (metadataOptions?.AccountCounter > 0)
            return metadataOptions.AccountCounter;

        if (metadataOptions == null ||
            metadataOptions.IsTrial ||
            !metadataOptions.SaveUserStatus ||
            user == null ||
            user.TelegramUserId <= 0)
        {
            return 0;
        }

        var userDbContext = new UserDbContext();
        var flowUser = await userDbContext.GetUserStatus(user.TelegramUserId);
        return flowUser.AccountCounter + 1;
    }

    /// <summary>
    /// Builds the HTML-formatted Telegram delivery text for one XUI v3 account result.
    /// </summary>
    /// <param name="result">
    /// Account creation result returned by <see cref="ApiServicev3.CreateUserAccountAsync"/>. A null or unsuccessful
    /// result is accepted and produces a fixed safe failure message; raw panel responses must not be assigned to it.
    /// </param>
    /// <returns>
    /// HTML-formatted account details when creation succeeded, or a Persian failure message that contains no panel
    /// URL, root path, endpoint, response body, token, cookie, or request payload.
    /// </returns>
    /// <remarks>
    /// The successful output is intended for Telegram with HTML parse mode. Failure output is passed through
    /// <see cref="XuiV3UserSafeError"/> again as a boundary safeguard, even though creation results are expected to
    /// have been sanitized earlier.
    /// </remarks>
    /// <example>
    /// <code>
    /// var text = purchaseService.BuildCreatedAccountText(createdAccount);
    /// await bot.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public string BuildCreatedAccountText(XuiV3AccountCreationResult result)
    {
        if (result == null || !result.Success)
            return $"ساخت اکانت ناموفق بود.\n{XuiV3UserSafeError.ForAccountCreation(result?.Message)}";

        var trafficLabel = IsUnlimitedAccount(result.Comment) ? "حد مصرف منصفانه" : "حجم";
        var text = "✅ اکانت شما با موفقیت ساخته شد.\n\n";
        text += $"👤 نام اکانت: <code>{System.Net.WebUtility.HtmlEncode(result.Email)}</code>\n";
        text += $"📦 {trafficLabel}: <b>{System.Net.WebUtility.HtmlEncode(FormatTrafficSize(result.TrafficBytes, result.TrafficGb))}</b>\n";
        text += $"📅 تاریخ انقضا: <b>{System.Net.WebUtility.HtmlEncode(FormatExpiry(result.ExpiryTime))}</b>\n\n";

        if (!string.IsNullOrWhiteSpace(result.SubLink))
        {
            text += "🔗 سابلینک:\n";
            text += $"<code>{System.Net.WebUtility.HtmlEncode(result.SubLink)}</code>\n\n";
            text += "📌 برای اتصال سریع، QR Code همین پیام را اسکن کنید.";
        }
        else
        {
            text += "⚠️ سابلینک ساخته نشد. مقدار xuiV3SubLinkBaseUrl یا مسیر subscription پنل را بررسی کنید.";
        }

        return text;
    }

    private static string FormatExpiry(long expiryTime)
    {
        if (expiryTime < 0)
            return $"{Math.Max(1, (int)Math.Ceiling(Math.Abs(expiryTime) / (double)TimeSpan.FromDays(1).TotalMilliseconds))} روز بعد از اولین اتصال";

        if (expiryTime == 0)
            return "نامحدود";

        return DateTimeOffset
            .FromUnixTimeMilliseconds(expiryTime)
            .UtcDateTime
            .AddMinutes(210)
            .ConvertToHijriShamsi();
    }

    public static int NormalizeAccountCount(int accountCount)
    {
        if (accountCount <= 0)
            return 1;

        return Math.Min(accountCount, MaxBulkAccountCount);
    }

    public static string FormatTrafficSize(long trafficBytes, int fallbackTrafficGb = 0)
    {
        if (trafficBytes <= 0 && fallbackTrafficGb > 0)
            trafficBytes = ApiService.ConvertGBToBytes(fallbackTrafficGb);

        if (trafficBytes <= 0)
            return "نامشخص";

        const decimal gb = 1024m * 1024m * 1024m;
        const decimal mb = 1024m * 1024m;
        if (trafficBytes >= (long)gb)
            return $"{trafficBytes / gb:0.##} GB";

        return $"{trafficBytes / mb:0.##} MB";
    }

    private static bool IsUnlimitedAccount(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return false;

        try
        {
            var metadata = JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
            return string.Equals(metadata?.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private XuiV3ServiceDefinition FindService(string serviceKey)
    {
        var service = GetEnabledServices().FirstOrDefault(s =>
            string.Equals(s.Key, serviceKey, StringComparison.OrdinalIgnoreCase));

        if (service == null)
            throw new InvalidOperationException($"Service '{serviceKey}' was not found or is disabled.");

        return service;
    }

    private static List<int> ResolveInboundIds(XuiV3ServiceDefinition service)
    {
        return service?.InboundIds?.Distinct().ToList() ?? new List<int>();
    }

    private static string BuildClientComment(
        CredUser user,
        XuiV3ResolvedPurchase resolved,
        List<int> inboundIds,
        ServerInfo serverInfo,
        XuiV3AccountMetadataOptions metadataOptions,
        long trafficBytes,
        long priceToman)
    {
        var metadata = new XuiV3ClientMetadata
        {
            TelegramUserId = user.TelegramUserId,
            UserRole = user.IsColleague ? "colleague" : "customer",
            ServiceKey = resolved.Service.Key,
            ServiceName = resolved.Service.DisplayName,
            ServiceKind = resolved.Service.Kind,
            PlanKey = resolved.IsUnlimited ? resolved.UnlimitedPlan?.Key : resolved.Duration?.Key,
            PlanName = resolved.IsUnlimited ? resolved.UnlimitedPlan?.DisplayName : resolved.Duration?.DisplayName,
            TrafficGb = resolved.TrafficGb,
            TrafficBytes = trafficBytes,
            DurationDays = resolved.DurationDays,
            LimitIp = resolved.LimitIp,
            PriceToman = priceToman,
            UserComment = metadataOptions.UserComment,
            BulkOrderId = metadataOptions.BulkOrderId,
            BulkIndex = metadataOptions.BulkIndex,
            BulkTotal = metadataOptions.BulkTotal,
            IsTrial = metadataOptions.IsTrial,
            TrialKey = metadataOptions.TrialKey,
            AccountCounter = metadataOptions.AccountCounter,
            InboundIds = inboundIds ?? new List<int>(),
            MultiInbound = resolved.Service.MultiInbound,
            PanelUrl = serverInfo?.Url,
            CreatedByBotId = string.IsNullOrWhiteSpace(metadataOptions.CreatedByBotId) ? BotContextAccessor.CurrentBotId : metadataOptions.CreatedByBotId,
            LastUpdatedByBotId = string.IsNullOrWhiteSpace(metadataOptions.LastUpdatedByBotId) ? BotContextAccessor.CurrentBotId : metadataOptions.LastUpdatedByBotId,
            CreatedByTelegramUserId = metadataOptions.CreatedByTelegramUserId ?? user.TelegramUserId,
            LastUpdatedByTelegramUserId = metadataOptions.LastUpdatedByTelegramUserId ?? user.TelegramUserId,
            LastAction = string.IsNullOrWhiteSpace(metadataOptions.LastAction) ? "customer-create" : metadataOptions.LastAction
        };

        return JsonConvert.SerializeObject(metadata, Formatting.None);
    }

    /// <summary>
    /// Formats a validated lifetime multiplier for user-visible tariff text without locale-dependent separators.
    /// </summary>
    /// <param name="multiplier">Positive finite multiplier loaded from the global service catalog.</param>
    /// <returns>A culture-invariant value with up to four fractional digits, suitable for HTML encoding.</returns>
    /// <remarks>This helper performs presentation only and must not be used for financial arithmetic.</remarks>
    /// <example>
    /// <code>
    /// var label = XuiV3PurchaseService.FormatLifetimeMultiplier(1.2D);
    /// </code>
    /// </example>
    public static string FormatLifetimeMultiplier(double multiplier)
    {
        return multiplier.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the canonical persisted key for one validated custom duration.
    /// </summary>
    /// <param name="days">Requested duration in whole days. Callers validate the configured range separately.</param>
    /// <returns>A culture-invariant key in the form <c>days-N</c>.</returns>
    /// <remarks>The key contains no colon and is safe inside existing owned and tenant callback payloads.</remarks>
    private static string BuildCustomDurationKey(int days)
    {
        return CustomDurationKeyPrefix + days.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a canonical custom-duration key without consulting the current service policy.
    /// </summary>
    /// <param name="durationKey">Persisted or callback key expected in the exact <c>days-N</c> format.</param>
    /// <param name="days">Parsed whole-day value when successful; otherwise zero.</param>
    /// <returns>
    /// <c>true</c> only for canonical values from one through <see cref="MaxCustomDurationDays" />; otherwise
    /// <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Policy enablement and service-specific minimum/maximum checks remain the responsibility of
    /// <see cref="TryResolveDurationKey" />.
    /// </remarks>
    private static bool TryParseCanonicalCustomDurationKey(string durationKey, out int days)
    {
        days = 0;
        var key = durationKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) ||
            !key.StartsWith(CustomDurationKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dayText = key.Substring(CustomDurationKeyPrefix.Length);
        if (!int.TryParse(
                dayText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out days) ||
            days < 1 ||
            days > MaxCustomDurationDays)
        {
            days = 0;
            return false;
        }

        if (!string.Equals(key, BuildCustomDurationKey(days), StringComparison.OrdinalIgnoreCase))
        {
            days = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts Latin, Persian, and Arabic-Indic decimal digits to ASCII while preserving every non-digit character.
    /// </summary>
    /// <param name="value">Raw Telegram text. Null and empty values return an empty string.</param>
    /// <returns>A same-shape string whose whole decimal digits use <c>0</c> through <c>9</c>.</returns>
    /// <remarks>
    /// Preserving signs, decimal separators, units, and letters lets the strict caller reject them instead of silently
    /// extracting a different positive number.
    /// </remarks>
    private static string NormalizeUnicodeDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            var normalizedCharacter = character switch
            {
                >= '\u06F0' and <= '\u06F9' => (char)('0' + character - '\u06F0'),
                >= '\u0660' and <= '\u0669' => (char)('0' + character - '\u0660'),
                _ => character
            };
            builder.Append(normalizedCharacter);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Validates metered duration-pricing fields before any catalog consumer can display or charge them.
    /// </summary>
    /// <param name="catalog">
    /// Detached global XUI v3 catalog loaded from JSON. Null collections are tolerated for legacy compatibility.
    /// </param>
    /// <remarks>
    /// Validation is intentionally global rather than role-specific: a negative colleague price must be rejected
    /// even when the current caller is a normal customer, because tenant base-cost calculation may use it later.
    /// This method has no persistence or external-service side effects.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a metered daily role price is negative, a lifetime multiplier is not positive and finite, a
    /// configured duration is invalid, or a custom-duration policy violates service or range constraints.
    /// </exception>
    private static void ValidateMeteredPricingConfiguration(XuiV3ServicePlanCatalog catalog)
    {
        foreach (var service in catalog?.Services ?? new List<XuiV3ServiceDefinition>())
        {
            if (service == null)
                continue;

            var customDuration = service.CustomDurationDays ?? new XuiV3CustomDurationDaysOptions();
            if (customDuration.MinimumDays < 1)
                throw new InvalidOperationException($"Service '{service.Key}' custom duration minimum must be at least one day.");
            if (customDuration.MaximumDays < customDuration.MinimumDays)
                throw new InvalidOperationException($"Service '{service.Key}' custom duration maximum cannot be less than its minimum.");
            if (customDuration.MaximumDays > MaxCustomDurationDays)
                throw new InvalidOperationException($"Service '{service.Key}' custom duration maximum cannot exceed {MaxCustomDurationDays} days.");
            if (customDuration.IsEnabled &&
                (!string.Equals(service.Kind, XuiV3ServiceKinds.Metered, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(service.Key, NormalServiceKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Custom duration input can be enabled only for the metered 'normal' service.");
            }

            if (service.IsUnlimited)
                continue;

            if ((service.PricePerDay?.User ?? 0L) < 0 || (service.PricePerDay?.Colleague ?? 0L) < 0)
                throw new InvalidOperationException($"Service '{service.Key}' cannot have a negative daily price.");
            if (!double.IsFinite(service.LifetimePriceMultiplier) || service.LifetimePriceMultiplier <= 0D)
                throw new InvalidOperationException($"Service '{service.Key}' must have a positive finite lifetime multiplier.");

            foreach (var duration in service.DurationOptions ?? new List<XuiV3DurationOption>())
            {
                if (duration == null)
                    throw new InvalidOperationException($"Service '{service.Key}' contains an empty duration entry.");
                if (duration.Days < 0)
                    throw new InvalidOperationException($"Duration '{duration.Key}' in service '{service.Key}' cannot have negative days.");
                if (duration.Key?.StartsWith(CustomDurationKeyPrefix, StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException($"Duration key '{duration.Key}' in service '{service.Key}' uses the reserved '{CustomDurationKeyPrefix}' prefix.");
            }
        }
    }

    private static string FormatInboundIds(IEnumerable<int> inboundIds)
    {
        return inboundIds == null ? "[]" : $"[{string.Join(",", inboundIds)}]";
    }
}

public class XuiV3AccountMetadataOptions
{
    public string UserComment { get; set; }
    public string BulkOrderId { get; set; }
    public int? BulkIndex { get; set; }
    public int? BulkTotal { get; set; }
    public bool IsTrial { get; set; }
    public string TrialKey { get; set; }
    public long TrafficBytes { get; set; }
    public long? PriceTomanOverride { get; set; }
    public long? CreatedByTelegramUserId { get; set; }
    public long? LastUpdatedByTelegramUserId { get; set; }
    public string CreatedByBotId { get; set; }
    public string LastUpdatedByBotId { get; set; }
    public string LastAction { get; set; }
    public int AccountCounter { get; set; }
    public bool SaveUserStatus { get; set; } = true;
}

public class XuiV3BulkCreateOptions
{
    public int AccountCount { get; set; } = 1;
    public string UserComment { get; set; }
    public string BulkOrderId { get; set; }
    public bool IsTrial { get; set; }
    public string TrialKey { get; set; }
    public long TrafficBytes { get; set; }
    public long? PriceTomanOverride { get; set; }
    public long? CreatedByTelegramUserId { get; set; }
    public long? LastUpdatedByTelegramUserId { get; set; }
    public string LastAction { get; set; }
    public int NextAccountCounter { get; set; }
    public bool SaveUserStatus { get; set; } = true;
    public int DelayBetweenCreatesMs { get; set; } = 350;
}

public class XuiV3BulkCreationResult
{
    public string BulkOrderId { get; set; }
    public int RequestedCount { get; set; }
    public int SuccessfulCount { get; set; }
    public long UnitPriceToman { get; set; }
    public long TotalRequestedPriceToman { get; set; }
    public long TotalSuccessfulPriceToman { get; set; }
    public string ServiceKey { get; set; }
    public string ServiceName { get; set; }
    public bool IsUnlimited { get; set; }
    public int TrafficGb { get; set; }
    public long TrafficBytes { get; set; }
    public int DurationDays { get; set; }
    public List<XuiV3AccountCreationResult> CreatedAccounts { get; set; } = new List<XuiV3AccountCreationResult>();
    public List<XuiV3BulkCreationFailure> Failures { get; set; } = new List<XuiV3BulkCreationFailure>();
    public bool Success => SuccessfulCount == RequestedCount && Failures.Count == 0;
}

/// <summary>
/// Describes one failed item in a bulk XUI v3 creation request without exposing panel infrastructure details.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is a Telegram-safe fixed message. The original exception is written separately to the
/// private daily diagnostic log and must not be copied into this DTO.
/// </remarks>
public class XuiV3BulkCreationFailure
{
    /// <summary>One-based item index inside the bulk account request.</summary>
    public int Index { get; set; }

    /// <summary>Generated client email when it was known before the failure; otherwise <c>null</c>.</summary>
    public string Email { get; set; }

    /// <summary>Fixed sanitized failure text that is safe to display through Telegram.</summary>
    public string Message { get; set; }
}

/// <summary>
/// Builds and parses compact Telegram callback payloads for XUI v3 purchase, renewal, and account-management actions.
/// </summary>
/// <remarks>
/// Callback values are transport identifiers, never authorization. Handlers must revalidate bot context, Telegram
/// ownership, current plan configuration, and persisted state before reading private panel data or applying a side
/// effect. Account configuration callbacks contain only a numeric client id and never SubId or protocol URLs.
/// </remarks>
public static class XuiV3PurchaseCallbacks
{
    private const string Prefix = "x3";

    public static string BackToServices()
    {
        return $"{Prefix}:back";
    }

    public static string Cancel()
    {
        return $"{Prefix}:cancel";
    }

    public static string Home()
    {
        return $"{Prefix}:home";
    }

    public static string Service(string serviceKey)
    {
        return $"{Prefix}:svc:{serviceKey}";
    }

    public static string Traffic(string serviceKey, int trafficGb)
    {
        return $"{Prefix}:gb:{serviceKey}:{trafficGb}";
    }

    public static string Duration(string serviceKey, int trafficGb, string durationKey)
    {
        return $"{Prefix}:dur:{serviceKey}:{trafficGb}:{durationKey}";
    }

    public static string UnlimitedPlan(string serviceKey, string planKey)
    {
        return $"{Prefix}:upl:{serviceKey}:{planKey}";
    }

    /// <summary>
    /// Builds the owned-bot confirmation callback for one purchase selection.
    /// </summary>
    /// <param name="selection">
    /// Selected service and either an unlimited-plan key or metered traffic/duration. Metered duration may be a
    /// configured key or a canonical custom key such as <c>days-3</c>.
    /// </param>
    /// <returns>Compact callback data that can be parsed by <see cref="TryParse" />.</returns>
    /// <remarks>
    /// The callback is only transport state. Confirmation handlers must call the central resolver again before wallet,
    /// ledger, XUI, or website-wallet side effects because configuration can change after this callback is sent.
    /// </remarks>
    /// <example>
    /// <code>
    /// var callback = XuiV3PurchaseCallbacks.Confirm(selection);
    /// </code>
    /// </example>
    public static string Confirm(XuiV3PurchaseSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.UnlimitedPlanKey))
            return $"{Prefix}:ok:{selection.ServiceKey}:u:{selection.UnlimitedPlanKey}";

        return $"{Prefix}:ok:{selection.ServiceKey}:{selection.TrafficGb}:{selection.DurationKey}";
    }

    /// <summary>
    /// Builds callback data for confirming an XUI v3 purchase with the Gozargah website wallet.
    /// </summary>
    /// <param name="selection">
    /// Selected service, traffic, enabled preset or <c>days-N</c> duration, or unlimited plan.
    /// </param>
    /// <returns>
    /// Callback data that carries the same purchase selection as <see cref="Confirm"/> while marking the
    /// payment source as the Gozargah website wallet.
    /// </returns>
    public static string SiteWalletConfirm(XuiV3PurchaseSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.UnlimitedPlanKey))
            return $"{Prefix}:sitepay:{selection.ServiceKey}:u:{selection.UnlimitedPlanKey}";

        return $"{Prefix}:sitepay:{selection.ServiceKey}:{selection.TrafficGb}:{selection.DurationKey}";
    }

    public static string AccountCount(int count)
    {
        return $"{Prefix}:cnt:{Math.Max(1, Math.Min(count, XuiV3PurchaseService.MaxBulkAccountCount))}";
    }

    /// <summary>
    /// Builds an owned-account enable or disable callback that preserves the originating list page.
    /// </summary>
    /// <param name="clientId">
    /// Stable numeric XUI client identifier from the authenticated panel client list. Must be positive.
    /// </param>
    /// <param name="enable"><c>true</c> to enable the account; <c>false</c> to disable it.</param>
    /// <param name="page">
    /// Zero-based page of the owned-account list that should be restored after the panel operation.
    /// Negative values are normalized to zero.
    /// </param>
    /// <returns>Compact callback data containing only the operation, numeric client id, and UI page.</returns>
    /// <remarks>
    /// Older Telegram messages may omit the page segment. <see cref="TryParse"/> continues to accept that shape,
    /// and the dispatcher restores page zero. The callback does not prove ownership; handlers must reload the client
    /// and verify its Telegram owner before changing panel state.
    /// </remarks>
    /// <example>
    /// <code>
    /// var callback = XuiV3PurchaseCallbacks.AccountState(client.Id, enable: false, page: 2);
    /// </code>
    /// </example>
    public static string AccountState(int clientId, bool enable, int page = 0)
    {
        return $"{Prefix}:acct:{(enable ? "en" : "dis")}:{clientId}:{Math.Max(0, page)}";
    }

    /// <summary>
    /// Builds the read-only callback used to retrieve protocol configuration URLs for one owned account.
    /// </summary>
    /// <param name="clientId">
    /// Stable numeric XUI client identifier from the panel client list. Must be positive.
    /// </param>
    /// <returns>A short callback that contains the numeric client id and never contains SubId or proxy URLs.</returns>
    /// <remarks>
    /// The receiving handler must reload the client and enforce Telegram ownership before requesting any links from
    /// the panel. This callback intentionally carries no panel credential, subscription id, email, or configuration.
    /// </remarks>
    /// <example>
    /// <code>
    /// var callback = XuiV3PurchaseCallbacks.AccountConfigs(client.Id);
    /// </code>
    /// </example>
    public static string AccountConfigs(int clientId)
    {
        return $"{Prefix}:acfg:{clientId}";
    }

    public static string AccountList(int page)
    {
        return $"{Prefix}:alist:{Math.Max(0, page)}";
    }

    public static string AccountSearchStart()
    {
        return $"{Prefix}:asrch";
    }

    public static string AccountSearchList(int page)
    {
        return $"{Prefix}:asl:{Math.Max(0, page)}";
    }

    public static string AccountView(int clientId, int page)
    {
        return $"{Prefix}:aview:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchView(int clientId, int page)
    {
        return $"{Prefix}:asv:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountRenew(int clientId, int page)
    {
        return $"{Prefix}:aren:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchRenew(int clientId, int page)
    {
        return $"{Prefix}:asren:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountUuidRenew(int clientId)
    {
        return $"{Prefix}:auren:{clientId}";
    }

    public static string AccountDeleteAsk(int clientId, int page)
    {
        return $"{Prefix}:adelask:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchDeleteAsk(int clientId, int page)
    {
        return $"{Prefix}:asdelask:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountDeleteConfirm(int clientId, int page)
    {
        return $"{Prefix}:adel:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchDeleteConfirm(int clientId, int page)
    {
        return $"{Prefix}:asdel:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchState(int clientId, bool enable, int page)
    {
        return $"{Prefix}:asacct:{(enable ? "en" : "dis")}:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountChangeLink(int clientId, int page)
    {
        return $"{Prefix}:ach:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchChangeLink(int clientId, int page)
    {
        return $"{Prefix}:asch:{clientId}:{Math.Max(0, page)}";
    }

    /// <summary>
    /// Builds callback data for the explicit final confirmation of one persisted link-change operation.
    /// </summary>
    /// <param name="operationKey">Random users.db operation key; it must not contain panel credentials.</param>
    /// <returns>Compact callback data that resumes exactly the saved operation.</returns>
    public static string AccountChangeLinkConfirm(string operationKey)
    {
        return $"{Prefix}:chok:{operationKey}";
    }

    /// <summary>
    /// Builds callback data that cancels a persisted link change before any panel mutation.
    /// </summary>
    /// <param name="operationKey">Random users.db operation key shown in the same bot confirmation message.</param>
    /// <returns>Compact callback data for the pre-mutation cancellation transition.</returns>
    public static string AccountChangeLinkCancel(string operationKey)
    {
        return $"{Prefix}:chcancel:{operationKey}";
    }

    /// <summary>
    /// Builds callback data that displays or requeues the same persisted link-change operation.
    /// </summary>
    /// <param name="operationKey">Random users.db operation key whose status should be refreshed.</param>
    /// <returns>Compact callback data that never generates a replacement identity.</returns>
    public static string AccountChangeLinkStatus(string operationKey)
    {
        return $"{Prefix}:chstatus:{operationKey}";
    }

    public static string AccountComment(int clientId, int page)
    {
        return $"{Prefix}:acom:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchComment(int clientId, int page)
    {
        return $"{Prefix}:ascom:{clientId}:{Math.Max(0, page)}";
    }

    /// <summary>
    /// Parses compact XUI callback data into a typed routing object without executing any business operation.
    /// </summary>
    /// <param name="callbackData">Telegram callback data beginning with the XUI prefix.</param>
    /// <param name="callback">Parsed routing values, or <c>null</c> when the prefix/shape is invalid.</param>
    /// <returns><c>true</c> when the callback belongs to this router; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Link-change confirmation/status actions expose only a random users.db operation key. They never carry panel
    /// URLs, UUIDs, credentials, or a newly generated identity and therefore cannot independently replay a mutation.
    /// Purchase callbacks may carry a <c>days-N</c> duration key, but parsing does not establish eligibility; callers
    /// must resolve it against the current custom-duration configuration before any financial or XUI operation.
    /// Account-configuration callbacks carry only the numeric client id. Parsing does not establish Telegram ownership,
    /// and legacy <c>acct</c> state callbacks without a page segment remain valid with a null page restored as zero.
    /// </remarks>
    public static bool TryParse(string callbackData, out XuiV3PurchaseCallback callback)
    {
        callback = null;

        if (string.IsNullOrWhiteSpace(callbackData))
            return false;

        var parts = callbackData.Split(':');
        if (parts.Length < 2 || parts[0] != Prefix)
            return false;

        callback = new XuiV3PurchaseCallback
        {
            Action = parts[1],
            ServiceKey = parts.Length > 2 ? parts[2] : null
        };

        if (callback.Action == "acct" && parts.Length >= 4)
        {
            callback.AccountOperation = parts[2];
            if (int.TryParse(parts[3], out var clientId))
                callback.ClientId = clientId;
            if (parts.Length >= 5 && int.TryParse(parts[4], out var page))
                callback.Page = page;
        }

        if (callback.Action == "acfg" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
        }

        if (callback.Action == "alist" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var page))
                callback.Page = page;
        }

        if (callback.Action == "asl" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var page))
                callback.Page = page;
        }

        if ((callback.Action == "aview" ||
             callback.Action == "aren" ||
             callback.Action == "adelask" ||
             callback.Action == "adel" ||
             callback.Action == "ach" ||
             callback.Action == "acom") &&
            parts.Length >= 4)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
            if (int.TryParse(parts[3], out var page))
                callback.Page = page;
        }

        if ((callback.Action == "asv" ||
             callback.Action == "asren" ||
             callback.Action == "asdelask" ||
             callback.Action == "asdel" ||
             callback.Action == "asch" ||
             callback.Action == "ascom") &&
            parts.Length >= 4)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
            if (int.TryParse(parts[3], out var page))
                callback.Page = page;
        }

        if ((callback.Action == "chok" ||
             callback.Action == "chcancel" ||
             callback.Action == "chstatus") &&
            parts.Length >= 3)
        {
            callback.OperationKey = parts[2];
        }

        if (callback.Action == "auren" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
        }

        if (callback.Action == "asacct" && parts.Length >= 5)
        {
            callback.AccountOperation = parts[2];
            if (int.TryParse(parts[3], out var clientId))
                callback.ClientId = clientId;
            if (int.TryParse(parts[4], out var page))
                callback.Page = page;
        }

        if (callback.Action == "gb" && parts.Length >= 4 && int.TryParse(parts[3], out var trafficGb))
            callback.TrafficGb = trafficGb;

        if (callback.Action == "cnt" && parts.Length >= 3 && int.TryParse(parts[2], out var accountCount))
            callback.AccountCount = accountCount;

        if (callback.Action == "dur" && parts.Length >= 5)
        {
            if (int.TryParse(parts[3], out var durationTrafficGb))
                callback.TrafficGb = durationTrafficGb;
            callback.DurationKey = parts[4];
        }

        if (callback.Action == "upl" && parts.Length >= 4)
            callback.UnlimitedPlanKey = parts[3];

        if ((callback.Action == "ok" || callback.Action == "sitepay") && parts.Length >= 5)
        {
            if (parts[3] == "u")
            {
                callback.UnlimitedPlanKey = parts[4];
            }
            else
            {
                if (int.TryParse(parts[3], out var confirmTrafficGb))
                    callback.TrafficGb = confirmTrafficGb;
                callback.DurationKey = parts[4];
            }
        }

        return true;
    }
}

/// <summary>
/// Parsed routing values for XUI purchase and account-management Telegram callbacks.
/// </summary>
/// <remarks>
/// This DTO contains identifiers only. Callers must still enforce bot context, Telegram ownership, state transitions,
/// and durable operation claims before calling XUI or changing financial state.
/// </remarks>
public class XuiV3PurchaseCallback
{
    /// <summary>Compact action name selected by the callback router.</summary>
    public string Action { get; set; }
    /// <summary>Configured XUI service-plan key, when the action belongs to purchase or renewal.</summary>
    public string ServiceKey { get; set; }
    /// <summary>Enable/disable account sub-action when applicable.</summary>
    public string AccountOperation { get; set; }
    /// <summary>Stable numeric XUI client id selected by list/search actions.</summary>
    public int? ClientId { get; set; }
    /// <summary>
    /// Zero-based UI page restored after an account action, or <c>null</c> for legacy state callbacks that default to
    /// page zero in the dispatcher.
    /// </summary>
    public int? Page { get; set; }
    /// <summary>Selected traffic in GB for metered purchase/renewal actions.</summary>
    public int? TrafficGb { get; set; }
    /// <summary>Requested account count for multi-account purchases.</summary>
    public int? AccountCount { get; set; }
    /// <summary>Configured duration option key or canonical custom key such as <c>days-3</c>.</summary>
    public string DurationKey { get; set; }
    /// <summary>Configured unlimited-plan key.</summary>
    public string UnlimitedPlanKey { get; set; }
    /// <summary>Random persisted link-change operation key carried by confirmation and recovery callbacks.</summary>
    public string OperationKey { get; set; }

    /// <summary>Creates the purchase selection represented by this callback.</summary>
    /// <returns>A detached selection with account count normalized to one.</returns>
    public XuiV3PurchaseSelection ToSelection()
    {
        return new XuiV3PurchaseSelection
        {
            ServiceKey = ServiceKey,
            TrafficGb = TrafficGb,
            DurationKey = DurationKey,
            UnlimitedPlanKey = UnlimitedPlanKey,
            AccountCount = 1
        };
    }
}
