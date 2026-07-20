using System.Text.RegularExpressions;
using Adminbot.Domain;
using Adminbot.Utils;

using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

using System.Globalization;
using System;
using System.Text;

using Adminbot.Domain.Logging;
using Newtonsoft.Json.Linq;

/// <summary>
/// Shared Telegram update dispatcher for owned brand bots and tenant storefront bots.
/// </summary>
/// <remarks>
/// The class still contains the legacy single-bot business flows, but multi-instance runtime now enters it through
/// <see cref="DispatchUpdateAsync"/> with a <see cref="BotRuntimeContext"/>. The current bot context selects the
/// correct token, brand config, mandatory-join channels, support account, payment return URLs, and bot-scoped user state.
/// </remarks>
public class TelegramBotService : IHostedService
{
    private const string BroadcastAudienceAll = "all";
    private const string BroadcastAudienceCustomers = "customers";
    private const string BroadcastAudienceColleagues = "colleagues";
    private const int MaxAccountInfoMessagesPerRequest = 5;

    /// <summary>
    /// Reply-keyboard action that starts the super-admin manual phone verification flow for an owned-bot user.
    /// </summary>
    private const string AdminVerifyPhoneAction = "📱 تایید دستی شماره تلفن";

    /// <summary>
    /// Conversation-state step that waits for the target user's numeric Telegram id.
    /// </summary>
    private const string AdminPhoneUserIdStep = "admin-phone-user-id";

    /// <summary>
    /// Conversation-state step prefix that waits for an international or local phone number.
    /// </summary>
    private const string AdminPhoneNumberStep = "admin-phone-number";

    /// <summary>
    /// Conversation-state step prefix that waits for the super-admin's final confirmation.
    /// </summary>
    private const string AdminPhoneConfirmationStep = "admin-phone-confirm";

    /// <summary>
    /// Positive confirmation label used only by the manual phone verification flow.
    /// </summary>
    private const string ConfirmAdminPhoneButton = "✅ تایید شماره تلفن";

    /// <summary>
    /// Cancellation label used only by the manual phone verification flow.
    /// </summary>
    private const string CancelAdminPhoneButton = "❌ انصراف";

    private readonly ITelegramBotClient _botClient;
    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly ILogger<TelegramBotService> _logger;
    private BroadcastManager _broadcastManager;
    private readonly NowPayments _nowPayments;
    private readonly NowPaymentsSettlementService _nowPaymentsSettlementService;
    private readonly HooshPay _hooshPay;
    private readonly HooshPaySettlementService _hooshPaySettlementService;
    private readonly XuiV3PurchaseService _xuiV3PurchaseService;
    private readonly XuiV3BotFlowService _xuiV3BotFlowService;
    private readonly XuiV3PurchaseSessionStore _xuiV3PurchaseSessionStore;
    private readonly XuiV3AdminFlowService _xuiV3AdminFlowService;
    private readonly TenantBotService _tenantBotService;
    private readonly SalesAssistantService _salesAssistantService;
    private readonly UserActivityLogService _userActivityLog;
    private readonly WalletLedgerService _walletLedgerService;
    /// <summary>Global owned-bot referral engine used by start links, dashboards, and legacy Zibal settlement.</summary>
    private readonly ReferralService _referralService;
    private readonly OwnedBotNotificationService _ownedBotNotificationService;
    private readonly GozargahSiteApiClient _gozargahSiteApiClient;
    private readonly GozargahSiteSyncService _gozargahSiteSyncService;
    private readonly BotRegistry _botRegistry;
    private readonly BotRuntimeStatusStore _botRuntimeStatusStore;
    private readonly BotContextAccessor _botContextAccessor;
    private ITelegramBotClient ActiveBotClient => _botContextAccessor.Current?.Client ?? _botClient;
    private BotInstanceConfig CurrentBot => _botContextAccessor.Current?.Config;
    private IEnumerable<string> CurrentChannelIds => CurrentBot != null
        ? CurrentBot.ChannelIds ?? Enumerable.Empty<string>()
        : _appConfig.ChannelIds ?? Enumerable.Empty<string>();
    /// <summary>
    /// Gets the support contact configured for the currently handling owned bot.
    /// </summary>
    /// <remarks>
    /// A resolved runtime bot owns its own support setting. An empty value must remain empty instead of silently
    /// falling back to the default brand, because showing another brand's support account is customer-facingly wrong.
    /// The legacy application setting is used only when no multi-bot runtime context exists.
    /// </remarks>
    private string CurrentSupportAccount => CurrentBot != null
        ? CurrentBot.SupportAccount
        : _appConfig.SupportAccount;
    private string[] CurrentIosTutorial => CurrentBot?.IosTutorial ?? _appConfig.IosTutorial;
    private string[] CurrentAndroidTutorial => CurrentBot?.AndroidTutorial ?? _appConfig.AndroidTutorial;
    private string[] CurrentWindowsTutorial => CurrentBot?.WindowsTutorial ?? _appConfig.WindowsTutorial;
    private string CurrentNowPaymentsSuccessUrl => CurrentBot?.BuildTelegramStartUrl("payment_success") ?? _appConfig.NowpaymentSuccessUrl;
    private string CurrentNowPaymentsCancelUrl => CurrentBot?.BuildTelegramStartUrl("payment_cancel") ?? _appConfig.NowpaymentCancelUrl;
    private string CurrentHooshPayReturnUrl => CurrentBot?.BuildTelegramStartUrl("payment_success") ?? _appConfig.HooshPayReturnUrl;

    /// <summary>
    /// Refreshes the shared credentials role from the Gozargah website before owned-bot pricing is shown.
    /// </summary>
    /// <param name="credUser">
    /// Shared credentials profile of the Telegram user currently interacting with an owned bot. The instance is updated
    /// in memory when the website lookup promotes the database row to colleague.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the website lookup and credentials database update.</param>
    /// <returns>A task that completes after the optional role refresh has finished.</returns>
    /// <remarks>
    /// Tenant storefronts are excluded because their customer prices are controlled by the tenant owner's storefront
    /// settings. Owned bots sell directly for the platform, so an existing non-banned Gozargah website user receives
    /// colleague pricing immediately.
    /// </remarks>
    private async Task RefreshOwnedBotColleagueRoleFromGozargahAsync(CredUser credUser, CancellationToken cancellationToken)
    {
        var botType = CurrentBot?.Type ?? BotInstanceTypes.Owned;
        if (!string.Equals(botType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
            return;

        await _gozargahSiteSyncService.PromoteToColleagueIfConnectedSiteUserAsync(credUser, cancellationToken);
    }

    /// <summary>
    /// Creates the shared Telegram service and wires all existing bot flows plus the tenant storefront flow.
    /// </summary>
    /// <param name="botClient">Default bot client kept for legacy single-bot hosted-service compatibility.</param>
    /// <param name="dbContext">Runtime database that stores bot-scoped conversation state and payment metadata.</param>
    /// <param name="credentialsDb">Shared credentials database that stores profiles, wallet balances, and roles.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="logger">Service logger.</param>
    /// <param name="broadcastManager">Broadcast manager used by super-admin broadcast flows.</param>
    /// <param name="nowPayments">NOWPayments API client.</param>
    /// <param name="nowPaymentsSettlementService">NOWPayments wallet settlement service.</param>
    /// <param name="hooshPay">HooshPay API client.</param>
    /// <param name="hooshPaySettlementService">HooshPay wallet settlement service.</param>
    /// <param name="xuiV3PurchaseService">Shared XuiV3 purchase/account creation service.</param>
    /// <param name="xuiV3BotFlowService">Regular user XuiV3 purchase and account-management flow.</param>
    /// <param name="xuiV3PurchaseSessionStore">
    /// Bot-scoped in-memory purchase selection store. It is cleared when a main-menu command cancels an unfinished
    /// customer flow, preventing stale plan selections from resuming after the referral dashboard is shown.
    /// </param>
    /// <param name="xuiV3AdminFlowService">Super-admin XuiV3 management flow.</param>
    /// <param name="tenantBotService">Tenant storefront owner and customer flow service.</param>
    /// <param name="userActivityLog">File-based user activity logger.</param>
    /// <param name="gozargahSiteApiClient">Gozargah website API client used to show colleague site wallet status.</param>
    /// <param name="gozargahSiteSyncService">
    /// Gozargah website sync service used to auto-promote connected website users before owned-bot pricing is shown.
    /// </param>
    /// <param name="botRegistry">Runtime registry used to list configured owned, assistant, and tenant bots.</param>
    /// <param name="botRuntimeStatusStore">
    /// Process-local receiver status store used by the super-admin bot status screen.
    /// </param>
    /// <param name="botContextAccessor">Async-local current bot context accessor.</param>
    /// <param name="referralService">
    /// Global owned-bot referral service used by start payloads, user reporting, and final legacy Zibal settlement.
    /// </param>
    public TelegramBotService(
        ITelegramBotClient botClient,
        UserDbContext dbContext,
        CredentialsDbContext credentialsDb,
        IConfiguration configuration,
        ILogger<TelegramBotService> logger,
        BroadcastManager broadcastManager,
        NowPayments nowPayments,
        NowPaymentsSettlementService nowPaymentsSettlementService,
        HooshPay hooshPay,
        HooshPaySettlementService hooshPaySettlementService,
        XuiV3PurchaseService xuiV3PurchaseService,
        XuiV3BotFlowService xuiV3BotFlowService,
        XuiV3PurchaseSessionStore xuiV3PurchaseSessionStore,
        XuiV3AdminFlowService xuiV3AdminFlowService,
        TenantBotService tenantBotService,
        SalesAssistantService salesAssistantService,
        UserActivityLogService userActivityLog,
        WalletLedgerService walletLedgerService,
        OwnedBotNotificationService ownedBotNotificationService,
        GozargahSiteApiClient gozargahSiteApiClient,
        GozargahSiteSyncService gozargahSiteSyncService,
        BotRegistry botRegistry,
        BotRuntimeStatusStore botRuntimeStatusStore,
        BotContextAccessor botContextAccessor,
        ReferralService referralService)
    {
        _botClient = botClient;
        _userDbContext = dbContext;
        _credentialsDbContext = credentialsDb;
        _configuration = configuration;
        _appConfig = _configuration.Get<AppConfig>();
        _logger = logger;
        _broadcastManager = broadcastManager;
        _nowPayments = nowPayments;
        _nowPaymentsSettlementService = nowPaymentsSettlementService;
        _hooshPay = hooshPay;
        _hooshPaySettlementService = hooshPaySettlementService;
        _xuiV3PurchaseService = xuiV3PurchaseService;
        _xuiV3BotFlowService = xuiV3BotFlowService;
        _xuiV3PurchaseSessionStore = xuiV3PurchaseSessionStore;
        _xuiV3AdminFlowService = xuiV3AdminFlowService;
        _tenantBotService = tenantBotService;
        _salesAssistantService = salesAssistantService;
        _userActivityLog = userActivityLog;
        _walletLedgerService = walletLedgerService;
        _ownedBotNotificationService = ownedBotNotificationService;
        _gozargahSiteApiClient = gozargahSiteApiClient;
        _gozargahSiteSyncService = gozargahSiteSyncService;
        _botRegistry = botRegistry;
        _botRuntimeStatusStore = botRuntimeStatusStore;
        _botContextAccessor = botContextAccessor;
        _referralService = referralService;
    }

    /// <summary>
    /// Processes one update for a specific bot runtime context.
    /// </summary>
    /// <param name="botClient">Telegram client that received the update.</param>
    /// <param name="update">Telegram update to dispatch.</param>
    /// <param name="botContext">Current bot configuration and client for this update.</param>
    /// <param name="cancellationToken">Cancellation token from Telegram polling.</param>
    /// <returns>A task that completes when the update is fully handled.</returns>
    public async Task DispatchUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        BotRuntimeContext botContext,
        CancellationToken cancellationToken)
    {
        using (_botContextAccessor.Push(botContext))
        {
            await HandleUpdateAsync(botClient, update, cancellationToken);
        }
    }

    /// <summary>
    /// Starts legacy single-bot polling when this service is used directly as an <see cref="IHostedService"/>.
    /// </summary>
    /// <remarks>
    /// In the multi-instance setup, <see cref="MultiBotHostedService"/> is the normal entry point and calls
    /// <see cref="DispatchUpdateAsync"/> for each enabled bot instead.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token supplied by the host.</param>
    /// <returns>A task that completes after polling is started.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {

        var me = await ActiveBotClient.GetMeAsync();
        Console.WriteLine($"Start listening for @{me.Username}");


        // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
        ReceiverOptions receiverOptions = new()
        {
            AllowedUpdates = Array.Empty<UpdateType>() // receive all update types except ChatMember related updates
        };

        // PeriodicTaskRunner._credentialsDbContext = _credentialsDbContext;
        // PeriodicTaskRunner.Start();

        ActiveBotClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cancellationToken
        );

