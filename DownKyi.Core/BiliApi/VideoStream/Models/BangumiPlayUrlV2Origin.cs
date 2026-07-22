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
    [JsonProperty("video_info")]
    public PlayUrl? VideoInfo { get; set; }
}
