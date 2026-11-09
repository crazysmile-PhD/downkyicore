using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Settings;
using DownKyi.Commands;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Settings;

internal partial class ViewVideoViewModel
{
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

        var commit = await _settingsStore.CommitAsync(settings => settings with
        {
            Video = settings.Video with { SaveVideoRootPath = directory }
        }).ConfigureAwait(true);
        var isSucceed = commit.Succeeded
                        && commit.Settings.Video.SaveVideoRootPath == directory;
        PublishTip(isSucceed);

        if (isSucceed)
        {
            SaveVideoDirectory = commit.Settings.Video.SaveVideoRootPath;
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

    private RelayCommand? _contentSelectionChangedCommand;

    public RelayCommand ContentSelectionChangedCommand =>
        _contentSelectionChangedCommand ??= new RelayCommand(ExecuteContentSelectionChangedCommand);

    private void ExecuteContentSelectionChangedCommand()
    {
        DownloadAll = DownloadAudio && DownloadVideo && DownloadDanmaku && DownloadSubtitle && DownloadCover;
        SetVideoContent();
    }

    private RelayCommand? _subtitlePreferenceCommand;

    public RelayCommand SubtitlePreferenceCommand =>
        _subtitlePreferenceCommand ??= new RelayCommand(ExecuteSubtitlePreferenceCommand);

    private void ExecuteSubtitlePreferenceCommand()
    {
        if (!_isOnNavigatedTo)
        {
            SetVideoContent();
        }
    }

    private RelayCommand? _generateMovieMetadataCommand;

    public RelayCommand GenerateMovieMetadataCommand =>
        _generateMovieMetadataCommand ??= new RelayCommand(ExecuteGenerateMovieMetadataCommand);

    private void ExecuteGenerateMovieMetadataCommand()
    {
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
