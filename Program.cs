using Adminbot.Domain;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Adminbot.Utils;

//var bot1 = "6019665082:AAGBDkTknaoRvTV8wmpS3xOits3XCcwufqU";
var bot2 = "6034372537:AAH_iAh1rLrosds9wGqtq-cdUG7yp4um60c";
var botClient = new TelegramBotClient(bot2);
UserDbContext _userDbContext = new UserDbContext();

using CancellationTokenSource cts = new();

// StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
ReceiverOptions receiverOptions = new()
{
    AllowedUpdates = Array.Empty<UpdateType>() // receive all update types except ChatMember related updates
};

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    pollingErrorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

var me = await botClient.GetMeAsync();

Console.WriteLine($"Start listening for @{me.Username}");
Console.ReadLine();

// Send cancellation request to stop bot
cts.Cancel();

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
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

    long[] allowedValues = { 6257546736, 85758085, 888197418 };
    if (!allowedValues.Contains(message.From.Id))
    {
        return;
    }

    if (message.Text == "/start")
    {
        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Main Menu:",
            replyMarkup: GetMainMenuKeyboard());
        await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
    }
    else if (message.Text == "➕ Create New Account")
    {
        var createAccountKeyboard = new ReplyKeyboardMarkup(new[]
        {
            new []
            {
                new KeyboardButton("🇩🇪 Germany"),
            },
            new []
            {
                new KeyboardButton("🇸🇪 Sweden"),
            },
            new []
            {
                new KeyboardButton("🇧🇬 Bulgaria"),
            },
        });

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Select your country:",
            replyMarkup: createAccountKeyboard);

        // Save the user's context (selected country)
        await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Create New Account", Flow = "create" });

    }

    else if (message.Text == "🇩🇪 Germany" || message.Text == "🇸🇪 Sweden" || message.Text == "🇧🇬 Bulgaria")
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

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Select the account period:",
            replyMarkup: periodKeyboard);
    }

    else if (message.Text == "1 Month" || message.Text == "2 Months" || message.Text == "3 Months" || message.Text == "6 Months")
    {
        // Handle the selected period
        await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, SelectedPeriod = message.Text });

        // user does not go throw the actual flow
        var user = await _userDbContext.GetUserStatus(message.From.Id);
        if (string.IsNullOrEmpty(user.SelectedCountry))
        {
            await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Main Menu:",
            replyMarkup: GetMainMenuKeyboard());
            return;
        }


        // Create a keyboard based on the selected period
        var keyboard = GetAccountTypeKeyboard();

        // Send the keyboard to the user
        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Select the account type:",
            replyMarkup: keyboard
        );


    }
    else if (message.Text == "Reality Ipv6")
    {
        await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, Type = "realityv6", TotoalGB = "500" });

        var user = await _userDbContext.GetUserStatus(message.From.Id);
        if (string.IsNullOrEmpty(user.SelectedCountry) || string.IsNullOrEmpty(user.SelectedPeriod))
        {
            await botClient.SendTextMessageAsync(
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

        user = await _userDbContext.GetUserStatus(message.From.Id);

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: $"You selected {user.SelectedCountry} for {user.SelectedPeriod} with account type {user.Type}. Confirm?",
            replyMarkup: confirmationKeyboard);

    }
    else if (message.Text == "All operators")
    {
        await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, Type = "tunnel" });

        var user = await _userDbContext.GetUserStatus(message.From.Id);
        if (string.IsNullOrEmpty(user.SelectedCountry) || string.IsNullOrEmpty(user.SelectedPeriod))
        {
            await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: "Main Menu:",
            replyMarkup: GetMainMenuKeyboard());
            return;
        }



        await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Type Traffic in GB and send! \n" + "For example if you send 20, the account will have 20GB traffic",
                    replyMarkup: new ReplyKeyboardRemove());



    }

    else if (message.Text == "Yes Create!")
    {
        await botClient.SendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "Please wait ...",
                    replyMarkup: new ReplyKeyboardRemove());


        var ready = await _userDbContext.IsUserReadyToCreate(message.From.Id);
        if (!ready) await botClient.SendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "Your information is not complete. please go throw steps correctly.",
                    replyMarkup: GetMainMenuKeyboard()); ;
        if (!ready) return;

        // Handle confirmation (create the account or perform other actions)
        var user = await _userDbContext.GetUserStatus(message.From.Id);

        // Access the server information from the servers.json file
        var serversJson = System.IO.File.ReadAllText("servers.json");
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
                user = await _userDbContext.GetUserStatus(message.From.Id);

                var msg = $"✅ Account details: \n";
                msg += $"Account Name: `{user.Email}`";
                msg += $"\nLocation: {user.SelectedCountry} \nDuration: {user.SelectedPeriod}";
                if (Convert.ToInt32(user.TotoalGB) < 100) msg += $"\nTraffic: {user.TotoalGB}GB.\n";
                string hijriShamsiDate = DateTime.UtcNow.AddDays(ApiService.ConvertPeriodToDays(user.SelectedPeriod)).AddMinutes(210).ConvertToHijriShamsi();
                msg += $"\nExpiration Date: {hijriShamsiDate}\n";
                msg += $"Your Connection link is: \n";
                msg += "============= Tap to Copy =============\n";
                msg += $"`{user.ConfigLink}`" + "\n ";

                // Send the photo with caption

                await botClient.SendPhotoAsync(message.Chat.Id, InputFile.FromStream(new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(user.ConfigLink, 200))), caption: msg, parseMode: ParseMode.Markdown);
                // .GetAwaiter()
                // .GetResult();


                await botClient.SendTextMessageAsync(
                   chatId: message.Chat.Id,
                   text: "Main menu",
                    replyMarkup: GetMainMenuKeyboard());

                await _userDbContext.ClearUserStatus(new User { Id = user.Id });

            }
        }
        else
        {
            // Handle the case where the selected country is not found in the servers.json file
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"Server information not found for {user.SelectedCountry}.",
                replyMarkup: GetMainMenuKeyboard());
        }
    }
    else if (message.Text == "No Don't Create!")
    {
        await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
        // Handle rejection or provide other options
        await botClient.SendTextMessageAsync(
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
        await botClient.SendTextMessageAsync(
                           chatId: message.Chat.Id,
                           text: "Send your Vmess or Vless link:",
                            replyMarkup: new ReplyKeyboardRemove());

    }

    else if ((await _userDbContext.GetUserStatus(message.From.Id)).Flow == "read" && StartsWithVMessOrVLess(message.Text))
    {
        var user = await _userDbContext.GetUserStatus(message.From.Id);
        ClientExtend client = await TryGetClient(message.Text);

        // Handle "Get Account Info" button click
        // You can implement the logic for this button here
        // For example, retrieve and display account information


        if (client == null)
        {
            await botClient.SendTextMessageAsync(
                          chatId: message.Chat.Id,
                          text: "There is a Error with decoding Your config link!",
                           replyMarkup: new ReplyKeyboardRemove());
            return;
        }

        var msg = $"✅ Account details: \n";
        msg += $"Active: {client.Enable}";
        msg += $"\n Account Name: \n `{client.Email}` \n";

        msg += client.TotalUsedTrafficInGB;
        string hijriShamsiDate = client.ExpiryTime.ConvertToHijriShamsi();
        msg += $"\nExpiration Date: {hijriShamsiDate}\n";


        await botClient.SendTextMessageAsync(
           chatId: message.Chat.Id, parseMode: ParseMode.Markdown,
           text: msg,
            replyMarkup: GetMainMenuKeyboard());


    }
    else if ((await _userDbContext.GetUserStatus(message.From.Id)).Flow == "update" && StartsWithVMessOrVLess(message.Text))
    {
        var user = await _userDbContext.GetUserStatus(message.From.Id);
        user.ConfigLink = message.Text;
        await _userDbContext.SaveUserStatus(user);

    }


    else if (message.Text == "🔄 Renew Existing Account")
    {
        await _userDbContext.SaveUserStatus(new User { Id = message.From.Id, LastStep = "Renew Existing Account", Flow = "update" });
        await botClient.SendTextMessageAsync(
                           chatId: message.Chat.Id,
                           text: "Send your Vmess or Vless link:",
                            replyMarkup: new ReplyKeyboardRemove());
    }
    else if ((await _userDbContext.GetUserStatus(message.From.Id)).Flow == "update")
    {


    }
    else if (message.Text == "📑 Menu")
    {
        await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });

        // Handle "Menu" button click
        // You can implement the logic for this button here
        // For example, show a different menu or perform another action
    }
    else if (int.TryParse(message.Text, out int res))
    {


        if ((await _userDbContext.GetUserStatus(message.From.Id)).LastStep == "Renew Existing Account")
        {
            // bayad tamdid konim va tamam az ro config link.
            // aval fetch mikonim 
            return;
        }
        if ((await _userDbContext.GetUserStatus(message.From.Id)).LastStep == "Create New Account" && (await _userDbContext.GetUserStatus(message.From.Id)).Flow == "create")
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

                var user = await _userDbContext.GetUserStatus(message.From.Id);

                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"You selected {user.SelectedCountry} for {user.SelectedPeriod}Month(s) with account type {user.Type} and Traffic {userTraffic}GB. Confirm?",
                    replyMarkup: confirmationKeyboard);


                return;
            }



            // You can now use the 'userTraffic' value in your logic
            // For example, store it in a database, perform further actions, etc.
        }
        else
        {
            // The user did not enter a valid number
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Please enter a valid number."
            );
        }



    }
    else
    {
        await _userDbContext.ClearUserStatus(new User { Id = message.From.Id });
        await botClient.SendTextMessageAsync(
                                   chatId: message.Chat.Id,
                                   text: "Oops! Start Again",
                                    replyMarkup: GetMainMenuKeyboard());

    }
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
            new KeyboardButton("📑 Menu"),
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
        var serversJson = System.IO.File.ReadAllText("servers.json");
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
        var serversJson = System.IO.File.ReadAllText("servers.json");
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
            var serverInfo = GetConfigServer(vmess);
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
        var vless = Vless.DecodeVlessLink(messageText);
        var serverInfo = GetConfigServerFromVless(vless);
        var inbound = serverInfo.Inbounds.FirstOrDefault(i => i.Type == "realityv6");
        if (inbound == null) return null;
        client = await ApiService.FetchClientFromServer(vless.Id, serverInfo, inbound.Id);
    }
    return client;

}