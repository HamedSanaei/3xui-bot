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
        public List<BotInstanceConfig> Bots { get; set; } = new();
        public BotInstanceConfig SalesAssistantBot { get; set; } = new();
        public string BotToken { get; set; }
        public string IpnSecretKey { get; set; }
        public string SupportAccount { get; set; }
        public string MainChannel { get; set; }
        public string LoggerChannel { get; set; }
        public bool UserActivityLogEnabled { get; set; } = true;
        public string UserActivityLogLevel { get; set; } = "Information";
        public string UserActivityLogFilePath { get; set; } = "./Data/Logs/user-activity-{shamsiDate}.jsonl";
        public int UserActivityLogMaxExceptionDepth { get; set; } = 1;
        public string UserDatabasePath { get; set; } = "./Data/users.db";
        public string CredentialsDatabasePath { get; set; } = "./Data/credentials.db";
        public int BroadcastDelayMs { get; set; } = 250;
        public int BroadcastMaxRetryCount { get; set; } = 3;
        public int BroadcastQueueCapacity { get; set; } = 10000;
        public bool HttpsEnabled { get; set; } = true;
        public int HttpsPort { get; set; } = 443;
        public int HttpPort { get; set; } = 80;
        public string HttpsCertificatePath { get; set; } = "./Data/tofanservice.ir cf15years/cert.crt";
        public string HttpsCertificateKeyPath { get; set; } = "./Data/tofanservice.ir cf15years/private.key";
        public string HttpsCertificatePfxPath { get; set; }
        public string HttpsCertificatePassword { get; set; }
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
        public bool AccountExpiryReminderEnabled { get; set; } = true;
        public int AccountExpiryReminderHourIran { get; set; } = 8;
        public int[] AccountExpiryReminderDays { get; set; } = new[] { 7, 3, 1 };
        public string NowpaymentSuccessUrl { get; set; }
        public string NowpaymentCancelUrl { get; set; }
        public string NowpaymentIpnUrl { get; set; }
        public string HooshPayApiKey { get; set; }
        public string HooshPayIpnSecretKey { get; set; }
        public string HooshPayBaseUrl { get; set; } = "https://pay.hooshnet.com";
        public string HooshPayIpnUrl { get; set; }
        public string HooshPayReturnUrl { get; set; }
        /// <summary>
        /// Enables synchronization of XUI v3 account lifecycle events with the Gozargah website API.
        /// </summary>
        public bool GozargahSiteSyncEnabled { get; set; }
        /// <summary>
        /// API endpoint used for all Gozargah website actions. The API expects the action name in the JSON body.
        /// </summary>
        public string GozargahSiteApiBaseUrl { get; set; } = "https://api.gozargah.network/api.php";
        /// <summary>
        /// Bearer token used when calling the Gozargah website API. This secret must never be written to logs.
        /// </summary>
        public string GozargahSiteApiKey { get; set; }
        /// <summary>
        /// Enables the "Gozargah site wallet" payment method inside bot account purchase and renewal flows.
        /// </summary>
        public bool GozargahSiteWalletPaymentsEnabled { get; set; }
        /// <summary>
        /// Enables realtime create-order sync events after XUI v3 account creation succeeds.
        /// </summary>
        public bool GozargahSiteRealtimeCreateSyncEnabled { get; set; } = true;
        /// <summary>
        /// Enables realtime update-order sync events after XUI v3 renewal, edit, or link-change succeeds.
        /// </summary>
        public bool GozargahSiteRealtimeUpdateSyncEnabled { get; set; } = true;
        /// <summary>
        /// Enables realtime delete-order sync events after XUI v3 account deletion succeeds.
        /// </summary>
        public bool GozargahSiteRealtimeDeleteSyncEnabled { get; set; } = true;
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
