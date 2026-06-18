using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

public class UserDbContext : DbContext
{
    private static string _databasePath = "./Data/users.db";

    public DbSet<User> Users { get; set; }
    public DbSet<CookieData> Cookies { get; set; }
    public DbSet<SwapinoPaymentInfo> SwapinoPaymentInfos { get; set; }
    public DbSet<HooshPayPaymentInfo> HooshPayPaymentInfos { get; set; }

    public DbSet<ZibalPaymentInfo> ZibalPaymentInfos { get; set; }

    public static string DatabasePath => _databasePath;

    public static void ConfigureDatabasePath(string databasePath)
    {
        if (!string.IsNullOrWhiteSpace(databasePath))
            _databasePath = databasePath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlite($"Data Source={_databasePath};Cache=Shared");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SwapinoPaymentInfo>(entity =>
        {
            entity.ToTable("SwapinoPaymentInfos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).IsRequired().HasMaxLength(120);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.HasIndex(x => x.ParentOrderId);
            entity.HasIndex(x => x.PaymentId);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.ChatId);
            entity.Property(x => x.BaseCurrency).HasMaxLength(32);
            entity.Property(x => x.PayCurrency).HasMaxLength(32);
            entity.Property(x => x.InvoiceId).HasMaxLength(120);
            entity.Property(x => x.PaymentId).HasMaxLength(120);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.PayAddress).HasMaxLength(256);
        });

        modelBuilder.Entity<HooshPayPaymentInfo>(entity =>
        {
            entity.ToTable("HooshPayPaymentInfos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).IsRequired().HasMaxLength(120);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.HasIndex(x => x.InvoiceUid);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.ChatId);
            entity.Property(x => x.InvoiceUid).HasMaxLength(120);
            entity.Property(x => x.FeeMode).HasMaxLength(32);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.TrackingCode).HasMaxLength(120);
        });
    }


    public async Task SaveUserStatus(User user)

    {
        var context = new UserDbContext();
        // Schema creation and updates are handled by migrations at application startup.

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Id == user.Id);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            context.Users.Add(user);
        }
        else
        {
            // User already exists, update the user's information if needed
            if (user.SelectedCountry != null) existingUser.SelectedCountry = user.SelectedCountry;
            if (user.SelectedPeriod != null) existingUser.SelectedPeriod = user.SelectedPeriod;
            if (user.Type != null) existingUser.Type = user.Type;
            if (user.LastStep != null) existingUser.LastStep = user.LastStep;
            if (user.TotoalGB != null) existingUser.TotoalGB = user.TotoalGB;
            if (user.ConfigLink != null) existingUser.ConfigLink = user.ConfigLink;
            if (user.SubLink != null) existingUser.SubLink = user.SubLink;
            if (user.Email != null) existingUser.Email = user.Email;
            if (user.Flow != null) existingUser.Flow = user.Flow;
            if (user._ConfigPrice != null) existingUser._ConfigPrice = user._ConfigPrice;
            if (user.AccountCounter > existingUser.AccountCounter) existingUser.AccountCounter = user.AccountCounter;
            if (user.PaymentMethod != existingUser.PaymentMethod) existingUser.PaymentMethod = user.PaymentMethod;
            if (user.PendingAccountCount > 0) existingUser.PendingAccountCount = user.PendingAccountCount;
            if (user.PendingUserComment != null) existingUser.PendingUserComment = user.PendingUserComment;
            if (user.LastFreeAcc > DateTime.MinValue) existingUser.LastFreeAcc = user.LastFreeAcc;
            if (user.LastFreeNationalAcc > DateTime.MinValue) existingUser.LastFreeNationalAcc = user.LastFreeNationalAcc;
            if (user.LastFreeNormalAcc > DateTime.MinValue) existingUser.LastFreeNormalAcc = user.LastFreeNormalAcc;


        }
        await context.SaveChangesAsync();

    }


    public async Task ClearUserStatus(User user)
    {
        var context = new UserDbContext();
        // context.Database.EnsureCreated(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Id == user.Id);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            context.Users.Add(user);
        }
        else
        {
            // User already exists, update the user's information if needed
            existingUser.SelectedCountry = "";
            existingUser.SelectedPeriod = "";
            existingUser.Type = "";
            existingUser.LastStep = "";
            existingUser.Flow = "";
            existingUser.TotoalGB = "";
            existingUser.ConfigLink = "";
            existingUser.Email = "";
            existingUser.Type = "";
            existingUser.SubLink = "";
            existingUser.ConfigPrice = 0;
            existingUser.PaymentMethod = "credit";
            existingUser.PendingAccountCount = 0;
            existingUser.PendingUserComment = "";

        }
        await context.SaveChangesAsync();

    }
    public async Task<bool> IsUserReadyToCreate(long teluserid)
    {

        UserDbContext context = new UserDbContext();
        // context.Database.EnsureCreated(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Id == teluserid);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            context.Users.Add(new User { Id = teluserid });
            return false;
        }

        if (existingUser.Type == "realityv6")
        {
            existingUser.TotoalGB = "500";
        }

        if (!string.IsNullOrEmpty(existingUser.LastStep) && !string.IsNullOrEmpty(existingUser.SelectedCountry) && !string.IsNullOrEmpty(existingUser.SelectedPeriod) && !string.IsNullOrEmpty(existingUser.TotoalGB) && !string.IsNullOrEmpty(existingUser.Type))
        {
            return true;
        }
        else
        {
            return false;
        }


    }


    public async Task<bool> IsUserReadyToUpdate(long teluserid)
    {

        UserDbContext context = new UserDbContext();
        // context.Database.EnsureCreated(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Id == teluserid);

        if (existingUser == null)
        {
            // User does not exist, create a new user
            context.Users.Add(new User { Id = teluserid });
            return false;
        }

        if (existingUser.Type == "realityv6")
        {
            existingUser.TotoalGB = "500";
        }

        if (!string.IsNullOrEmpty(existingUser.LastStep) && !string.IsNullOrEmpty(existingUser.SelectedPeriod) && !string.IsNullOrEmpty(existingUser.TotoalGB))
        {
            return true;
        }
        else
        {
            return false;
        }


    }

    public async Task<User> GetUserStatus(long userId)
    {


        var context = new UserDbContext();
        // context.Database.EnsureCreated(); // Create the database if it doesn't exist

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (existingUser != null)
        {
            // User does not exist, create a new user
            return existingUser;
        }
        else
        {
            var newUser = new User { Id = userId };
            await context.Users.AddAsync(newUser);

            return newUser;
        }

    }

}
