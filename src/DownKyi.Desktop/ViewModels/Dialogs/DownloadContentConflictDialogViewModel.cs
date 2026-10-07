using System;
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
        if (!prompt.Conflict.HasAvailableMedia)
        {
            throw new InvalidOperationException(
                "A download content conflict prompt requires available media.");
        }

        _conflict = prompt.Conflict;
        var media = DescribeMedia(prompt.Conflict.AvailableContent);
        Message = DictionaryResource.GetString("DownloadContentConflictMessage")
            .Replace("{0}", prompt.PageName, StringComparison.Ordinal);
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

    private static string DescribeMedia(DownKyi.Domain.Downloads.DownloadContentSelection content)
    {
        return (content.Audio, content.Video) switch
        {
            (true, true) => DictionaryResource.GetString("DownloadAudioAndVideo"),
            (true, false) => DictionaryResource.GetString("DownloadAudio"),
            (false, true) => DictionaryResource.GetString("DownloadVideo"),
            _ => throw new InvalidOperationException("Available media cannot be empty.")
        };
    }
}
