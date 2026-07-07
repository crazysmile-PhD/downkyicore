using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class RelationFollowInfo : BaseModel
{
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("attribute")] public int Attribute { get; set; }
    [JsonProperty("mtime")] public long Mtime { get; set; }
    [JsonProperty("tag")] public List<long> Tag { get; set; } = new();

    [JsonProperty("special")] public int Special { get; set; }

    // contract_info
    [JsonProperty("uname")] public string Name { get; set; } = string.Empty;
    [JsonProperty("face")] public string Face { get; set; } = string.Empty;

    [JsonProperty("sign")] public string Sign { get; set; } = string.Empty;
    // face_nft
    // official_verify
    // vip
    // nft_icon
}
