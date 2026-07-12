using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Cheese.Models;

// https://api.bilibili.com/pugv/view/web/season
public class CheeseViewOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    [JsonProperty("data")] public CheeseView Data { get; set; } = new();
}

public class CheeseView : BaseModel
{
    // active_market
    // activity_list
    [JsonProperty("brief")] public CheeseBrief Brief { get; set; } = new();

    // cooperation
    // coupon
    // course_content
    // courses
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;

    // ep_catalogue
    // ep_count
    // episode_page
    // episode_sort
    // episode_tag
    [JsonProperty("episodes")] public IReadOnlyList<CheeseEpisode> Episodes { get; set; } = Array.Empty<CheeseEpisode>();

    // faq
    // faq1
    // live_ep_count
    // opened_ep_count
    // payment
    // previewed_purchase_note
    // purchase_format_note
    // purchase_note
    // purchase_protocol
    // recommend_seasons 推荐课程
    // release_bottom_info
    // release_info
    // release_info2
    // release_status
    [JsonProperty("season_id")] public long SeasonId { get; set; }

    [JsonProperty("share_url")] public string ShareAddress { get; set; } = string.Empty;

    // short_link
    [JsonProperty("stat")] public CheeseStat Stat { get; set; } = new();

    // status
    [JsonProperty("subtitle")] public string Subtitle { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    [JsonProperty("up_info")] public CheeseUpInfo UpInfo { get; set; } = new();
    // update_status
    // user_status
}
