using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Bangumi.Models;

// https://api.bilibili.com/pgc/view/web/season
public class BangumiSeasonOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("result")] public BangumiSeason Result { get; set; } = new();
}

public class BangumiSeason : BaseModel
{
    // activity
    // alias
    [JsonProperty("areas")] public List<BangumiArea> Areas { get; set; } = new();
    [JsonProperty("bkg_cover")] public string BkgCover { get; set; } = string.Empty;
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("episodes")] public List<BangumiEpisode> Episodes { get; set; } = new();

    [JsonProperty("evaluate")] public string Evaluate { get; set; } = string.Empty;

    // freya
    // jp_title
    [JsonProperty("link")] public string Link { get; set; } = string.Empty;

    [JsonProperty("media_id")] public long MediaId { get; set; }

    // mode
    // new_ep
    // payment
    [JsonProperty("positive")] public BangumiPositive Positive { get; set; } = new();

    // publish

    [JsonProperty("rating")] public BangumiRating? Rating { get; set; }


    [JsonProperty("styles")] public string[] Styles { get; set; }

    // record
    // rights
    [JsonProperty("season_id")] public long SeasonId { get; set; }
    [JsonProperty("season_title")] public string SeasonTitle { get; set; } = string.Empty;
    [JsonProperty("seasons")] public List<BangumiSeasonInfo> Seasons { get; set; } = new();

    [JsonProperty("section")] public List<BangumiSection> Section { get; set; } = new();

    // series
    // share_copy
    // share_sub_title
    // share_url
    // show
    [JsonProperty("square_cover")] public string SquareCover { get; set; } = string.Empty;

    [JsonProperty("stat")] public BangumiStat Stat { get; set; } = new();

    // status
    [JsonProperty("subtitle")] public string Subtitle { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("up_info")] public BangumiUpInfo? UpInfo { get; set; }
}
