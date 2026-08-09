using Adminbot.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Normalizes traffic, quota, expiry, lifecycle timestamps, and renewal metadata from XUI v3 client responses.
/// </summary>
/// <remarks>
/// Different 3x-ui v3 releases distribute values between top-level fields, <c>traffic</c>, and JSON extension data.
/// Reminder workers use this resolver as their only interpretation layer so time and volume modules classify the same
/// client consistently without issuing per-client API requests.
/// </remarks>
public static class XuiV3ClientUsageResolver
{
    /// <summary>First customer-facing traffic threshold, expressed as a whole percentage.</summary>
    public const int WarningThreshold80 = 80;
    /// <summary>Urgent customer-facing traffic threshold, expressed as a whole percentage.</summary>
    public const int WarningThreshold90 = 90;
    /// <summary>Final customer-facing traffic threshold, expressed as a whole percentage.</summary>
    public const int FinalThreshold99 = 99;

    /// <summary>
    /// Builds one normalized usage snapshot from a client returned by <c>/panel/api/clients/list</c>.
    /// </summary>
    /// <param name="client">
    /// Existing XUI v3 list client. The object may omit nested traffic or extension fields but must not contain panel
    /// credentials.
    /// </param>
    /// <returns>
    /// A non-null detached snapshot. Missing counters, quota, timestamps, and owner metadata are represented by zero
    /// or null values and must be filtered by the caller before customer notification.
    /// </returns>
    /// <remarks>
    /// Upload and download counters are clamped at zero and summed without overflowing <see cref="long"/>. Quota
    /// lookup follows top-level <c>totalGB</c>, nested <c>traffic.totalGB</c>, nested <c>traffic.total</c>, and raw
    /// extension data. Missing nested <c>traffic</c> objects are valid for historical or not-yet-observed panel
    /// clients and resolve to the available top-level or extension values. A newer <c>updatedAt</c> alone is
    /// deliberately not classified as a renewal.
    /// </remarks>
    /// <example>
    /// <code>
    /// var snapshot = XuiV3ClientUsageResolver.Resolve(client);
    /// var threshold = XuiV3ClientUsageResolver.GetHighestReachedThreshold(snapshot);
    /// </code>
    /// </example>
    public static XuiV3ClientUsageSnapshot Resolve(XuiV3Client client)
    {
        if (client == null)
            return new XuiV3ClientUsageSnapshot();

        var upload = Math.Max(0, client.Traffic?.Up ?? ReadLongExtra(client, "up"));
        var download = Math.Max(0, client.Traffic?.Down ?? ReadLongExtra(client, "down"));
        var usedBytes = upload > long.MaxValue - download ? long.MaxValue : upload + download;
        var totalBytes = ResolveTotalBytes(client);
        var metadata = TryReadMetadata(client.Comment);

        return new XuiV3ClientUsageSnapshot
        {
            ClientId = client.Id,
            Email = client.Email ?? string.Empty,
            ClientCreatedAt = ReadLongExtra(client, "createdAt"),
            PanelUpdatedAt = ReadLongExtra(client, "updatedAt"),
            UsedBytes = usedBytes,
            TotalBytes = totalBytes,
            ExpiryTime = ResolveExpiryTime(client),
            ClientEnabled = client.Enable,
            TrafficEnabled = client.Traffic == null ? null : client.Traffic.Enable,
            OwnerTelegramUserId = client.TgId > 0 ? client.TgId : metadata?.TelegramUserId ?? 0,
            CreatedByBotId = string.IsNullOrWhiteSpace(metadata?.CreatedByBotId)
                ? BotContextAccessor.DefaultBotId
                : metadata.CreatedByBotId,
            LastRenewedAtUtc = metadata?.LastRenewedAtUtc
        };
    }

    /// <summary>
    /// Returns the highest configured traffic threshold reached by raw byte consumption.
    /// </summary>
    /// <param name="snapshot">Normalized finite-quota snapshot produced by <see cref="Resolve"/>.</param>
    /// <returns>99, 90, 80, or zero when the client has no finite quota or has not reached 80 percent.</returns>
    /// <remarks>
    /// Decimal multiplication is used instead of rounded display percentages. A jump from below 80 directly above
    /// 99 therefore yields only 99, allowing the durable store to mark all lower thresholds implicitly handled.
    /// </remarks>
    public static int GetHighestReachedThreshold(XuiV3ClientUsageSnapshot snapshot)
    {
        if (snapshot == null || snapshot.TotalBytes <= 0)
            return 0;

        if (HasReached(snapshot.UsedBytes, snapshot.TotalBytes, FinalThreshold99))
            return FinalThreshold99;
        if (HasReached(snapshot.UsedBytes, snapshot.TotalBytes, WarningThreshold90))
            return WarningThreshold90;
        if (HasReached(snapshot.UsedBytes, snapshot.TotalBytes, WarningThreshold80))
            return WarningThreshold80;
        return 0;
    }

