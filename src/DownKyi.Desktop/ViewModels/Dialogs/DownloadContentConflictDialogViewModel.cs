using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Services.Download;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Dialogs;

internal sealed record DownloadQualitySubstitutionItemViewModel(
    string PageName,
    string Description);

internal sealed class DownloadContentConflictDialogViewModel : BaseDialogViewModel
{
    private DownloadContentConflictPrompt? _prompt;
    private bool _hasQualitySubstitution;
    private bool _hasSingleQualitySubstitution;
    private bool _isQualitySubstitutionBatch;
    private bool _showApplyToAll = true;
    private string _applyToAllText = string.Empty;
    private string _affectedItemsHeader = string.Empty;
    private string _message = string.Empty;
    private string _qualitySubstitution = string.Empty;
    private IReadOnlyList<string> _qualitySubstitutionGroups = [];
    private IReadOnlyList<DownloadQualitySubstitutionItemViewModel> _qualitySubstitutionItems = [];
    private string _settingsHint = string.Empty;
    private string _skipPageText = string.Empty;
    private string _useAvailableContent = string.Empty;
    private bool _applyToAll;

    public DownloadContentConflictDialogViewModel()
    {
        Title = DictionaryResource.GetString("DownloadContentConflictTitle");
        UseAvailableContentCommand = new RelayCommand(() =>
            Close(DownloadContentConflictAction.UseAvailableContent));
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

    public bool HasSingleQualitySubstitution
    {
        get => _hasSingleQualitySubstitution;
        private set => SetProperty(ref _hasSingleQualitySubstitution, value);
    }

    public bool IsQualitySubstitutionBatch
    {
        get => _isQualitySubstitutionBatch;
        private set => SetProperty(ref _isQualitySubstitutionBatch, value);
    }

    public bool ShowApplyToAll
    {
        get => _showApplyToAll;
        private set => SetProperty(ref _showApplyToAll, value);
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

    public string AffectedItemsHeader
    {
        get => _affectedItemsHeader;
        private set => SetProperty(ref _affectedItemsHeader, value);
    }

    public IReadOnlyList<string> QualitySubstitutionGroups
    {
        get => _qualitySubstitutionGroups;
        private set => SetProperty(ref _qualitySubstitutionGroups, value);
    }

    public IReadOnlyList<DownloadQualitySubstitutionItemViewModel> QualitySubstitutionItems
    {
        get => _qualitySubstitutionItems;
        private set => SetProperty(ref _qualitySubstitutionItems, value);
    }

    public string SettingsHint
    {
        get => _settingsHint;
        private set => SetProperty(ref _settingsHint, value);
    }

    public string SkipPageText
    {
        get => _skipPageText;
        private set => SetProperty(ref _skipPageText, value);
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
        if (prompt.Items.Any(item => !item.Conflict.HasAvailableContent))
        {
            throw new InvalidOperationException(
                "A download content conflict prompt requires available content.");
        }

        _prompt = prompt;
        IsQualitySubstitutionBatch = prompt.Mode ==
                                     DownloadContentConflictPromptMode.QualitySubstitutionBatch;
        ShowApplyToAll = !IsQualitySubstitutionBatch;
        HasQualitySubstitution = prompt.Items.Any(item => item.Conflict.QualitySubstitutions.HasAny);
        HasSingleQualitySubstitution = HasQualitySubstitution && !IsQualitySubstitutionBatch;
        if (IsQualitySubstitutionBatch)
        {
            ConfigureQualitySubstitutionBatch(prompt);
            return;
        }

        var item = prompt.Items[0];
        var availableContent = DescribeContent(item.Conflict.AvailableContent);
        Message = DictionaryResource.GetString("DownloadContentConflictMessage")
            .Replace("{0}", item.PageName, StringComparison.Ordinal);
        ApplyToAllText = DictionaryResource.GetString(HasQualitySubstitution
            ? "ApplyToAllQualitySubstitution"
            : "ApplyToAllSameContentConflict");
        QualitySubstitution = DescribeQualitySubstitutions(
            item.Conflict.QualitySubstitutions);
        UseAvailableContent = DictionaryResource.GetString("UseAvailableDownloadContent")
            .Replace("{0}", availableContent, StringComparison.Ordinal);
        SkipPageText = DictionaryResource.GetString("SkipDownloadPage");
    }

    private void Close(DownloadContentConflictAction action)
    {
        if (_prompt == null)
        {
            throw new InvalidOperationException("Download content conflict was not initialized.");
        }

        CloseDialog(
            AppDialogOutcome.Accepted,
            DownloadContentConflictDialogContract.Encode(
                new DownloadContentConflictDecision(
                    action,
                    IsQualitySubstitutionBatch || ApplyToAll)));
    }

    private void ConfigureQualitySubstitutionBatch(DownloadContentConflictPrompt prompt)
    {
        var items = prompt.Items;
        Title = DictionaryResource.GetString("DownloadQualityBatchTitle");
        Message = DictionaryResource.GetString(prompt.AppliesToRemainingItems
                ? "DownloadQualityIncrementalBatchMessage"
                : "DownloadQualityBatchMessage")
            .Replace("{0}", items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        QualitySubstitutionItems = items.Select(item =>
                new DownloadQualitySubstitutionItemViewModel(
                    item.PageName,
                    DescribeQualitySubstitutions(item.Conflict.QualitySubstitutions)))
            .ToArray();
        QualitySubstitutionGroups = QualitySubstitutionItems
            .GroupBy(item => item.Description, StringComparer.Ordinal)
            .Select(group => DictionaryResource.GetString("DownloadQualityBatchGroup")
                .Replace("{0}", group.Key, StringComparison.Ordinal)
                .Replace(
                    "{1}",
                    group.Count().ToString(CultureInfo.CurrentCulture),
                    StringComparison.Ordinal))
            .ToArray();
        AffectedItemsHeader = DictionaryResource.GetString("DownloadQualityBatchAffectedItems")
            .Replace("{0}", items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        SettingsHint = DictionaryResource.GetString(prompt.AppliesToRemainingItems
            ? "DownloadQualityIncrementalBatchSettingsHint"
            : "DownloadQualityBatchSettingsHint");
        UseAvailableContent = prompt.AppliesToRemainingItems
            ? DictionaryResource.GetString("UseAvailableQualityIncrementalBatch")
            : DictionaryResource.GetString("UseAvailableQualityBatch")
                .Replace("{0}", items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        SkipPageText = prompt.AppliesToRemainingItems
            ? DictionaryResource.GetString("SkipQualityIncrementalBatch")
            : DictionaryResource.GetString("SkipQualityBatch")
                .Replace("{0}", items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
    }

    private static string DescribeContent(DownKyi.Domain.Downloads.DownloadContentSelection content)
    {
        var descriptions = new[]
        {
            content.Audio && content.Video
                ? DictionaryResource.GetString("DownloadAudioAndVideo")
                : content.Audio
                    ? DictionaryResource.GetString("DownloadAudio")
                    : content.Video
                        ? DictionaryResource.GetString("DownloadVideo")
                        : null,
            content.Subtitle ? DictionaryResource.GetString("DownloadSubtitle") : null,
            content.Danmaku ? DictionaryResource.GetString("DownloadDanmaku") : null,
            content.Cover ? DictionaryResource.GetString("DownloadCover") : null
        }.Where(static description => description != null);
        return string.Join(
            DictionaryResource.GetString("QualitySubstitutionSeparator"),
            descriptions);
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
