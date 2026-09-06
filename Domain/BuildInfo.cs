using System.Reflection;

/// <summary>Exposes non-secret immutable source identity embedded in the running application assembly.</summary>
/// <remarks>
/// The .NET SDK appends <c>SourceRevisionId</c> to the informational version. Release tooling must build from a clean,
/// exact commit and may pass <c>/p:SourceRevisionId=&lt;sha&gt;</c> explicitly. No filesystem or Git command is used at runtime.
/// </remarks>
public static class BuildInfo
{
    /// <summary>Gets the source commit SHA embedded by the SDK, or <c>unknown</c> when provenance was not supplied.</summary>
    public static string Commit { get; } = ReadCommit();

    /// <summary>Gets the MSBuild configuration embedded as assembly metadata, normally <c>Release</c>.</summary>
    public static string Configuration { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => string.Equals(x.Key, "BuildConfiguration", StringComparison.Ordinal))?.Value ?? "unknown";

    /// <summary>Extracts a full hexadecimal commit SHA from the SDK informational version.</summary>
    /// <returns>A forty-character lowercase or uppercase hexadecimal SHA, or <c>unknown</c>.</returns>
    /// <remarks>The full informational version is not returned because it also contains the product version.</remarks>
    private static string ReadCommit()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var candidate = informational?.Split('+').LastOrDefault();
        return candidate?.Length == 40 && candidate.All(Uri.IsHexDigit) ? candidate : "unknown";
    }
}
