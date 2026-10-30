using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Adminbot.Services;

/// <summary>Sanitized administrator review result; hashes bind confirmation but reveal no panel credentials.</summary>
/// <param name="OrderId">Internal users.db order id.</param>
/// <param name="Eligible">Whether a separate, fresh second confirmation may be prepared.</param>
/// <param name="Reason">Closed operational reason, never upstream text.</param>
/// <param name="ResumeStep">Proven pre-mutation claimed stage or complete GET-proven stage; null on refusal.</param>
/// <param name="Binding">Private comparison digest; not displayed.</param>
/// <param name="Snapshot">Mutable database version digest; not displayed.</param>
/// <param name="Inbounds">Sorted catalog placement JSON; no customer data.</param>
/// <param name="Evidence">Sanitized boolean/numeric proof only.</param>
public sealed record TenantCardReviewProbe(int OrderId, bool Eligible, string Reason, string ResumeStep = null,
    string Binding = null, string Snapshot = null, string Inbounds = null, string Evidence = null);

/// <summary>Actor-bound confirmation carried internally into the existing tenant fulfillment gate, never a payment authorization.</summary>
/// <param name="ReviewId">Internal durable confirmation id.</param>
/// <param name="Actor">Global super-admin Telegram id.</param>
/// <param name="BotId">Enabled owned bot hosting the private conversation.</param>
/// <param name="ChatId">Private chat id, equal to actor.</param>
public sealed record TenantCardReviewedRecoveryRequest(long ReviewId, long Actor, string BotId, long ChatId);

/// <summary>Authorized detached order and sanitized saga summary for the private administrator list.</summary>
/// <param name="Order">Exact tenant order; the UI must never render its private credential fields.</param>
/// <param name="Step">Closed saga step, or unknown for malformed data.</param>
/// <param name="Error">Bounded safe error category, never raw panel text.</param>
public sealed record TenantCardReviewListItem(TenantBotOrder Order, string Step, string Error);

/// <summary>GET-only review and explicit audited recovery admission for tenant-card provisional purchases.</summary>
/// <remarks>No balance mutation, receipt approval, client creation or alternative settlement exists here.
/// Execution delegates to the central tenant order gate, which repeats this proof before consuming the confirmation.
/// Historical manual-review rows never become automatically recoverable through migration or a worker.</remarks>
public sealed class TenantCardProvisionalManualReviewService
{
    private readonly UserDbContextFactory _factory;
    private readonly TenantCardProvisionalOperationStore _store;
    private readonly CredentialsStore _credentials;
    private readonly XuiV3PurchaseService _purchase;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _app;
    private readonly BotRegistry _registry;
    private readonly IServiceProvider _services;

    /// <summary>Constructs an operation-local reviewer with no retained EF tracker or financial executor.</summary>
    /// <param name="factory">users.db factory; only short local transactions are used.</param>
    /// <param name="store">Durable finalize/review store.</param>
    /// <param name="credentials">Global wallet receipt reader, not a tenant-specific balance.</param>
    /// <param name="purchase">Live catalog resolver; audience and entitlement validation remain authoritative.</param>
    /// <param name="configuration">Global configuration including live administrator allowlist and panel connection; never log it.</param>
    /// <param name="registry">Current enabled owned-host registry.</param>
    /// <param name="services">Current scope, used lazily to enter the existing TenantBotService settlement path without a DI cycle.</param>
    public TenantCardProvisionalManualReviewService(UserDbContextFactory factory, TenantCardProvisionalOperationStore store,
        CredentialsStore credentials, XuiV3PurchaseService purchase, IConfiguration configuration, BotRegistry registry, IServiceProvider services)
    {
        _factory = factory; _store = store; _credentials = credentials; _purchase = purchase;
        _configuration = configuration; _app = configuration.Get<AppConfig>() ?? new(); _registry = registry; _services = services;
    }

