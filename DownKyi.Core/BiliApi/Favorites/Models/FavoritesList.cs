using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites.Models;

// https://api.bilibili.com/x/v3/fav/folder/collected/list
public class FavoritesListOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public FavoritesList Data { get; set; } = new();
}

public class FavoritesList : BaseModel
{
    [JsonProperty("count")] public int Count { get; set; }

    [JsonProperty("list")] public List<FavoritesMetaInfo> List { get; set; } = new();
    //[JsonProperty("has_more")]
    //public bool HasMore { get; set; }
}
