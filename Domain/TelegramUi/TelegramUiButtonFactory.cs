using System;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// Semantic tone of a Telegram keyboard button, independent of any provider or brand wording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Telegram's button colour itself does NOT require a Telegram Premium subscription; it is an ordinary Bot API field.
    /// This project nevertheless groups coloured buttons together with custom emoji under one "premium visual mode" so
    /// the visual identity of a bot is either fully classic or fully modern. The tone enum exists so feature code states
    /// intent ("danger", "primary") instead of repeating Telegram enum names.
    /// </para>
    /// <para>
    /// In classic mode the factory always leaves the style unset, so enabling this infrastructure cannot change existing
    /// customer keyboards.
    /// </para>
    /// </remarks>
    public enum TelegramUiButtonTone
    {
        /// <summary>No explicit tone; Telegram applies its own app-specific style.</summary>
        Default,

        /// <summary>Primary / informational action (Telegram "primary" blue).</summary>
        Primary,

        /// <summary>Affirmative action such as confirm or enable (Telegram "success" green).</summary>
        Success,

        /// <summary>Destructive action such as delete, disable, or cancel (Telegram "danger" red).</summary>
        Danger
    }

    /// <summary>
    /// Central builder for Telegram keyboard buttons that supports ordinary fallback emoji, curated custom emoji, and
    /// semantic button tones without duplicating Telegram request construction in feature code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Behaviour is intentionally asymmetric and fail-safe:
    /// </para>
    /// <list type="bullet">
    /// <item>Classic mode always renders <c>"{fallback} {label}"</c>, never sets a custom emoji identifier, and never
    /// sets a style, so a caller that adopts this builder in normal mode produces the classic UI.</item>
    /// <item>Premium mode renders the bare label with a custom emoji identifier when the catalog has a curated value for
    /// the requested key, and falls back to the prefixed label when it does not. A style is applied in premium mode even
    /// when the custom emoji identifier is missing, because colour and emoji are independent Telegram capabilities and
    /// the project's premium mode groups them.</item>
    /// <item>A fallback emoji is never combined with a custom emoji identifier, so the label can never show two
    /// emoji.</item>
    /// </list>
    /// <para>
    /// Exactly one action field is set on every produced button, preserving Telegram's button-type invariant.
    /// </para>
    /// </remarks>
    public sealed class TelegramUiButtonFactory
    {
        private readonly ITelegramUiEmojiCatalog _catalog;

        /// <summary>
        /// Creates a button factory over the immutable emoji catalog.
        /// </summary>
        /// <param name="catalog">Catalog used to resolve logical emoji keys; must not be <c>null</c>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is <c>null</c>.</exception>
        public TelegramUiButtonFactory(ITelegramUiEmojiCatalog catalog)
            => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        /// <summary>
        /// Maps a semantic tone to the Telegram button style used in premium visual mode.
        /// </summary>
        /// <param name="tone">Semantic tone requested by the caller.</param>
        /// <returns>The matching Telegram style, or <c>null</c> for <see cref="TelegramUiButtonTone.Default"/>.</returns>
        /// <remarks>
        /// Classic mode never calls this mapping, so no existing keyboard can gain a colour unintentionally.
        /// </remarks>
        public static KeyboardButtonStyle? ToStyle(TelegramUiButtonTone tone) => tone switch
        {
            TelegramUiButtonTone.Primary => KeyboardButtonStyle.Primary,
            TelegramUiButtonTone.Success => KeyboardButtonStyle.Success,
            TelegramUiButtonTone.Danger => KeyboardButtonStyle.Danger,
            _ => null
        };

        /// <summary>
        /// Creates an inline callback button.
        /// </summary>
        /// <param name="label">
        /// User-visible button text without any emoji prefix. Empty values are allowed and produce an emoji-only or blank
        /// button exactly as the caller supplied.
        /// </param>
        /// <param name="callbackData">
        /// Telegram callback payload of at most 64 bytes. It is passed through unchanged; this factory never signs,
        /// encodes, or trusts callback data.
        /// </param>
        /// <param name="emojiKey">Optional logical emoji key from <see cref="TelegramUiEmojiKeys"/>.</param>
        /// <param name="tone">Semantic tone; only applied in premium mode.</param>
        /// <param name="premiumMode">
        /// <c>true</c> only when the resolved premium visual mode is active for the executing bot.
        /// </param>
        /// <returns>A callback button with exactly one action field set.</returns>
        /// <example>
        /// <code>
        /// var button = buttons.Callback(
        ///     label: "فعال‌سازی ظاهر پریمیوم",
        ///     callbackData: callback,
        ///     emojiKey: TelegramUiEmojiKeys.Premium,
        ///     tone: TelegramUiButtonTone.Primary,
        ///     premiumMode: false);
        /// </code>
        /// </example>
        public InlineKeyboardButton Callback(
            string label,
            string callbackData,
            string emojiKey = null,
            TelegramUiButtonTone tone = TelegramUiButtonTone.Default,
            bool premiumMode = false)
        {
            var decoration = Resolve(label, emojiKey, tone, premiumMode);
            return new InlineKeyboardButton
            {
                Text = decoration.Text,
                CallbackData = callbackData,
                IconCustomEmojiId = decoration.IconCustomEmojiId,
                Style = decoration.Style
            };
        }

        /// <summary>
        /// Creates an inline URL button.
        /// </summary>
        /// <param name="label">User-visible button text without any emoji prefix.</param>
        /// <param name="url">Absolute HTTP, HTTPS, or tg:// URL opened when the button is pressed.</param>
        /// <param name="emojiKey">Optional logical emoji key from <see cref="TelegramUiEmojiKeys"/>.</param>
        /// <param name="tone">Semantic tone; only applied in premium mode.</param>
        /// <param name="premiumMode">Whether the resolved premium visual mode is active for the executing bot.</param>
        /// <returns>A URL button with exactly one action field set.</returns>
        /// <remarks>The caller remains responsible for producing the URL; this factory performs no validation of it.</remarks>
        public InlineKeyboardButton Url(
            string label,
            string url,
            string emojiKey = null,
            TelegramUiButtonTone tone = TelegramUiButtonTone.Default,
            bool premiumMode = false)
        {
            var decoration = Resolve(label, emojiKey, tone, premiumMode);
            return new InlineKeyboardButton
            {
                Text = decoration.Text,
                Url = url,
                IconCustomEmojiId = decoration.IconCustomEmojiId,
                Style = decoration.Style
            };
        }

        /// <summary>
        /// Creates a reply-keyboard button.
        /// </summary>
        /// <param name="label">User-visible button text without any emoji prefix.</param>
        /// <param name="emojiKey">Optional logical emoji key from <see cref="TelegramUiEmojiKeys"/>.</param>
        /// <param name="tone">Semantic tone; only applied in premium mode.</param>
        /// <param name="premiumMode">Whether the resolved premium visual mode is active for the executing bot.</param>
        /// <returns>A reply button carrying the same decoration rules as inline buttons.</returns>
        /// <remarks>
        /// Existing reply keyboards are deliberately not migrated to this method in the current phase; it exists so the
        /// later migration cannot invent a second, divergent decoration rule.
        /// </remarks>
        public KeyboardButton Reply(
            string label,
            string emojiKey = null,
            TelegramUiButtonTone tone = TelegramUiButtonTone.Default,
            bool premiumMode = false)
        {
            var decoration = Resolve(label, emojiKey, tone, premiumMode);
            return new KeyboardButton
            {
                Text = decoration.Text,
                IconCustomEmojiId = decoration.IconCustomEmojiId,
                Style = decoration.Style
            };
        }

        /// <summary>
        /// Computes the label, custom emoji identifier, and style for one requested button.
        /// </summary>
        /// <param name="label">Caller-supplied label without an emoji prefix.</param>
        /// <param name="emojiKey">Optional logical emoji key.</param>
        /// <param name="tone">Requested semantic tone.</param>
        /// <param name="premiumMode">Whether premium visual mode is active.</param>
        /// <returns>The decoration to apply to the produced button.</returns>
        /// <remarks>
        /// The fallback emoji is always the visible placeholder text when a custom emoji identifier is present, because
        /// Telegram renders the identifier in place of that placeholder. The two are never combined.
        /// </remarks>
        private (string Text, string IconCustomEmojiId, KeyboardButtonStyle? Style) Resolve(
            string label,
            string emojiKey,
            TelegramUiButtonTone tone,
            bool premiumMode)
        {
            var safeLabel = label ?? string.Empty;

            if (!premiumMode)
                return (PrefixWithFallback(safeLabel, emojiKey), null, null);

            if (!string.IsNullOrEmpty(emojiKey) && _catalog.TryGetCustomEmojiId(emojiKey!, out var customEmojiId))
                return (safeLabel, customEmojiId, ToStyle(tone));

            return (PrefixWithFallback(safeLabel, emojiKey), null, ToStyle(tone));
        }

        /// <summary>
        /// Prefixes a label with the catalog fallback emoji for one logical key.
        /// </summary>
        /// <param name="label">Label without an emoji prefix.</param>
        /// <param name="emojiKey">Optional logical emoji key.</param>
        /// <returns>
        /// <c>"{fallback} {label}"</c> when the key resolves to a non-empty fallback, otherwise the unchanged label.
        /// </returns>
        private string PrefixWithFallback(string label, string emojiKey)
        {
            var fallback = _catalog.GetFallback(emojiKey);
            if (string.IsNullOrEmpty(fallback))
                return label;

            return label.Length == 0 ? fallback : $"{fallback} {label}";
        }
    }
}
