using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Services;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly IAppDialogService _dialogService;
    private readonly IAppNavigationService _navigationService;
    private readonly IUserNotificationService _notificationService;
    private readonly ISettingsStore _settingsStore;
    private readonly IClipboardMonitor _clipboardMonitor;
    private readonly SearchService _searchService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private bool _messageVisibility;
    private string? _oldMessage;
    private CancellationTokenSource? _messageCancellation;
    private CancellationTokenSource? _clipboardDebounceCancellation;

    private object? _mainContent;

    public object? MainContent
    {
        get => _mainContent;
        private set => SetProperty(ref _mainContent, value);
    }

    public bool MessageVisibility
    {
        get => _messageVisibility;
        set => SetProperty(ref _messageVisibility, value);
    }

    private string? _message;

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public RelayCommand? LoadedCommand { get; }

    private RelayCommand? _closingCommand;

    public RelayCommand ClosingCommand => _closingCommand ??= new RelayCommand(ExecuteClosingCommand);

    private RelayCommand<PointerPressedEventArgs>? _pointerPressedCommand;

    public RelayCommand<PointerPressedEventArgs> PointerPressedCommand =>
        _pointerPressedCommand ??= RequiredParameterCommand.Create<PointerPressedEventArgs>(ExecutePointerPressed);

    private void ExecuteClosingCommand()
    {
        Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        _lifetimeCancellation.Cancel();

        _notificationService.NotificationRaised -= NotificationServiceOnNotificationRaised;
        _clipboardMonitor.Changed -= ClipboardMonitorOnChanged;
        _navigationService.NavigationChanged -= NavigationServiceOnNavigationChanged;

        _clipboardDebounceCancellation?.Cancel();
        _clipboardDebounceCancellation?.Dispose();
        _clipboardDebounceCancellation = null;
        _messageCancellation?.Cancel();
        _messageCancellation?.Dispose();
        _messageCancellation = null;
        _lifetimeCancellation.Dispose();
    }

    private void ExecutePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        var updateKind = point.Properties.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.XButton1Pressed)
        {
            if (_navigationService.GetActiveView(AppNavigationRegion.Main) is ViewModelBase vm)
            {
                vm.ExecuteBackSpace();
                e.Handled = true;
            }
        }
    }

    public MainWindowViewModel(
        IAppNavigationService navigationService,
        IUserNotificationService notificationService,
        IAppDialogService dialogService,
        ISettingsStore settingsStore,
        IClipboardMonitor clipboardMonitor,
        SearchService searchService,
        ILogger<MainWindowViewModel> logger)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _clipboardMonitor = clipboardMonitor ?? throw new ArgumentNullException(nameof(clipboardMonitor));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        #region MyRegion

        // 订阅消息发送事件
        _notificationService.NotificationRaised += NotificationServiceOnNotificationRaised;
        _navigationService.NavigationChanged += NavigationServiceOnNavigationChanged;
        MainContent = _navigationService.GetActiveView(AppNavigationRegion.Main);

        #endregion


        LoadedCommand = new RelayCommand(() =>
        {
            if (Design.IsDesignMode)
            {
                return;
            }
            Upgrade();
            _ = CheckForUpdatesAsync();
            _clipboardMonitor.Changed -= ClipboardMonitorOnChanged;
            _clipboardMonitor.Changed += ClipboardMonitorOnChanged;
            _navigationService.Navigate(new AppNavigationRequest(
                AppRoute.Index,
                Parameter: "start"));
        });
    }

    private void NotificationServiceOnNotificationRaised(object? sender, UserNotificationEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            MessageVisibility = true;

            _oldMessage = Message;
            Message = e.Message;
            var delay = _oldMessage == Message ? 1500 : 2000;

            _messageCancellation?.Cancel();
            _messageCancellation?.Dispose();
            _messageCancellation = new CancellationTokenSource();
            _ = HideMessageAfterDelayAsync(delay, _messageCancellation.Token);
        });
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(true);
    }

    private async Task HideMessageAfterDelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await DelayAsync(milliseconds, cancellationToken).ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    MessageVisibility = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    #region 剪贴板

    private void ClipboardMonitorOnChanged(object? sender, ClipboardTextChangedEventArgs e)
    {
        var isListenClipboard = _settingsStore.Current.Basic.IsListenClipboard;
        if (isListenClipboard != AllowStatus.Yes)
        {
            return;
        }

        _clipboardDebounceCancellation?.Cancel();
        _clipboardDebounceCancellation?.Dispose();
        _clipboardDebounceCancellation = new CancellationTokenSource();
        _ = HandleClipboardChangedAsync(e.Text, _clipboardDebounceCancellation.Token);
    }

    private async Task HandleClipboardChangedAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _searchService.BiliInput(text + AppConstant.ClipboardId, AppRoute.Index);
            }
        });
    }

    private void NavigationServiceOnNavigationChanged(object? sender, AppNavigationChangedEventArgs e)
    {
        if (e.Region != AppNavigationRegion.Main)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            MainContent = e.Content;
            return;
        }

        Dispatcher.UIThread.Post(() => MainContent = e.Content);
    }

    #endregion

    private void Upgrade()
    {
        _ = ShowUpgradeDialogAsync();
    }

    private async Task ShowUpgradeDialogAsync()
    {
        try
        {
            await _dialogService
                .ShowAsync(new AppDialogRequest(AppDialog.LegacyUpgrade), _lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException e)
        {
            _logger.LogErrorMessage("Legacy upgrade dialog failed to open.", e);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var about = _settingsStore.Current.About;
            var isAutoUpdate = about.AutoUpdateWhenLaunch != AllowStatus.Yes;
            if (isAutoUpdate) return;
            var service = new VersionCheckerService(AppConstant.RepoOwner, AppConstant.RepoName,
                about.IsReceiveBetaVersion == AllowStatus.Yes);
            var release = await service
                .GetLatestReleaseAsync(about.SkipVersionOnLaunch, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (release != null && service.IsNewVersionAvailable(release.TagName))
            {
                await _dialogService.ShowAsync(new AppDialogRequest(
                    AppDialog.NewVersionAvailable,
                    new Dictionary<string, object?>
                    {
                        ["release"] = release,
                        ["enableSkipVersion"] = true
                    }), _lifetimeCancellation.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Expected while the application window is closing.
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarningMessage("Automatic update check timed out.");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            _logger.LogErrorMessage("Automatic update check failed.", ex);
        }
    }
}
