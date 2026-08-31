using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

/// <summary>
/// Stable outcomes returned while resolving the customer-authored comment for an XUI reminder.
/// </summary>
internal enum XuiV3ReminderCommentResolutionStatus
{
    /// <summary>The identity-checked detail response was valid, whether or not it contained a user comment.</summary>
    Resolved,
    /// <summary>The read-only detail request failed or returned no usable client body.</summary>
    Unavailable,
    /// <summary>The detail response did not identify the same numeric client, email, or available UUID.</summary>
    IdentityMismatch,
    /// <summary>The panel comment was non-empty but was not valid bot-owned JSON metadata.</summary>
    InvalidMetadata
}

/// <summary>
/// Detached, log-safe result of resolving one reminder's customer-authored comment.
/// </summary>
internal sealed class XuiV3ReminderCommentResolution
{
    /// <summary>Outcome that determines whether the reminder may proceed.</summary>
    public XuiV3ReminderCommentResolutionStatus Status { get; init; }

    /// <summary>
    /// Normalized customer-authored comment, or an empty string when the verified metadata contained no comment.
    /// </summary>
    public string UserComment { get; init; } = string.Empty;

    /// <summary><c>true</c> only when the detail identity and metadata were both safe to use.</summary>
    public bool Success => Status == XuiV3ReminderCommentResolutionStatus.Resolved;
}

/// <summary>
/// Resolves the customer-authored comment used by time and volume expiration reminders.
/// </summary>
/// <remarks>
/// Only <see cref="XuiV3ClientMetadata.UserComment"/> is exposed. Raw panel comments, metadata JSON, UUIDs,
/// subscription ids, request URIs, tokens, and response bodies never appear in the returned result. The resolver
/// performs GET-only enrichment and never mutates XUI, reminder state, orders, payments, wallets, or Telegram state.
/// </remarks>
internal static class XuiV3ReminderCommentResolver
{
    private const int MaximumUserCommentLength = 200;

