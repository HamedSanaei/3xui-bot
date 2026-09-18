using Adminbot.Domain;

/// <summary>
/// Builds the single configured XUI v3 panel descriptor used by read-only renewal verification.
/// </summary>
/// <remarks>
/// <para>
/// Background reconciliation and administrator manual-review re-checks must read the exact same panel with the exact
/// same credentials and root path. Centralizing the descriptor here prevents the two read-only paths from drifting
/// apart, which would make an administrator's re-check disagree with the worker's evidence for the same operation.
/// </para>
/// <para>
/// The returned descriptor carries the configured API token. It is never logged, never persisted, and never exposed to
/// Telegram; callers must use it only for authenticated panel requests.
/// </para>
/// </remarks>
internal static class XuiV3RenewalPanelDescriptor
{
    /// <summary>
    /// Creates the panel descriptor for one read-only renewal verification request.
    /// </summary>
    /// <param name="appConfig">
    /// Application configuration bound from the runtime configuration file. Must contain a non-empty
    /// <c>XuiV3ApiBaseUrl</c>; the token, root path, and subscription base are optional and normalize to empty.
    /// </param>
    /// <returns>
    /// A descriptor whose <c>Url</c> has no trailing slash and whose <c>RootPath</c> has no leading or trailing slash,
    /// matching the request shape the XUI v3 client expects.
    /// </returns>
    /// <remarks>
    /// The method performs no I/O and no validation beyond the base URL, because a caller that cannot build a descriptor
    /// must fail instead of silently reading an unrelated panel.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>XuiV3ApiBaseUrl</c> is missing, which means no renewal verification can be performed safely.
    /// </exception>
    /// <example>
    /// <code>var serverInfo = XuiV3RenewalPanelDescriptor.Build(appConfig);</code>
    /// </example>
    internal static ServerInfo Build(AppConfig appConfig)
    {
        if (string.IsNullOrWhiteSpace(appConfig?.XuiV3ApiBaseUrl))
            throw new InvalidOperationException("XuiV3ApiBaseUrl is not configured.");

        return new ServerInfo
        {
            ApiVersion = "v3",
            ApiToken = appConfig.XuiV3ApiToken,
            Url = appConfig.XuiV3ApiBaseUrl.TrimEnd('/'),
            RootPath = (appConfig.XuiV3ApiRootPath ?? string.Empty).Trim('/'),
            SubLinkUrl = string.IsNullOrWhiteSpace(appConfig.XuiV3SubLinkBaseUrl)
                ? null
                : appConfig.XuiV3SubLinkBaseUrl.TrimEnd('/'),
            Name = "Configured V3 Panel"
        };
    }
}
