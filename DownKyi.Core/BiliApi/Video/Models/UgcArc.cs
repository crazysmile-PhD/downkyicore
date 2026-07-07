using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video.Models;

public class UgcArc : BaseModel
{
    [JsonProperty("aid")] public long Aid { get; set; }
    [JsonProperty("videos")] public int Videos { get; set; }
    [JsonProperty("type_id")] public int TypeId { get; set; }
    [JsonProperty("type_name")] public string TypeName { get; set; } = string.Empty;
    [JsonProperty("copyright")] public int Copyright { get; set; }
    [JsonProperty("pic")] public string Pic { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("pubdate")] public long Pubdate { get; set; }
    [JsonProperty("ctime")] public long Ctime { get; set; }
    [JsonProperty("desc")] public string Desc { get; set; } = string.Empty;
    [JsonProperty("state")] public int State { get; set; }

    [JsonProperty("duration")] public long Duration { get; set; }

    //[JsonProperty("rights")]
    //public VideoRights Rights { get; set; } = new();
    [JsonProperty("author")] public VideoOwner Author { get; set; } = new();
    [JsonProperty("stat")] public VideoStat Stat { get; set; } = new();
    [JsonProperty("dynamic")] public string Dynamic { get; set; } = string.Empty;
    [JsonProperty("dimension")] public Dimension Dimension { get; set; } = new();
}
