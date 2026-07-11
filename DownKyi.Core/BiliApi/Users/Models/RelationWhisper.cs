using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

// https://api.bilibili.com/x/relation/whispers?pn={pn}&ps={ps}
public class RelationWhisper : BaseModel
{
    [JsonProperty("data")] public RelationWhisperData Data { get; set; } = new();
}

public class RelationWhisperData : BaseModel
{
    [JsonProperty("list")] public IReadOnlyList<RelationFollowInfo> List { get; set; } = Array.Empty<RelationFollowInfo>();
    // re_version
}
