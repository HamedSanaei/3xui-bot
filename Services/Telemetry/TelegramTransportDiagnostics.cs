using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using Telegram.Bot.Exceptions;

namespace Adminbot.Services.Telemetry;

/// <summary>Maps SDK methods and transport failures into a closed, payload-free diagnostic vocabulary.</summary>
/// <remarks>Exception messages, request URLs, headers and bodies are never inspected or retained.</remarks>
internal static class TelegramTransportDiagnostics
{
    /// <summary>Accepts only known SDK method constants, replacing unknown methods with one bounded label.</summary>
    /// <param name="method">SDK method name or the final URI segment, inspected locally and never emitted verbatim.</param>
    /// <returns>A compile-time method constant or unknown.</returns>
    /// <remarks>Span comparison avoids allocating or retaining token-bearing URL text.</remarks>
    /// <example><code>var method = TelegramTransportDiagnostics.Method(request.MethodName.AsSpan());</code></example>
    internal static string Method(ReadOnlySpan<char> method) => method switch
    {
        "getUpdates" => "getUpdates", "getMe" => "getMe", "getWebhookInfo" => "getWebhookInfo",
        "deleteWebhook" => "deleteWebhook", "setWebhook" => "setWebhook", "setMyCommands" => "setMyCommands",
        "sendMessage" => "sendMessage", "sendPhoto" => "sendPhoto", "sendDocument" => "sendDocument",
        "sendMediaGroup" => "sendMediaGroup", "sendVideo" => "sendVideo", "sendAnimation" => "sendAnimation",
        "sendAudio" => "sendAudio", "sendVoice" => "sendVoice", "sendSticker" => "sendSticker",
        "sendChatAction" => "sendChatAction", "answerCallbackQuery" => "answerCallbackQuery",
        "editMessageText" => "editMessageText", "editMessageCaption" => "editMessageCaption",
        "editMessageReplyMarkup" => "editMessageReplyMarkup", "editMessageMedia" => "editMessageMedia",
        "deleteMessage" => "deleteMessage", "deleteMessages" => "deleteMessages", "getChat" => "getChat",
        "getChatMember" => "getChatMember", "getChatMemberCount" => "getChatMemberCount",
        "getChatAdministrators" => "getChatAdministrators", "getFile" => "getFile",
        "forwardMessage" => "forwardMessage", "copyMessage" => "copyMessage", "copyMessages" => "copyMessages",
        "pinChatMessage" => "pinChatMessage", "unpinChatMessage" => "unpinChatMessage",
        "setChatMenuButton" => "setChatMenuButton", "setMyDescription" => "setMyDescription",
        "setMyShortDescription" => "setMyShortDescription", "getUserProfilePhotos" => "getUserProfilePhotos",
        _ => "unknown"
    };

    /// <summary>Groups fixed method constants, keeping normal long polls separate from interactive latency.</summary>
    /// <param name="method">Value returned by Method, never customer input.</param>
    /// <returns>A closed request category.</returns>
    /// <remarks>Unknown operations remain observable without increasing method cardinality.</remarks>
    /// <example><code>var category = TelegramTransportDiagnostics.Category("getUpdates");</code></example>
    internal static string Category(string method) => method switch
    {
        "getUpdates" => "polling",
        "answerCallbackQuery" => "callback_ack",
        "getChat" or "getChatMember" or "getChatMemberCount" or "getChatAdministrators" => "membership",
        "editMessageText" or "editMessageCaption" or "editMessageReplyMarkup" or "editMessageMedia" => "edit",
        "sendPhoto" or "sendDocument" or "sendMediaGroup" or "sendVideo" or "sendAnimation" or "sendAudio" or "sendVoice" => "upload",
        "sendMessage" or "sendSticker" or "forwardMessage" or "copyMessage" or "copyMessages" => "send",
        "getMe" or "getWebhookInfo" or "deleteWebhook" or "setWebhook" or "setMyCommands" => "runtime",
        "getFile" => "file_lookup",
        _ => "other"
    };

    /// <summary>Classifies numeric HTTP/API outcomes and typed inner failures without reading exception text.</summary>
    /// <param name="exception">Original SDK or HTTP exception, or null on completion.</param>
    /// <param name="status">Numeric HTTP status if actually observed, otherwise null.</param>
    /// <param name="caller">Original SDK caller token, not the HTTP client's timeout-linked token.</param>
    /// <returns>Safe metadata carrying only fixed classifications and observed numeric codes.</returns>
    /// <remarks>Real caller shutdown wins over deadlines. TLS and DNS diagnoses use runtime exception types/codes, never guessed message matching.</remarks>
    /// <example><code>var diagnosis = TelegramTransportDiagnostics.Classify(error, status, caller);</code></example>
    internal static LatencyTelemetryEvent Classify(Exception exception, int? status, CancellationToken caller)
    {
        int? apiCode = exception is ApiRequestException api ? api.ErrorCode : null;
        var category = "none";
        var source = "none";
        var timeout = "none";
        var cancellation = false;
        var transport = false;
        var foundTimeout = false;
        var tls = false;
        var dns = false;
        var socket = false;
        for (var current = exception; current != null; current = current.InnerException)
        {
            cancellation |= current is OperationCanceledException;
            foundTimeout |= current is TimeoutException;
            tls |= current is AuthenticationException || current is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError };
            dns |= current is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } ||
                current is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery };
            socket |= current is SocketException;
            transport |= current is HttpRequestException or RequestException or System.IO.IOException;
            if (status == null && current is HttpRequestException http && http.StatusCode.HasValue) status = (int)http.StatusCode.Value;
            if (status == null && current is RequestException sdk && sdk.HttpStatusCode.HasValue) status = (int)sdk.HttpStatusCode.Value;
        }
        if (cancellation || foundTimeout || exception is TelegramForegroundDeliveryTimeoutException)
        {
            source = TelegramRequestCancellationScope.Current?.CancellationSource ?? "none";
            if (source == "none" && caller.IsCancellationRequested) source = "caller";
            timeout = TelegramRequestCancellationScope.Current?.TimeoutCategory ?? "none";
            category = source switch
            {
                "caller" => "caller_cancellation", "callback_policy" => "callback_policy_timeout",
                "foreground_budget" => "foreground_budget_timeout", "startup_probe" => "startup_probe_timeout",
                _ => foundTimeout ? "http_timeout" : "transport_cancellation"
            };
            if (foundTimeout && source == "none") { source = "http_client"; timeout = "http_client"; }
        }
        else if (tls) category = "tls";
        else if (dns) category = "dns";
        else if (socket) category = "socket";
        else if (apiCode == 429 || status == 429) category = "http_429";
        else if (apiCode >= 500 || status >= 500) category = "http_5xx";
        else if (apiCode.HasValue) category = "telegram_api_rejection";
        else if (transport) category = "transport";
        else if (exception != null) category = "unexpected";
        else if (status >= 400) category = "http_rejection";
        return new LatencyTelemetryEvent
        {
            HttpStatusCode = status, ApiErrorCode = apiCode, FailureClassification = category,
            ExceptionCategory = exception == null ? "none" : category,
            CancellationSource = source, TimeoutCategory = timeout,
            Outcome = exception == null && !(status >= 400) ? "completed" : "failed"
        };
    }
}
