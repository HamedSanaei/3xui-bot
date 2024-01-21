using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

public class CredentialsDbContext : DbContext
{
    public DbSet<CredUser> Users { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Replace "YourUserDbConnectionString" with your actual connection string
        optionsBuilder.UseSqlite("Data Source=./Data/credentials.db");
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure the UserData entity
        modelBuilder.Entity<CredUser>()
            .HasKey(u => u.Id); // Set the Id property as the primary key
    }

    public async Task<CredUser> GetUserStatus(long userId, CredUser credUser)
    {
        var context = new CredentialsDbContext();
        context.Database.EnsureCreated(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == userId);

        if (existingUser != null)
        {
            return existingUser;
        }
        else
        {

            await context.Users.AddAsync(credUser);
            await context.SaveChangesAsync();
            return credUser;
        }

    }

}
