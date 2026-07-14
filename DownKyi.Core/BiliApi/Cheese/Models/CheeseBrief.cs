using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese.Models;

public class CheeseBrief : BaseModel
{
    // content
    [JsonProperty("img")] public IReadOnlyList<CheeseImg> Img { get; set; } = Array.Empty<CheeseImg>();
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("type")] public int Type { get; set; }
}
