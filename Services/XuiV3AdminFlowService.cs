using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Exceptions;

/// <summary>
/// Handles super-admin XuiV3 operations: creating accounts for users, renewing accounts,
/// deleting expired users, private messaging, and manually checking payment status.
/// </summary>
/// <remarks>
/// The multi-tenant addition affects this service in the manual HooshPay check path.
/// When a paid HooshPay row belongs to a tenant storefront, settlement is delegated to
/// <see cref="TenantBotService.ApplyPaidTenantOrderAsync"/> instead of charging a user's wallet.
/// </remarks>
public class XuiV3AdminFlowService
{
    private const string FlowName = "xui-v3-admin";
    private const string StepGetAccountInfo = "get-account-info";
    private const string StepGetNowPaymentStatus = "get-nowpayment-status";
    private const string StepCreateTargetUser = "create-target-user";
    private const string StepCreateService = "create-service";
    private const string StepCreateTraffic = "create-traffic";
    private const string StepCreateDuration = "create-duration";
    private const string StepCreateUnlimitedPlan = "create-unlimited-plan";
    private const string StepCreateAccountCount = "create-account-count";
    private const string StepCreateUserComment = "create-user-comment";
    private const string StepCreateConfirm = "create-confirm";
    private const string StepRenewAccount = "renew-account";
    private const string StepRenewTraffic = "renew-traffic";
    private const string StepRenewDuration = "renew-duration";
    private const string StepRenewConfirm = "renew-confirm";
    private const string StepDeleteExpiredTargetUser = "delete-expired-target-user";
    private const string StepDeleteExpiredConfirm = "delete-expired-confirm";
    private const string StepBanUsers = "ban-users";
    private const string StepUnbanUsers = "unban-users";
    private const string StepSendPrivateTarget = "send-private-target";
    private const string StepSendPrivateMessage = "send-private-message";
    private const string StepSendPrivateConfirm = "send-private-confirm";
    private const string HooshPayProvisionalStartCallbackPrefix = "x3admin:hp:provisional:";
    private const string HooshPayProvisionalConfirmCallbackPrefix = "x3admin:hp:provisional-confirm:";
    private const string HooshPayProvisionalCancelCallbackPrefix = "x3admin:hp:provisional-cancel:";
    private const int MaxDetailedAccountInfoMessages = 5;
    private const int MaxTelegramTextLength = 3900;
    private const string SkipCommentText = "ادامه بدون کامنت";

    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly NowPayments _nowPayments;
    private readonly NowPaymentsSettlementService _settlementService;
    private readonly HooshPay _hooshPay;
    private readonly HooshPaySettlementService _hooshPaySettlementService;
    private readonly TenantBotService _tenantBotService;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly GozargahSiteSyncService _gozargahSiteSyncService;
    private readonly ILogger<XuiV3AdminFlowService> _logger;
    private readonly UserActivityLogService _activityLog;

    /// <summary>
    /// Creates the super-admin XuiV3 flow service and injects the payment and tenant services needed by admin tools.
    /// </summary>
    /// <param name="userDbContext">Runtime database containing admin flow state and payment rows.</param>
    /// <param name="credentialsDbContext">Shared profile and wallet database.</param>
    /// <param name="configuration">Application configuration containing XuiV3 and payment settings.</param>
    /// <param name="nowPayments">NOWPayments API client for manual crypto payment checks.</param>
    /// <param name="settlementService">NOWPayments wallet settlement service.</param>
    /// <param name="hooshPay">HooshPay API client for manual rial payment checks.</param>
    /// <param name="hooshPaySettlementService">HooshPay wallet settlement service.</param>
    /// <param name="tenantBotService">Tenant storefront settlement service for direct tenant orders.</param>
    /// <param name="purchaseService">Shared XuiV3 purchase and renewal service.</param>
    /// <param name="gozargahSiteSyncService">
    /// Gozargah website sync service used by super-admin historical sync, admin account creation, and admin renewal.
    /// It writes to an outbox first, so website API failures do not roll back successful 3x-ui operations.
    /// </param>
    /// <param name="logger">Service logger.</param>
    /// <param name="activityLog">File-based activity logger for admin actions.</param>
    public XuiV3AdminFlowService(
        UserDbContext userDbContext,
        CredentialsDbContext credentialsDbContext,
        IConfiguration configuration,
        NowPayments nowPayments,
        NowPaymentsSettlementService settlementService,
        HooshPay hooshPay,
        HooshPaySettlementService hooshPaySettlementService,
        TenantBotService tenantBotService,
        XuiV3PurchaseService purchaseService,
        GozargahSiteSyncService gozargahSiteSyncService,
        ILogger<XuiV3AdminFlowService> logger,
        UserActivityLogService activityLog)
    {
        _userDbContext = userDbContext;
        _credentialsDbContext = credentialsDbContext;
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _nowPayments = nowPayments;
        _settlementService = settlementService;
        _hooshPay = hooshPay;
        _hooshPaySettlementService = hooshPaySettlementService;
        _tenantBotService = tenantBotService;
        _purchaseService = purchaseService;
        _gozargahSiteSyncService = gozargahSiteSyncService;
        _logger = logger;
        _activityLog = activityLog;
    }

