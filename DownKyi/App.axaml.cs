using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DownKyi.Application.Lifetime;
using DownKyi.Composition;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Desktop.Composition;
using DownKyi.Models;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services.Download;
using DownKyi.Utils;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using DownKyi.Views;
using DownKyi.Views.Dialogs;
using DownKyi.Views.DownloadManager;
using DownKyi.Views.Friends;
using DownKyi.Views.Settings;
using DownKyi.Views.Toolbox;
using DownKyi.Views.UserSpace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prism.DryIoc;
using Prism.Events;
using Prism.Ioc;
using Prism.Navigation.Regions;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;
using ViewSeasonsSeries = DownKyi.Views.ViewSeasonsSeries;
using ViewSeasonsSeriesViewModel = DownKyi.ViewModels.ViewSeasonsSeriesViewModel;

namespace DownKyi;

internal partial class App : PrismApplication, IDisposable
{
    private bool _disposed;
    public const string RepoOwner = "crazysmile-PhD";
    public const string RepoName = "downkyicore";

    public static ImmutableObservableCollection<DownloadingItem> DownloadingList { get; private set; } = new();
    public static ImmutableObservableCollection<DownloadedItem> DownloadedList { get; private set; } = new();
    public new static App Current => (App)Avalonia.Application.Current!;
    public new MainWindow MainWindow => _host?.Services.GetRequiredService<MainWindow>()
        ?? throw new InvalidOperationException("The application host has not been created.");
    public IClassicDesktopStyleApplicationLifetime? AppLife { get; private set; }
#if !DEBUG
    private static Mutex? _mutex;
#endif

    // 下载服务
    private IDownloadService? _downloadService;
    private IHost? _host;
    private Task? _downloadStartupTask;

