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

    public async Task<CredUser> GetUserStatus(CredUser credUser)
    {
        var context = new CredentialsDbContext();
        context.Database.Migrate(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser.TelegramUserId);

        if (existingUser != null)
        {
            // update public infos
            await context.SaveUserStatus(credUser);
            return existingUser;
        }
        else
        {
            // Add
            await context.Users.AddAsync(credUser);
            await context.SaveChangesAsync();
            return credUser;
        }
    }

    public async Task<CredUser> GetUserStatusWithId(long credUser)
    {

        var context = new CredentialsDbContext();
        context.Database.Migrate(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser);
        return existingUser;
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
            if (!string.IsNullOrEmpty(credUser.Username)) existingUser.Username = credUser.Username;
            if (!string.IsNullOrEmpty(credUser.LastName)) existingUser.LastName = credUser.LastName;
            if (credUser.Email != null) existingUser.Email = credUser.Email;
            if (credUser.ChatID != existingUser.ChatID) existingUser.ChatID = credUser.ChatID;
            if (credUser.LanguageCode != existingUser.LanguageCode) existingUser.LanguageCode = credUser.LanguageCode;
            if (credUser.FirstName != existingUser.FirstName) existingUser.FirstName = credUser.FirstName;
            if (credUser.IsColleague != existingUser.IsColleague) existingUser.IsColleague = credUser.IsColleague;
            if (credUser.TelegramUserId != existingUser.TelegramUserId) existingUser.TelegramUserId = credUser.TelegramUserId;
            // phone number does not exist
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

    public async Task<bool> AddFund(CredUser credUser, long amount)
    {
        //CredentialsDbContext credentialsDbContext = new CredentialsDbContext();
        Attach<CredUser>(credUser);
        credUser.AccountBalance += amount;
        await SaveChangesAsync();
        return true;
    }


}
