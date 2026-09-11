using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;
// Both this project's domain model and Telegram.Bot define a User type, and both define a File type.
using TelegramUser = Telegram.Bot.Types.User;
using File = System.IO.File;

/// <summary>
/// Regression coverage for the global latest-client-software download feature shared by owned and tenant bots.
/// </summary>
/// <remarks>
/// <para>
/// These tests protect four independent invariants that a future refactor could quietly break:
/// </para>
/// <list type="bullet">
/// <item>the release selector only ever exposes the vendor's exact, intended asset — never a wrong architecture, an
/// F-Droid build, a signature file, or a prerelease;</item>
/// <item>a provider outage degrades to a cached or friendly-unavailable answer instead of crashing Telegram update
/// processing or inventing a URL;</item>
/// <item>a super-admin toggle changes live behaviour without a restart while preserving every unrelated configuration
/// byte, including Persian text and secret values; and</item>
/// <item>stale reply keyboards and stale inline buttons fail closed, so disabling the feature can never leave a customer
/// triggering provider traffic.</item>
/// </list>
/// <para>
/// No test performs a real network call. Provider access is always a stubbed <see cref="HttpMessageHandler"/>, and the
/// Telegram side is always an in-memory fake.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Release selection
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The iOS target is a fixed App Store listing, so it must be answered without touching the provider at all.
    /// </summary>
    /// <remarks>
    /// Guards against a future refactor that routes iOS through GitHub discovery, which would make the App Store link
    /// depend on a release payload that has nothing to do with it.
    /// </remarks>
    [Fact]
    public async Task Ios_target_is_built_in_and_performs_no_provider_call()
    {
        var handler = new StubGitHubHandler();
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Ios, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://apps.apple.com/us/app/v2box-v2ray-client/id6446814690", result.Link.DownloadUrl);
        Assert.False(result.Link.IsStale);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// The Windows selector must return exactly <c>v2rayN-windows-64.zip</c> from the authoritative latest release.
    /// </summary>
    /// <remarks>
    /// The desktop, ARM64, 32-bit, and signature assets are deliberately present in the payload: selecting any of them
    /// would ship a client the customer cannot run.
    /// </remarks>
    [Fact]
    public async Task Windows_selects_only_the_exact_x64_archive()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            {
              "tag_name": "7.24.9",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "v2rayN-windows-64-desktop.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64-desktop.zip" },
                { "name": "v2rayN-windows-arm64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-arm64.zip" },
                { "name": "v2rayN-windows-86.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-86.zip" },
                { "name": "v2rayN-windows-64.zip.sig", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip.sig" },
                { "name": "v2rayN-linux-64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-linux-64.zip" },
                { "name": "v2rayN-windows-64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("v2rayN-windows-64.zip", result.Link.FileName);
        Assert.Equal("7.24.9", result.Link.Version);
        Assert.Equal("https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip", result.Link.DownloadUrl);
        Assert.Contains("/repos/2dust/v2rayN/releases/latest", handler.RequestedUrls[0]);
    }

    /// <summary>
    /// A release without the exact Windows archive must fail closed rather than substitute a nearby asset.
    /// </summary>
    [Fact]
    public async Task Windows_fails_closed_when_the_exact_archive_is_absent()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            {
              "tag_name": "7.25.0",
              "assets": [
                { "name": "v2rayN-windows-64-desktop.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.25.0/v2rayN-windows-64-desktop.zip" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ClientReleaseErrorCodes.AssetNotFound, result.ErrorCode);
    }

    /// <summary>
    /// The Android selector must return the standard ARM64 APK and must never hand out the F-Droid build.
    /// </summary>
    /// <remarks>
    /// This is the highest-risk selection in the feature: the F-Droid APK sits in the same release with a nearly identical
    /// name, so a prefix-only match would silently ship the wrong package.
    /// </remarks>
    [Fact]
    public async Task Android_selects_the_exact_arm64_apk_and_never_the_fdroid_build()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            {
              "tag_name": "2.2.6",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "v2rayNG_2.2.6-fdroid_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.2.6/v2rayNG_2.2.6-fdroid_arm64-v8a.apk" },
                { "name": "v2rayNG_2.2.6_armeabi-v7a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.2.6/v2rayNG_2.2.6_armeabi-v7a.apk" },
                { "name": "v2rayNG_2.2.6_x86_64.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.2.6/v2rayNG_2.2.6_x86_64.apk" },
                { "name": "v2rayNG_2.2.6_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.2.6/v2rayNG_2.2.6_arm64-v8a.apk" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("v2rayNG_2.2.6_arm64-v8a.apk", result.Link.FileName);
        Assert.DoesNotContain("-fdroid", result.Link.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v7a", result.Link.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("arm64-v8a", result.Link.Architecture);
    }

    /// <summary>
    /// A tag such as <c>v2.2.6</c> must be normalized for the exact-name match without leaking the <c>v</c> into the file
    /// name or the displayed version.
    /// </summary>
    [Fact]
    public async Task Android_normalizes_a_leading_v_in_the_release_tag()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            {
              "tag_name": "v2.2.6",
              "assets": [
                { "name": "v2rayNG_2.2.6_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/v2.2.6/v2rayNG_2.2.6_arm64-v8a.apk" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("2.2.6", result.Link.Version);
    }

    /// <summary>
    /// When the vendor renames the exact asset, the fallback may still succeed if exactly one safe ARM64 candidate exists.
    /// </summary>
    [Fact]
    public async Task Android_fallback_accepts_a_single_safe_arm64_candidate()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            {
              "tag_name": "2.3.0",
              "assets": [
                { "name": "v2rayNG_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.3.0/v2rayNG_arm64-v8a.apk" },
                { "name": "v2rayNG_2.3.0-fdroid_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.3.0/v2rayNG_2.3.0-fdroid_arm64-v8a.apk" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("v2rayNG_arm64-v8a.apk", result.Link.FileName);
    }

    /// <summary>
    /// Two surviving fallback candidates must fail closed rather than pick one, because a wrong APK is a support incident.
    /// </summary>
    [Fact]
    public async Task Android_fallback_fails_closed_when_candidates_are_ambiguous()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            {
              "tag_name": "2.3.0",
              "assets": [
                { "name": "v2rayNG_alpha_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.3.0/v2rayNG_alpha_arm64-v8a.apk" },
                { "name": "v2rayNG_beta_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.3.0/v2rayNG_beta_arm64-v8a.apk" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ClientReleaseErrorCodes.AssetAmbiguous, result.ErrorCode);
    }

    /// <summary>
    /// A draft or prerelease payload must never be presented to customers, even though the endpoint normally excludes them.
    /// </summary>
    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public async Task Draft_or_prerelease_releases_are_rejected(string draft, string prerelease)
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = $$"""
            {
              "tag_name": "7.25.0",
              "draft": {{draft}},
              "prerelease": {{prerelease}},
              "assets": [
                { "name": "v2rayN-windows-64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.25.0/v2rayN-windows-64.zip" }
              ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ClientReleaseErrorCodes.NotStableRelease, result.ErrorCode);
    }

    /// <summary>
    /// A provider failure must surface as a stable reason code instead of an exception reaching the update pipeline.
    /// </summary>
    [Fact]
    public async Task Provider_failures_are_reported_as_stable_reason_codes()
    {
        var failing = new StubGitHubHandler { StatusCode = HttpStatusCode.ServiceUnavailable };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(failing));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ClientReleaseErrorCodes.ProviderHttpError, result.ErrorCode);
    }

    /// <summary>
    /// A response that is not the expected JSON object must be reported as an invalid payload, not parsed optimistically.
    /// </summary>
    [Fact]
    public async Task Malformed_provider_payload_is_rejected()
    {
        var handler = new StubGitHubHandler { ReleaseJson = "not json at all" };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ClientReleaseErrorCodes.InvalidPayload, result.ErrorCode);
    }

    /// <summary>
    /// An asset URL that is not an absolute GitHub HTTPS release URL must be refused, so a hijacked payload cannot
    /// redirect a customer somewhere else.
    /// </summary>
    [Theory]
    [InlineData("http://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip")]
    [InlineData("https://example.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip")]
    [InlineData("https://github.com/2dust/v2rayN/archive/7.24.9.zip")]
    public async Task Untrusted_download_urls_are_refused(string url)
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = $$"""
            {
              "tag_name": "7.24.9",
              "assets": [ { "name": "v2rayN-windows-64.zip", "browser_download_url": "{{url}}" } ]
            }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ClientReleaseErrorCodes.UntrustedDownloadUrl, result.ErrorCode);
    }

    /// <summary>
    /// A multi-bot deployment must not turn simultaneous customer clicks into a provider request storm.
    /// </summary>
    /// <remarks>
    /// The invariant is one upstream request per repository per freshness window, asserted both for sequential reuse and
    /// for several callers that race the very first refresh.
    /// </remarks>
    [Fact]
    public async Task Release_lookups_are_cached_and_single_flight()
    {
        var handler = new StubGitHubHandler
        {
            ResponseDelay = TimeSpan.FromMilliseconds(75),
            ReleaseJson = """
            { "tag_name": "7.24.9", "assets": [ { "name": "v2rayN-windows-64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip" } ] }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None)));

        Assert.All(concurrent, result => Assert.True(result.Success));
        Assert.Equal(1, handler.CallCount);

        // A later caller inside the freshness window reuses the cached entry without another request.
        var cached = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);
        Assert.True(cached.Success);
        Assert.False(cached.Link.IsStale);
        Assert.Equal(1, handler.CallCount);

        // The Android repository has its own cache entry and its own single-flight gate.
        Assert.Contains("/repos/2dust/v2rayN/releases/latest", handler.RequestedUrls[0]);
    }

    /// <summary>
    /// An expired cache entry must trigger a refresh once the freshness window has passed.
    /// </summary>
    [Fact]
    public async Task Expired_cache_entry_is_refreshed()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            { "tag_name": "7.24.9", "assets": [ { "name": "v2rayN-windows-64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip" } ] }
            """
        };
        var service = new ClientReleaseService(
            NullLogger<ClientReleaseService>.Instance,
            new HttpClient(handler),
            () => clock.UtcNow);

        await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    /// <summary>
    /// A failed refresh inside the stale ceiling must fall back to the last known-good link and must label it as stale.
    /// </summary>
    /// <remarks>
    /// Customers must not be told a cached version was freshly verified, and they must not lose the feature during a short
    /// provider outage.
    /// </remarks>
    [Fact]
    public async Task Failed_refresh_serves_a_clearly_labelled_stale_link_within_the_ceiling()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            { "tag_name": "7.24.9", "assets": [ { "name": "v2rayN-windows-64.zip", "browser_download_url": "https://github.com/2dust/v2rayN/releases/download/7.24.9/v2rayN-windows-64.zip" } ] }
            """
        };
        var service = new ClientReleaseService(
            NullLogger<ClientReleaseService>.Instance,
            new HttpClient(handler),
            () => clock.UtcNow);

        await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        clock.UtcNow = clock.UtcNow.AddHours(2);
        handler.StatusCode = HttpStatusCode.BadGateway;
        var stale = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.True(stale.Success);
        Assert.True(stale.Link.IsStale);
        Assert.Equal("7.24.9", stale.Link.Version);

        // Beyond the stale ceiling the same outage must stop claiming any link is available.
        clock.UtcNow = clock.UtcNow.AddHours(30);
        var expired = await service.GetLatestAsync(ClientDownloadPlatform.Windows, CancellationToken.None);

        Assert.False(expired.Success);
        Assert.Equal(ClientReleaseErrorCodes.ProviderHttpError, expired.ErrorCode);
    }

    /// <summary>
    /// With no cached value at all, a provider failure must be reported rather than synthesised into a link.
    /// </summary>
    [Fact]
    public async Task Provider_failure_without_any_cache_reports_unavailable()
    {
        var handler = new StubGitHubHandler { StatusCode = HttpStatusCode.BadGateway };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        var result = await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Link);
    }

    /// <summary>
    /// Provider requests must identify the application and ask for the documented JSON media type.
    /// </summary>
    [Fact]
    public async Task Provider_request_carries_a_user_agent_and_the_documented_accept_header()
    {
        var handler = new StubGitHubHandler
        {
            ReleaseJson = """
            { "tag_name": "2.2.6", "assets": [ { "name": "v2rayNG_2.2.6_arm64-v8a.apk", "browser_download_url": "https://github.com/2dust/v2rayNG/releases/download/2.2.6/v2rayNG_2.2.6_arm64-v8a.apk" } ] }
            """
        };
        var service = new ClientReleaseService(NullLogger<ClientReleaseService>.Instance, new HttpClient(handler));

        await service.GetLatestAsync(ClientDownloadPlatform.Android, CancellationToken.None);

        Assert.StartsWith("Adminbot/", handler.RequestedUserAgents[0]);
        Assert.Contains("application/vnd.github+json", handler.RequestedAccepts[0]);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Callback vocabulary
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The callback parser is a closed set, so no customer payload can select a repository, tag, asset, or URL.
    /// </summary>
    [Theory]
    [InlineData("APPDL:android", true)]
    [InlineData("APPDL:ios", true)]
    [InlineData("APPDL:windows", true)]
    [InlineData("APPDL:ANDROID", true)]
    [InlineData("APPDL:linux", false)]
    [InlineData("APPDL:", false)]
    [InlineData("APPDL:android:../../etc", false)]
    [InlineData("APPDL:https://evil.example.com/app.apk", false)]
    [InlineData("TN:tutorial:android", false)]
    public void Download_callback_parser_accepts_only_the_three_known_platforms(string payload, bool expected)
    {
        Assert.Equal(expected, ClientDownloadCallbacks.TryParse(payload, out _));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Live global switch
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A toggle must rewrite only the single root boolean and leave every other byte of the configuration file intact.
    /// </summary>
    /// <remarks>
    /// This protects the production configuration file, which holds Persian text, emoji, and secrets that must never be
    /// reserialized, reordered, or re-encoded by an admin toggle.
    /// </remarks>
    [Fact]
    public async Task Toggle_preserves_every_unrelated_configuration_byte()
    {
        using var databases = new Databases();
        var path = Path.Combine(databases.DirectoryPath, "configuration.json");
        const string original =
            "{\n" +
            "  \"latestClientDownloadEnabled\": false,\n" +
            "  \"nowPaymentApiKey\": \"secret-token-value\",\n" +
            "  \"welcomeText\": \"سلام 👋\",\n" +
            "  \"beta\": \"true\"\n" +
            "}\n";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));

        var availability = new ClientDownloadAvailabilityService(
            new AppConfig(), path, NullLogger<ClientDownloadAvailabilityService>.Instance);

        Assert.False(availability.Snapshot.Enabled);

        var result = await availability.SetEnabledAsync(true, availability.Snapshot.Revision);

        Assert.True(result.Applied);
        Assert.True(availability.Snapshot.Enabled);

        var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        Assert.Contains("\"latestClientDownloadEnabled\": true", text, StringComparison.Ordinal);
        Assert.Contains("secret-token-value", text, StringComparison.Ordinal);
        Assert.Contains("سلام 👋", text, StringComparison.Ordinal);
        // A value that looks boolean but is a string must be left exactly as it was.
        Assert.Contains("\"beta\": \"true\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("latestClientDownloadEnabled\": false", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stale or replayed super-admin button must be rejected without writing to disk.
    /// </summary>
    [Fact]
    public async Task Stale_toggle_revision_is_rejected_without_writing()
    {
        using var databases = new Databases();
        var path = Path.Combine(databases.DirectoryPath, "configuration.json");
        const string original = "{\n  \"latestClientDownloadEnabled\": false\n}\n";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));

        var availability = new ClientDownloadAvailabilityService(
            new AppConfig(), path, NullLogger<ClientDownloadAvailabilityService>.Instance);

        var result = await availability.SetEnabledAsync(true, expectedRevision: availability.Snapshot.Revision - 1);

        Assert.False(result.Applied);
        Assert.False(availability.Snapshot.Enabled);
        Assert.Equal(original, await File.ReadAllTextAsync(path, Encoding.UTF8));
    }

    /// <summary>
    /// When the durable write fails the runtime flag must not move, so the UI never claims a state that was not persisted.
    /// </summary>
    [Fact]
    public async Task Failed_persistence_leaves_the_runtime_flag_unchanged()
    {
        using var databases = new Databases();
        var missingDirectory = Path.Combine(databases.DirectoryPath, "does-not-exist", "configuration.json");

        var availability = new ClientDownloadAvailabilityService(
            new AppConfig(), missingDirectory, NullLogger<ClientDownloadAvailabilityService>.Instance);

        var result = await availability.SetEnabledAsync(true, availability.Snapshot.Revision);

        Assert.False(result.Applied);
        Assert.False(availability.Snapshot.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    /// <summary>
    /// A configuration file without the key must keep the feature disabled rather than defaulting it on.
    /// </summary>
    [Fact]
    public void Missing_configuration_key_defaults_the_feature_off()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["botToken"] = "123:abc" })
            .Build();

        var appConfig = configuration.Get<AppConfig>();

        Assert.NotNull(appConfig);
        Assert.False(appConfig.LatestClientDownloadEnabled);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Keyboard gating and stale-keyboard fail-closed
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The owned customer keyboard must expose the download row only while the live switch is on.
    /// </summary>
    /// <remarks>
    /// The toggle is not persisted per user or per bot, so newly rendered keyboards must track the live snapshot instead of
    /// the startup configuration value.
    /// </remarks>
    [Fact]
    public void Owned_customer_keyboard_tracks_the_live_switch()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: false);
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTelegramService(databases, flag, new CountingReleaseService(), client);

        Assert.DoesNotContain(ClientDownloadCallbacks.OpenCommand, OwnedKeyboardLabels(service));

        flag.SetEnabled(true);
        Assert.Contains(ClientDownloadCallbacks.OpenCommand, OwnedKeyboardLabels(service));
    }

    /// <summary>
    /// The tenant storefront keyboard must expose the same row, driven by the same global switch and no tenant flag.
    /// </summary>
    [Fact]
    public void Tenant_customer_keyboard_tracks_the_live_switch()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: false);
        var service = BuildClientDownloadTenantService(databases, flag, new CountingReleaseService());

        Assert.DoesNotContain(ClientDownloadCallbacks.OpenCommand, TenantKeyboardLabels(service));

        flag.SetEnabled(true);
        Assert.Contains(ClientDownloadCallbacks.OpenCommand, TenantKeyboardLabels(service));
    }

    /// <summary>
    /// A stale reply keyboard pressed after an administrator disables the feature must answer without any provider call.
    /// </summary>
    /// <remarks>
    /// This is the core fail-closed guarantee: a reply keyboard outlives the state that produced it, so the handler itself
    /// must re-check the live switch before doing any work.
    /// </remarks>
    [Fact]
    public async Task Stale_owned_menu_button_fails_closed_after_disable()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: false);
        var releases = new CountingReleaseService();
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTelegramService(databases, flag, releases, client);

        var handled = await InvokeOwnedMenuRequestAsync(service, client, ClientDownloadCallbacks.OpenCommand);

        Assert.True(handled);
        var sent = Assert.Single(client.Sends);
        Assert.Equal(ClientDownloadCallbacks.DisabledMessage, sent.Text);
        Assert.Equal(0, releases.Calls);
    }

    /// <summary>
    /// The tenant storefront must apply the same fail-closed rule as the owned bot.
    /// </summary>
    [Fact]
    public async Task Stale_tenant_menu_button_fails_closed_after_disable()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: false);
        var releases = new CountingReleaseService();
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTenantService(databases, flag, releases);

        var handled = await InvokeTenantMenuRequestAsync(service, client, ClientDownloadCallbacks.OpenCommand);

        Assert.True(handled);
        var sent = Assert.Single(client.Sends);
        Assert.Equal(ClientDownloadCallbacks.DisabledMessage, sent.Text);
        Assert.Equal(0, releases.Calls);
    }

    /// <summary>
    /// With the feature enabled the owned bot must answer a genuine menu request with the three platform buttons.
    /// </summary>
    [Fact]
    public async Task Enabled_owned_menu_request_offers_the_three_platforms()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: true);
        var releases = new CountingReleaseService();
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTelegramService(databases, flag, releases, client);

        var handled = await InvokeOwnedMenuRequestAsync(service, client, ClientDownloadCallbacks.OpenCommand);

        Assert.True(handled);
        var sent = Assert.Single(client.Sends);
        var callbacks = ((InlineKeyboardMarkup)sent.ReplyMarkup!).InlineKeyboard
            .SelectMany(row => row)
            .Select(button => button.CallbackData)
            .ToList();
        Assert.Equal(new[] { "APPDL:android", "APPDL:ios", "APPDL:windows" }, callbacks);
        Assert.Equal(0, releases.Calls);
    }

    /// <summary>
    /// A stale inline platform button must not contact the provider after the feature is disabled.
    /// </summary>
    [Fact]
    public async Task Stale_platform_callback_does_not_query_the_provider_after_disable()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: false);
        var releases = new CountingReleaseService();
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTelegramService(databases, flag, releases, client);

        await InvokeOwnedPlatformCallbackAsync(service, client, "APPDL:windows");

        Assert.Equal(0, releases.Calls);
        Assert.Empty(client.Sends);
        var answer = Assert.Single(client.Answers);
        Assert.True(answer.ShowAlert);
        Assert.Equal(ClientDownloadCallbacks.DisabledMessage, answer.Text);
    }

    /// <summary>
    /// A navigation label that merely resembles the menu must not be intercepted from the customer's conversation flow.
    /// </summary>
    /// <remarks>
    /// The non-matches are asserted so the label can never broaden into a prefix match and swallow purchase, duration, or
    /// account input that happens to start with similar words.
    /// </remarks>
    [Theory]
    [InlineData("دریافت نسخه")]
    [InlineData("دریافت آخرین نسخه نرم‌افزار")]
    [InlineData("📥")]
    [InlineData("")]
    public async Task Non_menu_text_is_not_intercepted(string text)
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: true);
        var releases = new CountingReleaseService();
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTelegramService(databases, flag, releases, client);

        var handled = await InvokeOwnedMenuRequestAsync(service, client, text);

        Assert.False(handled);
        Assert.Empty(client.Sends);
        Assert.Equal(0, releases.Calls);
    }

    /// <summary>
    /// Whitespace padding around the exact label is tolerated, as it is for the other reply-keyboard navigation labels.
    /// </summary>
    [Fact]
    public async Task Menu_label_is_matched_case_and_padding_tolerantly()
    {
        using var databases = new Databases();
        var flag = new ClientDownloadAvailabilityProbe(enabled: true);
        var releases = new CountingReleaseService();
        var client = new GatewayTelegramClient();
        var service = BuildClientDownloadTelegramService(databases, flag, releases, client);

        var handled = await InvokeOwnedMenuRequestAsync(service, client, $"  {ClientDownloadCallbacks.OpenCommand}  ");

        Assert.True(handled);
        Assert.Single(client.Sends);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a production <see cref="TelegramBotService"/> whose download dependencies are test doubles.
    /// </summary>
    /// <param name="databases">Temporary database fixture for the state and credentials stores.</param>
    /// <param name="flag">Controllable availability probe substituted for the live global switch.</param>
    /// <param name="releases">Counting release resolver, used to prove no provider work happens while disabled.</param>
    /// <param name="client">Fake Telegram client capturing sends, answers, and edits.</param>
    /// <returns>A service instance plus the bot context accessor the caller must push a runtime context onto.</returns>
    private static TelegramBotService BuildClientDownloadTelegramService(
        Databases databases,
        ClientDownloadAvailabilityProbe flag,
        IClientReleaseService releases,
        GatewayTelegramClient client)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UserActivityLogEnabled"] = "false",
            ["UserActivityLogFilePath"] = Path.Combine(databases.DirectoryPath, "client-download-activity.jsonl")
        }).Build();
        var accessor = new BotContextAccessor();
        return new TelegramBotService(
            client, new UserWorkflowStore(databases.Users), new UserStateStore(databases.Users),
            new CredentialsStore(databases.Credentials), configuration, NullLogger<TelegramBotService>.Instance,
            // broadcast/nowpayments/hooshpay/tetraminator/uniquepay/atlaspay pairs, then the availability seams.
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            flag, releases,
            // x-ui purchase/session/admin flows, tenant and sales-assistant services.
            null!, null!, null!, null!, null!, null!,
            new UserActivityLogService(configuration),
            // analytics, chart renderer, wallet ledger, notification, gozargah, registry, runtime status.
            null!, null!, null!, null!, null!, null!, null!, null!,
            accessor, null!);
    }

    /// <summary>
    /// Builds a production <see cref="TenantBotService"/> whose download dependencies are test doubles.
    /// </summary>
    /// <param name="databases">Temporary database fixture for the state and credentials stores.</param>
    /// <param name="flag">Controllable availability probe substituted for the live global switch.</param>
    /// <param name="releases">Counting release resolver, used to prove no provider work happens while disabled.</param>
    /// <returns>A storefront service instance ready for the private keyboard and menu handlers under test.</returns>
    private static TenantBotService BuildClientDownloadTenantService(
        Databases databases,
        ClientDownloadAvailabilityProbe flag,
        IClientReleaseService releases)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UserActivityLogEnabled"] = "false"
        }).Build();
        var accessor = new BotContextAccessor();
        return new TenantBotService(
            new UserWorkflowStore(databases.Users), new UserStateStore(databases.Users),
            new CredentialsStore(databases.Credentials), configuration,
            null!, null!, null!, null!, null!, null!, null!, null!,
            flag, releases,
            null!, null!, accessor, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<TenantBotService>.Instance, null!, null!, null!);
    }

    /// <summary>Flattens the owned customer reply keyboard into its button labels.</summary>
    /// <param name="service">Owned service under test.</param>
    /// <returns>Every rendered button label in row order.</returns>
    private static List<string> OwnedKeyboardLabels(TelegramBotService service)
    {
        var method = typeof(TelegramBotService).GetMethod(
            "MainReplyMarkupKeyboardFa", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var markup = (ReplyKeyboardMarkup)method.Invoke(service, Array.Empty<object>())!;
        return markup.Keyboard.SelectMany(row => row).Select(button => button.Text).ToList();
    }

    /// <summary>Flattens the tenant storefront reply keyboard into its button labels.</summary>
    /// <param name="service">Storefront service under test.</param>
    /// <returns>Every rendered button label in row order.</returns>
    private static List<string> TenantKeyboardLabels(TenantBotService service)
    {
        var method = typeof(TenantBotService).GetMethod(
            "BuildTenantReplyKeyboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var markup = (ReplyKeyboardMarkup)method.Invoke(service, Array.Empty<object>())!;
        return markup.Keyboard.SelectMany(row => row).Select(button => button.Text).ToList();
    }

    /// <summary>Invokes the owned download-menu handler with a synthetic text message.</summary>
    /// <param name="service">Owned service under test.</param>
    /// <param name="client">Client the send is recorded on.</param>
    /// <param name="text">Message text to submit.</param>
    /// <returns><c>true</c> when the handler claimed the message.</returns>
    private static async Task<bool> InvokeOwnedMenuRequestAsync(
        TelegramBotService service, ITelegramBotClient client, string text)
    {
        var method = typeof(TelegramBotService).GetMethod(
            "TryHandleClientDownloadMenuRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var message = new Message { Chat = new Chat { Id = 4242 }, From = new TelegramUser { Id = 4242 }, Text = text };
        var task = (Task<bool>)method.Invoke(service, new object[] { client, message,            new global::User(), CancellationToken.None })!;
        return await task;
    }

    /// <summary>Invokes the tenant download-menu handler with a synthetic text message.</summary>
    /// <param name="service">Storefront service under test.</param>
    /// <param name="client">Client the send is recorded on.</param>
    /// <param name="text">Message text to submit.</param>
    /// <returns><c>true</c> when the handler claimed the message.</returns>
    private static async Task<bool> InvokeTenantMenuRequestAsync(
        TenantBotService service, ITelegramBotClient client, string text)
    {
        var method = typeof(TenantBotService).GetMethod(
            "TryHandleClientDownloadMenuRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var message = new Message { Chat = new Chat { Id = 4242 }, From = new TelegramUser { Id = 4242 }, Text = text };
        var task = (Task<bool>)method.Invoke(service, new object[] { client, message, CancellationToken.None })!;
        return await task;
    }

    /// <summary>Invokes the owned platform-callback handler for one payload.</summary>
    /// <param name="service">Owned service under test.</param>
    /// <param name="client">Client the answer is recorded on.</param>
    /// <param name="payload">Callback payload to submit.</param>
    /// <returns>A task that completes after the callback has been handled.</returns>
    private static async Task InvokeOwnedPlatformCallbackAsync(
        TelegramBotService service, ITelegramBotClient client, string payload)
    {
        Assert.True(ClientDownloadCallbacks.TryParse(payload, out var platform));
        var method = typeof(TelegramBotService).GetMethod(
            "HandleClientDownloadPlatformCallbackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var query = new CallbackQuery
        {
            Id = "cb-1",
            From = new TelegramUser { Id = 4242 },
            Data = payload,
            Message = new Message { MessageId = 7, Chat = new Chat { Id = 4242 } }
        };
        await (Task)method.Invoke(service, new object[] { client, query, platform, CancellationToken.None })!;
    }

    /// <summary>Mutable read-only in-memory stand-in for <see cref="IClientDownloadAvailability"/>.</summary>
    private sealed class ClientDownloadAvailabilityProbe : IClientDownloadAvailability
    {
        private ClientDownloadAvailabilitySnapshot _snapshot;

        /// <summary>Creates a probe with a deterministic starting state and revision.</summary>
        /// <param name="enabled">Initial live state.</param>
        public ClientDownloadAvailabilityProbe(bool enabled)
            => _snapshot = new ClientDownloadAvailabilitySnapshot(enabled, Revision: 3);

        /// <inheritdoc />
        public ClientDownloadAvailabilitySnapshot Snapshot => _snapshot;

        /// <inheritdoc />
        public Task<ClientDownloadToggleResult> SetEnabledAsync(bool enabled, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (expectedRevision != _snapshot.Revision)
                return Task.FromResult(new ClientDownloadToggleResult(false, _snapshot, "stale"));

            _snapshot = _snapshot with { Enabled = enabled, Revision = _snapshot.Revision + 1 };
            return Task.FromResult(new ClientDownloadToggleResult(true, _snapshot, "ok"));
        }

        /// <summary>Flips the live state as an administrator panel would, advancing the revision.</summary>
        /// <param name="enabled">Desired state.</param>
        public void SetEnabled(bool enabled) => _snapshot = _snapshot with { Enabled = enabled, Revision = _snapshot.Revision + 1 };
    }

    /// <summary>Release resolver stand-in that records how many times it was asked to resolve.</summary>
    private sealed class CountingReleaseService : IClientReleaseService
    {
        private int _calls;

        /// <summary>Number of resolution calls observed; must stay zero while the feature is disabled.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <inheritdoc />
        public Task<ClientReleaseResolution> GetLatestAsync(ClientDownloadPlatform platform, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ClientReleaseResolution(
                true,
                new ClientReleaseLink(platform, "1.0.0", "test", "test.bin", "https://github.com/2dust/v2rayN/releases/download/1.0.0/test.bin", false),
                null));
        }
    }

    /// <summary>Advanceable UTC clock used to exercise cache freshness without waiting.</summary>
    private sealed class MutableClock
    {
        /// <summary>Creates the clock at a fixed instant.</summary>
        /// <param name="utcNow">Starting UTC instant.</param>
        public MutableClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        /// <summary>Current UTC instant; settable so a test can jump forward in time.</summary>
        public DateTimeOffset UtcNow { get; set; }
    }

    /// <summary>Stubbed GitHub transport that records requests and returns a scripted release payload.</summary>
    private sealed class StubGitHubHandler : HttpMessageHandler
    {
        private readonly List<string> _urls = new();
        private readonly List<string> _userAgents = new();
        private readonly List<string> _accepts = new();
        private int _calls;

        /// <summary>JSON body returned for <c>/releases/latest</c>; a non-object body simulates a malformed payload.</summary>
        public string ReleaseJson { get; set; } = "{}";

        /// <summary>Status code returned for every request; non-200 simulates a provider outage.</summary>
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        /// <summary>Artificial latency so concurrent callers can race the first refresh.</summary>
        public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

        /// <summary>Number of provider requests actually issued.</summary>
        public int CallCount => Volatile.Read(ref _calls);

        /// <summary>Requested URLs in order of arrival.</summary>
        public IReadOnlyList<string> RequestedUrls => _urls;

        /// <summary>User-Agent header values in order of arrival.</summary>
        public IReadOnlyList<string> RequestedUserAgents => _userAgents;

        /// <summary>Accept header values in order of arrival.</summary>
        public IReadOnlyList<string> RequestedAccepts => _accepts;

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            lock (_urls)
            {
                _urls.Add(request.RequestUri!.ToString());
                _userAgents.Add(request.Headers.UserAgent.ToString());
                _accepts.Add(string.Join(",", request.Headers.Accept.Select(value => value.MediaType)));
            }

            if (ResponseDelay > TimeSpan.Zero)
                await Task.Delay(ResponseDelay, cancellationToken);

            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
