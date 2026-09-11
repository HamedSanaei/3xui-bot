using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Services;

/// <summary>
/// Shared customer-facing user experience for the latest-client-software download menu.
/// </summary>
/// <remarks>
/// <para>
/// Owned and tenant bots both delegate here so a customer sees identical wording, identical inline buttons, and identical
/// release-selection results no matter which storefront they are using. The helper is stateless: it renders text and links
/// only, and never touches orders, payments, wallets, tenants, panel state, or conversation state.
/// </para>
/// <para>
/// Every entry point re-checks the live global switch before doing work, and the release resolution is read-only. Nothing
/// here can unblock a purchase, renewal, or settlement path.
/// </para>
/// </remarks>
internal static class ClientDownloadFlow
{
    /// <summary>
    /// Reports whether a message text is the download-menu reply-keyboard label.
    /// </summary>
    /// <param name="text">Incoming message text; may be null for non-text updates.</param>
    /// <returns><c>true</c> only for the exact menu label.</returns>
    /// <remarks>
    /// Matched as an exact high-level navigation label so a partially typed customer message can never be mistaken for it.
    /// </remarks>
    internal static bool IsMenuRequest(string text)
        => !string.IsNullOrWhiteSpace(text) && text.Trim() == ClientDownloadCallbacks.OpenCommand;

    /// <summary>
    /// Sends the platform selector for the latest-client-software menu.
    /// </summary>
    /// <param name="botClient">Telegram client of the bot that received the customer's request.</param>
    /// <param name="chatId">Chat that should receive the selector.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send.</param>
    /// <returns>A task that completes after the selector message is sent.</returns>
    /// <remarks>
    /// The inline buttons carry only the three compile-time platform payloads, so no customer input can influence which
    /// repository, tag, asset, or URL is resolved.
    /// </remarks>
    internal static Task SendPlatformMenuAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        var rows = new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(
                ClientDownloadCallbacks.GetButtonLabel(ClientDownloadPlatform.Android),
                ClientDownloadCallbacks.Build(ClientDownloadPlatform.Android)) },
            new[] { InlineKeyboardButton.WithCallbackData(
                ClientDownloadCallbacks.GetButtonLabel(ClientDownloadPlatform.Ios),
                ClientDownloadCallbacks.Build(ClientDownloadPlatform.Ios)) },
            new[] { InlineKeyboardButton.WithCallbackData(
                ClientDownloadCallbacks.GetButtonLabel(ClientDownloadPlatform.Windows),
                ClientDownloadCallbacks.Build(ClientDownloadPlatform.Windows)) }
        };

        return botClient.SendTextMessageAsync(
            chatId,
            "📲 دریافت آخرین نسخه نرم‌افزار\nسیستم‌عامل خود را انتخاب کنید:",
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves one platform and sends the customer the official download link.
    /// </summary>
    /// <param name="botClient">Telegram client of the bot that received the customer's request.</param>
    /// <param name="chatId">Chat that should receive the result.</param>
    /// <param name="releases">Shared release resolver used identically by owned and tenant bots.</param>
    /// <param name="platform">Platform selected by an inline button.</param>
    /// <param name="cancellationToken">Cancellation token for provider and Telegram work.</param>
    /// <returns>A task that completes after a link or a friendly unavailability notice is sent.</returns>
    /// <remarks>
    /// The archive and APK are never uploaded or proxied through Telegram: only the vendor's own absolute HTTPS URL is
    /// sent, which keeps server bandwidth and Telegram file-size limits out of the picture. A resolution failure is
    /// reported as temporarily unavailable rather than as a guessed or partially reconstructed link.
    /// </remarks>
    internal static async Task SendPlatformResultAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        IClientReleaseService releases,
        ClientDownloadPlatform platform,
        CancellationToken cancellationToken)
    {
        ClientReleaseResolution resolution;
        try
        {
            resolution = await releases.GetLatestAsync(platform, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A provider outage must never escape into the Telegram update pipeline.
            await botClient.SendTextMessageAsync(
                chatId,
                BuildUnavailableMessage(platform),
                cancellationToken: cancellationToken);
            return;
        }

        if (!resolution.Success || resolution.Link == null)
        {
            await botClient.SendTextMessageAsync(
                chatId,
                BuildUnavailableMessage(platform),
                cancellationToken: cancellationToken);
            return;
        }

        var link = resolution.Link;
        var builder = new System.Text.StringBuilder();
        switch (platform)
        {
            case ClientDownloadPlatform.Windows:
                builder.Append("🪟 آخرین نسخه پایدار v2rayN\n\n");
                break;
            case ClientDownloadPlatform.Android:
                builder.Append("🤖 آخرین نسخه پایدار v2rayNG\n\n");
                break;
            default:
                builder.Append("🍎 V2Box برای iOS\n\n");
                break;
        }

        if (link.IsStale)
        {
            // A cached value is never presented as freshly verified.
            builder.Append("ℹ️ آخرین نسخه دریافت‌شده از GitHub\n\n");
        }

        if (platform == ClientDownloadPlatform.Ios)
        {
            builder.Append("این نرم‌افزار برای iOS و Android قابل استفاده است.");
        }
        else
        {
            builder.Append($"نسخه: {link.Version}\n");
            builder.Append($"معماری: {link.Architecture}");
        }

        var buttonLabel = platform == ClientDownloadPlatform.Ios
            ? "📥 دریافت از App Store"
            : $"📥 دانلود {link.FileName}";

        await botClient.SendTextMessageAsync(
            chatId,
            builder.ToString(),
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl(buttonLabel, link.DownloadUrl) }
            }),
            cancellationToken: cancellationToken);
    }

    /// <summary>Builds the friendly notice used when a platform's download cannot be resolved right now.</summary>
    /// <param name="platform">Platform that failed to resolve.</param>
    /// <returns>Customer-safe Persian text with no provider detail, URL, or exception text.</returns>
    private static string BuildUnavailableMessage(ClientDownloadPlatform platform)
        => platform == ClientDownloadPlatform.Ios
            ? "در حال حاضر لینک دریافت در دسترس نیست. لطفاً کمی بعد دوباره تلاش کنید."
            : "در حال حاضر امکان دریافت آخرین نسخه از GitHub وجود ندارد. لطفاً کمی بعد دوباره تلاش کنید.";
}
