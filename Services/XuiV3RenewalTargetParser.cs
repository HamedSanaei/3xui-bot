using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

/// <summary>
/// Identifies the customer-supplied shape used to locate one XUI v3 renewal target.
/// </summary>
public enum XuiV3RenewalTargetInputKind
{
    /// <summary>A raw value that may be either an exact account email or an exact subscription id.</summary>
    EmailOrSubscriptionId,

    /// <summary>An HTTP or HTTPS subscription URL whose final path segment is the subscription id.</summary>
    SubscriptionLink,

    /// <summary>A complete canonical account UUID supplied without a configuration wrapper.</summary>
    Uuid,

    /// <summary>A supported proxy configuration whose embedded UUID or password identifies the client.</summary>
    Configuration
}

/// <summary>
/// Specifies which trusted panel identity field may match a parsed renewal value.
/// </summary>
public enum XuiV3RenewalTargetCredentialKind
{
    /// <summary>The value must match the panel client's UUID.</summary>
    Uuid,

    /// <summary>The value must match the panel client's protocol password.</summary>
    Password,

    /// <summary>The raw value may match either email or subscription id.</summary>
    EmailOrSubscriptionId,

    /// <summary>The value comes from a subscription URL and must match subscription id only.</summary>
    SubscriptionId
}

/// <summary>
/// Contains one normalized, non-authorizing renewal identifier parsed from untrusted Telegram text.
/// </summary>
/// <remarks>
/// Values carried by this type may be account credentials. They must never be written to callbacks, Telegram error
/// messages, or operational logs. Parsing does not grant renewal or account-management access.
/// </remarks>
public sealed class XuiV3RenewalTargetInput
{
    /// <summary>Gets the customer input shape used for safe diagnostics and result handling.</summary>
    public XuiV3RenewalTargetInputKind InputKind { get; init; }

    /// <summary>Gets the trusted panel field against which <see cref="Value"/> may be compared.</summary>
    public XuiV3RenewalTargetCredentialKind CredentialKind { get; init; }

    /// <summary>
    /// Gets the normalized sensitive lookup value. The value is required and must never be logged or transported in a callback.
    /// </summary>
    public string Value { get; init; }
}

/// <summary>
/// Parses exact email, SubId/subscription-link, UUID, and supported proxy-configuration inputs for renewal lookup.
/// </summary>
/// <remarks>
/// Supported configurations are VLESS, VMess, Trojan, Shadowsocks, Hysteria, Hysteria2, and Hy2. Only an embedded
/// protocol credential is trusted. URI hosts, fragments, display labels, and unrelated query/comment values are
/// deliberately ignored; Hysteria's documented <c>auth</c>, <c>auth_str</c>, and <c>password</c> parameters are treated
/// as password credentials when user-info is absent.
/// </remarks>
public static class XuiV3RenewalTargetParser
{
    private static readonly Regex EmbeddedUuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses one customer-supplied renewal identifier without performing a panel request or authorization check.
    /// </summary>
    /// <param name="input">
    /// Untrusted Telegram text containing an exact email, raw SubId, HTTP(S) subscription URL, UUID, or supported
    /// configuration. Null, empty, command-like, whitespace-containing raw identifiers, and malformed links are rejected.
    /// </param>
    /// <param name="target">
    /// Normalized sensitive identifier when parsing succeeds; otherwise <c>null</c>. Callers must not log its value.
    /// </param>
    /// <returns><c>true</c> when a supported exact identifier was parsed; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Parsing is side-effect free. A successful result only provides lookup material; callers must require one unique
    /// panel client and persist a fresh panel-derived UUID before permitting a renewal.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (XuiV3RenewalTargetParser.TryParse(message.Text, out var input))
    ///     resolution = XuiV3RenewalTargetResolver.Resolve(clients, input);
    /// </code>
    /// </example>
    public static bool TryParse(string input, out XuiV3RenewalTargetInput target)
    {
        target = null;
        var value = input?.Trim().Trim('`');
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("/", StringComparison.Ordinal))
            return false;

        if (Guid.TryParse(value, out var rawUuid) && rawUuid != Guid.Empty)
        {
            target = BuildTarget(
                XuiV3RenewalTargetInputKind.Uuid,
                XuiV3RenewalTargetCredentialKind.Uuid,
                rawUuid.ToString());
            return true;
        }

