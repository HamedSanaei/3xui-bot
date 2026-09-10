using System.Globalization;
using System.Net;
using System.Text;
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
        return ParseStatusResponse(json, "verify");
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

    private static AtlasPayOrderStatusResponse ParseStatusResponse(string json, string operation)
    {
        AtlasPayOrderStatusResponse result;
        try { result = JsonConvert.DeserializeObject<AtlasPayOrderStatusResponse>(json); }
        catch (JsonException ex) { throw new AtlasPayApiException(0, $"AtlasPay {operation} response was malformed.", false, ex); }
        if (result == null || !result.Success || result.Id <= 0 || string.IsNullOrWhiteSpace(result.Status) || result.TotalAmountToman <= 0)
            throw new AtlasPayApiException(0, $"AtlasPay {operation} response was structurally invalid.");
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

public sealed class AtlasPaySettlementService
{
    private static readonly AsyncKeyedGate SettlementGate = new();
    private readonly UserDbContextFactory _factory;
    private readonly CredentialsStore _credentials;
    private readonly WalletLedgerService _ledger;
    private readonly ReferralService _referrals;
    private readonly ILogger<AtlasPaySettlementService> _logger;

    public AtlasPaySettlementService(UserDbContextFactory factory, CredentialsStore credentials,
        WalletLedgerService ledger, ReferralService referrals, ILogger<AtlasPaySettlementService> logger)
    { _factory = factory; _credentials = credentials; _ledger = ledger; _referrals = referrals; _logger = logger; }

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
                await EnsureLedgerAsync(tracked, tracked.BalanceBefore ?? user.AccountBalance - tracked.BaseAmountToman,
                    tracked.BalanceAfter ?? user.AccountBalance, cancellationToken);
                await ProcessReferralAsync(tracked, cancellationToken);
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
            context.Add(PaymentSettlementNotification.CreateOwnedWalletCredit("atlaspay", tracked.Id, tracked.BotId,
                tracked.TelegramUserId, tracked.ChatId, tracked.BaseAmountToman,
                $"اعتبار کیف پول شما به میزان {tracked.BaseAmountToman.FormatCurrency()} افزایش یافت.", tracked.SettledAtUtc.Value));
            await context.SaveAsync(cancellationToken);
            await EnsureLedgerAsync(tracked, receipt.BeforeBalance, receipt.AfterBalance, cancellationToken);
            await ProcessReferralAsync(tracked, cancellationToken);
            if (!tracked.SuccessLoggedAtUtc.HasValue)
            {
                tracked.SuccessLoggedAtUtc = DateTime.UtcNow; tracked.UpdatedAtUtc = DateTime.UtcNow; await context.SaveAsync(cancellationToken);
                _logger.LogPayment("✅ پرداخت AtlasPay تایید و کیف پول شارژ شد\n\n" +
                    $"Payment ID: <code>AP:{tracked.Id}</code>\nTracking: <code>{WebUtility.HtmlEncode(tracked.TrackingCode)}</code>\n" +
                    $"مبلغ شارژ: <code>{tracked.BaseAmountToman.FormatCurrency()}</code>\nمنبع: <code>{WebUtility.HtmlEncode(source)}</code>");
            }
            return NowPaymentsSettlementResult.Applied(receipt.BeforeBalance, receipt.AfterBalance);
        }
        finally { lease.Dispose(); }
    }

    private Task<WalletLedgerEntry> EnsureLedgerAsync(AtlasPayPaymentInfo payment, long before, long after, CancellationToken token)
        => _ledger.RecordAsync(payment.TelegramUserId, WalletLedgerDirections.Credit, payment.BaseAmountToman, before, after,
            WalletLedgerReasons.WalletCharge, provider: "atlaspay", referenceType: nameof(AtlasPayPaymentInfo),
            referenceId: payment.Id.ToString(CultureInfo.InvariantCulture), orderId: payment.MerchantOrderRef,
            description: "AtlasPay wallet charge", botId: payment.BotId, botUsername: payment.BotUsername,
            botType: BotInstanceTypes.Owned, idempotencyKey: $"payment:atlaspay:{payment.Id}:credit", cancellationToken: token);

    private Task ProcessReferralAsync(AtlasPayPaymentInfo payment, CancellationToken token)
        => _referrals.ProcessFinalOwnedWalletPaymentAsync(new ReferralPaymentSource("atlaspay", payment.PaymentPurpose,
            payment.ProviderOrderId?.ToString(CultureInfo.InvariantCulture) ?? payment.MerchantOrderRef, payment.BotId,
            BotInstanceTypes.Owned, payment.TelegramUserId, payment.BaseAmountToman,
            payment.SettledAtUtc ?? payment.PaidAtUtc ?? DateTime.UtcNow, payment.IsAddedToBalance,
            AtlasPayStatuses.IsSuccess(payment.ProviderStatus), IsProvisional: false), token);
}

public sealed class AtlasPayReconciliationHostedService : BackgroundService
{
    private static readonly AsyncKeyedGate Gate = new();
    private readonly AppConfig _configuration;
    private readonly UserDbContextFactory _factory;
    private readonly AtlasPay _atlasPay;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AtlasPayReconciliationHostedService> _logger;

