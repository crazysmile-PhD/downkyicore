using System.Collections.Generic;
using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaSendData
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("method")]
        public string Method { get; set; } = string.Empty;

        [JsonProperty("params")]
        public List<object> Params { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaSendOption
    {
        [JsonProperty("all-proxy")]
        public string HttpProxy { get; set; } = string.Empty;

        [JsonProperty("out")]
        public string Out { get; set; } = string.Empty;

        [JsonProperty("dir")]
        public string Dir { get; set; } = string.Empty;

        [JsonProperty("continue")]
        public string Continue { get; set; } = string.Empty;

        [JsonProperty("allow-overwrite")]
        public string AllowOverwrite { get; set; } = string.Empty;

        [JsonProperty("auto-file-renaming")]
        public string AutoFileRenaming { get; set; } = string.Empty;

        //[JsonProperty("header")]
        //public string Header { get; set; } = string.Empty;

        //[JsonProperty("use-head")]
        //public string UseHead { get; set; } = string.Empty;

        [JsonProperty("user-agent")]
        public string UserAgent { get; set; } = string.Empty;

        [JsonProperty("split")]
        public string Split { get; set; } = string.Empty;

        [JsonProperty("max-connection-per-server")]
        public string MaxConnectionPerServer { get; set; } = string.Empty;

        [JsonProperty("min-split-size")]
        public string MinSplitSize { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
