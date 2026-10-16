using System.Globalization;
using Telegram.Bot.Exceptions;

namespace Adminbot.Domain;

/// <summary>Classifies tenant weekly-report failures without persisting Telegram descriptions or credentials.</summary>
/// <remarks>Recipient unavailability is an expected pre-delivery condition, not permission to replay an ambiguous photo send.</remarks>
public static class TenantWeeklyDeliveryPolicy
{
    /// <summary>Exact private-chat identity mismatch; terminal for this report, never a reason to send to another chat.</summary>
    public const string RecipientIdentityMismatch = "weekly_recipient_identity_mismatch";
    /// <summary>Maximum time for the new read-only recipient probe; a timeout cannot imply ambiguous photo delivery.</summary>
    public static readonly TimeSpan RecipientProbeTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Minimum delay between definite unreachable-recipient checks; uses the dispatch row's existing UTC update time.</summary>
    public static readonly TimeSpan RecipientRetryDelay = TimeSpan.FromHours(6);

    /// <summary>Closed codes for definite recipient rejection; safe to persist and use in translated database predicates.</summary>
    public static IReadOnlyList<string> RecipientFailureCodes { get; } = Array.AsReadOnly(new[]
    {
        "telegram_recipient_chat_unavailable", "telegram_recipient_blocked", "telegram_recipient_deactivated"
    });

    /// <summary>Returns a closed diagnostic code for a weekly probe, rendering or delivery exception.</summary>
    /// <param name="exception">Required caught exception; API description is inspected locally but never returned or logged.</param>
    /// <returns>A credential-free category, generic Telegram status code, or exception type name; never raw exception text.</returns>
    /// <remarks>Only Telegram's definitive 400/403 recipient errors qualify for delayed retry. Network/408/5xx delivery remains ambiguous.</remarks>
    /// <exception cref="ArgumentNullException">The supplied exception is null.</exception>
    /// <example><code>var code = TenantWeeklyDeliveryPolicy.GetFailureCode(exception);</code></example>
    public static string GetFailureCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is InvalidOperationException && exception.Message == RecipientIdentityMismatch) return RecipientIdentityMismatch;
        if (exception is not ApiRequestException api) return exception.GetType().Name;
        var message = api.Message ?? string.Empty;
        if (api.ErrorCode is 400 or 403)
        {
            if (message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("user not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("bot can't initiate conversation", StringComparison.OrdinalIgnoreCase))
                return "telegram_recipient_chat_unavailable";
            if (message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase))
                return "telegram_recipient_blocked";
            if (message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase))
                return "telegram_recipient_deactivated";
        }
        if (api.ErrorCode == 400)
        {
            if (message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("caption is too long", StringComparison.OrdinalIgnoreCase)) return "telegram_invalid_caption";
            if (message.Contains("PHOTO_INVALID_DIMENSIONS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("IMAGE_PROCESS_FAILED", StringComparison.OrdinalIgnoreCase)) return "telegram_invalid_photo";
        }
        return "telegram_" + api.ErrorCode.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Identifies only definite recipient rejection codes that may be retried after the recipient delay.</summary>
    /// <param name="code">Sanitized diagnostic code; null or an unknown code does not qualify.</param>
    /// <returns>True for one of the closed recipient codes, never for ambiguous delivery or generic historical telegram_400.</returns>
    /// <remarks>This pure classification does not authorize a send; the caller must still honor the durable photo-send barrier.</remarks>
    /// <example><code>if (TenantWeeklyDeliveryPolicy.IsRecipientUnavailable(code)) { /* Defer without a photo retry now. */ }</code></example>
    public static bool IsRecipientUnavailable(string code) => RecipientFailureCodes.Contains(code, StringComparer.Ordinal);
}
