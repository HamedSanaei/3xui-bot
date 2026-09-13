using System;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// Resolves whether premium visuals are requested for the bot executing the current update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Premium mode is never inferred from a bot username, brand, owner id, callback payload, or message content. It is
    /// always the explicit persisted preference of the exact bot: the global
    /// <see cref="AppConfig.OwnedBotPremiumUiEnabled"/> switch for owned bots and
    /// <see cref="BotInstanceConfig.TenantPremiumUiEnabled"/> for a tenant storefront. The sales assistant bot is
    /// classic-only in this phase.
    /// </para>
    /// <para>
    /// "Requested" and "capable" are deliberately separate concepts. A bot may request premium visuals while the catalog
    /// has no curated identifier yet, or while the runtime circuit has recorded a definitive rejection; in both cases the
    /// rendering infrastructure degrades to classic output instead of breaking navigation.
    /// </para>
    /// </remarks>
    public interface ITelegramUiModeResolver
    {
        /// <summary>
        /// Gets a value indicating whether the bot with the given internal id requests premium visuals.
        /// </summary>
        /// <param name="botId">Internal BotId of the bot executing the update.</param>
        /// <returns><c>true</c> only when the exact bot is configured to request premium visuals.</returns>
        bool IsPremiumRequested(string botId);

        /// <summary>
        /// Gets a value indicating whether the given runtime bot configuration requests premium visuals.
        /// </summary>
        /// <param name="bot">Runtime bot configuration, or <c>null</c>.</param>
        /// <returns><c>true</c> only when the configuration is a tenant or owned bot with the feature enabled.</returns>
        bool IsPremiumRequested(BotInstanceConfig bot);

        /// <summary>
        /// Gets a value indicating whether premium visuals may actually be used for this bot right now.
        /// </summary>
        /// <param name="botId">Internal BotId of the bot executing the update.</param>
        /// <returns>
        /// <c>true</c> only when premium visuals are requested AND the runtime capability circuit has not recorded a
        /// definitive decorated rejection for this bot.
        /// </returns>
        /// <remarks>
        /// Catalog identifier availability is intentionally NOT part of this answer: it is checked per emoji key by the
        /// catalog itself so one missing identifier degrades only that decoration.
        /// </remarks>
        bool IsPremiumVisualsActive(string botId);
    }

    /// <summary>
    /// Default mode resolver backed by the runtime bot registry and the ephemeral capability circuit.
    /// </summary>
    public sealed class TelegramUiModeResolver : ITelegramUiModeResolver
    {
        private readonly AppConfig _appConfig;
        private readonly BotRegistry _registry;
        private readonly ITelegramPremiumUiRuntimeState _runtimeState;

        /// <summary>
        /// Creates the resolver.
        /// </summary>
        /// <param name="appConfig">Global application configuration holding the owned-bot premium switch.</param>
        /// <param name="registry">Runtime registry used to resolve an exact BotId to its configuration.</param>
        /// <param name="runtimeState">Ephemeral capability circuit consulted by the "active" answer.</param>
        /// <exception cref="ArgumentNullException">Thrown when any dependency is <c>null</c>.</exception>
        public TelegramUiModeResolver(
            AppConfig appConfig,
            BotRegistry registry,
            ITelegramPremiumUiRuntimeState runtimeState)
        {
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        }

        /// <inheritdoc />
        public bool IsPremiumRequested(string botId)
            => IsPremiumRequested(ResolveExact(botId));

        /// <inheritdoc />
        public bool IsPremiumRequested(BotInstanceConfig bot)
        {
            if (bot == null)
                return false;

            if (string.Equals(bot.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
                return bot.TenantPremiumUiEnabled;

            if (bot.IsSalesAssistant || string.Equals(bot.Type, BotInstanceTypes.SalesAssistant, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(bot.Type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
                return _appConfig.OwnedBotPremiumUiEnabled;

            return false;
        }

        /// <inheritdoc />
        public bool IsPremiumVisualsActive(string botId)
        {
            var bot = ResolveExact(botId);
            if (!IsPremiumRequested(bot))
                return false;

            return !_runtimeState.IsRejected(bot!.Id);
        }

        /// <summary>
        /// Resolves the exact runtime configuration for one internal BotId.
        /// </summary>
        /// <param name="botId">Internal BotId from the executing update context.</param>
        /// <returns>The exact matching configuration, or <c>null</c> when it is unknown.</returns>
        /// <remarks>
        /// The registry intentionally falls back to the default bot for unknown ids, so this helper re-checks the id and
        /// refuses a fallback match. Without that guard an unknown bot would silently inherit the default bot's premium
        /// preference.
        /// </remarks>
        private BotInstanceConfig ResolveExact(string botId)
        {
            if (string.IsNullOrWhiteSpace(botId))
                return null;

            var requested = botId!.Trim();
            var bot = _registry.GetById(requested);
            if (bot == null || !string.Equals(bot.Id, requested, StringComparison.OrdinalIgnoreCase))
                return null;

            return bot;
        }
    }
}
