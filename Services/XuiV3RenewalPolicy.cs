using Adminbot.Domain;
using Newtonsoft.Json;

public static class XuiV3RenewalPolicy
{
    private const long BytesPerGb = 1024L * 1024L * 1024L;

    public static XuiV3RenewalCalculation Calculate(
        XuiV3Client client,
        XuiV3ResolvedPurchase resolved,
        string action,
        long actorTelegramUserId = 0)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));
        if (resolved == null)
            throw new ArgumentNullException(nameof(resolved));

        return CalculateCore(
            client,
            resolved.Service,
            resolved.IsUnlimited,
            resolved.TrafficGb,
            resolved.TrafficBytes > 0 ? resolved.TrafficBytes : GbToBytes(resolved.TrafficGb),
            resolved.DurationDays,
            resolved.LimitIp,
            resolved.PriceToman,
            resolved.IsUnlimited ? resolved.UnlimitedPlan?.Key : resolved.Duration?.Key,
            resolved.IsUnlimited ? resolved.UnlimitedPlan?.DisplayName : resolved.Duration?.DisplayName,
            action,
            actorTelegramUserId);
    }

    public static XuiV3RenewalCalculation CalculateAdmin(
        XuiV3Client client,
        XuiV3ServiceDefinition service,
        int addTrafficGb,
        int addDays,
        string action,
        long actorTelegramUserId = 0)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));

        return CalculateCore(
            client,
            service,
            service?.IsUnlimited == true,
            Math.Max(0, addTrafficGb),
            GbToBytes(Math.Max(0, addTrafficGb)),
            Math.Max(0, addDays),
            client.LimitIp,
            0,
            null,
            null,
            action,
            actorTelegramUserId);
    }

    private static XuiV3RenewalCalculation CalculateCore(
        XuiV3Client client,
        XuiV3ServiceDefinition service,
        bool isUnlimited,
        int renewedTrafficGb,
        long renewedTrafficBytes,
        int addedDurationDays,
        int fallbackLimitIp,
        long priceToman,
        string planKey,
        string planName,
        string action,
        long actorTelegramUserId)
    {
        var metadata = TryReadMetadata(client.Comment);
        var ownerTelegramUserId = GetOwnerTelegramUserId(client, metadata, actorTelegramUserId);
        var currentTotalBytes = GetTotalBytes(client);
        var usedBytes = GetUsedBytes(client);
        var currentExpiryTime = GetExpiryTime(client);
        var remainingDays = CalculateRemainingDays(currentExpiryTime);
        var isExpired = IsExpired(currentExpiryTime);

        long updatedTotalBytes;
        long updatedExpiryTime;
        long targetAvailableBytes;
        int targetAvailableGb;
        int finalDurationDays;
        var shouldResetTraffic = false;

        if (isUnlimited)
        {
            finalDurationDays = Math.Max(0, remainingDays + addedDurationDays);
            updatedExpiryTime = finalDurationDays <= 0 ? 0 : -DaysToMilliseconds(finalDurationDays);
            targetAvailableGb = CalculateUnlimitedFairUsageGb(service, finalDurationDays, renewedTrafficGb);
            targetAvailableBytes = GbToBytes(targetAvailableGb);
            updatedTotalBytes = Math.Max(0, usedBytes) + targetAvailableBytes;
        }
        else
        {
            finalDurationDays = addedDurationDays <= 0 ? 0 : remainingDays + addedDurationDays;
            updatedExpiryTime = CalculateMeteredExpiryTime(currentExpiryTime, addedDurationDays);
            shouldResetTraffic = isExpired;
            targetAvailableBytes = renewedTrafficBytes;
            targetAvailableGb = renewedTrafficGb;
            updatedTotalBytes = shouldResetTraffic
                ? renewedTrafficBytes
                : currentTotalBytes + renewedTrafficBytes;
        }

        metadata ??= new XuiV3ClientMetadata
        {
            TelegramUserId = ownerTelegramUserId,
            ServiceKey = service?.Key ?? "unknown",
            ServiceName = service?.DisplayName ?? "unknown"
        };

        metadata.TelegramUserId = ownerTelegramUserId;
        if (service != null)
        {
            metadata.ServiceKey = service.Key;
            metadata.ServiceName = service.DisplayName;
            metadata.ServiceKind = service.Kind;
        }

        if (!string.IsNullOrWhiteSpace(planKey))
            metadata.PlanKey = planKey;
        if (!string.IsNullOrWhiteSpace(planName))
            metadata.PlanName = planName;

        metadata.LimitIp = client.LimitIp > 0 ? client.LimitIp : fallbackLimitIp;
        metadata.PriceToman = priceToman > 0 ? priceToman : metadata.PriceToman;
        metadata.LastUpdatedByTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : ownerTelegramUserId;
        metadata.LastAction = action;
        metadata.LastRenewedAtUtc = DateTime.UtcNow;
        metadata.DurationDays = finalDurationDays;
        metadata.TrafficBytes = isUnlimited ? targetAvailableBytes : updatedTotalBytes;
        metadata.TrafficGb = BytesToRoundedGb(metadata.TrafficBytes);
        metadata.Renewals ??= new List<XuiV3ClientRenewalRecord>();
        metadata.Renewals.Add(new XuiV3ClientRenewalRecord
        {
            ActorTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : ownerTelegramUserId,
            AddedTrafficGb = isUnlimited ? targetAvailableGb : renewedTrafficGb,
            AddedDurationDays = addedDurationDays,
            TotalBytesAfter = updatedTotalBytes,
            ExpiryTimeAfter = updatedExpiryTime
        });

        var payload = CopyClientPayload(client);
        payload.TotalGB = updatedTotalBytes;
        payload.ExpiryTime = updatedExpiryTime;
        payload.TgId = ownerTelegramUserId;
        payload.LimitIp = client.LimitIp;
        payload.Enable = true;
        payload.Comment = JsonConvert.SerializeObject(metadata, Formatting.None);

        return new XuiV3RenewalCalculation
        {
            Payload = payload,
            IsUnlimited = isUnlimited,
            IsExpiredBeforeRenew = isExpired,
            ShouldResetTraffic = shouldResetTraffic,
            CurrentTotalBytes = currentTotalBytes,
            UsedBytes = usedBytes,
            RenewedTrafficGb = renewedTrafficGb,
            RenewedTrafficBytes = renewedTrafficBytes,
            TargetAvailableTrafficGb = targetAvailableGb,
            TargetAvailableTrafficBytes = targetAvailableBytes,
            TotalBytesAfterRenew = updatedTotalBytes,
            CurrentExpiryTime = currentExpiryTime,
            UpdatedExpiryTime = updatedExpiryTime,
            RemainingDaysBeforeRenew = remainingDays,
            AddedDurationDays = addedDurationDays,
            FinalDurationDays = finalDurationDays
        };
    }

    private static XuiV3ClientPayload CopyClientPayload(XuiV3Client client)
    {
        return new XuiV3ClientPayload
        {
            Email = client.Email,
            Uuid = client.Uuid,
            Password = client.Password,
            TotalGB = GetTotalBytes(client),
            ExpiryTime = GetExpiryTime(client),
            TgId = client.TgId,
            LimitIp = client.LimitIp,
            Enable = client.Enable,
            SubId = client.SubId,
            Flow = client.Flow,
            Comment = client.Comment,
            Group = client.Group,
            Reverse = client.Reverse,
            Extra = client.Extra
        };
    }

    private static int CalculateUnlimitedFairUsageGb(XuiV3ServiceDefinition service, int finalDurationDays, int fallbackTrafficGb)
    {
        if (finalDurationDays <= 0)
            return 0;

        var plans = service?.UnlimitedPlans?
            .Where(plan => plan.IsEnabled && plan.Days > 0 && plan.FairUsageGb > 0)
            .OrderBy(plan => plan.Days)
            .ToList() ?? new List<XuiV3UnlimitedPlan>();

        if (plans.Count == 0)
            return Math.Max(0, fallbackTrafficGb);

        var direct = plans.FirstOrDefault(plan => plan.Days >= finalDurationDays);
        if (direct != null)
            return direct.FairUsageGb;

        var largest = plans[^1];
        var fullBlocks = finalDurationDays / largest.Days;
        var remainderDays = finalDurationDays % largest.Days;
        var fairUsageGb = fullBlocks * largest.FairUsageGb;
        if (remainderDays > 0)
            fairUsageGb += plans.First(plan => plan.Days >= remainderDays).FairUsageGb;

        return fairUsageGb;
    }

    private static long CalculateMeteredExpiryTime(long currentExpiryTime, int addedDurationDays)
    {
        if (addedDurationDays <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var baseDate = currentExpiryTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(currentExpiryTime)
            : now;

        if (baseDate < now)
            baseDate = now;

        return baseDate.AddDays(addedDurationDays).ToUnixTimeMilliseconds();
    }

    private static int CalculateRemainingDays(long expiryTime)
    {
        if (expiryTime < 0)
            return Math.Max(0, (int)Math.Ceiling(Math.Abs(expiryTime) / (double)TimeSpan.FromDays(1).TotalMilliseconds));

        if (expiryTime == 0)
            return 0;

        var remainingMilliseconds = expiryTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMilliseconds <= 0)
            return 0;

        return (int)Math.Ceiling(remainingMilliseconds / (double)TimeSpan.FromDays(1).TotalMilliseconds);
    }

    private static bool IsExpired(long expiryTime)
    {
        return expiryTime > 0 && expiryTime <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static long GetOwnerTelegramUserId(XuiV3Client client, XuiV3ClientMetadata metadata, long actorTelegramUserId)
    {
        if (client?.TgId > 0)
            return client.TgId;

        if (metadata?.TelegramUserId > 0)
            return metadata.TelegramUserId;

        return actorTelegramUserId > 0 ? actorTelegramUserId : 0;
    }

    private static XuiV3ClientMetadata TryReadMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
        }
        catch
        {
            return null;
        }
    }

    private static long GetUsedBytes(XuiV3Client client)
    {
        if (client == null)
            return 0;

        return (client.Traffic?.Up ?? ReadLongExtra(client, "up")) +
               (client.Traffic?.Down ?? ReadLongExtra(client, "down"));
    }

    private static long GetTotalBytes(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.TotalGB > 0)
            return client.TotalGB;

        if (client.Traffic?.TotalGB > 0)
            return client.Traffic.TotalGB;

        if (client.Traffic?.Total > 0)
            return client.Traffic.Total;

        return ReadLongExtra(client, "totalGB");
    }

    private static long GetExpiryTime(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        var trafficExpiryTime = client.Traffic?.ExpiryTime ?? 0;
        if (trafficExpiryTime != 0)
            return trafficExpiryTime;

        return ReadLongExtra(client, "expiryTime");
    }

    private static long ReadLongExtra(XuiV3Client client, string key)
    {
        if (client?.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null)
            return 0;

        try
        {
            return token.ToObject<long>();
        }
        catch
        {
            return 0;
        }
    }

    private static long GbToBytes(int gigabytes)
    {
        return Math.Max(0, gigabytes) * BytesPerGb;
    }

    private static int BytesToRoundedGb(long bytes)
    {
        if (bytes <= 0)
            return 0;

        return (int)Math.Ceiling(bytes / (double)BytesPerGb);
    }

    private static long DaysToMilliseconds(int days)
    {
        return (long)Math.Round(TimeSpan.FromDays(days).TotalMilliseconds);
    }
}

public sealed class XuiV3RenewalCalculation
{
    public XuiV3ClientPayload Payload { get; set; }
    public bool IsUnlimited { get; set; }
    public bool IsExpiredBeforeRenew { get; set; }
    public bool ShouldResetTraffic { get; set; }
    public long CurrentTotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public int RenewedTrafficGb { get; set; }
    public long RenewedTrafficBytes { get; set; }
    public int TargetAvailableTrafficGb { get; set; }
    public long TargetAvailableTrafficBytes { get; set; }
    public long TotalBytesAfterRenew { get; set; }
    public long CurrentExpiryTime { get; set; }
    public long UpdatedExpiryTime { get; set; }
    public int RemainingDaysBeforeRenew { get; set; }
    public int AddedDurationDays { get; set; }
    public int FinalDurationDays { get; set; }
}
