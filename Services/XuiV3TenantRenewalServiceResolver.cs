using Adminbot.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Describes why an existing XUI client could or could not be mapped to one tenant renewal service.
/// </summary>
internal enum XuiV3TenantRenewalServiceResolutionStatus
{
    /// <summary>The client was mapped to one enabled catalog service.</summary>
    Resolved,

    /// <summary>The selected client no longer exists in the authoritative detail endpoint.</summary>
    ClientMissing,

    /// <summary>The read-only detail endpoint was unavailable and list metadata was insufficient.</summary>
    DetailUnavailable,

    /// <summary>The detail response did not represent the same email, UUID, or numeric client id.</summary>
    IdentityMismatch,

    /// <summary>The comment metadata named a service whose configured kind contradicted the metadata kind.</summary>
    MetadataConflict,

    /// <summary>The comment metadata named a service that is missing or disabled in the current catalog.</summary>
    ServiceUnavailable,

    /// <summary>No enabled catalog service matched either metadata or the legacy inbound/expiry fallback.</summary>
    OutsideActiveServices,

    /// <summary>
    /// A legacy client has no authoritative service metadata and matches more than one enabled service category.
    /// </summary>
    ServiceSelectionRequired
}

/// <summary>
/// Identifies the non-sensitive evidence used to classify a tenant renewal target.
/// </summary>
internal enum XuiV3TenantRenewalServiceResolutionSource
{
    /// <summary>No service evidence produced a successful result.</summary>
    None,

    /// <summary>Structured metadata from <c>GET /clients/get/{email}</c> selected the service.</summary>
    DetailMetadata,

    /// <summary>Structured metadata from the already-authorized list row selected the service.</summary>
    ListMetadata,

    /// <summary>A legacy national inbound uniquely selected the national metered service.</summary>
    LegacyNationalInbound,

    /// <summary>A legacy negative first-use expiry selected the unlimited service.</summary>
    LegacyUnlimitedExpiry,

    /// <summary>A single compatible legacy service was selected without relying on a normal-service default.</summary>
    LegacyOnlyCompatibleService
}

/// <summary>
/// Describes whether the authoritative per-email client detail read completed.
/// </summary>
internal enum XuiV3TenantClientDetailAvailability
{
    /// <summary>The detail endpoint returned one client payload.</summary>
    Available,

    /// <summary>A transient or non-authoritative failure prevented the detail payload from being read.</summary>
    Unavailable,

    /// <summary>The detail endpoint authoritatively reported that the email no longer exists.</summary>
    NotFound
}

/// <summary>
/// Carries a sanitized tenant renewal service decision without exposing account identifiers or raw panel metadata.
/// </summary>
internal sealed class XuiV3TenantRenewalServiceResolution
{
    /// <summary>Gets the outcome of metadata, identity, and legacy fallback validation.</summary>
    public XuiV3TenantRenewalServiceResolutionStatus Status { get; init; }

    /// <summary>Gets the evidence category used for a successful decision.</summary>
    public XuiV3TenantRenewalServiceResolutionSource Source { get; init; }

    /// <summary>
    /// Gets the enabled catalog service selected for renewal, or <c>null</c> when <see cref="Status" /> is not resolved.
    /// </summary>
    public XuiV3ServiceDefinition Service { get; init; }

    /// <summary>
    /// Gets the raw comment whose successfully parsed metadata selected the service, or <c>null</c> for legacy inference.
    /// </summary>
    /// <remarks>
    /// This value may contain Telegram ids and operational metadata. It is intended only to enrich the detached client
    /// passed to the renewal policy and must never be written to application logs or shown directly to a customer.
    /// </remarks>
    public string AuthoritativeComment { get; init; }

    /// <summary>
    /// Gets the enabled services compatible with an identity-verified legacy client when customer selection is required.
    /// </summary>
    /// <remarks>
    /// The collection contains catalog definitions only and never account identifiers. Tenant callers must additionally
    /// enforce storefront visibility before rendering or accepting a choice. An empty collection means no manual choice
    /// is available.
    /// </remarks>
    public IReadOnlyList<XuiV3ServiceDefinition> CandidateServices { get; init; } =
        Array.Empty<XuiV3ServiceDefinition>();

    /// <summary>Gets whether the client may continue through tenant renewal using <see cref="Service" />.</summary>
    public bool Success => Status == XuiV3TenantRenewalServiceResolutionStatus.Resolved && Service != null;

