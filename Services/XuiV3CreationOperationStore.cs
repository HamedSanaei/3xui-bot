using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Reserves client identity exactly once before non-idempotent XUI provisioning.</summary>
/// <remarks>Uses one short users.db transaction and no network I/O. Reservation and POST authorization are separate durable boundaries.</remarks>
public sealed class XuiV3CreationOperationStore
{
    private readonly UserDbContextFactory _factory;
    /// <summary>Creates a store with independent persistence contexts.</summary>
    /// <param name="factory">Required users.db factory.</param>
    /// <remarks>The reservation must exist before addClient. Applied timestamps record verified results; neither method authorizes repeating an existing reservation's POST.</remarks>
    public XuiV3CreationOperationStore(UserDbContextFactory factory) => _factory = factory;

    /// <summary>Persists the first client identity or retrieves the original reservation.</summary>
    /// <param name="candidate">Required private client snapshot, stable business key, panel hash, and Telegram owner.</param>
    /// <param name="token">Cancellation before the external operation is allowed to start.</param>
    /// <returns>The detached immutable reservation and whether the unused reservation may compete for TryStartPostAsync; this flag alone never authorizes POST.</returns>
    /// <exception cref="InvalidOperationException">The key was reused for another owner, panel, inbound set, or plan parameters.</exception>
    /// <remarks>All callers must win TryStartPostAsync before POST. DefinitiveRejected is terminal: retry requires a new explicit business event/key, never automatic reuse.</remarks>
    /// <example><code>var claim = await store.ReserveAsync(candidate, token); if (claim.MayCreate &amp;&amp; await store.TryStartPostAsync(candidate.OperationKey, token)) { /* one POST */ }</code></example>
    public Task<(XuiV3CreationOperation Operation, bool MayCreate)> ReserveAsync(XuiV3CreationOperation candidate, CancellationToken token) =>
        SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.XuiV3CreationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey == candidate.OperationKey, ct);
            if (row != null)
            {
                if (row.PanelKey != candidate.PanelKey || row.TelegramUserId != candidate.TelegramUserId
                    || row.InboundIdsJson != candidate.InboundIdsJson || row.BusinessParametersJson != candidate.BusinessParametersJson)
                    throw new InvalidOperationException("Creation operation identity does not match its reservation.");
                return (row, row.Outcome == XuiV3CreationOutcome.Reserved);
            }
            candidate.InboxSequence = TelegramUpdateExecutionScope.CurrentSequence;
            db.XuiV3CreationOperations.Add(candidate);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (candidate, true);
        }, token);

    /// <summary>Records proven creation without removing the mutation reservation.</summary>
    /// <param name="key">Stable business operation key of the verified client.</param>
    /// <param name="token">Cancellation of local persistence, independent of external retries.</param>
    /// <returns>A task completing after the proof timestamp is saved.</returns>
    /// <remarks>The reservation must exist before addClient. Applied timestamps record verified results; neither method authorizes repeating an existing reservation's POST.</remarks>
    public async Task MarkAppliedAsync(string key, CancellationToken token) => await SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        return await db.XuiV3CreationOperations.Where(x => x.OperationKey == key && x.AppliedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.AppliedAtUtc, DateTime.UtcNow).SetProperty(x => x.Outcome, XuiV3CreationOutcome.Applied), ct);
    }, token);

    /// <summary>Atomically grants exactly one POST authorization for an unused reservation.</summary>
    /// <param name="key">Immutable business operation key already reserved in users.db; required.</param>
    /// <param name="token">Cancellation before crossing the external mutation boundary.</param>
    /// <returns>True only to the caller that durably changed Reserved to PostStarted; false forbids POST.</returns>
    /// <remarks>A crash after this commit is uncertain even if HTTP was never sent. No timeout or terminal state resets this grant.</remarks>
    public Task<bool> TryStartPostAsync(string key, CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        return await db.XuiV3CreationOperations.Where(x => x.OperationKey == key && x.Outcome == XuiV3CreationOutcome.Reserved)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Outcome, XuiV3CreationOutcome.PostStarted)
                .SetProperty(x => x.PostStartedAtUtc, DateTime.UtcNow), ct) == 1;
    }, token);

    /// <summary>Records uncertain transport or proven rejection without overwriting positive creation proof.</summary>
    /// <param name="key">Required immutable business operation key.</param>
    /// <param name="outcome">Only Ambiguous or DefinitiveRejected; rejection requires pre-mutation validation evidence.</param>
    /// <param name="token">Independent local persistence cancellation, not the interrupted POST token.</param>
    /// <returns>A task completing after a still-started attempt was classified; Applied is never downgraded.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The requested failure outcome is not supported.</exception>
    /// <remarks>No network I/O or retry authorization occurs here. Rejected keys remain terminal.</remarks>
    public Task<int> MarkFailureAsync(string key, XuiV3CreationOutcome outcome, CancellationToken token)
    {
        if (outcome is not (XuiV3CreationOutcome.Ambiguous or XuiV3CreationOutcome.DefinitiveRejected))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.XuiV3CreationOperations.Where(x => x.OperationKey == key && x.Outcome == XuiV3CreationOutcome.PostStarted)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Outcome, outcome), ct);
        }, token);
    }

    /// <summary>
    /// Records an operator-reviewed authoritative absence for one creation linked to an uncertain inbox row.
    /// </summary>
    /// <param name="sequence">Internal uncertain inbox sequence loaded by the authenticated review workflow.</param>
    /// <param name="operationKey">
    /// Exact durable operation key loaded from the row linked by <paramref name="sequence" />. This value must never
    /// come from Telegram command text.
    /// </param>
    /// <param name="operatorTelegramUserId">Positive global super-admin Telegram id authenticated by the receiver.</param>
    /// <param name="reviewReference">Restricted non-secret reference in <c>review-N</c> form.</param>
    /// <param name="token">Cancellation of the short users.db transaction after all panel reads have completed.</param>
    /// <returns>
    /// <c>true</c> when an exact linked <c>PostStarted</c>/<c>Ambiguous</c> operation became
    /// <c>DefinitiveRejected</c>; otherwise <c>false</c>. Applied and unrelated operations are never changed.
    /// </returns>
    /// <remarks>
    /// The caller must first prove absence through an authenticated, successful read-only panel request. This method
    /// performs no network I/O, never resets an operation to Reserved, and never grants another POST. The same local
    /// transaction also records the reviewer, UTC time, and reference on the still-uncertain inbox row.
    /// </remarks>
    /// <example><code>var changed = await store.MarkOperatorProvenAbsentAsync(sequence, persistedKey, adminId, "review-205", token);</code></example>
    internal Task<bool> MarkOperatorProvenAbsentAsync(
        long sequence,
        string operationKey,
        long operatorTelegramUserId,
        string reviewReference,
        CancellationToken token)
    {
        if (sequence <= 0 || string.IsNullOrWhiteSpace(operationKey) || operatorTelegramUserId <= 0 ||
            reviewReference == null ||
            !System.Text.RegularExpressions.Regex.IsMatch(reviewReference, @"\Areview-[0-9]{1,12}\z"))
            throw new ArgumentException("A linked operation, authenticated operator, and review-N reference are required.");

        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.XuiV3CreationOperations
                .Where(x => x.OperationKey == operationKey && x.InboxSequence == sequence &&
                    (x.Outcome == XuiV3CreationOutcome.PostStarted || x.Outcome == XuiV3CreationOutcome.Ambiguous))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Outcome, XuiV3CreationOutcome.DefinitiveRejected), ct);
            if (changed != 1)
                return false;

            var reviewedAtUtc = DateTime.UtcNow;
            var inboxChanged = await db.TelegramUpdateInbox
                .Where(x => x.Sequence == sequence && x.Status == "uncertain")
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.ReviewedByTelegramUserId, operatorTelegramUserId)
                    .SetProperty(x => x.ReviewedAtUtc, reviewedAtUtc)
                    .SetProperty(x => x.ReviewReference, reviewReference), ct);
            if (inboxChanged != 1)
                throw new InvalidOperationException("The linked uncertain inbox row was not available for review.");

            await transaction.CommitAsync(ct);
            return true;
        }, token);
    }
}