    /// <summary>
    /// Checks whether the application is currently configured to use XuiV3 admin flows.
    /// </summary>
    /// <returns><c>true</c> when <c>XuiApiVersionMode</c> is <c>v3</c>.</returns>
    public bool IsEnabled()
    {
        return string.Equals(_appConfig.XuiApiVersionMode, "v3", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to handle a super-admin message as part of the XuiV3 admin flow.
    /// </summary>
    /// <param name="botClient">Telegram client for the current bot.</param>
    /// <param name="message">Incoming admin message.</param>
    /// <param name="currentUser">Bot-scoped admin state row.</param>
    /// <param name="mainMenu">Reply keyboard returned when a flow finishes.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram, database, payment, and panel operations.</param>
    /// <returns><c>true</c> when the message was consumed by this service; otherwise <c>false</c>.</returns>
    public async Task<bool> TryHandleMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled() || message?.Text == null)
            return false;

        if (string.Equals(message.Text, "Sync Gozargah Site", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGozargahHistoricalSyncAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateTargetUser)
        {
            await HandleCreateTargetUserAsync(botClient, message, currentUser, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateService)
        {
            await HandleCreateServiceAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateTraffic)
        {
            await HandleCreateTrafficAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateDuration)
        {
            await HandleCreateDurationAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateUnlimitedPlan)
        {
            await HandleCreateUnlimitedPlanAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateAccountCount)
        {
            await HandleCreateAccountCountAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateUserComment)
        {
            await HandleCreateUserCommentAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepCreateConfirm)
        {
            await HandleCreateConfirmAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepRenewAccount)
        {
            await HandleRenewAccountAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepRenewTraffic)
        {
            await HandleRenewTrafficAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepRenewDuration)
        {
            await HandleRenewDurationAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepRenewConfirm)
        {
            await HandleRenewConfirmAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (message.Text == "➕ Create New Account")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepCreateTargetUser
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "تلگرام آیدی صاحب اکانت را بفرستید یا گزینه «برای خودم» را بزنید:",
                replyMarkup: new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("برای خودم") },
                    new[] { new KeyboardButton("📑 Menu") }
                })
                {
                    ResizeKeyboard = true
                },
                cancellationToken: cancellationToken);
            return true;
        }

        if (message.Text == "🔄 Renew Existing Account")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepRenewAccount
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "ایمیل اکانت نسخه ۳ را برای تمدید ارسال کنید. اگر تلگرام آیدی بفرستید، اکانت‌های همان کاربر را لیست می‌کنم.",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepGetAccountInfo)
        {
            await HandleGetAccountInfoAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepGetNowPaymentStatus)
        {
            await HandleNowPaymentStatusAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepDeleteExpiredTargetUser)
        {
            await HandleDeleteExpiredTargetUserAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepDeleteExpiredConfirm)
        {
            await HandleDeleteExpiredConfirmAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepBanUsers)
        {
            await HandleBlockUsersAsync(botClient, message, currentUser, mainMenu, true, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepUnbanUsers)
        {
            await HandleBlockUsersAsync(botClient, message, currentUser, mainMenu, false, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepSendPrivateTarget)
        {
            await HandlePrivateMessageTargetAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepSendPrivateMessage)
        {
            await HandlePrivateMessageTextAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (currentUser?.Flow == FlowName && currentUser.LastStep == StepSendPrivateConfirm)
        {
            await HandlePrivateMessageConfirmAsync(botClient, message, currentUser, mainMenu, cancellationToken);
            return true;
        }

        if (message.Text == "ℹ️ Get Account Info")
        {
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepGetAccountInfo
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "ایمیل اکانت نسخه ۳، آیدی تلگرام کاربر، یا ترکیب آیدی تلگرام و شماره اکانت را ارسال کنید.\nمثال:\n<code>8787745942 12</code>",
                parseMode: ParseMode.Html,
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (message.Text == "✔️ Verify payment")
        {
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepGetNowPaymentStatus
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "شناسه پرداخت را ارسال کنید.\nبرای NOWPayments می‌توانید `Order ID`، `Payment ID` یا `Invoice ID` بفرستید.\nبرای HooshPay می‌توانید `Order ID`، `Invoice UID` یا شناسه داخلی رکورد را بفرستید.\nبرای سفارش ناقص ربات فروشگاهی هم می‌توانید `OrderId` همان سفارش tenant را بفرستید تا تایید/تلاش مجدد انجام شود.\nاگر پرداخت در درگاه تایید شده باشد و قبلاً اعمال نشده باشد، تسویه یا تحویل انجام می‌شود:",
                parseMode: ParseMode.Markdown,
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (message.Text == "🗑 Delete expired accounts")
        {
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepDeleteExpiredTargetUser
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "تلگرام آیدی عددی کاربر را بفرستید تا اکانت‌های منقضی او روی پنل نسخه ۳ بررسی شود:",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (message.Text == "🚫 Ban user" || message.Text == "✅ Unban user")
        {
            var shouldBlock = message.Text == "🚫 Ban user";
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = shouldBlock ? StepBanUsers : StepUnbanUsers
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: shouldBlock
                    ? "آیدی عددی کاربر یا کاربران مورد نظر برای مسدودسازی را بفرستید. می‌توانید چند آیدی را با فاصله، ویرگول یا خط جدید بفرستید."
                    : "آیدی عددی کاربر یا کاربران مورد نظر برای رفع مسدودی را بفرستید. می‌توانید چند آیدی را با فاصله، ویرگول یا خط جدید بفرستید.",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (message.Text == "✉️ Send message to user")
        {
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepSendPrivateTarget
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "آیدی عددی کاربری که می‌خواهید برای او پیام خصوصی ارسال شود را بفرستید:",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        return false;
    }

    private async Task HandleCreateTargetUserAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        CancellationToken cancellationToken)
    {
        if (IsCancel(message.Text))
        {
            await CancelAsync(botClient, message.Chat.Id, currentUser, cancellationToken);
            return;
        }

        var input = message.Text.Trim();
        var targetTelegramUserId = string.Equals(input, "برای خودم", StringComparison.OrdinalIgnoreCase)
            ? message.From.Id
            : 0;

        if (targetTelegramUserId == 0 && !long.TryParse(input, out targetTelegramUserId))
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "تلگرام آیدی معتبر نیست. فقط عدد بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepCreateService,
            ConfigLink = targetTelegramUserId.ToString()
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: $"صاحب اکانت: <code>{targetTelegramUserId}</code>\nنوع سرویس را انتخاب کنید:",
            parseMode: ParseMode.Html,
            replyMarkup: BuildServiceReplyKeyboard(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateServiceAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var service = TryGetServiceFromText(message.Text);
        if (service == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "سرویس معتبر نیست. یکی از گزینه‌های لیست را انتخاب کنید.",
                replyMarkup: BuildServiceReplyKeyboard(),
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = service.IsUnlimited ? StepCreateUnlimitedPlan : StepCreateTraffic,
            SelectedCountry = service.Key
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: service.IsUnlimited
                ? "پلن نامحدود را انتخاب کنید:"
                : "حجم اکانت را انتخاب کنید:\nمی‌توانید یکی از دکمه‌ها را بزنید یا حجم دلخواه را به صورت عدد صحیح بفرستید؛ مثلا 7 یا ۷.",
            replyMarkup: service.IsUnlimited ? BuildUnlimitedPlanReplyKeyboard(service, false) : BuildTrafficReplyKeyboard(service),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateTrafficAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var service = FindService(currentUser.SelectedCountry);
        if (!TryGetTrafficGbFromText(message.Text, out var trafficGb) || trafficGb <= 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "حجم معتبر نیست. یکی از دکمه‌ها را بزنید یا فقط عدد صحیح بفرستید؛ مثلا 7 یا ۷.",
                replyMarkup: BuildTrafficReplyKeyboard(service),
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepCreateDuration,
            TotoalGB = trafficGb.ToString()
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "مدت اکانت را انتخاب کنید:",
            replyMarkup: BuildDurationReplyKeyboard(service),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateDurationAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var service = FindService(currentUser.SelectedCountry);
        var duration = TryGetDurationFromText(service, message.Text);
        if (duration == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "مدت معتبر نیست. یکی از گزینه‌ها را انتخاب کنید.",
                replyMarkup: BuildDurationReplyKeyboard(service),
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepCreateAccountCount,
            SelectedPeriod = duration.Key
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: $"تعداد اکانت مورد نظر را وارد کنید. حداکثر تعداد در هر سفارش {XuiV3PurchaseService.MaxBulkAccountCount} است.",
            replyMarkup: BuildAccountCountReplyKeyboard(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateUnlimitedPlanAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var service = FindService(currentUser.SelectedCountry);
        var plan = TryGetUnlimitedPlanFromText(service, message.Text);
        if (plan == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "پلن معتبر نیست. یکی از گزینه‌ها را انتخاب کنید.",
                replyMarkup: BuildUnlimitedPlanReplyKeyboard(service, false),
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepCreateAccountCount,
            Type = plan.Key
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: $"تعداد اکانت مورد نظر را وارد کنید. حداکثر تعداد در هر سفارش {XuiV3PurchaseService.MaxBulkAccountCount} است.",
            replyMarkup: BuildAccountCountReplyKeyboard(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateAccountCountAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        if (!TryGetIntFromText(message.Text, out var accountCount) ||
            accountCount <= 0 ||
            accountCount > XuiV3PurchaseService.MaxBulkAccountCount)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"تعداد اکانت معتبر نیست. یک عدد بین 1 تا {XuiV3PurchaseService.MaxBulkAccountCount} بفرستید.",
                replyMarkup: BuildAccountCountReplyKeyboard(),
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepCreateUserComment,
            PendingAccountCount = accountCount
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "اگر می‌خواهید برای این سفارش کامنت ذخیره شود، متن آن را بفرستید.\n\nاگر کامنتی ندارید، گزینه «ادامه بدون کامنت» را بزنید.",
            replyMarkup: BuildOptionalCommentReplyKeyboard(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateUserCommentAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var userComment = IsSkipCommentText(message.Text)
            ? string.Empty
            : NormalizeUserComment(message.Text);

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepCreateConfirm,
            PendingUserComment = userComment
        });

        var refreshedUser = await _userDbContext.GetUserStatus(message.From.Id);
        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: BuildCreateSummary(refreshedUser),
            parseMode: ParseMode.Html,
            replyMarkup: BuildYesNoKeyboard("Yes Create!", "No Don't Create!"),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateConfirmAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (message.Text == "No Don't Create!" || IsCancel(message.Text))
        {
            await CancelAsync(botClient, message.Chat.Id, currentUser, cancellationToken, mainMenu);
            return;
        }

        if (message.Text != "Yes Create!")
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای ساخت اکانت گزینه تایید را بزنید.",
                replyMarkup: BuildYesNoKeyboard("Yes Create!", "No Don't Create!"),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال ساخت اکانت نسخه ۳...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var bulkResult = await CreateAdminAccountsAsync(currentUser, message.From.Id, cancellationToken);
        if (bulkResult.SuccessfulCount == 0)
        {
            var failureMessage = bulkResult.Failures.FirstOrDefault()?.Message ?? "خطای نامشخص";
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"ساخت اکانت ناموفق بود.\n{failureMessage}",
                cancellationToken);
            return;
        }

        foreach (var createdAccount in bulkResult.CreatedAccounts)
        {
            var text = BuildCreatedAccountInfo(createdAccount);
            if (!string.IsNullOrWhiteSpace(createdAccount.SubLink))
            {
                using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(createdAccount.SubLink, 200));
                await botClient.SendPhotoAsync(
                    chatId: message.Chat.Id,
                    photo: InputFile.FromStream(qrStream, $"xui-v3-admin-account-{createdAccount.Email}.png"),
                    caption: text,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: text,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }

            await Task.Delay(250, cancellationToken);
        }

        if (long.TryParse(currentUser.ConfigLink, out var syncTargetTelegramUserId))
        {
            foreach (var createdAccount in bulkResult.CreatedAccounts)
            {
                await _gozargahSiteSyncService.QueueCreateAsync(
                    syncTargetTelegramUserId,
                    syncTargetTelegramUserId,
                    createdAccount,
                    bulkResult.BulkOrderId,
                    cancellationToken: cancellationToken);
            }
        }

        if (bulkResult.Failures.Count > 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: BuildAdminBulkFailureText(bulkResult),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

        await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "منوی اصلی", cancellationToken);
        _logger.LogInformation(
            BuildAdminBulkCreateLogMessage(currentUser, message.From.Id, bulkResult).EscapeMarkdown());

        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        await _activityLog.LogBotActionAsync(
            "xui_v3_admin_bulk_accounts_created",
            actorCredUser,
            bulkResult.Success,
            new Dictionary<string, object>
            {
                ["targetTelegramUserId"] = currentUser.ConfigLink,
                ["bulkOrderId"] = bulkResult.BulkOrderId,
                ["requestedCount"] = bulkResult.RequestedCount,
                ["successfulCount"] = bulkResult.SuccessfulCount,
                ["serviceKey"] = bulkResult.ServiceKey,
                ["serviceName"] = bulkResult.ServiceName,
                ["trafficBytes"] = bulkResult.TrafficBytes,
                ["durationDays"] = bulkResult.DurationDays,
                ["accounts"] = bulkResult.CreatedAccounts.Select(x => x.Email).ToList(),
                ["failures"] = bulkResult.Failures.Select(x => new { x.Index, x.Message }).ToList()
            },
            cancellationToken);

    }

    private async Task HandleRenewAccountAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var input = message.Text.Trim();
        var serverInfo = BuildConfiguredPanelServerInfo();
        XuiV3Client client = null;

        if (long.TryParse(input, out var telegramUserId))
        {
            var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
            var matches = clientsResponse.Obj?.Where(c => c.TgId == telegramUserId).OrderBy(c => c.Email).ToList() ?? new List<XuiV3Client>();
            if (matches.Count != 1)
            {
                var list = matches.Count == 0
                    ? "برای این تلگرام آیدی اکانتی پیدا نشد."
                    : "برای این تلگرام آیدی چند اکانت پیدا شد. لطفاً ایمیل یکی از این اکانت‌ها را بفرستید:\n" +
                      string.Join("\n", matches.Select(c => $"<code>{Html(c.Email)}</code>"));

                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: list,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
                return;
            }

            client = matches[0];
        }
        else
        {
            var clientResponse = await ApiServicev3.GetClientAsync(serverInfo, _configuration, input, cancellationToken);
            if (clientResponse.Success)
                client = clientResponse.Obj;
        }

        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "اکانت نسخه ۳ پیدا نشد.",
                cancellationToken: cancellationToken);
            return;
        }

        var service = ResolveServiceForClient(client);
        var metadata = TryReadMetadata(client.Comment);
        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepRenewTraffic,
            ConfigLink = client.Email,
            SelectedCountry = service?.Key ?? metadata?.ServiceKey
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: $"اکانت انتخاب شد: <code>{Html(client.Email)}</code>\nتلگرام آیدی مالک فعلی: <code>{client.TgId}</code>\nحجم اضافه را به گیگابایت بفرستید.\nمی‌توانید یکی از دکمه‌ها را بزنید یا عدد دلخواه را مستقیم بفرستید؛ مثلا 7 یا ۷:",
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleRenewTrafficAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        if (!TryGetTrafficGbFromText(message.Text, out var trafficGb) || trafficGb < 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "حجم معتبر نیست. عدد صحیح گیگابایت را بفرستید. برای تمدید فقط زمانی می‌توانید 0 بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepRenewDuration,
            TotoalGB = trafficGb.ToString()
        });

        var service = TryFindService(currentUser.SelectedCountry);
        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "مدت اضافه را انتخاب کنید:",
            replyMarkup: service == null ? BuildGenericDurationReplyKeyboard() : BuildDurationReplyKeyboard(service),
            cancellationToken: cancellationToken);
    }

    private async Task HandleRenewDurationAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var service = TryFindService(currentUser.SelectedCountry);
        var durationDays = service == null
            ? TryGetGenericDurationDays(message.Text)
            : TryGetDurationFromText(service, message.Text)?.Days;

        if (durationDays == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "مدت معتبر نیست.",
                replyMarkup: service == null ? BuildGenericDurationReplyKeyboard() : BuildDurationReplyKeyboard(service),
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepRenewConfirm,
            SelectedPeriod = durationDays.Value.ToString()
        });

        var refreshedUser = await _userDbContext.GetUserStatus(message.From.Id);
        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: await BuildRenewSummaryAsync(refreshedUser, cancellationToken),
            parseMode: ParseMode.Html,
            replyMarkup: BuildYesNoKeyboard("Yes Renew!", "No Don't Create!"),
            cancellationToken: cancellationToken);
    }

    private async Task HandleRenewConfirmAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (message.Text == "No Don't Create!" || IsCancel(message.Text))
        {
            await CancelAsync(botClient, message.Chat.Id, currentUser, cancellationToken, mainMenu);
            return;
        }

        if (message.Text != "Yes Renew!")
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای تمدید اکانت گزینه تایید را بزنید.",
                replyMarkup: BuildYesNoKeyboard("Yes Renew!", "No Don't Create!"),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال تمدید اکانت نسخه ۳...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var serverInfo = BuildConfiguredPanelServerInfo();
        var clientResponse = await ApiServicev3.GetClientAsync(serverInfo, _configuration, currentUser.ConfigLink, cancellationToken);
        if (!clientResponse.Success || clientResponse.Obj == null)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "اکانت برای تمدید پیدا نشد.", cancellationToken);
            return;
        }

        var client = clientResponse.Obj;
        var addTrafficGb = int.TryParse(currentUser.TotoalGB, out var parsedTraffic) ? parsedTraffic : 0;
        var addDays = int.TryParse(currentUser.SelectedPeriod, out var parsedDays) ? parsedDays : 0;
        var service = TryFindService(currentUser.SelectedCountry) ?? ResolveServiceForClient(client);
        var currentExpiryBeforeRenew = GetExpiryTime(client);
        var renewal = XuiV3RenewalPolicy.CalculateAdmin(client, service, addTrafficGb, addDays, "admin-renew", message.From.Id);
        var updatedClient = renewal.Payload;
        Console.WriteLine(
            $"[XUIv3] admin renew payload actor={message.From.Id}, email={client.Email}, durationDays={addDays}, currentExpiry={currentExpiryBeforeRenew}, newExpiry={updatedClient.ExpiryTime}, currentExpiryText={FormatExpiry(currentExpiryBeforeRenew)}, newExpiryText={FormatExpiry(updatedClient.ExpiryTime)}, resetTraffic={renewal.ShouldResetTraffic}, totalBytesAfter={renewal.TotalBytesAfterRenew}, targetAvailableBytes={renewal.TargetAvailableTrafficBytes}");

        var updateResponse = await ApiServicev3.UpdateClientAsync(serverInfo, _configuration, client.Email, updatedClient, cancellationToken);
        if (!updateResponse.Success)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, $"تمدید ناموفق بود.\n{updateResponse.Msg}", cancellationToken);
            return;
        }

        var trafficResetApplied = await ResetRenewedTrafficIfNeededAsync(serverInfo, client.Email, renewal, cancellationToken);
        client.TotalGB = updatedClient.TotalGB;
        client.ExpiryTime = updatedClient.ExpiryTime;
        client.Comment = updatedClient.Comment;
        client.TgId = updatedClient.TgId;
        client.Enable = updatedClient.Enable;
        // Some 3x-ui v3 responses omit the traffic object. The panel-side reset is already done;
        // only mirror the reset locally when the response actually carried counters.
        var traffic = client.Traffic;
        if (trafficResetApplied && traffic != null)
        {
            traffic.Up = 0;
            traffic.Down = 0;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "✅ اکانت با موفقیت تمدید شد.\n\n" + BuildClientInfo(client, serverInfo),
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);

        await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "منوی اصلی", cancellationToken);
        _logger.LogInformation(
            BuildAdminRenewLogMessage(currentUser, message.From.Id, client, serverInfo, addTrafficGb, addDays).EscapeMarkdown());

        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        await _activityLog.LogBotActionAsync(
            "xui_v3_admin_account_renewed",
            actorCredUser,
            true,
            new Dictionary<string, object>
            {
                ["targetTelegramUserId"] = client.TgId,
                ["accountEmail"] = client.Email,
                ["trafficAddedGb"] = addTrafficGb,
                ["targetAvailableGbAfterRenew"] = renewal.TargetAvailableTrafficGb,
                ["durationAddedDays"] = addDays,
                ["finalDurationDays"] = renewal.FinalDurationDays,
                ["trafficResetApplied"] = trafficResetApplied,
                ["totalGbAfterRenew"] = GetTotalBytes(client).ConvertBytesToGB(),
                ["usedGb"] = GetUsedBytes(client).ConvertBytesToGB(),
                ["expiryShamsi"] = FormatExpiry(client.ExpiryTime),
                ["subLink"] = ApiServicev3.BuildSubscriptionLink(serverInfo, client.SubId ?? client.Email),
                ["panelUrl"] = serverInfo.Url,
                ["rootPath"] = serverInfo.RootPath,
                ["comment"] = client.Comment
            },
            cancellationToken);

        var renewMetadata = TryReadMetadata(client.Comment);
        var syncOwnerTelegramUserId = client.TgId != 0 ? client.TgId : renewMetadata?.TelegramUserId ?? 0;
        if (syncOwnerTelegramUserId > 0)
        {
            await _gozargahSiteSyncService.QueueUpdateAsync(
                syncOwnerTelegramUserId,
                syncOwnerTelegramUserId,
                client,
                serverInfo,
                $"admin-renew-{client.Email}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                cancellationToken: cancellationToken);
        }

    }

    /// <summary>
    /// Handles the two-stage super-admin callback flow for provisionally crediting a pending HooshPay wallet charge.
    /// </summary>
    /// <param name="botClient">Telegram client for the owned bot through which the super-admin is working.</param>
    /// <param name="callbackQuery">Callback issued from the admin-only HooshPay provisional approval message.</param>
    /// <param name="mainMenu">Super-admin reply keyboard used after a financial decision completes.</param>
    /// <param name="cancellationToken">Cancellation token for HooshPay verification, users.db, wallet, ledger, and Telegram work.</param>
    /// <returns>
    /// <c>true</c> when the callback belonged to the provisional HooshPay flow and was consumed; otherwise <c>false</c>
    /// so other callback handlers may process it.
    /// </returns>
    /// <remarks>
    /// Only configured super-admin Telegram ids may reach this method. The callback carries only the internal payment
    /// row id; it never contains an invoice secret, provider key, or raw order payload. The confirm stage refreshes
    /// HooshPay once more before it makes a provisional financial exception.
    /// </remarks>
    public async Task<bool> TryHandleCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        var data = callbackQuery?.Data ?? string.Empty;
        if (!data.StartsWith(HooshPayProvisionalStartCallbackPrefix, StringComparison.Ordinal) &&
            !data.StartsWith(HooshPayProvisionalConfirmCallbackPrefix, StringComparison.Ordinal) &&
            !data.StartsWith(HooshPayProvisionalCancelCallbackPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsConfiguredSuperAdmin(callbackQuery.From?.Id ?? 0))
        {
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "اجازه انجام این عملیات را ندارید.", true, cancellationToken);
            return true;
        }

        var prefix = data.StartsWith(HooshPayProvisionalConfirmCallbackPrefix, StringComparison.Ordinal)
            ? HooshPayProvisionalConfirmCallbackPrefix
            : data.StartsWith(HooshPayProvisionalCancelCallbackPrefix, StringComparison.Ordinal)
                ? HooshPayProvisionalCancelCallbackPrefix
                : HooshPayProvisionalStartCallbackPrefix;
        if (!int.TryParse(data[prefix.Length..], out var paymentId) || paymentId <= 0)
        {
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "شناسه پرداخت معتبر نیست.", true, cancellationToken);
            return true;
        }

        var payment = await _userDbContext.HooshPayPaymentInfos.FindAsync(new object[] { paymentId }, cancellationToken);
        if (payment == null)
        {
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "پرداخت مورد نظر پیدا نشد.", true, cancellationToken);
            return true;
        }

        if (prefix == HooshPayProvisionalCancelCallbackPrefix)
        {
            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                "تایید موقت شارژ لغو شد. هیچ تغییری در کیف پول کاربر انجام نشد.",
                replyMarkup: null,
                cancellationToken);
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "لغو شد.", false, cancellationToken);
            return true;
        }

        if (prefix == HooshPayProvisionalStartCallbackPrefix)
        {
            if (!CanProvisionallyApproveHooshPay(payment))
            {
                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    BuildHooshPayPaymentInfo(payment, settlement: null) +
                    "\n\nاین پرداخت در وضعیت قابل تایید موقت نیست.",
                    replyMarkup: null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(botClient, callbackQuery, "این پرداخت قابل تایید موقت نیست.", true, cancellationToken);
                return true;
            }

            var confirmationText = "⚠️ <b>تایید موقت شارژ HooshPay</b>\n\n" +
                                   BuildHooshPayPaymentInfo(payment, settlement: null) +
                                   "\n\nاین عملیات بدون تایید رسمی HooshPay، کیف پول کاربر را شارژ می‌کند. " +
                                   "پس از تایید رسمی درگاه، فقط reconciliation و لاگ ثبت می‌شود و شارژ دوباره انجام نمی‌شود.\n\n" +
                                   "آیا تایید نهایی موقت انجام شود؟";
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ تایید نهایی موقت", HooshPayProvisionalConfirmCallbackPrefix + payment.Id),
                    InlineKeyboardButton.WithCallbackData("انصراف", HooshPayProvisionalCancelCallbackPrefix + payment.Id)
                }
            });
            await EditProvisionalMessageAsync(botClient, callbackQuery, confirmationText, keyboard, cancellationToken);
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "برای تایید نهایی، دکمه سبز را بزنید.", false, cancellationToken);
            return true;
        }

        await ConfirmProvisionalHooshPayAsync(botClient, callbackQuery, payment, mainMenu, cancellationToken);
        return true;
    }

    private async Task<bool> ResetRenewedTrafficIfNeededAsync(
        ServerInfo serverInfo,
        string email,
        XuiV3RenewalCalculation renewal,
        CancellationToken cancellationToken)
    {
        if (renewal?.ShouldResetTraffic != true)
            return false;

        var resetResponse = await ApiServicev3.ResetClientTrafficAsync(serverInfo, _configuration, email, cancellationToken);
        if (resetResponse.Success)
            return true;

        Console.WriteLine($"[XUIv3] admin renew traffic reset failed email={email}, msg={resetResponse.Msg}. Trying updateTraffic fallback.");
        var fallbackResponse = await ApiServicev3.UpdateClientTrafficAsync(serverInfo, _configuration, email, 0, 0, cancellationToken);
        if (fallbackResponse.Success)
            return true;

        Console.WriteLine($"[XUIv3] admin renew traffic reset fallback failed email={email}, msg={fallbackResponse.Msg}");
        return false;
    }

    private async Task HandleGetAccountInfoAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        var input = message.Text.Trim();
        var serverInfo = BuildConfiguredPanelServerInfo();

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال دریافت اطلاعات از پنل نسخه ۳...",
            cancellationToken: cancellationToken);

        var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!response.Success)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, $"دریافت اکانت‌ها ناموفق بود.\n{response.Msg}", cancellationToken);
            return;
        }

        var clients = response.Obj ?? new List<XuiV3Client>();
        List<XuiV3Client> matches;
        if (TryParseTelegramIdAndAccountCounter(input, out var ownerTelegramId, out var accountCounter))
        {
            matches = clients
                .Where(client => ClientBelongsToUser(client, ownerTelegramId))
                .Where(client => ClientHasAccountCounter(client, accountCounter))
                .OrderBy(client => client.Email)
                .ToList();
        }
        else if (long.TryParse(NormalizeDigits(input), out var telegramUserId))
        {
            matches = clients.Where(c => c.TgId == telegramUserId).OrderBy(c => c.Email).ToList();
        }
        else
        {
            matches = clients.Where(c => string.Equals(c.Email, input, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 0)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "اکانت مورد نظر روی پنل نسخه ۳ پیدا نشد.", cancellationToken);
            return;
        }

        await SendAccountInfoResultsAsync(botClient, message.Chat.Id, matches, serverInfo, cancellationToken);

        await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "منوی اصلی", cancellationToken);
    }

    private async Task HandleNowPaymentStatusAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        var input = message.Text.Trim();
        var payment = await _userDbContext.SwapinoPaymentInfos
            .FirstOrDefaultAsync(p => p.OrderId == input || p.PaymentId == input || p.InvoiceId == input, cancellationToken);

        if (payment == null)
        {
            if (await TryHandleHooshPayStatusAsync(botClient, message, currentUser, mainMenu, input, cancellationToken))
                return;
            if (await TryHandleTenantOrderManualConfirmationAsync(botClient, message, currentUser, mainMenu, input, cancellationToken))
                return;

            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "پرداخت NOWPayments، HooshPay یا سفارش tenant با این شناسه پیدا نشد.", cancellationToken);
            return;
        }

        var data = payment.GetNowPaymentsData();
        var isManualOrderConfirmation = string.Equals(payment.OrderId, input, StringComparison.OrdinalIgnoreCase);
        NowPaymentsSettlementResult settlement = null;

        if (isManualOrderConfirmation)
        {
            settlement = await _settlementService.ApplyManualConfirmationAsync(
                payment,
                "admin-manual-order",
                payment.ChatId == 0 ? null : payment.ChatId,
                cancellationToken);

            data = payment.GetNowPaymentsData();
        }
        else
        {
            var skipRemoteRefresh = payment.IsAddedToBalance &&
                                    payment.HasManualConfirmation() &&
                                    string.Equals(payment.PaymentStatus, NowPaymentsStatuses.Finished, StringComparison.OrdinalIgnoreCase);

            var paymentId = payment.PaymentId ?? data.PaymentId;
            if (!skipRemoteRefresh && !string.IsNullOrWhiteSpace(paymentId))
            {
                var status = await _nowPayments.GetPaymentStatusAsync(paymentId, cancellationToken);
                data.Apply(status);
                payment.SetNowPaymentsData(data);
                await _userDbContext.SaveChangesAsync(cancellationToken);
            }

            if (NowPaymentsStatuses.IsPaid(data.PaymentStatus ?? payment.PaymentStatus))
            {
                settlement = await _settlementService.ApplyFinishedPaymentAsync(
                    payment,
                    "admin-check",
                    payment.ChatId == 0 ? null : payment.ChatId,
                    cancellationToken);
            }
        }

        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        await _activityLog.LogBotActionAsync(
            isManualOrderConfirmation ? "nowpayments_manual_confirmed" : "nowpayments_status_checked",
            actorCredUser,
            true,
            new Dictionary<string, object>
            {
                ["orderId"] = payment.OrderId,
                ["paymentId"] = payment.PaymentId ?? data.PaymentId ?? string.Empty,
                ["invoiceId"] = payment.InvoiceId ?? data.InvoiceId ?? string.Empty,
                ["paymentStatus"] = payment.PaymentStatus ?? data.PaymentStatus ?? string.Empty,
                ["settlementStatus"] = settlement?.Status.ToString() ?? "not-applied",
                ["amountToman"] = payment.AmountToman
            },
            cancellationToken);

        if (isManualOrderConfirmation && settlement?.Status == NowPaymentsSettlementStatus.AlreadyAdded)
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                "این تراکنش قبلاً به صورت دستی تایید شده و مبلغ آن قبلاً به حساب کاربر افزوده شده است.\n\n" +
                BuildPaymentInfo(payment, data, settlement),
                cancellationToken,
                ParseMode.Html);
            return;
        }

        await FinishWithMessageAsync(
            botClient,
            message.Chat.Id,
            currentUser,
            mainMenu,
            BuildPaymentInfo(payment, data, settlement),
            cancellationToken,
            ParseMode.Html);
    }

    /// <summary>
    /// Handles the super-admin manual HooshPay payment check command.
    /// </summary>
    /// <remarks>
    /// The input may be an internal payment id, HooshPay order id, or invoice uid.
    /// Paid wallet-charge rows are settled through <see cref="HooshPaySettlementService"/>.
    /// Paid tenant-order rows are settled through <see cref="TenantBotService"/> to create the account
    /// and credit only the colleague owner's profit. A still-pending wallet charge may show the first stage of the
    /// two-step provisional-credit control; terminal failures and tenant orders never receive that control.
    /// </remarks>
    /// <param name="botClient">Telegram client used to answer the admin.</param>
    /// <param name="message">Admin message that contains the payment identifier.</param>
    /// <param name="currentUser">Bot-scoped admin state used to finish the flow.</param>
    /// <param name="mainMenu">Reply keyboard shown after the check finishes.</param>
    /// <param name="input">Payment id, order id, or invoice uid supplied by the admin.</param>
    /// <param name="cancellationToken">Cancellation token for API, database, and Telegram calls.</param>
    /// <returns><c>true</c> when a HooshPay row was found and handled; otherwise <c>false</c>.</returns>
    private async Task<bool> TryHandleHooshPayStatusAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        string input,
        CancellationToken cancellationToken)
    {
        HooshPayPaymentInfo payment = null;
        if (int.TryParse(input, out var paymentId))
            payment = await _userDbContext.HooshPayPaymentInfos.FindAsync(new object[] { paymentId }, cancellationToken);

        payment ??= await _userDbContext.HooshPayPaymentInfos.FirstOrDefaultAsync(
            p => p.OrderId == input || p.InvoiceUid == input,
            cancellationToken);

        if (payment == null)
            return false;

        NowPaymentsSettlementResult settlement = null;
        HooshPayInvoiceResponse invoice = null;
        HooshPayVerifyResponse verify = null;

        if (string.IsNullOrWhiteSpace(payment.InvoiceUid))
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                "شناسه فاکتور HooshPay برای این پرداخت ثبت نشده است.\n\n" + BuildHooshPayPaymentInfo(payment, settlement),
                cancellationToken,
                ParseMode.Html);
            return true;
        }

        try
        {
            invoice = await _hooshPay.GetInvoiceAsync(payment.InvoiceUid, cancellationToken);
            payment.Apply(invoice?.data);

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
        }
        catch (Exception ex)
        {
            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"خطا در بررسی وضعیت HooshPay:\n<code>{Html(ex.Message)}</code>\n\n" + BuildHooshPayPaymentInfo(payment, settlement),
                cancellationToken,
                ParseMode.Html);
            return true;
        }

        if (verify?.paid == true || HooshPayStatuses.IsPaid(payment.PaymentStatus))
        {
            payment.PaymentStatus = HooshPayStatuses.Paid;
            await _userDbContext.SaveChangesAsync(cancellationToken);
            // Admin manual checks must respect payment purpose to avoid charging tenant customers' wallets.
            var isTenantOrder = string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase);
            if (!isTenantOrder)
                await _hooshPaySettlementService.RecordProviderConfirmationAfterProvisionalAsync(
                    payment,
                    "admin-check",
                    cancellationToken);

            settlement = isTenantOrder
                ? await _tenantBotService.ApplyPaidTenantOrderAsync(
                    payment,
                    "admin-check",
                    cancellationToken)
                : await _hooshPaySettlementService.ApplyFinishedPaymentAsync(
                    payment,
                    "admin-check",
                    payment.ChatId == 0 ? null : payment.ChatId,
                    cancellationToken);
        }

        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        await _activityLog.LogBotActionAsync(
            "hooshpay_status_checked",
            actorCredUser,
            true,
            new Dictionary<string, object>
            {
                ["orderId"] = payment.OrderId ?? string.Empty,
                ["invoiceUid"] = payment.InvoiceUid ?? string.Empty,
                ["paymentStatus"] = payment.PaymentStatus ?? string.Empty,
                ["settlementStatus"] = settlement?.Status.ToString() ?? "not-applied",
                ["amountToman"] = payment.AmountToman,
                ["payableAmountToman"] = payment.PayableAmountToman,
                ["isAddedToBalance"] = payment.IsAddedToBalance
            },
            cancellationToken);

        if (settlement == null && CanProvisionallyApproveHooshPay(payment))
        {
            await _userDbContext.ClearUserStatus(currentUser);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: BuildHooshPayPaymentInfo(payment, settlement) +
                      "\n\nاین پرداخت هنوز از سمت HooshPay تایید نشده است. در صورت مشاهده و تایید دستی پرداخت، می‌توانید شارژ موقت انجام دهید.",
                parseMode: ParseMode.Html,
                replyMarkup: BuildProvisionalHooshPayStartKeyboard(payment.Id),
                cancellationToken: cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "منوی اصلی",
                replyMarkup: mainMenu,
                cancellationToken: cancellationToken);
            return true;
        }

        await FinishWithMessageAsync(
            botClient,
            message.Chat.Id,
            currentUser,
            mainMenu,
            BuildHooshPayPaymentInfo(payment, settlement),
            cancellationToken,
            ParseMode.Html);
        return true;
    }

    /// <summary>
    /// Rechecks HooshPay and applies the final super-admin provisional-credit decision for one wallet charge.
    /// </summary>
    /// <param name="botClient">Telegram client used to edit the two-stage approval message and restore the admin menu.</param>
    /// <param name="callbackQuery">Confirmed callback from the configured super-admin.</param>
    /// <param name="payment">Local HooshPay row selected by its internal users.db id.</param>
    /// <param name="mainMenu">Super-admin reply keyboard restored after the decision is recorded.</param>
    /// <param name="cancellationToken">Cancellation token for provider refresh, wallet settlement, ledger, and Telegram work.</param>
    /// <returns>A task that completes after the payment is officially settled, provisionally settled, or safely rejected.</returns>
    /// <remarks>
    /// The provider is refreshed a second time immediately before the financial decision. If it became paid meanwhile,
    /// normal official settlement is used. Otherwise only a non-final wallet-charge row may receive the documented
    /// provisional credit. Tenant orders are never eligible for this exception.
    /// </remarks>
    private async Task ConfirmProvisionalHooshPayAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        HooshPayPaymentInfo payment,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payment.InvoiceUid))
                throw new InvalidOperationException("شناسه فاکتور HooshPay برای این پرداخت ثبت نشده است.");

            var invoice = await _hooshPay.GetInvoiceAsync(payment.InvoiceUid, cancellationToken);
            payment.Apply(invoice?.data);
            var verify = await _hooshPay.VerifyInvoiceAsync(payment.InvoiceUid, cancellationToken);
            payment.Apply(verify?.data);
            if (!string.IsNullOrWhiteSpace(verify?.status))
                payment.PaymentStatus = verify.status;
            payment.RawResponseJson = JsonConvert.SerializeObject(new { invoice, verify });
            await _userDbContext.SaveChangesAsync(cancellationToken);

            if (verify?.paid == true || HooshPayStatuses.IsPaid(payment.PaymentStatus))
            {
                payment.PaymentStatus = HooshPayStatuses.Paid;
                await _userDbContext.SaveChangesAsync(cancellationToken);
                await _hooshPaySettlementService.RecordProviderConfirmationAfterProvisionalAsync(
                    payment,
                    "admin-provisional-confirm-refresh",
                    cancellationToken);
                var officialSettlement = await _hooshPaySettlementService.ApplyFinishedPaymentAsync(
                    payment,
                    "admin-provisional-confirm-refresh",
                    payment.ChatId == 0 ? null : payment.ChatId,
                    cancellationToken);
                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    "HooshPay در بررسی نهایی پرداخت را تایید کرد؛ شارژ رسمی اعمال شد.\n\n" +
                    BuildHooshPayPaymentInfo(payment, officialSettlement),
                    replyMarkup: null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(botClient, callbackQuery, "پرداخت درگاه تایید و تسویه شد.", false, cancellationToken);
                return;
            }

            if (!CanProvisionallyApproveHooshPay(payment))
            {
                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    BuildHooshPayPaymentInfo(payment, settlement: null) +
                    "\n\nتایید موقت انجام نشد؛ وضعیت جدید درگاه اجازه این عملیات را نمی‌دهد.",
                    replyMarkup: null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(botClient, callbackQuery, "وضعیت درگاه اجازه تایید موقت نمی‌دهد.", true, cancellationToken);
                return;
            }

            var provisionalSettlement = await _hooshPaySettlementService.ApplyProvisionalWalletPaymentAsync(
                payment,
                callbackQuery.From.Id,
                payment.ChatId == 0 ? null : payment.ChatId,
                cancellationToken);
            var actor = await GetActivityActorAsync(callbackQuery.From.Id);
            await _activityLog.LogBotActionAsync(
                "hooshpay_provisional_wallet_approved",
                actor,
                true,
                new Dictionary<string, object>
                {
                    ["orderId"] = payment.OrderId ?? string.Empty,
                    ["invoiceUid"] = payment.InvoiceUid ?? string.Empty,
                    ["paymentStatus"] = payment.PaymentStatus ?? string.Empty,
                    ["settlementStatus"] = provisionalSettlement.Status.ToString(),
                    ["amountToman"] = payment.AmountToman,
                    ["approvedByTelegramUserId"] = callbackQuery.From.Id
                },
                cancellationToken);

            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                provisionalSettlement.Status == NowPaymentsSettlementStatus.Applied
                    ? "✅ شارژ موقت کیف پول با موفقیت ثبت شد.\n\n" + BuildHooshPayPaymentInfo(payment, provisionalSettlement)
                    : "شارژ موقت انجام نشد.\n\n" + BuildHooshPayPaymentInfo(payment, provisionalSettlement),
                replyMarkup: null,
                cancellationToken);
            await AnswerCallbackSafelyAsync(
                botClient,
                callbackQuery,
                provisionalSettlement.Status == NowPaymentsSettlementStatus.Applied ? "شارژ موقت ثبت شد." : "شارژ موقت اعمال نشد.",
                provisionalSettlement.Status != NowPaymentsSettlementStatus.Applied,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "HooshPay provisional wallet confirmation failed. paymentId={PaymentId}, orderId={OrderId}, approvedBy={ApprovedBy}",
                payment.Id,
                payment.OrderId,
                callbackQuery.From?.Id);
            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                "تایید موقت انجام نشد. خطا در بررسی نهایی HooshPay:\n<code>" + Html(ex.Message) + "</code>",
                replyMarkup: null,
                cancellationToken);
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "خطا در بررسی نهایی HooshPay.", true, cancellationToken);
        }
        finally
        {
            if (callbackQuery.Message?.Chat.Id is long chatId && chatId != 0)
            {
                try
                {
                    await botClient.SendTextMessageAsync(chatId, "منوی اصلی", replyMarkup: mainMenu, cancellationToken: cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 403 || ex.ErrorCode == 400)
                {
                    _logger.LogWarning(ex, "Could not restore the super-admin menu after HooshPay provisional decision. chatId={ChatId}", chatId);
                }
            }
        }
    }

    /// <summary>
    /// Determines whether a local HooshPay row is eligible for an admin provisional wallet credit.
    /// </summary>
    /// <param name="payment">Payment row whose latest provider status has already been refreshed.</param>
    /// <returns>
    /// <c>true</c> only for an unpaid, non-terminal wallet charge that has not already mutated the user's balance.
    /// </returns>
    /// <remarks>
    /// Tenant orders and paid provider rows use their own normal settlement paths. Final gateway failures are never
    /// overrideable through this control because the provisional action is for delayed confirmation, not failed payment.
    /// </remarks>
    private static bool CanProvisionallyApproveHooshPay(HooshPayPaymentInfo payment)
    {
        return payment != null &&
               !payment.IsAddedToBalance &&
               !string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase) &&
               !HooshPayStatuses.IsPaid(payment.PaymentStatus) &&
               !HooshPayStatuses.IsFinalFailure(payment.PaymentStatus) &&
               !string.IsNullOrWhiteSpace(payment.InvoiceUid);
    }

    /// <summary>
    /// Builds the first-stage inline keyboard for a pending HooshPay wallet charge that a super-admin may review.
    /// </summary>
    /// <param name="paymentId">Internal positive users.db primary key of the HooshPay payment row.</param>
    /// <returns>Inline keyboard containing the first-stage provisional approval action.</returns>
    /// <remarks>
    /// Only the internal numeric id is embedded in callback data, keeping it below Telegram limits and avoiding raw
    /// provider identifiers in callback payloads.
    /// </remarks>
    private static InlineKeyboardMarkup BuildProvisionalHooshPayStartKeyboard(int paymentId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⚠️ تایید موقت شارژ", HooshPayProvisionalStartCallbackPrefix + paymentId)
            }
        });
    }

    /// <summary>
    /// Edits a provisional approval message while tolerating an unchanged Telegram message.
    /// </summary>
    /// <param name="botClient">Telegram client used for the edit.</param>
    /// <param name="callbackQuery">Callback that owns the editable message.</param>
    /// <param name="text">HTML-safe replacement text.</param>
    /// <param name="replyMarkup">Replacement inline keyboard, or null to remove approval actions.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    /// <returns>A task that completes after the edit succeeds or is safely skipped when unchanged.</returns>
    private static async Task EditProvisionalMessageAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        string text,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken cancellationToken)
    {
        if (callbackQuery?.Message == null)
            return;

        try
        {
            await botClient.EditMessageTextAsync(
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Telegram already shows the intended state; no financial retry is needed for this presentation-only edit.
        }
    }

    /// <summary>
    /// Answers an admin provisional-payment callback without allowing a stale Telegram query to interrupt settlement.
    /// </summary>
    /// <param name="botClient">Telegram client used to answer the callback.</param>
    /// <param name="callbackQuery">Callback query to answer. Null values are ignored.</param>
    /// <param name="text">Short user-facing callback toast text.</param>
    /// <param name="showAlert">Whether Telegram should show an alert rather than a transient toast.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    /// <returns>A task that completes after Telegram accepts or rejects the callback answer.</returns>
    private static async Task AnswerCallbackSafelyAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        string text,
        bool showAlert,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callbackQuery?.Id))
            return;

        try
        {
            await botClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                text,
                showAlert: showAlert,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message?.Contains("query is too old", StringComparison.OrdinalIgnoreCase) == true ||
                                               ex.Message?.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Telegram callback answers expire quickly; the persisted financial operation remains authoritative.
        }
    }

    /// <summary>
    /// Checks whether a Telegram user id belongs to the configured super-admin allow-list.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram user id supplied by an incoming callback.</param>
    /// <returns><c>true</c> only when the id is configured as a super-admin.</returns>
    private bool IsConfiguredSuperAdmin(long telegramUserId)
    {
        return telegramUserId > 0 && _appConfig.AdminsUserIds?.Contains(telegramUserId) == true;
    }

    /// <summary>
    /// Attempts to treat the admin payment-check input as a tenant storefront <c>OrderId</c>.
    /// </summary>
    /// <param name="botClient">Telegram client used to answer the super-admin.</param>
    /// <param name="message">Super-admin message containing the possible tenant order id.</param>
    /// <param name="currentUser">Bot-scoped admin flow state that should be cleared when the order is handled.</param>
    /// <param name="mainMenu">Admin reply keyboard shown after the check completes.</param>
    /// <param name="input">Raw identifier entered by the super-admin.</param>
    /// <param name="cancellationToken">Cancellation token for users.db, XUI, wallet, ledger, and Telegram work.</param>
    /// <returns>
    /// <c>true</c> when a tenant order was found and the manual confirmation/retry result was sent to the admin;
    /// otherwise <c>false</c> so the caller can continue reporting a normal payment-not-found message.
    /// </returns>
    /// <remarks>
    /// This path is intentionally part of the existing "Verify payment" admin flow. It lets super-admins recover a
    /// tenant order whose card receipt or gateway payment was accepted but XUI fulfillment timed out, without adding
    /// another account or another ledger row when the order is already fulfilled.
    /// </remarks>
    private async Task<bool> TryHandleTenantOrderManualConfirmationAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        string input,
        CancellationToken cancellationToken)
    {
        var resultText = await _tenantBotService.CONFIRMTENANTORDERBYSUPERADMINASYNC(
            input,
            message.From.Id,
            cancellationToken);
        if (resultText == null)
            return false;

        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        await _activityLog.LogBotActionAsync(
            "tenant_order_superadmin_manual_confirmed",
            actorCredUser,
            true,
            new Dictionary<string, object>
            {
                ["orderId"] = input ?? string.Empty
            },
            cancellationToken);

        await FinishWithMessageAsync(
            botClient,
            message.Chat.Id,
            currentUser,
            mainMenu,
            resultText,
            cancellationToken,
            ParseMode.Html);
        return true;
    }

    private async Task HandleDeleteExpiredTargetUserAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        if (!long.TryParse(NormalizeDigits(message.Text?.Trim()), out var targetTelegramUserId) || targetTelegramUserId <= 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "تلگرام آیدی معتبر نیست. فقط آیدی عددی کاربر را بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!response.Success)
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"دریافت اطلاعات اکانت‌ها ناموفق بود.\n{response.Msg}",
                cancellationToken);
            return;
        }

        var expiredClients = response.Obj?
            .Where(client => ClientBelongsToUser(client, targetTelegramUserId))
            .Where(IsExpiredOrDepleted)
            .OrderBy(client => client.Email)
            .ToList() ?? new List<XuiV3Client>();

        if (expiredClients.Count == 0)
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                "هیچ اکانت منقضی یا تمام‌شده‌ای برای این کاربر پیدا نشد.",
                cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepDeleteExpiredConfirm,
            ConfigLink = targetTelegramUserId.ToString(),
            SubLink = JsonConvert.SerializeObject(expiredClients.Select(client => client.Email).ToList())
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: BuildDeleteExpiredConfirmationText(expiredClients, true, targetTelegramUserId),
            parseMode: ParseMode.Html,
            replyMarkup: BuildYesNoKeyboard("Yes Delete Expired!", "No Don't Delete!"),
            cancellationToken: cancellationToken);
    }

    private async Task HandleDeleteExpiredConfirmAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (message.Text == "No Don't Delete!" || IsCancel(message.Text))
        {
            await CancelAsync(botClient, message.Chat.Id, currentUser, cancellationToken, mainMenu);
            return;
        }

        if (message.Text != "Yes Delete Expired!")
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای حذف اکانت‌های منقضی، گزینه تایید را بزنید.",
                replyMarkup: BuildYesNoKeyboard("Yes Delete Expired!", "No Don't Delete!"),
                cancellationToken: cancellationToken);
            return;
        }

        if (!long.TryParse(currentUser.ConfigLink, out var targetTelegramUserId))
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                "تلگرام آیدی کاربر برای حذف معتبر نیست.",
                cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال حذف اکانت‌های منقضی کاربر از پنل نسخه ۳...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var serverInfo = BuildConfiguredPanelServerInfo();
        var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!clientsResponse.Success)
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"دریافت اطلاعات اکانت‌ها ناموفق بود.\n{clientsResponse.Msg}",
                cancellationToken);
            return;
        }

        var requestedEmails = DeserializeEmailList(currentUser.SubLink);
        var eligibleClients = clientsResponse.Obj?
            .Where(client => requestedEmails.Contains(client.Email, StringComparer.OrdinalIgnoreCase))
            .Where(client => ClientBelongsToUser(client, targetTelegramUserId))
            .Where(IsExpiredOrDepleted)
            .OrderBy(client => client.Email)
            .ToList() ?? new List<XuiV3Client>();

        if (eligibleClients.Count == 0)
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                "اکانت منقضی قابل حذفی برای این کاربر پیدا نشد.",
                cancellationToken);
            return;
        }

        var eligibleEmails = eligibleClients.Select(client => client.Email).ToList();
        var bulkDeleteResponse = await ApiServicev3.BulkDeleteClientsAsync(serverInfo, _configuration, eligibleEmails, cancellationToken);
        var deleted = bulkDeleteResponse.Success ? eligibleEmails : new List<string>();
        var failed = bulkDeleteResponse.Success ? new List<string>() : eligibleEmails;

        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        if (deleted.Count > 0)
        {
            await _activityLog.LogBotActionAsync(
                "xui_v3_admin_expired_accounts_deleted",
                actorCredUser,
                true,
                new Dictionary<string, object>
                {
                    ["targetTelegramUserId"] = targetTelegramUserId,
                    ["deletedCount"] = deleted.Count,
                    ["failedCount"] = failed.Count,
                    ["deletedAccounts"] = string.Join(",", deleted)
                },
                cancellationToken);
        }

        _logger.LogInformation(
            BuildAdminDeleteExpiredLogMessage(
                actorCredUser,
                targetTelegramUserId,
                deleted,
                failed).EscapeMarkdown());

        await FinishWithMessageAsync(
            botClient,
            message.Chat.Id,
            currentUser,
            mainMenu,
            BuildDeleteExpiredResultText(deleted, failed, true, targetTelegramUserId),
            cancellationToken,
            ParseMode.Html);
    }

    private async Task HandleBlockUsersAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        bool shouldBlock,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var userIds = ParseTelegramUserIds(message.Text);
        if (userIds.Count == 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "هیچ آیدی عددی معتبری پیدا نشد. دوباره آیدی عددی تلگرام را بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        var changedCount = await _credentialsDbContext.SetBlockedStatus(userIds, shouldBlock, message.From.Id);
        var actorCredUser = await GetActivityActorAsync(message.From.Id);
        await _activityLog.LogBotActionAsync(
            shouldBlock ? "admin_users_blocked" : "admin_users_unblocked",
            actorCredUser,
            true,
            new Dictionary<string, object>
            {
                ["targetTelegramUserIds"] = userIds,
                ["changedCount"] = changedCount
            },
            cancellationToken);

        var idsText = string.Join("\n", userIds.Select(id => $"<code>{id}</code>"));
        await FinishWithMessageAsync(
            botClient,
            message.Chat.Id,
            currentUser,
            mainMenu,
            shouldBlock
                ? $"کاربرهای زیر مسدود شدند:\n{idsText}"
                : $"مسدودی کاربرهای زیر برداشته شد:\n{idsText}",
            cancellationToken,
            ParseMode.Html);
    }

    private async Task HandlePrivateMessageTargetAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        if (!long.TryParse(NormalizeDigits(message.Text?.Trim()), out var targetTelegramUserId) ||
            targetTelegramUserId <= 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "آیدی عددی معتبر نیست. فقط آیدی عددی تلگرام کاربر را بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        var targetUser = await _credentialsDbContext.GetUserStatusWithId(targetTelegramUserId);
        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepSendPrivateMessage,
            ConfigLink = targetTelegramUserId.ToString()
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: BuildPrivateMessageTargetText(targetTelegramUserId, targetUser),
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }

    private async Task HandlePrivateMessageTextAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (await CancelIfNeededAsync(botClient, message, currentUser, mainMenu, cancellationToken))
            return;

        var privateMessage = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(privateMessage))
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "متن پیام خالی است. متن پیام خصوصی را بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        if (privateMessage.Length > 3900)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "متن پیام خیلی طولانی است. لطفاً پیام را کوتاه‌تر از ۳۹۰۰ کاراکتر بفرستید.",
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepSendPrivateConfirm,
            SubLink = privateMessage
        });

        var targetUserId = long.TryParse(currentUser.ConfigLink, out var parsedTargetUserId)
            ? parsedTargetUserId
            : 0;
        var targetUser = targetUserId > 0
            ? await _credentialsDbContext.GetUserStatusWithId(targetUserId)
            : null;

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: BuildPrivateMessagePreviewText(targetUserId, targetUser, privateMessage),
            parseMode: ParseMode.Html,
            replyMarkup: BuildYesNoKeyboard("Yes Send Private!", "No Don't Send!"),
            cancellationToken: cancellationToken);
    }

    private async Task HandlePrivateMessageConfirmAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (message.Text == "No Don't Send!" || IsCancel(message.Text))
        {
            await CancelAsync(botClient, message.Chat.Id, currentUser, cancellationToken, mainMenu);
            return;
        }

        if (message.Text != "Yes Send Private!")
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای ارسال پیام خصوصی گزینه تایید را بزنید.",
                replyMarkup: BuildYesNoKeyboard("Yes Send Private!", "No Don't Send!"),
                cancellationToken: cancellationToken);
            return;
        }

        if (!long.TryParse(currentUser.ConfigLink, out var targetTelegramUserId) ||
            targetTelegramUserId <= 0 ||
            string.IsNullOrWhiteSpace(currentUser.SubLink))
        {
            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                "اطلاعات ارسال پیام کامل نیست. لطفاً از ابتدا شروع کنید.",
                cancellationToken);
            return;
        }

        var targetUser = await _credentialsDbContext.GetUserStatusWithId(targetTelegramUserId);
        var targetChatId = targetUser?.ChatID > 0 ? targetUser.ChatID : targetTelegramUserId;
        var actorCredUser = await GetActivityActorAsync(message.From.Id);

        try
        {
            var sentMessage = await botClient.SendTextMessageAsync(
                chatId: targetChatId,
                text: currentUser.SubLink,
                cancellationToken: cancellationToken);

            await _activityLog.LogBotActionAsync(
                "admin_private_message_sent",
                actorCredUser,
                true,
                new Dictionary<string, object>
                {
                    ["targetTelegramUserId"] = targetTelegramUserId,
                    ["targetChatId"] = targetChatId,
                    ["sentMessageId"] = sentMessage.MessageId,
                    ["messagePreview"] = ShortenForLog(currentUser.SubLink, 180)
                },
                cancellationToken);

            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"پیام خصوصی با موفقیت ارسال شد.\n\nکاربر: <code>{targetTelegramUserId}</code>\nMessage ID: <code>{sentMessage.MessageId}</code>",
                cancellationToken,
                ParseMode.Html);
        }
        catch (ApiRequestException ex)
        {
            var blockedText = IsPrivateMessageBlockedError(ex)
                ? "\nاحتمالاً کاربر ربات را بلاک کرده یا چت با ربات را شروع نکرده است."
                : string.Empty;

            await _activityLog.LogWarningAsync(
                "admin_private_message_failed",
                actorCredUser,
                true,
                new Dictionary<string, object>
                {
                    ["targetTelegramUserId"] = targetTelegramUserId,
                    ["targetChatId"] = targetChatId,
                    ["telegramErrorCode"] = ex.ErrorCode,
                    ["telegramErrorMessage"] = ex.Message,
                    ["messagePreview"] = ShortenForLog(currentUser.SubLink, 180)
                },
                cancellationToken);

            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"ارسال پیام خصوصی ناموفق بود.\n\nکاربر: <code>{targetTelegramUserId}</code>\nخطای تلگرام: <code>{ex.ErrorCode}</code>\n<code>{Html(ex.Message)}</code>{blockedText}",
                cancellationToken,
                ParseMode.Html);
        }
        catch (Exception ex)
        {
            await _activityLog.LogErrorAsync(
                "admin_private_message_failed",
                ex,
                actorCredUser,
                true,
                new Dictionary<string, object>
                {
                    ["targetTelegramUserId"] = targetTelegramUserId,
                    ["targetChatId"] = targetChatId,
                    ["messagePreview"] = ShortenForLog(currentUser.SubLink, 180)
                },
                cancellationToken);

            await FinishWithMessageAsync(
                botClient,
                message.Chat.Id,
                currentUser,
                mainMenu,
                $"ارسال پیام خصوصی ناموفق بود.\n\nکاربر: <code>{targetTelegramUserId}</code>\nخطا: <code>{Html(ex.Message)}</code>",
                cancellationToken,
                ParseMode.Html);
        }
    }

    private static string BuildPrivateMessageTargetText(long targetTelegramUserId, CredUser targetUser)
    {
        var builder = new StringBuilder();
        builder.AppendLine("کاربر مقصد انتخاب شد.");
        builder.AppendLine();
        builder.AppendLine($"آیدی عددی: <code>{targetTelegramUserId}</code>");

        if (targetUser != null)
        {
            if (!string.IsNullOrWhiteSpace(targetUser.Username))
                builder.AppendLine($"یوزرنیم: <code>@{Html(targetUser.Username.Trim().TrimStart('@'))}</code>");

            var fullName = string.Join(" ", new[] { targetUser.FirstName, targetUser.LastName }
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim()));

            if (!string.IsNullOrWhiteSpace(fullName))
                builder.AppendLine($"نام: <code>{Html(fullName)}</code>");

            builder.AppendLine($"نقش: <code>{(targetUser.IsColleague ? "همکار" : "کاربر عادی")}</code>");
            builder.AppendLine($"وضعیت مسدودی: <code>{(targetUser.IsBlocked ? "مسدود" : "آزاد")}</code>");
        }
        else
        {
            builder.AppendLine("این کاربر در دیتابیس credentials پیدا نشد. اگر قبلاً ربات را start نکرده باشد، ارسال پیام از سمت تلگرام رد می‌شود.");
        }

        builder.AppendLine();
        builder.AppendLine("حالا متن پیام خصوصی را بفرستید:");
        return builder.ToString();
    }

    private static string BuildPrivateMessagePreviewText(long targetTelegramUserId, CredUser targetUser, string privateMessage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("پیش‌نمایش پیام خصوصی");
        builder.AppendLine();
        builder.AppendLine($"مقصد: <code>{targetTelegramUserId}</code>");

        if (!string.IsNullOrWhiteSpace(targetUser?.Username))
            builder.AppendLine($"یوزرنیم: <code>@{Html(targetUser.Username.Trim().TrimStart('@'))}</code>");

        builder.AppendLine();
        builder.AppendLine("متن پیام:");
        builder.AppendLine($"<pre>{Html(privateMessage)}</pre>");
        builder.AppendLine();
        builder.AppendLine("برای ارسال، تایید نهایی را بزنید.");
        return builder.ToString();
    }

    private static bool IsPrivateMessageBlockedError(ApiRequestException ex)
    {
        if (ex == null)
            return false;

        var message = ex.Message ?? string.Empty;
        return ex.ErrorCode == 403 ||
               message.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCancel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        return normalized.Equals("📑 Menu", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Menu", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("منوی اصلی", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("انصراف", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSkipCommentText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        return value.Equals(SkipCommentText, StringComparison.OrdinalIgnoreCase) ||
               value.Equals("رد کردن", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("بدون کامنت", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CancelIfNeededAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (!IsCancel(message.Text))
            return false;

        await CancelAsync(botClient, message.Chat.Id, currentUser, cancellationToken, mainMenu);
        return true;
    }

    private async Task CancelAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        User currentUser,
        CancellationToken cancellationToken,
        IReplyMarkup mainMenu = null)
    {
        await _userDbContext.ClearUserStatus(currentUser);
        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "عملیات لغو شد.",
            replyMarkup: mainMenu ?? new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Runs the super-admin initiated historical sync from XUI v3 panel clients into the Gozargah website API.
    /// </summary>
    /// <param name="botClient">Telegram client of the current admin bot that receives progress and summary messages.</param>
    /// <param name="message">Super-admin Telegram message that requested the historical sync command.</param>
    /// <param name="currentUser">Bot-scoped admin state row that is cleared after the command completes.</param>
    /// <param name="mainMenu">Admin menu keyboard returned after the one-shot command completes.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram, database, panel, and website API calls.</param>
    /// <returns>A task that completes after all eligible panel clients have been queued and attempted once.</returns>
    /// <remarks>
    /// The command is intentionally manual and is not run at startup. Each eligible client is sent through the
    /// same outbox path used by realtime sync, so repeated historical runs are idempotent from the bot side and
    /// transient website API failures remain retryable in <c>GozargahSiteSyncEvents</c>.
    /// </remarks>
    private async Task HandleGozargahHistoricalSyncAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (!_appConfig.GozargahSiteSyncEnabled)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "Gozargah site sync is disabled in configuration.", cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Starting Gozargah historical sync. This may take a while...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var serverInfo = BuildConfiguredPanelServerInfo();
        var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!clientsResponse.Success)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, $"XUI client read failed.\n{clientsResponse.Msg}", cancellationToken);
            return;
        }

        var checkedCount = 0;
        var queuedCount = 0;
        var succeededCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        foreach (var client in clientsResponse.Obj ?? new List<XuiV3Client>())
        {
            checkedCount++;

            if (ResolveServiceForClient(client) == null)
            {
                skippedCount++;
                continue;
            }

            var metadata = TryReadMetadata(client.Comment);
            var ownerTelegramUserId = client.TgId != 0 ? client.TgId : metadata?.TelegramUserId ?? 0;
            if (ownerTelegramUserId <= 0)
            {
                skippedCount++;
                continue;
            }

            var syncEvent = await _gozargahSiteSyncService.QueueUpdateAsync(
                ownerTelegramUserId,
                ownerTelegramUserId,
                client,
                serverInfo,
                $"historical-{client.Email}",
                cancellationToken: cancellationToken);

            if (syncEvent == null)
            {
                skippedCount++;
                continue;
            }

            queuedCount++;
            if (string.Equals(syncEvent.Status, GozargahSiteSyncStatuses.Succeeded, StringComparison.OrdinalIgnoreCase))
                succeededCount++;
            else if (string.Equals(syncEvent.Status, GozargahSiteSyncStatuses.Skipped, StringComparison.OrdinalIgnoreCase))
                skippedCount++;
            else
                failedCount++;
        }

        var summary =
            "<b>Gozargah historical sync finished</b>\n\n" +
            $"Checked: <code>{checkedCount}</code>\n" +
            $"Queued: <code>{queuedCount}</code>\n" +
            $"Succeeded now: <code>{succeededCount}</code>\n" +
            $"Skipped: <code>{skippedCount}</code>\n" +
            $"Pending/failed for retry: <code>{failedCount}</code>";

        await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, summary, cancellationToken, ParseMode.Html);
    }

    private ReplyKeyboardMarkup BuildServiceReplyKeyboard()
    {
        var rows = _purchaseService.GetEnabledServices()
            .Select(service => new[]
            {
                new KeyboardButton($"{service.DisplayName} [{service.Key}]")
            })
            .Append(new[] { new KeyboardButton("📑 Menu") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private XuiV3ServiceDefinition TryGetServiceFromText(string text)
    {
        var key = ExtractBracketValue(text);
        return _purchaseService.GetEnabledServices().FirstOrDefault(service =>
            string.Equals(service.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.Key, text?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.DisplayName, text?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private XuiV3ServiceDefinition FindService(string serviceKey)
    {
        var service = TryFindService(serviceKey);
        if (service == null)
            throw new InvalidOperationException($"XUI v3 service '{serviceKey}' was not found.");

        return service;
    }

    private XuiV3ServiceDefinition TryFindService(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
            return null;

        return _purchaseService.GetEnabledServices().FirstOrDefault(service =>
            string.Equals(service.Key, serviceKey, StringComparison.OrdinalIgnoreCase));
    }

    private XuiV3ServiceDefinition ResolveServiceForClient(XuiV3Client client)
    {
        var metadata = TryReadMetadata(client?.Comment);
        var services = _purchaseService.GetEnabledServices();
        var clientInboundIds = GetClientInboundIds(client, metadata);

        if (clientInboundIds.Count == 0)
            return TryFindService(metadata?.ServiceKey);

        var nationalService = services.FirstOrDefault(service =>
            IsNationalService(service) && HasAnyInbound(clientInboundIds, service));
        if (nationalService != null)
        {
            Console.WriteLine($"[XUIv3] admin resolve service by inbound priority email={client.Email}, service={nationalService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return nationalService;
        }

        var unlimitedService = services.FirstOrDefault(service =>
            service.IsUnlimited && HasAnyInbound(clientInboundIds, service));
        if (unlimitedService != null)
        {
            Console.WriteLine($"[XUIv3] admin resolve service by inbound priority email={client.Email}, service={unlimitedService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return unlimitedService;
        }

        var normalService = services.FirstOrDefault(service =>
            IsNormalService(service) &&
            IsOnlyInServiceInbounds(clientInboundIds, service) &&
            HasAnyInbound(clientInboundIds, service));
        if (normalService != null)
        {
            Console.WriteLine($"[XUIv3] admin resolve service by inbound priority email={client.Email}, service={normalService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return normalService;
        }

        var meteredService = services.FirstOrDefault(service =>
            !service.IsUnlimited &&
            !IsNationalService(service) &&
            !IsNormalService(service) &&
            IsOnlyInServiceInbounds(clientInboundIds, service) &&
            HasAnyInbound(clientInboundIds, service));
        if (meteredService != null)
        {
            Console.WriteLine($"[XUIv3] admin resolve service by inbound fallback email={client.Email}, service={meteredService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return meteredService;
        }

        return TryFindService(metadata?.ServiceKey);
    }

    private static List<int> GetClientInboundIds(XuiV3Client client, XuiV3ClientMetadata metadata)
    {
        var inboundIds = new HashSet<int>();

        if (client?.InboundIds != null)
        {
            foreach (var id in client.InboundIds.Where(id => id > 0))
                inboundIds.Add(id);
        }

        if (metadata?.InboundIds != null)
        {
            foreach (var id in metadata.InboundIds.Where(id => id > 0))
                inboundIds.Add(id);
        }

        return inboundIds.OrderBy(id => id).ToList();
    }

    private static bool HasAnyInbound(IReadOnlyCollection<int> clientInboundIds, XuiV3ServiceDefinition service)
    {
        return service?.InboundIds != null &&
               service.InboundIds.Any(id => clientInboundIds.Contains(id));
    }

    private static bool IsOnlyInServiceInbounds(IReadOnlyCollection<int> clientInboundIds, XuiV3ServiceDefinition service)
    {
        if (clientInboundIds == null || clientInboundIds.Count == 0)
            return false;

        var serviceInboundIds = service?.InboundIds?
            .Where(id => id > 0)
            .ToHashSet() ?? new HashSet<int>();

        return serviceInboundIds.Count > 0 &&
               clientInboundIds.All(serviceInboundIds.Contains);
    }

    private static bool IsNationalService(XuiV3ServiceDefinition service)
    {
        return string.Equals(service?.Key, "national", StringComparison.OrdinalIgnoreCase) ||
               (service?.InboundProfileKeys?.Any(key => string.Equals(key, "national", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool IsNormalService(XuiV3ServiceDefinition service)
    {
        return string.Equals(service?.Key, "normal", StringComparison.OrdinalIgnoreCase) ||
               (service?.InboundProfileKeys?.Any(key => string.Equals(key, "normal", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static ReplyKeyboardMarkup BuildTrafficReplyKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = service.TrafficOptionsGb
            .OrderBy(gb => gb)
            .Select(gb => new KeyboardButton($"{gb} GB"))
            .Chunk(3)
            .Select(chunk => chunk.ToArray())
            .Append(new[] { new KeyboardButton("📑 Menu") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildDurationReplyKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = service.DurationOptions
            .OrderBy(duration => duration.Days)
            .Select(duration => new KeyboardButton($"{duration.DisplayName} [{duration.Key}]"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .Append(new[] { new KeyboardButton("📑 Menu") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildUnlimitedPlanReplyKeyboard(XuiV3ServiceDefinition service, bool isColleague)
    {
        var rows = service.UnlimitedPlans
            .Where(plan => plan.IsEnabled)
            .Select(plan => new KeyboardButton($"{plan.DisplayName} [{plan.Key}] - {plan.Price.GetForRole(isColleague).FormatCurrency()}"))
            .Chunk(1)
            .Select(chunk => chunk.ToArray())
            .Append(new[] { new KeyboardButton("📑 Menu") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildGenericDurationReplyKeyboard()
    {
        var rows = new[]
        {
            new[] { new KeyboardButton("نامحدود / لایف‌تایم [0]") },
            new[] { new KeyboardButton("30 روز [30]"), new KeyboardButton("60 روز [60]") },
            new[] { new KeyboardButton("90 روز [90]"), new KeyboardButton("180 روز [180]") },
            new[] { new KeyboardButton("📑 Menu") }
        };

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildYesNoKeyboard(string yesText, string noText)
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton(yesText), new KeyboardButton(noText) },
            new[] { new KeyboardButton("📑 Menu") }
        })
        {
            ResizeKeyboard = true
        };
    }

    private static ReplyKeyboardMarkup BuildAccountCountReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("1"), new KeyboardButton("2"), new KeyboardButton("3") },
            new[] { new KeyboardButton("5"), new KeyboardButton("10") },
            new[] { new KeyboardButton("انصراف") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    private static ReplyKeyboardMarkup BuildOptionalCommentReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton(SkipCommentText) },
            new[] { new KeyboardButton("انصراف") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    private static bool TryGetIntFromText(string text, out int value)
    {
        value = 0;
        var normalized = NormalizeDigits(text);
        var digits = new string((normalized ?? string.Empty).Where(char.IsDigit).ToArray());
        return !string.IsNullOrWhiteSpace(digits) && int.TryParse(digits, out value);
    }

    private static List<long> ParseTelegramUserIds(string text)
    {
        var normalized = NormalizeDigits(text ?? string.Empty);
        return System.Text.RegularExpressions.Regex
            .Matches(normalized, @"\d+")
            .Select(match => long.TryParse(match.Value, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private static bool TryGetTrafficGbFromText(string text, out int value)
    {
        value = 0;
        var normalized = NormalizeDigits(text)?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(
            normalized,
            @"^\s*(?<value>\d+)\s*(?<unit>.*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!match.Success || !int.TryParse(match.Groups["value"].Value, out value))
            return false;

        var unit = (match.Groups["unit"].Value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("\u064a", "\u06cc")
            .Replace("\u0643", "\u06a9");

        return string.IsNullOrWhiteSpace(unit) ||
               unit.Equals("gb", StringComparison.OrdinalIgnoreCase) ||
               unit.Equals("g", StringComparison.OrdinalIgnoreCase) ||
               unit.Contains("\u06af\u06cc\u06af", StringComparison.OrdinalIgnoreCase);
    }

    private static XuiV3DurationOption TryGetDurationFromText(XuiV3ServiceDefinition service, string text)
    {
        var key = ExtractBracketValue(text);
        return service.DurationOptions.FirstOrDefault(duration =>
            string.Equals(duration.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(duration.Key, text?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(duration.DisplayName, text?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static XuiV3UnlimitedPlan TryGetUnlimitedPlanFromText(XuiV3ServiceDefinition service, string text)
    {
        var key = ExtractBracketValue(text);
        return service.UnlimitedPlans.FirstOrDefault(plan =>
            plan.IsEnabled &&
            (string.Equals(plan.Key, key, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(plan.Key, text?.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(plan.DisplayName, text?.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private static int? TryGetGenericDurationDays(string text)
    {
        var key = ExtractBracketValue(text);
        if (int.TryParse(NormalizeDigits(key), out var days))
            return days;

        return TryGetIntFromText(text, out days) ? days : null;
    }

    private string BuildCreateSummary(User user)
    {
        var selection = BuildCreateSelection(user);
        var resolved = _purchaseService.ResolvePurchase(selection, false);
        var accountCount = XuiV3PurchaseService.NormalizeAccountCount(user.PendingAccountCount);
        var totalPrice = resolved.PriceToman * accountCount;
        var durationText = resolved.DurationDays <= 0 ? "نامحدود / لایف‌تایم" : $"{resolved.DurationDays} روز";
        var planText = resolved.IsUnlimited
            ? resolved.UnlimitedPlan?.DisplayName
            : resolved.Duration?.DisplayName;
        var commentText = string.IsNullOrWhiteSpace(user.PendingUserComment)
            ? "ندارد"
            : user.PendingUserComment;
        var trafficLabel = resolved.IsUnlimited ? "حد مصرف منصفانه هر اکانت" : "حجم هر اکانت";

        return "✅ خلاصه ساخت اکانت نسخه ۳\n\n" +
               $"👤 مالک اکانت: <code>{Html(user.ConfigLink)}</code>\n" +
               $"🧩 نوع سرویس: <code>{Html(resolved.Service.DisplayName)}</code>\n" +
               $"📌 پلن: <code>{Html(planText)}</code>\n" +
               $"📦 {trafficLabel}: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(resolved.TrafficBytes, resolved.TrafficGb))}</code>\n" +
               $"⏳ زمان: <code>{Html(durationText)}</code>\n" +
               $"👥 محدودیت کاربر/IP: <code>{resolved.LimitIp}</code>\n" +
               $"🔢 تعداد اکانت: <code>{accountCount}</code>\n" +
               $"💰 قیمت واحد: <code>{Html(resolved.PriceToman.FormatCurrency())}</code>\n" +
               $"💳 قیمت کل: <code>{Html(totalPrice.FormatCurrency())}</code>\n" +
               $"📝 کامنت: <code>{Html(commentText)}</code>\n" +
               $"🔗 Inbound IDs: <code>{Html(string.Join(",", resolved.Service.InboundIds ?? new List<int>()))}</code>\n\n" +
               "برای سوپرادمین، از کیف پول مبلغی کم نمی‌شود.";
    }

    private XuiV3PurchaseSelection BuildCreateSelection(User user)
    {
        var service = FindService(user.SelectedCountry);
        return service.IsUnlimited
            ? new XuiV3PurchaseSelection
            {
                ServiceKey = service.Key,
                UnlimitedPlanKey = user.Type,
                AccountCount = XuiV3PurchaseService.NormalizeAccountCount(user.PendingAccountCount),
                UserComment = user.PendingUserComment
            }
            : new XuiV3PurchaseSelection
            {
                ServiceKey = service.Key,
                TrafficGb = int.TryParse(user.TotoalGB, out var trafficGb) ? trafficGb : 0,
                DurationKey = user.SelectedPeriod,
                AccountCount = XuiV3PurchaseService.NormalizeAccountCount(user.PendingAccountCount),
                UserComment = user.PendingUserComment
            };
    }

    private async Task<XuiV3AccountCreationResult> CreateAdminAccountAsync(
        User currentUser,
        long actorTelegramUserId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(currentUser.ConfigLink, out var targetTelegramUserId))
            throw new InvalidOperationException("Target telegram user id is not valid.");

        var service = FindService(currentUser.SelectedCountry);
        var selection = service.IsUnlimited
            ? new XuiV3PurchaseSelection { ServiceKey = service.Key, UnlimitedPlanKey = currentUser.Type }
            : new XuiV3PurchaseSelection
            {
                ServiceKey = service.Key,
                TrafficGb = int.TryParse(currentUser.TotoalGB, out var trafficGb) ? trafficGb : 0,
                DurationKey = currentUser.SelectedPeriod
            };

        var targetUser = await _credentialsDbContext.GetUserStatusWithId(targetTelegramUserId)
                         ?? new CredUser
                         {
                             TelegramUserId = targetTelegramUserId,
                             ChatID = targetTelegramUserId,
                             AccountBalance = 0
                         };

        var serverInfo = BuildConfiguredPanelServerInfo();
        var creation = await _purchaseService.CreateAccountAsync(
            targetUser,
            serverInfo,
            selection,
            serverInfo.Url,
            cancellationToken,
            new XuiV3AccountMetadataOptions
            {
                CreatedByTelegramUserId = actorTelegramUserId,
                LastUpdatedByTelegramUserId = actorTelegramUserId,
                LastAction = "admin-create",
                SaveUserStatus = false
            });

        if (!creation.Success)
            return creation;

        var clientResponse = await ApiServicev3.GetClientAsync(serverInfo, _configuration, creation.Email, cancellationToken);
        if (clientResponse.Success && clientResponse.Obj != null)
        {
            var client = clientResponse.Obj;
            var metadata = TryReadMetadata(client.Comment) ?? new XuiV3ClientMetadata();
            metadata.TelegramUserId = client.TgId == 0 ? targetTelegramUserId : client.TgId;
            metadata.CreatedByTelegramUserId = actorTelegramUserId;
            metadata.LastUpdatedByTelegramUserId = actorTelegramUserId;
            metadata.LastAction = "admin-create";

            var updatedPayload = CopyClientPayload(client);
            updatedPayload.Comment = JsonConvert.SerializeObject(metadata, Formatting.None);

            var updateResponse = await ApiServicev3.UpdateClientAsync(
                serverInfo,
                _configuration,
                client.Email,
                updatedPayload,
                cancellationToken);

            if (updateResponse.Success)
                creation.Comment = updatedPayload.Comment;
        }

        return creation;
    }

    private async Task<XuiV3BulkCreationResult> CreateAdminAccountsAsync(
        User currentUser,
        long actorTelegramUserId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(currentUser.ConfigLink, out var targetTelegramUserId))
            throw new InvalidOperationException("Target telegram user id is not valid.");

        var selection = BuildCreateSelection(currentUser);
        var accountCount = XuiV3PurchaseService.NormalizeAccountCount(currentUser.PendingAccountCount);
        selection.AccountCount = accountCount;
        selection.UserComment = currentUser.PendingUserComment;

        var targetUser = await _credentialsDbContext.GetUserStatusWithId(targetTelegramUserId)
                         ?? new CredUser
                         {
                             TelegramUserId = targetTelegramUserId,
                             ChatID = targetTelegramUserId,
                             AccountBalance = 0
                         };
        var serverInfo = BuildConfiguredPanelServerInfo();
        return await _purchaseService.CreateBulkAccountsAsync(
            targetUser,
            serverInfo,
            selection,
            serverInfo.Url,
            new XuiV3BulkCreateOptions
            {
                AccountCount = accountCount,
                UserComment = currentUser.PendingUserComment,
                CreatedByTelegramUserId = actorTelegramUserId,
                LastUpdatedByTelegramUserId = actorTelegramUserId,
                LastAction = "admin-create",
                NextAccountCounter = 0,
                SaveUserStatus = false
            },
            cancellationToken);
    }

    private static string BuildAdminCreateLogMessage(
        User currentUser,
        long actorTelegramUserId,
        XuiV3AccountCreationResult creation)
    {
        var targetUserId = string.IsNullOrWhiteSpace(currentUser?.ConfigLink) ? "unknown" : currentUser.ConfigLink.Trim();
        var metadata = TryReadMetadata(creation?.Comment);
        var sb = new StringBuilder();
        sb.AppendLine("ساخت اکانت نسخه ۳ توسط ادمین");
        sb.AppendLine($"ادمین `{actorTelegramUserId}`");
        sb.AppendLine($"مالک `{targetUserId}`");
        sb.AppendLine($"تاریخ ساخت `{FormatMetadataCreatedAt(metadata)}`");

        if (!string.IsNullOrWhiteSpace(metadata?.ServiceName))
            sb.AppendLine($"سرویس `{metadata.ServiceName}`");

        sb.AppendLine($"نام اکانت `{creation.Email}`");
        sb.AppendLine($"حجم `{creation.TrafficGb} GB`");
        sb.AppendLine($"انقضا `{FormatExpiry(creation.ExpiryTime)}`");
        sb.AppendLine($"سابلینک `{creation.SubLink}`");
        return sb.ToString();
    }

    private static string BuildAdminBulkCreateLogMessage(
        User currentUser,
        long actorTelegramUserId,
        XuiV3BulkCreationResult result)
    {
        var targetUserId = string.IsNullOrWhiteSpace(currentUser?.ConfigLink) ? "unknown" : currentUser.ConfigLink.Trim();
        var metadata = TryReadMetadata(result.CreatedAccounts.FirstOrDefault()?.Comment);
        var sb = new StringBuilder();
        sb.AppendLine("ساخت انبوه اکانت نسخه ۳ توسط ادمین");
        sb.AppendLine($"ادمین `{actorTelegramUserId}`");
        sb.AppendLine($"مالک `{targetUserId}`");
        sb.AppendLine($"شناسه سفارش `{result.BulkOrderId}`");
        sb.AppendLine($"تاریخ ساخت `{FormatMetadataCreatedAt(metadata)}`");
        sb.AppendLine($"سرویس `{result.ServiceName}`");
        sb.AppendLine($"تعداد درخواست `{result.RequestedCount}`");
        sb.AppendLine($"تعداد موفق `{result.SuccessfulCount}`");
        sb.AppendLine($"{(string.Equals(metadata?.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase) ? "حد مصرف منصفانه هر اکانت" : "حجم هر اکانت")} `{XuiV3PurchaseService.FormatTrafficSize(result.TrafficBytes, result.TrafficGb)}`");
        sb.AppendLine($"مدت `{(result.DurationDays <= 0 ? "نامحدود" : result.DurationDays + " روز")}`");

        if (!string.IsNullOrWhiteSpace(metadata?.UserComment))
            sb.AppendLine($"کامنت کاربر `{ShortenForLog(metadata.UserComment, 120)}`");

        if (result.CreatedAccounts.Count > 0)
        {
            sb.AppendLine("اکانت‌های ساخته‌شده:");
            foreach (var account in result.CreatedAccounts)
                sb.AppendLine($"`{account.Email}`");
        }

        if (result.Failures.Count > 0)
        {
            sb.AppendLine("خطاها:");
            foreach (var failure in result.Failures)
                sb.AppendLine($"ردیف `{failure.Index}` - `{ShortenForLog(failure.Message, 120)}`");
        }

        return sb.ToString();
    }

    private static string BuildAdminBulkFailureText(XuiV3BulkCreationResult result)
    {
        var lines = new List<string>
        {
            "⚠️ بخشی از ساخت انبوه ناموفق بود.",
            "",
            $"تعداد درخواستی: <code>{result.RequestedCount}</code>",
            $"تعداد ساخته‌شده: <code>{result.SuccessfulCount}</code>"
        };

        foreach (var failure in result.Failures)
            lines.Add($"ردیف <code>{failure.Index}</code>: <code>{Html(ShortenForLog(failure.Message, 160))}</code>");

        return string.Join("\n", lines);
    }

    private static string ShortenForLog(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().Replace("\r", " ").Replace("\n", " ");
        return normalized.Length <= maxLength ? normalized : normalized.Substring(0, maxLength) + "...";
    }

    private static string BuildAdminRenewLogMessage(
        User currentUser,
        long actorTelegramUserId,
        XuiV3Client client,
        ServerInfo serverInfo,
        int addTrafficGb,
        int addDays)
    {
        var targetUserId = string.IsNullOrWhiteSpace(currentUser?.ConfigLink) ? client.TgId.ToString() : currentUser.ConfigLink.Trim();
        var metadata = TryReadMetadata(client?.Comment);
        var sb = new StringBuilder();
        sb.AppendLine("تمدید اکانت نسخه ۳ توسط ادمین");
        sb.AppendLine($"ادمین `{actorTelegramUserId}`");
        sb.AppendLine($"مالک `{targetUserId}`");
        sb.AppendLine($"تاریخ ساخت `{FormatMetadataCreatedAt(metadata)}`");

        if (!string.IsNullOrWhiteSpace(metadata?.ServiceName))
            sb.AppendLine($"سرویس `{metadata.ServiceName}`");

        sb.AppendLine($"نام اکانت `{client.Email}`");
        sb.AppendLine($"حجم اضافه `{addTrafficGb} GB`");
        sb.AppendLine($"زمان اضافه `{(addDays <= 0 ? "نامحدود" : $"{addDays} روز")}`");
        sb.AppendLine($"سابلینک `{ApiServicev3.BuildSubscriptionLink(serverInfo, client.SubId ?? client.Email)}`");
        return sb.ToString();
    }

    private static string BuildAdminDeleteExpiredLogMessage(
        CredUser actor,
        long targetTelegramUserId,
        IReadOnlyCollection<string> deleted,
        IReadOnlyCollection<string> failed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("حذف اکانت‌های منقضی نسخه ۳ توسط ادمین");
        sb.AppendLine($"تاریخ `{DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi()}`");
        sb.AppendLine($"ادمین `{FormatActor(actor)}`");
        sb.AppendLine($"کاربر هدف `{targetTelegramUserId}`");
        sb.AppendLine($"تعداد حذف‌شده `{deleted?.Count ?? 0}`");
        sb.AppendLine($"تعداد ناموفق `{failed?.Count ?? 0}`");

        if (deleted != null && deleted.Count > 0)
        {
            sb.AppendLine("اکانت‌های حذف‌شده:");
            foreach (var email in deleted)
                sb.AppendLine($"- `{email}`");
        }

        if (failed != null && failed.Count > 0)
        {
            sb.AppendLine("اکانت‌های حذف‌نشده:");
            foreach (var email in failed)
                sb.AppendLine($"- `{email}`");
        }

        return sb.ToString();
    }

    private static string FormatActor(CredUser actor)
    {
        if (actor == null)
            return "unknown";

        var parts = new List<string> { actor.TelegramUserId.ToString() };
        if (!string.IsNullOrWhiteSpace(actor.Username))
            parts.Add("@" + actor.Username.Trim().TrimStart('@'));

        var fullName = string.Join(" ", new[] { actor.FirstName, actor.LastName }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()));

        if (!string.IsNullOrWhiteSpace(fullName))
            parts.Add(fullName);

        return string.Join(" - ", parts);
    }

    private static string FormatMetadataCreatedAt(XuiV3ClientMetadata metadata)
    {
        if (metadata == null)
            return "نامشخص";

        return metadata.CreatedAtUtc.AddMinutes(210).ConvertToHijriShamsi();
    }

    private static string BuildCreatedAccountInfo(XuiV3AccountCreationResult creation)
    {
        var expiryText = FormatExpiry(creation.ExpiryTime);
        var metadata = TryReadMetadata(creation.Comment);
        var trafficLabel = string.Equals(metadata?.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase)
            ? "حد مصرف منصفانه"
            : "حجم";
        var text = "✅ اکانت نسخه ۳ با موفقیت ساخته شد.\n\n" +
               $"👤 نام اکانت: <code>{Html(creation.Email)}</code>\n" +
               $"📦 {trafficLabel}: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(creation.TrafficBytes, creation.TrafficGb))}</code>\n" +
               $"⏳ انقضا: <code>{Html(expiryText)}</code>\n" +
               $"🔗 Inbound IDs: <code>{Html(string.Join(",", creation.InboundIds ?? new List<int>()))}</code>\n\n" +
               "🔗 سابلینک:\n" +
               $"<code>{Html(creation.SubLink)}</code>";

        if (!string.IsNullOrWhiteSpace(metadata?.UserComment))
            text += $"\n\n📝 کامنت:\n<code>{Html(metadata.UserComment)}</code>";

        return text;
    }

    /// <summary>
    /// Builds the super-admin renewal preview from explicit traffic/day input and the latest XUI client state.
    /// </summary>
    /// <param name="user">
    /// Admin conversation state containing the target account email, resolved service key, traffic in GB, and duration
    /// in days. The account owner stored on XUI is preserved and is not replaced by this state's Telegram id.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read-only XUI lookup used to enrich the preview.</param>
    /// <returns>
    /// HTML-safe Persian summary describing exact traffic addition or expired-account replacement, reset behavior,
    /// and added duration. The method returns a plan-only fallback if panel lookup is unavailable.
    /// </returns>
    /// <remarks>
    /// This method does not update XUI. Unlimited calculations use the same shared policy as customer and tenant
    /// renewals: no quota is inferred from the final number of days.
    /// </remarks>
    private async Task<string> BuildRenewSummaryAsync(User user, CancellationToken cancellationToken)
    {
        var trafficGb = int.TryParse(user.TotoalGB, out var parsedTraffic) ? parsedTraffic : 0;
        var days = int.TryParse(user.SelectedPeriod, out var parsedDays) ? parsedDays : 0;
        var durationText = days <= 0 ? "نامحدود / لایف‌تایم" : $"{days} روز";
        XuiV3RenewalCalculation renewal = null;

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            var clientResponse = await ApiServicev3.GetClientAsync(serverInfo, _configuration, user.ConfigLink, cancellationToken);
            if (clientResponse.Success && clientResponse.Obj != null)
            {
                var service = TryFindService(user.SelectedCountry) ?? ResolveServiceForClient(clientResponse.Obj);
                renewal = XuiV3RenewalPolicy.CalculateAdmin(clientResponse.Obj, service, trafficGb, days, "admin-renew-summary", user.Id);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] admin renew summary lookup failed actor={user.Id}, email={user.ConfigLink}: {ex.Message}");
        }

        var trafficLine = renewal?.IsUnlimited == true
            ? renewal.ShouldResetTraffic
                ? $"📦 حجم جدید بعد از ریست مصرف: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(renewal.RenewedTrafficBytes))}</code>\n"
                : $"📦 حجم اضافه: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(renewal.RenewedTrafficBytes))}</code>\n" +
                  $"📦 حجم کل بعد از تمدید: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(renewal.TotalBytesAfterRenew))}</code>\n"
            : renewal?.ShouldResetTraffic == true
                ? $"📦 حجم جدید بعد از ریست مصرف: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(renewal.TargetAvailableTrafficBytes))}</code>\n"
                : $"📦 حجم اضافه: <code>{trafficGb} GB</code>\n";

        var resetLine = renewal?.ShouldResetTraffic == true
            ? "🔄 اکانت منقضی شده است؛ مصرف قبلی ریست می‌شود و حجم تمدید جایگزین حجم قبلی خواهد شد.\n"
            : "";

        return "✅ خلاصه تمدید اکانت نسخه ۳\n\n" +
               $"👤 اکانت: <code>{Html(user.ConfigLink)}</code>\n" +
               trafficLine +
               resetLine +
               $"⏳ زمان اضافه: <code>{Html(durationText)}</code>\n\n" +
               "مالک اکانت از روی خود پنل حفظ می‌شود و به آیدی سوپرادمین تغییر نمی‌کند.";
    }

    private static XuiV3ClientPayload CopyClientPayload(XuiV3Client client)
    {
        return new XuiV3ClientPayload
        {
            Email = client.Email,
            Uuid = client.Uuid,
            Password = client.Password,
            TotalGB = GetTotalBytes(client),
            ExpiryTime = GetExpiryTime(client),
            TgId = client.TgId,
            LimitIp = client.LimitIp,
            Enable = client.Enable,
            SubId = client.SubId,
            Flow = client.Flow,
            Comment = client.Comment,
            Group = client.Group,
            Reverse = client.Reverse,
            Extra = client.Extra
        };
    }

    /// <summary>
    /// Reads the configured traffic limit for an XUI v3 client without requiring the nested traffic object.
    /// </summary>
    /// <param name="client">
    /// XUI v3 client returned by the panel. The value may be null or may have a null <c>Traffic</c> property
    /// when the panel omits traffic counters from an account-info response.
    /// </param>
    /// <returns>
    /// Traffic limit in bytes. Returns <c>0</c> when the panel did not provide a limit in top-level fields,
    /// nested traffic fields, or the raw <c>Extra</c> dictionary.
    /// </returns>
    /// <remarks>
    /// The lookup order matches the v3 payload variants seen in production: top-level <c>TotalGB</c> first,
    /// then <c>Traffic.TotalGB</c>, then <c>Traffic.Total</c>, and finally <c>Extra["totalGB"]</c>.
    /// Keeping this helper null-safe prevents admin account-status and renewal screens from crashing when
    /// <c>traffic == null</c>.
    /// </remarks>
    private static long GetTotalBytes(XuiV3Client client)
    {
        if (client == null)
            return 0;

        if (client.TotalGB > 0)
            return client.TotalGB;

        var traffic = client.Traffic;
        if (traffic?.TotalGB > 0)
            return traffic.TotalGB;

        if (traffic?.Total > 0)
            return traffic.Total;

        return ReadLongExtra(client, "totalGB");
    }

    /// <summary>
    /// Reads consumed upload and download bytes for an XUI v3 client without assuming traffic counters exist.
    /// </summary>
    /// <param name="client">
    /// XUI v3 client returned by the panel. The nested <c>Traffic</c> property may be null.
    /// </param>
    /// <returns>
    /// Sum of uploaded and downloaded bytes. Missing counters are treated as zero and then resolved from
    /// <c>Extra["up"]</c> and <c>Extra["down"]</c> when available.
    /// </returns>
    /// <remarks>
    /// Admin account-info rendering calls this method for every result row. It must never throw for partially
    /// populated v3 clients because those exceptions stop the Telegram update handler.
    /// </remarks>
    private static long GetUsedBytes(XuiV3Client client)
    {
        if (client == null)
            return 0;

        var traffic = client.Traffic;
        return (traffic?.Up ?? ReadLongExtra(client, "up")) +
               (traffic?.Down ?? ReadLongExtra(client, "down"));
    }

    private static bool IsExpired(long expiryTime)
    {
        return expiryTime > 0 &&
               DateTimeOffset.FromUnixTimeMilliseconds(expiryTime).UtcDateTime < DateTime.UtcNow;
    }

    private static bool IsExpiredOrDepleted(XuiV3Client client)
    {
        if (client == null)
            return false;

        if (IsExpired(GetExpiryTime(client)))
            return true;

        var totalBytes = GetTotalBytes(client);
        var usedBytes = GetUsedBytes(client);
        return totalBytes > 0 && usedBytes >= totalBytes;
    }

    private static bool ClientBelongsToUser(XuiV3Client client, long telegramUserId)
    {
        if (client == null)
            return false;

        if (client.TgId == telegramUserId)
            return true;

        var metadata = TryReadMetadata(client.Comment);
        return metadata?.TelegramUserId == telegramUserId;
    }

    private static bool TryParseTelegramIdAndAccountCounter(string input, out long telegramUserId, out int accountCounter)
    {
        telegramUserId = 0;
        accountCounter = 0;

        var values = ParseTelegramUserIds(input);
        if (values.Count < 2)
            return false;

        telegramUserId = values[0];
        if (values[1] > int.MaxValue)
            return false;

        accountCounter = (int)values[1];
        return telegramUserId > 0 && accountCounter > 0;
    }

    private static bool ClientHasAccountCounter(XuiV3Client client, int accountCounter)
    {
        if (client == null || accountCounter <= 0)
            return false;

        var metadata = TryReadMetadata(client.Comment);
        if (metadata?.AccountCounter == accountCounter)
            return true;

        var normalizedEmail = NormalizeDigits(client.Email ?? string.Empty).Trim();
        return normalizedEmail.EndsWith("_" + accountCounter, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExpiryOrTrafficReason(XuiV3Client client)
    {
        var reasons = new List<string>();
        if (IsExpired(GetExpiryTime(client)))
            reasons.Add("اتمام زمان");

        var totalBytes = GetTotalBytes(client);
        var usedBytes = GetUsedBytes(client);
        if (totalBytes > 0 && usedBytes >= totalBytes)
            reasons.Add("اتمام حجم");

        return reasons.Count == 0 ? "نامشخص" : string.Join(" + ", reasons);
    }

    private static string BuildDeleteExpiredConfirmationText(IReadOnlyCollection<XuiV3Client> clients, bool isAdmin, long telegramUserId)
    {
        var builder = new StringBuilder();
        builder.AppendLine(isAdmin
            ? "🗑 لیست اکانت‌های منقضی کاربر مورد نظر"
            : "🗑 لیست اکانت‌های منقضی شما");
        builder.AppendLine();
        builder.AppendLine($"👤 تلگرام آیدی: <code>{telegramUserId}</code>");
        builder.AppendLine($"📦 تعداد: <code>{clients.Count}</code>");
        builder.AppendLine();

        foreach (var client in clients)
        {
            builder.AppendLine($"• <code>{Html(client.Email)}</code>");
            builder.AppendLine($"  علت: <code>{Html(GetExpiryOrTrafficReason(client))}</code>");
            builder.AppendLine($"  انقضا: <code>{Html(FormatDeleteExpiry(client))}</code>");
            builder.AppendLine($"  مصرف: <code>{Html(FormatDeleteTraffic(client))}</code>");
        }

        builder.AppendLine();
        builder.AppendLine("در صورت تایید، این اکانت‌ها از پنل نسخه ۳ حذف می‌شوند.");
        return builder.ToString();
    }

    private static string BuildDeleteExpiredResultText(
        IReadOnlyCollection<string> deleted,
        IReadOnlyCollection<string> failed,
        bool isAdmin,
        long telegramUserId)
    {
        var builder = new StringBuilder();
        builder.AppendLine(isAdmin
            ? "✅ نتیجه حذف اکانت‌های منقضی کاربر"
            : "✅ نتیجه حذف اکانت‌های منقضی شما");
        builder.AppendLine();
        builder.AppendLine($"👤 تلگرام آیدی: <code>{telegramUserId}</code>");
        builder.AppendLine($"🗑 حذف‌شده: <code>{deleted.Count}</code>");
        builder.AppendLine($"⚠️ ناموفق: <code>{failed.Count}</code>");

        if (deleted.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("اکانت‌های حذف‌شده:");
            foreach (var email in deleted)
                builder.AppendLine($"• <code>{Html(email)}</code>");
        }

        if (failed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("اکانت‌های حذف‌نشده:");
            foreach (var email in failed)
                builder.AppendLine($"• <code>{Html(email)}</code>");
        }

        return builder.ToString();
    }

    private static List<string> DeserializeEmailList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        try
        {
            return JsonConvert.DeserializeObject<List<string>>(raw) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string FormatDeleteExpiry(XuiV3Client client)
    {
        var expiryTime = GetExpiryTime(client);
        if (expiryTime < 0)
            return $"{FormatFirstUseDurationDays(expiryTime)} روز بعد از اولین اتصال";

        if (expiryTime == 0)
            return "نامحدود";

        var expiryUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiryTime).UtcDateTime;
        return expiryUtc.AddMinutes(210).ConvertToHijriShamsi();
    }

    private static string FormatDeleteTraffic(XuiV3Client client)
    {
        var usedBytes = GetUsedBytes(client);
        var totalBytes = GetTotalBytes(client);
        var usedText = $"{usedBytes.ConvertBytesToGB():0.##} GB";
        if (totalBytes <= 0)
            return $"{usedText} از نامحدود";

        return $"{usedText} از {totalBytes.ConvertBytesToGB():0.##} GB";
    }

    private static string ExtractBracketValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = text.LastIndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start
            ? text.Substring(start + 1, end - start - 1).Trim()
            : null;
    }

    private static string NormalizeDigits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var numericValue = char.GetNumericValue(ch);
            if (numericValue >= 0 && numericValue <= 9 && Math.Floor(numericValue) == numericValue)
                builder.Append((char)('0' + (int)numericValue));
            else
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string NormalizeUserComment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Trim();
        return normalized.Length <= 300 ? normalized : normalized.Substring(0, 300);
    }

    private async Task FinishWithMessageAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        User currentUser,
        IReplyMarkup mainMenu,
        string text,
        CancellationToken cancellationToken,
        ParseMode? parseMode = null)
    {
        await _userDbContext.ClearUserStatus(currentUser);
        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            replyMarkup: mainMenu,
            cancellationToken: cancellationToken);
    }

    private static string BuildClientInfo(XuiV3Client client, ServerInfo serverInfo)
    {
        var subId = string.IsNullOrWhiteSpace(client.SubId) ? client.Email : client.SubId;
        var subLink = ApiServicev3.BuildSubscriptionLink(serverInfo, subId);
        var metadata = TryReadMetadata(client.Comment);

        var text = $"👤 اکانت: <code>{Html(client.Email)}</code>\n" +
                   $"🆔 Client ID: <code>{client.Id}</code>\n" +
                   $"📌 وضعیت: <code>{(client.Enable ? "active" : "disabled")}</code>\n" +
                   $"👥 تلگرام آیدی: <code>{client.TgId}</code>\n" +
                   $"📦 مصرف: <code>{FormatTraffic(client)}</code>\n" +
                   $"📅 انقضا: <code>{Html(FormatExpiry(GetExpiryTime(client)))}</code>\n" +
                   $"🔗 سابلینک: <code>{Html(subLink)}</code>\n";

        if (metadata != null)
        {
            var metadataTrafficLabel = string.Equals(metadata.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase)
                ? "حد مصرف منصفانه"
                : "حجم";
            text += "\n🧾 متادیتا:\n" +
                    $"نوع سرویس: <code>{Html(metadata.ServiceKey)}</code>\n" +
                    $"نام سرویس: <code>{Html(metadata.ServiceName)}</code>\n" +
                    $"پلن: <code>{Html(metadata.PlanKey)}</code>\n" +
                    $"{metadataTrafficLabel}: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(metadata.TrafficBytes, metadata.TrafficGb))}</code>\n" +
                    $"مدت: <code>{metadata.DurationDays} days</code>\n" +
                    $"قیمت: <code>{Html(metadata.PriceToman.FormatCurrency())}</code>\n" +
                    $"اینباندها: <code>{Html(string.Join(",", metadata.InboundIds ?? new List<int>()))}</code>\n";

            if (!string.IsNullOrWhiteSpace(metadata.UserComment))
                text += $"کامنت کاربر: <code>{Html(metadata.UserComment)}</code>\n";
        }

        return text;
    }

    private static async Task SendAccountInfoResultsAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        IReadOnlyList<XuiV3Client> matches,
        ServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        if (matches.Count <= MaxDetailedAccountInfoMessages)
        {
            foreach (var client in matches)
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: BuildClientInfo(client, serverInfo),
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }

            return;
        }

        foreach (var text in BuildCompactAccountInfoMessages(matches))
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
    }

    private static IEnumerable<string> BuildCompactAccountInfoMessages(IReadOnlyList<XuiV3Client> clients)
    {
        var header = $"Found <code>{clients.Count}</code> accounts. Sending compact list to avoid Telegram flood limits.\n";
        header += "For full details, search the exact email/client name.\n\n";

        var builder = new StringBuilder(header);
        for (var index = 0; index < clients.Count; index++)
        {
            var line = BuildCompactAccountInfoLine(clients[index], index + 1);
            if (builder.Length > header.Length && builder.Length + line.Length > MaxTelegramTextLength)
            {
                yield return builder.ToString();
                builder.Clear();
                builder.Append(header);
            }

            builder.Append(line);
        }

        if (builder.Length > header.Length)
            yield return builder.ToString();
    }

    private static string BuildCompactAccountInfoLine(XuiV3Client client, int index)
    {
        var status = client.Enable ? "active" : "disabled";
        var email = string.IsNullOrWhiteSpace(client.Email) ? "-" : client.Email;
        var traffic = FormatTraffic(client);
        var expiry = FormatExpiry(GetExpiryTime(client));
        return $"{index}. <code>{Html(email)}</code> ID:<code>{client.Id}</code> <code>{status}</code> <code>{Html(traffic)}</code> exp:<code>{Html(expiry)}</code>\n";
    }

    /// <summary>
    /// Builds the HTML status report shown to super-admins after checking a NOWPayments row.
    /// </summary>
    /// <param name="payment">
    /// Local users.db NOWPayments row selected by order id, invoice id, or payment id.
    /// </param>
    /// <param name="data">
    /// Merged local/provider payment data used to show provider status, amount, currency, and hashes.
    /// </param>
    /// <param name="settlement">
    /// Optional settlement result. <see cref="NowPaymentsSettlementStatus.ProviderNotPaid"/> adds a warning that no
    /// balance was credited and the admin must confirm the payment inside NOWPayments first.
    /// </param>
    /// <returns>HTML-safe Telegram text for the super-admin manual status response.</returns>
    /// <remarks>
    /// This method is display-only. It must never mutate the payment row or settle a balance; settlement safety is
    /// enforced by <see cref="NowPaymentsSettlementService.ApplyManualConfirmationAsync"/>.
    /// </remarks>
    private static string BuildPaymentInfo(
        SwapinoPaymentInfo payment,
        NowPaymentsPaymentRecordData data,
        NowPaymentsSettlementResult settlement)
    {
        var providerNotPaidWarning = settlement?.Status == NowPaymentsSettlementStatus.ProviderNotPaid ||
                                     string.Equals(payment.ErrorCode, "nowpayments_provider_not_paid", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(payment.ErrorCode, "nowpayments_provider_check_failed", StringComparison.OrdinalIgnoreCase)
            ? "\n\n⚠️ <b>این پرداخت از سمت NOWPayments تایید کامل نشده است و هیچ مبلغی به موجودی کاربر اضافه نشد.</b>\n" +
              "اگر پرداخت ناقص، wrong asset یا مشکل‌دار است، ابتدا آن را داخل پنل NOWPayments تایید/Force کنید و سپس دوباره همین بررسی را در ربات انجام دهید.\n" +
              "به وضعیت محلی قبلی اعتماد نمی‌شود؛ معیار فقط وضعیت تازه‌ای است که NOWPayments در همین بررسی برمی‌گرداند.\n" +
              $"جزئیات خطا: <code>{Html(payment.ErrorMessage)}</code>"
            : string.Empty;

        return "وضعیت پرداخت NOWPayments\n\n" +
               $"Order ID: <code>{Html(payment.OrderId)}</code>\n" +
               $"Invoice ID: <code>{Html(payment.InvoiceId ?? data.InvoiceId)}</code>\n" +
               $"Payment ID: <code>{Html(payment.PaymentId ?? data.PaymentId)}</code>\n" +
               $"Telegram User: <code>{payment.TelegramUserId}</code>\n" +
               $"Amount Toman: <code>{Html(payment.AmountToman.FormatCurrency())}</code>\n" +
               $"Base: <code>{Html(FormatDecimal(payment.BaseAmount == 0 ? data.PriceAmount : payment.BaseAmount))} {Html(payment.BaseCurrency ?? data.PriceCurrency)}</code>\n" +
               $"Pay: <code>{Html(FormatDecimal(data.PayAmount))} {Html(payment.PayCurrency ?? data.PayCurrency)}</code>\n" +
               $"Actually Paid: <code>{Html(FormatDecimal(payment.ActuallyPaid == 0 ? data.ActuallyPaid : payment.ActuallyPaid))}</code>\n" +
               $"Status: <code>{Html(payment.PaymentStatus ?? data.PaymentStatus)}</code>\n" +
               $"Added To Balance: <code>{payment.IsAddedToBalance}</code>\n" +
               $"Settlement: <code>{Html(settlement?.Status.ToString() ?? "not-applied")}</code>\n" +
               $"Balance Before: <code>{Html(payment.BalanceBefore?.FormatCurrency())}</code>\n" +
               $"Balance After: <code>{Html(payment.BalanceAfter?.FormatCurrency())}</code>\n" +
               $"Payin Hash: <code>{Html(payment.PayinHash ?? data.PayinHash)}</code>\n" +
               $"Payout Hash: <code>{Html(payment.PayoutHash ?? data.PayoutHash)}</code>\n" +
               $"Invoice URL: <code>{Html(payment.InvoiceUrl ?? data.InvoiceUrl)}</code>" +
               providerNotPaidWarning;
    }

    /// <summary>
    /// Builds the HTML status summary shown to super-admins after a HooshPay manual check.
    /// </summary>
    /// <param name="payment">Local HooshPay payment row.</param>
    /// <param name="settlement">Optional settlement result when the payment was paid and settlement ran.</param>
    /// <returns>HTML-formatted payment status text.</returns>
    private static string BuildHooshPayPaymentInfo(
        HooshPayPaymentInfo payment,
        NowPaymentsSettlementResult settlement)
    {
        return "وضعیت پرداخت HooshPay\n\n" +
               $"Order ID: <code>{Html(payment.OrderId)}</code>\n" +
               $"Invoice UID: <code>{Html(payment.InvoiceUid)}</code>\n" +
               $"Telegram User: <code>{payment.TelegramUserId}</code>\n" +
               $"Chat ID: <code>{payment.ChatId}</code>\n" +
               $"Amount Toman: <code>{Html(payment.AmountToman.FormatCurrency())}</code>\n" +
               $"Payable Amount: <code>{Html(payment.PayableAmountToman.FormatCurrency())}</code>\n" +
               $"Merchant Credit: <code>{Html(payment.MerchantCreditToman.FormatCurrency())}</code>\n" +
               $"Fee: <code>{Html(payment.FeeAmountToman.FormatCurrency())}</code>\n" +
               $"Fee Percent: <code>{payment.FeePercent}</code>\n" +
               $"Fee Mode: <code>{Html(payment.FeeMode)}</code>\n" +
               $"Status: <code>{Html(payment.PaymentStatus)}</code>\n" +
               $"Tracking Code: <code>{Html(payment.TrackingCode)}</code>\n" +
               $"Added To Balance: <code>{payment.IsAddedToBalance}</code>\n" +
               $"Provisional Approval: <code>{payment.IsProvisionallyApproved}</code>\n" +
               $"Provisional Approved At: <code>{Html(payment.ProvisionalApprovedAtUtc?.AddMinutes(210).ConvertToHijriShamsi())}</code>\n" +
               $"Provisional Approved By: <code>{payment.ProvisionalApprovedByTelegramUserId}</code>\n" +
               $"Provider Confirmed After Provisional: <code>{Html(payment.ProviderConfirmedAfterProvisionalAtUtc?.AddMinutes(210).ConvertToHijriShamsi())}</code>\n" +
               $"Settlement: <code>{Html(settlement?.Status.ToString() ?? "not-applied")}</code>\n" +
               $"Balance Before: <code>{Html(payment.BalanceBefore?.FormatCurrency())}</code>\n" +
               $"Balance After: <code>{Html(payment.BalanceAfter?.FormatCurrency())}</code>\n" +
               $"Payment URL: <code>{Html(payment.PaymentUrl)}</code>";
    }

    /// <summary>
    /// Converts globally configured XuiV3 panel settings into the <see cref="ServerInfo"/> used by admin operations.
    /// </summary>
    /// <returns>Configured XuiV3 panel descriptor.</returns>
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

    /// <summary>
    /// Loads the credential user used as the actor in activity logs.
    /// </summary>
    /// <param name="telegramUserId">Telegram user id of the admin actor.</param>
    /// <returns>Credential user row, or a minimal fallback row when the admin is not in credentials.db.</returns>
    private async Task<CredUser> GetActivityActorAsync(long telegramUserId)
    {
        return await _credentialsDbContext.GetUserStatusWithId(telegramUserId)
               ?? new CredUser
               {
                   TelegramUserId = telegramUserId,
                   ChatID = telegramUserId
               };
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

    /// <summary>
    /// Formats traffic usage for admin account-info messages.
    /// </summary>
    /// <param name="client">
    /// XUI v3 client whose traffic may be present only in top-level fields or the raw <c>Extra</c> dictionary.
    /// </param>
    /// <returns>
    /// Human-readable traffic usage such as <c>1.25 GB / 10.00 GB</c> or <c>1.25 GB / unlimited</c>.
    /// </returns>
    /// <remarks>
    /// This method delegates to the null-safe traffic helpers so rendering admin account details remains stable
    /// for panel responses that omit <c>traffic</c>.
    /// </remarks>
    private static string FormatTraffic(XuiV3Client client)
    {
        var usedBytes = GetUsedBytes(client);
        var totalBytes = GetTotalBytes(client);
        if (totalBytes <= 0)
            return $"{usedBytes.ConvertBytesToGB():F2} GB / unlimited";

        return $"{usedBytes.ConvertBytesToGB():F2} GB / {totalBytes.ConvertBytesToGB():F2} GB";
    }

    /// <summary>
    /// Reads the client expiry timestamp without requiring the nested XUI traffic object.
    /// </summary>
    /// <param name="client">
    /// XUI v3 client returned by the panel. The value may be null, and <c>Traffic</c> may be absent.
    /// </param>
    /// <returns>
    /// Expiry timestamp in Unix milliseconds, <c>0</c> for unlimited/no expiry, or a negative first-use duration
    /// when the panel stores first-connection validity.
    /// </returns>
    /// <remarks>
    /// Lookup order is top-level <c>ExpiryTime</c>, nested <c>Traffic.ExpiryTime</c>, then
    /// <c>Extra["expiryTime"]</c>. The null guard is required because admin account status must not crash when
    /// 3x-ui returns <c>traffic: null</c>.
    /// </remarks>
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

    private static string FormatExpiry(long expiryTime)
    {
        if (expiryTime < 0)
            return $"{FormatFirstUseDurationDays(expiryTime)} روز بعد از اولین اتصال";

        if (expiryTime == 0)
            return "نامحدود";

        return DateTimeOffset.FromUnixTimeMilliseconds(expiryTime)
            .UtcDateTime
            .AddMinutes(210)
            .ConvertToHijriShamsi();
    }

    private static int FormatFirstUseDurationDays(long expiryTime)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Abs(expiryTime) / (double)TimeSpan.FromDays(1).TotalMilliseconds));
    }

    private static long ReadLongExtra(XuiV3Client client, string key)
    {
        if (client?.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null)
            return 0;

        return token.Type == Newtonsoft.Json.Linq.JTokenType.Integer ||
               token.Type == Newtonsoft.Json.Linq.JTokenType.Float ||
               token.Type == Newtonsoft.Json.Linq.JTokenType.String
            ? token.ToObject<long>()
            : 0;
    }

    private static string Html(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
    }
}
