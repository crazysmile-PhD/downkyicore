using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Favorites.Models;

public class FavUpper : BaseModel
{
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("face")] public string Face { get; set; } = string.Empty;

    [JsonProperty("followed")] public bool Followed { get; set; }
    // vip_type
    // vip_statue
}
