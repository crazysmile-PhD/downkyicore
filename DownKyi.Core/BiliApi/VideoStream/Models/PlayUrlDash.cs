using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlDash : BaseModel
{
    // Bilibili uses null, an empty array, and an omitted property interchangeably
    // when one media kind is absent. Keep that wire variation at this DTO boundary.
    private IReadOnlyList<PlayUrlDashVideo> _video = Array.Empty<PlayUrlDashVideo>();
    private IReadOnlyList<PlayUrlDashVideo> _audio = Array.Empty<PlayUrlDashVideo>();

    [JsonProperty("duration")] public long Duration { get; set; }

    //[JsonProperty("minBufferTime")]
    //public float minBufferTime { get; set; }
    //[JsonProperty("min_buffer_time")]
    //public float min_buffer_time { get; set; }
    [JsonProperty("video")]
    public IReadOnlyList<PlayUrlDashVideo> Video
    {
        get => _video;
        set => _video = value ?? Array.Empty<PlayUrlDashVideo>();
    }

    [JsonProperty("audio")]
    public IReadOnlyList<PlayUrlDashVideo> Audio
    {
        get => _audio;
        set => _audio = value ?? Array.Empty<PlayUrlDashVideo>();
    }
    [JsonProperty("dolby")] public PlayUrlDashDolby? Dolby { get; set; }
    [JsonProperty("flac")] public PlayUrlDashFlac? Flac { get; set; }
}
