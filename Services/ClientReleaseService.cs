using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Adminbot.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Adminbot.Services;

/// <summary>
/// Cached, single-flight GitHub-backed implementation of <see cref="IClientReleaseService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only two repositories are ever queried, and both are compile-time constants. The latest release is read from
/// <c>/repos/{owner}/{repo}/releases/latest</c>, which is the vendor's authoritative "latest stable release" declaration,
/// rather than by scraping the HTML releases page or enumerating tags.
/// </para>
/// <para>
/// A successful resolution is cached for <see cref="FreshFor"/>. Concurrent callers for the same repository are
/// serialized through a per-repository gate and reuse the fresh entry, so a transient outage or a traffic burst produces
/// at most one upstream request per repository per window. When a refresh fails, a previously cached result is served as
/// stale for at most <see cref="StaleCeiling"/>; beyond that the caller receives a failure and must tell the customer the
/// feature is temporarily unavailable.
/// </para>
/// </remarks>
public sealed class ClientReleaseService : IClientReleaseService
{
    /// <summary>Official v2rayN (Windows) repository.</summary>
    private const string WindowsRepository = "2dust/v2rayN";

    /// <summary>Official v2rayNG (Android) repository.</summary>
    private const string AndroidRepository = "2dust/v2rayNG";

    /// <summary>Exact Windows asset name requested; the other architecture and desktop variants must never be selected.</summary>
    private const string WindowsAssetName = "v2rayN-windows-64.zip";

    /// <summary>Android asset suffix for the standard ARM64 build.</summary>
    private const string AndroidArm64Suffix = "_arm64-v8a.apk";

    /// <summary>Android asset prefix shared by every v2rayNG APK.</summary>
    private const string AndroidAssetPrefix = "v2rayNG_";

    /// <summary>Marker present only in the F-Droid build, which customers must not receive from this menu.</summary>
    private const string AndroidFdroidMarker = "-fdroid";

    /// <summary>Fixed App Store listing for the iOS client; no version discovery is needed or performed.</summary>
    private const string IosAppStoreUrl = "https://apps.apple.com/us/app/v2box-v2ray-client/id6446814690";

    /// <summary>How long a successful resolution is considered fresh for every caller.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    /// <summary>Maximum age of a cached resolution that may still be served as a degraded fallback.</summary>
    private static readonly TimeSpan StaleCeiling = TimeSpan.FromHours(24);

