using System.Net;
using System.Text;
using Adminbot.Domain;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Periodically scans the complete XUI v3 client list and delivers durable 80/90/99 percent traffic reminders.
/// </summary>
/// <remarks>
/// Each enabled iteration performs exactly one <c>/panel/api/clients/list</c> request. Client cycle and delivery state
/// lives in <c>users.db</c>, while recipient profile/block information remains in <c>credentials.db</c>. Messages are
/// sent by the owned or tenant bot recorded in the client metadata; no wallet, order, payment, or XUI mutation occurs.
/// </remarks>
public sealed class XuiV3VolumeExpirationReminderService : BackgroundService
{
    /// <summary>Minimum delay between separate account messages sent to the same bot and private chat.</summary>
    private static readonly TimeSpan MinimumSameChatSpacing = TimeSpan.FromMilliseconds(1100);
    /// <summary>Minimum process-local spacing between any two volume-reminder Telegram sends.</summary>
    private static readonly TimeSpan MinimumGlobalSpacing = TimeSpan.FromMilliseconds(75);
    private readonly IConfiguration _configuration;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotRegistry _botRegistry;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly XuiV3VolumeReminderStateStore _stateStore;
    private readonly ILogger<XuiV3VolumeExpirationReminderService> _logger;
    /// <summary>Latest successful send time keyed by originating bot plus private chat.</summary>
    private readonly Dictionary<string, DateTime> _lastSendByChat = new(StringComparer.Ordinal);
    /// <summary>Latest successful volume-reminder send time across this worker process.</summary>
    private DateTime _lastGlobalSendUtc = DateTime.MinValue;

