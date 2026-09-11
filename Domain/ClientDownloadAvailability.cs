using System.Text;

namespace Adminbot.Domain;

/// <summary>
/// Stable identifiers for the client software download targets offered to customers.
/// </summary>
/// <remarks>
/// The set is compile-time closed. A callback payload can only ever select one of these three values, so no Telegram user
/// input can choose a GitHub owner, repository, tag, asset, or arbitrary external URL.
/// </remarks>
public enum ClientDownloadPlatform
{
    /// <summary>Android client, served from the official v2rayNG repository.</summary>
    Android,

    /// <summary>iOS client, served from the fixed V2Box App Store listing.</summary>
    Ios,

    /// <summary>Windows client, served from the official v2rayN repository.</summary>
    Windows
}

/// <summary>
/// Shared wire contract for the latest-client-software download menu in owned and tenant bots.
/// </summary>
/// <remarks>
/// Both bot families use these exact constants and this single parser, so a customer sees identical callbacks no matter
/// which storefront they are talking to and the two dispatchers cannot drift apart.
/// </remarks>
public static class ClientDownloadCallbacks
{
    /// <summary>Callback namespace shared by owned and tenant bots.</summary>
    public const string Prefix = "APPDL:";

    /// <summary>Reply-keyboard label that opens the platform selector.</summary>
    public const string OpenCommand = "📥 دریافت آخرین نسخه نرم‌افزار";

    /// <summary>Message shown when a customer asks for the menu while the feature is globally disabled.</summary>
    /// <remarks>
    /// A stale Telegram reply keyboard survives an administrator disabling the feature, so the text handler itself has to
    /// answer with this instead of silently doing nothing.
    /// </remarks>
    public const string DisabledMessage = "دریافت آخرین نسخه نرم‌افزار در حال حاضر غیرفعال است.";

    /// <summary>Builds the callback payload for one platform.</summary>
    /// <param name="platform">Compile-time known platform selected by the inline button.</param>
    /// <returns>A payload in <c>APPDL:&lt;platform&gt;</c> form using a lowercase invariant name.</returns>
    public static string Build(ClientDownloadPlatform platform)
        => Prefix + platform switch
        {
            ClientDownloadPlatform.Android => "android",
            ClientDownloadPlatform.Ios => "ios",
            ClientDownloadPlatform.Windows => "windows",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown download platform.")
        };

