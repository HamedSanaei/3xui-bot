namespace Adminbot.Domain;

/// <summary>Immutable global permission for forwarding test-account acquisition audits to Telegram.</summary>
/// <param name="Enabled">Whether new free and paid trial audits may enter the logger channel; never gates account creation.</param>
/// <param name="Revision">Positive process-local revision used to reject stale administrator buttons.</param>
public sealed record TrialAccountLoggingSnapshot(bool Enabled, long Revision);

/// <summary>Result of a persist-before-publish trial logging change.</summary>
/// <param name="Applied">True when the requested state is durable or already current; false for stale buttons or write failures.</param>
/// <param name="Snapshot">Current immutable global state, including after a rejected change.</param>
/// <param name="Message">Safe Persian acknowledgement for the super-admin panel; contains no configuration values or secrets.</param>
public sealed record TrialAccountLoggingToggleResult(bool Applied, TrialAccountLoggingSnapshot Snapshot, string Message);

/// <summary>Stores the live global test-account logger preference in the existing runtime configuration file.</summary>
/// <remarks>
/// Shared by every owned and tenant bot. Missing configuration defaults to disabled. Only Telegram channel admission
/// changes: local diagnostics, issuance, financial settlement and payment backup intents remain independent.
/// Already queued channel messages are not removed. No logger dependency is used because the Telegram provider consumes this singleton.
/// </remarks>
public sealed class TrialAccountLoggingSettings
{
    /// <summary>Exact root boolean edited without rewriting unrelated configuration bytes.</summary>
    public const string ConfigurationPropertyName = "trialAccountLoggingEnabled";
    private readonly string _configurationPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TrialAccountLoggingSnapshot _snapshot;

    /// <summary>Initializes the shared preference from startup configuration without reading or writing production data.</summary>
    /// <param name="configuration">Required global startup options; a missing trial flag defaults to false.</param>
    /// <param name="configurationPath">Required absolute or content-root-relative path to the writable runtime configuration JSON.</param>
    /// <remarks>Use one singleton per host, not one per bot or update. Runtime consumers read Snapshot rather than AppConfig.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <example><code>var settings = new TrialAccountLoggingSettings(config, Path.Combine(contentRoot, "Data", "configuration.json"));</code></example>
    public TrialAccountLoggingSettings(AppConfig configuration, string configurationPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configurationPath = Path.GetFullPath(configurationPath ?? throw new ArgumentNullException(nameof(configurationPath)));
        _snapshot = new(configuration.TrialAccountLoggingEnabled, 1);
    }

    /// <summary>Current lock-free global state; capture once when rendering panel text and keyboard.</summary>
    public TrialAccountLoggingSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Persists an explicit target state before publishing it to channel loggers.</summary>
    /// <param name="enabled">Required desired global channel state, not a blind toggle; false suppresses only trial acquisition audits.</param>
    /// <param name="expectedRevision">Positive process-local revision rendered on the authorized super-admin's current panel.</param>
    /// <param name="cancellationToken">Cancellation of the serialized configuration write; defaults to no cancellation.</param>
    /// <returns>A nonnull result with the current snapshot and safe acknowledgement; a failed write leaves the live state unchanged.</returns>
    /// <remarks>
    /// Call only after checking the configured super-admin allow-list in an owned bot. Uses the shared root-boolean editor,
    /// so concurrent gateway, download and sales changes preserve each other's bytes. Duplicate targets do not increment the revision.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Cancellation occurs before the durable write completes.</exception>
    /// <example><code>var result = await settings.SetEnabledAsync(false, settings.Snapshot.Revision, token);</code></example>
    public async Task<TrialAccountLoggingToggleResult> SetEnabledAsync(bool enabled, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = Snapshot;
            if (expectedRevision != current.Revision)
                return new(false, current, "این پنل قدیمی شده است؛ وضعیت جدید نمایش داده شد.");
            if (enabled == current.Enabled)
                return new(true, current, "وضعیت لاگ اکانت تست از قبل همین مقدار بود.");

            // Publish only after the durable, byte-preserving configuration write succeeds.
            await RootBooleanJsonFileEditor.SetAsync(_configurationPath, ConfigurationPropertyName, enabled, cancellationToken);
            var next = new TrialAccountLoggingSnapshot(enabled, current.Revision + 1);
            Volatile.Write(ref _snapshot, next);
            return new(true, next, enabled ? "لاگ اکانت تست روشن شد و تنظیم ذخیره شد." : "لاگ اکانت تست خاموش شد و تنظیم ذخیره شد.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, Snapshot, "ذخیره تنظیم ناموفق بود؛ وضعیت لاگ اکانت تست تغییر نکرد.");
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
