using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Adminbot.Domain
{
    public class ZibalAPI
    {

        private readonly AppConfig _appConfig;

        public ZibalAPI(IConfiguration configuration)
        {
            _appConfig = configuration.Get<AppConfig>();
        }

        /// <summary>Creates the existing Zibal payment request without changing amount or invoice admission.</summary>
        /// <param name="amount">Existing provider invoice amount in Iranian rial; passed unchanged.</param>
        /// <param name="callbackUrl">Existing callback URL sent only to Zibal, never telemetry metadata.</param>
        /// <param name="merchantId">Existing merchant credential, never diagnostic metadata.</param>
        /// <param name="description">Existing provider description, never diagnostic metadata.</param>
        /// <returns>Original provider creation response; callers retain existing validation and settlement responsibility.</returns>
        /// <remarks>Only the awaited HTTP/response parsing boundary uses payment_gateway; no balance, ledger, retry, or financial authorization changes.</remarks>
        /// <example><code>var invoice = await ZibalAPI.SendPaymentRequest(1000000, "https://example.invalid/payment/callback", "example-merchant");</code></example>
        public static async Task<PaymentRequestResponse> SendPaymentRequest(long amount, string callbackUrl, string merchantId = "zibal", string description = "توضیحات")
        {
            var paymentRequest = new PaymentRequest
            {
                Merchant = merchantId,
                Amount = amount,
                CallbackUrl = callbackUrl,
                Description = description
            };

            var json = JsonConvert.SerializeObject(paymentRequest);
            var data = new StringContent(json, Encoding.UTF8, "application/json");

            using (var client = new HttpClient())
            {
                using var latency = TelegramUpdateLatencyScope.Current?.Measure(TelegramUpdateStage.ProviderRead) ?? default;
                var response = await client.PostAsync("https://gateway.zibal.ir/v1/request", data);
                string result = await response.Content.ReadAsStringAsync();
                var paymentResponse = JsonConvert.DeserializeObject<PaymentRequestResponse>(result);
                return paymentResponse;
            }
        }
        public static string GetPaymentLink(PaymentRequestResponse payment)
        {
            return $"https://gateway.zibal.ir/start/{payment.TrackId}";
        }

        /// <summary>Performs the existing Zibal verification for the supplied provider track id.</summary>
        /// <param name="trId">Existing provider track id, never retained by latency diagnostics.</param>
        /// <param name="merchantId">Existing merchant credential sent only to the provider.</param>
        /// <returns>Original verification response; callers retain existing idempotent settlement rules.</returns>
        /// <remarks>Only awaited provider wall time is attributed to payment_gateway. No payment amount, proof validation, balance, timeout, or retry behavior changes.</remarks>
        /// <example><code>var proof = await ZibalAPI.Verify(123456, "example-merchant"); // Validate proof before existing idempotent settlement.</code></example>
        public static async Task<PaymentVerificationResponse> Verify(long trId, string merchantId = "zibal")
        {
            var paymentVerify = new
            {
                merchant = merchantId,
                trackId = trId
            };

            var json = JsonConvert.SerializeObject(paymentVerify);
            var data = new StringContent(json, Encoding.UTF8, "application/json");

            using (var client = new HttpClient())
            {
                using var latency = TelegramUpdateLatencyScope.Current?.Measure(TelegramUpdateStage.ProviderRead) ?? default;
                var response = await client.PostAsync("https://gateway.zibal.ir/v1/verify", data);
                string result = await response.Content.ReadAsStringAsync();
                var paymentverificationResponse = JsonConvert.DeserializeObject<PaymentVerificationResponse>(result);


                return paymentverificationResponse;
                // var paymentResponse = JsonConvert.DeserializeObject<PaymentRequestResponse>(result);
                // return paymentResponse;
            }


        }

        public static async Task<string> VerifyAndGetMessage(long trId, string merchantId = "zibal")
        {
            string msg = string.Empty;
            var inq = await Inquiry(trId, merchantId);
            // pardakht shode teid nashode!
            if (inq.Status == 2)
            {

                var ver_response = await ZibalAPI.Verify(trId, merchantId);

                if (ver_response.Result == 100)
                {
                    // yani taeid shod.
                    msg = "your payment was successfully confirmed!";
                }
                else if (ver_response.Result == 102 || ver_response.Result == 104 || ver_response.Result == 104)
                {
                    // ya yaft nashod ya 
                    msg = "your payment was not found or some other problems! \n";

                }
                else if (ver_response.Result == 201)
                {
                    // ghablan taeid shode
                    msg = "your payment was previously confirmed!! \n";

                }
                else if (ver_response.Result == 202)
                {
                    // pardakh nashodeh aslan
                    msg = "your payment was not paid or wasn't successful!! \n";

                }
                else if (ver_response.Result == 203)
                {
                    // track na motabar
                    msg = "trackid is not valid! \n";
                }

            }
            else if (inq.Status == 1)
            {
                //yani pardakht sode va taeid
                //Dont Change it !!! it is very important. if you change it your program enconter important bugs
                msg = "your payment was previously confirmed!! \n";

            }
            else if (inq.Status == -1)
            {
                //dar entezar pardakht
                msg = "your payment was not paid!! \n";

            }
            else
            {
                //other problems
                msg = "some problems occured and transaction is not valid. please checy on you Zibal web panel. \n";

            }
            return msg;
        }

        /// <summary>Reads the existing Zibal payment status without introducing payment retries or settlement.</summary>
        /// <param name="trId">Existing provider track id, never retained by telemetry.</param>
        /// <param name="merchantId">Existing provider merchant credential, never diagnostic metadata.</param>
        /// <returns>Original inquiry response for the caller's unchanged proof and settlement handling.</returns>
        /// <remarks>Awaited HTTP and response parsing use payment_gateway; no amount, balance, ledger, cancellation, or transport policy changes.</remarks>
        /// <example><code>var status = await ZibalAPI.Inquiry(123456, "example-merchant"); // Inquiry never settles a payment itself.</code></example>
        public static async Task<InquiryResponse> Inquiry(long trId, string merchantId = "zibal")
        {

            var paymentRequest = new
            {
                merchant = merchantId,
                trackId = trId
            };

            var json = JsonConvert.SerializeObject(paymentRequest);
            var data = new StringContent(json, Encoding.UTF8, "application/json");

            using (var client = new HttpClient())
            {
                using var latency = TelegramUpdateLatencyScope.Current?.Measure(TelegramUpdateStage.ProviderRead) ?? default;
                var response = await client.PostAsync("https://gateway.zibal.ir/v1/inquiry", data);
                string result = await response.Content.ReadAsStringAsync();
                var inquiryResponse = JsonConvert.DeserializeObject<InquiryResponse>(result);


                return inquiryResponse;
                // var paymentResponse = JsonConvert.DeserializeObject<PaymentRequestResponse>(result);
                // return paymentResponse;
            }


        }


        // mark zpi as paid
        public static ZibalPaymentInfo MarkAsPaid(ZibalPaymentInfo zpi, InquiryResponse inq)
        {
            zpi.PaidAt = inq.PaidAt ?? DateTime.MinValue;
            zpi.CreatedAt = inq.CreatedAt;
            zpi.IsPaid = true;
            zpi.IsExpired = true;
            zpi.AttemptsRemaining = 0;

            return zpi;
        }
    }




}
