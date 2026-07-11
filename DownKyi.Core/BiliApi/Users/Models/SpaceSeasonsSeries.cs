using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSeasons : BaseModel
{
    [JsonProperty("archives")] public IReadOnlyList<SpaceSeasonsSeriesArchives> Archives { get; set; } = Array.Empty<SpaceSeasonsSeriesArchives>();
    [JsonProperty("meta")] public SpaceSeasonsMeta Meta { get; set; } = new();
    [JsonProperty("recent_aids")] public IReadOnlyList<long> RecentAids { get; set; } = Array.Empty<long>();
}

public class SpaceSeries : BaseModel
{
    [JsonProperty("archives")] public IReadOnlyList<SpaceSeasonsSeriesArchives> Archives { get; set; } = Array.Empty<SpaceSeasonsSeriesArchives>();
    [JsonProperty("meta")] public SpaceSeriesMeta Meta { get; set; } = new();
    [JsonProperty("recent_aids")] public IReadOnlyList<long> RecentAids { get; set; } = Array.Empty<long>();
}
