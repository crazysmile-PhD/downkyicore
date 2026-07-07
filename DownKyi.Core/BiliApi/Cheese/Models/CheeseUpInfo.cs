using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese.Models;

public class CheeseUpInfo : BaseModel
{
    [JsonProperty("avatar")] public string Avatar { get; set; } = string.Empty;
    [JsonProperty("brief")] public string Brief { get; set; } = string.Empty;
    [JsonProperty("follower")] public long Follower { get; set; }
    [JsonProperty("is_follow")] public int IsFollow { get; set; }
    [JsonProperty("link")] public string Link { get; set; } = string.Empty;
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("uname")] public string Name { get; set; } = string.Empty;
}