    /// <summary>
    /// Creates the XUI v3 volume-expiration reminder worker.
    /// </summary>
    /// <param name="configuration">
    /// Reloadable application configuration containing panel credentials, enablement, interval, and admin ids.
    /// Secrets are used only by the existing XUI transport and must never be written to reminder logs or state.
    /// </param>
    /// <param name="botClientProvider">Provider for the exact owned or tenant Telegram bot recorded on each client.</param>
    /// <param name="botRegistry">Runtime bot registry used to reject missing or disabled originating bots.</param>
    /// <param name="botContextAccessor">Async-local bot context used while sending a bot-scoped reminder.</param>
    /// <param name="credentialsDbContext">
    /// Serialized shared credentials context used only to verify that the owner exists and is not globally blocked.
    /// </param>
    /// <param name="purchaseService">Plan catalog used to restrict reminders to currently active service inbounds.</param>
    /// <param name="stateStore">Durable users.db cycle, start-evidence, and delivery-claim store.</param>
    /// <param name="logger">Structured operational logger that never receives panel tokens or subscription secrets.</param>
    public XuiV3VolumeExpirationReminderService(
        IConfiguration configuration,
        BotClientProvider botClientProvider,
        BotRegistry botRegistry,
        BotContextAccessor botContextAccessor,
        CredentialsDbContext credentialsDbContext,
        XuiV3PurchaseService purchaseService,
        XuiV3VolumeReminderStateStore stateStore,
        ILogger<XuiV3VolumeExpirationReminderService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _botClientProvider = botClientProvider ?? throw new ArgumentNullException(nameof(botClientProvider));
        _botRegistry = botRegistry ?? throw new ArgumentNullException(nameof(botRegistry));
        _botContextAccessor = botContextAccessor ?? throw new ArgumentNullException(nameof(botContextAccessor));
        _credentialsDbContext = credentialsDbContext ?? throw new ArgumentNullException(nameof(credentialsDbContext));
        _purchaseService = purchaseService ?? throw new ArgumentNullException(nameof(purchaseService));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one immediate enabled scan, then waits the configured interval after each completed iteration.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token for panel, users.db, credentials.db, Telegram, and delays.</param>
    /// <returns>A task representing the worker lifetime.</returns>
    /// <remarks>
    /// Configuration is rebound before every iteration so enablement and interval changes are picked up without a
    /// restart after the current delay. Disabled workers check once per minute and perform no panel request.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _configuration.Get<AppConfig>() ?? new AppConfig();
            var delay = TimeSpan.FromMinutes(1);

            if (config.VolumeExpirationReminderEnabled)
            {
                try
                {
                    delay = TimeSpan.FromMinutes(GetValidatedIntervalMinutes(config));
                    await RunScanAsync(config, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "XUI v3 volume reminder scan failed.");
                    delay = TimeSpan.FromMinutes(5);
                }
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Fetches one complete panel list, reconciles durable cycles, and sends newly reached thresholds sequentially.
    /// </summary>
    /// <param name="config">Current configuration snapshot with the module enabled and a validated interval.</param>
    /// <param name="cancellationToken">Host shutdown token for all external and database operations.</param>
    /// <returns>A task that completes after every current candidate was skipped, sent, or durably classified.</returns>
    /// <remarks>
    /// A failed or partial list response produces no reminder-state mutation. This prevents transient panel failures
    /// from looking like deletion, counter reset, or renewal. Lower thresholds are not emitted when the same scan
    /// first observes a higher threshold. Recoverably malformed individual client rows are skipped and summarized in
    /// one diagnostic entry so one historical panel row cannot abort reconciliation for every valid account.
    /// </remarks>
    private async Task RunScanAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var serverInfo = BuildConfiguredPanelServerInfo(config);
        var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!response.Success)
        {
            _logger.LogWarning(
                "XUI v3 volume reminder could not fetch the complete client list. message={Message}",
                response.Msg ?? "unknown");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var enabledServices = _purchaseService.GetEnabledServices();
        var observations = new List<XuiV3VolumeReminderObservation>();
        var malformedClientCount = 0;
        var firstMalformedClientId = 0;
        Exception firstMalformedClientException = null;
        foreach (var client in response.Obj ?? new List<XuiV3Client>())
        {
            if (client == null || client.Id <= 0)
                continue;

            try
            {
                if (!XuiV3ClientPlanEligibility.IsClientInActiveServiceInbounds(client, enabledServices))
                    continue;

                var snapshot = XuiV3ClientUsageResolver.Resolve(client);
                if (snapshot.TotalBytes <= 0 || snapshot.OwnerTelegramUserId <= 0 || IsSuperAdmin(config, snapshot.OwnerTelegramUserId))
                    continue;

                var threshold = XuiV3ClientUsageResolver.GetHighestReachedThreshold(snapshot);
                observations.Add(new XuiV3VolumeReminderObservation
                {
                    ClientId = snapshot.ClientId,
                    ClientCreatedAt = snapshot.ClientCreatedAt,
                    Email = snapshot.Email,
                    BotId = snapshot.CreatedByBotId,
                    TelegramUserId = snapshot.OwnerTelegramUserId,
                    PanelUpdatedAt = snapshot.PanelUpdatedAt,
                    TotalBytes = snapshot.TotalBytes,
                    UsedBytes = snapshot.UsedBytes,
                    LastRenewedAtUtc = snapshot.LastRenewedAtUtc,
                    HighestReachedThreshold = threshold,
                    CanNotify = XuiV3ClientUsageResolver.CanNotifyVolumeThreshold(snapshot, nowUtc, threshold)
                });
            }
            catch (Exception ex) when (IsRecoverableClientRecordException(ex))
            {
                malformedClientCount++;
                // Retain only the first row-local failure so a panel containing many malformed historical clients
                // produces one actionable diagnostic instead of flooding the private Telegram logger channel.
                if (firstMalformedClientException == null)
                {
                    firstMalformedClientId = client.Id;
                    firstMalformedClientException = ex;
                }
            }
        }

        if (malformedClientCount > 0)
        {
            _logger.LogWarning(
                firstMalformedClientException,
                "XUI v3 volume reminder skipped malformed client rows. count={Count}, firstClientId={FirstClientId}",
                malformedClientCount,
                firstMalformedClientId);
        }

        var panelKey = XuiV3ClientUsageResolver.BuildPanelKey(serverInfo);
        var candidates = await _stateStore.ReconcileAsync(panelKey, observations, nowUtc, cancellationToken);
        var startedKeys = await _stateStore.GetStartedBotUserKeysAsync(candidates, cancellationToken);
        var sent = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var candidate in candidates
                     .OrderByDescending(x => x.Threshold)
                     .ThenBy(x => x.BotId, StringComparer.Ordinal)
                     .ThenBy(x => x.TelegramUserId)
                     .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase))
        {
            if (!startedKeys.Contains(XuiV3VolumeReminderStateStore.BuildBotUserKey(
                    candidate.BotId,
                    candidate.TelegramUserId)))
            {
                skipped++;
                continue;
            }

            var credUser = await _credentialsDbContext.GetUserStatusWithId(candidate.TelegramUserId);
            if (credUser == null || credUser.IsBlocked)
            {
                skipped++;
                continue;
            }

            var bot = _botRegistry.GetById(candidate.BotId);
            if (bot?.Enabled != true ||
                !string.Equals(bot.Id, candidate.BotId, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var chatId = credUser.ChatID > 0 ? credUser.ChatID : candidate.TelegramUserId;
            if (chatId <= 0 || !await _stateStore.TryClaimAsync(candidate, DateTime.UtcNow, cancellationToken))
            {
                skipped++;
                continue;
            }

            var botClient = _botClientProvider.GetClient(bot.Id);
            try
            {
                Message delivered;
                using (_botContextAccessor.Push(new BotRuntimeContext { Config = bot, Client = botClient }))
                {
                    delivered = await SendWithRateLimitAsync(botClient, chatId, candidate, cancellationToken);
                }

                try
                {
                    await _stateStore.MarkSentAsync(candidate, delivered.MessageId, DateTime.UtcNow, cancellationToken);
                }
                catch (Exception persistenceException)
                {
                    // Telegram has accepted the message. Retrying would violate the selected no-duplicate policy, so
                    // keep or recreate a terminal ambiguous state with a best-effort non-cancelled write.
                    try
                    {
                        await _stateStore.MarkAmbiguousAsync(
                            candidate,
                            delivered.MessageId,
                            persistenceException.Message,
                            DateTime.UtcNow,
                            CancellationToken.None);
                    }
                    catch (Exception ambiguousException)
                    {
                        _logger.LogError(
                            ambiguousException,
                            "XUI volume reminder was delivered but its terminal state could not be persisted. clientId={ClientId}, cycle={Cycle}, threshold={Threshold}",
                            candidate.ClientId,
                            candidate.CycleNumber,
                            candidate.Threshold);
                    }
                }

                sent++;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 403)
            {
                await _stateStore.MarkTelegramBlockedAsync(
                    candidate,
                    "Telegram returned 403 for the originating bot.",
                    DateTime.UtcNow,
                    CancellationToken.None);
                skipped++;
            }
            catch (ApiRequestException ex)
            {
                await _stateStore.MarkFailedAsync(
                    candidate,
                    $"Telegram API error {ex.ErrorCode}: {ex.Message}",
                    DateTime.UtcNow,
                    CancellationToken.None);
                failed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Leave the pre-delivery claim intact. The next worker marks it ambiguous after lease expiry so a
                // shutdown at an unknowable network boundary cannot duplicate an accepted Telegram message.
                throw;
            }
            catch (Exception ex)
            {
                await _stateStore.MarkAmbiguousAsync(
                    candidate,
                    telegramMessageId: null,
                    ex.Message,
                    DateTime.UtcNow,
                    CancellationToken.None);
                _logger.LogWarning(
                    ex,
                    "XUI volume reminder delivery became ambiguous and was suppressed. clientId={ClientId}, cycle={Cycle}, threshold={Threshold}",
                    candidate.ClientId,
                    candidate.CycleNumber,
                    candidate.Threshold);
                failed++;
            }
        }

        _logger.LogDebug(
            "XUI v3 volume reminder scan finished. clients={ClientCount}, finiteObservations={ObservationCount}, candidates={CandidateCount}, sent={Sent}, skipped={Skipped}, failed={Failed}",
            response.Obj?.Count ?? 0,
            observations.Count,
            candidates.Count,
            sent,
            skipped,
            failed);
    }

    /// <summary>
    /// Identifies row-local panel-shape failures that may be skipped without invalidating the complete list response.
    /// </summary>
    /// <param name="exception">
    /// Exception raised while normalizing or classifying one XUI client row. Cancellation and resource-exhaustion
    /// exceptions must not be supplied because they apply to the worker rather than one client.
    /// </param>
    /// <returns>
    /// <c>true</c> for nullable, conversion, collection, or invalid-shape failures that are isolated to one client;
    /// otherwise <c>false</c> so the outer worker guard can fail and retry the complete scan.
    /// </returns>
    /// <remarks>
    /// The first accepted exception is logged once with the number of skipped rows and a numeric client id. Email,
    /// panel credentials, subscription values, and client metadata are intentionally excluded from the diagnostic.
    /// </remarks>
    private static bool IsRecoverableClientRecordException(Exception exception)
    {
        return exception is NullReferenceException or
               ArgumentException or
               FormatException or
               OverflowException or
               InvalidCastException or
               InvalidOperationException or
               KeyNotFoundException;
    }

    /// <summary>
    /// Sends one separate account reminder while respecting Telegram per-chat/global pacing and 429 retry guidance.
    /// </summary>
    /// <param name="botClient">Originating owned or tenant Telegram client.</param>
    /// <param name="chatId">Private Telegram chat id of the verified account owner.</param>
    /// <param name="candidate">Claimed client/cycle/threshold whose message is being sent.</param>
    /// <param name="cancellationToken">Host shutdown token for pacing, retry delay, and Telegram transport.</param>
    /// <returns>The concrete Telegram message accepted by the bot API.</returns>
    /// <remarks>
    /// At most three attempts are made for explicit HTTP 429 responses, using Telegram's <c>RetryAfter</c>. Other
    /// failures are classified by the caller so the durable claim can be retried or suppressed appropriately.
    /// </remarks>
    private async Task<Message> SendWithRateLimitAsync(
        ITelegramBotClient botClient,
        long chatId,
        XuiV3VolumeReminderCandidate candidate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitForSendSlotAsync(candidate.BotId, chatId, cancellationToken);
            try
            {
                var message = await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: BuildMessage(candidate),
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildKeyboard(candidate),
                    cancellationToken: cancellationToken);
                var sentAtUtc = DateTime.UtcNow;
                _lastGlobalSendUtc = sentAtUtc;
                _lastSendByChat[BuildChatRateKey(candidate.BotId, chatId)] = sentAtUtc;
                return message;
            }
            catch (ApiRequestException ex) when (
                ex.ErrorCode == 429 &&
                ex.Parameters?.RetryAfter != null &&
                attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(ex.Parameters.RetryAfter.Value + 1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Waits until both per-chat and global Telegram pacing windows have elapsed.
    /// </summary>
    /// <param name="botId">Originating bot id; per-chat limits are isolated between bots.</param>
    /// <param name="chatId">Private Telegram chat id.</param>
    /// <param name="cancellationToken">Host shutdown token for the optional delay.</param>
    /// <returns>A task that completes when the next sequential send is allowed.</returns>
    private async Task WaitForSendSlotAsync(string botId, long chatId, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var delay = _lastGlobalSendUtc + MinimumGlobalSpacing - nowUtc;
        if (_lastSendByChat.TryGetValue(BuildChatRateKey(botId, chatId), out var lastChatSend))
        {
            var chatDelay = lastChatSend + MinimumSameChatSpacing - nowUtc;
            if (chatDelay > delay)
                delay = chatDelay;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Builds the Persian HTML reminder for one account and threshold.
    /// </summary>
    /// <param name="candidate">Claimed reminder candidate containing only owner-safe account and usage facts.</param>
    /// <returns>Customer-facing HTML text for 80, 90, or final 99 percent consumption.</returns>
    private static string BuildMessage(XuiV3VolumeReminderCandidate candidate)
    {
        var builder = new StringBuilder();
        if (candidate.Threshold >= XuiV3ClientUsageResolver.FinalThreshold99)
        {
            builder.AppendLine("⛔️ پایان حجم اکانت");
            builder.AppendLine();
            builder.AppendLine($"اکانت: <code>{Html(candidate.Email)}</code>");
            builder.AppendLine($"مصرف: <b>{Html(FormatTraffic(candidate.UsedBytes))}</b> از <b>{Html(FormatTraffic(candidate.TotalBytes))}</b>");
            builder.AppendLine();
            builder.AppendLine("شما کل حجم خود را مصرف کرده‌اید و اکانت شما تمام شد.");
            builder.AppendLine("برای ادامه استفاده از اکانت، با دکمه زیر آن را تمدید کنید.");
            return builder.ToString();
        }

        builder.AppendLine(candidate.Threshold >= XuiV3ClientUsageResolver.WarningThreshold90
            ? "🚨 هشدار مصرف حجم"
            : "⚠️ یادآوری مصرف حجم");
        builder.AppendLine();
        builder.AppendLine($"اکانت: <code>{Html(candidate.Email)}</code>");
        builder.AppendLine($"شما به <b>{candidate.Threshold}٪</b> مصرف حجم بسته خود رسیده‌اید.");
        builder.AppendLine($"مصرف فعلی: <b>{Html(FormatTraffic(candidate.UsedBytes))}</b> از <b>{Html(FormatTraffic(candidate.TotalBytes))}</b>");
        builder.AppendLine();
        builder.AppendLine("برای تمدید سریع اکانت می‌توانید از دکمه زیر استفاده کنید.");
        return builder.ToString();
    }

    /// <summary>
    /// Builds the existing account-renew callback button for one volume reminder.
    /// </summary>
    /// <param name="candidate">Claimed candidate with a positive numeric XUI client id.</param>
    /// <returns>One-row inline keyboard that enters the current owned or tenant renewal flow.</returns>
    private static InlineKeyboardMarkup BuildKeyboard(XuiV3VolumeReminderCandidate candidate)
        => new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"تمدید {candidate.Email}",
                    XuiV3PurchaseCallbacks.AccountRenew(candidate.ClientId, 0))
            }
        });

    /// <summary>
    /// Formats raw bytes with the existing XUI customer-facing GB/MB formatter.
    /// </summary>
    /// <param name="bytes">Non-negative quota or consumption in bytes.</param>
    /// <returns>Human-readable binary GB or MB text.</returns>
    private static string FormatTraffic(long bytes)
        => XuiV3PurchaseService.FormatTrafficSize(Math.Max(0, bytes));

    /// <summary>
    /// HTML-encodes one account or formatted traffic value before Telegram rendering.
    /// </summary>
    /// <param name="value">User-visible value that may contain reserved HTML characters.</param>
    /// <returns>HTML-safe text.</returns>
    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Builds the configured XUI v3 panel descriptor without exposing credentials to logs or users.db.
    /// </summary>
    /// <param name="config">Current configuration snapshot containing the v3 endpoint and bearer token.</param>
    /// <returns>Server descriptor accepted by the existing XUI v3 transport.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the v3 base URL is missing.</exception>
    private static ServerInfo BuildConfiguredPanelServerInfo(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.XuiV3ApiBaseUrl))
            throw new InvalidOperationException("XuiV3ApiBaseUrl is not configured.");

