using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Adminbot.Utils;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Adminbot.Domain;

public static class AtlasPayCreationStates
{
    public const string Attempting = "attempting";
    public const string Created = "created";
    public const string Ambiguous = "ambiguous";
    public const string Failed = "failed";
}

public static class AtlasPaySettlementStates
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Settled = "settled";
    public const string ManualReview = "manual_review";
}

public static class AtlasPayStatuses
{
    private static readonly HashSet<string> Success = new(StringComparer.OrdinalIgnoreCase) { "confirmed", "settled" };
    private static readonly HashSet<string> Terminal = new(StringComparer.OrdinalIgnoreCase) { "rejected", "expired", "cancelled" };
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase)
    { "awaiting_payment", "admin_review", "underpaid_review", "underpaid_awaiting_remainder" };

    public static bool IsSuccess(string value) => !string.IsNullOrWhiteSpace(value) && Success.Contains(value);
    public static bool IsTerminal(string value) => !string.IsNullOrWhiteSpace(value) && Terminal.Contains(value);
    public static bool IsPending(string value) => !string.IsNullOrWhiteSpace(value) && Pending.Contains(value);
    public static bool IsKnown(string value) => IsSuccess(value) || IsTerminal(value) || IsPending(value);
}

/// <summary>
/// Safe reconciliation-lifecycle markers persisted on an AtlasPay payment row.
/// </summary>
/// <remarks>
/// The lifecycle exists so an unresolved payment can never silently disappear from reconciliation just because its
/// next-inquiry timestamp became null. Super-admin verification and the customer check keep working for every state;
/// only the automatic polling loop is restricted, and it only stops for <see cref="Escalated"/> and
/// <see cref="Exhausted"/>.
/// </remarks>
public static class AtlasPayReconciliationStates
{
    /// <summary>Automatic polling is allowed; the payment is still waiting for a provider outcome.</summary>
    public const string Active = "active";

    /// <summary>
    /// The provider returned a permanent error (for example HTTP 401 credential failure, 404 order-not-found, or a
    /// rejected request). Automatic polling has stopped to avoid endless provider traffic; the row stays visible to
    /// super-admin verification and manual investigation.
    /// </summary>
    public const string Escalated = "escalated";

    /// <summary>
    /// The automatic inquiry budget was consumed while the payment was still unresolved. No further automatic provider
    /// requests are made, and the row remains explicitly discoverable and manually verifiable.
    /// </summary>
    public const string Exhausted = "exhausted";
}

/// <summary>
/// Stable, secret-free reason codes recorded on an AtlasPay payment when a provider call fails.
/// </summary>
/// <remarks>
/// These codes are persisted, shown to super-admins, and written to logs. They must never contain provider response
/// bodies, API keys, card data, or other sensitive values.
/// </remarks>
public static class AtlasPayFailureCodes
{
    /// <summary>Network, DNS, TLS, or socket failure while talking to AtlasPay.</summary>
    public const string ProviderTransportFailed = "provider_transport_failed";

    /// <summary>AtlasPay returned a documented temporary failure (HTTP 408/429/5xx, including 503).</summary>
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>AtlasPay rejected the API key (HTTP 401); almost always a credential or configuration problem.</summary>
    public const string ProviderAuthFailed = "provider_auth_failed";

    /// <summary>AtlasPay rejected the request as invalid input (HTTP 400); retrying the same request cannot help.</summary>
    public const string ProviderInputRejected = "provider_input_rejected";

    /// <summary>AtlasPay does not know the order, or it does not belong to this merchant (HTTP 404).</summary>
    public const string ProviderOrderNotFound = "provider_order_not_found";

    /// <summary>Any other non-transient provider or local failure that is not specifically classified.</summary>
    public const string ProviderCheckFailed = "provider_check_failed";

    /// <summary>The automatic inquiry budget was consumed while the payment was still unresolved.</summary>
    public const string ReconciliationExhausted = "reconciliation_exhausted";
}

/// <summary>
/// Decides how the reconciliation pipeline must treat a failed AtlasPay provider call.
/// </summary>
/// <remarks>
/// AtlasPay documents HTTP 400 (invalid input), 401 (invalid API key), 404 (order not found or not owned by this
/// merchant), and 503 (temporarily disabled). Only an error the provider itself marks as temporary may be retried
/// automatically; everything else must fail closed and escalate instead of generating endless provider traffic.
///
/// This policy is the single place that decides retryability, so the reconciliation worker, the customer check, and
/// the super-admin verification all agree. It never inspects or exposes response bodies or credentials.
/// </remarks>
public static class AtlasPayFailurePolicy
{
    /// <summary>
    /// Returns whether the failed provider call may be retried automatically.
    /// </summary>
    /// <param name="exception">
    /// The exception raised by the AtlasPay client. Recognition relies on <see cref="AtlasPayApiException.IsTransient"/>,
    /// which the client sets from the documented HTTP status (408/429/5xx and transport or timeout failures).
    /// </param>
    /// <returns>
    /// <c>true</c> only for failures the provider marks as temporary; <c>false</c> for HTTP 400, 401, 404, and any
    /// unclassified exception, because retrying those cannot succeed and would only create noise.
    /// </returns>
    /// <example>
    /// <code>
    /// if (AtlasPayFailurePolicy.IsRetryable(ex))
    ///     payment.NextInquiryAtUtc = DateTime.UtcNow.AddSeconds(interval);
    /// </code>
    /// </example>
    public static bool IsRetryable(Exception exception) => exception switch
    {
        AtlasPayApiException { IsTransient: true } => true,
        _ => false
    };

    /// <summary>
    /// Maps a failed provider call to a stable, secret-free reason code for persistence, logs, and admin display.
    /// </summary>
    /// <param name="exception">The exception raised by the AtlasPay client. May be null.</param>
    /// <returns>
    /// One of the <see cref="AtlasPayFailureCodes"/> constants. Never returns null and never returns provider response
    /// text, so the value is safe to store and show to an operator.
    /// </returns>
    public static string ReasonCode(Exception exception) => exception switch
    {
        AtlasPayApiException { StatusCode: 401 } => AtlasPayFailureCodes.ProviderAuthFailed,
        AtlasPayApiException { StatusCode: 404 } => AtlasPayFailureCodes.ProviderOrderNotFound,
        AtlasPayApiException { StatusCode: 400 } => AtlasPayFailureCodes.ProviderInputRejected,
        AtlasPayApiException { IsTransient: true } => AtlasPayFailureCodes.ProviderUnavailable,
        AtlasPayApiException => AtlasPayFailureCodes.ProviderCheckFailed,
        _ => AtlasPayFailureCodes.ProviderCheckFailed
    };
}

