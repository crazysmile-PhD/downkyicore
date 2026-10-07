using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public sealed class PlayUrlOrigin : BaseModel
{
    //[JsonProperty("code")]
    //public int Code { get; set; }
    //[JsonProperty("message")]
    //public string Message { get; set; } = string.Empty;
    //[JsonProperty("ttl")]
    //public int Ttl { get; set; }
    [JsonProperty("data")] public PlayUrl? Data { get; set; }
    [JsonProperty("result")] public PlayUrl? Result { get; set; }
}

public class PlayUrl : BaseModel
{
    // from
    // result
    // message
    // quality
    // format
    // timelength
    [JsonProperty("is_preview")] public bool? IsPreview { get; set; }
    // accept_format
    [JsonProperty("accept_description")] public IReadOnlyList<string> AcceptDescription { get; set; } = Array.Empty<string>();

    [JsonProperty("accept_quality")] public IReadOnlyList<int> AcceptQuality { get; set; } = Array.Empty<int>();

    // video_codecid
    // seek_param
    // seek_type
    [JsonProperty("durl")] public IReadOnlyList<PlayUrlDurl> Durl { get; set; } = Array.Empty<PlayUrlDurl>();
    [JsonProperty("dash")] public PlayUrlDash Dash { get; set; } = new();

    [JsonProperty("quality")] public int Quality { get; set; }

    [JsonProperty("video_codecid")] public int VideoCodecid { get; set; }

    [JsonProperty("support_formats")] public IReadOnlyList<PlayUrlSupportFormat> SupportFormats { get; set; } = Array.Empty<PlayUrlSupportFormat>();

    [JsonIgnore]
    public PlayUrlDiagnostics? Diagnostics { get; internal set; }

    [JsonIgnore]
    public PlayUrlAvailability? Availability { get; internal set; }
    // high_format
}

public sealed record PlayUrlDiagnostics(
    int RequestedQuality,
    int Fnval,
    string? PlayDetail,
    bool UsedWebPageFallback,
    string FallbackOutcome);
