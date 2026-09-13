using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Adminbot.Domain.TelegramUi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Telegram.Bot.Exceptions;
using Xunit;

/// <summary>
/// Regression coverage for premium visual mode resolution, the runtime capability circuit, registry mapping and the
/// premium-with-fallback executor.
/// </summary>
/// <remarks>
/// <para>
/// These tests pin the separation between "requested" and "capable": an owned bot may request premium visuals from the
/// global switch and a storefront from its persisted preference, but a recorded definitive rejection must always fall
/// back to the classic visual instead of breaking navigation or delivery.
/// </para>
/// <para>
/// The durable storefront auto-disable is exercised against a real temporary SQLite users.db so the runtime registry
/// refresh and the persisted flag are checked together, not mocked apart.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>In-memory capability circuit used to isolate mode resolution and executor behaviour.</summary>
    private sealed class RecordingRuntimeState : ITelegramPremiumUiRuntimeState
    {
        private readonly Dictionary<string, (TelegramPremiumUiCapabilityState State, string? Reason)> _states =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>BotIds recorded as capable.</summary>
        public List<string> Available { get; } = new();

        /// <summary>BotIds recorded as definitively rejected.</summary>
        public List<string> Rejected { get; } = new();

        /// <summary>Tenant auto-disable requests, in order.</summary>
        public List<(string BotId, string Reason)> TenantDisables { get; } = new();

        /// <summary>Value returned by the tenant auto-disable helper.</summary>
        public bool TenantDisableResult { get; set; } = true;

        /// <summary>Pre-seeds a capability state.</summary>
        /// <param name="botId">Internal BotId.</param>
        /// <param name="state">State to record.</param>
        public void Seed(string botId, TelegramPremiumUiCapabilityState state) => _states[botId] = (state, null);

        /// <inheritdoc />
        public TelegramPremiumUiCapabilityState GetState(string botId)
            => !string.IsNullOrEmpty(botId) && _states.TryGetValue(botId, out var entry) ? entry.State : TelegramPremiumUiCapabilityState.Unknown;

        /// <inheritdoc />
        public bool IsRejected(string botId) => GetState(botId) == TelegramPremiumUiCapabilityState.Rejected;

        /// <inheritdoc />
        public void MarkAvailable(string botId)
        {
            Available.Add(botId);
            _states[botId] = (TelegramPremiumUiCapabilityState.Available, null);
        }

        /// <inheritdoc />
        public void MarkRejected(string botId, string reasonCode)
        {
            Rejected.Add(botId);
            _states[botId] = (TelegramPremiumUiCapabilityState.Rejected, reasonCode);
        }

        /// <inheritdoc />
        public void Clear(string botId) => _states.Remove(botId);

        /// <inheritdoc />
        public string? GetReasonCode(string botId)
            => !string.IsNullOrEmpty(botId) && _states.TryGetValue(botId, out var entry) ? entry.Reason : null;

        /// <inheritdoc />
        public Task<bool> MarkTenantCapabilityRejectedAsync(string botId, string reasonCode, CancellationToken cancellationToken)
        {
            TenantDisables.Add((botId, reasonCode));
            MarkRejected(botId, reasonCode);
            return Task.FromResult(TenantDisableResult);
        }
    }

    /// <summary>Builds a registry whose only owned bot is the requested BotId.</summary>
    /// <param name="ownedPremiumUi">Global owned-bot premium switch.</param>
    /// <returns>The registry plus the resolver under test.</returns>
    private static (BotRegistry Registry, TelegramUiModeResolver Resolver, RecordingRuntimeState State) OwnedMode(bool ownedPremiumUi)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ownedBotPremiumUiEnabled"] = ownedPremiumUi.ToString(),
            ["bots:0:id"] = "owned-one",
            ["bots:0:username"] = "owned_one_bot",
            ["bots:0:type"] = BotInstanceTypes.Owned,
            ["bots:0:enabled"] = "true",
            ["bots:0:isDefault"] = "true"
        }).Build();
        var registry = new BotRegistry(configuration);
        var state = new RecordingRuntimeState();
        return (registry, new TelegramUiModeResolver(configuration.Get<AppConfig>()!, registry, state), state);
    }

    /// <summary>A disabled global switch keeps owned bots in the classic mode.</summary>
    [Fact]
    public void Owned_bot_premium_mode_follows_the_global_switch()
    {
        var (_, resolver, _) = OwnedMode(ownedPremiumUi: false);

        Assert.False(resolver.IsPremiumRequested("owned-one"));
        Assert.False(resolver.IsPremiumVisualsActive("owned-one"));
    }

    /// <summary>An enabled global switch requests premium visuals before anything is proven.</summary>
    [Fact]
    public void Owned_bot_premium_mode_is_requested_when_enabled()
    {
        var (_, resolver, _) = OwnedMode(ownedPremiumUi: true);

        Assert.True(resolver.IsPremiumRequested("owned-one"));
        Assert.True(resolver.IsPremiumVisualsActive("owned-one"));
    }

    /// <summary>A definitive rejection stops premium delivery without changing the requested preference.</summary>
    /// <remarks>
    /// This is the owned-bot circuit breaker: configuration on disk is never rewritten, so a restart allows the owner to
    /// retry after fixing Telegram Premium or the catalog.
    /// </remarks>
    [Fact]
    public void Owned_bot_definitive_rejection_falls_back_until_restart()
    {
        var (registry, resolver, state) = OwnedMode(ownedPremiumUi: true);
        state.Seed("owned-one", TelegramPremiumUiCapabilityState.Rejected);

        Assert.True(resolver.IsPremiumRequested("owned-one"));
        Assert.False(resolver.IsPremiumVisualsActive("owned-one"));
        // The configured switch itself is untouched: the breaker lives only in the ephemeral runtime state.
        Assert.True(registry.GetById("owned-one") != null);
    }

    /// <summary>A storefront requests premium visuals from its own persisted preference.</summary>
    /// <param name="enabled">Persisted storefront preference.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tenant_premium_mode_follows_the_persisted_preference(bool enabled)
    {
        var (registry, resolver, _) = OwnedMode(ownedPremiumUi: false);
        registry.Upsert(new BotInstance { Id = "tenant-711-1", Type = BotInstanceTypes.Tenant, Enabled = true, TenantPremiumUiEnabled = enabled });

        Assert.Equal(enabled, resolver.IsPremiumRequested("tenant-711-1"));
        Assert.Equal(enabled, resolver.IsPremiumVisualsActive("tenant-711-1"));
    }

    /// <summary>A storefront is unaffected by the global owned-bot switch.</summary>
    [Fact]
    public void Owned_switch_never_leaks_into_a_storefront()
    {
        var (registry, resolver, _) = OwnedMode(ownedPremiumUi: true);
        registry.Upsert(new BotInstance { Id = "tenant-711-1", Type = BotInstanceTypes.Tenant, Enabled = true, TenantPremiumUiEnabled = false });

        Assert.False(resolver.IsPremiumRequested("tenant-711-1"));
    }

    /// <summary>The sales assistant bot is classic-only in this phase.</summary>
    [Fact]
    public void Sales_assistant_is_never_premium()
    {
        var (registry, resolver, _) = OwnedMode(ownedPremiumUi: true);
        registry.Upsert(new BotInstance { Id = "assistant-one", Type = BotInstanceTypes.SalesAssistant, Enabled = true });

        Assert.False(resolver.IsPremiumRequested("assistant-one"));
        Assert.False(resolver.IsPremiumVisualsActive("assistant-one"));
    }

    /// <summary>An unknown BotId never inherits the default bot's premium preference.</summary>
    /// <remarks>
    /// The registry intentionally falls back to the default bot for unknown ids, so this guard is what prevents an
    /// unrelated bot from silently rendering premium visuals.
    /// </remarks>
    [Fact]
    public void Unknown_bot_id_does_not_inherit_the_default_bot_preference()
    {
        var (_, resolver, _) = OwnedMode(ownedPremiumUi: true);

        Assert.False(resolver.IsPremiumRequested("tenant-unknown-9"));
        Assert.False(resolver.IsPremiumVisualsActive("tenant-unknown-9"));
    }

    /// <summary>A null configuration never requests premium visuals.</summary>
    [Fact]
    public void Null_bot_configuration_is_never_premium()
    {
        var (_, resolver, _) = OwnedMode(ownedPremiumUi: true);

        Assert.False(resolver.IsPremiumRequested((BotInstanceConfig?)null));
    }

    /// <summary>The storefront premium preference survives runtime registry hydration.</summary>
    /// <remarks>
    /// This is the mapping regression that protects against a restart, a receiver rebuild, or another tenant setting
    /// change silently clearing a storefront's opt-in.
    /// </remarks>
    [Fact]
    public async Task Tenant_premium_preference_survives_registry_hydration()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance
            {
                Id = "tenant-711-1",
                Type = BotInstanceTypes.Tenant,
                Enabled = true,
                OwnerTelegramUserId = 711,
                TenantStoreNumber = 1,
                TenantPremiumUiEnabled = true
            });
            await db.SaveChangesAsync();
        }

        var registry = new BotRegistry(new ConfigurationBuilder().Build());
        await using (var db = databases.Users.CreateDbContext())
            await registry.LoadTenantBotsFromDatabaseAsync(db);

        Assert.True(registry.GetById("tenant-711-1").TenantPremiumUiEnabled);
    }

    /// <summary>The runtime circuit records only definitive outcomes and exposes a bounded reason code.</summary>
    [Fact]
    public void Runtime_circuit_records_only_definitive_outcomes()
    {
        using var databases = new Databases();
        var registry = new BotRegistry(new ConfigurationBuilder().Build());
        var state = new TelegramPremiumUiRuntimeState(databases.Users, registry, new BotClientProvider(registry));

        Assert.Equal(TelegramPremiumUiCapabilityState.Unknown, state.GetState("owned-one"));
        Assert.False(state.IsRejected("owned-one"));
        Assert.Null(state.GetReasonCode("owned-one"));

        state.MarkAvailable("owned-one");
        Assert.Equal(TelegramPremiumUiCapabilityState.Available, state.GetState("owned-one"));
        Assert.False(state.IsRejected("owned-one"));

        state.MarkRejected("owned-one", "decorated_rejected");
        Assert.True(state.IsRejected("owned-one"));
        Assert.Equal("decorated_rejected", state.GetReasonCode("owned-one"));

        state.Clear("owned-one");
        Assert.Equal(TelegramPremiumUiCapabilityState.Unknown, state.GetState("owned-one"));
    }

    /// <summary>A definitive storefront rejection durably clears the preference and refreshes the registry.</summary>
    [Fact]
    public async Task Tenant_rejection_clears_the_persisted_preference_and_refreshes_the_registry()
    {
        using var databases = new Databases();
        var registry = new BotRegistry(new ConfigurationBuilder().Build());
        var state = new TelegramPremiumUiRuntimeState(databases.Users, registry, new BotClientProvider(registry));
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(new BotInstance
            {
                Id = "tenant-711-1",
                Type = BotInstanceTypes.Tenant,
                Enabled = true,
                OwnerTelegramUserId = 711,
                TenantStoreNumber = 1,
                TenantPremiumUiEnabled = true
            });
            await db.SaveChangesAsync();
        }
        await using (var db = databases.Users.CreateDbContext())
            await registry.LoadTenantBotsFromDatabaseAsync(db);
        Assert.True(registry.GetById("tenant-711-1").TenantPremiumUiEnabled);

        var changed = await state.MarkTenantCapabilityRejectedAsync("tenant-711-1", "decorated_rejected", CancellationToken.None);

        Assert.True(changed);
        Assert.True(state.IsRejected("tenant-711-1"));
        Assert.False(registry.GetById("tenant-711-1").TenantPremiumUiEnabled);
        await using (var db = databases.Users.CreateDbContext())
            Assert.False((await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-711-1")).TenantPremiumUiEnabled);
    }

    /// <summary>Repeating the auto-disable is idempotent and never touches an owned bot or another storefront.</summary>
    [Fact]
    public async Task Tenant_rejection_is_idempotent_and_storefront_scoped()
    {
        using var databases = new Databases();
        var registry = new BotRegistry(new ConfigurationBuilder().Build());
        var state = new TelegramPremiumUiRuntimeState(databases.Users, registry, new BotClientProvider(registry));
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.AddRange(
                new BotInstance { Id = "tenant-711-1", Type = BotInstanceTypes.Tenant, Enabled = true, OwnerTelegramUserId = 711, TenantStoreNumber = 1, TenantPremiumUiEnabled = true },
                new BotInstance { Id = "tenant-711-2", Type = BotInstanceTypes.Tenant, Enabled = true, OwnerTelegramUserId = 711, TenantStoreNumber = 2, TenantPremiumUiEnabled = true },
                new BotInstance { Id = "owned-one", Type = BotInstanceTypes.Owned, Enabled = true, IsDefault = true });
            await db.SaveChangesAsync();
        }

        Assert.True(await state.MarkTenantCapabilityRejectedAsync("tenant-711-1", "decorated_rejected", CancellationToken.None));
        Assert.False(await state.MarkTenantCapabilityRejectedAsync("tenant-711-1", "decorated_rejected", CancellationToken.None));
        Assert.False(await state.MarkTenantCapabilityRejectedAsync("owned-one", "decorated_rejected", CancellationToken.None));
        Assert.False(await state.MarkTenantCapabilityRejectedAsync("missing-store", "decorated_rejected", CancellationToken.None));

        await using var check = databases.Users.CreateDbContext();
        Assert.False((await check.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-711-1")).TenantPremiumUiEnabled);
        // The sibling storefront of the same owner keeps its own independent preference.
        Assert.True((await check.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-711-2")).TenantPremiumUiEnabled);
    }

    /// <summary>Classic mode never invokes the premium factory.</summary>
    [Fact]
    public async Task Classic_mode_never_invokes_the_premium_factory()
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: false);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);
        var premiumCalls = 0;
        var classicCalls = 0;

        var outcome = await executor.ExecuteAsync(
            _ => { premiumCalls++; return Task.CompletedTask; },
            _ => { classicCalls++; return Task.CompletedTask; },
            "owned-one");

        Assert.Equal(TelegramPremiumUiExecutionOutcome.Classic, outcome);
        Assert.Equal(0, premiumCalls);
        Assert.Equal(1, classicCalls);
    }

    /// <summary>A successful decorated send records the capability and never sends the classic copy.</summary>
    [Fact]
    public async Task Premium_success_records_capability_without_a_classic_copy()
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: true);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);
        var classicCalls = 0;

        var outcome = await executor.ExecuteAsync(
            _ => Task.CompletedTask,
            _ => { classicCalls++; return Task.CompletedTask; },
            "owned-one");

        Assert.Equal(TelegramPremiumUiExecutionOutcome.Premium, outcome);
        Assert.Equal(0, classicCalls);
        Assert.Contains("owned-one", state.Available);
    }

    /// <summary>A definitive rejection sends the classic copy exactly once and disables the storefront.</summary>
    [Fact]
    public async Task Definitive_rejection_sends_one_classic_copy_and_disables_the_storefront()
    {
        var (registry, resolver, state) = OwnedMode(ownedPremiumUi: false);
        registry.Upsert(new BotInstance { Id = "tenant-711-1", Type = BotInstanceTypes.Tenant, Enabled = true, TenantPremiumUiEnabled = true });
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);
        var premiumCalls = 0;
        var classicCalls = 0;

        var outcome = await executor.ExecuteAsync(
            _ => { premiumCalls++; throw new ApiRequestException("bad request", 400); },
            _ => { classicCalls++; return Task.CompletedTask; },
            "tenant-711-1",
            "tenant-711-1");

        Assert.Equal(TelegramPremiumUiExecutionOutcome.PremiumFallback, outcome);
        Assert.Equal(1, premiumCalls);
        Assert.Equal(1, classicCalls);
        Assert.Contains("tenant-711-1", state.Rejected);
        Assert.Contains(("tenant-711-1", "decorated_rejected"), state.TenantDisables);
    }

    /// <summary>An owned bot never asks for a durable storefront disable.</summary>
    [Fact]
    public async Task Definitive_rejection_without_a_tenant_id_skips_the_durable_disable()
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: true);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);

        var outcome = await executor.ExecuteAsync(
            _ => throw new ApiRequestException("bad request", 400),
            _ => Task.CompletedTask,
            "owned-one");

        Assert.Equal(TelegramPremiumUiExecutionOutcome.PremiumFallback, outcome);
        Assert.Empty(state.TenantDisables);
    }

    /// <summary>Ambiguous outcomes never send a second copy and never record a rejection.</summary>
    /// <param name="kind">Which ambiguous failure to simulate.</param>
    [Theory]
    [InlineData("timeout")]
    [InlineData("http")]
    [InlineData("429")]
    [InlineData("503")]
    public async Task Ambiguous_outcomes_never_send_a_classic_copy(string kind)
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: true);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);
        var classicCalls = 0;

        var outcome = await executor.ExecuteAsync(
            _ => kind switch
            {
                "timeout" => throw new TelegramForegroundDeliveryTimeoutException("send_message", TimeSpan.FromSeconds(8)),
                "http" => throw new HttpRequestException("reset"),
                "429" => throw new ApiRequestException("slow down", 429),
                _ => throw new ApiRequestException("unavailable", 503)
            },
            _ => { classicCalls++; return Task.CompletedTask; },
            "owned-one");

        Assert.Equal(TelegramPremiumUiExecutionOutcome.Ambiguous, outcome);
        Assert.Equal(0, classicCalls);
        Assert.Empty(state.Rejected);
    }

    /// <summary>A failing classic copy propagates so the caller keeps its own failure handling.</summary>
    [Fact]
    public async Task Classic_copy_failures_propagate()
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: false);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            _ => Task.CompletedTask,
            _ => throw new InvalidOperationException("classic failed"),
            "owned-one"));
    }

    /// <summary>Caller cancellation propagates and records no capability state.</summary>
    [Fact]
    public async Task Caller_cancellation_propagates_from_the_executor()
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: true);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            _ => throw new OperationCanceledException(),
            _ => Task.CompletedTask,
            "owned-one",
            null,
            cancellation.Token));

        Assert.Empty(state.Available);
        Assert.Empty(state.Rejected);
    }

    /// <summary>A null factory is a programming error rather than a silent no-op.</summary>
    [Fact]
    public async Task Null_factories_are_rejected()
    {
        var (_, resolver, state) = OwnedMode(ownedPremiumUi: true);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, state);

        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(null!, _ => Task.CompletedTask, "owned-one"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(_ => Task.CompletedTask, null!, "owned-one"));
    }

    /// <summary>A failing durable disable never blocks the classic copy.</summary>
    [Fact]
    public async Task Durable_disable_failures_never_block_delivery()
    {
        var (registry, resolver, state) = OwnedMode(ownedPremiumUi: false);
        registry.Upsert(new BotInstance { Id = "tenant-711-1", Type = BotInstanceTypes.Tenant, Enabled = true, TenantPremiumUiEnabled = true });
        var failing = new ThrowingRuntimeState(state);
        var executor = new TelegramPremiumUiFallbackExecutor(resolver, failing);
        var classicCalls = 0;

        var outcome = await executor.ExecuteAsync(
            _ => throw new ApiRequestException("bad request", 400),
            _ => { classicCalls++; return Task.CompletedTask; },
            "tenant-711-1",
            "tenant-711-1");

        Assert.Equal(TelegramPremiumUiExecutionOutcome.PremiumFallback, outcome);
        Assert.Equal(1, classicCalls);
    }

    /// <summary>Runtime circuit whose durable disable path always fails.</summary>
    private sealed class ThrowingRuntimeState : ITelegramPremiumUiRuntimeState
    {
        private readonly RecordingRuntimeState _inner;

        /// <summary>Creates a decorating circuit that throws on the durable disable call.</summary>
        /// <param name="inner">Circuit used for the remaining members.</param>
        public ThrowingRuntimeState(RecordingRuntimeState inner) => _inner = inner;

        /// <inheritdoc />
        public TelegramPremiumUiCapabilityState GetState(string botId) => _inner.GetState(botId);
        /// <inheritdoc />
        public bool IsRejected(string botId) => _inner.IsRejected(botId);
        /// <inheritdoc />
        public void MarkAvailable(string botId) => _inner.MarkAvailable(botId);
        /// <inheritdoc />
        public void MarkRejected(string botId, string reasonCode) => _inner.MarkRejected(botId, reasonCode);
        /// <inheritdoc />
        public void Clear(string botId) => _inner.Clear(botId);
        /// <inheritdoc />
        public string? GetReasonCode(string botId) => _inner.GetReasonCode(botId);

        /// <inheritdoc />
        public Task<bool> MarkTenantCapabilityRejectedAsync(string botId, string reasonCode, CancellationToken cancellationToken)
            => throw new InvalidOperationException("database unavailable");
    }
}