public sealed class AtlasPayPaymentInfo
{
    public int Id { get; set; }
    public string MerchantOrderRef { get; set; }
    public int? ProviderOrderId { get; set; }
    public string TrackingCode { get; set; }
    public string CustomerStartLink { get; set; }
    public string CardNumberMasked { get; set; }
    public long BaseAmountToman { get; set; }
    public long? TotalAmountToman { get; set; }
    public long? ActualReceivedAmountToman { get; set; }
    public string ProviderStatus { get; set; }
    public bool RequiresManualDelivery { get; set; }
    public long TelegramUserId { get; set; }
    public long ChatId { get; set; }
    public int? TelMsgId { get; set; }
    /// <summary>Immutable wallet-charge origin type; historical rows default to owned. Tenant origin never grants owned referral rewards.</summary>
    public string WalletOriginBotType { get; set; } = BotInstanceTypes.Owned;
    /// <summary>Immutable numeric BotFather identity of a tenant-origin wallet invoice; null for legacy or owned rows.</summary>
    public long? WalletOriginTelegramBotId { get; set; }
    public string BotId { get; set; }
    public string BotUsername { get; set; }
    public string PaymentPurpose { get; set; }
    public int? TenantBotOrderId { get; set; }
    public long? TenantOwnerTelegramUserId { get; set; }
    public string CreationState { get; set; } = AtlasPayCreationStates.Attempting;
    public int CreationAttemptCount { get; set; }
    public DateTime? CreationAttemptedAtUtc { get; set; }
    public DateTime? CreationResolvedAtUtc { get; set; }
    public string CreationErrorCode { get; set; }
    public DateTime? LastInquiryAtUtc { get; set; }
    public DateTime? NextInquiryAtUtc { get; set; }
    public int InquiryAttemptCount { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public DateTime? SettledAtUtc { get; set; }
    public bool IsAddedToBalance { get; set; }
    public string SettlementState { get; set; } = AtlasPaySettlementStates.Pending;
    public string SettlementAttemptId { get; set; }
    public DateTime? SettlementStartedAtUtc { get; set; }
    public long? BalanceBefore { get; set; }
    public long? BalanceAfter { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }

    /// <summary>Whether a configured super-admin credited this wallet charge before AtlasPay officially confirmed it.</summary>
    /// <remarks>Direct tenant orders can never set this flag; it is an auditable wallet-only financial exception.</remarks>
    public bool IsProvisionallyApproved { get; set; }

    /// <summary>UTC time when the provisional wallet credit became durable.</summary>
    public DateTime? ProvisionalApprovedAtUtc { get; set; }

    /// <summary>Telegram user id of the configured super-admin who approved the provisional wallet credit.</summary>
    public long? ProvisionalApprovedByTelegramUserId { get; set; }

    /// <summary>UTC time when AtlasPay later officially confirmed a previously provisional wallet credit.</summary>
    /// <remarks>No second wallet, ledger, referral, or customer-notification mutation is allowed at this transition.</remarks>
    public DateTime? ProviderConfirmedAfterProvisionalAtUtc { get; set; }

    /// <summary>Last authenticated AtlasPay webhook event name received for this provider order.</summary>
    public string WebhookEvent { get; set; }

    /// <summary>UTC time when the application durably accepted the last valid signed AtlasPay webhook.</summary>
    public DateTime? WebhookReceivedAtUtc { get; set; }

    /// <summary>Provider-supplied event timestamp from the signed AtlasPay webhook, normalized to UTC.</summary>
    public DateTime? WebhookProviderTimestampUtc { get; set; }

    /// <summary>UTC time when the last durable webhook event reached a conclusive authoritative provider inquiry.</summary>
    /// <remarks>
    /// A null/older value means the received webhook still needs recovery processing after queue loss or process restart.
    /// </remarks>
    public DateTime? WebhookProcessedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the automatic reconciliation lifecycle marker. See <see cref="AtlasPayReconciliationStates"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AtlasPayReconciliationStates.Active"/>. The value only controls whether the background
    /// poller may issue further provider requests; it never authorises a financial effect and never blocks explicit
    /// customer or super-admin verification.
    /// </remarks>
    public string ReconciliationState { get; set; } = AtlasPayReconciliationStates.Active;

    /// <summary>
    /// Gets or sets the UTC time when the automatic inquiry budget was consumed for a still-unresolved payment.
    /// </summary>
    /// <remarks>
    /// Non-null marks a payment that needs explicit manual attention. It is set once and preserved, so the row keeps
    /// an auditable record of when automatic reconciliation gave up rather than being silently stranded.
    /// </remarks>
    public DateTime? ReconciliationExhaustedAtUtc { get; set; }

    public DateTime? LastErrorLoggedAtUtc { get; set; }
    public DateTime? SuccessLoggedAtUtc { get; set; }
    public DateTime? PaymentDeadlineAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public static AtlasPayPaymentInfo CreateWalletCharge(long telegramUserId, long chatId, long amountToman)
        => new()
        {
            MerchantOrderRef = CreateMerchantOrderRef(), BaseAmountToman = amountToman,
            TelegramUserId = telegramUserId, ChatId = chatId,
            PaymentPurpose = TenantBotPaymentPurposes.WalletCharge,
            CreationState = AtlasPayCreationStates.Attempting,
            SettlementState = AtlasPaySettlementStates.Pending,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };

    public static string CreateMerchantOrderRef() => $"AtlasPay-{Guid.NewGuid():N}";

    public void BeginCreationAttempt(DateTime nowUtc)
    {
        if (CreationAttemptCount != 0) throw new InvalidOperationException("atlaspay_create_already_attempted");
        CreationAttemptCount = 1;
        CreationState = AtlasPayCreationStates.Attempting;
        CreationAttemptedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void ApplyCreate(AtlasPayCreateOrderResponse response, DateTime nowUtc, DateTime? nextInquiryAtUtc)
    {
        ProviderOrderId = response.OrderId;
        TrackingCode = response.TrackingCode;
        TotalAmountToman = response.TotalAmountToman;
        CardNumberMasked = response.CardNumberMasked;
        CustomerStartLink = response.CustomerStartLink;
        PaymentDeadlineAtUtc = response.PaymentDeadlineAt.UtcDateTime;
        ProviderStatus = "awaiting_payment";
        CreationState = AtlasPayCreationStates.Created;
        CreationResolvedAtUtc = nowUtc;
        CreationErrorCode = null;
        NextInquiryAtUtc = nextInquiryAtUtc;
        ErrorCode = null; ErrorMessage = null; UpdatedAtUtc = nowUtc;
    }

    public void RecordCreationFailure(bool definitive, string code, DateTime nowUtc)
    {
        CreationState = definitive ? AtlasPayCreationStates.Failed : AtlasPayCreationStates.Ambiguous;
        CreationResolvedAtUtc = definitive ? nowUtc : null;
        CreationErrorCode = code;
        NextInquiryAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }
}

public sealed class AtlasPayCreateOrderRequest
{
    [JsonProperty("merchantOrderRef")] public string MerchantOrderRef { get; set; }
    [JsonProperty("baseAmountToman")] public long BaseAmountToman { get; set; }
    [JsonProperty("customerTelegramId")] public long CustomerTelegramId { get; set; }
}

public sealed class AtlasPayCreateOrderResponse
{
    [JsonProperty("orderId")] public int OrderId { get; set; }
    [JsonProperty("trackingCode")] public string TrackingCode { get; set; }
    [JsonProperty("totalAmountToman")] public long TotalAmountToman { get; set; }
    [JsonProperty("cardNumberMasked")] public string CardNumberMasked { get; set; }
    // Direct-card fields are optional and intentionally remain creation-response-only. They are never copied to
    // AtlasPayPaymentInfo/users.db, so full merchant card data is not retained by the normal payment record.
    [JsonProperty("cardNumber")] public string CardNumber { get; set; }
    [JsonProperty("cardHolderName")] public string CardHolderName { get; set; }
    [JsonProperty("bankName")] public string BankName { get; set; }
    [JsonProperty("paymentDeadlineAt")] public DateTimeOffset PaymentDeadlineAt { get; set; }
    [JsonProperty("customerStartLink")] public string CustomerStartLink { get; set; }
}

public sealed class AtlasPayOrderStatusResponse
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("trackingCode")] public string TrackingCode { get; set; }
    [JsonProperty("merchantOrderRef")] public string MerchantOrderRef { get; set; }
    [JsonProperty("status")] public string Status { get; set; }
    [JsonProperty("paid")] public bool? Paid { get; set; }
    [JsonProperty("totalAmountToman")] public long TotalAmountToman { get; set; }
    [JsonProperty("actualReceivedAmountToman")] public long? ActualReceivedAmountToman { get; set; }
    [JsonProperty("requiresManualDelivery")] public bool RequiresManualDelivery { get; set; }
    [JsonProperty("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>
/// Signed AtlasPay webhook notification. The payload is only a low-latency trigger; authoritative payment state is
/// always re-read through <c>GET /orders/{id}</c> before any wallet credit or tenant fulfillment.
/// </summary>
public sealed class AtlasPayWebhookPayload
{
    [JsonProperty("event")] public string Event { get; set; }
    [JsonProperty("orderId")] public int OrderId { get; set; }
    [JsonProperty("merchantOrderRef")] public string MerchantOrderRef { get; set; }
    [JsonProperty("totalAmountToman")] public long TotalAmountToman { get; set; }
    [JsonProperty("status")] public string Status { get; set; }
    [JsonProperty("timestamp")] public DateTimeOffset Timestamp { get; set; }
    [JsonProperty("reason")] public string Reason { get; set; }

    /// <summary>Returns whether the notification has the documented event shape.</summary>
    /// <returns><c>true</c> only for confirmed/rejected order notifications with stable order identity and amount.</returns>
    public bool IsStructurallyValid()
        => (string.Equals(Event, "order.confirmed", StringComparison.Ordinal) ||
            string.Equals(Event, "order.rejected", StringComparison.Ordinal)) &&
           OrderId > 0 && !string.IsNullOrWhiteSpace(MerchantOrderRef) && TotalAmountToman > 0 &&
           !string.IsNullOrWhiteSpace(Status) && Timestamp != default;
}

/// <summary>Verifies AtlasPay's <c>X-Webhook-Signature</c> over the exact raw HTTP request body.</summary>
public static class AtlasPayWebhookSignature
{
    /// <summary>Checks an HMAC-SHA256 hexadecimal signature without leaking timing information.</summary>
    /// <param name="rawBody">Exact UTF-8 webhook request body received from AtlasPay.</param>
    /// <param name="signature">Hexadecimal value from the <c>X-Webhook-Signature</c> header.</param>
    /// <param name="secret">Merchant webhook secret returned once when the webhook is registered.</param>
    /// <returns><c>true</c> only when the signature exactly matches the raw body and configured secret.</returns>
    /// <remarks>
    /// The body must not be deserialized and reserialized before verification because whitespace/property ordering would
    /// change the signed bytes. Malformed hexadecimal signatures fail closed and secrets are never logged.
    /// </remarks>
    public static bool Verify(string rawBody, string signature, string secret)
        => !string.IsNullOrEmpty(rawBody) && Verify(Encoding.UTF8.GetBytes(rawBody), signature, secret);

    /// <summary>Checks an HMAC-SHA256 hexadecimal signature against the exact received request bytes.</summary>
    /// <param name="rawBody">Exact HTTP entity bytes received from AtlasPay.</param>
    /// <param name="signature">Hexadecimal value from the <c>X-Webhook-Signature</c> header.</param>
    /// <param name="secret">Merchant webhook secret returned once when the webhook is registered.</param>
    /// <returns><c>true</c> only when the signature matches the exact body bytes and configured secret.</returns>
    public static bool Verify(byte[] rawBody, string signature, string secret)
    {
        if (rawBody == null || rawBody.Length == 0 || string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(secret))
            return false;

        byte[] provided;
        try { provided = Convert.FromHexString(signature.Trim()); }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}

public sealed class AtlasPayApiException : Exception
{
    public int StatusCode { get; }
    public bool IsTransient { get; }
    public AtlasPayApiException(int statusCode, string message, bool isTransient = false, Exception inner = null)
        : base(message, inner) { StatusCode = statusCode; IsTransient = isTransient; }
}

public sealed class AtlasPay
{
    private readonly AppConfig _configuration;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public AtlasPay(IConfiguration configuration) : this(configuration, new HttpClient()) { }
    public AtlasPay(IConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration.Get<AppConfig>() ?? new AppConfig();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = ValidateBaseUrl(_configuration.AtlasPayBaseUrl);
    }

    public static Uri ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("AtlasPay base URL must be an absolute HTTPS URL.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public static bool IsDefinitiveCreateFailure(Exception exception)
        => exception is AtlasPayApiException { StatusCode: 400 or 401 };

    public async Task<AtlasPayCreateOrderResponse> CreateOrderAsync(string merchantOrderRef, long baseAmountToman,
        long customerTelegramId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(merchantOrderRef) || baseAmountToman <= 0 || customerTelegramId <= 0)
            throw new ArgumentException("Invalid AtlasPay order parameters.");
        var request = new AtlasPayCreateOrderRequest
        { MerchantOrderRef = merchantOrderRef, BaseAmountToman = baseAmountToman, CustomerTelegramId = customerTelegramId };
        var response = await SendAsync(HttpMethod.Post, "orders", request, retryReadOnly: false, cancellationToken);
        AtlasPayCreateOrderResponse result;
        try { result = JsonConvert.DeserializeObject<AtlasPayCreateOrderResponse>(response); }
        catch (JsonException ex) { throw new AtlasPayApiException(0, "AtlasPay create response was malformed.", false, ex); }
        ValidateCreateResponse(result);
        return result;
    }

    public async Task<AtlasPayOrderStatusResponse> GetOrderAsync(int providerOrderId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (providerOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(providerOrderId));
        var json = await SendAsync(HttpMethod.Get, $"orders/{providerOrderId}", null, retryReadOnly: true, cancellationToken);
        return ParseStatusResponse(json, "inquiry");
    }

    public async Task<AtlasPayOrderStatusResponse> VerifyOrderAsync(int providerOrderId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (providerOrderId <= 0) throw new ArgumentOutOfRangeException(nameof(providerOrderId));
        var json = await SendAsync(HttpMethod.Post, $"orders/{providerOrderId}/verify", null, retryReadOnly: false, cancellationToken);
        // The documented verify contract returns the inquiry payload plus a mandatory boolean paid field, so a verify
        // response that omits paid is treated as invalid evidence instead of being optimistically parsed.
        return ParseStatusResponse(json, "verify", requirePaidField: true);
    }

    private async Task<string> SendAsync(HttpMethod method, string relativePath, object body, bool retryReadOnly,
        CancellationToken cancellationToken)
    {
        var retryCount = retryReadOnly ? Math.Clamp(_configuration.AtlasPayInquiryRetryCount, 0, 10) : 0;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
            request.Headers.TryAddWithoutValidation("X-API-Key", _configuration.AtlasPayApiKey);
            if (body != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_configuration.AtlasPayRequestTimeoutSeconds, 1, 120)));
            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return text;
                var code = (int)response.StatusCode;
                var transient = code is 408 or 429 || code >= 500;
                if (retryReadOnly && transient && attempt < retryCount)
                { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken); continue; }
                throw new AtlasPayApiException(code, $"AtlasPay request failed with HTTP {code}.", transient);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (retryReadOnly && attempt < retryCount)
                { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken); continue; }
                throw new AtlasPayApiException(0, "AtlasPay request timed out.", true, ex);
            }
            catch (HttpRequestException ex)
            {
                if (retryReadOnly && attempt < retryCount)
                { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken); continue; }
                throw new AtlasPayApiException(0, "AtlasPay transport failed.", true, ex);
            }
        }
    }

    /// <summary>
    /// Parses an AtlasPay inquiry or verify payload into the shared status response model.
    /// </summary>
    /// <param name="json">Raw JSON body returned by AtlasPay. Never persisted or logged verbatim.</param>
    /// <param name="operation">Operation label used to build a safe, secret-free diagnostic message.</param>
    /// <param name="requirePaidField">
    /// When <c>true</c> (the documented verify path) the response must contain the boolean <c>paid</c> field. A response
    /// without it is rejected as structurally invalid, so a malformed or partial verify payload can never be
    /// interpreted as successful payment evidence.
    /// </param>
    /// <returns>The parsed, structurally validated provider response.</returns>
    /// <exception cref="AtlasPayApiException">
    /// Thrown when the payload is malformed, not successful, missing the order id, status, positive total amount, or the
    /// required <c>paid</c> field. The exception is non-transient, because retrying a malformed payload cannot help.
    /// </exception>
    private static AtlasPayOrderStatusResponse ParseStatusResponse(string json, string operation, bool requirePaidField = false)
    {
        AtlasPayOrderStatusResponse result;
        try { result = JsonConvert.DeserializeObject<AtlasPayOrderStatusResponse>(json); }
        catch (JsonException ex) { throw new AtlasPayApiException(0, $"AtlasPay {operation} response was malformed.", false, ex); }
        if (result == null || !result.Success || result.Id <= 0 || string.IsNullOrWhiteSpace(result.Status) || result.TotalAmountToman <= 0)
            throw new AtlasPayApiException(0, $"AtlasPay {operation} response was structurally invalid.");
        if (requirePaidField && !result.Paid.HasValue)
            throw new AtlasPayApiException(0, $"AtlasPay {operation} response omitted the documented paid field.");
        return result;
    }

    private static void ValidateCreateResponse(AtlasPayCreateOrderResponse response)
    {
        if (response == null || response.OrderId <= 0 || string.IsNullOrWhiteSpace(response.TrackingCode) ||
            response.TotalAmountToman <= 0 || response.PaymentDeadlineAt == default || !IsSafeCustomerLink(response.CustomerStartLink))
            throw new AtlasPayApiException(0, "AtlasPay create response was structurally invalid.");
    }

    internal static bool IsSafeCustomerLink(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(uri.Host);

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_configuration.AtlasPayApiKey))
            throw new InvalidOperationException("AtlasPay is not configured.");
    }
}

