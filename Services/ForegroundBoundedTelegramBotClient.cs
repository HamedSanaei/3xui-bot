using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;

/// <summary>
/// Decorates one bot client for a single Telegram update execution and bounds only that update's non-durable
/// interactive UX calls with <see cref="TelegramForegroundDeliveryPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// Telegram.Bot v22 exposes one generic <see cref="ITelegramBotClient.SendRequest{TResponse}"/> entry point, and
/// every convenience method such as <c>SendMessage</c> builds a strongly typed request and calls it. That
/// makes one small decorator a complete chokepoint for the interactive surface without touching hundreds of call
/// sites, and without changing the shared transport timeout used by receivers, long polling, and durable workers.
/// </para>
/// <para>
/// What is bounded:
/// only explicitly listed interactive request kinds — message sends, photo/album/document sends, message edits,
/// message deletion, callback acknowledgements, and chat/membership lookups. Everything else, including
/// <c>getUpdates</c>, webhook management, and <see cref="DownloadFile"/>, is delegated untouched so receiver
/// polling and durable file relay keep their existing behavior.
/// </para>
/// <para>
/// Ambiguous sends:
/// If the budget expires, the call is abandoned and reported as
/// <see cref="TelegramForegroundDeliveryTimeoutException"/>. It is never retried automatically, because Telegram may
/// already have accepted the message. The scheduled update completes with a stable non-error outcome and the customer
/// presses the button again, which generates a new update.
/// </para>
/// <para>
/// Thread safety and lifetime:
/// the decorator is immutable and holds only the inner client and the immutable policy, so it can be created per
/// update execution and shared by every awaited call within that execution.
/// </para>
/// </remarks>
public sealed class ForegroundBoundedTelegramBotClient : ITelegramBotClient
{
    /// <summary>Inner client that performs the real Telegram call; never disposed by this decorator.</summary>
    private readonly ITelegramBotClient _inner;

    /// <summary>Immutable budgets for ordinary sends and multipart media-group uploads.</summary>
    private readonly TelegramForegroundDeliveryPolicy _policy;

    /// <summary>
    /// Creates a foreground-bounded view over an existing bot client.
    /// </summary>
    /// <param name="inner">
    /// Resolved runtime client for the bot that received the update. The decorator never disposes it because the same
    /// client instance is shared with the receiver and background workers.
    /// </param>
    /// <param name="policy">
    /// Immutable interactive delivery budget. Production passes <see cref="TelegramForegroundDeliveryPolicy.Production"/>;
    /// tests pass millisecond budgets.
    /// </param>
    /// <exception cref="ArgumentNullException">The inner client or the policy is null.</exception>
    public ForegroundBoundedTelegramBotClient(ITelegramBotClient inner, TelegramForegroundDeliveryPolicy policy)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public bool LocalBotServer => _inner.LocalBotServer;

    /// <inheritdoc />
    public long BotId => _inner.BotId;

    /// <summary>
    /// Gets or sets the transport timeout of the inner client. The decorator does not own or change this value.
    /// </summary>
    public TimeSpan Timeout
    {
        get => _inner.Timeout;
        set => _inner.Timeout = value;
    }

