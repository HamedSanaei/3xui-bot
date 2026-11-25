using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Services;

/// <summary>Shows the same device chooser, download actions and installation albums in owned and tenant bots.</summary>
/// <remarks>All navigation sends a new message. It leaves the purchase message, conversation state and payment untouched.</remarks>
internal static class ConnectionGuideFlow
{
    /// <summary>Reply action available from both customer home menus even when downloads are disabled.</summary>
    internal const string MenuCommand = "📲 راهنمای اتصال";

    /// <summary>New navigation within the owned tutorial callback dispatcher.</summary>
    internal const string OwnedPrefix = "owned:tutorial:guide:";

    /// <summary>New navigation within the tenant customer callback dispatcher.</summary>
    internal const string TenantPrefix = "TN:guide:";

    /// <summary>Opens the platform chooser from an existing purchase message or a platform submenu.</summary>
    private const string OpenAction = "open";

    /// <summary>Opens the built-in album for an exact platform kind.</summary>
    private const string LessonPrefix = "lesson:";

    /// <summary>Builds the single connection guide button to attach to an owned or tenant purchase delivery.</summary>
    /// <param name="callbackPrefix">Fixed <see cref="OwnedPrefix"/> or <see cref="TenantPrefix"/> chosen by the sender.</param>
    /// <param name="downloadsEnabled">Live application-wide download switch at the time the message is sent.</param>
    /// <returns>An inline keyboard with the guide button and no account identifiers.</returns>
    /// <remarks>Only successful new purchases use this markup. The button still opens the chooser after the switch changes.</remarks>
    /// <example><code>var markup = ConnectionGuideFlow.BuildPurchaseMarkup(ConnectionGuideFlow.OwnedPrefix, downloadsEnabled);</code></example>
    internal static InlineKeyboardMarkup BuildPurchaseMarkup(string callbackPrefix, bool downloadsEnabled) =>
        new(new[] { InlineKeyboardButton.WithCallbackData(
            downloadsEnabled ? "📲 دانلود نرم‌افزار و راهنمای اتصال" : MenuCommand,
            callbackPrefix + OpenAction) });

    /// <summary>Selects guide markup for messages sent to the buyer from a fulfilled tenant order.</summary>
    /// <param name="order">Stored tenant order used by the buyer's delivery or resend path.</param>
    /// <param name="downloadsEnabled">Live application-wide download switch at send time.</param>
    /// <returns>Guide markup for a purchase, or null for renewals and other order kinds.</returns>
    /// <remarks>Use only for buyer delivery. Owner and sales-assistant notifications have separate send paths.</remarks>
    internal static InlineKeyboardMarkup BuildTenantPurchaseMarkup(TenantBotOrder order, bool downloadsEnabled) =>
        string.Equals(order.OrderKind, TenantBotOrderKinds.Purchase, StringComparison.OrdinalIgnoreCase)
            ? BuildPurchaseMarkup(TenantPrefix, downloadsEnabled)
            : null;

    /// <summary>Opens the installed tutorial platform selector in a new Telegram message.</summary>
    /// <param name="botClient">The authorized current bot transport, never a default bot fallback.</param>
    /// <param name="chatId">Telegram chat requesting the connection guide.</param>
    /// <param name="callbackPrefix">Fixed owned or tenant callback prefix.</param>
    /// <param name="cancellationToken">Cancellation of the Telegram send.</param>
    /// <param name="backButton">Optional existing parent link for the owned miscellaneous tutorials menu.</param>
    /// <returns>A task that completes after the Android, iOS and Windows chooser is sent.</returns>
    /// <remarks>No download lookup occurs. Viewing a guide does not replace the reply keyboard or any account message.</remarks>
    /// <example><code>await ConnectionGuideFlow.SendPlatformMenuAsync(client, chatId, ConnectionGuideFlow.TenantPrefix, token);</code></example>
    internal static Task SendPlatformMenuAsync(ITelegramBotClient botClient, ChatId chatId, string callbackPrefix,
        CancellationToken cancellationToken, InlineKeyboardButton backButton = null)
    {
        var rows = TenantTutorialKinds.All.Select(kind => new[]
        {
            InlineKeyboardButton.WithCallbackData(ClientDownloadCallbacks.GetButtonLabel(GetPlatform(kind)), callbackPrefix + kind)
        });
        if (backButton != null)
            rows = rows.Append(new[] { backButton });
        return botClient.SendMessage(chatId, "📲 راهنمای اتصال\n\nسیستم‌عامل یا نرم‌افزار موردنظر خود را انتخاب کنید:",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: cancellationToken);
    }

