using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

/// <summary>Sanitized terminal-recovery inspection and conservative business-resolution preconditions.</summary>
/// <remarks>Never returns update/client payloads or financial keys. Exact inbox links are authoritative. A legacy
/// operation without an inbox link is correlated only when its bot/user matches and its creation time falls from five
/// minutes before acceptance through two hours after execution started; older history remains untouched and does not
/// affect a newer Telegram execution.</remarks>
public sealed partial class TelegramUpdateInboxStore
{
    private readonly CredentialsDbContextFactory _credentials;
    /// <summary>Live executions cannot be manually completed while a cancellation-ignoring handler still runs.</summary>
    internal readonly ConcurrentDictionary<long, byte> Executing = new();

    /// <summary>Reads a bounded metadata-only page of terminal Telegram receipts that still need business review.</summary>
    /// <param name="after">Exclusive internal sequence cursor, zero for the first page.</param>
    /// <param name="token">Cancellation of this detached database read.</param>
    /// <returns>At most ten detached metadata rows without private payloads; an empty page means end of results.</returns>
    /// <remarks>Authorization belongs to the operator service; no state changes or external calls occur.</remarks>
    public async Task<List<TelegramUpdateInboxEntry>> ListUncertainAsync(long after, CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramUpdateInbox.AsNoTracking().Where(x =>
                (x.Status == "completed_with_review" || x.Status == "uncertain") && x.Sequence > after)
            .OrderBy(x => x.Sequence).Take(10).Select(x => new TelegramUpdateInboxEntry
            { Sequence = x.Sequence, BotId = x.BotId, TelegramUserId = x.TelegramUserId, UpdateId = x.UpdateId,
                UpdateType = x.UpdateType, Status = x.Status, FailureCode = x.FailureCode,
                AcceptedAtUtc = x.AcceptedAtUtc, StartedAtUtc = x.StartedAtUtc }).ToListAsync(token);
    }

    /// <summary>Measures terminal recovery receipts and their oldest acceptance time without reading payloads.</summary>
    /// <param name="token">Cancellation of metadata queries.</param>
    /// <returns>Recovery receipt count, distinct affected bot/user count, and oldest age in seconds, zero when none exist.</returns>
    /// <remarks>The affected-user count is diagnostic only and never participates in admission or scheduling.</remarks>
    public async Task<(int Count, int Lanes, double AgeSeconds)> ReadUncertainSummaryAsync(CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        var query = db.TelegramUpdateInbox.Where(x => x.Status == "completed_with_review" || x.Status == "uncertain");
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
        var row = await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence &&
                (x.Status == "completed_with_review" || x.Status == "uncertain"))
            .Select(x => new { x.BotId, x.TelegramUserId, x.AcceptedAtUtc, x.StartedAtUtc }).SingleOrDefaultAsync(token);
        if (row == null) return (false, "not_uncertain");
        if (Executing.ContainsKey(sequence)) return (false, "handler_still_active");
        var states = await db.XuiV3CreationOperations.Where(x => x.InboxSequence == sequence)
            .Select(x => x.Outcome).ToListAsync(token);
        var ambiguous = states.Any(x => x is not (XuiV3CreationOutcome.Reserved or XuiV3CreationOutcome.Applied or XuiV3CreationOutcome.DefinitiveRejected));
        var legacyFromUtc = row.AcceptedAtUtc.AddMinutes(-5);
        var legacyThroughUtc = (row.StartedAtUtc ?? row.AcceptedAtUtc).AddHours(2);
        var exactRenewals = await db.XuiV3RenewalOperations.AsNoTracking()
            .Where(x => x.InboxSequence == sequence && x.Status != "failed" &&
                (x.Status != "applied" || x.SettlementStatus != "settled"))
            .Select(x => new { x.Id, x.Status }).Take(10).ToListAsync(token);
        var legacyRenewals = await db.XuiV3RenewalOperations.AsNoTracking()
            .Where(x => x.InboxSequence == null && x.BotId == row.BotId && x.TelegramUserId == row.TelegramUserId &&
                x.CreatedAtUtc >= legacyFromUtc && x.CreatedAtUtc <= legacyThroughUtc && x.Status != "failed" &&
                (x.Status != "applied" || x.SettlementStatus != "settled"))
            .Select(x => new { x.Id, x.Status }).Take(10).ToListAsync(token);
        var exactLinks = await db.XuiV3LinkChangeOperations.AsNoTracking()
            .Where(x => x.InboxSequence == sequence && x.Status != "succeeded" && x.Status != "failed_before_mutation" &&
                x.Status != "cancelled" && x.Status != "expired")
            .Select(x => new { x.Id, x.Status }).Take(10).ToListAsync(token);
        var legacyLinks = await db.XuiV3LinkChangeOperations.AsNoTracking()
            .Where(x => x.InboxSequence == null && x.BotId == row.BotId && x.TelegramUserId == row.TelegramUserId &&
                x.CreatedAtUtc >= legacyFromUtc && x.CreatedAtUtc <= legacyThroughUtc && x.Status != "succeeded" &&
                x.Status != "failed_before_mutation" && x.Status != "cancelled" && x.Status != "expired")
            .Select(x => new { x.Id, x.Status }).Take(10).ToListAsync(token);

