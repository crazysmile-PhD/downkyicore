using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Path = System.IO.Path;


namespace DownKyi.ViewModels.Toolbox;

internal class ViewDelogoViewModel : ViewModelBase
{
    public const string Tag = "PageToolboxDelogo";

    // 是否正在执行去水印任务
    private bool _isDelogo;
    private readonly IFilePickerService _filePickerService;
    private readonly FfmpegProcessor _ffmpegProcessor;
    private readonly ILogger<ViewDelogoViewModel> _logger;

    private IImage _source = null!;


    public IImage Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    #region 页面属性申明

    private string? _videoPath;

    public string? VideoPath
    {
        get => _videoPath;
        set => SetProperty(ref _videoPath, value);
    }

    private int _logoWidth;

    public int LogoWidth
    {
        get => _logoWidth;
        set
        {
            WatermarkArea = new Rect(_watermarkArea.X, _watermarkArea.Y, value, _watermarkArea.Height);
            SetProperty(ref _logoWidth, value);
        }
    }

    private int _logoHeight;

    public int LogoHeight
    {
        get => _logoHeight;
        set
        {
            WatermarkArea = new Rect(_watermarkArea.X, _watermarkArea.Y, _watermarkArea.Width, value);
            SetProperty(ref _logoHeight, value);
        }
    }

    private int _logoX;

    public int LogoX
    {
        get => _logoX;
        set
        {
            WatermarkArea = new Rect(value, _watermarkArea.Y, _watermarkArea.Width, _watermarkArea.Height);
            SetProperty(ref _logoX, value);
        }
    }

    private int _logoY;

    public int LogoY
    {
        get => _logoY;
        set
        {
            WatermarkArea = new Rect(_watermarkArea.X, value, _watermarkArea.Width, _watermarkArea.Height);
            SetProperty(ref _logoY, value);
        }
    }

    private string _status = string.Empty;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private bool _updatingWatermarkArea;

    private Rect _watermarkArea;

    public Rect WatermarkArea
    {
        get => _watermarkArea;
        set
        {
            if (_updatingWatermarkArea) return;
            _updatingWatermarkArea = true;
            LogoHeight = (int)Math.Round(value.Height);
            LogoWidth = (int)Math.Round(value.Width);
            LogoX = (int)Math.Round(value.X);
            LogoY = (int)Math.Round(value.Y);
            SetProperty(ref _watermarkArea, value);
            _updatingWatermarkArea = false;
        }
    }


    public IReadOnlyList<SolidColorBrush> AvailableColors { get; }


    private SolidColorBrush _selectedColor = null!;

    public SolidColorBrush SelectedColor
    {
        get => _selectedColor;
        set => SetProperty(ref _selectedColor, value);
    }

    #endregion

    public ViewDelogoViewModel(
        IDesktopInteractionContext desktopInteractions,
        IFilePickerService filePickerService,
        FfmpegProcessor ffmpegProcessor,
        ILogger<ViewDelogoViewModel> logger) : base(desktopInteractions)
    {
        _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        VideoPath = string.Empty;


        AvailableColors = new[]{
            new SolidColorBrush(Colors.Red),
            new SolidColorBrush(Colors.Green),
            new SolidColorBrush(Colors.Blue),
            new SolidColorBrush(Colors.White),
            new SolidColorBrush(Colors.Black),
            new SolidColorBrush(Colors.Gray),
            new SolidColorBrush(Colors.Fuchsia),
        };
        SelectedColor = AvailableColors[0];
        WatermarkArea = new Rect(20, 20, 100, 100);
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
        if (_isDelogo)
        {
            Notifications.Show(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }
        VideoPath = await _filePickerService.SelectVideoAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(VideoPath))
        {
            try
            {
                Source = new Bitmap(await _ffmpegProcessor
                    .ExtractVideoFrameAsync(VideoPath, TimeSpan.FromSeconds(1)).ConfigureAwait(true));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogErrorMessage("Delogo preview extraction failed.", e);
            }
        }
    }

    // 去水印事件
    private DownKyiAsyncDelegateCommand? _delogoCommand;

    public DownKyiAsyncDelegateCommand DelogoCommand => _delogoCommand ??= new DownKyiAsyncDelegateCommand(ExecuteDelogoCommand, _logger);

    /// <summary>
    /// 去水印事件
    /// </summary>
    private async Task ExecuteDelogoCommand()
    {
        if (_isDelogo)
        {
            Notifications.Show(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        if (VideoPath is null or "")
        {
            Notifications.Show(DictionaryResource.GetString("TipNoSeletedVideo"));
            return;
        }

        if (LogoWidth == -1)
        {
            Notifications.Show(DictionaryResource.GetString("TipInputRightLogoWidth"));
            return;
        }

        if (LogoHeight == -1)
        {
            Notifications.Show(DictionaryResource.GetString("TipInputRightLogoHeight"));
            return;
        }

        if (LogoX == -1)
        {
            Notifications.Show(DictionaryResource.GetString("TipInputRightLogoX"));
            return;
        }

        if (LogoY == -1)
        {
            Notifications.Show(DictionaryResource.GetString("TipInputRightLogoY"));
            return;
        }

        // 新文件名
        var newFileName = VideoPath.Insert(VideoPath.Length - 4, "_delogo");
        Status = string.Empty;

        _isDelogo = true;
        try
        {
            await _ffmpegProcessor.DelogoAsync(
                VideoPath,
                newFileName,
                _logoX,
                _logoY,
                _logoWidth,
                _logoHeight,
                output => { Status += output + "\n"; }).ConfigureAwait(true);
        }
        finally
        {
            _isDelogo = false;
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

        var scrollViewer = output.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        scrollViewer?.ScrollToEnd();
    }

    #endregion
}
