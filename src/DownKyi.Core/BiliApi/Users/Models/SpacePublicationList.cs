using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpacePublicationList : BaseModel
{
    [JsonProperty("tlist")]
    public IReadOnlyDictionary<string, SpacePublicationListTypeVideoZone> Tlist { get; set; }
        = new Dictionary<string, SpacePublicationListTypeVideoZone>();

    [JsonProperty("vlist")] public IReadOnlyList<SpacePublicationListVideo>? Vlist { get; set; }
}
