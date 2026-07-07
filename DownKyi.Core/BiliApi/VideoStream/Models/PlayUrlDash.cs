using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlDash : BaseModel
{
    [JsonProperty("duration")] public long Duration { get; set; }

    //[JsonProperty("minBufferTime")]
    //public float minBufferTime { get; set; }
    //[JsonProperty("min_buffer_time")]
    //public float min_buffer_time { get; set; }
    [JsonProperty("video")] public List<PlayUrlDashVideo> Video { get; set; } = new();
    [JsonProperty("audio")] public List<PlayUrlDashVideo> Audio { get; set; } = new();
    [JsonProperty("dolby")] public PlayUrlDashDolby Dolby { get; set; } = new();
    [JsonProperty("flac")] public PlayUrlDashFlac Flac { get; set; } = new();
}