    /// <summary>Routes a recognized guide callback to the platform chooser, available actions or built-in photo album.</summary>
    /// <param name="botClient">The current owned or tenant bot transport after its normal access checks.</param>
    /// <param name="chatId">Telegram chat receiving the standalone guide message or album.</param>
    /// <param name="callbackPrefix">Fixed owned or tenant guide prefix, supplied by the dispatcher.</param>
    /// <param name="action">Untrusted callback suffix. Only the open action and exact installed platform kinds are accepted.</param>
    /// <param name="downloadsEnabled">Current global switch, read after the callback has passed its access checks.</param>
    /// <param name="logger">Application logger for a missing local album. Only the relative asset directory is recorded.</param>
    /// <param name="cancellationToken">Cancellation of the Telegram send or existing album upload.</param>
    /// <returns>A task after navigation, album delivery, or an unavailable notice. An invalid action sends nothing.</returns>
    /// <remarks>Download uses the existing APPDL callback, which checks the live switch again before resolving a release.</remarks>
    /// <exception cref="OperationCanceledException">Telegram delivery or album upload is cancelled.</exception>
    internal static async Task SendActionAsync(ITelegramBotClient botClient, ChatId chatId, string callbackPrefix,
        string action, bool downloadsEnabled, ILogger logger, CancellationToken cancellationToken)
    {
        if (action == OpenAction)
        {
            await SendPlatformMenuAsync(botClient, chatId, callbackPrefix, cancellationToken);
            return;
        }

        if (TenantTutorialKinds.IsSupported(action))
        {
            var platform = GetPlatform(action);
            var rows = new List<InlineKeyboardButton[]>();
            if (downloadsEnabled)
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData("دانلود آخرین نسخه", ClientDownloadCallbacks.Build(platform)) });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("آموزش اتصال", callbackPrefix + LessonPrefix + action) });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("↩️ بازگشت", callbackPrefix + OpenAction) });
            await botClient.SendMessage(chatId, ClientDownloadCallbacks.GetButtonLabel(platform),
                replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: cancellationToken);
            return;
        }

        if (!action.StartsWith(LessonPrefix, StringComparison.Ordinal))
            return;

        var kind = action[LessonPrefix.Length..];
        // Callback data may select only an installed platform, never a directory or a download destination.
        if (!TenantTutorialKinds.IsSupported(kind))
            return;
        var assets = TenantTutorialAssetService.Resolve(kind);
        if (assets.IsAvailable)
        {
            await TenantTutorialAlbumSender.SendAsync(botClient, chatId, assets, logger, cancellationToken);
            return;
        }

        logger.LogWarning("Connection guide assets unavailable. kind={TutorialKind}, dir={RelativeDirectory}, status={AssetStatus}",
            kind, assets.RelativeDirectory, assets.Status);
        await botClient.SendMessage(chatId, "در حال حاضر فایل‌های آموزش در دسترس نیستند. لطفاً کمی بعد دوباره تلاش کنید.",
            cancellationToken: cancellationToken);
    }

    /// <summary>Pairs one installed tutorial kind with its existing official software download.</summary>
    /// <param name="kind">Exact supported platform kind from <see cref="TenantTutorialKinds"/>.</param>
    /// <returns>The release resolver platform for the same client covered by the tutorial.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tutorial kind does not identify a supported client.</exception>
    private static ClientDownloadPlatform GetPlatform(string kind) => kind switch
    {
        TenantTutorialKinds.Android => ClientDownloadPlatform.Android,
        TenantTutorialKinds.Ios => ClientDownloadPlatform.Ios,
        TenantTutorialKinds.Windows => ClientDownloadPlatform.Windows,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
