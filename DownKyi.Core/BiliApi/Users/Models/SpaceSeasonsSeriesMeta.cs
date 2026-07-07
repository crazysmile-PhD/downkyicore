using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSeasonsSeriesMeta : BaseModel
{
    [JsonProperty("category")] public int Category { get; set; }
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("description")] public string Description { get; set; } = string.Empty;
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("total")] public int Total { get; set; }
}

public class SpaceSeasonsMeta : SpaceSeasonsSeriesMeta
{
    [JsonProperty("ptime")] public long Ptime { get; set; }
    [JsonProperty("season_id")] public long SeasonId { get; set; }
}

public class SpaceSeriesMeta : SpaceSeasonsSeriesMeta
{
    [JsonProperty("creator")] public string Creator { get; set; } = string.Empty;
    [JsonProperty("ctime")] public long Ctime { get; set; }
    [JsonProperty("keywords")] public List<string> Keywords { get; set; } = new();
    [JsonProperty("last_update_ts")] public long LastUpdate { get; set; }
    [JsonProperty("mtime")] public long Mtime { get; set; }
    [JsonProperty("raw_keywords")] public string RawKeywords { get; set; } = string.Empty;
    [JsonProperty("series_id")] public long SeriesId { get; set; }
    [JsonProperty("state")] public int State { get; set; }
}
