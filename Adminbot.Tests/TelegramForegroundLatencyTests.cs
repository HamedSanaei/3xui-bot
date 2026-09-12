using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>
/// Regression coverage for the head-of-line blocking incident: a foreground Telegram update must never be allowed to
/// hold its strict FIFO lane for the full provider-oriented XUI timeout, a non-durable interactive Telegram send must
/// release the lane promptly, and the scheduler must name the lane blocker instead of warning about each victim.
/// </summary>
/// <remarks>
/// These tests never contact real Telegram, XUI, or payment-provider endpoints. XUI coverage uses a loopback HTTP panel
/// started inside the test process, Telegram coverage uses in-process fake clients, and every latency budget is a
/// millisecond value so no test waits for a production timeout.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Counts and controls requests for foreground delivery tests.</summary>
    private sealed class DeliveryProbeClient : ITelegramBotClient
    {
        /// <summary>Number of interactive message sends that entered the client.</summary>
        public int InteractiveSendAttempts;

        /// <summary>Number of non-interactive requests that entered the client.</summary>
        public int OtherRequests;

        /// <summary>True blocks a send until cancellation; false replies immediately.</summary>
        public bool HangOnSend { get; set; } = true;

        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long? BotId => 1;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApiAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>Records the request kind and either hangs or answers immediately.</summary>
        /// <typeparam name="TResponse">Requested response type.</typeparam>
        /// <param name="request">Request built by the caller.</param>
        /// <param name="cancellationToken">Token that also carries the foreground budget.</param>
        /// <returns>A synthetic Telegram response.</returns>
        public async Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SendMessageRequest)
            {
                Interlocked.Increment(ref InteractiveSendAttempts);
                if (HangOnSend)
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
            }
            else
            {
                Interlocked.Increment(ref OtherRequests);
            }

            object result = typeof(TResponse) == typeof(bool)
                ? true
                : typeof(TResponse) == typeof(Telegram.Bot.Types.User)
                    ? new Telegram.Bot.Types.User { Id = 7, FirstName = "probe" }
                    : new Message { MessageId = 1, Chat = new Chat { Id = 7 } };
            return (TResponse)result;
        }
    }

    /// <summary>Captures every log record so Information diagnostics can be asserted, not only warnings.</summary>
    /// <typeparam name="T">Logger category.</typeparam>
    private sealed class DiagnosticLogger<T> : ILogger<T>
    {
        /// <summary>Records level and formatted message for every emitted log line.</summary>
        public ConcurrentQueue<(LogLevel Level, string Message)> Records { get; } = new();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Records.Enqueue((logLevel, formatter(state, exception)));

        /// <summary>Counts records at one level whose message contains one marker.</summary>
        /// <param name="level">Level to match.</param>
        /// <param name="marker">Substring that must appear in the message.</param>
        /// <returns>The number of matching records.</returns>
        public int Count(LogLevel level, string marker)
            => Records.Count(x => x.Level == level && x.Message.Contains(marker, StringComparison.Ordinal));

        /// <summary>Returns every message at one level.</summary>
        /// <param name="level">Level to filter by.</param>
        /// <returns>Matching formatted messages in emission order.</returns>
        public List<string> Messages(LogLevel level)
            => Records.Where(x => x.Level == level).Select(x => x.Message).ToList();
    }

    /// <summary>Builds a loopback configuration with a short request timeout and a controllable retry budget.</summary>
    /// <param name="retryCount">Additional transient attempts allowed for background reads.</param>
    /// <returns>Configuration understood by <see cref="ApiServicev3"/>.</returns>
    private static IConfiguration LatencyPanelConfiguration(int retryCount)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["xuiV3RequestTimeoutSeconds"] = "30",
            ["xuiV3TransientRetryCount"] = retryCount.ToString(),
            ["xuiV3TransientRetryBaseDelayMilliseconds"] = "10"
        }).Build();

    /// <summary>Panel descriptor for one started fake panel.</summary>
    /// <param name="url">Loopback base URL.</param>
    /// <returns>Server information accepted by <see cref="ApiServicev3"/>.</returns>
    private static ServerInfo LatencyPanelServer(string url) => new() { ApiVersion = "v3", Url = url, RootPath = "", ApiToken = "test-only" };

    /// <summary>The shared production interactive delivery budget and the untouched UX interaction budgets.</summary>
    [Fact]
    public void Foreground_budgets_are_eight_seconds_and_existing_interaction_timeouts_are_unchanged()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), TelegramForegroundDeliveryPolicy.Production.OverallBudget);
        Assert.Equal(TimeSpan.FromSeconds(2), TelegramInteractionTimeouts.Production.CallbackAnswer);
        Assert.Equal(TimeSpan.FromSeconds(5), TelegramInteractionTimeouts.Production.MandatoryJoin);
        Assert.Equal(TimeSpan.FromSeconds(12), ApiServicev3.DefaultForegroundReadOverallBudget);
        Assert.Equal(TimeSpan.FromSeconds(15), ApiServicev3.ForegroundReadOverallHardCap);
    }

    /// <summary>A hanging interactive send is abandoned at the budget and never re-sent automatically.</summary>
    /// <returns>A task completing after the typed timeout and the single-attempt assertion.</returns>
    [Fact]
    public async Task Foreground_delivery_budget_abandons_hanging_send_without_resending()
    {
        var inner = new DeliveryProbeClient();
        var bounded = new ForegroundBoundedTelegramBotClient(
            inner, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(60) });

        var sw = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TelegramForegroundDeliveryTimeoutException>(() => bounded.SendTextMessageAsync(
            chatId: 7, text: "menu", cancellationToken: CancellationToken.None));
        sw.Stop();

        Assert.Equal("send_message", exception.RequestKind);
        Assert.Equal(TimeSpan.FromMilliseconds(60), exception.Budget);
        Assert.Equal(1, Volatile.Read(ref inner.InteractiveSendAttempts));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"elapsed={sw.Elapsed}");
        // The typed message must never expose the chat, payload, or token.
        Assert.DoesNotContain("token", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Non-interactive requests keep the inner client's own transport semantics with no foreground budget.</summary>
    /// <returns>A task completing after the delegated request count is asserted.</returns>
    [Fact]
    public async Task Foreground_delivery_delegates_non_interactive_requests()
    {
        var inner = new DeliveryProbeClient();
        var bounded = new ForegroundBoundedTelegramBotClient(
            inner, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(60) });

        var me = await bounded.GetMeAsync(CancellationToken.None);

        Assert.Equal(7, me.Id);
        Assert.Equal(1, Volatile.Read(ref inner.OtherRequests));
        Assert.Equal(0, Volatile.Read(ref inner.InteractiveSendAttempts));
    }

    /// <summary>Caller cancellation keeps its own identity and is never rewritten as a delivery timeout.</summary>
    /// <returns>A task completing after the cancellation assertion.</returns>
    [Fact]
    public async Task Foreground_delivery_propagates_caller_cancellation()
    {
        var inner = new DeliveryProbeClient();
        var bounded = new ForegroundBoundedTelegramBotClient(
            inner, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromSeconds(30) });
        using var outer = new CancellationTokenSource();

        var pending = bounded.SendTextMessageAsync(chatId: 7, text: "menu", cancellationToken: outer.Token);
        await Until(() => Volatile.Read(ref inner.InteractiveSendAttempts) == 1);
        outer.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    /// <summary>The latency scope reports only stages above its threshold and uses closed vocabulary names.</summary>
    [Fact]
    public void Latency_scope_reports_only_slow_stages_with_closed_vocabulary()
    {
        var reported = new ConcurrentQueue<(TelegramUpdateStage Stage, double Elapsed)>();

        using (TelegramUpdateLatencyScope.Push(1, "owned", 42, TimeSpan.FromMilliseconds(40),
            (stage, elapsed) => reported.Enqueue((stage, elapsed))))
        {
            using (TelegramUpdateLatencyScope.Current!.Measure(TelegramUpdateStage.DatabaseWait))
                Thread.Sleep(60);
            using (TelegramUpdateLatencyScope.Current!.Measure(TelegramUpdateStage.XuiRead))
                Thread.Sleep(1);
        }

        var single = Assert.Single(reported);
        Assert.Equal(TelegramUpdateStage.DatabaseWait, single.Stage);
        Assert.True(single.Elapsed >= 40, $"elapsed={single.Elapsed}");
    }

    /// <summary>The scope restores the previous ambient value and shared code tolerates its absence.</summary>
    [Fact]
    public void Latency_scope_is_scoped_and_absent_by_default()
    {
        Assert.Null(TelegramUpdateLatencyScope.Current);
        var outer = TelegramUpdateLatencyScope.Push(1, "owned", 1, TimeSpan.FromSeconds(1), (_, _) => { });
        var inner = TelegramUpdateLatencyScope.Push(2, "tenant", 2, TimeSpan.FromSeconds(1), (_, _) => { });
        // The newest pushed scope wins, and disposal restores the enclosing scope exactly once.
        Assert.Equal(2, TelegramUpdateLatencyScope.Current!.Sequence);
        inner.Dispose();
        Assert.Equal(1, TelegramUpdateLatencyScope.Current!.Sequence);
        inner.Dispose();
        Assert.Equal(1, TelegramUpdateLatencyScope.Current!.Sequence);
        outer.Dispose();
        Assert.Null(TelegramUpdateLatencyScope.Current);
    }

    /// <summary>A foreground XUI read is bounded by one overall budget and stops retrying immediately.</summary>
    /// <returns>A task completing after the typed timeout and the single-request assertion.</returns>
    /// <remarks>
    /// The panel delays far beyond the foreground budget while the per-attempt provider timeout stays at thirty seconds,
    /// which is exactly the production shape that held one lane for about 104 seconds.
    /// </remarks>
    [Fact]
    public async Task Foreground_xui_read_is_bounded_by_one_overall_budget()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = 0;
        app.Run(async context =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted);
        });
        await app.StartAsync();
        try
        {
            var foreground = new ConfigurationBuilder().AddConfiguration(LatencyPanelConfiguration(retryCount: 3))
                .AddInMemoryCollection(new Dictionary<string, string?> { ["xuiV3ForegroundReadTimeoutSeconds"] = "0.15" }).Build();

            var sw = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<XuiV3ForegroundReadTimeoutException>(() => ApiServicev3.GetClientsAsync(
                LatencyPanelServer(app.Urls.Single()), foreground, CancellationToken.None,
                XuiV3RequestExecutionPolicy.ForegroundRead));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"elapsed={sw.Elapsed}");
            Assert.True(exception.Budget <= ApiServicev3.ForegroundReadOverallHardCap, $"budget={exception.Budget}");
            // No additional foreground attempt may start after the overall budget expires.
            Assert.Equal(1, Volatile.Read(ref requests));
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>The foreground budget resolver clamps, defaults, and hard-caps exactly as documented.</summary>
    [Fact]
    public void Foreground_read_budget_resolver_defaults_and_caps()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), ApiServicev3.ResolveForegroundReadOverallBudget(null));
        Assert.Equal(TimeSpan.FromSeconds(12), ApiServicev3.ResolveForegroundReadOverallBudget(new AppConfig { XuiV3ForegroundReadTimeoutSeconds = 0 }));
        Assert.Equal(TimeSpan.FromSeconds(12), ApiServicev3.ResolveForegroundReadOverallBudget(new AppConfig { XuiV3ForegroundReadTimeoutSeconds = -3 }));
        Assert.Equal(TimeSpan.FromSeconds(15), ApiServicev3.ResolveForegroundReadOverallBudget(new AppConfig { XuiV3ForegroundReadTimeoutSeconds = 600 }));
        Assert.Equal(TimeSpan.FromMilliseconds(40), ApiServicev3.ResolveForegroundReadOverallBudget(new AppConfig { XuiV3ForegroundReadTimeoutSeconds = 0.04 }));
    }

    /// <summary>Background reads keep the existing transient retry budget.</summary>
    /// <returns>A task completing after the retry-then-success assertion.</returns>
    [Fact]
    public async Task Background_xui_read_keeps_transient_retry_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = 0;
        app.Run(async context =>
        {
            if (Interlocked.Increment(ref requests) < 3)
            {
                context.Response.StatusCode = 503;
                return;
            }

            await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}");
        });
        await app.StartAsync();
        try
        {
            var response = await ApiServicev3.GetClientsAsync(
                LatencyPanelServer(app.Urls.Single()), LatencyPanelConfiguration(retryCount: 3), CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal(3, Volatile.Read(ref requests));
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>A non-idempotent mutation still performs exactly one POST when the panel answers a transient status.</summary>
    /// <returns>A task completing after the single-POST assertion.</returns>
    /// <remarks>
    /// Guard against the foreground hardening accidentally making an ambiguous mutation retryable: the foreground budget
    /// must never be combined with <c>NoAutomaticRetry</c>.
    /// </remarks>
    [Fact]
    public async Task NoAutomaticRetry_mutation_performs_exactly_one_post()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var posts = 0;
        app.Run(async context =>
        {
            if (string.Equals(context.Request.Method, "POST", StringComparison.Ordinal))
                Interlocked.Increment(ref posts);
            context.Response.StatusCode = 503;
            await Task.CompletedTask;
        });
        await app.StartAsync();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ApiServicev3.UpdateClientTrafficAsync(
                LatencyPanelServer(app.Urls.Single()), LatencyPanelConfiguration(retryCount: 3), "customer@test", 0, 0,
                CancellationToken.None, XuiV3RequestRetryMode.NoAutomaticRetry));

            Assert.Equal(1, Volatile.Read(ref posts));
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>
    /// One slow lane head produces exactly one live warning, one completion diagnostic, and one queue-wait warning that
    /// names the blocker instead of warning separately for every victim.
    /// </summary>
    /// <returns>A task completing after all four same-lane updates finish and the log shape is asserted.</returns>
    [Fact]
    public async Task Slow_lane_head_is_reported_once_and_names_the_blocker()
    {
        using var databases = new Databases();
        var logs = new DiagnosticLogger<TelegramUpdateScheduler>();
        var started = new ConcurrentQueue<int>();
        var executor = new Executor(async (item, token) =>
        {
            started.Enqueue(item.Update.Id);
            if (item.Update.Id == 1)
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        });
        using var scheduler = new TelegramUpdateScheduler(
            databases.Inbox,
            executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 4, TelegramUpdateQueueCapacity = 100, TelegramUpdateShutdownDrainSeconds = 5 },
            logs)
        {
            LongHandlerWarningThreshold = TimeSpan.FromMilliseconds(40),
            InteractiveHandlerThreshold = TimeSpan.FromMilliseconds(30),
            SlowStageThreshold = TimeSpan.FromMilliseconds(30),
            LongQueueWaitThreshold = TimeSpan.FromMilliseconds(100)
        };
        await scheduler.StartAsync(default);
        try
        {
            for (var id = 1; id <= 4; id++)
                await scheduler.EnqueueAsync("owned", Update(id, 711), default);

            await Until(() => started.Count >= 4);
            await Until(() => scheduler.ActiveHandlerCount == 0);
        }
        finally { await scheduler.StopAsync(default); }

        Assert.Equal(new[] { 1, 2, 3, 4 }, started.ToArray());

        // Exactly one live warning for the slow root handler, never one per second or one per victim.
        Assert.Equal(1, logs.Count(LogLevel.Warning, "handler running unusually long"));
        // Exactly one completion diagnostic for the long handler, recorded at Information because the live warning
        // already alerted for that incident.
        Assert.Equal(1, logs.Count(LogLevel.Information, "long update handler completed"));
        // Queue-wait reporting is deduplicated to a single warning that names the ROOT blocker. Every victim of the
        // slow head reports the same blocker, so the cascade produces one alert instead of one per victim, and the
        // warning identifies both the blocking sequence and how long that handler ran.
        var waits = logs.Messages(LogLevel.Warning).Where(x => x.Contains("waited unusually long", StringComparison.Ordinal)).ToList();
        var rootWarning = Assert.Single(waits, x => x.Contains("PreviousUpdateId=1", StringComparison.Ordinal));
        Assert.Contains("PreviousHandlerDurationMs=", rootWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousSequence=0", rootWarning, StringComparison.Ordinal);
        // The slow head itself waited behind nothing, so it must never be reported as its own victim.
        Assert.DoesNotContain(waits, x => x.Contains("PreviousSequence=0", StringComparison.Ordinal));
    }

    /// <summary>A slow execution stage is attributed to one closed-vocabulary stage name.</summary>
    /// <returns>A task completing after the stage diagnostic assertion.</returns>
    [Fact]
    public async Task Slow_stage_is_attributed_to_closed_vocabulary_stage()
    {
        using var databases = new Databases();
        var logs = new DiagnosticLogger<TelegramUpdateScheduler>();
        var executor = new Executor(async (item, token) =>
        {
            using (TelegramUpdateLatencyScope.Current!.Measure(TelegramUpdateStage.XuiRead))
                await Task.Delay(TimeSpan.FromMilliseconds(80), token);
        });
        using var scheduler = new TelegramUpdateScheduler(
            databases.Inbox,
            executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 2, TelegramUpdateQueueCapacity = 10, TelegramUpdateShutdownDrainSeconds = 5 },
            logs)
        {
            SlowStageThreshold = TimeSpan.FromMilliseconds(30)
        };
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", Update(1, 711), default);
            await Until(() => logs.Count(LogLevel.Information, "slow update stage") == 1);
        }
        finally { await scheduler.StopAsync(default); }

        var stage = logs.Messages(LogLevel.Information).Single(x => x.Contains("slow update stage", StringComparison.Ordinal));
        Assert.Contains("Stage=XuiRead", stage, StringComparison.Ordinal);
        Assert.Contains("BotId=owned", stage, StringComparison.Ordinal);
        Assert.Contains("Sequence=", stage, StringComparison.Ordinal);
    }

    /// <summary>Strict FIFO is preserved for one slow lane head, with no overtaking and no overlap.</summary>
    /// <returns>A task completing after the order and overlap assertions.</returns>
    [Fact]
    public async Task Strict_fifo_is_preserved_when_the_lane_head_is_slow()
    {
        using var databases = new Databases();
        var order = new ConcurrentQueue<int>();
        var active = 0;
        var overlap = 0;
        var release = Signal();
        var headEntered = Signal();
        var executor = new Executor(async (item, token) =>
        {
            if (Interlocked.Increment(ref active) > 1) Interlocked.Increment(ref overlap);
            order.Enqueue(item.Update.Id);
            try
            {
                if (item.Update.Id == 1)
                {
                    headEntered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var scheduler = Create(databases, executor, concurrency: 4);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", Update(1, 711), default);
            await headEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scheduler.EnqueueAsync("owned", Update(2, 711), default);
            await scheduler.EnqueueAsync("owned", Update(3, 711), default);
            await Task.Delay(150);
            // Nothing may overtake the slow head while it is still running.
            Assert.Equal(new[] { 1 }, order.ToArray());
            release.TrySetResult();
            await Until(() => order.Count == 3);
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }

        Assert.Equal(new[] { 1, 2, 3 }, order.ToArray());
        Assert.Equal(0, overlap);
    }

    /// <summary>One blocked lane never freezes other bot/user lanes on the same scheduler.</summary>
    /// <returns>A task completing after the cross-lane progress assertion.</returns>
    [Fact]
    public async Task Other_lanes_keep_running_while_one_lane_is_blocked()
    {
        using var databases = new Databases();
        var release = Signal();
        var headEntered = Signal();
        var otherCompleted = Signal();
        var executor = new Executor(async (item, token) =>
        {
            if (item.Update.Id == 1)
            {
                headEntered.TrySetResult();
                await release.Task.WaitAsync(token);
                return;
            }
            otherCompleted.TrySetResult();
        });
        using var scheduler = Create(databases, executor, concurrency: 2);
        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned", Update(1, 711), default);
            await headEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scheduler.EnqueueAsync("owned", Update(5, 999), default);
            await otherCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); await scheduler.StopAsync(default); }
    }

    /// <summary>The durable lane-blocker query returns metadata only and never a payload.</summary>
    /// <returns>A task completing after the correlation assertions.</returns>
    [Fact]
    public async Task Previous_lane_blocker_query_returns_metadata_for_the_overlapping_execution()
    {
        using var databases = new Databases();
        var accepted = DateTime.UtcNow;
        long firstSequence;
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry
            {
                BotId = "owned", UpdateId = 916840327, TelegramUserId = 711, UpdateType = "Message",
                Status = "completed", AcceptedAtUtc = accepted.AddSeconds(-1),
                StartedAtUtc = accepted, CompletedAtUtc = accepted.AddSeconds(104)
            });
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry
            {
                BotId = "owned", UpdateId = 916840329, TelegramUserId = 711, UpdateType = "CallbackQuery",
                Status = "running", AcceptedAtUtc = accepted.AddSeconds(2), StartedAtUtc = accepted.AddSeconds(86),
                Payload = "private-customer-payload"
            });
            db.TelegramUpdateInbox.Add(new TelegramUpdateInboxEntry
            {
                BotId = "owned", UpdateId = 916840330, TelegramUserId = 722, UpdateType = "Message",
                Status = "completed", AcceptedAtUtc = accepted.AddSeconds(3),
                StartedAtUtc = accepted.AddSeconds(3), CompletedAtUtc = accepted.AddSeconds(4)
            });
            await db.SaveChangesAsync();
            firstSequence = (await db.TelegramUpdateInbox.Where(x => x.UpdateId == 916840327).ToListAsync()).Single().Sequence;
        }

        var blocker = await databases.Inbox.FindPreviousLaneExecutionAsync(
            firstSequence + 1, "owned", 711, accepted.AddSeconds(2), accepted.AddSeconds(86), default);

        Assert.NotNull(blocker);
        Assert.Equal(firstSequence, blocker!.Sequence);
        Assert.Equal(916840327, blocker.UpdateId);
        Assert.Equal("Message", blocker.UpdateType);
        Assert.True(blocker.HandlerDurationMs >= 100_000, $"duration={blocker.HandlerDurationMs}");
        // The projection has no payload member at all, which is asserted through the metadata-only type contract.
        Assert.Null(typeof(TelegramLaneExecutionSummary).GetProperty("Payload"));

        // A different lane and a non-overlapping window must not be reported as the blocker.
        Assert.Null(await databases.Inbox.FindPreviousLaneExecutionAsync(
            firstSequence + 2, "owned", 722, accepted.AddSeconds(30), accepted.AddSeconds(31), default));
    }

    /// <summary>
    /// A tenant <c>/start</c> whose menu send hangs releases the lane promptly, never re-sends, and does not fault the
    /// update.
    /// </summary>
    /// <returns>A task completing after the bounded dispatch assertion.</returns>
    /// <remarks>
    /// This reproduces the second production incident shape: the mandatory-join budget is untouched, the membership
    /// probe succeeds quickly, and only the later menu send is slow. The bounded client view is the one the scheduler
    /// installs for a real update execution.
    /// </remarks>
    [Fact]
    public async Task Tenant_start_with_hanging_menu_send_releases_lane_without_resending()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            var tenant = new BotInstance
            {
                Id = "tenant-latency", Username = "latency_store", Token = Token(51001), TelegramBotId = 51001,
                Type = BotInstanceTypes.Tenant, Enabled = true, OwnerTelegramUserId = 711,
                BrandName = "latency", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            };
            await using (var db = databases.Users.CreateDbContext())
            {
                db.BotInstances.Add(tenant);
                await db.SaveChangesAsync();
            }
            registry.Upsert(tenant);

            var inner = new DeliveryProbeClient();
            clients[tenant.Id] = new StorefrontClient();
            var bounded = new ForegroundBoundedTelegramBotClient(
                inner, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(80) });
            var runtime = new BotRuntimeContext { Config = RuntimeSnapshot.Copy(registry.GetById(tenant.Id)), Client = bounded };

            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
            var message = new Message
            {
                MessageId = 5, Date = DateTime.UtcNow, Text = "/start",
                Chat = new Chat { Id = 7468859738, Type = ChatType.Private },
                From = new Telegram.Bot.Types.User { Id = 7468859738, FirstName = "customer" }
            };
            var update = new Update { Id = 282126319, Message = message };

            var sw = Stopwatch.StartNew();
            await service.DispatchUpdateAsync(bounded, update, runtime, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();

            // The lane is released in milliseconds instead of waiting for a provider-oriented transport timeout, and the
            // abandoned send is never retried.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"elapsed={sw.Elapsed}");
            Assert.Equal(1, Volatile.Read(ref inner.InteractiveSendAttempts));
        }
    }

    /// <summary>
    /// The diagnostic file envelope prefers the bot id named by a global component's own message over the ambient
    /// default owned bot fallback.
    /// </summary>
    /// <remarks>
    /// Production evidence showed scheduler diagnostics whose message said <c>BotId=tenant-...</c> while the file
    /// envelope described the default owned bot, which made lane incidents look like they belonged to the wrong bot.
    /// </remarks>
    [Fact]
    public void Diagnostic_envelope_prefers_explicit_bot_id_over_default_owned_fallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AdminbotEnvelope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "diagnostics.log");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["errorFileLogEnabled"] = "true",
                ["errorFileLogMinimumLevel"] = "Information",
                ["errorFileLogFilePath"] = path
            }).Build();
            var logger = new DailyErrorFileLoggerProvider(configuration, new BotContextAccessor())
                .CreateLogger("TelegramUpdateScheduler");

            // No ambient bot context exists in a scheduler callback, so the accessor would fall back to the default
            // owned bot; the explicit tenant id in the message must win instead.
            logger.LogWarning("Telegram update handler running unusually long. BotId={BotId} Sequence={Sequence}", "tenant-297967493", 3545);
            logger.LogWarning("Telegram queue pressure. QueueDepth={QueueDepth}", 3);

            var written = string.Join("\n", Directory.GetFiles(directory).Select(System.IO.File.ReadAllText));
            Assert.Contains("botId=tenant-297967493", written, StringComparison.Ordinal);
            Assert.DoesNotContain("botId=vpnetiranbot Serial", written, StringComparison.Ordinal);
            // A message without an explicit bot id keeps the existing ambient/default behavior.
            Assert.Contains("botId=vpnetiranbot", written, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Every closed-vocabulary stage name is stable so a log parser can rely on it.</summary>
    [Fact]
    public void Stage_vocabulary_is_stable()
    {
        var names = Enum.GetNames<TelegramUpdateStage>();
        Assert.Equal(
            new[]
            {
                "XuiRead", "TelegramSend", "TelegramEdit", "TelegramMembership",
                "SiteLookup", "ProviderRead", "DatabaseWait", "BusinessRecovery"
            },
            names);
    }
}
