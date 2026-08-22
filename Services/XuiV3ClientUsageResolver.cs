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
        var clientExpiryTime = NormalizePanelTimestamp(client.ExpiryTime);
        var trafficExpiryTime = NormalizePanelTimestamp(client.Traffic?.ExpiryTime ?? 0);
        var extensionExpiryTime = NormalizePanelTimestamp(ReadLongExtra(client, "expiryTime", "expiry_time", "expiry"));
        var metadataExpiry = ResolveMetadataExpiryEvidence(metadata);

        return new XuiV3ClientUsageSnapshot
        {
            ClientId = client.Id,
            Email = client.Email ?? string.Empty,
            ClientCreatedAt = ReadLongExtra(client, "createdAt"),
            PanelUpdatedAt = ReadLongExtra(client, "updatedAt"),
            UsedBytes = usedBytes,
            TotalBytes = totalBytes,
            ExpiryTime = ResolveEffectiveExpiryTime(clientExpiryTime, trafficExpiryTime, extensionExpiryTime),
            ClientExpiryTime = clientExpiryTime,
            ClientExpirySourcePresent = true,
            TrafficExpiryTime = trafficExpiryTime,
            TrafficExpirySourcePresent = client.Traffic != null,
            ExtensionExpiryTime = extensionExpiryTime,
            ExtensionExpirySourcePresent = HasExtra(client, "expiry_time", "expiry"),
            MetadataExpectedExpiryTime = metadataExpiry.ExpectedExpiryTime,
            MetadataIndicatesLifetime = metadataExpiry.IndicatesLifetime,
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
    /// Produces a detailed, log-safe decision for one volume-reminder threshold.
    /// </summary>
    /// <param name="snapshot">
    /// Normalized XUI client observation. The snapshot must contain raw-byte quota and consumption plus the separate
    /// top-level, traffic-row, and extension expiry sources returned by the panel.
    /// </param>
    /// <param name="nowUtc">Current UTC instant used to classify positive Unix expiry timestamps.</param>
    /// <param name="readOnlyVerificationCompleted">
    /// <c>true</c> only when <paramref name="snapshot"/> came from an identity-checked
    /// <c>GET /panel/api/clients/get/{email}</c> response. This prevents a verified expired account from requesting
    /// the same GET again during the current backoff window.
    /// </param>
    /// <returns>
    /// A detached decision containing a stable status, highest reached threshold, and sanitized per-source summary.
    /// The summary contains no email, UUID, subscription id, panel token, URI, or response body.
    /// </returns>
    /// <remarks>
    /// A client is time-expired only when every present authoritative panel expiry source is a positive timestamp at
    /// or before <paramref name="nowUtc"/>. A future source or a negative first-use duration prevents a false expiry.
    /// When the list response says expired but bot metadata proves a still-current purchased duration, the first pass
    /// returns <see cref="XuiV3VolumeReminderEligibilityStatus.NeedsReadOnlyVerification"/> so the worker can perform
    /// one bounded GET. This method never calls the panel and never changes delivery state.
    /// </remarks>
    /// <example>
    /// <code>
    /// var decision = XuiV3ClientUsageResolver.EvaluateVolumeReminderEligibility(
    ///     snapshot,
    ///     DateTime.UtcNow,
    ///     readOnlyVerificationCompleted: false);
    /// </code>
    /// </example>
    public static XuiV3VolumeReminderEligibilityResult EvaluateVolumeReminderEligibility(
        XuiV3ClientUsageSnapshot snapshot,
        DateTime nowUtc,
        bool readOnlyVerificationCompleted = false)
    {
        var utcNow = NormalizeUtc(nowUtc);
        var nowUnixMilliseconds = new DateTimeOffset(utcNow).ToUnixTimeMilliseconds();
        var threshold = GetHighestReachedThreshold(snapshot);
        var expirySummary = BuildExpirySummary(snapshot, nowUnixMilliseconds);

        if (snapshot == null || snapshot.TotalBytes <= 0)
        {
            return XuiV3VolumeReminderEligibilityResult.Create(
                XuiV3VolumeReminderEligibilityStatus.InvalidQuota,
                threshold,
                expirySummary);
        }

        if (threshold <= 0)
        {
            return XuiV3VolumeReminderEligibilityResult.Create(
                XuiV3VolumeReminderEligibilityStatus.BelowThreshold,
                threshold,
                expirySummary);
        }

        var expiryValues = GetPresentExpiryValues(snapshot);
        var definitivelyExpired = expiryValues.Count > 0 &&
                                  expiryValues.All(value => value > 0 && value <= nowUnixMilliseconds);
        var metadataProvesCurrentDuration = snapshot.MetadataIndicatesLifetime ||
                                            snapshot.MetadataExpectedExpiryTime > nowUnixMilliseconds;
        if (definitivelyExpired)
        {
            var status = !readOnlyVerificationCompleted && metadataProvesCurrentDuration
                ? XuiV3VolumeReminderEligibilityStatus.NeedsReadOnlyVerification
                : XuiV3VolumeReminderEligibilityStatus.TimeExpired;
            return XuiV3VolumeReminderEligibilityResult.Create(status, threshold, expirySummary);
        }

        var isPanelEnabled = snapshot.ClientEnabled && snapshot.TrafficEnabled != false;
        if (!isPanelEnabled && threshold < FinalThreshold99)
        {
            return XuiV3VolumeReminderEligibilityResult.Create(
                XuiV3VolumeReminderEligibilityStatus.DisabledBeforeFinalThreshold,
                threshold,
                expirySummary);
        }

        return XuiV3VolumeReminderEligibilityResult.Create(
            XuiV3VolumeReminderEligibilityStatus.Eligible,
            threshold,
            expirySummary);
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
    /// Checks all present authoritative XUI expiry sources against the supplied UTC instant.
    /// </summary>
    /// <param name="snapshot">Normalized client snapshot.</param>
    /// <param name="nowUtc">Current UTC instant; non-UTC kinds are normalized as UTC.</param>
    /// <returns>
    /// <c>true</c> only when every present source is a positive Unix-millisecond timestamp at or before now. A future
    /// source, a missing/lifetime zero, or a negative first-connection duration prevents a definitive expired result.
    /// </returns>
    public static bool IsTimeExpired(XuiV3ClientUsageSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot == null)
            return false;

        var expiryValues = GetPresentExpiryValues(snapshot);
        if (expiryValues.Count == 0)
            return false;

        var nowUnixMilliseconds = new DateTimeOffset(NormalizeUtc(nowUtc)).ToUnixTimeMilliseconds();
        return expiryValues.All(value => value > 0 && value <= nowUnixMilliseconds);
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
    /// <param name="clientExpiryTime">Normalized top-level client expiry in milliseconds.</param>
    /// <param name="trafficExpiryTime">Normalized nested traffic-row expiry in milliseconds.</param>
    /// <param name="extensionExpiryTime">Normalized legacy extension expiry in milliseconds.</param>
    /// <returns>Positive Unix milliseconds, zero lifetime, or negative first-connection duration milliseconds.</returns>
    /// <remarks>
    /// The nested traffic object is optional in real <c>/panel/api/clients/list</c> responses. Its nullable expiry
    /// value is materialized before comparison so a missing object cannot pass a lifted <c>!= 0</c> comparison and
    /// then be dereferenced.
    /// </remarks>
    private static long ResolveEffectiveExpiryTime(
        long clientExpiryTime,
        long trafficExpiryTime,
        long extensionExpiryTime)
    {
        if (clientExpiryTime != 0)
            return clientExpiryTime;
        if (trafficExpiryTime != 0)
            return trafficExpiryTime;
        return extensionExpiryTime;
    }

    /// <summary>
    /// Normalizes a positive Unix timestamp supplied in seconds or milliseconds while preserving first-use durations.
    /// </summary>
    /// <param name="value">
    /// Raw panel expiry value. Positive values below ten billion are treated as Unix seconds; negative values are
    /// first-use duration milliseconds and zero represents a missing or lifetime value.
    /// </param>
    /// <returns>Unix milliseconds, the original non-positive value, or zero when second-to-millisecond conversion overflows.</returns>
    private static long NormalizePanelTimestamp(long value)
    {
        if (value <= 0 || value >= 10_000_000_000L)
            return value;

        return value <= long.MaxValue / 1000L ? value * 1000L : 0;
    }

    /// <summary>
    /// Derives non-authoritative bot metadata evidence used only to decide whether an expired list row needs GET verification.
    /// </summary>
    /// <param name="metadata">Parsed bot-owned client metadata, or null for historical/manual accounts.</param>
    /// <returns>
    /// A future absolute expiry hint, a lifetime marker, or an empty result. The evidence never overrides a verified
    /// panel GET and is never used by the daily time-expiry reminder.
    /// </returns>
    private static XuiV3MetadataExpiryEvidence ResolveMetadataExpiryEvidence(XuiV3ClientMetadata metadata)
    {
        if (metadata == null)
            return new XuiV3MetadataExpiryEvidence();

        var latestRenewal = metadata.Renewals?
            .Where(item => item != null)
            .OrderByDescending(item => item.RenewedAtUtc)
            .FirstOrDefault();
        if (latestRenewal != null)
        {
            var renewedExpiry = NormalizePanelTimestamp(latestRenewal.ExpiryTimeAfter);
            return renewedExpiry == 0
                ? new XuiV3MetadataExpiryEvidence { IndicatesLifetime = true }
                : new XuiV3MetadataExpiryEvidence { ExpectedExpiryTime = renewedExpiry };
        }

        if (metadata.DurationDays > 0 && metadata.CreatedAtUtc > DateTime.UnixEpoch)
        {
            try
            {
                return new XuiV3MetadataExpiryEvidence
                {
                    ExpectedExpiryTime = new DateTimeOffset(NormalizeUtc(metadata.CreatedAtUtc))
                        .AddDays(metadata.DurationDays)
                        .ToUnixTimeMilliseconds()
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return new XuiV3MetadataExpiryEvidence();
            }
        }

        var isExplicitLifetime = metadata.DurationDays == 0 &&
                                 !string.IsNullOrWhiteSpace(metadata.ServiceKey) &&
                                 !string.IsNullOrWhiteSpace(metadata.PlanKey);
        return new XuiV3MetadataExpiryEvidence { IndicatesLifetime = isExplicitLifetime };
    }

    /// <summary>
    /// Returns panel expiry values used for conservative all-sources expiry classification.
    /// </summary>
    /// <param name="snapshot">Normalized XUI client snapshot.</param>
    /// <returns>A small detached list containing no identifiers or secrets.</returns>
    private static IReadOnlyList<long> GetPresentExpiryValues(XuiV3ClientUsageSnapshot snapshot)
    {
        if (snapshot == null)
            return Array.Empty<long>();

        var values = new List<long>(3);
        if (snapshot.ClientExpirySourcePresent)
            values.Add(snapshot.ClientExpiryTime);
        if (snapshot.TrafficExpirySourcePresent)
            values.Add(snapshot.TrafficExpiryTime);
        if (snapshot.ExtensionExpirySourcePresent)
            values.Add(snapshot.ExtensionExpiryTime);
        return values.Distinct().ToArray();
    }

    /// <summary>
    /// Builds a sanitized per-source expiry and enablement summary for durable diagnostics.
    /// </summary>
    /// <param name="snapshot">Normalized XUI client snapshot; identifiers are deliberately ignored.</param>
    /// <param name="nowUnixMilliseconds">Current UTC Unix timestamp in milliseconds.</param>
    /// <returns>A bounded categorical summary safe for structured logs and users.db.</returns>
    private static string BuildExpirySummary(XuiV3ClientUsageSnapshot snapshot, long nowUnixMilliseconds)
    {
        if (snapshot == null)
            return "snapshot=missing";

        return string.Join(
            ';',
            $"clientExpiry={ClassifyExpirySource(snapshot.ClientExpirySourcePresent, snapshot.ClientExpiryTime, nowUnixMilliseconds)}",
            $"trafficExpiry={ClassifyExpirySource(snapshot.TrafficExpirySourcePresent, snapshot.TrafficExpiryTime, nowUnixMilliseconds)}",
            $"extensionExpiry={ClassifyExpirySource(snapshot.ExtensionExpirySourcePresent, snapshot.ExtensionExpiryTime, nowUnixMilliseconds)}",
            $"metadataExpiry={(snapshot.MetadataIndicatesLifetime ? "lifetime" : ClassifyExpirySource(snapshot.MetadataExpectedExpiryTime.HasValue, snapshot.MetadataExpectedExpiryTime ?? 0, nowUnixMilliseconds))}",
            $"clientEnabled={(snapshot.ClientEnabled ? 1 : 0)}",
            $"trafficEnabled={(snapshot.TrafficEnabled.HasValue ? snapshot.TrafficEnabled.Value ? "1" : "0" : "missing")}");
    }

    /// <summary>
    /// Converts one expiry value into a non-sensitive operational category.
    /// </summary>
    /// <param name="isPresent">Whether this source was represented by the normalized panel response.</param>
    /// <param name="value">Normalized expiry timestamp or first-use duration in milliseconds.</param>
    /// <param name="nowUnixMilliseconds">Current UTC Unix timestamp in milliseconds.</param>
    /// <returns><c>missing</c>, <c>lifetime</c>, <c>first_use</c>, <c>expired</c>, or <c>future</c>.</returns>
    private static string ClassifyExpirySource(bool isPresent, long value, long nowUnixMilliseconds)
        => !isPresent ? "missing" : value == 0 ? "lifetime" : value < 0 ? "first_use" : value <= nowUnixMilliseconds ? "expired" : "future";

    /// <summary>
    /// Normalizes a timestamp to UTC without applying a local-time conversion to unspecified metadata values.
    /// </summary>
    /// <param name="value">Timestamp obtained from the current scan or bot metadata.</param>
    /// <returns>A UTC-kind timestamp with the same clock value.</returns>
    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

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
    /// <param name="keys">Ordered case-sensitive JSON aliases such as <c>updatedAt</c>; empty aliases are ignored.</param>
    /// <returns>Parsed value, or zero when unavailable.</returns>
    private static long ReadLongExtra(XuiV3Client client, params string[] keys)
    {
        if (client?.Extra == null || keys == null || keys.Length == 0)
            return 0;

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                !client.Extra.TryGetValue(key, out var token) ||
                token == null ||
                token.Type == JTokenType.Null)
            {
                continue;
            }

            try
            {
                return token.ToObject<long>();
            }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidCastException or ArgumentException)
            {
                // Try another known alias before treating the extension value as unavailable.
            }
        }

        return 0;
    }

    /// <summary>
    /// Checks whether extension data contains at least one known expiry alias, including an explicit null/zero token.
    /// </summary>
    /// <param name="client">Panel client whose extension-data shape is being inspected.</param>
    /// <param name="keys">Case-sensitive JSON aliases; blank aliases are ignored.</param>
    /// <returns><c>true</c> when any alias is present in extension data; otherwise <c>false</c>.</returns>
    private static bool HasExtra(XuiV3Client client, params string[] keys)
        => client?.Extra != null &&
           keys?.Any(key => !string.IsNullOrWhiteSpace(key) && client.Extra.ContainsKey(key)) == true;
}

