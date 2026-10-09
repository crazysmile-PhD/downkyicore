using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Services.Download;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class DownloadContentConflictDialogViewModel : BaseDialogViewModel
{
    private DownloadContentConflict? _conflict;
    private bool _hasQualitySubstitution;
    private string _applyToAllText = string.Empty;
    private string _message = string.Empty;
    private string _qualitySubstitution = string.Empty;
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

    public bool HasQualitySubstitution
    {
        get => _hasQualitySubstitution;
        private set => SetProperty(ref _hasQualitySubstitution, value);
    }

    public string QualitySubstitution
    {
        get => _qualitySubstitution;
        private set => SetProperty(ref _qualitySubstitution, value);
    }

    public bool ApplyToAll
    {
        get => _applyToAll;
        set => SetProperty(ref _applyToAll, value);
    }

    public string ApplyToAllText
    {
        get => _applyToAllText;
        private set => SetProperty(ref _applyToAllText, value);
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
        HasQualitySubstitution = prompt.Conflict.QualitySubstitutions.HasAny;
        ApplyToAllText = DictionaryResource.GetString(HasQualitySubstitution
            ? "ApplyToAllQualitySubstitution"
            : "ApplyToAllSameContentConflict");
        QualitySubstitution = DescribeQualitySubstitutions(
            prompt.Conflict.QualitySubstitutions);
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

    private static string DescribeQualitySubstitutions(
        DownloadQualitySubstitutions substitutions)
    {
        ArgumentNullException.ThrowIfNull(substitutions);
        return string.Join(
            DictionaryResource.GetString("QualitySubstitutionSeparator"),
            new[]
            {
                DescribeQualitySubstitution("VideoQualitySubstitution", substitutions.Video),
                DescribeQualitySubstitution("AudioQualitySubstitution", substitutions.Audio)
            }.Where(description => !string.IsNullOrEmpty(description)));
    }

    private static string DescribeQualitySubstitution(
        string resourceKey,
        DownloadQualitySubstitution? substitution)
    {
        return substitution switch
        {
            null => string.Empty,
            _ => DictionaryResource.GetString(resourceKey)
                .Replace("{0}", substitution.RequestedName, StringComparison.Ordinal)
                .Replace("{1}", substitution.SelectedName, StringComparison.Ordinal)
        };
    }
}
