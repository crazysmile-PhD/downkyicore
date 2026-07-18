using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class UserVideoListOrigin : BaseModel
{
    [JsonProperty("data")] public UserVideoListData? Data { get; set; }
}

public class UserVideoListData : BaseModel
{
    [JsonProperty("archives")] public IReadOnlyList<UserVideoListArchive> Archives { get; set; } = Array.Empty<UserVideoListArchive>();
    [JsonProperty("page")] public SpacePublicationPage Page { get; set; } = new();
}

public class UserVideoListArchive : BaseModel
{
    [JsonProperty("aid")] public long Aid { get; set; }
    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
    [JsonProperty("pic")] public string Pic { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("duration")] public long Duration { get; set; }
    [JsonProperty("pubdate")] public long Pubdate { get; set; }
    [JsonProperty("attr")] public int Attr { get; set; }
    [JsonProperty("author")] public UserVideoListAuthor Author { get; set; } = new();
    [JsonProperty("stat")] public SpaceSeasonsSeriesStat Stat { get; set; } = new();
}

public class UserVideoListAuthor : BaseModel
{
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("face")] public string Face { get; set; } = string.Empty;
}
