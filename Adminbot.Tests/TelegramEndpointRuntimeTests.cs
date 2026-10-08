using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Adminbot.Services.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>A retained normal facade follows committed generations, while an old receiver cannot start another poll.</summary>
    /// <returns>A task completing after actual v22 HTTP origins and obsolete epoch rejection are asserted.</returns>
    [Fact]
    public async Task Endpoint_real_sdk_cloud_default_retained_facade_and_pinned_receiver()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var retained = clients.GetClient("endpoint-a");
        var previous = clients.GetEndpointReceiverClient("endpoint-a");
        Assert.False(retained.LocalBotServer);
        await retained.SendMessage(711, "synthetic fixture content");
        Assert.Contains(http.Calls, x => x.Endpoint == "cloud" && x.Method == "sendMessage" && x.Identity == 123456);
        gate.Fence("endpoint-a", 123456);
        await gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(1), default);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
        Assert.True(retained.LocalBotServer);
        await retained.SendMessage(711, "synthetic second fixture content");
        Assert.Contains(http.Calls, x => x.Endpoint == "local" && x.Method == "sendMessage" && x.Identity == 123456);
        await Assert.ThrowsAsync<OperationCanceledException>(() => previous.SendRequest(new GetUpdatesRequest()));
        Assert.DoesNotContain(http.Calls, x => x.Method == "getUpdates");
    }

    /// <summary>A→B→A identity replacement hydrates A's saved Local/uncertain state before any SDK call, without manual cache invalidation.</summary>
    /// <param name="uncertain">Whether the restored identity must remain fenced instead of sending through its saved Local endpoint.</param>
    /// <returns>A task completing after real HTTP origin/absence proves no fabricated Cloud route or generation rollback.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_returning_identity_hydrates_saved_route_before_ordinary_requests(bool uncertain)
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        var store = new TelegramEndpointStore(databases.Users, new AppConfig());
        var saved = await store.GetOrCreateAsync("endpoint-a", 123456, default);
        saved.DesiredEndpoint = saved.EffectiveEndpoint = TelegramEndpointType.Local;
        saved.Generation = 7;
        saved.MigrationState = uncertain ? TelegramEndpointMigrationState.LocalLogoutUncertain : TelegramEndpointMigrationState.Local;
        if (uncertain)
        {
            saved.LogoutEndpoint = TelegramEndpointType.Local;
            saved.LogoutAttemptedAtUtc = DateTime.UtcNow;
            saved.LastFailureCategory = "logout_uncertain";
        }
        Assert.True(await store.TrySaveAsync(saved, saved.Revision));
        var gate = new TelegramEndpointRuntimeGate(registry, store);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        Assert.False(gate.IsAvailable("endpoint-a", 123456));
        await gate.HydrateAsync("endpoint-a", 123456, default);
        registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(999888), Type = BotInstanceTypes.Owned, Enabled = true });
        await clients.GetClient("endpoint-a").SendMessage(711, "fresh identity fixture");
        registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        if (uncertain)
            await Assert.ThrowsAsync<BotTransportUnavailableException>(() => clients.GetClient("endpoint-a").SendMessage(711, "must remain fenced"));
        else
            await clients.GetClient("endpoint-a").SendMessage(711, "saved Local identity fixture");
        Assert.Equal(7, gate.GetRoute("endpoint-a", 123456).Generation);
        Assert.Equal(TelegramEndpointType.Local, gate.GetRoute("endpoint-a", 123456).Endpoint);
        Assert.DoesNotContain(http.Calls, x => x.Identity == 123456 && x.Endpoint == "cloud");
        Assert.Equal(uncertain ? 0 : 1, http.Calls.Count(x => x.Identity == 123456));
        Assert.Single(http.Calls, x => x.Identity == 999888 && x.Endpoint == "cloud");
    }

    /// <summary>Migration stops new background sends but lets one already-admitted send finish, without replay at the new endpoint.</summary>
    /// <returns>A task completing after drain waits for the original send and only the explicitly new send reaches Local.</returns>
    [Fact]
    public async Task Endpoint_inflight_send_drains_without_rerouting_or_replay()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp { BlockSend = true };
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var retained = clients.GetClient("endpoint-a");
        var sending = retained.SendMessage(711, "synthetic fixture content");
        await http.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        gate.Fence("endpoint-a", 123456);
        Assert.False(gate.TryAcquireExecution("endpoint-a", out _));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => retained.SendMessage(711, "must not dispatch"));
        var draining = gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
        Assert.False(draining.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => gate.PrepareActivation("endpoint-a", 123456, TelegramEndpointType.Local, 2));
        http.SendRelease.TrySetResult();
        await sending;
        await draining;
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
        await retained.SendMessage(711, "explicit new send");
        Assert.Equal(1, http.Calls.Count(x => x.Method == "sendMessage" && x.Endpoint == "cloud"));
        Assert.Equal(1, http.Calls.Count(x => x.Method == "sendMessage" && x.Endpoint == "local"));
    }

    /// <summary>An admitted whole handler may make a final original-generation request after fencing; unrelated work cannot inherit its lease.</summary>
    /// <returns>A task completing after handler and request ownership are independently drained.</returns>
    [Fact]
    public async Task Endpoint_admitted_handler_finishes_original_generation_and_other_bot_remains_available()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        Assert.True(gate.TryAcquireExecution("endpoint-a", out var admission));
        gate.Fence("endpoint-a", 123456);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => clients.GetClient("endpoint-a").SendMessage(711, "unadmitted"));
        using (admission.Enter()) await clients.GetClient("endpoint-a").SendMessage(711, "admitted final response");
        await clients.GetClient("endpoint-b").SendMessage(712, "independent bot");
        var draining = gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
        Assert.False(draining.IsCompleted);
        admission.Dispose();
        await draining;
        Assert.Contains(http.Calls, x => x.Identity == 654321 && x.Method == "sendMessage");
        Assert.Equal(1, http.Calls.Count(x => x.Identity == 123456 && x.Method == "sendMessage"));
    }

    /// <summary>BotFather replacement cannot let a retained facade or a durable migration control log out the replacement identity.</summary>
    /// <returns>A task completing after rejection occurs before the SDK transport is reached.</returns>
    [Fact]
    public async Task Endpoint_replaced_identity_rejects_retained_normal_and_control_clients()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var retained = clients.GetClient("endpoint-a");
        var control = clients.CreateEndpointControlClient("endpoint-a", TelegramEndpointType.Cloud, 1, 123456);
        registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(999888), Type = BotInstanceTypes.Owned, Enabled = true });
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => retained.SendMessage(711, "must not dispatch"));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => control.SendRequest(new LogOutRequest()));
        Assert.Throws<BotTransportUnavailableException>(() => clients.CreateEndpointControlClient("endpoint-a", TelegramEndpointType.Cloud, 1, 123456));
        Assert.Empty(http.Calls);
    }

    /// <summary>Disabled storefront capability checks remain read-only and ordinary disabled-tenant delivery remains unavailable.</summary>
    /// <returns>A task completing after getMe/chat capability succeeds and delivery is rejected without HTTP.</returns>
    [Fact]
    public async Task Endpoint_disabled_tenant_capability_preserves_activation_without_delivery()
    {
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "tenant-probe", Token = EndpointRuntimeToken(777888), Type = BotInstanceTypes.Tenant, Enabled = false });
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var probe = clients.GetClientForCapabilityProbe("tenant-probe");
        Assert.Equal(777888, (await probe.GetMe()).Id);
        await probe.GetChatAdministrators(-100123);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => probe.SendMessage(711, "must not deliver"));
        Assert.Throws<BotTransportUnavailableException>(() => clients.GetClient("tenant-probe"));
        Assert.DoesNotContain(http.Calls, x => x.Method == "sendMessage");
        Assert.False(registry.GetById("tenant-probe").Enabled);
    }

    /// <summary>Local files use the existing host mapping, reject traversal and obsolete lookup epochs, and retain TGFile provenance through foreground decoration.</summary>
    /// <returns>A task completing after actual read-only bytes are copied and invalid paths fail without exposure.</returns>
    [Fact]
    public async Task Endpoint_local_download_mapping_and_obsolete_file_generation()
    {
        var root = Path.Combine(Path.GetTempPath(), "endpoint-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "receipt.bin"), "synthetic file bytes");
            var registry = EndpointRuntimeRegistry();
            var gate = new TelegramEndpointRuntimeGate(registry);
            var options = new TelegramEndpointRoutingOptions { LocalFileServerRoot = "/data/local", LocalFileHostRoot = root }.ValidateAndSnapshot();
            using var http = new EndpointRuntimeHttp();
            using var clients = new BotClientProvider(registry, gate, options, http);
            var retained = clients.GetClient("endpoint-a");
            var oldFile = await retained.GetFile("synthetic-id");
            gate.Fence("endpoint-a", 123456);
            await gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(1), default);
            gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
            await using var destination = new MemoryStream();
            await Assert.ThrowsAsync<BotTransportUnavailableException>(() => retained.DownloadFile(oldFile, destination));
            var foreground = new ForegroundBoundedTelegramBotClient(retained, TelegramForegroundDeliveryPolicy.Production);
            var localFile = await foreground.GetFile("synthetic-id");
            await foreground.DownloadFile(localFile, destination);
            Assert.Equal("synthetic file bytes", Encoding.UTF8.GetString(destination.ToArray()));
            http.LocalFilePath = "/data/local/../private-secret";
            var invalidFile = await foreground.GetFile("invalid-synthetic-id");
            var failure = await Assert.ThrowsAsync<BotTransportUnavailableException>(() => foreground.DownloadFile(invalidFile, destination));
            Assert.DoesNotContain("private-secret", failure.Message);
            Assert.True(TelegramLocalFileMapper.IsReady(options));
            Assert.False(TelegramLocalFileMapper.IsReady(new TelegramEndpointRoutingOptions()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The actual runtime lifecycle joins old polling, validates destination getUpdates, and starts exactly one Local receiver without changing another bot.</summary>
    /// <returns>A task completing after real SDK polling epochs and strict identity failure behavior are observed.</returns>
    [Fact]
    public async Task Endpoint_runtime_exactly_one_receiver_and_strict_destination_readiness()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        using var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new MultiBotHostedService(registry, clients, new EndpointRuntimeNoopScheduler(),
            services.GetRequiredService<IServiceScopeFactory>(), new BotContextAccessor(), new BotRuntimeStatusStore(),
            new ConfigurationBuilder().Build(), NullLogger<MultiBotHostedService>.Instance, endpointGate: gate);
        await runtime.StartAsync(default);
        try
        {
            await http.CloudPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            gate.Fence("endpoint-a", 123456);
            await using (var lifecycle = await runtime.AcquireAsync("endpoint-a", default))
            {
                await lifecycle.StopAndWaitAsync(default);
                await gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
                var staged = EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 2);
                staged.MigrationState = TelegramEndpointMigrationState.SwitchingToLocal;
                gate.Publish(staged);
                gate.PrepareActivation("endpoint-a", 123456, TelegramEndpointType.Local, 2);
                Assert.True(await lifecycle.StartValidatedAsync(default));
                gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
            }
            await http.LocalPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, http.MaximumPollers[123456]);
            Assert.Equal(1, http.ActivePollers[654321]);
            Assert.Contains(http.Calls, x => x.Identity == 123456 && x.Endpoint == "local" && x.Method == "getUpdates" && x.ShortPoll);
            gate.Fence("endpoint-a", 123456);
            await using (var lifecycle = await runtime.AcquireAsync("endpoint-a", default))
            {
                await lifecycle.StopAndWaitAsync(default);
                await gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
                var staged = EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 3);
                staged.MigrationState = TelegramEndpointMigrationState.SwitchingToCloud;
                gate.Publish(staged);
                gate.PrepareActivation("endpoint-a", 123456, TelegramEndpointType.Cloud, 3);
                http.WrongIdentity = true;
                Assert.False(await lifecycle.StartValidatedAsync(default));
                Assert.Equal(0, http.ActivePollers[123456]);
                Assert.Equal(1, http.ActivePollers[654321]);
            }
        }
        finally
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await runtime.StopAsync(stopping.Token);
        }
    }

    /// <summary>A poll exiting under a migration fence cannot leave a dead CTS registration suppressing source recovery after failed preflight.</summary>
    /// <returns>A task completing after the actual v22 Cloud poll restarts once while another bot keeps receiving.</returns>
    [Fact]
    public async Task Endpoint_fenced_poll_completion_allows_prelogout_source_recovery()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        using var http = new EndpointRuntimeHttp { ReleaseFirstCloudPoll = true };
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        using var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new MultiBotHostedService(registry, clients, new EndpointRuntimeNoopScheduler(),
            services.GetRequiredService<IServiceScopeFactory>(), new BotContextAccessor(), new BotRuntimeStatusStore(),
            new ConfigurationBuilder().Build(), NullLogger<MultiBotHostedService>.Instance, endpointGate: gate);
        await runtime.StartAsync(default);
        try
        {
            await http.CloudPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var checking = EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1);
            checking.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
            gate.Publish(checking);
            http.CloudPollRelease.TrySetResult();
            await EndpointRuntimeUntil(() => http.ActivePollers[123456] == 0);
            var restored = EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1);
            restored.Revision = 2;
            gate.Publish(restored);
            await EndpointRuntimeUntil(() => http.Calls.Count(x => x.Identity == 123456 && x.Method == "getUpdates" && !x.ShortPoll) == 2 &&
                http.ActivePollers[123456] == 1);
            Assert.Equal(1, http.MaximumPollers[123456]);
            Assert.Equal(1, http.ActivePollers[654321]);
            Assert.DoesNotContain(http.Calls, x => x.Endpoint == "local");
        }
        finally
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await runtime.StopAsync(stopping.Token);
        }
    }

    /// <summary>Endpoint fences defer claims while independent bot lanes run, and unpausing preserves exact FIFO with no duplicate processing.</summary>
    /// <returns>A task completing after two same-user lane heads run once in their durable order.</returns>
    [Fact]
    public async Task Endpoint_scheduler_pause_preserves_durable_fifo_and_other_bot_execution()
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Fence("endpoint-a", 123456);
        var seen = new ConcurrentQueue<(string Bot, int Update)>();
        var executor = new EndpointRuntimeExecutor((item, token) => { seen.Enqueue((item.Key.BotId, item.Update.Id)); return Task.CompletedTask; });
        using var scheduler = new TelegramUpdateScheduler(databases.Inbox, executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 3 },
            NullLogger<TelegramUpdateScheduler>.Instance, endpointGate: gate);
        await scheduler.EnqueueAsync("endpoint-a", EndpointRuntimeUpdate(41, 711), default);
        await scheduler.EnqueueAsync("endpoint-a", EndpointRuntimeUpdate(42, 711), default);
        await scheduler.EnqueueAsync("endpoint-b", EndpointRuntimeUpdate(43, 712), default);
        await scheduler.StartAsync(default);
        try
        {
            await EndpointRuntimeUntil(() => seen.Any(x => x.Bot == "endpoint-b"));
            Assert.DoesNotContain(seen, x => x.Bot == "endpoint-a");
            await using (var db = databases.Users.CreateDbContext())
            {
                Assert.All(db.TelegramUpdateInbox.Where(x => x.BotId == "endpoint-a").ToArray(), x => Assert.Null(x.StartedAtUtc));
            }
            gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
            await EndpointRuntimeUntil(() => seen.Count == 3);
            Assert.Equal(new[] { 41, 42 }, seen.Where(x => x.Bot == "endpoint-a").Select(x => x.Update));
            Assert.Equal(3, seen.Distinct().Count());
        }
        finally { await scheduler.StopAsync(default); }
    }

    /// <summary>A disabled hydrated alias's held request and original handler must both drain before the shared session can log out.</summary>
    /// <returns>A task completing after deterministic request release proves no early logout, no replay and independent identity delivery.</returns>
    [Fact]
    public async Task Endpoint_identity_fence_drains_disabled_alias_request_and_handler_before_logout()
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "endpoint-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        var gate = new TelegramEndpointRuntimeGate(registry, new TelegramEndpointStore(databases.Users, new AppConfig()));
        await gate.HydrateAsync("endpoint-a", 123456, default);
        await gate.HydrateAsync("endpoint-alias", 123456, default);
        Assert.True(gate.TryAcquireExecution("endpoint-alias", out var handler));
        registry.Upsert(new BotInstance { Id = "endpoint-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = false });
        using var http = new EndpointRuntimeHttp { BlockSend = true };
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var alias = clients.GetClient("endpoint-alias");
        var sending = alias.SendMessage(711, "held disabled alias fixture");
        await http.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            gate.Fence("endpoint-a", 123456);
            await Assert.ThrowsAsync<BotTransportUnavailableException>(() => alias.SendMessage(711, "blocked alias fixture"));
            var draining = gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
            Assert.False(draining.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => gate.PrepareActivation("endpoint-a", 123456, TelegramEndpointType.Local, 2));
            Assert.DoesNotContain(http.Calls, x => x.Method == "logOut");
            await clients.GetClient("endpoint-b").SendMessage(712, "independent identity fixture");
            using (handler.Enter()) await alias.SendMessage(711, "admitted alias final response");
            http.SendRelease.TrySetResult();
            await sending;
            Assert.False(draining.IsCompleted);
            handler.Dispose();
            await draining;
            await clients.CreateEndpointControlClient("endpoint-a", TelegramEndpointType.Cloud, 1, 123456).SendRequest(new LogOutRequest());
            Assert.Single(http.Calls, x => x.Method == "logOut" && x.Identity == 123456);
            Assert.Equal(2, http.Calls.Count(x => x.Method == "sendMessage" && x.Identity == 123456 && x.Endpoint == "cloud"));
            Assert.Single(http.Calls, x => x.Method == "sendMessage" && x.Identity == 654321);
        }
        finally { http.SendRelease.TrySetResult(); handler.Dispose(); }
    }

    /// <summary>A→B→A replacement reattaches A's exact outstanding request and handler before reloading its durable route.</summary>
    /// <returns>A task completing after both original counters delay activation and the admitted handler finishes without reroute or replay.</returns>
    [Fact]
    public async Task Endpoint_returning_identity_retains_exact_request_and_handler_drain_counters()
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry, new TelegramEndpointStore(databases.Users, new AppConfig()));
        await gate.HydrateAsync("endpoint-a", 123456, default);
        Assert.True(gate.TryAcquireExecution("endpoint-a", out var handler));
        var originalEntry = handler.OwnedEntry;
        using var http = new EndpointRuntimeHttp { BlockSend = true };
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var retained = clients.GetClient("endpoint-a");
        var sending = retained.SendMessage(711, "held original identity fixture");
        await http.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(999888), Type = BotInstanceTypes.Owned, Enabled = true });
            await clients.GetClient("endpoint-a").SendMessage(711, "temporary identity fixture");
            registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
            await gate.HydrateAsync("endpoint-a", 123456, default);
            Assert.True(gate.TryAcquireExecution("endpoint-a", out var returned));
            Assert.Same(originalEntry, returned.OwnedEntry);
            returned.Dispose();
            gate.Fence("endpoint-a", 123456);
            var draining = gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
            Assert.False(draining.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => gate.PrepareActivation("endpoint-a", 123456, TelegramEndpointType.Local, 2));
            using (handler.Enter()) await retained.SendMessage(711, "original handler final response");
            http.SendRelease.TrySetResult();
            await sending;
            Assert.False(draining.IsCompleted);
            handler.Dispose();
            await draining;
            gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Local, 2));
            await retained.SendMessage(711, "new generation fixture");
            Assert.Equal(2, http.Calls.Count(x => x.Identity == 123456 && x.Endpoint == "cloud" && x.Method == "sendMessage"));
            Assert.Single(http.Calls, x => x.Identity == 123456 && x.Endpoint == "local" && x.Method == "sendMessage");
            Assert.Single(http.Calls, x => x.Identity == 999888 && x.Endpoint == "cloud" && x.Method == "sendMessage");
        }
        finally { http.SendRelease.TrySetResult(); handler.Dispose(); }
    }

    /// <summary>Active publication or ordinary hydration of another alias cannot reopen an identity's migration fence.</summary>
    /// <returns>A task completing after enabled, disabled and newly hydrated aliases all reject new handlers, API calls and identity probes without HTTP.</returns>
    [Fact]
    public async Task Endpoint_identity_fence_blocks_hydrated_disabled_and_new_alias_admission()
    {
        using var databases = new Databases();
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "endpoint-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        registry.Upsert(new BotInstance { Id = "tenant-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Tenant, Enabled = false });
        var gate = new TelegramEndpointRuntimeGate(registry, new TelegramEndpointStore(databases.Users, new AppConfig()));
        await gate.HydrateAsync("endpoint-a", 123456, default);
        await gate.HydrateAsync("endpoint-alias", 123456, default);
        await gate.HydrateAsync("tenant-alias", 123456, default);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        var owned = clients.GetClient("endpoint-alias");
        var capability = clients.GetClientForCapabilityProbe("tenant-alias");
        gate.Fence("endpoint-a", 123456);
        gate.Publish(EndpointRuntimeState("endpoint-alias", 123456, TelegramEndpointType.Cloud, 1));
        registry.Upsert(new BotInstance { Id = "new-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = false });
        await gate.HydrateAsync("new-alias", 123456, default);
        Assert.False(gate.TryAcquireExecution("endpoint-alias", out _));
        Assert.False(gate.GetRoute("endpoint-alias", 123456).Available);
        Assert.False(gate.GetRoute("new-alias", 123456).Available);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => owned.SendMessage(711, "blocked owned alias fixture"));
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => capability.GetMe());
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => clients.GetClient("new-alias").SendMessage(711, "blocked newly hydrated alias fixture"));
        Assert.Throws<BotTransportUnavailableException>(() => gate.AcquireIdentityProbe(123456));
        Assert.Empty(http.Calls);
        await clients.GetClient("endpoint-b").SendMessage(712, "independent identity fixture");
        Assert.Single(http.Calls, x => x.Identity == 654321);
    }

    /// <summary>A newer restrictive saved alias row closes already hydrated session admission, while ordinary active reads cannot undo it.</summary>
    /// <returns>A task completing after only the restrictive alias's explicit committed restoration reopens shared admission.</returns>
    [Fact]
    public async Task Endpoint_restrictive_saved_alias_closes_hydrated_identity_without_read_reopening()
    {
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "endpoint-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        gate.Publish(EndpointRuntimeState("endpoint-alias", 123456, TelegramEndpointType.Cloud, 1));
        var restrictive = EndpointRuntimeState("endpoint-alias", 123456, TelegramEndpointType.Cloud, 1);
        restrictive.Revision = 2;
        restrictive.MigrationState = TelegramEndpointMigrationState.CloudLogoutUncertain;
        gate.PublishIfUnhydrated(restrictive);
        var active = EndpointRuntimeState("endpoint-alias", 123456, TelegramEndpointType.Cloud, 1);
        active.Revision = 3;
        gate.PublishIfUnhydrated(active);
        gate.Publish(EndpointRuntimeState("endpoint-a", 123456, TelegramEndpointType.Cloud, 1));
        Assert.False(gate.IsAvailable("endpoint-a", 123456));
        Assert.False(gate.TryAcquireExecution("endpoint-alias", out _));
        Assert.Throws<BotTransportUnavailableException>(() => gate.AcquireIdentityProbe(123456));
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        await Assert.ThrowsAsync<BotTransportUnavailableException>(() => clients.GetClient("endpoint-a").SendMessage(711, "restrictive alias fixture"));
        Assert.Empty(http.Calls);
        gate.Publish(active);
        await clients.GetClient("endpoint-a").SendMessage(711, "committed source restoration fixture");
        Assert.Single(http.Calls);
    }

    /// <summary>Restrictive alias authority fences immediately without replacing the route still owned by an admitted original handler.</summary>
    /// <returns>A task completing after the original handler drains and only then can the saved restrictive replacement epoch be adopted.</returns>
    [Fact]
    public async Task Endpoint_restrictive_alias_epoch_preserves_original_handler_until_identity_drain()
    {
        var registry = EndpointRuntimeRegistry();
        registry.Upsert(new BotInstance { Id = "endpoint-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        var gate = new TelegramEndpointRuntimeGate(registry);
        Assert.True(gate.TryAcquireExecution("endpoint-a", out var admission));
        using var handler = admission;
        var restrictive = EndpointRuntimeState("endpoint-alias", 123456, TelegramEndpointType.Local, 2);
        restrictive.MigrationState = TelegramEndpointMigrationState.LocalLogoutUncertain;
        gate.PublishIfUnhydrated(restrictive);
        Assert.False(gate.GetRoute("endpoint-a", 123456).Available);
        Assert.Equal(1, gate.GetRoute("endpoint-alias", 123456).Generation);
        Assert.Equal(TelegramEndpointType.Cloud, gate.GetRoute("endpoint-alias", 123456).Endpoint);
        var draining = gate.DrainAsync("endpoint-a", 123456, TimeSpan.FromSeconds(3), default);
        Assert.False(draining.IsCompleted);
        using var http = new EndpointRuntimeHttp();
        using var clients = new BotClientProvider(registry, gate, new TelegramEndpointRoutingOptions(), http);
        using (handler.Enter()) await clients.GetClient("endpoint-a").SendMessage(711, "original handler under restrictive authority");
        handler.Dispose();
        await draining;
        gate.PublishIfUnhydrated(restrictive);
        Assert.Equal(2, gate.GetRoute("endpoint-alias", 123456).Generation);
        Assert.Equal(TelegramEndpointType.Local, gate.GetRoute("endpoint-alias", 123456).Endpoint);
        Assert.False(gate.GetRoute("endpoint-alias", 123456).Available);
        Assert.Single(http.Calls, x => x.Identity == 123456 && x.Endpoint == "cloud" && x.Method == "sendMessage");
        Assert.DoesNotContain(http.Calls, x => x.Endpoint == "local");
    }

    /// <summary>A live identity-only probe retains a replaced owner's numeric fence and remains part of another alias's drain.</summary>
    /// <returns>A task completing after the held proof is disposed, with no new same-identity admission during replacement.</returns>
    [Fact]
    public async Task Endpoint_identity_only_probe_retains_retired_fence_until_complete_drain()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        gate.GetRoute("endpoint-a", 123456);
        using var probe = gate.AcquireIdentityProbe(123456);
        gate.Fence("endpoint-a", 123456);
        registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(999888), Type = BotInstanceTypes.Owned, Enabled = true });
        registry.Upsert(new BotInstance { Id = "endpoint-alias", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = false });
        Assert.False(gate.GetRoute("endpoint-alias", 123456).Available);
        Assert.True(gate.GetRoute("endpoint-a", 999888).Available);
        Assert.Throws<BotTransportUnavailableException>(() => gate.AcquireIdentityProbe(123456));
        Assert.True(probe.IsActiveFor(gate, 123456));
        var draining = gate.DrainAsync("endpoint-alias", 123456, TimeSpan.FromSeconds(3), default);
        Assert.False(draining.IsCompleted);
        probe.Dispose();
        await draining;
        Assert.False(probe.IsActiveFor(gate, 123456));
    }

    /// <summary>Identity churn removes idle retired fences and completed probe metadata but retains an outstanding retired handler until disposal.</summary>
    [Fact]
    public void Endpoint_identity_churn_prunes_idle_retired_metadata_without_forgetting_live_leases()
    {
        var registry = EndpointRuntimeRegistry();
        var gate = new TelegramEndpointRuntimeGate(registry);
        Assert.True(gate.TryAcquireExecution("endpoint-a", out var handler));
        var original = handler.OwnedEntry.Admission;
        gate.Fence("endpoint-a", 123456);
        for (var index = 0; index < 32; index++)
        {
            var identity = 800000 + index;
            registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(identity), Type = BotInstanceTypes.Owned, Enabled = true });
            Assert.True(gate.GetRoute("endpoint-a", identity).Available);
            gate.Fence("endpoint-a", identity);
            using var probe = gate.AcquireIdentityProbe(900000 + index);
        }
        Assert.Single(original.Entries);
        handler.Dispose();
        Assert.Empty(original.Entries);
        var identities = (System.Collections.IDictionary)typeof(TelegramEndpointRuntimeGate)
            .GetField("_identities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(gate)!;
        Assert.Single(identities);
        registry.Upsert(new BotInstance { Id = "endpoint-a", Token = EndpointRuntimeToken(123456), Type = BotInstanceTypes.Owned, Enabled = true });
        Assert.True(gate.GetRoute("endpoint-a", 123456).Available);
        Assert.Single(identities);
    }

    /// <summary>Builds synthetic current identities; all tokens are deliberately nonproduction.</summary>
    /// <returns>A registry with two independent enabled owned identities.</returns>
    private static BotRegistry EndpointRuntimeRegistry() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["bots:0:id"] = "endpoint-a", ["bots:0:token"] = EndpointRuntimeToken(123456), ["bots:0:type"] = BotInstanceTypes.Owned,
        ["bots:0:isDefault"] = "true", ["bots:1:id"] = "endpoint-b", ["bots:1:token"] = EndpointRuntimeToken(654321), ["bots:1:type"] = BotInstanceTypes.Owned
    }).Build());
    /// <summary>Returns a valid-shaped fake token for the actual SDK; never contacts Telegram.</summary>
    /// <param name="identity">Synthetic positive BotFather id.</param><returns>A nonproduction fixture token.</returns>
    private static string EndpointRuntimeToken(long identity) => identity + ":" + new string('x', 35);
    /// <summary>Constructs a committed active metadata row for controlled generation transitions.</summary>
    /// <param name="bot">Synthetic internal bot id.</param><param name="identity">Synthetic BotFather id.</param>
    /// <param name="endpoint">Trusted endpoint enum.</param><param name="generation">Positive test epoch.</param>
    /// <returns>An active detached state without any token, customer id, or financial data.</returns>
    private static TelegramEndpointState EndpointRuntimeState(string bot, long identity, TelegramEndpointType endpoint, long generation) => new()
    {
        BotId = bot, TelegramBotId = identity, Generation = generation, Revision = generation,
        DesiredEndpoint = endpoint, EffectiveEndpoint = endpoint,
        MigrationState = endpoint == TelegramEndpointType.Cloud ? TelegramEndpointMigrationState.Cloud : TelegramEndpointMigrationState.Local
    };
    /// <summary>Creates a private synthetic update for the real durable inbox.</summary>
    /// <param name="id">Distinct synthetic Telegram update id.</param><param name="actor">Synthetic lane owner id.</param><returns>A message update with no production/customer content.</returns>
    private static Update EndpointRuntimeUpdate(int id, long actor) => new()
    {
        Id = id, Message = new Message { Id = id, From = new Telegram.Bot.Types.User { Id = actor, FirstName = "fixture" },
            Chat = new Chat { Id = actor, Type = Telegram.Bot.Types.Enums.ChatType.Private }, Text = "synthetic" }
    };
    /// <summary>Waits for a bounded observable runtime condition, not an incidental sleep.</summary>
    /// <param name="condition">Consumer-visible completion predicate.</param><returns>A task completing after observation.</returns>
    private static async Task EndpointRuntimeUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    /// <summary>Captures only safe request dimensions from fake HTTP; raw URLs/tokens are never retained.</summary>
    private sealed class EndpointRuntimeHttp : HttpMessageHandler
    {
        /// <summary>Payload-free request observations used to assert endpoint routing.</summary>
        public ConcurrentQueue<(long Identity, string Endpoint, string Method, bool ShortPoll)> Calls { get; } = new();
        /// <summary>Current simultaneous real SDK polls per synthetic BotFather identity.</summary>
        public ConcurrentDictionary<long, int> ActivePollers { get; } = new();
        /// <summary>Maximum observed simultaneous real SDK polls per synthetic BotFather identity.</summary>
        public ConcurrentDictionary<long, int> MaximumPollers { get; } = new();
        /// <summary>Deterministic barriers for an admitted send and both receiver origins.</summary>
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloudPollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LocalPollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Releases only the first target Cloud poll so the real SDK reaches its next fenced request.</summary>
        public TaskCompletionSource CloudPollRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Whether the first target Cloud cycle uses the explicit release barrier instead of cancellation.</summary>
        public bool ReleaseFirstCloudPoll;
        /// <summary>Whether the first send is held, identity replies intentionally mismatch, and the safe Local file fixture path.</summary>
        public bool BlockSend, WrongIdentity;
        public string LocalFilePath = "/data/local/receipt.bin";
        /// <summary>Returns real Bot API JSON envelopes for the actual v22 SDK without network or provider mutations.</summary>
        /// <param name="request">Actual SDK HTTP request; only routing/method/timeout fields are examined transiently.</param>
        /// <param name="token">Real SDK request cancellation.</param><returns>A deterministic response or cancellation.</returns>
        /// <remarks>Long polls are awaited until cancellation to exercise receiver joining. No payload is persisted or printed.</remarks>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/")
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(
                    "{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}", Encoding.UTF8, "application/json") };
            var segments = uri.AbsolutePath.Split('/');
            var identity = long.Parse(segments[1].AsSpan(3, segments[1].IndexOf(':') - 3));
            var endpoint = uri.Host == "api.telegram.org" ? "cloud" : "local";
            var method = segments[^1];
            var shortPoll = false;
            if (method == "getUpdates")
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                using var json = JsonDocument.Parse(body);
                shortPoll = !json.RootElement.TryGetProperty("timeout", out var timeout) || timeout.GetInt32() == 0;
            }
            Calls.Enqueue((identity, endpoint, method, shortPoll));
            if (method == "getUpdates" && !shortPoll)
            {
                var active = ActivePollers.AddOrUpdate(identity, 1, (_, value) => value + 1);
                MaximumPollers.AddOrUpdate(identity, active, (_, value) => Math.Max(value, active));
                if (identity == 123456) (endpoint == "cloud" ? CloudPollStarted : LocalPollStarted).TrySetResult();
                try
                {
                    if (identity == 123456 && endpoint == "cloud" && ReleaseFirstCloudPoll)
                    {
                        ReleaseFirstCloudPoll = false;
                        await CloudPollRelease.Task.WaitAsync(token);
                    }
                    else await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally { ActivePollers.AddOrUpdate(identity, 0, (_, value) => value - 1); }
            }
            if (method == "sendMessage" && BlockSend)
            {
                BlockSend = false; SendStarted.TrySetResult(); await SendRelease.Task.WaitAsync(token);
            }
            object result = method switch
            {
                "getMe" => new { id = WrongIdentity ? identity + 1 : identity, is_bot = true, first_name = "fixture", username = "fixture_bot" },
                "getWebhookInfo" => new { url = "", has_custom_certificate = false, pending_update_count = 0 },
                "getUpdates" or "getChatAdministrators" => Array.Empty<object>(),
                "getFile" => new { file_id = "synthetic-id", file_unique_id = "synthetic-unique", file_path = endpoint == "local" ? LocalFilePath : "documents/synthetic.bin" },
                "sendMessage" => new { message_id = 1, date = 1700000000, chat = new { id = 711, type = "private" }, text = "synthetic" },
                _ => true
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }), Encoding.UTF8, "application/json") };
        }
    }
    /// <summary>Runtime admission sink used when polling fixtures deliberately return no updates.</summary>
    private sealed class EndpointRuntimeNoopScheduler : ITelegramUpdateScheduler
    {
        /// <inheritdoc />
        public void StopAdmission() { }
        /// <inheritdoc />
        public Task EnqueueAsync(string botId, Update update, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected fixture update.");
    }
    /// <summary>Controlled business execution boundary using the real scheduler/inbox rather than mocking claim order.</summary>
    /// <param name="execute">Consumer-visible handler invoked exactly once per successful durable claim.</param>
    private sealed class EndpointRuntimeExecutor(Func<TelegramUpdateWorkItem, CancellationToken, Task> execute) : ITelegramUpdateExecutor
    {
        /// <inheritdoc />
        public bool IsAvailable(string botId) => true;
        /// <inheritdoc />
        public Task ExecuteAsync(TelegramUpdateWorkItem item, CancellationToken cancellationToken) => execute(item, cancellationToken);
    }
}
