using Newtonsoft.Json;

namespace Adminbot.Domain
{
    public class XuiV3ClientMetadata
    {
        public int Version { get; set; } = 1;
        public string Source { get; set; } = "telegram-bot";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public long TelegramUserId { get; set; }
        public string UserRole { get; set; }
        public string ServiceKey { get; set; }
        public string ServiceName { get; set; }
        public string ServiceKind { get; set; }
        public string PlanKey { get; set; }
        public string PlanName { get; set; }
        public int TrafficGb { get; set; }
        public long TrafficBytes { get; set; }
        public int DurationDays { get; set; }
        public int LimitIp { get; set; }
        public long PriceToman { get; set; }
        public string Currency { get; set; } = "toman";
        public string UserComment { get; set; }
        public string BulkOrderId { get; set; }
        public int? BulkIndex { get; set; }
        public int? BulkTotal { get; set; }
        public bool IsTrial { get; set; }
        public string TrialKey { get; set; }
        public int AccountCounter { get; set; }
        public List<int> InboundIds { get; set; } = new List<int>();
        public bool MultiInbound { get; set; }
        public string PanelUrl { get; set; }
        public string CreatedByBotId { get; set; }
        public string LastUpdatedByBotId { get; set; }
        public long? CreatedByTelegramUserId { get; set; }
        public long? LastUpdatedByTelegramUserId { get; set; }
        public string LastAction { get; set; }
        public DateTime? LastRenewedAtUtc { get; set; }
        public List<XuiV3ClientRenewalRecord> Renewals { get; set; } = new List<XuiV3ClientRenewalRecord>();
    }

    public class XuiV3ClientRenewalRecord
    {
        public DateTime RenewedAtUtc { get; set; } = DateTime.UtcNow;
        public long ActorTelegramUserId { get; set; }
        public int AddedTrafficGb { get; set; }
        public int AddedDurationDays { get; set; }
        public long TotalBytesAfter { get; set; }
        public long ExpiryTimeAfter { get; set; }
    }

    public class XuiV3ServicePlanCatalog
    {
        public int Version { get; set; } = 1;
        public string Currency { get; set; } = "toman";
        public List<XuiV3ServiceDefinition> Services { get; set; } = new List<XuiV3ServiceDefinition>();

        public static XuiV3ServicePlanCatalog Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                path = "./Data/xui-v3-service-plans.json";

