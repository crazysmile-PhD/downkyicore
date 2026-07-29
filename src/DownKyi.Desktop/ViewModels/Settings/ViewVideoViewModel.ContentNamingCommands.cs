using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Settings;

internal partial class ViewVideoViewModel
{
    // 是否使用默认下载目录事件
    private RelayCommand? _isUseDefaultDirectoryCommand;

    public RelayCommand IsUseDefaultDirectoryCommand =>
        _isUseDefaultDirectoryCommand ??= new RelayCommand(ExecuteIsUseDefaultDirectoryCommand);

    /// <summary>
    /// 是否使用默认下载目录事件
    /// </summary>
    private void ExecuteIsUseDefaultDirectoryCommand()
    {
        var isUseDefaultDirectory = IsUseDefaultDirectory ? AllowStatus.Yes : AllowStatus.No;

        var isSucceed = UpdateVideo(settings => settings with
        {
            IsUseSaveVideoRootPath = isUseDefaultDirectory
        }).IsUseSaveVideoRootPath == isUseDefaultDirectory;
        PublishTip(isSucceed);
    }

    // 修改默认下载目录事件
    private DownKyiAsyncDelegateCommand? _changeSaveVideoDirectoryCommand;

    public DownKyiAsyncDelegateCommand ChangeSaveVideoDirectoryCommand =>
        _changeSaveVideoDirectoryCommand ??=
            new DownKyiAsyncDelegateCommand(ExecuteChangeSaveVideoDirectoryCommand, _logger);

    /// <summary>
    /// 修改默认下载目录事件
    /// </summary>
    private async Task ExecuteChangeSaveVideoDirectoryCommand()
    {
        var directory = await _filePickerService.SelectFolderAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with
        {
            SaveVideoRootPath = directory
        }).SaveVideoRootPath == directory;
        PublishTip(isSucceed);

        if (isSucceed)
        {
            SaveVideoDirectory = directory;
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
    private RelayCommand? _downloadVideoCommand;

    public RelayCommand DownloadVideoCommand => _downloadVideoCommand ??= new RelayCommand(ExecuteDownloadVideoCommand);

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
    private RelayCommand? _downloadDanmakuCommand;

    public RelayCommand DownloadDanmakuCommand =>
        _downloadDanmakuCommand ??= new RelayCommand(ExecuteDownloadDanmakuCommand);

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
    private RelayCommand? _downloadSubtitleCommand;

    public RelayCommand DownloadSubtitleCommand =>
        _downloadSubtitleCommand ??= new RelayCommand(ExecuteDownloadSubtitleCommand);

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
    private RelayCommand? _downloadCoverCommand;

    public RelayCommand DownloadCoverCommand => _downloadCoverCommand ??= new RelayCommand(ExecuteDownloadCoverCommand);

    private RelayCommand? _generateMovieMetadataCommand;

    public RelayCommand GenerateMovieMetadataCommand =>
        _generateMovieMetadataCommand ??= new RelayCommand(ExecuteGenerateMovieMetadataCommand);

    private void ExecuteGenerateMovieMetadataCommand()
    {
        SetVideoContent();
    }

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

    // 选中文件名字段右键点击事件
    private RelayCommand<object>? _selectedFileNameRightCommand;

    public RelayCommand<object> SelectedFileNameRightCommand =>
        _selectedFileNameRightCommand ??=
            RequiredParameterCommand.Create<object>(ExecuteSelectedFileNameRightCommand);

    /// <summary>
    /// 选中文件名字段右键点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteSelectedFileNameRightCommand(object parameter)
    {
        if (parameter == null)
        {
            return;
        }

        var isSucceed = SelectedFileName.Remove((DisplayFileNamePart)parameter);
        if (!isSucceed)
        {
            PublishTip(isSucceed);
            return;
        }

        SelectedOptionalField = -1;
    }

    // 可选文件名字段点击事件
    private RelayCommand<object>? _optionalFieldsCommand;

    public RelayCommand<object> OptionalFieldsCommand =>
        _optionalFieldsCommand ??= RequiredParameterCommand.Create<object>(ExecuteOptionalFieldsCommand);

    /// <summary>
    /// 可选文件名字段点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteOptionalFieldsCommand(object parameter)
    {
        if (SelectedOptionalField == -1)
        {
            return;
        }

        SelectedFileName.Add((DisplayFileNamePart)parameter);

        var fileName = SelectedFileName.Select(item => item.Id).ToImmutableArray();
        var isSucceed = UpdateVideo(settings => settings with
        {
            FileNameParts = fileName
        }).FileNameParts.SequenceEqual(fileName);
        PublishTip(isSucceed);

        SelectedOptionalField = -1;
    }

    // 重置选中文件名字段
    private RelayCommand? _resetCommand;
    public RelayCommand ResetCommand => _resetCommand ??= new RelayCommand(ExecuteResetCommand);

    /// <summary>
    /// 重置选中文件名字段
    /// </summary>
    private void ExecuteResetCommand()
    {
        var updated = UpdateVideo(settings => settings with
        {
            FileNameParts = ApplicationSettingsDefaults.FileNameParts
        });
        var isSucceed = updated.FileNameParts.SequenceEqual(ApplicationSettingsDefaults.FileNameParts);
        PublishTip(isSucceed);

        var fileNameParts = updated.FileNameParts;
        SelectedFileName.Clear();
        foreach (var item in fileNameParts)
        {
            var display = DisplayFileNamePart(item);
            SelectedFileName.Add(new DisplayFileNamePart { Id = item, Title = display });
        }

        SelectedOptionalField = -1;
    }

    // 文件命名中的时间格式事件
    private RelayCommand<object>? _fileNamePartTimeFormatCommand;

    public RelayCommand<object> FileNamePartTimeFormatCommand =>
        _fileNamePartTimeFormatCommand ??=
            RequiredParameterCommand.Create<object>(ExecuteFileNamePartTimeFormatCommand);

    /// <summary>
    /// 文件命名中的时间格式事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteFileNamePartTimeFormatCommand(object parameter)
    {
        if (parameter is not string timeFormat)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with
        {
            FileNamePartTimeFormat = timeFormat
        }).FileNamePartTimeFormat == timeFormat;
        PublishTip(isSucceed);
    }

    // 文件命名中的序号格式事件
    private RelayCommand<object>? _orderFormatCommand;

    public RelayCommand<object> OrderFormatCommand =>
        _orderFormatCommand ??= RequiredParameterCommand.Create<object>(ExecuteOrderFormatCommandCommand);

    /// <summary>
    /// 文件命名中的序号格式事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteOrderFormatCommandCommand(object parameter)
    {
        if (parameter is not OrderFormatDisplay orderFormatDisplay)
        {
            return;
        }

        var isSucceed = UpdateVideo(settings => settings with
        {
            OrderFormat = orderFormatDisplay.OrderFormat
        }).OrderFormat == orderFormatDisplay.OrderFormat;
        PublishTip(isSucceed);
    }
}
