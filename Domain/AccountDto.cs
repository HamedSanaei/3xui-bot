using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Adminbot.Domain
{
    public class AccountDto
    {
        public long TelegramUserId { get; set; }
        public string SessionCookie { get; set; }
        public string SelectedCountry { get; set; }
        public string SelectedPeriod { get; set; }
        public string TotoalGB { get; set; }
        public ServerInfo ServerInfo { get; set; }
        //reality or tunnel
        public string AccType { get; set; }
        public int AccountCounter { get; set; }
        public bool IsColleague { get; set; }

    }
    public class AccountDtoUpdate : AccountDto
    {
        public ClientExtend Client { get; set; }
        public string ConfigLink { get; set; }

    }
}