using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class BangumiFollow : BaseModel
{
    [JsonProperty("season_id")] public long SeasonId { get; set; }
    [JsonProperty("media_id")] public long MediaId { get; set; }
    [JsonProperty("season_type")] public int SeasonType { get; set; }
    [JsonProperty("season_type_name")] public string SeasonTypeName { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;

    [JsonProperty("total_count")] public int TotalCount { get; set; }

    // is_finish
    // is_started
    // is_play
    [JsonProperty("badge")] public string Badge { get; set; } = string.Empty;

    [JsonProperty("badge_type")] public int BadgeType { get; set; }

    // rights
    // stat
    [JsonProperty("new_ep")] public BangumiFollowNewEp NewEp { get; set; } = new();

    // rating
    // square_cover
    [JsonProperty("season_status")] public int SeasonStatus { get; set; }
    [JsonProperty("season_title")] public string SeasonTitle { get; set; } = string.Empty;

    [JsonProperty("badge_ep")] public string BadgeEp { get; set; } = string.Empty;

    // media_attr
    // season_attr
    [JsonProperty("evaluate")] public string Evaluate { get; set; } = string.Empty;
    [JsonProperty("areas")] public IReadOnlyList<BangumiFollowAreas> Areas { get; set; } = Array.Empty<BangumiFollowAreas>();
    [JsonProperty("subtitle")] public string Subtitle { get; set; } = string.Empty;

    [JsonProperty("first_ep")] public long FirstEp { get; set; }

    // can_watch
    // series
    // publish
    // mode
    // section
    [JsonProperty("url")] public string Address { get; set; } = string.Empty;

    // badge_info
    // first_ep_info
    // formal_ep_count
    // short_url
    // badge_infos
    // season_version
    // horizontal_cover_16_9
    // horizontal_cover_16_10
    // subtitle_14
    // viewable_crowd_type
    // producers
    // follow_status
    // is_new
    [JsonProperty("progress")] public string Progress { get; set; } = string.Empty;
    // both_follow
}
