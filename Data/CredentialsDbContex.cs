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
        context.Database.Migrate(); // Create the database if it doesn't exist

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


    public async Task SaveUserStatus(CredUser credUser)

    {
        var context = new CredentialsDbContext();
        context.Database.Migrate(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Id == credUser.Id);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            context.Users.Add(credUser);
        }
        else
        {
            // // User already exists, update the user's information if needed
            // if (credUser.SelectedCountry != null) existingUser.SelectedCountry = user.SelectedCountry;
            // if (user.SelectedPeriod != null) existingUser.SelectedPeriod = user.SelectedPeriod;
            // if (user.Type != null) existingUser.Type = user.Type;
            // if (user.LastStep != null) existingUser.LastStep = user.LastStep;
            // if (user.TotoalGB != null) existingUser.TotoalGB = user.TotoalGB;
            // if (user.ConfigLink != null) existingUser.ConfigLink = user.ConfigLink;
            // if (user.Email != null) existingUser.Email = user.Email;
            // if (user.Flow != null) existingUser.Flow = user.Flow;
            // if (user._ConfigPrice != null) existingUser._ConfigPrice = user._ConfigPrice;

        }
        await context.SaveChangesAsync();

    }

    public async Task<bool> Pay(CredUser credUser, long amount)
    {
        CredentialsDbContext credentialsDbContext = new CredentialsDbContext();
        credentialsDbContext.Attach<CredUser>(credUser);

        if (credUser.AccountBalance < amount)
            return false;
        credUser.AccountBalance -= amount;
        await credentialsDbContext.SaveChangesAsync();
        return true;
    }
    public async Task<CredUser> GetUserStatus(long userId)
    {
        return await GetUserStatus(userId, new CredUser { TelegramUserId = userId });
    }

}
