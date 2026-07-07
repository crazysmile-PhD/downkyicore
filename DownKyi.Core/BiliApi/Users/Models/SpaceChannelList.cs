using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceChannelList : BaseModel
{
    [JsonProperty("cid")] public long Cid { get; set; }
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("intro")] public string Intro { get; set; } = string.Empty;
    [JsonProperty("mtime")] public long Mtime { get; set; }
    [JsonProperty("count")] public int Count { get; set; }

    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    // is_live_playback
}
