using System.Net.Sockets;
using Telegram.Bot.Exceptions;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Closed, secret-free failures used by endpoint health, durable history and telemetry.</summary>
public enum TelegramEndpointFailure
{
    /// <summary>The bounded operation succeeded.</summary>
    None,
    /// <summary>The loopback server refused a connection.</summary>
    ConnectionRefused,
    /// <summary>The operation exceeded its caller-owned budget.</summary>
    Timeout,
    /// <summary>A transport failure prevented a response.</summary>
    Network,
    /// <summary>The loopback root did not return the official Bot API response.</summary>
    InvalidResponse,
    /// <summary>Telegram explicitly rate-limited a request; this is not a local-server outage.</summary>
    RateLimited,
    /// <summary>Telegram returned an upstream server error; this is not a local-server outage.</summary>
    TelegramUpstream,
    /// <summary>Telegram authoritatively rejected the configured bot credentials.</summary>
    TokenRejected,
    /// <summary>The returned identity differs from the configured BotFather identity.</summary>
    IdentityMismatch,
    /// <summary>Telegram explicitly refused logout without acknowledging a session change.</summary>
    LogoutRefused,
    /// <summary>A sent logout has no definitive acknowledgement and must never be replayed.</summary>
    LogoutUncertain,
    /// <summary>The destination receiver did not prove readiness.</summary>
    ReceiverNotReady,
    /// <summary>The current bot or trusted local file mapping is unavailable.</summary>
    ConfigurationMissing
}

/// <summary>Immutable metadata-only reachability of the shared loopback Bot API process.</summary>
/// <param name="Reachable">Whether the latest tokenless root probe verified the official server response.</param>
/// <param name="LastCheckedAtUtc">Latest bounded probe time, or null before any probe.</param>
/// <param name="LastSuccessAtUtc">Latest verified root response time, or null if never verified.</param>
/// <param name="ErrorCategory">Closed failure code, or null after success; never response text or a URL.</param>
/// <param name="ConsecutiveFailures">Process-local consecutive tokenless root transport failures; successful reads reset this count.</param>
/// <param name="FirstFailureAtUtc">First UTC root failure in the current uninterrupted sequence; restart conservatively starts a new grace period.</param>
public sealed record TelegramEndpointSharedHealth(bool Reachable = false, DateTime? LastCheckedAtUtc = null,
    DateTime? LastSuccessAtUtc = null, string ErrorCategory = null, int ConsecutiveFailures = 0, DateTime? FirstFailureAtUtc = null);

/// <summary>Applies endpoint-specific failure classification and bounded retry arithmetic without network access.</summary>
public static class TelegramEndpointHealthPolicy
{
    /// <summary>Extracts safe numeric and closed-vocabulary diagnostic facts without retaining provider content.</summary>
    /// <param name="exception">Caught SDK/transport exception; descriptions are inspected only for fixed known reasons and are never returned or logged.</param>
    /// <returns>A secret-free probe result preserving existing retry classification, optional API error code and bounded RetryAfter seconds.</returns>
    /// <remarks>Unknown 400 responses remain generic bad_request rather than guessed credential/cooldown failures. This helper never retries, mutates sessions or changes financial state.</remarks>
    /// <example><code>var result = TelegramEndpointHealthPolicy.DescribeFailure(new ApiRequestException("Bad Request", 400));</code></example>
    public static TelegramEndpointProbeResult DescribeFailure(Exception exception)
    {
        var failure = Classify(exception);
        if (exception is not ApiRequestException api) return new(failure, ReasonCode: Code(failure));
        var reason = api.ErrorCode switch
        {
            400 when api.Message?.Contains("logged out", StringComparison.OrdinalIgnoreCase) == true => "telegram_logged_out",
            400 when api.Message?.Contains("not available", StringComparison.OrdinalIgnoreCase) == true => "telegram_method_unavailable",
            400 => "telegram_bad_request",
            404 => "telegram_not_found",
            401 or 403 => "telegram_token_rejected",
            409 => "telegram_conflict",
            429 => "telegram_rate_limited",
            >= 500 => "telegram_upstream",
            _ => "telegram_rejected"
        };
        var retry = api.Parameters?.RetryAfter;
        return new(failure, api.ErrorCode, reason, retry is > 0 ? Math.Min(retry.Value, 86400) : null);
    }

