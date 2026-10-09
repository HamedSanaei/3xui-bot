using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot.Types;
using Adminbot.Services.Telemetry;

/// <summary>Persists bounded Telegram admission, claims, and payload-free terminal receipts in users.db.</summary>
/// <remarks>All transactions are local and short. No receiver or handler executes while a write transaction is held.</remarks>
public sealed partial class TelegramUpdateInboxStore
{
    private readonly UserDbContextFactory _factory;
    /// <summary>Optional payload-free receiver timing published immediately after admission commit.</summary>
    private readonly UpdateTelemetryTracker _telemetryTracker;
    /// <summary>Post-commit readiness notification; subscribers must never throw or treat signals as durable work.</summary>
    public event Action ReadyChanged;
    /// <summary>Notifies the coordinator that persisted work or runtime availability may now permit execution.</summary>
    /// <remarks>Signals coalesce in the scheduler; startup and periodic scans cover missing process-local notifications.</remarks>
    public void NotifyReady() => ReadyChanged?.Invoke();
    /// <summary>Creates an inbox sharing only immutable database options.</summary>
    /// <param name="factory">Required users.db context factory.</param>
    /// <param name="credentials">Global wallet receipt factory; missing configuration refuses reviewed resolution.</param>
    /// <param name="telemetryTracker">Optional bounded receiver metadata tracker; never participates in durable admission or FIFO.</param>
    /// <remarks>Queries return detached metadata without loading payloads. Business recovery records never consume Telegram admission capacity.</remarks>
    public TelegramUpdateInboxStore(UserDbContextFactory factory, CredentialsDbContextFactory credentials = null,
        UpdateTelemetryTracker telemetryTracker = null)
    { _factory = factory; _credentials = credentials; _telemetryTracker = telemetryTracker; }

