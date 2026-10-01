using System.Collections.Concurrent;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Xunit;

/// <summary>Behavioral coverage for metadata-only foreground Telegram timings, cancellation ownership, and lane isolation.</summary>
/// <remarks>All calls use the actual decorator and an in-process transport. A controlled monotonic clock avoids timing sleeps.</remarks>
public sealed class TelegramForegroundRequestTelemetryTests
{
    /// <summary>Each explicitly bounded operation records a healthy call and clears its live request state.</summary>
    /// <param name="operation">Synthetic operation selecting a real SDK request without exposing its payload as a kind.</param>
    /// <param name="expectedKind">Expected closed diagnostic classification for that real request.</param>
    /// <returns>A task verifying the active snapshot, completed duration, and cleared request state.</returns>
    [Theory]
    [InlineData("text", TelegramForegroundRequestKind.TextSend)]
    [InlineData("edit-text", TelegramForegroundRequestKind.MessageEdit)]
    [InlineData("edit-caption", TelegramForegroundRequestKind.MessageEdit)]
    [InlineData("edit-media", TelegramForegroundRequestKind.MessageEdit)]
    [InlineData("edit-markup", TelegramForegroundRequestKind.MessageEdit)]
    [InlineData("ack", TelegramForegroundRequestKind.CallbackAcknowledgement)]
    [InlineData("document", TelegramForegroundRequestKind.DocumentUpload)]
    [InlineData("album", TelegramForegroundRequestKind.MediaGroup)]
    [InlineData("photo", TelegramForegroundRequestKind.PhotoSend)]
    [InlineData("delete", TelegramForegroundRequestKind.DeleteMessage)]
    [InlineData("chat", TelegramForegroundRequestKind.ChatLookup)]
    [InlineData("member", TelegramForegroundRequestKind.MembershipLookup)]
    [InlineData("member-count", TelegramForegroundRequestKind.MembershipLookup)]
    [InlineData("administrators", TelegramForegroundRequestKind.MembershipLookup)]
    public async Task Healthy_foreground_operations_record_elapsed_and_clear_live_state(
        string operation, TelegramForegroundRequestKind expectedKind)
    {
        const string secret = "@private_telemetry_probe";
        var clock = new TelemetryClock();
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(clock, observations.Add);
        var transport = new TelemetryClient
        {
            BeforeReply = (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(125));
                var active = Assert.IsType<TelegramForegroundRequestObservation>(scope.CurrentTelegramRequest);
                Assert.Equal(expectedKind, active.Kind);
                Assert.Equal(TelegramForegroundRequestOutcome.InProgress, active.Outcome);
                Assert.Null(active.ApiErrorCode);
                Assert.Equal(125d, active.RequestElapsedMs);
                Assert.Equal(125d, active.TotalTelegramElapsedMs);
                Assert.Equal(1L, active.RequestCount);
                Assert.Empty(observations);
                Assert.DoesNotContain(secret, JsonSerializer.Serialize(active), StringComparison.Ordinal);
                return Task.CompletedTask;
            }
        };
        await SendOperation(new ForegroundBoundedTelegramBotClient(transport, LongPolicy()), operation, secret);

