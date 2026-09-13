using System.Collections.Generic;
using System.IO;
using System.Linq;
using Adminbot.Domain;
using Adminbot.Domain.TelegramUi;
using Xunit;

/// <summary>
/// Regression coverage for the Telegram premium-UI emoji catalog and the global owned-bot switch.
/// </summary>
/// <remarks>
/// <para>
/// These tests protect the fail-closed contract of the visual layer:
/// </para>
/// <list type="bullet">
/// <item>a missing or malformed release asset must stop startup instead of rendering a half-configured UI;</item>
/// <item>every declared logical key must exist, so feature code can reference constants safely;</item>
/// <item>a custom-emoji identifier can only be a canonical positive decimal string, because it is an opaque Telegram
/// value that must never be invented, guessed, or silently reinterpreted; and</item>
/// <item>the shipped production asset and configuration example stay truthful, with no fabricated identifiers.</item>
/// </list>
/// <para>No test performs a network call.</para>
/// </remarks>
public sealed class TelegramPremiumUiCatalogTests
{
    /// <summary>Builds the <c>items</c> object body for every required key.</summary>
    /// <param name="premiumProbeItem">Raw JSON item used for the required <c>premium_probe</c> entry.</param>
    /// <returns>A complete JSON object body containing every required logical key.</returns>
    private static string ItemsJson(string premiumProbeItem = """{"fallback":"✨","customEmojiId":null}""")
    {
        var parts = new List<string> { "\"premium_probe\":" + premiumProbeItem };
        foreach (var key in TelegramUiEmojiKeys.All.Where(x => x != TelegramUiEmojiKeys.PremiumProbe))
            parts.Add($"\"{key}\":{{\"fallback\":\"✨\",\"customEmojiId\":null}}");
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>Builds catalog JSON with an optional raw items override or schema version.</summary>
    /// <param name="items">Raw items JSON body; when omitted every required key is emitted.</param>
    /// <param name="version">Schema version to declare.</param>
    /// <returns>Catalog JSON text.</returns>
    private static string CatalogJson(string? items = null, int version = 1)
        => $"{{\"version\":{version},\"items\":{(items ?? ItemsJson())}}}";

    /// <summary>Asserts that catalog JSON is rejected with one specific reason code.</summary>
    /// <param name="json">Catalog JSON under test.</param>
    /// <param name="expectedReason">Expected closed-vocabulary reason code.</param>
    private static void AssertRejected(string? json, string expectedReason)
    {
        var exception = Assert.Throws<TelegramUiEmojiCatalogException>(() => TelegramUiEmojiCatalogLoader.Parse(json, "test-asset"));
        Assert.Equal(expectedReason, exception.ReasonCode);
    }

    /// <summary>A production configuration that omits the new key keeps owned-bot premium visuals disabled.</summary>
    /// <remarks>
    /// Guards against a deployment that upgrades the binary before the configuration file: the missing key must never
    /// enable a new visual mode, and it must never block startup.
    /// </remarks>
    [Fact]
    public void Missing_owned_bot_premium_key_defaults_to_disabled()
    {
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<AppConfig>("{\"adminsUserIds\":[]}")!;

        Assert.False(config.OwnedBotPremiumUiEnabled);
    }

    /// <summary>The shipped configuration example documents the switch explicitly as disabled.</summary>
    /// <remarks>
    /// The example file is what operators copy, so a missing or enabled key there would silently opt a fresh deployment
    /// into a new visual mode.
    /// </remarks>
    [Fact]
    public void Configuration_example_documents_the_switch_as_disabled()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json"));
        var text = File.ReadAllText(path);

        Assert.Contains("\"ownedBotPremiumUiEnabled\": false", text, System.StringComparison.Ordinal);
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<AppConfig>(text)!;
        Assert.False(config.OwnedBotPremiumUiEnabled);
    }

    /// <summary>The production asset loads from the build output and exposes every declared logical key.</summary>
    [Fact]
    public void Production_asset_loads_and_declares_every_required_key()
    {
        var catalog = TelegramUiEmojiCatalogLoader.LoadFromDefaultAsset();

        Assert.True(catalog.Count >= TelegramUiEmojiKeys.All.Count);
        foreach (var key in TelegramUiEmojiKeys.All)
        {
            Assert.True(catalog.TryResolve(key, out var emoji), $"missing logical key {key}");
            Assert.False(string.IsNullOrWhiteSpace(emoji!.Fallback), $"empty fallback for {key}");
        }
    }

