using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Bridges tenant order identity to the existing global customer wallet and ledger without a cross-database transaction.</summary>
/// <remarks>Only committed credentials.db receipts prove money moved. Approval and global commercial permission gate new admission, never receipt reconciliation.</remarks>
public sealed class TenantCustomerWalletFunding
{
    private readonly UserDbContextFactory _users;
    private readonly CredentialsStore _wallet;
    private readonly WalletLedgerService _ledger;
    /// <summary>Central financial audit sink; never a storefront-owner notification.</summary>
    private readonly ILogger<TenantCustomerWalletFunding> _logger;
    /// <summary>Shared users.db quote and one-time capacity reservation logic.</summary>
    private readonly TenantDiscountService _discounts;
    /// <summary>Shared live commercial admission policy; absent injection fails closed for new work, never for receipt repair.</summary>
    private readonly IServiceSalesAvailability _salesAvailability;

    /// <summary>Creates an operation-local financial bridge.</summary>
    /// <param name="users">Factory for tenant orders in users.db.</param>
    /// <param name="wallet">Canonical global credentials.db wallet store.</param>
    /// <param name="ledger">Existing idempotent audit writer, not a balance authority.</param>
    /// <param name="logger">Optional central audit sink; omitted in isolated store fixtures.</param>
    /// <param name="discounts">Shared users.db quote and reservation primitive, also used by gateway admission.</param>
    /// <param name="salesAvailability">Production singleton shared by every bot. Required for admission/debit; recovery-only callers may omit it.</param>
    /// <remarks>Orders are isolated by internal tenant bot id; balances and immutable debit receipts belong to the global Telegram user. Construction moves no money. Production admission must share the global policy singleton; committed-receipt repair remains available to recovery-only callers.</remarks>
    /// <example><code>var funding = new TenantCustomerWalletFunding(users, wallet, ledger, salesAvailability: globalSalesAvailability);</code></example>
    public TenantCustomerWalletFunding(UserDbContextFactory users, CredentialsStore wallet, WalletLedgerService ledger,
        ILogger<TenantCustomerWalletFunding> logger = null, TenantDiscountService discounts = null,
        IServiceSalesAvailability salesAvailability = null)
    { _users = users; _wallet = wallet; _ledger = ledger; _logger = logger ?? NullLogger<TenantCustomerWalletFunding>.Instance;
        _salesAvailability = salesAvailability; _discounts = discounts ?? new TenantDiscountService(users, salesAvailability); }

    /// <summary>Enforces the global service/operation permission for new wallet admission or first debit.</summary>
    /// <param name="order">Authorized tenant order with its stored service, fixed-plan and operation identity.</param>
    /// <remarks>No tenant preference or customer role bypasses this global check. Committed receipt reconciliation must run before calling it.</remarks>
    /// <exception cref="InvalidOperationException">The policy dependency is missing or the operation is closed; no new debit is permitted.</exception>
    /// <example><code>EnsureSalesAdmission(unpaidOrder);</code></example>
    private void EnsureSalesAdmission(TenantBotOrder order)
    {
        if (_salesAvailability == null)
            throw new InvalidOperationException("Global sales admission policy is required.");
        var operation = order.OrderKind == TenantBotOrderKinds.Renew ? ServiceSalesOperation.Renewal : ServiceSalesOperation.Sale;
        var category = ServiceSalesPolicy.GetCategory(order.ServiceKey,
            string.IsNullOrWhiteSpace(order.UnlimitedPlanKey) ? null : XuiV3ServiceKinds.Unlimited);
        if (!_salesAvailability.Snapshot.IsEnabled(category, operation))
            throw new InvalidOperationException(ServiceSalesPolicy.GetDisabledMessage(category, operation));
    }

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
    /// <exception cref="InvalidOperationException">New commercial admission is closed or lacks its policy, approval or identity changed, or the existing order is funded through another channel.</exception>
    /// <remarks>This creates no money. Global sale/renewal permission is re-read before first admission. A crash before debit leaves an unpaid order; recovery never automatically debits an unconfirmed customer.</remarks>
    /// <example><code>var admitted = await funding.AdmitAsync(pricedOrder, confirmationKey, token);</code></example>
    public Task<TenantBotOrder> AdmitAsync(TenantBotOrder order, string admissionKey, CancellationToken token = default) =>
        AdmitCoreAsync(order, admissionKey, null, token);

