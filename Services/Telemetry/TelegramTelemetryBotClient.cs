using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;

namespace Adminbot.Services.Telemetry;

/// <summary>Uses the unchanged v22 SDK pipeline while observing fully validated API and polling outcomes.</summary>
/// <remarks>
/// The installed 22.10.3.2 SDK invokes OnApiResponseReceived before response deserialization and Ok validation.
/// Therefore only successful completion of base.SendRequest establishes a healthy poll, including an empty array.
/// This subclass never reads response bodies, changes request data, retries, or translates SDK exceptions.
/// </remarks>
internal sealed class TelegramTelemetryBotClient : TelegramBotClient
{
    /// <summary>Canonical internal registry id, independent of the token-bearing API URL.</summary>
    private readonly string _botId;
    /// <summary>Gets the canonical registry id for metadata from unscoped foreground calls.</summary>
    internal string CanonicalBotId => _botId;
    /// <summary>Shared nonblocking telemetry writer.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Gets whether cancellation provenance is needed for this cached client.</summary>
    internal bool IsTelemetryEnabled => _telemetry?.Enabled == true;
    /// <summary>Active-receiver health tracker shared with the runtime manager.</summary>
    private readonly TelegramPollingTelemetryTracker _polling;
    /// <summary>Monotonic diagnostic clock, independent of real SDK transport deadlines.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one cached SDK client using the provider's pooled HTTP transport.</summary>
    /// <param name="options">Existing SDK options with RetryCount zero; token is never retained by diagnostics.</param>
    /// <param name="httpClient">HTTP client created once per cached bot, sharing the provider's socket pool.</param>
    /// <param name="botId">Canonical internal registry id for all diagnostic records.</param>
    /// <param name="telemetry">Optional shared writer; null leaves the SDK path untouched.</param>
    /// <param name="polling">Provider-owned active receiver tracker.</param>
    /// <param name="timeProvider">Optional controlled monotonic diagnostic clock; null uses TimeProvider.System without changing HTTP timeouts.</param>
    /// <remarks>The provider owns transport lifetime; callers must not dispose or recreate transports per request.</remarks>
    internal TelegramTelemetryBotClient(TelegramBotClientOptions options, HttpClient httpClient, string botId,
        LatencyTelemetryService telemetry, TelegramPollingTelemetryTracker polling, TimeProvider timeProvider = null) : base(options, httpClient)
    {
        _botId = botId;
        _telemetry = telemetry;
        _polling = polling;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Measures the complete SDK request, including response buffering/deserialization and API validation.</summary>
    /// <typeparam name="TResponse">Unchanged SDK response type.</typeparam>
    /// <param name="request">SDK request; only a whitelisted MethodName is retained.</param>
    /// <param name="cancellationToken">Original caller token whose cancellation must remain distinct from HTTP timeout.</param>
    /// <returns>The original SDK-validated response, including an empty updates array; no payload is copied.</returns>
    /// <remarks>HTTP header-only observations are a separate record family and must not be summed with this duration. Long polling is classified separately and never a slow foreground operation.</remarks>
    /// <exception cref="Exception">Original SDK/API/transport exceptions are rethrown unchanged.</exception>
    /// <example><code>var result = await client.SendRequest(request, cancellationToken);</code></example>
    public override Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => _telemetry?.Enabled != true || LatencyTelemetrySuppression.IsActive
            ? base.SendRequest(request, cancellationToken)
            : SendMeasuredRequestAsync(request, cancellationToken);

    /// <summary>Records one enabled SDK request around the original full SDK await.</summary>
    /// <typeparam name="TResponse">Unchanged SDK response type.</typeparam>
    /// <param name="request">Original SDK request; only its fixed method label is inspected.</param>
    /// <param name="cancellationToken">Original caller token before the SDK links its transport timeout.</param>
    /// <returns>The unchanged SDK-validated response; exceptions retain their original identity.</returns>
    /// <remarks>
    /// The disabled/suppressed entry path never creates a context, event or additional async state machine.
    /// An unpersisted admin-control scope is measured here because its client intentionally bypasses the
    /// foreground decorator. Ordinary persisted handlers remain measured only by that decorator.
    /// </remarks>
    private async Task<TResponse> SendMeasuredRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        var method = TelegramTransportDiagnostics.Method(request == null ? default : request.MethodName.AsSpan());
        using var context = TelegramApiRequestContext.Push(method, cancellationToken);
        var scope = TelegramUpdateLatencyScope.Current;
        var receiver = UpdateTelemetryTracker.Current;
        var adminRequest = default(TelegramUpdateLatencyScope.TelegramRequestTimer);
        var adminStage = default(TelegramUpdateLatencyScope.StageTimer);
        if (scope?.Sequence == 0 && ForegroundBoundedTelegramBotClient.TryClassifyForegroundRequest(
            request, out var stage, out var kind))
        {
            adminStage = scope.Measure(stage);
            adminRequest = scope.MeasureTelegramRequest(kind);
        }
        var started = _timeProvider.GetTimestamp();
        Exception failure = null;
        try
        {
            return await base.SendRequest(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var elapsedMs = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
            var diagnosis = TelegramTransportDiagnostics.Classify(failure, context.HttpStatusCode, cancellationToken);
            var requestOutcome = failure == null ? TelegramForegroundRequestOutcome.Completed :
                diagnosis.ApiErrorCode.HasValue ? TelegramForegroundRequestOutcome.TelegramApiError :
                diagnosis.CancellationSource == "caller" ? TelegramForegroundRequestOutcome.CallerCancellation :
                diagnosis.FailureClassification == "unexpected" ? TelegramForegroundRequestOutcome.UnexpectedError :
                TelegramForegroundRequestOutcome.TransportError;
            adminRequest.Complete(requestOutcome, diagnosis.ApiErrorCode);
            adminStage.Dispose();
            _telemetry.TryRecord(diagnosis with
            {
                EventType = "telegram_api_request_completed", BotId = _botId, TraceId = scope?.TraceId ?? receiver?.TraceId,
                UpdateId = scope?.UpdateId ?? receiver?.UpdateId,
                Sequence = scope != null ? (scope.Sequence > 0 ? scope.Sequence : null) : receiver?.Sequence,
                Method = method, Category = TelegramTransportDiagnostics.Category(method),
                Stage = method == "getUpdates" ? "telegram_polling" : "telegram_api", Operation = "sdk_validated_request",
                DurationMs = elapsedMs, Attempt = 1
            });
            if (method == "getUpdates")
            {
                if (failure == null) _polling.Success(_botId, elapsedMs);
                else _polling.Failure(_botId, elapsedMs, diagnosis);
            }
        }
    }
}

/// <summary>Measures headers-only HTTP transport timing without touching request/response bodies or retry policy.</summary>
/// <remarks>HttpClient performs response buffering after this handler returns; SDK validation occurs later still. An HTTP 200 here does not mean Telegram API success.</remarks>
internal sealed class TelegramTelemetryHttpHandler : DelegatingHandler
{
    /// <summary>Canonical internal bot id supplied by the registry, never parsed from URLs.</summary>
    private readonly string _botId;
    /// <summary>Optional nonblocking writer shared by all bot families.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Monotonic headers-only diagnostic clock, independent of HTTP cancellation.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a lightweight per-cached-client wrapper over the provider's shared socket handler.</summary>
    /// <param name="botId">Canonical configured bot id.</param>
    /// <param name="telemetry">Optional writer; null makes instrumentation a no-op.</param>
    /// <param name="transport">Provider-owned pooled handler, never created per request or disposed by this wrapper.</param>
    /// <param name="timeProvider">Optional controlled monotonic diagnostic clock; null uses TimeProvider.System.</param>
    /// <remarks>The SDK's ordinary HttpClient timeout and three-minute pooled connection lifetime are preserved.</remarks>
    internal TelegramTelemetryHttpHandler(string botId, LatencyTelemetryService telemetry, HttpMessageHandler transport,
        TimeProvider timeProvider = null)
    {
        _botId = botId;
        _telemetry = telemetry;
        InnerHandler = transport;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Observes one transport attempt through response headers; body contents remain entirely SDK-owned.</summary>
    /// <param name="request">Original HTTP request, forwarded without reading URLs, headers or body.</param>
    /// <param name="cancellationToken">Existing HttpClient-linked token, never cancelled by instrumentation.</param>
    /// <returns>The unchanged original HTTP response.</returns>
    /// <remarks>Original exceptions are rethrown, with no retry. Unknown non-API downloads use one fixed method label.</remarks>
    /// <exception cref="Exception">Original socket, TLS, HTTP or cancellation failures are rethrown unchanged.</exception>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _telemetry?.Enabled != true || LatencyTelemetrySuppression.IsActive
            ? base.SendAsync(request, cancellationToken)
            : SendMeasuredAsync(request, cancellationToken);

    /// <summary>Records one enabled transport attempt through its response headers.</summary>
    /// <param name="request">Original request forwarded unchanged; its URL, headers and body are not read.</param>
    /// <param name="cancellationToken">Original HttpClient-linked transport token.</param>
    /// <returns>The original response, whose body remains owned by HttpClient and the SDK.</returns>
    /// <remarks>Disabled/suppressed requests bypass this async measurement entirely.</remarks>
    private async Task<HttpResponseMessage> SendMeasuredAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = TelegramApiRequestContext.Current;
        var method = context?.Method ?? "unknown";
        var scope = TelegramUpdateLatencyScope.Current;
        var receiver = UpdateTelemetryTracker.Current;
        var started = _timeProvider.GetTimestamp();
        Exception failure = null;
        int? status = null;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (context != null) context.HttpStatusCode = status;
            return response;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _telemetry.TryRecord(TelegramTransportDiagnostics.Classify(failure, status, context?.Caller ?? default) with
            {
                EventType = "telegram_request_completed", BotId = _botId, TraceId = scope?.TraceId ?? receiver?.TraceId,
                UpdateId = scope?.UpdateId ?? receiver?.UpdateId,
                Sequence = scope != null ? (scope.Sequence > 0 ? scope.Sequence : null) : receiver?.Sequence,
                Method = method, Category = TelegramTransportDiagnostics.Category(method), Stage = "telegram_http",
                Operation = "http_response_headers", DurationMs = _timeProvider.GetElapsedTime(started).TotalMilliseconds, Attempt = 1
            });
        }
    }
}

