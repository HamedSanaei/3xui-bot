using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class XuiV3AccountExpiryReminderService : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ITelegramBotClient _botClient;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly AppConfig _appConfig;
    private readonly TimeZoneInfo _iranTimeZone;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, byte> _sentKeys = new(StringComparer.Ordinal);
    private Task _workerTask;
    private int _disposed;

    public XuiV3AccountExpiryReminderService(
        IConfiguration configuration,
        ITelegramBotClient botClient,
        CredentialsDbContext credentialsDbContext,
        XuiV3PurchaseService purchaseService)
    {
        _configuration = configuration;
        _botClient = botClient;
        _credentialsDbContext = credentialsDbContext;
        _purchaseService = purchaseService;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _iranTimeZone = ResolveIranTimeZone();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_appConfig.AccountExpiryReminderEnabled)
        {
            Console.WriteLine("[XUIv3 ExpiryReminder] Disabled by configuration.");
            return Task.CompletedTask;
        }

        _workerTask ??= Task.Run(() => RunLoopAsync(_shutdown.Token), CancellationToken.None);
        Console.WriteLine($"[XUIv3 ExpiryReminder] Started. hourIran={GetReminderHour()}, days={string.Join(",", GetReminderDays())}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextRun();
                Console.WriteLine($"[XUIv3 ExpiryReminder] Next run in {delay:g}.");
                await Task.Delay(delay, cancellationToken);
                await RunReminderAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XUIv3 ExpiryReminder] Unexpected error: {ex}");
                await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
            }
        }
    }

    private async Task RunReminderAsync(CancellationToken cancellationToken)
    {
        var serverInfo = BuildConfiguredPanelServerInfo();
        var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!clientsResponse.Success)
        {
            Console.WriteLine($"[XUIv3 ExpiryReminder] Could not fetch clients. msg={clientsResponse.Msg}");
            return;
        }

        var nowIran = ToIranTime(DateTime.UtcNow);
        var todayIran = nowIran.Date;
        var reminderDays = GetReminderDays().ToHashSet();
        var enabledServices = _purchaseService.GetEnabledServices();
        var dueByUser = new Dictionary<long, List<ExpiryReminderItem>>();

        foreach (var client in clientsResponse.Obj ?? new List<XuiV3Client>())
        {
            if (client == null || client.Enable == false)
                continue;

            if (!XuiV3ClientPlanEligibility.IsClientInActiveServiceInbounds(client, enabledServices))
                continue;

            var ownerTelegramUserId = GetOwnerTelegramUserId(client);
            if (ownerTelegramUserId <= 0 || IsSuperAdmin(ownerTelegramUserId))
                continue;

            var expiryTime = GetExpiryTime(client);
            if (expiryTime <= 0)
                continue;

            var expiryUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiryTime).UtcDateTime;
            var expiryIranDate = ToIranTime(expiryUtc).Date;
            var daysLeft = (expiryIranDate - todayIran).Days;
            if (!reminderDays.Contains(daysLeft))
                continue;

            var item = new ExpiryReminderItem
            {
                Email = client.Email ?? "",
                ClientId = client.Id,
                DaysLeft = daysLeft,
                ExpiryIranDate = expiryIranDate
            };

            if (!dueByUser.TryGetValue(ownerTelegramUserId, out var items))
            {
                items = new List<ExpiryReminderItem>();
                dueByUser[ownerTelegramUserId] = items;
            }

            items.Add(item);
        }

        var sent = 0;
        var skipped = 0;
        foreach (var group in dueByUser)
        {
            var credUser = await _credentialsDbContext.GetUserStatusWithId(group.Key);
            if (credUser == null || credUser.IsBlocked)
            {
                skipped++;
                continue;
            }

            var chatId = credUser.ChatID > 0 ? credUser.ChatID : credUser.TelegramUserId;
            if (chatId <= 0)
            {
                skipped++;
                continue;
            }

            var freshItems = group.Value
                .OrderBy(item => item.DaysLeft)
                .ThenBy(item => item.Email, StringComparer.OrdinalIgnoreCase)
                .Where(item => MarkNotSent(todayIran, group.Key, item))
                .ToList();

            if (freshItems.Count == 0)
                continue;

            try
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: BuildReminderMessage(freshItems),
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildReminderKeyboard(freshItems),
                    cancellationToken: cancellationToken);
                sent++;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 403)
            {
                Console.WriteLine($"[XUIv3 ExpiryReminder] User blocked bot. chatId={chatId}");
                skipped++;
            }
            catch (ApiRequestException ex)
            {
                Console.WriteLine($"[XUIv3 ExpiryReminder] Telegram error. chatId={chatId}, code={ex.ErrorCode}, message={ex.Message}");
                skipped++;
            }
        }

        Console.WriteLine($"[XUIv3 ExpiryReminder] Finished. dueUsers={dueByUser.Count}, sent={sent}, skipped={skipped}");
    }

    private ServerInfo BuildConfiguredPanelServerInfo()
    {
        if (string.IsNullOrWhiteSpace(_appConfig.XuiV3ApiBaseUrl))
            throw new InvalidOperationException("XuiV3ApiBaseUrl is not configured.");

        return new ServerInfo
        {
            ApiVersion = "v3",
            ApiToken = _appConfig.XuiV3ApiToken,
            Url = _appConfig.XuiV3ApiBaseUrl.TrimEnd('/'),
            RootPath = (_appConfig.XuiV3ApiRootPath ?? string.Empty).Trim('/'),
            SubLinkUrl = string.IsNullOrWhiteSpace(_appConfig.XuiV3SubLinkBaseUrl)
                ? null
                : _appConfig.XuiV3SubLinkBaseUrl.TrimEnd('/'),
            Name = "Configured V3 Panel"
        };
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var nowUtc = DateTime.UtcNow;
        var nowIran = ToIranTime(nowUtc);
        var targetIran = nowIran.Date.AddHours(GetReminderHour());
        if (nowIran >= targetIran)
            targetIran = targetIran.AddDays(1);

        var targetIranUnspecified = DateTime.SpecifyKind(targetIran, DateTimeKind.Unspecified);
        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetIranUnspecified, _iranTimeZone);
        var delay = targetUtc - nowUtc;
        return delay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : delay;
    }

    private int GetReminderHour()
    {
        return Math.Clamp(_appConfig.AccountExpiryReminderHourIran, 0, 23);
    }

    private IEnumerable<int> GetReminderDays()
    {
        return (_appConfig.AccountExpiryReminderDays == null || _appConfig.AccountExpiryReminderDays.Length == 0
                ? new[] { 7, 3, 1 }
                : _appConfig.AccountExpiryReminderDays)
            .Where(day => day > 0)
            .Distinct()
            .OrderByDescending(day => day);
    }

    private bool MarkNotSent(DateTime todayIran, long telegramUserId, ExpiryReminderItem item)
    {
        var key = $"{todayIran:yyyyMMdd}:{telegramUserId}:{item.DaysLeft}:{item.Email}";
        return _sentKeys.TryAdd(key, 0);
    }

    private bool IsSuperAdmin(long telegramUserId)
    {
        return _appConfig.AdminsUserIds?.Contains(telegramUserId) == true;
    }

    private DateTime ToIranTime(DateTime utcDateTime)
    {
        var utc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, _iranTimeZone);
    }

    private static TimeZoneInfo ResolveIranTimeZone()
    {
        foreach (var id in new[] { "Iran Standard Time", "Asia/Tehran" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Iran Standard Time", TimeSpan.FromMinutes(210), "Iran Standard Time", "Iran Standard Time");
    }

    private static long GetOwnerTelegramUserId(XuiV3Client client)
    {
        if (client?.TgId > 0)
            return client.TgId;

        var metadata = TryReadMetadata(client?.Comment);
        return metadata?.TelegramUserId > 0 ? metadata.TelegramUserId : 0;
    }

    private static XuiV3ClientMetadata TryReadMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
        }
        catch
        {
            return null;
        }
    }

    private static long GetExpiryTime(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        var trafficExpiryTime = client.Traffic?.ExpiryTime ?? 0;
        if (trafficExpiryTime != 0)
            return trafficExpiryTime;

        return ReadLongExtra(client, "expiryTime");
    }

    private static long ReadLongExtra(XuiV3Client client, string key)
    {
        if (client?.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null)
            return 0;

        try
        {
            return token.ToObject<long>();
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildReminderMessage(List<ExpiryReminderItem> items)
    {
        var sb = new StringBuilder();
        if (items.Count == 1)
        {
            var item = items[0];
            sb.AppendLine("⏰ یادآوری تمدید اکانت");
            sb.AppendLine();
            sb.AppendLine($"اکانت شما با نام <code>{Html(item.Email)}</code> تا <b>{item.DaysLeft}</b> روز دیگر منقضی می‌شود.");
            sb.AppendLine("لطفاً قبل از منقضی شدن، از داخل ربات تمدیدش کنید تا سرویس قطع نشود.");
            sb.AppendLine();
            sb.AppendLine("برای تمدید سریع می‌توانید از دکمه پایین استفاده کنید.");
            return sb.ToString();
        }

        sb.AppendLine("⏰ یادآوری تمدید اکانت‌ها");
        sb.AppendLine();
        sb.AppendLine("اکانت‌های زیر به‌زودی منقضی می‌شوند:");
        foreach (var item in items)
            sb.AppendLine($"• <code>{Html(item.Email)}</code> - <b>{item.DaysLeft}</b> روز دیگر");

        sb.AppendLine();
        sb.AppendLine("لطفاً قبل از منقضی شدن، از داخل ربات تمدیدشان کنید تا سرویس قطع نشود.");
        sb.AppendLine("برای تمدید سریع هر اکانت، از دکمه‌های پایین استفاده کنید.");
        return sb.ToString();
    }

    private static InlineKeyboardMarkup BuildReminderKeyboard(List<ExpiryReminderItem> items)
    {
        var rows = items
            .Where(item => item.ClientId > 0)
            .Select(item => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"تمدید {item.Email}",
                    XuiV3PurchaseCallbacks.AccountRenew(item.ClientId, 0))
            })
            .ToArray();

        return rows.Length == 0 ? null : new InlineKeyboardMarkup(rows);
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private sealed class ExpiryReminderItem
    {
        public string Email { get; set; }
        public int ClientId { get; set; }
        public int DaysLeft { get; set; }
        public DateTime ExpiryIranDate { get; set; }
    }
}