        // A tenant order is exactly related through its durable tenant-create:{orderId} business key, even when the
        // uncertain Telegram actor is the Sales Assistant administrator rather than the customer or tenant owner.
        var tenantOrderIds = states.Count == 0
            ? new List<int>()
            : (await db.XuiV3CreationOperations.AsNoTracking().Where(x => x.InboxSequence == sequence)
                .Select(x => x.OperationKey).ToListAsync(token))
                .Select(TryParseTenantOrderId).Where(x => x.HasValue).Select(x => x.Value).Distinct().ToList();
        var orders = await db.TenantBotOrders.AsNoTracking()
            .Where(x => tenantOrderIds.Contains(x.Id) && !x.IsFulfilled && x.PaymentStatus != "receipt_rejected")
            .Select(x => x.Id).Take(10).ToListAsync(token);
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
        var renewalPending = exactRenewals.Count + legacyRenewals.Count;
        var linkPending = exactLinks.Count + legacyLinks.Count;
        var evidence = $"creation=[{string.Join(',', states.GroupBy(x => Enum.IsDefined(x) ? x.ToString() : "Unknown").Select(x => x.Key + ":" + x.Count()))}] "
            + $"walletReceipts={receipts.Count} walletBlocked={walletBlocked} renewalPending={renewalPending} "
            + $"exactRenewalPending={exactRenewals.Count} legacyCorrelatedRenewalPending={legacyRenewals.Count} "
            + $"linkPending={linkPending} exactLinkPending={exactLinks.Count} legacyCorrelatedLinkPending={legacyLinks.Count} "
            + $"tenantOrdersPending={orders.Count} settlementsPending={settlements} "
            + $"exactRenewals=[{FormatOperations(exactRenewals.Select(x => (x.Id, x.Status)))}] "
            + $"legacyRenewals=[{FormatOperations(legacyRenewals.Select(x => (x.Id, x.Status)))}] "
            + $"exactLinks=[{FormatOperations(exactLinks.Select(x => (x.Id, x.Status)))}] "
            + $"legacyLinks=[{FormatOperations(legacyLinks.Select(x => (x.Id, x.Status)))}] "
            + $"tenantOrderIds=[{string.Join(',', orders)}]";
        return (!ambiguous && !walletBlocked && renewalPending == 0 && linkPending == 0 && orders.Count == 0 && settlements == 0, evidence);
    }

    /// <summary>Parses only the documented tenant creation key prefix without accepting Telegram input.</summary>
    /// <param name="operationKey">Persisted business key loaded from the exact inbox-linked creation row.</param>
    /// <returns>The internal tenant order id, or null for non-tenant and malformed keys.</returns>
    /// <remarks>Both the original and <c>:retry:N</c> keys map to the same paid order.</remarks>
    private static int? TryParseTenantOrderId(string operationKey)
    {
        const string prefix = "tenant-create:";
        if (operationKey?.StartsWith(prefix, StringComparison.Ordinal) != true)
            return null;
        var idText = operationKey[prefix.Length..].Split(':')[0];
        return int.TryParse(idText, out var id) && id > 0 ? id : null;
    }

    /// <summary>Formats bounded numeric ids and fixed status categories for operator evidence.</summary>
    /// <param name="operations">Detached numeric identifiers and known persisted status values.</param>
    /// <returns>A comma-separated safe list; unknown categories are rendered as <c>other</c>.</returns>
    /// <remarks>No operation key, account identity, panel value, or customer payload enters this output.</remarks>
    private static string FormatOperations(IEnumerable<(int Id, string Status)> operations) => string.Join(',',
        operations.Select(x => $"{x.Id}:{SanitizeStatus(x.Status)}"));

    /// <summary>Restricts status evidence to non-sensitive ASCII state-machine categories.</summary>
    /// <param name="status">Persisted status, potentially from a historical database.</param>
    /// <returns>A bounded known-safe category or <c>other</c>.</returns>
    private static string SanitizeStatus(string status) => status is
        "pending" or "processing" or "applied" or "ambiguous" or "failed" or "manual_review" or
        "awaiting_confirmation" or "recovery_pending" or "succeeded" or "failed_before_mutation" or
        "cancelled" or "expired" ? status : "other";

    /// <summary>Marks an eligible terminal recovery receipt resolved after explicit operator review.</summary>
    /// <param name="sequence">Internal uncertain sequence.</param>
    /// <param name="operatorId">Authenticated global super-admin Telegram id.</param>
    /// <param name="reference">Validated review-N ticket, never free text.</param>
    /// <param name="token">Cancellation of evidence checks and local commit.</param>
    /// <returns>True only if known effects were safe and the row was completed; no handler is replayed.</returns>
    /// <remarks>Positive creation proof and reconciled receipts are monotonic. Telegram scheduling does not consult
    /// this review state. Cross-bot financial activity remains independently idempotent.</remarks>
    private async Task<bool> ResolveGuardedAsync(long sequence, long operatorId, string reference, CancellationToken token)
    {
        if (!(await CanResolveAsync(sequence, token)).Allowed) return false;
        var changed = await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.TelegramUpdateInbox.Where(x => x.Sequence == sequence &&
                (x.Status == "completed_with_review" || x.Status == "uncertain")
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