/// <summary>Correlates a single SDK call with its HTTP attempt without storing request data.</summary>
/// <remarks>Async-local state isolates concurrent bot requests. One fixed-size context is allocated only when telemetry is enabled.</remarks>
internal sealed class TelegramApiRequestContext : IDisposable
{
    /// <summary>Current SDK request metadata for the asynchronous HTTP handler.</summary>
    private static readonly AsyncLocal<TelegramApiRequestContext> Ambient = new();
    /// <summary>Enclosing request restored on scope exit.</summary>
    private readonly TelegramApiRequestContext _previous;
    /// <summary>Gets the currently awaited SDK request context, or null for downloads.</summary>
    internal static TelegramApiRequestContext Current => Ambient.Value;
    /// <summary>Whitelisted compile-time SDK method constant.</summary>
    internal string Method { get; }
    /// <summary>Original caller token, excluding HttpClient's linked timeout token.</summary>
    internal CancellationToken Caller { get; }
    /// <summary>Actual numeric response-header status, or null before headers arrive.</summary>
    internal int? HttpStatusCode { get; set; }

    /// <summary>Installs fixed-size request metadata for the SDK await.</summary>
    /// <param name="method">Whitelisted method constant.</param>
    /// <param name="caller">Original caller token.</param>
    /// <remarks>No payload, token or URL is retained.</remarks>
    private TelegramApiRequestContext(string method, CancellationToken caller)
    {
        _previous = Ambient.Value;
        Method = method;
        Caller = caller;
        Ambient.Value = this;
    }

    /// <summary>Creates ambient metadata that the shared transport can read across the SDK async call.</summary>
    /// <param name="method">Whitelisted method constant.</param>
    /// <param name="caller">Original caller token.</param>
    /// <returns>A context disposed after the SDK's full request completion is recorded.</returns>
    /// <remarks>Each concurrent request receives an isolated context.</remarks>
    /// <example><code>using var context = TelegramApiRequestContext.Push(method, cancellationToken);</code></example>
    internal static TelegramApiRequestContext Push(string method, CancellationToken caller) => new(method, caller);

    /// <summary>Restores the enclosing SDK metadata, retaining no completed request history.</summary>
    /// <remarks>The SDK override disposes exactly once after the completion event.</remarks>
    public void Dispose() => Ambient.Value = _previous;
}
