using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;

namespace DownKyi.ViewModels.PageViewModels;

internal class VideoPage : BindableBase
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

    public VideoPage CloneForCache()
    {
        var qualityList = VideoQualityList.Select(quality => quality.CloneForCache()).ToList();
        var selectedQuality = qualityList.FirstOrDefault(quality =>
            quality.Quality == VideoQuality.Quality &&
            quality.QualityFormat == VideoQuality.QualityFormat) ?? VideoQuality.CloneForCache();

        return new VideoPage
        {
            PlayUrl = PlayUrl,
            Avid = Avid,
            Bvid = Bvid,
            Cid = Cid,
            EpisodeId = EpisodeId,
            Owner = Owner,
            PublishTime = PublishTime,
            OriginalPublishTime = OriginalPublishTime,
            FirstFrame = FirstFrame,
            Page = Page,
            IsSelected = IsSelected,
            Order = Order,
            Name = Name,
            Duration = Duration,
            AudioQualityFormatList = new ObservableCollection<string>(AudioQualityFormatList),
            AudioQualityFormat = AudioQualityFormat,
            VideoQualityList = qualityList,
            VideoQuality = selectedQuality,
            LazyTags = new Lazy<List<string>>(() =>
                LazyTags.IsValueCreated ? LazyTags.Value.ToList() : new List<string>())
        };
    }

    #region

    // 视频画质选择事件
    private DelegateCommand? _videoQualitySelectedCommand;

    public DelegateCommand VideoQualitySelectedCommand => _videoQualitySelectedCommand ??= new DelegateCommand(ExecuteVideoQualitySelectedCommand);

    /// <summary>
    /// 视频画质选择事件
    /// </summary>
    private void ExecuteVideoQualitySelectedCommand()
    {
        // 杜比视界
        // string dolby = string.Empty;
        // try
        // {
        //     var qualities = Constant.GetAudioQualities();
        //     dolby = qualities[3].Name;
        // }
        // catch (Exception e)
        // {
        //     Console.PrintLine("ExecuteVideoQualitySelectedCommand()发生异常: {0}", e);
        //     LogManager.Error("ExecuteVideoQualitySelectedCommand", e);
        // }
        //
        // if (VideoQuality != null && VideoQuality.Quality == 126 && PlayUrl != null && PlayUrl.Dash != null &&
        //     PlayUrl.Dash.Dolby != null)
        // {
        //     ListHelper.AddUnique(AudioQualityFormatList, dolby);
        //     AudioQualityFormat = dolby;
        // }
        // else
        // {
        //     if (AudioQualityFormatList != null && AudioQualityFormatList.Contains(dolby))
        //     {
        //         AudioQualityFormatList.Remove(dolby);
        //         AudioQualityFormat = AudioQualityFormatList[0];
        //     }
        // }
    }

    #endregion
}
