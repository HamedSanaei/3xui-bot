using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{
    using Newtonsoft.Json;
    using System;

    public class InboundState
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("msg")]
        public string Message { get; set; }

        [JsonProperty("obj")]
        public ServerInfoObject ServerInfoObject { get; set; }
    }

    public class ServerInfoObject
    {
        [JsonProperty("settings")]
        public string Settings { get; set; }

    }








}