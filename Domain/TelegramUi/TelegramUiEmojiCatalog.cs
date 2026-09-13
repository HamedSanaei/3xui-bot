using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Adminbot.Domain.TelegramUi
{
    /// <summary>
    /// Conventional location of the source-controlled Telegram custom-emoji catalog beside the running assembly.
    /// </summary>
    /// <remarks>
    /// The path is resolved from <see cref="AppContext.BaseDirectory"/> rather than the process working directory so a
    /// systemd service started from any directory still loads the release artifact's own catalog. This is application
    /// content, not operator configuration: the path is deliberately not configuration-key driven, and the file contains
    /// no secrets.
    /// </remarks>
    public static class TelegramUiEmojiAssetPaths
    {
        /// <summary>Catalog folder relative to the loaded assembly directory.</summary>
        public const string RootRelativePath = "Assets/telegram-ui";

        /// <summary>Catalog file name inside <see cref="RootRelativePath"/>.</summary>
        public const string FileName = "emoji-map.json";

        /// <summary>Gets the absolute catalog path beside the loaded assembly.</summary>
        /// <returns>An absolute path that is safe to log because it contains no secret material.</returns>
        public static string AbsolutePath => Path.Combine(AppContext.BaseDirectory, "Assets", "telegram-ui", FileName);
    }

    /// <summary>
    /// Thrown when the Telegram custom-emoji catalog is missing or malformed.
    /// </summary>
    /// <remarks>
    /// The catalog is source-controlled release content, so a malformed asset is a deployment or programming error, not
    /// a runtime condition. Every loader failure surfaces through this single type so startup fails before any Telegram
    /// update can render a half-configured UI. The message identifies the asset path, the logical key when it is safe to
    /// name, and a stable reason code, and never contains a token, chat id, or Telegram response body.
    /// </remarks>
    public sealed class TelegramUiEmojiCatalogException : Exception
    {
        /// <summary>
        /// Creates a catalog validation failure.
        /// </summary>
        /// <param name="reasonCode">Stable closed-vocabulary reason code describing the failed validation.</param>
        /// <param name="assetPath">Asset path that failed validation; safe to display because it is not secret.</param>
        /// <param name="logicalKey">Logical catalog key involved, or <c>null</c> when the failure is not key-specific.</param>
        /// <param name="detail">Short human-readable detail that never contains secrets.</param>
        public TelegramUiEmojiCatalogException(string reasonCode, string assetPath, string logicalKey, string detail)
            : base(BuildMessage(reasonCode, assetPath, logicalKey, detail))
        {
            ReasonCode = reasonCode ?? "unknown";
            AssetPath = assetPath;
            LogicalKey = logicalKey;
        }

        /// <summary>Gets the stable reason code for this validation failure.</summary>
        public string ReasonCode { get; }

        /// <summary>Gets the asset path that failed validation.</summary>
        public string AssetPath { get; }

        /// <summary>Gets the logical key involved, or <c>null</c> when the failure is not key-specific.</summary>
        public string LogicalKey { get; }

        /// <summary>
        /// Builds the safe diagnostic text for one validation failure.
        /// </summary>
        /// <param name="reasonCode">Stable reason code.</param>
        /// <param name="assetPath">Asset path that failed validation.</param>
        /// <param name="logicalKey">Logical key involved, or <c>null</c>.</param>
        /// <param name="detail">Short non-secret detail.</param>
        /// <returns>Diagnostic text containing only the reason, path, optional key, and detail.</returns>
        private static string BuildMessage(string reasonCode, string assetPath, string logicalKey, string detail)
            => string.IsNullOrEmpty(logicalKey)
                ? $"Telegram UI emoji catalog rejected ({reasonCode}) at '{assetPath}': {detail}"
                : $"Telegram UI emoji catalog rejected ({reasonCode}) at '{assetPath}' for key '{logicalKey}': {detail}";
    }

    /// <summary>
    /// Immutable, order-independent lookup over the validated Telegram custom-emoji catalog.
    /// </summary>
    /// <remarks>
    /// One instance is created at startup, validated, and frozen. Lookups are ordinal and thread-safe, no mutable
    /// dictionary escapes, and unknown keys resolve to a fallback-only entry instead of throwing.
    /// </remarks>
    public sealed class TelegramUiEmojiCatalog : ITelegramUiEmojiCatalog
    {
        private readonly Dictionary<string, TelegramUiEmoji> _items;

        /// <summary>
        /// Creates a catalog over an already-validated set of entries.
        /// </summary>
        /// <param name="items">
        /// Validated entries keyed by logical catalog key. The dictionary is copied, so later mutation of the caller's
        /// collection cannot change this instance.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="items"/> is <c>null</c>.</exception>
        public TelegramUiEmojiCatalog(IEnumerable<TelegramUiEmoji> items)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            _items = new Dictionary<string, TelegramUiEmoji>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.Key))
                    continue;
                _items[item.Key] = item;
            }
        }

        /// <summary>Gets the number of validated entries in this catalog.</summary>
        public int Count => _items.Count;

        /// <inheritdoc />
        public TelegramUiEmoji Resolve(string key)
        {
            if (!string.IsNullOrEmpty(key) && _items.TryGetValue(key, out var found))
                return found;

            return new TelegramUiEmoji(key ?? string.Empty, string.Empty, null);
        }

        /// <inheritdoc />
        public bool TryResolve(string key, out TelegramUiEmoji emoji)
        {
            if (!string.IsNullOrEmpty(key) && _items.TryGetValue(key, out var found))
            {
                emoji = found;
                return true;
            }

            emoji = null;
            return false;
        }

        /// <inheritdoc />
        public string GetFallback(string key)
            => !string.IsNullOrEmpty(key) && _items.TryGetValue(key, out var found) ? found.Fallback : string.Empty;

        /// <inheritdoc />
        public bool TryGetCustomEmojiId(string key, out string customEmojiId)
        {
            if (!string.IsNullOrEmpty(key) && _items.TryGetValue(key, out var found) && found.HasCustomEmoji)
            {
                customEmojiId = found.CustomEmojiId;
                return true;
            }

            customEmojiId = null;
            return false;
        }

        /// <summary>
        /// Gets every validated logical key in stable catalog order.
        /// </summary>
        /// <returns>An ordinal-sorted snapshot of the logical keys.</returns>
        public IReadOnlyList<string> Keys()
        {
            var keys = new List<string>(_items.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }

    /// <summary>
    /// Strict loader and validator for <c>Assets/telegram-ui/emoji-map.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loader fails closed. A missing file, invalid JSON, unsupported version, unexpected shape, duplicate logical
    /// key, invalid key name, missing or empty fallback, or malformed custom-emoji identifier all raise
    /// <see cref="TelegramUiEmojiCatalogException"/> so application startup stops instead of running with a partially
    /// configured visual layer.
    /// </para>
    /// <para>
    /// Duplicate JSON properties are detected explicitly: <see cref="JsonDocument"/> preserves every property of an
    /// object, so the loader can reject a duplicated logical key rather than silently letting the last value win the way
    /// plain dictionary deserialization would.
    /// </para>
    /// <para>
    /// Custom-emoji identifiers are validated as canonical positive decimal strings using
    /// <see cref="BigInteger"/>, so the validation is not limited to <see cref="int"/> and rejects <c>0</c>, negative
    /// values, explicit signs, embedded characters, whitespace, and leading zeros. Identifiers are never invented here.
    /// </para>
    /// </remarks>
    public static class TelegramUiEmojiCatalogLoader
    {
        /// <summary>Only supported asset schema version.</summary>
        public const int SupportedVersion = 1;

        /// <summary>Reason code for a missing catalog file.</summary>
        public const string ReasonAssetMissing = "asset_missing";

        /// <summary>Reason code for a catalog whose root is not a JSON object.</summary>
        public const string ReasonInvalidRoot = "invalid_root";

        /// <summary>Reason code for unparsable JSON.</summary>
        public const string ReasonInvalidJson = "invalid_json";

        /// <summary>Reason code for a missing or non-numeric <c>version</c>.</summary>
        public const string ReasonInvalidVersion = "invalid_version";

        /// <summary>Reason code for a version other than <see cref="SupportedVersion"/>.</summary>
        public const string ReasonUnsupportedVersion = "unsupported_version";

        /// <summary>Reason code for a missing or non-object <c>items</c> node.</summary>
        public const string ReasonItemsMissing = "items_missing";

        /// <summary>Reason code for a duplicated logical key.</summary>
        public const string ReasonDuplicateKey = "duplicate_key";

        /// <summary>Reason code for a logical key that violates the naming rule.</summary>
        public const string ReasonInvalidKey = "invalid_key";

        /// <summary>Reason code for a required logical key that is absent from the asset.</summary>
        public const string ReasonMissingRequiredKey = "missing_required_key";

        /// <summary>Reason code for an item that is not a JSON object.</summary>
        public const string ReasonInvalidItemShape = "invalid_item_shape";

        /// <summary>Reason code for an item without a <c>fallback</c> member.</summary>
        public const string ReasonMissingFallback = "missing_fallback";

        /// <summary>Reason code for a fallback that is empty, whitespace-only, or contains control characters.</summary>
        public const string ReasonInvalidFallback = "invalid_fallback";

        /// <summary>Reason code for a malformed <c>customEmojiId</c>.</summary>
        public const string ReasonInvalidCustomEmojiId = "invalid_custom_emoji_id";

        /// <summary>
        /// Loads and validates the catalog from the conventional asset location beside the loaded assembly.
        /// </summary>
        /// <returns>A frozen, validated catalog instance.</returns>
        /// <exception cref="TelegramUiEmojiCatalogException">
        /// Thrown when the asset is missing or fails any documented validation rule.
        /// </exception>
        public static TelegramUiEmojiCatalog LoadFromDefaultAsset() => LoadFromFile(TelegramUiEmojiAssetPaths.AbsolutePath);

        /// <summary>
        /// Loads and validates the catalog from an explicit file path.
        /// </summary>
        /// <param name="path">
        /// Absolute or relative path of the catalog asset. The path is only used for reading and error reporting and is
        /// never written to by this loader.
        /// </param>
        /// <returns>A frozen, validated catalog instance.</returns>
        /// <exception cref="TelegramUiEmojiCatalogException">
        /// Thrown when the file does not exist or fails any documented validation rule.
        /// </exception>
        public static TelegramUiEmojiCatalog LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new TelegramUiEmojiCatalogException(ReasonAssetMissing, path ?? string.Empty, null, "no catalog path was supplied.");

            string json;
            try
            {
                if (!File.Exists(path))
                    throw new TelegramUiEmojiCatalogException(ReasonAssetMissing, path, null, "the catalog asset does not exist.");
                json = File.ReadAllText(path);
            }
            catch (TelegramUiEmojiCatalogException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TelegramUiEmojiCatalogException(ReasonAssetMissing, path, null, ex.GetType().Name);
            }

            return Parse(json, path);
        }

        /// <summary>
        /// Validates catalog JSON text and produces a frozen catalog instance.
        /// </summary>
        /// <param name="json">Raw catalog JSON text.</param>
        /// <param name="assetPath">Path label used in validation errors; never a secret.</param>
        /// <returns>A frozen, validated catalog instance.</returns>
        /// <exception cref="TelegramUiEmojiCatalogException">Thrown when any documented validation rule fails.</exception>
        public static TelegramUiEmojiCatalog Parse(string json, string assetPath = null)
        {
            var label = string.IsNullOrWhiteSpace(assetPath) ? TelegramUiEmojiAssetPaths.FileName : assetPath!;

            if (string.IsNullOrWhiteSpace(json))
                throw new TelegramUiEmojiCatalogException(ReasonInvalidJson, label, null, "the catalog content was empty.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json!);
            }
            catch (JsonException)
            {
                throw new TelegramUiEmojiCatalogException(ReasonInvalidJson, label, null, "the catalog content was not valid JSON.");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new TelegramUiEmojiCatalogException(ReasonInvalidRoot, label, null, "the catalog root must be a JSON object.");

                if (!root.TryGetProperty("version", out var versionElement) ||
                    versionElement.ValueKind != JsonValueKind.Number ||
                    !versionElement.TryGetInt32(out var version))
                {
                    throw new TelegramUiEmojiCatalogException(ReasonInvalidVersion, label, null, "the catalog must declare a numeric 'version'.");
                }

                if (version != SupportedVersion)
                    throw new TelegramUiEmojiCatalogException(ReasonUnsupportedVersion, label, null, $"version {version} is not supported.");

                if (!root.TryGetProperty("items", out var itemsElement))
                    throw new TelegramUiEmojiCatalogException(ReasonItemsMissing, label, null, "the catalog must declare an 'items' object.");

                if (itemsElement.ValueKind != JsonValueKind.Object)
                    throw new TelegramUiEmojiCatalogException(ReasonItemsMissing, label, null, "'items' must be a JSON object.");

                var entries = new Dictionary<string, TelegramUiEmoji>(StringComparer.Ordinal);
                foreach (var property in itemsElement.EnumerateObject())
                {
                    var key = property.Name;
                    if (!IsValidLogicalKey(key))
                        throw new TelegramUiEmojiCatalogException(ReasonInvalidKey, label, key, "logical keys must be trimmed lower_snake_case.");

                    // JsonDocument preserves duplicate members, so this is the only reliable duplicate check.
                    if (entries.ContainsKey(key))
                        throw new TelegramUiEmojiCatalogException(ReasonDuplicateKey, label, key, "the logical key is declared more than once.");

                    entries[key] = ReadEntry(label, key, property.Value);
                }

                foreach (var required in TelegramUiEmojiKeys.All)
                {
                    if (!entries.ContainsKey(required))
                        throw new TelegramUiEmojiCatalogException(ReasonMissingRequiredKey, label, required, "a required logical key is missing.");
                }

                return new TelegramUiEmojiCatalog(entries.Values);
            }
        }

        /// <summary>
        /// Validates one catalog item and converts it to an immutable entry.
        /// </summary>
        /// <param name="assetPath">Asset label used in validation errors.</param>
        /// <param name="key">Already-validated logical key.</param>
        /// <param name="value">Raw item element.</param>
        /// <returns>The validated immutable entry.</returns>
        /// <exception cref="TelegramUiEmojiCatalogException">Thrown when the item shape is invalid.</exception>
        private static TelegramUiEmoji ReadEntry(string assetPath, string key, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new TelegramUiEmojiCatalogException(ReasonInvalidItemShape, assetPath, key, "each item must be a JSON object.");

            if (!value.TryGetProperty("fallback", out var fallbackElement))
                throw new TelegramUiEmojiCatalogException(ReasonMissingFallback, assetPath, key, "each item must declare a 'fallback'.");

            if (fallbackElement.ValueKind != JsonValueKind.String)
                throw new TelegramUiEmojiCatalogException(ReasonInvalidFallback, assetPath, key, "'fallback' must be a string.");

            var fallback = fallbackElement.GetString() ?? string.Empty;
            if (fallback.Length == 0 || fallback.Trim().Length == 0 || ContainsControlCharacter(fallback))
                throw new TelegramUiEmojiCatalogException(ReasonInvalidFallback, assetPath, key, "'fallback' must be a non-empty, printable string.");

            var customEmojiId = ReadCustomEmojiId(assetPath, key, value);
            return new TelegramUiEmoji(key, fallback, customEmojiId);
        }

        /// <summary>
        /// Reads and validates the optional <c>customEmojiId</c> member of one catalog item.
        /// </summary>
        /// <param name="assetPath">Asset label used in validation errors.</param>
        /// <param name="key">Logical key being validated.</param>
        /// <param name="value">Raw item element.</param>
        /// <returns>The canonical identifier, or <c>null</c> when none is configured.</returns>
        /// <exception cref="TelegramUiEmojiCatalogException">Thrown when the member is present but malformed.</exception>
        /// <remarks>
        /// A missing member and an explicit JSON <c>null</c> are both accepted and mean "no curated identifier yet".
        /// Any other shape must be a canonical positive decimal string.
        /// </remarks>
        private static string ReadCustomEmojiId(string assetPath, string key, JsonElement value)
        {
            if (!value.TryGetProperty("customEmojiId", out var idElement) || idElement.ValueKind == JsonValueKind.Null)
                return null;

            if (idElement.ValueKind != JsonValueKind.String)
                throw new TelegramUiEmojiCatalogException(ReasonInvalidCustomEmojiId, assetPath, key, "'customEmojiId' must be a string or null.");

            var raw = idElement.GetString() ?? string.Empty;
            if (!IsCanonicalPositiveDecimal(raw))
                throw new TelegramUiEmojiCatalogException(ReasonInvalidCustomEmojiId, assetPath, key, "'customEmojiId' must be a canonical positive decimal string.");

            return raw;
        }

        /// <summary>
        /// Checks whether a logical key follows the documented naming rule.
        /// </summary>
        /// <param name="key">Candidate key.</param>
        /// <returns><c>true</c> for a non-empty, trimmed, lower_snake_case key.</returns>
        private static bool IsValidLogicalKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key!.Length == 0 || key.Length > 64)
                return false;

            if (!string.Equals(key, key.Trim(), StringComparison.Ordinal))
                return false;

            if (key[0] < 'a' || key[0] > 'z')
                return false;

            foreach (var character in key)
            {
                var isLowerLetter = character >= 'a' && character <= 'z';
                var isDigit = character >= '0' && character <= '9';
                if (!isLowerLetter && !isDigit && character != '_')
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks whether a custom-emoji identifier is a canonical positive decimal string.
        /// </summary>
        /// <param name="value">Candidate identifier.</param>
        /// <returns><c>true</c> only for a canonical positive decimal value.</returns>
        /// <remarks>
        /// Deliberately stricter than a numeric parse: explicit signs, whitespace, non-digits, zero, and leading zeros are
        /// all rejected so one identifier can never be written two different ways.
        /// </remarks>
        private static bool IsCanonicalPositiveDecimal(string value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length == 0)
                return false;

            if (value.Length > 32)
                return false;

            foreach (var character in value)
            {
                if (character < '0' || character > '9')
                    return false;
            }

            if (value[0] == '0')
                return false;

            if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return false;

            if (parsed <= BigInteger.Zero)
                return false;

            return string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
        }

        /// <summary>
        /// Checks whether a fallback contains any control character.
        /// </summary>
        /// <param name="value">Candidate fallback text.</param>
        /// <returns><c>true</c> when any character is a control character.</returns>
        private static bool ContainsControlCharacter(string value)
        {
            foreach (var character in value)
            {
                if (char.IsControl(character))
                    return true;
            }

            return false;
        }
    }
}
