using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Models.Json;

public class Subtitle : BaseModel
{
    [JsonProperty("from")] public decimal From { get; set; }
    [JsonProperty("to")] public decimal To { get; set; }
    [JsonProperty("location")] public int Location { get; set; }
    [JsonProperty("content")] public string Content { get; set; } = string.Empty;
}
