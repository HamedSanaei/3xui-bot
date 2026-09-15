using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot.Types;

/// <summary>Persists bounded Telegram admission, claims, and payload-free terminal receipts in users.db.</summary>
/// <remarks>All transactions are local and short. No receiver or handler executes while a write transaction is held.</remarks>
public sealed partial class TelegramUpdateInboxStore
{
    /// <summary>Deduplication window applied by the periodic scheduler maintenance call, in days.</summary>
    /// <remarks>Seven days matches the documented Telegram Bot API redelivery window and the receipt contract.</remarks>
    public const int DefaultReceiptRetentionDays = 7;

    /// <summary>Rows removed per delete statement when the caller does not supply a batch size.</summary>
    private const int MaximumPruneBatchSize = 1000;

    /// <summary>Maximum batches one prune call performs, bounding a single call to ten thousand rows.</summary>
    private const int MaximumPruneBatches = 10;

    private readonly UserDbContextFactory _factory;
    /// <summary>Post-commit readiness notification; subscribers must never throw or treat signals as durable work.</summary>
    public event Action ReadyChanged;
    /// <summary>Notifies the coordinator that persisted work or runtime availability may now permit execution.</summary>
    /// <remarks>Signals coalesce in the scheduler; startup and periodic scans cover missing process-local notifications.</remarks>
    public void NotifyReady() => ReadyChanged?.Invoke();
    /// <summary>Creates an inbox sharing only immutable database options.</summary>
    /// <param name="factory">Required users.db context factory.</param>
    /// <param name="credentials">Global wallet receipt factory; missing configuration refuses reviewed resolution.</param>
    /// <remarks>Queries return detached metadata without loading payloads. Business recovery records never consume Telegram admission capacity.</remarks>
    public TelegramUpdateInboxStore(UserDbContextFactory factory, CredentialsDbContextFactory credentials = null)
    { _factory = factory; _credentials = credentials; }

