using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Atomically reserves restart-safe colleague trial slots in the global owned-bot Tehran-day allowance.</summary>
/// <remarks>
/// Callers must verify the live credentials profile is a colleague and the current runtime bot is owned before
/// reserving; this store does not infer eligibility from stale roles or tenant conversations. Every local operation
/// owns a fresh users.db context and a short writer transaction through SqliteOperation. No network, Telegram,
/// wallet, ledger, order or partner-profit operation is performed while that connection or transaction is held.
/// </remarks>
public sealed class ColleagueTrialQuotaStore
{
    /// <summary>Factory supplying one independently disposed users.db context per local contention retry.</summary>
    private readonly UserDbContextFactory _factory;

    /// <summary>Linux/Windows Tehran timezone with the same modern fixed-offset fallback as usage analytics.</summary>
    private static readonly TimeZoneInfo IranTimeZone = ResolveIranTimeZone();

    /// <summary>Creates the quota store without retaining an EF change tracker or open connection.</summary>
    /// <param name="factory">Required users.db factory; not a credentials.db or tenant-specific database factory.</param>
    /// <remarks>Register with the shared users.db factory so all owned bots use one durable global quota.</remarks>
    /// <exception cref="ArgumentNullException">The factory is missing.</exception>
    public ColleagueTrialQuotaStore(UserDbContextFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Returns the original delivery receipt or atomically reserves one slot in the colleague's Tehran day.</summary>
    /// <param name="telegramUserId">Positive global Telegram sender id from the authenticated update, not a chat id.</param>
    /// <param name="botId">Required originating owned-bot internal runtime id, at most 64 characters; not its Telegram id.</param>
    /// <param name="serviceKey">Required normalized trial service key, exactly <c>national</c> or <c>normal</c>.</param>
    /// <param name="deliveryRequestKey">
    /// Required globally stable, credential-free delivery key of at most 240 characters, built from internal bot id,
    /// Telegram chat/message identifiers or a durable inbox sequence. Reuse it unchanged on every redelivery; never
    /// derive it from wall-clock time, mutable conversation state, free-form text, or the selected service alone.
    /// </param>
    /// <param name="dailyLimit">Nonnegative configured account count shared across bots and services; zero denies new freebies.</param>
    /// <param name="nowUtc">UTC-kind reservation instant; its Tehran calendar date, not its UTC date, determines capacity.</param>
    /// <param name="cancellationToken">Cancellation of local admission and contention delays before external creation.</param>
    /// <returns>
    /// Detached persisted receipt, never null. A new Reserved receipt holds capacity; Denied consumes none. Existing
    /// receipts keep their original date, operation key and status even when the day or configured limit changes.
    /// Released and Denied receipts forbid another attempt; Uncertain and Consumed allow only identity-safe recovery
    /// through the same creation operation key. No additional EF save is required and no private account data is returned.
    /// </returns>
    /// <remarks>
    /// Reserved, Uncertain and Consumed receipts count together per Telegram user and Tehran date, regardless of
    /// origin bot or service. A first no-op UPDATE takes SQLite's writer lock before checking deduplication/capacity,
    /// including on an empty table, so independent contexts, bots and restarts cannot over-admit. The unique delivery
    /// key enforces event identity and the unique operation key identifies exactly one creation attempt. Denials are
    /// retained too, preventing the same exhausted event from becoming a new grant after midnight. This method
    /// never authorizes execution: first win TryStartFreeCreationAsync before progress notification or panel work,
    /// then pass OperationKey to the existing creation-operation store's independent one-POST boundary.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram id is not positive or the daily limit is negative.</exception>
    /// <exception cref="ArgumentException">Identifiers, trial service or the UTC-kind timestamp are invalid.</exception>
    /// <exception cref="InvalidOperationException">A delivery key was reused for another sender, origin bot or service.</exception>
    /// <example><code>
    /// var grant = await quotas.ReserveAsync(sender.Id, botId, "normal",
    ///     $"trial-delivery:{botId}:{message.Chat.Id}:{message.Id}", dailyLimit, DateTime.UtcNow, token);
    /// if (grant.State == ColleagueTrialGrantState.Reserved &amp;&amp;
    ///     await quotas.TryStartFreeCreationAsync(grant.Id, sender.Id, botId, DateTime.UtcNow, token))
    /// {
    ///     // After this method returns, call CreateTrialAccountAsync with grant.OperationKey.
    ///     // After creation finishes, settle locally before sending any Telegram success message.
    /// }
    /// </code></example>
    public Task<ColleagueTrialGrant> ReserveAsync(
        long telegramUserId,
        string botId,
        string serviceKey,
        string deliveryRequestKey,
        int dailyLimit,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (telegramUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(telegramUserId), "A positive Telegram user id is required.");
        if (dailyLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(dailyLimit), "The daily account limit cannot be negative.");
        ValidateKey(botId, 64, nameof(botId));
        ValidateKey(deliveryRequestKey, 240, nameof(deliveryRequestKey));
        if (serviceKey is not ("national" or "normal"))
            throw new ArgumentException("Only national and normal trial services are supported.", nameof(serviceKey));
        ValidateUtc(nowUtc);
        var grantDateIran = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, IranTimeZone).Date;

        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Even a zero-row UPDATE acquires SQLite's writer lock before the first read. Do not move this
            // below the duplicate lookup or count: deferred read snapshots cannot safely admit concurrently.
            await db.ColleagueTrialGrants.Where(x => x.TelegramUserId == telegramUserId && x.GrantDateIran == grantDateIran)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, x => x.State), ct);
            var original = await db.ColleagueTrialGrants.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DeliveryRequestKey == deliveryRequestKey, ct);
            if (original != null)
            {
                if (original.TelegramUserId != telegramUserId || original.BotId != botId || original.ServiceKey != serviceKey)
                    throw new InvalidOperationException("The colleague trial delivery key belongs to another immutable request.");
                await transaction.CommitAsync(ct);
                return original;
            }

            var occupied = dailyLimit == 0 ? 0 : await db.ColleagueTrialGrants.CountAsync(x =>
                x.TelegramUserId == telegramUserId && x.GrantDateIran == grantDateIran &&
                x.State != ColleagueTrialGrantState.Released && x.State != ColleagueTrialGrantState.Denied, ct);
            var id = Guid.NewGuid().ToString("N");
            var grant = new ColleagueTrialGrant
            {
                Id = id,
                TelegramUserId = telegramUserId,
                BotId = botId,
                ServiceKey = serviceKey,
                DeliveryRequestKey = deliveryRequestKey,
                OperationKey = $"colleague-trial:{id}",
                GrantDateIran = grantDateIran,
                State = dailyLimit > occupied ? ColleagueTrialGrantState.Reserved : ColleagueTrialGrantState.Denied,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            db.ColleagueTrialGrants.Add(grant);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return grant;
        }, cancellationToken);
    }

    /// <summary>Atomically grants exactly one free creation executor before progress notification or panel provisioning.</summary>
    /// <param name="grantId">Required immutable users.db grant id returned by free quota reservation.</param>
    /// <param name="telegramUserId">Positive global Telegram sender id authenticated by the owned-bot update.</param>
    /// <param name="botId">Required exact originating internal owned-bot runtime id, not a Telegram bot id.</param>
    /// <param name="nowUtc">UTC-kind instant when the single executor marker is durably set.</param>
    /// <param name="cancellationToken">Cancellation of local authorization before any Telegram or panel request starts.</param>
    /// <returns>True only to the caller changing Reserved/FreeCreationStarted=false to true; false forbids new execution and winner-only release.</returns>
    /// <remarks>
    /// Claim before even sending a progress message, whose failure might otherwise release a slot while another
    /// duplicate prepares POST. The marker never resets, including on Released. Only the winning invocation may
    /// settle absent/Reserved creation after its progress/create invocation quiesces. Losers/restarts may reconcile
    /// existing PostStarted/Ambiguous/Applied/DefinitiveRejected evidence only; absent/Reserved with a true marker stays
    /// held and review-only. This claim owns no network/wallet I/O and is not the separate creation-store POST grant.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram sender id is not positive.</exception>
    /// <exception cref="ArgumentException">An identifier or UTC-kind instant is invalid.</exception>
    /// <example><code>
    /// if (await quotas.TryStartFreeCreationAsync(grant.Id, sender.Id, botId, DateTime.UtcNow, token))
    /// {
    ///     // This winner alone sends progress, invokes trial creation, and settles after that invocation stops.
    /// }
    /// </code></example>
    public Task<bool> TryStartFreeCreationAsync(
        string grantId,
        long telegramUserId,
        string botId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        ValidateUtc(nowUtc);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.ColleagueTrialGrants.Where(x => x.Id == grantId && x.TelegramUserId == telegramUserId &&
                x.BotId == botId && x.State == ColleagueTrialGrantState.Reserved && !x.FreeCreationStarted)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.FreeCreationStarted, true)
                    .SetProperty(x => x.UpdatedAtUtc, nowUtc), ct) == 1;
        }, cancellationToken);
    }

    /// <summary>Reads a delivery receipt for exactly its authenticated Telegram sender and originating owned bot.</summary>
    /// <param name="grantId">Required immutable 32-character users.db grant id returned by admission, not a Telegram id.</param>
    /// <param name="telegramUserId">Positive global Telegram sender id from the live authenticated update.</param>
    /// <param name="botId">Required originating internal owned-bot runtime id; another bot cannot access this offer.</param>
    /// <param name="cancellationToken">Cancellation of the isolated local read.</param>
    /// <returns>Detached exact actor/bot receipt, or null when missing or not owned by that actor/bot; no save is required.</returns>
    /// <remarks>Caller still verifies live colleague eligibility and owned runtime. The read does not allocate quota or expose private account data.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram sender id is not positive.</exception>
    /// <exception cref="ArgumentException">A grant id or bot id is missing, padded or oversized.</exception>
    /// <example><code>var offer = await quotas.FindAsync(grantId, callback.From.Id, botId, token);</code></example>
    public Task<ColleagueTrialGrant> FindAsync(
        string grantId,
        long telegramUserId,
        string botId,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.ColleagueTrialGrants.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == grantId && x.TelegramUserId == telegramUserId && x.BotId == botId, ct);
        }, cancellationToken);
    }

    /// <summary>Persists the explicit paid-test offer price for an exhausted delivery request before any wallet debit.</summary>
    /// <param name="grantId">Required immutable users.db grant id used by the caller as its paid session identity.</param>
    /// <param name="telegramUserId">Positive global Telegram sender id authenticated by the current owned-bot update.</param>
    /// <param name="botId">Required exact originating internal owned-bot runtime id, not the Telegram bot id.</param>
    /// <param name="priceToman">Positive colleague price snapshot in whole Iranian toman; no fractional or zero price is admitted.</param>
    /// <param name="nowUtc">UTC-kind instant when the paid price snapshot is saved.</param>
    /// <param name="cancellationToken">Cancellation of the short local price write and contention retries.</param>
    /// <returns>Detached updated Denied/NotStarted receipt, or null for a missing, mismatched or already-started receipt; no extra save is required.</returns>
    /// <remarks>
    /// Only Denied requests without a paid executor receive quotes; no admitted free trial or already-started paid attempt is repriced.
    /// The caller must calculate live colleague rates, invoke this before any debit, preview this snapshot and obtain
    /// explicit confirmation. This local store does not inspect credentials.db receipts, debit/refund a wallet, create
    /// an order, or authorize paid provisioning. Paid creation uses its separate colleague-paid-trial:{Id} identity.
    /// The free quota state, grant date and free creation operation key are unchanged by a quote.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram id or whole-toman price is not positive.</exception>
    /// <exception cref="ArgumentException">An identifier or the UTC-kind timestamp is invalid.</exception>
    /// <example><code>var offer = await quotas.SetPaidQuoteAsync(grant.Id, sender.Id, botId, colleaguePrice, DateTime.UtcNow, token);</code></example>
    public Task<ColleagueTrialGrant> SetPaidQuoteAsync(
        string grantId,
        long telegramUserId,
        string botId,
        long priceToman,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        if (priceToman <= 0)
            throw new ArgumentOutOfRangeException(nameof(priceToman), "A positive whole-toman price is required.");
        ValidateUtc(nowUtc);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.ColleagueTrialGrants.Where(x =>
                x.Id == grantId && x.TelegramUserId == telegramUserId && x.BotId == botId &&
                x.State == ColleagueTrialGrantState.Denied && x.PaidCreationState == ColleagueTrialPaidCreationState.NotStarted)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaidQuoteToman, priceToman)
                    .SetProperty(x => x.UpdatedAtUtc, nowUtc), ct);
            var grant = changed == 0 ? null : await db.ColleagueTrialGrants.AsNoTracking()
                .SingleAsync(x => x.Id == grantId, ct);
            await transaction.CommitAsync(ct);
            return grant;
        }, cancellationToken);
    }

    /// <summary>Cancels an unfunded paid preview without deleting its delivery receipt or quota history.</summary>
    /// <param name="grantId">Required immutable users.db grant id identifying the paid preview.</param>
    /// <param name="telegramUserId">Positive global Telegram sender id authenticated by the current owned-bot action.</param>
    /// <param name="botId">Required exact originating internal owned-bot runtime id.</param>
    /// <param name="cancellationToken">Cancellation of the isolated local preview cancellation.</param>
    /// <returns>True when an exact Denied/NotStarted nonnull quote was cleared; false for missing/mismatched, already-cleared or started receipts.</returns>
    /// <remarks>
    /// The caller must first prove no committed debit receipt exists; this method does not query credentials.db or
    /// refund a wallet. Clear before abandoning an unfunded offer so an old inline confirmation cannot initiate a
    /// fresh debit from a cancelled quote. Free quota state and immutable event/creation identities remain unchanged.
    /// Started and all resolved paid creation states preserve their authoritative receipt price.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram sender id is not positive.</exception>
    /// <exception cref="ArgumentException">An identifier is missing, padded or oversized.</exception>
    /// <example><code>await quotas.ClearPaidQuoteAsync(grant.Id, sender.Id, botId, token);</code></example>
    public Task<bool> ClearPaidQuoteAsync(
        string grantId,
        long telegramUserId,
        string botId,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        var nowUtc = DateTime.UtcNow;
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.ColleagueTrialGrants.Where(x => x.Id == grantId && x.TelegramUserId == telegramUserId &&
                x.BotId == botId && x.State == ColleagueTrialGrantState.Denied &&
                x.PaidCreationState == ColleagueTrialPaidCreationState.NotStarted && x.PaidQuoteToman != null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaidQuoteToman, (long?)null)
                    .SetProperty(x => x.UpdatedAtUtc, nowUtc), ct) == 1;
        }, cancellationToken);
    }

    /// <summary>Records completed exact debit-linked refund credit and ledger proof once, independently of conversation state.</summary>
    /// <param name="grantId">Required immutable users.db grant id linking the rejected paid operation and its debit/refund receipts.</param>
    /// <param name="telegramUserId">Positive global Telegram account owner loaded from the authenticated action or retained grant.</param>
    /// <param name="botId">Required exact originating internal owned-bot runtime id loaded from the retained grant.</param>
    /// <param name="nowUtc">UTC-kind proof-recording instant after verified receipt-linked credit and ledger both succeeded.</param>
    /// <param name="cancellationToken">Independent local-persistence cancellation of the completion marker.</param>
    /// <returns>True only when exact Denied/Rejected/null-proof changed to the supplied timestamp; false leaves existing proof or ineligible receipts unchanged.</returns>
    /// <remarks>
    /// The caller must verify the exact committed debit receipt, its idempotent matching credit and its users.db
    /// ledger before marking. This method performs no credentials, network, wallet or ledger I/O and does not infer
    /// refund success from a missing creation operation or cleared conversation. Rejected/null-proof receipts remain
    /// recovery candidates through /start, navigation and restart. Duplicate marking never refreshes the timestamp,
    /// reprices an account, changes quota state or reauthorizes a POST.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The global Telegram account owner id is not positive.</exception>
    /// <exception cref="ArgumentException">An identifier or UTC-kind proof timestamp is invalid.</exception>
    /// <example><code>
    /// // After the exact receipt-linked credit and ledger are verified:
    /// await quotas.MarkPaidRefundRecordedAsync(grant.Id, grant.TelegramUserId, grant.BotId, DateTime.UtcNow, token);
    /// </code></example>
    public Task<bool> MarkPaidRefundRecordedAsync(
        string grantId,
        long telegramUserId,
        string botId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        ValidateUtc(nowUtc);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.ColleagueTrialGrants.Where(x => x.Id == grantId && x.TelegramUserId == telegramUserId &&
                x.BotId == botId && x.State == ColleagueTrialGrantState.Denied &&
                x.PaidCreationState == ColleagueTrialPaidCreationState.Rejected && x.PaidRefundRecordedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaidRefundRecordedAtUtc, nowUtc)
                    .SetProperty(x => x.UpdatedAtUtc, nowUtc), ct) == 1;
        }, cancellationToken);
    }

    /// <summary>Atomically grants one paid creation executor and freezes the authoritative committed debit amount.</summary>
    /// <param name="grantId">Required immutable users.db receipt id shared by the paid session and debit receipt identity.</param>
    /// <param name="telegramUserId">Positive global Telegram sender id from the authenticated owned-bot confirmation.</param>
    /// <param name="botId">Required exact originating internal owned-bot runtime id.</param>
    /// <param name="receiptPriceToman">Positive whole-toman amount from the committed idempotent debit receipt, not a recomputed quote.</param>
    /// <param name="nowUtc">UTC-kind instant when the single paid executor claim is persisted.</param>
    /// <param name="cancellationToken">Cancellation of the local claim before the caller starts any panel work.</param>
    /// <returns>True only to the caller changing Denied/NotStarted to Started; false forbids creation and winner-only settlement/refund.</returns>
    /// <remarks>
    /// The caller first verifies live owned-colleague eligibility, ensures the exactly-once wallet debit and its
    /// ledger, and then competes here. The winner must create using colleague-paid-trial:{Id}; every losing or restarted
    /// confirmation may only read/reconcile durable panel evidence and never POST. The committed receipt amount replaces
    /// the preview and cannot subsequently be repriced. Started is never reset, even when no panel operation row exists:
    /// a crash between this claim and POST remains review-only rather than granting another executor or refund.
    /// This conditional SQLite write owns no wallet/network I/O and does not occupy or change free quota capacity.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram id or committed debit amount is not positive.</exception>
    /// <exception cref="ArgumentException">An identifier or UTC-kind instant is invalid.</exception>
    /// <example><code>
    /// if (await quotas.TryStartPaidCreationAsync(grant.Id, sender.Id, botId, -debitReceipt.AmountToman, DateTime.UtcNow, token))
    /// {
    ///     // Only this winner starts paid creation; losers cannot refund an absent or Reserved operation.
    /// }
    /// </code></example>
    public Task<bool> TryStartPaidCreationAsync(
        string grantId,
        long telegramUserId,
        string botId,
        long receiptPriceToman,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        if (receiptPriceToman <= 0)
            throw new ArgumentOutOfRangeException(nameof(receiptPriceToman), "A positive committed whole-toman debit amount is required.");
        ValidateUtc(nowUtc);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.ColleagueTrialGrants.Where(x => x.Id == grantId && x.TelegramUserId == telegramUserId &&
                x.BotId == botId && x.State == ColleagueTrialGrantState.Denied &&
                x.PaidCreationState == ColleagueTrialPaidCreationState.NotStarted)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaidCreationState, ColleagueTrialPaidCreationState.Started)
                    .SetProperty(x => x.PaidQuoteToman, receiptPriceToman).SetProperty(x => x.UpdatedAtUtc, nowUtc), ct) == 1;
        }, cancellationToken);
    }

    /// <summary>Settles the quiesced winning paid attempt from durable creation evidence without changing free quota.</summary>
    /// <param name="grantId">Required immutable users.db grant id linking the paid operation colleague-paid-trial:{Id}.</param>
    /// <param name="telegramUserId">Positive global Telegram sender id authenticated for this paid session.</param>
    /// <param name="botId">Required exact originating internal owned-bot runtime id.</param>
    /// <param name="nowUtc">UTC-kind local settlement instant persisted only on a paid lifecycle transition.</param>
    /// <param name="cancellationToken">Independent local-persistence cancellation after the creation invocation has stopped.</param>
    /// <returns>
    /// Detached exact Denied receipt or null for missing/mismatched/non-Denied access. Applied panel proof yields Applied;
    /// authorized or ambiguous POST yields Uncertain. A quiesced Started winner with absent/Reserved/DefinitiveRejected
    /// creation yields Rejected. NotStarted and terminal paid states remain unchanged. No wallet refund occurs here.
    /// </returns>
    /// <remarks>
    /// Only the winning TryStartPaidCreationAsync invocation may settle absent or Reserved creation, and only after
    /// its creation call quiesces. Losers/restarted callers must first read the existing paid operation and may call
    /// this method only for PostStarted, Ambiguous, Applied or DefinitiveRejected evidence; absent/Reserved Started is
    /// review-only. Reserved is atomically fenced to DefinitiveRejected before permitting a refund. Uncertain never
    /// downgrades on missing or Reserved evidence. The caller may issue one exact debit-receipt refund only for Rejected,
    /// never Started/Uncertain/Applied or notification failure. Receipt price and Denied free quota state stay unchanged.
    /// No panel, Telegram, wallet, ledger, order or partner-profit I/O is performed inside the writer transaction.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The authenticated Telegram id is not positive.</exception>
    /// <exception cref="ArgumentException">An identifier or UTC-kind instant is invalid.</exception>
    /// <exception cref="InvalidOperationException">The linked paid creation operation has another Telegram owner.</exception>
    /// <example><code>
    /// using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    /// var settled = await quotas.SettlePaidCreationAsync(grant.Id, sender.Id, botId, DateTime.UtcNow, persistence.Token);
    /// // Refund the exact committed receipt only when settled.PaidCreationState is Rejected.
    /// </code></example>
    public Task<ColleagueTrialGrant> SettlePaidCreationAsync(
        string grantId,
        long telegramUserId,
        string botId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantActor(grantId, telegramUserId, botId);
        ValidateUtc(nowUtc);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var found = await db.ColleagueTrialGrants.Where(x => x.Id == grantId && x.TelegramUserId == telegramUserId &&
                x.BotId == botId && x.State == ColleagueTrialGrantState.Denied)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaidCreationState, x => x.PaidCreationState), ct);
            if (found == 0)
                return null;
            var grant = await db.ColleagueTrialGrants.SingleAsync(x => x.Id == grantId, ct);
            if (grant.PaidCreationState is ColleagueTrialPaidCreationState.NotStarted or ColleagueTrialPaidCreationState.Applied or ColleagueTrialPaidCreationState.Rejected)
            {
                await transaction.CommitAsync(ct);
                return grant;
            }
            var operationKey = $"colleague-paid-trial:{grant.Id}";
            var creation = await db.XuiV3CreationOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct);
            if (creation != null && creation.TelegramUserId != telegramUserId)
                throw new InvalidOperationException("The paid creation operation does not belong to the colleague trial sender.");
            var nextState = creation?.Outcome switch
            {
                XuiV3CreationOutcome.Applied => ColleagueTrialPaidCreationState.Applied,
                XuiV3CreationOutcome.DefinitiveRejected => ColleagueTrialPaidCreationState.Rejected,
                null or XuiV3CreationOutcome.Reserved when grant.PaidCreationState == ColleagueTrialPaidCreationState.Started
                    => ColleagueTrialPaidCreationState.Rejected,
                _ => ColleagueTrialPaidCreationState.Uncertain
            };
            if (creation?.Outcome == XuiV3CreationOutcome.Reserved && nextState == ColleagueTrialPaidCreationState.Rejected)
            {
                // Serialize the terminal refund fence against the durable one-POST claim; a losing executor
                // must never reach this path while the actual winner is still preparing its creation request.
                await db.XuiV3CreationOperations.Where(x => x.OperationKey == operationKey && x.Outcome == XuiV3CreationOutcome.Reserved)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Outcome, XuiV3CreationOutcome.DefinitiveRejected), ct);
            }
            if (grant.PaidCreationState != nextState)
            {
                grant.PaidCreationState = nextState;
                grant.UpdatedAtUtc = nowUtc;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return grant;
        }, cancellationToken);
    }

    /// <summary>Settles a completed creation attempt from durable panel-mutation evidence, never from Telegram delivery.</summary>
    /// <param name="operationKey">Exact immutable OperationKey returned by admission; at most 64 credential-free characters.</param>
    /// <param name="nowUtc">UTC-kind local settlement instant, used only when a lifecycle transition is persisted.</param>
    /// <param name="cancellationToken">Independent local-persistence cancellation; do not reuse an already-cancelled HTTP/Telegram token.</param>
    /// <returns>
    /// Detached updated receipt, never null. Applied creation becomes Consumed; PostStarted/Ambiguous remains Uncertain;
    /// absent, still-Reserved or DefinitiveRejected creation becomes terminal Released. Denied, Released and Consumed
    /// receipts remain unchanged. No additional save, notification or external request occurs.
    /// </returns>
    /// <remarks>
    /// Only the winner of TryStartFreeCreationAsync may settle absent/Reserved after its progress/create invocation
    /// finishes, on both success and failure, and before any success notification. Losers/restarts must first read
    /// the existing creation operation and reconcile only PostStarted/Ambiguous/Applied/DefinitiveRejected evidence;
    /// absent/Reserved with FreeCreationStarted remains held for review. This is never a timeout scavenger.
    /// A Reserved creation is fenced to DefinitiveRejected in the same transaction before release, serializing with
    /// TryStartPostAsync. The immutable free executor marker is never reset. Uncertain receipts never expire;
    /// they release only on durable definitive rejection/authoritative absence or consume on Applied.
    /// Successful panel creation remains consumed even if account-detail construction or Telegram notification fails.
    /// </remarks>
    /// <exception cref="ArgumentException">The operation key or UTC-kind settlement timestamp is invalid.</exception>
    /// <exception cref="InvalidOperationException">The grant is missing or the creation operation belongs to another Telegram user.</exception>
    /// <example><code>
    /// using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    /// var settled = await quotas.SettleAsync(grant.OperationKey, DateTime.UtcNow, persistence.Token);
    /// // Only after settlement completes may the caller send a success notification.
    /// </code></example>
    public Task<ColleagueTrialGrant> SettleAsync(
        string operationKey,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(operationKey, 64, nameof(operationKey));
        ValidateUtc(nowUtc);
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.ColleagueTrialGrants.Where(x => x.OperationKey == operationKey)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, x => x.State), ct);
            var grant = await db.ColleagueTrialGrants.SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct)
                ?? throw new InvalidOperationException("The colleague trial grant does not exist.");
            if (grant.State is ColleagueTrialGrantState.Denied or ColleagueTrialGrantState.Released or ColleagueTrialGrantState.Consumed)
            {
                await transaction.CommitAsync(ct);
                return grant;
            }

            var creation = await db.XuiV3CreationOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationKey == operationKey, ct);
            if (creation != null && creation.TelegramUserId != grant.TelegramUserId)
                throw new InvalidOperationException("The creation operation does not belong to the colleague trial sender.");
            var nextState = creation?.Outcome switch
            {
                null or XuiV3CreationOutcome.Reserved or XuiV3CreationOutcome.DefinitiveRejected => ColleagueTrialGrantState.Released,
                XuiV3CreationOutcome.Applied => ColleagueTrialGrantState.Consumed,
                _ => ColleagueTrialGrantState.Uncertain
            };
            if (creation?.Outcome == XuiV3CreationOutcome.Reserved)
            {
                // Never leave an unused POST authorization behind a freed quota slot. SQLite serializes this
                // terminal fence against TryStartPostAsync, and the operation key is never reset or replaced.
                await db.XuiV3CreationOperations.Where(x => x.OperationKey == operationKey && x.Outcome == XuiV3CreationOutcome.Reserved)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Outcome, XuiV3CreationOutcome.DefinitiveRejected), ct);
            }
            if (grant.State != nextState)
            {
                grant.State = nextState;
                grant.UpdatedAtUtc = nowUtc;
                if (nextState == ColleagueTrialGrantState.Consumed)
                    grant.ConsumedAtUtc = nowUtc;
                else if (nextState == ColleagueTrialGrantState.Released)
                    grant.ReleasedAtUtc = nowUtc;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return grant;
        }, cancellationToken);
    }

    /// <summary>Rejects missing, padded or oversized immutable identifiers rather than silently changing their identity.</summary>
    /// <param name="value">Required credential-free identifier supplied by the runtime caller.</param>
    /// <param name="maximumLength">Maximum persisted identifier length in characters.</param>
    /// <param name="parameterName">Call-site parameter reported by validation errors.</param>
    /// <remarks>Exact ordinal request identity is preserved; the store does not trim, case-fold or rewrite keys.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank, padded or too long.</exception>
    private static void ValidateKey(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim())
            throw new ArgumentException($"A nonblank unpadded identifier of at most {maximumLength} characters is required.", parameterName);
    }

    /// <summary>Requires an explicit UTC instant so machine-local timezone settings cannot move the quota boundary.</summary>
    /// <param name="nowUtc">Reservation or settlement timestamp supplied by the runtime caller.</param>
    /// <remarks>Use DateTime.UtcNow; persisted Tehran dates themselves have unspecified DateTime kind.</remarks>
    /// <exception cref="ArgumentException">The timestamp is local or unspecified rather than UTC.</exception>
    private static void ValidateUtc(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("An explicit UTC timestamp is required.", nameof(nowUtc));
    }

    /// <summary>Validates the immutable grant and authenticated origin identifiers shared by paid-offer access.</summary>
    /// <param name="grantId">Required receipt id returned by quota admission.</param>
    /// <param name="telegramUserId">Positive global authenticated Telegram sender id.</param>
    /// <param name="botId">Required originating internal owned-bot runtime id.</param>
    /// <remarks>Validation is structural only; the database predicate enforces exact actor and bot access.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The Telegram id is not positive.</exception>
    /// <exception cref="ArgumentException">An immutable identifier is missing, padded or oversized.</exception>
    private static void ValidateGrantActor(string grantId, long telegramUserId, string botId)
    {
        if (telegramUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(telegramUserId), "A positive Telegram user id is required.");
        ValidateKey(grantId, 32, nameof(grantId));
        ValidateKey(botId, 64, nameof(botId));
    }

    /// <summary>Resolves the same Linux/Windows Tehran timezone convention used by usage analytics.</summary>
    /// <returns>System Tehran timezone, or the modern UTC+03:30 fallback when timezone data is unavailable.</returns>
    /// <remarks>The fixed fallback is suitable for new rollout-era grants; no historical trial backfill is performed.</remarks>
    private static TimeZoneInfo ResolveIranTimeZone()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Iran Fixed", TimeSpan.FromMinutes(210), "Iran Fixed", "Iran Fixed");
    }
}
