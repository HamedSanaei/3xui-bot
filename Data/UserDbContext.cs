using Microsoft.EntityFrameworkCore;

public class UserDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<CookieData> Cookies { get; set; }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite("Data Source=./Data/users.db");
    }


    public async Task SaveUserStatus(User user)

    {
        var context = new UserDbContext();
        context.Database.EnsureCreated(); // Create the database if it doesn't exist

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
            if (user.Email != null) existingUser.Email = user.Email;
            if (user.Flow != null) existingUser.Flow = user.Flow;

        }
        await context.SaveChangesAsync();

    }

    public async Task ClearUserStatus(User user)

    {
        var context = new UserDbContext();
        context.Database.EnsureCreated(); // Create the database if it doesn't exist

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
            existingUser.TotoalGB = "";
            existingUser.ConfigLink = "";
            existingUser.Email = "";
            existingUser.Type = "";
        }
        await context.SaveChangesAsync();

    }
    public async Task<bool> IsUserReadyToCreate(long teluserid)
    {

        UserDbContext context = new UserDbContext();
        context.Database.EnsureCreated(); // Create the database if it doesn't exist

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
        context.Database.EnsureCreated(); // Create the database if it doesn't exist

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
        context.Database.EnsureCreated(); // Create the database if it doesn't exist

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
