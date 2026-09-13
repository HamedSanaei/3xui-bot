using Adminbot.Domain.TelegramUi;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>
/// Regression coverage for the shared Telegram button factory and its semantic tone mapping.
/// </summary>
/// <remarks>
/// Two invariants matter most here: adopting the factory in classic mode must not change any existing button, and a
/// missing curated custom-emoji identifier must degrade to the fallback label instead of producing a button with no
/// icon and no emoji. The tests also pin Telegram's documented rule that button colour is independent of Telegram
/// Premium, which is why colour is applied in premium mode even when the emoji identifier is missing.
/// </remarks>
public sealed class TelegramPremiumUiButtonTests
{
    private const string CustomId = "5368324170671202286";

    /// <summary>Builds a catalog whose probe entry carries one curated identifier.</summary>
    /// <param name="withCustomEmoji">Whether the entry should carry the curated identifier.</param>
    /// <returns>A catalog containing just the probe entry.</returns>
    private static TelegramUiEmojiCatalog Catalog(bool withCustomEmoji)
        => new(new[]
        {
            new TelegramUiEmoji(
                TelegramUiEmojiKeys.PremiumProbe,
                "✨",
                withCustomEmoji ? CustomId : null)
        });

    /// <summary>Classic mode prefixes the fallback and leaves every premium field unset.</summary>
    /// <remarks>
    /// This is the guarantee that lets existing keyboards adopt the factory without a visual change.
    /// </remarks>
    [Fact]
    public void Classic_mode_uses_fallback_text_and_no_decoration()
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Callback(
            "مدیریت",
            "cb",
            TelegramUiEmojiKeys.PremiumProbe,
            TelegramUiButtonTone.Primary,
            premiumMode: false);

