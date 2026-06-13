using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Adminbot.Domain
{
    public class AppConfig
    {
        public AppConfig()
        {

        }
        public List<long> AdminsUserIds { get; set; }
        public string BotToken { get; set; }
        public string IpnSecretKey { get; set; }
        public string SupportAccount { get; set; }
        public string MainChannel { get; set; }
        public long TrafficPriceUser { get; set; }
        public long TrafficPriceShop { get; set; }
        public string LoggerChannel { get; set; }
        public bool UserActivityLogEnabled { get; set; } = true;
        public string UserActivityLogLevel { get; set; } = "Information";
        public string UserActivityLogFilePath { get; set; } = "./Data/Logs/user-activity-{shamsiDate}.jsonl";
        public int UserActivityLogMaxExceptionDepth { get; set; } = 1;
        public int BroadcastDelayMs { get; set; } = 250;
        public int BroadcastMaxRetryCount { get; set; } = 3;
        public int BroadcastQueueCapacity { get; set; } = 10000;
        public string NowPaymentApiKey { get; set; }
        public string NowPaymentJwtToken { get; set; }
        public string NowPaymentEmail { get; set; }
        public string NowPaymentPassword { get; set; }
        public string NowpaymentPriceCurrency { get; set; } = "usdtbsc";
        public string NowpaymentPayCurrency { get; set; } = "trx";
        public long NowpaymentUsdIrtFallbackPrice { get; set; } = 1800000;
        public string XuiApiVersionMode { get; set; } = "auto";
        public string XuiV3ApiBaseUrl { get; set; }
        public string XuiV3ApiRootPath { get; set; } = string.Empty;
        public string XuiV3ApiToken { get; set; }
        public string XuiV3SubLinkBaseUrl { get; set; }
        public string XuiV3ServicePlansPath { get; set; } = "./Data/xui-v3-service-plans.json";
        public int XuiV3RequestTimeoutSeconds { get; set; } = 60;
        public string NowpaymentSuccessUrl { get; set; }
        public string NowpaymentCancelUrl { get; set; }
        public string NowpaymentIpnUrl { get; set; }
        public string Iransocks5 { get; set; }
        public string AbanTetherTrxUrl { get; set; }
        public string AbanTetherUsdtUrl { get; set; }

        public string ZibalMerchantCode { get; set; }
        public long BackupChannel { get; set; }
        public string[] IosTutorial { get; set; }
        public string[] AndroidTutorial { get; set; }
        public string[] WindowsTutorial { get; set; }

        public List<string> ChannelIds { get; set; }

        public List<PriceConfig> PriceColleagues { get; set; }
        public List<PriceConfig> PriceCommon { get; set; }

        public List<PriceConfig> Price { get; set; }
    }

    public class PriceConfig
    {
        public string DurationName { get; set; }
        public int Traffic { get; set; }
        public int Price { get; set; }
        public string Duration { get; set; }
        public string FakeDescription { get; set; }

    }

}