    /// <summary>Identifies failures that can count toward a sustained local-server outage.</summary>
    /// <param name="failure">Closed outcome from a tokenless root or an already-local identity probe.</param>
    /// <returns>True only for local transport, timeout or invalid-root failures; 429 and Telegram 5xx never count.</returns>
    /// <remarks>Credential and identity failures require explicit intervention rather than an automatic endpoint change.</remarks>
    public static bool IsServerOutage(TelegramEndpointFailure failure) => failure is
        TelegramEndpointFailure.ConnectionRefused or TelegramEndpointFailure.Timeout or
        TelegramEndpointFailure.Network or TelegramEndpointFailure.InvalidResponse;

    /// <summary>Maps a caught exception to a bounded category without inspecting or storing its message.</summary>
    /// <param name="exception">Transport or SDK exception from one bounded attempt.</param>
    /// <returns>A safe closed failure classification; unknown exceptions are transport failures.</returns>
    /// <remarks>Cancellation caused by host shutdown is handled by the caller before invoking this method.</remarks>
    public static TelegramEndpointFailure Classify(Exception exception)
    {
        if (exception is TimeoutException or OperationCanceledException) return TelegramEndpointFailure.Timeout;
        if (exception is ApiRequestException api)
            return api.ErrorCode switch
            {
                429 => TelegramEndpointFailure.RateLimited,
                >= 500 => TelegramEndpointFailure.TelegramUpstream,
                401 or 403 => TelegramEndpointFailure.TokenRejected,
                409 => TelegramEndpointFailure.ReceiverNotReady,
                _ => TelegramEndpointFailure.InvalidResponse
            };
        for (Exception current = exception; current != null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return TelegramEndpointFailure.ConnectionRefused;
        return TelegramEndpointFailure.Network;
    }

    /// <summary>Returns the stable persistence and telemetry spelling for a classified failure.</summary>
    /// <param name="failure">Closed classification produced by this endpoint subsystem.</param>
    /// <returns>A bounded lowercase code, or null for success; never exception or response text.</returns>
    public static string Code(TelegramEndpointFailure failure) => failure switch
    {
        TelegramEndpointFailure.None => null,
        TelegramEndpointFailure.ConnectionRefused => "connection_refused",
        TelegramEndpointFailure.Timeout => "timeout",
        TelegramEndpointFailure.Network => "network",
        TelegramEndpointFailure.InvalidResponse => "invalid_response",
        TelegramEndpointFailure.RateLimited => "rate_limited",
        TelegramEndpointFailure.TelegramUpstream => "telegram_upstream",
        TelegramEndpointFailure.TokenRejected => "token_rejected",
        TelegramEndpointFailure.IdentityMismatch => "identity_mismatch",
        TelegramEndpointFailure.LogoutRefused => "logout_refused",
        TelegramEndpointFailure.LogoutUncertain => "logout_uncertain",
        TelegramEndpointFailure.ReceiverNotReady => "receiver_not_ready",
        _ => "configuration_missing"
    };

    /// <summary>Calculates exponential safe-read recovery delay without overflow or a hot loop.</summary>
    /// <param name="attempt">One-based safe recovery attempt count; nonpositive values use the first step.</param>
    /// <param name="maximumSeconds">Validated positive configured ceiling in seconds.</param>
    /// <returns>A delay from 15 seconds up to the configured ceiling; it never authorizes replaying logout.</returns>
    /// <remarks>Logout is never retried by this policy. Only root, identity and destination-readiness reads may retry.</remarks>
    public static TimeSpan RetryDelay(int attempt, int maximumSeconds) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(1, maximumSeconds), 15L << Math.Clamp(attempt - 1, 0, 20)));
}
