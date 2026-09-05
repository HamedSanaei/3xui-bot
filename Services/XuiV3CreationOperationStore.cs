using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Reserves client identity exactly once before non-idempotent XUI provisioning.</summary>
/// <remarks>Uses one short users.db transaction and no network I/O. Every existing reservation is recovery-only.</remarks>
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
    /// <returns>The detached immutable reservation and whether this invocation alone may issue the creation POST.</returns>
    /// <exception cref="InvalidOperationException">The key was reused for another owner, panel, inbound set, or plan parameters.</exception>
    /// <remarks>New callers must not POST when MayCreate is false, even if GET does not yet find the account.</remarks>
    /// <example><code>var claim = await store.ReserveAsync(candidate, token); if (!claim.MayCreate) return await RecoverByReadBackAsync(claim.Operation);</code></example>
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
                return (row, false);
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
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.AppliedAtUtc, DateTime.UtcNow), ct);
    }, token);
}
