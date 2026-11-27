using System.Net;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;

/// <summary>Manages issuance-time campaign promises and recovers official bonus credits without calling payment providers.</summary>
internal static class WalletChargeCampaignService
{
    /// <summary>Stable credentials.db and ledger key namespace reserved for campaign credits.</summary>
    internal const string BonusOperationPrefix = "wallet-charge-bonus:";

    /// <summary>Identifies the single platform wallet audience, independently of Telegram conversation state.</summary>
    /// <param name="purpose">Immutable purpose from a persisted provider payment; only wallet_charge qualifies.</param>
    /// <param name="botType">Persisted origin family; only owned qualifies.</param>
    /// <param name="botId">Internal originating bot id.</param>
    /// <param name="defaultBotId">Exact configured main owned bot id, not a fallback lookup.</param>
    /// <param name="isColleague">Fresh global credentials role at issuance.</param>
    /// <returns>True for the main owned bot or an owned-bot colleague, only for wallet charges.</returns>
    internal static bool IsEligible(string purpose, string botType, string botId, string defaultBotId, bool isColleague) =>
        purpose == TenantBotPaymentPurposes.WalletCharge && botType == BotInstanceTypes.Owned &&
        !string.IsNullOrWhiteSpace(botId) &&
        (isColleague || string.Equals(botId, defaultBotId, StringComparison.Ordinal));

    /// <summary>Reads a campaign offer without freezing an invoice or giving credit.</summary>
    /// <param name="settings">Global users.db settings; a missing row is equivalent to a paused campaign.</param>
    /// <param name="principal">Positive proposed wallet principal in whole toman; gateway fees are excluded.</param>
    /// <returns>Frozen-name/rate/amount values when valid, otherwise a zero-bonus preview.</returns>
    /// <remarks>Decimal arithmetic avoids long overflow during multiplication and floors fractions to whole toman.</remarks>
    internal static WalletChargeBonus Quote(WalletChargeCampaign settings, long principal)
    {
        var result = new WalletChargeBonus { PrincipalToman = principal, Name = string.Empty };
        if (settings is not { Enabled: true, Percent: >= 1 and <= 100 } ||
            string.IsNullOrWhiteSpace(settings.Name) || principal <= 0)
            return result;
        var bonus = (long)decimal.Floor((decimal)principal * settings.Percent / 100);
        if (bonus <= 0 || principal > long.MaxValue - bonus)
            return result;
        result.Name = settings.Name;
        result.Percent = settings.Percent;
        result.BonusToman = bonus;
        return result;
    }

    /// <summary>Formats the frozen or previewed campaign name, rate and all three amounts for Telegram.</summary>
    /// <param name="bonus">Invoice promise or preview; null/zero returns an empty string.</param>
    /// <param name="html">Whether the caller uses Telegram HTML. Only the editable name needs escaping.</param>
    /// <returns>Text suitable for appending to a charge preview, invoice or final receipt.</returns>
    internal static string Describe(WalletChargeBonus bonus, bool html = false) =>
        bonus is not { BonusToman: > 0 } ? string.Empty :
        $"\n\n🎁 {(html ? WebUtility.HtmlEncode(bonus.Name) : bonus.Name)} — هدیه {bonus.Percent}٪\n" +
        $"💳 اصل شارژ: {bonus.PrincipalToman.FormatCurrency()}\n" +
        $"🎁 هدیه: {bonus.BonusToman.FormatCurrency()}\n" +
        $"💰 جمع اعتبار: {(bonus.PrincipalToman + bonus.BonusToman).FormatCurrency()}";

    /// <summary>Stages the normal first-credit outbox or makes its official campaign receipt recoverable.</summary>
    /// <param name="workflow">The same users.db workflow that will save the principal payment marker.</param>
    /// <param name="notification">Existing principal notification with authoritative provider, origin and wallet identity.</param>
    /// <param name="receipt">Immutable credentials.db proof that the matching principal actually committed.</param>
    /// <param name="token">Cancellation of the campaign snapshot lookup.</param>
    /// <returns>A task staging the relevant changes; the caller must commit its workflow once.</returns>
    /// <remarks>Call only on the already verified official path. A provisional receipt encountered here can be
    /// confirmed by that path even when its users.db marker has not yet recovered. Partial credit never qualifies.
    /// For a positive offer, only the combined final receipt is queued after the bonus commits.</remarks>
    internal static async Task StageOfficialCreditAsync(UserWorkflowStore workflow,
        PaymentSettlementNotification notification, WalletOperation receipt, CancellationToken token)
    {
        if (notification.WalletOriginBotType != BotInstanceTypes.Owned || receipt.ApprovalKind is not ("official" or "provisional") ||
            !MarkReady(await workflow.ReadAsync(db => FindAsync(db, notification.Provider, notification.ProviderPaymentId, token)),
                receipt, notification.BotId, receipt.ApprovalKind == "provisional"))
            workflow.Add(notification);
    }

