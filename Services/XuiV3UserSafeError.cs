/// <summary>
/// Converts internal XUI v3 account-creation failures into fixed Telegram-safe messages.
/// </summary>
/// <remarks>
/// Panel URLs, root paths, endpoint names, API response bodies, request payloads, tokens, and cookies are
/// infrastructure secrets. Call this helper at every customer/admin Telegram boundary instead of displaying
/// <see cref="Exception.Message"/> or a raw panel response. Internal diagnostics must log the original exception
/// separately before using the returned text.
/// </remarks>
public static class XuiV3UserSafeError
{
    /// <summary>Safe message used for temporary network, proxy, timeout, and Cloudflare failures.</summary>
    public const string TemporaryAccountCreationFailureMessage =
        "ارتباط با سرویس ساخت اکانت موقتاً دچار اختلال شده است. لطفاً چند دقیقه دیگر دوباره تلاش کنید.";

    /// <summary>Safe message used for non-transient account-creation failures.</summary>
    public const string AccountCreationFailureMessage =
        "ساخت اکانت انجام نشد. لطفاً دوباره تلاش کنید و در صورت تکرار با پشتیبانی تماس بگیرید.";

    /// <summary>
    /// Maps an internal account-creation exception to a fixed message that is safe to send through Telegram.
    /// </summary>
    /// <param name="exception">
    /// Original exception from the XUI client or its HTTP transport. It may contain a private panel URL, root path,
    /// raw response body, or request details and must never be sent or HTML-encoded directly for a user.
    /// </param>
    /// <returns>
    /// A fixed Persian transient message for retryable XUI/network failures; otherwise a fixed generic creation
    /// failure. The returned value contains no data copied from <paramref name="exception"/>.
    /// </returns>
    /// <remarks>
    /// This method does not log. The caller must retain the original exception in the internal daily diagnostic file
    /// before displaying this sanitized result.
    /// </remarks>
    /// <example>
    /// <code>
    /// catch (Exception ex)
    /// {
    ///     logger.LogError(ex, "XUI account creation failed.");
    ///     await bot.SendTextMessageAsync(chatId, XuiV3UserSafeError.ForAccountCreation(ex));
    /// }
    /// </code>
    /// </example>
    public static string ForAccountCreation(Exception exception)
    {
        return ApiServicev3.IsTransientXuiTransportException(exception)
            ? TemporaryAccountCreationFailureMessage
            : AccountCreationFailureMessage;
    }

    /// <summary>
    /// Replaces an internal or provider-supplied account-creation message with a fixed Telegram-safe message.
    /// </summary>
    /// <param name="internalMessage">
    /// Raw result text from the panel, exception, bulk failure, or legacy creation API. The value is optional and is
    /// inspected only to preserve the already-safe transient classification; no part is copied to the return value.
    /// </param>
    /// <returns>
    /// A fixed safe transient or generic Persian message. The result never includes panel URLs, response bodies,
    /// endpoint paths, tokens, cookies, or arbitrary provider text.
    /// </returns>
    /// <remarks>
    /// Use this overload when an exception object is no longer available. Prefer the exception overload at the
    /// original catch site because it classifies transport failures more accurately.
    /// </remarks>
    /// <example>
    /// <code>
    /// var safeText = XuiV3UserSafeError.ForAccountCreation(creationResult.Message);
    /// </code>
    /// </example>
    public static string ForAccountCreation(string internalMessage)
    {
        if (string.Equals(
                internalMessage,
                TemporaryAccountCreationFailureMessage,
                StringComparison.Ordinal))
        {
            return TemporaryAccountCreationFailureMessage;
        }

        var value = internalMessage ?? string.Empty;
        if (value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("gateway", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("temporar", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 520", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 521", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 522", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 523", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 524", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 525", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 526", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 527", StringComparison.OrdinalIgnoreCase))
        {
            return TemporaryAccountCreationFailureMessage;
        }

        return AccountCreationFailureMessage;
    }
}
