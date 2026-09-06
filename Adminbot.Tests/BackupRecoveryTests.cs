using Adminbot.Domain.Logging;
using Microsoft.Data.Sqlite;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>Exercises persisted backup generations with real outbox databases and controlled Telegram barriers.</summary>
public sealed class BackupRecoveryTests
{
    /// <summary>Recovered payment deliveries, including a slow backlog, do not create new backup generations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_backlog_sends_ten_logs_and_one_database_pair(bool slow)
    {
        await using var fixture = new Fixture();
        await using (var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath))
            for (var i = 0; i < 10; i++) await outbox.EnqueueAsync(Item(i));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender { TextBarrier = slow ? release.Task : Task.CompletedTask };
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
        {
            await Until(() => sender.Documents == 2);
            if (slow) await Task.Delay(2200); // Exceeds max-delay while historical send remains blocked.
            release.TrySetResult();
            await Until(() => sender.Texts == 10);
        }
        Assert.Equal(2, sender.Documents);
        Assert.Equal(1, sender.Files.Count(x => x == "users.db"));
        Assert.Equal(1, sender.Files.Count(x => x == "credentials.db"));
    }

    /// <summary>Rapid requests, including different bots, share one global snapshot generation boundary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_burst_and_multibot_requests_coalesce(bool multiBot)
    {
        await using var fixture = new Fixture(); var sender = new Sender();
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
        {
            for (var i = 0; i < 20; i++) Assert.True(dispatcher.EnqueueDurable(new(TelegramLogDeliveryKind.Payment, "test", multiBot ? "bot" + i : "a", "log", "backup")));
            await Until(() => sender.Texts == 20 && sender.Documents == 2);
        }
        Assert.Equal(2, sender.Documents);
    }

    /// <summary>Requests after snapshot start collapse into exactly one follow-up.</summary>
    [Fact]
    public async Task Requests_during_upload_produce_one_followup()
    {
        await using var fixture = new Fixture(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender { DocumentBarrier = release.Task };
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
        {
            dispatcher.EnqueueDurable(new(TelegramLogDeliveryKind.Payment, "first", "a", "log", "backup"));
            await Until(() => sender.Documents == 1);
            for (var i = 0; i < 20; i++) dispatcher.EnqueueDurable(new(TelegramLogDeliveryKind.Payment, "later", "b", "log", "backup"));
            release.TrySetResult(); await Until(() => sender.Documents == 4);
        }
        Assert.Equal(4, sender.Documents);
    }

    /// <summary>Requests during the quiet window reset the debounce without leaving a spurious pending follow-up.</summary>
    [Fact]
    public async Task Requests_during_debounce_are_covered_by_first_snapshot()
    {
        await using var fixture = new Fixture(); var sender = new Sender();
        long elapsed = 0; var origin = DateTime.UtcNow;
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options with { UtcNow = () => origin.AddMilliseconds(Interlocked.Read(ref elapsed)), BackupDebounce = TimeSpan.FromMilliseconds(150), BackupMaxDelay = TimeSpan.FromSeconds(2) }))
        {
            for (var i = 0; i < 8; i++) { dispatcher.EnqueueDurable(new(TelegramLogDeliveryKind.Payment, "test", "a", "log", "backup")); await Task.Delay(20); }
            Assert.Equal(0, sender.Documents);
            Interlocked.Exchange(ref elapsed, 3000);
            await Until(() => sender.Documents == 2);
        }
        Assert.Equal(2, sender.Documents);
    }

    /// <summary>Unacknowledged log replay after a covered backup does not invalidate that backup watermark.</summary>
    [Fact]
    public async Task Ack_uncertainty_and_new_payment_after_recovery_have_distinct_backup_intents()
    {
        await using var fixture = new Fixture();
        await using (var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath))
        { await outbox.EnqueueAsync(Item(1)); var state = await outbox.ReadBackupAsync(); await outbox.CoverBackupAsync(state.Requested); }
        var sender = new Sender();
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
        {
            await Until(() => sender.Texts == 1); Assert.Equal(0, sender.Documents);
            dispatcher.EnqueueDurable(new(TelegramLogDeliveryKind.Payment, "new", "a", "log", "backup"));
            await Until(() => sender.Documents == 2);
        }
        Assert.Equal(2, sender.Documents);
    }

    /// <summary>A crash after document upload but before watermark ACK causes one coalesced recovery pair.</summary>
    /// <returns>A task completing after ten already-delivered logs leave only one durable backup intent to recover.</returns>
    [Fact]
    public async Task Backup_ack_uncertainty_recovers_one_pair_without_requiring_log_replay()
    {
        await using var fixture = new Fixture();
        await using (var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath))
        {
            for (var i = 0; i < 10; i++)
            {
                var id = await outbox.EnqueueAsync(Item(i));
                await outbox.AcknowledgeAsync(id);
            }
            // Simulate accepted documents followed by process death: deliberately do not commit Covered.
        }
        var sender = new Sender();
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
            await Until(() => sender.Documents == 2);
        Assert.Equal(0, sender.Texts); Assert.Equal(2, sender.Documents);
    }

    /// <summary>A transient log transport failure retries delivery without multiplying backup intent.</summary>
    [Fact]
    public async Task Payment_retry_does_not_request_another_backup()
    {
        await using var fixture = new Fixture(); var sender = new Sender { FailFirst = true };
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
        {
            dispatcher.EnqueueDurable(new(TelegramLogDeliveryKind.Payment, "retry", "a", "log", "backup"));
            await Until(() => sender.Texts >= 2 && sender.Documents == 2);
        }
        Assert.Equal(2, sender.Documents);
    }

    /// <summary>Tenant lifecycle audit retains HTML, identifiers and routing without creating financial backup intent.</summary>
    /// <returns>A task completing after twenty real lifecycle logger calls and one payment are inspected durably.</returns>
    [Fact]
    public async Task Tenant_lifecycle_is_durable_html_and_only_payment_increments_generation()
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var registry = new BotRegistry(configuration);
        var logger = new TelegramLogger("lifecycle", null, registry, new Adminbot.Domain.BotContextAccessor(), "log", "backup", dispatcher);
        var service = new MultiBotHostedService(null, null, null, null, null, null, configuration,
            new TypedLogger(logger));
        var method = typeof(MultiBotHostedService).GetMethod("LogTenantRuntimeEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [typeof(string), typeof(string), typeof(long?), typeof(IEnumerable<string>), typeof(string), typeof(string), typeof(string)], null)!;
        try
        {
            for (var i = 0; i < 20; i++) method.Invoke(service, ["tenant-" + i, "store", (long?)123, new[] { "channel" }, "support", "روشن شد", "error <test>"]);
            await using var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath);
            Assert.Equal(0, (await outbox.ReadBackupAsync()).Requested);
            using var db = new SqliteConnection("Data Source=" + fixture.Options.OutboxDatabasePath);
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT DeliveryKind, LoggerChannelId, Message FROM TelegramLogOutbox";
            using (var reader = await command.ExecuteReaderAsync())
            {
                var count = 0;
                while (await reader.ReadAsync())
                {
                    Assert.Equal((int)TelegramLogDeliveryKind.Html, reader.GetInt32(0));
                    Assert.Equal("log", reader.GetString(1));
                    Assert.Contains("tenant-", reader.GetString(2));
                    Assert.Contains("<b>روشن شد</b>", reader.GetString(2));
                    Assert.Contains("error &lt;test&gt;", reader.GetString(2));
                    count++;
                }
                Assert.Equal(20, count);
            }
            logger.LogPayment("financial payment");
            Assert.Equal(1, (await outbox.ReadBackupAsync()).Requested);
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>Idle recovery reads occur only after an explicitly released fallback wait, even without producer signals.</summary>
    /// <returns>A task completing after a directly committed payment is recovered by a manually released timeout.</returns>
    [Fact]
    public async Task Idle_wait_bounds_reads_and_fallback_recovers_without_signal()
    {
        await using var fixture = new Fixture(); var sender = new Sender();
        var waiting = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWait = 0;
        var options = fixture.Options with
        {
            BackupWaitAsync = async (signal, interval, token) =>
            {
                if (Interlocked.Increment(ref firstWait) == 1)
                {
                    waiting.TrySetResult(interval);
                    await timeout.Task.WaitAsync(token);
                    return false;
                }
                return await signal.WaitAsync(interval, token);
            }
        };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, options);
        Assert.Equal(TimeSpan.FromSeconds(10), await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, dispatcher.BackupStateReads);
        await using var outbox = new TelegramLogOutbox(options.OutboxDatabasePath);
        // Exercise database activity while the coordinator is held at the fallback timer boundary.
        for (var i = 0; i < 20; i++) Assert.Equal(0, (await outbox.ReadBackupAsync()).Requested);
        await outbox.EnqueueAsync(Item(1)); // Deliberately bypass dispatcher and its wake signal.
        Assert.Equal(1, dispatcher.BackupStateReads);
        Assert.Equal(0, sender.Documents);
        timeout.TrySetResult();
        await Until(() => sender.Documents == 2);
    }

    /// <summary>Global channel precedence treats all blank configuration forms as missing.</summary>
    /// <param name="configured">Optional configured Telegram channel under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_global_channel_uses_default_owned_channel(string? configured)
    {
        Assert.Equal("owned-channel", TelegramLogDispatcherOptions.SelectDestination(configured!, "owned-channel"));
        Assert.Equal("global-channel", TelegramLogDispatcherOptions.SelectDestination("global-channel", "owned-channel"));
    }

    /// <summary>Blank runtime destinations use durable identity; explicit owned destinations override a tenant fallback.</summary>
    /// <param name="useOwned">Whether the runtime provides the default-owned destination.</param>
    /// <returns>A task completing after the observed document sender and channel have been checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_destination_uses_owned_then_persisted_fallback(bool useOwned)
    {
        await using var fixture = new Fixture(); var sender = new Sender();
        var bots = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using (var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath)) await outbox.EnqueueAsync(Item(1));
        await using (var dispatcher = new TelegramLogDispatcher(bot => { bots.Add(bot); return sender; }, fixture.Options with
        { BackupBotId = useOwned ? "owned" : "   ", BackupChannelId = useOwned ? "owned-channel" : "" }))
            await Until(() => sender.Documents == 2);
        Assert.All(sender.Channels, channel => Assert.Equal(useOwned ? "owned-channel" : "backup", channel));
        Assert.Contains(useOwned ? "owned" : "a", bots);
        if (useOwned) Assert.Equal(2, bots.Count(bot => bot == "owned"));
    }

    /// <summary>Missing destinations retain intent without document calls or rapid coordinator reads.</summary>
    /// <returns>A task completing after pending state and no-send behavior have been checked.</returns>
    [Fact]
    public async Task Missing_destination_retains_pending_generation()
    {
        await using var fixture = new Fixture(); var sender = new Sender();
        await using var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath);
        await outbox.EnqueueAsync(Item(1) with { BotId = "", BackupChannelId = " " });
        var warning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options with
        { BackupDebounce = TimeSpan.Zero, BackupWarning = message => warning.TrySetResult(message) }))
        {
            Assert.Equal("[DatabaseBackup] destination unavailable; durable generation remains pending.",
                await warning.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, sender.Documents);
            var state = await outbox.ReadBackupAsync(); Assert.Equal(1, state.Requested); Assert.Equal(0, state.Covered);
            // Clear only the fixture's pending intent so disposal need not spend its production drain allowance.
            await outbox.CoverBackupAsync(1);
        }
        Assert.Equal(0, sender.Documents);
    }

    /// <summary>Forwards the real lifecycle category to the production Telegram logger without starting receivers.</summary>
    /// <param name="inner">Required production Telegram logger whose durable routing is exercised.</param>
    private sealed class TypedLogger(Microsoft.Extensions.Logging.ILogger inner) : Microsoft.Extensions.Logging.ILogger<MultiBotHostedService>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        /// <inheritdoc />
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => inner.IsEnabled(level);
        /// <inheritdoc />
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? error, Func<TState, Exception?, string> formatter) => inner.Log(level, eventId, state, error, formatter);
    }

    /// <summary>Builds a non-secret durable payment row for restart seeding.</summary>
    private static TelegramLogOutboxItem Item(int id) => new(0, DateTime.UtcNow, TelegramLogDeliveryKind.Payment, TelegramLogDeliveryKind.Payment,
        "a", "log", "backup", "test" + id, 0, DateTime.UtcNow, null, TelegramLogOutboxStatus.Pending, null, null);

    /// <summary>Waits for a controlled observable boundary with a finite failure deadline.</summary>
    private static async Task Until(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!ready()) await Task.Delay(10, timeout.Token); }

    /// <summary>Controls Telegram completion without making external requests.</summary>
    private sealed class Sender : ITelegramLogSender
    {
        public int Texts; public int Documents; public bool FailFirst;
        public Task TextBarrier = Task.CompletedTask; public Task DocumentBarrier = Task.CompletedTask;
        public System.Collections.Concurrent.ConcurrentBag<string> Files = [];
        /// <summary>Observed document destinations; text-log routes are intentionally excluded.</summary>
        public System.Collections.Concurrent.ConcurrentBag<string> Channels = [];
        public async Task SendTextMessageAsync(string channelId, string message, ParseMode? parseMode, CancellationToken cancellationToken)
        { var count = Interlocked.Increment(ref Texts); await TextBarrier.WaitAsync(cancellationToken); if (FailFirst && count == 1) throw new HttpRequestException("test transient"); }
        /// <summary>Records a fake document destination and waits at the controlled upload boundary.</summary>
        /// <param name="channelId">Resolved Telegram backup chat id; test data only.</param>
        /// <param name="fileName">Backup document name, users.db or credentials.db.</param>
        /// <param name="content">Open SQLite snapshot stream owned by the dispatcher.</param>
        /// <param name="cancellationToken">Dispatcher shutdown token cancelling the fake upload.</param>
        /// <returns>A task completing when the upload barrier releases.</returns>
        /// <remarks>No network request is made. Counting before the barrier marks snapshot/upload start.</remarks>
        public async Task SendDocumentAsync(string channelId, string fileName, Stream content, CancellationToken cancellationToken)
        { Channels.Add(channelId); Files.Add(fileName); Interlocked.Increment(ref Documents); await DocumentBarrier.WaitAsync(cancellationToken); }
    }

    /// <summary>Owns isolated real SQLite backup sources and the durable log outbox.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "AdminbotBackup-" + Guid.NewGuid().ToString("N"));
        public TelegramLogDispatcherOptions Options { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_path);
            foreach (var name in new[] { "users.db", "credentials.db" })
            { using var db = new SqliteConnection("Data Source=" + Path.Combine(_path, name)); db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE Example(Id INTEGER)"; command.ExecuteNonQuery(); }
            Options = new() { OutboxDatabasePath = Path.Combine(_path, "outbox.db"), UsersDatabasePath = Path.Combine(_path, "users.db"), CredentialsDatabasePath = Path.Combine(_path, "credentials.db"),
                ScanInterval = TimeSpan.FromMilliseconds(10), MinimumSendInterval = TimeSpan.Zero, BackupDebounce = TimeSpan.FromMilliseconds(300), BackupMaxDelay = TimeSpan.FromSeconds(2) };
        }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(_path, true); return ValueTask.CompletedTask; }
    }
}