    /// <summary>Bounded provider request timeout; a hung request must not stall Telegram update processing.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly ILogger<ClientReleaseService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the resolver over a shared HTTP client.
    /// </summary>
    /// <param name="logger">Operational logger for safe, non-payload diagnostics.</param>
    /// <param name="httpClient">
    /// Optional client used for provider requests. Production passes the process-wide client with the project's default
    /// handler and no credentials; tests pass a client over a stubbed handler.
    /// </param>
    /// <param name="utcNow">
    /// Optional clock override used by tests to exercise freshness, stale fallback, and expiry without waiting. Production
    /// leaves it <c>null</c> and uses the system clock.
    /// </param>
    public ClientReleaseService(
        ILogger<ClientReleaseService> logger,
        HttpClient httpClient = null,
        Func<DateTimeOffset> utcNow = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? CreateDefaultClient();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<ClientReleaseResolution> GetLatestAsync(
        ClientDownloadPlatform platform,
        CancellationToken cancellationToken)
    {
        // iOS is a fixed App Store listing: there is nothing to discover, so it is answered without any network access.
        if (platform == ClientDownloadPlatform.Ios)
        {
            return new ClientReleaseResolution(
                true,
                new ClientReleaseLink(
                    ClientDownloadPlatform.Ios,
                    Version: string.Empty,
                    Architecture: "iOS / Android",
                    FileName: "V2Box",
                    DownloadUrl: IosAppStoreUrl,
                    IsStale: false),
                ErrorCode: null);
        }

        var repository = platform == ClientDownloadPlatform.Windows ? WindowsRepository : AndroidRepository;

        if (TryGetCached(repository, FreshFor, out var freshLink))
            return new ClientReleaseResolution(true, freshLink, ErrorCode: null);

        // One gate per repository: waiting here cannot block a different repository's refresh, and no lock is held while
        // the provider request is in flight beyond the single repository's own gate.
        var gate = _refreshGates.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (TryGetCached(repository, FreshFor, out freshLink))
                return new ClientReleaseResolution(true, freshLink, ErrorCode: null);

            try
            {
                var fetched = await FetchLatestAsync(repository, platform, cancellationToken);
                if (!fetched.Success)
                    return FallbackOrFail(repository, fetched.ErrorCode);

                _cache[repository] = new CacheEntry(fetched.Link, _utcNow());
                return fetched;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                // Request timeout: treat as an outage, not as a cancellation of the caller's work.
                return FallbackOrFail(repository, ClientReleaseErrorCodes.ProviderUnreachable);
            }
            catch (HttpRequestException)
            {
                return FallbackOrFail(repository, ClientReleaseErrorCodes.ProviderUnreachable);
            }
            catch (JsonException)
            {
                // A proxy, captive portal, or provider error page can answer HTTP 200 with a body that is not the release
                // object. Parse failures must degrade to the cache or to a friendly failure, never escape into the
                // Telegram update pipeline.
                return FallbackOrFail(repository, ClientReleaseErrorCodes.InvalidPayload);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Returns a cached resolution when it is inside the allowed age window.
    /// </summary>
    /// <param name="repository">Repository key of the cached entry.</param>
    /// <param name="maxAge">Maximum age the caller will accept.</param>
    /// <param name="link">Cached link when one is available and young enough.</param>
    /// <returns><c>true</c> when a usable cached entry exists.</returns>
    /// <remarks>
    /// The returned link carries <see cref="ClientReleaseLink.IsStale" /> set from the age check, so a caller serving an
    /// older-than-fresh entry never labels it as freshly verified.
    /// </remarks>
    private bool TryGetCached(string repository, TimeSpan maxAge, out ClientReleaseLink link)
    {
        link = null;
        if (!_cache.TryGetValue(repository, out var entry))
            return false;

        if (_utcNow() - entry.FetchedAtUtc > maxAge)
            return false;

        link = entry.Link with { IsStale = maxAge != FreshFor };
        return true;
    }

    /// <summary>
    /// Serves a still-acceptable stale entry after a failed refresh, or reports the failure.
    /// </summary>
    /// <param name="repository">Repository key that failed to refresh.</param>
    /// <param name="errorCode">Stable reason code of the failed refresh.</param>
    /// <returns>A stale success inside the ceiling, otherwise the original failure.</returns>
    private ClientReleaseResolution FallbackOrFail(string repository, string errorCode)
    {
        if (TryGetCached(repository, StaleCeiling, out var staleLink))
        {
            _logger.LogWarning(
                "Serving stale client release metadata. repository={Repository}, errorCode={ErrorCode}",
                repository,
                errorCode);
            return new ClientReleaseResolution(true, staleLink, ErrorCode: null);
        }

        _logger.LogWarning(
            "Client release resolution failed with no usable cache. repository={Repository}, errorCode={ErrorCode}",
            repository,
            errorCode);
        return new ClientReleaseResolution(false, null, errorCode);
    }

    /// <summary>
    /// Calls the GitHub latest-release endpoint and selects the platform's exact asset.
    /// </summary>
    /// <param name="repository">Compile-time repository in <c>owner/name</c> form.</param>
    /// <param name="platform">Platform whose asset selection rules apply.</param>
    /// <param name="cancellationToken">Cancellation token for the provider request.</param>
    /// <returns>A successful resolution, or a failure carrying a stable reason code.</returns>
    /// <exception cref="JsonException">The response body is not a JSON object and could not be parsed.</exception>
    /// <exception cref="HttpRequestException">The provider request failed at the transport level.</exception>
    private async Task<ClientReleaseResolution> FetchLatestAsync(
        string repository,
        ClientDownloadPlatform platform,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub rejects requests without a User-Agent. No token is used because both repositories are public.
        request.Headers.UserAgent.ParseAdd($"Adminbot/{GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            timeoutSource.Token);

        if (response.StatusCode != HttpStatusCode.OK)
            return new ClientReleaseResolution(false, null, ClientReleaseErrorCodes.ProviderHttpError);

        var payload = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        var release = JObject.Parse(payload);

        // The endpoint already excludes drafts and prereleases, but the contract is asserted rather than assumed so a
        // future provider behaviour change cannot silently ship a prerelease to customers.
        if (release.Value<bool?>("draft") == true || release.Value<bool?>("prerelease") == true)
            return new ClientReleaseResolution(false, null, ClientReleaseErrorCodes.NotStableRelease);

        var tagName = release.Value<string>("tag_name");
        if (string.IsNullOrWhiteSpace(tagName))
            return new ClientReleaseResolution(false, null, ClientReleaseErrorCodes.InvalidPayload);

        var assets = release["assets"] as JArray;
        if (assets == null)
            return new ClientReleaseResolution(false, null, ClientReleaseErrorCodes.InvalidPayload);

        return platform == ClientDownloadPlatform.Windows
            ? SelectWindowsAsset(assets, tagName.Trim())
            : SelectAndroidAsset(assets, tagName.Trim());
    }

    /// <summary>
    /// Selects the exact Windows x64 archive from a release's assets.
    /// </summary>
    /// <param name="assets">Asset array from the authoritative release payload.</param>
    /// <param name="version">Release version taken from <c>tag_name</c>.</param>
    /// <returns>A successful resolution or a stable failure code.</returns>
    /// <remarks>
    /// Only the exact name <c>v2rayN-windows-64.zip</c> is accepted. The desktop, ARM64, and 32-bit archives, checksum
    /// and signature files, and non-Windows packages are never substituted, because delivering the wrong architecture
    /// would be worse than reporting the feature temporarily unavailable.
    /// </remarks>
    private static ClientReleaseResolution SelectWindowsAsset(JArray assets, string version)
    {
        var asset = assets
            .OfType<JObject>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Value<string>("name"),
                WindowsAssetName,
                StringComparison.Ordinal));

        if (asset == null)
            return new ClientReleaseResolution(false, null, ClientReleaseErrorCodes.AssetNotFound);

        return BuildLink(ClientDownloadPlatform.Windows, version, "Windows x64", asset);
    }

    /// <summary>
    /// Selects the standard ARM64 v2rayNG APK from a release's assets.
    /// </summary>
    /// <param name="assets">Asset array from the authoritative release payload.</param>
    /// <param name="tagName">Release tag, normalized only by stripping a single leading <c>v</c>/<c>V</c>.</param>
    /// <returns>A successful resolution or a stable failure code.</returns>
    /// <remarks>
    /// The exact file name <c>v2rayNG_{version}_arm64-v8a.apk</c> is preferred. If the vendor's naming changes, the
    /// fallback accepts a single asset that starts with <c>v2rayNG_</c>, ends with <c>_arm64-v8a.apk</c>, and is neither
    /// the F-Droid build nor a signature file. Zero or multiple surviving candidates fail closed rather than guessing an
    /// ABI, because shipping a wrong-architecture or F-Droid build is a support incident.
    /// </remarks>
    private static ClientReleaseResolution SelectAndroidAsset(JArray assets, string tagName)
    {
        var version = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName[1..] : tagName;
        var candidates = assets.OfType<JObject>().ToList();

        var expectedName = $"{AndroidAssetPrefix}{version}{AndroidArm64Suffix}";
        var exact = candidates.FirstOrDefault(candidate => string.Equals(
            candidate.Value<string>("name"),
            expectedName,
            StringComparison.Ordinal));

        if (exact != null)
            return BuildLink(ClientDownloadPlatform.Android, version, "arm64-v8a", exact);

        var fallback = candidates
            .Where(candidate =>
            {
                var name = candidate.Value<string>("name") ?? string.Empty;
                return name.StartsWith(AndroidAssetPrefix, StringComparison.Ordinal)
                       && name.EndsWith(AndroidArm64Suffix, StringComparison.Ordinal)
                       && !name.Contains(AndroidFdroidMarker, StringComparison.OrdinalIgnoreCase)
                       && !name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (fallback.Count != 1)
            return new ClientReleaseResolution(
                false,
                null,
                fallback.Count == 0 ? ClientReleaseErrorCodes.AssetNotFound : ClientReleaseErrorCodes.AssetAmbiguous);

        return BuildLink(ClientDownloadPlatform.Android, version, "arm64-v8a", fallback[0]);
    }

    /// <summary>
    /// Validates an asset's download URL and builds the customer-presentable link.
    /// </summary>
    /// <param name="platform">Platform the asset belongs to.</param>
    /// <param name="version">Displayed version.</param>
    /// <param name="architecture">Displayed architecture label.</param>
    /// <param name="asset">Selected asset object from the release payload.</param>
    /// <returns>A successful resolution, or a failure when the URL is unusable.</returns>
    private static ClientReleaseResolution BuildLink(
        ClientDownloadPlatform platform,
        string version,
        string architecture,
        JObject asset)
    {
        var fileName = asset.Value<string>("name");
        var url = asset.Value<string>("browser_download_url");
        if (string.IsNullOrWhiteSpace(fileName) || !IsTrustedAssetUrl(url))
            return new ClientReleaseResolution(false, null, ClientReleaseErrorCodes.UntrustedDownloadUrl);

        return new ClientReleaseResolution(
            true,
            new ClientReleaseLink(platform, version, architecture, fileName, url, IsStale: false),
            ErrorCode: null);
    }

    /// <summary>
    /// Determines whether a provider-supplied download URL is a trustworthy absolute GitHub HTTPS release URL.
    /// </summary>
    /// <param name="url">Candidate URL taken from the selected release asset.</param>
    /// <returns><c>true</c> only for absolute HTTPS GitHub release-download URLs.</returns>
    /// <remarks>
    /// The URL always originates from the authoritative release payload; this check exists so a malformed or hijacked
    /// payload cannot redirect a customer to an arbitrary host.
    /// </remarks>
    private static bool IsTrustedAssetUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
           && uri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the process-wide provider client used when no client is supplied.
    /// </summary>
    /// <returns>A client with pooling enabled and no ambient credentials.</returns>
    private static HttpClient CreateDefaultClient()
        => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    /// <summary>One cached successful resolution together with the instant it was fetched.</summary>
    /// <param name="Link">Resolved link recorded at fetch time.</param>
    /// <param name="FetchedAtUtc">UTC instant the successful resolution was obtained.</param>
    private sealed record CacheEntry(ClientReleaseLink Link, DateTimeOffset FetchedAtUtc);
}
