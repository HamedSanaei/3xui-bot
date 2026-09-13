using Adminbot.Domain.TelegramUi;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>
/// Regression coverage for premium-aware message text and Telegram custom-emoji entities.
/// </summary>
/// <remarks>
/// <para>
/// The highest-risk detail in this area is offset arithmetic. Telegram entity offsets and lengths are UTF-16 code units,
/// which is what <see cref="string.Length"/> already counts in .NET — but a naive implementation that used code points,
/// UTF-8 byte length, or a grapheme count would corrupt every entity that follows a surrogate pair or a variation
/// selector. These tests pin the correct behaviour for a single-unit emoji, a surrogate pair, and a
/// variation-selector sequence, with Persian text before and after the emoji.
/// </para>
/// <para>
/// The second invariant is that classic mode produces no entity at all, so callers keep plain Unicode output.
/// </para>
/// </remarks>
public sealed class TelegramPremiumUiTextTests
{
    private const string CustomId = "5368324170671202286";

    /// <summary>Builds a catalog with the supplied fallback and optional curated identifier.</summary>
    /// <param name="fallback">Visible placeholder text for the probe entry.</param>
    /// <param name="customEmojiId">Curated identifier, or null to model an uncurated entry.</param>
    /// <returns>A catalog containing just the probe entry.</returns>
    private static TelegramUiEmojiCatalog Catalog(string fallback, string? customEmojiId = null)
        => new(new[] { new TelegramUiEmoji(TelegramUiEmojiKeys.PremiumProbe, fallback, customEmojiId) });

    /// <summary>Classic mode appends the fallback and emits no entity.</summary>
    [Fact]
    public void Classic_mode_emits_plain_text_without_entities()
    {
        var body = new TelegramUiTextBuilder(Catalog("✨", CustomId), premiumMode: false)
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Append(" ظاهر پریمیوم")
            .Build();

        Assert.Equal("✨ ظاهر پریمیوم", body.Text);
        Assert.Empty(body.Entities);
        Assert.False(body.HasEntities);
    }

    /// <summary>Premium mode with a curated identifier emits one custom-emoji entity over the placeholder.</summary>
    /// <remarks>
    /// The placeholder stays in the visible text because that is exactly what Telegram replaces with the custom emoji.
    /// </remarks>
    [Fact]
    public void Premium_mode_emits_one_custom_emoji_entity()
    {
        var body = new TelegramUiTextBuilder(Catalog("✨", CustomId), premiumMode: true)
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Append(" ظاهر پریمیوم")
            .Build();

        Assert.Equal("✨ ظاهر پریمیوم", body.Text);
        var entity = Assert.Single(body.Entities);
        Assert.Equal(MessageEntityType.CustomEmoji, entity.Type);
        Assert.Equal(CustomId, entity.CustomEmojiId);
        Assert.Equal(0, entity.Offset);
        Assert.Equal(1, entity.Length);
    }

    /// <summary>Premium mode without a curated identifier emits no entity.</summary>
    [Fact]
    public void Premium_mode_without_identifier_emits_no_entity()
    {
        var body = new TelegramUiTextBuilder(Catalog("✨"), premiumMode: true)
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Build();

        Assert.Equal("✨", body.Text);
        Assert.Empty(body.Entities);
    }

    /// <summary>Persian text before the emoji shifts the entity offset by the exact UTF-16 length of that text.</summary>
    [Fact]
    public void Persian_text_before_the_emoji_shifts_the_offset()
    {
        const string prefix = "سلام ";

        var body = new TelegramUiTextBuilder(Catalog("✨", CustomId), premiumMode: true)
            .Append(prefix)
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Build();

        var entity = Assert.Single(body.Entities);
        Assert.Equal(prefix.Length, entity.Offset);
        Assert.Equal(1, entity.Length);
        Assert.StartsWith(prefix, body.Text, System.StringComparison.Ordinal);
    }

    /// <summary>An emoji before Persian text keeps offset zero and does not move because of the trailing text.</summary>
    [Fact]
    public void Emoji_before_persian_text_keeps_offset_zero()
    {
        const string suffix = " ظاهر پریمیوم فروشگاه";

        var body = new TelegramUiTextBuilder(Catalog("✨", CustomId), premiumMode: true)
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Append(suffix)
            .Build();

        var entity = Assert.Single(body.Entities);
        Assert.Equal(0, entity.Offset);
        Assert.EndsWith(suffix, body.Text, System.StringComparison.Ordinal);
    }

