using System;
using System.Threading;

namespace Adminbot.Services.Telemetry;

/// <summary>Identifies the actual cancellation owner without changing any Telegram deadline or token.</summary>
/// <remarks>Only token state is retained. Nested callback and foreground scopes preserve the original caller token; no request content is stored.</remarks>
public sealed class TelegramRequestCancellationScope : IDisposable
{
    /// <summary>Cancellation metadata for the current asynchronous request.</summary>
    private static readonly AsyncLocal<TelegramRequestCancellationScope> Ambient = new();
    /// <summary>Enclosing metadata restored after the awaited operation.</summary>
    private readonly TelegramRequestCancellationScope _previous;
    /// <summary>Original application-owned cancellation, before local budgets were linked.</summary>
    private readonly CancellationToken _caller;
    /// <summary>Existing interactive delivery deadline token.</summary>
    private readonly CancellationToken _foreground;
    /// <summary>Existing best-effort callback acknowledgement deadline token.</summary>
    private readonly CancellationToken _callback;
    /// <summary>Existing bounded startup/command initialization deadline token.</summary>
    private readonly CancellationToken _startup;
    /// <summary>Prevents repeated disposal from restoring stale metadata.</summary>
    private bool _disposed;

    /// <summary>Gets metadata associated with the current awaited request, or null outside a policy.</summary>
    public static TelegramRequestCancellationScope Current => Ambient.Value;
    /// <summary>Gets a fixed cancellation owner; simultaneous shutdown takes precedence over local deadlines.</summary>
    public string CancellationSource => _caller.IsCancellationRequested ? "caller" :
        _callback.IsCancellationRequested ? "callback_policy" : _foreground.IsCancellationRequested ? "foreground_budget" :
        _startup.IsCancellationRequested ? "startup_probe" : "none";
    /// <summary>Gets the local deadline category, or none when cancellation is not deadline-owned.</summary>
    public string TimeoutCategory => CancellationSource switch
    {
        "callback_policy" => "callback_best_effort",
        "foreground_budget" => "foreground",
        "startup_probe" => "startup_probe",
        _ => "none"
    };

    /// <summary>Captures existing policy tokens and installs their provenance for one asynchronous operation.</summary>
    /// <param name="caller">Original caller token, never a newly created deadline.</param>
    /// <param name="budget">Existing policy-linked deadline token; this scope never cancels it.</param>
    /// <param name="kind">Fixed policy kind: zero foreground, one callback acknowledgement, two startup probe.</param>
    /// <remarks>Nested foreground calls preserve the callback policy's original caller rather than its linked token.</remarks>
    private TelegramRequestCancellationScope(CancellationToken caller, CancellationToken budget, int kind)
    {
        _previous = Ambient.Value;
        _caller = _previous?._caller ?? caller;
        _callback = kind == 1 ? budget : _previous?._callback ?? default;
        _foreground = kind == 0 ? budget : _previous?._foreground ?? default;
        _startup = kind == 2 ? budget : _previous?._startup ?? default;
        Ambient.Value = this;
    }

    /// <summary>Publishes provenance for an existing interactive delivery deadline.</summary>
    /// <param name="caller">Caller-owned lane token passed to the decorator.</param>
    /// <param name="budget">Existing foreground-linked deadline token.</param>
    /// <returns>A scope disposed after request completion recording.</returns>
    /// <remarks>Does not alter budgets, exception translation or retries.</remarks>
    /// <example><code>using var metadata = TelegramRequestCancellationScope.PushForeground(caller, budget.Token);</code></example>
    public static TelegramRequestCancellationScope PushForeground(CancellationToken caller, CancellationToken budget)
        => new(caller, budget, 0);

    /// <summary>Publishes provenance for the existing best-effort callback acknowledgement policy.</summary>
    /// <param name="caller">Original lane token passed to the callback policy.</param>
    /// <param name="budget">Existing two-second policy-linked token, or the existing injected test deadline.</param>
    /// <returns>A scope disposed when the callback policy finishes.</returns>
    /// <remarks>A policy deadline is not caller cancellation even when a nested decorator receives its token.</remarks>
    /// <example><code>using var metadata = TelegramRequestCancellationScope.PushCallbackPolicy(caller, bounded.Token);</code></example>
    public static TelegramRequestCancellationScope PushCallbackPolicy(CancellationToken caller, CancellationToken budget)
        => new(caller, budget, 1);

    /// <summary>Publishes provenance for the existing bounded startup/command initialization probe.</summary>
    /// <param name="caller">Original host or owner-action cancellation token.</param>
    /// <param name="budget">Existing configured five-to-sixty-second startup-linked token.</param>
    /// <returns>A scope disposed after the SDK probe completes.</returns>
    /// <remarks>Does not change optimistic receiver registration or startup recovery decisions.</remarks>
    /// <example><code>using var metadata = TelegramRequestCancellationScope.PushStartupProbe(caller, probe.Token);</code></example>
    public static TelegramRequestCancellationScope PushStartupProbe(CancellationToken caller, CancellationToken budget)
        => new(caller, budget, 2);

    /// <summary>Restores the enclosing cancellation metadata without cancelling or disposing any caller token.</summary>
    /// <remarks>Called by the policy's using statement after its completion metadata has been captured.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ambient.Value = _previous;
    }
}
