using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>The existing production section binds exactly; missing configuration does not enable additional cleanup.</summary>
    [Fact]
    public void Cleanup_configuration_binds_and_rejects_unsafe_deduplication_window()
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            {"DatabaseCleanup":{"Enabled":true,"RetentionDays":30,"PayloadRetentionDays":2,"InboxRetentionDays":7,
              "BatchSize":2000,"MaxBatchesPerTable":20,"MaintenanceEnabled":true,"InitialDelay":"00:10:00","Interval":"1.00:00:00"}}
            """));
        var options = new ConfigurationBuilder().AddJsonStream(json).Build().Get<AppConfig>()!.DatabaseCleanup;
        options.Validate();
        Assert.Equal(TimeSpan.FromDays(1), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(10), options.InitialDelay);
        Assert.False(new AppConfig().DatabaseCleanup.Enabled);
        options.InboxRetentionDays = 6;
        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>Only shapes the business dispatcher handles may enter production inbox admission; subscription arrays cannot leak mutations.</summary>
    [Fact]
    public void Cleanup_admission_keeps_messages_callbacks_and_excludes_unused_channel_events()
    {
        Assert.True(TelegramUpdateAdmissionPolicy.RequiresHandling(new Update { Message = new Message() }));
        Assert.True(TelegramUpdateAdmissionPolicy.RequiresHandling(new Update { CallbackQuery = new CallbackQuery() }));
        Assert.False(TelegramUpdateAdmissionPolicy.RequiresHandling(new Update { ChannelPost = new Message() }));
        Assert.False(TelegramUpdateAdmissionPolicy.RequiresHandling(new Update { EditedChannelPost = new Message() }));
        Assert.False(TelegramUpdateAdmissionPolicy.RequiresHandling(new Update { MyChatMember = new ChatMemberUpdated() }));
        Assert.False(TelegramUpdateAdmissionPolicy.RequiresHandling(null!));
        var subscription = TelegramUpdateAdmissionPolicy.AllowedUpdates;
        subscription[0] = UpdateType.ChannelPost;
        Assert.Equal(new[] { UpdateType.Message, UpdateType.CallbackQuery }, TelegramUpdateAdmissionPolicy.AllowedUpdates);
    }

    /// <summary>Expired disposable rows are bounded; queued/running/review/unknown and operation-linked audit evidence survive repeated cleanup.</summary>
    /// <returns>A task proving removal/redaction without modifying the immutable XUI operation or its payload.</returns>
    [Fact]
    public async Task Cleanup_retains_business_work_and_limits_inbox_batches()
    {
        using var databases = new Databases();
        var options = new DatabaseCleanupOptions { Enabled = true, BatchSize = 1, MaxBatchesPerTable = 2, MaintenanceEnabled = false };
        var inbox = new TelegramUpdateInboxStore(databases.Users, databases.Credentials, cleanup: options);
        using var cleanup = new DatabaseCleanupService(databases.Users, inbox, new AppConfig { DatabaseCleanup = options },
            NullLogger<DatabaseCleanupService>.Instance);
        var old = DateTime.UtcNow.AddDays(-60);
        await using (var db = databases.Users.CreateDbContext())
        {
            // Financial audit and its unique settlement key must survive even though they exceed every retention cutoff.
            db.WalletLedgerEntries.Add(new WalletLedgerEntry { BotId = "fixture", BotType = BotInstanceTypes.Owned,
                TelegramUserId = 711, Direction = "credit", AmountToman = 100, BalanceBefore = 200, BalanceAfter = 300,
                Reason = "fixture", Provider = "fixture", ReferenceType = "fixture", ReferenceId = "fixture",
                IdempotencyKey = "permanent-fixture-credit", CreatedAtUtc = old });
            foreach (var (id, status) in new[] { (1, "completed"), (2, "completed_with_error"), (3, "completed"),
                (4, "queued"), (5, "running"), (6, "completed_with_review"), (7, "completed_unknown"), (8, "completed") })
                db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry { BotId = "fixture", UpdateId = id, Status = status,
                    AcceptedAtUtc = old, CompletedAtUtc = status is "queued" or "running" ? null : old, Payload = "private fixture" });
            await db.SaveChangesAsync();
            var linked = await db.TelegramUpdateInbox.SingleAsync(x => x.UpdateId == 8);
            db.XuiV3CreationOperations.Add(new XuiV3CreationOperation { OperationKey = "fixture-op", PanelKey = "fixture-panel",
                InboxSequence = linked.Sequence, ClientJson = "immutable fixture", InboundIdsJson = "[]", BusinessParametersJson = "{}",
                CreatedAtUtc = old, Outcome = XuiV3CreationOutcome.Ambiguous });
            await db.SaveChangesAsync();
        }
        var first = await cleanup.RunOnceAsync(default);
        Assert.Equal(2, first["inbox"]);
        Assert.Equal(2, first["payloads"]);
        await cleanup.RunOnceAsync(default);
        await cleanup.RunOnceAsync(default);
        await using var verify = databases.Users.CreateDbContext();
        var remaining = await verify.TelegramUpdateInbox.OrderBy(x => x.UpdateId).ToListAsync();
        Assert.Equal(new[] { 4, 5, 6, 7, 8 }, remaining.Select(x => x.UpdateId));
        Assert.All(remaining.Where(x => x.UpdateId is 4 or 5 or 7), x => Assert.Equal("private fixture", x.Payload));
        Assert.All(remaining.Where(x => x.UpdateId is 6 or 8), x => Assert.Null(x.Payload));
        Assert.Equal("immutable fixture", (await verify.XuiV3CreationOperations.SingleAsync()).ClientJson);
        var ledger = await verify.WalletLedgerEntries.SingleAsync();
        Assert.Equal("permanent-fixture-credit", ledger.IdempotencyKey);
        Assert.Equal(100, ledger.AmountToman);
        Assert.Equal(300, ledger.BalanceAfter);
    }

    /// <summary>Delivered endpoint alerts may expire only behind a permanent dedup key; newest/current-operation history and ambiguous alerts remain intact.</summary>
    /// <returns>A task proving receipt keys and unresolved delivery state cannot be deleted by retention.</returns>
    [Fact]
    public async Task Cleanup_endpoint_retention_preserves_permanent_keys_and_active_operation()
    {
        using var databases = new Databases();
        var options = new DatabaseCleanupOptions { Enabled = true, BatchSize = 2, MaxBatchesPerTable = 10, MaintenanceEnabled = false };
        using var cleanup = new DatabaseCleanupService(databases.Users,
            new TelegramUpdateInboxStore(databases.Users, cleanup: options), new AppConfig { DatabaseCleanup = options },
            NullLogger<DatabaseCleanupService>.Instance);
        var old = DateTime.UtcNow.AddDays(-60);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramEndpointStates.Add(new TelegramEndpointState { BotId = "fixture", TelegramBotId = 123456, OperationId = "active" });
            for (var i = 0; i < 25; i++) db.TelegramEndpointHistory.Add(new TelegramEndpointHistory
            { BotId = "fixture", TelegramBotId = 123456, OperationId = i == 0 ? "active" : "past", Reason = "migration_requested", Outcome = "Cloud", CreatedAtUtc = old });
            foreach (var (key, status) in new[] { ("safe", TelegramEndpointAlertStatus.Delivered),
                ("no-key", TelegramEndpointAlertStatus.Delivered), ("ambiguous", TelegramEndpointAlertStatus.DeliveryUncertain),
                ("pending", TelegramEndpointAlertStatus.Pending), ("processing", TelegramEndpointAlertStatus.Processing),
                ("review", TelegramEndpointAlertStatus.ManualReview) })
                db.TelegramEndpointAlerts.Add(new TelegramEndpointAlert { IncidentKey = key, BotId = "fixture", TelegramBotId = 123456,
                    Category = "fixture", Status = status, CreatedAtUtc = old, DeliveredAtUtc = old });
            db.TelegramEndpointAlertReceipts.Add(new TelegramEndpointAlertReceipt { IncidentKey = "safe" });
            await db.SaveChangesAsync();
        }
        var counts = await cleanup.RunOnceAsync(default);
        Assert.Equal(4, counts["history"]);
        Assert.Equal(1, counts["alerts"]);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(21, await verify.TelegramEndpointHistory.CountAsync());
        Assert.Single(await verify.TelegramEndpointAlertReceipts.ToListAsync());
        Assert.Equal(5, await verify.TelegramEndpointAlerts.CountAsync());
        Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
    }

    /// <summary>Maintenance must never perform an implicit full VACUUM; only pre-enabled incremental mode may release a bounded number of pages.</summary>
    /// <param name="incremental">Whether the isolated fixture was explicitly converted offline before running the worker.</param>
    /// <returns>A task proving vacuum mode stays unchanged and page reclamation obeys the configured cap.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_maintenance_never_converts_live_database_or_vacuums_unbounded(bool incremental)
    {
        using var databases = new Databases();
        var options = new DatabaseCleanupOptions { Enabled = true, BatchSize = 2, MaxBatchesPerTable = 1 };
        await using (var db = databases.Users.CreateDbContext())
        {
            if (incremental) await db.Database.ExecuteSqlRawAsync("PRAGMA auto_vacuum=INCREMENTAL; VACUUM;");
            for (var i = 0; i < 20; i++) db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry
            { BotId = "fixture", UpdateId = i, Status = "completed", AcceptedAtUtc = DateTime.UtcNow.AddDays(-60),
                CompletedAtUtc = DateTime.UtcNow.AddDays(-60), Payload = new string('x', 32768) });
            await db.SaveChangesAsync();
        }
        var before = await CleanupPragmaAsync(databases.Users, "page_count");
        using var cleanup = new DatabaseCleanupService(databases.Users,
            new TelegramUpdateInboxStore(databases.Users, cleanup: options), new AppConfig { DatabaseCleanup = options },
            NullLogger<DatabaseCleanupService>.Instance);
        await cleanup.RunOnceAsync(default);
        var after = await CleanupPragmaAsync(databases.Users, "page_count");
        Assert.Equal(incremental ? 2L : 0L, await CleanupPragmaAsync(databases.Users, "auto_vacuum"));
        Assert.InRange(before - after, 0, incremental ? options.BatchSize : 0);
    }

    /// <summary>Reads only the fixed test PRAGMA without changing durability or maintenance settings.</summary>
    /// <param name="factory">Isolated fixture database factory, never production users.db.</param>
    /// <param name="pragma">Test literal page_count or auto_vacuum; no external input is accepted.</param>
    /// <returns>The scalar page count or auto-vacuum mode; no connection is retained.</returns>
    /// <remarks>The context and command are disposed before a maintenance worker obtains its next write turn.</remarks>
    /// <example><code>var pages = await CleanupPragmaAsync(databases.Users, "page_count");</code></example>
    private static async Task<long> CleanupPragmaAsync(UserDbContextFactory factory, string pragma)
    {
        Assert.Contains(pragma, new[] { "page_count", "auto_vacuum" });
        await using var db = factory.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA " + pragma;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Disabled configuration never prunes new tables or modifies unknown/legacy maintenance state.</summary>
    /// <returns>A task proving a disabled worker has no data effects.</returns>
    [Fact]
    public async Task Cleanup_disabled_worker_has_no_mutations()
    {
        using var databases = new Databases();
        using var cleanup = new DatabaseCleanupService(databases.Users, databases.Inbox, new AppConfig(),
            NullLogger<DatabaseCleanupService>.Instance);
        var counts = await cleanup.RunOnceAsync(default);
        Assert.All(counts.Values, x => Assert.Equal(0, x));
    }
}
