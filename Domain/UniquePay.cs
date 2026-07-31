using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Telegram.Bot;

namespace Adminbot.Domain;

/// <summary>
/// Durable users.db record for one UniquePay owned-wallet charge or tenant storefront invoice.
/// </summary>
/// <remarks>
/// <see cref="HashId"/> is the merchant idempotency and lookup identity sent to UniquePay. The bearer token is never
/// persisted. Settlement requires a fresh authoritative inquiry whose identity, IRT amount, buyer fee, and paid flag
/// pass <see cref="UniquePayPaymentVerifier"/>.
/// </remarks>
public sealed class UniquePayPaymentInfo
{
    /// <summary>Internal users.db primary key.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Globally unique merchant hash sent to both UniquePay create and check operations.</summary>
    public string HashId { get; set; }

    /// <summary>Provider reference returned after invoice creation; stored as text because the API returns numeric and string forms.</summary>
    public string RefId { get; set; }

    /// <summary>UniquePay-hosted payment URL delivered only to the intended customer.</summary>
    public string PaymentLink { get; set; }

    /// <summary>Local source-of-truth amount in Iranian toman, excluding the buyer-paid UniquePay fee.</summary>
    public long BaseAmountToman { get; set; }

    /// <summary>Final IRT amount most recently reported by UniquePay, including the buyer fee.</summary>
    public long? ProviderAmountToman { get; set; }

    /// <summary>Buyer fee in Iranian toman most recently reported by UniquePay.</summary>
    public long? ProviderFeeToman { get; set; }

    /// <summary>Fee percentage snapshotted when the invoice was created; expected to be 12 for current business rules.</summary>
    public decimal FeePercent { get; set; } = 12m;

    /// <summary>Provider fee payer; only <c>buyer</c> is accepted for settlement.</summary>
    public string FeePayer { get; set; }

    /// <summary>Provider invoice currency; only <c>IRT</c> is accepted for settlement.</summary>
    public string Currency { get; set; }

    /// <summary>Latest local provider state: pending, paid, or failed verification.</summary>
    public string PaymentStatus { get; set; } = UniquePayStatuses.Pending;

    /// <summary>Latest value of UniquePay's informational <c>isVerified</c> field.</summary>
    public bool IsProviderVerified { get; set; }

    /// <summary>Telegram user id of the owned-wallet payer or tenant customer.</summary>
    public long TelegramUserId { get; set; }

    /// <summary>Telegram chat id used for invoice and best-effort settlement notifications.</summary>
    public long ChatId { get; set; }

    /// <summary>Optional Telegram message id containing the payment buttons.</summary>
    public long? TelMsgId { get; set; }

    /// <summary>Internal owned or tenant bot id that originated the invoice.</summary>
    public string BotId { get; set; } = BotContextAccessor.DefaultBotId;

    /// <summary>Originating bot username captured for safe audit attribution.</summary>
    public string BotUsername { get; set; } = BotContextAccessor.DefaultBotId;

    /// <summary>Payment target: owned wallet charge or tenant purchase/renew order.</summary>
    public string PaymentPurpose { get; set; } = TenantBotPaymentPurposes.WalletCharge;

    /// <summary>Nullable users.db tenant order primary key used only for tenant storefront payments.</summary>
    public int? TenantBotOrderId { get; set; }

    /// <summary>Nullable Telegram id of the tenant owner whose profit is credited after fulfillment.</summary>
    public long? TenantOwnerTelegramUserId { get; set; }

    /// <summary>Sanitized form payload retained for audit; it never contains the bearer token.</summary>
    public string RawRequestJson { get; set; }

    /// <summary>Latest provider JSON response retained for protected diagnostics.</summary>
    public string RawResponseJson { get; set; }

    /// <summary>UTC time of the latest authoritative <c>check-invoice</c> attempt.</summary>
    public DateTime? LastInquiryAtUtc { get; set; }

    /// <summary>UTC time when the reconciliation worker should next inspect this pending row.</summary>
    public DateTime? NextInquiryAtUtc { get; set; }

    /// <summary>Total number of read-only provider inquiry attempts recorded for the invoice.</summary>
    public int InquiryAttemptCount { get; set; }

    /// <summary>UTC time when the local payment row was first persisted.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC time of the latest local payment state change.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>UTC time when an authoritative UniquePay inquiry first passed all paid checks.</summary>
    public DateTime? PaidAtUtc { get; set; }

    /// <summary>UTC time when owned-wallet credit or tenant fulfillment completed.</summary>
    public DateTime? SettledAtUtc { get; set; }

    /// <summary>Exactly-once settlement marker; for tenant rows it represents successful shared fulfillment.</summary>
    public bool IsAddedToBalance { get; set; }

    /// <summary>
    /// Durable settlement claim state used to prevent concurrent worker, return, and customer-check processors from
    /// repeating wallet credit or tenant fulfillment.
    /// </summary>
    public string SettlementState { get; set; } = UniquePaySettlementStates.Pending;

    /// <summary>Non-secret unique id of the current settlement attempt, retained for crash and operator diagnosis.</summary>
    public string SettlementAttemptId { get; set; }

    /// <summary>UTC time when the current durable settlement claim was acquired.</summary>
    public DateTime? SettlementStartedAtUtc { get; set; }

    /// <summary>Wallet balance in toman before owned credit or tenant-owner profit settlement.</summary>
    public long? BalanceBefore { get; set; }

    /// <summary>Wallet balance in toman after owned credit or tenant-owner profit settlement.</summary>
    public long? BalanceAfter { get; set; }

    /// <summary>Stable, non-secret provider or verification error code.</summary>
    public string ErrorCode { get; set; }

    /// <summary>Protected operational error detail that must not be copied verbatim to customer messages.</summary>
    public string ErrorMessage { get; set; }

    /// <summary>UTC time when the current repeated reconciliation error was last sent to the logger channel.</summary>
    public DateTime? LastErrorLoggedAtUtc { get; set; }

    /// <summary>UTC time when successful payment settlement was reported to the central payment logger.</summary>
    public DateTime? SuccessLoggedAtUtc { get; set; }

