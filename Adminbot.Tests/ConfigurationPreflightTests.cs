using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>
/// Regression coverage for the configuration safety rules introduced after a production outage caused by pruning
/// <c>configuration.json</c> against a configuration example taken from a different branch.
/// </summary>
/// <remarks>
/// That prune removed <c>xuiV3ApiBaseUrl</c> and the per-bot <c>loggerChannel</c> / <c>backupChannel</c> values, so the
/// bot could no longer reach its panel and the durable Telegram log outbox filled with undeliverable rows.
///
/// Two independent invariants are protected here:
/// <list type="number">
/// <item><description>
/// The example template is documentation, never a key whitelist. Validation must never treat a key that is absent from
/// the example as obsolete, and unknown or forward-compatible keys must stay acceptable, so a configuration can only
/// lose a key if a human deletes it.
/// </description></item>
/// <item><description>
/// A configuration that cannot work is rejected at startup with a sanitized message, while a merely degraded
/// configuration is reported as a warning so the process still starts and keeps settling payments.
/// </description></item>
/// </list>
///
/// No test performs network I/O or starts a host.
/// </remarks>
public sealed class ConfigurationPreflightTests
{
    /// <summary>A panel URL that satisfies the absolute HTTP/HTTPS rule.</summary>
    private const string ValidPanelUrl = "https://panel.example.com:54321/";

