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

public class TelegramBotService : IHostedService
{
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
    private readonly XuiV3BotFlowService _xuiV3BotFlowService;
    private readonly XuiV3AdminFlowService _xuiV3AdminFlowService;
    private readonly UserActivityLogService _userActivityLog;

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
        XuiV3BotFlowService xuiV3BotFlowService,
        XuiV3AdminFlowService xuiV3AdminFlowService,
        UserActivityLogService userActivityLog)
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
        _xuiV3BotFlowService = xuiV3BotFlowService;
        _xuiV3AdminFlowService = xuiV3AdminFlowService;
        _userActivityLog = userActivityLog;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {

        var me = await _botClient.GetMeAsync();
        Console.WriteLine($"Start listening for @{me.Username}");


        // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
        ReceiverOptions receiverOptions = new()
        {
            AllowedUpdates = Array.Empty<UpdateType>() // receive all update types except ChatMember related updates
        };

        // PeriodicTaskRunner._credentialsDbContext = _credentialsDbContext;
        // PeriodicTaskRunner.Start();

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cancellationToken
        );

        // Start your bot logic here
        //_botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Add your cleanup code here
        return Task.CompletedTask;
    }

    private long MinimumChargeAmountToman =>
        _appConfig?.MinimumWalletChargeAmountToman > 0
            ? _appConfig.MinimumWalletChargeAmountToman
            : 100_000L;

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            await HandleUpdateCoreAsync(botClient, update, cancellationToken);
        }
        catch (Exception ex)
        {
            var credUser = GetCreduserFromUpdate(update);
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

    private async Task HandleUpdateCoreAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {

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

            if (callbackQuery.Data != null && callbackQuery.Data.StartsWith("x3:"))
            {
                var user = await _userDbContext.GetUserStatus(callbackQuery.From.Id);
                var callbackMainKeyboard = callbackIsSuperAdmin
                    ? GetMainMenuKeyboard()
                    : MainReplyMarkupKeyboardFa();
                if (await _xuiV3BotFlowService.TryHandleCallbackAsync(_botClient, callbackQuery, callbackCredUser, user, callbackMainKeyboard, cancellationToken))
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

        if (isSuperAdmin && await TryHandleUserActivityLogCommandAsync(botClient, message, messageCredUser, cancellationToken))
            return;

        if (await TryHandleNowPaymentsSuccessStartAsync(
            botClient,
            message,
            messageCredUser,
            isSuperAdmin ? GetMainMenuKeyboard() : MainReplyMarkupKeyboardFa(),
            cancellationToken))
        {
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

        if (await _xuiV3AdminFlowService.TryHandleMessageAsync(
            botClient,
            message,
            currentUser,
            GetMainMenuKeyboard(),
            cancellationToken))
        {
            return;
        }

        // await _botClient.ForwardMessageAsync(
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

            await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "This is Your message. Are  you Sure to send it to all of your users?",
                            replyMarkup: GetMessageSendConfirmationKeyboard());

            var forwardMessage = GetChannelAndPost(message.Text);
            if (forwardMessage != null)
            {
                await _botClient.CustomForwardMessage(
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
                        findedUser.AccountBalance += amount;
                        await _credentialsDbContext.SaveChangesAsync();



                        await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);


                        await botClient.CustomSendTextMessageAsync(
                                                    chatId: (findedUser).ChatID,
                                                    text: $"حساب شما به میزان {amount} تومان از طرف مدیریت شارژ شد.",
                                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

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
                        findedUser.AccountBalance -= amount;
                        await _credentialsDbContext.SaveChangesAsync();


                        await botClient.CustomSendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: await GetUserProfileMessage(findedUser),
                        replyMarkup: GetMainMenuKeyboard(), parseMode: ParseMode.Markdown);

                        await botClient.CustomSendTextMessageAsync(
                                                    chatId: findedUser.ChatID,
                                                    text: $"از حساب شما به میزان {amount} تومان از طرف مدیریت کسر شد.",
                                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

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
                    findedUser.IsColleague = true;
                    await _credentialsDbContext.PromotOrDemote(findedUser.TelegramUserId, true);


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
                    findedUser.IsColleague = false;
                    await _credentialsDbContext.PromotOrDemote(findedUser.TelegramUserId, false);

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
        else if (message.Text == "Yes Send!" && currentUser.Flow == "admin" && currentUser.LastStep == "confirm-public-message")
        {
            var channelPost = GetChannelAndPost(currentUser.ConfigLink);

            InlineKeyboardMarkup inlineKeyboard = new(new[]
              {
                 // first row
                        new []
                {
                    InlineKeyboardButton.WithUrl(text:"ارتباط با پشتیبانی",url:_appConfig.SupportAccount),
                    InlineKeyboardButton.WithUrl(text:"کانال ما",url:_appConfig.MainChannel),
                },});
            if (message.Text == "Yes Send!")
            {
                var allUsers = _credentialsDbContext.Users
                    .Select(x => x.ChatID > 0 ? x.ChatID : x.TelegramUserId)
                    .Where(chatId => chatId > 0)
                    .Distinct()
                    .ToList();

                if (allUsers.Count == 0)
                {
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        "هیچ کاربری برای ارسال پیام عمومی پیدا نشد.",
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
                //             await _botClient.CustomForwardMessage(
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
                await botClient.CustomSendTextMessageAsync(
                                                                chatId: message.From.Id,
                                                                text: currentUser.ConfigLink,
                                                                parseMode: ParseMode.Markdown,
                                                                replyMarkup: GetMessageSendConfirmationKeyboard()
                                                                );
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
                currentUser.LastStep = "Get-public-message";
                await _userDbContext.SaveUserStatus(currentUser);

                await botClient.CustomSendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "Type your message and Send it:",
                                replyMarkup: new ReplyKeyboardRemove());
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

            await _botClient.CustomSendTextMessageAsync(
                chatId: callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
                text: "بررسی پرداخت با خطا روبه‌رو شد. جزئیات خطا در لاگ ثبت شد؛ لطفاً کمی بعد دوباره تلاش کنید.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
        }
    }

    private async Task ProcessBroadcastStatusCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var jobId = callbackQuery.Data.Replace("broadcast_status_", "");
        var job = _broadcastManager.GetJob(jobId);
        if (job == null)
        {
            await _botClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                text: "وضعیت این ارسال پیدا نشد.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        if (job.RequestedByTelegramUserId != callbackQuery.From.Id && !IsSuperAdminUser(callbackQuery.From.Id))
        {
            await _botClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                text: "فقط ادمین شروع‌کننده ارسال می‌تواند این وضعیت را بروزرسانی کند.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        await _broadcastManager.RefreshStatusMessageAsync(jobId, cancellationToken);
        await _botClient.AnswerCallbackQueryAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت زیبال مورد نظر در دیتابیس پیدا نشد.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (zpi.IsAddedToBallance)
        {
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار مربوط به این پرداخت قبلاً به حساب کاربری شما افزوده شده است.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            await EditMessageWithCallback(_botClient, zpi.ChatId, Convert.ToInt32(zpi.TelMsgId));
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var inquiry = await ZibalAPI.Inquiry(zpi.TrackId, _appConfig.ZibalMerchantCode);
        if (inquiry == null)
        {
            await _botClient.CustomSendTextMessageAsync(
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

            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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

        await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است و دوباره شارژ نمی‌شود.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);

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
                    await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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

            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildCryptoManualCheckSettlementText(payment, data, settlement),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);
        }
        else if (NowPaymentsStatuses.IsPartiallyPaid(data.PaymentStatus))
        {
            var partialAmountToman = CalculatePartialCryptoChargeToman(payment, data);
            var text = BuildPartialCryptoPaymentText(payment, data, partialAmountToman);
            var replyMarkup = BuildPartialCryptoPaymentKeyboard(payment);
            Console.WriteLine($"[NOWPayments ManualCheck] partial payment. orderId={payment.OrderId}, paymentId={payment.PaymentId}, partialAmountToman={partialAmountToman}");

            await _botClient.CustomSendTextMessageAsync(
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

            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت مورد نظر پیدا نشد.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (payment.IsAddedToBalance)
        {
            Console.WriteLine($"[HooshPay ManualCheck] already added. orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, user={payment.TelegramUserId}");
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است و دوباره شارژ نمی‌شود.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(payment.InvoiceUid))
        {
            await _botClient.CustomSendTextMessageAsync(
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

            await _botClient.CustomSendTextMessageAsync(
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
            var settlement = await _hooshPaySettlementService.ApplyFinishedPaymentAsync(
                payment,
                "manual-check",
                chatId,
                cancellationToken);

            Console.WriteLine($"[HooshPay ManualCheck] settlement result. orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, settlement={settlement.Status}, before={settlement.BeforeBalance}, after={settlement.AfterBalance}");

            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildHooshPayManualCheckSettlementText(payment, settlement),
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);
        }
        else
        {
            var text = $"وضعیت فعلی پرداخت: <code>{Html(payment.PaymentStatus ?? "unknown")}</code>\n" +
                       "بعد از تایید پرداخت، موجودی شما از طریق IPN یا بررسی دستی شارژ می‌شود.";

            if (HooshPayStatuses.IsFinalFailure(payment.PaymentStatus))
                text = $"این پرداخت با وضعیت <code>{Html(payment.PaymentStatus)}</code> بسته شده و قابل شارژ نیست.";

            await _botClient.CustomSendTextMessageAsync(
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

    private async Task<bool> TryHandleNowPaymentsSuccessStartAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser credUser,
        IReplyMarkup mainReplyMarkup,
        CancellationToken cancellationToken)
    {
        if (message?.Text == null || !IsNowPaymentsSuccessStart(message.Text))
            return false;

        var telegramUserId = message.From?.Id ?? credUser?.TelegramUserId ?? 0;
        Console.WriteLine($"[NOWPayments SuccessUrl] received success start. user={telegramUserId}, chat={message.Chat.Id}, text={message.Text}");

        var payment = await _userDbContext.SwapinoPaymentInfos
            .Where(p => p.TelegramUserId == telegramUserId && !p.IsAddedToBalance)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (payment == null)
        {
            Console.WriteLine($"[NOWPayments SuccessUrl] no pending crypto payment found. user={telegramUserId}");
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "پرداخت در حال انتظاری برای حساب شما پیدا نشد. اگر کیف پول قبلاً شارژ شده، نیازی به اقدام دوباره نیست.",
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);
            return true;
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "بازگشت از درگاه پرداخت دریافت شد. در حال بررسی وضعیت پرداخت از NOWPayments هستم...",
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
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است و دوباره شارژ نمی‌شود.",
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);

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
                    await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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

            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: BuildCryptoManualCheckSettlementText(payment, data, settlement),
                parseMode: ParseMode.Html,
                replyMarkup: mainReplyMarkup,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);
        }
        else if (NowPaymentsStatuses.IsPartiallyPaid(data.PaymentStatus))
        {
            var partialAmountToman = CalculatePartialCryptoChargeToman(payment, data);
            var text = BuildPartialCryptoPaymentText(payment, data, partialAmountToman);
            var replyMarkup = BuildPartialCryptoPaymentKeyboard(payment);
            Console.WriteLine($"[NOWPayments ManualCheck] partial payment. source={source}, orderId={payment.OrderId}, paymentId={payment.PaymentId}, partialAmountToman={partialAmountToman}");

            await _botClient.CustomSendTextMessageAsync(
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

            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "پرداخت مورد نظر پیدا نشد.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (payment.IsAddedToBalance)
        {
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: "اعتبار این پرداخت قبلاً به کیف پول شما اضافه شده است.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        var data = payment.GetNowPaymentsData();
        if (string.IsNullOrWhiteSpace(payment.PaymentId ?? data.PaymentId))
        {
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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
                await EditMessageWithCallback(_botClient, chatId, messageId);

            await AnswerCallbackSafely(callbackQuery, cancellationToken);
            return;
        }

        if (!NowPaymentsStatuses.IsPartiallyPaid(data.PaymentStatus))
        {
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.CustomSendTextMessageAsync(
                chatId: chatId,
                text: $"پرداخت ناقص به اندازه <code>{partialAmountToman.FormatCurrency()}</code> به کیف پول شما اضافه شد.",
                parseMode: ParseMode.Html,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);

            if (messageId != 0)
                await EditMessageWithCallback(_botClient, chatId, messageId);
        }
        else
        {
            await _botClient.CustomSendTextMessageAsync(
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
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
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

    public async Task ZibalAddtoBalance(ZibalPaymentInfo zpi, AppConfig appConfig, CredUser credUser, long chatid, bool isAdmin)
    {
        if (zpi.IsAddedToBallance == true) return;

        var findedUser = await _credentialsDbContext.GetUserStatusWithId(zpi.TelegramUserId);
        long beforeBalance = credUser.AccountBalance;
        await _credentialsDbContext.AddFund(zpi.TelegramUserId, zpi.Amount / 10);
        zpi.IsAddedToBallance = true;


        if (isAdmin)
        {
            beforeBalance = findedUser.AccountBalance;
            //notify user
            await _botClient.CustomSendTextMessageAsync(
              chatId: findedUser.ChatID,
              text: $"اعتبار کیف پول شما به میزان {(zpi.Amount / 10).FormatCurrency()} افزایش یافت. با اسفتاده از این اعتبار میتوانید اکانت مورد نیاز خودرا تهیه بفرمایید.",
              replyMarkup: MainReplyMarkupKeyboardFa());
        }

        await _userDbContext.SaveChangesAsync();
        await _credentialsDbContext.SaveChangesAsync();

        long afterBalance = await _credentialsDbContext.GetAccountBalance(credUser.TelegramUserId);
        if (isAdmin)
        {
            afterBalance = await _credentialsDbContext.GetAccountBalance(findedUser.TelegramUserId);
        }

        //notify user ( admin)
        await _botClient.CustomSendTextMessageAsync(
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
        await EditMessageWithCallback(_botClient, zpi.ChatId, Convert.ToInt32(zpi.TelMsgId));

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
                new KeyboardButton("No Don't Send!"),
            },

        });
        return confirmationKeyboard;
    }
    private List<string> GetLocations()
    {

        var serversJson = ReadJsonFile.ReadJsonAsString();
        var servers = JsonConvert.DeserializeObject<Dictionary<string, ServerInfo>>(serversJson);
        return servers.Keys.ToList();


    }
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
            "✔️ Verify payment",
            "📑 Menu"
        };
        return actions;
    }

    private ReplyKeyboardMarkup GetAdminKeyboard()
    {
        var actions = GetAdminActions();

        // Creating keyboard buttons dynamically in two-column layout
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

        // Creating the keyboard markup
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

        var isJoined = await isJoinedToChannel(_appConfig.ChannelIds.Select(c => c.Replace("https://t.me/", "@")), message.From.Id);
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
            string toRemove = "https://t.me/";
            List<InlineKeyboardButton[]> rows = new List<InlineKeyboardButton[]>();
            foreach (var url in _appConfig.ChannelIds)
            {
                rows.Add(new[] { InlineKeyboardButton.WithUrl(url.Replace(toRemove, ""), url) });
            }

            InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows.ToArray());

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "به کانال(های) زیر بپیوندید و روی استارت کلیک کنید. \n" + "/start",
                replyMarkup: inlineKeyboard);

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "پس از عضویت روی دکمه زیر کلیک کنید.",
                replyMarkup: replyKeyboardMarkup);
            return;
        }



        if (message.Text == "/start")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id,
               text: "به ربات خوش آمدید!",
               replyMarkup: MainReplyMarkupKeyboardFa());
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

            var text = "✅ برای ارتباط با پشتیبانی از لینک زیر اقدام کنید." + "\n" + @"🆔 @vpnetiran\_admin";

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text, parseMode: ParseMode.Markdown,
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
                user.ConfigPrice = _appConfig.TrafficPriceShop;
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
                List<InlineKeyboardButton[]> rows = _appConfig.IosTutorial.Select((url, index) => new InlineKeyboardButton[]
                    {
                        InlineKeyboardButton.WithUrl(GetTutorialButtonText(index), url)
                    }).ToList();

                // Create the InlineKeyboardMarkup
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows);

                await _botClient.CustomSendTextMessageAsync(chatId: message.Chat.Id,
                     text: "برای دریافت آموزش روی دکمه زیر کلیک کنید.",
                     replyMarkup: inlineKeyboard);


                // foreach (var item in _appConfig.IosTutorial)
                // {
                // var forwardMessage = GetChannelAndPost(item);
                // await _botClient.CustomForwardMessage(chatId: message.Chat.Id,
                // fromChatId: forwardMessage.ChannelName,
                // messageId: forwardMessage.PostNumber);


                // }
            }
            else if (message.Text == "راهنمای اندروید 📱")
            {
                List<InlineKeyboardButton[]> rows = _appConfig.AndroidTutorial.Select((url, index) => new InlineKeyboardButton[]
                    {
                        InlineKeyboardButton.WithUrl(GetTutorialButtonText(index), url)
                    }).ToList();

                // Create the InlineKeyboardMarkup
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows);

                await _botClient.CustomSendTextMessageAsync(chatId: message.Chat.Id,
                     text: "برای دریافت آموزش روی دکمه زیر کلیک کنید.",
                     replyMarkup: inlineKeyboard);

                // foreach (var item in _appConfig.AndroidTutorial)
                // {
                //     var forwardMessage = GetChannelAndPost(item);
                //     await _botClient.CustomForwardMessage(chatId: message.Chat.Id,
                //     fromChatId: forwardMessage.ChannelName,
                //     messageId: forwardMessage.PostNumber);
                // }
            }
            else if (message.Text == "راهنمای ویندوز 💻")
            {

                List<InlineKeyboardButton[]> rows = _appConfig.WindowsTutorial.Select((url, index) => new InlineKeyboardButton[]
                    {
                        InlineKeyboardButton.WithUrl(GetTutorialButtonText(index), url)
                    }).ToList();

                // Create the InlineKeyboardMarkup
                InlineKeyboardMarkup inlineKeyboard = new InlineKeyboardMarkup(rows);

                await _botClient.CustomSendTextMessageAsync(chatId: message.Chat.Id,
                     text: "برای دریافت آموزش روی دکمه زیر کلیک کنید.",
                     replyMarkup: inlineKeyboard);

                // foreach (var item in _appConfig.WindowsTutorial)
                // {
                //     var forwardMessage = GetChannelAndPost(item);
                //     await _botClient.CustomForwardMessage(chatId: message.Chat.Id,
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
                new KeyboardButton[] { "حذف اکانت های منقضی" },
            };

            if (credUser?.IsColleague != true)
                accountManagementRows.Add(new KeyboardButton[] { "🤝 درخواست همکاری" });

            accountManagementRows.Add(new KeyboardButton[] { "💰شارژ حساب کاربری" });
            accountManagementRows.Add(new KeyboardButton[] { "📌 قابلیت‌های ربات" });
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
            var allPrices = _appConfig.Price.Union(_appConfig.PriceCommon).Union(_appConfig.PriceColleagues);
            foreach (var priceConfig in allPrices)
            {

                var buttonText = $"{Convert.ToInt64(priceConfig.Price).FormatCurrency()}";
                keyboardButtons.Add(new List<KeyboardButton> { new KeyboardButton(buttonText) });
            }


            // Add a "Back" button at the end
            keyboardButtons.Add(new List<KeyboardButton> { new KeyboardButton("بازگشت") });

            var keyboard = new ReplyKeyboardMarkup(keyboardButtons)
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };


            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "enter charge amount", Flow = "charge" });
            var minimumChargeAmount = MinimumChargeAmountToman;
            var msg = $"لطفاً میزان شارژ اکانت خود را انتخاب یا به تومان وارد کنید. به عنوان مثال {minimumChargeAmount} معادل {minimumChargeAmount.FormatCurrency()} است.\nحداقل میزان شارژ {minimumChargeAmount.FormatCurrency()} است.";
            //msg = "برای شارژ حساب کاربری به آیدی زیر پیام دهید: \n @vpnetiran_admin";
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: msg.EscapeMarkdown(),
                replyMarkup: keyboard, parseMode: ParseMode.Markdown);


        }

        else if (user.LastStep == "enter charge amount" && user.Flow == "charge")
        {
            // Usage
            bool canConvert = message.Text.PersianNumbersToEnglish().ToValidNumber().TryConvertToLong(out long longValue);
            if (canConvert)
            {
                var minimumChargeAmount = MinimumChargeAmountToman;
                if (longValue < minimumChargeAmount)
                {
                    await botClient.CustomSendTextMessageAsync(
                                        chatId: message.Chat.Id,
                                        text: $" شما مقدار {longValue.FormatCurrency()} را برای شارژ حساب خود وارد کرده اید. \n" + $" ❕ حداقل میزان شارژ {minimumChargeAmount.FormatCurrency()} است\n" + "\n" + "مبلغ مد نظر خود را مجدد وارد کنید",
                                        replyMarkup: new ReplyKeyboardRemove());
                    return;
                }
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
            await FinalizeRenewCustomerAccount(_botClient, user, credUser, message);

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
            await FinalizeCustomerAccount(_botClient, user, credUser, message);
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

                var logMesseage = "تمدید \n" + $"یوزر `{credUser.TelegramUserId}` \n {credUser} \n با مبلغ {user._ConfigPrice}" + " اکانت زیر را خریداری کرد" + $"\n موجودی قبل از خرید {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از خرید {afterBalance.FormatCurrency()}" + " \n \n" + msg;

                if (user.ConfigPrice > 1000) _logger.LogInformation(logMesseage.EscapeMarkdown());

                if (user.SelectedPeriod == "1 Day")
                {
                    user.LastFreeAcc = DateTime.Now;
                    _userDbContext.Users.Update(user);
                    await _userDbContext.SaveChangesAsync();
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

                var logMesseage = $"یوزر `{credUser.TelegramUserId}` \n {credUser} \n با مبلغ {user._ConfigPrice}" + " اکانت زیر را خریداری کرد" + $"\n موجودی قبل از خرید {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از خرید {afterBalance.FormatCurrency()}" + " \n \n" + msg;

                if (user.ConfigPrice > 1000) _logger.LogInformation(logMesseage.EscapeMarkdown());

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
    private async Task<bool> isJoinedToChannel(IEnumerable<string> channelIDs, long userId)
    {
        bool isJoined = true;

        foreach (var c in channelIDs)
        {

            var chatMember = await _botClient.GetChatMemberAsync(c, userId);
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

        return isJoined;

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
                await _botClient.CustomSendTextMessageAsync(chatId, messageBuilder.ToString().EscapeMarkdown(), parseMode: ParseMode.Markdown);
                messageBuilder.Clear(); // Clear the builder for the next message
            }

            // Add the current client's info to the message builder
            messageBuilder.Append(clientInfo);
        }

        // Send any remaining info
        if (messageBuilder.Length > 0)
        {
            await _botClient.SendTextMessageAsync(chatId, messageBuilder.ToString().EscapeMarkdown(), parseMode: ParseMode.Markdown);
        }
    }

    async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

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

        Console.WriteLine(ErrorMessage);
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
            text += $"‌🧰 نوع: اکانت شما از نوع همکار 💎می‌باشد. \n";
        }
        else
        {
            text += "‌🧰 نوع: اکانت شما از نوع کاربر عادی می‌باشد. \n";
        }
        return text.EscapeMarkdown();
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
        builder.AppendLine("انتخاب سرویس، حجم دلخواه، مدت زمان، تعداد اکانت و ثبت کامنت اختصاصی.");
        builder.AppendLine();
        builder.AppendLine("👤 <b>مدیریت اکانت‌ها</b>");
        builder.AppendLine("مشاهده همه اکانت‌ها، صفحه‌بندی، جستجو، دیدن جزئیات، تمدید، حذف تکی و تغییر لینک.");
        builder.AppendLine();
        builder.AppendLine("🔎 <b>جستجوی سریع</b>");
        builder.AppendLine("جستجو با نام اکانت، بخشی از کامنت یا UUID کامل کانفیگ.");
        builder.AppendLine();
        builder.AppendLine("🔁 <b>تمدید و تغییر لینک</b>");
        builder.AppendLine("تمدید با حجم دلخواه و ساخت لینک جدید در صورت لو رفتن اطلاعات اکانت.");
        builder.AppendLine();
        builder.AppendLine("🧹 <b>حذف اکانت‌های منقضی</b>");
        builder.AppendLine("نمایش و حذف یکجای اکانت‌هایی که حجم یا زمان آن‌ها تمام شده است.");
        builder.AppendLine();
        builder.AppendLine("💰 <b>شارژ حساب</b>");
        builder.AppendLine("شارژ کیف پول از مسیرهای پرداخت فعال ربات.");
        builder.AppendLine();
        builder.AppendLine("🌟 <b>اکانت تست رایگان</b>");
        builder.AppendLine("دریافت تست دوره‌ای برای بررسی کیفیت سرویس‌ها، در صورت داشتن شرایط.");

        if (credUser?.IsColleague == true)
        {
            builder.AppendLine();
            builder.AppendLine("💎 <b>امکانات همکاران</b>");
            builder.AppendLine("قیمت همکار، ساخت چند اکانت در یک سفارش و دسترسی سریع به اکانت‌ها با شماره اکانت.");
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
            _appConfig.HooshPayReturnUrl,
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
                return_url = _appConfig.HooshPayReturnUrl
            });

            Console.WriteLine($"[HooshPay] Creating invoice for user={credUser.TelegramUserId}, amount={amount}, orderId={payment.OrderId}, feeMode={HooshPayFeeModes.Buyer}");

            var invoice = await _hooshPay.CreateInvoiceAsync(
                amount,
                payment.OrderId,
                $"Wallet charge {payment.OrderId}",
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
                latestMsg = await _botClient.SendPhotoAsync(
                    message.Chat.Id,
                    InputFile.FromStream(qrStream),
                    caption: msg,
                    parseMode: ParseMode.Html,
                    replyMarkup: inlineKeyboardMarkup,
                    cancellationToken: cancellationToken);
            }
            else
            {
                latestMsg = await _botClient.CustomSendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: msg,
                    replyMarkup: inlineKeyboardMarkup,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }

            await _botClient.CustomSendTextMessageAsync(
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

            payment.ErrorCode = ex.StatusCode.ToString(CultureInfo.InvariantCulture);
            payment.ErrorMessage = ex.Message;
            payment.RawResponseJson = ex.ResponseBody;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await _botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "HooshPay درخواست را رد کرده است. جزئیات خطا در ترمینال ثبت شد.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[HooshPay] Unexpected exception while creating invoice:");
            Console.WriteLine(ex.ToString());

            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await _botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "ایجاد پرداخت ریالی HooshPay ناموفق بود. جزئیات خطا در ترمینال ثبت شد.",
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
        }
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
    ReplyKeyboardMarkup MainReplyMarkupKeyboardFa()
    {

        ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
               {
                    new KeyboardButton[] { "💳خرید اکانت جدید", "🏠منو","💻 ارتباط با ادمین" },
                    new KeyboardButton[] { "💡راهنما نصب", "🌟اکانت رایگان","⚙️ مدیریت اکانت" }})
        {
            ResizeKeyboard = false
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
                    new KeyboardButton[] { "🏠منو" ,"تمدید حجمی" }})
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
                new KeyboardButton("📑 Menu"), new KeyboardButton("🗽 Admin"),
            }
        });

        return keyboard;
    }

    private async Task SendBlockedUserMessageAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var support = string.IsNullOrWhiteSpace(_appConfig.SupportAccount)
            ? "@v2raysshvpn_admin"
            : _appConfig.SupportAccount.Trim();

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $"به علت تخلف مسدود شدید و امکان استفاده از ربات را ندارید.\nبرای پیگیری می‌توانید به پشتیبانی تلگرام پیام بدهید:\n<code>{System.Net.WebUtility.HtmlEncode(support)}</code>",
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


    async Task<bool> CreateAccount(AccountDto accountDto)
    {
        bool result;
        var sessionCookie = await ApiService.LoginAndGetSessionCookie(accountDto.ServerInfo);
        if (sessionCookie != null)
        {
            accountDto.SessionCookie = sessionCookie;
            // var selectedCountry = "🇸🇪 Sweden";
            // var selectedPeriod = "2 Months";

            result = await ApiService.CreateUserAccount(accountDto);
        }
        else
        {
            // Handle the case where login fails
            result = false;
        }
        return result;
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
                await _botClient.SendTextMessageAsync(chatId: chatId,
                                                   text: "خطا. لطفاً شماره اکانت خودتان با شماره واقعی را وارد کنید.",
                                                   replyMarkup: MainReplyMarkupKeyboardFa());
                return false;
            }


        }
        else
        {
            await _botClient.SendTextMessageAsync(chatId: chatId,
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
