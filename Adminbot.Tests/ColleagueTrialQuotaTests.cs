using Adminbot.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>Verifies global Tehran-day colleague quota, immutable delivery identities and sole-executor settlement boundaries.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Exactly three free accounts share one global allowance across owned bots and both service types.</summary>
    /// <returns>A task after admission, independent-user capacity and absence of financial side effects are checked.</returns>
    /// <remarks>Origin bots are audit identities, not partitions; normal and national trials cannot each receive three slots.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Three_global_slots_then_fourth_denied_without_financial_effects()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var first = await quotas.ReserveAsync(901, "owned-a", "national", "trial:owned-a:901:1", 3, now);
        var second = await quotas.ReserveAsync(901, "owned-b", "normal", "trial:owned-b:901:2", 3, now);
        var third = await quotas.ReserveAsync(901, "owned-c", "national", "trial:owned-c:901:3", 3, now);
        var denied = await quotas.ReserveAsync(901, "owned-b", "normal", "trial:owned-b:901:4", 3, now);
        Assert.All(new[] { first, second, third }, x => Assert.Equal(ColleagueTrialGrantState.Reserved, x.State));
        Assert.Equal(ColleagueTrialGrantState.Denied, denied.State);
        Assert.Equal(ColleagueTrialGrantState.Reserved,
            (await quotas.ReserveAsync(902, "owned-a", "normal", "trial:owned-a:902:1", 3, now)).State);
        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(3, await db.ColleagueTrialGrants.CountAsync(x => x.TelegramUserId == 901 && x.State == ColleagueTrialGrantState.Reserved));
        Assert.Equal(0, await db.WalletLedgerEntries.CountAsync());
        Assert.Equal(0, await db.TenantBotLedgerEntries.CountAsync());
        Assert.Equal(0, await db.TenantBotOrders.CountAsync());
        await using var credentials = databases.Credentials.CreateDbContext();
        Assert.Equal(0, await credentials.WalletOperations.CountAsync());
    }

    /// <summary>Concurrent independent stores and owned bots cannot exceed a configurable cap, including the first-ever grant.</summary>
    /// <param name="limit">Configured free-account count; not a fixed hard-coded three.</param>
    /// <returns>A task after all real SQLite contenders complete and exactly the configured number occupy capacity.</returns>
    /// <remarks>The barrier starts requests together against an empty quota table to protect the no-existing-lock-row case.</remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ColleagueTrialQuota_Concurrent_bots_and_services_never_over_admit(int limit)
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            await start.Task;
            var independent = new ColleagueTrialQuotaStore(databases.Users);
            return await independent.ReserveAsync(901, $"owned-{i % 3}", i % 2 == 0 ? "normal" : "national",
                $"trial:owned-{i % 3}:901:{i + 1}", limit, now);
        })).ToArray();
        start.SetResult();
        var grants = await Task.WhenAll(attempts);
        Assert.Equal(limit, grants.Count(x => x.State == ColleagueTrialGrantState.Reserved));
        Assert.Equal(12 - limit, grants.Count(x => x.State == ColleagueTrialGrantState.Denied));
        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(limit, await db.ColleagueTrialGrants.CountAsync(x => x.State != ColleagueTrialGrantState.Denied));
    }

    /// <summary>Same-event duplicates retain a single immutable creation identity and one quota slot after fresh-factory restart.</summary>
    /// <returns>A task after concurrent duplicates, durable replay and remaining capacity are checked.</returns>
    /// <remarks>Changing the retry day or configured limit must not replace the original event's operation key or grant date.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Duplicate_event_is_one_slot_and_restart_keeps_original_identity()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new ColleagueTrialQuotaStore(databases.Users).ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 3, now))));
        var original = attempts[0];
        Assert.All(attempts, x => Assert.Equal((original.Id, original.OperationKey, original.GrantDateIran, original.State),
            (x.Id, x.OperationKey, x.GrantDateIran, x.State)));
        var restarted = RestartColleagueQuotaStore(databases);
        var replay = await restarted.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 0, now.AddDays(1));
        Assert.Equal((original.Id, original.OperationKey, original.GrantDateIran, ColleagueTrialGrantState.Reserved),
            (replay.Id, replay.OperationKey, replay.GrantDateIran, replay.State));
        Assert.Equal(ColleagueTrialGrantState.Reserved,
            (await restarted.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 3, now)).State);
        Assert.Equal(ColleagueTrialGrantState.Reserved,
            (await restarted.ReserveAsync(901, "owned-c", "normal", "trial:owned-c:901:3", 3, now)).State);
        Assert.Equal(ColleagueTrialGrantState.Denied,
            (await restarted.ReserveAsync(901, "owned-b", "normal", "trial:owned-b:901:4", 3, now)).State);
        await using var db = databases.Users.CreateDbContext();
        Assert.Single(await db.ColleagueTrialGrants.Where(x => x.DeliveryRequestKey == original.DeliveryRequestKey).ToListAsync());
    }

    /// <summary>Tehran midnight resets immediately, UTC midnight does not, and previously denied events remain terminal.</summary>
    /// <returns>A task after adjacent Tehran dates and same-event replay across the boundary are checked.</returns>
    /// <remarks>2026 Tehran is UTC+03:30: 20:29:59 and 20:30:00 UTC are adjacent local calendar days, one second apart.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Resets_at_Tehran_midnight_not_UTC_or_rolling_twenty_four_hours()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var before = new DateTime(2026, 10, 1, 20, 29, 59, DateTimeKind.Utc);
        var original = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 1, before);
        var denied = await quotas.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 1, before);
        var nextDay = await quotas.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:3", 1, before.AddSeconds(1));
        Assert.Equal(new DateTime(2026, 10, 1), original.GrantDateIran);
        Assert.Equal(new DateTime(2026, 10, 2), nextDay.GrantDateIran);
        Assert.Equal(ColleagueTrialGrantState.Reserved, nextDay.State);
        var utcMidnight = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:4", 1,
            new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(ColleagueTrialGrantState.Denied, utcMidnight.State);
        var replay = await quotas.ReserveAsync(901, "owned-b", "national", denied.DeliveryRequestKey, 5, before.AddDays(1));
        Assert.Equal((denied.Id, denied.OperationKey, denied.GrantDateIran, ColleagueTrialGrantState.Denied),
            (replay.Id, replay.OperationKey, replay.GrantDateIran, replay.State));
    }

    /// <summary>Definitively not-created attempts release capacity but never permit replay of that delivery request.</summary>
    /// <param name="creationState">Absent, unused Reserved, or an authoritative rejection after the sole POST boundary.</param>
    /// <returns>A task after release, unused-POST fencing, terminal replay and reopened capacity are checked.</returns>
    /// <remarks>A Reserved creation is fenced to DefinitiveRejected before releasing; the durable key never returns to Reserved.</remarks>
    [Theory]
    [InlineData("absent")]
    [InlineData("reserved")]
    [InlineData("rejected")]
    public async Task ColleagueTrialQuota_Definitive_failure_reopens_capacity_without_replaying_request(string creationState)
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var creations = new XuiV3CreationOperationStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 1, now);
        Assert.True(await quotas.TryStartFreeCreationAsync(grant.Id, 901, "owned-a", now));
        if (creationState != "absent")
            await creations.ReserveAsync(ColleagueTrialCreation(grant), default);
        if (creationState == "rejected")
        {
            Assert.True(await creations.TryStartPostAsync(grant.OperationKey, default));
            await creations.MarkFailureAsync(grant.OperationKey, XuiV3CreationOutcome.DefinitiveRejected, default);
        }
        var released = await quotas.SettleAsync(grant.OperationKey, now.AddSeconds(1));
        Assert.Equal(ColleagueTrialGrantState.Released, released.State);
        Assert.Equal(now.AddSeconds(1), released.ReleasedAtUtc);
        Assert.True(released.FreeCreationStarted);
        Assert.False(await quotas.TryStartFreeCreationAsync(grant.Id, 901, "owned-a", now.AddSeconds(2)));
        if (creationState != "absent")
        {
            Assert.False(await creations.TryStartPostAsync(grant.OperationKey, default));
            Assert.False((await creations.ReserveAsync(ColleagueTrialCreation(grant), default)).MayCreate);
        }
        var replacement = await quotas.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 1, now);
        Assert.Equal(ColleagueTrialGrantState.Reserved, replacement.State);
        var replay = await RestartColleagueQuotaStore(databases).ReserveAsync(901, "owned-a", "normal", grant.DeliveryRequestKey, 5, now.AddDays(1));
        Assert.Equal((grant.Id, grant.OperationKey, grant.GrantDateIran, ColleagueTrialGrantState.Released),
            (replay.Id, replay.OperationKey, replay.GrantDateIran, replay.State));
        Assert.True(replay.FreeCreationStarted);
        Assert.False(await RestartColleagueQuotaStore(databases).TryStartFreeCreationAsync(grant.Id, 901, "owned-a", now.AddDays(1)));
        Assert.Equal(ColleagueTrialGrantState.Denied,
            (await quotas.ReserveAsync(901, "owned-c", "normal", "trial:owned-c:901:3", 1, now)).State);
    }

    /// <summary>Authorized or ambiguous POST keeps the slot; later success proof consumes it regardless of delivery failure.</summary>
    /// <param name="ambiguous">True records transport uncertainty; false models a crash immediately after POST authorization.</param>
    /// <returns>A task after durable uncertainty, same-day denial, proof reconciliation and repeated settlement are checked.</returns>
    /// <remarks>Neither elapsed time nor retrying settlement can release an unknown or proven-created account.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColleagueTrialQuota_Post_boundary_holds_and_creation_proof_survives_notification_failure(bool ambiguous)
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var creations = new XuiV3CreationOperationStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 1, now);
        Assert.True(await quotas.TryStartFreeCreationAsync(grant.Id, 901, "owned-a", now));
        await creations.ReserveAsync(ColleagueTrialCreation(grant), default);
        Assert.True(await creations.TryStartPostAsync(grant.OperationKey, default));
        if (ambiguous)
            await creations.MarkFailureAsync(grant.OperationKey, XuiV3CreationOutcome.Ambiguous, default);
        Assert.Equal(ColleagueTrialGrantState.Uncertain, (await quotas.SettleAsync(grant.OperationKey, now.AddMinutes(5))).State);
        var restarted = RestartColleagueQuotaStore(databases);
        var stillHeld = await restarted.SettleAsync(grant.OperationKey, now.AddDays(30));
        Assert.Equal(ColleagueTrialGrantState.Uncertain, stillHeld.State);
        Assert.Null(stillHeld.ReleasedAtUtc);
        Assert.Equal(ColleagueTrialGrantState.Denied,
            (await restarted.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 1, now.AddMinutes(10))).State);

        // The quota caller must settle from the durable Applied proof even when account-detail construction
        // or Telegram delivery failed afterwards. Repeated failure-path settlement cannot undo that proof.
        await creations.MarkAppliedAsync(grant.OperationKey, default);
        var consumed = await restarted.SettleAsync(grant.OperationKey, now.AddDays(30).AddSeconds(1));
        Assert.Equal(ColleagueTrialGrantState.Consumed, consumed.State);
        Assert.Equal(now.AddDays(30).AddSeconds(1), consumed.ConsumedAtUtc);
        var repeated = await restarted.SettleAsync(grant.OperationKey, now.AddDays(31));
        Assert.Equal((consumed.State, consumed.ConsumedAtUtc, consumed.UpdatedAtUtc),
            (repeated.State, repeated.ConsumedAtUtc, repeated.UpdatedAtUtc));
        Assert.Null(repeated.ReleasedAtUtc);
        Assert.False(await creations.TryStartPostAsync(grant.OperationKey, default));
        Assert.Equal(ColleagueTrialGrantState.Denied,
            (await restarted.ReserveAsync(901, "owned-c", "normal", "trial:owned-c:901:3", 1, now.AddMinutes(20))).State);
        var replay = await RestartColleagueQuotaStore(databases).ReserveAsync(901, "owned-a", "normal",
            grant.DeliveryRequestKey, 5, now.AddDays(31));
        Assert.Equal((grant.Id, grant.OperationKey, grant.GrantDateIran, ColleagueTrialGrantState.Consumed),
            (replay.Id, replay.OperationKey, replay.GrantDateIran, replay.State));
        Assert.Null(await restarted.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1200, now.AddDays(31)));
    }

    /// <summary>Zero is paid-only; exact sender/origin binding and Denied-only quotes protect paid-test offer identity.</summary>
    /// <returns>A task after paid quote access, persistence and non-Denied rejection are checked.</returns>
    /// <remarks>A quote is a positive whole-toman preview snapshot, not a new free grant, debit, ledger or order.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Zero_denies_freebies_but_preserves_actor_bound_paid_quote()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var denied = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 0, now);
        Assert.Equal(ColleagueTrialGrantState.Denied, denied.State);
        Assert.Null(await quotas.FindAsync(denied.Id, 902, "owned-a"));
        Assert.Null(await quotas.FindAsync(denied.Id, 901, "owned-b"));
        Assert.Null(await quotas.SetPaidQuoteAsync(denied.Id, 902, "owned-a", 1200, now));
        Assert.Null(await quotas.SetPaidQuoteAsync(denied.Id, 901, "owned-b", 1200, now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => quotas.SetPaidQuoteAsync(denied.Id, 901, "owned-a", 0, now));
        var quoted = await quotas.SetPaidQuoteAsync(denied.Id, 901, "owned-a", 1200, now.AddSeconds(1));
        Assert.NotNull(quoted);
        Assert.Equal(1200L, quoted.PaidQuoteToman);
        Assert.Equal((denied.Id, denied.OperationKey, denied.GrantDateIran, ColleagueTrialGrantState.Denied),
            (quoted.Id, quoted.OperationKey, quoted.GrantDateIran, quoted.State));
        var durable = await RestartColleagueQuotaStore(databases).FindAsync(denied.Id, 901, "owned-a");
        Assert.NotNull(durable);
        Assert.Equal(1200L, durable.PaidQuoteToman);
        var free = await quotas.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 1, now);
        Assert.Equal(ColleagueTrialGrantState.Reserved, free.State);
        Assert.Null(await quotas.SetPaidQuoteAsync(free.Id, 901, "owned-b", 1200, now));
        Assert.True(await quotas.TryStartFreeCreationAsync(free.Id, 901, "owned-b", now));
        await quotas.SettleAsync(free.OperationKey, now.AddSeconds(1));
        Assert.Null(await quotas.SetPaidQuoteAsync(free.Id, 901, "owned-b", 1200, now));
        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(0, await db.WalletLedgerEntries.CountAsync());
        Assert.Equal(0, await db.TenantBotOrders.CountAsync());
    }

    /// <summary>Invalid user ids/negative limits never create a grant, and an existing event cannot change actor, bot or service.</summary>
    /// <returns>A task after validation and immutable request ownership are checked against persisted receipts.</returns>
    /// <remarks>Missing configuration defaults remain the parent's responsibility; the store only accepts an explicit nonnegative limit.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Rejects_invalid_identity_limit_and_cross_request_key_reuse()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => quotas.ReserveAsync(0, "owned-a", "normal", "trial:invalid", 3, now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => quotas.ReserveAsync(901, "owned-a", "normal", "trial:negative", -1, now));
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 3, now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => quotas.ReserveAsync(902, "owned-a", "normal", grant.DeliveryRequestKey, 3, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => quotas.ReserveAsync(901, "owned-b", "normal", grant.DeliveryRequestKey, 3, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => quotas.ReserveAsync(901, "owned-a", "national", grant.DeliveryRequestKey, 3, now));
        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(grant.Id, (await db.ColleagueTrialGrants.SingleAsync()).Id);
    }

    /// <summary>Additive rollout preserves existing conversation state, performs no historical grant backfill and retains terminal receipts on rollback.</summary>
    /// <returns>A task after real migration upgrade, empty rollout, durable denial and refused destructive downgrade are checked.</returns>
    /// <remarks>Even an unused free quota denial is permanent event evidence; rollback may not erase it to reauthorize delivery. Exercise the quota downgrade before the unrelated irreversible logger cutover, then prove the complete upgrade preserves the receipt and conversation.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Migration_has_no_backfill_and_downgrade_cannot_erase_receipts()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        var migration = "20261001120000_AddColleagueTrialGrants";
        var migrations = db.Database.GetMigrations().ToList();
        var previous = migrations[migrations.IndexOf(migration) - 1];
        await db.GetService<IMigrator>().MigrateAsync(previous);
        db.BotUserStates.Add(new BotUserState { BotId = "owned-existing", TelegramUserId = 901, Flow = "active-purchase" });
        await db.SaveChangesAsync();
        // Reach the complete pre-cutover schema so the quota guard, rather than the unrelated
        // irreversible logger migration, rejects destructive rollback. Upgrade to latest afterward.
        await db.GetService<IMigrator>().MigrateAsync("20261009120000_AddTelegramEndpointRouting");
        Assert.Equal(0, await db.ColleagueTrialGrants.CountAsync());
        Assert.Equal("active-purchase", (await db.BotUserStates.AsNoTracking().SingleAsync()).Flow);
        var denied = await new ColleagueTrialQuotaStore(databases.Users).ReserveAsync(901, "owned-existing", "normal",
            "trial:owned-existing:901:1", 0, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(previous));
        Assert.Equal(denied.Id, (await db.ColleagueTrialGrants.AsNoTracking().SingleAsync()).Id);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(denied.Id, (await db.ColleagueTrialGrants.AsNoTracking().SingleAsync()).Id);
        Assert.Equal("active-purchase", (await db.BotUserStates.AsNoTracking().SingleAsync()).Flow);
    }

    /// <summary>Same-event free duplicates grant one executor; a crash before POST cannot free or replay its held request.</summary>
    /// <param name="reservedCreation">True also persists an unused creation identity before the simulated crash.</param>
    /// <returns>A task after concurrent claims, exact actor/bot gating, durable restart and held quota are checked.</returns>
    /// <remarks>Losers/restarted callers only read absent/Reserved evidence; they must not settle the still-started winner's grant.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColleagueTrialQuota_Free_claim_has_one_executor_and_crash_before_POST_remains_held(bool reservedCreation)
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var creations = new XuiV3CreationOperationStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 1, now);
        Assert.False(await quotas.TryStartFreeCreationAsync(grant.Id, 902, "owned-a", now));
        Assert.False(await quotas.TryStartFreeCreationAsync(grant.Id, 901, "owned-b", now));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new ColleagueTrialQuotaStore(databases.Users).TryStartFreeCreationAsync(grant.Id, 901, "owned-a", now))));
        Assert.Single(claims, x => x);
        if (reservedCreation)
            await creations.ReserveAsync(ColleagueTrialCreation(grant), default);
        var restarted = RestartColleagueQuotaStore(databases);
        var replay = await restarted.ReserveAsync(901, "owned-a", "normal", grant.DeliveryRequestKey, 1, now.AddDays(1));
        Assert.True(replay.FreeCreationStarted);
        Assert.Equal((grant.Id, grant.OperationKey, grant.GrantDateIran, ColleagueTrialGrantState.Reserved),
            (replay.Id, replay.OperationKey, replay.GrantDateIran, replay.State));
        Assert.False(await restarted.TryStartFreeCreationAsync(grant.Id, 901, "owned-a", now.AddDays(1)));
        var evidence = await creations.FindAsync(grant.OperationKey, default);
        if (reservedCreation)
            Assert.Equal(XuiV3CreationOutcome.Reserved, evidence.Outcome);
        else
            Assert.Null(evidence);
        Assert.Equal(ColleagueTrialGrantState.Denied,
            (await restarted.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 1, now.AddMinutes(1))).State);
    }

    /// <summary>Concurrent paid confirmations grant one executor, freeze the debit receipt amount and remain review-only after an unposted restart.</summary>
    /// <returns>A task after real SQLite claims, frozen price, origin/actor binding and absent-operation restart are checked.</returns>
    /// <remarks>Losers must not settle absent/Reserved creation or refund while the winning confirmation may still prepare its POST.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Paid_claim_has_one_executor_and_frozen_authoritative_receipt_price()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 0, now);
        await quotas.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1200, now);
        Assert.False(await quotas.TryStartPaidCreationAsync(grant.Id, 902, "owned-a", 1300, now));
        Assert.False(await quotas.TryStartPaidCreationAsync(grant.Id, 901, "owned-b", 1300, now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            quotas.TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 0, now));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new ColleagueTrialQuotaStore(databases.Users).TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 1300, now))));
        Assert.Single(claims, x => x);
        Assert.Null(await quotas.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1400, now.AddSeconds(1)));
        var restarted = RestartColleagueQuotaStore(databases);
        var persisted = await restarted.FindAsync(grant.Id, 901, "owned-a");
        Assert.NotNull(persisted);
        Assert.Equal((ColleagueTrialGrantState.Denied, ColleagueTrialPaidCreationState.Started, 1300L),
            (persisted.State, persisted.PaidCreationState, persisted.PaidQuoteToman!.Value));
        Assert.False(await restarted.TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 1300, now.AddDays(1)));
        Assert.Null(await new XuiV3CreationOperationStore(databases.Users).FindAsync($"colleague-paid-trial:{grant.Id}", default));
        Assert.Equal(ColleagueTrialGrantState.Reserved,
            (await restarted.ReserveAsync(901, "owned-b", "national", "trial:owned-b:901:2", 1, now)).State);
    }

    /// <summary>Only quiesced winning paid attempts settle; definitive failures fence unused POST while ambiguous/successful creation forbid refund.</summary>
    /// <param name="creationState">Durable paid-operation evidence after the winner's creation invocation has finished.</param>
    /// <param name="expected">Expected paid refund/uncertainty boundary independent of the Denied free quota state.</param>
    /// <returns>A task after durable settlement, one-POST fencing, terminal replay and authoritative success reconciliation are checked.</returns>
    /// <remarks>The absent/Reserved cases are winner-only completion, never an independent retry inspecting an in-flight Started grant.</remarks>
    [Theory]
    [InlineData("absent", ColleagueTrialPaidCreationState.Rejected)]
    [InlineData("reserved", ColleagueTrialPaidCreationState.Rejected)]
    [InlineData("rejected", ColleagueTrialPaidCreationState.Rejected)]
    [InlineData("started", ColleagueTrialPaidCreationState.Uncertain)]
    [InlineData("ambiguous", ColleagueTrialPaidCreationState.Uncertain)]
    [InlineData("applied", ColleagueTrialPaidCreationState.Applied)]
    public async Task ColleagueTrialQuota_Paid_settlement_fences_definitive_failure_and_preserves_possible_success(
        string creationState, ColleagueTrialPaidCreationState expected)
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var creations = new XuiV3CreationOperationStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "national", "trial:owned-a:901:1", 0, now);
        await quotas.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1200, now);
        Assert.True(await quotas.TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 1300, now));
        var operationKey = $"colleague-paid-trial:{grant.Id}";
        if (creationState != "absent")
            await creations.ReserveAsync(ColleagueTrialCreation(grant, operationKey), default);
        if (creationState is not ("absent" or "reserved"))
            Assert.True(await creations.TryStartPostAsync(operationKey, default));
        if (creationState == "rejected")
            await creations.MarkFailureAsync(operationKey, XuiV3CreationOutcome.DefinitiveRejected, default);
        else if (creationState == "ambiguous")
            await creations.MarkFailureAsync(operationKey, XuiV3CreationOutcome.Ambiguous, default);
        else if (creationState == "applied")
            await creations.MarkAppliedAsync(operationKey, default);
        Assert.Null(await quotas.SettlePaidCreationAsync(grant.Id, 902, "owned-a", now.AddSeconds(1)));
        Assert.Null(await quotas.SettlePaidCreationAsync(grant.Id, 901, "owned-b", now.AddSeconds(1)));
        var settled = await quotas.SettlePaidCreationAsync(grant.Id, 901, "owned-a", now.AddSeconds(1));
        Assert.NotNull(settled);
        Assert.Equal((ColleagueTrialGrantState.Denied, expected, 1300L),
            (settled.State, settled.PaidCreationState, settled.PaidQuoteToman!.Value));
        Assert.False(await RestartColleagueQuotaStore(databases).TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 1300, now.AddDays(1)));
        Assert.Null(await quotas.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1400, now.AddSeconds(2)));
        if (creationState != "absent")
            Assert.False(await creations.TryStartPostAsync(operationKey, default));
        if (creationState == "reserved")
            Assert.Equal(XuiV3CreationOutcome.DefinitiveRejected, (await creations.FindAsync(operationKey, default)).Outcome);
        var repeated = await quotas.SettlePaidCreationAsync(grant.Id, 901, "owned-a", now.AddSeconds(2));
        Assert.Equal((settled.PaidCreationState, settled.UpdatedAtUtc), (repeated.PaidCreationState, repeated.UpdatedAtUtc));
        Assert.False(await quotas.ClearPaidQuoteAsync(grant.Id, 901, "owned-a"));
        if (expected != ColleagueTrialPaidCreationState.Rejected)
            Assert.False(await quotas.MarkPaidRefundRecordedAsync(grant.Id, 901, "owned-a", now.AddSeconds(2)));
        if (expected == ColleagueTrialPaidCreationState.Uncertain)
        {
            await creations.MarkAppliedAsync(operationKey, default);
            var proven = await RestartColleagueQuotaStore(databases).SettlePaidCreationAsync(grant.Id, 901, "owned-a", now.AddSeconds(3));
            Assert.Equal(ColleagueTrialPaidCreationState.Applied, proven.PaidCreationState);
            Assert.Equal(ColleagueTrialGrantState.Denied, proven.State);
        }
    }

    /// <summary>Cancelling an unfunded paid quote preserves history, while started paid attempts retain their exact committed price.</summary>
    /// <returns>A task after actor-bound cancellation, terminal preview removal and post-claim quote preservation are checked.</returns>
    /// <remarks>The caller's no-debit verification is a precondition; this store changes no credentials.db or financial row.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Clear_unfunded_quote_preserves_receipt_and_cannot_clear_started_price()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 0, now);
        await quotas.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1200, now);
        Assert.False(await quotas.ClearPaidQuoteAsync(grant.Id, 902, "owned-a"));
        Assert.False(await quotas.ClearPaidQuoteAsync(grant.Id, 901, "owned-b"));
        Assert.True(await quotas.ClearPaidQuoteAsync(grant.Id, 901, "owned-a"));
        Assert.False(await quotas.ClearPaidQuoteAsync(grant.Id, 901, "owned-a"));
        var cancelled = await RestartColleagueQuotaStore(databases).FindAsync(grant.Id, 901, "owned-a");
        Assert.NotNull(cancelled);
        Assert.Null(cancelled.PaidQuoteToman);
        Assert.Equal((grant.OperationKey, grant.GrantDateIran, ColleagueTrialGrantState.Denied, ColleagueTrialPaidCreationState.NotStarted),
            (cancelled.OperationKey, cancelled.GrantDateIran, cancelled.State, cancelled.PaidCreationState));
        await quotas.SetPaidQuoteAsync(grant.Id, 901, "owned-a", 1250, now);
        Assert.True(await quotas.TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 1300, now));
        Assert.False(await quotas.ClearPaidQuoteAsync(grant.Id, 901, "owned-a"));
        var started = await quotas.FindAsync(grant.Id, 901, "owned-a");
        Assert.Equal(1300L, started.PaidQuoteToman);
        Assert.Equal(ColleagueTrialPaidCreationState.Started, started.PaidCreationState);
    }

    /// <summary>Refund completion is once-only, actor-bound and durable after conversations vanish; unproven paid outcomes cannot be marked refunded.</summary>
    /// <returns>A task after eligibility, rejected-refund discovery, concurrent marker admission and restarted proof are checked.</returns>
    /// <remarks>The caller has verified the exact receipt-linked credit and ledger before marking; the quota store performs neither itself.</remarks>
    [Fact]
    public async Task ColleagueTrialQuota_Refund_proof_is_rejected_only_once_and_independent_of_conversation()
    {
        using var databases = new Databases();
        var quotas = new ColleagueTrialQuotaStore(databases.Users);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var grant = await quotas.ReserveAsync(901, "owned-a", "normal", "trial:owned-a:901:1", 0, now);
        Assert.False(await quotas.MarkPaidRefundRecordedAsync(grant.Id, 901, "owned-a", now));
        Assert.True(await quotas.TryStartPaidCreationAsync(grant.Id, 901, "owned-a", 1300, now));
        Assert.False(await quotas.MarkPaidRefundRecordedAsync(grant.Id, 901, "owned-a", now));
        await quotas.SettlePaidCreationAsync(grant.Id, 901, "owned-a", now.AddSeconds(1));
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Equal(grant.Id, (await db.ColleagueTrialGrants.SingleAsync(x =>
                x.PaidCreationState == ColleagueTrialPaidCreationState.Rejected && x.PaidRefundRecordedAtUtc == null)).Id);
            Assert.Equal(0, await db.BotUserStates.CountAsync());
        }
        Assert.False(await quotas.MarkPaidRefundRecordedAsync(grant.Id, 902, "owned-a", now.AddSeconds(2)));
        Assert.False(await quotas.MarkPaidRefundRecordedAsync(grant.Id, 901, "owned-b", now.AddSeconds(2)));
        var markers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new ColleagueTrialQuotaStore(databases.Users).MarkPaidRefundRecordedAsync(grant.Id, 901, "owned-a", now.AddSeconds(2)))));
        Assert.Single(markers, x => x);
        Assert.False(await quotas.MarkPaidRefundRecordedAsync(grant.Id, 901, "owned-a", now.AddSeconds(3)));
        var recorded = await RestartColleagueQuotaStore(databases).FindAsync(grant.Id, 901, "owned-a");
        Assert.Equal(now.AddSeconds(2), recorded.PaidRefundRecordedAtUtc);
        Assert.Equal((ColleagueTrialGrantState.Denied, ColleagueTrialPaidCreationState.Rejected, 1300L),
            (recorded.State, recorded.PaidCreationState, recorded.PaidQuoteToman!.Value));
        await using var check = databases.Users.CreateDbContext();
        Assert.Equal(0, await check.ColleagueTrialGrants.CountAsync(x =>
            x.PaidCreationState == ColleagueTrialPaidCreationState.Rejected && x.PaidRefundRecordedAtUtc == null));
    }

    /// <summary>Creates a new factory/store over the same temporary file to model restart without any shared tracker.</summary>
    /// <param name="databases">The isolated fixture owning the durable users.db file.</param>
    /// <returns>A new quota store whose contexts reopen the fixture file.</returns>
    /// <remarks>Pooling is disabled exactly as in the fixture; no static application database path is changed.</remarks>
    private static ColleagueTrialQuotaStore RestartColleagueQuotaStore(Databases databases)
    {
        var connection = new SqliteConnectionStringBuilder(SqliteOperation.ConnectionString(Path.Combine(databases.DirectoryPath, "users.db")))
        { Pooling = false }.ToString();
        return new ColleagueTrialQuotaStore(new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>().UseSqlite(connection).Options));
    }

    /// <summary>Builds the immutable local free or paid creation reservation used to exercise the production POST state machine.</summary>
    /// <param name="grant">The admitted colleague receipt supplying the exact global owner.</param>
    /// <param name="operationKey">Optional exact deterministic paid operation key; null uses the grant's free operation key.</param>
    /// <returns>A deterministic private test identity for the grant; never a real panel request or account credential.</returns>
    /// <remarks>Provisioning remains outside the quota store; tests cross the actual durable authorization boundary without network I/O.</remarks>
    private static XuiV3CreationOperation ColleagueTrialCreation(ColleagueTrialGrant grant, string? operationKey = null) => new()
    {
        OperationKey = operationKey ?? grant.OperationKey,
        TelegramUserId = grant.TelegramUserId,
        PanelKey = "quota-test-panel-hash",
        ClientJson = "private-quota-test-identity",
        InboundIdsJson = "[1]",
        BusinessParametersJson = "{\"quotaBytes\":1073741824,\"durationDays\":3}",
        CreatedAtUtc = grant.CreatedAtUtc
    };
}
