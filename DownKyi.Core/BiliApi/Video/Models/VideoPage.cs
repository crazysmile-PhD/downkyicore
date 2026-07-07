using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video.Models;

public class VideoPage : BaseModel
{
    [JsonProperty("cid")] public long Cid { get; set; }
    [JsonProperty("page")] public int Page { get; set; }
    [JsonProperty("from")] public string From { get; set; } = string.Empty;
    [JsonProperty("part")] public string Part { get; set; } = string.Empty;
    [JsonProperty("duration")] public long Duration { get; set; }
    [JsonProperty("vid")] public string Vid { get; set; } = string.Empty;
    [JsonProperty("weblink")] public string Weblink { get; set; } = string.Empty;
    [JsonProperty("dimension")] public Dimension Dimension { get; set; } = new();
    [JsonProperty("first_frame")] public string FirstFrame { get; set; } = string.Empty;
}
