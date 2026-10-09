using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Adminbot.Domain;
using System.Text.Json;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.Extensions.Configuration;
using Adminbot.Services.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Xunit;

/// <summary>Deterministic durability, session-ordering and health tests using only fake bot transports.</summary>
/// <remarks>No production token, Bot API network request, container command or database is used. The real runtime
/// gate and coordinator are exercised with a CAS store, controlled clock and serialized receiver lifecycle.</remarks>
public sealed partial class TelegramEndpointCoordinatorTests
{
    /// <summary>Cloud logout must commit its marker and be acknowledged before the first real Local identity request.</summary>
    [Fact]
    public async Task Cloud_to_local_verifies_root_and_source_then_logs_out_once_before_local_identity()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Empty(f.Protocol.Events);
        Assert.Equal(0, f.Lifecycle.Acquisitions);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.Local, state.MigrationState);
        Assert.Equal(TelegramEndpointType.Local, state.EffectiveEndpoint);
        Assert.Equal(2, state.Generation);
        Assert.True(state.LogoutAcknowledgedAtUtc.HasValue);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), state.CloudReuseEligibleAtUtc);
        Assert.Equal(new[] { "root", "identity:Cloud", "logout:Cloud", "identity:Local" }, f.Protocol.Events);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>The confirmation callback must finish while its own handler lease remains admitted.</summary>
    [Fact]
    public async Task Queued_callback_never_self_drains_and_worker_waits_for_the_original_handler()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.True(f.Gate.TryAcquireExecution("owned-a", out var handler));
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        var work = f.Coordinator.RunPendingOperationsAsync(default);
        await f.Lifecycle.Stopped.Task;
        Assert.False(work.IsCompleted);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.False(f.Gate.TryAcquireExecution("owned-a", out _));
        handler.Dispose();
        await work;
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
    }

    /// <summary>Cloud-to-Local checks cannot create a Local session when the root or source identity is unavailable.</summary>
    /// <param name="failure">A closed safe prelogout root failure.</param>
    [Theory]
    [InlineData(TelegramEndpointFailure.ConnectionRefused)]
    [InlineData(TelegramEndpointFailure.Timeout)]
    [InlineData(TelegramEndpointFailure.InvalidResponse)]
    public async Task Unreachable_local_before_cloud_logout_preserves_cloud_without_local_getme(TelegramEndpointFailure failure)
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.RootFailure = failure;
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.Cloud, state.MigrationState);
        Assert.Equal(TelegramEndpointType.Local, state.DesiredEndpoint);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>A mismatched Cloud source identity is persisted with its exact prelogout stage and never authorizes logout or destination creation.</summary>
    /// <returns>A task after exact source-stage failure is saved with no irreversible call.</returns>
    [Fact]
    public async Task Wrong_source_identity_is_refused_before_logout()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.CloudFailure = TelegramEndpointFailure.IdentityMismatch;
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal("cloud_source_identity_identity_mismatch", (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
    }

    /// <summary>Missing trusted file mapping blocks Local intent before any probe or irreversible logout.</summary>
    /// <returns>A task after precise mapping_missing admission refusal without any protocol request.</returns>
    [Fact]
    public async Task Missing_mapping_refuses_local_migration_without_transport_calls()
    {
        using var f = new Fixture(options: new TelegramEndpointRoutingOptions());
        await f.InitializeAsync();
        Assert.Equal("local_file_mapping_missing", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Local migration cannot remove the last independent private owned Cloud administration path.</summary>
    /// <param name="condition">Which synthetic alternative-host prerequisite is absent.</param>
    /// <returns>A task completing after refusal without intent, lifecycle drain or logout.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("tenant")]
    [InlineData("assistant")]
    [InlineData("local")]
    [InlineData("fenced")]
    public async Task Local_migration_retains_independent_owned_cloud_control(string condition)
    {
        using var f = new Fixture(includeCloudControl: condition != "missing");
        await f.InitializeAsync();
        if (condition is "disabled" or "tenant" or "assistant")
            f.Registry.Upsert(new BotInstance
            {
                Id = "operator-control", Token = "345:abcdefghijklmnopqrstuvwxyz0123456789",
                Enabled = condition != "disabled",
                Type = condition == "tenant" ? BotInstanceTypes.Tenant :
                    condition == "assistant" ? BotInstanceTypes.SalesAssistant : BotInstanceTypes.Owned
            });
        if (condition == "local")
        {
            var state = new TelegramEndpointState
            {
                BotId = "operator-control", TelegramBotId = 345, DesiredEndpoint = TelegramEndpointType.Local,
                EffectiveEndpoint = TelegramEndpointType.Local, MigrationState = TelegramEndpointMigrationState.Local
            };
            f.Store.Seed(state);
            f.Gate.Publish(state);
        }
        if (condition == "fenced") f.Gate.Fence("operator-control", 345);
        Assert.Equal("control_path_missing", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Null((await f.StatusAsync()).OperationId);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(0, f.Lifecycle.Acquisitions);
        Assert.Equal(0, f.Protocol.LogoutCalls);
    }

    /// <summary>Competing intents cannot migrate both remaining owned Cloud controls to the shared Local server.</summary>
    /// <returns>A task after one Local intent is accepted and the independent control stays Cloud.</returns>
    [Fact]
    public async Task Competing_local_intents_preserve_the_remaining_cloud_control()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Equal("control_path_missing", await f.Coordinator.RequestMigrationAsync(
            "operator-control", TelegramEndpointType.Local, 7, 0, 345, default));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        var control = await f.Coordinator.GetStatusAsync("operator-control", default);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, control.MigrationState);
        Assert.Null(control.OperationId);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Losing the independent control after confirmation restores the verified Cloud source rather than logging it out.</summary>
    /// <returns>A task completing after safe source restoration without any irreversible call.</returns>
    [Fact]
    public async Task Lost_independent_control_before_logout_restores_cloud_source()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        f.Protocol.IdentityVerified = endpoint =>
        {
            if (endpoint == TelegramEndpointType.Cloud) f.Gate.Fence("operator-control", 345);
        };
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, (await f.StatusAsync()).MigrationState);
        Assert.Equal("operator_control_missing", (await f.StatusAsync()).LastFailureCategory);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>Opt-in failback must not move the final owned Cloud administration identity back to Local.</summary>
    /// <returns>A task after repeated healthy Local observations leave the real effective Cloud route unchanged.</returns>
    [Fact]
    public async Task Automatic_failback_retains_the_last_cloud_control()
    {
        using var f = new Fixture(includeCloudControl: false, options: new TelegramEndpointRoutingOptions
        { AutomaticFailback = true, LocalFileServerRoot = "/data/local", LocalFileHostRoot = Path.GetTempPath() });
        f.Store.Mutate(state =>
        { state.DesiredEndpoint = TelegramEndpointType.Local; state.MigrationState = TelegramEndpointMigrationState.CloudRecovered; });
        await f.InitializeAsync();
        for (var index = 0; index < 4; index++) await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudRecovered, (await f.StatusAsync()).MigrationState);
        Assert.Equal("operator_control_missing", (await f.StatusAsync()).LastFailureCategory);
        Assert.Null((await f.StatusAsync()).OperationId);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>The existing mapped directory must remain ready when the worker crosses the logout boundary.</summary>
    /// <returns>A task after precise host_missing failure preserves source service without logout.</returns>
    [Fact]
    public async Task Removed_mapping_after_confirmation_never_authorizes_logout()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        Directory.Delete(f.HostRoot);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.Equal("local_file_host_missing", (await f.StatusAsync()).LastFailureCategory);
    }

    /// <summary>Ambiguous mutation delivery stays fenced and never creates the destination session or replays logout.</summary>
    [Fact]
    public async Task Ambiguous_cloud_logout_stays_explicit_and_is_never_replayed_even_after_restart()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.LogoutResult = new(false, true, TelegramEndpointFailure.LogoutUncertain);
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.CloudLogoutUncertain, state.MigrationState);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal("unsafe", await f.RequestAsync(TelegramEndpointType.Cloud));
        f.RestartCoordinator();
        await f.InitializeAsync();
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
    }

    /// <summary>A timed-out dispatched logout is uncertain, even if the noncooperating transport never returns.</summary>
    [Fact]
    public async Task Dispatched_logout_timeout_is_durable_uncertainty_and_never_replayed()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.LogoutOverride = _ =>
            new TaskCompletionSource<TelegramEndpointLogoutResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await f.RequestAsync(TelegramEndpointType.Local);
        var work = f.Coordinator.RunPendingOperationsAsync(default);
        await f.Protocol.LogoutStarted.Task;
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        await work;
        Assert.Equal(TelegramEndpointMigrationState.CloudLogoutUncertain, (await f.StatusAsync()).MigrationState);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
    }

    /// <summary>A pending crash marker is uncertainty even if no fake request was observed before the crash.</summary>
    /// <param name="endpoint">The source with an unacknowledged durable logout boundary.</param>
    [Theory]
    [InlineData(TelegramEndpointType.Cloud)]
    [InlineData(TelegramEndpointType.Local)]
    public async Task Startup_pending_logout_becomes_uncertain_without_replay(TelegramEndpointType endpoint)
    {
        using var f = new Fixture(endpoint);
        f.Store.Mutate(state =>
        {
            state.MigrationState = endpoint == TelegramEndpointType.Cloud
                ? TelegramEndpointMigrationState.CloudLogoutPending : TelegramEndpointMigrationState.LocalLogoutPending;
            state.LogoutAttemptedAtUtc = f.Clock.GetUtcNow().UtcDateTime;
            state.LogoutEndpoint = endpoint;
            state.OperationId = Guid.NewGuid().ToString("N");
        });
        await f.InitializeAsync();
        var state = await f.StatusAsync();
        Assert.Equal(endpoint == TelegramEndpointType.Cloud ? TelegramEndpointMigrationState.CloudLogoutUncertain
            : TelegramEndpointMigrationState.LocalLogoutUncertain, state.MigrationState);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>A committed acknowledgement lost to CAS conflict cannot authorize destination getMe or mutation replay.</summary>
    [Fact]
    public async Task Acknowledgement_save_conflict_keeps_pending_marker_and_next_scan_marks_uncertain()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Store.RejectReason = "logout_acknowledged";
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudLogoutPending, (await f.StatusAsync()).MigrationState);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        f.Store.RejectReason = null;
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudLogoutUncertain, (await f.StatusAsync()).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>A CAS refusal before the durable boundary prevents even the first mutation attempt.</summary>
    [Fact]
    public async Task Logout_marker_cas_conflict_never_sends_logout()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Store.RejectReason = "cloud_logout_intent";
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
    }

    /// <summary>An explicit Cloud logout refusal retains its endpoint boundary and may safely restart only the known source.</summary>
    /// <returns>A task after source restoration without destination probing or replay of the same cleanup.</returns>
    [Fact]
    public async Task Definitive_logout_refusal_restores_cloud_and_does_not_retry_same_operation()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.LogoutResult = new(false, false, TelegramEndpointFailure.LogoutRefused);
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.Cloud, state.MigrationState);
        Assert.Equal("cloud_logout_refused", state.LastFailureCategory);
        Assert.Null(state.LogoutAttemptedAtUtc);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>Destination getMe alone cannot activate a route when strict receiving readiness fails; precise Local readiness diagnostics remain fenced.</summary>
    /// <returns>A task after safe receiver retry succeeds without a second logout.</returns>
    [Fact]
    public async Task Failed_receiving_readiness_stays_fenced_and_retries_reads_without_second_logout()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Lifecycle.Ready = false;
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.SwitchingToLocal, state.MigrationState);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal("local_destination_receiver_not_ready", state.LastFailureCategory);
        f.Lifecycle.Ready = true;
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
    }

    /// <summary>A receiver registered before a thrown startup or final commit must be joined before safe retry.</summary>
    /// <param name="throwAtStart">True injects a post-registration lifecycle exception; false injects final save failure.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Activation_exception_stops_partial_receiver_and_leaves_a_fenced_safe_retry(bool throwAtStart)
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        if (throwAtStart) f.Lifecycle.ThrowAfterStart = true;
        else f.Store.ThrowReason = "migration_succeeded";
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Lifecycle.ActiveReceivers);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(TelegramEndpointMigrationState.SwitchingToLocal, (await f.StatusAsync()).MigrationState);
        f.Lifecycle.ThrowAfterStart = false;
        f.Store.ThrowReason = null;
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
    }

    /// <summary>The Local identity mismatch cannot be treated as successful receiving or an automatic Cloud override.</summary>
    [Fact]
    public async Task Destination_identity_mismatch_requires_manual_intervention_after_acknowledged_logout()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.LocalFailure = TelegramEndpointFailure.IdentityMismatch;
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.ManualInterventionRequired, (await f.StatusAsync()).MigrationState);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(0, f.Lifecycle.Starts);
        Assert.Equal("unsafe", await f.RequestAsync(TelegramEndpointType.Cloud));
    }

    /// <summary>Local cleanup acknowledgement requires a fresh conservative ten-minute Cloud reuse wait.</summary>
    [Fact]
    public async Task Local_to_cloud_waits_full_cooldown_without_early_cloud_probe_or_dual_polling()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        await f.RequestAsync(TelegramEndpointType.Cloud);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudWait, (await f.StatusAsync()).MigrationState);
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
        f.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
        f.Clock.Advance(TimeSpan.FromTicks(1));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, (await f.StatusAsync()).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
    }

    /// <summary>An unreachable Local session cannot be bypassed with Cloud getMe; exhausting safe retries preserves its exact root failure stage.</summary>
    /// <returns>A task after bounded reads end in explicit intervention with no logout or destination activation.</returns>
    [Fact]
    public async Task Unsafe_local_cleanup_has_bounded_safe_reads_and_visible_manual_review()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        await f.RequestAsync(TelegramEndpointType.Cloud);
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await f.Coordinator.RunPendingOperationsAsync(default);
            var state = await f.StatusAsync();
            if (attempt < 12)
            {
                Assert.Equal(TelegramEndpointMigrationState.FallbackPending, state.MigrationState);
                Assert.InRange((state.NextAttemptAtUtc!.Value - f.Clock.GetUtcNow().UtcDateTime).TotalSeconds, 15, 300);
                f.Clock.Advance(state.NextAttemptAtUtc.Value - f.Clock.GetUtcNow().UtcDateTime);
            }
        }
        Assert.Equal(TelegramEndpointMigrationState.ManualInterventionRequired, (await f.StatusAsync()).MigrationState);
        Assert.Equal("local_root_connection_refused", (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
        Assert.Contains("manual_intervention", f.Store.AlertCategories);
    }

    /// <summary>Three transport failures declare one durable incident; the first two do not fence normal Local handlers.</summary>
    [Fact]
    public async Task Sustained_outage_uses_three_failures_and_deduplicates_repeated_health_alerts()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        for (var i = 0; i < 2; i++)
        {
            await f.Coordinator.RefreshHealthAsync("owned-a", default);
            Assert.True(f.Gate.IsAvailable("owned-a", 123));
        }
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        var incident = (await f.StatusAsync()).OutageId;
        Assert.NotNull(incident);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        for (var i = 0; i < 5; i++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        Assert.Equal(incident, (await f.StatusAsync()).OutageId);
        Assert.Equal(1, f.Store.Alerts.Count(x => x.EndsWith(":local_outage", StringComparison.Ordinal)));
        Assert.Equal(0, f.Protocol.LogoutCalls);
    }

    /// <summary>Telegram throttling and upstream errors never accumulate local-server outage counters.</summary>
    /// <param name="failure">A verified local transport's Telegram-level transient rejection.</param>
    [Theory]
    [InlineData(TelegramEndpointFailure.RateLimited)]
    [InlineData(TelegramEndpointFailure.TelegramUpstream)]
    public async Task Rate_limit_and_telegram_upstream_are_not_local_outages(TelegramEndpointFailure failure)
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.Protocol.LocalFailure = failure;
        for (var i = 0; i < 8; i++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        Assert.Equal(0, (await f.StatusAsync()).ConsecutiveFailures);
        Assert.Null((await f.StatusAsync()).OutageId);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(0, f.Protocol.LogoutCalls);
    }

    /// <summary>A transient connection failure followed by healthy identity never reaches sustained outage.</summary>
    [Fact]
    public async Task Brief_transport_transient_recovers_after_two_successes_without_logout()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        f.Protocol.RootFailure = TelegramEndpointFailure.None;
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        Assert.Equal(TelegramEndpointMigrationState.LocalDegraded, (await f.StatusAsync()).MigrationState);
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.Empty(f.Store.Alerts);
    }

    /// <summary>Recovery before any cleanup resumes the same Local session only after two successes in the worker.</summary>
    /// <param name="automaticFailover">Whether the original incident queued automatic fallback.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_local_before_logout_recovers_after_two_successes_without_cloud_fallback(bool automaticFailover)
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        f.Store.Mutate(x => x.AutoFailoverEnabled = automaticFailover);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        for (var i = 0; i < 3; i++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        f.Protocol.RootFailure = TelegramEndpointFailure.None;
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
        Assert.Contains("local_recovered", f.Store.AlertCategories);
    }

    /// <summary>Automatic Cloud recovery preserves desired Local and defaults to manual failback.</summary>
    [Fact]
    public async Task Automatic_fallback_is_truthful_and_does_not_automatically_fail_back_by_default()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        for (var i = 0; i < 3; i++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        f.Protocol.RootFailure = TelegramEndpointFailure.None;
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudWait, (await f.StatusAsync()).MigrationState);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.CloudRecovered, state.MigrationState);
        Assert.Equal(TelegramEndpointType.Local, state.DesiredEndpoint);
        Assert.Equal(TelegramEndpointType.Cloud, state.EffectiveEndpoint);
        for (var i = 0; i < 4; i++) await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(TelegramEndpointMigrationState.CloudRecovered, (await f.StatusAsync()).MigrationState);
    }

    /// <summary>Manual-failback Cloud recovery notifies two tokenless Local successes once without creating a Local session.</summary>
    [Fact]
    public async Task Manual_failback_reports_shared_local_recovery_once_after_two_root_successes_and_deduplicates_restart()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        for (var index = 0; index < 3; index++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        f.Protocol.RootFailure = TelegramEndpointFailure.None;
        await f.Coordinator.RunPendingOperationsAsync(default);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await f.Coordinator.RunPendingOperationsAsync(default);
        var recovered = await f.StatusAsync();
        var incident = recovered.OutageId;
        f.Protocol.Events.Clear();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        await f.Coordinator.RunHealthCycleAsync(default);
        f.Protocol.RootFailure = TelegramEndpointFailure.None;
        await f.Coordinator.RunHealthCycleAsync(default);
        Assert.DoesNotContain("local_recovered", f.Store.AlertCategories);
        await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Contains("local_recovered", f.Store.AlertCategories);
        var afterAlert = await f.StatusAsync();
        Assert.Equal(incident, afterAlert.OutageId);
        Assert.Equal(recovered.ControlRevision, afterAlert.ControlRevision);
        Assert.Equal(TelegramEndpointMigrationState.CloudRecovered, afterAlert.MigrationState);
        Assert.Equal(TelegramEndpointType.Cloud, afterAlert.EffectiveEndpoint);
        Assert.Equal(TelegramEndpointType.Local, afterAlert.DesiredEndpoint);
        for (var index = 0; index < 8; index++) await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(afterAlert.Revision, (await f.StatusAsync()).Revision);
        Assert.Equal(1, f.Store.Alerts.Count(x => x.EndsWith(":local_recovered", StringComparison.Ordinal)));
        Assert.All(f.Protocol.Events, entry => Assert.Equal("root", entry));
        Assert.Equal(1, f.Protocol.LogoutCalls);
        f.RestartCoordinator();
        await f.InitializeAsync();
        await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(afterAlert.Revision, (await f.StatusAsync()).Revision);
        await f.Coordinator.RunHealthCycleAsync(default);
        var afterRestartReceipt = await f.StatusAsync();
        for (var index = 0; index < 4; index++) await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(afterRestartReceipt.Revision, (await f.StatusAsync()).Revision);
        Assert.Equal(1, f.Store.Alerts.Count(x => x.EndsWith(":local_recovered", StringComparison.Ordinal)));
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Automatic failback is possible only when explicitly enabled and still obeys Cloud logout ordering.</summary>
    [Fact]
    public async Task Explicit_automatic_failback_requires_two_tokenless_successes_then_normal_cloud_logout()
    {
        using var f = new Fixture(TelegramEndpointType.Cloud, options: new TelegramEndpointRoutingOptions
        {
            AutomaticFailback = true, LocalFileServerRoot = "/data/local", LocalFileHostRoot = Path.GetTempPath()
        });
        f.Store.Mutate(x => { x.DesiredEndpoint = TelegramEndpointType.Local; x.MigrationState = TelegramEndpointMigrationState.CloudRecovered; });
        await f.InitializeAsync();
        await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudRecovered, (await f.StatusAsync()).MigrationState);
        await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CheckingLocal, (await f.StatusAsync()).MigrationState);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Restart hydrates saved Local rather than allowing an optimistic Cloud default receiver.</summary>
    [Fact]
    public async Task Startup_preserves_saved_local_route_and_cooldown_recovery_proof()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        Assert.Equal(TelegramEndpointType.Local, f.Gate.GetRoute("owned-a", 123).Endpoint);
        await f.RequestAsync(TelegramEndpointType.Cloud);
        await f.Coordinator.RunPendingOperationsAsync(default);
        f.RestartCoordinator();
        await f.InitializeAsync();
        Assert.Equal(TelegramEndpointType.Local, f.Gate.GetRoute("owned-a", 123).Endpoint);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointType.Cloud, f.Gate.GetRoute("owned-a", 123).Endpoint);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Concurrent confirmations for one revision produce one accepted operation and one non-replaying loser.</summary>
    [Fact]
    public async Task Concurrent_migrations_and_worker_scans_never_duplicate_logout_or_polling()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var outcomes = await Task.WhenAll(
            f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Local, 7, 0, 123, default),
            f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Local, 7, 0, 123, default));
        Assert.Single(outcomes, x => x == "accepted");
        Assert.Contains(outcomes, x => x is "stale" or "busy");
        await Task.WhenAll(f.Coordinator.RunPendingOperationsAsync(default), f.Coordinator.RunPendingOperationsAsync(default));
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
    }

    /// <summary>Current global authorization, frozen identity and control revision are enforced independently of UI.</summary>
    /// <returns>A task completing after denied actors, replaced identities, stale controls and revoked live authorization cause no migration.</returns>
    [Fact]
    public async Task Commands_recheck_admin_identity_and_revision()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.Equal("denied", await f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Local, 9, 0, 123, default));
        Assert.Equal("stale", await f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Local, 7, 0, 999, default));
        Assert.Equal("stale", await f.Coordinator.SetAutoFailoverAsync("owned-a", false, 7, 5, 123, default));
        f.Configuration["AdminsUserIds:0"] = "9";
        Assert.Equal("denied", await f.RequestAsync(TelegramEndpointType.Local));
    }

    /// <summary>Health-only CAS writes must not stale an operator's control confirmation.</summary>
    [Fact]
    public async Task Health_metadata_does_not_change_control_revision()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        var before = await f.StatusAsync();
        f.Protocol.LocalFailure = TelegramEndpointFailure.RateLimited;
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        var after = await f.StatusAsync();
        Assert.True(after.Revision > before.Revision);
        Assert.Equal(before.ControlRevision, after.ControlRevision);
        Assert.Equal("accepted", await f.Coordinator.SetAutoFailoverAsync("owned-a", false, 7, before.ControlRevision, 123, default));
    }

    /// <summary>A noncooperating fake root read is bounded by the injected three-second timer, without real sleeping.</summary>
    [Fact]
    public async Task Safe_probe_timeout_is_deterministic_and_typed()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Protocol.RootOverride = _ =>
        {
            started.TrySetResult();
            return new TaskCompletionSource<TelegramEndpointProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        };
        var health = f.Coordinator.RefreshHealthAsync("owned-a", default);
        await started.Task;
        f.Clock.Advance(TimeSpan.FromSeconds(4));
        await health;
        Assert.Equal("timeout", (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(1, (await f.StatusAsync()).ConsecutiveFailures);
        Assert.Equal(0, f.Protocol.LogoutCalls);
    }

    /// <summary>A blocked migration drain cannot starve tokenless root checks or another Local bot's periodic health.</summary>
    [Fact]
    public async Task Hosted_health_keeps_running_while_a_migration_waits_on_an_admitted_handler()
    {
        using var f = new Fixture(includeSecondBot: true);
        await f.InitializeAsync();
        Assert.True(f.Gate.TryAcquireExecution("owned-a", out var handler));
        await f.RequestAsync(TelegramEndpointType.Local);
        using var worker = new TelegramEndpointCoordinatorWorker(f.Coordinator, f.Options,
            NullLogger<TelegramEndpointCoordinatorWorker>.Instance, f.Clock);
        await worker.StartAsync(default);
        try
        {
            await f.Lifecycle.Stopped.Task;
            await f.Clock.MonitorDelayScheduled.Task;
            Assert.Equal(0, f.Protocol.LogoutCalls);
            f.Clock.Advance(TimeSpan.FromSeconds(16));
            await f.Store.SecondBotHealthSaved.Task;
            var other = await f.Coordinator.GetStatusAsync("owned-b", default);
            Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, other.LastHealthCheckAtUtc);
            Assert.Equal(TelegramEndpointMigrationState.Local, other.MigrationState);
            Assert.True(f.Protocol.Events.Count(x => x == "root") >= 3);
            Assert.Equal(0, f.Protocol.LogoutCalls);
        }
        finally
        {
            await worker.StopAsync(default);
            handler.Dispose();
        }
    }

    /// <summary>Enabled and disabled aliases share one Telegram session; disabled owned bots still permit background delivery.</summary>
    /// <param name="enabled">Whether the synthetic duplicate currently receives updates; session conflict exists in either case.</param>
    /// <returns>A task completing after migration refusal without source fencing or logout.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_identity_is_refused_without_fencing_either_source_or_dispatching_logout(bool enabled)
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.AddAlias(enabled);
        Assert.Equal("aliased", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Equal("aliased", await f.Coordinator.SetAutoFailoverAsync("owned-a", false, 7, 0, 123, default));
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(enabled, f.Gate.IsAvailable("owned-alias", 123));
        Assert.Empty(f.Protocol.Events);
        Assert.Equal(0, f.Lifecycle.Acquisitions);
    }

    /// <summary>An alias added after source identity verification is rechecked at the irreversible boundary.</summary>
    [Fact]
    public async Task Midflight_identity_alias_restores_original_source_without_logout_or_affecting_alias_admission()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.RequestAsync(TelegramEndpointType.Local);
        f.Protocol.IdentityVerified = endpoint => { if (endpoint == TelegramEndpointType.Cloud) f.AddAlias(); };
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.Equal("identity_alias_conflict", (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, (await f.StatusAsync()).MigrationState);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.True(f.Gate.IsAvailable("owned-alias", 123));
    }

    /// <summary>Automatic outage fallback never acquires cleanup authority across a current duplicate identity, including disabled aliases.</summary>
    /// <param name="enabled">Whether the duplicate has receiver admission; even disabled aliases retain read/send capability.</param>
    /// <returns>A task completing after an explicit fenced Local outage without logout.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_identity_blocks_automatic_fallback_intent_but_preserves_explicit_local_outage(bool enabled)
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        f.AddAlias(enabled);
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        for (var index = 0; index < 3; index++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        var state = await f.StatusAsync();
        Assert.Equal(TelegramEndpointMigrationState.LocalUnavailable, state.MigrationState);
        Assert.Equal("identity_alias_conflict", state.LastFailureCategory);
        Assert.Null(state.OperationId);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.False(f.Gate.IsAvailable("owned-alias", 123));
    }

    /// <summary>Opt-in automatic failback still cannot migrate a recovered Cloud session with an enabled alias.</summary>
    [Fact]
    public async Task Duplicate_identity_blocks_explicit_opt_in_automatic_failback()
    {
        using var f = new Fixture(options: new TelegramEndpointRoutingOptions
        {
            AutomaticFailback = true, LocalFileServerRoot = "/data/local", LocalFileHostRoot = Path.GetTempPath()
        });
        f.Store.Mutate(x => { x.DesiredEndpoint = TelegramEndpointType.Local; x.MigrationState = TelegramEndpointMigrationState.CloudRecovered; });
        await f.InitializeAsync();
        f.AddAlias();
        for (var index = 0; index < 4; index++) await f.Coordinator.RunHealthCycleAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudRecovered, (await f.StatusAsync()).MigrationState);
        Assert.Null((await f.StatusAsync()).OperationId);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.True(f.Gate.IsAvailable("owned-alias", 123));
    }

    /// <summary>A renamed internal key with unresolved historical Local authority is not a new safe Cloud session.</summary>
    [Fact]
    public async Task Saved_alias_authority_conflict_cannot_be_overridden_when_no_current_alias_is_enabled()
    {
        using var f = new Fixture();
        f.Store.Mutate(x =>
        {
            x.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
            x.LastFailureCategory = "identity_alias_conflict";
        });
        await f.InitializeAsync();
        Assert.Equal("aliased", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Equal("aliased", await f.Coordinator.SetAutoFailoverAsync("owned-a", false, 7, 0, 123, default));
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>The real telemetry writer receives exactly one terminal monotonic duration and no repeats in health/intermediate events.</summary>
    [Fact]
    public async Task Migration_completion_duration_is_emitted_once_and_never_repeated_by_future_health()
    {
        using var f = new Fixture(includeTelemetry: true);
        var telemetry = f.Telemetry!;
        await telemetry.StartAsync(default);
        try
        {
            await f.InitializeAsync();
            f.Protocol.IdentityVerified = endpoint =>
            {
                if (endpoint == TelegramEndpointType.Cloud) f.Clock.Advance(TimeSpan.FromSeconds(1));
            };
            await f.RequestAsync(TelegramEndpointType.Local);
            await f.Coordinator.RunPendingOperationsAsync(default);
            for (var index = 0; index < 4; index++)
            {
                f.Clock.Advance(TimeSpan.FromSeconds(1));
                await f.Coordinator.RefreshHealthAsync("owned-a", default);
            }
        }
        finally { await telemetry.StopAsync(default); }
        var observations = Directory.GetFiles(telemetry.StorageDirectory, "*.jsonl").SelectMany(File.ReadLines)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        var completed = observations.Where(row => row.TryGetProperty("failoverDurationMs", out var duration) &&
            duration.ValueKind == JsonValueKind.Number).ToArray();
        var terminal = Assert.Single(completed);
        Assert.Equal(1000d, terminal.GetProperty("failoverDurationMs").GetDouble());
        Assert.Equal("telegram_endpoint_migration", terminal.GetProperty("eventType").GetString());
        Assert.Equal("Local", terminal.GetProperty("migrationState").GetString());
        Assert.Equal(4, observations.Count(row => row.GetProperty("eventType").GetString() == "telegram_endpoint_health"));
    }

    /// <summary>Classification never treats throttling/upstream rejection as a local process refusal.</summary>
    [Fact]
    public void Typed_failure_classification_distinguishes_refusal_timeout_rate_limit_and_upstream()
    {
        Assert.Equal(TelegramEndpointFailure.ConnectionRefused, TelegramEndpointHealthPolicy.Classify(
            new HttpRequestException("not retained", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal(TelegramEndpointFailure.Timeout, TelegramEndpointHealthPolicy.Classify(new TimeoutException("not retained")));
        Assert.Equal(TelegramEndpointFailure.RateLimited, TelegramEndpointHealthPolicy.Classify(new ApiRequestException("not retained", 429)));
        Assert.Equal(TelegramEndpointFailure.TelegramUpstream, TelegramEndpointHealthPolicy.Classify(new ApiRequestException("not retained", 502)));
        Assert.False(TelegramEndpointHealthPolicy.IsServerOutage(TelegramEndpointFailure.RateLimited));
        Assert.False(TelegramEndpointHealthPolicy.IsServerOutage(TelegramEndpointFailure.TelegramUpstream));
        Assert.Equal(TimeSpan.FromSeconds(300), TelegramEndpointHealthPolicy.RetryDelay(int.MaxValue, 300));
    }

    /// <summary>The tokenless root verifies official JSON rather than accepting any HTTP 404 or following a token path.</summary>
    /// <param name="body">Controlled tokenless root response bytes.</param>
    /// <param name="expected">Whether the official 404 envelope is present.</param>
    [Theory]
    [InlineData("{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}", true)]
    [InlineData("<html>not found</html>", false)]
    [InlineData("{\"ok\":true,\"error_code\":404}", false)]
    public async Task Root_reachability_requires_bounded_official_envelope_without_token(string body, bool expected)
    {
        using var f = new Fixture();
        using var handler = new RootHandler(body);
        using var http = new HttpClient(handler);
        using var protocol = new TelegramEndpointProtocol(f.Clients, f.Options, http);
        Assert.Equal(expected, (await protocol.ProbeLocalServerAsync(default)).Success);
        Assert.Equal("/", handler.Path);
        Assert.Equal("", handler.Query);
    }

    /// <summary>Cloud refresh verifies only an already-active eligible Cloud bot; pending/cooldown phases cannot be probed.</summary>
    [Fact]
    public async Task Explicit_cloud_refresh_records_identity_health_but_cloud_wait_does_not_probe_cloud()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.Coordinator.RefreshHealthAsync("owned-a", default);
        var state = await f.StatusAsync();
        Assert.NotNull(state.LastHealthCheckAtUtc);
        Assert.NotNull(state.LastSuccessfulHealthAtUtc);
        Assert.Contains("identity:Cloud", f.Protocol.Events);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        using var local = new Fixture(TelegramEndpointType.Local);
        await local.InitializeAsync();
        await local.RequestAsync(TelegramEndpointType.Cloud);
        await local.Coordinator.RunPendingOperationsAsync(default);
        await local.Coordinator.RefreshHealthAsync("owned-a", default);
        Assert.DoesNotContain("identity:Cloud", local.Protocol.Events);
    }

    /// <summary>Even when fallback is disabled, sustained outage work promptly cancels polling without cleanup or Cloud activation.</summary>
    [Fact]
    public async Task Outage_worker_joins_receiver_before_unreachable_cleanup_and_when_failover_disabled()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        f.Store.Mutate(x => x.AutoFailoverEnabled = false);
        await f.InitializeAsync();
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        for (var i = 0; i < 3; i++) await f.Coordinator.RefreshHealthAsync("owned-a", default);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(0, f.Lifecycle.ActiveReceivers);
        Assert.Equal(TelegramEndpointMigrationState.LocalUnavailable, (await f.StatusAsync()).MigrationState);
        Assert.Null((await f.StatusAsync()).NextAttemptAtUtc);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
    }

    /// <summary>Owns only isolated temporary mapping metadata and fake transport dependencies.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Isolated host mapping directory; never a production volume.</summary>
        internal string HostRoot { get; } = Path.Combine(Path.GetTempPath(), "endpoint-tests-" + Guid.NewGuid().ToString("N"));
        /// <summary>Live in-memory global administrator and dummy-bot configuration.</summary>
        internal IConfigurationRoot Configuration { get; }
        /// <summary>Current runtime identities, shared with the real gate.</summary>
        internal BotRegistry Registry { get; }
        /// <summary>Provider is unstarted; fake protocol prevents bot SDK network access.</summary>
        internal BotClientProvider Clients { get; }
        /// <summary>Detached fake durable CAS store retained across coordinator restarts.</summary>
        internal MemoryStore Store { get; } = new();
        /// <summary>Controlled UTC and deadline timers, with no real waiting.</summary>
        internal ControlledClock Clock { get; } = new();
        /// <summary>Fake protocol enforces durable logout ordering before acknowledging.</summary>
        internal FakeProtocol Protocol { get; }
        /// <summary>Serialized receiver lifecycle with observable no-dual-poll invariant.</summary>
        internal FakeLifecycle Lifecycle { get; } = new();
        /// <summary>Valid trusted options; no unsafe endpoint URL substitution.</summary>
        internal TelegramEndpointRoutingOptions Options { get; }
        /// <summary>Current generation gate, replaced on simulated process restart.</summary>
        internal TelegramEndpointRuntimeGate Gate { get; private set; }
        /// <summary>Current controller, replaced while retaining durable fake state.</summary>
        internal TelegramEndpointCoordinator Coordinator { get; private set; }
        /// <summary>Lazy lifecycle DI container, owned by this fixture.</summary>
        private readonly ServiceProvider _services;
        /// <summary>Optional real telemetry writer over only the fixture's scratch directory.</summary>
        internal LatencyTelemetryService? Telemetry { get; }

        /// <summary>Creates one isolated dummy BotFather identity and a safe host mapping.</summary>
        /// <param name="endpoint">Initial durable endpoint, not an actual network session.</param>
        /// <param name="options">Optional explicit option variation; mapping omissions remain omitted.</param>
        /// <param name="includeSecondBot">Adds an independent already-local identity solely for hosted-health isolation.</param>
        /// <param name="includeTelemetry">Creates a real unstarted scratch-only JSONL writer for terminal-metadata coverage.</param>
        /// <param name="includeCloudControl">Retains an independent enabled owned Cloud control by default; false tests last-control refusal.</param>
        /// <remarks>No real receivers or network start; the extra Cloud identity represents the required alternate private administration path.</remarks>
        internal Fixture(TelegramEndpointType endpoint = TelegramEndpointType.Cloud, TelegramEndpointRoutingOptions? options = null,
            bool includeSecondBot = false, bool includeTelemetry = false, bool includeCloudControl = true)
        {
            Directory.CreateDirectory(HostRoot);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminsUserIds:0"] = "7", ["Bots:0:Id"] = "owned-a", ["Bots:0:Username"] = "dummy_a_bot",
                ["Bots:0:Token"] = "123:abcdefghijklmnopqrstuvwxyz0123456789", ["Bots:0:Type"] = BotInstanceTypes.Owned, ["Bots:0:Enabled"] = "true"
            }).Build();
            if (includeSecondBot)
            {
                Configuration["Bots:1:Id"] = "owned-b";
                Configuration["Bots:1:Username"] = "dummy_b_bot";
                Configuration["Bots:1:Token"] = "234:abcdefghijklmnopqrstuvwxyz0123456789";
                Configuration["Bots:1:Type"] = BotInstanceTypes.Owned;
                Configuration["Bots:1:Enabled"] = "true";
                Store.Seed(new TelegramEndpointState
                {
                    BotId = "owned-b", TelegramBotId = 234, DesiredEndpoint = TelegramEndpointType.Local,
                    EffectiveEndpoint = TelegramEndpointType.Local, MigrationState = TelegramEndpointMigrationState.Local
                });
            }
            Registry = new BotRegistry(Configuration);
            if (includeCloudControl)
            {
                Registry.Upsert(new BotInstance
                {
                    Id = "operator-control", Username = "dummy_control_bot",
                    Token = "345:abcdefghijklmnopqrstuvwxyz0123456789", Type = BotInstanceTypes.Owned, Enabled = true
                });
                Store.Seed(new TelegramEndpointState { BotId = "operator-control", TelegramBotId = 345 });
            }
            if (includeTelemetry)
                Telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions { ChannelCapacity = 256 }, HostRoot,
                    NullLogger<LatencyTelemetryService>.Instance, Clock);
            Clients = new BotClientProvider(Registry);
            Options = options ?? new TelegramEndpointRoutingOptions { LocalFileServerRoot = "/data/local", LocalFileHostRoot = HostRoot };
            Store.Seed(new TelegramEndpointState
            {
                BotId = "owned-a", TelegramBotId = 123, DesiredEndpoint = endpoint, EffectiveEndpoint = endpoint,
                MigrationState = endpoint == TelegramEndpointType.Local ? TelegramEndpointMigrationState.Local : TelegramEndpointMigrationState.Cloud
            });
            Gate = new TelegramEndpointRuntimeGate(Registry);
            Protocol = new FakeProtocol(Store, () => Gate, Lifecycle);
            _services = new ServiceCollection().AddSingleton<ITelegramEndpointReceiverLifecycle>(Lifecycle).BuildServiceProvider();
            Coordinator = CreateCoordinator();
        }

        /// <summary>Creates a real controller over fake durable/network/lifecycle dependencies.</summary>
        /// <returns>A controller with no real bot SDK request path.</returns>
        private TelegramEndpointCoordinator CreateCoordinator() => new(Store, Gate, Registry, Clients, Configuration,
            Options, _services, NullLogger<TelegramEndpointCoordinator>.Instance, telemetry: Telemetry, timeProvider: Clock, protocol: Protocol);
        /// <summary>Hydrates saved fake state exactly as production prehost initialization does.</summary>
        /// <returns>The completed route hydration task.</returns>
        internal Task InitializeAsync() => Coordinator.InitializeAsync(default);
        /// <summary>Reads the single exact fixture identity's detached status.</summary>
        /// <returns>The current durable fake state.</returns>
        internal Task<TelegramEndpointState> StatusAsync() => Coordinator.GetStatusAsync("owned-a", default);
        /// <summary>Adds a current duplicate internal id without any production token or network request.</summary>
        /// <param name="enabled">Whether the alias is enabled for receiver admission; disabled aliases still share session ownership.</param>
        internal void AddAlias(bool enabled = true)
        {
            Registry.Upsert(new BotInstance
            {
                Id = "owned-alias", Username = "dummy_alias_bot", Token = "123:abcdefghijklmnopqrstuvwxyz0123456789",
                Type = BotInstanceTypes.Owned, Enabled = enabled
            });
            var state = new TelegramEndpointState { BotId = "owned-alias", TelegramBotId = 123 };
            Store.Seed(state);
            Gate.Publish(state);
        }
        /// <summary>Queues an identity/revision-bound global operator command.</summary>
        /// <param name="target">Explicit fixture migration target.</param>
        /// <returns>The fixed command result code.</returns>
        internal async Task<string> RequestAsync(TelegramEndpointType target)
        {
            var state = await StatusAsync();
            return await Coordinator.RequestMigrationAsync("owned-a", target, 7, state.ControlRevision, 123, default);
        }
        /// <summary>Simulates process-local routing/controller loss while preserving committed state and transport observations.</summary>
        internal void RestartCoordinator()
        {
            Coordinator.Dispose();
            Gate = new TelegramEndpointRuntimeGate(Registry);
            Coordinator = CreateCoordinator();
        }
        /// <summary>Disposes unstarted dependencies and the fixture's isolated temporary directory.</summary>
        public void Dispose()
        {
            Coordinator.Dispose();
            Clients.Dispose();
            _services.Dispose();
            Telemetry?.Dispose();
            if (Directory.Exists(HostRoot)) Directory.Delete(HostRoot, true);
        }
    }

    /// <summary>Detached in-memory CAS persistence that makes every external boundary observable.</summary>
    private sealed class MemoryStore : ITelegramEndpointStateStore
    {
        /// <summary>Protects detached copies and atomic incident/history insertion.</summary>
        private readonly object _sync = new();
        /// <summary>Identity-scoped durable rows retained across fake process restarts.</summary>
        private readonly Dictionary<(string, long), TelegramEndpointState> _states = new();
        /// <summary>Append-only private transition receipts.</summary>
        private readonly List<TelegramEndpointHistory> _history = new();
        /// <summary>Deduplicated incident keys; no bodies or tokens are retained.</summary>
        internal HashSet<string> Alerts { get; } = new(StringComparer.Ordinal);
        /// <summary>Safe categories requested by coordinator transitions.</summary>
        internal HashSet<string> AlertCategories { get; } = new(StringComparer.Ordinal);
        /// <summary>Signals two independent Local health saves while another bot's migration is blocked.</summary>
        internal TaskCompletionSource SecondBotHealthSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Counts only the independent bot's committed health metadata writes.</summary>
        private int _otherBotHealthSaves;
        /// <summary>Optional deterministic CAS-conflict injection at one named durable boundary.</summary>
        internal string? RejectReason { get; set; }
        /// <summary>Optional deterministic persistence exception before a named transition commits.</summary>
        internal string? ThrowReason { get; set; }
        /// <summary>Seeds an independent durable identity before startup.</summary>
        /// <param name="state">Secret-free initial fake row.</param>
        internal void Seed(TelegramEndpointState state) { lock (_sync) _states[(state.BotId, state.TelegramBotId)] = state.Copy(); }
        /// <summary>Changes seeded durable state for a crash/configuration scenario.</summary>
        /// <param name="change">Deterministic mutation of the single fake fixture row.</param>
        internal void Mutate(Action<TelegramEndpointState> change) { lock (_sync) change(_states[("owned-a", 123)]); }
        /// <inheritdoc />
        public Task<TelegramEndpointState> GetOrCreateAsync(string botId, long identity, CancellationToken token)
        {
            lock (_sync)
            {
                if (!_states.TryGetValue((botId, identity), out var state))
                    _states[(botId, identity)] = state = new() { BotId = botId, TelegramBotId = identity };
                return Task.FromResult(state.Copy());
            }
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointState>> ReadAllAsync(CancellationToken token)
        {
            lock (_sync) return Task.FromResult<IReadOnlyList<TelegramEndpointState>>(_states.Values.Select(x => x.Copy()).ToArray());
        }
        /// <summary>Commits a detached fake CAS transition while preserving closed failure history for real panel/coordinator scenarios.</summary>
        /// <param name="state">Synthetic identity-bound proposal; never production state or credentials.</param>
        /// <param name="expectedRevision">Expected fake durable CAS revision.</param>
        /// <param name="historyReason">Optional fixed transition reason that creates a failure or state receipt.</param>
        /// <param name="alertCategory">Optional closed incident category recorded without message bodies.</param>
        /// <param name="token">Caller cancellation context retained by the existing fake interface.</param>
        /// <returns>True only when the fake revision commits; false for an injected or actual CAS conflict.</returns>
        /// <remarks>History includes a closed error category on failed transitions, matching the real store so later health cannot erase panel diagnosis. No network or filesystem operation occurs.</remarks>
        public Task<bool> TrySaveAsync(TelegramEndpointState state, long expectedRevision, string? historyReason = null,
            string? alertCategory = null, CancellationToken token = default)
        {
            lock (_sync)
            {
                if (historyReason != null && historyReason == ThrowReason) throw new InvalidOperationException("Controlled persistence failure.");
                var previous = _states[(state.BotId, state.TelegramBotId)];
                if (previous.Revision != expectedRevision || historyReason != null && historyReason == RejectReason) return Task.FromResult(false);
                state.Revision = expectedRevision + 1;
                var healthPhases = (previous.MigrationState is TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded) &&
                    (state.MigrationState is TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded);
                var changed = previous.DesiredEndpoint != state.DesiredEndpoint || previous.EffectiveEndpoint != state.EffectiveEndpoint ||
                    previous.Generation != state.Generation || previous.AutoFailoverEnabled != state.AutoFailoverEnabled ||
                    previous.OperationId != state.OperationId || previous.Trigger != state.Trigger ||
                    previous.LogoutAttemptedAtUtc != state.LogoutAttemptedAtUtc || previous.LogoutAcknowledgedAtUtc != state.LogoutAcknowledgedAtUtc ||
                    previous.CloudReuseEligibleAtUtc != state.CloudReuseEligibleAtUtc || previous.MigrationState != state.MigrationState && !healthPhases;
                state.ControlRevision = changed || state.ControlRevision > previous.ControlRevision ? previous.ControlRevision + 1 : previous.ControlRevision;
                _states[(state.BotId, state.TelegramBotId)] = state.Copy();
                if (state.BotId == "owned-b" && state.LastHealthCheckAtUtc.HasValue && ++_otherBotHealthSaves >= 2)
                    SecondBotHealthSaved.TrySetResult();
                if (historyReason != null) _history.Add(new()
                {
                    BotId = state.BotId, TelegramBotId = state.TelegramBotId, Reason = historyReason,
                    MigrationState = state.MigrationState, ActorTelegramUserId = state.ActorTelegramUserId, OperationId = state.OperationId,
                    FromDesiredEndpoint = previous.DesiredEndpoint, ToDesiredEndpoint = state.DesiredEndpoint,
                    FromEffectiveEndpoint = previous.EffectiveEndpoint, ToEffectiveEndpoint = state.EffectiveEndpoint,
                    Id = _history.Count + 1,
                    Outcome = state.LastFailureCategory != null &&
                        historyReason is "migration_failed" or "migration_admission_failed" or "logout_refused" or "logout_uncertain" or "manual_intervention" or "safe_retry_scheduled" or "startup_reconciled"
                        ? state.LastFailureCategory : state.MigrationState.ToString(), Revision = state.Revision,
                    CreatedAtUtc = state.LastMigrationAtUtc ?? state.MigrationStartedAtUtc ?? DateTime.UtcNow
                });
                if (alertCategory != null)
                {
                    AlertCategories.Add(alertCategory);
                    Alerts.Add($"{state.OutageId ?? state.OperationId ?? state.Revision.ToString()}:{alertCategory}");
                }
                return Task.FromResult(true);
            }
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointHistory>> ReadHistoryAsync(string botId, long identity, int limit, CancellationToken token)
        {
            lock (_sync) return Task.FromResult<IReadOnlyList<TelegramEndpointHistory>>(_history.Where(x => x.BotId == botId && x.TelegramBotId == identity).TakeLast(limit).Reverse().ToArray());
        }
        /// <inheritdoc />
        public Task<int> CountPendingAlertsAsync(CancellationToken token) { lock (_sync) return Task.FromResult(Alerts.Count); }
    }

    /// <summary>Fake protocol proves root/identity/logout ordering and never opens an HTTP connection.</summary>
    private sealed class FakeProtocol : ITelegramEndpointProtocol
    {
        /// <summary>Durable marker authority checked at every fake mutation.</summary>
        private readonly MemoryStore _store;
        /// <summary>Current process-local gate, including simulated restarts.</summary>
        private readonly Func<TelegramEndpointRuntimeGate> _gate;
        /// <summary>Receiver count checked before irreversible fake cleanup.</summary>
        private readonly FakeLifecycle _lifecycle;
        /// <summary>Ordered safe operation names, without tokens or endpoint URLs.</summary>
        internal ConcurrentQueue<string> Events { get; } = new();
        /// <summary>Controlled tokenless root result.</summary>
        internal TelegramEndpointFailure RootFailure { get; set; }
        /// <summary>Controlled exact Cloud identity result.</summary>
        internal TelegramEndpointFailure CloudFailure { get; set; }
        /// <summary>Controlled exact Local identity result.</summary>
        internal TelegramEndpointFailure LocalFailure { get; set; }
        /// <summary>Controlled one-attempt mutation acknowledgement.</summary>
        internal TelegramEndpointLogoutResult LogoutResult { get; set; } = new(true, false);
        /// <summary>Optional noncooperating safe-read transport for controlled deadline tests.</summary>
        internal Func<CancellationToken, Task<TelegramEndpointProbeResult>>? RootOverride { get; set; }
        /// <summary>Controlled registry/clock change after a source identity read but before the logout boundary.</summary>
        internal Action<TelegramEndpointType>? IdentityVerified { get; set; }
        /// <summary>Optional noncooperating mutation transport; a timed-out dispatch must never be replayed.</summary>
        internal Func<CancellationToken, Task<TelegramEndpointLogoutResult>>? LogoutOverride { get; set; }
        /// <summary>Signals the committed fake logout boundary before waiting for a controlled response.</summary>
        internal TaskCompletionSource LogoutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Actual fake irreversible attempt count.</summary>
        internal int LogoutCalls { get; private set; }
        /// <summary>Creates a marker-checking fake with no real token/network access.</summary>
        /// <param name="store">Detached fake durable state.</param>
        /// <param name="gate">Current real process-local generation gate resolver.</param>
        /// <param name="lifecycle">Observable receiver lifecycle.</param>
        internal FakeProtocol(MemoryStore store, Func<TelegramEndpointRuntimeGate> gate, FakeLifecycle lifecycle)
        { _store = store; _gate = gate; _lifecycle = lifecycle; }
        /// <inheritdoc />
        public Task<TelegramEndpointProbeResult> ProbeLocalServerAsync(CancellationToken cancellationToken)
        {
            Events.Enqueue("root");
            return RootOverride?.Invoke(cancellationToken) ?? Task.FromResult(new TelegramEndpointProbeResult(RootFailure));
        }
        /// <inheritdoc />
        public async Task<TelegramEndpointProbeResult> VerifyIdentityAsync(string botId, long identity,
            TelegramEndpointType endpoint, long generation, CancellationToken cancellationToken)
        {
            var state = await _store.GetOrCreateAsync(botId, identity, cancellationToken);
            if (endpoint == TelegramEndpointType.Local)
                Assert.True(state.EffectiveEndpoint == TelegramEndpointType.Local ||
                    state.LogoutEndpoint == TelegramEndpointType.Cloud && state.LogoutAcknowledgedAtUtc.HasValue,
                    "Local getMe before acknowledged Cloud logout would create an unsafe session.");
            Events.Enqueue("identity:" + endpoint);
            IdentityVerified?.Invoke(endpoint);
            return new(endpoint == TelegramEndpointType.Local ? LocalFailure : CloudFailure);
        }
        /// <inheritdoc />
        public async Task<TelegramEndpointLogoutResult> LogOutOnceAsync(string botId, long identity, TelegramEndpointType endpoint,
            long generation, CancellationToken cancellationToken)
        {
            var state = await _store.GetOrCreateAsync(botId, identity, cancellationToken);
            Assert.Equal(endpoint, state.LogoutEndpoint);
            Assert.NotNull(state.LogoutAttemptedAtUtc);
            Assert.Null(state.LogoutAcknowledgedAtUtc);
            Assert.Equal(endpoint == TelegramEndpointType.Cloud ? TelegramEndpointMigrationState.CloudLogoutPending
                : TelegramEndpointMigrationState.LocalLogoutPending, state.MigrationState);
            Assert.False(_gate().IsAvailable(botId, identity));
            Assert.Equal(0, _lifecycle.ActiveReceivers);
            Events.Enqueue("logout:" + endpoint);
            LogoutCalls++;
            LogoutStarted.TrySetResult();
            return LogoutOverride == null ? LogoutResult : await LogoutOverride(cancellationToken);
        }
    }

    /// <summary>Observable lifecycle tracks exactly one receiver and refuses optimistic readiness.</summary>
    private sealed class FakeLifecycle : ITelegramEndpointReceiverLifecycle
    {
        /// <summary>Existing runtime per-bot lifecycle semaphore.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);
        /// <summary>Current fake live receiver count.</summary>
        internal int ActiveReceivers { get; private set; } = 1;
        /// <summary>Maximum simultaneous receiver count, asserted never above one.</summary>
        internal int MaximumReceivers { get; private set; } = 1;
        /// <summary>Controlled strict readiness result.</summary>
        internal bool Ready { get; set; } = true;
        /// <summary>Injects a lifecycle exception after receiver registration to prove cleanup of partial startup.</summary>
        internal bool ThrowAfterStart { get; set; }
        /// <summary>Number of validated receiver starts.</summary>
        internal int Starts { get; private set; }
        /// <summary>Number of lazy lifecycle acquisitions.</summary>
        internal int Acquisitions { get; private set; }
        /// <summary>Signals original polling join before handler/request drain completes.</summary>
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public async Task<ITelegramEndpointReceiverLease> AcquireAsync(string botId, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            Acquisitions++;
            return new Lease(this);
        }
        /// <summary>Exclusive fake runtime semaphore lease; receiver stop/start never touches another bot.</summary>
        /// <param name="owner">Exclusive observable fixture lifecycle; no external receiver is involved.</param>
        private sealed class Lease(FakeLifecycle owner) : ITelegramEndpointReceiverLease
        {
            /// <summary>Prevents duplicate semaphore release.</summary>
            private bool _disposed;
            /// <inheritdoc />
            public Task StopAndWaitAsync(CancellationToken token)
            {
                owner.ActiveReceivers = 0;
                owner.Stopped.TrySetResult();
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task<bool> StartValidatedAsync(CancellationToken token)
            {
                Assert.Equal(0, owner.ActiveReceivers);
                if (!owner.Ready) return Task.FromResult(false);
                owner.ActiveReceivers++;
                owner.Starts++;
                owner.MaximumReceivers = Math.Max(owner.MaximumReceivers, owner.ActiveReceivers);
                if (owner.ThrowAfterStart) throw new InvalidOperationException("Controlled receiver startup failure.");
                return Task.FromResult(true);
            }
            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                if (!_disposed) { _disposed = true; owner._gate.Release(); }
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Injectable deterministic UTC, monotonic and cancellation timers.</summary>
    private sealed class ControlledClock : TimeProvider
    {
        /// <summary>Protects UTC and controlled due timers.</summary>
        private readonly object _sync = new();
        /// <summary>Deterministic UTC starting point.</summary>
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        /// <summary>Timers created only by bounded test operations.</summary>
        private readonly List<ControlledTimer> _timers = new();
        /// <summary>Signals creation of the monitor's first 15-second injected delay before advancing logical time.</summary>
        internal TaskCompletionSource MonitorDelayScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        /// <inheritdoc />
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ControlledTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                if (dueTime == TimeSpan.FromSeconds(15)) MonitorDelayScheduled.TrySetResult();
                return timer;
            }
        }
        /// <summary>Advances logical time and fires due deadline callbacks without sleeping.</summary>
        /// <param name="duration">Nonnegative simulated elapsed duration.</param>
        internal void Advance(TimeSpan duration)
        {
            ControlledTimer[] due;
            lock (_sync)
            {
                _now += duration;
                due = _timers.Where(x => x.Due <= _now).ToArray();
                foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }
        /// <summary>Controlled one-shot budget timer; disposing prevents later cancellation callbacks.</summary>
        /// <param name="owner">Logical UTC clock controlling deadline firing.</param>
        /// <param name="callback">Budget cancellation callback supplied by TimeProvider consumers.</param>
        /// <param name="state">Opaque callback state; never customer or token content.</param>
        private sealed class ControlledTimer(ControlledClock owner, TimerCallback callback, object? state) : ITimer
        {
            /// <summary>Next due UTC, or MaxValue after cancellation/disposal.</summary>
            internal DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            /// <summary>Whether a due timer was disposed before its callback was invoked.</summary>
            private bool _disposed;
            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
                    return true;
                }
            }
            /// <summary>Fires the controlled deadline once unless disposed.</summary>
            internal void Fire() { if (!_disposed) callback(state); }
            /// <inheritdoc />
            public void Dispose() { lock (owner._sync) { _disposed = true; Due = DateTimeOffset.MaxValue; } }
            /// <inheritdoc />
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>Controlled HTTP root response verifies production parser behavior without any real connection.</summary>
    /// <param name="body">Controlled official-root JSON or invalid bytes, not a bot API response.</param>
    private sealed class RootHandler(string body) : HttpMessageHandler
    {
        /// <summary>Observed root path, never a token-bearing API path.</summary>
        internal string? Path { get; private set; }
        /// <summary>Observed empty query string.</summary>
        internal string? Query { get; private set; }
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(body) });
        }
    }
}
