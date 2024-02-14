using System.Text.RegularExpressions;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography.X509Certificates;

public class TelegramBotService : IHostedService
{
    private readonly ITelegramBotClient _botClient;
    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(ITelegramBotClient botClient, UserDbContext dbContext, CredentialsDbContext credentialsDb, IConfiguration configuration, ILogger<TelegramBotService> logger)
    {
        _botClient = botClient;
        _userDbContext = dbContext;
        _credentialsDbContext = credentialsDb;
        _configuration = configuration;
        _appConfig = _configuration.Get<AppConfig>();
        _logger = logger;
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

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // Only process Message updates: https://core.telegram.org/bots/api#message
        if (update.Message is not { } message)
            return;
        // Only process text messages
        if (message.Text is not { } messageText)
            return;

        var chatId = message.Chat.Id;

        Console.WriteLine($"Received a '{messageText}' message in chat {chatId}.");


        //6257546736 amir
        //85758085 hamed
        // 888197418 admin hamed

        //        List<long> allowedValues = _configuration.GetSection("adminsUserIds").Get<List<long>>();
        List<long> allowedValues = _appConfig.AdminsUserIds;
        if (!allowedValues.Contains(message.From.Id))
        {
            // _logger.LogInformation("این یک یوزر عادی است.");
            await HandleUpdateRegularUsers(botClient, update, cancellationToken);
            return;
        }
        var currentUser = await _userDbContext.GetUserStatus(message.From.Id);
        //_userDbContext.Users.Attach(currentUser);

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

            var msg = $"✅ Account details: \n";
            msg += $"Active: {client.Enable}";
            msg += $"\n Account Name: \n `{client.Email}` \n";

            msg += client.TotalUsedTrafficInGB;
            string hijriShamsiDate = client.ExpiryTime.ConvertToHijriShamsi();
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
                    string hijriShamsiDate = client.ExpiryTime.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).ConvertToHijriShamsi();
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