    /// <summary>Stages bonus eligibility when an existing provisional payment receives official provider confirmation.</summary>
    /// <param name="workflow">Current users.db workflow committing verified provider confirmation.</param>
    /// <param name="credentials">Global wallet store used only to read the original provisional credit receipt.</param>
    /// <param name="provider">Canonical central provider key.</param>
    /// <param name="id">Positive local id of the same provider payment.</param>
    /// <param name="botId">Immutable origin of the verified payment.</param>
    /// <param name="token">Cancellation of both local reads.</param>
    /// <returns>A task staging the positive promise, if any. The caller must save it with the confirmation.</returns>
    /// <remarks>Must only be called after the provider-specific confirmation check. No money moves in this workflow.</remarks>
    internal static async Task StageProvisionalConfirmationAsync(UserWorkflowStore workflow, CredentialsStore credentials,
        string provider, int id, string botId, CancellationToken token)
    {
        var bonus = await workflow.ReadAsync(db => FindAsync(db, provider, id, token));
        if (bonus is not { BonusToman: > 0 }) return;
        var receipt = await credentials.GetWalletOperationAsync($"payment:{provider}:{id}:credit", token);
        if (receipt?.ApprovalKind == "provisional") MarkReady(bonus, receipt, botId, true);
    }

    /// <summary>Finds a promise by the option's exact provider and local payment id.</summary>
    /// <param name="db">Current users.db workflow or reconciliation transaction.</param>
    /// <param name="provider">Canonical provider key.</param>
    /// <param name="id">Positive local provider payment id.</param>
    /// <param name="token">Cancellation of the query.</param>
    /// <returns>Tracked according to the caller's context, or null for a historical or non-owned invoice.</returns>
    internal static Task<WalletChargeBonus> FindAsync(UserDbContext db, string provider, int id, CancellationToken token) =>
        db.WalletChargeBonuses.SingleOrDefaultAsync(x => x.Provider == provider && x.PaymentId == id, token);

    /// <summary>Marks a matched invoice promise due without crediting the principal or the bonus.</summary>
    /// <param name="bonus">Tracked or workflow-owned invoice promise, possibly null.</param>
    /// <param name="receipt">Required durable principal receipt, not webhook fields or current balance.</param>
    /// <param name="botId">Immutable origin of the payment being settled.</param>
    /// <param name="confirmedProvisional">True only on the provider-specific official confirmation path.</param>
    /// <returns>True if a positive matched promise owns the combined final receipt; false preserves the original notification.</returns>
    /// <remarks>Used both in live settlement and recovery. Duplicate evidence never resets completion or backoff.</remarks>
    internal static bool MarkReady(WalletChargeBonus bonus, WalletOperation receipt, string botId, bool confirmedProvisional)
    {
        if (bonus is not { BonusToman: > 0 } || !MatchesPrincipal(bonus, receipt, botId, confirmedProvisional))
            return false;
        if (!bonus.CompletedAtUtc.HasValue)
        {
            bonus.ProvisionalConfirmed |= confirmedProvisional;
            bonus.NextAttemptAtUtc ??= DateTime.UtcNow;
        }
        return true;
    }

    /// <summary>Validates a principal receipt against every immutable wallet identity and its approval kind.</summary>
    /// <param name="bonus">Required persisted positive promise.</param>
    /// <param name="receipt">Committed principal receipt, possibly absent.</param>
    /// <param name="botId">Expected exact internal owned origin.</param>
    /// <param name="confirmedProvisional">Whether this exact provisional payment received recorded official confirmation.</param>
    /// <returns>True only for the original exact charge with official or explicitly confirmed provisional authority.</returns>
    internal static bool MatchesPrincipal(WalletChargeBonus bonus, WalletOperation receipt, string botId, bool confirmedProvisional) =>
        receipt != null && bonus.BotId == botId && receipt.BotId == botId &&
        receipt.OperationKey == $"payment:{bonus.Provider}:{bonus.PaymentId}:credit" &&
        receipt.TelegramUserId == bonus.TelegramUserId && receipt.AmountToman == bonus.PrincipalToman &&
        receipt.ApprovalKind == (confirmedProvisional ? "provisional" : "official");

