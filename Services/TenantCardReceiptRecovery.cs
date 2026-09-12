using System.Globalization;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Adminbot.Services;

/// <summary>
/// Normalized operator filters for the missed tenant card-to-card receipt recovery command.
/// </summary>
/// <remarks>
/// Every filter is optional and read-only except <see cref="Apply" />. The recovery window bounds the incident
/// population: after the receipt-ingestion fix a perfectly healthy card customer who selected card payment but has not
/// paid yet is also <c>awaiting_receipt</c> with no receipt row, so a mutation is only permitted with an explicit
/// <see cref="UntilUtc" /> cutoff.
/// </remarks>
public sealed class TenantCardReceiptRecoveryOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the command is allowed to persist recovery intents.
    /// </summary>
    /// <remarks>False is a fully read-only dry run and is the default for the command.</remarks>
    public bool Apply { get; set; }

    /// <summary>
    /// Gets or sets the inclusive exclusive-side bound of the recovery window applied to
    /// <see cref="TenantBotOrder.CreatedAtUtc" />.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="Apply" /> is true. The operator supplies the pre-fix deployment instant, not this
    /// command; the commit timestamp of the receipt fix is never used as a proxy for the production deployment time.
    /// </remarks>
    public DateTime? UntilUtc { get; set; }

    /// <summary>Gets or sets the optional inclusive lower bound of the recovery window.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>
    /// Gets or sets an optional "must be at least this stale" filter in minutes before the scan ran.
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="UntilUtc" /> as an intersection when both are supplied. Must be greater than zero.
    /// </remarks>
    public int? OlderThanMinutes { get; set; }

    /// <summary>Gets or sets an optional exact tenant bot id filter. Global when null.</summary>
    public string TenantBotId { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of eligible orders listed and enqueued by this invocation.
    /// </summary>
    /// <remarks>When null the service applies <see cref="TenantCardReceiptRecoveryService.DefaultLimit" />.</remarks>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets an explicit <c>users.db</c> path, used to scan a copy or a non-standard installation.
    /// </summary>
    /// <remarks>
    /// When null the path is read from <c>./Data/configuration.json</c> relative to the current directory and resolved
    /// exactly like application startup.
    /// </remarks>
    public string UsersDatabasePath { get; set; }
}

/// <summary>
/// One tenant order that qualifies for an automatic receipt re-upload reminder.
/// </summary>
/// <remarks>Contains only non-secret identifiers; it never carries a Telegram file id, token, or card data.</remarks>
public sealed class TenantCardReceiptRecoveryCandidate
{
    /// <summary>Gets or sets the internal users.db id of the tenant order, used by the re-upload callback target.</summary>
    public int OrderDbId { get; set; }

    /// <summary>Gets or sets the customer-facing order number.</summary>
    public string OrderId { get; set; }

    /// <summary>Gets or sets the tenant storefront bot id that must send the reminder.</summary>
    public string TenantBotId { get; set; }

    /// <summary>Gets or sets the customer Telegram user id that owns the order.</summary>
    public long CustomerTelegramUserId { get; set; }

    /// <summary>Gets or sets the UTC creation time used to bound the incident window.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the order kind (<c>purchase</c> or <c>renew</c>).</summary>
    public string OrderKind { get; set; }

    /// <summary>Gets or sets the current payment status, which must still be <c>awaiting_receipt</c> to qualify.</summary>
    public string PaymentStatus { get; set; }
}

/// <summary>
/// Diagnostic view of an order that has a receipt but whose owner notification did not complete.
/// </summary>
/// <remarks>
/// These orders are the second, different incident: the receipt exists, so the customer must never be asked to upload
/// it again. Only an operator can recover the owner relay. The Telegram file id is deliberately not exposed.
/// </remarks>
public sealed class TenantCardReceiptDeliveryDiagnostic
{
    /// <summary>Gets or sets the internal users.db receipt id.</summary>
    public int ReceiptId { get; set; }

    /// <summary>Gets or sets the internal users.db order id the receipt belongs to.</summary>
    public int OrderDbId { get; set; }

    /// <summary>Gets or sets the customer-facing order number.</summary>
    public string OrderId { get; set; }

    /// <summary>Gets or sets the tenant storefront bot id that owns the receipt.</summary>
    public string TenantBotId { get; set; }

    /// <summary>Gets or sets the receipt review status (<c>pending</c>, <c>approved</c>, or <c>rejected</c>).</summary>
    public string ReceiptStatus { get; set; }

    /// <summary>Gets or sets the owner relay status, or null when no notification row exists at all.</summary>
    public string NotificationStatus { get; set; }