    /// <summary>
    /// Determines whether the existing daily time reminder must exclude a volume-ended account.
    /// </summary>
    /// <param name="snapshot">Normalized client usage snapshot.</param>
    /// <returns>
    /// <c>true</c> when finite consumption reached the full quota, or when the panel traffic row is disabled at or
    /// above the final 99 percent threshold; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Manual or time-based disablement below the final traffic threshold is not treated as volume exhaustion. This
    /// keeps the existing time-reminder behavior unchanged outside the reported volume-expiry bug.
    /// </remarks>
    public static bool IsVolumeEndedForTimeReminder(XuiV3ClientUsageSnapshot snapshot)
    {
        if (snapshot == null || snapshot.TotalBytes <= 0)
            return false;

        return snapshot.UsedBytes >= snapshot.TotalBytes ||
               snapshot.TrafficEnabled == false &&
               HasReached(snapshot.UsedBytes, snapshot.TotalBytes, FinalThreshold99);
    }

    /// <summary>
    /// Determines whether a client may produce its currently reached volume threshold notification.
    /// </summary>
    /// <param name="snapshot">Normalized client usage snapshot with a positive finite quota.</param>
    /// <param name="nowUtc">Current UTC instant used to reject already time-expired accounts.</param>
    /// <param name="threshold">Highest reached reminder threshold: 80, 90, or 99.</param>
    /// <returns>
    /// <c>true</c> for a time-valid active client, or for a time-valid disabled client at the final 99 percent level;
    /// otherwise <c>false</c>.
    /// </returns>
    public static bool CanNotifyVolumeThreshold(
        XuiV3ClientUsageSnapshot snapshot,
        DateTime nowUtc,
        int threshold)
    {
        if (snapshot == null || snapshot.TotalBytes <= 0 || threshold <= 0 || IsTimeExpired(snapshot, nowUtc))
            return false;

        var isPanelEnabled = snapshot.ClientEnabled && snapshot.TrafficEnabled != false;
        return isPanelEnabled || threshold >= FinalThreshold99;
    }

    /// <summary>
    /// Checks a positive absolute XUI expiry timestamp against the supplied UTC instant.
    /// </summary>
    /// <param name="snapshot">Normalized client snapshot.</param>
    /// <param name="nowUtc">Current UTC instant; non-UTC kinds are normalized as UTC.</param>
    /// <returns>
    /// <c>true</c> only when expiry is a positive Unix-millisecond timestamp at or before now. Zero lifetime values and
    /// negative first-connection durations are not considered expired here.
    /// </returns>
    public static bool IsTimeExpired(XuiV3ClientUsageSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot?.ExpiryTime <= 0)
            return false;

        var utc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return snapshot.ExpiryTime <= new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Creates a credential-free stable key for the configured XUI panel.
    /// </summary>
    /// <param name="serverInfo">
    /// Panel descriptor containing the base URL and optional root path. API tokens, usernames, and passwords are not
    /// read or hashed.
    /// </param>
    /// <returns>A lowercase 64-character SHA-256 hexadecimal key suitable for users.db uniqueness.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="serverInfo"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the panel base URL is missing.</exception>
    /// <example>
    /// <code>
    /// var panelKey = XuiV3ClientUsageResolver.BuildPanelKey(serverInfo);
    /// </code>
    /// </example>
    public static string BuildPanelKey(ServerInfo serverInfo)
    {
        ArgumentNullException.ThrowIfNull(serverInfo);
        if (string.IsNullOrWhiteSpace(serverInfo.Url))
            throw new InvalidOperationException("XUI panel URL is required to build the volume-reminder key.");

        return XuiV3LinkChangeOperationStore.BuildPanelKey(serverInfo);
    }

    /// <summary>
    /// Compares raw usage bytes with one whole percentage of a positive quota without display rounding.
    /// </summary>
    /// <param name="usedBytes">Non-negative upload-plus-download consumption in bytes.</param>
    /// <param name="totalBytes">Positive configured quota in bytes.</param>
    /// <param name="threshold">Whole percentage from 1 through 100.</param>
    /// <returns><c>true</c> when consumption is at or above the threshold; otherwise <c>false</c>.</returns>
    private static bool HasReached(long usedBytes, long totalBytes, int threshold)
        => usedBytes >= 0 && totalBytes > 0 && threshold > 0 &&
           (decimal)usedBytes * 100M >= (decimal)totalBytes * threshold;

