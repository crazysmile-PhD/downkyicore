using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese.Models;

// https://api.bilibili.com/pugv/view/web/ep/list
public class CheeseEpisodeListOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    [JsonProperty("data")] public CheeseEpisodeList Data { get; set; } = new();
}

public class CheeseEpisodeList : BaseModel
{
    [JsonProperty("items")] public List<CheeseEpisode> Items { get; set; } = new();
    [JsonProperty("page")] public CheeseEpisodePage Page { get; set; } = new();
}
