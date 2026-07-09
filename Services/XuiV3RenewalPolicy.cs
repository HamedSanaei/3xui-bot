using Adminbot.Domain;
using Newtonsoft.Json;

/// <summary>
/// Calculates replacement payloads for XUI v3 account renewals while preserving the client's identity,
/// ownership, access limits, protocol fields, and metadata.
/// </summary>
/// <remarks>
/// Metered and unlimited services share this policy so owned bots, tenant storefronts, and super-admin flows
/// apply identical traffic and expiry rules. Unlimited renewals add the selected plan's exact traffic and days
/// while an account is active. Once expired, they reset traffic and restart the selected duration from the first
/// connection by writing a negative expiry duration.
/// </remarks>
public static class XuiV3RenewalPolicy
{
    private const long BytesPerGb = 1024L * 1024L * 1024L;

    /// <summary>
    /// Calculates a customer or tenant renewal from a fully resolved service-plan selection.
    /// </summary>
    /// <param name="client">
    /// Current XUI v3 client read from the panel. The value is required and may have a null traffic object;
    /// identity fields and panel-specific extension data are preserved in the resulting payload.
    /// </param>
    /// <param name="resolved">
    /// Purchase-service result containing the exact renewal traffic in bytes, duration in days, price in toman,
    /// service type, and selected plan. The value must come from <see cref="XuiV3PurchaseService.ResolvePurchase"/>.
    /// </param>
    /// <param name="action">
    /// Audit action stored in client metadata, such as <c>user-renew</c> or <c>tenant-renew</c>. It must not
    /// contain secrets and may be shown in administrative diagnostics.
    /// </param>
    /// <param name="actorTelegramUserId">
    /// Numeric Telegram user id of the customer or administrator initiating the renewal. Pass zero only when the
    /// actor is unknown; the existing account owner is then used for metadata where possible.
    /// </param>
    /// <returns>
    /// A detached renewal calculation containing the complete XUI replacement payload, reset requirement,
    /// exact traffic added, final quota, expiry, and duration. The caller must send the payload to the panel and
    /// invoke the traffic-reset API when <see cref="XuiV3RenewalCalculation.ShouldResetTraffic"/> is true.
    /// </returns>
    /// <remarks>
    /// This method does not call XUI, reset traffic, charge a wallet, or persist a database record. For unlimited
    /// accounts it never derives a new quota from the final duration; it uses the selected plan's exact traffic.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/> or <paramref name="resolved"/> is null.
    /// </exception>
    /// <example>
    /// <code>
    /// var renewal = XuiV3RenewalPolicy.Calculate(client, resolved, "user-renew", telegramUserId);
    /// var response = await ApiServicev3.UpdateClientAsync(
    ///     serverInfo, configuration, client.Email, renewal.Payload, cancellationToken);
    /// </code>
    /// </example>
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

    /// <summary>
    /// Calculates a super-admin renewal from explicitly entered traffic and day values.
    /// </summary>
    /// <param name="client">
    /// Current XUI v3 client read from the panel. The value is required; UUID, password, subId, Telegram owner,
    /// IP limit, protocol fields, and extension data are preserved in the returned payload.
    /// </param>
    /// <param name="service">
    /// Service definition resolved for the target account. Its kind determines whether unlimited or metered
    /// renewal rules apply. A null value is tolerated for legacy metered accounts but cannot mark an account unlimited.
    /// </param>
    /// <param name="addTrafficGb">
    /// Exact traffic to add in binary gigabytes. Negative values are normalized to zero. For expired accounts this
    /// becomes the new total quota after traffic counters are reset.
    /// </param>
    /// <param name="addDays">
    /// Exact duration to add in days. Negative values are normalized to zero. For expired unlimited accounts this
    /// becomes a first-connection duration rather than being added to the expired timestamp.
    /// </param>
    /// <param name="action">
    /// Audit action written to the client's JSON metadata. It must not contain credentials or payment secrets.
    /// </param>
    /// <param name="actorTelegramUserId">
    /// Numeric Telegram id of the super-admin performing the renewal. It is recorded for audit but does not replace
    /// the account owner's Telegram id.
    /// </param>
    /// <returns>
    /// A detached calculation and replacement payload. The caller remains responsible for updating XUI and resetting
    /// counters when requested by <see cref="XuiV3RenewalCalculation.ShouldResetTraffic"/>.
    /// </returns>
    /// <remarks>
    /// No wallet is charged by this method. It applies the same active-versus-expired unlimited policy used by customer
    /// and tenant renewals so admin actions cannot produce a different quota or expiry shape.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    /// <example>
    /// <code>
    /// var renewal = XuiV3RenewalPolicy.CalculateAdmin(
    ///     client, service, addTrafficGb: 100, addDays: 31, action: "admin-renew", actorTelegramUserId);
    /// if (renewal.ShouldResetTraffic)
    ///     await ApiServicev3.ResetClientTrafficAsync(serverInfo, configuration, client.Email, cancellationToken);
    /// </code>
    /// </example>
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

