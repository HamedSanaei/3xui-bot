using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Creates a real inactive publisher for existing unrelated focused handler-constructor tests.</summary>
/// <remarks>No background service, database initialization, Telegram probe or alternate production implementation is started.</remarks>
internal static class PublicChannelPostTestSupport
{
    /// <summary>Supplies the required manager dependency without starting channel publication in unrelated scenarios.</summary>
    /// <param name="configuration">The focused test's configuration and configured admin/Owned identities, never production secrets.</param>
    /// <returns>A real unstarted manager; the test/container owns its lifetime. Its SQLite factory remains unused unless explicitly exercised.</returns>
    /// <remarks>Uses an options-bound in-memory SQLite factory, exact registry/provider and the normal delivery policy;
    /// it neither changes process-wide database paths nor creates another test project.</remarks>
    /// <exception cref="ArgumentNullException">The test configuration is null.</exception>
    /// <example><code>publicChannelPosts: PublicChannelPostTestSupport.CreateInactiveManager(configuration)</code></example>
    internal static PublicChannelPostManager CreateInactiveManager(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var users = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>().UseSqlite("Data Source=:memory:").Options);
        var registry = new BotRegistry(configuration);
        return new PublicChannelPostManager(users, registry, new BotClientProvider(registry), configuration,
            TelegramForegroundDeliveryPolicy.Production, NullLogger<PublicChannelPostManager>.Instance);
    }
}
