using System.Threading.Channels;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public class BroadcastManager : IHostedService, IDisposable
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<BroadcastManager> _logger;
    private readonly Channel<BroadcastItem> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _delayMs;
    private readonly int _maxRetryCount;
    private readonly int _capacity;
    private Task _workerTask;
    private int _disposed;

    public BroadcastManager(
        ITelegramBotClient bot,
        IConfiguration configuration,
        ILogger<BroadcastManager> logger)
    {
        _bot = bot;
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
        _logger.LogInformation(
            "Broadcast worker started. delayMs={DelayMs}, maxRetry={MaxRetry}, capacity={Capacity}",
            _delayMs,
            _maxRetryCount,
            _capacity);
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

    public async Task<int> EnqueueAsync(IEnumerable<long> chatIds, BroadcastItem template, CancellationToken cancellationToken = default)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        var normalizedChatIds = chatIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<long>();

        foreach (var id in normalizedChatIds)
        {
            await _queue.Writer.WriteAsync(new BroadcastItem
            {
                ChatId = id,
                Text = template.Text,
                FromChatId = template.FromChatId,
                MessageId = template.MessageId,
                IsForward = template.IsForward
            }, cancellationToken);
        }

        _logger.LogInformation("Broadcast queued for {Count} users.", normalizedChatIds.Count);
        return normalizedChatIds.Count;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_queue.Reader.TryRead(out var item))
            {
                await SendWithRetryAsync(item, cancellationToken);
                await DelayBetweenMessagesAsync(cancellationToken);
            }
        }
    }

    private async Task SendWithRetryAsync(BroadcastItem item, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendAsync(item, ParseMode.Markdown, cancellationToken);
                _logger.LogInformation("Broadcast sent. chatId={ChatId}", item.ChatId);
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter != null)
            {
                item.Attempt++;
                if (item.Attempt > _maxRetryCount)
                {
                    _logger.LogWarning("Broadcast skipped after retry limit. chatId={ChatId}, error={Error}", item.ChatId, ex.Message);
                    return;
                }

                var retryAfter = TimeSpan.FromSeconds(ex.Parameters.RetryAfter.Value + 1);
                _logger.LogWarning(
                    "Telegram rate limit hit. waiting={RetryAfterSeconds}s, chatId={ChatId}, attempt={Attempt}",
                    retryAfter.TotalSeconds,
                    item.ChatId,
                    item.Attempt);

                await Task.Delay(retryAfter, cancellationToken);
            }
            catch (ApiRequestException ex) when (!item.IsForward && ex.ErrorCode == 400 && LooksLikeMarkdownError(ex))
            {
                try
                {
                    await SendAsync(item, null, cancellationToken);
                    _logger.LogInformation("Broadcast sent without Markdown. chatId={ChatId}", item.ChatId);
                    return;
                }
                catch (ApiRequestException fallbackEx)
                {
                    LogTelegramSkip(item, fallbackEx);
                    return;
                }
            }
            catch (ApiRequestException ex)
            {
                LogTelegramSkip(item, ex);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                item.Attempt++;
                if (item.Attempt > _maxRetryCount)
                {
                    _logger.LogWarning(ex, "Broadcast failed after retry limit. chatId={ChatId}", item.ChatId);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private Task SendAsync(BroadcastItem item, ParseMode? parseMode, CancellationToken cancellationToken)
    {
        if (item.IsForward)
        {
            return _bot.ForwardMessageAsync(
                chatId: item.ChatId,
                fromChatId: item.FromChatId,
                messageId: item.MessageId,
                cancellationToken: cancellationToken);
        }

        return _bot.SendTextMessageAsync(
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

    private void LogTelegramSkip(BroadcastItem item, ApiRequestException ex)
    {
        if (ex.ErrorCode == 403)
        {
            _logger.LogInformation("Broadcast skipped because user blocked bot. chatId={ChatId}", item.ChatId);
            return;
        }

        _logger.LogWarning(
            "Broadcast skipped. chatId={ChatId}, telegramErrorCode={ErrorCode}, message={Message}",
            item.ChatId,
            ex.ErrorCode,
            ex.Message);
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
        public string Text { get; set; }
        public ChatId FromChatId { get; set; }
        public int MessageId { get; set; }
        public bool IsForward { get; set; }
        internal int Attempt { get; set; }
    }
}