        if (TryParseConfiguration(value, out target))
            return true;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            var segment = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(segment))
                return false;

            target = BuildTarget(
                XuiV3RenewalTargetInputKind.SubscriptionLink,
                XuiV3RenewalTargetCredentialKind.SubscriptionId,
                segment.Trim());
            return true;
        }

        if (value.Contains("://", StringComparison.Ordinal))
            return false;

        if (value.Any(char.IsWhiteSpace) || value.Contains("\r", StringComparison.Ordinal) || value.Contains("\n", StringComparison.Ordinal))
            return false;

        target = BuildTarget(
            XuiV3RenewalTargetInputKind.EmailOrSubscriptionId,
            XuiV3RenewalTargetCredentialKind.EmailOrSubscriptionId,
            value.Trim('/'));
        return !string.IsNullOrWhiteSpace(target.Value);
    }

    /// <summary>
    /// Attempts to extract and normalize a UUID from legacy UUID-bearing renewal text.
    /// </summary>
    /// <param name="input">Raw UUID, VMess configuration, or text/configuration containing a UUID.</param>
    /// <param name="uuid">Canonical dashed UUID when parsing succeeds; otherwise an empty string.</param>
    /// <returns><c>true</c> when a non-empty GUID was safely extracted; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// This compatibility API remains available to existing account-search callbacks. New renewal entry flows should
    /// use <see cref="TryParse"/> so email, SubId, and password-based configurations receive the same strict handling.
    /// The returned UUID is sensitive and must never be logged.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (XuiV3RenewalTargetParser.TryExtractUuid(config, out var uuid))
    ///     client = clients.SingleOrDefault(x => x.Uuid == uuid);
    /// </code>
    /// </example>
    public static bool TryExtractUuid(string input, out string uuid)
    {
        uuid = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (TryParse(input, out var parsed) &&
            parsed.CredentialKind == XuiV3RenewalTargetCredentialKind.Uuid &&
            Guid.TryParse(parsed.Value, out var parsedUuid) &&
            parsedUuid != Guid.Empty)
        {
            uuid = parsedUuid.ToString();
            return true;
        }

        var match = EmbeddedUuidRegex.Match(input);
        if (!match.Success || !Guid.TryParse(match.Value, out var embeddedUuid) || embeddedUuid == Guid.Empty)
            return false;

        uuid = embeddedUuid.ToString();
        return true;
    }

    /// <summary>
    /// Parses a supported proxy configuration and extracts only its authentication credential.
    /// </summary>
    /// <param name="value">Trimmed customer input that may be a supported configuration URI.</param>
    /// <param name="target">Parsed UUID/password target, or <c>null</c> when the URI is unsupported or malformed.</param>
    /// <returns><c>true</c> when a supported configuration credential was extracted; otherwise <c>false</c>.</returns>
    /// <remarks>URI labels, hosts, ports, fragments, and query parameters never participate in matching.</remarks>
    private static bool TryParseConfiguration(string value, out XuiV3RenewalTargetInput target)
    {
        target = null;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        var scheme = value[..separator];
        if (scheme.Equals("vmess", StringComparison.OrdinalIgnoreCase))
            return TryParseVmess(value, out target);
        if (scheme.Equals("ss", StringComparison.OrdinalIgnoreCase))
            return TryParseShadowsocks(value, out target);

        var passwordScheme = scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase) ||
                             scheme.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
                             scheme.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
                             scheme.Equals("hy2", StringComparison.OrdinalIgnoreCase);
        if (!passwordScheme && !scheme.Equals("vless", StringComparison.OrdinalIgnoreCase))
            return false;

        var credential = ExtractUriUserInfo(value[(separator + 3)..]);
        if (string.IsNullOrWhiteSpace(credential) && passwordScheme)
            credential = ExtractKnownPasswordQueryParameter(value);
        if (string.IsNullOrWhiteSpace(credential))
            return false;

        if (Guid.TryParse(credential, out var uuid) && uuid != Guid.Empty)
        {
            target = BuildTarget(
                XuiV3RenewalTargetInputKind.Configuration,
                XuiV3RenewalTargetCredentialKind.Uuid,
                uuid.ToString());
            return true;
        }

        if (!passwordScheme)
            return false;

        target = BuildTarget(
            XuiV3RenewalTargetInputKind.Configuration,
            XuiV3RenewalTargetCredentialKind.Password,
            credential);
        return true;
    }

    /// <summary>
    /// Decodes a VMess configuration and reads its exact JSON <c>id</c> credential.
    /// </summary>
    /// <param name="value">Complete VMess URI received from Telegram.</param>
    /// <param name="target">Canonical UUID target when decoding succeeds; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when the decoded payload contains one valid non-empty UUID; otherwise <c>false</c>.</returns>
    private static bool TryParseVmess(string value, out XuiV3RenewalTargetInput target)
    {
        target = null;
        try
        {
            var encoded = value["vmess://".Length..].Trim();
            if (!TryDecodeBase64(encoded, out var bytes))
                return false;

            var token = JObject.Parse(Encoding.UTF8.GetString(bytes));
            if (!Guid.TryParse(token["id"]?.ToString(), out var uuid) || uuid == Guid.Empty)
                return false;

            target = BuildTarget(
                XuiV3RenewalTargetInputKind.Configuration,
                XuiV3RenewalTargetCredentialKind.Uuid,
                uuid.ToString());
            return true;
        }
        catch (Exception) when (value != null)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes SIP002 and legacy Shadowsocks URI forms and extracts only the password portion.
    /// </summary>
    /// <param name="value">Complete Shadowsocks URI received from Telegram.</param>
    /// <param name="target">Password-based target when parsing succeeds; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a non-empty method/password credential was decoded; otherwise <c>false</c>.</returns>
    private static bool TryParseShadowsocks(string value, out XuiV3RenewalTargetInput target)
    {
        target = null;
        var body = value["ss://".Length..];
        var fragmentIndex = body.IndexOfAny(['#', '?']);
        if (fragmentIndex >= 0)
            body = body[..fragmentIndex];

        string userInfo;
        var atIndex = body.LastIndexOf('@');
        if (atIndex >= 0)
        {
            userInfo = WebUtility.UrlDecode(body[..atIndex]);
            if (!userInfo.Contains(':') && TryDecodeBase64(userInfo, out var decodedUserInfo))
                userInfo = Encoding.UTF8.GetString(decodedUserInfo);
        }
        else
        {
            if (!TryDecodeBase64(body, out var decodedBody))
                return false;
            var decoded = Encoding.UTF8.GetString(decodedBody);
            var decodedAtIndex = decoded.LastIndexOf('@');
            userInfo = decodedAtIndex >= 0 ? decoded[..decodedAtIndex] : decoded;
        }

        userInfo = WebUtility.UrlDecode(userInfo);
        var colonIndex = userInfo.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= userInfo.Length - 1)
            return false;

        var password = userInfo[(colonIndex + 1)..];
        target = BuildTarget(
            XuiV3RenewalTargetInputKind.Configuration,
            XuiV3RenewalTargetCredentialKind.Password,
            password);
        return true;
    }

    /// <summary>
    /// Extracts and URL-decodes the URI user-info segment before the destination host.
    /// </summary>
    /// <param name="body">Configuration text after its <c>scheme://</c> prefix.</param>
    /// <returns>The decoded credential, or an empty string when no user-info exists.</returns>
    private static string ExtractUriUserInfo(string body)
    {
        var atIndex = body.IndexOf('@');
        if (atIndex <= 0)
            return string.Empty;

        return WebUtility.UrlDecode(body[..atIndex]).Trim();
    }

    /// <summary>
    /// Extracts only recognized password-bearing query parameters used by Hysteria-style configuration links.
    /// </summary>
    /// <param name="value">Complete supported configuration URI.</param>
    /// <returns>The URL-decoded password/authentication string, or an empty value when no recognized key exists.</returns>
    /// <remarks>All unrelated query keys and URI fragments are ignored and can never identify an account.</remarks>
    private static string ExtractKnownPasswordQueryParameter(string value)
    {
        var queryStart = value.IndexOf('?');
        if (queryStart < 0 || queryStart >= value.Length - 1)
            return string.Empty;

        var query = value[(queryStart + 1)..];
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0)
            query = query[..fragmentStart];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator >= pair.Length - 1)
                continue;

            var key = WebUtility.UrlDecode(pair[..separator]);
            if (!string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "auth_str", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "password", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return WebUtility.UrlDecode(pair[(separator + 1)..]).Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Decodes standard or URL-safe unpadded Base64 without throwing for malformed customer input.
    /// </summary>
    /// <param name="value">Potential Base64 text without URI fragments.</param>
    /// <param name="bytes">Decoded bytes on success; otherwise an empty array.</param>
    /// <returns><c>true</c> when decoding succeeds and produces bytes; otherwise <c>false</c>.</returns>
    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(normalized);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates one normalized parser result while keeping construction consistent across protocol branches.
    /// </summary>
    /// <param name="inputKind">Customer input shape used for safe diagnostics.</param>
    /// <param name="credentialKind">Trusted panel field eligible for exact comparison.</param>
    /// <param name="value">Normalized sensitive lookup value; it must be non-empty and must not be logged.</param>
    /// <returns>A parser result ready for the pure renewal target resolver.</returns>
    private static XuiV3RenewalTargetInput BuildTarget(
        XuiV3RenewalTargetInputKind inputKind,
        XuiV3RenewalTargetCredentialKind credentialKind,
        string value)
    {
        return new XuiV3RenewalTargetInput
        {
            InputKind = inputKind,
            CredentialKind = credentialKind,
            Value = value
        };
    }
}
