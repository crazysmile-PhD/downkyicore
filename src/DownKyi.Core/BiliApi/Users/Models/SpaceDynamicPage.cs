using System.Collections.ObjectModel;
using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public sealed class SpaceDynamicOrigin : BaseModel
{
    [JsonProperty("data")] public SpaceDynamicPage? Data { get; set; }
}

public sealed class SpaceDynamicPage : BaseModel
{
    [JsonProperty("has_more")] public bool HasMore { get; set; }
    [JsonProperty("offset")] public string Offset { get; set; } = string.Empty;
    [JsonProperty("items")] public Collection<SpaceDynamicItem> Items { get; } = [];
}

public sealed class SpaceDynamicItem : BaseModel
{
    [JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [JsonProperty("modules")] public SpaceDynamicModules? Modules { get; set; }
}

public sealed class SpaceDynamicModules : BaseModel
{
    [JsonProperty("module_author")] public SpaceDynamicAuthor? Author { get; set; }
    [JsonProperty("module_dynamic")] public SpaceDynamicContent? Dynamic { get; set; }
}

public sealed class SpaceDynamicAuthor : BaseModel
{
    [JsonProperty("mid")] public long Mid { get; set; }
    [JsonProperty("pub_ts")] public long PublishedAt { get; set; }
}

public sealed class SpaceDynamicContent : BaseModel
{
    [JsonProperty("major")] public SpaceDynamicMajor? Major { get; set; }
}

public sealed class SpaceDynamicMajor : BaseModel
{
    [JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [JsonProperty("archive")] public SpaceDynamicArchive? Archive { get; set; }
}

public sealed class SpaceDynamicArchive : BaseModel
{
    [JsonProperty("aid")] public long Aid { get; set; }
    [JsonProperty("bvid")] public string Bvid { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;
    [JsonProperty("duration_text")] public string Duration { get; set; } = string.Empty;
    [JsonProperty("stat")] public SpaceDynamicArchiveStat? Stat { get; set; }
}

public sealed class SpaceDynamicArchiveStat : BaseModel
{
    [JsonProperty("play")] public string Play { get; set; } = string.Empty;
}
