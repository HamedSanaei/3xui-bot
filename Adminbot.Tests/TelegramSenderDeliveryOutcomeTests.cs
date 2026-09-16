using System.Collections.Concurrent;
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
/// Regression coverage for the reported <c>Status=uncertain</c> delivery outcome that appeared even when a send started
/// quickly and finished in milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// Production evidence behind these tests: the sender logged <c>Status=uncertain</c> with <c>QueueWaitMs</c> under 30ms
/// and <c>SendMs</c> under 15ms, which is impossible for a job that merely waited too long. The cause was lifetime
/// confusion, not transport latency. Three defects are pinned here:
/// </para>
/// <list type="number">
/// <item>
/// The caller's own token was linked into the worker's attempt, so an interactive wait expiring cancelled a Telegram call
/// the caller had already asked for, and the aborted attempt was recorded as an ambiguous delivery.
/// </item>
/// <item>
/// A failed terminal status write overwrote a confirmed <c>sent</c> outcome with <c>uncertain</c>, so a successful send
/// was reported to its caller as unknown.
/// </item>
/// <item>
/// A queued job whose caller stopped waiting was discarded, so a foreground reply could be dropped un-sent even though
/// nothing else would ever deliver it.
/// </item>
/// </list>
/// <para>
/// Every test uses an in-process fake Telegram client, a short-lived SQLite users.db, and a one-second transport deadline.
/// No listener, socket, polling loop, or real Telegram call is started, and no test asserts that anything is ever
/// replayed: an automatic replay is exactly what must not come back.
/// </para>
/// </remarks>
public sealed class TelegramSenderDeliveryOutcomeTests : IDisposable
{
    /// <summary>Internal bot identity used by the registry, the jobs, and the fake transport.</summary>
    private const string BotId = "owned-probe";

