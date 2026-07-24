using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Adminbot.Domain;

/// <summary>
/// Persists one Tetraminator invoice in <c>users.db</c> for an owned wallet charge or tenant order.
/// </summary>
/// <remarks>
/// The provider callback is unsigned, so this row is never settled from callback data alone. The saved
/// <see cref="PayId"/> must be inquired from Tetraminator and the returned amount must match <see cref="AmountToman"/>.
/// </remarks>
public sealed class TetraminatorPaymentInfo
{
    /// <summary>Internal users.db primary key; never sent to the provider as proof of payment.</summary>
    [Key]
    public int Id { get; set; }
    /// <summary>Globally unique local order id used to locate an unsigned callback.</summary>
    public string OrderId { get; set; }
    /// <summary>Provider pay id returned by invoice creation and required for authoritative inquiry.</summary>
    public string PayId { get; set; }
    /// <summary>Provider-hosted payment URL sent to the customer.</summary>
    public string PaymentLink { get; set; }
    /// <summary>Exact expected gross payment amount in Iranian toman.</summary>
    public long AmountToman { get; set; }
    /// <summary>Latest provider payment status; only exact <c>paid</c> is final success.</summary>
    public string PaymentStatus { get; set; } = TetraminatorStatuses.Pending;
    /// <summary>Invoice-specific public callback URL containing only the non-secret local order id.</summary>
    public string CallbackUrl { get; set; }
    /// <summary>Telegram user id of the owned-wallet payer or tenant customer.</summary>
    public long TelegramUserId { get; set; }
    /// <summary>Telegram chat id used for best-effort payment notifications.</summary>
    public long ChatId { get; set; }
    /// <summary>Optional Telegram invoice message id.</summary>
    public long? TelMsgId { get; set; }
    /// <summary>Internal owned or tenant bot id that originated the invoice.</summary>
    public string BotId { get; set; } = BotContextAccessor.DefaultBotId;
    /// <summary>Bot username captured for audit without a token.</summary>
    public string BotUsername { get; set; } = BotContextAccessor.DefaultBotId;
    /// <summary>Payment target: owned wallet charge or direct tenant order.</summary>
    public string PaymentPurpose { get; set; } = TenantBotPaymentPurposes.WalletCharge;
    /// <summary>Optional users.db tenant order primary key; required for tenant fulfillment.</summary>
    public int? TenantBotOrderId { get; set; }
    /// <summary>Optional Telegram user id of the tenant storefront owner.</summary>
    public long? TenantOwnerTelegramUserId { get; set; }
    /// <summary>Sanitized invoice request JSON that never contains the API key.</summary>
    public string RawRequestJson { get; set; }
    /// <summary>Latest provider response JSON retained for protected operational diagnostics.</summary>
    public string RawResponseJson { get; set; }
    /// <summary>Whether the public unsigned callback endpoint was reached.</summary>
    public bool CallbackReceived { get; set; }
    /// <summary>UTC timestamp of the first callback trigger.</summary>
    public DateTime? CallbackReceivedAtUtc { get; set; }
    /// <summary>UTC timestamp of the latest authoritative provider inquiry.</summary>
    public DateTime? LastInquiryAtUtc { get; set; }
    /// <summary>Total provider inquiry attempts observed for this row.</summary>
    public int InquiryAttemptCount { get; set; }
    /// <summary>UTC timestamp when the local payment row was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp of the latest local status or audit update.</summary>
    public DateTime? UpdatedAtUtc { get; set; }
    /// <summary>UTC timestamp when Tetraminator first returned verified <c>paid</c>.</summary>
    public DateTime? PaidAtUtc { get; set; }
    /// <summary>UTC timestamp when wallet credit or tenant fulfillment completed.</summary>
    public DateTime? SettledAtUtc { get; set; }
    /// <summary>Exactly-once local settlement marker; for tenant rows it represents delivery, not customer wallet credit.</summary>
    public bool IsAddedToBalance { get; set; }
    /// <summary>Wallet balance in toman before the owned or tenant-owner settlement.</summary>
    public long? BalanceBefore { get; set; }
    /// <summary>Wallet balance in toman after the owned or tenant-owner settlement.</summary>
    public long? BalanceAfter { get; set; }
    /// <summary>Stable non-sensitive verification or provider error code.</summary>
    public string ErrorCode { get; set; }
    /// <summary>Protected operational error detail that must not be exposed verbatim to customers.</summary>
    public string ErrorMessage { get; set; }
    /// <summary>Whether a super-admin provisionally credited an owned wallet before official confirmation.</summary>
    public bool IsProvisionallyApproved { get; set; }
    /// <summary>UTC timestamp of the provisional financial exception.</summary>
    public DateTime? ProvisionalApprovedAtUtc { get; set; }
    /// <summary>Telegram user id of the authenticated super-admin who approved the exception.</summary>
    public long? ProvisionalApprovedByTelegramUserId { get; set; }
    /// <summary>UTC timestamp when official paid status reconciled a prior provisional credit without another mutation.</summary>
    public DateTime? ProviderConfirmedAfterProvisionalAtUtc { get; set; }

