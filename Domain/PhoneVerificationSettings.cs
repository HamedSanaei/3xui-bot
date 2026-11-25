namespace Adminbot.Domain;

/// <summary>Current phone requirement shared by the owned sales bots.</summary>
/// <param name="Enabled">Whether owned customers need a verified number for entry and phone-gated actions.</param>
/// <param name="Revision">Process-local version included in administrative buttons to reject outdated changes.</param>
public sealed record PhoneVerificationSnapshot(bool Enabled, Guid Revision);

/// <summary>Stores the live phone requirement for owned sales bots in the runtime JSON configuration.</summary>
/// <remarks>Defaults to required. The setting never writes the shared Telegram user profile or changes tenant policy.</remarks>
public sealed class PhoneVerificationSettings
{
    /// <summary>Root boolean written in the writable runtime configuration.</summary>
    public const string ConfigurationPropertyName = "ownedPhoneVerificationRequired";

    private readonly string _configurationPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private PhoneVerificationSnapshot _snapshot;

    /// <summary>Initializes the host-wide owned-bot preference from startup configuration.</summary>
    /// <param name="configuration">Required bound startup options. With no saved value, verification is required.</param>
    /// <param name="configurationPath">Required path to the host's writable runtime configuration JSON.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PhoneVerificationSettings(AppConfig configuration, string configurationPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configurationPath = Path.GetFullPath(configurationPath);
        // Seed each host run separately so pre-restart buttons cannot overwrite the restored state.
        _snapshot = new(configuration.OwnedPhoneVerificationRequired, Guid.NewGuid());
    }

    /// <summary>Current immutable preference. Capture it once when rendering a panel and its button.</summary>
    public PhoneVerificationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Resolves whether a bot's existing phone checks should require a verified number.</summary>
    /// <param name="botType">Type from the active bot runtime. Unknown types retain the existing requirement.</param>
    /// <returns>False only for an owned bot when the shared owned preference is disabled.</returns>
    /// <remarks>Tenant storefronts keep their own existing checks even when the same Telegram user visits an owned bot.</remarks>
    /// <example><code>if (settings.IsRequiredFor(BotContextAccessor.CurrentBotType) &amp;&amp; string.IsNullOrWhiteSpace(profile.PhoneNumber)) return;</code></example>
    public bool IsRequiredFor(string botType) =>
        Snapshot.Enabled || !string.Equals(botType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase);

    /// <summary>Persists an explicit target state and activates it after the configuration write succeeds.</summary>
    /// <param name="enabled">Desired requirement for every owned sales bot.</param>
    /// <param name="expectedRevision">Revision read from the authorized super-admin's inline button.</param>
    /// <param name="cancellationToken">Optional cancellation of the serialized write.</param>
    /// <returns>True when the target is already current or saved, and false for an outdated button or failed write.</returns>
    /// <remarks>Call after authorizing the sender. The shared editor serializes concurrent root-property writes and preserves unrelated JSON bytes.</remarks>
    /// <exception cref="OperationCanceledException">The operation is cancelled while waiting or before persistence completes.</exception>
    /// <example><code>var saved = await settings.SetRequiredAsync(false, settings.Snapshot.Revision, token);</code></example>
    public async Task<bool> SetRequiredAsync(bool enabled, Guid expectedRevision, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = Snapshot;
            if (current.Revision != expectedRevision)
                return false;
            if (current.Enabled == enabled)
                return true;

            // Publish only after the shared editor has durably updated configuration.
            await RootBooleanJsonFileEditor.SetAsync(_configurationPath, ConfigurationPropertyName, enabled, cancellationToken);
            Volatile.Write(ref _snapshot, new(enabled, Guid.NewGuid()));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
