using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{
    public class AddClientResult
    {
        public bool Success { get; set; }
        public string Msg { get; set; }
        public Object Obj { get; set; }
    }
}