        var completed = Assert.Single(observations);
        Assert.Equal(expectedKind, completed.Kind);
        Assert.Equal(TelegramForegroundRequestOutcome.Completed, completed.Outcome);
        Assert.Equal(125d, completed.RequestElapsedMs);
        Assert.Equal(125d, completed.HandlerElapsedMs);
        Assert.Equal(125d, completed.TotalTelegramElapsedMs);
        Assert.Equal(1L, scope.RequestCount);
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(completed), StringComparison.Ordinal);
    }

    /// <summary>Telegram API failures preserve the exact exception even if the recorder throws, and retain only a numeric code.</summary>
    /// <param name="code">Numeric Telegram status whose error description includes synthetic secret data.</param>
    /// <returns>A task verifying the original failure and the sanitized terminal observation.</returns>
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Api_errors_record_numeric_status_without_secret_exception_content(int code)
    {
        const string secret = "secret-token-url-callback-customer-description";
        var clock = new TelemetryClock();
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(clock, observation =>
        {
            observations.Add(observation);
            throw new InvalidOperationException("local recorder failed");
        });
        var failure = new ApiRequestException(secret, code);
        var transport = new TelemetryClient
        {
            BeforeReply = (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(45));
                return Task.FromException(failure);
            }
        };
        var client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());

        Assert.Same(failure, await Assert.ThrowsAsync<ApiRequestException>(() => client.SendMessage(7, secret)));
        var completed = Assert.Single(observations);
        Assert.Equal(TelegramForegroundRequestOutcome.TelegramApiError, completed.Outcome);
        Assert.Equal(code, completed.ApiErrorCode);
        Assert.Equal(45d, completed.RequestElapsedMs);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(completed), StringComparison.Ordinal);
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.Equal(1, transport.Attempts);
    }

    /// <summary>Cancellation triggered by the real caller remains caller cancellation and is never rewritten as a deadline.</summary>
    /// <returns>A task verifying original cancellation identity, terminal ownership, and no second attempt.</returns>
    [Fact]
    public async Task Caller_cancellation_preserves_exception_identity_and_is_not_budget_expiry()
    {
        var clock = new TelemetryClock();
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(clock, observations.Add);
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new OperationCanceledException("private cancellation context", caller.Token);
        var transport = new TelemetryClient
        {
            BeforeReply = async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetException(original));
                entered.SetResult();
                await cancelled.Task;
            }
        };
        var client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());
        var pending = client.SendMessage(7, "private text", cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(30));
        caller.Cancel();

        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => pending));
        var completed = Assert.Single(observations);
        Assert.Equal(TelegramForegroundRequestOutcome.CallerCancellation, completed.Outcome);
        Assert.Equal(30d, completed.RequestElapsedMs);
        Assert.Null(completed.ApiErrorCode);
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.Equal(1, transport.Attempts);
    }

    /// <summary>Ordinary calls and multipart calls keep their distinct owned deadlines and never resend on expiry.</summary>
    /// <param name="operation">Real foreground operation that blocks until the decorator cancels its token.</param>
    /// <param name="multipart">Whether the existing multipart budget must apply.</param>
    /// <param name="timeoutKind">Existing typed-timeout request kind expected by downstream consumers.</param>
    /// <returns>A task verifying deadline selection, telemetry outcome, and the one-attempt invariant.</returns>
    [Theory]
    [InlineData("text", false, "send_message")]
    [InlineData("photo", false, "send_photo")]
    [InlineData("edit-caption", false, "edit_message_caption")]
    [InlineData("document", true, "send_document")]
    [InlineData("album", true, "send_media_group")]
    public async Task Owned_deadline_records_expiry_with_existing_budget_and_no_resend(
        string operation, bool multipart, string timeoutKind)
    {
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(TimeProvider.System, observations.Add);
        var transport = new TelemetryClient { BeforeReply = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var policy = new TelegramForegroundDeliveryPolicy
        {
            OverallBudget = TimeSpan.FromMilliseconds(20),
            MediaGroupBudget = TimeSpan.FromMilliseconds(45)
        };
        var client = new ForegroundBoundedTelegramBotClient(transport, policy);
        var failure = await Assert.ThrowsAsync<TelegramForegroundDeliveryTimeoutException>(
            () => SendOperation(client, operation, "private-file-or-message").WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(multipart ? policy.MediaGroupBudget : policy.OverallBudget, failure.Budget);
        Assert.Equal(timeoutKind, failure.RequestKind);
        var completed = Assert.Single(observations);
        Assert.Equal(TelegramForegroundRequestOutcome.ForegroundBudgetExpired, completed.Outcome);
        Assert.True(completed.RequestElapsedMs > 0);
        Assert.Equal(completed.RequestElapsedMs, scope.TotalTelegramElapsedMs);
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.Equal(1, transport.Attempts);
    }

    /// <summary>Transport cancellation and faults are not blamed on a live caller or deadline; unrelated failures stay closed-vocabulary.</summary>
    /// <param name="failureKind">Synthetic failure family emitted by the transport.</param>
    /// <param name="expectedOutcome">Expected safe classification without retaining exception names or messages.</param>
    /// <returns>A task verifying exact exception propagation, outcome, and the one-attempt invariant.</returns>
    [Theory]
    [InlineData("cancel", TelegramForegroundRequestOutcome.TransportError)]
    [InlineData("http", TelegramForegroundRequestOutcome.TransportError)]
    [InlineData("io", TelegramForegroundRequestOutcome.TransportError)]
    [InlineData("sdk", TelegramForegroundRequestOutcome.TransportError)]
    [InlineData("timeout", TelegramForegroundRequestOutcome.TransportError)]
    [InlineData("other", TelegramForegroundRequestOutcome.UnexpectedError)]
    public async Task Transport_faults_and_independent_cancellation_propagate_unchanged(
        string failureKind, TelegramForegroundRequestOutcome expectedOutcome)
    {
        Exception failure = failureKind switch
        {
            "cancel" => new OperationCanceledException("private transport cancellation"),
            "http" => new HttpRequestException("private URL"),
            "io" => new IOException("private path"),
            "sdk" => new RequestException("private token"),
            "timeout" => new TimeoutException("private provider context"),
            _ => new InvalidOperationException("private business detail")
        };
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(new TelemetryClock(), observations.Add);
        var transport = new TelemetryClient { BeforeReply = (_, _) => Task.FromException(failure) };
        var client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());
        var observed = await Record.ExceptionAsync(() => client.SendMessage(7, "private message"));

        Assert.Same(failure, observed);
        var completed = Assert.Single(observations);
        Assert.Equal(expectedOutcome, completed.Outcome);
        Assert.Null(completed.ApiErrorCode);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(completed), StringComparison.Ordinal);
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.Equal(1, transport.Attempts);
    }

    /// <summary>Healthy sequential calls remain measurable so a ten-second handler is not diagnosed as a single ten-second send.</summary>
    /// <returns>A task verifying request durations, aggregate elapsed, and non-Telegram handler time.</returns>
    [Fact]
    public async Task Sequential_healthy_calls_separate_aggregate_Telegram_time_from_handler_time()
    {
        var clock = new TelemetryClock();
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(clock, observations.Add);
        var durations = new Queue<TimeSpan>(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) });
        var transport = new TelemetryClient
        {
            BeforeReply = (_, _) => { clock.Advance(durations.Dequeue()); return Task.CompletedTask; }
        };
        var client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());
        clock.Advance(TimeSpan.FromSeconds(4));
        await client.SendMessage(7, "menu");
        await client.EditMessageText(7, 1, "updated menu");
        await client.AnswerCallbackQuery("private callback id");

        Assert.Equal(new[] { 1000d, 2000d, 3000d }, observations.Select(item => item.RequestElapsedMs));
        Assert.Equal(new[] { 1000d, 3000d, 6000d }, observations.Select(item => item.TotalTelegramElapsedMs));
        Assert.All(observations, item => Assert.Equal(TelegramForegroundRequestOutcome.Completed, item.Outcome));
        Assert.Equal(10000d, observations[^1].HandlerElapsedMs);
        Assert.Equal(TimeSpan.FromSeconds(10), scope.Elapsed);
        Assert.Equal(6000d, scope.TotalTelegramElapsedMs);
        Assert.Equal(3L, scope.RequestCount);
        Assert.Null(scope.CurrentTelegramRequest);
    }

    /// <summary>Finishing either overlapping request cannot revive a completed request in the watchdog snapshot.</summary>
    /// <param name="newestFinishesFirst">True completes the inner/latest call first; false completes the older call first.</param>
    /// <returns>A task verifying live cross-thread snapshots, restoration, and summed overlap durations.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_requests_restore_only_still_active_measurements(bool newestFinishesFirst)
    {
        var clock = new TelemetryClock();
        var observations = new ConcurrentQueue<TelegramForegroundRequestObservation>();
        using var scope = Push(clock, observations.Enqueue);
        var textRelease = Signal();
        var documentRelease = Signal();
        var transport = new TelemetryClient
        {
            BeforeReply = (request, _) => request is SendMessageRequest ? textRelease.Task : documentRelease.Task
        };
        var client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());
        var text = client.SendMessage(7, "menu");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var document = client.SendDocument(7, InputFile.FromFileId("private file id"));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        var active = await Task.Run(() => scope.CurrentTelegramRequest);
        Assert.Equal(TelegramForegroundRequestKind.DocumentUpload, active!.Value.Kind);
        Assert.Equal(20d, active.Value.RequestElapsedMs);
        Assert.Equal(50d, active.Value.TotalTelegramElapsedMs);

        (newestFinishesFirst ? documentRelease : textRelease).SetResult();
        await (newestFinishesFirst ? document : text).WaitAsync(TimeSpan.FromSeconds(2));
        active = await Task.Run(() => scope.CurrentTelegramRequest);
        Assert.Equal(newestFinishesFirst ? TelegramForegroundRequestKind.TextSend : TelegramForegroundRequestKind.DocumentUpload,
            active!.Value.Kind);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        (newestFinishesFirst ? textRelease : documentRelease).SetResult();
        await Task.WhenAll(text, document).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(await Task.Run(() => scope.CurrentTelegramRequest));
        Assert.Equal(55d, scope.TotalTelegramElapsedMs);
        Assert.Equal(2L, scope.RequestCount);
        Assert.Equal(2, observations.Count);
    }

    /// <summary>A request nested by the inner transport exposes the inner call and then restores only its active parent.</summary>
    /// <returns>A task verifying nesting, exact individual durations, and final removal of all live state.</returns>
    [Fact]
    public async Task Nested_requests_restore_active_outer_request_without_completed_state_leak()
    {
        var clock = new TelemetryClock();
        var observations = new List<TelegramForegroundRequestObservation>();
        using var scope = Push(clock, observations.Add);
        ForegroundBoundedTelegramBotClient client = null!;
        var transport = new TelemetryClient
        {
            BeforeReply = async (request, _) =>
            {
                if (request is SendMessageRequest)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(2));
                    await client.EditMessageText(7, 1, "nested edit");
                    Assert.Equal(TelegramForegroundRequestKind.TextSend, scope.CurrentTelegramRequest!.Value.Kind);
                    clock.Advance(TimeSpan.FromMilliseconds(4));
                }
                else
                {
                    Assert.Equal(TelegramForegroundRequestKind.MessageEdit, scope.CurrentTelegramRequest!.Value.Kind);
                    clock.Advance(TimeSpan.FromMilliseconds(3));
                }
            }
        };
        client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());
        await client.SendMessage(7, "outer text");

        Assert.Equal(new[] { 3d, 9d }, observations.Select(item => item.RequestElapsedMs));
        Assert.Equal(12d, scope.TotalTelegramElapsedMs);
        Assert.Equal(2L, scope.RequestCount);
        Assert.Null(scope.CurrentTelegramRequest);
    }

    /// <summary>Two concurrently executing bot lanes keep independent ambient scopes and request aggregates.</summary>
    /// <returns>A task verifying bot-local current requests and unrelated per-lane totals.</returns>
    [Fact]
    public async Task Separate_lanes_isolate_current_requests_and_aggregates()
    {
        Assert.Null(TelegramUpdateLatencyScope.Current);
        var ownedEntered = Signal();
        var tenantEntered = Signal();
        var release = Signal();
        var scopes = new ConcurrentDictionary<string, TelegramUpdateLatencyScope>();
        var observations = new ConcurrentDictionary<string, TelegramForegroundRequestObservation>();
        var first = Task.Run(() => RunIsolatedLaneAsync("owned", 30, false, ownedEntered, release.Task, scopes, observations));
        var second = Task.Run(() => RunIsolatedLaneAsync("tenant", 70, true, tenantEntered, release.Task, scopes, observations));
        await Task.WhenAll(ownedEntered.Task, tenantEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TelegramForegroundRequestKind.TextSend, scopes["owned"].CurrentTelegramRequest!.Value.Kind);
        Assert.Equal(TelegramForegroundRequestKind.DocumentUpload, scopes["tenant"].CurrentTelegramRequest!.Value.Kind);
        Assert.Equal(30d, scopes["owned"].TotalTelegramElapsedMs);
        Assert.Equal(70d, scopes["tenant"].TotalTelegramElapsedMs);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(30d, observations["owned"].RequestElapsedMs);
        Assert.Equal(70d, observations["tenant"].RequestElapsedMs);
        Assert.Null(TelegramUpdateLatencyScope.Current);
    }

    /// <summary>A throwing local request or slow-stage recorder cannot change an acknowledged Telegram delivery.</summary>
    /// <returns>A task verifying the actual delivered response and cleared state despite recorder exceptions.</returns>
    [Fact]
    public async Task Throwing_recorders_cannot_change_delivered_response()
    {
        var clock = new TelemetryClock();
        using var scope = TelegramUpdateLatencyScope.Push(1, "owned", 1, TimeSpan.FromMilliseconds(1),
            (_, _) => throw new InvalidOperationException("stage recorder failed"),
            _ => throw new InvalidOperationException("request recorder failed"), clock);
        var transport = new TelemetryClient
        {
            BeforeReply = (_, _) => { clock.Advance(TimeSpan.FromMilliseconds(10)); return Task.CompletedTask; }
        };
        var response = await new ForegroundBoundedTelegramBotClient(transport, LongPolicy()).SendMessage(7, "menu");

        Assert.Equal(17, response.Id);
        Assert.Equal(7, response.Chat.Id);
        Assert.Equal(1L, scope.RequestCount);
        Assert.Equal(10d, scope.TotalTelegramElapsedMs);
        Assert.Null(scope.CurrentTelegramRequest);
    }

    /// <summary>A diagnostic clock failure during completion cannot retain a completed request or change its delivered response.</summary>
    /// <returns>A task verifying successful delivery and request/stage removal while the metadata clock is unavailable.</returns>
    [Fact]
    public async Task Diagnostic_clock_failure_cannot_change_delivery_or_leave_completed_request_active()
    {
        var clock = new TelemetryClock();
        using var scope = Push(clock, _ => { });
        var transport = new TelemetryClient
        {
            BeforeReply = (_, _) =>
            {
                clock.FailReads = true;
                return Task.CompletedTask;
            }
        };
        var response = await new ForegroundBoundedTelegramBotClient(transport, LongPolicy()).SendMessage(7, "menu");

        Assert.Equal(17, response.Id);
        Assert.Equal(7, response.Chat.Id);
        Assert.Equal(1L, scope.RequestCount);
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.Null(scope.CurrentStage);
    }

    /// <summary>Executes one isolated bot lane while holding its real decorated request at a shared barrier.</summary>
    /// <param name="botId">Synthetic runtime bot id identifying this test lane, not a token.</param>
    /// <param name="elapsedMs">Controlled elapsed milliseconds unique to this lane.</param>
    /// <param name="document">True exercises document upload; false exercises text send.</param>
    /// <param name="entered">Lane-owned signal completed after its request snapshot is published.</param>
    /// <param name="release">Shared parent-controlled barrier that permits both requests to finish.</param>
    /// <param name="scopes">Thread-safe scope collection read by the parent watchdog assertions.</param>
    /// <param name="observations">Thread-safe terminal observation collection keyed by synthetic bot id.</param>
    /// <returns>A task completing after this lane's request and scope isolation invariants are checked.</returns>
    /// <remarks>Each invocation owns its clock and ambient scope; neither state nor elapsed time is shared with another lane.</remarks>
    private static async Task RunIsolatedLaneAsync(
        string botId, double elapsedMs, bool document, TaskCompletionSource entered, Task release,
        ConcurrentDictionary<string, TelegramUpdateLatencyScope> scopes,
        ConcurrentDictionary<string, TelegramForegroundRequestObservation> observations)
    {
        var clock = new TelemetryClock();
        using var scope = TelegramUpdateLatencyScope.Push(1, botId, 1, TimeSpan.FromSeconds(1), (_, _) => { },
            item => observations[botId] = item, clock);
        scopes[botId] = scope;
        var transport = new TelemetryClient
        {
            BeforeReply = async (_, _) =>
            {
                Assert.Same(scope, TelegramUpdateLatencyScope.Current);
                clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));
                entered.SetResult();
                await release;
                Assert.Same(scope, TelegramUpdateLatencyScope.Current);
            }
        };
        var client = new ForegroundBoundedTelegramBotClient(transport, LongPolicy());
        await SendOperation(client, document ? "document" : "text", "private payload");
        Assert.Null(scope.CurrentTelegramRequest);
        Assert.Equal(1L, scope.RequestCount);
        Assert.Equal(elapsedMs, scope.TotalTelegramElapsedMs);
    }

    /// <summary>Creates one test scope using an explicit monotonic clock and a metadata recorder.</summary>
    /// <param name="clock">System or test-owned monotonic source; it does not modify delivery cancellation deadlines.</param>
    /// <param name="recorder">Local completion recorder whose failures must remain diagnostic-only.</param>
    /// <returns>A scope that must be disposed in the same asynchronous context that pushed it.</returns>
    /// <example><code>using var scope = Push(clock, observations.Add);</code></example>
    private static TelegramUpdateLatencyScope Push(TimeProvider clock, Action<TelegramForegroundRequestObservation> recorder)
        => TelegramUpdateLatencyScope.Push(1, "owned", 1, TimeSpan.FromHours(1), (_, _) => { }, recorder, clock);

    /// <summary>Creates long delivery budgets so controlled-clock tests cannot accidentally depend on a short real deadline.</summary>
    /// <returns>A test-only policy that preserves separate ordinary and multipart budgets.</returns>
    private static TelegramForegroundDeliveryPolicy LongPolicy() => new()
    {
        OverallBudget = TimeSpan.FromSeconds(30),
        MediaGroupBudget = TimeSpan.FromSeconds(60)
    };

    /// <summary>Creates an asynchronous barrier without inline completion continuations.</summary>
    /// <returns>A manually controlled completion source for request lifetime tests.</returns>
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Issues a real SDK request selected by a fixed test operation.</summary>
    /// <param name="client">Actual foreground decorator under test.</param>
    /// <param name="operation">Fixed synthetic test operation, not customer data.</param>
    /// <param name="payload">Synthetic sensitive data; chat/membership cases require a valid @username. It must never enter diagnostic metadata.</param>
    /// <returns>A task completing when the decorator's actual SendRequest path completes.</returns>
    /// <remarks>The transport does no serialization or network I/O; payloads still reside on real SDK request objects.</remarks>
    private static async Task SendOperation(ForegroundBoundedTelegramBotClient client, string operation, string payload)
    {
        switch (operation)
        {
            case "text": await client.SendMessage(7, payload); break;
            case "edit-text": await client.EditMessageText(7, 1, payload); break;
            case "edit-caption": await client.EditMessageCaption(7, 1, caption: payload); break;
            case "edit-media": await client.EditMessageMedia(7, 1, new InputMediaPhoto(InputFile.FromFileId(payload))); break;
            case "edit-markup": await client.EditMessageReplyMarkup(7, 1); break;
            case "ack": await client.AnswerCallbackQuery(payload); break;
            case "document": await client.SendDocument(7, InputFile.FromFileId(payload)); break;
            case "album":
                await client.SendMediaGroup(7, new[]
                { new InputMediaPhoto(InputFile.FromFileId(payload)), new InputMediaPhoto(InputFile.FromFileId(payload)) }); break;
            case "photo": await client.SendPhoto(7, InputFile.FromFileId(payload)); break;
            case "delete": await client.DeleteMessage(7, 1); break;
            case "chat": await client.GetChat(payload); break;
            case "member": await client.GetChatMember(payload, 7); break;
            case "member-count": await client.GetChatMemberCount(payload); break;
            case "administrators": await client.GetChatAdministrators(payload); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    /// <summary>Thread-safe manual monotonic provider; production cancellation still uses actual elapsed wall time.</summary>
    private sealed class TelemetryClock : TimeProvider
    {
        /// <summary>Elapsed test ticks, advanced only by explicitly exercised request or handler work.</summary>
        private long _timestamp;
        /// <summary>True makes timestamp reads fail, exercising the rule that diagnostic failures cannot alter delivery.</summary>
        public bool FailReads { get; set; }
        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        /// <summary>Gets controlled ticks or simulates an unavailable diagnostic time source.</summary>
        /// <returns>The monotonic timestamp in TimeSpan ticks when diagnostic reads are available.</returns>
        /// <exception cref="InvalidOperationException">The test has explicitly made the diagnostic clock unavailable.</exception>
        public override long GetTimestamp() => FailReads
            ? throw new InvalidOperationException("diagnostic clock unavailable")
            : Interlocked.Read(ref _timestamp);

        /// <summary>Advances the monotonic test time without sleeping or touching cancellation timers.</summary>
        /// <param name="duration">Nonnegative elapsed test time to add.</param>
        /// <remarks>Independent providers let concurrent bot lanes have deterministically different durations.</remarks>
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    /// <summary>In-process transport with a controllable awaited request body and real response types.</summary>
    /// <remarks>The fake never retries; every generic SendRequest entry counts one delivery attempt.</remarks>
    private sealed class TelemetryClient : ITelegramBotClient
    {
        /// <summary>Number of real decorator attempts entering this transport.</summary>
        private int _attempts;
        /// <summary>Gets the number of attempted deliveries, including cancelled or failed attempts.</summary>
        public int Attempts => Volatile.Read(ref _attempts);
        /// <summary>Test-controlled awaited work before the synthetic response is returned.</summary>
        public Func<object, CancellationToken, Task> BeforeReply { get; init; } = (_, _) => Task.CompletedTask;
        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long BotId => 1;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>Counts one attempt, awaits controlled work, and returns the SDK request's actual response shape.</summary>
        /// <typeparam name="TResponse">The response shape requested by the real SDK convenience method.</typeparam>
        /// <param name="request">SDK request used only by the test transport, never retained in diagnostic metadata.</param>
        /// <param name="cancellationToken">Actual decorator token, enabling caller/deadline ownership regressions.</param>
        /// <returns>A successful synthetic Telegram response when controlled work does not throw.</returns>
        /// <remarks>No retries, timeouts, or cancellation translation are performed by this transport.</remarks>
        public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            await BeforeReply(request, cancellationToken);
            var message = new Message { Id = 17, Chat = new Chat { Id = 7 } };
            object response = request switch
            {
                AnswerCallbackQueryRequest or DeleteMessageRequest => true,
                SendMediaGroupRequest => new[] { message, message },
                GetChatRequest => new ChatFullInfo { Id = 7 },
                GetChatMemberRequest => new ChatMemberMember { User = new Telegram.Bot.Types.User { Id = 7, FirstName = "member" } },
                GetChatMemberCountRequest => 2,
                GetChatAdministratorsRequest => new ChatMember[]
                    { new ChatMemberOwner { User = new Telegram.Bot.Types.User { Id = 7, FirstName = "owner" } } },
                _ => message
            };
            return (TResponse)response;
        }
    }
}
