using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites.Models;

// https://api.bilibili.com/x/v3/fav/resource/ids
public class FavoritesMediaIdOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public List<FavoritesMediaId> Data { get; set; } = new();
}

public class FavoritesMediaId : BaseModel
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("bv_id")] public string BvId { get; set; } = string.Empty;
    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
}
