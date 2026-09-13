using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// Ephemeral runtime capability state for one bot's premium-decorated Telegram payloads.
    /// </summary>
    /// <remarks>
    /// This is deliberately a small closed vocabulary. "Requested" premium mode can be configured, but Telegram may still
    /// reject a decorated payload, so the runtime keeps the last known definitive answer per bot.
    /// </remarks>
    public enum TelegramPremiumUiCapabilityState
    {
        /// <summary>No decorated payload has been observed for this bot yet.</summary>
        Unknown,

        /// <summary>A decorated payload was accepted by Telegram for this bot.</summary>
        Available,

        /// <summary>Telegram definitively rejected a decorated payload for this bot.</summary>
        Rejected
    }

    /// <summary>
    /// Read/write access to the ephemeral per-bot premium capability circuit.
    /// </summary>
    /// <remarks>
    /// Only definitive Telegram answers are recorded. Timeouts, rate limits, 5xx responses, DNS/TLS failures, and
    /// connection resets are ambiguous and must never be persisted as a rejection, because a network problem is not proof
    /// that a capability disappeared. Nothing here is written to configuration files.
    /// </remarks>
    public interface ITelegramPremiumUiRuntimeState
    {
        /// <summary>
        /// Gets the current capability state for one bot.
        /// </summary>
        /// <param name="botId">Internal BotId. Unknown ids report <see cref="TelegramPremiumUiCapabilityState.Unknown"/>.</param>
        /// <returns>The last known definitive capability state.</returns>
        TelegramPremiumUiCapabilityState GetState(string botId);

        /// <summary>
        /// Gets a value indicating whether this bot's premium decoration has been definitively rejected.
        /// </summary>
        /// <param name="botId">Internal BotId.</param>
        /// <returns><c>true</c> only after a definitive decorated rejection.</returns>
        bool IsRejected(string botId);

        /// <summary>
        /// Records that Telegram accepted a decorated payload for this bot.
        /// </summary>
        /// <param name="botId">Internal BotId.</param>
        void MarkAvailable(string botId);

        /// <summary>
        /// Records that Telegram definitively rejected a decorated payload for this bot.
        /// </summary>
        /// <param name="botId">Internal BotId.</param>
        /// <param name="reasonCode">
        /// Short closed-vocabulary reason code. It must never contain a token, chat id, callback payload, or Telegram
        /// response body.
        /// </param>
        void MarkRejected(string botId, string reasonCode);

        /// <summary>
        /// Clears local capability state for one bot.
        /// </summary>
        /// <param name="botId">Internal BotId.</param>
        /// <remarks>Used by tests and by explicit operator diagnostics; production never clears state as a side effect.</remarks>
        void Clear(string botId);

        /// <summary>
        /// Gets the sanitized reason code recorded with the current state, if any.
        /// </summary>
        /// <param name="botId">Internal BotId.</param>
        /// <returns>A short reason code, or <c>null</c> when nothing definitive was recorded.</returns>
        string GetReasonCode(string botId);

        /// <summary>
        /// Durably disables the persisted tenant premium preference after a definitive decorated rejection.
        /// </summary>
        /// <param name="botId">Internal BotId of the tenant storefront whose setting should be cleared.</param>
        /// <param name="reasonCode">Short closed-vocabulary reason code recorded in the runtime state.</param>
        /// <param name="cancellationToken">Cancels the database write and the runtime registry refresh.</param>
        /// <returns>
        /// <c>true</c> when a persisted <c>TenantPremiumUiEnabled=true</c> row was found and set to <c>false</c>;
        /// <c>false</c> when the row is absent, is not a tenant storefront, or was already disabled.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is the infrastructure that lets a storefront whose owner later loses Telegram Premium return to the
        /// classic UI without the owner having to disable anything manually. It is idempotent: repeated calls converge on
        /// the same disabled state, and a bot that is not a tenant storefront is never modified.
        /// </para>
        /// <para>
        /// This method is intentionally not wired into every customer send in the current phase. Callers may use it after
        /// a definitive decorated rejection where a normal fallback send succeeded.
        /// </para>
        /// </remarks>
        Task<bool> MarkTenantCapabilityRejectedAsync(string botId, string reasonCode, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Thread-safe in-memory implementation of the premium capability circuit with an optional durable tenant disable.
    /// </summary>
    /// <remarks>
    /// State is intentionally process-local. Owned-bot rejections reset on restart so an operator who fixes Telegram
    /// Premium or the emoji catalog can retry without editing any file. Tenant rejections additionally clear the persisted
    /// storefront preference, because that preference is the tenant's explicit opt-in and should not keep requesting a
    /// capability Telegram no longer grants.
    /// </remarks>
    public sealed class TelegramPremiumUiRuntimeState : ITelegramPremiumUiRuntimeState
    {
        private readonly ConcurrentDictionary<string, Entry> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly UserDbContextFactory _userDbContextFactory;
        private readonly BotRegistry _registry;
        private readonly BotClientProvider _clientProvider;

        /// <summary>
        /// Creates the runtime capability state service.
        /// </summary>
        /// <param name="userDbContextFactory">Factory used to load the exact tenant row before disabling it.</param>
        /// <param name="registry">Runtime registry refreshed after a durable tenant disable.</param>
        /// <param name="clientProvider">Client cache invalidated after a durable tenant disable so the change takes effect.</param>
        /// <exception cref="ArgumentNullException">Thrown when any dependency is <c>null</c>.</exception>
        public TelegramPremiumUiRuntimeState(
            UserDbContextFactory userDbContextFactory,
            BotRegistry registry,
            BotClientProvider clientProvider)
        {
            _userDbContextFactory = userDbContextFactory ?? throw new ArgumentNullException(nameof(userDbContextFactory));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        }

        /// <inheritdoc />
        public TelegramPremiumUiCapabilityState GetState(string botId)
            => !string.IsNullOrEmpty(botId) && _states.TryGetValue(botId!, out var entry)
                ? entry.State
                : TelegramPremiumUiCapabilityState.Unknown;

        /// <inheritdoc />
        public bool IsRejected(string botId) => GetState(botId) == TelegramPremiumUiCapabilityState.Rejected;

        /// <inheritdoc />
        public void MarkAvailable(string botId)
        {
            if (string.IsNullOrEmpty(botId))
                return;

            _states[botId!] = new Entry(TelegramPremiumUiCapabilityState.Available, null, DateTime.UtcNow);
        }

        /// <inheritdoc />
        public void MarkRejected(string botId, string reasonCode)
        {
            if (string.IsNullOrEmpty(botId))
                return;

            _states[botId!] = new Entry(
                TelegramPremiumUiCapabilityState.Rejected,
                string.IsNullOrWhiteSpace(reasonCode) ? "decorated_rejected" : reasonCode,
                DateTime.UtcNow);
        }

        /// <inheritdoc />
        public void Clear(string botId)
        {
            if (!string.IsNullOrEmpty(botId))
                _states.TryRemove(botId!, out _);
        }

        /// <inheritdoc />
        public string GetReasonCode(string botId)
            => !string.IsNullOrEmpty(botId) && _states.TryGetValue(botId!, out var entry) ? entry.ReasonCode : null;

        /// <inheritdoc />
        public async Task<bool> MarkTenantCapabilityRejectedAsync(string botId, string reasonCode, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(botId))
                return false;

            var changed = false;
            BotInstance row;
            await using (var db = _userDbContextFactory.CreateDbContext())
            {
                var loaded = await db.BotInstances.FirstOrDefaultAsync(
                    x => x.Id == botId && x.Type == BotInstanceTypes.Tenant, cancellationToken);

                if (loaded == null)
                    return false;

                // A tenant that never opted in, or already returned to classic mode, is left byte-identical: this keeps
                // repeated definitive rejections idempotent and never rewrites UpdatedAtUtc for an unrelated change.
                if (loaded.TenantPremiumUiEnabled)
                {
                    loaded.TenantPremiumUiEnabled = false;
                    loaded.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    changed = true;
                }

                // Keep the fully-loaded row so the registry refresh below rebuilds the complete runtime configuration
                // instead of replacing it with a partially populated instance.
                row = loaded;
            }

            _registry.Upsert(row);
            MarkRejected(botId, reasonCode);
            if (changed)
                _clientProvider.Invalidate(botId!);

            return changed;
        }

        /// <summary>
        /// Immutable capability entry persisted in memory for one bot.
        /// </summary>
        /// <param name="State">Last known definitive capability state.</param>
        /// <param name="ReasonCode">Short reason code recorded with the state, or <c>null</c>.</param>
        /// <param name="RecordedAtUtc">UTC time the state was recorded.</param>
        private sealed record Entry(
            TelegramPremiumUiCapabilityState State,
            string ReasonCode,
            DateTime RecordedAtUtc);
    }
}
