using Adminbot.Domain;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Shared customer-facing wording for the XUI service families in owned and tenant bots.</summary>
/// <remarks>These labels are for presentation only. Catalog keys, custom display names, persisted metadata and payment logic remain independent.</remarks>
internal static class XuiCustomerServiceText
{
    /// <summary>Reply action for continuing a renewal in its verified current service family.</summary>
    internal const string RenewSame = "تمدید همین نوع سرویس";

    /// <summary>Offers same-family continuation and cancellation after presenting the verified service.</summary>
    /// <returns>Reply keyboard without any target identifiers or authorization data.</returns>
    internal static ReplyKeyboardMarkup SameServiceKeyboard() => new(new[]
    {
        new[] { new KeyboardButton(RenewSame) },
        new[] { new KeyboardButton("انصراف") }
    }) { ResizeKeyboard = true };

    /// <summary>Returns the customer label for an authoritative catalog service.</summary>
    /// <param name="service">Required enabled catalog service supplied by the current purchase or renewal flow.</param>
    /// <returns>A plain Persian service family label, independent of price, quota and duration.</returns>
    /// <example><code>var title = XuiCustomerServiceText.Label(resolved.Service);</code></example>
    internal static string Label(XuiV3ServiceDefinition service) => Label(ServiceSalesPolicy.GetCategory(service));

    /// <summary>Gets the presentation label for one commercial family.</summary>
    /// <param name="category">Closed family classification supplied by the commercial catalog.</param>
    /// <returns>The corresponding fixed Persian customer label.</returns>
    private static string Label(ServiceSalesCategory category) => category switch
    {
        ServiceSalesCategory.Unlimited => "بسته نامحدود با سقف مصرف",
        ServiceSalesCategory.National => "نت ملی",
        _ => "بسته حجمی عادی"
    };

    /// <summary>Explains the families actually present in a customer purchase keyboard.</summary>
    /// <param name="heading">Plain Persian prompt supplied by the active flow.</param>
    /// <param name="services">Catalog services already filtered by live availability and storefront pricing.</param>
    /// <returns>The prompt with one short explanation for each available family.</returns>
    /// <example><code>var text = XuiCustomerServiceText.ExplainChoices(prompt, availableServices);</code></example>
    internal static string ExplainChoices(string heading, IEnumerable<XuiV3ServiceDefinition> services) =>
        heading + "\n\n" + string.Join("\n", services.Select(ServiceSalesPolicy.GetCategory).Distinct().Select(category =>
            $"{Label(category)} — " + (category switch
            {
                ServiceSalesCategory.Unlimited => "بستهٔ آماده با حجم و مدت مشخص و سقف مصرف.",
                ServiceSalesCategory.National => "برای زمانی که دسترسی به اینترنت بین‌الملل به‌شدت محدود است.",
                _ => "حجم و مدت را خودتان انتخاب می‌کنید."
            })));

    /// <summary>Builds a purchase or renewal option with the family, package title, volume and current price.</summary>
    /// <param name="service">Current catalog service to which the ready package belongs.</param>
    /// <param name="plan">Eligible package with catalog title, days, and fair-usage gigabytes.</param>
    /// <param name="price">Already formatted authoritative price from the active owned role or tenant pricing policy.</param>
    /// <param name="includeReplyKey">True for a reply keyboard whose parser relies on the bracketed catalog key.</param>
    /// <returns>Plain Telegram option text. The duration is shown separately from the catalog title.</returns>
    /// <example><code>var caption = XuiCustomerServiceText.PackageOption(service, plan, price.FormatCurrency());</code></example>
    internal static string PackageOption(XuiV3ServiceDefinition service, XuiV3UnlimitedPlan plan, string price, bool includeReplyKey = false) =>
        $"{Label(service)} · {plan.DisplayName} · {plan.FairUsageGb} گیگ · {plan.Days} روز · {price}" +
        (includeReplyKey ? $" [{plan.Key}]" : string.Empty);

    /// <summary>Builds an unambiguous reply button for a compatible renewal service.</summary>
    /// <param name="service">Live catalog candidate compatible with the exact renewal target.</param>
    /// <returns>A display label followed by the bracketed stable service key.</returns>
    internal static string CategoryOption(XuiV3ServiceDefinition service) => $"{Label(service)} [{service.Key}]";

    /// <summary>Matches a renewal reply against the freshly authorized candidate set.</summary>
    /// <param name="text">Original customer reply, which may use an older catalog label.</param>
    /// <param name="candidates">Only services the caller has established are compatible and available.</param>
    /// <returns>The unique matched catalog service, or null for invalid or ambiguous input.</returns>
    /// <remarks>Exact matches only. Legacy catalog names and keyed reply buttons continue to work.</remarks>
    internal static XuiV3ServiceDefinition FindCategory(string text, IEnumerable<XuiV3ServiceDefinition> candidates)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var matches = candidates.Where(service =>
            string.Equals(normalized, CategoryOption(service), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, service.Key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, service.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Label(service), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, $"{service.DisplayName} [{service.Key}]", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(service => service.Key, StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