        Assert.Equal("✨ مدیریت", button.Text);
        Assert.Null(button.IconCustomEmojiId);
        Assert.Null(button.Style);
        Assert.Equal("cb", button.CallbackData);
    }

    /// <summary>Classic mode never colours a button, even when a tone is requested.</summary>
    [Theory]
    [InlineData(TelegramUiButtonTone.Primary)]
    [InlineData(TelegramUiButtonTone.Success)]
    [InlineData(TelegramUiButtonTone.Danger)]
    public void Classic_mode_ignores_tone(TelegramUiButtonTone tone)
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Callback("خرید", "cb", tone: tone, premiumMode: false);

        Assert.Null(button.Style);
        Assert.Null(button.IconCustomEmojiId);
    }

    /// <summary>Premium mode with a curated identifier replaces the fallback with the custom emoji.</summary>
    /// <remarks>
    /// The label must not keep the fallback emoji: sending both would render two emoji on one button.
    /// </remarks>
    [Fact]
    public void Premium_mode_with_identifier_does_not_duplicate_the_fallback()
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Callback(
            "مدیریت",
            "cb",
            TelegramUiEmojiKeys.PremiumProbe,
            TelegramUiButtonTone.Primary,
            premiumMode: true);

        Assert.Equal("مدیریت", button.Text);
        Assert.Equal(CustomId, button.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Primary, button.Style);
        Assert.DoesNotContain("✨", button.Text, System.StringComparison.Ordinal);
    }

    /// <summary>Premium mode with a curated identifier still honours every semantic tone.</summary>
    /// <param name="tone">Requested tone.</param>
    /// <param name="expected">Telegram style that must be produced.</param>
    [Theory]
    [InlineData(TelegramUiButtonTone.Primary, KeyboardButtonStyle.Primary)]
    [InlineData(TelegramUiButtonTone.Success, KeyboardButtonStyle.Success)]
    [InlineData(TelegramUiButtonTone.Danger, KeyboardButtonStyle.Danger)]
    public void Premium_mode_maps_tones(TelegramUiButtonTone tone, KeyboardButtonStyle expected)
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Callback(
            "مدیریت", "cb", TelegramUiEmojiKeys.PremiumProbe, tone, premiumMode: true);

        Assert.Equal(expected, button.Style);
    }

    /// <summary>The default tone never sets a style, in either mode.</summary>
    [Fact]
    public void Default_tone_never_sets_a_style()
    {
        var factory = new TelegramUiButtonFactory(Catalog(true));

        Assert.Null(factory.Callback("مدیریت", "cb", TelegramUiEmojiKeys.PremiumProbe, premiumMode: true).Style);
        Assert.Null(factory.Callback("مدیریت", "cb", TelegramUiEmojiKeys.PremiumProbe, premiumMode: false).Style);
        Assert.Null(TelegramUiButtonFactory.ToStyle(TelegramUiButtonTone.Default));
    }

    /// <summary>Premium mode without a curated identifier falls back to the prefixed label but keeps the tone.</summary>
    /// <remarks>
    /// Colour is a plain Bot API field and does not require Telegram Premium, so premium visual mode applies it even when
    /// the emoji identifier is not curated yet. The button must never carry an identifier in that case.
    /// </remarks>
    [Fact]
    public void Premium_mode_without_identifier_keeps_fallback_text_and_tone()
    {
        var button = new TelegramUiButtonFactory(Catalog(false)).Callback(
            "مدیریت",
            "cb",
            TelegramUiEmojiKeys.PremiumProbe,
            TelegramUiButtonTone.Danger,
            premiumMode: true);

        Assert.Equal("✨ مدیریت", button.Text);
        Assert.Null(button.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Danger, button.Style);
    }

    /// <summary>An unknown emoji key never invents an emoji or an identifier.</summary>
    [Fact]
    public void Unknown_emoji_key_is_ignored()
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Callback(
            "مدیریت", "cb", "not_a_key", TelegramUiButtonTone.Success, premiumMode: true);

        Assert.Equal("مدیریت", button.Text);
        Assert.Null(button.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Success, button.Style);
    }

    /// <summary>A missing emoji key leaves the label bare.</summary>
    [Fact]
    public void Missing_emoji_key_leaves_the_label_bare()
    {
        var factory = new TelegramUiButtonFactory(Catalog(true));

        Assert.Equal("مدیریت", factory.Callback("مدیریت", "cb").Text);
        Assert.Equal("مدیریت", factory.Reply("مدیریت").Text);
    }

    /// <summary>An empty label with a fallback becomes the emoji alone rather than a stray leading space.</summary>
    [Fact]
    public void Empty_label_with_fallback_produces_the_emoji_alone()
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Callback("", "cb", TelegramUiEmojiKeys.PremiumProbe, premiumMode: false);

        Assert.Equal("✨", button.Text);
    }

    /// <summary>A URL button carries exactly the URL action field and keeps premium decoration.</summary>
    [Fact]
    public void Url_button_sets_only_the_url_action()
    {
        var button = new TelegramUiButtonFactory(Catalog(true)).Url(
            "پشتیبانی", "https://t.me/support", TelegramUiEmojiKeys.PremiumProbe, TelegramUiButtonTone.Primary, premiumMode: true);

        Assert.Equal("https://t.me/support", button.Url);
        Assert.Null(button.CallbackData);
        Assert.Equal(CustomId, button.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Primary, button.Style);
    }

    /// <summary>Reply-keyboard buttons follow the same decoration rules as inline buttons.</summary>
    [Fact]
    public void Reply_buttons_follow_the_same_rules()
    {
        var factory = new TelegramUiButtonFactory(Catalog(true));

        var classic = factory.Reply("فروشگاه", TelegramUiEmojiKeys.PremiumProbe, premiumMode: false);
        Assert.Equal("✨ فروشگاه", classic.Text);
        Assert.Null(classic.IconCustomEmojiId);
        Assert.Null(classic.Style);

        var premium = factory.Reply("فروشگاه", TelegramUiEmojiKeys.PremiumProbe, TelegramUiButtonTone.Primary, premiumMode: true);
        Assert.Equal("فروشگاه", premium.Text);
        Assert.Equal(CustomId, premium.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Primary, premium.Style);
    }

    /// <summary>The tone mapping is exhaustive and total.</summary>
    /// <param name="tone">Semantic tone.</param>
    /// <param name="expected">Expected Telegram style, or null for the default tone.</param>
    [Theory]
    [InlineData(TelegramUiButtonTone.Default, null)]
    [InlineData(TelegramUiButtonTone.Primary, KeyboardButtonStyle.Primary)]
    [InlineData(TelegramUiButtonTone.Success, KeyboardButtonStyle.Success)]
    [InlineData(TelegramUiButtonTone.Danger, KeyboardButtonStyle.Danger)]
    public void Tone_mapping_is_total(TelegramUiButtonTone tone, KeyboardButtonStyle? expected)
        => Assert.Equal(expected, TelegramUiButtonFactory.ToStyle(tone));
}
