using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaShutdown
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public string Result { get; set; } = string.Empty;

        [JsonProperty("error")]
        public AriaError Error { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