    public override void Initialize()
    {
#if !DEBUG
        _mutex = new Mutex(true, BuildSingleInstanceMutexName(), out var createdNew);
        if (!createdNew)
        {
            Environment.Exit(0);
        }
#endif

        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, e) => { LogManager.Error("[Program crash]", e.Exception); };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            LogManager.Error("[Program crash]", exception!);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += OnExit!;
            AppLife = desktop;
        }

        LogManager.Info(nameof(App), $"Application initialized. Version={new AppInfo().VersionName}; Portable={StorageManager.IsPortableMode()}");

        base.Initialize();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<DownloadStorageService>();

        containerRegistry.RegisterSingleton<IDialogService, DialogService>();
        containerRegistry.Register<IDialogWindow, DialogWindow>();
        // pages
        containerRegistry.RegisterForNavigation<ViewIndex>(ViewIndexViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewLogin>(ViewLoginViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewVideoDetail>(ViewVideoDetailViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewSettings>(ViewSettingsViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewToolbox>(ViewToolboxViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDownloadManager>(ViewDownloadManagerViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewPublicFavorites>(ViewPublicFavoritesViewModel.Tag);

        containerRegistry.RegisterForNavigation<ViewUserSpace>(ViewUserSpaceViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewPublication>(ViewPublicationViewModel.Tag);
        // containerRegistry.RegisterForNavigation<Views.ViewChannel>(ViewModels.ViewChannelViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewSeasonsSeries>(ViewSeasonsSeriesViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewFriends>(ViewFriendsViewModel.Tag);

        containerRegistry.RegisterForNavigation<ViewMySpace>(ViewMySpaceViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyFavorites>(ViewMyFavoritesViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyBangumiFollow>(ViewMyBangumiFollowViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyToViewVideo>(ViewMyToViewVideoViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyHistory>(ViewMyHistoryViewModel.Tag);

        // downloadManager pages
        containerRegistry.RegisterForNavigation<ViewDownloading>(ViewDownloadingViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDownloadFinished>(ViewDownloadFinishedViewModel.Tag);

        // Friend
        containerRegistry.RegisterForNavigation<ViewFollowing>(ViewFollowingViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewFollower>(ViewFollowerViewModel.Tag);

        // settings pages
        containerRegistry.RegisterForNavigation<ViewBasic>(ViewBasicViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewNetwork>(ViewNetworkViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewVideo>(ViewVideoViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDanmaku>(ViewDanmakuViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewAbout>(ViewAboutViewModel.Tag);

        // tools pages
        containerRegistry.RegisterForNavigation<ViewBiliHelper>(ViewBiliHelperViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDelogo>(ViewDelogoViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewExtractMedia>(ViewExtractMediaViewModel.Tag);

        // UserSpace
        containerRegistry.RegisterForNavigation<ViewArchive>(ViewArchiveViewModel.Tag);
        // containerRegistry.RegisterForNavigation<Views.UserSpace.ViewChannel>(ViewModels.UserSpace.ViewChannelViewModel.Tag);
        containerRegistry.RegisterForNavigation<Views.UserSpace.ViewSeasonsSeries>(ViewModels.UserSpace.ViewSeasonsSeriesViewModel.Tag);
        containerRegistry.RegisterForNavigation<Views.UserSpace.ViewFavorites>(ViewModels.UserSpace.ViewFavoritesViewModel.Tag);

        // dialogs
        containerRegistry.RegisterDialog<ViewAlertDialog>(ViewAlertDialogViewModel.Tag);
        containerRegistry.RegisterDialog<ViewDownloadSetter>(ViewDownloadSetterViewModel.Tag);
        containerRegistry.RegisterDialog<ViewParsingSelector>(ViewParsingSelectorViewModel.Tag);
        containerRegistry.RegisterDialog<ViewAlreadyDownloadedDialog>(ViewAlreadyDownloadedDialogViewModel.Tag);
        containerRegistry.RegisterDialog<NewVersionAvailableDialog>(NewVersionAvailableDialogViewModel.Tag);
        containerRegistry.RegisterDialog<ViewUpgradingDialog>(ViewUpgradingDialogViewModel.Tag);
    }


    protected override AvaloniaObject CreateShell()
    {
        _host = DownKyiHost.Create(services => services.AddLegacyDesktopShell(
            Container.Resolve<IRegionManager>(),
            Container.Resolve<IEventAggregator>(),
            Container.Resolve<IDialogService>()));
        var shell = _host.Services.GetRequiredService<MainWindow>();
        shell.AttachLegacyRegion();
        if (!Design.IsDesignMode)
        {
            Dispatcher.UIThread.Post(StartDownloadBootstrap, DispatcherPriority.Background);
        }

        return shell;
    }

    protected override void OnInitialized()
    {
        ThemeHelper.SetTheme(SettingsManager.Instance.GetThemeMode());
        // var regionManager = Container.Resolve<IRegionManager>();
        // regionManager.RegisterViewWithRegion("ContentRegion", typeof(ViewIndex));
        // regionManager.RegisterViewWithRegion("DownloadManagerContentRegion", typeof(ViewDownloading));
        // regionManager.RegisterViewWithRegion("SettingsContentRegion", typeof(ViewBasic));
    }

    public static void PropertyChangeAsync(Action callback)
    {
        Dispatcher.UIThread.Invoke(callback);
    }

    public static void PropertyChangePost(Action callback)
    {
        Dispatcher.UIThread.Post(callback);
    }

    private void StartDownloadBootstrap()
    {
        RunStorageMaintenance();
        _downloadStartupTask ??= StartHostAndDownloadServiceAsync(GetShutdownToken());
    }

    private void RunStorageMaintenance()
    {
        var cancellationToken = GetShutdownToken();
        _ = Task.Run(async () =>
        {
            try
            {
                await StorageManager.RunMaintenanceAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogManager.Error(nameof(StorageManager), e);
            }
        });
    }

    private async Task StartHostAndDownloadServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_host != null)
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(true);
            }

            var downloadStorageService = Container.Resolve<DownloadStorageService>();
            var downloadState = await LoadDownloadStateAsync(downloadStorageService, cancellationToken).ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();
            DownloadingList.AddRange(downloadState.DownloadingItems);
            DownloadedList.AddRange(downloadState.DownloadedItems);

            _downloadService = CreateDownloadService();
            if (_downloadService != null)
            {
                await _downloadService.StartAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // App 正在关闭时允许取消。
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            LogManager.Error(nameof(App), e);
        }
    }

    private static async Task<DownloadStartupState> LoadDownloadStateAsync(
        DownloadStorageService downloadStorageService,
        CancellationToken cancellationToken)
    {
        var downloadingItemsTask = downloadStorageService.GetDownloadingAsync(cancellationToken);
        var downloadedItemsTask = downloadStorageService.GetDownloadedAsync(cancellationToken);

        await Task.WhenAll(downloadingItemsTask, downloadedItemsTask).ConfigureAwait(true);

        return new DownloadStartupState(
            await downloadingItemsTask.ConfigureAwait(true),
            await downloadedItemsTask.ConfigureAwait(true));
    }

    private IDownloadService? CreateDownloadService()
    {
        var dialogService = Container.Resolve<IDialogService>();
        return SettingsManager.Instance.GetDownloader() switch
        {
            Core.Settings.Downloader.BuiltIn => new BuiltinDownloadService(DownloadingList, DownloadedList, dialogService),
            Core.Settings.Downloader.Aria => new AriaDownloadService(DownloadingList, DownloadedList, dialogService),
            Core.Settings.Downloader.CustomAria => new CustomAriaDownloadService(DownloadingList, DownloadedList, dialogService),
            _ => null
        };
    }

    /// <summary>
    /// 下载完成列表排序
    /// </summary>
    /// <param name="finishedSort"></param>
    public static void SortDownloadedList(DownloadFinishedSort finishedSort)
    {
        var list = DownloadedList.ToList();
        switch (finishedSort)
        {
            case DownloadFinishedSort.DownloadAsc:
                // 按下载先后排序
                list.Sort((x, y) => x.Downloaded.FinishedTimestamp.CompareTo(y.Downloaded.FinishedTimestamp));
                break;
            case DownloadFinishedSort.DownloadDesc:
                // 按下载先后排序
                list.Sort((x, y) => y.Downloaded.FinishedTimestamp.CompareTo(x.Downloaded.FinishedTimestamp));
                break;
            case DownloadFinishedSort.Number:
                // 按序号排序
                list.Sort((x, y) =>
                {
                    var compare = string.Compare(x.MainTitle, y.MainTitle, StringComparison.Ordinal);
                    return compare == 0 ? x.Order.CompareTo(y.Order) : compare;
                });
                break;
            case DownloadFinishedSort.NotSet:
            default:
                break;
        }

        // 更新下载完成列表
        // 如果有更好的方法再重写
        DownloadedList.Clear();
        list.ForEach(item => DownloadedList.Add(item));
    }

    public void RefreshDownloadedList()
    {
        // 重新获取下载完成列表
        var downloadStorageService = Container.Resolve<DownloadStorageService>();
        var downloadedItems = downloadStorageService.GetDownloaded();
        DownloadedList.Clear();
        DownloadedList.AddRange(downloadedItems);
    }

    private void OnExit(object sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            if (!OnExitAsync().Wait(TimeSpan.FromSeconds(15)))
            {
                LogManager.Info(nameof(App), "Application exit cleanup timed out.");
            }
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            LogManager.Error(nameof(App), ex);
        }
        finally
        {
            Dispose();
        }
    }

    private async Task OnExitAsync()
    {
        if (_host != null)
        {
            await _host.Services
                .GetRequiredService<ApplicationCancellation>()
                .RequestShutdownAsync()
                .ConfigureAwait(false);
            using var hostStopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _host.StopAsync(hostStopTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (hostStopTimeout.IsCancellationRequested)
            {
                LogManager.Info(nameof(App), "Application host cleanup timed out.");
            }
        }

        // 强制落盘设置（防止防抖延迟期间退出导致配置丢失）
        SettingsManager.Instance.Flush();

        if (_downloadStartupTask != null)
        {
            await Task.WhenAny(_downloadStartupTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        // 关闭下载服务
        if (_downloadService != null)
        {
            var downloadShutdown = _downloadService.EndAsync();
            if (await Task.WhenAny(downloadShutdown, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(false) != downloadShutdown)
            {
                LogManager.Info(nameof(App), "Download service cleanup timed out; killing tracked aria2 process.");
                AriaServer.KillTrackedServer("application exit cleanup timed out.");
            }
        }

        await LogManager.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
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

        _downloadService?.Dispose();
        _downloadService = null;
        BiliWebClient.DisposeSharedResources();
        _host?.Dispose();
        _host = null;
#if !DEBUG
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process is exiting; only avoid masking the original shutdown path.
        }

        _mutex?.Dispose();
        _mutex = null;
#endif
    }

    private void NativeMenuItem_OnClick(object? sender, EventArgs e)
    {
        AppLife?.Shutdown();
    }

    private static string BuildSingleInstanceMutexName()
    {
        var installPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installPath)).AsSpan(0, 8));
        return $@"Global\DownKyi-{RepoOwner}-{RepoName}-{hash}";
    }

    private CancellationToken GetShutdownToken()
    {
        return _host?.Services.GetRequiredService<ApplicationCancellation>().ShutdownToken
            ?? throw new InvalidOperationException("The application cancellation service has not been created.");
    }

    private sealed record DownloadStartupState(
        IReadOnlyList<DownloadingItem> DownloadingItems,
        IReadOnlyList<DownloadedItem> DownloadedItems);
}
