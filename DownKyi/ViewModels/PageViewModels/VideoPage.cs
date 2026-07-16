using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.PageViewModels;

internal class VideoPage : ObservableObject
{
    public PlayUrl? PlayUrl { get; set; }

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

    private ObservableCollection<string> audioQualityFormatList = new();

    public ObservableCollection<string> AudioQualityFormatList
    {
        get => audioQualityFormatList;
        internal set => SetProperty(ref audioQualityFormatList, value);
    }

    private string audioQualityFormat = string.Empty;

    public string AudioQualityFormat
    {
        get => audioQualityFormat;
        set
        {
            if (value != null)
            {
                SetProperty(ref audioQualityFormat, value);
            }
        }
    }

    private IList<VideoQuality> videoQualityList = new List<VideoQuality>();

    public IList<VideoQuality> VideoQualityList
    {
        get => videoQualityList;
        internal set => SetProperty(ref videoQualityList, value);
    }

    private VideoQuality videoQuality = new();

    public VideoQuality VideoQuality
    {
        get => videoQuality;
        set => SetProperty(ref videoQuality, value);
    }

    [JsonIgnore]
    public Lazy<List<string>> LazyTags { get; set; } = new(() => new());
    #region

    // 视频画质选择事件
    private RelayCommand? _videoQualitySelectedCommand;

    public RelayCommand VideoQualitySelectedCommand => _videoQualitySelectedCommand ??= new RelayCommand(ExecuteVideoQualitySelectedCommand);

    /// <summary>
    /// 视频画质选择事件
    /// </summary>
    private void ExecuteVideoQualitySelectedCommand()
    {
    }

    #endregion
}
