using Adminbot.Domain;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Services.AppleMobileConfig;

/// <summary>Shared owned/tenant Telegram conversation for generating an unsigned iOS APN profile in memory.</summary>
/// <remarks>Generation stays in <see cref="IAppleMobileConfigGenerator"/>; this service owns only UI and bot-scoped state.</remarks>
public sealed class AppleMobileConfigTelegramFlow
{
    public const string FlowName = "apple-mobileconfig";
    private const string StepMenu = "menu";
    private const string StepApn = "apn";
    private const string StepProtocol = "protocol";
    private const string CallbackPrefix = "IOSAPN:";
    private const string CallbackCustom = CallbackPrefix + "CUSTOM";
    private const string CallbackBack = CallbackPrefix + "BACK";
    private const string CallbackCancel = CallbackPrefix + "CANCEL";
    private const string CallbackIp4 = CallbackPrefix + "IP:4";
    private const string CallbackIp6 = CallbackPrefix + "IP:6";
    private const string CallbackIp46 = CallbackPrefix + "IP:46";

    private readonly IAppleMobileConfigGenerator _generator;
    private readonly global::UserStateStore _state;
    private readonly global::TelegramInteractionTimeouts _interactionTimeouts;
    private readonly ILogger<AppleMobileConfigTelegramFlow> _logger;

    public AppleMobileConfigTelegramFlow(
        IAppleMobileConfigGenerator generator,
        global::UserStateStore state,
        global::TelegramInteractionTimeouts interactionTimeouts,
        ILogger<AppleMobileConfigTelegramFlow> logger)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _interactionTimeouts = interactionTimeouts ?? global::TelegramInteractionTimeouts.Production;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Returns true only for the compact callback namespace owned by this feature.</summary>
    public static bool IsCallback(string data)
        => data?.StartsWith(CallbackPrefix, StringComparison.Ordinal) == true;

