using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Xunit;

/// <summary>Read-only root validation and stage-specific migration regressions using the existing fake fixture.</summary>
public sealed partial class TelegramEndpointCoordinatorTests
{
    /// <summary>Production-shaped absent mapping refuses before probes, intent, receiver drain or logout and survives restart.</summary>
    /// <returns>A task after the active Cloud route and exact admission diagnostic are preserved.</returns>
    [Fact]
    public async Task Mapping_missing_is_a_durable_admission_failure_not_an_accepted_operation()
    {
        using var f = new Fixture(options: new TelegramEndpointRoutingOptions());
        await f.InitializeAsync();
        Assert.Equal("local_file_mapping_missing", await f.RequestAsync(TelegramEndpointType.Local));
        var state = await f.StatusAsync();
        Assert.Null(state.OperationId);
        Assert.Equal(TelegramEndpointType.Cloud, state.DesiredEndpoint);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, state.MigrationState);
        Assert.Equal(0, f.Lifecycle.Acquisitions);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.Empty(f.Protocol.Events);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        f.RestartCoordinator();
        await f.InitializeAsync();
        var diagnostic = TelegramEndpointDiagnosticCatalog.Describe((await f.StatusAsync()).LastFailureCategory);
        Assert.Equal("LOCAL_FILE_MAPPING_MISSING", diagnostic.Code);
        Assert.Equal("local_file_mapping", diagnostic.Stage);
    }

    /// <summary>Malformed roots remain visible to Cloud administration but cannot cross a Local-use boundary.</summary>
    /// <param name="server">Synthetic malformed server root, never a production path.</param>
    /// <param name="host">Synthetic malformed host root, never a production path.</param>
    /// <param name="expected">Closed missing versus invalid diagnostic.</param>
    /// <returns>A task after refusal without any transport or lifecycle call.</returns>
    [Theory]
    [InlineData("/data", "", "local_file_mapping_missing")]
    [InlineData("relative", "HOST", "local_file_mapping_invalid")]
    [InlineData("/data/../other", "HOST", "local_file_mapping_invalid")]
    [InlineData("/data/./other", "HOST", "local_file_mapping_invalid")]
    [InlineData("/data\n", "HOST", "local_file_mapping_invalid")]
    [InlineData("/data", "relative", "local_file_mapping_invalid")]
    public async Task Invalid_mapping_does_not_disable_cloud_and_refuses_local(string server, string host, string expected)
    {
        using var root = new Fixture();
        var options = new TelegramEndpointRoutingOptions { LocalFileServerRoot = server, LocalFileHostRoot = host == "HOST" ? root.HostRoot : host };
        using var f = new Fixture(options: options);
        await f.InitializeAsync();
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(expected, await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Empty(f.Protocol.Events);
        Assert.Equal(0, f.Lifecycle.Acquisitions);
    }

    /// <summary>The real empty-directory probe succeeds, but missing, denied and linked evidence never becomes readiness.</summary>
    [Fact]
    public void Mapping_probe_checks_listing_and_search_without_customer_file_reads_or_scratch_writes()
    {
        using var f = new Fixture();
        Assert.Null(TelegramLocalFileMapper.ProbeReadiness(f.Options));
        Assert.Equal("local_file_access_denied", TelegramLocalFileMapper.ProbeReadiness(f.Options, _ => throw new UnauthorizedAccessException("private-path-never-output")));
        Assert.Equal("local_file_probe_failed", TelegramLocalFileMapper.ProbeReadiness(f.Options, _ => throw new IOException("private-path-never-output")));
        var link = new IOException("private-link-never-output");
        link.Data["local_mapping_link"] = true;
        Assert.Equal("local_file_path_linked", TelegramLocalFileMapper.ProbeReadiness(f.Options, _ => throw link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.HostRoot));
        var missing = new TelegramEndpointRoutingOptions { LocalFileServerRoot = "/data", LocalFileHostRoot = Path.Combine(f.HostRoot, "missing") };
        Assert.Equal("local_file_host_missing", TelegramLocalFileMapper.ProbeReadiness(missing));
        // The seam only adds a failure after real validation; it cannot rescue a missing root.
        Assert.Equal("local_file_host_missing", TelegramLocalFileMapper.ProbeReadiness(missing, _ => { }));
        var traversal = new TelegramEndpointRoutingOptions { LocalFileServerRoot = "/data", LocalFileHostRoot = Path.Combine(f.HostRoot, "..", "outside") };
        Assert.Equal("local_file_mapping_invalid", TelegramLocalFileMapper.ProbeReadiness(traversal));
        Assert.Throws<BotTransportUnavailableException>(() => TelegramLocalFileMapper.Resolve(traversal, "/data/file"));
    }

    /// <summary>Real linked-directory evidence on Unix and deterministic Windows metadata failure are both fail-closed.</summary>
    [Fact]
    public void Linked_mapping_cannot_be_ready_or_resolve_a_customer_path()
    {
        using var f = new Fixture();
        if (OperatingSystem.IsWindows())
        {
            var deniedLink = new IOException("Synthetic reparse-point evidence");
            deniedLink.Data["local_mapping_link"] = true;
            Assert.Equal("local_file_path_linked", TelegramLocalFileMapper.ProbeReadiness(f.Options, _ => throw deniedLink));
        }
        else
        {
            var link = Path.Combine(f.HostRoot, "linked");
            Directory.CreateSymbolicLink(link, f.HostRoot);
            var options = new TelegramEndpointRoutingOptions { LocalFileServerRoot = "/data", LocalFileHostRoot = link };
            Assert.Equal("local_file_path_linked", TelegramLocalFileMapper.ProbeReadiness(options));
            Assert.Throws<BotTransportUnavailableException>(() => TelegramLocalFileMapper.Resolve(options, "/data/linked/file"));
            Directory.Delete(link);
        }
    }

    /// <summary>Root health versus Cloud source identity remain separately diagnosed before any logout.</summary>
    /// <param name="source">True selects source getMe; false selects tokenless Local root.</param>
    /// <param name="failure">Synthetic safe typed protocol failure.</param>
    /// <param name="expected">Exact persisted stage/category.</param>
    /// <returns>A task after safe source preservation and restart-stable diagnostics.</returns>
    [Theory]
    [InlineData(false, TelegramEndpointFailure.ConnectionRefused, "local_root_connection_refused")]
    [InlineData(false, TelegramEndpointFailure.Timeout, "local_root_timeout")]
    [InlineData(false, TelegramEndpointFailure.InvalidResponse, "local_root_invalid_response")]
    [InlineData(true, TelegramEndpointFailure.IdentityMismatch, "cloud_source_identity_identity_mismatch")]
    [InlineData(true, TelegramEndpointFailure.TokenRejected, "cloud_source_identity_token_rejected")]
    public async Task Prelogout_boundaries_have_precise_restart_stable_failure(bool source, TelegramEndpointFailure failure, string expected)
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        if (source) f.Protocol.CloudFailure = failure; else f.Protocol.RootFailure = failure;
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        f.RestartCoordinator();
        await f.InitializeAsync();
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(expected.ToUpperInvariant(), TelegramEndpointDiagnosticCatalog.Describe(expected).Code);
    }

    /// <summary>Destination identity rejection stays fenced after exactly one logout and never looks like success.</summary>
    /// <param name="failure">Synthetic destination identity mismatch or token rejection.</param>
    /// <returns>A task after persistent precise rejection and no replay across restart.</returns>
    [Theory]
    [InlineData(TelegramEndpointFailure.IdentityMismatch)]
    [InlineData(TelegramEndpointFailure.TokenRejected)]
    public async Task Destination_identity_rejection_preserves_stage_and_single_logout(TelegramEndpointFailure failure)
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.LocalFailure = failure;
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var expected = TelegramEndpointDiagnosticCatalog.ProbeCategory("local_destination_identity", failure);
        var state = await f.StatusAsync();
        Assert.Equal(expected, state.LastFailureCategory);
        Assert.Equal(TelegramEndpointMigrationState.ManualInterventionRequired, state.MigrationState);
        Assert.Null(state.LastMigrationAtUtc);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(1, f.Protocol.LogoutCalls);
        f.RestartCoordinator();
        await f.InitializeAsync();
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Cloud and Local logout refusal/uncertainty retain their irreversible boundary across restart.</summary>
    /// <param name="localSource">True starts an existing Local session; false starts Cloud.</param>
    /// <param name="uncertain">True means ambiguous cleanup; false means definitive refusal.</param>
    /// <returns>A task after one logout and a restart-stable precise diagnostic.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Logout_failures_are_endpoint_specific_and_never_replayed(bool localSource, bool uncertain)
    {
        using var f = new Fixture(localSource ? TelegramEndpointType.Local : TelegramEndpointType.Cloud);
        await f.InitializeAsync();
        f.Protocol.LogoutResult = new(false, uncertain, uncertain ? TelegramEndpointFailure.LogoutUncertain : TelegramEndpointFailure.LogoutRefused);
        await f.RequestAsync(localSource ? TelegramEndpointType.Cloud : TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var expected = (localSource ? "local" : "cloud") + (uncertain ? "_logout_uncertain" : "_logout_refused");
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(!uncertain, f.Gate.IsAvailable("owned-a", 123));
        f.RestartCoordinator();
        await f.InitializeAsync();
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Pending operation refusal is distinct from registration validation and cannot erase a cooldown marker.</summary>
    /// <returns>A task after unchanged durable operation and explicit in-progress/cooldown guidance across restart.</returns>
    [Fact]
    public async Task Pending_and_cooldown_diagnostics_do_not_claim_acceptance_or_repeat_cleanup()
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        await f.RequestAsync(TelegramEndpointType.Cloud);
        Assert.Equal("migration_in_progress", await f.RequestAsync(TelegramEndpointType.Cloud));
        await f.Coordinator.RunPendingOperationsAsync(default);
        var state = await f.StatusAsync();
        Assert.Equal("CLOUD_COOLDOWN", TelegramEndpointDiagnosticCatalog.Describe(null, state).Code);
        f.RestartCoordinator();
        await f.InitializeAsync();
        Assert.Equal("migration_in_progress", await f.RequestAsync(TelegramEndpointType.Cloud));
        Assert.Equal(state.CloudReuseEligibleAtUtc, (await f.StatusAsync()).CloudReuseEligibleAtUtc);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Admission failures produce real JSONL with a closed uppercase code, phase and checked category without path or payload.</summary>
    /// <returns>A task after the existing telemetry writer flushes a truthful rejected admission.</returns>
    [Fact]
    public async Task Mapping_admission_failure_emits_precise_safe_telemetry()
    {
        using var f = new Fixture(options: new TelegramEndpointRoutingOptions(), includeTelemetry: true);
        var telemetry = f.Telemetry!;
        await telemetry.StartAsync(default);
        try
        {
            await f.InitializeAsync();
            Assert.Equal("local_file_mapping_missing", await f.RequestAsync(TelegramEndpointType.Local));
        }
        finally { await telemetry.StopAsync(default); }
        var rows = Directory.GetFiles(telemetry.StorageDirectory, "*.jsonl").SelectMany(File.ReadLines)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); });
        var row = Assert.Single(rows, x => x.GetProperty("eventType").GetString() == "telegram_endpoint_migration");
        Assert.Equal("failure", row.GetProperty("outcome").GetString());
        Assert.Equal("LOCAL_FILE_MAPPING_MISSING", row.GetProperty("failureClassification").GetString());
        Assert.Equal("local_file_mapping", row.GetProperty("stage").GetString());
        Assert.Equal("local_file_mapping", row.GetProperty("operation").GetString());
        Assert.DoesNotContain(f.HostRoot, row.GetRawText());
        Assert.DoesNotContain("actorTelegramUserId", row.GetRawText());
    }

    /// <summary>Legacy generic failures do not gain invented source/destination stages; untrusted categories never become operator output.</summary>
    [Fact]
    public void Legacy_and_unknown_diagnostics_do_not_guess_or_echo_private_text()
    {
        Assert.Null(TelegramEndpointDiagnosticCatalog.Describe("accepted"));
        Assert.Null(TelegramEndpointDiagnosticCatalog.Describe("unchanged"));
        Assert.Equal("unknown", TelegramEndpointDiagnosticCatalog.Describe("identity_mismatch").Stage);
        Assert.Equal("unknown", TelegramEndpointDiagnosticCatalog.Describe("configuration_missing").Stage);
        var diagnostic = TelegramEndpointDiagnosticCatalog.Describe("private-token-path-payload");
        Assert.Equal("DIAGNOSTIC_UNAVAILABLE", diagnostic.Code);
        Assert.DoesNotContain("private", diagnostic.ToString());
    }

    /// <summary>Drain budget expiry records the awaited boundary without logout or guessed source restoration.</summary>
    /// <returns>A task after the source stays fenced for explicit review with no destination request.</returns>
    [Fact]
    public async Task Migration_drain_timeout_retains_precise_stage_without_logout()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        Assert.True(f.Gate.TryAcquireExecution("owned-a", out var handler));
        try
        {
            await f.RequestAsync(TelegramEndpointType.Local);
            var operation = f.Coordinator.RunPendingOperationsAsync(default);
            await f.Lifecycle.Stopped.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(31));
            await operation;
            Assert.Equal("source_drain_timeout", (await f.StatusAsync()).LastFailureCategory);
            Assert.Equal(TelegramEndpointMigrationState.ManualInterventionRequired, (await f.StatusAsync()).MigrationState);
            Assert.False(f.Gate.IsAvailable("owned-a", 123));
            Assert.Equal(0, f.Protocol.LogoutCalls);
            Assert.DoesNotContain("identity:Local", f.Protocol.Events);
        }
        finally { handler.Dispose(); }
    }

    /// <summary>A temporary Local destination outage keeps exact failure stage across restart and only repeats safe reads.</summary>
    /// <returns>A task after successful safe retry activates Local without a second Cloud logout.</returns>
    [Fact]
    public async Task Destination_outage_retains_diagnostic_and_safe_retry_authority_across_restart()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        f.Protocol.LocalFailure = TelegramEndpointFailure.ConnectionRefused;
        await f.RequestAsync(TelegramEndpointType.Local);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var failed = await f.StatusAsync();
        Assert.Equal("local_destination_identity_connection_refused", failed.LastFailureCategory);
        Assert.Equal(TelegramEndpointMigrationState.SwitchingToLocal, failed.MigrationState);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        f.RestartCoordinator();
        await f.InitializeAsync();
        Assert.Equal(failed.LastFailureCategory, (await f.StatusAsync()).LastFailureCategory);
        f.Protocol.LocalFailure = TelegramEndpointFailure.None;
        f.Clock.Advance(failed.NextAttemptAtUtc!.Value - f.Clock.GetUtcNow().UtcDateTime);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Each migration protocol boundary emits a precise safe uppercase code rather than a generic success or health failure.</summary>
    /// <param name="boundary">Closed synthetic failure boundary controlled only by this test.</param>
    /// <param name="expected">Expected exact persisted and serialized category.</param>
    /// <returns>A task after real JSONL is flushed and its secret-free diagnosis checked.</returns>
    [Theory]
    [InlineData("root", "local_root_invalid_response")]
    [InlineData("source", "cloud_source_identity_token_rejected")]
    [InlineData("destination", "local_destination_identity_identity_mismatch")]
    [InlineData("logout", "cloud_logout_refused")]
    [InlineData("uncertain", "cloud_logout_uncertain")]
    [InlineData("readiness", "local_destination_receiver_not_ready")]
    public async Task Migration_boundary_telemetry_matches_persisted_diagnostic(string boundary, string expected)
    {
        using var f = new Fixture(includeTelemetry: true);
        var telemetry = f.Telemetry!;
        await telemetry.StartAsync(default);
        try
        {
            await f.InitializeAsync();
            if (boundary == "root") f.Protocol.RootFailure = TelegramEndpointFailure.InvalidResponse;
            if (boundary == "source") f.Protocol.CloudFailure = TelegramEndpointFailure.TokenRejected;
            if (boundary == "destination") f.Protocol.LocalFailure = TelegramEndpointFailure.IdentityMismatch;
            if (boundary == "logout") f.Protocol.LogoutResult = new(false, false, TelegramEndpointFailure.LogoutRefused);
            if (boundary == "uncertain") f.Protocol.LogoutResult = new(false, true, TelegramEndpointFailure.LogoutUncertain);
            if (boundary == "readiness") f.Lifecycle.Ready = false;
            await f.RequestAsync(TelegramEndpointType.Local);
            await f.Coordinator.RunPendingOperationsAsync(default);
            Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        }
        finally { await telemetry.StopAsync(default); }
        var rows = Directory.GetFiles(telemetry.StorageDirectory, "*.jsonl").SelectMany(File.ReadLines)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        var row = Assert.Single(rows, x => x.TryGetProperty("category", out var category) && category.GetString() == expected);
        var diagnostic = TelegramEndpointDiagnosticCatalog.Describe(expected);
        Assert.Equal("failure", row.GetProperty("outcome").GetString());
        Assert.Equal(diagnostic.Code, row.GetProperty("failureClassification").GetString());
        Assert.Equal(diagnostic.Stage, row.GetProperty("stage").GetString());
        Assert.Equal(diagnostic.Stage, row.GetProperty("operation").GetString());
        Assert.DoesNotContain(f.HostRoot, row.GetRawText());
        Assert.Equal(0, telemetry.DroppedEvents);
    }

    /// <summary>The actual production server root shape maps to an isolated absolute host directory and permits the existing fake protocol flow.</summary>
    /// <returns>A task after official ordering, exact identity and one Cloud logout activate Local without filesystem writes.</returns>
    [Fact]
    public async Task Production_shaped_readable_data_mapping_permits_cloud_to_local()
    {
        using var mapping = new Fixture();
        using var f = new Fixture(options: new TelegramEndpointRoutingOptions
        { LocalFileServerRoot = "/data", LocalFileHostRoot = mapping.HostRoot });
        await f.InitializeAsync();
        Assert.Equal("accepted", await f.RequestAsync(TelegramEndpointType.Local));
        Assert.Empty(f.Protocol.Events);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.StatusAsync()).MigrationState);
        Assert.Equal(new[] { "root", "identity:Cloud", "logout:Cloud", "identity:Local" }, f.Protocol.Events);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(mapping.HostRoot));
    }

    /// <summary>Reverse migration retains source versus destination token rejection and respects the persisted Cloud cooldown.</summary>
    /// <param name="source">True rejects Local source identity; false rejects Cloud destination only after the cooldown.</param>
    /// <returns>A task after exact safe rejection without incorrect Cloud activation or repeated Local logout.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_to_cloud_identity_diagnostics_preserve_direction_and_cooldown(bool source)
    {
        using var f = new Fixture(TelegramEndpointType.Local);
        await f.InitializeAsync();
        if (source) f.Protocol.LocalFailure = TelegramEndpointFailure.TokenRejected;
        else f.Protocol.CloudFailure = TelegramEndpointFailure.TokenRejected;
        await f.RequestAsync(TelegramEndpointType.Cloud);
        await f.Coordinator.RunPendingOperationsAsync(default);
        if (!source)
        {
            Assert.Equal("CLOUD_COOLDOWN", TelegramEndpointDiagnosticCatalog.Describe(null, await f.StatusAsync()).Code);
            Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            await f.Coordinator.RunPendingOperationsAsync(default);
        }
        var expected = source ? "local_source_identity_token_rejected" : "cloud_destination_identity_token_rejected";
        Assert.Equal(expected, (await f.StatusAsync()).LastFailureCategory);
        Assert.Equal(source ? 0 : 1, f.Protocol.LogoutCalls);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
    }

    /// <summary>Bulk admission returns the same precise empty-config refusal per nonretained target without inventing operation receipts.</summary>
    /// <returns>A task after the independent Cloud control remains retained and no target probes or logs out.</returns>
    [Fact]
    public async Task Bulk_mapping_missing_preserves_precise_individual_results_without_protocol_calls()
    {
        using var f = new BulkFixture();
        f.Source.Options.LocalFileServerRoot = "";
        f.Source.Options.LocalFileHostRoot = "";
        f.Restart();
        var frozen = await f.FreezeAsync();
        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);
        Assert.Single(results, x => x.ResultCode == "retained_cloud_control");
        Assert.All(results.Where(x => x.ResultCode != "retained_cloud_control"), result =>
        {
            Assert.Equal("local_file_mapping_missing", result.ResultCode);
            Assert.Null(result.OperationId);
        });
        Assert.Empty(f.Store.Intents);
        Assert.Empty(f.Protocol.Events);
        Assert.Equal(0, f.Protocol.LogoutCalls);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
    }
}