    /// <summary>
    /// Applies the shared renewal arithmetic and assembles the complete replacement client payload.
    /// </summary>
    /// <param name="client">Current panel client whose identity and settings must be preserved.</param>
    /// <param name="service">Resolved service definition used to refresh service metadata.</param>
    /// <param name="isUnlimited">Whether unlimited renewal rules apply to this account.</param>
    /// <param name="renewedTrafficGb">Exact traffic selected for this renewal in binary gigabytes.</param>
    /// <param name="renewedTrafficBytes">Exact traffic selected for this renewal in bytes.</param>
    /// <param name="addedDurationDays">Exact plan duration added by this renewal in days.</param>
    /// <param name="fallbackLimitIp">Plan IP limit used only when metadata lacks a value; the panel limit is preserved.</param>
    /// <param name="priceToman">Price paid for this renewal in Iranian toman, stored for audit metadata.</param>
    /// <param name="planKey">Selected plan key, or null for an admin-entered renewal without a catalog plan.</param>
    /// <param name="planName">Selected plan display name, or null when no catalog plan was selected.</param>
    /// <param name="action">Audit action stored in the client's metadata.</param>
    /// <param name="actorTelegramUserId">Numeric Telegram id of the actor performing the renewal.</param>
    /// <returns>
    /// The calculated payload and operational facts needed by XUI update, traffic reset, logging, website sync,
    /// and customer notification callers.
    /// </returns>
    /// <remarks>
    /// Active unlimited accounts receive direct addition: <c>TotalGB += renewedTrafficBytes</c>, and their current
    /// expiry representation is extended by the selected days. Expired unlimited accounts receive
    /// <c>TotalGB = renewedTrafficBytes</c>, negative first-connection expiry, and a required counter reset.
    /// No nearest-plan or duration-to-fair-usage interpretation occurs here.
    /// </remarks>
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
            shouldResetTraffic = isExpired;
            updatedTotalBytes = shouldResetTraffic
                ? renewedTrafficBytes
                : currentTotalBytes + renewedTrafficBytes;
            updatedExpiryTime = CalculateUnlimitedExpiryTime(currentExpiryTime, addedDurationDays, shouldResetTraffic);
            finalDurationDays = CalculateRemainingDays(updatedExpiryTime);

