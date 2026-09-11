using Adminbot.Domain;
using Adminbot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

/// <summary>
/// Latest-client-software download support for tenant storefront bots.
/// </summary>
/// <remarks>
/// <para>
/// The feature is global: no tenant can enable, disable, or reconfigure it, and nothing is persisted on the tenant or its
/// offer configuration for it. Both the reply-keyboard button and the platform callbacks are gated on the live global
/// switch, so an administrator toggle applies to the next render and to every already-sent button.
/// </para>
/// <para>
/// This surface is deliberately separate from the built-in installation tutorial albums. Tutorial callbacks stay in the
/// <c>TN:</c> namespace and download callbacks in <c>APPDL:</c>, so the two features cannot collide or route into each
/// other.
/// </para>
/// </remarks>
public partial class TenantBotService
{
    /// <summary>
    /// Answers the customer download-menu label in a tenant storefront, or reports that it is currently disabled.
    /// </summary>
    /// <param name="botClient">Telegram client of the tenant storefront that received the message.</param>
    /// <param name="message">Incoming customer message; ignored unless its text is exactly the menu label.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send.</param>
    /// <returns>
    /// <c>true</c> when the message was the download-menu label and has been fully handled, so the storefront
    /// conversation state must not consume it as purchase, duration, account, or receipt input.
    /// </returns>
    /// <remarks>
    /// The live global switch is re-read here rather than trusting the keyboard the customer pressed, because a reply
    /// keyboard survives an administrator disabling the feature. When disabled the method answers and performs no provider
    /// lookup. It mutates no order, payment, wallet, tenant, or XUI state.
    /// </remarks>
    private async Task<bool> TryHandleClientDownloadMenuRequestAsync(
        ITelegramBotClient botClient,
        Message message,
        CancellationToken cancellationToken)
    {
        if (message?.Text is null || !ClientDownloadFlow.IsMenuRequest(message.Text))
            return false;

        if (!_clientDownloadAvailability.Snapshot.Enabled)
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                ClientDownloadCallbacks.DisabledMessage,
                replyMarkup: BuildTenantReplyKeyboard(),
                cancellationToken: cancellationToken);
            return true;
        }

        await ClientDownloadFlow.SendPlatformMenuAsync(botClient, message.Chat.Id, cancellationToken);
        return true;
    }

    /// <summary>
    /// Handles one tenant storefront platform selection from the download menu.
    /// </summary>
    /// <param name="botClient">Telegram client of the storefront that received the callback.</param>
    /// <param name="callbackQuery">Callback carrying one of the three compile-time platform payloads.</param>
    /// <param name="platform">Parsed platform; it selects only a fixed known download target.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram and provider work.</param>
    /// <returns>A task that completes after the link or a friendly notice has been sent.</returns>
    /// <remarks>
    /// The callback is acknowledged first so the customer's client does not spin while GitHub is queried, then the live
    /// global switch is re-checked so a stale inline button fails closed without contacting GitHub. Only the shared
    /// release resolver is used, so a tenant customer receives exactly the same asset a customer of the main bot does; the
    /// callback cannot select another tenant, another repository, or an arbitrary URL.
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
                botClient,
                callbackQuery.Id,
                ClientDownloadCallbacks.DisabledMessage,
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
        await ClientDownloadFlow.SendPlatformResultAsync(
            botClient,
            chatId,
            _clientReleaseService,
            platform,
            cancellationToken);
    }
}