        // Start your bot logic here
        //ActiveBotClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, cancellationToken);
    }

    /// <summary>
    /// Stops legacy hosted-service mode.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token supplied by the host.</param>
    /// <returns>A completed task; receiver lifetime is controlled by the host cancellation token.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Add your cleanup code here
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wraps core update handling with activity-log error capture and non-fatal delivery handling.
    /// </summary>
    /// <param name="botClient">Telegram client that should answer the update.</param>
    /// <param name="update">Telegram update being processed.</param>
    /// <param name="cancellationToken">Cancellation token from polling.</param>
    /// <remarks>
    /// Telegram can throw per-user delivery errors when a customer blocks an owned bot, tenant bot, or assistant bot.
    /// It can also raise transient request timeouts while sending a reply. Those errors are logged as skipped
    /// deliveries and are not rethrown, so the polling loop does not treat one unreachable chat or one slow Telegram
    /// request as a receiver failure. All other exceptions are still logged and rethrown for the polling error pipeline.
    /// </remarks>
    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            await HandleUpdateCoreAsync(botClient, update, cancellationToken);
        }
        catch (Exception ex)
        {
            var credUser = GetCreduserFromUpdate(update);
            if (IsUserDeliveryPollingError(ex))
            {
                await _userActivityLog.LogWarningAsync(
                    "handle_update_delivery_skipped",
                    credUser,
                    IsSuperAdminUser(credUser?.TelegramUserId ?? 0),
                    new Dictionary<string, object>
                    {
                        ["updateType"] = update?.Type.ToString() ?? "unknown",
                        ["telegramError"] = ex.Message ?? string.Empty,
                        ["botId"] = BotContextAccessor.CurrentBotId ?? string.Empty
                    },
                    cancellationToken);

                _logger.LogWarning(
                    "Telegram update delivery skipped because the chat is unreachable. botId={BotId}, userId={UserId}, chatId={ChatId}, telegramError={TelegramError}",
                    BotContextAccessor.CurrentBotId,
                    credUser?.TelegramUserId,
                    update?.Message?.Chat.Id ?? update?.CallbackQuery?.Message?.Chat.Id,
                    ex.Message);
                return;
            }

            if (IsExternalOperationTimeout(ex, cancellationToken))
            {
                await _userActivityLog.LogWarningAsync(
                    "handle_update_external_timeout",
                    credUser,
                    IsSuperAdminUser(credUser?.TelegramUserId ?? 0),
                    new Dictionary<string, object>
                    {
                        ["updateType"] = update?.Type.ToString() ?? "unknown",
                        ["timeoutError"] = ex.Message ?? string.Empty,
                        ["botId"] = BotContextAccessor.CurrentBotId ?? string.Empty
                    },
                    cancellationToken);

                _logger.LogWarning(
                    ex,
                    "Telegram update external operation timed out. botId={BotId}, userId={UserId}, chatId={ChatId}",
                    BotContextAccessor.CurrentBotId,
                    credUser?.TelegramUserId,
                    update?.Message?.Chat.Id ?? update?.CallbackQuery?.Message?.Chat.Id);

                await SendBestEffortTimeoutMessageAsync(botClient, update, cancellationToken);
                return;
            }

            await _userActivityLog.LogErrorAsync(
                "handle_update_failed",
                ex,
                credUser,
                IsSuperAdminUser(credUser?.TelegramUserId ?? 0),
                new Dictionary<string, object>
                {
                    ["updateType"] = update?.Type.ToString() ?? "unknown"
                },
                cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Routes callbacks and messages to tenant storefront flows, owner tenant configuration,
    /// user XuiV3 flows, super-admin flows, payment return handlers, and legacy menus.
    /// </summary>
    /// <param name="botClient">Telegram client for the current bot.</param>
    /// <param name="update">Telegram update being routed.</param>
    /// <param name="cancellationToken">Cancellation token from polling.</param>
    private async Task HandleUpdateCoreAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (_salesAssistantService.IsAssistantBot)
        {
            await _salesAssistantService.TryHandleUpdateAsync(botClient, update, cancellationToken);
            return;
        }

        if (update.CallbackQuery is { } callbackQuery)
        {
            var callbackCredUser = await _credentialsDbContext.GetUserStatus(
                GetCreduserFromTelegramUser(callbackQuery.From, callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id));
            var callbackIsSuperAdmin = IsSuperAdminUser(callbackQuery.From.Id);

            await _userActivityLog.LogCallbackAsync(callbackQuery, callbackCredUser, callbackIsSuperAdmin, cancellationToken);

            if (!callbackIsSuperAdmin && callbackCredUser?.IsBlocked == true)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: "به علت تخلف مسدود شدید. لطفاً با پشتیبانی تلگرام پیام بدهید.",
                    showAlert: true,
                    cancellationToken: cancellationToken);
                return;
            }

            var callbackUserState = await _userDbContext.GetUserStatus(callbackQuery.From.Id);
            // Tenant storefront callbacks are isolated from the main bot purchase/account flows.
            if (string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
            {
                await _tenantBotService.TryHandleTenantUpdateAsync(botClient, update, callbackCredUser, callbackUserState, cancellationToken);
                return;
            }

            // Owner callbacks configure a colleague storefront from inside the main brand bot.
            if (_tenantBotService.IsOwnerCallback(callbackQuery.Data))
            {
                await _tenantBotService.TryHandleOwnerCallbackAsync(botClient, callbackQuery, callbackCredUser, callbackUserState, cancellationToken);
                return;
            }

            if (callbackIsSuperAdmin && await _xuiV3AdminFlowService.TryHandleCallbackAsync(
                botClient,
                callbackQuery,
                GetMainMenuKeyboard(),
                cancellationToken))
            {
                return;
            }

            if (callbackQuery.Data != null && callbackQuery.Data.StartsWith("x3:"))
            {
                var callbackMainKeyboard = callbackIsSuperAdmin
                    ? GetMainMenuKeyboard()
                    : MainReplyMarkupKeyboardFa();
                if (await _xuiV3BotFlowService.TryHandleCallbackAsync(botClient, callbackQuery, callbackCredUser, callbackUserState, callbackMainKeyboard, cancellationToken))
                    return;
            }

            await ProccessCallbacks(callbackQuery, cancellationToken);
            return;
        }
        //if (true) return;
        // Only process Message updates: https://core.telegram.org/bots/api#message

        if (update.Message is not { } message)
            return;
        if (message.From == null)
            return;

        List<long> allowedValues = _appConfig.AdminsUserIds;
        var isSuperAdmin = message.From != null && allowedValues != null && allowedValues.Contains(message.From.Id);
        var messageCredUser = message.From == null
            ? null
            : await _credentialsDbContext.GetUserStatus(GetCreduserFromMessage(message));

        await _userActivityLog.LogMessageAsync(message, messageCredUser, isSuperAdmin, cancellationToken);

        if (!isSuperAdmin && messageCredUser?.IsBlocked == true)
        {
            await SendBlockedUserMessageAsync(botClient, message.Chat.Id, cancellationToken);
            return;
        }

        // Tenant bots answer as storefronts only; they do not expose the main brand menus.
        if (string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
        {
            var tenantUserState = await _userDbContext.GetUserStatus(message.From.Id);
            await _tenantBotService.TryHandleTenantUpdateAsync(botClient, update, messageCredUser, tenantUserState, cancellationToken);
            return;
        }

        if (isSuperAdmin && await TryHandleUserActivityLogCommandAsync(botClient, message, messageCredUser, cancellationToken))
            return;

        if (await TryHandleNowPaymentsReturnStartAsync(
            botClient,
            message,
            messageCredUser,
            isSuperAdmin ? GetMainMenuKeyboard() : MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }

        // Super-admin messages bypass the regular customer handler. Route the owned-bot referral menu explicitly
        // before the legacy admin command chain so an administrator can use the same customer-facing dashboard.
        if (isSuperAdmin && IsReferralMenuCommand(message.Text))
        {
            await TryHandleReferralMenuCommandAsync(
                botClient,
                message,
                user: null,
                cancellationToken);
            return;
        }

        if (!isSuperAdmin)
        {


            await HandleUpdateRegularUsers(botClient, update, cancellationToken);
            return;
        }
        // Only process text messages
        if (message.Text is not { } messageText)
            return;

        var chatId = message.Chat.Id;

        Console.WriteLine($"Received a '{messageText}' message in chat {chatId}.");

        //_userDbContext.Database.Migrate();
        //6257546736 amir
        //85758085 hamed
        // 888197418 admin hamed

        //        List<long> allowedValues = _configuration.GetSection("adminsUserIds").Get<List<long>>();
        var currentUser = await _userDbContext.GetUserStatus(message.From.Id);
        //_userDbContext.Users.Attach(currentUser);

        if (message.Text == "🤖 وضعیت ربات‌ها")
        {
            await SendRuntimeBotStatusAsync(botClient, message.Chat.Id, cancellationToken);
            return;
        }

        if (await _xuiV3AdminFlowService.TryHandleMessageAsync(
            botClient,
            message,
            currentUser,
            GetMainMenuKeyboard(),
            cancellationToken))
        {
            return;
        }

        if (await TryHandleManualPhoneVerificationAsync(
            botClient,
            message,
            currentUser,
            cancellationToken))
        {
            return;
        }

        // await ActiveBotClient.ForwardMessageAsync(
        //             chatId: 85758085,
        //             fromChatId: $"@kingofilter",
        //             messageId: 54107
        //         );

        if (message.Text == "/start")
        {

            //string hamed = "✅ Account details: \n Active: *Depleted* ❗️MultiIP \n Account Name: `vniaccgF8uNAN2` \n Expiration Date: 1402 / 12 / 1 - 8:13";
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Main Menu",
                replyMarkup: GetMainMenuKeyboard()
                );
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
        }

        else if (message.Text == "➕ Create New Account")
        {
            var createAccountKeyboard = GetLocationKeyboard();

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Select your country:",
                replyMarkup: createAccountKeyboard);

            // Save the user's context (selected country)
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Create New Account", Flow = "create" });

        }

        else if (GetLocations().Contains(message.Text))
        {
            // Update the user's context with the selected country
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, SelectedCountry = message.Text });


            var periodKeyboard = new ReplyKeyboardMarkup(new[]
            {
                new []
                {
                    new KeyboardButton("1 Month"),
                },
                new []
                {
                    new KeyboardButton("2 Months"),
                },
                new []
                {
                    new KeyboardButton("3 Months"),
                },
                new []
                {
                    new KeyboardButton("6 Months"),
                },
            });

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Select the account period:",
                replyMarkup: periodKeyboard);
        }

        else if (message.Text == "0 Month" || message.Text == "1 Month" || message.Text == "2 Months" || message.Text == "3 Months" || message.Text == "6 Months")
        {
            // Handle the selected period
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, SelectedPeriod = message.Text });

            // user does not go throw the actual flow
            var user = currentUser;
            if (string.IsNullOrEmpty(user.SelectedCountry) && (user.Flow == "create"))
            {
                await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Main Menu:",
                replyMarkup: GetMainMenuKeyboard());
                return;
            }


            // Create a keyboard based on the selected period
            var keyboard = GetAccountTypeKeyboard();

            // get trafic for renew
            if (currentUser.Flow == "update")
            {
                await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "get_traffic" });
                await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Type Traffic in GB and send! \n" + "For example if you send 20, the account will have 20GB traffic",
                        replyMarkup: new ReplyKeyboardRemove());
            }


            // Send the keyboard to the user for creation
            if (currentUser.Flow == "create")
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Select the account type:",
                    replyMarkup: keyboard
                );


        }

        else if (message.Text == "Reality Ipv6")
        {
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, Type = "realityv6", TotoalGB = "500" });

            var user = currentUser;
            if (string.IsNullOrEmpty(user.SelectedCountry) || string.IsNullOrEmpty(user.SelectedPeriod))
            {
                await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Main Menu:",
                replyMarkup: GetMainMenuKeyboard());
                return;
            }




            var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
            {
            new []
            {
                new KeyboardButton("Yes Create!"),
            },
            new []
            {
                new KeyboardButton("No Don't Create!"),
            },
        });

            user = currentUser;

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"You selected {user.SelectedCountry} for {user.SelectedPeriod} with account type {user.Type}. Confirm?",
                replyMarkup: confirmationKeyboard);

        }

        else if (message.Text == "All operators")
        {
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, Type = "tunnel", LastStep = "get_traffic" });

            var user = currentUser;
            if (string.IsNullOrEmpty(user.SelectedCountry) || string.IsNullOrEmpty(user.SelectedPeriod))
            {
                await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Main Menu:",
                replyMarkup: GetMainMenuKeyboard());
                return;
            }


            await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Type Traffic in GB and send! \n" + "For example if you send 20, the account will have 20GB traffic",
                        replyMarkup: new ReplyKeyboardRemove());
        }

        else if (message.Text == "Yes Create!")
        {
            await botClient.CustomSendTextMessageAsync(
                       chatId: message.Chat.Id,
                       text: "Please wait ...",
                        replyMarkup: new ReplyKeyboardRemove());


            var ready = await _userDbContext.IsUserReadyToCreate(message.From.Id);
            if (!ready) await botClient.CustomSendTextMessageAsync(
                       chatId: message.Chat.Id,
                       text: "Your information is not complete. please go throw steps correctly.",
                        replyMarkup: GetMainMenuKeyboard()); ;
            if (!ready) return;

            // Handle confirmation (create the account or perform other actions)
            var user = currentUser;

            // Access the server information from the servers.json file
            var serversJson = ReadJsonFile.ReadJsonAsString();
            var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

            if (servers.ContainsKey(user.SelectedCountry))
            {
                var serverInfo = servers[user.SelectedCountry];

                AccountDto accountDto = new AccountDto { TelegramUserId = message.From.Id, ServerInfo = serverInfo, SelectedCountry = user.SelectedCountry, SelectedPeriod = user.SelectedPeriod, AccType = user.Type, TotoalGB = user.TotoalGB };

                var result = await CreateAccount(accountDto);
                // Now you can use the selected country, period, and server information to perform actions
                // For example, create the account, send a request to the server, etc.
                if (result)
                {
                    user = await _userDbContext.GetUserStatus(currentUser.Id);

                    var msg = CaptionForAccountCreation(user, language: "en", showTraffic: false);

                    // Send the photo with caption


                    //await botClient.SendImagesWithCaptionAsync(message.Chat.Id, InputFile.FromStream(new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(user.ConfigLink, 200))), caption: msg)
                    await botClient.SendPhotoAsync(message.Chat.Id, InputFile.FromStream(new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(user.ConfigLink, 200))), caption: msg, parseMode: ParseMode.Markdown);
                    // .GetAwaiter()
                    // .GetResult();


                    await botClient.CustomSendTextMessageAsync(
                       chatId: message.Chat.Id,
                       text: "Main menu",
                        replyMarkup: GetMainMenuKeyboard());

                    await _userDbContext.ClearUserStatus(new User { Id = user.Id });

                }
            }
            else
            {
                // Handle the case where the selected country is not found in the servers.json file
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"Server information not found for {user.SelectedCountry}.",
                    replyMarkup: GetMainMenuKeyboard());
            }
        }

        else if (message.Text == "No Don't Create!")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            // Handle rejection or provide other options
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Account creation canceled.",
                replyMarkup: GetMainMenuKeyboard());
        }

        else if (message.Text == "ℹ️ Get Account Info")
        {
            await _userDbContext.SaveUserStatus(new User { Id = Convert.ToInt64(message.From.Id), LastStep = "Get Account Info", Flow = "read" });

            // Handle "Get Account Info" button click
            // You can implement the logic for this button here
            // For example, retrieve and display account information
            await botClient.CustomSendTextMessageAsync(
                               chatId: message.Chat.Id,
                               text: "Send your Vmess or Vless link:",
                                replyMarkup: new ReplyKeyboardRemove());

        }

        else if (currentUser.Flow == "read" && StartsWithVMessOrVLess(message.Text))
        {

            ClientExtend client = await TryGetClient(message.Text);
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            // Handle "Get Account Info" button click
            // You can implement the logic for this button here
            // For example, retrieve and display account information
            if (client == null)
            {
                await botClient.CustomSendTextMessageAsync(
                              chatId: message.Chat.Id,
                              text: "There is a Error with decoding Your config link!",
                               replyMarkup: GetMainMenuKeyboard());
                return;
            }

            var credUser = await _credentialsDbContext.GetUserStatus(GetCreduserFromMessage(message));
            string msg = string.Empty;

            // msg = $"✅ مشخصات اکانت شما:  \n";
            // msg += $"👤نام: `{client.Email}` \n";
            // //// msg += $"⌛️دوره : {ApiService.ConvertPeriodToDays(user.SelectedPeriod)} روزه \n";
            // //// msg += $"Location: {user.SelectedCountry} \n";
            // if (credUser.IsColleague) msg += $"🧮 حجم ترافیک: {client.TotalUsedTrafficInGB} گیگابایت\n";

            // string hijriShamsiDate = client.ExpiryTime.AddMinutes(210).ConvertToHijriShamsi();
            // msg += $"📅تاریخ انقضاء:  {hijriShamsiDate}\n";
            // msg += "\u200F" + "🔄 تمدید ⬅️  " + $"/renew_{client.Email} \n";


            msg = $"✅ Account details: \n";
            msg += $"Active: {client.Enable}";
            msg += $"\n Account Name: \n `{client.Email}` \n";

            msg += client.TotalUsedTrafficInGB;
            string hijriShamsiDate = client.ExpiryTime.AddMinutes(210).ConvertToHijriShamsi();
            msg += $"\nExpiration Date: {hijriShamsiDate}\n";


            await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id, parseMode: ParseMode.Markdown,
               text: msg,
                replyMarkup: GetMainMenuKeyboard());


        }

        else if (currentUser.Flow == "update" && StartsWithVMessOrVLess(message.Text))
        {
            var user = currentUser;
            user.ConfigLink = message.Text;
            await _userDbContext.SaveUserStatus(user);


            var periodKeyboard = new ReplyKeyboardMarkup(new[]
            {
                new []
                {
                    new KeyboardButton("0 Month"),
                },new []
                {
                    new KeyboardButton("1 Month"),
                },
                new []
                {
                    new KeyboardButton("2 Months"),
                },
                new []
                {
                    new KeyboardButton("3 Months"),
                },
                new []
                {
                    new KeyboardButton("6 Months"),
                },
            });

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Select the account period:",
                replyMarkup: periodKeyboard);

        }

        else if (message.Text == "🔄 Renew Existing Account")
        {
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Renew Existing Account", Flow = "update" });
            await botClient.CustomSendTextMessageAsync(
                               chatId: message.Chat.Id,
                               text: "Send your Vmess or Vless link:",
                                replyMarkup: new ReplyKeyboardRemove());
        }

        else if (message.Text == "📑 Menu")
        {
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Main Menu:",
                replyMarkup: GetMainMenuKeyboard());
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            // Handle "Menu" button click
            // You can implement the logic for this button here
            // For example, show a different menu or perform another action
        }

        else if (message.Text == "Yes Renew!")
        {

            await botClient.CustomSendTextMessageAsync(
                       chatId: message.Chat.Id,
                       text: "Please wait ...",
                        replyMarkup: new ReplyKeyboardRemove());


            var ready = await _userDbContext.IsUserReadyToUpdate(message.From.Id);
            if (!ready) await botClient.CustomSendTextMessageAsync(
                       chatId: message.Chat.Id,
                       text: "Your information is not complete. please go throw steps correctly.",
                        replyMarkup: GetMainMenuKeyboard()); ;
            if (!ready) return;


            var user = currentUser;
            ClientExtend client = await TryGetClient(user.ConfigLink);
            if (client == null)
            {
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

                await botClient.CustomSendTextMessageAsync(
                              chatId: message.Chat.Id,
                              text: "There is a Error with decoding Your config link!",
                               replyMarkup: new ReplyKeyboardRemove());
                return;
            }

            if (client != null)
            {
                ServerInfo findedServer = null;
                string findedcountry = null;
                AccountDtoUpdate accountDto = null;
                var serversJson = ReadJsonFile.ReadJsonAsString();
                var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);
                //trafic va modat faghat darim
                if (user.ConfigLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    // location nadarim Get location
                    var vless = Vless.DecodeVlessLink(user.ConfigLink);

                    // Iterate over the dictionary
                    foreach (var kvp in servers)
                    {
                        var country = kvp.Key;
                        ServerInfo serverInfo = kvp.Value;
                        if (serverInfo.Vless.Domain == vless.Domain)
                        {
                            findedServer = serverInfo;
                            findedcountry = country;
                        }
                    }
                    accountDto = new AccountDtoUpdate { TelegramUserId = message.From.Id, Client = client, ServerInfo = findedServer, SelectedCountry = findedcountry, SelectedPeriod = user.SelectedPeriod, AccType = "realityv6", TotoalGB = "500", ConfigLink = user.ConfigLink };
                }

                if (user.ConfigLink.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                {
                    var vmess = VMessConfiguration.DecodeVMessLink(user.ConfigLink);

                    // Iterate over the dictionary
                    foreach (var kvp in servers)
                    {
                        string country = kvp.Key;
                        ServerInfo serverInfo = kvp.Value;
                        if (serverInfo.VmessTemplate.Add == vmess.Add)
                        {
                            serverInfo.Inbounds = new List<Inbound> { serverInfo.Inbounds.FirstOrDefault(i => i.Port.ToString() == vmess.Port) };
                            serverInfo.VmessTemplate.Port = vmess.Port;
                            findedServer = serverInfo;
                            findedcountry = country;
                        }
                    }

                    accountDto = new AccountDtoUpdate { TelegramUserId = message.From.Id, Client = client, ServerInfo = findedServer, SelectedCountry = findedcountry, SelectedPeriod = user.SelectedPeriod, AccType = "tunnel", TotoalGB = user.TotoalGB, ConfigLink = user.ConfigLink };
                }
                await _userDbContext.SaveUserStatus(new User { Id = currentUser.Id, SelectedCountry = findedcountry });
                var result = await UpdateAccount(accountDto);


                if (result)
                {
                    user = await _userDbContext.GetUserStatus(user.Id);

                    user.TotoalGB = (Convert.ToInt64(user.TotoalGB) + (client.TotalGB / 1073741824L)).ToString();
                    var msg = $"✅ Account details: \n";
                    msg += $"Account Name: `{user.Email}`";
                    msg += $"\nLocation: {user.SelectedCountry} \nAdded duration: {user.SelectedPeriod}";
                    if (Convert.ToInt32(user.TotoalGB) < 100) msg += $"\nTraffic: {user.TotoalGB}GB.\n";
                    string hijriShamsiDate = client.ExpiryTime.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).AddMinutes(210).ConvertToHijriShamsi();

                    msg += $"\nExpiration Date: {hijriShamsiDate}\n";
                    msg += $"Your Sublink is: \n";
                    msg += $"`{user.SubLink}` \n";
                    msg += $"Your Connection link is: \n";
                    msg += "============= Tap to Copy =============\n";
                    msg += $"`{user.ConfigLink}`" + "\n ";

                    // Send the photo with caption

                    await botClient.SendPhotoAsync(message.Chat.Id, InputFile.FromStream(new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(user.ConfigLink, 200))), caption: msg, parseMode: ParseMode.Markdown);
                    // .GetAwaiter()
                    // .GetResult();
                    await _userDbContext.ClearUserStatus(new User { Id = user.Id });
                }

                await botClient.CustomSendTextMessageAsync(
           chatId: message.Chat.Id, parseMode: ParseMode.Markdown,
           text: "Main menu",
            replyMarkup: GetMainMenuKeyboard());


            }
        }

        else if (currentUser.LastStep == "get_traffic")
        {
            var isSuccessful = int.TryParse(message.Text, out int res);
            if (!isSuccessful)
            {
                await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Error! \n Type Traffic in GB and send! \n" + "For example if you send 20, the account will have 20GB traffic.\n Tap /start to cancell the proccess.",
                        replyMarkup: new ReplyKeyboardRemove());
                return;
            }

            if (currentUser.Flow == "update")
            {
                await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, TotoalGB = res.ToString() });

                // The user entered a valid number
                var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                           {
            new []
            {
                new KeyboardButton("Yes Renew!"),
            },
            new []
            {
                new KeyboardButton("No Don't Create!"),
            },
        });

                var user = currentUser;
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"You selected {user.SelectedPeriod}(s) with account type and Traffic {res}GB. Confirm?",
                    replyMarkup: confirmationKeyboard);
                return;
            }
            if (currentUser.Flow == "create")
            {
                if (int.TryParse(message.Text, out int userTraffic))
                {
                    await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, TotoalGB = userTraffic.ToString() });

                    // The user entered a valid number
                    var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                               {
            new []
            {
                new KeyboardButton("Yes Create!"),
            },
            new []
            {
                new KeyboardButton("No Don't Create!"),
            },
        });

                    var user = currentUser;

                    await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: $"You selected {user.SelectedCountry} for {user.SelectedPeriod}(s) with account type {user.Type} and Traffic {userTraffic}GB. Confirm?",
                        replyMarkup: confirmationKeyboard);


                    return;
                }
                // You can now use the 'userTraffic' value in your logic
                // For example, store it in a database, perform further actions, etc.
            }
            else
            {
                // The user did not enter a valid number
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Please enter a valid number."
                );
            }
        }


        else if (message.Text == "🗽 Admin")
        {
            await _userDbContext.ClearUserStatus(currentUser);

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Admin:",
                replyMarkup: GetAdminKeyboard());
        }

        //get public message:
        else if (currentUser.Flow == "admin" && currentUser.LastStep == "Get-public-message")
        {

            currentUser.ConfigLink = message.Text;
            currentUser.LastStep = "confirm-public-message";
            await _userDbContext.SaveUserStatus(currentUser);

            var audienceLabel = GetBroadcastAudienceLabel(currentUser.SubLink);
            await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: $"این پیام برای «{audienceLabel}» آماده شده است. آیا ارسال شود؟",
                            replyMarkup: GetMessageSendConfirmationKeyboard());

            var forwardMessage = GetChannelAndPost(message.Text);
            if (forwardMessage != null)
            {
                await ActiveBotClient.CustomForwardMessage(
                    chatId: message.Chat.Id,
                    fromChatId: forwardMessage.ChannelName,
                    messageId: forwardMessage.PostNumber
                );
            }
            else
            {
                await botClient.CustomSendTextMessageAsync(
            chatId: message.Chat.Id,
            text: currentUser.ConfigLink,
            replyMarkup: GetMessageSendConfirmationKeyboard());
            }


            return;
        }


        else if (currentUser.Flow == "admin" && currentUser.LastStep == "Get-trackid")
        {

            currentUser.ConfigLink = message.Text;
            currentUser.LastStep = "confirm-zibal-trackid";
            await _userDbContext.SaveUserStatus(currentUser);


            var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                                          {
                        new []
                        {
                            new KeyboardButton("Yes Confirm!"),
                        },
                        new []
                        {
                            new KeyboardButton("No Don't Confirm!"),
                        },});

            await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: $"This is Your trackid:{message.Text} Are  you Sure to confirm it?",
                            replyMarkup: confirmationKeyboard);

            return;
        }

        else if (currentUser.Flow == "admin" && currentUser.LastStep.Contains("get-money-amount"))
        {
            currentUser.LastStep = currentUser.LastStep.Replace("get-money-amount", "confirm-admin-action");
            currentUser.LastStep = currentUser.LastStep + "|" + (message.Text ?? "0");
            await _userDbContext.SaveUserStatus(currentUser);

            var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                               {
                        new []
                        {
                            new KeyboardButton("Yes Confirm!"),
                        },
                        new []
                        {
                            new KeyboardButton("No Don't Confirm!"),
                        },});

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "You have entered:\n" + message.Text + $"\n for action {currentUser.LastStep.Split('|')[1]}" + " Are you sure?",
                replyMarkup: confirmationKeyboard);

        }

        //get user id for admin operations:
        else if (currentUser.Flow == "admin" && currentUser.LastStep.Contains("get-tel-user-id"))
        {
            // var action = "🚀 Promote as admin";
            var action = currentUser.LastStep.Split('|')[1];
            if (action == "ℹ️ See User Account")
            {
                try
                {
                    // var userid = Convert.ToInt64(message.Text.Split('|').ElementAt(2));
                    var userid = Convert.ToInt64(message.Text);
                    if (userid == 0) throw new Exception("user id is null");
                    var findedClient = _credentialsDbContext.Users.Any(c => c.TelegramUserId == userid);
                    // if (!findedClient) await _credentialsDbContext.AddEmptyUser(userid);
                    // else { }
                    if (findedClient)
                    {
                        CredUser existedUser = await _credentialsDbContext.GetUserStatusWithId(userid);
                        var text = await GetUserProfileMessage(existedUser);
                        await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: text,
                            replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                    }
                    else
                    {
                        await botClient.CustomSendTextMessageAsync(
                                                    chatId: message.Chat.Id,
                                                    text: "User doesn't run the bot yet!. Ask him to first run the bot.",
                                                    replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);


                    }
                    await _userDbContext.ClearUserStatus(currentUser);

                }

                catch (System.Exception ex)
                {
                    string errorMessage;
                    switch (ex)
                    {
                        case ArgumentOutOfRangeException argumentOutOfRangeException:
                            errorMessage = "There is no userid in Database";
                            break;
                        case FormatException formatException:
                            errorMessage = "There is no userid in Database";
                            break;
                        case OverflowException overflowException:
                            errorMessage = "There is no userid in Database";
                            break;
                        default:
                            errorMessage = ex.Message;
                            break;
                    }
                    await botClient.CustomSendTextMessageAsync(
                                       chatId: message.Chat.Id,
                                       text: errorMessage,
                                       replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                    await _userDbContext.ClearUserStatus(currentUser);

                }
            }
            else if (action == "ℹ️ See All account of user")
            {
                try
                {
                    // var userid = Convert.ToInt64(message.Text.Split('|').ElementAt(2));
                    var userid = Convert.ToInt64(message.Text);
                    if (userid == 0) throw new Exception("user id is null");
                    var findedClient = _credentialsDbContext.Users.Any(c => c.TelegramUserId == userid);
                    // if (!findedClient) await _credentialsDbContext.AddEmptyUser(userid);
                    // else { }
                    if (findedClient)
                    {
                        CredUser existedUser = await _credentialsDbContext.GetUserStatusWithId(userid);
                        // var text = await GetUserProfileMessage(existedUser);

                        await botClient.CustomSendTextMessageAsync(
                      chatId: message.Chat.Id,
                      text: "Please wait for tens seconds ...",
                      replyMarkup: new ReplyKeyboardRemove());

                        var accounts = await TryGetَAllClient(existedUser.TelegramUserId);
                        if (accounts.Count < 1)
                        {

                            await botClient.CustomSendTextMessageAsync(
                           chatId: message.Chat.Id,
                           text: "There is no account for specified user!",
                           replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                            return;
                        }

                        await SendMessageWithClientInfo(message.Chat.Id, true, accounts);


                        await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "Main Menu",
                            replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                        await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                        return;

                    }
                    else
                    {
                        await botClient.CustomSendTextMessageAsync(
                                                    chatId: message.Chat.Id,
                                                    text: "User doesn't run the bot yet!. Ask him to first run the bot.",
                                                    replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                    }
                    await _userDbContext.ClearUserStatus(currentUser);

                }

                catch (System.Exception ex)
                {
                    string errorMessage;
                    switch (ex)
                    {
                        case ArgumentOutOfRangeException argumentOutOfRangeException:
                            errorMessage = "There is no userid in Database";
                            break;
                        case FormatException formatException:
                            errorMessage = "There is no userid in Database";
                            break;
                        case OverflowException overflowException:
                            errorMessage = "There is no userid in Database";
                            break;
                        default:
                            errorMessage = ex.Message;
                            break;
                    }
                    await botClient.CustomSendTextMessageAsync(
                                       chatId: message.Chat.Id,
                                       text: errorMessage,
                                       replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                    await _userDbContext.ClearUserStatus(currentUser);

                }
            }
            //promote demote
            else if (action == "🚀 Promote as admin" || action == "❌ Demote as admin")
            {
                // get confirmation
                currentUser.LastStep = currentUser.LastStep.Replace("get-tel-user-id", "confirm-admin-action");
                currentUser.LastStep = currentUser.LastStep + "|" + (message.Text ?? "0");
                await _userDbContext.SaveUserStatus(currentUser);


                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "You have entered:\n" + message.Text + $"\n for action {currentUser.LastStep.Split('|')[1]}" + " Are you sure?",
                    replyMarkup: GetAdminConfirmationKeyboard());
            }

            else if (action == "➕ Add credit" || action == "➖ Reduce credit")
            {
                // get confirmation
                currentUser.LastStep = currentUser.LastStep.Replace("get-tel-user-id", "get-money-amount");
                currentUser.LastStep = currentUser.LastStep + "|" + (message.Text ?? "0");
                await _userDbContext.SaveUserStatus(currentUser);

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Enter Credit and send it:",
                    replyMarkup: new ReplyKeyboardRemove());
            }

        }

        // confirm ZIBAL
        else if ((message.Text == "Yes Confirm!" || message.Text == "No Don't Confirm!") && currentUser.Flow == "admin" && currentUser.LastStep == "confirm-zibal-trackid")
        {

            if (message.Text == "No Don't Confirm!")
            {
                await _userDbContext.ClearUserStatus(currentUser);
                if (message.Text == "No Don't Confirm!")
                    await botClient.CustomSendTextMessageAsync(
                         chatId: message.Chat.Id,
                         text: "Cancelled",
                         replyMarkup: GetMainMenuKeyboard());
            }

            long trackId = 0;
            var isTrackidValid = long.TryParse(currentUser.ConfigLink, out trackId);
            if (!isTrackidValid)
            {
                await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: "There is a error with interpreting the user inputs",
                                    replyMarkup: GetMainMenuKeyboard());
                return;
            }


            var credUser = await _credentialsDbContext.GetUserStatus(GetCreduserFromMessage(message));
            try
            {
                var zpi = _userDbContext.ZibalPaymentInfos.SingleOrDefault(x => x.TrackId == trackId);
                var inq_respnse = await ZibalAPI.Inquiry(zpi.TrackId, _appConfig.ZibalMerchantCode);
                var msg = await ZibalAPI.VerifyAndGetMessage(trackId, _appConfig.ZibalMerchantCode);
                if (msg == "your payment was successfully confirmed!")
                {
                    zpi = ZibalAPI.MarkAsPaid(zpi, inq_respnse);


                    await ZibalAddtoBalance(zpi, _appConfig, credUser, chatId, true);
                    zpi.IsAddedToBallance = true;
                    await _userDbContext.SaveChangesAsync();

                }

                await botClient.CustomSendTextMessageAsync(
                                   chatId: message.Chat.Id,
                                   text: msg,
                                   replyMarkup: GetMainMenuKeyboard());

                return;

            }
            catch
            {
                await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: "There is a error with confirmation proccess!",
                                    replyMarkup: GetMainMenuKeyboard());
                return;
            }


        }
        //get confirmation and do admin action:
        else if ((message.Text == "Yes Confirm!" || message.Text == "No Don't Confirm!") && currentUser.Flow == "admin" && currentUser.LastStep.Contains("confirm-admin-action"))
        {
            string status = "", action = "", userid = "";
            bool canContinue = false;
            try
            {
                status = currentUser.LastStep.Split('|')[0];
                action = currentUser.LastStep.Split('|')[1];
                userid = currentUser.LastStep.Split('|')[2];
                canContinue = true;
                if (string.IsNullOrEmpty(status) || string.IsNullOrEmpty(action) || string.IsNullOrEmpty(userid))
                    canContinue = false;

            }
            catch (System.Exception)
            {
                canContinue = false;
            }
            if (!canContinue)
            {
                await botClient.CustomSendTextMessageAsync(
                                     chatId: message.Chat.Id,
                                     text: "There is a error with interpreting the user inputs",
                                     replyMarkup: GetMainMenuKeyboard());
                return;
            }

            // "admin-confirmed" "get-money-amount"
            long cUserId;
            bool isuseridValid = long.TryParse(userid, out cUserId);
            long amount = 0;
            bool isCreditAmountValid = false;
            if (action == "➕ Add credit" || action == "➖ Reduce credit")
            {
                if (currentUser.LastStep.Split('|').Length >= 4)
                    isCreditAmountValid = long.TryParse(currentUser.LastStep.Split('|')[3], out amount);
            }
            // currentUser.LastStep.Replace("get-tel-user-id", "get-money-amount");

            if (message.Text == "No Don't Confirm!" || !isuseridValid)
            {
                await _userDbContext.ClearUserStatus(currentUser);
                if (message.Text == "No Don't Confirm!")
                    await botClient.CustomSendTextMessageAsync(
                         chatId: message.Chat.Id,
                         text: "Cancelled",
                         replyMarkup: GetMainMenuKeyboard());
                else
                {
                    await botClient.CustomSendTextMessageAsync(
                         chatId: message.Chat.Id,
                         text: "User Input is not correct",
                         replyMarkup: GetMainMenuKeyboard());
                }
            }
            else
            {
                var findedUser = await _credentialsDbContext.GetUserStatusWithId(cUserId);
                if (findedUser == null)
                {
                    await botClient.CustomSendTextMessageAsync(
                                                    chatId: message.Chat.Id,
                                                    text: "User with specified id doesn't existed!",
                                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

                    return;
                }

                if (action == "➕ Add credit")
                {

                    if (isCreditAmountValid)
                    {
                        var beforeBalance = findedUser.AccountBalance;
                        findedUser.AccountBalance += amount;
                        await _credentialsDbContext.SaveChangesAsync();
                        var afterBalance = findedUser.AccountBalance;
                        await _walletLedgerService.RecordAsync(
                            findedUser.TelegramUserId,
                            WalletLedgerDirections.Credit,
                            amount,
                            beforeBalance,
                            afterBalance,
                            WalletLedgerReasons.AdminAdjustment,
                            provider: "admin",
                            referenceType: "admin-adjustment",
                            referenceId: message.From.Id.ToString(CultureInfo.InvariantCulture),
                            description: "Admin wallet credit",
                            cancellationToken: cancellationToken);

                        LogAdminWalletAdjustment(
                            message.From,
                            findedUser,
                            isCredit: true,
                            amount,
                            beforeBalance,
                            afterBalance);


                        await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);


                        await botClient.CustomSendTextMessageAsync(
                                                    chatId: (findedUser).ChatID,
                                                    text: $"حساب شما به میزان {amount} تومان از طرف مدیریت شارژ شد.",
                                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                        await _ownedBotNotificationService.NotifyUserAcrossOwnedBotsAsync(
                            findedUser.TelegramUserId,
                            $"✅ حساب شما به میزان {amount.FormatCurrency()} از طرف مدیریت شارژ شد.\nموجودی جدید: {afterBalance.FormatCurrency()}",
                            cancellationToken: cancellationToken);

                        await botClient.CustomSendTextMessageAsync(
                        chatId: findedUser.ChatID,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                    }

                    else
                    {
                        await _userDbContext.ClearUserStatus(currentUser);
                        await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "The credit amount you have just entered is not correct! go through steps again! ",
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                    }
                    await _userDbContext.ClearUserStatus(currentUser);
                }
                else if (action == "➖ Reduce credit")
                {
                    if (isCreditAmountValid)
                    {
                        var beforeBalance = findedUser.AccountBalance;
                        findedUser.AccountBalance -= amount;
                        await _credentialsDbContext.SaveChangesAsync();
                        var afterBalance = findedUser.AccountBalance;
                        await _walletLedgerService.RecordAsync(
                            findedUser.TelegramUserId,
                            WalletLedgerDirections.Debit,
                            amount,
                            beforeBalance,
                            afterBalance,
                            WalletLedgerReasons.AdminAdjustment,
                            provider: "admin",
                            referenceType: "admin-adjustment",
                            referenceId: message.From.Id.ToString(CultureInfo.InvariantCulture),
                            description: "Admin wallet debit",
                            cancellationToken: cancellationToken);

                        LogAdminWalletAdjustment(
                            message.From,
                            findedUser,
                            isCredit: false,
                            amount,
                            beforeBalance,
                            afterBalance);

                        await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                        await botClient.CustomSendTextMessageAsync(
                                                    chatId: findedUser.ChatID,
                                                    text: $"از حساب شما به میزان {amount} تومان از طرف مدیریت کسر شد.",
                                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                        await _ownedBotNotificationService.NotifyUserAcrossOwnedBotsAsync(
                            findedUser.TelegramUserId,
                            $"➖ از حساب شما به میزان {amount.FormatCurrency()} از طرف مدیریت کسر شد.\nموجودی جدید: {afterBalance.FormatCurrency()}",
                            cancellationToken: cancellationToken);

                        await botClient.CustomSendTextMessageAsync(
                        chatId: findedUser.ChatID,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                    }


                    else
                    {
                        await _userDbContext.ClearUserStatus(currentUser);
                        await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "The credit amount you have just entered is not correct! go through steps again! ",
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                    }

                    await _userDbContext.ClearUserStatus(currentUser);

                }
                else if (action == "🚀 Promote as admin")
                {
                    var wasColleague = findedUser.IsColleague;
                    findedUser.IsColleague = true;
                    var roleChanged = await _credentialsDbContext.PromotOrDemote(findedUser.TelegramUserId, true);
                    if (roleChanged)
                        LogAdminRoleChange(message.From, findedUser, wasColleague, isColleagueAfter: true);


                    await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                    await botClient.CustomSendTextMessageAsync(
                        chatId: findedUser.ChatID,
                        text: "تبریک! \n شما اکنون همکار مجموعه ما هستید. \n" + await GetUserProfileMessage(findedUser),
                        replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

                    await _userDbContext.ClearUserStatus(currentUser);

                }
                else if (action == "❌ Demote as admin")
                {
                    // Demote as admin logic here
                    var wasColleague = findedUser.IsColleague;
                    findedUser.IsColleague = false;
                    var roleChanged = await _credentialsDbContext.PromotOrDemote(findedUser.TelegramUserId, false);
                    if (roleChanged)
                        LogAdminRoleChange(message.From, findedUser, wasColleague, isColleagueAfter: false);

                    await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                    await botClient.CustomSendTextMessageAsync(
                   chatId: findedUser.ChatID,
                   text: "شما اکنون کاربر عادی مجموعه ما هستید.\n" + await GetUserProfileMessage(findedUser),
                   replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

                    await _userDbContext.ClearUserStatus(currentUser);

                }

                else
                {
                    await _userDbContext.ClearUserStatus(currentUser);
                    await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Something went wrong! Go through the steps correctly",
                    replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);


                }


                // switch (action)
                // {
                //     case "➕ Add credit":

                //         if (isCreditAmountValid)
                //         {
                //             await _credentialsDbContext.AddFund(findedUser, amount);
                //             findedUser.AccountBalance += amount;

                //             await botClient.CustomSendTextMessageAsync(
                //             chatId: message.Chat.Id,
                //             text: await GetUserProfileMessage(findedUser),
                //             replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);


                //             await botClient.CustomSendTextMessageAsync(
                //                                         chatId: findedUser.ChatID,
                //                                         text: $"حساب شما به میزان {amount} تومان از طرف مدیریت شارژ شد.",
                //                                         replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

                //             await botClient.CustomSendTextMessageAsync(
                //             chatId: findedUser.ChatID,
                //             text: await GetUserProfileMessage(findedUser),
                //             replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                //         }

                //         else
                //         {
                //             await _userDbContext.ClearUserStatus(currentUser);
                //             await botClient.CustomSendTextMessageAsync(
                //             chatId: message.Chat.Id,
                //             text: "The credit amount you have just entered is not correct! go through steps again! ",
                //             replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                //         }
                //         await _userDbContext.ClearUserStatus(currentUser);
                //         break;
                //     case "➖ Reduce credit":

                //         break;
                //     case "🚀 Promote as admin":

                //         findedUser.IsColleague = true;
                //         await _credentialsDbContext.SaveUserStatus(findedUser);

                //         await botClient.CustomSendTextMessageAsync(
                //             chatId: message.Chat.Id,
                //             text: await GetUserProfileMessage(findedUser),
                //             replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                //         await botClient.CustomSendTextMessageAsync(
                //             chatId: findedUser.ChatID,
                //             text: "تبریک! \n شما اکنون همکار مجموعه ما هستید. \n" + await GetUserProfileMessage(findedUser),
                //             replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

                //         await _userDbContext.ClearUserStatus(currentUser);

                //         break;
                //     case "❌ Demote as admin":
                //         // Demote as admin logic here
                //         findedUser.IsColleague = false;
                //         await _credentialsDbContext.SaveUserStatus(findedUser);

                //         await botClient.CustomSendTextMessageAsync(
                //             chatId: message.Chat.Id,
                //             text: await GetUserProfileMessage(findedUser),
                //             replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                //         await botClient.CustomSendTextMessageAsync(
                //        chatId: findedUser.ChatID,
                //        text: "شما اکنون کاربر عادی مجموعه ما هستید.\n" + await GetUserProfileMessage(findedUser),
                //        replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

                //         await _userDbContext.ClearUserStatus(currentUser);

                //         break;
                //     case "ℹ️ See User Account":
                //         // See user account logic here
                //         // it has been implemented before!
                //         break;

                //     default:
                //         await _userDbContext.ClearUserStatus(currentUser);
                //         await botClient.CustomSendTextMessageAsync(
                //         chatId: message.Chat.Id,
                //         text: "Something went wrong! Go through the steps correctly",
                //         replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);
                //         break;
                // }
                // // await botClient.CustomSendTextMessageAsync(
                // chatId: message.Chat.Id,
                // text: "You have entered:\n" + message.Text + "\n Are you sure?",
                // replyMarkup: GetMainMenuKeyboard());

            }

        }

        //send public message:
        else if (currentUser.Flow == "admin" && currentUser.LastStep == "confirm-public-message")
        {
            var channelPost = GetChannelAndPost(currentUser.ConfigLink);
            if (message.Text == "Yes Send!")
            {
                var audience = NormalizeBroadcastAudience(currentUser.SubLink);
                var audienceLabel = GetBroadcastAudienceLabel(audience);
                // Broadcast recipients must be scoped to the owned bot that opened this admin flow.
                // credentials.db contains shared users for every brand, so it is only used after
                // BotUserStates has narrowed the audience to the current BotId.
                var allUsers = await GetBroadcastRecipientsAsync(audience, cancellationToken);

                if (allUsers.Count == 0)
                {
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        $"هیچ گیرنده‌ای برای ارسال پیام عمومی به «{audienceLabel}» پیدا نشد.",
                        replyMarkup: GetMainMenuKeyboard());
                    await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                    return;
                }

                if (channelPost != null)
                {
                    var template = new BroadcastManager.BroadcastItem
                    {
                        FromChatId = BuildForwardSourceChatId(channelPost.ChannelName),
                        MessageId = channelPost.PostNumber,
                        IsForward = true
                    };

                    await StartBroadcastJobAsync(botClient, message, allUsers, template, cancellationToken);
                }
                else
                {
                    var template = new BroadcastManager.BroadcastItem
                    {
                        Text = currentUser.ConfigLink,
                        IsForward = false
                    };

                    await StartBroadcastJobAsync(botClient, message, allUsers, template, cancellationToken);
                }

                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });





                //     //forward
                //     if (channelPost != null)
                //     {
                //         foreach (var item in _credentialsDbContext.Users)
                //         {
                //             await ActiveBotClient.CustomForwardMessage(
                //                 chatId: item.ChatID,
                //                 fromChatId: channelPost.ChannelName,
                //                 messageId: channelPost.PostNumber
                //             );
                //             // Console.WriteLine("Message forwarded successfully.");
                //         }
                //     }

                //     // normal message
                //     else
                //     {
                //         foreach (var item in _credentialsDbContext.Users)
                //         {
                //             // Console.WriteLine($"{item.ChatID}")
                //             await botClient.CustomSendTextMessageAsync(
                //                                         chatId: item.ChatID,
                //                                         text: currentUser.ConfigLink,
                //                                         parseMode: ParseMode.Markdown,
                //                                         replyMarkup: inlineKeyboard
                //                                         );
                //         }

                //     }
                //     await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                //     await botClient.CustomSendTextMessageAsync(

            }
            else if (message.Text == "Preview message")
            {
                if (channelPost != null)
                {
                    await ActiveBotClient.CustomForwardMessage(
                        chatId: message.Chat.Id,
                        fromChatId: channelPost.ChannelName,
                        messageId: channelPost.PostNumber);
                }
                else
                {
                    await botClient.CustomSendTextMessageAsync(
                        chatId: message.From.Id,
                        text: currentUser.ConfigLink,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: GetMessageSendConfirmationKeyboard());
                }
            }
            else if (message.Text == "No Don't Send!")
            {
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                await botClient.CustomSendTextMessageAsync(
                                           chatId: message.Chat.Id,
                                           text: "The Operation(send message) has been cancelled.",
                                            replyMarkup: GetMainMenuKeyboard());

            }
            else
            {
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                await botClient.CustomSendTextMessageAsync(
                                           chatId: message.Chat.Id,
                                           text: "Oops! Start Again",
                                            replyMarkup: GetMainMenuKeyboard());
            }
        }

        else if (GetAdminActions().Contains(message.Text))
        {
            currentUser.Flow = "admin";
            currentUser.LastStep += "|" + message.Text;
            await _userDbContext.SaveUserStatus(currentUser);

            if (message.Text == "📑 Menu")
            {
                await _userDbContext.ClearUserStatus(currentUser);
                return;
            }
            else if (message.Text == "📨 Send message to all")
            {
                currentUser.Flow = "admin";
                currentUser.LastStep = "select-public-message-audience";
                currentUser.SubLink = string.Empty;
                await _userDbContext.SaveUserStatus(currentUser);

                await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "مخاطب پیام عمومی را انتخاب کنید:",
                                replyMarkup: BuildBroadcastAudienceKeyboard());
                return;
            }


            else if (message.Text == "✔️ Verify payment")
            {
                currentUser.Flow = "admin";
                currentUser.LastStep = "Get-trackid";
                await _userDbContext.SaveUserStatus(currentUser);

                await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "Type your Trackid(Zibal) and Send it:",
                                replyMarkup: new ReplyKeyboardRemove());
                return;
            }
            else
            {
                currentUser.Flow = "admin";
                currentUser.LastStep = "get-tel-user-id" + "|" + message.Text;
                await _userDbContext.SaveUserStatus(currentUser);

                // baraye ersal payam ya ertegha be admin
                await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Send User (user must get it from @userinfobot or our bot)",
                replyMarkup: new ReplyKeyboardRemove());

                return;
            }
        }

        else
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.CustomSendTextMessageAsync(
                                       chatId: message.Chat.Id,
                                       text: "Oops! Start Again",
                                        replyMarkup: GetMainMenuKeyboard());

        }

    }

    private async Task ProccessCallbacks(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        //Process call back query
        if (callbackQuery == null || string.IsNullOrWhiteSpace(callbackQuery.Data))
            return;

        try
        {
            if (callbackQuery.Data.Contains("Paid!"))
                return;

            if (callbackQuery.Data.StartsWith("broadcast_status_", StringComparison.Ordinal))
            {
                await ProcessBroadcastStatusCallback(callbackQuery, cancellationToken);
                return;
            }

            if (callbackQuery.Data.StartsWith("broadcast_scope_", StringComparison.Ordinal))
            {
                await ProcessBroadcastAudienceCallback(callbackQuery, cancellationToken);
                return;
            }

            if (callbackQuery.Data.StartsWith("ledger:", StringComparison.Ordinal))
            {
                await ProcessWalletLedgerCallback(callbackQuery, cancellationToken);
                return;
            }

            if (callbackQuery.Data.StartsWith("check_crypto_payment_"))
            {
                await ProcessCryptoPaymentCallback(callbackQuery, cancellationToken);
                return;
            }

            if (callbackQuery.Data.StartsWith("check_hooshpay_payment_") ||
                callbackQuery.Data.StartsWith("hpchk_"))
            {
                await ProcessHooshPayPaymentCallback(callbackQuery, cancellationToken);
                return;
            }

            if (callbackQuery.Data.StartsWith("settle_crypto_partial_"))
            {
                await ProcessCryptoPartialSettlementCallback(callbackQuery, cancellationToken);
                return;
            }

            if (callbackQuery.Data.Contains("check_payment_"))
            {
                await ProcessZibalPaymentCallback(callbackQuery, cancellationToken);
                return;
            }
        }
        catch (Exception ex)
        {
            var credUser = await _credentialsDbContext.GetUserStatus(
                GetCreduserFromTelegramUser(callbackQuery.From, callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id));

            await _userActivityLog.LogErrorAsync(
                "payment_callback_failed",
                ex,
                credUser,
                IsSuperAdminUser(callbackQuery.From.Id),
                new Dictionary<string, object>
                {
                    ["callbackData"] = callbackQuery.Data ?? string.Empty
                },
                cancellationToken);

            Console.WriteLine($"[PaymentCallback] exception data={callbackQuery.Data}: {ex}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
                text: "بررسی پرداخت با خطا روبه‌رو شد. جزئیات خطا در لاگ ثبت شد؛ لطفاً کمی بعد دوباره تلاش کنید.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
        }
    }

    private async Task ProcessBroadcastAudienceCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (!IsSuperAdminUser(callbackQuery.From.Id))
        {
            await ActiveBotClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                text: "این بخش فقط برای سوپرادمین‌هاست.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        var messageId = callbackQuery.Message?.MessageId;
        var selected = callbackQuery.Data.Replace("broadcast_scope_", "", StringComparison.Ordinal);

        if (string.Equals(selected, "back", StringComparison.OrdinalIgnoreCase))
        {
            await _userDbContext.ClearUserStatus(new User { Id = callbackQuery.From.Id });

            if (messageId.HasValue)
            {
                await ActiveBotClient.EditMessageTextAsync(
                    chatId: chatId,
                    messageId: messageId.Value,
                    text: "ارسال پیام عمومی لغو شد.",
                    cancellationToken: cancellationToken);
            }

            await ActiveBotClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Admin:",
                replyMarkup: GetAdminKeyboard(),
                cancellationToken: cancellationToken);

            await ActiveBotClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        var audience = NormalizeBroadcastAudience(selected);
        var currentUser = await _userDbContext.GetUserStatus(callbackQuery.From.Id);
        currentUser.Flow = "admin";
        currentUser.LastStep = "Get-public-message";
        currentUser.SubLink = audience;
        await _userDbContext.SaveUserStatus(currentUser);

        var audienceLabel = GetBroadcastAudienceLabel(audience);
        if (messageId.HasValue)
        {
            await ActiveBotClient.EditMessageTextAsync(
                chatId: chatId,
                messageId: messageId.Value,
                text: $"مخاطب انتخاب شد: <b>{Html(audienceLabel)}</b>\n\nحالا متن پیام یا لینک پست کانال را ارسال کنید.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

        await ActiveBotClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Type your message and Send it:",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        await ActiveBotClient.AnswerCallbackQueryAsync(
            callbackQuery.Id,
            text: "مخاطب پیام عمومی ثبت شد.",
            cancellationToken: cancellationToken);
    }

    private async Task SendWalletLedgerAsync(ITelegramBotClient botClient, ChatId chatId, long telegramUserId, int page, CancellationToken cancellationToken)
    {
        var (items, totalCount) = await _walletLedgerService.GetPageAsync(telegramUserId, page, 8, cancellationToken);
        await botClient.SendTextMessageAsync(
            chatId,
            BuildWalletLedgerListText(items, totalCount, page),
            parseMode: ParseMode.Html,
            replyMarkup: BuildWalletLedgerKeyboard(items, totalCount, page),
            cancellationToken: cancellationToken);
    }

    private async Task ProcessWalletLedgerCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data ?? string.Empty;
        var parts = data.Split(':');
        var botClient = ActiveBotClient;
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;

        if (parts.Length >= 3 && parts[1] == "page" && int.TryParse(parts[2], out var page))
        {
            var (items, totalCount) = await _walletLedgerService.GetPageAsync(callbackQuery.From.Id, page, 8, cancellationToken);
            await botClient.EditMessageTextAsync(
                chatId,
                callbackQuery.Message.MessageId,
                BuildWalletLedgerListText(items, totalCount, page),
                parseMode: ParseMode.Html,
                replyMarkup: BuildWalletLedgerKeyboard(items, totalCount, page),
                cancellationToken: cancellationToken);
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        if (parts.Length >= 3 && parts[1] == "detail" && int.TryParse(parts[2], out var entryId))
        {
            var entry = await _walletLedgerService.GetByIdAsync(callbackQuery.From.Id, entryId, cancellationToken);
            if (entry == null)
            {
                await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "تراکنش پیدا نشد.", showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            await botClient.EditMessageTextAsync(
                chatId,
                callbackQuery.Message.MessageId,
                BuildWalletLedgerDetailText(entry),
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("↩️ بازگشت به لیست", "ledger:page:0") },
                    new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ledger:home") }
                }),
                cancellationToken: cancellationToken);
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        if (parts.Length >= 2 && parts[1] == "home")
        {
            await botClient.SendTextMessageAsync(
                chatId,
                "منوی اصلی",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
        }
    }

    private static string BuildWalletLedgerListText(IReadOnlyCollection<WalletLedgerEntry> items, int totalCount, int page)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / 8d));
        var builder = new StringBuilder();
        builder.AppendLine("📒 <b>تراکنش‌های من</b>");
        builder.AppendLine($"صفحه <code>{page + 1}</code> از <code>{totalPages}</code>");
        builder.AppendLine();
        builder.AppendLine(items.Count == 0
            ? "هنوز تراکنشی ثبت نشده است."
            : "برای مشاهده جزئیات، روی دکمه هر تراکنش بزنید.");
        return builder.ToString();
    }

    private static InlineKeyboardMarkup BuildWalletLedgerKeyboard(IReadOnlyCollection<WalletLedgerEntry> items, int totalCount, int page)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var item in items)
        {
            var isCredit = string.Equals(item.Direction, WalletLedgerDirections.Credit, StringComparison.OrdinalIgnoreCase);
            var icon = isCredit ? "➕🟢" : "➖🔴";
            var text = $"{icon} {item.AmountToman.FormatCurrency()} 🔚 {item.BalanceAfter.FormatCurrency()}";
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(text, $"ledger:detail:{item.Id}") });
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / 8d));
        var nav = new List<InlineKeyboardButton>();
        if (page > 0)
            nav.Add(InlineKeyboardButton.WithCallbackData("⬅️ قبلی", $"ledger:page:{page - 1}"));
        if (page + 1 < totalPages)
            nav.Add(InlineKeyboardButton.WithCallbackData("بعدی ➡️", $"ledger:page:{page + 1}"));
        if (nav.Count > 0)
            rows.Add(nav.ToArray());

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ledger:home") });
        return new InlineKeyboardMarkup(rows);
    }

    private static string BuildWalletLedgerDetailText(WalletLedgerEntry entry)
    {
        var isCredit = string.Equals(entry.Direction, WalletLedgerDirections.Credit, StringComparison.OrdinalIgnoreCase);
        return $"{(isCredit ? "➕🟢" : "➖🔴")} <b>جزئیات تراکنش</b>\n\n" +
               $"مبلغ: <code>{Html(entry.AmountToman.FormatCurrency())}</code>\n" +
               $"بابت: <code>{Html(entry.Reason)}</code>\n" +
               $"منبع/درگاه: <code>{Html(entry.Provider)}</code>\n" +
               $"شماره سفارش: <code>{Html(entry.OrderId)}</code>\n" +
               $"موجودی قبل: <code>{Html(entry.BalanceBefore.FormatCurrency())}</code>\n" +
               $"موجودی بعد: <code>{Html(entry.BalanceAfter.FormatCurrency())}</code>\n" +
               $"ربات: <code>{Html(entry.BotUsername)}</code>\n" +
               $"توضیح: <code>{Html(entry.Description)}</code>\n" +
               $"زمان: <code>{Html(entry.CreatedAtUtc.AddMinutes(210).ConvertToHijriShamsi())}</code>";
    }

    private async Task ProcessBroadcastStatusCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var jobId = callbackQuery.Data.Replace("broadcast_status_", "");
        var job = _broadcastManager.GetJob(jobId);
        if (job == null)
        {
            await ActiveBotClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                text: "وضعیت این ارسال پیدا نشد.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        if (job.RequestedByTelegramUserId != callbackQuery.From.Id && !IsSuperAdminUser(callbackQuery.From.Id))
        {
            await ActiveBotClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                text: "فقط ادمین شروع‌کننده ارسال می‌تواند این وضعیت را بروزرسانی کند.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        await _broadcastManager.RefreshStatusMessageAsync(jobId, cancellationToken);
        await ActiveBotClient.AnswerCallbackQueryAsync(
            callbackQuery.Id,
            text: "وضعیت بروزرسانی شد.",
            cancellationToken: cancellationToken);
    }

    private async Task StartBroadcastJobAsync(
        ITelegramBotClient botClient,
        Message message,
        IReadOnlyCollection<long> allUsers,
        BroadcastManager.BroadcastItem template,
        CancellationToken cancellationToken)
    {
        Message statusMessage = null;
        try
        {
            statusMessage = await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"در حال آماده‌سازی ارسال عمومی برای <code>{allUsers.Count}</code> کاربر...",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            var job = await _broadcastManager.EnqueueAsync(
                allUsers,
                template,
                message.Chat.Id,
                statusMessage.MessageId,
                message.From.Id,
                CancellationToken.None);

            await _broadcastManager.RefreshStatusMessageAsync(job.Id, cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "ارسال عمومی شروع شد. وضعیت را از پیام بالا پیگیری کنید.",
                replyMarkup: GetMainMenuKeyboard(),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Broadcast start failed. admin={message.From?.Id}, error={ex}");
            var errorText = $"شروع ارسال عمومی با خطا روبه‌رو شد:\n<code>{Html(ex.Message)}</code>";
            if (statusMessage != null)
            {
                await botClient.EditMessageTextAsync(
                    chatId: message.Chat.Id,
                    messageId: statusMessage.MessageId,
                    text: errorText,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: errorText,
                    parseMode: ParseMode.Html,
                    replyMarkup: GetMainMenuKeyboard(),
                    cancellationToken: cancellationToken);
            }
        }
    }

    private async Task ProcessZibalPaymentCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;

        if (!int.TryParse(callbackQuery.Data.Replace("check_payment_", ""), out var zpiId))
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "شناسه پرداخت زیبال معتبر نیست.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var zpi = await _userDbContext.ZibalPaymentInfos.FindAsync(new object[] { zpiId }, cancellationToken);
        if (zpi == null)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت زیبال مورد نظر در دیتابیس پیدا نشد.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (zpi.IsAddedToBallance)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار مربوط به این پرداخت قبلاً به حساب کاربری شما افزوده شده است.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            await EditMessageWithCallback(ActiveBotClient, zpi.ChatId, Convert.ToInt32(zpi.TelMsgId));
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var inquiry = await ZibalAPI.Inquiry(zpi.TrackId, _appConfig.ZibalMerchantCode);
        if (inquiry == null)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پاسخ معتبری از زیبال دریافت نشد. لطفاً کمی بعد دوباره بررسی کنید.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        zpi.Result = BuildZibalStatusText(inquiry);
        await _userDbContext.SaveChangesAsync(cancellationToken);

        Console.WriteLine(
            $"[Zibal] inquiry paymentId={zpi.Id}, trackId={zpi.TrackId}, user={zpi.TelegramUserId}, status={inquiry.Status}, result={inquiry.Result}, message={inquiry.Message}");

        if (inquiry.Status == 2)
        {
            var verify = await ZibalAPI.Verify(zpi.TrackId, _appConfig.ZibalMerchantCode);
            zpi.Result = BuildZibalStatusText(inquiry, verify);
            await _userDbContext.SaveChangesAsync(cancellationToken);

            Console.WriteLine(
                $"[Zibal] verify paymentId={zpi.Id}, trackId={zpi.TrackId}, result={verify?.Result}, status={verify?.Status}, message={verify?.Message}");

            if (verify != null && (verify.Result == 100 || verify.Result == 201))
            {
                var credUser = await _credentialsDbContext.GetUserStatus(new CredUser { TelegramUserId = zpi.TelegramUserId });
                zpi = ZibalAPI.MarkAsPaid(zpi, inquiry);
                await ZibalAddtoBalance(zpi, _appConfig, credUser, chatId, false);
                await AnswerCallbackSafely(callbackQuery, cancellationToken);
                return;
            }

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildZibalUserMessage(zpi.TrackId, inquiry, verify),
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (inquiry.Status == 1)
        {
            var credUser = await _credentialsDbContext.GetUserStatus(new CredUser { TelegramUserId = zpi.TelegramUserId });
            zpi = ZibalAPI.MarkAsPaid(zpi, inquiry);
            await ZibalAddtoBalance(zpi, _appConfig, credUser, chatId, false);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (inquiry.Status == -1)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"نشست شماره `{zpi.TrackId}` هنوز در انتظار پرداخت است.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        zpi.IsExpired = IsFinalUnsuccessfulZibalStatus(inquiry.Status);
        await _userDbContext.SaveChangesAsync(cancellationToken);

        await _userActivityLog.LogWarningAsync(
            "zibal_payment_not_successful",
            await _credentialsDbContext.GetUserStatus(new CredUser { TelegramUserId = zpi.TelegramUserId }),
            false,
            new Dictionary<string, object>
            {
                ["paymentId"] = zpi.Id,
                ["trackId"] = zpi.TrackId,
                ["status"] = inquiry.Status,
                ["result"] = inquiry.Result,
                ["message"] = inquiry.Message ?? string.Empty
            },
            cancellationToken);

        await ActiveBotClient.CustomSendTextMessageAsync(
            chatId: chatId,
            text: BuildZibalUserMessage(zpi.TrackId, inquiry, null),
            replyMarkup: MainReplyMarkupKeyboardFa(),
            cancellationToken: cancellationToken);

        await AnswerCallbackSafely(callbackQuery, cancellationToken);
    }

    private async Task ProcessCryptoPaymentCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        var messageId = callbackQuery.Message?.MessageId ?? 0;
        var orderId = callbackQuery.Data.Replace("check_crypto_payment_", "");
        Console.WriteLine($"[NOWPayments ManualCheck] start user={callbackQuery.From.Id}, chat={chatId}, orderId={orderId}");

        var payment = await _userDbContext.SwapinoPaymentInfos.FirstOrDefaultAsync(
            p => p.OrderId == orderId,
            cancellationToken);
        if (payment == null)
        {
            Console.WriteLine($"[NOWPayments ManualCheck] payment not found. orderId={orderId}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت مورد نظر پیدا نشد.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (payment.IsAddedToBallance)
        {
            Console.WriteLine($"[NOWPayments ManualCheck] already added. orderId={payment.OrderId}, paymentId={payment.PaymentId}, user={payment.TelegramUserId}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است و دوباره شارژ نمی‌شود.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var data = payment.GetNowPaymentsData();
        NowPaymentsPaymentStatusResult status;
        try
        {
            var paymentId = payment.PaymentId ?? data.PaymentId;
            if (!string.IsNullOrWhiteSpace(paymentId))
            {
                status = await _nowPayments.GetPaymentStatusAsync(paymentId, cancellationToken);
                Console.WriteLine(
                    $"[NOWPayments ManualCheck] remote status received by payment_id. orderId={payment.OrderId}, paymentId={paymentId}, status={status?.payment_status}, actuallyPaid={status?.actually_paid}, payAmount={status?.pay_amount}, payCurrency={status?.pay_currency}");
            }
            else
            {
                var invoiceId = payment.InvoiceId ?? data.InvoiceId;
                Console.WriteLine(
                    $"[NOWPayments ManualCheck] payment_id missing; searching NOWPayments by invoice/order. orderId={payment.OrderId}, invoiceId={invoiceId}, invoiceUrl={payment.InvoiceUrl ?? data.InvoiceUrl}");

                status = await _nowPayments.FindPaymentStatusByInvoiceOrOrderAsync(
                    invoiceId,
                    payment.OrderId ?? data.OrderId,
                    cancellationToken);

                if (status == null)
                {
                    Console.WriteLine($"[NOWPayments ManualCheck] no remote payment found for invoice/order. orderId={payment.OrderId}, invoiceId={invoiceId}");
                    await ActiveBotClient.CustomSendTextMessageAsync(
                        chatId: chatId,
                        text: "هنوز پرداختی برای این فاکتور در لیست پرداخت‌های NOWPayments پیدا نشد.\n" +
                              "اگر پرداخت را تازه انجام داده‌اید چند دقیقه بعد دوباره بررسی کنید.\n\n" +
                              $"<a href=\"{System.Net.WebUtility.HtmlEncode(payment.InvoiceUrl ?? data.InvoiceUrl ?? "")}\">باز کردن فاکتور</a>",
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);
                    await AnswerCallbackSafely(callbackQuery, cancellationToken);
                    return;
                }

                Console.WriteLine(
                    $"[NOWPayments ManualCheck] remote status received by invoice/order. orderId={payment.OrderId}, paymentId={status.payment_id}, invoiceId={status.invoice_id}, status={status.payment_status}, actuallyPaid={status.actually_paid}, payAmount={status.pay_amount}, payCurrency={status.pay_currency}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NOWPayments ManualCheck] remote status failed. orderId={payment.OrderId}, paymentId={payment.PaymentId ?? data.PaymentId}, invoiceId={payment.InvoiceId ?? data.InvoiceId}, error={ex.Message}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"خطا در بررسی وضعیت پرداخت: <code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        data.Apply(status);
        payment.SetNowPaymentsData(data);
        payment.OutcomeAmount = data.PayAmount == 0 ? payment.OutcomeAmount : data.PayAmount;
        payment.BaseAmount = data.PriceAmount == 0 ? payment.BaseAmount : data.PriceAmount;
        await _userDbContext.SaveChangesAsync(cancellationToken);
        Console.WriteLine($"[NOWPayments ManualCheck] local payment updated. orderId={payment.OrderId}, paymentId={payment.PaymentId}, status={payment.PaymentStatus}, added={payment.IsAddedToBalance}");

        if (NowPaymentsStatuses.IsPaid(data.PaymentStatus))
        {
            var settlement = await _nowPaymentsSettlementService.ApplyFinishedPaymentAsync(
                payment,
                "manual-check",
                chatId,
                cancellationToken);
            Console.WriteLine($"[NOWPayments ManualCheck] settlement result. orderId={payment.OrderId}, paymentId={payment.PaymentId}, settlement={settlement.Status}, before={settlement.BeforeBalance}, after={settlement.AfterBalance}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildCryptoManualCheckSettlementText(payment, data, settlement),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);
        }
        else if (NowPaymentsStatuses.IsPartiallyPaid(data.PaymentStatus))
        {
            var partialAmountToman = CalculatePartialCryptoChargeToman(payment, data);
            var text = BuildPartialCryptoPaymentText(payment, data, partialAmountToman);
            var replyMarkup = BuildPartialCryptoPaymentKeyboard(payment);
            Console.WriteLine($"[NOWPayments ManualCheck] partial payment. orderId={payment.OrderId}, paymentId={payment.PaymentId}, partialAmountToman={partialAmountToman}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        else
        {
            var text = $"وضعیت فعلی پرداخت: <code>{System.Net.WebUtility.HtmlEncode(data.PaymentStatus ?? "unknown")}</code>\n" +
                       "بعد از تایید شبکه، موجودی شما به صورت خودکار شارژ می‌شود.";

            if (NowPaymentsStatuses.IsFinalFailure(data.PaymentStatus))
                text = $"این پرداخت با وضعیت <code>{System.Net.WebUtility.HtmlEncode(data.PaymentStatus)}</code> بسته شده و قابل شارژ نیست.";

            Console.WriteLine($"[NOWPayments ManualCheck] not settled. orderId={payment.OrderId}, paymentId={payment.PaymentId}, status={data.PaymentStatus}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

        await AnswerCallbackSafely(callbackQuery, cancellationToken);
    }

    private async Task ProcessHooshPayPaymentCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        var messageId = callbackQuery.Message?.MessageId ?? 0;
        var lookupValue = callbackQuery.Data.StartsWith("hpchk_", StringComparison.Ordinal)
            ? callbackQuery.Data.Replace("hpchk_", "")
            : callbackQuery.Data.Replace("check_hooshpay_payment_", "");
        Console.WriteLine($"[HooshPay ManualCheck] start user={callbackQuery.From.Id}, chat={chatId}, lookup={lookupValue}");

        HooshPayPaymentInfo payment = null;
        if (int.TryParse(lookupValue, out var paymentId))
        {
            payment = await _userDbContext.HooshPayPaymentInfos.FindAsync(new object[] { paymentId }, cancellationToken);
        }

        payment ??= await _userDbContext.HooshPayPaymentInfos.FirstOrDefaultAsync(
            p => p.OrderId == lookupValue,
            cancellationToken);
        if (payment == null)
        {
            Console.WriteLine($"[HooshPay ManualCheck] payment not found. lookup={lookupValue}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت مورد نظر پیدا نشد.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var needsProvisionalProviderReconciliation = payment.IsProvisionallyApproved &&
                                                   !payment.ProviderConfirmedAfterProvisionalAtUtc.HasValue;
        if (payment.IsAddedToBalance && !needsProvisionalProviderReconciliation)
        {
            Console.WriteLine($"[HooshPay ManualCheck] already added. orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, user={payment.TelegramUserId}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است و دوباره شارژ نمی‌شود.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(payment.InvoiceUid))
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "شناسه فاکتور HooshPay برای این پرداخت ثبت نشده است.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        HooshPayVerifyResponse verify;
        try
        {
            var invoice = await _hooshPay.GetInvoiceAsync(payment.InvoiceUid, cancellationToken);
            payment.Apply(invoice?.data);
            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);

            verify = await _hooshPay.VerifyInvoiceAsync(payment.InvoiceUid, cancellationToken);
            payment.Apply(verify?.data);
            if (!string.IsNullOrWhiteSpace(verify?.status))
                payment.PaymentStatus = verify.status;
            payment.RawResponseJson = JsonConvert.SerializeObject(new
            {
                invoice,
                verify
            });

            await _userDbContext.SaveChangesAsync(cancellationToken);
            Console.WriteLine($"[HooshPay ManualCheck] remote status received. orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, status={payment.PaymentStatus}, paid={verify?.paid}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HooshPay ManualCheck] remote status failed. orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, error={ex.Message}");
            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"خطا در بررسی وضعیت پرداخت: <code>{Html(ex.Message)}</code>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (verify?.paid == true || HooshPayStatuses.IsPaid(payment.PaymentStatus))
        {
            payment.PaymentStatus = HooshPayStatuses.Paid;
            var isTenantOrder = string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase);
            if (!isTenantOrder)
                await _hooshPaySettlementService.RecordProviderConfirmationAfterProvisionalAsync(
                    payment,
                    "manual-check",
                    cancellationToken);

            var settlement = isTenantOrder
                ? await _tenantBotService.ApplyPaidTenantOrderAsync(
                    payment,
                    "manual-check",
                    cancellationToken)
                : await _hooshPaySettlementService.ApplyFinishedPaymentAsync(
                    payment,
                    "manual-check",
                    chatId,
                    cancellationToken);

            Console.WriteLine($"[HooshPay ManualCheck] settlement result. orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, settlement={settlement.Status}, before={settlement.BeforeBalance}, after={settlement.AfterBalance}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildHooshPayManualCheckSettlementText(payment, settlement),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);
        }
        else
        {
            var text = needsProvisionalProviderReconciliation
                ? $"اعتبار این پرداخت قبلاً به صورت موقت شارژ شده است. وضعیت فعلی HooshPay: <code>{Html(payment.PaymentStatus ?? "unknown")}</code>\n" +
                  "پس از تایید رسمی درگاه، فقط ثبت reconciliation و لاگ انجام می‌شود."
                : $"وضعیت فعلی پرداخت: <code>{Html(payment.PaymentStatus ?? "unknown")}</code>\n" +
                  "بعد از تایید پرداخت، موجودی شما از طریق IPN یا بررسی دستی شارژ می‌شود.";

            if (HooshPayStatuses.IsFinalFailure(payment.PaymentStatus))
                text = $"این پرداخت با وضعیت <code>{Html(payment.PaymentStatus)}</code> بسته شده و قابل شارژ نیست.";

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

        await AnswerCallbackSafely(callbackQuery, cancellationToken);
    }

    private static string BuildHooshPayManualCheckSettlementText(
        HooshPayPaymentInfo payment,
        NowPaymentsSettlementResult settlement)
    {
        var status = Html(payment?.PaymentStatus ?? "unknown");
        var orderId = Html(payment?.OrderId ?? "");
        var invoiceUid = Html(payment?.InvoiceUid ?? "");
        var amount = Html((payment?.AmountToman ?? 0).FormatCurrency());
        var payableAmount = Html((payment?.PayableAmountToman ?? 0).FormatCurrency());

        if (settlement?.Status == NowPaymentsSettlementStatus.Applied)
        {
            return "✅ پرداخت شما با موفقیت تایید شد و کیف پول شارژ شد.\n\n" +
                   $"📌 وضعیت: <code>{status}</code>\n" +
                   $"🧾 Order ID: <code>{orderId}</code>\n" +
                   $"🧾 Invoice UID: <code>{invoiceUid}</code>\n" +
                   $"💰 مبلغ شارژ: <code>{amount}</code>\n" +
                   $"💳 مبلغ پرداختی: <code>{payableAmount}</code>\n" +
                   $"💳 موجودی قبل: <code>{Html(settlement.BeforeBalance.FormatCurrency())}</code>\n" +
                   $"💳 موجودی بعد: <code>{Html(settlement.AfterBalance.FormatCurrency())}</code>";
        }

        if (settlement?.Status == NowPaymentsSettlementStatus.AlreadyAdded)
        {
            return "ℹ️ اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است.\n\n" +
                   $"📌 وضعیت: <code>{status}</code>\n" +
                   $"🧾 Order ID: <code>{orderId}</code>\n" +
                   $"🧾 Invoice UID: <code>{invoiceUid}</code>\n" +
                   $"💳 موجودی فعلی: <code>{Html(settlement.AfterBalance.FormatCurrency())}</code>";
        }

        if (settlement?.Status == NowPaymentsSettlementStatus.UserNotFound)
            return "پرداخت در سمت HooshPay تایید شده، اما کاربر مربوط به این پرداخت در دیتابیس پیدا نشد.";

        return $"پرداخت تایید شد، اما شارژ کیف پول انجام نشد. وضعیت تسویه: <code>{Html(settlement?.Status.ToString() ?? "unknown")}</code>";
    }

    private static string BuildCryptoManualCheckSettlementText(
        SwapinoPaymentInfo payment,
        NowPaymentsPaymentRecordData data,
        NowPaymentsSettlementResult settlement)
    {
        var status = Html(data?.PaymentStatus ?? payment?.PaymentStatus ?? "unknown");
        var orderId = Html(payment?.OrderId ?? "");
        var paymentId = Html(payment?.PaymentId ?? data?.PaymentId ?? "");
        var amount = Html((payment?.AmountToman ?? 0).FormatCurrency());

        if (settlement?.Status == NowPaymentsSettlementStatus.Applied)
        {
            return "✅ پرداخت شما با موفقیت تایید شد و کیف پول شارژ شد.\n\n" +
                   $"📌 وضعیت: <code>{status}</code>\n" +
                   $"🧾 Order ID: <code>{orderId}</code>\n" +
                   $"🧾 Payment ID: <code>{paymentId}</code>\n" +
                   $"💰 مبلغ شارژ: <code>{amount}</code>\n" +
                   $"💳 موجودی قبل: <code>{Html(settlement.BeforeBalance.FormatCurrency())}</code>\n" +
                   $"💳 موجودی بعد: <code>{Html(settlement.AfterBalance.FormatCurrency())}</code>";
        }

        if (settlement?.Status == NowPaymentsSettlementStatus.AlreadyAdded)
        {
            return "ℹ️ اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است.\n\n" +
                   $"📌 وضعیت: <code>{status}</code>\n" +
                   $"🧾 Order ID: <code>{orderId}</code>\n" +
                   $"🧾 Payment ID: <code>{paymentId}</code>\n" +
                   $"💳 موجودی فعلی: <code>{Html(settlement.AfterBalance.FormatCurrency())}</code>";
        }

        if (settlement?.Status == NowPaymentsSettlementStatus.UserNotFound)
        {
            return "پرداخت در سمت NOWPayments تایید شده، اما کاربر مربوط به این پرداخت در دیتابیس پیدا نشد.\n" +
                   $"🧾 Order ID: <code>{orderId}</code>";
        }

        return "پرداخت در سمت NOWPayments تایید شده، اما شارژ کیف پول کامل نشد.\n\n" +
               $"📌 وضعیت پرداخت: <code>{status}</code>\n" +
               $"📌 نتیجه شارژ: <code>{Html(settlement?.Status.ToString() ?? "unknown")}</code>\n" +
               $"🧾 Order ID: <code>{orderId}</code>";
    }

    private async Task<bool> TryHandleNowPaymentsReturnStartAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (message?.Text == null)
            return false;

        var isSuccess = IsNowPaymentsSuccessStart(message.Text);
        var isCancel = IsNowPaymentsCancelStart(message.Text);
        if (!isSuccess && !isCancel)
            return false;

        var telegramUserId = message.From?.Id ?? credUser?.TelegramUserId ?? 0;
        Console.WriteLine($"[NOWPayments ReturnUrl] received start. user={telegramUserId}, chat={message.Chat.Id}, text={message.Text}, success={isSuccess}, cancel={isCancel}");

        var payment = await _userDbContext.SwapinoPaymentInfos
            .Where(p => p.TelegramUserId == telegramUserId &&
                        !p.IsAddedToBalance &&
                        (p.BotId == BotContextAccessor.CurrentBotId || string.IsNullOrWhiteSpace(p.BotId)))
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (payment == null)
        {
            Console.WriteLine($"[NOWPayments ReturnUrl] no pending crypto payment found. user={telegramUserId}");
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: isCancel
                    ? "پرداخت در حال انتظاری برای بستن پیدا نشد. اگر قبلاً کنسل شده باشد، نیازی به اقدام دوباره نیست."
                    : "پرداخت در حال انتظاری برای حساب شما پیدا نشد. اگر کیف پول قبلاً شارژ شده، نیازی به اقدام دوباره نیست.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (isCancel)
        {
            payment.PaymentStatus = "cancelled_by_user";
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "پرداخت توسط کاربر کنسل شد و فاکتور مربوطه در درگاه پرداخت به صورت بسته شده درآمد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "پرداخت از سمت درگاه پرداخت تایید شد.\nدر حال بررسی وضعیت پرداخت از NOWPayments هستم...",
            cancellationToken: cancellationToken);

        await CheckAndSettleCryptoPaymentCoreAsync(
            payment,
            message.Chat.Id,
            0,
            telegramUserId,
            "success-url",
            mainReplyMarkup,
            cancellationToken);

        return true;
    }

    private static bool IsNowPaymentsSuccessStart(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        return normalized.Equals("/start payment_success", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("/start=payment_success", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("payment_success", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/start payment_success", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNowPaymentsCancelStart(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        return normalized.Equals("/start payment_cancel", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("/start=payment_cancel", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("payment_cancel", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/start payment_cancel", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CheckAndSettleCryptoPaymentCoreAsync(
        SwapinoPaymentInfo payment,
        long chatId,
        int messageId,
        long actorUserId,
        string source,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (payment.IsAddedToBalance)
        {
            Console.WriteLine($"[NOWPayments ManualCheck] already added. source={source}, actor={actorUserId}, orderId={payment.OrderId}, paymentId={payment.PaymentId}, user={payment.TelegramUserId}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است و دوباره شارژ نمی‌شود.",
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);

            return;
        }

        var data = payment.GetNowPaymentsData();
        NowPaymentsPaymentStatusResult status;
        try
        {
            var paymentId = payment.PaymentId ?? data.PaymentId;
            if (!string.IsNullOrWhiteSpace(paymentId))
            {
                status = await _nowPayments.GetPaymentStatusAsync(paymentId, cancellationToken);
                Console.WriteLine(
                    $"[NOWPayments ManualCheck] remote status received by payment_id. source={source}, orderId={payment.OrderId}, paymentId={paymentId}, status={status?.payment_status}, actuallyPaid={status?.actually_paid}, payAmount={status?.pay_amount}, payCurrency={status?.pay_currency}");
            }
            else
            {
                var invoiceId = payment.InvoiceId ?? data.InvoiceId;
                Console.WriteLine(
                    $"[NOWPayments ManualCheck] payment_id missing; searching NOWPayments by invoice/order. source={source}, orderId={payment.OrderId}, invoiceId={invoiceId}, invoiceUrl={payment.InvoiceUrl ?? data.InvoiceUrl}");

                status = await _nowPayments.FindPaymentStatusByInvoiceOrOrderAsync(
                    invoiceId,
                    payment.OrderId ?? data.OrderId,
                    cancellationToken);

                if (status == null)
                {
                    Console.WriteLine($"[NOWPayments ManualCheck] no remote payment found for invoice/order. source={source}, orderId={payment.OrderId}, invoiceId={invoiceId}");
                    await ActiveBotClient.CustomSendTextMessageAsync(
                        chatId: chatId,
                        text: "هنوز پرداختی برای این فاکتور در لیست پرداخت‌های NOWPayments پیدا نشد.\n" +
                              "اگر پرداخت را تازه انجام داده‌اید چند دقیقه بعد دوباره بررسی کنید.\n\n" +
                              $"<a href=\"{System.Net.WebUtility.HtmlEncode(payment.InvoiceUrl ?? data.InvoiceUrl ?? "")}\">باز کردن فاکتور</a>",
                        parseMode: ParseMode.Html,
                        replyMarkup: mainReplyMarkup,
                        cancellationToken: cancellationToken);
                    return;
                }

                Console.WriteLine(
                    $"[NOWPayments ManualCheck] remote status received by invoice/order. source={source}, orderId={payment.OrderId}, paymentId={status.payment_id}, invoiceId={status.invoice_id}, status={status.payment_status}, actuallyPaid={status.actually_paid}, payAmount={status.pay_amount}, payCurrency={status.pay_currency}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NOWPayments ManualCheck] remote status failed. source={source}, orderId={payment.OrderId}, paymentId={payment.PaymentId ?? data.PaymentId}, invoiceId={payment.InvoiceId ?? data.InvoiceId}, error={ex.Message}");
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"خطا در بررسی وضعیت پرداخت: <code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code>",
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        data.Apply(status);
        payment.SetNowPaymentsData(data);
        payment.OutcomeAmount = data.PayAmount == 0 ? payment.OutcomeAmount : data.PayAmount;
        payment.BaseAmount = data.PriceAmount == 0 ? payment.BaseAmount : data.PriceAmount;
        await _userDbContext.SaveChangesAsync(cancellationToken);
        Console.WriteLine($"[NOWPayments ManualCheck] local payment updated. source={source}, orderId={payment.OrderId}, paymentId={payment.PaymentId}, status={payment.PaymentStatus}, added={payment.IsAddedToBalance}");

        if (NowPaymentsStatuses.IsPaid(data.PaymentStatus))
        {
            var settlement = await _nowPaymentsSettlementService.ApplyFinishedPaymentAsync(
                payment,
                source,
                chatId,
                cancellationToken);
            Console.WriteLine($"[NOWPayments ManualCheck] settlement result. source={source}, orderId={payment.OrderId}, paymentId={payment.PaymentId}, settlement={settlement.Status}, before={settlement.BeforeBalance}, after={settlement.AfterBalance}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildCryptoManualCheckSettlementText(payment, data, settlement),
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);
        }
        else if (NowPaymentsStatuses.IsPartiallyPaid(data.PaymentStatus))
        {
            var partialAmountToman = CalculatePartialCryptoChargeToman(payment, data);
            var text = BuildPartialCryptoPaymentText(payment, data, partialAmountToman);
            var replyMarkup = BuildPartialCryptoPaymentKeyboard(payment);
            Console.WriteLine($"[NOWPayments ManualCheck] partial payment. source={source}, orderId={payment.OrderId}, paymentId={payment.PaymentId}, partialAmountToman={partialAmountToman}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        else
        {
            var text = $"وضعیت فعلی پرداخت: <code>{System.Net.WebUtility.HtmlEncode(data.PaymentStatus ?? "unknown")}</code>\n" +
                       "بعد از تایید شبکه، موجودی شما به صورت خودکار شارژ می‌شود.";

            if (NowPaymentsStatuses.IsFinalFailure(data.PaymentStatus))
                text = $"این پرداخت با وضعیت <code>{System.Net.WebUtility.HtmlEncode(data.PaymentStatus)}</code> بسته شده و قابل شارژ نیست.";

            Console.WriteLine($"[NOWPayments ManualCheck] not settled. source={source}, orderId={payment.OrderId}, paymentId={payment.PaymentId}, status={data.PaymentStatus}");

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessCryptoPartialSettlementCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        var messageId = callbackQuery.Message?.MessageId ?? 0;
        var idText = callbackQuery.Data.Replace("settle_crypto_partial_", "");

        if (!int.TryParse(idText, out var paymentDbId))
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "شناسه پرداخت ناقص معتبر نیست.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var payment = await _userDbContext.SwapinoPaymentInfos.FirstOrDefaultAsync(
            p => p.Id == paymentDbId,
            cancellationToken);

        if (payment == null)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت مورد نظر پیدا نشد.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (payment.IsAddedToBalance)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var data = payment.GetNowPaymentsData();
        if (string.IsNullOrWhiteSpace(payment.PaymentId ?? data.PaymentId))
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "شناسه پرداخت NOWPayments هنوز ثبت نشده است. کمی بعد دوباره بررسی کنید.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        try
        {
            var status = await _nowPayments.GetPaymentStatusAsync(payment.PaymentId ?? data.PaymentId, cancellationToken);
            data.Apply(status);
            payment.SetNowPaymentsData(data);
            payment.OutcomeAmount = data.PayAmount == 0 ? payment.OutcomeAmount : data.PayAmount;
            payment.BaseAmount = data.PriceAmount == 0 ? payment.BaseAmount : data.PriceAmount;
            await _userDbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"خطا در بررسی مجدد پرداخت: <code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (NowPaymentsStatuses.IsPaid(data.PaymentStatus))
        {
            await _nowPaymentsSettlementService.ApplyFinishedPaymentAsync(
                payment,
                "manual-check-after-partial",
                chatId,
                cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (!NowPaymentsStatuses.IsPartiallyPaid(data.PaymentStatus))
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"این پرداخت در وضعیت <code>{System.Net.WebUtility.HtmlEncode(data.PaymentStatus ?? "unknown")}</code> است و فعلاً قابل شارژ نسبی نیست.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var partialAmountToman = CalculatePartialCryptoChargeToman(payment, data);
        if (partialAmountToman <= 0)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "مقدار پرداخت‌شده برای شارژ نسبی قابل محاسبه نیست. لطفاً ادامه مبلغ را پرداخت کنید یا با پشتیبانی تماس بگیرید.",
                parseMode: ParseMode.Html,
                replyMarkup: BuildPartialCryptoPaymentKeyboard(payment),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var settlement = await _nowPaymentsSettlementService.ApplyPartialPaymentAsync(
            payment,
            partialAmountToman,
            "manual-partial-check",
            chatId,
            cancellationToken);

        if (settlement.Status == NowPaymentsSettlementStatus.Applied)
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"پرداخت ناقص به اندازه <code>{partialAmountToman.FormatCurrency()}</code> به کیف پول شما اضافه شد.",
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(ActiveBotClient, chatId, messageId);
        }
        else
        {
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"شارژ نسبی انجام نشد. وضعیت: <code>{settlement.Status}</code>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

        await AnswerCallbackSafely(callbackQuery, cancellationToken);
    }

    private static string BuildPartialCryptoPaymentText(
        SwapinoPaymentInfo payment,
        NowPaymentsPaymentRecordData data,
        long partialAmountToman)
    {
        var expectedPayAmount = GetExpectedCryptoPayAmount(payment, data);
        var actuallyPaid = GetActuallyPaidCryptoAmount(payment, data);
        var remaining = expectedPayAmount > actuallyPaid
            ? expectedPayAmount - actuallyPaid
            : 0;
        var payCurrency = payment.PayCurrency ?? data.PayCurrency ?? "crypto";
        var baseCurrency = payment.BaseCurrency ?? data.PriceCurrency ?? "usdtbsc";
        var baseAmount = payment.BaseAmount == 0 ? data.PriceAmount : payment.BaseAmount;

        var builder = new StringBuilder();
        builder.AppendLine("⚠️ <b>پرداخت شما ناقص ثبت شده است.</b>");
        builder.AppendLine();
        builder.AppendLine($"🧾 سفارش: <code>{Html(payment.OrderId)}</code>");
        builder.AppendLine($"📌 وضعیت: <code>{Html(data.PaymentStatus ?? payment.PaymentStatus ?? "partially_paid")}</code>");
        builder.AppendLine($"💵 مبلغ سفارش: <code>{Html(FormatCryptoDecimal(baseAmount))} {Html(baseCurrency)}</code>");
        builder.AppendLine($"🪙 مبلغ مورد انتظار: <code>{Html(FormatCryptoDecimal(expectedPayAmount))} {Html(payCurrency)}</code>");
        builder.AppendLine($"✅ پرداخت‌شده: <code>{Html(FormatCryptoDecimal(actuallyPaid))} {Html(payCurrency)}</code>");

        if (remaining > 0)
            builder.AppendLine($"⏳ باقی‌مانده: <code>{Html(FormatCryptoDecimal(remaining))} {Html(payCurrency)}</code>");

        builder.AppendLine();
        if (partialAmountToman > 0)
        {
            builder.AppendLine($"💰 معادل قابل شارژ فعلی: <code>{Html(partialAmountToman.FormatCurrency())}</code>");
            builder.AppendLine();
            builder.AppendLine("می‌توانید باقی مبلغ را از همان فاکتور پرداخت کنید، یا فقط همین مقدار پرداخت‌شده را به کیف پول خود اضافه کنید.");
        }
        else
        {
            builder.AppendLine("مقدار پرداخت‌شده هنوز برای شارژ نسبی قابل محاسبه نیست. لطفاً کمی بعد دوباره بررسی کنید یا ادامه مبلغ را پرداخت کنید.");
        }

        return builder.ToString();
    }

    private static InlineKeyboardMarkup BuildPartialCryptoPaymentKeyboard(SwapinoPaymentInfo payment)
    {
        var rows = new List<InlineKeyboardButton[]>();
        var invoiceUrl = payment.InvoiceUrl ?? payment.GetNowPaymentsData().InvoiceUrl;
        if (!string.IsNullOrWhiteSpace(invoiceUrl))
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithUrl("پرداخت باقی‌مانده", invoiceUrl)
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("شارژ به اندازه پرداختی", $"settle_crypto_partial_{payment.Id}")
        });

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("بررسی مجدد", $"check_crypto_payment_{payment.OrderId}")
        });

        return new InlineKeyboardMarkup(rows);
    }

    private static long CalculatePartialCryptoChargeToman(SwapinoPaymentInfo payment, NowPaymentsPaymentRecordData data)
    {
        if (payment == null || payment.AmountToman <= 0 || data == null)
            return 0;

        var expectedPayAmount = GetExpectedCryptoPayAmount(payment, data);
        var actuallyPaid = GetActuallyPaidCryptoAmount(payment, data);

        if (expectedPayAmount <= 0 || actuallyPaid <= 0)
        {
            var baseAmount = payment.BaseAmount == 0 ? data.PriceAmount : payment.BaseAmount;
            var actuallyPaidAtFiat = payment.ActuallyPaidAtFiat == 0 ? data.ActuallyPaidAtFiat : payment.ActuallyPaidAtFiat;
            if (baseAmount <= 0 || actuallyPaidAtFiat <= 0)
                return 0;

            return ClampPartialTomanAmount(payment.AmountToman, actuallyPaidAtFiat / baseAmount);
        }

        return ClampPartialTomanAmount(payment.AmountToman, actuallyPaid / expectedPayAmount);
    }

    private static decimal GetExpectedCryptoPayAmount(SwapinoPaymentInfo payment, NowPaymentsPaymentRecordData data)
    {
        if (data?.PayAmount > 0)
            return data.PayAmount;

        if (payment?.OutcomeAmount > 0)
            return payment.OutcomeAmount;

        return 0;
    }

    private static decimal GetActuallyPaidCryptoAmount(SwapinoPaymentInfo payment, NowPaymentsPaymentRecordData data)
    {
        if (data?.ActuallyPaid > 0)
            return data.ActuallyPaid;

        if (data?.AmountReceived > 0)
            return data.AmountReceived;

        if (payment?.ActuallyPaid > 0)
            return payment.ActuallyPaid;

        return 0;
    }

    private static long ClampPartialTomanAmount(long originalAmountToman, decimal ratio)
    {
        if (ratio <= 0)
            return 0;

        if (ratio > 1)
            ratio = 1;

        return (long)Math.Floor(originalAmountToman * ratio);
    }

    private static string FormatCryptoDecimal(decimal value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private async Task AnswerCallbackSafely(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        try
        {
            await ActiveBotClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            Console.WriteLine("Bad Request: query is too old and response timeout expired or query ID is invalid");
        }
    }

    private static string BuildZibalStatusText(InquiryResponse inquiry, PaymentVerificationResponse verify = null)
    {
        var builder = new StringBuilder();
        builder.Append($"inquiry_status={inquiry?.Status}");
        builder.Append($", inquiry_result={inquiry?.Result}");
        builder.Append($", inquiry_message={inquiry?.Message ?? string.Empty}");

        if (verify != null)
        {
            builder.Append($", verify_status={verify.Status}");
            builder.Append($", verify_result={verify.Result}");
            builder.Append($", verify_message={verify.Message ?? string.Empty}");
            builder.Append($", ref_number={verify.RefNumber ?? string.Empty}");
        }

        return builder.ToString();
    }

    private static string BuildZibalUserMessage(long trackId, InquiryResponse inquiry, PaymentVerificationResponse verify)
    {
        if (inquiry == null)
            return "امکان بررسی وضعیت پرداخت وجود ندارد. لطفاً کمی بعد دوباره تلاش کنید.";

        if (inquiry.Status == -1)
            return $"نشست شماره `{trackId}` هنوز در انتظار پرداخت است.";

        if (inquiry.Status == 2 && verify != null)
        {
            if (verify.Result == 100 || verify.Result == 201)
                return "پرداخت با موفقیت تایید شد و اعتبار به حساب شما اضافه شد.";

            return $"پرداخت انجام شده، اما تایید نهایی زیبال موفق نبود.\nکد پیگیری: `{trackId}`\nکد پاسخ: `{verify.Result}`\nلطفاً چند دقیقه بعد دوباره بررسی کنید یا با پشتیبانی تماس بگیرید.";
        }

        var statusText = inquiry.Status switch
        {
            3 => "پرداخت لغو شده است.",
            4 => "پرداخت ناموفق بوده است.",
            5 => "نشست پرداخت منقضی شده است.",
            _ => "پرداخت در وضعیت موفق قرار ندارد."
        };

        return $"{statusText}\nکد پیگیری: `{trackId}`\nکد وضعیت زیبال: `{inquiry.Status}`";
    }

    private static bool IsFinalUnsuccessfulZibalStatus(int status)
    {
        return status == 3 || status == 4 || status == 5;
    }

    /// <summary>
    /// Applies one provider-verified Zibal wallet charge using its persisted payment guard, records its ledger, and processes owned referral rewards.
    /// </summary>
    /// <param name="zpi">Local Zibal row containing final provider status, track id, wallet amount in rial, and originating bot.</param>
    /// <param name="appConfig">Application configuration retained for legacy call-site compatibility.</param>
    /// <param name="credUser">Legacy caller profile; the authoritative target user is reloaded by <paramref name="zpi"/> Telegram id.</param>
    /// <param name="chatid">Telegram chat id that receives the final wallet-credit confirmation.</param>
    /// <param name="isAdmin">Whether provider verification was initiated from an admin flow; this does not bypass final provider status.</param>
    /// <returns>A task that completes after wallet, ledger, referral, notification, and payment-message updates.</returns>
    /// <remarks>
    /// The amount is converted from rial to Iranian toman by dividing by ten. Only rows with <c>IsPaid=true</c>
    /// reach referral settlement. Repeated calls repair missing audit/referral work but never credit the wallet twice.
    /// </remarks>
    public async Task ZibalAddtoBalance(ZibalPaymentInfo zpi, AppConfig appConfig, CredUser credUser, long chatid, bool isAdmin)
    {
        if (zpi == null || !zpi.IsPaid)
            return;

        var findedUser = await _credentialsDbContext.GetUserStatusWithId(zpi.TelegramUserId);
        if (findedUser == null)
            return;

        var zibalProviderPaymentId = zpi.TrackId > 0
            ? zpi.TrackId.ToString(CultureInfo.InvariantCulture)
            : zpi.Id.ToString(CultureInfo.InvariantCulture);
        var zibalMutationKey = $"wallet-credit:zibal:{TenantBotPaymentPurposes.WalletCharge}:{zibalProviderPaymentId}";
        if (zpi.IsAddedToBallance)
        {
            await _walletLedgerService.RecordAsync(
                zpi.TelegramUserId,
                WalletLedgerDirections.Credit,
                zpi.Amount / 10,
                findedUser.AccountBalance - (zpi.Amount / 10),
                findedUser.AccountBalance,
                WalletLedgerReasons.WalletCharge,
                provider: "zibal",
                referenceType: nameof(ZibalPaymentInfo),
                referenceId: zpi.Id.ToString(CultureInfo.InvariantCulture),
                orderId: zibalProviderPaymentId,
                description: "Zibal wallet charge",
                botId: zpi.BotId,
                botUsername: zpi.BotUsername,
                botType: BotInstanceTypes.Owned,
                idempotencyKey: zibalMutationKey,
                cancellationToken: CancellationToken.None);
            if (zpi.IsPaid)
            {
                await _referralService.ProcessFinalOwnedWalletPaymentAsync(
                    new ReferralPaymentSource(
                        "zibal",
                        TenantBotPaymentPurposes.WalletCharge,
                        zibalProviderPaymentId,
                        zpi.BotId,
                        BotInstanceTypes.Owned,
                        zpi.TelegramUserId,
                        zpi.Amount / 10,
                        zpi.PaidAt == default ? zpi.CreatedAt : zpi.PaidAt,
                        true,
                        true,
                        false),
                    CancellationToken.None);
            }
            return;
        }

        var beforeBalance = findedUser.AccountBalance;
        var credited = await _credentialsDbContext.AddFund(
            zpi.TelegramUserId,
            zpi.Amount / 10);
        if (!credited)
            return;
        var afterBalance = checked(beforeBalance + (zpi.Amount / 10));

        zpi.IsAddedToBallance = true;

        await _userDbContext.SaveChangesAsync();
        await _walletLedgerService.RecordAsync(
            zpi.TelegramUserId,
            WalletLedgerDirections.Credit,
            zpi.Amount / 10,
            beforeBalance,
            afterBalance,
            WalletLedgerReasons.WalletCharge,
            provider: "zibal",
            referenceType: nameof(ZibalPaymentInfo),
            referenceId: zpi.Id.ToString(CultureInfo.InvariantCulture),
            orderId: zpi.TrackId.ToString(CultureInfo.InvariantCulture),
            description: isAdmin ? "Admin-confirmed Zibal wallet charge" : "Zibal wallet charge",
            botId: zpi.BotId,
            botUsername: zpi.BotUsername,
            botType: BotInstanceTypes.Owned,
            idempotencyKey: zibalMutationKey,
            cancellationToken: CancellationToken.None);

        if (zpi.IsPaid)
        {
            await _referralService.ProcessFinalOwnedWalletPaymentAsync(
                new ReferralPaymentSource(
                    "zibal",
                    TenantBotPaymentPurposes.WalletCharge,
                    zibalProviderPaymentId,
                    zpi.BotId,
                    BotInstanceTypes.Owned,
                    zpi.TelegramUserId,
                    zpi.Amount / 10,
                    zpi.PaidAt == default ? DateTime.UtcNow : zpi.PaidAt,
                    true,
                    true,
                    false),
                CancellationToken.None);
        }

        if (isAdmin)
        {
            // Provider verification can be initiated by an admin, but the user receives the same final-credit notice.
            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: findedUser.ChatID,
                text: $"اعتبار کیف پول شما به میزان {(zpi.Amount / 10).FormatCurrency()} افزایش یافت. با اسفتاده از این اعتبار میتوانید اکانت مورد نیاز خودرا تهیه بفرمایید.",
                replyMarkup: MainReplyMarkupKeyboardFa());
        }

        //notify user ( admin)
        await ActiveBotClient.CustomSendTextMessageAsync(
            chatId: chatid,
            text: $"اعتبار کیف پول شما به میزان {(zpi.Amount / 10).FormatCurrency()} افزایش یافت. با اسفتاده از این اعتبار میتوانید اکانت مورد نیاز خودرا تهیه بفرمایید.",
            replyMarkup: MainReplyMarkupKeyboardFa());

        var msg = await GetZipalPaymentMessage(credUser, true, zpi, $"https://gateway.zibal.ir/start/{zpi.TrackId}");

        var start = "درگاه پرداخت زیبال \n";
        var logMesseage = $"{start}یوزر <code>{zpi.TelegramUserId}</code> \n {credUser} \n به مبلغ {(zpi.Amount / 10).FormatCurrency()}" + " حساب کاربری خود را شارژ کرد." + $"\n موجودی قبل از شارژ {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از شارژ {afterBalance.FormatCurrency()} \n" + msg;

        if (isAdmin)
        {
            msg = await GetZipalPaymentMessage(findedUser, true, zpi, $"https://gateway.zibal.ir/start/{zpi.TrackId}");
            logMesseage = $"{start}یوزر <code>{zpi.TelegramUserId}</code> \n {findedUser} \n به مبلغ {(zpi.Amount / 10).FormatCurrency()}" + " حساب کاربری خود را شارژ کرد." + $"\n موجودی قبل از شارژ {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از شارژ {afterBalance.FormatCurrency()} \n" + msg;
        }
        // _logger.LogInformation(logMesseage.EscapeMarkdown());
        _logger.LogPayment(logMesseage);


        //change buttons!
        await EditMessageWithCallback(ActiveBotClient, zpi.ChatId, Convert.ToInt32(zpi.TelMsgId));

        return;
    }

    private ReplyKeyboardMarkup GetAdminConfirmationKeyboard()
    {

        var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                               {
            new []
            {
                new KeyboardButton("Yes Confirm!"),
            },
            new []
            {
                new KeyboardButton("No Don't Confirm!"),
            },

        });
        return confirmationKeyboard;
    }
    private CredUser GetCreduserFromMessage(Message message)
    {
        return GetCreduserFromTelegramUser(message.From, message.Chat.Id);
    }

    private CredUser GetCreduserFromTelegramUser(Telegram.Bot.Types.User telegramUser, long chatId)
    {
        return new CredUser
        {
            TelegramUserId = telegramUser.Id,
            ChatID = chatId,
            IsColleague = false,
            FirstName = telegramUser.FirstName ?? string.Empty,
            LastName = telegramUser.LastName ?? string.Empty,
            Username = telegramUser.Username ?? string.Empty,
            LanguageCode = telegramUser.LanguageCode ?? string.Empty
        };
    }

    private CredUser GetCreduserFromUpdate(Update update)
    {
        if (update?.Message?.From != null)
            return GetCreduserFromTelegramUser(update.Message.From, update.Message.Chat.Id);

        if (update?.CallbackQuery?.From != null)
            return GetCreduserFromTelegramUser(
                update.CallbackQuery.From,
                update.CallbackQuery.Message?.Chat.Id ?? update.CallbackQuery.From.Id);

        return null;
    }

    private bool IsSuperAdminUser(long telegramUserId)
    {
        return telegramUserId != 0 && _appConfig.AdminsUserIds?.Contains(telegramUserId) == true;
    }

    private async Task<bool> TryHandleUserActivityLogCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        CancellationToken cancellationToken)
    {
        if (message?.Text == null)
            return false;

        var text = message.Text.Trim();
        if (!text.StartsWith("/userlog", StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Equals("/userlog_on", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/userlog on", StringComparison.OrdinalIgnoreCase))
        {
            _userActivityLog.SetEnabled(true);
            await _userActivityLog.LogBotActionAsync(
                "user_activity_log_settings_changed",
                credUser,
                true,
                new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["level"] = _userActivityLog.CurrentLevel,
                    ["changedByCommand"] = text
                },
                cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"لاگ رفتار کاربران روشن شد.\nسطح فعلی: {_userActivityLog.CurrentLevel}\nفایل: {_userActivityLog.CurrentFilePath}",
                cancellationToken: cancellationToken);
            return true;
        }

        if (text.Equals("/userlog_off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/userlog off", StringComparison.OrdinalIgnoreCase))
        {
            await _userActivityLog.LogBotActionAsync(
                "user_activity_log_settings_changed",
                credUser,
                true,
                new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["level"] = _userActivityLog.CurrentLevel,
                    ["changedByCommand"] = text
                },
                cancellationToken);

            _userActivityLog.SetEnabled(false);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "لاگ رفتار کاربران خاموش شد. برای روشن کردن دوباره از /userlog_on استفاده کنید.",
                cancellationToken: cancellationToken);
            return true;
        }

        if (text.Equals("/userlog_status", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/userlog status", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/userlog", StringComparison.OrdinalIgnoreCase))
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"وضعیت لاگ رفتار کاربران:\nفعال: {_userActivityLog.IsEnabled}\nسطح: {_userActivityLog.CurrentLevel}\nفایل: {_userActivityLog.CurrentFilePath}\n\nفرمان‌ها:\n/userlog_on\n/userlog_off\n/userlog_level_error\n/userlog_level_warning\n/userlog_level_info\n/userlog_level_debug",
                cancellationToken: cancellationToken);
            return true;
        }

        var level = text.StartsWith("/userlog_level_", StringComparison.OrdinalIgnoreCase)
            ? text.Substring("/userlog_level_".Length)
            : text.StartsWith("/userlog level ", StringComparison.OrdinalIgnoreCase)
                ? text.Substring("/userlog level ".Length)
                : null;

        if (!string.IsNullOrWhiteSpace(level))
        {
            if (!_userActivityLog.TrySetLevel(level, out var normalizedLevel))
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "سطح لاگ معتبر نیست. یکی از این مقدارها را بفرستید: error, warning, info, debug",
                    cancellationToken: cancellationToken);
                return true;
            }

            await _userActivityLog.LogBotActionAsync(
                "user_activity_log_settings_changed",
                credUser,
                true,
                new Dictionary<string, object>
                {
                    ["enabled"] = _userActivityLog.IsEnabled,
                    ["level"] = normalizedLevel,
                    ["changedByCommand"] = text
                },
                cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"سطح لاگ روی {normalizedLevel} تنظیم شد.",
                cancellationToken: cancellationToken);
            return true;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "فرمان لاگ نامعتبر است. برای دیدن راهنما /userlog_status را بفرستید.",
            cancellationToken: cancellationToken);
        return true;
    }
    private ReplyKeyboardMarkup GetMessageSendConfirmationKeyboard()
    {

        var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                       {
            new []
            {
                new KeyboardButton("Yes Send!"),
            },
            new []
            {
                new KeyboardButton("Preview message"),
            },
            new []
            {
                new KeyboardButton("No Don't Send!"),
            },

        });
        return confirmationKeyboard;
    }

    private static InlineKeyboardMarkup BuildBroadcastAudienceKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("کاربران عادی", "broadcast_scope_customers"),
                InlineKeyboardButton.WithCallbackData("همکاران", "broadcast_scope_colleagues")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("کل کاربران", "broadcast_scope_all")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("برگشت به منوی قبل", "broadcast_scope_back")
            }
        });
    }

    private static string NormalizeBroadcastAudience(string audience)
    {
        return (audience ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            BroadcastAudienceCustomers => BroadcastAudienceCustomers,
            BroadcastAudienceColleagues => BroadcastAudienceColleagues,
            BroadcastAudienceAll => BroadcastAudienceAll,
            _ => BroadcastAudienceAll
        };
    }

    private static string GetBroadcastAudienceLabel(string audience)
    {
        return NormalizeBroadcastAudience(audience) switch
        {
            BroadcastAudienceCustomers => "کاربران عادی",
            BroadcastAudienceColleagues => "همکاران",
            _ => "کل کاربران"
        };
    }

    /// <summary>
    /// Builds the recipient list for an owned-bot public broadcast using the current bot scope.
    /// </summary>
    /// <param name="audience">
    /// Requested audience segment from the super-admin confirmation flow. Accepted values are
    /// <c>all</c>, <c>customers</c>, and <c>colleagues</c>; any other value is normalized to <c>all</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to cancel the two database reads when the Telegram update is stopped or times out.
    /// </param>
    /// <returns>
    /// Distinct Telegram chat ids that belong to users who have interacted with the current owned bot.
    /// The collection can be empty. Values are safe to pass directly to Telegram send/forward methods.
    /// </returns>
    /// <remarks>
    /// Broadcast isolation is based on <see cref="BotUserState.BotId"/> and
    /// <see cref="BotContextAccessor.CurrentBotId"/>. The shared <c>credentials.db</c> user table is
    /// intentionally not the audience source because it contains users from every owned brand and tenant.
    ///
    /// Role filtering still uses <c>credentials.db</c> because colleague/customer status is shared profile
    /// data. Users without a credentials row are treated as regular customers for the customer/all scopes
    /// and are excluded from the colleague-only scope.
    /// </remarks>
    /// <example>
    /// <code>
    /// var recipients = await GetBroadcastRecipientsAsync("colleagues", cancellationToken);
    /// await StartBroadcastJobAsync(botClient, message, recipients, template, cancellationToken);
    /// </code>
    /// </example>
    private async Task<List<long>> GetBroadcastRecipientsAsync(string audience, CancellationToken cancellationToken)
    {
        var normalizedAudience = NormalizeBroadcastAudience(audience);
        var botId = BotContextAccessor.CurrentBotId;
        var scopedTelegramUserIds = await _userDbContext.BotUserStates
            .AsNoTracking()
            .Where(x => x.BotId == botId && x.TelegramUserId > 0)
            .Select(x => x.TelegramUserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (scopedTelegramUserIds.Count == 0)
            return new List<long>();

        var credentialRows = await _credentialsDbContext.Users
            .AsNoTracking()
            .Select(x => new
            {
                x.TelegramUserId,
                x.ChatID,
                x.IsColleague
            })
            .ToListAsync(cancellationToken);

        var credentialsByKnownId = new Dictionary<long, (long TelegramUserId, long ChatId, bool IsColleague)>();
        foreach (var credential in credentialRows)
        {
            var value = (credential.TelegramUserId, credential.ChatID, credential.IsColleague);
            if (credential.TelegramUserId > 0 && !credentialsByKnownId.ContainsKey(credential.TelegramUserId))
                credentialsByKnownId.Add(credential.TelegramUserId, value);

            if (credential.ChatID > 0 && !credentialsByKnownId.ContainsKey(credential.ChatID))
                credentialsByKnownId.Add(credential.ChatID, value);
        }

        var recipients = new List<long>();
        foreach (var scopedTelegramUserId in scopedTelegramUserIds)
        {
            credentialsByKnownId.TryGetValue(scopedTelegramUserId, out var credential);
            var hasCredential = credential.TelegramUserId > 0 || credential.ChatId > 0;
            var isColleague = hasCredential && credential.IsColleague;

            if (normalizedAudience == BroadcastAudienceCustomers && isColleague)
                continue;

            if (normalizedAudience == BroadcastAudienceColleagues && !isColleague)
                continue;

            var chatId = hasCredential && credential.ChatId > 0
                ? credential.ChatId
                : hasCredential && credential.TelegramUserId > 0
                    ? credential.TelegramUserId
                    : scopedTelegramUserId;

            if (chatId > 0)
                recipients.Add(chatId);
        }

        return recipients
            .Distinct()
            .ToList();
    }

    private List<string> GetLocations()
    {

        var serversJson = ReadJsonFile.ReadJsonAsString();
        var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);
        return servers.Keys.ToList();


    }
    /// <summary>
    /// Sends the current runtime status of every configured, assistant, and tenant bot to a super-admin chat.
    /// </summary>
    /// <param name="botClient">Telegram client that received the super-admin command.</param>
    /// <param name="chatId">Telegram chat id where the status report should be sent.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram send operations.</param>
    /// <returns>A task that completes after one or more status message chunks are sent.</returns>
    /// <remarks>
    /// The report is generated from in-memory receiver status plus <see cref="BotRegistry" /> configuration. It does
    /// not call Telegram or expose bot tokens, so it is safe to run while Telegram is unstable or while some bots are
    /// offline.
    /// </remarks>
    private async Task SendRuntimeBotStatusAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var snapshots = _botRuntimeStatusStore.GetSnapshots(_botRegistry.Bots);
        var report = BuildRuntimeBotStatusText(snapshots);

        foreach (var chunk in SplitTelegramPlainText(report, 3800))
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: chunk,
                replyMarkup: GetMainMenuKeyboard(),
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Builds a plain-text super-admin report from runtime bot status snapshots.
    /// </summary>
    /// <param name="snapshots">Status snapshots returned by <see cref="BotRuntimeStatusStore.GetSnapshots" />.</param>
    /// <returns>Plain Telegram-safe text that contains no Markdown or HTML entities.</returns>
    /// <remarks>
    /// The output intentionally avoids parse modes because bot ids and usernames often contain underscores. A parse
    /// error in this diagnostic command would make it useless exactly when the admin needs it most.
    /// </remarks>
    private static string BuildRuntimeBotStatusText(IReadOnlyList<BotRuntimeStatusSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        var now = DateTime.UtcNow;
        var total = snapshots?.Count ?? 0;
        var running = snapshots?.Count(x => x.IsReceiverRunning) ?? 0;
        var enabled = snapshots?.Count(x => x.Enabled) ?? 0;

        builder.AppendLine("🤖 وضعیت ربات‌های در حال اجرای سرویس");
        builder.AppendLine($"زمان UTC: {now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"خلاصه: {running}/{total} receiver running, enabled={enabled}");
        builder.AppendLine();

        foreach (var bot in snapshots ?? Array.Empty<BotRuntimeStatusSnapshot>())
        {
            var icon = GetRuntimeBotStatusIcon(bot);
            var username = string.IsNullOrWhiteSpace(bot.Username) ? "بدون username" : "@" + bot.Username.Trim().TrimStart('@');
            builder.AppendLine($"{icon} {bot.BotId} ({username})");
            builder.AppendLine($"   type={bot.BotType ?? "unknown"} | brand={bot.BrandName ?? "-"}");
            builder.AppendLine($"   enabled={bot.Enabled} | token={bot.HasToken} | receiver={bot.IsReceiverRunning}");
            builder.AppendLine($"   status={bot.Status ?? "-"} | updatedUtc={(bot.UpdatedAtUtc.HasValue ? bot.UpdatedAtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-")}");
            if (bot.OwnerTelegramUserId.HasValue)
                builder.AppendLine($"   owner={bot.OwnerTelegramUserId.Value}");
            if (!string.IsNullOrWhiteSpace(bot.LastError))
                builder.AppendLine($"   lastError={TrimForStatus(bot.LastError, 180)}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Selects a visual status marker for one runtime bot snapshot.
    /// </summary>
    /// <param name="snapshot">Bot status snapshot shown in the super-admin report.</param>
    /// <returns>
    /// Emoji marker that distinguishes disabled/missing/failed bots, fully listening receivers, and optimistic
    /// receivers whose Telegram initialization is still <c>initializing</c> or <c>degraded</c>.
    /// </returns>
    private static string GetRuntimeBotStatusIcon(BotRuntimeStatusSnapshot snapshot)
    {
        if (snapshot == null)
            return "⚪";
        if (!snapshot.Enabled)
            return "⚪";
        if (!snapshot.HasToken)
            return "🟡";
        if (snapshot.IsReceiverRunning &&
            (string.Equals(snapshot.Status, "initializing", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(snapshot.Status, "degraded", StringComparison.OrdinalIgnoreCase)))
        {
            return "⚠️";
        }
        if (snapshot.IsReceiverRunning)
            return "✅";
        if (string.Equals(snapshot.Status, "startup_failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.Status, "invalid_token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.Status, "duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return "❌";
        }

        return "⏸";
    }

    /// <summary>
    /// Splits a plain text report into Telegram-sized chunks while preserving line boundaries where possible.
    /// </summary>
    /// <param name="text">Plain text to split. The value must already be safe to send without parse mode.</param>
    /// <param name="maxLength">Maximum chunk length in UTF-16 characters. Use a value below Telegram's hard limit.</param>
    /// <returns>One or more chunks that can be sent as separate Telegram messages.</returns>
    private static IEnumerable<string> SplitTelegramPlainText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (builder.Length + line.Length + 1 > maxLength && builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            builder.AppendLine(line);
        }

        if (builder.Length > 0)
            yield return builder.ToString();
    }

    /// <summary>
    /// Trims long diagnostic text so one noisy error does not dominate the super-admin status screen.
    /// </summary>
    /// <param name="value">Raw non-secret error text.</param>
    /// <param name="maxLength">Maximum number of characters to keep.</param>
    /// <returns>The original value when short enough; otherwise a trimmed value with an ellipsis suffix.</returns>
    private static string TrimForStatus(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value.Substring(0, Math.Max(0, maxLength - 1)) + "…";
    }

    /// <summary>
    /// Handles the super-admin flow that manually verifies any owned-bot user's phone number.
    /// </summary>
    /// <param name="botClient">
    /// Telegram client of the currently active owned bot. The client is used only for the private admin conversation;
    /// the verified user is notified separately through every owned bot they have previously started.
    /// </param>
    /// <param name="message">
    /// Text message sent by a configured super-admin. The flow expects the action button, a numeric Telegram user id,
    /// a phone number, and finally one of the confirmation buttons.
    /// </param>
    /// <param name="currentUser">
    /// Bot-scoped conversation state for the super-admin. This state belongs to the active owned bot and cannot affect
    /// the same administrator's state in another owned or tenant bot.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for database operations and Telegram notifications.</param>
    /// <returns>
    /// <c>true</c> when the message belongs to the manual phone flow and has been fully handled; otherwise
    /// <c>false</c> so the normal super-admin router can process it.
    /// </returns>
    /// <remarks>
    /// Manual verification deliberately bypasses the normal Iranian-number and shared-contact ownership checks. This
    /// allows a super-admin to approve virtual numbers and numbers from any country. The target must already exist in
    /// <c>credentials.db</c>, and the flow requires a separate final confirmation before mutating the shared profile.
    /// </remarks>
    /// <example>
    /// <code>
    /// 📱 تایید دستی شماره تلفن
    /// 123456789
    /// +491701234567
    /// ✅ تایید شماره تلفن
    /// </code>
    /// </example>
    private async Task<bool> TryHandleManualPhoneVerificationAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        CancellationToken cancellationToken)
    {
        var text = message.Text?.Trim() ?? string.Empty;
        if (string.Equals(text, "/start", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "📑 Menu", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(text, AdminVerifyPhoneAction, StringComparison.Ordinal))
        {
            // Clear stale admin-flow data before starting this sensitive identity operation.
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            currentUser = new User
            {
                Id = message.From.Id,
                Flow = "admin",
                LastStep = AdminPhoneUserIdStep
            };
            await _userDbContext.SaveUserStatus(currentUser);

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "آیدی عددی تلگرام کاربر را وارد کنید.\n\nکاربر باید قبلاً حداقل یکی از ربات‌های مجموعه را اجرا کرده باشد.",
                replyMarkup: BuildAdminPhoneInputKeyboard());
            return true;
        }

        if (!string.Equals(currentUser.Flow, "admin", StringComparison.Ordinal))
            return false;

        if (string.Equals(currentUser.LastStep, AdminPhoneUserIdStep, StringComparison.Ordinal))
        {
            var normalizedUserId = text.PersianNumbersToEnglish();
            if (!long.TryParse(normalizedUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var targetUserId) ||
                targetUserId <= 0)
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "آیدی عددی معتبر نیست. فقط آیدی عددی تلگرام کاربر را وارد کنید.",
                    replyMarkup: BuildAdminPhoneInputKeyboard());
                return true;
            }

            var target = await _credentialsDbContext.GetUserStatusWithId(targetUserId);
            if (target == null)
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "این کاربر در اطلاعات ربات پیدا نشد. از کاربر بخواهید ابتدا یکی از ربات‌های مجموعه را اجرا کند، سپس دوباره آیدی را بفرستید.",
                    replyMarkup: BuildAdminPhoneInputKeyboard());
                return true;
            }

            currentUser.LastStep = $"{AdminPhoneNumberStep}|{targetUserId.ToString(CultureInfo.InvariantCulture)}";
            await _userDbContext.SaveUserStatus(currentUser);

            var existingPhone = string.IsNullOrWhiteSpace(target.PhoneNumber)
                ? "ثبت نشده"
                : MaskPhoneNumber(target.PhoneNumber);

            // User-controlled names and usernames can contain Markdown control characters such as underscores.
            // Build this dynamic profile block as encoded HTML so Telegram cannot reject the next flow step.
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text:
                    "👤 <b>کاربر پیدا شد</b>\n" +
                    $"{TelegramUserLinkFormatter.HtmlSummary(target)}\n" +
                    $"شماره فعلی: <code>{Html(existingPhone)}</code>\n\n" +
                    "شماره جدید را همراه کد کشور وارد کنید. شماره مجازی و شماره کشورهای غیرایرانی نیز قابل تأیید است.\n" +
                    "نمونه: +491701234567",
                parseMode: ParseMode.Html,
                replyMarkup: BuildAdminPhoneInputKeyboard());
            return true;
        }

        if (currentUser.LastStep?.StartsWith(AdminPhoneNumberStep + "|", StringComparison.Ordinal) == true)
        {
            if (!TryReadStateTargetUserId(currentUser.LastStep, AdminPhoneNumberStep, out var targetUserId))
            {
                await ResetBrokenAdminPhoneFlowAsync(botClient, message.Chat.Id, currentUser);
                return true;
            }

            if (!TryNormalizeAdminPhoneNumber(text, out var normalizedPhone))
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "ساختار شماره معتبر نیست. شماره را با ۷ تا ۱۸ رقم و در صورت نیاز با علامت + و کد کشور وارد کنید.",
                    replyMarkup: BuildAdminPhoneInputKeyboard());
                return true;
            }

            currentUser.ConfigLink = normalizedPhone;
            currentUser.LastStep = $"{AdminPhoneConfirmationStep}|{targetUserId.ToString(CultureInfo.InvariantCulture)}";
            await _userDbContext.SaveUserStatus(currentUser);

            var target = await _credentialsDbContext.GetUserStatusWithId(targetUserId);
            if (target == null)
            {
                await ResetBrokenAdminPhoneFlowAsync(botClient, message.Chat.Id, currentUser);
                return true;
            }

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text:
                    "📱 <b>تأیید نهایی شماره تلفن</b>\n\n" +
                    $"{TelegramUserLinkFormatter.HtmlSummary(target)}\n" +
                    $"آیدی عددی: <code>{targetUserId}</code>\n" +
                    $"شماره: <code>{Html(normalizedPhone)}</code>\n\n" +
                    "پس از تأیید، محدودیت ثبت شماره برای این کاربر برداشته می‌شود.",
                parseMode: ParseMode.Html,
                replyMarkup: BuildAdminPhoneConfirmationKeyboard());
            return true;
        }

        if (currentUser.LastStep?.StartsWith(AdminPhoneConfirmationStep + "|", StringComparison.Ordinal) == true)
        {
            if (string.Equals(text, CancelAdminPhoneButton, StringComparison.Ordinal))
            {
                await _userDbContext.ClearUserStatus(currentUser);
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "عملیات تأیید شماره تلفن لغو شد.",
                    replyMarkup: GetAdminKeyboard());
                return true;
            }

            if (!string.Equals(text, ConfirmAdminPhoneButton, StringComparison.Ordinal))
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "برای ثبت شماره از دکمه تأیید استفاده کنید یا عملیات را لغو کنید.",
                    replyMarkup: BuildAdminPhoneConfirmationKeyboard());
                return true;
            }

            if (!TryReadStateTargetUserId(currentUser.LastStep, AdminPhoneConfirmationStep, out var targetUserId) ||
                !TryNormalizeAdminPhoneNumber(currentUser.ConfigLink, out var normalizedPhone))
            {
                await ResetBrokenAdminPhoneFlowAsync(botClient, message.Chat.Id, currentUser);
                return true;
            }

            var target = await _credentialsDbContext.GetUserStatusWithId(targetUserId);
            if (target == null)
            {
                await ResetBrokenAdminPhoneFlowAsync(botClient, message.Chat.Id, currentUser);
                return true;
            }

            var previousPhone = target.PhoneNumber;
            await _credentialsDbContext.SavePhoneNumber(targetUserId, normalizedPhone);
            target.PhoneNumber = normalizedPhone;
            await _userDbContext.ClearUserStatus(currentUser);

            LogAdminPhoneVerification(message.From, target, previousPhone, normalizedPhone);

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text:
                    "✅ <b>شماره تلفن کاربر با موفقیت تأیید شد.</b>\n\n" +
                    $"{TelegramUserLinkFormatter.HtmlSummary(target)}\n" +
                    $"شماره ثبت‌شده: <code>{Html(normalizedPhone)}</code>\n" +
                    $"نوع حساب: <code>{Html(target.IsColleague ? "همکار" : "کاربر عادی")}</code>",
                parseMode: ParseMode.Html,
                replyMarkup: GetAdminKeyboard());

            try
            {
                await _ownedBotNotificationService.NotifyUserAcrossOwnedBotsAsync(
                    targetUserId,
                    "✅ شماره تلفن حساب شما توسط مدیریت تأیید شد و اکنون می‌توانید از امکانات ربات استفاده کنید.",
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // Verification is already persisted; a Telegram delivery failure must not roll it back.
                _logger.LogWarning(
                    ex,
                    "Manual phone verification notification failed after persistence. userId={TelegramUserId}",
                    targetUserId);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses the target Telegram user id stored in a manual-phone conversation-state key.
    /// </summary>
    /// <param name="lastStep">State value in the form <c>step-prefix|telegram-user-id</c>.</param>
    /// <param name="expectedPrefix">Exact state-step prefix expected by the current phase.</param>
    /// <param name="telegramUserId">Receives the positive numeric Telegram user id when parsing succeeds.</param>
    /// <returns><c>true</c> when the state contains the expected prefix and a positive user id; otherwise <c>false</c>.</returns>
    /// <remarks>This rejects malformed or stale state instead of applying a sensitive verification to the wrong user.</remarks>
    private static bool TryReadStateTargetUserId(string lastStep, string expectedPrefix, out long telegramUserId)
    {
        telegramUserId = 0;
        var parts = lastStep?.Split('|', StringSplitOptions.TrimEntries);
        return parts?.Length == 2 &&
               string.Equals(parts[0], expectedPrefix, StringComparison.Ordinal) &&
               long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out telegramUserId) &&
               telegramUserId > 0;
    }

    /// <summary>
    /// Normalizes a super-admin supplied phone number without applying country, carrier, or virtual-number rules.
    /// </summary>
    /// <param name="input">
    /// Raw phone number from the private admin chat. Persian/Arabic digits and common visual separators are accepted;
    /// letters, extensions, and multiple plus signs are rejected.
    /// </param>
    /// <param name="normalizedPhone">
    /// Receives 7 to 18 ASCII digits, optionally prefixed with <c>+</c>. A leading <c>00</c> is converted to <c>+</c>.
    /// </param>
    /// <returns><c>true</c> when the input has a safe phone-number shape; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// This is an administrative override, not proof that Telegram owns the number. It intentionally accepts Iranian,
    /// non-Iranian, and virtual numbers while still rejecting arbitrary text from entering the credentials profile.
    /// </remarks>
    private static bool TryNormalizeAdminPhoneNumber(string input, out string normalizedPhone)
    {
        normalizedPhone = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var value = input.PersianNumbersToEnglish().Trim();
        var digits = new StringBuilder(value.Length);
        var hasLeadingPlus = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '+' && index == 0)
            {
                hasLeadingPlus = true;
                continue;
            }

            if (char.IsDigit(character))
            {
                digits.Append(character);
                continue;
            }

            if (character is ' ' or '-' or '(' or ')')
                continue;

            return false;
        }

        if (digits.Length < 7 || digits.Length > 18)
            return false;

        var digitText = digits.ToString();
        if (!hasLeadingPlus && digitText.StartsWith("00", StringComparison.Ordinal) && digitText.Length > 2)
        {
            hasLeadingPlus = true;
            digitText = digitText.Substring(2);
        }

        if (digitText.All(character => character == '0'))
            return false;

        normalizedPhone = hasLeadingPlus ? "+" + digitText : digitText;
        return true;
    }

    /// <summary>
    /// Creates the compact input keyboard used while an admin enters a target id or phone number.
    /// </summary>
    /// <returns>A keyboard whose only action returns safely to the super-admin main menu.</returns>
    private static ReplyKeyboardMarkup BuildAdminPhoneInputKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("📑 Menu") }
        })
        {
            ResizeKeyboard = true
        };
    }

    /// <summary>
    /// Creates the final confirmation keyboard for a manual phone verification.
    /// </summary>
    /// <returns>A keyboard containing explicit confirm, cancel, and final-row main-menu actions.</returns>
    private static ReplyKeyboardMarkup BuildAdminPhoneConfirmationKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton(ConfirmAdminPhoneButton), new KeyboardButton(CancelAdminPhoneButton) },
            new[] { new KeyboardButton("📑 Menu") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    /// <summary>
    /// Clears a malformed manual-phone flow and returns the super-admin to a usable admin panel.
    /// </summary>
    /// <param name="botClient">Telegram client for the active owned bot.</param>
    /// <param name="chatId">Private super-admin Telegram chat id that should receive the recovery message.</param>
    /// <param name="currentUser">Bot-scoped admin state that could not be parsed safely.</param>
    /// <returns>A task that completes after state cleanup and Telegram notification.</returns>
    /// <remarks>No credentials profile is modified by this recovery path.</remarks>
    private async Task ResetBrokenAdminPhoneFlowAsync(
        ITelegramBotClient botClient,
        long chatId,
        User currentUser)
    {
        await _userDbContext.ClearUserStatus(currentUser);
        await botClient.CustomSendTextMessageAsync(
            chatId: chatId,
            text: "اطلاعات این مرحله ناقص یا منقضی شده بود. لطفاً تأیید شماره تلفن را از پنل ادمین دوباره شروع کنید.",
            replyMarkup: GetAdminKeyboard());
    }

    /// <summary>
    /// Masks a phone number for non-confirmation status and private audit messages.
    /// </summary>
    /// <param name="phoneNumber">Stored or normalized phone number. The value may include a leading plus sign.</param>
    /// <returns>A partially masked phone number that keeps only a short prefix and suffix visible.</returns>
    private static string MaskPhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return "ثبت نشده";

        var value = phoneNumber.Trim();
        if (value.Length <= 6)
            return new string('*', value.Length);

        return value.Substring(0, 3) + new string('*', value.Length - 6) + value.Substring(value.Length - 3);
    }

    /// <summary>
    /// Writes a private-channel audit record after a super-admin manually verifies a user's phone number.
    /// </summary>
    /// <param name="actor">Telegram super-admin who confirmed the operation.</param>
    /// <param name="target">Shared credentials user whose phone verification was persisted.</param>
    /// <param name="previousPhone">Phone value before the operation; it may be empty when the user was unverified.</param>
    /// <param name="verifiedPhone">New normalized phone value persisted in <c>credentials.db</c>.</param>
    /// <remarks>
    /// Phone values are masked because the central logger channel is an operational audit surface. The target and actor
    /// remain clickable by numeric Telegram id, and no bot token or other credential is included.
    /// </remarks>
    private void LogAdminPhoneVerification(
        Telegram.Bot.Types.User actor,
        CredUser target,
        string previousPhone,
        string verifiedPhone)
    {
        var actorUser = BuildCredUserFromTelegramActor(actor);
        var message =
            "📱 <b>تأیید دستی شماره تلفن</b>\n\n" +
            "📌 انجام‌دهنده\n" +
            $"{TelegramUserLinkFormatter.HtmlSummary(actorUser)}\n\n" +
            "📌 کاربر هدف\n" +
            $"{TelegramUserLinkFormatter.HtmlSummary(target)}\n\n" +
            $"وضعیت قبلی: <code>{Html(string.IsNullOrWhiteSpace(previousPhone) ? "تأیید نشده" : "تأیید شده")}</code>\n" +
            $"شماره قبلی: <code>{Html(MaskPhoneNumber(previousPhone))}</code>\n" +
            $"شماره جدید: <code>{Html(MaskPhoneNumber(verifiedPhone))}</code>\n" +
            $"نوع حساب: <code>{Html(target.IsColleague ? "همکار" : "کاربر عادی")}</code>";

        _logger.LogPayment(message);
    }

    /// <summary>
    /// Gets the reply-keyboard actions available to super-admin users.
    /// </summary>
    /// <returns>Ordered action labels shown in the super-admin keyboard.</returns>
    private string[] GetAdminActions()
    {
        string[] actions = new string[]
        {
            "🚫 Ban user",
            "✅ Unban user",
            "➕ Add credit",
            "➖ Reduce credit",
            "🚀 Promote as admin",
            "❌ Demote as admin",
            "ℹ️ See User Account",
            "📨 Send message to all",
            "✉️ Send message to user",
            "ℹ️ See All account of user",
            "🗑 Delete expired accounts",
            "Sync Gozargah Site",
            "✔️ Verify payment",
            AdminVerifyPhoneAction,
            "🤖 وضعیت ربات‌ها",
            "📑 Menu"
        };
        return actions;
    }

    /// <summary>
    /// Builds the super-admin reply keyboard while keeping the main-menu action on its own final row.
    /// </summary>
    /// <returns>
    /// A two-column reply keyboard for administrative actions, followed by a single full-width
    /// <c>📑 Menu</c> row.
    /// </returns>
    /// <remarks>
    /// The keyboard is used only in owned bots for users listed in <c>adminsUserIds</c>. Tenant owners receive their
    /// separate storefront panel and cannot reach these platform-wide actions.
    /// </remarks>
    private ReplyKeyboardMarkup GetAdminKeyboard()
    {
        var actions = GetAdminActions()
            .Where(action => !string.Equals(action, "📑 Menu", StringComparison.Ordinal))
            .ToArray();

        // Keep operational actions dense, but reserve the final full row for an unambiguous escape to the main menu.
        List<KeyboardButton[]> keyboardRows = new List<KeyboardButton[]>();
        for (int i = 0; i < actions.Length; i += 2)
        {
            KeyboardButton[] row;
            if (i + 1 < actions.Length)
            {
                // Pair two locations in one row
                row = new KeyboardButton[] { new KeyboardButton(actions[i]), new KeyboardButton(actions[i + 1]) };
            }
            else
            {
                // For odd number of locations, last row will have a single column
                row = new KeyboardButton[] { new KeyboardButton(actions[i]) };
            }
            keyboardRows.Add(row);
        }

        keyboardRows.Add(new[] { new KeyboardButton("📑 Menu") });

        var createAccountKeyboard = new ReplyKeyboardMarkup(keyboardRows.ToArray());
        return createAccountKeyboard;
    }




    private ReplyKeyboardMarkup GetLocationKeyboard()
    {
        // Example list of locations
        List<string> locations = GetLocations();

        // Creating keyboard buttons dynamically
        var keyboardButtons = locations.Select(location => new KeyboardButton[] { new KeyboardButton(location) }).ToArray();

        // Creating the keyboard markup
        var createAccountKeyboard = new ReplyKeyboardMarkup(keyboardButtons);
        return createAccountKeyboard;
    }

    private string CaptionForRenewAccount(User user, DateTime expirationDateUTC, bool showTraffic)
    {
        string msg = "";
        msg = $"✅ مشخصات اکانت شما:  \n";
        msg += $"👤نام: `{user.Email}` \n";
        msg += $"⌛️دوره : {ApiService.ConvertPeriodToDays(user.SelectedPeriod)} روزه \n";
        // msg += $"Location: {user.SelectedCountry} \n";
        if (showTraffic) msg += $"🧮 حجم ترافیک: {user.TotoalGB} گیگابایت\n";

        string hijriShamsiDate = DateTime.UtcNow.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).AddMinutes(210).ConvertToHijriShamsi();

        //expired
        if (expirationDateUTC <= DateTime.UtcNow)
            msg += $"📅تاریخ انقضاء:  {hijriShamsiDate}\n";
        else
        {
            hijriShamsiDate = expirationDateUTC.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).AddMinutes(210).ConvertToHijriShamsi();
            msg += $"📅تاریخ انقضاء:  {hijriShamsiDate}\n";
        }


        // msg += "✳️ آموزش کانفیگ لینک" + $"**آی‌اواس** [link text]({_appConfig.ConfiglinkTutorial[0]})" + " | " + $"**اندروید** [link text]({_appConfig.ConfiglinkTutorial[1]}) \n";
        // msg += "✴️ آموزش سابلینک (برای تعویض اتوماتیک و فیلترینگ شدید)" + $"**آی‌اواس** [link text]({_appConfig.SubLinkTotorial[0]})" + " | " + $"**اندروید** [link text]({_appConfig.SubLinkTotorial[1]}) \n";
        msg += $"🔗 ساب لینک: \n `{user.SubLink}`\n \n ";

        msg += $"🔗 لینک اتصال: \n";
        msg += "=== برای کپی شدن لمس کنید === \n";
        msg += $"`{user.ConfigLink}`" + "\n ";
        return msg;
    }

    private string CaptionForAccountCreation(User user, string language, bool showTraffic)
    {
        string msg;
        if (language == "en")
        {
            msg = $"✅ Account details: \n";
            msg += $"Account Name: `{user.Email}`\n";
            msg += $"Location: {user.SelectedCountry} \nDuration: {user.SelectedPeriod}";
            if (Convert.ToInt32(user.TotoalGB) < 100) msg += $"\nTraffic: {user.TotoalGB}GB.\n";
            string hijriShamsiDate = DateTime.UtcNow.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).AddMinutes(210).ConvertToHijriShamsi();
            msg += $"\nExpiration Date: {hijriShamsiDate}\n";
            msg += $"Your Sublink is: \n `{user.SubLink}` \n";
            msg += $"Your Connection link is: \n";
            msg += "============= Tap to Copy =============\n";
            msg += $"`{user.ConfigLink}`" + "\n ";
        }
        else
        {
            msg = $"✅ مشخصات اکانت شما:  \n";
            msg += $"👤نام: `{user.Email}` \n";
            msg += $"⌛️دوره : {ApiService.ConvertPeriodToDays(user.SelectedPeriod)} روزه \n";
            // msg += $"Location: {user.SelectedCountry} \n";
            if (showTraffic) msg += $"🧮 حجم ترافیک: {user.TotoalGB} گیگابایت\n";

            string hijriShamsiDate = DateTime.UtcNow.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).AddMinutes(210).ConvertToHijriShamsi();
            msg += $"📅تاریخ انقضاء:  {hijriShamsiDate}\n";

            // msg += "✳️ آموزش کانفیگ لینک" + $"**آی‌اواس** [link text]({_appConfig.ConfiglinkTutorial[0]})" + " | " + $"**اندروید** [link text]({_appConfig.ConfiglinkTutorial[1]}) \n";
            // msg += "✴️ آموزش سابلینک (برای تعویض اتوماتیک و فیلترینگ شدید)" + $"**آی‌اواس** [link text]({_appConfig.SubLinkTotorial[0]})" + " | " + $"**اندروید** [link text]({_appConfig.SubLinkTotorial[1]}) \n";
            msg += $"🔗 ساب لینک: \n `{user.SubLink}`\n \n ";

            msg += $"🔗 لینک اتصال: \n";
            msg += "=== برای کپی شدن لمس کنید === \n";
            msg += $"`{user.ConfigLink}`" + "\n ";

        }
        return msg;
    }

    private async Task HandleUpdateRegularUsers(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {


        if (update.Message is not { } message)
            return;
        var chatId = message.Chat.Id;


        if (message is not null && message.Type == MessageType.Contact && message.Contact != null)
        {
            await _credentialsDbContext.GetUserStatus(GetCreduserFromMessage(message));
            Contact userContact;
            userContact = message.Contact;
            bool isValidPhoneNumber = await CheckUserPhoneNumber(message.Chat.Id, message);
            if (isValidPhoneNumber)
            {
                await _credentialsDbContext.SavePhoneNumber(message.From.Id, message.Contact.PhoneNumber);
                await botClient.CustomSendTextMessageAsync(
                             chatId: message.Chat.Id,
                             text: "شماره شما با موفقیت تایید شد. حالا دوباره گزینه مورد نظرتان را انتخاب کنید.",
                             replyMarkup: MainReplyMarkupKeyboardFa());
                return;
            }
            else
            {

            }
        }
        // Only process text messages
        if (message.Text is not { } messageText)
            return;

        Console.WriteLine($"Received a '{message.Text}' message in chat {chatId}.");


        var credUser = await _credentialsDbContext.GetUserStatus(GetCreduserFromMessage(message));
        var user = await _userDbContext.GetUserStatus(message.From.Id);
        ReferralRegistrationResult referralRegistration = null;
        var isReferralStart = ReferralService.TryParseStartPayload(message.Text, out var referralCode);
        if (isReferralStart)
        {
            referralRegistration = await _referralService.RegisterRelationshipAsync(
                message.From.Id,
                referralCode,
                CurrentBot?.Id ?? BotContextAccessor.DefaultBotId,
                CurrentBot?.Type ?? BotInstanceTypes.Owned,
                cancellationToken);
        }

        var mandatoryJoinChannels = BuildMandatoryJoinChannels(CurrentChannelIds);
        var isJoined = await isJoinedToChannel(mandatoryJoinChannels.Select(c => c.ChatId), message.From.Id);
        // var isJoined = false;
        if (!isJoined)
        {
            ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
               {
                    new KeyboardButton[] { "عضو شدم!" }
                })
            {
                ResizeKeyboard = false
            };
            List<InlineKeyboardButton[]> rows = new List<InlineKeyboardButton[]>();
            foreach (var channel in mandatoryJoinChannels.Where(c => !string.IsNullOrWhiteSpace(c.Url)))
            {
                rows.Add(new[] { InlineKeyboardButton.WithUrl(channel.Label, channel.Url) });
            }

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            if (rows.Count > 0)
            {
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows.ToArray());
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "به کانال(های) زیر بپیوندید و روی استارت کلیک کنید. \n" + "/start",
                    replyMarkup: inlineKeyboard);
            }
            else
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "برای استفاده از ربات باید عضو کانال‌های معرفی‌شده شوید، اما لینک قابل نمایش برای کانال‌ها تنظیم نشده است. لطفاً به پشتیبانی پیام بدهید.");
            }

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "پس از عضویت روی دکمه زیر کلیک کنید.",
                replyMarkup: replyKeyboardMarkup);
            return;
        }



        if (message.Text == "/start" || isReferralStart)
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            var registrationText = BuildReferralRegistrationMessage(referralRegistration);
            await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id,
               text: string.IsNullOrWhiteSpace(registrationText)
                   ? "به ربات خوش آمدید!"
                   : $"به ربات خوش آمدید!\n\n{registrationText}",
                replyMarkup: MainReplyMarkupKeyboardFa());
            return;
        }
        else if (await TryHandleReferralMenuCommandAsync(
                     botClient,
                     message,
                     user,
                     cancellationToken))
        {
            return;
        }
        else if (message.Text == "عضو شدم!")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "به ربات خوش آمدید!",
                replyMarkup: MainReplyMarkupKeyboardFa());

        }
        else if (message.Text == "💻 ارتباط با ادمین")
        {
            var support = BuildOwnedBotSupportContactHtml(CurrentSupportAccount);
            var text = string.IsNullOrWhiteSpace(support)
                ? "پشتیبانی این ربات هنوز تنظیم نشده است. لطفاً بعداً دوباره تلاش کنید."
                : "✅ برای ارتباط با پشتیبانی از لینک زیر اقدام کنید.\n🆔 " + support;

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text, parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa());

            // Save the user's context
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

        }

        else if (message.Text == "🏠منو" || message.Text == "لغو" || message.Text == "منوی اصلی")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "منوی اصلی",
                replyMarkup: MainReplyMarkupKeyboardFa());
        }

        else if (message.Text == "📌 قابلیت‌های ربات" || message.Text == "قابلیت‌های ربات")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: BuildBotCapabilitiesMessage(credUser),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa());
            return;
        }

        else if (message.Text == "📋 تعرفه‌ها" || message.Text == "تعرفه‌ها")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await RefreshOwnedBotColleagueRoleFromGozargahAsync(credUser, cancellationToken);

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: _xuiV3PurchaseService.BuildTariffsText(credUser?.IsColleague == true),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa());
            return;
        }

        else if (message.Text == "📒 تراکنش‌های من" || message.Text == "تراکنش‌های من")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await SendWalletLedgerAsync(botClient, message.Chat.Id, message.From.Id, 0, cancellationToken);
            return;
        }

        else if (await _tenantBotService.TryHandleOwnerMessageAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }

        else if (await _xuiV3BotFlowService.TryHandleAccountActionAsync(
            botClient,
            message,
            credUser,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            await _userDbContext.ClearUserStatus(user);
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleDeleteExpiredAccountsAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleAccountCommentTextAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleAccountSearchAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleColleagueRequestAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleFreeTrialAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }

        else if (await _xuiV3BotFlowService.TryHandlePurchaseTextAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleRenewAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }
        else if (await _xuiV3BotFlowService.TryHandleAccountCounterLookupAsync(
            botClient,
            message,
            credUser,
            user,
            MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
            return;
        }

        else if (StartsWithEnableOrDisable(message.Text))
        {
            bool enable;
            var input = message.Text;
            if (message.Text.Contains("/enable_"))
            {
                input = message.Text.Replace("/enable_", "");
                enable = true;
            }
            else
            {
                input = message.Text.Replace("/disable_", "");
                enable = false;
            }

            bool result = await ApiService.AccountActivating(input, credUser.TelegramUserId, enable);


            if (!result)
            {
                await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "متاسفانه عملیات مورد نظر انجام نشد!",
                                replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
            }
            else
            {
                await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "عملیات مورد نظر با موفقیت انجام شد!",
                                replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
            }

            await _userDbContext.ClearUserStatus(user);
            return;
        }

        else if (message.Text == "🌟اکانت رایگان")
        {
            var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                           {
            new []
            {
                new KeyboardButton("تایید نهایی"),
            },
            new []
            {
                new KeyboardButton("انصراف"),
            },
        });

            if (credUser.IsColleague)
            {
                if (credUser.AccountBalance <= 1000)
                {
                    await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"شما اعتبار لازم برای ساخت اکانت تست را ندارید. ابتدا حساب خود را شارژ بفرمایید.",
                    replyMarkup: MainReplyMarkupKeyboardFa());
                    return;
                }

                user.Flow = "create";
                user.LastStep = "ask_confirmation";
                user.SelectedCountry = "Test";
                user.TotoalGB = "1";
                user.Type = "tunnel";
                user.PaymentMethod = "credit";
                user.SelectedPeriod = "1 Day";
                user.ConfigPrice = 0;
                await _userDbContext.SaveUserStatus(user);

                await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: $"✅ شما اعتبار لازم برای ساخت اکانت مورد نظر را دارید. \n" + " ❕ برای دریافت اکانت، گزینه تایید نهایی را بزنید در غیر این صورت انصراف را انتخاب نمایید.\n",
                                    replyMarkup: confirmationKeyboard);
                return;

            }
            // Normal user
            else
            {
                if (string.IsNullOrEmpty(credUser.PhoneNumber))
                {
                    string text = " لطفا اجازه دریافت شماره خود را برای دریافت اکانت رایگان یک روزه ارسال کنید و سپس مجدد روی دریافت اکانت رایگان کلیک کنید. " + "/n" + " در صورت عدم رضایت روی /start کلیک کنید";
                    await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: text,
                                replyMarkup: GetPhoneNumber());
                    return;
                }
                else if ((DateTime.Now - user.LastFreeAcc).Days <= 30)
                {
                    var remainingDays = (TimeSpan.FromDays(31) - (DateTime.Now - user.LastFreeAcc)).Days.ToString();
                    string text = $"شما در یک ماه گذشته اکانت رایگان خود را دریافت کرده اید. لطفاً {remainingDays} روز دیگر تلاش کنید. ";
                    await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: text,
                                replyMarkup: MainReplyMarkupKeyboardFa());
                    return;
                }
                else
                {
                    user.Flow = "create";
                    user.LastStep = "ask_confirmation";
                    user.SelectedCountry = "Test";
                    user.TotoalGB = "1";
                    user.Type = "tunnel";
                    user.PaymentMethod = "credit";
                    user.SelectedPeriod = "1 Day";
                    user.ConfigPrice = 0;
                    await _userDbContext.SaveUserStatus(user);
                    await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: $"✅ شما امکان ساخت اکانت مورد نظر را دارید. \n" + " ❕ برای دریافت اکانت، گزینه تایید نهایی را بزنید در غیر این صورت انصراف را انتخاب نمایید.\n",
                                    replyMarkup: confirmationKeyboard);
                    return;
                }
            }


        }

        else if (message.Text == "شارژ حساب کاربری")
        {
            // await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            // var text = "درحال حاضر شارژ حساب فقط از طریق ادمین امکان پذیر می‌باشد.برای شارژ حساب خود به ادمین پیام دهید و پیام زیر را برای ایشان فوروارد کنید: /n @vpsnetiran_vpn /n به زودی پرداخت ریالی و ترونی به ربات اضافه خواهد شد.";
            // await botClient.CustomSendTextMessageAsync(
            //                 chatId: message.Chat.Id,
            //                 text: text,
            //                 replyMarkup: new ReplyKeyboardRemove());

            // text = await GetUserProfileMessage(credUser);
            // await botClient.CustomSendTextMessageAsync(
            //     chatId: message.Chat.Id,
            //     text: text,
            //     replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });



            user.PaymentMethod = "crypto";
            user.Flow = "charge";
            user.LastStep = "payment_method_selection";
            await _userDbContext.SaveUserStatus(user);



        }

        else if (message.Text == "💳خرید اکانت جدید")
        {
            if (string.Equals(_appConfig.XuiApiVersionMode, "v3", StringComparison.OrdinalIgnoreCase))
            {
                var xuiUser = await _userDbContext.GetUserStatus(message.From.Id);
                if (await _xuiV3BotFlowService.TryStartPurchaseAsync(botClient, message, credUser, xuiUser, cancellationToken))
                    return;
            }

            var replyKeboard = PriceReplyMarkupKeyboardFa(credUser.IsColleague, false);

            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Create New Account", Flow = "create" });

            await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id,
               text: "شرایط اکانت ها به شرح زیر میباشد:",
               replyMarkup: replyKeboard);

        }

        else if (message.Text.Contains("راهنما"))
        {

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            var rkm = new ReplyKeyboardMarkup(new[]
                {
                    new KeyboardButton[] { "راهنمای اپل 📱" },
                    new KeyboardButton[] { "راهنمای اندروید 📱" },
                    new KeyboardButton[] { "راهنمای ویندوز 💻" }
                })
            {
                ResizeKeyboard = true, // Optional: to fit the keyboard to the button sizes
                OneTimeKeyboard = true // Optional: to hide the keyboard after a button is pressed
            };
            if (message.Text == "💡راهنما نصب")
            {

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "منوی راهنما",
                    replyMarkup: rkm);
                return;
            }
            else if (message.Text == "راهنمای اپل 📱")
            {
                List<InlineKeyboardButton[]> rows = (CurrentIosTutorial ?? Array.Empty<string>()).Select((url, index) => new InlineKeyboardButton[]
                    {
                        InlineKeyboardButton.WithUrl(GetTutorialButtonText(index), url)
                    }).ToList();

                // Create the InlineKeyboardMarkup
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows);

                await ActiveBotClient.CustomSendTextMessageAsync(chatId: message.Chat.Id,
                     text: "برای دریافت آموزش روی دکمه زیر کلیک کنید.",
                     replyMarkup: inlineKeyboard);


                // foreach (var item in _appConfig.IosTutorial)
                // {
                // var forwardMessage = GetChannelAndPost(item);
                // await ActiveBotClient.CustomForwardMessage(chatId: message.Chat.Id,
                // fromChatId: forwardMessage.ChannelName,
                // messageId: forwardMessage.PostNumber);


                // }
            }
            else if (message.Text == "راهنمای اندروید 📱")
            {
                List<InlineKeyboardButton[]> rows = (CurrentAndroidTutorial ?? Array.Empty<string>()).Select((url, index) => new InlineKeyboardButton[]
                    {
                        InlineKeyboardButton.WithUrl(GetTutorialButtonText(index), url)
                    }).ToList();

                // Create the InlineKeyboardMarkup
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows);

                await ActiveBotClient.CustomSendTextMessageAsync(chatId: message.Chat.Id,
                     text: "برای دریافت آموزش روی دکمه زیر کلیک کنید.",
                     replyMarkup: inlineKeyboard);

                // foreach (var item in _appConfig.AndroidTutorial)
                // {
                //     var forwardMessage = GetChannelAndPost(item);
                //     await ActiveBotClient.CustomForwardMessage(chatId: message.Chat.Id,
                //     fromChatId: forwardMessage.ChannelName,
                //     messageId: forwardMessage.PostNumber);
                // }
            }
            else if (message.Text == "راهنمای ویندوز 💻")
            {

                List<InlineKeyboardButton[]> rows = (CurrentWindowsTutorial ?? Array.Empty<string>()).Select((url, index) => new InlineKeyboardButton[]
                    {
                        InlineKeyboardButton.WithUrl(GetTutorialButtonText(index), url)
                    }).ToList();

                // Create the InlineKeyboardMarkup
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows);

                await ActiveBotClient.CustomSendTextMessageAsync(chatId: message.Chat.Id,
                     text: "برای دریافت آموزش روی دکمه زیر کلیک کنید.",
                     replyMarkup: inlineKeyboard);

                // foreach (var item in _appConfig.WindowsTutorial)
                // {
                //     var forwardMessage = GetChannelAndPost(item);
                //     await ActiveBotClient.CustomForwardMessage(chatId: message.Chat.Id,
                //     fromChatId: forwardMessage.ChannelName,
                //     messageId: forwardMessage.PostNumber);
                // }
            }
            else
            {
                await botClient.CustomSendTextMessageAsync(
                              chatId: message.Chat.Id,
                              text: "آموزش مورد نظر وجود ندارد",
                              replyMarkup: MainReplyMarkupKeyboardFa());
            }
            await botClient.CustomSendTextMessageAsync(
                              chatId: message.Chat.Id,
                              text: "منوی اصلی",
                              replyMarkup: MainReplyMarkupKeyboardFa());
        }

        else if (message.Text == "⚙️ مدیریت اکانت")
        {
            var accountManagementRows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "مشاهده وضعیت حساب","تمدید اکانت"},
                new KeyboardButton[] { "وضعیت اکانت های من","🔎 جستجوی اکانت" },
            };

            if (credUser?.IsColleague != true)
                accountManagementRows.Add(new KeyboardButton[] { "حذف اکانت های منقضی", "🤝 درخواست همکاری" });
            else
                accountManagementRows.Add(new KeyboardButton[] { "حذف اکانت های منقضی", "📌 قابلیت‌های ربات" });

            if (credUser?.IsColleague != true)
                accountManagementRows.Add(new KeyboardButton[] { "📌 قابلیت‌های ربات", "💳خرید اکانت جدید" });
            else
            {
                accountManagementRows.Add(new KeyboardButton[] { "💳خرید اکانت جدید", "💰شارژ حساب کاربری" });
                accountManagementRows.Add(new KeyboardButton[] { TenantBotService.OwnerMenuButton });
            }

            accountManagementRows.Add(new KeyboardButton[] { "منوی اصلی" });

            ReplyKeyboardMarkup replyKeyboardMarkup = new(accountManagementRows)
            {
                ResizeKeyboard = true, // This will make the keyboard buttons resize to fit their container
                OneTimeKeyboard = true // This will hide the keyboard after a button is pressed (optional)
            };


            // var text = await GetUserProfileMessage(credUser);
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "یک گزینه را انتخاب نمائید.",
                replyMarkup: replyKeyboardMarkup, parseMode: ParseMode.Markdown);

        }
        else if (user.LastStep == "confirmation" && user.Flow == "charge")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            if (message.Text == "انصراف")
            {
                await botClient.CustomSendTextMessageAsync(
                                        chatId: message.Chat.Id,
                                        text: "فرایند شارژ حساب شما کنسل شد.",
                                        replyMarkup: MainReplyMarkupKeyboardFa());
                return;

            }
            else if (message.Text == "تایید نهایی")
            {
                if (user.PaymentMethod == "zibal")
                {
                    user.LastStep = "payment_method_selection";
                    user.Flow = "charge";
                    user.PaymentMethod = string.Empty;
                    await _userDbContext.SaveUserStatus(user);

                    await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "درگاه پرداخت ریالی فعلاً غیرفعال است. لطفاً از درگاه‌های فعال استفاده کنید.",
                        replyMarkup: BuildChargePaymentMethodKeyboard(),
                        cancellationToken: cancellationToken);
                    return;
                }

                await botClient.CustomSendTextMessageAsync(
                                                                            chatId: message.Chat.Id,
                                                                            text: "لطفاً چند ثانیه صبر کنید.",
                                                                            replyMarkup: new ReplyKeyboardRemove());

                if (user.PaymentMethod == "zibal")
                {
                    long amount = Convert.ToInt64(user.ConfigLink) * 10;
                    var zpi = new ZibalPaymentInfo(user.Id);
                    zpi.ChatId = message.Chat.Id;


                    //search for descripttion
                    // Assuming Price and PriceColleagues are IEnumerable<T>
                    var combinedList = _appConfig.Price.Concat(_appConfig.PriceCommon).Concat(_appConfig.PriceColleagues).ToList();
                    var temp = Math.Abs(combinedList[0].Price - amount);
                    string description = combinedList[0].FakeDescription;
                    foreach (var item in combinedList)
                    {
                        if (Math.Abs(item.Price - amount) < temp)
                        {
                            temp = Math.Abs(item.Price - amount);
                            description = item.FakeDescription;
                        }
                    }




                    long dollarPrice = await new DollarPriceHelper().NobitexUSDTIRTPrice();
                    if (dollarPrice == 0) dollarPrice = 780000;
                    description = $"گیفت کارت {Math.Ceiling((double)(amount / dollarPrice))} دلاری استیم";
                    PaymentRequestResponse x = await ZibalAPI.SendPaymentRequest(amount, zpi.CallbackUrl, _appConfig.ZibalMerchantCode, description);
                    x.PayLink = ZibalAPI.GetPaymentLink(x);
                    zpi.TrackId = x.TrackId;
                    zpi.Amount = amount;
                    zpi.Result = x.Result;
                    zpi.CreatedAt = DateTime.UtcNow;

                    _userDbContext.ZibalPaymentInfos.Add(zpi);
                    await _userDbContext.SaveChangesAsync();

                    var msg = await GetZipalPaymentMessage(credUser, false, zpi, x.PayLink);

                    var inlineKeyboardMarkup = new InlineKeyboardMarkup(new[]
                         {
                                new[]
                                {
                                    InlineKeyboardButton.WithUrl(text: "پرداخت آنلاین  🏧", url: x.PayLink),
                                    InlineKeyboardButton.WithCallbackData(text: "پرداخت کردم", callbackData: $"check_payment_{zpi.Id}"),

                                }
                            });
                    // var msg = x.Message + "\n" + x.PayLink + "\n" + x.Result + "\n" + x.TrackId;
                    var latestMsg = await botClient.CustomSendTextMessageAsync(
                                                chatId: message.Chat.Id,
                                                text: msg,
                                                replyMarkup: inlineKeyboardMarkup,
                                                parseMode: ParseMode.Html);
                    await botClient.CustomSendTextMessageAsync(
                                                chatId: message.Chat.Id,
                                                text: "منوی اصلی",
                                                replyMarkup: MainReplyMarkupKeyboardFa());



                    zpi.TelMsgId = latestMsg.MessageId;

                    await _userDbContext.SaveChangesAsync();

                }

                else if (user.PaymentMethod == "hooshpay")
                {
                    await CreateHooshPayWalletChargeAsync(message, credUser, user, cancellationToken);
                }

                else if (user.PaymentMethod == "swapino")
                {

                    //                     NowPayments nowPayments = new NowPayments(_configuration);
                    //                     long amount = Convert.ToInt64(user.ConfigLink);
                    //                     var now_response = await nowPayments.Createpayment(amount);
                    //                     var trx = (decimal)amount / (await nowPayments.GetTronRate());
                    //                     var theter = (decimal)amount / (await nowPayments.GetUsThetherRate());


                    //                     var text = "✅ لینک خرید از درگاه سواپینو  \n";
                    //                     text += $"\u200F📝 شماره سند:  `{now_response.payment_id}` \n";

                    //                     text += $"\u200F🆔 آیدی عددی کاربر: `{credUser.TelegramUserId}` \n";
                    //                     string hijriShamsiDate = now_response.created_at.ConvertToHijriShamsi();

                    //                     text += $"‌\u200F📅 تاریخ صدور صورتحساب: {hijriShamsiDate}\n";
                    //                     text += $"‌\u200F🧰 آدرس ولت ترونی : `{now_response.pay_address}`\n";

                    //                     text += $"‌\u200F💰(تومان): {Convert.ToInt64(user.ConfigLink).FormatCurrency()}\n";
                    //                     text += $"‌\u200F💲 ترون: {trx.ToString("F4")}\n";
                    //                     text += $"‌\u200F💵 تتر: {theter.ToString("F4")}\n";

                    //                     text += $"‌\u200F🔗  لینک پرداخت: {now_response.weswap_paymentlink}\n";


                    //                     InlineKeyboardMarkup inlineKeyboard = new(new[]
                    //                   {
                    //                  // first row
                    //             new []
                    //     {
                    //                 InlineKeyboardButton.WithCallbackData(text:"وضعیت در انتظار پرداخت 🔄",callbackData:$"PaymentID{now_response.payment_id}"),

                    //     },
                    //     // second row
                    //     new []
                    //     {
                    //         InlineKeyboardButton.WithCallbackData(text:"❓بررسی پرداخت",callbackData:$"PaymentID{now_response.payment_id}"),
                    //         //InlineKeyboardButton.WithCallbackData(text: "2.2", callbackData: "22"),
                    //     },
                    // });

                    //                     var x = new SwapinoPaymentInfo() { Payment_Id = now_response.payment_id, RialAmount = Convert.ToInt64(user.ConfigLink), TelegramUserId = credUser.TelegramUserId, TronAmount = now_response.pay_amount, UsdtAmount = now_response.price_amount };
                    //                     _userDbContext.SwapinoPaymentInfos.Add(x);
                    //                     _userDbContext.SaveChanges();

                    //                     await botClient.CustomSendTextMessageAsync(
                    //                                         chatId: message.Chat.Id,
                    //                                         text: text.EscapeMarkdown(),
                    //                                         replyMarkup: inlineKeyboard);

                    //                     await botClient.CustomSendTextMessageAsync(
                    //                                         chatId: message.Chat.Id,
                    //                                         text: "پس از پرداخت فاکتور 5 دقیقه صبر کنید و روی گزینه بررسی وضعیت پرداخت بزنید تا حساب شما شارژ شود.",
                    //                                         replyMarkup: MainReplyMarkupKeyboardFa());
                    return;

                }

                else if (user.PaymentMethod == "crypto")
                {
                    long amount = Convert.ToInt64(user.ConfigLink);
                    var payment = SwapinoPaymentInfo.CreateCryptoCharge(
                        credUser.TelegramUserId,
                        amount,
                        _appConfig.NowpaymentIpnUrl,
                        chatId: message.Chat.Id,
                        baseCurrency: _appConfig.NowpaymentPriceCurrency);

                    _userDbContext.SwapinoPaymentInfos.Add(payment);
                    await _userDbContext.SaveChangesAsync();

                    try
                    {
                        var priceCurrency = string.IsNullOrWhiteSpace(_appConfig.NowpaymentPriceCurrency)
                            ? "usdtbsc"
                            : _appConfig.NowpaymentPriceCurrency;

                        Console.WriteLine($"[NOWPayments] Creating invoice for user={credUser.TelegramUserId}, amount={amount}, orderId={payment.OrderId}, priceCurrency={priceCurrency}, payCurrency=all");

                        var nowPayment = await _nowPayments.CreateInvoiceAsync(
                            amount,
                            payment.OrderId,
                            $"Wallet charge {payment.OrderId}",
                            null,
                            priceCurrency,
                            CurrentNowPaymentsSuccessUrl,
                            CurrentNowPaymentsCancelUrl,
                            cancellationToken);

                        payment.RawRequestJson = JsonConvert.SerializeObject(new
                        {
                            orderId = payment.OrderId,
                            invoiceId = nowPayment.id,
                            invoiceUrl = nowPayment.invoice_url,
                            paymentId = (string)null,
                            amountToman = amount,
                            priceCurrency,
                            payCurrency = (string)null,
                            usdtIrtPrice = nowPayment.LocalUsdtIrtPrice,
                            priceSource = nowPayment.LocalPriceSource,
                            usedFallbackPrice = nowPayment.LocalUsedFallbackPrice,
                            priceIsRial = nowPayment.LocalPriceIsRial,
                            calculatedPriceAmount = nowPayment.price_amount,
                            callbackUrl = _appConfig.NowpaymentIpnUrl
                        });
                        payment.RawResponseJson = JsonConvert.SerializeObject(nowPayment);
                        Console.WriteLine(
                            $"[NOWPayments] Invoice created. orderId={payment.OrderId}, invoiceId={nowPayment.id}, invoiceUrl={nowPayment.invoice_url}, ipnCallbackUrl={_appConfig.NowpaymentIpnUrl}, priceCurrency={nowPayment.price_currency}, priceAmount={nowPayment.price_amount}, paymentId=");
                        var data = NowPaymentsPaymentRecordData.FromInvoiceResponse(nowPayment);
                        data.OrderId = payment.OrderId;
                        payment.SetNowPaymentsData(data);
                        payment.BaseAmount = nowPayment.price_amount;
                        payment.BaseCurrency = nowPayment.price_currency;
                        await _userDbContext.SaveChangesAsync();

                        var msg = await GetNowPaymentsPaymentMessage(credUser, payment);
                        var inlineKeyboardMarkup = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithUrl(text: "باز کردن فاکتور", url: nowPayment.invoice_url)
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData(text: "بررسی وضعیت", callbackData: $"check_crypto_payment_{payment.OrderId}")
                            }
                        });

                        Message latestMsg;
                        if (!string.IsNullOrWhiteSpace(nowPayment.invoice_url))
                        {
                            using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(nowPayment.invoice_url, 200));
                            latestMsg = await botClient.SendPhotoAsync(
                                message.Chat.Id,
                                InputFile.FromStream(qrStream),
                                caption: msg,
                                parseMode: ParseMode.Html,
                                replyMarkup: inlineKeyboardMarkup,
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            latestMsg = await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: msg,
                                replyMarkup: inlineKeyboardMarkup,
                                parseMode: ParseMode.Html,
                                cancellationToken: cancellationToken);
                        }

                        await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "منوی اصلی",
                            replyMarkup: MainReplyMarkupKeyboardFa(),
                            cancellationToken: cancellationToken);

                        if (latestMsg != null)
                            payment.TelMsgId = latestMsg.MessageId;

                        await _userDbContext.SaveChangesAsync();
                    }
                    catch (NowPaymentsApiException ex)
                    {
                        Console.WriteLine("[NOWPayments] API exception while creating payment:");
                        Console.WriteLine(ex.ToString());

                        payment.Result = JsonConvert.SerializeObject(new
                        {
                            error = ex.Message,
                            statusCode = ex.StatusCode,
                            requestMethod = ex.RequestMethod,
                            requestUri = ex.RequestUri,
                            requestBody = ex.RequestBody,
                            responseBody = ex.ResponseBody,
                            orderId = payment.OrderId,
                            createdAt = DateTime.UtcNow
                        });
                        await _userDbContext.SaveChangesAsync();

                        await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "NOWPayments درخواست را رد کرده است. جزئیات خطا در ترمینال ثبت شد.",
                            replyMarkup: MainReplyMarkupKeyboardFa(),
                            cancellationToken: cancellationToken);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("minimum for usd->"))
                    {
                        Console.WriteLine("[NOWPayments] Minimum amount validation failed:");
                        Console.WriteLine(ex.ToString());

                        payment.Result = JsonConvert.SerializeObject(new
                        {
                            error = ex.Message,
                            orderId = payment.OrderId,
                            createdAt = DateTime.UtcNow
                        });
                        await _userDbContext.SaveChangesAsync();

                        await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "مبلغ وارد شده از حداقل مجاز NOWPayments کمتر است. جزئیات دقیق در ترمینال ثبت شد.",
                            replyMarkup: MainReplyMarkupKeyboardFa(),
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[NOWPayments] Unexpected exception while creating payment:");
                        Console.WriteLine(ex.ToString());

                        payment.Result = JsonConvert.SerializeObject(new
                        {
                            error = ex.Message,
                            orderId = payment.OrderId,
                            createdAt = DateTime.UtcNow
                        });
                        await _userDbContext.SaveChangesAsync();

                        await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "ایجاد پرداخت ارز دیجیتال ناموفق بود. جزئیات خطا در ترمینال ثبت شد.",
                            replyMarkup: MainReplyMarkupKeyboardFa(),
                            cancellationToken: cancellationToken);
                    }

                }

                else
                {
                }
            }
        }
        else if (user.LastStep == "payment_method_selection" && user.Flow == "charge")
        {

            // The user entered a valid number
            var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                       {
            new []
            {
                new KeyboardButton("تایید نهایی"),
            },
            new []
            {
                new KeyboardButton("انصراف"),
            },
        });

            if (message.Text == "درگاه سواپینو(غیرفعال)")
            {
                user.LastStep = "payment_method_selection";
                user.Flow = "charge";
                user.PaymentMethod = string.Empty;
                await _userDbContext.SaveUserStatus(user);

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "این درگاه فعلاً غیرفعال است. لطفاً یکی از درگاه‌های فعال را انتخاب کنید.",
                    replyMarkup: BuildChargePaymentMethodKeyboard(),
                    cancellationToken: cancellationToken);
                return;
            }
            else if (message.Text == "درگاه ریالی هوش‌پی")
            {
                user.PaymentMethod = "hooshpay";
            }
            else if (message.Text == "درگاه ریالی" || message.Text == "درگاه ریالی (غیرفعال)")
            {
                user.LastStep = "payment_method_selection";
                user.Flow = "charge";
                user.PaymentMethod = string.Empty;
                await _userDbContext.SaveUserStatus(user);

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "درگاه پرداخت ریالی فعلاً غیرفعال است. لطفاً از درگاه‌های فعال استفاده کنید.",
                    replyMarkup: BuildChargePaymentMethodKeyboard(),
                    cancellationToken: cancellationToken);
                return;
            }
            else if (message.Text == "درگاه ارز دیجیتال")
            {
                user.PaymentMethod = "crypto";
            }

            user.LastStep = "confirmation";
            user.Flow = "charge";
            await _userDbContext.SaveUserStatus(user);


            var gatewayName = user.PaymentMethod == "crypto"
                ? "درگاه ارز دیجیتال"
                : user.PaymentMethod == "hooshpay"
                    ? "درگاه ریالی هوش‌پی"
                : user.PaymentMethod == "zibal"
                    ? "درگاه ریالی"
                    : "درگاه پرداخت";

            var text = $"✅ شما مقدار {Convert.ToInt64(user.ConfigLink).FormatCurrency()} را برای شارژ حساب خود وارد کرده‌اید.\n" +
                       $"درگاه انتخابی: {gatewayName}\n" +
                       "برای ادامه، گزینه تایید نهایی را بزنید. در غیر این صورت انصراف را انتخاب کنید.";
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: confirmationKeyboard);
            return;


        }

        else if (message.Text == "💰شارژ حساب کاربری")
        {
            var keyboardButtons = new List<List<KeyboardButton>>();
            var allPrices = _appConfig.Price
                .Concat(_appConfig.PriceCommon)
                .Concat(_appConfig.PriceColleagues)
                .Select(priceConfig => Convert.ToInt64(priceConfig.Price))
                .Where(price => price > 0)
                .Distinct()
                .OrderBy(price => price)
                .ToList();

            for (var index = 0; index < allPrices.Count; index += 4)
            {
                keyboardButtons.Add(allPrices
                    .Skip(index)
                    .Take(4)
                    .Select(price => new KeyboardButton(price.FormatCurrency()))
                    .ToList());
            }

            // Add a "Back" button at the end
            keyboardButtons.Add(new List<KeyboardButton> { new KeyboardButton("بازگشت") });

            var keyboard = new ReplyKeyboardMarkup(keyboardButtons)
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };


            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "enter charge amount", Flow = "charge" });
            var msg = "💰 <b>شارژ کیف پول</b>\n\n" +
                      "👇 از مبلغ‌های پیشنهادی پایین استفاده کنید یا مبلغ دلخواه را به تومان و با عدد وارد کنید.";
            //msg = "برای شارژ حساب کاربری به آیدی زیر پیام دهید: \n @vpnetiran_admin";
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: msg,
                replyMarkup: keyboard, parseMode: ParseMode.Html);


        }

        else if (user.LastStep == "enter charge amount" && user.Flow == "charge")
        {
            // Usage
            bool canConvert = message.Text.PersianNumbersToEnglish().ToValidNumber().TryConvertToLong(out long longValue);
            if (canConvert)
            {
                // use longValue
                user.ConfigLink = longValue.ToString();
                user.LastStep = "payment_method_selection";
                user.Flow = "charge";
                await _userDbContext.SaveUserStatus(user);


                // The user entered a valid number
                var paymentmethod = BuildChargePaymentMethodKeyboard();


                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"✅ شما مقدار {longValue.FormatCurrency()}  را برای شارژ حساب خود وارد کرده اید. \n" + "لطفاً درگاه مورد نظر خود را برای پرداخت آنلاین انتخاب نمائید.",
                    replyMarkup: paymentmethod);
                return;

            }
            else
            {
                // handle the case where it's not a valid long
                await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "enter charge amount", Flow = "charge" });
                var msg = "عدد وارد شده صحیح نمیباشد. لطفاً مبلغ را به تومان و به عدد وارد کنید و گزینه ارسال را بزنید.";
                msg += "\n  در صورتی که میخواهید به منوی اصلی  برگردید روی استارت کلیک کنید /start";
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: msg,
                    replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown);

            }
            return;

        }

        else if (message.Text == "مشاهده وضعیت حساب")
        {
            var text = await GetUserProfileMessage(credUser);
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text,
                replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
        }
        else if (message.Text == "وضعیت اکانت های من")
        {
            if (await _xuiV3BotFlowService.TryHandleMyAccountsAsync(
                botClient,
                message,
                credUser,
                MainReplyMarkupKeyboardFa(),
                cancellationToken))
            {
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                return;
            }

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "لطفاً چند ثانیه صبر کنید. دریافت اطلاعات از سرورها ممکن است لحظاتی طول بکشد ...",
                replyMarkup: new ReplyKeyboardRemove());

            var accounts = await TryGetَAllClient(credUser.TelegramUserId);
            if (accounts.Count < 1)
            {

                await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id,
               text: "شما هنوز هیچ اکانتی از مجموعه ما ندارید.",
               replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                return;
            }
            await SendMessageWithClientInfo(credUser.ChatID, credUser.IsColleague, accounts);


            await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id,
               text: "منوی اصلی",
               replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            return;
        }
        else if (message.Text == "تمدید اکانت")
        {
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Renew Existing Account", Flow = "update" });
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "لطفاً لینک Vmess یا نام اکانت خود را برای ربات ارسال کنید:",
                replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown);

        }
        else if (user.Flow == "update" && user.LastStep == "get-traffic")
        {
            var isSuccessful = int.TryParse(message.Text, out int res);
            if (!isSuccessful)
            {
                await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "خطا! \n ترافیک را به گیگابایت و با اعداد انگلیسی تایپ کنید \n" + "به عنوان مثال 20 معادل بیست گیگابایت خواهد بود \n روی /start برای شروع مجدد کلیک کنید.",
                        replyMarkup: new ReplyKeyboardRemove());
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                return;
            }

            long price = res * 1000;
            if (credUser.AccountBalance >= price)
            {

                user.Flow = "update";
                user.LastStep = "ask_confirmation";
                user._ConfigPrice = price.ToString();
                user.Type = "tunnel";
                user.TotoalGB = res.ToString();
                user.SelectedPeriod = "0 Month";


                await _userDbContext.SaveUserStatus(user);


                // The user entered a valid number
                var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                           {
            new []
            {
                new KeyboardButton("تایید نهایی"),
            },
            new []
            {
                new KeyboardButton("انصراف"),
            },
        });

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"✅ شما اعتبار لازم برای تمدید اکانت مورد نظر را دارید. \n" + " ❕ برای دریافت اکانت، گزینه تایید نهایی را بزنید در غیر این صورت انصراف را انتخاب نمایید.\n",
                    replyMarkup: confirmationKeyboard);
                return;

            }

            else
            {
                await botClient.CustomSendTextMessageAsync(
                                   chatId: message.Chat.Id,
                                   text: $"⛔️ شما اعتبار لازم برای ساخت اکانت مورد نظر را ندارید. \n" + " ❗️ برای شارژ حساب از منوی مربوطه اقدام کنید.\n",
                                   replyMarkup: MainReplyMarkupKeyboardFa());
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

                return;
            }

        }

        else if (message.Text == "تمدید حجمی" && user.Flow == "update")
        {
            user.LastStep = "get-traffic";
            await _userDbContext.SaveUserStatus(user);

            await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "ترافیک مورد نظر را به عدد ارسال کنید. هر گیگابایت معادل 1000 تومان از حساب شما کسر خواهد شد.",
                                replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown);

        }
        else if (user.Flow == "update" && user.LastStep == "ask_confirmation" && (message.Text == "تایید نهایی" || message.Text == "انصراف"))
        {
            await FinalizeRenewCustomerAccount(ActiveBotClient, user, credUser, message);

        }
        else if (user.Flow == "update" && user.LastStep == "set-renew-type" && message.Text.Contains("تمدید"))
        {
            long price = TryParsPrice(message.Text);
            if (price == 0)
            {
                await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: "خطا",
                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                await _userDbContext.ClearUserStatus(user);
                return;
            }

            if (CheckButtonCorrectness(credUser.IsColleague, message.Text, true) == false)
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "خطا",
                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                await _userDbContext.ClearUserStatus(user);
                return;
            }

            if (credUser.AccountBalance >= price)
            {

                await PrepareAccount(message.Text, credUser, user, true);
                user.Flow = "update";
                user.LastStep = "ask_confirmation";
                user._ConfigPrice = price.ToString();
                await _userDbContext.SaveUserStatus(user);


                // The user entered a valid number
                var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                           {
            new []
            {
                new KeyboardButton("تایید نهایی"),
            },
            new []
            {
                new KeyboardButton("انصراف"),
            },
        });

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"✅ شما اعتبار لازم برای تمدید اکانت مورد نظر را دارید. \n" + " ❕ برای دریافت اکانت، گزینه تایید نهایی را بزنید در غیر این صورت انصراف را انتخاب نمایید.\n",
                    replyMarkup: confirmationKeyboard);
                return;

            }

            else
            {
                await botClient.CustomSendTextMessageAsync(
                                   chatId: message.Chat.Id,
                                   text: $"⛔️ شما اعتبار لازم برای ساخت اکانت مورد نظر را ندارید. \n" + " ❗️ برای شارژ حساب از منوی مربوطه اقدام کنید.\n",
                                   replyMarkup: MainReplyMarkupKeyboardFa());
                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

                return;
            }
        }

        else if ((user.Flow == "update" && user.LastStep == "Renew Existing Account") || message.Text.Contains("/renew_"))
        {

            var replyKeboard = PriceReplyMarkupKeyboardFa(credUser.IsColleague, true);

            var input = message.Text;

            if (message.Text.Contains("/renew_"))
                input = message.Text.Replace("/renew_", "");

            if (StartsWithVMessOrVLess(message.Text))
            {
                user.ConfigLink = message.Text;
                await _userDbContext.SaveUserStatus(user);
            }
            else // if (message.Text.StartsWith("/renew_", StringComparison.OrdinalIgnoreCase))
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "لطفاً چند لحظه صبر کنید تا اکانت شما را پیدا کنیم. این عملیات ممکن است چند ثانیه طول بکشد...",
                    replyMarkup: new ReplyKeyboardRemove(), parseMode: ParseMode.Markdown);
                // ممکن است که مشکلی در رابطه با ذخیره وی مس  در  دیتا بیس وجود داشته باشد.
                var client = await ApiService.FetchClientByEmail(input, credUser.TelegramUserId);
                if (client.ClientExtend == null)
                {
                    await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: "اکانت مورد نظر پیدا نشد.",
                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                    await _userDbContext.ClearUserStatus(user);
                    return;

                }
            }

            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "set-renew-type", Flow = "update" });

            await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "یک گزینه را انتخاب نمائید:",
                    replyMarkup: replyKeboard, parseMode: ParseMode.Markdown);

        }

        else if (user.Flow == "create" && user.LastStep == "Create New Account" && message.Text.Contains("خرید"))
        {
            long price = TryParsPrice(message.Text);
            if (price == 0)
            {
                await botClient.CustomSendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: "خطا",
                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                await _userDbContext.ClearUserStatus(user);
                return;
            }

            if (CheckButtonCorrectness(credUser.IsColleague, message.Text, false) == false)
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "خطا",
                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);
                await _userDbContext.ClearUserStatus(user);
                return;
            }

            if (credUser.AccountBalance >= price)
            {
                await PrepareAccount(message.Text, credUser, user, false);
                user.Flow = "create";
                user.LastStep = "ask_confirmation";
                user._ConfigPrice = price.ToString();
                await _userDbContext.SaveUserStatus(user);


                // The user entered a valid number
                var confirmationKeyboard = new ReplyKeyboardMarkup(new[]
                           {
            new []
            {
                new KeyboardButton("تایید نهایی"),
            },
            new []
            {
                new KeyboardButton("انصراف"),
            },
        });

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"✅ شما اعتبار لازم برای ساخت اکانت مورد نظر را دارید. \n" + " ❕ برای دریافت اکانت، گزینه تایید نهایی را بزنید در غیر این صورت انصراف را انتخاب نمایید.\n",
                    replyMarkup: confirmationKeyboard);
                return;

            }
            else
            {
                await botClient.CustomSendTextMessageAsync(
                                   chatId: message.Chat.Id,
                                   text: $"⛔️ شما اعتبار لازم برای ساخت اکانت مورد نظر را ندارید. \n" + " ❗️ برای شارژ حساب از منوی مربوطه اقدام کنید.\n",
                                   replyMarkup: MainReplyMarkupKeyboardFa());
                return;
            }

        }
        else if (user.Flow == "create" && user.LastStep == "ask_confirmation" && (message.Text == "تایید نهایی" || message.Text == "انصراف"))
        {
            await FinalizeCustomerAccount(ActiveBotClient, user, credUser, message);
        }

        else
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.CustomSendTextMessageAsync(
                                       chatId: message.Chat.Id,
                                       text: "مشکلی به وجود امد. لطفاً از اول تلاش کنید.",
                                        replyMarkup: MainReplyMarkupKeyboardFa());

        }

        return;

    }

    private async Task EditMessageWithCallback(ITelegramBotClient botClient, long chatid, int messageId)
    {
        //string payment_status = (await new NowPayments().GetPaymentStatus(paymentID)).payment_status;

        DateTime d = DateTime.Now;
        PersianCalendar pc = new PersianCalendar();
        string persianDateTime = string.Format("{0}/{1}/{2} {3}:{4}:{5}:{6} ", pc.GetYear(d), pc.GetMonth(d), pc.GetDayOfMonth(d), pc.GetHour(d), pc.GetMinute(d), pc.GetSecond(d), pc.GetMilliseconds(d));


        InlineKeyboardMarkup paid = new(new[]
                       {
                 // first row
            new []
    {
                InlineKeyboardButton.WithUrl(text:"پرداخت شده ✅",url:"google.com"),

    },
    // second row
    // new []
    // {
    //     InlineKeyboardButton.WithCallbackData(text:"❓بررسی پرداخت"+"\n" +persianDateTime,callbackData:$"PaymentID{paymentID}"),
    //     //InlineKeyboardButton.WithCallbackData(text: "2.2", callbackData: "22"),
    // },
});


        //         InlineKeyboardMarkup notpaid = new(new[]
        //                            {
        //                  // first row
        //                   new []
        //     {
        //                 // InlineKeyboardButton.WithCallbackData(text:payment_status + new Random().Next().ToString(),callbackData:$"PaymentID{paymentID}"),
        //                 InlineKeyboardButton.WithCallbackData(text:payment_status ,callbackData:$"PaymentID{paymentID}"),

        //     },

        //     // second row
        //     new []
        //     {
        //         InlineKeyboardButton.WithCallbackData(text:"❓بررسی پرداخت"+"\n"+persianDateTime,callbackData:$"PaymentID{paymentID}"),
        //         //InlineKeyboardButton.WithCallbackData(text: "2.2", callbackData: "22"),
        //     },
        // });


        // if (payment_status == "finished")
        await botClient.EditMessageReplyMarkupAsync(
                  chatId: chatid,
                  messageId: messageId,
                  replyMarkup: paid);
        // else await botClient.EditMessageReplyMarkupAsync(
        // chatId: chatid,
        // messageId: messageId,
        // replyMarkup: notpaid,
        // cancellationToken: cancellationToken);



    }

    private async Task FinalizeRenewCustomerAccount(ITelegramBotClient botClient, User user, CredUser credUser, Message message)
    {
        if (message.Text == "انصراف")
        {
            await _userDbContext.ClearUserStatus(user);
            return;
        }
        await botClient.CustomSendTextMessageAsync(
                           chatId: message.Chat.Id,
                           text: "لطفاً تا تمدید شدن اکانت چند لحظه صبر کنید ...",
                            replyMarkup: new ReplyKeyboardRemove());


        var ready = await _userDbContext.IsUserReadyToCreate(message.From.Id);

        if (!ready)
        {
            await botClient.CustomSendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "مشخصات اکانت کامل نیست. لطفاً مراحل دریافت اکانت را به طور کامل طی کنید..",
                    replyMarkup: MainReplyMarkupKeyboardFa());
            await _userDbContext.ClearUserStatus(user);
            return;
        }

        ClientExtend client = await TryGetClient(user.ConfigLink);
        if (client == null)
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            await botClient.CustomSendTextMessageAsync(
                          chatId: message.Chat.Id,
                          text: "مشکلی با لینک vmess ارسالی شما وجود دارد. سعی کنید ابتدا لینک سالم را برای ربات بفرستید و درصورت عدم رفع مشکل به پشتیبانی پیام دهید.",
                           replyMarkup: new ReplyKeyboardRemove());
            return;
        }

        if (client != null)
        {
            ServerInfo findedServer = null;
            string findedcountry = null;
            AccountDtoUpdate accountDto = null;
            var serversJson = ReadJsonFile.ReadJsonAsString();
            var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

            if (user.ConfigLink.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var vmess = VMessConfiguration.DecodeVMessLink(user.ConfigLink);

                // Iterate over the dictionary
                foreach (var kvp in servers)
                {
                    string country = kvp.Key;
                    ServerInfo serverInfo = kvp.Value;
                    if (serverInfo.VmessTemplate.Add == vmess.Add)
                    {
                        serverInfo.Inbounds = new List<Inbound> { serverInfo.Inbounds.FirstOrDefault(i => i.Port.ToString() == vmess.Port) };
                        serverInfo.VmessTemplate.Port = vmess.Port;
                        findedServer = serverInfo;
                        findedcountry = country;
                    }
                }

                accountDto = new AccountDtoUpdate { TelegramUserId = message.From.Id, Client = client, ServerInfo = findedServer, SelectedCountry = findedcountry, SelectedPeriod = user.SelectedPeriod, AccType = "tunnel", TotoalGB = user.TotoalGB, ConfigLink = user.ConfigLink };
            }
            await _userDbContext.SaveUserStatus(new User { Id = user.Id, SelectedCountry = findedcountry });
            var result = await UpdateAccount(accountDto);

            if (result)
            {
                user = await _userDbContext.GetUserStatus(user.Id);

                if (client == null)
                {
                    await botClient.CustomSendTextMessageAsync(
                                  chatId: message.Chat.Id,
                                  text: "متاسفانه مشکلی در ساخت اکانت شما به وجود آمد. مجدداً دقایقی دیگر تلاش کنید",
                                   replyMarkup: MainReplyMarkupKeyboardFa());
                    await _userDbContext.ClearUserStatus(user);
                    return;
                }

                var msg = CaptionForRenewAccount(user, expirationDateUTC: client.ExpiryTime, showTraffic: false);

                await botClient.SendPhotoAsync(message.Chat.Id, InputFile.FromStream(new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(user.ConfigLink, 200))), caption: msg, parseMode: ParseMode.Markdown);
                // .GetAwaiter()
                // .GetResult();

                await botClient.CustomSendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "بازگشت به منوی اصلی",
                    replyMarkup: MainReplyMarkupKeyboardFa());

                long beforeBalance = credUser.AccountBalance;
                await _credentialsDbContext.Pay(credUser, Convert.ToInt64(user._ConfigPrice));
                long afterBalance = await _credentialsDbContext.GetAccountBalance(credUser.TelegramUserId);
                await _walletLedgerService.RecordAsync(
                    credUser.TelegramUserId,
                    WalletLedgerDirections.Debit,
                    Convert.ToInt64(user._ConfigPrice),
                    beforeBalance,
                    afterBalance,
                    WalletLedgerReasons.AccountRenew,
                    provider: "wallet",
                    referenceType: "legacy-renew",
                    referenceId: user.ConfigLink,
                    description: "Legacy account renewal",
                    cancellationToken: CancellationToken.None);

                var logMesseage = "تمدید \n" + $"یوزر `{credUser.TelegramUserId}` \n {credUser} \n با مبلغ {user._ConfigPrice}" + " اکانت زیر را خریداری کرد" + $"\n موجودی قبل از خرید {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از خرید {afterBalance.FormatCurrency()}" + " \n \n" + msg;

                if (user.ConfigPrice > 1000) _logger.LogInformation(logMesseage.EscapeMarkdown());

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "✅ تمدید با موفقیت انجام شد.\n\n" +
                          BuildPlainBalanceDeductionText(beforeBalance, Convert.ToInt64(user._ConfigPrice), afterBalance),
                    replyMarkup: MainReplyMarkupKeyboardFa());

                if (user.SelectedPeriod == "1 Day")
                {
                    user.LastFreeAcc = DateTime.Now;
                    await _userDbContext.SaveUserStatus(user);
                }

            }
        }
        else
        {
            // Handle the case where the selected country is not found in the servers.json file
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "مشکلی در بازیابی اطاعات اکانت ارسالی شما برای عملیات تمدید وجود دارد.",
                replyMarkup: MainReplyMarkupKeyboardFa());
            await _userDbContext.ClearUserStatus(user);

        }

        await _userDbContext.ClearUserStatus(user);

    }
    private async Task FinalizeCustomerAccount(ITelegramBotClient botClient, User user, CredUser credUser, Message message)
    {
        if (message.Text == "انصراف")
        {
            await _userDbContext.ClearUserStatus(user);
            return;
        }

        await botClient.CustomSendTextMessageAsync(
                           chatId: message.Chat.Id,
                           text: "لطفاً تا ساخته شدن اکانت چند لحظه صبر کنید ...",
                            replyMarkup: new ReplyKeyboardRemove());


        var ready = await _userDbContext.IsUserReadyToCreate(message.From.Id);
        if (!ready) await botClient.CustomSendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "مشخصات اکانت کامل نیست. لطفاً مراحل دریافت اکانت را به طور کامل طی کنید..",
                    replyMarkup: MainReplyMarkupKeyboardFa()); ;

        if (!ready)
        {
            await _userDbContext.ClearUserStatus(user);
            return;
        }

        // Access the server information from the servers.json file
        var serversJson = ReadJsonFile.ReadJsonAsString();
        var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

        if (servers.ContainsKey(user.SelectedCountry))
        {
            var serverInfo = servers[user.SelectedCountry];

            AccountDto accountDto = new AccountDto { TelegramUserId = message.From.Id, IsColleague = credUser.IsColleague, AccountCounter = user.AccountCounter + 1, ServerInfo = serverInfo, SelectedCountry = user.SelectedCountry, SelectedPeriod = user.SelectedPeriod, AccType = user.Type, TotoalGB = user.TotoalGB };

            var result = await CreateAccount(accountDto);

            if (result)
            {
                user = await _userDbContext.GetUserStatus(user.Id);

                ClientExtend client = await TryGetClient(user.ConfigLink);

                if (client == null || client?.Enable == false)
                {
                    await botClient.CustomSendTextMessageAsync(
                                  chatId: message.Chat.Id,
                                  text: "متاسفانه مشکلی در ساخت اکانت شما به وجود آمد. مجدداً دقایقی دیگر تلاش کنید",
                                   replyMarkup: MainReplyMarkupKeyboardFa());
                    await _userDbContext.ClearUserStatus(user);
                    return;
                }


                var msg = CaptionForAccountCreation(user, language: "fa", showTraffic: false);

                await botClient.SendPhotoAsync(message.Chat.Id, InputFile.FromStream(new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(user.ConfigLink, 200))), caption: msg, parseMode: ParseMode.Markdown);
                // .GetAwaiter()
                // .GetResult();

                await botClient.CustomSendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "بازگشت به منوی اصلی",
                    replyMarkup: MainReplyMarkupKeyboardFa());

                long beforeBalance = credUser.AccountBalance;
                await _credentialsDbContext.Pay(credUser, Convert.ToInt64(user._ConfigPrice));
                long afterBalance = await _credentialsDbContext.GetAccountBalance(credUser.TelegramUserId);
                await _walletLedgerService.RecordAsync(
                    credUser.TelegramUserId,
                    WalletLedgerDirections.Debit,
                    Convert.ToInt64(user._ConfigPrice),
                    beforeBalance,
                    afterBalance,
                    WalletLedgerReasons.AccountPurchase,
                    provider: "wallet",
                    referenceType: "legacy-purchase",
                    referenceId: user.Email,
                    description: "Legacy account purchase",
                    cancellationToken: CancellationToken.None);

                var logMesseage = $"یوزر `{credUser.TelegramUserId}` \n {credUser} \n با مبلغ {user._ConfigPrice}" + " اکانت زیر را خریداری کرد" + $"\n موجودی قبل از خرید {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از خرید {afterBalance.FormatCurrency()}" + " \n \n" + msg;

                if (user.ConfigPrice > 1000) _logger.LogInformation(logMesseage.EscapeMarkdown());

                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "✅ خرید با موفقیت انجام شد.\n\n" +
                          BuildPlainBalanceDeductionText(beforeBalance, Convert.ToInt64(user._ConfigPrice), afterBalance),
                    replyMarkup: MainReplyMarkupKeyboardFa());

                if (user.SelectedPeriod == "1 Day")
                {
                    user.LastFreeAcc = DateTime.Now;

                    await _userDbContext.SaveChangesAsync();
                }
                else
                {
                    user.AccountCounter = user.AccountCounter + 1;
                    await _userDbContext.SaveUserStatus(user);
                    await _userDbContext.ClearUserStatus(new User { Id = user.Id });
                }

            }
            else
            {
                await botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "متاسفانه مشکلی در ساخت اکانت شما به وجود آمد. مجدداً دقایقی دیگر تلاش کنید",
                    replyMarkup: MainReplyMarkupKeyboardFa());
            }
        }
        else
        {
            // Handle the case where the selected country is not found in the servers.json file
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"اطلاعات سرور مورد نظر پیدا نشد.",
                replyMarkup: MainReplyMarkupKeyboardFa());
            await _userDbContext.ClearUserStatus(user);

        }
        await _userDbContext.ClearUserStatus(user);

    }
    /// <summary>
    /// Normalizes configured mandatory-join channel values for Telegram membership checks and join buttons.
    /// </summary>
    /// <param name="channelIds">
    /// Channel identifiers from the current bot configuration. Each item may be a bare username, an <c>@username</c>,
    /// a <c>https://t.me/...</c> link, or a numeric Telegram channel id.
    /// </param>
    /// <returns>
    /// A list of normalized channel descriptors. <c>ChatId</c> is safe to pass to <c>GetChatMember</c>; <c>Url</c>
    /// is safe to use in an inline URL button when the source value can be represented as a public Telegram link.
    /// </returns>
    /// <remarks>
    /// The old mandatory-join path prepended <c>@</c> after a simple URL replacement, which could produce invalid
    /// values such as <c>@@channel</c>. This helper keeps membership checks and button URLs consistent for every
    /// owned bot, including bot-specific channel settings restored from the database.
    /// </remarks>
    private static IReadOnlyList<(string ChatId, string Url, string Label)> BuildMandatoryJoinChannels(IEnumerable<string> channelIds)
    {
        return (channelIds ?? Enumerable.Empty<string>())
            .Select(NormalizeMandatoryJoinChannel)
            .Where(channel => !string.IsNullOrWhiteSpace(channel.ChatId))
            .ToList();
    }

    /// <summary>
    /// Converts one mandatory-join channel setting into Telegram API and button-safe values.
    /// </summary>
    /// <param name="channelId">
    /// Raw channel value from configuration or the current bot database record. The value is optional; blank values
    /// return an empty descriptor and are ignored by the caller.
    /// </param>
    /// <returns>
    /// A normalized tuple containing the Telegram chat id for membership checks, a public URL when one can be built,
    /// and a readable label for the inline join button.
    /// </returns>
    /// <remarks>
    /// Public channel usernames are represented as <c>@username</c> for the Bot API and <c>https://t.me/username</c>
    /// for user-facing buttons. Numeric private channel ids can be checked by Telegram but cannot automatically be
    /// converted into a public join link.
    /// </remarks>
    private static (string ChatId, string Url, string Label) NormalizeMandatoryJoinChannel(string channelId)
    {
        var value = (channelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return (string.Empty, string.Empty, string.Empty);

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "t.me", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "telegram.me", StringComparison.OrdinalIgnoreCase)))
        {
            value = uri.AbsolutePath.Trim('/');
        }

        value = value.Trim().TrimStart('/');
        if (value.Contains('?'))
            value = value[..value.IndexOf('?')];

        value = value.TrimStart('@');
        if (string.IsNullOrWhiteSpace(value))
            return (string.Empty, string.Empty, string.Empty);

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return (value, string.Empty, value);

        var chatId = "@" + value;
        return (chatId, $"https://t.me/{value}", chatId);
    }

    /// <summary>
    /// Checks whether a Telegram user is a member of all mandatory channels for the active bot.
    /// </summary>
    /// <param name="channelIDs">
    /// Normalized Telegram chat identifiers for mandatory channels. Values should come from
    /// <see cref="BuildMandatoryJoinChannels(IEnumerable&lt;string&gt;)"/> so usernames are not double-prefixed with <c>@</c>.
    /// </param>
    /// <param name="userId">
    /// Numeric Telegram user id from the incoming update sender. This is the account whose channel membership is checked.
    /// </param>
    /// <returns>
    /// <c>true</c> only when the user is visible as a non-left, non-kicked member of every configured mandatory channel;
    /// <c>false</c> when the user is missing from at least one channel or when the active bot cannot verify membership.
    /// </returns>
    /// <remarks>
    /// Telegram may return <c>chat not found</c> or <c>member list is inaccessible</c> when the bot is not added to the
    /// channel or lacks the required channel access. The method fails closed in that case so users cannot bypass the
    /// mandatory-join gate because of a bad channel setting.
    /// </remarks>
    private async Task<bool> isJoinedToChannel(IEnumerable<string> channelIDs, long userId)
    {
        bool isJoined = true;

        foreach (var c in channelIDs)
        {
            if (string.IsNullOrWhiteSpace(c))
            {
                isJoined = false;
                continue;
            }

            try
            {
                var chatMember = await ActiveBotClient.GetChatMemberAsync(c, userId);
                //var st = chatMember.Status.ToString();
                // if (st == "null" || st == "" || st == "Left")
                if (chatMember != null && chatMember.Status != ChatMemberStatus.Left && chatMember.Status != ChatMemberStatus.Kicked)
                {
                    isJoined = isJoined && true;
                }
                else
                {
                    isJoined = isJoined && false;
                }
            }
            catch (ApiRequestException ex) when (IsMandatoryJoinChannelAccessError(ex))
            {
                isJoined = false;
                _logger.LogWarning(
                    ex,
                    "Mandatory join check failed closed because the current bot cannot access channel members. BotId={BotId}, BotUsername={BotUsername}, Channel={Channel}, UserId={UserId}",
                    BotContextAccessor.CurrentBotId,
                    BotContextAccessor.CurrentBotUsername,
                    c,
                    userId);
            }
        }

        return isJoined;

    }

    /// <summary>
    /// Detects Telegram Bot API errors that mean the bot could not verify mandatory channel membership.
    /// </summary>
    /// <param name="ex">Telegram API exception thrown by <c>GetChatMember</c>; may be <c>null</c>.</param>
    /// <returns>
    /// <c>true</c> when the error is one of Telegram's known inaccessible-channel responses; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Callers use this only to choose the safe failure path for mandatory join. The error is still logged by the
    /// membership checker because it usually means the bot must be added to the channel or promoted.
    /// </remarks>
    private static bool IsMandatoryJoinChannelAccessError(ApiRequestException ex)
    {
        var message = ex?.Message ?? string.Empty;
        return ex?.ErrorCode == 400 &&
               (message.Contains("member list is inaccessible", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("chat not found", StringComparison.OrdinalIgnoreCase));
    }
    private async Task PrepareAccount(string messageText, CredUser credUser, User user, bool isForRenew)
    {

        var priceConfig = GetPriceConfig(messageText, credUser, isForRenew);
        ServerInfo randomServerInfo = GetRandomServer();
        var serverInfo = randomServerInfo;

        var serversJson = ReadJsonFile.ReadJsonAsString();
        var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

        var pair = servers.FirstOrDefault(kv => kv.Value != null &&
                                                   typeof(ServerInfo).GetProperty("Url")?.GetValue(kv.Value)?.ToString() == serverInfo.Url);


        user.Type = "tunnel";
        user.TotoalGB = priceConfig.Traffic.ToString();
        user.SelectedPeriod = priceConfig.Duration;
        user.SelectedCountry = pair.Key;
        await _userDbContext.SaveUserStatus(user);

        // AccountDto accountDto = new AccountDto { TelegramUserId = user.Id, ServerInfo = serverInfo, SelectedCountry = pair.Key, SelectedPeriod = priceConfig.Duration, AccType = user.Type, TotoalGB = priceConfig.Traffic.ToString() };

        return;
    }

    private ServerInfo GetRandomServer()
    {
        var serversJson = ReadJsonFile.ReadJsonAsString();
        var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);


        List<ServerInfo> serverInfos = servers.Values.ToList();
        // List<ServerInfo> serverInfos = new List<ServerInfo>();
        var weightedItems = serverInfos.Select(i => new WeightedItem<ServerInfo>(i, i.Chance));



        ServerInfo selected = RouletteWheel.Spin(weightedItems.ToList<WeightedItem<ServerInfo>>());
        return selected;
        // Console.WriteLine($"Selected item: {selected}");
    }
    public void TestGetRandomServerHits()
    {
        var hitDictionary = new Dictionary<string, int>();
        int numberOfTests = 100;

        for (int i = 0; i < numberOfTests; i++)
        {
            ServerInfo server = GetRandomServer();

            if (hitDictionary.ContainsKey(server.Name))
            {
                hitDictionary[server.Name]++;
            }
            else
            {
                hitDictionary.Add(server.Name, 1);
            }
        }

        // Calculate and print the percentage of hits for each server
        foreach (var entry in hitDictionary)
        {
            double percentage = (double)entry.Value / numberOfTests * 100;
            Console.WriteLine($"Server: {entry.Key}, Hits: {entry.Value}, Percentage: {percentage}%");
        }
    }
    private bool CheckButtonCorrectness(bool isColleague, string text, bool isForRenew)
    {
        return GetPrices(isColleague, isForRenew).Contains(text);
    }

    public async Task SendMessageWithClientInfo(ChatId chatId, bool isColleague, List<ClientExtend> clients)
    {
        const int MaxMessageLength = 4096; // Telegram max message length
        StringBuilder messageBuilder = new StringBuilder();
        var sentMessages = 0;
        string clientInfo = "وضعیت اکانت های شما به شرح زیر است: \n";
        foreach (var client in clients)
        {
            clientInfo = $"👤 نام: `{client.Email}`\n";
            // $"- Name: {client.Name}\n" +
            // $"- Subscription: {client.}\n" +


            if (client.ExpiryTimeRaw > 0)
            {
                clientInfo += $"📅 انقضاء: {client.ExpiryTime.AddMinutes(210).ConvertToHijriShamsi()}\n";
                if (client.ExpiryTime < DateTime.UtcNow)
                    clientInfo += $"\u200F🚫 منقضی شده است. \n";
                else if ((client.ExpiryTime - DateTime.UtcNow) <= TimeSpan.FromDays(5))
                    clientInfo += $"\u200F❕⌛️ روزهای باقی‌مانده: " + (client.ExpiryTime - DateTime.UtcNow).Days + " روز \n";

                else
                    clientInfo += $"\u200F⏳ روزهای باقی‌مانده: " + (client.ExpiryTime - DateTime.UtcNow).Days + " روز \n";
            }
            else
                clientInfo += $"\u200F⌛️ روزهای باقی‌مانده: " + (client.ExpiryTime - DateTime.UtcNow).Days + " روز پس از برقراری اولین اتصال \n";



            if (isColleague)
            {

                double totalUsed = (client.Up + client.Down).ConvertBytesToGB();
                if (((client.Up + client.Down) / client.TotalGB) < 0.9)
                    clientInfo += "\u200F" + "🔋 میزان مصرف : " + $"{totalUsed:F2}" + $" از {client.TotalGB.ConvertBytesToGB()} گیگابایت" + "\n";
                else
                    clientInfo += "\u200F" + "🪫 میزان مصرف: " + $"{totalUsed:F2}" + $" از {client.TotalGB.ConvertBytesToGB()} گیگابایت" + "\n";

                if (client.Enable)
                    clientInfo += $"\u200F✔️ فعال  \n" + "\u200F غیر فعال سازی ⬅️" + $"/disable_{client.Email} \n";

                else
                    clientInfo += $"\u200F🚫 غیرفعال  \n" + "\u200F فعالسازی ⬅️" + $"/enable_{client.Email} \n";

            }
            else
            {
                if ((client.Up + client.Down) >= client.TotalGB && (client.ExpiryTime > DateTime.UtcNow))
                    clientInfo += "\u200F" + $"❗️مولتی آیپی \n";
            }


            // tamdid 
            clientInfo += "\u200F" + "🔄 تمدید ⬅️  " + $"/renew_{client.Email} \n";
            // /renew_{client.Email}
            clientInfo += "\u200F" + "🔗 ساب لینک: \n" + $"`{client.SubId}` \n";
            //clientInfo += ":میزان مصرف" + client.TotalUsedTrafficInGB + "\n";

            clientInfo += "___________________________\n";

            // Check if adding this client's info will exceed the Telegram message length limit
            if (messageBuilder.Length + clientInfo.Length > MaxMessageLength)
            {
                // Send the current message
                await ActiveBotClient.CustomSendTextMessageAsync(chatId, messageBuilder.ToString().EscapeMarkdown(), parseMode: ParseMode.Markdown);
                messageBuilder.Clear(); // Clear the builder for the next message
            }

            // Add the current client's info to the message builder
            messageBuilder.Append(clientInfo);
        }

        // Send any remaining info
        if (messageBuilder.Length > 0)
        {
            await ActiveBotClient.SendTextMessageAsync(chatId, messageBuilder.ToString().EscapeMarkdown(), parseMode: ParseMode.Markdown);
        }
    }

    /// <summary>
    /// Handles Telegram polling errors without allowing logging failures to stop bot receivers.
    /// </summary>
    /// <param name="botClient">Telegram client whose polling loop reported the error.</param>
    /// <param name="exception">Exception raised by Telegram polling or by an update handler.</param>
    /// <param name="cancellationToken">Polling cancellation token supplied by Telegram.Bot.</param>
    /// <returns>A task that completes after best-effort logging.</returns>
    /// <remarks>
    /// The Telegram polling library routes unhandled update-handler exceptions here. Per-user delivery errors,
    /// such as a customer blocking an owned or tenant bot, are non-actionable delivery noise and must not crash the
    /// process or spam the private Telegram logger channel. The method catches its own logging failures so the
    /// error callback remains non-throwing.
    /// Telegram 5xx polling bursts are treated as transient provider noise: they are written only as local warning
    /// activity entries and are not sent to the operational Telegram logger channel.
    /// </remarks>
    public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        try
        {
            if (IsUserDeliveryPollingError(exception))
            {
                await _userActivityLog.LogWarningAsync(
                    "telegram_polling_delivery_skipped",
                    null,
                    false,
                    new Dictionary<string, object>
                    {
                        ["source"] = "Telegram polling",
                        ["telegramError"] = exception.Message ?? string.Empty
                    },
                    cancellationToken);
            }
            else if (IsTransientTelegramPollingError(exception))
            {
                await _userActivityLog.LogWarningAsync(
                    "telegram_polling_transient_skipped",
                    null,
                    false,
                    new Dictionary<string, object>
                    {
                        ["source"] = "Telegram polling",
                        ["telegramError"] = exception.Message ?? string.Empty
                    },
                    cancellationToken);
            }
            else
            {
                await _userActivityLog.LogErrorAsync(
                    "telegram_polling_error",
                    exception,
                    null,
                    false,
                    new Dictionary<string, object>
                    {
                        ["source"] = "Telegram polling"
                    },
                    cancellationToken);
            }
        }
        catch (Exception logException)
        {
            _logger.LogError(
                logException,
                "Failed to write Telegram polling error activity log. originalError={OriginalError}",
                exception.Message);
        }

        var isDeliveryError = IsUserDeliveryPollingError(exception);
        var isTransientTelegramError = IsTransientTelegramPollingError(exception);
        if (isDeliveryError)
            _logger.LogDebug("Telegram polling delivery error ignored. {ErrorMessage}", ErrorMessage);
        else if (isTransientTelegramError)
            _logger.LogDebug("Transient Telegram polling error ignored. {ErrorMessage}", ErrorMessage);
        else
            _logger.LogError(exception, "Telegram polling error. {ErrorMessage}", ErrorMessage);

        Console.WriteLine(isDeliveryError || isTransientTelegramError ? $"Telegram polling skipped: {exception.Message}" : ErrorMessage);
    }

    /// <summary>
    /// Detects Telegram delivery failures that definitively identify an unreachable user chat.
    /// </summary>
    /// <param name="exception">Exception raised by Telegram polling or update handling.</param>
    /// <returns>
    /// <c>true</c> when the error is a blocked-user, deactivated-user, forbidden, or chat-not-found response;
    /// otherwise <c>false</c>. Transport timeouts are intentionally excluded.
    /// </returns>
    /// <remarks>
    /// These responses prove that Telegram cannot deliver to that chat. A request timeout proves only a temporary
    /// transport failure and is instead handled by <see cref="IsExternalOperationTimeout" />, without changing or
    /// mislabeling user reachability.
    /// </remarks>
    private static bool IsUserDeliveryPollingError(Exception exception)
    {
        if (exception is not ApiRequestException apiException)
            return false;

        var message = apiException.Message ?? string.Empty;
        return apiException.ErrorCode == 403 ||
               message.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects temporary Telegram 5xx polling responses that should not be forwarded to the operational log channel.
    /// </summary>
    /// <param name="exception">Exception raised by Telegram polling or update handling.</param>
    /// <returns>
    /// <c>true</c> when Telegram returned a transient gateway/server response such as 502 Bad Gateway; otherwise
    /// <c>false</c>.
    /// </returns>
    /// <remarks>
    /// These errors happen before a user update is available and are retried by the polling loop. They are written
    /// to the local activity file as warnings but logged only at debug level through <see cref="ILogger"/> so the
    /// private Telegram logger channel does not receive repeated non-actionable 502 messages.
    /// </remarks>
    private static bool IsTransientTelegramPollingError(Exception exception)
    {
        if (exception is not ApiRequestException apiException)
            return false;

        var message = apiException.Message ?? string.Empty;
        return apiException.ErrorCode is 500 or 502 or 503 or 504 ||
               message.Contains("bad gateway", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects non-shutdown transient failures from external systems used while handling a Telegram update.
    /// </summary>
    /// <param name="exception">
    /// Exception caught by the update wrapper. This is commonly a <see cref="TaskCanceledException"/> from
    /// <see cref="HttpClient"/> when the XUI panel times out, or an <see cref="HttpRequestException"/> when the panel
    /// or Telegram connection fails with a transient TLS/socket problem.
    /// </param>
    /// <param name="cancellationToken">
    /// Polling cancellation token from Telegram.Bot. When this token is cancelled, the timeout belongs to shutdown
    /// and must not be swallowed as a recoverable business failure.
    /// </param>
    /// <returns>
    /// <c>true</c> when the exception represents a transient external failure that should be logged and handled
    /// without stopping the receiver; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Gozargah and other owned bots can hit slow 3x-ui panels. Timeouts, TLS bad-record errors, and temporary
    /// gateway failures should produce a user-facing retry message, not a polling failure that stops the active bot.
    /// </remarks>
    private static bool IsExternalOperationTimeout(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (exception is TimeoutException)
            return true;

        if (exception is TaskCanceledException)
            return true;

        if (ApiServicev3.IsTransientXuiTransportException(exception, cancellationToken))
            return true;

        var message = exception?.Message ?? string.Empty;
        return message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("decryption failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("bad record mac", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SSL_ERROR_SSL", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sends a short best-effort timeout notice to the Telegram chat that triggered the update.
    /// </summary>
    /// <param name="botClient">Telegram client for the active owned or tenant bot.</param>
    /// <param name="update">Update whose message or callback chat should receive the notice.</param>
    /// <param name="cancellationToken">Polling cancellation token for the Telegram send attempt.</param>
    /// <returns>A task that completes after the notification is sent or skipped.</returns>
    /// <remarks>
    /// This method intentionally swallows Telegram delivery failures. It is already running inside an exception
    /// handler, so a blocked user or another send timeout must not create a second polling error.
    /// </remarks>
    private async Task SendBestEffortTimeoutMessageAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        var chatId = update?.Message?.Chat.Id ?? update?.CallbackQuery?.Message?.Chat.Id;
        if (!chatId.HasValue)
            return;

        try
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId.Value,
                text: "ارتباط با پنل یا تلگرام بیش از حد طول کشید. لطفاً چند دقیقه دیگر دوباره تلاش کنید.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (IsUserDeliveryPollingError(ex) || IsExternalOperationTimeout(ex, cancellationToken))
        {
            _logger.LogWarning(
                "Skipped timeout notification because Telegram could not deliver it. botId={BotId}, chatId={ChatId}, telegramError={TelegramError}",
                BotContextAccessor.CurrentBotId,
                chatId.Value,
                ex.Message);
        }
    }

    async Task<string> GetUserProfileMessage(CredUser credUser)
    {
        var _credUser = await _credentialsDbContext.GetUserStatus(credUser);

        var text = "✅ مشخصات اکانت شما به شرح زیر میباشد:  \n";
        text += $"👤نام حساب: {_credUser.FirstName} {_credUser.LastName} \n";
        if (!string.IsNullOrEmpty(_credUser.Username))
            text += $"\u200F🆔 آیدی: @{_credUser.Username} \n";
        text += $"\u200Fℹ️ آیدی عددی: `{_credUser.TelegramUserId}` \n";
        text += $"‌💰اعتبار حساب: {_credUser.AccountBalance.FormatCurrency()}\n";
        if (_credUser.IsColleague)
        {
            text += await BuildGozargahSiteWalletStatusLineAsync(_credUser.TelegramUserId);
            text += $"‌🧰 نوع: اکانت شما از نوع همکار 💎می‌باشد. \n";
        }
        else
        {
            text += "‌🧰 نوع: اکانت شما از نوع کاربر عادی می‌باشد. \n";
        }
        return text.EscapeMarkdown();
    }

    /// <summary>
    /// Builds the optional Gozargah website wallet line shown in colleague account status messages.
    /// </summary>
    /// <param name="telegramUserId">
    /// Numeric Telegram user id of the colleague. The Gozargah website API uses the same id to find the linked website user.
    /// </param>
    /// <returns>
    /// A human-readable status line containing the website wallet balance, ban status, or a short unavailable message.
    /// The returned text is not escaped; <see cref="GetUserProfileMessage(CredUser)"/> escapes the full profile message.
    /// </returns>
    /// <remarks>
    /// This method is display-only. It never debits either wallet and never blocks the owned-bot status flow if the
    /// website API is unavailable.
    /// </remarks>
    private async Task<string> BuildGozargahSiteWalletStatusLineAsync(long telegramUserId)
    {
        if (_gozargahSiteApiClient == null || !_gozargahSiteApiClient.IsConfigured())
            return string.Empty;

        try
        {
            var siteUser = await _gozargahSiteApiClient.GetUserAsync(telegramUserId);
            if (!siteUser.Success || siteUser.Data == null)
            {
                var statusText = IsGozargahSiteUserNotConnectedMessage(siteUser.Message)
                    ? "متصل نشده"
                    : $"در دسترس نیست ({siteUser.Message ?? "کاربر پیدا نشد"})";
                return $"🌐 موجودی سایت گذرگاه: {statusText}\n";
            }

            var banText = siteUser.Data.IsBanned ? " - مسدود در سایت" : string.Empty;
            return $"🌐 موجودی سایت گذرگاه: {siteUser.Data.Wallet.FormatCurrency()}{banText}\n";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Gozargah site wallet for colleague profile. userId={TelegramUserId}", telegramUserId);
            return "🌐 موجودی سایت گذرگاه: خطا در دریافت اطلاعات سایت\n";
        }
    }

    /// <summary>
    /// Detects the normal Gozargah website response for a Telegram user that has not connected a site account.
    /// </summary>
    /// <param name="message">
    /// Message returned by the Gozargah website <c>get_user</c> response wrapper. It may be a plain API message or
    /// the local HTTP wrapper text, for example <c>HTTP 404: {"success":false,"message":"not found"}</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> when the message represents the expected "not connected/not found" state; otherwise <c>false</c>
    /// so real API failures can still be shown as unavailable.
    /// </returns>
    /// <remarks>
    /// The profile page is customer-facing. A website 404 is not an operational error here; it only means the
    /// Telegram user has not linked or registered a Gozargah website wallet, so the bot should show "متصل نشده".
    /// </remarks>
    private static bool IsGozargahSiteUserNotConnectedMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("پیدا نشد", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPlainBalanceDeductionText(long beforeBalance, long deductedAmount, long afterBalance)
    {
        return $"💳 موجودی قبل: {beforeBalance.FormatCurrency()}\n" +
               $"💸 مبلغ کسر شده: {deductedAmount.FormatCurrency()}\n" +
               $"💰 موجودی باقی‌مانده: {afterBalance.FormatCurrency()}";
    }

    /// <summary>
    /// Writes the private-channel audit log for a manual admin wallet adjustment.
    /// </summary>
    /// <param name="actor">
    /// Telegram sender who confirmed the admin operation. The value comes from the update sender and is converted
    /// to a clickable audit identity without storing or exposing any bot token.
    /// </param>
    /// <param name="target">
    /// Credentials user whose shared bot-wallet balance was changed by the admin operation.
    /// </param>
    /// <param name="isCredit">
    /// <c>true</c> when the admin added balance; <c>false</c> when the admin deducted balance.
    /// </param>
    /// <param name="amountToman">Adjustment amount in Iranian toman.</param>
    /// <param name="beforeBalance">Target bot-wallet balance in toman before the adjustment.</param>
    /// <param name="afterBalance">Target bot-wallet balance in toman after the adjustment.</param>
    /// <remarks>
    /// The wallet mutation and ledger write happen before this method is called. This method only mirrors the
    /// completed financial action to the central private logger channel so admins can audit manual changes later.
    /// </remarks>
    private void LogAdminWalletAdjustment(
        Telegram.Bot.Types.User actor,
        CredUser target,
        bool isCredit,
        long amountToman,
        long beforeBalance,
        long afterBalance)
    {
        var actorUser = BuildCredUserFromTelegramActor(actor);
        var actionText = isCredit ? "افزایش موجودی دستی" : "کسر موجودی دستی";
        var directionIcon = isCredit ? "➕🟢" : "➖🔴";

        var message =
            $"{directionIcon} <b>{Html(actionText)}</b>\n\n" +
            "📌 انجام‌دهنده\n" +
            $"{TelegramUserLinkFormatter.HtmlSummary(actorUser)}\n\n" +
            "📌 کاربر هدف\n" +
            $"{TelegramUserLinkFormatter.HtmlSummary(target)}\n\n" +
            $"مبلغ: <code>{Html(amountToman.FormatCurrency())}</code>\n" +
            $"موجودی قبل: <code>{Html(beforeBalance.FormatCurrency())}</code>\n" +
            $"موجودی بعد: <code>{Html(afterBalance.FormatCurrency())}</code>";

        _logger.LogPayment(message);
    }

    /// <summary>
    /// Writes the private-channel audit log for a super-admin colleague role change.
    /// </summary>
    /// <param name="actor">
    /// Telegram sender who confirmed the role change. The value is used only to build a clickable admin identity
    /// for the private audit log.
    /// </param>
    /// <param name="target">Credentials user whose colleague pricing flag was changed.</param>
    /// <param name="wasColleague">Role flag before the admin operation was applied.</param>
    /// <param name="isColleagueAfter">Role flag after the admin operation was persisted.</param>
    /// <remarks>
    /// Role changes affect pricing and tenant-bot access, so both promotion and demotion are logged. The caller
    /// performs the database update first; this method has no side effects except the private audit log.
    /// </remarks>
    private void LogAdminRoleChange(
        Telegram.Bot.Types.User actor,
        CredUser target,
        bool wasColleague,
        bool isColleagueAfter)
    {
        var actorUser = BuildCredUserFromTelegramActor(actor);
        var actionText = isColleagueAfter ? "ارتقای کاربر به همکار" : "تنزل کاربر به عادی";

        var message =
            $"👥 <b>{Html(actionText)}</b>\n\n" +
            "📌 انجام‌دهنده\n" +
            $"{TelegramUserLinkFormatter.HtmlSummary(actorUser)}\n\n" +
            "📌 کاربر هدف\n" +
            $"{TelegramUserLinkFormatter.HtmlSummary(target)}\n\n" +
            $"نقش قبل: <code>{Html(wasColleague ? "همکار" : "کاربر عادی")}</code>\n" +
            $"نقش بعد: <code>{Html(isColleagueAfter ? "همکار" : "کاربر عادی")}</code>";

        _logger.LogPayment(message);
    }

    /// <summary>
    /// Converts a Telegram update sender into the shared credentials shape used by audit link formatters.
    /// </summary>
    /// <param name="actor">
    /// Telegram sender from the incoming update. A null value is converted to a zero-id placeholder to keep audit
    /// logging best-effort and non-throwing.
    /// </param>
    /// <returns>
    /// A lightweight credentials user object suitable for HTML audit summaries. The returned object is not tracked
    /// by EF Core and must not be saved.
    /// </returns>
    private static CredUser BuildCredUserFromTelegramActor(Telegram.Bot.Types.User actor)
    {
        return new CredUser
        {
            TelegramUserId = actor?.Id ?? 0,
            ChatID = actor?.Id ?? 0,
            Username = actor?.Username ?? string.Empty,
            FirstName = actor?.FirstName ?? string.Empty,
            LastName = actor?.LastName ?? string.Empty,
            LanguageCode = actor?.LanguageCode ?? string.Empty
        };
    }

    private static string BuildBotCapabilitiesMessage(CredUser credUser)
    {
        var roleText = credUser?.IsColleague == true ? "همکار" : "کاربر عادی";
        var builder = new StringBuilder();

        builder.AppendLine("📌 <b>قابلیت‌های مهم ربات</b>");
        builder.AppendLine();
        builder.AppendLine($"نوع حساب شما: <code>{Html(roleText)}</code>");
        builder.AppendLine();
        builder.AppendLine("با این ربات می‌توانید:");
        builder.AppendLine();
        builder.AppendLine("🛒 <b>خرید اکانت</b>");
        builder.AppendLine("انتخاب سرویس نت عادی، نت ملی یا نامحدود با مصرف منصفانه، تعداد اکانت و ثبت کامنت اختصاصی.");
        builder.AppendLine();
        builder.AppendLine("📋 <b>تعرفه‌های داینامیک</b>");
        builder.AppendLine("تعرفه‌ها همیشه بر اساس نوع حساب شما، یعنی کاربر عادی یا همکار، نمایش داده می‌شود.");
        builder.AppendLine();
        builder.AppendLine("👤 <b>مدیریت اکانت‌ها</b>");
        builder.AppendLine("مشاهده همه اکانت‌ها، صفحه‌بندی، جستجو، دیدن جزئیات، تمدید، حذف تکی و تغییر لینک.");
        builder.AppendLine();
        builder.AppendLine("🔎 <b>جستجوی سریع</b>");
        builder.AppendLine("جستجو با نام اکانت، بخشی از کامنت یا UUID کامل کانفیگ.");
        builder.AppendLine();
        builder.AppendLine("🔁 <b>تمدید و تغییر لینک</b>");
        builder.AppendLine("تمدید با پلن‌های فعال ربات، تمدید مستقیم از پیام هشدار انقضا و ساخت لینک جدید در صورت لو رفتن اطلاعات اکانت.");
        builder.AppendLine();
        builder.AppendLine("⏰ <b>هشدار انقضا</b>");
        builder.AppendLine("برای اکانت‌های قابل تمدید، ۷ روز، ۳ روز و ۱ روز قبل از انقضا پیام یادآوری همراه با دکمه تمدید ارسال می‌شود.");
        builder.AppendLine();
        builder.AppendLine("🧹 <b>حذف اکانت‌های منقضی</b>");
        builder.AppendLine("نمایش و حذف یکجای اکانت‌هایی که حجم یا زمان آن‌ها تمام شده است.");
        builder.AppendLine();
        builder.AppendLine("💰 <b>شارژ حساب</b>");
        builder.AppendLine("شارژ کیف پول از درگاه ریالی HooshPay یا پرداخت ارز دیجیتال، با ثبت و بررسی وضعیت پرداخت.");
        builder.AppendLine();
        builder.AppendLine("🌟 <b>اکانت تست رایگان</b>");
        builder.AppendLine("دریافت تست دوره‌ای برای بررسی کیفیت سرویس‌ها، در صورت داشتن شرایط.");

        if (credUser?.IsColleague == true)
        {
            builder.AppendLine();
            builder.AppendLine("💎 <b>امکانات همکاران</b>");
            builder.AppendLine("قیمت همکار، ساخت چند اکانت در یک سفارش و دسترسی سریع به اکانت‌ها با شماره اکانت.");
            builder.AppendLine("فعالسازی ربات فروشگاهی اختصاصی، خرید و تمدید مستقیم با HooshPay، ارز دیجیتال یا کارت‌به‌کارت همکار.");
            builder.AppendLine("مدیریت آموزش‌های فروشگاه، پیام عمومی فقط به مشتریان همان tenant، گزارش سفارش‌ها، آمار روزانه و ثبت سود یا کسر هزینه در ledger.");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("🤝 <b>درخواست همکاری</b>");
            builder.AppendLine("اگر فروش هفتگی شما به حد نصاب برسد، می‌توانید از بخش مدیریت اکانت درخواست همکاری ثبت کنید.");
        }

        builder.AppendLine();
        builder.AppendLine("📋 <b>دستورهای عمومی و اکانت</b>");
        builder.AppendLine();
        builder.AppendLine("🔹 <code>/start</code>");
        builder.AppendLine("شروع مجدد ربات، پاک کردن وضعیت موقت کاربر و برگشت به منوی اصلی.");
        builder.AppendLine();
        builder.AppendLine("🔹 <code>/renew_EMAIL</code>");
        builder.AppendLine("شروع تمدید اکانت با نام اکانت یا همان ایمیل.");
        builder.AppendLine("مثال:");
        builder.AppendLine("<code>/renew_vniaccXXXXX</code>");
        builder.AppendLine();
        builder.AppendLine("🔹 <code>/enable_EMAIL</code>");
        builder.AppendLine("فعال کردن اکانت با نام اکانت یا ایمیل.");
        builder.AppendLine("ابتدا از پنل نسخه ۳ انجام می‌شود و در صورت نیاز مسیر قدیمی نسخه ۲ بررسی می‌شود.");
        builder.AppendLine("مثال:");
        builder.AppendLine("<code>/enable_vniaccXXXXX</code>");
        builder.AppendLine();
        builder.AppendLine("🔹 <code>/disable_EMAIL</code>");
        builder.AppendLine("غیرفعال کردن اکانت با نام اکانت یا ایمیل.");
        builder.AppendLine("ابتدا از پنل نسخه ۳ انجام می‌شود و در صورت نیاز مسیر قدیمی نسخه ۲ بررسی می‌شود.");
        builder.AppendLine("مثال:");
        builder.AppendLine("<code>/disable_vniaccXXXXX</code>");
        builder.AppendLine();
        builder.AppendLine("🔹 <code>/account_NUMBER</code>");
        builder.AppendLine("جستجوی اکانت همکار بر اساس شماره اکانت.");
        builder.AppendLine("این دستور فقط برای همکاران فعال است.");
        builder.AppendLine("مثال:");
        builder.AppendLine("<code>/account_34</code>");
        builder.AppendLine();
        builder.AppendLine("همچنین می‌توانید فقط عدد اکانت را بدون پیشوند ارسال کنید:");
        builder.AppendLine("<code>34</code>");
        builder.AppendLine();
        builder.AppendLine("برای شروع، از دکمه‌های پایین صفحه استفاده کنید.");
        return builder.ToString();
    }

    private static string Html(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static ReplyKeyboardMarkup BuildChargePaymentMethodKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("درگاه ریالی هوش‌پی"),
                new KeyboardButton("درگاه ارز دیجیتال")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    private async Task CreateHooshPayWalletChargeAsync(
        Message message,
        CredUser credUser,
        User user,
        CancellationToken cancellationToken)
    {
        long amount = Convert.ToInt64(user.ConfigLink);
        var payment = HooshPayPaymentInfo.CreateWalletCharge(
            credUser.TelegramUserId,
            amount,
            _appConfig.HooshPayIpnUrl,
            CurrentHooshPayReturnUrl,
            message.Chat.Id);

        _userDbContext.HooshPayPaymentInfos.Add(payment);
        await _userDbContext.SaveChangesAsync(cancellationToken);

        try
        {
            payment.RawRequestJson = JsonConvert.SerializeObject(new
            {
                amount,
                fee_mode = HooshPayFeeModes.Buyer,
                order_id = payment.OrderId,
                callback_url = _appConfig.HooshPayIpnUrl,
                return_url = CurrentHooshPayReturnUrl
            });

            Console.WriteLine($"[HooshPay] Creating invoice for user={credUser.TelegramUserId}, amount={amount}, orderId={payment.OrderId}, feeMode={HooshPayFeeModes.Buyer}");

            var invoice = await _hooshPay.CreateInvoiceAsync(
                amount,
                payment.OrderId,
                $"Wallet charge {payment.OrderId}",
                _appConfig.HooshPayIpnUrl,
                CurrentHooshPayReturnUrl,
                cancellationToken);

            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            if (invoice?.data == null)
                throw new InvalidOperationException("HooshPay invoice response did not contain data.");

            payment.Apply(invoice.data);
            await _userDbContext.SaveChangesAsync(cancellationToken);

            var msg = await GetHooshPayPaymentMessage(credUser, payment);
            var inlineKeyboardMarkup = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithUrl(text: "پرداخت آنلاین", url: payment.PaymentUrl)
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(text: "بررسی وضعیت", callbackData: $"hpchk_{payment.Id}")
                }
            });

            Message latestMsg;
            if (!string.IsNullOrWhiteSpace(payment.PaymentUrl))
            {
                using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(payment.PaymentUrl, 200));
                latestMsg = await ActiveBotClient.SendPhotoAsync(
                    message.Chat.Id,
                    InputFile.FromStream(qrStream),
                    caption: msg,
                    parseMode: ParseMode.Html,
                    replyMarkup: inlineKeyboardMarkup,
                    cancellationToken: cancellationToken);
            }
            else
            {
                latestMsg = await ActiveBotClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: msg,
                    replyMarkup: inlineKeyboardMarkup,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "منوی اصلی",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (latestMsg != null)
                payment.TelMsgId = latestMsg.MessageId;

            await _userDbContext.SaveChangesAsync(cancellationToken);
        }
        catch (HooshPayApiException ex)
        {
            Console.WriteLine("[HooshPay] API exception while creating invoice:");
            Console.WriteLine(ex.ToString());

            _logger.LogWarning(
                ex,
                "HooshPay invoice creation was rejected. botId={BotId}, userId={UserId}, chatId={ChatId}, orderId={OrderId}, amountToman={AmountToman}, statusCode={StatusCode}",
                BotContextAccessor.CurrentBotId,
                credUser.TelegramUserId,
                message.Chat.Id,
                payment.OrderId,
                amount,
                ex.StatusCode);

            payment.ErrorCode = ex.StatusCode.ToString(CultureInfo.InvariantCulture);
            payment.ErrorMessage = ex.Message;
            payment.RawResponseJson = ex.ResponseBody;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: BuildHooshPayInvoiceCreationFailureText(ex),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[HooshPay] Unexpected exception while creating invoice:");
            Console.WriteLine(ex.ToString());

            _logger.LogError(
                ex,
                "HooshPay invoice creation failed unexpectedly. botId={BotId}, userId={UserId}, chatId={ChatId}, orderId={OrderId}, amountToman={AmountToman}",
                BotContextAccessor.CurrentBotId,
                credUser.TelegramUserId,
                message.Chat.Id,
                payment.OrderId,
                amount);

            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await ActiveBotClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "ایجاد پرداخت ریالی HooshPay ناموفق بود. جزئیات خطا در ترمینال ثبت شد.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Builds the customer-safe error message for a rejected HooshPay invoice request.
    /// </summary>
    /// <param name="exception">
    /// HooshPay API exception containing the HTTP status and provider response body. The raw body is parsed only for
    /// a user-safe provider message and is never inserted into Telegram unescaped.
    /// </param>
    /// <returns>
    /// Persian HTML-safe plain-text message that explains the provider rejection. Amount-limit responses also tell the
    /// customer to split the charge into multiple payments or use the crypto gateway.
    /// </returns>
    /// <remarks>
    /// Provider response text can be malformed, HTML-like, or unexpectedly long. This method truncates and escapes
    /// it before Telegram delivery while the complete raw payload remains stored on the local payment row and daily
    /// diagnostic file for administrators.
    /// </remarks>
    private static string BuildHooshPayInvoiceCreationFailureText(HooshPayApiException exception)
    {
        var providerMessage = ExtractHooshPayProviderMessage(exception?.ResponseBody);
        var safeDetails = string.IsNullOrWhiteSpace(providerMessage)
            ? string.Empty
            : $"\nجزئیات درگاه: <code>{Html(providerMessage)}</code>";

        if (IsHooshPayAmountLimitFailure(exception, providerMessage))
        {
            return "درگاه ریالی HooshPay این مبلغ را برای یک تراکنش نپذیرفت." +
                   safeDetails +
                   "\n\nلطفاً مبلغ را در چند تراکنش کوچک‌تر پرداخت کنید یا از درگاه ارز دیجیتال استفاده کنید.";
        }

        return "HooshPay درخواست پرداخت را رد کرد." + safeDetails +
               "\nلطفاً مبلغ یا اطلاعات پرداخت را بررسی کنید؛ در صورت تکرار، از درگاه ارز دیجیتال استفاده کنید.";
    }

    /// <summary>
    /// Extracts a compact provider message from a HooshPay JSON or plain-text error response.
    /// </summary>
    /// <param name="responseBody">Raw error response body returned by HooshPay; it may be JSON, plain text, or empty.</param>
    /// <returns>At most 300 characters of provider detail suitable for escaped Telegram display, or an empty string.</returns>
    /// <remarks>
    /// The response body is intentionally not trusted as HTML and must be escaped by the caller before display.
    /// </remarks>
    private static string ExtractHooshPayProviderMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return string.Empty;

        try
        {
            var token = JToken.Parse(responseBody);
            var message = token["message"]?.ToString()
                          ?? token["error"]?.ToString()
                          ?? token["errors"]?.ToString(Formatting.None);
            if (!string.IsNullOrWhiteSpace(message))
                return message.Length <= 300 ? message : message[..300] + "...";
        }
        catch (JsonException)
        {
            // A non-JSON gateway response is still useful after a conservative length limit is applied below.
        }

        var compact = responseBody.Trim();
        return compact.Length <= 300 ? compact : compact[..300] + "...";
    }

    /// <summary>
    /// Detects whether a rejected HooshPay request is likely caused by the provider's per-transaction amount limit.
    /// </summary>
    /// <param name="exception">HTTP-level HooshPay exception, when available.</param>
    /// <param name="providerMessage">Compact provider message parsed from the response body.</param>
    /// <returns><c>true</c> when the rejection indicates an amount, maximum, limit, or ceiling constraint.</returns>
    /// <remarks>
    /// HooshPay has returned validation responses with different wording over time, so the check combines validation
    /// status codes with conservative Persian and English message markers rather than relying on one exact string.
    /// </remarks>
    private static bool IsHooshPayAmountLimitFailure(HooshPayApiException exception, string providerMessage)
    {
        var text = string.Join(" ", exception?.ResponseBody ?? string.Empty, providerMessage ?? string.Empty);
        return exception?.StatusCode is 400 or 409 or 422 &&
               (text.Contains("amount", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("max", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("سقف", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("مبلغ", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("حداکثر", StringComparison.OrdinalIgnoreCase));
    }


    async Task<string> GetZipalPaymentMessage(CredUser credUser, bool isSuperAdmin, ZibalPaymentInfo zpi, string paymentLink)
    {
        var _credUser = await _credentialsDbContext.GetUserStatus(credUser);

        string text = string.Empty;
        if (!isSuperAdmin) text = "✅ درگاه پرداخت برای شما با موفقیت ایجاد شد.  \n";
        text += $"💵 مبلغ: {(zpi.Amount / 10).FormatCurrency()} \n";
        text += $"\u200F📅 تاریخ: {DateTime.Now.ConvertToHijriShamsi()} \n";
        text += $"‌🧾شماره سند: <code>{zpi.TrackId}</code>    \n";
        if (isSuperAdmin == true) text += $"\u200F 🧾شماره سند: {zpi.Id} \n";
        text += $"\u200F ℹ️  آیدی عددی خریدار: <code>{credUser.TelegramUserId}</code> \n";

        text += $"\u200F لطفاً برای پرداخت از لینک زیر اقدام فرمایید. \n";
        text += $"\u200F <a href=\"{paymentLink}\">🏧   برای پرداخت کلیک کنید.</a> \n";
        if (!isSuperAdmin)
            text += "❗️نکات زیر را حتماً مد نظر قرار دهید:" + "\n" + "2. بعد از تکمیل پرداخت روی گزینه پرداخت کردم بزنید تا حساب شما شارژ شود." + "\n" + "3. ساعت 12 شب تا  بامداد1 سیکل تسویه بانک مرکزی است و در این مدت امکان پرداخت وجود ندارد." + "\n" + "4. نیم ساعت پس از ایجاد لینک پرداخت، نشست منقضی میشود و امکان پرداخت آن وجود ندارد. لذا سعی کنید بلافاصله بعد از ایجاد درگاه، آنرا پرداخت کنید." + "\n" + "5. هنگام پرداخت VPN خود را خاموش کنید." + "\n" + "6. در صورت بروز هرگونه مشکل با آیدی پشتیبانی(@vpnetiran_admin) در تماس باشید." + "\n";

        return text;
    }

    async Task<string> GetHooshPayPaymentMessage(CredUser credUser, HooshPayPaymentInfo payment)
    {
        await _credentialsDbContext.GetUserStatus(credUser);

        var paymentUrl = System.Net.WebUtility.HtmlEncode(payment.PaymentUrl ?? "");
        var orderId = System.Net.WebUtility.HtmlEncode(payment.OrderId ?? "");
        var invoiceUid = System.Net.WebUtility.HtmlEncode(payment.InvoiceUid ?? "");
        var trackingCode = System.Net.WebUtility.HtmlEncode(payment.TrackingCode ?? "");
        var payableAmount = payment.PayableAmountToman > 0
            ? payment.PayableAmountToman
            : payment.AmountToman + payment.FeeAmountToman;
        var payableAmountRial = payableAmount * 10;

        string text = "✅ درگاه پرداخت ریالی HooshPay برای شما ایجاد شد.\n";
        text += $"💰 مبلغ شارژ کیف پول: {payment.AmountToman.FormatCurrency()}\n";
        text += $"💳 مبلغ قابل پرداخت با کارمزد: {payableAmount.FormatCurrency()}\n";
        text += $"💳 مبلغ دقیق قابل پرداخت به ریال: <code>{payableAmountRial:0,0}</code>\n";
        if (payment.FeeAmountToman > 0)
            text += $"🧾 کارمزد درگاه: {payment.FeeAmountToman.FormatCurrency()}\n";
        text += "\n⚠️ کارمزد درگاه هوش‌پی ۱۵ درصد است و به مبلغ شما اضافه می‌شود. اگر نمی‌خواهید کارمزد پرداخت کنید، از درگاه ارز دیجیتال استفاده کنید.\n";
        text += "⚠️ مبلغ پرداخت را دقیقاً مطابق عدد نمایش داده‌شده در صفحه پرداخت و تا ریال آخر واریز کنید. در پرداخت کارت‌به‌کارت، هر اختلافی باعث گم شدن پرداخت و تایید نشدن آن می‌شود و مجموعه ما مسئولیتی در قبال مبلغ گم‌شده نمی‌پذیرد.\n";
        text += $"🧾 شماره سفارش: <code>{orderId}</code>\n";
        if (!string.IsNullOrWhiteSpace(invoiceUid))
            text += $"🧾 شناسه فاکتور: <code>{invoiceUid}</code>\n";
        if (!string.IsNullOrWhiteSpace(trackingCode))
            text += $"🔎 کد پیگیری: <code>{trackingCode}</code>\n";
        if (!string.IsNullOrWhiteSpace(paymentUrl))
            text += $"🔗 لینک پرداخت: <a href=\"{paymentUrl}\">باز کردن صفحه پرداخت</a>\n";
        text += "\nپس از تایید پرداخت، شارژ کیف پول شما از طریق IPN به صورت خودکار انجام می‌شود. اگر لازم بود می‌توانید از دکمه بررسی وضعیت استفاده کنید.";

        return text;
    }

    async Task<string> GetNowPaymentsPaymentMessage(CredUser credUser, SwapinoPaymentInfo payment)
    {
        await _credentialsDbContext.GetUserStatus(credUser);
        var data = payment.GetNowPaymentsData();
        var baseCurrency = System.Net.WebUtility.HtmlEncode((payment.BaseCurrency ?? data.PriceCurrency ?? "usdtbsc").ToUpperInvariant());
        var baseAmount = (payment.BaseAmount == 0 ? data.PriceAmount : payment.BaseAmount).ToString("0.########", CultureInfo.InvariantCulture);
        var invoiceUrl = System.Net.WebUtility.HtmlEncode(payment.InvoiceUrl ?? data.InvoiceUrl ?? "");
        var paymentId = System.Net.WebUtility.HtmlEncode(payment.PaymentId ?? data.PaymentId ?? "");
        var orderId = System.Net.WebUtility.HtmlEncode(payment.OrderId);

        string text = "✅ درگاه پرداخت ارز دیجیتال برای شما ایجاد شد.\n";
        text += $"💵 مبلغ شارژ کیف پول: {payment.AmountToman.FormatCurrency()}\n";
        text += $"💲 ارز مبنا: <code>{baseAmount} {baseCurrency}</code>\n";
        if (data.UsdtIrtPrice > 0)
        {
            var dollarPriceToman = data.PriceIsRial
                ? data.UsdtIrtPrice / 10m
                : data.UsdtIrtPrice;
            var priceNote = data.UsedFallbackPrice ? "قیمت پیش‌فرض" : "قیمت لحظه‌ای نوبیتکس";
            text += $"💱 نرخ تتر/دلار زمان ساخت فاکتور: <code>{dollarPriceToman:0,0} تومان</code> ({priceNote})\n";
        }
        text += $"🧾 شماره سفارش: <code>{orderId}</code>\n";
        if (!string.IsNullOrWhiteSpace(invoiceUrl))
            text += $"🔗 لینک فاکتور: <a href=\"{invoiceUrl}\">باز کردن صفحه پرداخت</a>\n";
        if (!string.IsNullOrWhiteSpace(paymentId))
            text += $"🧾 شناسه پرداخت: <code>{paymentId}</code>\n";
        text += "\nشما می‌توانید از هر ارز دیجیتالی که NOWPayments پشتیبانی می‌کند پرداخت را انجام دهید. پس از تایید شبکه، شارژ کیف پول شما از طریق IPN به صورت خودکار انجام می‌شود.";

        return text;
    }

    private static string GetTutorialButtonText(int index)
    {
        return index switch
        {
            0 => "آموزش نصب کانفیگ لینک",
            1 => "آموزش نصب سابلینک",
            _ => $"آموزش شماره {index + 1}"
        };
    }

    string[] GetPrices(bool isColleague, bool isForRenew)
    {

        List<string> buttonsName = new List<string>();
        if (isForRenew)
        {
            if (isColleague)
            {
                _appConfig.PriceColleagues.ForEach(i => buttonsName.Add($"تمدید اکانت {i.DurationName} قیمت {i.Price}"));
            }
            else
            {
                _appConfig.Price.ForEach(i => buttonsName.Add($"تمدید اکانت {i.DurationName} قیمت {i.Price}"));
            }
        }
        else
        {
            if (isColleague)
            {
                _appConfig.PriceColleagues.ForEach(i => buttonsName.Add($"خرید اکانت {i.DurationName} قیمت {i.Price}"));
            }
            else
            {
                _appConfig.Price.ForEach(i => buttonsName.Add($"خرید اکانت {i.DurationName} قیمت {i.Price}"));
            }
        }
        return buttonsName.ToArray();
    }

    PriceConfig GetPriceConfig(string messageText, CredUser credUser, bool isForRenew)
    {
        var appConfig = _configuration.Get<AppConfig>();

        PriceConfig priceConfig;
        var prices = GetPrices(credUser.IsColleague, isForRenew);
        int index = -1;
        try
        {
            index = Array.IndexOf(prices, messageText);
        }
        catch (System.Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        if (index == -1) return null;

        if (credUser.IsColleague)
        {
            priceConfig = appConfig.PriceColleagues.ToArray().ElementAtOrDefault(index) ?? null;
        }
        else
        {
            priceConfig = appConfig.Price.ToArray().ElementAtOrDefault(index) ?? null;
        }
        return priceConfig;

    }
    long TryParsPrice(string input)
    {

        //input = "خرید اکانت شش ماهه قیمت 360000";

        // Define a regular expression pattern to match a numeric value.
        //string pattern = @"([\d٠-٩]+)";
        string pattern = @"(\d+)";

        // Use Regex.Match to find the first match in the input string.
        Match match = Regex.Match(input, pattern);
        long value = 0;
        // Check if the match was successful.
        if (match.Success)
        {
            // Try to parse the matched value as a long.
            if (long.TryParse(match.Groups[1].Value, out long extractedValue))
            {
                value = extractedValue;
            }
            else
            {
                value = 0;
            }
        }
        else
        {
            value = 0;
        }
        return value;
    }
    /// <summary>
    /// Builds the Persian owned-bot main reply keyboard including global referral access.
    /// </summary>
    /// <returns>A resized reply keyboard whose final row is the single full-width main-menu button.</returns>
    /// <remarks>
    /// The referral button is available only in owned routing; tenant storefronts construct their own customer menu
    /// and do not use this keyboard for referral registration or reporting.
    /// </remarks>
    ReplyKeyboardMarkup MainReplyMarkupKeyboardFa()
    {

        ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
               {
                    new KeyboardButton[] { "💳خرید اکانت جدید", "💰شارژ حساب کاربری" },
                    new KeyboardButton[] { "📋 تعرفه‌ها", "📒 تراکنش‌های من" },
                    new KeyboardButton[] { "⚙️ مدیریت اکانت" },
                    new KeyboardButton[] { "🌟اکانت رایگان", "💡راهنما نصب" },
                    new KeyboardButton[] { "🎁 دعوت از دوستان", "💻 ارتباط با ادمین" },
                    new KeyboardButton[] { "🏠منو" }})
        {
            ResizeKeyboard = true
        };
        return replyKeyboardMarkup;

        // var buttons = new[]
        // {
        // new[] { "💳خرید اکانت جدید", "🏠منو","💻 ارتباط با ادمین" },
        // new[] { "💡راهنما نصب", "🌟اکانت رایگان", "⚙️مدیریت اکانت ها" }
        // };

        // var keyboardButtons = buttons
        //     .Select(row => row.Select(buttonText => new KeyboardButton(buttonText)))
        //     .ToArray();
        // return new ReplyKeyboardMarkup(keyboardButtons, ResizeKeyboard = false);
    }


    ReplyKeyboardMarkup PriceReplyMarkupKeyboardFa(bool isColleague, bool isForRenew)
    {
        var prices = GetPrices(isColleague, isForRenew);
        if (isForRenew && isColleague)
        {
            ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
                          {
                    new KeyboardButton[] { prices[0], prices[1] },
                    new KeyboardButton[] { prices[2],prices[3] },
                    new KeyboardButton[] { "تمدید حجمی" },
                    new KeyboardButton[] { "🏠منو" }})
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };

            return replyKeyboardMarkup;
        }
        else
        {
            ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
              {
                    new KeyboardButton[] { prices[0], prices[1] },
                    new KeyboardButton[] { prices[2],prices[3] },
                    new KeyboardButton[] { "🏠منو" }})
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };

            return replyKeyboardMarkup;

        }

    }

    static ReplyKeyboardMarkup GetMainMenuKeyboard()
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("➕ Create New Account"),
            },
            new[]
            {
                new KeyboardButton("🔄 Renew Existing Account"),
            },
            new[]
            {
                new KeyboardButton("ℹ️ Get Account Info"),
            },
            new[]
            {
                new KeyboardButton("🗽 Admin"),
            },
            new[]
            {
                new KeyboardButton("📑 Menu"),
            }
        });

        return keyboard;
    }

    /// <summary>
    /// Builds the user-facing outcome shown after an owned-bot referral start payload is processed.
    /// </summary>
    /// <param name="result">Registration result returned by the global referral service, or <c>null</c> for ordinary start.</param>
    /// <returns>Readable Persian status text, or an empty string when no referral-specific message is needed.</returns>
    /// <remarks>
    /// The message deliberately does not reveal another referrer's Telegram id when the immutable relationship was
    /// already claimed through a different link.
    /// </remarks>
    private static string BuildReferralRegistrationMessage(ReferralRegistrationResult result)
    {
        if (result == null)
            return string.Empty;

        return result.Status switch
        {
            ReferralRegistrationStatus.Created =>
                "✅ دعوت شما با موفقیت ثبت شد. پاداش‌ها پس از اولین پرداخت واجدشرایط اعمال می‌شوند.",
            ReferralRegistrationStatus.AlreadyRegistered =>
                "ℹ️ این لینک دعوت قبلاً برای حساب شما ثبت شده است.",
            ReferralRegistrationStatus.DifferentReferrerAlreadyRegistered =>
                "ℹ️ حساب شما قبلاً با یک لینک دعوت دیگر ثبت شده و معرف اول قابل تغییر نیست.",
            ReferralRegistrationStatus.SelfReferralRejected =>
                "⛔️ امکان ثبت لینک دعوت خودتان وجود ندارد.",
            ReferralRegistrationStatus.InvalidCode =>
                "⚠️ لینک دعوت معتبر نیست یا صاحب آن هنوز در ربات ثبت نشده است.",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Determines whether a Telegram text message requests the owned-bot referral dashboard.
    /// </summary>
    /// <param name="text">
    /// Raw message text received from Telegram. Null and whitespace are allowed; surrounding whitespace is ignored.
    /// </param>
    /// <returns>
    /// <c>true</c> for the referral reply-keyboard label with or without the gift emoji; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method intentionally recognizes a main-menu command instead of a persisted customer-flow value. Callers
    /// must route a match before arbitrary-text purchase, search, comment, trial, colleague, or renewal handlers.
    /// </remarks>
    /// <example>
    /// <code>
    /// IsReferralMenuCommand("  🎁 دعوت از دوستان  "); // true
    /// IsReferralMenuCommand("دعوت از دوستان");       // true
    /// </code>
    /// </example>
    private static bool IsReferralMenuCommand(string text)
    {
        var normalized = text?.Trim();
        return string.Equals(normalized, "🎁 دعوت از دوستان", StringComparison.Ordinal) ||
               string.Equals(normalized, "دعوت از دوستان", StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancels the current owned-bot customer conversation and displays the existing global referral dashboard.
    /// </summary>
    /// <param name="botClient">Telegram client for the owned bot that received the main-menu command.</param>
    /// <param name="message">
    /// Incoming Telegram message. Its sender id selects bot-scoped conversation/session state and its chat id receives
    /// the dashboard or a safe failure message.
    /// </param>
    /// <param name="user">
    /// Current bot-scoped persisted customer state when the caller has already loaded it. Pass <c>null</c> from the
    /// super-admin route so this handler loads the state inside its protected/logged operation. Only transient
    /// conversation fields are cleared; credentials, wallet, referral relationships, rewards, and XUI accounts are
    /// untouched.
    /// </param>
    /// <param name="cancellationToken">Token that cancels users.db access and Telegram delivery.</param>
    /// <returns>
    /// <c>true</c> when the text is a referral menu command and the update was consumed, including safe error handling;
    /// <c>false</c> for unrelated text or a non-owned bot.
    /// </returns>
    /// <remarks>
    /// This handler is shared by regular and super-admin owned-bot routes. It clears both the persisted <see cref="User"/>
    /// flow and the current bot/user entry in <see cref="XuiV3PurchaseSessionStore"/> before calling
    /// <see cref="SendReferralDashboardAsync"/>. Telegram notification failures never mutate referral or financial data.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (await TryHandleReferralMenuCommandAsync(botClient, message, user, cancellationToken))
    ///     return;
    /// </code>
    /// </example>
    private async Task<bool> TryHandleReferralMenuCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        User user,
        CancellationToken cancellationToken)
    {
        if (message?.From == null || !IsReferralMenuCommand(message.Text))
            return false;

        var botType = CurrentBot?.Type ?? BotContextAccessor.CurrentBotType;
        if (!string.Equals(botType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
            return false;

        var telegramUserId = message.From.Id;
        var botId = CurrentBot?.Id ?? BotContextAccessor.CurrentBotId;
        var botUsername = CurrentBot?.Username ?? BotContextAccessor.CurrentBotUsername;
        var isSuperAdmin = IsSuperAdminUser(telegramUserId);
        var previousFlow = user?.Flow ?? "(not-loaded)";
        var previousLastStep = user?.LastStep ?? "(not-loaded)";

        try
        {
            var currentState = user ?? await _userDbContext.GetUserStatus(telegramUserId);
            previousFlow = currentState?.Flow ?? string.Empty;
            previousLastStep = currentState?.LastStep ?? string.Empty;

            // Both stores are bot-scoped. Clearing them prevents a stale arbitrary-text handler or purchase selection
            // from resuming after the user deliberately returned to the referral main-menu screen.
            await _userDbContext.ClearUserStatus(currentState ?? new User { Id = telegramUserId });
            _xuiV3PurchaseSessionStore.Clear(telegramUserId);

            var dashboardMessage = await SendReferralDashboardAsync(botClient, message, cancellationToken);
            var dashboardSent = dashboardMessage != null;
            if (!dashboardSent)
                throw new InvalidOperationException("Telegram did not return a message for the referral dashboard.");

            _logger.LogInformation(
                "Owned-bot referral menu routed. telegramUserId={TelegramUserId}, botId={BotId}, botUsername={BotUsername}, isSuperAdmin={IsSuperAdmin}, previousFlow={PreviousFlow}, previousLastStep={PreviousLastStep}, dashboardSent={DashboardSent}",
                telegramUserId,
                botId,
                botUsername,
                isSuperAdmin,
                previousFlow,
                previousLastStep,
                dashboardSent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var telegramApiException = ex as ApiRequestException;
            _logger.LogError(
                ex,
                "Owned-bot referral menu failed. telegramUserId={TelegramUserId}, botId={BotId}, botUsername={BotUsername}, isSuperAdmin={IsSuperAdmin}, previousFlow={PreviousFlow}, previousLastStep={PreviousLastStep}, dashboardSent={DashboardSent}, exceptionType={ExceptionType}, telegramErrorCode={TelegramErrorCode}, errorMessage={ErrorMessage}",
                telegramUserId,
                botId,
                botUsername,
                isSuperAdmin,
                previousFlow,
                previousLastStep,
                false,
                ex.GetType().FullName,
                telegramApiException?.ErrorCode,
                ex.Message);

            try
            {
                await botClient.SendReferralTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "نمایش اطلاعات دعوت از دوستان با خطا روبه‌رو شد. لطفاً چند دقیقه دیگر دوباره تلاش کنید.",
                    replyMarkup: MainReplyMarkupKeyboardFa(),
                    cancellationToken: cancellationToken);
            }
            catch (Exception deliveryException)
            {
                _logger.LogError(
                    deliveryException,
                    "Referral dashboard failure notification could not be delivered. telegramUserId={TelegramUserId}, botId={BotId}, botUsername={BotUsername}",
                    telegramUserId,
                    botId,
                    botUsername);
            }
        }

        return true;
    }

    /// <summary>
    /// Shows the current user's global referral link and reward statistics in an owned bot.
    /// </summary>
    /// <param name="botClient">Telegram client for the currently handling owned bot.</param>
    /// <param name="message">Incoming message containing the numeric Telegram user and chat ids.</param>
    /// <param name="cancellationToken">Cancellation token for users.db statistics and Telegram delivery.</param>
    /// <returns>
    /// The non-null Telegram message accepted for delivery. The caller must use this result, rather than completion of
    /// the method alone, when recording <c>dashboardSent=true</c>.
    /// </returns>
    /// <remarks>
    /// The link uses the current owned bot username, but invited count and rewards are global across every owned bot.
    /// Tenant bots never call this method and cannot create referral links or relationships.
    /// </remarks>
    private async Task<Message> SendReferralDashboardAsync(
        ITelegramBotClient botClient,
        Message message,
        CancellationToken cancellationToken)
    {
        if (_appConfig.Referral?.Enabled != true)
        {
            return await botClient.SendReferralTextMessageAsync(
                chatId: message.Chat.Id,
                text: "قابلیت دعوت از دوستان در حال حاضر فعال نیست.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
        }

        var username = (CurrentBot?.Username ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
        {
            return await botClient.SendReferralTextMessageAsync(
                chatId: message.Chat.Id,
                text: "نام کاربری این ربات برای ساخت لینک دعوت تنظیم نشده است.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
        }

        var stats = await _referralService.GetUserStatsAsync(message.From.Id, cancellationToken);
        var code = ReferralCodeCodec.Encode(message.From.Id);
        var link = $"https://t.me/{username}?start=ref_{code}";
        var text =
            "🎁 دعوت از دوستان\n\n" +
            $"لینک اختصاصی شما:\n{link}\n\n" +
            $"👥 تعداد دعوت‌شده‌ها: {stats.InvitedCount:N0}\n" +
            $"✅ دعوت‌های واجدشرایط: {stats.EligibleReferralCount:N0}\n" +
            $"💰 مجموع پاداش‌ها: {stats.TotalAppliedRewardToman:N0} تومان\n" +
            $"⏳ پاداش‌های در انتظار: {stats.PendingRewardCount:N0}\n" +
            $"⚠️ پاداش‌های نیازمند بررسی مجدد: {stats.FailedRewardCount:N0}\n\n" +
            $"حداقل پرداخت واجدشرایط: {_appConfig.Referral.MinimumEligiblePaymentAmountToman:N0} تومان";

        return await botClient.SendReferralTextMessageAsync(
            chatId: message.Chat.Id,
            text: text,
            replyMarkup: MainReplyMarkupKeyboardFa(),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds one clickable, HTML-safe support reference for an owned bot customer message.
    /// </summary>
    /// <param name="supportAccount">
    /// Support value from the current owned bot configuration. A public Telegram username may be supplied as
    /// <c>username</c>, <c>@username</c>, <c>t.me/username</c>, or <c>https://t.me/username</c>.
    /// </param>
    /// <returns>
    /// A clickable HTML anchor whose visible text is <c>@username</c>, or an empty string when no valid public
    /// Telegram support username has been configured for the active bot.
    /// </returns>
    /// <remarks>
    /// This helper is intentionally owned-bot scoped. Tenant storefront support formatting remains in
    /// <see cref="TenantBotService"/> because tenant owners manage a separate support setting.
    /// </remarks>
    private static string BuildOwnedBotSupportContactHtml(string supportAccount)
    {
        if (string.IsNullOrWhiteSpace(supportAccount))
            return string.Empty;

        var normalized = supportAccount.Trim();
        if (normalized.StartsWith("@http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("@https://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("@t.me/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("@telegram.me/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        if (normalized.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "t.me", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "telegram.me", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = uri.AbsolutePath.Trim('/');
        }

        normalized = normalized.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return string.Empty;
        }

        var safeUsername = Html(normalized);
        return $"<a href=\"https://t.me/{safeUsername}\">@{safeUsername}</a>";
    }

    /// <summary>
    /// Sends the current owned bot's blocked-user notice with its own configured support contact.
    /// </summary>
    /// <param name="botClient">Telegram client for the bot that blocked the user.</param>
    /// <param name="chatId">Telegram chat id that should receive the notice.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send operation.</param>
    /// <returns>A task that completes after Telegram accepts the notice or throws a delivery exception.</returns>
    /// <remarks>
    /// Support identity must stay brand-scoped here as well; an empty support setting is reported instead of leaking
    /// the default bot's contact to users of another owned brand.
    /// </remarks>
    private async Task SendBlockedUserMessageAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var support = BuildOwnedBotSupportContactHtml(CurrentSupportAccount);
        var supportText = string.IsNullOrWhiteSpace(support)
            ? "پشتیبانی این ربات تنظیم نشده است."
            : support;

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $"به علت تخلف مسدود شدید و امکان استفاده از ربات را ندارید.\nبرای پیگیری می‌توانید به پشتیبانی تلگرام پیام بدهید:\n{supportText}",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }

    static ReplyKeyboardMarkup GetAccountTypeKeyboard()
    {
        // Create an inline keyboard with the available account types

        var keyboard = new ReplyKeyboardMarkup(new[]
        {
        new[]
        {
            new KeyboardButton("All operators"),
        },
        new[]
        {
            new KeyboardButton("Reality Ipv6"),
        }
        });

        return keyboard;
    }


    /// <summary>
    /// Creates one legacy x-ui account after obtaining a session cookie for the selected panel.
    /// </summary>
    /// <param name="accountDto">
    /// Account creation request assembled by the Telegram purchase flow. It must include a non-null
    /// <see cref="AccountDto.ServerInfo"/> with a valid panel URL and inbound configuration.
    /// </param>
    /// <returns>
    /// <c>true</c> when login succeeds and the x-ui add-client request reports success; otherwise <c>false</c>.
    /// Exceptions are caught and converted to <c>false</c> so Telegram update handling does not crash.
    /// </returns>
    /// <remarks>
    /// This is the legacy non-v3 account creation path used by <c>FinalizeCustomerAccount</c>. A null return from
    /// <see cref="ApiService.LoginAndGetSessionCookie"/> is treated as a recoverable login failure.
    /// </remarks>
    async Task<bool> CreateAccount(AccountDto accountDto)
    {
        try
        {
            if (accountDto?.ServerInfo == null)
            {
                Console.WriteLine("[CreateAccount] AccountDto or ServerInfo is null.");
                return false;
            }

            var sessionCookie = await ApiService.LoginAndGetSessionCookie(accountDto.ServerInfo);
            if (!string.IsNullOrWhiteSpace(sessionCookie))
            {
                accountDto.SessionCookie = sessionCookie;
                // var selectedCountry = "🇸🇪 Sweden";
                // var selectedPeriod = "2 Months";

                return await ApiService.CreateUserAccount(accountDto);
            }

            Console.WriteLine($"[CreateAccount] Could not obtain session cookie. user={accountDto.TelegramUserId}, panel={accountDto.ServerInfo.Url}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreateAccount] Failed. user={accountDto?.TelegramUserId}, error={ex}");
            return false;
        }
    }

    async Task<bool> UpdateAccount(AccountDtoUpdate accountDto)
    {
        bool result;
        var sessionCookie = await ApiService.LoginAndGetSessionCookie(accountDto.ServerInfo);
        if (sessionCookie != null)
        {
            accountDto.SessionCookie = sessionCookie;
            result = await ApiService.UpdateUserAccount(accountDto);
        }
        else
        {
            // Handle the case where login fails
            result = false;
        }
        return result;
    }

    static bool StartsWithVMessOrVLess(string value)
    {
        return value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase);
    }
    static bool StartsWithEnableOrDisable(string value)
    {
        return value.StartsWith("/disable_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/enable_", StringComparison.OrdinalIgnoreCase);
    }

    static ServerInfo GetConfigServer(VMessConfiguration vmess)
    {

        if (VMessConfiguration.ArePropertiesNotNullOrEmpty(vmess, null))
        {
            // Access the server information from the servers.json file
            var serversJson = ReadJsonFile.ReadJsonAsString();
            var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

            // Iterate over the dictionary
            foreach (var kvp in servers)
            {
                string country = kvp.Key;
                ServerInfo serverInfo = kvp.Value;
                if (serverInfo.VmessTemplate.Add == vmess.Add)
                {
                    serverInfo.Inbounds = new List<Inbound> { serverInfo.Inbounds.FirstOrDefault(i => i.Port.ToString() == vmess.Port) };
                    serverInfo.VmessTemplate.Port = vmess.Port;
                    return serverInfo;
                }
            }

            throw new Exception("Your Vmess Link is not for us! Try again ...");

        }
        else
        {

            throw new Exception("Your Vmess Link is not completed!");
        }


    }

    static ServerInfo GetConfigServerFromVless(Vless vless)
    {

        if (vless.Domain != null)
        {
            // Access the server information from the servers.json file
            var serversJson = ReadJsonFile.ReadJsonAsString();
            var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

            // Iterate over the dictionary
            foreach (var kvp in servers)
            {
                string country = kvp.Key;
                ServerInfo serverInfo = kvp.Value;
                if (serverInfo.Vless.Domain == vless.Domain)
                {
                    return serverInfo;
                }
            }

            throw new Exception("Your Vmess Link is not for us! Try again ...");

        }
        else
        {
            throw new Exception("Your Vmess Link is not completed!");
        }
    }

    async Task<ClientExtend> TryGetClient(string messageText)
    {
        ClientExtend client = null;

        // Handle "Get Account Info" button click
        // You can implement the logic for this button here
        // For example, retrieve and display account information

        //vmess
        if (messageText.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var vmess = VMessConfiguration.DecodeVMessLink(messageText);
                ServerInfo serverInfo = GetConfigServer(vmess);
                var inbound = serverInfo.Inbounds.FirstOrDefault(i => i.Type == "tunnel");
                if (inbound == null) return null;
                client = await ApiService.FetchClientFromServer(vmess.Id, serverInfo, inbound.Id);

                //var inboundIds = new List<int>();
                //serverInfo.Inbounds.ForEach(i => inboundIds.Add(i.Id));
            }

            catch (System.Exception ex)
            {

                Console.WriteLine(ex.Message);
            }
        }
        //vless
        else
        {
            try
            {
                var vless = Vless.DecodeVlessLink(messageText);
                var serverInfo = GetConfigServerFromVless(vless);
                var inbound = serverInfo.Inbounds.FirstOrDefault(i => i.Type == "realityv6");
                if (inbound == null) return null;
                client = await ApiService.FetchClientFromServer(vless.Id, serverInfo, inbound.Id);
            }
            catch (System.Exception ex)
            {

                Console.WriteLine(ex.Message);
            }


        }
        return client;

    }



    async Task<List<ClientExtend>> TryGetَAllClient(long telegramUserId)
    {
        List<ClientExtend> clients = new List<ClientExtend>();

        var serversJson = ReadJsonFile.ReadJsonAsString();
        var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);

        foreach (var s in servers)
        {
            ServerInfo serverInfo = s.Value;
            foreach (var inbound in serverInfo.Inbounds)
            {
                if (s.Key == "Vpnnetiran")
                    Console.WriteLine("seen");
                if (inbound.Type == "tunnel")
                {
                    try
                    {
                        var temp = await ApiService.FetchAllClientFromServer(telegramUserId, serverInfo, inbound.Id);

                        if (temp.Count > 0)
                            clients.AddRange(temp);

                    }
                    catch (System.Exception ex)
                    {

                        Console.WriteLine(ex.Message);
                    }

                }
            }

        }
        return clients;

    }

    async Task<bool> CheckUserPhoneNumber(long chatId, Message message)
    {
        long? senderID = message?.From?.Id;
        long? contactID = message?.Contact?.UserId;
        if (senderID == contactID && senderID != null && contactID != null)
        {

            string phoneNumber = message.Contact.PhoneNumber;

            // Check if the phone number starts with the Iranian country code
            bool isIranianPhoneNumber = phoneNumber.StartsWith("98") || phoneNumber.StartsWith("+98") || phoneNumber.StartsWith("0098");

            // Check the length to be sure (country code + 10 digits)
            if (isIranianPhoneNumber && (phoneNumber.Length == 12 || phoneNumber.Length == 13 || phoneNumber.Length == 14))
            {
                return true;

            }
            else
            {
                await ActiveBotClient.SendTextMessageAsync(chatId: chatId,
                                                   text: "خطا. لطفاً شماره اکانت خودتان با شماره واقعی را وارد کنید.",
                                                   replyMarkup: MainReplyMarkupKeyboardFa());
                return false;
            }


        }
        else
        {
            await ActiveBotClient.SendTextMessageAsync(chatId: chatId,
                                                   text: "خطا. لطفاً شماره اکانت خودتان را وارد کنید.",
                                                   replyMarkup: MainReplyMarkupKeyboardFa());
        }
        return false;
    }


    private ReplyKeyboardMarkup GetPhoneNumber()
    {
        ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
            {
                // Row with the 'send contact' button
                new KeyboardButton[]
                {
                    KeyboardButton.WithRequestContact("ارسال شماره تلفن")
                },
                // Row with the 'cancel' button
                new KeyboardButton[]
                {
                    new KeyboardButton("لغو") // Replace "لغو" with the text you want for the cancellation button
                }
            })
        {
            ResizeKeyboard = true, // Set to true to fit the keyboard size to its buttons
            OneTimeKeyboard = true // Optional: set to true to hide the keyboard after a button is pressed
        };
        return replyKeyboardMarkup;
    }

    private ChannelInfo GetChannelAndPost(string link)
    {


        ChannelInfo channelInfo = null;
        var match = Regex.Match(link?.Trim() ?? string.Empty, @"^https?://t\.me/(?<channelname>@?[A-Za-z0-9_]+)/(?<postnumber>\d+)/?$", RegexOptions.IgnoreCase);
        // var match = Regex.Match(link, @"https://t.me/(?<channelname>[^/]+)/(?<postnumber>\d+)");
        if (match.Success)
        {
            string channelName = match.Groups["channelname"].Value;
            int postNumber = int.Parse(match.Groups["postnumber"].Value);

            channelInfo = new ChannelInfo { PostNumber = postNumber, ChannelName = channelName };

        }
        else
        {
            Console.WriteLine("Normal public message");
        }
        return channelInfo;

    }

    private static ChatId BuildForwardSourceChatId(string channelName)
    {
        var value = (channelName ?? string.Empty).Trim();
        if (long.TryParse(value, out var numericChatId))
            return new ChatId(numericChatId);

        if (!value.StartsWith("@", StringComparison.Ordinal))
            value = "@" + value;

        return new ChatId(value);
    }
}

