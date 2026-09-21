using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Bridges tenant order identity to the existing global customer wallet and ledger without a cross-database transaction.</summary>
/// <remarks>Only committed credentials.db receipts prove money moved. Approval gates admission, never receipt reconciliation.</remarks>
public sealed class TenantCustomerWalletFunding
{
    private readonly UserDbContextFactory _users;
    private readonly CredentialsStore _wallet;
    private readonly WalletLedgerService _ledger;
    /// <summary>Central financial audit sink; never a storefront-owner notification.</summary>
    private readonly ILogger<TenantCustomerWalletFunding> _logger;

    /// <summary>Creates an operation-local financial bridge.</summary>
    /// <param name="users">Factory for tenant orders in users.db.</param>
    /// <param name="wallet">Canonical global credentials.db wallet store.</param>
    /// <param name="ledger">Existing idempotent audit writer, not a balance authority.</param>
    /// <param name="logger">Optional central audit sink; omitted in isolated store fixtures.</param>
    public TenantCustomerWalletFunding(UserDbContextFactory users, CredentialsStore wallet, WalletLedgerService ledger,
        ILogger<TenantCustomerWalletFunding> logger = null)
    { _users = users; _wallet = wallet; _ledger = ledger; _logger = logger ?? NullLogger<TenantCustomerWalletFunding>.Instance; }

    /// <summary>Returns the immutable customer debit identity for one persisted tenant order.</summary>
    /// <param name="orderId">Positive users.db TenantBotOrder primary key.</param>
    /// <returns>A global receipt key independent of update id and provider callbacks.</returns>
    /// <remarks>Use only after users.db has assigned the order id; the same key survives retries and process restart.</remarks>
    public static string DebitKey(int orderId) => $"tenant-customer-wallet:{orderId}:debit";

    /// <summary>Verifies exact amount, customer, storefront, sign and order linkage against committed balance evidence.</summary>
    /// <param name="order">Persisted tenant order; prices must come from the authoritative catalog.</param>
    /// <param name="token">Cancellation of local receipt reads.</param>
    /// <returns>A detached receipt or null; refund-authorized orders are never spendable.</returns>
    /// <exception cref="InvalidOperationException">A committed receipt conflicts with the order.</exception>
    /// <remarks>This read authorizes existing fulfillment, never a new debit, and remains valid after approval revocation.</remarks>
    public async Task<WalletOperation> ReadPaidEvidenceAsync(TenantBotOrder order, CancellationToken token = default)
    {
        if (order.PaymentProvider != "wallet" || order.CustomerWalletState is "refund_pending" or "refunded") return null;
        var receipt = await _wallet.GetWalletOperationAsync(DebitKey(order.Id), token);
        if (receipt != null && (receipt.TelegramUserId != order.CustomerTelegramUserId || receipt.AmountToman != -order.SalePriceToman
            || receipt.BotId != order.TenantBotId || order.SalePriceToman <= 0))
            throw new InvalidOperationException("Customer wallet receipt conflicts with tenant order.");
        return receipt;
    }

