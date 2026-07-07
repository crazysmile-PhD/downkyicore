using Newtonsoft.Json;
using System.Collections.Generic;

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
        public List<AriaGetServersResult> Result { get; set; } = new();

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
        public List<AriaResultServer> Servers { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaResultServer
    {
        [JsonProperty("currentUri")]
        public string CurrentUri { get; set; } = string.Empty;

        [JsonProperty("downloadSpeed")]
        public string DownloadSpeed { get; set; } = string.Empty;

        [JsonProperty("uri")]
        public string Uri { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
