using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Xunit;

/// <summary>Exercises tenant dashboards using real SQLite, real chart rendering and the Telegram SDK transport.</summary>
public sealed class TenantWeeklyDeliveryTests
{
    /// <summary>Friday's final second still addresses the older week; Saturday midnight immediately starts the newly completed week.</summary>
    /// <returns>A task after the real worker's UTC periods and two distinct dispatches have been checked.</returns>
    [Fact]
    public async Task Exact_Saturday_boundary_and_latest_only_startup_catchup()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Store("store-a", 711, 20001));
        var worker = fixture.Worker();
        var saturday = new DateTime(2026, 10, 10);
        Assert.Equal(1, await worker.ProcessOnceAsync(saturday.AddTicks(-1)));
        Assert.Equal(1, await worker.ProcessOnceAsync(saturday));
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(saturday.AddDays(2)));
        await using var db = fixture.Users.CreateDbContext();
        var periods = await db.UsageReportDispatches.OrderBy(x => x.PeriodEndUtc).ToListAsync();
        Assert.Equal(2, periods.Count);
        Assert.Equal(fixture.Analytics.ConvertIranTimeToUtc(saturday.AddDays(-7)), periods[0].PeriodEndUtc);
        Assert.Equal(fixture.Analytics.ConvertIranTimeToUtc(saturday), periods[1].PeriodEndUtc);
        Assert.Equal(TimeSpan.FromSeconds(20), TenantWeeklyUsageReportHostedService.GetNextScanDelay(saturday.AddSeconds(-20)));
        Assert.Equal(TimeSpan.FromMinutes(1), TenantWeeklyUsageReportHostedService.GetNextScanDelay(saturday));

        using var late = new Fixture();
        await late.SeedAsync(Store("late-start", 712, 20002));
        Assert.Equal(1, await late.Worker().ProcessOnceAsync(saturday.AddDays(3)));
        await using var lateDb = late.Users.CreateDbContext();
        Assert.Equal(fixture.Analytics.ConvertIranTimeToUtc(saturday), (await lateDb.UsageReportDispatches.SingleAsync()).PeriodEndUtc);
    }

    /// <summary>One owner gets independent storefront totals, another owner gets only theirs, and disabled configured stores remain reportable.</summary>
    /// <returns>A task after actual multipart SDK destinations, captions, PNG payloads and persisted states have been checked.</returns>
    [Fact]
    public async Task Multi_store_isolation_assistant_only_and_configured_disabled_eligibility()
    {
        using var fixture = new Fixture();
        var disabled = Store("store-disabled", 711, 20002); disabled.Enabled = false;
        var reset = Store("reset", 711, 20004); reset.Token = null; reset.Username = null; reset.TelegramBotId = null;
        var noUsername = Store("no-username", 711, 20005); noUsername.Username = " ";
        var noIdentity = Store("no-identity", 711, 20006); noIdentity.TelegramBotId = null;
        var noOwner = Store("no-owner", 0, 20007);
        var owned = Store("owned-persisted", 711, 20008); owned.Type = BotInstanceTypes.Owned;
        var createdAtBoundary = Store("new-at-boundary", 711, 20009);
        createdAtBoundary.CreatedAtUtc = fixture.Analytics.ConvertIranTimeToUtc(new DateTime(2026, 10, 10));
        var createdAfterBoundary = Store("new-after-boundary", 711, 20010);
        createdAfterBoundary.CreatedAtUtc = fixture.Analytics.ConvertIranTimeToUtc(new DateTime(2026, 10, 11));
        await fixture.SeedAsync(Store("store-a", 711, 20001), disabled, Store("store-b", 712, 20003),
            reset, noUsername, noIdentity, noOwner, owned, createdAtBoundary, createdAfterBoundary);
        var end = new DateTime(2026, 10, 10);
        await using (var db = fixture.Users.CreateDbContext())
        {
            db.TenantBotOrders.AddRange(Order(fixture, "store-a", 711, 12345, end.AddDays(-1)),
                Order(fixture, "store-disabled", 711, 67890, end.AddDays(-1)),
                Order(fixture, "store-b", 712, 54321, end.AddDays(-1)),
                Order(fixture, "store-a", 999, 999999, end.AddDays(-1)));
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, await fixture.Worker().ProcessOnceAsync(end));
        var sends = fixture.Http.Sends.ToArray();
        Assert.Equal(3, sends.Length);
        Assert.All(sends, send =>
        {
            Assert.Equal(90001, send.BotIdentity);
            Assert.InRange(send.Caption.Length, 1, 1024);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, send.Photo.Take(8).ToArray());
        });
        foreach (var (id, owner, sales) in new[] { ("store-a", 711L, 12345L), ("store-disabled", 711L, 67890L), ("store-b", 712L, 54321L) })
        {
            var send = Assert.Single(sends, x => x.Caption.Contains("@" + id, StringComparison.Ordinal));
            Assert.Equal(owner, send.ChatId);
            Assert.Contains(sales.ToString("N0", CultureInfo.CurrentCulture), send.Caption);
            Assert.DoesNotContain(999999L.ToString("N0", CultureInfo.CurrentCulture), send.Caption);
            Assert.DoesNotContain("@reset", send.Caption);
        }
        await using var verify = fixture.Users.CreateDbContext();
        Assert.Equal(3, await verify.UsageReportDispatches.CountAsync(x => x.Status == UsageReportDispatchStatuses.Sent));
        Assert.Equal(4, await verify.TenantBotOrders.CountAsync());
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
    }

    /// <summary>A durable SendStarted remains consumed while HTTP is in flight, across concurrent cycles and a recreated worker.</summary>
    /// <returns>A task after one and only one real SDK request and one positive acknowledgement.</returns>
    [Fact]
    public async Task Concurrent_cycles_and_restart_never_duplicate_send_started_or_sent()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Store("store-a", 711, 20001));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Respond = async (send, _) => { entered.TrySetResult(); await release.Task; return Success(send.ChatId); };
        var end = new DateTime(2026, 10, 10);
        var first = fixture.Worker().ProcessOnceAsync(end);
        var second = fixture.Worker().ProcessOnceAsync(end);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(end));
            await using var db = fixture.Users.CreateDbContext();
            var row = await db.UsageReportDispatches.SingleAsync();
            Assert.Equal(UsageReportDispatchStatuses.SendStarted, row.Status);
            Assert.Null(row.LeaseUntilUtc);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, (await Task.WhenAll(first, second)).Sum());
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(end.AddDays(1)));
        Assert.Single(fixture.Http.Sends);

        // A crash can leave only SendStarted behind. Its age must never authorize another HTTP request.
        using var crashed = new Fixture();
        await crashed.SeedAsync(Store("crashed", 713, 20003));
        var key = TenantWeeklyUsageReportHostedService.CreateReportKey(end, "crashed", 713, 20003);
        Assert.True(await crashed.Store.TryStartTenantSendAsync(key, crashed.Analytics.ConvertIranTimeToUtc(end.AddDays(-7)),
            crashed.Analytics.ConvertIranTimeToUtc(end), default));
        await using (var db = crashed.Users.CreateDbContext())
            await db.UsageReportDispatches.ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow.AddDays(-30)));
        Assert.Equal(0, await crashed.Worker().ProcessOnceAsync(end.AddDays(2)));
        Assert.Empty(crashed.Http.Sends);
    }

    /// <summary>Transport uncertainty and server errors are terminal for automatic sending, even after restart.</summary>
    /// <param name="responseMode">Zero throws a network error, -1 a timeout; positive values are actual Telegram API status codes.</param>
    /// <returns>A task after the durable uncertainty state and absence of duplicate requests have been checked.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(408)]
    [InlineData(500)]
    public async Task Ambiguous_delivery_never_blindly_resends(int responseMode)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Store("store-a", 711, 20001));
        fixture.Http.Respond = (_, _) => responseMode switch
        {
            0 => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture-secret-must-not-persist")),
            -1 => Task.FromException<HttpResponseMessage>(new TaskCanceledException("fixture-secret-must-not-persist")),
            _ => Task.FromResult(Failure(responseMode))
        };
        var end = new DateTime(2026, 10, 10);
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(end));
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(end.AddMinutes(1)));
        Assert.Single(fixture.Http.Sends);
        await using var db = fixture.Users.CreateDbContext();
        var row = await db.UsageReportDispatches.SingleAsync();
        Assert.Equal(UsageReportDispatchStatuses.DeliveryUncertain, row.Status);
        Assert.DoesNotContain("fixture-secret", row.LastError);
    }

    /// <summary>A definite rate limit permits a later single retry, whereas a blocked owner cannot prevent another store's delivery.</summary>
    /// <returns>A task after retry/permanent-rejection state transitions and independent destinations have been checked.</returns>
    [Fact]
    public async Task Definite_429_retries_but_403_is_terminal_and_does_not_block_other_owners()
    {
        using var rateLimited = new Fixture();
        await rateLimited.SeedAsync(Store("limited", 711, 20001));
        rateLimited.Http.Respond = (send, number) => Task.FromResult(number == 1 ? Failure(429) : Success(send.ChatId));
        var end = new DateTime(2026, 10, 10);
        Assert.Equal(0, await rateLimited.Worker().ProcessOnceAsync(end));
        await using (var db = rateLimited.Users.CreateDbContext())
            Assert.Equal(UsageReportDispatchStatuses.Failed, (await db.UsageReportDispatches.SingleAsync()).Status);
        Assert.Equal(1, await rateLimited.Worker().ProcessOnceAsync(end.AddMinutes(1)));
        Assert.Equal(0, await rateLimited.Worker().ProcessOnceAsync(end.AddMinutes(2)));
        Assert.Equal(2, rateLimited.Http.Sends.Count);

        using var blocked = new Fixture();
        await blocked.SeedAsync(Store("blocked", 711, 20001), Store("healthy", 712, 20002));
        blocked.Http.Respond = (send, _) => Task.FromResult(send.ChatId == 711 ? Failure(403) : Success(send.ChatId));
        Assert.Equal(1, await blocked.Worker().ProcessOnceAsync(end));
        Assert.Equal(0, await blocked.Worker().ProcessOnceAsync(end.AddMinutes(1)));
        Assert.Equal(2, blocked.Http.Sends.Count);
        await using var verify = blocked.Users.CreateDbContext();
        Assert.Equal(1, await verify.UsageReportDispatches.CountAsync(x => x.Status == UsageReportDispatchStatuses.Rejected));
        Assert.Equal(1, await verify.UsageReportDispatches.CountAsync(x => x.Status == UsageReportDispatchStatuses.Sent));
    }

    /// <summary>Persisted owner or BotFather replacement rejects a stale snapshot; inventory-to-send changes are also caught before the barrier.</summary>
    /// <returns>A task after both worker and direct assistant authorization paths have made no HTTP requests.</returns>
    [Fact]
    public async Task Ownership_changes_between_inventory_and_delivery_are_rejected_without_fallback()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Store("store-a", 711, 20001));
        fixture.BeforeAssistantResolve = () =>
        {
            using var db = fixture.Users.CreateDbContext();
            db.BotInstances.ExecuteUpdate(s => s.SetProperty(x => x.OwnerTelegramUserId, (long?)999));
            fixture.BeforeAssistantResolve = null;
        };
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(new DateTime(2026, 10, 10)));
        using var scope = fixture.Provider.CreateScope();
        var assistant = scope.ServiceProvider.GetRequiredService<SalesAssistantService>();
        using var png = new MemoryStream(new byte[] { 1, 2, 3 });
        Assert.Null(await assistant.SendTenantWeeklyReportPhotoAsync("store-a", 711, 20001, png, "caption"));
        await using (var db = fixture.Users.CreateDbContext())
            await db.BotInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.TelegramBotId, (long?)20002));
        Assert.Null(await assistant.SendTenantWeeklyReportPhotoAsync("store-a", 999, 20001, png, "caption"));
        Assert.Empty(fixture.Http.Sends);
        await using var verify = fixture.Users.CreateDbContext();
        Assert.Empty(await verify.UsageReportDispatches.ToListAsync());
    }

    /// <summary>A known unavailable assistant after inventory releases the no-send boundary for a later safe retry.</summary>
    /// <returns>A task after zero first-cycle HTTP requests and one restored-route delivery.</returns>
    [Fact]
    public async Task Route_disappearing_before_HTTP_is_a_known_no_send_and_safe_to_retry()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Store("store-a", 711, 20001));
        fixture.BeforeAssistantResolve = () =>
        {
            fixture.Registry.Upsert(new BotInstance
            { Id = "assistant", Type = BotInstanceTypes.SalesAssistant, Enabled = false, Token = Token(90001) });
            fixture.BeforeAssistantResolve = null;
        };
        var end = new DateTime(2026, 10, 10);
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(end));
        Assert.Empty(fixture.Http.Sends);
        await using (var db = fixture.Users.CreateDbContext())
            Assert.Equal(UsageReportDispatchStatuses.Failed, (await db.UsageReportDispatches.SingleAsync()).Status);
        fixture.Registry.Upsert(new BotInstance
        { Id = "assistant", Type = BotInstanceTypes.SalesAssistant, Enabled = true, Token = Token(90001) });
        Assert.Equal(1, await fixture.Worker().ProcessOnceAsync(end.AddMinutes(1)));
        Assert.Single(fixture.Http.Sends);
    }

    /// <summary>Removing or disabling Sales Assistant must never substitute an available owned or tenant transport.</summary>
    /// <returns>A task after the exact-route API and worker have made no Telegram requests.</returns>
    [Fact]
    public async Task Unavailable_assistant_has_no_owned_or_tenant_fallback()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Store("store-a", 711, 20001));
        fixture.Registry.Upsert(new BotInstance { Id = "assistant", Type = BotInstanceTypes.SalesAssistant, Enabled = false, Token = Token(90001) });
        Assert.Equal(0, await fixture.Worker().ProcessOnceAsync(new DateTime(2026, 10, 10)));
        using var scope = fixture.Provider.CreateScope();
        using var image = new MemoryStream(new byte[] { 1 });
        Assert.Null(await scope.ServiceProvider.GetRequiredService<SalesAssistantService>()
            .SendTenantWeeklyReportPhotoAsync("store-a", 711, 20001, image, "caption"));
        Assert.Empty(fixture.Http.Sends);
    }

    /// <summary>Creates one configured storefront with an exact persisted owner and numeric BotFather identity.</summary>
    /// <param name="id">Internal users.db storefront id, also used as its fixture username.</param>
    /// <param name="owner">Telegram owner user id.</param>
    /// <param name="identity">Numeric BotFather bot id.</param>
    /// <returns>A detached configurable tenant fixture row.</returns>
    private static BotInstance Store(string id, long owner, long identity) => new()
    { Id = id, Username = id, Token = Token(identity), TelegramBotId = identity, OwnerTelegramUserId = owner,
      Type = BotInstanceTypes.Tenant, CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) };

    /// <summary>Creates a synthetic token accepted by the SDK without reaching a real server.</summary>
    /// <param name="identity">Numeric fixture bot identity.</param>
    /// <returns>A fake token used exclusively with the captured HTTP handler.</returns>
    private static string Token(long identity) => identity.ToString(CultureInfo.InvariantCulture) + ":" + new string('a', 35);

    /// <summary>Creates an authoritative completed sale; no wallet mutation or financial service is invoked.</summary>
    /// <param name="fixture">Fixture supplying Tehran-to-UTC conversion.</param>
    /// <param name="botId">Exact storefront internal id.</param>
    /// <param name="owner">Owner captured on the order.</param>
    /// <param name="sales">Gross successful sale in whole toman.</param>
    /// <param name="completedIran">Completion timestamp within the current Tehran week.</param>
    /// <returns>A detached fulfilled order for analytics isolation checks.</returns>
    private static TenantBotOrder Order(Fixture fixture, string botId, long owner, long sales, DateTime completedIran) => new()
    { OrderId = Guid.NewGuid().ToString("N"), TenantBotId = botId, OwnerTelegramUserId = owner, CustomerTelegramUserId = 123,
      IsFulfilled = true, SalePriceToman = sales, FulfilledAtUtc = fixture.Analytics.ConvertIranTimeToUtc(completedIran) };

    /// <summary>Returns Telegram's successful photo acknowledgement through the real SDK response parser.</summary>
    /// <param name="chatId">Exact owner destination recorded from multipart upload.</param>
    /// <returns>A fresh successful HTTP response; the SDK owns disposal.</returns>
    private static HttpResponseMessage Success(long chatId) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result = new { message_id = 4242, date = 1, chat = new { id = chatId, type = "private" } } }), Encoding.UTF8, "application/json") };

    /// <summary>Returns a definite Telegram rejection or server error with a deliberately untrusted description.</summary>
    /// <param name="code">Telegram HTTP/API status, such as 429, 403 or 500.</param>
    /// <returns>A fresh failure response interpreted by the real SDK.</returns>
    private static HttpResponseMessage Failure(int code) => new((HttpStatusCode)code)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = code, description = "fixture-secret-must-not-persist", parameters = new { retry_after = 1 } }), Encoding.UTF8, "application/json") };

    /// <summary>Captures the assistant identity, owner chat, caption and actual rendered PNG from SDK multipart serialization.</summary>
    private sealed record Upload(long BotIdentity, long ChatId, string Caption, byte[] Photo);

    /// <summary>Real SDK HTTP boundary with controllable replies and no production network access.</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        /// <summary>Thread-safe observed attempts, including rejected/ambiguous requests.</summary>
        public ConcurrentQueue<Upload> Sends { get; } = new();
        /// <summary>Optional asynchronous response selector; permits in-flight concurrency barriers.</summary>
        public Func<Upload, int, Task<HttpResponseMessage>>? Respond { get; set; }
        private int _count;
        /// <summary>Reads genuine multipart parts and returns the selected Telegram response.</summary>
        /// <param name="request">SDK-owned request containing the assistant token and multipart payload; never logged.</param>
        /// <param name="cancellationToken">SDK request cancellation token.</param>
        /// <returns>Selected synthetic response for the real SDK parser.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var multipart = Assert.IsAssignableFrom<MultipartFormDataContent>(request.Content);
            var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? photo = null;
            foreach (var part in multipart)
            {
                var bytes = await part.ReadAsByteArrayAsync(cancellationToken);
                parts[part.Headers.ContentDisposition!.Name!.Trim('"')] = bytes;
                // The photo field is an attach:// reference; the rendered PNG is the multipart file itself.
                if (part.Headers.ContentDisposition.FileName != null)
                    photo = bytes;
            }
            string Text(string name) => Encoding.UTF8.GetString(parts[name]);
            var path = request.RequestUri!.AbsolutePath;
            var botIdentity = long.Parse(path.AsSpan(path.IndexOf("/bot", StringComparison.Ordinal) + 4,
                path.IndexOf(':') - path.IndexOf("/bot", StringComparison.Ordinal) - 4), CultureInfo.InvariantCulture);
            var send = new Upload(botIdentity, long.Parse(Text("chat_id"), CultureInfo.InvariantCulture), Text("caption"),
                photo ?? throw new InvalidOperationException("No chart file was uploaded."));
            Sends.Enqueue(send);
            var number = Interlocked.Increment(ref _count);
            return Respond == null ? Success(send.ChatId) : await Respond(send, number);
        }
    }

    /// <summary>Owns only a temporary users.db, real production services and captured SDK HTTP clients.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "TenantWeeklyDelivery-" + Guid.NewGuid().ToString("N"));
        /// <summary>Independent contexts for the isolated real SQLite users database.</summary>
        public UserDbContextFactory Users { get; }
        /// <summary>Shared production aggregator with temporary log paths.</summary>
        public UsageAnalyticsService Analytics { get; }
        /// <summary>Production durable dispatch store.</summary>
        public UsageReportDispatchStore Store { get; }
        /// <summary>Runtime identity registry containing assistant and owned transports.</summary>
        public BotRegistry Registry { get; }
        /// <summary>HTTP capture boundary shared by all clients in this fixture.</summary>
        public CaptureHandler Http { get; } = new();
        /// <summary>Scope provider resolving real SalesAssistantService instances.</summary>
        public ServiceProvider Provider { get; }
        /// <summary>Optional inventory-to-send mutation invoked while resolving the real assistant service.</summary>
        public Action? BeforeAssistantResolve { get; set; }
        /// <summary>Creates isolated persisted state and real SDK transports without a credentials database.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "users.db"), Pooling = false }.ToString();
            Users = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>().UseSqlite(connection).Options);
            using (var db = Users.CreateDbContext()) { db.Database.EnsureCreated(); db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotToken"] = Token(80001), ["UserActivityLogFilePath"] = Path.Combine(_directory, "activity-{date}.jsonl"),
                ["SalesAssistantBot:Id"] = "assistant", ["SalesAssistantBot:Token"] = Token(90001), ["SalesAssistantBot:Enabled"] = "true"
            }).Build();
            Registry = new BotRegistry(configuration);
            var clients = new BotClientProvider(Registry, bot => new TelegramBotClient(
                new TelegramBotClientOptions(bot.Token) { RetryCount = 0 }, new HttpClient(Http, disposeHandler: false)));
            Analytics = new UsageAnalyticsService(configuration, Users, NullLogger<UsageAnalyticsService>.Instance);
            Store = new UsageReportDispatchStore(Users);
            var services = new ServiceCollection();
            services.AddScoped(provider =>
            {
                BeforeAssistantResolve?.Invoke();
                return new SalesAssistantService(Users, Registry, clients, provider, null!, NullLogger<SalesAssistantService>.Instance);
            });
            Provider = services.BuildServiceProvider();
        }
        /// <summary>Creates a fresh production worker sharing only the durable database and controlled transport.</summary>
        /// <returns>An unstarted worker for deterministic ProcessOnceAsync cycles.</returns>
        public TenantWeeklyUsageReportHostedService Worker() => new(Users, Analytics, new UsageReportChartRenderer(), Store,
            Provider.GetRequiredService<IServiceScopeFactory>(), Registry, NullLogger<TenantWeeklyUsageReportHostedService>.Instance);
        /// <summary>Persists storefront inventory and mirrors it into the runtime registry.</summary>
        /// <param name="bots">Detached fixture rows, including intentionally ineligible inventory.</param>
        /// <returns>A task completing after all rows are committed.</returns>
        public async Task SeedAsync(params BotInstance[] bots)
        {
            await using var db = Users.CreateDbContext();
            db.BotInstances.AddRange(bots);
            await db.SaveChangesAsync();
            foreach (var bot in bots) Registry.Upsert(bot);
        }
        /// <summary>Disposes scopes and transport, then removes only this fixture's random temporary directory.</summary>
        public void Dispose()
        {
            Provider.Dispose();
            Http.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
