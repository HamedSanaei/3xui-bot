using System.Net;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Startup-bound trusted endpoint settings; configuration cannot redirect tokens to arbitrary origins.</summary>
public sealed class TelegramEndpointRoutingOptions
{
    /// <summary>Enables endpoint management; existing bots still default to Cloud without migrations.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Official production Cloud origin; only HTTPS api.telegram.org without a nondefault port is allowed.</summary>
    public string CloudBaseUrl { get; set; } = "https://api.telegram.org";
    /// <summary>Existing HTTP loopback Local origin; remote hosts and URL paths are prohibited.</summary>
    public string LocalBaseUrl { get; set; } = "http://127.0.0.1:8081";
    /// <summary>Health polling interval in seconds, bounded from 5 to 3600.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 15;
    /// <summary>One health request deadline in seconds, bounded from 1 to 30.</summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 3;
    /// <summary>Consecutive relevant health failures needed to declare an outage, from 1 to 20.</summary>
    public int FailureThreshold { get; set; } = 3;
    /// <summary>Consecutive health successes needed for recovery, from 1 to 20.</summary>
    public int RecoveryThreshold { get; set; } = 2;
    /// <summary>Whether recovered Local service may initiate automatic failback; defaults false.</summary>
    public bool AutomaticFailback { get; set; }
    /// <summary>Maximum admitted-handler drain duration in seconds, from 1 to 300.</summary>
    public int MigrationDrainSeconds { get; set; } = 30;
    /// <summary>Migration request timeout in seconds, from 1 to 120.</summary>
    public int MigrationTimeoutSeconds { get; set; } = 30;
    /// <summary>Maximum safe recovery attempts before visible manual intervention, from 1 to 100.</summary>
    public int RecoveryMaxAttempts { get; set; } = 12;
    /// <summary>Maximum exponential retry delay in seconds, from 5 to 3600.</summary>
    public int RetryMaxSeconds { get; set; } = 300;
    /// <summary>Maximum actual safe notification attempts before manual review, from 1 to 100; missing logger/sender prerequisites do not consume this budget.</summary>
    public int NotificationMaxAttempts { get; set; } = 12;
    /// <summary>Trusted absolute Local server root, normally a POSIX container directory; empty blocks Local migration.</summary>
    public string LocalFileServerRoot { get; set; } = "";
    /// <summary>Trusted absolute host directory mapped to the server root; empty blocks Local migration.</summary>
    public string LocalFileHostRoot { get; set; } = "";

    /// <summary>Validates trusted origins and resource bounds, then returns an independently mutable startup snapshot.</summary>
    /// <returns>A detached validated settings copy; URLs contain only trusted endpoint origins.</returns>
    /// <exception cref="ArgumentException">An origin, identifier, mapping pair, or bound is unsafe.</exception>
    /// <remarks>Missing mapping roots are allowed at startup, but <see cref="HasLocalFileMapping"/> must gate first Local migration. Tests substitute HTTP transports, never relax origin validation.</remarks>
    /// <example><code>var routing = configured.ValidateAndSnapshot();</code></example>
    public TelegramEndpointRoutingOptions ValidateAndSnapshot()
    {
        if (!Uri.TryCreate(CloudBaseUrl, UriKind.Absolute, out var cloud) || cloud.Scheme != "https" ||
            !string.Equals(cloud.Host, "api.telegram.org", StringComparison.OrdinalIgnoreCase) || !cloud.IsDefaultPort || !OriginOnly(cloud))
            throw new ArgumentException("Cloud endpoint must be the official HTTPS origin.");
        if (!Uri.TryCreate(LocalBaseUrl, UriKind.Absolute, out var local) || local.Scheme != "http" || !OriginOnly(local) ||
            !(string.Equals(local.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
              (IPAddress.TryParse(local.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))))
            throw new ArgumentException("Local endpoint must be an HTTP loopback origin.");
        Bound(HealthCheckIntervalSeconds, 5, 3600, nameof(HealthCheckIntervalSeconds));
        Bound(HealthCheckTimeoutSeconds, 1, 30, nameof(HealthCheckTimeoutSeconds));
        Bound(FailureThreshold, 1, 20, nameof(FailureThreshold));
        Bound(RecoveryThreshold, 1, 20, nameof(RecoveryThreshold));
        Bound(MigrationDrainSeconds, 1, 300, nameof(MigrationDrainSeconds));
        Bound(MigrationTimeoutSeconds, 1, 120, nameof(MigrationTimeoutSeconds));
        Bound(RecoveryMaxAttempts, 1, 100, nameof(RecoveryMaxAttempts));
        Bound(RetryMaxSeconds, 5, 3600, nameof(RetryMaxSeconds));
        Bound(NotificationMaxAttempts, 1, 100, nameof(NotificationMaxAttempts));
        var server = LocalFileServerRoot ?? "";
        var host = LocalFileHostRoot ?? "";
        if ((server.Length == 0) != (host.Length == 0) ||
            (server.Length > 0 && (!AbsoluteServerRoot(server) || !Path.IsPathFullyQualified(host) || host.Any(char.IsControl))))
            throw new ArgumentException("Local file mapping requires two trusted absolute roots.");
        var copy = (TelegramEndpointRoutingOptions)MemberwiseClone();
        copy.CloudBaseUrl = cloud.GetLeftPart(UriPartial.Authority);
        copy.LocalBaseUrl = local.GetLeftPart(UriPartial.Authority);
        copy.LocalFileServerRoot = server;
        copy.LocalFileHostRoot = host;
        return copy;
    }

    /// <summary>Whether both trusted file roots are explicitly configured; file existence is checked by the host mapper.</summary>
    public bool HasLocalFileMapping => !string.IsNullOrEmpty(LocalFileServerRoot) && !string.IsNullOrEmpty(LocalFileHostRoot);


    /// <summary>Rejects path, credential, query, and fragment components in an origin.</summary>
    /// <param name="uri">Parsed required absolute endpoint URI.</param>
    /// <returns>True for an origin-only URI.</returns>
    private static bool OriginOnly(Uri uri)
    {
        if (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) return false;
        var original = uri.OriginalString;
        if (original != original.Trim() || original.Any(char.IsControl) || original.Contains('\\')) return false;
        var pathStart = original.IndexOf('/', original.IndexOf("://", StringComparison.Ordinal) + 3);
        return pathStart < 0 || original[pathStart..] == "/";
    }

    /// <summary>Accepts trusted absolute POSIX or host-native server roots without parent traversal.</summary>
    /// <param name="root">Required Local server mapping root.</param>
    /// <returns>True for a nontrivial absolute path without control characters or parent segments.</returns>
    private static bool AbsoluteServerRoot(string root) => !root.Any(char.IsControl) && root.Length > 1 &&
        (root.StartsWith('/') || Path.IsPathFullyQualified(root)) && !root.Replace('\\', '/').Split('/').Contains("..");

    /// <summary>Enforces a finite startup resource bound.</summary>
    /// <param name="value">Configured numeric count or duration in seconds.</param>
    /// <param name="minimum">Inclusive safe lower bound.</param>
    /// <param name="maximum">Inclusive safe upper bound.</param>
    /// <param name="name">Internal option property name, never user text.</param>
    /// <exception cref="ArgumentException">The configured value lies outside the bounds.</exception>
    private static void Bound(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum) throw new ArgumentException("Endpoint option is outside safe bounds.", name);
    }
}
