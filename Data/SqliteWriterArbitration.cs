using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Arbitrates process-local SQLite writers before the provider enters its synchronous busy wait.</summary>
/// <remarks>
/// Each physical database has one writer. Reads remain concurrent; SQLite still enforces cross-process isolation,
/// constraints and financial transactions. Admission and active update persistence receive at most eight turns
/// before a waiting background writer. No SQL, customer payload, network request or timeout policy is changed.
/// </remarks>
internal static class SqliteWriterArbitration
{
    /// <summary>Canonical database gates; only gate metadata, never contexts or provider connections, is retained.</summary>
    private static readonly ConcurrentDictionary<string, WriterGate> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    /// <summary>Connection-local ownership permits commands inside the same immediate transaction to reenter.</summary>
    private static readonly ConditionalWeakTable<DbConnection, ConnectionOwnership> Connections = new();
    /// <summary>Weak transaction ownership allows disposal cleanup without accessing an already-disposed EF context.</summary>
    private static readonly ConditionalWeakTable<DbTransaction, ConnectionOwnership> TransactionOwners = new();
    /// <summary>Explicit foreground priority for receiver admission before a handler execution scope exists.</summary>
    private static readonly AsyncLocal<bool> Foreground = new();
    /// <summary>Shared command interceptor installed once on every users and credentials context.</summary>
    private static readonly Commands CommandInterceptor = new();
    /// <summary>Extends actual SaveChanges writes through RETURNING-reader disposal without gating empty saves.</summary>
    private static readonly Saves SaveInterceptor = new();
    /// <summary>Reserved connection-local save boundary, distinct from EF command/transaction ids and cleanup.</summary>
    private static readonly Guid SaveBoundary = Guid.NewGuid();
    /// <summary>Shared transaction interceptor installed once on every users and credentials context.</summary>
    private static readonly Transactions TransactionInterceptor = new();
    /// <summary>Shared cleanup interceptor keeps provider pool reset inside writer arbitration after writes.</summary>
    private static readonly Cleanup CleanupInterceptor = new();
    /// <summary>Process-lifetime filtered subscription observes actual transaction disposal, including rollback.</summary>
    private static readonly DisposalObserver Disposals = new();

    /// <summary>Installs writer and resource-cleanup interception without replacing existing telemetry interceptors.</summary>
    /// <param name="options">The operation-local context options builder; its existing SQLite connection is preserved.</param>
    /// <remarks>Call from OnConfiguring so legacy constructors and factory-created contexts share the same arbitration.</remarks>
    /// <example><code>SqliteWriterArbitration.Configure(optionsBuilder);</code></example>
    internal static void Configure(DbContextOptionsBuilder options)
        => options.AddInterceptors(CommandInterceptor, SaveInterceptor, TransactionInterceptor, CleanupInterceptor);

    /// <summary>Marks a short inbox persistence operation as foreground work.</summary>
    /// <returns>A scope restoring the previous priority when disposed.</returns>
    /// <remarks>Priority is captured only when requesting a writer; it never permits bypassing an active transaction.</remarks>
    /// <example><code>using var priority = SqliteWriterArbitration.Prioritize();</code></example>
    internal static PriorityScope Prioritize() => new(Foreground.Value);

    /// <summary>Restores the admission caller's asynchronous priority without allocating a disposable object.</summary>
    internal readonly struct PriorityScope : IDisposable
    {
        /// <summary>Previous asynchronous admission priority, restored without affecting another control flow.</summary>
        private readonly bool _previous;
        /// <summary>Installs foreground priority for the current asynchronous control flow.</summary>
        /// <param name="previous">The priority value to restore on exit.</param>
        internal PriorityScope(bool previous) { _previous = previous; Foreground.Value = true; }
        /// <summary>Restores the priority from before this scope.</summary>
        public void Dispose() => Foreground.Value = _previous;
    }