    /// <summary>Persists the order before debit, checking fresh approval inside the admission transaction.</summary>
    /// <param name="order">Authoritatively priced new purchase or existing unpaid renewal. Its customer comes from the Telegram sender.</param>
    /// <param name="admissionKey">Stable confirmation identity scoped to storefront/customer, at most 240 characters.</param>
    /// <param name="token">Cancellation of short local operations; no provider calls run inside the transaction.</param>
    /// <returns>A detached admitted order, reusing the same order for repeated confirmation.</returns>
    /// <exception cref="InvalidOperationException">Approval or identity changed, or the existing order is already funded through another channel.</exception>
    /// <remarks>This creates no money. A crash before debit leaves an unpaid order. Recovery never automatically debits an unconfirmed customer.</remarks>
    /// <example><code>var admitted = await funding.AdmitAsync(pricedOrder, confirmationKey, token);</code></example>
    public Task<TenantBotOrder> AdmitAsync(TenantBotOrder order, string admissionKey, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(admissionKey) || admissionKey.Length > 240 || order.CustomerTelegramUserId <= 0 || order.SalePriceToman <= 0)
            throw new ArgumentException("A persisted business identity and positive customer sale are required.");
        var isNew = order.Id == 0;
        return SqliteOperation.RunAsync(async ct =>
        {
            if (isNew) order.Id = 0;
            await using var db = _users.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var store = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.TenantBotId, ct);
            if (!TenantCustomerWalletPolicy.IsApproved(store) || store.OwnerTelegramUserId != order.OwnerTelegramUserId)
                throw new InvalidOperationException("Storefront is not approved for wallet admission.");
            var existing = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.CustomerWalletAdmissionKey == admissionKey, ct);
            if (existing != null)
            {
                if (existing.TenantBotId != order.TenantBotId || existing.CustomerTelegramUserId != order.CustomerTelegramUserId)
                    throw new InvalidOperationException("Wallet admission identity conflicts.");
                if (existing.ServiceKey != order.ServiceKey || existing.TrafficGb != order.TrafficGb || existing.DurationKey != order.DurationKey
                    || existing.UnlimitedPlanKey != order.UnlimitedPlanKey)
                    throw new InvalidOperationException("Wallet confirmation was reused for a different selection.");
                if (existing.PaidAtUtc == null && (existing.SalePriceToman != order.SalePriceToman || existing.BaseCostToman != order.BaseCostToman))
                    throw new InvalidOperationException("Wallet confirmation price changed; request a new quote.");
                return existing;
            }
            if (order.Id > 0)
            {
                var candidate = await db.TenantBotOrders.SingleAsync(x => x.Id == order.Id, ct);
                if (candidate.CustomerTelegramUserId != order.CustomerTelegramUserId || candidate.TenantBotId != order.TenantBotId
                    || candidate.PaymentProvider != "pending" || candidate.PaidAtUtc != null || candidate.IsFulfilled)
                    throw new InvalidOperationException("Tenant order already has a payment channel.");
                order = candidate;
            }
            else db.TenantBotOrders.Add(order);
            order.PaymentProvider = "wallet";
            order.CustomerWalletAdmissionKey = admissionKey;
            order.CustomerWalletState = "admitted";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return order;
        }, token);
    }

    /// <summary>Debits once with sufficient funds, then repairs the ledger and paid marker from the receipt.</summary>
    /// <param name="order">Persisted admitted order owned by the current customer and storefront.</param>
    /// <param name="token">Cancellation of local commits.</param>
    /// <returns>True when the exact debit exists; false when funds are insufficient and nothing was debited.</returns>
    /// <remarks>The caller rechecks approval immediately before this method. Repeated receipt recovery remains allowed after revocation.</remarks>
    /// <exception cref="InvalidOperationException">Persisted admission, fresh approval or receipt parameters do not match.</exception>
    /// <example><code>bool paid = await funding.DebitAsync(admitted, token);</code></example>
    public async Task<bool> DebitAsync(TenantBotOrder order, CancellationToken token = default)
    {
        await using (var db = _users.CreateDbContext())
        {
            var persisted = await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.Id, token);
            if (persisted == null || persisted.CustomerTelegramUserId != order.CustomerTelegramUserId || persisted.TenantBotId != order.TenantBotId
                || persisted.PaymentProvider != "wallet" || string.IsNullOrWhiteSpace(persisted.CustomerWalletAdmissionKey))
                throw new InvalidOperationException("Wallet debit requires a persisted admitted tenant order.");
            order = persisted;
        }
        if (order.CustomerWalletState is "refund_pending" or "refunded") return false;
        if (await ReadPaidEvidenceAsync(order, token) is { } existing)
        { await ReconcileReceiptAsync(order, existing, token); return true; }
        await using (var db = _users.CreateDbContext())
        {
            var store = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.TenantBotId, token);
            if (!TenantCustomerWalletPolicy.IsApproved(store)) throw new InvalidOperationException("Wallet admission was revoked.");
        }
        var receipt = await _wallet.TryDebitWalletIfSufficientAsync(order.CustomerTelegramUserId, order.SalePriceToman,
            DebitKey(order.Id), order.TenantBotId, token);
        if (receipt == null) return false;
        await ReconcileReceiptAsync(order, receipt, token);
        return true;
    }

    /// <summary>Refunds only a durably rejected and proven non-applied tenant wallet order.</summary>
    /// <param name="orderId">Internal tenant-order id, called while holding the existing tenant fulfillment gate.</param>
    /// <param name="token">Cancellation of local receipt, proof and compensation commits.</param>
    /// <returns>True when an exact compensation receipt was persisted; false when no safe refund is proven.</returns>
    /// <remarks>Refund authorization is persisted before credit. Any reserved, started, ambiguous or applied XUI operation
    /// prevents refund. The terminal marker blocks all subsequent fulfillment attempts, including administrator retry.</remarks>
    public async Task<bool> RefundRejectedAsync(int orderId, CancellationToken token = default)
    {
        var order = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.Id == orderId, ct);
            if (row?.PaymentProvider != "wallet" || row.IsFulfilled || row.CustomerWalletState == "refunded") return null;
            if (row.CustomerWalletState != "refund_pending")
            {
                var prefix = $"tenant-create:{row.Id}";
                if (await db.XuiV3CreationOperations.AnyAsync(x => (x.OperationKey == prefix || x.OperationKey.StartsWith(prefix + ":"))
                    && x.Outcome != XuiV3CreationOutcome.DefinitiveRejected, ct)) return null;
                var renewalKey = "tenant-renew-" + row.OrderId;
                if (await db.XuiV3RenewalOperations.AnyAsync(x => x.OperationKey == renewalKey
                    && x.Status != XuiV3RenewalOperationStatuses.Failed, ct)) return null;
                var rejected = await db.XuiV3CreationOperations.AnyAsync(x => (x.OperationKey == prefix || x.OperationKey.StartsWith(prefix + ":"))
                    && x.Outcome == XuiV3CreationOutcome.DefinitiveRejected, ct)
                    || await db.XuiV3RenewalOperations.AnyAsync(x => x.OperationKey == renewalKey && x.Status == XuiV3RenewalOperationStatuses.Failed, ct);
                if (row.PaymentStatus != TenantBotOrderStatuses.Failed && !rejected) return null;
                row.CustomerWalletState = "refund_pending";
                row.PaymentStatus = TenantBotOrderStatuses.Failed;
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return row;
        }, token);
        if (order == null) return false;
        var debit = await _wallet.GetWalletOperationAsync(DebitKey(order.Id), token);
        if (debit == null || debit.TelegramUserId != order.CustomerTelegramUserId || debit.AmountToman != -order.SalePriceToman
            || debit.BotId != order.TenantBotId) return false;
        var receipt = await _wallet.MutateWalletAsync(order.CustomerTelegramUserId, order.SalePriceToman,
            $"tenant-customer-wallet:{order.Id}:refund", token, order.TenantBotId);
        if (receipt == null) return false;
        await ReconcileReceiptAsync(order, receipt, token);
        return true;
    }

    /// <summary>Reconstructs the audit and paid marker without changing a wallet or contacting an external service.</summary>
    /// <param name="order">Persisted source order from users.db, never callback-supplied financial data.</param>
    /// <param name="receipt">Immutable matching credentials.db debit or refund receipt.</param>
    /// <param name="token">Cancellation of the independent users.db commit.</param>
    /// <returns>A task completing after idempotent ledger and settlement metadata persistence.</returns>
    /// <remarks>Safe after crash between databases; a refund remains terminal even when an older debit is reconciled later.
    /// Refund completion atomically enqueues a delivery-only customer notification. Financial audit logs contain order identity
    /// and signed amount, never a customer's whole wallet balance or account configuration.</remarks>
    public async Task ReconcileReceiptAsync(TenantBotOrder order, WalletOperation receipt, CancellationToken token = default)
    {
        var refund = receipt.OperationKey == $"tenant-customer-wallet:{order.Id}:refund";
        if ((!refund && receipt.OperationKey != DebitKey(order.Id)) || receipt.TelegramUserId != order.CustomerTelegramUserId
            || receipt.AmountToman != (refund ? order.SalePriceToman : -order.SalePriceToman) || receipt.BotId != order.TenantBotId)
            throw new InvalidOperationException("Customer wallet receipt conflicts with tenant order.");
        await _ledger.RecordAsync(receipt.TelegramUserId, refund ? WalletLedgerDirections.Credit : WalletLedgerDirections.Debit,
            Math.Abs(receipt.AmountToman), receipt.BeforeBalance, receipt.AfterBalance,
            refund ? "account_refund" : order.OrderKind == TenantBotOrderKinds.Renew ? WalletLedgerReasons.AccountRenew : WalletLedgerReasons.AccountPurchase,
            provider: "wallet", referenceType: order.OrderKind == TenantBotOrderKinds.Renew ? "tenant-renew-order" : "tenant-order",
            referenceId: order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), orderId: order.OrderId,
            botId: order.TenantBotId, botUsername: order.TenantBotUsername, botType: BotInstanceTypes.Tenant,
            idempotencyKey: receipt.OperationKey, cancellationToken: token);
        var stateChanged = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var changed = refund
                ? await db.TenantBotOrders.Where(x => x.Id == order.Id && x.CustomerWalletState == "refund_pending")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CustomerWalletState, "refunded"), ct)
                : await db.TenantBotOrders.Where(x => x.Id == order.Id && x.CustomerWalletState == "admitted")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CustomerWalletState, "paid")
                        .SetProperty(x => x.PaidAtUtc, receipt.CreatedAtUtc).SetProperty(x => x.PaymentStatus, TenantBotOrderStatuses.Paid), ct);
            if (refund)
            {
                // The receipt authorizes delivery only; notification retries cannot repeat the refund.
                var key = $"tenant-wallet-refund:{order.Id}";
                if (!await db.PaymentSettlementNotifications.AnyAsync(x => x.NotificationKey == key, ct))
                {
                    db.PaymentSettlementNotifications.Add(new PaymentSettlementNotification
                    {
                        NotificationKey = key, Provider = "wallet-refund", ProviderPaymentId = order.Id,
                        BotId = order.TenantBotId, WalletOriginBotType = BotInstanceTypes.Tenant,
                        WalletOriginTelegramBotId = null, TelegramUserId = order.CustomerTelegramUserId, ChatId = order.CustomerChatId,
                        AmountToman = receipt.AmountToman, CreatedAtUtc = receipt.CreatedAtUtc, UpdatedAtUtc = receipt.CreatedAtUtc,
                        NextAttemptAtUtc = receipt.CreatedAtUtc,
                        MessageText = $"مبلغ {receipt.AmountToman:N0} تومان بابت عملیات انجام‌نشده به کیف پول سراسری شما برگشت.\nموجودی پس از بازگشت: {receipt.AfterBalance:N0} تومان"
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
            await transaction.CommitAsync(ct);
            return changed;
        }, token);
        if (stateChanged > 0)
            _logger.LogPayment($"Tenant customer wallet {(refund ? "refund" : "debit")}\nOrder: <code>{order.Id}</code>\nCustomer: <code>{order.CustomerTelegramUserId}</code>\nAmount (toman): <code>{receipt.AmountToman}</code>");
    }
}
