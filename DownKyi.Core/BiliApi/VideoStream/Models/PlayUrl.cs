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
    private IReadOnlyList<string> _acceptDescription = Array.Empty<string>();
    private IReadOnlyList<int> _acceptQuality = Array.Empty<int>();
    private IReadOnlyList<PlayUrlDurl> _durl = Array.Empty<PlayUrlDurl>();
    private PlayUrlDash _dash = new();
    private IReadOnlyList<PlayUrlSupportFormat> _supportFormats = Array.Empty<PlayUrlSupportFormat>();

    // from
    // result
    // message
    // quality
    // format
    // timelength
    [JsonProperty("is_preview")] public bool? IsPreview { get; set; }
    // accept_format
    [JsonProperty("accept_description")]
    public IReadOnlyList<string> AcceptDescription
    {
        get => _acceptDescription;
        set => _acceptDescription = value ?? Array.Empty<string>();
    }

    [JsonProperty("accept_quality")]
    public IReadOnlyList<int> AcceptQuality
    {
        get => _acceptQuality;
        set => _acceptQuality = value ?? Array.Empty<int>();
    }

    // video_codecid
    // seek_param
    // seek_type
    [JsonProperty("durl")]
    public IReadOnlyList<PlayUrlDurl> Durl
    {
        get => _durl;
        set => _durl = value ?? Array.Empty<PlayUrlDurl>();
    }

    [JsonProperty("dash")]
    public PlayUrlDash Dash
    {
        get => _dash;
        set => _dash = value ?? new PlayUrlDash();
    }

    [JsonProperty("quality")] public int Quality { get; set; }

    [JsonProperty("video_codecid")] public int VideoCodecid { get; set; }

    [JsonProperty("support_formats")]
    public IReadOnlyList<PlayUrlSupportFormat> SupportFormats
    {
        get => _supportFormats;
        set => _supportFormats = value ?? Array.Empty<PlayUrlSupportFormat>();
    }

    [JsonIgnore]
    public PlayUrlDiagnostics? Diagnostics { get; internal set; }

    [JsonIgnore]
    public PlayUrlAvailability? Availability { get; internal set; }
    // high_format
}

public enum PlayUrlResolutionSource
{
    WebPage,
    Api
}

public sealed record PlayUrlDiagnostics(
    int RequestedQuality,
    int? Fnval,
    string? PlayDetail,
    PlayUrlResolutionSource Source,
    string Outcome);
