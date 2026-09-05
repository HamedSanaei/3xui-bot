using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot.Types;

/// <summary>Persists bounded Telegram admission, claims, and payload-free terminal receipts in users.db.</summary>
/// <remarks>All transactions are local and short. No receiver or handler executes while a write transaction is held.</remarks>
public sealed class TelegramUpdateInboxStore
{
    private readonly UserDbContextFactory _factory;
    /// <summary>Creates an inbox sharing only immutable database options.</summary>
    /// <param name="factory">Required users.db context factory.</param>
    /// <remarks>Queries return detached metadata without loading payloads. Uncertain lane heads remain unresolved and prevent later work from overtaking them.</remarks>
    public TelegramUpdateInboxStore(UserDbContextFactory factory) => _factory = factory;

    /// <summary>Attempts durable admission without exceeding the configured unfinished-work limit.</summary>
    /// <param name="botId">Required canonical runtime bot id; never a token.</param>
    /// <param name="update">Required private Telegram update.</param>
    /// <param name="capacity">Positive maximum unfinished rows, including running and uncertain rows.</param>
    /// <param name="token">Receiver cancellation before acceptance.</param>
    /// <returns>True when committed or already accepted; false when admission must wait for capacity.</returns>
    /// <remarks>Duplicate delivery succeeds even when full. Cancellation after commit is resolved by durable deduplication.</remarks>
    /// <example><code>while (!await store.TryAcceptAsync(botId, update, capacity, token)) await Task.Delay(100, token);</code></example>
    public Task<bool> TryAcceptAsync(string botId, Update update, int capacity, CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.TelegramUpdateInbox.AnyAsync(x => x.BotId == botId && x.UpdateId == update.Id, ct)) return true;
        if (await db.TelegramUpdateInbox.CountAsync(x => x.Status != "completed", ct) >= capacity) return false;
        db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry
        {
            BotId = botId, UpdateId = update.Id, TelegramUserId = TelegramUpdateIdentity.ResolveUserId(update),
            UpdateType = update.Type.ToString(), Payload = JsonConvert.SerializeObject(update), AcceptedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }, token);

    /// <summary>Loads only the oldest unfinished row per bot/user lane, excluding blocked lanes.</summary>
    /// <param name="capacity">Maximum rows to materialize; equals the validated admission capacity.</param>
    /// <param name="token">Cancellation of the read.</param>
    /// <returns>Detached queued lane heads ordered by acceptance, possibly empty; payloads are not materialized.</returns>
    /// <remarks>Queries return detached metadata without loading payloads. Uncertain lane heads remain unresolved and prevent later work from overtaking them.</remarks>
    public async Task<List<TelegramUpdateInboxEntry>> ReadReadyAsync(int capacity, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.AsNoTracking().Where(x => x.Status == "queued"
            && !db.TelegramUpdateInbox.Any(prior => prior.BotId == x.BotId && prior.TelegramUserId == x.TelegramUserId
                && prior.Sequence < x.Sequence && prior.Status != "completed"))
            .OrderBy(x => x.Sequence).Take(capacity).Select(x => new TelegramUpdateInboxEntry
            {
                Sequence = x.Sequence, BotId = x.BotId, UpdateId = x.UpdateId, TelegramUserId = x.TelegramUserId,
                UpdateType = x.UpdateType, AcceptedAtUtc = x.AcceptedAtUtc, Status = x.Status
            }).ToListAsync(token);
    }

    /// <summary>Claims one queued lane head before any external handler effect.</summary>
    /// <param name="sequence">Internal inbox sequence selected by this scheduler.</param>
    /// <param name="token">Cancellation of the local claim.</param>
    /// <returns>A private work item, or null if another executor already claimed the row.</returns>
    /// <remarks>A process crash after the claim creates uncertain work, never automatic redelivery.</remarks>
    public Task<TelegramUpdateWorkItem> ClaimAsync(long sequence, CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "queued")
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "running").SetProperty(x => x.StartedAtUtc, DateTime.UtcNow), ct);
        if (claimed == 0) return null;
        var row = await db.TelegramUpdateInbox.AsNoTracking().SingleAsync(x => x.Sequence == sequence, ct);
        await transaction.CommitAsync(ct);
        return new TelegramUpdateWorkItem(row.Sequence, new(row.BotId, row.TelegramUserId),
            JsonConvert.DeserializeObject<Update>(row.Payload) ?? throw new InvalidOperationException("Invalid durable update payload."), row.AcceptedAtUtc);
    }, token);

    /// <summary>Finalizes a claim or quarantines it when execution cannot be proven complete.</summary>
    /// <param name="sequence">Internal claimed inbox sequence.</param>
    /// <param name="failureCode">Null for success; otherwise a coarse non-secret failure code.</param>
    /// <param name="token">Independent persistence cancellation token, normally not the cancelled handler token.</param>
    /// <returns>A task completing after the claim is terminal or uncertain.</returns>
    /// <remarks>Successful completion erases customer payload; failures retain it and block later same-key execution.</remarks>
    public async Task FinishAsync(long sequence, string failureCode, CancellationToken token)
    {
        await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return failureCode == null
                ? await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "running")
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed").SetProperty(x => x.Payload, (string)null)
                        .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), ct)
                : await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "running")
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "uncertain").SetProperty(x => x.FailureCode, failureCode), ct);
        }, token);
    }

    /// <summary>Quarantines claims left by the previous process and expires completed deduplication receipts.</summary>
    /// <param name="token">Host startup cancellation; must finish before receiver admission begins.</param>
    /// <returns>The number of interrupted claims quarantined for reconciliation or operator review.</returns>
    /// <remarks>Requires the deployment's single polling process. Queued updates and uncertain payloads are retained.</remarks>
    public Task<int> RecoverAsync(CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var count = await db.TelegramUpdateInbox.Where(x => x.Status == "running")
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "uncertain").SetProperty(x => x.FailureCode, "process_interrupted"), ct);
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await db.TelegramUpdateInbox.Where(x => x.Status == "completed" && x.CompletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }, token);

    /// <summary>Measures unfinished admission pressure without loading private updates.</summary>
    /// <param name="token">Cancellation of the count.</param>
    /// <returns>Unfinished rows, including quarantined work that continues to consume capacity.</returns>
    /// <remarks>Queries return detached metadata without loading payloads. Uncertain lane heads remain unresolved and prevent later work from overtaking them.</remarks>
    public async Task<int> CountPendingAsync(CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.CountAsync(x => x.Status != "completed", token);
    }

    /// <summary>Expires only completed receipts while leaving every unresolved lane intact.</summary>
    /// <param name="token">Cancellation of the short maintenance write.</param>
    /// <returns>The number of completed deduplication records older than seven days removed.</returns>
    /// <remarks>Payloads have already been erased at completion; this runs periodically during long uptimes.</remarks>
    public Task<int> PruneAsync(CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var cutoff = DateTime.UtcNow.AddDays(-7);
        return await db.TelegramUpdateInbox.Where(x => x.Status == "completed" && x.CompletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }, token);

    /// <summary>Detects linked XUI attempts whose outcome is still ambiguous despite a handled user-facing failure.</summary>
    /// <param name="sequence">Internal inbox sequence of the handler that just returned.</param>
    /// <param name="token">Cancellation of the metadata-only lookup.</param>
    /// <returns>True when at least one linked creation lacks proof; the scheduler must quarantine that update.</returns>
    /// <remarks>This query never replays provisioning and never loads private client payloads.</remarks>
    public async Task<bool> HasUnresolvedCreationAsync(long sequence, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.XuiV3CreationOperations.AnyAsync(x => x.InboxSequence == sequence && x.AppliedAtUtc == null, token);
    }

    /// <summary>Releases a quarantined lane only after an authorized operator has reconciled its business effects.</summary>
    /// <param name="sequence">Internal uncertain inbox sequence, never the Telegram update id.</param>
    /// <param name="operatorTelegramUserId">Positive authenticated operator Telegram id; the caller must enforce admin authority.</param>
    /// <param name="reviewReference">Required non-secret audit reference of at most 64 letters, digits, dots, underscores or hyphens.</param>
    /// <param name="token">Cancellation of the explicit review write.</param>
    /// <returns>True if the uncertain row was closed and its payload erased; false when it was already resolved or absent.</returns>
    /// <remarks>No Telegram handler is replayed. Resolve wallet receipts, tenant orders and XUI reservations before calling;
    /// retain the review evidence in the operator's audit record. This method is not exposed to customer callbacks.</remarks>
    /// <exception cref="ArgumentException">An operator id or audit reference is invalid.</exception>
    public Task<bool> ResolveReviewedAsync(long sequence, long operatorTelegramUserId, string reviewReference, CancellationToken token)
    {
        if (sequence <= 0 || operatorTelegramUserId <= 0 || string.IsNullOrWhiteSpace(reviewReference) || reviewReference.Length > 64
            || reviewReference.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("A positive identity and non-secret operator review reference are required.");
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "uncertain")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed").SetProperty(x => x.Payload, (string)null)
                    .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow)
                    .SetProperty(x => x.FailureCode, $"reviewed:{operatorTelegramUserId}:{reviewReference}"), ct) == 1;
        }, token);
    }
}
