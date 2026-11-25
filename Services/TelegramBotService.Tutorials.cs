using Adminbot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Owned-bot educational navigation using the same built-in installation photo albums as tenant storefronts.</summary>
public partial class TelegramBotService
{
    /// <summary>Owned account-management action opening its miscellaneous tutorials submenu, never the home keyboard.</summary>
    private const string OwnedTutorialsAction = "📚 آموزش‌های متفرقه";

    /// <summary>Installation entry offered only inside miscellaneous tutorials.</summary>
    private const string OwnedInstallationGuideAction = "💡راهنما نصب";

    /// <summary>Owned-only closed tutorial callback namespace; the suffix selects a platform or parent navigation.</summary>
    private const string OwnedTutorialCallbackPrefix = "owned:tutorial:";

    /// <summary>Consumes owned educational menu actions before any purchase or APN conversation can treat them as input.</summary>
    /// <param name="botClient">Required transport of the owned bot receiving this message.</param>
    /// <param name="message">Original Telegram message with its chat id and an exact tutorial menu action.</param>
    /// <param name="cancellationToken">Cancellation of Telegram menu delivery.</param>
    /// <returns>True when the home connection guide, an existing tutorial submenu or the platform chooser was sent; false for unrelated messages.</returns>
    /// <remarks>Call after normal owned-user access checks. This is presentation-only: it never clears or writes bot/user conversation state, wallet data or tenant settings.</remarks>
    /// <exception cref="OperationCanceledException">Menu delivery is cancelled.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The menu send exceeds its foreground budget; it is not automatically resent.</exception>
    /// <example><code>if (await TryHandleOwnedTutorialMessageAsync(client, message, token)) return;</code></example>
    private async Task<bool> TryHandleOwnedTutorialMessageAsync(ITelegramBotClient botClient, Message message,
        CancellationToken cancellationToken)
    {
        if (message.Text?.Trim() == ConnectionGuideFlow.MenuCommand)
            await ConnectionGuideFlow.SendPlatformMenuAsync(botClient, message.Chat.Id, ConnectionGuideFlow.OwnedPrefix, cancellationToken);
        else if (message.Text == OwnedTutorialsAction)
            await SendOwnedMiscellaneousTutorialsAsync(botClient, message.Chat.Id, cancellationToken);
        else if (message.Text == OwnedInstallationGuideAction || message.Text == "راهنما نصب")
            await SendOwnedTutorialPlatformMenuAsync(botClient, message.Chat.Id, cancellationToken);
        else
            return false;
        return true;
    }

