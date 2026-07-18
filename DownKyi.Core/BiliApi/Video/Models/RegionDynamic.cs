using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video.Models;

// https://api.bilibili.com/x/web-interface/dynamic/region
public class RegionDynamicOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public RegionDynamic? Data { get; set; }
}

public class RegionDynamic : BaseModel
{
    [JsonProperty("archives")] public IReadOnlyList<DynamicVideoView> Archives { get; set; } = Array.Empty<DynamicVideoView>();
    // page
}
