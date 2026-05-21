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
        public string NowPaymentApiKey { get; set; }
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