    /// <summary>
    /// Creates an unsaved pending UniquePay row for an owned-bot wallet charge.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram id of the shared wallet owner.</param>
    /// <param name="chatId">Telegram chat id to receive the payment link and settlement message.</param>
    /// <param name="baseAmountToman">Wallet credit amount in Iranian toman, excluding the 12% buyer fee; must be positive.</param>
    /// <param name="feePercent">Fee percentage snapshotted from global configuration, normally 12.</param>
    /// <returns>An unsaved row with a unique merchant hash and no provider credential.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when amount or fee is outside its valid financial range.</exception>
    public static UniquePayPaymentInfo CreateWalletCharge(
        long telegramUserId,
        long chatId,
        long baseAmountToman,
        decimal feePercent)
    {
        if (baseAmountToman <= 0)
            throw new ArgumentOutOfRangeException(nameof(baseAmountToman), "UniquePay wallet charge must be positive.");
        if (feePercent < 0 || feePercent > 100)
            throw new ArgumentOutOfRangeException(nameof(feePercent), "UniquePay fee percent must be between zero and 100.");

        return new UniquePayPaymentInfo
        {
            HashId = CreateHashId(telegramUserId),
            TelegramUserId = telegramUserId,
            ChatId = chatId,
            BaseAmountToman = baseAmountToman,
            FeePercent = feePercent,
            PaymentPurpose = TenantBotPaymentPurposes.WalletCharge,
            BotId = BotContextAccessor.CurrentBotId,
            BotUsername = BotContextAccessor.CurrentBotUsername,
            PaymentStatus = UniquePayStatuses.Pending,
            NextInquiryAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Generates the merchant hash used as UniquePay's create/check identity.
    /// </summary>
    /// <param name="telegramUserId">Telegram payer id used only for operational correlation, never authentication.</param>
    /// <returns>A globally unique, non-secret ASCII hash id.</returns>
    public static string CreateHashId(long telegramUserId)
        => $"TelBotUnique-{DateTime.UtcNow:yyyyMMddHHmmss}-{telegramUserId}-{Guid.NewGuid():N}";

    /// <summary>
    /// Applies a successful create response without marking the invoice paid.
    /// </summary>
    /// <param name="response">UniquePay response containing the matching hash, reference, and hosted link.</param>
    public void Apply(UniquePayCreateInvoiceResponse response)
    {
        if (response == null)
            return;
        RefId = response.RefId ?? RefId;
        PaymentLink = response.PaymentLink ?? PaymentLink;
        RawResponseJson = response.RawResponseJson ?? RawResponseJson;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Records one authoritative inquiry observation and schedules the next pending scan.
    /// </summary>
    /// <param name="response">UniquePay check response, which may represent pending or paid state.</param>
    /// <param name="nextInquiryAtUtc">UTC time for the next worker attempt when settlement has not completed.</param>
    public void Apply(UniquePayCheckInvoiceResponse response, DateTime nextInquiryAtUtc)
    {
        InquiryAttemptCount++;
        LastInquiryAtUtc = DateTime.UtcNow;
        NextInquiryAtUtc = nextInquiryAtUtc;
        RawResponseJson = response?.RawResponseJson ?? RawResponseJson;
        if (response?.Invoice != null)
        {
            // Public check-invoice responses normally identify the invoice through invoice.id, not root refId.
            RefId ??= !string.IsNullOrWhiteSpace(response.RefId)
                ? response.RefId
                : response.Invoice.InvoiceId;
            ProviderAmountToman = response.Invoice.Amount;
            ProviderFeeToman = response.Invoice.Fee;
            Currency = response.Invoice.Currency;
            FeePayer = response.Invoice.FeePayer;
            IsProviderVerified = response.Invoice.IsVerified;
            PaymentStatus = response.Invoice.IsPaid ? UniquePayStatuses.Paid : UniquePayStatuses.Pending;
        }
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

/// <summary>
/// Local UniquePay status constants used by settlement and reconciliation guards.
/// </summary>
public static class UniquePayStatuses
{
    /// <summary>Invoice exists locally but has not passed an authoritative paid inquiry.</summary>
    public const string Pending = "pending";

    /// <summary>Authoritative inquiry passed all identity, IRT amount, buyer fee, and payment checks.</summary>
    public const string Paid = "paid";

    /// <summary>Invoice creation or authoritative verification failed and no financial side effect is permitted.</summary>
    public const string Failed = "failed";

    /// <summary>Provider explicitly reported that the unpaid invoice expired.</summary>
    public const string Expired = "expired";

    /// <summary>Provider explicitly reported that the unpaid invoice was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Compatibility alias for verification failures, which are represented by terminal <c>failed</c>.</summary>
    public const string VerificationFailed = Failed;

    /// <summary>
    /// Checks whether the local status represents an authoritatively verified paid invoice.
    /// </summary>
    /// <param name="status">Local payment status; null and unknown values are rejected.</param>
    /// <returns><c>true</c> only for exact case-insensitive <c>paid</c>.</returns>
    public static bool IsPaid(string status)
        => string.Equals(status?.Trim(), Paid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a local UniquePay status must no longer be polled automatically.
    /// </summary>
    /// <param name="status">Local status stored in users.db; null and unknown values remain non-terminal.</param>
    /// <returns><c>true</c> for failed, expired, or cancelled; otherwise <c>false</c>.</returns>
    public static bool IsTerminal(string status)
        => string.Equals(status?.Trim(), Failed, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status?.Trim(), Expired, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status?.Trim(), Cancelled, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps optional provider lifecycle fields to a safe terminal local state without treating them as payment proof.
    /// </summary>
    /// <param name="invoice">
    /// Invoice returned by the official inquiry. Current public documentation omits lifecycle fields, so null/unknown
    /// values remain pending; only explicit terminal values are recognized.
    /// </param>
    /// <returns>Expired, cancelled, failed, or <c>null</c> when the invoice should remain pending.</returns>
    public static string GetProviderTerminalStatus(UniquePayInvoiceData invoice)
    {
        if (invoice == null || invoice.IsPaid)
            return null;
        if (invoice.IsExpired == true)
            return Expired;
        if (invoice.IsCancelled == true || invoice.IsCanceled == true)
            return Cancelled;

        var providerStatus = (invoice.PaymentStatus ?? invoice.ProviderStatus)?.Trim();
        if (string.Equals(providerStatus, "expired", StringComparison.OrdinalIgnoreCase))
            return Expired;
        if (string.Equals(providerStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerStatus, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return Cancelled;
        }
        if (string.Equals(providerStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerStatus, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            return Failed;
        }

        return null;
    }
}

/// <summary>
/// Durable UniquePay settlement-claim states used across owned-wallet and tenant-order financial boundaries.
/// </summary>
public static class UniquePaySettlementStates
{
    /// <summary>No settlement processor currently owns the paid row.</summary>
    public const string Pending = "pending";

    /// <summary>A processor durably claimed the row before performing an external wallet or XUI side effect.</summary>
    public const string Processing = "processing";

    /// <summary>All required local financial or fulfillment effects were persisted.</summary>
    public const string Settled = "settled";

    /// <summary>A processor crashed or became ambiguous after claiming the row; automatic replay is fail-closed.</summary>
    public const string ManualReview = "manual_review";
}

/// <summary>
/// Verifies UniquePay inquiry identity, IRT currency, buyer fee, base amount, and paid state before settlement.
/// </summary>
public static class UniquePayPaymentVerifier
{
    /// <summary>
    /// Applies the fail-closed financial verification rules for one UniquePay inquiry.
    /// </summary>
    /// <param name="payment">
    /// Local users.db invoice containing immutable merchant hash, base amount in toman, and fee percentage snapshot.
    /// </param>
    /// <param name="response">Authoritative provider response returned by <c>/api/check-invoice</c>.</param>
    /// <param name="errorCode">Stable non-secret rejection code, or <c>null</c> when verification succeeds.</param>
    /// <returns><c>true</c> only when the invoice is paid and every financial/identity invariant matches.</returns>
    /// <remarks>
    /// <c>isVerified</c> is intentionally informational because UniquePay documentation marks it as a future feature.
    /// The allowed one-toman fee difference accounts only for provider rounding; no other mismatch is tolerated.
    /// </remarks>
    public static bool IsVerifiedPaid(
        UniquePayPaymentInfo payment,
        UniquePayCheckInvoiceResponse response,
        out string errorCode)
    {
        if (payment == null || response == null || !response.Status || response.Code != 200 || response.Invoice == null)
        {
            errorCode = "provider_inquiry_unsuccessful";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(response.HashId) &&
            !string.Equals(payment.HashId?.Trim(), response.HashId.Trim(), StringComparison.Ordinal))
        {
            errorCode = "provider_hash_id_mismatch";
            return false;
        }

        var providerInvoiceIdentity = !string.IsNullOrWhiteSpace(response.RefId)
            ? response.RefId.Trim()
            : response.Invoice.InvoiceId;
        if (!string.IsNullOrWhiteSpace(payment.RefId) &&
            !string.Equals(payment.RefId.Trim(), providerInvoiceIdentity, StringComparison.Ordinal))
        {
            errorCode = "provider_ref_id_mismatch";
            return false;
        }
        if (string.IsNullOrWhiteSpace(payment.RefId) && string.IsNullOrWhiteSpace(providerInvoiceIdentity))
        {
            errorCode = "provider_invoice_identity_missing";
            return false;
        }

        if (!response.Invoice.IsPaid)
        {
            errorCode = "provider_not_paid";
            return false;
        }

        if (!string.Equals(response.Invoice.Currency?.Trim(), "IRT", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "provider_currency_mismatch";
            return false;
        }

        if (!string.Equals(response.Invoice.FeePayer?.Trim(), "buyer", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "provider_fee_payer_mismatch";
            return false;
        }

        if (response.Invoice.Amount - response.Invoice.Fee != payment.BaseAmountToman)
        {
            errorCode = "provider_base_amount_mismatch";
            return false;
        }

        var expectedFee = decimal.Round(
            payment.BaseAmountToman * payment.FeePercent / 100m,
            decimals: 0,
            MidpointRounding.AwayFromZero);
        if (Math.Abs(response.Invoice.Fee - expectedFee) > 1m)
        {
            errorCode = "provider_fee_mismatch";
            return false;
        }

        errorCode = null;
        return true;
    }
}

/// <summary>
/// UniquePay HTTP client for generic form-encoded invoice creation and authoritative polling.
/// </summary>
/// <remarks>
/// Creation is executed once because duplicate/ambiguous requests must be resolved by their merchant hash. Only
/// read-only inquiries retry transient HTTP and transport failures. The bearer token is never logged or persisted.
/// </remarks>
public sealed class UniquePay
{
    private readonly AppConfig _configuration;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the production UniquePay client from application configuration.
    /// </summary>
    /// <param name="configuration">Configuration containing UniquePay host, bearer token, timeout, and inquiry retry count.</param>
    public UniquePay(IConfiguration configuration)
        : this(configuration, new HttpClient())
    {
    }

    /// <summary>
    /// Creates a UniquePay client with an injected HTTP transport for tests or custom runtime transport.
    /// </summary>
    /// <param name="configuration">Configuration containing startup UniquePay values; the token may be empty while disabled.</param>
    /// <param name="httpClient">HTTP client whose ownership remains with the caller.</param>
    public UniquePay(IConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration.Get<AppConfig>() ?? new AppConfig();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var baseUrl = (_configuration.UniquePayBaseUrl ?? string.Empty).TrimEnd('/') + "/";
        if (_httpClient.BaseAddress == null &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseAddress) &&
            (baseAddress.Scheme == Uri.UriSchemeHttp || baseAddress.Scheme == Uri.UriSchemeHttps))
        {
            _httpClient.BaseAddress = baseAddress;
        }
    }

    /// <summary>
    /// Creates one UniquePay invoice without automatic retry.
    /// </summary>
    /// <param name="hashId">Globally unique merchant hash already persisted in users.db.</param>
    /// <param name="amountToman">Base amount in Iranian toman/IRT, excluding the buyer-paid fee; must be positive.</param>
    /// <param name="redirectUrl">
    /// Absolute platform return URL containing the merchant hash only as a lookup hint; it is not a settlement callback.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the single create request.</param>
    /// <returns>A validated response containing matching hash id, provider reference, and payment link.</returns>
    /// <exception cref="UniquePayApiException">Thrown for provider rejection or malformed/incomplete response.</exception>
    public async Task<UniquePayCreateInvoiceResponse> CreateInvoiceAsync(
        string hashId,
        long amountToman,
        string redirectUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashId);
        if (amountToman <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountToman), "UniquePay invoice amount must be positive IRT.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hashId"] = hashId,
            ["amount"] = amountToman.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(redirectUrl))
            fields["redirectUrl"] = redirectUrl;

        var response = await SendFormAsync<UniquePayCreateInvoiceResponse>(
            "api/create-invoice",
            fields,
            retryInquiry: false,
            cancellationToken);

        if (!response.Status ||
            response.Code != 200 ||
            !string.Equals(response.HashId?.Trim(), hashId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(response.RefId) ||
            string.IsNullOrWhiteSpace(response.PaymentLink))
        {
            throw new UniquePayApiException(
                response.Code,
                response.RawResponseJson,
                "UniquePay returned an incomplete or mismatched invoice response.");
        }

        return response;
    }

    /// <summary>
    /// Reads the authoritative state and financial fields for one saved merchant hash.
    /// </summary>
    /// <param name="hashId">Exact merchant hash persisted before invoice creation.</param>
    /// <param name="cancellationToken">Cancellation token for bounded retry delays and HTTP attempts.</param>
    /// <returns>
    /// Parsed provider response. Callers must pass it through <see cref="UniquePayPaymentVerifier"/> before settlement.
    /// </returns>
    public Task<UniquePayCheckInvoiceResponse> CheckInvoiceAsync(
        string hashId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashId);
        return SendFormAsync<UniquePayCheckInvoiceResponse>(
            "api/check-invoice",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["hashId"] = hashId },
            retryInquiry: true,
            cancellationToken);
    }

    /// <summary>
    /// Sends one authenticated form request and parses its JSON response.
    /// </summary>
    /// <typeparam name="T">Expected UniquePay response DTO type.</typeparam>
    /// <param name="relativePath">Official provider-relative API route without credentials.</param>
    /// <param name="fields">Form fields; the collection must never include the bearer token.</param>
    /// <param name="retryInquiry">Whether transient read-only failures may be retried.</param>
    /// <param name="cancellationToken">Cancellation token for requests and backoff.</param>
    /// <returns>Deserialized response with its raw JSON attached for protected diagnostics.</returns>
    /// <exception cref="InvalidOperationException">Thrown when token or base URL is absent.</exception>
    /// <exception cref="UniquePayApiException">Thrown after a provider rejection, malformed response, or exhausted retry.</exception>
    private async Task<T> SendFormAsync<T>(
        string relativePath,
        IReadOnlyDictionary<string, string> fields,
        bool retryInquiry,
        CancellationToken cancellationToken)
        where T : UniquePayResponseBase
    {
        if (string.IsNullOrWhiteSpace(_configuration.UniquePayBusinessToken))
            throw new InvalidOperationException("UniquePay business token is not configured.");
        if (_httpClient.BaseAddress == null)
            throw new InvalidOperationException("UniquePay API base URL is not configured.");

        var attempts = retryInquiry ? Math.Max(1, _configuration.UniquePayInquiryRetryCount + 1) : 1;
        Exception lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.UniquePayBusinessToken);
            request.Content = new FormUrlEncodedContent(fields);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_configuration.UniquePayRequestTimeoutSeconds, 5, 120)));
            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var error = new UniquePayApiException(
                        (int)response.StatusCode,
                        responseBody,
                        $"UniquePay returned HTTP {(int)response.StatusCode}.");
                    if (!retryInquiry || !IsTransientStatus(response.StatusCode) || attempt == attempts)
                        throw error;
                    lastError = error;
                }
                else
                {
                    T parsed;
                    try
                    {
                        parsed = JsonConvert.DeserializeObject<T>(responseBody);
                    }
                    catch (JsonException ex)
                    {
                        throw new UniquePayApiException(
                            (int)response.StatusCode,
                            responseBody,
                            "UniquePay returned malformed JSON.",
                            ex);
                    }

                    if (parsed == null)
                        throw new UniquePayApiException((int)response.StatusCode, responseBody, "UniquePay returned empty JSON.");
                    parsed.RawResponseJson = responseBody;
                    return parsed;
                }
            }
            catch (Exception ex) when (retryInquiry && IsTransientException(ex, cancellationToken))
            {
                lastError = ex;
            }

            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500 * attempt, 2000)), cancellationToken);
        }

        throw lastError as UniquePayApiException ??
              new UniquePayApiException(0, null, "UniquePay inquiry failed after transient retries.", lastError);
    }

    /// <summary>
    /// Identifies HTTP results safe to retry for a read-only UniquePay inquiry.
    /// </summary>
    /// <param name="statusCode">Provider HTTP status.</param>
    /// <returns><c>true</c> for request timeout, rate limiting, and server errors.</returns>
    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           (int)statusCode == 429 ||
           (int)statusCode >= 500;

    /// <summary>
    /// Identifies retryable transport errors while preserving caller-requested cancellation.
    /// </summary>
    /// <param name="exception">Transport or timeout exception.</param>
    /// <param name="callerToken">Original token used to distinguish timeout from requested cancellation.</param>
    /// <returns><c>true</c> for HTTP transport failure or internal timeout.</returns>
    private static bool IsTransientException(Exception exception, CancellationToken callerToken)
        => exception is HttpRequestException ||
           (exception is OperationCanceledException && !callerToken.IsCancellationRequested);

    /// <summary>
    /// Determines whether a failed create attempt is known not to have produced a usable provider invoice.
    /// </summary>
    /// <param name="exception">Exception raised by the one allowed create attempt.</param>
    /// <returns>
    /// <c>true</c> for local configuration failures and definitive provider validation/authentication codes;
    /// <c>false</c> for transport timeouts, rate limits, server failures, malformed success responses, and duplicate
    /// hash responses that must remain inquiry-eligible because provider creation may have succeeded.
    /// </returns>
    /// <remarks>
    /// Callers use this classification only to schedule polling. It never marks an invoice paid and never retries the
    /// mutating create endpoint.
    /// </remarks>
    public static bool IsDefinitiveCreateFailure(Exception exception)
    {
        if (exception is InvalidOperationException)
            return true;
        if (exception is not UniquePayApiException providerError)
            return false;

        var code = providerError.StatusCode;
        return code is > 0 and < 500 &&
               code is not 103 and not 200 and not 408 and not 429;
    }
}

