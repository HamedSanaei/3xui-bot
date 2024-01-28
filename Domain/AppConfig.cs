using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{
    public class AppConfig
    {
        public List<long> AdminsUserIds { get; set; }
        public string BotToken { get; set; }
        public List<string> ChannelIds { get; set; }
        public List<PriceConfig> PriceColleagues { get; set; }
        public List<PriceConfig> Price { get; set; }
    }

    public class PriceConfig
    {
        public string DurationName { get; set; }
        public int Traffic { get; set; }
        public int Price { get; set; }
        public string Duration { get; set; }

    }

}