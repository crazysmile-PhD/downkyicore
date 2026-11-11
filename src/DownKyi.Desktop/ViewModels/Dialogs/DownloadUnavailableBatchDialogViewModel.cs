using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Services.Download;
using DownKyi.Utils;

namespace DownKyi.ViewModels.Dialogs;

internal sealed record DownloadUnavailableItemViewModel(
    string DisplayName,
    string Source,
    string Reason,
    string TechnicalDetail);

internal sealed class DownloadUnavailableBatchDialogViewModel : BaseDialogViewModel
{
    private string _message = string.Empty;
    private string _affectedItemsHeader = string.Empty;
    private string _skipText = string.Empty;
    private string _cancelText = string.Empty;
    private IReadOnlyList<DownloadUnavailableItemViewModel> _items = [];

    public DownloadUnavailableBatchDialogViewModel()
    {
        Title = DictionaryResource.GetString("DownloadUnavailableBatchTitle");
        SkipCommand = new RelayCommand(() => Close(DownloadUnavailableBatchAction.SkipUnavailable));
        CancelCommand = new RelayCommand(() => Close(DownloadUnavailableBatchAction.CancelBatch));
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string AffectedItemsHeader
    {
        get => _affectedItemsHeader;
        private set => SetProperty(ref _affectedItemsHeader, value);
    }

    public string SkipText
    {
        get => _skipText;
        private set => SetProperty(ref _skipText, value);
    }

    public string CancelText
    {
        get => _cancelText;
        private set => SetProperty(ref _cancelText, value);
    }

    public IReadOnlyList<DownloadUnavailableItemViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public RelayCommand SkipCommand { get; }

    public RelayCommand CancelCommand { get; }

    public override bool CanCloseDialog() => false;

    public override void OnDialogOpened(AppDialogRequest request)
    {
        var prompt = GetRequiredParameter<DownloadUnavailableBatchPrompt>(
            request,
            DownloadUnavailableBatchDialogContract.PromptParameter);
        var count = prompt.Items.Count.ToString(CultureInfo.CurrentCulture);
        Message = Format("DownloadUnavailableBatchMessage", count);
        AffectedItemsHeader = Format("DownloadUnavailableBatchAffectedItems", count);
        SkipText = Format("SkipUnavailableBatch", count);
        CancelText = DictionaryResource.GetString("CancelUnavailableBatch");
        Items = prompt.Items.Select(item => new DownloadUnavailableItemViewModel(
                item.DisplayName,
                item.Source,
                DescribeReason(item.Reason),
                item.ApiCode is { } code
                    ? Format("DownloadUnavailableApiCode", code.ToString(CultureInfo.InvariantCulture))
                    : string.Empty))
            .ToArray();
    }

    private void Close(DownloadUnavailableBatchAction action) =>
        CloseDialog(
            AppDialogOutcome.Accepted,
            DownloadUnavailableBatchDialogContract.Encode(action));

    private static string DescribeReason(DownloadUnavailableReason reason) =>
        DictionaryResource.GetString(reason switch
        {
            DownloadUnavailableReason.AccountRequired => "DownloadUnavailableReasonAccountRequired",
            DownloadUnavailableReason.AccessRestrictedOrUnavailable =>
                "DownloadUnavailableReasonAccessRestrictedOrUnavailable",
            DownloadUnavailableReason.VideoUnavailable => "DownloadUnavailableReasonVideoUnavailable",
            DownloadUnavailableReason.NoPlaybackResource => "DownloadUnavailableReasonNoPlaybackResource",
            DownloadUnavailableReason.NetworkFailure => "DownloadUnavailableReasonNetworkFailure",
            DownloadUnavailableReason.RequestTimedOut => "DownloadUnavailableReasonRequestTimedOut",
            DownloadUnavailableReason.InvalidResponse => "DownloadUnavailableReasonInvalidResponse",
            DownloadUnavailableReason.ApiRejected => "DownloadUnavailableReasonApiRejected",
            DownloadUnavailableReason.NoDownloadData => "DownloadUnavailableReasonNoDownloadData",
            DownloadUnavailableReason.Unknown => "DownloadUnavailableReasonUnknown",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        });

    private static string Format(string resourceKey, string value) =>
        DictionaryResource.GetString(resourceKey)
            .Replace("{0}", value, StringComparison.Ordinal);
}
