using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    /*
         {
        "id": "qwer",
        "jsonrpc": "2.0",
        "result": {
            "downloadSpeed": "0",
            "numActive": "0",
            "numStopped": "0",
            "numStoppedTotal": "0",
            "numWaiting": "0",
            "uploadSpeed": "0"
        }
        }
         */
    [JsonObject]
    public class AriaGetGlobalStat
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public AriaGetGlobalStatResult Result { get; set; } = new();

        [JsonProperty("error")]
        public AriaError Error { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaGetGlobalStatResult
    {
        [JsonProperty("downloadSpeed")]
        public string DownloadSpeed { get; set; } = string.Empty;

        [JsonProperty("numActive")]
        public string NumActive { get; set; } = string.Empty;

        [JsonProperty("numStopped")]
        public string NumStopped { get; set; } = string.Empty;

        [JsonProperty("numStoppedTotal")]
        public string NumStoppedTotal { get; set; } = string.Empty;

        [JsonProperty("numWaiting")]
        public string NumWaiting { get; set; } = string.Empty;

        [JsonProperty("uploadSpeed")]
        public string UploadSpeed { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
