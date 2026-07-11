using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites.Models;

public class FavoritesMedia : BaseModel
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("intro")] public string Intro { get; set; } = string.Empty;
    [JsonProperty("page")] public int Page { get; set; }
    [JsonProperty("duration")] public long Duration { get; set; }

    [JsonProperty("upper")] public FavUpper Upper { get; set; } = new();

    // attr
    [JsonProperty("cnt_info")] public MediaStatus CntInfo { get; set; } = new();
    [JsonProperty("link")] public string Link { get; set; } = string.Empty;
    [JsonProperty("ctime")] public long Ctime { get; set; }
    [JsonProperty("pubtime")] public long Pubtime { get; set; }
    [JsonProperty("fav_time")] public long FavTime { get; set; }
    [JsonProperty("bv_id")] public string LegacyBvid { get; set; } = string.Empty;

    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
    // season
    // ogv
    // ugc
}
