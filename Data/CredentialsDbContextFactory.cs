using Microsoft.EntityFrameworkCore;

/// <summary>Creates isolated contexts for the global user profile and wallet database.</summary>
/// <remarks>The factory is safe to share; contexts and their tracked entities are not.</remarks>
public sealed class CredentialsDbContextFactory : IDbContextFactory<CredentialsDbContext>
{
    private readonly DbContextOptions<CredentialsDbContext> _options;

    /// <summary>Stores immutable connection options for per-operation contexts.</summary>
    /// <param name="options">Required credentials.db options, using a five-second timeout and private cache.</param>
    /// <remarks>The caller owns and disposes each context. The factory holds immutable options and is safe to share across bots and webhook callers.</remarks>
    public CredentialsDbContextFactory(DbContextOptions<CredentialsDbContext> options) => _options = options;

    /// <summary>Creates a context owned exclusively by the calling database operation.</summary>
    /// <returns>A new context which the caller must dispose; never cache it in a service.</returns>
    /// <example><code>await using var db = factory.CreateDbContext();</code></example>
    public CredentialsDbContext CreateDbContext() => new(_options);
}
