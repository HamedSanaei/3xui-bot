using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

/// <summary>Bounds UX-only Telegram callback acknowledgements so transport latency cannot stall a user lane.</summary>
public static class TelegramCallbackAnswerPolicy
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const double SlowThresholdMs = 2000;

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
        long? telegramUserId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(callbackQueryId)) return false;
        var started = Stopwatch.GetTimestamp();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(Timeout);
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
