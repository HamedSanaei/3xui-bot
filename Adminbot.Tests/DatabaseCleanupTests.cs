using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Regression coverage for the daily retention pass that keeps the application SQLite databases proportional to the
/// live workload.
/// </summary>
/// <remarks>
/// <para>
/// These tests protect two production invariants that are easy to break with a careless predicate:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// A completed, terminal row is eventually deleted, so the database stops growing by one row per Telegram update,
/// account creation, renewal, and payment forever.
/// </description>
/// </item>
/// <item>
/// <description>
/// A row that is still active, unresolved, awaiting an operator, or not yet financially settled is never deleted or
/// striped of its stored evidence, no matter how old it is. Renewal audit rows are compacted but never removed.
/// </description>
/// </item>
/// </list>
/// <para>
/// Every test seeds rows through Entity Framework against a fixture-owned temporary database and calls
/// <see cref="DatabaseCleanupRunner.RunOnceAsync" /> directly, so no host, listener, panel, or Telegram client is
/// started. The seeded payload values are obvious markers such as <c>payload-old</c> so a failed assertion shows
/// exactly which predicate drifted.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Verifies that only terminal account-creation reservations are removed and that their private client payload is
    /// cleared first, while unresolved reservations keep both the row and the payload.
    /// </summary>
    /// <returns>A task completing after the retention invariants have been asserted.</returns>
    /// <remarks>
    /// An ambiguous creation row can still be proven absent by an operator read-back, and a post-started row is a
    /// possible external effect. Deleting either would destroy the evidence that prevents a duplicate panel POST.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_deletes_only_terminal_creation_operations_and_keeps_unresolved_evidence()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedCreationOperationsAsync(databases,
            Creation("applied-old", XuiV3CreationOutcome.Applied, now.AddDays(-40), now.AddDays(-40)),
            Creation("rejected-old", XuiV3CreationOutcome.DefinitiveRejected, now.AddDays(-40), null),
            Creation("ambiguous-old", XuiV3CreationOutcome.Ambiguous, now.AddDays(-40), null),
            Creation("poststarted-old", XuiV3CreationOutcome.PostStarted, now.AddDays(-40), null),
            Creation("reserved-old", XuiV3CreationOutcome.Reserved, now.AddDays(-40), null),
            Creation("applied-recent", XuiV3CreationOutcome.Applied, now.AddDays(-1), now.AddDays(-1)));

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        var table = report.Tables.Single(row => row.Table == "XuiV3CreationOperations");
        Assert.Null(table.Failure);
        Assert.Equal(2, table.DeletedRows);

        await using var verify = databases.Users.CreateDbContext();
        var remaining = await verify.XuiV3CreationOperations.OrderBy(row => row.OperationKey).ToListAsync();
        Assert.Equal(new[] { "ambiguous-old", "applied-recent", "poststarted-old", "reserved-old" },
            remaining.Select(row => row.OperationKey).ToArray());
        // Unresolved rows keep the private payload an operator read-back still needs.
        Assert.Equal("payload-ambiguous-old", remaining.Single(row => row.OperationKey == "ambiguous-old").ClientJson);
        Assert.Equal("payload-poststarted-old", remaining.Single(row => row.OperationKey == "poststarted-old").ClientJson);
        // The recent applied row is inside both windows and therefore untouched.
        Assert.Equal("payload-applied-recent", remaining.Single(row => row.OperationKey == "applied-recent").ClientJson);
    }

    /// <summary>
    /// Verifies that a settled renewal keeps its financial audit row while its two heaviest JSON columns are cleared.
    /// </summary>
    /// <returns>A task completing after the compaction invariants have been asserted.</returns>
    /// <remarks>
    /// The renewal row is the exactly-once anchor for a wallet debit, so it must never be deleted. Its stored mutation
    /// payload and pre-mutation snapshot are only reachable while recovery can still run, which is never true after the
    /// row is both applied and settled.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_compacts_settled_renewal_payloads_and_never_deletes_renewal_audit()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedRenewalsAsync(databases,
            Renewal(1, XuiV3RenewalOperationStatuses.Applied, XuiV3RenewalSettlementStatuses.Settled, now.AddDays(-3)),
            Renewal(2, XuiV3RenewalOperationStatuses.Applied, XuiV3RenewalSettlementStatuses.Settled, now.AddDays(-1)),
            Renewal(3, XuiV3RenewalOperationStatuses.Applied, XuiV3RenewalSettlementStatuses.Pending, now.AddDays(-40)),
            Renewal(4, XuiV3RenewalOperationStatuses.ManualReview, XuiV3RenewalSettlementStatuses.Settled, now.AddDays(-40)));

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        var table = report.Tables.Single(row => row.Table == "XuiV3RenewalOperations");
        Assert.Null(table.Failure);
        Assert.Equal(0, table.DeletedRows);
        Assert.Equal(1, table.CompactedRows);

        await using var verify = databases.Users.CreateDbContext();
        var rows = await verify.XuiV3RenewalOperations.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Null(rows[0].MutationPayloadJson);
        Assert.Null(rows[0].PreMutationSnapshotJson);
        // Audit-relevant columns survive compaction.
        Assert.Equal(42_000, rows[0].PriceToman);
        Assert.Equal("client@example.test", rows[0].TargetEmail);
        Assert.Equal(XuiV3RenewalOperationStatuses.Applied, rows[0].Status);
        // A recent settlement still needs its payload, and an unsettled or escalated row always keeps it.
        Assert.NotNull(rows[1].MutationPayloadJson);
        Assert.NotNull(rows[2].MutationPayloadJson);
        Assert.NotNull(rows[3].MutationPayloadJson);
    }

    /// <summary>
    /// Verifies that finished link-change sagas are removed while every unresolved saga is preserved.
    /// </summary>
    /// <returns>A task completing after the link-change retention invariants have been asserted.</returns>
    /// <remarks>
    /// Awaiting-confirmation, processing, recovery-pending, and manual-review rows own the account lock that stops two
    /// identity mutations on one panel client, so deleting them would silently permit a duplicate rename.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_removes_finished_link_changes_and_preserves_active_sagas()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedLinkChangesAsync(databases,
            LinkChange(1, XuiV3LinkChangeStatuses.Succeeded, now.AddDays(-40)),
            LinkChange(2, XuiV3LinkChangeStatuses.AwaitingConfirmation, now.AddDays(-40)),
            LinkChange(3, XuiV3LinkChangeStatuses.ManualReview, now.AddDays(-60)),
            LinkChange(4, XuiV3LinkChangeStatuses.Processing, now.AddDays(-60)),
            LinkChange(5, XuiV3LinkChangeStatuses.Cancelled, now.AddDays(-1)));

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        var table = report.Tables.Single(row => row.Table == "XuiV3LinkChangeOperations");
        Assert.Null(table.Failure);
        Assert.Equal(1, table.DeletedRows);

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(new[] { 2, 3, 4, 5 },
            (await verify.XuiV3LinkChangeOperations.OrderBy(row => row.Id).Select(row => row.Id).ToListAsync()).ToArray());
    }

    /// <summary>
    /// Verifies that one pass deletes at most the configured batches, so a large backlog cannot hold a long write lock.
    /// </summary>
    /// <returns>A task completing after the batching invariant has been asserted.</returns>
    /// <remarks>
    /// Three rows per batch across two batches must remove exactly six rows and leave the remainder for the next pass.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_bounds_deletions_to_the_configured_batches()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedCreationOperationsAsync(databases,
            Enumerable.Range(0, 8)
                .Select(index => Creation("batch-" + index, XuiV3CreationOutcome.Applied, now.AddDays(-40), now.AddDays(-40)))
                .ToArray());

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases,
            ("DatabaseCleanup:BatchSize", "3"),
            ("DatabaseCleanup:MaxBatchesPerTable", "2")));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        Assert.Equal(6, report.Tables.Single(row => row.Table == "XuiV3CreationOperations").DeletedRows);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(2, await verify.XuiV3CreationOperations.CountAsync());
    }

    /// <summary>
    /// Verifies that old completed Telegram receipts expire while queued work and recent receipts are preserved.
    /// </summary>
    /// <returns>A task completing after the inbox retention invariants have been asserted.</returns>
    /// <remarks>
    /// A queued receipt is an update the bot still has to answer. Deleting it would drop a customer's message, so the
    /// cleanup predicate must stay terminal-only even for a very old queued row.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_prunes_only_completed_telegram_receipts()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.TelegramUpdateInbox.AddRange(
                Receipt(1, "completed", now.AddDays(-10), now.AddDays(-10)),
                Receipt(2, "completed_with_error", now.AddDays(-10), now.AddDays(-10)),
                Receipt(3, "completed", now.AddDays(-1), now.AddDays(-1)),
                Receipt(4, "queued", now.AddDays(-90), null));
            await seed.SaveChangesAsync();
        }

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        var table = report.Tables.Single(row => row.Table == "TelegramUpdateInbox");
        Assert.Null(table.Failure);
        Assert.Equal(2, table.DeletedRows);

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(new[] { 3, 4 },
            (await verify.TelegramUpdateInbox.OrderBy(row => row.UpdateId).Select(row => row.UpdateId).ToListAsync()).ToArray());
    }

    /// <summary>
    /// Verifies that provider payload copies are cleared only for financially terminal payment rows.
    /// </summary>
    /// <returns>A task completing after the payment compaction invariants have been asserted.</returns>
    /// <remarks>
    /// The <c>paid</c> HooshPay row that was never added to a balance is the important case: it is exactly the row a
    /// reconciliation or dispute path still needs, so it must keep its stored provider response while the credited rows
    /// are compacted. Payment rows themselves are never deleted because they are financial records.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_compacts_only_financially_terminal_payment_rows()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.HooshPayPaymentInfos.AddRange(
                new HooshPayPaymentInfo
                {
                    OrderId = "credited-old", BotId = "owned", TelegramUserId = 1, AmountToman = 100,
                    PaymentStatus = HooshPayStatuses.Paid, IsAddedToBalance = true, CreatedAtUtc = now.AddDays(-40),
                    RawRequestJson = "request-credited-old", RawResponseJson = "response-credited-old", RawIpnJson = "ipn-credited-old"
                },
                new HooshPayPaymentInfo
                {
                    OrderId = "paid-not-credited", BotId = "owned", TelegramUserId = 1, AmountToman = 100,
                    PaymentStatus = HooshPayStatuses.Paid, IsAddedToBalance = false, CreatedAtUtc = now.AddDays(-40),
                    RawResponseJson = "response-uncredited"
                },
                new HooshPayPaymentInfo
                {
                    OrderId = "expired-old", BotId = "owned", TelegramUserId = 1, AmountToman = 100,
                    PaymentStatus = HooshPayStatuses.Expired, IsAddedToBalance = false, CreatedAtUtc = now.AddDays(-40),
                    RawResponseJson = "response-expired"
                },
                new HooshPayPaymentInfo
                {
                    OrderId = "pending-old", BotId = "owned", TelegramUserId = 1, AmountToman = 100,
                    PaymentStatus = HooshPayStatuses.Pending, IsAddedToBalance = false, CreatedAtUtc = now.AddDays(-40),
                    RawResponseJson = "response-pending"
                },
                new HooshPayPaymentInfo
                {
                    OrderId = "credited-recent", BotId = "owned", TelegramUserId = 1, AmountToman = 100,
                    PaymentStatus = HooshPayStatuses.Paid, IsAddedToBalance = true, CreatedAtUtc = now.AddDays(-1),
                    RawResponseJson = "response-credited-recent"
                });
            // A second provider proves the same financial-terminal rule applies per table rather than once globally.
            seed.UniquePayPaymentInfos.Add(new UniquePayPaymentInfo
            {
                HashId = "uniquepay-credited-old", TelegramUserId = 1, BaseAmountToman = 100,
                PaymentStatus = UniquePayStatuses.Paid, IsAddedToBalance = true, CreatedAtUtc = now.AddDays(-40),
                RawRequestJson = "unique-request", RawResponseJson = "unique-response"
            });
            await seed.SaveChangesAsync();
        }

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        Assert.Null(report.Tables.Single(row => row.Table == "HooshPayPaymentInfos").Failure);
        Assert.Equal(2, report.Tables.Single(row => row.Table == "HooshPayPaymentInfos").CompactedRows);
        Assert.Equal(1, report.Tables.Single(row => row.Table == "UniquePayPaymentInfos").CompactedRows);

        await using var verify = databases.Users.CreateDbContext();
        var creditedOld = await verify.HooshPayPaymentInfos.SingleAsync(row => row.OrderId == "credited-old");
        Assert.Null(creditedOld.RawRequestJson);
        Assert.Null(creditedOld.RawResponseJson);
        Assert.Null(creditedOld.RawIpnJson);
        // Financial fields stay intact so the payment remains auditable by provider reference.
        Assert.Equal(100, creditedOld.AmountToman);
        Assert.True(creditedOld.IsAddedToBalance);

        Assert.Equal("response-uncredited", (await verify.HooshPayPaymentInfos.SingleAsync(row => row.OrderId == "paid-not-credited")).RawResponseJson);
        // A terminal provider failure can never be settled later, so its payload is removed as well.
        Assert.Null((await verify.HooshPayPaymentInfos.SingleAsync(row => row.OrderId == "expired-old")).RawResponseJson);
        Assert.Equal("response-pending", (await verify.HooshPayPaymentInfos.SingleAsync(row => row.OrderId == "pending-old")).RawResponseJson);
        Assert.Equal("response-credited-recent", (await verify.HooshPayPaymentInfos.SingleAsync(row => row.OrderId == "credited-recent")).RawResponseJson);
        Assert.Null((await verify.UniquePayPaymentInfos.SingleAsync(row => row.HashId == "uniquepay-credited-old")).RawResponseJson);
    }

    /// <summary>
    /// Verifies that the daily pass trims website outbox payloads on terminal rows and never touches a retryable one.
    /// </summary>
    /// <returns>A task completing after the outbox compaction invariants have been asserted.</returns>
    /// <remarks>
    /// The outbox keeps each account's latest successful event as a tombstone, so the row itself must survive while its
    /// request and response documents are dropped. A <c>pending</c> event still has to be sent, which means its request
    /// JSON is the retry payload and must be preserved at any age.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_compacts_terminal_website_outbox_payloads_only()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.GozargahSiteSyncEvents.AddRange(
                new GozargahSiteSyncEvent
                {
                    BotId = "owned", TelegramUserId = 1, Operation = GozargahSiteSyncOperations.Create,
                    Email = "terminal@example.test", Uuid = "terminal-uuid", Status = GozargahSiteSyncStatuses.Succeeded,
                    CreatedAtUtc = now.AddDays(-40), UpdatedAtUtc = now.AddDays(-40), SucceededAtUtc = now.AddDays(-40),
                    RequestJson = "terminal-request", ResponseJson = "terminal-response"
                },
                new GozargahSiteSyncEvent
                {
                    BotId = "owned", TelegramUserId = 1, Operation = GozargahSiteSyncOperations.Create,
                    Email = "pending@example.test", Uuid = "pending-uuid", Status = GozargahSiteSyncStatuses.Pending,
                    CreatedAtUtc = now.AddDays(-40), RequestJson = "pending-request"
                });
            await seed.SaveChangesAsync();
        }

        var configuration = CleanupConfiguration(databases);
        var compactor = new GozargahSiteSyncService(
            databases.Users,
            new CredentialsStore(databases.Credentials),
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration,
            NullLogger<GozargahSiteSyncService>.Instance);
        var runner = CreateCleanupRunner(databases, configuration);
        var report = await runner.RunOnceAsync(now, compactor, CancellationToken.None);

        var table = report.Tables.Single(row => row.Table == "GozargahSiteSyncEvents");
        Assert.Null(table.Failure);
        Assert.Equal(1, table.CompactedRows);
        Assert.Equal(0, table.DeletedRows);

        await using var verify = databases.Users.CreateDbContext();
        var terminal = await verify.GozargahSiteSyncEvents.SingleAsync(row => row.Email == "terminal@example.test");
        Assert.Null(terminal.RequestJson);
        Assert.Null(terminal.ResponseJson);
        Assert.Equal("terminal-uuid", terminal.Uuid);
        Assert.Equal("pending-request", (await verify.GozargahSiteSyncEvents.SingleAsync(row => row.Email == "pending@example.test")).RequestJson);
    }

    /// <summary>
    /// Verifies that a second pass performs no further work and that a pass which changed rows runs SQLite maintenance.
    /// </summary>
    /// <returns>A task completing after the idempotence and maintenance invariants have been asserted.</returns>
    /// <remarks>
    /// VACUUM rewrites the whole database and takes an exclusive lock, so it must be tied to an actual change. The
    /// second pass proves both that retention is idempotent and that a no-op pass skips maintenance.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_is_idempotent_and_vacuums_only_after_a_change()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedCreationOperationsAsync(databases,
            Creation("old-1", XuiV3CreationOutcome.Applied, now.AddDays(-40), now.AddDays(-40)),
            Creation("old-2", XuiV3CreationOutcome.Applied, now.AddDays(-41), now.AddDays(-41)));

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases));
        var first = await runner.RunOnceAsync(now, null, CancellationToken.None);
        Assert.Equal(2, first.DeletedRows);
        Assert.True(first.MaintenanceRan);
        Assert.True(first.UsersDatabaseBytesBefore > 0);
        Assert.True(first.UsersDatabaseBytesAfter > 0);

        var second = await runner.RunOnceAsync(now, null, CancellationToken.None);
        Assert.Equal(0, second.DeletedRows);
        Assert.Equal(0, second.CompactedRows);
        Assert.False(second.MaintenanceRan);
    }

    /// <summary>
    /// Verifies that disabling cleanup performs no deletion at all.
    /// </summary>
    /// <returns>A task completing after the disabled invariant has been asserted.</returns>
    /// <remarks>
    /// The switch is the operator's escape hatch. It must be absolute: a disabled pass deletes nothing even when rows
    /// are far beyond the retention window.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_disabled_deletes_nothing()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedCreationOperationsAsync(databases,
            Creation("old-1", XuiV3CreationOutcome.Applied, now.AddDays(-400), now.AddDays(-400)));

        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases, ("DatabaseCleanup:Enabled", "false")));
        var report = await runner.RunOnceAsync(now, null, CancellationToken.None);

        Assert.Equal(0, report.DeletedRows);
        Assert.False(report.MaintenanceRan);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(1, await verify.XuiV3CreationOperations.CountAsync());
    }

    /// <summary>
    /// Verifies that an invalid retention window refuses to run instead of deleting recent rows.
    /// </summary>
    /// <returns>A task completing after the validation failure has been asserted.</returns>
    /// <remarks>
    /// A zero-day window would make every terminal row immediately eligible, which is a data-loss event rather than a
    /// cleanup preference, so configuration validation must fail closed.
    /// </remarks>
    [Fact]
    public async Task Database_cleanup_rejects_a_non_positive_retention_window()
    {
        using var databases = new Databases();
        var runner = CreateCleanupRunner(databases, CleanupConfiguration(databases, ("DatabaseCleanup:RetentionDays", "0")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunOnceAsync(DateTime.UtcNow, null, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that a missing database path and a missing database file are reported as zero instead of throwing.
    /// </summary>
    /// <remarks>
    /// Size logging runs on every pass. It must never become the reason a cleanup pass fails on a fresh installation
    /// where the database file has not been created yet.
    /// </remarks>
    [Fact]
    public async Task Sqlite_maintenance_reports_zero_and_defers_for_an_unreachable_database()
    {
        using var databases = new Databases();
        var missing = Path.Combine(databases.DirectoryPath, "does-not-exist.db");
        var size = SqliteDatabaseMaintenance.Measure(missing);

        Assert.Equal(0, size.MainBytes);
        Assert.Equal(0, size.TotalBytes);
        // A database whose directory cannot even be opened reports a deferred maintenance instead of throwing, so a
        // cleanup pass never fails because of a housekeeping measurement.
        var unreachable = Path.Combine(databases.DirectoryPath, "missing-directory", "users.db");
        Assert.False(await SqliteDatabaseMaintenance.VacuumAndAnalyzeAsync(unreachable, CancellationToken.None));
    }

    /// <summary>Builds a cleanup configuration pointing at the fixture's databases.</summary>
    /// <param name="databases">Fixture that owns the temporary database directory.</param>
    /// <param name="overrides">Optional <c>DatabaseCleanup</c> key overrides applied on top of the defaults.</param>
    /// <returns>A configuration containing the fixture database paths and the requested retention settings.</returns>
    /// <remarks>
    /// The paths are supplied so the size metrics and the maintenance step read the fixture's files rather than a
    /// working-directory <c>./Data</c> database that belongs to a developer machine.
    /// </remarks>
    private static IConfiguration CleanupConfiguration(Databases databases, params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["userDatabasePath"] = Path.Combine(databases.DirectoryPath, "users.db"),
            ["credentialsDatabasePath"] = Path.Combine(databases.DirectoryPath, "credentials.db")
        };
        foreach (var (key, value) in overrides)
            values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Creates the retention runner against the fixture's users database.</summary>
    /// <param name="databases">Fixture that owns the temporary databases.</param>
    /// <param name="configuration">Validated or deliberately invalid cleanup configuration for the scenario.</param>
    /// <returns>A runner that logs nothing, so assertions read only the returned report.</returns>
    private static DatabaseCleanupRunner CreateCleanupRunner(Databases databases, IConfiguration configuration) =>
        new(databases.Users, databases.Inbox, configuration, NullLogger<DatabaseCleanupRunner>.Instance);

    /// <summary>Builds one account-creation reservation row for a retention scenario.</summary>
    /// <param name="operationKey">Stable operation key asserted after the pass.</param>
    /// <param name="outcome">Durable creation outcome that decides eligibility.</param>
    /// <param name="createdAtUtc">UTC reservation time used by both retention windows.</param>
    /// <param name="appliedAtUtc">UTC proven-creation time, or null when the outcome has no applied timestamp.</param>
    /// <returns>A detached creation-operation row that is never saved by the builder itself.</returns>
    private static XuiV3CreationOperation Creation(string operationKey, XuiV3CreationOutcome outcome, DateTime createdAtUtc, DateTime? appliedAtUtc) =>
        new()
        {
            OperationKey = operationKey,
            PanelKey = "panel-key",
            TelegramUserId = 123,
            ClientJson = "payload-" + operationKey,
            InboundIdsJson = "[1]",
            BusinessParametersJson = "{\"gb\":1}",
            Outcome = outcome,
            CreatedAtUtc = createdAtUtc,
            AppliedAtUtc = appliedAtUtc
        };

    /// <summary>Builds one renewal operation row for a compaction scenario.</summary>
    /// <param name="id">Explicit identifier so assertions can address rows deterministically.</param>
    /// <param name="status">Lifecycle status that decides compaction eligibility.</param>
    /// <param name="settlementStatus">Settlement status that decides compaction eligibility.</param>
    /// <param name="updatedAtUtc">UTC last-update time used by the payload window.</param>
    /// <returns>A detached renewal row carrying recognizable payload markers.</returns>
    private static XuiV3RenewalOperation Renewal(int id, string status, string settlementStatus, DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            OperationKey = "renew-" + id,
            OperationId = "renew-op-" + id,
            BotId = "owned",
            TelegramUserId = 123,
            TargetEmail = "client@example.test",
            NormalizedTargetEmail = "client@example.test",
            PriceToman = 42_000,
            MutationPayloadJson = "mutation-" + id,
            PreMutationSnapshotJson = "snapshot-" + id,
            Status = status,
            SettlementStatus = settlementStatus,
            CreatedAtUtc = updatedAtUtc.AddHours(-1),
            UpdatedAtUtc = updatedAtUtc
        };

    /// <summary>Builds one link-change saga row for a retention scenario.</summary>
    /// <param name="id">Explicit identifier so assertions can address rows deterministically.</param>
    /// <param name="status">Lifecycle status that decides retention eligibility.</param>
    /// <param name="completedAtUtc">UTC completion time used by both retention windows.</param>
    /// <returns>A detached link-change row that is never saved by the builder itself.</returns>
    private static XuiV3LinkChangeOperation LinkChange(int id, string status, DateTime completedAtUtc) =>
        new()
        {
            Id = id,
            OperationKey = "link-" + id,
            PanelKey = "panel-key",
            // The active-state filtered unique index treats (PanelKey, ClientId) as the lock key, so each seeded saga
            // must own a distinct client id, exactly as production does for different accounts.
            ClientId = 1000 + id,
            BotId = "owned",
            TelegramUserId = 123,
            Status = status,
            SnapshotJson = "snapshot-" + id,
            PayloadJson = "payload-" + id,
            CreatedAtUtc = completedAtUtc.AddHours(-1),
            UpdatedAtUtc = completedAtUtc,
            CompletedAtUtc = completedAtUtc,
            ConfirmationExpiresAtUtc = completedAtUtc.AddMinutes(5)
        };

    /// <summary>Builds one Telegram update receipt for an inbox retention scenario.</summary>
    /// <param name="updateId">Telegram update id; it must be unique per bot for the deduplication index.</param>
    /// <param name="status">Execution status that decides retention eligibility.</param>
    /// <param name="completedAtUtc">UTC terminal time used by the inbox window, or null while the update is unfinished.</param>
    /// <param name="acceptedAtUtc">UTC acceptance time.</param>
    /// <returns>A detached receipt row whose payload is already erased, mirroring production terminal rows.</returns>
    private static TelegramUpdateInboxEntry Receipt(int updateId, string status, DateTime completedAtUtc, DateTime? acceptedAtUtc) =>
        new()
        {
            BotId = "owned",
            UpdateId = updateId,
            TelegramUserId = 123,
            UpdateType = "Message",
            Payload = null,
            Status = status,
            AcceptedAtUtc = acceptedAtUtc ?? DateTime.UtcNow,
            CompletedAtUtc = completedAtUtc
        };

    /// <summary>Saves the supplied creation reservations into the fixture database.</summary>
    /// <param name="databases">Fixture that owns the temporary users database.</param>
    /// <param name="rows">Reservation rows to persist.</param>
    /// <returns>A task completing after the rows are committed.</returns>
    private static async Task SeedCreationOperationsAsync(Databases databases, params XuiV3CreationOperation[] rows)
    {
        await using var seed = databases.Users.CreateDbContext();
        seed.XuiV3CreationOperations.AddRange(rows);
        await seed.SaveChangesAsync();
    }

    /// <summary>Saves the supplied renewal operations into the fixture database.</summary>
    /// <param name="databases">Fixture that owns the temporary users database.</param>
    /// <param name="rows">Renewal rows to persist.</param>
    /// <returns>A task completing after the rows are committed.</returns>
    private static async Task SeedRenewalsAsync(Databases databases, params XuiV3RenewalOperation[] rows)
    {
        await using var seed = databases.Users.CreateDbContext();
        seed.XuiV3RenewalOperations.AddRange(rows);
        await seed.SaveChangesAsync();
    }

    /// <summary>Saves the supplied link-change sagas into the fixture database.</summary>
    /// <param name="databases">Fixture that owns the temporary users database.</param>
    /// <param name="rows">Link-change rows to persist.</param>
    /// <returns>A task completing after the rows are committed.</returns>
    private static async Task SeedLinkChangesAsync(Databases databases, params XuiV3LinkChangeOperation[] rows)
    {
        await using var seed = databases.Users.CreateDbContext();
        seed.XuiV3LinkChangeOperations.AddRange(rows);
        await seed.SaveChangesAsync();
    }
}