    /// <summary>Gets whether an identity-verified legacy client needs an explicit compatible service choice.</summary>
    public bool RequiresCustomerSelection =>
        Status == XuiV3TenantRenewalServiceResolutionStatus.ServiceSelectionRequired &&
        CandidateServices.Count > 0;
}

/// <summary>
/// Resolves the tenant renewal service of an existing XUI client from structured comment metadata first.
/// </summary>
/// <remarks>
/// The resolver is side-effect free. It never calls XUI, writes tenant state, creates an order, or performs a financial
/// operation. The caller supplies the already-authorized list row and, when available, the newer per-email detail row.
/// Normal and unlimited services can share inbound ids, so a successful detail comment is authoritative. Legacy
/// inference is used only when no readable metadata exists after a successful detail lookup.
/// </remarks>
internal static class XuiV3TenantRenewalServiceResolver
{
    /// <summary>
    /// Maps one exact XUI client to an enabled service while keeping metadata authority separate from legacy inference.
    /// </summary>
    /// <param name="listClient">
    /// The detached client selected from the authenticated full client list. It must already be authorized for renewal;
    /// its email, UUID, SubId, and raw comment are sensitive and must never be logged by this method's caller.
    /// </param>
    /// <param name="detailClient">
    /// The detached payload returned by the read-only per-email endpoint, or <c>null</c> when the lookup was unavailable
    /// or authoritatively not found. When present, it must identify the same physical client as <paramref name="listClient" />.
    /// </param>
    /// <param name="detailAvailability">
    /// Whether the per-email lookup returned a payload, failed transiently, or authoritatively reported no client.
    /// </param>
    /// <param name="services">
    /// Current global XUI service catalog. Disabled entries are ignored and metadata that explicitly names one is rejected.
    /// </param>
    /// <returns>
    /// A detached sanitized decision. A successful result contains one enabled service; failures contain no account
    /// identifiers and must stop tenant order, provider, wallet, and XUI side effects.
    /// </returns>
    /// <remarks>
    /// When the detail lookup is unavailable, readable list metadata may still produce a safe result; legacy fallback is
    /// not allowed because a transient network failure must not turn an active unlimited client into a normal client.
    /// After a successful detail lookup with no readable metadata, national inbound and negative first-use expiry remain
    /// deterministic. A positive/zero-expiry client that matches both normal and unlimited services is returned as an
    /// explicit service-selection decision instead of being silently classified as normal.
    /// </remarks>
    /// <example>
    /// <code>
    /// var resolution = XuiV3TenantRenewalServiceResolver.Resolve(
    ///     listClient,
    ///     detailClient,
    ///     XuiV3TenantClientDetailAvailability.Available,
    ///     catalog.Services);
    /// </code>
    /// </example>
    public static XuiV3TenantRenewalServiceResolution Resolve(
        XuiV3Client listClient,
        XuiV3Client detailClient,
        XuiV3TenantClientDetailAvailability detailAvailability,
        IEnumerable<XuiV3ServiceDefinition> services)
    {
        if (listClient == null)
            return Failure(XuiV3TenantRenewalServiceResolutionStatus.ClientMissing);

        var enabledServices = services?
            .Where(service => service?.IsEnabled == true)
            .ToList() ?? new List<XuiV3ServiceDefinition>();

        if (detailAvailability == XuiV3TenantClientDetailAvailability.NotFound)
            return Failure(XuiV3TenantRenewalServiceResolutionStatus.ClientMissing);

        if (detailAvailability == XuiV3TenantClientDetailAvailability.Available)
        {
            if (detailClient == null || !RepresentsSameClient(listClient, detailClient))
                return Failure(XuiV3TenantRenewalServiceResolutionStatus.IdentityMismatch);

            var detailMetadata = TryReadMetadata(detailClient.Comment);
            if (detailMetadata != null)
                return ResolveMetadata(
                    detailMetadata,
                    enabledServices,
                    XuiV3TenantRenewalServiceResolutionSource.DetailMetadata,
                    detailClient.Comment);
        }

        var listMetadata = TryReadMetadata(listClient.Comment);
        if (listMetadata != null)
            return ResolveMetadata(
                listMetadata,
                enabledServices,
                XuiV3TenantRenewalServiceResolutionSource.ListMetadata,
                listClient.Comment);

        if (detailAvailability != XuiV3TenantClientDetailAvailability.Available)
            return Failure(XuiV3TenantRenewalServiceResolutionStatus.DetailUnavailable);

        return ResolveLegacy(listClient, detailClient, enabledServices);
    }