            // Report the actual quota left after the update. The panel total itself is always a direct add or replace;
            // it is never reconstructed from duration or a nearest fair-usage plan.
            targetAvailableBytes = shouldResetTraffic
                ? renewedTrafficBytes
                : Math.Max(0, updatedTotalBytes - usedBytes);
            targetAvailableGb = BytesToRoundedGb(targetAvailableBytes);
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
        metadata.TrafficBytes = updatedTotalBytes;
        metadata.TrafficGb = BytesToRoundedGb(metadata.TrafficBytes);
        metadata.Renewals ??= new List<XuiV3ClientRenewalRecord>();
        metadata.Renewals.Add(new XuiV3ClientRenewalRecord
        {
            ActorTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : ownerTelegramUserId,
            AddedTrafficGb = renewedTrafficGb,
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

    /// <summary>
    /// Calculates the XUI expiry value for an unlimited renewal without converting total days into a different plan.
    /// </summary>
    /// <param name="currentExpiryTime">
    /// Existing XUI expiry in milliseconds. A negative value means duration from first connection, a positive value
    /// is an absolute Unix timestamp, and zero has no usable remaining duration for this renewal calculation.
    /// </param>
    /// <param name="addedDurationDays">Exact number of plan days being added; values at or below zero add no duration.</param>
    /// <param name="resetFromFirstConnection">
    /// Whether the account is expired and must restart from first connection. When true, the previous timestamp is
    /// ignored and the result is the negative duration of the selected plan.
    /// </param>
    /// <returns>
    /// Negative duration milliseconds for first-connection accounts, an extended positive Unix timestamp for active
    /// connected accounts, or the unchanged/zero value when no duration is added.
    /// </returns>
    /// <remarks>
    /// This method preserves the panel's current expiry mode for active accounts. It intentionally does not round an
    /// absolute timestamp into whole days, preventing renewal from moving the user's expiration time of day.
    /// </remarks>
    private static long CalculateUnlimitedExpiryTime(
        long currentExpiryTime,
        int addedDurationDays,
        bool resetFromFirstConnection)
    {
        if (addedDurationDays <= 0)
            return resetFromFirstConnection ? 0 : currentExpiryTime;

        var addedMilliseconds = DaysToMilliseconds(addedDurationDays);
        if (resetFromFirstConnection || currentExpiryTime == 0)
            return -addedMilliseconds;

        return currentExpiryTime < 0
            ? currentExpiryTime - addedMilliseconds
            : currentExpiryTime + addedMilliseconds;
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

/// <summary>
/// Describes the complete result of a renewal calculation before the payload is sent to XUI v3.
/// </summary>
/// <remarks>
/// This object is detached and has no database side effects. Callers use it to perform the panel update, decide
/// whether traffic counters must be reset, write audit logs, and build customer-facing renewal summaries.
/// </remarks>
public sealed class XuiV3RenewalCalculation
{
    /// <summary>Complete replacement payload that preserves the current XUI client identity and settings.</summary>
    public XuiV3ClientPayload Payload { get; set; }

    /// <summary>Indicates whether unlimited renewal arithmetic was applied.</summary>
    public bool IsUnlimited { get; set; }

    /// <summary>Indicates whether the account had already expired before this renewal was calculated.</summary>
    public bool IsExpiredBeforeRenew { get; set; }

    /// <summary>Indicates that traffic counters must be reset after a successful panel update.</summary>
    public bool ShouldResetTraffic { get; set; }

    /// <summary>Total quota in bytes before renewal, as reported by the panel.</summary>
    public long CurrentTotalBytes { get; set; }

    /// <summary>Uploaded plus downloaded bytes before renewal; missing panel counters are treated as zero.</summary>
    public long UsedBytes { get; set; }

    /// <summary>Exact traffic selected by the user or admin for this renewal in binary gigabytes.</summary>
    public int RenewedTrafficGb { get; set; }

    /// <summary>Exact traffic selected for this renewal in bytes.</summary>
    public long RenewedTrafficBytes { get; set; }

    /// <summary>Estimated usable quota after renewal in rounded-up binary gigabytes.</summary>
    public int TargetAvailableTrafficGb { get; set; }

    /// <summary>
    /// Estimated usable quota after renewal in bytes. For a reset it equals the selected traffic; otherwise it is
    /// the updated total quota minus already consumed traffic.
    /// </summary>
    public long TargetAvailableTrafficBytes { get; set; }

    /// <summary>Exact XUI total quota in bytes after direct addition or expired-account replacement.</summary>
    public long TotalBytesAfterRenew { get; set; }

    /// <summary>Panel expiry value before renewal, in milliseconds or negative first-connection duration.</summary>
    public long CurrentExpiryTime { get; set; }

    /// <summary>Panel expiry value after renewal, in milliseconds or negative first-connection duration.</summary>
    public long UpdatedExpiryTime { get; set; }

    /// <summary>Whole rounded-up days remaining before renewal; zero indicates no positive remaining duration.</summary>
    public int RemainingDaysBeforeRenew { get; set; }

    /// <summary>Exact duration added by the selected renewal plan in days.</summary>
    public int AddedDurationDays { get; set; }

    /// <summary>Rounded-up remaining duration after renewal in days.</summary>
    public int FinalDurationDays { get; set; }
}
