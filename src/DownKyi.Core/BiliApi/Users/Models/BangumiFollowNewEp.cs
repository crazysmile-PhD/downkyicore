using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class BangumiFollowNewEp : BaseModel
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("index_show")] public string IndexShow { get; set; } = string.Empty;
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("long_title")] public string LongTitle { get; set; } = string.Empty;
    [JsonProperty("pub_time")] public string PubTime { get; set; } = string.Empty;
    [JsonProperty("duration")] public long Duration { get; set; }
}
