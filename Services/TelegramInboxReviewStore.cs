using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

/// <summary>Sanitized uncertain-inbox inspection and conservative resolution preconditions.</summary>
/// <remarks>Never returns update/client payloads or financial keys. Legacy sagas are conservatively correlated by
/// bot/user when no exact inbox link exists; unrelated unresolved operations can require additional review.</remarks>
public sealed partial class TelegramUpdateInboxStore
{
    private readonly CredentialsDbContextFactory _credentials;
    /// <summary>Live executions cannot be manually completed while a cancellation-ignoring handler still runs.</summary>
    internal readonly ConcurrentDictionary<long, byte> Executing = new();

    /// <summary>Reads a bounded metadata-only page of uncertain updates.</summary>
    /// <param name="after">Exclusive internal sequence cursor, zero for the first page.</param>
    /// <param name="token">Cancellation of this detached database read.</param>
    /// <returns>At most ten detached metadata rows without private payloads; an empty page means end of results.</returns>
    /// <remarks>Authorization belongs to the operator service; no state changes or external calls occur.</remarks>
    public async Task<List<TelegramUpdateInboxEntry>> ListUncertainAsync(long after, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.AsNoTracking().Where(x => x.Status == "uncertain" && x.Sequence > after)
            .OrderBy(x => x.Sequence).Take(10).Select(x => new TelegramUpdateInboxEntry
            { Sequence = x.Sequence, BotId = x.BotId, TelegramUserId = x.TelegramUserId, UpdateId = x.UpdateId,
                UpdateType = x.UpdateType, Status = x.Status, FailureCode = x.FailureCode,
                AcceptedAtUtc = x.AcceptedAtUtc, StartedAtUtc = x.StartedAtUtc }).ToListAsync(token);
    }

    /// <summary>Measures all uncertain rows, blocked execution lanes and oldest acceptance without reading payloads.</summary>
    /// <param name="token">Cancellation of metadata queries.</param>
    /// <returns>Uncertain count, distinct bot/user lane count, and oldest age in seconds, zero when none exist.</returns>
    /// <remarks>Uncertain rows remain included in strict admission capacity.</remarks>
    public async Task<(int Count, int Lanes, double AgeSeconds)> ReadUncertainSummaryAsync(CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        var query = db.TelegramUpdateInbox.Where(x => x.Status == "uncertain");
        var count = await query.CountAsync(token);
        var lanes = await query.Select(x => new { x.BotId, x.TelegramUserId }).Distinct().CountAsync(token);
        var oldest = await query.MinAsync(x => (DateTime?)x.AcceptedAtUtc, token);
        return (count, lanes, oldest.HasValue ? Math.Max(0, (DateTime.UtcNow - oldest.Value).TotalSeconds) : 0);
    }

