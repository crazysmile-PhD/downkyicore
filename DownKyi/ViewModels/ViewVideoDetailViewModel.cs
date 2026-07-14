using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.Services;
using DownKyi.Services.Video;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.ViewModels.UiState;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;
using Prism.Navigation.Regions;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels;

internal sealed class ViewVideoDetailViewModel : ViewModelBase
{
    public const string Tag = "PageVideoDetail";

    private readonly IClipboardService _clipboardService;
    private readonly IVideoDetailDownloadCoordinator _downloadCoordinator;
    private readonly ISettingsStore _settingsStore;
    private readonly IVideoDetailWorkflowCoordinator _workflow;

    public ViewVideoDetailViewModel(
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        IClipboardService clipboardService,
        ISettingsStore settingsStore,
        IVideoDetailWorkflowCoordinator workflow,
        IVideoDetailDownloadCoordinator downloadCoordinator)
        : base(eventAggregator, dialogService)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        UiState.DownloadManage = CreateDownloadManageIcon();
        BackSpaceCommand = new DelegateCommand(ExecuteBackSpace);
        DownloadManagerCommand = new DelegateCommand(ExecuteDownloadManagerCommand);
        InputCommand = new DownKyiAsyncDelegateCommand(ExecuteInputCommandAsync, () => !UiState.IsBusy);
        InputSearchCommand = new DelegateCommand(() => _workflow.ApplySearch(UiState.InputSearchText));
        CopyCoverUrlCommand = new DownKyiAsyncDelegateCommand(ExecuteCopyCoverUrlCommandAsync);
        UpperCommand = new DelegateCommand(ExecuteUpperCommand);
        SelectAllCommand = new DelegateCommand(() => SetAllSelected(UiState.IsSelectAll));
        ClearSelectionCommand = new DelegateCommand(() => SetAllSelected(isSelected: false));
        ParseCommand = new DownKyiAsyncDelegateCommand<object>(ExecuteParseCommandAsync, _ => !UiState.IsBusy);
        ParseAllVideoCommand = new DownKyiAsyncDelegateCommand(ExecuteParseAllVideoCommandAsync, () => !UiState.IsBusy);
        AddToDownloadCommand = new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(false), () => !UiState.IsBusy);
    }

    public VideoDetailUiState UiState { get; } = new();

    public RangeObservableCollection<VideoSection> VideoSections { get; } = new();

    public DelegateCommand BackSpaceCommand { get; }
    public DelegateCommand DownloadManagerCommand { get; }
    public ICommand InputCommand { get; }
    public ICommand InputSearchCommand { get; }
    public ICommand CopyCoverUrlCommand { get; }
    public DelegateCommand UpperCommand { get; }
    public DelegateCommand SelectAllCommand { get; }
    public DelegateCommand ClearSelectionCommand { get; }
    public ICommand ParseCommand { get; }
    public ICommand ParseAllVideoCommand { get; }
    public ICommand AddToDownloadCommand { get; }

    protected internal override void ExecuteBackSpace()
    {
        _workflow.Cancel();
        if (TryNavigateBack())
        {
            return;
        }

        EventAggregator.GetEvent<NavigationEvent>().Publish(new NavigationParam
        {
            ViewName = string.IsNullOrWhiteSpace(ParentView) ? ViewIndexViewModel.Tag : ParentView
        });
    }

    private void ExecuteDownloadManagerCommand()
    {
        EventAggregator.GetEvent<NavigationEvent>().Publish(new NavigationParam
        {
            ViewName = ViewDownloadManagerViewModel.Tag,
            ParentViewName = string.IsNullOrWhiteSpace(ParentView) ? ViewIndexViewModel.Tag : ParentView
        });
    }

    private Task ExecuteInputCommandAsync()
    {
        return ExecuteInputCommandAsync(UiState.InputText);
    }

    private async Task ExecuteInputCommandAsync(string? requestedInput)
    {
        var operation = _workflow.StartOperation();
        ResetView();
        if (string.IsNullOrWhiteSpace(requestedInput))
        {
            SetDisplayState(VideoDetailDisplayState.Idle);
            return;
        }

        await RunOperationAsync(operation, async () =>
        {
            UiState.InputText = _workflow.SetInput(requestedInput);
            LogManager.Debug(Tag, "Processing captured video input.");
            var result = await _workflow.LoadDetailAsync(operation).ConfigureAwait(true);
            await UiDispatcher.InvokeAsync(() => ApplyVideoDetailResult(result, operation.CancellationToken));
            if (_workflow.IsCurrent(operation) && _settingsStore.Settings.GetIsAutoParseVideo() == AllowStatus.Yes)
            {
                RunFireAndForget(ExecuteParseAllVideoCommandAsync(), nameof(ExecuteParseAllVideoCommandAsync));
            }
        }, VideoDetailDisplayState.Empty).ConfigureAwait(true);
    }

    private async Task ExecuteCopyCoverUrlCommandAsync()
    {
        if (UiState.VideoInfoView?.CoverUrl is not { } coverUrl)
        {
            return;
        }

        await _clipboardService.SetTextAsync(coverUrl).ConfigureAwait(true);
        LogManager.Info(Tag, "复制封面url到剪贴板");
    }

    private void ExecuteUpperCommand()
    {
        if (UiState.VideoInfoView != null)
        {
            NavigateToView.NavigateToViewUserSpace(EventAggregator, Tag, UiState.VideoInfoView.UpperMid);
        }
    }

    private void SetAllSelected(bool isSelected)
    {
        VideoSelectionState.SetAllSelected(VideoSelectionState.GetSelectedSection(VideoSections), isSelected);
        UiState.IsSelectAll = isSelected;
    }

    private async Task ExecuteParseCommandAsync(object? parameter)
    {
        if (parameter is not VideoPage page)
        {
            return;
        }

        var operation = _workflow.StartOperation();
        SetDisplayState(VideoDetailDisplayState.Busy);
        await RunOperationAsync(operation, async () =>
        {
            var result = await _workflow.LoadPageStreamAsync(page, operation).ConfigureAwait(true);
            if (result != null)
            {
                await UiDispatcher.InvokeAsync(() => ApplyVideoStreamResults([result], operation.CancellationToken));
            }

            RestoreDisplayStateIfCurrent(operation);
        }, null).ConfigureAwait(true);
    }

    private async Task ExecuteParseAllVideoCommandAsync()
    {
        var parseScope = _settingsStore.Settings.GetParseScope();
        if (parseScope != ParseScope.None)
        {
            await ExecuteParseAsync(parseScope).ConfigureAwait(true);
            return;
        }

        if (DialogService != null)
        {
            await DialogService.ShowDialogAsync(ViewParsingSelectorViewModel.Tag, null, async result =>
            {
                if (result.Result == ButtonResult.OK)
                {
                    await ExecuteParseAsync(result.Parameters.GetValue<ParseScope>("parseScope")).ConfigureAwait(true);
                }
            }).ConfigureAwait(true);
        }
    }

    private async Task ExecuteParseAsync(ParseScope parseScope)
    {
        var operation = _workflow.StartOperation();
        SetDisplayState(VideoDetailDisplayState.Busy);
        await RunOperationAsync(operation, async () =>
        {
            var results = await _workflow
                .LoadPageStreamsAsync(VideoSections, parseScope, operation)
                .ConfigureAwait(true);
            await UiDispatcher.InvokeAsync(() => ApplyVideoStreamResults(results, operation.CancellationToken));
            if (!_workflow.IsCurrent(operation))
            {
                return;
            }

            RestoreDisplayState();
            if (parseScope != ParseScope.None && _settingsStore.Settings.GetIsAutoDownloadAll() == AllowStatus.Yes)
            {
                await AddToDownloadAsync(true).ConfigureAwait(true);
            }

            LogManager.Debug(Tag, $"ParseScope: {parseScope:G}");
        }, null).ConfigureAwait(true);
    }

    private async Task AddToDownloadAsync(bool isAll)
    {
        if (UiState.VideoInfoView == null)
        {
            PublishAddedCount(0);
            return;
        }

        var operation = _workflow.StartOperation();
        try
        {
            var addedCount = await _downloadCoordinator.AddAsync(
                _workflow.CurrentInput,
                UiState.VideoInfoView,
                VideoSections.ToList(),
                isAll,
                EventAggregator,
                DialogService,
                operation.CancellationToken).ConfigureAwait(true);
            if (addedCount is { } count && _workflow.IsCurrent(operation))
            {
                PublishAddedCount(count);
            }
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ResetView()
    {
        _workflow.Reset();
        UiState.GridResetVersion++;
        SetDisplayState(VideoDetailDisplayState.Busy);
        UiState.VideoInfoView = null;
        UiState.SelectedVideoPage = null;
        VideoSections.ReplaceRange(Array.Empty<VideoSection>());
    }

    private void ApplyVideoDetailResult(VideoDetailParseResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UiState.VideoInfoView = result.VideoInfoView;
        if (UiState.VideoInfoView == null)
        {
            SetDisplayState(VideoDetailDisplayState.Empty);
            return;
        }

        VideoSections.ReplaceRange(result.VideoSections);
        UiState.SelectedVideoPage = VideoSelectionState.SelectInputPage(VideoSections, _workflow.CurrentInput);
        SetDisplayState(VideoDetailDisplayState.Content);
    }

    private static void ApplyVideoStreamResults(
        IReadOnlyList<VideoStreamParseResult> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var result in results)
        {
            Services.Utils.VideoPageInfo(result.PlayUrl, result.Page);
        }
    }

    private void PublishAddedCount(int count)
    {
        var message = count <= 0
            ? DictionaryResource.GetString("TipAddDownloadingZero")
            : $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}{count}{DictionaryResource.GetString("TipAddDownloadingFinished2")}";
        EventAggregator.GetEvent<MessageEvent>().Publish(message);
    }

    private void HandleOperationError(
        Exception exception,
        VideoDetailOperation operation,
        VideoDetailDisplayState? failureState)
    {
        if (!_workflow.IsCurrent(operation))
        {
            return;
        }

        LogManager.Error(Tag, exception);
        EventAggregator.GetEvent<MessageEvent>().Publish(exception.Message);
        if (failureState is { } state)
        {
            SetDisplayState(state);
        }
        else
        {
            RestoreDisplayState();
        }
    }

    private async Task RunOperationAsync(
        VideoDetailOperation operation,
        Func<Task> action,
        VideoDetailDisplayState? failureState)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            RestoreDisplayStateIfCurrent(operation);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            HandleOperationError(exception, operation, failureState);
        }
    }

    private static bool IsExpectedOperationException(Exception exception)
    {
        return exception is System.Net.Http.HttpRequestException or InvalidOperationException
            or ArgumentException or FormatException or RegexMatchTimeoutException
            or Newtonsoft.Json.JsonException;
    }

    private void RestoreDisplayStateIfCurrent(VideoDetailOperation operation)
    {
        if (_workflow.IsCurrent(operation))
        {
            RestoreDisplayState();
        }
    }

    private void RestoreDisplayState()
    {
        SetDisplayState(UiState.VideoInfoView == null
            ? VideoDetailDisplayState.Idle
            : VideoDetailDisplayState.Content);
    }

    private void SetDisplayState(VideoDetailDisplayState state)
    {
        if (UiDispatcher.CheckAccess())
        {
            UiState.DisplayState = state;
        }
        else
        {
            UiDispatcher.Post(() => UiState.DisplayState = state);
        }
    }

    private static VectorImage CreateDownloadManageIcon()
    {
        var icon = ButtonIcon.Instance().DownloadManage;
        icon.Height = 24;
        icon.Width = 24;
        icon.Fill = DictionaryResource.GetColor("ColorPrimary");
        return icon;
    }

    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        UiState.DownloadManage = CreateDownloadManageIcon();
        if (navigationContext.Parameters.GetValue<string>("Parent") is not null)
        {
            var parameter = navigationContext.Parameters.GetValue<string>("Parameter");
            var input = parameter.Replace(AppConstant.ClipboardId, string.Empty, StringComparison.Ordinal);
            if (UiState.InputText != input || !parameter.EndsWith(AppConstant.ClipboardId, StringComparison.Ordinal))
            {
                if (!UiState.IsBusy)
                {
                    UiState.InputText = input;
                    RunFireAndForget(ExecuteInputCommandAsync(input), nameof(ExecuteInputCommandAsync));
                }
            }
        }

        base.OnNavigatedTo(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _workflow.Dispose();
        }

        base.Dispose(disposing);
    }
}
