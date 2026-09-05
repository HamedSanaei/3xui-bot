using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>Persists detached workflow edits without retaining an EF context across Telegram or HTTP calls.</summary>
/// <remarks>
/// Scoped to one sequential handler graph. Reads materialize snapshots and dispose their context immediately.
/// SaveAsync reloads each changed row, validates its original scalar values and applies only explicitly changed
/// properties in a short transaction. Concurrent changes fail closed rather than attaching a stale snapshot.
/// Financial correctness still belongs to wallet receipts and XUI sagas; this store never retries external work.
/// </remarks>
public sealed class UserWorkflowStore
{
    private readonly UserDbContextFactory _factory;
    private readonly IModel _model;
    private readonly List<Snapshot> _snapshots = [];

    /// <summary>Creates an execution-local detached workflow store using immutable EF model metadata.</summary>
    /// <param name="factory">Shared users.db factory; every operation owns and disposes its context.</param>
    public UserWorkflowStore(UserDbContextFactory factory)
    {
        _factory = factory;
        using var db = factory.CreateDbContext();
        _model = db.Model;
    }

    /// <summary>Materializes a local query and returns detached rows or scalar projections.</summary>
    /// <typeparam name="T">Materialized result type; an entity, list of entities, or scalar projection.</typeparam>
    /// <param name="read">Database-only query. Must fully materialize results and contain no external I/O or writes.</param>
    /// <returns>Detached result. Entity scalar originals are retained for conflict detection on an explicit SaveAsync.</returns>
    /// <remarks>Repeated reads preserve pending edits to the same row; use ReloadAsync to explicitly discard them.</remarks>
    /// <example><code>var order = await store.ReadAsync(db =&gt; db.TenantBotOrders.SingleAsync(x =&gt; x.Id == orderId, token));</code></example>
    public async Task<T> ReadAsync<T>(Func<UserDbContext, Task<T>> read)
    {
        await using var db = _factory.CreateDbContext();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var result = await read(db);
        if (result is IQueryable) throw new InvalidOperationException("Workflow reads must be materialized.");
        if (result is IList list)
            for (var i = 0; i < list.Count; i++) list[i] = Capture(list[i]);
        else result = (T)Capture(result);
        return result;
    }

