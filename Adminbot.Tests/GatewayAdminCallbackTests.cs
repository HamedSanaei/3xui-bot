using System.Reflection;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

public sealed partial class ConcurrencyTests
{
    private const long GatewaySuperAdminId = 85758085;

    [Fact]
    public async Task Super_admin_gateway_refresh_message_not_modified_is_noop_and_never_shows_customer_menu()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var client = new GatewayTelegramClient
        {
            EditException = new ApiRequestException("Bad Request: message is not modified", 400)
        };
        var (service, accessor, _) = BuildGatewayCallbackService(databases, gateway, client);
        var before = gateway.Snapshot;

        await InvokeGatewayCallbackAsync(service, accessor, client, GatewayCallback("refresh", 0, gateway.Snapshot.Revision));
        Assert.Equal(before, gateway.Snapshot);
        Assert.Equal(0, gateway.SetCalls);
        Assert.Single(client.EditAttempts);
        Assert.Single(client.Answers);
        Assert.NotEqual(true, client.Answers[0].ShowAlert);
        Assert.False(string.IsNullOrWhiteSpace(client.Answers[0].Text));
        Assert.Empty(client.Sends);
    }

    [Fact]
    public async Task Super_admin_gateway_refresh_success_acknowledges_once_without_extra_message()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var client = new GatewayTelegramClient();
        var (service, accessor, _) = BuildGatewayCallbackService(databases, gateway, client);
        var before = gateway.Snapshot;

        await InvokeGatewayCallbackAsync(service, accessor, client, GatewayCallback("refresh", 0, gateway.Snapshot.Revision));

        Assert.Equal(before, gateway.Snapshot);
        Assert.Equal(0, gateway.SetCalls);
        var edit = Assert.Single(client.EditAttempts);
        Assert.StartsWith("<b>", edit.Text.Replace("⚙️ ", string.Empty));
        Assert.NotNull(edit.ReplyMarkup);
        Assert.Single(client.Answers);
        Assert.Empty(client.Sends);
    }

    [Fact]
    public async Task Super_admin_gateway_refresh_real_edit_failure_is_gateway_specific_and_non_cascading()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var client = new GatewayTelegramClient();
        client.EditException = new ApiRequestException("Bad Request: chat not found", 400);
        var (service, accessor, _) = BuildGatewayCallbackService(databases, gateway, client);
        var before = gateway.Snapshot;

        await InvokeGatewayCallbackAsync(service, accessor, client, GatewayCallback("refresh", 0, gateway.Snapshot.Revision));

        Assert.Equal(before, gateway.Snapshot);
        Assert.Equal(0, gateway.SetCalls);
        Assert.Single(client.EditAttempts);
        var answer = Assert.Single(client.Answers);
        Assert.True(answer.ShowAlert);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.Empty(client.Sends);
    }
    [Fact]
    public async Task Stale_gateway_toggle_keeps_current_state_and_answers_once()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var client = new GatewayTelegramClient();
        var (service, accessor, _) = BuildGatewayCallbackService(databases, gateway, client);
        var before = gateway.Snapshot;

        await InvokeGatewayCallbackAsync(service, accessor, client, GatewayCallback("hp", 0, before.Revision - 1));

        Assert.Equal(before, gateway.Snapshot);
        Assert.Equal(1, gateway.SetCalls);
        Assert.Single(client.EditAttempts);
        var answer = Assert.Single(client.Answers);
        Assert.True(answer.ShowAlert);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.Empty(client.Sends);
    }

    [Fact]
    public async Task Gateway_toggle_success_updates_once_and_answers_once()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var client = new GatewayTelegramClient();
        var (service, accessor, _) = BuildGatewayCallbackService(databases, gateway, client);
        var revision = gateway.Snapshot.Revision;

        await InvokeGatewayCallbackAsync(service, accessor, client, GatewayCallback("hp", 0, revision));
        Assert.False(gateway.Snapshot.HooshPayEnabled);
        Assert.Equal(revision + 1, gateway.Snapshot.Revision);
        Assert.Equal(1, gateway.SetCalls);
        Assert.Single(client.EditAttempts);
        Assert.Single(client.Answers);
        Assert.Empty(client.Sends);
    }

    [Fact]
    public async Task Non_super_admin_replayed_gateway_callback_is_rejected_without_state_change()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var client = new GatewayTelegramClient();
        var (service, accessor, _) = BuildGatewayCallbackService(databases, gateway, client);
        var before = gateway.Snapshot;
        var callback = GatewayCallback("refresh", 0, before.Revision, actor: 99112233);

        await InvokeGatewayCallbackAsync(service, accessor, client, callback);

        Assert.Equal(before, gateway.Snapshot);
        Assert.Equal(0, gateway.SetCalls);
        Assert.Empty(client.EditAttempts);
        var answer = Assert.Single(client.Answers);
        Assert.True(answer.ShowAlert);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.Empty(client.Sends);
    }
    private static (TelegramBotService Service, BotContextAccessor Accessor, string ActivityLogPath) BuildGatewayCallbackService(
        Databases databases,
        GatewayAvailabilityProbe gateway,
        GatewayTelegramClient client)
    {
        var activityLogPath = Path.Combine(databases.DirectoryPath, "gateway-activity.jsonl");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminsUserIds:0"] = GatewaySuperAdminId.ToString(),
            ["UserActivityLogEnabled"] = "false",
            ["UserActivityLogFilePath"] = activityLogPath
        }).Build();
        var accessor = new BotContextAccessor();
        var service = new TelegramBotService(
            client, new UserWorkflowStore(databases.Users), new UserStateStore(databases.Users),
            new CredentialsStore(databases.Credentials), configuration, NullLogger<TelegramBotService>.Instance,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, gateway,
            null!, null!,
            null!, null!, null!, null!, null!, null!, new UserActivityLogService(configuration), null!, null!,
            null!, null!, null!, null!, null!, null!, accessor, null!);
        return (service, accessor, activityLogPath);
    }

    private static async Task InvokeGatewayCallbackAsync(
        TelegramBotService service, BotContextAccessor accessor, ITelegramBotClient client, CallbackQuery callback)
    {
        var method = typeof(TelegramBotService).GetMethod("ProccessCallbacks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scope = accessor.Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = "owned-gateway-test", Type = BotInstanceTypes.Owned, Username = "owned_gateway_test" },
            Client = client
        });
        await (Task)method.Invoke(service, new object[] { callback, CancellationToken.None })!;
    }
    private static CallbackQuery GatewayCallback(string action, int target, long revision, long actor = GatewaySuperAdminId)
        => new()
        {
            Id = "gateway-callback-test",
            From = new Telegram.Bot.Types.User { Id = actor, FirstName = "test" },
            Data = $"gw:{revision}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{action}:{target}",
            Message = new Message
            {
                MessageId = 77,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = actor, Type = Telegram.Bot.Types.Enums.ChatType.Private }
            }
        };

    private sealed class GatewayAvailabilityProbe : IPaymentGatewayAvailability
    {
        public PaymentGatewayAvailabilitySnapshot Snapshot { get; private set; } = new(true, false, true, true, false, 7);
        public int SetCalls { get; private set; }
        public bool IsConfigured(PaymentGateway gateway) => true;

        public Task<PaymentGatewayToggleResult> SetEnabledAsync(
            PaymentGateway gateway, bool enabled, long expectedRevision, CancellationToken cancellationToken = default)
        {
            SetCalls++;
            var current = Snapshot;
            if (expectedRevision != current.Revision)
                return Task.FromResult(new PaymentGatewayToggleResult(false, current, "Ø§ÛŒÙ† Ù¾Ù†Ù„ Ù‚Ø¯ÛŒÙ…ÛŒ Ø´Ø¯Ù‡ Ø§Ø³ØªØ› ÙˆØ¶Ø¹ÛŒØª Ø¬Ø¯ÛŒØ¯ Ù†Ù…Ø§ÛŒØ´ Ø¯Ø§Ø¯Ù‡ Ø´Ø¯."));
            Snapshot = current with
            {
                HooshPayEnabled = gateway == PaymentGateway.HooshPay ? enabled : current.HooshPayEnabled,
                TetraminatorEnabled = gateway == PaymentGateway.Tetraminator ? enabled : current.TetraminatorEnabled,
                UniquePayEnabled = gateway == PaymentGateway.UniquePay ? enabled : current.UniquePayEnabled,
                AtlasPayEnabled = gateway == PaymentGateway.AtlasPay ? enabled : current.AtlasPayEnabled,
                NowPaymentsEnabled = gateway == PaymentGateway.NowPayments ? enabled : current.NowPaymentsEnabled,
                Revision = current.Revision + 1
            };
            return Task.FromResult(new PaymentGatewayToggleResult(true, Snapshot, "ÙˆØ¶Ø¹ÛŒØª Ø¯Ø±Ú¯Ø§Ù‡ ØªØºÛŒÛŒØ± Ú©Ø±Ø¯."));
        }
    }

    private sealed class GatewayTelegramClient : ITelegramBotClient
    {
        public List<EditMessageTextRequest> EditAttempts { get; } = new();
        public List<AnswerCallbackQueryRequest> Answers { get; } = new();
        public List<SendMessageRequest> Sends { get; } = new();
        public Exception? EditException { get; set; }
        public bool LocalBotServer => false;
        public long? BotId => 12345;
        public TimeSpan Timeout { get; set; }
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        public Task<bool> TestApiAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is EditMessageTextRequest edit)
            {
                EditAttempts.Add(edit);
                if (EditException != null)
                    throw EditException;
                return Task.FromResult((TResponse)(object)new Message { MessageId = edit.MessageId, Chat = new Chat { Id = 1 } });
            }
            if (request is AnswerCallbackQueryRequest answer)
            {
                Answers.Add(answer);
                return Task.FromResult((TResponse)(object)true);
            }
            if (request is SendMessageRequest send)
            {
                Sends.Add(send);
                return Task.FromResult((TResponse)(object)new Message { MessageId = 1, Chat = new Chat { Id = 1 } });
            }
            throw new InvalidOperationException($"Unexpected Telegram request in gateway callback test: {request.GetType().Name}");
        }
    }
}