    /// <summary>The production asset ships without curated identifiers so no value is fabricated.</summary>
    /// <remarks>
    /// This assertion is deliberately explicit. Curating a real identifier later is a deliberate, reviewable change to
    /// source-controlled content, and this test forces that change to be visible rather than accidental.
    /// </remarks>
    [Fact]
    public void Production_asset_contains_no_fabricated_custom_emoji_ids()
    {
        var catalog = TelegramUiEmojiCatalogLoader.LoadFromDefaultAsset();

        foreach (var key in TelegramUiEmojiKeys.All)
            Assert.False(catalog.TryGetCustomEmojiId(key, out _), $"unexpected curated identifier for {key}");
    }

    /// <summary>A missing catalog file fails closed.</summary>
    [Fact]
    public void Missing_asset_file_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-emoji-map-{System.Guid.NewGuid():N}.json");

        var exception = Assert.Throws<TelegramUiEmojiCatalogException>(() => TelegramUiEmojiCatalogLoader.LoadFromFile(path));

        Assert.Equal(TelegramUiEmojiCatalogLoader.ReasonAssetMissing, exception.ReasonCode);
        Assert.Contains(path, exception.Message, System.StringComparison.Ordinal);
    }

    /// <summary>Malformed JSON fails closed.</summary>
    [Fact]
    public void Malformed_json_is_rejected() => AssertRejected("{ not json", TelegramUiEmojiCatalogLoader.ReasonInvalidJson);

    /// <summary>A JSON root that is not an object fails closed.</summary>
    [Fact]
    public void Non_object_root_is_rejected() => AssertRejected("[1,2,3]", TelegramUiEmojiCatalogLoader.ReasonInvalidRoot);

    /// <summary>A missing or non-numeric schema version fails closed.</summary>
    [Fact]
    public void Missing_version_is_rejected() => AssertRejected("{\"items\":" + ItemsJson() + "}", TelegramUiEmojiCatalogLoader.ReasonInvalidVersion);

    /// <summary>Any schema version other than the supported one fails closed.</summary>
    [Fact]
    public void Unsupported_version_is_rejected() => AssertRejected(CatalogJson(version: 2), TelegramUiEmojiCatalogLoader.ReasonUnsupportedVersion);

    /// <summary>A missing items node fails closed.</summary>
    [Fact]
    public void Missing_items_node_is_rejected() => AssertRejected("{\"version\":1}", TelegramUiEmojiCatalogLoader.ReasonItemsMissing);

    /// <summary>A duplicated logical key fails closed instead of letting the last value win.</summary>
    /// <remarks>
    /// JSON documents preserve duplicate object members, so this is the exact bug that plain dictionary deserialization
    /// would hide by silently overwriting the first value.
    /// </remarks>
    [Fact]
    public void Duplicate_logical_key_is_rejected()
    {
        var body = string.Join(",", TelegramUiEmojiKeys.All.Select(x => $"\"{x}\":{{\"fallback\":\"✨\",\"customEmojiId\":null}}"));
        // The second "home" member lives INSIDE the items object, which is where duplicate detection must fire.
        var items = "{" + body + ",\"home\":{\"fallback\":\"🏠\",\"customEmojiId\":null}}";

        AssertRejected(CatalogJson(items), TelegramUiEmojiCatalogLoader.ReasonDuplicateKey);
    }

    /// <summary>A missing required logical key fails closed.</summary>
    [Fact]
    public void Missing_premium_probe_key_is_rejected()
    {
        var items = "{" + string.Join(",", TelegramUiEmojiKeys.All.Where(x => x != TelegramUiEmojiKeys.PremiumProbe)
            .Select(x => $"\"{x}\":{{\"fallback\":\"✨\",\"customEmojiId\":null}}")) + "}";

        AssertRejected(CatalogJson(items), TelegramUiEmojiCatalogLoader.ReasonMissingRequiredKey);
    }

    /// <summary>A logical key that violates the naming rule fails closed.</summary>
    [Theory]
    [InlineData("Home")]
    [InlineData("premium-probe")]
    [InlineData("_home")]
    [InlineData("")]
    public void Invalid_logical_key_names_are_rejected(string key)
    {
        var items = "\"" + key + "\":{\"fallback\":\"✨\",\"customEmojiId\":null}," +
            string.Join(",", TelegramUiEmojiKeys.All.Select(x => $"\"{x}\":{{\"fallback\":\"✨\",\"customEmojiId\":null}}"));

        AssertRejected(CatalogJson("{" + items + "}"), TelegramUiEmojiCatalogLoader.ReasonInvalidKey);
    }

    /// <summary>An item without a fallback fails closed.</summary>
    [Fact]
    public void Missing_fallback_is_rejected()
        => AssertRejected(CatalogJson(ItemsJson("{\"customEmojiId\":null}")), TelegramUiEmojiCatalogLoader.ReasonMissingFallback);

    /// <summary>An empty, whitespace-only, or control-character fallback fails closed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\u0007")]
    public void Invalid_fallback_is_rejected(string fallback)
        => AssertRejected(
            CatalogJson(ItemsJson("{\"fallback\":\"" + fallback + "\",\"customEmojiId\":null}")),
            TelegramUiEmojiCatalogLoader.ReasonInvalidFallback);

    /// <summary>An explicit JSON null identifier is accepted and means "not curated yet".</summary>
    [Fact]
    public void Null_custom_emoji_id_is_accepted()
    {
        var catalog = TelegramUiEmojiCatalogLoader.Parse(CatalogJson(), "test-asset");

        Assert.False(catalog.TryGetCustomEmojiId(TelegramUiEmojiKeys.PremiumProbe, out var id));
        Assert.Null(id);
    }

    /// <summary>A missing identifier member is accepted exactly like an explicit null.</summary>
    [Fact]
    public void Absent_custom_emoji_id_is_accepted()
    {
        var catalog = TelegramUiEmojiCatalogLoader.Parse(CatalogJson(ItemsJson("{\"fallback\":\"✨\"}")), "test-asset");

        Assert.False(catalog.TryGetCustomEmojiId(TelegramUiEmojiKeys.PremiumProbe, out _));
    }

    /// <summary>A canonical positive decimal identifier is accepted and preserved verbatim.</summary>
    /// <remarks>
    /// The value exceeds <see cref="int.MaxValue"/>, which proves the validation is not accidentally limited to
    /// <see cref="int"/>.
    /// </remarks>
    [Fact]
    public void Canonical_large_custom_emoji_id_is_accepted()
    {
        const string id = "5368324170671202286";
        var catalog = TelegramUiEmojiCatalogLoader.Parse(
            CatalogJson(ItemsJson($"{{\"fallback\":\"✨\",\"customEmojiId\":\"{id}\"}}")), "test-asset");

        Assert.True(catalog.TryGetCustomEmojiId(TelegramUiEmojiKeys.PremiumProbe, out var resolved));
        Assert.Equal(id, resolved);
    }

    /// <summary>Every non-canonical identifier shape fails closed.</summary>
    /// <param name="id">Raw identifier value inserted into the asset.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("00123")]
    [InlineData("12abc")]
    [InlineData(" ")]
    [InlineData(" 123")]
    [InlineData("123 ")]
    [InlineData("1.5")]
    [InlineData("1234567890123456789012345678901234")]
    public void Non_canonical_custom_emoji_ids_are_rejected(string id)
        => AssertRejected(
            CatalogJson(ItemsJson("{\"fallback\":\"✨\",\"customEmojiId\":\"" + id + "\"}")),
            TelegramUiEmojiCatalogLoader.ReasonInvalidCustomEmojiId);

    /// <summary>A non-string identifier type fails closed.</summary>
    [Fact]
    public void Numeric_custom_emoji_id_type_is_rejected()
        => AssertRejected(
            CatalogJson(ItemsJson("{\"fallback\":\"✨\",\"customEmojiId\":123}")),
            TelegramUiEmojiCatalogLoader.ReasonInvalidCustomEmojiId);

    /// <summary>An unknown logical key resolves to a safe fallback-only entry instead of throwing.</summary>
    /// <remarks>
    /// Rendering must never crash because a key was misspelled or a future key is missing from a deployment; it must
    /// degrade to the classic visual.
    /// </remarks>
    [Fact]
    public void Unknown_keys_resolve_safely()
    {
        var catalog = TelegramUiEmojiCatalogLoader.Parse(CatalogJson(), "test-asset");

        Assert.False(catalog.TryResolve("not_a_key", out _));
        Assert.Equal(string.Empty, catalog.GetFallback("not_a_key"));
        Assert.Equal(string.Empty, catalog.Resolve("not_a_key").Fallback);
        Assert.False(catalog.TryGetCustomEmojiId("not_a_key", out _));
    }

    /// <summary>The catalog is immutable once created: mutating the source collection changes nothing.</summary>
    [Fact]
    public void Catalog_is_immutable_after_construction()
    {
        var source = new List<TelegramUiEmoji> { new("home", "🏠", null) };
        var catalog = new TelegramUiEmojiCatalog(source);

        source.Clear();

        Assert.True(catalog.TryResolve("home", out var emoji));
        Assert.Equal("🏠", emoji!.Fallback);
        Assert.Equal(1, catalog.Count);
    }
}
