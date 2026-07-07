using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.History.Models
{
    public class HistoryList : BaseModel
    {
        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;
        // long_title
        [JsonProperty("cover")]
        public string Cover { get; set; } = string.Empty;
        // covers
        [JsonProperty("uri")]
        public string Uri { get; set; } = string.Empty;
        [JsonProperty("history")]
        public HistoryListHistory History { get; set; } = new();
        [JsonProperty("videos")]
        public int Videos { get; set; }
        [JsonProperty("author_name")]
        public string AuthorName { get; set; } = string.Empty;
        [JsonProperty("author_face")]
        public string AuthorFace { get; set; } = string.Empty;
        [JsonProperty("author_mid")]
        public long AuthorMid { get; set; }
        [JsonProperty("view_at")]
        public long ViewAt { get; set; }
        [JsonProperty("progress")]
        public long Progress { get; set; }
        // badge
        [JsonProperty("show_title")]
        public string ShowTitle { get; set; } = string.Empty;
        [JsonProperty("duration")]
        public long Duration { get; set; }
        // current
        // total
        [JsonProperty("new_desc")]
        public string NewDesc { get; set; } = string.Empty;
        // is_finish
        // is_fav
        // kid
        [JsonProperty("tag_name")]
        public string TagName { get; set; } = string.Empty;
        // live_status
    }
}
