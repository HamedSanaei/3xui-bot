using System.Buffers;
using System.Net;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Result of a single safe endpoint read, without tokens, response text or URLs.</summary>
/// <param name="Failure">Closed failure category; None means the expected identity or root was verified.</param>
public sealed record TelegramEndpointProbeResult(TelegramEndpointFailure Failure = TelegramEndpointFailure.None)
{
    /// <summary>Whether this read verified the expected server or bot.</summary>
    public bool Success => Failure == TelegramEndpointFailure.None;
}

/// <summary>Result of exactly one irreversible bot-specific logout request.</summary>
/// <param name="Acknowledged">Whether Telegram explicitly acknowledged the logout.</param>
/// <param name="Uncertain">Whether the request may have taken effect and must never be replayed.</param>
/// <param name="Failure">Closed rejection or ambiguous-failure category; no external text is retained.</param>
public sealed record TelegramEndpointLogoutResult(bool Acknowledged, bool Uncertain,
    TelegramEndpointFailure Failure = TelegramEndpointFailure.None);

/// <summary>Non-retrying, bot-specific endpoint protocol used by the durable coordinator.</summary>
/// <remarks>Local identity reads are session-creating; callers must prove prior Cloud logout or an already-local session.</remarks>
public interface ITelegramEndpointProtocol
{
    /// <summary>Checks shared loopback reachability without a bot token or session-creating API call.</summary>
    /// <param name="cancellationToken">Required caller-owned bounded operation budget.</param>
    /// <returns>Verified official root response or a typed failure; no bot session is touched.</returns>
    Task<TelegramEndpointProbeResult> ProbeLocalServerAsync(CancellationToken cancellationToken);

    /// <summary>Verifies the exact configured BotFather identity on one selected endpoint.</summary>
    /// <param name="botId">Exact internal registry id; never defaults to another bot.</param>
    /// <param name="identity">Expected positive numeric BotFather bot id, not a chat or user id.</param>
    /// <param name="endpoint">Endpoint explicitly authorized by the current durable migration state.</param>
    /// <param name="generation">Current positive runtime generation associated with this control request.</param>
    /// <param name="cancellationToken">Caller-owned bounded identity-read budget.</param>
    /// <returns>Success only for the expected bot identity, otherwise a safe typed failure.</returns>
    Task<TelegramEndpointProbeResult> VerifyIdentityAsync(string botId, long identity,
        TelegramEndpointType endpoint, long generation, CancellationToken cancellationToken);

    /// <summary>Issues exactly one logout request after a durable attempt marker has committed.</summary>
    /// <param name="botId">Exact internal registry id of the session being removed.</param>
    /// <param name="identity">Required positive BotFather identity frozen by the durable intent; replaced tokens cannot log out another bot.</param>
    /// <param name="endpoint">Source endpoint; close is never substituted for logOut.</param>
    /// <param name="generation">Current source generation for endpoint telemetry.</param>
    /// <param name="cancellationToken">Caller-owned bounded mutation budget; cancellation after dispatch is uncertain.</param>
    /// <returns>Explicit acknowledgement, definitive refusal or uncertainty; callers must never replay uncertainty.</returns>
    Task<TelegramEndpointLogoutResult> LogOutOnceAsync(string botId, long identity, TelegramEndpointType endpoint,
        long generation, CancellationToken cancellationToken);
}

/// <summary>Production protocol using pooled control SDK clients and a tokenless, bounded root HTTP read.</summary>
/// <remarks>Never edits a shared server, calls close, drops updates, retries logout or logs external response content.</remarks>
public sealed class TelegramEndpointProtocol : ITelegramEndpointProtocol, IDisposable
{
    /// <summary>Exact identity-bound shared SDK client provider; clients remain provider-owned.</summary>
    private readonly BotClientProvider _clients;
    /// <summary>Single root-only HTTP client; no bot token is ever added to its requests.</summary>
    private readonly HttpClient _rootClient;
    /// <summary>Validated loopback root with no path, query, userinfo or fragment.</summary>
    private readonly Uri _localRoot;
    /// <summary>Whether this protocol owns the tokenless HTTP client's lifetime.</summary>
    private readonly bool _ownsClient;

