using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>Builds migration models without loading production configuration, secrets, or hosted services.</summary>
/// <remarks>Migration generation uses an isolated filename and never migrates a deployed database.</remarks>
public sealed class DatabaseDesignTimeFactory : IDesignTimeDbContextFactory<UserDbContext>, IDesignTimeDbContextFactory<CredentialsDbContext>
{
    /// <summary>Creates the users.db migration model independently of the web host.</summary>
    /// <param name="args">Unused EF tooling arguments; no production credentials are accepted.</param>
    /// <returns>A tooling-owned users context that EF must dispose.</returns>
    /// <remarks>Design tooling uses an isolated design database filename and does not start receivers, load private configuration, or apply financial effects.</remarks>
    UserDbContext IDesignTimeDbContextFactory<UserDbContext>.CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<UserDbContext>().UseSqlite(SqliteOperation.ConnectionString("migration-design-users.db")).Options);

    /// <summary>Creates the credentials.db migration model independently of the web host.</summary>
    /// <param name="args">Unused EF tooling arguments; no production credentials are accepted.</param>
    /// <returns>A tooling-owned credentials context that EF must dispose.</returns>
    /// <remarks>Design tooling uses an isolated design database filename and does not start receivers, load private configuration, or apply financial effects.</remarks>
    CredentialsDbContext IDesignTimeDbContextFactory<CredentialsDbContext>.CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CredentialsDbContext>().UseSqlite(SqliteOperation.ConnectionString("migration-design-credentials.db")).Options);
}
