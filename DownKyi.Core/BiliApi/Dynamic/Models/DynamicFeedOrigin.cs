using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Dynamic.Models;

public sealed class DynamicFeedOrigin : BaseModel
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("data")]
    public DynamicFeedData? Data { get; set; }
}

public sealed class DynamicFeedData : BaseModel
{
    [JsonProperty("has_more")]
    public bool HasMore { get; set; }

    [JsonProperty("offset")]
    public string Offset { get; set; } = string.Empty;

    [JsonProperty("items")]
    public IReadOnlyList<DynamicFeedItem> Items { get; set; } = Array.Empty<DynamicFeedItem>();
}

public sealed class DynamicFeedItem : BaseModel
{
    [JsonProperty("id_str")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("visible")]
    public bool Visible { get; set; } = true;

    [JsonProperty("modules")]
    public DynamicModules Modules { get; set; } = new();

    [JsonProperty("orig")]
    public DynamicFeedItem? Original { get; set; }
}

public sealed class DynamicModules : BaseModel
{
    [JsonProperty("module_author")]
    public DynamicAuthor Author { get; set; } = new();

    [JsonProperty("module_dynamic")]
    public DynamicContent Content { get; set; } = new();

    [JsonProperty("module_stat")]
    public DynamicStats Stats { get; set; } = new();
}

public sealed class DynamicAuthor : BaseModel
{
    [JsonProperty("mid")]
    public long Mid { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("face")]
    public string Face { get; set; } = string.Empty;

    [JsonProperty("pub_action")]
    public string PublishAction { get; set; } = string.Empty;

    [JsonProperty("pub_time")]
    public string PublishTime { get; set; } = string.Empty;

    [JsonProperty("pub_ts")]
    public long PublishTimestamp { get; set; }
}

public sealed class DynamicContent : BaseModel
{
    [JsonProperty("desc")]
    public DynamicText? Description { get; set; }

    [JsonProperty("major")]
    public DynamicMajor? Major { get; set; }
}

public sealed class DynamicText : BaseModel
{
    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class DynamicMajor : BaseModel
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("archive")]
    public DynamicArchive? Archive { get; set; }

    [JsonProperty("draw")]
    public DynamicDraw? Draw { get; set; }

    [JsonProperty("opus")]
    public DynamicOpus? Opus { get; set; }

    [JsonProperty("article")]
    public DynamicArticle? Article { get; set; }

    [JsonProperty("pgc")]
    public DynamicCommonCard? Pgc { get; set; }

    [JsonProperty("common")]
    public DynamicCommonCard? Common { get; set; }

    [JsonProperty("live")]
    public DynamicCommonCard? Live { get; set; }

    [JsonProperty("none")]
    public DynamicUnavailable? Unavailable { get; set; }
}

public sealed class DynamicArchive : BaseModel
{
    [JsonProperty("bvid")]
    public string Bvid { get; set; } = string.Empty;

    [JsonProperty("cover")]
    public string Cover { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("desc")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("duration_text")]
    public string Duration { get; set; } = string.Empty;

    [JsonProperty("jump_url")]
    public string JumpAddress { get; set; } = string.Empty;
}

public sealed class DynamicDraw : BaseModel
{
    [JsonProperty("items")]
    public IReadOnlyList<DynamicPicture> Items { get; set; } = Array.Empty<DynamicPicture>();
}

public sealed class DynamicOpus : BaseModel
{
    [JsonProperty("jump_url")]
    public string JumpAddress { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("summary")]
    public DynamicText? Summary { get; set; }

    [JsonProperty("pics")]
    public IReadOnlyList<DynamicPicture> Pictures { get; set; } = Array.Empty<DynamicPicture>();
}

public sealed class DynamicPicture : BaseModel
{
    [JsonProperty("src")]
    public string Source { get; set; } = string.Empty;

    [JsonProperty("url")]
    private string AlternateSource
    {
        set
        {
            if (string.IsNullOrEmpty(Source))
            {
                Source = value;
            }
        }
    }

    [JsonProperty("width")]
    public int Width { get; set; }

    [JsonProperty("height")]
    public int Height { get; set; }
}

public sealed class DynamicArticle : BaseModel
{
    [JsonProperty("covers")]
    public IReadOnlyList<string> Covers { get; set; } = Array.Empty<string>();

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("desc")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("jump_url")]
    public string JumpAddress { get; set; } = string.Empty;
}

public sealed class DynamicCommonCard : BaseModel
{
    [JsonProperty("cover")]
    public string Cover { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("desc")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("jump_url")]
    public string JumpAddress { get; set; } = string.Empty;
}

public sealed class DynamicUnavailable : BaseModel
{
    [JsonProperty("tips")]
    public string Tips { get; set; } = string.Empty;
}

public sealed class DynamicStats : BaseModel
{
    [JsonProperty("forward")]
    public DynamicStat Forward { get; set; } = new();

    [JsonProperty("comment")]
    public DynamicStat Comment { get; set; } = new();

    [JsonProperty("like")]
    public DynamicStat Like { get; set; } = new();
}

public sealed class DynamicStat : BaseModel
{
    [JsonProperty("count")]
    public long Count { get; set; }
}
