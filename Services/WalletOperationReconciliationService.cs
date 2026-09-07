using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Repairs the users.db side of committed credentials.db wallet events after a crash between commits.</summary>
/// <remarks>
/// This worker never changes balances or calls payment/XUI providers. The immutable wallet receipt is the source of
/// truth. Ledger idempotency permits a crash after its insert and before the credentials reconciliation timestamp.
/// </remarks>
public sealed class WalletOperationReconciliationService : BackgroundService
{
    private readonly CredentialsDbContextFactory _credentials;
    private readonly UserDbContextFactory _users;
    private readonly WalletLedgerService _ledger;
    private readonly ILogger<WalletOperationReconciliationService> _logger;
    private readonly HashSet<string> _reportedPendingReceipts = new(StringComparer.Ordinal);

    /// <summary>Creates a recovery worker retaining factories and stateless services only.</summary>
    /// <param name="credentials">Global balance/receipt database factory.</param>
    /// <param name="users">Financial metadata and ledger database factory.</param>
    /// <param name="ledger">Idempotent users.db ledger writer.</param>
    /// <param name="logger">Operational logger; logs coarse failures without private row contents.</param>
    /// <remarks>Recovery reads committed wallet receipts, writes users.db idempotently, then marks credentials.db reconciled. These are separate local commits with no network calls.</remarks>
    public WalletOperationReconciliationService(CredentialsDbContextFactory credentials, UserDbContextFactory users,
        WalletLedgerService ledger, ILogger<WalletOperationReconciliationService> logger)
    { _credentials = credentials; _users = users; _ledger = ledger; _logger = logger; }

    /// <summary>Periodically reconciles committed receipts in bounded batches.</summary>
    /// <param name="stoppingToken">Host shutdown cancellation.</param>
    /// <returns>The tracked background lifetime.</returns>
    /// <remarks>Recovery reads committed wallet receipts, writes users.db idempotently, then marks credentials.db reconciled. These are separate local commits with no network calls.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError("Wallet receipt reconciliation failed. ErrorType={ErrorType}", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>Repairs one batch of wallet events whose normal caller had time to finish its second commit.</summary>
    /// <param name="token">Cancellation of database-only recovery.</param>
    /// <returns>Number of receipts whose ledger and supported settlement metadata were reconciled.</returns>
    /// <remarks>Receipts younger than one minute remain with their active caller. No history is inferred from current balances.
    /// Confirmed website debt transfers first repair their idempotent local credit; no website mutation is retried.</remarks>
    /// <example><code>var repaired = await reconciliation.ReconcileAsync(token);</code></example>
    public async Task<int> ReconcileAsync(CancellationToken token = default)
    {
        List<TenantDebtTransfer> transfers;
        await using (var db = _users.CreateDbContext())
            transfers = await db.Set<TenantDebtTransfer>().AsNoTracking()
                .Where(x => x.Status == "pending" && db.Set<SiteWalletDebitOperation>().Any(r =>
                    r.Id == "site:" + x.OwnerTelegramUserId + ":tenant-debt:" + x.Id && r.Status == "applied"))
                .OrderBy(x => x.CreatedAtUtc).Take(100).ToListAsync(token);
        foreach (var transfer in transfers)
        {
            try { await TenantAccessService.RecoverAsync(_users, new CredentialsStore(_credentials), transfer, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError("Debt transfer credit remains pending. ErrorType={ErrorType}", ex.GetType().Name); }
        }
        List<WalletOperation> receipts;
        await using (var db = _credentials.CreateDbContext())
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            receipts = await db.WalletOperations.AsNoTracking().Where(x => x.ReconciledAtUtc == null && x.CreatedAtUtc <= cutoff)
                .OrderBy(x => x.CreatedAtUtc).Take(100).ToListAsync(token);
        }
        var repaired = 0;
        foreach (var receipt in receipts)
        {
            try
            {
                var credit = receipt.AmountToman > 0;
                // A receipt's key links directly to the order/payment or referral event even when notification failed.
                await _ledger.RecordAsync(receipt.TelegramUserId, credit ? WalletLedgerDirections.Credit : WalletLedgerDirections.Debit,
                    Math.Abs(receipt.AmountToman), receipt.BeforeBalance, receipt.AfterBalance,
                    ResolveReason(receipt.OperationKey), provider: "wallet-reconciliation", referenceType: nameof(WalletOperation),
                    referenceId: receipt.OperationKey, description: "Recovered committed wallet operation",
                    botId: receipt.BotId, idempotencyKey: receipt.OperationKey, cancellationToken: token);
                await RepairSettlementAsync(receipt, token);
                await SqliteOperation.RunAsync(async ct =>
                {
                    await using var db = _credentials.CreateDbContext();
                    return await db.WalletOperations.Where(x => x.OperationKey == receipt.OperationKey && x.ReconciledAtUtc == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.ReconciledAtUtc, DateTime.UtcNow), ct);
                }, token);
                _reportedPendingReceipts.Remove(receipt.OperationKey);
                repaired++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (InvalidOperationException ex)
            {
                if (_reportedPendingReceipts.Add(receipt.OperationKey))
                    _logger.LogError(
                        "Wallet receipt remains pending. OperationKey={OperationKey} ErrorType={ErrorType} Error={Error}",
                        receipt.OperationKey, ex.GetType().Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Wallet receipt remains pending. OperationKey={OperationKey} ErrorType={ErrorType}",
                    receipt.OperationKey, ex.GetType().Name);
            }
        }
        return repaired;
    }

