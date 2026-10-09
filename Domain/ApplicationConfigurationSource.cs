using Microsoft.Extensions.Configuration;

namespace Adminbot.Domain;

/// <summary>Identifies and loads the one private application configuration beneath the host's explicit content root.</summary>
/// <remarks>Assembly-directory and shell-directory fallback are forbidden: configuration, databases and configuration editors must agree on the same host-owned Data directory. This service never rewrites configuration or starts a receiver.</remarks>
public sealed class ApplicationConfigurationSource
{
    /// <summary>The absolute private JSON source used at startup; safe only for authorized local/operator diagnostics, never a public endpoint.</summary>
    public string FilePath { get; }

    /// <summary>The explicit absolute host content root, retained for the configuration provider's file watcher.</summary>
    private readonly string _contentRoot;

    /// <summary>Resolves the host-owned Data/configuration.json without probing another directory or reading any secrets.</summary>
    /// <param name="contentRootPath">Required fully qualified application content root from IWebHostEnvironment.ContentRootPath, not an operator-supplied callback or guessed assembly directory.</param>
    /// <remarks>The existing deployment's WorkingDirectory/content-root and preserved Data layout remain authoritative. Missing files are not repaired from the example or another release.</remarks>
    /// <exception cref="ArgumentException">The content root is absent or not fully qualified.</exception>
    /// <example><code>var source = new ApplicationConfigurationSource(builder.Environment.ContentRootPath);</code></example>
    public ApplicationConfigurationSource(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath) || !Path.IsPathFullyQualified(contentRootPath))
            throw new ArgumentException("Application configuration requires an explicit absolute content root.", nameof(contentRootPath));
        _contentRoot = Path.GetFullPath(contentRootPath);
        FilePath = Path.Combine(_contentRoot, "Data", "configuration.json");
    }

    /// <summary>Loads the exact authoritative UTF-8 JSON source with the existing live-provider reload behavior.</summary>
    /// <returns>The live configuration root, including unknown production keys. The caller owns its lifetime and must dispose it through IDisposable after the host stops; bound startup option snapshots remain startup-bound.</returns>
    /// <remarks>Only this file is loaded. A missing or malformed source is an explicit startup error, never permission to use a stale assembly-directory file. No secrets or configuration values are logged.</remarks>
    /// <exception cref="FileNotFoundException">The required authoritative configuration file does not exist.</exception>
    /// <exception cref="InvalidDataException">The selected file contains malformed JSON.</exception>
    /// <example><code>var configuration = source.Load(); // Register this same root as IConfiguration.</code></example>
    public IConfigurationRoot Load() => new ConfigurationBuilder()
        .SetBasePath(_contentRoot)
        .AddJsonFile(FilePath, optional: false, reloadOnChange: true)
        .Build();
}
