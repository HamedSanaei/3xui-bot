using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

/// <summary>
/// Delivers durable owned-wallet settlement notifications independently of financial settlement.
/// </summary>
/// <remarks>
/// This hosted worker depends only on <see cref="UserDbContextFactory"/>, <see cref="BotClientProvider"/>, and
/// Telegram. It intentionally has no <c>CredentialsDbContext</c>, wallet ledger, provider settlement service, tenant
/// fulfillment service, or XUI dependency, so retries cannot credit a wallet or fulfill an order again.
///
/// Pending rows are polled every fifteen seconds and claimed with a two-minute users.db lease. Transient delivery
/// failures retry at most six times with bounded exponential backoff. An expired processing lease is permanently
/// moved to delivery-uncertain instead of being resent because Telegram may have accepted the original message before
/// the process crashed. Routine successful/retry events stay at Debug; permanent or ambiguous outcomes are warnings
/// or errors suitable for operator attention.
/// </remarks>
public sealed class PaymentSettlementNotificationWorker : BackgroundService
{
    /// <summary>Maximum Telegram claims before a transiently failing row moves to manual review.</summary>
    private const int MaximumAttempts = 6;
    /// <summary>Maximum due rows claimed during one fifteen-second scan.</summary>
    private const int MaximumBatchSize = 20;
    /// <summary>Delay between normal outbox scans.</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);
    /// <summary>Processing ownership window after which delivery outcome is treated as uncertain.</summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    /// <summary>First transient retry delay before the factor-four backoff is applied.</summary>
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(30);
    /// <summary>Upper bound for any automatic transient retry delay.</summary>
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(6);

    /// <summary>Factory for independent users.db claim and state-transition contexts.</summary>
    private readonly UserDbContextFactory _contextFactory;
    /// <summary>Resolves the originating owned Telegram bot without exposing its token to the outbox.</summary>
    private readonly BotClientProvider _botClientProvider;
    /// <summary>Structured delivery diagnostics; notification bodies and secrets are never logged.</summary>
    private readonly ILogger<PaymentSettlementNotificationWorker> _logger;

