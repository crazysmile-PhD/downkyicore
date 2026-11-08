using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Commands;
using DownKyi.Images;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.UiState;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal sealed class ViewVideoDetailViewModel : ViewModelBase
{
    public const string Tag = "PageVideoDetail";

    private readonly IClipboardService _clipboardService;
    private readonly IVideoDetailDownloadCoordinator _downloadCoordinator;
    private readonly ILogger<ViewVideoDetailViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private readonly IVideoDetailWorkflowCoordinator _workflow;

    public ViewVideoDetailViewModel(
        IDesktopInteractionContext desktopInteractions,
        IClipboardService clipboardService,
        ISettingsStore settingsStore,
        IVideoDetailWorkflowCoordinator workflow,
        IVideoDetailDownloadCoordinator downloadCoordinator,
        ILogger<ViewVideoDetailViewModel> logger)
        : base(desktopInteractions)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var operationCommandGate = new DownKyiAsyncCommandGate();
        UiState.DownloadManage = CreateDownloadManageIcon();
        BackSpaceCommand = new RelayCommand(ExecuteBackSpace);
        DownloadManagerCommand = new RelayCommand(ExecuteDownloadManagerCommand);
        InputCommand = new DownKyiAsyncDelegateCommand(ExecuteInputCommandAsync, _logger, () => !UiState.IsBusy, executionGate: operationCommandGate);
        InputSearchCommand = new RelayCommand(() => _workflow.ApplySearch(UiState.InputSearchText));
        CopyCoverUrlCommand = new DownKyiAsyncDelegateCommand(ExecuteCopyCoverUrlCommandAsync, _logger);
        UpperCommand = new RelayCommand(ExecuteUpperCommand);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(UiState.IsSelectAll));
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(isSelected: false));
        AddToDownloadCommand = new DownKyiAsyncDelegateCommand(AddToDownloadAsync, _logger, () => !UiState.IsBusy, executionGate: operationCommandGate);
        UiState.IsBusyChanged += OnIsBusyChanged;
    }

    public VideoDetailUiState UiState { get; } = new();
    public RangeObservableCollection<VideoSection> VideoSections { get; } = new();

    public RelayCommand BackSpaceCommand { get; }
    public RelayCommand DownloadManagerCommand { get; }
    public DownKyiAsyncDelegateCommand InputCommand { get; }
    public ICommand InputSearchCommand { get; }
    public ICommand CopyCoverUrlCommand { get; }
    public RelayCommand UpperCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public DownKyiAsyncDelegateCommand AddToDownloadCommand { get; }

    private void OnIsBusyChanged()
    {
        InputCommand.NotifyCanExecuteChanged();
        AddToDownloadCommand.NotifyCanExecuteChanged();
    }

    protected internal override void ExecuteBackSpace()
    {
        _workflow.Cancel();
        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent();
    }

    private void ExecuteDownloadManagerCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.DownloadManager,
            ParentRoute));
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

        SetOperationStatus(
            VideoDetailOperationStatus.Running,
            StatusText("VideoDetailStatusLoading", "正在读取视频信息……"));

        await RunOperationAsync(operation, async () =>
        {
            if (BilibiliInputParser.Parse(requestedInput) is not BilibiliMediaInput input)
            {
                ApplyVideoDetailResult(VideoDetailParseResult.Empty, operation.CancellationToken);
                return;
            }

            _workflow.SetInput(input);
            UiState.InputText = input.CanonicalUri.AbsoluteUri;
            _logger.LogDebugMessage("Processing captured video input.");
            var result = await _workflow.LoadDetailAsync(operation).ConfigureAwait(true);
            await UiDispatcher.InvokeAsync(() => ApplyVideoDetailResult(result, operation.CancellationToken));
        }, VideoDetailDisplayState.Empty).ConfigureAwait(true);
    }

    private async Task ExecuteCopyCoverUrlCommandAsync()
    {
        if (UiState.VideoInfoView?.CoverUrl is not { } coverUrl)
        {
            return;
        }

        await _clipboardService.SetTextAsync(coverUrl).ConfigureAwait(true);
        _logger.LogInformationMessage("Video cover URL copied to the clipboard.");
    }

    private void ExecuteUpperCommand()
    {
        if (UiState.VideoInfoView != null)
        {
            var route = _settingsStore.Current.User.Mid == UiState.VideoInfoView.UpperMid
                ? AppRoute.MySpace
                : AppRoute.UserSpace;
            Navigation.Navigate(new AppNavigationRequest(
                route,
                AppRoute.VideoDetail,
                UiState.VideoInfoView.UpperMid));
        }
    }

    private void SetAllSelected(bool isSelected)
    {
        VideoSelectionState.SetAllSelected(VideoSelectionState.GetSelectedSection(VideoSections), isSelected);
        UiState.IsSelectAll = isSelected;
    }

    private async Task AddToDownloadAsync()
    {
        SetOperationStatus(
            VideoDetailOperationStatus.Running,
            StatusText("VideoDetailStatusAddingDownloads", "正在建立下载任务……"));
        if (UiState.VideoInfoView == null)
        {
            PublishAddResult(DownloadAddResult.Stopped(
                DownloadAddOutcome.Rejected,
                DownloadAddStopReason.NoPagesSelected));
            return;
        }

        var operation = _workflow.StartOperation();
        await RunOperationAsync(operation, async () =>
        {
            var input = _workflow.CurrentInput;
            if (input == null)
            {
                PublishAddResult(DownloadAddResult.Stopped(
                    DownloadAddOutcome.Rejected,
                    DownloadAddStopReason.UnsupportedInput));
                return;
            }

            var result = await _downloadCoordinator.AddAsync(
                input,
                UiState.VideoInfoView,
                VideoSections.ToList(),
                isAll: false,
                operation.CancellationToken).ConfigureAwait(true);
            if (_workflow.IsCurrent(operation))
            {
                PublishAddResult(result);
            }
        }, null).ConfigureAwait(true);
    }

    private void ResetView()
    {
        _workflow.Reset();
        UiState.GridResetVersion++;
        SetDisplayState(VideoDetailDisplayState.Busy);
        UiState.VideoInfoView = null;
        UiState.SelectedVideoPage = null;
        VideoSections.ReplaceRange(Array.Empty<VideoSection>());
        SetOperationStatus(VideoDetailOperationStatus.None, string.Empty);
    }

    private void ApplyVideoDetailResult(VideoDetailParseResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Outcome == VideoDetailParseOutcome.NoContent)
        {
            UiState.VideoInfoView = null;
            VideoSections.ReplaceRange(Array.Empty<VideoSection>());
            SetDisplayState(VideoDetailDisplayState.Empty);
            SetOperationStatus(
                VideoDetailOperationStatus.Error,
                StatusText(
                    "VideoDetailStatusNoContent",
                    "没有找到可解析的视频内容。"));
            return;
        }

        UiState.VideoInfoView = result.VideoInfoView;
        VideoSections.ReplaceRange(result.VideoSections);
        UiState.SelectedVideoPage = _workflow.CurrentInput is { } input
            ? VideoSelectionState.SelectInputPage(VideoSections, input)
            : null;
        SetDisplayState(VideoDetailDisplayState.Content);
        SetOperationStatus(
            VideoDetailOperationStatus.Success,
            StatusText("VideoDetailStatusLoaded", "视频信息已载入，可选择内容并下载。"));
    }

    private void PublishAddResult(DownloadAddResult result)
    {
        var notification = DownloadAddNotificationFormatter.Create(result);
        Notifications.Show(notification.Message);
        SetOperationStatus(notification.Kind switch
        {
            DownloadAddNotificationKind.Success => VideoDetailOperationStatus.Success,
            DownloadAddNotificationKind.Warning => VideoDetailOperationStatus.Warning,
            DownloadAddNotificationKind.Canceled => VideoDetailOperationStatus.Canceled,
            DownloadAddNotificationKind.Error => VideoDetailOperationStatus.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(result), notification.Kind, null)
        }, notification.Message);
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

        _logger.LogErrorMessage("Video detail operation failed.", exception);
        Notifications.Show(exception.Message);
        SetOperationStatus(VideoDetailOperationStatus.Error, exception.Message);
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
        catch (DownloadAddCanceledException exception)
        {
            if (_workflow.IsCurrent(operation))
            {
                RestoreDisplayState();
                PublishAddResult(exception.PartialResult);
            }
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            if (_workflow.IsCurrent(operation))
            {
                RestoreDisplayState();
                SetOperationStatus(
                    VideoDetailOperationStatus.Canceled,
                    StatusText("VideoDetailStatusCanceled", "操作已取消。"));
            }
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            HandleOperationError(exception, operation, failureState);
        }
        finally
        {
            SettleIncompleteOperation(operation, failureState);
        }
    }

    private void SettleIncompleteOperation(
        VideoDetailOperation operation,
        VideoDetailDisplayState? failureState)
    {
        if (!_workflow.IsCurrent(operation)
            || UiState.OperationStatus != VideoDetailOperationStatus.Running)
        {
            return;
        }

        if (failureState is { } state)
        {
            SetDisplayState(state);
        }
        else
        {
            RestoreDisplayState();
        }

        SetOperationStatus(
            VideoDetailOperationStatus.Error,
            StatusText("VideoDetailStatusFailed", "操作失败，请查看日志。"));
    }

    private static bool IsExpectedOperationException(Exception exception)
    {
        return exception is System.Net.Http.HttpRequestException or InvalidOperationException
            or ArgumentException or FormatException or RegexMatchTimeoutException
            or Newtonsoft.Json.JsonException or System.IO.IOException
            or UnauthorizedAccessException;
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

    private void SetOperationStatus(VideoDetailOperationStatus status, string message)
    {
        void Apply()
        {
            UiState.OperationStatusIcon = status switch
            {
                VideoDetailOperationStatus.Error => SystemIcon.Current.Error,
                VideoDetailOperationStatus.Warning => SystemIcon.Current.Warning,
                _ => SystemIcon.Current.Info
            };
            UiState.OperationStatusText = message;
            UiState.OperationStatus = status;
        }

        if (UiDispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            UiDispatcher.Post(Apply);
        }
    }

    private static string StatusText(
        string resourceKey,
        string fallback,
        params object[] arguments)
    {
        var format = DictionaryResource.GetString(resourceKey);
        if (string.IsNullOrWhiteSpace(format))
        {
            format = fallback;
        }

        return arguments.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, arguments);
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
        var icon = ButtonIcon.DownloadManage;
        icon.Height = 24;
        icon.Width = 24;
        return icon;
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        UiState.DownloadManage = CreateDownloadManageIcon();
        if (navigationContext.Parameter is VideoNavigationPayload payload)
        {
            var input = payload.Input.CanonicalUri.AbsoluteUri;
            if (UiState.InputText != input || !payload.SuppressDuplicate)
            {
                if (InputCommand.CanExecute(null))
                {
                    UiState.InputText = input;
                    InputCommand.Execute(null);
                }
            }
        }

        base.OnNavigatedTo(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            UiState.IsBusyChanged -= OnIsBusyChanged;
            _workflow.Dispose();
        }

        base.Dispose(disposing);
    }
}
