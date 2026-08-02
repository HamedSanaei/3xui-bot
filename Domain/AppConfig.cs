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
        /// <summary>
        /// Enables the aggregate Saturday 00:01 Tehran usage chart sent to the central logger channel.
        /// </summary>
        /// <remarks>
        /// The default is disabled so older configuration files do not unexpectedly start scheduled delivery.
        /// Manual super-admin weekly and 30-day usage commands remain available regardless of this flag.
        /// </remarks>
        public bool WeeklyUsageReportEnabled { get; set; }
        /// <summary>
        /// Enables the append-only daily diagnostic file that captures warning, error, and critical application logs.
        /// </summary>
        /// <remarks>
        /// This file is independent from the compact user-activity JSONL audit trail. It is intended for operational
        /// troubleshooting and never contains bot tokens, API keys, cookies, or webhook secrets.
        /// </remarks>
        public bool ErrorFileLogEnabled { get; set; } = true;
        /// <summary>
        /// Minimum Microsoft.Extensions.Logging level written to the daily diagnostic file.
        /// </summary>
        /// <remarks>
        /// The default <c>Warning</c> records operational problems without copying normal information-level traffic.
        /// Accepted values use the standard <see cref="Microsoft.Extensions.Logging.LogLevel"/> names.
        /// </remarks>
        public string ErrorFileLogMinimumLevel { get; set; } = "Warning";
        /// <summary>
        /// UTF-8 append-only diagnostic file path. The <c>{shamsiDate}</c> placeholder is expanded using Tehran time.
        /// </summary>
        public string ErrorFileLogFilePath { get; set; } = "./Data/Logs/errors-{shamsiDate}.log";
        public string UserDatabasePath { get; set; } = "./Data/users.db";
        public string CredentialsDatabasePath { get; set; } = "./Data/credentials.db";
        public int BroadcastDelayMs { get; set; } = 250;
        public int BroadcastMaxRetryCount { get; set; } = 3;
        public int BroadcastQueueCapacity { get; set; } = 10000;
        /// <summary>
        /// Global owned-bot referral settings used by registration, settlement, reporting, and reconciliation.
        /// </summary>
        /// <remarks>
        /// Referral relationships are global across every owned bot. Bot identifiers are retained only for
        /// attribution and never partition relationship uniqueness or reward eligibility.
        /// </remarks>
        public ReferralOptions Referral { get; set; } = new();
        /// <summary>
        /// Maximum duration, in seconds, allowed for one Telegram bot startup probe such as <c>getMe</c> or
        /// command-menu configuration.
        /// </summary>
        /// <remarks>
        /// A transient probe timeout does not stop receiver creation. The runtime starts the receiver optimistically
        /// and repeats identity and command initialization in the background. Values below five seconds are clamped
        /// to protect normal Telegram latency, and values above sixty seconds are capped to keep owner callbacks responsive.
        /// </remarks>
        public int TelegramBotStartupProbeTimeoutSeconds { get; set; } = 12;
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
        /// <summary>
        /// Maximum time, in seconds, that one HTTP attempt against the XUI v3 panel may run before it is treated as a
        /// transient panel timeout.
        /// </summary>
        public int XuiV3RequestTimeoutSeconds { get; set; } = 60;
        /// <summary>
        /// Number of additional attempts used for transient XUI v3 transport failures such as TLS record errors,
        /// request timeouts, HTTP 429, and HTTP 5xx gateway errors.
        /// </summary>
        public int XuiV3TransientRetryCount { get; set; } = 3;
        /// <summary>
        /// Initial delay, in milliseconds, before retrying a transient XUI v3 API failure.
        /// </summary>
        public int XuiV3TransientRetryBaseDelayMs { get; set; } = 1500;
        /// <summary>
        /// Maximum retry delay, in milliseconds, used when exponential backoff is applied to transient XUI v3 API
        /// failures.
        /// </summary>
        public int XuiV3TransientRetryMaxDelayMs { get; set; } = 12000;
        /// <summary>
        /// Number of minutes an explicit account link-change confirmation remains valid before it is closed safely.
        /// </summary>
        public int XuiV3LinkChangeConfirmationMinutes { get; set; } = 10;
        /// <summary>
        /// Number of seconds between scans for XUI link-change operations that require automatic recovery.
        /// </summary>
        public int XuiV3LinkChangeRecoveryPollSeconds { get; set; } = 30;
        /// <summary>
        /// Maximum number of foreground and background attempts made for the same saved link-change identity.
        /// </summary>
        public int XuiV3LinkChangeRecoveryMaxAttempts { get; set; } = 12;
        /// <summary>
        /// Maximum exponential-backoff delay, in seconds, before retrying an ambiguous XUI link change.
        /// </summary>
        public int XuiV3LinkChangeRecoveryMaxDelaySeconds { get; set; } = 900;
        /// <summary>
        /// Exclusive processing lease, in seconds, used to prevent concurrent workers from mutating one XUI client.
        /// </summary>
        public int XuiV3LinkChangeLeaseSeconds { get; set; } = 300;
        public bool AccountExpiryReminderEnabled { get; set; } = true;
        public int AccountExpiryReminderHourIran { get; set; } = 8;
        public int[] AccountExpiryReminderDays { get; set; } = new[] { 7, 3, 1 };
        public string NowpaymentSuccessUrl { get; set; }
        public string NowpaymentCancelUrl { get; set; }
        public string NowpaymentIpnUrl { get; set; }
        /// <summary>
        /// Enables creation of new NOWPayments cryptocurrency invoices across owned and tenant bots.
        /// </summary>
        /// <remarks>
        /// This startup-bound value initializes the live gateway snapshot. Super-admin changes update that snapshot
        /// and this JSON key without rebinding the options object. Disabling never stops IPN or existing settlement.
        /// </remarks>
        public bool NowPaymentsEnabled { get; set; }
        /// <summary>
        /// Enables creation of new HooshPay rial invoices across owned and tenant bots.
        /// </summary>
        /// <remarks>
        /// This startup-bound value initializes the live gateway snapshot. Super-admin changes update that snapshot
        /// and this JSON key immediately. Existing payment rows remain verifiable and settleable after disablement;
        /// a missing configuration key initializes the gateway as disabled.
        /// </remarks>
        public bool HooshPayEnabled { get; set; }
        public string HooshPayApiKey { get; set; }
        public string HooshPayIpnSecretKey { get; set; }
        public string HooshPayBaseUrl { get; set; } = "https://pay.hooshnet.com";
        public string HooshPayIpnUrl { get; set; }
        public string HooshPayReturnUrl { get; set; }
        /// <summary>
        /// Enables creation of new Tetraminator rial invoices across owned and tenant bots.
        /// </summary>
        /// <remarks>
        /// This startup-bound value initializes the live gateway snapshot. Super-admin changes update that snapshot
        /// and this JSON key immediately. Existing payment rows remain verifiable and settleable after disablement.
        /// </remarks>
        public bool TetraminatorEnabled { get; set; }
        /// <summary>
        /// Base API URL used for Tetraminator invoice creation and payment inquiry requests.
        /// </summary>
        public string TetraminatorApiBaseUrl { get; set; } = "https://api.tetraminator.com/v1";
        /// <summary>
        /// Secret Tetraminator API key sent through the <c>X-API-KEY</c> header.
        /// </summary>
        /// <remarks>This value must never be included in Telegram messages, logs, or persisted raw request JSON.</remarks>
        public string TetraminatorApiKey { get; set; }
        /// <summary>
        /// Public callback endpoint called by Tetraminator after a successful payment.
        /// </summary>
        public string TetraminatorCallbackUrl { get; set; }
        /// <summary>
        /// Maximum duration in seconds for one Tetraminator HTTP request.
        /// </summary>
        public int TetraminatorRequestTimeoutSeconds { get; set; } = 30;
        /// <summary>
        /// Number of additional retries for read-only Tetraminator payment inquiries.
        /// </summary>
        /// <remarks>Invoice creation is never retried because that provider operation has no idempotency key.</remarks>
        public int TetraminatorInquiryRetryCount { get; set; } = 3;
        /// <summary>
        /// Minimum invoice amount in Iranian toman offered through Tetraminator.
        /// </summary>
        public long TetraminatorMinimumAmountToman { get; set; } = 50000;
        /// <summary>
        /// Enables creation of new UniquePay toman invoices across owned and tenant bots.
        /// </summary>
        /// <remarks>
        /// This startup-bound value initializes the live gateway snapshot. Super-admin changes update that snapshot
        /// and this JSON key immediately. Existing invoices remain eligible for polling and settlement while disabled;
        /// a missing key initializes the gateway as disabled.
        /// </remarks>
        public bool UniquePayEnabled { get; set; }
        /// <summary>
        /// Official UniquePay API host used for generic invoice creation and inquiry endpoints.
        /// </summary>
        public string UniquePayBaseUrl { get; set; } = "https://uniquepay.top";
        /// <summary>
        /// Secret UniquePay business bearer token.
        /// </summary>
        /// <remarks>This value must never be displayed, partially masked, stored on payment rows, or written to logs.</remarks>
        public string UniquePayBusinessToken { get; set; }
        /// <summary>
        /// Public platform return endpoint given to UniquePay for redirecting a customer after payment.
        /// </summary>
        /// <remarks>
        /// This browser-return URL is separate from <see cref="UniquePayCallbackUrl"/>. It is only a lookup trigger;
        /// the application always calls <c>/api/check-invoice</c> before applying a financial change.
        /// </remarks>
        public string UniquePayReturnUrl { get; set; }
        /// <summary>
        /// Public HTTPS endpoint that receives UniquePay's bot-gateway payment notification.
        /// </summary>
        /// <remarks>
        /// The callback is an unsigned trigger and is never accepted as payment proof. The application locates the
        /// saved invoice by its merchant hash and performs an authoritative <c>/api/check-invoice</c> request before
        /// any wallet credit or tenant fulfillment.
        /// </remarks>
        public string UniquePayCallbackUrl { get; set; }
        /// <summary>
        /// UniquePay fee percentage displayed to customers and verified against provider inquiry data.
        /// </summary>
        /// <remarks>
        /// The provider business setting decides whether the buyer (<c>user</c>/<c>buyer</c>) or <c>owner</c> pays
        /// this fee. The public create-invoice API has no fee-payer field, so changing that party must be done in the
        /// UniquePay panel.
        /// </remarks>
        public decimal UniquePayFeePercent { get; set; } = 12m;
        /// <summary>
        /// Maximum duration, in seconds, for one UniquePay HTTP attempt.
        /// </summary>
        public int UniquePayRequestTimeoutSeconds { get; set; } = 30;
        /// <summary>
        /// Number of additional attempts allowed for transient, read-only UniquePay invoice inquiries.
        /// </summary>
        /// <remarks>UniquePay invoice creation is never retried automatically.</remarks>
        public int UniquePayInquiryRetryCount { get; set; } = 3;
        /// <summary>
        /// Number of seconds between background scans for due pending UniquePay invoices.
        /// </summary>
        public int UniquePayReconciliationIntervalSeconds { get; set; } = 30;
        /// <summary>
        /// Maximum number of automatic background inquiries allowed for one UniquePay invoice.
        /// </summary>
        /// <remarks>
        /// Callback, browser-return, customer-check, and super-admin checks remain available after this limit. The
        /// bound prevents abandoned invoices from being queried indefinitely.
        /// </remarks>
        public int UniquePayReconciliationMaxAttempts { get; set; } = 12;
        /// <summary>
        /// Maximum exponential-backoff delay, in seconds, between automatic UniquePay inquiries.
        /// </summary>
        public int UniquePayReconciliationMaxDelaySeconds { get; set; } = 900;
        /// <summary>
        /// Maximum number of due UniquePay rows processed during one reconciliation scan.
        /// </summary>
        public int UniquePayReconciliationBatchSize { get; set; } = 50;
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