    /// <summary>
    /// Creates a pending Tetraminator wallet-charge row for the currently routed owned bot.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram id of the wallet owner.</param>
    /// <param name="amountToman">Requested wallet credit in Iranian toman; it must satisfy configured provider limits.</param>
    /// <param name="callbackUrl">Invoice-specific public callback URL containing the generated order id.</param>
    /// <param name="chatId">Telegram chat receiving invoice and status messages.</param>
    /// <returns>An unsaved payment row whose API key is deliberately not persisted.</returns>
    public static TetraminatorPaymentInfo CreateWalletCharge(
        long telegramUserId,
        long amountToman,
        string callbackUrl,
        long chatId)
    {
        return new TetraminatorPaymentInfo
        {
            OrderId = CreateOrderId(telegramUserId),
            AmountToman = amountToman,
            CallbackUrl = callbackUrl,
            TelegramUserId = telegramUserId,
            ChatId = chatId,
            BotId = BotContextAccessor.CurrentBotId,
            BotUsername = BotContextAccessor.CurrentBotUsername,
            PaymentPurpose = TenantBotPaymentPurposes.WalletCharge,
            PaymentStatus = TetraminatorStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Generates a globally unique local order id used to correlate the unsigned provider callback.
    /// </summary>
    /// <param name="telegramUserId">Telegram user id included only for operational correlation.</param>
    /// <returns>A non-secret order id with timestamp and random suffix.</returns>
    public static string CreateOrderId(long telegramUserId)
        => $"TelBotTetra-{DateTime.UtcNow:yyyyMMddHHmmss}-{telegramUserId}-{Guid.NewGuid():N}";

    /// <summary>
    /// Applies a successful invoice-creation response without changing settlement state.
    /// </summary>
    /// <param name="response">Provider response containing a stable pay id and Telegram payment link.</param>
    public void Apply(TetraminatorCreateInvoiceResponse response)
    {
        if (response == null)
            return;
        PayId = response.PayId ?? PayId;
        PaymentLink = response.PaymentLink ?? PaymentLink;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Applies a verified inquiry response and records the latest provider observation.
    /// </summary>
    /// <param name="response">Inquiry response returned for this row's saved pay id.</param>
    public void Apply(TetraminatorInquiryResponse response)
    {
        InquiryAttemptCount++;
        LastInquiryAtUtc = DateTime.UtcNow;
        if (response != null)
            PaymentStatus = response.PaymentStatus ?? PaymentStatus;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

/// <summary>
/// Known Tetraminator payment statuses used by local settlement guards.
/// </summary>
public static class TetraminatorStatuses
{
    /// <summary>Initial or not-yet-paid provider state.</summary>
    public const string Pending = "pending";
    /// <summary>The only documented Tetraminator status accepted for settlement.</summary>
    public const string Paid = "paid";

    /// <summary>
    /// Checks whether the provider explicitly returned its only documented successful status.
    /// </summary>
    /// <param name="status">Raw provider status; null and unknown values are not paid.</param>
    /// <returns><c>true</c> only for an exact case-insensitive <c>paid</c> value.</returns>
    public static bool IsPaid(string status)
        => string.Equals(status?.Trim(), Paid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Validates that an inquiry response belongs to the expected local invoice before settlement.
/// </summary>
public static class TetraminatorPaymentVerifier
{
    /// <summary>
    /// Checks provider success, paid status, pay-id equality, and exact toman amount equality.
    /// </summary>
    /// <param name="payment">Local users.db invoice used as the expected source of truth.</param>
    /// <param name="inquiry">Authoritative provider inquiry response.</param>
    /// <param name="errorCode">Receives a stable non-sensitive failure code for audit and UI routing.</param>
    /// <returns><c>true</c> only when all provider identity and amount checks pass.</returns>
    /// <remarks>
    /// Callback query values are untrusted and are deliberately absent from this decision. A mismatch must never
    /// change a wallet, fulfill a tenant order, or be converted into a paid local status.
    /// </remarks>
    public static bool IsVerifiedPaid(
        TetraminatorPaymentInfo payment,
        TetraminatorInquiryResponse inquiry,
        out string errorCode)
    {
        if (payment == null || inquiry == null || !inquiry.Status)
        {
            errorCode = "provider_inquiry_unsuccessful";
            return false;
        }
        if (!string.Equals(payment.PayId?.Trim(), inquiry.PayId?.Trim(), StringComparison.Ordinal))
        {
            errorCode = "provider_pay_id_mismatch";
            return false;
        }
        if (payment.AmountToman != inquiry.Amount)
        {
            errorCode = "provider_amount_mismatch";
            return false;
        }
        if (!TetraminatorStatuses.IsPaid(inquiry.PaymentStatus))
        {
            errorCode = "provider_not_paid";
            return false;
        }
        errorCode = null;
        return true;
    }
}

/// <summary>
/// Creates Tetraminator invoices and performs authoritative payment inquiries.
/// </summary>
/// <remarks>
/// Invoice creation is never automatically retried because the API exposes no idempotency key. Read-only inquiry
/// requests retry only transient transport and HTTP failures. The API key is added as a header and never logged.
/// </remarks>
public sealed class Tetraminator
{
    private readonly AppConfig _appConfig;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the production API client from application configuration.
    /// </summary>
    /// <param name="configuration">Configuration containing Tetraminator URL, key, timeout, and retry settings.</param>
    public Tetraminator(IConfiguration configuration)
        : this(configuration, new HttpClient())
    {
    }

    /// <summary>
    /// Creates an API client with an injected HTTP transport for deterministic verification.
    /// </summary>
    /// <param name="configuration">Application configuration; the API key is required for live calls.</param>
    /// <param name="httpClient">HTTP client used for provider requests; ownership remains with the caller.</param>
    /// <remarks>
    /// A missing base URL is tolerated while the gateway is disabled so application startup remains available. Live
    /// invoice or inquiry calls fail with a controlled configuration exception until a valid HTTP/HTTPS URL exists.
    /// </remarks>
    public Tetraminator(IConfiguration configuration, HttpClient httpClient)
    {
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var configuredBaseUrl = (_appConfig.TetraminatorApiBaseUrl ?? string.Empty).TrimEnd('/') + "/";
        if (_httpClient.BaseAddress == null &&
            Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseAddress) &&
            (baseAddress.Scheme == Uri.UriSchemeHttp || baseAddress.Scheme == Uri.UriSchemeHttps))
        {
            _httpClient.BaseAddress = baseAddress;
        }
    }

    /// <summary>
    /// Creates one provider invoice without automatic retry.
    /// </summary>
    /// <param name="amountToman">Invoice amount in Iranian toman.</param>
    /// <param name="callbackUrl">Absolute callback URL containing the local order id.</param>
    /// <param name="cancellationToken">Cancellation token for the single non-idempotent HTTP request.</param>
    /// <returns>A validated response containing non-empty pay id and payment link.</returns>
    /// <exception cref="TetraminatorApiException">Thrown for provider rejection, malformed responses, or transport failure.</exception>
    public async Task<TetraminatorCreateInvoiceResponse> CreateInvoiceAsync(
        long amountToman,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        var payload = new TetraminatorCreateInvoiceRequest
        {
            Price = amountToman,
            CallbackUrl = callbackUrl
        };
        var response = await SendAsync<TetraminatorCreateInvoiceResponse>(
            HttpMethod.Post,
            "invoice/create",
            payload,
            retryInquiry: false,
            cancellationToken);
        if (response?.Status != true || string.IsNullOrWhiteSpace(response.PayId) || string.IsNullOrWhiteSpace(response.PaymentLink))
            throw new TetraminatorApiException(0, "Tetraminator returned an incomplete invoice response.");
        return response;
    }

    /// <summary>
    /// Reads the authoritative status and amount for a saved Tetraminator pay id.
    /// </summary>
    /// <param name="payId">Provider pay id returned by invoice creation; it must not be empty.</param>
    /// <param name="cancellationToken">Cancellation token for retry delays and HTTP requests.</param>
    /// <returns>The provider inquiry response; callers must additionally compare pay id and amount.</returns>
    public Task<TetraminatorInquiryResponse> InquiryAsync(string payId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payId))
            throw new ArgumentException("Tetraminator pay id is required.", nameof(payId));
        return SendAsync<TetraminatorInquiryResponse>(
            HttpMethod.Get,
            "payment/inquiry/" + Uri.EscapeDataString(payId.Trim()),
            body: null,
            retryInquiry: true,
            cancellationToken);
    }

    /// <summary>
    /// Sends an authenticated request and safely deserializes the provider response.
    /// </summary>
    /// <typeparam name="T">Expected JSON response type.</typeparam>
    /// <param name="method">HTTP method; only GET inquiries are retryable.</param>
    /// <param name="relativePath">Provider-relative path without credentials.</param>
    /// <param name="body">Optional JSON body that must never contain the API key.</param>
    /// <param name="retryInquiry">Whether transient read-only failures may be retried.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Deserialized provider response.</returns>
    /// <exception cref="InvalidOperationException">Thrown when API credentials or the provider base URL are unavailable.</exception>
    /// <exception cref="TetraminatorApiException">Thrown for provider rejection, malformed JSON, or exhausted inquiry retries.</exception>
    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object body,
        bool retryInquiry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_appConfig.TetraminatorApiKey))
            throw new InvalidOperationException("Tetraminator API key is not configured.");
        if (_httpClient.BaseAddress == null)
            throw new InvalidOperationException("Tetraminator API base URL is not configured.");

        var maxAttempts = retryInquiry ? Math.Max(1, _appConfig.TetraminatorInquiryRetryCount + 1) : 1;
        Exception lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, relativePath);
            request.Headers.TryAddWithoutValidation("X-API-KEY", _appConfig.TetraminatorApiKey);
            if (body != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_appConfig.TetraminatorRequestTimeoutSeconds, 5, 120)));
            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var apiError = new TetraminatorApiException((int)response.StatusCode, responseBody);
                    if (!retryInquiry || !IsTransientStatus(response.StatusCode) || attempt == maxAttempts)
                        throw apiError;
                    lastError = apiError;
                }
                else
                {
                    var result = JsonConvert.DeserializeObject<T>(responseBody);
                    return result ?? throw new TetraminatorApiException((int)response.StatusCode, "Tetraminator returned an empty JSON response.");
                }
            }
            catch (Exception ex) when (retryInquiry && IsTransientException(ex, cancellationToken) && attempt < maxAttempts)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000 * attempt, 3000)), cancellationToken);
        }

        throw lastError as TetraminatorApiException ?? new TetraminatorApiException(0, "Tetraminator inquiry failed after transient retries.", lastError);
    }

    /// <summary>
    /// Identifies provider status codes that are safe to retry for read-only inquiries.
    /// </summary>
    /// <param name="statusCode">HTTP status returned by Tetraminator.</param>
    /// <returns><c>true</c> for 429 and all 5xx statuses; otherwise <c>false</c>.</returns>
    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    /// <summary>
    /// Identifies retryable transport failures while preserving caller-requested cancellation.
    /// </summary>
    /// <param name="exception">Transport or cancellation exception raised by the read-only inquiry request.</param>
    /// <param name="callerToken">Original caller token used to distinguish timeout from requested cancellation.</param>
    /// <returns><c>true</c> for HTTP transport errors and internal timeouts; otherwise <c>false</c>.</returns>
    private static bool IsTransientException(Exception exception, CancellationToken callerToken)
        => exception is HttpRequestException || (exception is OperationCanceledException && !callerToken.IsCancellationRequested);
}

