using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Recovers only definitively owed owned-colleague test refunds from persistent grant and wallet receipts.</summary>
public sealed partial class WalletOperationReconciliationService
{
    /// <summary>Closes paid-test rejection-before-refund crash windows without Telegram state or any panel call.</summary>
    /// <param name="token">Host cancellation of short database operations and idempotent wallet/ledger commits.</param>
    /// <returns>Number of exact debit-linked refunds whose proof marker was first recorded in this bounded batch.</returns>
    /// <remarks>
    /// Reads at most 100 retained grants older than one minute. Rejected grants or Started/Uncertain grants with a
    /// durable DefinitiveRejected creation operation are eligible; missing/Reserved/ambiguous/Applied evidence never
    /// triggers a refund. The exact negative credentials.db debit receipt selects actor, origin and amount. Credit,
    /// ledger and marker are separate idempotent commits, so restart or a concurrent winning handler never duplicates
    /// a balance change. No conversation is required, no payment/XUI request is made and no notification is resent.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The host cancels a local database operation.</exception>
    /// <example><code>var recovered = await RecoverColleagueTrialRefundsAsync(stoppingToken);</code></example>
    private async Task<int> RecoverColleagueTrialRefundsAsync(CancellationToken token)
    {
        List<ColleagueTrialGrant> pending;
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _users.CreateDbContext())
        {
            pending = await db.ColleagueTrialGrants.AsNoTracking().Where(x =>
                x.State == ColleagueTrialGrantState.Denied && x.PaidRefundRecordedAtUtc == null && x.CreatedAtUtc <= cutoff &&
                (x.PaidCreationState == ColleagueTrialPaidCreationState.Rejected ||
                    ((x.PaidCreationState == ColleagueTrialPaidCreationState.Started ||
                      x.PaidCreationState == ColleagueTrialPaidCreationState.Uncertain) &&
                     db.XuiV3CreationOperations.Any(operation => operation.OperationKey == "colleague-paid-trial:" + x.Id &&
                         operation.Outcome == XuiV3CreationOutcome.DefinitiveRejected))))
                .OrderBy(x => x.CreatedAtUtc).Take(100).ToListAsync(token);
        }
        var credentials = new CredentialsStore(_credentials);
        var quotas = new ColleagueTrialQuotaStore(_users);
        var recovered = 0;
        foreach (var candidate in pending)
        {
            try
            {
                var grant = candidate;
                if (grant.PaidCreationState != ColleagueTrialPaidCreationState.Rejected)
                    grant = await quotas.SettlePaidCreationAsync(grant.Id, grant.TelegramUserId, grant.BotId,
                        DateTime.UtcNow, token);
                if (grant?.PaidCreationState != ColleagueTrialPaidCreationState.Rejected) continue;
                var key = "colleague-paid-trial:" + grant.Id;
                var debit = await credentials.GetWalletOperationAsync(key + ":debit", token);
                if (debit == null || debit.AmountToman >= 0 || debit.TelegramUserId != grant.TelegramUserId ||
                    debit.BotId != grant.BotId || checked(-debit.AmountToman) != grant.PaidQuoteToman)
                    throw new InvalidOperationException("Definitive paid-test refund lacks a matching immutable debit receipt.");
                var amount = checked(-debit.AmountToman);
                // An existing credit receipt is returned unchanged after a crash or concurrent handler refund.
                var refund = await credentials.MutateWalletAsync(grant.TelegramUserId, amount, key + ":refund", token, grant.BotId);
                if (refund == null) throw new InvalidOperationException("Paid-test refund recipient is missing.");
                await _ledger.RecordAsync(grant.TelegramUserId, WalletLedgerDirections.Credit, amount,
                    refund.BeforeBalance, refund.AfterBalance, WalletLedgerReasons.ColleagueTrialRefund,
                    provider: "wallet", referenceType: "colleague_trial", referenceId: grant.Id,
                    botId: grant.BotId, botType: BotInstanceTypes.Owned, idempotencyKey: refund.OperationKey,
                    cancellationToken: token);
                if (await quotas.MarkPaidRefundRecordedAsync(grant.Id, grant.TelegramUserId, grant.BotId,
                        DateTime.UtcNow, token)) recovered++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError("Paid-test refund remains pending. GrantId={GrantId} ErrorType={ErrorType}",
                    candidate.Id, ex.GetType().Name);
            }
        }
        return recovered;
    }
}
