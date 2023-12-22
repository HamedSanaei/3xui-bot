using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{
    public class Inbound
    {
        public int Id { get; set; }
        public string Type { get; set; }
        // other properties...
        public int Port { get; set; }
    }
}