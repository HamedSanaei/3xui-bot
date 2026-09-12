using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Sends the existing once-daily Tehran-time reminders for accounts approaching their configured time expiry.
/// </summary>
/// <remarks>
/// This service remains responsible only for positive absolute expiry dates. Accounts whose finite traffic is already
/// exhausted are excluded so the independent volume-reminder worker owns 80/90/99 percent consumption messaging.
/// Existing scheduling, grouping, Persian text, callback buttons, and in-memory once-per-day deduplication are retained.
/// </remarks>
public class XuiV3AccountExpiryReminderService : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotRegistry _botRegistry;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly CredentialsStore _credentialsDbContext;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly AppConfig _appConfig;
    private readonly TimeZoneInfo _iranTimeZone;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, byte> _sentKeys = new(StringComparer.Ordinal);
    private Task _workerTask;
    private int _disposed;

    /// <summary>Creates the expiry worker without retaining a global credentials change tracker.</summary>
    /// <param name="configuration">Reminder schedule and private panel transport settings.</param>
    /// <param name="botClientProvider">Bot-keyed clients used for reminder delivery.</param>
    /// <param name="botRegistry">Current owned and tenant runtime metadata.</param>
    /// <param name="botContextAccessor">Restores the correct bot identity during each reminder delivery.</param>
    /// <param name="credentialsDbContext">Factory-backed global profile store; every read is detached.</param>
    /// <param name="purchaseService">Shared catalog and account formatting service.</param>
    /// <remarks>Construction performs no database or Telegram write. The tracked worker owns its shutdown lifetime.</remarks>
    public XuiV3AccountExpiryReminderService(
        IConfiguration configuration,
        BotClientProvider botClientProvider,
        BotRegistry botRegistry,
        BotContextAccessor botContextAccessor,
        CredentialsStore credentialsDbContext,
        XuiV3PurchaseService purchaseService)
    {
        _configuration = configuration;
        _botClientProvider = botClientProvider;
        _botRegistry = botRegistry;
        _botContextAccessor = botContextAccessor;
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

    /// <summary>
    /// Performs one complete daily expiry scan and sends the existing grouped time reminders.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token for the panel, credentials database, and Telegram calls.</param>
    /// <returns>A task that completes after the scan is skipped, fails safely, or sends all due grouped reminders.</returns>
    /// <remarks>
    /// The complete list request remains the source of truth. Before evaluating days remaining, the shared usage
    /// resolver excludes a finite account that consumed its quota or whose traffic row was disabled at/above 99
    /// percent. For each due account, an identity-checked detail GET supplies only the customer-authored comment;
    /// failure defers that account before daily deduplication. No volume threshold message is generated here.
    /// </remarks>
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
        var dueByUser = new Dictionary<string, List<ExpiryReminderItem>>(StringComparer.Ordinal);
        var commentDeferred = 0;

        foreach (var client in clientsResponse.Obj ?? new List<XuiV3Client>())
        {
            if (client == null || client.Enable == false)
                continue;

            var usageSnapshot = XuiV3ClientUsageResolver.Resolve(client);
            if (XuiV3ClientUsageResolver.IsVolumeEndedForTimeReminder(usageSnapshot))
                continue;

            if (!XuiV3ClientPlanEligibility.IsClientInActiveServiceInbounds(client, enabledServices))
                continue;

            var metadata = TryReadMetadata(client.Comment);

            // A provisional tenant card-to-card courtesy client is not a paid subscription: its 1 GB / 1 day allowance
            // only exists until the store owner reviews the receipt, and the real entitlement is applied to this same
            // client at approval. A normal paid-service expiry reminder would tell the customer to renew a plan they
            // already bought. Finalization replaces this plan key, so the account becomes eligible again afterwards.
            if (TenantCardProvisionalProvisioningService.IsProvisionalPlanKey(metadata?.PlanKey))
                continue;

            var ownerTelegramUserId = GetOwnerTelegramUserId(client, metadata);
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

            var commentResolution = await XuiV3ReminderCommentResolver.ResolveAsync(
                serverInfo,
                _configuration,
                client,
                cancellationToken);
            if (!commentResolution.Success)
            {
                // The daily dedup key is intentionally untouched: a later same-day retry can still notify this
                // account after the read-only detail endpoint becomes available.
                Console.WriteLine(
                    $"[XUIv3 ExpiryReminder] Comment enrichment deferred. clientId={client.Id}, result={commentResolution.Status}");
                commentDeferred++;
                continue;
            }

            var item = new ExpiryReminderItem
            {
                Email = client.Email ?? "",
                ClientId = client.Id,
                DaysLeft = daysLeft,
                ExpiryIranDate = expiryIranDate,
                BotId = string.IsNullOrWhiteSpace(metadata?.CreatedByBotId)
                    ? BotContextAccessor.DefaultBotId
                    : metadata.CreatedByBotId,
                UserComment = commentResolution.UserComment
            };

            var groupKey = BuildReminderGroupKey(item.BotId, ownerTelegramUserId);
            if (!dueByUser.TryGetValue(groupKey, out var items))
            {
                items = new List<ExpiryReminderItem>();
                dueByUser[groupKey] = items;
            }

            items.Add(item);
        }

        var sent = 0;
        var skipped = 0;
        foreach (var group in dueByUser)
        {
            var (botId, telegramUserId) = ParseReminderGroupKey(group.Key);
            var credUser = await _credentialsDbContext.GetUserStatusWithId(telegramUserId);
            if (credUser == null || credUser.IsBlocked)
            {
                skipped++;
                continue;
            }

            var bot = _botRegistry.GetById(botId);
            var botClient = _botClientProvider.GetClient(bot?.Id);
            var chatId = credUser.ChatID > 0 ? credUser.ChatID : credUser.TelegramUserId;
            if (chatId <= 0)
            {
                skipped++;
                continue;
            }

            var freshItems = group.Value
                .OrderBy(item => item.DaysLeft)
                .ThenBy(item => item.Email, StringComparer.OrdinalIgnoreCase)
                .Where(item => MarkNotSent(todayIran, botId, telegramUserId, item))
                .ToList();

            if (freshItems.Count == 0)
                continue;

            try
            {
                using (_botContextAccessor.Push(new BotRuntimeContext { Config = bot, Client = botClient }))
                {
                    await botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: BuildReminderMessage(freshItems),
                        parseMode: ParseMode.Html,
                        replyMarkup: BuildReminderKeyboard(freshItems),
                        cancellationToken: cancellationToken);
                }
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

        Console.WriteLine(
            $"[XUIv3 ExpiryReminder] Finished. dueUsers={dueByUser.Count}, sent={sent}, skipped={skipped}, commentDeferred={commentDeferred}");
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

    private bool MarkNotSent(DateTime todayIran, string botId, long telegramUserId, ExpiryReminderItem item)
    {
        var key = $"{todayIran:yyyyMMdd}:{botId}:{telegramUserId}:{item.DaysLeft}:{item.Email}";
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

    private static long GetOwnerTelegramUserId(XuiV3Client client, XuiV3ClientMetadata metadata)
    {
        if (client?.TgId > 0)
            return client.TgId;

        return metadata?.TelegramUserId > 0 ? metadata.TelegramUserId : 0;
    }

    private static string BuildReminderGroupKey(string botId, long telegramUserId)
    {
        return $"{(string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.DefaultBotId : botId)}:{telegramUserId}";
    }

    private static (string BotId, long TelegramUserId) ParseReminderGroupKey(string key)
    {
        var parts = (key ?? "").Split(':', 2);
        if (parts.Length == 2 && long.TryParse(parts[1], out var userId))
            return (parts[0], userId);

        return (BotContextAccessor.DefaultBotId, 0);
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

    /// <summary>
    /// Builds one Persian HTML reminder for a single account or a grouped set owned by the same bot user.
    /// </summary>
    /// <param name="items">
    /// Due accounts whose detail identity and metadata were verified. Each optional user comment is already bounded
    /// but remains untrusted text and is HTML-encoded here.
    /// </param>
    /// <returns>
    /// Customer-facing reminder text. Accounts without a registered <c>UserComment</c> omit the comment line.
    /// </returns>
    /// <remarks>
    /// Grouped output places each comment immediately beneath its account so comments cannot be associated with a
    /// neighboring account. This method performs no panel, database, wallet, payment, or Telegram operation.
    /// </remarks>
    private static string BuildReminderMessage(List<ExpiryReminderItem> items)
    {
        var sb = new StringBuilder();
        if (items.Count == 1)
        {
            var item = items[0];
            sb.AppendLine("⏰ یادآوری تمدید اکانت");
            sb.AppendLine();
            sb.AppendLine($"اکانت شما با نام <code>{Html(item.Email)}</code> تا <b>{item.DaysLeft}</b> روز دیگر منقضی می‌شود.");
            if (!string.IsNullOrWhiteSpace(item.UserComment))
                sb.AppendLine($"📝 کامنت: <code>{Html(item.UserComment)}</code>");
            sb.AppendLine("لطفاً قبل از منقضی شدن، از داخل ربات تمدیدش کنید تا سرویس قطع نشود.");
            sb.AppendLine();
            sb.AppendLine("برای تمدید سریع می‌توانید از دکمه پایین استفاده کنید.");
            return sb.ToString();
        }

        sb.AppendLine("⏰ یادآوری تمدید اکانت‌ها");
        sb.AppendLine();
        sb.AppendLine("اکانت‌های زیر به‌زودی منقضی می‌شوند:");
        foreach (var item in items)
        {
            sb.AppendLine($"• <code>{Html(item.Email)}</code> - <b>{item.DaysLeft}</b> روز دیگر");
            if (!string.IsNullOrWhiteSpace(item.UserComment))
                sb.AppendLine($"  📝 کامنت: <code>{Html(item.UserComment)}</code>");
        }

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

    /// <summary>
    /// In-memory time-expiry reminder item enriched from one identity-checked current client detail response.
    /// </summary>
    private sealed class ExpiryReminderItem
    {
        /// <summary>Customer-visible XUI account email.</summary>
        public string Email { get; set; }
        /// <summary>Positive numeric XUI client id used by the existing renewal callback.</summary>
        public int ClientId { get; set; }
        /// <summary>Whole calendar days remaining in Iran time.</summary>
        public int DaysLeft { get; set; }
        /// <summary>Resolved expiry date in Iran time retained for current reminder bookkeeping.</summary>
        public DateTime ExpiryIranDate { get; set; }
        /// <summary>Owned or tenant runtime bot id that must deliver this account reminder.</summary>
        public string BotId { get; set; }
        /// <summary>Verified customer-authored metadata comment, or an empty string when none was registered.</summary>
        public string UserComment { get; set; } = string.Empty;
    }
}
