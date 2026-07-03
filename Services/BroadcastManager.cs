using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Queues and sends Telegram broadcast jobs with retry, progress tracking, and refreshable status messages.
/// </summary>
/// <remarks>
/// A broadcast job can use two different bots: <see cref="BroadcastJob.SenderBotId"/> sends the actual
/// recipient messages, while <see cref="BroadcastJob.StatusBotId"/> edits the admin/owner progress message.
/// This keeps tenant broadcasts isolated to the tenant bot audience without moving the owner progress UI out
/// of the owned bot that started the job.
/// </remarks>
public class BroadcastManager : IHostedService, IDisposable
{
    private readonly BotClientProvider _botClientProvider;
    private readonly ILogger<BroadcastManager> _logger;
    private readonly Channel<BroadcastItem> _queue;
    private readonly ConcurrentDictionary<string, BroadcastJob> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _delayMs;
    private readonly int _maxRetryCount;
    private readonly int _capacity;
    private Task _workerTask;
    private int _disposed;

    /// <summary>
    /// Creates the shared broadcast queue worker.
    /// </summary>
    /// <param name="botClientProvider">
    /// Runtime Telegram client provider used to resolve both sender bots and status-message bots by internal
    /// bot id.
    /// </param>
    /// <param name="configuration">
    /// Application configuration that supplies broadcast delay, retry count, and queue capacity.
    /// </param>
    /// <param name="logger">Logger used for broadcast delivery and worker failures.</param>
    public BroadcastManager(
        BotClientProvider botClientProvider,
        IConfiguration configuration,
        ILogger<BroadcastManager> logger)
    {
        _botClientProvider = botClientProvider;
        _logger = logger;

        var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _delayMs = Math.Max(50, appConfig.BroadcastDelayMs);
        _maxRetryCount = Math.Max(0, appConfig.BroadcastMaxRetryCount);

        _capacity = Math.Max(100, appConfig.BroadcastQueueCapacity);
        _queue = Channel.CreateBounded<BroadcastItem>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Starts the background broadcast queue reader.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token supplied by ASP.NET Core.</param>
    /// <returns>A completed task after the queue worker has been scheduled.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerTask ??= Task.Run(() => ProcessQueueAsync(_shutdown.Token), CancellationToken.None);
        Console.WriteLine($"Broadcast worker started. delayMs={_delayMs}, maxRetry={_maxRetryCount}, capacity={_capacity}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the broadcast queue reader and waits briefly for the current worker loop to exit.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token that limits the wait time.</param>
    /// <returns>A task that completes when the worker has stopped or the shutdown wait has elapsed.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        _shutdown.Cancel();

        if (_workerTask == null)
            return;

        try
        {
            await Task.WhenAny(_workerTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Creates a broadcast job and enqueues one delivery item for each destination chat.
    /// </summary>
    /// <param name="chatIds">
    /// Numeric Telegram private chat ids that should receive the broadcast. Values less than or equal to zero
    /// are ignored and duplicate ids are removed before queueing.
    /// </param>
    /// <param name="template">
    /// Broadcast template containing either plain text or a Telegram source chat/message pair for forwarding.
    /// The template must not contain a recipient chat id.
    /// </param>
    /// <param name="adminChatId">
    /// Telegram chat id where the refreshable status message already exists. For tenant broadcasts this is the
    /// colleague owner's owned-bot chat, not a tenant customer chat.
    /// </param>
    /// <param name="statusMessageId">Telegram message id of the status/progress message to edit.</param>
    /// <param name="requestedByTelegramUserId">
    /// Numeric Telegram user id of the super-admin or tenant owner who started the broadcast. Only this user
    /// or a super-admin may refresh the status callback.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for queue writes.</param>
    /// <param name="senderBotId">
    /// Optional internal bot id that should send recipient messages. Pass a tenant bot id for tenant storefront
    /// broadcasts. When omitted, the current bot context is used for both delivery and status updates.
    /// </param>
    /// <returns>
    /// The in-memory broadcast job. The caller can pass <see cref="BroadcastJob.Id"/> to
    /// <see cref="RefreshStatusMessageAsync"/> to immediately render progress.
    /// </returns>
    /// <remarks>
    /// This method is tenant-safe only when the caller supplies an already-filtered audience. It does not read
    /// users from any database and does not broaden the recipient list.
    /// </remarks>
    public async Task<BroadcastJob> EnqueueAsync(
        IEnumerable<long> chatIds,
        BroadcastItem template,
        long adminChatId,
        int statusMessageId,
        long requestedByTelegramUserId,
        CancellationToken cancellationToken = default,
        string senderBotId = null)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        var normalizedChatIds = chatIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<long>();
        var statusBotId = BotContextAccessor.CurrentBotId;
        var deliveryBotId = string.IsNullOrWhiteSpace(senderBotId) ? statusBotId : senderBotId;
        var job = new BroadcastJob
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            AdminChatId = adminChatId,
            StatusMessageId = statusMessageId,
            RequestedByTelegramUserId = requestedByTelegramUserId,
            Total = normalizedChatIds.Count,
            IsForward = template.IsForward,
            BotId = statusBotId,
            StatusBotId = statusBotId,
            SenderBotId = deliveryBotId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _jobs[job.Id] = job;

        foreach (var id in normalizedChatIds)
        {
            await _queue.Writer.WriteAsync(new BroadcastItem
            {
                JobId = job.Id,
                ChatId = id,
                Text = template.Text,
                FromChatId = template.FromChatId,
                MessageId = template.MessageId,
                IsForward = template.IsForward,
                BotId = job.SenderBotId
            }, cancellationToken);
        }

        Console.WriteLine($"Broadcast queued. jobId={job.Id}, total={normalizedChatIds.Count}, admin={requestedByTelegramUserId}");
        return job;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_queue.Reader.TryRead(out var item))
            {
                var result = await SendWithRetryAsync(item, cancellationToken);
                ApplyResult(item, result);
                if (TryGetCompletedJob(item.JobId, out var job))
                    await CompleteJobAsync(job, cancellationToken);

                await DelayBetweenMessagesAsync(cancellationToken);
            }
        }
    }

    private async Task<BroadcastSendResult> SendWithRetryAsync(BroadcastItem item, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendAsync(item, ParseMode.Markdown, cancellationToken);
                return BroadcastSendResult.CreateSent(item.Attempt);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter != null)
            {
                item.Attempt++;
                if (item.Attempt > _maxRetryCount)
                {
                    Console.WriteLine($"Broadcast failed after retry limit. jobId={item.JobId}, chatId={item.ChatId}, error={ex.Message}");
                    return BroadcastSendResult.CreateFailed(item.Attempt, ex.ErrorCode, ex.Message);
                }

                var retryAfter = TimeSpan.FromSeconds(ex.Parameters.RetryAfter.Value + 1);
                Console.WriteLine($"Broadcast rate limited. jobId={item.JobId}, waiting={retryAfter.TotalSeconds}s, chatId={item.ChatId}, attempt={item.Attempt}");

                await Task.Delay(retryAfter, cancellationToken);
            }
            catch (ApiRequestException ex) when (!item.IsForward && ex.ErrorCode == 400 && LooksLikeMarkdownError(ex))
            {
                try
                {
                    await SendAsync(item, null, cancellationToken);
                    return BroadcastSendResult.CreateSent(item.Attempt, markdownFallback: true);
                }
                catch (ApiRequestException fallbackEx)
                {
                    return ClassifyTelegramFailure(item, fallbackEx);
                }
            }
            catch (ApiRequestException ex)
            {
                return ClassifyTelegramFailure(item, ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BroadcastSendResult.CreateFailed(item.Attempt, 0, "Broadcast stopped.");
            }
            catch (Exception ex)
            {
                item.Attempt++;
                if (item.Attempt > _maxRetryCount)
                {
                    Console.WriteLine($"Broadcast failed after retry limit. jobId={item.JobId}, chatId={item.ChatId}, error={ex.Message}");
                    return BroadcastSendResult.CreateFailed(item.Attempt, 0, ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        return BroadcastSendResult.CreateFailed(item.Attempt, 0, "Broadcast stopped.");
    }

    private Task SendAsync(BroadcastItem item, ParseMode? parseMode, CancellationToken cancellationToken)
    {
        var bot = _botClientProvider.GetClient(item.BotId);
        if (item.IsForward)
        {
            return bot.ForwardMessageAsync(
                chatId: item.ChatId,
                fromChatId: item.FromChatId,
                messageId: item.MessageId,
                cancellationToken: cancellationToken);
        }

        return bot.SendTextMessageAsync(
            chatId: item.ChatId,
            text: item.Text ?? string.Empty,
            parseMode: parseMode,
            cancellationToken: cancellationToken);
    }

    private async Task DelayBetweenMessagesAsync(CancellationToken cancellationToken)
    {
        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);
    }

    private BroadcastSendResult ClassifyTelegramFailure(BroadcastItem item, ApiRequestException ex)
    {
        if (ex.ErrorCode == 403)
        {
            Console.WriteLine($"Broadcast skipped: blocked. jobId={item.JobId}, chatId={item.ChatId}");
            return BroadcastSendResult.CreateBlocked(item.Attempt, ex.ErrorCode, ex.Message);
        }

        Console.WriteLine($"Broadcast skipped. jobId={item.JobId}, chatId={item.ChatId}, telegramErrorCode={ex.ErrorCode}, message={ex.Message}");
        return BroadcastSendResult.CreateFailed(item.Attempt, ex.ErrorCode, ex.Message);
    }

    private void ApplyResult(BroadcastItem item, BroadcastSendResult result)
    {
        if (!_jobs.TryGetValue(item.JobId, out var job))
            return;

        lock (job.SyncRoot)
        {
            job.Processed++;
            job.RetryAttempts += Math.Max(0, result.RetryAttempts);
            if (result.Sent)
                job.Sent++;
            else if (result.Blocked)
                job.Blocked++;
            else
                job.Failed++;

            if (result.MarkdownFallback)
                job.MarkdownFallbackSent++;

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                job.LastError = result.ErrorMessage;
                job.LastErrorCode = result.ErrorCode;
            }

            job.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private bool TryGetCompletedJob(string jobId, out BroadcastJob job)
    {
        job = null;
        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId, out var current))
            return false;

        lock (current.SyncRoot)
        {
            if (current.CompletedAtUtc.HasValue || current.Processed < current.Total)
                return false;

            current.CompletedAtUtc = DateTime.UtcNow;
            current.UpdatedAtUtc = current.CompletedAtUtc.Value;
            job = current;
            return true;
        }
    }

    private async Task CompleteJobAsync(BroadcastJob job, CancellationToken cancellationToken)
    {
        await RefreshStatusMessageAsync(job.Id, cancellationToken);

        try
        {
            await SendFinalSummaryAsync(job, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Broadcast final summary failed. jobId={job.Id}, error={ex.Message}");
        }
    }

    public BroadcastJob GetJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;

        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    /// <summary>
    /// Re-renders the Telegram status message for an active or completed broadcast job.
    /// </summary>
    /// <param name="jobId">In-memory broadcast job id returned by <see cref="EnqueueAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram edit call.</param>
    /// <returns>A task that completes after the status message is edited or skipped.</returns>
    /// <remarks>
    /// Status edits use <see cref="BroadcastJob.StatusBotId"/>, not the sender bot. This is important for
    /// tenant broadcasts where recipients must see the tenant bot but the owner progress message belongs to
    /// the owned bot chat.
    /// </remarks>
    public async Task RefreshStatusMessageAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        if (job == null || job.AdminChatId == 0 || job.StatusMessageId == 0)
            return;

        try
        {
            await EditStatusMessageAsync(job, cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message?.IndexOf("message is not modified", StringComparison.OrdinalIgnoreCase) >= 0)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Broadcast status refresh failed. jobId={job.Id}, error={ex.Message}");
        }
    }

    private async Task EditStatusMessageAsync(BroadcastJob job, CancellationToken cancellationToken)
    {
        var bot = _botClientProvider.GetClient(job.StatusBotId ?? job.BotId);
        try
        {
            await bot.EditMessageTextAsync(
                chatId: job.AdminChatId,
                messageId: job.StatusMessageId,
                text: BuildStatusText(job),
                parseMode: ParseMode.Html,
                replyMarkup: BuildStatusKeyboard(job),
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter != null)
        {
            await Task.Delay(TimeSpan.FromSeconds(ex.Parameters.RetryAfter.Value + 1), cancellationToken);
            await bot.EditMessageTextAsync(
                chatId: job.AdminChatId,
                messageId: job.StatusMessageId,
                text: BuildStatusText(job),
                parseMode: ParseMode.Html,
                replyMarkup: BuildStatusKeyboard(job),
                cancellationToken: cancellationToken);
        }
    }

    private async Task SendFinalSummaryAsync(BroadcastJob job, CancellationToken cancellationToken)
    {
        var bot = _botClientProvider.GetClient(job.StatusBotId ?? job.BotId);
        try
        {
            await bot.SendTextMessageAsync(
                chatId: job.AdminChatId,
                text: BuildStatusText(job, finalSummary: true),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter != null)
        {
            await Task.Delay(TimeSpan.FromSeconds(ex.Parameters.RetryAfter.Value + 1), cancellationToken);
            await bot.SendTextMessageAsync(
                chatId: job.AdminChatId,
                text: BuildStatusText(job, finalSummary: true),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Builds the inline refresh button shown under a broadcast status message.
    /// </summary>
    /// <param name="job">Broadcast job whose id is embedded in the callback data.</param>
    /// <returns>Inline keyboard containing a single refresh button.</returns>
    public static InlineKeyboardMarkup BuildStatusKeyboard(BroadcastJob job)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("به‌روزرسانی وضعیت", $"broadcast_status_{job.Id}")
            }
        });
    }

    /// <summary>
    /// Builds the HTML status text for live progress and final broadcast summaries.
    /// </summary>
    /// <param name="job">Broadcast job whose counters should be rendered.</param>
    /// <param name="finalSummary">
    /// Pass <c>true</c> when the text is being sent as a separate final summary message instead of editing the
    /// live status message.
    /// </param>
    /// <returns>HTML-safe Telegram message text containing total, success, blocked, failed, and retry counts.</returns>
    public static string BuildStatusText(BroadcastJob job, bool finalSummary = false)
    {
        if (job == null)
            return "وضعیت ارسال عمومی پیدا نشد.";

        lock (job.SyncRoot)
        {
            var isDone = job.CompletedAtUtc.HasValue || job.Processed >= job.Total;
            var title = finalSummary || isDone
                ? "✅ ارسال عمومی تمام شد"
                : "📨 وضعیت ارسال عمومی";
            var percent = job.Total <= 0 ? 100 : (int)Math.Floor(job.Processed * 100m / job.Total);
            var elapsed = (job.CompletedAtUtc ?? DateTime.UtcNow) - job.CreatedAtUtc;
            var builder = new StringBuilder();
            builder.AppendLine($"<b>{Html(title)}</b>");
            builder.AppendLine();
            builder.AppendLine($"شناسه ارسال: <code>{Html(job.Id)}</code>");
            builder.AppendLine($"نوع ارسال: <code>{(job.IsForward ? "forward" : "text")}</code>");
            builder.AppendLine($"کل مقصدها: <code>{job.Total}</code>");
            builder.AppendLine($"پردازش‌شده: <code>{job.Processed}</code> / <code>{job.Total}</code> (<code>{percent}%</code>)");
            builder.AppendLine($"موفق: <code>{job.Sent}</code>");
            builder.AppendLine($"ربات را بسته/بلاک کرده‌اند: <code>{job.Blocked}</code>");
            builder.AppendLine($"خطاهای دیگر: <code>{job.Failed}</code>");
            builder.AppendLine($"تلاش مجدد: <code>{job.RetryAttempts}</code>");
            if (job.MarkdownFallbackSent > 0)
                builder.AppendLine($"ارسال بدون Markdown: <code>{job.MarkdownFallbackSent}</code>");
            if (!string.IsNullOrWhiteSpace(job.LastError))
                builder.AppendLine($"آخرین خطا: <code>{Html(job.LastErrorCode == 0 ? job.LastError : $"{job.LastErrorCode}: {job.LastError}")}</code>");
            builder.AppendLine($"زمان سپری‌شده: <code>{FormatDuration(elapsed)}</code>");
            builder.AppendLine($"آخرین بروزرسانی: <code>{Html(DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi())}</code>");
            return builder.ToString();
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours:0}:{duration.Minutes:00}:{duration.Seconds:00}";

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string Html(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static bool LooksLikeMarkdownError(ApiRequestException ex)
    {
        return ex.Message?.IndexOf("parse", StringComparison.OrdinalIgnoreCase) >= 0 ||
               ex.Message?.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0 ||
               ex.Message?.IndexOf("markdown", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            if (!_shutdown.IsCancellationRequested)
                _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// One queued Telegram delivery item inside a broadcast job.
    /// </summary>
    /// <remarks>
    /// The item is created from a broadcast template and a single destination chat. <see cref="BotId"/> is the
    /// internal bot id that sends this recipient message and may differ from the job status bot id.
    /// </remarks>
    public class BroadcastItem
    {
        /// <summary>
        /// Numeric Telegram chat id that receives this item.
        /// </summary>
        public long ChatId { get; set; }

        /// <summary>
        /// In-memory broadcast job id that owns this item.
        /// </summary>
        public string JobId { get; set; }

        /// <summary>
        /// Text to send when <see cref="IsForward"/> is <c>false</c>.
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Telegram source chat used when forwarding a channel post.
        /// </summary>
        public ChatId FromChatId { get; set; }

        /// <summary>
        /// Telegram message id in <see cref="FromChatId"/> used for forwarded broadcasts.
        /// </summary>
        public int MessageId { get; set; }

        /// <summary>
        /// Whether this item should forward an existing Telegram post instead of sending <see cref="Text"/>.
        /// </summary>
        public bool IsForward { get; set; }

        /// <summary>
        /// Internal bot id that sends this recipient message.
        /// </summary>
        public string BotId { get; set; }

        /// <summary>
        /// Number of retry attempts already consumed for this item.
        /// </summary>
        internal int Attempt { get; set; }
    }

    /// <summary>
    /// In-memory progress record for a broadcast job.
    /// </summary>
    /// <remarks>
    /// Jobs are not persisted across process restarts. Counters are updated by the single broadcast worker and
    /// guarded by <see cref="SyncRoot"/> because Telegram callback refreshes can read them concurrently.
    /// </remarks>
    public class BroadcastJob
    {
        internal object SyncRoot { get; } = new();

        /// <summary>
        /// Short in-memory id used in status callback data.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Telegram chat id where the status message is edited and the final summary is sent.
        /// </summary>
        public long AdminChatId { get; set; }

        /// <summary>
        /// Telegram message id of the refreshable status message.
        /// </summary>
        public int StatusMessageId { get; set; }

        /// <summary>
        /// Telegram user id of the admin or tenant owner who started the broadcast.
        /// </summary>
        public long RequestedByTelegramUserId { get; set; }

        /// <summary>
        /// Total number of unique recipient chats queued for the job.
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Number of queued recipient items that have reached a terminal sent, blocked, or failed state.
        /// </summary>
        public int Processed { get; set; }

        /// <summary>
        /// Number of recipient messages successfully sent or forwarded.
        /// </summary>
        public int Sent { get; set; }

        /// <summary>
        /// Number of recipients that blocked the bot or denied private-message delivery.
        /// </summary>
        public int Blocked { get; set; }

        /// <summary>
        /// Number of recipient deliveries that failed for non-block reasons after retry handling.
        /// </summary>
        public int Failed { get; set; }

        /// <summary>
        /// Total retry attempts consumed across all queued items.
        /// </summary>
        public int RetryAttempts { get; set; }

        /// <summary>
        /// Number of text messages resent without Markdown after Telegram rejected Markdown entities.
        /// </summary>
        public int MarkdownFallbackSent { get; set; }

        /// <summary>
        /// Whether the broadcast forwards an existing Telegram post instead of sending plain text.
        /// </summary>
        public bool IsForward { get; set; }

        /// <summary>
        /// Backward-compatible status bot id. New code should prefer <see cref="StatusBotId"/>.
        /// </summary>
        public string BotId { get; set; }

        /// <summary>
        /// Internal bot id that edits progress and sends the final summary.
        /// </summary>
        public string StatusBotId { get; set; }

        /// <summary>
        /// Internal bot id that sends or forwards the broadcast to recipients.
        /// </summary>
        public string SenderBotId { get; set; }

        /// <summary>
        /// Telegram error code from the most recent failed delivery, or zero for non-Telegram failures.
        /// </summary>
        public int LastErrorCode { get; set; }

        /// <summary>
        /// Most recent delivery error text, safe only for admin/owner status messages after HTML encoding.
        /// </summary>
        public string LastError { get; set; }

        /// <summary>
        /// UTC timestamp when the job was queued.
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// UTC timestamp of the latest counter update.
        /// </summary>
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>
        /// UTC timestamp when all queued items reached a terminal state, or <c>null</c> while still running.
        /// </summary>
        public DateTime? CompletedAtUtc { get; set; }
    }

    private class BroadcastSendResult
    {
        public bool Sent { get; set; }
        public bool Blocked { get; set; }
        public bool MarkdownFallback { get; set; }
        public int RetryAttempts { get; set; }
        public int ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static BroadcastSendResult CreateSent(int retryAttempts, bool markdownFallback = false)
            => new() { Sent = true, RetryAttempts = retryAttempts, MarkdownFallback = markdownFallback };

        public static BroadcastSendResult CreateBlocked(int retryAttempts, int errorCode, string errorMessage)
            => new() { Blocked = true, RetryAttempts = retryAttempts, ErrorCode = errorCode, ErrorMessage = errorMessage };

        public static BroadcastSendResult CreateFailed(int retryAttempts, int errorCode, string errorMessage)
            => new() { RetryAttempts = retryAttempts, ErrorCode = errorCode, ErrorMessage = errorMessage };
    }
}
