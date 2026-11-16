using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSeriesMetaData : BaseModel
{
    [JsonProperty("meta")] public SpaceSeriesMeta Meta { get; set; } = new();
    [JsonProperty("recent_aids")] public IReadOnlyList<long> RecentAids { get; set; } = Array.Empty<long>();
}
