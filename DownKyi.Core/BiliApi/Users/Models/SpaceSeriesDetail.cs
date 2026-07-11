using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

// https://api.bilibili.com/x/series/archives?mid={mid}&series_id={seriesId}&only_normal=true&sort=desc&pn={pn}&ps={ps}
public class SpaceSeriesDetailOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public SpaceSeriesDetail Data { get; set; } = new();
}

public class SpaceSeriesDetail : BaseModel
{
    [JsonProperty("aids")] public IReadOnlyList<long> Aids { get; set; } = Array.Empty<long>();

    // page
    [JsonProperty("archives")] public IReadOnlyList<SpaceSeasonsSeriesArchives> Archives { get; set; } = Array.Empty<SpaceSeasonsSeriesArchives>();
}
