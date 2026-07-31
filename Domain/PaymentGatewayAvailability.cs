using System.Text;

namespace Adminbot.Domain;

/// <summary>
/// Stable identifiers for payment gateways whose ability to create new invoices can be changed at runtime.
/// </summary>
public enum PaymentGateway
{
    /// <summary>HooshPay rial invoices.</summary>
    HooshPay,

    /// <summary>Tetraminator rial invoices.</summary>
    Tetraminator,

    /// <summary>UniquePay rial invoices.</summary>
    UniquePay,

    /// <summary>NOWPayments cryptocurrency invoices.</summary>
    NowPayments
}

/// <summary>
/// Immutable process-local view of the four global gateway switches.
/// </summary>
/// <param name="HooshPayEnabled">Whether new HooshPay invoices may be created.</param>
/// <param name="TetraminatorEnabled">Whether new Tetraminator invoices may be created.</param>
/// <param name="UniquePayEnabled">Whether new UniquePay invoices may be created.</param>
/// <param name="NowPaymentsEnabled">Whether new NOWPayments invoices may be created.</param>
/// <param name="Revision">
/// Monotonically increasing process-local revision used to reject stale super-admin callback buttons.
/// </param>
public sealed record PaymentGatewayAvailabilitySnapshot(
    bool HooshPayEnabled,
    bool TetraminatorEnabled,
    bool UniquePayEnabled,
    bool NowPaymentsEnabled,
    long Revision)
{
    /// <summary>
    /// Returns whether the specified provider currently accepts creation of new invoices.
    /// </summary>
    /// <param name="gateway">Stable provider identifier selected by payment UI or callback routing.</param>
    /// <returns><c>true</c> when new invoices for <paramref name="gateway"/> are globally enabled.</returns>
    /// <example>
    /// <code>
    /// if (!snapshot.IsEnabled(PaymentGateway.UniquePay))
    ///     return;
    /// </code>
    /// </example>
    public bool IsEnabled(PaymentGateway gateway)
        => gateway switch
        {
            PaymentGateway.HooshPay => HooshPayEnabled,
            PaymentGateway.Tetraminator => TetraminatorEnabled,
            PaymentGateway.UniquePay => UniquePayEnabled,
            PaymentGateway.NowPayments => NowPaymentsEnabled,
            _ => false
        };
}

/// <summary>
/// Result of one requested global gateway state transition.
/// </summary>
/// <param name="Applied">Whether the requested state was durably written and published to runtime readers.</param>
/// <param name="Snapshot">Current snapshot after the operation, including when it was rejected.</param>
/// <param name="Message">Safe operational explanation suitable for a super-admin Telegram alert.</param>
public sealed record PaymentGatewayToggleResult(
    bool Applied,
    PaymentGatewayAvailabilitySnapshot Snapshot,
    string Message);

/// <summary>
/// Provides the live global invoice-creation switches and persists super-admin changes to configuration JSON.
/// </summary>
/// <remarks>
/// This service governs only creation of new invoices. Provider inquiry, callbacks, and settlement of previously
/// created payments intentionally continue while a gateway is disabled.
/// </remarks>
public interface IPaymentGatewayAvailability
{
    /// <summary>Gets an immutable, lock-free snapshot of the current gateway switches.</summary>
    PaymentGatewayAvailabilitySnapshot Snapshot { get; }

    /// <summary>
    /// Reports whether the non-switch credential, URL, and financial configuration required to create invoices is complete.
    /// </summary>
    /// <param name="gateway">Provider whose API credential and mandatory URLs should be checked.</param>
    /// <returns>
    /// <c>true</c> when required values captured at application startup are present and structurally valid; UniquePay
    /// additionally requires the configured buyer fee to remain the supported 12 percent.
    /// No credential value is returned and callers must not infer or display the secret.
    /// </returns>
    bool IsConfigured(PaymentGateway gateway);

