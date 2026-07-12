using System.Collections.Generic;
using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaGetServers
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public IReadOnlyList<AriaGetServersResult> Result { get; set; } = Array.Empty<AriaGetServersResult>();

        [JsonProperty("error")]
        public AriaError Error { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaGetServersResult
    {
        [JsonProperty("index")]
        public string Index { get; set; } = string.Empty;

        [JsonProperty("servers")]
        public IReadOnlyList<AriaResultServer> Servers { get; set; } = Array.Empty<AriaResultServer>();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaResultServer
    {
        [JsonProperty("currentUri")]
        public string CurrentAddress { get; set; } = string.Empty;

        [JsonProperty("downloadSpeed")]
        public string DownloadSpeed { get; set; } = string.Empty;

        [JsonProperty("uri")]
        public string Address { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
