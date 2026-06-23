using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Adminbot.Domain
{
    public class HooshPayPaymentInfo
    {
        [Key]
        public int Id { get; set; }
        public string OrderId { get; set; }
        public string InvoiceUid { get; set; }
        public string PaymentUrl { get; set; }
        public long AmountToman { get; set; }
        public long PayableAmountToman { get; set; }
        public long MerchantCreditToman { get; set; }
        public long FeeAmountToman { get; set; }
        public decimal FeePercent { get; set; }
        public string FeeMode { get; set; } = HooshPayFeeModes.Buyer;
        public string PaymentStatus { get; set; }
        public string TrackingCode { get; set; }
        public string RawRequestJson { get; set; }
        public string RawResponseJson { get; set; }
        public string RawIpnJson { get; set; }
        public string IpnCallbackUrl { get; set; }
        public string ReturnUrl { get; set; }
        public long TelegramUserId { get; set; }
        public long ChatId { get; set; }
        public long? TelMsgId { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public DateTime? SettledAtUtc { get; set; }
        public bool IsAddedToBalance { get; set; }
        public long? BalanceBefore { get; set; }
        public long? BalanceAfter { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static HooshPayPaymentInfo CreateWalletCharge(
            long telegramUserId,
            long amountToman,
            string callbackUrl,
            string returnUrl,
            long? chatId = null)
        {
            return new HooshPayPaymentInfo
            {
                OrderId = CreateOrderId(telegramUserId),
                AmountToman = amountToman,
                FeeMode = HooshPayFeeModes.Buyer,
                IpnCallbackUrl = callbackUrl,
                ReturnUrl = returnUrl,
                TelegramUserId = telegramUserId,
                ChatId = chatId ?? 0,
                PaymentStatus = HooshPayStatuses.Pending,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        public static string CreateOrderId(long telegramUserId)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"TelBotHoosh-{DateTime.UtcNow:yyyyMMddHHmmss}-{telegramUserId}-{suffix}";
        }

        public void Apply(HooshPayInvoiceData data)
        {
            if (data == null)
                return;

            InvoiceUid = data.uid ?? InvoiceUid;
            AmountToman = data.amount == 0 ? AmountToman : data.amount;
            FeeMode = data.fee_mode ?? FeeMode;
            FeePercent = data.fee_percent == 0 ? FeePercent : data.fee_percent;
            FeeAmountToman = data.fee_amount == 0 ? FeeAmountToman : data.fee_amount;
            PayableAmountToman = data.payable_amount == 0 ? PayableAmountToman : data.payable_amount;
            MerchantCreditToman = data.merchant_credit == 0 ? MerchantCreditToman : data.merchant_credit;
            PaymentStatus = data.status ?? PaymentStatus;
            PaymentUrl = data.payment_url ?? PaymentUrl;
            TrackingCode = data.tracking_code ?? TrackingCode;
            PaidAtUtc = data.paid_at ?? PaidAtUtc;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        public void Apply(HooshPayIpn ipn)
        {
            if (ipn == null)
                return;

            InvoiceUid = ipn.invoice ?? InvoiceUid;
            AmountToman = ipn.amount == 0 ? AmountToman : ipn.amount;
            PayableAmountToman = ipn.payable_amount == 0 ? PayableAmountToman : ipn.payable_amount;
            MerchantCreditToman = ipn.merchant_credit == 0 ? MerchantCreditToman : ipn.merchant_credit;
            FeeAmountToman = ipn.fee_amount == 0 ? FeeAmountToman : ipn.fee_amount;
            FeeMode = ipn.fee_mode ?? FeeMode;
            PaymentStatus = ipn.status ?? PaymentStatus;
            TrackingCode = ipn.tracking_code ?? TrackingCode;
            PaidAtUtc = ipn.paid_at ?? PaidAtUtc;
            UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public class HooshPay
    {
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture
        };

        private readonly AppConfig _appConfig;
        private readonly HttpClient _httpClient;

        public HooshPay(IConfiguration configuration)
            : this(configuration, new HttpClient())
        {
        }

        public HooshPay(IConfiguration configuration, HttpClient httpClient)
        {
            _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
            _httpClient = httpClient;
            _httpClient.BaseAddress ??= BuildBaseApiUri(_appConfig.HooshPayBaseUrl);
        }

        public async Task<HooshPayCreateInvoiceResponse> CreateInvoiceAsync(
            long amountToman,
            string orderId,
            string description,
            CancellationToken cancellationToken = default)
        {
            var request = new HooshPayCreateInvoiceRequest
            {
                amount = amountToman,
                fee_mode = HooshPayFeeModes.Buyer,
                order_id = orderId,
                description = description,
                callback_url = _appConfig.HooshPayIpnUrl,
                return_url = _appConfig.HooshPayReturnUrl
            };

            return await SendAsync<HooshPayCreateInvoiceResponse>(
                HttpMethod.Post,
                "invoices",
                request,
                cancellationToken);
        }

        public async Task<HooshPayInvoiceResponse> GetInvoiceAsync(
            string invoiceUid,
            CancellationToken cancellationToken = default)
        {
            return await SendAsync<HooshPayInvoiceResponse>(
                HttpMethod.Get,
                $"invoices/{Uri.EscapeDataString(invoiceUid)}",
                null,
                cancellationToken);
        }

        public async Task<HooshPayVerifyResponse> VerifyInvoiceAsync(
            string invoiceUid,
            CancellationToken cancellationToken = default)
        {
            return await SendAsync<HooshPayVerifyResponse>(
                HttpMethod.Post,
                $"invoices/{Uri.EscapeDataString(invoiceUid)}/verify",
                null,
                cancellationToken);
        }

        private async Task<T> SendAsync<T>(
            HttpMethod method,
            string relativeUrl,
            object body,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_appConfig.HooshPayApiKey))
                throw new InvalidOperationException("HooshPay API key is not configured.");

            var requestJson = body == null ? null : JsonConvert.SerializeObject(body, RequestJsonSettings);
            var requestUri = new Uri(_httpClient.BaseAddress, relativeUrl);

            Console.WriteLine($"[HooshPay] -> {method} {requestUri}");
            if (!string.IsNullOrWhiteSpace(requestJson))
                Console.WriteLine($"[HooshPay] request body: {requestJson}");

            using var request = new HttpRequestMessage(method, relativeUrl);
            request.Headers.TryAddWithoutValidation("X-API-KEY", _appConfig.HooshPayApiKey);
            if (!string.IsNullOrWhiteSpace(requestJson))
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            Console.WriteLine($"[HooshPay] <- {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.WriteLine($"[HooshPay] response body: {responseBody}");

            if (!response.IsSuccessStatusCode)
                throw new HooshPayApiException(
                    method.ToString(),
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    responseBody,
                    requestJson);

            var result = JsonConvert.DeserializeObject<T>(responseBody);
            if (result == null)
                throw new HooshPayApiException(
                    method.ToString(),
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    "HooshPay returned an empty response.",
                    requestJson);

            return result;
        }

        private static Uri BuildBaseApiUri(string baseUrl)
        {
            var root = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://pay.hooshnet.com"
                : baseUrl.TrimEnd('/');

            return new Uri(root + "/api/v1/");
        }
    }

    public class HooshPaySettlementService
    {
        private readonly UserDbContext _userDbContext;
        private readonly CredentialsDbContext _credentialsDbContext;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<HooshPaySettlementService> _logger;

        public HooshPaySettlementService(
            UserDbContext userDbContext,
            CredentialsDbContext credentialsDbContext,
            ITelegramBotClient botClient,
            ILogger<HooshPaySettlementService> logger)
        {
            _userDbContext = userDbContext;
            _credentialsDbContext = credentialsDbContext;
            _botClient = botClient;
            _logger = logger;
        }

        public async Task<NowPaymentsSettlementResult> ApplyFinishedPaymentAsync(
            HooshPayPaymentInfo payment,
            string source,
            long? notifyChatId = null,
            CancellationToken cancellationToken = default)
        {
            if (payment == null)
                return NowPaymentsSettlementResult.NotFound();

            var credUser = await _credentialsDbContext.GetUserStatusWithId(payment.TelegramUserId);
            if (credUser == null)
                return NowPaymentsSettlementResult.UserNotFound();

            if (payment.IsAddedToBalance)
                return NowPaymentsSettlementResult.AlreadyAdded(credUser.AccountBalance);

            var beforeBalance = credUser.AccountBalance;
            var added = await _credentialsDbContext.AddFund(payment.TelegramUserId, payment.AmountToman);
            if (!added)
                return NowPaymentsSettlementResult.UserNotFound();

            payment.IsAddedToBalance = true;
            payment.BalanceBefore = beforeBalance;
            payment.BalanceAfter = await _credentialsDbContext.GetAccountBalance(payment.TelegramUserId);
            payment.SettledAtUtc = DateTime.UtcNow;
            await _userDbContext.SaveChangesAsync(cancellationToken);

            var afterBalance = payment.BalanceAfter ?? await _credentialsDbContext.GetAccountBalance(payment.TelegramUserId);
            await NotifyUserAsync(credUser, payment, notifyChatId, cancellationToken);
            LogPayment(payment, credUser, beforeBalance, afterBalance, source);

            return NowPaymentsSettlementResult.Applied(beforeBalance, afterBalance);
        }

        private async Task NotifyUserAsync(
            CredUser credUser,
            HooshPayPaymentInfo payment,
            long? notifyChatId,
            CancellationToken cancellationToken)
        {
            var chatId = notifyChatId.GetValueOrDefault(credUser.ChatID);
            if (chatId == 0)
                return;

            var text = $"اعتبار کیف پول شما به میزان {payment.AmountToman.FormatCurrency()} افزایش یافت.\n" +
                       "اکنون می‌توانید از این اعتبار برای خرید یا تمدید اکانت استفاده کنید.";

            try
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: text,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HooshPay user notification failed: {ex.Message}");
            }
        }

        private void LogPayment(
            HooshPayPaymentInfo payment,
            CredUser credUser,
            long beforeBalance,
            long afterBalance,
            string source)
        {
            var logMessage = "✅ پرداخت ریالی HooshPay تایید شد\n\n" +
                             $"👤 کاربر: <code>{Html(payment.TelegramUserId.ToString())}</code>\n" +
                             $"{Html(credUser?.ToString())}\n\n" +
                             $"💰 مبلغ شارژ: <code>{Html(payment.AmountToman.FormatCurrency())}</code>\n" +
                             $"💳 مبلغ پرداختی کاربر: <code>{Html(payment.PayableAmountToman.FormatCurrency())}</code>\n" +
                             $"🧾 کارمزد: <code>{Html(payment.FeeAmountToman.FormatCurrency())}</code>\n" +
                             $"📌 وضعیت: <code>{Html(payment.PaymentStatus)}</code>\n" +
                             $"🧾 Order ID: <code>{Html(payment.OrderId)}</code>\n" +
                             $"🧾 Invoice UID: <code>{Html(payment.InvoiceUid)}</code>\n" +
                             $"🔎 Tracking code: <code>{Html(payment.TrackingCode)}</code>\n\n" +
                             $"💳 موجودی قبل: <code>{Html(beforeBalance.FormatCurrency())}</code>\n" +
                             $"💳 موجودی بعد: <code>{Html(afterBalance.FormatCurrency())}</code>\n" +
                             $"📡 منبع تایید: <code>{Html(source)}</code>\n" +
                             $"🕒 زمان ثبت: <code>{Html(DateTime.UtcNow.AddMinutes(210).ConvertToHijriShamsi())}</code>";

            _logger.LogPayment(logMessage);
        }

        private static string Html(string value)
        {
            return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }

    public static class HooshPayFeeModes
    {
        public const string Seller = "seller";
        public const string Buyer = "buyer";
        public const string Split = "split";
    }

    public static class HooshPayStatuses
    {
        public const string Pending = "pending";
        public const string Paid = "paid";
        public const string Expired = "expired";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";

        public static bool IsPaid(string status)
        {
            return string.Equals(status, Paid, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFinalFailure(string status)
        {
            return string.Equals(status, Expired, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, Cancelled, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class HooshPaySignature
    {
        public static bool Verify(string requestBody, string receivedSignature, string secret)
        {
            if (string.IsNullOrWhiteSpace(requestBody) ||
                string.IsNullOrWhiteSpace(receivedSignature) ||
                string.IsNullOrWhiteSpace(secret))
            {
                return false;
            }

            var sortedJson = JsonConvert.SerializeObject(SortToken(JToken.Parse(requestBody)), Formatting.None);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret.Trim()));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(sortedJson));
            var computed = Convert.ToHexString(hash).ToLowerInvariant();
            var received = receivedSignature.Trim().ToLowerInvariant();

            var computedBytes = Encoding.UTF8.GetBytes(computed);
            var receivedBytes = Encoding.UTF8.GetBytes(received);

            return computedBytes.Length == receivedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(computedBytes, receivedBytes);
        }

        private static JToken SortToken(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted.Add(property.Name, SortToken(property.Value));

                return sorted;
            }

            if (token is JArray array)
                return new JArray(array.Select(SortToken));

            return token.DeepClone();
        }
    }

    public class HooshPayCreateInvoiceRequest
    {
        public long amount { get; set; }
        public string fee_mode { get; set; }
        public string description { get; set; }
        public string order_id { get; set; }
        public string callback_url { get; set; }
        public string return_url { get; set; }
    }

    public class HooshPayCreateInvoiceResponse
    {
        public bool success { get; set; }
        public HooshPayInvoiceData data { get; set; }
    }

    public class HooshPayInvoiceResponse
    {
        public bool success { get; set; }
        public HooshPayInvoiceData data { get; set; }
    }

    public class HooshPayVerifyResponse
    {
        public bool success { get; set; }
        public bool paid { get; set; }
        public string status { get; set; }
        public HooshPayInvoiceData data { get; set; }
    }

    public class HooshPayInvoiceData
    {
        public string uid { get; set; }
        public long amount { get; set; }
        public string fee_mode { get; set; }
        public decimal fee_percent { get; set; }
        public long fee_amount { get; set; }
        public long payable_amount { get; set; }
        public long merchant_credit { get; set; }
        public string status { get; set; }
        public string payment_url { get; set; }
        public DateTime? expires_at { get; set; }
        public string tracking_code { get; set; }
        public DateTime? paid_at { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Data { get; set; }
    }

    public class HooshPayIpn
    {
        public string @event { get; set; }
        public string invoice { get; set; }
        public string order_id { get; set; }
        public string status { get; set; }
        public long amount { get; set; }
        public long payable_amount { get; set; }
        public long merchant_credit { get; set; }
        public long fee_amount { get; set; }
        public string fee_mode { get; set; }
        public string tracking_code { get; set; }
        public DateTime? paid_at { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Data { get; set; }
    }

    public class HooshPayApiException : Exception
    {
        public string RequestMethod { get; }
        public string RequestUri { get; }
        public int StatusCode { get; }
        public string ResponseBody { get; }
        public string RequestBody { get; }

        public HooshPayApiException(string requestMethod, string requestUri, int statusCode, string responseBody, string requestBody = null)
            : base($"HooshPay API request failed with status {statusCode} for {requestMethod} {requestUri}: {responseBody}")
        {
            RequestMethod = requestMethod;
            RequestUri = requestUri;
            StatusCode = statusCode;
            ResponseBody = responseBody;
            RequestBody = requestBody;
        }

        public override string ToString()
        {
            return $"{base.ToString()}\nRequestMethod: {RequestMethod}\nRequestUri: {RequestUri}\nStatusCode: {StatusCode}\nRequestBody: {RequestBody}\nResponseBody: {ResponseBody}";
        }
    }
}