/// <summary>
/// Common fields returned by UniquePay API operations.
/// </summary>
public abstract class UniquePayResponseBase
{
    /// <summary>Whether UniquePay completed the requested API operation.</summary>
    [JsonProperty("status")]
    public bool Status { get; set; }

    /// <summary>Provider result/error code.</summary>
    [JsonProperty("code")]
    public int Code { get; set; }

    /// <summary>Merchant hash returned by the provider.</summary>
    [JsonProperty("hashId")]
    public string HashId { get; set; }

    /// <summary>Raw numeric or string provider reference token.</summary>
    [JsonProperty("refId")]
    public JToken RefIdValue { get; set; }

    /// <summary>Provider reference normalized to invariant text for storage and comparison.</summary>
    [JsonIgnore]
    public string RefId => RefIdValue?.Type == JTokenType.Null ? null : RefIdValue?.ToString();

    /// <summary>Raw provider JSON retained for protected diagnostics; never includes the request bearer token.</summary>
    [JsonIgnore]
    public string RawResponseJson { get; set; }
}

/// <summary>
/// Successful or rejected UniquePay invoice-creation response.
/// </summary>
public sealed class UniquePayCreateInvoiceResponse : UniquePayResponseBase
{
    /// <summary>Provider-hosted payment page URL.</summary>
    [JsonProperty("paymentLink")]
    public string PaymentLink { get; set; }

