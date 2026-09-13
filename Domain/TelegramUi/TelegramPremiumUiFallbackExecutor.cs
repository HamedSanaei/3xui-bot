using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Exceptions;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// Classifies Telegram send failures into definitive and ambiguous outcomes.
    /// </summary>
    /// <remarks>
    /// The distinction is the core safety rule of premium delivery. A definitive rejection means Telegram answered that
    /// the request itself was not accepted, so sending the plain form is safe. An ambiguous outcome means the request may
    /// already have been delivered, so a second send would create a duplicate message and must never happen
    /// automatically.
    /// </remarks>
    public static class TelegramPremiumUiFailureClassification
    {
        /// <summary>
        /// Gets a value indicating whether an exception is a definitive decorated-payload rejection.
        /// </summary>
        /// <param name="exception">Exception raised by a decorated send.</param>
        /// <returns><c>true</c> only for a Telegram 400, which proves the request was not accepted.</returns>
        /// <remarks>
        /// Only HTTP 400 qualifies. 403 (blocked or forbidden), 429 (rate limited), and 5xx (server side) all describe a
        /// transport or policy situation that says nothing about whether premium decoration is supported.
        /// </remarks>
        public static bool IsDefinitiveRejection(Exception exception)
            => exception is ApiRequestException apiRequestException && apiRequestException.ErrorCode == 400;

        /// <summary>
        /// Gets a value indicating whether an exception describes an unknown transmission outcome.
        /// </summary>
        /// <param name="exception">Exception raised by a send.</param>
        /// <returns><c>true</c> when the request may or may not have reached Telegram.</returns>
        /// <remarks>
        /// <para>
        /// <see cref="TelegramForegroundDeliveryTimeoutException"/> is included because it derives from
        /// <see cref="TimeoutException"/>: the local budget expiring says nothing about whether Telegram accepted the
        /// request. Caller cancellation is deliberately NOT included; callers decide how to propagate their own token.
        /// </para>
        /// <para>
        /// A definitive rejection is explicitly excluded even though <see cref="ApiRequestException"/> derives from
        /// <see cref="RequestException"/>, so the two predicates are disjoint and a caller that checks them in either
        /// order reaches the same decision.
        /// </para>
        /// </remarks>
        public static bool IsAmbiguousTransmission(Exception exception)
            => !IsDefinitiveRejection(exception)
                && exception is TelegramForegroundDeliveryTimeoutException
                    or TimeoutException
                    or HttpRequestException
                    or RequestException
                    or IOException
                    or SocketException;
    }

    /// <summary>
    /// Outcome of one premium-aware send performed through <see cref="TelegramPremiumUiFallbackExecutor"/>.
    /// </summary>
    public enum TelegramPremiumUiExecutionOutcome
    {
        /// <summary>Premium visuals were not active, so only the classic payload was sent.</summary>
        Classic,

        /// <summary>The decorated payload was sent successfully.</summary>
        Premium,

        /// <summary>The decorated payload was definitively rejected and the classic payload was sent once instead.</summary>
        PremiumFallback,

        /// <summary>The decorated attempt's outcome is unknown; nothing else was sent.</summary>
        Ambiguous
    }

    /// <summary>
    /// Executes a premium-decorated send with an at-most-once classic fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reusable shape Phase 2 callers should adopt: build the decorated payload and the classic payload, then
    /// let one component own the fallback decision instead of each feature re-implementing it.
    /// </para>
    /// <para>
    /// Rules:
    /// </para>
    /// <list type="bullet">
    /// <item>When premium visuals are not active for the bot, the premium factory is never invoked and only the classic
    /// factory runs.</item>
    /// <item>A definitive Telegram 400 marks the bot's capability rejected, durably disables a tenant storefront that
    /// opted in when a tenant id was supplied, and then runs the classic factory exactly once.</item>
    /// <item>An ambiguous outcome returns without sending anything else and without recording a rejection, because a
    /// network problem is not proof that the capability disappeared.</item>
    /// <item>Caller cancellation propagates unchanged.</item>
    /// <item>Exceptions thrown by the classic factory are not swallowed, because existing callers rely on their own
    /// failure handling.</item>
    /// </list>
    /// <para>
    /// This type performs no database work itself beyond the tenant auto-disable helper it delegates to, and it never
    /// logs, so it cannot leak message content.
    /// </para>
    /// </remarks>
    public sealed class TelegramPremiumUiFallbackExecutor
    {
        private readonly ITelegramUiModeResolver _modeResolver;
        private readonly ITelegramPremiumUiRuntimeState _runtimeState;

        /// <summary>
        /// Creates the fallback executor.
        /// </summary>
        /// <param name="modeResolver">Resolver answering whether premium visuals are active for a bot.</param>
        /// <param name="runtimeState">Capability circuit updated after a definitive rejection.</param>
        /// <exception cref="ArgumentNullException">Thrown when a dependency is <c>null</c>.</exception>
        public TelegramPremiumUiFallbackExecutor(
            ITelegramUiModeResolver modeResolver,
            ITelegramPremiumUiRuntimeState runtimeState)
        {
            _modeResolver = modeResolver ?? throw new ArgumentNullException(nameof(modeResolver));
            _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        }

        /// <summary>
        /// Executes one premium-aware send.
        /// </summary>
        /// <param name="premiumFactory">
        /// Sends the decorated payload exactly once when invoked. It must not send the classic form, because the executor
        /// owns that decision.
        /// </param>
        /// <param name="fallbackFactory">Sends the classic payload exactly once when invoked.</param>
        /// <param name="botId">Internal BotId whose premium mode and capability circuit apply.</param>
        /// <param name="tenantBotId">
        /// Tenant storefront BotId when the caller is a storefront, or <c>null</c>/empty for an owned bot. It is passed to
        /// the durable auto-disable helper after a definitive rejection.
        /// </param>
        /// <param name="cancellationToken">Caller cancellation token.</param>
        /// <returns>The classified execution outcome.</returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown when the caller's own token is cancelled during the decorated attempt.
        /// </exception>
        /// <exception cref="ArgumentNullException">Thrown when a factory is <c>null</c>.</exception>
        /// <example>
        /// <code>
        /// var outcome = await executor.ExecuteAsync(
        ///     premiumFactory: token =&gt; client.SendMessage(chatId, body.Text, entities: body.Entities, cancellationToken: token),
        ///     fallbackFactory: token =&gt; client.SendMessage(chatId, plainText, cancellationToken: token),
        ///     botId: bot.Id,
        ///     tenantBotId: bot.Type == BotInstanceTypes.Tenant ? bot.Id : null,
        ///     cancellationToken: cancellationToken);
        /// </code>
        /// </example>
        public async Task<TelegramPremiumUiExecutionOutcome> ExecuteAsync(
            Func<CancellationToken, Task> premiumFactory,
            Func<CancellationToken, Task> fallbackFactory,
            string botId,
            string tenantBotId = null,
            CancellationToken cancellationToken = default)
        {
            if (premiumFactory == null)
                throw new ArgumentNullException(nameof(premiumFactory));
            if (fallbackFactory == null)
                throw new ArgumentNullException(nameof(fallbackFactory));

            if (!_modeResolver.IsPremiumVisualsActive(botId))
            {
                await fallbackFactory(cancellationToken);
                return TelegramPremiumUiExecutionOutcome.Classic;
            }

            try
            {
                await premiumFactory(cancellationToken);
                _runtimeState.MarkAvailable(botId);
                return TelegramPremiumUiExecutionOutcome.Premium;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (TelegramPremiumUiFailureClassification.IsDefinitiveRejection(ex))
            {
                _runtimeState.MarkRejected(botId, "decorated_rejected");
                await TryDisableTenantStorefrontAsync(tenantBotId, cancellationToken);

                await fallbackFactory(cancellationToken);
                return TelegramPremiumUiExecutionOutcome.PremiumFallback;
            }
            catch (Exception ex) when (TelegramPremiumUiFailureClassification.IsAmbiguousTransmission(ex))
            {
                // The decorated request may already have been delivered. Re-sending as classic would duplicate it.
                return TelegramPremiumUiExecutionOutcome.Ambiguous;
            }
        }

        /// <summary>
        /// Applies the durable tenant auto-disable after a definitive decorated rejection.
        /// </summary>
        /// <param name="tenantBotId">Tenant storefront BotId, or <c>null</c>/empty for an owned bot.</param>
        /// <param name="cancellationToken">Caller cancellation token.</param>
        /// <remarks>
        /// A failure here is deliberately swallowed: the customer's classic message is more important than persisting the
        /// preference change, and the next definitive rejection retries it.
        /// </remarks>
        private async Task TryDisableTenantStorefrontAsync(string tenantBotId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tenantBotId))
                return;

            try
            {
                await _runtimeState.MarkTenantCapabilityRejectedAsync(tenantBotId, "decorated_rejected", cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Never block a customer-visible fallback because a preference write failed.
            }
        }
    }
}