            await botClient.CustomSendTextMessageAsync(
            chatId: message.Chat.Id,
            text: currentUser.ConfigLink,
            replyMarkup: GetMessageSendConfirmationKeyboard());

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
                                                    replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);


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
                        _credentialsDbContext.Users.Update(findedUser);
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
                        _credentialsDbContext.Users.Update(findedUser);
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
                    await _credentialsDbContext.SaveUserStatus(findedUser);


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
                    await _credentialsDbContext.SaveUserStatus(findedUser);

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

                foreach (var item in _credentialsDbContext.Users)
                {
                    // Console.WriteLine($"{item.ChatID}")
                    await botClient.CustomSendTextMessageAsync(
                                                chatId: item.ChatID,
                                                text: currentUser.ConfigLink,
                                                parseMode: ParseMode.Markdown,
                                                replyMarkup: inlineKeyboard
                                                );

                }

                await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
                await botClient.CustomSendTextMessageAsync(
                                           chatId: message.Chat.Id,
                                           text: "Done! Main menu",
                                            replyMarkup: GetMainMenuKeyboard());

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
        // Extract the information from the message
        long telegramUserId = message.From.Id;
        long chatId = message.Chat.Id;
        string username = message.From.Username ?? "";
        string firstName = message.From.FirstName;
        string lastName = message.From.LastName ?? "";
        return new CredUser { TelegramUserId = telegramUserId, ChatID = chatId, IsColleague = false, FirstName = firstName, LastName = lastName, Username = username };
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
        string[] actions = new string[] { "➕ Add credit", "➖ Reduce credit", "🚀 Promote as admin", "❌ Demote as admin", "ℹ️ See User Account", "📨 Send message to all", "📑 Menu" };
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

    private string CaptionForAccountCreation(User user, string language, bool showTraffic)
    {
        string msg;
        if (language == "en")
        {
            msg = $"✅ Account details: \n";
            msg += $"Account Name: `{user.Email}`";
            msg += $"\nLocation: {user.SelectedCountry} \nDuration: {user.SelectedPeriod}";
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
            msg += $"\n📅تاریخ انقضاء:  {hijriShamsiDate}\n";

            // msg += "✳️ آموزش کانفیگ لینک" + $"**آی‌اواس** [link text]({_appConfig.ConfiglinkTutorial[0]})" + " | " + $"**اندروید** [link text]({_appConfig.ConfiglinkTutorial[1]}) \n";
            // msg += "✴️ آموزش سابلینک (برای تعویض اتوماتیک و فیلترینگ شدید)" + $"**آی‌اواس** [link text]({_appConfig.SubLinkTotorial[0]})" + " | " + $"**اندروید** [link text]({_appConfig.SubLinkTotorial[1]}) \n";
            msg += $"🔗 ساب لینک: \n `{user.SubLink}`\n";


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
        // Only process text messages
        if (message.Text is not { } messageText)
            return;

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
        }
        if (message.Text == "عضو شدم!")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "به ربات خوش آمدید!",
                replyMarkup: MainReplyMarkupKeyboardFa());

        }
        else if (message.Text == "💻 ارتباط با ادمین")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            var text = "✅ برای ارتباط با پشتیبانی از لینک زیر اقدام کنید." + "\n" + "🆔 @vpnetiran_admin";

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text,
                replyMarkup: MainReplyMarkupKeyboardFa());

            // Save the user's context
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

        }

        else if (message.Text == "🏠منو")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "منوی اصلی",
                replyMarkup: MainReplyMarkupKeyboardFa());
        }

        else if (message.Text == "🌟اکانت رایگان")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
            return;
        }

        else if (message.Text == "💰شارژ حساب کاربری")
        {
            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

            var text = "درحال حاضر شارژ حساب فقط از طریق ادمین امکان پذیر می‌باشد.برای شارژ حساب خود به ادمین پیام دهید و پیام زیر را برای ایشان فوروارد کنید: /n @vpsnetiran_vpn /n به زودی پرداخت ریالی و ترونی به ربات اضافه خواهد شد.";
            await botClient.CustomSendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: text,
                            replyMarkup: new ReplyKeyboardRemove());

            text = await GetUserProfileMessage(credUser);
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text,
                replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

            await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

        }

        else if (message.Text == "💳خرید اکانت جدید")
        {
            var replyKeboard = PriceReplyMarkupKeyboardFa(credUser.IsColleague);

            await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Create New Account", Flow = "create" });

            await botClient.CustomSendTextMessageAsync(
               chatId: message.Chat.Id,
               text: "شرایط اکانت ها به شرح زیر میباشد:",
               replyMarkup: replyKeboard);

        }

        else if (message.Text == "⚙️مدیریت اکانت")
        {
            var text = await GetUserProfileMessage(credUser);
            await botClient.CustomSendTextMessageAsync(
                chatId: message.Chat.Id,
                text: text,
                replyMarkup: MainReplyMarkupKeyboardFa(), parseMode: ParseMode.Markdown);

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

            if (CheckButtonCorrectness(credUser.IsColleague, message.Text) == false)
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

                await PrepareAccount(message.Text, credUser, user);
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
        }

        else if (user.Flow == "create" && user.LastStep == "ask_confirmation" && (message.Text == "تایید نهایی" || message.Text == "انصراف"))
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

                AccountDto accountDto = new AccountDto { TelegramUserId = message.From.Id, ServerInfo = serverInfo, SelectedCountry = user.SelectedCountry, SelectedPeriod = user.SelectedPeriod, AccType = user.Type, TotoalGB = user.TotoalGB };

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
                    await _credentialsDbContext.Pay(credUser, Convert.ToInt64(user._ConfigPrice));
                    await _userDbContext.ClearUserStatus(new User { Id = user.Id });

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
    private async Task PrepareAccount(string messageText, CredUser credUser, User user)
    {

        var priceConfig = GetPriceConfig(messageText, credUser);
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

    private bool CheckButtonCorrectness(bool isColleague, string text)
    {
        return GetPrices(isColleague).Contains(text);
    }

    Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(ErrorMessage);
        return Task.CompletedTask;
    }

    async Task<string> GetUserProfileMessage(CredUser credUser)
    {
        var _credUser = await _credentialsDbContext.GetUserStatus(credUser);

        var text = "✅ مشخصات اکانت شما به شرح زیر میباشد:  \n";
        text += $"👤نام حساب: {_credUser.FirstName} {_credUser.LastName} \n";
        if (!string.IsNullOrEmpty(_credUser.Username))
            text += $"\u200F🆔 آیدی: @{_credUser.Username} \n";
        text += $"\u200Fℹ️ آیدی عددی: `{_credUser.TelegramUserId}` \n";
        text += $"‌💰اعتبار حساب: {_credUser.AccountBalance} تومان \n";
        if (_credUser.IsColleague)
        {
            text += $"‌🧰 نوع: اکانت شما از نوع همکار 💎می‌باشد. \n";
        }
        else
        {
            text += "‌🧰 نوع: اکانت شما از نوع کاربر عادی می‌باشد. \n";
        }
        return text;
    }
    string[] GetPrices(bool isColleague)
    {

        List<string> buttonsName = new List<string>();

        if (isColleague)
        {
            _appConfig.PriceColleagues.ForEach(i => buttonsName.Add($"خرید اکانت {i.DurationName} قیمت {i.Price}"));


            //     return new string[]{ "خرید اکانت یک ماهه قیمت 60000",
            // "خرید اکانت  دو ماهه قیمت 120000",
            // "خرید اکانت سه ماهه قیمت 180000",
            // "خرید اکانت شش ماهه قیمت 360000" };
        }
        else
        {
            _appConfig.Price.ForEach(i => buttonsName.Add($"خرید اکانت {i.DurationName} قیمت {i.Price}"));

            //     return new string[]{ "خرید اکانت یک ماهه قیمت 149000",
            // "خرید اکانت  دو ماهه قیمت 259000",
            // "خرید اکانت سه ماهه قیمت 345000",
            // "خرید اکانت شش ماهه قیمت 649000" };
        }
        return buttonsName.ToArray();
    }

    PriceConfig GetPriceConfig(string messageText, CredUser credUser)
    {
        var appConfig = _configuration.Get<AppConfig>();

        PriceConfig priceConfig;
        var prices = GetPrices(credUser.IsColleague);
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
                    new KeyboardButton[] { "💡راهنما نصب", "🌟اکانت رایگان","⚙️مدیریت اکانت" }})
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


    ReplyKeyboardMarkup PriceReplyMarkupKeyboardFa(bool isColleague)
    {

        var prices = GetPrices(isColleague);
        ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
               {
                    new KeyboardButton[] { prices[0], prices[1] },
                    new KeyboardButton[] { prices[2],prices[3] },
                    new KeyboardButton[] { "🏠منو" }})
        {
            ResizeKeyboard = false
        };
        return replyKeyboardMarkup;
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


}

