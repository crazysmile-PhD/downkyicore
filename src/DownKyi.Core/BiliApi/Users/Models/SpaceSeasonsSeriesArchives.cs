using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSeasonsSeriesArchives : BaseModel
{
    [JsonProperty("aid")] public long Aid { get; set; }
    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
    [JsonProperty("ctime")] public long Ctime { get; set; }
    [JsonProperty("duration")] public long Duration { get; set; }
    [JsonProperty("interactive_video")] public bool InteractiveVideo { get; set; }
    [JsonProperty("pic")] public string Pic { get; set; } = string.Empty;
    [JsonProperty("pubdate")] public long Pubdate { get; set; }

    [JsonProperty("stat")] public SpaceSeasonsSeriesStat Stat { get; set; } = new();

    // state
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    // ugc_pay
}
