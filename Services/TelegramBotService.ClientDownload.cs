using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Latest-client-software download support for owned bots, plus the global super-admin switch that governs it.
/// </summary>
/// <remarks>
/// Split out of the main service because the feature is a small, self-contained navigation surface. The customer-facing
/// rendering and the release resolution live in shared helpers, so the tenant storefront behaves identically without a
/// second copy of the rules.
/// </remarks>
public partial class TelegramBotService
{
    /// <summary>
    /// Super-admin reply-keyboard action that opens the global latest-client-download switch panel.
    /// </summary>
    private const string AdminClientDownloadAction = "📥 دانلود نرم‌افزار";

    /// <summary>Callback namespace for the super-admin switch panel, deliberately separate from the <c>gw:</c> family.</summary>
    private const string ClientDownloadAdminCallbackPrefix = "cdl:";

    /// <summary>Lifetime of one super-admin switch button before it must be refreshed.</summary>
    private const long ClientDownloadAdminCallbackMaxAgeSeconds = 600;

    /// <summary>
    /// Answers the customer download-menu label in an owned bot, or reports that it is currently disabled.
    /// </summary>
    /// <param name="botClient">Telegram client of the owned bot that received the message.</param>
    /// <param name="message">Incoming message; ignored unless its text is exactly the menu label.</param>
    /// <param name="user">Bot-scoped customer state, untouched by this navigation action.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send.</param>
    /// <returns>
    /// <c>true</c> when the message was the download-menu label and has been fully handled, so the caller must not pass it
    /// to any conversation state machine.
    /// </returns>
    /// <remarks>
    /// The live global switch is re-read here rather than trusting the keyboard the customer pressed, because a reply
    /// keyboard survives an administrator disabling the feature. When disabled the method answers and performs no release
    /// lookup, so a stale button cannot trigger provider traffic.
    /// </remarks>
    private async Task<bool> TryHandleClientDownloadMenuRequestAsync(
        ITelegramBotClient botClient,
        Message message,
        User user,
        CancellationToken cancellationToken)
    {
        if (message?.Text is null || !ClientDownloadFlow.IsMenuRequest(message.Text))
            return false;

        if (!_clientDownloadAvailability.Snapshot.Enabled)
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                ClientDownloadCallbacks.DisabledMessage,
                replyMarkup: MainReplyMarkupKeyboardFa(),
                cancellationToken: cancellationToken);
            return true;
        }

        await ClientDownloadFlow.SendPlatformMenuAsync(botClient, message.Chat.Id, cancellationToken);
        return true;
    }

    /// <summary>
    /// Handles one owned-bot platform selection from the download menu.
    /// </summary>
    /// <param name="botClient">Telegram client of the owned bot that received the callback.</param>
    /// <param name="callbackQuery">Callback carrying one of the three compile-time platform payloads.</param>
    /// <param name="platform">Parsed platform.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram and provider work.</param>
    /// <returns>A task that completes after the link or a friendly notice has been sent.</returns>
    /// <remarks>
    /// The callback is acknowledged first so the customer's client does not spin while GitHub is queried, then the live
    /// global switch is re-checked, so a stale inline button fails closed without any provider request.
    /// </remarks>
    private async Task HandleClientDownloadPlatformCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        ClientDownloadPlatform platform,
        CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        if (!_clientDownloadAvailability.Snapshot.Enabled)
        {
            await SafeAnswerCallbackQueryAsync(
                callbackQuery.Id,
                ClientDownloadCallbacks.DisabledMessage,
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        await SafeAnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
        await ClientDownloadFlow.SendPlatformResultAsync(
            botClient,
            chatId,
            _clientReleaseService,
            platform,
            cancellationToken);
    }

    /// <summary>
    /// Sends the global latest-client-download switch panel to an authenticated super-admin.
    /// </summary>
    /// <param name="botClient">Telegram client for the current owned bot.</param>
    /// <param name="chatId">Super-admin Telegram chat id.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send operation.</param>
    /// <returns>A task that completes after the panel message is sent.</returns>
    /// <remarks>The panel shows the live global state and never performs a provider lookup.</remarks>
    private async Task SendClientDownloadPanelAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        await botClient.SendTextMessageAsync(
            chatId,
            BuildClientDownloadPanelText(),
            parseMode: ParseMode.Html,
            replyMarkup: BuildClientDownloadPanelMarkup(),
            cancellationToken: cancellationToken);
    }

    /// <summary>Builds the safe HTML status text for the global latest-client-download panel.</summary>
    /// <returns>Text containing the live state and the exact configuration key, without secrets.</returns>
    private string BuildClientDownloadPanelText()
    {
        var snapshot = _clientDownloadAvailability.Snapshot;
        var builder = new StringBuilder("📥 <b>دانلود آخرین نسخه نرم‌افزار</b>\n\n");
        builder.AppendLine(
            $"وضعیت: {(snapshot.Enabled ? "✅ فعال" : "❌ غیرفعال")}\n" +
            $"<code>{Html(ClientDownloadAvailabilityService.ConfigurationPropertyName)}</code>");
        builder.AppendLine();
        builder.Append("این تنظیم سراسری است و روی ربات اصلی و همه فروشگاه‌های tenant اثر می‌گذارد.");
        return builder.ToString();
    }

    /// <summary>
    /// Builds the revisioned toggle button for the global latest-client-download switch.
    /// </summary>
    /// <returns>Inline keyboard with one target-state button and a refresh button.</returns>
    /// <remarks>
    /// The button encodes the revision it was rendered from, so a stale or replayed callback is rejected instead of
    /// overwriting a newer state.
    /// </remarks>
    private InlineKeyboardMarkup BuildClientDownloadPanelMarkup()
    {
        var snapshot = _clientDownloadAvailability.Snapshot;
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var desired = !snapshot.Enabled;
        var label = desired ? "✅ فعال کردن" : "❌ غیرفعال کردن";
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    label,
                    $"{ClientDownloadAdminCallbackPrefix}{snapshot.Revision}:{issuedAt}:{(desired ? 1 : 0)}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "🔄 تازه‌سازی",
                    $"{ClientDownloadAdminCallbackPrefix}{snapshot.Revision}:{issuedAt}:refresh")
            }
        });
    }

    /// <summary>
    /// Processes one super-admin latest-client-download callback with failure isolation.
    /// </summary>
    /// <param name="callbackQuery">Callback carrying revision, timestamp, and target state.</param>
    /// <param name="cancellationToken">Cancellation token for persistence and Telegram edits.</param>
    /// <returns>A task that completes after the panel is refreshed or the callback is rejected.</returns>
    private async Task ProcessClientDownloadCallbackSafelyAsync(
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessClientDownloadCallbackAsync(callbackQuery, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Latest-client-download admin callback failed. botId={BotId}, superAdmin={SuperAdmin}",
                BotContextAccessor.CurrentBotId,
                IsSuperAdminUser(callbackQuery.From.Id));
            try
            {
                await SafeAnswerCallbackQueryAsync(
                    callbackQuery.Id,
                    "به‌روزرسانی وضعیت دانلود نرم‌افزار انجام نشد؛ لطفاً دوباره تلاش کنید.",
                    showAlert: true,
                    cancellationToken: cancellationToken);
            }
            catch (Exception answerEx)
            {
                _logger.LogWarning(
                    "Latest-client-download failure response could not be delivered. botId={BotId}, errorType={ErrorType}",
                    BotContextAccessor.CurrentBotId,
                    answerEx.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Applies one authorized, unexpired, revision-matching global switch transition.
    /// </summary>
    /// <param name="callbackQuery">Callback carrying revision, timestamp, and target state.</param>
    /// <param name="cancellationToken">Cancellation token for the serialized configuration write.</param>
    /// <returns>A task that completes after the panel text is refreshed or the callback is rejected.</returns>
    /// <remarks>
    /// Authorization is enforced independently of the UI route: a callback from anyone outside the configured
    /// super-admin list performs no write at all. When the durable write fails the runtime flag is left unchanged and the
    /// administrator is told so explicitly.
    /// </remarks>
    private async Task ProcessClientDownloadCallbackAsync(
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        if (!IsSuperAdminUser(callbackQuery.From.Id))
        {
            await SafeAnswerCallbackQueryAsync(
                callbackQuery.Id,
                "این بخش فقط برای سوپرادمین‌هاست.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        var parts = (callbackQuery.Data ?? string.Empty).Split(':');
        if (parts.Length != 4 ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedAt))
        {
            await SafeAnswerCallbackQueryAsync(callbackQuery.Id, "دکمه نامعتبر است.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAt) > ClientDownloadAdminCallbackMaxAgeSeconds)
        {
            await SafeAnswerCallbackQueryAsync(callbackQuery.Id, "این دکمه منقضی شده است؛ پنل را تازه کنید.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var target = parts[3];
        if (string.Equals(target, "refresh", StringComparison.Ordinal))
        {
            await SafeAnswerCallbackQueryAsync(callbackQuery.Id, "وضعیت به‌روز شد.", cancellationToken: cancellationToken);
            await EditClientDownloadPanelAsync(callbackQuery, cancellationToken);
            return;
        }

        if (target is not ("0" or "1"))
        {
            await SafeAnswerCallbackQueryAsync(callbackQuery.Id, "دکمه نامعتبر است.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var result = await _clientDownloadAvailability.SetEnabledAsync(
            enabled: target == "1",
            expectedRevision: revision,
            cancellationToken: cancellationToken);

        await SafeAnswerCallbackQueryAsync(
            callbackQuery.Id,
            result.Message,
            showAlert: !result.Applied,
            cancellationToken: cancellationToken);
        await EditClientDownloadPanelAsync(callbackQuery, cancellationToken);
    }

    /// <summary>
    /// Replaces the super-admin panel with the current live state.
    /// </summary>
    /// <param name="callbackQuery">Callback whose message is the panel being refreshed.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram edit.</param>
    /// <returns>A task that completes after the edit or its best-effort failure.</returns>
    /// <remarks>
    /// An edit can legitimately fail when the message is unchanged or too old; that is not an operational error, so it is
    /// deliberately swallowed rather than surfaced to the administrator as a failure.
    /// </remarks>
    private async Task EditClientDownloadPanelAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (callbackQuery.Message is not { } panelMessage)
            return;

        try
        {
            await ActiveBotClient.EditMessageTextAsync(
                chatId: panelMessage.Chat.Id,
                messageId: panelMessage.MessageId,
                text: BuildClientDownloadPanelText(),
                parseMode: ParseMode.Html,
                replyMarkup: BuildClientDownloadPanelMarkup(),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Latest-client-download panel refresh was not applied. botId={BotId}", BotContextAccessor.CurrentBotId);
        }
    }
}
