using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Xunit;

/// <summary>Exercises actual administrative gate observations without changing durable routing or starting live receivers.</summary>
public sealed partial class TelegramEndpointCoordinatorTests
{
    /// <summary>Administrative reads distinguish the currently fenced admission gate from a still-active durable Cloud row without reopening the fence.</summary>
    /// <returns>A task completing after both inventory and detail preserve the same closed current-process route.</returns>
    [Fact]
    public async Task Administrative_snapshot_observes_fence_without_guessing_active_persisted_endpoint()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var active = await fixture.StatusAsync();
        Assert.Equal(TelegramEndpointType.Cloud, active.RuntimeEndpoint);
        Assert.True(active.RuntimeAvailable);
        Assert.Equal(active.Generation, active.RuntimeGeneration);
        fixture.Gate.Fence("owned-a", 123);
        var detail = await fixture.StatusAsync();
        var inventory = Assert.Single(await fixture.Coordinator.GetInventoryAsync(default), row => row.BotId == "owned-a");
        Assert.Equal(TelegramEndpointMigrationState.Cloud, detail.MigrationState);
        Assert.Equal(TelegramEndpointType.Cloud, detail.EffectiveEndpoint);
        Assert.False(detail.RuntimeAvailable);
        Assert.False(inventory.RuntimeAvailable);
        Assert.Equal(TelegramEndpointType.Cloud, inventory.RuntimeEndpoint);
        Assert.False(fixture.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>Verified Local activation and the official Cloud wait expose different running admission snapshots while retaining the last activated route.</summary>
    /// <returns>A task completing after destination validation, cooldown, process restart and eventual Cloud validation update the observed snapshot.</returns>
    [Fact]
    public async Task Administrative_snapshot_tracks_validated_activation_cloud_wait_and_restart()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        Assert.Equal("accepted", await fixture.RequestAsync(TelegramEndpointType.Local));
        var queued = await fixture.StatusAsync();
        Assert.False(queued.RuntimeAvailable);
        Assert.Equal(TelegramEndpointType.Cloud, queued.RuntimeEndpoint);
        await fixture.Coordinator.RunPendingOperationsAsync(default);
        var local = await fixture.StatusAsync();
        Assert.True(local.RuntimeAvailable);
        Assert.Equal(TelegramEndpointType.Local, local.RuntimeEndpoint);
        Assert.NotNull(local.LastMigrationAtUtc);
        Assert.Equal("accepted", await fixture.RequestAsync(TelegramEndpointType.Cloud));
        await fixture.Coordinator.RunPendingOperationsAsync(default);
        var waiting = await fixture.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.CloudWait, waiting.MigrationState);
        Assert.Equal(TelegramEndpointType.Local, waiting.EffectiveEndpoint);
        Assert.False(waiting.RuntimeAvailable);
        fixture.RestartCoordinator();
        await fixture.InitializeAsync();
        Assert.False((await fixture.StatusAsync()).RuntimeAvailable);
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await fixture.Coordinator.RunPendingOperationsAsync(default);
        var cloud = await fixture.StatusAsync();
        Assert.True(cloud.RuntimeAvailable);
        Assert.Equal(TelegramEndpointType.Cloud, cloud.RuntimeEndpoint);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, cloud.MigrationState);
    }

    /// <summary>Disabled valid identities are never called active, and tokenless inventory entries retain genuinely unavailable measurements rather than Cloud defaults.</summary>
    /// <returns>A task completing after registry changes are consumed by genuine inventory and detail reads.</returns>
    [Fact]
    public async Task Administrative_snapshot_disabled_and_missing_identity_are_not_active()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Registry.Upsert(new BotInstance
        {
            Id = "owned-a", Token = "123:abcdefghijklmnopqrstuvwxyz0123456789",
            Type = BotInstanceTypes.Owned, Enabled = false
        });
        var disabled = await fixture.StatusAsync();
        Assert.False(disabled.RuntimeAvailable);
        Assert.Equal(TelegramEndpointType.Cloud, disabled.RuntimeEndpoint);
        fixture.Registry.Upsert(new BotInstance { Id = "missing", Token = "", Type = BotInstanceTypes.Owned, Enabled = true });
        var missing = Assert.Single(await fixture.Coordinator.GetInventoryAsync(default), row => row.BotId == "missing");
        Assert.Equal(0, missing.TelegramBotId);
        Assert.Null(missing.RuntimeAvailable);
        Assert.Null(missing.RuntimeEndpoint);
        Assert.Null(missing.RuntimeGeneration);
    }
}
