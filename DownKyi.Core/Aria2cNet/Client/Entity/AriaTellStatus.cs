using System.Collections.Generic;
using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaTellStatus
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public AriaTellStatusResult? Result { get; set; }

        [JsonProperty("error")]
        public AriaError? Error { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaTellStatusList
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("jsonrpc")]
        public string Jsonrpc { get; set; } = string.Empty;

        [JsonProperty("result")]
        public IReadOnlyList<AriaTellStatusResult>? Result { get; set; }

        [JsonProperty("error")]
        public AriaError? Error { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaTellStatusResult
    {
        [JsonProperty("bitfield")]
        public string Bitfield { get; set; } = string.Empty;

        [JsonProperty("completedLength")]
        public string CompletedLength { get; set; } = string.Empty;

        [JsonProperty("connections")]
        public string Connections { get; set; } = string.Empty;

        [JsonProperty("dir")]
        public string Dir { get; set; } = string.Empty;

        [JsonProperty("downloadSpeed")]
        public string DownloadSpeed { get; set; } = string.Empty;

        [JsonProperty("errorCode")]
        public string ErrorCode { get; set; } = string.Empty;

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        [JsonProperty("files")]
        public IReadOnlyList<AriaTellStatusResultFile> Files { get; set; } = Array.Empty<AriaTellStatusResultFile>();

        [JsonProperty("gid")]
        public string Gid { get; set; } = string.Empty;

        [JsonProperty("numPieces")]
        public string NumPieces { get; set; } = string.Empty;

        [JsonProperty("pieceLength")]
        public string PieceLength { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("totalLength")]
        public string TotalLength { get; set; } = string.Empty;

        [JsonProperty("uploadLength")]
        public string UploadLength { get; set; } = string.Empty;

        [JsonProperty("uploadSpeed")]
        public string UploadSpeed { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [JsonObject]
    public class AriaTellStatusResultFile
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
