using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

// https://api.bilibili.com/x/polymer/space/seasons_archives_list?mid={mid}&season_id={seasonId}&page_num={pageNum}&page_size={pageSize}&sort_reverse=false
public class SpaceSeasonsDetailOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public SpaceSeasonsDetail Data { get; set; } = new();
}

public class SpaceSeasonsDetail : BaseModel
{
    [JsonProperty("aids")] public IReadOnlyList<long> Aids { get; set; } = Array.Empty<long>();
    [JsonProperty("archives")] public IReadOnlyList<SpaceSeasonsSeriesArchives> Archives { get; set; } = Array.Empty<SpaceSeasonsSeriesArchives>();
    [JsonProperty("meta")] public SpaceSeasonsMeta Meta { get; set; } = new();
    [JsonProperty("page")] public SpaceSeasonsSeriesPage Page { get; set; } = new();
}
