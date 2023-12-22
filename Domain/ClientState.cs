using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Adminbot.Domain
{
    public class ClientState
    {

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("msg")]
        public string Message { get; set; }

        [JsonProperty("obj")]
        public ClientStateObject ClientStateObject { get; set; }

    }

    public class ClientStateObject
    {
        public int Id { get; set; }
        public int InboundId { get; set; }
        public bool Enable { get; set; }
        public string Email { get; set; }
        public long Up { get; set; }
        public long Down { get; set; }
        public long ExpiryTime { get; set; }
        public long Total { get; set; }
    }
}