    /// <summary>Checks global administrator and exact private enabled-owned host authority on every entry.</summary>
    /// <param name="actor">Telegram sender id, not a callback-supplied customer id.</param>
    /// <param name="botId">Current internal runtime bot id.</param>
    /// <param name="chatId">Private chat id; must equal sender.</param>
    /// <returns>True only for the currently configured global administrator on an enabled owned bot.</returns>
    /// <remarks>Tenant owner/colleague status never grants recovery authority.</remarks>
    /// <example><code>if (!review.IsAuthorized(sender.Id, runtime.Config.Id, privateChat.Id)) return;</code></example>
    public bool IsAuthorized(long actor, string botId, long chatId)
        => actor > 0 && chatId == actor && (_configuration.GetSection("AdminsUserIds").Get<long[]>()?.Contains(actor) ?? false)
           && _registry.GetById(botId) is { Enabled: true, Type: BotInstanceTypes.Owned } host
           && string.Equals(host.Id, botId, StringComparison.Ordinal);

    /// <summary>Lists unresolved provisional manual-review orders, or one exact public order id.</summary>
    /// <param name="actor">Global administrator Telegram id.</param><param name="botId">Current owned host.</param>
    /// <param name="chatId">Private actor chat.</param><param name="exactOrderId">Optional exact public order id from admin search, never logged.</param>
    /// <param name="page">Nonnegative zero-based page, bounded to 10000.</param><param name="token">Read cancellation.</param>
    /// <returns>At most ten detached orders; empty for unauthorized/not found. Never expose their identity fields to ordinary users.</returns>
    /// <remarks>Includes reviewed settlement interruptions, but excludes renewals, gateways and ordinary purchases.</remarks>
    /// <example><code>var page = await review.ListAsync(adminId, ownedBotId, adminId, null, 0, token);</code></example>
    public async Task<List<TenantCardReviewListItem>> ListAsync(long actor, string botId, long chatId, string exactOrderId, int page, CancellationToken token)
    {
        if (!IsAuthorized(actor, botId, chatId)) return new();
        await using var db = _factory.CreateDbContext();
        var query = db.TenantBotOrders.AsNoTracking().Where(o => o.PaymentProvider == "tenant_card" &&
            o.OrderKind == TenantBotOrderKinds.Purchase && !o.IsFulfilled && db.TenantCardProvisionalOperations.Any(s =>
                s.TenantBotOrderId == o.Id && s.Kind == TenantCardProvisionalOperationKinds.Finalize &&
                (s.Step == TenantCardProvisionalSteps.ManualReview || s.ReviewedRecoveryId != null)));
        if (!string.IsNullOrWhiteSpace(exactOrderId)) query = query.Where(o => o.OrderId == exactOrderId.Trim());
        var orders = await query.OrderByDescending(o => o.Id).Skip(Math.Clamp(page, 0, 10000) * 10).Take(10).ToListAsync(token);
        var ids = orders.Select(o => o.Id).ToArray();
        var sagas = await db.TenantCardProvisionalOperations.AsNoTracking().Where(s => ids.Contains(s.TenantBotOrderId) &&
            s.Kind == TenantCardProvisionalOperationKinds.Finalize).ToListAsync(token);
        return orders.Select(o => {
            var saga = sagas.SingleOrDefault(s => s.OperationKey == $"tenant-card-finalize:{o.OrderId}");
            var step = saga != null && TenantCardProvisionalSteps.OrderOf(saga.Step) != int.MaxValue ? saga.Step : "unknown";
            var error = saga?.ErrorCode;
            return new TenantCardReviewListItem(o, step, !string.IsNullOrEmpty(error) && error.Length <= 96 &&
                error.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? error : "none_or_unsanitized");
        }).ToList();
    }