    /// <summary>Admits a wallet quote, immutable admission key, order and optional code claim in one users.db commit.</summary>
    /// <param name="order">Unsaved tenant purchase snapshot from the current selection; net is authoritative only after quote admission.</param>
    /// <param name="admissionKey">Stable wallet confirmation identity for this exact quote message.</param>
    /// <param name="quoteId">Stored quote id, never sufficient alone for authorization.</param>
    /// <param name="messageId">Actual callback message id bound to this quote.</param>
    /// <param name="selectionKey">Selection parsed from the server-stored quote.</param>
    /// <param name="grossToman">Fresh undiscounted tariff, compared inside the admission transaction.</param>
    /// <param name="baseCostToman">Fresh colleague cost, compared inside the admission transaction.</param>
    /// <param name="token">Cancellation of local users.db work.</param>
    /// <returns>The one admitted order; retries return its original immutable sale.</returns>
    /// <exception cref="InvalidOperationException">New commercial admission is closed or lacks its policy, the quote was admitted elsewhere, or its discount changed.</exception>
    /// <example><code>var order = await funding.AdmitQuotedAsync(pricedOrder, confirmationKey, quote.Id, messageId, quote.SelectionKey, gross, cost, token);</code></example>
    /// <remarks>The caller resolves selection and tariff from the stored, message-bound quote. First admission requires live global sale permission; no wallet debit occurs in this transaction and committed-receipt reconciliation is separate.</remarks>
    public Task<TenantBotOrder> AdmitQuotedAsync(TenantBotOrder order, string admissionKey, int quoteId,
        int messageId, string selectionKey, long grossToman, long baseCostToman, CancellationToken token = default) =>
        AdmitCoreAsync(order, admissionKey, new WalletQuoteAdmission(quoteId, messageId, selectionKey, grossToman, baseCostToman), token);

    /// <summary>Frozen inputs bound to a quoted purchase message and fresh tariff.</summary>
    /// <param name="QuoteId">Stored quote identity.</param>
    /// <param name="MessageId">Delivered Telegram message identity.</param>
    /// <param name="SelectionKey">Server-stored selection.</param>
    /// <param name="GrossToman">Current gross tariff.</param>
    /// <param name="BaseCostToman">Current base cost.</param>
    private sealed record WalletQuoteAdmission(int QuoteId, int MessageId, string SelectionKey, long GrossToman, long BaseCostToman);

