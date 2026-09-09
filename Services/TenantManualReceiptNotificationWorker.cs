using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>Processes one durable scan; internal for restart/idempotency regression tests.</summary>
    internal async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        await MarkExpiredClaimsUncertainAsync(cancellationToken);
        var rows = await ClaimDueBatchAsync(cancellationToken);
        foreach (var row in rows)
            await DeliverAsync(row, cancellationToken);
        return rows.Count;
    }

    private async Task MarkExpiredClaimsUncertainAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        var count = await db.TenantManualReceiptNotifications
            .Where(x => x.Status == TenantManualReceiptNotificationStatuses.Processing &&
                        x.LeaseUntilUtc.HasValue && x.LeaseUntilUtc <= now)
            .ExecuteUpdateAsync(setters => setters
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

        try
        {
            var messageId = await _sender.SendAsync(receipt, cancellationToken);
            if (messageId.HasValue)
            {
                await MarkDeliveredAsync(notification, messageId.Value, cancellationToken);
                return;
            }

            await RetryOrExhaustAsync(notification, "assistant_delivery_unavailable", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Tenant receipt notification attempt failed. NotificationId={NotificationId} ErrorType={ErrorType}",
                notification.Id, ex.GetType().Name);
            await RetryOrExhaustAsync(notification, "assistant_delivery_failed", cancellationToken);
        }
    }

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
        {
            _logger.LogError("Tenant receipt notification acknowledgement became uncertain. NotificationId={NotificationId}", notification.Id);
            await MarkDeliveryUncertainAsync(notification.Id, cancellationToken);
        }
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

    private async Task MarkDeliveryUncertainAsync(int notificationId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = _contextFactory.CreateDbContext();
        await db.TenantManualReceiptNotifications.Where(x => x.Id == notificationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, TenantManualReceiptNotificationStatuses.DeliveryUncertain)
                .SetProperty(x => x.LastError, "telegram_ack_persistence_uncertain")
                .SetProperty(x => x.ClaimToken, (string)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }
}