    /// <summary>Optional provider diagnostic text.</summary>
    [JsonProperty("message")]
    public string Message { get; set; }
}

/// <summary>
/// Authoritative UniquePay invoice inquiry response.
/// </summary>
public sealed class UniquePayCheckInvoiceResponse : UniquePayResponseBase
{
    /// <summary>Authoritative invoice financial and payment fields.</summary>
    [JsonProperty("invoice")]
    public UniquePayInvoiceData Invoice { get; set; }

    /// <summary>Optional provider diagnostic text.</summary>
    [JsonProperty("message")]
    public string Message { get; set; }
}

/// <summary>
/// Financial fields returned inside a UniquePay <c>check-invoice</c> result.
/// </summary>
public sealed class UniquePayInvoiceData
{
    /// <summary>Provider internal invoice identifier retained only for diagnostics.</summary>
    [JsonProperty("id")]
    public JToken IdValue { get; set; }

    /// <summary>
    /// Provider invoice identifier normalized to invariant text for comparison with the create response reference.
    /// </summary>
    [JsonIgnore]
    public string InvoiceId => IdValue?.Type == JTokenType.Null ? null : IdValue?.ToString();

    /// <summary>
    /// Optional provider lifecycle value when exposed as <c>status</c>; it is never accepted as proof of payment.
    /// </summary>
    [JsonProperty("status")]
    public string ProviderStatus { get; set; }

    /// <summary>
    /// Optional provider lifecycle value when exposed as <c>paymentStatus</c>; only explicit terminal values are mapped.
    /// </summary>
    [JsonProperty("paymentStatus")]
    public string PaymentStatus { get; set; }

    /// <summary>Optional provider expiry flag; absent values remain pending.</summary>
    [JsonProperty("isExpired")]
    public bool? IsExpired { get; set; }

    /// <summary>Optional British-spelling provider cancellation flag; absent values remain pending.</summary>
    [JsonProperty("isCancelled")]
    public bool? IsCancelled { get; set; }

    /// <summary>Optional American-spelling provider cancellation flag; absent values remain pending.</summary>
    [JsonProperty("isCanceled")]
    public bool? IsCanceled { get; set; }

    /// <summary>Final amount in IRT/toman including buyer fee.</summary>
    [JsonProperty("amount")]
    public long Amount { get; set; }