    /// <summary>
    /// Proves the reported outage condition is rejected: the reminder is enabled but no panel URL is configured.
    /// </summary>
    /// <remarks>
    /// This is the exact configuration that would otherwise fail repeatedly at runtime, because the reminder worker
    /// builds its panel descriptor from the base URL and throws on every cycle.
    /// </remarks>
    [Fact]
    public void Enabled_volume_reminder_without_a_panel_url_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig { VolumeExpirationReminderEnabled = true }));

        Assert.Contains("volumeExpirationReminderEnabled", error.Message, StringComparison.Ordinal);
        Assert.Contains("xuiV3ApiBaseUrl", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves a structurally wrong panel URL is rejected whether or not the reminder is enabled.
    /// </summary>
    /// <param name="panelUrl">Panel URL that is not an absolute HTTP or HTTPS address.</param>
    /// <remarks>
    /// Every XUI v3 client builds its request URI from this value, so a wrong shape can never reach a panel. Failing at
    /// startup is strictly better than failing on every customer interaction.
    /// </remarks>
    [Theory]
    [InlineData("panel.example.com:54321")]
    [InlineData("ftp://panel.example.com")]
    [InlineData("//panel.example.com")]
    [InlineData("not a url at all")]
    public void Malformed_panel_url_is_rejected_independently_of_the_reminder_switch(string panelUrl)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig { XuiV3ApiBaseUrl = panelUrl }));

        Assert.Contains("xuiV3ApiBaseUrl", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves a disabled reminder is tolerated with no panel URL, so a wallet-only deployment still starts.
    /// </summary>
    /// <remarks>
    /// Only explicitly enabled features are gated on the panel URL. The account-expiry reminder defaults to enabled but
    /// is deliberately not gated, because that would make an intentionally panel-less deployment unbootable.
    /// </remarks>
    [Fact]
    public void Disabled_reminder_without_a_panel_url_is_allowed()
    {
        ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig());
        ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig { XuiV3ApiBaseUrl = "" });
    }

    /// <summary>Proves a usable panel URL satisfies the enabled-reminder rule.</summary>
    /// <param name="panelUrl">Absolute HTTP or HTTPS panel URL.</param>
    [Theory]
    [InlineData("https://panel.example.com:54321/")]
    [InlineData("http://127.0.0.1:2053")]
    public void Enabled_volume_reminder_with_a_usable_panel_url_is_allowed(string panelUrl)
    {
        ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig
        {
            VolumeExpirationReminderEnabled = true,
            XuiV3ApiBaseUrl = panelUrl
        });
    }

    /// <summary>
    /// Proves a rejected value is never echoed, because it may be a pasted secret.
    /// </summary>
    [Fact]
    public void Validation_messages_never_echo_the_configured_value()
    {
        const string secretLookingValue = "SecretPanelTokenABCDEF1234567890";

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig { XuiV3ApiBaseUrl = secretLookingValue }));

        Assert.DoesNotContain(secretLookingValue, error.Message, StringComparison.Ordinal);
        Assert.Contains("xuiV3ApiBaseUrl", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves two application databases pointed at one file are rejected instead of corrupting the shared schema.
    /// </summary>
    /// <remarks>
    /// Both EF models migrate against whatever file the path resolves to, so sharing a file corrupts the schema rather
    /// than failing cleanly.
    /// </remarks>
    [Fact]
    public void Identical_database_paths_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig
            {
                UserDatabasePath = "/srv/vpnetiran/Data/shared.db",
                CredentialsDatabasePath = "/srv/vpnetiran/Data/shared.db"
            }));

        Assert.Contains("userDatabasePath", error.Message, StringComparison.Ordinal);
        Assert.Contains("credentialsDatabasePath", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves distinct paths and omitted paths are both accepted, because the startup path resolution substitutes the
    /// documented <c>./Data/...</c> defaults for a missing key.
    /// </summary>
    [Fact]
    public void Distinct_or_omitted_database_paths_are_allowed()
    {
        ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig
        {
            UserDatabasePath = "./Data/users.db",
            CredentialsDatabasePath = "./Data/credentials.db"
        });

        ConfigurationPreflight.ValidateEnabledFeatures(new AppConfig
        {
            UserDatabasePath = "./Data/users.db"
        });
    }

    /// <summary>
    /// Proves the startup report is fail-soft: it always returns a summary line and never throws.
    /// </summary>
    /// <remarks>
    /// Reporting must never be able to stop the process, because the operations whose logging is misconfigured are
    /// payment settlement and Telegram update handling.
    /// </remarks>
    [Fact]
    public void Startup_report_is_never_fatal()
    {
        var lines = ConfigurationPreflight.DescribeStartupReport(null!, null, null, null, null);

        Assert.StartsWith("[ConfigurationPreflight] loggerChannel=none backupChannel=none", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("no deliverable Telegram logger channel", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("no deliverable Telegram backup channel", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("xuiV3ApiBaseUrl is not configured", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves a key that this build does not know is accepted rather than rejected as unknown.
    /// </summary>
    /// <remarks>
    /// Forward compatibility is the reason a key set must never be treated as a schema: a newer configuration must stay
    /// usable by an older build, and an older configuration must stay usable by a newer one.
    /// </remarks>
    [Fact]
    public void Unknown_forward_compatible_keys_are_not_rejected()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["volumeExpirationReminderEnabled"] = "false",
            ["someFutureFeatureEnabled"] = "true",
            ["nestedFutureSection:innerKey"] = "value",
            ["xuiV3ApiBaseUrl"] = ValidPanelUrl
        }).Build();

        var appConfig = configuration.Get<AppConfig>()!;
        ConfigurationPreflight.ValidateEnabledFeatures(appConfig);

        // Known keys are still bound, so an unknown key neither fails validation nor hides real configuration.
        Assert.Equal(ValidPanelUrl, appConfig.XuiV3ApiBaseUrl);
        Assert.False(appConfig.VolumeExpirationReminderEnabled);
    }

    /// <summary>
    /// Proves validation accepts the production configuration shape even though it contains many keys that are absent
    /// from the shipped example.
    /// </summary>
    /// <remarks>
    /// This is the regression for the reported incident: a key missing from the example is not obsolete, and nothing in
    /// validation may treat the example as an authoritative key list.
    /// </remarks>
    [Fact]
    public void Keys_absent_from_the_example_are_not_treated_as_obsolete()
    {
        // Deliberately includes keys the shipped example omits today.
        var appConfig = new AppConfig
        {
            XuiV3ApiBaseUrl = ValidPanelUrl,
            XuiV3ApiToken = "test-only",
            XuiV3ApiRootPath = "panel",
            XuiV3SubLinkBaseUrl = "https://sub.example.com",
            UserDatabasePath = "./Data/users.db",
            CredentialsDatabasePath = "./Data/credentials.db",
            ZibalMerchantCode = "test-only",
            MainChannel = "@main_channel",
            SupportAccount = "@support_username"
        };

        ConfigurationPreflight.ValidateEnabledFeatures(appConfig);

        var lines = ConfigurationPreflight.DescribeStartupReport(
            "@logger_channel",
            string.Empty,
            "@backup_channel",
            string.Empty,
            appConfig.XuiV3ApiBaseUrl);

        Assert.Single(lines);
    }

    /// <summary>
    /// Proves the shipped example documents that it is a template rather than a key whitelist.
    /// </summary>
    /// <remarks>
    /// The example is what an operator copies and, as the incident showed, what an operator can mistakenly prune
    /// against. The warning must therefore be machine-visible in the file itself, not only in external documentation.
    /// </remarks>
    [Fact]
    public void Example_template_declares_that_it_is_not_a_key_whitelist()
    {
        var example = ReadExampleTemplate();
        var readme = Assert.IsType<JArray>(example["_readme"]);
        var text = string.Join("\n", readme.Select(line => line.Value<string>() ?? string.Empty));

        Assert.True(readme.Count >= 5);
        Assert.Contains("not a key whitelist", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT obsolete", text, StringComparison.Ordinal);
        Assert.Contains("NEVER prune", text, StringComparison.Ordinal);
        Assert.Contains("jq", text, StringComparison.Ordinal);
        Assert.Contains("ConfigurationPreflight", text, StringComparison.Ordinal);
        Assert.Contains("docs/deployment.md", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves the example declares the keys whose absence caused the incident, and carries no real secret.
    /// </summary>
    [Fact]
    public void Example_template_declares_the_incident_critical_keys_without_secrets()
    {
        var example = ReadExampleTemplate();
        var path = ExampleTemplatePath();
        var text = File.ReadAllText(path);

        // The pruned keys must be visible in the template so they cannot look obsolete to a future operator.
        Assert.True(example.ContainsKey("loggerChannel"), "the example must document the global loggerChannel key");
        Assert.True(example.ContainsKey("backupChannel"), "the example must document the global backupChannel key");
        Assert.True(example.ContainsKey("xuiV3ApiBaseUrl"), "the example must document the panel base URL key");
        var bot = (JObject)Assert.IsType<JArray>(example["bots"])[0]!;
        Assert.True(bot.ContainsKey("loggerChannel"), "the example must document the per-bot loggerChannel key");
        Assert.True(bot.ContainsKey("backupChannel"), "the example must document the per-bot backupChannel key");

        // The global backup channel documents the numeric sentinel contract: 0 means "not configured".
        Assert.Equal(0L, example["backupChannel"]!.Value<long>());

        // No real Telegram bot token may appear in a tracked template.
        Assert.DoesNotMatch(new Regex(@"\d{6,}:[A-Za-z0-9_\-]{30,}"), text);
    }

    /// <summary>
    /// Proves validation has no file side effects: it never rewrites, prunes, or recreates a configuration file.
    /// </summary>
    /// <remarks>
    /// This is the strongest available statement of the safety property. The test snapshots the exact bytes and the
    /// directory listing, runs every entry point, and proves that neither changed and that every key is still present.
    /// A validator that silently normalized, pruned, or re-serialized a production file would fail here.
    /// </remarks>
    [Fact]
    public void Validation_never_rewrites_or_prunes_a_configuration_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AdminbotConfigSafety-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "configuration.json");
        Directory.CreateDirectory(directory);
        try
        {
            // A configuration shaped like production: it carries keys the shipped example does not contain, which is
            // exactly the content a prune would silently destroy.
            File.WriteAllText(path, """
            {
              "loggerChannel": "-1001234567890",
              "backupChannel": 0,
              "xuiV3ApiBaseUrl": "https://panel.example.com:54321/",
              "xuiV3ApiToken": "test-only",
              "zibalMerchantCode": "test-only",
              "priceCommon": [],
              "gozargahSiteSyncEnabled": false,
              "volumeExpirationReminderEnabled": false,
              "userDatabasePath": "./Data/users.db",
              "credentialsDatabasePath": "./Data/credentials.db"
            }
            """);

            var before = SHA256.HashData(File.ReadAllBytes(path));
            var beforeKeys = ConfigurationKeys(path);
            var beforeEntries = Directory.GetFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.Ordinal).ToArray();

            var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
            var appConfig = configuration.Get<AppConfig>()!;

            ConfigurationPreflight.ValidateEnabledFeatures(appConfig);
            ConfigurationPreflight.DescribeStartupReport(
                configuration["loggerChannel"],
                string.Empty,
                configuration["backupChannel"],
                string.Empty,
                appConfig.XuiV3ApiBaseUrl);

            var after = SHA256.HashData(File.ReadAllBytes(path));
            Assert.Equal(before, after);
            Assert.Equal(beforeKeys, ConfigurationKeys(path));
            Assert.Equal(beforeEntries, Directory.GetFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Reads the shipped configuration template as a JSON object.</summary>
    /// <returns>The parsed template; callers assert on individual keys.</returns>
    private static JObject ReadExampleTemplate() => JObject.Parse(File.ReadAllText(ExampleTemplatePath()));

    /// <summary>Resolves the shipped template path the same way the other configuration tests do.</summary>
    /// <returns>Absolute path of <c>Data/configuration.example.json</c> in the source tree.</returns>
    private static string ExampleTemplatePath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json"));

    /// <summary>Extracts the exact set of keys from a JSON configuration file for byte-level prune detection.</summary>
    /// <param name="path">Configuration file to read; never modified.</param>
    /// <returns>A stable, ordered key list so an added or removed key fails an equality assertion.</returns>
    private static string[] ConfigurationKeys(string path) =>
        JObject.Parse(File.ReadAllText(path)).Properties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
