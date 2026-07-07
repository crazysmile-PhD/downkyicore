using Newtonsoft.Json;
using System.Collections.Generic;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaGetUris
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public List<AriaUri> Result { get; set; } = new();

        [JsonProperty("error")]
        public AriaError Error { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
