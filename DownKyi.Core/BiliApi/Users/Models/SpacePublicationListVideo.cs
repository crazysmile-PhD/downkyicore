using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpacePublicationListVideo : BaseModel
{
    //[JsonProperty("comment")]
    //public int Comment { get; set; }
    [JsonProperty("typeid")] public int Typeid { get; set; }
    [JsonProperty("play")] public int Play { get; set; }

    [JsonProperty("pic")] public string Pic { get; set; } = string.Empty;

    //[JsonProperty("subtitle")]
    //public string Subtitle { get; set; } = string.Empty;
    //[JsonProperty("description")]
    //public string Description { get; set; } = string.Empty;
    //[JsonProperty("copyright")]
    //public string Copyright { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    //[JsonProperty("review")]
    //public int Review { get; set; }
    //[JsonProperty("author")]
    //public string Author { get; set; } = string.Empty;
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("created")] public long Created { get; set; }

    [JsonProperty("length")] public string Length { get; set; } = string.Empty;

    //[JsonProperty("video_review")]
    //public int VideoReview { get; set; }
    [JsonProperty("aid")] public long Aid { get; set; }

    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
    //[JsonProperty("hide_click")]
    //public bool HideClick { get; set; }
    //[JsonProperty("is_pay")]
    //public int IsPay { get; set; }
    //[JsonProperty("is_union_video")]
    //public int IsUnionVideo { get; set; }
    //[JsonProperty("is_steins_gate")]
    //public int IsSteinsGate { get; set; }
    //[JsonProperty("is_live_playback")]
    //public int IsLivePlayback { get; set; }
}
