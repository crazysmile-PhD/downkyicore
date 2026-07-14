using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Models.Json;

public class BiliTagInfo
{
    [JsonProperty("tag_id")]
    public long TagId { get; set; }

    [JsonProperty("tag_name")]
    public string TagName { get; set; } = string.Empty;
}

public class TagResult
{
    [JsonProperty("data")]
    public IReadOnlyList<BiliTagInfo> Data { get; set; } = Array.Empty<BiliTagInfo>();
}
