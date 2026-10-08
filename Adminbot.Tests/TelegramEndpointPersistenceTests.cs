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
using Telegram.Bot;
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

    /// <summary>CAS admits only one stale proposal and makes state, history, and per-global-superadmin alerts one atomic outcome.</summary>
    /// <returns>A task completing after one writer wins and duplicate incidents remain deduplicated.</returns>
    [Fact]
    public async Task Endpoint_cas_is_atomic_and_outbox_deduplicates_per_global_admin()
    {
        using var databases = new Databases();
        var configuration = new AppConfig { AdminsUserIds = [711, 712, 711, -1] };
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
        Assert.Equal(2, await store.CountPendingAlertsAsync(default));
        var latest = await store.GetOrCreateAsync("owned-cas", 123, default);
        Assert.True(await store.TrySaveAsync(latest, latest.Revision, "safe_retry_scheduled", "migration_started"));
        Assert.Equal(2, await store.CountPendingAlertsAsync(default));
        latest.OperationId = Guid.NewGuid().ToString("N");
        Assert.True(await store.TrySaveAsync(latest, latest.Revision, "migration_requested", "migration_started"));
        Assert.Equal(4, await store.CountPendingAlertsAsync(default));
        await using var db = databases.Users.CreateDbContext();
        Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
        Assert.Empty(await db.ReferralRewards.ToListAsync());
        Assert.All(await db.TelegramEndpointAlerts.ToListAsync(), x => Assert.Contains(x.RecipientTelegramUserId, new long[] { 711, 712 }));
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

    /// <summary>Unconfigured independent transport retains incidents across restart and caps safe attempts into visible manual review.</summary>
    /// <returns>A task completing after retry backoff and the undelivered count are asserted.</returns>
    [Fact]
    public async Task Endpoint_alert_unconfigured_transport_restart_retry_cap_never_drops_incident()
    {
        using var harness = new EndpointAlertHarness(configured: false, maxAttempts: 2);
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var first = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, first.Status);
        Assert.Equal("transport_unconfigured", first.ErrorCategory);
        Assert.True(first.NextAttemptAtUtc > harness.Now);
        Assert.False(await worker.ProcessOneAsync(default));
        harness.Advance(TimeSpan.FromMinutes(1));
        using var restarted = harness.Worker(new TelegramEndpointStore(harness.Databases.Users, harness.Configuration, harness.Options));
        Assert.True(await restarted.ProcessOneAsync(default));
        var retained = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.ManualReview, retained.Status);
        Assert.Equal(2, retained.Attempts);
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
        Assert.True(await restarted.MarkAlertSendStartedAsync(recovered, harness.Now, default));
        harness.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await restarted.ClaimAlertAsync(harness.Now, default));
        Assert.Equal(TelegramEndpointAlertStatus.DeliveryUncertain, (await harness.AlertAsync()).Status);
        Assert.Equal(0, harness.Http.Sends);
        Assert.Equal(1, await restarted.CountPendingAlertsAsync(default));
    }

    /// <summary>Current global authorization is checked even when the recipient had privileges when the incident was queued.</summary>
    /// <returns>A task completing after revoked authorization prevents every network request.</returns>
    [Fact]
    public async Task Endpoint_alert_recipient_authorization_is_rechecked_and_retained()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Configuration.AdminsUserIds.Clear();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(TelegramEndpointAlertStatus.ManualReview, (await harness.AlertAsync()).Status);
        Assert.Equal(0, harness.Http.Probes);
        Assert.Equal(0, harness.Http.Sends);
        Assert.Equal(1, await harness.Store.CountPendingAlertsAsync(default));
    }

    /// <summary>Live allowlist removal blocks a queued alert even when the startup AppConfig still contains the recipient.</summary>
    /// <returns>A task completing after the durable intent remains visible without an identity probe or send.</returns>
    [Fact]
    public async Task Endpoint_alert_live_allowlist_reload_does_not_reuse_startup_authority()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        var live = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminsUserIds:0"] = "711"
        }).Build();
        var store = new TelegramEndpointStore(harness.Databases.Users, harness.Configuration, harness.Options, live);
        Assert.True(store.IsAuthorizedRecipient(711));
        live["AdminsUserIds:0"] = null;
        using var worker = harness.Worker(store);
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(TelegramEndpointAlertStatus.ManualReview, (await harness.AlertAsync()).Status);
        Assert.Equal(0, harness.Http.Probes);
        Assert.Equal(0, harness.Http.Sends);
        Assert.Equal(1, await store.CountPendingAlertsAsync(default));
    }

    /// <summary>A read-only notifier getMe must prove the exact configured identity before any incident send.</summary>
    /// <returns>A task completing after wrong remote identity is retained as a safe pre-send retry.</returns>
    [Fact]
    public async Task Endpoint_alert_notifier_remote_identity_mismatch_never_sends()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Http.Identity = 910001;
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Equal("notifier_identity_mismatch", row.ErrorCategory);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Equal(1, harness.Http.Probes);
        Assert.Equal(0, harness.Http.Sends);
    }

    /// <summary>A definitive independent-notifier 429 is retryable after its provider delay, while a permanent rejection is retained for manual review.</summary>
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
        harness.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await worker.ProcessOneAsync(default));
        harness.Http.RejectedSendStatus = null;
        harness.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(status == 429, await worker.ProcessOneAsync(default));
        Assert.Equal(status == 429 ? 2 : 1, harness.Http.Sends);
        Assert.Equal(status == 429 ? TelegramEndpointAlertStatus.Delivered : TelegramEndpointAlertStatus.ManualReview, (await harness.AlertAsync()).Status);
    }

    /// <summary>Authorization revoked while the independent identity probe is in flight must still block the final send.</summary>
    /// <returns>A task completing after the second authorization fence prevents delivery.</returns>
    [Fact]
    public async Task Endpoint_alert_authorization_changed_during_probe_blocks_send_boundary()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        harness.Http.AfterProbe = () => harness.Configuration.AdminsUserIds.Clear();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.ManualReview, row.Status);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Equal(1, harness.Http.Probes);
        Assert.Equal(0, harness.Http.Sends);
    }

    /// <summary>Notifier reservation is exact, enabled, owned, identity-bound, and cannot reuse a formerly Local identity.</summary>
    /// <returns>A task completing after denied transports produce no notifier probes or sends.</returns>
    [Fact]
    public async Task Endpoint_alert_notifier_must_be_exact_enabled_owned_cloud_only_identity()
    {
        using var harness = new EndpointAlertHarness();
        await harness.QueueAsync();
        var notifier = harness.Registry.Bots.Single(x => x.Id == "endpoint-notifier");
        notifier.Enabled = false;
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        notifier.Enabled = true;
        notifier.Type = BotInstanceTypes.Tenant;
        harness.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await worker.ProcessOneAsync(default));
        notifier.Type = BotInstanceTypes.Owned;
        notifier.Token = "910001:" + new string('b', 35);
        harness.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await worker.ProcessOneAsync(default));
        notifier.Token = "910000:" + new string('a', 35);
        var oldAlias = await harness.Store.GetOrCreateAsync("former-notifier-alias", 910000, default);
        oldAlias.DesiredEndpoint = TelegramEndpointType.Local;
        oldAlias.EffectiveEndpoint = TelegramEndpointType.Local;
        oldAlias.MigrationState = TelegramEndpointMigrationState.Local;
        Assert.True(await harness.Store.TrySaveAsync(oldAlias, oldAlias.Revision, "migration_succeeded"));
        oldAlias.DesiredEndpoint = TelegramEndpointType.Cloud;
        oldAlias.EffectiveEndpoint = TelegramEndpointType.Cloud;
        oldAlias.MigrationState = TelegramEndpointMigrationState.Cloud;
        Assert.True(await harness.Store.TrySaveAsync(oldAlias, oldAlias.Revision, "cloud_recovered"));
        harness.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal("notifier_not_cloud", (await harness.AlertAsync()).ErrorCategory);
        Assert.Equal(0, harness.Http.Sends);
        Assert.Equal(0, harness.Http.Probes);
    }

    /// <summary>Provider acknowledgment delivers exactly once and renders Persian text without storing message or error bodies.</summary>
    /// <returns>A task completing after SDK delivery and durable receipt checks.</returns>
    [Fact]
    public async Task Endpoint_alert_acknowledged_send_delivers_once_with_independent_cloud_transport()
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
        Assert.Equal(1, harness.Http.Sends);
        Assert.Equal(1, harness.Http.Probes);
        Assert.False(await worker.ProcessOneAsync(default));
        Assert.Equal(0, await harness.Store.CountPendingAlertsAsync(default));
    }

    /// <summary>Detailed delivered history expires without erasing the permanent incident/recipient deduplication receipt.</summary>
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

    /// <summary>Missing Local file mapping blocks eligibility rather than changing the shared container; paired absolute roots are required.</summary>
    [Fact]
    public void Endpoint_options_default_cloud_mapping_prerequisites_and_exact_notifier()
    {
        var source = new TelegramEndpointRoutingOptions();
        var snapshot = source.ValidateAndSnapshot();
        Assert.True(snapshot.Enabled);
        Assert.False(snapshot.AutomaticFailback);
        Assert.False(snapshot.HasLocalFileMapping);
        Assert.Equal("https://api.telegram.org", snapshot.CloudBaseUrl);
        source.FailureThreshold = 8;
        Assert.Equal(3, snapshot.FailureThreshold);
        Assert.Throws<ArgumentException>(() => new TelegramEndpointRoutingOptions { LocalFileServerRoot = "/var/lib/telegram" }.ValidateAndSnapshot());
        Assert.Throws<ArgumentException>(() => new TelegramEndpointRoutingOptions { NotificationBotId = "notifier" }.ValidateAndSnapshot());
        Assert.Throws<ArgumentException>(() => new TelegramEndpointRoutingOptions { NotificationTelegramBotId = 910000 }.ValidateAndSnapshot());
        var configured = new TelegramEndpointRoutingOptions
        {
            LocalFileServerRoot = "/var/lib/telegram", LocalFileHostRoot = Path.GetFullPath(Path.GetTempPath()),
            NotificationBotId = "notifier", NotificationTelegramBotId = 910000
        }.ValidateAndSnapshot();
        Assert.True(configured.HasLocalFileMapping);
        Assert.True(configured.IsNotificationBot("notifier", 910000));
        Assert.False(configured.IsNotificationBot("NOTIFIER", 910000));
        Assert.False(configured.IsNotificationBot("notifier", 910001));
    }

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
        await migrator.MigrateAsync("20261009120000_AddTelegramEndpointRouting");
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
        /// <summary>Global authorization remains mutable to exercise revocation.</summary>
        public AppConfig Configuration { get; } = new() { AdminsUserIds = [711] };
        /// <summary>Trusted origin settings with explicitly fake reserved bot identity.</summary>
        public TelegramEndpointRoutingOptions Options { get; }
        /// <summary>Exact current registry bots.</summary>
        public BotRegistry Registry { get; }
        /// <summary>Endpoint-only outbox store.</summary>
        public TelegramEndpointStore Store { get; }
        /// <summary>Controlled SDK transport that never opens a socket.</summary>
        public EndpointAlertHttp Http { get; } = new();
        /// <summary>Controlled monotonic UTC wall clock.</summary>
        private readonly EndpointAlertClock _clock = new();
        /// <summary>Provider still applies exact registry lookup and control-client behavior.</summary>
        private readonly BotClientProvider _clients;
        /// <summary>Fixture clock's current UTC instant.</summary>
        public DateTime Now => _clock.GetUtcNow().UtcDateTime;

        /// <summary>Creates fake bot definitions and isolated persistence without using any production token.</summary>
        /// <param name="configured">Whether to configure the independent notifier explicitly.</param>
        /// <param name="maxAttempts">Finite safe pre-send retry cap.</param>
        public EndpointAlertHarness(bool configured = true, int maxAttempts = 12)
        {
            Options = new TelegramEndpointRoutingOptions
            {
                NotificationBotId = configured ? "endpoint-notifier" : "",
                NotificationTelegramBotId = configured ? 910000 : null,
                NotificationMaxAttempts = maxAttempts
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bots:0:Id"] = "endpoint-notifier", ["Bots:0:Token"] = "910000:" + new string('a', 35),
                ["Bots:0:Type"] = BotInstanceTypes.Owned, ["Bots:0:Enabled"] = "true", ["Bots:0:IsDefault"] = "true"
            }).Build();
            Registry = new BotRegistry(configuration);
            _clients = new BotClientProvider(Registry, bot => new TelegramBotClient(
                new TelegramBotClientOptions(bot.Token) { RetryCount = 0 }, new HttpClient(Http, disposeHandler: false)));
            Store = new TelegramEndpointStore(Databases.Users, Configuration, Options);
        }

        /// <summary>Creates a worker against the current durable store or a simulated restarted store.</summary>
        /// <param name="store">Optional independently constructed replacement store.</param>
        /// <returns>A disposable worker using the shared fake-only transport and deterministic clock.</returns>
        public TelegramEndpointNotificationWorker Worker(TelegramEndpointStore? store = null) => new(store ?? Store, Options,
            Registry, _clients, NullLogger<TelegramEndpointNotificationWorker>.Instance, _clock);

        /// <summary>Queues one safe synthetic migration incident transactionally.</summary>
        /// <returns>A task completing after the per-superadmin intent is durable.</returns>
        public async Task QueueAsync()
        {
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
        /// <summary>BotFather identity returned by the synthetic independent Cloud probe.</summary>
        public long Identity { get; set; } = 910000;
        /// <summary>Optional fixture callback simulating authorization or registry changes during the probe.</summary>
        public Action? AfterProbe { get; set; }

        /// <summary>Produces Bot API responses or scripted failures without persisting private request data.</summary>
        /// <param name="request">Private SDK request inspected only for its final method name.</param>
        /// <param name="cancellationToken">Cancellation of the synthetic request.</param>
        /// <returns>A valid SDK response or a deliberate ambiguous transport exception.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
            else
            {
                Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                Sends++;
                if (AmbiguousSend) throw new HttpRequestException("private fixture error");
                if (RejectedSendStatus is int status)
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(
                        $"{{\"ok\":false,\"error_code\":{status},\"description\":\"synthetic rejection\",\"parameters\":{{\"retry_after\":5}}}}",
                        Encoding.UTF8, "application/json") });
                body = "{\"ok\":true,\"result\":{\"message_id\":1,\"date\":1700000000,\"chat\":{\"id\":711,\"type\":\"private\"},\"text\":\"ack\"}}";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