    /// <summary>Fixture-owned temporary directory holding this test's users.db.</summary>
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "adminbot-sender-outcome-" + Guid.NewGuid().ToString("N"));

    /// <summary>In-process transport that records attempts and can hold a send open.</summary>
    private readonly ProbeClient _client = new();

    /// <summary>Formatted log lines captured so outcome fields can be asserted directly.</summary>
    private readonly List<string> _logs = new();

    private readonly UserDbContextFactory _factory;
    private readonly BotRegistry _registry;
    private readonly BotClientProvider _clients;
    private TelegramSenderService _sender;

    /// <summary>
    /// Creates a migrated users.db, a registry with one enabled owned bot, and a transport provider that hands the fake
    /// client to the sender.
    /// </summary>
    /// <remarks>
    /// The registry is built from in-memory configuration rather than production configuration.json, so no real token,
    /// chat, or storefront is involved. The short-lived database uses the temporary-directory pattern shared by the other
    /// runtime fixtures and is released in <see cref="Dispose"/>.
    /// </remarks>
    public TelegramSenderDeliveryOutcomeTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(SqliteOperation.ConnectionString(Path.Combine(_directory, "users.db"))).Options);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bots:0:Id"] = BotId,
                ["Bots:0:Token"] = "000000:probe-token",
                ["Bots:0:Enabled"] = "true",
                ["Bots:0:IsDefault"] = "true",
                ["Bots:0:Type"] = BotInstanceTypes.Owned
            })
            .Build();

        _registry = new BotRegistry(configuration);
        // The alternate constructor is the documented test seam: production would otherwise build a real HTTP client.
        _clients = new BotClientProvider(_registry, _ => _client);
    }

    /// <summary>
    /// A send that reached Telegram must be reported as sent and never as uncertain, because the caller's own deadline is
    /// not evidence about Telegram's answer.
    /// </summary>
    /// <remarks>
    /// This is the direct regression for the reported symptom: a fast, successful send recorded as an ambiguous delivery.
    /// </remarks>
    [Fact]
    public async Task Successful_send_is_recorded_as_sent_and_never_as_uncertain()
    {
        var sender = await StartSenderAsync();

        var message = await sender.EnqueueAsync(BotId,
            new SendMessageRequest { ChatId = 7, Text = "menu" }, false, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(1, await WaitForStatusAsync("sent"));
        Assert.Equal(1, _client.Attempts(nameof(SendMessageRequest)));
        Assert.DoesNotContain(_logs, line => line.Contains("Status=uncertain", StringComparison.Ordinal));
    }

    /// <summary>
    /// Once a job is durably queued, the caller's deadline must not fail the update, and the worker must keep owning the
    /// attempt so the reply is still delivered exactly once.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Returning a failure would resurface as an interactive update error, and aborting the attempt
    /// would either lose the reply or record a delivered message as ambiguous. Exactly one attempt is asserted because an
    /// automatic replay of an interrupted send is deliberately not part of this design.
    /// </remarks>
    [Fact]
    public async Task Caller_deadline_after_durable_admission_neither_fails_the_caller_nor_aborts_the_send()
    {
        var sender = await StartSenderAsync();
        _client.BlockSends = true;
        using var callerToken = new CancellationTokenSource();

        var sending = sender.EnqueueAsync(BotId, new SendMessageRequest { ChatId = 7, Text = "reply" },
            false, callerToken.Token, TelegramWorkPriority.Normal);

        // The attempt is in flight, so the durable admission has already succeeded.
        await _client.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        callerToken.Cancel();

        // The caller stops waiting without a delivery failure.
        Assert.Null(await sending);

        // The worker still owns the attempt and completes it.
        _client.Release.TrySetResult();
        Assert.Equal(1, await WaitForStatusAsync("sent"));
        Assert.Equal(1, _client.Attempts(nameof(SendMessageRequest)));
        Assert.Contains(_logs, line => line.Contains("cancellation_source=caller_token", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only the sender's own transport deadline may produce uncertainty, because that is the single case where Telegram's
    /// answer is genuinely unknown.
    /// </summary>
    /// <remarks>
    /// The caller's token is deliberately left live so the uncertainty cannot be attributed to it. The recorded
    /// cancellation source is asserted as well as the outcome, because "cancelled" without a source is what made the
    /// original incident unattributable.
    /// </remarks>
    [Fact]
    public async Task Transport_deadline_reports_uncertain_and_names_its_cancellation_source()
    {
        var sender = await StartSenderAsync();
        _client.BlockSends = true;
        using var callerToken = new CancellationTokenSource();

        await Assert.ThrowsAsync<TelegramDeliveryUncertainException>(() => sender.EnqueueAsync(BotId,
            new SendMessageRequest { ChatId = 7, Text = "reply" }, false, callerToken.Token));

        Assert.False(callerToken.IsCancellationRequested);
        Assert.Equal(1, await WaitForStatusAsync("uncertain"));
        Assert.Contains(_logs, line => line.Contains("cancellation_source=send_timeout", StringComparison.Ordinal));
    }

    /// <summary>
    /// A financial claim whose caller disappeared must still be abandoned, because that caller re-sends its own row and
    /// delivering here as well could duplicate a receipt.
    /// </summary>
    /// <remarks>
    /// This test exists to prove the fix did not trade one bug for a worse one. The abandoned foreground reply is
    /// deliberately treated differently in the next test.
    /// </remarks>
    [Fact]
    public async Task Financial_claim_whose_caller_disappeared_is_still_not_delivered()
    {
        var sender = await StartSenderAsync();
        _client.BlockSends = true;

        var occupying = sender.EnqueueAsync(BotId, new SendMessageRequest { ChatId = 7, Text = "first" },
            false, CancellationToken.None, TelegramWorkPriority.Normal);
        await _client.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var financialToken = new CancellationTokenSource();
        var financial = sender.EnqueueAsync(BotId, new SendMessageRequest { ChatId = 7, Text = "receipt" },
            false, financialToken.Token, TelegramWorkPriority.Critical);
        await WaitForQueuedCountAsync(2);
        financialToken.Cancel();
        Assert.Null(await financial);

        _client.Release.TrySetResult();
        await occupying;
        Assert.Equal(1, await WaitForStatusAsync("failed"));
        Assert.Equal(1, _client.Attempts(nameof(SendMessageRequest)));
    }

    /// <summary>
    /// A normal-priority reply whose caller disappeared must still be delivered, because no other component owns its
    /// recovery and dropping it would mean the customer never receives the reply that was already admitted.
    /// </summary>
    /// <remarks>
    /// The contrast with the financial claim above is the whole policy: this sender owns normal-priority output, while a
    /// critical claim belongs to the outbox that admitted it.
    /// </remarks>
    [Fact]
    public async Task Normal_reply_whose_caller_disappeared_is_still_delivered()
    {
        var sender = await StartSenderAsync();
        _client.BlockSends = true;

        var occupying = sender.EnqueueAsync(BotId, new SendMessageRequest { ChatId = 7, Text = "first" },
            false, CancellationToken.None, TelegramWorkPriority.Normal);
        await _client.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var abandonedToken = new CancellationTokenSource();
        var abandoned = sender.EnqueueAsync(BotId, new SendMessageRequest { ChatId = 7, Text = "reply" },
            false, abandonedToken.Token, TelegramWorkPriority.Normal);
        await WaitForQueuedCountAsync(2);
        abandonedToken.Cancel();
        Assert.Null(await abandoned);

        _client.Release.TrySetResult();
        await occupying;
        // Both admitted replies reach Telegram exactly once and neither is left ambiguous or discarded.
        await WaitForSentCountAsync(2);
        Assert.Equal(2, await CountJobsAsync("sent"));
        Assert.Equal(0, await CountJobsAsync("failed"));
    }

    /// <summary>
    /// A delivery Telegram already confirmed must stay confirmed for its caller even when the terminal status write fails,
    /// because a failed local write is not evidence about a successful remote send.
    /// </summary>
    /// <remarks>
    /// The store is removed after the attempt is in flight, so the claim has already succeeded and only the terminal update
    /// can fail. The previous behaviour replaced the confirmed <c>sent</c> status with <c>uncertain</c>, which reported a
    /// delivered message to the customer's own handler as an unknown outcome - the reported defect.
    /// </remarks>
    [Fact]
    public async Task Confirmed_send_stays_confirmed_when_the_terminal_status_write_fails()
    {
        var sender = await StartSenderAsync();
        _client.BlockSends = true;

        var sending = sender.EnqueueAsync(BotId, new SendMessageRequest { ChatId = 7, Text = "reply" },
            false, CancellationToken.None);
        await _client.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Remove the durable store while the send is in flight: the claim is already stored, so only the terminal write can
        // still fail. The pool is cleared before every attempt so no cached handle keeps the store alive, and the removal is
        // retried because a pooling handle from a pump or worker context can outlive the first attempt on Windows.
        Assert.True(await RemoveStoreAsync(), "the temporary database directory could not be removed for this test");
        _client.Release.TrySetResult();

        var message = await sending;

        Assert.NotNull(message);
        Assert.Contains(_logs, line => line.Contains("Telegram output status persistence failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cancellation vocabulary and the four new metric names must be exactly what an operator was told to expect.
    /// </summary>
    /// <remarks>
    /// A widened vocabulary would silently create unlabeled dashboard series, so an arbitrary value is rejected here.
    /// </remarks>
    [Fact]
    public void Cancellation_vocabulary_and_metric_names_are_the_documented_ones()
    {
        Assert.True(TelegramCancellationSources.IsKnown(TelegramCancellationSources.CallerToken));
        Assert.True(TelegramCancellationSources.IsKnown(TelegramCancellationSources.SendTimeout));
        Assert.True(TelegramCancellationSources.IsKnown(TelegramCancellationSources.HostShutdown));
        Assert.False(TelegramCancellationSources.IsKnown("caller-token"));
        Assert.False(TelegramCancellationSources.IsKnown(null));

        Assert.Equal("enqueue_wait_ms", TelegramLatencyMetrics.EnqueueWaitMs.Name);
        Assert.Equal("worker_start_delay_ms", TelegramLatencyMetrics.WorkerStartDelayMs.Name);
        Assert.Equal("telegram_api_ms", TelegramLatencyMetrics.TelegramApiMs.Name);
        Assert.Equal("telegram_delivery_cancellation_total", TelegramLatencyMetrics.CancellationSource.Name);
    }

    /// <summary>Starts the sender so durable admission and the worker loops are active.</summary>
    /// <returns>The started sender, owned by this fixture and stopped in <see cref="Dispose"/>.</returns>
    private async Task<TelegramSenderService> StartSenderAsync()
    {
        _sender = new TelegramSenderService(_factory, _clients, _registry,
            new TelegramPerformanceOptions { WorkerCount = 1, SendTimeoutSeconds = 1 },
            new TelegramWorkQueue(new TelegramPerformanceOptions()), new SinkLogger(_logs));
        await _sender.StartAsync(CancellationToken.None);
        return _sender;
    }

    /// <summary>Removes this fixture's durable store so the next write fails at the filesystem level.</summary>
    /// <returns><c>true</c> when the directory is gone; <c>false</c> when a handle kept it alive for the whole window.</returns>
    /// <remarks>Used only by the persistence-failure regression test. The pattern mirrors the production failure shape: a
    /// store that cannot be opened at all, so the terminal status write cannot run.</remarks>
    private async Task<bool> RemoveStoreAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            SqliteTestPools.ClearForDirectory(_directory);
            try
            {
                Directory.Delete(_directory, recursive: true);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }

        return false;
    }

    /// <summary>Waits until exactly one job row holds the requested status.</summary>
    /// <param name="status">Stored job status to wait for.</param>
    /// <returns>The number of rows found, for a precise assertion at the call site.</returns>
    private async Task<int> WaitForStatusAsync(string status)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await CountJobsAsync(status) == 1) return 1;
            await Task.Delay(50);
        }

        return await CountJobsAsync(status);
    }

    /// <summary>Waits until the requested number of jobs reached the sent state.</summary>
    /// <param name="expected">Number of delivered rows the test has admitted so far.</param>
    private async Task WaitForSentCountAsync(int expected)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await CountJobsAsync("sent") >= expected) return;
            await Task.Delay(50);
        }
    }

    /// <summary>Waits until the durable queue holds the expected number of rows.</summary>
    /// <param name="expected">Number of jobs the test has admitted so far.</param>
    private async Task WaitForQueuedCountAsync(int expected)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var db = _factory.CreateDbContext();
            if (await db.TelegramDeliveryJobs.CountAsync() >= expected) return;
            await Task.Delay(25);
        }
    }

    /// <summary>Counts durable output jobs in one status.</summary>
    /// <param name="status">Stored status value to count.</param>
    /// <returns>Row count; zero also means the job reached a terminal state that is not the requested one.</returns>
    private async Task<int> CountJobsAsync(string status)
    {
        await using var db = _factory.CreateDbContext();
        return await db.TelegramDeliveryJobs.CountAsync(x => x.Status == status);
    }

    /// <summary>Stops the sender and releases this fixture's SQLite pools and temporary directory.</summary>
    public void Dispose()
    {
        try { _sender?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        SqliteTestPools.ClearForDirectory(_directory);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Fake transport that counts attempts, can hold a send open, and answers with a synthetic Telegram payload.
    /// </summary>
    /// <remarks>
    /// Holding a send open is what makes the reported timing shape reproducible: the caller's deadline expires while the
    /// attempt is in flight, which previously cancelled the API call itself.
    /// </remarks>
    private sealed class ProbeClient : ITelegramBotClient
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

        /// <summary>When true, a send waits for <see cref="Release"/> or the attempt's own token.</summary>
        public bool BlockSends { get; set; }

        /// <summary>Completes when a send has entered the transport.</summary>
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes a blocked send on demand.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Counts how many attempts one request kind reached the transport.</summary>
        /// <param name="requestTypeName">Runtime request type name.</param>
        /// <returns>Attempt count for that kind; zero when it never reached the transport.</returns>
        public int Attempts(string requestTypeName) => _attempts.TryGetValue(requestTypeName, out var value) ? value : 0;

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

        /// <summary>Records one attempt and answers either immediately or after the test releases it.</summary>
        /// <typeparam name="TResponse">Response type the stored request expects.</typeparam>
        /// <param name="request">Request rebuilt from the durable job payload.</param>
        /// <param name="cancellationToken">Attempt token: host shutdown plus the sender's own transport deadline.</param>
        /// <returns>A synthetic response shaped like the real API payload.</returns>
        public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            _attempts.AddOrUpdate(request.GetType().Name, 1, (_, value) => value + 1);
            SendStarted.TrySetResult();
            if (BlockSends)
                await Release.Task.WaitAsync(cancellationToken);

            object result = typeof(TResponse) == typeof(bool)
                ? true
                : new Message { Id = 1, Chat = new Chat { Id = 7 } };
            return (TResponse)result;
        }
    }

    /// <summary>Captures formatted log lines so telemetry fields can be asserted instead of guessed at.</summary>
    private sealed class SinkLogger : ILogger<TelegramSenderService>
    {
        private readonly List<string> _logs;
        private readonly object _gate = new();

        /// <summary>Creates a sink appending to the fixture's shared log list.</summary>
        /// <param name="logs">Fixture-owned list of formatted messages.</param>
        public SinkLogger(List<string> logs) => _logs = logs;

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            lock (_gate) _logs.Add(formatter(state, exception));
        }
    }
}
