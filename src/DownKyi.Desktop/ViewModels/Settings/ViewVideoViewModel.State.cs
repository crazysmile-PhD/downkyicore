using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Settings;
using DownKyi.Models;

namespace DownKyi.ViewModels.Settings;

internal partial class ViewVideoViewModel
{
    #region 页面属性申明

    private IReadOnlyList<Quality> _videoCodecs = Array.Empty<Quality>();

    public IReadOnlyList<Quality> VideoCodecs
    {
        get => _videoCodecs;
        set => SetProperty(ref _videoCodecs, value);
    }

    private Quality _selectedVideoCodec = null!;

    public Quality SelectedVideoCodec
    {
        get => _selectedVideoCodec;
        set => SetProperty(ref _selectedVideoCodec, value);
    }

    private IReadOnlyList<Quality> _videoQualityList = Array.Empty<Quality>();

    public IReadOnlyList<Quality> VideoQualityList
    {
        get => _videoQualityList;
        set => SetProperty(ref _videoQualityList, value);
    }

    private Quality _selectedVideoQuality = null!;

    public Quality SelectedVideoQuality
    {
        get => _selectedVideoQuality;
        set => SetProperty(ref _selectedVideoQuality, value);
    }

    private IReadOnlyList<Quality> _audioQualityList = Array.Empty<Quality>();

    public IReadOnlyList<Quality> AudioQualityList
    {
        get => _audioQualityList;
        set => SetProperty(ref _audioQualityList, value);
    }

    private Quality _selectedAudioQuality = null!;

    public Quality SelectedAudioQuality
    {
        get => _selectedAudioQuality;
        set => SetProperty(ref _selectedAudioQuality, value);
    }

    private IReadOnlyList<VideoParseType> _videoParseTypeList = Array.Empty<VideoParseType>();

    public IReadOnlyList<VideoParseType> VideoParseTypeList
    {
        get => _videoParseTypeList;
        set => SetProperty(ref _videoParseTypeList, value);
    }

    private VideoParseType _selectedVideoParseType = null!;

    public VideoParseType SelectedVideoParseType
    {
        get => _selectedVideoParseType;
        set => SetProperty(ref _selectedVideoParseType, value);
    }

    private bool _isTranscodingFlvToMp4;

    public bool IsTranscodingFlvToMp4
    {
        get => _isTranscodingFlvToMp4;
        set => SetProperty(ref _isTranscodingFlvToMp4, value);
    }

    private bool _isTranscodingAacToMp3;

    public bool IsTranscodingAacToMp3
    {
        get => _isTranscodingAacToMp3;
        set => SetProperty(ref _isTranscodingAacToMp3, value);
    }

    private IReadOnlyList<FfmpegHardwareAccelerationItem> _ffmpegHardwareAccelerations =
        Array.Empty<FfmpegHardwareAccelerationItem>();

    public IReadOnlyList<FfmpegHardwareAccelerationItem> FfmpegHardwareAccelerations
    {
        get => _ffmpegHardwareAccelerations;
        set => SetProperty(ref _ffmpegHardwareAccelerations, value);
    }

    private FfmpegHardwareAccelerationItem _selectedFfmpegHardwareAcceleration = null!;

    public FfmpegHardwareAccelerationItem SelectedFfmpegHardwareAcceleration
    {
        get => _selectedFfmpegHardwareAcceleration;
        set => SetProperty(ref _selectedFfmpegHardwareAcceleration, value);
    }

    private IReadOnlyList<int> _ffmpegMaxParallelJobs = Array.Empty<int>();

    public IReadOnlyList<int> FfmpegMaxParallelJobs
    {
        get => _ffmpegMaxParallelJobs;
        set => SetProperty(ref _ffmpegMaxParallelJobs, value);
    }

    private int _selectedFfmpegMaxParallelJob;

    public int SelectedFfmpegMaxParallelJob
    {
        get => _selectedFfmpegMaxParallelJob;
        set => SetProperty(ref _selectedFfmpegMaxParallelJob, value);
    }

    private bool _isUseDefaultDirectory;

    public bool IsUseDefaultDirectory
    {
        get => _isUseDefaultDirectory;
        set => SetProperty(ref _isUseDefaultDirectory, value);
    }

    private string _saveVideoDirectory = string.Empty;

    public string SaveVideoDirectory
    {
        get => _saveVideoDirectory;
        set => SetProperty(ref _saveVideoDirectory, value);
    }

    private bool _downloadAll;

    public bool DownloadAll
    {
        get => _downloadAll;
        set => SetProperty(ref _downloadAll, value);
    }

    private bool _downloadAudio;

    public bool DownloadAudio
    {
        get => _downloadAudio;
        set => SetProperty(ref _downloadAudio, value);
    }

    private bool _downloadVideo;

    public bool DownloadVideo
    {
        get => _downloadVideo;
        set => SetProperty(ref _downloadVideo, value);
    }

    private bool _downloadDanmaku;

    public bool DownloadDanmaku
    {
        get => _downloadDanmaku;
        set => SetProperty(ref _downloadDanmaku, value);
    }

    private bool _downloadSubtitle;

    public bool DownloadSubtitle
    {
        get => _downloadSubtitle;
        set => SetProperty(ref _downloadSubtitle, value);
    }

    private bool _downloadCover;

    public bool DownloadCover
    {
        get => _downloadCover;
        set => SetProperty(ref _downloadCover, value);
    }

    private bool _generateMovieMetadata;

    public bool GenerateMovieMetadata
    {
        get => _generateMovieMetadata;
        set => SetProperty(ref _generateMovieMetadata, value);
    }

    private ObservableCollection<DisplayFileNamePart> _selectedFileName = new();

    public ObservableCollection<DisplayFileNamePart> SelectedFileName => _selectedFileName;

    private ObservableCollection<DisplayFileNamePart> _optionalFields = new();

    public ObservableCollection<DisplayFileNamePart> OptionalFields => _optionalFields;

    private int _selectedOptionalField;

    public int SelectedOptionalField
    {
        get => _selectedOptionalField;
        set => SetProperty(ref _selectedOptionalField, value);
    }

    private IReadOnlyList<string> _fileNamePartTimeFormatList = Array.Empty<string>();

    public IReadOnlyList<string> FileNamePartTimeFormatList
    {
        get => _fileNamePartTimeFormatList;
        set => SetProperty(ref _fileNamePartTimeFormatList, value);
    }

    private string _selectedFileNamePartTimeFormat = string.Empty;

    public string SelectedFileNamePartTimeFormat
    {
        get => _selectedFileNamePartTimeFormat;
        set => SetProperty(ref _selectedFileNamePartTimeFormat, value);
    }

    private IReadOnlyList<OrderFormatDisplay> _orderFormatList = Array.Empty<OrderFormatDisplay>();

    public IReadOnlyList<OrderFormatDisplay> OrderFormatList
    {
        get => _orderFormatList;
        set => SetProperty(ref _orderFormatList, value);
    }

    private OrderFormatDisplay _orderFormatDisplay = null!;

    public OrderFormatDisplay OrderFormatDisplay
    {
        get => _orderFormatDisplay;
        set => SetProperty(ref _orderFormatDisplay, value);
    }

    #endregion
}
