using System.Reflection;
using Adminbot.Domain;
using Adminbot.Services.AppleMobileConfig;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;
using TelegramUser = Telegram.Bot.Types.User;

public sealed partial class ConcurrencyTests
{
    [Theory]
    [InlineData("IOSAPN:CARRIER:MCI", "mcinet")]
    [InlineData("IOSAPN:CARRIER:IRANCELL", "mtnirancell")]
    [InlineData("IOSAPN:CARRIER:RIGHTEL", "RighTel")]
    [InlineData("IOSAPN:CARRIER:SHATEL", "shatelmobile")]
    public async Task Apple_apn_carrier_selection_generates_dual_stack_without_protocol_question(
        string callbackData,
        string expectedApn)
    {
        using var databases = new Databases();
        var state = new global::UserStateStore(databases.Users);
        var client = new GatewayTelegramClient();
        var flow = BuildFlow(state);
        var accessor = new BotContextAccessor();
        var home = HomeKeyboard();

        using (accessor.Push(OwnedContext(client)))
        {
            Assert.True(await flow.TryHandleMessageAsync(
                client, Message(4242, AppleMobileConfigText.MenuCommand),
                new global::User { Id = 4242 }, home, CancellationToken.None));
            var menuState = await state.GetUserStatus(4242);
            Assert.Equal("menu", menuState.LastStep);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, callbackData),
                menuState, home, CancellationToken.None));

            var document = Assert.Single(client.Documents);
            var fileName = document.Document.GetType()
                .GetProperty("FileName", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(document.Document) as string;
            Assert.Equal("iphone-apn-ipv4-ipv6.mobileconfig", fileName);
            Assert.Contains(client.Sends, send =>
                send.Text?.Contains($"APN: {expectedApn}", StringComparison.Ordinal) == true &&
                send.Text.Contains("IPv4 + IPv6", StringComparison.Ordinal));
            Assert.DoesNotContain(client.Sends, send =>
                send.Text?.Contains("نوع IP را انتخاب کنید", StringComparison.Ordinal) == true);

            var cleared = await state.GetUserStatus(4242);
            Assert.True(string.IsNullOrEmpty(cleared.Flow));
            Assert.True(string.IsNullOrEmpty(cleared.LastStep));
        }
    }

    [Fact]
    public async Task Apple_apn_custom_value_generates_immediately_without_profile_name_or_protocol_step()
    {
        using var databases = new Databases();
        var state = new global::UserStateStore(databases.Users);
        var client = new GatewayTelegramClient();
        var flow = BuildFlow(state);
        var accessor = new BotContextAccessor();
        var home = HomeKeyboard();

        using (accessor.Push(OwnedContext(client)))
        {
            await flow.TryHandleMessageAsync(
                client, Message(4242, AppleMobileConfigText.MenuCommand),
                new global::User { Id = 4242 }, home, CancellationToken.None);
            var menuState = await state.GetUserStatus(4242);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:CUSTOM"),
                menuState, home, CancellationToken.None));
            var apnState = await state.GetUserStatus(4242);
            Assert.Equal("apn", apnState.LastStep);

            Assert.True(await flow.TryHandleMessageAsync(
                client, Message(4242, "custom.apn"), apnState, home, CancellationToken.None));
            Assert.Single(client.Documents);
            Assert.DoesNotContain(client.Sends, send =>
                send.Text?.Contains("نوع IP را انتخاب کنید", StringComparison.Ordinal) == true ||
                send.Text?.Contains("نام پروفایل را وارد کنید", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void Apple_apn_menu_lists_major_iranian_carriers_and_no_protocol_choices()
    {
        var labels = AppleMobileConfigTelegramFlow.BuildMenuKeyboard()
            .InlineKeyboard.SelectMany(row => row).Select(button => button.Text).ToArray();

        Assert.Contains(AppleMobileConfigText.MciButton, labels);
        Assert.Contains(AppleMobileConfigText.IrancellButton, labels);
        Assert.Contains(AppleMobileConfigText.RightelButton, labels);
        Assert.Contains(AppleMobileConfigText.ShatelMobileButton, labels);
        Assert.Contains(AppleMobileConfigText.CustomApnButton, labels);
        Assert.DoesNotContain(labels, label =>
            label.Contains("IPv4", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("IPv6", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apple_apn_invalid_input_back_and_cancel_are_recoverable()
    {
        using var databases = new Databases();
        var state = new global::UserStateStore(databases.Users);
        var client = new GatewayTelegramClient();
        var flow = BuildFlow(state);
        var accessor = new BotContextAccessor();
        var home = HomeKeyboard();

        using (accessor.Push(OwnedContext(client)))
        {
            await state.ResetUserStatus(new global::User
            {
                Id = 4242,
                Flow = AppleMobileConfigTelegramFlow.FlowName,
                LastStep = "apn"
            });
            var apnState = await state.GetUserStatus(4242);

            Assert.True(await flow.TryHandleMessageAsync(
                client, Message(4242, "   "), apnState, home, CancellationToken.None));
            Assert.Equal("apn", (await state.GetUserStatus(4242)).LastStep);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:BACK"), apnState, home, CancellationToken.None));
            var menuState = await state.GetUserStatus(4242);
            Assert.Equal("menu", menuState.LastStep);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:CANCEL"), menuState, home, CancellationToken.None));
            Assert.True(string.IsNullOrEmpty((await state.GetUserStatus(4242)).Flow));
        }
    }

    private static AppleMobileConfigTelegramFlow BuildFlow(global::UserStateStore state)
        => new(
            new AppleMobileConfigGenerator(),
            state,
            TelegramInteractionTimeouts.Production,
            NullLogger<AppleMobileConfigTelegramFlow>.Instance);

    private static ReplyKeyboardMarkup HomeKeyboard()
        => new(new[] { new[] { new KeyboardButton("خانه") } });

    private static BotRuntimeContext OwnedContext(GatewayTelegramClient client)
        => new()
        {
            Config = new BotInstanceConfig { Id = "owned-apn-test", Type = BotInstanceTypes.Owned },
            Client = client
        };

    private static Message Message(long userId, string text)
        => new()
        {
            Chat = new Chat { Id = userId },
            From = new TelegramUser { Id = userId },
            Text = text
        };

    private static CallbackQuery Callback(long userId, string data)
        => new()
        {
            Id = "apn-callback-" + data,
            From = new TelegramUser { Id = userId },
            Data = data,
            Message = new Message
            {
                Id = 10,
                Chat = new Chat { Id = userId }
            }
        };
}
