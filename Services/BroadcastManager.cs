
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;

public class BroadcastManager
{
    private readonly ITelegramBotClient _bot;
    private readonly Channel<BroadcastItem> _queue;

    private const int WorkerCount = 8;
    private const int DelayMs = 35; // حدود 30 پیام در ثانیه

    public BroadcastManager(ITelegramBotClient bot)
    {
        _bot = bot;
        _queue = Channel.CreateUnbounded<BroadcastItem>();

        for (int i = 0; i < WorkerCount; i++)
            Task.Run(ProcessQueueAsync);
    }

    public async Task EnqueueAsync(IEnumerable<long> chatIds, BroadcastItem template)
    {
        foreach (var id in chatIds)
        {
            await _queue.Writer.WriteAsync(new BroadcastItem
            {
                ChatId = id,
                Text = template.Text,
                FromChatId = template.FromChatId,
                MessageId = template.MessageId,
                IsForward = template.IsForward
            });
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (item.IsForward)
                {
                    await _bot.ForwardMessageAsync(
                        chatId: item.ChatId,
                        fromChatId: item.FromChatId,
                        messageId: item.MessageId);
                }
                else
                {
                    await _bot.SendTextMessageAsync(
                        chatId: item.ChatId,
                        text: item.Text,
                        parseMode: ParseMode.Markdown);
                }

                await Task.Delay(DelayMs);
            }
            catch (ApiRequestException ex)
            {
                if (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter != null)
                {
                    // صبر کن به اندازه‌ای که تلگرام گفته
                    await Task.Delay(ex.Parameters.RetryAfter.Value * 1000);

                    // دوباره بندازش تو صف
                    await _queue.Writer.WriteAsync(item);
                }
                else if (ex.ErrorCode == 403)
                {
                    // کاربر بلاک کرده — فقط رد می‌کنیم
                }
            }
            catch
            {
                // لاگ اگر خواستی
            }
        }
    }

    public class BroadcastItem
    {
        public long ChatId { get; set; }

        public string Text { get; set; }


        public ChatId FromChatId { get; set; }
        public int MessageId { get; set; }

        public bool IsForward { get; set; }
    }
}

