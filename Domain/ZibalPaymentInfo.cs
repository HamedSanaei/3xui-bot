using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Adminbot.Domain
{
    public class ZibalPaymentInfo
    {
        [Key]
        public int Id { get; set; }
        public long TrackId { get; set; }
        public string Result { get; set; }
        public string CallbackUrl { get; set; }
        public long Amount { get; set; }
        public long TelegramUserId { get; set; }
        public long ChatId { get; set; }
        public long TelMsgId { get; set; }
        public bool IsAddedToBallance { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime PaidAt { get; set; }
        public int AttemptsRemaining { get; set; } = 30; // Default to 30 attempts
        public bool IsPaid { get; set; } = false;
        public bool IsExpired { get; set; } = false;



        public ZibalPaymentInfo(long userId)
        {
            CallbackUrl = $"http://pluspremium.ir/confirm/{userId}_{this.Id}";
            TelegramUserId = userId;

        }
        public ZibalPaymentInfo()
        {

        }

    }
    public class PaymentRequest
    {
        [JsonProperty("merchant")]
        public string Merchant { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }

        [JsonProperty("callbackUrl")]
        public string CallbackUrl { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        // شناسه‌ی جلسه‌ی پرداختی که قصد تایید آن را دارید.
        [JsonProperty("orderId")]
        public string OrderId { get; set; }

    }
    public class PaymentRequestResponse
    {
        public long TrackId { get; set; }
        public string Result { get; set; }
        public string PayLink { get; set; }
        public string Message { get; set; }
    }
    public class PaymentVerificationResponse
    {
        [JsonProperty("paidAt")]
        public string PaidAt { get; set; }

        [JsonProperty("cardNumber")]
        public string CardNumber { get; set; }

        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }

        [JsonProperty("refNumber")]
        public string RefNumber { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("orderId")]
        public string OrderId { get; set; }

        [JsonProperty("result")]
        public int Result { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
    public class InquiryResponse
    {
        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("paidAt")]
        public DateTime? PaidAt { get; set; }

        [JsonProperty("verifiedAt")]
        public DateTime? VerifiedAt { get; set; }

        [JsonProperty("cardNumber")]
        public string CardNumber { get; set; }

        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }

        [JsonProperty("refNumber")]
        public string RefNumber { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("orderId")]
        public string OrderId { get; set; }

        [JsonProperty("wage")]
        public long Wage { get; set; }

        [JsonProperty("result")]
        public int Result { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }


}