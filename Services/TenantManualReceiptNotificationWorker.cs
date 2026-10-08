using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Adminbot.Services.Telemetry;

internal interface ITenantManualReceiptNotificationSender
{
    Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken);
}

internal sealed class TenantManualReceiptNotificationSender : ITenantManualReceiptNotificationSender
{
    private readonly IServiceScopeFactory _scopeFactory;
    public TenantManualReceiptNotificationSender(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SalesAssistantService>()
            .NOTIFYMANUALRECEIPTASYNC(receipt, cancellationToken);
    }
}

/// <summary>Relays persisted tenant receipt notifications outside the customer's serialized Telegram update lane.</summary>
public sealed class TenantManualReceiptNotificationWorker : BackgroundService
{
    private const int MaximumAttempts = 6;
    private const int MaximumBatchSize = 10;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(15);

    private readonly UserDbContextFactory _contextFactory;
    private readonly ITenantManualReceiptNotificationSender _sender;
    private readonly ILogger<TenantManualReceiptNotificationWorker> _logger;

    public TenantManualReceiptNotificationWorker(
        UserDbContextFactory contextFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<TenantManualReceiptNotificationWorker> logger)
        : this(contextFactory, new TenantManualReceiptNotificationSender(scopeFactory), logger)
    {
    }

    internal TenantManualReceiptNotificationWorker(
        UserDbContextFactory contextFactory,
        ITenantManualReceiptNotificationSender sender,
        ILogger<TenantManualReceiptNotificationWorker> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Scans and relays durable tenant receipt notifications under the existing lease/retry policy.</summary>
    /// <param name="stoppingToken">Host lifetime cancellation for local scans and existing background delivery.</param>
    /// <returns>The worker lifetime task; individual notification failures retain the existing handling.</returns>
    /// <remarks>A fixed payload-free SQLite category identifies this worker's reads/writes and contention.
    /// Tenant ownership remains in the existing receipt queries; no tenant/customer id or payment details enter telemetry.
    /// The category creates no database write, Telegram request, balance effect or additional retry.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sqliteDiagnostics = LatencySqliteOperationScope.Push(LatencySqliteOperationCategory.TenantManualReceiptNotification);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError("Tenant receipt notification scan failed. ErrorType={ErrorType}", ex.GetType().Name);
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    /// <summary>Recovers abandoned claims, claims due receipts, and delivers one bounded durable scan.</summary>
    /// <param name="cancellationToken">Host token for local scan work and receipt transport; accepted-send quarantine is independent of it.</param>
    /// <returns>The number of notifications claimed for this scan, including attempts ending in uncertainty; zero when no receipt is due.</returns>
    /// <remarks>An idle WAL scan is read-only. A send acknowledgement followed by persistence failure never authorizes another automatic send.</remarks>
    /// <exception cref="OperationCanceledException">The scan or transport is cancelled before an accepted-send outcome can be handled.</exception>
    /// <example><code>var attempted = await worker.ProcessOnceAsync(cancellationToken);</code></example>
    internal async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        await MarkExpiredClaimsUncertainAsync(cancellationToken);
        var rows = await ClaimDueBatchAsync(cancellationToken);
        foreach (var row in rows)
            await DeliverAsync(row, cancellationToken);
        return rows.Count;
    }

