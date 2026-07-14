using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class UserInfoVip : BaseModel
{
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("status")] public int Status { get; set; }

    [JsonProperty("due_date")] public long DueDate { get; set; }

    // vip_pay_type
    // theme_type
    [JsonProperty("label")] public UserInfoVipLabel? Label { get; set; }
    [JsonProperty("avatar_subscript")] public int AvatarSubscript { get; set; }

    [JsonProperty("nickname_color")] public string NicknameColor { get; set; } = string.Empty;

    // role
    [JsonProperty("avatar_subscript_url")] public string AvatarSubscriptAddress { get; set; } = string.Empty;
}

public class UserInfoVipLabel : BaseModel
{
    // path
    [JsonProperty("text")] public string Text { get; set; } = string.Empty;
    [JsonProperty("label_theme")] public string LabelTheme { get; set; } = string.Empty;

    [JsonProperty("text_color")] public string TextColor { get; set; } = string.Empty;
    // bg_style
    // bg_color
    // border_color
}
