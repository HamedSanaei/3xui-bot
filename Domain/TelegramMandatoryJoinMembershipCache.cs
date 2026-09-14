using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Adminbot.Domain
{
    /// <summary>
    /// Process-local, positive-only cache of successful Telegram mandatory-join membership checks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this type exists:
    /// A customer who passed mandatory join is re-verified before every later message and callback. Without a cache the
    /// same <c>GetChatMember</c> probe is repeated for every tap, so one active customer produces a steady stream of
    /// Telegram membership calls that occasionally time out and flood the operator channel. A short positive cache
    /// removes the repeated calls without ever weakening the gate.
    /// </para>
    /// <para>
    /// Fail-closed contract:
    /// only a complete successful evaluation for the exact bot, customer, and channel set is stored. Non-membership,
    /// local timeouts, unknown statuses, transport failures, API failures, and channel-access failures are never
    /// stored, so they are always re-checked against Telegram. A missing or expired entry therefore behaves exactly
    /// like the previous uncached implementation.
    /// </para>
    /// <para>
    /// Scope:
    /// the cache key is the exact runtime bot id plus the numeric Telegram user id plus the normalized channel set.
    /// Channel reordering, casing, and duplicate entries do not create a new entry, while a configured channel change
    /// automatically invalidates previous entries because the channel component of the key changes. Two bots, two
    /// customers, or two channel sets can never share one entry.
    /// </para>
    /// </remarks>
    public interface ITelegramMandatoryJoinMembershipCache
    {
        /// <summary>
        /// Looks up whether a complete successful membership evaluation is still valid for one bot, customer, and
        /// channel set.
        /// </summary>
        /// <param name="botId">
        /// Exact runtime bot id that performed the membership check, for example <c>vpnetiranbot</c> or
        /// <c>tenant-7405716665</c>. This is the internal configured id, never a Telegram bot id, chat id, or token.
        /// A blank value is treated as a missing key component and never matches.
        /// </param>
        /// <param name="telegramUserId">
        /// Numeric Telegram user id of the customer whose membership was verified. Usernames and display names must
        /// not be passed here. Non-positive values never match a stored entry.
        /// </param>
        /// <param name="channelIds">
        /// Normalized Telegram chat identifiers that were verified together. The order, casing, and duplicate entries
        /// of this sequence do not affect matching. An empty sequence never matches.
        /// </param>
        /// <returns>
        /// <c>true</c> only when the exact bot, customer, and channel set currently has a non-expired positive entry;
        /// otherwise <c>false</c>, which requires the caller to perform a real Telegram check.
        /// </returns>
        /// <remarks>
        /// This method has no side effects other than discarding an expired entry it happens to read. A caller must
        /// never treat <c>false</c> as proof of non-membership; it only means the caller must ask Telegram.
        /// </remarks>
        /// <example>
        /// <code>
        /// if (membershipCache.TryGetPositive(botId, userId, channelIds))
        ///     return true;
        /// </code>
        /// </example>
        bool TryGetPositive(string botId, long telegramUserId, IEnumerable<string> channelIds);

        /// <summary>
        /// Stores one complete successful membership evaluation for the exact bot, customer, and channel set.
        /// </summary>
        /// <param name="botId">
        /// Exact runtime bot id that performed the check. A blank value makes the call a no-op so an unresolved context
        /// can never create an undifferentiated shared entry.
        /// </param>
        /// <param name="telegramUserId">
        /// Numeric Telegram user id of the verified customer. Non-positive values make the call a no-op.
        /// </param>
        /// <param name="channelIds">
        /// The complete channel set that was verified successfully. An empty sequence makes the call a no-op, because
        /// no channel means no verified membership.
        /// </param>
        /// <remarks>
        /// Call this only after every configured channel returned an accepted membership status. Never call it for a
        /// non-member, a timeout, an unknown status, or any failure outcome, because a stored entry suppresses the next
        /// real check for the lifetime of the entry.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Reached only when every configured channel accepted the customer.
        /// membershipCache.RememberPositive(botId, userId, channelIds);
        /// </code>
        /// </example>
        void RememberPositive(string botId, long telegramUserId, IEnumerable<string> channelIds);
    }

    /// <summary>
    /// Default <see cref="ITelegramMandatoryJoinMembershipCache"/> implementation backed by one bounded process-local
    /// dictionary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lifetime:
    /// a single instance is registered as a singleton and injected into both the owned-bot service and the tenant
    /// storefront service, so the two flows share one view of recently verified memberships.
    /// </para>
    /// <para>
    /// Storage:
    /// one monotonic expiry timestamp per composite key. Because the value is a timestamp and not an entity, the cache
    /// holds no customer name, message text, channel title, bot token, or credential, and it survives a Telegram outage
    /// without leaking stale authorization beyond its own lifetime.
    /// </para>
    /// <para>
    /// Bounding:
    /// the map is cleared wholesale when it exceeds <see cref="MaxEntries"/> distinct keys. Membership entries are
    /// short-lived, so discarding them only costs a repeated Telegram check; it can never grant access that was not
    /// already verified.
    /// </para>
    /// </remarks>
    public sealed class TelegramMandatoryJoinMembershipCache : ITelegramMandatoryJoinMembershipCache
    {
        /// <summary>
        /// Positive lifetime of one stored membership result. Production value: thirty seconds.
        /// </summary>
        /// <remarks>
        /// The window is deliberately short. It is long enough to collapse the repeated checks produced by one customer
        /// tapping through a menu, and short enough that leaving a channel cannot stay invisible for long.
        /// </remarks>
        public static readonly TimeSpan PositiveLifetime = TimeSpan.FromSeconds(30);

        /// <summary>Maximum number of distinct composite keys retained before the dictionary is cleared.</summary>
        public const int MaxEntries = 8192;

        /// <summary>Separator that cannot appear in a Telegram chat identifier or a configured bot id.</summary>
        private const char KeySeparator = '\u001f';

        /// <summary>Composite key to monotonic expiry timestamp in milliseconds.</summary>
        private readonly ConcurrentDictionary<string, long> _positiveExpiryByKey = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the number of retained positive entries for diagnostics and regression verification.
        /// </summary>
        /// <remarks>
        /// Exposed for tests and local diagnosis only. A production caller must not make authorization decisions from
        /// this value.
        /// </remarks>
        public int Count => _positiveExpiryByKey.Count;

        /// <inheritdoc />
        public bool TryGetPositive(string botId, long telegramUserId, IEnumerable<string> channelIds)
            => TryGetPositive(botId, telegramUserId, channelIds, Environment.TickCount64);

        /// <summary>
        /// Looks up one positive membership entry against an explicit monotonic timestamp.
        /// </summary>
        /// <param name="botId">Exact runtime bot id that performed the membership check; blank never matches.</param>
        /// <param name="telegramUserId">Numeric Telegram user id of the customer; non-positive never matches.</param>
        /// <param name="channelIds">Normalized channel set that was verified together; empty never matches.</param>
        /// <param name="nowTicks">Monotonic millisecond timestamp used for expiry evaluation.</param>
        /// <returns><c>true</c> when a non-expired positive entry exists for the exact composite key.</returns>
        /// <remarks>
        /// This overload exists so regression tests can prove expiry deterministically without waiting thirty seconds.
        /// Production code always uses the ambient clock through the public overload.
        /// </remarks>
        internal bool TryGetPositive(string botId, long telegramUserId, IEnumerable<string> channelIds, long nowTicks)
        {
            var key = TryBuildKey(botId, telegramUserId, channelIds);
            if (key == null)
                return false;

            if (!_positiveExpiryByKey.TryGetValue(key, out var expiryTicks))
                return false;

            if (nowTicks - expiryTicks < 0)
                return true;

            // Expired: drop the entry so the next caller performs a real Telegram check.
            _positiveExpiryByKey.TryRemove(key, out _);
            return false;
        }

        /// <inheritdoc />
        public void RememberPositive(string botId, long telegramUserId, IEnumerable<string> channelIds)
            => RememberPositive(botId, telegramUserId, channelIds, Environment.TickCount64);

        /// <summary>
        /// Stores one verified membership result using an explicit monotonic timestamp.
        /// </summary>
        /// <param name="botId">Exact runtime bot id that performed the membership check; blank is a no-op.</param>
        /// <param name="telegramUserId">Numeric Telegram user id of the verified customer; non-positive is a no-op.</param>
        /// <param name="channelIds">Complete verified channel set; empty is a no-op.</param>
        /// <param name="nowTicks">Monotonic millisecond timestamp the entry starts from.</param>
        /// <remarks>
        /// This overload exists so regression tests can prove that a stored entry expires exactly at the configured
        /// lifetime without sleeping. Production code always uses the ambient clock.
        /// </remarks>
        internal void RememberPositive(string botId, long telegramUserId, IEnumerable<string> channelIds, long nowTicks)
        {
            var key = TryBuildKey(botId, telegramUserId, channelIds);
            if (key == null)
                return;

            if (_positiveExpiryByKey.Count >= MaxEntries)
                _positiveExpiryByKey.Clear();

            _positiveExpiryByKey[key] = nowTicks + (long)PositiveLifetime.TotalMilliseconds;
        }

        /// <summary>
        /// Builds the composite cache key for one bot, customer, and channel set.
        /// </summary>
        /// <param name="botId">Exact runtime bot id that performed the membership check.</param>
        /// <param name="telegramUserId">Numeric Telegram user id of the customer.</param>
        /// <param name="channelIds">Channel set that was verified together, in any order and casing.</param>
        /// <returns>
        /// A stable key that changes whenever the bot, the customer, or the configured channel set changes, or
        /// <c>null</c> when any key component is missing so the caller performs a real Telegram check instead.
        /// </returns>
        /// <remarks>
        /// Channel identifiers are trimmed, lowercased with the invariant culture, de-duplicated, and sorted with an
        /// ordinal comparer, so a reordered or re-cased configuration cannot produce a second entry for the same
        /// effective channel set. Only identifiers already configured by the operator are used, so no customer-supplied
        /// text can reach the key.
        /// </remarks>
        private static string TryBuildKey(string botId, long telegramUserId, IEnumerable<string> channelIds)
        {
            var normalizedBotId = (botId ?? string.Empty).Trim();
            if (normalizedBotId.Length == 0 || telegramUserId <= 0)
                return null;

            var channelKey = NormalizeChannelSet(channelIds);
            if (channelKey.Length == 0)
                return null;

            return normalizedBotId + KeySeparator +
                   telegramUserId.ToString(CultureInfo.InvariantCulture) + KeySeparator +
                   channelKey;
        }

        /// <summary>
        /// Produces a stable, order-independent representation of one configured channel set.
        /// </summary>
        /// <param name="channelIds">
        /// Channel identifiers from the active bot or tenant configuration. Entries may be blank and are ignored.
        /// </param>
        /// <returns>
        /// Comma-joined normalized channel identifiers, or an empty string when no usable channel exists.
        /// </returns>
        /// <remarks>
        /// The result is used only as a cache-key component. It is never logged and never rendered to a customer.
        /// </remarks>
        internal static string NormalizeChannelSet(IEnumerable<string> channelIds)
        {
            if (channelIds == null)
                return string.Empty;

            return string.Join(
                ",",
                channelIds
                    .Select(channel => (channel ?? string.Empty).Trim().ToLowerInvariant())
                    .Where(channel => channel.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(channel => channel, StringComparer.Ordinal));
        }
    }
}
