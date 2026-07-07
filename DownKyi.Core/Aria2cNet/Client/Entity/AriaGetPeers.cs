using Newtonsoft.Json;
using System.Collections.Generic;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaGetPeers
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public List<AriaPeer> Result { get; set; } = new();

        [JsonProperty("error")]
        public AriaError Error { get; set; } = new();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaPeer
    {
        [JsonProperty("amChoking")]
        public string AmChoking { get; set; } = string.Empty;

        [JsonProperty("bitfield")]
        public string Bitfield { get; set; } = string.Empty;

        [JsonProperty("downloadSpeed")]
        public string DownloadSpeed { get; set; } = string.Empty;

        [JsonProperty("ip")]
        public string Ip { get; set; } = string.Empty;

        [JsonProperty("peerChoking")]
        public string PeerChoking { get; set; } = string.Empty;

        [JsonProperty("peerId")]
        public string PeerId { get; set; } = string.Empty;

        [JsonProperty("port")]
        public string Port { get; set; } = string.Empty;

        [JsonProperty("seeder")]
        public string Seeder { get; set; } = string.Empty;

        [JsonProperty("uploadSpeed")]
        public string UploadSpeed { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
