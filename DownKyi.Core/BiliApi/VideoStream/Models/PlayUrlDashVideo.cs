using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlDashVideo : BaseModel
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("base_url")] public string BaseAddress { get; set; } = string.Empty;

    [JsonProperty("backup_url")] public IReadOnlyList<string> BackupUrl { get; set; } = Array.Empty<string>();

    // bandwidth
    [JsonProperty("mimeType")] public string MimeType { get; set; } = string.Empty;

    // mime_type
    [JsonProperty("codecs")] public string Codecs { get; set; } = string.Empty;
    [JsonProperty("width")] public int Width { get; set; }
    [JsonProperty("height")] public int Height { get; set; }

    [JsonProperty("frameRate")] public string FrameRate { get; set; } = string.Empty;

    public long ExpectedSize { get; set; }

    // frame_rate
    // sar
    // startWithSap
    // start_with_sap
    // SegmentBase
    // segment_base
    [JsonProperty("codecid")] public int CodecId { get; set; }
}