    /// <summary>Maps the business event key to the existing ledger reason vocabulary.</summary>
    /// <param name="key">Stable non-secret wallet event identity.</param>
    /// <returns>The audit reason; unknown future events use an explicit recovery label.</returns>
    /// <remarks>Debt repayment is an owner wallet transfer, never purchase profit. Ledger recovery uses committed receipts only.</remarks>
    private static string ResolveReason(string key) => key.StartsWith("tenant-debt:", StringComparison.Ordinal) ? "owner_debt_settlement"
        : key.StartsWith("payment:", StringComparison.Ordinal) ? WalletLedgerReasons.WalletCharge
        : key.StartsWith("renew:", StringComparison.Ordinal) || key.StartsWith("legacy-renew:", StringComparison.Ordinal) ? WalletLedgerReasons.AccountRenew
        : key.StartsWith("purchase:", StringComparison.Ordinal) || key.StartsWith("legacy-purchase:", StringComparison.Ordinal) ? WalletLedgerReasons.AccountPurchase
        : key.StartsWith("admin:", StringComparison.Ordinal) ? WalletLedgerReasons.AdminAdjustment : "wallet_recovery";

    /// <summary>Completes local payment/referral metadata using the receipt without repeating the financial event.</summary>
    /// <param name="receipt">Detached proof of a committed balance change.</param>
    /// <param name="token">Cancellation of the short users.db transaction.</param>
    /// <returns>A task completing after all supported local settlement fields commit.</returns>
    /// <remarks>Tenant order fulfillment and XUI delivery remain with their existing sagas; a wallet receipt alone does not prove delivery.
    /// A missing or mismatched payment target throws and leaves the receipt pending for operator review.</remarks>
    private async Task RepairSettlementAsync(WalletOperation receipt, CancellationToken token)
    {
        await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _users.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var parts = receipt.OperationKey.Split(':');
            if (parts.Length == 4 && parts[0] == "payment" && int.TryParse(parts[2], out var id))
            {
                string botId = null; long chatId = 0;
                switch (parts[1])
                {
                    case "hooshpay":
                        var hoosh = await db.HooshPayPaymentInfos.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (hoosh == null || hoosh.TelegramUserId != receipt.TelegramUserId || receipt.AmountToman <= 0)
                            throw new InvalidOperationException("Committed wallet receipt has no matching payment target; operator review is required.");
                        if (hoosh != null && !hoosh.IsAddedToBalance)
                        {
                            hoosh.IsAddedToBalance = true; hoosh.BalanceBefore = receipt.BeforeBalance; hoosh.BalanceAfter = receipt.AfterBalance;
                            hoosh.SettledAtUtc ??= receipt.CreatedAtUtc; botId = hoosh.BotId; chatId = hoosh.ChatId;
                            if (receipt.ApprovalKind == "provisional")
                            {
                                hoosh.IsProvisionallyApproved = true; hoosh.ProvisionalApprovedAtUtc = receipt.CreatedAtUtc;
                                hoosh.ProvisionalApprovedByTelegramUserId = receipt.ApprovedByTelegramUserId;
                            }
                        }
                        break;
                    case "nowpayments":
                        var now = await db.SwapinoPaymentInfos.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (now == null || now.TelegramUserId != receipt.TelegramUserId || receipt.AmountToman <= 0)
                            throw new InvalidOperationException("Committed wallet receipt has no matching payment target; operator review is required.");
                        if (now != null && !now.IsAddedToBalance)
                        {
                            now.IsAddedToBalance = true; now.BalanceBefore = receipt.BeforeBalance; now.BalanceAfter = receipt.AfterBalance;
                            now.SettledAtUtc ??= receipt.CreatedAtUtc; botId = now.BotId; chatId = now.ChatId;
                            if (receipt.ApprovalKind == "partial")
                            {
                                now.AmountToman = receipt.AmountToman; now.ErrorCode = "partial_settlement";
                                now.ErrorMessage = "Partial crypto credit recovered from its durable wallet receipt.";
                            }
                        }
                        break;
                    case "tetraminator":
                        var tetra = await db.TetraminatorPaymentInfos.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (tetra == null || tetra.TelegramUserId != receipt.TelegramUserId || receipt.AmountToman <= 0)
                            throw new InvalidOperationException("Committed wallet receipt has no matching payment target; operator review is required.");
                        if (tetra != null && !tetra.IsAddedToBalance)
                        {
                            tetra.IsAddedToBalance = true; tetra.BalanceBefore = receipt.BeforeBalance; tetra.BalanceAfter = receipt.AfterBalance;
                            tetra.SettledAtUtc ??= receipt.CreatedAtUtc; botId = tetra.BotId; chatId = tetra.ChatId;
                            if (receipt.ApprovalKind == "provisional")
                            {
                                tetra.IsProvisionallyApproved = true; tetra.ProvisionalApprovedAtUtc = receipt.CreatedAtUtc;
                                tetra.ProvisionalApprovedByTelegramUserId = receipt.ApprovedByTelegramUserId;
                            }
                        }
                        break;
                    case "uniquepay":
                        var unique = await db.UniquePayPaymentInfos.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (unique == null || unique.TelegramUserId != receipt.TelegramUserId || receipt.AmountToman <= 0)
                            throw new InvalidOperationException("Committed wallet receipt has no matching payment target; operator review is required.");
                        if (unique != null && !unique.IsAddedToBalance)
                        {
                            unique.IsAddedToBalance = true; unique.BalanceBefore = receipt.BeforeBalance; unique.BalanceAfter = receipt.AfterBalance;
                            unique.SettledAtUtc ??= receipt.CreatedAtUtc; unique.SettlementState = UniquePaySettlementStates.Settled;
                            unique.SettlementAttemptId = null; unique.SettlementStartedAtUtc = null;
                            botId = unique.BotId; chatId = unique.ChatId;
                            if (receipt.ApprovalKind == "provisional")
                            {
                                unique.IsProvisionallyApproved = true; unique.ProvisionalApprovedAtUtc = receipt.CreatedAtUtc;
                                unique.ProvisionalApprovedByTelegramUserId = receipt.ApprovedByTelegramUserId;
                            }
                        }
                        break;
                    case "zibal":
                        var zibal = await db.ZibalPaymentInfos.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (zibal == null || zibal.TelegramUserId != receipt.TelegramUserId || receipt.AmountToman <= 0)
                            throw new InvalidOperationException("Committed wallet receipt has no matching payment target; operator review is required.");
                        if (zibal != null) zibal.IsAddedToBallance = true;
                        break;
                }
                if (botId != null)
                {
                    var notification = PaymentSettlementNotification.CreateOwnedWalletCredit(parts[1], id, botId,
                        receipt.TelegramUserId, chatId, receipt.AmountToman,
                        "اعتبار کیف پول شما افزایش یافت. اکنون می‌توانید خرید یا تمدید اکانت را ادامه دهید.", receipt.CreatedAtUtc);
                    if (!await db.PaymentSettlementNotifications.AnyAsync(x => x.NotificationKey == notification.NotificationKey, ct))
                        db.PaymentSettlementNotifications.Add(notification);
                }
            }
            var reward = await db.ReferralRewards.SingleOrDefaultAsync(x => x.WalletMutationKey == receipt.OperationKey, ct);
            if (reward != null && reward.Status != ReferralProcessingStatuses.Applied)
            {
                reward.Status = ReferralProcessingStatuses.Credited;
                reward.BalanceBefore = receipt.BeforeBalance; reward.BalanceAfter = receipt.AfterBalance;
                reward.LastError = null; reward.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }, token);
    }
}
