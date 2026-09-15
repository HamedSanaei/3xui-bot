using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;

/// <summary>
/// Routes output through the shared sender while preserving real responses for result-dependent callers.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// Telegram.Bot v22 exposes one generic <see cref="ITelegramBotClient.SendRequest{TResponse}"/> entry point, and
/// every convenience method such as <c>SendMessage</c> builds a strongly typed request and calls it. That
/// makes one small decorator a complete chokepoint for the interactive surface without touching hundreds of call
/// sites. Only explicit admission-only reply builders return before network delivery.
/// </para>
/// <para>
/// What is bounded:
/// Text and keyboard output is snapshotted into SQLite; stream-backed sends are tracked live while their caller owns
/// the stream. Callback acknowledgements alone bypass ordinary send ordering. Metadata, polling and downloads use
/// separate finite deadlines. No fake Telegram message ids are returned to workflows.
/// </para>
/// <para>
/// Ambiguous sends:
/// Sender timeouts become <see cref="TelegramDeliveryUncertainException"/> and are never automatically replayed.
/// Clients without a sender retain the local <see cref="TelegramForegroundDeliveryTimeoutException"/> policy.
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

    /// <summary>Immutable overall budget applied to each bounded interactive delivery.</summary>
    private readonly TelegramForegroundDeliveryPolicy _policy;
    private readonly TelegramSenderService _sender;
    private readonly string _botId;
    private readonly Adminbot.Domain.TelegramWorkPriority _priority;

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
    /// <param name="sender">Optional durable output scheduler; missing only in isolated clients and existing fixtures.</param>
    /// <param name="botId">Internal runtime bot id required with the sender, never a token.</param>
    /// <param name="priority">Critical for existing durable notification workers; normal for update replies.</param>
    public ForegroundBoundedTelegramBotClient(ITelegramBotClient inner, TelegramForegroundDeliveryPolicy policy,
        TelegramSenderService sender = null, string botId = null,
        Adminbot.Domain.TelegramWorkPriority priority = Adminbot.Domain.TelegramWorkPriority.Normal)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sender = sender;
        _botId = botId;
        _priority = priority;
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
    public async Task<bool> TestApi(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.OverallBudget);
        return await _inner.TestApi(timeout.Token);
    }

    /// <inheritdoc />
    public Task DownloadFile(Telegram.Bot.Types.TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => DownloadFile(file.FilePath, destination, cancellationToken);

    /// <summary>
    /// Downloads a Telegram file with a separate finite two-minute transfer deadline.
    /// </summary>
    /// <param name="filePath">Telegram file path returned by <c>GetFile</c>.</param>
    /// <param name="destination">Destination stream owned by the caller.</param>
    /// <param name="cancellationToken">Caller cancellation, linked to the transfer deadline.</param>
    /// <returns>A task completing after the file has been copied into <paramref name="destination"/>.</returns>
    /// <remarks>
    /// File transfer is size-driven and uses a longer deadline than text send/edit; it never waits indefinitely.
    /// </remarks>
    public async Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await _inner.DownloadFile(filePath, destination, timeout.Token);
    }

    /// <summary>
    /// Executes one Telegram request, applying the foreground delivery budget only to interactive UX request kinds.
    /// </summary>
    /// <typeparam name="TResponse">Response type required by the Telegram request.</typeparam>
    /// <param name="request">Request built by the handler; only its runtime type is inspected.</param>
    /// <param name="cancellationToken">The caller's own lane cancellation token.</param>
    /// <returns>The inner client's response for the request.</returns>
    /// <remarks>
    /// Supported output enters the shared per-bot sender. Explicit menu builders await durable admission only;
    /// other callers receive the real response. Only AnswerCallbackQuery bypasses ordinary output ordering.
    /// Without sender injection, the existing local foreground timeout remains in effect.
    /// </remarks>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">
    /// The overall interactive delivery budget expired before Telegram answered.
    /// </exception>
    public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (_sender != null && request is AnswerCallbackQueryRequest answer)
            return (TResponse)(object)_sender.QueueAcknowledgement(_botId, answer);
        if (_sender != null && TelegramSenderService.Supports(request))
            return await _sender.EnqueueAsync(_botId, request, TelegramQueuedDelivery.AdmissionOnly, cancellationToken, _priority);
        if (_sender != null && request is SendPhotoRequest or SendDocumentRequest or SendMediaGroupRequest
            or EditMessageMediaRequest or EditMessageCaptionRequest)
            return await _sender.SendLiveAsync(_botId, request, cancellationToken, _priority);

        if (!TryClassifyForegroundRequest(request, out var stage))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request is GetUpdatesRequest ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30));
            return await _inner.SendRequest(request, timeout.Token);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_policy.OverallBudget);
        using var measurement = TelegramUpdateLatencyScope.Current?.Measure(stage) ?? default;

        try
        {
            return await _inner.SendRequest(request, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            // Only our own budget expired: the customer's lane is still live, so report a typed interactive-delivery
            // timeout instead of a shutdown cancellation. The send is abandoned and never replayed automatically.
            throw new TelegramForegroundDeliveryTimeoutException(DescribeRequestKind(request), _policy.OverallBudget);
        }
    }

    /// <summary>
    /// Classifies a Telegram request as an interactive foreground UX call with its latency stage.
    /// </summary>
    /// <param name="request">Request instance built by the handler.</param>
    /// <param name="stage">Receives the closed-vocabulary stage for a bounded request.</param>
    /// <returns>
    /// <c>true</c> when the request is an interactive UX call that must respect the foreground budget; otherwise
    /// <c>false</c> so the request is delegated untouched.
    /// </returns>
    /// <remarks>
    /// The mapping is a fixed compile-time list. Unknown request kinds — including receiver long polling, webhook
    /// management, and file transfer — are never bounded, so this decorator cannot alter runtime or durable behavior.
    /// </remarks>
    private static bool TryClassifyForegroundRequest<TResponse>(IRequest<TResponse> request, out TelegramUpdateStage stage)
    {
        switch (request)
        {
            case SendMessageRequest:
            case SendPhotoRequest:
            case SendMediaGroupRequest:
            case SendDocumentRequest:
            case AnswerCallbackQueryRequest:
            case DeleteMessageRequest:
                stage = TelegramUpdateStage.TelegramSend;
                return true;

            case EditMessageTextRequest:
            case EditMessageReplyMarkupRequest:
            case EditMessageMediaRequest:
            case EditMessageCaptionRequest:
                stage = TelegramUpdateStage.TelegramEdit;
                return true;

            case GetChatRequest:
            case GetChatMemberRequest:
            case GetChatMemberCountRequest:
            case GetChatAdministratorsRequest:
                stage = TelegramUpdateStage.TelegramMembership;
                return true;

            default:
                stage = default;
                return false;
        }
    }

    /// <summary>
    /// Produces a closed-vocabulary kind for the abandoned request so the typed timeout carries no customer data.
    /// </summary>
    /// <param name="request">Request that exceeded the budget.</param>
    /// <returns>A stable snake-case kind derived from the request type; never a chat id, payload, or token.</returns>
    private static string DescribeRequestKind<TResponse>(IRequest<TResponse> request)
    {
        var name = request.GetType().Name;
        if (name.EndsWith("Request", StringComparison.Ordinal))
            name = name[..^"Request".Length];

        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (index > 0 && char.IsUpper(current))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