    /// <summary>Attempts durable admission without exceeding the configured unfinished-work limit.</summary>
    /// <param name="botId">Required canonical runtime bot id; never a token.</param>
    /// <param name="update">Required private Telegram update.</param>
    /// <param name="capacity">Positive maximum queued or currently running Telegram executions.</param>
    /// <param name="token">Receiver cancellation before acceptance.</param>
    /// <returns>True when committed or already accepted; false when admission must wait for capacity.</returns>
    /// <remarks>Duplicate delivery succeeds even when full. Read-only duplicate/full checks avoid writer admission;
    /// capacity and deduplication are rechecked inside the immediate transaction before insertion. Serialization
    /// happens before the writer transaction, and readiness/telemetry publication happens after context disposal.
    /// Cancellation after commit is resolved by durable deduplication. Telegram's native serializer retains the
    /// Bot API wire format now that v22 uses System.Text.Json attributes.</remarks>
    /// <example><code>while (!await store.TryAcceptAsync(botId, update, capacity, token)) await Task.Delay(100, token);</code></example>
    public async Task<bool> TryAcceptAsync(string botId, Update update, int capacity, CancellationToken token)
    {
        using var priority = SqliteWriterArbitration.Prioritize();
        string payload = null;
        var result = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            if (await db.TelegramUpdateInbox.AnyAsync(x => x.BotId == botId && x.UpdateId == update.Id, ct))
                return (Accepted: true, Duplicate: true, Entry: (TelegramUpdateInboxEntry)null);
            if (await db.TelegramUpdateInbox.Where(x => x.Status == "queued" || x.Status == "running")
                .Select(x => x.Sequence).Take(capacity).CountAsync(ct) >= capacity)
                return (Accepted: false, Duplicate: false, Entry: (TelegramUpdateInboxEntry)null);

            payload ??= System.Text.Json.JsonSerializer.Serialize(update, Telegram.Bot.JsonBotAPI.Options);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.TelegramUpdateInbox.AnyAsync(x => x.BotId == botId && x.UpdateId == update.Id, ct))
                return (Accepted: true, Duplicate: true, Entry: (TelegramUpdateInboxEntry)null);
            if (await db.TelegramUpdateInbox.Where(x => x.Status == "queued" || x.Status == "running")
                .Select(x => x.Sequence).Take(capacity).CountAsync(ct) >= capacity)
                return (Accepted: false, Duplicate: false, Entry: (TelegramUpdateInboxEntry)null);
            var entry = new TelegramUpdateInboxEntry
            {
                BotId = botId, UpdateId = update.Id, TelegramUserId = TelegramUpdateIdentity.ResolveUserId(update),
                UpdateType = update.Type.ToString(), Payload = payload, AcceptedAtUtc = DateTime.UtcNow
            };
            db.TelegramUpdateInbox.Add(entry);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (Accepted: true, Duplicate: false, Entry: entry);
        }, token);
        if (result.Duplicate) UpdateTelemetryTracker.Current?.CompleteDuplicate();
        if (result.Entry != null)
        {
            _telemetryTracker?.Persisted(result.Entry.Sequence, result.Entry.AcceptedAtUtc);
            NotifyReady();
        }
        return result.Accepted;
    }

    /// <summary>Loads only the oldest queued row behind any currently queued or running work for its bot/user key.</summary>
    /// <param name="capacity">Maximum rows to materialize; equals the validated admission capacity.</param>
    /// <param name="token">Cancellation of the read.</param>
    /// <returns>Detached queued lane heads ordered by acceptance, possibly empty; payloads are not materialized.</returns>
    /// <remarks>Only live Telegram execution states participate in FIFO ordering. Terminal error/review receipts and linked business recovery records never block later user input.</remarks>
    public async Task<List<TelegramUpdateInboxEntry>> ReadReadyAsync(int capacity, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.AsNoTracking().Where(x => x.Status == "queued"
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
    /// <returns>A private work item including the exact persisted UTC claim time, or null if another executor already claimed the row.</returns>
    /// <remarks>The conditional UPDATE claims only a queued lane head, even when competing callers selected
    /// the same stale ready snapshot. Payload loading precedes the short transaction; the claim timestamp is
    /// assigned after writer admission. Deserialization follows commit and context disposal. A crash after the claim
    /// retains a review receipt; business mutations keep their own durable records. Bot API options also read v19 payloads.</remarks>
    /// <example><code>var item = await store.ClaimAsync(sequence, token); // item.StartedAtUtc is the durable wait endpoint.</code></example>
    /// <exception cref="InvalidOperationException">The committed claim contains an invalid durable update payload; recovery must preserve the claimed receipt for review.</exception>
    /// <exception cref="OperationCanceledException">The local claim operation was cancelled before returning a work item.</exception>
    public async Task<TelegramUpdateWorkItem> ClaimAsync(long sequence, CancellationToken token)
    {
        using var priority = SqliteWriterArbitration.Prioritize();
        var row = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var candidate = await db.TelegramUpdateInbox.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Sequence == sequence && x.Status == "queued"
                    && !db.TelegramUpdateInbox.Any(prior => prior.BotId == x.BotId && prior.TelegramUserId == x.TelegramUserId
                        && prior.Sequence < x.Sequence && (prior.Status == "queued" || prior.Status == "running")), ct);
            if (candidate == null) return null;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var startedAtUtc = DateTime.UtcNow;
            var claimed = await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "queued"
                && !db.TelegramUpdateInbox.Any(prior => prior.BotId == x.BotId && prior.TelegramUserId == x.TelegramUserId
                    && prior.Sequence < x.Sequence && (prior.Status == "queued" || prior.Status == "running")))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "running")
                    .SetProperty(x => x.StartedAtUtc, startedAtUtc), ct);
            if (claimed == 0) return null;
            await transaction.CommitAsync(ct);
            candidate.StartedAtUtc = startedAtUtc;
            return candidate;
        }, token);
        if (row == null) return null;
        return new TelegramUpdateWorkItem(row.Sequence, new(row.BotId, row.TelegramUserId),
            System.Text.Json.JsonSerializer.Deserialize<Update>(row.Payload, Telegram.Bot.JsonBotAPI.Options)
                ?? throw new InvalidOperationException("Invalid durable update payload."), row.AcceptedAtUtc, row.StartedAtUtc.Value);
    }

    /// <summary>Finalizes a claim as a payload-free terminal receipt after the handler exits.</summary>
    /// <param name="sequence">Internal claimed inbox sequence.</param>
    /// <param name="failureCode">Null for success; otherwise a coarse non-secret failure code.</param>
    /// <param name="token">Independent persistence cancellation token, normally not the cancelled handler token.</param>
    /// <returns>True when a running claim was transitioned; false when no running claim remained and no row changed.</returns>
    /// <remarks>Every changed receipt erases the private payload. A failure requiring business reconciliation becomes
    /// <c>completed_with_review</c>; other failures become <c>completed_with_error</c>. An already-finalized row
    /// is checked read-only before the conditional update. Neither terminal state blocks later updates; only a changed
    /// receipt publishes readiness. A false result can follow live shutdown recovery and is not proof of a new final commit.</remarks>
    public async Task<bool> FinishAsync(long sequence, string failureCode, CancellationToken token)
    {
        using var priority = SqliteWriterArbitration.Prioritize();
        var changed = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            if (!await db.TelegramUpdateInbox.AnyAsync(x => x.Sequence == sequence && x.Status == "running", ct)) return 0;
            var terminalStatus = failureCode == null ? "completed"
                : failureCode is "creation_requires_review" or "process_interrupted" or "execution_cancelled" ? "completed_with_review"
                : "completed_with_error";
            return await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "running")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, terminalStatus)
                    .SetProperty(x => x.Payload, (string)null).SetProperty(x => x.FailureCode, failureCode)
                    .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), ct);
        }, token);
        if (changed > 0) NotifyReady();
        return changed > 0;
    }

    /// <summary>Converts interrupted and legacy nonterminal claims to terminal review receipts and expires old deduplication receipts.</summary>
    /// <param name="token">Existing startup or independent shutdown-recovery cancellation.</param>
    /// <param name="preRestartTelemetry">True for previous-process startup receipts; false reconciles only known live timelines during shutdown.</param>
    /// <returns>The number of interrupted or legacy uncertain receipts converted without replaying their handlers.</returns>
    /// <remarks>Requires the deployment's single polling process. Queued updates remain runnable; business operations preserve any side-effect ambiguity independently.
    /// Private payloads are erased from every converted receipt. Startup emits bounded recovered summaries with
    /// pre-restart clocks null; shutdown uses each live timeline's exact-once terminal guard, avoiding duplicate summaries.
    /// No handler is replayed and no telemetry schema is added.</remarks>
    public Task<int> RecoverAsync(CancellationToken token, bool preRestartTelemetry = true) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var interrupted = db.TelegramUpdateInbox.Where(x => x.Status == "running" || x.Status == "uncertain");
        var count = await interrupted.AnyAsync(ct)
            ? await interrupted.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed_with_review")
                .SetProperty(x => x.Payload, (string)null).SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.FailureCode, x => x.Status == "running" ? "process_interrupted" : x.FailureCode), ct)
            : 0;
        if (count > 0 && _telemetryTracker != null)
            await RecordRecoveryTelemetryAsync(db, now, preRestartTelemetry, ct);
        await PruneAsync(ct);
        return count;
    }, token);

    /// <summary>Records already-terminal interrupted receipts in bounded metadata pages without reloading payloads.</summary>
    /// <param name="db">Existing short-lived recovery context after its terminal update, not an execution context.</param>
    /// <param name="completedAtUtc">Exact UTC completion timestamp assigned by this recovery attempt.</param>
    /// <param name="preRestartTelemetry">True reconstructs previous-process summaries; false only reconciles live shutdown execution metadata.</param>
    /// <param name="token">Existing recovery cancellation; diagnostic read failures do not alter recovery results.</param>
    /// <returns>A task completing after available metadata is observed; no records are mutated.</returns>
    /// <remarks>Only rows updated by this recovery timestamp are inspected. Retention and business recovery remain unchanged.</remarks>
    private async Task RecordRecoveryTelemetryAsync(UserDbContext db, DateTime completedAtUtc, bool preRestartTelemetry, CancellationToken token)
    {
        try
        {
            long after = 0;
            while (true)
            {
                var rows = await db.TelegramUpdateInbox.AsNoTracking()
                    .Where(x => x.Sequence > after && x.Status == "completed_with_review" && x.CompletedAtUtc == completedAtUtc)
                    .OrderBy(x => x.Sequence).Take(128)
                    .Select(x => new TelegramUpdateInboxEntry { Sequence = x.Sequence, BotId = x.BotId, UpdateId = x.UpdateId,
                        UpdateType = x.UpdateType, AcceptedAtUtc = x.AcceptedAtUtc, StartedAtUtc = x.StartedAtUtc }).ToListAsync(token);
                if (rows.Count == 0) break;
                foreach (var row in rows)
                    _telemetryTracker.RecordRecovered(row.Sequence, row.BotId, row.UpdateId, row.UpdateType,
                        row.AcceptedAtUtc, row.StartedAtUtc, completedAtUtc, preRestartTelemetry);
                after = rows[^1].Sequence;
            }
        }
        catch
        {
            // Optional metadata cannot turn a successful terminal recovery into a failed retry or delay startup further.
        }
    }

    /// <summary>Finds the earlier same-lane execution contributing the largest positive overlap to the victim's wait.</summary>
    /// <param name="sequence">Internal inbox sequence of the currently starting (victim) update.</param>
    /// <param name="botId">Required canonical runtime bot id of the lane; never a token.</param>
    /// <param name="telegramUserId">Telegram actor id of the lane, or zero for the bot-scoped no-actor fallback lane.</param>
    /// <param name="acceptedAtUtc">UTC acceptance time of the victim update, which is when its wait began.</param>
    /// <param name="startedAtUtc">UTC claim time of the victim update, which is when its wait ended.</param>
    /// <param name="token">Cancellation of the metadata-only read.</param>
    /// <returns>
    /// Detached metadata for the greatest positive overlap, or <c>null</c> for an empty/reversed wait or when no
    /// meaningful same-bot/user predecessor exists. Equal overlaps choose the smaller inbox sequence.
    /// No payload is selected or materialized.
    /// </returns>
    /// <remarks>
    /// Compares <c>min(completed ?? victimStarted, victimStarted) - max(started, victimAccepted)</c> in integer ticks,
    /// discarding zero/negative intervals. Completion after the victim starts is clipped; null completion is still running.
    /// SQLite computes and orders the candidates, returning only one metadata row. EF's canonical UTC DateTime text
    /// is converted to ticks without julianday floating-point rounding of sub-millisecond ties.
    /// Diagnostics only: no scheduling, persistence or business state changes.
    /// </remarks>
    /// <example>
    /// <code>
    /// var blocker = await store.FindPreviousLaneExecutionAsync(sequence, botId, userId, acceptedAtUtc, startedAtUtc, token);
    /// </code>
    /// </example>
    /// <exception cref="OperationCanceledException">The metadata read was cancelled; the scheduler retains its existing execution_cancelled review policy.</exception>
    public async Task<TelegramLaneExecutionSummary> FindPreviousLaneExecutionAsync(
        long sequence,
        string botId,
        long telegramUserId,
        DateTime acceptedAtUtc,
        DateTime startedAtUtc,
        CancellationToken token)
    {
        if (startedAtUtc <= acceptedAtUtc) return null;
        await using var db = _factory.CreateDbContext();
        return await db.Database.SqlQuery<TelegramLaneExecutionSummary>($"""
            SELECT "Sequence", "UpdateId", "UpdateType", "StartedAtUtc", "CompletedAtUtc",
                   {startedAtUtc} AS "ObservedAtUtc", ("OverlapTicks" / 10000.0) AS "BlockingOverlapMs"
            FROM (
                SELECT "Sequence", "UpdateId", "UpdateType", "StartedAtUtc", "CompletedAtUtc",
                       MIN(COALESCE("CompletionTicks", {startedAtUtc.Ticks}), {startedAtUtc.Ticks})
                       - MAX("StartTicks", {acceptedAtUtc.Ticks}) AS "OverlapTicks"
                FROM (
                    SELECT "Sequence", "UpdateId", "UpdateType", "StartedAtUtc", "CompletedAtUtc",
                           CAST(strftime('%s', substr("StartedAtUtc", 1, 19)) AS INTEGER) * 10000000
                             + 621355968000000000
                             + CAST(substr(substr("StartedAtUtc", 21) || '0000000', 1, 7) AS INTEGER) AS "StartTicks",
                           CASE WHEN "CompletedAtUtc" IS NULL THEN NULL ELSE
                             CAST(strftime('%s', substr("CompletedAtUtc", 1, 19)) AS INTEGER) * 10000000
                             + 621355968000000000
                             + CAST(substr(substr("CompletedAtUtc", 21) || '0000000', 1, 7) AS INTEGER)
                           END AS "CompletionTicks"
                    FROM "TelegramUpdateInbox"
                    WHERE "BotId" = {botId} AND "TelegramUserId" = {telegramUserId}
                      AND "Sequence" < {sequence} AND "StartedAtUtc" IS NOT NULL
                      AND "StartedAtUtc" < {startedAtUtc}
                      AND ("CompletedAtUtc" IS NULL OR "CompletedAtUtc" > {acceptedAtUtc})
                )
            )
            WHERE "OverlapTicks" > 0
            ORDER BY "OverlapTicks" DESC, "Sequence" ASC
            LIMIT 1
            """).FirstOrDefaultAsync(token);
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
    /// <remarks>Payloads have already been erased at completion. An empty retention scan is read-only and
    /// never requests the database writer; rows becoming eligible after that scan wait for the next maintenance pass.</remarks>
    public Task<int> PruneAsync(CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var expired = db.TelegramUpdateInbox.Where(x => x.Status.StartsWith("completed") && x.CompletedAtUtc < cutoff);
        return await expired.AnyAsync(ct) ? await expired.ExecuteDeleteAsync(ct) : 0;
    }, token);

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