    /// <summary>Runs both legacy and quote wallet admission under the same approval and admission-key writer transaction.</summary>
    /// <param name="order">New customer order or existing pending renewal.</param>
    /// <param name="admissionKey">Unique wallet admission identity.</param>
    /// <param name="quote">Exact bound quote and fresh tariff, or null for legacy flow.</param>
    /// <param name="token">Cancellation of local users.db work.</param>
    /// <returns>Persisted order and its original wallet charge identity.</returns>
    /// <remarks>Writes are committed once with any discount redemption. Live global sale/renewal permission is re-read before new admission and final staging; committed-receipt recovery is separate. Credentials.db is not accessed here.</remarks>
    /// <exception cref="InvalidOperationException">The store, method, selection, price or claim changed.</exception>
    /// <exception cref="ArgumentException">The admission key or wallet sale identity is invalid.</exception>
    private Task<TenantBotOrder> AdmitCoreAsync(TenantBotOrder order, string admissionKey,
        WalletQuoteAdmission quote, CancellationToken token)
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
                if (quote == null && existing.PaidAtUtc == null
                    && (existing.SalePriceToman != order.SalePriceToman || existing.BaseCostToman != order.BaseCostToman))
                    throw new InvalidOperationException("Wallet confirmation price changed; request a new quote.");
                if (quote != null && (existing.PaymentProvider != "wallet" || existing.CustomerChatId != order.CustomerChatId
                    || !await db.TenantDiscountQuotes.AnyAsync(x => x.Id == quote.QuoteId && x.TenantBotId == order.TenantBotId
                        && x.CustomerTelegramUserId == order.CustomerTelegramUserId && x.ChatId == order.CustomerChatId
                        && x.MessageId == quote.MessageId && x.SelectionKey == quote.SelectionKey && x.OrderId == existing.Id
                        && x.SelectedProvider == "W" && x.State == TenantDiscountQuoteStates.Admitted, ct)))
                    throw new InvalidOperationException("Wallet quote admission was superseded or changed.");
                if (quote != null && ((existing.OriginalSalePriceToman ?? existing.SalePriceToman) != quote.GrossToman
                    || existing.BaseCostToman != quote.BaseCostToman))
                    throw new InvalidOperationException("Wallet confirmation original tariff changed.");
                if (existing.CustomerWalletState == "definitive_failed"
                    || existing.PaymentStatus == TenantBotOrderStatuses.DiscountExpired)
                    throw new InvalidOperationException("Wallet quote is terminal; request a fresh quote.");
                if (existing.TenantDiscountCodeId != null && !await db.TenantDiscountRedemptions.AnyAsync(x =>
                        x.TenantBotOrderId == existing.Id && x.CodeId == existing.TenantDiscountCodeId
                        && x.State != TenantDiscountRedemptionStates.Released, ct))
                    throw new InvalidOperationException("Wallet discount claim was released.");
                return existing;
            }
            EnsureSalesAdmission(order);
            if (order.Id > 0)
            {
                var candidate = await db.TenantBotOrders.SingleAsync(x => x.Id == order.Id, ct);
                if (candidate.CustomerTelegramUserId != order.CustomerTelegramUserId || candidate.TenantBotId != order.TenantBotId
                    || candidate.PaymentProvider != "pending" || candidate.PaidAtUtc != null || candidate.IsFulfilled)
                    throw new InvalidOperationException("Tenant order already has a payment channel.");
                if (candidate.TenantDiscountCodeId.HasValue && (candidate.DiscountInvoiceAttemptState != "none"
                    || candidate.PaymentStatus == TenantBotOrderStatuses.DiscountExpired
                    || !await db.TenantDiscountRedemptions.AnyAsync(x => x.TenantBotOrderId == candidate.Id
                        && x.CodeId == candidate.TenantDiscountCodeId
                        && x.State == TenantDiscountRedemptionStates.Reserved, ct)))
                    throw new InvalidOperationException("Renewal discount claim is no longer payable.");
                order = candidate;
            }
            else if (quote == null) db.TenantBotOrders.Add(order);
            if (quote != null)
            {
                if (!isNew) throw new InvalidOperationException("A quoted wallet purchase requires an unsaved order.");
                var admitted = await _discounts.AdmitQuotedOrderInTransactionAsync(db, quote.QuoteId, order.TenantBotId,
                    order.CustomerTelegramUserId, order.CustomerChatId, quote.MessageId, quote.SelectionKey, "W",
                    quote.GrossToman, quote.BaseCostToman, order, ct);
                if (!admitted.Success) throw new InvalidOperationException($"Wallet quote admission failed: {admitted.Failure}.");
                order = admitted.Value;
                if (order.PaymentProvider != "wallet")
                    throw new InvalidOperationException("Wallet quote was already admitted with another method.");
                if (order.CustomerWalletAdmissionKey != null)
                    throw new InvalidOperationException("Wallet quote is already admitted; use its existing receipt or status.");
            }
            order.PaymentProvider = "wallet";
            order.CustomerWalletAdmissionKey = admissionKey;
            EnsureSalesAdmission(order);
            order.CustomerWalletState = "admitted";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return order;
        }, token);
    }

    /// <summary>Debits exactly once at the persisted net; frozen attempts are claimed before credentials.db is touched.</summary>
    /// <param name="order">Persisted admitted order owned by the current customer and storefront.</param>
    /// <param name="token">Cancellation of local commits.</param>
    /// <param name="freshGrossToman">Current gross tariff for a discounted order; must match its original snapshot before any debit.</param>
    /// <param name="freshBaseCostToman">Current colleague base for a discounted order; must match its original snapshot.</param>
    /// <returns>True when the exact debit receipt exists; false when no receipt was created or the order is terminal.</returns>
    /// <remarks>The caller supplies fresh gross/base for an unpaid discounted order. Global closure blocks a first debit, not reconciliation of an exact committed receipt. After the first frozen debit attempt, retries can only reconcile immutable receipt evidence; insufficient funds terminate that order, while uncertain outcomes retain its claim.</remarks>
    /// <exception cref="InvalidOperationException">A first debit is globally closed or lacks its policy, or persisted admission, fresh approval or receipt parameters do not match.</exception>
    /// <example><code>bool paid = await funding.DebitAsync(admitted, token, freshGrossToman: gross, freshBaseCostToman: baseCost);</code></example>
    public async Task<bool> DebitAsync(TenantBotOrder order, CancellationToken token = default,
        long? freshGrossToman = null, long? freshBaseCostToman = null)
    {
        await using (var db = _users.CreateDbContext())
        {
            var persisted = await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.Id, token);
            if (persisted == null || persisted.CustomerTelegramUserId != order.CustomerTelegramUserId || persisted.TenantBotId != order.TenantBotId
                || persisted.PaymentProvider != "wallet" || string.IsNullOrWhiteSpace(persisted.CustomerWalletAdmissionKey))
                throw new InvalidOperationException("Wallet debit requires a persisted admitted tenant order.");
            order = persisted;
        }
        if (order.CustomerWalletState is "refund_pending" or "refunded" or "definitive_failed"
            || order.PaymentStatus == TenantBotOrderStatuses.DiscountExpired) return false;
        if (await ReadPaidEvidenceAsync(order, token) is { } existing)
        { await ReconcileReceiptAsync(order, existing, token); return true; }
        if (order.TenantDiscountCodeId.HasValue && (freshGrossToman != (order.OriginalSalePriceToman ?? order.SalePriceToman)
            || freshBaseCostToman != order.BaseCostToman))
            throw new InvalidOperationException("Discounted wallet order tariff changed before debit.");
        await using (var db = _users.CreateDbContext())
        {
            var store = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.TenantBotId, token);
            if (!TenantCustomerWalletPolicy.IsApproved(store)) throw new InvalidOperationException("Wallet admission was revoked.");
        }
        // Immutable debit evidence was reconciled above. A pending admission/insufficient balance is not
        // grandfathered money, so re-read the global operation before claiming or touching either wallet.
        EnsureSalesAdmission(order);
        DateTime? claimAtUtc = null;
        if (order.DiscountInvoiceAttemptState != null)
            claimAtUtc = await ClaimFrozenWalletDebitAsync(order, token);
        try { EnsureSalesAdmission(order); }
        catch (InvalidOperationException)
        {
            // This invocation owns the unique first claim and has not called credentials.db at all.
            // Undo only that exact unused claim, not a receipt/refund or an ambiguous debit attempt.
            if (claimAtUtc.HasValue)
                await ReleaseUnspentClosedWalletClaimAsync(order.Id, claimAtUtc.Value, CancellationToken.None);
            throw;
        }
        WalletOperation receipt;
        try
        {
            receipt = await _wallet.TryDebitWalletIfSufficientAsync(order.CustomerTelegramUserId, order.SalePriceToman,
                DebitKey(order.Id), order.TenantBotId, token);
        }
        catch (Exception) when (order.DiscountInvoiceAttemptState != null)
        {
            await MarkAmbiguousFrozenWalletAsync(order.Id, CancellationToken.None);
            throw;
        }
        if (receipt == null)
        {
            if (order.DiscountInvoiceAttemptState != null)
            {
                // A null sufficient-funds result proves this invocation did not debit. An overlapping
                // immutable receipt must still be reconciled, never treated as an unpaid order.
                var raced = await ReadPaidEvidenceAsync(order, token);
                if (raced != null) { await ReconcileReceiptAsync(order, raced, token); return true; }
                await MarkInsufficientFrozenWalletAsync(order, token);
            }
            return false;
        }
        await ReconcileReceiptAsync(order, receipt, token);
        return true;
    }

    /// <summary>Locks a quote-admitted purchase or discounted renewal to its first wallet debit before touching credentials.db.</summary>
    /// <param name="order">Persisted wallet order with an unstarted frozen payment attempt.</param>
    /// <param name="token">Cancellation of the users.db write.</param>
    /// <returns>The exact UTC claim instant identifying this invocation; no wallet mutation has happened yet.</returns>
    /// <remarks>Only the first claimant may attempt the debit. A closure before credentials.db is called releases only this unspent claim; started/ambiguous money attempts and committed receipts are never reset.</remarks>
    private Task<DateTime> ClaimFrozenWalletDebitAsync(TenantBotOrder order, CancellationToken token) =>
        SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.TenantBotOrders.SingleAsync(x => x.Id == order.Id, ct);
            if (row.PaymentProvider != "wallet" || row.CustomerWalletState != "admitted"
                || row.DiscountInvoiceAttemptState != "none" || row.PaidAtUtc != null
                || row.TenantDiscountCodeId != order.TenantDiscountCodeId
                || (row.TenantDiscountCodeId.HasValue && !await db.TenantDiscountRedemptions.AnyAsync(x => x.TenantBotOrderId == row.Id
                    && x.CodeId == row.TenantDiscountCodeId && x.State == TenantDiscountRedemptionStates.Reserved, ct)))
                throw new InvalidOperationException("Wallet debit attempt is already started or the discount claim is unavailable.");
            EnsureSalesAdmission(row);
            row.DiscountInvoiceAttemptState = "started";
            var claimAtUtc = DateTime.UtcNow;
            row.DiscountInvoiceAttemptedAtUtc = claimAtUtc;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return claimAtUtc;
        }, token);

    /// <summary>Restores only this invocation's frozen claim when global closure prevented any wallet call.</summary>
    /// <param name="orderId">Persisted users.db order claimed by this invocation.</param>
    /// <param name="claimAtUtc">Exact stored UTC instant returned by the unique first-claim transaction.</param>
    /// <param name="token">Cancellation of the short local reset; callers use an uncancelled token after denial.</param>
    /// <returns>A task completing after the guarded reset; the original discount reservation and quote remain intact.</returns>
    /// <remarks>Call exclusively before the wallet API is invoked. Never use for timeout, insufficient funds, existing receipts or recovery, because those outcomes have different financial evidence.</remarks>
    /// <example><code>await ReleaseUnspentClosedWalletClaimAsync(order.Id, claimAtUtc, CancellationToken.None);</code></example>
    private async Task ReleaseUnspentClosedWalletClaimAsync(int orderId, DateTime claimAtUtc, CancellationToken token)
    {
        await using var db = _users.CreateDbContext();
        await db.TenantBotOrders.Where(row => row.Id == orderId && row.PaymentProvider == "wallet"
            && row.CustomerWalletState == "admitted" && row.DiscountInvoiceAttemptState == "started"
            && row.DiscountInvoiceAttemptedAtUtc == claimAtUtc && row.PaidAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.DiscountInvoiceAttemptState, "none")
                .SetProperty(row => row.DiscountInvoiceAttemptedAtUtc, (DateTime?)null), token);
    }

    /// <summary>Holds a frozen wallet order after uncertain cross-database debit; only persisted wallet evidence resolves it.</summary>
    /// <param name="orderId">Wallet order with a started local attempt.</param>
    /// <param name="token">Cancellation of local users.db marker write.</param>
    /// <returns>A completed marker update; the claim remains held.</returns>
    /// <remarks>A transport exception cannot prove no debit, so this state is never a release authority.</remarks>
    private Task MarkAmbiguousFrozenWalletAsync(int orderId, CancellationToken token) =>
        SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await db.TenantBotOrders.Where(x => x.Id == orderId && x.DiscountInvoiceAttemptState == "started")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DiscountInvoiceAttemptState, "ambiguous"), ct);
            return 0;
        }, token);

    /// <summary>Terminates an insufficient-funds wallet attempt, releasing its discount claim only when one exists.</summary>
    /// <remarks>Transport errors never call this method. A crash between the two databases leaves any claim reserved for reconciliation.</remarks>
    /// <param name="order">Wallet order for which the credential store returned a definitive insufficient-funds result.</param>
    /// <param name="token">Cancellation of short users.db terminal write.</param>
    /// <returns>A completed terminal update only when no local payable rows or paid markers exist.</returns>
    private Task MarkInsufficientFrozenWalletAsync(TenantBotOrder order, CancellationToken token) =>
        SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.TenantBotOrders.SingleAsync(x => x.Id == order.Id, ct);
            if (row.PaymentProvider != "wallet" || row.CustomerWalletState != "admitted"
                || row.DiscountInvoiceAttemptState != "started" || row.PaidAtUtc != null || row.IsFulfilled
                || row.HooshPayPaymentInfoId != null || row.NowPaymentsPaymentInfoId != null
                || row.TetraminatorPaymentInfoId != null || row.UniquePayPaymentInfoId != null
                || row.AtlasPayPaymentInfoId != null || row.ManualReceiptId != null
                || await db.HooshPayPaymentInfos.AnyAsync(x => x.TenantBotOrderId == row.Id, ct)
                || await db.SwapinoPaymentInfos.AnyAsync(x => x.TenantBotOrderId == row.Id, ct)
                || await db.TetraminatorPaymentInfos.AnyAsync(x => x.TenantBotOrderId == row.Id, ct)
                || await db.UniquePayPaymentInfos.AnyAsync(x => x.TenantBotOrderId == row.Id, ct)
                || await db.AtlasPayPaymentInfos.AnyAsync(x => x.TenantBotOrderId == row.Id, ct))
                return 0;
            if (row.TenantDiscountCodeId.HasValue)
            {
                var claim = await db.TenantDiscountRedemptions.SingleOrDefaultAsync(x =>
                    x.TenantBotOrderId == row.Id && x.CodeId == row.TenantDiscountCodeId
                    && x.State == TenantDiscountRedemptionStates.Reserved, ct);
                if (claim == null) return 0;
                claim.State = TenantDiscountRedemptionStates.Released;
                claim.ReleasedAtUtc = DateTime.UtcNow;
            }
            row.DiscountInvoiceAttemptState = "definitive_failed";
            row.PaymentStatus = TenantBotOrderStatuses.Failed;
            row.CustomerWalletState = "definitive_failed";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return 0;
        }, token);

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
            if (row?.PaymentProvider != "wallet" || row.IsFulfilled || row.CustomerWalletState is "refunded" or "definitive_failed") return null;
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
    /// <example><code>await funding.ReconcileReceiptAsync(order, committedReceipt, token);</code></example>
    public async Task ReconcileReceiptAsync(TenantBotOrder order, WalletOperation receipt, CancellationToken token = default)
    {
        var refund = receipt.OperationKey == $"tenant-customer-wallet:{order.Id}:refund";
        if ((!refund && receipt.OperationKey != DebitKey(order.Id)) || receipt.TelegramUserId != order.CustomerTelegramUserId
            || receipt.AmountToman != (refund ? order.SalePriceToman : -order.SalePriceToman) || receipt.BotId != order.TenantBotId)
            throw new InvalidOperationException("Customer wallet receipt conflicts with tenant order.");
        if (refund)
        {
            var committedRefund = await _wallet.GetWalletOperationAsync(receipt.OperationKey, token);
            if (committedRefund == null || committedRefund.AmountToman != order.SalePriceToman
                || committedRefund.TelegramUserId != order.CustomerTelegramUserId || committedRefund.BotId != order.TenantBotId)
                throw new InvalidOperationException("Customer wallet refund receipt was not committed for this order.");
        }
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