/// <summary>
/// Stable outcomes produced by detailed XUI volume-reminder eligibility evaluation.
/// </summary>
public enum XuiV3VolumeReminderEligibilityStatus
{
    /// <summary>The panel row has no positive finite traffic quota.</summary>
    InvalidQuota,
    /// <summary>The finite account has not reached the first configured 80-percent threshold.</summary>
    BelowThreshold,
    /// <summary>The current account state may proceed to recipient validation and an atomic Telegram claim.</summary>
    Eligible,
    /// <summary>Every present authoritative panel expiry source is definitively expired.</summary>
    TimeExpired,
    /// <summary>The client or traffic row is disabled before the final 99-percent threshold.</summary>
    DisabledBeforeFinalThreshold,
    /// <summary>The list response conflicts with still-current bot metadata and requires one GET-only verification.</summary>
    NeedsReadOnlyVerification,
    /// <summary>The read-only panel verification failed or returned no usable client body.</summary>
    ReadOnlyVerificationUnavailable,
    /// <summary>The read-only response did not match the numeric id and normalized email observed by the list scan.</summary>
    ReadOnlyVerificationIdentityMismatch
}

/// <summary>
/// Detailed, sanitized eligibility result for one XUI volume-reminder observation.
/// </summary>
public sealed class XuiV3VolumeReminderEligibilityResult
{
    /// <summary>Stable decision status used by the worker and durable diagnostic state.</summary>
    public XuiV3VolumeReminderEligibilityStatus Status { get; init; }
    /// <summary>Highest reached traffic threshold: 80, 90, 99, or zero.</summary>
    public int Threshold { get; init; }
    /// <summary>Sanitized per-source expiry and enablement categories with no account identifier or secret.</summary>
    public string Summary { get; init; } = string.Empty;
    /// <summary><c>true</c> only when recipient validation and delivery claiming may continue.</summary>
    public bool IsEligible => Status == XuiV3VolumeReminderEligibilityStatus.Eligible;
    /// <summary><c>true</c> only when the worker should perform a bounded identity-checked GET.</summary>
    public bool RequiresReadOnlyVerification => Status == XuiV3VolumeReminderEligibilityStatus.NeedsReadOnlyVerification;
    /// <summary>Lowercase stable persistence/logging code for <see cref="Status"/>.</summary>
    public string Code => Status switch
    {
        XuiV3VolumeReminderEligibilityStatus.InvalidQuota => "invalid_quota",
        XuiV3VolumeReminderEligibilityStatus.BelowThreshold => "below_threshold",
        XuiV3VolumeReminderEligibilityStatus.Eligible => "eligible",
        XuiV3VolumeReminderEligibilityStatus.TimeExpired => "time_expired",
        XuiV3VolumeReminderEligibilityStatus.DisabledBeforeFinalThreshold => "disabled_before_final",
        XuiV3VolumeReminderEligibilityStatus.NeedsReadOnlyVerification => "needs_readonly_verification",
        XuiV3VolumeReminderEligibilityStatus.ReadOnlyVerificationUnavailable => "verification_unavailable",
        XuiV3VolumeReminderEligibilityStatus.ReadOnlyVerificationIdentityMismatch => "verification_identity_mismatch",
        _ => "unknown"
    };