/// <summary>
/// Settles officially paid and super-admin provisional Tetraminator owned-wallet charges.
/// </summary>
/// <remarks>
/// Tenant orders are explicitly excluded and are fulfilled by <see cref="TenantBotService"/>. Telegram delivery is
/// fail-soft and runs only after durable wallet/payment/ledger work.
/// </remarks>
public sealed class TetraminatorSettlementService
{
    private static readonly SemaphoreSlim SettlementGate = new(1, 1);
    private readonly UserDbContext _userDbContext;
    private readonly CredentialsDbContext _credentialsDbContext;
    private readonly WalletLedgerService _walletLedgerService;
    private readonly ReferralService _referralService;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotRegistry _botRegistry;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly ILogger<TetraminatorSettlementService> _logger;

    /// <summary>
    /// Creates the owned-wallet settlement boundary for Tetraminator payments.
    /// </summary>
    /// <param name="userDbContext">users.db context containing payment audit and settlement flags.</param>
    /// <param name="credentialsDbContext">credentials.db context containing the original user wallet balance.</param>
    /// <param name="walletLedgerService">Append-only users.db ledger writer with unique idempotency keys.</param>
    /// <param name="referralService">Existing global owned-bot referral settlement and reconciliation service.</param>
    /// <param name="botClientProvider">Provider used for best-effort notification through the originating owned bot.</param>
    /// <param name="botRegistry">Runtime bot metadata registry used to restore the originating bot context.</param>
    /// <param name="botContextAccessor">Async bot context accessor used while logging and notifying settlement.</param>
    /// <param name="logger">Structured operational logger; API credentials are never included.</param>
    public TetraminatorSettlementService(
        UserDbContext userDbContext,
        CredentialsDbContext credentialsDbContext,
        WalletLedgerService walletLedgerService,
        ReferralService referralService,
        BotClientProvider botClientProvider,
        BotRegistry botRegistry,
        BotContextAccessor botContextAccessor,
        ILogger<TetraminatorSettlementService> logger)
    {
        _userDbContext = userDbContext;
        _credentialsDbContext = credentialsDbContext;
        _walletLedgerService = walletLedgerService;
        _referralService = referralService;
        _botClientProvider = botClientProvider;
        _botRegistry = botRegistry;
        _botContextAccessor = botContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Credits an officially paid owned-bot wallet invoice exactly once.
    /// </summary>
    /// <param name="payment">Locally persisted payment already verified against provider pay id and amount.</param>
    /// <param name="source">Audit source such as callback, customer check, or super-admin check.</param>
    /// <param name="notifyChatId">Optional Telegram chat override.</param>
    /// <param name="cancellationToken">Cancellation token for database and notification work.</param>
    /// <returns>Applied, AlreadyAdded, ProviderNotPaid, UserNotFound, or NotFound.</returns>
    public async Task<NowPaymentsSettlementResult> ApplyOfficialPaymentAsync(
        TetraminatorPaymentInfo payment,
        string source,
        long? notifyChatId = null,
        CancellationToken cancellationToken = default)
    {
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();
        if (!IsWalletCharge(payment) ||
            !TetraminatorStatuses.IsPaid(payment.PaymentStatus) ||
            string.IsNullOrWhiteSpace(payment.PayId) ||
            !payment.PaidAtUtc.HasValue ||
            !string.IsNullOrWhiteSpace(payment.ErrorCode))
        {
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }

        await SettlementGate.WaitAsync(cancellationToken);
        try
        {
            var user = await _credentialsDbContext.GetUserStatusWithId(payment.TelegramUserId);
            if (user == null)
                return NowPaymentsSettlementResult.UserNotFound();

            if (payment.IsAddedToBalance)
            {
                if (payment.IsProvisionallyApproved)
                {
                    if (!payment.ProviderConfirmedAfterProvisionalAtUtc.HasValue)
                    {
                        payment.ProviderConfirmedAfterProvisionalAtUtc = DateTime.UtcNow;
                        payment.UpdatedAtUtc = DateTime.UtcNow;
                        await _userDbContext.SaveChangesAsync(cancellationToken);
                        LogOfficialConfirmationAfterProvisional(payment, user, source);
                    }
                }
                else
                {
                    await EnsureOfficialLedgerAsync(payment, payment.BalanceBefore ?? user.AccountBalance - payment.AmountToman, payment.BalanceAfter ?? user.AccountBalance, cancellationToken);
                    await ProcessReferralAsync(payment, cancellationToken);
                }
                return NowPaymentsSettlementResult.AlreadyAdded(user.AccountBalance);
            }

            var before = user.AccountBalance;
            if (!await _credentialsDbContext.AddFund(payment.TelegramUserId, payment.AmountToman))
                return NowPaymentsSettlementResult.UserNotFound();
            var after = checked(before + payment.AmountToman);

            payment.IsAddedToBalance = true;
            payment.BalanceBefore = before;
            payment.BalanceAfter = after;
            payment.SettledAtUtc ??= DateTime.UtcNow;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);
            await EnsureOfficialLedgerAsync(payment, before, after, cancellationToken);
            await ProcessReferralAsync(payment, cancellationToken);

            using (_botContextAccessor.Push(CreatePaymentBotContext(payment)))
            {
                await NotifyUserAsync(payment, notifyChatId ?? user.ChatID, false, cancellationToken);
                LogSettlement(payment, user, before, after, source, false);
            }
            return NowPaymentsSettlementResult.Applied(before, after);
        }
        finally
        {
            SettlementGate.Release();
        }
    }