    /// <summary>Gets a connection's writer state, normalizing file aliases without logging their paths.</summary>
    /// <param name="connection">SQLite connection owned by EF; non-SQLite connections return no state.</param>
    /// <returns>Weakly owned connection metadata, or null for another provider.</returns>
    /// <remarks>Private in-memory connections do not share a physical writer; named shared memory uses its datasource key.</remarks>
    private static ConnectionOwnership Ownership(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return null;
        return Connections.GetValue(connection, c =>
        {
            var builder = new SqliteConnectionStringBuilder(c.ConnectionString);
            var source = builder.DataSource;
            var privateMemory = (source == ":memory:" || builder.Mode == SqliteOpenMode.Memory) && builder.Cache != SqliteCacheMode.Shared;
            var key = privateMemory ? null : builder.Mode == SqliteOpenMode.Memory || source.StartsWith("file:", StringComparison.Ordinal)
                ? source : Path.GetFullPath(source);
            return new ConnectionOwnership(key == null ? new WriterGate() : Gates.GetOrAdd(key, static _ => new WriterGate()),
                builder.DefaultTimeout);
        });
    }

    /// <summary>Looks up writer metadata during cleanup without allocating state for a read-only connection.</summary>
    /// <param name="connection">The EF provider connection being closed or disposed; null has no ownership.</param>
    /// <returns>Existing weakly owned writer metadata, or null when no writer state was created.</returns>
    /// <remarks>Cleanup must not create a physical-database gate or connection state for ordinary reads.</remarks>
    private static ConnectionOwnership ExistingOwnership(DbConnection connection)
        => connection != null && Connections.TryGetValue(connection, out var owner) ? owner : null;

    /// <summary>Distinguishes SQLite data/schema writes from reader commands without inspecting parameter values.</summary>
    /// <param name="command">Provider command whose SQL remains private and is never retained.</param>
    /// <returns>True for a writer command; false for SELECT and read-only PRAGMA commands.</returns>
    /// <remarks>EF-generated DML, raw local DML, migrations and VACUUM are covered. SaveChanges RETURNING readers retain ownership until disposal.</remarks>
    private static bool Writes(DbCommand command)
    {
        var sql = command.CommandText.AsSpan().TrimStart();
        while (sql.StartsWith("--", StringComparison.Ordinal))
        {
            var newline = sql.IndexOf('\n');
            if (newline < 0) return false;
            sql = sql[(newline + 1)..].TrimStart();
        }
        return sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DROP", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("VACUUM", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("REINDEX", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase) && sql.Contains('=');
    }

