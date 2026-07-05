using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using System.Threading;

/// <summary>
/// Stores shared Telegram user profile, colleague status, block status, and bot-wallet balance records.
/// </summary>
/// <remarks>
/// This context is currently registered as a singleton by the application host. Public asynchronous operations are
/// serialized with an internal semaphore so simultaneous updates from multiple owned, tenant, or assistant bot
/// receivers do not start overlapping EF Core operations on the same context instance. This is a temporary
/// compatibility guard until the application can move to per-operation DbContext instances.
/// </remarks>
public class CredentialsDbContext : DbContext
{
    /// <summary>
    /// Serializes EF Core operations for the singleton credentials context instance.
    /// </summary>
    /// <remarks>
    /// EF Core DbContext is not thread-safe. Multiple Telegram receivers can call this context at the same time, so
    /// every public async method must enter this gate before executing a query or save operation.
    /// </remarks>
    private readonly SemaphoreSlim _dbGate = new(1, 1);

    public DbSet<CredUser> Users { get; set; }

    public CredentialsDbContext(DbContextOptions<CredentialsDbContext> options)
       : base(options)
    {
    }

    /// <summary>
    /// Runs a credentials database operation while holding the singleton context concurrency gate.
    /// </summary>
    /// <typeparam name="T">Return type produced by the protected database operation.</typeparam>
    /// <param name="operation">
    /// Asynchronous EF Core operation to execute. The delegate must not call another public method on this same
    /// context instance because the gate is intentionally non-reentrant.
    /// </param>
    /// <returns>The value returned by <paramref name="operation"/> after the database work completes.</returns>
    /// <remarks>
    /// This helper is a targeted safety layer for the current singleton registration. It prevents EF Core's
    /// <c>A second operation was started on this context instance</c> exception without changing schema or call sites.
    /// </remarks>
    private async Task<T> RunSerializedAsync<T>(Func<Task<T>> operation)
    {
        await _dbGate.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>
    /// Runs a credentials database operation while holding the singleton context concurrency gate.
    /// </summary>
    /// <param name="operation">
    /// Asynchronous EF Core operation to execute. The delegate must save its own changes when persistence is needed.
    /// </param>
    /// <returns>A task that completes after <paramref name="operation"/> finishes and the gate is released.</returns>
    /// <remarks>
    /// Use this overload for write methods that do not return a value. It keeps the temporary singleton DbContext
    /// concurrency fix in one place.
    /// </remarks>
    private async Task RunSerializedAsync(Func<Task> operation)
    {
        await _dbGate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _dbGate.Release();
        }
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

    /// <summary>
    /// Gets an existing credentials user or creates it from the incoming Telegram profile snapshot.
    /// </summary>
    /// <param name="credUser">
    /// User profile and wallet row candidate keyed by numeric Telegram user id. The profile fields may be partial
    /// and are used only to refresh the stored Telegram display data.
    /// </param>
    /// <returns>
    /// The tracked credentials user row after creation or profile refresh. The returned entity belongs to this
    /// shared context and must not be modified concurrently outside the serialized context methods.
    /// </returns>
    public async Task<CredUser> GetUserStatus(CredUser credUser)
    {
        return await RunSerializedAsync(async () =>
        {
            var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser.TelegramUserId);

            if (existingUser != null)
            {
                ApplyTelegramProfile(existingUser, credUser);
                await SaveChangesAsync();
                return existingUser;
            }

            await Users.AddAsync(credUser);
            await SaveChangesAsync();
            return credUser;
        });
    }

