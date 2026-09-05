using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Reads and writes detached conversation snapshots using a fresh users.db context per operation.</summary>
/// <remarks>State belongs to BotId plus TelegramUserId; one Telegram user can hold independent state in every bot.</remarks>
public sealed class UserStateStore
{
    private readonly UserDbContextFactory _factory;
    /// <summary>Creates a singleton-safe conversation store.</summary>
    /// <param name="factory">Required users.db factory; no live tracker is retained.</param>
    /// <remarks>The active bot context scopes all state reads and writes. Returned snapshots cannot be attached to another context or reused as another bot's state.</remarks>
    public UserStateStore(UserDbContextFactory factory) => _factory = factory;

    /// <summary>Creates the store used by static legacy transport helpers after startup configures the database path.</summary>
    /// <returns>A factory-backed store using the configured users.db path and the standard five-second busy timeout.</returns>
    /// <remarks>Production dependency-injected services should use their injected store. This compatibility entry point
    /// owns no database context and must be called only after Program sets UserDbContext.DatabasePath.</remarks>
    public static UserStateStore ForConfiguredDatabase() => new(new UserDbContextFactory(
        new DbContextOptionsBuilder<UserDbContext>().UseSqlite(SqliteOperation.ConnectionString(UserDbContext.DatabasePath)).Options));

    /// <summary>Loads an owned or tenant bot's conversation as a detached legacy snapshot.</summary>
    /// <param name="userId">Telegram sender id; the owning bot comes from the active execution context.</param>
    /// <returns>A detached state or an empty User with the requested id; no implicit row is inserted.</returns>
    /// <remarks>The active bot context scopes all state reads and writes. Returned snapshots cannot be attached to another context or reused as another bot's state.</remarks>
    public async Task<User> GetUserStatus(long userId)
    {
        var botId = BotContextAccessor.CurrentBotId;
        await using var db = _factory.CreateDbContext();
        var row = await db.BotUserStates.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == botId && x.TelegramUserId == userId);
        return row?.ToUser() ?? new User { Id = userId };
    }

    /// <summary>Applies partial conversation fields for the active bot, preserving omitted null fields.</summary>
    /// <param name="user">Required detached state; Id is the Telegram sender, never a chat or database id.</param>
    /// <returns>A task completing when the bot-scoped partial update commits.</returns>
    /// <remarks>Uses BotUserState.ApplyPartial: null preserves an existing field and an empty string clears it.</remarks>
    /// <example><code>await store.SaveUserStatus(new User { Id = senderId, LastStep = "select-duration" });</code></example>
    public Task SaveUserStatus(User user) => WriteAsync(user, clear: false, replace: true);

    /// <summary>Clears transient state while preserving the bot/user's long-lived counters.</summary>
    /// <param name="user">Required snapshot identifying the Telegram sender; other fields are ignored.</param>
    /// <returns>A task completing after the single local clear commits.</returns>
    /// <remarks>Never removes wallets, payments, orders, accounts, or another bot's state.</remarks>
    public Task ClearUserStatus(User user) => WriteAsync(user, clear: true, replace: false);

    /// <summary>Atomically clears stale transient fields and installs an explicit replacement state.</summary>
    /// <param name="user">Required replacement snapshot keyed by the Telegram sender id.</param>
    /// <returns>A task completing after clear and replacement commit together.</returns>
    /// <remarks>Long-lived counters are preserved. No caller can observe an intermediate committed empty state.</remarks>
    public Task ResetUserStatus(User user) => WriteAsync(user, clear: true, replace: true);

    /// <summary>Evaluates the legacy creation prerequisites without inserting or tracking state.</summary>
    /// <param name="teluserid">Telegram sender id in the active bot.</param>
    /// <returns>True when all required legacy create fields are present; false for a missing state.</returns>
    /// <remarks>The active bot context scopes all state reads and writes. Returned snapshots cannot be attached to another context or reused as another bot's state.</remarks>
    public async Task<bool> IsUserReadyToCreate(long teluserid)
    {
        var row = await GetUserStatus(teluserid);
        return !string.IsNullOrEmpty(row.LastStep) && !string.IsNullOrEmpty(row.SelectedCountry)
            && !string.IsNullOrEmpty(row.SelectedPeriod) && !string.IsNullOrEmpty(row.Type)
            && (row.Type == "realityv6" || !string.IsNullOrEmpty(row.TotoalGB));
    }

    /// <summary>Evaluates the legacy renewal prerequisites without changing conversation state.</summary>
    /// <param name="teluserid">Telegram sender id in the active bot.</param>
    /// <returns>True when the last step, period, and traffic fields are present; false otherwise.</returns>
    /// <remarks>The active bot context scopes all state reads and writes. Returned snapshots cannot be attached to another context or reused as another bot's state.</remarks>
    public async Task<bool> IsUserReadyToUpdate(long teluserid)
    {
        var row = await GetUserStatus(teluserid);
        return !string.IsNullOrEmpty(row.LastStep) && !string.IsNullOrEmpty(row.SelectedPeriod)
            && (row.Type == "realityv6" || !string.IsNullOrEmpty(row.TotoalGB));
    }

    /// <summary>Reloads and modifies one bot/user row inside a short conflict-safe transaction.</summary>
    /// <param name="user">Required detached replacement or partial snapshot.</param>
    /// <param name="clear">Whether to clear transient fields first.</param>
    /// <param name="replace">Whether to apply supplied partial fields after clearing.</param>
    /// <returns>A task completing after the transaction commits; failures never hold a live context.</returns>
    /// <remarks>The writer transaction serializes only local SQLite work. No external call is permitted in this delegate.</remarks>
    private async Task WriteAsync(User user, bool clear, bool replace)
    {
        ArgumentNullException.ThrowIfNull(user);
        var botId = BotContextAccessor.CurrentBotId;
        await SqliteOperation.RunAsync(async token =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var row = await db.BotUserStates.SingleOrDefaultAsync(x => x.BotId == botId && x.TelegramUserId == user.Id, token);
            if (row == null)
            {
                row = BotUserState.FromUser(botId, new User { Id = user.Id });
                db.BotUserStates.Add(row);
            }
            if (clear) row.Clear();
            if (replace) row.ApplyPartial(user);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        });
    }
}
