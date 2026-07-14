using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites.Models;

// https://api.bilibili.com/x/v3/fav/resource/list
public class FavoritesMediaResourceOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public FavoritesMediaResource Data { get; set; } = new();
}

public class FavoritesMediaResource : BaseModel
{
    [JsonProperty("info")] public FavoritesMetaInfo Info { get; set; } = new();
    [JsonProperty("medias")] public IReadOnlyList<FavoritesMedia> Medias { get; set; } = Array.Empty<FavoritesMedia>();
    [JsonProperty("has_more")] public bool HasMore { get; set; }
}