    /// <summary>Tracks nested writer boundaries on one exclusively owned provider connection.</summary>
    /// <param name="gate">Arbitration gate for this connection's physical database.</param>
    /// <param name="defaultTimeoutSeconds">Configured SQLite connection timeout in seconds; zero preserves unlimited waiting.</param>
    /// <remarks>EF contexts must not be used concurrently. Depth accounts separately for transaction, command and cleanup ownership.</remarks>
    private sealed class ConnectionOwnership(WriterGate gate, int defaultTimeoutSeconds)
    {
        /// <summary>Exact acquired boundary identities; duplicate cleanup and canceled-before-acquisition events are harmless.</summary>
        private readonly HashSet<Guid> _boundaries = new();
        /// <summary>Whether an EF save is in progress; no writer is acquired until its first actual mutation.</summary>
        private bool _saving;
        /// <summary>Whether this connection performed a write since its last completed close.</summary>
        internal bool Wrote { get; private set; }
        /// <summary>Acquires synchronous ownership for synchronous EF APIs, reentering an existing local transaction.</summary>
        /// <param name="id">Opaque command or transaction id; Guid.Empty identifies provider cleanup.</param>
        /// <param name="timeoutSeconds">Optional command timeout in seconds; null uses the connection timeout and zero is unlimited.</param>
        /// <exception cref="SqliteException">The configured writer-admission budget expires; primary code 5 retains the existing bounded retry contract.</exception>
        internal void Enter(Guid id, int? timeoutSeconds = null)
        {
            if (_boundaries.Contains(id)) return;
            if (_boundaries.Count == 0) gate.EnterAsync(IsForeground(), default, timeoutSeconds ?? defaultTimeoutSeconds).GetAwaiter().GetResult();
            _boundaries.Add(id);
            if (_saving) _boundaries.Add(SaveBoundary);
            Wrote = true;
        }
        /// <summary>Acquires ownership asynchronously before invoking synchronous Microsoft.Data.Sqlite provider work.</summary>
        /// <param name="id">Opaque command or transaction id; Guid.Empty identifies provider cleanup.</param>
        /// <param name="token">Original database-operation cancellation, including queue cancellation.</param>
        /// <param name="timeoutSeconds">Optional command timeout in seconds; null uses the connection timeout and zero is unlimited.</param>
        /// <returns>A task completing when this connection may write.</returns>
        /// <exception cref="SqliteException">The writer-admission budget expires without caller cancellation; primary code 5 remains retryable.</exception>
        /// <exception cref="OperationCanceledException">The original caller cancels before writer admission.</exception>
        internal async ValueTask EnterAsync(Guid id, CancellationToken token, int? timeoutSeconds = null)
        {
            if (_boundaries.Contains(id)) return;
            if (_boundaries.Count == 0) await gate.EnterAsync(IsForeground(), token, timeoutSeconds ?? defaultTimeoutSeconds);
            _boundaries.Add(id);
            if (_saving) _boundaries.Add(SaveBoundary);
            Wrote = true;
        }
        /// <summary>Releases one owned boundary, ignoring duplicate or failed-before-acquisition notifications.</summary>
        /// <param name="id">Exact opaque identity captured after writer acquisition.</param>
        internal void Exit(Guid id) { if (_boundaries.Remove(id) && _boundaries.Count == 0) gate.Exit(); }
        /// <summary>Marks a save lifetime without doing a redundant DetectChanges pass or taking an idle writer.</summary>
        internal void BeginSave() => _saving = true;
        /// <summary>Ends a save after provider readers and cleanup finish, preserving any enclosing transaction.</summary>
        internal void EndSave() { _saving = false; Exit(SaveBoundary); }
        /// <summary>Releases remaining ownership after the connection has actually closed or failed.</summary>
        /// <remarks>Only this connection's outstanding ownership is removed; repeated cleanup is harmless.</remarks>
        internal void Closed() { if (_boundaries.Count > 0) { _boundaries.Clear(); gate.Exit(); } Wrote = false; }
    }

    /// <summary>Captures admission or already-running update priority without depending on enabled telemetry.</summary>
    /// <returns>True for admission scopes or a durable active update; false for background maintenance.</returns>
    private static bool IsForeground() => Foreground.Value || TelegramUpdateExecutionScope.CurrentSequence.HasValue;

    /// <summary>Bounds queued arbitration metadata and gives foreground writers a starvation-free preference.</summary>
    /// <remarks>The queue holds at most 256 waiters; additional callers wait cancellably for a queue slot, never lose work.</remarks>
    private sealed class WriterGate
    {
        /// <summary>Protects only short arbitration metadata changes, never SQLite or task continuations.</summary>
        private readonly Lock _sync = new();
        /// <summary>Caps explicit priority-queue nodes; excess callers wait cancellably before allocating a waiter.</summary>
        private readonly SemaphoreSlim _slots = new(256, 256);
        /// <summary>FIFO requests for receiver admission and active durable-update persistence.</summary>
        private readonly LinkedList<Waiter> _foreground = new();
        /// <summary>FIFO requests for independent background persistence and maintenance.</summary>
        private readonly LinkedList<Waiter> _background = new();
        /// <summary>Whether a writer turn is held or has been transferred to a completing waiter.</summary>
        private bool _held;
        /// <summary>Consecutive queued foreground turns since the most recent background turn, capped for fairness.</summary>
        private int _foregroundBurst;

