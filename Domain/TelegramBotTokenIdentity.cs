using System.Globalization;

namespace Adminbot.Domain;

/// <summary>
/// Provides safe identity helpers for Telegram bot tokens without exposing the token secret.
/// </summary>
/// <remarks>
/// Telegram bot tokens start with the numeric bot id followed by a colon and the secret part.
/// The numeric prefix and the bot username returned by <c>GetMe</c> are enough to detect duplicate
/// bot registrations across owned bots and tenant storefronts without logging or displaying the full token.
/// </remarks>
public static class TelegramBotTokenIdentity
{
    /// <summary>
    /// Normalizes a Telegram bot token for exact in-memory/database comparison.
    /// </summary>
    /// <param name="token">
    /// Raw BotFather token entered by a tenant owner or loaded from configuration. The value may be null,
    /// empty, or padded with whitespace; the returned value is trimmed and never modified otherwise.
    /// </param>
    /// <returns>
    /// Trimmed token text, or an empty string when <paramref name="token" /> is null or whitespace.
    /// The return value must not be written to logs or Telegram messages.
    /// </returns>
    public static string NormalizeToken(string token)
    {
        return token?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Extracts the numeric Telegram bot id prefix from a BotFather token.
    /// </summary>
    /// <param name="token">
    /// Raw or normalized BotFather token. Only the portion before the first colon is parsed.
    /// </param>
    /// <returns>
    /// Numeric Telegram bot id when the prefix is present and valid; otherwise <c>null</c>.
    /// </returns>
    public static long? ExtractBotId(string token)
    {
        var normalized = NormalizeToken(token);
        var colonIndex = normalized.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
            return null;

        return long.TryParse(
            normalized[..colonIndex],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var botId)
            ? botId
            : null;
    }

    /// <summary>
    /// Normalizes a Telegram bot username for case-insensitive duplicate checks.
    /// </summary>
    /// <param name="username">
    /// Username returned by Telegram <c>GetMe</c>, loaded from configuration, or stored in users.db.
    /// The value may include a leading <c>@</c>.
    /// </param>
    /// <returns>
    /// Username without a leading <c>@</c>, or an empty string when no username is available.
    /// </returns>
    public static string NormalizeUsername(string username)
    {
        return username?.Trim().TrimStart('@') ?? string.Empty;
    }

    /// <summary>
    /// Checks whether two Telegram bot tokens identify the same bot.
    /// </summary>
    /// <param name="leftToken">First raw or normalized Telegram bot token.</param>
    /// <param name="rightToken">Second raw or normalized Telegram bot token.</param>
    /// <returns>
    /// <c>true</c> when both tokens are exactly equal after trimming, or when both expose the same numeric
    /// Telegram bot id prefix; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// The exact-token check catches accidental copy/paste reuse. The numeric id check catches the same bot
    /// after BotFather rotates/revokes and recreates the secret part for the same Telegram bot.
    /// </remarks>
    public static bool IsSameBotToken(string leftToken, string rightToken)
    {
        var left = NormalizeToken(leftToken);
        var right = NormalizeToken(rightToken);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;

        var leftBotId = ExtractBotId(left);
        var rightBotId = ExtractBotId(right);
        return leftBotId.HasValue &&
               rightBotId.HasValue &&
               leftBotId.Value == rightBotId.Value;
    }

    /// <summary>
    /// Creates a non-secret token label that can be written to operational logs.
    /// </summary>
    /// <param name="token">Raw or normalized Telegram bot token.</param>
    /// <returns>
    /// A masked token label containing only the numeric bot id prefix when available. The secret suffix is never returned.
    /// </returns>
    public static string MaskToken(string token)
    {
        var botId = ExtractBotId(token);
        return botId.HasValue ? $"{botId.Value}:***" : "***";
    }
}
