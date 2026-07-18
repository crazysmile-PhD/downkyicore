using System.Collections.Generic;
using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaGetFiles
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public IReadOnlyList<AriaUri>? Result { get; set; }

        [JsonProperty("error")]
        public AriaError? Error { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaGetFilesResult
    {
        [JsonProperty("completedLength")]
        public string CompletedLength { get; set; } = string.Empty;

        [JsonProperty("index")]
        public string Index { get; set; } = string.Empty;

        [JsonProperty("length")]
        public string Length { get; set; } = string.Empty;

        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;

        [JsonProperty("selected")]
        public string Selected { get; set; } = string.Empty;

        [JsonProperty("uris")]
        public IReadOnlyList<AriaUri> Uris { get; set; } = Array.Empty<AriaUri>();

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
