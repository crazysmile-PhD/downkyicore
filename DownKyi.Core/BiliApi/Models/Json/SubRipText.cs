using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Models.Json;

public class SubRipText : BaseModel
{
    [JsonProperty("lan")] public string Lan { get; set; } = string.Empty;
    [JsonProperty("lan_doc")] public string LanDoc { get; set; } = string.Empty;
    [JsonProperty("srtString")] public string SrtString { get; set; } = string.Empty;
}
