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
/// Decorates one bot client for a single Telegram update execution and bounds only that update's non-durable
/// interactive UX calls with <see cref="TelegramForegroundDeliveryPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// Telegram.Bot v19 exposes one generic <see cref="ITelegramBotClient.MakeRequestAsync{TResponse}"/> entry point, and
/// every convenience method such as <c>SendTextMessageAsync</c> builds a strongly typed request and calls it. That
/// makes one small decorator a complete chokepoint for the interactive surface without touching hundreds of call
/// sites, and without changing the shared transport timeout used by receivers, long polling, and durable workers.
/// </para>
/// <para>
/// What is bounded:
/// only explicitly listed interactive request kinds — message sends, photo/album/document sends, message edits,
/// message deletion, callback acknowledgements, and chat/membership lookups. Everything else, including
/// <c>getUpdates</c>, webhook management, and <see cref="DownloadFileAsync"/>, is delegated untouched so receiver
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

    /// <summary>Immutable overall budget applied to each bounded interactive delivery.</summary>
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
    public long? BotId => _inner.BotId;

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
    public Task<bool> TestApiAsync(CancellationToken cancellationToken = default)
        => _inner.TestApiAsync(cancellationToken);

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
    public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => _inner.DownloadFileAsync(filePath, destination, cancellationToken);

    /// <summary>
    /// Executes one Telegram request, applying the foreground delivery budget only to interactive UX request kinds.
    /// </summary>
    /// <typeparam name="TResponse">Response type required by the Telegram request.</typeparam>
    /// <param name="request">Request built by the handler; only its runtime type is inspected.</param>
    /// <param name="cancellationToken">The caller's own lane cancellation token.</param>
    /// <returns>The inner client's response for the request.</returns>
    /// <remarks>
    /// Non-interactive requests pass through unchanged. For interactive requests one linked token adds the overall
    /// budget, and a stage measurement reports elapsed time to the ambient
    /// <see cref="TelegramUpdateLatencyScope"/>. When only the budget expired — the caller's own token is still live —
    /// the typed <see cref="TelegramForegroundDeliveryTimeoutException"/> is raised and no retry is attempted. Caller
    /// cancellation and Telegram errors keep their original exception identity.
    /// </remarks>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">
    /// The overall interactive delivery budget expired before Telegram answered.
    /// </exception>
    public async Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (!TryClassifyForegroundRequest(request, out var stage))
            return await _inner.MakeRequestAsync(request, cancellationToken);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_policy.OverallBudget);
        using var measurement = TelegramUpdateLatencyScope.Current?.Measure(stage) ?? default;

        try
        {
            return await _inner.MakeRequestAsync(request, budget.Token);
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
