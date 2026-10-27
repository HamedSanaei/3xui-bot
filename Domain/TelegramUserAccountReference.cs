using System.Globalization;

namespace Adminbot.Domain;

/// <summary>Parses exact administrator profile references without contacting Telegram or resolving arbitrary URLs.</summary>
/// <remarks>Usernames identify only locally observed profiles, never financial authority or proof of account ownership.</remarks>
public static class TelegramUserAccountReference
{
    /// <summary>Recognizes a positive numeric Telegram user id, bare/@ username, or single-profile t.me HTTP/HTTPS URL.</summary>
    /// <param name="input">Untrusted admin message, optionally surrounded by whitespace; null, invalid identifiers and non-profile URLs are rejected.</param>
    /// <param name="telegramUserId">Positive global Telegram user id on numeric success, otherwise zero; not an internal database or tenant id.</param>
    /// <param name="username">Case-preserved ASCII username without @ on username success, otherwise null. It must only be used for an exact, case-insensitive local lookup.</param>
    /// <returns>True when exactly one reference form is valid; false leaves both outputs empty and performs no I/O.</returns>
    /// <remarks>Only t.me links with one path segment and no credentials, query, fragment, escapes or custom port are accepted.
    /// Locally stored short usernames are supported (1–32 characters, starting with an ASCII letter); this is not a Telegram availability validator.
    /// Numeric ids remain authoritative; a username may be stale or ambiguous and requires caller-side duplicate rejection.</remarks>
    /// <example><code>if (TelegramUserAccountReference.TryParse("https://t.me/im_Tony2", out var id, out var username)) { /* Read stored profiles only. */ }</code></example>
    public static bool TryParse(string input, out long telegramUserId, out string username)
    {
        telegramUserId = 0;
        username = null;
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 160 || value.Any(char.IsControl)) return false;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            telegramUserId = id;
            return true;
        }
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (value.Contains('%') || value.Contains('\\') || value.Contains("/.", StringComparison.Ordinal) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort ||
                !string.Equals(uri.Host, "t.me", StringComparison.OrdinalIgnoreCase) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return false;
            value = uri.AbsolutePath[1..];
            if (value.EndsWith('/')) value = value[..^1];
        }
        else if (value.StartsWith('@')) value = value[1..];
        if (value.Length is < 1 or > 32 || !char.IsAsciiLetter(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return false;
        username = value;
        return true;
    }
}
