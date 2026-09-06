using Adminbot.Domain;
using Adminbot.Migrations;
using Adminbot.Migrations.CredentialsDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Diagnostics;
using Xunit;

/// <summary>Regression coverage for migration drift, historical upgrades, artifact preflight, and build provenance.</summary>
public sealed class MigrationDeploymentGuardTests
{
    /// <summary>Both current production models must exactly match their real migration snapshots.</summary>
    /// <returns>A task completing after the two independent EF migration assemblies are checked.</returns>
    /// <remarks>This test does not create a database and cannot be masked by <c>EnsureCreated</c>.</remarks>
    [Fact]
    public async Task Both_contexts_have_no_pending_model_changes()
    {
        using var fixture = new MigrationFixture();
        await using var users = fixture.CreateUsers();
        await using var credentials = fixture.CreateCredentials();
        Assert.False(users.Database.HasPendingModelChanges());
        Assert.False(credentials.Database.HasPendingModelChanges());
    }

    /// <summary>A deliberately added EF property without a matching snapshot is detected by the migration model differ.</summary>
    /// <returns>A completed task after the intentional drift is rejected.</returns>
    /// <remarks>This protects the guard itself from becoming a constant-success check.</remarks>
    [Fact]
    public async Task Intentionally_modified_model_without_migration_fails_guard()
    {
        var options = new DbContextOptionsBuilder<IntentionalDriftContext>().UseSqlite("Data Source=:memory:").Options;
        await using var context = new IntentionalDriftContext(options);
        Assert.True(MigrationPreflight.ModelHasDifferences(context, new IntentionalBaselineSnapshot()));
    }