    /// <summary>Provider currency code; settlement requires <c>IRT</c>.</summary>
    [JsonProperty("currency")]
    public string Currency { get; set; }

    /// <summary>Provider-reported fee in Iranian toman.</summary>
    [JsonProperty("fee")]
    public long Fee { get; set; }

    /// <summary>Party paying the fee; settlement requires <c>buyer</c>.</summary>
    [JsonProperty("feePayer")]
    public string FeePayer { get; set; }

    /// <summary>Authoritative provider paid flag required for settlement.</summary>
    [JsonProperty("isPaid")]
    public bool IsPaid { get; set; }

    /// <summary>Future/optional provider verification flag retained for audit but not used as a paid condition.</summary>
    [JsonProperty("isVerified")]
    public bool IsVerified { get; set; }

    /// <summary>Return URL stored by UniquePay for the invoice.</summary>
    [JsonProperty("redirectUrl")]
    public string RedirectUrl { get; set; }
}

/// <summary>
/// Sanitized UniquePay provider exception.
/// </summary>
/// <remarks>The bearer token and request headers are never retained on this exception.</remarks>
public sealed class UniquePayApiException : Exception
{
    /// <summary>HTTP or provider result code; zero represents local transport/validation failure.</summary>
    public int StatusCode { get; }

    /// <summary>Provider response body retained for protected diagnostics and never shown verbatim to customers.</summary>
    public string ResponseBody { get; }

