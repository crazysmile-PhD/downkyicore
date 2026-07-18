using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Events;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Events;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using IDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.ViewModels;

internal sealed class MainWindowViewModel : BindableBase, IDisposable
{
    private bool _disposed;
    private readonly IEventAggregator _eventAggregator;

    private readonly IRegionManager _regionManager;

    private readonly IDialogService _dialogService;

    private const string ContentRegion = nameof(ContentRegion);

    private ClipboardListener? _clipboardListener;

    private bool _messageVisibility;
    private string? _oldMessage;
    private CancellationTokenSource? _messageCancellation;
    private CancellationTokenSource? _clipboardDebounceCancellation;

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

    public DelegateCommand? LoadedCommand { get; }

    private DelegateCommand? _closingCommand;

    public DelegateCommand ClosingCommand => _closingCommand ??= new DelegateCommand(ExecuteClosingCommand);

    private DelegateCommand<PointerPressedEventArgs>? _pointerPressedCommand;

    public DelegateCommand<PointerPressedEventArgs> PointerPressedCommand =>
        _pointerPressedCommand ??= new DelegateCommand<PointerPressedEventArgs>(ExecutePointerPressed);

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

        if (_clipboardListener != null)
        {
            _clipboardListener.Changed -= ClipboardListenerOnChanged;
            _clipboardListener.Dispose();
            _clipboardListener = null;
        }

        _clipboardDebounceCancellation?.Cancel();
        _clipboardDebounceCancellation?.Dispose();
        _clipboardDebounceCancellation = null;
        _messageCancellation?.Cancel();
        _messageCancellation?.Dispose();
        _messageCancellation = null;
    }

    private void ExecutePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        var updateKind = point.Properties.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.XButton1Pressed)
        {
            var v = GetCurrentUserControl()?.DataContext;
            if (v is ViewModelBase vm)
            {
                vm.ExecuteBackSpace();
                e.Handled = true;
            }
        }
    }

    private UserControl? GetCurrentUserControl() => _regionManager
        .Regions[ContentRegion].ActiveViews
        .FirstOrDefault() as UserControl;

    public MainWindowViewModel(IRegionManager regionManager, IEventAggregator eventAggregator, IDialogService dialogService)
    {
        _eventAggregator = eventAggregator;
        _regionManager = regionManager;
        _dialogService = dialogService;

        #region MyRegion

        _eventAggregator.GetEvent<NavigationEvent>().Subscribe(view =>
        {
            if (IsHistoryBackRequest(view))
            {
                var journal = regionManager.Regions[ContentRegion].NavigationService.Journal;
                if (journal.CanGoBack)
                {
                    journal.GoBack();
                    return;
                }
            }

            var param = new NavigationParameters
            {
                { "Parent", view.ParentViewName ?? string.Empty },
                { "Parameter", view.Parameter ?? string.Empty }
            };
            regionManager.RequestNavigate(ContentRegion, view.ViewName, param);
        });

        // 订阅消息发送事件
        _eventAggregator.GetEvent<MessageEvent>().Subscribe(message =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MessageVisibility = true;

                _oldMessage = Message;
                Message = message;
                var delay = _oldMessage == Message ? 1500 : 2000;

                _messageCancellation?.Cancel();
                _messageCancellation?.Dispose();
                _messageCancellation = new CancellationTokenSource();
                _ = HideMessageAfterDelayAsync(delay, _messageCancellation.Token);
            });
        }, ThreadOption.BackgroundThread);

        #endregion


        LoadedCommand = new DelegateCommand(() =>
        {
            if (Design.IsDesignMode)
            {
                return;
            }
            Upgrade();
            _ = CheckForUpdatesAsync();
            _clipboardListener = new ClipboardListener(App.Current.MainWindow);
            _clipboardListener.Changed += ClipboardListenerOnChanged;
            var param = new NavigationParameters
            {
                { "Parent", "" },
                { "Parameter", "start" }
            };
            _regionManager.RequestNavigate("ContentRegion", ViewIndexViewModel.Tag, param);
        });
    }

    internal static bool IsHistoryBackRequest(NavigationParam view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.ParentViewName == null && view.Parameter == null;
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

    private void ClipboardListenerOnChanged(object? sender, ClipboardChangedEventArgs e)
    {
        var isListenClipboard = SettingsManager.Instance.GetIsListenClipboard();
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

        var searchService = new SearchService();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                searchService.BiliInput(text + AppConstant.ClipboardId, ViewIndexViewModel.Tag, _eventAggregator);
            }
        });
    }

    #endregion

    private void Upgrade()
    {
        _dialogService.ShowDialogAsync(ViewUpgradingDialogViewModel.Tag, new DialogParameters(), (result) => { });
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var isAutoUpdate = SettingsManager.Instance.GetAutoUpdateWhenLaunch() != AllowStatus.Yes;
            if (isAutoUpdate) return;
            var service = new VersionCheckerService(App.RepoOwner, App.RepoName,
                SettingsManager.Instance.GetIsReceiveBetaVersion() == AllowStatus.Yes);
            var release = await service.GetLatestReleaseAsync(SettingsManager.Instance.GetSkipVersionOnLaunch()).ConfigureAwait(true);
            if (release != null && service.IsNewVersionAvailable(release.TagName))
            {
                await _dialogService.ShowDialogAsync(NewVersionAvailableDialogViewModel.Tag, new
                    DialogParameters { { "release", release }, { "enableSkipVersion", true } }).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            LogManager.Error(nameof(MainWindowViewModel), ex);
        }
    }
}
