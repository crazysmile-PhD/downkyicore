using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Presentation;

internal class VideoPage : ObservableObject
{
    public PlayStreamType? PlaybackStreamType { get; set; }

    public string PlaybackAddress { get; set; } = string.Empty;

    public PlayUrlAvailability? PlaybackAvailability { get; internal set; }

    public bool HasPlayback => PlaybackAvailability?.HasPlayableMedia == true;

    public long Avid { get; set; }
    public string Bvid { get; set; } = string.Empty;
    public long Cid { get; set; }
    public long EpisodeId { get; set; }
    public VideoOwner? Owner { get; set; }
    public string PublishTime { get; set; } = string.Empty;

    public DateTime OriginalPublishTime { get; set; }

    public string FirstFrame { get; set; } = string.Empty;

    public int Page { get; set; }

    private bool isSelected;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    private int order;

    public int Order
    {
        get => order;
        set => SetProperty(ref order, value);
    }

    private string name = string.Empty;

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    private string duration = string.Empty;

    public string Duration
    {
        get => duration;
        set => SetProperty(ref duration, value);
    }

    private string audioQualityFormat = string.Empty;

    public string AudioQualityFormat
    {
        get => audioQualityFormat;
        set => SetProperty(ref audioQualityFormat, value ?? audioQualityFormat);
    }

    internal PlaybackQualityMatch? AudioQualityMatch { get; private set; }

    private VideoQuality? videoQuality;

    public VideoQuality? VideoQuality
    {
        get => videoQuality;
        set => SetProperty(ref videoQuality, value);
    }

    internal PlaybackQualityMatch? VideoQualityMatch { get; private set; }

    internal void SetAutomaticAudioQuality(
        string value,
        PlaybackQualityMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        SetProperty(ref audioQualityFormat, value);
        AudioQualityMatch = match;
    }

    internal void SetAutomaticVideoQuality(
        VideoQuality? value,
        PlaybackQualityMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        SetProperty(ref videoQuality, value);
        VideoQualityMatch = match;
    }

    [JsonIgnore]
    public Func<CancellationToken, Task<IReadOnlyList<string>>> LoadTagsAsync { get; set; } =
        static cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        };
}
