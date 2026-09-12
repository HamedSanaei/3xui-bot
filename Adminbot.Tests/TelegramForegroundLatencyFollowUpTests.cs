using System.Collections.Concurrent;
using System.Diagnostics;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>
/// Follow-up regression coverage for the foreground latency hardening: production review separated controlled
/// latency-guard outcomes from real incidents, the most frequent UX telemetry event was unattributable, one
/// interactive website lookup was still provider-bound, and the durable delivery path had to be proven excluded
/// from the new UX budget.
/// </summary>
/// <remarks>
/// These tests never contact real Telegram, XUI, Gozargah, or payment-provider endpoints. Website coverage uses a
/// loopback HTTP endpoint started inside the test process, Telegram coverage uses in-process fake clients, and every
/// latency budget is a millisecond value so no test waits for a production timeout except the single test that must
/// outlast the real four-second optional site-lookup budget to prove the background path does not inherit it.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Telegram client fake that records the request kinds it receives and can hang on selected kinds.
    /// </summary>
    /// <remarks>
    /// Used to prove that specific interactive request shapes — message edits and photo albums — are bounded by the
    /// foreground delivery policy, and that a bounded interactive call is attempted exactly once.
    /// </remarks>
    private sealed class FollowUpProbeClient : ITelegramBotClient
    {
        /// <summary>Attempt counts keyed by the request type name, for example <c>EditMessageTextRequest</c>.</summary>
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

        /// <summary>Request type names that block until the caller's token is cancelled.</summary>
        public HashSet<string> HangOn { get; } = new(StringComparer.Ordinal);

        /// <summary>Counts how many times one request kind entered the client.</summary>
        /// <param name="requestTypeName">Runtime request type name, for example <c>SendMediaGroupRequest</c>.</param>
        /// <returns>The number of observed attempts for that request kind.</returns>
        public int Attempts(string requestTypeName) => _attempts.TryGetValue(requestTypeName, out var value) ? value : 0;

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

        /// <summary>Records the request kind and either hangs until cancellation or answers immediately.</summary>
        /// <typeparam name="TResponse">Requested response type.</typeparam>
        /// <param name="request">Request built by the caller.</param>
        /// <param name="cancellationToken">Token that also carries any foreground delivery budget.</param>
        /// <returns>A synthetic Telegram response shaped like the real API payload.</returns>
        public async Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var name = request.GetType().Name;
            _attempts.AddOrUpdate(name, 1, (_, value) => value + 1);

            if (HangOn.Contains(name))
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }

            object result = typeof(TResponse) == typeof(bool)
                ? true
                : typeof(TResponse) == typeof(Telegram.Bot.Types.User)
                    ? new Telegram.Bot.Types.User { Id = 7, FirstName = "probe" }
                    : typeof(TResponse) == typeof(ChatMember)
                        ? new ChatMemberMember { User = new Telegram.Bot.Types.User { Id = 7, FirstName = "probe" } }
                        : new Message { MessageId = 1, Chat = new Chat { Id = 7 } };
            return (TResponse)result;
        }
    }

    /// <summary>Captures formatted log messages so UX telemetry fields can be asserted directly.</summary>
    private sealed class FollowUpLogSink : ILogger
    {
        /// <summary>Locks appends so concurrently produced telemetry cannot corrupt the list.</summary>
        private readonly object _gate = new();

        /// <summary>Formatted messages in emission order.</summary>
        public List<string> Messages { get; } = new();

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_gate)
                Messages.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Builds a configured Gozargah website API client pointed at one loopback endpoint.
    /// </summary>
    /// <param name="baseUrl">Loopback endpoint that stands in for the website API.</param>
    /// <returns>A client whose <c>get_user</c> lookup targets <paramref name="baseUrl"/>.</returns>
    /// <remarks>The API key is a test-only literal; no production secret is ever read or written by this test.</remarks>
    private static GozargahSiteApiClient FollowUpSiteClient(string baseUrl) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GozargahSiteSyncEnabled"] = "true",
            ["GozargahSiteApiBaseUrl"] = baseUrl,
            ["GozargahSiteApiKey"] = "test-only-key"
        }).Build(),
        NullLogger<GozargahSiteApiClient>.Instance);

    /// <summary>
    /// Starts a loopback HTTP endpoint that never answers, standing in for a hanging Telegram or website backend.
    /// </summary>
    /// <returns>A started application whose every request hangs; the caller must stop and dispose it.</returns>
    /// <remarks>The endpoint is bound to port zero so parallel test runs cannot collide.</remarks>
    private static async Task<WebApplication> StartHangingEndpointAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context => await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted));
        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// A completed guard outcome stays out of the operator channel while every genuine latency failure is delivered.
    /// </summary>
    /// <remarks>
    /// Production review classified the log families instead of treating every slow-operation line as an incident. A
    /// mandatory-join evaluation that completed inside its own five-second deadline is a success, and a transport,
    /// API, or channel-access failure is a real signal that operators must still receive.
    /// </remarks>
    [Fact]
    public void Operator_channel_downgrades_only_controlled_guard_outcomes()
    {
        // The guard finished inside its own deadline: metric and file telemetry only, never an operator alert.
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "Slow Telegram operation. BotId=owned-noise-1 TelegramUserId=5 Operation=telegram_mandatory_join " +
            "ElapsedMs=2221 Outcome=completed ErrorType=", null));

        // Real failures of the very same guards must still reach the channel.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Slow Telegram operation. BotId=owned-noise-1 TelegramUserId=5 Operation=telegram_mandatory_join " +
            "ElapsedMs=5001 Outcome=transport_error ErrorType=RequestException", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Slow Telegram operation. BotId=owned-noise-1 TelegramUserId=5 Operation=telegram_mandatory_join " +
            "ElapsedMs=5001 Outcome=channel_access_error ErrorType=ApiRequestException", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Slow Telegram operation. BotId=owned-noise-1 TelegramUserId=5 Operation=telegram_callback_ack " +
            "ElapsedMs=2001 Outcome=telegram_api_403 ErrorType=ApiRequestException", null));

        // An unrelated slow-operation family is untouched by the controlled-latency rule.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Slow Telegram operation. BotId=owned-noise-1 TelegramUserId=5 Operation=telegram_manual_join " +
            "ElapsedMs=9000 Outcome=local_timeout ErrorType=OperationCanceledException", null));

        // Financial and settlement failures are never suppressed by this rule.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant settlement failed while crediting the owner wallet. orderId=12", null));
    }

    /// <summary>
    /// A repeated controlled local timeout is delivered to the operator channel at most once per window per key.
    /// </summary>
    /// <remarks>
    /// The first occurrence of a local timeout is information the operator may still want; the following occurrences
    /// of the same bot/operation/outcome inside the window are the amplification that must stop. Distinct bots keep
    /// their own notification so one noisy storefront cannot hide another bot's guard activity.
    /// </remarks>
    [Fact]
    public void Operator_channel_rate_limits_repeated_controlled_local_timeouts()
    {
        static string Message(string botId) =>
            $"Slow Telegram operation. BotId={botId} TelegramUserId=5 Operation=telegram_callback_ack " +
            "ElapsedMs=2001 Outcome=local_timeout ErrorType=OperationCanceledException";

        // First occurrence of this key is informative, so it reaches the channel.
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("owned-noise-rl-a"), null));
        // Repeats inside the ten-minute window are suppressed as amplification.
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("owned-noise-rl-a"), null));
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("owned-noise-rl-a"), null));

        // A different bot keeps its own slot, so suppression is per key and not global.
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("owned-noise-rl-b"), null));
        // A different operation on the same bot is a separate key.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Slow Telegram operation. BotId=owned-noise-rl-a TelegramUserId=5 Operation=telegram_mandatory_join " +
            "ElapsedMs=5001 Outcome=local_timeout ErrorType=OperationCanceledException", null));
    }

    /// <summary>The interaction actor scope restores the enclosing value and normalizes invalid identities.</summary>
    /// <remarks>
    /// The actor is diagnostics-only, but a leaked or invalid value would misattribute UX telemetry, so scoping and
    /// normalization are asserted directly.
    /// </remarks>
    [Fact]
    public void Interaction_actor_scope_restores_previous_value_and_rejects_invalid_ids()
    {
        Assert.Null(TelegramInteractionActor.Current);

        using (TelegramInteractionActor.Push(411))
        {
            Assert.Equal(411, TelegramInteractionActor.Current);

            // A non-positive id can never be a real Telegram sender, so it is reported as unknown rather than as 0.
            using (TelegramInteractionActor.Push(0))
                Assert.Null(TelegramInteractionActor.Current);
            using (TelegramInteractionActor.Push(-9))
                Assert.Null(TelegramInteractionActor.Current);

            Assert.Equal(411, TelegramInteractionActor.Current);
        }

        // Disposal restores the previous value, and a double dispose must not pop an enclosing scope again.
        var outer = TelegramInteractionActor.Push(900);
        var inner = TelegramInteractionActor.Push(901);
        inner.Dispose();
        inner.Dispose();
        Assert.Equal(900, TelegramInteractionActor.Current);
        outer.Dispose();
        Assert.Null(TelegramInteractionActor.Current);
    }

    /// <summary>
    /// A callback acknowledgement names the ambient update sender instead of reporting a null identity.
    /// </summary>
    /// <returns>A task completing after the bounded acknowledgement and telemetry assertion.</returns>
    /// <remarks>
    /// This is the production defect that made the most frequent controlled latency event unattributable: the
    /// acknowledgement helper only ever held the opaque callback id.
    /// </remarks>
    [Fact]
    public async Task Callback_acknowledgement_uses_the_ambient_sender_when_none_is_supplied()
    {
        var probe = new FollowUpProbeClient();
        probe.HangOn.Add("AnswerCallbackQueryRequest");
        var sink = new FollowUpLogSink();

        using (TelegramInteractionActor.Push(4242))
        {
            var answered = await TelegramCallbackAnswerPolicy.TryAnswerAsync(
                probe, "callback-id", cancellationToken: CancellationToken.None,
                logger: sink, botId: "owned", timeout: TimeSpan.FromMilliseconds(40));

            Assert.False(answered);
        }

        var telemetry = Assert.Single(sink.Messages);
        Assert.Contains("TelegramUserId=4242", telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("TelegramUserId=(null)", telemetry, StringComparison.Ordinal);
        Assert.Contains("Outcome=local_timeout", telemetry, StringComparison.Ordinal);
    }

    /// <summary>An explicitly supplied sender wins over the ambient actor for the acknowledgement telemetry.</summary>
    /// <returns>A task completing after the bounded acknowledgement and telemetry assertion.</returns>
    /// <remarks>
    /// Call sites that already hold <c>CallbackQuery.From.Id</c> must stay authoritative, so the ambient fallback can
    /// never override a known value.
    /// </remarks>
    [Fact]
    public async Task Explicit_callback_sender_takes_precedence_over_the_ambient_actor()
    {
        var probe = new FollowUpProbeClient();
        probe.HangOn.Add("AnswerCallbackQueryRequest");
        var sink = new FollowUpLogSink();

        using (TelegramInteractionActor.Push(1111))
        {
            var answered = await TelegramCallbackAnswerPolicy.TryAnswerAsync(
                probe, "callback-id", cancellationToken: CancellationToken.None,
                logger: sink, botId: "owned", telegramUserId: 2222, timeout: TimeSpan.FromMilliseconds(40));

            Assert.False(answered);
        }

        var telemetry = Assert.Single(sink.Messages);
        Assert.Contains("TelegramUserId=2222", telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("TelegramUserId=1111", telemetry, StringComparison.Ordinal);
    }

    /// <summary>The optional foreground website-lookup budget is four seconds and can never exceed five.</summary>
    [Fact]
    public void Optional_site_lookup_budget_is_four_seconds_capped_at_five()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), GozargahSiteApiClient.OptionalLookupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), GozargahSiteApiClient.OptionalLookupHardMaximum);
        Assert.True(GozargahSiteApiClient.OptionalLookupTimeout <= GozargahSiteApiClient.OptionalLookupHardMaximum);

        // An already cancelled lane must produce an immediately cancelled lookup instead of re-arming a fresh budget.
        using var cancelledOuter = new CancellationTokenSource();
        cancelledOuter.Cancel();
        using var cancelledScope = GozargahSiteApiClient.CreateOptionalLookupCancellation(cancelledOuter.Token);
        Assert.True(cancelledScope.Token.IsCancellationRequested);

        // A live caller token must not produce an already-expired budget.
        using var liveOuter = new CancellationTokenSource();
        using var liveScope = GozargahSiteApiClient.CreateOptionalLookupCancellation(liveOuter.Token);
        Assert.False(liveScope.Token.IsCancellationRequested);
    }

    /// <summary>
    /// A foreground website lookup linked to the lane token is abandoned as soon as the lane ends.
    /// </summary>
    /// <returns>A task completing after the lookup is proven to release control promptly.</returns>
    /// <remarks>
    /// The website backend hangs for thirty seconds. The interactive caller must still be released quickly, which is
    /// what prevents a slow website from holding a strict FIFO Telegram lane.
    /// </remarks>
    [Fact]
    public async Task Foreground_site_lookup_releases_the_lane_on_outer_cancellation()
    {
        await using var endpoint = await StartHangingEndpointAsync();
        try
        {
            var client = FollowUpSiteClient(endpoint.Urls.Single());
            using var lane = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            using var scope = GozargahSiteApiClient.CreateOptionalLookupCancellation(lane.Token);

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetUserAsync(4242, scope.Token));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"elapsed={sw.Elapsed}");
        }
        finally { await endpoint.StopAsync(); }
    }

    /// <summary>
    /// A caller that does not create the optional lookup scope is not bounded by the four-second foreground budget.
    /// </summary>
    /// <returns>A task completing after the background-style lookup is proven still in flight past the budget.</returns>
    /// <remarks>
    /// Background website work — the sync outbox, its retry worker, the funding monitor, and post-commit mirroring —
    /// passes its own token and keeps the provider-oriented transport ceiling. This test deliberately outlasts the real
    /// four-second budget, because that is the only way to prove the budget is opt-in and is not silently imposed on
    /// the background path.
    /// </remarks>
    [Fact]
    public async Task Site_lookup_without_the_foreground_scope_is_not_bounded_by_the_optional_budget()
    {
        await using var endpoint = await StartHangingEndpointAsync();
        try
        {
            var client = FollowUpSiteClient(endpoint.Urls.Single());
            using var background = new CancellationTokenSource();
            using var completed = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(background.Token, completed.Token);

            var pending = client.GetUserAsync(4242, linked.Token);

            // Still running after the foreground budget plus slack: the background caller owns its own deadline.
            await Task.Delay(GozargahSiteApiClient.OptionalLookupTimeout + TimeSpan.FromMilliseconds(400));
            Assert.False(pending.IsCompleted);

            // The caller's own token is still the effective bound.
            background.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { await endpoint.StopAsync(); }
    }

    /// <summary>
    /// Message edits and photo albums are bounded as interactive delivery and attempted exactly once.
    /// </summary>
    /// <returns>A task completing after both request shapes are proven bounded.</returns>
    /// <remarks>
    /// The production storefront service-selection step edits the previous message, and tutorial delivery sends a media
    /// group. Both are interactive UX calls, so both must respect the same overall budget and must never be re-sent
    /// automatically after the budget expires.
    /// </remarks>
    [Fact]
    public async Task Foreground_delivery_bounds_edit_and_album_requests()
    {
        var probe = new FollowUpProbeClient();
        probe.HangOn.Add("EditMessageTextRequest");
        probe.HangOn.Add("SendMediaGroupRequest");
        var bounded = new ForegroundBoundedTelegramBotClient(
            probe, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(60) });

        var editSw = Stopwatch.StartNew();
        var editTimeout = await Assert.ThrowsAsync<TelegramForegroundDeliveryTimeoutException>(
            () => bounded.EditMessageTextAsync(chatId: 7, messageId: 5, text: "menu", cancellationToken: CancellationToken.None));
        editSw.Stop();

        Assert.Equal("edit_message_text", editTimeout.RequestKind);
        Assert.Equal(1, probe.Attempts("EditMessageTextRequest"));
        Assert.True(editSw.Elapsed < TimeSpan.FromSeconds(3), $"elapsed={editSw.Elapsed}");

        var albumSw = Stopwatch.StartNew();
        var albumTimeout = await Assert.ThrowsAsync<TelegramForegroundDeliveryTimeoutException>(
            () => bounded.SendMediaGroupAsync(
                chatId: 7,
                media: new[] { new InputMediaPhoto(InputFile.FromFileId("test-file-id")) },
                cancellationToken: CancellationToken.None));
        albumSw.Stop();

        Assert.Equal("send_media_group", albumTimeout.RequestKind);
        Assert.Equal(1, probe.Attempts("SendMediaGroupRequest"));
        Assert.True(albumSw.Elapsed < TimeSpan.FromSeconds(3), $"elapsed={albumSw.Elapsed}");
    }

    /// <summary>
    /// A storefront service-selection callback whose message edit hangs releases the lane without a second edit.
    /// </summary>
    /// <returns>A task completing after the bounded dispatch assertion.</returns>
    /// <remarks>
    /// This reproduces the second production blocker shape, <c>TN:svc:normal</c>, through the real tenant storefront
    /// dispatcher rather than through a decorator unit test: the callback is acknowledged quickly, any membership probe
    /// answers immediately, and only the menu edit is slow.
    /// </remarks>
    [Fact]
    public async Task Tenant_service_selection_callback_with_hanging_edit_releases_lane_without_resending()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            var tenant = new BotInstance
            {
                Id = "tenant-svc-latency", Username = "svc_latency_store", Token = Token(52001), TelegramBotId = 52001,
                Type = BotInstanceTypes.Tenant, Enabled = true, OwnerTelegramUserId = 711,
                BrandName = "svc-latency", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            };
            await using (var db = databases.Users.CreateDbContext())
            {
                db.BotInstances.Add(tenant);
                await db.SaveChangesAsync();
            }
            // A funded owner clears the storefront access gate, so the callback reaches the real service-selection
            // step instead of stopping at the shared "storefront unavailable" restriction notice.
            await using (var credentials = databases.Credentials.CreateDbContext())
            {
                credentials.Users.Add(new CredUser { TelegramUserId = 711, AccountBalance = 12_000_000 });
                await credentials.SaveChangesAsync();
            }
            registry.Upsert(tenant);

            var probe = new FollowUpProbeClient();
            probe.HangOn.Add("EditMessageTextRequest");
            probe.HangOn.Add("SendMessageRequest");
            clients[tenant.Id] = new StorefrontClient();
            var bounded = new ForegroundBoundedTelegramBotClient(
                probe, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(80) });
            var runtime = new BotRuntimeContext { Config = RuntimeSnapshot.Copy(registry.GetById(tenant.Id)), Client = bounded };

            var callback = new CallbackQuery
            {
                Id = "callback-svc-latency",
                From = new Telegram.Bot.Types.User { Id = 7468859738, IsBot = false, FirstName = "customer" },
                Message = new Message
                {
                    MessageId = 11, Date = DateTime.UtcNow,
                    Chat = new Chat { Id = 7468859738, Type = ChatType.Private }
                },
                Data = "TN:svc:normal"
            };
            var update = new Update { Id = 282126320, CallbackQuery = callback };

            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();

            var sw = Stopwatch.StartNew();
            await service.DispatchUpdateAsync(bounded, update, runtime, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();

            // The lane is released in milliseconds, the slow interactive call happened exactly once, and no edit was
            // replayed. Whether the menu went out as an edit or a send depends on the catalog, so both are accepted.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"elapsed={sw.Elapsed}");
            Assert.Equal(1, probe.Attempts("EditMessageTextRequest") + probe.Attempts("SendMessageRequest"));
        }
    }

    /// <summary>
    /// The durable tenant notification transport is never wrapped in the interactive foreground delivery policy.
    /// </summary>
    /// <remarks>
    /// Production showed a customer account delivery that legitimately took about twenty-eight seconds and finished
    /// with <c>outcome=delivered</c>. Durable delivery owns an outbox with delivered, delivery-uncertain, and
    /// manual-review semantics, so the eight-second interactive budget must never reach it: wrapping that transport
    /// would turn an ambiguous send into either a lost account or a duplicate delivery attempt. This is asserted
    /// structurally because the durable path deliberately has no compile-time dependency on the foreground policy,
    /// which is exactly the property that must not regress.
    /// </remarks>
    [Fact]
    public void Durable_notification_transport_is_never_wrapped_in_the_foreground_delivery_policy()
    {
        // Positive control: the per-update executor is what installs the bounded view, so the guard below cannot pass
        // vacuously if the decorator is ever renamed or removed.
        var executor = ReadRepositoryFile("Services/TelegramUpdateExecutor.cs");
        Assert.Contains("ForegroundBoundedTelegramBotClient", executor, StringComparison.Ordinal);

        var orderWorker = ReadRepositoryFile("Services/TenantOrderNotificationWorker.cs");
        Assert.DoesNotContain("ForegroundBoundedTelegramBotClient", orderWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("TelegramForegroundDeliveryPolicy", orderWorker, StringComparison.Ordinal);
        // The durable order worker uses the shared production client provider directly, which is the raw transport.
        Assert.Contains("BotClientProvider", orderWorker, StringComparison.Ordinal);

        // The durable receipt worker relays through the assistant bot and must stay off the interactive budget too.
        var receiptWorker = ReadRepositoryFile("Services/TenantManualReceiptNotificationWorker.cs");
        Assert.DoesNotContain("ForegroundBoundedTelegramBotClient", receiptWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("TelegramForegroundDeliveryPolicy", receiptWorker, StringComparison.Ordinal);
    }

    /// <summary>
    /// One slow lane head produces one root warning and one queue-wait warning carrying the full blocker correlation.
    /// </summary>
    /// <returns>A task completing after every same-lane update finished and the diagnostics were asserted.</returns>
    /// <remarks>
    /// The reported production symptom was four warnings describing the victims and none naming the cause. The
    /// correlation block must name the waiting update, its lane, its wait, and the exact earlier execution that
    /// occupied the lane, so an operator can identify the root blocker from a single line without reading the inbox
    /// payload.
    /// </remarks>
    [Fact]
    public async Task Queue_wait_warning_carries_full_blocker_correlation_and_dedups_the_cascade()
    {
        using var databases = new Databases();
        var logs = new DiagnosticLogger<TelegramUpdateScheduler>();
        var executor = new Executor(async (item, token) =>
        {
            if (item.Update.Id == 916840327)
                await Task.Delay(TimeSpan.FromMilliseconds(220), token);
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
            await scheduler.EnqueueAsync("vpnetiranbot", Update(916840327, 711), default);
            await Until(() => scheduler.ActiveHandlerCount == 1);
            await scheduler.EnqueueAsync("vpnetiranbot", Update(916840329, 711), default);
            await scheduler.EnqueueAsync("vpnetiranbot", Update(916840330, 711), default);

            await Until(() => scheduler.ActiveHandlerCount == 0);
        }
        finally { await scheduler.StopAsync(default); }

        // One warning for the slow root handler, and no per-second repeat.
        Assert.Equal(1, logs.Count(LogLevel.Warning, "handler running unusually long"));

        // Every victim of the same slow head reports the same earliest overlapping execution, so the cascade collapses
        // to a single queue-wait warning instead of one per waiting update.
        var waits = logs.Messages(LogLevel.Warning)
            .Where(x => x.Contains("waited unusually long", StringComparison.Ordinal))
            .ToList();
        var rootWarning = Assert.Single(waits);

        Assert.Contains("BotId=vpnetiranbot", rootWarning, StringComparison.Ordinal);
        Assert.Contains("TelegramUserId=711", rootWarning, StringComparison.Ordinal);
        Assert.Contains("WaitingUpdateId=916840329", rootWarning, StringComparison.Ordinal);
        Assert.Contains("WaitingUpdateType=Message", rootWarning, StringComparison.Ordinal);
        Assert.Contains("QueueWaitMs=", rootWarning, StringComparison.Ordinal);
        Assert.Contains("PreviousUpdateId=916840327", rootWarning, StringComparison.Ordinal);
        Assert.Contains("PreviousUpdateType=Message", rootWarning, StringComparison.Ordinal);
        Assert.Contains("PreviousHandlerDurationMs=", rootWarning, StringComparison.Ordinal);
        // The root blocker never waited behind anything, so it can never appear as its own victim.
        Assert.DoesNotContain("PreviousSequence=0", rootWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Owned-bot navigation replies — the installation-guide menu, the main menu, and the latest-client download
    /// menu — are bounded by the same shared foreground delivery policy, so a hanging ordinary send releases the lane
    /// instead of holding it until the transport ceiling.
    /// </summary>
    /// <returns>A task completing after all three owned navigation menus are proven bounded.</returns>
    /// <remarks>
    /// Production showed an owned navigation handler occupying its lane for roughly seven seconds and the next update
    /// then waiting about five seconds. Each menu is dispatched through the real owned dispatcher with its own hanging
    /// send, because a single slow reply would otherwise hide the menus that are never reached. A per-feature timeout
    /// helper would not satisfy this: the same <see cref="ForegroundBoundedTelegramBotClient"/> must bound every
    /// interactive navigation send, and an abandoned send must never be repeated.
    /// </remarks>
    [Fact]
    public async Task Owned_navigation_menus_share_the_single_foreground_delivery_boundary()
    {
        using var databases = new Databases();
        // The latest-client menu only renders while the global switch is on, so one test enables it explicitly rather
        // than bypassing the live availability snapshot the real keyboard reads.
        var (provider, registry, _) = IncidentProvider(
            databases,
            extraConfiguration: new Dictionary<string, string?> { ["latestClientDownloadEnabled"] = "true" });
        await using (provider)
        {
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();

            var menus = new[]
            {
                ("owned-tutorial-menu", "\U0001F4A1ر\u0627\u0647\u0646\u0645\u0627 \u0646\u0635\u0628"),
                ("owned-main-menu", "/start"),
                ("owned-latest-client-menu", ClientDownloadCallbacks.OpenCommand)
            };

            var updateId = 916846000;
            foreach (var (label, text) in menus)
            {
                var probe = new FollowUpProbeClient();
                probe.HangOn.Add("SendMessageRequest");
                var bounded = new ForegroundBoundedTelegramBotClient(
                    probe, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(80) });
                var runtime = new BotRuntimeContext
                {
                    Config = RuntimeSnapshot.Copy(registry.DefaultBot),
                    Client = bounded
                };
                var message = new Message
                {
                    MessageId = 21,
                    Date = DateTime.UtcNow,
                    Text = text,
                    Chat = new Chat { Id = 7468859739, Type = ChatType.Private },
                    From = new Telegram.Bot.Types.User { Id = 7468859739, FirstName = "customer" }
                };
                var update = new Update { Id = updateId++, Message = message };

                var sw = Stopwatch.StartNew();
                await service.DispatchUpdateAsync(bounded, update, runtime, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                sw.Stop();

                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"{label} elapsed={sw.Elapsed}");
                Assert.Equal(1, probe.Attempts("SendMessageRequest"));
            }
        }
    }

    /// <summary>
    /// Reads one repository-relative source file, locating the checkout root from the test assembly location.
    /// </summary>
    /// <param name="relativePath">Repository-relative path such as <c>Services/TenantOrderNotificationWorker.cs</c>.</param>
    /// <returns>The full file text.</returns>
    /// <remarks>
    /// Used only by architectural guards that must assert an absence. A missing file or checkout fails the test loudly
    /// rather than silently passing.
    /// </remarks>
    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Adminbot.csproj")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(System.IO.File.Exists(path), $"missing repository file: {relativePath}");
        return System.IO.File.ReadAllText(path);
    }
}