    /// <summary>Fresh users.db and credentials.db files are created only through real migrations and remain drift-free.</summary>
    /// <returns>A task completing after both migration chains reach their latest schemas.</returns>
    [Fact]
    public async Task Fresh_databases_migrate_and_match_current_models()
    {
        using var fixture = new MigrationFixture();
        await using var users = fixture.CreateUsers();
        await using var credentials = fixture.CreateCredentials();
        await users.Database.MigrateAsync();
        await credentials.Database.MigrateAsync();
        Assert.False(users.Database.HasPendingModelChanges());
        Assert.False(credentials.Database.HasPendingModelChanges());
        Assert.NotEmpty(await users.Database.GetAppliedMigrationsAsync());
        Assert.NotEmpty(await credentials.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>Known historical schemas upgrade through every later migration and finish with matching models.</summary>
    /// <returns>A task completing after users.db and credentials.db historical upgrade paths succeed.</returns>
    /// <remarks>No historical financial effect is replayed and <c>EnsureCreated</c> is never used.</remarks>
    [Fact]
    public async Task Historical_databases_upgrade_and_match_current_models()
    {
        using var fixture = new MigrationFixture();
        await using var users = fixture.CreateUsers();
        await using var credentials = fixture.CreateCredentials();
        await users.GetService<IMigrator>().MigrateAsync("20260901234439_AddTenantRenewalServiceResolutionMode");
        await credentials.GetService<IMigrator>().MigrateAsync("20260611010000_AddBlockedUsers");
        await users.Database.MigrateAsync();
        await credentials.Database.MigrateAsync();
        Assert.False(users.Database.HasPendingModelChanges());
        Assert.False(credentials.Database.HasPendingModelChanges());
    }

    /// <summary>The application preflight migrates online-backup copies and leaves source migration histories unchanged.</summary>
    /// <returns>A task completing after production-shaped copy validation returns exit code zero.</returns>
    /// <remarks>The exact executable path invokes this same method before hosting; no receiver or HTTP server is registered.</remarks>
    [Fact]
    public async Task Published_migration_preflight_validates_copies_without_mutating_sources()
    {
        using var fixture = new MigrationFixture();
        await using (var users = fixture.CreateUsers()) await users.Database.MigrateAsync();
        await using (var credentials = fixture.CreateCredentials()) await credentials.Database.MigrateAsync();
        var usersWrite = File.GetLastWriteTimeUtc(fixture.UsersPath);
        var credentialsWrite = File.GetLastWriteTimeUtc(fixture.CredentialsPath);
        using var output = new StringWriter();
        var exitCode = await MigrationPreflight.RunAsync(
            ["--migration-check", "--users-source", fixture.UsersPath, "--credentials-source", fixture.CredentialsPath],
            output,
            default);
        Assert.Equal(0, exitCode);
        Assert.Contains("UserDbContext: OK", output.ToString());
        Assert.Contains("CredentialsDbContext: OK", output.ToString());
        Assert.Equal(usersWrite, File.GetLastWriteTimeUtc(fixture.UsersPath));
        Assert.Equal(credentialsWrite, File.GetLastWriteTimeUtc(fixture.CredentialsPath));
    }

    /// <summary>Release metadata contains the exact repository commit and Release build configuration.</summary>
    /// <returns>A task completing after comparing embedded metadata with the checked-out Git commit.</returns>
    /// <remarks>Deployment separately rejects dirty source, making this commit sufficient artifact provenance.</remarks>
    [Fact]
    public async Task Build_metadata_contains_exact_source_commit()
    {
        var start = new ProcessStartInfo("git", "rev-parse HEAD")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..")) };
        using var process = Process.Start(start)!;
        var expected = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(expected, BuildInfo.Commit);
        Assert.Equal("Release", BuildInfo.Configuration);
    }

    /// <summary>Owns two empty SQLite paths whose schemas are created exclusively by real migrations.</summary>
    private sealed class MigrationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdminbotMigrationGuard-" + Guid.NewGuid().ToString("N"));
        /// <summary>Gets the isolated users.db path used by this fixture.</summary>
        public string UsersPath => Path.Combine(_directory, "users.db");
        /// <summary>Gets the isolated credentials.db path used by this fixture.</summary>
        public string CredentialsPath => Path.Combine(_directory, "credentials.db");
        /// <summary>Creates the fixture directory without creating either database schema.</summary>
        public MigrationFixture() => Directory.CreateDirectory(_directory);
        /// <summary>Creates an independently owned UserDbContext connected to the empty or migrated fixture file.</summary>
        /// <returns>A disposable context using the production SQLite options and migration assembly.</returns>
        public UserDbContext CreateUsers() => new(new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(SqliteOperation.ConnectionString(UsersPath)).Options);
        /// <summary>Creates an independently owned CredentialsDbContext connected to the fixture file.</summary>
        /// <returns>A disposable context using the production SQLite options and migration assembly.</returns>
        public CredentialsDbContext CreateCredentials() => new(new DbContextOptionsBuilder<CredentialsDbContext>()
            .UseSqlite(SqliteOperation.ConnectionString(CredentialsPath)).Options);
        /// <summary>Clears SQLite pools and removes only this fixture's random OS-temporary directory.</summary>
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid migration fixture directory.");
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Minimal current model containing an intentional unmigrated column.</summary>
    private sealed class IntentionalDriftContext(DbContextOptions<IntentionalDriftContext> options) : DbContext(options)
    {
        /// <summary>Builds a model with one extra property absent from the baseline snapshot.</summary>
        /// <param name="modelBuilder">EF model builder used only by this guard regression test.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<IntentionalEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UnmigratedValue);
        });
    }

    /// <summary>Baseline snapshot intentionally omitting the current model's extra property.</summary>
    private sealed class IntentionalBaselineSnapshot : ModelSnapshot
    {
        /// <summary>Builds the last-migration model containing only the entity primary key.</summary>
        /// <param name="modelBuilder">Snapshot model builder supplied by EF.</param>
        protected override void BuildModel(ModelBuilder modelBuilder) => modelBuilder.Entity<IntentionalEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
        });
    }

    /// <summary>Test-only entity whose second property simulates accidental source model drift.</summary>
    private sealed class IntentionalEntity
    {
        /// <summary>Primary key present in both current model and snapshot.</summary>
        public int Id { get; set; }
        /// <summary>Property intentionally absent from the snapshot.</summary>
        public string? UnmigratedValue { get; set; }
    }
}