    /// <summary>Quarantines expired processing leases without requesting a writer when no lease is eligible.</summary>
    /// <param name="cancellationToken">Token for the eligibility read and conditional update.</param>
    /// <returns>A task that completes after currently eligible claims stop automatic delivery.</returns>
    /// <remarks>A claim becoming eligible after the read is deferred to the next scan; the write retains the same predicate.</remarks>
    /// <exception cref="OperationCanceledException">The local eligibility read or update is cancelled.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">SQLite rejects the local read or an eligible recovery update.</exception>
    /// <example>An expired processing lease is quarantined; an unexpired lease is left untouched.</example>
    private async Task MarkExpiredClaimsUncertainAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var expired = db.TenantManualReceiptNotifications
            .Where(x => x.Status == TenantManualReceiptNotificationStatuses.Processing &&
                        x.LeaseUntilUtc.HasValue && x.LeaseUntilUtc <= now);
        // Even a no-match UPDATE takes SQLite's writer lock, so idle maintenance must remain read-only.
        if (!await expired.AnyAsync(cancellationToken))
            return;
        var count = await expired.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantManualReceiptNotificationStatuses.DeliveryUncertain)
                .SetProperty(x => x.LastError, "processing_lease_expired_after_possible_delivery")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (count > 0)
            _logger.LogError("Tenant receipt notifications became delivery-uncertain. Count={Count}", count);
    }

    private async Task<IReadOnlyList<TenantManualReceiptNotification>> ClaimDueBatchAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var ids = await db.TenantManualReceiptNotifications.AsNoTracking()
            .Where(x => x.Status == TenantManualReceiptNotificationStatuses.Pending &&
                        (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now))
            .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(MaximumBatchSize).ToListAsync(cancellationToken);

        var claimed = new List<TenantManualReceiptNotification>();
        foreach (var id in ids)
        {
            var claimToken = Guid.NewGuid().ToString("N");
            var updated = await db.TenantManualReceiptNotifications
                .Where(x => x.Id == id && x.Status == TenantManualReceiptNotificationStatuses.Pending &&
                            (!x.NextAttemptAtUtc.HasValue || x.NextAttemptAtUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, TenantManualReceiptNotificationStatuses.Processing)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.ClaimToken, claimToken)
                    .SetProperty(x => x.LeaseUntilUtc, now.Add(ClaimLease))
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            if (updated != 1) continue;
            claimed.Add(await db.TenantManualReceiptNotifications.AsNoTracking()
                .SingleAsync(x => x.Id == id && x.ClaimToken == claimToken, cancellationToken));
        }
        return claimed;
    }

    /// <summary>Sends a claimed receipt and keeps acknowledgement persistence failures separate from transport failures.</summary>
    /// <param name="notification">Detached receipt notification carrying this attempt's claim token.</param>
    /// <param name="cancellationToken">Host token for receipt loading, transport and acknowledgement.</param>
    /// <returns>A task that completes after delivery or a conservative non-retryable persistence outcome.</returns>
    /// <remarks>Only transport failures can release a retryable claim. Once the sender returns a message id,
    /// acknowledgement persistence, cancellation, or logger failure cannot return it to pending.</remarks>
    /// <exception cref="OperationCanceledException">The receipt read or pre-acknowledgement transport wait is cancelled.</exception>
    /// <example>A returned message id followed by an ACK database failure leaves delivery uncertain, not retryable.</example>
    private async Task DeliverAsync(TenantManualReceiptNotification notification, CancellationToken cancellationToken)
    {
        TenantManualPaymentReceipt receipt;
        await using (var db = _contextFactory.CreateDbContext())
        {
            receipt = await db.TenantManualPaymentReceipts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == notification.ReceiptId, cancellationToken);
        }

        if (receipt == null)
        {
            await MarkTerminalAsync(notification, TenantManualReceiptNotificationStatuses.FailedPermanent,
                "receipt_not_found", cancellationToken);
            return;
        }

        int? messageId;
        try
        {
            messageId = await _sender.SendAsync(receipt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation can race with Telegram acceptance; keep the lease intact rather than authorizing another send.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Tenant receipt notification attempt failed. NotificationId={NotificationId} ErrorType={ErrorType}",
                notification.Id, ex.GetType().Name);
            await RetryOrExhaustAsync(notification, "assistant_delivery_failed", cancellationToken);
            return;
        }

        // Persistence and diagnostics after an accepted send must never enter the transport retry branch.
        if (messageId.HasValue)
        {
            try
            {
                await MarkDeliveredAsync(notification, messageId.Value, cancellationToken);
            }
            catch (Exception persistenceException)
            {
                await MarkDeliveryUncertainAfterAcknowledgementAsync(notification, persistenceException);
            }
            return;
        }

        await RetryOrExhaustAsync(notification, "assistant_delivery_unavailable", cancellationToken);
    }

    /// <summary>Persists an accepted message only while this attempt still owns the processing claim.</summary>
    /// <param name="notification">Detached notification containing the active claim token.</param>
    /// <param name="messageId">Message id acknowledged by the receipt sender.</param>
    /// <param name="cancellationToken">Token for the conditional acknowledgement update.</param>
    /// <returns>A task that completes when exactly one owned claim becomes delivered.</returns>
    /// <exception cref="InvalidOperationException">The original processing claim no longer matches a row.</exception>
    /// <remarks>The conditional update is not replayed: a provider failure during command cleanup may follow an applied acknowledgement.</remarks>
    /// <exception cref="OperationCanceledException">The acknowledgement update is cancelled after Telegram acceptance.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Acknowledgement execution or cleanup fails; the update may already have applied.</exception>
    private async Task MarkDeliveredAsync(TenantManualReceiptNotification notification, int messageId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var updated = await db.TenantManualReceiptNotifications
            .Where(x => x.Id == notification.Id &&
                        x.Status == TenantManualReceiptNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantManualReceiptNotificationStatuses.Delivered)
                .SetProperty(x => x.TelegramMessageId, messageId)
                .SetProperty(x => x.DeliveredAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.LastError, (string)null)
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null), cancellationToken);
        if (updated != 1)
            throw new InvalidOperationException("The tenant receipt notification delivery claim no longer matches.");
    }

    private async Task RetryOrExhaustAsync(TenantManualReceiptNotification notification, string safeError, CancellationToken cancellationToken)
    {
        if (notification.AttemptCount >= MaximumAttempts)
        {
            await MarkTerminalAsync(notification, TenantManualReceiptNotificationStatuses.ManualReview, safeError, cancellationToken);
            return;
        }

        var seconds = Math.Min(MaximumRetryDelay.TotalSeconds,
            InitialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, notification.AttemptCount - 1)));
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        await db.TenantManualReceiptNotifications
            .Where(x => x.Id == notification.Id && x.Status == TenantManualReceiptNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantManualReceiptNotificationStatuses.Pending)
                .SetProperty(x => x.NextAttemptAtUtc, now.AddSeconds(seconds))
                .SetProperty(x => x.LastError, safeError)
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    /// <summary>Stops automatic delivery only while the original attempt still owns its processing claim.</summary>
    /// <param name="notification">Detached tenant receipt notification with the attempt's original claim token.</param>
    /// <param name="status">Terminal outbox state, including delivery-uncertain after an accepted send; never pending.</param>
    /// <param name="safeError">Non-secret classification stored for operator recovery, not raw transport or database exception text.</param>
    /// <param name="cancellationToken">Token for this single conditional users.db update; accepted-send quarantine passes None.</param>
    /// <returns>A task completing after the owned claim is terminal or a stale claim matches no row.</returns>
    /// <remarks>The processing/status/token predicate prevents downgrading an applied acknowledgement or overwriting a replacement claim.</remarks>
    /// <exception cref="OperationCanceledException">The supplied local-write token is cancelled.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The single terminal update cannot complete or its outcome is ambiguous.</exception>
    /// <example>An accepted send uses DeliveryUncertain and CancellationToken.None; an already Delivered row remains untouched.</example>
    private async Task MarkTerminalAsync(TenantManualReceiptNotification notification, string status, string safeError, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        await db.TenantManualReceiptNotifications
            .Where(x => x.Id == notification.Id && x.Status == TenantManualReceiptNotificationStatuses.Processing &&
                        x.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.LastError, safeError)
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    /// <summary>Attempts to quarantine an accepted receipt after acknowledgement persistence fails.</summary>
    /// <param name="notification">Detached notification carrying the original processing claim token.</param>
    /// <param name="persistenceException">Failure recorded locally without persisting raw exception text.</param>
    /// <returns>A task that completes after one fresh-context quarantine attempt, even if it cannot persist.</returns>
    /// <remarks>
    /// The token/status predicate preserves an acknowledgement that applied before command cleanup failed. If quarantine
    /// also fails, the processing lease remains fail-closed and later expires to delivery-uncertain, never pending.
    /// </remarks>
    /// <example>If both acknowledgement and quarantine fail, lease recovery stops further automatic sends.</example>
    private async Task MarkDeliveryUncertainAfterAcknowledgementAsync(
        TenantManualReceiptNotification notification, Exception persistenceException)
    {
        try
        {
            // Shutdown after acceptance must not cancel the independent attempt to disable automatic resend.
            await MarkTerminalAsync(notification, TenantManualReceiptNotificationStatuses.DeliveryUncertain,
                "telegram_ack_persistence_uncertain", CancellationToken.None);
        }
        catch (Exception quarantineException)
        {
            _logger.LogError(
                "Tenant receipt acknowledgement and uncertainty persistence both failed; processing lease retained. NotificationId={NotificationId} ErrorType={ErrorType}",
                notification.Id, quarantineException.GetType().Name);
        }

        _logger.LogError(
            "Tenant receipt was accepted but acknowledgement persistence failed; automatic resend is disabled. NotificationId={NotificationId} ErrorType={ErrorType}",
            notification.Id, persistenceException.GetType().Name);
    }
}