            if (!File.Exists(path))
                throw new FileNotFoundException("XUI v3 service plan file was not found.", path);

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<XuiV3ServicePlanCatalog>(json) ?? new XuiV3ServicePlanCatalog();
        }
    }

    /// <summary>
    /// Describes one globally configured XUI v3 service and its purchasable pricing options.
    /// </summary>
    /// <remarks>
    /// Metered services use role-specific per-GB and per-day prices. A duration with zero days is treated as
    /// lifetime and uses <see cref="LifetimePriceMultiplier" /> instead of the per-day component. Unlimited
    /// fair-usage services continue to use their own fixed-price entries in <see cref="UnlimitedPlans" />.
    /// </remarks>
    public class XuiV3ServiceDefinition
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Kind { get; set; } = XuiV3ServiceKinds.Metered;
        public bool IsEnabled { get; set; } = true;
        public List<int> InboundIds { get; set; } = new List<int>();
        public bool MultiInbound { get; set; } = true;
        public List<string> InboundProfileKeys { get; set; } = new List<string>();
        public List<string> FallbackInboundTypes { get; set; } = new List<string>();
        public XuiV3RolePrice PricePerGb { get; set; } = new XuiV3RolePrice();
        /// <summary>
        /// Gets or sets the role-specific daily price in Iranian toman for finite metered durations.
        /// </summary>
        /// <remarks>
        /// Missing configuration defaults both roles to zero for backward compatibility. This component is not
        /// charged when the selected duration has zero days.
        /// </remarks>
        public XuiV3RolePrice PricePerDay { get; set; } = new XuiV3RolePrice();
        /// <summary>
        /// Gets or sets the multiplier applied to the traffic-derived price of a lifetime metered duration.
        /// </summary>
        /// <remarks>
        /// The JSON value is a floating-point number such as <c>1.2</c>, while financial arithmetic converts the
        /// validated value to <see cref="decimal" />. A missing value defaults to <c>1</c> and preserves legacy prices.
        /// </remarks>
        public double LifetimePriceMultiplier { get; set; } = 1D;
        public List<int> TrafficOptionsGb { get; set; } = new List<int>();
        /// <summary>
        /// Minimum traffic, in GB, that can be purchased or renewed for this metered service.
        /// </summary>
        /// <remarks>
        /// The value is read from <c>xui-v3-service-plans.json</c> and is intentionally ignored for
        /// unlimited services because their traffic is fixed by the selected fair-usage plan.
        /// </remarks>
        public int MinimumTrafficGb { get; set; } = 1;
        public List<XuiV3DurationOption> DurationOptions { get; set; } = new List<XuiV3DurationOption>();
        public List<XuiV3UnlimitedPlan> UnlimitedPlans { get; set; } = new List<XuiV3UnlimitedPlan>();

        public bool IsUnlimited => string.Equals(Kind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the configured per-GB price for a normal customer or colleague.
        /// </summary>
        /// <param name="isColleague">
        /// <c>true</c> for the colleague tariff; <c>false</c> for the normal-customer tariff.
        /// </param>
        /// <returns>The selected role's price for one GB in Iranian toman.</returns>
        /// <remarks>
        /// This method only selects the role rate. Callers must use the central purchase resolver to calculate
        /// a payable amount that also includes duration pricing and lifetime rules.
        /// </remarks>
        /// <example>
        /// <code>
        /// var customerRate = service.GetPricePerGb(isColleague: false);
        /// </code>
        /// </example>
        public long GetPricePerGb(bool isColleague)
        {
            return isColleague ? PricePerGb.Colleague : PricePerGb.User;
        }

        /// <summary>
        /// Gets the configured daily price for a normal customer or colleague.
        /// </summary>
        /// <param name="isColleague">
        /// <c>true</c> for the colleague tariff; <c>false</c> for the normal-customer tariff.
        /// </param>
        /// <returns>
        /// The selected role's daily price in Iranian toman, or zero when an older catalog omits
        /// <c>pricePerDay</c> or explicitly stores it as null.
        /// </returns>
        /// <remarks>
        /// The rate applies only to finite metered durations. Lifetime selections ignore it and use
        /// <see cref="LifetimePriceMultiplier" /> instead.
        /// </remarks>
        /// <example>
        /// <code>
        /// var dailyRate = service.GetPricePerDay(isColleague: true);
        /// </code>
        /// </example>
        public long GetPricePerDay(bool isColleague)
        {
            return PricePerDay?.GetForRole(isColleague) ?? 0L;
        }
    }

    public static class XuiV3ServiceKinds
    {
        public const string Metered = "metered";
        public const string Unlimited = "unlimited";
    }

    /// <summary>
    /// Stores one Iranian-toman price for normal customers and another for colleagues.
    /// </summary>
    public class XuiV3RolePrice
    {
        public long User { get; set; }
        public long Colleague { get; set; }

        public long GetForRole(bool isColleague)
        {
            return isColleague ? Colleague : User;
        }
    }

    /// <summary>
    /// Describes one selectable duration for a metered XUI v3 service.
    /// </summary>
    /// <remarks>
    /// Zero days represents lifetime access. Disabled options remain in configuration for operational toggling but
    /// must not be displayed or accepted by purchase, renewal, tenant, or super-admin account-creation flows.
    /// </remarks>
    public class XuiV3DurationOption
    {
        /// <summary>
        /// Gets or sets the stable callback and persisted-selection key, such as <c>life</c> or <c>m2</c>.
        /// </summary>
        public string Key { get; set; }
        /// <summary>
        /// Gets or sets the user-visible duration label shown in Telegram.
        /// </summary>
        public string DisplayName { get; set; }
        /// <summary>
        /// Gets or sets the duration in days; zero represents lifetime and negative values are invalid.
        /// </summary>
        public int Days { get; set; }
        /// <summary>
        /// Gets or sets whether this duration can be displayed and selected.
        /// </summary>
        /// <remarks>A missing JSON value defaults to <c>true</c> so existing catalogs remain compatible.</remarks>
        public bool IsEnabled { get; set; } = true;
    }

    public class XuiV3UnlimitedPlan
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public int Days { get; set; }
        public int FairUsageGb { get; set; }
        public int MaxUsers { get; set; } = 1;
        public XuiV3RolePrice Price { get; set; } = new XuiV3RolePrice();
        public bool IsEnabled { get; set; } = true;
    }

    public class XuiV3PurchaseSelection
    {
        public string ServiceKey { get; set; }
        public int? TrafficGb { get; set; }
        public string DurationKey { get; set; }
        public string UnlimitedPlanKey { get; set; }
        public int AccountCount { get; set; } = 1;
        public string UserComment { get; set; }
    }

    /// <summary>
    /// Contains the validated service selection and authoritative unit price used by purchase and renewal flows.
    /// </summary>
    /// <remarks>
    /// The object is detached configuration-derived data. Metered selections include
    /// <see cref="MeteredPriceBreakdown" /> so confirmation messages can explain the same amount that wallet and
    /// account-creation code consumes without recalculating financial rules in the presentation layer.
    /// </remarks>
    public class XuiV3ResolvedPurchase
    {
        public XuiV3ServiceDefinition Service { get; set; }
        public XuiV3DurationOption Duration { get; set; }
        public XuiV3UnlimitedPlan UnlimitedPlan { get; set; }
        public int TrafficGb { get; set; }
        public long TrafficBytes { get; set; }
        public int DurationDays { get; set; }
        public int LimitIp { get; set; }
        public long PriceToman { get; set; }
        public bool IsUnlimited { get; set; }
        /// <summary>
        /// Gets or sets the authoritative metered unit-price components, or <c>null</c> for fixed-price unlimited plans.
        /// </summary>
        /// <remarks>
        /// Values are Iranian toman major units and describe one account. Callers multiply only
        /// <see cref="PriceToman" /> by account count; they must not round or sum these components again.
        /// </remarks>
        public XuiV3MeteredPriceBreakdown MeteredPriceBreakdown { get; set; }
    }

    /// <summary>
    /// Describes every component of the authoritative price for one metered XUI v3 account.
    /// </summary>
    /// <remarks>
    /// Finite durations consist of traffic and day subtotals. Lifetime durations ignore the daily rate and apply the
    /// configured multiplier to the traffic subtotal. <see cref="RawTotalToman" /> preserves the pre-rounding amount,
    /// while <see cref="TotalPriceToman" /> is the whole-toman value that may be charged.
    /// </remarks>
    public class XuiV3MeteredPriceBreakdown
    {
        /// <summary>Gets or sets the selected traffic quantity in whole GB.</summary>
        public int TrafficGb { get; set; }
        /// <summary>Gets or sets the selected role's price for one GB in Iranian toman.</summary>
        public long PricePerGbToman { get; set; }
        /// <summary>Gets or sets the traffic subtotal in Iranian toman before duration pricing.</summary>
        public long TrafficSubtotalToman { get; set; }
        /// <summary>Gets or sets the selected duration in days; zero represents lifetime.</summary>
        public int DurationDays { get; set; }
        /// <summary>Gets or sets the selected role's daily price in Iranian toman; lifetime selections store zero.</summary>
        public long PricePerDayToman { get; set; }
        /// <summary>Gets or sets the finite-duration day subtotal in Iranian toman; lifetime selections store zero.</summary>
        public long DurationSubtotalToman { get; set; }
        /// <summary>Gets or sets the lifetime multiplier applied to traffic; finite durations store <c>1</c>.</summary>
        public double LifetimePriceMultiplier { get; set; } = 1D;
        /// <summary>Gets or sets whether the selected duration uses lifetime multiplier pricing.</summary>
        public bool IsLifetime { get; set; }
        /// <summary>Gets or sets the exact decimal total before whole-toman upward rounding.</summary>
        public decimal RawTotalToman { get; set; }
        /// <summary>Gets or sets the final whole-toman price for one account.</summary>
        public long TotalPriceToman { get; set; }
    }
}
