using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public PlayUrl Data { get; set; } = new();
    [JsonProperty("result")] public PlayUrl Result { get; set; } = new();
}

public class PlayUrl : BaseModel
{
    // from
    // result
    // message
    // quality
    // format
    // timelength
    // accept_format
    [JsonProperty("accept_description")] public List<string> AcceptDescription { get; set; } = new();

    [JsonProperty("accept_quality")] public List<int> AcceptQuality { get; set; } = new();

    // video_codecid
    // seek_param
    // seek_type
    [JsonProperty("durl")] public List<PlayUrlDurl> Durl { get; set; } = new();
    [JsonProperty("dash")] public PlayUrlDash Dash { get; set; } = new();

    [JsonProperty("quality")] public int Quality { get; set; }

    [JsonProperty("video_codecid")] public int VideoCodecid { get; set; }

    [JsonProperty("support_formats")] public List<PlayUrlSupportFormat> SupportFormats { get; set; } = new();
    // high_format
}
