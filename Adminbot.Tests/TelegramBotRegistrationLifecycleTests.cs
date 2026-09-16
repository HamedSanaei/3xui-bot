using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Xunit;

/// <summary>
/// Regression tests for the Telegram bot registration lifecycle across the registry and the shared output pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Production evidence for this fixture: many tenant receivers start successfully (for example
/// <c>tenant-6052930127</c>, <c>tenant-6910211284</c>, <c>tenant-8161518157</c>) while the shared sender only ever reported
/// <c>BotId=vpnetiranbot</c> with <c>ActiveBots=1</c>, which was read as "tenant bots are not registered into the pipeline".
/// </para>
/// <para>
/// These tests pin both halves of that question. Membership is registry state: which bots exist, which are enabled, and
/// which of them the pipeline may deliver to. <c>ActiveBots</c> is not membership at all - it counts the bots with a job in
/// flight at one instant, so a quiet storefront legitimately reports one while the owned bot carries the operator-log
/// stream. The tests therefore assert registry membership, per-bot lane registration, and the delivery census, and they
/// assert that a tenant storefront and the owned bot travel the same code path.
/// </para>
/// <para>
/// No real Telegram call, socket, polling loop, or token is used.
/// </para>
/// </remarks>
public sealed class TelegramBotRegistrationLifecycleTests : IDisposable
{
    /// <summary>Internal id of the configured owned bot used as the registry default.</summary>
    private const string OwnedBotId = "vpnetiranbot";

    /// <summary>Internal id of the tenant storefront registered by these tests.</summary>
    private const string TenantBotId = "tenant-6052930127";