    /// <summary>Creates the non-retrying endpoint control transport.</summary>
    /// <param name="clients">Required exact bot provider; no default-bot fallback is permitted.</param>
    /// <param name="options">Validated endpoint configuration; production URLs are not test substitution points.</param>
    /// <param name="rootClient">Optional controlled HTTP transport for deterministic tests, never an unsafe configured URL.</param>
    /// <remarks>The caller supplies the budget for every operation; the default HTTP client has no independent timeout.</remarks>
    /// <exception cref="ArgumentNullException">The provider or options is missing.</exception>
    /// <example><code>var protocol = new TelegramEndpointProtocol(clients, endpointOptions);</code></example>
    public TelegramEndpointProtocol(BotClientProvider clients, TelegramEndpointRoutingOptions options, HttpClient rootClient = null)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _localRoot = new Uri((options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot().LocalBaseUrl);
        _ownsClient = rootClient == null;
        _rootClient = rootClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            UseCookies = false
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc />
    public async Task<TelegramEndpointProbeResult> ProbeLocalServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _localRoot);
            using var response = await _rootClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound)
                return new(TelegramEndpointFailure.InvalidResponse);
            // A root 404 alone could be any web server. Verify a bounded official Bot API JSON envelope.
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = ArrayPool<byte>.Shared.Rent(4097);
            try
            {
                var count = 0;
                while (count < 4097)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count, 4097 - count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0 || count > 4096) return new(TelegramEndpointFailure.InvalidResponse);
                using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
                var root = json.RootElement;
                return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) &&
                    ok.ValueKind == JsonValueKind.False && root.TryGetProperty("error_code", out var code) &&
                    code.TryGetInt32(out var value) && value == 404
                    ? new() : new(TelegramEndpointFailure.InvalidResponse);
            }
            finally { ArrayPool<byte>.Shared.Return(bytes); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JsonException) { return new(TelegramEndpointFailure.InvalidResponse); }
        catch (Exception exception) { return new(TelegramEndpointHealthPolicy.Classify(exception)); }
    }

    /// <inheritdoc />
    public async Task<TelegramEndpointProbeResult> VerifyIdentityAsync(string botId, long identity,
        TelegramEndpointType endpoint, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var me = await _clients.CreateEndpointControlClient(botId, endpoint, generation, identity).GetMe(cancellationToken);
            return me.IsBot && me.Id == identity ? new() : new(TelegramEndpointFailure.IdentityMismatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BotTransportUnavailableException) { return new(TelegramEndpointFailure.ConfigurationMissing); }
        catch (Exception exception) { return new(TelegramEndpointHealthPolicy.Classify(exception)); }
    }

    /// <inheritdoc />
    public async Task<TelegramEndpointLogoutResult> LogOutOnceAsync(string botId, long identity, TelegramEndpointType endpoint,
        long generation, CancellationToken cancellationToken)
    {
        try
        {
            var acknowledged = await _clients.CreateEndpointControlClient(botId, endpoint, generation, identity)
                .SendRequest(new LogOutRequest(), cancellationToken);
            return acknowledged ? new(true, false) : new(false, false, TelegramEndpointFailure.LogoutRefused);
        }
        catch (ApiRequestException exception) when (exception.ErrorCode is >= 400 and < 500)
        {
            var failure = TelegramEndpointHealthPolicy.Classify(exception);
            return new(false, false, failure == TelegramEndpointFailure.InvalidResponse
                ? TelegramEndpointFailure.LogoutRefused : failure);
        }
        catch (BotTransportUnavailableException) { return new(false, false, TelegramEndpointFailure.ConfigurationMissing); }
        catch (Exception) { return new(false, true, TelegramEndpointFailure.LogoutUncertain); }
    }

    /// <summary>Disposes only the protocol-owned tokenless HTTP transport, never shared bot clients.</summary>
    public void Dispose()
    {
        if (_ownsClient) _rootClient.Dispose();
    }
}
