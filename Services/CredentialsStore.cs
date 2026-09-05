using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Provides operation-local access to global Telegram profiles and wallet receipts.</summary>
/// <remarks>Singleton-safe: only a factory is retained. Every returned profile is detached; changing it never saves it.</remarks>
public sealed class CredentialsStore
{
    private readonly CredentialsDbContextFactory _factory;

    /// <summary>Creates a store sharing immutable options rather than an EF change tracker.</summary>
    /// <param name="factory">Required factory for the global credentials database.</param>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public CredentialsStore(CredentialsDbContextFactory factory) => _factory = factory;

    /// <summary>Reads one global user profile without tracking.</summary>
    /// <param name="credUser">Required positive Telegram user id, independent of the active bot.</param>
    /// <returns>A detached profile, or null if absent. Wallet values are snapshots, not authorization to spend.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task<CredUser> GetUserStatusWithId(long credUser)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.TelegramUserId == credUser);
    }

    /// <summary>Returns a global wallet balance snapshot in toman.</summary>
    /// <param name="credUser">Global Telegram user id.</param>
    /// <returns>Current balance, or the legacy -1 sentinel if the user is absent.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task<long> GetAccountBalance(long credUser) => (await GetUserStatusWithId(credUser))?.AccountBalance ?? -1;

    /// <summary>Reads the immutable proof of a named financial event.</summary>
    /// <param name="operationKey">Stable non-secret business event key.</param>
    /// <param name="token">Cancellation of the local read.</param>
    /// <returns>A detached receipt or null; absence proves no mutation committed through this receipt protocol.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task<WalletOperation> GetWalletOperationAsync(string operationKey, CancellationToken token = default)
    {
        await using var db = _factory.CreateDbContext();
        return await db.WalletOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey == operationKey, token);
    }

    /// <summary>Reads the minimal global identity mapping used to route authorized broadcasts.</summary>
    /// <param name="token">Cancellation of the database projection.</param>
    /// <returns>Detached identity-only snapshots; possibly empty, and never including balances or contact details.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task<List<CredentialsAudienceSnapshot>> ReadAudienceAsync(CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Users.AsNoTracking().Select(x => new CredentialsAudienceSnapshot(x.TelegramUserId, x.ChatID, x.IsColleague)).ToListAsync(token);
    }

    /// <summary>Creates or refreshes only Telegram profile fields and returns a detached snapshot.</summary>
    /// <param name="credUser">Required Telegram profile; existing wallet, role, and block fields are preserved.</param>
    /// <returns>A detached saved profile owned by the global Telegram user.</returns>
    /// <remarks>Concurrent first contact uses a conflict-safe insert before reloading in the same short transaction.</remarks>
    /// <example><code>var profile = await store.GetUserStatus(new CredUser { TelegramUserId = sender.Id, FirstName = sender.FirstName });</code></example>
    public Task<CredUser> GetUserStatus(CredUser credUser) => SqliteOperation.RunAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(credUser);
        await using var db = _factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await InsertEmptyAsync(db, credUser.TelegramUserId, token);
        var row = await db.Users.SingleAsync(x => x.TelegramUserId == credUser.TelegramUserId, token);
        if (credUser.ChatID != 0) row.ChatID = credUser.ChatID;
        if (credUser.Username != null) row.Username = credUser.Username;
        if (credUser.FirstName != null) row.FirstName = credUser.FirstName;
        if (credUser.LastName != null) row.LastName = credUser.LastName;
        if (credUser.LanguageCode != null) row.LanguageCode = credUser.LanguageCode;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return row;
    });

    /// <summary>Persists a Telegram profile refresh without modifying financial or access-control fields.</summary>
    /// <param name="credUser">Required detached profile from the Telegram sender.</param>
    /// <returns>A task completing after the short database commit.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task SaveUserStatus(CredUser credUser) => await GetUserStatus(credUser);

    /// <summary>Creates an empty profile atomically if the Telegram user has not registered.</summary>
    /// <param name="userid">Positive global Telegram user id.</param>
    /// <returns>A task completing after persistence; no subsequent SaveChanges call is required.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task AddEmptyUser(long userid) => await GetUserStatus(new CredUser { TelegramUserId = userid });

    /// <summary>Persists a user's contact number without overwriting their profile or balance.</summary>
    /// <param name="credUserId">Global Telegram user id supplied by the authorized caller.</param>
    /// <param name="phoneNumber">Contact number; sensitive and never logged by this store.</param>
    /// <returns>A task completing after the local write commits.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task SavePhoneNumber(long credUserId, string phoneNumber)
    {
        await AddEmptyUser(credUserId);
        await SqliteOperation.RunAsync(async token =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.Users.Where(x => x.TelegramUserId == credUserId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PhoneNumber, phoneNumber), token);
        });
    }

    /// <summary>Changes a global user's colleague pricing flag.</summary>
    /// <param name="credUserId">Global Telegram user id; the caller must already have admin authority.</param>
    /// <param name="isColleague">The explicit target state, making repeated commands idempotent.</param>
    /// <returns>True if the user exists; false if absent.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public Task<bool> PromotOrDemote(long credUserId, bool isColleague) => SqliteOperation.RunAsync(async token =>
    {
        await using var db = _factory.CreateDbContext();
        return await db.Users.Where(x => x.TelegramUserId == credUserId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsColleague, isColleague), token) == 1;
    });

    /// <summary>Sets global service blocking flags in one short transaction.</summary>
    /// <param name="credUserIds">Telegram user ids; duplicates and nonpositive values are ignored.</param>
    /// <param name="isBlocked">Explicit target block state.</param>
    /// <param name="actorTelegramUserId">Authorized administrator's Telegram user id for audit.</param>
    /// <returns>Number of distinct valid users updated, including newly created empty profiles.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public Task<int> SetBlockedStatus(IEnumerable<long> credUserIds, bool isBlocked, long actorTelegramUserId) => SqliteOperation.RunAsync(async token =>
    {
        var ids = (credUserIds ?? []).Where(x => x > 0).Distinct().ToArray();
        await using var db = _factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        foreach (var id in ids)
        {
            await InsertEmptyAsync(db, id, token);
            await db.Users.Where(x => x.TelegramUserId == id).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.IsBlocked, isBlocked)
                .SetProperty(x => x.BlockedAtUtc, isBlocked ? (DateTime?)DateTime.UtcNow : null)
                .SetProperty(x => x.BlockedByTelegramUserId, isBlocked ? (long?)actorTelegramUserId : null), token);
        }
        await transaction.CommitAsync(token);
        return ids.Length;
    });

    /// <summary>Atomically applies or recovers a uniquely identified global wallet change.</summary>
    /// <param name="telegramUserId">Positive global Telegram user id; not a tenant database id.</param>
    /// <param name="amountToman">Signed nonzero toman amount greater than long.MinValue; negative debits preserve existing overdraft policy.</param>
    /// <param name="operationKey">Required non-secret business key, at most 240 characters; reuse it on every recovery.</param>
    /// <param name="cancellationToken">Cancellation of local persistence; never covers an external mutation.</param>
    /// <param name="botId">Optional explicit originating runtime bot id for webhook callers without ambient context.</param>
    /// <param name="approvalKind">Financial authority: official by default, provisional for admin exceptions, or partial for underpaid crypto.</param>
    /// <param name="approvedByTelegramUserId">Optional authorized Telegram administrator id for provisional credits.</param>
    /// <returns>The detached immutable receipt, including authoritative before/after balances, or null for an absent user.</returns>
    /// <exception cref="ArgumentException">The key or amount is invalid.</exception>
    /// <exception cref="InvalidOperationException">The key was previously used for different financial parameters.</exception>
    /// <remarks>The receipt and balance share one transaction in credentials.db. A retry after restart returns the receipt without changing the balance.
    /// The first committed approval evidence is retained when an official provider confirmation follows a provisional approval.</remarks>
    /// <example><code>var receipt = await store.MutateWalletAsync(userId, -priceToman, $"purchase:{orderId}:debit", token);</code></example>
    public Task<WalletOperation> MutateWalletAsync(long telegramUserId, long amountToman, string operationKey, CancellationToken cancellationToken = default, string botId = null,
        string approvalKind = "official", long? approvedByTelegramUserId = null)
    {
        if (telegramUserId <= 0 || amountToman is 0 or long.MinValue || string.IsNullOrWhiteSpace(operationKey) || operationKey.Length > 240)
            throw new ArgumentException("A positive Telegram user id, nonzero amount, and stable operation key are required.");
        if (approvalKind is not ("official" or "provisional" or "partial")
            || (approvalKind == "provisional" && approvedByTelegramUserId is not > 0))
            throw new ArgumentException("Wallet approval evidence must identify a supported authority and any provisional administrator.");
        return SqliteOperation.RunAsync(async token =>
        {
            await using var db = _factory.CreateDbContext();
            // Acquire the writer transaction before reading either receipt or balance; no network calls occur here.
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var existing = await db.WalletOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey == operationKey, token);
            if (existing != null)
            {
                if (existing.TelegramUserId != telegramUserId || existing.AmountToman != amountToman)
                    throw new InvalidOperationException("Wallet operation key conflicts with its committed financial parameters.");
                return existing;
            }
            var user = await db.Users.SingleOrDefaultAsync(x => x.TelegramUserId == telegramUserId, token);
            if (user == null) return null;
            var receipt = new WalletOperation
            {
                OperationKey = operationKey, TelegramUserId = telegramUserId, AmountToman = amountToman,
                BeforeBalance = user.AccountBalance, AfterBalance = checked(user.AccountBalance + amountToman),
                BotId = botId ?? BotContextAccessor.CurrentBotId, CreatedAtUtc = DateTime.UtcNow,
                InboxSequence = TelegramUpdateExecutionScope.CurrentSequence,
                ApprovalKind = approvalKind, ApprovedByTelegramUserId = approvedByTelegramUserId
            };
            user.AccountBalance = receipt.AfterBalance;
            db.WalletOperations.Add(receipt);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return receipt;
        }, cancellationToken);
    }

    /// <summary>Debits once and refreshes the caller's detached balance snapshot.</summary>
    /// <param name="credUser">Required global user profile; it is never attached to EF.</param>
    /// <param name="amount">Positive debit amount in toman; overdrafts remain supported for existing settlement flows.</param>
    /// <param name="operationKey">Stable business debit identity, reused after uncertain completion.</param>
    /// <returns>Whether the pre-debit balance was sufficient; false can still mean the legacy overdraft was applied.</returns>
    /// <remarks>Use the business operation key, not a new random value on retries. The snapshot reflects the original operation's result.</remarks>
    public async Task<bool> Pay(CredUser credUser, long amount, string operationKey)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var receipt = await MutateWalletAsync(credUser.TelegramUserId, -amount, operationKey);
        if (receipt == null) return false;
        credUser.AccountBalance = receipt.AfterBalance;
        return receipt.BeforeBalance >= amount;
    }

    /// <summary>Applies a named wallet adjustment once, including legacy negative adjustments.</summary>
    /// <param name="credUserId">Global Telegram user id.</param>
    /// <param name="amount">Signed nonzero adjustment in toman.</param>
    /// <param name="operationKey">Stable business event identity, never a token or payment secret.</param>
    /// <param name="botId">Optional explicit originating runtime bot id for payment callbacks.</param>
    /// <param name="approvalKind">Official, provisional, or partial credit authority, persisted before the balance changes.</param>
    /// <param name="approvedByTelegramUserId">Authorized administrator id for a provisional exception; null otherwise.</param>
    /// <returns>True when the user exists and the operation was committed or already committed.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    public async Task<bool> AddFund(long credUserId, long amount, string operationKey, string botId = null,
        string approvalKind = "official", long? approvedByTelegramUserId = null) =>
        await MutateWalletAsync(credUserId, amount, operationKey, botId: botId,
            approvalKind: approvalKind, approvedByTelegramUserId: approvedByTelegramUserId) != null;

    /// <summary>Inserts a zero-balance global profile without modifying an existing row.</summary>
    /// <param name="db">Exclusive operation-local context inside a short transaction.</param>
    /// <param name="id">Positive Telegram user id.</param>
    /// <param name="token">Cancellation of the local insert.</param>
    /// <returns>The number of inserted rows, zero when the user already exists.</returns>
    /// <remarks>Global profiles are detached. Each explicit write reloads its own target; wallet changes require a unique business receipt committed atomically with the balance.</remarks>
    private static Task<int> InsertEmptyAsync(CredentialsDbContext db, long id, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Users (TelegramUserId, ChatID, Username, FirstName, LastName, LanguageCode, AccountBalance, IsColleague, IsBlocked) VALUES ({id}, 0, '', '', '', '', 0, 0, 0) ON CONFLICT(TelegramUserId) DO NOTHING", token);
}

/// <summary>Minimal detached global user identity for bot-scoped audience intersection.</summary>
/// <param name="TelegramUserId">Global Telegram user id.</param>
/// <param name="ChatID">Known Telegram chat id, possibly zero if the user has not started the bot.</param>
/// <param name="IsColleague">Whether colleague audience filters include the user.</param>
public sealed record CredentialsAudienceSnapshot(long TelegramUserId, long ChatID, bool IsColleague);