    /// <summary>Registers a new detached row for the next explicit local commit.</summary>
    /// <typeparam name="T">Mapped users.db entity type.</typeparam>
    /// <param name="entity">New entity owned by this workflow; generated keys are copied back only after commit.</param>
    /// <remarks>The snapshot itself is never attached to EF. Each retry inserts a separate fresh instance.</remarks>
    public void Add<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_snapshots.Any(x => ReferenceEquals(x.Entity, entity))) return;
        _snapshots.Add(new(entity, EntityType(entity), null));
    }

    /// <summary>Reloads one detached row, explicitly discarding pending scalar changes for that row.</summary>
    /// <typeparam name="T">Mapped users.db entity type.</typeparam>
    /// <param name="entity">Persisted snapshot whose primary key identifies the required current row.</param>
    /// <param name="token">Cancellation of the local database read.</param>
    /// <returns>A task completing after the snapshot and its concurrency baseline are refreshed.</returns>
    /// <exception cref="DbUpdateConcurrencyException">The previously read row no longer exists.</exception>
    public async Task ReloadAsync<T>(T entity, CancellationToken token = default) where T : class
    {
        var type = EntityType(entity);
        await using var db = _factory.CreateDbContext();
        var current = await db.FindAsync(type.ClrType, Keys(type, entity), token)
            ?? throw new DbUpdateConcurrencyException("Workflow row was removed before reload.");
        var values = Values(type, current);
        Copy(type, values, entity);
        var existing = _snapshots.FirstOrDefault(x => ReferenceEquals(x.Entity, entity));
        if (existing == null) _snapshots.Add(new(entity, type, values));
        else existing.Original = values;
    }

    /// <summary>Executes an explicit database-only conditional write using a fresh context on each bounded retry.</summary>
    /// <typeparam name="T">Materialized write result, normally affected-row count.</typeparam>
    /// <param name="write">Local conditional write; no external I/O and no workflow snapshot attachment is allowed.</param>
    /// <param name="token">Cancellation of database work and contention delays.</param>
    /// <returns>The committed local result; reload affected snapshots before subsequent workflow edits.</returns>
    /// <example><code>var count = await store.WriteAsync(db =&gt; db.UniquePayPaymentInfos.Where(x =&gt; x.Id == id).ExecuteUpdateAsync(set =&gt; set.SetProperty(x =&gt; x.SettlementState, state), token), token);</code></example>
    public Task<T> WriteAsync<T>(Func<UserDbContext, Task<T>> write, CancellationToken token = default) =>
        SqliteOperation.RunAsync(async _ => { await using var db = _factory.CreateDbContext(); return await write(db); }, token);

    /// <summary>Commits explicit changes to detached rows after reloading and validating their database originals.</summary>
    /// <param name="token">Cancellation of the short transaction and bounded SQLite contention delays.</param>
    /// <returns>The number of inserted or changed rows. Zero means no edits were pending.</returns>
    /// <remarks>
    /// All changed rows commit atomically in users.db. No network calls occur inside the transaction. Only BUSY/LOCKED
    /// retries, at most three fresh-context attempts, are allowed. Conflict or validation failures never retry.
    /// The caller must abort or deliberately reload/re-evaluate a conflicting business workflow, not resend external I/O.
    /// </remarks>
    /// <exception cref="DbUpdateConcurrencyException">A row was removed or changed since its snapshot was read.</exception>
    /// <example><code>order.ErrorMessage = safeFailure; await store.SaveAsync(token);</code></example>
    public async Task<int> SaveAsync(CancellationToken token = default)
    {
        var pending = _snapshots.Select(x => new Pending(x, Values(x.Type, x.Entity)))
            .Where(x => x.Snapshot.Original == null || !Equal(x.Snapshot.Type, x.Snapshot.Original, x.Values)).ToArray();
        if (pending.Length == 0) return 0;
        var committed = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var targets = new List<(Pending Pending, object Target)>();
            foreach (var edit in pending)
            {
                var snapshot = edit.Snapshot;
                object target;
                if (snapshot.Original == null)
                {
                    target = Activator.CreateInstance(snapshot.Type.ClrType)!;
                    Copy(snapshot.Type, edit.Values, target);
                    db.Add(target);
                }
                else
                {
                    target = await db.FindAsync(snapshot.Type.ClrType, Keys(snapshot.Type, snapshot.Entity), ct)
                        ?? throw new DbUpdateConcurrencyException("Workflow row was removed before saving.");
                    if (!Equal(snapshot.Type, snapshot.Original, Values(snapshot.Type, target)))
                        throw new DbUpdateConcurrencyException("Workflow row changed; reload and re-evaluate before saving.");
                    // Apply only the workflow's changed scalar fields to the freshly loaded target. Never attach its snapshot.
                    foreach (var property in snapshot.Type.GetProperties().Where(x => x.PropertyInfo != null))
                        if (!Same(property, snapshot.Original[property.Name], edit.Values[property.Name]))
                            property.PropertyInfo!.SetValue(target, edit.Values[property.Name]);
                }
                targets.Add((edit, target));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return targets.Select(x => (x.Pending.Snapshot, Values: Values(x.Pending.Snapshot.Type, x.Target))).ToArray();
        }, token);
        foreach (var (snapshot, values) in committed)
        {
            Copy(snapshot.Type, values, snapshot.Entity);
            snapshot.Original = values;
        }
        return committed.Length;
    }

    /// <summary>Captures mapped entity originals and resolves repeated reads to one detached workflow identity.</summary>
    private object Capture(object entity)
    {
        if (entity == null) return null;
        var type = _model.FindEntityType(entity.GetType());
        if (type == null) return entity;
        var values = Values(type, entity);
        var existing = _snapshots.FirstOrDefault(x => x.Type == type && Keys(type, x.Entity).SequenceEqual(Keys(type, entity)));
        if (existing == null) { _snapshots.Add(new(entity, type, values)); return entity; }
        if (existing.Original != null && Equal(type, existing.Original, Values(type, existing.Entity)))
        { Copy(type, values, existing.Entity); existing.Original = values; }
        return existing.Entity;
    }

    /// <summary>Gets model metadata for a mapped workflow row.</summary>
    private IEntityType EntityType(object entity) => _model.FindEntityType(entity.GetType())
        ?? throw new ArgumentException("A mapped users.db entity is required.");

    /// <summary>Reads the ordered scalar primary-key values without retaining any EF entry.</summary>
    private static object[] Keys(IEntityType type, object entity) => type.FindPrimaryKey()!.Properties
        .Select(x => x.PropertyInfo!.GetValue(entity)).ToArray();

    /// <summary>Copies scalar baseline values using the EF model's comparison/snapshot semantics.</summary>
    private static Dictionary<string, object> Values(IEntityType type, object entity) => type.GetProperties()
        .Where(x => x.PropertyInfo != null).ToDictionary(x => x.Name, x => x.GetValueComparer().Snapshot(x.PropertyInfo!.GetValue(entity)));

    /// <summary>Copies committed scalar values to a detached workflow object.</summary>
    private static void Copy(IEntityType type, Dictionary<string, object> values, object entity)
    { foreach (var property in type.GetProperties().Where(x => x.PropertyInfo != null)) property.PropertyInfo!.SetValue(entity, property.GetValueComparer().Snapshot(values[property.Name])); }

    /// <summary>Compares all persisted scalar originals to detect a concurrent business-state change.</summary>
    private static bool Equal(IEntityType type, Dictionary<string, object> first, Dictionary<string, object> second) =>
        type.GetProperties().Where(x => x.PropertyInfo != null).All(x => Same(x, first[x.Name], second[x.Name]));

    /// <summary>Uses the model value comparer so converted scalar values retain their EF equality semantics.</summary>
    private static bool Same(IProperty property, object first, object second) => property.GetValueComparer().Equals(first, second);

    /// <summary>Owns a detached entity and copied original scalars; contains no context or EF entity entry.</summary>
    private sealed class Snapshot(object entity, IEntityType type, Dictionary<string, object> original)
    {
        public object Entity { get; } = entity;
        public IEntityType Type { get; } = type;
        public Dictionary<string, object> Original { get; set; } = original;
    }

    /// <summary>Freezes the desired values once so a local retry cannot observe a different workflow edit.</summary>
    private sealed record Pending(Snapshot Snapshot, Dictionary<string, object> Values);
}
