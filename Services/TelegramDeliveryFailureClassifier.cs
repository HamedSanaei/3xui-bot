using Telegram.Bot.Exceptions;

public static class TelegramDeliveryFailureClassifier
{
    public static string Classify(ApiRequestException ex)
    {
        if (ex == null)
            return "telegram_api_unknown";

        var message = ex.Message ?? string.Empty;
        if (ex.ErrorCode == 429)
            return "telegram_api_429";
        if (ex.ErrorCode >= 500)
            return "telegram_api_5xx";

        if (message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("bot blocked by user", StringComparison.OrdinalIgnoreCase))
            return "telegram_bot_blocked_by_user";

        if (message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("user deactivated", StringComparison.OrdinalIgnoreCase))
            return "telegram_user_deactivated";

        if (ex.ErrorCode == 400 &&
            message.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            return "telegram_chat_not_found";

        if (ex.ErrorCode == 403)
            return "telegram_forbidden";

        if (ex.ErrorCode == 400)
            return "telegram_api_400_other";

        return $"telegram_api_{Math.Clamp(ex.ErrorCode, 0, 999)}";
    }
}
