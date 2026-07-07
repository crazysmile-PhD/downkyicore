using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceCheese : BaseModel
{
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("ep_count")] public int EpCount { get; set; }
    [JsonProperty("link")] public string Link { get; set; } = string.Empty;
    [JsonProperty("page")] public int Page { get; set; }
    [JsonProperty("play")] public int Play { get; set; }
    [JsonProperty("season_id")] public long SeasonId { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = string.Empty;
    [JsonProperty("subtitle")] public string SubTitle { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
}