    /// <summary>
    /// Resolves the finite quota across supported XUI v3 list response shapes.
    /// </summary>
    /// <param name="client">Panel client whose quota fields are being normalized.</param>
    /// <returns>Positive quota bytes, or zero when the account has no supplied finite limit.</returns>
    private static long ResolveTotalBytes(XuiV3Client client)
    {
        if (client.TotalGB > 0)
            return client.TotalGB;
        if (client.Traffic?.TotalGB > 0)
            return client.Traffic.TotalGB;
        if (client.Traffic?.Total > 0)
            return client.Traffic.Total;
        return Math.Max(0, ReadLongExtra(client, "totalGB"));
    }

    /// <summary>
    /// Resolves the expiry timestamp across supported XUI v3 list response shapes.
    /// </summary>
    /// <param name="client">Panel client whose expiry fields are being normalized.</param>
    /// <returns>Positive Unix milliseconds, zero lifetime, or negative first-connection duration milliseconds.</returns>
    /// <remarks>
    /// The nested traffic object is optional in real <c>/panel/api/clients/list</c> responses. Its nullable expiry
    /// value is materialized before comparison so a missing object cannot pass a lifted <c>!= 0</c> comparison and
    /// then be dereferenced.
    /// </remarks>
    private static long ResolveExpiryTime(XuiV3Client client)
    {
        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        var trafficExpiryTime = client.Traffic?.ExpiryTime ?? 0;
        if (trafficExpiryTime != 0)
            return trafficExpiryTime;

        return ReadLongExtra(client, "expiryTime");
    }

    /// <summary>
    /// Parses bot-owned client metadata without allowing malformed historical comments to stop reminder scans.
    /// </summary>
    /// <param name="comment">Raw XUI client comment, which may be plain text or bot metadata JSON.</param>
    /// <returns>Parsed metadata, or null when the comment is empty or not valid metadata JSON.</returns>
    private static XuiV3ClientMetadata TryReadMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one signed 64-bit extension value while tolerating absent or incompatible JSON tokens.
    /// </summary>
    /// <param name="client">Panel client that owns extension data.</param>
    /// <param name="key">Case-sensitive JSON property name such as <c>updatedAt</c>.</param>
    /// <returns>Parsed value, or zero when unavailable.</returns>
    private static long ReadLongExtra(XuiV3Client client, string key)
    {
        if (client?.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null || token.Type == JTokenType.Null)
            return 0;

        try
        {
            return token.ToObject<long>();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            return 0;
        }
    }
}

/// <summary>
/// Detached normalized usage and ownership facts for one XUI v3 client-list row.
/// </summary>
public sealed class XuiV3ClientUsageSnapshot
{
    /// <summary>Numeric panel client id used by renewal callbacks and durable reminder identity.</summary>
    public int ClientId { get; init; }
    /// <summary>Current panel client email safe to show only to its resolved owner.</summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>Panel <c>createdAt</c> Unix-millisecond value, or zero when omitted.</summary>
    public long ClientCreatedAt { get; init; }
    /// <summary>Panel <c>updatedAt</c> Unix-millisecond value, or zero when omitted.</summary>
    public long PanelUpdatedAt { get; init; }
    /// <summary>Non-negative upload plus download consumption in bytes.</summary>
    public long UsedBytes { get; init; }
    /// <summary>Finite traffic quota in bytes, or zero for missing/unlimited quota.</summary>
    public long TotalBytes { get; init; }
    /// <summary>Positive absolute expiry, zero lifetime, or negative first-connection duration in milliseconds.</summary>
    public long ExpiryTime { get; init; }
    /// <summary>Top-level panel client enabled flag.</summary>
    public bool ClientEnabled { get; init; }
    /// <summary>Nested traffic enabled flag, or null when the list response omitted traffic.</summary>
    public bool? TrafficEnabled { get; init; }
    /// <summary>Numeric Telegram owner id resolved from <c>tgId</c> or bot metadata.</summary>
    public long OwnerTelegramUserId { get; init; }
    /// <summary>Owned or tenant bot id that originally created the account.</summary>
    public string CreatedByBotId { get; init; } = BotContextAccessor.DefaultBotId;
    /// <summary>Latest bot-recorded successful renewal timestamp, when present in metadata.</summary>
    public DateTime? LastRenewedAtUtc { get; init; }
}
