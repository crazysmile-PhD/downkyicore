using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public sealed class BangumiPlayUrlV2Origin : BaseModel
{
    [JsonProperty("result")]
    public BangumiPlayUrlV2Result? Result { get; set; }
}

public sealed class BangumiPlayUrlV2Result : BaseModel
{
    [JsonProperty("play_check")]
    public BangumiPlayUrlV2PlayCheck? PlayCheck { get; set; }

    [JsonProperty("play_video_type")]
    public string? PlayVideoType { get; set; }

    [JsonProperty("video_info")]
    public PlayUrl? VideoInfo { get; set; }
}

public sealed class BangumiPlayUrlV2PlayCheck : BaseModel
{
    [JsonProperty("play_detail")]
    public string? PlayDetail { get; set; }
}
