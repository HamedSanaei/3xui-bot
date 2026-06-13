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
            ApplyTelegramProfile(existingUser, credUser);
            await SaveChangesAsync();
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
            ApplyTelegramProfile(existingUser, credUser);
        }
        await SaveChangesAsync();

    }

    private static void ApplyTelegramProfile(CredUser existingUser, CredUser incomingUser)
    {
        if (existingUser == null || incomingUser == null)
            return;

        var hasProfileSnapshot =
            incomingUser.ChatID != 0 ||
            incomingUser.Username != null ||
            incomingUser.FirstName != null ||
            incomingUser.LastName != null ||
            incomingUser.LanguageCode != null;

        if (!hasProfileSnapshot)
            return;

        if (incomingUser.ChatID != 0 && incomingUser.ChatID != existingUser.ChatID)
            existingUser.ChatID = incomingUser.ChatID;

        existingUser.Username = incomingUser.Username ?? string.Empty;
        existingUser.FirstName = incomingUser.FirstName ?? string.Empty;
        existingUser.LastName = incomingUser.LastName ?? string.Empty;
        existingUser.LanguageCode = incomingUser.LanguageCode ?? string.Empty;
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

    public async Task<int> SetBlockedStatus(IEnumerable<long> credUserIds, bool isBlocked, long actorTelegramUserId)
    {
        var ids = credUserIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<long>();

        var changed = 0;
        foreach (var id in ids)
        {
            var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == id);
            if (existingUser == null)
            {
                existingUser = new CredUser
                {
                    TelegramUserId = id,
                    ChatID = id,
                    Username = "",
                    FirstName = "",
                    LastName = "",
                    LanguageCode = "",
                    AccountBalance = 0
                };
                await Users.AddAsync(existingUser);
            }

            existingUser.IsBlocked = isBlocked;
            existingUser.BlockedAtUtc = isBlocked ? DateTime.UtcNow : null;
            existingUser.BlockedByTelegramUserId = isBlocked ? actorTelegramUserId : null;
            changed++;
        }

        await SaveChangesAsync();
        return changed;
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