    /// <summary>Recovers one bounded batch of verified bonus credits and their ledger and combined outbox receipts.</summary>
    /// <param name="users">users.db factory for due promises, their completion and delivery outbox.</param>
    /// <param name="credentials">Global wallet database factory for original and bonus receipts.</param>
    /// <param name="ledger">Idempotent users.db ledger service; uses the same named credential receipt.</param>
    /// <param name="logger">Operational diagnostics; no user, campaign text or payment payload is logged.</param>
    /// <param name="token">Worker cancellation during local database operations only.</param>
    /// <returns>Number of due promises durably completed, including recovery after a previous wallet commit.</returns>
    /// <remarks>No provider or Telegram I/O. The principal is never replayed. A named positive credit in credentials.db
    /// precedes the users.db ledger/outbox; retry reuses the same receipt and cannot increase the wallet twice.</remarks>
    internal static async Task<int> ReconcileAsync(UserDbContextFactory users, CredentialsDbContextFactory credentials,
        WalletLedgerService ledger, ILogger logger, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        List<WalletChargeBonus> due;
        await using (var db = users.CreateDbContext())
            due = await db.WalletChargeBonuses.AsNoTracking()
                .Where(x => x.CompletedAtUtc == null && x.NextAttemptAtUtc <= now && x.BonusToman > 0)
                .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.Provider).ThenBy(x => x.PaymentId).Take(50).ToListAsync(token);
        var store = new CredentialsStore(credentials);
        var completed = 0;
        foreach (var bonus in due)
        {
            try
            {
                var reference = $"{bonus.Provider}:{bonus.PaymentId}";
                var principal = await store.GetWalletOperationAsync($"payment:{reference}:credit", token);
                if (!MatchesPrincipal(bonus, principal, bonus.BotId, bonus.ProvisionalConfirmed))
                    throw new InvalidOperationException("The campaign promise has no matching authorized principal receipt.");
                var key = BonusOperationPrefix + reference;
                var gift = await store.MutateWalletAsync(bonus.TelegramUserId, bonus.BonusToman, key, token, botId: bonus.BotId);
                if (gift == null || gift.BotId != bonus.BotId || gift.TelegramUserId != bonus.TelegramUserId ||
                    gift.AmountToman != bonus.BonusToman)
                    throw new InvalidOperationException("The committed campaign receipt did not match the promise.");
                await ledger.RecordAsync(bonus.TelegramUserId, WalletLedgerDirections.Credit, bonus.BonusToman,
                    gift.BeforeBalance, gift.AfterBalance, WalletLedgerReasons.WalletChargeBonus, provider: bonus.Provider,
                    referenceType: "wallet-charge-bonus", referenceId: bonus.PaymentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    description: bonus.Name, botId: bonus.BotId, botType: BotInstanceTypes.Owned, idempotencyKey: key, cancellationToken: token);

                // Only this ledger-aware path acknowledges the gift receipt; the generic worker ignores its namespace.
                await using (var walletDb = credentials.CreateDbContext())
                {
                    var reconciledAt = DateTime.UtcNow;
                    await walletDb.WalletOperations.Where(x => x.OperationKey == key && x.ReconciledAtUtc == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReconciledAtUtc, reconciledAt), token);
                }

                // Commit completion and delivery together; a second worker sees completion even after a process restart.
                completed += await SqliteOperation.RunAsync(async ct =>
                {
                    await using var db = users.CreateDbContext();
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var current = await FindAsync(db, bonus.Provider, bonus.PaymentId, ct);
                    if (current.CompletedAtUtc.HasValue) return 0;
                    var notification = PaymentSettlementNotification.CreateWalletCredit(bonus.Provider, bonus.PaymentId, bonus.BotId,
                        bonus.TelegramUserId, bonus.ChatId, checked(bonus.PrincipalToman + bonus.BonusToman),
                        "✅ شارژ کیف پول و هدیه کمپین ثبت شد." + Describe(bonus), DateTime.UtcNow, BotInstanceTypes.Owned, gift.AfterBalance);
                    if (bonus.ProvisionalConfirmed) notification.NotificationKey += ":campaign-confirmed";
                    db.PaymentSettlementNotifications.Add(notification);
                    current.CompletedAtUtc = DateTime.UtcNow;
                    current.NextAttemptAtUtc = null;
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return 1;
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Wallet campaign bonus remains pending. Provider={Provider} PaymentId={PaymentId} ErrorType={ErrorType}",
                    bonus.Provider, bonus.PaymentId, ex.GetType().Name);
                await using var db = users.CreateDbContext();
                var retryAt = DateTime.UtcNow.AddMinutes(5);
                await db.WalletChargeBonuses.Where(x => x.Provider == bonus.Provider && x.PaymentId == bonus.PaymentId && x.CompletedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, retryAt), token);
            }
        }
        return completed;
    }
}
