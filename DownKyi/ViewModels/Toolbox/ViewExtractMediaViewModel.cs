using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DownKyi.Commands;
using DownKyi.Core.FFMpeg;
using DownKyi.Events;
using DownKyi.Utils;
using Prism.Commands;
using Prism.Events;

namespace DownKyi.ViewModels.Toolbox;

public class ViewExtractMediaViewModel : ViewModelBase
{
    public const string Tag = "PageToolboxExtractMedia";

    // 是否正在执行任务
    private bool _isExtracting;

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

    public ViewExtractMediaViewModel(IEventAggregator eventAggregator) : base(eventAggregator)
    {
        #region 属性初始化

        VideoPaths = Array.Empty<string>();

        #endregion
    }

    #region 命令申明

    // 选择视频事件
    private DownKyiAsyncDelegateCommand? _selectVideoCommand;

    public DownKyiAsyncDelegateCommand SelectVideoCommand => _selectVideoCommand ??= new DownKyiAsyncDelegateCommand(ExecuteSelectVideoCommand);

    /// <summary>
    /// 选择视频事件
    /// </summary>
    private async Task ExecuteSelectVideoCommand()
    {
        if (_isExtracting)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        VideoPaths = await DialogUtils.SelectMultiVideoFile().ConfigureAwait(true) ?? Array.Empty<string>();
    }

    // 提取音频事件
    private DownKyiAsyncDelegateCommand? _extractAudioCommand;

    public DownKyiAsyncDelegateCommand ExtractAudioCommand => _extractAudioCommand ??= new DownKyiAsyncDelegateCommand(ExecuteExtractAudioCommand);

    /// <summary>
    /// 提取音频事件
    /// </summary>
    private async Task ExecuteExtractAudioCommand()
    {
        if (_isExtracting)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        if (VideoPaths.Count <= 0)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipNoSelectedVideo"));
            return;
        }

        Status = string.Empty;

        await Task.Run(() =>
        {
            _isExtracting = true;
            foreach (var item in VideoPaths)
            {
                // 音频文件名
                var audioFileName = item.Remove(item.Length - 4, 4) + ".aac";
                // 执行提取音频程序
                FfmpegProcessor.Instance.ExtractAudio(item, audioFileName, output => { Status += output + "\n"; });
            }

            _isExtracting = false;
        }).ConfigureAwait(true);
    }

    // 提取视频事件
    private DownKyiAsyncDelegateCommand? _extractVideoCommand;

    public DownKyiAsyncDelegateCommand ExtractVideoCommand => _extractVideoCommand ??= new DownKyiAsyncDelegateCommand(ExecuteExtractVideoCommand);

    /// <summary>
    /// 提取视频事件
    /// </summary>
    private async Task ExecuteExtractVideoCommand()
    {
        if (_isExtracting)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipWaitTaskFinished"));
            return;
        }

        if (VideoPaths.Count <= 0)
        {
            EventAggregator.GetEvent<MessageEvent>().Publish(DictionaryResource.GetString("TipNoSeletedVideo"));
            return;
        }

        Status = string.Empty;

        await Task.Run(() =>
        {
            _isExtracting = true;
            foreach (var item in VideoPaths)
            {
                // 视频文件名
                var videoFileName = item.Remove(item.Length - 4, 4) + "_onlyVideo.mp4";
                // 执行提取视频程序
                FfmpegProcessor.Instance.ExtractVideo(item, videoFileName, new Action<string>((output) => { Status += output + "\n"; }));
            }

            _isExtracting = false;
        }).ConfigureAwait(true);
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