    /// <summary>Commits a deduplicated input, optionally spilling past the memory window into durable storage.</summary>
    /// <param name="botId">Required canonical runtime bot id; never a token.</param>
    /// <param name="update">Required private Telegram update.</param>
    /// <param name="capacity">Positive maximum queued or currently running Telegram executions.</param>
    /// <param name="token">Receiver cancellation before acceptance.</param>
    /// <param name="allowDurableOverflow">True for production receivers: excess work stays on disk rather than blocking polling on worker capacity.</param>
    /// <returns>True when committed or already accepted; false when admission must wait for capacity.</returns>
    /// <remarks>Duplicate delivery succeeds even when full. Strict mode counts inside the transaction; production overflow mode retains all inputs on disk.
    /// Cancellation after commit is resolved by durable deduplication.
    /// Telegram's native serializer retains the Bot API wire format now that v22 uses System.Text.Json attributes.</remarks>
    /// <example><code>while (!await store.TryAcceptAsync(botId, update, capacity, token)) await Task.Delay(100, token);</code></example>
    public Task<bool> TryAcceptAsync(string botId, Update update, int capacity, CancellationToken token, bool allowDurableOverflow = false) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.TelegramUpdateInbox.AnyAsync(x => x.BotId == botId && x.UpdateId == update.Id, ct)) return true;
        if (!allowDurableOverflow && await db.TelegramUpdateInbox.Where(x => x.Status == "queued" || x.Status == "running")
            .Select(x => x.Sequence).Take(capacity).CountAsync(ct) >= capacity) return false;
        db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry
        {
            BotId = botId, UpdateId = update.Id, TelegramUserId = TelegramUpdateIdentity.ResolveUserId(update),
            UpdateType = update.Type.ToString(), Payload = System.Text.Json.JsonSerializer.Serialize(update, Telegram.Bot.JsonBotAPI.Options), AcceptedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        NotifyReady();
        return true;
    }, token);

    /// <summary>Loads only the oldest queued row behind any currently queued or running work for its bot/user key.</summary>
    /// <param name="capacity">Maximum rows to materialize; equals the validated admission capacity.</param>
    /// <param name="token">Cancellation of the read.</param>
    /// <param name="excludedBots">Optional internal bot ids already at their active execution limit.</param>
    /// <param name="availableBots">Optional enabled internal bot ids; filtering precedes the bounded query window.</param>
    /// <returns>Detached queued lane heads ordered by acceptance, possibly empty; payloads are not materialized.</returns>
    /// <remarks>Only live Telegram execution states participate in FIFO ordering. Terminal error/review receipts and linked business recovery records never block later user input.</remarks>
    public async Task<List<TelegramUpdateInboxEntry>> ReadReadyAsync(int capacity, CancellationToken token, string[] excludedBots = null, string[] availableBots = null)
    {
        await using var db = _factory.CreateDbContext();
        excludedBots ??= Array.Empty<string>();
        var filterAvailable = availableBots != null;
        availableBots ??= Array.Empty<string>();
        return await db.TelegramUpdateInbox.AsNoTracking().Where(x => x.Status == "queued" && !excludedBots.Contains(x.BotId)
            && (!filterAvailable || availableBots.Contains(x.BotId))
            && !db.TelegramUpdateInbox.Any(prior => prior.BotId == x.BotId && prior.TelegramUserId == x.TelegramUserId
                && prior.Sequence < x.Sequence && (prior.Status == "queued" || prior.Status == "running")))
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
    /// <remarks>A process crash after the claim creates a terminal review receipt at recovery; unsafe business mutations are protected by their own durable operation records.
    /// Bot API serialization options read both newly accepted updates and existing v19 snake-case payloads.</remarks>
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
            System.Text.Json.JsonSerializer.Deserialize<Update>(row.Payload, Telegram.Bot.JsonBotAPI.Options) ?? throw new InvalidOperationException("Invalid durable update payload."), row.AcceptedAtUtc);
    }, token);

    /// <summary>Finalizes a claim as a payload-free terminal receipt after the handler exits.</summary>
    /// <param name="sequence">Internal claimed inbox sequence.</param>
    /// <param name="failureCode">Null for success; otherwise a coarse non-secret failure code.</param>
    /// <param name="token">Independent persistence cancellation token, normally not the cancelled handler token.</param>
    /// <returns>A task completing after the claim becomes terminal for Telegram scheduling.</returns>
    /// <remarks>Every outcome erases the private payload. A failure requiring business reconciliation becomes <c>completed_with_review</c>; other failures become <c>completed_with_error</c>. Neither state blocks later updates.</remarks>
    public async Task FinishAsync(long sequence, string failureCode, CancellationToken token)
    {
        await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var terminalStatus = failureCode == null ? "completed"
                : failureCode is "creation_requires_review" or "process_interrupted" or "execution_cancelled" ? "completed_with_review"
                : "completed_with_error";
            return await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "running")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, terminalStatus)
                    .SetProperty(x => x.Payload, (string)null).SetProperty(x => x.FailureCode, failureCode)
                    .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), ct);
        }, token);
        NotifyReady();
    }

    /// <summary>Converts interrupted and legacy nonterminal claims to terminal review receipts and expires old deduplication receipts.</summary>
    /// <param name="token">Host startup cancellation; must finish before receiver admission begins.</param>
    /// <returns>The number of interrupted or legacy uncertain receipts converted without replaying their handlers.</returns>
    /// <remarks>Requires the deployment's single polling process. Queued updates remain runnable; business operations preserve any side-effect ambiguity independently. Private payloads are erased from every converted receipt.</remarks>
    public Task<int> RecoverAsync(CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var count = await db.TelegramUpdateInbox.Where(x => x.Status == "running" || x.Status == "uncertain")
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed_with_review")
                .SetProperty(x => x.Payload, (string)null).SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.FailureCode, x => x.Status == "running" ? "process_interrupted" : x.FailureCode), ct);
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await db.TelegramUpdateInbox.Where(x => x.Status.StartsWith("completed") && x.CompletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }, token);

    /// <summary>
    /// Finds the previous execution in the same bot/user lane whose execution interval overlapped one update's
    /// accepted-to-started wait.
    /// </summary>
    /// <param name="sequence">Internal inbox sequence of the currently starting (victim) update.</param>
    /// <param name="botId">Required canonical runtime bot id of the lane; never a token.</param>
    /// <param name="telegramUserId">Telegram actor id of the lane, or zero for the bot-scoped no-actor fallback lane.</param>
    /// <param name="acceptedAtUtc">UTC acceptance time of the victim update, which is when its wait began.</param>
    /// <param name="startedAtUtc">UTC claim time of the victim update, which is when its wait ended.</param>
    /// <param name="token">Cancellation of the metadata-only read.</param>
    /// <returns>
    /// The earliest earlier execution on the same lane whose run overlapped the victim's wait — the root of a slow-lane
    /// chain — or <c>null</c> when no such execution exists, for example when the victim waited on admission pressure
    /// rather than on a lane predecessor. The returned object is detached and never carries a payload.
    /// </returns>
    /// <remarks>
    /// This is diagnostics only and never changes scheduling. The overlap test is
    /// <c>started &lt; victimStarted &amp;&amp; (completed == null || completed &gt; victimAccepted)</c>, which is true
    /// for a predecessor that was still running when the victim arrived and for one that finished during the wait.
    ///
    /// Earliest rather than newest is deliberate. Strict FIFO makes every later update in a lane cascade behind one
    /// slow handler, so the newest predecessor of victim C is simply victim B, which would make each cascade member look
    /// like a separate incident and defeat warning deduplication. Every overlapping predecessor contributed to the wait,
    /// so the earliest of them is the single root blocker the operator needs to see.
    /// </remarks>
    /// <example>
    /// <code>
    /// var blocker = await store.FindPreviousLaneExecutionAsync(sequence, botId, userId, acceptedAtUtc, startedAtUtc, token);
    /// </code>
    /// </example>
    public async Task<TelegramLaneExecutionSummary> FindPreviousLaneExecutionAsync(
        long sequence,
        string botId,
        long telegramUserId,
        DateTime acceptedAtUtc,
        DateTime startedAtUtc,
        CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.AsNoTracking()
            .Where(x => x.BotId == botId && x.TelegramUserId == telegramUserId && x.Sequence < sequence
                && x.StartedAtUtc != null && x.StartedAtUtc < startedAtUtc
                && (x.CompletedAtUtc == null || x.CompletedAtUtc > acceptedAtUtc))
            .OrderBy(x => x.Sequence)
            .Select(x => new TelegramLaneExecutionSummary
            {
                Sequence = x.Sequence,
                UpdateId = x.UpdateId,
                UpdateType = x.UpdateType,
                StartedAtUtc = x.StartedAtUtc.Value,
                CompletedAtUtc = x.CompletedAtUtc
            })
            .FirstOrDefaultAsync(token);
    }

    /// <summary>Measures unfinished admission pressure without loading private updates.</summary>
    /// <param name="token">Cancellation of the count.</param>
    /// <returns>The number of queued and currently running Telegram executions.</returns>
    /// <remarks>Terminal failure/review receipts and business recovery backlogs are deliberately excluded.</remarks>
    public async Task<int> CountPendingAsync(CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.CountAsync(x => x.Status == "queued" || x.Status == "running", token);
    }

    /// <summary>Expires all terminal Telegram receipts after the seven-day deduplication window.</summary>
    /// <param name="token">Cancellation of the short maintenance write.</param>
    /// <returns>The number of completed deduplication records older than seven days removed.</returns>
    /// <remarks>
    /// Payloads have already been erased at completion; this overload is the periodic maintenance call the Telegram
    /// scheduler makes during long uptimes and delegates to the configurable retention overload with the documented
    /// Bot API redelivery window.
    /// </remarks>
    /// <example><code>var removed = await store.PruneAsync(token);</code></example>
    public Task<int> PruneAsync(CancellationToken token) => PruneAsync(DefaultReceiptRetentionDays, DateTime.UtcNow, MaximumPruneBatchSize, token);

    /// <summary>Expires terminal Telegram receipts after a caller-supplied deduplication window.</summary>
    /// <param name="retentionDays">
    /// Positive number of days a completed receipt is kept for Telegram redelivery deduplication. The cleanup service
    /// supplies the configured inbox window; the scheduled maintenance call supplies the seven-day default.
    /// </param>
    /// <param name="nowUtc">UTC reference instant the window is measured back from; a non-UTC value is converted.</param>
    /// <param name="token">Cancellation of the short maintenance write.</param>
    /// <returns>The number of completed deduplication records older than the window that were removed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="retentionDays" /> is not positive.</exception>
    /// <remarks>Used by tests and by the cleanup runner; deletion stays bounded to one batch per call.</remarks>
    public Task<int> PruneAsync(int retentionDays, DateTime nowUtc, CancellationToken token) =>
        PruneAsync(retentionDays, nowUtc, MaximumPruneBatchSize, token);

    /// <summary>Expires terminal Telegram receipts after a caller-supplied window using a bounded batch size.</summary>
    /// <param name="retentionDays">
    /// Positive number of days a completed receipt is kept for Telegram redelivery deduplication.
    /// </param>
    /// <param name="nowUtc">UTC reference instant the window is measured back from; a non-UTC value is converted.</param>
    /// <param name="batchSize">
    /// Positive maximum number of rows removed per statement. Small batches keep the write lock short so a large
    /// backlog cannot stall a Telegram handler waiting to deliver a user-visible answer.
    /// </param>
    /// <param name="token">Cancellation of the short maintenance write.</param>
    /// <returns>The number of completed deduplication records older than the window that were removed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a day or batch value is not positive.</exception>
    /// <remarks>
    /// Only receipts whose status starts with <c>completed</c> are candidates, which excludes queued work, a running
    /// handler, and an uncertain row awaiting operator review. Payloads were already erased when the receipt became
    /// terminal, so deletion removes metadata only. Deletion repeats in batches until a batch is not full, giving one
    /// call the same total effect as one unbounded statement while holding the lock for only a fraction of the time.
    /// </remarks>
    /// <example>
    /// <code>
    /// var removed = await store.PruneAsync(options.InboxRetentionDays, DateTime.UtcNow, options.BatchSize, token);
    /// </code>
    /// </example>
    public Task<int> PruneAsync(int retentionDays, DateTime nowUtc, int batchSize, CancellationToken token)
    {
        if (retentionDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "A positive retention window in days is required.");
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "A positive batch size is required.");

        return SqliteOperation.RunAsync(async ct =>
        {
            var now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
            var cutoff = now.AddDays(-retentionDays);
            var removed = 0;
            for (var batch = 0; batch < MaximumPruneBatches; batch++)
            {
                await using var db = _factory.CreateDbContext();
                var candidates = db.TelegramUpdateInbox
                    .Where(x => x.Status.StartsWith("completed") && x.CompletedAtUtc < cutoff)
                    .OrderBy(x => x.Sequence)
                    .Select(x => x.Sequence)
                    .Take(batchSize);
                var deleted = await db.TelegramUpdateInbox
                    .Where(x => candidates.Contains(x.Sequence))
                    .ExecuteDeleteAsync(ct);
                removed += deleted;
                if (deleted < batchSize)
                    break;
            }

            return removed;
        }, token);
    }

    /// <summary>Detects linked XUI attempts whose outcome is still ambiguous despite a handled user-facing failure.</summary>
    /// <param name="sequence">Internal inbox sequence of the handler that just returned.</param>
    /// <param name="token">Cancellation of the metadata-only lookup.</param>
    /// <returns>True only when a linked creation is PostStarted, Ambiguous or unknown; Applied, DefinitiveRejected and unused Reserved need no review classification.</returns>
    /// <remarks>This query never replays provisioning and never loads private client payloads.</remarks>
    public async Task<bool> HasUnresolvedCreationAsync(long sequence, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.XuiV3CreationOperations.AnyAsync(x => x.InboxSequence == sequence && (x.Outcome != XuiV3CreationOutcome.Reserved && x.Outcome != XuiV3CreationOutcome.Applied && x.Outcome != XuiV3CreationOutcome.DefinitiveRejected), token);
    }

    /// <summary>Records explicit operator resolution of a terminal review receipt after its business effects are reconciled.</summary>
    /// <param name="sequence">Internal uncertain inbox sequence, never the Telegram update id.</param>
    /// <param name="operatorTelegramUserId">Positive authenticated operator Telegram id; the caller must enforce admin authority.</param>
    /// <param name="reviewReference">Required numeric ticket reference in review-N format with one to twelve digits; no free text.</param>
    /// <param name="token">Cancellation of the explicit review write.</param>
    /// <returns>True if the review receipt was marked resolved; false when it was already resolved or absent.</returns>
    /// <remarks>No Telegram handler is replayed. Resolve wallet receipts, tenant orders and XUI reservations before calling;
    /// retain the review evidence in the operator's audit record. This method is not exposed to customer callbacks.</remarks>
    /// <exception cref="ArgumentException">An operator id or audit reference is invalid.</exception>
    public Task<bool> ResolveReviewedAsync(long sequence, long operatorTelegramUserId, string reviewReference, CancellationToken token)
    {
        if (sequence <= 0 || operatorTelegramUserId <= 0 || reviewReference == null
            || !System.Text.RegularExpressions.Regex.IsMatch(reviewReference, @"\Areview-[0-9]{1,12}\z"))
            throw new ArgumentException("A positive identity and review-N numeric ticket are required.");
        return ResolveGuardedAsync(sequence, operatorTelegramUserId, reviewReference, token);
    }
}