public static class AtlasPayPaymentVerifier
{
    public static bool IsVerifiedForAutomaticSettlement(AtlasPayPaymentInfo payment, AtlasPayOrderStatusResponse response,
        out string errorCode, out bool manualReview)
    {
        errorCode = null; manualReview = false;
        if (payment == null || response == null || !response.Success) { errorCode = "provider_response_invalid"; return false; }
        if (!payment.ProviderOrderId.HasValue || response.Id != payment.ProviderOrderId.Value) { errorCode = "provider_order_id_mismatch"; return false; }
        if (!string.Equals(response.MerchantOrderRef, payment.MerchantOrderRef, StringComparison.Ordinal)) { errorCode = "merchant_order_ref_mismatch"; return false; }
        if (!string.IsNullOrWhiteSpace(response.TrackingCode) && !string.IsNullOrWhiteSpace(payment.TrackingCode) &&
            !string.Equals(response.TrackingCode, payment.TrackingCode, StringComparison.Ordinal)) { errorCode = "tracking_code_mismatch"; return false; }
        if (!payment.TotalAmountToman.HasValue || response.TotalAmountToman != payment.TotalAmountToman.Value)
        { errorCode = "total_amount_mismatch"; return false; }
        if (!AtlasPayStatuses.IsKnown(response.Status)) { errorCode = "unknown_provider_status"; return false; }
        var successful = AtlasPayStatuses.IsSuccess(response.Status) && (response.Paid != false);
        if (response.RequiresManualDelivery && (successful || response.Paid == true))
        { manualReview = true; errorCode = "requires_manual_delivery"; return false; }
        if (!successful) { errorCode = "provider_not_paid"; return false; }
        return true;
    }
}

/// <summary>
/// Applies the throttle that protects AtlasPay from customer-initiated check-button spam.
/// </summary>
/// <remarks>
/// AtlasPay polling remains the authoritative fallback even when the optional signed webhook is configured, so every
/// customer check still costs a real provider request. A per-payment cooldown keeps one impatient customer from generating a burst of consecutive
/// inquiries while still allowing the background reconciliation worker to run normally. The cooldown is advisory and
/// financial-safe: while it is active no provider call is made and no financial state changes.
/// </remarks>
public static class AtlasPayManualCheckPolicy
{
    /// <summary>
    /// Determines whether a customer check is still inside the per-payment provider cooldown.
    /// </summary>
    /// <param name="payment">
    /// The AtlasPay payment being checked. The cooldown is measured from <see cref="AtlasPayPaymentInfo.LastInquiryAtUtc"/>,
    /// which is written by every provider inquiry, including the background worker, so spam cannot bypass it by
    /// switching to the button.
    /// </param>
    /// <param name="minIntervalSeconds">
    /// The configured minimum seconds between two provider requests for the same payment. Values are clamped to the
    /// range 0..3600; 0 disables the cooldown entirely. Production default is ten seconds.
    /// </param>
    /// <param name="nowUtc">The current UTC time, supplied by the caller so tests remain deterministic.</param>
    /// <param name="remainingSeconds">
    /// When the method returns <c>true</c>, receives the whole number of seconds still remaining in the cooldown so the
    /// user can be told how long to wait; otherwise 0.
    /// </param>
    /// <returns>
    /// <c>true</c> when the caller must skip the provider request and answer the user from local state only;
    /// <c>false</c> when a fresh provider verification may be performed.
    /// </returns>
    /// <example>
    /// <code>
    /// if (AtlasPayManualCheckPolicy.IsWithinCooldown(payment, intervalSeconds, DateTime.UtcNow, out var wait))
    /// {
    ///     await AnswerCallbackAsync($"لطفاً {wait} ثانیه دیگر دوباره بررسی کنید.");
    ///     return;
    /// }
    /// </code>
    /// </example>
    public static bool IsWithinCooldown(AtlasPayPaymentInfo payment, int minIntervalSeconds, DateTime nowUtc,
        out long remainingSeconds)
    {
        remainingSeconds = 0;
        if (payment?.LastInquiryAtUtc == null) return false;
        var interval = Math.Clamp(minIntervalSeconds, 0, 3600);
        if (interval <= 0) return false;
        var elapsed = nowUtc - payment.LastInquiryAtUtc.Value;
        if (elapsed >= TimeSpan.FromSeconds(interval)) return false;
        remainingSeconds = (long)Math.Ceiling((TimeSpan.FromSeconds(interval) - elapsed).TotalSeconds);
        return true;
    }
}

