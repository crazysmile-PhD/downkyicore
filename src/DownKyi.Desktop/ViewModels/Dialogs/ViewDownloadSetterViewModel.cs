using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Images;
using DownKyi.Services.Download;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal class ViewDownloadSetterViewModel : BaseDialogViewModel
{
    public const string Tag = "DialogDownloadSetter";
    private readonly IUserNotificationService _notifications;
    private readonly IFilePickerService _filePickerService;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ViewDownloadSetterViewModel> _logger;

    // 历史文件夹的数量
    private const int MaxDirectoryListCount = 20;

    #region 页面属性申明

    private VectorImage _cloudDownloadIcon = null!;

    public VectorImage CloudDownloadIcon
    {
        get => _cloudDownloadIcon;
        set => SetProperty(ref _cloudDownloadIcon, value);
    }

    private VectorImage _folderIcon = null!;

    public VectorImage FolderIcon
    {
        get => _folderIcon;
        set => SetProperty(ref _folderIcon, value);
    }

    private bool _isDefaultDownloadDirectory;

    public bool IsDefaultDownloadDirectory
    {
        get => _isDefaultDownloadDirectory;
        set => SetProperty(ref _isDefaultDownloadDirectory, value);
    }


    public ObservableCollection<string> DirectoryList { get; private set; }

    public ObservableCollection<SubtitleTrackItem> SubtitleTracks { get; } = [];

    public DownloadSettingsDialog.SubtitleTrackDiscoveryStatus SubtitleTrackDiscoveryStatus
    {
        get;
        private set;
    }

    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;


    private string _directory = string.Empty;

    public string Directory
    {
        get => _directory;
        set
        {
            SetProperty(ref _directory, value);

            if (string.IsNullOrEmpty(_directory) || !Path.IsPathFullyQualified(_directory))
            {
                return;
            }

            DriveName = Path.GetPathRoot(_directory) ?? _directory;
            try
            {
                DriveNameFreeSpace = Format.FormatFileSize(new DriveInfo(_directory).TotalFreeSpace);
            }
            catch (Exception e) when (e is DriveNotFoundException or IOException or UnauthorizedAccessException)
            {
                DriveNameFreeSpace = Format.FormatFileSize(0);
                _logger.LogErrorMessage("Available download disk space could not be read.", e);
            }
        }
    }

    private string _driveName = string.Empty;

    public string DriveName
    {
        get => _driveName;
        set => SetProperty(ref _driveName, value);
    }

    private string _driveNameFreeSpace = string.Empty;

    public string DriveNameFreeSpace
    {
        get => _driveNameFreeSpace;
        set => SetProperty(ref _driveNameFreeSpace, value);
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

    #endregion

    public ViewDownloadSetterViewModel(
        IUserNotificationService notifications,
        IFilePickerService filePickerService,
        ISettingsStore settingsStore,
        ILogger<ViewDownloadSetterViewModel> logger)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region 属性初始化

        Title = DictionaryResource.GetString("DownloadSetter");

        CloudDownloadIcon = NormalIcon.Current.CloudDownload;

        FolderIcon = NormalIcon.Current.Folder;

        // 下载内容
        var videoSettings = _settingsStore.Current.Video;
        var videoContent = videoSettings.Content;

        DownloadAudio = videoContent.DownloadAudio;
        DownloadVideo = videoContent.DownloadVideo;
        DownloadDanmaku = videoContent.DownloadDanmaku;
        DownloadSubtitle = videoContent.DownloadSubtitle;
        DownloadCover = videoContent.DownloadCover;

        UpdateDownloadAll();

        // 历史下载目录
        DirectoryList = new ObservableCollection<string>(videoSettings.HistoryVideoRootPaths);
        var directory = videoSettings.SaveVideoRootPath;
        if (!DirectoryList.Contains(directory))
        {
            ListHelper.InsertUnique(DirectoryList, directory, 0);
        }

        Directory = directory;

        // 是否使用默认下载目录
        IsDefaultDownloadDirectory = videoSettings.IsUseSaveVideoRootPath == AllowStatus.Yes;

        #endregion
    }

    public override void OnDialogOpened(AppDialogRequest request)
    {
        var discovery = DownloadSettingsDialog.ReadSubtitleDiscovery(request);
        SubtitleTrackDiscoveryStatus = discovery.Status;
        var tracks = discovery.Tracks;

        for (var index = 0; index < tracks.Count; index++)
        {
            SubtitleTracks.Add(new SubtitleTrackItem(
                tracks[index], DownloadSubtitle, DownloadSubtitle && index == 0));
        }

        OnPropertyChanged(nameof(HasSubtitleTracks));
    }

    #region 命令申明

    // 浏览文件夹事件
    private DownKyiAsyncDelegateCommand? _browseCommand;

    public DownKyiAsyncDelegateCommand BrowseCommand => _browseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteBrowseCommand, _logger);

    /// <summary>
    /// 浏览文件夹事件
    /// </summary>
    private async Task ExecuteBrowseCommand()
    {
        var directory = await SetDirectory().ConfigureAwait(true);

        if (directory == null)
        {
            _notifications.Show(DictionaryResource.GetString("WarningNullDirectory"));
        }
        else
        {
            ListHelper.InsertUnique(DirectoryList, directory, 0);
            Directory = directory;

            if (DirectoryList.Count > MaxDirectoryListCount)
            {
                DirectoryList.RemoveAt(MaxDirectoryListCount);
            }
        }
    }

    // 所有内容选择事件
    private RelayCommand? _downloadAllCommand;

    public RelayCommand DownloadAllCommand => _downloadAllCommand ??= new RelayCommand(ExecuteDownloadAllCommand);

    /// <summary>
    /// 所有内容选择事件
    /// </summary>
    private void ExecuteDownloadAllCommand()
    {
        if (DownloadAll)
        {
            DownloadAudio = true;
            DownloadVideo = true;
            DownloadDanmaku = true;
            DownloadSubtitle = true;
            DownloadCover = true;
        }
        else
        {
            DownloadAudio = false;
            DownloadVideo = false;
            DownloadDanmaku = false;
            DownloadSubtitle = false;
            DownloadCover = false;
        }

        SetVideoContent();
    }

    // 音频选择事件
    private RelayCommand? _downloadAudioCommand;

    public RelayCommand DownloadAudioCommand => _downloadAudioCommand ??= new RelayCommand(ExecuteDownloadAudioCommand);

    /// <summary>
    /// 音频选择事件
    /// </summary>
    private void ExecuteDownloadAudioCommand()
    {
        UpdateDownloadAll();
        SetVideoContent();
    }

    // 视频选择事件
    private RelayCommand? _downloadVideoCommand;

    public RelayCommand DownloadVideoCommand => _downloadVideoCommand ??= new RelayCommand(ExecuteDownloadVideoCommand);

    /// <summary>
    /// 视频选择事件
    /// </summary>
    private void ExecuteDownloadVideoCommand()
    {
        UpdateDownloadAll();
        SetVideoContent();
    }

    // 弹幕选择事件
    private RelayCommand? _downloadDanmakuCommand;

    public RelayCommand DownloadDanmakuCommand => _downloadDanmakuCommand ??= new RelayCommand(ExecuteDownloadDanmakuCommand);

    /// <summary>
    /// 弹幕选择事件
    /// </summary>
    private void ExecuteDownloadDanmakuCommand()
    {
        UpdateDownloadAll();
        SetVideoContent();
    }

    // 字幕选择事件
    private RelayCommand? _downloadSubtitleCommand;

    public RelayCommand DownloadSubtitleCommand => _downloadSubtitleCommand ??= new RelayCommand(ExecuteDownloadSubtitleCommand);

    /// <summary>
    /// 字幕选择事件
    /// </summary>
    private void ExecuteDownloadSubtitleCommand()
    {
        UpdateDownloadAll();
        SetVideoContent();
    }

    // 封面选择事件
    private RelayCommand? _downloadCoverCommand;

    public RelayCommand DownloadCoverCommand => _downloadCoverCommand ??= new RelayCommand(ExecuteDownloadCoverCommand);

    /// <summary>
    /// 封面选择事件
    /// </summary>
    private void ExecuteDownloadCoverCommand()
    {
        UpdateDownloadAll();
        SetVideoContent();
    }

    private void UpdateDownloadAll()
    {
        DownloadAll = DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover;
    }

    // 确认下载事件
    private RelayCommand? _downloadCommand;

    public RelayCommand DownloadCommand => _downloadCommand ??= new RelayCommand(ExecuteDownloadCommand);

    /// <summary>
    /// 确认下载事件
    /// </summary>
    private void ExecuteDownloadCommand()
    {
        if (string.IsNullOrEmpty(Directory))
        {
            return;
        }

        // 将Directory移动到第一项
        // 如果直接在ComboBox中选择的就需要
        // 否则选中项不会在下次出现在第一项
        var selectedDirectory = Directory;
        ListHelper.InsertUnique(DirectoryList, selectedDirectory, 0);
        Directory = selectedDirectory;

        // 将更新后的目录设置一次写入，避免其他消费者看到半套状态
        _settingsStore.Update(settings => settings with
        {
            Video = settings.Video with
            {
                IsUseSaveVideoRootPath = IsDefaultDownloadDirectory ? AllowStatus.Yes : AllowStatus.No,
                SaveVideoRootPath = Directory,
                HistoryVideoRootPaths = DirectoryList.ToImmutableArray()
            }
        });

        // 返回数据
        var requestedContent = new DownloadContentSelection(
            DownloadAudio,
            DownloadVideo,
            DownloadDanmaku,
            DownloadSubtitle,
            DownloadCover);
        var selectedIds = HasSubtitleTracks
            ? SubtitleTracks.Where(track => track.IsSelected).Select(track => track.TrackId).ToArray()
            : null;
        var defaultTrackId = SubtitleTracks.FirstOrDefault(track => track.IsDefault)?.TrackId;
        CloseDialog(AppDialogOutcome.Accepted, DownloadSettingsDialog.EncodeResult(
            Directory, requestedContent, selectedIds, defaultTrackId));
    }

    #endregion

    /// <summary>
    /// 保存下载视频内容到设置
    /// </summary>
    private void SetVideoContent()
    {
        _settingsStore.Update(settings => settings with
        {
            Video = settings.Video with
            {
                Content = settings.Video.Content with
                {
                    DownloadAudio = DownloadAudio,
                    DownloadVideo = DownloadVideo,
                    DownloadDanmaku = DownloadDanmaku,
                    DownloadSubtitle = DownloadSubtitle,
                    DownloadCover = DownloadCover
                }
            }
        });
    }

    /// <summary>
    /// 设置下载路径
    /// </summary>
    /// <returns></returns>
    private async Task<string?> SetDirectory()
    {
        // 下载目录
        // 弹出选择下载目录的窗口
        return await _filePickerService.SelectFolderAsync().ConfigureAwait(true);
    }
}

internal sealed partial class SubtitleTrackItem(
    DownloadSettingsDialog.SubtitleTrack track, bool isSelected, bool isDefault) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = isSelected;

    [ObservableProperty]
    private bool _isDefault = isDefault;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!value)
        {
            IsDefault = false;
        }
    }

    public long TrackId { get; } = track.TrackId;
    public string Language { get; } = track.Language;
    public string DisplayLanguage { get; } = track.DisplayLanguage;
    public int Type { get; } = track.Type;
    public string Url { get; } = track.Url;
}