        /// <summary>Waits outside SQLite for one writer turn, with a nonallocating uncontended fast path.</summary>
        /// <param name="foreground">Whether this local write belongs to admission or an active update.</param>
        /// <param name="token">Cancellation before provider execution; an acquired turn must still be released.</param>
        /// <param name="timeoutSeconds">Existing command/connection busy budget in seconds; zero means unlimited, as in SQLite.</param>
        /// <returns>A task completing after exclusive writer admission.</returns>
        /// <remarks>Priority reorders only independent database boundaries, never durable lane order or financial effects.
        /// Queue-slot and writer waits share one existing timeout. Expiry is SQLite BUSY, not caller cancellation;
        /// retained or nested competing contexts cannot turn the bounded three-attempt policy into an indefinite wait.</remarks>
        /// <exception cref="SqliteException">Writer admission exceeds its configured budget; primary error code is 5.</exception>
        /// <exception cref="OperationCanceledException">The original database caller cancels while queued.</exception>
        internal async ValueTask EnterAsync(bool foreground, CancellationToken token, int timeoutSeconds)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync) { if (!_held) { _held = true; return; } }
            // Allocate a deadline only on contention; native SQLite and existing retry budgets are not increased.
            using var deadline = timeoutSeconds > 0 ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
            deadline?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var waitingToken = deadline?.Token ?? token;
            try
            {
                await _slots.WaitAsync(waitingToken);
                Waiter waiter;
                lock (_sync)
                {
                    if (waitingToken.IsCancellationRequested)
                    {
                        _slots.Release();
                        waitingToken.ThrowIfCancellationRequested();
                    }
                    if (!_held) { _held = true; _slots.Release(); return; }
                    waiter = new Waiter(this, foreground, waitingToken);
                    waiter.Node = (foreground ? _foreground : _background).AddLast(waiter);
                }
                using var registration = waitingToken.UnsafeRegister(static state => ((Waiter)state).Cancel(), waiter);
                await waiter.Completion.Task;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline?.IsCancellationRequested == true)
            {
                throw new SqliteException("SQLite writer admission timed out: database is locked.", 5);
            }
        }

        /// <summary>Transfers writer ownership to the oldest eligible waiter without running its continuation under the lock.</summary>
        /// <remarks>At most eight queued foreground turns precede a waiting background turn; canceled nodes are removed immediately.</remarks>
        internal void Exit()
        {
            Waiter next;
            lock (_sync)
            {
                var queue = _foreground.Count > 0 && (_background.Count == 0 || _foregroundBurst < 8) ? _foreground : _background;
                if (queue.Count == 0) { _held = false; _foregroundBurst = 0; return; }
                next = queue.First.Value; queue.RemoveFirst(); next.Node = null;
                _foregroundBurst = next.Foreground ? Math.Min(_foregroundBurst + 1, 8) : 0;
            }
            _slots.Release();
            next.Completion.TrySetResult();
        }

        /// <summary>Removes a canceled queued writer; a turn already transferred still executes its cancellation-aware operation.</summary>
        /// <param name="waiter">Exact queued waiter requesting cancellation.</param>
        private void Cancel(Waiter waiter)
        {
            lock (_sync)
            {
                if (waiter.Node == null) return;
                (waiter.Foreground ? _foreground : _background).Remove(waiter.Node); waiter.Node = null;
            }
            _slots.Release(); waiter.Completion.TrySetCanceled(waiter.Token);
        }

        /// <summary>Owns only one queued writer turn and its cancellation metadata.</summary>
        /// <param name="owner">The physical database gate.</param>
        /// <param name="foreground">Priority captured at queue entry.</param>
        /// <param name="token">Original caller token used for exact cancellation.</param>
        private sealed class Waiter(WriterGate owner, bool foreground, CancellationToken token)
        {
            /// <summary>Asynchronous turn handoff; continuations never execute inside the arbitration metadata lock.</summary>
            internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Priority captured before this request entered the queue.</summary>
            internal readonly bool Foreground = foreground;
            /// <summary>Original operation token, used when a queued request is removed by cancellation.</summary>
            internal readonly CancellationToken Token = token;
            /// <summary>Removable queue node; null means the turn was transferred or cancellation already removed it.</summary>
            internal LinkedListNode<Waiter> Node;
            /// <summary>Requests removal from this waiter's own gate.</summary>
            internal void Cancel() => owner.Cancel(this);
        }
    }

    /// <summary>Arbitrates nonquery writers and INSERT/UPDATE RETURNING readers without gating ordinary reads.</summary>
    private sealed class Commands : DbCommandInterceptor
    {
        /// <inheritdoc />
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { if (Writes(command)) Ownership(command.Connection)?.Enter(eventData.CommandId, command.CommandTimeout); return result; }
        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Writes(command) && Ownership(command.Connection) is { } owner) await owner.EnterAsync(eventData.CommandId, cancellationToken, command.CommandTimeout); return result; }
        /// <inheritdoc />
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); return result; }
        /// <inheritdoc />
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); return ValueTask.FromResult(result); }
        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { if (Writes(command)) Ownership(command.Connection)?.Enter(eventData.CommandId, command.CommandTimeout); return result; }
        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (Writes(command) && Ownership(command.Connection) is { } owner) await owner.EnterAsync(eventData.CommandId, cancellationToken, command.CommandTimeout); return result; }
        /// <inheritdoc />
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); return result; }
        /// <inheritdoc />
        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); }
        /// <inheritdoc />
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); return Task.CompletedTask; }
        /// <inheritdoc />
        public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); }
        /// <inheritdoc />
        public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
        { if (Writes(command)) Ownership(command.Connection)?.Exit(eventData.CommandId); return Task.CompletedTask; }
    }

    /// <summary>Tracks SaveChanges lifetimes so RETURNING statements finish before a competing writer starts.</summary>
    /// <remarks>SavingChanges itself does not acquire a writer. The first actual command extends its turn through
    /// reader disposal and save completion, avoiding both writer-on-empty saves and duplicate change detection.</remarks>
    private sealed class Saves : SaveChangesInterceptor
    {
        /// <inheritdoc />
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Ownership(eventData.Context?.Database.GetDbConnection())?.BeginSave(); return result; }
        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Ownership(eventData.Context?.Database.GetDbConnection())?.BeginSave(); return ValueTask.FromResult(result); }
        /// <inheritdoc />
        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        { Ownership(eventData.Context?.Database.GetDbConnection())?.EndSave(); return result; }
        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { Ownership(eventData.Context?.Database.GetDbConnection())?.EndSave(); return ValueTask.FromResult(result); }
        /// <inheritdoc />
        public override void SaveChangesFailed(DbContextErrorEventData eventData) => Ownership(eventData.Context?.Database.GetDbConnection())?.EndSave();
        /// <inheritdoc />
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        { SaveChangesFailed(eventData); return Task.CompletedTask; }
        /// <inheritdoc />
        public override void SaveChangesCanceled(DbContextEventData eventData) => Ownership(eventData.Context?.Database.GetDbConnection())?.EndSave();
        /// <inheritdoc />
        public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
        { SaveChangesCanceled(eventData); return Task.CompletedTask; }
    }

    /// <summary>Owns a writer from transaction begin through actual commit, rollback or transaction disposal.</summary>
    private sealed class Transactions : DbTransactionInterceptor
    {
        /// <inheritdoc />
        /// <remarks>Invokes the unchanged provider begin exactly once after arbitration and supplies that real
        /// transaction to EF. The local catch releases a failed begin even when EF omits TransactionFailed and
        /// the caller deliberately retains its open connection. Earlier interceptor-supplied results are preserved.</remarks>
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            var owner = Ownership(connection);
            if (owner == null || result.HasResult) return result;
            owner.Enter(eventData.TransactionId);
            try
            {
                var transaction = connection.BeginTransaction(eventData.IsolationLevel);
                TransactionOwners.Add(transaction, owner);
                return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
            }
            catch { owner.Exit(eventData.TransactionId); throw; }
        }
        /// <inheritdoc />
        /// <remarks>Only writer admission is asynchronously queued; SQLite's original provider begin, isolation
        /// and cancellation token remain unchanged. Failed begin ownership is released before the exception escapes.</remarks>
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var owner = Ownership(connection);
            if (owner == null || result.HasResult) return result;
            await owner.EnterAsync(eventData.TransactionId, cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken);
                TransactionOwners.Add(transaction, owner);
                return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
            }
            catch { owner.Exit(eventData.TransactionId); throw; }
        }
        /// <inheritdoc />
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
            => Ownership(eventData.Context?.Database.GetDbConnection() ?? transaction.Connection)?.Exit(eventData.TransactionId);
        /// <inheritdoc />
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { TransactionCommitted(transaction, eventData); return Task.CompletedTask; }
        /// <inheritdoc />
        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
            => Ownership(eventData.Context?.Database.GetDbConnection() ?? transaction.Connection)?.Exit(eventData.TransactionId);
        /// <inheritdoc />
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { TransactionRolledBack(transaction, eventData); return Task.CompletedTask; }
        /// <inheritdoc />
        /// <remarks>A failed begin owns no provider transaction and releases its turn immediately. Failed commit,
        /// rollback or savepoint operations keep ownership until actual rollback/disposal, preserving financial isolation.
        /// EF can omit failed-begin callbacks; connection failure/close/disposal remains the required fallback.</remarks>
        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        {
            if (transaction == null)
                Ownership(eventData.Context?.Database.GetDbConnection())?.Exit(eventData.TransactionId);
        }
        /// <inheritdoc />
        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        { TransactionFailed(transaction, eventData); return Task.CompletedTask; }
    }

    /// <summary>Serializes provider pool-reset cleanup only for connections which performed writes.</summary>
    /// <remarks>Read-only duplicate/full checks never wait for another writer at close. Failed begin and failed cleanup release connection-local ownership.</remarks>
    private sealed class Cleanup : DbConnectionInterceptor
    {
        /// <inheritdoc />
        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { if (ExistingOwnership(connection) is { Wrote: true } owner) owner.Enter(Guid.Empty); return result; }
        /// <inheritdoc />
        public override async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { if (ExistingOwnership(connection) is { Wrote: true } owner) await owner.EnterAsync(Guid.Empty, default); return result; }
        /// <inheritdoc />
        public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => ExistingOwnership(connection)?.Closed();
        /// <inheritdoc />
        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        { ExistingOwnership(connection)?.Closed(); return Task.CompletedTask; }
        /// <inheritdoc />
        public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => ExistingOwnership(connection)?.Closed();
        /// <inheritdoc />
        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
        { ExistingOwnership(connection)?.Closed(); return Task.CompletedTask; }
        /// <inheritdoc />
        public override void ConnectionDisposed(DbConnection connection, ConnectionEndEventData eventData) => ExistingOwnership(connection)?.Closed();
        /// <inheritdoc />
        public override Task ConnectionDisposedAsync(DbConnection connection, ConnectionEndEventData eventData)
        { ExistingOwnership(connection)?.Closed(); return Task.CompletedTask; }
    }

    /// <summary>Releases abandoned transaction ownership after EF actually disposes its provider transaction.</summary>
    /// <remarks>Only EF's TransactionDisposed event is enabled. EF can dispose its context before its transaction;
    /// cleanup uses weak provider-transaction ownership, never the disposed context. Commit/rollback already release
    /// their turn; disposal is idempotent and removes the transaction metadata.</remarks>
    private sealed class DisposalObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>>
    {
        /// <summary>Registers process-lifetime listener discovery; the static observer outlives every application context.</summary>
        internal DisposalObserver() => DiagnosticListener.AllListeners.Subscribe(this);
        /// <inheritdoc />
        public void OnNext(DiagnosticListener listener)
        { if (listener.Name == "Microsoft.EntityFrameworkCore") listener.Subscribe(this, name => name == RelationalEventId.TransactionDisposed.Name); }
        /// <inheritdoc />
        public void OnNext(KeyValuePair<string, object> value)
        {
            if (value.Value is TransactionEventData data
                && TransactionOwners.TryGetValue(data.Transaction, out var owner))
            {
                TransactionOwners.Remove(data.Transaction);
                owner.Exit(data.TransactionId);
            }
        }
        /// <inheritdoc />
        public void OnError(Exception error) { }
        /// <inheritdoc />
        public void OnCompleted() { }
    }
}
