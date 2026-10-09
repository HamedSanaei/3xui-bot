using System.Net;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Adminbot.Services.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Old registry bots remain lazy Cloud defaults; restart and exact BotFather replacement retain separate detached identities.</summary>
    /// <returns>A task completing after durable identity isolation is asserted.</returns>
    [Fact]
    public async Task Endpoint_state_defaults_restart_identity_and_detached_contexts()
    {
        using var databases = new Databases();
        var configuration = new AppConfig { AdminsUserIds = [711] };
        var store = new TelegramEndpointStore(databases.Users, configuration);
        Assert.Empty(await store.ReadAllAsync(default));
        var first = await store.GetOrCreateAsync("owned-a", 123, default);
        Assert.Equal(TelegramEndpointType.Cloud, first.DesiredEndpoint);
        Assert.Equal(TelegramEndpointType.Cloud, first.EffectiveEndpoint);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, first.MigrationState);
        Assert.Equal(1, first.Generation);
        Assert.True(first.AutoFailoverEnabled);
        first.DesiredEndpoint = TelegramEndpointType.Local;
        Assert.Equal(TelegramEndpointType.Cloud, (await store.GetOrCreateAsync("owned-a", 123, default)).DesiredEndpoint);
        Assert.True(await store.TrySaveAsync(first, 0, "migration_requested"));
        var restarted = new TelegramEndpointStore(databases.Users, configuration);
        Assert.Equal(TelegramEndpointType.Local, (await restarted.GetOrCreateAsync("owned-a", 123, default)).DesiredEndpoint);
        var replacement = await restarted.GetOrCreateAsync("owned-a", 456, default);
        Assert.Equal(TelegramEndpointType.Cloud, replacement.DesiredEndpoint);
        Assert.Equal(0, replacement.Revision);
        Assert.Equal(2, (await restarted.ReadAllAsync(default)).Count);
        Assert.Single(await restarted.ReadHistoryAsync("owned-a", 123, 100, default));
        Assert.Empty(await restarted.ReadHistoryAsync("owned-a", 456, 100, default));
    }

    /// <summary>SQLite-loaded UTC success timestamps survive real health observations instead of being rejected by the JSONL schema.</summary>
    /// <returns>A task completing after repeated SDK health reads persist UTC metadata with zero writer-side schema loss.</returns>
    [Fact]
    public async Task Endpoint_sqlite_loaded_health_timestamps_persist_as_utc_without_schema_loss()
    {
        using var databases = new Databases();
        var directory = Path.Combine(Path.GetTempPath(), "adminbot-endpoint-utc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = EndpointRuntimeRegistry();
            var store = new TelegramEndpointStore(databases.Users, new AppConfig());
            var state = await store.GetOrCreateAsync("endpoint-a", 123456, default);
            state.LastSuccessfulHealthAtUtc = DateTime.UtcNow;
            Assert.True(await store.TrySaveAsync(state, state.Revision));
            var gate = new TelegramEndpointRuntimeGate(registry, store);
            using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions(), directory, NullLogger<LatencyTelemetryService>.Instance);
            using var http = new EndpointRuntimeHttp();
            using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http, telemetry);
            using var root = new HttpClient(http, disposeHandler: false);
            using var protocol = new TelegramEndpointProtocol(clients, new TelegramEndpointRoutingOptions(), root);
            using var services = new ServiceCollection().BuildServiceProvider();
            using var coordinator = new TelegramEndpointCoordinator(store, gate, registry, clients, new ConfigurationBuilder().Build(),
                new TelegramEndpointRoutingOptions(), services, NullLogger<TelegramEndpointCoordinator>.Instance, telemetry, protocol: protocol);
            await telemetry.StartAsync(default);
            try
            {
                await coordinator.InitializeAsync(default);
                await coordinator.RefreshHealthAsync("endpoint-a", default);
                await coordinator.RefreshHealthAsync("endpoint-a", default);
            }
            finally { await telemetry.StopAsync(default); }
            Assert.Equal(0, telemetry.DroppedEvents);
            Assert.Equal(0, telemetry.WriterFailures);
            var observations = Directory.GetFiles(telemetry.StorageDirectory, "*.jsonl").SelectMany(File.ReadLines)
                .Select(line => { using var parsed = JsonDocument.Parse(line); return parsed.RootElement.Clone(); })
                .Where(row => row.GetProperty("eventType").GetString() == "telegram_endpoint_health").ToArray();
            Assert.Equal(2, observations.Length);
            Assert.All(observations, row => Assert.Equal(DateTimeKind.Utc, row.GetProperty("lastSuccessUtc").GetDateTime().Kind));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    /// <summary>CAS admits one stale proposal and commits a single incident intent regardless of administrator or logger configuration.</summary>
    /// <returns>A task completing after one writer wins and duplicate incidents remain durably deduplicated.</returns>
    [Fact]
    public async Task Endpoint_cas_is_atomic_and_outbox_deduplicates_per_incident()
    {
        using var databases = new Databases();
        var configuration = new AppConfig { AdminsUserIds = [] };
        var store = new TelegramEndpointStore(databases.Users, configuration);
        var first = await store.GetOrCreateAsync("owned-cas", 123, default);
        first.OperationId = Guid.NewGuid().ToString("N");
        first.DesiredEndpoint = TelegramEndpointType.Local;
        first.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
        var stale = first.Copy();
        var results = await Task.WhenAll(store.TrySaveAsync(first, 0, "migration_requested", "migration_started"),
            new TelegramEndpointStore(databases.Users, configuration).TrySaveAsync(stale, 0, "migration_requested", "migration_started"));
        Assert.Single(results, x => x);
        Assert.Single(await store.ReadHistoryAsync("owned-cas", 123, 100, default));
        Assert.Equal(1, await store.CountPendingAlertsAsync(default));
        var latest = await store.GetOrCreateAsync("owned-cas", 123, default);
        Assert.True(await store.TrySaveAsync(latest, latest.Revision, "safe_retry_scheduled", "migration_started"));
        Assert.Equal(1, await store.CountPendingAlertsAsync(default));
        latest.OperationId = Guid.NewGuid().ToString("N");
        Assert.True(await store.TrySaveAsync(latest, latest.Revision, "migration_requested", "migration_started"));
        Assert.Equal(2, await store.CountPendingAlertsAsync(default));
        await using var db = databases.Users.CreateDbContext();
        Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
        Assert.Empty(await db.ReferralRewards.ToListAsync());
        Assert.All(await db.TelegramEndpointAlerts.ToListAsync(), x => Assert.Null(x.DestinationChatId));
    }

    /// <summary>Health counters and Local degradation do not stale operator confirmations, while failover or migration intent does.</summary>
    /// <returns>A task completing after independent CAS and control revisions are asserted.</returns>
    [Fact]
    public async Task Endpoint_control_revision_ignores_health_and_advances_for_intent()
    {
        using var databases = new Databases();
        var store = new TelegramEndpointStore(databases.Users, new AppConfig());
        var state = await store.GetOrCreateAsync("owned-health", 123, default);
        state.LastHealthCheckAtUtc = DateTime.UtcNow;
        state.ConsecutiveFailures = 1;
        state.LastFailureCategory = "timeout";
        Assert.True(await store.TrySaveAsync(state, 0));
        Assert.Equal(1, state.Revision);
        Assert.Equal(0, state.ControlRevision);
        state.DesiredEndpoint = TelegramEndpointType.Local;
        state.EffectiveEndpoint = TelegramEndpointType.Local;
        state.MigrationState = TelegramEndpointMigrationState.Local;
        Assert.True(await store.TrySaveAsync(state, state.Revision, "migration_succeeded"));
        var control = state.ControlRevision;
        state.MigrationState = TelegramEndpointMigrationState.LocalDegraded;
        Assert.True(await store.TrySaveAsync(state, state.Revision));
        Assert.Equal(control, state.ControlRevision);
        state.AutoFailoverEnabled = false;
        Assert.True(await store.TrySaveAsync(state, state.Revision, "auto_failover_changed"));
        Assert.Equal(control + 1, state.ControlRevision);
        state.Generation = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveAsync(state, state.Revision));
    }

    /// <summary>A missing logger survives long absence and restart without consuming the finite network retry budget.</summary>
    /// <returns>A task completing after repeated capped deferrals remain Pending with no SDK requests.</returns>
    [Fact]
    public async Task Endpoint_alert_missing_logger_restart_and_long_absence_never_exhaust_budget()
    {
        using var harness = new EndpointAlertHarness(configured: false, maxAttempts: 2);
        await harness.QueueAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var worker = harness.Worker(new TelegramEndpointStore(harness.Databases.Users, harness.Configuration, harness.Options));
            Assert.True(await worker.ProcessOneAsync(default));
            var retained = await harness.AlertAsync();
            Assert.Equal(TelegramEndpointAlertStatus.Pending, retained.Status);
            Assert.Equal("logger_unconfigured", retained.ErrorCategory);
            Assert.Equal(0, retained.Attempts);
            Assert.Equal(harness.Now.AddSeconds(harness.Options.RetryMaxSeconds), retained.NextAttemptAtUtc);
            Assert.False(await worker.ProcessOneAsync(default));
            harness.Advance(TimeSpan.FromDays(30));
        }
        Assert.Equal(1, await harness.Store.CountPendingAlertsAsync(default));
        Assert.Equal(0, harness.Http.Sends);
        Assert.Equal(0, harness.Http.Probes);
    }

    /// <summary>Read-only pre-send failures safely retry but post-send ambiguous failures become terminal uncertainty and never replay.</summary>
    /// <returns>A task completing after controlled SDK transport attempts and durable outcomes are asserted.</returns>
    [Fact]
    public async Task Endpoint_alert_safe_probe_retry_then_ambiguous_send_is_never_replayed()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Http.FailProbeCount = 1;
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(TelegramEndpointAlertStatus.Pending, (await harness.AlertAsync()).Status);
        Assert.Equal(0, harness.Http.Sends);
        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Http.AmbiguousSend = true;
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.DeliveryUncertain, row.Status);
        Assert.Equal("send_uncertain", row.ErrorCategory);
        Assert.NotNull(row.SendStartedAtUtc);
        Assert.Equal(1, harness.Http.Sends);
        harness.Advance(TimeSpan.FromDays(1));
        using var restarted = harness.Worker();
        Assert.False(await restarted.ProcessOneAsync(default));
        Assert.Equal(1, harness.Http.Sends);
        Assert.Equal(1, await harness.Store.CountPendingAlertsAsync(default));
        Assert.DoesNotContain("private fixture error", row.ErrorCategory);
    }

    /// <summary>Actual failed read attempts, unlike missing prerequisites, retain the finite retry cap and safe fixed diagnostics.</summary>
    /// <returns>A task completing after two failed SDK identity reads require manual review without dispatch.</returns>
    [Fact]
    public async Task Endpoint_alert_actual_read_failures_exhaust_finite_retry_budget()
    {
        using var harness = new EndpointAlertHarness(maxAttempts: 2);
        await harness.QueueAsync();
        harness.Http.FailProbeCount = 10;
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(1, (await harness.AlertAsync()).Attempts);
        harness.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await worker.ProcessOneAsync(default));
        var exhausted = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.ManualReview, exhausted.Status);
        Assert.Equal(2, exhausted.Attempts);
        Assert.Equal("pre_send_failure", exhausted.ErrorCategory);
        Assert.Equal(0, harness.Http.Sends);
    }

    /// <summary>Interrupted pre-send leases recover safely; interrupted send-boundary leases become uncertain on restart.</summary>
    /// <returns>A task completing after both persisted crash boundaries are reconciled.</returns>
    [Fact]
    public async Task Endpoint_alert_crash_boundaries_recover_or_quarantine_without_replay()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        var beforeSend = await harness.Store.ClaimAlertAsync(harness.Now, default);
        Assert.NotNull(beforeSend);
        harness.Advance(TimeSpan.FromMinutes(5));
        var restarted = new TelegramEndpointStore(harness.Databases.Users, harness.Configuration, harness.Options);
        var recovered = await restarted.ClaimAlertAsync(harness.Now, default);
        Assert.NotNull(recovered);
        Assert.NotEqual(beforeSend!.ClaimId, recovered!.ClaimId);
        Assert.True(await restarted.MarkAlertSendStartedAsync(recovered, -100711000, harness.Now, default));
        harness.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await restarted.ClaimAlertAsync(harness.Now, default));
        Assert.Equal(TelegramEndpointAlertStatus.DeliveryUncertain, (await harness.AlertAsync()).Status);
        Assert.Equal(0, harness.Http.Sends);
        Assert.Equal(1, await restarted.CountPendingAlertsAsync(default));
    }

    /// <summary>The send boundary accepts only negative channel ids and freezes the destination across definitive 429 and proven pre-dispatch deferrals.</summary>
    /// <returns>A task completing after atomic destination CAS rejects retargeting and unused marked claims are safely refunded.</returns>
    [Fact]
    public async Task Endpoint_alert_send_boundary_pins_channel_and_defers_only_before_dispatch()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        var first = await harness.Store.ClaimAlertAsync(harness.Now, default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Store.MarkAlertSendStartedAsync(first!, 711, harness.Now, default));
        Assert.True(await harness.Store.MarkAlertSendStartedAsync(first!, -100711000, harness.Now, default));
        await harness.Store.RetryRateLimitedAlertAsync(first!, harness.Now, 1, default);
        harness.Advance(TimeSpan.FromSeconds(2));
        var second = await harness.Store.ClaimAlertAsync(harness.Now, default);
        Assert.Equal(-100711000L, second!.DestinationChatId);
        Assert.False(await harness.Store.MarkAlertSendStartedAsync(second, -100711001, harness.Now, default));
        Assert.True(await harness.Store.MarkAlertSendStartedAsync(second, -100711000, harness.Now, default));
        await harness.Store.DeferUnsentAlertAsync(second, "transport_unavailable", harness.Now, default);
        var deferred = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, deferred.Status);
        Assert.Null(deferred.SendStartedAtUtc);
        Assert.Null(deferred.SendDeadlineAtUtc);
        Assert.Equal(-100711000L, deferred.DestinationChatId);
        Assert.Equal(1, deferred.Attempts);
        Assert.Equal(harness.Now.AddSeconds(harness.Options.RetryMaxSeconds), deferred.NextAttemptAtUtc);
        Assert.Equal(0, harness.Http.Sends);
    }

    /// <summary>Live logger configuration overrides startup authority and invalid/removed root values use only the explicit current fallback.</summary>
    [Fact]
    public void Endpoint_logger_resolution_uses_live_root_and_sanitized_current_fallback()
    {
        using var harness = new EndpointAlertHarness();
        var live = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["loggerChannel"] = " @current_channel "
        }).Build();
        var store = new TelegramEndpointStore(harness.Databases.Users, harness.Configuration, harness.Options, live);
        Assert.Equal("@current_channel", store.ResolveLoggerChannel("-100711001"));
        live["loggerChannel"] = null;
        Assert.Equal("-100711001", store.ResolveLoggerChannel(" -100711001 "));
        Assert.Equal("", store.ResolveLoggerChannel("not_a_destination"));
        live["loggerChannel"] = "malformed";
        Assert.Equal("", store.ResolveLoggerChannel(null));
        Assert.Equal("-100711000", harness.Store.ResolveLoggerChannel(null));
    }

    /// <summary>A read-only sender getMe must prove the exact current owned identity before any incident send.</summary>
    /// <returns>A task completing after wrong remote identity is retained as a safe pre-send retry.</returns>
    [Fact]
    public async Task Endpoint_alert_sender_remote_identity_mismatch_never_sends()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Http.Identity = 910001;
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Equal("pre_send_identity_mismatch", row.ErrorCategory);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Equal(1, harness.Http.Probes);
        Assert.Equal(0, harness.Http.Sends);
    }

    /// <summary>Changing the configured logger during read-only preparation defers instead of sending to stale or newly substituted destinations.</summary>
    /// <returns>A task completing after the live target fence retains a zero-budget Pending intent without a send boundary.</returns>
    [Fact]
    public async Task Endpoint_alert_logger_change_during_probe_blocks_send_boundary()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Http.AfterProbe = () => harness.Configuration.LoggerChannel = "-100711001";
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Equal("logger_unavailable", row.ErrorCategory);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.DestinationChatId);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Equal(0, harness.Http.Sends);
    }

    /// <summary>A definitive 429 retries the same frozen channel after its delay; a permanent rejection remains manual review.</summary>
    /// <param name="status">Explicit Telegram API rejection code, not a transport ambiguity.</param>
    /// <returns>A task completing after durable state proves only the 429 can cause a second provider send.</returns>
    [Theory]
    [InlineData(429)]
    [InlineData(403)]
    public async Task Endpoint_alert_definitive_rejection_has_bounded_nonambiguous_delivery_policy(int status)
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Http.RejectedSendStatus = status;
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var rejected = await harness.AlertAsync();
        Assert.Equal(status == 429 ? TelegramEndpointAlertStatus.Pending : TelegramEndpointAlertStatus.ManualReview, rejected.Status);
        Assert.Equal(status == 429 ? "rate_limited" : "send_rejected", rejected.ErrorCategory);
        Assert.Equal(-100711000L, rejected.DestinationChatId);
        harness.Configuration.LoggerChannel = "-100711001";
        harness.Http.ChannelId = -100711001;
        harness.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await worker.ProcessOneAsync(default));
        harness.Http.RejectedSendStatus = null;
        harness.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(status == 429, await worker.ProcessOneAsync(default));
        Assert.Equal(status == 429 ? 2 : 1, harness.Http.Sends);
        Assert.Equal(status == 429 ? TelegramEndpointAlertStatus.Delivered : TelegramEndpointAlertStatus.ManualReview, (await harness.AlertAsync()).Status);
        Assert.All(harness.Http.SentChatIds, id => Assert.Equal(-100711000L, id));
    }

    /// <summary>Current unavailable Local senders defer without budget loss; verified CloudRecovered senders remain eligible after Local history.</summary>
    /// <returns>A task completing after current route gating, safe restart deferral, and recovered logger delivery.</returns>
    [Fact]
    public async Task Endpoint_alert_local_sender_defers_and_cloud_recovered_sender_delivers()
    {
        using var harness = new EndpointAlertHarness(maxAttempts: 2);
        await harness.QueueAsync();
        var sender = await harness.Store.GetOrCreateAsync("endpoint-sender", 910000, default);
        sender.DesiredEndpoint = TelegramEndpointType.Local;
        sender.EffectiveEndpoint = TelegramEndpointType.Local;
        sender.MigrationState = TelegramEndpointMigrationState.Local;
        Assert.True(await harness.Store.TrySaveAsync(sender, sender.Revision, "migration_succeeded"));
        harness.Gate.Publish(sender);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var worker = harness.Worker();
            Assert.True(await worker.ProcessOneAsync(default));
            var pending = await harness.AlertAsync();
            Assert.Equal(TelegramEndpointAlertStatus.Pending, pending.Status);
            Assert.Equal("transport_unavailable", pending.ErrorCategory);
            Assert.Equal(0, pending.Attempts);
            harness.Advance(TimeSpan.FromDays(30));
        }
        Assert.Equal(0, harness.Http.Probes);
        Assert.Equal(0, harness.Http.Sends);
        sender.EffectiveEndpoint = TelegramEndpointType.Cloud;
        sender.MigrationState = TelegramEndpointMigrationState.CloudRecovered;
        sender.Generation++;
        Assert.True(await harness.Store.TrySaveAsync(sender, sender.Revision, "cloud_recovered"));
        harness.Gate.Publish(sender);
        using var recoveredWorker = harness.Worker();
        Assert.True(await recoveredWorker.ProcessOneAsync(default));
        Assert.Equal(TelegramEndpointAlertStatus.Delivered, (await harness.AlertAsync()).Status);
        Assert.Equal(1, harness.Http.Sends);
    }

    /// <summary>Provider acknowledgment delivers exactly once to the verified channel without persisting message/error bodies or private recipients.</summary>
    /// <returns>A task completing after real SDK logger delivery and durable receipt checks.</returns>
    [Fact]
    public async Task Endpoint_alert_acknowledged_send_delivers_once_to_logger_channel()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Delivered, row.Status);
        Assert.NotNull(row.DeliveredAtUtc);
        Assert.NotNull(row.SendStartedAtUtc);
        Assert.Null(row.ErrorCategory);
        Assert.Equal(-100711000L, row.DestinationChatId);
        Assert.Single(harness.Http.SentChatIds, id => id == -100711000L);
        Assert.Equal(1, harness.Http.Sends);
        Assert.Equal(1, harness.Http.Probes);
        Assert.False(await worker.ProcessOneAsync(default));
        Assert.Equal(0, await harness.Store.CountPendingAlertsAsync(default));
    }

    /// <summary>Detailed delivered history expires without erasing the permanent incident-only deduplication receipt.</summary>
    /// <returns>A task completing after an old incident cannot create another send intent.</returns>
    [Fact]
    public async Task Endpoint_alert_retention_preserves_durable_deduplication()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        harness.Advance(TimeSpan.FromDays(91));
        await harness.Store.PruneDeliveredAlertsAsync(harness.Now, default);
        await using (var db = harness.Databases.Users.CreateDbContext())
        {
            Assert.Empty(await db.TelegramEndpointAlerts.ToListAsync());
            Assert.Single(await db.TelegramEndpointAlertReceipts.ToListAsync());
        }
        var unchangedIncident = await harness.Store.GetOrCreateAsync("migrating-owned", 123, default);
        Assert.True(await harness.Store.TrySaveAsync(unchangedIncident, unchangedIncident.Revision,
            "safe_retry_scheduled", "migration_started"));
        Assert.False(await worker.ProcessOneAsync(default));
        Assert.Equal(1, harness.Http.Sends);
        Assert.Equal(0, await harness.Store.CountPendingAlertsAsync(default));
    }

    /// <summary>Only fixed trusted Cloud and loopback Local origins survive startup validation.</summary>
    /// <param name="cloud">Synthetic configured Cloud origin.</param>
    /// <param name="local">Synthetic configured Local origin.</param>
    [Theory]
    [InlineData("http://api.telegram.org", "http://127.0.0.1:8081")]
    [InlineData("https://example.invalid", "http://127.0.0.1:8081")]
    [InlineData("https://api.telegram.org:444", "http://127.0.0.1:8081")]
    [InlineData("https://api.telegram.org", "http://192.168.1.10:8081")]
    [InlineData("https://api.telegram.org", "https://localhost:8081")]
    [InlineData("https://api.telegram.org/path", "http://localhost:8081")]
    [InlineData("https://api.telegram.org/.", "http://localhost:8081")]
    [InlineData("https://api.telegram.org", "http://localhost:8081/./")]
    [InlineData("https://api.telegram.org", "http://user@localhost:8081")]
    [InlineData("https://api.telegram.org", "http://localhost:8081/?query=1")]
    [InlineData("https://api.telegram.org", "http://localhost:8081/#fragment")]
    public void Endpoint_options_reject_untrusted_origins(string cloud, string local) =>
        Assert.Throws<ArgumentException>(() => new TelegramEndpointRoutingOptions { CloudBaseUrl = cloud, LocalBaseUrl = local }.ValidateAndSnapshot());


    /// <summary>Raw payloads cannot enter endpoint history or error fields.</summary>
    /// <returns>A task completing after unsafe state/history proposals are rejected without persistence.</returns>
    [Fact]
    public async Task Endpoint_store_rejects_unknown_error_and_history_payloads()
    {
        using var databases = new Databases();
        var store = new TelegramEndpointStore(databases.Users, new AppConfig());
        var state = await store.GetOrCreateAsync("owned-safe", 123, default);
        state.LastFailureCategory = "910000:private_token";
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveAsync(state, 0));
        state.LastFailureCategory = null;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveAsync(state, 0, "raw_provider_error"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveAsync(state, 0, alertCategory: "raw_provider_error"));
        Assert.Equal(0, (await store.GetOrCreateAsync("owned-safe", 123, default)).Revision);
        Assert.Empty(await store.ReadHistoryAsync("owned-safe", 123, 100, default));
    }

    /// <summary>The additive production migration preserves every existing table/index definition and does not create migration intents for old bots.</summary>
    /// <returns>A task completing after a prior-schema users.db upgrade and existing-definition comparison.</returns>
    [Fact]
    public async Task Endpoint_migration_upgrades_prior_schema_without_financial_model_changes()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261005120000_AddTenantPublicChannelPostsEnabled");
        var before = await EndpointSchemaAsync(db);
        await migrator.MigrateAsync("20261009130000_RouteEndpointIncidentsToLoggerChannel");
        var after = await EndpointSchemaAsync(db);
        foreach (var definition in before) Assert.Equal(definition.Value, after[definition.Key]);
        Assert.Empty(await db.TelegramEndpointStates.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TelegramEndpointHistory.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TelegramEndpointAlerts.AsNoTracking().ToListAsync());
        Assert.Contains("table:TelegramEndpointStates", after.Keys);
        Assert.Contains("table:TelegramEndpointHistory", after.Keys);
        Assert.Contains("table:TelegramEndpointAlerts", after.Keys);
        Assert.Contains("table:TelegramEndpointAlertReceipts", after.Keys);
        var store = new TelegramEndpointStore(databases.Users, new AppConfig());
        Assert.Equal(TelegramEndpointType.Cloud, (await store.GetOrCreateAsync("old-owned", 123, default)).EffectiveEndpoint);
    }

    /// <summary>Legacy private-recipient upgrade collapses only proven unsent incidents and fences acknowledged, started, uncertain, or pruned recipients.</summary>
    /// <returns>A task completing after the real retrofit migration retains deduplication and leaves all non-outbox definitions unchanged.</returns>
    [Fact]
    public async Task Endpoint_logger_migration_consolidates_legacy_intents_and_preserves_receipt_fences()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261009120000_AddTelegramEndpointRouting");
        var before = await EndpointSchemaAsync(db);
        var scenarios = new[]
        {
            (Name: "pending", Status: TelegramEndpointAlertStatus.Pending, Started: false, Expected: TelegramEndpointAlertStatus.Pending),
            (Name: "started", Status: TelegramEndpointAlertStatus.Processing, Started: true, Expected: TelegramEndpointAlertStatus.DeliveryUncertain),
            (Name: "uncertain", Status: TelegramEndpointAlertStatus.DeliveryUncertain, Started: false, Expected: TelegramEndpointAlertStatus.DeliveryUncertain),
            (Name: "manual_started", Status: TelegramEndpointAlertStatus.ManualReview, Started: true, Expected: TelegramEndpointAlertStatus.DeliveryUncertain),
            (Name: "delivered", Status: TelegramEndpointAlertStatus.Delivered, Started: true, Expected: TelegramEndpointAlertStatus.Delivered),
            (Name: "mixed_pruned", Status: TelegramEndpointAlertStatus.Pending, Started: false, Expected: TelegramEndpointAlertStatus.Delivered)
        };
        var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var scenario in scenarios)
        {
            var operation = operations[scenario.Name] = Guid.NewGuid().ToString("N");
            var key = $"legacy-owned:123:{operation}:migration_started";
            await SeedLegacyEndpointAlertAsync(db, key, 711, scenario.Status, scenario.Started ? now : null, now);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO TelegramEndpointAlertReceipts (IncidentKey, RecipientTelegramUserId) VALUES ({key}, {712L})");
            if (scenario.Name != "mixed_pruned")
                await SeedLegacyEndpointAlertAsync(db, key, 712, TelegramEndpointAlertStatus.Pending, null, now, receiptExists: true);
        }
        var receiptOnlyOperation = Guid.NewGuid().ToString("N");
        var receiptOnlyKey = $"legacy-owned:123:{receiptOnlyOperation}:migration_started";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO TelegramEndpointAlertReceipts (IncidentKey, RecipientTelegramUserId) VALUES ({receiptOnlyKey}, {711L})");
        // An orphan detailed intent must gain a permanent receipt during the retrofit too.
        var orphanOperation = Guid.NewGuid().ToString("N");
        var orphanKey = $"legacy-owned:123:{orphanOperation}:migration_started";
        await SeedLegacyEndpointAlertAsync(db, orphanKey, 711, TelegramEndpointAlertStatus.Pending, null, now);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM TelegramEndpointAlertReceipts WHERE IncidentKey = {orphanKey}");

        await migrator.MigrateAsync("20261009130000_RouteEndpointIncidentsToLoggerChannel");
        var after = await EndpointSchemaAsync(db);
        foreach (var definition in before.Where(x =>
            !x.Key.Contains("TelegramEndpointAlerts", StringComparison.Ordinal) &&
            !x.Key.Contains("TelegramEndpointAlertReceipts", StringComparison.Ordinal)))
            Assert.Equal(definition.Value, after[definition.Key]);
        var rows = await db.TelegramEndpointAlerts.AsNoTracking().ToListAsync();
        Assert.Equal(scenarios.Length + 1, rows.Count);
        Assert.Equal(scenarios.Length + 2, await db.TelegramEndpointAlertReceipts.CountAsync());
        Assert.All(rows, row => { Assert.Null(row.DestinationChatId); Assert.Null(row.ClaimId); Assert.Null(row.LeaseUntilUtc); });
        foreach (var scenario in scenarios)
        {
            var key = $"legacy-owned:123:{operations[scenario.Name]}:migration_started";
            var row = rows.Single(x => x.IncidentKey == key);
            Assert.Equal(scenario.Expected, row.Status);
            if (scenario.Name == "mixed_pruned") Assert.Null(row.DeliveredAtUtc);
        }
        Assert.DoesNotContain(rows, x => x.IncidentKey == receiptOnlyKey);
        Assert.Equal(TelegramEndpointAlertStatus.Pending, rows.Single(x => x.IncidentKey == orphanKey).Status);
        var store = new TelegramEndpointStore(databases.Users, new AppConfig { AdminsUserIds = [] });
        var state = await store.GetOrCreateAsync("legacy-owned", 123, default);
        foreach (var operation in operations.Values.Append(receiptOnlyOperation).Append(orphanOperation))
        {
            state.OperationId = operation;
            Assert.True(await store.TrySaveAsync(state, state.Revision, alertCategory: "migration_started"));
        }
        Assert.Equal(scenarios.Length + 1, await db.TelegramEndpointAlerts.CountAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261009120000_AddTelegramEndpointRouting"));
    }

    /// <summary>Seeds the historical private-recipient schema without using the current EF entity mapping.</summary>
    /// <param name="db">Fixture-owned users.db context migrated only through the legacy routing migration.</param>
    /// <param name="key">Secret-free incident/category key shared by legacy recipient copies.</param>
    /// <param name="recipient">Positive synthetic legacy private user id.</param>
    /// <param name="status">Legacy durable delivery state to preserve during consolidation.</param>
    /// <param name="started">Optional durable legacy send boundary; any value prohibits new logger delivery.</param>
    /// <param name="now">Fixture UTC creation/scheduling time.</param>
    /// <param name="receiptExists">Whether the caller already inserted the matching compact recipient receipt.</param>
    /// <returns>A task completing after one historical outbox detail and its compact receipt exist.</returns>
    private static async Task SeedLegacyEndpointAlertAsync(UserDbContext db, string key, long recipient,
        TelegramEndpointAlertStatus status, DateTime? started, DateTime now, bool receiptExists = false)
    {
        if (!receiptExists)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO TelegramEndpointAlertReceipts (IncidentKey, RecipientTelegramUserId) VALUES ({key}, {recipient})");
        DateTime? delivered = status == TelegramEndpointAlertStatus.Delivered ? now : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO TelegramEndpointAlerts (IncidentKey, BotId, TelegramBotId, RecipientTelegramUserId, Category, MigrationState, DesiredEndpoint, EffectiveEndpoint, Generation, CreatedAtUtc, Status, Attempts, NextAttemptAtUtc, SendStartedAtUtc, DeliveredAtUtc) VALUES ({key}, {"legacy-owned"}, {123L}, {recipient}, {"migration_started"}, {0}, {0}, {0}, {1L}, {now}, {(int)status}, {0}, {now}, {started}, {delivered})");
    }

    /// <summary>Captures non-endpoint SQLite definitions for additive migration compatibility assertions.</summary>
    /// <param name="db">Fixture-owned real users.db context.</param>
    /// <returns>Exact named table/index DDL, excluding internal migration receipts and SQLite implementation tables.</returns>
    private static async Task<Dictionary<string, string>> EndpointSchemaAsync(UserDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE type IN ('table','index') AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) result.Add(reader.GetString(0) + ":" + reader.GetString(1), reader.GetString(2));
        return result;
    }

    /// <summary>Owns deterministic endpoint-only persistence and a no-network SDK wire fixture.</summary>
    private sealed class EndpointAlertHarness : IDisposable
    {
        /// <summary>Isolated real SQLite databases; never the production files.</summary>
        public Databases Databases { get; } = new();
        /// <summary>Root logger authority; administrator membership does not affect incident delivery.</summary>
        public AppConfig Configuration { get; } = new() { AdminsUserIds = [], LoggerChannel = "-100711000" };
        /// <summary>Trusted origins and finite network retry budget.</summary>
        public TelegramEndpointRoutingOptions Options { get; }
        /// <summary>Exact current registry bots.</summary>
        public BotRegistry Registry { get; }
        /// <summary>Endpoint-only outbox store.</summary>
        public TelegramEndpointStore Store { get; }
        /// <summary>Actual durable route admission gate shared by the worker and pooled SDK provider.</summary>
        public TelegramEndpointRuntimeGate Gate { get; }
        /// <summary>Controlled SDK transport that never opens a socket.</summary>
        public EndpointAlertHttp Http { get; } = new();
        /// <summary>Controlled monotonic UTC wall clock.</summary>
        private readonly EndpointAlertClock _clock = new();
        /// <summary>Provider uses the real shared gate and pooled SDK construction over the fake wire.</summary>
        private readonly BotClientProvider _clients;
        /// <summary>Fixture clock's current UTC instant.</summary>
        public DateTime Now => _clock.GetUtcNow().UtcDateTime;

        /// <summary>Creates fake bot definitions and isolated persistence without using any production token.</summary>
        /// <param name="configured">Whether the root logger destination is configured.</param>
        /// <param name="maxAttempts">Finite safe pre-send retry cap.</param>
        public EndpointAlertHarness(bool configured = true, int maxAttempts = 12)
        {
            Options = new TelegramEndpointRoutingOptions { NotificationMaxAttempts = maxAttempts };
            if (!configured) Configuration.LoggerChannel = "";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bots:0:Id"] = "endpoint-sender", ["Bots:0:Token"] = "910000:" + new string('a', 35),
                ["Bots:0:Type"] = BotInstanceTypes.Owned, ["Bots:0:Enabled"] = "true", ["Bots:0:IsDefault"] = "true"
            }).Build();
            Registry = new BotRegistry(configuration);
            Store = new TelegramEndpointStore(Databases.Users, Configuration, Options);
            Gate = new TelegramEndpointRuntimeGate(Registry, Store);
            _clients = new BotClientProvider(Registry, Gate, Options, Http);
        }

        /// <summary>Creates a worker against the current durable store or a simulated restarted store.</summary>
        /// <param name="store">Optional independently constructed replacement store.</param>
        /// <returns>A disposable worker using the shared fake-only transport and deterministic clock.</returns>
        public TelegramEndpointNotificationWorker Worker(TelegramEndpointStore? store = null) => new(store ?? Store, Options,
            Registry, _clients, Gate, NullLogger<TelegramEndpointNotificationWorker>.Instance, _clock);

        /// <summary>Queues one safe synthetic migration incident transactionally.</summary>
        /// <returns>A task completing after one incident intent is durable and the sender's actual Cloud route is hydrated.</returns>
        public async Task QueueAsync()
        {
            await Gate.HydrateAsync("endpoint-sender", 910000, default);
            var state = await Store.GetOrCreateAsync("migrating-owned", 123, default);
            state.OperationId = Guid.NewGuid().ToString("N");
            state.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
            Assert.True(await Store.TrySaveAsync(state, state.Revision, "migration_requested", "migration_started"));
        }

        /// <summary>Reads the sole detached durable alert for assertions.</summary>
        /// <returns>The fixture's alert, independent of a live EF context.</returns>
        public async Task<TelegramEndpointAlert> AlertAsync()
        {
            await using var db = Databases.Users.CreateDbContext();
            return await db.TelegramEndpointAlerts.AsNoTracking().SingleAsync();
        }

        /// <summary>Advances only the fixture clock so retry tests do not sleep.</summary>
        /// <param name="elapsed">Positive synthetic elapsed duration.</param>
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        /// <summary>Disposes controlled transports before deleting fixture-owned databases.</summary>
        public void Dispose() { _clients.Dispose(); Http.Dispose(); Databases.Dispose(); }
    }

    /// <summary>Deterministic UTC clock used for durable lease and retry boundaries.</summary>
    private sealed class EndpointAlertClock : TimeProvider
    {
        /// <summary>Fixture-local current instant, initially later than wall-clock intent creation.</summary>
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddSeconds(1);
        /// <summary>Returns the current fixture UTC time.</summary>
        /// <returns>Current UTC instant without consulting network or the system clock again.</returns>
        public override DateTimeOffset GetUtcNow() => _now;
        /// <summary>Advances fixture time.</summary>
        /// <param name="elapsed">Synthetic elapsed duration.</param>
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    /// <summary>Real SDK HTTP fixture with controlled identity reads and ambiguous send failures, never a network socket.</summary>
    private sealed class EndpointAlertHttp : HttpMessageHandler
    {
        /// <summary>Number of real SDK send method invocations.</summary>
        public int Sends { get; private set; }
        /// <summary>Number of real SDK getMe invocations.</summary>
        public int Probes { get; private set; }
        /// <summary>Remaining safe probe failures before a valid identity response.</summary>
        public int FailProbeCount { get; set; }
        /// <summary>Whether sends fail after crossing the provider boundary.</summary>
        public bool AmbiguousSend { get; set; }
        /// <summary>Optional definitive provider rejection; the fixture returns a valid typed Bot API error envelope.</summary>
        public int? RejectedSendStatus { get; set; }
        /// <summary>BotFather identity returned by the synthetic Cloud identity probe.</summary>
        public long Identity { get; set; } = 910000;
        /// <summary>Optional callback simulating registry or logger changes during the identity probe.</summary>
        public Action? AfterProbe { get; set; }
        /// <summary>Channel returned for current logger lookup; numeric frozen lookups return their requested id.</summary>
        public long ChannelId { get; set; } = -100711000;
        /// <summary>Wire destinations observed only in this fixture, proving no private send or retry retargeting.</summary>
        public List<long> SentChatIds { get; } = [];

        /// <summary>Produces Bot API responses or scripted failures without persisting private request data.</summary>
        /// <param name="request">Synthetic SDK request inspected for method and fixture chat id only; tokens and message bodies are not retained.</param>
        /// <param name="cancellationToken">Cancellation of the synthetic request.</param>
        /// <returns>A valid SDK response or a deliberate ambiguous transport exception.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/getMe", StringComparison.OrdinalIgnoreCase))
            {
                Probes++;
                if (FailProbeCount-- > 0) throw new HttpRequestException("private fixture error");
                AfterProbe?.Invoke();
                body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true, result = new { id = Identity, is_bot = true, first_name = "fixture", username = "endpoint_fixture_bot" }
                });
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/getChat", StringComparison.OrdinalIgnoreCase))
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var target = payload.RootElement.GetProperty("chat_id");
                var id = target.ValueKind == JsonValueKind.Number ? target.GetInt64() :
                    long.TryParse(target.GetString(), out var numeric) ? numeric : ChannelId;
                body = JsonSerializer.Serialize(new { ok = true, result = new { id, type = "channel", title = "fixture logger" } });
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/getChatMember", StringComparison.OrdinalIgnoreCase))
                body = JsonSerializer.Serialize(new { ok = true, result = new { status = "administrator", can_post_messages = true,
                    can_manage_chat = true, can_delete_messages = true, can_manage_video_chats = true, can_restrict_members = true,
                    can_promote_members = true, can_change_info = true, can_invite_users = true, is_anonymous = false,
                    user = new { id = Identity, is_bot = true, first_name = "fixture" } } });
            else
            {
                Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var target = payload.RootElement.GetProperty("chat_id");
                var chatId = target.ValueKind == JsonValueKind.Number ? target.GetInt64() : long.Parse(target.GetString()!);
                SentChatIds.Add(chatId);
                Sends++;
                if (AmbiguousSend) throw new HttpRequestException("private fixture error");
                if (RejectedSendStatus is int status)
                    return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(
                        $"{{\"ok\":false,\"error_code\":{status},\"description\":\"synthetic rejection\",\"parameters\":{{\"retry_after\":5}}}}",
                        Encoding.UTF8, "application/json") };
                body = JsonSerializer.Serialize(new { ok = true, result = new { message_id = 1, date = 1700000000,
                    chat = new { id = chatId, type = "channel", title = "fixture logger" }, text = "ack" } });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