    /// <summary>
    /// Creates an exception that contains only non-credential provider diagnostics.
    /// </summary>
    /// <param name="statusCode">HTTP/provider code, or zero for local validation and transport failures.</param>
    /// <param name="responseBody">Provider response body; it must not contain request headers or bearer token.</param>
    /// <param name="message">Safe local explanation.</param>
    /// <param name="innerException">Optional underlying transport or JSON exception.</param>
    public UniquePayApiException(
        int statusCode,
        string responseBody,
        string message,
        Exception innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

/// <summary>
/// Credits officially verified UniquePay owned-wallet invoices exactly once.
/// </summary>
/// <remarks>
/// Tenant payments are deliberately rejected and routed through <see cref="TenantBotService"/>. Telegram notification
/// and central reporting occur only after wallet, payment marker, ledger, and referral work are durably applied.
/// </remarks>
public sealed class UniquePaySettlementService
{
    private static readonly SemaphoreSlim SettlementGate = new(1, 1);
    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly WalletLedgerService _walletLedgerService;
    private readonly ReferralService _referralService;
    private readonly BotClientProvider _botClientProvider;
    private readonly ILogger<UniquePaySettlementService> _logger;

    /// <summary>
    /// Creates the owned-wallet UniquePay settlement boundary.
    /// </summary>
    /// <param name="userDbContext">users.db context containing payment rows and settlement markers.</param>
    /// <param name="credentialsDbContext">credentials.db context containing the shared wallet balance.</param>
    /// <param name="walletLedgerService">Idempotent append-only wallet-ledger writer.</param>
    /// <param name="referralService">Global owned-bot referral engine for final official wallet payments.</param>
    /// <param name="botClientProvider">Bot client provider used for best-effort customer delivery.</param>
    /// <param name="logger">Operational and payment logger; provider credentials are never included.</param>
    public UniquePaySettlementService(
        UserDbContext userDbContext,
        CredentialsDbContext credentialsDbContext,
        WalletLedgerService walletLedgerService,
        ReferralService referralService,
        BotClientProvider botClientProvider,
        ILogger<UniquePaySettlementService> logger)
    {
        _userDbContext = userDbContext;
        _credentialsDbContext = credentialsDbContext;
        _walletLedgerService = walletLedgerService;
        _referralService = referralService;
        _botClientProvider = botClientProvider;
        _logger = logger;
    }

    /// <summary>
    /// Applies a fully verified owned-wallet UniquePay payment exactly once.
    /// </summary>
    /// <param name="payment">Tracked payment already verified against an authoritative check response.</param>
    /// <param name="source">Safe audit source such as customer-check, return-trigger, or reconciliation-worker.</param>
    /// <param name="notifyChatId">Optional Telegram destination override; null uses the saved payment chat.</param>
    /// <param name="cancellationToken">Cancellation token for wallet, database, referral, and notification work.</param>
    /// <returns>Applied, AlreadyAdded, ProviderNotPaid, UserNotFound, or NotFound.</returns>
    /// <remarks>
    /// The process-wide gate and persisted <see cref="UniquePayPaymentInfo.IsAddedToBalance"/> marker prevent duplicate
    /// wallet mutations when return, customer button, and worker race. The append-only ledger has a second unique key.
    /// </remarks>
    public async Task<NowPaymentsSettlementResult> ApplyOfficialPaymentAsync(
        UniquePayPaymentInfo payment,
        string source,
        long? notifyChatId = null,
        CancellationToken cancellationToken = default)
    {
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();
        if (!IsOwnedWalletCharge(payment) ||
            !UniquePayStatuses.IsPaid(payment.PaymentStatus) ||
            !payment.PaidAtUtc.HasValue)
        {
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }

        await SettlementGate.WaitAsync(cancellationToken);
        try
        {
            var tracked = await _userDbContext.UniquePayPaymentInfos
                .FirstOrDefaultAsync(x => x.Id == payment.Id, cancellationToken);
            if (tracked == null)
                return NowPaymentsSettlementResult.NotFound();
            if (!UniquePayStatuses.IsPaid(tracked.PaymentStatus))
                return NowPaymentsSettlementResult.ProviderNotPaid();

            var user = await _credentialsDbContext.GetUserStatusWithId(tracked.TelegramUserId);
            if (user == null)
            {
                tracked.ErrorCode = "wallet_user_not_found";
                tracked.ErrorMessage = "The credentials wallet user was not found.";
                tracked.NextInquiryAtUtc = DateTime.UtcNow.AddMinutes(1);
                tracked.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbContext.SaveChangesAsync(cancellationToken);
                return NowPaymentsSettlementResult.UserNotFound();
            }

            if (tracked.IsAddedToBalance)
            {
                if (!string.Equals(tracked.SettlementState, UniquePaySettlementStates.Settled, StringComparison.Ordinal))
                {
                    tracked.SettlementState = UniquePaySettlementStates.Settled;
                    tracked.SettlementAttemptId = null;
                    tracked.SettlementStartedAtUtc = null;
                    tracked.UpdatedAtUtc = DateTime.UtcNow;
                    await _userDbContext.SaveChangesAsync(cancellationToken);
                }
                await EnsureLedgerAsync(
                    tracked,
                    tracked.BalanceBefore ?? user.AccountBalance - tracked.BaseAmountToman,
                    tracked.BalanceAfter ?? user.AccountBalance,
                    cancellationToken);
                await ProcessReferralAsync(tracked, cancellationToken);
                return NowPaymentsSettlementResult.AlreadyAdded(user.AccountBalance);
            }

            if (await RejectActiveOrAmbiguousClaimAsync(tracked, cancellationToken))
                return NowPaymentsSettlementResult.ProviderNotPaid();

            var attemptId = Guid.NewGuid().ToString("N");
            var claimedAtUtc = DateTime.UtcNow;
            var claimed = await _userDbContext.UniquePayPaymentInfos
                .Where(x => x.Id == tracked.Id &&
                            !x.IsAddedToBalance &&
                            (x.SettlementState == null || x.SettlementState == UniquePaySettlementStates.Pending))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.SettlementState, UniquePaySettlementStates.Processing)
                        .SetProperty(x => x.SettlementAttemptId, attemptId)
                        .SetProperty(x => x.SettlementStartedAtUtc, claimedAtUtc)
                        .SetProperty(x => x.UpdatedAtUtc, claimedAtUtc),
                    cancellationToken);
            if (claimed != 1)
            {
                await _userDbContext.Entry(tracked).ReloadAsync(cancellationToken);
                return tracked.IsAddedToBalance
                    ? NowPaymentsSettlementResult.AlreadyAdded(tracked.BalanceAfter ?? user.AccountBalance)
                    : NowPaymentsSettlementResult.ProviderNotPaid();
            }

            await _userDbContext.Entry(tracked).ReloadAsync(cancellationToken);

            var before = user.AccountBalance;
            if (!await _credentialsDbContext.AddFund(tracked.TelegramUserId, tracked.BaseAmountToman))
            {
                tracked.SettlementState = UniquePaySettlementStates.Pending;
                tracked.SettlementAttemptId = null;
                tracked.SettlementStartedAtUtc = null;
                tracked.NextInquiryAtUtc = DateTime.UtcNow.AddMinutes(1);
                tracked.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbContext.SaveChangesAsync(cancellationToken);
                return NowPaymentsSettlementResult.UserNotFound();
            }
            var after = checked(before + tracked.BaseAmountToman);

            tracked.IsAddedToBalance = true;
            tracked.SettlementState = UniquePaySettlementStates.Settled;
            tracked.SettlementAttemptId = null;
            tracked.SettlementStartedAtUtc = null;
            tracked.BalanceBefore = before;
            tracked.BalanceAfter = after;
            tracked.SettledAtUtc ??= DateTime.UtcNow;
            tracked.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);
            await EnsureLedgerAsync(tracked, before, after, cancellationToken);
            await ProcessReferralAsync(tracked, cancellationToken);
            await NotifyCustomerAsync(tracked, notifyChatId ?? tracked.ChatId, cancellationToken);
            LogSettlementOnce(tracked, user, before, after, source);
            return NowPaymentsSettlementResult.Applied(before, after);
        }
        finally
        {
            SettlementGate.Release();
        }
    }

    /// <summary>
    /// Rejects an active settlement claim and converts a stale crash-ambiguous claim to manual review.
    /// </summary>
    /// <param name="payment">Tracked paid UniquePay row that has not yet reached the durable settled marker.</param>
    /// <param name="cancellationToken">Cancellation token for persisting the manual-review transition.</param>
    /// <returns>
    /// <c>true</c> when automatic settlement must stop because another processor owns the claim or a stale claim is
    /// ambiguous; <c>false</c> when a new atomic claim may be attempted.
    /// </returns>
    /// <remarks>
    /// A crash after credentials.db was credited but before users.db was finalized cannot be resolved safely without
    /// a cross-database transaction. This transition fails closed instead of risking a duplicate wallet credit.
    /// </remarks>
    private async Task<bool> RejectActiveOrAmbiguousClaimAsync(
        UniquePayPaymentInfo payment,
        CancellationToken cancellationToken)
    {
        if (string.Equals(payment.SettlementState, UniquePaySettlementStates.ManualReview, StringComparison.Ordinal))
            return true;
        if (!string.Equals(payment.SettlementState, UniquePaySettlementStates.Processing, StringComparison.Ordinal))
            return false;

        if (payment.SettlementStartedAtUtc.HasValue &&
            DateTime.UtcNow - payment.SettlementStartedAtUtc.Value < TimeSpan.FromMinutes(30))
        {
            return true;
        }

        payment.SettlementState = UniquePaySettlementStates.ManualReview;
        payment.ErrorCode = "settlement_claim_ambiguous";
        payment.ErrorMessage = "A previous UniquePay wallet settlement claim became stale and requires manual review.";
        payment.NextInquiryAtUtc = null;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        await _userDbContext.SaveChangesAsync(cancellationToken);
        _logger.LogError(
            "UniquePay wallet settlement stopped for manual review. paymentId={PaymentId}, attemptId={AttemptId}, userId={UserId}, amountToman={AmountToman}",
            payment.Id,
            payment.SettlementAttemptId,
            payment.TelegramUserId,
            payment.BaseAmountToman);
        return true;
    }

    /// <summary>
    /// Ensures a UniquePay wallet credit has one append-only audit row.
    /// </summary>
    /// <param name="payment">Settled owned-wallet payment.</param>
    /// <param name="before">Wallet balance in toman before credit.</param>
    /// <param name="after">Wallet balance in toman after credit.</param>
    /// <param name="cancellationToken">Cancellation token for users.db work.</param>
    /// <returns>The existing or newly inserted idempotent ledger row.</returns>
    private Task<WalletLedgerEntry> EnsureLedgerAsync(
        UniquePayPaymentInfo payment,
        long before,
        long after,
        CancellationToken cancellationToken)
    {
        var providerId = GetStableProviderId(payment);
        var sourceKey = ReferralService.BuildSourcePaymentKey(
            "uniquepay",
            TenantBotPaymentPurposes.WalletCharge,
            providerId);
        return _walletLedgerService.RecordAsync(
            payment.TelegramUserId,
            WalletLedgerDirections.Credit,
            payment.BaseAmountToman,
            before,
            after,
            WalletLedgerReasons.WalletCharge,
            provider: "uniquepay",
            referenceType: nameof(UniquePayPaymentInfo),
            referenceId: payment.Id.ToString(CultureInfo.InvariantCulture),
            orderId: payment.HashId,
            description: "UniquePay wallet charge",
            botId: payment.BotId,
            botUsername: payment.BotUsername,
            botType: BotInstanceTypes.Owned,
            idempotencyKey: $"wallet-credit:{sourceKey}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Presents one settled official UniquePay wallet payment to the global referral engine.
    /// </summary>
    /// <param name="payment">Final owned-wallet payment with durable local credit.</param>
    /// <param name="cancellationToken">Cancellation token for referral event processing.</param>
    /// <returns>A task that completes after reward work succeeds or is durably left retryable.</returns>
    private Task ProcessReferralAsync(UniquePayPaymentInfo payment, CancellationToken cancellationToken)
        => _referralService.ProcessFinalOwnedWalletPaymentAsync(
            new ReferralPaymentSource(
                "uniquepay",
                payment.PaymentPurpose,
                GetStableProviderId(payment),
                payment.BotId,
                BotInstanceTypes.Owned,
                payment.TelegramUserId,
                payment.BaseAmountToman,
                payment.SettledAtUtc ?? payment.PaidAtUtc ?? DateTime.UtcNow,
                payment.IsAddedToBalance,
                UniquePayStatuses.IsPaid(payment.PaymentStatus),
                IsProvisional: false),
            cancellationToken);

    /// <summary>
    /// Sends a best-effort confirmation through the originating owned bot after durable settlement.
    /// </summary>
    /// <param name="payment">Settled payment containing the originating bot and credited amount.</param>
    /// <param name="chatId">Telegram chat id; zero suppresses delivery.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    private async Task NotifyCustomerAsync(
        UniquePayPaymentInfo payment,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (chatId == 0)
            return;
        try
        {
            await _botClientProvider.GetClient(payment.BotId).SendTextMessageAsync(
                chatId,
                $"اعتبار کیف پول شما به میزان {payment.BaseAmountToman.FormatCurrency()} افزایش یافت.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "UniquePay customer settlement notification failed. paymentId={PaymentId}, userId={UserId}",
                payment.Id,
                payment.TelegramUserId);
        }
    }

    /// <summary>
    /// Sends the central one-time successful UniquePay payment report after durable financial work.
    /// </summary>
    /// <param name="payment">Settled payment identifiers and amounts.</param>
    /// <param name="user">Shared wallet owner shown in the private audit.</param>
    /// <param name="before">Wallet balance in toman before credit.</param>
    /// <param name="after">Wallet balance in toman after credit.</param>
    /// <param name="source">Safe settlement trigger label.</param>
    private void LogSettlementOnce(
        UniquePayPaymentInfo payment,
        CredUser user,
        long before,
        long after,
        string source)
    {
        if (payment.SuccessLoggedAtUtc.HasValue)
            return;
        payment.SuccessLoggedAtUtc = DateTime.UtcNow;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        _userDbContext.SaveChanges();

        _logger.LogPayment(
            "✅ پرداخت ریالی یونیک‌پی تایید شد\n\n" +
            TelegramUserLinkFormatter.HtmlSummary(user) + "\n\n" +
            $"💰 مبلغ پایه: <code>{Html(payment.BaseAmountToman.FormatCurrency())}</code>\n" +
            $"💸 کارمزد خریدار: <code>{Html((payment.ProviderFeeToman ?? 0).FormatCurrency())}</code>\n" +
            $"🧾 Hash ID: <code>{Html(payment.HashId)}</code>\n" +
            $"🧾 Ref ID: <code>{Html(payment.RefId)}</code>\n" +
            $"💳 موجودی قبل: <code>{Html(before.FormatCurrency())}</code>\n" +
            $"💳 موجودی بعد: <code>{Html(after.FormatCurrency())}</code>\n" +
            $"📡 منبع: <code>{Html(source)}</code>");
    }

    /// <summary>
    /// Selects the strongest provider identity used by wallet and referral idempotency keys.
    /// </summary>
    /// <param name="payment">UniquePay payment containing provider and merchant identifiers.</param>
    /// <returns>Provider reference when present; otherwise merchant hash or invariant local id.</returns>
    private static string GetStableProviderId(UniquePayPaymentInfo payment)
        => !string.IsNullOrWhiteSpace(payment.RefId)
            ? payment.RefId.Trim()
            : !string.IsNullOrWhiteSpace(payment.HashId)
                ? payment.HashId.Trim()
                : payment.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Restricts this settlement service to owned wallet-charge rows.
    /// </summary>
    /// <param name="payment">Payment proposed for wallet settlement.</param>
    /// <returns><c>true</c> for wallet charges and <c>false</c> for tenant orders.</returns>
    private static bool IsOwnedWalletCharge(UniquePayPaymentInfo payment)
        => string.IsNullOrWhiteSpace(payment?.PaymentPurpose) ||
           string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.WalletCharge, StringComparison.OrdinalIgnoreCase);

    /// <summary>HTML-encodes provider and audit values before Telegram delivery.</summary>
    /// <param name="value">Potentially null user/provider text.</param>
    /// <returns>Non-null HTML-safe text.</returns>
    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