    /// <summary>
    /// Creates one immutable detailed eligibility result.
    /// </summary>
    /// <param name="status">Business outcome selected by the resolver.</param>
    /// <param name="threshold">Highest reached whole-percent threshold, or zero.</param>
    /// <param name="summary">Sanitized expiry/enablement summary; identifiers and secrets are forbidden.</param>
    /// <returns>A non-null detached result safe to persist and log.</returns>
    internal static XuiV3VolumeReminderEligibilityResult Create(
        XuiV3VolumeReminderEligibilityStatus status,
        int threshold,
        string summary)
        => new()
        {
            Status = status,
            Threshold = threshold,
            Summary = string.IsNullOrWhiteSpace(summary) ? "unavailable" : summary
        };
}

/// <summary>
/// Internal non-authoritative expiry hint parsed from bot metadata.
/// </summary>
internal sealed class XuiV3MetadataExpiryEvidence
{
    /// <summary>Expected absolute Unix-millisecond expiry, or null when metadata cannot prove one.</summary>
    public long? ExpectedExpiryTime { get; init; }
    /// <summary>Whether complete bot metadata explicitly describes a lifetime account.</summary>
    public bool IndicatesLifetime { get; init; }
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
    /// <summary>Normalized top-level client expiry from the panel, or zero when absent/lifetime.</summary>
    public long ClientExpiryTime { get; init; }
    /// <summary>
    /// Whether the top-level expiry source participates in conservative expiry classification. The response model
    /// treats an omitted top-level value as zero/lifetime so absence cannot falsely prove time expiry.
    /// </summary>
    public bool ClientExpirySourcePresent { get; init; }
    /// <summary>Normalized nested traffic-row expiry from the panel, or zero when absent/lifetime.</summary>
    public long TrafficExpiryTime { get; init; }
    /// <summary>Whether the panel response included a nested traffic row whose expiry value can participate.</summary>
    public bool TrafficExpirySourcePresent { get; init; }
    /// <summary>Normalized legacy extension expiry alias, or zero when absent.</summary>
    public long ExtensionExpiryTime { get; init; }
    /// <summary>Whether extension data explicitly included a supported legacy expiry alias.</summary>
    public bool ExtensionExpirySourcePresent { get; init; }
    /// <summary>Non-authoritative expected expiry derived from creation/renewal metadata for GET-verification routing.</summary>
    public long? MetadataExpectedExpiryTime { get; init; }
    /// <summary>Whether complete metadata identifies a lifetime plan that should not be classified as expired from list data alone.</summary>
    public bool MetadataIndicatesLifetime { get; init; }
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