    /// <summary>
    /// Reads one current XUI client detail row and safely extracts its customer-authored reminder comment.
    /// </summary>
    /// <param name="serverInfo">
    /// Configured XUI v3 panel descriptor. Its API credentials are consumed by the existing transport and must not
    /// be logged or exposed to the reminder recipient.
    /// </param>
    /// <param name="configuration">Runtime configuration supplying XUI authentication, timeout, and read retry policy.</param>
    /// <param name="expectedClient">
    /// Client from the successful complete-list scan. It must have a positive numeric panel id and non-empty email;
    /// when either response exposes a UUID, both responses must expose the same UUID.
    /// </param>
    /// <param name="cancellationToken">Host shutdown token for the read-only panel request and its safe retries.</param>
    /// <returns>
    /// A detached result. <see cref="XuiV3ReminderCommentResolution.Success"/> is true for valid metadata even when
    /// no user comment was registered; callers must defer the reminder for every other status.
    /// </returns>
    /// <remarks>
    /// The GET is intentionally performed only after an account becomes due for a reminder. Transient transport or
    /// response failures are converted to <c>Unavailable</c> so one account can be retried without aborting the scan.
    /// Host cancellation is propagated.
    /// </remarks>
    /// <example>
    /// <code>
    /// var result = await XuiV3ReminderCommentResolver.ResolveAsync(
    ///     serverInfo,
    ///     configuration,
    ///     listClient,
    ///     cancellationToken);
    /// if (!result.Success)
    ///     return;
    /// </code>
    /// </example>
    public static async Task<XuiV3ReminderCommentResolution> ResolveAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        XuiV3Client expectedClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverInfo);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(expectedClient);

        if (expectedClient.Id <= 0 || string.IsNullOrWhiteSpace(expectedClient.Email))
            return Failure(XuiV3ReminderCommentResolutionStatus.IdentityMismatch);

        try
        {
            var response = await ApiServicev3.GetClientAsync(
                serverInfo,
                configuration,
                expectedClient.Email,
                cancellationToken,
                suppressIdentifierBearingRetryLogs: true);
            return !response.Success || response.Obj == null
                ? Failure(XuiV3ReminderCommentResolutionStatus.Unavailable)
                : ResolveVerifiedClient(expectedClient, response.Obj);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableReadFailure(ex))
        {
            return Failure(XuiV3ReminderCommentResolutionStatus.Unavailable);
        }
    }

    /// <summary>
    /// Extracts a user comment from an already-fetched detail response after checking client identity.
    /// </summary>
    /// <param name="expectedClient">
    /// Client from the complete-list scan that established reminder ownership, quota, and due status.
    /// </param>
    /// <param name="detailClient">
    /// Client returned by the current identity-specific GET. Its raw comment may contain sensitive bot metadata and
    /// must not be logged or displayed directly.
    /// </param>
    /// <returns>
    /// A resolved normalized comment, a resolved empty comment, or a safe failure status. The result contains no
    /// account identifier or raw metadata and is safe to include in categorical local diagnostics.
    /// </returns>
    /// <remarks>
    /// Volume-expiry verification uses this overload to reuse its existing GET response rather than requesting the
    /// same client twice in one scan. This method performs no I/O or state mutation.
    /// </remarks>
    /// <example>
    /// <code>
    /// var comment = XuiV3ReminderCommentResolver.ResolveVerifiedClient(listClient, detailClient);
    /// </code>
    /// </example>
    public static XuiV3ReminderCommentResolution ResolveVerifiedClient(
        XuiV3Client expectedClient,
        XuiV3Client detailClient)
    {
        if (!IsSameClient(expectedClient, detailClient))
            return Failure(XuiV3ReminderCommentResolutionStatus.IdentityMismatch);

        if (string.IsNullOrWhiteSpace(detailClient.Comment))
            return Success(string.Empty);

        XuiV3ClientMetadata metadata;
        try
        {
            metadata = JsonConvert.DeserializeObject<XuiV3ClientMetadata>(detailClient.Comment);
        }
        catch (JsonException)
        {
            return Failure(XuiV3ReminderCommentResolutionStatus.InvalidMetadata);
        }

        if (metadata == null)
            return Failure(XuiV3ReminderCommentResolutionStatus.InvalidMetadata);

        return Success(IsInternalTenantAuditComment(metadata.UserComment)
            ? string.Empty
            : NormalizeUserComment(metadata.UserComment));
    }

    /// <summary>
    /// Verifies that a detail GET still refers to the list client selected for the reminder.
    /// </summary>
    /// <param name="expectedClient">Complete-list client with numeric id, email, and optional UUID.</param>
    /// <param name="detailClient">Detail response that must represent the same panel client.</param>
    /// <returns>
    /// <c>true</c> when normalized email matches, UUID presence/value agrees, and numeric ids agree when the detail
    /// endpoint supplies one; otherwise <c>false</c>.
    /// </returns>
    private static bool IsSameClient(XuiV3Client expectedClient, XuiV3Client detailClient)
    {
        if (expectedClient == null || detailClient == null ||
            expectedClient.Id <= 0 ||
            !string.Equals(
                expectedClient.Email?.Trim(),
                detailClient.Email?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedUuid = NormalizeUuid(expectedClient.Uuid);
        var detailUuid = NormalizeUuid(detailClient.Uuid);
        if (!string.IsNullOrEmpty(expectedUuid) || !string.IsNullOrEmpty(detailUuid))
        {
            if (string.IsNullOrEmpty(expectedUuid) || string.IsNullOrEmpty(detailUuid) ||
                !string.Equals(expectedUuid, detailUuid, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return detailClient.Id <= 0 || detailClient.Id == expectedClient.Id;
    }

    /// <summary>
    /// Normalizes a panel UUID for an ordinal identity comparison without exposing it.
    /// </summary>
    /// <param name="value">Optional UUID returned by the panel list or detail endpoint.</param>
    /// <returns>A lowercase canonical UUID, trimmed fallback text, or an empty string.</returns>
    private static string NormalizeUuid(string value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        return Guid.TryParse(trimmed, out var parsed)
            ? parsed.ToString("D")
            : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Produces a one-line bounded user comment suitable for later HTML encoding by a message builder.
    /// </summary>
    /// <param name="value">Optional customer-authored comment from trusted bot metadata.</param>
    /// <returns>An empty string or at most 200 characters with line breaks replaced by spaces.</returns>
    private static string NormalizeUserComment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= MaximumUserCommentLength
            ? normalized
            : normalized[..(MaximumUserCommentLength - 3)] + "...";
    }

    /// <summary>
    /// Detects the legacy tenant-sale audit string that was stored in <c>UserComment</c> for internal traceability.
    /// </summary>
    /// <param name="value">Optional metadata user-comment value from an identity-checked detail response.</param>
    /// <returns>
    /// <c>true</c> only for the complete legacy pattern containing the tenant-sale prefix plus buyer and tenant ids;
    /// otherwise <c>false</c> so genuine customer comments remain visible.
    /// </returns>
    /// <remarks>
    /// The audit value is not a customer-authored comment and includes internal identifiers. It remains untouched in
    /// XUI metadata for operational history but is treated as absent in reminder messages.
    /// </remarks>
    private static bool IsInternalTenantAuditComment(string value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) &&
               trimmed.StartsWith("tenant sale VIA @", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("; Buyer=", StringComparison.Ordinal) &&
               trimmed.Contains("; tenant=", StringComparison.Ordinal);
    }

    /// <summary>
    /// Classifies exceptions that should defer one reminder rather than terminate the whole scan.
    /// </summary>
    /// <param name="exception">Exception raised by the read-only transport or JSON response handling.</param>
    /// <returns><c>true</c> for ordinary transient/shape failures; fatal process exceptions are excluded.</returns>
    private static bool IsRecoverableReadFailure(Exception exception)
        => exception is XuiV3ApiException or
           HttpRequestException or
           OperationCanceledException or
           TimeoutException or
           JsonException or
           ArgumentException or
           FormatException or
           OverflowException or
           InvalidCastException or
           InvalidOperationException;

    /// <summary>
    /// Creates a successful comment result without retaining raw metadata.
    /// </summary>
    /// <param name="userComment">Normalized user comment, which may be empty.</param>
    /// <returns>A resolved detached result.</returns>
    private static XuiV3ReminderCommentResolution Success(string userComment)
        => new()
        {
            Status = XuiV3ReminderCommentResolutionStatus.Resolved,
            UserComment = userComment ?? string.Empty
        };

    /// <summary>
    /// Creates a non-sensitive failed resolution.
    /// </summary>
    /// <param name="status">Failure category; <c>Resolved</c> is not valid here.</param>
    /// <returns>A detached failure result containing no panel or account data.</returns>
    private static XuiV3ReminderCommentResolution Failure(
        XuiV3ReminderCommentResolutionStatus status)
        => new() { Status = status };
}
