using System;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// One built message body together with the explicit Telegram entities that must accompany it.
    /// </summary>
    /// <param name="Text">
    /// Message text. In premium mode a custom emoji is represented by its ordinary Unicode placeholder here, and the
    /// matching <see cref="MessageEntity"/> carries the Telegram identifier.
    /// </param>
    /// <param name="Entities">
    /// Explicit entities ordered by ascending offset. The collection is never <c>null</c> and is empty for ordinary text,
    /// which is what keeps classic mode byte-identical to plain string sending.
    /// </param>
    /// <remarks>
    /// Custom emoji are expressed only through explicit entities. Callers must not also set a parse mode that would
    /// reinterpret the placeholder text, because Telegram entity offsets are relative to the raw message text.
    /// </remarks>
    public sealed record TelegramUiText(string Text, IReadOnlyList<MessageEntity> Entities)
    {
        /// <summary>
        /// Gets a value indicating whether this body carries at least one explicit entity.
        /// </summary>
        public bool HasEntities => Entities.Count > 0;
    }

    /// <summary>
    /// Builds message text that can carry Telegram custom emoji without ever breaking Persian text, ordinary emoji, or
    /// the classic non-premium experience.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offset strategy: Telegram expresses entity offsets and lengths in UTF-16 code units, which is exactly what
    /// <see cref="StringBuilder.Length"/> and <see cref="string.Length"/> already count in .NET. Offsets are therefore
    /// taken from the builder's current length and lengths from <see cref="string.Length"/> of the appended placeholder.
    /// Code points, <c>Rune</c> counts, grapheme clusters, and UTF-8 byte lengths are deliberately never used, because
    /// every one of them disagrees with Telegram for astral-plane emoji such as 😀 (a surrogate pair) and for
    /// variation-selector sequences such as ⚠️.
    /// </para>
    /// <para>
    /// In classic mode the builder appends the catalog fallback only and produces no entity, so the output remains
    /// ordinary Unicode text and callers can keep using it with any parse mode they already use.
    /// </para>
    /// <para>
    /// Entities are emitted in append order, which is already ascending by offset, so Telegram receives a valid,
    /// non-overlapping entity list.
    /// </para>
    /// </remarks>
    public sealed class TelegramUiTextBuilder
    {
        private readonly ITelegramUiEmojiCatalog _catalog;
        private readonly bool _premiumMode;
        private readonly StringBuilder _text = new();
        private readonly List<MessageEntity> _entities = new();

        /// <summary>
        /// Creates a message text builder for one message.
        /// </summary>
        /// <param name="catalog">Immutable emoji catalog used to resolve logical keys.</param>
        /// <param name="premiumMode">
        /// <c>true</c> only when the resolved premium visual mode is active for the executing bot. When <c>false</c> the
        /// builder produces plain text with no entities.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is <c>null</c>.</exception>
        public TelegramUiTextBuilder(ITelegramUiEmojiCatalog catalog, bool premiumMode = false)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _premiumMode = premiumMode;
        }

        /// <summary>Gets the number of UTF-16 code units appended so far.</summary>
        public int Length => _text.Length;

        /// <summary>
        /// Appends literal text exactly as supplied.
        /// </summary>
        /// <param name="text">Text to append; <c>null</c> is treated as an empty string.</param>
        /// <returns>This builder so calls can be chained.</returns>
        /// <remarks>
        /// The text is appended verbatim; this builder never escapes or rewrites caller text, so existing Persian
        /// wording and already-escaped HTML remain untouched.
        /// </remarks>
        public TelegramUiTextBuilder Append(string text)
        {
            if (!string.IsNullOrEmpty(text))
                _text.Append(text);

            return this;
        }

        /// <summary>
        /// Appends a newline.
        /// </summary>
        /// <returns>This builder so calls can be chained.</returns>
        public TelegramUiTextBuilder AppendLine()
        {
            _text.Append('\n');
            return this;
        }

        /// <summary>
        /// Appends literal text followed by a newline.
        /// </summary>
        /// <param name="text">Text to append; <c>null</c> appends only the newline.</param>
        /// <returns>This builder so calls can be chained.</returns>
        public TelegramUiTextBuilder AppendLine(string text)
        {
            Append(text);
            return AppendLine();
        }

        /// <summary>
        /// Appends one logical emoji entry from the catalog.
        /// </summary>
        /// <param name="emojiKey">Logical key from <see cref="TelegramUiEmojiKeys"/>.</param>
        /// <returns>This builder so calls can be chained.</returns>
        /// <remarks>
        /// <para>
        /// The ordinary Unicode fallback is always appended as the visible placeholder, which is what Telegram itself
        /// renders in place of a custom emoji and what remains visible when premium mode is unavailable.
        /// </para>
        /// <para>
        /// Only when premium mode is active AND the catalog holds a curated identifier for the key does this method add a
        /// <see cref="MessageEntityType.CustomEmoji"/> entity. No identifier is ever invented.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var body = new TelegramUiTextBuilder(catalog, premiumMode: true)
        ///     .AppendEmoji(TelegramUiEmojiKeys.Premium)
        ///     .Append(" ظاهر پریمیوم")
        ///     .Build();
        /// </code>
        /// </example>
        public TelegramUiTextBuilder AppendEmoji(string emojiKey)
        {
            var fallback = _catalog.GetFallback(emojiKey);
            if (fallback.Length == 0)
                return this;

            var offset = _text.Length;
            _text.Append(fallback);

            if (_premiumMode && _catalog.TryGetCustomEmojiId(emojiKey, out var customEmojiId))
            {
                _entities.Add(new MessageEntity
                {
                    Type = MessageEntityType.CustomEmoji,
                    Offset = offset,
                    Length = fallback.Length,
                    CustomEmojiId = customEmojiId
                });
            }

            return this;
        }

        /// <summary>
        /// Finalizes the message body.
        /// </summary>
        /// <returns>The accumulated text plus an ordered, possibly empty entity list.</returns>
        /// <remarks>
        /// The builder remains usable afterwards, so callers may keep appending; the returned record is an immutable
        /// snapshot of the current state.
        /// </remarks>
        public TelegramUiText Build()
            => new(_text.ToString(), _entities.Count == 0 ? Array.Empty<MessageEntity>() : _entities.ToArray());
    }
}
