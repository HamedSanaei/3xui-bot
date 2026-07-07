using System.Globalization;
using System.Net;
using System.Text;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// owns all colleague tenant Bot behavior.
/// it has two RESPONSIBILITIES:
/// 1) owner-side setup from the Main brand Bot, and
/// 2) customer-side storefront purchase, payment, fulfillment, and ledger accounting inside tenant bots.
/// </summary>
public class TenantBotService
{
    public const string OwnerMenuButton = "🛒 فعالسازی ربات فروشگاهی";

    private const string OWNERCALLBACKPREFIX = "TBM:";
    private const string CUSTOMERCALLBACKPREFIX = "TN:";
    private const string OWNERFLOW = "TENANTBOT-owner";
    private const string TENANTPURCHASEFLOW = "TENANTBOT-purchase";
    private const string TENANTPURCHASESTEPTRAFFIC = "purchase-traffic";
    private const string TENANTPURCHASESTEPDURATION = "purchase-duration";
    private const string STEPTOKEN = "Token";
    private const string STEPMARKUP = "markup";
    private const string STEPSUPPORT = "support";
    private const string STEPWELCOME = "WELCOME";
    private const string STEPCARDNUMBER = "card-number";
    private const string STEPCARDHOLDER = "card-HOLDER";
    private const string STEPMANDATORYCHANNEL = "mandatory-channel";
    private const string STEPMANUALCARDORDERID = "manual-card-order-id";
    private const string STEPTUTORIALTITLE = "tutorial-title";
    private const string STEPTUTORIALURL = "tutorial-url";
    private const string STEPBROADCASTINPUT = "broadcast-input";
    private const string TENANTRENEWFLOW = "TENANTBOT-renew";
    private const string TENANTRENEWSTEPACCOUNT = "renew-account";
    private const string TENANTRENEWSTEPTRAFFIC = "renew-traffic";
    private const string TENANTRENEWSTEPDURATION = "renew-duration";
    private const string TENANTRENEWSTEPUNLIMITEDPLAN = "renew-unlimited-plan";
    private const string TENANTRENEWSTEPCONFIRM = "renew-confirm";

    private readonly UserDbContext _userDbcontext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly HooshPay _hooshPay;
    private readonly NowPayments _nowPayments;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly XuiV3BotFlowService _xuiV3BotFlowService;
    private readonly WalletLedgerService _walletLedgerService;
    private readonly SalesAssistantService _salesAssistantService;
    private readonly GozargahSiteSyncService _gozargahSiteSyncService;
    private readonly BroadcastManager _broadcastManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantBotService> _logger;

    /// <summary>
    /// creates the tenant Bot service with all dependencies needed for owner setup, customer storefronts, payments, and xui account creation.
    /// </summary>
    /// <param name="UserDbContext">users.db context for Bot instances, orders, payments, and ledger rows.</param>
    /// <param name="CredentialsDbContext">credentials.db context for Shared User profiles and owner wallet balances.</param>
    /// <param name="Configuration">Application Configuration.</param>
    /// <param name="purchaseService">xui v3 purchase and account creation service.</param>
    /// <param name="HooshPay">HooshPay API client used to Create and Verify invoices.</param>
    /// <param name="NowPayments">NOWPayments API client used to create and verify tenant crypto invoices.</param>
    /// <param name="BotRegistry">runtime Bot registry.</param>
    /// <param name="BotClientProvider">Telegram client Provider for owned and tenant bots.</param>
    /// <param name="BotContextAccessor">current Bot context accessor.</param>
    /// <param name="xuiV3BotFlowService">
    /// Shared XUI v3 customer-flow service reused for tenant account search, account list, renewal, and
    /// account-management callbacks so the tenant bot does not duplicate owned-bot account logic.
    /// </param>
    /// <param name="WalletLedgerService">
    /// Append-only wallet ledger writer used for tenant sales, owner profit, card-payment debits, and audit views.
    /// </param>
    /// <param name="SalesAssistantService">
    /// Sales Assistant notifier that sends tenant sale and receipt-review events to the colleague owner.
    /// </param>
    /// <param name="GozargahSiteSyncService">
    /// Site sync service used after successful tenant XUI operations. Tenant records are owned by the
    /// colleague owner on the website while preserving the buyer Telegram id for audit and customer support.
    /// </param>
    /// <param name="BroadcastManager">
    /// Shared broadcast queue used for tenant public messages. Tenant broadcasts use the tenant bot as the
    /// sender, but keep progress/status updates in the owned bot chat that started the job.
    /// </param>
    /// <param name="ServiceProvider">service Provider used to REACH MultiBotHostedService for runtime TOGGLE.</param>
    /// <param name="Logger">Logger used for tenant payment/audit channel logs.</param>
    public TenantBotService(
        UserDbContext UserDbContext,
        CredentialsDbContext CredentialsDbContext,
        IConfiguration Configuration,
        XuiV3PurchaseService purchaseService,
        HooshPay HooshPay,
        NowPayments NowPayments,
        BotRegistry BotRegistry,
        BotClientProvider BotClientProvider,
        BotContextAccessor BotContextAccessor,
        XuiV3BotFlowService xuiV3BotFlowService,
        WalletLedgerService WalletLedgerService,
        SalesAssistantService SalesAssistantService,
        GozargahSiteSyncService GozargahSiteSyncService,
        BroadcastManager BroadcastManager,
        IServiceProvider ServiceProvider,
        ILogger<TenantBotService> Logger)
    {
        _userDbcontext = UserDbContext;
        _credentialsDbContext = CredentialsDbContext;
        _configuration = Configuration;
        _appConfig = Configuration.Get<AppConfig>() ?? new AppConfig();
        _purchaseService = purchaseService;
        _hooshPay = HooshPay;
        _nowPayments = NowPayments;
        _botRegistry = BotRegistry;
        _botClientProvider = BotClientProvider;
        _botContextAccessor = BotContextAccessor;
        _xuiV3BotFlowService = xuiV3BotFlowService;
        _walletLedgerService = WalletLedgerService;
        _salesAssistantService = SalesAssistantService;
        _gozargahSiteSyncService = GozargahSiteSyncService;
        _broadcastManager = BroadcastManager;
        _serviceProvider = ServiceProvider;
        _logger = Logger;
    }

    /// <summary>
    /// checks whether callback Data belongs to the owner-side tenant management panel.
    /// </summary>
    /// <param name="Data">Raw Telegram callback Data.</param>
    /// <returns>true when the callback starts with the owner management prefix.</returns>
    public bool IsOwnerCallback(string Data)
    {
        return Data?.StartsWith(OWNERCALLBACKPREFIX, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// checks whether callback Data belongs to the tenant customer storefront.
    /// </summary>
    /// <param name="Data">Raw Telegram callback Data.</param>
    /// <returns>true when the callback starts with the customer storefront prefix.</returns>
    public bool IsCustomerCallback(string Data)
    {
        return Data?.StartsWith(CUSTOMERCALLBACKPREFIX, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Handles Text messages from A colleague inside the Main brand Bot while CONFIGURING A tenant storefront.
    /// </summary>
    /// <param name="botClient">Telegram client of the Main brand Bot that received the Message.</param>
    /// <param name="Message">Incoming owner Message.</param>
    /// <param name="CredUser">Shared credentials profile of the sender.</param>
    /// <param name="User">Bot-scoped conversation state for the sender.</param>
    /// <param name="mainReplyMarkup">Main Menu keyboard to Use when the Flow ENDS or is Rejected.</param>
    /// <param name="CancellationToken">Cancellation Token for async Telegram/database calls.</param>
    /// <returns>true when this Message was handled by tenant setup; false when caller should continue normal routing.</returns>
    /// <remarks>
    /// Owner setup state is scoped to the current owned bot through <see cref="UserDbContext"/>.
    /// The text button <c>بازگشت به پنل</c> is treated as a cancellation for any pending owner setting input,
    /// clears the temporary state, and returns the colleague to the tenant storefront panel without changing
    /// token, support, payment, card, or tutorial settings.
    /// </remarks>
    public async Task<bool> TryHandleOwnerMessageAsync(
        ITelegramBotClient botClient,
        Message Message,
        CredUser CredUser,
        User User,
        IReplyMarkup mainReplyMarkup,
        CancellationToken CancellationToken)
    {
        if (Message?.From == null || string.IsNullOrWhiteSpace(Message.Text))
            return false;

        // owner-side Flow Runs inside the Main brand Bot and Configures one tenant storefront.
        if (Message.Text == OwnerMenuButton)
        {
            if (CredUser?.IsColleague != true)
            {
                await botClient.SendTextMessageAsync(
                    chatId: Message.Chat.Id,
                    text: "این بخش فقط برای همکاران فعال است.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: CancellationToken);
                return true;
            }

            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, CredUser, null, CancellationToken);
            return true;
        }

        if (!string.Equals(User?.Flow, OWNERFLOW, StringComparison.Ordinal))
            return false;

        if (CredUser?.IsColleague != true)
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            await botClient.SendTextMessageAsync(
                chatId: Message.Chat.Id,
                text: "این بخش فقط برای همکاران فعال است.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: CancellationToken);
            return true;
        }

        var step = User.LastStep ?? string.Empty;
        if (string.Equals(Message.Text.Trim(), "بازگشت به پنل", StringComparison.Ordinal))
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                "به پنل ربات فروشگاهی برگشتید.",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: CancellationToken);
            await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, CredUser, null, CancellationToken);
            return true;
        }

        if (step == STEPTOKEN)
        {
            await SAVETENANTBOTTOKENASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPMARKUP)
        {
            await SAVETENANTMARKUPASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPSUPPORT)
        {
            await SAVETENANTSUPPORTASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPWELCOME)
        {
            await SAVETENANTWELCOMEASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPCARDNUMBER)
        {
            await SAVETENANTCARDNUMBERASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPCARDHOLDER)
        {
            await SAVETENANTCARDHOLDERASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPMANDATORYCHANNEL)
        {
            await SAVETENANTMANDATORYCHANNELASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPMANUALCARDORDERID)
        {
            await CONFIRMMANUALCARDORDERBYORDERIDASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        if (step == STEPTUTORIALTITLE || step == STEPTUTORIALURL)
        {
            await SAVETENANTTUTORIALSTEPASYNC(botClient, Message, CredUser, User, CancellationToken);
            return true;
        }

        if (step == STEPBROADCASTINPUT)
        {
            await PREPARETENANTBROADCASTASYNC(botClient, Message, CredUser, CancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles inline callbacks from the colleague tenant management panel.
    /// </summary>
    /// <param name="botClient">Telegram client of the Main brand Bot.</param>
    /// <param name="CallbackQuery">Incoming callback Query.</param>
    /// <param name="CredUser">credentials profile of the colleague owner.</param>
    /// <param name="User">current Bot-scoped conversation state.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <returns>true when the callback belongs to tenant owner management; otherwise false.</returns>
    public async Task<bool> TryHandleOwnerCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser CredUser,
        User User,
        CancellationToken CancellationToken)
    {
        if (!IsOwnerCallback(CallbackQuery?.Data))
            return false;

        if (CredUser?.IsColleague != true)
        {
            await SafeAnswerCallbackQueryAsync(botClient, 
                CallbackQuery.Id,
                text: "این بخش فقط برای همکاران فعال است.",
                showAlert: true,
                cancellationToken: CancellationToken);
            return true;
        }

        var action = CallbackQuery.Data[OWNERCALLBACKPREFIX.Length..];
        var ChatId = CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id;
        var MessageId = CallbackQuery.Message?.MessageId;

        if (action == "panel")
        {
            await SHOWOWNERPANELASYNC(botClient, ChatId, CredUser, MessageId, CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return true;
        }

        if (action == "reset")
        {
            await SHOWTENANTRESETCONFIRMATIONASYNC(botClient, CallbackQuery, CancellationToken);
            return true;
        }

        if (action == "reset-confirm")
        {
            await RESETTENANTSETTINGSASYNC(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action == "TOGGLE")
        {
            await TOGGLETENANTBOTASYNC(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action.StartsWith("TOGGLE:", StringComparison.Ordinal))
        {
            await TOGGLETENANTSETTINGASYNC(botClient, CallbackQuery, CredUser, action["TOGGLE:".Length..], CancellationToken);
            return true;
        }

        if (action == "settlement")
        {
            await SafeAnswerCallbackQueryAsync(botClient, 
                CallbackQuery.Id,
                "برداشت در نسخه بعدی فعال می‌شود.",
                showAlert: true,
                cancellationToken: CancellationToken);
            return true;
        }

        if (action == "orders")
        {
            await SHOWOWNERORDERSASYNC(botClient, CallbackQuery, CredUser, 0, CancellationToken);
            return true;
        }

        if (action.StartsWith("orders:", StringComparison.Ordinal))
        {
            var pageText = action["orders:".Length..];
            var page = int.TryParse(pageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage)
                ? Math.Max(0, parsedPage)
                : 0;
            await SHOWOWNERORDERSASYNC(botClient, CallbackQuery, CredUser, page, CancellationToken);
            return true;
        }

        if (action.StartsWith("order:", StringComparison.Ordinal))
        {
            await SHOWOWNERORDERDETAILASYNC(botClient, CallbackQuery, CredUser, action["order:".Length..], CancellationToken);
            return true;
        }

        if (action == "ledger")
        {
            await ShowOwnerLedgerHintAsync(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action == "guide")
        {
            await SHOWOWNERGUIDEASYNC(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action == "assistant-test")
        {
            await TESTSALESASSISTANTASYNC(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action == "manual-card-confirm")
        {
            await STARTMANUALCARDORDERCONFIRMASYNC(botClient, CallbackQuery, CancellationToken);
            return true;
        }

        if (action == "tutorials")
        {
            await SHOWTENANTTUTORIALMANAGERASYNC(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action == "tutorial-add")
        {
            await STARTTENANTTUTORIALADDASYNC(botClient, CallbackQuery, CancellationToken);
            return true;
        }

        if (action.StartsWith("tutorial-del:", StringComparison.Ordinal))
        {
            await DELETETENANTTUTORIALASYNC(botClient, CallbackQuery, CredUser, action["tutorial-del:".Length..], CancellationToken);
            return true;
        }

        if (action == "broadcast")
        {
            await STARTTENANTBROADCASTASYNC(botClient, CallbackQuery, CancellationToken);
            return true;
        }

        if (action.StartsWith("broadcast-send:", StringComparison.Ordinal))
        {
            await SENDTENANTBROADCASTASYNC(botClient, CallbackQuery, CredUser, action["broadcast-send:".Length..], CancellationToken);
            return true;
        }

        if (action == "stats")
        {
            await SHOWTENANTDAILYSTATSASYNC(botClient, CallbackQuery, CredUser, CancellationToken);
            return true;
        }

        if (action.StartsWith("set:", StringComparison.Ordinal))
        {
            var field = action.Replace("set:", "", StringComparison.Ordinal);
            await STARTOWNERINPUTASYNC(botClient, CallbackQuery, field, CancellationToken);
            return true;
        }

        return true;
    }

    /// <summary>
    /// Handles an update received by A tenant storefront Bot.
    /// tenant bots do not expose the Main Bot menus; this method CONSUMES their messages and callbacks.
    /// </summary>
    /// <param name="botClient">Telegram client for the tenant Bot that received the update.</param>
    /// <param name="update">Raw Telegram update.</param>
    /// <param name="CredUser">Shared credentials profile of the customer.</param>
    /// <param name="User">Bot-scoped conversation state for the customer.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <returns>true when current Bot is A tenant Bot and update has been handled; otherwise false.</returns>
    public async Task<bool> TryHandleTenantUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CredUser CredUser,
        User User,
        CancellationToken CancellationToken)
    {
        if (!string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
            return false;

        // tenant bots expose A SMALL storefront only; they should not FALL through to the Main Bot Menu.
        if (update.CallbackQuery is { } CallbackQuery)
        {
            var tenant = await GetCurrentTenantBotAsync(CancellationToken);
            if (tenant != null)
                await TouchTenantCustomerStateAsync(tenant, CallbackQuery.From.Id, CancellationToken);

            if (IsCustomerCallback(CallbackQuery.Data))
            {
                await HANDLECUSTOMERCALLBACKASYNC(botClient, CallbackQuery, CredUser, User, CancellationToken);
                return true;
            }

            // Account-management keyboards are produced by XuiV3BotFlowService and do not use the tenant
            // callback prefix. Route them to the shared flow under the current tenant bot context instead of
            // falling through to owned-bot menus.
            await _xuiV3BotFlowService.TryHandleCallbackAsync(
                botClient,
                CallbackQuery,
                CredUser,
                User,
                BuildTenantReplyKeyboard(),
                CancellationToken);
            return true;
        }

        if (update.Message is not { } Message || Message.From == null)
            return true;

        var messageTenant = await GetCurrentTenantBotAsync(CancellationToken);
        if (messageTenant != null)
            await TouchTenantCustomerStateAsync(messageTenant, Message.From.Id, CancellationToken);

        await HANDLECUSTOMERMESSAGEASYNC(botClient, Message, CredUser, User, CancellationToken);
        return true;
    }

    /// <summary>
    /// Ensures a tenant customer has a bot-scoped state row so tenant broadcasts can target everyone who started the storefront.
    /// </summary>
    /// <param name="tenant">
    /// Tenant bot that received the Telegram update. The tenant id is the local runtime id, not the Telegram bot id.
    /// </param>
    /// <param name="telegramUserId">
    /// Numeric Telegram user id from the incoming message or callback sender. This value is tenant-scoped with
    /// <paramref name="tenant"/> and is used as the broadcast audience key.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to cancel the users.db lookup and write when the update receiver is shutting down.
    /// </param>
    /// <returns>A task that completes after the state row has been inserted or touched.</returns>
    /// <remarks>
    /// This method deliberately does not call <see cref="UserDbContext.SaveUserStatus(User)"/> because that
    /// method applies partial legacy flow updates. Broadcast audience tracking must not overwrite purchase,
    /// renewal, receipt-upload, or comment-change state; it only creates a missing row or updates the timestamp.
    /// </remarks>
    private async Task TouchTenantCustomerStateAsync(BotInstance tenant, long telegramUserId, CancellationToken cancellationToken)
    {
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.Id) || telegramUserId <= 0)
            return;

        var existing = await _userDbcontext.BotUserStates.FirstOrDefaultAsync(
            x => x.BotId == tenant.Id && x.TelegramUserId == telegramUserId,
            cancellationToken);

        if (existing == null)
        {
            _userDbcontext.BotUserStates.Add(BotUserState.FromUser(tenant.Id, new User { Id = telegramUserId }));
        }
        else
        {
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _userDbcontext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// sends or edits the owner management panel that SHOWS Token, Username, support, WELCOME Text, markup, and enabled state.
    /// </summary>
    /// <param name="botClient">Telegram client used to Send/edit the panel.</param>
    /// <param name="ChatId">owner chat Id.</param>
    /// <param name="owner">colleague owner profile.</param>
    /// <param name="MessageId">Message Id to edit; null sends A new Message.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <remarks>
    /// Opening or refreshing the panel probes an existing tenant bot token with Telegram <c>getMe</c>. If Telegram
    /// proves the token is revoked or unauthorized, the method clears only the token identity fields and disables
    /// the tenant bot before rendering, while preserving card, support, tutorial, and historical order settings.
    /// Transient Telegram failures are shown as a warning and never clear the owner configuration.
    /// </remarks>
    private async Task SHOWOWNERPANELASYNC(
        ITelegramBotClient botClient,
        ChatId ChatId,
        CredUser owner,
        int? MessageId,
        CancellationToken CancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, CancellationToken);
        var tokenNotice = await VALIDATETENANTTOKENFORPANELASYNC(tenant, CancellationToken);
        var Text = BUILDOWNERPANELTEXT(tenant, owner, tokenNotice);
        var keyboard = BUILDOWNERPANELKEYBOARD(tenant);

        if (MessageId.HasValue)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: ChatId,
                messageId: MessageId.Value,
                text: Text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: ChatId,
            text: Text,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Builds the owner management panel Text from the persisted tenant Bot row.
    /// </summary>
    /// <param name="tenant">current tenant Bot row, or null if the owner has not created one yet.</param>
    /// <param name="owner">colleague owner profile.</param>
    /// <param name="tokenNotice">Optional HTML-safe status line produced by token validation before panel rendering.</param>
    /// <returns>Html-formatted panel Text.</returns>
    private string BUILDOWNERPANELTEXT(BotInstance tenant, CredUser owner, string tokenNotice = null)
    {
        var hasToken = !string.IsNullOrWhiteSpace(tenant?.Token);
        var IsEnabled = tenant?.Enabled == true;
        var Username = string.IsNullOrWhiteSpace(tenant?.Username) ? "ثبت نشده" : "@" + tenant.Username.TrimStart('@');
        var support = string.IsNullOrWhiteSpace(tenant?.SupportAccount) ? "ثبت نشده" : tenant.SupportAccount;
        var markup = tenant?.TenantPriceMarkupPercent ?? 0;
        var card = tenant?.TenantCardPaymentEnabled == true && !string.IsNullOrWhiteSpace(tenant.TenantCardNumber)
            ? $"{tenant.TenantCardNumber} - {tenant.TenantCardHolderName}"
            : "ثبت نشده/خاموش";
        var TENANTJOIN = tenant?.TenantMandatoryJoinEnabled == true
            ? $"روشن ({string.Join(", ", GETTENANTCHANNELIDS(tenant))})"
            : "خاموش";
        var WELCOME = string.IsNullOrWhiteSpace(tenant?.TenantWelcomeText) ? "ثبت نشده" : "ثبت شده";

        var text = "🛒 <b>ربات فروشگاهی همکار</b>\n\n" +
                   "با این بخش می‌توانید ربات فروشگاهی خودتان را با توکن BotFather فعال کنید. مشتری‌ها داخل همان ربات خرید می‌کنند، بعد از پرداخت موفق اکانت ساخته می‌شود و سود سفارش به موجودی شما اضافه می‌شود.\n\n";

        if (!string.IsNullOrWhiteSpace(tokenNotice))
            text += tokenNotice + "\n\n";

        return text +
               $"{STATUSICON(hasToken)} توکن ربات: <code>{Html(hasToken ? "ثبت شده" : "ثبت نشده")}</code>\n" +
               $"{STATUSICON(hasToken)} یوزرنیم ربات: <code>{Html(Username)}</code>\n" +
               $"{STATUSICON(true)} درصد سود روی قیمت همکار: <code>{markup}%</code>\n" +
               $"{STATUSICON(!string.IsNullOrWhiteSpace(tenant?.SupportAccount))} پشتیبانی فروشگاه: <code>{Html(support)}</code>\n" +
               $"{STATUSICON(!string.IsNullOrWhiteSpace(tenant?.TenantWelcomeText))} متن خوشامد: <code>{Html(WELCOME)}</code>\n" +
               $"{STATUSICON(tenant?.TenantHooshPayEnabled == true)} درگاه هوش‌پی: <b>{Html(tenant?.TenantHooshPayEnabled == true ? "روشن" : "خاموش")}</b>\n" +
               $"{STATUSICON(tenant?.TenantNowPaymentsEnabled == true)} درگاه ارز دیجیتال: <b>{Html(tenant?.TenantNowPaymentsEnabled == true ? "روشن" : "خاموش")}</b>\n" +
               $"{STATUSICON(tenant?.TenantCardPaymentEnabled == true)} کارت به کارت همکار: <code>{Html(card)}</code>\n" +
               $"{STATUSICON(tenant?.TenantMandatoryJoinEnabled == true)} جوین اجباری فروشگاه: <code>{Html(TENANTJOIN)}</code>\n" +
               $"{STATUSICON(IsEnabled)} وضعیت: <b>{Html(IsEnabled ? "روشن" : "خاموش")}</b>\n\n" +
               "اگر درصد سود را صفر بگذارید، قیمت فروش با تعرفه کاربر عادی محاسبه می‌شود و سود شما اختلاف قیمت کاربر عادی و قیمت همکار خواهد بود.";
    }

    /// <summary>
    /// Builds inline buttons for EDITING tenant settings and TOGGLING storefront status.
    /// </summary>
    /// <param name="tenant">current tenant Bot row.</param>
    /// <returns>inline keyboard for the owner panel.</returns>
    private static InlineKeyboardMarkup BUILDOWNERPANELKEYBOARD(BotInstance tenant)
    {
        var IsEnabled = tenant?.Enabled == true;
        var HOOSHPAYENABLED = tenant?.TenantHooshPayEnabled == true;
        var NOWPAYMENTSENABLED = tenant?.TenantNowPaymentsEnabled == true;
        var CARDENABLED = tenant?.TenantCardPaymentEnabled == true;
        var JOINENABLED = tenant?.TenantMandatoryJoinEnabled == true;

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🤖 ثبت/تغییر توکن", OWNERCALLBACKPREFIX + "set:Token"),
                InlineKeyboardButton.WithCallbackData("📈 درصد سود", OWNERCALLBACKPREFIX + "set:markup")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("💬 پشتیبانی", OWNERCALLBACKPREFIX + "set:support"),
                InlineKeyboardButton.WithCallbackData("👋 متن خوشامد", OWNERCALLBACKPREFIX + "set:WELCOME")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("💳 شماره کارت", OWNERCALLBACKPREFIX + "set:card-number"),
                InlineKeyboardButton.WithCallbackData("👤 صاحب کارت", OWNERCALLBACKPREFIX + "set:card-HOLDER")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(CARDENABLED ? "✅ کارت‌به‌کارت" : "❌ کارت‌به‌کارت", OWNERCALLBACKPREFIX + "TOGGLE:card"),
                InlineKeyboardButton.WithCallbackData(HOOSHPAYENABLED ? "✅ هوش‌پی" : "❌ هوش‌پی", OWNERCALLBACKPREFIX + "TOGGLE:HooshPay")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(NOWPAYMENTSENABLED ? "✅ ارز دیجیتال" : "❌ ارز دیجیتال", OWNERCALLBACKPREFIX + "TOGGLE:NowPayments"),
                InlineKeyboardButton.WithCallbackData(JOINENABLED ? "✅ جوین اجباری" : "❌ جوین اجباری", OWNERCALLBACKPREFIX + "TOGGLE:join")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📣 کانال جوین", OWNERCALLBACKPREFIX + "set:mandatory-channel"),
                InlineKeyboardButton.WithCallbackData("🧾 سفارش‌ها", OWNERCALLBACKPREFIX + "orders")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🎓 آموزش‌ها", OWNERCALLBACKPREFIX + "tutorials"),
                InlineKeyboardButton.WithCallbackData("📢 پیام عمومی", OWNERCALLBACKPREFIX + "broadcast")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📊 آمار روزانه", OWNERCALLBACKPREFIX + "stats"),
                InlineKeyboardButton.WithCallbackData("📒 تراکنش‌ها", OWNERCALLBACKPREFIX + "ledger"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("💸 تسویه حساب", OWNERCALLBACKPREFIX + "settlement"),
                InlineKeyboardButton.WithCallbackData("📘 راهنمای پنل همکاری", OWNERCALLBACKPREFIX + "guide")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🧪 تست دستیار فروش", OWNERCALLBACKPREFIX + "assistant-test"),
                InlineKeyboardButton.WithCallbackData("✅ تایید دستی کارت‌به‌کارت", OWNERCALLBACKPREFIX + "manual-card-confirm")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔄 بروزرسانی", OWNERCALLBACKPREFIX + "panel")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("♻️ بازنشانی تنظیمات ربات", OWNERCALLBACKPREFIX + "reset")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(IsEnabled ? "⛔ خاموش کردن فروشگاه" : "✅ روشن کردن فروشگاه", OWNERCALLBACKPREFIX + "TOGGLE")
            }
        });
    }

    /// <summary>
    /// Shows the destructive reset confirmation for the tenant owner panel.
    /// </summary>
    /// <param name="botClient">
    /// Owned-brand Telegram client that received the owner callback and can edit the panel message.
    /// </param>
    /// <param name="CallbackQuery">
    /// Callback query from the colleague owner. The callback message is edited in-place when possible.
    /// </param>
    /// <param name="CancellationToken">
    /// Token used to cancel Telegram edit and callback-answer operations when the update pipeline stops.
    /// </param>
    /// <remarks>
    /// Reset removes every owner-configured tenant storefront setting and disables the tenant bot, so this
    /// method always asks for a second confirmation before <see cref="RESETTENANTSETTINGSASYNC" /> can run.
    /// </remarks>
    private async Task SHOWTENANTRESETCONFIRMATIONASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CancellationToken CancellationToken)
    {
        var chatId = CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id;
        var messageId = CallbackQuery.Message?.MessageId;
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ تایید بازنشانی", OWNERCALLBACKPREFIX + "reset-confirm")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("↩️ برگشت به پنل", OWNERCALLBACKPREFIX + "panel")
            }
        });

        const string text =
            "♻️ <b>بازنشانی تنظیمات ربات فروشگاهی</b>\n\n" +
            "با تایید این گزینه، توکن ربات، یوزرنیم ربات، پشتیبانی، متن خوشامد، کارت‌به‌کارت، جوین اجباری، آموزش‌ها و تنظیمات درگاه‌های فروشگاه پاک می‌شود و ربات فروشگاهی خاموش خواهد شد.\n\n" +
            "سوابق سفارش‌ها، رسیدها، پرداخت‌ها، تراکنش‌ها و کاربران قبلی حذف نمی‌شوند.";

        if (messageId.HasValue)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId,
                messageId.Value,
                text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
        }
        else
        {
            await botClient.SendTextMessageAsync(
                chatId,
                text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
        }

        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Resets all owner-configured storefront settings and disables the colleague tenant bot.
    /// </summary>
    /// <param name="botClient">
    /// Owned-brand Telegram client used to edit the owner panel after the reset has been persisted.
    /// </param>
    /// <param name="CallbackQuery">
    /// Confirmation callback from the tenant owner. The sender must be the colleague who owns the tenant row.
    /// </param>
    /// <param name="owner">
    /// Shared credentials profile of the colleague owner whose tenant settings must be reset.
    /// </param>
    /// <param name="CancellationToken">
    /// Token used to cancel users.db writes, runtime stop, cache invalidation, and Telegram replies.
    /// </param>
    /// <remarks>
    /// This method intentionally resets only the mutable storefront configuration stored on <see cref="BotInstance" />.
    /// It preserves the tenant internal id, owner Telegram id, orders, receipts, ledger entries, payments, and
    /// customer state so financial and delivery history remains auditable after the owner starts over.
    /// </remarks>
    private async Task RESETTENANTSETTINGSASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        await StopTenantRuntimeBestEffortAsync(tenant.Id, CancellationToken);

        ResetTenantStorefrontSettings(tenant, clearAllStorefrontSettings: true);
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        _botRegistry.Upsert(tenant);
        _botClientProvider.Invalidate(tenant.Id);

        await SafeAnswerCallbackQueryAsync(
            botClient,
            CallbackQuery.Id,
            "تنظیمات ربات فروشگاهی بازنشانی شد.",
            showAlert: true,
            cancellationToken: CancellationToken);

        await SHOWOWNERPANELASYNC(
            botClient,
            CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id,
            owner,
            CallbackQuery.Message?.MessageId,
            CancellationToken);
    }

    /// <summary>
    /// Validates the persisted tenant bot token before the owner panel is rendered.
    /// </summary>
    /// <param name="tenant">
    /// Tenant bot row owned by the colleague whose panel is being opened. A null value or a row without a token
    /// is treated as an unconfigured storefront and does not call Telegram.
    /// </param>
    /// <param name="CancellationToken">
    /// Token used to cancel the Telegram <c>getMe</c> probe and any users.db cleanup if the update stops.
    /// </param>
    /// <returns>
    /// An HTML-safe Persian notice to prepend to the owner panel, or null when no user-visible warning is needed.
    /// The returned text is safe to include in a message sent with <see cref="ParseMode.Html" />.
    /// </returns>
    /// <remarks>
    /// A clearly revoked or unauthorized token is cleaned immediately because keeping it in the panel causes the
    /// owner to believe a dead storefront is still configured. Transient Telegram errors such as timeouts or 5xx
    /// responses do not clear the token; the owner sees a temporary warning and can retry refresh later.
    /// </remarks>
    private async Task<string> VALIDATETENANTTOKENFORPANELASYNC(BotInstance tenant, CancellationToken CancellationToken)
    {
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.Token))
            return null;

        try
        {
            var client = new TelegramBotClient(tenant.Token);
            var me = await client.GetMeAsync(CancellationToken);
            var username = me.Username?.Trim().TrimStart('@');
            var changed = false;

            if (!string.IsNullOrWhiteSpace(username) &&
                !string.Equals(tenant.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                tenant.Username = username;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(tenant.BrandName) && !string.IsNullOrWhiteSpace(me.FirstName))
            {
                tenant.BrandName = me.FirstName;
                changed = true;
            }

            if (changed)
            {
                tenant.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken);
                _botRegistry.Upsert(tenant);
                _botClientProvider.Invalidate(tenant.Id);
            }

            return null;
        }
        catch (Exception ex) when (ISTELEGRAMTOKENINVALIDERROR(ex))
        {
            await StopTenantRuntimeBestEffortAsync(tenant.Id, CancellationToken);

            ResetTenantStorefrontSettings(tenant, clearAllStorefrontSettings: false);
            await _userDbcontext.SaveChangesAsync(CancellationToken);
            _botRegistry.Upsert(tenant);
            _botClientProvider.Invalidate(tenant.Id);

            _logger.LogWarning(
                "Tenant bot token was invalid during owner panel refresh and was cleared. tenantBotId={TenantBotId}, owner={OwnerTelegramUserId}, reason={Reason}",
                tenant.Id,
                tenant.OwnerTelegramUserId,
                ex.Message);

            return "⚠️ توکن ربات فروشگاهی معتبر نبود و از تنظیمات پاک شد. لطفاً توکن جدید ثبت کنید.";
        }
        catch (Exception ex) when (ISTELEGRAMTRANSIENTTOKENCHECKERROR(ex))
        {
            _logger.LogDebug(
                ex,
                "Tenant bot token validation skipped because Telegram returned a transient error. tenantBotId={TenantBotId}",
                tenant.Id);

            return "⚠️ فعلاً امکان بررسی توکن ربات فروشگاهی نیست. اگر تلگرام مشکل موقت داشته باشد، دوباره بروزرسانی را بزنید.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Tenant bot token validation skipped because Telegram returned an unknown non-authoritative error. tenantBotId={TenantBotId}",
                tenant.Id);

            return "⚠️ فعلاً امکان بررسی توکن ربات فروشگاهی نیست. تنظیمات شما پاک نشد؛ کمی بعد دوباره بروزرسانی را بزنید.";
        }
    }

    /// <summary>
    /// Stops a tenant receiver without failing the owner-panel cleanup flow.
    /// </summary>
    /// <param name="tenantBotId">
    /// Internal tenant bot id, for example <c>tenant-123456</c>. This is not a Telegram bot id or chat id.
    /// </param>
    /// <param name="CancellationToken">
    /// Token used to cancel the runtime stop request if the application is shutting down.
    /// </param>
    /// <returns>A task that completes after the runtime stop attempt has finished or been logged as failed.</returns>
    /// <remarks>
    /// Reset and invalid-token cleanup must update users.db even when the receiver is already stopped or the hosted
    /// service is unavailable. For that reason this helper logs runtime stop failures and lets the caller continue.
    /// </remarks>
    private async Task StopTenantRuntimeBestEffortAsync(string tenantBotId, CancellationToken CancellationToken)
    {
        var runtime = _serviceProvider.GetService<MultiBotHostedService>();
        if (runtime == null || string.IsNullOrWhiteSpace(tenantBotId))
            return;

        try
        {
            await runtime.StopBotAsync(tenantBotId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Tenant bot receiver stop failed during settings cleanup. tenantBotId={TenantBotId}",
                tenantBotId);
        }
    }

    /// <summary>
    /// Applies either a full storefront reset or an invalid-token cleanup to a tenant bot row.
    /// </summary>
    /// <param name="tenant">
    /// Persisted tenant bot row tracked by <see cref="UserDbContext" />. The caller must save changes after this
    /// method returns.
    /// </param>
    /// <param name="clearAllStorefrontSettings">
    /// When true, all owner-configured storefront settings are cleared. When false, only token identity and
    /// enabled state are cleared so unrelated settings like card payment and support remain intact.
    /// </param>
    /// <remarks>
    /// This method deliberately does not delete tenant orders, receipts, ledger entries, customer states, or payment
    /// rows. Those records are separate audit history and must survive both owner-requested resets and revoked-token
    /// cleanup.
    /// </remarks>
    private static void ResetTenantStorefrontSettings(BotInstance tenant, bool clearAllStorefrontSettings)
    {
        tenant.Enabled = false;
        tenant.Token = null;
        tenant.Username = null;
        tenant.UpdatedAtUtc = DateTime.UtcNow;

        if (!clearAllStorefrontSettings)
            return;

        tenant.BrandName = null;
        tenant.SupportAccount = null;
        tenant.TenantWelcomeText = null;
        tenant.TenantPriceMarkupPercent = 0;
        tenant.TenantMandatoryJoinEnabled = false;
        tenant.TenantChannelIdsJson = null;
        tenant.TenantCardPaymentEnabled = false;
        tenant.TenantCardNumber = null;
        tenant.TenantCardHolderName = null;
        tenant.TenantHooshPayEnabled = true;
        tenant.TenantNowPaymentsEnabled = true;
        tenant.TenantTutorialsJson = JsonConvert.SerializeObject(Array.Empty<TenantTutorialLink>());
    }

    /// <summary>
    /// Determines whether a Telegram exception proves that a bot token is revoked, invalid, or unauthorized.
    /// </summary>
    /// <param name="exception">
    /// Exception thrown by Telegram <c>getMe</c> or another bot-token validation call. The exception message is
    /// inspected without logging or exposing the raw token.
    /// </param>
    /// <returns>
    /// <c>true</c> when the token should be cleared from the tenant row; otherwise <c>false</c>.
    /// </returns>
    private static bool ISTELEGRAMTOKENINVALIDERROR(Exception exception)
    {
        if (exception is ApiRequestException apiException &&
            (apiException.ErrorCode == 401 || CONTAINSTOKENINVALIDTEXT(apiException.Message)))
            return true;

        return CONTAINSTOKENINVALIDTEXT(exception?.Message);
    }

    /// <summary>
    /// Determines whether a token validation failure is likely a temporary Telegram/network issue.
    /// </summary>
    /// <param name="exception">
    /// Exception thrown while probing the tenant token. The raw token is never included in this value by callers.
    /// </param>
    /// <returns>
    /// <c>true</c> for timeout, cancellation, rate-limit, and Telegram 5xx errors that should not clear settings.
    /// </returns>
    private static bool ISTELEGRAMTRANSIENTTOKENCHECKERROR(Exception exception)
    {
        if (exception is TimeoutException || exception is TaskCanceledException)
            return true;

        if (exception is ApiRequestException apiException)
            return apiException.ErrorCode == 429 || apiException.ErrorCode >= 500;

        var message = exception?.Message;
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("temporarily", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks Telegram error text for invalid-token keywords without exposing the token value.
    /// </summary>
    /// <param name="message">Telegram or client-library error message to inspect.</param>
    /// <returns><c>true</c> when the message describes an invalid, revoked, or unauthorized bot token.</returns>
    private static bool CONTAINSTOKENINVALIDTEXT(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("invalid token", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("bot token", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts a one-message owner input flow for a specific tenant setting.
    /// </summary>
    /// <param name="botClient">
    /// Main owned-brand bot client that is currently serving the colleague owner panel.
    /// This is not the tenant storefront bot client.
    /// </param>
    /// <param name="CallbackQuery">
    /// Callback from the owner panel that selected which tenant setting should be edited.
    /// The sender id is used to persist the next expected input step.
    /// </param>
    /// <param name="field">
    /// Requested setting key such as <c>Token</c>, <c>markup</c>, <c>support</c>, or <c>WELCOME</c>.
    /// Unknown values are ignored and only answer the callback.
    /// </param>
    /// <param name="CancellationToken">
    /// Token used to cancel database state writes and Telegram prompt sending if the update pipeline stops.
    /// </param>
    /// <remarks>
    /// The method writes a temporary owner-flow state row so the next text message is routed to the matching
    /// save method. Prompts are sent as HTML because the BotFather token prompt contains a clickable
    /// <c>@BotFather</c> link. The support prompt includes a reply-keyboard back button because owners often
    /// need to inspect the current support id before deciding whether to change it.
    /// </remarks>
    private async Task STARTOWNERINPUTASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        string field,
        CancellationToken CancellationToken)
    {
        var step = field switch
        {
            STEPTOKEN => STEPTOKEN,
            STEPMARKUP => STEPMARKUP,
            STEPSUPPORT => STEPSUPPORT,
            STEPWELCOME => STEPWELCOME,
            STEPCARDNUMBER => STEPCARDNUMBER,
            STEPCARDHOLDER => STEPCARDHOLDER,
            STEPMANDATORYCHANNEL => STEPMANDATORYCHANNEL,
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(step))
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        await _userDbcontext.SaveUserStatus(new User
        {
            Id = CallbackQuery.From.Id,
            Flow = OWNERFLOW,
            LastStep = step
        });

        var tenant = await GETTENANTBOTBYOWNERASYNC(CallbackQuery.From.Id, CancellationToken);
        var currentSupport = string.IsNullOrWhiteSpace(tenant?.SupportAccount)
            ? "ثبت نشده"
            : NormalizeTenantSupportAccount(tenant.SupportAccount) ?? tenant.SupportAccount;

        var PROMPT = step switch
        {
            STEPTOKEN => "توکن رباتی که از <a href=\"https://t.me/BotFather\">@BotFather</a> گرفته‌اید را ارسال کنید.",
            STEPMARKUP => "درصد سود روی قیمت همکار را فقط به عدد ارسال کنید. مثال: 20",
            STEPSUPPORT => $"آیدی پشتیبان فعلی: <code>{Html(currentSupport)}</code>\n\nیوزرنیم پشتیبانی فروشگاه را ارسال کنید. مثال: <code>@SUPPORT_USERNAME</code>",
            STEPWELCOME => "متن خوشامد فروشگاه را ارسال کنید.",
            STEPCARDNUMBER => "شماره کارت درگاه کارت‌به‌کارت فروشگاه را بدون فاصله یا با فاصله خوانا ارسال کنید.",
            STEPCARDHOLDER => "نام صاحب کارت را دقیق ارسال کنید تا برای مشتری نمایش داده شود.",
            STEPMANDATORYCHANNEL => "آیدی یا لینک کانال جوین اجباری را ارسال کنید. مثال: @YOURCHANNEL",
            _ => "مقدار جدید را ارسال کنید."
        };

        await botClient.SendTextMessageAsync(
            chatId: CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id,
            text: PROMPT,
            parseMode: ParseMode.Html,
            replyMarkup: step == STEPSUPPORT
                ? new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("بازگشت به پنل") }
                })
                {
                    ResizeKeyboard = true
                }
                : new ReplyKeyboardRemove(),
            cancellationToken: CancellationToken);

        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Validates A BOTFATHER Token with Telegram GetMe and stores it as the owner's tenant Bot Token.
    /// </summary>
    /// <param name="botClient">Main brand Bot client used to Reply to the owner.</param>
    /// <param name="Message">owner Message containing the Token.</param>
    /// <param name="owner">colleague owner profile.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    private async Task SAVETENANTBOTTOKENASYNC(
        ITelegramBotClient botClient,
        Message Message,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        var Token = Message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(Token) || !Token.Contains(':'))
        {
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                "توکن معتبر نیست. توکن BOTFATHER را کامل ارسال کنید.",
                cancellationToken: CancellationToken);
            return;
        }

        Telegram.Bot.Types.User me;
        try
        {
            var TENANTCLIENT = new TelegramBotClient(Token);
            me = await TENANTCLIENT.GetMeAsync(CancellationToken);
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                $"اعتبارسنجی توکن ناموفق بود:\n<code>{Html(ex.Message)}</code>",
                parseMode: ParseMode.Html,
                cancellationToken: CancellationToken);
            return;
        }

        var Username = me.Username?.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(Username))
        {
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                "این توکن یوزرنیم معتبر برنگرداند. لطفاً BOTFATHER را بررسی کنید.",
                cancellationToken: CancellationToken);
            return;
        }

        var duplicate = await FindTenantTokenConflictAsync(Token, Username, owner.TelegramUserId, CancellationToken);
        if (duplicate.HasConflict)
        {
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                "این توکن/ربات در حال حاضر برای اکانت یا ربات دیگری استفاده می‌شود. لطفاً توکن دیگری وارد کنید.",
                cancellationToken: CancellationToken);
            _logger.LogWarning(
                "Rejected duplicate tenant bot token. owner={OwnerTelegramUserId}, conflict={ConflictType}, botId={TokenBotId}, username=@{Username}",
                owner.TelegramUserId,
                duplicate.ConflictType,
                TelegramBotTokenIdentity.ExtractBotId(Token),
                Username);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.Token = Token;
        tenant.Username = Username;
        tenant.BrandName = string.IsNullOrWhiteSpace(me.FirstName) ? Username : me.FirstName;
        tenant.Type = BotInstanceTypes.Tenant;
        tenant.OwnerTelegramUserId = owner.TelegramUserId;
        tenant.Enabled = false;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        if (tenant.TenantPriceMarkupPercent < 0)
            tenant.TenantPriceMarkupPercent = 0;
        if (string.IsNullOrWhiteSpace(tenant.SupportAccount) && !string.IsNullOrWhiteSpace(owner.Username))
            tenant.SupportAccount = "@" + owner.Username.TrimStart('@');

        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);
        _botClientProvider.Invalidate(tenant.Id);

        await botClient.SendTextMessageAsync(
            Message.Chat.Id,
            $"✅ توکن ربات <b>@{Html(Username)}</b> ثبت شد.\nبرای شروع دریافت پیام، فروشگاه را از پنل روشن کنید.",
            parseMode: ParseMode.Html,
            cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// Checks whether a validated tenant bot token or username is already owned by another runtime bot.
    /// </summary>
    /// <param name="token">
    /// Trimmed BotFather token entered by the colleague. The token is compared in memory only and must never be
    /// written to Telegram messages or operational logs.
    /// </param>
    /// <param name="username">
    /// Username returned by Telegram <c>GetMe</c> for the entered token, without a required leading <c>@</c>.
    /// </param>
    /// <param name="ownerTelegramUserId">
    /// Numeric Telegram user id of the colleague who is saving the token. That owner's existing tenant bot is
    /// excluded so the owner can re-save or rotate their own token.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to cancel the users.db query when the update handler stops.
    /// </param>
    /// <returns>
    /// A conflict descriptor. <see cref="TenantTokenConflictResult.HasConflict" /> is <c>true</c> when the token,
    /// token bot id, or username belongs to another tenant owner or to a configured non-tenant bot.
    /// </returns>
    /// <remarks>
    /// This guard runs after Telegram has accepted the token with <c>GetMe</c>. It prevents two tenant owners from
    /// receiving updates through the same bot and prevents a tenant storefront from reusing an owned bot token.
    /// </remarks>
    private async Task<TenantTokenConflictResult> FindTenantTokenConflictAsync(
        string token,
        string username,
        long ownerTelegramUserId,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = TelegramBotTokenIdentity.NormalizeUsername(username);
        var tenants = await _userDbcontext.BotInstances
            .Where(x => x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId != ownerTelegramUserId)
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            if (TelegramBotTokenIdentity.IsSameBotToken(token, tenant.Token))
                return TenantTokenConflictResult.Conflict("tenant-token");

            if (!string.IsNullOrWhiteSpace(normalizedUsername) &&
                string.Equals(
                    normalizedUsername,
                    TelegramBotTokenIdentity.NormalizeUsername(tenant.Username),
                    StringComparison.OrdinalIgnoreCase))
            {
                return TenantTokenConflictResult.Conflict("tenant-username");
            }
        }

        foreach (var bot in _botRegistry.Bots.Where(x => !string.Equals(x.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase)))
        {
            if (TelegramBotTokenIdentity.IsSameBotToken(token, bot.Token))
                return TenantTokenConflictResult.Conflict("owned-token");

            if (!string.IsNullOrWhiteSpace(normalizedUsername) &&
                string.Equals(
                    normalizedUsername,
                    TelegramBotTokenIdentity.NormalizeUsername(bot.Username),
                    StringComparison.OrdinalIgnoreCase))
            {
                return TenantTokenConflictResult.Conflict("owned-username");
            }
        }

        return TenantTokenConflictResult.None;
    }

    /// <summary>
    /// stores the global markup percent used for tenant storefront prices.
    /// </summary>
    /// <param name="botClient">Main brand Bot client.</param>
    /// <param name="Message">owner Message containing A numeric percent.</param>
    /// <param name="owner">colleague owner profile.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    private async Task SAVETENANTMARKUPASYNC(ITelegramBotClient botClient, Message Message, CredUser owner, CancellationToken CancellationToken)
    {
        if (!int.TryParse(Message.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var markup) || markup < 0 || markup > 500)
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "درصد سود باید عددی بین 0 تا 500 باشد.", cancellationToken: CancellationToken);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.TenantPriceMarkupPercent = markup;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);

        await botClient.SendTextMessageAsync(Message.Chat.Id, "✅ درصد سود ذخیره شد.", cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// Stores the support account displayed inside the tenant storefront.
    /// </summary>
    /// <param name="botClient">
    /// Main owned-brand bot client used to confirm the saved value and reopen the owner panel.
    /// </param>
    /// <param name="Message">
    /// Owner message containing a Telegram support username. It may include the leading <c>@</c>;
    /// empty values are rejected and do not change the current support account.
    /// </param>
    /// <param name="owner">
    /// Colleague profile from <c>credentials.db</c>. The owner's numeric Telegram id identifies the tenant bot row.
    /// </param>
    /// <param name="CancellationToken">
    /// Token used to cancel database persistence and Telegram replies.
    /// </param>
    /// <remarks>
    /// The value is normalized to a public Telegram username with one leading <c>@</c> and is stored on
    /// <see cref="BotInstance.SupportAccount"/>. The setting is tenant-scoped and is shown only in that
    /// colleague's storefront support flow. Public <c>t.me</c> links are converted to their username segment
    /// so customers never see values like <c>@https://t.me/name</c>.
    /// </remarks>
    private async Task SAVETENANTSUPPORTASYNC(ITelegramBotClient botClient, Message Message, CredUser owner, CancellationToken CancellationToken)
    {
        var support = NormalizeTenantSupportAccount(Message.Text);
        if (string.IsNullOrWhiteSpace(support))
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "آیدی پشتیبانی معتبر نیست. لطفاً یوزرنیم عمومی تلگرام مثل @SUPPORT_USERNAME یا لینک t.me را ارسال کنید.", cancellationToken: CancellationToken);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.SupportAccount = support;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);

        await botClient.SendTextMessageAsync(
            Message.Chat.Id,
            "✅ پشتیبانی فروشگاه ذخیره شد.",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// stores the WELCOME Text displayed on tenant Bot /start.
    /// </summary>
    /// <param name="botClient">Main brand Bot client.</param>
    /// <param name="Message">owner Message containing WELCOME Text.</param>
    /// <param name="owner">colleague owner profile.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    private async Task SAVETENANTWELCOMEASYNC(ITelegramBotClient botClient, Message Message, CredUser owner, CancellationToken CancellationToken)
    {
        var WELCOME = Message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(WELCOME))
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "متن خوشامد نمی‌تواند خالی باشد.", cancellationToken: CancellationToken);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.TenantWelcomeText = WELCOME;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);

        await botClient.SendTextMessageAsync(Message.Chat.Id, "✅ متن خوشامد ذخیره شد.", cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// stores the tenant owner's card number for the optional card-to-card payment gateway.
    /// </summary>
    /// <param name="botClient">Main owned Bot client used to Reply to the tenant owner.</param>
    /// <param name="Message">owner Message containing the card number Text.</param>
    /// <param name="owner">colleague User who owns the tenant storefront; the value COMES from credentials.db.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db Writes and Telegram REPLIES.</param>
    /// <remarks>
    /// the card number is tenant-scoped and is shown only to customers of this tenant Bot when the owner
    /// Enables card-to-card payment. the value is stored in users.db because credentials.db schema must STAY UNCHANGED.
    /// </remarks>
    private async Task SAVETENANTCARDNUMBERASYNC(ITelegramBotClient botClient, Message Message, CredUser owner, CancellationToken CancellationToken)
    {
        var cardNumber = Message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(cardNumber))
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "شماره کارت نمی‌تواند خالی باشد.", cancellationToken: CancellationToken);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.TenantCardNumber = cardNumber;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);

        await botClient.SendTextMessageAsync(Message.Chat.Id, "✅ شماره کارت فروشگاه ذخیره شد.", cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// stores the display name of the card HOLDER for the tenant owner's card-to-card gateway.
    /// </summary>
    /// <param name="botClient">Main owned Bot client used to answer the owner.</param>
    /// <param name="Message">owner Message containing the card HOLDER name.</param>
    /// <param name="owner">colleague User who owns the tenant storefront.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db Writes and Telegram REPLIES.</param>
    /// <remarks>
    /// this method does not VALIDATE the name AGAINST A BANK; it only stores the exact owner-provided
    /// Text after TRIMMING so the customer can match the card TRANSFER DESTINATION.
    /// </remarks>
    private async Task SAVETENANTCARDHOLDERASYNC(ITelegramBotClient botClient, Message Message, CredUser owner, CancellationToken CancellationToken)
    {
        var CARDHOLDER = Message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(CARDHOLDER))
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "نام صاحب کارت نمی‌تواند خالی باشد.", cancellationToken: CancellationToken);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.TenantCardHolderName = CARDHOLDER;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);

        await botClient.SendTextMessageAsync(Message.Chat.Id, "✅ نام صاحب کارت ذخیره شد.", cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// stores the tenant storefront channel used for optional forced-join checks.
    /// </summary>
    /// <param name="botClient">Main owned Bot client used to answer the tenant owner.</param>
    /// <param name="Message">owner Message containing one Telegram channel Username, Id, or INVITE-STYLE link.</param>
    /// <param name="owner">colleague User who owns the tenant storefront.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db Writes and Telegram REPLIES.</param>
    /// <remarks>
    /// the value is normalized to A single channel Id/Username List in <see cref="BotInstance.TenantChannelIdsJson" />.
    /// the Bot Access ITSELF is checked when forced join is enabled or when the storefront is TURNED on.
    /// </remarks>
    private async Task SAVETENANTMANDATORYCHANNELASYNC(ITelegramBotClient botClient, Message Message, CredUser owner, CancellationToken CancellationToken)
    {
        var channel = NORMALIZETELEGRAMCHANNEL(Message.Text);
        if (string.IsNullOrWhiteSpace(channel))
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "کانال معتبر نیست. آیدی کانال مثل @YOURCHANNEL را ارسال کنید.", cancellationToken: CancellationToken);
            return;
        }

        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        tenant.TenantChannelIdsJson = JsonConvert.SerializeObject(new[] { channel });
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        _botRegistry.Upsert(tenant);

        await botClient.SendTextMessageAsync(Message.Chat.Id, "✅ کانال جوین اجباری ذخیره شد.", cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// Enables or disables the tenant Bot and starts/Stops its Telegram receiver at runtime.
    /// </summary>
    /// <param name="botClient">Main brand Bot client.</param>
    /// <param name="CallbackQuery">TOGGLE callback from the owner panel.</param>
    /// <param name="owner">colleague owner profile.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <remarks>
    /// Enabling a tenant bot is only persisted as active when <see cref="MultiBotHostedService.StartBotAsync" />
    /// actually creates a receiver. If Telegram times out after its bounded retries, this method rolls the tenant
    /// row back to disabled and shows the owner an alert instead of leaving the panel in a stale "روشن" state.
    /// </remarks>
    private async Task TOGGLETENANTBOTASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, CancellationToken);
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.Token) || string.IsNullOrWhiteSpace(tenant.Username))
        {
            await SafeAnswerCallbackQueryAsync(botClient, 
                CallbackQuery.Id,
                "اول توکن ربات فروشگاهی را ثبت کنید.",
                showAlert: true,
                cancellationToken: CancellationToken);
            return;
        }

        var NEXTENABLED = !tenant.Enabled;
        if (NEXTENABLED && tenant.TenantMandatoryJoinEnabled)
        {
            var validation = await VALIDATETENANTMANDATORYJOINASYNC(tenant, CancellationToken);
            if (!validation.ISVALID)
            {
                tenant.Enabled = false;
                tenant.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken);
                _botRegistry.Upsert(tenant);

                await SafeAnswerCallbackQueryAsync(botClient, 
                    CallbackQuery.Id,
                    validation.ErrorMessage,
                    showAlert: true,
                    cancellationToken: CancellationToken);
                await SHOWOWNERPANELASYNC(botClient, CallbackQuery.Message.Chat.Id, owner, CallbackQuery.Message.MessageId, CancellationToken);
                return;
            }
        }

        tenant.Enabled = NEXTENABLED;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        _botRegistry.Upsert(tenant);
        _botClientProvider.Invalidate(tenant.Id);

        var runtime = _serviceProvider.GetService<MultiBotHostedService>();
        if (runtime != null)
        {
            if (tenant.Enabled)
            {
                var started = await runtime.StartBotAsync(tenant.Id, CancellationToken);
                if (!started)
                {
                    tenant.Enabled = false;
                    tenant.UpdatedAtUtc = DateTime.UtcNow;
                    await _userDbcontext.SaveChangesAsync(CancellationToken);
                    _botRegistry.Upsert(tenant);
                    _botClientProvider.Invalidate(tenant.Id);

                    await SafeAnswerCallbackQueryAsync(
                        botClient,
                        CallbackQuery.Id,
                        "تلگرام یا شبکه هنگام روشن‌کردن ربات فروشگاهی پاسخ نداد. چند لحظه بعد دوباره روشن کنید.",
                        showAlert: true,
                        cancellationToken: CancellationToken);
                    await SHOWOWNERPANELASYNC(botClient, CallbackQuery.Message.Chat.Id, owner, CallbackQuery.Message.MessageId, CancellationToken);
                    return;
                }
            }
            else
            {
                await runtime.StopBotAsync(tenant.Id);
            }
        }

        await SafeAnswerCallbackQueryAsync(botClient, 
            CallbackQuery.Id,
            tenant.Enabled ? "فروشگاه روشن شد." : "فروشگاه خاموش شد.",
            cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, CallbackQuery.Message.Chat.Id, owner, CallbackQuery.Message.MessageId, CancellationToken);
    }

    /// <summary>
    /// Toggles A CONFIGURABLE tenant storefront FEATURE such as GATEWAYS, card payment, or forced join.
    /// </summary>
    /// <param name="botClient">Main owned Bot client used to answer the owner callback.</param>
    /// <param name="CallbackQuery">callback that requested A FEATURE TOGGLE from the tenant owner panel.</param>
    /// <param name="owner">colleague User who owns the tenant storefront.</param>
    /// <param name="setting">short setting key from callback Data: card, HooshPay, NowPayments, or join.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db Writes, Telegram calls, and optional join validation.</param>
    /// <remarks>
    /// forced join is VALIDATED immediately before it is enabled. if Telegram reports that the tenant Bot cannot
    /// read the channel member List, the setting stays Disabled and the owner Receives an ACTIONABLE ALERT.
    /// </remarks>
    private async Task TOGGLETENANTSETTINGASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        string setting,
        CancellationToken CancellationToken)
    {
        var tenant = await GETORCREATETENANTBOTASYNC(owner, CancellationToken);
        switch (setting)
        {
            case "card":
                if (!tenant.TenantCardPaymentEnabled &&
                    (string.IsNullOrWhiteSpace(tenant.TenantCardNumber) || string.IsNullOrWhiteSpace(tenant.TenantCardHolderName)))
                {
                    await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "اول شماره کارت و نام صاحب کارت را ثبت کنید.", showAlert: true, cancellationToken: CancellationToken);
                    return;
                }

                tenant.TenantCardPaymentEnabled = !tenant.TenantCardPaymentEnabled;
                break;
            case "HooshPay":
                tenant.TenantHooshPayEnabled = !tenant.TenantHooshPayEnabled;
                break;
            case "NowPayments":
                tenant.TenantNowPaymentsEnabled = !tenant.TenantNowPaymentsEnabled;
                break;
            case "join":
                if (!tenant.TenantMandatoryJoinEnabled)
                {
                    var validation = await VALIDATETENANTMANDATORYJOINASYNC(tenant, CancellationToken);
                    if (!validation.ISVALID)
                    {
                        tenant.TenantMandatoryJoinEnabled = false;
                        await _userDbcontext.SaveChangesAsync(CancellationToken);
                        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, validation.ErrorMessage, showAlert: true, cancellationToken: CancellationToken);
                        return;
                    }
                }

                tenant.TenantMandatoryJoinEnabled = !tenant.TenantMandatoryJoinEnabled;
                break;
            default:
                await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
                return;
        }

        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        _botRegistry.Upsert(tenant);

        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "تنظیمات فروشگاه به‌روزرسانی شد.", cancellationToken: CancellationToken);
        await SHOWOWNERPANELASYNC(botClient, CallbackQuery.Message.Chat.Id, owner, CallbackQuery.Message.MessageId, CancellationToken);
    }

    /// <summary>
    /// SHOWS the tenant owner A compact newest-first summary of recent storefront orders.
    /// </summary>
    /// <param name="botClient">Main owned Bot client used to edit or Send the owner-FACING REPORT.</param>
    /// <param name="CallbackQuery">owner callback REQUESTING the orders REPORT.</param>
    /// <param name="owner">colleague User whose tenant orders should be LISTED.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db and Telegram calls.</param>
    /// <remarks>
    /// this is intentionally Lightweight in the first tenant ROLLOUT: it gives owners IMMEDIATE VISIBILITY
    /// into recent orders while the full PAGINATED REPORT can REUSE the same order Query LATER.
    /// </remarks>
    private async Task SHOWOWNERORDERSASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        int page,
        CancellationToken CancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, CancellationToken);
        const int pageSize = 10;
        var orders = tenant == null
            ? new List<TenantBotOrder>()
            : await _userDbcontext.TenantBotOrders
                .Where(x => x.TenantBotId == tenant.Id)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToListAsync(CancellationToken);
        var totalCount = tenant == null
            ? 0
            : await _userDbcontext.TenantBotOrders.CountAsync(x => x.TenantBotId == tenant.Id, CancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));

        var Builder = new System.Text.StringBuilder();
        Builder.AppendLine("🧾 <b>سفارش‌های فروشگاه</b>");
        Builder.AppendLine($"صفحه <code>{page + 1}</code> از <code>{totalPages}</code>");
        Builder.AppendLine();
        if (orders.Count == 0)
        {
            Builder.AppendLine("هنوز سفارشی ثبت نشده است.");
        }
        else
        {
            Builder.AppendLine("برای دیدن جزئیات، روی سفارش بزنید.");
        }

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var order in orders)
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    BuildOwnerOrderButtonText(order),
                    OWNERCALLBACKPREFIX + $"order:{order.Id}:{page}")
            });

        var navigation = new List<InlineKeyboardButton>();
        if (page > 0)
            navigation.Add(InlineKeyboardButton.WithCallbackData("⬅️ قبلی", OWNERCALLBACKPREFIX + $"orders:{page - 1}"));
        if (page + 1 < totalPages)
            navigation.Add(InlineKeyboardButton.WithCallbackData("بعدی ➡️", OWNERCALLBACKPREFIX + $"orders:{page + 1}"));
        if (navigation.Count > 0)
            rows.Add(navigation.ToArray());
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", OWNERCALLBACKPREFIX + "panel") });

        await SafeEditMessageTextAsync(botClient, 
            CallbackQuery.Message.Chat.Id,
            CallbackQuery.Message.MessageId,
            Builder.ToString(),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: CancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Shows full details for one tenant order selected from the owner order list.
    /// </summary>
    /// <param name="botClient">Owned bot client used to edit the owner-panel message.</param>
    /// <param name="callbackQuery">Owner callback containing order id and source page.</param>
    /// <param name="owner">Colleague owner requesting the order details.</param>
    /// <param name="payload">Callback payload in the form <c>{orderId}:{page}</c>.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    private async Task SHOWOWNERORDERDETAILASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CredUser owner,
        string payload,
        CancellationToken cancellationToken)
    {
        var parts = payload.Split(':');
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderId))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "سفارش نامعتبر است.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var page = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage)
            ? Math.Max(0, parsedPage)
            : 0;
        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == orderId && x.OwnerTelegramUserId == owner.TelegramUserId,
            cancellationToken);
        if (order == null)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "سفارش پیدا نشد.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var text =
            "🔎 <b>جزئیات سفارش فروشگاه</b>\n\n" +
            $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
            $"نوع سفارش: <b>{Html(GetOrderKindDisplay(order))}</b>\n" +
            $"تاریخ ثبت: <code>{Html(FormatTenantOrderDate(order.CreatedAtUtc))}</code>\n" +
            $"خریدار: {BuildTelegramUserLink(order.CustomerTelegramUserId, BuildCustomerDisplayName(order))}\n" +
            $"درگاه: <code>{Html(order.PaymentProvider)}</code>\n" +
            $"وضعیت پرداخت: <code>{Html(order.PaymentStatus)}</code>\n" +
            $"وضعیت تحویل: <code>{Html(order.IsFulfilled ? "تحویل شده" : "تحویل نشده")}</code>\n\n" +
            $"سرویس: <code>{Html(order.ServiceKey)}</code>\n" +
            $"حجم: <code>{Html(order.TrafficGb?.ToString(CultureInfo.InvariantCulture) ?? "-")} GB</code>\n" +
            $"مدت/پلن: <code>{Html(order.DurationKey ?? order.UnlimitedPlanKey ?? "-")}</code>\n" +
            $"اکانت هدف/ساخته‌شده: <code>{Html(order.TargetAccountEmail ?? order.CreatedAccountEmail ?? "-")}</code>\n" +
            $"ساب‌لینک: <code>{Html(order.CreatedSubLink ?? "-")}</code>\n\n" +
            $"مبلغ فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"هزینه پایه: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
            $"سود/کسر همکار: <code>{Html(order.OwnerWalletDelta.FormatCurrency())}</code>\n" +
            $"موجودی بعد: <code>{Html(order.OwnerBalanceAfter?.FormatCurrency() ?? "-")}</code>" +
            BuildTenantOrderErrorLine(order);

        await SafeEditMessageTextAsync(
            botClient,
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به سفارش‌ها", OWNERCALLBACKPREFIX + $"orders:{page}") },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به پنل", OWNERCALLBACKPREFIX + "panel") }
            }),
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Shows the tenant tutorial manager for the colleague owner.
    /// </summary>
    /// <param name="botClient">Owned bot client used to edit the panel message.</param>
    /// <param name="callbackQuery">Owner callback requesting tutorial management.</param>
    /// <param name="owner">Colleague owner whose tenant tutorials are displayed.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    private async Task SHOWTENANTTUTORIALMANAGERASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CredUser owner,
        CancellationToken cancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, cancellationToken);
        var tutorials = ReadTenantTutorials(tenant).ToList();
        var text = new System.Text.StringBuilder();
        text.AppendLine("🎓 <b>آموزش‌های ربات فروشگاهی</b>");
        text.AppendLine();
        text.AppendLine("هر آموزش شامل یک عنوان و یک لینک از کانال یا سایت شماست.");
        text.AppendLine();
        if (tutorials.Count == 0)
            text.AppendLine("هنوز آموزشی ثبت نشده است.");
        else
            for (var i = 0; i < tutorials.Count; i++)
                text.AppendLine($"{i + 1}. <a href=\"{Html(tutorials[i].Url)}\">{Html(tutorials[i].Title)}</a>");

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ افزودن آموزش", OWNERCALLBACKPREFIX + "tutorial-add") }
        };
        for (var i = 0; i < tutorials.Count; i++)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData($"🗑 حذف {i + 1}", OWNERCALLBACKPREFIX + $"tutorial-del:{i}") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", OWNERCALLBACKPREFIX + "panel") });

        await SafeEditMessageTextAsync(
            botClient,
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageId,
            text.ToString(),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Starts owner input for adding a tenant tutorial title.
    /// </summary>
    /// <param name="botClient">Owned bot client used to prompt the owner.</param>
    /// <param name="callbackQuery">Owner callback that requested tutorial creation.</param>
    /// <param name="cancellationToken">Cancellation token for state and Telegram operations.</param>
    private async Task STARTTENANTTUTORIALADDASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        await _userDbcontext.SaveUserStatus(new User
        {
            Id = callbackQuery.From.Id,
            Flow = OWNERFLOW,
            LastStep = STEPTUTORIALTITLE
        });
        await botClient.SendTextMessageAsync(
            callbackQuery.Message.Chat.Id,
            "عنوان آموزش را ارسال کنید؛ مثلا: آموزش نصب روی اندروید",
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Saves the two-step tenant tutorial input collected from the owner.
    /// </summary>
    /// <param name="botClient">Owned bot client used to ask for the next field or show success.</param>
    /// <param name="message">Owner message containing title or URL.</param>
    /// <param name="owner">Colleague owner whose tenant tutorial list is updated.</param>
    /// <param name="state">Current owner state row.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    private async Task SAVETENANTTUTORIALSTEPASYNC(
        ITelegramBotClient botClient,
        Message message,
        CredUser owner,
        User state,
        CancellationToken cancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, cancellationToken);
        if (tenant == null)
            return;

        var text = message.Text?.Trim();
        if (state.LastStep == STEPTUTORIALTITLE)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "عنوان نمی‌تواند خالی باشد.", cancellationToken: cancellationToken);
                return;
            }

            state.Flow = OWNERFLOW;
            state.LastStep = STEPTUTORIALURL;
            state.SubLink = text;
            await _userDbcontext.SaveUserStatus(state);
            await botClient.SendTextMessageAsync(message.Chat.Id, "حالا لینک آموزش را ارسال کنید.", cancellationToken: cancellationToken);
            return;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out _))
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, "لینک معتبر نیست. یک لینک کامل مثل https://t.me/... ارسال کنید.", cancellationToken: cancellationToken);
            return;
        }

        var tutorials = ReadTenantTutorials(tenant).ToList();
        tutorials.Add(new TenantTutorialLink { Title = state.SubLink, Url = text });
        tenant.TenantTutorialsJson = JsonConvert.SerializeObject(tutorials);
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(cancellationToken);
        await _userDbcontext.ClearUserStatus(state);
        await botClient.SendTextMessageAsync(message.Chat.Id, "✅ آموزش ثبت شد.", replyMarkup: BUILDOWNERPANELKEYBOARD(tenant), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Deletes one tenant tutorial by list index.
    /// </summary>
    /// <param name="botClient">Owned bot client used to edit the tutorial manager.</param>
    /// <param name="callbackQuery">Owner callback containing the tutorial index.</param>
    /// <param name="owner">Colleague owner whose tutorial is deleted.</param>
    /// <param name="indexText">Zero-based tutorial index from callback data.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    private async Task DELETETENANTTUTORIALASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CredUser owner,
        string indexText,
        CancellationToken cancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, cancellationToken);
        var tutorials = ReadTenantTutorials(tenant).ToList();
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= tutorials.Count)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "آموزش پیدا نشد.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        tutorials.RemoveAt(index);
        tenant.TenantTutorialsJson = JsonConvert.SerializeObject(tutorials);
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(cancellationToken);
        await SHOWTENANTTUTORIALMANAGERASYNC(botClient, callbackQuery, owner, cancellationToken);
    }

    /// <summary>
    /// Starts the tenant broadcast input flow for a colleague owner.
    /// </summary>
    /// <param name="botClient">Owned bot client used to prompt the owner.</param>
    /// <param name="callbackQuery">Owner callback that opened broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for state and Telegram operations.</param>
    private async Task STARTTENANTBROADCASTASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        await _userDbcontext.SaveUserStatus(new User
        {
            Id = callbackQuery.From.Id,
            Flow = OWNERFLOW,
            LastStep = STEPBROADCASTINPUT
        });
        await botClient.SendTextMessageAsync(
            callbackQuery.Message.Chat.Id,
            "پیام عمومی فروشگاه را ارسال کنید.\nمی‌توانید متن بفرستید یا لینک پست کانال خودتان را ارسال کنید تا همان پست forward شود.",
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stores a tenant broadcast draft and asks the owner for final confirmation.
    /// </summary>
    /// <param name="botClient">Owned bot client used to show the preview.</param>
    /// <param name="message">Owner message containing text or a Telegram post URL.</param>
    /// <param name="owner">Colleague owner who owns the tenant broadcast audience.</param>
    /// <param name="cancellationToken">Cancellation token for state and Telegram operations.</param>
    private async Task PREPARETENANTBROADCASTASYNC(
        ITelegramBotClient botClient,
        Message message,
        CredUser owner,
        CancellationToken cancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, cancellationToken);
        if (tenant == null)
            return;

        var recipients = await GetTenantBroadcastRecipientsAsync(tenant, cancellationToken);
        var audienceCount = recipients.Count;
        var draft = message.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(draft))
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, "متن یا لینک پست نمی‌تواند خالی باشد.", cancellationToken: cancellationToken);
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..10];
        await _userDbcontext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = OWNERFLOW,
            LastStep = string.Empty,
            ConfigLink = token,
            SubLink = draft
        });

        await botClient.SendTextMessageAsync(
            message.Chat.Id,
            "📢 <b>پیش‌نمایش پیام عمومی</b>\n\n" +
            $"مخاطب‌های این فروشگاه: <code>{audienceCount}</code>\n" +
            $"محتوا:\n<code>{Html(draft)}</code>\n\n" +
            "ارسال فقط برای کاربرانی انجام می‌شود که همین tenant bot را start کرده‌اند.",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ ارسال نهایی", OWNERCALLBACKPREFIX + $"broadcast-send:{token}") },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت", OWNERCALLBACKPREFIX + "panel") }
            }),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Queues a confirmed tenant broadcast to users who have interacted with that exact tenant bot.
    /// </summary>
    /// <param name="botClient">Owned bot client used to edit progress for the owner.</param>
    /// <param name="callbackQuery">Owner confirmation callback containing the draft token.</param>
    /// <param name="owner">Colleague owner whose tenant audience receives the broadcast.</param>
    /// <param name="token">Draft token stored in the owner bot state.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    /// <remarks>
    /// The tenant audience is tenant-scoped through <see cref="GetTenantBroadcastRecipientsAsync" />.
    /// Delivery itself is delegated to <see cref="BroadcastManager"/> so tenant owners get the same retry,
    /// refresh, live progress, and final summary behavior as super-admin broadcasts. The status message is
    /// edited by the owned bot that received the owner callback, while each recipient receives the message
    /// from the tenant bot configured by the colleague.
    /// </remarks>
    private async Task SENDTENANTBROADCASTASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CredUser owner,
        string token,
        CancellationToken cancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, cancellationToken);
        var state = await _userDbcontext.GetUserStatus(owner.TelegramUserId);
        if (tenant == null || state?.ConfigLink != token || string.IsNullOrWhiteSpace(state.SubLink))
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "پیش‌نویس پیام عمومی پیدا نشد.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var content = state.SubLink;
        var recipients = await GetTenantBroadcastRecipientsAsync(tenant, cancellationToken);
        var template = BuildTenantBroadcastTemplate(content);

        await SafeEditMessageTextAsync(
            botClient,
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageId,
            $"در حال آماده‌سازی ارسال عمومی فروشگاه برای <code>{recipients.Count}</code> کاربر...",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);

        var job = await _broadcastManager.EnqueueAsync(
            recipients,
            template,
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageId,
            owner.TelegramUserId,
            cancellationToken,
            senderBotId: tenant.Id);

        await _userDbcontext.ClearUserStatus(state);
        await _broadcastManager.RefreshStatusMessageAsync(job.Id, cancellationToken);
        await botClient.SendTextMessageAsync(
            callbackQuery.Message.Chat.Id,
            "ارسال عمومی فروشگاه شروع شد. وضعیت را از پیام بالا پیگیری کنید.",
            replyMarkup: BUILDOWNERPANELKEYBOARD(tenant),
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Converts an owner tenant-broadcast draft into the shared broadcast template model.
    /// </summary>
    /// <param name="content">
    /// Owner-provided broadcast content. A public Telegram post URL is converted to a forward template;
    /// every other value is sent as plain text by the tenant bot.
    /// </param>
    /// <returns>
    /// A <see cref="BroadcastManager.BroadcastItem"/> without a recipient chat id. The caller supplies the
    /// tenant-scoped audience when enqueueing the job.
    /// </returns>
    /// <remarks>
    /// This helper deliberately does not read global users. It only decides how the already-approved tenant
    /// broadcast content should be delivered by <see cref="BroadcastManager"/>.
    /// </remarks>
    private static BroadcastManager.BroadcastItem BuildTenantBroadcastTemplate(string content)
    {
        if (TryParseTelegramPostLink(content, out var fromChat, out var messageId))
        {
            return new BroadcastManager.BroadcastItem
            {
                FromChatId = fromChat,
                MessageId = messageId,
                IsForward = true
            };
        }

        return new BroadcastManager.BroadcastItem
        {
            Text = content,
            IsForward = false
        };
    }

    /// <summary>
    /// Gets the exact Telegram audience for a tenant storefront broadcast.
    /// </summary>
    /// <param name="tenant">
    /// Tenant bot whose private-chat users should receive the broadcast. The method uses
    /// <see cref="BotInstance.Id"/> as the tenant-scoped key and never reads users from another bot.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the users.db query.</param>
    /// <returns>
    /// A distinct list of numeric Telegram user ids that have a <see cref="BotUserState"/> row for this exact
    /// tenant bot. The list can be empty when nobody has started or interacted with the storefront yet.
    /// </returns>
    /// <remarks>
    /// This is the only audience source for tenant broadcasts. A colleague owner must not send tenant broadcast
    /// messages to global owned-bot users, users of another tenant, or users known only from <c>credentials.db</c>.
    /// Telegram private chats use the numeric user id as the chat id, so the returned ids are safe to pass to
    /// the tenant bot client for text or forwarded-message delivery.
    /// </remarks>
    private async Task<List<long>> GetTenantBroadcastRecipientsAsync(BotInstance tenant, CancellationToken cancellationToken)
    {
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.Id))
            return new List<long>();

        return await _userDbcontext.BotUserStates
            .Where(x => x.BotId == tenant.Id && x.TelegramUserId > 0)
            .Select(x => x.TelegramUserId)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Shows seven-day tenant usage statistics from the JSONL user activity log.
    /// </summary>
    /// <param name="botClient">Owned bot client used to edit the owner-panel message.</param>
    /// <param name="callbackQuery">Owner callback that requested daily statistics.</param>
    /// <param name="owner">Colleague owner whose tenant bot stats are displayed.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    private async Task SHOWTENANTDAILYSTATSASYNC(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CredUser owner,
        CancellationToken cancellationToken)
    {
        var tenant = await GETTENANTBOTBYOWNERASYNC(owner.TelegramUserId, cancellationToken);
        if (tenant == null)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "ربات فروشگاهی پیدا نشد.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var stats = ReadTenantDailyStats(tenant.Id);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("📊 <b>آمار روزانه فروشگاه</b>");
        builder.AppendLine("۷ روز اخیر بر اساس فایل لاگ فعالیت:");
        builder.AppendLine();
        foreach (var item in stats)
            builder.AppendLine($"📅 <code>{Html(item.Date)}</code> | 👤 <code>{item.UniqueUsers}</code> کاربر | 💬 <code>{item.MessageCount}</code> پیام");

        await SafeEditMessageTextAsync(
            botClient,
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageId,
            builder.ToString(),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔄 بروزرسانی", OWNERCALLBACKPREFIX + "stats") },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت", OWNERCALLBACKPREFIX + "panel") }
            }),
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// sends A short owner hint for the Shared wallet ledger UNTIL the full owner-panel ledger VIEW is expanded.
    /// </summary>
    /// <param name="botClient">Main owned Bot client used to answer the callback.</param>
    /// <param name="CallbackQuery">owner callback that requested ledger Information.</param>
    /// <param name="owner">colleague User whose wallet ledger is stored UNDER the Shared credentials IDENTITY.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram calls.</param>
    /// <remarks>
    /// the general User Menu already EXPOSES the PAGINATED ledger by Telegram User Id. this method POINTS
    /// the tenant owner to the same ledger Source without creating A second INCONSISTENT financial VIEW.
    /// </remarks>
    private async Task ShowOwnerLedgerHintAsync(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
        await SafeEditMessageTextAsync(botClient, 
            CallbackQuery.Message.Chat.Id,
            CallbackQuery.Message.MessageId,
            "📌 تراکنش‌های کیف پول شما در بخش «تراکنش‌های من» منوی اصلی قابل مشاهده است.\nهمه فروش‌های tenant و کسر/افزایش موجودی هم در همان ledger ثبت می‌شود.",
            replyMarkup: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("بازگشت", OWNERCALLBACKPREFIX + "panel") } }),
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Shows the colleague-facing setup guide for BotFather, tenant storefront branding, forced join, and Sales Assistant usage.
    /// </summary>
    /// <param name="botClient">Main owned bot client used to edit the owner-panel message.</param>
    /// <param name="CallbackQuery">Owner callback that requested the guide.</param>
    /// <param name="owner">Colleague profile that owns the tenant storefront.</param>
    /// <param name="CancellationToken">Cancellation token for Telegram delivery.</param>
    /// <remarks>
    /// The guide is informational only. It does not mutate tenant settings and it does not validate tokens or channels.
    /// </remarks>
    private async Task SHOWOWNERGUIDEASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        var assistant = _botRegistry.Bots.FirstOrDefault(x => string.Equals(x.Type, BotInstanceTypes.SalesAssistant, StringComparison.OrdinalIgnoreCase));
        var assistantUsername = string.IsNullOrWhiteSpace(assistant?.Username) ? "ربات دستیار فروش" : "@" + assistant.Username.TrimStart('@');
        var text =
            "📘 <b>راهنمای پنل همکاری و ربات فروشگاهی</b>\n\n" +
            "1. داخل <b>@BotFather</b> دستور <code>/newbot</code> را بزنید و برای فروشگاه خودتان یک نام و username بسازید.\n" +
            "2. بعد از ساخت ربات، توکن را از BotFather بگیرید و در همین پنل با دکمه «ثبت/تغییر توکن» ذخیره کنید.\n" +
            "3. برای حرفه‌ای‌تر شدن فروشگاه، در BotFather از بخش‌های <code>/setuserpic</code>، <code>/setdescription</code> و <code>/setabouttext</code> عکس، توضیحات و متن معرفی ربات را تنظیم کنید.\n" +
            "4. اگر می‌خواهید جوین اجباری داشته باشید، ربات فروشگاهی را داخل کانال خودتان اضافه و admin کنید، سپس آیدی کانال را در بخش «کانال جوین» ثبت کنید.\n" +
            "5. ربات فروشگاهی و کانال بعد از ساخت دست خودتان می‌ماند و مشتری‌ها با برند شما خرید می‌کنند.\n\n" +
            $"🤖 <b>ربات دستیار فروش:</b> {Html(assistantUsername)}\n" +
            "این ربات برای نمایش فروش‌ها، رسیدهای کارت‌به‌کارت و تایید نهایی رسیدهاست. حتماً آن را start کنید. وقتی مشتری رسید کارت‌به‌کارت می‌فرستد، عکس رسید در دستیار فروش می‌آید؛ شما اول «تایید» و بعد «تایید نهایی» را می‌زنید.\n\n" +
            "اگر رسیدی به هر دلیل تایید نشد، از دکمه «تایید دستی کارت‌به‌کارت» در همین پنل استفاده کنید و OrderId سفارش را دقیق وارد کنید.";

        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
        await SafeEditMessageTextAsync(
            botClient,
            CallbackQuery.Message.Chat.Id,
            CallbackQuery.Message.MessageId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("بازگشت", OWNERCALLBACKPREFIX + "panel") } }),
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Sends a test message from the configured Sales Assistant bot to the colleague owner.
    /// </summary>
    /// <param name="botClient">Main owned bot client used to answer the callback in the owner panel.</param>
    /// <param name="CallbackQuery">Owner callback that requested the assistant test.</param>
    /// <param name="owner">Colleague profile whose Telegram id should receive the assistant test message.</param>
    /// <param name="CancellationToken">Cancellation token for Telegram delivery.</param>
    /// <remarks>
    /// Telegram only lets a bot message users who have started it. A <c>chat not found</c> or block error is
    /// reported to the owner as setup guidance instead of failing the tenant panel flow.
    /// </remarks>
    private async Task TESTSALESASSISTANTASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        var assistant = _botRegistry.Bots.FirstOrDefault(x => string.Equals(x.Type, BotInstanceTypes.SalesAssistant, StringComparison.OrdinalIgnoreCase));
        if (assistant == null || string.IsNullOrWhiteSpace(assistant.Token))
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "ربات دستیار فروش در کانفیگ فعال نشده است.", showAlert: true, cancellationToken: CancellationToken);
            return;
        }

        var assistantUsername = string.IsNullOrWhiteSpace(assistant.Username) ? "ربات دستیار فروش" : "@" + assistant.Username.TrimStart('@');
        try
        {
            await _botClientProvider.GetClient(assistant.Id).SendTextMessageAsync(
                owner.TelegramUserId,
                "✅ تست ربات دستیار فروش موفق بود.\nاز این به بعد فروش‌ها و رسیدهای کارت‌به‌کارت اینجا برای شما ارسال می‌شود.",
                cancellationToken: CancellationToken);

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "پیام تست در ربات دستیار فروش ارسال شد.", showAlert: true, cancellationToken: CancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, $"اول {assistantUsername} را start کنید، بعد دوباره تست بگیرید.", showAlert: true, cancellationToken: CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sales assistant test failed. owner={OwnerTelegramUserId}", owner.TelegramUserId);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "ارسال پیام تست ناموفق بود. لاگ را بررسی کنید.", showAlert: true, cancellationToken: CancellationToken);
        }
    }

    /// <summary>
    /// Starts the owner-side flow that asks for a tenant card-to-card order id and manually approves it.
    /// </summary>
    /// <param name="botClient">Main owned bot client used to prompt the colleague owner.</param>
    /// <param name="CallbackQuery">Owner callback that requested manual card-order confirmation.</param>
    /// <param name="CancellationToken">Cancellation token for state persistence and Telegram delivery.</param>
    /// <remarks>
    /// The next owner text message is interpreted as an exact tenant <c>OrderId</c>. The final approval still
    /// runs through the shared idempotent fulfillment path so duplicate approvals cannot create duplicate accounts.
    /// </remarks>
    private async Task STARTMANUALCARDORDERCONFIRMASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CancellationToken CancellationToken)
    {
        await _userDbcontext.SaveUserStatus(new User
        {
            Id = CallbackQuery.From.Id,
            Flow = OWNERFLOW,
            LastStep = STEPMANUALCARDORDERID
        });

        await botClient.SendTextMessageAsync(
            CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id,
            "OrderId سفارش کارت‌به‌کارت را دقیق ارسال کنید.\nمثال: <code>TENANTBOT-...</code>",
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: CancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Handles tenant customer Text messages such as /start, purchase, TARIFFS, support, and payment return payloads.
    /// </summary>
    /// <param name="botClient">tenant Bot client.</param>
    /// <param name="Message">Incoming customer Message.</param>
    /// <param name="customer">Shared customer profile.</param>
    /// <param name="User">
    /// Bot-scoped conversation state for this Telegram user. The state belongs to the active tenant bot and
    /// is passed through to shared XUI handlers so account search, comment changes, and renewal steps do not
    /// leak into another bot instance.
    /// </param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <remarks>
    /// This method handles tenant-only purchase and payment messages locally, but delegates account-management
    /// commands to <see cref="XuiV3BotFlowService" />. <c>/start</c> and top-level tenant menu buttons are handled
    /// before any state-machine step so a stale renewal/search state cannot treat commands as XUI account identifiers.
    /// </remarks>
    private async Task HANDLECUSTOMERMESSAGEASYNC(
        ITelegramBotClient botClient,
        Message Message,
        CredUser customer,
        User User,
        CancellationToken CancellationToken)
    {
        var tenant = await GetCurrentTenantBotAsync(CancellationToken);
        if (tenant == null || !tenant.Enabled)
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "فروشگاه در حال حاضر غیرفعال است.", cancellationToken: CancellationToken);
            return;
        }

        if (!await EnsureTenantCustomerJoinAsync(botClient, Message, tenant, CancellationToken))
            return;

        if (Message.Photo?.Length > 0)
        {
            await CREATETENANTMANUALRECEIPTASYNC(botClient, Message, tenant, customer, CancellationToken);
            return;
        }

        var Text = Message.Text?.Trim() ?? string.Empty;
        var tenantReplyKeyboard = BuildTenantReplyKeyboard();

        if (Text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            if (Text.Contains("payment_success", StringComparison.OrdinalIgnoreCase))
            {
                await botClient.SendTextMessageAsync(Message.Chat.Id, "پرداخت از سمت درگاه پرداخت تایید شد. در حال بررسی وضعیت سفارش...", cancellationToken: CancellationToken);
                await CHECKLATESTCUSTOMERORDERASYNC(botClient, Message.Chat.Id, customer.TelegramUserId, CancellationToken);
                return;
            }

            if (Text.Contains("payment_cancel", StringComparison.OrdinalIgnoreCase))
            {
                await MARKLATESTCUSTOMERORDERCANCELLEDASYNC(botClient, Message.Chat.Id, customer.TelegramUserId, CancellationToken);
                return;
            }

            await SendTenantHomeAsync(botClient, Message.Chat.Id, tenant, CancellationToken);
            return;
        }

        if (Text == "خرید اکانت" || Text == "💳 خرید اکانت")
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            await SendServiceSelectionAsync(botClient, Message.Chat.Id, tenant, CancellationToken);
            return;
        }

        if (Text == "تعرفه‌ها" || Text == "📋 تعرفه‌ها")
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                BUILDTENANTTARIFFSTEXT(tenant),
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantReplyKeyboard(),
                cancellationToken: CancellationToken);
            return;
        }

        if (Text == "پشتیبانی" || Text == "💬 پشتیبانی")
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            var support = BuildTenantSupportContactHtml(tenant.SupportAccount);
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                $"برای پشتیبانی فروشگاه به این آیدی پیام بدهید:\n{support}",
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantReplyKeyboard(),
                cancellationToken: CancellationToken);
            return;
        }

        if (IsTenantTutorialCommand(Text))
        {
            await _userDbcontext.ClearUserStatus(new User { Id = Message.From.Id });
            await SendTenantTutorialsAsync(botClient, Message.Chat.Id, tenant, CancellationToken);
            return;
        }

        if (await TRYHANDLETENANTPURCHASETEXTASYNC(
                botClient,
                Message,
                tenant,
                User,
                CancellationToken))
            return;

        if (await _xuiV3BotFlowService.TryHandleAccountCommentTextAsync(
                botClient,
                Message,
                customer,
                User,
                tenantReplyKeyboard,
                CancellationToken))
            return;

        if (IsTenantMyAccountsCommand(Text))
        {
            if (await _xuiV3BotFlowService.TryHandleMyAccountsAsync(
                    botClient,
                    Message,
                    customer,
                    tenantReplyKeyboard,
                    CancellationToken))
                return;
        }

        if (await _xuiV3BotFlowService.TryHandleAccountSearchAsync(
                botClient,
                Message,
                customer,
                User,
                tenantReplyKeyboard,
                CancellationToken))
            return;

        if (await TRYHANDLETENANTRENEWASYNC(
                botClient,
                Message,
                tenant,
                customer,
                User,
                tenantReplyKeyboard,
                CancellationToken))
            return;

        await SendTenantHomeAsync(botClient, Message.Chat.Id, tenant, CancellationToken);
    }

    /// <summary>
    /// Handles typed metered traffic while a tenant customer is in the purchase traffic-selection step.
    /// </summary>
    /// <param name="botClient">Tenant bot client that received the customer's typed traffic value.</param>
    /// <param name="message">Incoming customer text message, expected to contain a GB amount such as <c>12</c> or <c>12 GB</c>.</param>
    /// <param name="tenant">Tenant storefront whose prices will be used after a valid traffic amount is selected.</param>
    /// <param name="user">
    /// Bot-scoped customer state. The state belongs to the active tenant bot and carries the selected metered service key.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for users.db state updates and Telegram replies.</param>
    /// <returns>
    /// <c>true</c> when the message belonged to the tenant purchase traffic state and was handled; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Tenant purchase originally accepted only inline traffic buttons. This method adds typed custom traffic while
    /// preserving the existing callback path. It validates the typed amount against the same plan-file
    /// <c>minimumTrafficGb</c> rule used by owned bots before showing duration choices.
    /// </remarks>
    private async Task<bool> TRYHANDLETENANTPURCHASETEXTASYNC(
        ITelegramBotClient botClient,
        Message message,
        BotInstance tenant,
        User user,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(user?.Flow, TENANTPURCHASEFLOW, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(user.SelectedCountry))
        {
            return false;
        }

        var text = message.Text?.Trim() ?? string.Empty;
        if (IsCancelText(text))
        {
            await _userDbcontext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "فرایند خرید لغو شد.",
                replyMarkup: BuildTenantReplyKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        var service = _purchaseService.GetEnabledServices()
            .FirstOrDefault(x => string.Equals(x.Key, user.SelectedCountry, StringComparison.OrdinalIgnoreCase));
        if (service == null || service.IsUnlimited)
        {
            await _userDbcontext.ClearUserStatus(user);
            await SendTenantHomeAsync(botClient, message.Chat.Id, tenant, cancellationToken);
            return true;
        }

        if (user.LastStep == TENANTPURCHASESTEPTRAFFIC)
        {
            if (!TryParseTenantTrafficSelection(text, service, out var trafficGb))
            {
                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    BuildTenantMinimumTrafficMessage(service),
                    replyMarkup: BuildTenantTrafficInlineKeyboard(service),
                    cancellationToken: cancellationToken);
                return true;
            }

            await _userDbcontext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = TENANTPURCHASEFLOW,
                LastStep = TENANTPURCHASESTEPDURATION,
                SelectedCountry = service.Key,
                TotoalGB = trafficGb.ToString(CultureInfo.InvariantCulture)
            });

            await SHOWDURATIONOPTIONSASYNC(
                botClient,
                message.Chat.Id,
                null,
                tenant,
                service.Key,
                trafficGb,
                cancellationToken);
            return true;
        }

        await botClient.SendTextMessageAsync(
            message.Chat.Id,
            "برای ادامه، مدت سرویس را از دکمه‌های پیام قبلی انتخاب کنید یا انصراف دهید.",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به انتخاب سرویس", CUSTOMERCALLBACKPREFIX + "services") }
            }),
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>
    /// Checks whether a tenant customer asked to see the XUI accounts already linked to their Telegram id.
    /// </summary>
    /// <param name="text">
    /// The raw reply-keyboard text received from the tenant bot customer. The value is tenant-scoped only by
    /// the active <see cref="BotContextAccessor" /> context; it is not a Telegram command or payment provider id.
    /// </param>
    /// <returns>
    /// <c>true</c> when the text is one of the tenant-facing aliases for the existing "my accounts" flow.
    /// </returns>
    /// <remarks>
    /// The shared XUI flow historically starts from "وضعیت اکانت های من"; tenant storefronts display the
    /// shorter "اکانت‌های من" label, so the tenant layer normalizes that label before delegating to the shared service.
    /// </remarks>
    private static bool IsTenantMyAccountsCommand(string text)
    {
        return string.Equals(text, "اکانت‌های من", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "اکانت های من", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "وضعیت اکانت های من", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a tenant customer opened the installation tutorial menu.
    /// </summary>
    /// <param name="text">The tenant customer reply-keyboard text. Empty values are treated as non-matches.</param>
    /// <returns><c>true</c> when the customer selected the tenant tutorial command.</returns>
    private static bool IsTenantTutorialCommand(string text)
    {
        return string.Equals(text, "راهنما نصب", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "💡راهنما نصب", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Handles the tenant-specific renewal flow without debiting the customer's shared wallet.
    /// </summary>
    /// <param name="botClient">Tenant bot client that is serving the storefront customer.</param>
    /// <param name="message">Incoming tenant customer message.</param>
    /// <param name="tenant">Tenant bot whose pricing and payment settings apply to the renewal.</param>
    /// <param name="customer">Credentials profile of the customer requesting renewal.</param>
    /// <param name="user">Bot-scoped state row for this customer and tenant bot.</param>
    /// <param name="mainReplyMarkup">Tenant reply keyboard restored when the flow ends or fails.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram, database, and panel calls.</param>
    /// <returns>
    /// <c>true</c> when the message belonged to the tenant renewal state machine; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Tenant renewals intentionally do not call the owned-bot renewal completion path because that path debits
    /// the customer's wallet. This flow only collects the same plan choices, calculates the same tenant sale
    /// price used by purchases, and creates a tenant order that is fulfilled after payment settlement.
    /// </remarks>
    private async Task<bool> TRYHANDLETENANTRENEWASYNC(
        ITelegramBotClient botClient,
        Message message,
        BotInstance tenant,
        CredUser customer,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        var text = message.Text?.Trim() ?? string.Empty;
        if (string.Equals(user?.Flow, TENANTRENEWFLOW, StringComparison.Ordinal))
        {
            await HANDLETENANTRENEWSTEPASYNC(botClient, message, tenant, customer, user, mainReplyMarkup, cancellationToken);
            return true;
        }

        if (!IsTenantRenewCommand(text))
            return false;

        await _userDbcontext.ClearUserStatus(new User { Id = message.From.Id });
        await _userDbcontext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = TENANTRENEWFLOW,
            LastStep = TENANTRENEWSTEPACCOUNT
        });

        await botClient.SendTextMessageAsync(
            message.Chat.Id,
            "نام اکانت، UUID، ساب‌لینک یا لینک کانفیگ اکانتی که می‌خواهید تمدید کنید را ارسال کنید.",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>
    /// Advances one step of the tenant renewal state machine.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to answer the customer.</param>
    /// <param name="message">Customer message containing the current renewal input.</param>
    /// <param name="tenant">Tenant bot whose pricing settings apply.</param>
    /// <param name="customer">Customer profile that must own the target account.</param>
    /// <param name="user">Current bot-scoped renewal state.</param>
    /// <param name="mainReplyMarkup">Tenant reply keyboard used after completion.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <remarks>
    /// The method stores only temporary selection state in users.db. The actual renewal is not applied until
    /// a tenant payment order is settled, which keeps duplicate callbacks idempotent.
    /// </remarks>
    private async Task HANDLETENANTRENEWSTEPASYNC(
        ITelegramBotClient botClient,
        Message message,
        BotInstance tenant,
        CredUser customer,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        var text = message.Text?.Trim() ?? string.Empty;
        if (IsCancelText(text))
        {
            await _userDbcontext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(message.Chat.Id, "فرایند تمدید لغو شد.", replyMarkup: mainReplyMarkup, cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == TENANTRENEWSTEPACCOUNT)
        {
            await STARTTENANTRENEWSELECTIONASYNC(botClient, message, tenant, customer, text, mainReplyMarkup, cancellationToken);
            return;
        }

        var service = _purchaseService.GetEnabledServices().FirstOrDefault(x => string.Equals(x.Key, user.SelectedCountry, StringComparison.OrdinalIgnoreCase));
        if (service == null)
        {
            await _userDbcontext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(message.Chat.Id, "سرویس اکانت برای تمدید پیدا نشد. دوباره از منوی تمدید اقدام کنید.", replyMarkup: mainReplyMarkup, cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == TENANTRENEWSTEPTRAFFIC)
        {
            if (!TryParseTenantTrafficSelection(text, service, out var trafficGb))
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, BuildTenantMinimumTrafficMessage(service), replyMarkup: BuildTenantRenewTrafficKeyboard(service), cancellationToken: cancellationToken);
                return;
            }

            user.Flow = TENANTRENEWFLOW;
            user.LastStep = TENANTRENEWSTEPDURATION;
            user.TotoalGB = trafficGb.ToString(CultureInfo.InvariantCulture);
            await _userDbcontext.SaveUserStatus(user);
            await botClient.SendTextMessageAsync(message.Chat.Id, "مدت تمدید را انتخاب کنید:", replyMarkup: BuildTenantRenewDurationKeyboard(service), cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == TENANTRENEWSTEPDURATION)
        {
            var duration = FindTenantDurationOption(service, text);
            if (duration == null)
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "مدت تمدید معتبر نیست. یکی از گزینه‌های زیر را انتخاب کنید.", replyMarkup: BuildTenantRenewDurationKeyboard(service), cancellationToken: cancellationToken);
                return;
            }

            user.Flow = TENANTRENEWFLOW;
            user.LastStep = TENANTRENEWSTEPCONFIRM;
            user.SelectedPeriod = duration.Key;
            await _userDbcontext.SaveUserStatus(user);
            await SendTenantRenewSummaryAsync(botClient, message.Chat.Id, tenant, customer, user, cancellationToken);
            return;
        }

        if (user.LastStep == TENANTRENEWSTEPUNLIMITEDPLAN)
        {
            var plan = FindTenantUnlimitedPlan(service, text);
            if (plan == null)
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "پلن تمدید معتبر نیست. یکی از گزینه‌های زیر را انتخاب کنید.", replyMarkup: BuildTenantRenewUnlimitedKeyboard(service, tenant), cancellationToken: cancellationToken);
                return;
            }

            user.Flow = TENANTRENEWFLOW;
            user.LastStep = TENANTRENEWSTEPCONFIRM;
            user.Type = plan.Key;
            await _userDbcontext.SaveUserStatus(user);
            await SendTenantRenewSummaryAsync(botClient, message.Chat.Id, tenant, customer, user, cancellationToken);
            return;
        }

        if (user.LastStep == TENANTRENEWSTEPCONFIRM)
        {
            if (!IsConfirmText(text))
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "برای ساخت فاکتور تمدید، گزینه تایید را بزنید یا انصراف دهید.", replyMarkup: BuildTenantRenewConfirmKeyboard(), cancellationToken: cancellationToken);
                return;
            }

            await CreateTenantRenewOrderFromStateAsync(botClient, message.Chat.Id, tenant, customer, user, mainReplyMarkup, cancellationToken);
        }
    }

    /// <summary>
    /// Finds the target XUI account and starts selecting a renewal plan for it.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send the next selection keyboard.</param>
    /// <param name="message">Customer message containing the account identifier.</param>
    /// <param name="tenant">Tenant bot that owns the storefront.</param>
    /// <param name="customer">Customer who must own the account.</param>
    /// <param name="input">Account email, UUID, config link, or subscription link.</param>
    /// <param name="mainReplyMarkup">Tenant reply keyboard restored on failure.</param>
    /// <param name="cancellationToken">Cancellation token for panel and Telegram operations.</param>
    private async Task STARTTENANTRENEWSELECTIONASYNC(
        ITelegramBotClient botClient,
        Message message,
        BotInstance tenant,
        CredUser customer,
        string input,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        var serverInfo = BuildConfiguredPanelServerInfo();
        var client = await FindTenantClientAsync(serverInfo, input, cancellationToken);
        if (client == null || !ClientBelongsToTenantCustomer(client, customer.TelegramUserId, tenant.Id))
        {
            await _userDbcontext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.SendTextMessageAsync(message.Chat.Id, "اکانت پیدا نشد یا متعلق به حساب شما در این فروشگاه نیست.", replyMarkup: mainReplyMarkup, cancellationToken: cancellationToken);
            return;
        }

        var service = ResolveTenantServiceForClient(client);
        if (service == null)
        {
            await _userDbcontext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.SendTextMessageAsync(message.Chat.Id, "این اکانت مربوط به پلن‌های فعال فروشگاه نیست و از این مسیر قابل تمدید نیست.", replyMarkup: mainReplyMarkup, cancellationToken: cancellationToken);
            return;
        }

        await _userDbcontext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = TENANTRENEWFLOW,
            LastStep = service.IsUnlimited ? TENANTRENEWSTEPUNLIMITEDPLAN : TENANTRENEWSTEPTRAFFIC,
            ConfigLink = client.Email,
            SelectedCountry = service.Key
        });

        await botClient.SendTextMessageAsync(
            message.Chat.Id,
            service.IsUnlimited ? "پلن تمدید نامحدود را انتخاب کنید:" : $"حجم تمدید را انتخاب کنید یا حجم دلخواه را به GB وارد کنید.\nحداقل حجم این سرویس {XuiV3PurchaseService.GetMinimumTrafficGb(service)} GB است.",
            replyMarkup: service.IsUnlimited ? BuildTenantRenewUnlimitedKeyboard(service, tenant) : BuildTenantRenewTrafficKeyboard(service),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sends the tenant renewal summary before creating a payable tenant order.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send the summary.</param>
    /// <param name="chatId">Customer chat id receiving the renewal summary.</param>
    /// <param name="tenant">Tenant bot whose pricing settings apply.</param>
    /// <param name="customer">Customer requesting the renewal.</param>
    /// <param name="user">Bot-scoped state containing the target email and selected renewal plan.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram and panel operations.</param>
    private async Task SendTenantRenewSummaryAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        BotInstance tenant,
        CredUser customer,
        User user,
        CancellationToken cancellationToken)
    {
        var selection = BuildTenantRenewSelectionFromState(user);
        var resolved = _purchaseService.ResolvePurchase(selection, false);
        var price = CalculateTenantPrice(tenant, selection);
        var serverInfo = BuildConfiguredPanelServerInfo();
        var client = await FindTenantClientAsync(serverInfo, user.ConfigLink, cancellationToken);
        var renewal = client == null ? null : XuiV3RenewalPolicy.Calculate(client, resolved, "tenant-renew-summary", customer.TelegramUserId);

        var fairUsageLine = resolved.IsUnlimited
            ? $"حد مصرف منصفانه قابل استفاده بعد از تمدید: <code>{Html((renewal?.TargetAvailableTrafficGb ?? resolved.TrafficGb).ToString(CultureInfo.InvariantCulture))} GB</code>\n"
            : $"حجم تمدید: <code>{Html(resolved.TrafficGb.ToString(CultureInfo.InvariantCulture))} GB</code>\n";

        var text =
            "✅ <b>خلاصه تمدید اکانت</b>\n\n" +
            $"اکانت: <code>{Html(user.ConfigLink)}</code>\n" +
            $"سرویس: <b>{Html(resolved.Service.DisplayName)}</b>\n" +
            fairUsageLine +
            $"مدت افزوده: <code>{Html(resolved.DurationDays <= 0 ? "نامحدود" : resolved.DurationDays + " روز")}</code>\n" +
            (renewal == null ? string.Empty : $"مدت نهایی بعد از تمدید: <code>{Html(renewal.FinalDurationDays <= 0 ? "نامحدود" : renewal.FinalDurationDays + " روز")}</code>\n") +
            $"مبلغ قابل پرداخت: <b>{Html(price.SalePriceToman.FormatCurrency())}</b>\n\n" +
            "بعد از تایید، روش پرداخت را انتخاب می‌کنید و پس از پرداخت موفق اکانت تمدید می‌شود.";

        await botClient.SendTextMessageAsync(
            chatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantRenewConfirmKeyboard(),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates a pending tenant renewal order from the customer's saved renewal state.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send payment buttons.</param>
    /// <param name="chatId">Customer chat id that receives payment choices.</param>
    /// <param name="tenant">Tenant bot that owns the renewal order.</param>
    /// <param name="customer">Customer who will pay for the renewal.</param>
    /// <param name="user">Bot-scoped state containing target account and selected plan.</param>
    /// <param name="mainReplyMarkup">Tenant reply keyboard restored after order creation.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    /// <remarks>
    /// The order is not fulfilled here. Payment callbacks and IPNs later call the shared tenant fulfillment
    /// routine, which applies the renewal exactly once.
    /// </remarks>
    private async Task CreateTenantRenewOrderFromStateAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        BotInstance tenant,
        CredUser customer,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        var selection = BuildTenantRenewSelectionFromState(user);
        var price = CalculateTenantPrice(tenant, selection);
        var order = CreateTenantOrder(tenant, customer, chatId, selection, price, "pending");
        order.OrderKind = TenantBotOrderKinds.Renew;
        order.TargetAccountEmail = user.ConfigLink;
        order.PaymentStatus = TenantBotOrderStatuses.Pending;
        order.UpdatedAtUtc = DateTime.UtcNow;

        _userDbcontext.TenantBotOrders.Add(order);
        await _userDbcontext.SaveChangesAsync(cancellationToken);
        await _userDbcontext.ClearUserStatus(user);

        await botClient.SendTextMessageAsync(
            chatId,
            BuildTenantRenewOrderPaymentChoiceText(order),
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantRenewPaymentProviderKeyboard(order, tenant),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds the selected renewal plan from temporary bot state.
    /// </summary>
    /// <param name="user">Bot-scoped state row created by the tenant renewal flow.</param>
    /// <returns>The selected XUI v3 purchase selection reused for renewal pricing.</returns>
    private static XuiV3PurchaseSelection BuildTenantRenewSelectionFromState(User user)
    {
        return string.IsNullOrWhiteSpace(user.Type)
            ? new XuiV3PurchaseSelection
            {
                ServiceKey = user.SelectedCountry,
                TrafficGb = int.TryParse(user.TotoalGB, NumberStyles.Integer, CultureInfo.InvariantCulture, out var trafficGb) ? trafficGb : 0,
                DurationKey = user.SelectedPeriod
            }
            : new XuiV3PurchaseSelection
            {
                ServiceKey = user.SelectedCountry,
                UnlimitedPlanKey = user.Type
            };
    }

    /// <summary>
    /// Checks whether a tenant customer selected the renewal command.
    /// </summary>
    /// <param name="text">Raw reply-keyboard text from the tenant bot.</param>
    /// <returns><c>true</c> when the text starts the tenant renewal flow.</returns>
    private static bool IsTenantRenewCommand(string text)
    {
        return string.Equals(text, "تمدید اکانت", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "🔄 تمدید اکانت", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a tenant customer confirmed a state-machine step.
    /// </summary>
    /// <param name="text">Raw customer text.</param>
    /// <returns><c>true</c> when the text is the tenant confirmation label.</returns>
    private static bool IsConfirmText(string text)
    {
        return string.Equals(text, "تایید", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "✅ تایید", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a tenant customer cancelled a state-machine step.
    /// </summary>
    /// <param name="text">Raw customer text.</param>
    /// <returns><c>true</c> when the text is a recognized cancel label.</returns>
    private static bool IsCancelText(string text)
    {
        return string.Equals(text, "انصراف", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "❌ انصراف", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the reply keyboard used to choose metered renewal traffic.
    /// </summary>
    /// <param name="service">Metered XUI service definition.</param>
    /// <returns>Reply keyboard containing traffic options and cancel.</returns>
    private static ReplyKeyboardMarkup BuildTenantRenewTrafficKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = XuiV3PurchaseService.GetVisibleTrafficOptions(service)
            .Chunk(3)
            .Select(chunk => chunk.Select(x => new KeyboardButton($"{x} GB")).ToArray())
            .Append(new[] { new KeyboardButton("❌ انصراف") })
            .ToArray();
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    /// <summary>
    /// Builds the reply keyboard used to choose metered renewal duration.
    /// </summary>
    /// <param name="service">Metered XUI service definition.</param>
    /// <returns>Reply keyboard containing duration options and cancel.</returns>
    private static ReplyKeyboardMarkup BuildTenantRenewDurationKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = service.DurationOptions
            .OrderBy(x => x.Days)
            .Select(x => new[] { new KeyboardButton($"{x.DisplayName} [{x.Key}]") })
            .Append(new[] { new KeyboardButton("❌ انصراف") })
            .ToArray();
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    /// <summary>
    /// Builds the reply keyboard used to choose an unlimited renewal plan with tenant prices.
    /// </summary>
    /// <param name="service">Unlimited XUI service definition.</param>
    /// <param name="tenant">Tenant bot whose markup controls displayed prices.</param>
    /// <returns>Reply keyboard containing unlimited plans and cancel.</returns>
    private ReplyKeyboardMarkup BuildTenantRenewUnlimitedKeyboard(XuiV3ServiceDefinition service, BotInstance tenant)
    {
        var rows = service.UnlimitedPlans
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Days)
            .Select(plan =>
            {
                var selection = new XuiV3PurchaseSelection { ServiceKey = service.Key, UnlimitedPlanKey = plan.Key };
                var price = CalculateTenantPrice(tenant, selection).SalePriceToman;
                return new[] { new KeyboardButton($"{plan.DisplayName} [{plan.Key}] - {price.FormatCurrency()}") };
            })
            .Append(new[] { new KeyboardButton("❌ انصراف") })
            .ToArray();
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    /// <summary>
    /// Builds the final confirmation keyboard for tenant renewal order creation.
    /// </summary>
    /// <returns>Reply keyboard with confirm and cancel labels.</returns>
    private static ReplyKeyboardMarkup BuildTenantRenewConfirmKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("✅ تایید"), new KeyboardButton("❌ انصراف") }
        })
        {
            ResizeKeyboard = true
        };
    }

    /// <summary>
    /// Parses and validates tenant metered traffic entered by button or typed text.
    /// </summary>
    /// <param name="text">Customer text such as <c>20 GB</c>, <c>20</c>, or Persian-digit equivalents.</param>
    /// <param name="service">Metered service whose minimum traffic rule is enforced.</param>
    /// <param name="trafficGb">Selected traffic in GB when parsing and minimum validation succeed.</param>
    /// <returns>
    /// <c>true</c> when the text contains a positive integer GB amount that is at least the service minimum;
    /// otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Tenant storefront purchases can use custom typed traffic, so this parser must not require the amount to
    /// exist in <c>trafficOptionsGb</c>. Preset buttons are only suggestions.
    /// </remarks>
    private static bool TryParseTenantTrafficSelection(string text, XuiV3ServiceDefinition service, out int trafficGb)
    {
        var digits = new string(NormalizeTenantDigits(text ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out trafficGb) &&
               trafficGb > 0 &&
               XuiV3PurchaseService.MeetsMinimumTraffic(service, trafficGb);
    }

    /// <summary>
    /// Builds tenant purchase inline buttons for visible metered traffic presets.
    /// </summary>
    /// <param name="service">Metered service whose visible preset values should be shown.</param>
    /// <returns>Inline keyboard with traffic buttons that satisfy the service minimum, plus a back button.</returns>
    /// <remarks>
    /// The keyboard is intentionally not the full policy surface: customers may type any larger custom traffic
    /// amount, and <see cref="TryParseTenantTrafficSelection"/> validates it.
    /// </remarks>
    private static InlineKeyboardMarkup BuildTenantTrafficInlineKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = XuiV3PurchaseService.GetVisibleTrafficOptions(service)
            .Chunk(2)
            .Select(chunk => chunk
                .Select(gb => InlineKeyboardButton.WithCallbackData($"{gb} GB", CUSTOMERCALLBACKPREFIX + $"GB:{service.Key}:{gb}"))
                .ToArray())
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", CUSTOMERCALLBACKPREFIX + "services") })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Builds the Persian validation message shown when tenant traffic input is invalid or below the service minimum.
    /// </summary>
    /// <param name="service">Metered service whose minimum traffic should be displayed.</param>
    /// <returns>Customer-facing text explaining that a larger integer GB amount is required.</returns>
    private static string BuildTenantMinimumTrafficMessage(XuiV3ServiceDefinition service)
    {
        if (service == null)
            return "حجم وارد شده معتبر نیست. لطفاً دوباره تلاش کنید.";

        return $"حجم وارد شده معتبر نیست. حداقل حجم این سرویس {XuiV3PurchaseService.GetMinimumTrafficGb(service)} GB است. می‌توانید یکی از دکمه‌ها را بزنید یا عدد دلخواه بزرگ‌تر را وارد کنید.";
    }

    /// <summary>
    /// Converts Persian and Arabic digits in customer input to ASCII digits before numeric parsing.
    /// </summary>
    /// <param name="text">Raw tenant customer text, possibly containing Persian or Arabic digits.</param>
    /// <returns>Text with localized decimal digits converted to ASCII digits; null becomes an empty string.</returns>
    private static string NormalizeTenantDigits(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var numericValue = char.GetNumericValue(ch);
            builder.Append(numericValue is >= 0 and <= 9 && Math.Abs(numericValue % 1) < double.Epsilon
                ? (char)('0' + (int)numericValue)
                : ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Finds a duration option from a tenant reply-keyboard label.
    /// </summary>
    /// <param name="service">Service containing duration options.</param>
    /// <param name="text">Customer text containing display name or key.</param>
    /// <returns>The matched duration option, or <c>null</c> when invalid.</returns>
    private static XuiV3DurationOption FindTenantDurationOption(XuiV3ServiceDefinition service, string text)
    {
        return service.DurationOptions.FirstOrDefault(x =>
            string.Equals(text, x.Key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, x.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            (text?.Contains($"[{x.Key}]", StringComparison.OrdinalIgnoreCase) == true));
    }

    /// <summary>
    /// Finds an unlimited plan from a tenant reply-keyboard label.
    /// </summary>
    /// <param name="service">Unlimited service containing plan options.</param>
    /// <param name="text">Customer text containing display name or key.</param>
    /// <returns>The matched plan, or <c>null</c> when invalid.</returns>
    private static XuiV3UnlimitedPlan FindTenantUnlimitedPlan(XuiV3ServiceDefinition service, string text)
    {
        return service.UnlimitedPlans.FirstOrDefault(x => x.IsEnabled &&
            (string.Equals(text, x.Key, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(text, x.DisplayName, StringComparison.OrdinalIgnoreCase) ||
             (text?.Contains($"[{x.Key}]", StringComparison.OrdinalIgnoreCase) == true)));
    }

    /// <summary>
    /// Sends tenant-specific installation tutorial links when the owner configured them for the storefront.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send the tutorial message.</param>
    /// <param name="chatId">Telegram chat id of the tenant customer requesting help.</param>
    /// <param name="tenant">Tenant bot row that owns tutorial link configuration.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send operation.</param>
    /// <remarks>
    /// Tutorial links are tenant-owned storefront settings. When no links are configured, the customer receives a
    /// clear tenant message instead of falling back to the platform-owned bot tutorials.
    /// </remarks>
    private async Task SendTenantTutorialsAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        BotInstance tenant,
        CancellationToken cancellationToken)
    {
        var rows = ReadTenantTutorials(tenant)
            .Select(x => new[] { InlineKeyboardButton.WithUrl(x.Title, x.Url) })
            .ToList();

        if (rows.Count == 0)
        {
            await botClient.SendTextMessageAsync(
                chatId,
                "هنوز آموزشی برای این فروشگاه ثبت نشده است. لطفاً با پشتیبانی فروشگاه تماس بگیرید.",
                replyMarkup: BuildTenantReplyKeyboard(),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId,
            "برای دریافت آموزش، یکی از دکمه‌های زیر را انتخاب کنید:",
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Adds URL buttons for one tutorial platform to a tenant tutorial keyboard.
    /// </summary>
    /// <param name="rows">Mutable inline-keyboard rows that will be sent to the tenant customer.</param>
    /// <param name="label">Human-readable platform label, such as Android or Windows.</param>
    /// <param name="json">JSON array of URLs stored on the tenant bot row; null or invalid JSON is ignored.</param>
    /// <remarks>
    /// Invalid or non-HTTP URLs are skipped so tenant owners cannot accidentally create Telegram buttons that fail
    /// at send time.
    /// </remarks>
    private static void AddTenantTutorialButtons(List<InlineKeyboardButton[]> rows, string label, string json)
    {
        var urls = DeserializeTenantStringArray(json);
        for (var i = 0; i < urls.Length; i++)
        {
            var url = urls[i];
            if (string.IsNullOrWhiteSpace(url) ||
                (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var suffix = urls.Length == 1 ? string.Empty : $" {i + 1}";
            rows.Add(new[] { InlineKeyboardButton.WithUrl($"{label}{suffix}", url) });
        }
    }

    /// <summary>
    /// Deserializes a tenant-owned JSON string array without throwing into the update pipeline.
    /// </summary>
    /// <param name="json">JSON array stored in users.db for one tenant tutorial platform.</param>
    /// <returns>An array of configured strings; empty when the value is null, empty, or invalid.</returns>
    private static string[] DeserializeTenantStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            return JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// routes tenant customer inline callbacks through service selection, plan selection, payment creation, and manual checks.
    /// </summary>
    /// <param name="botClient">tenant Bot client.</param>
    /// <param name="CallbackQuery">Incoming customer callback.</param>
    /// <param name="customer">Shared customer profile.</param>
    /// <param name="User">
    /// Bot-scoped state for the customer that clicked the inline button. Tenant-specific callbacks use it only
    /// for isolation; non-tenant XUI callbacks are delegated before this method is called.
    /// </param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <remarks>
    /// Only callbacks with the tenant customer prefix are processed here. Shared account-management callbacks
    /// are routed to <see cref="XuiV3BotFlowService" /> by the update dispatcher so tenant storefront buttons can
    /// reuse the existing account details, search, renewal, and comment logic.
    /// </remarks>
    private async Task HANDLECUSTOMERCALLBACKASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        CredUser customer,
        User User,
        CancellationToken CancellationToken)
    {
        var tenant = await GetCurrentTenantBotAsync(CancellationToken);
        if (tenant == null || !tenant.Enabled)
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "فروشگاه غیرفعال است.", showAlert: true, cancellationToken: CancellationToken);
            return;
        }

        var action = CallbackQuery.Data[CUSTOMERCALLBACKPREFIX.Length..];
        var ChatId = CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id;
        var MessageId = CallbackQuery.Message?.MessageId;

        if (action == "joincheck")
        {
            var joined = await EnsureTenantCustomerJoinAsync(
                botClient,
                ChatId,
                CallbackQuery.From.Id,
                tenant,
                CancellationToken,
                isJoinRetry: true);
            if (joined)
            {
                await SendTenantHomeAsync(botClient, ChatId, tenant, CancellationToken);
                await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "عضویت شما تایید شد.", cancellationToken: CancellationToken);
            }
            else
            {
                await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "هنوز عضو کانال نشده‌اید.", showAlert: true, cancellationToken: CancellationToken);
            }

            return;
        }

        if (!await EnsureTenantCustomerJoinAsync(botClient, ChatId, CallbackQuery.From.Id, tenant, CancellationToken))
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "ابتدا در کانال عضو شوید.", showAlert: true, cancellationToken: CancellationToken);
            return;
        }

        if (action == "home")
        {
            await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
            await SendTenantHomeAsync(botClient, ChatId, tenant, CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (action == "services")
        {
            await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
            await SendServiceSelectionAsync(botClient, ChatId, tenant, CancellationToken, MessageId);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (action.StartsWith("svc:", StringComparison.Ordinal))
        {
            await SHOWSERVICEOPTIONSASYNC(botClient, ChatId, MessageId, tenant, action["svc:".Length..], CallbackQuery.From.Id, CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (action.StartsWith("GB:", StringComparison.Ordinal))
        {
            var parts = action.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[2], out var GB))
            {
                var service = _purchaseService.GetEnabledServices().FirstOrDefault(x => string.Equals(x.Key, parts[1], StringComparison.OrdinalIgnoreCase));
                if (service == null || !XuiV3PurchaseService.MeetsMinimumTraffic(service, GB))
                {
                    await EDITORSENDASYNC(
                        botClient,
                        ChatId,
                        MessageId,
                        BuildTenantMinimumTrafficMessage(service),
                        service == null ? null : BuildTenantTrafficInlineKeyboard(service),
                        CancellationToken);
                }
                else
                {
                    await _userDbcontext.SaveUserStatus(new User
                    {
                        Id = CallbackQuery.From.Id,
                        Flow = TENANTPURCHASEFLOW,
                        LastStep = TENANTPURCHASESTEPDURATION,
                        SelectedCountry = service.Key,
                        TotoalGB = GB.ToString(CultureInfo.InvariantCulture)
                    });
                    await SHOWDURATIONOPTIONSASYNC(botClient, ChatId, MessageId, tenant, parts[1], GB, CancellationToken);
                }
            }

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (action.StartsWith("dur:", StringComparison.Ordinal))
        {
            var parts = action.Split(':');
            if (parts.Length == 4 && int.TryParse(parts[2], out var GB))
            {
                var service = _purchaseService.GetEnabledServices().FirstOrDefault(x => string.Equals(x.Key, parts[1], StringComparison.OrdinalIgnoreCase));
                if (service == null || !XuiV3PurchaseService.MeetsMinimumTraffic(service, GB))
                {
                    await EDITORSENDASYNC(
                        botClient,
                        ChatId,
                        MessageId,
                        BuildTenantMinimumTrafficMessage(service),
                        service == null ? null : BuildTenantTrafficInlineKeyboard(service),
                        CancellationToken);
                    await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
                    return;
                }

                var selection = new XuiV3PurchaseSelection { ServiceKey = parts[1], TrafficGb = GB, DurationKey = parts[3] };
                await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
                await SHOWCUSTOMERCONFIRMASYNC(botClient, ChatId, MessageId, tenant, selection, CancellationToken);
            }

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (action.StartsWith("upl:", StringComparison.Ordinal))
        {
            var parts = action.Split(':');
            if (parts.Length == 3)
            {
                var selection = new XuiV3PurchaseSelection { ServiceKey = parts[1], UnlimitedPlanKey = parts[2] };
                await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
                await SHOWCUSTOMERCONFIRMASYNC(botClient, ChatId, MessageId, tenant, selection, CancellationToken);
            }

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (action.StartsWith("Pay:", StringComparison.Ordinal))
        {
            var selection = PARSESELECTIONFROMPAYACTION(action);
            if (selection == null)
            {
                await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "سفارش نامعتبر است.", showAlert: true, cancellationToken: CancellationToken);
                return;
            }

            await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
            await CreateTenantOrderINVOICEASYNC(botClient, CallbackQuery, tenant, customer, selection, CancellationToken);
            return;
        }

        if (action.StartsWith("PAYHP:", StringComparison.Ordinal) ||
            action.StartsWith("PAYNP:", StringComparison.Ordinal) ||
            action.StartsWith("PAYCARD:", StringComparison.Ordinal))
        {
            var Provider = action.Split(':', 2)[0];
            var PAYACTION = action[(Provider.Length + 1)..];
            var selection = PARSESELECTIONFROMPAYACTION(PAYACTION);
            if (selection == null)
            {
                await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "سفارش نامعتبر است.", showAlert: true, cancellationToken: CancellationToken);
                return;
            }

            if (Provider == "PAYHP")
            {
                await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
                await CreateTenantOrderINVOICEASYNC(botClient, CallbackQuery, tenant, customer, selection, CancellationToken);
            }
            else if (Provider == "PAYNP")
            {
                await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
                await CreateTenantNowPaymentsInvoiceAsync(botClient, CallbackQuery, tenant, customer, selection, CancellationToken);
            }
            else
            {
                await _userDbcontext.ClearUserStatus(new User { Id = CallbackQuery.From.Id });
                await CreateTenantCardOrderAsync(botClient, CallbackQuery, tenant, customer, selection, CancellationToken);
            }
            return;
        }

        if (action.StartsWith("RNHP:", StringComparison.Ordinal) ||
            action.StartsWith("RNNP:", StringComparison.Ordinal) ||
            action.StartsWith("RNCARD:", StringComparison.Ordinal))
        {
            var provider = action.Split(':', 2)[0];
            var idText = action[(provider.Length + 1)..];
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderDbId))
            {
                await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "سفارش تمدید نامعتبر است.", showAlert: true, cancellationToken: CancellationToken);
                return;
            }

            if (provider == "RNHP")
                await CreateTenantHooshPayInvoiceForExistingOrderAsync(botClient, CallbackQuery, tenant, customer, orderDbId, CancellationToken);
            else if (provider == "RNNP")
                await CreateTenantNowPaymentsInvoiceForExistingOrderAsync(botClient, CallbackQuery, tenant, customer, orderDbId, CancellationToken);
            else
                await ActivateTenantCardPaymentForExistingOrderAsync(botClient, CallbackQuery, tenant, customer, orderDbId, CancellationToken);
            return;
        }

        if (action.StartsWith("CHK:", StringComparison.OrdinalIgnoreCase))
        {
            var idText = action["CHK:".Length..];
            if (int.TryParse(idText, out var OrderId))
                await CheckTenantOrderAsync(botClient, ChatId, OrderId, customer.TelegramUserId, CancellationToken);

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "وضعیت بررسی شد.", cancellationToken: CancellationToken);
            return;
        }

        if (action.StartsWith("receipt:", StringComparison.Ordinal))
        {
            var idText = action["receipt:".Length..];
            if (int.TryParse(idText, out var OrderId))
                await PromptTenantReceiptUploadAsync(botClient, ChatId, OrderId, customer.TelegramUserId, CancellationToken);

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "رسید را به صورت عکس ارسال کنید.", cancellationToken: CancellationToken);
            return;
        }
    }

    /// <summary>
    /// sends the tenant storefront home Message and Reply keyboard.
    /// </summary>
    /// <param name="botClient">tenant Bot client.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="tenant">current tenant Bot row.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    private async Task SendTenantHomeAsync(ITelegramBotClient botClient, ChatId ChatId, BotInstance tenant, CancellationToken CancellationToken)
    {
        var WELCOME = string.IsNullOrWhiteSpace(tenant.TenantWelcomeText)
            ? $"به فروشگاه {tenant.BrandName ?? tenant.Username} خوش آمدید."
            : tenant.TenantWelcomeText;

        await botClient.SendTextMessageAsync(
            chatId: ChatId,
            text: $"{Html(WELCOME)}\n\nبرای خرید اکانت یا دیدن تعرفه‌ها از دکمه‌های پایین استفاده کنید.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantReplyKeyboard(),
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Builds the SMALL Reply keyboard shown to tenant customers.
    /// </summary>
    /// <returns>Reply keyboard with purchase, TARIFFS, and support buttons.</returns>
    private static ReplyKeyboardMarkup BuildTenantReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "💳 خرید اکانت", "📋 تعرفه‌ها" },
            new KeyboardButton[] { "اکانت‌های من", "🔄 تمدید اکانت" },
            new KeyboardButton[] { "جستجوی اکانت", "راهنما نصب" },
            new KeyboardButton[] { "💬 پشتیبانی" }
        })
        {
            ResizeKeyboard = true
        };
    }

    /// <summary>
    /// Builds a clickable Telegram support contact for tenant storefront customers.
    /// </summary>
    /// <param name="supportAccount">
    /// Tenant-scoped support account configured by the colleague owner. The value may be a raw username,
    /// an <c>@username</c>, a public <c>https://t.me/username</c> link, or a <c>t.me/username</c> value without
    /// a scheme. Empty values mean no support account has been configured.
    /// </param>
    /// <returns>
    /// HTML-safe text for a Telegram message. Valid Telegram usernames are returned as clickable
    /// <c>https://t.me/...</c> links with visible <c>@username</c> text; unsupported values are escaped and
    /// displayed as plain text.
    /// </returns>
    /// <remarks>
    /// The tenant support message is sent with <see cref="ParseMode.Html"/>. This helper keeps owner-provided
    /// text out of raw HTML while making normal Telegram usernames clickable even when the client does not
    /// auto-link plain <c>@username</c> text.
    /// </remarks>
    private static string BuildTenantSupportContactHtml(string supportAccount)
    {
        if (string.IsNullOrWhiteSpace(supportAccount))
            return "ثبت نشده";

        var normalized = NormalizeTenantSupportAccount(supportAccount);
        if (string.IsNullOrWhiteSpace(normalized))
            return Html(supportAccount.Trim());

        var username = normalized.TrimStart('@');
        var safeUsername = Html(username);
        return $"<a href=\"https://t.me/{safeUsername}\">@{safeUsername}</a>";
    }

    /// <summary>
    /// Normalizes a tenant support contact into a Telegram username with one leading <c>@</c>.
    /// </summary>
    /// <param name="supportAccount">
    /// Owner-provided support contact. The value may be <c>@username</c>, <c>username</c>,
    /// <c>https://t.me/username</c>, <c>t.me/username</c>, or <c>telegram.me/username</c>.
    /// </param>
    /// <returns>
    /// A normalized <c>@username</c> when the input contains a valid public Telegram username; otherwise <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Tenant support is customer-facing. Storing the canonical username avoids rendering broken values such as
    /// <c>@https://t.me/name</c> later in support messages, owner panels, and logs.
    /// </remarks>
    private static string NormalizeTenantSupportAccount(string supportAccount)
    {
        if (string.IsNullOrWhiteSpace(supportAccount))
            return null;

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
            normalized = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        }

        var username = normalized.Trim().TrimStart('@');
        return IsTelegramUsername(username) ? "@" + username : null;
    }

    /// <summary>
    /// Checks whether a value can be used as a public Telegram username link.
    /// </summary>
    /// <param name="username">
    /// Username without the leading <c>@</c>. It must contain only ASCII letters, digits, or underscores.
    /// </param>
    /// <returns>
    /// <c>true</c> when the value satisfies Telegram's public username shape closely enough to build a
    /// clickable <c>t.me</c> link; otherwise <c>false</c>.
    /// </returns>
    private static bool IsTelegramUsername(string username)
    {
        return !string.IsNullOrWhiteSpace(username) &&
               username.Length is >= 5 and <= 32 &&
               username.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>
    /// sends or edits the first storefront purchase step where the customer CHOOSES A service.
    /// </summary>
    /// <param name="botClient">tenant Bot client.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="tenant">current tenant Bot row.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    /// <param name="MessageId">optional Message Id to edit.</param>
    private async Task SendServiceSelectionAsync(
        ITelegramBotClient botClient,
        ChatId ChatId,
        BotInstance tenant,
        CancellationToken CancellationToken,
        int? MessageId = null)
    {
        var keyboard = BUILDTENANTSERVICEKEYBOARD();
        var Text = "نوع سرویس مورد نظر را انتخاب کنید:";
        if (MessageId.HasValue)
        {
            await SafeEditMessageTextAsync(botClient, ChatId, MessageId.Value, Text, replyMarkup: keyboard, cancellationToken: CancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(ChatId, Text, replyMarkup: keyboard, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Builds inline buttons for all enabled xui v3 service definitions.
    /// </summary>
    /// <returns>inline keyboard where each button POINTS to A tenant service callback.</returns>
    private InlineKeyboardMarkup BUILDTENANTSERVICEKEYBOARD()
    {
        var rows = _purchaseService.GetEnabledServices()
            .Select(service => new[]
            {
                InlineKeyboardButton.WithCallbackData(service.DisplayName, CUSTOMERCALLBACKPREFIX + "svc:" + service.Key)
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", CUSTOMERCALLBACKPREFIX + "home") })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// SHOWS Traffic Options for Metered services or Unlimited plan Options for Unlimited services.
    /// </summary>
    /// <param name="botClient">tenant Bot client.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="MessageId">optional Message Id to edit.</param>
    /// <param name="tenant">current tenant Bot row.</param>
    /// <param name="ServiceKey">selected xui service key.</param>
    /// <param name="CustomerTelegramUserId">
    /// Numeric Telegram user id of the tenant customer. This is stored in users.db state so a typed traffic
    /// message can continue the same purchase flow after the service callback.
    /// </param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    private async Task SHOWSERVICEOPTIONSASYNC(
        ITelegramBotClient botClient,
        ChatId ChatId,
        int? MessageId,
        BotInstance tenant,
        string ServiceKey,
        long CustomerTelegramUserId,
        CancellationToken CancellationToken)
    {
        var service = _purchaseService.GetEnabledServices().FirstOrDefault(x => x.Key == ServiceKey);
        if (service == null)
            return;

        if (service.IsUnlimited)
        {
            await _userDbcontext.ClearUserStatus(new User { Id = CustomerTelegramUserId });
            var rows = service.UnlimitedPlans
                .Where(x => x.IsEnabled)
                .Select(plan =>
                {
                    var selection = new XuiV3PurchaseSelection { ServiceKey = service.Key, UnlimitedPlanKey = plan.Key };
                    var Price = CalculateTenantPrice(tenant, selection).SalePriceToman;
                    return new[]
                    {
                        InlineKeyboardButton.WithCallbackData(
                            $"{plan.DisplayName} - {Price.FormatCurrency()}",
                            CUSTOMERCALLBACKPREFIX + $"upl:{service.Key}:{plan.Key}")
                    };
                })
                .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", CUSTOMERCALLBACKPREFIX + "services") })
                .ToArray();

            await EDITORSENDASYNC(botClient, ChatId, MessageId, "پلن مورد نظر را انتخاب کنید:", new InlineKeyboardMarkup(rows), CancellationToken);
            return;
        }

        await _userDbcontext.SaveUserStatus(new User
        {
            Id = CustomerTelegramUserId,
            Flow = TENANTPURCHASEFLOW,
            LastStep = TENANTPURCHASESTEPTRAFFIC,
            SelectedCountry = service.Key
        });

        await EDITORSENDASYNC(
            botClient,
            ChatId,
            MessageId,
            $"حجم مورد نظر را انتخاب کنید یا حجم دلخواه را به GB وارد کنید.\nحداقل حجم این سرویس {XuiV3PurchaseService.GetMinimumTrafficGb(service)} GB است.",
            BuildTenantTrafficInlineKeyboard(service),
            CancellationToken);
    }

    /// <summary>
    /// Shows the duration choices for a tenant customer's metered purchase after traffic has been selected.
    /// </summary>
    /// <param name="botClient">
    /// Telegram client for the tenant storefront that is serving the customer.
    /// </param>
    /// <param name="ChatId">
    /// Telegram chat id where the customer is choosing the plan duration.
    /// </param>
    /// <param name="MessageId">
    /// Optional Telegram message id to edit. When <c>null</c>, a new message is sent instead.
    /// </param>
    /// <param name="tenant">
    /// Tenant bot instance that owns the storefront, pricing markup, and payment settings for this purchase.
    /// </param>
    /// <param name="ServiceKey">
    /// Service key from <c>xui-v3-service-plans.json</c>, such as <c>normal</c> or <c>national</c>.
    /// </param>
    /// <param name="TrafficGb">
    /// Customer-selected metered traffic in GB. The value must satisfy the selected service's
    /// <c>minimumTrafficGb</c> policy before duration buttons are shown.
    /// </param>
    /// <param name="CancellationToken">
    /// Cancellation token for Telegram and state-validation operations.
    /// </param>
    /// <remarks>
    /// This method is shared by callback traffic buttons and manually typed traffic in tenant bots.
    /// It rechecks the minimum traffic here so stale callback data or edited messages cannot bypass the
    /// same pricing policy used by owned bots.
    /// </remarks>
    private async Task SHOWDURATIONOPTIONSASYNC(ITelegramBotClient botClient, ChatId ChatId, int? MessageId, BotInstance tenant, string ServiceKey, int TrafficGb, CancellationToken CancellationToken)
    {
        var service = _purchaseService.GetEnabledServices().FirstOrDefault(x => x.Key == ServiceKey);
        if (service == null)
            return;

        if (!XuiV3PurchaseService.MeetsMinimumTraffic(service, TrafficGb))
        {
            await EDITORSENDASYNC(
                botClient,
                ChatId,
                MessageId,
                BuildTenantMinimumTrafficMessage(service),
                BuildTenantTrafficInlineKeyboard(service),
                CancellationToken);
            return;
        }

        var rows = service.DurationOptions
            .Select(Duration =>
            {
                var selection = new XuiV3PurchaseSelection { ServiceKey = ServiceKey, TrafficGb = TrafficGb, DurationKey = Duration.Key };
                var Price = CalculateTenantPrice(tenant, selection).SalePriceToman;
                return new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"{Duration.DisplayName} - {Price.FormatCurrency()}",
                        CUSTOMERCALLBACKPREFIX + $"dur:{ServiceKey}:{TrafficGb}:{Duration.Key}")
                };
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", CUSTOMERCALLBACKPREFIX + $"svc:{ServiceKey}") })
            .ToArray();

        await EDITORSENDASYNC(botClient, ChatId, MessageId, "مدت سرویس را انتخاب کنید:", new InlineKeyboardMarkup(rows), CancellationToken);
    }

    /// <summary>
    /// SHOWS the tenant customer pre-invoice summary and payment confirmation button.
    /// </summary>
    /// <param name="botClient">tenant Bot client.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="MessageId">optional Message Id to edit.</param>
    /// <param name="tenant">current tenant Bot row.</param>
    /// <param name="selection">selected xui purchase OPTION.</param>
    /// <param name="CancellationToken">Cancellation Token.</param>
    private async Task SHOWCUSTOMERCONFIRMASYNC(ITelegramBotClient botClient, ChatId ChatId, int? MessageId, BotInstance tenant, XuiV3PurchaseSelection selection, CancellationToken CancellationToken)
    {
        var Price = CalculateTenantPrice(tenant, selection);
        var resolved = _purchaseService.ResolvePurchase(selection, false);
        var Text = "📌 <b>پیش‌فاکتور خرید</b>\n\n" +
                   $"سرویس: <b>{Html(resolved.Service.DisplayName)}</b>\n" +
                   (resolved.IsUnlimited
                       ? $"حد مصرف منصفانه: <code>{resolved.TrafficGb} GB</code>\n"
                       : $"حجم: <code>{resolved.TrafficGb} GB</code>\n") +
                   $"مدت: <code>{(resolved.DurationDays <= 0 ? "نامحدود" : resolved.DurationDays + " روز")}</code>\n" +
                   $"مبلغ قابل پرداخت: <b>{Html(Price.SalePriceToman.FormatCurrency())}</b>\n\n" +
                   "پس از پرداخت موفق، اکانت به صورت خودکار ساخته و ارسال می‌شود.";

        var PAYMENTROWS = new List<InlineKeyboardButton[]>();
        if (tenant.TenantHooshPayEnabled)
            PAYMENTROWS.Add(new[] { InlineKeyboardButton.WithCallbackData("پرداخت ریالی هوش‌پی", CUSTOMERCALLBACKPREFIX + "PAYHP:" + BUILDPAYACTION(selection)) });
        if (tenant.TenantNowPaymentsEnabled)
            PAYMENTROWS.Add(new[] { InlineKeyboardButton.WithCallbackData("پرداخت ارز دیجیتال", CUSTOMERCALLBACKPREFIX + "PAYNP:" + BUILDPAYACTION(selection)) });
        if (tenant.TenantCardPaymentEnabled && !string.IsNullOrWhiteSpace(tenant.TenantCardNumber))
            PAYMENTROWS.Add(new[] { InlineKeyboardButton.WithCallbackData("کارت‌به‌کارت به فروشگاه", CUSTOMERCALLBACKPREFIX + "PAYCARD:" + BUILDPAYACTION(selection)) });
        PAYMENTROWS.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", CUSTOMERCALLBACKPREFIX + "services") });

        await EDITORSENDASYNC(
            botClient,
            ChatId,
            MessageId,
            Text,
            new InlineKeyboardMarkup(PAYMENTROWS),
            CancellationToken,
            ParseMode.Html);
    }

    /// <summary>
    /// creates the local tenant order, creates the LINKED HooshPay invoice, stores both records,
    /// and sends the payment CONTROLS to the tenant customer.
    /// </summary>
    /// <param name="botClient">Telegram client for the tenant storefront Bot that is SERVING the customer.</param>
    /// <param name="CallbackQuery">callback that Confirmed the tenant customer's selected plan.</param>
    /// <param name="tenant">tenant Bot definition and PRICING settings owned by the colleague.</param>
    /// <param name="customer">Credential User record for the customer BUYING from the tenant storefront.</param>
    /// <param name="selection">resolved XuiV3 service, Traffic, Duration, or Unlimited-plan selection.</param>
    /// <param name="CancellationToken">Cancellation Token PROPAGATED from the Telegram update handler.</param>
    private async Task CreateTenantOrderINVOICEASYNC(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        BotInstance tenant,
        CredUser customer,
        XuiV3PurchaseSelection selection,
        CancellationToken CancellationToken)
    {
        var ChatId = CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id;
        var Price = CalculateTenantPrice(tenant, selection);
        var OrderId = CreateTenantOrderId(tenant, customer.TelegramUserId);

        // the local order is created before the invoice so ipn can be matched EVEN if the User leaves Telegram.
        var order = new TenantBotOrder
        {
            OrderId = OrderId,
            TenantBotId = tenant.Id,
            TenantBotUsername = tenant.Username,
            OwnerTelegramUserId = tenant.OwnerTelegramUserId ?? 0,
            CustomerTelegramUserId = customer.TelegramUserId,
            CustomerChatId = ChatId,
            CustomerUsername = customer.Username,
            CustomerFirstName = customer.FirstName,
            CustomerLastName = customer.LastName,
            OrderKind = TenantBotOrderKinds.Purchase,
            ServiceKey = selection.ServiceKey,
            TrafficGb = selection.TrafficGb,
            DurationKey = selection.DurationKey,
            UnlimitedPlanKey = selection.UnlimitedPlanKey,
            AccountCount = 1,
            SalePriceToman = Price.SalePriceToman,
            BaseCostToman = Price.BaseCostToman,
            ProfitToman = Price.ProfitToman,
            PaymentStatus = TenantBotOrderStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        _userDbcontext.TenantBotOrders.Add(order);
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        var payment = new HooshPayPaymentInfo
        {
            OrderId = order.OrderId,
            AmountToman = order.SalePriceToman,
            FeeMode = HooshPayFeeModes.Buyer,
            IpnCallbackUrl = _appConfig.HooshPayIpnUrl,
            ReturnUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_success"),
            TelegramUserId = customer.TelegramUserId,
            ChatId = ChatId,
            BotId = tenant.Id,
            BotUsername = tenant.Username,
            PaymentPurpose = TenantBotPaymentPurposes.TenantOrder,
            TenantBotOrderId = order.Id,
            TenantOwnerTelegramUserId = tenant.OwnerTelegramUserId,
            PaymentStatus = HooshPayStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        // Purpose marks this HooshPay row as A direct sale, not A wallet top-Up.
        _userDbcontext.HooshPayPaymentInfos.Add(payment);
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        order.HooshPayPaymentInfoId = payment.Id;
        payment.RawRequestJson = JsonConvert.SerializeObject(new
        {
            amount = payment.AmountToman,
            fee_mode = HooshPayFeeModes.Buyer,
            Description = $"tenant Bot order {order.OrderId}",
            order_id = order.OrderId,
            callback_url = payment.IpnCallbackUrl,
            return_url = payment.ReturnUrl
        });

        try
        {
            var invoice = await _hooshPay.CreateInvoiceAsync(
                payment.AmountToman,
                payment.OrderId,
                $"tenant Bot order {order.OrderId}",
                payment.IpnCallbackUrl,
                payment.ReturnUrl,
                CancellationToken);

            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            payment.Apply(invoice?.data);
            order.HooshPayInvoiceUid = payment.InvoiceUid;
            order.PaymentUrl = payment.PaymentUrl;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(CancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: ChatId,
                text: BuildTenantPaymentText(order, payment),
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, payment),
                cancellationToken: CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "فاکتور پرداخت ساخته شد.", cancellationToken: CancellationToken);
        }
        catch (Exception ex)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = ex.Message;
            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "ساخت فاکتور ناموفق بود.", showAlert: true, cancellationToken: CancellationToken);
            await botClient.SendTextMessageAsync(ChatId, "ساخت فاکتور پرداخت ناموفق بود. لطفاً بعداً دوباره تلاش کنید.", cancellationToken: CancellationToken);
        }
    }

    /// <summary>
    /// creates A tenant order invoice through NowPayments and stores the crypto invoice metadata in users.db.
    /// </summary>
    /// <param name="botClient">tenant storefront Bot client that will Send the invoice link to the customer.</param>
    /// <param name="CallbackQuery">customer callback that selected crypto payment for A PREPARED plan.</param>
    /// <param name="tenant">tenant Bot that owns the storefront and Receives profit after settlement.</param>
    /// <param name="customer">credentials profile of the tenant customer.</param>
    /// <param name="selection">selected service, Traffic, Duration, or Unlimited plan.</param>
    /// <param name="CancellationToken">Cancellation Token for NowPayments, users.db, and Telegram calls.</param>
    /// <remarks>
    /// the created <see cref="SwapinoPaymentInfo" /> is marked with <see cref="TenantBotPaymentPurposes.TenantOrder" />
    /// so the NowPayments ipn endpoint routes paid invoices to tenant fulfillment instead of wallet top-Up settlement.
    /// </remarks>
    private async Task CreateTenantNowPaymentsInvoiceAsync(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        BotInstance tenant,
        CredUser customer,
        XuiV3PurchaseSelection selection,
        CancellationToken CancellationToken)
    {
        var ChatId = CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id;
        var Price = CalculateTenantPrice(tenant, selection);
        var order = CreateTenantOrder(tenant, customer, ChatId, selection, Price, "NowPayments");

        _userDbcontext.TenantBotOrders.Add(order);
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        var payment = SwapinoPaymentInfo.CreateCryptoCharge(
            customer.TelegramUserId,
            order.SalePriceToman,
            _appConfig.NowpaymentIpnUrl,
            ChatId,
            _appConfig.NowpaymentPayCurrency);
        payment.OrderId = order.OrderId;
        payment.SuccessUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_success");
        payment.CancelUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_cancel");
        payment.BotId = tenant.Id;
        payment.BotUsername = tenant.Username;
        payment.PaymentPurpose = TenantBotPaymentPurposes.TenantOrder;
        payment.TenantBotOrderId = order.Id;
        payment.TenantOwnerTelegramUserId = tenant.OwnerTelegramUserId;

        _userDbcontext.SwapinoPaymentInfos.Add(payment);
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        try
        {
            var invoice = await _nowPayments.CreateInvoiceAsync(
                order.SalePriceToman,
                order.OrderId,
                $"tenant Bot order {order.OrderId}",
                successUrl: payment.SuccessUrl,
                cancelUrl: payment.CancelUrl,
                cancellationToken: CancellationToken);
            var Data = NowPaymentsPaymentRecordData.FromInvoiceResponse(invoice);
            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            payment.SetNowPaymentsData(Data);
            order.NowPaymentsPaymentInfoId = payment.Id;
            order.PaymentUrl = payment.InvoiceUrl;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(CancellationToken);

            await botClient.SendTextMessageAsync(
                ChatId,
                BUILDTENANTGATEWAYPAYMENTTEXT(order, "ارز دیجیتال"),
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, payment.InvoiceUrl),
                cancellationToken: CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "فاکتور پرداخت ساخته شد.", cancellationToken: CancellationToken);
        }
        catch (Exception ex)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = ex.Message;
            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "ساخت فاکتور ناموفق بود.", showAlert: true, cancellationToken: CancellationToken);
        }
    }

    /// <summary>
    /// creates A tenant card-to-card order and asks the customer to Send A payment receipt photo.
    /// </summary>
    /// <param name="botClient">tenant storefront Bot client used to Send card INSTRUCTIONS.</param>
    /// <param name="CallbackQuery">customer callback that selected card-to-card payment.</param>
    /// <param name="tenant">tenant Bot whose owner Receives card-to-card money directly.</param>
    /// <param name="customer">credentials profile of the tenant customer.</param>
    /// <param name="selection">selected service, Traffic, Duration, or Unlimited plan.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db and Telegram calls.</param>
    /// <remarks>
    /// the customer PAYS the owner outside the PLATFORM. after the sales assistant CONFIRMS the receipt,
    /// fulfillment debits the tenant owner's base cost from the Shared wallet and may LEAVE the balance negative.
    /// </remarks>
    private async Task CreateTenantCardOrderAsync(
        ITelegramBotClient botClient,
        CallbackQuery CallbackQuery,
        BotInstance tenant,
        CredUser customer,
        XuiV3PurchaseSelection selection,
        CancellationToken CancellationToken)
    {
        var ChatId = CallbackQuery.Message?.Chat.Id ?? CallbackQuery.From.Id;
        var Price = CalculateTenantPrice(tenant, selection);
        var order = CreateTenantOrder(tenant, customer, ChatId, selection, Price, "tenant_card");
        order.PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt;
        order.UpdatedAtUtc = DateTime.UtcNow;

        _userDbcontext.TenantBotOrders.Add(order);
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        await botClient.SendTextMessageAsync(
            ChatId,
            "💳 <b>پرداخت کارت‌به‌کارت فروشگاه</b>\n\n" +
            $"مبلغ دقیق: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"شماره کارت: <code>{Html(tenant.TenantCardNumber)}</code>\n" +
            $"نام صاحب کارت: <b>{Html(tenant.TenantCardHolderName)}</b>\n" +
            $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n\n" +
            "بعد از پرداخت، عکس رسید را همینجا ارسال کنید تا همکار آن را تایید کند.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantCardPaymentKeyboard(order),
            cancellationToken: CancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "سفارش کارت‌به‌کارت ثبت شد.", cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Creates a HooshPay invoice for an existing tenant renewal order.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send the invoice to the customer.</param>
    /// <param name="callbackQuery">Customer callback that selected HooshPay for renewal.</param>
    /// <param name="tenant">Tenant bot that owns the order.</param>
    /// <param name="customer">Customer who owns the order.</param>
    /// <param name="orderDbId">Internal users.db id of the pending renewal order.</param>
    /// <param name="cancellationToken">Cancellation token for users.db, HooshPay, and Telegram operations.</param>
    private async Task CreateTenantHooshPayInvoiceForExistingOrderAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        BotInstance tenant,
        CredUser customer,
        int orderDbId,
        CancellationToken cancellationToken)
    {
        var order = await GetPendingTenantRenewOrderAsync(orderDbId, tenant, customer, cancellationToken);
        if (order == null)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "سفارش تمدید پیدا نشد یا قبلاً پردازش شده است.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        order.PaymentProvider = "HooshPay";
        order.UpdatedAtUtc = DateTime.UtcNow;
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        var payment = new HooshPayPaymentInfo
        {
            OrderId = order.OrderId,
            AmountToman = order.SalePriceToman,
            FeeMode = HooshPayFeeModes.Buyer,
            IpnCallbackUrl = _appConfig.HooshPayIpnUrl,
            ReturnUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_success"),
            TelegramUserId = customer.TelegramUserId,
            ChatId = chatId,
            BotId = tenant.Id,
            BotUsername = tenant.Username,
            PaymentPurpose = TenantBotPaymentPurposes.TenantOrder,
            TenantBotOrderId = order.Id,
            TenantOwnerTelegramUserId = tenant.OwnerTelegramUserId,
            PaymentStatus = HooshPayStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        _userDbcontext.HooshPayPaymentInfos.Add(payment);
        await _userDbcontext.SaveChangesAsync(cancellationToken);

        order.HooshPayPaymentInfoId = payment.Id;
        payment.RawRequestJson = JsonConvert.SerializeObject(new
        {
            amount = payment.AmountToman,
            fee_mode = HooshPayFeeModes.Buyer,
            description = $"tenant renewal order {order.OrderId}",
            order_id = order.OrderId,
            callback_url = payment.IpnCallbackUrl,
            return_url = payment.ReturnUrl
        });

        try
        {
            var invoice = await _hooshPay.CreateInvoiceAsync(
                payment.AmountToman,
                payment.OrderId,
                $"tenant renewal order {order.OrderId}",
                payment.IpnCallbackUrl,
                payment.ReturnUrl,
                cancellationToken);

            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            payment.Apply(invoice?.data);
            order.HooshPayInvoiceUid = payment.InvoiceUid;
            order.PaymentUrl = payment.PaymentUrl;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId,
                BuildTenantPaymentText(order, payment),
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, payment),
                cancellationToken: cancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "فاکتور پرداخت ساخته شد.", cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = ex.Message;
            payment.ErrorMessage = ex.Message;
            order.UpdatedAtUtc = DateTime.UtcNow;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(cancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "ساخت فاکتور ناموفق بود.", showAlert: true, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Creates a NOWPayments invoice for an existing tenant renewal order.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send the invoice link.</param>
    /// <param name="callbackQuery">Customer callback that selected crypto payment.</param>
    /// <param name="tenant">Tenant bot that owns the order.</param>
    /// <param name="customer">Customer who owns the order.</param>
    /// <param name="orderDbId">Internal users.db id of the pending renewal order.</param>
    /// <param name="cancellationToken">Cancellation token for database, gateway, and Telegram calls.</param>
    private async Task CreateTenantNowPaymentsInvoiceForExistingOrderAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        BotInstance tenant,
        CredUser customer,
        int orderDbId,
        CancellationToken cancellationToken)
    {
        var order = await GetPendingTenantRenewOrderAsync(orderDbId, tenant, customer, cancellationToken);
        if (order == null)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "سفارش تمدید پیدا نشد یا قبلاً پردازش شده است.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        order.PaymentProvider = "NowPayments";
        order.UpdatedAtUtc = DateTime.UtcNow;
        var payment = SwapinoPaymentInfo.CreateCryptoCharge(
            customer.TelegramUserId,
            order.SalePriceToman,
            _appConfig.NowpaymentIpnUrl,
            chatId,
            _appConfig.NowpaymentPayCurrency);
        payment.OrderId = order.OrderId;
        payment.SuccessUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_success");
        payment.CancelUrl = _botRegistry.GetById(tenant.Id).BuildTelegramStartUrl("payment_cancel");
        payment.BotId = tenant.Id;
        payment.BotUsername = tenant.Username;
        payment.PaymentPurpose = TenantBotPaymentPurposes.TenantOrder;
        payment.TenantBotOrderId = order.Id;
        payment.TenantOwnerTelegramUserId = tenant.OwnerTelegramUserId;
        _userDbcontext.SwapinoPaymentInfos.Add(payment);
        await _userDbcontext.SaveChangesAsync(cancellationToken);

        try
        {
            var invoice = await _nowPayments.CreateInvoiceAsync(
                order.SalePriceToman,
                order.OrderId,
                $"tenant renewal order {order.OrderId}",
                successUrl: payment.SuccessUrl,
                cancelUrl: payment.CancelUrl,
                cancellationToken: cancellationToken);
            var data = NowPaymentsPaymentRecordData.FromInvoiceResponse(invoice);
            payment.RawResponseJson = JsonConvert.SerializeObject(invoice);
            payment.SetNowPaymentsData(data);
            order.NowPaymentsPaymentInfoId = payment.Id;
            order.PaymentUrl = payment.InvoiceUrl;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId,
                BUILDTENANTGATEWAYPAYMENTTEXT(order, "ارز دیجیتال"),
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, payment.InvoiceUrl),
                cancellationToken: cancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "فاکتور پرداخت ساخته شد.", cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = ex.Message;
            payment.ErrorMessage = ex.Message;
            order.UpdatedAtUtc = DateTime.UtcNow;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(cancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "ساخت فاکتور ناموفق بود.", showAlert: true, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Activates card-to-card payment for an existing tenant renewal order.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to send card instructions.</param>
    /// <param name="callbackQuery">Customer callback that selected tenant card payment.</param>
    /// <param name="tenant">Tenant bot containing card number and holder name.</param>
    /// <param name="customer">Customer who owns the order.</param>
    /// <param name="orderDbId">Internal users.db id of the pending renewal order.</param>
    /// <param name="cancellationToken">Cancellation token for users.db and Telegram operations.</param>
    private async Task ActivateTenantCardPaymentForExistingOrderAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        BotInstance tenant,
        CredUser customer,
        int orderDbId,
        CancellationToken cancellationToken)
    {
        var order = await GetPendingTenantRenewOrderAsync(orderDbId, tenant, customer, cancellationToken);
        if (order == null)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "سفارش تمدید پیدا نشد یا قبلاً پردازش شده است.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        order.PaymentProvider = "tenant_card";
        order.PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(cancellationToken);

        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        await botClient.SendTextMessageAsync(
            chatId,
            "💳 <b>پرداخت کارت‌به‌کارت تمدید</b>\n\n" +
            $"مبلغ دقیق: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"شماره کارت: <code>{Html(tenant.TenantCardNumber)}</code>\n" +
            $"نام صاحب کارت: <b>{Html(tenant.TenantCardHolderName)}</b>\n" +
            $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n\n" +
            "بعد از پرداخت، عکس رسید را همینجا ارسال کنید تا همکار آن را تایید کند.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantCardPaymentKeyboard(order),
            cancellationToken: cancellationToken);
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, "سفارش کارت‌به‌کارت ثبت شد.", cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Loads a pending tenant renewal order and verifies that it belongs to the current tenant customer.
    /// </summary>
    /// <param name="orderDbId">Internal users.db order id from callback data.</param>
    /// <param name="tenant">Current tenant bot.</param>
    /// <param name="customer">Current tenant customer.</param>
    /// <param name="cancellationToken">Cancellation token for the database lookup.</param>
    /// <returns>The pending renewal order, or <c>null</c> when not eligible for payment activation.</returns>
    private Task<TenantBotOrder> GetPendingTenantRenewOrderAsync(
        int orderDbId,
        BotInstance tenant,
        CredUser customer,
        CancellationToken cancellationToken)
    {
        return _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == orderDbId &&
                 x.TenantBotId == tenant.Id &&
                 x.CustomerTelegramUserId == customer.TelegramUserId &&
                 x.OrderKind == TenantBotOrderKinds.Renew &&
                 !x.IsFulfilled &&
                 (x.PaymentStatus == TenantBotOrderStatuses.Pending || x.PaymentStatus == TenantBotOrderStatuses.Failed),
            cancellationToken);
    }

    /// <summary>
    /// fulfills A paid tenant order exactly once: creates the XuiV3 account, credits the tenant owner
    /// with the profit, Writes the ledger row, NOTIFIES both PARTIES, and EMITS the operational log.
    /// </summary>
    /// <param name="payment">HooshPay payment row whose <c>PaymentPurpose</c> is A tenant order.</param>
    /// <param name="Source">settlement Source, for example <c>ipn</c> or <c>manual-check</c>.</param>
    /// <param name="CancellationToken">Cancellation Token for database, Telegram, and panel operations.</param>
    /// <returns>
    /// settlement status describing whether the order was applied, already fulfilled, or could not be completed.
    /// </returns>
    public async Task<NowPaymentsSettlementResult> ApplyPaidTenantOrderAsync(
        HooshPayPaymentInfo payment,
        string Source,
        CancellationToken CancellationToken = default)
    {
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == payment.TenantBotOrderId || x.OrderId == payment.OrderId,
            CancellationToken);
        if (order == null)
            return NowPaymentsSettlementResult.NotFound();

        return await FULFILLPAIDTENANTORDERASYNC(order, Source, payment, null, false, CancellationToken);

        if (await ISTENANTORDERALREADYFULFILLEDASYNC(order, CancellationToken))
            return NowPaymentsSettlementResult.AlreadyAdded(order.OwnerBalanceAfter ?? 0);

        // fulfillment is idempotent: paid orders Create one account and credit the owner once.
        order.PaymentStatus = TenantBotOrderStatuses.Paid;
        order.PaidAtUtc = DateTime.UtcNow;
        order.UpdatedAtUtc = DateTime.UtcNow;
        payment.PaymentStatus = HooshPayStatuses.Paid;
        payment.PaidAtUtc ??= DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        var TENANTCONFIG = _botRegistry.GetById(order.TenantBotId);
        var tenant = await _userDbcontext.BotInstances.FirstOrDefaultAsync(x => x.Id == order.TenantBotId, CancellationToken);
        var owner = await _credentialsDbContext.GetUserStatusWithId(order.OwnerTelegramUserId);
        var customer = await _credentialsDbContext.GetUserStatusWithId(order.CustomerTelegramUserId);
        if (owner == null || customer == null || tenant == null)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = "tenant owner or customer was not found.";
            await _userDbcontext.SaveChangesAsync(CancellationToken);
            return NowPaymentsSettlementResult.UserNotFound();
        }

        var selection = new XuiV3PurchaseSelection
        {
            ServiceKey = order.ServiceKey,
            TrafficGb = order.TrafficGb,
            DurationKey = order.DurationKey,
            UnlimitedPlanKey = order.UnlimitedPlanKey,
            AccountCount = 1
        };

        using (_botContextAccessor.Push(new BotRuntimeContext
        {
            Config = TENANTCONFIG,
            Client = _botClientProvider.GetClient(order.TenantBotId)
        }))
        {
            try
            {
                if (string.Equals(order.OrderKind, TenantBotOrderKinds.Renew, StringComparison.OrdinalIgnoreCase))
                    return await FULFILLPAIDTENANTRENEWORDERASYNC(order, owner, customer, tenant, selection, Source, false, CancellationToken);

                var created = await _purchaseService.CreateAccountAsync(
                    customer,
                    BuildConfiguredPanelServerInfo(),
                    selection,
                    _appConfig.XuiV3ApiBaseUrl,
                    CancellationToken,
                    new XuiV3AccountMetadataOptions
                    {
                        UserComment = $"tenant sale VIA @{tenant.Username}",
                        PriceTomanOverride = order.SalePriceToman,
                        CreatedByBotId = order.TenantBotId,
                        LastUpdatedByBotId = order.TenantBotId,
                        CreatedByTelegramUserId = order.CustomerTelegramUserId,
                        LastUpdatedByTelegramUserId = order.OwnerTelegramUserId,
                        LastAction = "tenant-Bot-sale",
                        SaveUserStatus = true
                    });

                if (!created.Success)
                {
                    var retryable = IsTenantFulfillmentTimeout(created.Message);
                    order.PaymentStatus = retryable
                        ? TenantBotOrderStatuses.ReceiptApproved
                        : TenantBotOrderStatuses.Failed;
                    order.ErrorMessage = created.Message;
                    order.UpdatedAtUtc = DateTime.UtcNow;
                    await _userDbcontext.SaveChangesAsync(CancellationToken);
                    if (retryable)
                    {
                        await NOTIFYTENANTCUSTOMERRETRYABLEFULFILLMENTASYNC(order, created.Message, CancellationToken);
                        LOGTENANTORDER(order, owner, customer, Source, "account-create-timeout-retryable");
                    }
                    else
                    {
                        await NOTIFYTENANTCUSTOMERFAILUREASYNC(order, created.Message, CancellationToken);
                        LOGTENANTORDER(order, owner, customer, Source, "account-Create-failed");
                    }

                    return NowPaymentsSettlementResult.InvalidAmount();
                }

                var beforeBalance = owner.AccountBalance;
                if (order.ProfitToman > 0)
                    await _credentialsDbContext.AddFund(order.OwnerTelegramUserId, order.ProfitToman);
                var afterBalance = await _credentialsDbContext.GetAccountBalance(order.OwnerTelegramUserId);

                order.IsFulfilled = true;
                order.IsOwnerCredited = order.ProfitToman > 0;
                order.OwnerBalanceBefore = beforeBalance;
                order.OwnerBalanceAfter = afterBalance;
                order.PaymentStatus = TenantBotOrderStatuses.Fulfilled;
                order.CreatedAccountEmail = created.Email;
                order.CreatedSubLink = created.SubLink;
                order.CreatedAccountJson = JsonConvert.SerializeObject(created);
                order.FulfilledAtUtc = DateTime.UtcNow;
                order.UpdatedAtUtc = DateTime.UtcNow;
                await CLEARTENANTORDERFULFILLMENTERRORASYNC(order, CancellationToken);

                _userDbcontext.TenantBotLedgerEntries.Add(new TenantBotLedgerEntry
                {
                    TenantBotId = order.TenantBotId,
                    TenantBotUsername = order.TenantBotUsername,
                    TenantBotOrderId = order.Id,
                    OrderId = order.OrderId,
                    OwnerTelegramUserId = order.OwnerTelegramUserId,
                    CustomerTelegramUserId = order.CustomerTelegramUserId,
                    SalePriceToman = order.SalePriceToman,
                    BaseCostToman = order.BaseCostToman,
                    ProfitToman = order.ProfitToman,
                    OwnerBalanceBefore = beforeBalance,
                    OwnerBalanceAfter = afterBalance,
                    Description = $"tenant Bot sale VIA @{order.TenantBotUsername}",
                    CreatedAtUtc = DateTime.UtcNow
                });

                await _userDbcontext.SaveChangesAsync(CancellationToken);
                await NOTIFYTENANTCUSTOMERSUCCESSASYNC(order, created, CancellationToken);
                await NOTIFYTENANTOWNERSUCCESSASYNC(order, owner, customer, CancellationToken);
                LOGTENANTORDER(order, owner, customer, Source, "fulfilled");
                return NowPaymentsSettlementResult.Applied(beforeBalance, afterBalance);
            }
            catch (Exception ex)
            {
                var retryable = IsTenantFulfillmentTimeout(ex);
                order.PaymentStatus = retryable
                    ? TenantBotOrderStatuses.ReceiptApproved
                    : TenantBotOrderStatuses.Failed;
                order.ErrorMessage = ex.Message;
                order.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken);
                if (retryable)
                {
                    await NOTIFYTENANTCUSTOMERRETRYABLEFULFILLMENTASYNC(order, ex.Message, CancellationToken);
                    LOGTENANTORDER(order, owner, customer, Source, "Exception-timeout-retryable");
                }
                else
                {
                    await NOTIFYTENANTCUSTOMERFAILUREASYNC(order, ex.Message, CancellationToken);
                    LOGTENANTORDER(order, owner, customer, Source, "Exception");
                }

                return NowPaymentsSettlementResult.InvalidAmount();
            }
        }
    }

    /// <summary>
    /// fulfills A NowPayments tenant order exactly once after the crypto gateway reports A paid invoice.
    /// </summary>
    /// <param name="payment">NowPayments payment row marked as A tenant order.</param>
    /// <param name="Source">settlement Source such as ipn, manual-check, or admin-manual.</param>
    /// <param name="CancellationToken">Cancellation Token for database, wallet, panel, and Telegram operations.</param>
    /// <returns>settlement result INDICATING whether the tenant order was applied or HAD already been fulfilled.</returns>
    /// <remarks>
    /// this method intentionally does not credit the customer wallet. it creates the ORDERED account, credits
    /// the tenant owner profit, Writes the general wallet ledger, and NOTIFIES the sales assistant. Manual
    /// super-admin sources force a fresh NOWPayments provider lookup and return
    /// <see cref="NowPaymentsSettlementStatus.ProviderNotPaid"/> unless the provider reports a paid status.
    /// </remarks>
    public async Task<NowPaymentsSettlementResult> ApplyPaidTenantOrderAsync(
        SwapinoPaymentInfo payment,
        string Source,
        CancellationToken CancellationToken = default)
    {
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();

        var data = payment.GetNowPaymentsData();
        var requiresProviderRefresh = Source?.IndexOf("manual", StringComparison.OrdinalIgnoreCase) >= 0;
        if (requiresProviderRefresh || !NowPaymentsStatuses.IsPaid(data.PaymentStatus ?? payment.PaymentStatus))
        {
            NowPaymentsPaymentStatusResult remoteStatus = null;
            try
            {
                remoteStatus = await REFRESHTENANTNOWPAYMENTSSTATUSASYNC(payment, data, CancellationToken);
            }
            catch (Exception ex)
            {
                payment.ErrorCode = "nowpayments_provider_check_failed";
                payment.ErrorMessage = $"NOWPayments provider check failed before tenant fulfillment. {ex.Message}";
                await _userDbcontext.SaveChangesAsync(CancellationToken);
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }

            if (remoteStatus != null)
            {
                data.Apply(remoteStatus);
                payment.SetNowPaymentsData(data);
                await _userDbcontext.SaveChangesAsync(CancellationToken);
            }

            var providerReportedPaid = remoteStatus != null &&
                                       NowPaymentsStatuses.IsPaid(remoteStatus.payment_status ?? data.PaymentStatus);
            if (!providerReportedPaid && (requiresProviderRefresh || !NowPaymentsStatuses.IsPaid(data.PaymentStatus ?? payment.PaymentStatus)))
                return NowPaymentsSettlementResult.ProviderNotPaid();
        }

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == payment.TenantBotOrderId || x.OrderId == payment.OrderId,
            CancellationToken);
        if (order == null)
            return NowPaymentsSettlementResult.NotFound();

        return await FULFILLPAIDTENANTORDERASYNC(order, Source, null, payment, false, CancellationToken);
    }

    /// <summary>
    /// Refreshes a tenant NOWPayments row before manual or repeated fulfillment attempts.
    /// </summary>
    /// <param name="payment">
    /// Local users.db NOWPayments row attached to a tenant order. The row may still contain only invoice data when a
    /// super-admin enters an order id before the provider has emitted a paid IPN.
    /// </param>
    /// <param name="data">
    /// Parsed NOWPayments data stored on the local row. The method updates this instance only after a provider
    /// response is returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for NOWPayments API calls.</param>
    /// <returns>
    /// Provider payment status when NOWPayments can identify the payment; otherwise <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Tenant fulfillment must never rely on a local manual status flip. This helper mirrors the wallet-charge
    /// manual-check rule: use payment id first, then invoice/order lookup, and fulfill only when the provider status
    /// is accepted by <see cref="NowPaymentsStatuses.IsPaid(string)"/>.
    /// </remarks>
    private async Task<NowPaymentsPaymentStatusResult> REFRESHTENANTNOWPAYMENTSSTATUSASYNC(
        SwapinoPaymentInfo payment,
        NowPaymentsPaymentRecordData data,
        CancellationToken cancellationToken)
    {
        var paymentId = payment.PaymentId ?? data.PaymentId;
        if (!string.IsNullOrWhiteSpace(paymentId))
            return await _nowPayments.GetPaymentStatusAsync(paymentId, cancellationToken);

        return await _nowPayments.FindPaymentStatusByInvoiceOrOrderAsync(
            payment.InvoiceId ?? data.InvoiceId,
            payment.OrderId ?? data.OrderId,
            cancellationToken);
    }

    /// <summary>
    /// PERFORMS the Shared one-time fulfillment for paid tenant orders from HooshPay, NowPayments, or manual card Approval.
    /// </summary>
    /// <param name="order">local tenant order that links the customer, owner, payment Provider, and selected plan.</param>
    /// <param name="Source">audit Source that caused fulfillment, for example ipn, manual-check, or assistant-final.</param>
    /// <param name="HOOSHPAYPAYMENT">optional HooshPay row when the Source Provider is HooshPay.</param>
    /// <param name="NOWPAYMENTSPAYMENT">optional NowPayments row when the Source Provider is NowPayments.</param>
    /// <param name="DEBITOWNERBASECOST">
    /// true for card-to-card receipts where the tenant owner received money directly and must Pay base cost from wallet.
    /// false for PLATFORM GATEWAYS where only the owner's profit is credited.
    /// </param>
    /// <param name="CancellationToken">Cancellation Token for users.db, credentials.db, Telegram, and xui calls.</param>
    /// <returns>settlement result with before/after owner wallet balances when fulfillment succeeds.</returns>
    /// <remarks>
    /// Idempotency:
    /// if <see cref="TenantBotOrder.IsFulfilled" /> is already true, this method returns without creating
    /// another xui account or another wallet ledger entry. this PROTECTS REPEATED IPNs and REPEATED assistant callbacks.
    /// </remarks>
    private async Task<NowPaymentsSettlementResult> FULFILLPAIDTENANTORDERASYNC(
        TenantBotOrder order,
        string Source,
        HooshPayPaymentInfo HOOSHPAYPAYMENT,
        SwapinoPaymentInfo NOWPAYMENTSPAYMENT,
        bool DEBITOWNERBASECOST,
        CancellationToken CancellationToken)
    {
        if (order.IsFulfilled)
            return NowPaymentsSettlementResult.AlreadyAdded(order.OwnerBalanceAfter ?? 0);

        order.PaymentStatus = TenantBotOrderStatuses.Paid;
        order.PaidAtUtc ??= DateTime.UtcNow;
        order.UpdatedAtUtc = DateTime.UtcNow;
        if (HOOSHPAYPAYMENT != null)
        {
            HOOSHPAYPAYMENT.PaymentStatus = HooshPayStatuses.Paid;
            HOOSHPAYPAYMENT.PaidAtUtc ??= DateTime.UtcNow;
        }

        if (NOWPAYMENTSPAYMENT != null)
        {
            NOWPAYMENTSPAYMENT.PaymentStatus = NowPaymentsStatuses.Finished;
            NOWPAYMENTSPAYMENT.PaidAtUtc ??= DateTime.UtcNow;
        }

        await _userDbcontext.SaveChangesAsync(CancellationToken);

        var TENANTCONFIG = _botRegistry.GetById(order.TenantBotId);
        var tenant = await _userDbcontext.BotInstances.FirstOrDefaultAsync(x => x.Id == order.TenantBotId, CancellationToken);
        var owner = await _credentialsDbContext.GetUserStatusWithId(order.OwnerTelegramUserId);
        var customer = await _credentialsDbContext.GetUserStatusWithId(order.CustomerTelegramUserId);
        if (owner == null || customer == null || tenant == null)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = "tenant owner or customer was not found.";
            await _userDbcontext.SaveChangesAsync(CancellationToken);
            return NowPaymentsSettlementResult.UserNotFound();
        }

        var selection = new XuiV3PurchaseSelection
        {
            ServiceKey = order.ServiceKey,
            TrafficGb = order.TrafficGb,
            DurationKey = order.DurationKey,
            UnlimitedPlanKey = order.UnlimitedPlanKey,
            AccountCount = 1
        };

        using (_botContextAccessor.Push(new BotRuntimeContext
        {
            Config = TENANTCONFIG,
            Client = _botClientProvider.GetClient(order.TenantBotId)
        }))
        {
            try
            {
                var created = await _purchaseService.CreateAccountAsync(
                    customer,
                    BuildConfiguredPanelServerInfo(),
                    selection,
                    _appConfig.XuiV3ApiBaseUrl,
                    CancellationToken,
                    new XuiV3AccountMetadataOptions
                    {
                        UserComment = $"tenant sale VIA @{tenant.Username}; Buyer={order.CustomerTelegramUserId}; tenant={order.TenantBotId}",
                        PriceTomanOverride = order.SalePriceToman,
                        CreatedByBotId = order.TenantBotId,
                        LastUpdatedByBotId = order.TenantBotId,
                        CreatedByTelegramUserId = order.CustomerTelegramUserId,
                        LastUpdatedByTelegramUserId = order.OwnerTelegramUserId,
                        LastAction = "tenant-Bot-sale",
                        SaveUserStatus = true
                    });

                if (!created.Success)
                {
                    order.PaymentStatus = TenantBotOrderStatuses.Failed;
                    order.ErrorMessage = created.Message;
                    order.UpdatedAtUtc = DateTime.UtcNow;
                    await _userDbcontext.SaveChangesAsync(CancellationToken);
                    await NOTIFYTENANTCUSTOMERFAILUREASYNC(order, created.Message, CancellationToken);
                    LOGTENANTORDER(order, owner, customer, Source, "account-Create-failed");
                    return NowPaymentsSettlementResult.InvalidAmount();
                }

                var settlement = await SETTLETENANTOWNERWALLETASYNC(
                    order,
                    owner,
                    DEBITOWNERBASECOST,
                    referenceType: "tenant-order",
                    CancellationToken);

                order.IsFulfilled = true;
                order.IsOwnerCredited = settlement.OwnerDelta > 0;
                order.OwnerWalletDelta = settlement.OwnerDelta;
                order.OwnerBalanceBefore = settlement.BotWalletBefore;
                order.OwnerBalanceAfter = settlement.BotWalletAfter;
                order.PaymentStatus = TenantBotOrderStatuses.Fulfilled;
                order.FulfillmentSource = Source;
                order.CreatedAccountEmail = created.Email;
                order.CreatedSubLink = created.SubLink;
                order.CreatedAccountJson = JsonConvert.SerializeObject(created);
                order.FulfilledAtUtc = DateTime.UtcNow;
                order.UpdatedAtUtc = DateTime.UtcNow;

                _userDbcontext.TenantBotLedgerEntries.Add(new TenantBotLedgerEntry
                {
                    TenantBotId = order.TenantBotId,
                    TenantBotUsername = order.TenantBotUsername,
                    TenantBotOrderId = order.Id,
                    OrderId = order.OrderId,
                    OwnerTelegramUserId = order.OwnerTelegramUserId,
                    CustomerTelegramUserId = order.CustomerTelegramUserId,
                    SalePriceToman = order.SalePriceToman,
                    BaseCostToman = order.BaseCostToman,
                    ProfitToman = settlement.OwnerDelta,
                    OwnerBalanceBefore = settlement.BotWalletBefore,
                    OwnerBalanceAfter = settlement.BotWalletAfter,
                    Description = $"tenant Bot sale VIA @{order.TenantBotUsername}",
                    CreatedAtUtc = DateTime.UtcNow
                });

                await _userDbcontext.SaveChangesAsync(CancellationToken);

                await _gozargahSiteSyncService.QueueCreateAsync(
                    order.OwnerTelegramUserId,
                    order.CustomerTelegramUserId,
                    created,
                    order.OrderId,
                    order.TenantBotId,
                    CancellationToken);

                await NOTIFYTENANTCUSTOMERSUCCESSASYNC(order, created, CancellationToken);
                await NOTIFYTENANTOWNERSUCCESSASYNC(order, owner, customer, CancellationToken, settlement);
                await _salesAssistantService.NOTIFYTENANTSALEASYNC(order, settlement.BotWalletBefore, settlement.BotWalletAfter, CancellationToken);
                LOGTENANTORDER(order, owner, customer, Source, "fulfilled", settlement);
                return NowPaymentsSettlementResult.Applied(settlement.BotWalletBefore, settlement.BotWalletAfter);
            }
            catch (Exception ex)
            {
                order.PaymentStatus = TenantBotOrderStatuses.Failed;
                order.ErrorMessage = ex.Message;
                order.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken);
                await NOTIFYTENANTCUSTOMERFAILUREASYNC(order, ex.Message, CancellationToken);
                LOGTENANTORDER(order, owner, customer, Source, "Exception");
                return NowPaymentsSettlementResult.InvalidAmount();
            }
        }
    }

    /// <summary>
    /// Refreshes a tenant order and detects whether fulfillment has already been recorded.
    /// </summary>
    /// <param name="order">
    /// Tenant order entity passed from an IPN, manual check, or Sales Assistant confirmation flow. The entity may be
    /// stale because the shared <see cref="UserDbContext"/> can already be tracking an older version of the same row.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the reload and ledger lookup.</param>
    /// <returns>
    /// <c>true</c> when the order is already fulfilled or when the unique tenant-order ledger row already exists;
    /// otherwise <c>false</c> and the caller may continue with account delivery.
    /// </returns>
    /// <remarks>
    /// This is the idempotency gate for tenant fulfillment. A customer can click "check status" while an IPN has
    /// already fulfilled the same order, and a singleton EF context may still expose a stale pending entity. Reloading
    /// the row and checking the unique ledger prevents duplicate XUI accounts, duplicate owner settlement, and the
    /// SQLite <c>UNIQUE constraint failed: TenantBotLedgerEntries.TenantBotOrderId</c> crash.
    /// </remarks>
    private async Task<bool> ISTENANTORDERALREADYFULFILLEDASYNC(
        TenantBotOrder order,
        CancellationToken cancellationToken)
    {
        if (order == null)
            return false;

        try
        {
            await _userDbcontext.Entry(order).ReloadAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Tenant order reload skipped before fulfillment. orderId={OrderId}", order.OrderId);
        }

        if (order.IsFulfilled)
            return true;

        return await _userDbcontext.TenantBotLedgerEntries
            .AsNoTracking()
            .AnyAsync(x => x.TenantBotOrderId == order.Id, cancellationToken);
    }

    /// <summary>
    /// PROMPTS A tenant customer to upload or Replace the photo receipt for A card-to-card order.
    /// </summary>
    /// <param name="botClient">tenant Bot client used to Send the PROMPT.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="ORDERDBID">internal users.db Id of the tenant order.</param>
    /// <param name="CustomerTelegramUserId">Telegram User Id used as an ownership guard.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db and Telegram work.</param>
    /// <remarks>
    /// this method does not Create or FULFILL ANYTHING. it only keeps the customer ORIENTED and MAKES
    /// RE-UPLOADING A receipt DISCOVERABLE from the Original order Message.
    /// </remarks>
    private async Task PromptTenantReceiptUploadAsync(
        ITelegramBotClient botClient,
        ChatId ChatId,
        int ORDERDBID,
        long CustomerTelegramUserId,
        CancellationToken CancellationToken)
    {
        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == ORDERDBID &&
                 x.CustomerTelegramUserId == CustomerTelegramUserId &&
                 x.PaymentProvider == "tenant_card" &&
                 !x.IsFulfilled,
            CancellationToken);

        if (order == null)
        {
            await botClient.SendTextMessageAsync(ChatId, "سفارش کارت‌به‌کارت فعالی برای ارسال رسید پیدا نشد.", cancellationToken: CancellationToken);
            return;
        }

        order.PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        await botClient.SendTextMessageAsync(
            ChatId,
            "لطفاً عکس رسید کارت‌به‌کارت همین سفارش را ارسال کنید.\n" +
            $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
            $"مبلغ سفارش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n\n" +
            "اگر قبلاً رسید فرستاده‌اید، ارسال عکس جدید جایگزین رسید قبلی همین سفارش می‌شود.",
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantCardPaymentKeyboard(order),
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// creates A manual card-to-card receipt from A customer photo and FORWARDS it to the sales assistant Bot.
    /// </summary>
    /// <param name="botClient">tenant Bot client that received the receipt photo.</param>
    /// <param name="Message">customer photo Message; the largest Telegram photo size is stored as the receipt file Id.</param>
    /// <param name="tenant">current tenant Bot that owns the pending order.</param>
    /// <param name="customer">credentials profile of the tenant customer who sent the receipt.</param>
    /// <param name="CancellationToken">Cancellation Token for users.db and Telegram calls.</param>
    /// <remarks>
    /// this method does not FULFILL the order. it only stores an auditable pending receipt and sends it to
    /// the Central sales assistant where the tenant owner must APPROVE and then finally confirm it.
    /// </remarks>
    private async Task CREATETENANTMANUALRECEIPTASYNC(
        ITelegramBotClient botClient,
        Message Message,
        BotInstance tenant,
        CredUser customer,
        CancellationToken CancellationToken)
    {
        var order = await _userDbcontext.TenantBotOrders
            .Where(x => x.TenantBotId == tenant.Id &&
                        x.CustomerTelegramUserId == customer.TelegramUserId &&
                        x.PaymentProvider == "tenant_card" &&
                        !x.IsFulfilled &&
                        (x.PaymentStatus == TenantBotOrderStatuses.AwaitingReceipt ||
                         x.PaymentStatus == TenantBotOrderStatuses.ReceiptSubmitted ||
                         x.PaymentStatus == TenantBotOrderStatuses.ReceiptRejected))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(CancellationToken);

        if (order == null)
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "سفارش کارت‌به‌کارت فعالی برای این رسید پیدا نشد.", cancellationToken: CancellationToken);
            return;
        }

        var photo = Message.Photo.OrderByDescending(x => x.FileSize ?? 0).FirstOrDefault();
        if (photo == null)
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "عکس رسید معتبر نیست.", cancellationToken: CancellationToken);
            return;
        }

        var receipt = order.ManualReceiptId.HasValue
            ? await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == order.ManualReceiptId.Value, CancellationToken)
            : await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.TenantBotOrderId == order.Id, CancellationToken);

        if (receipt == null)
        {
            receipt = new TenantManualPaymentReceipt
            {
                TenantBotOrderId = order.Id,
                OrderId = order.OrderId,
                TenantBotId = order.TenantBotId,
                TenantBotUsername = order.TenantBotUsername,
                OwnerTelegramUserId = order.OwnerTelegramUserId,
                CustomerTelegramUserId = order.CustomerTelegramUserId,
                CustomerChatId = order.CustomerChatId,
                AmountToman = order.SalePriceToman,
                CreatedAtUtc = DateTime.UtcNow
            };
            _userDbcontext.TenantManualPaymentReceipts.Add(receipt);
        }

        receipt.PhotoFileId = photo.FileId;
        receipt.Status = TenantManualPaymentReceiptStatuses.Pending;
        receipt.ReviewerTelegramUserId = null;
        receipt.ApprovedAtUtc = null;
        receipt.RejectedAtUtc = null;
        receipt.FinalConfirmedAtUtc = null;
        receipt.UpdatedAtUtc = DateTime.UtcNow;
        order.PaymentStatus = TenantBotOrderStatuses.ReceiptSubmitted;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        order.ManualReceiptId = receipt.Id;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        await _salesAssistantService.NOTIFYMANUALRECEIPTASYNC(receipt, CancellationToken);
        await botClient.SendTextMessageAsync(Message.Chat.Id, "✅ رسید شما ثبت شد و برای تایید همکار ارسال شد.", cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Final-confirms a tenant card-to-card receipt from the Sales Assistant and fulfills the linked order once.
    /// </summary>
    /// <param name="RECEIPTID">
    /// Internal <c>users.db</c> id of the receipt selected in the Sales Assistant bot.
    /// </param>
    /// <param name="ReviewerTelegramUserId">
    /// Numeric Telegram user id of the tenant owner who pressed final confirmation in the assistant bot.
    /// The value must match the receipt owner.
    /// </param>
    /// <param name="CancellationToken">
    /// Cancellation token for database updates, owner wallet debit, XUI account creation, ledger writes, and Telegram notifications.
    /// </param>
    /// <returns>
    /// A Persian result string shown as the Telegram callback alert in the Sales Assistant.
    /// </returns>
    /// <remarks>
    /// The tenant owner must be the reviewer. The method is idempotent: fulfilled orders do not create
    /// another XUI account, ledger entry, or customer delivery. Repeated confirmations only resend account
    /// details to the owner when useful, keeping the customer from receiving duplicate account messages.
    /// </remarks>
    public async Task<string> APPROVEMANUALRECEIPTASYNC(int RECEIPTID, long ReviewerTelegramUserId, CancellationToken CancellationToken)
    {
        var receipt = await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == RECEIPTID, CancellationToken);
        if (receipt == null)
            return "رسید پیدا نشد.";

        if (receipt.OwnerTelegramUserId != ReviewerTelegramUserId)
            return "فقط صاحب همین ربات فروشگاهی می‌تواند این رسید را تایید کند.";

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId,
            CancellationToken);
        if (order == null)
            return "سفارش مرتبط با رسید پیدا نشد.";

        if (order.IsFulfilled)
        {
            await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: false, sendOwner: true, CancellationToken);
            return "این سفارش قبلاً تایید و ساخته شده است.";
        }

        receipt.Status = TenantManualPaymentReceiptStatuses.Approved;
        receipt.ReviewerTelegramUserId = ReviewerTelegramUserId;
        receipt.ApprovedAtUtc = DateTime.UtcNow;
        receipt.FinalConfirmedAtUtc = DateTime.UtcNow;
        receipt.UpdatedAtUtc = DateTime.UtcNow;
        order.PaymentStatus = TenantBotOrderStatuses.ReceiptApproved;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        var settlement = await FULFILLPAIDTENANTORDERASYNC(order, "assistant-final", null, null, true, CancellationToken);
        if (settlement.Status == NowPaymentsSettlementStatus.Applied || settlement.Status == NowPaymentsSettlementStatus.AlreadyAdded)
        {
            await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: false, sendOwner: true, CancellationToken);
            return "رسید تایید شد و سفارش پردازش شد.";
        }

        if (IsTenantFulfillmentTimeout(order.ErrorMessage))
            return "رسید تایید شد، اما پنل ساخت اکانت پاسخ نداد. چند دقیقه دیگر دوباره تایید را بزنید.";

        return "تایید رسید ثبت شد ولی ساخت اکانت موفق نبود. لاگ را بررسی کنید.";
    }

    /// <summary>
    /// Manually approves one tenant card-to-card order by its public order id from the owner panel.
    /// </summary>
    /// <param name="botClient">Main owned bot client used to answer the colleague owner.</param>
    /// <param name="Message">Owner text message containing the exact public <c>OrderId</c>.</param>
    /// <param name="owner">Colleague profile that must own the target tenant order.</param>
    /// <param name="CancellationToken">Cancellation token for database, wallet, XUI, and Telegram work.</param>
    /// <remarks>
    /// This method is used when a card-to-card receipt was not approved through the Sales Assistant. It creates
    /// a receipt audit row when needed, marks it approved, and delegates fulfillment to the same idempotent
    /// method used by assistant final confirmation. If the order was already fulfilled, the method does not
    /// create another account or ledger entry and only resends existing account details to the tenant owner.
    /// </remarks>
    private async Task CONFIRMMANUALCARDORDERBYORDERIDASYNC(
        ITelegramBotClient botClient,
        Message Message,
        CredUser owner,
        CancellationToken CancellationToken)
    {
        var orderId = Message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(orderId))
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "OrderId نمی‌تواند خالی باشد. دوباره OrderId سفارش کارت‌به‌کارت را ارسال کنید.", cancellationToken: CancellationToken);
            return;
        }

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.OwnerTelegramUserId == owner.TelegramUserId && x.OrderId == orderId,
            CancellationToken);

        if (order == null)
        {
            await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
            await botClient.SendTextMessageAsync(Message.Chat.Id, "سفارشی با این OrderId برای ربات فروشگاهی شما پیدا نشد.", cancellationToken: CancellationToken);
            await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
            return;
        }

        if (!string.Equals(order.PaymentProvider, "tenant_card", StringComparison.OrdinalIgnoreCase))
        {
            await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
            await botClient.SendTextMessageAsync(Message.Chat.Id, "این سفارش مربوط به پرداخت کارت‌به‌کارت همکار نیست و از این مسیر قابل تایید دستی نیست.", cancellationToken: CancellationToken);
            await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
            return;
        }

        var receipt = await ENSUREMANUALRECEIPTASYNC(order, owner.TelegramUserId, CancellationToken);
        if (order.IsFulfilled)
        {
            await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
            await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: false, sendOwner: true, CancellationToken);
            await botClient.SendTextMessageAsync(Message.Chat.Id, "این سفارش قبلاً تایید شده بود. مشخصات اکانت فقط برای شما دوباره ارسال شد.", cancellationToken: CancellationToken);
            await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
            return;
        }

        receipt.Status = TenantManualPaymentReceiptStatuses.Approved;
        receipt.ReviewerTelegramUserId = owner.TelegramUserId;
        receipt.ApprovedAtUtc = DateTime.UtcNow;
        receipt.FinalConfirmedAtUtc = DateTime.UtcNow;
        receipt.UpdatedAtUtc = DateTime.UtcNow;
        order.PaymentStatus = TenantBotOrderStatuses.ReceiptApproved;
        order.ManualReceiptId = receipt.Id;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        var settlement = await FULFILLPAIDTENANTORDERASYNC(order, "owner-orderid-manual", null, null, true, CancellationToken);
        await _userDbcontext.ClearUserStatus(new User { Id = owner.TelegramUserId });
        if (settlement.Status == NowPaymentsSettlementStatus.Applied || settlement.Status == NowPaymentsSettlementStatus.AlreadyAdded)
        {
            await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: false, sendOwner: true, CancellationToken);
            await botClient.SendTextMessageAsync(Message.Chat.Id, "پرداخت کارت‌به‌کارت تایید شد، سفارش پردازش شد و مشخصات اکانت ارسال شد.", cancellationToken: CancellationToken);
        }
        else
        {
            await botClient.SendTextMessageAsync(Message.Chat.Id, "تایید ثبت شد، اما ساخت اکانت موفق نبود. لاگ را بررسی کنید.", cancellationToken: CancellationToken);
        }

        await SHOWOWNERPANELASYNC(botClient, Message.Chat.Id, owner, null, CancellationToken);
    }

    /// <summary>
    /// Lets a super-admin confirm or retry a tenant storefront order by its public <c>OrderId</c>.
    /// </summary>
    /// <param name="orderId">
    /// Public tenant order id copied from the tenant bot, Sales Assistant, or operational log. The lookup is global
    /// across tenant owners because only super-admin flows call this method.
    /// </param>
    /// <param name="superAdminTelegramUserId">
    /// Numeric Telegram user id of the super-admin performing the manual confirmation. It is stored as the reviewer
    /// on audit-only manual receipt rows when the order uses tenant card-to-card payment.
    /// </param>
    /// <param name="CancellationToken">Cancellation token for users.db, wallet, XUI, ledger, and Telegram delivery.</param>
    /// <returns>
    /// HTML-formatted status text when a tenant order was found and processed; <c>null</c> when the supplied id does
    /// not belong to a tenant order and the caller should continue checking other payment providers.
    /// </returns>
    /// <remarks>
    /// This method is idempotent. Fulfilled orders are not charged or created again; stored account details are only
    /// resent. Unfulfilled orders run the same fulfillment path used by IPN, customer checks, owner card approval,
    /// and Sales Assistant final confirmation.
    /// </remarks>
    public async Task<string> CONFIRMTENANTORDERBYSUPERADMINASYNC(
        string orderId,
        long superAdminTelegramUserId,
        CancellationToken CancellationToken)
    {
        orderId = orderId?.Trim();
        if (string.IsNullOrWhiteSpace(orderId))
            return null;

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(x => x.OrderId == orderId, CancellationToken);
        if (order == null)
            return null;

        if (order.IsFulfilled)
        {
            await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: true, sendOwner: true, CancellationToken);
            return BUILDSUPERADMINTENANTORDERRESULTTEXT(order, null, "این سفارش قبلاً تحویل شده بود؛ مشخصات ذخیره‌شده دوباره برای خریدار و همکار ارسال شد.");
        }

        NowPaymentsSettlementResult settlement;
        if (string.Equals(order.PaymentProvider, "tenant_card", StringComparison.OrdinalIgnoreCase))
        {
            var receipt = await ENSUREMANUALRECEIPTASYNC(order, superAdminTelegramUserId, CancellationToken);
            receipt.Status = TenantManualPaymentReceiptStatuses.Approved;
            receipt.ReviewerTelegramUserId = superAdminTelegramUserId;
            receipt.ApprovedAtUtc ??= DateTime.UtcNow;
            receipt.FinalConfirmedAtUtc = DateTime.UtcNow;
            receipt.UpdatedAtUtc = DateTime.UtcNow;
            order.PaymentStatus = TenantBotOrderStatuses.ReceiptApproved;
            order.ManualReceiptId = receipt.Id;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(CancellationToken);

            settlement = await FULFILLPAIDTENANTORDERASYNC(order, "super-admin-orderid-manual", null, null, true, CancellationToken);
        }
        else
        {
            var hooshPay = await _userDbcontext.HooshPayPaymentInfos.FirstOrDefaultAsync(
                x => x.TenantBotOrderId == order.Id || x.OrderId == order.OrderId,
                CancellationToken);
            if (hooshPay != null)
            {
                settlement = await ApplyPaidTenantOrderAsync(hooshPay, "super-admin-orderid-manual", CancellationToken);
            }
            else
            {
                var nowPayments = await _userDbcontext.SwapinoPaymentInfos.FirstOrDefaultAsync(
                    x => x.TenantBotOrderId == order.Id || x.OrderId == order.OrderId,
                    CancellationToken);
                settlement = nowPayments != null
                    ? await ApplyPaidTenantOrderAsync(nowPayments, "super-admin-orderid-manual", CancellationToken)
                    : await FULFILLPAIDTENANTORDERASYNC(order, "super-admin-orderid-manual", null, null, false, CancellationToken);
            }
        }

        var note = settlement?.Status == NowPaymentsSettlementStatus.Applied
            ? "سفارش تایید و پردازش شد."
            : settlement?.Status == NowPaymentsSettlementStatus.AlreadyAdded
                ? "این سفارش قبلاً پردازش شده بود؛ تحویل دوباره ساخته نشد."
                : IsTenantFulfillmentTimeout(order.ErrorMessage)
                    ? "تایید ثبت شد، اما پنل ساخت اکانت timeout داد. همین OrderId را چند دقیقه دیگر دوباره بررسی کنید."
                    : "تایید ثبت شد، اما تکمیل سفارش موفق نبود. خطای سفارش را بررسی کنید.";

        return BUILDSUPERADMINTENANTORDERRESULTTEXT(order, settlement, note);
    }

    /// <summary>
    /// Builds the HTML result shown to super-admins after confirming a tenant order by <c>OrderId</c>.
    /// </summary>
    /// <param name="order">Tenant order that was found by public order id.</param>
    /// <param name="settlement">Optional settlement result returned by the fulfillment path.</param>
    /// <param name="note">Human-readable outcome summary shown at the top of the admin response.</param>
    /// <returns>HTML-safe Telegram message with order, fulfillment, and latest error details.</returns>
    private static string BUILDSUPERADMINTENANTORDERRESULTTEXT(
        TenantBotOrder order,
        NowPaymentsSettlementResult settlement,
        string note)
    {
        return "✅ <b>بررسی سفارش tenant</b>\n\n" +
               $"{Html(note)}\n\n" +
               $"OrderId: <code>{Html(order.OrderId)}</code>\n" +
               $"ربات: <code>{Html(order.TenantBotId)}</code> @{Html(order.TenantBotUsername)}\n" +
               $"مالک: <code>{order.OwnerTelegramUserId}</code>\n" +
               $"خریدار: <code>{order.CustomerTelegramUserId}</code>\n" +
               $"نوع سفارش: <code>{Html(order.OrderKind)}</code>\n" +
               $"درگاه: <code>{Html(order.PaymentProvider)}</code>\n" +
               $"وضعیت پرداخت: <code>{Html(order.PaymentStatus)}</code>\n" +
               $"وضعیت settlement: <code>{Html(settlement?.Status.ToString() ?? "-")}</code>\n" +
               $"تحویل: <code>{Html(order.IsFulfilled ? "انجام شده" : "انجام نشده")}</code>\n" +
               $"اکانت: <code>{Html(order.CreatedAccountEmail)}</code>" +
               BuildTenantOrderErrorLine(order);
    }

    /// <summary>
    /// Gets the existing manual receipt row for an order or creates an audit-only row when no photo receipt exists.
    /// </summary>
    /// <param name="order">Tenant card-to-card order that is being manually approved.</param>
    /// <param name="reviewerTelegramUserId">Telegram user id of the colleague owner approving the order.</param>
    /// <param name="CancellationToken">Cancellation token for users.db access.</param>
    /// <returns>
    /// A tracked receipt entity linked to the tenant order. The row may have a null photo id when approval was
    /// performed by OrderId instead of a customer-uploaded receipt photo.
    /// </returns>
    /// <remarks>
    /// The database has a unique index on <see cref="TenantManualPaymentReceipt.TenantBotOrderId" />, so this method
    /// always reuses an existing row before creating a new one.
    /// </remarks>
    private async Task<TenantManualPaymentReceipt> ENSUREMANUALRECEIPTASYNC(
        TenantBotOrder order,
        long reviewerTelegramUserId,
        CancellationToken CancellationToken)
    {
        var receipt = order.ManualReceiptId.HasValue
            ? await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == order.ManualReceiptId.Value, CancellationToken)
            : await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.TenantBotOrderId == order.Id, CancellationToken);

        if (receipt != null)
            return receipt;

        receipt = new TenantManualPaymentReceipt
        {
            TenantBotOrderId = order.Id,
            OrderId = order.OrderId,
            TenantBotId = order.TenantBotId,
            TenantBotUsername = order.TenantBotUsername,
            OwnerTelegramUserId = order.OwnerTelegramUserId,
            CustomerTelegramUserId = order.CustomerTelegramUserId,
            CustomerChatId = order.CustomerChatId,
            AmountToman = order.SalePriceToman,
            Status = TenantManualPaymentReceiptStatuses.Pending,
            ReviewerTelegramUserId = reviewerTelegramUserId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _userDbcontext.TenantManualPaymentReceipts.Add(receipt);
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        order.ManualReceiptId = receipt.Id;
        return receipt;
    }

    /// <summary>
    /// Resends account details for a fulfilled manual receipt order when the Sales Assistant requests it.
    /// </summary>
    /// <param name="RECEIPTID">Internal users.db receipt id selected from a Sales Assistant callback.</param>
    /// <param name="ReviewerTelegramUserId">Telegram user id of the owner requesting the resend.</param>
    /// <param name="CancellationToken">Cancellation token for database and Telegram work.</param>
    /// <returns>Human-readable Persian result shown in the Sales Assistant callback alert.</returns>
    public async Task<string> RESENDMANUALRECEIPTACCOUNTASYNC(int RECEIPTID, long ReviewerTelegramUserId, CancellationToken CancellationToken)
    {
        var receipt = await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == RECEIPTID, CancellationToken);
        if (receipt == null)
            return "رسید پیدا نشد.";

        if (receipt.OwnerTelegramUserId != ReviewerTelegramUserId)
            return "فقط صاحب همین ربات فروشگاهی می‌تواند مشخصات این سفارش را دریافت کند.";

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId,
            CancellationToken);
        if (order == null)
            return "سفارش مرتبط با رسید پیدا نشد.";

        if (!order.IsFulfilled)
            return "این سفارش هنوز ساخته نشده است و مشخصات اکانت برای ارسال مجدد وجود ندارد.";

        await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: true, sendOwner: true, CancellationToken);
        return "مشخصات اکانت دوباره برای شما و خریدار ارسال شد.";
    }

    /// <summary>
    /// Finds the newest tenant order for the current Bot and customer, then Runs the normal order-status check.
    /// </summary>
    /// <param name="botClient">Telegram client for the current tenant Bot.</param>
    /// <param name="ChatId">chat that should Receive the manual-check result.</param>
    /// <param name="CustomerTelegramUserId">Telegram User Id of the storefront customer.</param>
    /// <param name="CancellationToken">Cancellation Token for database and Telegram calls.</param>
    private async Task CHECKLATESTCUSTOMERORDERASYNC(ITelegramBotClient botClient, ChatId ChatId, long CustomerTelegramUserId, CancellationToken CancellationToken)
    {
        var order = await _userDbcontext.TenantBotOrders
            .Where(x => x.TenantBotId == BotContextAccessor.CurrentBotId && x.CustomerTelegramUserId == CustomerTelegramUserId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(CancellationToken);

        if (order == null)
        {
            await botClient.SendTextMessageAsync(ChatId, "سفارش فعالی برای بررسی پیدا نشد.", cancellationToken: CancellationToken);
            return;
        }

        await CheckTenantOrderAsync(botClient, ChatId, order.Id, CustomerTelegramUserId, CancellationToken);
    }

    /// <summary>
    /// Verifies a specific tenant order against its payment provider and replays fulfilled account details when needed.
    /// </summary>
    /// <param name="botClient">Telegram client for the tenant Bot that owns the order.</param>
    /// <param name="ChatId">chat that Receives the current payment status.</param>
    /// <param name="ORDERDBID">Internal users.db id of the tenant order embedded in the tenant callback.</param>
    /// <param name="CustomerTelegramUserId">
    /// Numeric Telegram user id of the tenant customer who clicked the status button. The order must belong to
    /// this user and to the current tenant bot context.
    /// </param>
    /// <param name="CancellationToken">Cancellation token for provider API calls, users.db writes, and Telegram replies.</param>
    /// <remarks>
    /// This method is used by tenant payment keyboards and payment return checks. It is idempotent: fulfilled
    /// orders are never built or charged again; their stored account details are resent to the buyer instead.
    /// </remarks>
    private async Task CheckTenantOrderAsync(ITelegramBotClient botClient, ChatId ChatId, int ORDERDBID, long CustomerTelegramUserId, CancellationToken CancellationToken)
    {
        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == ORDERDBID &&
                 x.TenantBotId == BotContextAccessor.CurrentBotId &&
                 x.CustomerTelegramUserId == CustomerTelegramUserId,
            CancellationToken);
        if (order == null)
        {
            await botClient.SendTextMessageAsync(ChatId, "سفارش پیدا نشد.", cancellationToken: CancellationToken);
            return;
        }

        if (order.IsFulfilled)
        {
            await RESENDFULFILLEDTENANTORDERTOCUSTOMERASYNC(botClient, ChatId, order, CancellationToken);
            return;
        }

        if (IsPendingTenantCardOrder(order))
        {
            await botClient.SendTextMessageAsync(
                ChatId,
                "در انتظار پرداخت و تایید مدیر.",
                replyMarkup: BuildTenantCardPaymentKeyboard(order),
                cancellationToken: CancellationToken);
            return;
        }

        if (string.Equals(order.PaymentProvider, "tenant_card", StringComparison.OrdinalIgnoreCase))
        {
            await botClient.SendTextMessageAsync(
                ChatId,
                "پرداخت شما توسط مدیر تایید شده است، اما ساخت اکانت هنوز کامل نشده یا نیاز به تلاش مجدد دارد. لطفاً کمی صبر کنید یا با پشتیبانی فروشگاه تماس بگیرید.",
                cancellationToken: CancellationToken);
            return;
        }

        var payment = await _userDbcontext.HooshPayPaymentInfos.FirstOrDefaultAsync(
            x => x.Id == order.HooshPayPaymentInfoId || x.TenantBotOrderId == order.Id,
            CancellationToken);
        if (payment == null && order.NowPaymentsPaymentInfoId.HasValue)
        {
            var CRYPTOPAYMENT = await _userDbcontext.SwapinoPaymentInfos.FirstOrDefaultAsync(
                x => x.Id == order.NowPaymentsPaymentInfoId.Value || x.TenantBotOrderId == order.Id,
                CancellationToken);
            if (CRYPTOPAYMENT == null)
            {
                await botClient.SendTextMessageAsync(ChatId, "فاکتور پرداخت این سفارش پیدا نشد.", cancellationToken: CancellationToken);
                return;
            }

            var Data = CRYPTOPAYMENT.GetNowPaymentsData();
            var status = await _nowPayments.FindPaymentStatusByInvoiceOrOrderAsync(
                Data.InvoiceId,
                CRYPTOPAYMENT.OrderId,
                CancellationToken);
            if (status != null)
            {
                Data.Apply(status);
                CRYPTOPAYMENT.SetNowPaymentsData(Data);
                order.PaymentStatus = NowPaymentsStatuses.IsPaid(Data.PaymentStatus)
                    ? TenantBotOrderStatuses.Paid
                    : TenantBotOrderStatuses.Pending;
                order.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken);
            }

            if (NowPaymentsStatuses.IsPaid(CRYPTOPAYMENT.PaymentStatus))
            {
                var settlement = await ApplyPaidTenantOrderAsync(CRYPTOPAYMENT, "manual-check", CancellationToken);
                await SENDTENANTSETTLEMENTCHECKRESULTASYNC(botClient, ChatId, order, settlement, CancellationToken);
                return;
            }

            await botClient.SendTextMessageAsync(
                ChatId,
                $"پرداخت هنوز تایید نشده است.\nوضعیت فعلی: <code>{Html(CRYPTOPAYMENT.PaymentStatus)}</code>",
                parseMode: ParseMode.Html,
                replyMarkup: BuildTenantPaymentKeyboard(order, CRYPTOPAYMENT.InvoiceUrl),
                cancellationToken: CancellationToken);
            return;
        }
        if (payment == null || string.IsNullOrWhiteSpace(payment.InvoiceUid))
        {
            await botClient.SendTextMessageAsync(ChatId, "فاکتور پرداخت این سفارش پیدا نشد.", cancellationToken: CancellationToken);
            return;
        }

        // manual checks REUSE HooshPay Verify, then Run the same fulfillment path as ipn.
        var Verify = await _hooshPay.VerifyInvoiceAsync(payment.InvoiceUid, CancellationToken);
        payment.RawResponseJson = JsonConvert.SerializeObject(Verify);
        payment.Apply(Verify?.data);
        order.PaymentStatus = HooshPayStatuses.IsPaid(payment.PaymentStatus) || Verify?.paid == true
            ? TenantBotOrderStatuses.Paid
            : TenantBotOrderStatuses.Pending;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbcontext.SaveChangesAsync(CancellationToken);

        if (Verify?.paid == true || HooshPayStatuses.IsPaid(payment.PaymentStatus))
        {
            var settlement = await ApplyPaidTenantOrderAsync(payment, "manual-check", CancellationToken);
            await SENDTENANTSETTLEMENTCHECKRESULTASYNC(botClient, ChatId, order, settlement, CancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            ChatId,
            $"پرداخت هنوز تایید نشده است.\nوضعیت فعلی: <code>{Html(payment.PaymentStatus)}</code>",
            parseMode: ParseMode.Html,
            replyMarkup: BuildTenantPaymentKeyboard(order, payment),
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Checks whether an unfulfilled tenant order is waiting for owner-side card-to-card payment review.
    /// </summary>
    /// <param name="order">
    /// Tenant order selected by the current customer status callback. The order must already be guarded by
    /// tenant bot id and customer Telegram user id before this helper is called.
    /// </param>
    /// <returns>
    /// <c>true</c> when the order uses the tenant owner's card-to-card provider and is still before owner approval;
    /// otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Card-to-card tenant orders do not have a HooshPay or NOWPayments invoice to verify. Until the owner or
    /// Sales Assistant confirms the receipt, customer status checks should keep the receipt upload/status
    /// keyboard visible. Once the receipt is approved, even if XUI fulfillment times out, the customer should see
    /// a post-approval retry message rather than another "waiting for payment" message.
    /// </remarks>
    private static bool IsPendingTenantCardOrder(TenantBotOrder order)
    {
        if (order == null || order.IsFulfilled)
            return false;

        if (!string.Equals(order.PaymentProvider, "tenant_card", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(order.PaymentStatus, TenantBotOrderStatuses.Pending, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(order.PaymentStatus, TenantBotOrderStatuses.AwaitingReceipt, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(order.PaymentStatus, TenantBotOrderStatuses.ReceiptSubmitted, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(order.PaymentStatus, TenantBotOrderStatuses.ReceiptRejected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sends the customer-facing result after a manual tenant payment-status check triggers fulfillment.
    /// </summary>
    /// <param name="botClient">Tenant bot client that should answer the customer in the current chat.</param>
    /// <param name="chatId">Telegram chat id where the status-check result should be posted.</param>
    /// <param name="order">Tenant order that was checked and may now be fulfilled.</param>
    /// <param name="settlement">
    /// Result returned by the idempotent tenant fulfillment path. <c>Applied</c> means a new account was
    /// created during this check; <c>AlreadyAdded</c> means the order had already been fulfilled.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    /// <returns>A task that completes after the result message and any safe resend attempt finish.</returns>
    /// <remarks>
    /// The method is notification-only. It never creates XUI accounts, credits/debits wallets, or writes ledger
    /// rows. Duplicate callbacks are handled by resending stored account data when the settlement was already
    /// applied earlier.
    /// </remarks>
    private async Task SENDTENANTSETTLEMENTCHECKRESULTASYNC(
        ITelegramBotClient botClient,
        ChatId chatId,
        TenantBotOrder order,
        NowPaymentsSettlementResult settlement,
        CancellationToken cancellationToken)
    {
        if (settlement?.Status == NowPaymentsSettlementStatus.Applied)
        {
            await botClient.SendTextMessageAsync(
                chatId,
                "✅ پرداخت تایید شد و مشخصات اکانت برای شما ارسال شد.",
                cancellationToken: cancellationToken);
            return;
        }

        if (settlement?.Status == NowPaymentsSettlementStatus.AlreadyAdded || order.IsFulfilled)
        {
            await RESENDFULFILLEDTENANTORDERTOCUSTOMERASYNC(botClient, chatId, order, cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId,
            "پرداخت تایید شد، اما تکمیل سفارش با خطا روبه‌رو شد. موضوع برای بررسی ثبت شد.",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Replays stored account details for a tenant order that has already been fulfilled.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to post the explanatory status message.</param>
    /// <param name="chatId">Telegram chat id where the customer clicked the status-check button.</param>
    /// <param name="order">Fulfilled tenant order whose stored account details should be resent.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    /// <returns>A task that completes after the resend attempt and status message have finished.</returns>
    /// <remarks>
    /// This is the duplicate-safe path for old payment buttons, repeated manual checks, and callbacks after
    /// IPN or Sales Assistant fulfillment. It does not touch payment status, owner balance, ledger rows, or XUI.
    /// </remarks>
    private async Task RESENDFULFILLEDTENANTORDERTOCUSTOMERASYNC(
        ITelegramBotClient botClient,
        ChatId chatId,
        TenantBotOrder order,
        CancellationToken cancellationToken)
    {
        var sent = await SENDTENANTORDERACCOUNTDETAILSASYNC(order, sendCustomer: true, sendOwner: false, CancellationToken: cancellationToken);
        await botClient.SendTextMessageAsync(
            chatId,
            sent
                ? "✅ این سفارش قبلاً تایید شده بود. مشخصات اکانت دوباره برای شما ارسال شد."
                : "✅ این سفارش قبلاً تایید شده بود، اما مشخصات ذخیره‌شده‌ای برای ارسال مجدد پیدا نشد. لطفاً با پشتیبانی تماس بگیرید.",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// marks the customer's newest UNFULFILLED order in the current tenant Bot as Cancelled after A payment-return Cancel.
    /// </summary>
    /// <param name="botClient">Telegram client used to notify the customer.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="CustomerTelegramUserId">Telegram User Id of the customer whose pending order is Cancelled.</param>
    /// <param name="CancellationToken">Cancellation Token for database and Telegram work.</param>
    private async Task MARKLATESTCUSTOMERORDERCANCELLEDASYNC(ITelegramBotClient botClient, ChatId ChatId, long CustomerTelegramUserId, CancellationToken CancellationToken)
    {
        var order = await _userDbcontext.TenantBotOrders
            .Where(x => x.TenantBotId == BotContextAccessor.CurrentBotId &&
                        x.CustomerTelegramUserId == CustomerTelegramUserId &&
                        !x.IsFulfilled)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(CancellationToken);

        if (order != null)
        {
            order.PaymentStatus = "cancelled_by_user";
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(CancellationToken);
        }

        await botClient.SendTextMessageAsync(ChatId, "پرداخت توسط کاربر کنسل شد و سفارش بسته شد.", cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Sends the created account details to the tenant customer after fulfillment.
    /// </summary>
    /// <param name="order">fulfilled tenant order containing the target customer chat.</param>
    /// <param name="created">result returned by the XuiV3 purchase service after account creation.</param>
    /// <param name="CancellationToken">Cancellation token for Telegram delivery.</param>
    /// <remarks>
    /// Delivery is best-effort and intentionally does not throw back into fulfillment. A Telegram delivery
    /// failure after XUI creation and ledger writes must not mark the already-created order as failed.
    /// </remarks>
    private async Task NOTIFYTENANTCUSTOMERSUCCESSASYNC(TenantBotOrder order, XuiV3AccountCreationResult created, CancellationToken CancellationToken)
    {
        try
        {
            await SENDCREATEDACCOUNTDETAILSASYNC(
                _botClientProvider.GetClient(order.TenantBotId),
                GetTenantCustomerDeliveryChatId(order),
                created,
                CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant account delivery to customer failed after fulfillment. orderId={OrderId}", order.OrderId);
        }
    }

    /// <summary>
    /// Resends the fulfilled tenant order account details to the buyer and/or the tenant owner.
    /// </summary>
    /// <param name="order">Fulfilled tenant order containing the serialized account creation result.</param>
    /// <param name="sendCustomer">Whether the buyer should receive the account again from the tenant bot.</param>
    /// <param name="sendOwner">Whether the colleague owner should receive the account from the tenant bot.</param>
    /// <param name="CancellationToken">Cancellation token for Telegram delivery.</param>
    /// <returns><c>true</c> when account details were available for sending; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// This method never changes wallet, ledger, payment, or XUI state. It only replays stored fulfillment data
    /// and is therefore safe to call after duplicate manual approvals or duplicate Sales Assistant callbacks.
    /// </remarks>
    private async Task<bool> SENDTENANTORDERACCOUNTDETAILSASYNC(
        TenantBotOrder order,
        bool sendCustomer,
        bool sendOwner,
        CancellationToken CancellationToken)
    {
        var created = BUILDCREATEDACCOUNTRESULTFROMORDER(order);
        if (created == null || string.IsNullOrWhiteSpace(created.Email) && string.IsNullOrWhiteSpace(created.SubLink))
            return false;

        var tenantClient = _botClientProvider.GetClient(order.TenantBotId);
        if (sendCustomer)
        {
            try
            {
                await SENDCREATEDACCOUNTDETAILSASYNC(tenantClient, GetTenantCustomerDeliveryChatId(order), created, CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant account resend to customer failed. orderId={OrderId}", order.OrderId);
            }
        }

        if (sendOwner)
        {
            try
            {
                await SENDCREATEDACCOUNTDETAILSASYNC(tenantClient, order.OwnerTelegramUserId, created, CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant account resend to owner via tenant bot failed. orderId={OrderId}", order.OrderId);
                try
                {
                    await _botClientProvider.GetClient(_botRegistry.DefaultBot.Id).SendTextMessageAsync(
                        order.OwnerTelegramUserId,
                        "مشخصات اکانت ساخته‌شده:\n\n" + _purchaseService.BuildCreatedAccountText(created),
                        parseMode: ParseMode.Html,
                        cancellationToken: CancellationToken);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogWarning(fallbackEx, "Tenant account resend fallback to owner failed. orderId={OrderId}", order.OrderId);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the best Telegram chat id for sending tenant order details to the buyer.
    /// </summary>
    /// <param name="order">
    /// Tenant order that stores both the original customer chat id and the numeric Telegram user id.
    /// </param>
    /// <returns>
    /// The original customer chat id when it is available; otherwise the buyer's Telegram user id as a private
    /// chat fallback.
    /// </returns>
    /// <remarks>
    /// Older or manually-created tenant orders may have <see cref="TenantBotOrder.CustomerChatId"/> unset.
    /// Telegram private chat ids normally match user ids, so the fallback lets duplicate checks and manual
    /// confirmations still deliver account details without changing order or wallet state.
    /// </remarks>
    private static ChatId GetTenantCustomerDeliveryChatId(TenantBotOrder order)
    {
        return order.CustomerChatId == 0 ? order.CustomerTelegramUserId : order.CustomerChatId;
    }

    /// <summary>
    /// Sends one created account result as text or QR photo to a Telegram chat.
    /// </summary>
    /// <param name="botClient">Telegram bot client that should send the account details.</param>
    /// <param name="chatId">Target Telegram chat id for the buyer or tenant owner.</param>
    /// <param name="created">Stored account creation result.</param>
    /// <param name="CancellationToken">Cancellation token for Telegram delivery.</param>
    /// <remarks>
    /// When a subscription link exists, the QR is regenerated locally from the stored link. No panel call is made.
    /// </remarks>
    private async Task SENDCREATEDACCOUNTDETAILSASYNC(
        ITelegramBotClient botClient,
        ChatId chatId,
        XuiV3AccountCreationResult created,
        CancellationToken CancellationToken)
    {
        var Text = _purchaseService.BuildCreatedAccountText(created);
        if (!string.IsNullOrWhiteSpace(created.SubLink))
        {
            using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(created.SubLink, 200));
            await botClient.SendPhotoAsync(
                chatId: chatId,
                photo: InputFile.FromStream(qrStream, "subscription-qr.png"),
                caption: Text,
                parseMode: ParseMode.Html,
                cancellationToken: CancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(chatId, Text, parseMode: ParseMode.Html, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Rebuilds a minimal account creation result from stored tenant order fulfillment fields.
    /// </summary>
    /// <param name="order">Fulfilled tenant order that stores account JSON and fallback account fields.</param>
    /// <returns>
    /// A successful account creation result suitable for display, or <c>null</c> when the order has no stored
    /// account identity.
    /// </returns>
    private static XuiV3AccountCreationResult BUILDCREATEDACCOUNTRESULTFROMORDER(TenantBotOrder order)
    {
        XuiV3AccountCreationResult created = null;
        if (!string.IsNullOrWhiteSpace(order?.CreatedAccountJson))
        {
            try
            {
                created = JsonConvert.DeserializeObject<XuiV3AccountCreationResult>(order.CreatedAccountJson);
            }
            catch
            {
                created = null;
            }
        }

        created ??= new XuiV3AccountCreationResult();
        created.Success = true;
        if (string.IsNullOrWhiteSpace(created.Email))
            created.Email = order?.CreatedAccountEmail;
        if (string.IsNullOrWhiteSpace(created.SubLink))
            created.SubLink = order?.CreatedSubLink;

        return string.IsNullOrWhiteSpace(created.Email) && string.IsNullOrWhiteSpace(created.SubLink)
            ? null
            : created;
    }

    /// <summary>
    /// NOTIFIES the customer that payment was accepted but account creation failed and must be reviewed.
    /// </summary>
    /// <param name="order">tenant order whose customer should be notified.</param>
    /// <param name="Error">internal Error Text kept for diagnostics; it is not EXPOSED VERBATIM to the customer.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram delivery.</param>
    private async Task NOTIFYTENANTCUSTOMERFAILUREASYNC(TenantBotOrder order, string Error, CancellationToken CancellationToken)
    {
        try
        {
            await _botClientProvider.GetClient(order.TenantBotId).SendTextMessageAsync(
                order.CustomerChatId,
                "پرداخت شما تایید شد، اما ساخت اکانت با خطا روبه‌رو شد. موضوع برای بررسی ثبت شد.",
                cancellationToken: CancellationToken);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Notifies a tenant customer that payment is accepted but XUI account delivery is waiting for a retry.
    /// </summary>
    /// <param name="order">
    /// Tenant order whose payment or manual receipt has already been accepted. The order remains unfulfilled and
    /// must keep enough state for the tenant owner or payment checker to retry the same fulfillment path.
    /// </param>
    /// <param name="Error">
    /// Internal timeout text recorded for operators. The raw value is not shown to the customer because it may
    /// contain infrastructure details from the XUI panel or HTTP stack.
    /// </param>
    /// <param name="CancellationToken">Cancellation token for the best-effort Telegram notification.</param>
    /// <returns>A task that completes after the notification is sent or skipped.</returns>
    /// <remarks>
    /// A panel timeout is not treated as a definitive account-creation failure. The order stays in a retryable
    /// approved state so Sales Assistant callbacks can attempt fulfillment again without creating wallet or ledger
    /// changes until the XUI account is actually created.
    /// </remarks>
    private async Task NOTIFYTENANTCUSTOMERRETRYABLEFULFILLMENTASYNC(TenantBotOrder order, string Error, CancellationToken CancellationToken)
    {
        try
        {
            await _botClientProvider.GetClient(order.TenantBotId).SendTextMessageAsync(
                GetTenantCustomerDeliveryChatId(order),
                "✅ پرداخت شما تایید شد، اما پنل ساخت اکانت در این لحظه پاسخ نداد. سفارش برای تلاش مجدد ثبت شد و پس از ساخت موفق، مشخصات اکانت برای شما ارسال می‌شود.",
                cancellationToken: CancellationToken);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Applies the tenant owner's financial settlement for a fulfilled tenant purchase or renewal.
    /// </summary>
    /// <param name="order">
    /// Tenant order that has already succeeded on the XUI panel and is about to be marked fulfilled locally.
    /// </param>
    /// <param name="owner">
    /// Credentials row for the colleague who owns the tenant storefront. The method refreshes
    /// <see cref="CredUser.AccountBalance"/> after any bot-wallet mutation.
    /// </param>
    /// <param name="debitOwnerBaseCost">
    /// <c>true</c> for card-to-card flows where the owner received the customer money and must pay base cost;
    /// <c>false</c> for platform gateways where the owner should receive profit.
    /// </param>
    /// <param name="referenceType">
    /// Ledger reference type, such as <c>tenant-order</c> or <c>tenant-renew-order</c>, used to identify the
    /// fulfilled local order in wallet history.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for credentials, site-wallet, and ledger operations.</param>
    /// <returns>
    /// Settlement result containing bot-wallet before/after values, optional Gozargah website wallet before/after
    /// values, selected wallet source, owner delta, and any warning that should be shown to the owner.
    /// </returns>
    /// <remarks>
    /// Card-to-card settlement is intentionally ordered: debit the owner's bot wallet when it can cover the base
    /// cost, otherwise debit the Gozargah website wallet only when it is connected and sufficient, otherwise allow
    /// the owner's bot wallet to go negative and warn the owner. Platform-gateway settlement never debits the site
    /// wallet; it only credits tenant profit to the bot wallet and records a live site-wallet snapshot for audit logs.
    /// </remarks>
    private async Task<TenantOwnerWalletSettlementResult> SETTLETENANTOWNERWALLETASYNC(
        TenantBotOrder order,
        CredUser owner,
        bool debitOwnerBaseCost,
        string referenceType,
        CancellationToken cancellationToken)
    {
        var botBefore = owner.AccountBalance;
        var siteBefore = await GETTENANTOWNERSITEWALLETSNAPSHOTASYNC(order.OwnerTelegramUserId, cancellationToken);

        if (!debitOwnerBaseCost)
        {
            var ownerDelta = order.ProfitToman;
            if (ownerDelta > 0)
                await _credentialsDbContext.AddFund(order.OwnerTelegramUserId, ownerDelta);

            var botAfter = await _credentialsDbContext.GetAccountBalance(order.OwnerTelegramUserId);
            owner.AccountBalance = botAfter;
            var siteAfter = await GETTENANTOWNERSITEWALLETSNAPSHOTASYNC(order.OwnerTelegramUserId, cancellationToken);

            await RECORDTENANTOWNERWALLETLEDGERASYNC(
                order,
                WalletLedgerDirections.Credit,
                ownerDelta,
                botBefore,
                botAfter,
                WalletLedgerReasons.TenantGatewayProfit,
                provider: order.PaymentProvider,
                referenceType,
                description: "سود فروش tenant با درگاه پلتفرم",
                cancellationToken);

            return TenantOwnerWalletSettlementResult.Create(
                TenantOwnerWalletSources.PlatformGatewayProfit,
                ownerDelta,
                botBefore,
                botAfter,
                siteBefore,
                siteAfter);
        }

        if (botBefore >= order.BaseCostToman)
        {
            await _credentialsDbContext.Pay(owner, order.BaseCostToman);
            var botAfter = await _credentialsDbContext.GetAccountBalance(order.OwnerTelegramUserId);
            owner.AccountBalance = botAfter;
            var siteAfter = await GETTENANTOWNERSITEWALLETSNAPSHOTASYNC(order.OwnerTelegramUserId, cancellationToken);

            await RECORDTENANTOWNERWALLETLEDGERASYNC(
                order,
                WalletLedgerDirections.Debit,
                order.BaseCostToman,
                botBefore,
                botAfter,
                WalletLedgerReasons.TenantCardBaseCost,
                provider: order.PaymentProvider,
                referenceType,
                description: "کسر هزینه پایه فروش کارت‌به‌کارت tenant از کیف پول ربات",
                cancellationToken);

            return TenantOwnerWalletSettlementResult.Create(
                TenantOwnerWalletSources.BotWallet,
                -order.BaseCostToman,
                botBefore,
                botAfter,
                siteBefore,
                siteAfter);
        }

        var siteEligibility = await _gozargahSiteSyncService.CheckSiteWalletEligibilityAsync(
            order.OwnerTelegramUserId,
            order.BaseCostToman,
            cancellationToken);
        if (siteEligibility.CanUse)
        {
            var siteDebit = await _gozargahSiteSyncService.DeductSiteWalletAfterPanelSuccessAsync(
                order.OwnerTelegramUserId,
                order.BaseCostToman,
                referenceType,
                order.OrderId,
                $"Tenant card base cost: {order.OrderId}",
                cancellationToken);
            if (siteDebit.Success)
            {
                var botAfter = await _credentialsDbContext.GetAccountBalance(order.OwnerTelegramUserId);
                owner.AccountBalance = botAfter;
                var siteDebitBefore = TenantSiteWalletSnapshot.Connected(siteDebit.BeforeWallet);
                var siteDebitAfter = TenantSiteWalletSnapshot.Connected(siteDebit.AfterWallet);

                await RECORDTENANTOWNERWALLETLEDGERASYNC(
                    order,
                    WalletLedgerDirections.Debit,
                    order.BaseCostToman,
                    siteDebit.BeforeWallet,
                    siteDebit.AfterWallet,
                    WalletLedgerReasons.TenantCardBaseCost,
                    provider: "gozargah_site_wallet",
                    referenceType,
                    description: "کسر هزینه پایه فروش کارت‌به‌کارت tenant از کیف پول سایت گذرگاه",
                    cancellationToken);

                return TenantOwnerWalletSettlementResult.Create(
                    TenantOwnerWalletSources.GozargahSiteWallet,
                    -order.BaseCostToman,
                    botBefore,
                    botAfter,
                    siteDebitBefore,
                    siteDebitAfter);
            }
        }

        await _credentialsDbContext.Pay(owner, order.BaseCostToman);
        var negativeBotAfter = await _credentialsDbContext.GetAccountBalance(order.OwnerTelegramUserId);
        owner.AccountBalance = negativeBotAfter;
        var negativeSiteAfter = await GETTENANTOWNERSITEWALLETSNAPSHOTASYNC(order.OwnerTelegramUserId, cancellationToken);
        var warning =
            "⚠️ موجودی کیف پول ربات شما برای هزینه پایه کافی نبود و کیف پول سایت گذرگاه هم قابل استفاده نبود. " +
            "هزینه پایه از کیف پول ربات کسر شد و موجودی شما منفی شد. لطفاً موجودی را افزایش دهید؛ در غیر اینصورت اکانت مشتری ممکن است غیرفعال شود.";

        await RECORDTENANTOWNERWALLETLEDGERASYNC(
            order,
            WalletLedgerDirections.Debit,
            order.BaseCostToman,
            botBefore,
            negativeBotAfter,
            WalletLedgerReasons.TenantCardBaseCost,
            provider: order.PaymentProvider,
            referenceType,
            description: "کسر هزینه پایه فروش کارت‌به‌کارت tenant با منفی شدن کیف پول ربات",
            cancellationToken);

        return TenantOwnerWalletSettlementResult.Create(
            TenantOwnerWalletSources.NegativeBotWallet,
            -order.BaseCostToman,
            botBefore,
            negativeBotAfter,
            siteBefore,
            negativeSiteAfter,
            warning);
    }

    /// <summary>
    /// Records the wallet ledger row for a tenant owner settlement.
    /// </summary>
    /// <param name="order">Tenant order that caused the financial movement.</param>
    /// <param name="direction">Ledger direction, credit or debit.</param>
    /// <param name="amountToman">Positive amount in toman to record. Zero values are ignored by the ledger service.</param>
    /// <param name="beforeBalance">Balance of the selected wallet before the movement.</param>
    /// <param name="afterBalance">Balance of the selected wallet after the movement.</param>
    /// <param name="reason">Business reason key used by wallet history and admin audit.</param>
    /// <param name="provider">Provider/source key, for example <c>tenant_card</c> or <c>gozargah_site_wallet</c>.</param>
    /// <param name="referenceType">Local reference type for the order.</param>
    /// <param name="description">Human-readable audit description.</param>
    /// <param name="cancellationToken">Cancellation token for users.db insert.</param>
    /// <returns>A task that completes after the ledger row is saved or skipped for a zero amount.</returns>
    private Task RECORDTENANTOWNERWALLETLEDGERASYNC(
        TenantBotOrder order,
        string direction,
        long amountToman,
        long beforeBalance,
        long afterBalance,
        string reason,
        string provider,
        string referenceType,
        string description,
        CancellationToken cancellationToken)
    {
        return _walletLedgerService.RecordAsync(
            order.OwnerTelegramUserId,
            direction,
            amountToman,
            beforeBalance,
            afterBalance,
            reason,
            provider: provider,
            referenceType: referenceType,
            referenceId: order.Id.ToString(CultureInfo.InvariantCulture),
            orderId: order.OrderId,
            description: description,
            ownerTelegramUserId: order.OwnerTelegramUserId,
            counterpartyTelegramUserId: order.CustomerTelegramUserId,
            botId: order.TenantBotId,
            botUsername: order.TenantBotUsername,
            botType: BotInstanceTypes.Tenant,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads a display-only Gozargah website wallet snapshot for tenant owner audit logs.
    /// </summary>
    /// <param name="ownerTelegramUserId">Telegram user id of the tenant owner whose website wallet should be checked.</param>
    /// <param name="cancellationToken">Cancellation token for the site-wallet eligibility lookup.</param>
    /// <returns>
    /// A snapshot containing either the current site-wallet balance or a short display status such as
    /// <c>متصل نشده</c> or <c>در دسترس نیست</c>.
    /// </returns>
    /// <remarks>
    /// The lookup uses an amount of zero so it never reserves or debits money. It is used only for logs and owner
    /// messages; actual site-wallet debits still call the dedicated debit endpoint after account delivery succeeds.
    /// </remarks>
    private async Task<TenantSiteWalletSnapshot> GETTENANTOWNERSITEWALLETSNAPSHOTASYNC(
        long ownerTelegramUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var eligibility = await _gozargahSiteSyncService.CheckSiteWalletEligibilityAsync(
                ownerTelegramUserId,
                0,
                cancellationToken);

            if (eligibility.User != null ||
                eligibility.CanUse ||
                string.Equals(eligibility.Message, "موجودی کیف پول سایت کافی نیست.", StringComparison.OrdinalIgnoreCase))
            {
                return TenantSiteWalletSnapshot.Connected(eligibility.WalletToman);
            }

            return TenantSiteWalletSnapshot.Unavailable(NORMALIZEGOZARGAHSITEWALLETSTATUS(eligibility.Message));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tenant owner site-wallet snapshot failed. owner={OwnerTelegramUserId}", ownerTelegramUserId);
            return TenantSiteWalletSnapshot.Unavailable("در دسترس نیست");
        }
    }

    /// <summary>
    /// Normalizes website wallet lookup messages for tenant audit display.
    /// </summary>
    /// <param name="message">Raw user-facing message returned by the Gozargah wallet eligibility helper.</param>
    /// <returns>A short Persian status safe for tenant sale logs.</returns>
    private static string NORMALIZEGOZARGAHSITEWALLETSTATUS(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "در دسترس نیست";

        return message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("پیدا نشد", StringComparison.OrdinalIgnoreCase)
            ? "متصل نشده"
            : "در دسترس نیست";
    }

    /// <summary>
    /// Detects XUI or HTTP timeout errors that should keep tenant fulfillment retryable.
    /// </summary>
    /// <param name="error">
    /// Exception or message returned by the XUI creation path. It may be a <see cref="TaskCanceledException"/>,
    /// <see cref="TimeoutException"/>, Telegram request timeout, or plain provider message.
    /// </param>
    /// <returns>
    /// <c>true</c> when the error indicates an external timeout and the order should remain retryable; otherwise
    /// <c>false</c> for definitive validation or business failures.
    /// </returns>
    /// <remarks>
    /// This helper deliberately does not classify every exception as retryable. Duplicate-safe retry is only offered
    /// for timeout-style failures where the payment is accepted but the panel did not answer in time.
    /// </remarks>
    private static bool IsTenantFulfillmentTimeout(object error)
    {
        if (error is TimeoutException || error is TaskCanceledException)
            return true;

        var message = error switch
        {
            Exception ex => ex.Message,
            string text => text,
            _ => null
        } ?? string.Empty;

        return message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NOTIFIES the colleague owner through the default Bot that A tenant storefront sale was completed.
    /// </summary>
    /// <param name="order">fulfilled tenant order with sale, cost, profit, and balance fields.</param>
    /// <param name="owner">Credential record for the colleague who owns the tenant Bot.</param>
    /// <param name="customer">Credential record for the Buyer.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram delivery.</param>
    /// <param name="settlement">
    /// Optional settlement details used to show which wallet paid or received money. Null is allowed for legacy
    /// callers and shows only the order's persisted bot-wallet balances.
    /// </param>
    private async Task NOTIFYTENANTOWNERSUCCESSASYNC(
        TenantBotOrder order,
        CredUser owner,
        CredUser customer,
        CancellationToken CancellationToken,
        TenantOwnerWalletSettlementResult settlement = null)
    {
        try
        {
            var settlementText = settlement == null
                ? string.Empty
                : "\n" +
                  $"منبع تسویه: <code>{Html(settlement.SourceDisplayName)}</code>\n" +
                  $"کیف پول سایت قبل: <code>{Html(FormatTenantSiteWalletSnapshot(settlement.SiteWalletBefore))}</code>\n" +
                  $"کیف پول سایت بعد: <code>{Html(FormatTenantSiteWalletSnapshot(settlement.SiteWalletAfter))}</code>" +
                  (string.IsNullOrWhiteSpace(settlement.WarningMessage) ? string.Empty : $"\n\n{Html(settlement.WarningMessage)}");

            var botClient = _botClientProvider.GetClient(_botRegistry.DefaultBot.Id);
            await botClient.SendTextMessageAsync(
                owner.ChatID == 0 ? owner.TelegramUserId : owner.ChatID,
                "✅ فروش ربات فروشگاهی انجام شد.\n\n" +
                $"ربات: @{Html(order.TenantBotUsername)}\n" +
                $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
                $"مبلغ فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
                $"هزینه همکار: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
                $"سود اضافه‌شده به موجودی شما: <code>{Html(order.ProfitToman.FormatCurrency())}</code>\n" +
                $"موجودی قبل: <code>{Html(order.OwnerBalanceBefore?.FormatCurrency())}</code>\n" +
                $"موجودی بعد: <code>{Html(order.OwnerBalanceAfter?.FormatCurrency())}</code>" +
                settlementText,
                parseMode: ParseMode.Html,
                cancellationToken: CancellationToken);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Writes the operational payment/account log for A tenant storefront order.
    /// </summary>
    /// <param name="order">tenant order being logged.</param>
    /// <param name="owner">colleague owner shown in the log.</param>
    /// <param name="customer">customer shown in the log.</param>
    /// <param name="Source">settlement Source such as ipn, manual check, or Exception path.</param>
    /// <param name="result">final local result of the order processing Attempt.</param>
    /// <param name="settlement">
    /// Optional owner settlement result. Successful tenant fulfillment passes this value so the audit log can show
    /// bot-wallet and Gozargah website-wallet before/after values without adding new database columns.
    /// </param>
    private void LOGTENANTORDER(
        TenantBotOrder order,
        CredUser owner,
        CredUser customer,
        string Source,
        string result,
        TenantOwnerWalletSettlementResult settlement = null)
    {
        var settlementText = settlement == null
            ? string.Empty
            : $"منبع تسویه همکار: <code>{Html(settlement.SourceDisplayName)}</code>\n" +
              $"کیف پول ربات قبل: <code>{Html(settlement.BotWalletBefore.FormatCurrency())}</code>\n" +
              $"کیف پول ربات بعد: <code>{Html(settlement.BotWalletAfter.FormatCurrency())}</code>\n" +
              $"کیف پول سایت قبل: <code>{Html(FormatTenantSiteWalletSnapshot(settlement.SiteWalletBefore))}</code>\n" +
              $"کیف پول سایت بعد: <code>{Html(FormatTenantSiteWalletSnapshot(settlement.SiteWalletAfter))}</code>\n" +
              (string.IsNullOrWhiteSpace(settlement.WarningMessage)
                  ? string.Empty
                  : $"هشدار: <code>{Html(settlement.WarningMessage)}</code>\n");

        var Message = "📌 فروش ربات فروشگاهی\n\n" +
                      $"نتیجه: <code>{Html(result)}</code>\n" +
                      $"منبع تایید: <code>{Html(Source)}</code>\n" +
                      $"ربات tenant: <code>{Html(order.TenantBotId)}</code> @{Html(order.TenantBotUsername)}\n\n" +
                      BuildTenantBotPurchaseLogContext(order) +
                      "📌 مالک فروشگاه\n" +
                      $"{TelegramUserLinkFormatter.HtmlSummary(owner)}\n\n" +
                      "📌 مشتری\n" +
                      $"{TelegramUserLinkFormatter.HtmlSummary(customer)}\n\n" +
                      $"order Id: <code>{Html(order.OrderId)}</code>\n" +
                      $"فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
                      $"هزینه پایه: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
                      $"سود همکار: <code>{Html(order.ProfitToman.FormatCurrency())}</code>\n" +
                      settlementText +
                      $"اکانت: <code>{Html(order.CreatedAccountEmail)}</code>" +
                      BuildTenantOrderErrorLine(order);

        _logger.LogPayment(Message);
    }

    /// <summary>
    /// Formats a Gozargah website wallet snapshot for tenant owner messages and private audit logs.
    /// </summary>
    /// <param name="snapshot">
    /// Snapshot returned by <see cref="GETTENANTOWNERSITEWALLETSNAPSHOTASYNC(long, CancellationToken)"/> or by a
    /// successful website-wallet debit.
    /// </param>
    /// <returns>
    /// Formatted balance in toman when the website wallet is connected; otherwise a short status such as
    /// <c>متصل نشده</c> or <c>در دسترس نیست</c>.
    /// </returns>
    private static string FormatTenantSiteWalletSnapshot(TenantSiteWalletSnapshot snapshot)
    {
        if (snapshot?.IsConnected == true)
            return snapshot.WalletToman.GetValueOrDefault().FormatCurrency();

        return string.IsNullOrWhiteSpace(snapshot?.StatusText) ? "در دسترس نیست" : snapshot.StatusText;
    }

    /// <summary>
    /// Clears stale tenant-order fulfillment errors after an order is successfully delivered.
    /// </summary>
    /// <param name="order">
    /// Tracked tenant order that has just been marked fulfilled. The method clears the order error and, when a
    /// manual receipt is linked, clears the tracked receipt error as well.
    /// </param>
    /// <param name="CancellationToken">
    /// Cancellation token for the optional receipt lookup in users.db.
    /// </param>
    /// <returns>
    /// A task that completes after the tracked order and optional receipt have been updated in memory. The caller
    /// remains responsible for the surrounding <see cref="DbContext.SaveChangesAsync(CancellationToken)" /> call.
    /// </returns>
    /// <remarks>
    /// Tenant fulfillment can time out on the first attempt and later succeed from a retry, owner confirmation, or
    /// super-admin recovery path. This helper prevents that old timeout text from appearing in successful order
    /// details, Sales Assistant details, or central purchase logs after the account is actually delivered.
    /// </remarks>
    private async Task CLEARTENANTORDERFULFILLMENTERRORASYNC(
        TenantBotOrder order,
        CancellationToken CancellationToken)
    {
        order.ErrorMessage = null;

        var receipt = order.ManualReceiptId.HasValue
            ? await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(
                x => x.Id == order.ManualReceiptId.Value,
                CancellationToken)
            : await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(
                x => x.TenantBotOrderId == order.Id || x.OrderId == order.OrderId,
                CancellationToken);

        if (receipt != null)
            receipt.ErrorMessage = null;
    }

    /// <summary>
    /// Builds the optional HTML error line for tenant order detail and audit messages.
    /// </summary>
    /// <param name="order">
    /// Tenant order whose latest delivery state controls whether an error should be displayed.
    /// </param>
    /// <returns>
    /// An HTML-safe line break plus error text when the order is still unfulfilled and has an error; otherwise an
    /// empty string so stale timeout errors are hidden after successful fulfillment.
    /// </returns>
    /// <remarks>
    /// Financial and fulfillment logs should not keep showing historical timeout text once a later retry has
    /// delivered the account. Failure paths still show the latest error for troubleshooting.
    /// </remarks>
    private static string BuildTenantOrderErrorLine(TenantBotOrder order)
    {
        return order.IsFulfilled || string.IsNullOrWhiteSpace(order.ErrorMessage)
            ? string.Empty
            : $"\nخطا: <code>{Html(order.ErrorMessage)}</code>";
    }

    /// <summary>
    /// Builds the tenant bot metadata block added to tenant sale and renewal logs.
    /// </summary>
    /// <param name="order">
    /// Tenant order whose <see cref="TenantBotOrder.TenantBotId"/> identifies the storefront that received the
    /// customer payment.
    /// </param>
    /// <returns>
    /// HTML-safe lines containing bot id, username, brand, type, forced-join channels, and support contact for the
    /// storefront. Missing runtime settings are represented with <c>-</c>.
    /// </returns>
    /// <remarks>
    /// Tenant payment settlement can happen from IPN or Sales Assistant callbacks where the customer update context is
    /// not active. Reading the runtime registry from the order id keeps central purchase logs attributable to the
    /// storefront even outside a normal tenant Telegram update.
    /// </remarks>
    private string BuildTenantBotPurchaseLogContext(TenantBotOrder order)
    {
        var bot = _botRegistry.GetById(order?.TenantBotId);
        var username = string.IsNullOrWhiteSpace(bot?.Username)
            ? order?.TenantBotUsername
            : bot.Username.Trim().TrimStart('@');

        var builder = new StringBuilder();
        builder.AppendLine($"BotId: <code>{Html(order?.TenantBotId)}</code>");
        builder.AppendLine($"BotUsername: {FormatTelegramLogReference(string.IsNullOrWhiteSpace(username) ? null : "@" + username)}");
        builder.AppendLine($"Brand: <code>{Html(bot?.BrandName)}</code>");
        builder.AppendLine($"BotType: <code>{Html(bot?.Type ?? BotInstanceTypes.Tenant)}</code>");
        builder.AppendLine($"Channels: {FormatTelegramLogReferences(bot?.TenantChannelIds)}");
        builder.AppendLine($"Support: {FormatTelegramLogReference(bot?.SupportAccount)}");
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>
    /// Formats multiple Telegram references for tenant purchase logs.
    /// </summary>
    /// <param name="references">Tenant forced-join channels or related Telegram references.</param>
    /// <returns>Comma-separated HTML-safe references, or <c>-</c> when no references are configured.</returns>
    private static string FormatTelegramLogReferences(IEnumerable<string> references)
    {
        var formatted = (references ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(FormatTelegramLogReference)
            .ToArray();
        return formatted.Length == 0 ? "<code>-</code>" : string.Join(", ", formatted);
    }

    /// <summary>
    /// Formats one Telegram username, t.me link, private id, or free-form value for tenant purchase logs.
    /// </summary>
    /// <param name="reference">Raw tenant channel or support value.</param>
    /// <returns>Clickable HTML when the value is public Telegram username/link; otherwise HTML-safe text.</returns>
    private static string FormatTelegramLogReference(string reference)
    {
        var value = reference?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "<code>-</code>";

        var normalizedSupport = NormalizeTenantSupportAccount(value);
        if (!string.IsNullOrWhiteSpace(normalizedSupport))
        {
            var username = normalizedSupport.TrimStart('@');
            return $"<a href=\"https://t.me/{HtmlAttribute(username)}\">@{Html(username)}</a>";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "t.me", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "telegram.me", StringComparison.OrdinalIgnoreCase)))
        {
            return $"<a href=\"{HtmlAttribute(uri.ToString())}\">{Html(value)}</a>";
        }

        return long.TryParse(value, out _)
            ? $"<code>{Html(value)}</code>"
            : Html(value);
    }

    /// <summary>
    /// Builds the tariff message shown inside a tenant storefront.
    /// </summary>
    /// <param name="tenant">
    /// Tenant bot whose sale-price markup, enabled payment options, and storefront identity should be reflected
    /// in the message.
    /// </param>
    /// <returns>
    /// HTML-formatted Persian tariff text suitable for Telegram <c>ParseMode.Html</c>. Metered traffic options
    /// below each service's configured <c>minimumTrafficGb</c> are omitted from the visible examples.
    /// </returns>
    /// <remarks>
    /// Tenant pricing uses the same purchase rules as owned bots, then applies the tenant owner's sale-price
    /// policy. The method is presentation-only and does not create orders or modify tenant state.
    /// </remarks>
    private string BUILDTENANTTARIFFSTEXT(BotInstance tenant)
    {
        var Builder = new System.Text.StringBuilder();
        Builder.AppendLine("📋 <b>تعرفه‌های فروشگاه</b>");
        Builder.AppendLine();
        Builder.AppendLine("✨ برای استفاده روزمره، پلن‌های نامحدود با حد مصرف منصفانه پیشنهاد می‌شوند.");
        Builder.AppendLine("🌍 لوکیشن‌های فعال فعلی: آلمان، آمریکا و فنلاند. لوکیشن‌های بیشتری هم به‌زودی اضافه می‌شود.");
        Builder.AppendLine();

        foreach (var service in _purchaseService.GetEnabledServices())
        {
            Builder.AppendLine($"🔹 <b>{Html(service.DisplayName)}</b>");
            if (service.IsUnlimited)
            {
                foreach (var plan in service.UnlimitedPlans.Where(x => x.IsEnabled).OrderBy(x => x.Days))
                {
                    var selection = new XuiV3PurchaseSelection { ServiceKey = service.Key, UnlimitedPlanKey = plan.Key };
                    var Price = CalculateTenantPrice(tenant, selection).SalePriceToman;
                    Builder.AppendLine($"• {Html(plan.DisplayName)} | حد مصرف منصفانه <code>{plan.FairUsageGb} GB</code> | کاربر مجاز <code>{plan.MaxUsers}</code> | <b>{Html(Price.FormatCurrency())}</b>");
                }
            }
            else
            {
                var visibleTrafficOptions = XuiV3PurchaseService.GetVisibleTrafficOptions(service);
                var traffic = string.Join("، ", visibleTrafficOptions.Select(x => $"{x}GB"));
                Builder.AppendLine($"• حجم‌ها: <code>{Html(traffic)}</code>");
                foreach (var duration in service.DurationOptions.OrderBy(x => x.Days))
                {
                    var sampleTraffic = visibleTrafficOptions.FirstOrDefault();
                    if (sampleTraffic <= 0)
                        continue;
                    var selection = new XuiV3PurchaseSelection { ServiceKey = service.Key, TrafficGb = sampleTraffic, DurationKey = duration.Key };
                    var samplePrice = CalculateTenantPrice(tenant, selection).SalePriceToman;
                    Builder.AppendLine($"• {Html(duration.DisplayName)} | نمونه {sampleTraffic}GB: <b>{Html(samplePrice.FormatCurrency())}</b>");
                }
            }

            Builder.AppendLine();
        }

        Builder.AppendLine("💡 برای شرایط قطعی یا اختلال شدید اینترنت، داشتن یک کانفیگ نت ملی با زمان انقضای نامحدود هم توصیه می‌شود.");
        return Builder.ToString();
    }

    /// <summary>
    /// Builds the payment INSTRUCTION Text for A tenant order after the HooshPay invoice is created.
    /// </summary>
    /// <param name="order">tenant order containing sale Price and public order Id.</param>
    /// <param name="payment">HooshPay payment row containing Payable amount, fee, and invoice Data.</param>
    /// <returns>Html-formatted payment Text for the customer.</returns>
    private string BuildTenantPaymentText(TenantBotOrder order, HooshPayPaymentInfo payment)
    {
        var Payable = payment.PayableAmountToman > 0 ? payment.PayableAmountToman : payment.AmountToman + payment.FeeAmountToman;
        return "✅ فاکتور پرداخت ساخته شد.\n\n" +
               $"مبلغ سفارش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
               $"مبلغ قابل پرداخت با کارمزد درگاه: <code>{Html(Payable.FormatCurrency())}</code>\n" +
               $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n\n" +
               "پس از پرداخت موفق، اکانت شما به صورت خودکار ساخته و ارسال می‌شود. اگر پرداخت انجام شد و پیام ساخت اکانت را نگرفتید، دکمه بررسی وضعیت را بزنید.";
    }

    /// <summary>
    /// Builds the payment-provider choice text for a tenant renewal order.
    /// </summary>
    /// <param name="order">Pending tenant renewal order.</param>
    /// <returns>HTML-formatted Telegram text explaining the renewal order and amount.</returns>
    private static string BuildTenantRenewOrderPaymentChoiceText(TenantBotOrder order)
    {
        return "💳 <b>روش پرداخت تمدید را انتخاب کنید</b>\n\n" +
               $"اکانت: <code>{Html(order.TargetAccountEmail)}</code>\n" +
               $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
               $"مبلغ تمدید: <b>{Html(order.SalePriceToman.FormatCurrency())}</b>\n\n" +
               "قیمت تمدید دقیقاً مثل قیمت خرید همین پلن در فروشگاه محاسبه شده است.";
    }

    /// <summary>
    /// Builds payment provider callbacks for an already-created tenant renewal order.
    /// </summary>
    /// <param name="order">Tenant renewal order whose database id is embedded in callbacks.</param>
    /// <param name="tenant">Tenant bot whose enabled gateway settings decide which buttons are visible.</param>
    /// <returns>Inline keyboard containing enabled payment providers and status check.</returns>
    private static InlineKeyboardMarkup BuildTenantRenewPaymentProviderKeyboard(TenantBotOrder order, BotInstance tenant)
    {
        var rows = new List<InlineKeyboardButton[]>();
        if (tenant.TenantHooshPayEnabled)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("درگاه ریالی هوش‌پی", CUSTOMERCALLBACKPREFIX + $"RNHP:{order.Id}") });
        if (tenant.TenantNowPaymentsEnabled)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("پرداخت ارز دیجیتال", CUSTOMERCALLBACKPREFIX + $"RNNP:{order.Id}") });
        if (tenant.TenantCardPaymentEnabled && !string.IsNullOrWhiteSpace(tenant.TenantCardNumber))
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("کارت‌به‌کارت به فروشگاه", CUSTOMERCALLBACKPREFIX + $"RNCARD:{order.Id}") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بررسی وضعیت سفارش", CUSTOMERCALLBACKPREFIX + $"chk:{order.Id}") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به فروشگاه", CUSTOMERCALLBACKPREFIX + "home") });
        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Builds the customer-FACING invoice Text for tenant orders paid through non-HooshPay GATEWAYS.
    /// </summary>
    /// <param name="order">tenant order containing sale amount and public order Id.</param>
    /// <param name="PROVIDERDISPLAYNAME">Human-readable gateway name, already safe to expose to the customer.</param>
    /// <returns>Html-formatted invoice Text for the storefront customer.</returns>
    private static string BUILDTENANTGATEWAYPAYMENTTEXT(TenantBotOrder order, string PROVIDERDISPLAYNAME)
    {
        return "✅ فاکتور پرداخت ساخته شد.\n\n" +
               $"درگاه: <b>{Html(PROVIDERDISPLAYNAME)}</b>\n" +
               $"مبلغ سفارش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
               $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n\n" +
               "پس از پرداخت موفق، اکانت شما به صورت خودکار ساخته و ارسال می‌شود. اگر پرداخت انجام شد و پیام ساخت اکانت را نگرفتید، دکمه بررسی وضعیت را بزنید.";
    }

    /// <summary>
    /// Builds the inline keyboard for PAYING A tenant order and manually checking its status.
    /// </summary>
    /// <param name="order">tenant order whose Id is EMBEDDED into the status-check callback.</param>
    /// <param name="payment">HooshPay payment Info whose payment URL is EXPOSED as A URL button.</param>
    /// <returns>inline keyboard with payment and status-check actions.</returns>
    private static InlineKeyboardMarkup BuildTenantPaymentKeyboard(TenantBotOrder order, HooshPayPaymentInfo payment)
    {
        var rows = new List<InlineKeyboardButton[]>();
        if (!string.IsNullOrWhiteSpace(payment.PaymentUrl))
            rows.Add(new[] { InlineKeyboardButton.WithUrl("پرداخت", payment.PaymentUrl) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بررسی وضعیت پرداخت", CUSTOMERCALLBACKPREFIX + $"chk:{order.Id}") });
        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Builds the inline keyboard for GATEWAYS whose payment URL is stored directly on the tenant order.
    /// </summary>
    /// <param name="order">tenant order whose Id is EMBEDDED into the manual status-check callback.</param>
    /// <param name="PaymentUrl">external gateway URL; when empty only the status-check button is shown.</param>
    /// <returns>inline keyboard with payment URL and status-check CONTROLS.</returns>
    private static InlineKeyboardMarkup BuildTenantPaymentKeyboard(TenantBotOrder order, string PaymentUrl)
    {
        var rows = new List<InlineKeyboardButton[]>();
        if (!string.IsNullOrWhiteSpace(PaymentUrl))
            rows.Add(new[] { InlineKeyboardButton.WithUrl("پرداخت", PaymentUrl) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بررسی وضعیت پرداخت", CUSTOMERCALLBACKPREFIX + $"chk:{order.Id}") });
        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Builds the inline CONTROLS shown on A tenant card-to-card order.
    /// </summary>
    /// <param name="order">tenant order whose Id is EMBEDDED into receipt upload and status callbacks.</param>
    /// <returns>inline keyboard that lets the customer Send or Replace A receipt and check order status.</returns>
    private static InlineKeyboardMarkup BuildTenantCardPaymentKeyboard(TenantBotOrder order)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("ارسال / تعویض رسید", CUSTOMERCALLBACKPREFIX + $"receipt:{order.Id}") },
            new[] { InlineKeyboardButton.WithCallbackData("بررسی وضعیت سفارش", CUSTOMERCALLBACKPREFIX + $"chk:{order.Id}") },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به فروشگاه", CUSTOMERCALLBACKPREFIX + "home") }
        });
    }

    /// <summary>
    /// edits an existing tenant Menu Message when POSSIBLE; otherwise sends A new one.
    /// </summary>
    /// <param name="botClient">Telegram client for the tenant Bot.</param>
    /// <param name="ChatId">target chat Id.</param>
    /// <param name="MessageId">Message Id to edit; when null A new Message is sent.</param>
    /// <param name="Text">Message body.</param>
    /// <param name="keyboard">inline keyboard attached to the Message.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram calls.</param>
    /// <param name="ParseMode">Telegram Parse Mode used for the Message body.</param>
    private async Task EDITORSENDASYNC(
        ITelegramBotClient botClient,
        ChatId ChatId,
        int? MessageId,
        string Text,
        InlineKeyboardMarkup keyboard,
        CancellationToken CancellationToken,
        ParseMode ParseMode = ParseMode.Html)
    {
        if (MessageId.HasValue)
        {
            await SafeEditMessageTextAsync(botClient, 
                ChatId,
                MessageId.Value,
                Text,
                parseMode: ParseMode,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(ChatId, Text, parseMode: ParseMode, replyMarkup: keyboard, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// CALCULATES customer sale Price, owner base cost, and owner profit for A tenant-storefront selection.
    /// </summary>
    /// <param name="tenant">tenant Bot whose optional markup percent can override the public TARIFF.</param>
    /// <param name="selection">service and plan selection selected by the storefront customer.</param>
    /// <returns>Price BREAKDOWN in toman for order creation and ledger RECORDING.</returns>
    private TenantPriceResult CalculateTenantPrice(BotInstance tenant, XuiV3PurchaseSelection selection)
    {
        var PUBLICRESOLVED = _purchaseService.ResolvePurchase(selection, false);
        var COLLEAGUERESOLVED = _purchaseService.ResolvePurchase(selection, true);
        var BASECOST = COLLEAGUERESOLVED.PriceToman;
        var sale = PUBLICRESOLVED.PriceToman;
        var markup = Math.Max(0, tenant?.TenantPriceMarkupPercent ?? 0);
        // default sale Price is public TARIFF; A custom markup overrides it from colleague base cost.
        if (markup > 0)
            sale = (long)Math.Ceiling(BASECOST * (1M + markup / 100M));

        if (sale < BASECOST)
            sale = BASECOST;

        return new TenantPriceResult
        {
            SalePriceToman = sale,
            BaseCostToman = BASECOST,
            ProfitToman = Math.Max(0, sale - BASECOST)
        };
    }

    /// <summary>
    /// Builds A persisted tenant order from A selected plan and A calculated Price BREAKDOWN.
    /// </summary>
    /// <param name="tenant">tenant Bot that owns the storefront and Receives the financial result.</param>
    /// <param name="customer">credentials profile of the storefront customer.</param>
    /// <param name="ChatId">Telegram chat Id where the customer should Receive payment and fulfillment messages.</param>
    /// <param name="selection">selected service, Traffic, Duration, or Unlimited plan.</param>
    /// <param name="Price">sale Price, base cost, and profit in toman.</param>
    /// <param name="Provider">payment Provider key stored on the order for audit and ledger filtering.</param>
    /// <returns>A new UNSAVED <see cref="TenantBotOrder" /> ready to be added to users.db.</returns>
    /// <remarks>
    /// the order Id is generated before Any gateway request so ASYNCHRONOUS ipn or receipt callbacks can
    /// be CORRELATED with the local tenant order. the returned Entity is not TRACKED UNTIL the caller Adds it.
    /// </remarks>
    private static TenantBotOrder CreateTenantOrder(
        BotInstance tenant,
        CredUser customer,
        ChatId ChatId,
        XuiV3PurchaseSelection selection,
        TenantPriceResult Price,
        string Provider)
    {
        return new TenantBotOrder
        {
            OrderId = CreateTenantOrderId(tenant, customer.TelegramUserId),
            TenantBotId = tenant.Id,
            TenantBotUsername = tenant.Username,
            OwnerTelegramUserId = tenant.OwnerTelegramUserId ?? 0,
            CustomerTelegramUserId = customer.TelegramUserId,
            CustomerChatId = ChatId.Identifier ?? customer.TelegramUserId,
            CustomerUsername = customer.Username,
            CustomerFirstName = customer.FirstName,
            CustomerLastName = customer.LastName,
            OrderKind = TenantBotOrderKinds.Purchase,
            ServiceKey = selection.ServiceKey,
            TrafficGb = selection.TrafficGb,
            DurationKey = selection.DurationKey,
            UnlimitedPlanKey = selection.UnlimitedPlanKey,
            AccountCount = 1,
            SalePriceToman = Price.SalePriceToman,
            BaseCostToman = Price.BaseCostToman,
            ProfitToman = Price.ProfitToman,
            PaymentProvider = Provider,
            PaymentStatus = TenantBotOrderStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Encodes A tenant purchase selection into A compact callback action.
    /// </summary>
    /// <param name="selection">selection to Serialize into callback Data.</param>
    /// <returns>callback suffix used after the tenant customer callback prefix.</returns>
    private static string BUILDPAYACTION(XuiV3PurchaseSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.UnlimitedPlanKey))
            return $"Pay:u:{selection.ServiceKey}:{selection.UnlimitedPlanKey}";

        return $"Pay:m:{selection.ServiceKey}:{selection.TrafficGb}:{selection.DurationKey}";
    }

    /// <summary>
    /// Parses the compact callback action generated by <see cref="BUILDPAYACTION"/> back into A purchase selection.
    /// </summary>
    /// <param name="action">callback action string received from Telegram.</param>
    /// <returns>purchase selection when the callback is valid; otherwise null.</returns>
    private static XuiV3PurchaseSelection PARSESELECTIONFROMPAYACTION(string action)
    {
        var parts = action.Split(':');
        if (parts.Length == 4 && parts[1] == "u")
            return new XuiV3PurchaseSelection { ServiceKey = parts[2], UnlimitedPlanKey = parts[3] };

        if (parts.Length == 5 && parts[1] == "m" && int.TryParse(parts[3], out var GB))
            return new XuiV3PurchaseSelection { ServiceKey = parts[2], TrafficGb = GB, DurationKey = parts[4] };

        return null;
    }

    /// <summary>
    /// Loads the tenant Bot row that matches the current async Bot context.
    /// </summary>
    /// <param name="CancellationToken">Cancellation Token for the database Query.</param>
    /// <returns>tenant Bot row for the current Bot Id, or null when the update is not from A tenant Bot.</returns>
    private async Task<BotInstance> GetCurrentTenantBotAsync(CancellationToken CancellationToken)
    {
        return await _userDbcontext.BotInstances.FirstOrDefaultAsync(x => x.Id == BotContextAccessor.CurrentBotId, CancellationToken);
    }

    /// <summary>
    /// Loads the tenant storefront owned by A colleague.
    /// </summary>
    /// <param name="OwnerTelegramUserId">Telegram User Id of the colleague owner.</param>
    /// <param name="CancellationToken">Cancellation Token for the database Query.</param>
    /// <returns>the owner's tenant Bot row, or null if the owner has not created one yet.</returns>
    private async Task<BotInstance> GETTENANTBOTBYOWNERASYNC(long OwnerTelegramUserId, CancellationToken CancellationToken)
    {
        return await _userDbcontext.BotInstances.FirstOrDefaultAsync(
            x => x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == OwnerTelegramUserId,
            CancellationToken);
    }

    /// <summary>
    /// returns the colleague's tenant Bot row, creating A Disabled DRAFT row when it does not exist.
    /// </summary>
    /// <param name="owner">Credential User record for the colleague who owns the storefront.</param>
    /// <param name="CancellationToken">Cancellation Token for the database operation.</param>
    /// <returns>existing or newly-created tenant Bot row.</returns>
    private async Task<BotInstance> GETORCREATETENANTBOTASYNC(CredUser owner, CancellationToken CancellationToken)
    {
        var Id = BUILDTENANTBOTID(owner.TelegramUserId);
        var tenant = await _userDbcontext.BotInstances.FirstOrDefaultAsync(x => x.Id == Id, CancellationToken);
        if (tenant != null)
            return tenant;

        // stable tenant Id is DERIVED from owner Id so each colleague Gets exactly one storefront Bot.
        var current = _botContextAccessor.Current?.Config;
        tenant = new BotInstance
        {
            Id = Id,
            Type = BotInstanceTypes.Tenant,
            Enabled = false,
            IsDefault = false,
            OwnerTelegramUserId = owner.TelegramUserId,
            BrandName = owner.Username ?? owner.TelegramUserId.ToString(),
            SupportAccount = string.IsNullOrWhiteSpace(owner.Username) ? null : "@" + owner.Username.TrimStart('@'),
            LoggerChannel = current?.LoggerChannel,
            BackupChannel = current?.BackupChannel,
            ChannelIdsJson = BotInstanceConfigExtensions.SerializeStringArray(Array.Empty<string>()),
            IosTutorialJson = BotInstanceConfigExtensions.SerializeStringArray(Array.Empty<string>()),
            AndroidTutorialJson = BotInstanceConfigExtensions.SerializeStringArray(Array.Empty<string>()),
            WindowsTutorialJson = BotInstanceConfigExtensions.SerializeStringArray(Array.Empty<string>()),
            TenantTutorialsJson = JsonConvert.SerializeObject(Array.Empty<TenantTutorialLink>()),
            CreatedAtUtc = DateTime.UtcNow
        };

        _userDbcontext.BotInstances.Add(tenant);
        await _userDbcontext.SaveChangesAsync(CancellationToken);
        return tenant;
    }

    /// <summary>
    /// ANSWERS A Telegram callback without ALLOWING Expired callback ids to Escape and stop the update pipeline.
    /// </summary>
    /// <param name="botClient">Telegram Bot client that received the callback Query.</param>
    /// <param name="callbackQueryId">OPAQUE callback Query Id from Telegram; valid only for A short time.</param>
    /// <param name="Text">optional ALERT or TOAST Text shown to the User.</param>
    /// <param name="showAlert">whether Telegram should show the Text as an ALERT DIALOG instead of A TOAST.</param>
    /// <param name="URL">optional URL Supported by Telegram callback ANSWERS.</param>
    /// <param name="CACHETIME">optional Telegram callback Cache time in seconds.</param>
    /// <param name="CancellationToken">Cancellation Token for the Telegram API call.</param>
    /// <remarks>
    /// Telegram returns <c>Bad request: Query is too old</c> when A callback is ANSWERED after its short TTL.
    /// that CONDITION is not A Business failure and must Never stop owned Bot or tenant Bot receivers.
    /// </remarks>
    private async Task SafeAnswerCallbackQueryAsync(
        ITelegramBotClient botClient,
        string callbackQueryId,
        string text = null,
        bool? showAlert = null,
        string url = null,
        int? cacheTime = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await botClient.AnswerCallbackQueryAsync(
                callbackQueryId,
                text,
                showAlert,
                url,
                cacheTime,
                cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
                                            (ex.Message.Contains("Query is too old", StringComparison.OrdinalIgnoreCase) ||
                                             ex.Message.Contains("Query Id is invalid", StringComparison.OrdinalIgnoreCase) ||
                                             ex.Message.Contains("response Timeout Expired", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                ex,
                "IGNORING STALE Telegram callback answer. callbackQueryId={callbackQueryId}",
                callbackQueryId);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Telegram callback answer failed but was SWALLOWED to Keep the receiver ALIVE. callbackQueryId={callbackQueryId}",
                callbackQueryId);
        }
    }

    /// <summary>
    /// edits A Telegram Message without ALLOWING HARMLESS edit failures to stop the Bot receiver.
    /// </summary>
    /// <param name="botClient">Telegram Bot client that owns the Message.</param>
    /// <param name="ChatId">Telegram chat Id containing the Message to edit.</param>
    /// <param name="MessageId">Telegram Message Id to edit.</param>
    /// <param name="Text">new Message Text.</param>
    /// <param name="ParseMode">optional Parse Mode used for the edited Text.</param>
    /// <param name="replyMarkup">optional inline keyboard for the edited Message.</param>
    /// <param name="CancellationToken">Cancellation Token for the Telegram API call.</param>
    /// <remarks>
    /// Telegram rejects edits when the new content and markup are IDENTICAL to the current Message.
    /// that is A no-OP from our Business PERSPECTIVE and must not break owned Bot or tenant Bot flows.
    /// </remarks>
    private async Task SafeEditMessageTextAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await botClient.EditMessageTextAsync(
                chatId,
                messageId,
                text,
                parseMode: parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
                                            (ex.Message.Contains("Message is not modified", StringComparison.OrdinalIgnoreCase) ||
                                             ex.Message.Contains("Message to edit not found", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                ex,
                "IGNORING non-CRITICAL Telegram edit failure. ChatId={ChatId}, MessageId={MessageId}",
                chatId,
                messageId);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Telegram Message edit failed but was SWALLOWED to Keep the receiver ALIVE. ChatId={ChatId}, MessageId={MessageId}",
                chatId,
                messageId);
        }
    }

    /// <summary>
    /// checks whether A tenant customer SATISFIES the storefront forced-join RULE before CONTINUING the Flow.
    /// </summary>
    /// <param name="botClient">tenant Bot client used to call Telegram and Send the join PROMPT.</param>
    /// <param name="Message">Incoming customer Message that identifies the Telegram User and chat.</param>
    /// <param name="tenant">current tenant Bot whose channel List owns the forced-join RULE.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram API calls.</param>
    /// <returns>true when forced join is Disabled or the User is A channel member; false after sending A join PROMPT.</returns>
    /// <remarks>
    /// Telegram may return "member List is inaccessible" when the Bot is not an admin in the tenant channel.
    /// that Error is handled as A failed check so the tenant storefront does not CRASH the whole receiver.
    /// </remarks>
    private async Task<bool> EnsureTenantCustomerJoinAsync(
        ITelegramBotClient botClient,
        Message Message,
        BotInstance tenant,
        CancellationToken CancellationToken)
    {
        return await EnsureTenantCustomerJoinAsync(
            botClient,
            Message.Chat.Id,
            Message.From.Id,
            tenant,
            CancellationToken);
    }

    /// <summary>
    /// Checks the tenant forced-join rule for either a message or a callback query.
    /// </summary>
    /// <param name="botClient">Tenant bot client used to call Telegram and send the join prompt.</param>
    /// <param name="chatId">
    /// Telegram chat id where the customer should receive the join prompt. This is usually the private chat
    /// with the tenant bot, not the forced-join channel id.
    /// </param>
    /// <param name="telegramUserId">
    /// Numeric Telegram user id whose channel membership is checked with <c>GetChatMember</c>.
    /// Usernames or chat ids must not be passed here.
    /// </param>
    /// <param name="tenant">
    /// Tenant bot that owns the forced-join channel list. Only this tenant's configured channels are checked.
    /// </param>
    /// <param name="CancellationToken">Cancellation token for Telegram API calls and prompt delivery.</param>
    /// <param name="isJoinRetry">
    /// <c>true</c> when the customer clicked the explicit "عضو شدم" button; the rejection message is then
    /// phrased as a retry result instead of a first-time join prompt.
    /// </param>
    /// <returns>
    /// <c>true</c> when forced join is disabled, no channel is configured, or the user is a member of every
    /// configured channel; otherwise <c>false</c> after a join prompt has been sent.
    /// </returns>
    /// <remarks>
    /// This method is intentionally used before every tenant customer message and callback action. It prevents
    /// old inline buttons, payment checks, receipt uploads, and menu callbacks from bypassing mandatory join.
    /// Telegram access errors are treated as a failed check so the storefront remains closed instead of
    /// crashing or silently allowing the customer through.
    /// </remarks>
    private async Task<bool> EnsureTenantCustomerJoinAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        long telegramUserId,
        BotInstance tenant,
        CancellationToken CancellationToken,
        bool isJoinRetry = false)
    {
        if (!tenant.TenantMandatoryJoinEnabled)
            return true;

        var Channels = GETTENANTCHANNELIDS(tenant).ToList();
        if (Channels.Count == 0)
            return true;

        foreach (var channel in Channels)
        {
            try
            {
                var member = await botClient.GetChatMemberAsync(channel, telegramUserId, CancellationToken);
                if (member.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
                    continue;

                await SENDTENANTJOINPROMPTASYNC(
                    botClient,
                    chatId,
                    Channels,
                    CancellationToken,
                    isJoinRetry ? "هنوز عضو کانال نشده‌اید. بعد از عضویت دوباره روی «عضو شدم» بزنید." : null);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "tenant forced-join check failed. TenantBotId={TenantBotId}, channel={channel}", tenant.Id, channel);
                await SENDTENANTJOINPROMPTASYNC(
                    botClient,
                    chatId,
                    Channels,
                    CancellationToken,
                    "امکان تایید عضویت شما وجود ندارد. لطفاً مطمئن شوید در کانال عضو شده‌اید و دوباره تلاش کنید.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies that the tenant Bot can read members of its configured forced-join Channels.
    /// </summary>
    /// <param name="tenant">tenant Bot whose Token and channel List should be VALIDATED.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram API calls.</param>
    /// <returns>A validation result containing an ACTIONABLE owner-FACING Error when Access is missing.</returns>
    /// <remarks>
    /// the check uses the tenant Bot Token, not the default owned Bot. this GUARANTEES the same Bot that SERVES
    /// customers can LATER call GETCHATMEMBER for the configured channel.
    /// </remarks>
    private async Task<(bool ISVALID, string ErrorMessage)> VALIDATETENANTMANDATORYJOINASYNC(BotInstance tenant, CancellationToken CancellationToken)
    {
        var Channels = GETTENANTCHANNELIDS(tenant).ToList();
        if (Channels.Count == 0)
            return (false, "اول کانال جوین اجباری را ثبت کنید.");

        if (string.IsNullOrWhiteSpace(tenant.Token))
            return (false, "اول توکن ربات فروشگاهی را ثبت کنید.");

        try
        {
            var client = _botClientProvider.GetClient(tenant.Id);
            var me = await client.GetMeAsync(CancellationToken);
            foreach (var channel in Channels)
                await client.GetChatMemberAsync(channel, me.Id, CancellationToken);

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tenant forced-join validation failed. TenantBotId={TenantBotId}", tenant.Id);
            return (false, "ربات فروشگاهی به member List کانال دسترسی ندارد. ربات را به کانال اضافه و admin کنید.");
        }
    }

    /// <summary>
    /// sends A tenant customer the channel buttons required for forced join.
    /// </summary>
    /// <param name="botClient">tenant Bot client used to Send the PROMPT.</param>
    /// <param name="ChatId">customer chat Id.</param>
    /// <param name="Channels">tenant channel ids or USERNAMES configured by the owner.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram delivery.</param>
    /// <param name="PromptText">
    /// Optional customer-facing text used when a retry or Telegram access error needs a more specific message.
    /// When null, the default first-time join prompt is sent.
    /// </param>
    private static async Task SENDTENANTJOINPROMPTASYNC(
        ITelegramBotClient botClient,
        ChatId ChatId,
        IReadOnlyCollection<string> Channels,
        CancellationToken CancellationToken,
        string PromptText = null)
    {
        var rows = Channels
            .Select(channel =>
            {
                var URL = channel.StartsWith("@", StringComparison.Ordinal)
                    ? $"HTTPS://t.me/{channel.TrimStart('@')}"
                    : channel;
                return new[] { InlineKeyboardButton.WithUrl("عضویت در کانال", URL) };
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("عضو شدم", CUSTOMERCALLBACKPREFIX + "joincheck") })
            .ToArray();

        await botClient.SendTextMessageAsync(
            ChatId,
            PromptText ?? "برای استفاده از فروشگاه ابتدا در کانال معرفی‌شده عضو شوید و سپس دوباره تلاش کنید.",
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: CancellationToken);
    }

    /// <summary>
    /// reads the tenant forced-join channel List from its Json Column.
    /// </summary>
    /// <param name="tenant">tenant Bot whose channel settings should be Parsed.</param>
    /// <returns>A non-null SEQUENCE of normalized channel USERNAMES, ids, or URLs.</returns>
    private static IEnumerable<string> GETTENANTCHANNELIDS(BotInstance tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant?.TenantChannelIdsJson))
            return Array.Empty<string>();

        try
        {
            return JsonConvert.DeserializeObject<List<string>>(tenant.TenantChannelIdsJson)?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Fulfills a paid tenant renewal order by updating the existing XUI client instead of creating a new one.
    /// </summary>
    /// <param name="order">Paid tenant order whose <see cref="TenantBotOrder.OrderKind"/> is <c>renew</c>.</param>
    /// <param name="owner">Tenant owner whose wallet receives profit or is debited for card-to-card base cost.</param>
    /// <param name="customer">Tenant customer who owns the target account.</param>
    /// <param name="tenant">Tenant bot that owns the storefront.</param>
    /// <param name="selection">Renewal service/plan selection stored on the order.</param>
    /// <param name="source">Settlement source such as IPN, manual check, or assistant confirmation.</param>
    /// <param name="debitOwnerBaseCost">Whether the owner should be debited base cost instead of credited profit.</param>
    /// <param name="cancellationToken">Cancellation token for panel, database, ledger, and Telegram operations.</param>
    /// <returns>
    /// Settlement result describing the owner balance movement or failure reason.
    /// </returns>
    /// <remarks>
    /// This method is called only from the tenant order fulfillment gate after duplicate-order checks.
    /// It updates XUI first; wallet and ledger changes are recorded only after the panel confirms renewal.
    /// </remarks>
    private async Task<NowPaymentsSettlementResult> FULFILLPAIDTENANTRENEWORDERASYNC(
        TenantBotOrder order,
        CredUser owner,
        CredUser customer,
        BotInstance tenant,
        XuiV3PurchaseSelection selection,
        string source,
        bool debitOwnerBaseCost,
        CancellationToken cancellationToken)
    {
        if (await ISTENANTORDERALREADYFULFILLEDASYNC(order, cancellationToken))
            return NowPaymentsSettlementResult.AlreadyAdded(order.OwnerBalanceAfter ?? 0);

        var serverInfo = BuildConfiguredPanelServerInfo();
        var client = await FindTenantClientAsync(serverInfo, order.TargetAccountEmail, cancellationToken);
        if (client == null || !ClientBelongsToTenantCustomer(client, order.CustomerTelegramUserId, order.TenantBotId))
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = "Target account was not found or does not belong to this tenant customer.";
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(cancellationToken);
            await NOTIFYTENANTCUSTOMERFAILUREASYNC(order, "اکانت هدف تمدید پیدا نشد.", cancellationToken);
            LOGTENANTORDER(order, owner, customer, source, "renew-target-not-found");
            return NowPaymentsSettlementResult.NotFound();
        }

        var resolved = _purchaseService.ResolvePurchase(selection, false);
        resolved.PriceToman = order.SalePriceToman;
        var renewal = XuiV3RenewalPolicy.Calculate(client, resolved, "tenant-renew", order.CustomerTelegramUserId);
        var updateResponse = await ApiServicev3.UpdateClientAsync(serverInfo, _configuration, client.Email, renewal.Payload, cancellationToken);
        if (!updateResponse.Success)
        {
            order.PaymentStatus = TenantBotOrderStatuses.Failed;
            order.ErrorMessage = updateResponse.Msg;
            order.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbcontext.SaveChangesAsync(cancellationToken);
            await NOTIFYTENANTCUSTOMERFAILUREASYNC(order, updateResponse.Msg, cancellationToken);
            LOGTENANTORDER(order, owner, customer, source, "renew-update-failed");
            return NowPaymentsSettlementResult.InvalidAmount();
        }

        if (renewal.ShouldResetTraffic)
            await RESETTENANTRENEWEDTRAFFICASYNC(serverInfo, client.Email, cancellationToken);

        client.TotalGB = renewal.Payload.TotalGB;
        client.ExpiryTime = renewal.Payload.ExpiryTime;
        client.Comment = renewal.Payload.Comment;

        var settlement = await SETTLETENANTOWNERWALLETASYNC(
            order,
            owner,
            debitOwnerBaseCost,
            referenceType: "tenant-renew-order",
            cancellationToken);

        order.IsFulfilled = true;
        order.IsOwnerCredited = settlement.OwnerDelta > 0;
        order.OwnerWalletDelta = settlement.OwnerDelta;
        order.OwnerBalanceBefore = settlement.BotWalletBefore;
        order.OwnerBalanceAfter = settlement.BotWalletAfter;
        order.PaymentStatus = TenantBotOrderStatuses.Fulfilled;
        order.FulfillmentSource = source;
        order.CreatedAccountEmail = client.Email;
        order.CreatedSubLink = ApiServicev3.BuildSubscriptionLink(serverInfo, client.SubId ?? client.Email);
        order.CreatedAccountJson = JsonConvert.SerializeObject(new XuiV3AccountCreationResult
        {
            Success = true,
            Email = client.Email,
            SubId = client.SubId,
            SubLink = order.CreatedSubLink,
            TrafficGb = resolved.TrafficGb,
            TrafficBytes = renewal.TargetAvailableTrafficBytes,
            ExpiryTime = renewal.UpdatedExpiryTime,
            DurationDays = renewal.FinalDurationDays,
            Comment = renewal.Payload.Comment
        });
        order.FulfilledAtUtc = DateTime.UtcNow;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await CLEARTENANTORDERFULFILLMENTERRORASYNC(order, cancellationToken);

        _userDbcontext.TenantBotLedgerEntries.Add(new TenantBotLedgerEntry
        {
            TenantBotId = order.TenantBotId,
            TenantBotUsername = order.TenantBotUsername,
            TenantBotOrderId = order.Id,
            OrderId = order.OrderId,
            OwnerTelegramUserId = order.OwnerTelegramUserId,
            CustomerTelegramUserId = order.CustomerTelegramUserId,
            SalePriceToman = order.SalePriceToman,
            BaseCostToman = order.BaseCostToman,
            ProfitToman = settlement.OwnerDelta,
            OwnerBalanceBefore = settlement.BotWalletBefore,
            OwnerBalanceAfter = settlement.BotWalletAfter,
            Description = $"tenant Bot renewal VIA @{order.TenantBotUsername}",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _userDbcontext.SaveChangesAsync(cancellationToken);

        await _gozargahSiteSyncService.QueueUpdateAsync(
            order.OwnerTelegramUserId,
            order.CustomerTelegramUserId,
            client,
            serverInfo,
            order.OrderId,
            order.TenantBotId,
            cancellationToken);

        await SENDTENANTRENEWSUCCESSASYNC(order, renewal, cancellationToken);
        await NOTIFYTENANTOWNERSUCCESSASYNC(order, owner, customer, cancellationToken, settlement);
        await _salesAssistantService.NOTIFYTENANTSALEASYNC(order, settlement.BotWalletBefore, settlement.BotWalletAfter, cancellationToken);
        LOGTENANTORDER(order, owner, customer, source, "renew-fulfilled", settlement);
        return NowPaymentsSettlementResult.Applied(settlement.BotWalletBefore, settlement.BotWalletAfter);
    }

    /// <summary>
    /// Resets traffic counters after a tenant renewal when the shared renewal policy requires it.
    /// </summary>
    /// <param name="serverInfo">Configured XUI v3 panel descriptor.</param>
    /// <param name="email">XUI client email whose counters should be reset.</param>
    /// <param name="cancellationToken">Cancellation token for panel API calls.</param>
    /// <returns>A task that completes after the reset or fallback attempt.</returns>
    private async Task RESETTENANTRENEWEDTRAFFICASYNC(ServerInfo serverInfo, string email, CancellationToken cancellationToken)
    {
        var resetResponse = await ApiServicev3.ResetClientTrafficAsync(serverInfo, _configuration, email, cancellationToken);
        if (resetResponse.Success)
            return;

        await ApiServicev3.UpdateClientTrafficAsync(serverInfo, _configuration, email, 0, 0, cancellationToken);
    }

    /// <summary>
    /// Sends the customer-facing success message for a fulfilled tenant renewal order.
    /// </summary>
    /// <param name="order">Fulfilled tenant renewal order.</param>
    /// <param name="renewal">Calculated renewal details applied to the XUI client.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    private async Task SENDTENANTRENEWSUCCESSASYNC(
        TenantBotOrder order,
        XuiV3RenewalCalculation renewal,
        CancellationToken cancellationToken)
    {
        try
        {
            var text =
                "✅ <b>اکانت شما با موفقیت تمدید شد.</b>\n\n" +
                $"اکانت: <code>{Html(order.CreatedAccountEmail)}</code>\n" +
                $"شماره سفارش: <code>{Html(order.OrderId)}</code>\n" +
                $"مبلغ پرداختی: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
                (renewal.IsUnlimited
                    ? $"حد مصرف منصفانه قابل استفاده: <code>{Html(renewal.TargetAvailableTrafficGb.ToString(CultureInfo.InvariantCulture))} GB</code>\n"
                    : $"حجم تمدید: <code>{Html(renewal.RenewedTrafficGb.ToString(CultureInfo.InvariantCulture))} GB</code>\n") +
                $"مدت نهایی: <code>{Html(renewal.FinalDurationDays <= 0 ? "نامحدود" : renewal.FinalDurationDays + " روز")}</code>\n" +
                $"ساب‌لینک: <code>{Html(order.CreatedSubLink)}</code>";

            await _botClientProvider.GetClient(order.TenantBotId).SendTextMessageAsync(
                order.CustomerChatId,
                text,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant renewal success notification failed. orderId={OrderId}", order.OrderId);
        }
    }

    /// <summary>
    /// Finds an XUI client for tenant renewal from email, UUID, config link, or subscription link input.
    /// </summary>
    /// <param name="serverInfo">Configured XUI v3 panel descriptor.</param>
    /// <param name="input">Customer-provided account identifier.</param>
    /// <param name="cancellationToken">Cancellation token for panel API calls.</param>
    /// <returns>
    /// The matched XUI client, or <c>null</c> when no client matches the normalized identifier or the panel
    /// rejects a direct lookup with a not-found response.
    /// </returns>
    /// <remarks>
    /// This method only locates a candidate. Tenant ownership is checked separately by
    /// <see cref="ClientBelongsToTenantCustomer"/> before renewal is allowed. Direct <c>GET clients/get</c>
    /// calls can return HTTP 404 for unknown emails, so those failures are treated as a miss and the method
    /// falls back to listing clients instead of allowing an exception to stop the tenant receiver.
    /// </remarks>
    private async Task<XuiV3Client> FindTenantClientAsync(ServerInfo serverInfo, string input, CancellationToken cancellationToken)
    {
        var candidate = NormalizeTenantAccountInput(input);
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        try
        {
            var direct = await ApiServicev3.GetClientAsync(serverInfo, _configuration, candidate, cancellationToken);
            if (direct.Success && direct.Obj != null)
                return direct.Obj;
        }
        catch (XuiV3ApiException ex) when (ex.StatusCode == 404)
        {
            _logger.LogInformation(ex, "Tenant renewal direct XUI lookup returned not found. candidate={Candidate}", candidate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant renewal direct XUI lookup failed. candidate={Candidate}", candidate);
        }

        try
        {
            var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
            var clients = clientsResponse.Obj ?? new List<XuiV3Client>();
            return clients.FirstOrDefault(client =>
                string.Equals(client.Email, candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(client.Uuid, candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(client.SubId, candidate, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant renewal client list lookup failed. candidate={Candidate}", candidate);
            return null;
        }
    }

    /// <summary>
    /// Normalizes account identifiers entered in tenant renewal flows.
    /// </summary>
    /// <param name="input">Raw customer input such as an email, UUID, VLESS link, VMess link, or subscription URL.</param>
    /// <returns>
    /// A likely email, UUID, or subscription id. Empty input and Telegram slash commands return <c>null</c>
    /// because commands must never be sent to the XUI <c>clients/get</c> endpoint as account identifiers.
    /// </returns>
    private static string NormalizeTenantAccountInput(string input)
    {
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("/", StringComparison.Ordinal))
            return null;

        if (value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            var withoutScheme = value["vless://".Length..];
            var atIndex = withoutScheme.IndexOf('@');
            return atIndex > 0 ? withoutScheme[..atIndex] : withoutScheme.Split('?', '#')[0];
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(lastSegment))
                return WebUtility.UrlDecode(lastSegment);
        }

        return value.Trim().Trim('`');
    }

    /// <summary>
    /// Checks whether a found XUI client belongs to the tenant customer and storefront.
    /// </summary>
    /// <param name="client">XUI client found on the configured panel.</param>
    /// <param name="customerTelegramUserId">Numeric Telegram user id of the tenant customer.</param>
    /// <param name="tenantBotId">Internal tenant bot id that owns the storefront.</param>
    /// <returns>
    /// <c>true</c> when the client belongs to the customer and is either created by the same tenant or has no
    /// bot ownership metadata from older records.
    /// </returns>
    private static bool ClientBelongsToTenantCustomer(XuiV3Client client, long customerTelegramUserId, string tenantBotId)
    {
        var metadata = TryReadTenantMetadata(client?.Comment);
        var ownerMatches = client?.TgId == customerTelegramUserId || metadata?.TelegramUserId == customerTelegramUserId;
        var tenantMatches = string.IsNullOrWhiteSpace(metadata?.CreatedByBotId) ||
                            string.Equals(metadata.CreatedByBotId, tenantBotId, StringComparison.OrdinalIgnoreCase);
        return ownerMatches && tenantMatches;
    }

    /// <summary>
    /// Resolves the active service definition for an existing XUI client.
    /// </summary>
    /// <param name="client">XUI client whose metadata and inbound ids are inspected.</param>
    /// <returns>The matching enabled service, or <c>null</c> when the client is outside active plan inbounds.</returns>
    /// <remarks>
    /// Tenant normal and unlimited services can share the same XUI inbound ids. Metadata is therefore trusted first.
    /// If an older or link-changed account has no readable metadata, fallback inference uses the negative
    /// first-use expiry convention to identify unlimited accounts; otherwise shared public inbounds resolve to
    /// the normal metered service instead of incorrectly showing unlimited renewal plans.
    /// </remarks>
    private XuiV3ServiceDefinition ResolveTenantServiceForClient(XuiV3Client client)
    {
        var metadata = TryReadTenantMetadata(client?.Comment);
        var services = _purchaseService.GetEnabledServices();
        if (!string.IsNullOrWhiteSpace(metadata?.ServiceKey))
        {
            var byMetadata = services
                .FirstOrDefault(x => string.Equals(x.Key, metadata.ServiceKey, StringComparison.OrdinalIgnoreCase));
            if (byMetadata != null)
                return byMetadata;
        }

        var inboundIds = GetTenantClientInboundIds(client, metadata);
        if (inboundIds.Count == 0)
            return null;

        var nationalService = services.FirstOrDefault(service =>
            IsNationalTenantService(service) && ServiceHasAnyInbound(service, inboundIds));
        if (nationalService != null)
            return nationalService;

        var normalService = services.FirstOrDefault(service =>
            IsNormalTenantService(service) && ServiceHasAnyInbound(service, inboundIds));
        if (normalService != null && !LooksLikeUnlimitedTenantClient(client, metadata))
            return normalService;

        var unlimitedService = services.FirstOrDefault(service =>
            service.IsUnlimited && ServiceHasAnyInbound(service, inboundIds));
        if (unlimitedService != null)
            return unlimitedService;

        return services.FirstOrDefault(service =>
            !service.IsUnlimited && ServiceHasAnyInbound(service, inboundIds));
    }

    /// <summary>
    /// Checks whether a tenant service definition represents the national metered service.
    /// </summary>
    /// <param name="service">Service definition loaded from the XUI v3 plan file.</param>
    /// <returns><c>true</c> when the service key or inbound profile identifies national traffic.</returns>
    private static bool IsNationalTenantService(XuiV3ServiceDefinition service)
    {
        return string.Equals(service?.Key, "national", StringComparison.OrdinalIgnoreCase) ||
               (service?.InboundProfileKeys?.Any(key => string.Equals(key, "national", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>
    /// Checks whether a tenant service definition represents the normal metered service.
    /// </summary>
    /// <param name="service">Service definition loaded from the XUI v3 plan file.</param>
    /// <returns><c>true</c> when the service key or inbound profile identifies normal metered traffic.</returns>
    private static bool IsNormalTenantService(XuiV3ServiceDefinition service)
    {
        return string.Equals(service?.Key, "normal", StringComparison.OrdinalIgnoreCase) ||
               (service?.InboundProfileKeys?.Any(key => string.Equals(key, "normal", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>
    /// Checks whether a tenant service overlaps at least one inbound id from an existing XUI client.
    /// </summary>
    /// <param name="service">Service definition whose inbound ids are tested.</param>
    /// <param name="inboundIds">Inbound ids read from the XUI client and its metadata.</param>
    /// <returns><c>true</c> when the service and client share at least one inbound id.</returns>
    private static bool ServiceHasAnyInbound(XuiV3ServiceDefinition service, IReadOnlyCollection<int> inboundIds)
    {
        return service?.InboundIds != null &&
               inboundIds != null &&
               service.InboundIds.Any(inboundIds.Contains);
    }

    /// <summary>
    /// Infers whether a tenant client is likely an unlimited account when metadata is missing.
    /// </summary>
    /// <param name="client">XUI client read from the panel.</param>
    /// <param name="metadata">Parsed client metadata, when available.</param>
    /// <returns>
    /// <c>true</c> when metadata explicitly says unlimited or the panel expiry uses the negative
    /// first-use-duration convention used by unlimited plans.
    /// </returns>
    /// <remarks>
    /// This fallback intentionally does not classify by inbound ids alone because normal and unlimited storefront
    /// plans can share the same inbounds. That exact ambiguity caused metered accounts to show unlimited renewal
    /// options after link changes when the panel response did not include service metadata.
    /// </remarks>
    private static bool LooksLikeUnlimitedTenantClient(XuiV3Client client, XuiV3ClientMetadata metadata)
    {
        if (string.Equals(metadata?.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(metadata?.ServiceKey) &&
            string.Equals(metadata.ServiceKey, "unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GetTenantExpiryTime(client) < 0;
    }

    /// <summary>
    /// Reads the XUI expiry timestamp from a tenant client using panel fields and known extra payload keys.
    /// </summary>
    /// <param name="client">XUI client returned by the panel; may omit the top-level expiry field.</param>
    /// <returns>Expiry timestamp in milliseconds, <c>0</c> for lifetime, or a negative first-use duration.</returns>
    private static long GetTenantExpiryTime(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        return ReadTenantLongExtra(client.Extra, "expiryTime", "expiry_time", "expiry");
    }

    /// <summary>
    /// Reads a long value from a tenant client's raw XUI extra JSON.
    /// </summary>
    /// <param name="extra">Raw extra dictionary returned by the XUI panel.</param>
    /// <param name="keys">Candidate property names that may hold the desired value.</param>
    /// <returns>The first parsed long value, or <c>0</c> when no key is present.</returns>
    private static long ReadTenantLongExtra(IDictionary<string, JToken> extra, params string[] keys)
    {
        if (extra == null || keys == null || keys.Length == 0)
            return 0;

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !extra.TryGetValue(key, out var token) || token == null)
                continue;

            if (token.Type == JTokenType.Integer)
                return token.Value<long>();

            if (long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return 0;
    }

    /// <summary>
    /// Reads all known inbound ids from the client and its JSON metadata.
    /// </summary>
    /// <param name="client">XUI client read from the panel.</param>
    /// <param name="metadata">Parsed metadata from the client comment, when available.</param>
    /// <returns>A distinct list of inbound ids attached to the client.</returns>
    private static List<int> GetTenantClientInboundIds(XuiV3Client client, XuiV3ClientMetadata metadata)
    {
        var ids = new List<int>();
        if (metadata?.InboundIds != null)
            ids.AddRange(metadata.InboundIds);
        if (client?.InboundIds != null)
            ids.AddRange(client.InboundIds);
        return ids.Where(id => id > 0).Distinct().ToList();
    }

    /// <summary>
    /// Parses XUI client metadata from the panel comment field.
    /// </summary>
    /// <param name="comment">Raw JSON comment stored on the XUI client.</param>
    /// <returns>Parsed metadata, or <c>null</c> when the comment is empty or not JSON metadata.</returns>
    private static XuiV3ClientMetadata TryReadTenantMetadata(string comment)
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

    /// <summary>
    /// Normalizes owner-provided channel Text into A Telegram Username or URL that can be stored in users.db.
    /// </summary>
    /// <param name="Text">Raw owner input, COMMONLY an @Username, t.me link, or numeric channel Id.</param>
    /// <returns>normalized channel identifier, or null when the input is BLANK.</returns>
    private static string NORMALIZETELEGRAMCHANNEL(string Text)
    {
        var value = Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("HTTPS://t.me/", StringComparison.OrdinalIgnoreCase))
            value = "@" + value.Split('/', StringSplitOptions.RemoveEmptyEntries).Last().TrimStart('@');
        else if (!value.StartsWith("@", StringComparison.Ordinal) && !long.TryParse(value, out _))
            value = "@" + value.TrimStart('@');

        return value;
    }

    /// <summary>
    /// Converts the globally configured XuiV3 panel settings into the <see cref="ServerInfo"/> object
    /// expected by the existing account-creation service.
    /// </summary>
    /// <returns>configured XuiV3 server descriptor.</returns>
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
            Name = "configured v3 panel"
        };
    }

    /// <summary>
    /// Builds the stable database Id for A colleague's tenant Bot from the owner's Telegram Id.
    /// </summary>
    /// <param name="OwnerTelegramUserId">Telegram User Id of the colleague owner.</param>
    /// <returns>stable tenant Bot Id used in users.db and the runtime registry.</returns>
    private static string BUILDTENANTBOTID(long OwnerTelegramUserId)
    {
        return $"tenant-{OwnerTelegramUserId}";
    }

    /// <summary>
    /// creates the public order Id used by tenant HooshPay invoices and operational logs.
    /// </summary>
    /// <param name="tenant">tenant Bot that owns the storefront order.</param>
    /// <param name="CustomerTelegramUserId">Telegram User Id of the customer.</param>
    /// <returns>Unique order Id containing tenant-owner Id, customer Id, timestamp, and random suffix.</returns>
    private static string CreateTenantOrderId(BotInstance tenant, long CustomerTelegramUserId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"TENANTBOT-{DateTime.UtcNow:yyyyMMddHHmmss}-{tenant.OwnerTelegramUserId}-{CustomerTelegramUserId}-{suffix}";
    }

    /// <summary>
    /// Converts A boolean Configuration state into the icon used in the owner setup panel.
    /// </summary>
    /// <param name="Ok">whether the setup field is configured.</param>
    /// <returns>Success or failure icon Text.</returns>
    private static string STATUSICON(bool Ok)
    {
        return Ok ? "✅" : "❌";
    }

    /// <summary>
    /// Builds the compact text shown on one order-list callback button.
    /// </summary>
    /// <param name="order">Tenant order represented by the button.</param>
    /// <returns>A short Telegram button label containing status, amount, and final balance.</returns>
    private static string BuildOwnerOrderButtonText(TenantBotOrder order)
    {
        var icon = order.IsFulfilled ? "✅" : order.PaymentStatus == TenantBotOrderStatuses.Failed ? "❌" : "⏳";
        var kind = string.Equals(order.OrderKind, TenantBotOrderKinds.Renew, StringComparison.OrdinalIgnoreCase) ? "تمدید" : "خرید";
        var shortId = string.IsNullOrWhiteSpace(order.OrderId) ? order.Id.ToString(CultureInfo.InvariantCulture) : order.OrderId[^Math.Min(8, order.OrderId.Length)..];
        return $"{icon} {kind} #{shortId} | {order.SalePriceToman.FormatCurrency()} | {FormatTenantOrderDate(order.CreatedAtUtc)}";
    }

    /// <summary>
    /// Formats a tenant order timestamp for compact Persian order reports.
    /// </summary>
    /// <param name="utc">UTC timestamp stored in users.db.</param>
    /// <returns>Shamsi date/time text when conversion helpers are available.</returns>
    private static string FormatTenantOrderDate(DateTime utc)
    {
        var tehran = utc.AddMinutes(210);
        return $"{tehran.ConvertToHijriShamsi()} {tehran:HH:mm}";
    }

    /// <summary>
    /// Builds a clickable Telegram user link for owner-facing reports.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram user id of the customer.</param>
    /// <param name="displayName">Display name shown in the clickable link.</param>
    /// <returns>HTML anchor using the <c>tg://user</c> Telegram deep link.</returns>
    private static string BuildTelegramUserLink(long telegramUserId, string displayName)
    {
        var safeName = string.IsNullOrWhiteSpace(displayName) ? telegramUserId.ToString(CultureInfo.InvariantCulture) : displayName;
        return $"<a href=\"tg://user?id={telegramUserId}\">{Html(safeName)}</a>";
    }

    /// <summary>
    /// Builds the best available display name for a tenant customer stored on an order.
    /// </summary>
    /// <param name="order">Tenant order containing cached Telegram profile fields.</param>
    /// <returns>Full name, username, or numeric id text.</returns>
    private static string BuildCustomerDisplayName(TenantBotOrder order)
    {
        var fullName = string.Join(" ", new[] { order.CustomerFirstName, order.CustomerLastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;
        if (!string.IsNullOrWhiteSpace(order.CustomerUsername))
            return "@" + order.CustomerUsername.TrimStart('@');
        return order.CustomerTelegramUserId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts tenant order kind values to Persian display text.
    /// </summary>
    /// <param name="order">Tenant order whose kind should be displayed.</param>
    /// <returns>Human-readable order kind.</returns>
    private static string GetOrderKindDisplay(TenantBotOrder order)
    {
        return string.Equals(order.OrderKind, TenantBotOrderKinds.Renew, StringComparison.OrdinalIgnoreCase)
            ? "تمدید اکانت"
            : "خرید اکانت";
    }

    /// <summary>
    /// Encodes Text before inserting it into Telegram Html messages.
    /// </summary>
    /// <param name="value">Raw Text that may contain Telegram Html-sensitive characters.</param>
    /// <returns>Html-encoded Text; null becomes an empty string.</returns>
    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    /// <summary>
    /// Encodes text before inserting it into a Telegram HTML attribute.
    /// </summary>
    /// <param name="value">Raw attribute text such as a Telegram username or URL.</param>
    /// <returns>HTML-encoded attribute text; null becomes an empty string.</returns>
    private static string HtmlAttribute(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    /// <summary>
    /// Reads tenant tutorial links from the bot instance JSON column.
    /// </summary>
    /// <param name="tenant">Tenant bot whose tutorial JSON should be parsed.</param>
    /// <returns>A safe sequence of tutorial links; invalid JSON returns an empty list.</returns>
    private static IEnumerable<TenantTutorialLink> ReadTenantTutorials(BotInstance tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant?.TenantTutorialsJson))
            return Array.Empty<TenantTutorialLink>();

        try
        {
            return JsonConvert.DeserializeObject<List<TenantTutorialLink>>(tenant.TenantTutorialsJson)?
                .Where(x => !string.IsNullOrWhiteSpace(x.Title) && !string.IsNullOrWhiteSpace(x.Url))
                .ToList() ?? new List<TenantTutorialLink>();
        }
        catch
        {
            return Array.Empty<TenantTutorialLink>();
        }
    }

    /// <summary>
    /// Parses a Telegram post URL into the chat and message id required by <c>ForwardMessage</c>.
    /// </summary>
    /// <param name="text">Owner-provided URL such as <c>https://t.me/channel/123</c>.</param>
    /// <param name="chatId">Parsed public chat username or private channel id.</param>
    /// <param name="messageId">Parsed Telegram message id.</param>
    /// <returns><c>true</c> when the URL can be forwarded by Telegram.</returns>
    private static bool TryParseTelegramPostLink(string text, out ChatId chatId, out int messageId)
    {
        chatId = null;
        messageId = 0;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !int.TryParse(segments[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out messageId))
            return false;

        if (segments[0] == "c" && segments.Length >= 3)
        {
            chatId = new ChatId(long.Parse("-100" + segments[1], CultureInfo.InvariantCulture));
            return true;
        }

        chatId = new ChatId("@" + segments[0].TrimStart('@'));
        return true;
    }

    /// <summary>
    /// Reads seven-day tenant usage statistics from the JSONL activity log.
    /// </summary>
    /// <param name="tenantBotId">Internal tenant bot id whose events should be counted.</param>
    /// <returns>Seven daily buckets ordered from oldest to newest.</returns>
    private IReadOnlyList<TenantDailyStatsRow> ReadTenantDailyStats(string tenantBotId)
    {
        var today = DateTime.UtcNow.AddMinutes(210).Date;
        var rows = Enumerable.Range(0, 7)
            .Select(offset => today.AddDays(offset - 6))
            .Select(date => new TenantDailyStatsRow { Date = date.ConvertToHijriShamsi() })
            .ToList();
        var usersByDate = rows.ToDictionary(x => x.Date, _ => new HashSet<long>());

        var logPath = ResolveTenantActivityLogPath();
        if (!System.IO.File.Exists(logPath))
            return rows;

        foreach (var line in System.IO.File.ReadLines(logPath))
        {
            try
            {
                var obj = JObject.Parse(line.TrimStart('\uFEFF'));
                if (!string.Equals(obj.Value<string>("botId"), tenantBotId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(obj.Value<string>("event"), "telegram_message", StringComparison.OrdinalIgnoreCase))
                    continue;

                var date = (obj.Value<string>("time") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(date) || !usersByDate.TryGetValue(date, out var users))
                    continue;

                var row = rows.First(x => x.Date == date);
                row.MessageCount++;
                var userId = obj.Value<long?>("userId") ?? 0;
                if (userId > 0)
                    users.Add(userId);
            }
            catch
            {
                // A single malformed line must not break owner-facing statistics.
            }
        }

        foreach (var row in rows)
            row.UniqueUsers = usersByDate[row.Date].Count;
        return rows;
    }

    /// <summary>
    /// Resolves the current user activity log path using the same date placeholder convention as the logger.
    /// </summary>
    /// <returns>Filesystem path of today's JSONL activity log.</returns>
    private string ResolveTenantActivityLogPath()
    {
        var path = string.IsNullOrWhiteSpace(_appConfig.UserActivityLogFilePath)
            ? "./Data/Logs/user-activity-{shamsiDate}.jsonl"
            : _appConfig.UserActivityLogFilePath;
        var shamsi = DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return path.Replace("{shamsiDate}", shamsi, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One tenant-owned tutorial link configured by a colleague.
    /// </summary>
    private sealed class TenantTutorialLink
    {
        /// <summary>
        /// User-facing tutorial title shown on a Telegram URL button.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Telegram or web URL opened when customers tap the tutorial button.
        /// </summary>
        public string Url { get; set; }
    }

    /// <summary>
    /// One daily tenant usage bucket derived from the JSONL activity log.
    /// </summary>
    private sealed class TenantDailyStatsRow
    {
        /// <summary>
        /// Shamsi date label for the bucket.
        /// </summary>
        public string Date { get; set; }

        /// <summary>
        /// Number of distinct Telegram users who sent messages to the tenant bot that day.
        /// </summary>
        public int UniqueUsers { get; set; }

        /// <summary>
        /// Number of message events logged for the tenant bot that day.
        /// </summary>
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// Result object returned by the tenant token duplicate guard.
    /// </summary>
    /// <remarks>
    /// The result intentionally stores only a conflict type label. It does not carry the conflicting token or
    /// any other secret so it is safe to include in structured logs.
    /// </remarks>
    private sealed class TenantTokenConflictResult
    {
        /// <summary>
        /// Shared no-conflict instance used when the token can be assigned to the current tenant owner.
        /// </summary>
        public static readonly TenantTokenConflictResult None = new();

        /// <summary>
        /// Indicates whether another bot already owns the entered token, token bot id, or username.
        /// </summary>
        public bool HasConflict { get; private init; }

        /// <summary>
        /// Non-secret source label such as <c>tenant-token</c> or <c>owned-username</c>.
        /// </summary>
        public string ConflictType { get; private init; }

        /// <summary>
        /// Creates a conflict result with a non-secret reason label.
        /// </summary>
        /// <param name="conflictType">
        /// Short internal label describing which duplicate rule matched. The value must not include a token.
        /// </param>
        /// <returns>A result whose <see cref="HasConflict" /> property is <c>true</c>.</returns>
        public static TenantTokenConflictResult Conflict(string conflictType)
        {
            return new TenantTokenConflictResult
            {
                HasConflict = true,
                ConflictType = conflictType
            };
        }
    }

    /// <summary>
    /// Source keys for tenant owner settlement movements.
    /// </summary>
    /// <remarks>
    /// These values are internal audit labels. They describe which wallet paid the base cost or received tenant
    /// profit and are displayed in private logs and owner notifications.
    /// </remarks>
    private static class TenantOwnerWalletSources
    {
        /// <summary>
        /// The tenant owner's bot wallet paid the card-to-card base cost.
        /// </summary>
        public const string BotWallet = "bot_wallet";

        /// <summary>
        /// The tenant owner's Gozargah website wallet paid the card-to-card base cost.
        /// </summary>
        public const string GozargahSiteWallet = "gozargah_site_wallet";

        /// <summary>
        /// The tenant owner's bot wallet was allowed to go negative for card-to-card base cost.
        /// </summary>
        public const string NegativeBotWallet = "negative_bot_wallet";

        /// <summary>
        /// A platform gateway sale credited profit to the tenant owner's bot wallet.
        /// </summary>
        public const string PlatformGatewayProfit = "platform_gateway_profit";
    }

    /// <summary>
    /// Display-only snapshot of a tenant owner's Gozargah website wallet.
    /// </summary>
    private sealed class TenantSiteWalletSnapshot
    {
        /// <summary>
        /// Whether the website wallet exists and its balance was read successfully.
        /// </summary>
        public bool IsConnected { get; private init; }

        /// <summary>
        /// Website wallet balance in toman when <see cref="IsConnected"/> is true.
        /// </summary>
        public long? WalletToman { get; private init; }

        /// <summary>
        /// Short display status such as <c>متصل نشده</c> or <c>در دسترس نیست</c> when not connected.
        /// </summary>
        public string StatusText { get; private init; }

        /// <summary>
        /// Creates a connected website-wallet snapshot.
        /// </summary>
        /// <param name="walletToman">Current website wallet balance in toman.</param>
        /// <returns>Connected snapshot with a readable balance.</returns>
        public static TenantSiteWalletSnapshot Connected(long walletToman)
            => new() { IsConnected = true, WalletToman = walletToman };

        /// <summary>
        /// Creates an unavailable website-wallet snapshot.
        /// </summary>
        /// <param name="statusText">Short Persian status safe for owner messages and private logs.</param>
        /// <returns>Unavailable snapshot with no balance.</returns>
        public static TenantSiteWalletSnapshot Unavailable(string statusText)
            => new() { StatusText = string.IsNullOrWhiteSpace(statusText) ? "در دسترس نیست" : statusText };
    }

    /// <summary>
    /// Result of settling the tenant owner's wallet side for one fulfilled storefront order.
    /// </summary>
    private sealed class TenantOwnerWalletSettlementResult
    {
        /// <summary>
        /// Internal source key from <see cref="TenantOwnerWalletSources"/>.
        /// </summary>
        public string WalletSource { get; private init; }

        /// <summary>
        /// Human-readable Persian source label shown to the owner and in private audit logs.
        /// </summary>
        public string SourceDisplayName { get; private init; }

        /// <summary>
        /// Signed tenant owner financial delta in toman. Positive values credit profit; negative values represent
        /// a base-cost debit from either bot wallet or website wallet.
        /// </summary>
        public long OwnerDelta { get; private init; }

        /// <summary>
        /// Tenant owner's bot-wallet balance before the settlement.
        /// </summary>
        public long BotWalletBefore { get; private init; }

        /// <summary>
        /// Tenant owner's bot-wallet balance after the settlement.
        /// </summary>
        public long BotWalletAfter { get; private init; }

        /// <summary>
        /// Website wallet snapshot before settlement. It is display-only and may be unavailable.
        /// </summary>
        public TenantSiteWalletSnapshot SiteWalletBefore { get; private init; }

        /// <summary>
        /// Website wallet snapshot after settlement. It is display-only unless the website wallet paid base cost.
        /// </summary>
        public TenantSiteWalletSnapshot SiteWalletAfter { get; private init; }

        /// <summary>
        /// Optional owner-facing warning, currently used when the bot wallet falls back to a negative balance.
        /// </summary>
        public string WarningMessage { get; private init; }

        /// <summary>
        /// Creates a settlement result with source, balances, website snapshots, and optional warning.
        /// </summary>
        /// <param name="walletSource">Internal source key from <see cref="TenantOwnerWalletSources"/>.</param>
        /// <param name="ownerDelta">Signed owner delta in toman.</param>
        /// <param name="botWalletBefore">Bot-wallet balance before settlement.</param>
        /// <param name="botWalletAfter">Bot-wallet balance after settlement.</param>
        /// <param name="siteWalletBefore">Website wallet snapshot before settlement.</param>
        /// <param name="siteWalletAfter">Website wallet snapshot after settlement.</param>
        /// <param name="warningMessage">Optional owner-facing warning text.</param>
        /// <returns>Settlement result consumed by order persistence, ledger, owner messages, and private logs.</returns>
        public static TenantOwnerWalletSettlementResult Create(
            string walletSource,
            long ownerDelta,
            long botWalletBefore,
            long botWalletAfter,
            TenantSiteWalletSnapshot siteWalletBefore,
            TenantSiteWalletSnapshot siteWalletAfter,
            string warningMessage = null)
        {
            return new TenantOwnerWalletSettlementResult
            {
                WalletSource = walletSource,
                SourceDisplayName = walletSource switch
                {
                    TenantOwnerWalletSources.BotWallet => "کیف پول ربات",
                    TenantOwnerWalletSources.GozargahSiteWallet => "کیف پول سایت گذرگاه",
                    TenantOwnerWalletSources.NegativeBotWallet => "منفی شدن کیف پول ربات",
                    TenantOwnerWalletSources.PlatformGatewayProfit => "سود درگاه پلتفرم",
                    _ => walletSource
                },
                OwnerDelta = ownerDelta,
                BotWalletBefore = botWalletBefore,
                BotWalletAfter = botWalletAfter,
                SiteWalletBefore = siteWalletBefore,
                SiteWalletAfter = siteWalletAfter,
                WarningMessage = warningMessage
            };
        }
    }

    /// <summary>
    /// internal Price BREAKDOWN for tenant storefront orders.
    /// </summary>
    private sealed class TenantPriceResult
    {
        public long SalePriceToman { get; set; }
        public long BaseCostToman { get; set; }
        public long ProfitToman { get; set; }
    }
}
