using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Services.Download;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class DownloadContentConflictDialogViewModel : BaseDialogViewModel
{
    private DownloadContentConflict? _conflict;
    private string _message = string.Empty;
    private string _useAvailableContent = string.Empty;
    private bool _applyToAll;

    public DownloadContentConflictDialogViewModel()
    {
        Title = DictionaryResource.GetString("DownloadContentConflictTitle");
        UseAvailableContentCommand = new RelayCommand(() =>
            Close(DownloadContentConflictAction.UseAvailableMedia));
        SkipPageCommand = new RelayCommand(() =>
            Close(DownloadContentConflictAction.SkipPage));
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string UseAvailableContent
    {
        get => _useAvailableContent;
        private set => SetProperty(ref _useAvailableContent, value);
    }

    public bool ApplyToAll
    {
        get => _applyToAll;
        set => SetProperty(ref _applyToAll, value);
    }

    public RelayCommand UseAvailableContentCommand { get; }

    public RelayCommand SkipPageCommand { get; }

    public override bool CanCloseDialog()
    {
        return false;
    }

    public override void OnDialogOpened(AppDialogRequest request)
    {
        var prompt = GetRequiredParameter<DownloadContentConflictPrompt>(
            request,
            DownloadContentConflictDialogContract.PromptParameter);
        if (!prompt.Conflict.HasAvailableContent)
        {
            throw new InvalidOperationException(
                "A download content conflict prompt requires available content.");
        }

        _conflict = prompt.Conflict;
        var media = DescribeContent(prompt.Conflict.AvailableContent);
        var qualityChanges = new List<string>();
        if (prompt.Conflict.UsesLowerVideoQuality)
        {
            qualityChanges.Add($"畫質：{prompt.Conflict.AvailableMedia.LowerVideoQuality}");
        }

        if (prompt.Conflict.UsesLowerAudioQuality)
        {
            qualityChanges.Add($"音質：{prompt.Conflict.AvailableMedia.LowerAudioQuality}");
        }

        Message = DictionaryResource.GetString("DownloadContentConflictMessage")
            .Replace("{0}", prompt.PageName, StringComparison.Ordinal);
        if (prompt.ApiFailure != null)
        {
            Message += $" API 查詢失敗：{prompt.ApiFailure}。";
        }

        if (qualityChanges.Count > 0)
        {
            Message += $" 可用替代品質：{string.Join("、", qualityChanges)}。";
        }

        UseAvailableContent = DictionaryResource.GetString("UseAvailableDownloadContent")
            .Replace("{0}", media, StringComparison.Ordinal);
    }

    private void Close(DownloadContentConflictAction action)
    {
        if (_conflict == null)
        {
            throw new InvalidOperationException("Download content conflict was not initialized.");
        }

        CloseDialog(
            AppDialogOutcome.Accepted,
            DownloadContentConflictDialogContract.Encode(
                new DownloadContentConflictDecision(action, ApplyToAll)));
    }

    private static string DescribeContent(DownKyi.Domain.Downloads.DownloadContentSelection content)
    {
        var items = new List<string>();
        var media = (content.Audio, content.Video) switch
        {
            (true, true) => DictionaryResource.GetString("DownloadAudioAndVideo"),
            (true, false) => DictionaryResource.GetString("DownloadAudio"),
            (false, true) => DictionaryResource.GetString("DownloadVideo"),
            _ => null
        };
        if (media != null)
        {
            items.Add(media);
        }

        if (content.Danmaku) items.Add(DictionaryResource.GetString("DownloadDanmaku"));
        if (content.Subtitle) items.Add(DictionaryResource.GetString("DownloadSubtitle"));
        if (content.Cover) items.Add(DictionaryResource.GetString("DownloadCover"));
        return string.Join("、", items);
    }
}
