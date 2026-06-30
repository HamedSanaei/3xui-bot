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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerTask ??= Task.Run(() => ProcessQueueAsync(_shutdown.Token), CancellationToken.None);
        Console.WriteLine($"Broadcast worker started. delayMs={_delayMs}, maxRetry={_maxRetryCount}, capacity={_capacity}");
        return Task.CompletedTask;
    }

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

    public async Task<BroadcastJob> EnqueueAsync(
        IEnumerable<long> chatIds,
        BroadcastItem template,
        long adminChatId,
        int statusMessageId,
        long requestedByTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        var normalizedChatIds = chatIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<long>();
        var job = new BroadcastJob
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            AdminChatId = adminChatId,
            StatusMessageId = statusMessageId,
            RequestedByTelegramUserId = requestedByTelegramUserId,
            Total = normalizedChatIds.Count,
            IsForward = template.IsForward,
            BotId = BotContextAccessor.CurrentBotId,
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
                BotId = job.BotId
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
        var bot = _botClientProvider.GetClient(job.BotId);
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
        var bot = _botClientProvider.GetClient(job.BotId);
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

    public class BroadcastItem
    {
        public long ChatId { get; set; }
        public string JobId { get; set; }
        public string Text { get; set; }
        public ChatId FromChatId { get; set; }
        public int MessageId { get; set; }
        public bool IsForward { get; set; }
        public string BotId { get; set; }
        internal int Attempt { get; set; }
    }

    public class BroadcastJob
    {
        internal object SyncRoot { get; } = new();
        public string Id { get; set; }
        public long AdminChatId { get; set; }
        public int StatusMessageId { get; set; }
        public long RequestedByTelegramUserId { get; set; }
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Sent { get; set; }
        public int Blocked { get; set; }
        public int Failed { get; set; }
        public int RetryAttempts { get; set; }
        public int MarkdownFallbackSent { get; set; }
        public bool IsForward { get; set; }
        public string BotId { get; set; }
        public int LastErrorCode { get; set; }
        public string LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
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