    /// <summary>Handles the menu command or one active APN text-input step.</summary>
    public async Task<bool> TryHandleMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        global::User user,
        ReplyMarkup homeKeyboard,
        CancellationToken cancellationToken)
    {
        if (message?.From == null || message.Text == null)
            return false;
        var text = message.Text.Trim();
        if (string.Equals(text, AppleMobileConfigText.MenuCommand, StringComparison.Ordinal))
        {
            await _state.ResetUserStatus(new global::User
            {
                Id = message.From.Id,
                Flow = FlowName,
                LastStep = StepMenu
            });
            await botClient.SendMessage(
                message.Chat.Id,
                AppleMobileConfigText.Intro,
                replyMarkup: BuildMenuKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (!IsActive(user))
            return false;

        if (text.StartsWith("/", StringComparison.Ordinal))
            return false;

        if (text is "لغو" or "منوی اصلی" or "🏠منو")
        {
            await CancelAsync(botClient, message.Chat.Id, message.From.Id, homeKeyboard, cancellationToken);
            return true;
        }

        if (!string.Equals(user.LastStep, StepApn, StringComparison.Ordinal))
            return false;

        string apn;
        try
        {
            apn = MobileConfigValidation.NormalizeApn(text);
        }
        catch (ArgumentException)
        {
            await botClient.SendMessage(
                message.Chat.Id,
                AppleMobileConfigText.InvalidApn,
                replyMarkup: BuildApnInputKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        await _state.ResetUserStatus(new global::User
        {
            Id = message.From.Id,
            Flow = FlowName,
            LastStep = StepProtocol,
            ConfigLink = apn
        });
        await botClient.SendMessage(
            message.Chat.Id,
            AppleMobileConfigText.ProtocolTitle,
            replyMarkup: BuildProtocolKeyboard(),
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>Handles custom-APN, protocol, back, and cancel callbacks after the caller's access gate has passed.</summary>
    public async Task<bool> TryHandleCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        global::User user,
        ReplyMarkup homeKeyboard,
        CancellationToken cancellationToken,
        bool callbackAlreadyAcknowledged = false)
    {
        if (callbackQuery?.From == null || !IsCallback(callbackQuery.Data))
            return false;

        if (!callbackAlreadyAcknowledged)
        {
            await global::TelegramCallbackAnswerPolicy.TryAnswerAsync(
                botClient,
                callbackQuery.Id,
                cancellationToken: cancellationToken,
                logger: _logger,
                botId: BotContextAccessor.CurrentBotId,
                telegramUserId: callbackQuery.From.Id,
                timeout: _interactionTimeouts.CallbackAnswer);
        }

        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        if (string.Equals(callbackQuery.Data, CallbackCancel, StringComparison.Ordinal))
        {
            await CancelAsync(botClient, chatId, callbackQuery.From.Id, homeKeyboard, cancellationToken);
            return true;
        }

        if (string.Equals(callbackQuery.Data, CallbackCustom, StringComparison.Ordinal) ||
            string.Equals(callbackQuery.Data, CallbackBack, StringComparison.Ordinal))
        {
            await _state.ResetUserStatus(new global::User
            {
                Id = callbackQuery.From.Id,
                Flow = FlowName,
                LastStep = StepApn
            });
            await botClient.SendMessage(
                chatId,
                AppleMobileConfigText.ApnPrompt,
                replyMarkup: BuildApnInputKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        if (!TryParseProtocol(callbackQuery.Data, out var protocol))
            return true;

        if (!IsActive(user) ||
            !string.Equals(user.LastStep, StepProtocol, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(user.ConfigLink))
        {
            await _state.ClearUserStatus(new global::User { Id = callbackQuery.From.Id });
            await botClient.SendMessage(
                chatId,
                AppleMobileConfigText.ExpiredStep,
                replyMarkup: homeKeyboard,
                cancellationToken: cancellationToken);
            return true;
        }

        try
        {
            var options = new ApnProfileOptions
            {
                Apn = user.ConfigLink,
                Protocol = protocol,
                AuthenticationType = ApnAuthenticationType.Pap,
                ConfigureRoamingProtocol = true,
                DisplayName = "APN Configuration"
            };
            var profileBytes = _generator.GenerateApnProfile(options);
            var fileName = AppleMobileConfigText.FileName(protocol);

            await using var stream = new MemoryStream(profileBytes, writable: false);
            await botClient.SendDocument(
                chatId,
                InputFile.FromStream(stream, fileName),
                cancellationToken: cancellationToken);

            await _state.ClearUserStatus(new global::User { Id = callbackQuery.From.Id });
            _logger.LogInformation(
                "Apple APN profile generated. BotId={BotId} TelegramUserId={TelegramUserId} Protocol={Protocol} HasUsername={HasUsername} HasPassword={HasPassword}",
                BotContextAccessor.CurrentBotId,
                callbackQuery.From.Id,
                protocol,
                !string.IsNullOrEmpty(options.Username),
                !string.IsNullOrEmpty(options.Password));

            await botClient.SendMessage(
                chatId,
                AppleMobileConfigText.BuildSuccess(options.Apn, protocol),
                replyMarkup: homeKeyboard,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never log generated XML, APN credentials, or exception data that could contain a password.
            _logger.LogWarning(
                "Apple APN profile generation/delivery failed. BotId={BotId} TelegramUserId={TelegramUserId} Protocol={Protocol} ErrorType={ErrorType}",
                BotContextAccessor.CurrentBotId,
                callbackQuery.From.Id,
                protocol,
                ex.GetType().Name);
            await botClient.SendMessage(
                chatId,
                AppleMobileConfigText.GenericError,
                replyMarkup: BuildProtocolKeyboard(),
                cancellationToken: cancellationToken);
        }

        return true;
    }

    private async Task CancelAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        long userId,
        ReplyMarkup homeKeyboard,
        CancellationToken cancellationToken)
    {
        await _state.ClearUserStatus(new global::User { Id = userId });
        await botClient.SendMessage(
            chatId,
            AppleMobileConfigText.Cancelled,
            replyMarkup: homeKeyboard,
            cancellationToken: cancellationToken);
    }

    private static bool IsActive(global::User user)
        => string.Equals(user?.Flow, FlowName, StringComparison.Ordinal);

    private static bool TryParseProtocol(string data, out IpProtocolMode protocol)
    {
        protocol = data switch
        {
            CallbackIp4 => IpProtocolMode.IPv4,
            CallbackIp6 => IpProtocolMode.IPv6,
            CallbackIp46 => IpProtocolMode.IPv4AndIPv6,
            _ => 0
        };
        return protocol != 0;
    }

    public static InlineKeyboardMarkup BuildMenuKeyboard()
        => new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(AppleMobileConfigText.CustomApnButton, CallbackCustom) },
            new[] { InlineKeyboardButton.WithCallbackData(AppleMobileConfigText.CancelButton, CallbackCancel) }
        });

    public static InlineKeyboardMarkup BuildProtocolKeyboard()
        => new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("IPv4", CallbackIp4),
                InlineKeyboardButton.WithCallbackData("IPv6", CallbackIp6)
            },
            new[] { InlineKeyboardButton.WithCallbackData("IPv4 + IPv6 ✅", CallbackIp46) },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(AppleMobileConfigText.BackButton, CallbackBack),
                InlineKeyboardButton.WithCallbackData(AppleMobileConfigText.CancelButton, CallbackCancel)
            }
        });

    private static InlineKeyboardMarkup BuildApnInputKeyboard()
        => new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(AppleMobileConfigText.CancelButton, CallbackCancel) }
        });
}
