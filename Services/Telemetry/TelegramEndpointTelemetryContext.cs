using System;
using System.Threading;
using Adminbot.Services.TelegramEndpoints;

namespace Adminbot.Services.Telemetry;

/// <summary>Captures the identity-free endpoint route for one actual Telegram request.</summary>
/// <remarks>Push only around an admitted SDK/control request, never around ReceiveAsync or its update callbacks. Immutable async-local metadata isolates concurrent generations and contains no URL, token, actor or raw state.</remarks>
public sealed class TelegramEndpointTelemetryContext : IDisposable
{
    /// <summary>Current request route, isolated by execution context.</summary>
    private static readonly AsyncLocal<TelegramEndpointTelemetryContext> Ambient = new();
    /// <summary>Enclosing route restored when the request finishes.</summary>
    private readonly TelegramEndpointTelemetryContext _previous;
    /// <summary>Gets the current actual-request route, or null when no route was supplied.</summary>
    public static TelegramEndpointTelemetryContext Current => Ambient.Value;
    /// <summary>Gets the closed cloud/local endpoint label for JSONL, not a server address.</summary>
    public string EndpointType { get; }
    /// <summary>Gets the positive, identity-scoped route generation admitted for this request.</summary>
    public long EndpointGeneration { get; }
    /// <summary>Gets the closed migration-state enum name captured at request admission.</summary>
    public string MigrationState { get; }

    /// <summary>Installs an immutable request-route snapshot.</summary>
    /// <param name="endpoint">Validated Cloud or Local enum from the runtime route.</param>
    /// <param name="generation">Positive generation from the runtime route, not a user identifier.</param>
    /// <param name="migrationState">Closed state from the runtime route.</param>
    private TelegramEndpointTelemetryContext(TelegramEndpointType endpoint, long generation, TelegramEndpointMigrationState migrationState)
    {
        EndpointType = endpoint switch
        {
            TelegramEndpointType.Cloud => "cloud",
            TelegramEndpointType.Local => "local",
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        EndpointGeneration = generation;
        MigrationState = migrationState switch
        {
            TelegramEndpointMigrationState.Cloud => "Cloud",
            TelegramEndpointMigrationState.CheckingLocal => "CheckingLocal",
            TelegramEndpointMigrationState.CloudLogoutPending => "CloudLogoutPending",
            TelegramEndpointMigrationState.CloudLogoutUncertain => "CloudLogoutUncertain",
            TelegramEndpointMigrationState.SwitchingToLocal => "SwitchingToLocal",
            TelegramEndpointMigrationState.Local => "Local",
            TelegramEndpointMigrationState.LocalDegraded => "LocalDegraded",
            TelegramEndpointMigrationState.LocalUnavailable => "LocalUnavailable",
            TelegramEndpointMigrationState.FallbackPending => "FallbackPending",
            TelegramEndpointMigrationState.LocalLogoutPending => "LocalLogoutPending",
            TelegramEndpointMigrationState.LocalLogoutUncertain => "LocalLogoutUncertain",
            TelegramEndpointMigrationState.CloudWait => "CloudWait",
            TelegramEndpointMigrationState.SwitchingToCloud => "SwitchingToCloud",
            TelegramEndpointMigrationState.CloudRecovered => "CloudRecovered",
            TelegramEndpointMigrationState.MigrationFailed => "MigrationFailed",
            TelegramEndpointMigrationState.ManualInterventionRequired => "ManualInterventionRequired",
            _ => throw new ArgumentOutOfRangeException(nameof(migrationState))
        };
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>Pushes metadata around one actual admitted request and restores the previous route on disposal.</summary>
    /// <param name="endpoint">Cloud or Local endpoint chosen by the identity-bound runtime gate.</param>
    /// <param name="generation">Positive per-bot route generation captured before sending.</param>
    /// <param name="migrationState">Closed state captured at request admission.</param>
    /// <returns>A disposable immutable context; dispose after the awaited request, before any receiver callback.</returns>
    /// <remarks>The caller owns route selection; telemetry never discovers endpoints from URLs or changes routing.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">An endpoint/state enum is undefined or generation is not positive.</exception>
    /// <example><code>using var route = TelegramEndpointTelemetryContext.Push(endpoint, generation, migrationState); await client.SendRequest(request, token);</code></example>
    public static TelegramEndpointTelemetryContext Push(TelegramEndpointType endpoint, long generation, TelegramEndpointMigrationState migrationState)
        => new(endpoint, generation, migrationState);

    /// <summary>Recognizes only nullable closed endpoint labels at the writer/report trust boundary.</summary>
    /// <param name="value">Optional untrusted serialized endpoint label.</param>
    /// <returns>True only for unavailable, cloud or local.</returns>
    internal static bool IsEndpointType(string value) => value is null or "cloud" or "local";

    /// <summary>Recognizes the version-one closed migration vocabulary without retaining raw state.</summary>
    /// <param name="value">Optional enum-name label from a producer or archive.</param>
    /// <returns>True only for unavailable or an explicitly permitted state.</returns>
    internal static bool IsMigrationState(string value) => value is null or "Cloud" or "CheckingLocal" or "CloudLogoutPending"
        or "CloudLogoutUncertain" or "SwitchingToLocal" or "Local" or "LocalDegraded" or "LocalUnavailable"
        or "FallbackPending" or "LocalLogoutPending" or "LocalLogoutUncertain" or "CloudWait" or "SwitchingToCloud"
        or "CloudRecovered" or "MigrationFailed" or "ManualInterventionRequired";

    /// <summary>Recognizes the closed operator/automatic migration trigger vocabulary.</summary>
    /// <param name="value">Optional serialized trigger; never an actor identifier or free-form explanation.</param>
    /// <returns>True only for unavailable or a supported trigger.</returns>
    internal static bool IsFailoverTrigger(string value) => value is null or "manual" or "automatic_outage" or "automatic_failback" or "startup_recovery";

    /// <summary>Restores the enclosing request route without retaining completed request history.</summary>
    /// <remarks>Dispose once in the same logical request scope, in reverse push order.</remarks>
    public void Dispose() => Ambient.Value = _previous;
}