    /// <summary>Shows the owned miscellaneous tutorials menu with installation guidance and explicit parent/home actions.</summary>
    /// <param name="botClient">Required current owned-bot transport.</param>
    /// <param name="chatId">Original Telegram chat requesting tutorials, not a tenant or database identifier.</param>
    /// <param name="cancellationToken">Cancellation of the menu send.</param>
    /// <returns>A task completing after the submenu is delivered.</returns>
    /// <remarks>No conversation step is created or replaced; the account-management action returns to the existing customer submenu.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels menu delivery.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The submenu send exceeds its foreground budget; it is not automatically resent.</exception>
    /// <example><code>await SendOwnedMiscellaneousTutorialsAsync(client, message.Chat.Id, token);</code></example>
    private static Task SendOwnedMiscellaneousTutorialsAsync(ITelegramBotClient botClient, ChatId chatId,
        CancellationToken cancellationToken) => botClient.SendMessage(chatId,
            "📚 آموزش‌های متفرقه\n\nآموزش موردنظر خود را انتخاب کنید:",
            replyMarkup: new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { OwnedInstallationGuideAction },
                new KeyboardButton[] { "⚙️ مدیریت اکانت", "🏠منو" }
            }) { ResizeKeyboard = true }, cancellationToken: cancellationToken);

    /// <summary>Opens the shared connection guide with a return action to owned miscellaneous tutorials.</summary>
    /// <param name="botClient">Required transport of the requesting owned bot.</param>
    /// <param name="chatId">Original Telegram chat that receives the platform chooser.</param>
    /// <param name="cancellationToken">Cancellation of the chooser send.</param>
    /// <returns>A task completing after Android, iOS and Windows choices and the parent action are displayed.</returns>
    /// <remarks>The platform chooser is the same one reached from the home menu and purchase delivery.
    /// The existing reply keyboard and conversation remain intact.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels chooser delivery.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The chooser send exceeds its foreground budget; it is not automatically resent.</exception>
    /// <example><code>await SendOwnedTutorialPlatformMenuAsync(client, chatId, token);</code></example>
    private static Task SendOwnedTutorialPlatformMenuAsync(ITelegramBotClient botClient, ChatId chatId,
        CancellationToken cancellationToken) =>
        ConnectionGuideFlow.SendPlatformMenuAsync(botClient, chatId, ConnectionGuideFlow.OwnedPrefix, cancellationToken,
            InlineKeyboardButton.WithCallbackData("⬅️ آموزش‌های متفرقه", OwnedTutorialCallbackPrefix + "back"));

    /// <summary>Acknowledges and serves one owned tutorial platform or parent action without touching conversation state.</summary>
    /// <param name="botClient">Required current owned-bot transport, including its per-update foreground upload budget.</param>
    /// <param name="callback">Original Telegram callback; only the closed owned tutorial namespace is consumed.</param>
    /// <param name="cancellationToken">Cancellation of acknowledgement, file reads and album uploads.</param>
    /// <returns>True for a consumed tutorial callback, including safely rejected unsupported kinds; false for another namespace.</returns>
    /// <remarks>Call only after tenant callback isolation and blocked-user checks. The connection guide checks mandatory
    /// membership before navigation or upload. Legacy album callbacks retain their existing route. Images resolve only
    /// from fixed deployed directories. Viewing a tutorial preserves any active purchase, wallet or APN state.</remarks>
    /// <exception cref="OperationCanceledException">The callback or upload is cancelled.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">A parent or unavailable-assets menu times out; upload failures are handled by the shared sender without retries.</exception>
    /// <example><code>if (await TryHandleOwnedTutorialCallbackAsync(client, callback, token)) return;</code></example>
    private async Task<bool> TryHandleOwnedTutorialCallbackAsync(ITelegramBotClient botClient, CallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (callback.Data?.StartsWith(OwnedTutorialCallbackPrefix, StringComparison.Ordinal) != true)
            return false;
        await SafeAnswerCallbackQueryAsync(botClient, callback.Id, cancellationToken: cancellationToken);
        var chatId = callback.Message?.Chat.Id ?? callback.From.Id;
        if (callback.Data.StartsWith(ConnectionGuideFlow.OwnedPrefix, StringComparison.Ordinal))
        {
            var channels = BuildMandatoryJoinChannels(CurrentChannelIds);
            if (!await isJoinedToChannel(channels.Select(channel => channel.ChatId), callback.From.Id, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "برای دیدن راهنمای اتصال، ابتدا با /start عضویت در کانال‌های اجباری را بررسی کنید.",
                    cancellationToken: cancellationToken);
                return true;
            }

            await ConnectionGuideFlow.SendActionAsync(botClient, chatId, ConnectionGuideFlow.OwnedPrefix,
                callback.Data[ConnectionGuideFlow.OwnedPrefix.Length..], _clientDownloadAvailability.Snapshot.Enabled,
                _logger, cancellationToken);
            return true;
        }

        var kind = callback.Data[OwnedTutorialCallbackPrefix.Length..];
        if (kind == "back")
            await SendOwnedMiscellaneousTutorialsAsync(botClient, chatId, cancellationToken);
        else
        {
            // Only the closed platform mapper may choose a folder; callback data is never a filesystem path.
            var assets = TenantTutorialAssetService.Resolve(kind);
            if (!assets.IsAvailable)
            {
                _logger.LogWarning("Owned tutorial assets unavailable. kind={TutorialKind}, dir={RelativeDirectory}, status={AssetStatus}",
                    TenantTutorialKinds.IsSupported(kind) ? kind : "unsupported", assets.RelativeDirectory, assets.Status);
                await botClient.SendMessage(chatId,
                    "در حال حاضر فایل‌های آموزش در دسترس نیستند. لطفاً کمی بعد دوباره تلاش کنید.",
                    cancellationToken: cancellationToken);
            }
            else
                await TenantTutorialAlbumSender.SendAsync(botClient, chatId, assets, _logger, cancellationToken);
        }
        return true;
    }
}