/// <summary>Real SQLite persistence coverage for closed endpoint migration diagnostics.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>The real users.db allowlist preserves every closed migration category and admission history across store recreation.</summary>
    /// <returns>A task after precise failure metadata is read from SQLite without adding fields or changing financial state.</returns>
    [Fact]
    public async Task Migration_diagnostic_allowlist_survives_real_sqlite_restart()
    {
        using var databases = new Databases();
        var store = new TelegramEndpointStore(databases.Users, new AppConfig());
        foreach (var category in TelegramEndpointDiagnosticCatalog.MigrationFailureCategories)
        {
            var state = await store.GetOrCreateAsync("diagnostic-bot", 123, default);
            state.LastFailureCategory = category;
            Assert.True(await store.TrySaveAsync(state, state.Revision, "migration_admission_failed"));
            var restarted = new TelegramEndpointStore(databases.Users, new AppConfig());
            Assert.Equal(category, (await restarted.GetOrCreateAsync("diagnostic-bot", 123, default)).LastFailureCategory);
            var latest = Assert.Single(await restarted.ReadHistoryAsync("diagnostic-bot", 123, 1, default));
            Assert.Equal(category, latest.Outcome);
            Assert.NotEqual("DIAGNOSTIC_UNAVAILABLE", TelegramEndpointDiagnosticCatalog.Describe(category).Code);
            Assert.NotEqual("unknown", TelegramEndpointDiagnosticCatalog.Describe(category).Stage);
        }
        Assert.Contains(await store.ReadHistoryAsync("diagnostic-bot", 123, 100, default), row => row.Reason == "migration_admission_failed");
    }

    /// <summary>Later health success cannot erase a migration stage from history or leak an old failure into successful/requested receipts.</summary>
    /// <returns>A task after real SQLite history distinguishes failure evidence from state-only success and intent transitions.</returns>
    [Fact]
    public async Task Historical_failure_stage_survives_health_clear_but_success_receipts_remain_state_outcomes()
    {
        using var databases = new Databases();
        var store = new TelegramEndpointStore(databases.Users, new AppConfig());
        var state = await store.GetOrCreateAsync("diagnostic-bot", 123, default);
        state.LastFailureCategory = "cloud_source_identity_token_rejected";
        Assert.True(await store.TrySaveAsync(state, state.Revision, "migration_failed"));
        Assert.True(await store.TrySaveAsync(state, state.Revision, "migration_requested"));
        Assert.True(await store.TrySaveAsync(state, state.Revision, "migration_succeeded"));
        state.LastFailureCategory = null;
        Assert.True(await store.TrySaveAsync(state, state.Revision));
        var restarted = new TelegramEndpointStore(databases.Users, new AppConfig());
        var history = await restarted.ReadHistoryAsync("diagnostic-bot", 123, 100, default);
        Assert.Equal("cloud_source_identity_token_rejected", Assert.Single(history, x => x.Reason == "migration_failed").Outcome);
        Assert.Equal(TelegramEndpointMigrationState.Cloud.ToString(), Assert.Single(history, x => x.Reason == "migration_requested").Outcome);
        Assert.Equal(TelegramEndpointMigrationState.Cloud.ToString(), Assert.Single(history, x => x.Reason == "migration_succeeded").Outcome);
        Assert.Null((await restarted.GetOrCreateAsync("diagnostic-bot", 123, default)).LastFailureCategory);
    }
}
