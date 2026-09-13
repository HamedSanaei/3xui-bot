using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// Closed vocabulary describing the outcome of one tenant-bot premium capability probe.
    /// </summary>
    /// <remarks>
    /// The values keep a definitive Telegram rejection separate from an ambiguous network outcome so callers can decide
    /// safely: only <see cref="Supported"/> may authorize persisting a storefront opt-in, and every other value fails
    /// closed.
    /// </remarks>
    public enum TelegramPremiumUiProbeStatus
    {
        /// <summary>Telegram accepted the decorated payload; the bot can send custom emoji.</summary>
        Supported,

        /// <summary>The caller is not allowed to run this probe, or required inputs were missing.</summary>
        Unauthorized,

        /// <summary>The authorized storefront owner's own Telegram account does not have Premium, so no probe was sent.</summary>
        PremiumRequired,

        /// <summary>The catalog holds no curated custom-emoji identifier for the probe entry.</summary>
        CatalogUnavailable,

        /// <summary>The exact tenant bot transport could not be resolved.</summary>
        TransportUnavailable,

        /// <summary>The decorated payload was rejected while the plain baseline succeeded: premium decoration is unsupported.</summary>
        Rejected,

        /// <summary>The plain baseline payload was rejected too, so the transport itself is unusable.</summary>
        BaselineRejected,

        /// <summary>Telegram answered with a non-definitive API error such as 403/429/5xx; no conclusion was drawn.</summary>
        TransientFailure,

        /// <summary>The transmission outcome is unknown; the request may already have reached Telegram.</summary>
        Ambiguous
    }

    /// <summary>
    /// Input for one premium capability probe.
    /// </summary>
    /// <param name="BotId">
    /// Internal BotId of the exact tenant storefront whose transport must be probed. It must be the tenant bot's own id,
    /// never the owned, sales-assistant, logger, or default bot.
    /// </param>
    /// <param name="OwnerChatId">
    /// Private chat id that receives the preview. Production passes the persisted storefront owner's Telegram user id
    /// after authorization; the value must be positive.
    /// </param>
    /// <param name="PreviewEmojiKey">
    /// Logical catalog key whose curated identifier decorates the preview. Defaults to
    /// <see cref="TelegramUiEmojiKeys.PremiumProbe"/>.
    /// </param>
    /// <param name="OwnerIsPremium">
    /// The authorized owner's Telegram Premium status, read from the incoming callback after ownership has been proven.
    /// It defaults to <c>false</c> so a caller that forgets to assert it fails closed and sends no probe at all.
    /// </param>
    public sealed record TelegramPremiumUiCapabilityProbeRequest(
        string BotId,
        long OwnerChatId,
        string PreviewEmojiKey = TelegramUiEmojiKeys.PremiumProbe,
        bool OwnerIsPremium = false);

    /// <summary>
    /// Result of one premium capability probe.
    /// </summary>
    /// <param name="Status">Closed-vocabulary outcome.</param>
    /// <param name="ReasonCode">Short non-secret reason code for diagnostics and logging.</param>
    public sealed record TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus Status, string ReasonCode)
    {
        /// <summary>
        /// Gets a value indicating whether the decorated payload was proven acceptable.
        /// </summary>
        /// <remarks>Only this state may authorize persisting a storefront premium opt-in.</remarks>
        public bool IsSupported => Status == TelegramPremiumUiProbeStatus.Supported;
    }

    /// <summary>
    /// Resolves the exact Telegram transport for one internal BotId.
    /// </summary>
    /// <remarks>
    /// This is a narrow port over the existing <see cref="BotClientProvider"/> so probe classification can be exercised
    /// without a real Telegram connection. Production must never substitute another bot's client.
    /// </remarks>
    public interface ITelegramPremiumUiTransportResolver
    {
        /// <summary>
        /// Resolves the runtime Telegram client for one internal BotId.
        /// </summary>
        /// <param name="botId">Internal BotId of the exact bot whose token must be used.</param>
        /// <returns>The cached or newly created client for that exact bot.</returns>
        /// <exception cref="BotTransportUnavailableException">The bot is unknown, disabled, or has no token.</exception>
        ITelegramBotClient Resolve(string botId);
    }

    /// <summary>
    /// Production transport resolver backed by the shared runtime bot client cache.
    /// </summary>
    public sealed class TelegramPremiumUiTransportResolver : ITelegramPremiumUiTransportResolver
    {
        private readonly BotClientProvider _clients;

        /// <summary>
        /// Creates the resolver.
        /// </summary>
        /// <param name="clients">Shared runtime client cache; never disposed by this resolver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="clients"/> is <c>null</c>.</exception>
        public TelegramPremiumUiTransportResolver(BotClientProvider clients)
            => _clients = clients ?? throw new ArgumentNullException(nameof(clients));

        /// <inheritdoc />
        public ITelegramBotClient Resolve(string botId) => _clients.GetClient(botId);
    }

    /// <summary>
    /// Proves whether one tenant storefront bot is allowed by Telegram to send custom-emoji-decorated payloads.
    /// </summary>
    /// <remarks>Implemented by <see cref="TelegramPremiumUiCapabilityProbe"/>.</remarks>
    public interface ITelegramPremiumUiCapabilityProbe
    {
        /// <summary>
        /// Sends one inert preview through the exact tenant bot transport and classifies the outcome.
        /// </summary>
        /// <param name="request">Probe input identifying the exact tenant bot and the receiving owner chat.</param>
        /// <param name="cancellationToken">Caller cancellation; cancelling propagates and records no state.</param>
        /// <returns>A closed-vocabulary probe result. The method never throws for a Telegram or transport failure.</returns>
        Task<TelegramPremiumUiProbeResult> ProbeAsync(
            TelegramPremiumUiCapabilityProbeRequest request,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default capability probe: one decorated preview, with at most one plain baseline retry and no blind retries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: a bot may use custom emoji only when Telegram's own capability requirements are satisfied for the
    /// bot's owner, and neither the persisted project-owner relationship nor a customer's premium flag proves that.
    /// The tenant bot's own token is the final authority, so the probe is performed over that exact transport.
    /// </para>
    /// <para>
    /// Send policy:
    /// </para>
    /// <list type="bullet">
    /// <item>A decorated send that succeeds proves the capability.</item>
    /// <item>A Telegram 400 on the decorated request is definitive: the decorated request was not accepted, so exactly
    /// one plain baseline preview (identical text, identical callback semantics, identical style, no custom emoji) is
    /// attempted. Baseline success means the transport works and decoration is unsupported; baseline failure means the
    /// transport itself is broken and premium is never blamed.</item>
    /// <item>Every ambiguous outcome — timeout, caller-independent cancellation, connection reset, HTTP transport
    /// failure, 403, 429, or 5xx — performs NO baseline send, because the first request may already have been accepted
    /// and a second preview would be a duplicate message.</item>
    /// </list>
    /// <para>
    /// The probe is bounded by the shared <see cref="TelegramForegroundDeliveryPolicy"/> so the owner's interactive lane
    /// cannot be held by a stalled Telegram call. A local budget expiry is ambiguous, never a rejection.
    /// </para>
    /// <para>
    /// Logging is restricted to the internal BotId, the outcome, the exception type name, and Telegram's numeric error
    /// code. Tokens, chat text, callback payloads, and catalog contents are never logged.
    /// </para>
    /// </remarks>
    public sealed class TelegramPremiumUiCapabilityProbe : ITelegramPremiumUiCapabilityProbe
    {
        /// <summary>Inert callback payload of the preview button; it never represents a business action.</summary>
        public const string PreviewCallbackData = "PUI:preview";

        /// <summary>Visible preview text used by both the decorated and the baseline preview.</summary>
        public const string PreviewText = "✨ پیش‌نمایش ظاهر پریمیوم";

        /// <summary>Visible preview button label used by both the decorated and the baseline preview.</summary>
        public const string PreviewButtonLabel = "ظاهر پریمیوم";

        private const string DecoratedRejectedReason = "decorated_rejected";
        private const string BaselineRejectedReason = "baseline_rejected";

        private readonly ITelegramUiEmojiCatalog _catalog;
        private readonly ITelegramPremiumUiTransportResolver _transportResolver;
        private readonly TelegramForegroundDeliveryPolicy _foregroundPolicy;
        private readonly ILogger<TelegramPremiumUiCapabilityProbe> _logger;

        /// <summary>
        /// Creates the capability probe.
        /// </summary>
        /// <param name="catalog">Immutable catalog that supplies the curated probe identifier.</param>
        /// <param name="transportResolver">Resolver returning the exact tenant bot transport.</param>
        /// <param name="foregroundPolicy">
        /// Interactive delivery budget applied to the preview send. <c>null</c> uses
        /// <see cref="TelegramForegroundDeliveryPolicy.Production"/>.
        /// </param>
        /// <param name="logger">Logger for the safe structured outcome fields.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required dependency is <c>null</c>.</exception>
        public TelegramPremiumUiCapabilityProbe(
            ITelegramUiEmojiCatalog catalog,
            ITelegramPremiumUiTransportResolver transportResolver,
            TelegramForegroundDeliveryPolicy foregroundPolicy = null,
            ILogger<TelegramPremiumUiCapabilityProbe> logger = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _transportResolver = transportResolver ?? throw new ArgumentNullException(nameof(transportResolver));
            _foregroundPolicy = foregroundPolicy ?? TelegramForegroundDeliveryPolicy.Production;
            _logger = logger;
        }

        /// <summary>
        /// Gets a value indicating whether a callback payload is the inert premium preview button.
        /// </summary>
        /// <param name="callbackData">Raw callback payload from Telegram.</param>
        /// <returns><c>true</c> only for the exact preview payload.</returns>
        /// <remarks>
        /// The dispatcher uses this to answer the preview button without letting it reach any business handler, which is
        /// what makes the probe payload safe to press.
        /// </remarks>
        public static bool IsPreviewCallback(string callbackData)
            => string.Equals(callbackData, PreviewCallbackData, StringComparison.Ordinal);

        /// <inheritdoc />
        public async Task<TelegramPremiumUiProbeResult> ProbeAsync(
            TelegramPremiumUiCapabilityProbeRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BotId) || request.OwnerChatId <= 0)
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Unauthorized, "probe_input_invalid");

            var emojiKey = string.IsNullOrWhiteSpace(request.PreviewEmojiKey)
                ? TelegramUiEmojiKeys.PremiumProbe
                : request.PreviewEmojiKey;

            // The owner's own Premium status is a precondition, but it is never treated as proof: even when it is true a
            // real tenant-bot probe still has to succeed. When it is false no Telegram call is made at all.
            if (!request.OwnerIsPremium)
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.PremiumRequired, "owner_not_premium");

            if (!_catalog.TryGetCustomEmojiId(emojiKey, out var customEmojiId) || string.IsNullOrEmpty(customEmojiId))
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.CatalogUnavailable, "catalog_custom_emoji_missing");

            ITelegramBotClient client;
            try
            {
                client = _transportResolver.Resolve(request.BotId);
            }
            catch (Exception ex) when (ex is BotTransportUnavailableException or InvalidOperationException)
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.TransportUnavailable, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.TransportUnavailable, "bot_transport_unavailable");
            }

            var bounded = new ForegroundBoundedTelegramBotClient(client, _foregroundPolicy);

            try
            {
                await bounded.SendRequest(BuildPreview(request.OwnerChatId, decorated: true, customEmojiId), cancellationToken);
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Supported, null, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Supported, "decorated_accepted");
            }
            catch (TelegramForegroundDeliveryTimeoutException ex)
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Ambiguous, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Ambiguous, "decorated_timeout");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation is not a Telegram verdict and records no capability state.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Ambiguous, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Ambiguous, "decorated_cancelled");
            }
            catch (ApiRequestException ex)
            {
                if (ex.ErrorCode == 400)
                {
                    // Definitive: the decorated request was not accepted, so one plain baseline is safe to attempt.
                    return await TryBaselineAsync(request, bounded, customEmojiId, cancellationToken);
                }

                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.TransientFailure, ex.GetType().Name, ex.ErrorCode);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.TransientFailure, "telegram_api_error");
            }
            catch (Exception ex) when (TelegramPremiumUiFailureClassification.IsAmbiguousTransmission(ex))
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Ambiguous, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Ambiguous, "decorated_transport_failure");
            }
        }

        /// <summary>
        /// Attempts the single permitted plain baseline preview after a definitive decorated rejection.
        /// </summary>
        /// <param name="request">Original probe input.</param>
        /// <param name="bounded">Foreground-bounded client already used for the decorated attempt.</param>
        /// <param name="customEmojiId">Curated probe identifier, deliberately not used by the baseline payload.</param>
        /// <param name="cancellationToken">Caller cancellation token.</param>
        /// <returns>The classified probe result for the baseline attempt.</returns>
        private async Task<TelegramPremiumUiProbeResult> TryBaselineAsync(
            TelegramPremiumUiCapabilityProbeRequest request,
            ForegroundBoundedTelegramBotClient bounded,
            string customEmojiId,
            CancellationToken cancellationToken)
        {
            try
            {
                await bounded.SendRequest(BuildPreview(request.OwnerChatId, decorated: false, customEmojiId), cancellationToken);
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Rejected, null, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Rejected, DecoratedRejectedReason);
            }
            catch (TelegramForegroundDeliveryTimeoutException ex)
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Ambiguous, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Ambiguous, "baseline_timeout");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Ambiguous, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Ambiguous, "baseline_cancelled");
            }
            catch (ApiRequestException ex)
            {
                // Even a 400 here means the transport cannot deliver the plain form either, so premium is not blamed.
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.BaselineRejected, ex.GetType().Name, ex.ErrorCode);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.BaselineRejected, BaselineRejectedReason);
            }
            catch (Exception ex) when (TelegramPremiumUiFailureClassification.IsAmbiguousTransmission(ex))
            {
                LogOutcome(request.BotId, TelegramPremiumUiProbeStatus.Ambiguous, ex.GetType().Name, null);
                return new TelegramPremiumUiProbeResult(TelegramPremiumUiProbeStatus.Ambiguous, "baseline_transport_failure");
            }
        }

        /// <summary>
        /// Builds one preview request.
        /// </summary>
        /// <param name="ownerChatId">Private chat id receiving the preview.</param>
        /// <param name="decorated">Whether the button carries the curated custom-emoji identifier.</param>
        /// <param name="customEmojiId">Curated identifier; ignored when <paramref name="decorated"/> is <c>false</c>.</param>
        /// <returns>A single-message request whose button is inert.</returns>
        /// <remarks>
        /// Style is identical in both variants so the ONLY capability difference under test is custom-emoji decoration;
        /// a coloured button is not a premium-only feature and proving colour support would prove nothing about emoji.
        /// </remarks>
        private static SendMessageRequest BuildPreview(long ownerChatId, bool decorated, string customEmojiId)
        {
            var button = new InlineKeyboardButton
            {
                Text = PreviewButtonLabel,
                CallbackData = PreviewCallbackData,
                IconCustomEmojiId = decorated ? customEmojiId : null,
                Style = KeyboardButtonStyle.Primary
            };

            return new SendMessageRequest
            {
                ChatId = ownerChatId,
                Text = PreviewText,
                ReplyMarkup = new InlineKeyboardMarkup(new[] { new[] { button } })
            };
        }

        /// <summary>
        /// Writes one safe structured probe outcome line.
        /// </summary>
        /// <param name="botId">Internal BotId of the probed storefront.</param>
        /// <param name="status">Classified probe status.</param>
        /// <param name="errorType">Exception type name, or <c>null</c> when the attempt succeeded.</param>
        /// <param name="telegramErrorCode">Telegram numeric error code, or <c>null</c> when unavailable.</param>
        /// <remarks>The line never contains a token, chat id, owner identity, message text, or catalog contents.</remarks>
        private void LogOutcome(string botId, TelegramPremiumUiProbeStatus status, string errorType, int? telegramErrorCode)
        {
            if (_logger == null)
                return;

            if (status == TelegramPremiumUiProbeStatus.Supported)
            {
                _logger.LogInformation(
                    "Telegram premium UI capability probe completed. BotId={BotId} Outcome={Outcome}",
                    botId,
                    status);
                return;
            }

            _logger.LogWarning(
                "Telegram premium UI capability probe did not prove support. BotId={BotId} Outcome={Outcome} ErrorType={ErrorType} TelegramErrorCode={TelegramErrorCode}",
                botId,
                status,
                errorType,
                telegramErrorCode);
        }
    }
}