    /// <summary>
    /// Parses a callback payload into one of the three known platforms.
    /// </summary>
    /// <param name="data">Raw Telegram callback payload; null, empty, or any unknown value is rejected.</param>
    /// <param name="platform">Parsed platform when the payload is valid.</param>
    /// <returns><c>true</c> only for the three recognized payloads.</returns>
    /// <remarks>
    /// Parsing is a closed switch rather than a free-form parse, so a crafted payload cannot smuggle a path, URL, or
    /// repository name into the release lookup.
    /// </remarks>
    public static bool TryParse(string data, out ClientDownloadPlatform platform)
    {
        platform = default;
        if (string.IsNullOrWhiteSpace(data) || !data.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var value = data[Prefix.Length..].Trim();
        if (value.Equals("android", StringComparison.OrdinalIgnoreCase))
        {
            platform = ClientDownloadPlatform.Android;
            return true;
        }

        if (value.Equals("ios", StringComparison.OrdinalIgnoreCase))
        {
            platform = ClientDownloadPlatform.Ios;
            return true;
        }

        if (value.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            platform = ClientDownloadPlatform.Windows;
            return true;
        }

        return false;
    }

    /// <summary>Gets the customer-facing inline button label for one platform.</summary>
    /// <param name="platform">Platform whose label is rendered.</param>
    /// <returns>The exact label shown on the platform selector.</returns>
    public static string GetButtonLabel(ClientDownloadPlatform platform) => platform switch
    {
        ClientDownloadPlatform.Android => "🤖 Android - v2rayNG",
        ClientDownloadPlatform.Ios => "🍎 iOS - V2Box",
        ClientDownloadPlatform.Windows => "🪟 Windows - v2rayN",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown download platform.")
    };
}

/// <summary>
/// One resolved, customer-presentable client download target.
/// </summary>
/// <param name="Platform">Platform the link belongs to.</param>
/// <param name="Version">Release version as published by the vendor, without a leading <c>v</c>.</param>
/// <param name="Architecture">Human-readable architecture label shown to the customer.</param>
/// <param name="FileName">Exact asset file name that will be downloaded.</param>
/// <param name="DownloadUrl">
/// Absolute HTTPS download URL taken verbatim from the authoritative release asset. It is never constructed locally.
/// </param>
/// <param name="IsStale">
/// <c>true</c> when the values come from a cache entry that could not be refreshed inside the freshness window. Callers
/// must not present a stale link as freshly verified.
/// </param>
public sealed record ClientReleaseLink(
    ClientDownloadPlatform Platform,
    string Version,
    string Architecture,
    string FileName,
    string DownloadUrl,
    bool IsStale);

/// <summary>
/// Outcome of resolving the latest release for one platform.
/// </summary>
/// <param name="Success">Whether a safe, trusted download target was resolved.</param>
/// <param name="Link">Resolved target; meaningful only when <paramref name="Success"/> is <c>true</c>.</param>
/// <param name="ErrorCode">
/// Stable safe reason code when resolution failed. Never raw provider text or a response body.
/// </param>
public sealed record ClientReleaseResolution(bool Success, ClientReleaseLink Link, string ErrorCode);

/// <summary>
/// Resolves the latest official client software download for one platform.
/// </summary>
/// <remarks>
/// This is the single release-resolution contract for both owned and tenant bots, so the selection rules cannot diverge
/// between storefronts. Resolution is read-only and network-bounded; it never mutates orders, payments, wallets, tenants,
/// or panel state.
/// </remarks>
public interface IClientReleaseService
{
    /// <summary>
    /// Resolves the latest safe download target for a platform.
    /// </summary>
    /// <param name="platform">Compile-time known platform; customer input cannot widen this set.</param>
    /// <param name="cancellationToken">Cancellation token that aborts the provider request and any cache wait.</param>
    /// <returns>
    /// A successful resolution carrying a trusted absolute HTTPS URL, or a failure carrying a stable reason code. A
    /// failed resolution must be surfaced to the customer as temporarily unavailable rather than as a guessed link.
    /// </returns>
    /// <remarks>
    /// iOS is answered from a fixed App Store listing and performs no network call. Dynamic platforms use the GitHub
    /// <c>/releases/latest</c> endpoint with a process-local cache and single-flight refresh, so a large multi-bot
    /// deployment cannot turn simultaneous customer clicks into a request storm.
    /// </remarks>
    Task<ClientReleaseResolution> GetLatestAsync(
        ClientDownloadPlatform platform,
        CancellationToken cancellationToken);
}

/// <summary>
/// Closed-vocabulary reason codes reported when a client release cannot be resolved safely.
/// </summary>
/// <remarks>
/// The codes are logged and audited, so they must stay stable and must never embed a raw provider message, response body,
/// or URL.
/// </remarks>
public static class ClientReleaseErrorCodes
{
    /// <summary>The provider could not be reached, timed out, or returned a transport-level failure.</summary>
    public const string ProviderUnreachable = "client_release_provider_unreachable";

    /// <summary>The provider answered with a non-success HTTP status.</summary>
    public const string ProviderHttpError = "client_release_provider_http_error";

    /// <summary>The provider response was not the expected JSON shape.</summary>
    public const string InvalidPayload = "client_release_invalid_payload";

    /// <summary>The release was flagged as a draft or prerelease.</summary>
    public const string NotStableRelease = "client_release_not_stable";

    /// <summary>No asset matched the platform's required file name.</summary>
    public const string AssetNotFound = "client_release_asset_not_found";

    /// <summary>More than one asset matched the platform's fallback rules.</summary>
    public const string AssetAmbiguous = "client_release_asset_ambiguous";

    /// <summary>The selected asset's download URL was not a trusted absolute GitHub HTTPS URL.</summary>
    public const string UntrustedDownloadUrl = "client_release_download_url_untrusted";
}

/// <summary>
/// Immutable process-local view of the single global latest-client-download switch.
/// </summary>
/// <param name="Enabled">
/// Whether owned and tenant bots currently advertise the download menu to customers.
/// </param>
/// <param name="Revision">
/// Monotonically increasing process-local revision used to reject stale super-admin callback buttons.
/// </param>
public sealed record ClientDownloadAvailabilitySnapshot(bool Enabled, long Revision);

/// <summary>
/// Result of one requested global latest-client-download state transition.
/// </summary>
/// <param name="Applied">Whether the requested state was durably written and published to runtime readers.</param>
/// <param name="Snapshot">Current snapshot after the operation, including when the request was rejected.</param>
/// <param name="Message">Safe operational explanation suitable for a super-admin Telegram alert.</param>
public sealed record ClientDownloadToggleResult(
    bool Applied,
    ClientDownloadAvailabilitySnapshot Snapshot,
    string Message);

/// <summary>
/// Provides the live global latest-client-download switch and persists super-admin changes to configuration JSON.
/// </summary>
/// <remarks>
/// The switch is global by design: it is not per tenant, not per owned bot, and never stored in users.db. Callers must read
/// <see cref="Snapshot"/> at render and at handling time instead of capturing the startup <c>AppConfig</c> value, so an
/// administrator toggle takes effect on the next keyboard render without an application restart.
/// </remarks>
public interface IClientDownloadAvailability
{
    /// <summary>Gets an immutable, lock-free snapshot of the current global switch.</summary>
    ClientDownloadAvailabilitySnapshot Snapshot { get; }

