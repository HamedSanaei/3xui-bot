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
        public List<int> TrafficOptionsGb { get; set; } = new List<int>();
        public List<XuiV3DurationOption> DurationOptions { get; set; } = new List<XuiV3DurationOption>();
        public List<XuiV3UnlimitedPlan> UnlimitedPlans { get; set; } = new List<XuiV3UnlimitedPlan>();

        public bool IsUnlimited => string.Equals(Kind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase);

        public long GetPricePerGb(bool isColleague)
        {
            return isColleague ? PricePerGb.Colleague : PricePerGb.User;
        }
    }

    public static class XuiV3ServiceKinds
    {
        public const string Metered = "metered";
        public const string Unlimited = "unlimited";
    }

    public class XuiV3RolePrice
    {
        public long User { get; set; }
        public long Colleague { get; set; }

        public long GetForRole(bool isColleague)
        {
            return isColleague ? Colleague : User;
        }
    }

    public class XuiV3DurationOption
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public int Days { get; set; }
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
    }
}