    /// <summary>Gets or sets the number of owner relay attempts already made.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets the last safe failure code recorded for the relay, never a response body.</summary>
    public string LastError { get; set; }
}

/// <summary>
/// Read-only result of one missed-receipt recovery scan.
/// </summary>
public sealed class TenantCardReceiptRecoveryScan
{
    /// <summary>Gets or sets the effective inclusive lower bound of the recovery window.</summary>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>Gets or sets the effective inclusive upper bound of the recovery window.</summary>
    public DateTime WindowEndUtc { get; set; }

    /// <summary>Gets or sets the eligible orders this invocation may list and enqueue.</summary>
    public IReadOnlyList<TenantCardReceiptRecoveryCandidate> Candidates { get; set; } = Array.Empty<TenantCardReceiptRecoveryCandidate>();

    /// <summary>Gets or sets the receipts whose owner relay needs operator attention.</summary>
    public IReadOnlyList<TenantCardReceiptDeliveryDiagnostic> ReceiptDeliveryDiagnostics { get; set; } =
        Array.Empty<TenantCardReceiptDeliveryDiagnostic>();

    /// <summary>Gets or sets the total eligible order count before the limit was applied.</summary>
    public long Eligible { get; set; }

    /// <summary>Gets or sets the orders that already carry a recovery intent for this kind.</summary>
    public long AlreadyRecoveryQueued { get; set; }

    /// <summary>Gets or sets the orders that already have a receipt, so the customer must not be asked again.</summary>
    public long ExistingReceipt { get; set; }

    /// <summary>Gets or sets the receipts whose owner relay needs review (missing, uncertain, or failed).</summary>
    public long ReceiptDeliveryNeedsReview { get; set; }

    /// <summary>Gets or sets the orders rejected because the customer identity is not usable.</summary>
    public long InvalidCustomer { get; set; }

    /// <summary>Gets or sets the orders rejected because the tenant bot identity is missing.</summary>
    public long InvalidTenant { get; set; }

    /// <summary>Gets or sets the orders whose receipt row is not linked from <c>ManualReceiptId</c>.</summary>
    public long ReceiptLinkInconsistent { get; set; }

    /// <summary>Gets or sets eligible orders whose tenant storefront currently has no usable transport.</summary>
    public long TransportUnavailable { get; set; }

    /// <summary>Gets or sets the eligible orders skipped because of the configured limit.</summary>
    public long LimitApplied { get; set; }
}

/// <summary>
/// Finds and queues the customer receipt re-upload reminder for tenant card-to-card orders whose receipt image was
/// dropped by the pre-fix Telegram document handling.
/// </summary>
/// <remarks>
/// <para>
/// The service never fabricates a receipt and never advances payment state. A dropped Telegram image cannot be
/// reconstructed: the durable update inbox erases terminal private payloads and activity logs only record coarse
/// markers such as <c>[Document]</c>. Recovery therefore means asking the customer to send the image again, and the
/// only durable effect of this service is a <see cref="TenantOrderNotification" /> intent of kind
/// <see cref="TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery" /> that the existing
/// <see cref="TenantOrderNotificationWorker" /> delivers through the exact tenant bot.
/// </para>
/// <para>
/// <see cref="ScanAsync" /> is completely read-only. <see cref="ApplyAsync" /> re-reads every candidate under a short
/// transaction and relies on the existing unique (order, kind) index as the final idempotency guard, so repeated or
/// concurrent invocations cannot produce more than one reminder per order.
/// </para>
/// </remarks>
public sealed class TenantCardReceiptRecoveryService
{
    /// <summary>Provider value that marks a card-to-card (manual) tenant payment.</summary>
    private const string TenantCardProvider = "tenant_card";

    /// <summary>Default maximum number of orders listed and enqueued by one invocation.</summary>
    public const int DefaultLimit = 500;

    /// <summary>Hard upper bound for <c>--limit</c> so one invocation cannot exhaust the database.</summary>
    public const int MaximumLimit = 100000;

    private readonly UserDbContextFactory _contextFactory;

