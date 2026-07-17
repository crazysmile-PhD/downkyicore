using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.FFMpeg;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using Prism.Commands;

namespace DownKyi.ViewModels.Toolbox;

internal class ViewExtractMediaViewModel : ViewModelBase
{
    public const string Tag = "PageToolboxExtractMedia";

    // 是否正在执行任务
    private bool _isExtracting;
    private readonly IFilePickerService _filePickerService;
    private readonly FfmpegProcessor _ffmpegProcessor;
    private readonly ILogger<ViewExtractMediaViewModel> _logger;

    #region 页面属性申明

    private string _videoPathsStr = string.Empty;

    public string VideoPathsStr
    {
        get => _videoPathsStr;
        set => SetProperty(ref _videoPathsStr, value);
    }

    private IReadOnlyList<string> _videoPaths = Array.Empty<string>();

    public IReadOnlyList<string> VideoPaths
    {
        get => _videoPaths;
        set
        {
            _videoPaths = value;
            VideoPathsStr = string.Join(Environment.NewLine, value);
        }
    }

    private string _status = string.Empty;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    #endregion

    public ViewExtractMediaViewModel(
        IDesktopInteractionContext desktopInteractions,
        IFilePickerService filePickerService,
        FfmpegProcessor ffmpegProcessor,
        ILogger<ViewExtractMediaViewModel> logger) : base(desktopInteractions)
    {
        _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        VideoPaths = Array.Empty<string>();

        #endregion
    }

    #region 命令申明

    // 选择视频事件
    private DownKyiAsyncDelegateCommand? _selectVideoCommand;

    public DownKyiAsyncDelegateCommand SelectVideoCommand => _selectVideoCommand ??= new DownKyiAsyncDelegateCommand(ExecuteSelectVideoCommand, _logger);

    /// <summary>
    /// 选择视频事件
    /// </summary>
    private async Task ExecuteSelectVideoCommand()
    {
        if (_isExtracting)
        {
            Notifications.Show(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        VideoPaths = await _filePickerService.SelectVideosAsync().ConfigureAwait(true);
    }

    // 提取音频事件
    private DownKyiAsyncDelegateCommand? _extractAudioCommand;

    public DownKyiAsyncDelegateCommand ExtractAudioCommand => _extractAudioCommand ??= new DownKyiAsyncDelegateCommand(ExecuteExtractAudioCommand, _logger);

    /// <summary>
    /// 提取音频事件
    /// </summary>
    private async Task ExecuteExtractAudioCommand()
    {
        if (_isExtracting)
        {
            Notifications.Show(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        if (VideoPaths.Count <= 0)
        {
            Notifications.Show(DictionaryResource.GetString("TipNoSelectedVideo"));
            return;
        }

        Status = string.Empty;

        _isExtracting = true;
        try
        {
            foreach (var item in VideoPaths)
            {
                var audioFileName = item.Remove(item.Length - 4, 4) + ".aac";
                await _ffmpegProcessor.ExtractAudioAsync(
                    item,
                    audioFileName,
                    output => { Status += output + "\n"; }).ConfigureAwait(true);
            }
        }
        finally
        {
            _isExtracting = false;
        }
    }

    // 提取视频事件
    private DownKyiAsyncDelegateCommand? _extractVideoCommand;

    public DownKyiAsyncDelegateCommand ExtractVideoCommand => _extractVideoCommand ??= new DownKyiAsyncDelegateCommand(ExecuteExtractVideoCommand, _logger);

    /// <summary>
    /// 提取视频事件
    /// </summary>
    private async Task ExecuteExtractVideoCommand()
    {
        if (_isExtracting)
        {
            Notifications.Show(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        if (VideoPaths.Count <= 0)
        {
            Notifications.Show(DictionaryResource.GetString("TipNoSeletedVideo"));
            return;
        }

        Status = string.Empty;

        _isExtracting = true;
        try
        {
            foreach (var item in VideoPaths)
            {
                var videoFileName = item.Remove(item.Length - 4, 4) + "_onlyVideo.mp4";
                await _ffmpegProcessor.ExtractVideoAsync(
                    item,
                    videoFileName,
                    output => { Status += output + "\n"; }).ConfigureAwait(true);
            }
        }
        finally
        {
            _isExtracting = false;
        }
    }

    // Status改变事件
    private DelegateCommand<object>? _statusCommand;

    public DelegateCommand<object> StatusCommand => _statusCommand ??= new DelegateCommand<object>(ExecuteStatusCommand);

    /// <summary>
    /// Status改变事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteStatusCommand(object parameter)
    {
        if (parameter is not TextBox output)
        {
            return;
        }

        // TextBox滚动到底部
        output.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
    }

    #endregion
}