    /// <summary>
    /// Atomically persists and immediately publishes one desired global gateway state.
    /// </summary>
    /// <param name="gateway">Provider whose root <c>*Enabled</c> JSON property should change.</param>
    /// <param name="enabled">Desired target state; disabling is always permitted.</param>
    /// <param name="expectedRevision">
    /// Revision encoded in the super-admin panel. A mismatch rejects stale or replayed callbacks without writing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the serialized file write.</param>
    /// <returns>
    /// A result containing the current snapshot and a safe user-facing explanation. Enabling is rejected when the
    /// provider configuration is incomplete.
    /// </returns>
    /// <remarks>
    /// Only the ASCII boolean token belonging to the selected root property is replaced. Existing secret values,
    /// Persian text, emoji, whitespace, casing, and unrelated bytes are preserved exactly.
    /// </remarks>
    Task<PaymentGatewayToggleResult> SetEnabledAsync(
        PaymentGateway gateway,
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe implementation of <see cref="IPaymentGatewayAvailability"/> backed by <c>configuration.json</c>.
/// </summary>
public sealed class PaymentGatewayAvailabilityService : IPaymentGatewayAvailability
{
    private readonly AppConfig _configuration;
    private readonly string _configurationPath;
    private readonly ILogger<PaymentGatewayAvailabilityService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private PaymentGatewayAvailabilitySnapshot _snapshot;

    /// <summary>
    /// Creates the live switch store from startup configuration values.
    /// </summary>
    /// <param name="configuration">
    /// Startup configuration. Credentials remain immutable for the process lifetime and are used only for readiness
    /// validation; secret values are never exposed by this service.
    /// </param>
    /// <param name="configurationPath">
    /// Absolute or content-root-relative path of the JSON file whose four root boolean properties are persisted.
    /// </param>
    /// <param name="logger">Operational logger used for safe gateway-toggle audit events.</param>
    public PaymentGatewayAvailabilityService(
        AppConfig configuration,
        string configurationPath,
        ILogger<PaymentGatewayAvailabilityService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _configurationPath = Path.GetFullPath(configurationPath ?? throw new ArgumentNullException(nameof(configurationPath)));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshot = new PaymentGatewayAvailabilitySnapshot(
            configuration.HooshPayEnabled,
            configuration.TetraminatorEnabled,
            configuration.UniquePayEnabled,
            configuration.NowPaymentsEnabled,
            Revision: 1);
    }

    /// <inheritdoc />
    public PaymentGatewayAvailabilitySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public bool IsConfigured(PaymentGateway gateway)
        => gateway switch
        {
            PaymentGateway.HooshPay =>
                HasSecret(_configuration.HooshPayApiKey) &&
                HasSecret(_configuration.HooshPayIpnSecretKey) &&
                IsAbsoluteHttpUrl(_configuration.HooshPayIpnUrl) &&
                IsAbsoluteHttpUrl(_configuration.HooshPayReturnUrl),
            PaymentGateway.Tetraminator =>
                HasSecret(_configuration.TetraminatorApiKey) &&
                IsAbsoluteHttpUrl(_configuration.TetraminatorApiBaseUrl) &&
                IsAbsoluteHttpUrl(_configuration.TetraminatorCallbackUrl),
            PaymentGateway.UniquePay =>
                HasSecret(_configuration.UniquePayBusinessToken) &&
                IsAbsoluteHttpUrl(_configuration.UniquePayBaseUrl) &&
                IsAbsoluteHttpUrl(_configuration.UniquePayReturnUrl) &&
                _configuration.UniquePayFeePercent == 12m,
            PaymentGateway.NowPayments =>
                HasSecret(_configuration.NowPaymentApiKey) &&
                HasSecret(_configuration.IpnSecretKey) &&
                IsAbsoluteHttpUrl(_configuration.NowpaymentSuccessUrl) &&
                IsAbsoluteHttpUrl(_configuration.NowpaymentCancelUrl) &&
                IsAbsoluteHttpUrl(_configuration.NowpaymentIpnUrl),
            _ => false
        };

    /// <inheritdoc />
    public async Task<PaymentGatewayToggleResult> SetEnabledAsync(
        PaymentGateway gateway,
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = Snapshot;
            if (expectedRevision != current.Revision)
            {
                return new PaymentGatewayToggleResult(
                    false,
                    current,
                    "این پنل قدیمی شده است؛ وضعیت جدید نمایش داده شد.");
            }

            if (enabled && !IsConfigured(gateway))
            {
                return new PaymentGatewayToggleResult(
                    false,
                    current,
                    "کانفیگ این درگاه ناقص است و تا ثبت کلید و آدرس‌های لازم روشن نمی‌شود.");
            }

            if (current.IsEnabled(gateway) == enabled)
                return new PaymentGatewayToggleResult(true, current, "وضعیت درگاه از قبل همین مقدار بود.");

            var propertyName = GetConfigurationPropertyName(gateway);
            await RootBooleanJsonFileEditor.SetAsync(
                _configurationPath,
                propertyName,
                enabled,
                cancellationToken);

            var next = current with
            {
                HooshPayEnabled = gateway == PaymentGateway.HooshPay ? enabled : current.HooshPayEnabled,
                TetraminatorEnabled = gateway == PaymentGateway.Tetraminator ? enabled : current.TetraminatorEnabled,
                UniquePayEnabled = gateway == PaymentGateway.UniquePay ? enabled : current.UniquePayEnabled,
                NowPaymentsEnabled = gateway == PaymentGateway.NowPayments ? enabled : current.NowPaymentsEnabled,
                Revision = current.Revision + 1
            };
            Volatile.Write(ref _snapshot, next);

            _logger.LogInformation(
                "Global payment gateway state changed. gateway={Gateway}, enabled={Enabled}, revision={Revision}",
                gateway,
                enabled,
                next.Revision);

            return new PaymentGatewayToggleResult(
                true,
                next,
                enabled ? "درگاه روشن شد و تغییر در کانفیگ ذخیره شد." : "درگاه خاموش شد و تغییر در کانفیگ ذخیره شد.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to persist global payment gateway state. gateway={Gateway}", gateway);
            return new PaymentGatewayToggleResult(false, Snapshot, "ذخیره فایل کانفیگ ناموفق بود؛ وضعیت runtime تغییر نکرد.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied while persisting global payment gateway state. gateway={Gateway}", gateway);
            return new PaymentGatewayToggleResult(false, Snapshot, "دسترسی نوشتن فایل کانفیگ وجود ندارد؛ وضعیت runtime تغییر نکرد.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "Invalid configuration JSON prevented global gateway persistence. gateway={Gateway}", gateway);
            return new PaymentGatewayToggleResult(false, Snapshot, "ساختار فایل کانفیگ معتبر نیست؛ وضعیت runtime تغییر نکرد.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Maps a stable gateway identifier to its exact camel-case root property in <c>configuration.json</c>.
    /// </summary>
    /// <param name="gateway">Gateway selected by the super-admin panel.</param>
    /// <returns>Exact case-sensitive root property name persisted for the provider.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an unknown enum value.</exception>
    private static string GetConfigurationPropertyName(PaymentGateway gateway)
        => gateway switch
        {
            PaymentGateway.HooshPay => "hooshPayEnabled",
            PaymentGateway.Tetraminator => "tetraminatorEnabled",
            PaymentGateway.UniquePay => "uniquePayEnabled",
            PaymentGateway.NowPayments => "nowPaymentsEnabled",
            _ => throw new ArgumentOutOfRangeException(nameof(gateway), gateway, "Unknown payment gateway.")
        };

    /// <summary>
    /// Checks whether a secret-shaped startup value is non-empty without returning or logging it.
    /// </summary>
    /// <param name="value">Credential read from startup configuration; it may be null or whitespace.</param>
    /// <returns><c>true</c> only when a non-whitespace value exists.</returns>
    private static bool HasSecret(string value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Checks whether a configured provider or return URL is an absolute HTTP(S) address.
    /// </summary>
    /// <param name="value">Configured URL; null, relative, and non-HTTP schemes are rejected.</param>
    /// <returns><c>true</c> for absolute HTTP or HTTPS URLs.</returns>
    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// Performs byte-preserving updates of one root JSON boolean and replaces the destination file atomically.
/// </summary>
public static class RootBooleanJsonFileEditor
{
    /// <summary>
    /// Sets one case-sensitive root boolean property without reserializing unrelated JSON content.
    /// </summary>
    /// <param name="path">Existing UTF-8 JSON file path. The containing directory must be writable.</param>
    /// <param name="propertyName">Exact ASCII root property name, without quotes.</param>
    /// <param name="value">Boolean value to persist.</param>
    /// <param name="cancellationToken">Cancellation token checked before read and write operations.</param>
    /// <returns>A task that completes after the replacement file is durably moved over the original.</returns>
    /// <remarks>
    /// The method replaces only the four or five bytes of an existing <c>true</c>/<c>false</c> token. When the key is
    /// absent it inserts one ASCII root property immediately before the root closing brace. A temporary file in the
    /// same directory is flushed with write-through semantics before <see cref="File.Replace(string,string,string)"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// await RootBooleanJsonFileEditor.SetAsync(
    ///     "./Data/configuration.json",
    ///     "uniquePayEnabled",
    ///     true,
    ///     cancellationToken);
    /// </code>
    /// </example>
    public static async Task SetAsync(
        string path,
        string propertyName,
        bool value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (propertyName.Any(ch => ch > 0x7f || !(char.IsLetterOrDigit(ch) || ch == '_')))
            throw new ArgumentException("The root property name must contain only ASCII letters, digits, or underscore.", nameof(propertyName));

        var fullPath = Path.GetFullPath(path);
        var original = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var updated = ReplaceOrInsertRootBoolean(original, propertyName, value);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Configuration directory could not be resolved.");
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(updated, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Produces new JSON bytes by editing one boolean token at object depth one.
    /// </summary>
    /// <param name="source">Original UTF-8 JSON bytes, including any BOM, formatting, Persian text, and emoji.</param>
    /// <param name="propertyName">Exact ASCII root property name.</param>
    /// <param name="value">Boolean value to encode as lowercase JSON.</param>
    /// <returns>Updated bytes with every unrelated byte copied verbatim.</returns>
    /// <exception cref="InvalidDataException">Thrown when the document has no root object or the target is not boolean.</exception>
    internal static byte[] ReplaceOrInsertRootBoolean(byte[] source, string propertyName, bool value)
    {
        ArgumentNullException.ThrowIfNull(source);
        var property = Encoding.ASCII.GetBytes(propertyName);
        var replacement = value ? "true"u8.ToArray() : "false"u8.ToArray();
        var depth = 0;
        var inString = false;
        var escaped = false;
        var rootClosingBrace = -1;

        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == (byte)'\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == (byte)'"')
                    inString = false;
                continue;
            }

            if (current == (byte)'"')
            {
                if (depth == 1 && MatchesProperty(source, index + 1, property))
                {
                    var propertyEnd = index + 1 + property.Length;
                    if (propertyEnd < source.Length && source[propertyEnd] == (byte)'"')
                    {
                        var tokenStart = FindBooleanValueStart(source, propertyEnd + 1);
                        var tokenLength = MatchBooleanLength(source, tokenStart);
                        if (tokenLength == 0)
                            throw new InvalidDataException($"Root JSON property '{propertyName}' is not a boolean.");
                        return Splice(source, tokenStart, tokenLength, replacement);
                    }
                }

                inString = true;
                continue;
            }

            if (current == (byte)'{')
                depth++;
            else if (current == (byte)'}')
            {
                if (depth == 1)
                    rootClosingBrace = index;
                depth--;
            }
        }

        if (rootClosingBrace < 0)
            throw new InvalidDataException("Configuration JSON does not contain a complete root object.");

        var newline = DetectNewline(source);
        var beforeBrace = rootClosingBrace - 1;
        while (beforeBrace >= 0 && IsWhitespace(source[beforeBrace]))
            beforeBrace--;
        var needsComma = beforeBrace >= 0 && source[beforeBrace] != (byte)'{';
        var insertionText =
            $"{(needsComma ? "," : string.Empty)}{newline}  \"{propertyName}\": {(value ? "true" : "false")}{newline}";
        return Splice(source, rootClosingBrace, 0, Encoding.UTF8.GetBytes(insertionText));
    }

    /// <summary>
    /// Checks an exact ASCII property name at the supplied byte offset.
    /// </summary>
    /// <param name="source">Original JSON bytes.</param>
    /// <param name="offset">First byte after the opening quote.</param>
    /// <param name="property">ASCII property-name bytes.</param>
    /// <returns><c>true</c> when all property bytes match case-sensitively.</returns>
    private static bool MatchesProperty(byte[] source, int offset, byte[] property)
    {
        if (offset + property.Length >= source.Length)
            return false;
        for (var i = 0; i < property.Length; i++)
        {
            if (source[offset + i] != property[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Locates the first non-whitespace value byte following a root property name and colon.
    /// </summary>
    /// <param name="source">Original JSON bytes.</param>
    /// <param name="offset">Byte immediately after the property closing quote.</param>
    /// <returns>Offset of the first boolean token byte.</returns>
    /// <exception cref="InvalidDataException">Thrown when the colon or value is missing.</exception>
    private static int FindBooleanValueStart(byte[] source, int offset)
    {
        while (offset < source.Length && IsWhitespace(source[offset]))
            offset++;
        if (offset >= source.Length || source[offset] != (byte)':')
            throw new InvalidDataException("Root JSON property is missing its colon.");
        offset++;
        while (offset < source.Length && IsWhitespace(source[offset]))
            offset++;
        if (offset >= source.Length)
            throw new InvalidDataException("Root JSON property is missing its value.");
        return offset;
    }

    /// <summary>
    /// Returns the length of a lowercase JSON boolean token at the supplied offset.
    /// </summary>
    /// <param name="source">Original JSON bytes.</param>
    /// <param name="offset">Candidate token start.</param>
    /// <returns>Four for <c>true</c>, five for <c>false</c>, or zero for another token.</returns>
    private static int MatchBooleanLength(byte[] source, int offset)
    {
        if (MatchesAsciiToken(source, offset, "true"u8))
            return 4;
        if (MatchesAsciiToken(source, offset, "false"u8))
            return 5;
        return 0;
    }

    /// <summary>
    /// Checks one fixed ASCII token within a byte array.
    /// </summary>
    /// <param name="source">Original bytes.</param>
    /// <param name="offset">Candidate token start.</param>
    /// <param name="token">ASCII token bytes.</param>
    /// <returns><c>true</c> when the complete token matches.</returns>
    private static bool MatchesAsciiToken(byte[] source, int offset, ReadOnlySpan<byte> token)
    {
        if (offset < 0 || offset + token.Length > source.Length)
            return false;
        return source.AsSpan(offset, token.Length).SequenceEqual(token);
    }

    /// <summary>
    /// Creates a byte array with one source range replaced by supplied bytes.
    /// </summary>
    /// <param name="source">Original bytes.</param>
    /// <param name="offset">Start of the range to replace.</param>
    /// <param name="length">Number of original bytes to remove.</param>
    /// <param name="replacement">Replacement bytes.</param>
    /// <returns>New array containing unchanged prefix/suffix bytes and the replacement.</returns>
    private static byte[] Splice(byte[] source, int offset, int length, byte[] replacement)
    {
        var result = new byte[source.Length - length + replacement.Length];
        Buffer.BlockCopy(source, 0, result, 0, offset);
        Buffer.BlockCopy(replacement, 0, result, offset, replacement.Length);
        Buffer.BlockCopy(
            source,
            offset + length,
            result,
            offset + replacement.Length,
            source.Length - offset - length);
        return result;
    }

    /// <summary>
    /// Detects the existing line-ending convention so insertion of a missing root key does not mix styles.
    /// </summary>
    /// <param name="source">Original JSON bytes.</param>
    /// <returns><c>\r\n</c> when observed first; otherwise <c>\n</c>.</returns>
    private static string DetectNewline(byte[] source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != (byte)'\n')
                continue;
            return i > 0 && source[i - 1] == (byte)'\r' ? "\r\n" : "\n";
        }

        return Environment.NewLine;
    }

    /// <summary>
    /// Identifies JSON whitespace bytes permitted around a property name, colon, or value.
    /// </summary>
    /// <param name="value">Candidate UTF-8 byte.</param>
    /// <returns><c>true</c> for space, tab, carriage return, or line feed.</returns>
    private static bool IsWhitespace(byte value)
        => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
