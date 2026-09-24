using System;
using System.Collections.Generic;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// One resolved entry of the Telegram custom-emoji catalog: the logical key, the ordinary Unicode fallback that is
    /// always safe to render, and the optional Telegram custom-emoji identifier used only in premium visual mode.
    /// </summary>
    /// <param name="Key">
    /// Stable lower-snake-case logical key, for example <c>premium_probe</c>. It is a catalog identifier, never a
    /// Telegram identifier and never user input.
    /// </param>
    /// <param name="Fallback">
    /// Non-empty ordinary emoji (or text) that is always appended to a button label or message text. This value is what
    /// keeps the UI readable when premium mode is disabled, when no custom identifier is configured, or when Telegram
    /// has rejected premium decoration for the active bot.
    /// </param>
    /// <param name="CustomEmojiId">
    /// Telegram custom-emoji identifier as a positive decimal string, or <c>null</c> when the project has not yet
    /// curated a real value. It is never invented or scraped; only source-controlled catalog values appear here.
    /// </param>
    /// <remarks>
    /// This record is immutable and is the only shape feature code should depend on. Telegram entity identifiers are
    /// deliberately kept as strings because Telegram's identifiers are unsigned 64-bit values that must not be forced
    /// through <see cref="int"/>.
    /// </remarks>
    public sealed record TelegramUiEmoji(string Key, string Fallback, string CustomEmojiId)
    {
        /// <summary>
        /// Gets a value indicating whether this entry carries a usable Telegram custom-emoji identifier.
        /// </summary>
        /// <remarks>
        /// A <c>false</c> value means premium visual mode must fall back to <see cref="Fallback"/> for this entry; it is
        /// not an error, because the catalog ships with empty identifiers until real ones are curated.
        /// </remarks>
        public bool HasCustomEmoji => !string.IsNullOrEmpty(CustomEmojiId);
    }

    /// <summary>
    /// Read-only access to the single immutable Telegram custom-emoji catalog.
    /// </summary>
    /// <remarks>
    /// The catalog is loaded and validated once at startup and is never re-read per Telegram update. Resolvers return
    /// fallback-safe values rather than throwing so a rendering path can never crash because of a missing logical key.
    /// No mutable dictionary escapes this abstraction.
    /// </remarks>
    public interface ITelegramUiEmojiCatalog
    {
        /// <summary>
        /// Resolves one logical emoji key.
        /// </summary>
        /// <param name="key">
        /// Logical catalog key, normally one of the <see cref="TelegramUiEmojiKeys"/> constants. <c>null</c>, empty, and
        /// unknown values are allowed and never throw.
        /// </param>
        /// <returns>
        /// The configured entry, or a synthesized fallback-only entry with an empty fallback when the key is unknown.
        /// The returned record is immutable and safe to cache.
        /// </returns>
        TelegramUiEmoji Resolve(string key);

        /// <summary>
        /// Attempts to resolve one logical emoji key without synthesizing a value.
        /// </summary>
        /// <param name="key">Logical catalog key; <c>null</c> and unknown keys return <c>false</c>.</param>
        /// <param name="emoji">Receives the configured entry only when the key exists.</param>
        /// <returns><c>true</c> when the key exists in the catalog; otherwise <c>false</c>.</returns>
        bool TryResolve(string key, out TelegramUiEmoji emoji);

        /// <summary>
        /// Gets only the ordinary Unicode fallback for one logical key.
        /// </summary>
        /// <param name="key">Logical catalog key; unknown keys yield an empty string rather than an exception.</param>
        /// <returns>
        /// The configured fallback, or an empty string when the key is unknown. Callers must tolerate an empty result.
        /// </returns>
        string GetFallback(string key);

        /// <summary>
        /// Attempts to read the curated Telegram custom-emoji identifier for one logical key.
        /// </summary>
        /// <param name="key">Logical catalog key.</param>
        /// <param name="customEmojiId">
        /// Receives the positive decimal identifier only when the catalog entry exists and carries one.
        /// </param>
        /// <returns><c>true</c> only when a usable identifier is configured; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// A <c>false</c> result is the documented fail-closed signal for premium decoration. Callers must never
        /// substitute a guessed identifier.
        /// </remarks>
        bool TryGetCustomEmojiId(string key, out string customEmojiId);
    }

    /// <summary>
    /// Central logical emoji keys shared by Telegram premium-UI infrastructure.
    /// </summary>
    /// <remarks>
    /// Feature code must reference these constants instead of raw strings so a typo cannot silently degrade to a missing
    /// catalog entry. These constants identify logical catalog entries only; they never contain Telegram identifiers, so
    /// rotating a custom-emoji identifier never requires a code change.
    /// </remarks>
    public static class TelegramUiEmojiKeys
    {
        /// <summary>Logical entry used by the tenant-bot premium capability probe.</summary>
        public const string PremiumProbe = "premium_probe";

        /// <summary>Main menu / home entry.</summary>
        public const string Home = "home";

        /// <summary>Back navigation entry.</summary>
        public const string Back = "back";

        /// <summary>Affirmative confirm entry.</summary>
        public const string Confirm = "confirm";

        /// <summary>Negative cancel entry.</summary>
        public const string Cancel = "cancel";

        /// <summary>Settings entry.</summary>
        public const string Settings = "settings";

        /// <summary>Wallet entry.</summary>
        public const string Wallet = "wallet";

        /// <summary>Owned-bot wallet-view action.</summary>
        public const string WalletView = "wallet_view";

        /// <summary>Storefront / shop entry.</summary>
        public const string Shop = "shop";

        /// <summary>Premium appearance entry.</summary>
        public const string Premium = "premium";

        /// <summary>Support entry.</summary>
        public const string Support = "support";

        /// <summary>Download entry.</summary>
        public const string Download = "download";

        /// <summary>Android entry.</summary>
        public const string Android = "android";

        /// <summary>iOS entry.</summary>
        public const string Ios = "ios";

        /// <summary>Windows entry.</summary>
        public const string Windows = "windows";

        /// <summary>Warning entry.</summary>
        public const string Warning = "warning";

        /// <summary>Informational entry.</summary>
        public const string Info = "info";

        /// <summary>Card payment entry.</summary>
        public const string Card = "card";

        /// <summary>Cryptocurrency entry.</summary>
        public const string Crypto = "crypto";

        /// <summary>Gift entry.</summary>
        public const string Gift = "gift";

        /// <summary>Account-renewal entry.</summary>
        public const string Renew = "renew";

        /// <summary>Refresh entry.</summary>
        public const string Refresh = "refresh";

        /// <summary>
        /// Every required logical key. The catalog loader rejects an asset that omits any of these, so a deployment can
        /// never start with a half-populated catalog.
        /// </summary>
        /// <remarks>
        /// The order is stable and is used for diagnostics only; lookups always use <see cref="StringComparer.Ordinal"/>
        /// semantics through the catalog.
        /// </remarks>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            PremiumProbe,
            Home,
            Back,
            Confirm,
            Cancel,
            Settings,
            Wallet,
            WalletView,
            Shop,
            Premium,
            Support,
            Download,
            Android,
            Ios,
            Windows,
            Warning,
            Info,
            Card,
            Crypto,
            Gift,
            Renew,
            Refresh
        };
    }
}
