using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Operation-local EF context for global Telegram profiles and wallet receipts.</summary>
/// <remarks>Never share this context between callers. Use CredentialsStore or CredentialsDbContextFactory and dispose each operation.</remarks>
public class CredentialsDbContext : DbContext
{
    /// <summary>Global profiles keyed by Telegram user id, including the shared wallet balance.</summary>
    public DbSet<CredUser> Users { get; set; }
    /// <summary>Append-only wallet receipts; only reconciliation timestamps may change after insertion.</summary>
    public DbSet<WalletOperation> WalletOperations { get; set; }

    /// <summary>Creates an exclusively owned credentials unit of work.</summary>
    /// <param name="options">Required factory options for the global credentials database.</param>
    public CredentialsDbContext(DbContextOptions<CredentialsDbContext> options) : base(options) { }

    /// <summary>Maps global profiles and immutable wallet receipts; receipts have no cross-database foreign key.</summary>
    /// <param name="modelBuilder">EF model builder for credentials.db.</param>
    /// <remarks>Schema configuration adds immutable receipt keys without a cross-database foreign key or financial backfill.</remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WalletOperation>(entity =>
        {
            entity.HasKey(x => x.OperationKey);
            entity.Property(x => x.OperationKey).HasMaxLength(240);
            entity.HasIndex(x => new { x.ReconciledAtUtc, x.CreatedAtUtc });
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.InboxSequence);
        });
        modelBuilder.Entity<CredUser>()
            .HasKey(c => c.TelegramUserId);

        modelBuilder.Entity<CredUser>()
            .Property(c => c.TelegramUserId)
            .ValueGeneratedNever(); // This tells EF Core not to expect a database-generated value

    }

}
