using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaGetSessionInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public AriaGetSessionInfoResult Result { get; set; } = new();

        [JsonProperty("error")]
        public AriaError Error { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaGetSessionInfoResult
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