    /// <summary>
    /// Creates the delivery-only settlement notification worker.
    /// </summary>
    /// <param name="contextFactory">
    /// Factory for independent users.db contexts used to claim and update notification rows atomically.
    /// </param>
    /// <param name="botClientProvider">
    /// Runtime provider that resolves the originating owned bot client from the persisted internal bot id.
    /// </param>
    /// <param name="logger">Structured logger that never receives bot tokens or notification message bodies.</param>
    public PaymentSettlementNotificationWorker(
        UserDbContextFactory contextFactory,
        BotClientProvider botClientProvider,
        ILogger<PaymentSettlementNotificationWorker> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _botClientProvider = botClientProvider ?? throw new ArgumentNullException(nameof(botClientProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reconciles abandoned claims and processes due notification rows until application shutdown.
    /// </summary>
    /// <param name="stoppingToken">Host lifetime token that cancels polling and Telegram delivery during shutdown.</param>
    /// <returns>A task that completes when the host requests shutdown.</returns>
    /// <remarks>
    /// Startup performs no historical backfill and no financial work. It only changes stale outbox processing claims
    /// to delivery-uncertain. A loop-level database failure is logged and retried on the next scan without touching
    /// provider payment or wallet state.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MarkExpiredClaimsUncertainAsync(stoppingToken);
                var claimed = await ClaimDueBatchAsync(stoppingToken);
                foreach (var notification in claimed)
                    await DeliverClaimAsync(notification, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment settlement notification worker scan failed.");
            }

            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Converts expired processing leases into a terminal delivery-uncertain state without resending them.
    /// </summary>
    /// <param name="cancellationToken">Host token for the users.db read and update.</param>
    /// <returns>A task that completes after every currently expired claim is quarantined.</returns>
    /// <remarks>
    /// A process may stop after Telegram accepts a message but before the message id is saved. Automatically retrying
    /// that row would risk customer spam, so lease expiry is permanently fail-closed until an administrator explicitly
    /// changes the row.
    /// </remarks>
    private async Task MarkExpiredClaimsUncertainAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var expiredCount = await context.PaymentSettlementNotifications
            .Where(row => row.Status == PaymentSettlementNotificationStatuses.Processing &&
                          row.LeaseUntilUtc.HasValue &&
                          row.LeaseUntilUtc.Value <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, PaymentSettlementNotificationStatuses.DeliveryUncertain)
                    .SetProperty(row => row.LastError, "processing_lease_expired_after_possible_delivery")
                    .SetProperty(row => row.ClaimToken, (string)null)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(row => row.NextAttemptAtUtc, (DateTime?)null)
                    .SetProperty(row => row.UpdatedAtUtc, now),
                cancellationToken);

        if (expiredCount > 0)
        {
            _logger.LogError(
                "Payment settlement notifications became delivery-uncertain after abandoned claims. count={Count}",
                expiredCount);
        }
    }

    /// <summary>
    /// Atomically claims a bounded snapshot of due pending notifications for this worker iteration.
    /// </summary>
    /// <param name="cancellationToken">Host token for users.db claim operations.</param>
    /// <returns>
    /// Detached claimed rows owned by unique claim tokens. The collection is empty when no notification is due.
    /// </returns>
    /// <remarks>
    /// Candidate reads may race across processes, but each conditional update requires the row to remain pending and
    /// due. Therefore only one worker can increment the attempt counter and acquire the two-minute lease.
    /// </remarks>
    private async Task<IReadOnlyList<PaymentSettlementNotification>> ClaimDueBatchAsync(
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var candidateIds = await context.PaymentSettlementNotifications
            .AsNoTracking()
            .Where(row => row.Status == PaymentSettlementNotificationStatuses.Pending &&
                          (!row.NextAttemptAtUtc.HasValue || row.NextAttemptAtUtc.Value <= now) &&
                          (!row.LeaseUntilUtc.HasValue || row.LeaseUntilUtc.Value <= now))
            .OrderBy(row => row.NextAttemptAtUtc)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(MaximumBatchSize)
            .ToListAsync(cancellationToken);

        var claimedRows = new List<PaymentSettlementNotification>(candidateIds.Count);
        foreach (var id in candidateIds)
        {
            var claimToken = Guid.NewGuid().ToString("N");
            var claimed = await context.PaymentSettlementNotifications
                .Where(row => row.Id == id &&
                              row.Status == PaymentSettlementNotificationStatuses.Pending &&
                              (!row.NextAttemptAtUtc.HasValue || row.NextAttemptAtUtc.Value <= now) &&
                              (!row.LeaseUntilUtc.HasValue || row.LeaseUntilUtc.Value <= now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, PaymentSettlementNotificationStatuses.Processing)
                        .SetProperty(row => row.AttemptCount, row => row.AttemptCount + 1)
                        .SetProperty(row => row.ClaimToken, claimToken)
                        .SetProperty(row => row.LeaseUntilUtc, now.Add(ClaimLease))
                        .SetProperty(row => row.UpdatedAtUtc, now),
                    cancellationToken);

            if (claimed != 1)
                continue;

            var row = await context.PaymentSettlementNotifications
                .AsNoTracking()
                .SingleAsync(item => item.Id == id && item.ClaimToken == claimToken, cancellationToken);
            claimedRows.Add(row);
        }

        return claimedRows;
    }

    /// <summary>
    /// Sends one claimed notification and persists a delivered, retryable, permanent, manual, or uncertain result.
    /// </summary>
    /// <param name="notification">Detached row whose processing claim belongs to this attempt.</param>
    /// <param name="cancellationToken">Host token for Telegram and users.db operations.</param>
    /// <returns>A task that completes after the claim reaches a durable state or shutdown interrupts delivery.</returns>
    /// <remarks>
    /// A Telegram message id is the success acknowledgement. If saving that acknowledgement fails, the worker makes
    /// one fresh-context attempt to mark delivery-uncertain and never deliberately resends the same message.
    /// </remarks>
    private async Task DeliverClaimAsync(
        PaymentSettlementNotification notification,
        CancellationToken cancellationToken)
    {
        if (notification.ChatId == 0 || string.IsNullOrWhiteSpace(notification.MessageText))
        {
            await MarkTerminalAsync(
                notification,
                PaymentSettlementNotificationStatuses.FailedPermanent,
                "invalid_notification_destination_or_text",
                cancellationToken);
            return;
        }

        try
        {
            var client = _botClientProvider.GetClient(notification.BotId);
            var sent = await client.SendTextMessageAsync(
                chatId: notification.ChatId,
                text: notification.MessageText,
                cancellationToken: cancellationToken);

            try
            {
                await MarkDeliveredAsync(notification, sent.MessageId, cancellationToken);
                _logger.LogDebug(
                    "Payment settlement notification delivered. notificationId={NotificationId}, provider={Provider}, attempt={Attempt}",
                    notification.Id,
                    notification.Provider,
                    notification.AttemptCount);
            }
            catch (Exception persistenceException)
            {
                await MarkDeliveryUncertainAfterAcknowledgementAsync(notification, persistenceException);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep the processing claim intact. Its lease will become delivery-uncertain after restart because the
            // cancellation may have raced with Telegram accepting the request.
            throw;
        }
        catch (Exception ex)
        {
            await HandleDeliveryFailureAsync(notification, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Persists Telegram acknowledgement for a claim only when its opaque token still matches.
    /// </summary>
    /// <param name="notification">Detached claimed notification.</param>
    /// <param name="telegramMessageId">Positive Telegram message id returned by the send call.</param>
    /// <param name="cancellationToken">Host token for the users.db update.</param>
    /// <returns>A task that completes after exactly one matching processing row becomes delivered.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the processing claim no longer matches one row.</exception>
    private async Task MarkDeliveredAsync(
        PaymentSettlementNotification notification,
        int telegramMessageId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var updated = await context.PaymentSettlementNotifications
            .Where(row => row.Id == notification.Id &&
                          row.Status == PaymentSettlementNotificationStatuses.Processing &&
                          row.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, PaymentSettlementNotificationStatuses.Delivered)
                    .SetProperty(row => row.TelegramMessageId, telegramMessageId)
                    .SetProperty(row => row.DeliveredAtUtc, now)
                    .SetProperty(row => row.UpdatedAtUtc, now)
                    .SetProperty(row => row.LastError, (string)null)
                    .SetProperty(row => row.ClaimToken, (string)null)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(row => row.NextAttemptAtUtc, (DateTime?)null),
                cancellationToken);

        if (updated != 1)
            throw new InvalidOperationException("The payment notification delivery claim no longer matches.");
    }

    /// <summary>
    /// Classifies one Telegram/network failure and applies bounded retry or a terminal delivery state.
    /// </summary>
    /// <param name="notification">Detached processing row whose attempt failed.</param>
    /// <param name="exception">Telegram or transport exception; its raw message is never persisted.</param>
    /// <param name="cancellationToken">Host token for the state transition.</param>
    /// <returns>A task that completes after the claim is released or quarantined.</returns>
    private async Task HandleDeliveryFailureAsync(
        PaymentSettlementNotification notification,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ApiRequestException { ErrorCode: 429 } rateLimited)
        {
            var retryAfterSeconds = rateLimited.Parameters?.RetryAfter ?? 0;
            var delay = retryAfterSeconds > 0
                ? TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, retryAfterSeconds + 1d))
                : CalculateRetryDelay(notification.AttemptCount);
            await RetryOrExhaustAsync(notification, delay, "telegram_rate_limited", cancellationToken);
            return;
        }

        if (exception is ApiRequestException apiException)
        {
            if (apiException.ErrorCode is 400 or 403)
            {
                await MarkTerminalAsync(
                    notification,
                    PaymentSettlementNotificationStatuses.FailedPermanent,
                    $"telegram_permanent_{apiException.ErrorCode}",
                    cancellationToken);
                return;
            }

            if (apiException.ErrorCode >= 500)
            {
                await RetryOrExhaustAsync(
                    notification,
                    CalculateRetryDelay(notification.AttemptCount),
                    $"telegram_transient_{apiException.ErrorCode}",
                    cancellationToken);
                return;
            }
        }

        if (exception is TaskCanceledException or TimeoutException or HttpRequestException or RequestException)
        {
            await RetryOrExhaustAsync(
                notification,
                CalculateRetryDelay(notification.AttemptCount),
                "telegram_transport_transient",
                cancellationToken);
            return;
        }

        await MarkTerminalAsync(
            notification,
            PaymentSettlementNotificationStatuses.ManualReview,
            "telegram_delivery_unknown",
            cancellationToken);
    }

    /// <summary>
    /// Releases a transiently failed claim with exponential backoff or stops after the sixth attempt.
    /// </summary>
    /// <param name="notification">Detached claimed notification whose attempt failed transiently.</param>
    /// <param name="delay">Delay before the next UTC attempt, already bounded to six hours.</param>
    /// <param name="sanitizedError">Credential-free error category persisted for operations.</param>
    /// <param name="cancellationToken">Host token for the users.db update.</param>
    /// <returns>A task that completes after retry or manual-review state is persisted.</returns>
    private async Task RetryOrExhaustAsync(
        PaymentSettlementNotification notification,
        TimeSpan delay,
        string sanitizedError,
        CancellationToken cancellationToken)
    {
        if (notification.AttemptCount >= MaximumAttempts)
        {
            await MarkTerminalAsync(
                notification,
                PaymentSettlementNotificationStatuses.ManualReview,
                "telegram_retry_exhausted:" + sanitizedError,
                cancellationToken);
            _logger.LogWarning(
                "Payment settlement notification retry budget exhausted. notificationId={NotificationId}, provider={Provider}, attempts={Attempts}",
                notification.Id,
                notification.Provider,
                notification.AttemptCount);
            return;
        }

        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        await context.PaymentSettlementNotifications
            .Where(row => row.Id == notification.Id &&
                          row.Status == PaymentSettlementNotificationStatuses.Processing &&
                          row.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, PaymentSettlementNotificationStatuses.Pending)
                    .SetProperty(row => row.NextAttemptAtUtc, now.Add(delay))
                    .SetProperty(row => row.LastError, sanitizedError)
                    .SetProperty(row => row.ClaimToken, (string)null)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(row => row.UpdatedAtUtc, now),
                cancellationToken);

        _logger.LogDebug(
            "Payment settlement notification scheduled for retry. notificationId={NotificationId}, provider={Provider}, attempt={Attempt}, delaySeconds={DelaySeconds}",
            notification.Id,
            notification.Provider,
            notification.AttemptCount,
            delay.TotalSeconds);
    }

