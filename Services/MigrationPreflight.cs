using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Validates the exact published EF models and migrations against isolated temporary SQLite databases.</summary>
/// <remarks>
/// This non-serving mode starts no web host, Telegram receiver, hosted service, or remote logger. Optional source
/// databases are copied with SQLite online backup before migrations run, so production business data is never changed.
/// </remarks>
public static class MigrationPreflight
{
    private const string Mode = "--migration-check";

    /// <summary>Determines whether command-line arguments select the non-serving migration preflight.</summary>
    /// <param name="args">Raw application arguments. Null and empty arrays mean normal serving mode.</param>
    /// <returns><c>true</c> only when the exact <c>--migration-check</c> switch is present.</returns>
    public static bool IsRequested(IReadOnlyCollection<string> args) => args?.Contains(Mode, StringComparer.Ordinal) == true;

    /// <summary>Runs pending-model and migration checks for both application DbContexts using temporary databases.</summary>
    /// <param name="args">
    /// Application arguments. Optional <c>--users-source PATH</c> and <c>--credentials-source PATH</c> must be supplied
    /// together and identify existing SQLite databases to copy through the online backup API.
    /// </param>
    /// <param name="output">Non-secret output writer used by deployment and CI logs.</param>
    /// <param name="cancellationToken">Cancellation token for backup, migration, and cleanup work.</param>
    /// <returns>Zero only when both snapshots match and both isolated databases migrate successfully; otherwise one.</returns>
    /// <remarks>
    /// A model mismatch is checked before any migration. Database migration then runs only on fresh files or online
    /// backup copies, followed by the same model check. Paths, connection strings, rows, and payloads are never printed.
    /// </remarks>
    /// <example><code>./Adminbot --migration-check --users-source /opt/app/shared/Data/users.db --credentials-source /opt/app/shared/Data/credentials.db</code></example>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "AdminbotMigrationPreflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var usersSource = ReadOption(args, "--users-source");
            var credentialsSource = ReadOption(args, "--credentials-source");
            if (string.IsNullOrWhiteSpace(usersSource) != string.IsNullOrWhiteSpace(credentialsSource))
                throw new ArgumentException("Both source database options are required together.");

            var usersPath = Path.Combine(temporaryDirectory, "users.db");
            var credentialsPath = Path.Combine(temporaryDirectory, "credentials.db");
            if (usersSource != null)
            {
                BackupDatabase(usersSource, usersPath);
                BackupDatabase(credentialsSource!, credentialsPath);
            }

            await output.WriteLineAsync("Migration preflight:");
            var usersOk = await ValidateAsync(
                new UserDbContext(new DbContextOptionsBuilder<UserDbContext>()
                    .UseSqlite(SqliteOperation.ConnectionString(usersPath)).Options),
                "UserDbContext", output, cancellationToken);
            var credentialsOk = await ValidateAsync(
                new CredentialsDbContext(new DbContextOptionsBuilder<CredentialsDbContext>()
                    .UseSqlite(SqliteOperation.ConnectionString(credentialsPath)).Options),
                "CredentialsDbContext", output, cancellationToken);
            return usersOk && credentialsOk ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Migration preflight failed: {SanitizeFailure(ex)}");
            return 1;
        }
        finally
        {
            try { SqliteConnection.ClearAllPools(); }
            catch { /* A missing native provider is already reported as preflight failure; cleanup must preserve exit 1. */ }
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>Checks one real migration assembly/model before and after applying migrations to its isolated database.</summary>
    /// <param name="context">Owned context connected only to a temporary database.</param>
    /// <param name="name">Fixed safe context name printed in preflight output.</param>
    /// <param name="output">Deployment or CI output writer.</param>
    /// <param name="cancellationToken">Cancellation token for EF migration work.</param>
    /// <returns><c>true</c> when the snapshot matches and migration succeeds; otherwise <c>false</c>.</returns>
    private static async Task<bool> ValidateAsync(
        DbContext context,
        string name,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using (context)
        {
            if (context.Database.HasPendingModelChanges())
            {
                await output.WriteLineAsync($"{name}: PENDING_MODEL_CHANGES");
                return false;
            }

            try
            {
                await context.Database.MigrateAsync(cancellationToken);
                if (context.Database.HasPendingModelChanges())
                {
                    await output.WriteLineAsync($"{name}: PENDING_MODEL_CHANGES");
                    return false;
                }
                await output.WriteLineAsync($"{name}: OK");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await output.WriteLineAsync($"{name}: MIGRATION_FAILED ({SanitizeFailure(ex)})");
                return false;
            }
        }
    }

    /// <summary>Compares an explicitly supplied migration snapshot with a context's design-time relational model.</summary>
    /// <param name="context">Context whose current design-time model is being validated.</param>
    /// <param name="snapshot">Last migration snapshot from the same migration assembly.</param>
    /// <returns><c>true</c> when an entity, property, key, index, constraint, or relational annotation differs.</returns>
    /// <remarks>This test seam uses the same EF model differ behind pending-model validation and performs no database I/O.</remarks>
    internal static bool ModelHasDifferences(DbContext context, ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var current = context.GetService<IDesignTimeModel>().Model;
        var snapshotModel = context.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot.Model, designTime: true, validationLogger: null);
        return differ.HasDifferences(snapshotModel.GetRelationalModel(), current.GetRelationalModel());
    }

    /// <summary>Creates a consistent SQLite online backup without changing or logging the source database.</summary>
    /// <param name="sourcePath">Required existing users.db or credentials.db path supplied by deployment tooling.</param>
    /// <param name="destinationPath">New file inside the preflight-owned temporary directory.</param>
    /// <exception cref="FileNotFoundException">The requested production-shaped source database does not exist.</exception>
    private static void BackupDatabase(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("A migration preflight source database was not found.");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(sourcePath), Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        source.Open(); destination.Open(); source.BackupDatabase(destination);
    }

    /// <summary>Reads one required-value option without accepting duplicates or arbitrary trailing values.</summary>
    /// <param name="args">Raw preflight arguments.</param>
    /// <param name="name">Exact supported option name.</param>
    /// <returns>The supplied nonempty value, or null when the option is absent.</returns>
    /// <exception cref="ArgumentException">The option is duplicated or lacks a value.</exception>
    private static string ReadOption(IReadOnlyList<string> args, string name)
    {
        var indexes = Enumerable.Range(0, args?.Count ?? 0).Where(i => args![i] == name).ToArray();
        if (indexes.Length == 0) return null;
        if (indexes.Length != 1 || indexes[0] + 1 >= args!.Count || string.IsNullOrWhiteSpace(args[indexes[0] + 1]))
            throw new ArgumentException("Migration preflight options require one value.");
        return args[indexes[0] + 1];
    }

    /// <summary>Maps preflight exceptions to fixed categories without exposing database paths or row contents.</summary>
    /// <param name="exception">Caught local validation exception.</param>
    /// <returns>A safe fixed category for deployment logs.</returns>
    private static string SanitizeFailure(Exception exception) => exception switch
    {
        FileNotFoundException => "SOURCE_DATABASE_NOT_FOUND",
        ArgumentException => "INVALID_ARGUMENTS",
        SqliteException => "SQLITE_ERROR",
        InvalidOperationException => "MODEL_OR_MIGRATION_ERROR",
        _ => "UNEXPECTED_ERROR"
    };
}
