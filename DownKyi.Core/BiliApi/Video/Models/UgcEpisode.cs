using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video.Models;

public class UgcEpisode : BaseModel
{
    [JsonProperty("season_id")] public long SeasonId { get; set; }
    [JsonProperty("section_id")] public long SectionId { get; set; }
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("aid")] public long Aid { get; set; }
    [JsonProperty("cid")] public long Cid { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("attribute")] public int Attribute { get; set; }
    [JsonProperty("arc")] public UgcArc Arc { get; set; } = new();
    [JsonProperty("page")] public VideoPage Page { get; set; } = new();

    [JsonProperty("pages")] public List<VideoPage> Pages { get; set; } = new();
    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
}