/// <summary>Settles verified central AtlasPay charges into the global customer wallet with immutable owned/tenant origin.</summary>
/// <remarks>Uses existing receipt keys and independent database commits. Customer delivery is an outbox intent;
/// tenant-origin payments never qualify for owned referral rewards or direct tenant-order fulfillment.</remarks>
public sealed partial class AtlasPaySettlementService
{
    private static readonly AsyncKeyedGate SettlementGate = new();
    private readonly UserDbContextFactory _factory;
    private readonly CredentialsStore _credentials;
    private readonly WalletLedgerService _ledger;
    private readonly ReferralService _referrals;
    private readonly TenantWalletOwnerTopUpMirrorService _ownerTopUpMirror;
    private readonly AppConfig _configuration;
    private readonly ILogger<AtlasPaySettlementService> _logger;

    /// <summary>Creates a scoped settlement service without retaining an EF context.</summary>
    /// <param name="factory">Factory for provider settlement rows and outbox intents in users.db.</param>
    /// <param name="credentials">Global wallet and immutable receipt authority in credentials.db.</param>
    /// <param name="ledger">Idempotent customer ledger writer.</param>
    /// <param name="referrals">Owned-only referral policy, which rejects immutable tenant origins.</param>
    /// <param name="configuration">Runtime configuration used to re-check configured super-admin authorization.</param>
    /// <param name="logger">Central financial audit logger.</param>
    public AtlasPaySettlementService(UserDbContextFactory factory, CredentialsStore credentials,
        WalletLedgerService ledger, ReferralService referrals, IConfiguration configuration,
        ILogger<AtlasPaySettlementService> logger,
        TenantWalletOwnerTopUpMirrorService ownerTopUpMirror = null)
    {
        _factory = factory;
        _credentials = credentials;
        _ledger = ledger;
        _referrals = referrals;
        _ownerTopUpMirror = ownerTopUpMirror;
        _configuration = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
    }