    /// <summary>Two custom emoji in one message get independent, non-overlapping offsets.</summary>
    [Fact]
    public void Two_custom_emoji_get_independent_offsets()
    {
        var catalog = new TelegramUiEmojiCatalog(new[]
        {
            new TelegramUiEmoji(TelegramUiEmojiKeys.PremiumProbe, "✨", CustomId),
            new TelegramUiEmoji(TelegramUiEmojiKeys.Card, "💳", "6001234567890123456")
        });

        var body = new TelegramUiTextBuilder(catalog, premiumMode: true)
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Append(" و ")
            .AppendEmoji(TelegramUiEmojiKeys.Card)
            .Build();

        Assert.Equal(2, body.Entities.Count);
        Assert.Equal(0, body.Entities[0].Offset);
        Assert.Equal(1, body.Entities[0].Length);
        // "✨ و " is four UTF-16 units: the emoji, a space, the Persian conjunction, and one more space.
        Assert.Equal(4, body.Entities[1].Offset);
        // The credit-card emoji is astral, so it occupies two UTF-16 units.
        Assert.Equal(2, body.Entities[1].Length);
        Assert.Equal("6001234567890123456", body.Entities[1].CustomEmojiId);
        Assert.True(body.Entities[0].Offset + body.Entities[0].Length <= body.Entities[1].Offset);
    }

    /// <summary>A surrogate-pair fallback reports its UTF-16 length, not its code-point count.</summary>
    /// <remarks>
    /// The window emoji is a single code point that occupies two UTF-16 units. Measuring it as one would move every later
    /// entity by one unit and corrupt the message.
    /// </remarks>
    [Fact]
    public void Surrogate_pair_fallback_uses_utf16_length()
    {
        const string window = "\U0001FA9Fa";

        var body = new TelegramUiTextBuilder(Catalog(window, CustomId), premiumMode: true)
            .Append("x")
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Build();

        var entity = Assert.Single(body.Entities);
        Assert.Equal(1, entity.Offset);
        Assert.Equal(window.Length, entity.Length);
        Assert.Equal(3, entity.Length);
    }

    /// <summary>A variation-selector fallback reports the combined UTF-16 length.</summary>
    /// <remarks>
    /// The warning sign is a base character plus a variation selector, so the entity must cover both units.
    /// </remarks>
    [Fact]
    public void Variation_selector_fallback_uses_utf16_length()
    {
        const string warning = "\u26A0\uFE0F";

        var body = new TelegramUiTextBuilder(Catalog(warning, CustomId), premiumMode: true)
            .Append("قبل ")
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Append(" بعد")
            .Build();

        var entity = Assert.Single(body.Entities);
        Assert.Equal("قبل ".Length, entity.Offset);
        Assert.Equal(warning.Length, entity.Length);
        Assert.Equal(2, entity.Length);
    }

    /// <summary>Entities remain ordered and inside the message bounds for mixed decorated and plain emoji.</summary>
    [Fact]
    public void Mixed_emoji_entities_remain_ordered_and_in_bounds()
    {
        var catalog = new TelegramUiEmojiCatalog(new[]
        {
            new TelegramUiEmoji(TelegramUiEmojiKeys.PremiumProbe, "🪟", CustomId),
            new TelegramUiEmoji(TelegramUiEmojiKeys.Card, "💳", null)
        });

        var body = new TelegramUiTextBuilder(catalog, premiumMode: true)
            .AppendEmoji(TelegramUiEmojiKeys.Card)
            .Append("-")
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Build();

        var entity = Assert.Single(body.Entities);
        Assert.Equal("💳-".Length, entity.Offset);
        Assert.Equal("🪟".Length, entity.Length);
        Assert.True(entity.Offset + entity.Length <= body.Text.Length);
    }

    /// <summary>An unknown emoji key appends nothing and never emits an entity.</summary>
    [Fact]
    public void Unknown_emoji_key_appends_nothing()
    {
        var body = new TelegramUiTextBuilder(Catalog("✨", CustomId), premiumMode: true)
            .Append("متن")
            .AppendEmoji("not_a_key")
            .Build();

        Assert.Equal("متن", body.Text);
        Assert.Empty(body.Entities);
    }

    /// <summary>AppendLine helpers keep the builder totals consistent for later emoji offsets.</summary>
    [Fact]
    public void Append_line_helpers_keep_offsets_consistent()
    {
        var body = new TelegramUiTextBuilder(Catalog("✨", CustomId), premiumMode: true)
            .AppendLine("خط اول")
            .AppendLine()
            .AppendEmoji(TelegramUiEmojiKeys.PremiumProbe)
            .Build();

        var entity = Assert.Single(body.Entities);
        Assert.Equal("خط اول\n\n".Length, entity.Offset);
    }
}