    /// <summary>Reads durable evidence and authenticated panel state without any write, claim, approval or mutation.</summary>
    /// <param name="orderId">Internal tenant order id from an administrator list, not an account id.</param>
    /// <param name="actor">Actual Telegram sender id.</param><param name="botId">Current owned host.</param>
    /// <param name="chatId">Private sender chat.</param><param name="token">Database/panel read cancellation.</param>
    /// <returns>Credential-free eligibility/refusal. A failed GET leaves every row untouched.</returns>
    /// <remarks>Only before-first-mutation placement failures can restart the saga. Partial/ambiguous writes require complete
    /// frozen-target proof; reviewed proven deliveries preserve paid usage when retrying settlement.</remarks>
    /// <example><code>var probe = await review.ProbeAsync(order.Id, adminId, ownedBotId, adminId, token);</code></example>
    public async Task<TenantCardReviewProbe> ProbeAsync(int orderId, long actor, string botId, long chatId, CancellationToken token)
    {
        TenantCardReviewProbe Refuse(string code) => new(orderId, false, code);
        if (!IsAuthorized(actor, botId, chatId)) return Refuse("unauthorized");
        if (!_configuration.GetValue<bool>("TenantCardProvisionalDeliveryEnabled")) return Refuse("feature_disabled");
        await using var db = _factory.CreateDbContext();
        var o = await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, token);
        if (o == null) return Refuse("order_missing");
        if (o.IsFulfilled) return Refuse("already_fulfilled");
        if (o.PaymentProvider != "tenant_card" || o.OrderKind != TenantBotOrderKinds.Purchase || o.AccountCount != 1)
            return Refuse("order_scope_invalid");
        var s = await db.TenantCardProvisionalOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey == $"tenant-card-finalize:{o.OrderId}", token);
        if (s == null || s.TenantBotOrderId != o.Id || s.OrderId != o.OrderId || s.Kind != TenantCardProvisionalOperationKinds.Finalize ||
            (s.Step != TenantCardProvisionalSteps.ManualReview && s.ReviewedRecoveryId == null)) return Refuse("saga_binding_invalid");
        var tenant = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == o.TenantBotId, token);
        if (tenant == null || tenant.Type != BotInstanceTypes.Tenant || tenant.OwnerTelegramUserId != o.OwnerTelegramUserId ||
            await _credentials.GetUserStatusWithId(o.OwnerTelegramUserId) == null ||
            await _credentials.GetUserStatusWithId(o.CustomerTelegramUserId) == null) return Refuse("tenant_binding_invalid");
        var receipts = await db.TenantManualPaymentReceipts.AsNoTracking().Where(x => x.TenantBotOrderId == o.Id &&
            x.Status == TenantManualPaymentReceiptStatuses.Approved).Take(2).ToListAsync(token);
        var r = receipts.Count == 1 ? receipts[0] : null;
        if (r == null || r.Id != o.ManualReceiptId || r.OrderId != o.OrderId || r.TenantBotId != o.TenantBotId ||
            r.OwnerTelegramUserId != o.OwnerTelegramUserId || r.CustomerTelegramUserId != o.CustomerTelegramUserId ||
            r.AmountToman != o.SalePriceToman || r.ApprovedAtUtc == null || r.FinalConfirmedAtUtc == null || r.RejectedAtUtc != null ||
            o.SalePriceToman <= 0 || o.BaseCostToman <= 0) return Refuse("payment_evidence_invalid");
        if (string.IsNullOrWhiteSpace(o.ProvisionalAccountEmail) || string.IsNullOrWhiteSpace(o.ProvisionalAccountUuid))
            return Refuse("identity_missing");
        if (await db.TenantBotOrders.AnyAsync(x => x.Id != o.Id &&
                (x.ProvisionalAccountUuid == o.ProvisionalAccountUuid || x.ProvisionalAccountEmail == o.ProvisionalAccountEmail), token))
            return Refuse("identity_binding_ambiguous");
        if (await db.TenantCardProvisionalOperations.AnyAsync(x => x.TenantBotOrderId == o.Id && x.Kind == TenantCardProvisionalOperationKinds.Revoke, token))
            return Refuse("revocation_exists");
        var normalKey = $"tenant-create:{o.Id}";
        if (await db.XuiV3CreationOperations.AnyAsync(x => x.OperationKey == normalKey || x.OperationKey.StartsWith(normalKey + ":"), token))
            return Refuse("competing_creation");
        var normalizedEmail = o.ProvisionalAccountEmail.Trim().ToLowerInvariant();
        if (await db.XuiV3RenewalOperations.AnyAsync(x => x.AccountLockKey != null &&
            (x.TargetUuid == o.ProvisionalAccountUuid || x.NormalizedTargetEmail == normalizedEmail), token)) return Refuse("competing_renewal");
        var activeLinkStatuses = XuiV3LinkChangeStatuses.Active.ToArray();
        if (await db.XuiV3LinkChangeOperations.AnyAsync(x => activeLinkStatuses.Contains(x.Status) &&
            (x.OldUuid == o.ProvisionalAccountUuid || x.NewUuid == o.ProvisionalAccountUuid ||
             x.OldEmail == o.ProvisionalAccountEmail || x.NewEmail == o.ProvisionalAccountEmail), token)) return Refuse("competing_link_change");

        XuiV3ResolvedPurchase resolved;
        try { resolved = _purchase.ResolveTenantPurchase(new XuiV3PurchaseSelection { ServiceKey = o.ServiceKey,
            TrafficGb = o.TrafficGb, DurationKey = o.DurationKey, UnlimitedPlanKey = o.UnlimitedPlanKey, AccountCount = 1 }, false); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException or System.IO.IOException)
        { return Refuse("catalog_invalid"); }
        var inbounds = XuiV3PurchaseService.ResolveServiceInboundIds(resolved.Service).Distinct().Order().ToArray();
        if (inbounds.Length == 0 || !s.FinalQuotaBytes.HasValue || !s.FinalExpiryTimeMs.HasValue || s.FinalizationEffectiveAtUtc == null ||
            s.FinalQuotaBytes != resolved.TrafficBytes) return Refuse("frozen_entitlement_invalid");
        var expectedExpiry = resolved.DurationDays > 0
            ? new DateTimeOffset(DateTime.SpecifyKind(s.FinalizationEffectiveAtUtc.Value, DateTimeKind.Utc)).AddDays(resolved.DurationDays).ToUnixTimeMilliseconds() : 0;
        if (Math.Abs((decimal)s.FinalExpiryTimeMs.Value - expectedExpiry) > 2000 ||
            (s.FinalExpiryTimeMs > 0 && s.FinalExpiryTimeMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return Refuse("frozen_expiry_invalid");
        var panel = new ServerInfo { ApiVersion = "v3", Url = _app.XuiV3ApiBaseUrl?.TrimEnd('/'), ApiToken = _app.XuiV3ApiToken,
            RootPath = (_app.XuiV3ApiRootPath ?? "").Trim('/') };
        var creation = await db.XuiV3CreationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey ==
            TenantCardProvisionalProvisioningService.BuildCreateOperationKey(o.OrderId), token);
        XuiV3ClientPayload original;
        try { original = creation == null ? null : JsonConvert.DeserializeObject<XuiV3ClientPayload>(creation.ClientJson); }
        catch (JsonException) { return Refuse("creation_evidence_invalid"); }
        if (creation?.Outcome != XuiV3CreationOutcome.Applied || creation.PanelKey != XuiV3LinkChangeOperationStore.BuildPanelKey(panel) ||
            creation.TelegramUserId != o.CustomerTelegramUserId || original == null ||
            !SameIdentity(original, o)) return Refuse("creation_evidence_invalid");

        // FIRST recovery requires zero financial artifacts. A fresh review of this protocol's proven delivery may
        // resume its matching receipt through the original settlement tail, never compensate or choose another route.
        var settlementResume = s.ReviewedRecoveryId.HasValue && s.ProvenAtUtc.HasValue &&
            (s.Step == TenantCardProvisionalSteps.Proven || s.LastProvenStep == TenantCardProvisionalSteps.Proven);
        if (s.ReviewedRecoveryId.HasValue)
        {
            var prior = await db.TenantCardProvisionalReviews.AsNoTracking().SingleOrDefaultAsync(x => x.Id == s.ReviewedRecoveryId, token);
            if (prior == null || prior.TenantBotOrderId != o.Id || prior.OperationKey != s.OperationKey ||
                prior.AuthorizedAtUtc == null || prior.Status is not ("authorized" or "refused") ||
                prior.BindingHash != ReviewBinding.Binding(o, s, r, JsonConvert.SerializeObject(inbounds)))
                return Refuse("prior_recovery_binding_invalid");
        }
        if (await db.TenantBotLedgerEntries.AnyAsync(x => x.TenantBotOrderId == o.Id, token)) return Refuse("tenant_ledger_exists");
        var debit = await _credentials.GetWalletOperationAsync($"tenant:{o.Id}:base-cost", token);
        var profit = await _credentials.GetWalletOperationAsync($"tenant:{o.Id}:profit", token);
        var route = await db.Set<TenantWalletRoute>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == o.Id, token);
        var walletLedger = await db.WalletLedgerEntries.AsNoTracking().Where(x => x.OrderId == o.OrderId).ToListAsync(token);
        var siteKey = $"site:{o.OwnerTelegramUserId}:tenant-base-cost:{o.Id}";
        var site = await db.Set<SiteWalletDebitOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == siteKey, token);
        if (!settlementResume && (debit != null || profit != null || route != null || walletLedger.Count != 0 || site != null))
            return Refuse("financial_artifact_exists");
        if (settlementResume && (profit != null || (debit != null && (debit.TelegramUserId != o.OwnerTelegramUserId || debit.AmountToman != -o.BaseCostToman ||
                (decimal)debit.BeforeBalance - debit.AfterBalance != o.BaseCostToman)) ||
            (route != null && (route.OwnerTelegramUserId != o.OwnerTelegramUserId || route.AmountToman != o.BaseCostToman ||
                route.Source is not ("bot" or "negative" or "site"))) || (site != null && (site.Status != "applied" || site.AmountToman != o.BaseCostToman ||
                site.OwnerTelegramUserId != o.OwnerTelegramUserId || (decimal)site.BeforeBalance - site.AfterBalance != o.BaseCostToman ||
                debit != null || route?.Source != "site")) ||
            walletLedger.Any(x => x.TelegramUserId != o.OwnerTelegramUserId || x.AmountToman != o.BaseCostToman || x.Direction != WalletLedgerDirections.Debit ||
                (x.IdempotencyKey != $"tenant:{o.Id}:base-cost" && x.IdempotencyKey != $"tenant:{o.Id}:site-base-cost") ||
                (debit == null && site == null))))
            return Refuse("financial_resume_uncertain");
        try
        {
            var response = await ApiServicev3.GetClientAsync(panel, _configuration, o.ProvisionalAccountEmail, token);
            if (response is not { Success: true, Obj: not null }) return Refuse("panel_unavailable");
            var client = response.Obj;
            if (!SameIdentity(client, o)) return Refuse("identity_mismatch");
            if (client.InboundIds == null || !client.InboundIds.Distinct().Order().SequenceEqual(inbounds)) return Refuse("placement_mismatch");
            var traffic = await ApiServicev3.GetClientTrafficAsync(panel, _configuration, o.ProvisionalAccountEmail, token);
            if (traffic is not { Success: true, Obj: not null }) return Refuse("traffic_unavailable");
            if (!string.Equals(traffic.Obj.Email, o.ProvisionalAccountEmail, StringComparison.OrdinalIgnoreCase)) return Refuse("traffic_identity_mismatch");
            var zero = traffic.Obj.Up == 0 && traffic.Obj.Down == 0;
            var target = client.Enable && client.TotalGB == s.FinalQuotaBytes && client.ExpiryTime == s.FinalExpiryTimeMs;
            string resume;
            if (target && (zero || settlementResume)) resume = TenantCardProvisionalSteps.Proven;
            else if (ReviewBinding.IsUnmutatedPlacementFailure(s) && client.TotalGB == 1073741824L && client.ExpiryTime > 0)
                resume = TenantCardProvisionalSteps.Claimed;
            else return Refuse("partial_or_ambiguous_mutation");
            var placement = JsonConvert.SerializeObject(inbounds);
            return new(o.Id, true, settlementResume ? "settlement_resume_proven" : "safe_recovery_proven", resume,
                ReviewBinding.Binding(o, s, r, placement), ReviewBinding.Snapshot(o, s, r, placement), placement,
                JsonConvert.SerializeObject(new { identity = true, placement = true, finalTarget = target, countersZero = zero,
                    quotaBytes = client.TotalGB, expiryTimeMs = client.ExpiryTime, resume }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is XuiV3ApiException or HttpRequestException or OperationCanceledException or JsonException)
        { return Refuse("panel_unavailable"); }
    }

    /// <summary>Prepares a ten-minute second confirmation after successful GET-only proof; never opens the saga yet.</summary>
    /// <param name="orderId">Internal exact order id.</param><param name="actor">Actual administrator sender id.</param>
    /// <param name="botId">Current owned host.</param><param name="chatId">Private sender chat.</param><param name="token">Read/audit write cancellation.</param>
    /// <returns>New durable review id, or null after refused/stale proof.</returns>
    /// <remarks>The row binds actor, host, chat, receipt, frozen entitlement and database versions. No financial change occurs.</remarks>
    /// <example><code>var confirmationId = await review.PrepareAsync(order.Id, adminId, ownedBotId, adminId, token);</code></example>
    public async Task<long?> PrepareAsync(int orderId, long actor, string botId, long chatId, CancellationToken token)
    {
        var p = await ProbeAsync(orderId, actor, botId, chatId, token);
        if (!p.Eligible) return null;
        var order = await FindOrderAsync(orderId, token);
        if (order == null) return null;
        var operation = await _store.FindAsync($"tenant-card-finalize:{order.OrderId}", token);
        if (operation == null) return null;
        var now = DateTime.UtcNow;
        var row = await _store.CreateReviewedConfirmationAsync(new() { TenantBotOrderId = orderId, OperationKey = operation.OperationKey,
            ActorTelegramUserId = actor, BotId = botId, ChatId = chatId, BindingHash = p.Binding, SnapshotHash = p.Snapshot,
            ExpectedInboundIdsJson = p.Inbounds, ResumeStep = p.ResumeStep, OriginalReason = operation.ErrorCode,
            EvidenceJson = p.Evidence, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10) }, token);
        return row?.Id;
    }

    /// <summary>Freshly authorizes a reviewed execution while the caller owns the existing central order gate.</summary>
    /// <param name="order">Fresh order reloaded under the central gate, not customer state.</param>
    /// <param name="request">Actor/bot/chat-bound second confirmation.</param><param name="token">Fresh proof/atomic authorization cancellation.</param>
    /// <returns>True only after one confirmation is durably consumed and safe resume step selected.</returns>
    /// <remarks>Call before marking paid, finalizing XUI, debiting wallets or writing ledgers. Competing/replayed approvals fail closed.</remarks>
    /// <example><code>// Only inside the existing central tenant order gate:
    /// var authorized = await review.AuthorizeUnderOrderGateAsync(reloadedOrder, confirmation, token);</code></example>
    public async Task<bool> AuthorizeUnderOrderGateAsync(TenantBotOrder order, TenantCardReviewedRecoveryRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request.Actor, request.BotId, request.ChatId)) return false;
        var p = await ProbeAsync(order.Id, request.Actor, request.BotId, request.ChatId, token);
        var authorized = p.Eligible && await _store.TryAuthorizeReviewedRecoveryAsync(request.ReviewId, request.Actor, request.BotId, request.ChatId,
            p.Binding, p.Snapshot, p.ResumeStep, p.Evidence, token);
        if (!authorized)
        {
            await using var db = _factory.CreateDbContext();
            var reason = p.Eligible ? "confirmation_snapshot_changed" : p.Reason;
            await db.TenantCardProvisionalReviews.Where(x => x.Id == request.ReviewId && x.Status == "reviewed" &&
                x.ActorTelegramUserId == request.Actor && x.BotId == request.BotId && x.ChatId == request.ChatId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "refused").SetProperty(x => x.OutcomeCode, reason)
                    .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), token);
        }
        return authorized;
    }

    /// <summary>Durably cancels an unused second confirmation without changing its order, saga or payment.</summary>
    /// <param name="request">Actual actor/private host/chat and the confirmation being dismissed.</param>
    /// <param name="token">Short conditional audit-write cancellation.</param>
    /// <returns>True only when this actor's unused confirmation was cancelled; an executing/completed review cannot be cancelled.</returns>
    /// <remarks>Replaying its old confirm button afterwards is stale. Cancellation never reverses or re-arms an external effect.</remarks>
    /// <example><code>await review.CancelAsync(new(reviewId, sender.Id, ownedBotId, privateChat.Id), token);</code></example>
    public async Task<bool> CancelAsync(TenantCardReviewedRecoveryRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request.Actor, request.BotId, request.ChatId)) return false;
        await using var db = _factory.CreateDbContext();
        return await db.TenantCardProvisionalReviews.Where(x => x.Id == request.ReviewId && x.Status == "reviewed" &&
            x.ActorTelegramUserId == request.Actor && x.BotId == request.BotId && x.ChatId == request.ChatId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "refused").SetProperty(x => x.OutcomeCode, "cancelled")
                .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), token) == 1;
    }

    /// <summary>Executes a private second confirmation through the existing central fulfillment/settlement path.</summary>
    /// <param name="request">Current actor/host/chat and durable review id, never a payment or account credential.</param>
    /// <param name="token">Execution cancellation; interrupted authorizations require another explicit fresh review.</param>
    /// <returns>Safe durable outcome code; fulfilled means the order commit succeeded, not merely panel success.</returns>
    /// <remarks>No new receipt/payment is created. The central gate and receipt keys prevent duplicate financial effects.
    /// Cancellation/storage failures may leave an authorized audit unfinished; they never re-arm it or authorize automatic replay.</remarks>
    /// <exception cref="OperationCanceledException">Caller shutdown interrupts proof/execution; the next action must begin with fresh review.</exception>
    /// <example><code>var result = await review.ExecuteAsync(new(reviewId, sender.Id, ownedBotId, privateChat.Id), token);</code></example>
    public async Task<string> ExecuteAsync(TenantCardReviewedRecoveryRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request.Actor, request.BotId, request.ChatId)) return "unauthorized";
        await using var db = _factory.CreateDbContext();
        var review = await db.TenantCardProvisionalReviews.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReviewId, token);
        if (review == null || review.ActorTelegramUserId != request.Actor || review.BotId != request.BotId || review.ChatId != request.ChatId)
            return "confirmation_invalid";
        if (review.Status == "fulfilled") return "already_fulfilled";
        if (review.Status != "reviewed" || review.ExpiresAtUtc <= DateTime.UtcNow) return "confirmation_stale";
        await _services.GetRequiredService<TenantBotService>().RecoverReviewedProvisionalOrderAsync(review.TenantBotOrderId, request, token);
        var order = await FindOrderAsync(review.TenantBotOrderId, token);
        var outcome = order?.IsFulfilled == true ? "fulfilled" : "recovery_refused_or_incomplete";
        if (order?.IsFulfilled != true)
        {
            var currentReview = await db.TenantCardProvisionalReviews.AsNoTracking().SingleAsync(x => x.Id == review.Id, token);
            var operation = await _store.FindAsync(review.OperationKey, token);
            outcome = currentReview.Status == "refused" ? currentReview.OutcomeCode
                : operation?.Step == TenantCardProvisionalSteps.Proven ? "settlement_pending_after_panel_proof"
                : operation?.MutationPending == true ? "panel_mutation_uncertain"
                : operation?.Step == TenantCardProvisionalSteps.ManualReview ? "panel_proof_requires_manual_review"
                : "recovery_refused_or_incomplete";
        }
        await db.TenantCardProvisionalReviews.Where(x => x.Id == review.Id && (x.Status == "authorized" || x.Status == "reviewed"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, order != null && order.IsFulfilled ? "fulfilled" : "refused")
                .SetProperty(x => x.OutcomeCode, outcome).SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), token);
        return outcome;
    }

    /// <summary>Reads one detached order using the current database factory.</summary>
    /// <param name="id">Internal order primary key.</param><param name="token">Read cancellation.</param><returns>Detached order or null.</returns>
    private async Task<TenantBotOrder> FindOrderAsync(int id, CancellationToken token)
    { await using var db = _factory.CreateDbContext(); return await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token); }

    /// <summary>Compares all immutable client credentials; a same-email replacement must fail closed.</summary>
    /// <param name="client">Private live or creation client payload; never log.</param><param name="order">Saved provisional order identity.</param>
    /// <returns>True only for exact UUID/SubId and case-insensitive email equality.</returns>
    private static bool SameIdentity(XuiV3ClientPayload client, TenantBotOrder order)
        => string.Equals(client.Email, order.ProvisionalAccountEmail, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(client.Uuid, order.ProvisionalAccountUuid, StringComparison.Ordinal) &&
           string.Equals(client.SubId ?? "", order.ProvisionalSubId ?? "", StringComparison.Ordinal);
}