    /// <summary>Credits one officially verified wallet charge and preserves the originating bot for audit and notification.</summary>
    /// <param name="payment">Persisted local payment identity, reloaded under its invoice gate; null yields NotFound.</param>
    /// <param name="source">Non-secret caller category for financial audit, never a provider response body.</param>
    /// <param name="cancellationToken">Cancellation of independent database commits and referral reconciliation.</param>
    /// <returns>Applied with receipt balances, AlreadyAdded for a duplicate, or a non-applied validation result.</returns>
    /// <remarks>Only a verified paid wallet_charge can settle. Receipt and balance commit atomically in credentials.db;
    /// marker and notification commit separately in users.db. Repeated calls repair the ledger without another credit.
    /// Approval revocation does not block a previously created invoice's legitimate settlement.</remarks>
    public async Task<NowPaymentsSettlementResult> ApplyOfficialPaymentAsync(AtlasPayPaymentInfo payment, string source,
        CancellationToken cancellationToken = default)
    {
        if (payment == null) return NowPaymentsSettlementResult.NotFound();
        using var lease = await SettlementGate.EnterAsync(payment.Id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        try
        {
            var context = new UserWorkflowStore(_factory);
            var tracked = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos.FirstOrDefaultAsync(x => x.Id == payment.Id, cancellationToken));
            if (tracked == null) return NowPaymentsSettlementResult.NotFound();
            if (!string.Equals(tracked.PaymentPurpose, TenantBotPaymentPurposes.WalletCharge, StringComparison.OrdinalIgnoreCase) ||
                !AtlasPayStatuses.IsSuccess(tracked.ProviderStatus) || tracked.RequiresManualDelivery || !tracked.PaidAtUtc.HasValue)
                return NowPaymentsSettlementResult.ProviderNotPaid();
            var user = await _credentials.GetUserStatusWithId(tracked.TelegramUserId);
            if (user == null) return NowPaymentsSettlementResult.UserNotFound();
            if (tracked.IsAddedToBalance)
            {
                if (tracked.IsProvisionallyApproved)
                {
                    await EnsureProvisionalLedgerAsync(
                        tracked,
                        tracked.BalanceBefore ?? user.AccountBalance - tracked.BaseAmountToman,
                        tracked.BalanceAfter ?? user.AccountBalance,
                        cancellationToken);
                }
                else
                {
                    await EnsureLedgerAsync(
                        tracked,
                        tracked.BalanceBefore ?? user.AccountBalance - tracked.BaseAmountToman,
                        tracked.BalanceAfter ?? user.AccountBalance,
                        cancellationToken);
                    await ProcessReferralAsync(tracked, cancellationToken);
                }
                if (_ownerTopUpMirror != null)
                    await _ownerTopUpMirror.EnsureAsync("atlaspay", tracked.Id, tracked.BotId, tracked.BotUsername,
                        tracked.WalletOriginBotType, tracked.TenantOwnerTelegramUserId, tracked.TelegramUserId,
                        tracked.BaseAmountToman, cancellationToken);
                return NowPaymentsSettlementResult.AlreadyAdded(tracked.BalanceAfter ?? user.AccountBalance);
            }
            if (string.Equals(tracked.SettlementState, AtlasPaySettlementStates.ManualReview, StringComparison.Ordinal))
                return NowPaymentsSettlementResult.ProviderNotPaid();
            if (string.Equals(tracked.SettlementState, AtlasPaySettlementStates.Processing, StringComparison.Ordinal))
            {
                if (!tracked.SettlementStartedAtUtc.HasValue || DateTime.UtcNow - tracked.SettlementStartedAtUtc.Value >= TimeSpan.FromMinutes(30))
                {
                    tracked.SettlementState = AtlasPaySettlementStates.ManualReview;
                    tracked.ErrorCode = "settlement_claim_ambiguous"; tracked.NextInquiryAtUtc = null; tracked.UpdatedAtUtc = DateTime.UtcNow;
                    await context.SaveAsync(cancellationToken);
                }
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }
            var attemptId = Guid.NewGuid().ToString("N"); var now = DateTime.UtcNow;
            var claimed = await context.WriteAsync(async db => await db.AtlasPayPaymentInfos
                .Where(x => x.Id == tracked.Id && !x.IsAddedToBalance && x.SettlementState == AtlasPaySettlementStates.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SettlementState, AtlasPaySettlementStates.Processing)
                    .SetProperty(x => x.SettlementAttemptId, attemptId).SetProperty(x => x.SettlementStartedAtUtc, now)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken));
            if (claimed != 1) return NowPaymentsSettlementResult.ProviderNotPaid();
            var key = $"payment:atlaspay:{tracked.Id}:credit";
            if (!await _credentials.AddFund(tracked.TelegramUserId, tracked.BaseAmountToman, key, botId: tracked.BotId))
                return NowPaymentsSettlementResult.UserNotFound();
            var receipt = await _credentials.GetWalletOperationAsync(key);
            tracked = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos.FirstAsync(x => x.Id == payment.Id, cancellationToken));
            tracked.IsAddedToBalance = true; tracked.SettlementState = AtlasPaySettlementStates.Settled;
            tracked.SettlementAttemptId = null; tracked.SettlementStartedAtUtc = null;
            tracked.BalanceBefore = receipt.BeforeBalance; tracked.BalanceAfter = receipt.AfterBalance;
            tracked.SettledAtUtc ??= DateTime.UtcNow; tracked.NextInquiryAtUtc = null; tracked.ErrorCode = null; tracked.ErrorMessage = null; tracked.UpdatedAtUtc = DateTime.UtcNow;
            context.Add(PaymentSettlementNotification.CreateWalletCredit("atlaspay", tracked.Id, tracked.BotId,
                tracked.TelegramUserId, tracked.ChatId, tracked.BaseAmountToman,
                $"اعتبار کیف پول شما به میزان {tracked.BaseAmountToman.FormatCurrency()} افزایش یافت.", tracked.SettledAtUtc.Value, tracked.WalletOriginBotType, tracked.BalanceAfter,
                tracked.WalletOriginTelegramBotId));
            await context.SaveAsync(cancellationToken);
            await EnsureLedgerAsync(tracked, receipt.BeforeBalance, receipt.AfterBalance, cancellationToken);
            await ProcessReferralAsync(tracked, cancellationToken);
            if (_ownerTopUpMirror != null)
                await _ownerTopUpMirror.EnsureAsync("atlaspay", tracked.Id, tracked.BotId, tracked.BotUsername,
                    tracked.WalletOriginBotType, tracked.TenantOwnerTelegramUserId, tracked.TelegramUserId,
                    tracked.BaseAmountToman, cancellationToken);
            if (!tracked.SuccessLoggedAtUtc.HasValue)
            {
                tracked.SuccessLoggedAtUtc = DateTime.UtcNow; tracked.UpdatedAtUtc = DateTime.UtcNow; await context.SaveAsync(cancellationToken);
                var userSummary = TelegramUserLinkFormatter.HtmlSummary(user);
                if (string.IsNullOrWhiteSpace(userSummary))
                    userSummary = $"👤 کاربر: <code>{tracked.TelegramUserId}</code>";
                var botSummary = string.IsNullOrWhiteSpace(tracked.BotUsername)
                    ? WebUtility.HtmlEncode(tracked.BotId)
                    : $"{WebUtility.HtmlEncode(tracked.BotId)} / @{WebUtility.HtmlEncode(tracked.BotUsername.TrimStart('@'))}";
                var feeToman = tracked.TotalAmountToman.HasValue
                    ? Math.Max(0, tracked.TotalAmountToman.Value - tracked.BaseAmountToman)
                    : 0;
                _logger.LogPayment("✅ پرداخت ریالی AtlasPay تایید شد\n\n" +
                    userSummary + "\n\n" +
                    $"💰 مبلغ شارژ کیف پول: <code>{tracked.BaseAmountToman.FormatCurrency()}</code>\n" +
                    $"💳 مبلغ نهایی فاکتور: <code>{tracked.TotalAmountToman?.FormatCurrency()}</code>\n" +
                    $"🧾 کارمزد/اختلاف فاکتور: <code>{feeToman.FormatCurrency()}</code> (<code>۱۲٪</code>)\n" +
                    $"💵 مبلغ دریافتی provider: <code>{tracked.ActualReceivedAmountToman?.FormatCurrency()}</code>\n" +
                    $"📌 وضعیت provider: <code>{WebUtility.HtmlEncode(tracked.ProviderStatus)}</code>\n" +
                    $"🧾 Payment ID: <code>AP:{tracked.Id}</code>\n" +
                    $"🧾 Provider Order ID: <code>{tracked.ProviderOrderId}</code>\n" +
                    $"🧾 Merchant Ref: <code>{WebUtility.HtmlEncode(tracked.MerchantOrderRef)}</code>\n" +
                    $"🔎 Tracking code: <code>{WebUtility.HtmlEncode(tracked.TrackingCode)}</code>\n" +
                    $"🎯 هدف پرداخت: <code>{WebUtility.HtmlEncode(tracked.PaymentPurpose)}</code>\n" +
                    $"🤖 ربات/منشأ: <code>{botSummary}</code> / <code>{WebUtility.HtmlEncode(tracked.WalletOriginBotType)}</code>\n\n" +
                    $"💳 موجودی قبل: <code>{receipt.BeforeBalance.FormatCurrency()}</code>\n" +
                    $"💳 موجودی بعد: <code>{receipt.AfterBalance.FormatCurrency()}</code>\n" +
                    $"📡 منبع تایید: <code>{WebUtility.HtmlEncode(FormatConfirmationSource(source))}</code>\n" +
                    $"📨 Webhook event: <code>{WebUtility.HtmlEncode(tracked.WebhookEvent ?? "-")}</code>\n" +
                    $"📥 زمان دریافت Webhook: <code>{WebUtility.HtmlEncode(tracked.WebhookReceivedAtUtc?.AddMinutes(210).ConvertToHijriShamsi() ?? "-")}</code>\n" +
                    $"🕒 زمان ثبت: <code>{WebUtility.HtmlEncode(DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi())}</code>");
            }
            return NowPaymentsSettlementResult.Applied(receipt.BeforeBalance, receipt.AfterBalance);
        }
        finally { lease.Dispose(); }
    }

    /// <summary>Provisionally credits one AtlasPay wallet charge after an explicit two-stage super-admin decision.</summary>
    /// <param name="payment">Freshly verified pending wallet-charge row; direct tenant orders are rejected.</param>
    /// <param name="approvedByTelegramUserId">Configured super-admin Telegram id persisted with the financial exception.</param>
    /// <param name="notifyChatId">Optional customer chat override for the durable settlement notification.</param>
    /// <param name="cancellationToken">Cancellation token for wallet, users.db, ledger, and outbox work.</param>
    /// <returns>Applied for the first provisional credit, AlreadyAdded for a duplicate, or a non-mutating rejection.</returns>
    /// <remarks>
    /// The immutable base amount is credited exactly once through the same credentials.db operation key as official
    /// settlement. Provider status remains pending. A later official webhook confirmation records audit only and never
    /// creates another wallet credit, ledger row, referral reward, or customer notification.
    /// </remarks>
    public async Task<NowPaymentsSettlementResult> ApplyProvisionalPaymentAsync(
        AtlasPayPaymentInfo payment,
        long approvedByTelegramUserId,
        long? notifyChatId = null,
        CancellationToken cancellationToken = default)
    {
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();
        if (approvedByTelegramUserId <= 0 ||
            _configuration.AdminsUserIds?.Contains(approvedByTelegramUserId) != true)
            return NowPaymentsSettlementResult.InvalidAmount();

        using var lease = await SettlementGate.EnterAsync(payment.Id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        try
        {
            var context = new UserWorkflowStore(_factory);
            var tracked = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos
                .FirstOrDefaultAsync(x => x.Id == payment.Id, cancellationToken));
            if (tracked == null)
                return NowPaymentsSettlementResult.NotFound();

            var user = await _credentials.GetUserStatusWithId(tracked.TelegramUserId);
            if (user == null)
                return NowPaymentsSettlementResult.UserNotFound();

            if (tracked.IsAddedToBalance)
            {
                if (tracked.IsProvisionallyApproved)
                {
                    await EnsureProvisionalLedgerAsync(
                        tracked,
                        tracked.BalanceBefore ?? user.AccountBalance - tracked.BaseAmountToman,
                        tracked.BalanceAfter ?? user.AccountBalance,
                        cancellationToken);
                }
                return NowPaymentsSettlementResult.AlreadyAdded(tracked.BalanceAfter ?? user.AccountBalance);
            }

            if (!CanApplyProvisionalCredit(tracked))
                return NowPaymentsSettlementResult.ProviderNotPaid();

            var attemptId = Guid.NewGuid().ToString("N");
            var claimedAtUtc = DateTime.UtcNow;
            var claimed = await context.WriteAsync(async db => await db.AtlasPayPaymentInfos
                .Where(x => x.Id == tracked.Id &&
                            !x.IsAddedToBalance &&
                            x.SettlementState == AtlasPaySettlementStates.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.SettlementState, AtlasPaySettlementStates.Processing)
                        .SetProperty(x => x.SettlementAttemptId, attemptId)
                        .SetProperty(x => x.SettlementStartedAtUtc, claimedAtUtc)
                        .SetProperty(x => x.UpdatedAtUtc, claimedAtUtc),
                    cancellationToken));
            if (claimed != 1)
                return NowPaymentsSettlementResult.ProviderNotPaid();

            var key = $"payment:atlaspay:{tracked.Id}:credit";
            if (!await _credentials.AddFund(
                    tracked.TelegramUserId,
                    tracked.BaseAmountToman,
                    key,
                    botId: tracked.BotId,
                    approvalKind: "provisional",
                    approvedByTelegramUserId: approvedByTelegramUserId))
            {
                tracked = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos
                    .FirstAsync(x => x.Id == payment.Id, cancellationToken));
                tracked.SettlementState = AtlasPaySettlementStates.Pending;
                tracked.SettlementAttemptId = null;
                tracked.SettlementStartedAtUtc = null;
                tracked.UpdatedAtUtc = DateTime.UtcNow;
                await context.SaveAsync(cancellationToken);
                return NowPaymentsSettlementResult.UserNotFound();
            }

            var receipt = await _credentials.GetWalletOperationAsync(key);
            tracked = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos
                .FirstAsync(x => x.Id == payment.Id, cancellationToken));
            tracked.IsAddedToBalance = true;
            tracked.IsProvisionallyApproved = true;
            tracked.ProvisionalApprovedAtUtc = DateTime.UtcNow;
            tracked.ProvisionalApprovedByTelegramUserId = approvedByTelegramUserId;
            tracked.SettlementState = AtlasPaySettlementStates.Settled;
            tracked.SettlementAttemptId = null;
            tracked.SettlementStartedAtUtc = null;
            tracked.BalanceBefore = receipt.BeforeBalance;
            tracked.BalanceAfter = receipt.AfterBalance;
            tracked.SettledAtUtc ??= DateTime.UtcNow;
            tracked.NextInquiryAtUtc = AtlasPayPollingPolicy.GetInitialNextInquiryUtc(_configuration, DateTime.UtcNow);
            tracked.UpdatedAtUtc = DateTime.UtcNow;

            context.Add(PaymentSettlementNotification.CreateWalletCredit(
                "atlaspay",
                tracked.Id,
                tracked.BotId,
                tracked.TelegramUserId,
                notifyChatId ?? tracked.ChatId,
                tracked.BaseAmountToman,
                $"اعتبار کیف پول شما به میزان {tracked.BaseAmountToman.FormatCurrency()} به صورت موقت توسط مدیر تایید و افزایش یافت.",
                tracked.SettledAtUtc.Value,
                tracked.WalletOriginBotType,
                tracked.BalanceAfter,
                tracked.WalletOriginTelegramBotId));
            await context.SaveAsync(cancellationToken);

            await EnsureProvisionalLedgerAsync(tracked, receipt.BeforeBalance, receipt.AfterBalance, cancellationToken);
            LogProvisionalSettlement(tracked, user, receipt.BeforeBalance, receipt.AfterBalance);
            return NowPaymentsSettlementResult.Applied(receipt.BeforeBalance, receipt.AfterBalance);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Ensures a provisionally approved AtlasPay wallet credit has one append-only audit ledger row.</summary>
    /// <param name="payment">Provisionally credited wallet-charge row.</param>
    /// <param name="before">Wallet balance before the provisional credit, in toman.</param>
    /// <param name="after">Wallet balance after the provisional credit, in toman.</param>
    /// <param name="token">Cancellation token for the idempotent users.db ledger write.</param>
    /// <returns>The existing or newly inserted ledger entry; no balance is changed.</returns>
    private Task<WalletLedgerEntry> EnsureProvisionalLedgerAsync(
        AtlasPayPaymentInfo payment,
        long before,
        long after,
        CancellationToken token)
        => _ledger.RecordAsync(
            payment.TelegramUserId,
            WalletLedgerDirections.Credit,
            payment.BaseAmountToman,
            before,
            after,
            WalletLedgerReasons.WalletCharge,
            provider: "atlaspay_provisional_admin",
            referenceType: nameof(AtlasPayPaymentInfo),
            referenceId: payment.Id.ToString(CultureInfo.InvariantCulture),
            orderId: payment.MerchantOrderRef,
            description: $"AtlasPay provisional wallet charge approved by {payment.ProvisionalApprovedByTelegramUserId}",
            botId: payment.BotId,
            botUsername: payment.BotUsername,
            botType: payment.WalletOriginBotType,
            idempotencyKey: $"payment:atlaspay:{payment.Id}:credit",
            cancellationToken: token);

    /// <summary>Writes the central Telegram audit for an AtlasPay provisional wallet credit.</summary>
    /// <param name="payment">Provisionally credited payment with immutable provider and admin audit identifiers.</param>
    /// <param name="user">Wallet owner shown in the protected logger-channel message.</param>
    /// <param name="before">Wallet balance before credit in toman.</param>
    /// <param name="after">Wallet balance after credit in toman.</param>
    private void LogProvisionalSettlement(AtlasPayPaymentInfo payment, CredUser user, long before, long after)
    {
        var userSummary = TelegramUserLinkFormatter.HtmlSummary(user);
        if (string.IsNullOrWhiteSpace(userSummary))
            userSummary = $"👤 کاربر: <code>{payment.TelegramUserId}</code>";

        _logger.LogPayment(
            "⚠️ شارژ موقت کیف پول با AtlasPay توسط سوپرادمین\n\n" +
            userSummary + "\n\n" +
            $"💰 مبلغ شارژ: <code>{payment.BaseAmountToman.FormatCurrency()}</code>\n" +
            $"💳 مبلغ فاکتور AtlasPay: <code>{payment.TotalAmountToman?.FormatCurrency()}</code>\n" +
            "💸 کارمزد اعلامی: <code>۱۲٪</code>\n" +
            $"🧾 Payment ID: <code>AP:{payment.Id}</code>\n" +
            $"🧾 Provider Order ID: <code>{payment.ProviderOrderId}</code>\n" +
            $"🧾 Merchant Ref: <code>{WebUtility.HtmlEncode(payment.MerchantOrderRef)}</code>\n" +
            $"🔎 Tracking code: <code>{WebUtility.HtmlEncode(payment.TrackingCode)}</code>\n" +
            $"📌 وضعیت رسمی درگاه: <code>{WebUtility.HtmlEncode(payment.ProviderStatus)}</code>\n" +
            $"👨‍💼 تاییدکننده: <code>{payment.ProvisionalApprovedByTelegramUserId}</code>\n\n" +
            $"💳 موجودی قبل: <code>{before.FormatCurrency()}</code>\n" +
            $"💳 موجودی بعد: <code>{after.FormatCurrency()}</code>\n" +
            "🔒 این شارژ موقت است؛ تایید رسمی بعدی دوباره کیف پول یا ledger را تغییر نمی‌دهد.");
    }

    /// <summary>Records a later official AtlasPay confirmation for a provisionally credited wallet without crediting again.</summary>
    /// <param name="payment">Provisionally credited payment that has now received an official successful provider state.</param>
    /// <param name="source">Non-secret trigger label that observed the official provider confirmation.</param>
    /// <param name="cancellationToken">Cancellation token for the users.db audit update.</param>
    /// <returns>A task completing after the audit marker and logger message are written, or a safe no-op.</returns>
    /// <remarks>No wallet, ledger, referral, or customer-notification mutation is performed.</remarks>
    public async Task RecordProviderConfirmationAfterProvisionalAsync(
        AtlasPayPaymentInfo payment,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (payment == null)
            return;

        using var lease = await SettlementGate.EnterAsync(payment.Id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        try
        {
            var context = new UserWorkflowStore(_factory);
            var tracked = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos
                .FirstOrDefaultAsync(x => x.Id == payment.Id, cancellationToken));
            if (tracked == null ||
                !tracked.IsProvisionallyApproved ||
                !tracked.IsAddedToBalance ||
                !AtlasPayStatuses.IsSuccess(tracked.ProviderStatus) ||
                tracked.ProviderConfirmedAfterProvisionalAtUtc.HasValue)
            {
                return;
            }

            tracked.ProviderConfirmedAfterProvisionalAtUtc = DateTime.UtcNow;
            tracked.NextInquiryAtUtc = null;
            tracked.ErrorCode = null;
            tracked.ErrorMessage = null;
            tracked.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveAsync(cancellationToken);

            var user = await _credentials.GetUserStatusWithId(tracked.TelegramUserId);
            var userSummary = TelegramUserLinkFormatter.HtmlSummary(user);
            if (string.IsNullOrWhiteSpace(userSummary))
                userSummary = $"👤 کاربر: <code>{tracked.TelegramUserId}</code>";

            _logger.LogPayment(
                "ℹ️ AtlasPay شارژ موقت را رسماً تایید کرد\n\n" +
                userSummary + "\n\n" +
                $"🧾 Payment ID: <code>AP:{tracked.Id}</code>\n" +
                $"🧾 Provider Order ID: <code>{tracked.ProviderOrderId}</code>\n" +
                $"🔎 Tracking code: <code>{WebUtility.HtmlEncode(tracked.TrackingCode)}</code>\n" +
                $"💰 مبلغ شارژ: <code>{tracked.BaseAmountToman.FormatCurrency()}</code>\n" +
                $"📌 وضعیت رسمی: <code>{WebUtility.HtmlEncode(tracked.ProviderStatus)}</code>\n" +
                $"👨‍💼 تایید موقت توسط: <code>{tracked.ProvisionalApprovedByTelegramUserId}</code>\n" +
                $"📡 منبع تایید رسمی: <code>{WebUtility.HtmlEncode(FormatConfirmationSource(source))}</code>\n" +
                $"📨 Webhook event: <code>{WebUtility.HtmlEncode(tracked.WebhookEvent ?? "-")}</code>\n" +
                "🔒 کیف پول، ledger، referral و اعلان مشتری دوباره تغییر نکردند.");
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Maps internal AtlasPay trigger labels to concise operator-facing audit text.</summary>
    /// <param name="source">Internal non-secret reconciliation source.</param>
    /// <returns>Human-readable source text suitable for the Telegram logger channel.</returns>
    internal static string FormatConfirmationSource(string source)
        => source switch
        {
            "atlaspay-webhook" => "Webhook/IPN AtlasPay",
            "superadmin-verify" => "استعلام رسمی Super Admin",
            "admin-provisional-confirm-refresh" => "بررسی نهایی Super Admin",
            "customer-check" => "بررسی دستی کاربر",
            "reconciliation-worker" => "Legacy polling (Webhook تنظیم نشده)",
            _ => string.IsNullOrWhiteSpace(source) ? "-" : source
        };

    /// <summary>Determines whether a freshly verified AtlasPay wallet charge may receive a super-admin provisional credit.</summary>
    /// <param name="payment">Payment row after the immediately preceding authoritative provider verification.</param>
    /// <returns><c>true</c> only for an unresolved wallet charge with stable provider identity and no terminal/manual-review state.</returns>
    /// <remarks>
    /// Direct tenant orders, tenant-origin wallet charges, paid/terminal rows, provider-review/underpayment states,
    /// ambiguous settlement claims, and provider errors are excluded. The final confirmation path performs another
    /// official verification while holding the payment gate.
    /// </remarks>
    public static bool CanApplyProvisionalCredit(AtlasPayPaymentInfo payment)
        => CanOfferAdminReview(payment) &&
           string.Equals(payment.ProviderStatus, "awaiting_payment", StringComparison.OrdinalIgnoreCase) &&
           string.IsNullOrWhiteSpace(payment.ErrorCode) &&
           string.Equals(payment.SettlementState, AtlasPaySettlementStates.Pending, StringComparison.Ordinal);

    /// <summary>Appends the receipt-backed wallet-charge audit using the persisted payment origin.</summary>
    /// <param name="payment">Settled local payment with immutable customer, amount and originating bot.</param>
    /// <param name="before">Balance before credit in toman, from its immutable receipt.</param>
    /// <param name="after">Balance after credit in toman, from the same receipt.</param>
    /// <param name="token">Cancellation of the idempotent users.db ledger write.</param>
    /// <returns>The existing or inserted detached ledger entry; no balance is changed.</returns>
    private Task<WalletLedgerEntry> EnsureLedgerAsync(AtlasPayPaymentInfo payment, long before, long after, CancellationToken token)
        => _ledger.RecordAsync(payment.TelegramUserId, WalletLedgerDirections.Credit, payment.BaseAmountToman, before, after,
            WalletLedgerReasons.WalletCharge, provider: "atlaspay", referenceType: nameof(AtlasPayPaymentInfo),
            referenceId: payment.Id.ToString(CultureInfo.InvariantCulture), orderId: payment.MerchantOrderRef,
            description: "AtlasPay wallet charge", botId: payment.BotId, botUsername: payment.BotUsername,
            botType: payment.WalletOriginBotType, idempotencyKey: $"payment:atlaspay:{payment.Id}:credit", cancellationToken: token);

    /// <summary>Passes immutable origin to the owned referral engine so tenant charges cannot award referrals.</summary>
    /// <param name="payment">Official settled wallet charge; never a direct storefront order.</param>
    /// <param name="token">Cancellation of referral persistence and notifications.</param>
    /// <returns>A task completing after eligible owned rewards or a tenant-origin no-op.</returns>
    private Task ProcessReferralAsync(AtlasPayPaymentInfo payment, CancellationToken token)
        => _referrals.ProcessFinalOwnedWalletPaymentAsync(new ReferralPaymentSource("atlaspay", payment.PaymentPurpose,
            payment.ProviderOrderId?.ToString(CultureInfo.InvariantCulture) ?? payment.MerchantOrderRef, payment.BotId,
            payment.WalletOriginBotType, payment.TelegramUserId, payment.BaseAmountToman,
            payment.SettledAtUtc ?? payment.PaidAtUtc ?? DateTime.UtcNow, payment.IsAddedToBalance,
            AtlasPayStatuses.IsSuccess(payment.ProviderStatus), IsProvisional: payment.IsProvisionallyApproved), token);
}

public sealed partial class AtlasPayReconciliationHostedService : BackgroundService
{
    private static readonly AsyncKeyedGate Gate = new();
    private readonly Channel<int> _webhookTriggers = Channel.CreateBounded<int>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly AppConfig _configuration;
    private readonly UserDbContextFactory _factory;
    private readonly AtlasPay _atlasPay;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AtlasPayReconciliationHostedService> _logger;

    public AtlasPayReconciliationHostedService(IConfiguration configuration, UserDbContextFactory factory, AtlasPay atlasPay,
        IServiceScopeFactory scopeFactory, ILogger<AtlasPayReconciliationHostedService> logger)
    { _configuration = configuration.Get<AppConfig>() ?? new AppConfig(); _factory = factory; _atlasPay = atlasPay; _scopeFactory = scopeFactory; _logger = logger; }

    /// <summary>Queues one already-durable authenticated AtlasPay webhook as an immediate reconciliation wake-up.</summary>
    /// <param name="paymentId">Local <see cref="AtlasPayPaymentInfo.Id"/> already bound to the signed provider payload.</param>
    /// <returns><c>true</c> when the bounded wake-up queue accepted the id; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Queue admission is never the durability boundary. <see cref="AtlasPayPaymentInfo.WebhookReceivedAtUtc"/> is committed
    /// before this method is called, so queue loss or restart is recovered from users.db. The webhook body is never payment
    /// proof; every financial effect still requires an authoritative AtlasPay order inquiry.
    /// </remarks>
    public bool TryQueueWebhookTrigger(int paymentId)
        => paymentId > 0 && _webhookTriggers.Writer.TryWrite(paymentId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(_configuration.AtlasPayReconciliationIntervalSeconds, 10, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_webhookTriggers.Reader.TryRead(out var paymentId))
                {
                    await ReconcilePaymentAsync(paymentId, "atlaspay-webhook", false, stoppingToken);
                    await MarkWebhookProcessedIfConclusiveAsync(paymentId, stoppingToken);
                }

                await ReconcileDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "AtlasPay reconciliation scan failed."); }

            try
            {
                var webhookReady = _webhookTriggers.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var pollDue = Task.Delay(delay, stoppingToken);
                await Task.WhenAny(webhookReady, pollDue);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Processes due AtlasPay automatic work without allowing broad polling to race the signed webhook path.</summary>
    /// <param name="cancellationToken">Cancellation token for users.db reads and provider reconciliation.</param>
    /// <returns>A task completing after the bounded due batch has been processed.</returns>
    /// <remarks>
    /// With a webhook secret configured, only payments with a durably received but not conclusively processed webhook are
    /// eligible. The periodic timer merely recovers that received event after queue loss/restart; it does not scan or
    /// confirm unrelated invoices. Without a webhook secret, legacy bounded polling remains available for compatibility.
    /// </remarks>
    public async Task ReconcileDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var max = Math.Clamp(_configuration.AtlasPayReconciliationMaxAttempts, 1, 500);
        var batch = Math.Clamp(_configuration.AtlasPayReconciliationBatchSize, 1, 500);
        var webhookPrimary = AtlasPayPollingPolicy.UsesWebhookPrimary(_configuration);
        await using var db = _factory.CreateDbContext();

        List<int> ids;
        if (webhookPrimary)
        {
            ids = await db.AtlasPayPaymentInfos.AsNoTracking()
                .Where(x => x.ProviderOrderId != null &&
                            x.CreationState == AtlasPayCreationStates.Created &&
                            x.WebhookReceivedAtUtc != null &&
                            (x.WebhookProcessedAtUtc == null || x.WebhookProcessedAtUtc < x.WebhookReceivedAtUtc) &&
                            (x.NextInquiryAtUtc == null || x.NextInquiryAtUtc <= now))
                .OrderBy(x => x.WebhookReceivedAtUtc).ThenBy(x => x.Id)
                .Select(x => x.Id).Take(batch).ToListAsync(cancellationToken);
        }
        else
        {
            ids = await db.AtlasPayPaymentInfos.AsNoTracking()
                .Where(x => x.ProviderOrderId != null &&
                            (!x.IsAddedToBalance ||
                             (x.IsProvisionallyApproved && x.ProviderConfirmedAfterProvisionalAtUtc == null)) &&
                            x.CreationState == AtlasPayCreationStates.Created &&
                            x.SettlementState != AtlasPaySettlementStates.ManualReview &&
                            x.ProviderStatus != "rejected" && x.ProviderStatus != "expired" && x.ProviderStatus != "cancelled" &&
                            x.InquiryAttemptCount < max &&
                            x.NextInquiryAtUtc != null && x.NextInquiryAtUtc <= now)
                .OrderBy(x => x.NextInquiryAtUtc).ThenBy(x => x.Id)
                .Select(x => x.Id).Take(batch).ToListAsync(cancellationToken);
        }

        foreach (var id in ids)
        {
            if (webhookPrimary && await MarkWebhookProcessedIfConclusiveAsync(id, cancellationToken))
                continue;

            await ReconcilePaymentAsync(
                id,
                webhookPrimary ? "atlaspay-webhook" : "reconciliation-worker",
                false,
                cancellationToken);

            if (webhookPrimary)
                await MarkWebhookProcessedIfConclusiveAsync(id, cancellationToken);
        }
    }

    public async Task<NowPaymentsSettlementResult> ReconcilePaymentAsync(int paymentId, string source,
        bool useVerify = false, CancellationToken cancellationToken = default)
    {
        if (string.Equals(source, "atlaspay-webhook", StringComparison.Ordinal) &&
            AtlasPayPollingPolicy.UsesWebhookPrimary(_configuration) &&
            !await HasPendingWebhookReceiptAsync(paymentId, cancellationToken))
        {
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }

        using var lease = Gate.TryEnter(paymentId.ToString(CultureInfo.InvariantCulture));
        if (lease == null) return NowPaymentsSettlementResult.ProviderNotPaid();
        try { return await ReconcileCoreAsync(paymentId, source, useVerify, cancellationToken); }
        finally { lease.Dispose(); }
    }

    /// <summary>Returns whether the payment has a durable signed webhook event that still needs handling.</summary>
    /// <param name="paymentId">Internal AtlasPay payment id.</param>
    /// <param name="cancellationToken">Cancellation token for the users.db read.</param>
    /// <returns><c>true</c> only when a webhook receipt exists and is newer than its processed marker.</returns>
    private async Task<bool> HasPendingWebhookReceiptAsync(int paymentId, CancellationToken cancellationToken)
    {
        if (paymentId <= 0)
            return false;

        await using var db = _factory.CreateDbContext();
        return await db.AtlasPayPaymentInfos.AsNoTracking().AnyAsync(
            x => x.Id == paymentId &&
                 x.WebhookReceivedAtUtc != null &&
                 (x.WebhookProcessedAtUtc == null || x.WebhookProcessedAtUtc < x.WebhookReceivedAtUtc),
            cancellationToken);
    }

    /// <summary>Performs a fresh AtlasPay verification and, only if it remains safely pending, applies provisional admin credit.</summary>
    /// <param name="paymentId">Positive internal AtlasPay payment id selected by the super-admin flow.</param>
    /// <param name="approvedByTelegramUserId">Configured super-admin Telegram id persisted with the financial exception.</param>
    /// <param name="notifyChatId">Optional customer chat id for the provisional-credit notification.</param>
    /// <param name="source">Non-secret audit source for the final admin confirmation.</param>
    /// <param name="cancellationToken">Cancellation token for provider, users.db, wallet, ledger, and notification work.</param>
    /// <returns>
    /// Official settlement when AtlasPay now reports paid; otherwise provisional settlement only when the freshly
    /// verified row remains eligible. Tenant orders, terminal responses, manual-review rows, and provider failures fail closed.
    /// </returns>
    /// <remarks>
    /// The same payment-keyed reconciliation gate is held from provider verification through provisional admission, so a
    /// concurrent webhook cannot slip between the final provider check and the durable wallet claim.
    /// </remarks>
    public async Task<NowPaymentsSettlementResult> ReconcileAndApplyProvisionalAsync(
        int paymentId,
        long approvedByTelegramUserId,
        long? notifyChatId,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (paymentId <= 0 ||
            approvedByTelegramUserId <= 0 ||
            _configuration.AdminsUserIds?.Contains(approvedByTelegramUserId) != true)
        {
            return NowPaymentsSettlementResult.InvalidAmount();
        }

        using var lease = await Gate.EnterAsync(paymentId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        try
        {
            var official = await ReconcileCoreAsync(paymentId, source, useVerify: true, cancellationToken);
            await using var db = _factory.CreateDbContext();
            var payment = await db.AtlasPayPaymentInfos.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paymentId, cancellationToken);
            if (payment == null)
                return NowPaymentsSettlementResult.NotFound();

            if (AtlasPayStatuses.IsSuccess(payment.ProviderStatus) || payment.IsAddedToBalance)
                return official;
            if (!AtlasPaySettlementService.CanApplyProvisionalCredit(payment))
                return official;

            await using var scope = _scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>()
                .ApplyProvisionalPaymentAsync(payment, approvedByTelegramUserId, notifyChatId, cancellationToken);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Marks the latest durable signed webhook handled only after its state became conclusive.</summary>
    /// <param name="paymentId">Internal AtlasPay payment id whose webhook receipt may be complete.</param>
    /// <param name="cancellationToken">Cancellation token for users.db work.</param>
    /// <returns>
    /// <c>true</c> when there is no pending webhook work or the latest webhook was conclusively handled; <c>false</c>
    /// when the same durable event still requires a later authoritative retry.
    /// </returns>
    private async Task<bool> MarkWebhookProcessedIfConclusiveAsync(
        int paymentId,
        CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        var payment = await db.AtlasPayPaymentInfos
            .FirstOrDefaultAsync(x => x.Id == paymentId, cancellationToken);
        if (payment?.WebhookReceivedAtUtc == null)
            return true;
        if (payment.WebhookProcessedAtUtc.HasValue &&
            payment.WebhookProcessedAtUtc.Value >= payment.WebhookReceivedAtUtc.Value)
        {
            return true;
        }

        var conclusive =
            payment.ProviderConfirmedAfterProvisionalAtUtc.HasValue ||
            (payment.IsAddedToBalance && !payment.IsProvisionallyApproved) ||
            AtlasPayStatuses.IsTerminal(payment.ProviderStatus) ||
            string.Equals(payment.SettlementState, AtlasPaySettlementStates.ManualReview, StringComparison.Ordinal) ||
            string.Equals(payment.ReconciliationState, AtlasPayReconciliationStates.Escalated, StringComparison.Ordinal) ||
            string.Equals(payment.ReconciliationState, AtlasPayReconciliationStates.Exhausted, StringComparison.Ordinal);

        if (!conclusive)
            return false;

        payment.WebhookProcessedAtUtc = DateTime.UtcNow;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<NowPaymentsSettlementResult> ReconcileCoreAsync(int paymentId, string source, bool useVerify, CancellationToken token)
    {
        var context = new UserWorkflowStore(_factory);
        var payment = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos.FirstOrDefaultAsync(x => x.Id == paymentId, token));
        if (payment == null) return NowPaymentsSettlementResult.NotFound();
        var awaitingOfficialAfterProvisional =
            payment.IsAddedToBalance &&
            payment.IsProvisionallyApproved &&
            !payment.ProviderConfirmedAfterProvisionalAtUtc.HasValue;
        if ((payment.IsAddedToBalance ||
             string.Equals(payment.SettlementState, AtlasPaySettlementStates.Settled, StringComparison.Ordinal)) &&
            !awaitingOfficialAfterProvisional)
        {
            return NowPaymentsSettlementResult.AlreadyAdded(payment.BalanceAfter ?? 0);
        }
        if (!payment.ProviderOrderId.HasValue || payment.CreationState != AtlasPayCreationStates.Created)
            return NowPaymentsSettlementResult.ProviderNotPaid();
        if (payment.SettlementState == AtlasPaySettlementStates.ManualReview || payment.RequiresManualDelivery)
            return NowPaymentsSettlementResult.ProviderNotPaid();

        // Once an authoritative provider inquiry has persisted a successful paid state, crash/retry recovery must reuse
        // that durable evidence instead of generating another AtlasPay API request. Webhook remains the confirmation
        // source; users.db and the existing idempotent settlement sagas recover only the local financial/fulfillment tail.
        if (AtlasPayStatuses.IsSuccess(payment.ProviderStatus) && payment.PaidAtUtc.HasValue)
        {
            if (awaitingOfficialAfterProvisional)
            {
                await using var provisionalScope = _scopeFactory.CreateAsyncScope();
                await provisionalScope.ServiceProvider
                    .GetRequiredService<AtlasPaySettlementService>()
                    .RecordProviderConfirmationAfterProvisionalAsync(payment, source, token);
                return NowPaymentsSettlementResult.AlreadyAdded(payment.BalanceAfter ?? 0);
            }

            if (string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase))
            {
                await using var tenantScope = _scopeFactory.CreateAsyncScope();
                return await tenantScope.ServiceProvider
                    .GetRequiredService<TenantBotService>()
                    .ApplyPaidTenantOrderAsync(payment, source, token);
            }

            await using var walletScope = _scopeFactory.CreateAsyncScope();
            return await walletScope.ServiceProvider
                .GetRequiredService<AtlasPaySettlementService>()
                .ApplyOfficialPaymentAsync(payment, source, token);
        }

        if (AtlasPayStatuses.IsTerminal(payment.ProviderStatus))
            return NowPaymentsSettlementResult.ProviderNotPaid();
        try
        {
            var response = useVerify
                ? await _atlasPay.VerifyOrderAsync(payment.ProviderOrderId.Value, token)
                : await _atlasPay.GetOrderAsync(payment.ProviderOrderId.Value, token);
            payment.InquiryAttemptCount++; payment.LastInquiryAtUtc = DateTime.UtcNow;
            payment.ActualReceivedAmountToman = response.ActualReceivedAmountToman;
            payment.RequiresManualDelivery = response.RequiresManualDelivery;
            payment.ProviderStatus = response.Status;
            if (string.IsNullOrWhiteSpace(payment.TrackingCode) && !string.IsNullOrWhiteSpace(response.TrackingCode)) payment.TrackingCode = response.TrackingCode;

            var verified = AtlasPayPaymentVerifier.IsVerifiedForAutomaticSettlement(payment, response, out var error, out var manual);
            if (manual)
            {
                payment.SettlementState = AtlasPaySettlementStates.ManualReview; payment.ErrorCode = error;
                payment.ErrorMessage = "AtlasPay reported an accepted payment that requires manual delivery review.";
                payment.NextInquiryAtUtc = null; payment.UpdatedAtUtc = DateTime.UtcNow; await context.SaveAsync(token);
                _logger.LogError("AtlasPay accepted underpayment requires manual review. paymentId={PaymentId}, tenantOrderId={TenantOrderId}, trackingCode={TrackingCode}, actualReceivedAmountToman={ActualReceivedAmountToman}",
                    payment.Id, payment.TenantBotOrderId, payment.TrackingCode, payment.ActualReceivedAmountToman);
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }
            if (!verified)
            {
                if (AtlasPayStatuses.IsTerminal(response.Status)) { payment.NextInquiryAtUtc = null; payment.ErrorCode = $"provider_{response.Status}"; }
                else if (error != "provider_not_paid") { payment.SettlementState = AtlasPaySettlementStates.ManualReview; payment.NextInquiryAtUtc = null; payment.ErrorCode = error; }
                else { payment.ErrorCode = null; NextInquiry(payment); }
                payment.UpdatedAtUtc = DateTime.UtcNow; await context.SaveAsync(token);
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }
            payment.PaidAtUtc ??= DateTime.UtcNow;
            payment.ErrorCode = null;
            payment.ErrorMessage = null;
            payment.NextInquiryAtUtc = null;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveAsync(token);

            if (awaitingOfficialAfterProvisional)
            {
                await using var provisionalScope = _scopeFactory.CreateAsyncScope();
                await provisionalScope.ServiceProvider
                    .GetRequiredService<AtlasPaySettlementService>()
                    .RecordProviderConfirmationAfterProvisionalAsync(payment, source, token);
                return NowPaymentsSettlementResult.AlreadyAdded(payment.BalanceAfter ?? 0);
            }

            if (string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase))
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<TenantBotService>().ApplyPaidTenantOrderAsync(payment, source, token);
            }
            await using var settlementScope = _scopeFactory.CreateAsyncScope();
            return await settlementScope.ServiceProvider.GetRequiredService<AtlasPaySettlementService>().ApplyOfficialPaymentAsync(payment, source, token);
        }
        catch (Exception ex) when (ex is AtlasPayApiException or InvalidOperationException)
        {
            // Classify before scheduling anything. A permanent provider error (documented HTTP 400 invalid input,
            // 401 invalid API key, 404 order not found) must stop automatic traffic and escalate instead of being
            // retried forever, while a temporary failure keeps the bounded automatic retry budget.
            var reason = AtlasPayFailurePolicy.ReasonCode(ex);
            var retryable = AtlasPayFailurePolicy.IsRetryable(ex);
            payment.InquiryAttemptCount++;
            payment.LastInquiryAtUtc = DateTime.UtcNow;
            payment.ErrorCode = reason;
            // Persist only a stable operator-facing sentence; provider response bodies and transport text are never stored.
            payment.ErrorMessage = retryable
                ? "AtlasPay is temporarily unavailable; this payment will be re-checked automatically."
                : "AtlasPay rejected the verification request; automatic polling stopped and manual investigation is required.";
            payment.UpdatedAtUtc = DateTime.UtcNow;
            if (retryable) NextInquiry(payment);
            else
            {
                payment.NextInquiryAtUtc = null;
                payment.ReconciliationState = AtlasPayReconciliationStates.Escalated;
            }
            await context.SaveAsync(token);
            _logger.LogWarning("AtlasPay inquiry failed safely. paymentId={PaymentId}, tenantOrderId={TenantOrderId}, attempt={Attempt}, reason={Reason}, retryable={Retryable}",
                payment.Id, payment.TenantBotOrderId, payment.InquiryAttemptCount, reason, retryable);
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }
    }

    /// <summary>
    /// Schedules the next automatic inquiry for an unresolved payment, or marks the payment exhausted when the
    /// configured attempt budget has been consumed.
    /// </summary>
    /// <param name="payment">
    /// The tracked AtlasPay payment being reconciled. Its <see cref="AtlasPayPaymentInfo.InquiryAttemptCount"/> is
    /// compared with the configured maximum, and its reconciliation fields are updated in place.
    /// </param>
    /// <returns>
    /// The UTC time of the next automatic inquiry, or <c>null</c> when no further automatic inquiry will be scheduled.
    /// </returns>
    /// <remarks>
    /// A null result is never silent. When the budget is exhausted the payment is moved to
    /// <see cref="AtlasPayReconciliationStates.Exhausted"/>, the exhaustion timestamp is recorded once, and a stable
    /// <see cref="AtlasPayFailureCodes.ReconciliationExhausted"/> error code is stored when no more specific provider
    /// reason exists. The row therefore stays discoverable and super-admin verification can still retry it explicitly
    /// instead of the payment disappearing from reconciliation.
    /// </remarks>
    private DateTime? NextInquiry(AtlasPayPaymentInfo payment)
    {
        var max = Math.Clamp(_configuration.AtlasPayReconciliationMaxAttempts, 1, 500);
        if (payment.InquiryAttemptCount >= max)
        {
            payment.NextInquiryAtUtc = null;
            payment.ReconciliationState = AtlasPayReconciliationStates.Exhausted;
            payment.ReconciliationExhaustedAtUtc ??= DateTime.UtcNow;
            payment.ErrorCode ??= AtlasPayFailureCodes.ReconciliationExhausted;
            return null;
        }
        payment.ReconciliationState = AtlasPayReconciliationStates.Active;
        var seconds = Math.Clamp(_configuration.AtlasPayReconciliationIntervalSeconds, 10, 3600);
        payment.NextInquiryAtUtc = DateTime.UtcNow.AddSeconds(seconds);
        return payment.NextInquiryAtUtc;
    }
}
