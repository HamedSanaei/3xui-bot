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
        public async Task SendTextMessageAsync(string channelId, string message, ParseMode? parseMode, CancellationToken cancellationToken)
        { var count = Interlocked.Increment(ref Texts); await TextBarrier.WaitAsync(cancellationToken); if (FailFirst && count == 1) throw new HttpRequestException("test transient"); }
        public async Task SendDocumentAsync(string channelId, string fileName, Stream content, CancellationToken cancellationToken)
        { Files.Add(fileName); Interlocked.Increment(ref Documents); await DocumentBarrier.WaitAsync(cancellationToken); }
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
