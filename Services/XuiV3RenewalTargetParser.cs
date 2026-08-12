using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

/// <summary>
/// Extracts a canonical XUI client UUID from renewal identifiers shared by owned and tenant Telegram flows.
/// </summary>
/// <remarks>
/// Supported input includes a raw UUID, VLESS/Trojan-style text containing a UUID, and VMess base64 JSON. Parsing is
/// side-effect free and never logs the identifier because UUIDs are access credentials. Account-name and SubId lookup
/// remain separate so they cannot accidentally receive UUID-based non-owner authorization.
/// </remarks>
public static class XuiV3RenewalTargetParser
{
    /// <summary>
    /// Attempts to extract and normalize a UUID from customer-provided renewal text.
    /// </summary>
    /// <param name="input">
    /// Raw Telegram text. It may be a UUID, a configuration URL containing one, or a complete VMess URL. Null, empty,
    /// malformed, and owner-only account-name inputs are rejected.
    /// </param>
    /// <param name="uuid">Canonical dashed UUID when parsing succeeds; otherwise an empty string.</param>
    /// <returns><c>true</c> when a non-empty GUID was safely extracted; otherwise <c>false</c>.</returns>
    /// <remarks>The returned UUID is sensitive and must not be included in callbacks, logs, or user-facing diagnostics.</remarks>
    /// <example>
    /// <code>
    /// if (XuiV3RenewalTargetParser.TryExtractUuid(message.Text, out var uuid))
    ///     await StartUuidRenewalAsync(uuid, cancellationToken);
    /// </code>
    /// </example>
    public static bool TryExtractUuid(string input, out string uuid)
    {
        uuid = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (TryExtractVmessUuid(input, out uuid))
            return true;

        var match = Regex.Match(
            input,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.CultureInvariant);

        if (!match.Success || !Guid.TryParse(match.Value, out var parsed) || parsed == Guid.Empty)
            return false;

        uuid = parsed.ToString();
        return true;
    }

    /// <summary>
    /// Decodes a VMess URL and extracts its JSON <c>id</c> without exposing malformed payload contents.
    /// </summary>
    /// <param name="input">Potential VMess URL received from Telegram.</param>
    /// <param name="uuid">Canonical UUID from the decoded VMess configuration, or an empty string on failure.</param>
    /// <returns><c>true</c> when a valid non-empty VMess UUID was decoded; otherwise <c>false</c>.</returns>
    /// <remarks>All parse failures are intentionally silent because callers only need a safe match/no-match result.</remarks>
    private static bool TryExtractVmessUuid(string input, out string uuid)
    {
        uuid = string.Empty;
        if (!input.Trim().StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var payload = input.Trim()["vmess://".Length..].Trim()
                .Replace('-', '+')
                .Replace('_', '/');
            var padding = payload.Length % 4;
            if (padding > 0)
                payload = payload.PadRight(payload.Length + (4 - padding), '=');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var vmess = JsonConvert.DeserializeObject<VMessConfiguration>(json);
            if (vmess?.Id == Guid.Empty)
                return false;

            uuid = vmess.Id.ToString();
            return true;
        }
        catch (Exception) when (input != null)
        {
            return false;
        }
    }
}
