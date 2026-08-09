using System;

/// <summary>
/// Identifies the high-priority Telegram commands that abandon a bot-scoped conversation and return to a home menu.
/// </summary>
internal enum TelegramNavigationCommandKind
{
    /// <summary>
    /// Telegram's standard start command. It may carry a deep-link payload such as a payment return or referral code.
    /// </summary>
    Start,

    /// <summary>
    /// Owned-bot-only alias that performs the same plain home reset as <c>/start</c> and never accepts a payload.
    /// </summary>
    Refresh
}

/// <summary>
/// Represents one validated navigation command addressed to the currently active Telegram bot.
/// </summary>
internal readonly struct TelegramNavigationCommand
{
    /// <summary>
    /// Creates a parsed navigation command.
    /// </summary>
    /// <param name="kind">The validated <c>start</c> or <c>refresh</c> command kind.</param>
    /// <param name="payload">
    /// Optional Telegram deep-link payload without the command token. This value is empty for plain start and every
    /// valid refresh command.
    /// </param>
    public TelegramNavigationCommand(TelegramNavigationCommandKind kind, string payload)
    {
        Kind = kind;
        Payload = payload ?? string.Empty;
    }

    /// <summary>
    /// Gets the parsed start or refresh command kind.
    /// </summary>
    public TelegramNavigationCommandKind Kind { get; }

    /// <summary>
    /// Gets the optional start payload after whitespace or the legacy equals separator; never <c>null</c>.
    /// </summary>
    public string Payload { get; }

    /// <summary>
    /// Checks whether the start payload begins with one complete routing token.
    /// </summary>
    /// <param name="token">
    /// Non-empty routing token such as <c>payment_success</c>. Matching is case-insensitive and requires either the
    /// complete payload or a Unicode whitespace boundary after the token.
    /// </param>
    /// <returns>
    /// <c>true</c> when <see cref="Payload"/> begins with the complete token; otherwise <c>false</c>. Refresh commands
    /// always return <c>false</c> because their payload is empty.
    /// </returns>
    /// <remarks>
    /// The token-boundary check prevents a value such as <c>payment_success_fake</c> from being routed as an official
    /// payment return while retaining compatibility with legacy start links that append whitespace-delimited data.
    /// </remarks>
    /// <example><code>command.HasPayloadToken("payment_success");</code></example>
    public bool HasPayloadToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(Payload))
            return false;

        if (Payload.Equals(token, StringComparison.OrdinalIgnoreCase))
            return true;

        return Payload.Length > token.Length &&
               Payload.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
               char.IsWhiteSpace(Payload[token.Length]);
    }
}

/// <summary>
/// Parses bot-addressed <c>/start</c> and <c>/refresh</c> messages without accepting commands meant for another bot.
/// </summary>
internal static class TelegramNavigationCommandParser
{
    /// <summary>
    /// Parses a Telegram navigation command and validates an optional <c>@BotUsername</c> mention.
    /// </summary>
    /// <param name="text">
    /// Raw Telegram message text. Leading and trailing whitespace is allowed; null, empty, and non-command text are
    /// rejected.
    /// </param>
    /// <param name="currentBotUsername">
    /// Username of the bot handling the update, with or without a leading <c>@</c>. A mentioned command is accepted
    /// only when this value is non-empty and matches case-insensitively.
    /// </param>
    /// <param name="allowRefresh">
    /// <c>true</c> for owned bots that expose <c>/refresh</c>; <c>false</c> for tenant and other bot types.
    /// </param>
    /// <param name="command">
    /// Validated command kind and normalized payload when parsing succeeds; otherwise the default value.
    /// </param>
    /// <returns>
    /// <c>true</c> only when the complete command token is <c>/start</c> or an allowed payload-free
    /// <c>/refresh</c>, and any bot mention targets the current bot; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Start payloads are retained for payment and referral handlers. The legacy <c>/start=payload</c> form remains
    /// supported, while refresh deliberately rejects payloads so it cannot become an alternate deep-link surface.
    /// This method only parses text and has no database, wallet, order, account, or Telegram side effects.
    /// </remarks>
    /// <example>
    /// <code>
    /// TelegramNavigationCommandParser.TryParse(" /start@ShopBot ref_ab12 ", "ShopBot", true, out var command);
    /// // command.Kind == TelegramNavigationCommandKind.Start; command.Payload == "ref_ab12"
    /// </code>
    /// </example>
    public static bool TryParse(
        string text,
        string currentBotUsername,
        bool allowRefresh,
        out TelegramNavigationCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        var separatorIndex = FindFirstWhitespace(normalized);
        var commandToken = separatorIndex < 0 ? normalized : normalized[..separatorIndex];
        var payload = separatorIndex < 0 ? string.Empty : normalized[(separatorIndex + 1)..].Trim();

        var equalsIndex = commandToken.IndexOf('=');
        if (equalsIndex >= 0)
        {
            if (payload.Length > 0)
                return false;

            payload = commandToken[(equalsIndex + 1)..].Trim();
            commandToken = commandToken[..equalsIndex];
        }

        if (commandToken.Length == 0 || commandToken[0] != '/')
            return false;

        var commandName = commandToken[1..];
        var mentionIndex = commandName.IndexOf('@');
        if (mentionIndex >= 0)
        {
            var mentionedUsername = commandName[(mentionIndex + 1)..];
            commandName = commandName[..mentionIndex];
            var normalizedCurrentUsername = currentBotUsername?.Trim().TrimStart('@') ?? string.Empty;
            if (mentionedUsername.Length == 0 ||
                normalizedCurrentUsername.Length == 0 ||
                !mentionedUsername.Equals(normalizedCurrentUsername, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (commandName.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            command = new TelegramNavigationCommand(TelegramNavigationCommandKind.Start, payload);
            return true;
        }

        if (allowRefresh &&
            payload.Length == 0 &&
            commandName.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            command = new TelegramNavigationCommand(TelegramNavigationCommandKind.Refresh, string.Empty);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the first Unicode whitespace separator between a Telegram command token and its payload.
    /// </summary>
    /// <param name="value">Trimmed non-empty Telegram message text to scan.</param>
    /// <returns>The zero-based whitespace index, or <c>-1</c> when the text contains only one token.</returns>
    /// <remarks>
    /// Telegram normally uses an ASCII space, but accepting other Unicode whitespace keeps Persian and mobile-client
    /// input predictable without normalizing or rewriting the payload text.
    /// </remarks>
    /// <example><code>FindFirstWhitespace("/start ref_demo"); // 6</code></example>
    private static int FindFirstWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
                return index;
        }

        return -1;
    }
}