    /// <summary>Fixture-owned temporary directory holding this test's users.db.</summary>
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "adminbot-bot-registration-" + Guid.NewGuid().ToString("N"));

    /// <summary>Per-bot fake transports proving which bot's credential a delivery used.</summary>
    private readonly Dictionary<string, RecordingClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Formatted log lines captured so census fields can be asserted directly.</summary>
    private readonly List<string> _logs = new();

    /// <summary>Operation-local users.db factory used by the sender and the registry.</summary>
    private readonly UserDbContextFactory _factory;

    /// <summary>Runtime registry under test; the source of truth for membership and availability.</summary>
    private readonly BotRegistry _registry;

    /// <summary>Transport provider that resolves the fixture's recording client per bot id.</summary>
    private readonly BotClientProvider _bots;

    /// <summary>Started sender instance, stopped and released in <see cref="Dispose" />.</summary>
    private TelegramSenderService _sender;

    /// <summary>
    /// Creates a migrated users.db plus a registry holding one enabled owned bot, and a per-bot transport map.
    /// </summary>
    /// <remarks>
    /// The owned bot comes from in-memory configuration, exactly like production brand bots, while tenant bots are added
    /// by the tests through the same <c>Upsert</c> and database-load paths production uses.
    /// </remarks>
    public TelegramBotRegistrationLifecycleTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(SqliteOperation.ConnectionString(Path.Combine(_directory, "users.db"))).Options);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bots:0:Id"] = OwnedBotId,
                ["Bots:0:Username"] = "vpnetiranbot",
                ["Bots:0:Token"] = "000000:owned-probe-token",
                ["Bots:0:Enabled"] = "true",
                ["Bots:0:IsDefault"] = "true",
                ["Bots:0:Type"] = BotInstanceTypes.Owned
            })
            .Build();

        _registry = new BotRegistry(configuration);
        _bots = new BotClientProvider(_registry, bot => Transport(bot.Id));
    }

    /// <summary>
    /// Regression: a tenant storefront and the configured owned bot occupy the same registry with their own identities.
    /// </summary>
    /// <remarks>
    /// The failure this protects against is a registry that collapses unknown ids onto the default owned bot, which would
    /// make every tenant storefront share one entry and explain a pipeline that only ever reports the owned bot.
    /// </remarks>
    [Fact]
    public void Tenant_and_owned_bots_keep_separate_registry_entries()
    {
        _registry.Upsert(Tenant(TenantBotId, enabled: true, ownerId: 6052930127));

        var census = _registry.Describe();

        Assert.Equal(2, census.Total);
        Assert.Equal(1, census.Owned);
        Assert.Equal(1, census.Tenant);
        Assert.Equal(1, census.EnabledTenant);
        Assert.Equal(TenantBotId, _registry.GetById(TenantBotId).Id);
        Assert.Equal(OwnedBotId, _registry.GetById(OwnedBotId).Id);
        Assert.Equal(2, _registry.Bots.Count);

        // The documented fallback: an unknown id resolves to the default owned bot, which is exactly why membership must
        // always be verified by id comparison and never inferred from a non-null result.
        Assert.Equal(OwnedBotId, _registry.GetById("tenant-does-not-exist").Id);
    }

    /// <summary>
    /// Regression: every tenant row in users.db reaches the registry through the startup hydration path, and the census
    /// reports how many of them are actually enabled.
    /// </summary>
    /// <remarks>
    /// A receiver can only start for a bot that is in the registry, so a tenant missing here would explain a storefront
    /// whose receiver never began - the opposite of the reported evidence, which is why the census is asserted explicitly.
    /// </remarks>
    [Fact]
    public async Task Database_hydration_registers_every_tenant_and_reports_its_census()
    {
        using (var seed = _factory.CreateDbContext())
        {
            seed.BotInstances.Add(TenantRow("tenant-6910211284", enabled: true, ownerId: 6910211284));
            seed.BotInstances.Add(TenantRow("tenant-8161518157", enabled: false, ownerId: 8161518157));
            await seed.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
            await _registry.LoadTenantBotsFromDatabaseAsync(db);

        var census = _registry.Describe();
        Assert.Equal(3, census.Total);
        Assert.Equal(2, census.Tenant);
        Assert.Equal(1, census.EnabledTenant);
        Assert.Equal("tenant-6910211284", _registry.GetById("tenant-6910211284").Id);
        Assert.True(_registry.GetById("tenant-6910211284").Enabled);
        Assert.False(_registry.GetById("tenant-8161518157").Enabled);
    }

    /// <summary>
    /// Regression: the sender names exactly why queued output for one bot cannot be handed to a worker.
    /// </summary>
    /// <remarks>
    /// The pump selects jobs only for registry bots that are enabled, so this classifier is the difference between
    /// "the storefront is quiet" and "the storefront's output can never be delivered". It is asserted directly so the rule
    /// cannot drift from the census and warning that both use it.
    ///
    /// An unknown id is reported as <c>bot_id_mismatch</c> rather than as missing, because <see cref="BotRegistry.GetById" />
    /// answers an unknown id with the default owned bot. That fallback is why the classifier compares the resolved id with
    /// the requested one: without that comparison an unregistered storefront would look like a perfectly healthy owned bot.
    /// </remarks>
    [Fact]
    public void Lane_eligibility_names_missing_disabled_and_mismatched_bots()
    {
        _registry.Upsert(Tenant(TenantBotId, enabled: true, ownerId: 6052930127));
        _registry.Upsert(Tenant("tenant-8161518157", enabled: false, ownerId: 8161518157));

        Assert.Null(TelegramSenderService.DescribeLaneIneligibility(OwnedBotId, _registry));
        Assert.Null(TelegramSenderService.DescribeLaneIneligibility(TenantBotId, _registry));
        Assert.Equal("bot_disabled", TelegramSenderService.DescribeLaneIneligibility("tenant-8161518157", _registry));
        Assert.Equal("bot_id_mismatch", TelegramSenderService.DescribeLaneIneligibility("tenant-9999999999", _registry));
        Assert.Equal("bot_id_missing", TelegramSenderService.DescribeLaneIneligibility("  ", _registry));
        Assert.Equal("registry_unavailable", TelegramSenderService.DescribeLaneIneligibility(TenantBotId, null));
    }

    /// <summary>
    /// Regression: a tenant storefront's output is delivered through the same shared sender as the owned bot, using the
    /// tenant's own transport, and the tenant is registered as a pipeline lane.
    /// </summary>
    /// <returns>A task completing after one tenant delivery and its assertions.</returns>
    /// <remarks>
    /// This is the direct regression for "only vpnetiranbot is active". If the pipeline were owned-only, the tenant job
    /// would never be selected, the tenant transport would never be called, and no lane or <c>BotId=tenant-…</c> line would
    /// exist. The completion line still reports <c>ActiveBots=1</c>, which is the point: that field measures in-flight work,
    /// not membership.
    /// </remarks>
    [Fact]
    public async Task Tenant_storefront_output_is_delivered_through_the_shared_pipeline()
    {
        _registry.Upsert(Tenant(TenantBotId, enabled: true, ownerId: 6052930127));
        var sender = await StartSenderAsync();

        await sender.EnqueueAsync(TenantBotId,
            new SendMessageRequest { ChatId = 6052930127, Text = "tenant reply" }, false, CancellationToken.None);

        Assert.Equal(1, await WaitForSentCountAsync(1));
        Assert.Equal(1, Attempts(TenantBotId));
        Assert.Equal(0, Attempts(OwnedBotId));

        // The tenant id is the delivery identity in the shared pipeline, and its lane was registered exactly once.
        Assert.Contains(_logs, line => line.Contains($"BotId={TenantBotId}", StringComparison.Ordinal));
        Assert.Contains(_logs, line => line.Contains($"Telegram sender lane registered. BotId={TenantBotId}", StringComparison.Ordinal));
        Assert.Equal(1, sender.RegisteredLaneCount);

        var completed = Assert.Single(_logs, line => line.Contains("Telegram output completed.", StringComparison.Ordinal));
        Assert.Contains($"BotId={TenantBotId}", completed, StringComparison.Ordinal);
        Assert.Contains("ActiveBots=1", completed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: the delivery census reports registry membership, lane registration, and the queued workload together.
    /// </summary>
    /// <returns>A task completing after one census line has been produced.</returns>
    /// <remarks>
    /// The census is the operational answer to "is this storefront part of the shared pipeline": it separates registered
    /// bots from lanes this process has actually carried and from bots currently holding queued output.
    /// </remarks>
    [Fact]
    public async Task Delivery_census_reports_registry_lanes_and_queued_workload()
    {
        _registry.Upsert(Tenant(TenantBotId, enabled: true, ownerId: 6052930127));
        var sender = await StartSenderAsync();

        await sender.EnqueueAsync(TenantBotId,
            new SendMessageRequest { ChatId = 6052930127, Text = "tenant reply" }, false, CancellationToken.None);
        Assert.Equal(1, await WaitForSentCountAsync(1));

        Assert.Contains(_logs, line =>
            line.Contains("Telegram output census.", StringComparison.Ordinal) &&
            line.Contains("RegistryBots=2", StringComparison.Ordinal) &&
            line.Contains("TenantRegistryBots=1", StringComparison.Ordinal) &&
            line.Contains("EnabledRegistryBots=2", StringComparison.Ordinal) &&
            line.Contains("UndeliverableBots=0", StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression: output queued for a bot the registry cannot deliver to is reported once and retained.
    /// </summary>
    /// <returns>A task completing after the retention window and its assertions.</returns>
    /// <remarks>
    /// This is the "receiver started but never registered" detector. The rows must stay queued: enabling or re-registering
    /// the bot has to deliver them, so failing or deleting them would silently drop customer output that a configuration fix
    /// could still send. The notice is bounded to one per bot per window so a persistent misconfiguration cannot flood the
    /// journal.
    /// </remarks>
    [Fact]
    public async Task Output_queued_for_an_unregistered_bot_is_reported_once_and_retained()
    {
        var sender = await StartSenderAsync();

        using (var seed = _factory.CreateDbContext())
        {
            seed.TelegramDeliveryJobs.Add(new TelegramDeliveryJob
            {
                BotId = "tenant-ghost",
                Kind = nameof(SendMessageRequest),
                Payload = "{}",
                Priority = (int)TelegramWorkPriority.Normal,
                Status = "queued",
                Attempts = 0,
                CreatedAtUtc = DateTime.UtcNow,
                NotBeforeUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await seed.SaveChangesAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // The reason is bot_id_mismatch, not "not registered": GetById answers an unknown id with the default owned bot, so
        // the id comparison is what proves the storefront is absent from the registry.
        Assert.Single(_logs, line => line.Contains("Reason=bot_id_mismatch", StringComparison.Ordinal));
        Assert.Contains(_logs, line => line.Contains("Retained=true", StringComparison.Ordinal));
        Assert.Contains(_logs, line =>
            line.Contains("Telegram output census.", StringComparison.Ordinal) &&
            line.Contains("UndeliverableBots=1", StringComparison.Ordinal));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.TelegramDeliveryJobs.CountAsync(x => x.Status == "queued"));
    }

    /// <summary>Starts the real sender so durable admission, the pump, and the workers run.</summary>
    /// <returns>The started sender, owned by this fixture and stopped in <see cref="Dispose" />.</returns>
    private async Task<TelegramSenderService> StartSenderAsync()
    {
        _sender = new TelegramSenderService(_factory, _bots, _registry,
            new TelegramPerformanceOptions { WorkerCount = 1, SendTimeoutSeconds = 1 },
            new TelegramWorkQueue(new TelegramPerformanceOptions()), new SinkLogger(_logs));
        await _sender.StartAsync(CancellationToken.None);
        return _sender;
    }

    /// <summary>Waits until the durable store holds at least the expected number of delivered rows.</summary>
    /// <param name="expected">Number of jobs the test has admitted so far.</param>
    /// <returns>The observed number of sent rows, so the caller can assert it exactly.</returns>
    private async Task<int> WaitForSentCountAsync(int expected)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var db = _factory.CreateDbContext();
            var sent = await db.TelegramDeliveryJobs.CountAsync(x => x.Status == "sent");
            if (sent >= expected) return sent;
            await Task.Delay(25);
        }

        await using var final = _factory.CreateDbContext();
        return await final.TelegramDeliveryJobs.CountAsync(x => x.Status == "sent");
    }

    /// <summary>Creates a tenant bot instance for the registry under test.</summary>
    /// <param name="id">Internal runtime bot id, for example <c>tenant-6052930127</c>.</param>
    /// <param name="enabled">Whether the storefront currently allows receiving and delivery.</param>
    /// <param name="ownerId">Telegram user id of the owning colleague; the bot id embeds it.</param>
    /// <returns>A persisted-shape tenant instance with its own token.</returns>
    private static BotInstance Tenant(string id, bool enabled, long ownerId) => new()
    {
        Id = id,
        Username = id + "_bot",
        Token = ownerId.ToString() + ":" + new string('t', 35),
        Type = BotInstanceTypes.Tenant,
        OwnerTelegramUserId = ownerId,
        Enabled = enabled
    };

    /// <summary>Creates a persisted tenant row for the database hydration test.</summary>
    /// <param name="id">Internal runtime bot id stored on the row.</param>
    /// <param name="enabled">Whether the storefront is enabled in the database.</param>
    /// <param name="ownerId">Telegram user id of the owning colleague.</param>
    /// <returns>A new tenant row; the caller saves it.</returns>
    private static BotInstance TenantRow(string id, bool enabled, long ownerId) => new()
    {
        Id = id,
        Username = id + "_bot",
        Token = ownerId.ToString() + ":" + new string('u', 35),
        Type = BotInstanceTypes.Tenant,
        OwnerTelegramUserId = ownerId,
        Enabled = enabled
    };

    /// <summary>Reads how many requests reached one bot's transport without creating it.</summary>
    /// <param name="botId">Internal runtime bot id whose transport is inspected.</param>
    /// <returns>Attempt count, or zero when that bot's transport was never resolved at all.</returns>
    /// <remarks>
    /// Zero attempts for the owned bot is a meaningful assertion - it means a tenant delivery did not silently use the
    /// owned bot's credential - so absence must read as zero instead of throwing.
    /// </remarks>
    private int Attempts(string botId)
        => _clients.TryGetValue(botId, out var client) ? client.Attempts : 0;

    /// <summary>Resolves (and caches) the recording transport for one bot id.</summary>
    /// <param name="botId">Internal runtime bot id the transport belongs to.</param>
    /// <returns>The stable recording client for that bot.</returns>
    private RecordingClient Transport(string botId)
    {
        if (!_clients.TryGetValue(botId, out var client))
        {
            client = new RecordingClient();
            _clients[botId] = client;
        }

        return client;
    }

    /// <summary>Stops the sender and releases this fixture's SQLite pools and temporary directory.</summary>
    public void Dispose()
    {
        try { _sender?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        SqliteTestPools.ClearForDirectory(_directory);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Counts sends per request kind and answers with a synthetic Telegram payload.</summary>
    /// <remarks>
    /// One instance exists per bot id, which is what lets a test prove that a tenant delivery used the tenant's own
    /// transport rather than the owned bot's.
    /// </remarks>
    private sealed class RecordingClient : ITelegramBotClient
    {
        /// <summary>Number of requests that reached this transport.</summary>
        public int Attempts;

        /// <inheritdoc />
        public bool LocalBotServer => false;

        /// <inheritdoc />
        public long BotId => 1;

        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }

        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;

        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest { add { } remove { } }

        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived { add { } remove { } }

        /// <inheritdoc />
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

        /// <inheritdoc />
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>Records the attempt and returns a synthetic successful response.</summary>
        /// <typeparam name="TResponse">Response type the stored request expects.</typeparam>
        /// <param name="request">Request rebuilt from the durable job payload.</param>
        /// <param name="cancellationToken">Attempt token supplied by the sender worker.</param>
        /// <returns>A message-shaped response for send requests.</returns>
        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            object result = typeof(TResponse) == typeof(bool)
                ? true
                : new Message { Id = 1, Chat = new Chat { Id = 7 } };
            return Task.FromResult((TResponse)result);
        }
    }

    /// <summary>Captures formatted log lines so census and lane fields can be asserted instead of guessed at.</summary>
    private sealed class SinkLogger : ILogger<TelegramSenderService>
    {
        /// <summary>Fixture-owned list of formatted messages.</summary>
        private readonly List<string> _logs;

        /// <summary>Guards the shared list against concurrent worker writes.</summary>
        private readonly object _gate = new();

        /// <summary>Creates a sink appending to the fixture's shared log list.</summary>
        /// <param name="logs">Fixture-owned list of formatted messages.</param>
        public SinkLogger(List<string> logs) => _logs = logs;

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception error, Func<TState, Exception, string> formatter)
        {
            lock (_gate) _logs.Add(formatter(state, error));
        }
    }
}