    /// <summary>
    /// Checks whether two detached panel payloads identify the same physical XUI client.
    /// </summary>
    /// <param name="listClient">Authorized client-list row selected by the renewal target resolver.</param>
    /// <param name="detailClient">Per-email detail row that may provide the authoritative comment.</param>
    /// <returns>
    /// <c>true</c> when normalized email matches and every available UUID/numeric id pair agrees; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A UUID present on only one side is treated as a mismatch. This prevents a reused email or incomplete unrelated
    /// detail response from supplying metadata for another account. Numeric ids are compared when both endpoints expose them.
    /// </remarks>
    public static bool RepresentsSameClient(XuiV3Client listClient, XuiV3Client detailClient)
    {
        if (listClient == null || detailClient == null ||
            string.IsNullOrWhiteSpace(listClient.Email) || string.IsNullOrWhiteSpace(detailClient.Email) ||
            !string.Equals(listClient.Email.Trim(), detailClient.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var listUuid = NormalizeUuid(listClient.Uuid);
        var detailUuid = NormalizeUuid(detailClient.Uuid);
        if (!string.IsNullOrEmpty(listUuid) || !string.IsNullOrEmpty(detailUuid))
        {
            if (string.IsNullOrEmpty(listUuid) || string.IsNullOrEmpty(detailUuid) ||
                !string.Equals(listUuid, detailUuid, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return listClient.Id <= 0 || detailClient.Id <= 0 || listClient.Id == detailClient.Id;
    }

    /// <summary>
    /// Resolves an explicit metadata service and validates its configured kind.
    /// </summary>
    /// <param name="metadata">Parsed JSON metadata from a panel-controlled client comment.</param>
    /// <param name="enabledServices">Current enabled service definitions.</param>
    /// <param name="source">Whether the metadata came from the detail endpoint or list row.</param>
    /// <param name="authoritativeComment">
    /// Raw JSON comment corresponding to <paramref name="metadata" />. It is returned for in-memory renewal enrichment
    /// and must never be logged or rendered directly.
    /// </param>
    /// <returns>A successful service decision or a fail-closed metadata status.</returns>
    /// <remarks>
    /// An explicit service key never falls back to inbound heuristics. This ensures that disabling or removing a service
    /// cannot silently reinterpret its accounts as normal and expose a payable renewal under another category.
    /// </remarks>
    private static XuiV3TenantRenewalServiceResolution ResolveMetadata(
        XuiV3ClientMetadata metadata,
        IReadOnlyCollection<XuiV3ServiceDefinition> enabledServices,
        XuiV3TenantRenewalServiceResolutionSource source,
        string authoritativeComment)
    {
        var serviceKey = metadata?.ServiceKey?.Trim();
        XuiV3ServiceDefinition service = null;
        if (!string.IsNullOrWhiteSpace(serviceKey))
        {
            service = enabledServices.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, serviceKey, StringComparison.OrdinalIgnoreCase));
            if (service == null)
                return Failure(XuiV3TenantRenewalServiceResolutionStatus.ServiceUnavailable);
        }
        else if (string.Equals(metadata?.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase))
        {
            var unlimitedMatches = enabledServices.Where(candidate => candidate.IsUnlimited).Take(2).ToList();
            if (unlimitedMatches.Count != 1)
                return Failure(XuiV3TenantRenewalServiceResolutionStatus.ServiceUnavailable);
            service = unlimitedMatches[0];
        }
        else
        {
            return Failure(XuiV3TenantRenewalServiceResolutionStatus.ServiceUnavailable);
        }

        if (!string.IsNullOrWhiteSpace(metadata.ServiceKind) &&
            !string.Equals(service.Kind, metadata.ServiceKind.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Failure(XuiV3TenantRenewalServiceResolutionStatus.MetadataConflict);
        }

        return Success(service, source, authoritativeComment);
    }

    /// <summary>
    /// Applies the backward-compatible service inference used only by clients without readable bot metadata.
    /// </summary>
    /// <param name="listClient">Authorized list row supplying inbound attachments and fallback expiry.</param>
    /// <param name="detailClient">Identity-verified detail row supplying the freshest expiry and attachments.</param>
    /// <param name="enabledServices">Current enabled catalog services.</param>
    /// <returns>A legacy service decision or an outside-active-services failure.</returns>
    /// <remarks>
    /// Positive/zero expiry cannot distinguish an unlimited account after first connection from a metered account when
    /// both share inbounds. Such clients return compatible candidates and require a separately persisted customer choice;
    /// quota, duration, free-form comment, and plan-name heuristics are deliberately not trusted.
    /// </remarks>
    private static XuiV3TenantRenewalServiceResolution ResolveLegacy(
        XuiV3Client listClient,
        XuiV3Client detailClient,
        IReadOnlyCollection<XuiV3ServiceDefinition> enabledServices)
    {
        var inboundIds = GetInboundIds(listClient, detailClient);
        if (inboundIds.Count == 0)
            return Failure(XuiV3TenantRenewalServiceResolutionStatus.OutsideActiveServices);

        var national = enabledServices.FirstOrDefault(service =>
            IsNationalService(service) && HasAnyInbound(service, inboundIds));
        if (national != null)
            return Success(national, XuiV3TenantRenewalServiceResolutionSource.LegacyNationalInbound);

        var expiryTime = GetExpiryTime(detailClient);
        if (expiryTime == 0)
            expiryTime = GetExpiryTime(listClient);
        if (expiryTime < 0)
        {
            var unlimited = enabledServices.FirstOrDefault(service =>
                service.IsUnlimited && HasAnyInbound(service, inboundIds));
            if (unlimited != null)
                return Success(unlimited, XuiV3TenantRenewalServiceResolutionSource.LegacyUnlimitedExpiry);
        }

        var compatibleServices = enabledServices
            .Where(service => HasAnyInbound(service, inboundIds))
            .Where(service => !IsNationalService(service))
            .DistinctBy(service => service.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (compatibleServices.Count == 1)
        {
            return Success(
                compatibleServices[0],
                XuiV3TenantRenewalServiceResolutionSource.LegacyOnlyCompatibleService);
        }

        if (compatibleServices.Count > 1)
        {
            return SelectionRequired(compatibleServices);
        }

        return Failure(XuiV3TenantRenewalServiceResolutionStatus.OutsideActiveServices);
    }

    /// <summary>
    /// Reads structured bot metadata from a client comment without surfacing malformed JSON.
    /// </summary>
    /// <param name="comment">Raw panel comment. It may be empty, legacy text, or JSON and must never be logged.</param>
    /// <returns>Parsed metadata, or <c>null</c> when no usable JSON object exists.</returns>
    private static XuiV3ClientMetadata TryReadMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            var metadata = JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
            return !string.IsNullOrWhiteSpace(metadata?.ServiceKey) ||
                   !string.IsNullOrWhiteSpace(metadata?.ServiceKind)
                ? metadata
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Combines positive inbound ids from the authorized list and identity-verified detail payloads.
    /// </summary>
    /// <param name="clients">Detached XUI clients for the same physical account.</param>
    /// <returns>A distinct positive inbound-id set; the collection may be empty.</returns>
    private static HashSet<int> GetInboundIds(params XuiV3Client[] clients)
    {
        var result = new HashSet<int>();
        foreach (var client in clients ?? Array.Empty<XuiV3Client>())
        {
            if (client?.InboundIds == null)
                continue;
            foreach (var inboundId in client.InboundIds.Where(id => id > 0))
                result.Add(inboundId);
        }

        return result;
    }

    /// <summary>
    /// Gets an expiry value from typed and extension-data shapes used by supported 3x-ui versions.
    /// </summary>
    /// <param name="client">Detached client payload; null is treated as lifetime/unknown zero.</param>
    /// <returns>Unix milliseconds, a negative first-use duration, or zero when absent.</returns>
    private static long GetExpiryTime(XuiV3Client client)
    {
        if (client == null)
            return 0;
        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        foreach (var key in new[] { "expiryTime", "expiry_time", "expiry" })
        {
            if (client.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null)
                continue;
            if (token.Type == JTokenType.Integer)
                return token.Value<long>();
            if (long.TryParse(token.ToString(), out var value))
                return value;
        }

        return 0;
    }

    /// <summary>Checks whether a service is the configured national metered category.</summary>
    /// <param name="service">Enabled catalog service.</param>
    /// <returns><c>true</c> when its key or profile identifies national traffic.</returns>
    private static bool IsNationalService(XuiV3ServiceDefinition service)
    {
        return string.Equals(service?.Key, "national", StringComparison.OrdinalIgnoreCase) ||
               (service?.InboundProfileKeys?.Any(key => string.Equals(key, "national", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>Checks whether a service is the configured normal metered category.</summary>
    /// <param name="service">Enabled catalog service.</param>
    /// <returns><c>true</c> when its key or profile identifies normal traffic.</returns>
    private static bool IsNormalService(XuiV3ServiceDefinition service)
    {
        return string.Equals(service?.Key, "normal", StringComparison.OrdinalIgnoreCase) ||
               (service?.InboundProfileKeys?.Any(key => string.Equals(key, "normal", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>Checks whether a service shares at least one positive inbound id with a legacy client.</summary>
    /// <param name="service">Enabled catalog service whose inbound membership is examined.</param>
    /// <param name="inboundIds">Distinct positive client inbound ids.</param>
    /// <returns><c>true</c> when at least one configured inbound is present.</returns>
    private static bool HasAnyInbound(XuiV3ServiceDefinition service, IReadOnlySet<int> inboundIds)
    {
        return service?.InboundIds?.Any(inboundIds.Contains) == true;
    }

    /// <summary>Normalizes a protocol UUID for ordinal identity comparison.</summary>
    /// <param name="value">Raw UUID from an authenticated panel response; it must never be logged.</param>
    /// <returns>Canonical lowercase GUID text, or an empty string for absent/invalid values.</returns>
    private static string NormalizeUuid(string value)
    {
        return Guid.TryParse(value?.Trim(), out var uuid)
            ? uuid.ToString("D").ToLowerInvariant()
            : string.Empty;
    }

    /// <summary>Creates a successful sanitized resolution.</summary>
    /// <param name="service">Enabled service selected for tenant renewal.</param>
    /// <param name="source">Evidence category that selected the service.</param>
    /// <param name="authoritativeComment">
    /// Optional raw JSON metadata comment retained only for detached in-memory renewal calculation.
    /// </param>
    /// <returns>A successful detached result.</returns>
    private static XuiV3TenantRenewalServiceResolution Success(
        XuiV3ServiceDefinition service,
        XuiV3TenantRenewalServiceResolutionSource source,
        string authoritativeComment = null)
    {
        return new XuiV3TenantRenewalServiceResolution
        {
            Status = XuiV3TenantRenewalServiceResolutionStatus.Resolved,
            Source = source,
            Service = service,
            AuthoritativeComment = authoritativeComment
        };
    }

    /// <summary>Creates a failed sanitized resolution without account identifiers.</summary>
    /// <param name="status">Definitive or temporary reason tenant renewal must stop.</param>
    /// <returns>A detached failure result with no service.</returns>
    private static XuiV3TenantRenewalServiceResolution Failure(XuiV3TenantRenewalServiceResolutionStatus status)
    {
        return new XuiV3TenantRenewalServiceResolution
        {
            Status = status,
            Source = XuiV3TenantRenewalServiceResolutionSource.None
        };
    }

    /// <summary>Creates a sanitized legacy-category decision that requires explicit customer selection.</summary>
    /// <param name="candidateServices">
    /// Enabled catalog services whose inbound membership matches the same identity-verified client. The collection must
    /// not contain account data and may include services that the tenant layer subsequently hides from its storefront.
    /// </param>
    /// <returns>
    /// A non-successful resolution carrying distinct compatible service definitions and no account identifiers or raw
    /// metadata.
    /// </returns>
    /// <remarks>
    /// This method has no persistence, Telegram, payment, wallet, provider, or XUI side effects. A caller may continue
    /// only after it stores an explicit tenant-visible choice and revalidates it against a fresh panel read.
    /// </remarks>
    private static XuiV3TenantRenewalServiceResolution SelectionRequired(
        IEnumerable<XuiV3ServiceDefinition> candidateServices)
    {
        return new XuiV3TenantRenewalServiceResolution
        {
            Status = XuiV3TenantRenewalServiceResolutionStatus.ServiceSelectionRequired,
            Source = XuiV3TenantRenewalServiceResolutionSource.None,
            CandidateServices = candidateServices?
                .Where(service => service != null)
                .DistinctBy(service => service.Key, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<XuiV3ServiceDefinition>()
        };
    }
}
