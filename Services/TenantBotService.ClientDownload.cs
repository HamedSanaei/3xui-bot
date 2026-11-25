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
    /// The connection guide and standalone download menu both use the <c>APPDL:</c> download callbacks.
    /// Album navigation uses the existing <c>TN:</c> customer namespace.
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
            await botClient.SendMessage(
                message.Chat.Id,
                ClientDownloadCallbacks.DisabledMessage,
                replyMarkup: await BuildCurrentTenantReplyKeyboardAsync(cancellationToken),
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
    /// The live switch is checked before and after acknowledgement, so a stale inline button cannot start a lookup.
    /// The shared release resolver gives a tenant customer the same asset as the main bot. The
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
        if (!_clientDownloadAvailability.Snapshot.Enabled) return;
        await ClientDownloadFlow.SendPlatformResultAsync(
            botClient,
            chatId,
            _clientReleaseService,
            platform,
            cancellationToken);
    }

    /// <summary>Renders the shared Happ or parent menu after the current tenant storefront's existing access checks.</summary>
    /// <param name="botClient">Client of the tenant bot receiving the callback; no cross-bot/default delivery is permitted.</param>
    /// <param name="callbackQuery">Customer callback recognized by the exact shared navigation parser.</param>
    /// <param name="menu">Closed Happ or parent destination, not a tenant, repository or arbitrary URL.</param>
    /// <param name="cancellationToken">Current tenant callback lifetime for acknowledgement and one new menu message.</param>
    /// <returns>A task after the menu is sent, or a disabled-feature alert with no resolver/provider call.</returns>
    /// <remarks>The global switch is rechecked after acknowledgement as well as before it. Existing tenant availability,
    /// owner-block and customer-access routing remain dispatcher-owned; purchase/renewal state and financial data are unchanged.</remarks>
    private async Task HandleClientDownloadNavigationCallbackAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery,
        ClientDownloadMenu menu, CancellationToken cancellationToken)
    {
        if (!_clientDownloadAvailability.Snapshot.Enabled)
        {
            await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, ClientDownloadCallbacks.DisabledMessage,
                showAlert: true, cancellationToken: cancellationToken);
            return;
        }
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
        if (!_clientDownloadAvailability.Snapshot.Enabled) return;
        await ClientDownloadFlow.SendNavigationMenuAsync(botClient, callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
            menu, cancellationToken);
    }
}
