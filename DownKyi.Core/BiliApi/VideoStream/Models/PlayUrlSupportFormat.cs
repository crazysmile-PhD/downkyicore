using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlSupportFormat : BaseModel
{
    [JsonProperty("quality")] public int Quality { get; set; }
    [JsonProperty("format")] public string Format { get; set; } = string.Empty;
    [JsonProperty("new_description")] public string NewDescription { get; set; } = string.Empty;
    [JsonProperty("display_desc")] public string DisplayDesc { get; set; } = string.Empty;
    [JsonProperty("superscript")] public string Superscript { get; set; } = string.Empty;
}
