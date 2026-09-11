using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

public sealed partial class ConcurrencyTests
{
    private const long RoutingOwnerId = 6910211284;

    [Fact]
    public async Task Sequence_2990_xui_home_callback_ack_timeout_is_bounded_and_business_continues()
    {
        using var databases = new Databases();
        // Inject a 60 ms acknowledgement budget so this regression proves the bound without sleeping the real
        // two-second production timeout. If the production budget ever leaked back in, the elapsed assertion below
        // would fail instead of silently passing after a two-second lane stall.
        var (provider, _, _) = IncidentProvider(
            databases,
            interactionTimeouts: new TelegramInteractionTimeouts { CallbackAnswer = TimeSpan.FromMilliseconds(60) });
        await using (provider)
        {
            await using var scope = provider.CreateAsyncScope();
            var flow = scope.ServiceProvider.GetRequiredService<XuiV3BotFlowService>();
            var client = new BlockingAckClient();
            var callback = new CallbackQuery
            {
                Id = "sequence-2990",
                Data = XuiV3PurchaseCallbacks.Home(),
                From = new Telegram.Bot.Types.User { Id = 711 },
                Message = new Message { MessageId = 17, Chat = new Chat { Id = 711 } }
            };
            var sw = Stopwatch.StartNew();
            var handled = await flow.TryHandleCallbackAsync(
                client,
                callback,
                new CredUser { TelegramUserId = 711, ChatID = 711 },
                new User { Id = 711 },
                new ReplyKeyboardRemove(),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.True(handled);
            await client.AckStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            // Comfortably below the real two-second production budget, so a regression that re-inherited the long
            // Telegram timeout fails here rather than passing after a two-second stall, while still tolerating a slow
            // test machine.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"elapsed={sw.Elapsed}");
            Assert.Contains(client.Texts, x => x.Contains("منوی اصلی", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Callback_ack_transport_and_stale_failures_are_best_effort()
    {
        var scenarios = new Func<ITelegramBotClient>[]
        {
            () => new ThrowingAckClient(new RequestException("transport failed")),
            () => new ThrowingAckClient(new ApiRequestException("Bad Request: query is too old and response timeout expired", 400)),
            () => new ThrowingAckClient(new ApiRequestException("Bad Request: query ID is invalid", 400))
        };

        foreach (var makeClient in scenarios)
        {
            var client = makeClient();
            var sw = Stopwatch.StartNew();
            var result = await TelegramCallbackAnswerPolicy.TryAnswerAsync(client, "ack-test", cancellationToken: CancellationToken.None);
            sw.Stop();
            Assert.False(result);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"elapsed={sw.Elapsed}");
        }
    }

    [Fact]
    public async Task Callback_ack_outer_cancellation_propagates_instead_of_becoming_local_timeout()
    {
        var client = new BlockingAckClient();
        using var outer = new CancellationTokenSource();
        // A long explicit budget makes outer cancellation the only possible trigger, so the assertion cannot race
        // against the local timeout and become flaky.
        var task = TelegramCallbackAnswerPolicy.TryAnswerAsync(
            client, "outer-cancel", cancellationToken: outer.Token, timeout: TimeSpan.FromSeconds(30));
        await client.AckStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        outer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }
    [Fact]
    public void Production_interaction_timeouts_remain_two_and_five_seconds()
    {
        // Guards the production latency policy against a future test optimisation accidentally changing it: the
        // shared production budgets and the policy constant must stay exactly two and five seconds.
        Assert.Equal(TimeSpan.FromSeconds(2), TelegramInteractionTimeouts.Production.CallbackAnswer);
        Assert.Equal(TimeSpan.FromSeconds(5), TelegramInteractionTimeouts.Production.MandatoryJoin);
        Assert.Equal(TimeSpan.FromSeconds(2), TelegramCallbackAnswerPolicy.Timeout);

        // Parallel-safety proof: a test-scoped instance with millisecond values must not affect the shared
        // production instance, so concurrently running tests cannot race over a process-wide timeout.
        var custom = new TelegramInteractionTimeouts
        {
            CallbackAnswer = TimeSpan.FromMilliseconds(20),
            MandatoryJoin = TimeSpan.FromMilliseconds(30)
        };
        Assert.Equal(TimeSpan.FromMilliseconds(20), custom.CallbackAnswer);
        Assert.Equal(TimeSpan.FromMilliseconds(30), custom.MandatoryJoin);
        Assert.Equal(TimeSpan.FromSeconds(2), TelegramInteractionTimeouts.Production.CallbackAnswer);
        Assert.Equal(TimeSpan.FromSeconds(5), TelegramInteractionTimeouts.Production.MandatoryJoin);
    }

    [Fact]
    public async Task Mandatory_join_uses_one_overall_timeout_budget_and_fails_closed()
    {
        using var databases = new Databases();
        var client = new BlockingMembershipClient();
        // A 60 ms overall budget keeps this regression fast; production uses the five-second default.
        var service = BuildBareTelegramService(
            databases,
            client,
            out var accessor,
            new TelegramInteractionTimeouts { MandatoryJoin = TimeSpan.FromMilliseconds(60) });
        using var context = accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-join", Type = BotInstanceTypes.Owned, Username = "owned_join" },
            Client = client
        });
        var sw = Stopwatch.StartNew();
        var joined = await InvokeMandatoryJoinAsync(service, new[] { "@a", "@b", "@c" }, 711, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.False(joined);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(40), $"elapsed={sw.Elapsed}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"elapsed={sw.Elapsed}");
        // The blocked first channel is abandoned when the single overall budget expires, so the loop never reaches
        // the second or third channel. One call proves the budget is overall rather than per channel.
        Assert.Equal(1, client.GetChatMemberCalls);
    }

    [Fact]
    public async Task Mandatory_join_outer_cancellation_propagates()
    {
        using var databases = new Databases();
        var client = new BlockingMembershipClient();
        // A long budget keeps outer cancellation the only trigger, so this test cannot race the local timeout.
        var service = BuildBareTelegramService(
            databases,
            client,
            out var accessor,
            new TelegramInteractionTimeouts { MandatoryJoin = TimeSpan.FromSeconds(30) });
        using var context = accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-join", Type = BotInstanceTypes.Owned },
            Client = client
        });
        using var outer = new CancellationTokenSource();
        var task = InvokeMandatoryJoinAsync(service, new[] { "@a", "@b" }, 711, outer.Token);
        await client.MemberStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        outer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task Mandatory_join_transport_failure_is_prompt_and_fail_closed()
    {
        using var databases = new Databases();
        var client = new ThrowingMembershipClient(new RequestException("membership transport failed"));
        var service = BuildBareTelegramService(databases, client, out var accessor);
        using var context = accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-join", Type = BotInstanceTypes.Owned }, Client = client
        });
        var joined = await InvokeMandatoryJoinAsync(service, new[] { "@a", "@b" }, 711, CancellationToken.None);
        Assert.False(joined);
        Assert.Equal(1, client.GetChatMemberCalls);
    }
    [Fact]
    public async Task Same_user_fifo_is_preserved_while_blocked_ack_releases_lane_after_local_timeout()
    {
        using var databases = new Databases();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var firstFinished = 0;
        var client = new BlockingAckClient();
        var executor = new Executor(async (item, token) =>
        {
            if (item.Update.Id == 1)
            {
                firstStarted.TrySetResult();
                // Tiny budget: the first update still blocks in the acknowledgement until the bound expires, which is
                // what the FIFO assertion depends on, but the lane is released in milliseconds instead of two seconds.
                await TelegramCallbackAnswerPolicy.TryAnswerAsync(
                    client, "lane-ack", cancellationToken: token, timeout: TimeSpan.FromMilliseconds(40));
                Interlocked.Exchange(ref firstFinished, 1);
            }
            else
            {
                Assert.Equal(1, Volatile.Read(ref firstFinished));
                secondStarted.TrySetResult();
            }
        });
        using var scheduler = Create(databases, executor, concurrency: 4);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", Update(1, 711), default);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await scheduler.EnqueueAsync("owned", Update(2, 711), default);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await scheduler.StopAsync(default); }
    }
    [Fact]
    public async Task Different_users_on_same_bot_still_run_concurrently()
    {
        using var databases = new Databases();
        var bothEntered = Signal();
        var release = Signal();
        var active = 0;
        var max = 0;
        var executor = new Executor(async (_, token) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref max, now);
            if (now == 2) bothEntered.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref active); }
        });
        using var scheduler = Create(databases, executor, concurrency: 2);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", Update(11, 711), default);
            await scheduler.EnqueueAsync("owned", Update(12, 722), default);
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, max);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
    }
    [Fact]
    public async Task Owner_route_explicit_precedence_beats_newer_history()
    {
        using var databases = new Databases();
        var (resolver, _, _, _) = CreateRoutingResolver(databases);
        var tenant = Tenant("tenant-route-explicit", "OwnedA");
        await SeedOwnerStateAsync(databases, "OwnedA", RoutingOwnerId, DateTime.UtcNow.AddMinutes(-10));
        await SeedOwnerStateAsync(databases, "OwnedB", RoutingOwnerId, DateTime.UtcNow);

        var resolved = await resolver.ResolveAsync(tenant, RoutingOwnerId);

        Assert.True(resolved.IsAvailable);
        Assert.Equal("OwnedA", resolved.Bot.Id);
        Assert.Equal("explicit", resolved.ResolutionSource);
    }

    [Fact]
    public async Task Owner_route_historical_fallback_is_latest_and_deterministic()
    {
        using var databases = new Databases();
        var (resolver, _, _, _) = CreateRoutingResolver(databases);
        var tenant = Tenant("tenant-route-history");
        await SeedOwnerStateAsync(databases, "OwnedA", RoutingOwnerId, DateTime.UtcNow.AddMinutes(-5));
        await SeedOwnerStateAsync(databases, "OwnedB", RoutingOwnerId, DateTime.UtcNow);
        var resolved = await resolver.ResolveAsync(tenant, RoutingOwnerId);
        Assert.Equal("OwnedB", resolved.Bot.Id);
        Assert.Equal("historical_bot_user_state", resolved.ResolutionSource);
    }
    [Fact]
    public async Task Invalid_explicit_owner_route_falls_back_to_valid_history()
    {
        using var databases = new Databases();
        var (resolver, registry, _, _) = CreateRoutingResolver(databases);
        var ownedA = registry.Bots.Single(x => x.Id == "OwnedA");
        ownedA.Enabled = false;
        var tenant = Tenant("tenant-invalid-explicit", "OwnedA");
        await SeedOwnerStateAsync(databases, "OwnedB", RoutingOwnerId, DateTime.UtcNow);

        var resolved = await resolver.ResolveAsync(tenant, RoutingOwnerId);

        Assert.True(resolved.IsAvailable);
        Assert.Equal("OwnedB", resolved.Bot.Id);
        Assert.Equal("historical_bot_user_state", resolved.ResolutionSource);
    }

    [Fact]
    public async Task Owner_route_without_evidence_is_unavailable_and_ignores_tenant_history()
    {
        using var databases = new Databases();
        var (resolver, _, _, _) = CreateRoutingResolver(databases);
        var tenant = Tenant("tenant-no-evidence");
        await SeedOwnerStateAsync(databases, tenant.Id, RoutingOwnerId, DateTime.UtcNow);

        var resolved = await resolver.ResolveAsync(tenant, RoutingOwnerId);

        Assert.False(resolved.IsAvailable);
        Assert.Null(resolved.Bot);
        Assert.Equal("unavailable", resolved.ResolutionSource);
    }
    [Fact]
    public async Task Owner_sale_actual_send_uses_historical_owned_transport_only()
    {
        using var databases = new Databases();
        var (resolver, registry, clients, botClients) = CreateRoutingResolver(databases);
        var tenant = Tenant("tenant-6910211284");
        await SeedTenantAsync(databases, tenant);
        await SeedOwnerStateAsync(databases, "OwnedB", RoutingOwnerId, DateTime.UtcNow);
        var delivery = new TenantOrderNotificationDeliveryService(
            registry, botClients, new CredentialsStore(databases.Credentials), null!, null!, resolver, databases.Users);
        var order = RoutingOrder(tenant.Id);

        var messageId = await delivery.SendAsync(order, TenantOrderNotificationKinds.OwnerSaleNotification, default);

        Assert.NotNull(messageId);
        Assert.Single(clients["OwnedB"].Texts);
        Assert.False(clients.TryGetValue("OwnedA", out var a) && a.Texts.Count > 0);
        Assert.False(clients.TryGetValue(tenant.Id, out var t) && t.Texts.Count > 0);
    }

    [Theory]
    [InlineData(TenantStorefrontFundingAlertKinds.UnderfundedTransition)]
    [InlineData(TenantStorefrontFundingAlertKinds.CustomerAttempt)]
    public async Task Funding_alert_actual_send_uses_same_historical_owned_transport(string kind)
    {
        using var databases = new Databases();
        var (resolver, _, clients, _) = CreateRoutingResolver(databases);
        var tenant = Tenant("tenant-funding-route");
        await SeedTenantAsync(databases, tenant);
        await SeedOwnerStateAsync(databases, "OwnedB", RoutingOwnerId, DateTime.UtcNow);
        var delivery = new TenantStorefrontFundingAlertDeliveryService(
            resolver, databases.Users, new CredentialsStore(databases.Credentials));
        var alert = new TenantStorefrontFundingAlert
        {
            TenantBotId = tenant.Id, TenantBotUsername = "tenant_route", OwnerTelegramUserId = RoutingOwnerId,
            Kind = kind, MinimumSiteWalletToman = 50_000, BotBalanceToman = 0, SiteWalletToman = 0
        };

        Assert.NotNull(await delivery.SendAsync(alert, default));
        Assert.Single(clients["OwnedB"].Texts);
        Assert.False(clients.TryGetValue("OwnedA", out var a) && a.Texts.Count > 0);
    }
    [Fact]
    public async Task Tenant_creation_persists_owner_notification_route_only_from_owned_context()
    {
        using var databases = new Databases();
        var stores = new TenantStoreStore(databases.Users, new ConfigurationBuilder().Build());
        var accessor = new BotContextAccessor();
        using (accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "OwnedA", Type = BotInstanceTypes.Owned }
        }))
        {
            var created = await stores.CreateAsync(RoutingOwnerId, Guid.NewGuid().ToString("N"));
            Assert.Equal("OwnedA", created!.TenantOwnerNotificationBotId);
        }

        using (accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "tenant-context", Type = BotInstanceTypes.Tenant }
        }))
        {
            var created = await stores.CreateAsync(RoutingOwnerId + 1, Guid.NewGuid().ToString("N"));
            Assert.Null(created!.TenantOwnerNotificationBotId);
        }
    }
    [Fact]
    public async Task Legacy_owner_management_establishes_route_once_and_rejects_wrong_owner()
    {
        using var databases = new Databases();
        var stores = new TenantStoreStore(databases.Users, new ConfigurationBuilder().Build());
        var tenant = Tenant("tenant-legacy-management");
        await SeedTenantAsync(databases, tenant);
        var accessor = new BotContextAccessor();
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "OwnedA", Type = BotInstanceTypes.Owned } }))
        {
            Assert.False(await stores.EstablishOwnerNotificationRouteFromCurrentOwnedBotAsync(tenant.Id, RoutingOwnerId + 99));
            Assert.True(await stores.EstablishOwnerNotificationRouteFromCurrentOwnedBotAsync(tenant.Id, RoutingOwnerId));
        }
        using (accessor.Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "OwnedB", Type = BotInstanceTypes.Owned } }))
            Assert.False(await stores.EstablishOwnerNotificationRouteFromCurrentOwnedBotAsync(tenant.Id, RoutingOwnerId));

        await using var db = databases.Users.CreateDbContext();
        Assert.Equal("OwnedA", (await db.BotInstances.SingleAsync(x => x.Id == tenant.Id)).TenantOwnerNotificationBotId);
    }
    [Theory]
    [InlineData(400, "Bad Request: chat not found", "telegram_chat_not_found")]
    [InlineData(403, "Forbidden: bot was blocked by the user", "telegram_bot_blocked_by_user")]
    [InlineData(403, "Forbidden: user is deactivated", "telegram_user_deactivated")]
    [InlineData(400, "Bad Request: user is deactivated", "telegram_user_deactivated")]
    [InlineData(403, "Forbidden: write access denied", "telegram_forbidden")]
    [InlineData(400, "Bad Request: arbitrary provider detail", "telegram_api_400_other")]
    [InlineData(429, "Too Many Requests", "telegram_api_429")]
    [InlineData(500, "Internal Server Error", "telegram_api_5xx")]
    [InlineData(502, "Bad Gateway", "telegram_api_5xx")]
    public void Telegram_delivery_failure_classifier_emits_only_safe_codes(int code, string message, string expected)
    {
        var result = TelegramDeliveryFailureClassifier.Classify(new ApiRequestException(message, code));
        Assert.Equal(expected, result);
        Assert.DoesNotContain("arbitrary provider detail", result, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Builds a production <see cref="TelegramBotService"/> instance whose UX-only interaction budgets can be
    /// shrunk by tests.
    /// </summary>
    /// <param name="databases">Temporary database fixture backing the user-state and credentials dependencies.</param>
    /// <param name="client">Fake Telegram client used by the test to observe acknowledgement and membership calls.</param>
    /// <param name="accessor">Receives the bot context accessor the caller must push a runtime context onto.</param>
    /// <param name="timeouts">
    /// Optional immutable interaction budgets. When null production defaults apply, so callback acknowledgement is
    /// bounded at two seconds and mandatory-join verification at one overall five-second budget.
    /// </param>
    /// <returns>A configured service instance ready for the private callback and mandatory-join paths under test.</returns>
    private static TelegramBotService BuildBareTelegramService(
        Databases databases,
        ITelegramBotClient client,
        out BotContextAccessor accessor,
        TelegramInteractionTimeouts? timeouts = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UserActivityLogEnabled"] = "false",
            ["UserActivityLogFilePath"] = Path.Combine(databases.DirectoryPath, "lane-activity.jsonl")
        }).Build();
        accessor = new BotContextAccessor();
        return new TelegramBotService(
            client, new UserWorkflowStore(databases.Users), new UserStateStore(databases.Users),
            new CredentialsStore(databases.Credentials), configuration, NullLogger<TelegramBotService>.Instance,
            // broadcast/nowpayments/hooshpay/tetraminator/uniquepay/atlaspay pairs plus the two availability seams.
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!,
            // x-ui purchase/session/admin flows, tenant and sales-assistant services.
            null!, null!, null!, null!, null!, null!,
            new UserActivityLogService(configuration),
            // analytics, chart renderer, wallet ledger, notification, gozargah, registry, runtime status.
            null!, null!, null!, null!, null!, null!, null!, null!,
            accessor, null!, timeouts);
    }

    private static Task<bool> InvokeMandatoryJoinAsync(
        TelegramBotService service, IEnumerable<string> channels, long userId, CancellationToken token)
    {
        var method = typeof(TelegramBotService).GetMethod("isJoinedToChannel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(service, new object[] { channels, userId, token })!;
    }
    private static (TenantOwnerNotificationTransportResolver Resolver, BotRegistry Registry,
        ConcurrentDictionary<string, StorefrontClient> Clients, BotClientProvider Provider)
        CreateRoutingResolver(Databases databases)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["bots:0:id"] = "OwnedA", ["bots:0:username"] = "owned_a", ["bots:0:token"] = Token(41001),
            ["bots:0:type"] = BotInstanceTypes.Owned, ["bots:0:enabled"] = "true", ["bots:0:isDefault"] = "true",
            ["bots:1:id"] = "OwnedB", ["bots:1:username"] = "owned_b", ["bots:1:token"] = Token(41002),
            ["bots:1:type"] = BotInstanceTypes.Owned, ["bots:1:enabled"] = "true", ["bots:1:isDefault"] = "false",
            ["salesAssistantBot:id"] = "assistant", ["salesAssistantBot:username"] = "assistant_bot",
            ["salesAssistantBot:token"] = Token(41003), ["salesAssistantBot:enabled"] = "true"
        }).Build();
        var registry = new BotRegistry(configuration);
        var clients = new ConcurrentDictionary<string, StorefrontClient>(StringComparer.OrdinalIgnoreCase);
        var provider = new BotClientProvider(registry, bot => clients.GetOrAdd(bot.Id, _ => new StorefrontClient()));
        var resolver = new TenantOwnerNotificationTransportResolver(registry, databases.Users, provider);
        return (resolver, registry, clients, provider);
    }

    private static BotInstance Tenant(string id, string? explicitRoute = null) => new()
    {
        Id = id, Type = BotInstanceTypes.Tenant, Enabled = true,
        OwnerTelegramUserId = RoutingOwnerId, TenantOwnerNotificationBotId = explicitRoute,
        Username = id.Replace("tenant-", "tenant_"), BrandName = "routing tenant", CreatedAtUtc = DateTime.UtcNow
    };
    private static async Task SeedTenantAsync(Databases databases, BotInstance tenant)
    {
        await using var db = databases.Users.CreateDbContext();
        if (!await db.BotInstances.AnyAsync(x => x.Id == tenant.Id))
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedOwnerStateAsync(
        Databases databases, string botId, long ownerId, DateTime updatedAt)
    {
        await using var db = databases.Users.CreateDbContext();
        db.BotUserStates.Add(new BotUserState
        {
            BotId = botId, TelegramUserId = ownerId,
            CreatedAtUtc = updatedAt.AddMinutes(-1), UpdatedAtUtc = updatedAt
        });
        await db.SaveChangesAsync();
    }

    private static TenantBotOrder RoutingOrder(string tenantBotId) => new()
    {
        OrderId = "routing-order", TenantBotId = tenantBotId, TenantBotUsername = "tenant_route",
        OwnerTelegramUserId = RoutingOwnerId, CustomerTelegramUserId = 9001, CustomerChatId = 9001,
        SalePriceToman = 150_000, BaseCostToman = 100_000, ProfitToman = 50_000,
        OwnerWalletDelta = 50_000, OwnerBalanceBefore = 200_000, OwnerBalanceAfter = 250_000,
        PaymentProvider = "tenant_card", PaymentStatus = TenantBotOrderStatuses.Fulfilled,
        IsFulfilled = true, CreatedAtUtc = DateTime.UtcNow
    };
    private sealed class BlockingAckClient : StorefrontClient
    {
        public TaskCompletionSource AckStarted { get; } = Signal();

        public override async Task<TResponse> MakeRequestAsync<TResponse>(
            IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is AnswerCallbackQueryRequest)
            {
                AckStarted.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            return await base.MakeRequestAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowingAckClient(Exception exception) : StorefrontClient
    {
        public override Task<TResponse> MakeRequestAsync<TResponse>(
            IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is AnswerCallbackQueryRequest)
                return Task.FromException<TResponse>(exception);
            return base.MakeRequestAsync(request, cancellationToken);
        }
    }
    private sealed class BlockingMembershipClient : StorefrontClient
    {
        public TaskCompletionSource MemberStarted { get; } = Signal();
        public int GetChatMemberCalls;

        public override async Task<TResponse> MakeRequestAsync<TResponse>(
            IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetChatMemberRequest)
            {
                Interlocked.Increment(ref GetChatMemberCalls);
                MemberStarted.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            return await base.MakeRequestAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowingMembershipClient(Exception exception) : StorefrontClient
    {
        public int GetChatMemberCalls;
        public override Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetChatMemberRequest)
            {
                Interlocked.Increment(ref GetChatMemberCalls);
                return Task.FromException<TResponse>(exception);
            }
            return base.MakeRequestAsync(request, cancellationToken);
        }
    }
}

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task Owner_notification_worker_route_unavailable_is_retryable_pre_send_and_financially_isolated()
    {
        using var databases = new Databases();
        var (orderId, _) = await SeedFulfilledOrderWithNotificationAsync(
            databases, "owner-route-unavailable", TenantOrderNotificationKinds.OwnerSaleNotification);
        await using var before = databases.Users.CreateDbContext();
        var ledgerBefore = await before.TenantBotLedgerEntries.CountAsync();
        var debitsBefore = await before.Set<SiteWalletDebitOperation>().CountAsync();

        var worker = new TenantOrderNotificationWorker(
            databases.Users, new OwnerRouteUnavailableOrderSender(),
            NullLogger<TenantOrderNotificationWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantOrderNotifications.SingleAsync(x => x.TenantBotOrderId == orderId);
        Assert.Equal(TenantOrderNotificationStatuses.Pending, row.Status);
        Assert.Equal(OwnerNotificationTransportUnavailableException.SafeReason, row.LastError);
        Assert.Null(row.SendStartedAtUtc);
        Assert.NotNull(row.NextAttemptAtUtc);
        Assert.True((await verify.TenantBotOrders.SingleAsync(x => x.Id == orderId)).IsFulfilled);
        Assert.Equal(ledgerBefore, await verify.TenantBotLedgerEntries.CountAsync());
        Assert.Equal(debitsBefore, await verify.Set<SiteWalletDebitOperation>().CountAsync());
    }

    [Fact]
    public async Task Funding_worker_route_unavailable_is_retryable_pre_send()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-funding-route-unavailable");
        await InsertAlertRowAsync(databases, tenant, new TenantStorefrontFundingAlert
        {
            BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition",
            TenantBotId = tenant.Id, OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username,
            Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition, EpisodeNumber = 1,
            MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Pending
        });
        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.TenantStorefrontFundingAlertStates.Add(new TenantStorefrontFundingAlertState
            { TenantBotId = tenant.Id, IsUnderfunded = true, EpisodeNumber = 1, UpdatedAtUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var worker = new TenantStorefrontFundingAlertWorker(
            databases.Users, new OwnerRouteUnavailableFundingSender(),
            NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
        Assert.Equal(TenantStorefrontFundingAlertStatuses.Pending, row.Status);
        Assert.Equal(OwnerNotificationTransportUnavailableException.SafeReason, row.LastError);
        Assert.Null(row.SendStartedAtUtc);
        Assert.NotNull(row.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Owner_route_migration_preserves_legacy_rows_without_backfill_and_history_still_resolves()
    {
        const string previous = "20260910012628_AddAtlasPayGateway";
        const string current = "20260910184123_AddTenantOwnerNotificationRoute";
        using var databases = new Databases(initialize: false);
        await using var users = databases.Users.CreateDbContext();
        var migrator = users.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);

        await users.Database.ExecuteSqlRawAsync("""
            INSERT INTO BotInstances (Id, Type, OwnerTelegramUserId, Enabled, IsDefault, TenantPriceMarkupPercent,
                TenantMandatoryJoinEnabled, TenantCardPaymentEnabled, TenantHooshPayEnabled, TenantNowPaymentsEnabled,
                TenantTetraminatorEnabled, TenantUniquePayEnabled, CreatedAtUtc)
            VALUES ('OwnedA', 'owned', NULL, 1, 1, 0, 0, 0, 1, 1, 1, 1, '2026-09-01 00:00:00'),
                   ('OwnedB', 'owned', NULL, 1, 0, 0, 0, 0, 1, 1, 1, 1, '2026-09-01 00:00:00'),
                   ('tenant-legacy-route', 'tenant', 6910211284, 1, 0, 27, 0, 0, 1, 1, 1, 1, '2026-09-01 00:00:00');
            """);
        await users.Database.ExecuteSqlRawAsync("""
            INSERT INTO BotUserStates (BotId, TelegramUserId, LastFreeAcc, AccountCounter, PendingAccountCount,
                LastFreeNationalAcc, LastFreeNormalAcc, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('OwnedB', 6910211284, '0001-01-01', 0, 0, '0001-01-01', '0001-01-01',
                '2026-09-01 00:00:00', '2026-09-10 00:00:00');
            """);
        await users.Database.ExecuteSqlRawAsync("""
            INSERT INTO TenantBotOrders (OrderId, TenantBotId, TenantBotUsername, OwnerTelegramUserId,
                CustomerTelegramUserId, CustomerChatId, OrderKind, PaymentProvider, PaymentStatus,
                AccountCount, IsFulfilled, IsOwnerCredited, OwnerWalletDelta,
                SalePriceToman, BaseCostToman, ProfitToman, CreatedAtUtc)
            VALUES ('legacy-route-order', 'tenant-legacy-route', 'legacy_route', 6910211284, 55, 55,
                'purchase', 'tenant_card', 'fulfilled', 1, 1, 1, 50000, 150000, 100000, 50000,
                '2026-09-01 00:00:00');
            """);

        await migrator.MigrateAsync(current);
        // The owner-route migration under test is no longer the newest one, so advance to the full latest schema before
        // reading through the current EF model: the model always expects every applied column, and a test that pins an
        // earlier migration could only use raw SQL for those reads. Legacy seeded rows must survive this step unchanged.
        await migrator.MigrateAsync();
        var tenant = await users.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "tenant-legacy-route");
        Assert.Null(tenant.TenantOwnerNotificationBotId);
        Assert.Equal(27, tenant.TenantPriceMarkupPercent);
        Assert.Equal(1, await users.BotUserStates.CountAsync(x => x.TelegramUserId == RoutingOwnerId));
        var order = await users.TenantBotOrders.AsNoTracking().SingleAsync(x => x.OrderId == "legacy-route-order");
        Assert.True(order.IsFulfilled);
        Assert.Equal(150000, order.SalePriceToman);
        Assert.Equal(100000, order.BaseCostToman);
        Assert.Equal(50000, order.ProfitToman);
        // The tenant card-to-card provisional-delivery migration must not backfill anything: a historical fulfilled
        // order keeps "none" and no temporary account identity, so deploying it cannot resurrect old receipts.
        Assert.Equal(TenantCardProvisionalStates.None, order.ProvisionalDeliveryState);
        Assert.Null(order.ProvisionalAccountEmail);
        Assert.Null(order.ProvisionalCreatedAtUtc);
        // Historical financial and fulfillment state is untouched by the new migration.
        Assert.True(order.IsFulfilled);
        Assert.True(order.IsOwnerCredited);

        var applied = (await users.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Contains(current, applied);
        Assert.Contains(previous, applied);
        Assert.Contains("20260911000006_AddTenantCardProvisionalDelivery", applied);
        var previousIndex = applied.IndexOf(previous);
        var currentIndex = applied.IndexOf(current);
        Assert.True(previousIndex >= 0 && previousIndex < currentIndex, "owner-route migration must follow the AtlasPay migration");
        Assert.Contains("20260911023015_AddTenantCardProvisionalOperation", applied);
        Assert.Contains("20260911035150_AddProvisionalFinalizationFreeze", applied);
        // The provisional series only adds a saga-state table and then nullable freeze columns on it, so none of it can
        // rewrite the legacy rows asserted above. Ordering is asserted rather than a hard-coded "is latest" name so an
        // unrelated future migration does not fail a test about backfill behaviour.
        var provisionalDeliveryIndex = applied.IndexOf("20260911000006_AddTenantCardProvisionalDelivery");
        var sagaStateIndex = applied.IndexOf("20260911023015_AddTenantCardProvisionalOperation");
        var finalizeFreezeIndex = applied.IndexOf("20260911035150_AddProvisionalFinalizationFreeze");
        Assert.True(provisionalDeliveryIndex >= 0 && provisionalDeliveryIndex < sagaStateIndex,
            "the provisional saga-state migration must follow the provisional-delivery schema");
        Assert.True(sagaStateIndex < finalizeFreezeIndex,
            "the finalize freeze migration must follow the provisional saga-state table it extends");

        var (resolver, _, _, _) = CreateRoutingResolver(databases);
        var resolved = await resolver.ResolveAsync(tenant, RoutingOwnerId);
        Assert.True(resolved.IsAvailable);
        Assert.Equal("OwnedB", resolved.Bot.Id);
        Assert.Equal("historical_bot_user_state", resolved.ResolutionSource);
    }

    private sealed class OwnerRouteUnavailableOrderSender : ITenantOrderNotificationSender
    {
        public Task<int?> SendAsync(TenantBotOrder order, string kind, CancellationToken cancellationToken) =>
            Task.FromException<int?>(new OwnerNotificationTransportUnavailableException());
    }

    private sealed class OwnerRouteUnavailableFundingSender : ITenantStorefrontFundingAlertSender
    {
        public Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken) =>
            Task.FromException<int?>(new OwnerNotificationTransportUnavailableException());
    }
}