    /// <summary>Evaluates durable XUI, wallet/ledger and tenant/saga evidence before explicit review.</summary>
    /// <param name="sequence">Internal uncertain sequence; never a Telegram update id.</param>
    /// <param name="token">Cancellation of short detached reads across the two independent databases.</param>
    /// <returns>A sanitized fixed-format evidence summary and whether all known preconditions permit review.</returns>
    /// <remarks>Missing wallet storage fails closed. Global receipts protect wallets across bots; legacy bot/user
    /// saga matching is intentionally conservative. This does not assert a transaction spanning both databases.</remarks>
    public async Task<(bool Allowed, string Evidence)> CanResolveAsync(long sequence, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        var row = await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "uncertain")
            .Select(x => new { x.BotId, x.TelegramUserId }).SingleOrDefaultAsync(token);
        if (row == null) return (false, "not_uncertain");
        if (Executing.ContainsKey(sequence)) return (false, "handler_still_active");
        var states = await db.XuiV3CreationOperations.Where(x => x.InboxSequence == sequence)
            .Select(x => x.Outcome).ToListAsync(token);
        var ambiguous = states.Any(x => x is not (XuiV3CreationOutcome.Reserved or XuiV3CreationOutcome.Applied or XuiV3CreationOutcome.DefinitiveRejected));
        var renewals = await db.XuiV3RenewalOperations.CountAsync(x => (x.InboxSequence == sequence || x.BotId == row.BotId && x.TelegramUserId == row.TelegramUserId)
            && x.Status != "failed" && (x.Status != "applied" || x.SettlementStatus != "settled"), token);
        var links = await db.XuiV3LinkChangeOperations.CountAsync(x => (x.InboxSequence == sequence || x.BotId == row.BotId && x.TelegramUserId == row.TelegramUserId)
            && x.Status != "succeeded" && x.Status != "failed_before_mutation" && x.Status != "cancelled" && x.Status != "expired", token);
        var orders = await db.TenantBotOrders.CountAsync(x => x.TenantBotId == row.BotId
            && (x.CustomerTelegramUserId == row.TelegramUserId || x.OwnerTelegramUserId == row.TelegramUserId)
            && !x.IsFulfilled && x.PaymentStatus != "receipt_rejected", token);
        if (_credentials == null) return (false, "wallet_evidence_unavailable");
        await using var credentials = _credentials.CreateDbContext();
        // Exact inbox receipts also include mutations of another user's wallet by this operator.
        var receipts = await credentials.WalletOperations.AsNoTracking()
            .Where(x => x.InboxSequence == sequence || (x.TelegramUserId == row.TelegramUserId && x.ReconciledAtUtc == null))
            .ToListAsync(token);
        var walletBlocked = false;
        foreach (var receipt in receipts)
        {
            if (receipt.ReconciledAtUtc == null || !await db.WalletLedgerEntries.AnyAsync(x => x.IdempotencyKey == receipt.OperationKey
                && x.TelegramUserId == receipt.TelegramUserId && x.BalanceBefore == receipt.BeforeBalance
                && x.BalanceAfter == receipt.AfterBalance && x.AmountToman == Math.Abs(receipt.AmountToman)
                && x.Direction == (receipt.AmountToman > 0 ? "credit" : "debit"), token)) walletBlocked = true;
        }
        var settlements = await db.UniquePayPaymentInfos.CountAsync(x => (x.TelegramUserId == row.TelegramUserId || x.ProvisionalApprovedByTelegramUserId == row.TelegramUserId)
            && !x.IsAddedToBalance && (x.SettlementStartedAtUtc != null || x.PaidAtUtc != null || x.PaymentStatus == "paid"), token)
            + await db.HooshPayPaymentInfos.CountAsync(x => (x.TelegramUserId == row.TelegramUserId || x.ProvisionalApprovedByTelegramUserId == row.TelegramUserId)
                && !x.IsAddedToBalance && (x.PaidAtUtc != null || x.PaymentStatus == "paid"), token)
            + await db.TetraminatorPaymentInfos.CountAsync(x => (x.TelegramUserId == row.TelegramUserId || x.ProvisionalApprovedByTelegramUserId == row.TelegramUserId)
                && !x.IsAddedToBalance && (x.PaidAtUtc != null || x.PaymentStatus == "paid"), token)
            + await db.SwapinoPaymentInfos.CountAsync(x => x.TelegramUserId == row.TelegramUserId && !x.IsAddedToBalance
                && (x.PaidAtUtc != null || x.PaymentStatus == "finished"), token);
        settlements += await db.ZibalPaymentInfos.CountAsync(x => x.TelegramUserId == row.TelegramUserId && x.IsPaid && !x.IsAddedToBallance, token);
        var renewalIds = await db.XuiV3RenewalOperations.Where(x => x.InboxSequence == sequence).OrderBy(x => x.Id).Select(x => x.Id).Take(10).ToListAsync(token);
        var linkIds = await db.XuiV3LinkChangeOperations.Where(x => x.InboxSequence == sequence).OrderBy(x => x.Id).Select(x => x.Id).Take(10).ToListAsync(token);
        var orderIds = await db.TenantBotOrders.Where(x => x.TenantBotId == row.BotId && (x.CustomerTelegramUserId == row.TelegramUserId || x.OwnerTelegramUserId == row.TelegramUserId))
            .OrderByDescending(x => x.Id).Select(x => x.Id).Take(10).ToListAsync(token);
        var evidence = $"creation=[{string.Join(',', states.GroupBy(x => Enum.IsDefined(x) ? x.ToString() : "Unknown").Select(x => x.Key + ":" + x.Count()))}] "
            + $"walletReceipts={receipts.Count} walletBlocked={walletBlocked} renewalPending={renewals} linkPending={links} tenantOrdersPending={orders} settlementsPending={settlements}"
            + $" renewalIds=[{string.Join(',',renewalIds)}] linkIds=[{string.Join(',',linkIds)}] recentTenantOrderIds=[{string.Join(',',orderIds)}]";
        return (!ambiguous && !walletBlocked && renewals == 0 && links == 0 && orders == 0 && settlements == 0, evidence);
    }

    /// <summary>Closes an eligible uncertain row after explicit operator review and preserves its original failure category.</summary>
    /// <param name="sequence">Internal uncertain sequence.</param>
    /// <param name="operatorId">Authenticated global super-admin Telegram id.</param>
    /// <param name="reference">Validated review-N ticket, never free text.</param>
    /// <param name="token">Cancellation of evidence checks and local commit.</param>
    /// <returns>True only if known effects were safe and the row was completed; no handler is replayed.</returns>
    /// <remarks>Positive creation proof and reconciled receipts are monotonic. The single-process scheduler cannot
    /// run this blocked lane during review. Cross-bot financial activity remains independently idempotent.</remarks>
    private async Task<bool> ResolveGuardedAsync(long sequence, long operatorId, string reference, CancellationToken token)
    {
        if (!(await CanResolveAsync(sequence, token)).Allowed) return false;
        var changed = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence && x.Status == "uncertain"
                && !db.XuiV3CreationOperations.Any(c => c.InboxSequence == sequence
                    && c.Outcome != XuiV3CreationOutcome.Applied && c.Outcome != XuiV3CreationOutcome.DefinitiveRejected
                    && c.Outcome != XuiV3CreationOutcome.Reserved))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed").SetProperty(x => x.Payload, (string)null)
                    .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow).SetProperty(x => x.ReviewedAtUtc, DateTime.UtcNow)
                    .SetProperty(x => x.ReviewedByTelegramUserId, operatorId).SetProperty(x => x.ReviewReference, reference), ct) == 1;
        }, token);
        if (changed) NotifyReady();
        return changed;
    }
}
