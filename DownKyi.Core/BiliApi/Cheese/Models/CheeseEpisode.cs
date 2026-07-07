using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese.Models;

public class CheeseEpisode : BaseModel
{
    [JsonProperty("aid")] public long Aid { get; set; }
    [JsonProperty("catalogue_index")] public int CatalogueIndex { get; set; }
    [JsonProperty("cid")] public long Cid { get; set; }
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("duration")] public long Duration { get; set; }
    [JsonProperty("from")] public string From { get; set; } = string.Empty;
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("index")] public int Index { get; set; }
    [JsonProperty("page")] public int Page { get; set; }
    [JsonProperty("play")] public long Play { get; set; }
    [JsonProperty("play_way")] public int PlayWay { get; set; }
    [JsonProperty("play_way_format")] public string PlayWayFormat { get; set; } = string.Empty;
    [JsonProperty("release_date")] public long ReleaseDate { get; set; }
    [JsonProperty("status")] public int Status { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("watched")] public bool Watched { get; set; }
    [JsonProperty("watchedHistory")] public int WatchedHistory { get; set; }
}