        return new ServerInfo
        {
            ApiVersion = "v3",
            ApiToken = config.XuiV3ApiToken,
            Url = config.XuiV3ApiBaseUrl.TrimEnd('/'),
            RootPath = (config.XuiV3ApiRootPath ?? string.Empty).Trim('/'),
            SubLinkUrl = string.IsNullOrWhiteSpace(config.XuiV3SubLinkBaseUrl)
                ? null
                : config.XuiV3SubLinkBaseUrl.TrimEnd('/'),
            Name = "Configured V3 Panel"
        };
    }

    /// <summary>
    /// Returns the enabled scan interval after enforcing the documented runtime safety range.
    /// </summary>
    /// <param name="config">Current application configuration snapshot.</param>
    /// <returns>Whole minutes from 5 through 1440.</returns>
    /// <exception cref="InvalidOperationException">Thrown for an out-of-range reloaded configuration value.</exception>
    private static int GetValidatedIntervalMinutes(AppConfig config)
    {
        var value = config.VolumeExpirationReminderIntervalMinutes;
        if (value is < 5 or > 1440)
        {
            throw new InvalidOperationException(
                $"VolumeExpirationReminderIntervalMinutes must be between 5 and 1440; actual value is {value}.");
        }

        return value;
    }

    /// <summary>
    /// Excludes configured super-admin ids from customer traffic reminders.
    /// </summary>
    /// <param name="config">Current configuration snapshot containing global admin Telegram ids.</param>
    /// <param name="telegramUserId">Numeric account owner id.</param>
    /// <returns><c>true</c> when the owner is a configured super-admin.</returns>
    private static bool IsSuperAdmin(AppConfig config, long telegramUserId)
        => config.AdminsUserIds?.Contains(telegramUserId) == true;

    /// <summary>
    /// Builds a bot-scoped key for in-memory Telegram rate-limit timestamps.
    /// </summary>
    /// <param name="botId">Originating bot id.</param>
    /// <param name="chatId">Private Telegram chat id.</param>
    /// <returns>Ordinal in-memory key; it is never persisted or exposed.</returns>
    private static string BuildChatRateKey(string botId, long chatId)
        => $"{botId}:{chatId}";
}
