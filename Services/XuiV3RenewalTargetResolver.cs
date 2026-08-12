/// <summary>
/// Describes the outcome of resolving one parsed renewal identifier against the current XUI client list.
/// </summary>
public enum XuiV3RenewalTargetResolutionStatus
{
    /// <summary>Exactly one client matched and supplied a canonical UUID suitable for durable target locking.</summary>
    Success,

    /// <summary>The input could not be parsed as one of the supported renewal identifiers.</summary>
    InvalidInput,

    /// <summary>No current panel client matched the exact parsed identifier.</summary>
    NotFound,

    /// <summary>More than one distinct panel client matched, so choosing either would be unsafe.</summary>
    Ambiguous,

    /// <summary>The matched panel row had no valid UUID and therefore could not be safely locked through payment.</summary>
    MissingCanonicalUuid
}

/// <summary>
/// Returns the unique XUI client and panel-derived identity lock for a renewal lookup.
/// </summary>
/// <remarks>This object contains sensitive identity data and must not be serialized into callbacks or logs.</remarks>
public sealed class XuiV3RenewalTargetResolution
{
    /// <summary>Gets the safe resolution status.</summary>
    public XuiV3RenewalTargetResolutionStatus Status { get; init; }

    /// <summary>
    /// Gets the unique matched detached panel client on success or <see cref="XuiV3RenewalTargetResolutionStatus.MissingCanonicalUuid"/>;
    /// otherwise <c>null</c>.
    /// </summary>
    public XuiV3Client Client { get; init; }

    /// <summary>Gets the canonical panel UUID used to lock the target through preview and settlement.</summary>
    public string CanonicalUuid { get; init; } = string.Empty;

    /// <summary>Gets the parsed input kind for safe operational metrics without exposing its value.</summary>
    public XuiV3RenewalTargetInputKind? InputKind { get; init; }

    /// <summary>Gets whether exactly one safely lockable client was resolved.</summary>
    public bool Success => Status == XuiV3RenewalTargetResolutionStatus.Success;
}

