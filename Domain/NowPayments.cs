using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Adminbot.Domain
{


    public class StatusResult
    {
        public string Message { get; set; }
    }
    public class LoginResult
    {
        public string Token { get; set; }
    }

    public class AuthenticationInfo
    {
        public string Email { get; set; }
        public string Password { get; set; }
    }
    public class AvailableCurrenciesResult
    {
        public ICollection<string> Currencies { get; set; }
    }

    public class PaymentFactory
    {
        private readonly AppConfig _config;

        public PaymentFactory(IConfiguration configuration)
        {
            _config = configuration.Get<AppConfig>();
        }

        public CreatepaymentCto Create(decimal netPrice,
                                       decimal tetherRate,
                                       decimal trxRate)
        {
            return new CreatepaymentCto
            {
                price_amount = netPrice / tetherRate,
                pay_amount = netPrice / trxRate,
                ipn_callback_url = _config.NowpaymentIpnUrl
            };
        }
    }

    public class CreatepaymentCto
    {

        public CreatepaymentCto()
        {

        }
        // dollar
        public decimal price_amount { get; set; }
        public string price_currency { get; set; } = "usd";
        // bitcoin amount (optional)
        public decimal pay_amount { get; set; }
        public string pay_currency { get; set; } = "trx";
        public string ipn_callback_url { get; set; }
        public string order_id { get; set; } // آیدی ما در دیتا بیس خودمون
        public string order_description { get; set; } = "Apple Macbook Pro 2019 x 1";
    }

    public class PaymentStatusResult
    {
        public long payment_id { get; set; }
        public string payment_status { get; set; }
        public string pay_address { get; set; }
        public double price_amount { get; set; }
        public string price_currency { get; set; } = "usd";
        public double pay_amount { get; set; }
        public string pay_currency { get; set; } = "trx";
        public string order_id { get; set; }
        public string order_description { get; set; }
        public long purchase_id { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public double outcome_amount { get; set; }
        public string outcome_currency { get; set; } = "trx";
    }
    public enum PaymentStatusEnum
    {
        waiting, //waiting for the customer to send the payment. The initial status of each payment;
        confirming, // the transaction is being processed on the blockchain. Appears when NOWPayments detect the funds from the user on the blockchain;
        confirmed,// - the process is confirmed by the blockchain.Customer’s funds have accumulated enough confirmations;
        sending,// - the funds are being sent to your personal wallet.We are in the process of sending the funds to you;
        partially_paid,// - it shows that the customer sent the less than the actual price.Appears when the funds have arrived in your wallet;
        finished,// - the funds have reached your personal address and the payment is finished;
        failed,// - the payment wasn't completed due to the error of some kind;
        refunded,// - the funds were refunded back to the user;
        expired,// - the user didn't send the funds to the specified address in the 7 days time window;

    }
    public class PaymentLinkCto
    {
        public string payment_id { get; set; }
        public string payment_status { get; set; }
        public string pay_address { get; set; }
        public double price_amount { get; set; }
        public string price_currency { get; set; } = "usd";
        public double pay_amount { get; set; }
        public double amount_received { get; set; }
        public string pay_currency { get; set; } = "trx";
        public string order_id { get; set; }
        public string order_description { get; set; }
        public string ipn_callback_url { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public string purchase_id { get; set; }
        public string smart_contract { get; set; }
        public string network { get; set; } = "trx";
        public string network_precision { get; set; }
        public string time_limit { get; set; }
        public string burning_percent { get; set; }
        public DateTime expiration_estimate_date { get; set; }
        public bool is_fixed_rate { get; set; }
        public bool is_fee_paid_by_user { get; set; }
        public DateTime valid_until { get; set; }
        public string type { get; set; }
        public string weswap_paymentlink { get; set; }

    }

    public class SwapinoPaymentInfo
    {
        // now payment 
        [Key]
        public int Payment_Id { get; set; } // آیدی توی دیتابیس خودمون که میشه اردر آیدی برای نوپیمینت
        public string Result { get; set; }
        public string NowPaymentId { get; set; }  // این payment_id از NOWPayments هست
        public string Order_Id { get; set; }                 // مقدار order_id که خودت وقتی payment ایجاد می‌کنی ارسال می‌کنی

        public string CallbackUrl { get; set; }
        public long RialAmount { get; set; }
        public long TelegramUserId { get; set; }
        public decimal Price_Amount { get; set; }
        public decimal Pay_Amount { get; set; }
        public decimal Actually_Paid { get; set; }
        public decimal TronAmount { get; set; }
        public decimal UsdtAmount { get; set; }
        public long TelMsgId { get; set; }
        public bool IsAddedToBallance { get; set; } = false;
        public string Payment_Status { get; set; } = "Pending";   // وضعیت پرداخت (waiting, confirmed, finished, failed, expired)
        public string Pay_Address { get; set; }

        public string Price_Currency { get; set; }
        public string Pay_Currency { get; set; }
        public long Purchase_Id { get; set; }
        public string Payin_Hash { get; set; }
        public string Payout_Hash { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime Updated_At { get; set; }
        public bool IpN_Processed { get; set; } = false;    // برای جلوگیری از چندبار پردازش

    }

    public class NowPayments
    {
        public AuthenticationInfo AuthenticationInfo { get; set; }

        public string Token { get; set; }
        private readonly AppConfig _appConfig;
        public NowPayments()
        {
            AuthenticationInfo = new AuthenticationInfo { Email = "EMAIL", Password = "PASSWORD" };
            // Authentication();
        }
        public async Task GetAPIStatus()
        {
            HttpClient httpClient = new HttpClient();
            string url = "https://api.nowpayments.io/v1/status";
            httpClient.BaseAddress = new Uri(url);
            HttpResponseMessage response = await httpClient.GetAsync("");
            string responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<StatusResult>(responseBody);
            // Console.WriteLine(result.Message);
        }



        public NowPayments(IConfiguration configuration)
        {
            _appConfig = configuration.Get<AppConfig>();
        }

        //useless!
        // public void Authentication()
        // {
        //     string url = "https://api.nowpayments.io/v1/auth";
        //     restClient = new RestClient(url);
        //     restRequest.AddHeader("Content-Type", "application/json");
        //     //restRequest.AddHeader("Accept", "application/json");
        //     restRequest.AddJsonBody(AuthenticationInfo);
        //     var response = restClient.Post<LoginResult>(restRequest);
        //     // Console.WriteLine(response?.Token);
        //     Token = response?.Token ?? "Error: No Token";
        // }

        public async Task GetAvailableCurrencies()
        {
            HttpClient httpClient = new HttpClient();
            string url = "https://api.nowpayments.io/v1/currencies";
            httpClient.BaseAddress = new Uri(url);
            httpClient.DefaultRequestHeaders.Add("x-api-key", _appConfig.NowPaymentApiKey);

            try
            {
                HttpResponseMessage response = await httpClient.GetAsync("");
                string responseBody = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<AvailableCurrenciesResult>(responseBody);



                foreach (var item in result?.Currencies)
                {
                    if (item == "trx")
                        Console.WriteLine(item);
                }
            }
            catch (System.Exception)
            {
                Console.WriteLine("An Error Occured with login or Api key");
            }

            //Console.WriteLine(response?.Content);

        }
        public async Task<long> GetTronRate()
        {
            HttpClient httpClient = new HttpClient();
            string url = "https://api.wallex.ir/v1/markets";
            httpClient.BaseAddress = new Uri(url);
            HttpResponseMessage response = await httpClient.GetAsync("");
            string responseBody = await response.Content.ReadAsStringAsync();

            JObject data = JObject.Parse(responseBody);
            var lastPricess = data["result"]["symbols"]["TRXTMN"]["stats"]["lastPrice"].Value<double>();

            long lastPrice = Convert.ToInt64(lastPricess);
            return lastPrice;
        }

        public async Task<long> GetUsThetherRate()
        {
            HttpClient httpClient = new HttpClient();
            string url = "https://api.wallex.ir/v1/markets";
            httpClient.BaseAddress = new Uri(url);
            HttpResponseMessage response = await httpClient.GetAsync("");
            string responseBody = await response.Content.ReadAsStringAsync();

            JObject data = JObject.Parse(responseBody);
            var lastPricess = data["result"]["symbols"]["USDTTMN"]["stats"]["lastPrice"].Value<double>();


            long lastPrice = Convert.ToInt64(lastPricess);
            return lastPrice;
        }



        public async Task<PaymentStatusResult> GetPaymentStatus(long patmentId)
        {

            HttpClient httpClient = new HttpClient();
            string url = $"https://api.nowpayments.io/v1/payment/{patmentId.ToString()}";
            httpClient.BaseAddress = new Uri(url);
            httpClient.DefaultRequestHeaders.Add("x-api-key", _appConfig.NowPaymentApiKey);
            httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");
            PaymentStatusResult result = null;

            try
            {
                HttpResponseMessage x = await httpClient.GetAsync("");
                string responseBody = await x.Content.ReadAsStringAsync();
                result = JsonConvert.DeserializeObject<PaymentStatusResult>(responseBody);

            }
            catch
            {

            }
            return result;

        }

        public async Task<PaymentLinkCto> CreatepaymentWithSwapino(long rialPrice = 150000)
        {
            if (rialPrice < 60000) rialPrice = 60000;
            double netPrice = 0;
            // netPrice = rialPrice > 200000 ? (double)rialPrice - (rialPrice * 0.05) : rialPrice - 10000;

            var trxRate = await GetTronRate();
            var thetherRate = await GetUsThetherRate();
            long fee = Convert.ToInt64(1.1 * trxRate) + 8000;

            netPrice = rialPrice - fee;

            Console.WriteLine("This is my amountttttttt:   " + netPrice.ToString());

            HttpClient httpClient = new HttpClient();
            string url = "https://api.nowpayments.io/v1/payment";
            httpClient.BaseAddress = new Uri(url);
            httpClient.DefaultRequestHeaders.Add("x-api-key", _appConfig.NowPaymentApiKey);
            // httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");

            CreatepaymentCto payment = new CreatepaymentCto
            {
                price_amount = Convert.ToDecimal(netPrice / thetherRate),
                pay_amount = Convert.ToDecimal(netPrice / trxRate),
            };
            PaymentLinkCto result = null;
            try
            {
                var response = await httpClient.PostAsJsonAsync("", value: payment);

                string responseBody = await response.Content.ReadAsStringAsync();
                result = JsonConvert.DeserializeObject<PaymentLinkCto>(responseBody);

                string paymentLink = $"https://t.me/SwapinoBot?start=trx-{result.pay_address}-{netPrice}-irt";

                Console.WriteLine($"PaymentId: {result.payment_id}  USD Amount: {result.price_amount} TRX Amount: {result.pay_amount}");
                Console.WriteLine(paymentLink);
                result.weswap_paymentlink = paymentLink;

            }
            catch
            {

            }
            return result;

        }


        // public void GetTheMinimumPaymentAmount()
        // {
        //     string url = "https://api.nowpayments.io/v1/min-amount?currency_from=eth&currency_to=trx&fiat_equivalent=usd";
        //     restClient = new RestClient(url);
        //     restRequest = new RestRequest();
        //     restRequest.AddHeader("x-api-key", API_KEY);
        //     var response = restClient.Post(restRequest);
        // }
    }
    public class NowPaymentsIpn
    {
        public long payment_id { get; set; }
        public long? invoice_id { get; set; }
        public string payment_status { get; set; }
        public string pay_address { get; set; }
        public string? payin_extra_id { get; set; }
        public decimal price_amount { get; set; }
        public string price_currency { get; set; }
        public decimal pay_amount { get; set; }
        public decimal actually_paid { get; set; }
        public string pay_currency { get; set; }
        public string? order_id { get; set; }
        public string? order_description { get; set; }
        public long purchase_id { get; set; }
        public decimal outcome_amount { get; set; }
        public string outcome_currency { get; set; }
        public string payout_hash { get; set; }
        public string payin_hash { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public string? burning_percent { get; set; }
        public string type { get; set; }
        public List<long> payment_extra_ids { get; set; }
    }

}