    /// <summary>
    /// Loads a credentials user by numeric Telegram user id.
    /// </summary>
    /// <param name="credUser">Numeric Telegram user id stored as the primary key in <c>credentials.db</c>.</param>
    /// <returns>
    /// The tracked user row when it exists; otherwise <c>null</c>. Callers should not log or expose missing-user
    /// failures as payment success.
    /// </returns>
    public async Task<CredUser> GetUserStatusWithId(long credUser)
    {
        return await RunSerializedAsync(() => Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser));
    }

    /// <summary>
    /// Gets the bot-wallet balance for one Telegram user.
    /// </summary>
    /// <param name="credUser">Numeric Telegram user id whose wallet balance should be read.</param>
    /// <returns>
    /// The balance in Iranian toman when the user exists; otherwise <c>-1</c> to preserve legacy missing-user
    /// behavior.
    /// </returns>
    public async Task<long> GetAccountBalance(long credUser)
    {
        return await RunSerializedAsync(async () =>
        {
            long balance = -1;
            var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser);
            if (existingUser != null)
            {
                balance = existingUser.AccountBalance;
            }
            return balance;
        });
    }


    /// <summary>
    /// Inserts or refreshes a shared credentials user profile.
    /// </summary>
    /// <param name="credUser">
    /// Telegram profile snapshot keyed by numeric Telegram user id. Wallet and role fields are preserved for
    /// existing users; profile fields are refreshed from the incoming Telegram update.
    /// </param>
    /// <returns>A task that completes after the profile row is inserted or updated.</returns>
    public async Task SaveUserStatus(CredUser credUser)
    {
        await RunSerializedAsync(async () =>
        {
            var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUser.TelegramUserId);

            if (existingUser == null)
            {
                Users.Add(credUser);
            }
            else
            {
                ApplyTelegramProfile(existingUser, credUser);
            }
            await SaveChangesAsync();
        });
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



    /// <summary>
    /// Saves or updates the phone number associated with a Telegram user.
    /// </summary>
    /// <param name="credUserId">Numeric Telegram user id whose phone number is being stored.</param>
    /// <param name="phoneNumber">Phone number text received from Telegram contact sharing or an admin flow.</param>
    /// <returns>A task that completes after the phone number is persisted.</returns>
    public async Task SavePhoneNumber(long credUserId, string phoneNumber)
    {
        await RunSerializedAsync(async () =>
        {
            var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUserId);

            if (existingUser == null)
            {
                Users.Add(new CredUser { TelegramUserId = credUserId, PhoneNumber = phoneNumber });
            }
            else
            {
                if (existingUser.PhoneNumber != phoneNumber) existingUser.PhoneNumber = phoneNumber;
            }
            await SaveChangesAsync();
        });
    }

    /// <summary>
    /// Changes whether a Telegram user receives colleague pricing.
    /// </summary>
    /// <param name="credUserId">Numeric Telegram user id stored in <c>credentials.db</c>.</param>
    /// <param name="isColleague">
    /// <c>true</c> to promote the user to colleague pricing; <c>false</c> to demote the user to regular pricing.
    /// </param>
    /// <returns>
    /// <c>true</c> when an existing user row was updated; <c>false</c> when no credentials user exists.
    /// </returns>
    public async Task<bool> PromotOrDemote(long credUserId, bool isColleague)
    {
        return await RunSerializedAsync(async () =>
        {
            var existingUser = await Users.FirstOrDefaultAsync(u => u.TelegramUserId == credUserId);

            if (existingUser == null)
            {
                return false;
            }

            existingUser.IsColleague = isColleague;
            await SaveChangesAsync();
            return true;
        });
    }

    /// <summary>
    /// Sets the local service block flag for one or more Telegram users.
    /// </summary>
    /// <param name="credUserIds">
    /// Numeric Telegram user ids selected by a super-admin. Non-positive ids are ignored and duplicates are
    /// collapsed before any database work is performed.
    /// </param>
    /// <param name="isBlocked"><c>true</c> to block service access; <c>false</c> to unblock.</param>
    /// <param name="actorTelegramUserId">Numeric Telegram user id of the admin who performed the change.</param>
    /// <returns>The number of user rows that were created or updated.</returns>
    public async Task<int> SetBlockedStatus(IEnumerable<long> credUserIds, bool isBlocked, long actorTelegramUserId)
    {
        return await RunSerializedAsync(async () =>
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
        });
    }


    /// <summary>
    /// Debits the shared bot wallet for a Telegram user.
    /// </summary>
    /// <param name="credUser">
    /// Tracked or detached credentials user whose balance should be reduced. The entity is attached before the
    /// debit so legacy call sites can pass the row returned from another context method.
    /// </param>
    /// <param name="amount">Debit amount in Iranian toman. The legacy method allows negative balances.</param>
    /// <returns>
    /// <c>true</c> when the balance was sufficient before the debit; <c>false</c> when the debit was still applied
    /// but the user did not have enough balance.
    /// </returns>
    public async Task<bool> Pay(CredUser credUser, long amount)
    {
        return await RunSerializedAsync(async () =>
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
        });
    }

    /// <summary>
    /// Credits the shared bot wallet for a Telegram user.
    /// </summary>
    /// <param name="credUserId">Numeric Telegram user id whose wallet should be credited.</param>
    /// <param name="amount">Credit amount in Iranian toman. The value is added directly to the stored balance.</param>
    /// <returns><c>true</c> when the user existed and was credited; otherwise <c>false</c>.</returns>
    public async Task<bool> AddFund(long credUserId, long amount)
    {
        return await RunSerializedAsync(async () =>
        {
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
        });
    }

    /// <summary>
    /// Adds an empty credentials user row without saving changes.
    /// </summary>
    /// <param name="userid">Numeric Telegram user id for the new empty credentials row.</param>
    /// <returns>A task that completes after the row is added to the EF change tracker.</returns>
    internal async Task AddEmptyUser(long userid)
    {
        await RunSerializedAsync(async () =>
        {
            var user = new CredUser { TelegramUserId = userid, ChatID = 0, Username = "", FirstName = "", LastName = "", LanguageCode = "", AccountBalance = 0 };
            await Users.AddAsync(user);
        });
    }
}
