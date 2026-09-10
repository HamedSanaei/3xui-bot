using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

/// <summary>Bounds UX-only Telegram callback acknowledgements so transport latency cannot stall a user lane.</summary>
/// <remarks>
/// This policy exists because a callback acknowledgement is a user-experience operation: the only cost
/// of skipping it is a spinner that stays visible on the tapped button. It must never be allowed to
/// inherit the Telegram client's default HTTP timeout, because that latency is charged to the update
/// lane and blocks every later update from the same user (the Sequence-2990 incident).
///
/// Failures are therefore best-effort. A local timeout, a Telegram transport failure, or a stale/expired
/// callback returns <c>false</c> and lets the business handler continue. Only cancellation that came from
/// the caller's own token is rethrown, because that means the whole update lane is shutting down or the
/// request was abandoned and continuing would be wrong.
/// </remarks>
public static class TelegramCallbackAnswerPolicy
{
    /// <summary>
    /// The production callback-acknowledgement budget. Exactly two seconds, and independent of any
    /// injected test value, so production latency policy cannot be changed by test wiring.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const double SlowThresholdMs = 2000;

    /// <summary>
    /// Attempts to acknowledge a Telegram callback query inside a bounded, best-effort budget.
    /// </summary>
    /// <param name="client">
    /// The Telegram bot client that owns the callback query. Must be the client of the bot that received the
    /// update; never a different bot from the same tenant or owner, because a callback can only be acknowledged
    /// by the bot it was sent to.
    /// </param>
    /// <param name="callbackQueryId">
    /// The Telegram callback query id taken from <c>CallbackQuery.Id</c>. When this is null or whitespace the
    /// method returns <c>false</c> without calling Telegram.
    /// </param>
    /// <param name="text">
    /// Optional notification text or alert body shown to the user. Must not contain secrets, bot tokens, or
    /// internal operator data, because Telegram renders it directly in the client UI.
    /// </param>
    /// <param name="showAlert">
    /// When <c>true</c> Telegram shows the text as a modal alert instead of a toast. Used for authorization
    /// alerts. Alert presentation does not change the bounded transport behaviour of this method.
    /// </param>
    /// <param name="url">Optional URL opened by Telegram when the callback is a game callback. Normally null.</param>
    /// <param name="cacheTime">Optional Telegram callback cache lifetime in seconds. Normally null.</param>
    /// <param name="cancellationToken">
    /// The update lane cancellation token. When this token is cancelled the resulting
    /// <see cref="OperationCanceledException"/> is rethrown to the caller instead of being swallowed, because
    /// outer cancellation means the lane is stopping rather than the acknowledgement merely being slow.
    /// </param>
    /// <param name="logger">
    /// Optional logger used only for slow-call and failure diagnostics. Must never be used to log callback
    /// payload contents, tokens, or API keys.
    /// </param>
    /// <param name="botId">
    /// Optional internal bot identifier used for diagnostics only. This is the configured bot id, not a
    /// Telegram bot id or chat id.
    /// </param>
    /// <param name="telegramUserId">
    /// Optional numeric Telegram user id of the user who tapped the button, used for diagnostics only.
    /// </param>
    /// <param name="timeout">
    /// The effective acknowledgement budget. When null the production budget <see cref="Timeout"/> (two
    /// seconds) is used. Production callers pass <c>TelegramInteractionTimeouts.CallbackAnswer</c>; tests pass
    /// a millisecond value so timeout behaviour can be proven without waiting the real two seconds. Values
    /// must be greater than zero.
    /// </param>
    /// <returns>
    /// <c>true</c> when Telegram accepted the acknowledgement; <c>false</c> for every best-effort failure,
    /// including a local timeout, a transport failure, a stale or invalid callback query, and a missing
    /// callback id. Callers must never gate business behaviour on this result.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> was cancelled by the caller. This indicates outer lane
    /// shutdown and is intentionally propagated.
    /// </exception>
    /// <example>
    /// <code>
    /// // Production: bounded by the configured two-second budget.
    /// await TelegramCallbackAnswerPolicy.TryAnswerAsync(
    ///     botClient,
    ///     callbackQuery.Id,
    ///     cancellationToken: cancellationToken,
    ///     logger: _logger,
    ///     botId: BotContextAccessor.CurrentBotId,
    ///     timeout: _interactionTimeouts.CallbackAnswer);
    /// </code>
    /// </example>
    public static async Task<bool> TryAnswerAsync(
        ITelegramBotClient client,
        string callbackQueryId,
        string text = null,
        bool? showAlert = null,
        string url = null,
        int? cacheTime = null,
        CancellationToken cancellationToken = default,
        ILogger logger = null,
        string botId = null,
        long? telegramUserId = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(callbackQueryId)) return false;
        var started = Stopwatch.GetTimestamp();
        // The caller-supplied token is linked so outer lane cancellation still propagates, while the local
        // budget guarantees this UX-only request cannot outlive its deadline and stall the user lane.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout ?? Timeout);
        try
        {
            await client.AnswerCallbackQueryAsync(
                callbackQueryId, text, showAlert, url, cacheTime, bounded.Token);
            LogIfSlow(logger, started, botId, telegramUserId, "completed", null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Log(logger, started, botId, telegramUserId, "local_timeout", ex.GetType().Name);
            return false;
        }
        catch (ApiRequestException ex) when (IsHarmlessStaleCallback(ex))
        {
            logger?.LogDebug("Telegram callback ACK ignored as stale/invalid. BotId={BotId} ErrorCode={ErrorCode}",
                botId ?? string.Empty, ex.ErrorCode);
            return false;
        }
        catch (ApiRequestException ex)
        {
            Log(logger, started, botId, telegramUserId, $"telegram_api_{ex.ErrorCode}", ex.GetType().Name);
            return false;
        }
        catch (RequestException ex)
        {
            Log(logger, started, botId, telegramUserId, "telegram_transport_error", ex.GetType().Name);
            return false;
        }
        catch (HttpRequestException ex)
        {
            Log(logger, started, botId, telegramUserId, "telegram_http_error", ex.GetType().Name);
            return false;
        }
        catch (TimeoutException ex)
        {
            Log(logger, started, botId, telegramUserId, "telegram_timeout", ex.GetType().Name);
            return false;
        }
    }

    public static bool IsHarmlessStaleCallback(ApiRequestException ex)
    {
        if (ex?.ErrorCode != 400) return false;
        var message = ex.Message ?? string.Empty;
        return message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("response timeout expired", StringComparison.OrdinalIgnoreCase);
    }
    private static void LogIfSlow(ILogger logger, long started, string botId, long? userId, string outcome, string errorType)
    {
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds < SlowThresholdMs) return;
        Log(logger, started, botId, userId, outcome, errorType);
    }

    private static void Log(ILogger logger, long started, string botId, long? userId, string outcome, string errorType)
    {
        if (logger == null) return;
        logger.LogInformation(
            "Slow Telegram operation. BotId={BotId} TelegramUserId={TelegramUserId} Operation={Operation} ElapsedMs={ElapsedMs:0} Outcome={Outcome} ErrorType={ErrorType}",
            botId ?? string.Empty,
            userId,
            "telegram_callback_ack",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            outcome,
            errorType ?? string.Empty);
    }
}
