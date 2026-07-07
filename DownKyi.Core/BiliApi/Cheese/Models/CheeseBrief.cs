using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese.Models;

public class CheeseBrief : BaseModel
{
    // content
    [JsonProperty("img")] public List<CheeseImg> Img { get; set; } = new();
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("type")] public int Type { get; set; }
}