    /// <summary>
    /// Moves one matching claim to a non-retryable terminal state.
    /// </summary>
    /// <param name="notification">Detached notification carrying the active claim token.</param>
    /// <param name="status">Failed-permanent, manual-review, or delivery-uncertain target status.</param>
    /// <param name="sanitizedError">Credential-free operational reason.</param>
    /// <param name="cancellationToken">Token for the users.db update.</param>
    /// <returns>A task that completes after the claim is cleared and automatic delivery is disabled.</returns>
    private async Task MarkTerminalAsync(
        PaymentSettlementNotification notification,
        string status,
        string sanitizedError,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var context = _contextFactory.CreateDbContext();
        var updated = await context.PaymentSettlementNotifications
            .Where(row => row.Id == notification.Id &&
                          row.Status == PaymentSettlementNotificationStatuses.Processing &&
                          row.ClaimToken == notification.ClaimToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, status)
                    .SetProperty(row => row.LastError, sanitizedError)
                    .SetProperty(row => row.ClaimToken, (string)null)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(row => row.NextAttemptAtUtc, (DateTime?)null)
                    .SetProperty(row => row.UpdatedAtUtc, now),
                cancellationToken);

        if (updated == 1)
        {
            _logger.LogWarning(
                "Payment settlement notification delivery stopped. notificationId={NotificationId}, provider={Provider}, status={Status}, reason={Reason}",
                notification.Id,
                notification.Provider,
                status,
                sanitizedError);
        }
    }

    /// <summary>
    /// Marks a Telegram-acknowledged message as delivery-uncertain when durable acknowledgement cannot be saved.
    /// </summary>
    /// <param name="notification">Detached row whose Telegram send returned a message id.</param>
    /// <param name="persistenceException">Persistence failure logged locally without notification text or secrets.</param>
    /// <returns>A task that completes after one best-effort fresh-context quarantine attempt.</returns>
    /// <remarks>
    /// The supplied exception is never written to the outbox row. If the quarantine update also fails, the processing
    /// lease is left intact so the next worker start converts it to delivery-uncertain rather than resending it.
    /// </remarks>
    private async Task MarkDeliveryUncertainAfterAcknowledgementAsync(
        PaymentSettlementNotification notification,
        Exception persistenceException)
    {
        try
        {
            await MarkTerminalAsync(
                notification,
                PaymentSettlementNotificationStatuses.DeliveryUncertain,
                "telegram_acknowledged_but_persistence_failed",
                CancellationToken.None);
        }
        catch (Exception quarantineException)
        {
            _logger.LogError(
                quarantineException,
                "Payment notification acknowledgement and uncertainty persistence both failed. notificationId={NotificationId}, provider={Provider}",
                notification.Id,
                notification.Provider);
        }

        _logger.LogError(
            persistenceException,
            "Payment notification was accepted by Telegram but acknowledgement persistence failed; automatic resend is disabled. notificationId={NotificationId}, provider={Provider}",
            notification.Id,
            notification.Provider);
    }

    /// <summary>
    /// Calculates the bounded retry delay for a one-based delivery attempt number.
    /// </summary>
    /// <param name="attemptCount">Persisted one-based attempt count, normally between one and six.</param>
    /// <returns>Thirty seconds multiplied by four per prior attempt, capped at six hours.</returns>
    /// <example>
    /// Attempt one returns 30 seconds; attempt two returns 120 seconds.
    /// </example>
    private static TimeSpan CalculateRetryDelay(int attemptCount)
    {
        var exponent = Math.Max(0, attemptCount - 1);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(4d, exponent);
        return TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, seconds));
    }
}
