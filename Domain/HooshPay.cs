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
    /// <summary>
    /// Local <c>users.db</c> record for a HooshPay invoice.
    /// </summary>
    /// <remarks>
    /// The same table is used for wallet top-ups and tenant storefront direct purchases.
    /// <see cref="PaymentPurpose"/> decides which settlement service may process the paid invoice.
    /// </remarks>
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
        public string BotId { get; set; } = BotContextAccessor.DefaultBotId;
        public string BotUsername { get; set; } = BotContextAccessor.DefaultBotId;
        // Distinguishes wallet top-ups from tenant storefront orders during IPN settlement.
        public string PaymentPurpose { get; set; } = TenantBotPaymentPurposes.WalletCharge;
        public int? TenantBotOrderId { get; set; }
        public long? TenantOwnerTelegramUserId { get; set; }

        /// <summary>
        /// Creates a new local HooshPay payment row for charging a user's shared wallet balance.
        /// </summary>
        /// <param name="telegramUserId">Telegram user id whose wallet will be charged after payment confirmation.</param>
        /// <param name="amountToman">Wallet charge amount in toman, excluding the HooshPay buyer fee.</param>
        /// <param name="callbackUrl">IPN callback URL sent to HooshPay.</param>
        /// <param name="returnUrl">Telegram return URL used after the user leaves the gateway.</param>
        /// <param name="chatId">Optional chat id used for payment status messages.</param>
        /// <returns>Initialized pending payment row. The caller must add it to <see cref="UserDbContext.HooshPayPaymentInfos"/>.</returns>
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
                BotId = BotContextAccessor.CurrentBotId,
                BotUsername = BotContextAccessor.CurrentBotUsername,
                PaymentPurpose = TenantBotPaymentPurposes.WalletCharge,
                PaymentStatus = HooshPayStatuses.Pending,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Builds the public order id used for HooshPay wallet charge invoices created by the Telegram bot.
        /// </summary>
        /// <param name="telegramUserId">Telegram user id included in the order id for diagnostics.</param>
        /// <returns>Unique order id with <c>TelBotHoosh</c> prefix, UTC timestamp, user id, and random suffix.</returns>
        public static string CreateOrderId(long telegramUserId)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"TelBotHoosh-{DateTime.UtcNow:yyyyMMddHHmmss}-{telegramUserId}-{suffix}";
        }

        /// <summary>
        /// Applies invoice data returned by create, get, or verify API calls without overwriting known values with blanks.
        /// </summary>
        /// <param name="data">Invoice data returned from HooshPay.</param>
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

        /// <summary>
        /// Applies HooshPay webhook data to the local payment row.
        /// </summary>
        /// <param name="ipn">Verified HooshPay IPN payload.</param>
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

    /// <summary>
    /// Minimal HooshPay API client used by wallet charges and tenant storefront purchases.
    /// </summary>
    /// <remarks>
    /// The client sends <c>X-API-KEY</c>, always requests <c>fee_mode = buyer</c>, and stores raw request/response
    /// data in the caller's payment row for auditability.
    /// </remarks>
    public class HooshPay
    {
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture
        };

        private readonly AppConfig _appConfig;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Creates a HooshPay client with a default <see cref="HttpClient"/>.
        /// </summary>
        /// <param name="configuration">Application configuration containing HooshPay API settings.</param>
        public HooshPay(IConfiguration configuration)
            : this(configuration, new HttpClient())
        {
        }

        /// <summary>
        /// Creates a HooshPay client with an injected HTTP client for runtime use or tests.
        /// </summary>
        /// <param name="configuration">Application configuration containing HooshPay API settings.</param>
        /// <param name="httpClient">HTTP client used for HooshPay API calls.</param>
        public HooshPay(IConfiguration configuration, HttpClient httpClient)
        {
            _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
            _httpClient = httpClient;
            _httpClient.BaseAddress ??= BuildBaseApiUri(_appConfig.HooshPayBaseUrl);
        }

        /// <summary>
        /// Creates a HooshPay invoice.
        /// </summary>
        /// <param name="amountToman">Merchant amount in toman before buyer fee.</param>
        /// <param name="orderId">Local order id used to match IPN callbacks.</param>
        /// <param name="description">Invoice description visible in HooshPay.</param>
        /// <param name="callbackUrl">Optional IPN URL; configuration fallback is used when empty.</param>
        /// <param name="returnUrl">Optional Telegram return URL; configuration fallback is used when empty.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP call.</param>
        /// <returns>HooshPay invoice creation response containing uid, payment URL, fee, and status.</returns>
        public async Task<HooshPayCreateInvoiceResponse> CreateInvoiceAsync(
            long amountToman,
            string orderId,
            string description,
            string callbackUrl = null,
            string returnUrl = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HooshPayCreateInvoiceRequest
            {
                amount = amountToman,
                fee_mode = HooshPayFeeModes.Buyer,
                order_id = orderId,
                description = description,
                callback_url = string.IsNullOrWhiteSpace(callbackUrl) ? _appConfig.HooshPayIpnUrl : callbackUrl,
                return_url = string.IsNullOrWhiteSpace(returnUrl) ? _appConfig.HooshPayReturnUrl : returnUrl
            };

            return await SendAsync<HooshPayCreateInvoiceResponse>(
                HttpMethod.Post,
                "invoices",
                request,
                cancellationToken);
        }

        /// <summary>
        /// Reads the latest invoice state from HooshPay.
        /// </summary>
        /// <param name="invoiceUid">HooshPay invoice uid.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP call.</param>
        /// <returns>Invoice response with the current gateway data.</returns>
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

        /// <summary>
        /// Verifies an invoice and asks HooshPay whether the payment is paid.
        /// </summary>
        /// <param name="invoiceUid">HooshPay invoice uid.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP call.</param>
        /// <returns>Verification response including the boolean paid flag and invoice data.</returns>
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

        /// <summary>
        /// Sends a JSON request to the HooshPay API and deserializes the response.
        /// </summary>
        /// <typeparam name="T">Expected response DTO type.</typeparam>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativeUrl">API path relative to <c>/api/v1/</c>.</param>
        /// <param name="body">Optional JSON request body.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP call.</param>
        /// <returns>Deserialized HooshPay response.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the API key is missing.</exception>
        /// <exception cref="HooshPayApiException">Thrown when HooshPay returns an error or empty response.</exception>
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

        /// <summary>
        /// Normalizes the configured HooshPay base URL into the API root URI.
        /// </summary>
        /// <param name="baseUrl">Configured root URL; empty values use the official HooshPay host.</param>
        /// <returns>Absolute URI ending in <c>/api/v1/</c>.</returns>
        private static Uri BuildBaseApiUri(string baseUrl)
        {
            var root = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://pay.hooshnet.com"
                : baseUrl.TrimEnd('/');

            return new Uri(root + "/api/v1/");
        }
    }

    /// <summary>
    /// Settles paid HooshPay wallet-charge invoices.
    /// </summary>
    /// <remarks>
    /// Tenant storefront orders are intentionally excluded by <see cref="HooshPayPaymentInfo.PaymentPurpose"/>
    /// and are fulfilled by <see cref="TenantBotService.ApplyPaidTenantOrderAsync"/>.
    /// </remarks>
    public class HooshPaySettlementService
    {
        private readonly UserDbContext _userDbContext;
        private readonly CredentialsDbContext _credentialsDbContext;
        private readonly BotClientProvider _botClientProvider;
        private readonly BotRegistry _botRegistry;
        private readonly BotContextAccessor _botContextAccessor;
        private readonly WalletLedgerService _walletLedgerService;
        private readonly ILogger<HooshPaySettlementService> _logger;

        /// <summary>
        /// Creates the wallet-charge settlement service.
        /// </summary>
        /// <param name="userDbContext">Runtime database containing HooshPay rows.</param>
        /// <param name="credentialsDbContext">Shared wallet/profile database.</param>
        /// <param name="botClientProvider">Factory/cache for sending messages through the correct bot.</param>
        /// <param name="botRegistry">Runtime bot registry used to resolve payment bot metadata.</param>
        /// <param name="botContextAccessor">Async bot context accessor used while notifying and logging.</param>
        /// <param name="logger">Application logger.</param>
        public HooshPaySettlementService(
            UserDbContext userDbContext,
            CredentialsDbContext credentialsDbContext,
            BotClientProvider botClientProvider,
            BotRegistry botRegistry,
            BotContextAccessor botContextAccessor,
            WalletLedgerService walletLedgerService,
            ILogger<HooshPaySettlementService> logger)
        {
            _userDbContext = userDbContext;
            _credentialsDbContext = credentialsDbContext;
            _botClientProvider = botClientProvider;
            _botRegistry = botRegistry;
            _botContextAccessor = botContextAccessor;
            _walletLedgerService = walletLedgerService;
            _logger = logger;
        }

        /// <summary>
        /// Adds a paid HooshPay wallet invoice to the user's wallet exactly once.
        /// </summary>
        /// <param name="payment">Local HooshPay payment row that belongs to a wallet charge.</param>
        /// <param name="source">Settlement source, for example IPN or manual check.</param>
        /// <param name="notifyChatId">Optional chat id override for the user notification.</param>
        /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
        /// <returns>Settlement result describing applied, duplicate, or missing-user state.</returns>
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
            await _walletLedgerService.RecordAsync(
                payment.TelegramUserId,
                WalletLedgerDirections.Credit,
                payment.AmountToman,
                beforeBalance,
                afterBalance,
                WalletLedgerReasons.WalletCharge,
                provider: "hooshpay",
                referenceType: nameof(HooshPayPaymentInfo),
                referenceId: payment.Id.ToString(CultureInfo.InvariantCulture),
                orderId: payment.OrderId,
                description: "HooshPay wallet charge",
                botId: payment.BotId,
                botUsername: payment.BotUsername,
                botType: BotInstanceTypes.Owned,
                cancellationToken: cancellationToken);
            using (_botContextAccessor.Push(CreatePaymentBotContext(payment)))
            {
                await NotifyUserAsync(credUser, payment, notifyChatId, cancellationToken);
                LogPayment(payment, credUser, beforeBalance, afterBalance, source);
            }

            return NowPaymentsSettlementResult.Applied(beforeBalance, afterBalance);
        }

        /// <summary>
        /// Sends the wallet charge confirmation through the same bot that created the payment.
        /// </summary>
        /// <param name="credUser">Wallet user who received credit.</param>
        /// <param name="payment">Settled payment row.</param>
        /// <param name="notifyChatId">Optional chat id override.</param>
        /// <param name="cancellationToken">Cancellation token for Telegram delivery.</param>
        private async Task NotifyUserAsync(
            CredUser credUser,
            HooshPayPaymentInfo payment,
            long? notifyChatId,
            CancellationToken cancellationToken)
        {
            var chatId = notifyChatId.GetValueOrDefault(credUser.ChatID);
            if (chatId == 0)
                return;

            var botClient = _botClientProvider.GetClient(payment.BotId);

            var text = $"اعتبار کیف پول شما به میزان {payment.AmountToman.FormatCurrency()} افزایش یافت.\n" +
                       "اکنون می‌توانید از این اعتبار برای خرید یا تمدید اکانت استفاده کنید.";

            try
            {
                await botClient.SendTextMessageAsync(
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

        /// <summary>
        /// Writes the HooshPay wallet-charge settlement log to the configured logger channel.
        /// </summary>
        /// <param name="payment">Settled payment row.</param>
        /// <param name="credUser">Wallet user shown in the log.</param>
        /// <param name="beforeBalance">Wallet balance before settlement.</param>
        /// <param name="afterBalance">Wallet balance after settlement.</param>
        /// <param name="source">Settlement source shown in the log.</param>
        private void LogPayment(
            HooshPayPaymentInfo payment,
            CredUser credUser,
            long beforeBalance,
            long afterBalance,
            string source)
        {
            var userSummary = TelegramUserLinkFormatter.HtmlSummary(credUser);
            if (string.IsNullOrWhiteSpace(userSummary))
                userSummary = $"👤 کاربر: <code>{Html(payment.TelegramUserId.ToString())}</code>";

            var logMessage = "✅ پرداخت ریالی HooshPay تایید شد\n\n" +
                             $"{userSummary}\n\n" +
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

        /// <summary>
        /// Encodes text before inserting it into Telegram HTML messages.
        /// </summary>
        /// <param name="value">Raw text that may contain HTML-sensitive characters.</param>
        /// <returns>HTML-encoded text; null becomes an empty string.</returns>
        private static string Html(string value)
        {
            return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        }

        /// <summary>
        /// Builds a bot runtime context from the bot metadata stored on the payment row.
        /// </summary>
        /// <param name="payment">Payment row that contains <c>BotId</c>.</param>
        /// <returns>Runtime context used while sending settlement notifications and logs.</returns>
        private BotRuntimeContext CreatePaymentBotContext(HooshPayPaymentInfo payment)
        {
            var bot = _botRegistry.GetById(payment.BotId);
            return new BotRuntimeContext
            {
                Config = bot,
                Client = _botClientProvider.GetClient(bot?.Id)
            };
        }
    }

    /// <summary>
    /// HooshPay fee policy values accepted by the invoice API.
    /// </summary>
    public static class HooshPayFeeModes
    {
        public const string Seller = "seller";
        public const string Buyer = "buyer";
        public const string Split = "split";
    }

    /// <summary>
    /// HooshPay status values used by invoice responses and IPN payloads.
    /// </summary>
    public static class HooshPayStatuses
    {
        public const string Pending = "pending";
        public const string Paid = "paid";
        public const string Expired = "expired";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";

        /// <summary>
        /// Checks whether a HooshPay status represents a successfully paid invoice.
        /// </summary>
        /// <param name="status">Gateway status string.</param>
        /// <returns><c>true</c> only for <c>paid</c>.</returns>
        public static bool IsPaid(string status)
        {
            return string.Equals(status, Paid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks whether a HooshPay status is a terminal non-paid state.
        /// </summary>
        /// <param name="status">Gateway status string.</param>
        /// <returns><c>true</c> for expired, cancelled, or failed invoices.</returns>
        public static bool IsFinalFailure(string status)
        {
            return string.Equals(status, Expired, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, Cancelled, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Verifies HooshPay webhook signatures by sorting JSON keys and applying HMAC-SHA256.
    /// </summary>
    public static class HooshPaySignature
    {
        /// <summary>
        /// Validates the <c>X-HooshPay-Signature</c> header for an IPN body.
        /// </summary>
        /// <param name="requestBody">Raw JSON body received by the controller.</param>
        /// <param name="receivedSignature">Signature header sent by HooshPay.</param>
        /// <param name="secret">Configured HooshPay IPN secret key.</param>
        /// <returns><c>true</c> when the computed signature matches using fixed-time comparison.</returns>
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

        /// <summary>
        /// Recursively sorts JSON object keys so signature calculation is stable.
        /// </summary>
        /// <param name="token">JSON token to sort.</param>
        /// <returns>Sorted clone of the supplied token.</returns>
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

    /// <summary>
    /// Request DTO for <c>POST /api/v1/invoices</c>.
    /// </summary>
    public class HooshPayCreateInvoiceRequest
    {
        public long amount { get; set; }
        public string fee_mode { get; set; }
        public string description { get; set; }
        public string order_id { get; set; }
        public string callback_url { get; set; }
        public string return_url { get; set; }
    }

    /// <summary>
    /// Response DTO returned after creating a HooshPay invoice.
    /// </summary>
    public class HooshPayCreateInvoiceResponse
    {
        public bool success { get; set; }
        public HooshPayInvoiceData data { get; set; }
    }

    /// <summary>
    /// Response DTO returned when reading an existing HooshPay invoice.
    /// </summary>
    public class HooshPayInvoiceResponse
    {
        public bool success { get; set; }
        public HooshPayInvoiceData data { get; set; }
    }

    /// <summary>
    /// Response DTO returned by HooshPay invoice verification.
    /// </summary>
    public class HooshPayVerifyResponse
    {
        public bool success { get; set; }
        public bool paid { get; set; }
        public string status { get; set; }
        public HooshPayInvoiceData data { get; set; }
    }

    /// <summary>
    /// Shared invoice data payload returned by HooshPay create, get, verify, and IPN operations.
    /// </summary>
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

    /// <summary>
    /// Webhook payload sent by HooshPay to the <c>/hooshpay-ipn</c> endpoint.
    /// </summary>
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

    /// <summary>
    /// Exception thrown when HooshPay returns an unsuccessful or unusable API response.
    /// </summary>
    public class HooshPayApiException : Exception
    {
        public string RequestMethod { get; }
        public string RequestUri { get; }
        public int StatusCode { get; }
        public string ResponseBody { get; }
        public string RequestBody { get; }

        /// <summary>
        /// Creates an exception that preserves the request and response details needed for diagnostics.
        /// </summary>
        /// <param name="requestMethod">HTTP method used for the failed request.</param>
        /// <param name="requestUri">Absolute request URL.</param>
        /// <param name="statusCode">HTTP status code returned by HooshPay.</param>
        /// <param name="responseBody">Raw response body returned by HooshPay.</param>
        /// <param name="requestBody">Optional raw request body sent to HooshPay.</param>
        public HooshPayApiException(string requestMethod, string requestUri, int statusCode, string responseBody, string requestBody = null)
            : base($"HooshPay API request failed with status {statusCode} for {requestMethod} {requestUri}: {responseBody}")
        {
            RequestMethod = requestMethod;
            RequestUri = requestUri;
            StatusCode = statusCode;
            ResponseBody = responseBody;
            RequestBody = requestBody;
        }

        /// <summary>
        /// Returns a diagnostic string that includes HTTP metadata and the raw HooshPay payloads.
        /// </summary>
        /// <returns>Detailed exception text.</returns>
        public override string ToString()
        {
            return $"{base.ToString()}\nRequestMethod: {RequestMethod}\nRequestUri: {RequestUri}\nStatusCode: {StatusCode}\nRequestBody: {RequestBody}\nResponseBody: {ResponseBody}";
        }
    }
}