    /// <summary>
    /// Gets or sets the inner client's exception parser. The decorator forwards it so Telegram error classification is
    /// unchanged.
    /// </summary>
    public IExceptionParser ExceptionsParser
    {
        get => _inner.ExceptionsParser;
        set => _inner.ExceptionsParser = value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest
    {
        add => _inner.OnMakingApiRequest += value;
        remove => _inner.OnMakingApiRequest -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived
    {
        add => _inner.OnApiResponseReceived += value;
        remove => _inner.OnApiResponseReceived -= value;
    }

    /// <inheritdoc />
    public Task<bool> TestApi(CancellationToken cancellationToken = default)
        => _inner.TestApi(cancellationToken);

    /// <inheritdoc />
    public Task DownloadFile(Telegram.Bot.Types.TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => DownloadFile(file.FilePath, destination, cancellationToken);

    /// <summary>
    /// Downloads a Telegram file through the inner client without any foreground budget.
    /// </summary>
    /// <param name="filePath">Telegram file path returned by <c>GetFile</c>.</param>
    /// <param name="destination">Destination stream owned by the caller.</param>
    /// <param name="cancellationToken">Caller cancellation; never replaced by a foreground budget.</param>
    /// <returns>A task completing after the file has been copied into <paramref name="destination"/>.</returns>
    /// <remarks>
    /// File transfer is deliberately excluded from the foreground policy: receipt relay and configuration export are
    /// durable, size-driven operations whose partial download must not be mistaken for a failed interactive send.
    /// </remarks>
    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => _inner.DownloadFile(filePath, destination, cancellationToken);

    /// <summary>
    /// Executes one Telegram request, applying the foreground delivery budget only to interactive UX request kinds.
    /// </summary>
    /// <typeparam name="TResponse">Response type required by the Telegram request.</typeparam>
    /// <param name="request">Required SDK request built by the handler; only its explicit type is classified, never its private content.</param>
    /// <param name="cancellationToken">Optional caller-owned lane cancellation; default permits the selected foreground deadline to own cancellation.</param>
    /// <returns>The unchanged SDK response, safe only under the caller's existing exposure rules; a successful send must not be resent.</returns>
    /// <remarks>
    /// Non-interactive requests pass through unchanged. Interactive requests use one linked token with the selected
    /// deadline: media groups and documents get the bounded multipart upload budget, while other interactive calls
    /// keep the ordinary deadline. The ambient <see cref="TelegramUpdateLatencyScope"/> measures the same awaited
    /// inner request and records a metadata-only completion even for healthy calls. The recorder is local telemetry,
    /// not an operator incident; recorder failures cannot alter the response or exception.
    /// When only that deadline expires — the caller's own token is still live — the typed
    /// <see cref="TelegramForegroundDeliveryTimeoutException"/> is raised without retrying. Caller cancellation,
    /// transport errors, and Telegram API errors keep their original exception identity. Cancellation is recorded as
    /// caller-owned, deadline-owned, or independent transport cancellation according to the actual token states.
    /// </remarks>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">
    /// The overall interactive delivery budget expired before Telegram answered.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled, or the inner transport cancelled independently.</exception>
    /// <exception cref="ApiRequestException">Telegram rejected the request; only its numeric code enters diagnostics.</exception>
    /// <example><code>var response = await client.SendRequest(request, laneCancellationToken);</code></example>
    public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (!TryClassifyForegroundRequest(request, out var stage, out var kind))
            return await _inner.SendRequest(request, cancellationToken);

        var overallBudget = request is SendMediaGroupRequest or SendDocumentRequest
            ? _policy.MediaGroupBudget : _policy.OverallBudget;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(overallBudget);
        var latencyScope = TelegramUpdateLatencyScope.Current;
        using var measurement = latencyScope?.Measure(stage) ?? default;
        var requestMeasurement = latencyScope?.MeasureTelegramRequest(kind) ?? default;
        var outcome = TelegramForegroundRequestOutcome.Completed;
        int? apiErrorCode = null;

        try
        {
            return await _inner.SendRequest(request, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            outcome = TelegramForegroundRequestOutcome.ForegroundBudgetExpired;
            // An upload may have reached Telegram before its response was lost; never retry an ambiguous send.
            throw new TelegramForegroundDeliveryTimeoutException(DescribeRequestKind(request), overallBudget);
        }
        catch (ApiRequestException exception)
        {
            outcome = TelegramForegroundRequestOutcome.TelegramApiError;
            apiErrorCode = exception.ErrorCode;
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested
                ? TelegramForegroundRequestOutcome.CallerCancellation
                : TelegramForegroundRequestOutcome.TransportError;
            throw;
        }
        catch (Exception exception)
        {
            outcome = exception is HttpRequestException or IOException or RequestException or TimeoutException
                ? TelegramForegroundRequestOutcome.TransportError
                : TelegramForegroundRequestOutcome.UnexpectedError;
            throw;
        }
        finally
        {
            requestMeasurement.Complete(outcome, apiErrorCode);
        }
    }

    /// <summary>
    /// Classifies an interactive foreground request with its closed-vocabulary stage and metadata kind.
    /// </summary>
    /// <typeparam name="TResponse">SDK response type carried by the supplied request.</typeparam>
    /// <param name="request">Request instance built by the handler.</param>
    /// <param name="stage">Receives the closed-vocabulary stage for a bounded request.</param>
    /// <param name="kind">Receives the explicit closed-vocabulary operation; no content or dynamic type name is used.</param>
    /// <returns>
    /// <c>true</c> when the request is an interactive UX call that must respect the foreground budget; otherwise
    /// <c>false</c> so the request is delegated untouched.
    /// </returns>
    /// <remarks>
    /// The mapping is a fixed compile-time list. Unknown request kinds — including receiver long polling, webhook
    /// management, and file transfer — are never bounded, so this decorator cannot alter runtime or durable behavior.
    /// </remarks>
    /// <example><code>var isForeground = TryClassifyForegroundRequest(request, out var stage, out var kind);</code></example>
    private static bool TryClassifyForegroundRequest<TResponse>(
        IRequest<TResponse> request, out TelegramUpdateStage stage, out TelegramForegroundRequestKind kind)
    {
        stage = TelegramUpdateStage.TelegramSend;
        switch (request)
        {
            case SendMessageRequest:
                kind = TelegramForegroundRequestKind.TextSend;
                return true;
            case SendPhotoRequest:
                kind = TelegramForegroundRequestKind.PhotoSend;
                return true;
            case SendMediaGroupRequest:
                kind = TelegramForegroundRequestKind.MediaGroup;
                return true;
            case SendDocumentRequest:
                kind = TelegramForegroundRequestKind.DocumentUpload;
                return true;
            case AnswerCallbackQueryRequest:
                kind = TelegramForegroundRequestKind.CallbackAcknowledgement;
                return true;
            case DeleteMessageRequest:
                kind = TelegramForegroundRequestKind.DeleteMessage;
                return true;
            case EditMessageTextRequest:
            case EditMessageReplyMarkupRequest:
            case EditMessageMediaRequest:
            case EditMessageCaptionRequest:
                stage = TelegramUpdateStage.TelegramEdit;
                kind = TelegramForegroundRequestKind.MessageEdit;
                return true;
            case GetChatRequest:
                stage = TelegramUpdateStage.TelegramMembership;
                kind = TelegramForegroundRequestKind.ChatLookup;
                return true;
            case GetChatMemberRequest:
            case GetChatMemberCountRequest:
            case GetChatAdministratorsRequest:
                stage = TelegramUpdateStage.TelegramMembership;
                kind = TelegramForegroundRequestKind.MembershipLookup;
                return true;
            default:
                stage = default;
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// Produces a closed-vocabulary kind for the abandoned request so the typed timeout carries no customer data.
    /// </summary>
    /// <typeparam name="TResponse">SDK response type carried by the abandoned request.</typeparam>
    /// <param name="request">Request that exceeded the budget.</param>
    /// <returns>A stable explicit snake-case constant compatible with the existing typed timeout contract.</returns>
    /// <remarks>The caller has already classified the request as foreground; unknown requests never reach this mapping.</remarks>
    /// <example><code>throw new TelegramForegroundDeliveryTimeoutException(DescribeRequestKind(request), overallBudget);</code></example>
    private static string DescribeRequestKind<TResponse>(IRequest<TResponse> request) => request switch
    {
        SendMessageRequest => "send_message",
        SendPhotoRequest => "send_photo",
        SendMediaGroupRequest => "send_media_group",
        SendDocumentRequest => "send_document",
        AnswerCallbackQueryRequest => "answer_callback_query",
        DeleteMessageRequest => "delete_message",
        EditMessageTextRequest => "edit_message_text",
        EditMessageReplyMarkupRequest => "edit_message_reply_markup",
        EditMessageMediaRequest => "edit_message_media",
        EditMessageCaptionRequest => "edit_message_caption",
        GetChatRequest => "get_chat",
        GetChatMemberRequest => "get_chat_member",
        GetChatMemberCountRequest => "get_chat_member_count",
        GetChatAdministratorsRequest => "get_chat_administrators",
        _ => "foreground_request"
    };
}
