using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.Utils;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;

namespace DownKyi.ViewModels.Dialogs;

internal class ViewDownloadSetterViewModel : BaseDialogViewModel
{
    public const string Tag = "DialogDownloadSetter";
    private readonly IEventAggregator _eventAggregator;
    private readonly IFilePickerService _filePickerService;
    private readonly ISettingsStore _settingsStore;

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


    private string _directory = string.Empty;

    public string Directory
    {
        get => _directory;
        set
        {
            SetProperty(ref _directory, value);

            if (string.IsNullOrEmpty(_directory)) return;
            DriveName = _directory[..1].ToUpperInvariant();
            DriveNameFreeSpace = Format.FormatFileSize(HardDisk.GetHardDiskFreeSpace(DriveName));
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
        IEventAggregator eventAggregator,
        IFilePickerService filePickerService,
        ISettingsStore settingsStore)
    {
        _eventAggregator = eventAggregator;
        _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        #region 属性初始化

        Title = DictionaryResource.GetString("DownloadSetter");

        CloudDownloadIcon = NormalIcon.Instance().CloudDownload;
        CloudDownloadIcon.Fill = DictionaryResource.GetColor("ColorPrimary");

        FolderIcon = NormalIcon.Instance().Folder;
        FolderIcon.Fill = DictionaryResource.GetColor("ColorPrimary");

        // 下载内容
        var videoSettings = _settingsStore.Current.Video;
        var videoContent = videoSettings.Content;

        DownloadAudio = videoContent.DownloadAudio;
        DownloadVideo = videoContent.DownloadVideo;
        DownloadDanmaku = videoContent.DownloadDanmaku;
        DownloadSubtitle = videoContent.DownloadSubtitle;
        DownloadCover = videoContent.DownloadCover;

        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }
        else
        {
            DownloadAll = false;
        }

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

    #region 命令申明

    // 浏览文件夹事件
    private DownKyiAsyncDelegateCommand? _browseCommand;

    public DownKyiAsyncDelegateCommand BrowseCommand => _browseCommand ??= new DownKyiAsyncDelegateCommand(ExecuteBrowseCommand);

    /// <summary>
    /// 浏览文件夹事件
    /// </summary>
    private async Task ExecuteBrowseCommand()
    {
        var directory = await SetDirectory().ConfigureAwait(true);

        if (directory == null)
        {
            _eventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("WarningNullDirectory"));
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
    private DelegateCommand? _downloadAllCommand;

    public DelegateCommand DownloadAllCommand => _downloadAllCommand ??= new DelegateCommand(ExecuteDownloadAllCommand);

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
    private DelegateCommand? _downloadAudioCommand;

    public DelegateCommand DownloadAudioCommand => _downloadAudioCommand ??= new DelegateCommand(ExecuteDownloadAudioCommand);

    /// <summary>
    /// 音频选择事件
    /// </summary>
    private void ExecuteDownloadAudioCommand()
    {
        if (!DownloadAudio)
        {
            DownloadAll = false;
        }

        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }

        SetVideoContent();
    }

    // 视频选择事件
    private DelegateCommand? _downloadVideoCommand;

    public DelegateCommand DownloadVideoCommand => _downloadVideoCommand ??= new DelegateCommand(ExecuteDownloadVideoCommand);

    /// <summary>
    /// 视频选择事件
    /// </summary>
    private void ExecuteDownloadVideoCommand()
    {
        if (!DownloadVideo)
        {
            DownloadAll = false;
        }

        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }

        SetVideoContent();
    }

    // 弹幕选择事件
    private DelegateCommand? _downloadDanmakuCommand;

    public DelegateCommand DownloadDanmakuCommand => _downloadDanmakuCommand ??= new DelegateCommand(ExecuteDownloadDanmakuCommand);

    /// <summary>
    /// 弹幕选择事件
    /// </summary>
    private void ExecuteDownloadDanmakuCommand()
    {
        if (!DownloadDanmaku)
        {
            DownloadAll = false;
        }

        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }

        SetVideoContent();
    }

    // 字幕选择事件
    private DelegateCommand? _downloadSubtitleCommand;

    public DelegateCommand DownloadSubtitleCommand => _downloadSubtitleCommand ??= new DelegateCommand(ExecuteDownloadSubtitleCommand);

    /// <summary>
    /// 字幕选择事件
    /// </summary>
    private void ExecuteDownloadSubtitleCommand()
    {
        if (!DownloadSubtitle)
        {
            DownloadAll = false;
        }

        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }

        SetVideoContent();
    }

    // 封面选择事件
    private DelegateCommand? _downloadCoverCommand;

    public DelegateCommand DownloadCoverCommand => _downloadCoverCommand ??= new DelegateCommand(ExecuteDownloadCoverCommand);

    /// <summary>
    /// 封面选择事件
    /// </summary>
    private void ExecuteDownloadCoverCommand()
    {
        if (!DownloadCover)
        {
            DownloadAll = false;
        }

        if (DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover)
        {
            DownloadAll = true;
        }

        SetVideoContent();
    }

    // 确认下载事件
    private DelegateCommand? _downloadCommand;

    public DelegateCommand DownloadCommand => _downloadCommand ??= new DelegateCommand(ExecuteDownloadCommand);

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
        ListHelper.InsertUnique(DirectoryList, Directory, 0, ref _directory);

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
        IDialogParameters parameters = new DialogParameters
        {
            { "directory", Directory },
            { "downloadAudio", DownloadAudio },
            { "downloadVideo", DownloadVideo },
            { "downloadDanmaku", DownloadDanmaku },
            { "downloadSubtitle", DownloadSubtitle },
            { "downloadCover", DownloadCover }
        };

        CloseDialog(new DialogResult(ButtonResult.OK) { Parameters = parameters });
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
        // if (path == null || path == string.Empty)
        // {
        //     return null;
        // }
    }
}
