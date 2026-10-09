using System.Collections.Concurrent;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>Bulk-admission regressions using the real coordinator/gate and the existing detached CAS, clock and fake protocol fixtures.</summary>
public sealed partial class TelegramEndpointCoordinatorTests
{
    /// <summary>Cloud includes its own Local hosting identity and every configured entry; registration never performs network or lifecycle work.</summary>
    /// <returns>A task after exact frozen-entry results and durable per-bot intent admission have been asserted.</returns>
    [Fact]
    public async Task Bulk_cloud_admits_all_configured_snapshots_including_host_tenant_and_assistant()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        f.AddBot("tenant", 456, BotInstanceTypes.Tenant, endpoint: TelegramEndpointType.Local);
        f.AddBot("assistant", 567, BotInstanceTypes.SalesAssistant, endpoint: TelegramEndpointType.Local);
        f.AddBot("disabled", 678, enabled: false, endpoint: TelegramEndpointType.Local);
        f.AddBot("missing", 0);
        var frozen = await f.FreezeAsync();

        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen);

        Assert.Equal(frozen.Select(x => (x.BotId, x.Identity)), results.Select(x => (x.BotId, x.Identity)));
        Assert.Equal(frozen.Length, results.Count);
        foreach (var result in results)
        {
            if (result.BotId is "disabled" or "missing")
            {
                Assert.Equal("unavailable", result.ResultCode);
                Assert.Null(result.OperationId);
                continue;
            }
            Assert.Equal("accepted", result.ResultCode);
            var state = await f.Coordinator.GetStatusAsync(result.BotId, default);
            Assert.Equal(TelegramEndpointMigrationState.FallbackPending, state.MigrationState);
            Assert.Equal(TelegramEndpointType.Local, state.EffectiveEndpoint);
            Assert.Equal(TelegramEndpointType.Cloud, state.DesiredEndpoint);
            Assert.Equal(state.OperationId, result.OperationId);
            Assert.Equal(32, result.OperationId!.Length);
        }
        Assert.Equal(new[] { "assistant", "operator-control", "owned-a", "tenant" }, f.Store.Intents.ToArray());
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.Empty(f.Protocol.Events);
        Assert.Equal(0, f.Lifecycle.Acquisitions);
    }

    /// <summary>Local deliberately retains the eligible Cloud hosting identity while admitting owned, tenant and assistant targets, including truthful disabled/missing refusals.</summary>
    /// <returns>A task after every frozen result and the still-admitted Cloud control have been asserted.</returns>
    [Fact]
    public async Task Bulk_local_retains_host_and_reports_every_snapshot_without_health_dependency()
    {
        using var f = new BulkFixture();
        f.AddBot("owned-b", 456);
        f.AddBot("tenant", 567, BotInstanceTypes.Tenant);
        f.AddBot("assistant", 678, BotInstanceTypes.SalesAssistant);
        f.AddBot("disabled", 789, enabled: false);
        f.AddBot("missing", 0);
        f.Protocol.RootFailure = TelegramEndpointFailure.ConnectionRefused;
        var frozen = await f.FreezeAsync();

        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);

        Assert.Equal(frozen.Select(x => (x.BotId, x.Identity)), results.Select(x => (x.BotId, x.Identity)));
        Assert.Equal("retained_cloud_control", results.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Null(results.Single(x => x.BotId == "owned-a").OperationId);
        Assert.Equal("unavailable", results.Single(x => x.BotId == "disabled").ResultCode);
        Assert.Equal("unavailable", results.Single(x => x.BotId == "missing").ResultCode);
        foreach (var result in results.Where(x => x.BotId is not ("owned-a" or "disabled" or "missing")))
        {
            Assert.Equal("accepted", result.ResultCode);
            var state = await f.Coordinator.GetStatusAsync(result.BotId, default);
            Assert.Equal(TelegramEndpointMigrationState.CheckingLocal, state.MigrationState);
            Assert.Equal(TelegramEndpointType.Cloud, state.EffectiveEndpoint);
            Assert.Equal(result.OperationId, state.OperationId);
            Assert.False(f.Gate.IsAvailable(result.BotId, result.Identity));
        }
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(TelegramEndpointType.Cloud, f.Gate.GetRoute("owned-a", 123).Endpoint);
        Assert.Empty(f.Protocol.Events);
        Assert.Equal(0, f.Lifecycle.Acquisitions);
    }

    /// <summary>A Local host cannot be retained as Cloud; the fallback Cloud control is chosen by ordinal id rather than caller order.</summary>
    /// <returns>A task after deterministic retention, unchanged Local host and accepted remaining intents have been asserted.</returns>
    [Fact]
    public async Task Bulk_local_retains_deterministic_owned_cloud_control_when_host_is_local()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("a-cloud", 456);
        f.AddBot("z-cloud", 567);
        var frozen = (await f.FreezeAsync()).Reverse().ToArray();

        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);

        Assert.Equal(frozen.Select(x => x.BotId), results.Select(x => x.BotId));
        Assert.Equal("retained_cloud_control", results.Single(x => x.BotId == "a-cloud").ResultCode);
        Assert.Equal("unchanged", results.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Equal("accepted", results.Single(x => x.BotId == "operator-control").ResultCode);
        Assert.Equal("accepted", results.Single(x => x.BotId == "z-cloud").ResultCode);
        Assert.True(f.Gate.IsAvailable("a-cloud", 456));
        Assert.Null((await f.Coordinator.GetStatusAsync("a-cloud", default)).OperationId);
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Requesting Local for the only owned Cloud identity explicitly retains it instead of bypassing independent-control safety.</summary>
    /// <returns>A task after the sole snapshot is reported retained and no intent was persisted.</returns>
    [Fact]
    public async Task Bulk_local_explicitly_retains_the_only_cloud_control()
    {
        using var f = new BulkFixture(includeCloudControl: false);
        var frozen = await f.FreezeAsync();
        var result = Assert.Single(await f.RequestAsync(TelegramEndpointType.Local, frozen));
        Assert.Equal("retained_cloud_control", result.ResultCode);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Null((await f.Coordinator.GetStatusAsync("owned-a", default)).OperationId);
        Assert.Empty(f.Store.Intents);
    }

    /// <summary>Fenced/disabled/nonowned Cloud metadata cannot masquerade as an independent retained Cloud admission path.</summary>
    /// <returns>A task after existing individual safety refuses all Cloud-to-Local candidates without new durable intent.</returns>
    [Fact]
    public async Task Bulk_local_never_uses_fenced_disabled_tenant_or_assistant_cloud_as_control()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("disabled-owned", 456, enabled: false);
        f.AddBot("tenant", 567, BotInstanceTypes.Tenant);
        f.AddBot("assistant", 678, BotInstanceTypes.SalesAssistant);
        var frozen = await f.FreezeAsync();
        f.Gate.Fence("operator-control", 345);

        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);

        Assert.DoesNotContain(results, x => x.ResultCode == "retained_cloud_control");
        Assert.Equal("unchanged", results.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Equal("unavailable", results.Single(x => x.BotId == "disabled-owned").ResultCode);
        Assert.All(results.Where(x => x.BotId is not ("owned-a" or "disabled-owned")),
            x => Assert.Equal("control_path_missing", x.ResultCode));
        Assert.Empty(f.Store.Intents);
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Frozen identities and operator revisions remain authoritative; newly added registry entries never join the batch.</summary>
    /// <returns>A task after replaced, stale, unknown and valid snapshots retain distinct truthful outcomes.</returns>
    [Fact]
    public async Task Bulk_preserves_frozen_identity_revision_and_excludes_later_inventory_entries()
    {
        using var f = new BulkFixture();
        f.AddBot("revision-changed", 456);
        f.AddBot("valid", 567);
        var frozen = (await f.FreezeAsync()).Append(new TelegramEndpointBulkTarget("unknown", 999, 0)).ToArray();
        f.AddBot("operator-control", 678);
        f.Source.Store.Seed(new TelegramEndpointState { BotId = "revision-changed", TelegramBotId = 456, ControlRevision = 1 });
        f.AddBot("new-after-confirmation", 789);

        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);

        Assert.Equal(frozen.Select(x => (x.BotId, x.Identity)), results.Select(x => (x.BotId, x.Identity)));
        Assert.Equal("stale", results.Single(x => x.BotId == "operator-control").ResultCode);
        Assert.Equal(345, results.Single(x => x.BotId == "operator-control").Identity);
        Assert.Equal("stale", results.Single(x => x.BotId == "revision-changed").ResultCode);
        Assert.Equal("unavailable", results.Single(x => x.BotId == "unknown").ResultCode);
        Assert.Equal("accepted", results.Single(x => x.BotId == "valid").ResultCode);
        Assert.DoesNotContain(results, x => x.BotId == "new-after-confirmation");
        Assert.Null((await f.Coordinator.GetStatusAsync("new-after-confirmation", default)).OperationId);
        Assert.Null((await f.Store.GetOrCreateAsync("operator-control", 345, default)).OperationId);
        Assert.Equal(new[] { "valid" }, f.Store.Intents.ToArray());
    }

    /// <summary>Aliased, pending, cooldown and uncertain snapshots remain visible refusals rather than disappearing from a bulk inventory.</summary>
    /// <returns>A task after all individual protocol refusals preserve their existing state and deadlines.</returns>
    [Fact]
    public async Task Bulk_keeps_alias_pending_cooldown_and_uncertainty_refusals_visible()
    {
        using var f = new BulkFixture();
        f.AddBot("alias", 345, enabled: false);
        f.AddBot("pending", 456);
        f.AddBot("uncertain", 567);
        f.AddBot("cooldown", 678, endpoint: TelegramEndpointType.Local);
        var deadline = f.Clock.GetUtcNow().UtcDateTime.AddMinutes(10);
        f.Source.Store.Seed(new TelegramEndpointState
        {
            BotId = "pending", TelegramBotId = 456, DesiredEndpoint = TelegramEndpointType.Local,
            MigrationState = TelegramEndpointMigrationState.CheckingLocal, OperationId = new string('a', 32)
        });
        f.Source.Store.Seed(new TelegramEndpointState
        {
            BotId = "uncertain", TelegramBotId = 567, MigrationState = TelegramEndpointMigrationState.CloudLogoutUncertain,
            LogoutEndpoint = TelegramEndpointType.Cloud, LogoutAttemptedAtUtc = f.Clock.GetUtcNow().UtcDateTime,
            OperationId = new string('b', 32), LastFailureCategory = "logout_uncertain"
        });
        f.Source.Store.Seed(new TelegramEndpointState
        {
            BotId = "cooldown", TelegramBotId = 678, DesiredEndpoint = TelegramEndpointType.Cloud,
            EffectiveEndpoint = TelegramEndpointType.Local, MigrationState = TelegramEndpointMigrationState.CloudWait,
            LogoutEndpoint = TelegramEndpointType.Local, LogoutAttemptedAtUtc = f.Clock.GetUtcNow().UtcDateTime,
            LogoutAcknowledgedAtUtc = f.Clock.GetUtcNow().UtcDateTime, CloudReuseEligibleAtUtc = deadline,
            NextAttemptAtUtc = deadline, OperationId = new string('c', 32)
        });
        var frozen = await f.FreezeAsync();

        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen);

        Assert.Equal(frozen.Length, results.Count);
        Assert.Equal("aliased", results.Single(x => x.BotId == "operator-control").ResultCode);
        Assert.Equal("unavailable", results.Single(x => x.BotId == "alias").ResultCode);
        Assert.Equal("migration_in_progress", results.Single(x => x.BotId == "pending").ResultCode);
        Assert.Equal("migration_in_progress", results.Single(x => x.BotId == "cooldown").ResultCode);
        Assert.Equal("unsafe", results.Single(x => x.BotId == "uncertain").ResultCode);
        Assert.Equal(deadline, (await f.Coordinator.GetStatusAsync("cooldown", default)).CloudReuseEligibleAtUtc);
        Assert.Equal(new string('b', 32), (await f.Coordinator.GetStatusAsync("uncertain", default)).OperationId);
        Assert.All(results.Where(x => x.ResultCode is not ("accepted" or "unchanged")), x => Assert.Null(x.OperationId));
        Assert.Empty(f.Store.Intents);
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Only the live global allowlist grants bulk authority; positive tenant ownership and invalid actor ids grant none.</summary>
    /// <param name="actor">Authenticated synthetic Telegram user id not present in the global allowlist.</param>
    /// <returns>A task after every snapshot is denied without intent admission.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    public async Task Bulk_rejects_non_global_actors_even_when_they_own_a_tenant(long actor)
    {
        using var f = new BulkFixture();
        f.AddBot("tenant", 456, BotInstanceTypes.Tenant, owner: actor);
        var frozen = await f.FreezeAsync();
        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen, actor: actor);
        Assert.All(results, x => Assert.Equal("denied", x.ResultCode));
        Assert.Empty(f.Store.Intents);
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Revocation takes effect at the API boundary despite a previously frozen authorized confirmation.</summary>
    /// <returns>A task after the complete frozen inventory is denied by the live global configuration.</returns>
    [Fact]
    public async Task Bulk_rechecks_live_global_admin_revocation()
    {
        using var f = new BulkFixture();
        var frozen = await f.FreezeAsync();
        f.Source.Configuration["AdminsUserIds:0"] = "8";
        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);
        Assert.All(results, x => Assert.Equal("denied", x.ResultCode));
        Assert.Empty(f.Store.Intents);
    }

    /// <summary>The API independently rejects unowned, assistant, disabled, missing and identity-replaced hosting identities.</summary>
    /// <param name="condition">Synthetic current hosting-configuration security failure.</param>
    /// <param name="expected">Closed refusal returned for every frozen snapshot.</param>
    /// <returns>A task after no target was admitted through an ineligible host.</returns>
    [Theory]
    [InlineData("tenant", "denied")]
    [InlineData("assistant", "denied")]
    [InlineData("assistant-flag", "denied")]
    [InlineData("disabled", "unavailable")]
    [InlineData("missing", "unavailable")]
    [InlineData("replaced", "stale")]
    [InlineData("unknown", "unavailable")]
    public async Task Bulk_authenticates_the_current_owned_host_identity(string condition, string expected)
    {
        using var f = new BulkFixture();
        var frozen = await f.FreezeAsync();
        f.AddBot("owned-a", condition == "missing" ? 0 : condition == "replaced" ? 456 : 123,
            condition == "tenant" ? BotInstanceTypes.Tenant : condition == "assistant" ? BotInstanceTypes.SalesAssistant : BotInstanceTypes.Owned,
            enabled: condition != "disabled");
        if (condition == "assistant-flag") f.Source.Registry.GetById("owned-a").IsSalesAssistant = true;
        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen,
            host: condition == "unknown" ? "unknown" : "owned-a");
        Assert.All(results, x => Assert.Equal(expected, x.ResultCode));
        Assert.Empty(f.Store.Intents);
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Duplicate canonical ids are rejected before any registration instead of submitting one bot twice.</summary>
    /// <returns>A task after both duplicate snapshots receive denied and no intent is persisted.</returns>
    [Fact]
    public async Task Bulk_rejects_duplicate_case_variant_targets_before_any_admission()
    {
        using var f = new BulkFixture();
        var frozen = await f.FreezeAsync();
        var duplicates = frozen.Append(frozen.Single(x => x.BotId == "owned-a") with { BotId = "OWNED-A" }).ToArray();
        var results = await f.RequestAsync(TelegramEndpointType.Local, duplicates);
        Assert.Equal(duplicates.Length, results.Count);
        Assert.All(results, x => Assert.Equal("denied", x.ResultCode));
        Assert.Empty(f.Store.Intents);
    }

    /// <summary>A blocked individual save holds only admission locks: concurrent batches refuse immediately and conflicting individual intent remains busy.</summary>
    /// <returns>A task after the original batch alone completes its sequential admissions.</returns>
    [Fact]
    public async Task Bulk_serialization_is_nonwaiting_and_individual_conflicting_intent_is_busy()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        var frozen = HostFirst(await f.FreezeAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.BeforeSave = async (state, reason, token) =>
        {
            if (state.BotId != "owned-a" || reason != "migration_requested") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var first = f.RequestAsync(TelegramEndpointType.Cloud, frozen);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var concurrent = await f.RequestAsync(TelegramEndpointType.Cloud, frozen);
            Assert.All(concurrent, x => Assert.Equal("batch_busy", x.ResultCode));
            Assert.Equal("busy", await f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Local, 7, 0, 123, default));
            Assert.Empty(f.Store.Intents);
            Assert.Empty(f.Protocol.Events);
        }
        finally { release.TrySetResult(); }
        Assert.All(await first, x => Assert.Equal("accepted", x.ResultCode));
        Assert.Equal(new[] { "owned-a", "operator-control" }, f.Store.Intents.ToArray());
    }

    /// <summary>The deliberately retained Cloud bot's existing lock prevents an unrelated concurrent individual request from removing it mid-batch.</summary>
    /// <returns>A task after the independent command was busy and the retained control remained Cloud.</returns>
    [Fact]
    public async Task Bulk_local_reserves_retained_cloud_control_only_for_bounded_admission()
    {
        using var f = new BulkFixture();
        var frozen = HostFirst(await f.FreezeAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.BeforeSave = async (state, reason, token) =>
        {
            if (reason != "migration_requested") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var batch = f.RequestAsync(TelegramEndpointType.Local, frozen);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal("busy", await f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Local, 7, 0, 123, default));
            Assert.True(f.Gate.IsAvailable("owned-a", 123));
            Assert.Empty(f.Protocol.Events);
        }
        finally { release.TrySetResult(); }
        var results = await batch;
        Assert.Equal("retained_cloud_control", results.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Equal("accepted", results.Single(x => x.BotId == "operator-control").ResultCode);
        Assert.Equal("unchanged", await f.Coordinator.RequestMigrationAsync("owned-a", TelegramEndpointType.Cloud, 7, 0, 123, default));
    }

    /// <summary>Cancellation after a proven successful admission preserves its exact operation receipt and never submits the untouched tail.</summary>
    /// <returns>A task after one accepted result and all remaining not_submitted results have been asserted.</returns>
    [Fact]
    public async Task Bulk_cancellation_after_commit_preserves_accepted_receipt_and_untouched_tail()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        f.AddBot("tail", 456, endpoint: TelegramEndpointType.Local);
        var frozen = HostFirst(await f.FreezeAsync());
        using var cancellation = new CancellationTokenSource();
        f.Store.AfterSave = (state, reason, token) =>
        {
            if (state.BotId == "owned-a" && reason == "migration_requested") cancellation.Cancel();
            return Task.CompletedTask;
        };

        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen, token: cancellation.Token);

        Assert.Equal("accepted", results[0].ResultCode);
        Assert.Equal((await f.Store.GetOrCreateAsync("owned-a", 123, default)).OperationId, results[0].OperationId);
        Assert.All(results.Skip(1), x => Assert.Equal("not_submitted", x.ResultCode));
        Assert.Equal(new[] { "owned-a" }, f.Store.Intents.ToArray());
        Assert.Null((await f.Coordinator.GetStatusAsync("tail", default)).OperationId);
    }

    /// <summary>Cancellation while persistence is in flight distinguishes a possibly committed current registration from entries never submitted.</summary>
    /// <param name="afterCommit">True cancels after the CAS commit; false cancels before the same target's commit.</param>
    /// <returns>A task after the proven prefix, uncertain current entry and untouched tail remain distinct without replay.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bulk_cancellation_during_registration_is_uncertain_and_never_retries(bool afterCommit)
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        f.AddBot("tail", 456, endpoint: TelegramEndpointType.Local);
        var frozen = HostFirst(await f.FreezeAsync());
        using var cancellation = new CancellationTokenSource();
        Func<TelegramEndpointState, string?, CancellationToken, Task> cancelCurrent = (state, reason, token) =>
        {
            if (state.BotId == "operator-control" && reason == "migration_requested")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };
        if (afterCommit) f.Store.AfterSave = cancelCurrent;
        else f.Store.BeforeSave = cancelCurrent;

        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen, token: cancellation.Token);

        Assert.Equal(new[] { "accepted", "registration_uncertain", "not_submitted" }, results.Select(x => x.ResultCode));
        Assert.NotNull(results[0].OperationId);
        Assert.Null(results[1].OperationId);
        Assert.Equal(afterCommit ? new[] { "owned-a", "operator-control" } : new[] { "owned-a" }, f.Store.Intents.ToArray());
        var current = await f.Store.GetOrCreateAsync("operator-control", 345, default);
        Assert.Equal(afterCommit, current.OperationId != null);
        Assert.Null((await f.Coordinator.GetStatusAsync("tail", default)).OperationId);
    }

    /// <summary>A persistence exception can happen before or after commit; both cases retain the proven prefix and never replay the ambiguous current request.</summary>
    /// <param name="afterCommit">True injects failure after the durable individual commit; false before it.</param>
    /// <returns>A task after honest partial results and restart resumption of only committed individual intents.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bulk_partial_persistence_failure_keeps_truthful_results_and_restart_resumes_commits(bool afterCommit)
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        f.AddBot("tail", 456, endpoint: TelegramEndpointType.Local);
        var frozen = HostFirst(await f.FreezeAsync());
        Func<TelegramEndpointState, string?, CancellationToken, Task> failCurrent = (state, reason, token) =>
            state.BotId == "operator-control" && reason == "migration_requested"
                ? Task.FromException(new InvalidOperationException("Controlled persistence failure.")) : Task.CompletedTask;
        if (afterCommit) f.Store.AfterSave = failCurrent;
        else f.Store.BeforeSave = failCurrent;

        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen);

        Assert.Equal(new[] { "accepted", "registration_uncertain", "not_submitted" }, results.Select(x => x.ResultCode));
        Assert.Equal(afterCommit ? new[] { "owned-a", "operator-control" } : new[] { "owned-a" }, f.Store.Intents.ToArray());
        Assert.Empty(f.Protocol.Events);
        f.Store.BeforeSave = null;
        f.Store.AfterSave = null;
        f.Restart();
        await f.Coordinator.InitializeAsync(default);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.CloudWait, (await f.Coordinator.GetStatusAsync("owned-a", default)).MigrationState);
        Assert.Equal(afterCommit ? TelegramEndpointMigrationState.CloudWait : TelegramEndpointMigrationState.Local,
            (await f.Coordinator.GetStatusAsync("operator-control", default)).MigrationState);
        Assert.Equal(TelegramEndpointMigrationState.Local, (await f.Coordinator.GetStatusAsync("tail", default)).MigrationState);
        Assert.Equal(afterCommit ? 2 : 1, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
        Assert.Equal(results[0].OperationId, (await f.Coordinator.GetStatusAsync("owned-a", default)).OperationId);
    }

    /// <summary>A definite CAS refusal is stale, not uncertain, and does not suppress admissions for later independent snapshots.</summary>
    /// <returns>A task after accepted/stale/accepted outcomes prove sequential complete inventory processing.</returns>
    [Fact]
    public async Task Bulk_cas_refusal_preserves_stale_result_and_continues_other_targets()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        f.AddBot("tail", 456, endpoint: TelegramEndpointType.Local);
        var frozen = HostFirst(await f.FreezeAsync());
        f.Store.RejectBotId = "operator-control";
        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen);
        Assert.Equal(new[] { "accepted", "stale", "accepted" }, results.Select(x => x.ResultCode));
        Assert.Null(results[1].OperationId);
        Assert.Equal(new[] { "owned-a", "tail" }, f.Store.Intents.ToArray());
        Assert.Null((await f.Coordinator.GetStatusAsync("operator-control", default)).OperationId);
    }

    /// <summary>Accepted operation receipts are captured at their own commit and survive worker advancement plus a newer individual operation during a later admission.</summary>
    /// <returns>A task after the aggregate retained the original accepted operation instead of echoing the latest state.</returns>
    [Fact]
    public async Task Bulk_accepted_receipt_survives_worker_advancement_and_newer_individual_intent()
    {
        using var f = new BulkFixture();
        f.AddBot("later", 456);
        var inventory = await f.FreezeAsync();
        var frozen = new[]
        {
            inventory.Single(x => x.BotId == "operator-control"), inventory.Single(x => x.BotId == "later"),
            inventory.Single(x => x.BotId == "owned-a")
        };
        string? originalOperation = null;
        string? newerOperation = null;
        f.Store.BeforeSave = async (state, reason, token) =>
        {
            if (state.BotId != "later" || reason != "migration_requested") return;
            originalOperation = (await f.Coordinator.GetStatusAsync("operator-control", default)).OperationId;
            await f.Coordinator.RunPendingOperationsAsync(default);
            var advanced = await f.Coordinator.GetStatusAsync("operator-control", default);
            Assert.Equal(TelegramEndpointMigrationState.Local, advanced.MigrationState);
            Assert.Equal("accepted", await f.Coordinator.RequestMigrationAsync("operator-control", TelegramEndpointType.Cloud,
                7, advanced.ControlRevision, 345, default));
            newerOperation = (await f.Coordinator.GetStatusAsync("operator-control", default)).OperationId;
        };

        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);

        Assert.Equal("accepted", results[0].ResultCode);
        Assert.Equal(originalOperation, results[0].OperationId);
        Assert.NotEqual(newerOperation, results[0].OperationId);
        Assert.Equal("accepted", results[1].ResultCode);
        Assert.Equal("retained_cloud_control", results[2].ResultCode);
        Assert.Equal(1, f.Protocol.LogoutCalls);
    }

    /// <summary>Mixed-route batches migrate only the opposite route and leave the destination's admitted request and saved preference untouched.</summary>
    /// <param name="target">Explicit Cloud or Local bulk destination; both must skip their already-active receiver epoch.</param>
    /// <returns>A task after the real worker completes the one necessary migration without writing or fencing the skipped identity.</returns>
    /// <remarks>Regression: recovered Cloud and degraded Local remain valid skip destinations even when desired intent differs. A pinned request on the skipped bot must not delay another identity's migration.</remarks>
    [Theory]
    [InlineData(TelegramEndpointType.Cloud)]
    [InlineData(TelegramEndpointType.Local)]
    public async Task Bulk_mixed_routes_skip_active_destination_without_changing_or_draining_it(TelegramEndpointType target)
    {
        using var f = new BulkFixture(target);
        var opposite = target == TelegramEndpointType.Cloud ? TelegramEndpointType.Local : TelegramEndpointType.Cloud;
        f.AddBot("needs-migration", 456, BotInstanceTypes.Tenant, endpoint: opposite);
        var priorOperation = new string('a', 32);
        f.Source.Store.Mutate(state =>
        {
            state.DesiredEndpoint = opposite;
            state.MigrationState = target == TelegramEndpointType.Cloud
                ? TelegramEndpointMigrationState.CloudRecovered : TelegramEndpointMigrationState.LocalDegraded;
            state.OperationId = priorOperation;
        });
        var frozen = await f.FreezeAsync();
        var before = await f.Coordinator.GetStatusAsync("owned-a", default);
        var route = f.Gate.GetRoute("owned-a", 123);
        using var pinnedRequest = f.Gate.AcquireRequest("owned-a", 123, route.Generation);

        var results = await f.RequestAsync(target, frozen);

        var skipped = results.Single(x => x.BotId == "owned-a");
        Assert.Equal("unchanged", skipped.ResultCode);
        Assert.Equal(priorOperation, skipped.OperationId);
        Assert.Equal("accepted", results.Single(x => x.BotId == "needs-migration").ResultCode);
        Assert.Equal(new[] { "needs-migration" }, f.Store.Intents.ToArray());
        Assert.Equal(route, f.Gate.GetRoute("owned-a", 123));
        await f.Coordinator.RunPendingOperationsAsync(default);
        // Cloud activation respects the official cooldown; advancing only the isolated clock avoids a real wait.
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        await f.Coordinator.RunPendingOperationsAsync(default);

        var after = await f.Coordinator.GetStatusAsync("owned-a", default);
        Assert.Equal(before.DesiredEndpoint, after.DesiredEndpoint);
        Assert.Equal(before.EffectiveEndpoint, after.EffectiveEndpoint);
        Assert.Equal(before.MigrationState, after.MigrationState);
        Assert.Equal(before.OperationId, after.OperationId);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.ControlRevision, after.ControlRevision);
        Assert.Equal(route, f.Gate.GetRoute("owned-a", 123));
        Assert.Empty(await f.Coordinator.GetHistoryAsync("owned-a", default));
        var moved = await f.Coordinator.GetStatusAsync("needs-migration", default);
        Assert.Equal(target, moved.EffectiveEndpoint);
        Assert.True(f.Gate.IsAvailable("needs-migration", 456));
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(1, f.Lifecycle.Starts);
    }

    /// <summary>All admitted Local intents survive process-local state loss and resume individually while the explicitly retained Cloud control remains admitted.</summary>
    /// <returns>A task after fake protocol migration completes the durable admissions without recreating the aggregate batch.</returns>
    [Fact]
    public async Task Bulk_restart_resumes_individual_local_intents_and_preserves_retained_control()
    {
        using var f = new BulkFixture();
        f.AddBot("tenant", 456, BotInstanceTypes.Tenant);
        var results = await f.RequestAsync(TelegramEndpointType.Local, await f.FreezeAsync());
        Assert.Empty(f.Protocol.Events);
        f.Restart();
        await f.Coordinator.InitializeAsync(default);
        await f.Coordinator.RunPendingOperationsAsync(default);
        foreach (var result in results.Where(x => x.ResultCode == "accepted"))
        {
            var state = await f.Coordinator.GetStatusAsync(result.BotId, default);
            Assert.Equal(TelegramEndpointMigrationState.Local, state.MigrationState);
            Assert.Equal(result.OperationId, state.OperationId);
            Assert.True(f.Gate.IsAvailable(result.BotId, result.Identity));
        }
        Assert.Equal(2, f.Protocol.LogoutCalls);
        Assert.True(f.Gate.IsAvailable("owned-a", 123));
        Assert.Equal(TelegramEndpointType.Cloud, f.Gate.GetRoute("owned-a", 123).Endpoint);
        Assert.Null((await f.Coordinator.GetStatusAsync("owned-a", default)).OperationId);
        Assert.Equal(1, f.Lifecycle.MaximumReceivers);
    }

    /// <summary>Bulk Cloud admission never bypasses the individual Local-cleanup wait or an uncertain logout, including after restart.</summary>
    /// <returns>A task after cooldown remains fenced, later Cloud activation succeeds, and uncertainty is never replayed.</returns>
    [Fact]
    public async Task Bulk_cloud_preserves_individual_cooldown_and_logout_uncertainty()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("uncertain", 456, endpoint: TelegramEndpointType.Local);
        f.Source.Store.Seed(new TelegramEndpointState
        {
            BotId = "uncertain", TelegramBotId = 456, DesiredEndpoint = TelegramEndpointType.Cloud,
            EffectiveEndpoint = TelegramEndpointType.Local, MigrationState = TelegramEndpointMigrationState.LocalLogoutUncertain,
            LogoutEndpoint = TelegramEndpointType.Local, LogoutAttemptedAtUtc = f.Clock.GetUtcNow().UtcDateTime,
            LastFailureCategory = "logout_uncertain", OperationId = new string('d', 32)
        });
        var results = await f.RequestAsync(TelegramEndpointType.Cloud, await f.FreezeAsync());
        Assert.Equal("accepted", results.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Equal("unsafe", results.Single(x => x.BotId == "uncertain").ResultCode);
        await f.Coordinator.RunPendingOperationsAsync(default);
        var waiting = await f.Coordinator.GetStatusAsync("owned-a", default);
        Assert.Equal(TelegramEndpointMigrationState.CloudWait, waiting.MigrationState);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), waiting.CloudReuseEligibleAtUtc);
        Assert.False(f.Gate.IsAvailable("owned-a", 123));
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
        f.Restart();
        await f.Coordinator.InitializeAsync(default);
        var retry = await f.RequestAsync(TelegramEndpointType.Cloud, await f.FreezeAsync());
        Assert.Equal("migration_in_progress", retry.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Equal("unsafe", retry.Single(x => x.BotId == "uncertain").ResultCode);
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.DoesNotContain("identity:Cloud", f.Protocol.Events);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await f.Coordinator.RunPendingOperationsAsync(default);
        Assert.Equal(TelegramEndpointMigrationState.Cloud, (await f.Coordinator.GetStatusAsync("owned-a", default)).MigrationState);
        Assert.Equal(TelegramEndpointMigrationState.LocalLogoutUncertain, (await f.Coordinator.GetStatusAsync("uncertain", default)).MigrationState);
        Assert.Equal(1, f.Protocol.LogoutCalls);
        Assert.Equal(results.Single(x => x.BotId == "owned-a").OperationId,
            (await f.Coordinator.GetStatusAsync("owned-a", default)).OperationId);
    }

    /// <summary>A new Cloud control may satisfy the existing independent-control rule, but cannot be added to an already frozen migration target set.</summary>
    /// <returns>A task after only the original tenant snapshot was admitted and the later Cloud identity remained untouched.</returns>
    [Fact]
    public async Task Bulk_excludes_new_cloud_control_from_targets_while_preserving_existing_safety_guard()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, BotInstanceTypes.Tenant);
        var frozen = await f.FreezeAsync();
        f.AddBot("new-cloud-control", 456);
        await f.Coordinator.GetStatusAsync("new-cloud-control", default);

        var results = await f.RequestAsync(TelegramEndpointType.Local, frozen);

        Assert.Equal(frozen.Select(x => x.BotId), results.Select(x => x.BotId));
        Assert.Equal("unchanged", results.Single(x => x.BotId == "owned-a").ResultCode);
        Assert.Equal("accepted", results.Single(x => x.BotId == "operator-control").ResultCode);
        Assert.DoesNotContain(results, x => x.BotId == "new-cloud-control");
        Assert.Equal(new[] { "operator-control" }, f.Store.Intents.ToArray());
        Assert.Null((await f.Coordinator.GetStatusAsync("new-cloud-control", default)).OperationId);
        Assert.True(f.Gate.IsAvailable("new-cloud-control", 456));
    }

    /// <summary>Mid-batch admin revocation preserves already committed intent and denies the remaining frozen entries without submitting them.</summary>
    /// <returns>A task after an accepted prefix and denied untouched tail have been asserted against live configuration.</returns>
    [Fact]
    public async Task Bulk_rechecks_global_authority_between_individual_admissions()
    {
        using var f = new BulkFixture(TelegramEndpointType.Local);
        f.AddBot("operator-control", 345, endpoint: TelegramEndpointType.Local);
        f.AddBot("tail", 456, endpoint: TelegramEndpointType.Local);
        var frozen = HostFirst(await f.FreezeAsync());
        f.Store.AfterSave = (state, reason, token) =>
        {
            if (state.BotId == "owned-a" && reason == "migration_requested")
                f.Source.Configuration["AdminsUserIds:0"] = "8";
            return Task.CompletedTask;
        };

        var results = await f.RequestAsync(TelegramEndpointType.Cloud, frozen);

        Assert.Equal("accepted", results[0].ResultCode);
        Assert.All(results.Skip(1), x => Assert.Equal("denied", x.ResultCode));
        Assert.Equal(new[] { "owned-a" }, f.Store.Intents.ToArray());
        Assert.Null((await f.Coordinator.GetStatusAsync("tail", default)).OperationId);
    }

    /// <summary>A pre-cancelled callback submits no target and an empty inventory has no invented outcome.</summary>
    /// <returns>A task after empty and cancelled requests leave all individual state untouched.</returns>
    [Fact]
    public async Task Bulk_empty_or_pre_cancelled_inventory_never_submits_intent()
    {
        using var f = new BulkFixture();
        var frozen = await f.FreezeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.All(await f.RequestAsync(TelegramEndpointType.Local, frozen, token: cancellation.Token),
            x => Assert.Equal("not_submitted", x.ResultCode));
        Assert.Empty(await f.RequestAsync(TelegramEndpointType.Cloud, Array.Empty<TelegramEndpointBulkTarget>()));
        Assert.Empty(f.Store.Intents);
        Assert.Empty(f.Protocol.Events);
    }

    /// <summary>Orders the hosting snapshot first while retaining deterministic ordinal order for the rest of an isolated confirmation.</summary>
    /// <param name="frozen">Complete secret-free fixture inventory, copied rather than modified.</param>
    /// <returns>A new frozen array with owned-a first and every other entry preserved.</returns>
    private static TelegramEndpointBulkTarget[] HostFirst(TelegramEndpointBulkTarget[] frozen) =>
        frozen.OrderBy(x => x.BotId == "owned-a" ? 0 : 1).ThenBy(x => x.BotId, StringComparer.Ordinal).ToArray();

    /// <summary>Reuses the existing network-free coordinator fixture with a controllable persistence wrapper and a real bulk controller.</summary>
    private sealed class BulkFixture : IDisposable
    {
        /// <summary>Existing fake protocol, clock, CAS store and registry fixture; no real receiver is started.</summary>
        internal Fixture Source { get; }
        /// <summary>Persistence boundary controls wrapping the existing detached CAS store.</summary>
        internal BulkStore Store { get; }
        /// <summary>Real coordinator under test, recreated on simulated process restart.</summary>
        internal TelegramEndpointCoordinator Coordinator { get; private set; }
        /// <summary>Current real gate used by both this controller and the existing fake mutation-order assertions.</summary>
        internal TelegramEndpointRuntimeGate Gate => Source.Gate;
        /// <summary>Shared no-network fake protocol enforcing durable logout markers.</summary>
        internal FakeProtocol Protocol => Source.Protocol;
        /// <summary>Existing observable serialized receiver lifecycle.</summary>
        internal FakeLifecycle Lifecycle => Source.Lifecycle;
        /// <summary>Existing deterministic UTC and deadline clock.</summary>
        internal ControlledClock Clock => Source.Clock;
        /// <summary>Lazy isolated lifecycle container used only when a test explicitly invokes durable worker execution.</summary>
        private readonly ServiceProvider _services;

        /// <summary>Creates a real controller sharing the existing fixture's no-network dependencies.</summary>
        /// <param name="endpoint">Initial hosting endpoint stored before route hydration.</param>
        /// <param name="includeCloudControl">Whether to include the default independent owned Cloud identity.</param>
        internal BulkFixture(TelegramEndpointType endpoint = TelegramEndpointType.Cloud, bool includeCloudControl = true)
        {
            Source = new Fixture(endpoint, includeCloudControl: includeCloudControl);
            Store = new BulkStore(Source.Store);
            _services = new ServiceCollection().AddSingleton<ITelegramEndpointReceiverLifecycle>(Source.Lifecycle).BuildServiceProvider();
            Coordinator = CreateCoordinator();
        }

        /// <summary>Creates the real coordinator without starting its hosted worker or any transport.</summary>
        /// <returns>A coordinator bound to the current fixture gate and durable wrapper.</returns>
        private TelegramEndpointCoordinator CreateCoordinator() => new(Store, Source.Gate, Source.Registry, Source.Clients,
            Source.Configuration, Source.Options, _services, NullLogger<TelegramEndpointCoordinator>.Instance,
            timeProvider: Source.Clock, protocol: Source.Protocol);

        /// <summary>Adds or replaces an isolated configured identity and seeds its exact detached endpoint state.</summary>
        /// <param name="botId">Unique synthetic internal registry id.</param>
        /// <param name="identity">Synthetic positive BotFather id, or zero for a missing token.</param>
        /// <param name="type">Owned, tenant or sales-assistant registry type.</param>
        /// <param name="enabled">Whether the synthetic configuration is enabled.</param>
        /// <param name="endpoint">Initial durable endpoint for a positive identity.</param>
        /// <param name="owner">Optional synthetic tenant owner's Telegram user id; never migration authority.</param>
        internal void AddBot(string botId, long identity, string type = BotInstanceTypes.Owned, bool enabled = true,
            TelegramEndpointType endpoint = TelegramEndpointType.Cloud, long? owner = null)
        {
            Source.Registry.Upsert(new BotInstance
            {
                Id = botId, Username = "dummy_" + botId.Replace('-', '_') + "_bot",
                Token = identity > 0 ? identity + ":abcdefghijklmnopqrstuvwxyz0123456789" : "",
                Type = type, Enabled = enabled, OwnerTelegramUserId = owner
            });
            if (identity > 0) Source.Store.Seed(new TelegramEndpointState
            {
                BotId = botId, TelegramBotId = identity, DesiredEndpoint = endpoint, EffectiveEndpoint = endpoint,
                MigrationState = endpoint == TelegramEndpointType.Cloud ? TelegramEndpointMigrationState.Cloud : TelegramEndpointMigrationState.Local
            });
        }

        /// <summary>Reads the real inventory and freezes all identities/control revisions exactly as a confirmed UI would.</summary>
        /// <returns>A complete secret-free snapshot array including disabled and token-missing configurations.</returns>
        internal async Task<TelegramEndpointBulkTarget[]> FreezeAsync() =>
            (await Coordinator.GetInventoryAsync(default)).Select(x => new TelegramEndpointBulkTarget(x.BotId, x.TelegramBotId, x.ControlRevision)).ToArray();

        /// <summary>Invokes the real bulk API with a fixed owned private-host binding and explicitly frozen inventory.</summary>
        /// <param name="target">Explicit endpoint destination.</param>
        /// <param name="frozen">Complete frozen confirmation snapshots.</param>
        /// <param name="actor">Synthetic authenticated Telegram actor; seven is the configured global admin.</param>
        /// <param name="host">Exact hosting internal id, normally owned-a.</param>
        /// <param name="token">Controllable callback persistence budget.</param>
        /// <returns>The coordinator's actual per-target admission outcomes.</returns>
        internal Task<IReadOnlyList<TelegramEndpointBulkResult>> RequestAsync(TelegramEndpointType target,
            IReadOnlyList<TelegramEndpointBulkTarget> frozen, long actor = 7, string host = "owned-a", CancellationToken token = default) =>
            Coordinator.RequestBulkMigrationAsync(host, 123, target, actor, frozen, token);

        /// <summary>Drops the process-local controller/gate while keeping the exact committed store and existing protocol observations.</summary>
        internal void Restart()
        {
            Coordinator.Dispose();
            Source.RestartCoordinator();
            Coordinator = CreateCoordinator();
        }

        /// <summary>Disposes unstarted services and delegates isolated mapping cleanup to the existing fixture.</summary>
        public void Dispose()
        {
            Coordinator.Dispose();
            _services.Dispose();
            Source.Dispose();
        }
    }

    /// <summary>Controllable persistence adapter over the existing real-behavior detached CAS fake, never a mock command echo.</summary>
    /// <param name="inner">Existing identity-scoped store retaining committed state and append-only migration receipts.</param>
    private sealed class BulkStore(MemoryStore inner) : ITelegramEndpointStateStore
    {
        /// <summary>Actual committed migration-request bot ids in sequential admission order, retained across fake restart.</summary>
        internal ConcurrentQueue<string> Intents { get; } = new();
        /// <summary>Optional precommit persistence pause, exception or cancellation, receiving the exact caller token.</summary>
        internal Func<TelegramEndpointState, string?, CancellationToken, Task>? BeforeSave { get; set; }
        /// <summary>Optional postcommit exception or cancellation proving an acknowledgement can be ambiguous.</summary>
        internal Func<TelegramEndpointState, string?, CancellationToken, Task>? AfterSave { get; set; }
        /// <summary>Optional exact target whose migration-request CAS is definitively refused without changing durable state.</summary>
        internal string? RejectBotId { get; set; }

        /// <inheritdoc />
        public Task<TelegramEndpointState> GetOrCreateAsync(string botId, long identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return inner.GetOrCreateAsync(botId, identity, token);
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointState>> ReadAllAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return inner.ReadAllAsync(token);
        }
        /// <inheritdoc />
        public async Task<bool> TrySaveAsync(TelegramEndpointState state, long expectedRevision, string? historyReason = null,
            string? alertCategory = null, CancellationToken token = default)
        {
            if (BeforeSave != null) await BeforeSave(state, historyReason, token);
            token.ThrowIfCancellationRequested();
            if (historyReason == "migration_requested" && state.BotId == RejectBotId) return false;
            var committed = await inner.TrySaveAsync(state, expectedRevision, historyReason, alertCategory, token);
            if (!committed) return false;
            if (historyReason == "migration_requested") Intents.Enqueue(state.BotId);
            if (AfterSave != null) await AfterSave(state, historyReason, token);
            return true;
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointHistory>> ReadHistoryAsync(string botId, long identity, int limit, CancellationToken token) =>
            inner.ReadHistoryAsync(botId, identity, limit, token);
        /// <inheritdoc />
        public Task<int> CountPendingAlertsAsync(CancellationToken token) => inner.CountPendingAlertsAsync(token);
    }
}
