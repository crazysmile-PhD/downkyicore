using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.FileName;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Settings;

internal sealed class FfmpegHardwareAccelerationItem
{
    public string Name { get; init; } = string.Empty;
    public FfmpegHardwareAcceleration Value { get; init; }
}

internal partial class ViewVideoViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsVideo";

    private bool _isOnNavigatedTo;
    private readonly IFilePickerService _filePickerService;
    private readonly ILogger<ViewVideoViewModel> _logger;
    private readonly ISettingsStore _settingsStore;

    public ViewVideoViewModel(
        IDesktopInteractionContext desktopInteractions,
        IFilePickerService filePickerService,
        ISettingsStore settingsStore,
        ILogger<ViewVideoViewModel> logger) : base(desktopInteractions)
    {
        _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        // 优先下载的视频编码
        VideoCodecs = PlaybackQualityCatalog.GetCodecIds();

        // 优先下载画质
        VideoQualityList = PlaybackQualityCatalog.GetResolutions();

        // 优先下载音质
        AudioQualityList = PlaybackQualityCatalog.GetAudioQualities();
        AudioQualityList[3].Id += 1000;
        AudioQualityList[4].Id += 1000;

        // 首选视频解析方式
        VideoParseTypeList = new List<VideoParseType>
        {
            new() { Name = "API(解析快、易风控)", Id = 0 },
            new() { Name = "WebPage(解析慢、不易风控)", Id = 1 },
        };

        // 文件命名格式
        SelectedFileName.CollectionChanged += (sender, e) =>
        {
            // 当前显示的命名格式part
            var fileName = SelectedFileName.Select(item => item.Id).ToImmutableArray();
            var updated = UpdateVideo(settings => settings with { FileNameParts = fileName });
            var isSucceed = updated.FileNameParts.SequenceEqual(fileName);
            PublishTip(isSucceed);
        };

        foreach (var item in Enum.GetValues<FileNamePart>())
        {
            var display = DisplayFileNamePart(item);
            OptionalFields.Add(new DisplayFileNamePart { Id = item, Title = display });
        }

        SelectedOptionalField = -1;

        // 文件命名中的时间格式
        FileNamePartTimeFormatList = new List<string>
        {
            "yyyy-MM-dd",
            "yyyy.MM.dd",
        };

        // 文件命名中的序号格式
        OrderFormatList = new List<OrderFormatDisplay>
        {
            new() { Name = DictionaryResource.GetString("OrderFormatNatural"), OrderFormat = OrderFormat.Natural },
            new()
            {
                Name = DictionaryResource.GetString("OrderFormatLeadingZeros"), OrderFormat = OrderFormat.LeadingZeros
            },
        };

        FfmpegHardwareAccelerations = new List<FfmpegHardwareAccelerationItem>
        {
            new() { Name = "自动检测", Value = FfmpegHardwareAcceleration.Auto },
            new() { Name = "禁用硬件加速", Value = FfmpegHardwareAcceleration.Disabled },
            new() { Name = "NVIDIA NVENC", Value = FfmpegHardwareAcceleration.NvidiaNvenc },
            new() { Name = "Intel QSV", Value = FfmpegHardwareAcceleration.IntelQsv },
            new() { Name = "AMD AMF", Value = FfmpegHardwareAcceleration.AmdAmf },
            new() { Name = "Linux VAAPI", Value = FfmpegHardwareAcceleration.Vaapi },
            new() { Name = "macOS VideoToolbox", Value = FfmpegHardwareAcceleration.VideoToolbox },
        };

        FfmpegMaxParallelJobs = new List<int> { 1, 2, 3, 4 };

        #endregion
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        _isOnNavigatedTo = true;

        // 优先下载的视频编码
        var videoSettings = _settingsStore.Current.Video;
        var videoCodecs = videoSettings.VideoCodecs;
        SelectedVideoCodec = VideoCodecs.FirstOrDefault(t => t.Id == videoCodecs) ?? VideoCodecs[0];

        // 优先下载画质
        var quality = videoSettings.Quality;
        SelectedVideoQuality = VideoQualityList.FirstOrDefault(t => t.Id == quality) ?? VideoQualityList[0];

        // 优先下载音质
        var audioQuality = videoSettings.AudioQuality;
        SelectedAudioQuality = AudioQualityList.FirstOrDefault(t => t.Id == audioQuality) ?? AudioQualityList[0];

        // 首选视频解析方式
        var videoParseType = videoSettings.VideoParseType;
        SelectedVideoParseType = VideoParseTypeList.FirstOrDefault(t => t.Id == videoParseType) ?? VideoParseTypeList[0];

        // 是否下载flv视频后转码为mp4
        var isTranscodingFlvToMp4 = videoSettings.IsTranscodingFlvToMp4;
        IsTranscodingFlvToMp4 = isTranscodingFlvToMp4 == AllowStatus.Yes;

        // 是否下载aac音频后转码为mp3
        var isTranscodingAacToMp3 = videoSettings.IsTranscodingAacToMp3;
        IsTranscodingAacToMp3 = isTranscodingAacToMp3 == AllowStatus.Yes;

        var ffmpegHardwareAcceleration = videoSettings.FfmpegHardwareAcceleration;
        SelectedFfmpegHardwareAcceleration = FfmpegHardwareAccelerations
                                                 .FirstOrDefault(t => t.Value == ffmpegHardwareAcceleration) ??
                                             FfmpegHardwareAccelerations[0];

        SelectedFfmpegMaxParallelJob = videoSettings.FfmpegMaxParallelJobs;

        // 是否使用默认下载目录
        var isUseSaveVideoRootPath = videoSettings.IsUseSaveVideoRootPath;
        IsUseDefaultDirectory = isUseSaveVideoRootPath == AllowStatus.Yes;

        // 默认下载目录
        SaveVideoDirectory = videoSettings.SaveVideoRootPath;

        // 下载内容
        var videoContent = videoSettings.Content;

        DownloadAudio = videoContent.DownloadAudio;
        DownloadVideo = videoContent.DownloadVideo;
        DownloadDanmaku = videoContent.DownloadDanmaku;
        DownloadSubtitle = videoContent.DownloadSubtitle;
        DownloadCover = videoContent.DownloadCover;
        GenerateMovieMetadata = videoContent.GenerateMovieMetadata;
        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }
        else
        {
            DownloadAll = false;
        }

        // 文件命名格式
        var fileNameParts = videoSettings.FileNameParts;
        SelectedFileName.Clear();
        foreach (var fileNamePart in fileNameParts.Select(x => new DisplayFileNamePart()
        {
            Id = x,
            Title = DisplayFileNamePart(x),
        }))
        {
            SelectedFileName.Add(fileNamePart);
        }

        // 文件命名中的时间格式
        SelectedFileNamePartTimeFormat = videoSettings.FileNamePartTimeFormat;

        // 文件命名中的序号格式
        var orderFormat = videoSettings.OrderFormat;
        OrderFormatDisplay = OrderFormatList.FirstOrDefault(t => t.OrderFormat == orderFormat) ?? OrderFormatList[0];

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 优先下载的视频编码事件
    private RelayCommand<object>? _videoCodecsCommand;

    public RelayCommand<object> VideoCodecsCommand => _videoCodecsCommand ??= RequiredParameterCommand.Create<object>(ExecuteVideoCodecsCommand);

    /// <summary>
    /// 优先下载的视频编码事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteVideoCodecsCommand(object parameter)
    {
        if (parameter is not Quality videoCodecs)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with { VideoCodecs = videoCodecs.Id }).VideoCodecs == videoCodecs.Id;
        PublishTip(isSucceed);
    }

    // 优先下载画质事件
    private RelayCommand<object>? _videoQualityCommand;

    public RelayCommand<object> VideoQualityCommand => _videoQualityCommand ??= RequiredParameterCommand.Create<object>(ExecuteVideoQualityCommand);

    /// <summary>
    /// 优先下载画质事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteVideoQualityCommand(object parameter)
    {
        if (parameter is not Quality resolution)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with { Quality = resolution.Id }).Quality == resolution.Id;
        PublishTip(isSucceed);
    }

    // 优先下载音质事件
    private RelayCommand<object>? _audioQualityCommand;

    public RelayCommand<object> AudioQualityCommand => _audioQualityCommand ??= RequiredParameterCommand.Create<object>(ExecuteAudioQualityCommand);

    /// <summary>
    /// 优先下载音质事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteAudioQualityCommand(object parameter)
    {
        if (parameter is not Quality quality)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with { AudioQuality = quality.Id }).AudioQuality == quality.Id;
        PublishTip(isSucceed);
    }


    // 首选视频解析线路事件
    private RelayCommand<object>? _videoParseTypeCommand;

    public RelayCommand<object> VideoParseTypeCommand => _videoParseTypeCommand ??= RequiredParameterCommand.Create<object>(ExecuteVideoParseTypeCommand);

    /// <summary>
    /// 首选视频解析线路事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteVideoParseTypeCommand(object parameter)
    {
        if (parameter is not VideoParseType type)
        {
            return;
        }

        var parseType = type.Id ?? 1;
        var isSucceed = UpdateVideo(settings => settings with { VideoParseType = parseType }).VideoParseType == parseType;
        PublishTip(isSucceed);
    }

    // 是否下载flv视频后转码为mp4事件
    private RelayCommand? _isTranscodingFlvToMp4Command;

    public RelayCommand IsTranscodingFlvToMp4Command => _isTranscodingFlvToMp4Command ??= new RelayCommand(ExecuteIsTranscodingFlvToMp4Command);

    /// <summary>
    /// 是否下载flv视频后转码为mp4事件
    /// </summary>
    private void ExecuteIsTranscodingFlvToMp4Command()
    {
        var isTranscodingFlvToMp4 = IsTranscodingFlvToMp4 ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateVideo(settings => settings with
        {
            IsTranscodingFlvToMp4 = isTranscodingFlvToMp4
        }).IsTranscodingFlvToMp4 == isTranscodingFlvToMp4;
        PublishTip(isSucceed);
    }

    // 是否下载aac音频后转码为mp3事件
    private RelayCommand? _isTranscodingAacToMp3Command;

    public RelayCommand IsTranscodingAacToMp3Command => _isTranscodingAacToMp3Command ??= new RelayCommand(ExecuteIsTranscodingAacToMp3Command);

    /// <summary>
    /// 是否下载aac音频后转码为mp3事件
    /// </summary>
    private void ExecuteIsTranscodingAacToMp3Command()
    {
        var isTranscodingAacToMp3 = IsTranscodingAacToMp3 ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateVideo(settings => settings with
        {
            IsTranscodingAacToMp3 = isTranscodingAacToMp3
        }).IsTranscodingAacToMp3 == isTranscodingAacToMp3;
        PublishTip(isSucceed);
    }

    private RelayCommand<object>? _ffmpegHardwareAccelerationCommand;

    public RelayCommand<object> FfmpegHardwareAccelerationCommand =>
        _ffmpegHardwareAccelerationCommand ??= RequiredParameterCommand.Create<object>(ExecuteFfmpegHardwareAccelerationCommand);

    private void ExecuteFfmpegHardwareAccelerationCommand(object parameter)
    {
        if (parameter is not FfmpegHardwareAccelerationItem acceleration)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with
        {
            FfmpegHardwareAcceleration = acceleration.Value
        }).FfmpegHardwareAcceleration == acceleration.Value;
        PublishTip(isSucceed);
    }

    private RelayCommand<object>? _ffmpegMaxParallelJobsCommand;

    public RelayCommand<object> FfmpegMaxParallelJobsCommand =>
        _ffmpegMaxParallelJobsCommand ??= RequiredParameterCommand.Create<object>(ExecuteFfmpegMaxParallelJobsCommand);

    private void ExecuteFfmpegMaxParallelJobsCommand(object parameter)
    {
        if (parameter is not int maxParallelJobs)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with
        {
            FfmpegMaxParallelJobs = maxParallelJobs
        }).FfmpegMaxParallelJobs == maxParallelJobs;
        PublishTip(isSucceed);
    }

    #endregion

    /// <summary>
    /// 保存下载视频内容到设置
    /// </summary>
    private void SetVideoContent()
    {
        var updated = UpdateVideo(settings => settings with
        {
            Content = settings.Content with
            {
                DownloadAudio = DownloadAudio,
                DownloadVideo = DownloadVideo,
                DownloadDanmaku = DownloadDanmaku,
                DownloadSubtitle = DownloadSubtitle,
                DownloadCover = DownloadCover,
                GenerateMovieMetadata = GenerateMovieMetadata
            }
        });
        var isSucceed = updated.Content == new VideoContentApplicationSettings(
            DownloadAudio,
            DownloadVideo,
            DownloadDanmaku,
            DownloadSubtitle,
            DownloadCover,
            GenerateMovieMetadata);
        PublishTip(isSucceed);
    }

    private VideoApplicationSettings UpdateVideo(
        Func<VideoApplicationSettings, VideoApplicationSettings> update)
    {
        return _settingsStore.Update(settings => settings with
        {
            Video = update(settings.Video)
        }).Video;
    }

    /// <summary>
    /// 文件名字段显示
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    private static string DisplayFileNamePart(FileNamePart item)
    {
        var display = item switch
        {
            FileNamePart.Order => DictionaryResource.GetString("DisplayOrder"),
            FileNamePart.Section => DictionaryResource.GetString("DisplaySection"),
            FileNamePart.MainTitle => DictionaryResource.GetString("DisplayMainTitle"),
            FileNamePart.PageTitle => DictionaryResource.GetString("DisplayPageTitle"),
            FileNamePart.VideoZone => DictionaryResource.GetString("DisplayVideoZone"),
            FileNamePart.AudioQuality => DictionaryResource.GetString("DisplayAudioQuality"),
            FileNamePart.VideoQuality => DictionaryResource.GetString("DisplayVideoQuality"),
            FileNamePart.VideoCodec => DictionaryResource.GetString("DisplayVideoCodec"),
            FileNamePart.VideoPublishTime => DictionaryResource.GetString("DisplayVideoPublishTime"),
            FileNamePart.Avid => "avid",
            FileNamePart.Bvid => "bvid",
            FileNamePart.Cid => "cid",
            FileNamePart.UpMid => DictionaryResource.GetString("DisplayUpMid"),
            FileNamePart.UpName => DictionaryResource.GetString("DisplayUpName"),
            _ => string.Empty
        };

        if ((int)item >= 100)
        {
            display = HyphenSeparated.Hyphen[(int)item];
        }

        if (display == " ")
        {
            display = DictionaryResource.GetString("DisplaySpace");
        }

        return display;
    }

    /// <summary>
    /// 发送需要显示的tip
    /// </summary>
    /// <param name="isSucceed"></param>
    private void PublishTip(bool isSucceed)
    {
        if (_isOnNavigatedTo)
        {
            return;
        }

        Notifications.Show(isSucceed ? DictionaryResource.GetString("TipSettingUpdated") : DictionaryResource.GetString("TipSettingFailed"));
    }
}