    /// <summary>
    /// Applies a two-stage super-admin provisional credit to a non-terminal owned wallet invoice.
    /// </summary>
    /// <param name="payment">Pending wallet-charge payment; tenant orders are rejected.</param>
    /// <param name="approvedByTelegramUserId">Authenticated super-admin Telegram id persisted for audit.</param>
    /// <param name="notifyChatId">Optional customer chat id override.</param>
    /// <param name="cancellationToken">Cancellation token for wallet, users.db, ledger, and Telegram operations.</param>
    /// <returns>Applied for the first provisional credit or a non-mutating settlement status.</returns>
    public async Task<NowPaymentsSettlementResult> ApplyProvisionalPaymentAsync(
        TetraminatorPaymentInfo payment,
        long approvedByTelegramUserId,
        long? notifyChatId = null,
        CancellationToken cancellationToken = default)
    {
        if (payment == null)
            return NowPaymentsSettlementResult.NotFound();
        if (!IsWalletCharge(payment) || (!payment.IsAddedToBalance && !CanApplyProvisionalCredit(payment)))
            return NowPaymentsSettlementResult.InvalidAmount();

        await SettlementGate.WaitAsync(cancellationToken);
        try
        {
            var user = await _credentialsDbContext.GetUserStatusWithId(payment.TelegramUserId);
            if (user == null)
                return NowPaymentsSettlementResult.UserNotFound();
            if (payment.IsAddedToBalance)
            {
                if (payment.IsProvisionallyApproved)
                {
                    await EnsureProvisionalLedgerAsync(
                        payment,
                        payment.BalanceBefore ?? user.AccountBalance - payment.AmountToman,
                        payment.BalanceAfter ?? user.AccountBalance,
                        cancellationToken);
                }
                return NowPaymentsSettlementResult.AlreadyAdded(user.AccountBalance);
            }

            var before = user.AccountBalance;
            if (!await _credentialsDbContext.AddFund(payment.TelegramUserId, payment.AmountToman))
                return NowPaymentsSettlementResult.UserNotFound();
            var after = checked(before + payment.AmountToman);
            payment.IsAddedToBalance = true;
            payment.IsProvisionallyApproved = true;
            payment.ProvisionalApprovedAtUtc = DateTime.UtcNow;
            payment.ProvisionalApprovedByTelegramUserId = approvedByTelegramUserId;
            payment.BalanceBefore = before;
            payment.BalanceAfter = after;
            payment.SettledAtUtc = DateTime.UtcNow;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            await EnsureProvisionalLedgerAsync(payment, before, after, cancellationToken);

            using (_botContextAccessor.Push(CreatePaymentBotContext(payment)))
            {
                await NotifyUserAsync(payment, notifyChatId ?? user.ChatID, true, cancellationToken);
                LogSettlement(payment, user, before, after, "admin-provisional", true);
            }
            return NowPaymentsSettlementResult.Applied(before, after);
        }
        finally
        {
            SettlementGate.Release();
        }
    }

