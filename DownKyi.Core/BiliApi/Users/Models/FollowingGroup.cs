using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

// https://api.bilibili.com/x/relation/tags
public class FollowingGroupOrigin : BaseModel
{
    [JsonProperty("data")] public IReadOnlyList<FollowingGroup> Data { get; set; } = Array.Empty<FollowingGroup>();
}

public class FollowingGroup : BaseModel
{
    [JsonProperty("tagid")] public int TagId { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("count")] public int Count { get; set; }
    [JsonProperty("tip")] public string Tip { get; set; } = string.Empty;
}
