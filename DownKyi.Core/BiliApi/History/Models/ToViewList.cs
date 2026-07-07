using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.History.Models
{
    public class ToViewList : BaseModel
    {
        [JsonProperty("aid")]
        public long Aid { get; set; }
        // videos
        // tid
        // tname
        // copyright
        [JsonProperty("pic")]
        public string Pic { get; set; } = string.Empty;
        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;
        // pubdate
        // ctime
        // desc
        // state
        // duration
        // rights
        [JsonProperty("owner")]
        public VideoOwner Owner { get; set; } = new();
        // stat
        // dynamic
        // dimension
        // short_link_v2
        // first_frame
        // page
        // count
        [JsonProperty("cid")]
        public long Cid { get; set; }
        // progress
        [JsonProperty("add_at")]
        public long AddAt { get; set; }
        [JsonProperty("bvid")]
        public string Bvid { get; set; } = string.Empty;
        // uri
        // viewed
    }
}
