using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Exercises real owned updates and checks that the private super-admin switch governs entry and purchase without erasing verified numbers.</summary>
    /// <returns>A task completing after persistence, denied edits, live admission, stale purchase input and contact handling are verified.</returns>
    /// <remarks>Uses isolated SQLite and a recording Telegram transport. The same unverified user is tested before and after each toggle.</remarks>
    [Fact]
    public async Task Owned_phone_verification_controls_entry_and_purchase_and_preserves_tenant_policy()
    {
        const long customerId = 7000123;
        const string action = "📱 الزام تأیید شماره تلفن";
        using var databases = new Databases();
        var configurationDirectory = Path.Combine(databases.DirectoryPath, "Data");
        Directory.CreateDirectory(configurationDirectory);
        var configurationPath = Path.Combine(configurationDirectory, "configuration.json");
        await File.WriteAllTextAsync(configurationPath, "{\"supportLabel\":\"💬 پشتیبانی\"}");
        await using var provider = StorefrontProvider(databases, configureServices: services =>
        {
            var original = (IConfiguration)services.Last(x => x.ServiceType == typeof(IConfiguration)).ImplementationInstance!;
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddConfiguration(original)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminsUserIds:0"] = GatewaySuperAdminId.ToString(),
                    ["XuiApiVersionMode"] = "v3"
                }).Build());
        });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
        var settings = provider.GetRequiredService<PhoneVerificationSettings>();
        var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
        var client = new GatewayTelegramClient();
        var context = new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-phone-test", Type = BotInstanceTypes.Owned },
            Client = client
        };

        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(customerId, "/start"), context, default);
        Assert.Contains("برای ورود به ربات", Assert.Single(client.Sends).Text);
        client.Sends.Clear();
        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(customerId, "عضو شدم!"), context, default);
        Assert.Contains("برای ورود به ربات", Assert.Single(client.Sends).Text);

        client.Sends.Clear();
        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(GatewaySuperAdminId, action), context, default);
        var panel = Assert.Single(client.Sends);
        Assert.Contains("✅ روشن", panel.Text);
        var disable = Assert.Single(Assert.Single(Assert.IsType<InlineKeyboardMarkup>(panel.ReplyMarkup).InlineKeyboard)).CallbackData;
        var denied = GatewayCallback("refresh", 0, 0, customerId);
        denied.Data = disable;
        await service.DispatchUpdateAsync(client, new Update { CallbackQuery = denied }, context, default);
        Assert.True(settings.Snapshot.Enabled);
        Assert.True(Assert.Single(client.Answers).ShowAlert);

        var accepted = GatewayCallback("refresh", 0, 0);
        accepted.Data = disable;
        await service.DispatchUpdateAsync(client, new Update { CallbackQuery = accepted }, context, default);
        Assert.False(settings.IsRequiredFor(BotInstanceTypes.Owned));
        Assert.True(settings.IsRequiredFor(BotInstanceTypes.Tenant));
        var stored = new ConfigurationBuilder().AddJsonFile(configurationPath).Build();
        Assert.False(new PhoneVerificationSettings(stored.Get<AppConfig>()!, configurationPath).Snapshot.Enabled);
        Assert.Equal("💬 پشتیبانی", stored["supportLabel"]);

        client.Sends.Clear();
        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(customerId, "/start"), context, default);
        Assert.Contains("به ربات خوش آمدید!", Assert.Single(client.Sends).Text);
        client.Sends.Clear();
        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(customerId, "💳خرید اکانت جدید"), context, default);
        Assert.Contains(client.Sends, send => send.Text.Contains("نوع سرویس را انتخاب کنید"));
        Assert.True(string.IsNullOrWhiteSpace((await credentials.GetUserStatusWithId(customerId)).PhoneNumber));

        var enable = GatewayCallback("refresh", 0, 0);
        enable.Data = $"phoneverify:{settings.Snapshot.Revision:N}:1";
        await service.DispatchUpdateAsync(client, new Update { CallbackQuery = enable }, context, default);
        await service.DispatchUpdateAsync(client, new Update { CallbackQuery = accepted }, context, default);
        Assert.True(settings.Snapshot.Enabled);

        // A user who started buying while it was off must see the current requirement on their next purchase input.
        client.Sends.Clear();
        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(customerId, "[normal]"), context, default);
        Assert.Contains("ابتدا باید شماره تلفن خودتان را تأیید کنید", Assert.Single(client.Sends).Text);

        client.Sends.Clear();
        var foreignContact = PhoneVerificationMessage(customerId, null);
        foreignContact.Message!.Contact = new Contact { UserId = customerId, FirstName = "Customer", PhoneNumber = "+12025550123" };
        await service.DispatchUpdateAsync(client, foreignContact, context, default);
        var retry = Assert.IsType<ReplyKeyboardMarkup>(Assert.Single(client.Sends).ReplyMarkup);
        Assert.Contains(retry.Keyboard.SelectMany(row => row), button => button.RequestContact == true);

        client.Sends.Clear();
        var contact = PhoneVerificationMessage(customerId, null);
        contact.Message!.Contact = new Contact { UserId = customerId, FirstName = "Customer", PhoneNumber = "09123456789" };
        await service.DispatchUpdateAsync(client, contact, context, default);
        Assert.Contains("شماره شما با موفقیت تایید شد", Assert.Single(client.Sends).Text);

        client.Sends.Clear();
        await service.DispatchUpdateAsync(client, PhoneVerificationMessage(customerId, "/start"), context, default);
        Assert.Contains("به ربات خوش آمدید!", Assert.Single(client.Sends).Text);
        Assert.Equal("+989123456789", (await credentials.GetUserStatusWithId(customerId)).PhoneNumber);
    }

    /// <summary>Builds an isolated private Telegram update for the owned-bot verification scenario.</summary>
    /// <param name="senderId">Synthetic global Telegram user id that also identifies the private chat.</param>
    /// <param name="text">Optional message text. A contact test sets the contact after construction.</param>
    /// <returns>A Telegram update for the production dispatcher.</returns>
    /// <example><code>var update = PhoneVerificationMessage(7000123, "/start");</code></example>
    private static Update PhoneVerificationMessage(long senderId, string? text) => new()
    {
        Message = new Message
        {
            Id = 1,
            Date = DateTime.UtcNow,
            From = new Telegram.Bot.Types.User { Id = senderId, FirstName = "Customer" },
            Chat = new Chat { Id = senderId, Type = ChatType.Private },
            Text = text
        }
    };
}
