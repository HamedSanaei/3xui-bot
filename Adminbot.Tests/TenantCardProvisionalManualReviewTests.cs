using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>Real SQLite/fake upstream regression coverage for explicit provisional purchase manual recovery.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Historical pre-write placement failures become eligible only after correction and GET-only proof.</summary>
    [Fact]
    public async Task ProvisionalReview_corrected_placement_reprobe_is_read_only()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        panel.AttachInbounds(f.Email, 999);
        var blocked = await f.Finalizer.FinalizeAsync(f.Request, default);
        Assert.Equal(TenantCardProvisionalFinalizationStatus.ManualReview, blocked.Status);
        Assert.Equal("placement_mismatch", (await f.Probe()).Reason);
        panel.AttachInbounds(f.Email, f.Inbounds);
        var before = await f.Saga(); var posts = panel.TotalPostCount;
        var probe = await f.Probe();
        Assert.True(probe.Eligible, probe.Reason); Assert.Equal("claimed", probe.ResumeStep);
        Assert.Equal(posts, panel.TotalPostCount); Assert.Equal(before.UpdatedAtUtc, (await f.Saga()).UpdatedAtUtc);
        await using var db = dbs.Users.CreateDbContext(); Assert.Empty(await db.TenantCardProvisionalReviews.ToListAsync());
        Assert.Null(await f.Wallet.GetWalletOperationAsync($"tenant:{f.Order.Id}:base-cost"));
    }

    /// <summary>Central fulfillment upgrades the same identity, settles once, and duplicate/concurrent confirmations never create another client.</summary>
    [Fact]
    public async Task ProvisionalReview_central_recovery_concurrency_settles_once_and_preserves_frozen_target()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var frozen = await f.Saga(); var id1 = await f.Prepare(); var id2 = await f.Prepare();
        Assert.NotNull(id1); Assert.NotNull(id2);
        var outcomes = await Task.WhenAll(f.Execute(id1!.Value), f.Execute(id2!.Value));
        Assert.Contains("fulfilled", outcomes);
        Assert.Equal("already_fulfilled", await f.Execute(id1.Value));
        var client = panel.ReadClient(f.Email);
        Assert.Equal(f.Email, client["email"]!.Value<string>()); Assert.Equal(f.Uuid, client["uuid"]!.Value<string>());
        Assert.Equal(f.SubId, client["subId"]!.Value<string>()); Assert.Equal(PurchasedQuota, client["totalGB"]!.Value<long>());
        Assert.Equal(frozen.FinalExpiryTimeMs, client["expiryTime"]!.Value<long>());
        Assert.Equal(1, panel.ResetRequestCount); Assert.Equal(1, panel.UpdateRequestCount);
        Assert.Equal(panel.ResetRequestCount + panel.UpdateRequestCount + panel.CounterWriteRequestCount, panel.TotalPostCount);
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner));
        await using var db = dbs.Users.CreateDbContext();
        Assert.True((await db.TenantBotOrders.SingleAsync()).IsFulfilled);
        Assert.Single(await db.TenantBotLedgerEntries.ToListAsync()); Assert.Single(await db.WalletLedgerEntries.ToListAsync());
        Assert.NotEmpty(await db.TenantOrderNotifications.ToListAsync());
        Assert.Equal("provisional_identity_placement_mismatch", (await db.TenantCardProvisionalReviews.SingleAsync(x => x.Id == id1)).OriginalReason);
    }

    /// <summary>A depleted/expired courtesy client is enabled without rotating identity, then reset and upgraded on the same account.</summary>
    [Fact]
    public async Task ProvisionalReview_disabled_courtesy_client_keeps_identity_during_enable_and_upgrade()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        panel.SeedClient(f.Email, 100, 200, enable: false);
        panel.SetClientIdentity(f.Email, f.Uuid, f.SubId);
        panel.SetClientQuota(f.Email, OneGib, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());
        panel.AttachInbounds(f.Email, f.Inbounds);
        var id = await f.Prepare(); Assert.NotNull(id);
        Assert.Equal("fulfilled", await f.Execute(id!.Value));
        var client = panel.ReadClient(f.Email);
        Assert.True(client["enable"]!.Value<bool>()); Assert.Equal(f.Uuid, client["uuid"]!.Value<string>());
        Assert.Equal(f.SubId, client["subId"]!.Value<string>()); Assert.Equal(PurchasedQuota, client["totalGB"]!.Value<long>());
        Assert.Equal(2, panel.UpdateRequestCount); Assert.Equal(1, panel.ResetRequestCount);
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner));
    }

    /// <summary>A panel-proven delivery followed by wallet-ledger failure resumes settlement only, preserving newly accrued paid usage.</summary>
    [Fact]
    public async Task ProvisionalReview_settlement_failure_restart_never_replays_upgrade_or_resets_paid_usage()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        await using (var db = dbs.Users.CreateDbContext()) await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_review_ledger BEFORE INSERT ON WalletLedgerEntries BEGIN SELECT RAISE(ABORT, 'isolated settlement failure'); END;");
        var id = await f.Prepare(); Assert.Equal("settlement_pending_after_panel_proof", await f.Execute(id!.Value));
        var saga = await f.Saga(); Assert.Equal("proven", saga.Step);
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner)); // Debit committed in credentials.db before users.db failure.
        var postCount = panel.TotalPostCount; var expiry = saga.FinalExpiryTimeMs;
        panel.SetSharedTraffic(f.Email, 123456, 654321);
        await using (var db = dbs.Users.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_review_ledger;");
        // A new scope models restart. No in-memory approval/step is needed; a fresh explicit review reuses the durable debit key.
        using var scope = f.Services.CreateScope();
        var review = scope.ServiceProvider.GetRequiredService<TenantCardProvisionalManualReviewService>();
        var retry = await review.PrepareAsync(f.Order.Id, f.Admin, "review-owned", f.Admin, default);
        Assert.NotNull(retry);
        Assert.Equal("fulfilled", await review.ExecuteAsync(new(retry!.Value, f.Admin, "review-owned", f.Admin), default));
        Assert.Equal(postCount, panel.TotalPostCount); Assert.Equal(expiry, panel.ReadClient(f.Email)["expiryTime"]!.Value<long>());
        Assert.Equal(123456, panel.ReadClient(f.Email)["up"]!.Value<long>());
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner));
        await using var check = dbs.Users.CreateDbContext(); Assert.Single(await check.TenantBotLedgerEntries.ToListAsync());
    }

    /// <summary>Malformed payment evidence, identities and competing operations must refuse before POST or financial writes.</summary>
    /// <param name="scenario">One isolated safety invariant invalidated after the historical failure.</param>
    [Theory]
    [InlineData("uuid")][InlineData("subid")][InlineData("email")][InlineData("missing_receipt")]
    [InlineData("unconfirmed")][InlineData("two_receipts")][InlineData("receipt_tenant")][InlineData("receipt_amount")]
    [InlineData("wallet_receipt")][InlineData("site_uncertain")][InlineData("ledger")][InlineData("revoke")]
    [InlineData("partial")][InlineData("pending_mutation")][InlineData("creation_ambiguous")]
    [InlineData("wrong_provider")][InlineData("renewal")][InlineData("no_provisional")][InlineData("frozen_quota")]
    public async Task ProvisionalReview_unsafe_evidence_is_refused_without_effects(string scenario)
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        await using (var db = dbs.Users.CreateDbContext())
        {
            var o = await db.TenantBotOrders.SingleAsync(); var s = await db.TenantCardProvisionalOperations.SingleAsync();
            var r = await db.TenantManualPaymentReceipts.SingleAsync();
            switch (scenario)
            {
                case "uuid": panel.SetClientIdentity(f.Email, Guid.NewGuid().ToString(), f.SubId); break;
                case "subid": panel.SetClientIdentity(f.Email, f.Uuid, "other"); break;
                case "email": o.ProvisionalAccountEmail = "missing"; break;
                case "missing_receipt": db.Remove(r); break;
                case "unconfirmed": r.FinalConfirmedAtUtc = null; break;
                case "two_receipts": db.Add(new TenantManualPaymentReceipt { TenantBotOrderId = o.Id, Status = TenantManualPaymentReceiptStatuses.Approved }); break;
                case "receipt_tenant": r.TenantBotId = "another-store"; break;
                case "receipt_amount": r.AmountToman++; break;
                case "wallet_receipt": await f.Wallet.Pay(new CredUser { TelegramUserId = f.Owner }, o.BaseCostToman, $"tenant:{o.Id}:base-cost"); break;
                case "site_uncertain": db.Add(new SiteWalletDebitOperation { Id = $"site:{f.Owner}:tenant-base-cost:{o.Id}", OwnerTelegramUserId = f.Owner, AmountToman = o.BaseCostToman }); break;
                case "ledger": db.Add(new TenantBotLedgerEntry { TenantBotOrderId = o.Id }); break;
                case "revoke": db.Add(new TenantCardProvisionalOperation { OperationKey = "test-revoke", TenantBotOrderId = o.Id, OrderId = o.OrderId, Kind = "revoke", Step = "claimed" }); break;
                case "partial": s.ResetAtUtc = DateTime.UtcNow; panel.SetClientQuota(f.Email, OneGib * 2, s.FinalExpiryTimeMs!.Value); break;
                case "pending_mutation": s.MutationPending = true; break;
                case "creation_ambiguous": (await db.XuiV3CreationOperations.SingleAsync()).Outcome = XuiV3CreationOutcome.Ambiguous; break;
                case "wrong_provider": o.PaymentProvider = "atlaspay"; break;
                case "renewal": o.OrderKind = TenantBotOrderKinds.Renew; break;
                case "no_provisional": o.ProvisionalAccountUuid = null; break;
                case "frozen_quota": s.FinalQuotaBytes++; break;
            }
            await db.SaveChangesAsync();
        }
        var before = await f.Wallet.GetAccountBalance(f.Owner); var posts = panel.TotalPostCount;
        Assert.False((await f.Probe()).Eligible); Assert.Null(await f.Prepare());
        Assert.Equal(posts, panel.TotalPostCount); Assert.Equal(before, await f.Wallet.GetAccountBalance(f.Owner));
        Assert.Equal("manual_review", (await f.Saga()).Step);
    }

    /// <summary>Failed GET preserves every review/saga field; an ambiguous committed POST is sent once and can only be GET-proven.</summary>
    [Fact]
    public async Task ProvisionalReview_transport_failures_do_not_rewind_or_replay()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var old = await f.Saga(); panel.FailReads = true;
        Assert.Equal("panel_unavailable", (await f.Probe()).Reason);
        Assert.Equal(old.UpdatedAtUtc, (await f.Saga()).UpdatedAtUtc); Assert.Equal(0, panel.TotalPostCount);
        panel.FailReads = false;
        var approval = await f.Prepare(); panel.FailUpdateAfterCommit = true;
        // A real retry budget must still yield ONE POST: zero configured retries would conceal a transport-policy regression.
        f.Services.GetRequiredService<IConfiguration>()["xuiV3TransientRetryCount"] = "3";
        Assert.Equal("panel_mutation_uncertain", await f.Execute(approval!.Value));
        Assert.Equal(1, panel.UpdateRequestCount); Assert.True((await f.Saga()).MutationPending);
        panel.FailUpdateAfterCommit = false;
        var p = await f.Probe(); Assert.True(p.Eligible, p.Reason); Assert.Equal("proven", p.ResumeStep);
        var posts = panel.TotalPostCount; var retry = await f.Prepare();
        Assert.Equal("fulfilled", await f.Execute(retry!.Value)); Assert.Equal(posts, panel.TotalPostCount);
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner));
    }

    /// <summary>Copied, expired, fabricated and stale callbacks cannot use another actor/bot/chat's confirmation.</summary>
    [Fact]
    public async Task ProvisionalReview_authorization_binding_and_stale_confirmation_fail_closed()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var id = await f.Prepare();
        Assert.Equal("unauthorized", await f.Review.ExecuteAsync(new(id!.Value, f.Owner, "review-owned", f.Owner), default));
        Assert.Equal("unauthorized", await f.Review.ExecuteAsync(new(id.Value, f.Admin, "unknown-host", f.Admin), default));
        Assert.Equal("unauthorized", await f.Review.ExecuteAsync(new(id.Value, f.Admin, f.Order.TenantBotId, f.Admin), default));
        Assert.Equal("unauthorized", await f.Review.ExecuteAsync(new(id.Value, f.Admin, "review-owned", -123), default));
        Assert.Equal("confirmation_invalid", await f.Execute(999999));
        await using (var db = dbs.Users.CreateDbContext())
        {
            var order = await db.TenantBotOrders.SingleAsync(); order.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1); await db.SaveChangesAsync();
        }
        Assert.Equal("confirmation_snapshot_changed", await f.Execute(id.Value));
        Assert.Equal(0, panel.TotalPostCount); Assert.Equal(1000000, await f.Wallet.GetAccountBalance(f.Owner));
        var expires = await f.Prepare();
        await using (var db = dbs.Users.CreateDbContext()) await db.TenantCardProvisionalReviews.Where(x => x.Id == expires)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal("confirmation_stale", await f.Execute(expires!.Value));
        var cancelled = await f.Prepare();
        Assert.True(await f.Review.CancelAsync(new(cancelled!.Value, f.Admin, "review-owned", f.Admin), default));
        Assert.Equal("confirmation_stale", await f.Execute(cancelled.Value));
        Assert.Equal(0, panel.TotalPostCount);
    }

    /// <summary>The actual UI rejects unauthorized and malformed callbacks; GET reprobe and prepare remain distinct from second confirmation.</summary>
    [Fact]
    public async Task ProvisionalReview_private_ui_requires_separate_confirmation_and_rejects_forgery()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var registry = f.Services.GetRequiredService<BotRegistry>();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = registry.GetById("review-owned") });
        var http = new ReviewTelegramHttp(); var client = new TelegramBotClient("123456:test-only", new HttpClient(http));
        var ui = new TenantCardProvisionalManualReviewAdminService(f.Review, new UserStateStore(dbs.Users), new XuiV3PurchaseSessionStore());
        CallbackQuery Query(string data, long actor) => new() { Id = Guid.NewGuid().ToString("N"), Data = data,
            From = new Telegram.Bot.Types.User { Id = actor }, Message = new Message { Chat = new Chat { Id = actor, Type = ChatType.Private } } };
        await ui.TryHandleCallbackAsync(client, Query($"tcpm:y:1", f.Owner), default);
        await ui.TryHandleCallbackAsync(client, Query("tcpm:y:not-a-number", f.Admin), default);
        await ui.TryHandleCallbackAsync(client, Query("tcpm:y:999999", f.Admin), default);
        Assert.Equal(0, panel.TotalPostCount);
        await ui.TryHandleMessageAsync(client, new Message { Text = TenantCardProvisionalManualReviewAdminService.Action,
            From = new Telegram.Bot.Types.User { Id = f.Admin }, Chat = new Chat { Id = f.Admin, Type = ChatType.Private } }, default);
        await ui.TryHandleCallbackAsync(client, Query($"tcpm:r:{f.Order.Id}", f.Admin), default);
        await ui.TryHandleCallbackAsync(client, Query($"tcpm:a:{f.Order.Id}", f.Admin), default);
        await using var db = dbs.Users.CreateDbContext(); var review = await db.TenantCardProvisionalReviews.SingleAsync();
        Assert.Equal("reviewed", review.Status); Assert.Equal(0, panel.TotalPostCount);
        Assert.Contains(http.Bodies, b => b.Contains("tcpm:y:")); // Only the second distinct callback may enter central execution.
        await ui.TryHandleCallbackAsync(client, Query($"tcpm:y:{review.Id}", f.Admin), default);
        Assert.True((await db.TenantBotOrders.AsNoTracking().SingleAsync()).IsFulfilled);
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner));
    }

    /// <summary>An authorized crash after persisting intent but before any dispatch requires a new explicit review, never background replay.</summary>
    [Fact]
    public async Task ProvisionalReview_consumed_approval_survives_restart_without_auto_replay()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var id = await f.Prepare();
        Assert.True(await f.Review.AuthorizeUnderOrderGateAsync(f.Order, new(id!.Value, f.Admin, "review-owned", f.Admin), default));
        Assert.Equal("confirmation_stale", await f.Execute(id.Value));
        Assert.Equal(0, panel.TotalPostCount);
        using var scope = f.Services.CreateScope(); var review = scope.ServiceProvider.GetRequiredService<TenantCardProvisionalManualReviewService>();
        var fresh = await review.PrepareAsync(f.Order.Id, f.Admin, "review-owned", f.Admin, default);
        Assert.NotNull(fresh); Assert.NotEqual(id, fresh);
        Assert.Equal("fulfilled", await review.ExecuteAsync(new(fresh!.Value, f.Admin, "review-owned", f.Admin), default));
        Assert.Equal(900000, await f.Wallet.GetAccountBalance(f.Owner));
    }

    /// <summary>Disabling the courtesy feature after fresh proof cannot fall through to ordinary account creation.</summary>
    [Fact]
    public async Task ProvisionalReview_feature_disabled_during_authorization_never_creates_another_account()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var id = await f.Prepare();
        var config = f.Services.GetRequiredService<IConfiguration>();
        panel.AfterTrafficRead = () => config["TenantCardProvisionalDeliveryEnabled"] = "false";
        Assert.Equal("recovery_refused_or_incomplete", await f.Execute(id!.Value));
        Assert.Equal(0, panel.TotalPostCount); Assert.Equal(1000000, await f.Wallet.GetAccountBalance(f.Owner));
        await using var db = dbs.Users.CreateDbContext(); Assert.False((await db.TenantBotOrders.SingleAsync()).IsFulfilled);
    }

    /// <summary>A catalog duration change between fresh review and central resolution refuses before any mutation or settlement.</summary>
    [Fact]
    public async Task ProvisionalReview_catalog_changed_during_authorization_does_not_replace_frozen_entitlement()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        var frozen = await f.Saga(); var id = await f.Prepare();
        var config = f.Services.GetRequiredService<IConfiguration>();
        panel.AfterTrafficRead = () =>
        {
            panel.AfterTrafficRead = null;
            var path = config["XuiV3ServicePlansPath"]!;
            var json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            var normal = json["services"]!.Single(x => x["key"]!.Value<string>() == "normal");
            normal["durationOptions"]!.Single(x => x["key"]!.Value<string>() == "m1")["days"] = 31;
            File.WriteAllText(path, json.ToString(), new UTF8Encoding(false)); // Isolated fixture catalog, never repository or production config.
        };
        Assert.Equal("panel_proof_requires_manual_review", await f.Execute(id!.Value));
        Assert.Equal(0, panel.TotalPostCount); Assert.Equal(frozen.FinalExpiryTimeMs, (await f.Saga()).FinalExpiryTimeMs);
        Assert.Equal(1000000, await f.Wallet.GetAccountBalance(f.Owner));
    }

    /// <summary>Authorization freezes approved evidence against concurrent stale edits and later owner rejection/replacement.</summary>
    [Fact]
    public async Task ProvisionalReview_authorized_receipt_cannot_be_replaced_or_rejected_by_a_racing_workflow()
    {
        using var dbs = new Databases(); await using var panel = await FakeUpstreamPanel.StartAsync();
        using var f = await ProvisionalReviewFixture.CreateAsync(dbs, panel);
        await using (var db = dbs.Users.CreateDbContext())
        {
            db.Add(new TenantManualPaymentReceipt { TenantBotOrderId = f.Order.Id, OrderId = f.Order.OrderId, Status = "pending", UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var stale = new UserWorkflowStore(dbs.Users);
        var oldReceipt = await stale.ReadAsync(db => db.TenantManualPaymentReceipts.SingleAsync(x => x.Status == "approved"));
        var stalePending = new UserWorkflowStore(dbs.Users);
        var oldPending = await stalePending.ReadAsync(db => db.TenantManualPaymentReceipts.SingleAsync(x => x.Status == "pending"));
        var id = await f.Prepare();
        Assert.True(await f.Review.AuthorizeUnderOrderGateAsync(f.Order, new(id!.Value, f.Admin, "review-owned", f.Admin), default));
        oldReceipt.Status = "rejected"; oldReceipt.UpdatedAtUtc = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveAsync());
        oldPending.Status = "approved"; oldPending.FinalConfirmedAtUtc = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stalePending.SaveAsync());
        using var scope = f.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        Assert.Contains("قفل", await tenant.REJECTMANUALRECEIPTASYNC(oldReceipt.Id, f.Owner, default));
        Assert.Contains("قفل", await tenant.APPROVEMANUALRECEIPTASYNC(oldReceipt.Id, f.Owner, default));
        var method = typeof(TenantBotService).GetMethod("PERSISTTENANTMANUALRECEIPTASYNC", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(-2, await (Task<int>)method.Invoke(tenant, new object[] { f.Order.Id, "fake-photo", CancellationToken.None })!);
        await using var check = dbs.Users.CreateDbContext();
        var receipt = await check.TenantManualPaymentReceipts.SingleAsync(x => x.Id == oldReceipt.Id);
        Assert.Equal("approved", receipt.Status); Assert.NotNull(receipt.FinalConfirmedAtUtc);
        Assert.Equal(0, panel.TotalPostCount); Assert.Equal(1000000, await f.Wallet.GetAccountBalance(f.Owner));
    }

    /// <summary>Additive migration leaves historical manual rows unapproved and does not backfill reviews or finance.</summary>
    [Fact]
    public async Task ProvisionalReview_migration_preserves_historical_manual_rows_without_authorization()
    {
        using var dbs = new Databases(initialize: false); await using var db = dbs.Users.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        var previous = db.Database.GetMigrations().Last(x => !x.EndsWith("AddTenantCardProvisionalReviewedRecovery"));
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO TenantCardProvisionalOperations (Id,OperationKey,TenantBotOrderId,OrderId,Kind,Step,ErrorCode,FinalQuotaBytes,CreatedAtUtc,UpdatedAtUtc) VALUES (1,'old',694,'historic','finalize','manual_review','provisional_identity_placement_mismatch',107374182400,'2026-10-01','2026-10-01')");
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var row = await db.TenantCardProvisionalOperations.AsNoTracking().SingleAsync();
        Assert.Equal("manual_review", row.Step); Assert.Null(row.ReviewedRecoveryId); Assert.False(row.MutationPending);
        Assert.Equal(107374182400, row.FinalQuotaBytes); Assert.Empty(await db.TenantCardProvisionalReviews.ToListAsync());
        Assert.Empty(await db.TenantBotLedgerEntries.ToListAsync()); Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
    }

    /// <summary>Isolated real central-fulfillment fixture; every external connection is fake/disabled.</summary>
    private sealed class ProvisionalReviewFixture : IDisposable
    {
        /// <summary>Synthetic global Telegram sender ids; only Admin is in this fixture's allowlist.</summary>
        public readonly long Admin = 901, Owner = 902, Customer = 903;
        /// <summary>Synthetic immutable panel credentials, never loaded from production configuration.</summary>
        public readonly string Email = "review-account", Uuid = "11111111-2222-3333-4444-555555555555", SubId = "review-sub";
        /// <summary>Existing approved users.db purchase whose identity is bound to the courtesy client.</summary>
        public TenantBotOrder Order = null!;
        /// <summary>Catalog-resolved active panel inbound ids, not a hardcoded production placement.</summary>
        public int[] Inbounds = Array.Empty<int>();
        /// <summary>Same-client finalization request using only isolated test identities and loopback connection.</summary>
        public TenantCardProvisionalFinalizationRequest Request = null!;
        /// <summary>Production finalizer over the isolated saga store.</summary>
        public TenantCardProvisionalFinalizationService Finalizer = null!;
        /// <summary>Durable isolated operation/audit store shared across simulated callback scopes.</summary>
        public TenantCardProvisionalOperationStore Store = null!;
        /// <summary>Isolated credentials.db receipts and owner balance.</summary>
        public CredentialsStore Wallet = null!;
        /// <summary>Isolated service graph; central execution receives a new workflow tracker in each scope.</summary>
        public ServiceProvider Services = null!;
        /// <summary>Reviewer used for read-only preparations; executions use fresh scopes below.</summary>
        public TenantCardProvisionalManualReviewService Review => Services.GetRequiredService<TenantCardProvisionalManualReviewService>();
        private IDisposable? _context;

        /// <summary>Seeds exact approved order/creation evidence and the historical pre-mutation placement failure.</summary>
        /// <param name="dbs">Fresh isolated users/credentials databases.</param><param name="panel">Loopback panel owned by this test.</param>
        /// <returns>Fixture with no live network dependency or production paths.</returns>
        public static async Task<ProvisionalReviewFixture> CreateAsync(Databases dbs, FakeUpstreamPanel panel)
        {
            var f = new ProvisionalReviewFixture();
            var catalog = WriteTenantCatalog(dbs, _ => { });
            var config = BuildConfiguration(dbs, catalog, panel.Url);
            config["TenantCardProvisionalDeliveryEnabled"] = "true";
            config["AdminsUserIds:0"] = f.Admin.ToString();
            config["Bots:0:Id"] = "review-owned"; config["Bots:0:Token"] = "123456:test-only";
            config["Bots:0:Type"] = "owned"; config["UserActivityLogEnabled"] = "false";
            var registry = new BotRegistry(config);
            var tenant = new BotInstance { Id = "tenant-review", Username = "review_store", Token = "123457:test-only",
                Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = f.Owner, Enabled = true };
            registry.Upsert(tenant);
            f._context = new BotContextAccessor().Push(new BotRuntimeContext { Config = registry.GetById("review-owned") });
            var purchase = new XuiV3PurchaseService(config, dbs.Users);
            var resolved = purchase.ResolveTenantPurchase(new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1 }, false);
            f.Inbounds = XuiV3PurchaseService.ResolveServiceInboundIds(resolved.Service).Order().ToArray();
            SeedProvisionalClient(panel, f.Email, f.Uuid, f.SubId, 999);
            f.Order = new TenantBotOrder { OrderId = "TEST-PROVISIONAL-REVIEW", TenantBotId = tenant.Id, TenantBotUsername = tenant.Username,
                OwnerTelegramUserId = f.Owner, CustomerTelegramUserId = f.Customer, PaymentProvider = "tenant_card", OrderKind = "purchase",
                ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1, SalePriceToman = 200000, BaseCostToman = 100000,
                PaymentStatus = "paid", ProvisionalDeliveryState = "manual_review", ProvisionalAccountEmail = f.Email,
                ProvisionalAccountUuid = f.Uuid, ProvisionalSubId = f.SubId, UpdatedAtUtc = DateTime.UtcNow };
            await using (var db = dbs.Users.CreateDbContext())
            {
                db.Add(tenant); db.Add(f.Order); await db.SaveChangesAsync();
                var receipt = new TenantManualPaymentReceipt { TenantBotOrderId = f.Order.Id, OrderId = f.Order.OrderId, TenantBotId = tenant.Id,
                    OwnerTelegramUserId = f.Owner, CustomerTelegramUserId = f.Customer, AmountToman = f.Order.SalePriceToman,
                    Status = "approved", ApprovedAtUtc = DateTime.UtcNow, FinalConfirmedAtUtc = DateTime.UtcNow };
                db.Add(receipt); await db.SaveChangesAsync(); f.Order.ManualReceiptId = receipt.Id; await db.SaveChangesAsync();
                db.Add(new XuiV3CreationOperation { OperationKey = TenantCardProvisionalProvisioningService.BuildCreateOperationKey(f.Order.OrderId),
                    PanelKey = XuiV3LinkChangeOperationStore.BuildPanelKey(panel.ServerInfo), Outcome = XuiV3CreationOutcome.Applied,
                    TelegramUserId = f.Customer, ClientJson = JsonConvert.SerializeObject(new XuiV3ClientPayload { Email = f.Email, Uuid = f.Uuid, SubId = f.SubId }),
                    InboundIdsJson = JsonConvert.SerializeObject(f.Inbounds), CreatedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync();
            }
            await using (var db = dbs.Credentials.CreateDbContext())
            { db.Users.AddRange(new CredUser { TelegramUserId = f.Owner, AccountBalance = 1000000 }, new CredUser { TelegramUserId = f.Customer }); await db.SaveChangesAsync(); }
            f.Store = new(dbs.Users); f.Wallet = new(dbs.Credentials);
            f.Finalizer = new(f.Store, config, NullLogger<TenantCardProvisionalFinalizationService>.Instance);
            f.Request = new(f.Order.Id, f.Order.OrderId, panel.ServerInfo, f.Email, f.Uuid, f.SubId, f.Inbounds, resolved.TrafficBytes, resolved.DurationDays);
            Assert.Equal(TenantCardProvisionalFinalizationStatus.ManualReview, (await f.Finalizer.FinalizeAsync(f.Request, default)).Status);
            panel.AttachInbounds(f.Email, f.Inbounds);
            var services = new ServiceCollection();
            services.AddSingleton(config); services.AddSingleton<IConfiguration>(config); services.AddSingleton(dbs.Users);
            services.AddSingleton(f.Store); services.AddSingleton(f.Wallet); services.AddSingleton(purchase); services.AddSingleton(registry);
            services.AddScoped<TenantCardProvisionalManualReviewService>();
            services.AddScoped(_ => new UserWorkflowStore(dbs.Users));
            services.AddScoped<TenantBotService>(sp => new TenantBotService(
                sp.GetRequiredService<UserWorkflowStore>(), new UserStateStore(dbs.Users), f.Wallet, config, purchase,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, registry,
                new BotClientProvider(registry, _ => new TelegramBotClient("123456:test-only", new HttpClient(new ReviewTelegramHttp()))),
                new BotContextAccessor(), null!, new WalletLedgerService(dbs.Users, f.Wallet), null!,
                new GozargahSiteSyncService(dbs.Users, f.Wallet, new GozargahSiteApiClient(config, NullLogger<GozargahSiteApiClient>.Instance), config, NullLogger<GozargahSiteSyncService>.Instance),
                null!, null!, sp, NullLogger<TenantBotService>.Instance, null!, null!, null!,
                new TenantCardProvisionalProvisioningService(dbs.Users, purchase, new XuiV3CreationOperationStore(dbs.Users), config, NullLogger<TenantCardProvisionalProvisioningService>.Instance),
                new TenantCardProvisionalFinalizationService(f.Store, config, NullLogger<TenantCardProvisionalFinalizationService>.Instance),
                new TenantCardProvisionalRevocationService(f.Store, config, NullLogger<TenantCardProvisionalRevocationService>.Instance)));
            f.Services = services.BuildServiceProvider(); return f;
        }
        /// <summary>Reprobes as the authorized private owned-host administrator.</summary>
        /// <returns>Sanitized GET-only proof, without consuming any authorization.</returns>
        public Task<TenantCardReviewProbe> Probe() => Review.ProbeAsync(Order.Id, Admin, "review-owned", Admin, default);
        /// <summary>Prepares a new actor-bound second confirmation.</summary>
        /// <returns>Numeric durable audit id or null for refused/stale evidence.</returns>
        public Task<long?> Prepare() => Review.PrepareAsync(Order.Id, Admin, "review-owned", Admin, default);
        /// <summary>Executes through a fresh scoped central fulfillment graph, as production does per callback.</summary>
        /// <param name="id">Prepared review id.</param><returns>Durable safe outcome.</returns>
        public async Task<string> Execute(long id)
        { using var scope = Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<TenantCardProvisionalManualReviewService>().ExecuteAsync(new(id, Admin, "review-owned", Admin), default); }
        /// <summary>Reads the durable finalize saga without a tracker.</summary>
        /// <returns>Exact detached saga snapshot for frozen-target and mutation-boundary assertions.</returns>
        public Task<TenantCardProvisionalOperation> Saga() => Store.FindAsync(FinalizeKey(Order.OrderId), default);
        /// <summary>Stops fixture scopes before their database directory is removed.</summary>
        public void Dispose() { Services.Dispose(); _context?.Dispose(); }
    }

    /// <summary>In-memory Telegram HTTP responses; prevents tests from contacting Telegram on success or error notices.</summary>
    private sealed class ReviewTelegramHttp : HttpMessageHandler
    {
        /// <summary>Captured fake Telegram request bodies for UI contract assertions; contains synthetic test data only.</summary>
        public List<string> Bodies { get; } = new();
        /// <summary>Returns a minimal accepted SDK message/ack response without network I/O.</summary>
        /// <param name="request">SDK request; never logged.</param><param name="cancellationToken">Test request cancellation.</param>
        /// <returns>Successful Telegram-shaped response.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var json = request.RequestUri!.AbsolutePath.Contains("answerCallbackQuery", StringComparison.OrdinalIgnoreCase)
                ? "{\"ok\":true,\"result\":true}" : "{\"ok\":true,\"result\":{\"message_id\":1,\"date\":0,\"chat\":{\"id\":901,\"type\":\"private\"}}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