    /// <summary>
    /// Ensures the official wallet credit has a unique append-only ledger row.
    /// </summary>
    /// <param name="payment">Settled owned-wallet payment used for provider and reference identity.</param>
    /// <param name="before">Wallet balance in toman before the credit.</param>
    /// <param name="after">Wallet balance in toman after the credit.</param>
    /// <param name="cancellationToken">Cancellation token for the users.db ledger insert.</param>
    /// <returns>The existing or newly persisted ledger entry selected by its stable idempotency key.</returns>
    private Task<WalletLedgerEntry> EnsureOfficialLedgerAsync(TetraminatorPaymentInfo payment, long before, long after, CancellationToken cancellationToken)
    {
        var sourceKey = ReferralService.BuildSourcePaymentKey("tetraminator", TenantBotPaymentPurposes.WalletCharge, GetStablePaymentId(payment));
        return _walletLedgerService.RecordAsync(
            payment.TelegramUserId,
            WalletLedgerDirections.Credit,
            payment.AmountToman,
            before,
            after,
            WalletLedgerReasons.WalletCharge,
            provider: "tetraminator",
            referenceType: nameof(TetraminatorPaymentInfo),
            referenceId: payment.Id.ToString(CultureInfo.InvariantCulture),
            orderId: payment.OrderId,
            description: "Tetraminator wallet charge",
            botId: payment.BotId,
            botUsername: payment.BotUsername,
            botType: BotInstanceTypes.Owned,
            idempotencyKey: $"wallet-credit:{sourceKey}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Ensures a super-admin provisional wallet credit has one append-only ledger row.
    /// </summary>
    /// <param name="payment">Provisionally settled owned-wallet payment and its persisted approving administrator.</param>
    /// <param name="before">Wallet balance in toman before the provisional credit.</param>
    /// <param name="after">Wallet balance in toman after the provisional credit.</param>
    /// <param name="cancellationToken">Cancellation token for the users.db ledger insert or lookup.</param>
    /// <returns>The existing or newly persisted ledger entry selected by the provider-specific idempotency key.</returns>
    /// <remarks>
    /// This helper is also called on an already-settled retry. It repairs an interrupted ledger write without changing
    /// the credentials wallet a second time.
    /// </remarks>
    private Task<WalletLedgerEntry> EnsureProvisionalLedgerAsync(
        TetraminatorPaymentInfo payment,
        long before,
        long after,
        CancellationToken cancellationToken)
        => _walletLedgerService.RecordAsync(
            payment.TelegramUserId,
            WalletLedgerDirections.Credit,
            payment.AmountToman,
            before,
            after,
            WalletLedgerReasons.WalletCharge,
            provider: "tetraminator_provisional_admin",
            referenceType: nameof(TetraminatorPaymentInfo),
            referenceId: payment.Id.ToString(CultureInfo.InvariantCulture),
            orderId: payment.OrderId,
            description: $"Tetraminator provisional wallet charge approved by {payment.ProvisionalApprovedByTelegramUserId}",
            botId: payment.BotId,
            botUsername: payment.BotUsername,
            botType: BotInstanceTypes.Owned,
            idempotencyKey: $"wallet-credit:tetraminator-provisional:{GetStablePaymentId(payment)}",
            cancellationToken: cancellationToken);

    /// <summary>
    /// Sends an official, non-provisional owned-wallet payment to the existing global referral engine.
    /// </summary>
    /// <param name="payment">Officially paid and locally credited owned-wallet payment.</param>
    /// <param name="cancellationToken">Cancellation token for referral persistence and notifications.</param>
    /// <returns>A task that completes after reward application or durable retry state is recorded.</returns>
    private Task ProcessReferralAsync(TetraminatorPaymentInfo payment, CancellationToken cancellationToken)
        => _referralService.ProcessFinalOwnedWalletPaymentAsync(
            new ReferralPaymentSource(
                "tetraminator",
                payment.PaymentPurpose,
                GetStablePaymentId(payment),
                payment.BotId,
                BotInstanceTypes.Owned,
                payment.TelegramUserId,
                payment.AmountToman,
                payment.SettledAtUtc ?? payment.PaidAtUtc ?? DateTime.UtcNow,
                payment.IsAddedToBalance,
                TetraminatorStatuses.IsPaid(payment.PaymentStatus),
                payment.IsProvisionallyApproved),
            cancellationToken);

    /// <summary>
    /// Sends a best-effort customer notification after durable financial settlement.
    /// </summary>
    /// <param name="payment">Settled payment containing bot attribution and credited amount.</param>
    /// <param name="chatId">Telegram chat id of the wallet owner; zero suppresses delivery.</param>
    /// <param name="provisional">Whether the notification describes a super-admin provisional credit.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
    private async Task NotifyUserAsync(TetraminatorPaymentInfo payment, long chatId, bool provisional, CancellationToken cancellationToken)
    {
        if (chatId == 0)
            return;
        try
        {
            var text = provisional
                ? $"اعتبار کیف پول شما به میزان {payment.AmountToman.FormatCurrency()} به صورت موقت توسط مدیر افزایش یافت."
                : $"اعتبار کیف پول شما به میزان {payment.AmountToman.FormatCurrency()} افزایش یافت.";
            await _botClientProvider.GetClient(payment.BotId).SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tetraminator customer settlement notification failed. paymentId={PaymentId}, userId={UserId}", payment.Id, payment.TelegramUserId);
        }
    }

    /// <summary>
    /// Writes the central financial audit for an official or provisional wallet credit.
    /// </summary>
    /// <param name="payment">Settled payment identifiers and amount.</param>
    /// <param name="user">Wallet owner shown in the central private audit.</param>
    /// <param name="before">Wallet balance in toman before settlement.</param>
    /// <param name="after">Wallet balance in toman after settlement.</param>
    /// <param name="source">Non-secret settlement source label.</param>
    /// <param name="provisional">Whether the audit represents a provisional financial exception.</param>
    private void LogSettlement(TetraminatorPaymentInfo payment, CredUser user, long before, long after, string source, bool provisional)
    {
        var userSummary = TelegramUserLinkFormatter.HtmlSummary(user);
        var message = (provisional ? "⚠️ شارژ موقت تترامیناتور" : "✅ پرداخت ریالی تترامیناتور تایید شد") + "\n\n" +
                      userSummary + "\n\n" +
                      $"💰 مبلغ: <code>{Html(payment.AmountToman.FormatCurrency())}</code>\n" +
                      $"🧾 Order ID: <code>{Html(payment.OrderId)}</code>\n" +
                      $"🧾 Pay ID: <code>{Html(payment.PayId)}</code>\n" +
                      $"💳 موجودی قبل: <code>{Html(before.FormatCurrency())}</code>\n" +
                      $"💳 موجودی بعد: <code>{Html(after.FormatCurrency())}</code>\n" +
                      $"📡 منبع: <code>{Html(source)}</code>";
        _logger.LogPayment(message);
    }

    /// <summary>
    /// Logs one official confirmation after an earlier provisional credit without changing balance or ledger.
    /// </summary>
    /// <param name="payment">Payment officially confirmed after its prior provisional credit.</param>
    /// <param name="user">Wallet owner displayed in the central audit.</param>
    /// <param name="source">Non-secret callback or manual-check source.</param>
    private void LogOfficialConfirmationAfterProvisional(TetraminatorPaymentInfo payment, CredUser user, string source)
    {
        _logger.LogPayment(
            "ℹ️ تترامیناتور پرداخت موقت را بعداً تایید کرد\n\n" +
            TelegramUserLinkFormatter.HtmlSummary(user) + "\n\n" +
            $"🧾 Order ID: <code>{Html(payment.OrderId)}</code>\n" +
            $"🧾 Pay ID: <code>{Html(payment.PayId)}</code>\n" +
            $"📡 منبع تایید رسمی: <code>{Html(source)}</code>\n" +
            "🔒 کیف پول و ledger دوباره شارژ نشدند.");
    }

    /// <summary>
    /// Returns the provider pay id, local order id, or local row id for deterministic idempotency keys.
    /// </summary>
    /// <param name="payment">Payment whose strongest stable identifier is required.</param>
    /// <returns>Provider pay id, local order id, or invariant internal id in that priority order.</returns>
    private static string GetStablePaymentId(TetraminatorPaymentInfo payment)
        => !string.IsNullOrWhiteSpace(payment.PayId)
            ? payment.PayId.Trim()
            : !string.IsNullOrWhiteSpace(payment.OrderId)
                ? payment.OrderId.Trim()
                : payment.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Restricts wallet settlement to owned wallet-charge records, including legacy empty-purpose rows.
    /// </summary>
    /// <param name="payment">Payment being considered for owned-wallet settlement.</param>
    /// <returns><c>true</c> for owned wallet charges; <c>false</c> for tenant orders.</returns>
    private static bool IsWalletCharge(TetraminatorPaymentInfo payment)
        => string.IsNullOrWhiteSpace(payment?.PaymentPurpose) ||
           string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.WalletCharge, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Enforces provisional-credit eligibility at the financial boundary independently of Telegram UI checks.
    /// </summary>
    /// <param name="payment">Latest authoritatively inquired payment row proposed for a manual exception.</param>
    /// <returns>
    /// <c>true</c> only for an owned wallet charge with a saved pay id whose latest inquiry was non-paid and whose
    /// local wallet has not already been credited; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Requiring the stable <c>provider_not_paid</c> verification code prevents a stale, failed, mismatched, tenant,
    /// or never-inquired invoice from bypassing the two-stage super-admin workflow.
    /// </remarks>
    private static bool CanApplyProvisionalCredit(TetraminatorPaymentInfo payment)
        => IsWalletCharge(payment) &&
           !payment.IsAddedToBalance &&
           !string.IsNullOrWhiteSpace(payment.PayId) &&
           payment.LastInquiryAtUtc.HasValue &&
           !TetraminatorStatuses.IsPaid(payment.PaymentStatus) &&
           string.Equals(payment.ErrorCode, "provider_not_paid", StringComparison.Ordinal);

    /// <summary>
    /// Builds the original owned-bot runtime context captured on the payment row.
    /// </summary>
    /// <param name="payment">Payment containing originating bot id and username attribution.</param>
    /// <returns>Runtime context used only for notification and central payment logging.</returns>
    private BotRuntimeContext CreatePaymentBotContext(TetraminatorPaymentInfo payment)
    {
        var bot = _botRegistry.GetById(payment.BotId);
        return new BotRuntimeContext { Config = bot, Client = _botClientProvider.GetClient(bot?.Id) };
    }

    /// <summary>HTML-encodes provider and audit values before Telegram delivery.</summary>
    /// <param name="value">Potentially null text that may contain Telegram HTML metacharacters.</param>
    /// <returns>Non-null HTML-safe text.</returns>
    private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

/// <summary>
/// JSON request accepted by Tetraminator invoice creation.
/// </summary>
public sealed class TetraminatorCreateInvoiceRequest
{
    /// <summary>Requested invoice amount in Iranian toman.</summary>
    [JsonProperty("price")]
    public long Price { get; set; }
    /// <summary>Absolute unsigned callback URL invoked after provider-side payment activity.</summary>
    [JsonProperty("callback_url")]
    public string CallbackUrl { get; set; }
}

/// <summary>
/// Successful Tetraminator invoice-creation response.
/// </summary>
public sealed class TetraminatorCreateInvoiceResponse
{
    /// <summary>Whether the provider accepted and created the invoice.</summary>
    [JsonProperty("status")]
    public bool Status { get; set; }
    /// <summary>Provider diagnostic message retained for protected operational review.</summary>
    [JsonProperty("message")]
    public string Message { get; set; }
    /// <summary>Stable provider payment id required for all later inquiries.</summary>
    [JsonProperty("pay_id")]
    public string PayId { get; set; }
    /// <summary>Provider-hosted payment URL safe to send to the intended customer.</summary>
    [JsonProperty("payment_link")]
    public string PaymentLink { get; set; }
}

/// <summary>
/// Authoritative Tetraminator inquiry response used before any financial or tenant settlement.
/// </summary>
public sealed class TetraminatorInquiryResponse
{
    /// <summary>Whether the provider successfully resolved the requested pay id.</summary>
    [JsonProperty("status")]
    public bool Status { get; set; }
    /// <summary>Authoritative provider status; only exact <c>paid</c> permits settlement.</summary>
    [JsonProperty("payment_status")]
    public string PaymentStatus { get; set; }
    /// <summary>Provider pay id returned for identity comparison with the local row.</summary>
    [JsonProperty("pay_id")]
    public string PayId { get; set; }
    /// <summary>Authoritative paid or expected amount in Iranian toman used for exact comparison.</summary>
    [JsonProperty("amount")]
    public long Amount { get; set; }
}

/// <summary>
/// Sanitized exception for Tetraminator HTTP or response failures.
/// </summary>
/// <remarks>The API key is never included; response text is retained for protected operational logs.</remarks>
public sealed class TetraminatorApiException : Exception
{
    /// <summary>HTTP status code, or zero for transport and local response-validation failures.</summary>
    public int StatusCode { get; }
    /// <summary>Provider response retained for protected logs; callers must not expose it directly to customers.</summary>
    public string ResponseBody { get; }

    /// <summary>
    /// Creates a provider exception without exposing request headers or credentials.
    /// </summary>
    /// <param name="statusCode">HTTP status code, or zero for transport/validation failures.</param>
    /// <param name="responseBody">Provider response or sanitized local diagnostic.</param>
    /// <param name="innerException">Optional underlying transport exception.</param>
    public TetraminatorApiException(int statusCode, string responseBody, Exception innerException = null)
        : base($"Tetraminator API request failed with status {statusCode}.", innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
