using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
    public class SwapinoPaymentInfo
    {
        [Key]
        public int Id { get; set; }
        public string OrderId { get; set; }
        public string ParentOrderId { get; set; }
        public string RootOrderId { get; set; }
        public int AttemptNo { get; set; } = 1;
        public string Result { get; set; }
        public string RawRequestJson { get; set; }
        public string RawResponseJson { get; set; }
        public string RawIpnJson { get; set; }
        public string IpnCallbackUrl { get; set; }
        public string SuccessUrl { get; set; }
        public string CancelUrl { get; set; }
        public long AmountToman { get; set; }
        public string BaseCurrency { get; set; } = "usdtbsc";
        public decimal BaseAmount { get; set; }
        public string PayCurrency { get; set; }
        public string InvoiceId { get; set; }
        public string InvoiceUrl { get; set; }
        public string PaymentId { get; set; }
        public string ParentPaymentId { get; set; }
        public string PaymentStatus { get; set; }
        public string PayAddress { get; set; }
        public string PayinHash { get; set; }
        public string PayoutHash { get; set; }
        public string OutcomeCurrency { get; set; }
        public decimal OutcomeAmount { get; set; }
        public decimal ActuallyPaid { get; set; }
        public decimal ActuallyPaidAtFiat { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public DateTime? SettledAtUtc { get; set; }
        public bool IsAddedToBalance { get; set; }
        public long? BalanceBefore { get; set; }
        public long? BalanceAfter { get; set; }
        public long ChatId { get; set; }
        public long? TelMsgId { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        [NotMapped]
        public string Payment_Id
        {
            get => OrderId;
            set => OrderId = value;
        }

        [NotMapped]
        public string CallbackUrl
        {
            get => IpnCallbackUrl;
            set => IpnCallbackUrl = value;
        }

        [NotMapped]
        public long RialAmount
        {
            get => AmountToman;
            set => AmountToman = value;
        }

        [NotMapped]
        public double TronAmount
        {
            get => (double)OutcomeAmount;
            set => OutcomeAmount = Convert.ToDecimal(value);
        }

        [NotMapped]
        public double UsdtAmount
        {
            get => (double)BaseAmount;
            set => BaseAmount = Convert.ToDecimal(value);
        }

        [NotMapped]
        public bool IsAddedToBallance
        {
            get => IsAddedToBalance;
            set => IsAddedToBalance = value;
        }

        public long TelegramUserId { get; set; }

        [NotMapped]
        public string PayCurrencyDisplay => PayCurrency;

        public static SwapinoPaymentInfo CreateCryptoCharge(
            long telegramUserId,
            long tomanAmount,
            string callbackUrl,
            long? chatId = null,
            string baseCurrency = null,
            string parentOrderId = null,
            int attemptNo = 1)
        {
            var orderId = CreateTelBotOrderId(telegramUserId, attemptNo);
            return new SwapinoPaymentInfo
            {
                OrderId = orderId,
                ParentOrderId = parentOrderId,
                RootOrderId = parentOrderId ?? orderId,
                AttemptNo = attemptNo < 1 ? 1 : attemptNo,
                IpnCallbackUrl = callbackUrl,
                AmountToman = tomanAmount,
                BaseCurrency = string.IsNullOrWhiteSpace(baseCurrency) ? "usdtbsc" : baseCurrency,
                TelegramUserId = telegramUserId,
                ChatId = chatId ?? 0,
                CreatedAtUtc = DateTime.UtcNow,
                Result = new NowPaymentsPaymentRecordData().ToJson()
            };
        }

        public static string CreateTelBotOrderId(long telegramUserId, int attemptNo = 1)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"TelBot-{DateTime.UtcNow:yyyyMMddHHmmss}-{telegramUserId}-{attemptNo}-{suffix}";
        }

        public NowPaymentsPaymentRecordData GetNowPaymentsData()
        {
            return NowPaymentsPaymentRecordData.FromJson(Result);
        }

        public void SetNowPaymentsData(NowPaymentsPaymentRecordData data)
        {
            Result = data.ToJson();
            InvoiceId = data.InvoiceId;
            InvoiceUrl = data.InvoiceUrl;
            PaymentId = data.PaymentId;
            ParentPaymentId = data.ParentPaymentId;
            PaymentStatus = data.PaymentStatus;
            PayAddress = data.PayAddress;
            PayCurrency = data.PayCurrency;
            BaseCurrency = data.PriceCurrency ?? BaseCurrency;
            BaseAmount = data.PriceAmount == 0 ? BaseAmount : data.PriceAmount;
            OutcomeCurrency = data.OutcomeCurrency;
            OutcomeAmount = data.OutcomeAmount == 0 ? OutcomeAmount : data.OutcomeAmount;
            ActuallyPaid = data.ActuallyPaid == 0 ? ActuallyPaid : data.ActuallyPaid;
            ActuallyPaidAtFiat = data.ActuallyPaidAtFiat == 0 ? ActuallyPaidAtFiat : data.ActuallyPaidAtFiat;
            PayinHash = data.PayinHash;
            PayoutHash = data.PayoutHash;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        public bool HasManualConfirmation()
        {
            if (string.IsNullOrWhiteSpace(RawResponseJson))
                return false;

            try
            {
                var token = JToken.Parse(RawResponseJson);
                return token["manual_confirmation"]?.Value<bool>() == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public class NowPayments
    {
        private static readonly Uri BaseApiUri = new Uri("https://api.nowpayments.io/v1/");
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture
        };

        private readonly AppConfig _appConfig;
        private readonly HttpClient _httpClient;
        private string _cachedJwtToken;

        public NowPayments(IConfiguration configuration)
            : this(configuration, new HttpClient { BaseAddress = BaseApiUri })
        {
        }

        public NowPayments(IConfiguration configuration, HttpClient httpClient)
        {
            _appConfig = configuration.Get<AppConfig>();
            _httpClient = httpClient;
            _httpClient.BaseAddress ??= BaseApiUri;
        }

        public static string CreateOrderId()
        {
            return SwapinoPaymentInfo.CreateTelBotOrderId(0);
        }

        public async Task<NowPaymentsStatusResult> GetApiStatusAsync(CancellationToken cancellationToken = default)
        {
            return await SendAsync<NowPaymentsStatusResult>(HttpMethod.Get, "status", null, cancellationToken);
        }

        public async Task<NowPaymentsCurrenciesResult> GetAvailableCurrenciesAsync(CancellationToken cancellationToken = default)
        {
            return await SendAsync<NowPaymentsCurrenciesResult>(HttpMethod.Get, "currencies", null, cancellationToken);
        }

        public async Task<NowPaymentsMinAmountResult> GetMinimumAmountAsync(
            string currencyFrom,
            string currencyTo,
            string fiatEquivalent = "usd",
            bool isFixedRate = true,
            CancellationToken cancellationToken = default)
        {
            var url = $"min-amount?currency_from={Uri.EscapeDataString(currencyFrom)}&currency_to={Uri.EscapeDataString(currencyTo)}&fiat_equivalent={Uri.EscapeDataString(fiatEquivalent)}&is_fixed_rate={(isFixedRate ? "true" : "false")}";
            return await SendAsync<NowPaymentsMinAmountResult>(HttpMethod.Get, url, null, cancellationToken);
        }

        public async Task<decimal> GetMinimumPriceAmountAsync(
            string currencyFrom,
            string currencyTo,
            string fiatEquivalent = "usd",
            bool isFixedRate = false,
            CancellationToken cancellationToken = default)
        {
            var result = await GetMinimumAmountAsync(currencyFrom, currencyTo, fiatEquivalent, isFixedRate, cancellationToken);
            var minimum = result.MinAmount;
            if (minimum <= 0 && result.Data != null)
            {
                if (result.Data.TryGetValue("min_amount", out var minAmountToken) && minAmountToken != null)
                    minimum = minAmountToken.Value<decimal>();
                else if (result.Data.TryGetValue("minAmount", out var minAmountCamelToken) && minAmountCamelToken != null)
                    minimum = minAmountCamelToken.Value<decimal>();
            }

            return minimum;
        }

        public async Task<NowPaymentsEstimateResult> GetEstimateAsync(
            decimal amount,
            string currencyFrom,
            string currencyTo,
            CancellationToken cancellationToken = default)
        {
            var amountText = amount.ToString("0.########", CultureInfo.InvariantCulture);
            var url = $"estimate?amount={Uri.EscapeDataString(amountText)}&currency_from={Uri.EscapeDataString(currencyFrom)}&currency_to={Uri.EscapeDataString(currencyTo)}";
            return await SendAsync<NowPaymentsEstimateResult>(HttpMethod.Get, url, null, cancellationToken);
        }

        public async Task<NowPaymentsInvoiceResponse> CreateInvoiceAsync(
            long tomanAmount,
            string orderId,
            string orderDescription,
            string payCurrency = null,
            string priceCurrency = null,
            CancellationToken cancellationToken = default)
        {
            priceCurrency ??= _appConfig.NowpaymentPriceCurrency;

            var conversion = await ConvertTomanToPriceCurrencyAsync(tomanAmount, priceCurrency);
            var priceAmount = conversion.PriceAmount;
            Console.WriteLine($"[NOWPayments] calculated invoice amount: toman={tomanAmount}, priceCurrency={priceCurrency}, amount={priceAmount}, payCurrency={(payCurrency ?? "all")}");

            var request = new NowPaymentsCreateInvoiceRequest
            {
                price_amount = priceAmount,
                price_currency = priceCurrency,
                pay_currency = payCurrency,
                ipn_callback_url = _appConfig.NowpaymentIpnUrl,
                order_id = orderId,
                order_description = orderDescription,
                success_url = _appConfig.NowpaymentSuccessUrl,
                cancel_url = _appConfig.NowpaymentCancelUrl,
                is_fixed_rate = false,
                is_fee_paid_by_user = false
            };

            var response = await SendAsync<NowPaymentsInvoiceResponse>(HttpMethod.Post, "invoice", request, cancellationToken);
            response.LocalTomanAmount = tomanAmount;
            response.LocalUsdtIrtPrice = conversion.UsdtIrtPrice;
            response.LocalPriceSource = conversion.PriceSource;
            response.LocalUsedFallbackPrice = conversion.UsedFallbackPrice;
            response.LocalPriceIsRial = conversion.PriceIsRial;
            return response;
        }

        public async Task<NowPaymentsPaymentResponse> CreatePaymentAsync(
            long tomanAmount,
            string orderId,
            string orderDescription,
            string payCurrency = null,
            string priceCurrency = null,
            CancellationToken cancellationToken = default)
        {
            priceCurrency ??= _appConfig.NowpaymentPriceCurrency;
            payCurrency ??= _appConfig.NowpaymentPayCurrency;

            var conversion = await ConvertTomanToPriceCurrencyAsync(tomanAmount, priceCurrency);
            var priceAmount = conversion.PriceAmount;
            Console.WriteLine($"[NOWPayments] calculated price amount: toman={tomanAmount}, priceCurrency={priceCurrency}, amount={priceAmount}, payCurrency={payCurrency}");

            var request = new NowPaymentsCreatePaymentRequest
            {
                price_amount = priceAmount,
                price_currency = priceCurrency,
                pay_currency = payCurrency,
                ipn_callback_url = _appConfig.NowpaymentIpnUrl,
                order_id = orderId,
                order_description = orderDescription,
                is_fixed_rate = true,
                is_fee_paid_by_user = false
            };

            var response = await SendAsync<NowPaymentsPaymentResponse>(HttpMethod.Post, "payment", request, cancellationToken);
            response.LocalTomanAmount = tomanAmount;
            response.LocalUsdtIrtPrice = conversion.UsdtIrtPrice;
            response.LocalPriceSource = conversion.PriceSource;
            response.LocalUsedFallbackPrice = conversion.UsedFallbackPrice;
            response.LocalPriceIsRial = conversion.PriceIsRial;
            return response;
        }

        public async Task<NowPaymentsPaymentStatusResult> GetPaymentStatusAsync(
            string paymentId,
            CancellationToken cancellationToken = default)
        {
            return await SendAsync<NowPaymentsPaymentStatusResult>(
                HttpMethod.Get,
                $"payment/{Uri.EscapeDataString(paymentId)}",
                null,
                cancellationToken);
        }

        public async Task<NowPaymentsPaymentStatusResult> FindPaymentStatusByInvoiceOrOrderAsync(
            string invoiceId,
            string orderId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(invoiceId) && string.IsNullOrWhiteSpace(orderId))
                return null;

            Console.WriteLine($"[NOWPayments] searching payment by invoice/order. invoiceId={invoiceId}, orderId={orderId}");

            var response = await SendAsync<JToken>(
                HttpMethod.Get,
                "payment/?limit=500&page=0&sortBy=created_at&orderBy=desc",
                null,
                cancellationToken,
                useBearerToken: true);

            var match = FindPaymentToken(response, invoiceId, orderId);
            if (match == null)
            {
                Console.WriteLine($"[NOWPayments] no payment found in recent payments for invoiceId={invoiceId}, orderId={orderId}");
                return null;
            }

            var paymentId = match["payment_id"]?.Value<string>();
            var matchedInvoiceId = match["invoice_id"]?.Value<string>();
            var matchedOrderId = match["order_id"]?.Value<string>();
            var matchedStatus = match["payment_status"]?.Value<string>();
            Console.WriteLine($"[NOWPayments] matched payment. paymentId={paymentId}, invoiceId={matchedInvoiceId}, orderId={matchedOrderId}, status={matchedStatus}");

            if (!string.IsNullOrWhiteSpace(paymentId))
                return await GetPaymentStatusAsync(paymentId, cancellationToken);

            return match.ToObject<NowPaymentsPaymentStatusResult>();
        }

        private async Task<NowPaymentsPriceConversionResult> ConvertTomanToPriceCurrencyAsync(long tomanAmount, string priceCurrency)
        {
            if (tomanAmount <= 0)
                throw new ArgumentException("Amount must be greater than zero.", nameof(tomanAmount));

            var quote = await new DollarPriceHelper().NobitexUSDTIRTQuote();
            long usdtIrtPrice = quote.Price;
            var usedFallback = false;
            var priceSource = quote.Source;
            if (usdtIrtPrice <= 0)
            {
                usdtIrtPrice = _appConfig.NowpaymentUsdIrtFallbackPrice > 0
                    ? _appConfig.NowpaymentUsdIrtFallbackPrice
                    : 1800000;
                usedFallback = true;
                priceSource = "config:fallback";
                Console.WriteLine($"[NOWPayments] fallback USD/IRT price from config: {usdtIrtPrice}");
            }

            var priceIsRial = usdtIrtPrice >= 200000;
            decimal stableAmount = priceIsRial
                ? (tomanAmount * 10m) / usdtIrtPrice
                : tomanAmount / (decimal)usdtIrtPrice;

            stableAmount = Math.Round(stableAmount, 6, MidpointRounding.AwayFromZero);
            if (stableAmount < 0.000001m)
                stableAmount = 0.000001m;

            Console.WriteLine($"[NOWPayments] converted toman to {priceCurrency}: {stableAmount}");
            return new NowPaymentsPriceConversionResult
            {
                PriceAmount = stableAmount,
                UsdtIrtPrice = usdtIrtPrice,
                PriceSource = priceSource,
                UsedFallbackPrice = usedFallback,
                PriceIsRial = priceIsRial
            };
        }

        private async Task<T> SendAsync<T>(
            HttpMethod method,
            string relativeUrl,
            object body,
            CancellationToken cancellationToken,
            bool useBearerToken = false)
        {
            if (string.IsNullOrWhiteSpace(_appConfig.NowPaymentApiKey))
                throw new InvalidOperationException("NOWPayments API key is not configured.");

            var requestJson = body == null ? null : JsonConvert.SerializeObject(body, RequestJsonSettings);
            var requestUri = new Uri(_httpClient.BaseAddress, relativeUrl);

            Console.WriteLine($"[NOWPayments] -> {method} {requestUri}");
            if (useBearerToken)
                Console.WriteLine("[NOWPayments] request requires Bearer JWT. tokenSource=configuration/auth");

            if (!string.IsNullOrWhiteSpace(requestJson))
                Console.WriteLine($"[NOWPayments] request body: {requestJson}");

            using var request = new HttpRequestMessage(method, relativeUrl);
            request.Headers.TryAddWithoutValidation("x-api-key", _appConfig.NowPaymentApiKey);
            if (useBearerToken)
            {
                var jwtToken = await GetJwtTokenAsync(cancellationToken);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwtToken}");
            }

            if (!string.IsNullOrWhiteSpace(requestJson))
            {
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            Console.WriteLine($"[NOWPayments] <- {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.WriteLine($"[NOWPayments] response body: {responseBody}");

            if (!response.IsSuccessStatusCode)
                throw new NowPaymentsApiException(
                    method.ToString(),
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    responseBody,
                    requestJson);

            var result = JsonConvert.DeserializeObject<T>(responseBody);
            if (result == null)
                throw new NowPaymentsApiException(
                    method.ToString(),
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    "NOWPayments returned an empty response.",
                    requestJson);

            return result;
        }

        private async Task<string> GetJwtTokenAsync(CancellationToken cancellationToken)
        {
            var configuredToken = NormalizeBearerToken(_appConfig.NowPaymentJwtToken);
            if (!string.IsNullOrWhiteSpace(configuredToken))
                return configuredToken;

            if (!string.IsNullOrWhiteSpace(_cachedJwtToken))
                return _cachedJwtToken;

            if (string.IsNullOrWhiteSpace(_appConfig.NowPaymentEmail) ||
                string.IsNullOrWhiteSpace(_appConfig.NowPaymentPassword))
            {
                throw new InvalidOperationException(
                    "NOWPayments manual invoice lookup requires Bearer JWT. Configure nowPaymentJwtToken, or configure nowPaymentEmail and nowPaymentPassword so the bot can request a JWT.");
            }

            var requestBody = new
            {
                email = _appConfig.NowPaymentEmail,
                password = _appConfig.NowPaymentPassword
            };

            var requestJson = JsonConvert.SerializeObject(requestBody, RequestJsonSettings);
            var requestUri = new Uri(_httpClient.BaseAddress, "auth");

            Console.WriteLine($"[NOWPayments] -> POST {requestUri}");
            Console.WriteLine("[NOWPayments] auth request body: {\"email\":\"configured\",\"password\":\"configured\"}");

            using var request = new HttpRequestMessage(HttpMethod.Post, "auth");
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            Console.WriteLine($"[NOWPayments] auth <- {(int)response.StatusCode} {response.ReasonPhrase}");

            if (!response.IsSuccessStatusCode)
                throw new NowPaymentsApiException(
                    "POST",
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    responseBody,
                    "{\"email\":\"configured\",\"password\":\"configured\"}");

            var token = ExtractJwtToken(responseBody);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("NOWPayments auth response did not contain a JWT token.");

            _cachedJwtToken = NormalizeBearerToken(token);
            Console.WriteLine("[NOWPayments] auth token received and cached.");
            return _cachedJwtToken;
        }

        private static string NormalizeBearerToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            token = token.Trim();
            const string bearerPrefix = "Bearer ";
            if (token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                token = token.Substring(bearerPrefix.Length).Trim();

            return token;
        }

        private static string ExtractJwtToken(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                var token = JToken.Parse(responseBody);
                return NormalizeBearerToken(FindStringProperty(token, "token", "jwt", "access_token", "accessToken"));
            }
            catch
            {
                return NormalizeBearerToken(responseBody);
            }
        }

        private static string FindStringProperty(JToken token, params string[] names)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                foreach (var property in obj.Properties())
                {
                    if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = property.Value.Type == JTokenType.String
                            ? property.Value.Value<string>()
                            : property.Value.ToString(Formatting.None);

                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }

                foreach (var property in obj.Properties())
                {
                    var value = FindStringProperty(property.Value, names);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Children())
                {
                    var value = FindStringProperty(item, names);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return null;
        }

        private static JToken FindPaymentToken(JToken token, string invoiceId, string orderId)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var tokenPaymentId = obj["payment_id"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(tokenPaymentId))
                {
                    var tokenInvoiceId = obj["invoice_id"]?.Value<string>();
                    var tokenOrderId = obj["order_id"]?.Value<string>();

                    var invoiceMatches = !string.IsNullOrWhiteSpace(invoiceId) &&
                                         string.Equals(tokenInvoiceId, invoiceId, StringComparison.OrdinalIgnoreCase);
                    var orderMatches = !string.IsNullOrWhiteSpace(orderId) &&
                                       string.Equals(tokenOrderId, orderId, StringComparison.OrdinalIgnoreCase);

                    if (invoiceMatches || orderMatches)
                        return obj;
                }

                foreach (var property in obj.Properties())
                {
                    var match = FindPaymentToken(property.Value, invoiceId, orderId);
                    if (match != null)
                        return match;
                }
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Children())
                {
                    var match = FindPaymentToken(item, invoiceId, orderId);
                    if (match != null)
                        return match;
                }
            }

            return null;
        }
    }

    public class NowPaymentsPriceConversionResult
    {
        public decimal PriceAmount { get; set; }
        public long UsdtIrtPrice { get; set; }
        public string PriceSource { get; set; }
        public bool UsedFallbackPrice { get; set; }
        public bool PriceIsRial { get; set; }
    }

    public class NowPaymentsSettlementService
    {
        private readonly UserDbContext _userDbContext;
        private readonly CredentialsDbContext _credentialsDbContext;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<NowPaymentsSettlementService> _logger;

        public NowPaymentsSettlementService(
            UserDbContext userDbContext,
            CredentialsDbContext credentialsDbContext,
            ITelegramBotClient botClient,
            ILogger<NowPaymentsSettlementService> logger)
        {
            _userDbContext = userDbContext;
            _credentialsDbContext = credentialsDbContext;
            _botClient = botClient;
            _logger = logger;
        }

        public async Task<NowPaymentsSettlementResult> ApplyFinishedPaymentAsync(
            SwapinoPaymentInfo payment,
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

        public async Task<NowPaymentsSettlementResult> ApplyPartialPaymentAsync(
            SwapinoPaymentInfo payment,
            long creditedAmountToman,
            string source,
            long? notifyChatId = null,
            CancellationToken cancellationToken = default)
        {
            if (payment == null)
                return NowPaymentsSettlementResult.NotFound();

            if (creditedAmountToman <= 0)
                return NowPaymentsSettlementResult.InvalidAmount();

            var credUser = await _credentialsDbContext.GetUserStatusWithId(payment.TelegramUserId);
            if (credUser == null)
                return NowPaymentsSettlementResult.UserNotFound();

            if (payment.IsAddedToBalance)
                return NowPaymentsSettlementResult.AlreadyAdded(credUser.AccountBalance);

            var originalAmountToman = payment.AmountToman;
            var beforeBalance = credUser.AccountBalance;
            var added = await _credentialsDbContext.AddFund(payment.TelegramUserId, creditedAmountToman);
            if (!added)
                return NowPaymentsSettlementResult.UserNotFound();

            payment.AmountToman = creditedAmountToman;
            payment.IsAddedToBalance = true;
            payment.BalanceBefore = beforeBalance;
            payment.BalanceAfter = await _credentialsDbContext.GetAccountBalance(payment.TelegramUserId);
            payment.SettledAtUtc = DateTime.UtcNow;
            payment.ErrorCode = "partial_settlement";
            payment.ErrorMessage = $"Partial crypto payment credited. OriginalAmountToman={originalAmountToman}, CreditedAmountToman={creditedAmountToman}";
            payment.RawResponseJson = JsonConvert.SerializeObject(new
            {
                partial_settlement = true,
                source,
                original_amount_toman = originalAmountToman,
                credited_amount_toman = creditedAmountToman,
                settled_at_utc = payment.SettledAtUtc,
                order_id = payment.OrderId,
                payment_id = payment.PaymentId,
                invoice_id = payment.InvoiceId,
                payment_status = payment.PaymentStatus
            }, Formatting.None);

            await _userDbContext.SaveChangesAsync(cancellationToken);

            var afterBalance = payment.BalanceAfter ?? await _credentialsDbContext.GetAccountBalance(payment.TelegramUserId);
            await NotifyUserAsync(credUser, payment, notifyChatId, cancellationToken);
            LogPayment(payment, credUser, beforeBalance, afterBalance, source);

            return NowPaymentsSettlementResult.Applied(beforeBalance, afterBalance);
        }

        public async Task<NowPaymentsSettlementResult> ApplyManualConfirmationAsync(
            SwapinoPaymentInfo payment,
            string source,
            long? notifyChatId = null,
            CancellationToken cancellationToken = default)
        {
            if (payment == null)
                return NowPaymentsSettlementResult.NotFound();

            if (payment.IsAddedToBalance)
                return await ApplyFinishedPaymentAsync(payment, source, notifyChatId, cancellationToken);

            var data = payment.GetNowPaymentsData();
            var now = DateTime.UtcNow;

            data.OrderId ??= payment.OrderId;
            data.InvoiceId ??= payment.InvoiceId;
            data.InvoiceUrl ??= payment.InvoiceUrl;
            data.PaymentId ??= payment.PaymentId;
            data.ParentPaymentId ??= payment.ParentPaymentId;
            data.PayAddress ??= payment.PayAddress;
            data.PayCurrency ??= payment.PayCurrency;
            data.PriceCurrency ??= payment.BaseCurrency;
            data.OutcomeCurrency ??= payment.OutcomeCurrency;
            data.PayinHash ??= payment.PayinHash;
            data.PayoutHash ??= payment.PayoutHash;
            data.PriceAmount = data.PriceAmount == 0 ? payment.BaseAmount : data.PriceAmount;
            data.PayAmount = data.PayAmount == 0 ? payment.OutcomeAmount : data.PayAmount;
            data.OutcomeAmount = data.OutcomeAmount == 0 ? payment.OutcomeAmount : data.OutcomeAmount;
            data.PaymentStatus = NowPaymentsStatuses.Finished;
            data.ActuallyPaid = data.ActuallyPaid == 0
                ? (payment.ActuallyPaid != 0 ? payment.ActuallyPaid : (data.PayAmount != 0 ? data.PayAmount : data.PriceAmount))
                : data.ActuallyPaid;
            data.ActuallyPaidAtFiat = data.ActuallyPaidAtFiat == 0
                ? (payment.ActuallyPaidAtFiat != 0 ? payment.ActuallyPaidAtFiat : (data.PriceAmount != 0 ? data.PriceAmount : data.ActuallyPaid))
                : data.ActuallyPaidAtFiat;
            data.CreatedAt ??= payment.CreatedAtUtc;
            data.UpdatedAt = now;

            payment.PaymentStatus = NowPaymentsStatuses.Finished;
            payment.PaidAtUtc ??= now;
            payment.ErrorCode = null;
            payment.ErrorMessage = null;
            payment.SetNowPaymentsData(data);

            payment.RawResponseJson = JsonConvert.SerializeObject(new
            {
                manual_confirmation = true,
                source,
                confirmed_at_utc = now,
                order_id = payment.OrderId,
                payment_id = payment.PaymentId,
                invoice_id = payment.InvoiceId
            }, Formatting.None);

            await _userDbContext.SaveChangesAsync(cancellationToken);
            return await ApplyFinishedPaymentAsync(payment, source, notifyChatId, cancellationToken);
        }

        private async Task NotifyUserAsync(
            CredUser credUser,
            SwapinoPaymentInfo payment,
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
                Console.WriteLine($"NOWPayments user notification failed: {ex.Message}");
            }
        }

        private void LogPayment(
            SwapinoPaymentInfo payment,
            CredUser credUser,
            long beforeBalance,
            long afterBalance,
            string source)
        {
            var data = payment.GetNowPaymentsData();
            var baseCurrency = payment.BaseCurrency ?? data.PriceCurrency;
            var baseAmount = payment.BaseAmount == 0 ? data.PriceAmount : payment.BaseAmount;
            var payCurrency = payment.PayCurrency ?? data.PayCurrency;
            var payAmount = data.PayAmount == 0 ? payment.OutcomeAmount : data.PayAmount;
            var actuallyPaid = payment.ActuallyPaid == 0 ? data.ActuallyPaid : payment.ActuallyPaid;
            var actuallyPaidAtFiat = payment.ActuallyPaidAtFiat == 0 ? data.ActuallyPaidAtFiat : payment.ActuallyPaidAtFiat;
            var outcomeAmount = payment.OutcomeAmount == 0 ? data.OutcomeAmount : payment.OutcomeAmount;
            var outcomeCurrency = payment.OutcomeCurrency ?? data.OutcomeCurrency;

            var userSummary = TelegramUserLinkFormatter.HtmlSummary(credUser);
            if (string.IsNullOrWhiteSpace(userSummary))
                userSummary = $"👤 کاربر: <code>{Html(payment.TelegramUserId.ToString())}</code>";
            var logMessage = "✅ پرداخت ارز دیجیتال تایید شد\n\n" +
                             $"{userSummary}\n\n" +
                             $"💰 مبلغ شارژ: <code>{Html(payment.AmountToman.FormatCurrency())}</code>\n" +
                             $"💵 ارز مبنا: <code>{Html(FormatDecimal(baseAmount))} {Html(baseCurrency)}</code>\n" +
                             $"🪙 ارز پرداختی: <code>{Html(FormatDecimal(payAmount))} {Html(payCurrency)}</code>\n" +
                             $"✅ مقدار پرداخت‌شده واقعی: <code>{Html(FormatDecimal(actuallyPaid))} {Html(payCurrency)}</code>\n" +
                             $"💲 معادل فیات پرداخت‌شده: <code>{Html(FormatDecimal(actuallyPaidAtFiat))}</code>\n" +
                             $"📤 خروجی NOWPayments: <code>{Html(FormatDecimal(outcomeAmount))} {Html(outcomeCurrency)}</code>\n\n" +
                             $"📌 وضعیت: <code>{Html(data.PaymentStatus ?? payment.PaymentStatus)}</code>\n" +
                             $"🧾 Order ID: <code>{Html(payment.OrderId)}</code>\n" +
                             $"🧾 Invoice ID: <code>{Html(payment.InvoiceId ?? data.InvoiceId)}</code>\n" +
                             $"🧾 Payment ID: <code>{Html(payment.PaymentId ?? data.PaymentId)}</code>\n" +
                             $"🔗 Invoice URL: <code>{Html(payment.InvoiceUrl ?? data.InvoiceUrl)}</code>\n" +
                             $"🔐 Pay address: <code>{Html(payment.PayAddress ?? data.PayAddress)}</code>\n" +
                             $"📥 Payin hash: <code>{Html(payment.PayinHash ?? data.PayinHash)}</code>\n" +
                             $"📤 Payout hash: <code>{Html(payment.PayoutHash ?? data.PayoutHash)}</code>\n\n" +
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

        private static string FormatDecimal(decimal value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }
    }

    public class NowPaymentsSettlementResult
    {
        public NowPaymentsSettlementStatus Status { get; set; }
        public long BeforeBalance { get; set; }
        public long AfterBalance { get; set; }

        public static NowPaymentsSettlementResult Applied(long beforeBalance, long afterBalance)
        {
            return new NowPaymentsSettlementResult
            {
                Status = NowPaymentsSettlementStatus.Applied,
                BeforeBalance = beforeBalance,
                AfterBalance = afterBalance
            };
        }

        public static NowPaymentsSettlementResult AlreadyAdded(long balance)
        {
            return new NowPaymentsSettlementResult
            {
                Status = NowPaymentsSettlementStatus.AlreadyAdded,
                BeforeBalance = balance,
                AfterBalance = balance
            };
        }

        public static NowPaymentsSettlementResult NotFound()
        {
            return new NowPaymentsSettlementResult { Status = NowPaymentsSettlementStatus.PaymentNotFound };
        }

        public static NowPaymentsSettlementResult UserNotFound()
        {
            return new NowPaymentsSettlementResult { Status = NowPaymentsSettlementStatus.UserNotFound };
        }

        public static NowPaymentsSettlementResult InvalidAmount()
        {
            return new NowPaymentsSettlementResult { Status = NowPaymentsSettlementStatus.InvalidAmount };
        }
    }

    public enum NowPaymentsSettlementStatus
    {
        Applied,
        AlreadyAdded,
        PaymentNotFound,
        UserNotFound,
        InvalidAmount
    }

    public static class NowPaymentsStatuses
    {
        public const string Waiting = "waiting";
        public const string Confirming = "confirming";
        public const string Confirmed = "confirmed";
        public const string Sending = "sending";
        public const string PartiallyPaid = "partially_paid";
        public const string Finished = "finished";
        public const string Failed = "failed";
        public const string Refunded = "refunded";
        public const string Expired = "expired";

        public static bool IsPaid(string status)
        {
            return string.Equals(status, Finished, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPartiallyPaid(string status)
        {
            return string.Equals(status, PartiallyPaid, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFinalFailure(string status)
        {
            return string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, Refunded, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, Expired, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class NowPaymentsIpnSignature
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
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret.Trim()));
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

    public class NowPaymentsPaymentRecordData
    {
        public string OrderId { get; set; }
        public string InvoiceId { get; set; }
        public string InvoiceUrl { get; set; }
        public string PaymentId { get; set; }
        public string ParentPaymentId { get; set; }
        public string PaymentStatus { get; set; }
        public string PayAddress { get; set; }
        public string PayCurrency { get; set; }
        public string PriceCurrency { get; set; }
        public string PurchaseId { get; set; }
        public string PayinHash { get; set; }
        public string PayoutHash { get; set; }
        public string OutcomeCurrency { get; set; }
        public decimal PriceAmount { get; set; }
        public decimal PayAmount { get; set; }
        public decimal AmountReceived { get; set; }
        public decimal ActuallyPaid { get; set; }
        public decimal ActuallyPaidAtFiat { get; set; }
        public decimal OutcomeAmount { get; set; }
        public long UsdtIrtPrice { get; set; }
        public string PriceSource { get; set; }
        public bool UsedFallbackPrice { get; set; }
        public bool PriceIsRial { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? ValidUntil { get; set; }
        public DateTime? ExpirationEstimateDate { get; set; }

        public static NowPaymentsPaymentRecordData FromPaymentResponse(NowPaymentsPaymentResponse response)
        {
            var data = new NowPaymentsPaymentRecordData();
            data.Apply(response);
            return data;
        }

        public static NowPaymentsPaymentRecordData FromInvoiceResponse(NowPaymentsInvoiceResponse response)
        {
            return new NowPaymentsPaymentRecordData
            {
                InvoiceId = response.id,
                InvoiceUrl = response.invoice_url,
                PriceAmount = response.price_amount,
                PriceCurrency = response.price_currency,
                OrderId = response.order_id,
                CreatedAt = response.created_at,
                UsdtIrtPrice = response.LocalUsdtIrtPrice,
                PriceSource = response.LocalPriceSource,
                UsedFallbackPrice = response.LocalUsedFallbackPrice,
                PriceIsRial = response.LocalPriceIsRial
            };
        }

        public static NowPaymentsPaymentRecordData FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new NowPaymentsPaymentRecordData();

            try
            {
                return JsonConvert.DeserializeObject<NowPaymentsPaymentRecordData>(json) ??
                       new NowPaymentsPaymentRecordData();
            }
            catch
            {
                return new NowPaymentsPaymentRecordData();
            }
        }

        public void Apply(NowPaymentsPaymentResponse response)
        {
            PaymentId = response.payment_id ?? PaymentId;
            InvoiceId = response.invoice_id ?? InvoiceId;
            PaymentStatus = response.payment_status ?? PaymentStatus;
            PayAddress = response.pay_address ?? PayAddress;
            PriceAmount = response.price_amount == 0 ? PriceAmount : response.price_amount;
            PriceCurrency = response.price_currency ?? PriceCurrency;
            PayAmount = response.pay_amount == 0 ? PayAmount : response.pay_amount;
            AmountReceived = response.amount_received == 0 ? AmountReceived : response.amount_received;
            PayCurrency = response.pay_currency ?? PayCurrency;
            OrderId = response.order_id ?? OrderId;
            PurchaseId = response.purchase_id ?? PurchaseId;
            OutcomeAmount = response.outcome_amount == 0 ? OutcomeAmount : response.outcome_amount;
            OutcomeCurrency = response.outcome_currency ?? OutcomeCurrency;
            ValidUntil = response.valid_until ?? ValidUntil;
            ExpirationEstimateDate = response.expiration_estimate_date ?? ExpirationEstimateDate;
            CreatedAt = response.created_at ?? CreatedAt;
            UpdatedAt = response.updated_at ?? UpdatedAt;
            UsdtIrtPrice = response.LocalUsdtIrtPrice == 0 ? UsdtIrtPrice : response.LocalUsdtIrtPrice;
            PriceSource = response.LocalPriceSource ?? PriceSource;
            UsedFallbackPrice = response.LocalUsedFallbackPrice;
            PriceIsRial = response.LocalPriceIsRial;
        }

        public void Apply(NowPaymentsPaymentStatusResult status)
        {
            PaymentId = status.payment_id ?? PaymentId;
            InvoiceId = status.invoice_id ?? InvoiceId;
            PaymentStatus = status.payment_status ?? PaymentStatus;
            PayAddress = status.pay_address ?? PayAddress;
            PriceAmount = status.price_amount == 0 ? PriceAmount : status.price_amount;
            PriceCurrency = status.price_currency ?? PriceCurrency;
            PayAmount = status.pay_amount == 0 ? PayAmount : status.pay_amount;
            AmountReceived = status.amount_received == 0 ? AmountReceived : status.amount_received;
            ActuallyPaid = status.actually_paid == 0 ? ActuallyPaid : status.actually_paid;
            ActuallyPaidAtFiat = status.actually_paid_at_fiat == 0 ? ActuallyPaidAtFiat : status.actually_paid_at_fiat;
            PayCurrency = status.pay_currency ?? PayCurrency;
            OrderId = status.order_id ?? OrderId;
            PurchaseId = status.purchase_id ?? PurchaseId;
            OutcomeAmount = status.outcome_amount == 0 ? OutcomeAmount : status.outcome_amount;
            OutcomeCurrency = status.outcome_currency ?? OutcomeCurrency;
            PayinHash = status.payin_hash ?? PayinHash;
            PayoutHash = status.payout_hash ?? PayoutHash;
            CreatedAt = status.created_at ?? CreatedAt;
            UpdatedAt = status.updated_at ?? UpdatedAt;
        }

        public void Apply(NowPaymentsIpn ipn)
        {
            PaymentId = ipn.payment_id ?? PaymentId;
            ParentPaymentId = ipn.parent_payment_id ?? ParentPaymentId;
            InvoiceId = ipn.invoice_id ?? InvoiceId;
            PaymentStatus = ipn.payment_status ?? PaymentStatus;
            PayAddress = ipn.pay_address ?? PayAddress;
            PriceAmount = ipn.price_amount == 0 ? PriceAmount : ipn.price_amount;
            PriceCurrency = ipn.price_currency ?? PriceCurrency;
            PayAmount = ipn.pay_amount == 0 ? PayAmount : ipn.pay_amount;
            ActuallyPaid = ipn.actually_paid == 0 ? ActuallyPaid : ipn.actually_paid;
            ActuallyPaidAtFiat = ipn.actually_paid_at_fiat == 0 ? ActuallyPaidAtFiat : ipn.actually_paid_at_fiat;
            PayCurrency = ipn.pay_currency ?? PayCurrency;
            OrderId = ipn.order_id ?? OrderId;
            PurchaseId = ipn.purchase_id ?? PurchaseId;
            OutcomeAmount = ipn.outcome_amount == 0 ? OutcomeAmount : ipn.outcome_amount;
            OutcomeCurrency = ipn.outcome_currency ?? OutcomeCurrency;
            PayinHash = ipn.payin_hash ?? PayinHash;
            PayoutHash = ipn.payout_hash ?? PayoutHash;
            CreatedAt = ipn.created_at ?? CreatedAt;
            UpdatedAt = ipn.updated_at ?? UpdatedAt;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }
    }

    public class NowPaymentsCreateInvoiceRequest
    {
        public decimal price_amount { get; set; }
        public string price_currency { get; set; }
        public string pay_currency { get; set; }
        public string ipn_callback_url { get; set; }
        public string order_id { get; set; }
        public string order_description { get; set; }
        public string success_url { get; set; }
        public string cancel_url { get; set; }
        public bool is_fixed_rate { get; set; }
        public bool is_fee_paid_by_user { get; set; }
    }

    public class NowPaymentsInvoiceResponse
    {
        public string id { get; set; }
        public string invoice_url { get; set; }
        public decimal price_amount { get; set; }
        public string price_currency { get; set; }
        public string order_id { get; set; }
        public string order_description { get; set; }
        public DateTime? created_at { get; set; }
        public long LocalTomanAmount { get; set; }
        public long LocalUsdtIrtPrice { get; set; }
        public string LocalPriceSource { get; set; }
        public bool LocalUsedFallbackPrice { get; set; }
        public bool LocalPriceIsRial { get; set; }
    }

    public class NowPaymentsCreatePaymentRequest
    {
        public decimal price_amount { get; set; }
        public string price_currency { get; set; }
        public string pay_currency { get; set; }
        public string ipn_callback_url { get; set; }
        public string order_id { get; set; }
        public string order_description { get; set; }
        public bool is_fixed_rate { get; set; }
        public bool is_fee_paid_by_user { get; set; }
    }

    public class NowPaymentsPaymentResponse
    {
        public string payment_id { get; set; }
        public string invoice_id { get; set; }
        public string payment_status { get; set; }
        public string pay_address { get; set; }
        public decimal price_amount { get; set; }
        public string price_currency { get; set; }
        public decimal pay_amount { get; set; }
        public decimal amount_received { get; set; }
        public string pay_currency { get; set; }
        public string order_id { get; set; }
        public string order_description { get; set; }
        public string purchase_id { get; set; }
        public decimal outcome_amount { get; set; }
        public string outcome_currency { get; set; }
        public DateTime? created_at { get; set; }
        public DateTime? updated_at { get; set; }
        public DateTime? valid_until { get; set; }
        public DateTime? expiration_estimate_date { get; set; }
        public long LocalTomanAmount { get; set; }
        public long LocalUsdtIrtPrice { get; set; }
        public string LocalPriceSource { get; set; }
        public bool LocalUsedFallbackPrice { get; set; }
        public bool LocalPriceIsRial { get; set; }
    }

    public class NowPaymentsPaymentStatusResult : NowPaymentsPaymentResponse
    {
        public decimal actually_paid { get; set; }
        public decimal actually_paid_at_fiat { get; set; }
        public string payin_hash { get; set; }
        public string payout_hash { get; set; }
    }

    public class NowPaymentsIpn
    {
        public string payment_id { get; set; }
        public string parent_payment_id { get; set; }
        public string invoice_id { get; set; }
        public string payment_status { get; set; }
        public string pay_address { get; set; }
        public string payin_extra_id { get; set; }
        public decimal price_amount { get; set; }
        public string price_currency { get; set; }
        public decimal pay_amount { get; set; }
        public decimal actually_paid { get; set; }
        public decimal actually_paid_at_fiat { get; set; }
        public string pay_currency { get; set; }
        public string order_id { get; set; }
        public string order_description { get; set; }
        public string purchase_id { get; set; }
        public decimal outcome_amount { get; set; }
        public string outcome_currency { get; set; }
        public string payout_hash { get; set; }
        public string payin_hash { get; set; }
        public DateTime? created_at { get; set; }
        public DateTime? updated_at { get; set; }
        public string burning_percent { get; set; }
        public string type { get; set; }
        public List<string> payment_extra_ids { get; set; }
    }

    public class NowPaymentsStatusResult
    {
        public string message { get; set; }
    }

    public class NowPaymentsCurrenciesResult
    {
        public ICollection<string> currencies { get; set; }

        [JsonIgnore]
        public ICollection<string> Currencies => currencies;
    }

    public class NowPaymentsMinAmountResult
    {
        public decimal min_amount { get; set; }
        public decimal fiat_equivalent { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Data { get; set; }

        [JsonIgnore]
        public decimal MinAmount => min_amount;

        [JsonIgnore]
        public decimal FiatEquivalent => fiat_equivalent;
    }

    public class NowPaymentsEstimateResult
    {
        public decimal estimated_amount { get; set; }
        public string currency_from { get; set; }
        public string currency_to { get; set; }
    }

    public class NowPaymentsApiException : Exception
    {
        public string RequestMethod { get; }
        public string RequestUri { get; }
        public int StatusCode { get; }
        public string ResponseBody { get; }
        public string RequestBody { get; }

        public NowPaymentsApiException(string requestMethod, string requestUri, int statusCode, string responseBody, string requestBody = null)
            : base($"NOWPayments API request failed with status {statusCode} for {requestMethod} {requestUri}: {responseBody}")
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
