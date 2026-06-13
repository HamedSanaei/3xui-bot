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
    private const string SkipCommentText = "ادامه بدون کامنت";

    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly NowPayments _nowPayments;
    private readonly NowPaymentsSettlementService _settlementService;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly ILogger<XuiV3AdminFlowService> _logger;
    private readonly UserActivityLogService _activityLog;

    public XuiV3AdminFlowService(
        UserDbContext userDbContext,
        CredentialsDbContext credentialsDbContext,
        IConfiguration configuration,
        NowPayments nowPayments,
        NowPaymentsSettlementService settlementService,
        XuiV3PurchaseService purchaseService,
        ILogger<XuiV3AdminFlowService> logger,
        UserActivityLogService activityLog)
    {
        _userDbContext = userDbContext;
        _credentialsDbContext = credentialsDbContext;
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _nowPayments = nowPayments;
        _settlementService = settlementService;
        _purchaseService = purchaseService;
        _logger = logger;
        _activityLog = activityLog;
    }

    public bool IsEnabled()
    {
        return string.Equals(_appConfig.XuiApiVersionMode, "v3", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> TryHandleMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        User currentUser,
        IReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled() || message?.Text == null)
            return false;

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
                text: "شناسه NOWPayments را ارسال کنید.\nاگر `Order ID` بفرستید، پرداخت به صورت دستی تایید و تسویه می‌شود.\nاگر `Payment ID` یا `Invoice ID` بفرستید، فقط وضعیت از NOWPayments بررسی می‌شود:",
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
            text: BuildRenewSummary(refreshedUser),
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
        var currentExpiryBeforeRenew = GetExpiryTime(client);
        var updatedClient = BuildRenewPayload(client, addTrafficGb, addDays, message.From.Id);
        Console.WriteLine(
            $"[XUIv3] admin renew payload actor={message.From.Id}, email={client.Email}, durationDays={addDays}, currentExpiry={currentExpiryBeforeRenew}, newExpiry={updatedClient.ExpiryTime}, currentExpiryText={FormatExpiry(currentExpiryBeforeRenew)}, newExpiryText={FormatExpiry(updatedClient.ExpiryTime)}");

        var updateResponse = await ApiServicev3.UpdateClientAsync(serverInfo, _configuration, client.Email, updatedClient, cancellationToken);
        if (!updateResponse.Success)
        {
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, $"تمدید ناموفق بود.\n{updateResponse.Msg}", cancellationToken);
            return;
        }

        client.TotalGB = updatedClient.TotalGB;
        client.ExpiryTime = updatedClient.ExpiryTime;
        client.Comment = updatedClient.Comment;
        client.TgId = updatedClient.TgId;

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
                ["durationAddedDays"] = addDays,
                ["totalGbAfterRenew"] = GetTotalBytes(client).ConvertBytesToGB(),
                ["usedGb"] = GetUsedBytes(client).ConvertBytesToGB(),
                ["expiryShamsi"] = FormatExpiry(client.ExpiryTime),
                ["subLink"] = ApiServicev3.BuildSubscriptionLink(serverInfo, client.SubId ?? client.Email),
                ["panelUrl"] = serverInfo.Url,
                ["rootPath"] = serverInfo.RootPath,
                ["comment"] = client.Comment
            },
            cancellationToken);

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

        foreach (var client in matches)
        {
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: BuildClientInfo(client, serverInfo),
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }

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
            await FinishWithMessageAsync(botClient, message.Chat.Id, currentUser, mainMenu, "پرداخت NOWPayments با این شناسه پیدا نشد.", cancellationToken);
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

        return "✅ خلاصه ساخت اکانت نسخه ۳\n\n" +
               $"👤 مالک اکانت: <code>{Html(user.ConfigLink)}</code>\n" +
               $"🧩 نوع سرویس: <code>{Html(resolved.Service.DisplayName)}</code>\n" +
               $"📌 پلن: <code>{Html(planText)}</code>\n" +
               $"📦 حجم هر اکانت: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(resolved.TrafficBytes, resolved.TrafficGb))}</code>\n" +
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
            cancellationToken);

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
        var targetFlowUser = await _userDbContext.GetUserStatus(targetTelegramUserId);

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
                NextAccountCounter = targetFlowUser.AccountCounter + 1,
                SaveUserStatus = true
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
        sb.AppendLine($"حجم هر اکانت `{XuiV3PurchaseService.FormatTrafficSize(result.TrafficBytes, result.TrafficGb)}`");
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
        var text = "✅ اکانت نسخه ۳ با موفقیت ساخته شد.\n\n" +
               $"👤 نام اکانت: <code>{Html(creation.Email)}</code>\n" +
               $"📦 حجم: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(creation.TrafficBytes, creation.TrafficGb))}</code>\n" +
               $"⏳ انقضا: <code>{Html(expiryText)}</code>\n" +
               $"🔗 Inbound IDs: <code>{Html(string.Join(",", creation.InboundIds ?? new List<int>()))}</code>\n\n" +
               "🔗 سابلینک:\n" +
               $"<code>{Html(creation.SubLink)}</code>";

        if (!string.IsNullOrWhiteSpace(metadata?.UserComment))
            text += $"\n\n📝 کامنت:\n<code>{Html(metadata.UserComment)}</code>";

        return text;
    }

    private static string BuildRenewSummary(User user)
    {
        var trafficGb = int.TryParse(user.TotoalGB, out var parsedTraffic) ? parsedTraffic : 0;
        var days = int.TryParse(user.SelectedPeriod, out var parsedDays) ? parsedDays : 0;
        var durationText = days <= 0 ? "نامحدود / لایف‌تایم" : $"{days} روز";

        return "✅ خلاصه تمدید اکانت نسخه ۳\n\n" +
               $"👤 اکانت: <code>{Html(user.ConfigLink)}</code>\n" +
               $"📦 حجم اضافه: <code>{trafficGb} GB</code>\n" +
               $"⏳ زمان اضافه: <code>{Html(durationText)}</code>\n\n" +
               "مالک اکانت از روی خود پنل حفظ می‌شود و به آیدی سوپرادمین تغییر نمی‌کند.";
    }

    private static XuiV3ClientPayload BuildRenewPayload(
        XuiV3Client client,
        int addTrafficGb,
        int addDays,
        long actorTelegramUserId)
    {
        var currentTotalBytes = GetTotalBytes(client);
        var updatedTotalBytes = currentTotalBytes + ApiService.ConvertGBToBytes(addTrafficGb);
        var currentExpiryTime = GetExpiryTime(client);
        var updatedExpiryTime = CalculateRenewedExpiryTime(currentExpiryTime, addDays);

        var metadata = TryReadMetadata(client.Comment) ?? new XuiV3ClientMetadata
        {
            TelegramUserId = client.TgId,
            ServiceKey = "unknown",
            ServiceName = "unknown"
        };

        metadata.TelegramUserId = client.TgId;
        metadata.LastUpdatedByTelegramUserId = actorTelegramUserId;
        metadata.LastAction = "admin-renew";
        metadata.LastRenewedAtUtc = DateTime.UtcNow;
        metadata.TrafficGb = Convert.ToInt32(Math.Max(0, updatedTotalBytes).ConvertBytesToGB());
        if (addDays <= 0)
            metadata.DurationDays = 0;
        else
            metadata.DurationDays += addDays;
        metadata.Renewals ??= new List<XuiV3ClientRenewalRecord>();
        metadata.Renewals.Add(new XuiV3ClientRenewalRecord
        {
            ActorTelegramUserId = actorTelegramUserId,
            AddedTrafficGb = addTrafficGb,
            AddedDurationDays = addDays,
            TotalBytesAfter = updatedTotalBytes,
            ExpiryTimeAfter = updatedExpiryTime
        });

        var payload = CopyClientPayload(client);
        payload.TotalGB = updatedTotalBytes;
        payload.ExpiryTime = updatedExpiryTime;
        payload.TgId = client.TgId;
        payload.Enable = true;
        payload.Comment = JsonConvert.SerializeObject(metadata, Formatting.None);
        return payload;
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

    private static long GetUsedBytes(XuiV3Client client)
    {
        return (client.Traffic?.Up ?? ReadLongExtra(client, "up")) +
               (client.Traffic?.Down ?? ReadLongExtra(client, "down"));
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
            text += "\n🧾 متادیتا:\n" +
                    $"نوع سرویس: <code>{Html(metadata.ServiceKey)}</code>\n" +
                    $"نام سرویس: <code>{Html(metadata.ServiceName)}</code>\n" +
                    $"پلن: <code>{Html(metadata.PlanKey)}</code>\n" +
                    $"حجم: <code>{Html(XuiV3PurchaseService.FormatTrafficSize(metadata.TrafficBytes, metadata.TrafficGb))}</code>\n" +
                    $"مدت: <code>{metadata.DurationDays} days</code>\n" +
                    $"قیمت: <code>{Html(metadata.PriceToman.FormatCurrency())}</code>\n" +
                    $"اینباندها: <code>{Html(string.Join(",", metadata.InboundIds ?? new List<int>()))}</code>\n";

            if (!string.IsNullOrWhiteSpace(metadata.UserComment))
                text += $"کامنت کاربر: <code>{Html(metadata.UserComment)}</code>\n";
        }

        return text;
    }

    private static string BuildPaymentInfo(
        SwapinoPaymentInfo payment,
        NowPaymentsPaymentRecordData data,
        NowPaymentsSettlementResult settlement)
    {
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
               $"Invoice URL: <code>{Html(payment.InvoiceUrl ?? data.InvoiceUrl)}</code>";
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

    private static string FormatTraffic(XuiV3Client client)
    {
        var usedBytes = (client.Traffic?.Up ?? ReadLongExtra(client, "up")) +
                        (client.Traffic?.Down ?? ReadLongExtra(client, "down"));
        var totalBytes = client.TotalGB > 0 ? client.TotalGB : client.Traffic?.TotalGB ?? client.Traffic?.Total ?? 0;
        if (totalBytes <= 0)
            return $"{usedBytes.ConvertBytesToGB():F2} GB / unlimited";

        return $"{usedBytes.ConvertBytesToGB():F2} GB / {totalBytes.ConvertBytesToGB():F2} GB";
    }

    private static long GetExpiryTime(XuiV3Client client)
    {
        if (client.ExpiryTime != 0)
            return client.ExpiryTime;

        if (client.Traffic?.ExpiryTime != 0)
            return client.Traffic.ExpiryTime;

        return ReadLongExtra(client, "expiryTime");
    }

    private static string FormatExpiry(long expiryTime)
    {
        if (expiryTime <= 0)
            return "نامحدود";

        return DateTimeOffset.FromUnixTimeMilliseconds(expiryTime)
            .UtcDateTime
            .AddMinutes(210)
            .ConvertToHijriShamsi();
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
