using System.Text;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>Protects the authoritative host configuration against assembly-directory shadowing and unsafe fallback.</summary>
/// <remarks>Each test owns one temporary root. No process working directory, environment variable, private repository file, static database path or live Telegram transport is changed.</remarks>
public sealed class ApplicationConfigurationSourceTests
{
    /// <summary>Local migration reads the mapping selected by the explicit host rather than an unrelated assembly-directory Data copy.</summary>
    /// <remarks>The selected values are intentionally distinct from production/default values; the old unbased loader cannot satisfy this host-specific configuration.</remarks>
    [Fact]
    public void Explicit_content_root_controls_the_loaded_mapping_and_startup_binding()
    {
        using var files = new ConfigurationFiles();
        files.Write("""{"telegramEndpointRouting":{"localFileServerRoot":"/explicit-host-data","localFileHostRoot":"/explicit-host/local-data"}}""");
        var source = new ApplicationConfigurationSource(files.Root);
        using var configuration = (ConfigurationRoot)source.Load();
        var bound = configuration.Get<AppConfig>()!;
        Assert.Equal("/explicit-host-data", bound.TelegramEndpointRouting.LocalFileServerRoot);
        Assert.Equal("/explicit-host/local-data", bound.TelegramEndpointRouting.LocalFileHostRoot);
    }

    /// <summary>Absent authoritative configuration is fatal instead of borrowing another release's credentials or example.</summary>
    /// <remarks>The fallback-like example exists, but it cannot satisfy the required private source.</remarks>
    [Fact]
    public void Missing_private_source_does_not_load_the_example()
    {
        using var files = new ConfigurationFiles();
        File.WriteAllText(Path.Combine(files.Root, "Data", "configuration.example.json"), """{"telegramEndpointRouting":{"localFileServerRoot":"/wrong-fallback"}}""", Encoding.UTF8);
        var source = new ApplicationConfigurationSource(files.Root);
        Assert.Throws<FileNotFoundException>(() => source.Load());
    }

    /// <summary>Malformed selected JSON never produces a defaulted Cloud/Local policy that conceals the operator's configuration error.</summary>
    /// <remarks>Only syntactically invalid, synthetic content is used; exception bodies are never printed.</remarks>
    [Fact]
    public void Malformed_authoritative_json_is_an_explicit_error()
    {
        using var files = new ConfigurationFiles();
        files.Write("{\"telegramEndpointRouting\":");
        var source = new ApplicationConfigurationSource(files.Root);
        Assert.Throws<InvalidDataException>(() => source.Load());
    }

    /// <summary>Configuration-provider casing rules preserve existing camel-case nested routing settings when startup binds AppConfig.</summary>
    /// <remarks>This exercises the real JSON provider and binder, not a replacement dictionary or hand-built routing options.</remarks>
    [Fact]
    public void Existing_camel_case_configuration_binds_the_nested_mapping()
    {
        using var files = new ConfigurationFiles();
        files.Write("""{"telegramEndpointRouting":{"localFileServerRoot":"/data","localFileHostRoot":"/opt/telegram-media-downloader-bot/data","enabled":true}}""");
        using var configuration = (ConfigurationRoot)new ApplicationConfigurationSource(files.Root).Load();
        var options = configuration.Get<AppConfig>()!.TelegramEndpointRouting;
        Assert.True(options.Enabled);
        Assert.Equal("/data", options.LocalFileServerRoot);
        Assert.Equal("/opt/telegram-media-downloader-bot/data", options.LocalFileHostRoot);
    }

    /// <summary>Live diagnostic roots can differ from the bound runtime mapping without silently mutating an already admitted endpoint generation.</summary>
    /// <remarks>Explicit Reload makes the scenario deterministic without watcher sleeps; routing options remain the startup snapshot until a deliberate application restart.</remarks>
    [Fact]
    public void Reload_exposes_the_new_source_but_does_not_mutate_the_startup_mapping()
    {
        using var files = new ConfigurationFiles();
        files.Write("""{"telegramEndpointRouting":{"localFileServerRoot":"/old","localFileHostRoot":"/old-host"}}""");
        using var configuration = (ConfigurationRoot)new ApplicationConfigurationSource(files.Root).Load();
        var startup = configuration.Get<AppConfig>()!.TelegramEndpointRouting.ValidateAndSnapshot();
        files.Write("""{"telegramEndpointRouting":{"localFileServerRoot":"/data","localFileHostRoot":"/opt/telegram-media-downloader-bot/data"}}""");
        configuration.Reload();
        Assert.Equal("/data", configuration["telegramEndpointRouting:localFileServerRoot"]);
        Assert.Equal("/old", startup.LocalFileServerRoot);
        Assert.Equal("/old-host", startup.LocalFileHostRoot);
    }

    /// <summary>Only an explicit absolute host root may select the private configuration; arbitrary relative paths cannot fall back to the shell.</summary>
    /// <param name="contentRoot">Absent or relative synthetic input that is invalid for the host environment contract.</param>
    /// <remarks>No filesystem lookup occurs for these invalid roots.</remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-data")]
    public void Absent_or_relative_host_roots_are_rejected(string? contentRoot)
    {
        Assert.Throws<ArgumentException>(() => new ApplicationConfigurationSource(contentRoot!));
    }

    /// <summary>Owns only one unique test configuration tree outside the repository.</summary>
    private sealed class ConfigurationFiles : IDisposable
    {
        /// <summary>The explicit absolute root passed to the real production loader.</summary>
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "adminbot-config-source-tests-" + Guid.NewGuid().ToString("N"));

        /// <summary>Creates the private Data directory without using any production files.</summary>
        public ConfigurationFiles() => Directory.CreateDirectory(Path.Combine(Root, "Data"));

        /// <summary>Writes synthetic UTF-8 configuration for the real JSON provider.</summary>
        /// <param name="json">Required synthetic JSON or deliberately malformed syntax; must not contain real credentials or customer data.</param>
        /// <remarks>Only the owned temporary configuration file is replaced.</remarks>
        public void Write(string json) => File.WriteAllText(Path.Combine(Root, "Data", "configuration.json"), json, Encoding.UTF8);

        /// <summary>Deletes only the owned tree after configuration roots and watchers have been disposed.</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
