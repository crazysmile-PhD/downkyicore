using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSettingsToutu : BaseModel
{
    [JsonProperty("sid")] public int Sid { get; set; }
    [JsonProperty("expire")] public long Expire { get; set; }
    [JsonProperty("s_img")] public string Simg { get; set; } = string.Empty; // 完整url为http://i0.hdslb.com/+相对路径
    [JsonProperty("l_img")] public string Limg { get; set; } = string.Empty; // 完整url为http://i0.hdslb.com/+相对路径
    [JsonProperty("android_img")] public string AndroidImg { get; set; } = string.Empty;
    [JsonProperty("iphone_img")] public string IphoneImg { get; set; } = string.Empty;
    [JsonProperty("ipad_img")] public string IpadImg { get; set; } = string.Empty;
    [JsonProperty("thumbnail_img")] public string ThumbnailImg { get; set; } = string.Empty;
    [JsonProperty("platform")] public int Platform { get; set; }
}