    /// <summary>
    /// Creates the recovery service over the shared users.db factory.
    /// </summary>
    /// <param name="contextFactory">
    /// Required factory for the runtime <c>users.db</c>. The service only creates short-lived contexts and never holds
    /// one open across the whole scan, so it is safe to run while the application owns the same file.
    /// </param>
    public TenantCardReceiptRecoveryService(UserDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>
    /// Scans users.db for card-to-card orders that need an automatic receipt re-upload reminder.
    /// </summary>
    /// <param name="options">
    /// Operator filters. <see cref="TenantCardReceiptRecoveryOptions.Apply" /> is ignored here; the scan itself never
    /// writes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the read-only database work.</param>
    /// <returns>
    /// A snapshot containing the effective window, the bounded candidate list, the classification counters, and the
    /// orders whose existing receipt was not relayed to the owner. No entity is tracked and nothing is written.
    /// </returns>
    /// <remarks>
    /// An order qualifies only when it is card-to-card, still <c>awaiting_receipt</c>, not fulfilled, has no
    /// <c>ManualReceiptId</c>, has no receipt row at all, has a positive customer Telegram id, has a tenant bot id, and
    /// falls inside the window. An existing receipt always wins over every other classification, so a customer who
    /// already uploaded one is never asked to upload it again.
    /// </remarks>
    /// <example>
    /// <code>
    /// var scan = await service.ScanAsync(new TenantCardReceiptRecoveryOptions
    /// {
    ///     UntilUtc = DateTime.Parse("2026-09-12T02:30:00Z", null, DateTimeStyles.AdjustToUniversal)
    /// }, cancellationToken);
    /// </code>
    /// </example>
    public async Task<TenantCardReceiptRecoveryScan> ScanAsync(
        TenantCardReceiptRecoveryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var db = _contextFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var windowEnd = ResolveWindowEnd(options, now);
        var windowStart = options.SinceUtc ?? DateTime.MinValue;
        var limit = Math.Clamp(options.Limit ?? DefaultLimit, 1, MaximumLimit);

        // The base population is every card-to-card order that still looks unpaid inside the operator window. Orders
        // that already have a receipt stay in this set so the summary can distinguish "no receipt was persisted" from
        // "the receipt exists but the owner relay did not finish"; only the former is eligible.
        var query = db.TenantBotOrders.AsNoTracking()
            .Where(x => x.PaymentProvider != null && x.PaymentProvider.ToLower() == TenantCardProvider)
            .Where(x => x.PaymentStatus == TenantBotOrderStatuses.AwaitingReceipt)
            .Where(x => !x.IsFulfilled)
            .Where(x => x.CreatedAtUtc <= windowEnd)
            .Where(x => x.CreatedAtUtc >= windowStart);
        if (!string.IsNullOrWhiteSpace(options.TenantBotId))
            query = query.Where(x => x.TenantBotId == options.TenantBotId);

        var rows = await query
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new RecoveryRow
            {
                OrderDbId = x.Id,
                OrderId = x.OrderId,
                TenantBotId = x.TenantBotId,
                CustomerTelegramUserId = x.CustomerTelegramUserId,
                CreatedAtUtc = x.CreatedAtUtc,
                OrderKind = x.OrderKind,
                PaymentStatus = x.PaymentStatus,
                ManualReceiptId = x.ManualReceiptId
            })
            .ToListAsync(cancellationToken);

        var orderIds = rows.Select(x => x.OrderDbId).ToList();
        var receipts = orderIds.Count == 0
            ? new List<ReceiptRow>()
            : await db.TenantManualPaymentReceipts.AsNoTracking()
                .Where(x => orderIds.Contains(x.TenantBotOrderId))
                .Select(x => new ReceiptRow { ReceiptId = x.Id, OrderDbId = x.TenantBotOrderId, Status = x.Status })
                .ToListAsync(cancellationToken);
        var receiptsByOrder = receipts.GroupBy(x => x.OrderDbId).ToDictionary(x => x.Key, x => x.First());
        var receiptIds = receipts.Select(x => x.ReceiptId).ToList();
        var relays = receiptIds.Count == 0
            ? new List<RelayRow>()
            : await db.TenantManualReceiptNotifications.AsNoTracking()
                .Where(x => receiptIds.Contains(x.ReceiptId))
                .Select(x => new RelayRow
                {
                    ReceiptId = x.ReceiptId,
                    Status = x.Status,
                    AttemptCount = x.AttemptCount,
                    LastError = x.LastError
                })
                .ToListAsync(cancellationToken);
        var relaysByReceipt = relays.GroupBy(x => x.ReceiptId).ToDictionary(x => x.Key, x => x.First());
        var alreadyQueued = orderIds.Count == 0
            ? new HashSet<int>()
            : (await db.TenantOrderNotifications.AsNoTracking()
                .Where(x => orderIds.Contains(x.TenantBotOrderId) &&
                            x.Kind == TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery)
                .Select(x => x.TenantBotOrderId)
                .ToListAsync(cancellationToken)).ToHashSet();
        var usableBotIds = new HashSet<string>(
            await db.BotInstances.AsNoTracking()
                .Where(x => x.Type == BotInstanceTypes.Tenant && x.Enabled &&
                            x.Token != null && x.Token != "")
                .Select(x => x.Id)
                .ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        var scan = new TenantCardReceiptRecoveryScan { WindowStartUtc = windowStart, WindowEndUtc = windowEnd };
        var candidates = new List<TenantCardReceiptRecoveryCandidate>();
        var diagnostics = new List<TenantCardReceiptDeliveryDiagnostic>();

        foreach (var row in rows)
        {
            receiptsByOrder.TryGetValue(row.OrderDbId, out var receipt);
            var hasReceipt = row.ManualReceiptId.HasValue || receipt != null;
            if (hasReceipt)
            {
                // Existing receipt always wins. The receipt is already in the system, so the customer flow is not the
                // problem here; report the owner relay instead of asking for another upload.
                scan.ExistingReceipt++;
                if (receipt != null)
                {
                    if (!row.ManualReceiptId.HasValue)
                        scan.ReceiptLinkInconsistent++;
                    relaysByReceipt.TryGetValue(receipt.ReceiptId, out var relay);
                    diagnostics.Add(new TenantCardReceiptDeliveryDiagnostic
                    {
                        ReceiptId = receipt.ReceiptId,
                        OrderDbId = row.OrderDbId,
                        OrderId = row.OrderId,
                        TenantBotId = row.TenantBotId,
                        ReceiptStatus = receipt.Status,
                        NotificationStatus = relay?.Status,
                        AttemptCount = relay?.AttemptCount ?? 0,
                        LastError = relay?.LastError
                    });
                    if (relay == null ||
                        relay.Status is TenantManualReceiptNotificationStatuses.DeliveryUncertain
                            or TenantManualReceiptNotificationStatuses.ManualReview
                            or TenantManualReceiptNotificationStatuses.FailedPermanent)
                        scan.ReceiptDeliveryNeedsReview++;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.TenantBotId))
            {
                scan.InvalidTenant++;
                continue;
            }

            if (row.CustomerTelegramUserId <= 0)
            {
                scan.InvalidCustomer++;
                continue;
            }

            if (alreadyQueued.Contains(row.OrderDbId))
            {
                scan.AlreadyRecoveryQueued++;
                continue;
            }

            if (!usableBotIds.Contains(row.TenantBotId))
                scan.TransportUnavailable++;

            scan.Eligible++;
            if (candidates.Count >= limit)
            {
                scan.LimitApplied++;
                continue;
            }

            candidates.Add(new TenantCardReceiptRecoveryCandidate
            {
                OrderDbId = row.OrderDbId,
                OrderId = row.OrderId,
                TenantBotId = row.TenantBotId,
                CustomerTelegramUserId = row.CustomerTelegramUserId,
                CreatedAtUtc = row.CreatedAtUtc,
                OrderKind = row.OrderKind,
                PaymentStatus = row.PaymentStatus
            });
        }

        scan.Candidates = candidates;
        scan.ReceiptDeliveryDiagnostics = diagnostics;
        return scan;
    }

    /// <summary>
    /// Persists one durable recovery reminder intent for each candidate that is still eligible.
    /// </summary>
    /// <param name="scan">
    /// Required result of <see cref="ScanAsync" />. Its fixed window is reused so the re-check cannot drift with the
    /// clock, and no earlier scan decision is trusted on its own.
    /// </param>
    /// <param name="options">Required operator filters; only the window and tenant filter are re-applied.</param>
    /// <param name="cancellationToken">Cancellation token for the short per-order transactions.</param>
    /// <returns>
    /// The number of newly inserted recovery intents. Orders that already had an intent, received a receipt, or changed
    /// state since the scan are skipped and are not counted.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Each candidate is re-read immediately before insertion inside its own transaction, so a receipt that arrived
    /// between the scan and the apply can never produce a reminder. The unique (TenantBotOrderId, Kind) index is the
    /// final guard: a concurrently inserted row raises a duplicate-key failure that is treated as already queued rather
    /// than as a recovery error.
    /// </para>
    /// <para>
    /// No receipt, payment, wallet, ledger, order, or XUI state is ever written by this method. The automatic
    /// 1 GiB / 1 day courtesy account, if the tenant card provisional feature is enabled, is created only later by the
    /// normal receipt flow after the customer actually re-uploads the image.
    /// </para>
    /// </remarks>
    public async Task<int> ApplyAsync(
        TenantCardReceiptRecoveryScan scan,
        TenantCardReceiptRecoveryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(options);
        var enqueued = 0;
        foreach (var candidate in scan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enqueued += await SqliteOperation.RunAsync(async token =>
            {
                await using var db = _contextFactory.CreateDbContext();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var order = await db.TenantBotOrders.FirstOrDefaultAsync(x => x.Id == candidate.OrderDbId, token);
                if (order == null || !IsStillEligible(order, options, scan.WindowEndUtc))
                    return 0;

                if (await db.TenantManualPaymentReceipts.AnyAsync(x => x.TenantBotOrderId == order.Id, token))
                    return 0;

                if (await db.TenantOrderNotifications.AnyAsync(x =>
                        x.TenantBotOrderId == order.Id &&
                        x.Kind == TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery, token))
                    return 0;

                var now = DateTime.UtcNow;
                db.TenantOrderNotifications.Add(new TenantOrderNotification
                {
                    TenantBotOrderId = order.Id,
                    Kind = TenantOrderNotificationKinds.TenantCardReceiptReuploadRecovery,
                    Status = TenantOrderNotificationStatuses.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                try
                {
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    return 1;
                }
                catch (DbUpdateException ex) when (!SqliteOperation.IsBusy(ex))
                {
                    // The unique (TenantBotOrderId, Kind) index decided this race. Another operator or process already
                    // queued the same reminder, which is the desired end state, not a recovery failure. Contention is
                    // deliberately excluded so it still flows through the shared SQLite retry policy.
                    return 0;
                }
            }, cancellationToken);
        }

        return enqueued;
    }

    /// <summary>
    /// Re-applies every candidate condition to a freshly loaded order.
    /// </summary>
    /// <param name="order">Tracked order reloaded inside the apply transaction.</param>
    /// <param name="options">Operator filters that must still match.</param>
    /// <param name="windowEndUtc">Fixed window end captured by the scan.</param>
    /// <returns><c>true</c> only while the order still looks like a dropped-receipt incident.</returns>
    /// <remarks>
    /// The receipt row check is deliberately performed by the caller, because it needs a second query that must run
    /// inside the same transaction.
    /// </remarks>
    private static bool IsStillEligible(TenantBotOrder order, TenantCardReceiptRecoveryOptions options, DateTime windowEndUtc) =>
        string.Equals(order.PaymentProvider, TenantCardProvider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(order.PaymentStatus, TenantBotOrderStatuses.AwaitingReceipt, StringComparison.Ordinal) &&
        !order.IsFulfilled &&
        !order.ManualReceiptId.HasValue &&
        order.CustomerTelegramUserId > 0 &&
        !string.IsNullOrWhiteSpace(order.TenantBotId) &&
        order.CreatedAtUtc <= windowEndUtc &&
        (!options.SinceUtc.HasValue || order.CreatedAtUtc >= options.SinceUtc.Value) &&
        (string.IsNullOrWhiteSpace(options.TenantBotId) ||
         string.Equals(order.TenantBotId, options.TenantBotId, StringComparison.Ordinal));

    /// <summary>
    /// Resolves the effective inclusive upper bound of the recovery window.
    /// </summary>
    /// <param name="options">Operator filters.</param>
    /// <param name="now">Current UTC instant used only for the relative filter.</param>
    /// <returns>
    /// The smaller of the explicit <c>--until</c> bound and the relative <c>--older-than-minutes</c> bound, or the
    /// current instant when neither was supplied.
    /// </returns>
    /// <remarks>
    /// Combining both filters as an intersection keeps a mistyped relative filter from widening the window beyond what
    /// the operator explicitly authorized.
    /// </remarks>
    private static DateTime ResolveWindowEnd(TenantCardReceiptRecoveryOptions options, DateTime now)
    {
        var end = options.UntilUtc ?? now;
        if (options.OlderThanMinutes is > 0)
            end = end < now.AddMinutes(-options.OlderThanMinutes.Value) ? end : now.AddMinutes(-options.OlderThanMinutes.Value);
        return end;
    }

    /// <summary>Projection of one in-window card order; never tracked and never contains a receipt file id.</summary>
    private sealed class RecoveryRow
    {
        public int OrderDbId { get; init; }
        public string OrderId { get; init; }
        public string TenantBotId { get; init; }
        public long CustomerTelegramUserId { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string OrderKind { get; init; }
        public string PaymentStatus { get; init; }
        public int? ManualReceiptId { get; init; }
    }

    /// <summary>Minimal receipt projection used for existence, linkage, and relay diagnostics.</summary>
    private sealed class ReceiptRow
    {
        public int ReceiptId { get; init; }
        public int OrderDbId { get; init; }
        public string Status { get; init; }
    }

    /// <summary>Minimal owner-relay projection; the Telegram message id is deliberately not read.</summary>
    private sealed class RelayRow
    {
        public int ReceiptId { get; init; }
        public string Status { get; init; }
        public int AttemptCount { get; init; }
        public string LastError { get; init; }
    }
}

/// <summary>
/// Non-serving command-line entry point for the missed tenant card-to-card receipt recovery.
/// </summary>
/// <remarks>
/// Like <see cref="MigrationPreflight" />, this mode exits before host construction and starts no web listener,
/// Telegram receiver, hosted worker, provider reconciliation, XUI worker, or Telegram logging worker. Dry run needs
/// only users.db reads; apply only inserts durable reminder intents. The command never calls Telegram or XUI itself.
/// </remarks>
public static class TenantCardReceiptRecoveryCli
{
    /// <summary>Exact switch that selects the recovery mode.</summary>
    public const string Mode = "--recover-missed-tenant-card-receipts";

    private const string ApplySwitch = "--apply";
    private const string UntilOption = "--until";
    private const string SinceOption = "--since";
    private const string OlderThanOption = "--older-than-minutes";
    private const string TenantBotOption = "--tenant-bot-id";
    private const string LimitOption = "--limit";
    private const string UsersDbOption = "--users-db";

    /// <summary>Exit code returned when the arguments are invalid; no database is opened.</summary>
    private const int InvalidArgumentsExitCode = 2;

    /// <summary>Exit code returned when the users database cannot be resolved or the run failed.</summary>
    private const int FailureExitCode = 1;

    /// <summary>
    /// Determines whether the command line selects the non-serving receipt recovery mode.
    /// </summary>
    /// <param name="args">Raw application arguments; null or empty means normal serving mode.</param>
    /// <returns><c>true</c> only when the exact recovery switch is present.</returns>
    public static bool IsRequested(IReadOnlyCollection<string> args) =>
        args?.Contains(Mode, StringComparer.Ordinal) == true;

    /// <summary>
    /// Parses and validates the recovery command line without touching the database.
    /// </summary>
    /// <param name="args">Raw application arguments including <see cref="Mode" />.</param>
    /// <param name="options">Receives the normalized filters when parsing succeeds.</param>
    /// <param name="error">Receives a stable, secret-free failure code when parsing fails.</param>
    /// <returns><c>true</c> when every argument is supported and consistent; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// <c>--apply</c> is refused unless an explicit <c>--until</c> cutoff is supplied, because
    /// <c>awaiting_receipt</c> alone never proves payment and a healthy customer may legitimately be waiting to pay.
    /// Duplicate options, missing values, non-UTC timestamps, and non-positive counts are all refused.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (!TenantCardReceiptRecoveryCli.TryParse(args, out var options, out var error)) return 2;
    /// </code>
    /// </example>
    internal static bool TryParse(
        IReadOnlyList<string> args,
        out TenantCardReceiptRecoveryOptions options,
        out string error)
    {
        // Parsing writes into a local so a refused command line can never leak a partially enabled option to the
        // caller. Only a fully validated result is published through the out parameter.
        var parsed = new TenantCardReceiptRecoveryOptions();
        options = new TenantCardReceiptRecoveryOptions();
        error = null;
        if (args == null)
        {
            error = "missing_arguments";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, Mode, StringComparison.Ordinal))
                continue;

            if (string.Equals(argument, ApplySwitch, StringComparison.Ordinal))
            {
                if (!seen.Add(argument))
                {
                    error = "duplicate_option";
                    return false;
                }

                parsed.Apply = true;
                continue;
            }

            if (argument is UntilOption or SinceOption or OlderThanOption or TenantBotOption or LimitOption or UsersDbOption)
            {
                if (!seen.Add(argument))
                {
                    error = "duplicate_option";
                    return false;
                }

                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) ||
                    args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "missing_option_value";
                    return false;
                }

                var value = args[index + 1];
                index++;
                if (argument == UntilOption)
                {
                    if (!TryParseUtc(value, out var until))
                    {
                        error = "invalid_until";
                        return false;
                    }

                    parsed.UntilUtc = until;
                }
                else if (argument == SinceOption)
                {
                    if (!TryParseUtc(value, out var since))
                    {
                        error = "invalid_since";
                        return false;
                    }

                    parsed.SinceUtc = since;
                }
                else if (argument == OlderThanOption)
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var olderThan) ||
                        olderThan <= 0)
                    {
                        error = "invalid_older_than_minutes";
                        return false;
                    }

                    parsed.OlderThanMinutes = olderThan;
                }
                else if (argument == LimitOption)
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ||
                        limit <= 0 || limit > TenantCardReceiptRecoveryService.MaximumLimit)
                    {
                        error = "invalid_limit";
                        return false;
                    }

