using Adminbot.Domain;
using Adminbot.Utils;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using System.Text;
using Adminbot.Domain.Logging;

public class XuiV3BotFlowService
{
    private const string RenewFlowName = "xui-v3-renew";
    private const string RenewStepAccount = "renew-account";
    private const string RenewStepTraffic = "renew-traffic";
    private const string RenewStepDuration = "renew-duration";
    private const string RenewStepUnlimitedPlan = "renew-unlimited-plan";
    private const string RenewStepConfirm = "renew-confirm";
    private const string DeleteExpiredFlowName = "xui-v3-delete-expired";
    private const string DeleteExpiredStepConfirm = "delete-expired-confirm";
    private const string AccountSearchFlowName = "xui-v3-account-search";
    private const string AccountSearchStepQuery = "account-search-query";
    private const string AccountSearchStepResults = "account-search-results";
    private const string ExternalUuidRenewPaymentMethod = "xui-v3-uuid-renew";
    private const string PurchaseFlowName = "xui-v3";
    private const string PurchaseStepSelectService = "select-service";
    private const string PurchaseStepSelectTraffic = "select-traffic";
    private const string PurchaseStepSelectDuration = "select-duration";
    private const string PurchaseStepSelectUnlimitedPlan = "select-unlimited-plan";
    private const string PurchaseStepAccountCount = "select-account-count";
    private const string PurchaseStepUserComment = "select-user-comment";
    private const string PurchaseStepConfirm = "confirm";
    private const string TrialFlowName = "xui-v3-trial";
    private const string TrialStepSelectService = "trial-select-service";
    private const string ColleagueRequestFlowName = "colleague-request";
    private const string ColleagueRequestStepConfirm = "colleague-request-confirm";
    private const string SkipCommentText = "ادامه بدون کامنت";
    private const int TrialDays = 3;
    private const long NationalTrialBytes = 100L * 1024L * 1024L;
    private const long NormalTrialBytes = 1L * 1024L * 1024L * 1024L;
    private const long MinimumWeeklyColleagueSalesToman = 5_000_000L;
    private const int AccountListPageSize = 20;

    private readonly XuiV3PurchaseService _purchaseService;
    private readonly XuiV3PurchaseSessionStore _sessionStore;
    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly ILogger<XuiV3BotFlowService> _logger;
    private readonly UserActivityLogService _activityLog;

    public XuiV3BotFlowService(
        XuiV3PurchaseService purchaseService,
        XuiV3PurchaseSessionStore sessionStore,
        UserDbContext userDbContext,
        CredentialsDbContext credentialsDbContext,
        IConfiguration configuration,
        ILogger<XuiV3BotFlowService> logger,
        UserActivityLogService activityLog)
    {
        _purchaseService = purchaseService;
        _sessionStore = sessionStore;
        _userDbContext = userDbContext;
        _credentialsDbContext = credentialsDbContext;
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
        _activityLog = activityLog;
    }