/// <summary>
/// Periodically inquires pending UniquePay invoices and routes verified payments to idempotent owned or tenant settlement.
/// </summary>
/// <remarks>
/// The worker intentionally ignores the global creation switch: disabling UniquePay must not strand invoices that
/// customers received earlier. Repeated provider outages are throttled before they reach the Telegram logger channel.
/// </remarks>
public sealed class UniquePayReconciliationHostedService : BackgroundService
{
    private static readonly SemaphoreSlim ReconciliationGate = new(1, 1);
    private static readonly TimeSpan StaleSettlementClaimAge = TimeSpan.FromMinutes(30);
    private readonly AppConfig _configuration;
    private readonly UserDbContext _userDbContext;
    private readonly UniquePay _uniquePay;
    private readonly UniquePaySettlementService _ownedSettlement;
    private readonly TenantBotService _tenantBotService;
    private readonly ILogger<UniquePayReconciliationHostedService> _logger;

    /// <summary>
    /// Creates the UniquePay polling worker.
    /// </summary>
    /// <param name="configuration">Startup configuration containing interval and batch limits.</param>
    /// <param name="userDbContext">users.db context containing pending UniquePay rows.</param>
    /// <param name="uniquePay">Authenticated read-only inquiry client.</param>
    /// <param name="ownedSettlement">Idempotent owned-wallet settlement service.</param>
    /// <param name="tenantBotService">Shared tenant purchase/renew fulfillment boundary.</param>
    /// <param name="logger">Operational logger used for throttled provider and verification failures.</param>
    public UniquePayReconciliationHostedService(
        IConfiguration configuration,
        UserDbContext userDbContext,
        UniquePay uniquePay,
        UniquePaySettlementService ownedSettlement,
        TenantBotService tenantBotService,
        ILogger<UniquePayReconciliationHostedService> logger)
    {
        _configuration = configuration.Get<AppConfig>() ?? new AppConfig();
        _userDbContext = userDbContext;
        _uniquePay = uniquePay;
        _ownedSettlement = ownedSettlement;
        _tenantBotService = tenantBotService;
        _logger = logger;
    }