                    parsed.Limit = limit;
                }
                else if (argument == TenantBotOption)
                {
                    parsed.TenantBotId = value;
                }
                else
                {
                    parsed.UsersDatabasePath = value;
                }

                continue;
            }

            error = "unknown_argument";
            return false;
        }

        if (parsed.SinceUtc.HasValue && parsed.UntilUtc.HasValue && parsed.SinceUtc > parsed.UntilUtc)
        {
            error = "since_after_until";
            return false;
        }

        // Mutation is only ever allowed inside an operator-declared incident window. Without a cutoff the command would
        // also target ordinary customers who simply have not paid yet, which is not an incident.
        if (parsed.Apply && !parsed.UntilUtc.HasValue)
        {
            error = "apply_requires_until";
            return false;
        }

        options = parsed;
        return true;
    }

    /// <summary>
    /// Runs the recovery scan and, only with <c>--apply</c>, enqueues the durable reminder intents.
    /// </summary>
    /// <param name="args">Raw application arguments including <see cref="Mode" />.</param>
    /// <param name="output">Non-secret operator output writer.</param>
    /// <param name="cancellationToken">Cancellation token for database work.</param>
    /// <returns>
    /// Zero when the requested mode completed, <see cref="InvalidArgumentsExitCode" /> for refused arguments, and
    /// <see cref="FailureExitCode" /> when the users database is missing or the run failed.
    /// </returns>
    /// <remarks>
    /// Nothing is sent to Telegram and nothing is provisioned. The printed rows contain only internal ids, the order
    /// number, the tenant bot id, the customer Telegram id, and status codes.
    /// </remarks>
    /// <example><code>./Adminbot --recover-missed-tenant-card-receipts --until 2026-09-12T02:30:00Z --apply</code></example>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!TryParse(args, out var options, out var error))
        {
            await output.WriteLineAsync($"Missed receipt recovery: INVALID_ARGUMENTS ({error})");
            return InvalidArgumentsExitCode;
        }

        string databasePath;
        try
        {
            databasePath = ResolveUsersDatabasePath(options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync("Missed receipt recovery: USERS_DATABASE_NOT_FOUND");
            return FailureExitCode;
        }

        try
        {
            var factory = new UserDbContextFactory(
                new DbContextOptionsBuilder<UserDbContext>()
                    .UseSqlite(SqliteOperation.ConnectionString(databasePath))
                    .Options);
            var service = new TenantCardReceiptRecoveryService(factory);
            var scan = await service.ScanAsync(options, cancellationToken);
            await WriteReportAsync(output, options, databasePath, scan);
            if (!options.Apply)
                return 0;

            var enqueued = await service.ApplyAsync(scan, options, cancellationToken);
            await output.WriteLineAsync($"Enqueued: {enqueued}");
            await output.WriteLineAsync("No receipt, payment, wallet, ledger, order, or XUI state was modified.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Missed receipt recovery failed: {ex.GetType().Name}");
            return FailureExitCode;
        }
    }

    /// <summary>
    /// Writes the safe dry-run or apply summary, the bounded candidate rows, and the owner-relay diagnostics.
    /// </summary>
    /// <param name="output">Operator output writer.</param>
    /// <param name="options">Normalized filters that were used.</param>
    /// <param name="databasePath">Resolved users.db path, printed like application startup prints it.</param>
    /// <param name="scan">Completed read-only scan.</param>
    /// <returns>A task completing after every line is written.</returns>
    private static async Task WriteReportAsync(
        TextWriter output,
        TenantCardReceiptRecoveryOptions options,
        string databasePath,
        TenantCardReceiptRecoveryScan scan)
    {
        await output.WriteLineAsync($"Mode: {(options.Apply ? "APPLY" : "DRY-RUN")}");
        await output.WriteLineAsync($"UsersDatabase: {databasePath}");
        await output.WriteLineAsync($"WindowStartUtc: {Iso(scan.WindowStartUtc)}");
        await output.WriteLineAsync($"WindowEndUtc: {Iso(scan.WindowEndUtc)}");
        await output.WriteLineAsync($"Eligible: {scan.Eligible}");
        await output.WriteLineAsync($"AlreadyRecoveryQueued: {scan.AlreadyRecoveryQueued}");
        await output.WriteLineAsync($"ExistingReceipt: {scan.ExistingReceipt}");
        await output.WriteLineAsync($"ReceiptDeliveryNeedsReview: {scan.ReceiptDeliveryNeedsReview}");
        await output.WriteLineAsync($"InvalidCustomer: {scan.InvalidCustomer}");
        await output.WriteLineAsync($"InvalidTenant: {scan.InvalidTenant}");
        await output.WriteLineAsync($"ReceiptLinkInconsistent: {scan.ReceiptLinkInconsistent}");
        await output.WriteLineAsync($"TransportUnavailable: {scan.TransportUnavailable}");
        await output.WriteLineAsync($"LimitApplied: {scan.LimitApplied}");

        await output.WriteLineAsync("Candidates:");
        foreach (var candidate in scan.Candidates)
            await output.WriteLineAsync(
                $"  orderDbId={candidate.OrderDbId} orderId={candidate.OrderId} tenantBotId={candidate.TenantBotId} " +
                $"customerTelegramUserId={candidate.CustomerTelegramUserId} createdAtUtc={Iso(candidate.CreatedAtUtc)} " +
                $"orderKind={candidate.OrderKind} paymentStatus={candidate.PaymentStatus}");

        await output.WriteLineAsync("Receipt delivery diagnostics (operator review, no customer reminder):");
        foreach (var diagnostic in scan.ReceiptDeliveryDiagnostics)
            await output.WriteLineAsync(
                $"  receiptId={diagnostic.ReceiptId} orderDbId={diagnostic.OrderDbId} orderId={diagnostic.OrderId} " +
                $"tenantBotId={diagnostic.TenantBotId} receiptStatus={diagnostic.ReceiptStatus} " +
                $"notificationStatus={diagnostic.NotificationStatus ?? "missing"} " +
                $"attemptCount={diagnostic.AttemptCount} lastError={diagnostic.LastError ?? "-"}");
    }

    /// <summary>
    /// Resolves the users.db path either from an explicit option or exactly like application startup does.
    /// </summary>
    /// <param name="options">Parsed filters that may carry <c>--users-db</c>.</param>
    /// <returns>An absolute path to an existing users.db file.</returns>
    /// <exception cref="FileNotFoundException">No users.db exists at the resolved location.</exception>
    /// <remarks>
    /// The configured path is read from <c>./Data/configuration.json</c> relative to the current directory, matching
    /// the content root the serving application uses, so a command run from the install directory targets the same
    /// file. An explicit option makes it possible to dry-run against an offline copy.
    /// </remarks>
    private static string ResolveUsersDatabasePath(TenantCardReceiptRecoveryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.UsersDatabasePath))
        {
            var explicitPath = Path.GetFullPath(options.UsersDatabasePath);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException("The requested users database was not found.");
            return explicitPath;
        }

        var contentRoot = Directory.GetCurrentDirectory();
        var configured = ReadConfiguredUsersDatabasePath(contentRoot) ?? "./Data/users.db";
        var resolved = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(contentRoot, configured));
        if (!File.Exists(resolved))
            throw new FileNotFoundException("The resolved users database was not found.");
        return resolved;
    }

    /// <summary>
    /// Reads the configured users.db path from the private configuration file without exposing any other value.
    /// </summary>
    /// <param name="contentRoot">Directory the command was run from.</param>
    /// <returns>The configured relative or absolute path, or null when configuration is absent or unreadable.</returns>
    /// <remarks>Only <see cref="AppConfig.UserDatabasePath" /> is read; tokens, secrets, and bot configuration are ignored.</remarks>
    private static string ReadConfiguredUsersDatabasePath(string contentRoot)
    {
        var configurationPath = Path.Combine(contentRoot, "Data", "configuration.json");
        if (!File.Exists(configurationPath))
            return null;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(configurationPath, optional: true)
                .Build();
            var configured = configuration["UserDatabasePath"];
            return string.IsNullOrWhiteSpace(configured) ? null : configured;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a required UTC ISO-8601 timestamp, converting an offset-bearing value to UTC.
    /// </summary>
    /// <param name="value">Raw option value.</param>
    /// <param name="parsed">Receives the UTC instant when parsing succeeds.</param>
    /// <returns><c>true</c> when the value is a valid absolute timestamp.</returns>
    private static bool TryParseUtc(string value, out DateTime parsed)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var offset))
        {
            parsed = offset.UtcDateTime;
            return true;
        }

        parsed = default;
        return false;
    }

    /// <summary>Formats an instant as round-trip UTC for operator logs.</summary>
    /// <param name="value">Instant to format.</param>
    /// <returns>An ISO-8601 UTC string.</returns>
    private static string Iso(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
