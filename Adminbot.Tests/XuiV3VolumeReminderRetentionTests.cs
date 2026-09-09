using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed partial class ConcurrencyTests
{
    private const string ReminderPanelKey = "panel-retention-test";

    [Fact]
    public async Task Volume_reminder_prune_deletes_only_stale_missing_safe_rows()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var store = CreateReminderStore(databases, 30);
        await SeedReminderStatesAsync(databases,
            ReminderState(1, now.AddDays(-31)),
            ReminderState(2, now.AddDays(-31)),
            ReminderState(3, now.AddDays(-29)),
            ReminderState(4, now.AddDays(-31), XuiV3VolumeReminderDeliveryStatuses.Processing, 90, now.AddMinutes(10)),
            ReminderState(5, now.AddDays(-31), XuiV3VolumeReminderDeliveryStatuses.Failed, 90),
            ReminderState(6, now.AddDays(-31), XuiV3VolumeReminderDeliveryStatuses.Idle, null, now.AddMinutes(10)),
            ReminderState(7, now.AddDays(-31), panelKey: "other-panel"));

        var deleted = await store.PruneMissingClientsAsync(ReminderPanelKey, new HashSet<int> { 2 }, now, CancellationToken.None);
        Assert.Equal(1, deleted);
        await using var verify = databases.Users.CreateDbContext();
        var remaining = await verify.XuiV3VolumeReminderStates
            .OrderBy(state => state.ClientId)
            .Select(state => new { state.PanelKey, state.ClientId })
            .ToListAsync();
        Assert.DoesNotContain(remaining, row => row.PanelKey == ReminderPanelKey && row.ClientId == 1);
        Assert.Contains(remaining, row => row.PanelKey == ReminderPanelKey && row.ClientId == 2);
        Assert.Contains(remaining, row => row.PanelKey == ReminderPanelKey && row.ClientId == 3);
        Assert.Contains(remaining, row => row.PanelKey == ReminderPanelKey && row.ClientId == 4);
        Assert.Contains(remaining, row => row.PanelKey == ReminderPanelKey && row.ClientId == 5);
        Assert.Contains(remaining, row => row.PanelKey == ReminderPanelKey && row.ClientId == 6);
        Assert.Contains(remaining, row => row.PanelKey == "other-panel" && row.ClientId == 7);
    }

    [Fact]
    public void Complete_list_presence_requires_success_and_non_null_payload()
    {
        Assert.False(XuiV3VolumeExpirationReminderService.TryCaptureCompletePanelClientIds(
            new XuiV3ApiResponse<List<XuiV3Client>> { Success = false, Obj = new List<XuiV3Client> { new() { Id = 1 } } },
            out var failedIds));
        Assert.Empty(failedIds);

        Assert.False(XuiV3VolumeExpirationReminderService.TryCaptureCompletePanelClientIds(
            new XuiV3ApiResponse<List<XuiV3Client>> { Success = true, Obj = null! },
            out var nullIds));
        Assert.Empty(nullIds);
        Assert.True(XuiV3VolumeExpirationReminderService.TryCaptureCompletePanelClientIds(
            new XuiV3ApiResponse<List<XuiV3Client>> { Success = true, Obj = new List<XuiV3Client>() },
            out var emptyIds));
        Assert.Empty(emptyIds);
    }

    [Fact]
    public async Task Raw_complete_list_presence_retains_reminder_ineligible_client()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var store = CreateReminderStore(databases, 30);
        await SeedReminderStatesAsync(databases, ReminderState(10, now.AddDays(-45)));
        var response = new XuiV3ApiResponse<List<XuiV3Client>>
        {
            Success = true,
            // Id is present, while default quota/owner fields make this row reminder-ineligible later in RunScanAsync.
            Obj = new List<XuiV3Client> { new() { Id = 10 } }
        };

        Assert.True(XuiV3VolumeExpirationReminderService.TryCaptureCompletePanelClientIds(response, out var currentIds));
        Assert.Contains(10, currentIds);
        Assert.Equal(0, await store.PruneMissingClientsAsync(ReminderPanelKey, currentIds, now, CancellationToken.None));

        await using var verify = databases.Users.CreateDbContext();
        Assert.True(await verify.XuiV3VolumeReminderStates.AnyAsync(state => state.ClientId == 10));
    }
    [Fact]
    public async Task Successful_empty_complete_list_prunes_only_expired_safe_rows()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var store = CreateReminderStore(databases, 30);
        await SeedReminderStatesAsync(databases,
            ReminderState(20, now.AddDays(-40)),
            ReminderState(21, now.AddDays(-20)));

        var response = new XuiV3ApiResponse<List<XuiV3Client>> { Success = true, Obj = new List<XuiV3Client>() };
        Assert.True(XuiV3VolumeExpirationReminderService.TryCaptureCompletePanelClientIds(response, out var currentIds));
        Assert.Equal(1, await store.PruneMissingClientsAsync(ReminderPanelKey, currentIds, now, CancellationToken.None));

        await using var verify = databases.Users.CreateDbContext();
        Assert.False(await verify.XuiV3VolumeReminderStates.AnyAsync(state => state.ClientId == 20));
        Assert.True(await verify.XuiV3VolumeReminderStates.AnyAsync(state => state.ClientId == 21));
    }

    [Fact]
    public async Task Volume_reminder_prune_is_bounded_to_one_hundred_rows_per_call()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var store = CreateReminderStore(databases, 30);
        await SeedReminderStatesAsync(databases,
            Enumerable.Range(1000, 125).Select(id => ReminderState(id, now.AddDays(-60))).ToArray());
        Assert.Equal(XuiV3VolumeReminderStateStore.MaxPruneBatchSize,
            await store.PruneMissingClientsAsync(ReminderPanelKey, new HashSet<int>(), now, CancellationToken.None));

        await using (var verifyFirst = databases.Users.CreateDbContext())
            Assert.Equal(25, await verifyFirst.XuiV3VolumeReminderStates.CountAsync());

        Assert.Equal(25,
            await store.PruneMissingClientsAsync(ReminderPanelKey, new HashSet<int>(), now, CancellationToken.None));

        await using var verifySecond = databases.Users.CreateDbContext();
        Assert.Equal(0, await verifySecond.XuiV3VolumeReminderStates.CountAsync());
    }

    [Fact]
    public async Task Client_returning_before_retention_keeps_existing_cycle_state()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var store = CreateReminderStore(databases, 30);
        var state = ReminderState(300, now.AddDays(-29), cycleNumber: 7);
        state.ClientCreatedAt = 123;
        state.Email = "client-300@example.test";
        state.BotId = "owned";
        state.TelegramUserId = 3000;
        state.PanelUpdatedAt = 456;
        state.TotalBytes = 1_000;
        state.UsedBytes = 500;
        await SeedReminderStatesAsync(databases, state);
        Assert.Equal(0,
            await store.PruneMissingClientsAsync(ReminderPanelKey, new HashSet<int>(), now, CancellationToken.None));

        var candidates = await store.ReconcileAsync(ReminderPanelKey,
            new[]
            {
                new XuiV3VolumeReminderObservation
                {
                    ClientId = 300,
                    ClientCreatedAt = 123,
                    Email = "client-300@example.test",
                    BotId = "owned",
                    TelegramUserId = 3000,
                    PanelUpdatedAt = 456,
                    TotalBytes = 1_000,
                    UsedBytes = 500,
                    HighestReachedThreshold = 0,
                    IsEligible = false
                }
            },
            now,
            CancellationToken.None);
        Assert.Empty(candidates);

        await using var verify = databases.Users.CreateDbContext();
        var persisted = await verify.XuiV3VolumeReminderStates.SingleAsync(state => state.ClientId == 300);
        Assert.Equal(7, persisted.CycleNumber);
        Assert.Equal(now, persisted.LastObservedAtUtc);
    }
    [Fact]
    public async Task Volume_reminder_prune_rejects_missing_presence_evidence_and_invalid_retention()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        await SeedReminderStatesAsync(databases, ReminderState(400, now.AddDays(-60)));

        var validStore = CreateReminderStore(databases, 30);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            validStore.PruneMissingClientsAsync(ReminderPanelKey, null!, now, CancellationToken.None));

        var invalidStore = CreateReminderStore(databases, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invalidStore.PruneMissingClientsAsync(ReminderPanelKey, new HashSet<int>(), now, CancellationToken.None));

        await using var verify = databases.Users.CreateDbContext();
        Assert.True(await verify.XuiV3VolumeReminderStates.AnyAsync(state => state.ClientId == 400));
    }

    [Fact]
    public async Task Volume_reminder_prune_does_not_modify_wallet_ledger()
    {
        using var databases = new Databases();
        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        await SeedReminderStatesAsync(databases, ReminderState(500, now.AddDays(-60)));

        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.WalletLedgerEntries.Add(new WalletLedgerEntry
            {
                BotId = "owned",
                BotUsername = "owned_bot",
                BotType = "owned",
                TelegramUserId = 9001,
                Direction = "credit",
                AmountToman = 100,
                BalanceBefore = 0,
                BalanceAfter = 100,
                Reason = "test",
                Provider = "test",
                ReferenceType = "retention-test",
                ReferenceId = "ledger-1",
                IdempotencyKey = "retention-ledger-1",
                CreatedAtUtc = now
            });
            await seed.SaveChangesAsync();
        }

        var store = CreateReminderStore(databases, 30);
        Assert.Equal(1,
            await store.PruneMissingClientsAsync(ReminderPanelKey, new HashSet<int>(), now, CancellationToken.None));

        await using var verify = databases.Users.CreateDbContext();
        var ledger = await verify.WalletLedgerEntries.SingleAsync();
        Assert.Equal(100, ledger.AmountToman);
        Assert.Equal("retention-ledger-1", ledger.IdempotencyKey);
    }
    private static XuiV3VolumeReminderStateStore CreateReminderStore(Databases databases, int retentionDays)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["xuiV3VolumeReminderStateRetentionDays"] = retentionDays.ToString()
            })
            .Build();
        return new XuiV3VolumeReminderStateStore(
            databases.Users,
            configuration,
            NullLogger<XuiV3VolumeReminderStateStore>.Instance);
    }

    private static async Task SeedReminderStatesAsync(
        Databases databases,
        params XuiV3VolumeReminderState[] states)
    {
        await using var context = databases.Users.CreateDbContext();
        context.XuiV3VolumeReminderStates.AddRange(states);
        await context.SaveChangesAsync();
    }

    private static XuiV3VolumeReminderState ReminderState(
        int clientId,
        DateTime lastObservedAtUtc,
        string deliveryStatus = XuiV3VolumeReminderDeliveryStatuses.Idle,
        int? claimedThreshold = null,
        DateTime? leaseUntilUtc = null,
        string panelKey = ReminderPanelKey,
        long cycleNumber = 1)
    {
        return new XuiV3VolumeReminderState
        {
            PanelKey = panelKey,
            ClientId = clientId,
            ClientCreatedAt = clientId * 1000L,
            Email = $"client-{clientId}@example.test",
            BotId = "owned",
            TelegramUserId = 10_000L + clientId,
            CycleNumber = cycleNumber,
            PanelUpdatedAt = clientId * 1000L,
            TotalBytes = 10_000,
            UsedBytes = 1_000,
            HighestHandledThreshold = 0,
            ClaimedThreshold = claimedThreshold,
            DeliveryStatus = deliveryStatus,
            LeaseUntilUtc = leaseUntilUtc,
            CreatedAtUtc = lastObservedAtUtc,
            UpdatedAtUtc = lastObservedAtUtc,
            LastObservedAtUtc = lastObservedAtUtc
        };
    }
}
