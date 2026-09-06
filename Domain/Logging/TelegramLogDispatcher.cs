using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Adminbot.Domain.Logging
{
    /// <summary>Identifies the Telegram representation required by one queued log item.</summary>
    /// <remarks>
    /// The enum order doubles as outbox priority ordering: <see cref="Payment"/> outranks <see cref="Html"/>.
    /// <see cref="Plain"/> is memory-only and never durable.
    /// </remarks>
    internal enum TelegramLogDeliveryKind
    {
        /// <summary>Plain-text operational log, bounded memory-only channel, never persisted.</summary>
        Plain,
        /// <summary>HTML audit log, persisted in the outbox.</summary>
        Html,
        /// <summary>Payment HTML log with a coalesced database backup side effect, persisted in the outbox.</summary>
        Payment
    }

    /// <summary>
    /// Delivery snapshot captured synchronously in the producer context. Contains identifiers only — never a
    /// Telegram client object — so the same record survives restarts unchanged.
    /// </summary>
    /// <param name="Kind">Delivery representation and outbox priority.</param>
    /// <param name="Message">Bounded message body; HTML kinds must already be HTML-encoded.</param>
    /// <param name="BotId">Internal bot registry id resolved to a live client at delivery time.</param>
    /// <param name="LoggerChannelId">Telegram chat id receiving this log.</param>
    /// <param name="BackupChannelId">Telegram chat id receiving backup documents for Payment items.</param>
    internal sealed record TelegramLogItem(
        TelegramLogDeliveryKind Kind,
        string Message,
        string BotId,
        string LoggerChannelId,
        string BackupChannelId);

    /// <summary>
    /// Narrow send abstraction over Telegram used by the outbox dispatcher so delivery can be driven by a fake
    /// transport in the reliability harness without changing production behavior.
    /// </summary>
    internal interface ITelegramLogSender
    {
        /// <summary>Sends one text message with the given parse mode (null = plain text).</summary>
        /// <param name="channelId">Target Telegram chat id.</param>
        /// <param name="message">Bounded message body.</param>
        /// <param name="parseMode">Telegram parse mode; null keeps the text literal.</param>
        /// <param name="cancellationToken">Cancels the HTTP request.</param>
        Task SendTextMessageAsync(string channelId, string message, ParseMode? parseMode, CancellationToken cancellationToken);

        /// <summary>Sends one document (database backup) to the backup channel.</summary>
        /// <param name="channelId">Target Telegram chat id.</param>
        /// <param name="fileName">Document file name shown in Telegram.</param>
        /// <param name="content">Readable stream consumed by the client; must be readable for the call duration.</param>
        /// <param name="cancellationToken">Cancels the HTTP request.</param>
        Task SendDocumentAsync(string channelId, string fileName, Stream content, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Production <see cref="ITelegramLogSender"/> that forwards to a live <see cref="ITelegramBotClient"/>.
    /// The client is resolved by <see cref="BotClientProvider"/> at delivery time; this object is created per send
    /// and is never persisted.
    /// </summary>
    internal sealed class TelegramBotLogSender : ITelegramLogSender
    {
        private readonly ITelegramBotClient _client;

        /// <summary>Wraps a resolved Telegram client.</summary>
        /// <param name="client">Live client for the bot that owns the delivery; must not be null.</param>
        public TelegramBotLogSender(ITelegramBotClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <inheritdoc/>
        public Task SendTextMessageAsync(string channelId, string message, ParseMode? parseMode, CancellationToken cancellationToken)
            => _client.SendTextMessageAsync(channelId, message, parseMode: parseMode, cancellationToken: cancellationToken);

        /// <inheritdoc/>
        public Task SendDocumentAsync(string channelId, string fileName, Stream content, CancellationToken cancellationToken)
            => _client.SendDocumentAsync(channelId, InputFile.FromStream(content, fileName), cancellationToken: cancellationToken);
    }

    /// <summary>Configuration for the outbox dispatcher; production values default from <see cref="TelegramRateLimitPolicy"/>.</summary>
    /// <remarks>
    /// The override knobs (<see cref="UtcNow"/>, <see cref="ScanInterval"/>, <see cref="MinimumSendInterval"/>,
    /// <see cref="WakeSignalEnabled"/>, <see cref="ResetSendingOnStartup"/>) exist only for the executable
    /// reliability harness (fake clock, fast scans, crash simulation). Production constructs the options with
    /// <see cref="CreateDefault"/> so every timing value still comes from the central policy.
    /// </remarks>
    internal sealed record TelegramLogDispatcherOptions
    {
        /// <summary>Absolute path of the outbox SQLite database.</summary>
        public string OutboxDatabasePath { get; init; }

        /// <summary>Absolute path of users.db used as a backup source; empty disables that backup.</summary>
        public string UsersDatabasePath { get; init; }

        /// <summary>Absolute path of credentials.db used as a backup source; empty disables that backup.</summary>
        public string CredentialsDatabasePath { get; init; }

        /// <summary>UTC clock source; overridable so the harness can advance time without sleeping.</summary>
        public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

        /// <summary>Idle scan interval; covers lost wake-up signals and lease expiry checks.</summary>
        public TimeSpan ScanInterval { get; init; } = TelegramRateLimitPolicy.PeriodicScanInterval;

        /// <summary>Sending lease duration granted by each atomic claim.</summary>
        public TimeSpan LeaseDuration { get; init; } = TelegramRateLimitPolicy.LeaseDuration;

        /// <summary>Minimum spacing between Telegram sends performed by this dispatcher.</summary>
        public TimeSpan MinimumSendInterval { get; init; } = TelegramRateLimitPolicy.MinimumSendInterval;

        /// <summary>Attempt count after which permanent Telegram errors dead-letter.</summary>
        public int PermanentFailureMaxAttempts { get; init; } = TelegramRateLimitPolicy.PermanentFailureMaxAttempts;

        /// <summary>When false the wake signal is never released; used by the lost-wake-up harness scenario.</summary>
        public bool WakeSignalEnabled { get; init; } = true;

        /// <summary>
        /// Quiet window that must pass without a new backup request before one backup run starts. A payment burst
        /// therefore collapses into a single run instead of one run per payment.
        /// </summary>
        public TimeSpan BackupDebounce { get; init; } = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// Hard cap on how long a backup may wait for the burst to settle. Under continuous payment traffic the
        /// debounce would otherwise be restarted forever. This caps the wait before the next snapshot attempt;
        /// network duration is not part of this cap.
        /// </summary>
        public TimeSpan BackupMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Recovery scan interval for durable backup intent when no wake signal arrives; must be positive.</summary>
        public TimeSpan BackupRecoveryInterval { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>Sanitized local warning sink; never route this back through the Telegram logger.</summary>
        internal Action<string> BackupWarning { get; init; } = Console.WriteLine;

        /// <summary>Async wake wait seam for deterministic recovery tests; production uses the bounded semaphore timeout.</summary>
        internal Func<SemaphoreSlim, TimeSpan, CancellationToken, Task<bool>> BackupWaitAsync { get; init; }
            = (signal, timeout, token) => signal.WaitAsync(timeout, token);

        /// <summary>Authoritative global backup sender, normally the default owned bot; blank uses durable intent.</summary>
        public string BackupBotId { get; init; }
        /// <summary>Nonblank global channel, resolved ahead of the default owned channel; blank uses durable intent.</summary>
        public string BackupChannelId { get; init; }

        /// <summary>Selects the first nonblank destination in precedence order without changing its contents.</summary>
        /// <param name="configured">Optional global Telegram channel or validated internal bot id; never a bot token.</param>
        /// <param name="fallback">Optional default-owned or persisted destination of the same identifier type.</param>
        /// <returns>The configured identifier, otherwise the fallback, or an empty string when neither exists.</returns>
        /// <remarks>Program resolves global channel then default-owned channel; the worker applies persisted fallback.
        /// Bot ids passed here must already be resolved by the runtime registry; this helper does not validate bots.</remarks>
        /// <example><code>var channel = SelectDestination(globalChannel, defaultBot.BackupChannel);</code></example>
        internal static string SelectDestination(string configured, string fallback) =>
            !string.IsNullOrWhiteSpace(configured) ? configured : !string.IsNullOrWhiteSpace(fallback) ? fallback : string.Empty;

        /// <summary>When false startup does not force-reset Sending rows; used by lease-recovery harness scenarios.</summary>
        public bool ResetSendingOnStartup { get; init; } = true;

        /// <summary>Builds the production options with the resolved outbox and runtime database paths.</summary>
        /// <param name="outboxDatabasePath">Absolute <c>&lt;contentRoot&gt;/Data/telegram-log-outbox.db</c> path.</param>
        /// <param name="usersDatabasePath">Absolute users.db path resolved at startup.</param>
        /// <param name="credentialsDatabasePath">Absolute credentials.db path resolved at startup.</param>
        /// <returns>Options carrying every production default from <see cref="TelegramRateLimitPolicy"/>.</returns>
        public static TelegramLogDispatcherOptions CreateDefault(
            string outboxDatabasePath,
            string usersDatabasePath,
            string credentialsDatabasePath) => new()
        {
            OutboxDatabasePath = outboxDatabasePath,
            UsersDatabasePath = usersDatabasePath,
            CredentialsDatabasePath = credentialsDatabasePath
        };
    }

    /// <summary>
    /// Single serialized sender for Telegram logger delivery, driving durable records straight from the SQLite
    /// outbox. Persistence is the source of truth: the in-memory channel carries only Normal (non-durable) logs.
    /// </summary>
    /// <remarks>
    /// Guarantees provided by this dispatcher:
    /// <list type="bullet">
    /// <item><description>At-least-once: a durable record is acked (deleted) only after Telegram accepts the
    /// message. A crash in the window between Telegram acceptance and the local DELETE can duplicate, never loses.</description></item>
    /// <item><description>Atomic claim: rows are claimed by a conditional UPDATE guarded on Status=Pending and
    /// eligibility, so concurrent workers or restart races can never double-own one row.</description></item>
    /// <item><description>Lease recovery: startup force-resets every Sending row (single-instance host) and the
    /// periodic scan expires stale leases, so crashed sends are retried, never stuck.</description></item>
    /// <item><description>Durable retry state: 429, transient, and permanent outcomes are persisted with their
    /// retry schedule (<c>NextAttemptAtUtc</c>) before the send path returns; a restart mid-cooldown still waits.</description></item>
    /// <item><description>Weighted fairness: per drain round at most two Payment rows and one Html row are claimed
    /// (2:1), and one Normal row is sent per round, so a permanent critical backlog cannot starve audit or normal logs.</description></item>
    /// <item><description>Bounded RAM: candidate pages are small, delivered rows are deleted, and the only unbounded
    /// state is the SQLite file itself.</description></item>
    /// </list>
    /// Telegram calls never execute inside an outbox transaction: claim commits, then the network send runs, then
    /// ack/fail commits. Backup generations are committed with payment enqueue, independently of delivery.
    /// A recovered backlog is covered by one global snapshot even when log replay takes minutes.
    /// New requests after snapshot start remain pending for a coalesced follow-up.
    /// </remarks>
    internal sealed class TelegramLogDispatcher : IAsyncDisposable
    {
        /// <summary>
        /// Maximum plain-text log length sent to Telegram, kept below Telegram's hard 4096-character limit.
        /// </summary>
        private const int MaxTelegramLogMessageLength = 3900;

        /// <summary>
        /// Truncates a plain-text log so Telegram accepts it as one message.
        /// </summary>
        /// <param name="message">Plain-text log message; may be empty or include a full stack trace.</param>
        /// <returns>
        /// The original message when it fits Telegram's size limit; otherwise a shortened message with a marker
        /// telling admins the stack was truncated.
        /// </returns>
        internal static string TruncateForTelegramLog(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= MaxTelegramLogMessageLength)
                return message ?? string.Empty;
            return message[..MaxTelegramLogMessageLength] + "\n...[log truncated for Telegram]";
        }

        private const int CandidatesPageSize = 96;
        private const int CriticalPerRound = 2;
        private const int AuditPerRound = 1;
        private const int StatsRoundInterval = 64;

        private readonly TelegramLogOutbox _outbox;
        private readonly TelegramLogDispatcherOptions _options;
        private readonly Func<string, ITelegramLogSender> _senderFactory;
        private readonly Channel<TelegramLogItem> _normalQueue = CreateNormalQueue();
        private readonly SemaphoreSlim _wakeSignal = new(0, 1);
        /// <summary>Coalesced post-commit backup wakeups; durable generations remain authoritative.</summary>
        private readonly SemaphoreSlim _backupWakeSignal = new(0, 1);
        /// <summary>Atomic coordinator read counter used to verify idle connection churn.</summary>
        private long _backupStateReads;
        /// <summary>Number of coordinator state reads, excluding shutdown, for bounded-idle diagnostics and tests.</summary>
        internal long BackupStateReads => Interlocked.Read(ref _backupStateReads);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _worker;
        private readonly object _normalGate = new();

        private DateTime _lastSendAt = DateTime.MinValue;
        private DateTime _lastCountsCheckAt = DateTime.MinValue;
        private DateTime _lastBacklogWarnAt = DateTime.MinValue;
        private DateTime _lastMaintenanceAt = DateTime.MinValue;
        private int _drainRoundsSinceStats;
        private int _droppedNormalCount;
        private long _enqueuedCount;
        private long _enqueueFailureCount;
        private long _deliveredCount;
        private long _rateLimitedCount;
        private long _transientFailureCount;
        private long _permanentRetryCount;
        private long _deadLetteredCount;
        private long _normalSentCount;
        private long _backupRuns;
        private readonly Task _backupWorker;
        private int _disposed;
        private long _backupRequestsCoalesced;
        private long _backupFollowUps;
        private long _backupRequests;
        private string _backupBotId = string.Empty;
        private string _backupChannelId = string.Empty;

        private static Channel<TelegramLogItem> CreateNormalQueue() =>
            Channel.CreateBounded<TelegramLogItem>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

        /// <summary>Starts the single outbox worker and its periodic recovery scanner.</summary>
        /// <param name="senderFactory">Resolves a fresh sender for a bot id at delivery time; never cached here.</param>
        /// <param name="options">Dispatcher configuration; production callers use <see cref="TelegramLogDispatcherOptions.CreateDefault"/>.</param>
        /// <exception cref="ArgumentNullException">When <paramref name="senderFactory"/> or <paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentException">The required outbox database path is blank.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The backup recovery interval is not positive.</exception>
        public TelegramLogDispatcher(Func<string, ITelegramLogSender> senderFactory, TelegramLogDispatcherOptions options)
        {
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.OutboxDatabasePath))
                throw new ArgumentException("OutboxDatabasePath is required.", nameof(options));
            if (_options.BackupRecoveryInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "BackupRecoveryInterval must be positive.");
            _outbox = new TelegramLogOutbox(_options.OutboxDatabasePath);
            _worker = Task.Run(WorkerAsync);
            _backupWorker = Task.Run(RunBackupLoopAsync);
        }

        /// <summary>
        /// Persists a durable Payment/Html record and signals the worker. Blocks the caller only for the SQLite
        /// commit, which is the durability barrier: this method must complete before the logger operation returns.
        /// </summary>
        /// <param name="item">Delivery snapshot captured in the producer context; must not be null.</param>
        /// <returns>
        /// <c>true</c> when the record was committed and is guaranteed to be delivered unless Telegram itself loses
        /// or rejects it; <c>false</c> when the commit failed and this log has no durable guarantee. Failures are
        /// contained locally (console + counter) and never re-logged through Telegram.
        /// </returns>
        /// <remarks>
        /// Order is strictly: INSERT → COMMIT → wake signal → return. If the process dies between the COMMIT and
        /// the wake signal, the periodic scan or the next startup still finds the row, so no wake-up can lose a
        /// record. The dispatch itself is never awaited here; only the durable commit is.
        /// </remarks>
        public bool EnqueueDurable(TelegramLogItem item)
        {
            if (item is null || Volatile.Read(ref _disposed) != 0)
                return false;
            try
            {
                var now = _options.UtcNow();
                var id = _outbox.EnqueueAsync(new TelegramLogOutboxItem(
                    0, now, item.Kind, item.Kind,
                    item.BotId ?? string.Empty, item.LoggerChannelId ?? string.Empty,
                    item.BackupChannelId ?? string.Empty, item.Message ?? string.Empty,
                    0, now, null, TelegramLogOutboxStatus.Pending, null, null)).GetAwaiter().GetResult();
                Interlocked.Increment(ref _enqueuedCount);
                if (item.Kind == TelegramLogDeliveryKind.Payment && !string.IsNullOrWhiteSpace(_options.BackupChannelId)
                    && !string.IsNullOrWhiteSpace(item.BackupChannelId) && item.BackupChannelId != _options.BackupChannelId)
                    Console.WriteLine("[DatabaseBackup] conflicting requested destination; using configured global destination.");
                // The payment row and Requested watermark have committed before either worker is signaled.
                if (item.Kind == TelegramLogDeliveryKind.Payment) SignalBackupWorker();
                WakeWorker();
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _enqueueFailureCount);
                // A failed durable commit must be loud locally: this specific log has NO durability guarantee.
                Console.WriteLine($"[TelegramOutbox] DURABLE ENQUEUE FAILED (no durability guarantee for this log): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Queues a best-effort plain-text log into the bounded memory-only channel, dropping and counting on overflow.
        /// </summary>
        /// <param name="item">Delivery snapshot; null values are ignored.</param>
        /// <remarks>
        /// Normal logs are never durable: a Telegram outage must not grow disk usage for informational noise. The
        /// channel capacity is 256 with drop-write, and every dropped message is counted for the shutdown report.
        /// </remarks>
        public void EnqueueNormal(TelegramLogItem item)
        {
            if (item is null)
                return;
            lock (_normalGate)
            {
                if (!_normalQueue.Writer.TryWrite(item))
                    Interlocked.Increment(ref _droppedNormalCount);
            }
            WakeWorker();
        }

        private void WakeWorker()
        {
            if (!_options.WakeSignalEnabled)
                return;
            try { _wakeSignal.Release(); }
            catch (SemaphoreFullException) { }
        }

        private async Task WorkerAsync()
        {
            var ct = _shutdown.Token;
            try
            {
                if (_options.ResetSendingOnStartup)
                {
                    try
                    {
                        var reset = await _outbox.ResetAllSendingAsync(_options.UtcNow());
                        if (reset > 0)
                            Console.WriteLine($"[TelegramOutbox] startup: reset {reset} stale Sending lease(s); they will be retried (at-least-once).");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TelegramOutbox] startup lease reset failed: {ex.Message}");
                    }
                }

                // Gate maintenance on a fresh start time so the first maintenance pass (dead-letter purge, WAL
                // checkpoint, PRAGMA optimize) runs only after a full MaintenanceInterval, never on the first scan.
                _lastMaintenanceAt = _options.UtcNow();

                while (!ct.IsCancellationRequested)
                {
                    var worked = await DrainDueAsync(ct);
                    if (LeaveNormalGate(out var normal))
                    {
                        await SendNormalAsync(normal, ct);
                        continue;
                    }
                    if (worked)
                    {
                        _drainRoundsSinceStats++;
                        await MaybeMaintenanceAsync(_options.UtcNow());
                        continue;
                    }
                    await MaybeMaintenanceAsync(_options.UtcNow());
                    try { await _wakeSignal.WaitAsync(_options.ScanInterval, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramOutbox] dispatcher worker faulted: {ex}");
            }
        }

        private bool LeaveNormalGate(out TelegramLogItem item)
        {
            lock (_normalGate)
                return _normalQueue.Reader.TryRead(out item);
        }

        /// <summary>
        /// Runs one weighted drain round: claims up to <see cref="CriticalPerRound"/> Payment and
        /// <see cref="AuditPerRound"/> Html rows from the outbox and sends each.
        /// </summary>
        /// <param name="ct">Shutdown token passed to Telegram sends.</param>
        /// <returns><c>true</c> when at least one row was claimed and sent this round.</returns>
        /// <remarks>
        /// The 2:1 claim ratio is the starvation bound: while any Html row is due, every round claims one, so a
        /// continuous critical backlog can delay audit logs but never starve them mathematically forever. Rows
        /// already claimed by another worker simply fail their conditional UPDATE and are skipped.
        /// </remarks>
        private async Task<bool> DrainDueAsync(CancellationToken ct)
        {
            var now = _options.UtcNow();
            IReadOnlyList<TelegramLogOutboxItem> candidates;
            try
            {
                candidates = await _outbox.LoadCandidatesAsync(now, CandidatesPageSize);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramOutbox] candidate load failed: {ex.Message}");
                return false;
            }
            if (candidates.Count == 0)
                return false;

            var criticalTaken = 0;
            var auditTaken = 0;
            var anyWork = false;
            foreach (var candidate in candidates)
            {
                if (candidate.Priority == TelegramLogDeliveryKind.Payment && criticalTaken >= CriticalPerRound)
                    continue;
                if (candidate.Priority == TelegramLogDeliveryKind.Html && auditTaken >= AuditPerRound)
                    continue;

                var claimedAttempt = candidate.AttemptCount + 1;
                var claimed = false;
                try
                {
                    claimed = await _outbox.ClaimAsync(candidate.Id, claimedAttempt, now.Add(_options.LeaseDuration), now);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TelegramOutbox] claim failed id={candidate.Id}: {ex.Message}");
                }
                if (!claimed)
                    continue;

                if (candidate.Priority == TelegramLogDeliveryKind.Payment) criticalTaken++;
                else auditTaken++;
                anyWork = true;
                await SendDurableAsync(candidate, claimedAttempt);
            }
            return anyWork;
        }

        /// <summary>
        /// Sends one claimed durable row and persists the definitive outcome: DELETE on Telegram success, or a
        /// retry/DeadLetter schedule on failure. Never runs Telegram I/O inside an outbox transaction.
        /// </summary>
        /// <param name="row">Claimed outbox row snapshot.</param>
        /// <param name="claimedAttempt">One-based attempt number of this send.</param>
        /// <remarks>
        /// Outcome contract: Telegram success → local ack (DELETE). 429 → Pending at
        /// <c>now + RetryAfter(+1s buffer, capped)</c>, so a restart mid-cooldown cannot retry early. Transient
        /// network/5xx → Pending with exponential backoff (5s doubling, 5-minute cap). Permanent 400/401/403/410 →
        /// short backoff until <see cref="TelegramLogDispatcherOptions.PermanentFailureMaxAttempts"/>, then a
        /// retained DeadLetter row. Shutdown cancellation mid-send leaves the row Sending for lease recovery:
        /// never ack, never lose.
        /// </remarks>
        private async Task SendDurableAsync(TelegramLogOutboxItem row, int claimedAttempt)
        {
            var now = _options.UtcNow();
            try
            {
                var parseMode = row.DeliveryKind == TelegramLogDeliveryKind.Plain ? (ParseMode?)null : ParseMode.Html;
                await PacedSendAsync(
                    () => _senderFactory(row.BotId).SendTextMessageAsync(
                        row.LoggerChannelId, row.Message, parseMode, _shutdown.Token));
                await _outbox.AcknowledgeAsync(row.Id);
                Interlocked.Increment(ref _deliveredCount);
                // Backup intent was committed with enqueue; replaying delivery never requests another snapshot.
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Crash/shutdown mid-send: the row keeps its Sending lease. Startup reset or the periodic lease
                // recovery re-queues it, which is the explicit at-least-once duplicate window.
            }
            catch (Exception ex) when (TelegramRateLimitPolicy.IsRateLimited(ex))
            {
                var delay = TelegramRateLimitPolicy.GetRetryDelay(ex);
                try
                {
                    await _outbox.FailAsync(row.Id, now.Add(delay), $"429 retry after {delay.TotalSeconds:0}s: {ex.Message}", now, deadLetter: false);
                }
                catch (Exception persistEx)
                {
                    Console.WriteLine($"[TelegramOutbox] 429 persistence failed id={row.Id}: {persistEx.Message}");
                }
                Interlocked.Increment(ref _rateLimitedCount);
                Console.WriteLine($"[TelegramOutbox] 429 id={row.Id}, next attempt in {delay.TotalSeconds:0}s");
            }
            catch (Exception ex) when (TelegramRateLimitPolicy.IsTransientFailure(ex, _shutdown.IsCancellationRequested))
            {
                var delay = TelegramRateLimitPolicy.GetTransientRetryDelay(claimedAttempt);
                try
                {
                    await _outbox.FailAsync(row.Id, now.Add(delay), $"{ex.GetType().Name}: {ex.Message}", now, deadLetter: false);
                }
                catch (Exception persistEx)
                {
                    Console.WriteLine($"[TelegramOutbox] transient persistence failed id={row.Id}: {persistEx.Message}");
                }
                Interlocked.Increment(ref _transientFailureCount);
                Console.WriteLine($"[TelegramOutbox] transient failure id={row.Id} attempt={claimedAttempt}, next in {delay.TotalSeconds:0}s");
            }
            catch (Exception ex) when (TelegramRateLimitPolicy.IsPermanentFailure(ex))
            {
                if (claimedAttempt >= _options.PermanentFailureMaxAttempts)
                {
                    try
                    {
                        await _outbox.FailAsync(row.Id, now, $"permanent ({ex.GetType().Name}): {ex.Message}", now, deadLetter: true);
                    }
                    catch (Exception persistEx)
                    {
                        Console.WriteLine($"[TelegramOutbox] dead-letter persistence failed id={row.Id}: {persistEx.Message}");
                    }
                    Interlocked.Increment(ref _deadLetteredCount);
                    Console.WriteLine($"[TelegramOutbox] DEAD-LETTERED id={row.Id} attempt={claimedAttempt}: {ex.Message}");
                }
                else
                {
                    var delay = TelegramRateLimitPolicy.GetTransientRetryDelay(claimedAttempt);
                    try
                    {
                        await _outbox.FailAsync(row.Id, now.Add(delay), $"permanent retry ({ex.GetType().Name}): {ex.Message}", now, deadLetter: false);
                    }
                    catch (Exception persistEx)
                    {
                        Console.WriteLine($"[TelegramOutbox] permanent-retry persistence failed id={row.Id}: {persistEx.Message}");
                    }
                    Interlocked.Increment(ref _permanentRetryCount);
                    Console.WriteLine($"[TelegramOutbox] permanent error id={row.Id} attempt={claimedAttempt}, retrying in {delay.TotalSeconds:0}s");
                }
            }
            catch (Exception ex)
            {
                var delay = TelegramRateLimitPolicy.GetTransientRetryDelay(claimedAttempt);
                try
                {
                    await _outbox.FailAsync(row.Id, now.Add(delay), $"{ex.GetType().Name}: {ex.Message}", now, deadLetter: false);
                }
                catch (Exception persistEx)
                {
                    Console.WriteLine($"[TelegramOutbox] failure persistence failed id={row.Id}: {persistEx.Message}");
                }
                Interlocked.Increment(ref _transientFailureCount);
                Console.WriteLine($"[TelegramOutbox] unexpected delivery error id={row.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends one best-effort normal log with a single attempt; failures drop the message locally.
        /// </summary>
        /// <param name="item">Normal item dequeued from the bounded channel.</param>
        /// <param name="ct">Shutdown token.</param>
        private async Task SendNormalAsync(TelegramLogItem item, CancellationToken ct)
        {
            try
            {
                await PacedSendAsync(
                    () => _senderFactory(item.BotId).SendTextMessageAsync(
                        item.LoggerChannelId, TruncateForTelegramLog(item.Message), null, ct));
                Interlocked.Increment(ref _normalSentCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _droppedNormalCount);
                Console.WriteLine($"[TelegramOutbox] normal log dropped after send failure: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task PacedSendAsync(Func<Task> send)
        {
            var wait = _options.MinimumSendInterval - (_options.UtcNow() - _lastSendAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _shutdown.Token);
            await send();
            _lastSendAt = _options.UtcNow();
        }

        /// <summary>
        /// Runs the time-boxed backlog warning and SQLite maintenance while the worker is idle or every
        /// <see cref="StatsRoundInterval"/> drain rounds.
        /// </summary>
        private async Task MaybeMaintenanceAsync(DateTime now)
        {
            if (_drainRoundsSinceStats >= StatsRoundInterval || now - _lastCountsCheckAt >= _options.ScanInterval)
            {
                _drainRoundsSinceStats = 0;
                _lastCountsCheckAt = now;
                try
                {
                    var (pending, deadLetter) = await _outbox.GetCountsAsync();
                    if (now - _lastBacklogWarnAt >= TelegramRateLimitPolicy.BacklogWarningInterval &&
                        pending > TelegramRateLimitPolicy.BacklogWarningPendingThreshold)
                    {
                        _lastBacklogWarnAt = now;
                        // Console/file only, never Telegram: the channel is the thing that is down.
                        Console.WriteLine($"[TelegramOutbox] backlog high: pending={pending} deadletter={deadLetter}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TelegramOutbox] counts check failed: {ex.Message}");
                }
            }

            if (now - _lastMaintenanceAt >= TelegramRateLimitPolicy.MaintenanceInterval)
            {
                _lastMaintenanceAt = now;
                await RunMaintenanceAsync();
            }
        }

        private async Task RunMaintenanceAsync()
        {
            try
            {
                var cutoff = _options.UtcNow().Subtract(TelegramRateLimitPolicy.DeadLetterRetention);
                var purged = await _outbox.PurgeDeadLettersAsync(cutoff);
                await _outbox.CheckpointAsync();
                if (purged > 0)
                    Console.WriteLine($"[TelegramOutbox] maintenance: purged {purged} dead-letter row(s) older than {TelegramRateLimitPolicy.DeadLetterRetention.TotalDays:0} days.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramOutbox] maintenance failed: {ex.Message}");
            }
        }

        /// <summary>Coalesces backup notifications after durable payment commit without blocking producers.</summary>
        /// <remarks>A missed signal is harmless: startup and the ten-second default recovery scan read SQLite.</remarks>
        private void SignalBackupWorker()
        {
            try { _backupWakeSignal.Release(); }
            catch (SemaphoreFullException) { } // A pending wake already covers every committed generation.
        }

        /// <summary>Reads the authoritative backup watermarks and counts coordinator database accesses.</summary>
        /// <returns>Global requested/covered generations and the persisted non-secret destination fallback.</returns>
        /// <remarks>No network operation runs inside this short SQLite read.</remarks>
        private async Task<(long Requested, long Covered, string BotId, string ChannelId)> ReadBackupStateAsync()
        {
            Interlocked.Increment(ref _backupStateReads);
            return await _outbox.ReadBackupAsync();
        }

        /// <summary>Consumes durable global backup generations, waking on committed payment intent or recovery timeout.</summary>
        /// <returns>The tracked backup lifetime, stopped and awaited by dispatcher disposal.</returns>
        /// <remarks>Startup reads SQLite immediately. Idle waits default to ten seconds; signals only improve latency.
        /// All requests observed before snapshot start are covered together. Later requests cause a coalesced follow-up.
        /// Failure or a missing destination leaves the watermark pending. Neither log retry nor ACK creates intent.</remarks>
        private async Task RunBackupLoopAsync()
        {
            var token = _shutdown.Token;
            var startup = true;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var state = await ReadBackupStateAsync();
                    if (startup)
                    {
                        Console.WriteLine($"[DatabaseBackup] startup requestedGeneration={state.Requested} coveredGeneration={state.Covered} pendingRequests={state.Requested - state.Covered}");
                        startup = false;
                    }
                    if (state.Requested <= state.Covered)
                    {
                        await _options.BackupWaitAsync(_backupWakeSignal, _options.BackupRecoveryInterval, token);
                        continue;
                    }
                    var first = _options.UtcNow(); var quiet = first; var observed = state.Requested;
                    while (true)
                    {
                        var now = _options.UtcNow();
                        var remaining = TimeSpan.FromTicks(Math.Min(
                            (_options.BackupDebounce - (now - quiet)).Ticks,
                            (_options.BackupMaxDelay - (now - first)).Ticks));
                        if (remaining <= TimeSpan.Zero) break;
                        await _options.BackupWaitAsync(_backupWakeSignal, remaining, token);
                        state = await ReadBackupStateAsync();
                        if (state.Requested != observed) { observed = state.Requested; quiet = _options.UtcNow(); }
                    }
                    // This read is the snapshot boundary: any later durable request remains above Covered.
                    state = await ReadBackupStateAsync();
                    _backupBotId = TelegramLogDispatcherOptions.SelectDestination(_options.BackupBotId, state.BotId);
                    _backupChannelId = TelegramLogDispatcherOptions.SelectDestination(_options.BackupChannelId, state.ChannelId);
                    if (string.IsNullOrWhiteSpace(_backupBotId) || string.IsNullOrWhiteSpace(_backupChannelId))
                    {
                        _options.BackupWarning("[DatabaseBackup] destination unavailable; durable generation remains pending.");
                        // Ignore producer wakes during configuration failure to bound warnings and database activity.
                        await Task.Delay(_options.BackupRecoveryInterval, token);
                        continue;
                    }
                    var generation = state.Requested;
                    Interlocked.Exchange(ref _backupRequests, generation);
                    Interlocked.Add(ref _backupRequestsCoalesced, Math.Max(0, generation - state.Covered - 1));
                    Interlocked.Increment(ref _backupRuns);
                    Console.WriteLine($"[DatabaseBackup] started generation={generation} previouslyCovered={state.Covered}");
                    await BackupDatabasesOnceAsync();
                    await _outbox.CoverBackupAsync(generation);
                    state = await ReadBackupStateAsync();
                    if (state.Requested > generation) Interlocked.Increment(ref _backupFollowUps);
                    Console.WriteLine($"[DatabaseBackup] completed coveredGeneration={generation} requestedGeneration={state.Requested} followUp={state.Requested > generation}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DatabaseBackup] pending retry errorType={ex.GetType().Name}");
                    try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        /// <summary>
        /// Creates SQLite-consistent copies and uploads both database documents to the authoritative channel.
        /// A failure leaves the generation pending; the tracked worker retries the complete pair after backoff.
        /// </summary>
        /// <returns>A task completing only after every configured database document has uploaded successfully.</returns>
        /// <remarks>Each SQLite backup is consistent individually. There is no atomic snapshot across both databases
        /// or atomic Telegram upload of the pair; partial failure may repeat the first document on retry.</remarks>
        private async Task BackupDatabasesOnceAsync()
        {
            var botId = Volatile.Read(ref _backupBotId);
            var channelId = Volatile.Read(ref _backupChannelId);
            foreach (var target in BackupTargets())
            {
                try
                {
                    // The temp file is fully flushed and CLOSED before the upload handle opens it, so the write
                    // handle (which cannot share) never conflicts with the read handle on any OS. The upload
                    // stream must stay open only for the duration of the document request.
                    // SQLite online backup includes committed WAL pages; raw file copying does not.
                    using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target.Source};Mode=ReadOnly"))
                    using (var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target.Temp}"))
                    {
                        source.Open(); destination.Open(); source.BackupDatabase(destination);
                    }
                    await using var upload = new FileStream(target.Temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await _senderFactory(botId).SendDocumentAsync(channelId, target.FileName, upload, _shutdown.Token);
                }
                catch { throw; }
            }
        }

        /// <summary>
        /// Enumerates the runtime databases copied by a backup pass, with temp files adjacent to their sources.
        /// </summary>
        /// <returns>Source path, temp path, and upload file name for each configured database.</returns>
        private IEnumerable<(string Source, string Temp, string FileName)> BackupTargets()
        {
            if (!string.IsNullOrWhiteSpace(_options.UsersDatabasePath))
            {
                var dir = Path.GetDirectoryName(_options.UsersDatabasePath) ?? ".";
                yield return (_options.UsersDatabasePath, Path.Combine(dir, "users_backup.db"), "users.db");
            }
            if (!string.IsNullOrWhiteSpace(_options.CredentialsDatabasePath))
            {
                var dir = Path.GetDirectoryName(_options.CredentialsDatabasePath) ?? ".";
                yield return (_options.CredentialsDatabasePath, Path.Combine(dir, "credentials_backup.db"), "credentials.db");
            }
        }

        /// <summary>Stops the worker, releases resources, and prints the final delivery statistics.</summary>
        /// <returns>A task completing after log shutdown and bounded backup draining.</returns>
        /// <remarks>New durable admission closes first. Backup work receives ten seconds to drain and five seconds
        /// for cooperative cancellation. Pending durable generations survive forced process termination.</remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _normalQueue.Writer.TryComplete();
            // Give an active/pending backup a bounded opportunity to cover its durable generation.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var state = await _outbox.ReadBackupAsync();
                if (state.Requested <= state.Covered) break;
                await Task.Delay(250);
            }
            _shutdown.Cancel();
            try { await _backupWorker.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            try { await _worker; }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            // A cancellation-ignoring transport remains observed by its tracked task. Keep its resources alive
            // until it returns rather than disposing the outbox beneath a late backup acknowledgement.
            if (_backupWorker.IsCompleted)
            {
                _shutdown.Dispose();
                await _outbox.DisposeAsync();
            }
            var dropped = Interlocked.Exchange(ref _droppedNormalCount, 0);
            Console.WriteLine(
                $"[TelegramOutbox] shutdown: enqueued={Interlocked.Read(ref _enqueuedCount)} enqueueFailures={Interlocked.Read(ref _enqueueFailureCount)} " +
                $"delivered={Interlocked.Read(ref _deliveredCount)} rateLimited={Interlocked.Read(ref _rateLimitedCount)} " +
                $"transient={Interlocked.Read(ref _transientFailureCount)} permanentRetries={Interlocked.Read(ref _permanentRetryCount)} " +
                $"deadLettered={Interlocked.Read(ref _deadLetteredCount)} normalSent={Interlocked.Read(ref _normalSentCount)} " +
                $"backupRequests={Interlocked.Read(ref _backupRequests)} backupRuns={Interlocked.Read(ref _backupRuns)} backupRequestsCoalesced={Interlocked.Read(ref _backupRequestsCoalesced)} backupFollowUps={Interlocked.Read(ref _backupFollowUps)} droppedNormal={dropped}");
        }
    }
}