    public AtlasPayReconciliationHostedService(IConfiguration configuration, UserDbContextFactory factory, AtlasPay atlasPay,
        IServiceScopeFactory scopeFactory, ILogger<AtlasPayReconciliationHostedService> logger)
    { _configuration = configuration.Get<AppConfig>() ?? new AppConfig(); _factory = factory; _atlasPay = atlasPay; _scopeFactory = scopeFactory; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(_configuration.AtlasPayReconciliationIntervalSeconds, 10, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "AtlasPay reconciliation scan failed."); }
            await Task.Delay(delay, stoppingToken);
        }
    }

    public async Task ReconcileDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow; var max = Math.Clamp(_configuration.AtlasPayReconciliationMaxAttempts, 1, 500);
        var batch = Math.Clamp(_configuration.AtlasPayReconciliationBatchSize, 1, 500);
        await using var db = _factory.CreateDbContext();
        var ids = await db.AtlasPayPaymentInfos.AsNoTracking()
            .Where(x => x.ProviderOrderId != null && !x.IsAddedToBalance &&
                        x.CreationState == AtlasPayCreationStates.Created &&
                        x.SettlementState != AtlasPaySettlementStates.ManualReview &&
                        x.ProviderStatus != "rejected" && x.ProviderStatus != "expired" && x.ProviderStatus != "cancelled" &&
                        x.InquiryAttemptCount < max &&
                        x.NextInquiryAtUtc != null && x.NextInquiryAtUtc <= now)
            .OrderBy(x => x.NextInquiryAtUtc).ThenBy(x => x.Id).Select(x => x.Id).Take(batch).ToListAsync(cancellationToken);
        foreach (var id in ids) await ReconcilePaymentAsync(id, "reconciliation-worker", false, cancellationToken);
    }

    public async Task<NowPaymentsSettlementResult> ReconcilePaymentAsync(int paymentId, string source,
        bool useVerify = false, CancellationToken cancellationToken = default)
    {
        using var lease = Gate.TryEnter(paymentId.ToString(CultureInfo.InvariantCulture));
        if (lease == null) return NowPaymentsSettlementResult.ProviderNotPaid();
        try { return await ReconcileCoreAsync(paymentId, source, useVerify, cancellationToken); }
        finally { lease.Dispose(); }
    }

    private async Task<NowPaymentsSettlementResult> ReconcileCoreAsync(int paymentId, string source, bool useVerify, CancellationToken token)
    {
        var context = new UserWorkflowStore(_factory);
        var payment = await context.ReadAsync(async db => await db.AtlasPayPaymentInfos.FirstOrDefaultAsync(x => x.Id == paymentId, token));
        if (payment == null) return NowPaymentsSettlementResult.NotFound();
        if (payment.IsAddedToBalance || string.Equals(payment.SettlementState, AtlasPaySettlementStates.Settled, StringComparison.Ordinal))
            return NowPaymentsSettlementResult.AlreadyAdded(payment.BalanceAfter ?? 0);
        if (!payment.ProviderOrderId.HasValue || payment.CreationState != AtlasPayCreationStates.Created)
            return NowPaymentsSettlementResult.ProviderNotPaid();
        if (AtlasPayStatuses.IsTerminal(payment.ProviderStatus) || payment.SettlementState == AtlasPaySettlementStates.ManualReview)
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
                else { payment.ErrorCode = null; payment.NextInquiryAtUtc = NextInquiry(payment.InquiryAttemptCount); }
                payment.UpdatedAtUtc = DateTime.UtcNow; await context.SaveAsync(token);
                return NowPaymentsSettlementResult.ProviderNotPaid();
            }
            payment.PaidAtUtc ??= DateTime.UtcNow; payment.ErrorCode = null; payment.ErrorMessage = null; payment.NextInquiryAtUtc = null; payment.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveAsync(token);
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
            payment.InquiryAttemptCount++; payment.LastInquiryAtUtc = DateTime.UtcNow; payment.ErrorCode = "provider_check_failed";
            payment.ErrorMessage = ex.Message; payment.NextInquiryAtUtc = NextInquiry(payment.InquiryAttemptCount); payment.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveAsync(token);
            _logger.LogWarning("AtlasPay inquiry failed safely. paymentId={PaymentId}, tenantOrderId={TenantOrderId}, attempt={Attempt}, errorType={ErrorType}",
                payment.Id, payment.TenantBotOrderId, payment.InquiryAttemptCount, ex.GetType().Name);
            return NowPaymentsSettlementResult.ProviderNotPaid();
        }
    }

    private DateTime? NextInquiry(int attempts)
    {
        var max = Math.Clamp(_configuration.AtlasPayReconciliationMaxAttempts, 1, 500);
        if (attempts >= max) return null;
        var seconds = Math.Clamp(_configuration.AtlasPayReconciliationIntervalSeconds, 10, 3600);
        return DateTime.UtcNow.AddSeconds(seconds);
    }
}
