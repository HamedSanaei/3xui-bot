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
    [Fact]
    public async Task Apple_apn_flow_generates_dual_stack_mobileconfig_and_clears_state()
    {
        using var databases = new Databases();
        var state = new global::UserStateStore(databases.Users);
        var client = new GatewayTelegramClient();
        var flow = new AppleMobileConfigTelegramFlow(
            new AppleMobileConfigGenerator(),
            state,
            TelegramInteractionTimeouts.Production,
            NullLogger<AppleMobileConfigTelegramFlow>.Instance);
        var accessor = new BotContextAccessor();
        var home = new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("خانه") } });

        using (accessor.Push(new BotRuntimeContext
               {
                   Config = new BotInstanceConfig { Id = "owned-apn-test", Type = BotInstanceTypes.Owned },
                   Client = client
               }))
        {
            var opened = await flow.TryHandleMessageAsync(
                client,
                Message(4242, AppleMobileConfigText.MenuCommand),
                new global::User { Id = 4242 },
                home,
                CancellationToken.None);
            Assert.True(opened);
            var menuState = await state.GetUserStatus(4242);
            Assert.Equal(AppleMobileConfigTelegramFlow.FlowName, menuState.Flow);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:CUSTOM"), menuState, home, CancellationToken.None));
            var apnState = await state.GetUserStatus(4242);
            Assert.Equal("apn", apnState.LastStep);

            Assert.True(await flow.TryHandleMessageAsync(
                client, Message(4242, "test"), apnState, home, CancellationToken.None));
            var protocolState = await state.GetUserStatus(4242);
            Assert.Equal("protocol", protocolState.LastStep);
            Assert.Equal("test", protocolState.ConfigLink);

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:IP:46"), protocolState, home, CancellationToken.None));

            var document = Assert.Single(client.Documents);
            var fileName = document.Document.GetType().GetProperty("FileName", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(document.Document) as string;
            Assert.Equal("iphone-apn-ipv4-ipv6.mobileconfig", fileName);
            Assert.Contains(client.Sends, send =>
                send.Text?.Contains("IPv4 + IPv6", StringComparison.Ordinal) == true);

            var cleared = await state.GetUserStatus(4242);
            Assert.True(string.IsNullOrEmpty(cleared.Flow));
            Assert.True(string.IsNullOrEmpty(cleared.LastStep));
            Assert.True(string.IsNullOrEmpty(cleared.ConfigLink));
        }
    }

    [Fact]
    public async Task Apple_apn_invalid_input_back_and_cancel_are_recoverable()
    {
        using var databases = new Databases();
        var state = new global::UserStateStore(databases.Users);
        var client = new GatewayTelegramClient();
        var flow = new AppleMobileConfigTelegramFlow(
            new AppleMobileConfigGenerator(),
            state,
            TelegramInteractionTimeouts.Production,
            NullLogger<AppleMobileConfigTelegramFlow>.Instance);
        var accessor = new BotContextAccessor();
        var home = new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("خانه") } });

        using (accessor.Push(new BotRuntimeContext
               {
                   Config = new BotInstanceConfig { Id = "owned-apn-test", Type = BotInstanceTypes.Owned },
                   Client = client
               }))
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
            var afterInvalid = await state.GetUserStatus(4242);
            Assert.Equal("apn", afterInvalid.LastStep);
            Assert.Contains(client.Sends, send =>
                send.Text?.Contains("مقدار APN معتبر نیست", StringComparison.Ordinal) == true);

            await state.ResetUserStatus(new global::User
            {
                Id = 4242,
                Flow = AppleMobileConfigTelegramFlow.FlowName,
                LastStep = "protocol",
                ConfigLink = "test"
            });
            var protocolState = await state.GetUserStatus(4242);
            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:BACK"), protocolState, home, CancellationToken.None));
            var afterBack = await state.GetUserStatus(4242);
            Assert.Equal("apn", afterBack.LastStep);
            Assert.True(string.IsNullOrEmpty(afterBack.ConfigLink));

            Assert.True(await flow.TryHandleCallbackAsync(
                client, Callback(4242, "IOSAPN:CANCEL"), afterBack, home, CancellationToken.None));
            var afterCancel = await state.GetUserStatus(4242);
            Assert.True(string.IsNullOrEmpty(afterCancel.Flow));
        }
    }

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
