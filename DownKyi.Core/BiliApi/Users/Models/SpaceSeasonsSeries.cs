using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSeasons : BaseModel
{
    [JsonProperty("archives")] public List<SpaceSeasonsSeriesArchives> Archives { get; set; } = new();
    [JsonProperty("meta")] public SpaceSeasonsMeta Meta { get; set; } = new();
    [JsonProperty("recent_aids")] public List<long> RecentAids { get; set; } = new();
}

public class SpaceSeries : BaseModel
{
    [JsonProperty("archives")] public List<SpaceSeasonsSeriesArchives> Archives { get; set; } = new();
    [JsonProperty("meta")] public SpaceSeriesMeta Meta { get; set; } = new();
    [JsonProperty("recent_aids")] public List<long> RecentAids { get; set; } = new();
}
