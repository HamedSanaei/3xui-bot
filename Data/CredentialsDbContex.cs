using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

public class CredentialsDbContext : DbContext
{
    public DbSet<CredUser> Users { get; set; }

    public CredentialsDbContext(DbContextOptions<CredentialsDbContext> options)
       : base(options)
    {
    }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Replace "YourUserDbConnectionString" with your actual connection string
        // optionsBuilder.UseSqlite("Data Source=./Data/credentials.db");

    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CredUser>()
            .HasKey(c => c.TelegramUserId);

        modelBuilder.Entity<CredUser>()
            .Property(c => c.TelegramUserId)
            .ValueGeneratedNever(); // This tells EF Core not to expect a database-generated value

    }

    public async Task<CredUser> GetUserStatus(CredUser credUser)
    {
        // Create the database if it doesn't exist

        var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser.TelegramUserId);

        if (existingUser != null)
        {
            // update public infos
            //await SaveUserStatus(credUser);
            return existingUser;
        }
        else
        {
            // Add
            await Users.AddAsync(credUser);
            await SaveChangesAsync();
            return credUser;
        }
    }

    public async Task<CredUser> GetUserStatusWithId(long credUser)
    {
        var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser);
        return existingUser;
    }

    public async Task<long> GetAccountBalance(long credUser)
    {
        long balance = -1;
        var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser);
        if (existingUser != null)
        {
            balance = existingUser.AccountBalance;
        }
        return balance;
    }


    public async Task SaveUserStatus(CredUser credUser)
    {
        var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser.TelegramUserId);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            Users.Add(credUser);
        }
        else
        {
            // // User already exists, update the user's information if needed
            if (!string.IsNullOrEmpty(credUser.Username)) existingUser.Username = credUser.Username;
            if (!string.IsNullOrEmpty(credUser.LastName)) existingUser.LastName = credUser.LastName;
            // if (credUser.Email != null) existingUser.Email = credUser.Email;
            if (credUser.ChatID != existingUser.ChatID) existingUser.ChatID = credUser.ChatID;
            if (credUser.LanguageCode != existingUser.LanguageCode) existingUser.LanguageCode = credUser.LanguageCode;
            if (credUser.FirstName != existingUser.FirstName) existingUser.FirstName = credUser.FirstName;
            // if (credUser.IsColleague != existingUser.IsColleague) existingUser.IsColleague = credUser.IsColleague;
            if (credUser.TelegramUserId != existingUser.TelegramUserId) existingUser.TelegramUserId = credUser.TelegramUserId;
            // phone number does not exist
        }
        await SaveChangesAsync();

    }



    public async Task SavePhoneNumber(long credUserId, string phoneNumber)
    {

        var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUserId);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            Users.Add(new CredUser { TelegramUserId = credUserId, PhoneNumber = phoneNumber });
        }
        else
        {
            if (existingUser.PhoneNumber != phoneNumber) existingUser.PhoneNumber = phoneNumber;
        }
        await SaveChangesAsync();

    }

    public async Task<bool> PromotOrDemote(long credUserId, bool isColleague)
    {

        var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUserId);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            return false;
        }
        else
        {
            existingUser.IsColleague = isColleague;
        }
        await SaveChangesAsync();
        return true;

    }


    public async Task<bool> Pay(CredUser credUser, long amount)
    {
        Attach<CredUser>(credUser);

        if (credUser.AccountBalance < amount)
        {
            credUser.AccountBalance -= amount;
            await SaveChangesAsync();
            return false;
        }
        credUser.AccountBalance -= amount;
        await SaveChangesAsync();
        return true;
    }

    public async Task<bool> AddFund(long credUserId, long amount)
    {
        //CredentialsDbContext credentialsDbContext = new CredentialsDbContext();
        // var user = await Users.FirstOrDefaultAsync(c => c.TelegramUserId == credUser.TelegramUserId);
        // if (user == null) return false;

        // var existingUser = Users.Find(credUser.Id);
        // if (existingUser != null)
        // {
        //     credUser.AccountBalance += amount;
        // }
        // else
        // {
        //     Users.Add(credUser);
        // }
        var existingUser = await Users.FirstOrDefaultAsync(c => c.TelegramUserId == credUserId);

        if (existingUser != null)
        {
            existingUser.AccountBalance += amount;
        }
        else
        {
            return false;
        }

        await SaveChangesAsync();
        return true;
    }

    internal async Task AddEmptyUser(long userid)
    {

        var user = new CredUser { TelegramUserId = userid, ChatID = 0, Username = "", FirstName = "", LastName = "", LanguageCode = "", AccountBalance = 0 };
        await Users.AddAsync(user);
    }
}
