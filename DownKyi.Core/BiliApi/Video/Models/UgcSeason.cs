using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video.Models;

public class UgcSeason : BaseModel
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("intro")] public string Intro { get; set; } = string.Empty;
    [JsonProperty("sign_state")] public int SignState { get; set; }
    [JsonProperty("attribute")] public int Attribute { get; set; }
    [JsonProperty("sections")] public IReadOnlyList<UgcSection>? Sections { get; set; }
    [JsonProperty("stat")] public UgcStat Stat { get; set; } = new();
    [JsonProperty("ep_count")] public int EpCount { get; set; }
    [JsonProperty("season_type")] public int SeasonType { get; set; }
}