/// <summary>
/// Resolves renewal identifiers against one fresh XUI client-list snapshot without applying ownership filtering.
/// </summary>
/// <remarks>
/// Successful resolution requires exactly one distinct client and a canonical panel UUID. A unique UUID-less row is
/// returned with <see cref="XuiV3RenewalTargetResolutionStatus.MissingCanonicalUuid"/> so callers may preserve the
/// owner-checked legacy path but must reject a non-owned target. Resolution itself never applies ownership filtering.
/// </remarks>
public static class XuiV3RenewalTargetResolver
{
    /// <summary>
    /// Parses and resolves one untrusted renewal identifier against current panel clients.
    /// </summary>
    /// <param name="clients">
    /// Detached clients returned by one authenticated <c>clients/list</c> request. Null entries are ignored.
    /// </param>
    /// <param name="input">
    /// Customer Telegram text containing an email, SubId/subscription URL, UUID, or supported configuration.
    /// </param>
    /// <returns>
    /// A status-only failure, a unique UUID-less legacy row, or one unique client with a canonical UUID. The result is
    /// not account-management authorization and must be revalidated before preview, payment, order, and fulfillment.
    /// </returns>
    /// <remarks>
    /// Email and SubId comparisons are ordinal-ignore-case; UUIDs are canonicalized as GUIDs; passwords remain
    /// ordinal and case-sensitive. Raw identifiers that match different clients as email and SubId are ambiguous.
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = XuiV3RenewalTargetResolver.Resolve(response.Obj, message.Text);
    /// if (!result.Success)
    ///     return;
    /// </code>
    /// </example>
    public static XuiV3RenewalTargetResolution Resolve(IEnumerable<XuiV3Client> clients, string input)
    {
        if (!XuiV3RenewalTargetParser.TryParse(input, out var parsed))
            return Failure(XuiV3RenewalTargetResolutionStatus.InvalidInput, null);

        var matchGroups = (clients ?? Array.Empty<XuiV3Client>())
            .Where(client => client != null && Matches(client, parsed))
            .GroupBy(GetStableClientKey, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        if (matchGroups.Count == 0)
            return Failure(XuiV3RenewalTargetResolutionStatus.NotFound, parsed.InputKind);
        if (matchGroups.Count > 1)
            return Failure(XuiV3RenewalTargetResolutionStatus.Ambiguous, parsed.InputKind);

        var group = matchGroups[0].ToList();
        var canonicalUuids = group
            .Select(client => Guid.TryParse(client.Uuid?.Trim(), out var uuid) && uuid != Guid.Empty
                ? uuid.ToString()
                : null)
            .Where(uuid => uuid != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        if (canonicalUuids.Count > 1)
            return Failure(XuiV3RenewalTargetResolutionStatus.Ambiguous, parsed.InputKind);

        var client = group[0];
        if (canonicalUuids.Count == 0)
        {
            return new XuiV3RenewalTargetResolution
            {
                Status = XuiV3RenewalTargetResolutionStatus.MissingCanonicalUuid,
                Client = client,
                InputKind = parsed.InputKind
            };
        }

        var canonicalUuid = canonicalUuids[0];
        client = group.First(candidate =>
            Guid.TryParse(candidate.Uuid?.Trim(), out var candidateUuid) &&
            string.Equals(candidateUuid.ToString(), canonicalUuid, StringComparison.OrdinalIgnoreCase));

        return new XuiV3RenewalTargetResolution
        {
            Status = XuiV3RenewalTargetResolutionStatus.Success,
            Client = client,
            CanonicalUuid = canonicalUuid,
            InputKind = parsed.InputKind
        };
    }

    /// <summary>
    /// Compares one client only against the trusted field selected by the parser.
    /// </summary>
    /// <param name="client">Current detached XUI client.</param>
    /// <param name="parsed">Normalized sensitive parser result.</param>
    /// <returns><c>true</c> when the selected panel field matches exactly; otherwise <c>false</c>.</returns>
    private static bool Matches(XuiV3Client client, XuiV3RenewalTargetInput parsed)
    {
        return parsed.CredentialKind switch
        {
            XuiV3RenewalTargetCredentialKind.Uuid =>
                (Guid.TryParse(client.Uuid?.Trim(), out var clientUuid) &&
                 Guid.TryParse(parsed.Value, out var inputUuid) &&
                 clientUuid != Guid.Empty &&
                 clientUuid == inputUuid) ||
                (parsed.InputKind == XuiV3RenewalTargetInputKind.Uuid &&
                 !string.IsNullOrWhiteSpace(client.SubId) &&
                 string.Equals(client.SubId.Trim().Trim('/'), parsed.Value.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase)),
            XuiV3RenewalTargetCredentialKind.Password =>
                !string.IsNullOrEmpty(client.Password) &&
                string.Equals(client.Password, parsed.Value, StringComparison.Ordinal),
            XuiV3RenewalTargetCredentialKind.SubscriptionId =>
                !string.IsNullOrWhiteSpace(client.SubId) &&
                string.Equals(client.SubId.Trim().Trim('/'), parsed.Value.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase),
            XuiV3RenewalTargetCredentialKind.EmailOrSubscriptionId =>
                (!string.IsNullOrWhiteSpace(client.Email) &&
                 string.Equals(client.Email.Trim(), parsed.Value.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(client.SubId) &&
                 string.Equals(client.SubId.Trim().Trim('/'), parsed.Value.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    /// <summary>
    /// Builds a stable distinctness key so one panel row cannot appear ambiguous merely because the API duplicated it.
    /// </summary>
    /// <param name="client">Matched detached panel client.</param>
    /// <returns>Numeric-id key when available; otherwise an email/UUID identity key.</returns>
    private static string GetStableClientKey(XuiV3Client client)
    {
        return client.Id > 0
            ? $"id:{client.Id}"
            : $"email:{client.Email?.Trim()}|uuid:{client.Uuid?.Trim()}";
    }

    /// <summary>
    /// Creates a failure result without retaining any sensitive lookup value.
    /// </summary>
    /// <param name="status">Non-success resolution status.</param>
    /// <param name="inputKind">Parsed input shape when available.</param>
    /// <returns>A result containing no client, UUID, email, SubId, password, or configuration text.</returns>
    private static XuiV3RenewalTargetResolution Failure(
        XuiV3RenewalTargetResolutionStatus status,
        XuiV3RenewalTargetInputKind? inputKind)
    {
        return new XuiV3RenewalTargetResolution
        {
            Status = status,
            InputKind = inputKind
        };
    }
}