    public bool IsEnabledForPurchaseFlow()
    {
        return string.Equals(_appConfig.XuiApiVersionMode, "v3", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> TryHandleMyAccountsAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || message?.Text != "وضعیت اکانت های من")
            return false;

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "لطفاً چند ثانیه صبر کنید. در حال دریافت اکانت‌های شما از پنل نسخه ۳ هستم...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            Console.WriteLine($"[XUIv3] my accounts start user={credUser.TelegramUserId} panel={serverInfo.Url}, rootPath={serverInfo.RootPath}");

            var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
            if (!response.Success)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"دریافت اکانت‌ها ناموفق بود.\n{response.Msg}",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            var accounts = response.Obj?
                .Where(client => ClientBelongsToUser(client, credUser.TelegramUserId))
                .OrderBy(client => client.Email)
                .ToList() ?? new List<XuiV3Client>();

            Console.WriteLine($"[XUIv3] my accounts found user={credUser.TelegramUserId} count={accounts.Count}");

            if (accounts.Count == 0)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "شما هنوز هیچ اکانتی از مجموعه ما ندارید.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            await SendV3AccountListPageAsync(botClient, message.Chat.Id, 0, credUser, cancellationToken);


            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] my accounts exception user={credUser.TelegramUserId}: {ex}");
            await _activityLog.LogErrorAsync(
                "xui_v3_my_accounts_failed",
                ex,
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["panelUrl"] = _appConfig.XuiV3ApiBaseUrl ?? string.Empty
                },
                cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "در دریافت وضعیت اکانت‌ها خطا رخ داد. جزئیات در ترمینال ثبت شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }
    }

    public async Task<bool> TryHandleAccountCounterLookupAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() ||
            message?.Text == null ||
            credUser?.IsColleague != true ||
            !string.IsNullOrWhiteSpace(user?.Flow) ||
            !TryParseAccountCounterLookup(message.Text, out var accountCounter))
        {
            return false;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال جست‌وجوی اکانت نسخه ۳ با شماره اکانت...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var serverInfo = BuildConfiguredPanelServerInfo();
        var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!response.Success)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"دریافت اکانت‌ها ناموفق بود.\n{response.Msg}",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        var client = response.Obj?
            .Where(item => ClientBelongsToUser(item, credUser.TelegramUserId))
            .FirstOrDefault(item => ClientHasAccountCounter(item, accountCounter));

        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"اکانتی با شماره <code>{accountCounter}</code> برای حساب شما پیدا نشد.",
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: BuildV3ClientInfo(client, serverInfo, credUser.IsColleague),
            parseMode: ParseMode.Html,
            replyMarkup: BuildAccountDetailsKeyboard(client, 0, credUser.IsColleague),
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task<bool> TryHandleAccountSearchAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || string.IsNullOrWhiteSpace(message?.Text))
            return false;

        var text = message.Text.Trim();
        var isStart = string.Equals(text, "🔎 جستجوی اکانت", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(text, "جستجوی اکانت", StringComparison.OrdinalIgnoreCase);

        if (user?.Flow != AccountSearchFlowName && !isStart)
            return false;

        if (IsCancel(text))
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "جستجوی اکانت لغو شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (isStart || user?.LastStep == null)
        {
            await StartAccountSearchAsync(botClient, message.Chat.Id, message.From.Id, cancellationToken);
            return true;
        }

        if (user.LastStep != AccountSearchStepQuery && user.LastStep != AccountSearchStepResults)
            return false;

        var query = text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "عبارت جستجو خالی است. نام اکانت، بخشی از کامنت، یا UUID کامل کانفیگ را بفرستید.",
                cancellationToken: cancellationToken);
            return true;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = message.From.Id,
            Flow = AccountSearchFlowName,
            LastStep = AccountSearchStepResults,
            ConfigLink = query
        });

        await SendAccountSearchResultsAsync(
            botClient,
            message.Chat.Id,
            credUser,
            query,
            0,
            cancellationToken);

        return true;
    }

    public async Task<bool> TryHandleAccountActionAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || string.IsNullOrWhiteSpace(message?.Text))
            return false;

        var text = message.Text.Trim();
        var enable = text.StartsWith("/enable_", StringComparison.OrdinalIgnoreCase);
        var disable = text.StartsWith("/disable_", StringComparison.OrdinalIgnoreCase);
        if (!enable && !disable)
            return false;

        var email = enable
            ? text.Substring("/enable_".Length)
            : text.Substring("/disable_".Length);

        if (string.IsNullOrWhiteSpace(email))
            return false;

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "لطفاً چند لحظه صبر کنید. در حال اعمال تغییر روی پنل نسخه ۳ هستم...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            var client = await FindClientByEmailAsync(serverInfo, email, credUser.TelegramUserId, cancellationToken);
            if (client == null || !ClientBelongsToUser(client, credUser.TelegramUserId))
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "اکانت مورد نظر پیدا نشد یا متعلق به حساب شما نیست.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            var updateResponse = await ApiServicev3.SetClientEnabledAsync(
                serverInfo,
                _configuration,
                client.Email ?? email,
                enable,
                credUser.TelegramUserId,
                cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: updateResponse.Success
                    ? "عملیات مورد نظر با موفقیت انجام شد."
                    : $"متاسفانه عملیات مورد نظر انجام نشد.\n{updateResponse.Msg}",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);

            if (updateResponse.Success)
            {
                await _activityLog.LogBotActionAsync(
                    enable ? "xui_v3_account_enabled" : "xui_v3_account_disabled",
                    credUser,
                    false,
                    new Dictionary<string, object>
                    {
                        ["accountEmail"] = email,
                        ["panelUrl"] = serverInfo.Url,
                        ["rootPath"] = serverInfo.RootPath
                    },
                    cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] account action exception user={credUser.TelegramUserId}, email={email}: {ex}");
            await _activityLog.LogErrorAsync(
                "xui_v3_account_action_failed",
                ex,
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["accountEmail"] = email,
                    ["requestedAction"] = enable ? "enable" : "disable"
                },
                cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "در انجام عملیات خطا رخ داد. جزئیات در ترمینال ثبت شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }
    }

    public async Task<bool> TryHandleRenewAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || string.IsNullOrWhiteSpace(message?.Text))
            return false;

        if (user?.Flow == RenewFlowName)
        {
            if (!await EnsurePhoneVerifiedAsync(botClient, message.Chat.Id, credUser, cancellationToken))
                return true;

            await HandleRenewFlowStepAsync(botClient, message, credUser, user, mainReplyMarkup, cancellationToken);
            return true;
        }

        var text = message.Text.Trim();
        if (text == "تمدید اکانت")
        {
            if (!await EnsurePhoneVerifiedAsync(botClient, message.Chat.Id, credUser, cancellationToken))
                return true;

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = "update",
                LastStep = "Renew Existing Account"
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "نام اکانت نسخه ۳ را برای تمدید ارسال کنید:",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (user?.Flow == "update" && user.LastStep == "Renew Existing Account" && !text.StartsWith("/renew_", StringComparison.OrdinalIgnoreCase))
            text = "/renew_" + text;

        if (!text.StartsWith("/renew_", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!await EnsurePhoneVerifiedAsync(botClient, message.Chat.Id, credUser, cancellationToken))
            return true;

        var email = NormalizeAccountNameInput(text.Substring("/renew_".Length));
        if (string.IsNullOrWhiteSpace(email))
            return false;

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "لطفاً چند لحظه صبر کنید. اکانت از پنل نسخه ۳ بررسی می‌شود...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            Console.WriteLine($"[XUIv3] renew command user={credUser.TelegramUserId}, email={email}, panel={serverInfo.Url}, rootPath={serverInfo.RootPath}");

            var client = await FindClientByEmailAsync(serverInfo, email, credUser.TelegramUserId, cancellationToken);
            if (client == null)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "اکانت مورد نظر پیدا نشد یا متعلق به حساب شما نیست.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            if (!ClientBelongsToUser(client, credUser.TelegramUserId))
            {
                Console.WriteLine($"[XUIv3] renew owner mismatch requestedUser={credUser.TelegramUserId}, email={client.Email}, clientTgId={client.TgId}, metadataUser={TryReadMetadata(client.Comment)?.TelegramUserId}");
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "اکانت پیدا شد، ولی مالک آن با حساب تلگرام شما یکی نیست.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            var service = ResolveServiceForClient(client);
            if (service == null)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "برای این اکانت متادیتای سرویس نسخه ۳ پیدا نشد. لطفاً تمدید این اکانت را از بخش مدیریت انجام دهید.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = RenewFlowName,
                LastStep = service.IsUnlimited ? RenewStepUnlimitedPlan : RenewStepTraffic,
                ConfigLink = client.Email,
                SelectedCountry = service.Key
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: service.IsUnlimited
                    ? "پلن تمدید نامحدود را انتخاب کنید:"
                    : "حجم تمدید را انتخاب کنید:\nمی‌توانید یکی از دکمه‌ها را بزنید یا حجم دلخواه را به صورت عدد صحیح بفرستید؛ مثلا 7 یا ۷.",
                replyMarkup: service.IsUnlimited ? BuildUnlimitedPlanReplyKeyboard(service, credUser.IsColleague) : BuildTrafficReplyKeyboard(service),
                cancellationToken: cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] renew command exception user={credUser.TelegramUserId}, email={email}: {ex}");
            await _activityLog.LogErrorAsync(
                "xui_v3_renew_start_failed",
                ex,
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["accountEmail"] = email
                },
                cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "در شروع تمدید نسخه ۳ خطا رخ داد. جزئیات در ترمینال ثبت شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }
    }

    public async Task<bool> TryHandleDeleteExpiredAccountsAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || string.IsNullOrWhiteSpace(message?.Text))
            return false;

        var text = message.Text.Trim();

        if (user?.Flow == DeleteExpiredFlowName && user.LastStep == DeleteExpiredStepConfirm)
        {
            if (IsCancel(text) || string.Equals(text, "انصراف", StringComparison.OrdinalIgnoreCase))
            {
                await _userDbContext.ClearUserStatus(user);
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "عملیات حذف اکانت‌های منقضی لغو شد.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            if (!string.Equals(text, "تایید حذف اکانت های منقضی", StringComparison.OrdinalIgnoreCase))
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "برای حذف اکانت‌های منقضی، دکمه تایید را بزنید یا انصراف دهید.",
                    replyMarkup: BuildDeleteExpiredConfirmKeyboard(),
                    cancellationToken: cancellationToken);
                return true;
            }

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "لطفاً چند لحظه صبر کنید. در حال حذف اکانت‌های منقضی شما از پنل نسخه ۳ هستم...",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);

            try
            {
                var emails = DeserializeEmailList(user.SubLink);
                if (emails.Count == 0)
                {
                    await _userDbContext.ClearUserStatus(user);
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "لیست اکانت‌های منقضی خالی است. دوباره از منوی مدیریت اکانت اقدام کنید.",
                        replyMarkup: mainReplyMarkup,
                        cancellationToken: cancellationToken);
                    return true;
                }

                var serverInfo = BuildConfiguredPanelServerInfo();
                var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
                if (!clientsResponse.Success)
                {
                    await _userDbContext.ClearUserStatus(user);
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: $"دریافت اطلاعات اکانت‌ها ناموفق بود.\n{clientsResponse.Msg}",
                        replyMarkup: mainReplyMarkup,
                        cancellationToken: cancellationToken);
                    return true;
                }

                var eligibleClients = clientsResponse.Obj?
                    .Where(client => emails.Contains(client.Email, StringComparer.OrdinalIgnoreCase))
                    .Where(client => ClientBelongsToUser(client, credUser.TelegramUserId))
                    .Where(IsExpiredOrDepleted)
                    .OrderBy(client => client.Email)
                    .ToList() ?? new List<XuiV3Client>();

                if (eligibleClients.Count == 0)
                {
                    await _userDbContext.ClearUserStatus(user);
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "اکانت منقضی قابل حذفی برای شما پیدا نشد.",
                        replyMarkup: mainReplyMarkup,
                        cancellationToken: cancellationToken);
                    return true;
                }

                var eligibleEmails = eligibleClients.Select(client => client.Email).ToList();
                var bulkDeleteResponse = await ApiServicev3.BulkDeleteClientsAsync(serverInfo, _configuration, eligibleEmails, cancellationToken);
                var deleted = bulkDeleteResponse.Success ? eligibleEmails : new List<string>();
                var failed = bulkDeleteResponse.Success ? new List<string>() : eligibleEmails;

                await _userDbContext.ClearUserStatus(user);

                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: BuildDeleteExpiredResultText(deleted, failed, false, credUser.TelegramUserId),
                    parseMode: ParseMode.Html,
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);

                if (deleted.Count > 0)
                {
                    await _activityLog.LogBotActionAsync(
                        "xui_v3_expired_accounts_deleted",
                        credUser,
                        false,
                        new Dictionary<string, object>
                        {
                            ["deletedCount"] = deleted.Count,
                            ["failedCount"] = failed.Count,
                            ["deletedAccounts"] = string.Join(",", deleted)
                        },
                        cancellationToken);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XUIv3] delete expired accounts exception user={credUser.TelegramUserId}: {ex}");
                await _activityLog.LogErrorAsync(
                    "xui_v3_delete_expired_accounts_failed",
                    ex,
                    credUser,
                    false,
                    new Dictionary<string, object>
                    {
                        ["telegramUserId"] = credUser.TelegramUserId
                    },
                    cancellationToken);
                await _userDbContext.ClearUserStatus(user);
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "در حذف اکانت‌های منقضی خطا رخ داد. جزئیات در لاگ ثبت شد.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }
        }

        if (!string.Equals(text, "حذف اکانت های منقضی", StringComparison.OrdinalIgnoreCase))
            return false;

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "لطفاً چند لحظه صبر کنید. در حال بررسی اکانت‌های منقضی شما روی پنل نسخه ۳ هستم...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
            if (!response.Success)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"دریافت اطلاعات اکانت‌ها ناموفق بود.\n{response.Msg}",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            var expiredClients = response.Obj?
                .Where(client => ClientBelongsToUser(client, credUser.TelegramUserId))
                .Where(IsExpiredOrDepleted)
                .OrderBy(client => client.Email)
                .ToList() ?? new List<XuiV3Client>();

            if (expiredClients.Count == 0)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "هیچ اکانت منقضی یا تمام‌شده‌ای برای شما پیدا نشد.",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = DeleteExpiredFlowName,
                LastStep = DeleteExpiredStepConfirm,
                SubLink = JsonConvert.SerializeObject(expiredClients.Select(client => client.Email).ToList())
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: BuildDeleteExpiredConfirmationText(expiredClients, false, credUser.TelegramUserId),
                parseMode: ParseMode.Html,
                replyMarkup: BuildDeleteExpiredConfirmKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] delete expired accounts prepare exception user={credUser.TelegramUserId}: {ex}");
            await _activityLog.LogErrorAsync(
                "xui_v3_delete_expired_accounts_prepare_failed",
                ex,
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["telegramUserId"] = credUser.TelegramUserId
                },
                cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "در بررسی اکانت‌های منقضی خطا رخ داد. جزئیات در لاگ ثبت شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }
    }

    private async Task HandleRenewFlowStepAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (IsCancel(message.Text))
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "تمدید لغو شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        var service = FindService(user.SelectedCountry);
        if (service == null)
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "سرویس تمدید پیدا نشد. فایل پلن‌های نسخه ۳ را بررسی کنید.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == RenewStepTraffic)
        {
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
                Flow = RenewFlowName,
                LastStep = RenewStepDuration,
                TotoalGB = trafficGb.ToString()
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "مدت تمدید را انتخاب کنید:",
                replyMarkup: BuildDurationReplyKeyboard(service),
                cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == RenewStepDuration)
        {
            var duration = TryGetDurationFromText(service, message.Text);
            if (duration == null)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "مدت معتبر نیست. یکی از گزینه‌های لیست را انتخاب کنید.",
                    replyMarkup: BuildDurationReplyKeyboard(service),
                    cancellationToken: cancellationToken);
                return;
            }

            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = RenewFlowName,
                LastStep = RenewStepConfirm,
                SelectedPeriod = duration.Key
            });

            var refreshedUser = await _userDbContext.GetUserStatus(message.From.Id);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: await BuildRenewSummaryAsync(refreshedUser, credUser.IsColleague, credUser.TelegramUserId, cancellationToken),
                parseMode: ParseMode.Html,
                replyMarkup: BuildConfirmReplyKeyboard(),
                cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == RenewStepUnlimitedPlan)
        {
            var plan = TryGetUnlimitedPlanFromText(service, message.Text);
            if (plan == null)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "پلن معتبر نیست. یکی از گزینه‌های لیست را انتخاب کنید.",
                    replyMarkup: BuildUnlimitedPlanReplyKeyboard(service, credUser.IsColleague),
                    cancellationToken: cancellationToken);
                return;
            }

            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = RenewFlowName,
                LastStep = RenewStepConfirm,
                Type = plan.Key
            });

            var refreshedUser = await _userDbContext.GetUserStatus(message.From.Id);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: await BuildRenewSummaryAsync(refreshedUser, credUser.IsColleague, credUser.TelegramUserId, cancellationToken),
                parseMode: ParseMode.Html,
                replyMarkup: BuildConfirmReplyKeyboard(),
                cancellationToken: cancellationToken);
            return;
        }

        if (user.LastStep == RenewStepConfirm)
        {
            if (!message.Text.Trim().Equals("تایید تمدید", StringComparison.OrdinalIgnoreCase))
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "برای تمدید، گزینه تایید تمدید را بزنید.",
                    replyMarkup: BuildConfirmReplyKeyboard(),
                    cancellationToken: cancellationToken);
                return;
            }

            await CompleteRenewAsync(botClient, message, credUser, user, mainReplyMarkup, cancellationToken);
        }
    }

    private async Task CompleteRenewAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        var service = FindService(user.SelectedCountry);
        var selection = service.IsUnlimited
            ? new XuiV3PurchaseSelection { ServiceKey = service.Key, UnlimitedPlanKey = user.Type }
            : new XuiV3PurchaseSelection
            {
                ServiceKey = service.Key,
                TrafficGb = int.TryParse(user.TotoalGB, out var trafficGb) ? trafficGb : 0,
                DurationKey = user.SelectedPeriod
            };

        var resolved = _purchaseService.ResolvePurchase(selection, credUser.IsColleague);
        if (credUser.AccountBalance < resolved.PriceToman)
        {
            await _activityLog.LogWarningAsync(
                "xui_v3_renew_insufficient_balance",
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["accountEmail"] = user.ConfigLink,
                    ["serviceKey"] = service.Key,
                    ["priceToman"] = resolved.PriceToman,
                    ["balanceToman"] = credUser.AccountBalance
                },
                cancellationToken);

            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"موجودی شما کافی نیست.\nقیمت: {resolved.PriceToman.FormatCurrency()}",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال تمدید اکانت نسخه ۳...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var serverInfo = BuildConfiguredPanelServerInfo();
        Console.WriteLine($"[XUIv3] renew confirm user={credUser.TelegramUserId}, email={user.ConfigLink}, panel={serverInfo.Url}, rootPath={serverInfo.RootPath}, service={service.Key}");

        var allowExternalUuidRenew = string.Equals(user.PaymentMethod, ExternalUuidRenewPaymentMethod, StringComparison.OrdinalIgnoreCase);
        var client = await FindClientByEmailAsync(serverInfo, user.ConfigLink, credUser.TelegramUserId, cancellationToken);
        if (client == null || (!allowExternalUuidRenew && !ClientBelongsToUser(client, credUser.TelegramUserId)))
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: allowExternalUuidRenew
                    ? "اکانت برای تمدید پیدا نشد."
                    : "اکانت برای تمدید پیدا نشد یا متعلق به حساب شما نیست.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        var currentExpiryBeforeRenew = GetExpiryTime(client);
        var payload = BuildRenewPayload(client, resolved, allowExternalUuidRenew ? "uuid-search-renew" : "user-renew", credUser.TelegramUserId);
        Console.WriteLine(
            $"[XUIv3] renew payload user={credUser.TelegramUserId}, email={client.Email}, durationDays={resolved.DurationDays}, currentExpiry={currentExpiryBeforeRenew}, newExpiry={payload.ExpiryTime}, currentExpiryText={FormatExpiry(currentExpiryBeforeRenew)}, newExpiryText={FormatExpiry(payload.ExpiryTime)}");

        var updateResponse = await ApiServicev3.UpdateClientAsync(serverInfo, _configuration, client.Email, payload, cancellationToken);
        if (!updateResponse.Success)
        {
            await _activityLog.LogWarningAsync(
                "xui_v3_account_renew_failed",
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["accountEmail"] = client.Email,
                    ["serviceKey"] = service.Key,
                    ["message"] = updateResponse.Msg ?? string.Empty
                },
                cancellationToken);

            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"تمدید ناموفق بود.\n{updateResponse.Msg}",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        var beforeBalance = credUser.AccountBalance;
        await _credentialsDbContext.Pay(credUser, resolved.PriceToman);
        var afterBalance = await _credentialsDbContext.GetAccountBalance(credUser.TelegramUserId);
        await _userDbContext.ClearUserStatus(user);

        client.TotalGB = payload.TotalGB;
        client.ExpiryTime = payload.ExpiryTime;
        client.Comment = payload.Comment;
        client.Enable = payload.Enable;

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "✅ تمدید با موفقیت انجام شد.\n\n" + BuildV3ClientInfo(client, serverInfo, credUser.IsColleague),
            parseMode: ParseMode.Html,
            replyMarkup: mainReplyMarkup,
            cancellationToken: cancellationToken);
        LogV3Purchase(
            title: "تمدید اکانت نسخه ۳",
            credUser: credUser,
            priceToman: resolved.PriceToman,
            beforeBalance: beforeBalance,
            afterBalance: afterBalance,
            details: new[]
            {
                $"نام اکانت `{client.Email}`",
                $"حجم اضافه `{resolved.TrafficGb} GB`",
                $"زمان اضافه `{(resolved.DurationDays <= 0 ? "نامحدود" : $"{resolved.DurationDays} روز")}`",
                $"سابلینک `{ApiServicev3.BuildSubscriptionLink(serverInfo, client.SubId ?? client.Email)}`",
                $"کامنت `{client.Comment}`"
            });
        await _activityLog.LogBotActionAsync(
            "xui_v3_account_renewed",
            credUser,
            false,
            new Dictionary<string, object>
            {
                ["accountEmail"] = client.Email,
                ["serviceKey"] = service.Key,
                ["serviceName"] = service.DisplayName,
                ["trafficAddedGb"] = resolved.TrafficGb,
                ["durationAddedDays"] = resolved.DurationDays,
                ["priceToman"] = resolved.PriceToman,
                ["balanceBeforeToman"] = beforeBalance,
                ["balanceAfterToman"] = afterBalance,
                ["totalGbAfterRenew"] = client.TotalGB.ConvertBytesToGB(),
                ["usedGb"] = GetUsedBytes(client).ConvertBytesToGB(),
                ["expiryShamsi"] = FormatExpiry(client.ExpiryTime),
                ["subLink"] = ApiServicev3.BuildSubscriptionLink(serverInfo, client.SubId ?? client.Email),
                ["panelUrl"] = serverInfo.Url,
                ["rootPath"] = serverInfo.RootPath
            },
            cancellationToken);
    }

    public async Task<bool> TryStartPurchaseAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow())
            return false;

        if (!await EnsurePhoneVerifiedAsync(botClient, message.Chat.Id, credUser, cancellationToken))
        {
            await _userDbContext.ClearUserStatus(user);
            return true;
        }

        await _userDbContext.ClearUserStatus(user);
        var selection = new XuiV3PurchaseSelection();
        _sessionStore.Set(credUser.TelegramUserId, selection);

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "نوع سرویس را انتخاب کنید:",
            replyMarkup: _purchaseService.BuildServiceKeyboard(),
            cancellationToken: cancellationToken);

        user.Flow = PurchaseFlowName;
        user.LastStep = PurchaseStepSelectService;
        user.SelectedCountry = null;
        user.SelectedPeriod = null;
        user.Type = null;
        user.TotoalGB = null;
        user.ConfigLink = null;
        user.SubLink = null;
        user._ConfigPrice = null;
        user.PendingAccountCount = 0;
        user.PendingUserComment = "";
        await _userDbContext.SaveUserStatus(user);
        return true;
    }

    public async Task<bool> TryHandlePurchaseTextAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || message?.Text == null || user?.Flow != PurchaseFlowName)
            return false;

        if (IsCancel(message.Text))
        {
            _sessionStore.Clear(credUser.TelegramUserId);
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "فرایند خرید لغو شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (!await EnsurePhoneVerifiedAsync(botClient, message.Chat.Id, credUser, cancellationToken))
            return true;

        var selection = _sessionStore.GetOrCreate(credUser.TelegramUserId);
        var service = FindService(selection.ServiceKey ?? user.SelectedCountry);
        if (service == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "ابتدا نوع سرویس را از دکمه‌ها انتخاب کنید.",
                replyMarkup: _purchaseService.BuildServiceKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (user.LastStep == PurchaseStepAccountCount)
        {
            if (!TryGetIntFromText(message.Text, out var accountCount) ||
                accountCount <= 0 ||
                accountCount > XuiV3PurchaseService.MaxBulkAccountCount)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"تعداد اکانت معتبر نیست. یک عدد بین 1 تا {XuiV3PurchaseService.MaxBulkAccountCount} بفرستید.",
                    replyMarkup: BuildAccountCountReplyKeyboard(),
                    cancellationToken: cancellationToken);
                return true;
            }

            selection.AccountCount = accountCount;
            _sessionStore.Set(credUser.TelegramUserId, selection);

            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = PurchaseFlowName,
                LastStep = PurchaseStepUserComment,
                PendingAccountCount = accountCount
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "اگر برای این سفارش کامنتی دارید، متن آن را بفرستید. این کامنت روی اکانت ذخیره می‌شود و بعداً در وضعیت اکانت نمایش داده می‌شود.\n\nاگر کامنتی ندارید، گزینه «ادامه بدون کامنت» را بزنید.",
                replyMarkup: BuildOptionalCommentReplyKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (user.LastStep == PurchaseStepUserComment)
        {
            var userComment = IsSkipCommentText(message.Text)
                ? string.Empty
                : NormalizeUserComment(message.Text);

            selection.AccountCount = XuiV3PurchaseService.NormalizeAccountCount(user.PendingAccountCount);
            selection.UserComment = userComment;
            _sessionStore.Set(credUser.TelegramUserId, selection);

            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = PurchaseFlowName,
                LastStep = PurchaseStepConfirm,
                PendingUserComment = userComment
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: _purchaseService.BuildSummaryText(selection, credUser.IsColleague),
                parseMode: ParseMode.Html,
                replyMarkup: _purchaseService.BuildConfirmKeyboard(selection),
                cancellationToken: cancellationToken);
            return true;
        }

        if (service.IsUnlimited)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای سرویس نامحدود، یکی از پلن‌های نمایش داده شده را انتخاب کنید.",
                replyMarkup: _purchaseService.BuildUnlimitedPlanKeyboard(service.Key, credUser.IsColleague),
                cancellationToken: cancellationToken);
            return true;
        }

        if (!selection.TrafficGb.HasValue)
        {
            if (!TryGetTrafficGbFromText(message.Text, out var trafficGb) || trafficGb <= 0)
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "حجم معتبر نیست. فقط عدد صحیح وارد کنید؛ مثلا 7 یا ۷. سپس دوباره تلاش کنید.",
                    replyMarkup: _purchaseService.BuildTrafficKeyboard(service.Key),
                    cancellationToken: cancellationToken);
                return true;
            }

            selection.ServiceKey = service.Key;
            selection.TrafficGb = trafficGb;
            selection.DurationKey = null;
            selection.UnlimitedPlanKey = null;
            _sessionStore.Set(credUser.TelegramUserId, selection);

            user.Flow = PurchaseFlowName;
            user.LastStep = PurchaseStepSelectDuration;
            user.SelectedCountry = service.Key;
            user.TotoalGB = trafficGb.ToString();
            await _userDbContext.SaveUserStatus(user);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"حجم انتخاب شد: {trafficGb} GB\nحالا مدت را انتخاب کنید:",
                replyMarkup: _purchaseService.BuildDurationKeyboard(service.Key, trafficGb),
                cancellationToken: cancellationToken);
            return true;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "برای ادامه از دکمه‌های پیام سفارش استفاده کنید.",
            cancellationToken: cancellationToken);
        return true;
    }

    public async Task<bool> TryHandleFreeTrialAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!IsEnabledForPurchaseFlow() || string.IsNullOrWhiteSpace(message?.Text))
            return false;

        var text = message.Text.Trim();
        var isTrialStart = text.Contains("اکانت رایگان", StringComparison.OrdinalIgnoreCase) ||
                           text.Equals("🌟اکانت رایگان", StringComparison.OrdinalIgnoreCase);

        if (user?.Flow != TrialFlowName && !isTrialStart)
            return false;

        if (IsCancel(text))
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "فرایند دریافت اکانت تست لغو شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (credUser.IsColleague)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "اکانت تست رایگان نسخه ۳ فقط برای کاربران عادی فعال است.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (!await EnsurePhoneVerifiedAsync(botClient, message.Chat.Id, credUser, cancellationToken))
            return true;

        if (user?.Flow != TrialFlowName)
        {
            await _userDbContext.ClearUserStatus(user);
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = TrialFlowName,
                LastStep = TrialStepSelectService
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "نوع اکانت تست را انتخاب کنید:",
                replyMarkup: BuildTrialServiceReplyKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (user.LastStep != TrialStepSelectService)
            return false;

        var serviceKey = TryGetTrialServiceKey(text);
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "نوع تست معتبر نیست. یکی از گزینه‌های نمایش داده شده را انتخاب کنید.",
                replyMarkup: BuildTrialServiceReplyKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        var lastTrial = serviceKey == "national" ? user.LastFreeNationalAcc : user.LastFreeNormalAcc;
        var now = DateTime.UtcNow;
        if (lastTrial > DateTime.MinValue && (now - lastTrial).TotalDays < 30)
        {
            var remainingDays = Math.Max(1, 30 - (int)Math.Floor((now - lastTrial).TotalDays));
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"شما تست این سرویس را در ۳۰ روز گذشته دریافت کرده‌اید. لطفاً {remainingDays} روز دیگر دوباره تلاش کنید.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            await _userDbContext.ClearUserStatus(user);
            return true;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "در حال ساخت اکانت تست نسخه ۳، لطفاً چند لحظه صبر کنید...",
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);

        var serverInfo = BuildConfiguredPanelServerInfo();
        var trafficBytes = serviceKey == "national" ? NationalTrialBytes : NormalTrialBytes;
        var displayTrafficGb = 1;
        var trialKey = serviceKey == "national" ? "national-monthly-free" : "normal-monthly-free";
        var creation = await _purchaseService.CreateTrialAccountAsync(
            credUser,
            serverInfo,
            serviceKey,
            displayTrafficGb,
            trafficBytes,
            TrialDays,
            trialKey,
            cancellationToken);

        if (!creation.Success)
        {
            await _activityLog.LogWarningAsync(
                "xui_v3_trial_create_failed",
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["serviceKey"] = serviceKey,
                    ["message"] = creation.Message ?? string.Empty,
                    ["panelUrl"] = serverInfo.Url,
                    ["rootPath"] = serverInfo.RootPath
                },
                cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"ساخت اکانت تست ناموفق بود.\n{creation.Message}",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            await _userDbContext.ClearUserStatus(user);
            return true;
        }

        if (serviceKey == "national")
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastFreeNationalAcc = now });
        else
            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastFreeNormalAcc = now });

        var accountText = _purchaseService.BuildCreatedAccountText(creation);
        if (!string.IsNullOrWhiteSpace(creation.SubLink))
        {
            using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(creation.SubLink, 200));
            await botClient.SendPhotoAsync(
                chatId: message.Chat.Id,
                photo: InputFile.FromStream(qrStream, "trial-subscription-qr.png"),
                caption: "✅ اکانت تست شما ساخته شد.\n\n" + accountText,
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
        }
        else
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "✅ اکانت تست شما ساخته شد.\n\n" + accountText,
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
        }

        await _userDbContext.ClearUserStatus(user);
        return true;
    }

    public async Task<bool> TryHandleColleagueRequestAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message?.Text))
            return false;

        var text = message.Text.Trim();
        var isStart = string.Equals(text, "🤝 درخواست همکاری", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(text, "درخواست همکاری", StringComparison.OrdinalIgnoreCase);

        if (user?.Flow != ColleagueRequestFlowName && !isStart)
            return false;

        if (IsCancel(text))
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "درخواست همکاری لغو شد.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (credUser?.IsColleague == true)
        {
            await _userDbContext.ClearUserStatus(user);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "حساب شما هم‌اکنون از نوع همکار است و نیازی به ثبت درخواست جدید نیست.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (user?.Flow != ColleagueRequestFlowName)
        {
            await _userDbContext.ClearUserStatus(user);
            await _userDbContext.SaveUserStatus(new User
            {
                Id = message.From.Id,
                Flow = ColleagueRequestFlowName,
                LastStep = ColleagueRequestStepConfirm
            });

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای ثبت درخواست همکاری باید تایید کنید که فروش هفتگی شما حداقل ۵,۰۰۰,۰۰۰ تومان است.\n\nاگر فروش هفتگی شما کمتر از این مقدار باشد، حساب شما کاربر عادی محسوب می‌شود و امکان خرید با قیمت همکار برای شما فعال نخواهد شد.",
                replyMarkup: BuildColleagueRequestConfirmKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (user.LastStep != ColleagueRequestStepConfirm)
            return false;

        if (!string.Equals(text, "شرایط را قبول دارم و درخواست همکاری می‌فرستم", StringComparison.OrdinalIgnoreCase))
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "برای ثبت درخواست همکاری، دکمه تایید شرایط را بزنید یا انصراف دهید.",
                replyMarkup: BuildColleagueRequestConfirmKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        _logger.LogPayment(BuildColleagueRequestLogMessage(credUser));

        await _activityLog.LogBotActionAsync(
            "colleague_request_submitted",
            credUser,
            false,
            new Dictionary<string, object>
            {
                ["minimumWeeklySalesToman"] = MinimumWeeklyColleagueSalesToman,
                ["chatId"] = credUser?.ChatID ?? message.Chat.Id,
                ["phoneNumber"] = credUser?.PhoneNumber ?? string.Empty,
                ["accountBalanceToman"] = credUser?.AccountBalance ?? 0
            },
            cancellationToken);

        await _userDbContext.ClearUserStatus(user);
        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "درخواست همکاری شما ثبت شد و برای بررسی به سوپرادمین‌ها ارسال شد.\nبعد از بررسی، نتیجه از طریق پشتیبانی یا همین ربات به شما اطلاع داده می‌شود.",
            replyMarkup: mainReplyMarkup,
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task<bool> TryHandleCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (!XuiV3PurchaseCallbacks.TryParse(callbackQuery.Data, out var callback))
            return false;

        var chatId = callbackQuery.Message?.Chat.Id ?? credUser.ChatID;
        var messageId = callbackQuery.Message?.MessageId ?? 0;
        await AnswerCallbackSafelyAsync(botClient, callbackQuery.Id, cancellationToken);

        if (callback.Action == "home")
        {
            await ReturnToMainMenuAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                user,
                mainReplyMarkup,
                cancellationToken);
            return true;
        }

        if (callback.Action == "asrch")
        {
            await StartAccountSearchAsync(
                botClient,
                chatId,
                credUser.TelegramUserId,
                cancellationToken,
                messageId);
            return true;
        }

        if (callback.Action == "asl")
        {
            var query = user?.ConfigLink;
            if (string.IsNullOrWhiteSpace(query))
            {
                await StartAccountSearchAsync(botClient, chatId, credUser.TelegramUserId, cancellationToken, messageId);
                return true;
            }

            await SendAccountSearchResultsAsync(
                botClient,
                chatId,
                credUser,
                query,
                callback.Page ?? 0,
                cancellationToken,
                messageId);
            return true;
        }

        if (callback.Action == "asv")
        {
            await HandleAccountSearchViewCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "asren")
        {
            if (!await EnsurePhoneVerifiedAsync(botClient, chatId, credUser, cancellationToken))
                return true;

            await HandleAccountSearchRenewCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                false,
                cancellationToken);
            return true;
        }

        if (callback.Action == "auren")
        {
            if (!await EnsurePhoneVerifiedAsync(botClient, chatId, credUser, cancellationToken))
                return true;

            await HandleAccountSearchRenewCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                true,
                cancellationToken);
            return true;
        }

        if (callback.Action == "asdelask")
        {
            await HandleAccountSearchDeleteAskCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "asdel")
        {
            await HandleAccountSearchDeleteConfirmCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "asacct")
        {
            await HandleAccountSearchStateCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                string.Equals(callback.AccountOperation, "en", StringComparison.OrdinalIgnoreCase),
                cancellationToken);
            return true;
        }

        if (callback.Action == "asch")
        {
            await HandleAccountChangeLinkCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                true,
                cancellationToken);
            return true;
        }

        if (callback.Action == "acct")
        {
            await HandleAccountStateCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                string.Equals(callback.AccountOperation, "en", StringComparison.OrdinalIgnoreCase),
                cancellationToken);
            return true;
        }

        if (callback.Action == "alist")
        {
            await SendV3AccountListPageAsync(
                botClient,
                chatId,
                callback.Page ?? 0,
                credUser,
                cancellationToken,
                messageId);
            return true;
        }

        if (callback.Action == "aview")
        {
            await HandleAccountViewCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "ach")
        {
            await HandleAccountChangeLinkCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                false,
                cancellationToken);
            return true;
        }

        if (callback.Action == "aren")
        {
            if (!await EnsurePhoneVerifiedAsync(botClient, chatId, credUser, cancellationToken))
                return true;

            await HandleAccountRenewCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                user,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "adelask")
        {
            await HandleAccountDeleteAskCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "adel")
        {
            await HandleAccountDeleteConfirmCallbackAsync(
                botClient,
                chatId,
                messageId,
                credUser,
                callback.ClientId,
                callback.Page ?? 0,
                cancellationToken);
            return true;
        }

        if (callback.Action == "cancel")
        {
            _sessionStore.Clear(credUser.TelegramUserId);
            user.Flow = null;
            user.LastStep = null;
            user.SelectedCountry = null;
            user.SelectedPeriod = null;
            user.Type = null;
            user.TotoalGB = null;
            user._ConfigPrice = null;
            await _userDbContext.ClearUserStatus(user);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: "فرایند خرید لغو شد.",
                    cancellationToken: cancellationToken);
            }

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "به منوی اصلی برگشتید.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        if (!await EnsurePhoneVerifiedAsync(botClient, chatId, credUser, cancellationToken))
            return true;

        if (callback.Action == "back")
        {
            var selection = _sessionStore.GetOrCreate(credUser.TelegramUserId);
            selection.ServiceKey = null;
            selection.TrafficGb = null;
            selection.DurationKey = null;
            selection.UnlimitedPlanKey = null;
            _sessionStore.Set(credUser.TelegramUserId, selection);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: "نوع سرویس را انتخاب کنید:",
                    replyMarkup: _purchaseService.BuildServiceKeyboard(),
                    cancellationToken: cancellationToken);
            }
            return true;
        }

        var selectionState = _sessionStore.GetOrCreate(credUser.TelegramUserId);

        if (callback.Action == "cnt")
        {
            var count = XuiV3PurchaseService.NormalizeAccountCount(callback.AccountCount ?? 1);
            selectionState.AccountCount = count;
            _sessionStore.Set(credUser.TelegramUserId, selectionState);

            await _userDbContext.SaveUserStatus(new User
            {
                Id = credUser.TelegramUserId,
                Flow = PurchaseFlowName,
                LastStep = PurchaseStepUserComment,
                PendingAccountCount = count
            });

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: $"تعداد انتخاب شد: {count}",
                    cancellationToken: cancellationToken);
            }

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اگر برای این سفارش کامنتی دارید بفرستید. این کامنت روی اکانت ذخیره می‌شود و بعداً در وضعیت اکانت نمایش داده می‌شود.",
                replyMarkup: BuildOptionalCommentReplyKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (callback.Action == "svc")
        {
            selectionState.ServiceKey = callback.ServiceKey;
            selectionState.TrafficGb = null;
            selectionState.DurationKey = null;
            selectionState.UnlimitedPlanKey = null;
            selectionState.AccountCount = 1;
            selectionState.UserComment = null;
            _sessionStore.Set(credUser.TelegramUserId, selectionState);

            var service = _purchaseService.LoadCatalog().Services.FirstOrDefault(s => string.Equals(s.Key, callback.ServiceKey, StringComparison.OrdinalIgnoreCase));
            if (service == null)
                return false;

            user.Flow = PurchaseFlowName;
            user.LastStep = service.IsUnlimited ? PurchaseStepSelectUnlimitedPlan : PurchaseStepSelectTraffic;
            user.SelectedCountry = service.Key;
            user.SelectedPeriod = null;
            user.Type = null;
            user.TotoalGB = null;
            await _userDbContext.SaveUserStatus(user);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: service.IsUnlimited
                        ? $"سرویس انتخاب شد: {service.DisplayName}\nحالا یکی از پلن‌های نامحدود را انتخاب کنید:"
                        : $"سرویس انتخاب شد: {service.DisplayName}\nحالا یکی از دکمه‌های حجم را بزنید یا حجم دلخواه را به صورت عدد صحیح بفرستید؛ مثلا 7 یا ۷.",
                    replyMarkup: service.IsUnlimited
                        ? _purchaseService.BuildUnlimitedPlanKeyboard(service.Key, credUser.IsColleague)
                        : _purchaseService.BuildTrafficKeyboard(service.Key),
                    cancellationToken: cancellationToken);
            }

            return true;
        }

        if (callback.Action == "gb")
        {
            selectionState.ServiceKey = callback.ServiceKey;
            selectionState.TrafficGb = callback.TrafficGb;
            selectionState.DurationKey = null;
            selectionState.UnlimitedPlanKey = null;
            selectionState.AccountCount = 1;
            selectionState.UserComment = null;
            _sessionStore.Set(credUser.TelegramUserId, selectionState);

            user.Flow = PurchaseFlowName;
            user.LastStep = PurchaseStepSelectDuration;
            user.SelectedCountry = callback.ServiceKey;
            user.TotoalGB = callback.TrafficGb?.ToString();
            await _userDbContext.SaveUserStatus(user);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: $"حجم انتخاب شد: {callback.TrafficGb} GB\nحالا مدت را انتخاب کنید:",
                    replyMarkup: _purchaseService.BuildDurationKeyboard(callback.ServiceKey, callback.TrafficGb ?? 0),
                    cancellationToken: cancellationToken);
            }
            return true;
        }

        if (callback.Action == "dur")
        {
            selectionState.ServiceKey = callback.ServiceKey;
            selectionState.TrafficGb = callback.TrafficGb;
            selectionState.DurationKey = callback.DurationKey;
            selectionState.UnlimitedPlanKey = null;
            selectionState.AccountCount = 1;
            selectionState.UserComment = null;
            _sessionStore.Set(credUser.TelegramUserId, selectionState);

            user.Flow = PurchaseFlowName;
            user.LastStep = PurchaseStepAccountCount;
            user.SelectedCountry = callback.ServiceKey;
            user.TotoalGB = callback.TrafficGb?.ToString();
            user.SelectedPeriod = callback.DurationKey;
            await _userDbContext.SaveUserStatus(user);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: $"تعداد اکانت مورد نظر را وارد کنید. حداکثر تعداد در هر سفارش {XuiV3PurchaseService.MaxBulkAccountCount} است.",
                    replyMarkup: BuildAccountCountInlineKeyboard(),
                    cancellationToken: cancellationToken);
            }
            return true;
        }

        if (callback.Action == "upl")
        {
            selectionState.ServiceKey = callback.ServiceKey;
            selectionState.UnlimitedPlanKey = callback.UnlimitedPlanKey;
            selectionState.TrafficGb = null;
            selectionState.DurationKey = null;
            selectionState.AccountCount = 1;
            selectionState.UserComment = null;
            _sessionStore.Set(credUser.TelegramUserId, selectionState);

            user.Flow = PurchaseFlowName;
            user.LastStep = PurchaseStepAccountCount;
            user.SelectedCountry = callback.ServiceKey;
            user.Type = callback.UnlimitedPlanKey;
            await _userDbContext.SaveUserStatus(user);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: $"تعداد اکانت مورد نظر را وارد کنید. حداکثر تعداد در هر سفارش {XuiV3PurchaseService.MaxBulkAccountCount} است.",
                    replyMarkup: BuildAccountCountInlineKeyboard(),
                    cancellationToken: cancellationToken);
            }
            return true;
        }

        if (callback.Action == "ok")
        {
            var hasSession = _sessionStore.TryGet(credUser.TelegramUserId, out var selection);
            if (!hasSession || selection == null || string.IsNullOrWhiteSpace(selection.ServiceKey))
            {
                selection = callback.ToSelection();
                Console.WriteLine($"[XUIv3] confirm fallback from callback data for user {credUser.TelegramUserId}");
            }

            if (selection == null || string.IsNullOrWhiteSpace(selection.ServiceKey))
            {
                Console.WriteLine($"[XUIv3] confirm failed: selection is still empty for user {credUser.TelegramUserId}");
                return true;
            }

            try
            {
                if (selection.AccountCount <= 0)
                    selection.AccountCount = XuiV3PurchaseService.NormalizeAccountCount(user.PendingAccountCount);
                if (selection.UserComment == null)
                    selection.UserComment = user.PendingUserComment;

                var resolved = _purchaseService.ResolvePurchase(selection, credUser.IsColleague);
                var accountCount = XuiV3PurchaseService.NormalizeAccountCount(selection.AccountCount);
                var totalPrice = resolved.PriceToman * accountCount;
                Console.WriteLine($"[XUIv3] confirm start user={credUser.TelegramUserId} service={resolved.Service.Key} trafficGb={resolved.TrafficGb} durationDays={resolved.DurationDays} count={accountCount} unitPrice={resolved.PriceToman} totalPrice={totalPrice}");

                if (credUser.AccountBalance < totalPrice)
                {
                    Console.WriteLine($"[XUIv3] insufficient balance user={credUser.TelegramUserId} balance={credUser.AccountBalance} price={totalPrice}");
                    await _activityLog.LogWarningAsync(
                        "xui_v3_create_insufficient_balance",
                        credUser,
                        false,
                        new Dictionary<string, object>
                        {
                            ["serviceKey"] = resolved.Service.Key,
                            ["trafficGb"] = resolved.TrafficGb,
                            ["durationDays"] = resolved.DurationDays,
                            ["accountCount"] = accountCount,
                            ["priceToman"] = totalPrice,
                            ["balanceToman"] = credUser.AccountBalance
                        },
                        cancellationToken);

                    if (messageId != 0)
                    {
                        await SafeEditMessageTextAsync(
                            botClient,
                            chatId: chatId,
                            messageId: messageId,
                            text: $"موجودی شما کافی نیست.\nقیمت کل: {totalPrice.FormatCurrency()}",
                            replyMarkup: new InlineKeyboardMarkup(new[]
                            {
                                new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices()) },
                                new[] { InlineKeyboardButton.WithCallbackData("انصراف", XuiV3PurchaseCallbacks.Cancel()) }
                            }),
                            cancellationToken: cancellationToken);
                    }
                    return true;
                }

                if (messageId != 0)
                {
                    await SafeEditMessageTextAsync(
                        botClient,
                        chatId: chatId,
                        messageId: messageId,
                        text: "در حال ساخت اکانت، لطفاً چند لحظه صبر کنید...",
                        cancellationToken: cancellationToken);
                }

                var serverInfo = BuildConfiguredPanelServerInfo();
                Console.WriteLine($"[XUIv3] using configured panel url={serverInfo.Url}, rootPath={serverInfo.RootPath}, token={(string.IsNullOrWhiteSpace(serverInfo.ApiToken) ? "missing" : "set")}");

                var bulkResult = await _purchaseService.CreateBulkAccountsAsync(
                    credUser,
                    serverInfo,
                    selection,
                    serverInfo.Url,
                    new XuiV3BulkCreateOptions
                    {
                        AccountCount = accountCount,
                        UserComment = selection.UserComment,
                        CreatedByTelegramUserId = credUser.TelegramUserId,
                        LastUpdatedByTelegramUserId = credUser.TelegramUserId,
                        LastAction = "customer-create",
                        NextAccountCounter = user.AccountCounter + 1,
                        SaveUserStatus = true
                    },
                    cancellationToken);

                Console.WriteLine($"[XUIv3] bulk create result user={credUser.TelegramUserId} success={bulkResult.Success} requested={bulkResult.RequestedCount} created={bulkResult.SuccessfulCount} failures={bulkResult.Failures.Count} serverUrl={serverInfo.Url} rootPath={serverInfo.RootPath}");

                if (bulkResult.SuccessfulCount == 0)
                {
                    var failureMessage = bulkResult.Failures.FirstOrDefault()?.Message ?? "ساخت اکانت ناموفق بود.";
                    await _activityLog.LogWarningAsync(
                        "xui_v3_bulk_account_create_failed",
                        credUser,
                        false,
                        new Dictionary<string, object>
                        {
                            ["serviceKey"] = resolved.Service.Key,
                            ["trafficGb"] = resolved.TrafficGb,
                            ["durationDays"] = resolved.DurationDays,
                            ["accountCount"] = accountCount,
                            ["priceToman"] = totalPrice,
                            ["message"] = failureMessage,
                            ["panelUrl"] = serverInfo.Url,
                            ["rootPath"] = serverInfo.RootPath
                        },
                        cancellationToken);

                    if (messageId != 0)
                    {
                        await SafeEditMessageTextAsync(
                            botClient,
                            chatId: chatId,
                            messageId: messageId,
                            text: $"ساخت اکانت ناموفق بود.\n{failureMessage}",
                            cancellationToken: cancellationToken);
                    }
                    return true;
                }

                if (messageId != 0)
                {
                    await SafeEditMessageTextAsync(
                        botClient,
                        chatId: chatId,
                        messageId: messageId,
                        text: bulkResult.Success
                            ? $"✅ {bulkResult.SuccessfulCount} اکانت ساخته شد. مشخصات و QR Code در پیام‌های بعدی ارسال می‌شود."
                            : $"⚠️ {bulkResult.SuccessfulCount} اکانت ساخته شد، اما ادامه ساخت متوقف شد. مشخصات اکانت‌های موفق ارسال می‌شود.",
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);
                }

                foreach (var createdAccount in bulkResult.CreatedAccounts)
                {
                    var createdAccountText = _purchaseService.BuildCreatedAccountText(createdAccount);
                    if (!string.IsNullOrWhiteSpace(createdAccount.SubLink))
                    {
                        using var qrStream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(createdAccount.SubLink, 200));
                        await botClient.SendPhotoAsync(
                            chatId: chatId,
                            photo: InputFile.FromStream(qrStream, "subscription-qr.png"),
                            caption: createdAccountText,
                            parseMode: ParseMode.Html,
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: createdAccountText,
                            parseMode: ParseMode.Html,
                            cancellationToken: cancellationToken);
                    }

                    await Task.Delay(250, cancellationToken);
                }

                if (bulkResult.Failures.Count > 0)
                {
                    await botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: BuildBulkFailureText(bulkResult),
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);
                }

                var bulkBeforeBalance = credUser.AccountBalance;
                if (bulkResult.TotalSuccessfulPriceToman > 0)
                    await _credentialsDbContext.Pay(credUser, bulkResult.TotalSuccessfulPriceToman);
                var bulkAfterBalance = await _credentialsDbContext.GetAccountBalance(credUser.TelegramUserId);

                LogV3Purchase(
                    title: accountCount > 1 ? "ساخت انبوه اکانت نسخه ۳" : "ساخت اکانت نسخه ۳",
                    credUser: credUser,
                    priceToman: bulkResult.TotalSuccessfulPriceToman,
                    beforeBalance: bulkBeforeBalance,
                    afterBalance: bulkAfterBalance,
                    details: BuildBulkPurchaseLogDetails(bulkResult, selection.UserComment));

                await _activityLog.LogBotActionAsync(
                    accountCount > 1 ? "xui_v3_bulk_accounts_created" : "xui_v3_account_created",
                    credUser,
                    false,
                    new Dictionary<string, object>
                    {
                        ["bulkOrderId"] = bulkResult.BulkOrderId,
                        ["requestedCount"] = bulkResult.RequestedCount,
                        ["successfulCount"] = bulkResult.SuccessfulCount,
                        ["accountEmails"] = bulkResult.CreatedAccounts.Select(item => item.Email).ToList(),
                        ["serviceKey"] = resolved.Service.Key,
                        ["serviceName"] = resolved.Service.DisplayName,
                        ["trafficGb"] = resolved.TrafficGb,
                        ["trafficBytes"] = bulkResult.TrafficBytes,
                        ["durationDays"] = resolved.DurationDays,
                        ["unitPriceToman"] = bulkResult.UnitPriceToman,
                        ["priceToman"] = bulkResult.TotalSuccessfulPriceToman,
                        ["balanceBeforeToman"] = bulkBeforeBalance,
                        ["balanceAfterToman"] = bulkAfterBalance,
                        ["panelUrl"] = serverInfo.Url,
                        ["rootPath"] = serverInfo.RootPath,
                        ["userComment"] = selection.UserComment ?? string.Empty
                    },
                    cancellationToken);

                _sessionStore.Clear(credUser.TelegramUserId);
                await _userDbContext.ClearUserStatus(user);
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "منوی اصلی",
                    replyMarkup: mainReplyMarkup,
                    cancellationToken: cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XUIv3] confirm exception user={credUser.TelegramUserId}: {ex}");
                await _activityLog.LogErrorAsync(
                    "xui_v3_confirm_failed",
                    ex,
                    credUser,
                    false,
                    new Dictionary<string, object>
                    {
                        ["callbackData"] = callbackQuery.Data ?? string.Empty
                    },
                    cancellationToken);
                if (messageId != 0)
                {
                    await SafeEditMessageTextAsync(
                        botClient,
                        chatId: chatId,
                        messageId: messageId,
                        text: "در ساخت اکانت خطا رخ داد. جزئیات در ترمینال ثبت شد.",
                        cancellationToken: cancellationToken);
                }
            }
            return true;
        }

        return false;
    }

    private async Task StartAccountSearchAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        long telegramUserId,
        CancellationToken cancellationToken,
        int messageId = 0)
    {
        await _userDbContext.ClearUserStatus(new User { Id = telegramUserId });
        await _userDbContext.SaveUserStatus(new User
        {
            Id = telegramUserId,
            Flow = AccountSearchFlowName,
            LastStep = AccountSearchStepQuery,
            ConfigLink = null
        });

        const string text = "عبارت جستجو را بفرستید.\n\n" +
                            "اگر UUID کامل کانفیگ را بفرستید، کل پنل بررسی می‌شود و حتی اگر اکانت متعلق به شما نباشد فقط امکان تمدید آن فعال می‌شود.\n\n" +
                            "اگر یک متن معمولی بفرستید، فقط بین اکانت‌های خودتان و روی نام اکانت و کامنت کاربر جستجو می‌کنم.";

        if (messageId != 0)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: chatId,
                messageId: messageId,
                text: text,
                replyMarkup: BuildSearchStartKeyboard(),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: text,
            replyMarkup: BuildSearchStartKeyboard(),
            cancellationToken: cancellationToken);
    }

    private async Task ReturnToMainMenuAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        User user,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (credUser != null)
            _sessionStore.Clear(credUser.TelegramUserId);

        var userId = user?.Id ?? credUser?.TelegramUserId ?? 0;
        if (userId > 0)
            await _userDbContext.ClearUserStatus(user ?? new User { Id = userId });

        if (messageId != 0)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: chatId,
                messageId: messageId,
                text: "به منوی اصلی برگشتید.",
                cancellationToken: cancellationToken);
        }

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "منوی اصلی",
            replyMarkup: mainReplyMarkup,
            cancellationToken: cancellationToken);
    }

    private async Task SendAccountSearchResultsAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CredUser credUser,
        string query,
        int page,
        CancellationToken cancellationToken,
        int messageId = 0)
    {
        var normalizedQuery = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                "عبارت جستجو معتبر نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!response.Success)
        {
            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                $"دریافت اکانت‌ها ناموفق بود.\n{response.Msg}",
                cancellationToken: cancellationToken);
            return;
        }

        var allClients = response.Obj ?? new List<XuiV3Client>();
        if (TryExtractUuidQuery(query, out var uuid))
        {
            var uuidClient = allClients.FirstOrDefault(client => ClientUuidEquals(client, uuid));
            if (uuidClient == null)
            {
                await SendOrEditTextAsync(
                    botClient,
                    chatId,
                    messageId,
                    $"هیچ اکانتی با این UUID پیدا نشد:\n<code>{Html(uuid)}</code>",
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildSearchEmptyKeyboard(),
                    cancellationToken: cancellationToken);
                return;
            }

            var isOwner = ClientBelongsToUser(uuidClient, credUser.TelegramUserId);
            var text = BuildUuidSearchResultText(uuidClient, serverInfo, isOwner);
            var keyboard = BuildUuidSearchResultKeyboard(uuidClient);

            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                text,
                ParseMode.Html,
                keyboard,
                cancellationToken);
            return;
        }

        var results = allClients
            .Where(client => ClientBelongsToUser(client, credUser.TelegramUserId))
            .Where(client => ClientMatchesSearchQuery(client, normalizedQuery))
            .OrderBy(client => IsExpiredOrDepleted(client) ? 0 : 1)
            .ThenBy(client => client.Email)
            .ToList();

        await _userDbContext.SaveUserStatus(new User
        {
            Id = credUser.TelegramUserId,
            Flow = AccountSearchFlowName,
            LastStep = AccountSearchStepResults,
            ConfigLink = query
        });

        if (results.Count == 0)
        {
            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                $"برای عبارت زیر اکانتی بین اکانت‌های شما پیدا نشد:\n<code>{Html(query)}</code>",
                ParseMode.Html,
                BuildSearchEmptyKeyboard(),
                cancellationToken);
            return;
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(results.Count / (double)AccountListPageSize));
        page = Math.Clamp(page, 0, totalPages - 1);
        var pageAccounts = results
            .Skip(page * AccountListPageSize)
            .Take(AccountListPageSize)
            .ToList();

        var resultText = BuildAccountSearchListText(query, results.Count, page, totalPages);
        var resultKeyboard = BuildAccountSearchListKeyboard(pageAccounts, page, totalPages);

        await SendOrEditTextAsync(
            botClient,
            chatId,
            messageId,
            resultText,
            ParseMode.Html,
            resultKeyboard,
            cancellationToken);
    }

    private async Task SendV3AccountListPageAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int page,
        CredUser credUser,
        CancellationToken cancellationToken,
        int messageId = 0)
    {
        var serverInfo = BuildConfiguredPanelServerInfo();
        var response = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!response.Success)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"دریافت اکانت‌ها ناموفق بود.\n{response.Msg}",
                cancellationToken: cancellationToken);
            return;
        }

        var accounts = response.Obj?
            .Where(client => ClientBelongsToUser(client, credUser.TelegramUserId))
            .OrderBy(client => IsExpiredOrDepleted(client) ? 0 : 1)
            .ThenBy(client => client.Email)
            .ToList() ?? new List<XuiV3Client>();

        if (accounts.Count == 0)
        {
            const string emptyText = "شما هنوز هیچ اکانتی از مجموعه ما ندارید.";
            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: emptyText,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: emptyText,
                    cancellationToken: cancellationToken);
            }
            return;
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(accounts.Count / (double)AccountListPageSize));
        page = Math.Clamp(page, 0, totalPages - 1);
        var pageAccounts = accounts
            .Skip(page * AccountListPageSize)
            .Take(AccountListPageSize)
            .ToList();

        var text = BuildAccountListText(accounts.Count, page, totalPages);
        var keyboard = BuildAccountListKeyboard(pageAccounts, page, totalPages);

        if (messageId != 0)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        else
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAccountViewCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        var text = BuildV3ClientInfo(client, serverInfo, credUser.IsColleague);
        var keyboard = BuildAccountDetailsKeyboard(client, page, credUser.IsColleague);

        if (messageId != 0)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        else
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAccountRenewCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        User user,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر برای تمدید پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var service = ResolveServiceForClient(client);
        await _userDbContext.SaveUserStatus(new User
        {
            Id = credUser.TelegramUserId,
            Flow = RenewFlowName,
            LastStep = RenewStepTraffic,
            ConfigLink = client.Email,
            SelectedCountry = service?.Key
        });

        var text = $"اکانت انتخاب شد: <code>{Html(client.Email)}</code>\nحجم اضافه را به گیگابایت بفرستید.\nمی‌توانید عدد دلخواه مثل <code>7</code> یا <code>۷</code> وارد کنید.";

        if (messageId != 0)
        {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: text,
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست", XuiV3PurchaseCallbacks.AccountList(page)) },
                    new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
                }),
                cancellationToken: cancellationToken);
        }
        else
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAccountDeleteAskCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر برای حذف پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var text = $"آیا از حذف این اکانت مطمئن هستید؟\n\nاکانت: <code>{Html(client.Email)}</code>\n\nاین عملیات فقط همین اکانت را حذف می‌کند.";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("تایید حذف", XuiV3PurchaseCallbacks.AccountDeleteConfirm(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.AccountView(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
        });

        await SafeEditMessageTextAsync(
            botClient,
            chatId: chatId,
            messageId: messageId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    private async Task HandleAccountDeleteConfirmCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر برای حذف پیدا نشد یا قبلاً حذف شده است.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        var deleteResponse = await ApiServicev3.DeleteClientAsync(serverInfo, _configuration, client.Email, cancellationToken);
        if (!deleteResponse.Success)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: chatId,
                messageId: messageId,
                text: $"حذف اکانت ناموفق بود.\n{deleteResponse.Msg}",
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.AccountView(client.Id, page)) },
                    new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
                }),
                cancellationToken: cancellationToken);
            return;
        }

        await _activityLog.LogBotActionAsync(
            "xui_v3_account_deleted_by_user",
            credUser,
            false,
            new Dictionary<string, object>
            {
                ["accountEmail"] = client.Email,
                ["panelUrl"] = serverInfo.Url,
                ["rootPath"] = serverInfo.RootPath
            },
            cancellationToken);

        await SafeEditMessageTextAsync(
            botClient,
            chatId: chatId,
            messageId: messageId,
            text: $"اکانت با موفقیت حذف شد.\n\nاکانت: <code>{Html(client.Email)}</code>",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست", XuiV3PurchaseCallbacks.AccountList(page)) },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
            }),
            cancellationToken: cancellationToken);
    }

    private async Task HandleAccountChangeLinkCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        bool fromSearch,
        CancellationToken cancellationToken)
    {
        if (clientId == null || clientId <= 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "شناسه اکانت معتبر نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();

        try
        {
            var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
            if (!clientsResponse.Success)
            {
                await SendOrEditTextAsync(
                    botClient,
                    chatId,
                    messageId,
                    $"دریافت اطلاعات اکانت ناموفق بود.\n{clientsResponse.Msg}",
                    replyMarkup: BuildChangeLinkResultKeyboard(clientId.Value, page, fromSearch),
                    cancellationToken: cancellationToken);
                return;
            }

            var allClients = clientsResponse.Obj ?? new List<XuiV3Client>();
            var client = allClients.FirstOrDefault(item =>
                item.Id == clientId.Value && ClientBelongsToUser(item, credUser.TelegramUserId));

            if (client == null)
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "اکانت مورد نظر برای تغییر لینک پیدا نشد یا متعلق به حساب شما نیست.",
                    cancellationToken: cancellationToken);
                return;
            }

            var oldEmail = client.Email ?? string.Empty;
            var oldUuid = client.Uuid ?? string.Empty;
            var oldSubId = string.IsNullOrWhiteSpace(client.SubId) ? oldEmail : client.SubId;
            var oldSubLink = ApiServicev3.BuildSubscriptionLink(serverInfo, oldSubId);
            var newEmail = GenerateReplacementAccountEmail(allClients, oldEmail);
            var newUuid = await GenerateReplacementUuidAsync(serverInfo, cancellationToken);
            var newSubId = newEmail;
            var newSubLink = ApiServicev3.BuildSubscriptionLink(serverInfo, newSubId);
            var payload = BuildChangeLinkPayload(client, newEmail, newUuid, credUser.TelegramUserId);

            Console.WriteLine(
                $"[XUIv3] change link user={credUser.TelegramUserId}, clientId={client.Id}, oldEmail={oldEmail}, newEmail={newEmail}, panel={serverInfo.Url}, rootPath={serverInfo.RootPath}");

            var updateResponse = await ApiServicev3.UpdateClientAsync(
                serverInfo,
                _configuration,
                oldEmail,
                payload,
                cancellationToken);

            if (!updateResponse.Success)
            {
                await _activityLog.LogWarningAsync(
                    "xui_v3_change_link_failed",
                    credUser,
                    false,
                    new Dictionary<string, object>
                    {
                        ["clientId"] = client.Id,
                        ["oldEmail"] = oldEmail,
                        ["attemptedNewEmail"] = newEmail,
                        ["message"] = updateResponse.Msg ?? string.Empty,
                        ["panelUrl"] = serverInfo.Url,
                        ["rootPath"] = serverInfo.RootPath
                    },
                    cancellationToken);

                await SendOrEditTextAsync(
                    botClient,
                    chatId,
                    messageId,
                    $"تغییر لینک ناموفق بود.\n{updateResponse.Msg}",
                    replyMarkup: BuildChangeLinkResultKeyboard(client.Id, page, fromSearch),
                    cancellationToken: cancellationToken);
                return;
            }

            client.Email = newEmail;
            client.Uuid = newUuid;
            client.SubId = newSubId;
            client.TotalGB = payload.TotalGB;
            client.ExpiryTime = payload.ExpiryTime;
            client.Comment = payload.Comment;

            await _activityLog.LogBotActionAsync(
                "xui_v3_account_link_changed",
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["clientId"] = client.Id,
                    ["ownerTelegramUserId"] = GetClientOwnerTelegramId(client),
                    ["oldEmail"] = oldEmail,
                    ["newEmail"] = newEmail,
                    ["oldUuid"] = oldUuid,
                    ["newUuid"] = newUuid,
                    ["oldSubId"] = oldSubId,
                    ["newSubId"] = newSubId,
                    ["usedGb"] = GetUsedBytes(client).ConvertBytesToGB(),
                    ["totalGb"] = GetTotalBytes(client).ConvertBytesToGB(),
                    ["expiryShamsi"] = FormatExpiry(GetExpiryTime(client)),
                    ["panelUrl"] = serverInfo.Url,
                    ["rootPath"] = serverInfo.RootPath
                },
                cancellationToken);

            _logger.LogPayment(BuildChangeLinkLogMessage(
                credUser,
                client,
                oldEmail,
                oldUuid,
                oldSubId,
                oldSubLink,
                newEmail,
                newUuid,
                newSubId,
                newSubLink,
                serverInfo));

            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                BuildChangeLinkSuccessText(oldEmail, oldUuid, oldSubId, newEmail, newUuid, newSubId, newSubLink),
                ParseMode.Html,
                BuildChangeLinkResultKeyboard(client.Id, page, fromSearch),
                cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] change link exception user={credUser.TelegramUserId}, clientId={clientId}: {ex}");
            await _activityLog.LogErrorAsync(
                "xui_v3_change_link_exception",
                ex,
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["clientId"] = clientId,
                    ["panelUrl"] = serverInfo.Url,
                    ["rootPath"] = serverInfo.RootPath
                },
                cancellationToken);

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "تغییر لینک اکانت با خطا روبه‌رو شد. لطفاً دوباره تلاش کنید یا با پشتیبانی تماس بگیرید.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAccountSearchViewCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        await SendOrEditTextAsync(
            botClient,
            chatId,
            messageId,
            BuildV3ClientInfo(client, serverInfo, credUser.IsColleague),
            ParseMode.Html,
            BuildAccountSearchDetailsKeyboard(client, page),
            cancellationToken);
    }

    private async Task HandleAccountSearchRenewCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        bool allowExternalRenew,
        CancellationToken cancellationToken)
    {
        var client = allowExternalRenew
            ? await GetAnyClientByIdAsync(clientId, cancellationToken)
            : await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);

        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: allowExternalRenew
                    ? "اکانت مورد نظر برای تمدید پیدا نشد."
                    : "اکانت مورد نظر برای تمدید پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var service = ResolveServiceForClient(client);
        if (service == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "سرویس این اکانت قابل تشخیص نیست و تمدید از سرچ انجام نشد.",
                cancellationToken: cancellationToken);
            return;
        }

        await _userDbContext.SaveUserStatus(new User
        {
            Id = credUser.TelegramUserId,
            Flow = RenewFlowName,
            LastStep = service.IsUnlimited ? RenewStepUnlimitedPlan : RenewStepTraffic,
            ConfigLink = client.Email,
            SelectedCountry = service.Key,
            PaymentMethod = allowExternalRenew ? ExternalUuidRenewPaymentMethod : "credit"
        });

        var text = service.IsUnlimited
            ? $"اکانت انتخاب شد: <code>{Html(client.Email)}</code>\nپلن تمدید نامحدود را انتخاب کنید."
            : $"اکانت انتخاب شد: <code>{Html(client.Email)}</code>\nحجم اضافه را به گیگابایت بفرستید.\nمی‌توانید عدد دلخواه مثل <code>7</code> یا <code>۷</code> وارد کنید.";

        var backKeyboard = allowExternalRenew
            ? new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("جستجوی جدید", XuiV3PurchaseCallbacks.AccountSearchStart()) },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
            })
            : new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به نتایج جستجو", XuiV3PurchaseCallbacks.AccountSearchList(page)) },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
            });

        await SendOrEditTextAsync(
            botClient,
            chatId,
            messageId,
            text,
            ParseMode.Html,
            backKeyboard,
            cancellationToken);

        if (service.IsUnlimited)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "یکی از گزینه‌های زیر را انتخاب کنید:",
                replyMarkup: BuildUnlimitedPlanReplyKeyboard(service, credUser.IsColleague),
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAccountSearchDeleteAskCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر برای حذف پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var text = $"آیا از حذف این اکانت مطمئن هستید؟\n\nاکانت: <code>{Html(client.Email)}</code>\n\nاین عملیات فقط همین اکانت را حذف می‌کند.";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("تایید حذف", XuiV3PurchaseCallbacks.AccountSearchDeleteConfirm(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.AccountSearchView(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
        });

        await SendOrEditTextAsync(
            botClient,
            chatId,
            messageId,
            text,
            ParseMode.Html,
            keyboard,
            cancellationToken);
    }

    private async Task HandleAccountSearchDeleteConfirmCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر برای حذف پیدا نشد یا قبلاً حذف شده است.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        var deleteResponse = await ApiServicev3.DeleteClientAsync(serverInfo, _configuration, client.Email, cancellationToken);
        if (!deleteResponse.Success)
        {
            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                $"حذف اکانت ناموفق بود.\n{deleteResponse.Msg}",
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.AccountSearchView(client.Id, page)) },
                    new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
                }),
                cancellationToken: cancellationToken);
            return;
        }

        await _activityLog.LogBotActionAsync(
            "xui_v3_account_deleted_by_user_search",
            credUser,
            false,
            new Dictionary<string, object>
            {
                ["accountEmail"] = client.Email,
                ["panelUrl"] = serverInfo.Url,
                ["rootPath"] = serverInfo.RootPath
            },
            cancellationToken);

        await SendOrEditTextAsync(
            botClient,
            chatId,
            messageId,
            $"اکانت با موفقیت حذف شد.\n\nاکانت: <code>{Html(client.Email)}</code>",
            ParseMode.Html,
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به نتایج جستجو", XuiV3PurchaseCallbacks.AccountSearchList(page)) },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست کلی", XuiV3PurchaseCallbacks.AccountList(0)) },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
            }),
            cancellationToken);
    }

    private async Task HandleAccountSearchStateCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        int page,
        bool enable,
        CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientByIdAsync(credUser.TelegramUserId, clientId, cancellationToken);
        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "اکانت مورد نظر پیدا نشد یا متعلق به حساب شما نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        var serverInfo = BuildConfiguredPanelServerInfo();
        var updateResponse = await ApiServicev3.SetClientEnabledAsync(
            serverInfo,
            _configuration,
            client.Email,
            enable,
            credUser.TelegramUserId,
            cancellationToken);
        if (!updateResponse.Success)
        {
            await SendOrEditTextAsync(
                botClient,
                chatId,
                messageId,
                $"متاسفانه عملیات مورد نظر انجام نشد.\n{updateResponse.Msg}",
                replyMarkup: BuildAccountSearchDetailsKeyboard(client, page),
                cancellationToken: cancellationToken);
            return;
        }

        await _activityLog.LogBotActionAsync(
            enable ? "xui_v3_account_enabled_search" : "xui_v3_account_disabled_search",
            credUser,
            false,
            new Dictionary<string, object>
            {
                ["accountEmail"] = client.Email,
                ["panelUrl"] = serverInfo.Url,
                ["rootPath"] = serverInfo.RootPath,
                ["source"] = "account_search_callback"
            },
            cancellationToken);

        client.Enable = enable;
        await SendOrEditTextAsync(
            botClient,
            chatId,
            messageId,
            BuildV3ClientInfo(client, serverInfo, credUser.IsColleague),
            ParseMode.Html,
            BuildAccountSearchDetailsKeyboard(client, page),
            cancellationToken);
    }

    private async Task<XuiV3Client> GetOwnedClientByIdAsync(
        long telegramUserId,
        int? clientId,
        CancellationToken cancellationToken)
    {
        if (clientId == null || clientId <= 0)
            return null;

        var serverInfo = BuildConfiguredPanelServerInfo();
        var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!clientsResponse.Success)
            return null;

        return clientsResponse.Obj?
            .FirstOrDefault(client => client.Id == clientId.Value && ClientBelongsToUser(client, telegramUserId));
    }

    private async Task<XuiV3Client> GetAnyClientByIdAsync(
        int? clientId,
        CancellationToken cancellationToken)
    {
        if (clientId == null || clientId <= 0)
            return null;

        var serverInfo = BuildConfiguredPanelServerInfo();
        var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        if (!clientsResponse.Success)
            return null;

        return clientsResponse.Obj?.FirstOrDefault(client => client.Id == clientId.Value);
    }

    private static string BuildAccountListText(int totalAccounts, int page, int totalPages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("اکانت‌های من");
        builder.AppendLine();
        builder.AppendLine($"تعداد کل: <code>{totalAccounts}</code>");
        builder.AppendLine($"صفحه: <code>{page + 1}</code> از <code>{totalPages}</code>");
        builder.AppendLine();
        builder.AppendLine("برای دیدن مشخصات، تمدید یا حذف تکی، یکی از اکانت‌ها را انتخاب کنید.");
        builder.AppendLine("علامت باتری خالی یعنی اکانت منقضی شده یا حجم آن تمام شده است.");
        return builder.ToString();
    }

    private static string BuildAccountSearchListText(string query, int totalAccounts, int page, int totalPages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("نتایج جستجوی اکانت");
        builder.AppendLine();
        builder.AppendLine($"عبارت: <code>{Html(query)}</code>");
        builder.AppendLine($"تعداد نتیجه: <code>{totalAccounts}</code>");
        builder.AppendLine($"صفحه: <code>{page + 1}</code> از <code>{totalPages}</code>");
        builder.AppendLine();
        builder.AppendLine("برای دیدن مشخصات کامل، تمدید، حذف یا فعال/غیرفعال‌سازی، یکی از اکانت‌ها را انتخاب کنید.");
        builder.AppendLine("علامت باتری خالی یعنی اکانت منقضی شده یا حجم آن تمام شده است.");
        return builder.ToString();
    }

    private static string BuildUuidSearchResultText(XuiV3Client client, ServerInfo serverInfo, bool isOwner)
    {
        var builder = new StringBuilder();
        builder.AppendLine("نتیجه جستجوی UUID");
        builder.AppendLine();
        builder.AppendLine(isOwner
            ? "این اکانت متعلق به حساب شماست."
            : "این اکانت متعلق به حساب شما نیست؛ فقط امکان تمدید آن فعال است.");
        builder.AppendLine();
        builder.Append(BuildV3ClientInfo(client, serverInfo, false, false));
        return builder.ToString();
    }

    private static InlineKeyboardMarkup BuildAccountListKeyboard(
        IReadOnlyList<XuiV3Client> pageAccounts,
        int page,
        int totalPages)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var client in pageAccounts)
        {
            var marker = IsExpiredOrDepleted(client) ? "🪫 " : "";
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{marker}{client.Email}",
                    XuiV3PurchaseCallbacks.AccountView(client.Id, page))
            });
        }

        var nav = new List<InlineKeyboardButton>();
        if (page > 0)
            nav.Add(InlineKeyboardButton.WithCallbackData("قبلی", XuiV3PurchaseCallbacks.AccountList(page - 1)));
        if (page < totalPages - 1)
            nav.Add(InlineKeyboardButton.WithCallbackData("بعدی", XuiV3PurchaseCallbacks.AccountList(page + 1)));
        if (nav.Count > 0)
            rows.Add(nav.ToArray());

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 جستجوی اکانت", XuiV3PurchaseCallbacks.AccountSearchStart()) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) });

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildAccountSearchListKeyboard(
        IReadOnlyList<XuiV3Client> pageAccounts,
        int page,
        int totalPages)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var client in pageAccounts)
        {
            var marker = IsExpiredOrDepleted(client) ? "🪫 " : "";
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{marker}{client.Email}",
                    XuiV3PurchaseCallbacks.AccountSearchView(client.Id, page))
            });
        }

        var nav = new List<InlineKeyboardButton>();
        if (page > 0)
            nav.Add(InlineKeyboardButton.WithCallbackData("قبلی", XuiV3PurchaseCallbacks.AccountSearchList(page - 1)));
        if (page < totalPages - 1)
            nav.Add(InlineKeyboardButton.WithCallbackData("بعدی", XuiV3PurchaseCallbacks.AccountSearchList(page + 1)));
        if (nav.Count > 0)
            rows.Add(nav.ToArray());

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("جستجوی جدید", XuiV3PurchaseCallbacks.AccountSearchStart()) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست کلی", XuiV3PurchaseCallbacks.AccountList(0)) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) });

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildAccountDetailsKeyboard(XuiV3Client client, int page, bool isColleague)
    {
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("تمدید اکانت", XuiV3PurchaseCallbacks.AccountRenew(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("تغییر لینک", XuiV3PurchaseCallbacks.AccountChangeLink(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("حذف همین اکانت", XuiV3PurchaseCallbacks.AccountDeleteAsk(client.Id, page)) }
        };

        var actionText = client.Enable ? "غیرفعال کردن" : "فعال کردن";
        var callbackData = client.Enable
            ? XuiV3PurchaseCallbacks.AccountState(client.Id, false)
            : XuiV3PurchaseCallbacks.AccountState(client.Id, true);
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData(actionText, callbackData) });

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست", XuiV3PurchaseCallbacks.AccountList(page)) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) });
        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildAccountSearchDetailsKeyboard(XuiV3Client client, int page)
    {
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("تمدید اکانت", XuiV3PurchaseCallbacks.AccountSearchRenew(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("تغییر لینک", XuiV3PurchaseCallbacks.AccountSearchChangeLink(client.Id, page)) },
            new[] { InlineKeyboardButton.WithCallbackData("حذف همین اکانت", XuiV3PurchaseCallbacks.AccountSearchDeleteAsk(client.Id, page)) }
        };

        var actionText = client.Enable ? "غیرفعال کردن" : "فعال کردن";
        var callbackData = client.Enable
            ? XuiV3PurchaseCallbacks.AccountSearchState(client.Id, false, page)
            : XuiV3PurchaseCallbacks.AccountSearchState(client.Id, true, page);
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData(actionText, callbackData) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به نتایج جستجو", XuiV3PurchaseCallbacks.AccountSearchList(page)) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست کلی", XuiV3PurchaseCallbacks.AccountList(0)) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) });
        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildUuidSearchResultKeyboard(XuiV3Client client)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("تمدید اکانت", XuiV3PurchaseCallbacks.AccountUuidRenew(client.Id)) },
            new[] { InlineKeyboardButton.WithCallbackData("جستجوی جدید", XuiV3PurchaseCallbacks.AccountSearchStart()) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست کلی", XuiV3PurchaseCallbacks.AccountList(0)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
        });
    }

    private static InlineKeyboardMarkup BuildSearchStartKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست کلی", XuiV3PurchaseCallbacks.AccountList(0)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
        });
    }

    private static InlineKeyboardMarkup BuildSearchEmptyKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("جستجوی جدید", XuiV3PurchaseCallbacks.AccountSearchStart()) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به لیست کلی", XuiV3PurchaseCallbacks.AccountList(0)) },
            new[] { InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home()) }
        });
    }

    private async Task SendV3AccountsInfoAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        bool isColleague,
        ServerInfo serverInfo,
        List<XuiV3Client> clients,
        CancellationToken cancellationToken)
    {
        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "✅ وضعیت اکانت‌های شما به شرح زیر است:",
            cancellationToken: cancellationToken);

        foreach (var client in clients)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: BuildV3ClientInfo(client, serverInfo, isColleague),
                parseMode: ParseMode.Html,
                replyMarkup: BuildV3AccountKeyboard(client, isColleague),
                cancellationToken: cancellationToken);
        }
    }

    private XuiV3ServiceDefinition FindService(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
            return null;

        return _purchaseService.GetEnabledServices().FirstOrDefault(service =>
            string.Equals(service.Key, serviceKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatExpiry(long expiryTime)
    {
        if (expiryTime <= 0)
            return "نامحدود";

        return DateTimeOffset
            .FromUnixTimeMilliseconds(expiryTime)
            .UtcDateTime
            .AddMinutes(210)
            .ConvertToHijriShamsi();
    }

    private void LogV3Purchase(
        string title,
        CredUser credUser,
        long priceToman,
        long? beforeBalance,
        long? afterBalance,
        IEnumerable<string> details)
    {
        var message = new StringBuilder();
        var normalizedDetails = new List<string>();
        XuiV3ClientMetadata metadataFromComment = null;

        foreach (var detail in details ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(detail))
                continue;

            if (IsCommentLogDetail(detail))
            {
                metadataFromComment ??= TryReadMetadata(ExtractCodeValue(detail));
                continue;
            }

            normalizedDetails.Add(detail);
        }

        message.AppendLine(title);
        message.AppendLine($"یوزر `{credUser.TelegramUserId}`");

        var userSummary = FormatCredUserSummary(credUser);
        if (!string.IsNullOrWhiteSpace(userSummary))
            message.AppendLine(userSummary);

        message.AppendLine($"مبلغ `{priceToman.FormatCurrency()}`");

        if (beforeBalance.HasValue)
            message.AppendLine($"موجودی قبل از خرید `{beforeBalance.Value.FormatCurrency()}`");

        if (afterBalance.HasValue)
            message.AppendLine($"موجودی پس از خرید `{afterBalance.Value.FormatCurrency()}`");

        if (metadataFromComment != null)
        {
            message.AppendLine($"تاریخ ساخت `{FormatMetadataCreatedAt(metadataFromComment)}`");

            if (!string.IsNullOrWhiteSpace(metadataFromComment.ServiceName))
                message.AppendLine($"سرویس `{metadataFromComment.ServiceName}`");
        }

        foreach (var detail in normalizedDetails)
        {
            message.AppendLine(detail);
        }

        _logger.LogInformation(message.ToString().EscapeMarkdown());
    }

    private static string BuildBulkFailureText(XuiV3BulkCreationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("⚠️ بخشی از ساخت اکانت ناموفق بود.");
        builder.AppendLine($"تعداد درخواستی: <code>{result.RequestedCount}</code>");
        builder.AppendLine($"تعداد ساخته‌شده: <code>{result.SuccessfulCount}</code>");

        foreach (var failure in result.Failures)
        {
            builder.AppendLine();
            builder.AppendLine($"ردیف: <code>{failure.Index}</code>");
            builder.AppendLine($"خطا: <code>{Html(failure.Message)}</code>");
        }

        return builder.ToString();
    }

    private static IEnumerable<string> BuildBulkPurchaseLogDetails(
        XuiV3BulkCreationResult result,
        string userComment)
    {
        var details = new List<string>
        {
            $"شناسه سفارش `{result.BulkOrderId}`",
            $"تعداد درخواستی `{result.RequestedCount}`",
            $"تعداد ساخته‌شده `{result.SuccessfulCount}`",
            $"سرویس `{result.ServiceName}`",
            $"حجم هر اکانت `{XuiV3PurchaseService.FormatTrafficSize(result.TrafficBytes, result.TrafficGb)}`"
        };

        if (result.DurationDays <= 0)
            details.Add("انقضا `نامحدود`");
        else
            details.Add($"مدت `{result.DurationDays} روز`");

        if (!string.IsNullOrWhiteSpace(userComment))
            details.Add($"کامنت کاربر `{ShortenForLog(userComment, 120)}`");

        foreach (var account in result.CreatedAccounts)
        {
            details.Add($"اکانت `{account.Email}`");
            details.Add($"سابلینک `{account.SubLink}`");
        }

        if (result.Failures.Count > 0)
        {
            foreach (var failure in result.Failures)
                details.Add($"خطای ردیف {failure.Index} `{ShortenForLog(failure.Message, 120)}`");
        }

        return details;
    }

    private static string ShortenForLog(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized.Substring(0, maxLength) + "...";
    }

    private static bool IsCommentLogDetail(string detail)
    {
        return detail.StartsWith("کامنت `", StringComparison.Ordinal) ||
               detail.StartsWith("comment `", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCodeValue(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        var firstTick = detail.IndexOf('`');
        var lastTick = detail.LastIndexOf('`');
        if (firstTick < 0 || lastTick <= firstTick)
            return string.Empty;

        return detail.Substring(firstTick + 1, lastTick - firstTick - 1);
    }

    private static string FormatMetadataCreatedAt(XuiV3ClientMetadata metadata)
    {
        if (metadata == null)
            return "نامشخص";

        return metadata.CreatedAtUtc.AddMinutes(210).ConvertToHijriShamsi();
    }

    private static string FormatCredUserSummary(CredUser credUser)
    {
        if (credUser == null)
            return string.Empty;

        var parts = new List<string>();
        var fullName = string.Join(" ", new[] { credUser.FirstName, credUser.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));

        if (!string.IsNullOrWhiteSpace(fullName))
            parts.Add($"نام `{fullName}`");

        if (!string.IsNullOrWhiteSpace(credUser.Username))
            parts.Add($"یوزرنیم `@{credUser.Username.Trim().TrimStart('@')}`");

        parts.Add($"نوع `{(credUser.IsColleague ? "همکار" : "کاربر عادی")}`");

        return string.Join("\n", parts);
    }

    private static string BuildColleagueRequestLogMessage(CredUser credUser)
    {
        var fullName = string.Join(" ", new[] { credUser?.FirstName, credUser?.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));

        var username = string.IsNullOrWhiteSpace(credUser?.Username)
            ? "ندارد"
            : "@" + credUser.Username.Trim().TrimStart('@');

        var phone = string.IsNullOrWhiteSpace(credUser?.PhoneNumber)
            ? "ثبت نشده"
            : credUser.PhoneNumber.Trim();

        var email = string.IsNullOrWhiteSpace(credUser?.Email)
            ? "ثبت نشده"
            : credUser.Email.Trim();

        var now = DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi();

        return "🤝 <b>درخواست همکاری جدید</b>\n\n" +
               $"👤 نام: <code>{Html(string.IsNullOrWhiteSpace(fullName) ? "ثبت نشده" : fullName)}</code>\n" +
               $"🔹 یوزرنیم: <code>{Html(username)}</code>\n" +
               $"🆔 آیدی عددی: <code>{credUser?.TelegramUserId ?? 0}</code>\n" +
               $"💬 Chat ID: <code>{credUser?.ChatID ?? 0}</code>\n" +
               $"📱 شماره تلفن: <code>{Html(phone)}</code>\n" +
               $"📧 ایمیل: <code>{Html(email)}</code>\n" +
               $"💰 موجودی: <code>{Html((credUser?.AccountBalance ?? 0).FormatCurrency())}</code>\n" +
               $"👥 نوع فعلی: <code>{(credUser?.IsColleague == true ? "همکار" : "کاربر عادی")}</code>\n" +
               $"📌 شرط اعلام‌شده: <code>حداقل فروش هفتگی {MinimumWeeklyColleagueSalesToman.FormatCurrency()}</code>\n" +
               $"🕒 زمان درخواست: <code>{Html(now)}</code>";
    }

    private XuiV3ServiceDefinition ResolveServiceForClient(XuiV3Client client)
    {
        var metadata = TryReadMetadata(client.Comment);
        var services = _purchaseService.GetEnabledServices();
        var clientInboundIds = GetClientInboundIds(client, metadata);

        if (clientInboundIds.Count == 0)
        {
            var metadataService = FindService(metadata?.ServiceKey);
            if (metadataService != null)
                Console.WriteLine($"[XUIv3] resolve service by metadata only email={client?.Email}, service={metadataService.Key}");

            return metadataService;
        }

        var nationalService = services.FirstOrDefault(service =>
            IsNationalService(service) && HasAnyInbound(clientInboundIds, service));
        if (nationalService != null)
        {
            Console.WriteLine($"[XUIv3] resolve service by inbound priority email={client.Email}, service={nationalService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return nationalService;
        }

        var unlimitedService = services.FirstOrDefault(service =>
            service.IsUnlimited && HasAnyInbound(clientInboundIds, service));
        if (unlimitedService != null)
        {
            Console.WriteLine($"[XUIv3] resolve service by inbound priority email={client.Email}, service={unlimitedService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return unlimitedService;
        }

        var normalService = services.FirstOrDefault(service =>
            IsNormalService(service) &&
            IsOnlyInServiceInbounds(clientInboundIds, service) &&
            HasAnyInbound(clientInboundIds, service));
        if (normalService != null)
        {
            Console.WriteLine($"[XUIv3] resolve service by inbound priority email={client.Email}, service={normalService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
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
            Console.WriteLine($"[XUIv3] resolve service by inbound fallback email={client.Email}, service={meteredService.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return meteredService;
        }

        var metadataFallback = FindService(metadata?.ServiceKey);
        if (metadataFallback != null)
        {
            Console.WriteLine($"[XUIv3] resolve service metadata fallback email={client.Email}, service={metadataFallback.Key}, inboundIds=[{string.Join(",", clientInboundIds)}]");
            return metadataFallback;
        }

        return null;
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

    private async Task<XuiV3Client> FindClientByEmailAsync(
        ServerInfo serverInfo,
        string email,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeAccountNameInput(email);
        XuiV3Client directClient = null;

        try
        {
            var directResponse = await ApiServicev3.GetClientAsync(serverInfo, _configuration, normalizedEmail, cancellationToken);
            directClient = directResponse.Obj;
            Console.WriteLine(
                $"[XUIv3] find client direct email={normalizedEmail}, success={directResponse.Success}, msg={directResponse.Msg}, found={(directClient == null ? "no" : "yes")}, tgId={directClient?.TgId}, metadataUser={TryReadMetadata(directClient?.Comment)?.TelegramUserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] find client direct exception email={normalizedEmail}: {ex.Message}");
        }

        if (directClient != null && EmailEquals(directClient.Email, normalizedEmail) && ClientBelongsToUser(directClient, telegramUserId))
            return directClient;

        var listResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
        var matches = listResponse.Obj?
            .Where(client => EmailEquals(client.Email, normalizedEmail))
            .ToList() ?? new List<XuiV3Client>();

        Console.WriteLine(
            $"[XUIv3] find client list email={normalizedEmail}, success={listResponse.Success}, total={listResponse.Obj?.Count ?? 0}, matches={matches.Count}, ownerMatches={matches.Count(client => ClientBelongsToUser(client, telegramUserId))}");

        var ownerMatch = matches.FirstOrDefault(client => ClientBelongsToUser(client, telegramUserId));
        if (ownerMatch != null)
            return ownerMatch;

        if (matches.Count > 0)
            return matches[0];

        return directClient;
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

    private static bool EmailEquals(string left, string right)
    {
        return string.Equals(
            NormalizeAccountNameInput(left),
            NormalizeAccountNameInput(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAccountNameInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeDigits(value).Trim();
        return new string(normalized.Where(ch => !char.IsControl(ch) && ch != '\u200e' && ch != '\u200f').ToArray()).Trim();
    }

    private static ReplyKeyboardMarkup BuildTrafficReplyKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = service.TrafficOptionsGb
            .OrderBy(gb => gb)
            .Select(gb => new KeyboardButton($"{gb} GB"))
            .Chunk(3)
            .Select(chunk => chunk.ToArray())
            .Append(new[] { new KeyboardButton("انصراف") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildDurationReplyKeyboard(XuiV3ServiceDefinition service)
    {
        var rows = service.DurationOptions
            .OrderBy(duration => duration.Days)
            .Select(duration => new KeyboardButton($"{duration.DisplayName} [{duration.Key}]"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .Append(new[] { new KeyboardButton("انصراف") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildUnlimitedPlanReplyKeyboard(XuiV3ServiceDefinition service, bool isColleague)
    {
        var rows = service.UnlimitedPlans
            .Where(plan => plan.IsEnabled)
            .Select(plan => new KeyboardButton($"{plan.DisplayName} [{plan.Key}] - {plan.Price.GetForRole(isColleague).FormatCurrency()}"))
            .Chunk(1)
            .Select(chunk => chunk.ToArray())
            .Append(new[] { new KeyboardButton("انصراف") });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private static ReplyKeyboardMarkup BuildConfirmReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("تایید تمدید") },
            new[] { new KeyboardButton("انصراف") }
        })
        {
            ResizeKeyboard = true
        };
    }

    private static InlineKeyboardMarkup BuildAccountCountInlineKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1", XuiV3PurchaseCallbacks.AccountCount(1)),
                InlineKeyboardButton.WithCallbackData("2", XuiV3PurchaseCallbacks.AccountCount(2)),
                InlineKeyboardButton.WithCallbackData("3", XuiV3PurchaseCallbacks.AccountCount(3))
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("5", XuiV3PurchaseCallbacks.AccountCount(5)),
                InlineKeyboardButton.WithCallbackData("10", XuiV3PurchaseCallbacks.AccountCount(10))
            },
            new[] { InlineKeyboardButton.WithCallbackData("انصراف", XuiV3PurchaseCallbacks.Cancel()) }
        });
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

    private static ReplyKeyboardMarkup BuildColleagueRequestConfirmKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("شرایط را قبول دارم و درخواست همکاری می‌فرستم") },
            new[] { new KeyboardButton("انصراف") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    private static ReplyKeyboardMarkup BuildTrialServiceReplyKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("تست نت ملی") },
            new[] { new KeyboardButton("تست نت عادی") },
            new[] { new KeyboardButton("انصراف") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    private static string TryGetTrialServiceKey(string text)
    {
        var key = ExtractBracketValue(text);
        if (string.Equals(key, "national", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text?.Trim(), "national", StringComparison.OrdinalIgnoreCase) ||
            text?.Contains("ملی", StringComparison.OrdinalIgnoreCase) == true)
            return "national";

        if (string.Equals(key, "normal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text?.Trim(), "normal", StringComparison.OrdinalIgnoreCase) ||
            text?.Contains("عادی", StringComparison.OrdinalIgnoreCase) == true)
            return "normal";

        return null;
    }

    private async Task<bool> EnsurePhoneVerifiedAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CredUser credUser,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(credUser?.PhoneNumber))
            return true;

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "برای خرید یا تمدید اکانت، ابتدا باید شماره تلفن خودتان را تایید کنید. لطفاً فقط از دکمه پایین پیام استفاده کنید.",
            replyMarkup: BuildPhoneNumberKeyboard(),
            cancellationToken: cancellationToken);

        return false;
    }

    private static ReplyKeyboardMarkup BuildPhoneNumberKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { KeyboardButton.WithRequestContact("ارسال شماره تلفن") },
            new[] { new KeyboardButton("لغو") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }

    private async Task<string> BuildRenewSummaryAsync(
        User user,
        bool isColleague,
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var service = FindService(user.SelectedCountry);
        var selection = service.IsUnlimited
            ? new XuiV3PurchaseSelection { ServiceKey = service.Key, UnlimitedPlanKey = user.Type }
            : new XuiV3PurchaseSelection
            {
                ServiceKey = service.Key,
                TrafficGb = int.TryParse(user.TotoalGB, out var trafficGb) ? trafficGb : 0,
                DurationKey = user.SelectedPeriod
            };

        var resolved = _purchaseService.ResolvePurchase(selection, isColleague);
        var durationText = resolved.DurationDays <= 0 ? "نامحدود / لایف‌تایم" : $"{resolved.DurationDays} روز";
        var usedBytes = 0L;
        var totalBytes = 0L;

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            var client = await FindClientByEmailAsync(serverInfo, user.ConfigLink, telegramUserId, cancellationToken);
            if (client != null)
            {
                usedBytes = GetUsedBytes(client);
                totalBytes = GetTotalBytes(client);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] renew summary traffic lookup failed user={telegramUserId}, email={user.ConfigLink}: {ex.Message}");
        }

        var totalAfterRenewBytes = totalBytes > 0
            ? totalBytes + ApiService.ConvertGBToBytes(resolved.TrafficGb)
            : 0;

        var text = new StringBuilder();
        text.AppendLine("✅ وضعیت تمدید");
        text.AppendLine();
        text.AppendLine($"👤 اکانت: <code>{Html(user.ConfigLink)}</code>");
        text.AppendLine($"🧩 سرویس: <code>{Html(resolved.Service.DisplayName)}</code>");
        text.AppendLine($"📦 حجم اضافه: <code>{resolved.TrafficGb} GB</code>");
        text.AppendLine($"📦 حجم کلی بعد از تمدید: <code>{Html(FormatGb(totalAfterRenewBytes))}</code>");
        text.AppendLine($"🔋 مصرف شده تا کنون: <code>{Html(FormatGb(usedBytes, zeroAsUnknown: false))}</code>");
        text.AppendLine($"⏳ زمان اضافه: <code>{Html(durationText)}</code>");
        text.AppendLine($"💰 قیمت: <code>{Html(resolved.PriceToman.FormatCurrency())}</code>");
        return text.ToString();
    }

    private static XuiV3ClientPayload BuildRenewPayload(
        XuiV3Client client,
        XuiV3ResolvedPurchase resolved,
        string action,
        long actorTelegramUserId = 0)
    {
        var currentTotalBytes = GetTotalBytes(client);
        var updatedTotalBytes = currentTotalBytes + ApiService.ConvertGBToBytes(resolved.TrafficGb);
        var currentExpiryTime = GetExpiryTime(client);
        var updatedExpiryTime = CalculateRenewedExpiryTime(currentExpiryTime, resolved.DurationDays);
        var ownerTelegramUserId = GetClientOwnerTelegramId(client);
        if (ownerTelegramUserId <= 0)
            ownerTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : client.TgId;

        var metadata = TryReadMetadata(client.Comment) ?? new XuiV3ClientMetadata
        {
            TelegramUserId = ownerTelegramUserId,
            ServiceKey = resolved.Service.Key,
            ServiceName = resolved.Service.DisplayName,
            ServiceKind = resolved.Service.Kind
        };

        metadata.TelegramUserId = ownerTelegramUserId;
        metadata.ServiceKey = resolved.Service.Key;
        metadata.ServiceName = resolved.Service.DisplayName;
        metadata.ServiceKind = resolved.Service.Kind;
        metadata.PlanKey = resolved.IsUnlimited ? resolved.UnlimitedPlan?.Key : resolved.Duration?.Key;
        metadata.PlanName = resolved.IsUnlimited ? resolved.UnlimitedPlan?.DisplayName : resolved.Duration?.DisplayName;
        metadata.LimitIp = resolved.LimitIp;
        metadata.PriceToman = resolved.PriceToman;
        metadata.LastUpdatedByTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : ownerTelegramUserId;
        metadata.LastAction = action;
        metadata.LastRenewedAtUtc = DateTime.UtcNow;
        metadata.TrafficGb = Convert.ToInt32(Math.Max(0, updatedTotalBytes).ConvertBytesToGB());
        if (resolved.DurationDays <= 0)
            metadata.DurationDays = 0;
        else
            metadata.DurationDays += resolved.DurationDays;
        metadata.Renewals ??= new List<XuiV3ClientRenewalRecord>();
        metadata.Renewals.Add(new XuiV3ClientRenewalRecord
        {
            ActorTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : ownerTelegramUserId,
            AddedTrafficGb = resolved.TrafficGb,
            AddedDurationDays = resolved.DurationDays,
            TotalBytesAfter = updatedTotalBytes,
            ExpiryTimeAfter = updatedExpiryTime
        });

        return new XuiV3ClientPayload
        {
            Email = client.Email,
            Uuid = client.Uuid,
            Password = client.Password,
            TotalGB = updatedTotalBytes,
            ExpiryTime = updatedExpiryTime,
            TgId = ownerTelegramUserId,
            LimitIp = client.LimitIp,
            Enable = true,
            SubId = client.SubId,
            Flow = client.Flow,
            Comment = JsonConvert.SerializeObject(metadata, Formatting.None),
            Group = client.Group,
            Reverse = client.Reverse,
            Extra = client.Extra
        };
    }

    private static long CalculateRenewedExpiryTime(long currentExpiryTime, int addedDurationDays)
    {
        if (addedDurationDays <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var baseDate = currentExpiryTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(currentExpiryTime)
            : now;

        if (baseDate < now)
            baseDate = now;

        return baseDate.AddDays(addedDurationDays).ToUnixTimeMilliseconds();
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

    private static bool TryGetIntFromText(string text, out int value)
    {
        value = 0;
        var normalized = NormalizeDigits(text);
        var digits = new string((normalized ?? string.Empty).Where(char.IsDigit).ToArray());
        return !string.IsNullOrWhiteSpace(digits) && int.TryParse(digits, out value);
    }

    private static bool TryParseAccountCounterLookup(string text, out int accountCounter)
    {
        accountCounter = 0;
        var normalized = NormalizeDigits(text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        normalized = normalized
            .Replace("\u064a", "\u06cc")
            .Replace("\u0643", "\u06a9");

        var match = System.Text.RegularExpressions.Regex.Match(
            normalized,
            @"^(?:/account_|اکانت\s*)?(?<counter>\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return match.Success && int.TryParse(match.Groups["counter"].Value, out accountCounter) && accountCounter > 0;
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

    private static bool IsCancel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        return value.Equals("انصراف", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("لغو", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("منوی اصلی", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/start", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeSearchText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return NormalizeDigits(text)
            .Trim()
            .Replace("\u064a", "\u06cc")
            .Replace("\u0643", "\u06a9")
            .ToLowerInvariant();
    }

    private static bool TryExtractUuidQuery(string text, out string uuid)
    {
        uuid = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!match.Success || !Guid.TryParse(match.Value, out var parsed))
            return false;

        uuid = parsed.ToString();
        return true;
    }

    private static bool ClientUuidEquals(XuiV3Client client, string uuid)
    {
        return client != null &&
               !string.IsNullOrWhiteSpace(client.Uuid) &&
               string.Equals(client.Uuid.Trim(), uuid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ClientMatchesSearchQuery(XuiV3Client client, string normalizedQuery)
    {
        if (client == null || string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var email = NormalizeSearchText(client.Email);
        if (!string.IsNullOrWhiteSpace(email) && email.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            return true;

        var metadata = TryReadMetadata(client.Comment);
        var userComment = NormalizeSearchText(metadata?.UserComment);
        return !string.IsNullOrWhiteSpace(userComment) &&
               userComment.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SendOrEditTextAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        if (messageId != 0)
        {
            await SafeEditMessageTextAsync(
                botClient,
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            replyMarkup: replyMarkup,
            cancellationToken: cancellationToken);
    }

    private static async Task SafeEditMessageTextAsync(
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
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (
            ex.ErrorCode == 400 &&
            ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[XUIv3] Telegram edit ignored: message is not modified. messageId={messageId}");
        }
    }

    private static async Task AnswerCallbackSafelyAsync(
        ITelegramBotClient botClient,
        string callbackQueryId,
        CancellationToken cancellationToken)
    {
        try
        {
            await botClient.AnswerCallbackQueryAsync(callbackQueryId, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (
            ex.ErrorCode == 400 &&
            ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[XUIv3] Telegram callback answer ignored: query is too old.");
        }
        catch (ApiRequestException ex) when (
            ex.ErrorCode == 400 &&
            ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[XUIv3] Telegram callback answer ignored: query ID is invalid.");
        }
    }

    private static string GenerateReplacementAccountEmail(IReadOnlyCollection<XuiV3Client> clients, string oldEmail)
    {
        var suffix = ExtractAccountCounterSuffix(oldEmail);
        var existingEmails = (clients ?? Array.Empty<XuiV3Client>())
            .Select(client => client.Email)
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = AccountGenerator.GenerateRandomAccountName() + suffix;
            if (!existingEmails.Contains(candidate))
                return candidate;
        }

        return AccountGenerator.GenerateRandomAccountName() + suffix;
    }

    private static string ExtractAccountCounterSuffix(string email)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            email ?? string.Empty,
            @"_(?<counter>\d+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return match.Success ? "_" + match.Groups["counter"].Value : string.Empty;
    }

    private async Task<string> GenerateReplacementUuidAsync(ServerInfo serverInfo, CancellationToken cancellationToken)
    {
        try
        {
            var uuidResponse = await ApiServicev3.GetNewUuidAsync(serverInfo, _configuration, cancellationToken);
            var token = uuidResponse.Obj;
            var candidate = token?.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? token.ToObject<string>()
                : token?["uuid"]?.ToString()
                  ?? token?["id"]?.ToString()
                  ?? token?.ToString();

            if (uuidResponse.Success && Guid.TryParse(candidate, out var parsed))
                return parsed.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] get new uuid failed, fallback to local guid: {ex.Message}");
        }

        return Guid.NewGuid().ToString();
    }

    private static XuiV3ClientPayload BuildChangeLinkPayload(
        XuiV3Client client,
        string newEmail,
        string newUuid,
        long actorTelegramUserId)
    {
        var metadata = TryReadMetadata(client.Comment);
        var comment = client.Comment;
        if (metadata != null)
        {
            metadata.LastUpdatedByTelegramUserId = actorTelegramUserId;
            metadata.LastAction = "change-link";
            comment = JsonConvert.SerializeObject(metadata, Formatting.None);
        }

        var ownerTelegramUserId = GetClientOwnerTelegramId(client);
        if (ownerTelegramUserId <= 0)
            ownerTelegramUserId = actorTelegramUserId > 0 ? actorTelegramUserId : client.TgId;
        if (metadata != null)
        {
            if (metadata.TelegramUserId <= 0)
                metadata.TelegramUserId = ownerTelegramUserId;
            comment = JsonConvert.SerializeObject(metadata, Formatting.None);
        }

        return new XuiV3ClientPayload
        {
            Email = newEmail,
            Uuid = newUuid,
            Password = client.Password,
            TotalGB = GetTotalBytes(client),
            ExpiryTime = GetExpiryTime(client),
            TgId = ownerTelegramUserId,
            LimitIp = client.LimitIp,
            Enable = client.Enable,
            SubId = newEmail,
            Flow = client.Flow,
            Comment = comment,
            Group = client.Group,
            Reverse = client.Reverse,
            Extra = client.Extra
        };
    }

    private static InlineKeyboardMarkup BuildChangeLinkResultKeyboard(int clientId, int page, bool fromSearch)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "مشاهده مشخصات",
                    fromSearch
                        ? XuiV3PurchaseCallbacks.AccountSearchView(clientId, page)
                        : XuiV3PurchaseCallbacks.AccountView(clientId, page))
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    fromSearch ? "بازگشت به نتایج جستجو" : "بازگشت به لیست",
                    fromSearch
                        ? XuiV3PurchaseCallbacks.AccountSearchList(page)
                        : XuiV3PurchaseCallbacks.AccountList(page))
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home())
            }
        });
    }

    private static string BuildChangeLinkSuccessText(
        string oldEmail,
        string oldUuid,
        string oldSubId,
        string newEmail,
        string newUuid,
        string newSubId,
        string newSubLink)
    {
        var builder = new StringBuilder();
        builder.AppendLine("✅ لینک اکانت با موفقیت تغییر کرد.");
        builder.AppendLine();
        builder.AppendLine("📌 اطلاعات قبلی");
        builder.AppendLine("👤 نام قبلی:");
        builder.AppendLine($"<code>{Html(oldEmail)}</code>");
        builder.AppendLine("🆔 UUID قبلی:");
        builder.AppendLine($"<code>{Html(oldUuid)}</code>");
        builder.AppendLine("🔖 Subscription ID قبلی:");
        builder.AppendLine($"<code>{Html(oldSubId)}</code>");
        builder.AppendLine();
        builder.AppendLine("✅ اطلاعات جدید");
        builder.AppendLine("👤 نام جدید:");
        builder.AppendLine($"<code>{Html(newEmail)}</code>");
        builder.AppendLine("🆔 UUID جدید:");
        builder.AppendLine($"<code>{Html(newUuid)}</code>");
        builder.AppendLine("🔖 Subscription ID جدید:");
        builder.AppendLine($"<code>{Html(newSubId)}</code>");
        builder.AppendLine();
        builder.AppendLine("🔗 سابلینک جدید:");
        builder.AppendLine($"<code>{Html(newSubLink)}</code>");
        builder.AppendLine();
        builder.AppendLine("از این لحظه لینک قبلی را استفاده نکنید.");
        return builder.ToString();
    }

    private static string BuildChangeLinkLogMessage(
        CredUser actor,
        XuiV3Client client,
        string oldEmail,
        string oldUuid,
        string oldSubId,
        string oldSubLink,
        string newEmail,
        string newUuid,
        string newSubId,
        string newSubLink,
        ServerInfo serverInfo)
    {
        var metadata = TryReadMetadata(client.Comment);
        var actorFullName = string.Join(" ", new[] { actor?.FirstName, actor?.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));
        var actorUsername = string.IsNullOrWhiteSpace(actor?.Username)
            ? "ندارد"
            : "@" + actor.Username.Trim().TrimStart('@');
        var actorRole = actor?.IsColleague == true ? "همکار" : "کاربر عادی";
        var ownerTelegramUserId = GetClientOwnerTelegramId(client);
        var totalBytes = GetTotalBytes(client);
        var usedBytes = GetUsedBytes(client);
        var trafficText = totalBytes > 0
            ? $"{usedBytes.ConvertBytesToGB():0.##} از {totalBytes.ConvertBytesToGB():0.##} GB"
            : $"{usedBytes.ConvertBytesToGB():0.##} از نامحدود";
        var userComment = CompactLogText(metadata?.UserComment, 120);
        var now = DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi();

        var builder = new StringBuilder();
        builder.AppendLine("🔁 <b>گزارش تغییر لینک اکانت نسخه ۳</b>");
        builder.AppendLine();
        builder.AppendLine($"👤 انجام‌دهنده: <code>{Html(string.IsNullOrWhiteSpace(actorFullName) ? "ثبت نشده" : actorFullName)}</code>");
        builder.AppendLine($"🔹 یوزرنیم: <code>{Html(actorUsername)}</code>");
        builder.AppendLine($"🆔 آیدی عددی انجام‌دهنده: <code>{actor?.TelegramUserId ?? 0}</code>");
        builder.AppendLine($"👥 نوع کاربر: <code>{Html(actorRole)}</code>");
        builder.AppendLine($"📌 مالک اکانت روی پنل: <code>{ownerTelegramUserId}</code>");
        builder.AppendLine($"🕒 زمان تغییر: <code>{Html(now)}</code>");
        builder.AppendLine();
        builder.AppendLine("قبل از تغییر:");
        builder.AppendLine($"• Email: <code>{Html(oldEmail)}</code>");
        builder.AppendLine($"• UUID: <code>{Html(oldUuid)}</code>");
        builder.AppendLine($"• Subscription ID: <code>{Html(oldSubId)}</code>");
        builder.AppendLine($"• Sub Link: <code>{Html(oldSubLink)}</code>");
        builder.AppendLine();
        builder.AppendLine("بعد از تغییر:");
        builder.AppendLine($"• Email: <code>{Html(newEmail)}</code>");
        builder.AppendLine($"• UUID: <code>{Html(newUuid)}</code>");
        builder.AppendLine($"• Subscription ID: <code>{Html(newSubId)}</code>");
        builder.AppendLine($"• Sub Link: <code>{Html(newSubLink)}</code>");
        builder.AppendLine();
        builder.AppendLine("مشخصات اکانت در لحظه تغییر:");
        builder.AppendLine($"• سرویس: <code>{Html(metadata?.ServiceName ?? "نامشخص")}</code>");
        builder.AppendLine($"• مصرف: <code>{Html(trafficText)}</code>");
        builder.AppendLine($"• انقضا: <code>{Html(FormatExpiry(GetExpiryTime(client)))}</code>");
        builder.AppendLine($"• وضعیت: <code>{(client.Enable ? "فعال" : "غیرفعال")}</code>");
        if (!string.IsNullOrWhiteSpace(userComment))
            builder.AppendLine($"• کامنت کاربر: <code>{Html(userComment)}</code>");
        builder.AppendLine($"• پنل: <code>{Html(serverInfo.Url)}</code>");
        builder.AppendLine($"• Root Path: <code>{Html(serverInfo.RootPath)}</code>");
        return builder.ToString();
    }

    private static long GetClientOwnerTelegramId(XuiV3Client client)
    {
        if (client != null && client.TgId > 0)
            return client.TgId;

        return TryReadMetadata(client?.Comment)?.TelegramUserId ?? 0;
    }

    private static string CompactLogText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized.Substring(0, maxLength) + "...";
    }

    private static string NormalizeUserComment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 200
            ? normalized
            : normalized.Substring(0, 200);
    }

    private static string BuildV3ClientInfo(XuiV3Client client, ServerInfo serverInfo, bool isColleague, bool showRenewCommand = true)
    {
        var email = Html(client.Email);
        var usedBytes = GetUsedBytes(client);
        var totalBytes = GetTotalBytes(client);
        var expiryTime = GetExpiryTime(client);
        var subId = string.IsNullOrWhiteSpace(client.SubId) ? client.Email : client.SubId;
        var subLink = ApiServicev3.BuildSubscriptionLink(serverInfo, subId);
        var metadata = TryReadMetadata(client.Comment);

        var text = new System.Text.StringBuilder();
        text.AppendLine($"👤 نام: <code>{email}</code>");
        text.AppendLine(FormatV3Expiry(expiryTime));
        text.AppendLine(FormatV3Traffic(usedBytes, totalBytes));
        if (!string.IsNullOrWhiteSpace(metadata?.UserComment))
            text.AppendLine($"📝 کامنت: <code>{Html(metadata.UserComment)}</code>");

        if (client.Enable)
        {
            text.AppendLine("✔️ فعال");
        }
        else
        {
            text.AppendLine("🚫 غیرفعال");
        }

        if (!isColleague && totalBytes > 0 && usedBytes >= totalBytes && !IsExpired(expiryTime))
        {
            text.AppendLine("❗️مولتی آیپی");
        }

        if (showRenewCommand)
            text.AppendLine($"🔄 تمدید ⬅️ /renew_{email}");
        text.AppendLine("🔗 سابلینک:");
        text.AppendLine($"<code>{Html(subLink)}</code>");
        text.AppendLine("___________________________");
        return text.ToString();
    }

    private static InlineKeyboardMarkup BuildV3AccountKeyboard(XuiV3Client client, bool isColleague)
    {
        if (client == null || client.Id <= 0)
            return null;

        var actionText = client.Enable ? "🚫 غیرفعال کردن" : "✅ فعال کردن";
        var callbackData = client.Enable
            ? XuiV3PurchaseCallbacks.AccountState(client.Id, false)
            : XuiV3PurchaseCallbacks.AccountState(client.Id, true);

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(actionText, callbackData)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("بازگشت به منوی اصلی", XuiV3PurchaseCallbacks.Home())
            }
        });
    }

    private async Task HandleAccountStateCallbackAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        CredUser credUser,
        int? clientId,
        bool enable,
        CancellationToken cancellationToken)
    {
        if (clientId == null || clientId <= 0)
        {
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "شناسه اکانت معتبر نیست.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var serverInfo = BuildConfiguredPanelServerInfo();
            Console.WriteLine($"[XUIv3] account state callback user={credUser.TelegramUserId}, clientId={clientId}, enable={enable}, panel={serverInfo.Url}, rootPath={serverInfo.RootPath}");

            var clientsResponse = await ApiServicev3.GetClientsAsync(serverInfo, _configuration, cancellationToken);
            if (!clientsResponse.Success)
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"دریافت اطلاعات اکانت ناموفق بود.\n{clientsResponse.Msg}",
                    cancellationToken: cancellationToken);
                return;
            }

            var client = clientsResponse.Obj?.FirstOrDefault(c => c.Id == clientId.Value);
            if (client == null || !ClientBelongsToUser(client, credUser.TelegramUserId))
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "اکانت مورد نظر پیدا نشد یا متعلق به حساب شما نیست.",
                    cancellationToken: cancellationToken);
                return;
            }

            var updateResponse = await ApiServicev3.SetClientEnabledAsync(
                serverInfo,
                _configuration,
                client.Email,
                enable,
                credUser.TelegramUserId,
                cancellationToken);
            if (!updateResponse.Success)
            {
                await _activityLog.LogWarningAsync(
                    "xui_v3_account_state_callback_failed",
                    credUser,
                    false,
                    new Dictionary<string, object>
                    {
                        ["clientId"] = clientId,
                        ["accountEmail"] = client.Email,
                        ["requestedAction"] = enable ? "enable" : "disable",
                        ["message"] = updateResponse.Msg ?? string.Empty
                    },
                    cancellationToken);

                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"متاسفانه عملیات مورد نظر انجام نشد.\n{updateResponse.Msg}",
                    cancellationToken: cancellationToken);
                return;
            }

            await _activityLog.LogBotActionAsync(
                enable ? "xui_v3_account_enabled" : "xui_v3_account_disabled",
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["clientId"] = clientId,
                    ["accountEmail"] = client.Email,
                    ["panelUrl"] = serverInfo.Url,
                    ["rootPath"] = serverInfo.RootPath,
                    ["source"] = "account_callback"
                },
                cancellationToken);

            client.Enable = enable;
            var updatedText = BuildV3ClientInfo(client, serverInfo, credUser.IsColleague);
            var updatedKeyboard = BuildV3AccountKeyboard(client, credUser.IsColleague);

            if (messageId != 0)
            {
                await SafeEditMessageTextAsync(
                    botClient,
                    chatId: chatId,
                    messageId: messageId,
                    text: updatedText,
                    parseMode: ParseMode.Html,
                    replyMarkup: updatedKeyboard,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: updatedText,
                    parseMode: ParseMode.Html,
                    replyMarkup: updatedKeyboard,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XUIv3] account state callback exception user={credUser.TelegramUserId}, clientId={clientId}, enable={enable}: {ex}");
            await _activityLog.LogErrorAsync(
                "xui_v3_account_state_callback_exception",
                ex,
                credUser,
                false,
                new Dictionary<string, object>
                {
                    ["clientId"] = clientId,
                    ["requestedAction"] = enable ? "enable" : "disable"
                },
                cancellationToken);
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "در انجام عملیات خطا رخ داد. جزئیات در ترمینال ثبت شد.",
                cancellationToken: cancellationToken);
        }
    }

    private static string FormatV3Expiry(long expiryTime)
    {
        if (expiryTime <= 0)
            return "📅 انقضا: نامحدود";

        var expiryUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiryTime).UtcDateTime;
        var expiryText = expiryUtc.AddMinutes(210).ConvertToHijriShamsi();

        if (expiryUtc < DateTime.UtcNow)
            return $"📅 انقضا: {Html(expiryText)}\n🚫 منقضی شده است.";

        var remainingDays = (expiryUtc - DateTime.UtcNow).Days;
        var icon = remainingDays <= 5 ? "❕⌛️" : "⏳";
        return $"📅 انقضا: {Html(expiryText)}\n{icon} روزهای باقی‌مانده: {remainingDays} روز";
    }

    private static string FormatV3Traffic(long usedBytes, long totalBytes)
    {
        var usedGb = usedBytes.ConvertBytesToGB();
        if (totalBytes <= 0)
            return $"🔋 میزان مصرف: {usedGb:F2} گیگابایت از نامحدود";

        var totalGb = totalBytes.ConvertBytesToGB();
        var icon = usedBytes < totalBytes * 0.9 ? "🔋" : "🪫";
        return $"{icon} میزان مصرف: {usedGb:F2} از {totalGb:F2} گیگابایت";
    }

    private static string FormatGb(long bytes, bool zeroAsUnknown = true)
    {
        if (bytes < 0 || bytes == 0 && zeroAsUnknown)
            return "نامحدود / نامشخص";

        return $"{bytes.ConvertBytesToGB():0.##} GB";
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

    private static ReplyKeyboardMarkup BuildDeleteExpiredConfirmKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("تایید حذف اکانت های منقضی") },
            new[] { new KeyboardButton("انصراف") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
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
        if (expiryTime <= 0)
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

    private static long GetUsedBytes(XuiV3Client client)
    {
        var traffic = client.Traffic;
        return (traffic?.Up ?? ReadLongExtra(client, "up")) +
               (traffic?.Down ?? ReadLongExtra(client, "down"));
    }

    private static long GetTotalBytes(XuiV3Client client)
    {
        if (client.TotalGB > 0)
            return client.TotalGB;

        if (client.Traffic?.TotalGB > 0)
            return client.Traffic.TotalGB;

        if (client.Traffic?.Total > 0)
            return client.Traffic.Total;

        return ReadLongExtra(client, "totalGB");
    }

    private static long GetExpiryTime(XuiV3Client client)
    {
        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        if (client.Traffic?.ExpiryTime != 0)
            return client.Traffic.ExpiryTime;

        return ReadLongExtra(client, "expiryTime");
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

    private ServerInfo BuildConfiguredPanelServerInfo()
    {
        var baseUrl = _appConfig.XuiV3ApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("XuiV3ApiBaseUrl is not configured.");

        return new ServerInfo
        {
            ApiVersion = "v3",
            ApiToken = _appConfig.XuiV3ApiToken,
            Url = baseUrl.TrimEnd('/'),
            RootPath = (_appConfig.XuiV3ApiRootPath ?? string.Empty).Trim('/'),
            SubLinkUrl = string.IsNullOrWhiteSpace(_appConfig.XuiV3SubLinkBaseUrl)
                ? null
                : _appConfig.XuiV3SubLinkBaseUrl.TrimEnd('/'),
            Name = "Configured V3 Panel"
        };
    }
}
