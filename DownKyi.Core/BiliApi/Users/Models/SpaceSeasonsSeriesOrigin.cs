using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

// https://api.bilibili.com/x/space/channel/video?mid={mid}&page_num={pageNum}&page_size={pageSize}
public class SpaceSeasonsSeriesOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public SpaceSeasonsSeriesData Data { get; set; } = new();
}

public class SpaceSeasonsSeriesData : BaseModel
{
    [JsonProperty("items_lists")] public SpaceSeasonsSeries ItemsLists { get; set; } = new();
}

public class SpaceSeasonsSeries : BaseModel
{
    [JsonProperty("page")] public SpaceSeasonsSeriesPage Page { get; set; } = new();
    [JsonProperty("seasons_list")] public IReadOnlyList<SpaceSeasons> SeasonsList { get; set; } = Array.Empty<SpaceSeasons>();
    [JsonProperty("series_list")] public IReadOnlyList<SpaceSeries> SeriesList { get; set; } = Array.Empty<SpaceSeries>();
}