    /// <summary>
    /// Runs bounded reconciliation scans until application shutdown.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    /// <returns>A task representing the worker lifetime.</returns>
    /// <remarks>
    /// An unavailable token is tolerated while UniquePay has no existing rows. Once rows exist, the controlled
    /// configuration error is logged with throttling and the rows remain pending for later recovery.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.UniquePayReconciliationIntervalSeconds,
            10,
            3600));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UniquePay reconciliation scan failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>
    /// Reconciles one fair batch of due, unsettled UniquePay rows.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for users.db, provider, and settlement work.</param>
    /// <returns>A task that completes after all selected rows have been inspected.</returns>
    /// <remarks>
    /// The process-wide gate prevents a return request and the periodic scan from issuing overlapping inquiries for
    /// the same shared DbContext. Database settlement markers remain the final duplicate-prevention boundary.
    /// </remarks>
    public async Task ReconcileDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var staleClaimBefore = now.Subtract(StaleSettlementClaimAge);
        var batchSize = Math.Clamp(_configuration.UniquePayReconciliationBatchSize, 1, 500);
        var ids = await _userDbContext.UniquePayPaymentInfos
            .AsNoTracking()
            .Where(x => !x.IsAddedToBalance &&
                        x.PaymentStatus != UniquePayStatuses.Failed &&
                        x.PaymentStatus != UniquePayStatuses.Expired &&
                        x.PaymentStatus != UniquePayStatuses.Cancelled &&
                        x.SettlementState != UniquePaySettlementStates.ManualReview &&
                        (x.SettlementState != UniquePaySettlementStates.Processing ||
                         x.SettlementStartedAtUtc == null ||
                         x.SettlementStartedAtUtc <= staleClaimBefore) &&
                        (x.NextInquiryAtUtc == null || x.NextInquiryAtUtc <= now))
            .OrderBy(x => x.NextInquiryAtUtc)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
            await ReconcilePaymentAsync(id, "reconciliation-worker", cancellationToken);
    }

    /// <summary>
    /// Authoritatively checks and, when valid, settles one UniquePay row selected by internal id.
    /// </summary>
    /// <param name="paymentId">Internal users.db UniquePay row id, never a provider proof.</param>
    /// <param name="source">Safe trigger label used in financial audit logs.</param>
    /// <param name="cancellationToken">Cancellation token for provider, database, and fulfillment work.</param>
    /// <returns>
    /// Settlement result when paid; <see cref="NowPaymentsSettlementStatus.ProviderNotPaid"/> for pending/mismatch;
    /// or <see cref="NowPaymentsSettlementStatus.NotFound"/> when the local row no longer exists.
    /// </returns>
    /// <remarks>
    /// Callers may use this method for customer-check and return triggers. It never trusts query or callback fields
    /// beyond locating the local row and always performs <c>/api/check-invoice</c>.
    /// </remarks>
    public async Task<NowPaymentsSettlementResult> ReconcilePaymentAsync(
        int paymentId,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (!await ReconciliationGate.WaitAsync(0, cancellationToken))
            return NowPaymentsSettlementResult.ProviderNotPaid();
        try
        {
            return await ReconcilePaymentCoreAsync(paymentId, source, cancellationToken);
        }
        finally
        {
            ReconciliationGate.Release();
        }
    }

    /// <summary>
    /// Performs one UniquePay inquiry while the process-wide reconciliation gate is held.
    /// </summary>
    /// <param name="paymentId">Internal users.db UniquePay payment id.</param>
    /// <param name="source">Safe settlement trigger label.</param>
    /// <param name="cancellationToken">Cancellation token for provider and settlement operations.</param>
    /// <returns>Settlement result from the authoritative inquiry and downstream fulfillment.</returns>
    private async Task<NowPaymentsSettlementResult> ReconcilePaymentCoreAsync(
        int paymentId,
        string source,
        CancellationToken cancellationToken)
    {
        var payment = await _userDbContext.UniquePayPaymentInfos
            .FirstOrDefaultAsync(x => x.Id == paymentId, cancellationToken);
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();
        if (payment.IsAddedToBalance)
            return NowPaymentsSettlementResult.AlreadyAdded(payment.BalanceAfter ?? 0);
        if (UniquePayStatuses.IsTerminal(payment.PaymentStatus) ||
            string.Equals(payment.SettlementState, UniquePaySettlementStates.ManualReview, StringComparison.Ordinal))
        {
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }

        var nextDelay = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.UniquePayReconciliationIntervalSeconds,
            10,
            3600));
        try
        {
            var response = await _uniquePay.CheckInvoiceAsync(payment.HashId, cancellationToken);
            payment.Apply(response, DateTime.UtcNow.Add(nextDelay));
            var providerTerminalStatus = UniquePayStatuses.GetProviderTerminalStatus(response.Invoice);
            if (providerTerminalStatus != null)
            {
                payment.PaymentStatus = providerTerminalStatus;
                payment.NextInquiryAtUtc = null;
                payment.ErrorCode = $"provider_{providerTerminalStatus}";
                payment.ErrorMessage = "UniquePay reported a terminal unpaid invoice state.";
                payment.UpdatedAtUtc = DateTime.UtcNow;
                LogFailureWithThrottle(payment, "UniquePay reported a terminal unpaid invoice.", null);
                await _userDbContext.SaveChangesAsync(cancellationToken);
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }

            var verified = UniquePayPaymentVerifier.IsVerifiedPaid(payment, response, out var errorCode);
            if (!verified)
            {
                payment.ErrorCode = string.Equals(errorCode, "provider_not_paid", StringComparison.Ordinal)
                    ? null
                    : errorCode;
                payment.ErrorMessage = payment.ErrorCode == null ? null : "UniquePay verification rejected provider data.";
                if (payment.ErrorCode != null)
                {
                    payment.PaymentStatus = UniquePayStatuses.Failed;
                    payment.NextInquiryAtUtc = null;
                    LogFailureWithThrottle(payment, "UniquePay payment verification mismatch.", null);
                }
                await _userDbContext.SaveChangesAsync(cancellationToken);
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }

            // The documented check response exposes the provider identity as invoice.id and may omit root refId.
            payment.RefId ??= !string.IsNullOrWhiteSpace(response.RefId)
                ? response.RefId
                : response.Invoice.InvoiceId;
            payment.PaymentStatus = UniquePayStatuses.Paid;
            payment.PaidAtUtc ??= DateTime.UtcNow;
            payment.NextInquiryAtUtc = null;
            payment.ErrorCode = null;
            payment.ErrorMessage = null;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            if (string.Equals(
                    payment.PaymentPurpose,
                    TenantBotPaymentPurposes.TenantOrder,
                    StringComparison.OrdinalIgnoreCase))
            {
                return await _tenantBotService.ApplyPaidTenantOrderAsync(payment, source, cancellationToken);
            }

            return await _ownedSettlement.ApplyOfficialPaymentAsync(
                payment,
                source,
                payment.ChatId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is UniquePayApiException or HttpRequestException or InvalidOperationException)
        {
            payment.InquiryAttemptCount++;
            payment.LastInquiryAtUtc = DateTime.UtcNow;
            payment.NextInquiryAtUtc = DateTime.UtcNow.Add(nextDelay);
            payment.ErrorCode = "provider_check_failed";
            payment.ErrorMessage = ex.Message;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            LogFailureWithThrottle(payment, "UniquePay invoice inquiry failed.", ex);
            await _userDbContext.SaveChangesAsync(cancellationToken);
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }
    }

    /// <summary>
    /// Sends a transition/hourly-throttled logger-channel error for one pending payment.
    /// </summary>
    /// <param name="payment">Payment whose safe identifiers and amount are logged.</param>
    /// <param name="message">Non-secret failure category.</param>
    /// <param name="exception">Optional sanitized provider/transport exception.</param>
    private void LogFailureWithThrottle(
        UniquePayPaymentInfo payment,
        string message,
        Exception exception)
    {
        var now = DateTime.UtcNow;
        if (payment.LastErrorLoggedAtUtc.HasValue &&
            now - payment.LastErrorLoggedAtUtc.Value < TimeSpan.FromHours(1))
        {
            return;
        }

        payment.LastErrorLoggedAtUtc = now;
        _logger.LogError(
            exception,
            "{Message} paymentId={PaymentId}, hashId={HashId}, tenantOrderId={TenantOrderId}, botId={BotId}, amountToman={AmountToman}, errorCode={ErrorCode}",
            message,
            payment.Id,
            payment.HashId,
            payment.TenantBotOrderId,
            payment.BotId,
            payment.BaseAmountToman,
            payment.ErrorCode);
    }
}