    /// <summary>
    /// Atomically persists and immediately publishes the desired global state.
    /// </summary>
    /// <param name="enabled">Desired target state.</param>
    /// <param name="expectedRevision">
    /// Revision encoded in the super-admin panel button. A mismatch rejects a stale or replayed callback without writing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the serialized file write.</param>
    /// <returns>
    /// A result containing the current snapshot and a safe user-facing explanation. Runtime state is left untouched when
    /// the durable write fails.
    /// </returns>
    /// <remarks>
    /// Only the ASCII boolean token of the single root property <c>latestClientDownloadEnabled</c> is replaced. Existing
    /// secret values, Persian text, emoji, whitespace, casing, key order, and unrelated bytes are preserved exactly.
    /// </remarks>
    Task<ClientDownloadToggleResult> SetEnabledAsync(
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe implementation of <see cref="IClientDownloadAvailability"/> backed by <c>configuration.json</c>.
/// </summary>
/// <remarks>
/// The implementation deliberately mirrors the payment-gateway switch service: one serialized write gate, one immutable
/// published snapshot, and a byte-preserving root-boolean editor. Reusing that editor is what keeps a super-admin toggle
/// from reserializing the whole configuration file and disturbing Persian text, tokens, or formatting.
/// </remarks>
public sealed class ClientDownloadAvailabilityService : IClientDownloadAvailability
{
    /// <summary>Exact case-sensitive root JSON property persisted for this global switch.</summary>
    public const string ConfigurationPropertyName = "latestClientDownloadEnabled";

    private readonly string _configurationPath;
    private readonly ILogger<ClientDownloadAvailabilityService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ClientDownloadAvailabilitySnapshot _snapshot;

    /// <summary>
    /// Creates the live switch store from the startup configuration value.
    /// </summary>
    /// <param name="configuration">
    /// Startup options. The property is optional in configuration JSON, so a missing key leaves the feature disabled.
    /// </param>
    /// <param name="configurationPath">
    /// Absolute or content-root-relative path of the JSON file whose root boolean is persisted on toggle.
    /// </param>
    /// <param name="logger">Operational logger used for safe toggle audit events.</param>
    /// <exception cref="ArgumentNullException">Any required argument is <c>null</c>.</exception>
    public ClientDownloadAvailabilityService(
        AppConfig configuration,
        string configurationPath,
        ILogger<ClientDownloadAvailabilityService> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configurationPath = Path.GetFullPath(configurationPath ?? throw new ArgumentNullException(nameof(configurationPath)));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshot = new ClientDownloadAvailabilitySnapshot(configuration.LatestClientDownloadEnabled, Revision: 1);
    }

    /// <inheritdoc />
    public ClientDownloadAvailabilitySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public async Task<ClientDownloadToggleResult> SetEnabledAsync(
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = Snapshot;
            if (expectedRevision != current.Revision)
            {
                return new ClientDownloadToggleResult(
                    false,
                    current,
                    "این پنل قدیمی شده است؛ وضعیت جدید نمایش داده شد.");
            }

            if (current.Enabled == enabled)
                return new ClientDownloadToggleResult(true, current, "وضعیت دانلود نرم‌افزار از قبل همین مقدار بود.");

            await RootBooleanJsonFileEditor.SetAsync(
                _configurationPath,
                ConfigurationPropertyName,
                enabled,
                cancellationToken);

            var next = current with { Enabled = enabled, Revision = current.Revision + 1 };
            Volatile.Write(ref _snapshot, next);

            _logger.LogInformation(
                "Global latest-client-download state changed. enabled={Enabled}, revision={Revision}",
                enabled,
                next.Revision);

            return new ClientDownloadToggleResult(
                true,
                next,
                enabled
                    ? "دانلود آخرین نسخه نرم‌افزار روشن شد و تغییر در کانفیگ ذخیره شد."
                    : "دانلود آخرین نسخه نرم‌افزار خاموش شد و تغییر در کانفیگ ذخیره شد.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to persist global latest-client-download state.");
            return new ClientDownloadToggleResult(false, Snapshot, "ذخیره فایل کانفیگ ناموفق بود؛ وضعیت runtime تغییر نکرد.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied while persisting global latest-client-download state.");
            return new ClientDownloadToggleResult(false, Snapshot, "دسترسی نوشتن فایل کانفیگ وجود ندارد؛ وضعیت runtime تغییر نکرد.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "Invalid configuration JSON prevented latest-client-download persistence.");
            return new ClientDownloadToggleResult(false, Snapshot, "ساختار فایل کانفیگ معتبر نیست؛ وضعیت runtime تغییر نکرد.");
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
