using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

/// <summary>Durable text/edit/delete delivery with per-bot serialization and a separate realtime callback lane.</summary>
/// <remarks>Retries only definite 429 rejections. Timeouts and interrupted sends are uncertain, never replayed.
/// Existing financial outboxes retain their own completion semantics. No payload, token or exception body is logged.</remarks>
public sealed class TelegramSenderService : BackgroundService
{
    private readonly UserDbContextFactory _factory;
    private readonly BotClientProvider _clients;
    private readonly BotRegistry _registry;
    private readonly TelegramPerformanceOptions _options;
    private readonly TelegramWorkQueue _queue;
    private readonly ILogger<TelegramSenderService> _logger;
    private readonly ConcurrentDictionary<string, byte> _activeBots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<object> Completion, CancellationToken Token)> _waiters = new();
    /// <summary>Live stream-backed requests; never persisted or retained beyond the caller's awaited invocation.</summary>
    private readonly ConcurrentDictionary<string, LiveRequest> _liveRequests = new();
    private readonly Channel<(string BotId, AnswerCallbackQueryRequest Request, long? ChatId, long Received)> _callbacks;
    private readonly MemoryCache _answered = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _slots;
    /// <summary>Prevents new live jobs racing restart recovery before hosted background initialization completes.</summary>
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _rateGate = new();
    private long _nextSend;
    /// <summary>
    /// Transport pacing gate reserved exclusively for callback acknowledgements.
    /// </summary>
    /// <remarks>
    /// It is deliberately not the ordinary <see cref="_rateGate"/>. Sharing one gate meant a bulk broadcast or a reminder
    /// scan could occupy every slot in the shared budget and delay a customer's button response by exactly as long as the
    /// bulk backlog needed, which is one of the reported production symptoms.
    /// </remarks>
    private readonly object _ackRateGate = new();
    private long _nextAck;
    /// <summary>Coalesced notices for refused acknowledgements, keyed by bot and refusal reason.</summary>
    private readonly MemoryCache _ackRejectionNotices = new(new MemoryCacheOptions { SizeLimit = 10000 });
    /// <summary>Coalesced notices for refused ordinary output lanes, keyed by lane.</summary>
    private readonly MemoryCache _laneRefusalNotices = new(new MemoryCacheOptions { SizeLimit = 64 });
    private string _lastBot = "";
    private volatile bool _stopping;
    private volatile bool _underPressure;
    private readonly MemoryCache _pressureNotices = new(new MemoryCacheOptions { SizeLimit = 10000 });

    /// <summary>Interactive target for one callback acknowledgement. Production value: 500 milliseconds.</summary>
    /// <remarks>
    /// The metric is recorded for every attempt. This value only decides whether the attempt is also reported at
    /// Information: a healthy acknowledgement stays at Debug so the operator channel is not turned into a latency
    /// dashboard, while a breach is visible exactly where an operator already looks.
    /// </remarks>
    private const double CallbackAckTargetMs = 500;

    /// <summary>Delay before a pump pass retries after a full output lane refused a durable job.</summary>
    private static readonly TimeSpan OutputLaneRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Creates delivery infrastructure without opening a database or starting network calls.</summary>
    /// <param name="factory">Operation-local users.db factory.</param>
    /// <param name="clients">Raw clients resolved by internal bot identity at execution time.</param>
    /// <param name="registry">Current bot availability, including manual storefront enablement.</param>
    /// <param name="options">Validated startup worker, timeout and bounded-memory settings.</param>
    /// <param name="queue">Bounded eligible-head handoff.</param>
    /// <param name="logger">Structured, payload-free diagnostics.</param>
    public TelegramSenderService(UserDbContextFactory factory, BotClientProvider clients, BotRegistry registry,
        TelegramPerformanceOptions options, TelegramWorkQueue queue, ILogger<TelegramSenderService> logger)
    {
        _factory = factory; _clients = clients; _registry = registry; _options = options; _queue = queue; _logger = logger;
        _slots = new(options.WorkerCount, options.WorkerCount);
        // The acknowledgement lane has its own capacity, sized independently of the ordinary output handoff, so a bulk
        // output burst can never consume the room a button response needs.
        _callbacks = Channel.CreateBounded<(string, AnswerCallbackQueryRequest, long?, long)>(new BoundedChannelOptions(options.CallbackAcknowledgementCapacity)
        { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }

    /// <summary>Recognizes request shapes safe to serialize without open streams or runtime closures.</summary>
    /// <param name="request">Immediate Bot API request; never logged.</param>
    /// <returns>True for text, text/keyboard edits, and deletion only.</returns>
    public static bool Supports(object request) => request is SendMessageRequest or EditMessageTextRequest
        or EditMessageReplyMarkupRequest or EditMessageCaptionRequest or DeleteMessageRequest;

    /// <summary>Commits a private output intent, optionally awaiting the real Telegram response.</summary>
    /// <typeparam name="T">Telegram response type required by the original request.</typeparam>
    /// <param name="botId">Canonical internal bot id, never a token.</param>
    /// <param name="request">Supported Bot API request, snapshotted before returning.</param>
    /// <param name="admissionOnly">True only in an explicit reply builder that discards the response.</param>
    /// <param name="token">Caller cancellation; cancellation after commit leaves the durable job intact.</param>
    /// <param name="priority">Bot-local priority; existing financial outboxes use Critical, log transport uses Low.</param>
    /// <returns>The real response when awaited; default only for an explicit admission-only builder.</returns>
    /// <remarks>Normal UI requests have priority 1. Existing financial workers select priority 0 and retain real message ids.
    /// Backlog resides durably in SQLite; bounded memory admission never waits for a Telegram network slot.</remarks>
    /// <exception cref="InvalidOperationException">Delivery is stopping or the request is unsupported.</exception>
    public async Task<T> EnqueueAsync<T>(string botId, IRequest<T> request, bool admissionOnly, CancellationToken token,
        TelegramWorkPriority priority = TelegramWorkPriority.Normal)
    {
        await _ready.Task.WaitAsync(token);
        if (_stopping || !Supports(request)) throw new InvalidOperationException("Telegram delivery is unavailable.");
        var timer = Stopwatch.StartNew();
        var job = new TelegramDeliveryJob { BotId = botId, Kind = request.GetType().Name,
            Payload = JsonSerializer.Serialize(request, request.GetType(), JsonBotAPI.Options), Priority = (int)priority,
            RequiresLiveCaller = !admissionOnly };
        var waiter = admissionOnly ? null : new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (waiter != null) _waiters[job.CompletionKey] = (waiter, token);
        try
        {
            using (TelegramUpdateLatencyScope.Current?.Measure(TelegramUpdateStage.TelegramEnqueue) ?? default)
                await SqliteOperation.RunAsync(async ct =>
                {
                    await using var db = _factory.CreateDbContext();
                    // A fresh entity/context per retry; never reuse a generated SQLite id after rollback.
                    job.Id = 0;
                    db.TelegramDeliveryJobs.Add(job);
                    await db.SaveChangesAsync(ct);
                    return true;
                }, token);
            _logger.LogDebug("Telegram output admitted. BotId={BotId} JobId={JobId} UpdateId={UpdateId} UserId={UserId} EnqueueMs={EnqueueMs}",
                botId, job.Id, TelegramUpdateLatencyScope.Current?.UpdateId, TelegramInteractionActor.Current, timer.Elapsed.TotalMilliseconds);
            Wake();
            return waiter == null ? default : (T)await waiter.Task.WaitAsync(token);
        }
        finally { if (waiter != null) _waiters.TryRemove(job.CompletionKey, out _); }
    }

    /// <summary>Schedules an awaited stream-backed send without serializing a stream or replacing its real result.</summary>
    /// <typeparam name="T">Telegram response type expected by the existing delivery worker.</typeparam>
    /// <param name="botId">Canonical internal bot id.</param>
    /// <param name="request">Live photo/document/media request; caller owns streams until this task completes.</param>
    /// <param name="token">Caller cancellation, linked to the HTTP attempt.</param>
    /// <param name="priority">Existing durable outboxes use Critical; background logs use Low.</param>
    /// <returns>The actual Telegram result. A process restart never replays this live request.</returns>
    /// <remarks>Only metadata is persisted. The original business outbox remains the recovery authority.</remarks>
    public async Task<T> SendLiveAsync<T>(string botId, IRequest<T> request, CancellationToken token, TelegramWorkPriority priority)
    {
        await _ready.Task.WaitAsync(token);
        if (_stopping) throw new InvalidOperationException("Telegram delivery is stopping.");
        var job = new TelegramDeliveryJob { BotId = botId, Kind = "live", RequiresLiveCaller = true, Priority = (int)priority };
        var waiter = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[job.CompletionKey] = (waiter, token);
        var live = new LiveRequest(async (client, ct) => await client.SendRequest(request, ct), waiter, token);
        _liveRequests[job.CompletionKey] = live;
        using var cancelled = token.Register(live.CancelBeforeStart);
        try
        {
            await SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                job.Id = 0;
                db.TelegramDeliveryJobs.Add(job);
                return await db.SaveChangesAsync(ct);
            }, token);
            Wake();
            // Wait for worker cancellation as well, so the caller cannot dispose a stream still in use by HTTP.
            return (T)await waiter.Task;
        }
        finally { _waiters.TryRemove(job.CompletionKey, out _); _liveRequests.TryRemove(job.CompletionKey, out _); }
    }

    /// <summary>Offers only a blank callback acknowledgement to the independent realtime lane.</summary>
    /// <param name="botId">Internal bot receiving the callback.</param>
    /// <param name="callbackId">Private opaque callback id; never logged or persisted.</param>
    /// <param name="chatId">Optional Telegram chat id used to preserve later alert text after early acknowledgement.</param>
    /// <returns>True if accepted; false on bounded overload, shutdown or when disabled.</returns>
    /// <remarks>This is solely spinner dismissal, never a statement that business work was saved or succeeded.</remarks>
    public bool TryAcknowledge(string botId, string callbackId, long? chatId = null)
    {
        var refused = ResolveAcknowledgementRefusal(botId, callbackId);
        if (refused != null) return false;
        _answered.Set((botId, callbackId), (Answered: false, ChatId: chatId),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        if (_callbacks.Writer.TryWrite((botId, new AnswerCallbackQueryRequest { CallbackQueryId = callbackId }, chatId, Stopwatch.GetTimestamp())))
            return true;
        ReportAcknowledgementRefusal(botId, "ack_lane_full");
        return false;
    }

    /// <summary>Resolves why a callback acknowledgement cannot be offered, without logging anything yet.</summary>
    /// <param name="botId">Internal bot id owning the callback; used only for the coalesced notice key.</param>
    /// <param name="callbackId">Opaque callback id; used only to detect malformed input and never logged.</param>
    /// <returns>A closed-vocabulary refusal reason, or null when the acknowledgement may be offered.</returns>
    /// <remarks>
    /// A missing callback id and a disabled realtime lane are permanent conditions, so they are reported once per bot
    /// rather than on every callback; only the transient <c>ack_lane_full</c> condition is decided by the bounded write
    /// itself. The callback id is never placed in a reason string.
    /// </remarks>
    private string ResolveAcknowledgementRefusal(string botId, string callbackId)
    {
        if (string.IsNullOrWhiteSpace(callbackId))
        {
            // Malformed input rather than pressure: recorded quietly because it points at a caller bug, not an incident.
            _logger.LogDebug("Telegram callback acknowledgement was skipped because the callback id was empty. BotId={BotId}", botId);
            return "callback_id_missing";
        }

        if (!_options.CallbackAckImmediately) return "realtime_lane_disabled";
        if (_stopping) return "delivery_stopping";

        return null;
    }

    /// <summary>Emits one coalesced operator notice for a callback acknowledgement refused by a full lane.</summary>
    /// <param name="botId">Internal bot id whose acknowledgement was refused; never a token.</param>
    /// <param name="reason">Closed-vocabulary refusal reason with no payload data.</param>
    /// <remarks>
    /// Only genuine capacity pressure is reported, because a deliberately disabled lane or a shutdown does not need an
    /// operator alert. The refusal is a UX-only miss, so one notice per bot and reason per minute is enough to make a
    /// sustained problem visible without turning a burst of refused callbacks into a log storm.
    /// </remarks>
    private void ReportAcknowledgementRefusal(string botId, string reason)
    {
        if (_ackRejectionNotices.TryGetValue((botId, reason), out _)) return;
        _ackRejectionNotices.Set((botId, reason), true,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
        _logger.LogWarning(
            "Telegram callback acknowledgement lane is full; the tap is left unacknowledged and the business update still runs. BotId={BotId} Reason={Reason} LaneCapacity={LaneCapacity}",
            botId, reason, _options.CallbackAcknowledgementCapacity);
    }

    /// <summary>Offers a handler acknowledgement to the realtime lane without waiting for Telegram.</summary>
    /// <param name="botId">Internal bot identity owning the callback.</param>
    /// <param name="request">Private callback response; only this request type may bypass per-bot send ordering.</param>
    /// <returns>Whether the bounded realtime queue accepted the acknowledgement; false is a best-effort UX miss.</returns>
    public bool QueueAcknowledgement(string botId, AnswerCallbackQueryRequest request)
    {
        if (request == null || ResolveAcknowledgementRefusal(botId, request.CallbackQueryId) != null) return false;
        if (_callbacks.Writer.TryWrite((botId, request, null, Stopwatch.GetTimestamp()))) return true;
        ReportAcknowledgementRefusal(botId, "ack_lane_full");
        return false;
    }

    /// <summary>Queues a coalesced pressure notice only after the receiver committed the business input.</summary>
    /// <param name="botId">Internal bot that durably accepted the update.</param>
    /// <param name="update">Already-persisted update; used only to resolve the destination chat.</param>
    /// <param name="token">Cancellation of output admission, never of the committed business job.</param>
    /// <param name="businessPressure">Whether the latest durable input sample exceeded its memory window.</param>
    /// <returns>Completion after notice admission when overloaded; normally an immediate no-op.</returns>
    /// <remarks>No notice is sent before persistence. One notice per bot/chat/minute avoids amplifying overload.</remarks>
    public async Task NotifyAcceptedPressureAsync(string botId, Update update, CancellationToken token, bool businessPressure = false)
    {
        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
        if ((!_underPressure && !businessPressure) || !chatId.HasValue || _pressureNotices.TryGetValue((botId, chatId.Value), out _)) return;
        await EnqueueAsync(botId, new SendMessageRequest { ChatId = chatId.Value,
            Text = "درخواست شما ثبت شد، لطفا چند لحظه صبر کنید" }, true, token);
        _pressureNotices.Set((botId, chatId.Value), true,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
    }

    /// <summary>Preserves late authorization/error text when the realtime lane already dismissed the spinner.</summary>
    /// <param name="botId">Internal bot owning the callback.</param>
    /// <param name="request">Handler's acknowledgement request, possibly containing alert text.</param>
    /// <param name="token">Cancellation of durable text admission.</param>
    /// <returns>True when an early acknowledgement already succeeded; otherwise the caller should answer normally.</returns>
    /// <remarks>Late text becomes a normal queued chat message, never a realtime SendMessage bypass.</remarks>
    public async Task<bool> HandlePreviouslyAnsweredAsync(string botId, AnswerCallbackQueryRequest request, CancellationToken token)
    {
        if (!_answered.TryGetValue((botId, request.CallbackQueryId), out (bool Answered, long? ChatId) state) || !state.Answered) return false;
        var chatId = state.ChatId;
        if (!string.IsNullOrWhiteSpace(request.Text) && chatId.HasValue)
            await EnqueueAsync(botId, new SendMessageRequest { ChatId = chatId.Value, Text = request.Text }, true, token);
        return string.IsNullOrEmpty(request.Url) && (string.IsNullOrWhiteSpace(request.Text) || chatId.HasValue);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var db = _factory.CreateDbContext();
            // Sending at process death is ambiguous. Never resend a possibly accepted Telegram message.
            await db.TelegramDeliveryJobs.Where(x => x.Status == "sending").ExecuteUpdateAsync(
                set => set.SetProperty(x => x.Status, "uncertain").SetProperty(x => x.Payload, (string)null), stoppingToken);
            // A lost caller cannot consume a response. Its existing financial saga/outbox owns recovery, not this transport.
            await db.TelegramDeliveryJobs.Where(x => x.Status == "queued" && x.RequiresLiveCaller).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.Status, "failed").SetProperty(x => x.Payload, (string)null), stoppingToken);
            _ready.TrySetResult();
        }
        catch (Exception ex) { _ready.TrySetException(ex); throw; }
        var workers = Enumerable.Range(0, _options.WorkerCount).Select(_ => WorkAsync(stoppingToken)).ToArray();
        var acknowledgers = Enumerable.Range(0, _options.WorkerCount).Select(_ => AcknowledgeAsync(stoppingToken)).ToArray();
        try { await PumpAsync(stoppingToken); }
        finally { await Task.WhenAll(workers.Concat(acknowledgers)); }
    }

    /// <summary>Selects ready bots round-robin, admitting only one head per bot regardless of worker count.</summary>
    /// <param name="token">Host shutdown cancellation.</param>
    /// <returns>The tracked coordinator lifetime.</returns>
    /// <remarks>429 delays live in SQLite. No worker is occupied waiting for another job of its bot.</remarks>
    private async Task PumpAsync(CancellationToken token)
    {
        var maintenance = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(token);
                TelegramDeliveryJob selected = null;
                var handedOff = false;
                try
                {
                    await using var db = _factory.CreateDbContext();
                    var now = DateTime.UtcNow;
                    var active = _activeBots.Keys.ToArray();
                    var enabled = _registry.Bots.Where(x => x.Enabled).Select(x => x.Id).ToArray();
                    var ready = await db.TelegramDeliveryJobs.AsNoTracking().Where(x => x.Status == "queued"
                        && x.NotBeforeUtc <= now && !active.Contains(x.BotId) && enabled.Contains(x.BotId)
                        && !db.TelegramDeliveryJobs.Any(p => p.BotId == x.BotId && (p.Status == "queued" || p.Status == "sending")
                            && (p.Priority < x.Priority || (p.Priority == x.Priority && p.Id < x.Id))))
                        .OrderBy(x => x.Id).Take(_options.QueueSize).ToListAsync(token);
                    var available = ready.Where(x => _registry.GetById(x.BotId) is { Enabled: true }).OrderBy(x => x.BotId, StringComparer.Ordinal).ToArray();
                    selected = available.FirstOrDefault(x => string.CompareOrdinal(x.BotId, _lastBot) > 0) ?? available.FirstOrDefault();
                    if (selected != null)
                    {
                        _activeBots.TryAdd(selected.BotId, 0);
                        _lastBot = selected.BotId;
                        // A full lane refuses instead of waiting. The job is not lost: its durable row stays queued and the
                        // next pass offers it again once the lane drains, so an output backlog can never stall this pump or
                        // the update handler that produced the job.
                        var admission = _queue.TryEnqueue(selected);
                        handedOff = admission.Accepted;
                        if (!handedOff)
                        {
                            ReportOutputLaneRefusal(admission);
                            await Task.Delay(OutputLaneRetryDelay, token);
                        }
                    }
                    if (now >= maintenance)
                    {
                        // A terminal write may fail after HTTP. Only inactive claims can be closed here; never replay them.
                        var currentActive = _activeBots.Keys.ToArray();
                        await db.TelegramDeliveryJobs.Where(x => x.Status == "sending" && !currentActive.Contains(x.BotId))
                            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "uncertain")
                                .SetProperty(x => x.Payload, (string)null), token);
                        var liveCallers = _waiters.Keys.ToArray();
                        await db.TelegramDeliveryJobs.Where(x => x.Status == "queued" && x.RequiresLiveCaller
                            && !liveCallers.Contains(x.CompletionKey) && !currentActive.Contains(x.BotId))
                            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "failed")
                                .SetProperty(x => x.Payload, (string)null), token);
                        var depth = await db.TelegramDeliveryJobs.CountAsync(x => x.Status == "queued", token);
                        _underPressure = depth >= _options.QueueSize;
                        if (depth >= _options.QueueSize)
                            _logger.LogWarning("Telegram output pressure. DurableQueueDepth={QueueDepth} MemoryCapacity={Capacity} Workers={Workers}", depth, _options.QueueSize, _options.WorkerCount);
                        var cutoff = now.AddDays(-7);
                        var expired = db.TelegramDeliveryJobs.Where(x => (x.Status == "sent" || x.Status == "failed") && x.CreatedAtUtc < cutoff)
                            .OrderBy(x => x.Id).Select(x => x.Id).Take(1000);
                        await db.TelegramDeliveryJobs.Where(x => expired.Contains(x.Id))
                            .ExecuteDeleteAsync(token);
                        maintenance = now.AddSeconds(10);
                    }
                }
                finally
                {
                    if (!handedOff)
                    {
                        if (selected != null) _activeBots.TryRemove(selected.BotId, out _);
                        _slots.Release();
                    }
                }
                if (selected == null) await _wake.WaitAsync(TimeSpan.FromMilliseconds(250), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Telegram output scheduling failed. ErrorType={ErrorType}", ex.GetType().Name);
                await Task.Delay(500, token);
            }
        }
    }

    /// <summary>Executes eligible heads, persisting definite failures separately from ambiguous transport results.</summary>
    /// <param name="token">Host shutdown cancellation.</param>
    /// <returns>A tracked worker lifetime; all failures are observed.</returns>
    private async Task WorkAsync(CancellationToken token)
    {
        await foreach (var job in _queue.ReadAllAsync(token))
        {
            var started = Stopwatch.StartNew();
            object result = null;
            Exception failure = null;
            var status = "uncertain";
            var retryAt = DateTime.UtcNow;
            try
            {
                var callerToken = CancellationToken.None;
                LiveRequest live = null;
                if (job.RequiresLiveCaller)
                {
                    if (!_waiters.TryGetValue(job.CompletionKey, out var caller) || caller.Token.IsCancellationRequested)
                    { status = "failed"; continue; }
                    callerToken = caller.Token;
                }
                if (job.Kind == "live" && (!_liveRequests.TryGetValue(job.CompletionKey, out live) || !live.TryStart()))
                { status = "failed"; continue; }
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(token, callerToken);
                var claimed = await SqliteOperation.RunAsync(async ct =>
                {
                    await using var db = _factory.CreateDbContext();
                    return await db.TelegramDeliveryJobs.Where(x => x.Id == job.Id && x.Status == "queued")
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "sending")
                            .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
                }, execution.Token);
                if (claimed != 1) { status = "failed"; continue; }
                await RateLimitAsync(execution.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(job.Kind == "live" ? Math.Max(60, _options.SendTimeoutSeconds) : _options.SendTimeoutSeconds));
                var client = _clients.GetRawClient(job.BotId);
                result = live != null ? await live.Send(client, timeout.Token) : await SendAsync(client, job, timeout.Token);
                status = "sent";
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && job.Attempts < 2 && job.Kind != "live")
            {
                status = "queued";
                retryAt = DateTime.UtcNow.AddSeconds(Math.Max(ex.Parameters?.RetryAfter ?? 1, Math.Pow(2, job.Attempts + 1)));
                failure = ex;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode < 500) { status = "failed"; failure = ex; }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    // Independent bounded persistence records uncertainty even when the host cancelled the HTTP call.
                    using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await SqliteOperation.RunAsync(async ct =>
                    {
                        await using var db = _factory.CreateDbContext();
                        return await db.TelegramDeliveryJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(set =>
                            set.SetProperty(x => x.Status, status).SetProperty(x => x.NotBeforeUtc, retryAt)
                                .SetProperty(x => x.Payload, status == "queued" ? job.Payload : null), ct);
                    }, save.Token);
                }
                catch (Exception ex) { failure ??= ex; status = "uncertain"; }
                if (status != "queued" && _waiters.TryGetValue(job.CompletionKey, out var waiter))
                {
                    if (status == "sent") waiter.Completion.TrySetResult(result);
                    else waiter.Completion.TrySetException(status == "uncertain" ? new TelegramDeliveryUncertainException()
                        : failure ?? new InvalidOperationException("Telegram delivery did not complete."));
                }
                _logger.LogInformation("Telegram output completed. BotId={BotId} JobId={JobId} Status={Status} QueueWaitMs={QueueWaitMs} SendMs={SendMs} Workers={Workers} ActiveBots={ActiveBots}",
                    job.BotId, job.Id, status, (DateTime.UtcNow - job.CreatedAtUtc).TotalMilliseconds - started.Elapsed.TotalMilliseconds,
                    started.Elapsed.TotalMilliseconds, _options.WorkerCount, _activeBots.Count);
                _activeBots.TryRemove(job.BotId, out _);
                _slots.Release();
                Wake();
            }
        }
    }

    /// <summary>Runs acknowledgements independently of ordinary per-bot send serialization and pacing.</summary>
    /// <param name="token">Host shutdown cancellation.</param>
    /// <returns>A tracked bounded callback worker lifetime.</returns>
    /// <remarks>
    /// Only AnswerCallbackQuery can use this path. A one-second attempt cannot guarantee network delivery. The lane is
    /// paced by its own reserved transport budget and never by the ordinary send gate, so no amount of bulk or reminder
    /// traffic can slow a button response. Every attempt records <c>callback_ack_ms</c>, and only an attempt that misses the
    /// interactive target is also logged at Information.
    /// </remarks>
    private async Task AcknowledgeAsync(CancellationToken token)
    {
        await foreach (var item in _callbacks.Reader.ReadAllAsync(token))
        {
            try
            {
                if (await HandlePreviouslyAnsweredAsync(item.BotId, item.Request, token)) continue;
                _answered.TryGetValue((item.BotId, item.Request.CallbackQueryId), out (bool Answered, long? ChatId) state);
                if (Stopwatch.GetElapsedTime(item.Received) > TimeSpan.FromSeconds(1))
                {
                    // Do not lose a late authorization/error alert just because its spinner deadline passed.
                    if (!string.IsNullOrWhiteSpace(item.Request.Text) && state.ChatId.HasValue)
                        await EnqueueAsync(item.BotId, new SendMessageRequest { ChatId = state.ChatId.Value, Text = item.Request.Text }, true, token);
                    continue;
                }
                await RateLimitAcknowledgementAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await _clients.GetRawClient(item.BotId).SendRequest(item.Request, timeout.Token);
                _answered.Set((item.BotId, item.Request.CallbackQueryId), (Answered: true, ChatId: item.ChatId ?? state.ChatId),
                    new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
                RecordAcknowledgementLatency(item.BotId, item.Request.CallbackQueryId, item.Received, "completed");
            }
            catch (Exception ex)
            {
                RecordAcknowledgementLatency(item.BotId, item.Request?.CallbackQueryId, item.Received, "failed");
                _logger.LogDebug("Telegram realtime acknowledgement failed. BotId={BotId} ErrorType={ErrorType}", item.BotId, ex.GetType().Name);
            }
        }
    }

    /// <summary>Records one acknowledgement's age and reports it only when it missed the interactive target.</summary>
    /// <param name="botId">Internal bot id that owned the callback; never a token.</param>
    /// <param name="callbackId">Opaque callback id, used only to resolve the ambient customer for attribution.</param>
    /// <param name="receivedTimestamp">Monotonic timestamp taken when the acknowledgement entered the realtime lane.</param>
    /// <param name="outcome">Closed-vocabulary outcome label: <c>completed</c> or <c>failed</c>.</param>
    /// <remarks>
    /// The metric is recorded for every attempt so an operator can chart the real distribution, while the log line is
    /// emitted only above 500ms. The callback id itself is never logged; only the actor id already published by
    /// <see cref="TelegramInteractionActor"/> is used for attribution.
    /// </remarks>
    private void RecordAcknowledgementLatency(string botId, string callbackId, long receivedTimestamp, string outcome)
    {
        var elapsed = Stopwatch.GetElapsedTime(receivedTimestamp).TotalMilliseconds;
        TelegramLatencyMetrics.Record(TelegramLatencyMetrics.CallbackAckMs, elapsed);
        if (elapsed < CallbackAckTargetMs)
        {
            _logger.LogDebug("Telegram callback acknowledged. BotId={BotId} callback_ack_ms={CallbackAckMs} outcome={Outcome}", botId, elapsed, outcome);
            return;
        }

        _logger.LogInformation(
            "Telegram callback acknowledgement exceeded its interactive target. BotId={BotId} UserId={UserId} callback_ack_ms={CallbackAckMs} outcome={Outcome}",
            botId, TelegramInteractionActor.Current, elapsed, outcome);
    }

    /// <summary>Reserves one acknowledgement transport slot from the lane's own budget, never the ordinary send gate.</summary>
    /// <param name="token">Acknowledgement worker cancellation.</param>
    /// <returns>Completion when the reserved slot becomes available.</returns>
    /// <remarks>
    /// The budget is <see cref="TelegramPerformanceOptions.CallbackAcknowledgementPerSecond"/>, which defaults to
    /// Telegram's documented 25 requests per second. It is a separate counter from <see cref="RateLimitAsync"/>, so ordinary
    /// sends and acknowledgements cannot consume each other's capacity.
    /// </remarks>
    private Task RateLimitAcknowledgementAsync(CancellationToken token)
    {
        double delay;
        lock (_ackRateGate)
        {
            var now = Stopwatch.GetTimestamp();
            var slot = Math.Max(now, _nextAck);
            _nextAck = slot + Stopwatch.Frequency / Math.Max(1, _options.CallbackAcknowledgementPerSecond);
            delay = (slot - now) * 1000.0 / Stopwatch.Frequency;
        }
        return delay <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(delay), token);
    }

    /// <summary>Emits one coalesced operator notice when a bounded output lane refused a durable job.</summary>
    /// <param name="admission">Refused admission carrying the lane and its closed-vocabulary reason.</param>
    /// <remarks>
    /// One notice per lane per thirty seconds. The job itself is only delayed, never discarded, so this is a capacity
    /// observation rather than an incident-worthy event.
    /// </remarks>
    private void ReportOutputLaneRefusal(TelegramQueueAdmission admission)
    {
        var key = admission.Lane.ToString();
        if (_laneRefusalNotices.TryGetValue(key, out _)) return;
        _laneRefusalNotices.Set(key, true,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
        _logger.LogWarning(
            "Telegram output lane is full; the durable job stays queued for a later attempt. Lane={Lane} Reason={RejectionReason} LaneCapacity={LaneCapacity} PendingLaneJobs={PendingLaneJobs} Workers={Workers}",
            admission.Lane, admission.RejectionReason, _queue.CapacityOf(admission.Lane),
            _queue.PendingCount(admission.Lane), _options.WorkerCount);
    }

    /// <summary>Reserves one global 25-request/second transport slot without holding a lock during delay.</summary>
    /// <param name="token">Worker cancellation.</param>
    /// <returns>Completion when the reserved slot becomes available.</returns>
    private Task RateLimitAsync(CancellationToken token)
    {
        double delay;
        lock (_rateGate)
        {
            var now = Stopwatch.GetTimestamp();
            var slot = Math.Max(now, _nextSend);
            _nextSend = slot + Stopwatch.Frequency / 25;
            delay = (slot - now) * 1000.0 / Stopwatch.Frequency;
        }
        return delay <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(delay), token);
    }

    /// <summary>Deserializes only compile-time approved request types into the correct bot transport.</summary>
    /// <param name="client">Raw runtime client for this job's bot.</param>
    /// <param name="job">Private immutable intent loaded from SQLite.</param>
    /// <param name="token">Per-attempt deadline linked to shutdown.</param>
    /// <returns>The real Telegram response, including a message id where supplied.</returns>
    /// <exception cref="InvalidOperationException">The stored request kind is unsupported.</exception>
    private static async Task<object> SendAsync(ITelegramBotClient client, TelegramDeliveryJob job, CancellationToken token)
        => job.Kind switch
        {
            nameof(SendMessageRequest) => await client.SendRequest(JsonSerializer.Deserialize<SendMessageRequest>(job.Payload, JsonBotAPI.Options), token),
            nameof(EditMessageTextRequest) => await client.SendRequest(JsonSerializer.Deserialize<EditMessageTextRequest>(job.Payload, JsonBotAPI.Options), token),
            nameof(EditMessageReplyMarkupRequest) => await client.SendRequest(JsonSerializer.Deserialize<EditMessageReplyMarkupRequest>(job.Payload, JsonBotAPI.Options), token),
            nameof(EditMessageCaptionRequest) => await client.SendRequest(JsonSerializer.Deserialize<EditMessageCaptionRequest>(job.Payload, JsonBotAPI.Options), token),
            nameof(DeleteMessageRequest) => await client.SendRequest(JsonSerializer.Deserialize<DeleteMessageRequest>(job.Payload, JsonBotAPI.Options), token),
            _ => throw new InvalidOperationException("Unsupported Telegram output request kind.")
        };

    /// <summary>Coalesces readiness notifications without retaining one signal per queued output.</summary>
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>Transfers stream ownership to a worker atomically with respect to caller cancellation.</summary>
    private sealed class LiveRequest
    {
        private int _started;
        private readonly TaskCompletionSource<object> _completion;
        private readonly CancellationToken _token;
        /// <summary>Immediate transport invocation; owns no bot context or database scope.</summary>
        internal Func<ITelegramBotClient, CancellationToken, Task<object>> Send { get; }
        /// <summary>Captures the request while its caller keeps all streams alive.</summary>
        /// <param name="send">Transport-only delegate; never a business operation.</param>
        /// <param name="completion">Live caller result, completed only after active HTTP releases its streams.</param>
        /// <param name="token">Caller cancellation.</param>
        internal LiveRequest(Func<ITelegramBotClient, CancellationToken, Task<object>> send,
            TaskCompletionSource<object> completion, CancellationToken token)
        { Send = send; _completion = completion; _token = token; }
        /// <summary>Claims ownership unless cancellation already released the caller's streams.</summary>
        /// <returns>True only for the first live worker; false when cancelled before execution.</returns>
        internal bool TryStart() => Interlocked.CompareExchange(ref _started, 1, 0) == 0;
        /// <summary>Releases an unstarted caller immediately; active requests finish under their linked HTTP cancellation.</summary>
        internal void CancelBeforeStart()
        { if (Interlocked.CompareExchange(ref _started, 2, 0) == 0) _completion.TrySetCanceled(_token); }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _ready.TrySetCanceled(cancellationToken);
        // Receiver/business draining happens first (host reverse registration order). Spend the remaining host
        // allowance on immediately deliverable output; disabled bots and delayed 429 jobs remain durable.
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (!drain.IsCancellationRequested)
            {
                await using var db = _factory.CreateDbContext();
                var enabled = _registry.Bots.Where(x => x.Enabled).Select(x => x.Id).ToArray();
                var now = DateTime.UtcNow;
                if (!await db.TelegramDeliveryJobs.AnyAsync(x => x.Status == "sending"
                    || (x.Status == "queued" && x.NotBeforeUtc <= now && enabled.Contains(x.BotId)), drain.Token)) break;
                Wake();
                await Task.Delay(100, drain.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("Telegram output drain stopped. ErrorType={ErrorType}", ex.GetType().Name); }
        try { await base.StopAsync(cancellationToken); }
        finally
        {
            foreach (var live in _liveRequests.Values) live.CancelBeforeStart();
            foreach (var pair in _waiters)
                if (!_liveRequests.ContainsKey(pair.Key)) pair.Value.Completion.TrySetCanceled(cancellationToken);
        }
    }
}
