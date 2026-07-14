using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Application.Time;
using DownKyi.Composition;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Services.Download;
using DownKyi.Services.Friends;
using DownKyi.Services.Media;
using DownKyi.Services.Toolbox;
using DownKyi.Services.UserSpace;
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

    public new static App Current => (App)Avalonia.Application.Current!;
    public new MainWindow MainWindow => _host?.Services.GetRequiredService<MainWindow>()
        ?? throw new InvalidOperationException("The application host has not been created.");
    public IClassicDesktopStyleApplicationLifetime? AppLife { get; private set; }
#if !DEBUG
    private static Mutex? _mutex;
#endif

    private IHost? _host;
    private Task? _downloadStartupTask;
    private Task? _shutdownTask;

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
        containerRegistry.RegisterInstance(new SqliteDownloadTaskStoreOptions(StorageManager.GetDbPath()));
        containerRegistry.RegisterSingleton<IClock, SystemClock>();
        containerRegistry.RegisterSingleton<IDownloadTaskStore, SqliteDownloadTaskStore>();
        containerRegistry.RegisterSingleton<DownloadStorageService>();
        containerRegistry.RegisterSingleton<DownloadListState>();
        containerRegistry.RegisterSingleton<IAddToDownloadServiceFactory, AddToDownloadServiceFactory>();
        containerRegistry.RegisterSingleton<IContentDownloadCoordinator, ContentDownloadCoordinator>();
        containerRegistry.RegisterSingleton<IPersonalMediaCoordinator, PersonalMediaCoordinator>();
        containerRegistry.RegisterSingleton<IFavoritesService, FavoritesService>();
        containerRegistry.RegisterSingleton<IFavoritesCoordinator, FavoritesCoordinator>();
        containerRegistry.RegisterSingleton<IBiliHelperCoordinator, BiliHelperCoordinator>();
        containerRegistry.RegisterSingleton<IUserSessionCoordinator, UserSessionCoordinator>();
        containerRegistry.RegisterSingleton<ILoginCoordinator, LoginCoordinator>();
        containerRegistry.RegisterSingleton<IFriendRelationCoordinator, FriendRelationCoordinator>();
        containerRegistry.RegisterSingleton<ISeasonsSeriesCoordinator, SeasonsSeriesCoordinator>();
        containerRegistry.RegisterSingleton<IClipboardService, AvaloniaClipboardService>();
        containerRegistry.RegisterSingleton<IFilePickerService, AvaloniaFilePickerService>();

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
        var downloadLists = Container.Resolve<DownloadListState>();
        _host = DownKyiHost.Create(services =>
        {
            services.AddDownKyiBilibiliHttpClient();
            services.AddSingleton<IHostedService, StorageMaintenanceHostedService>();
            services.AddSingleton(downloadLists);
            services.AddSingleton(Container.Resolve<DownloadStorageService>());
            services.AddSingleton(Container.Resolve<IAddToDownloadServiceFactory>());
            services.AddLegacyDesktopShell(
                Container.Resolve<IRegionManager>(),
                Container.Resolve<IEventAggregator>(),
                Container.Resolve<IDialogService>(),
                Container.Resolve<IClipboardService>());
            services.AddSingleton<IDownloadRuntimeFactory, DownloadRuntimeFactory>();
            services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
            services.AddSingleton<IHostedService, DownloadBootstrapHostedService>();
        });
        WebClient.Configure(_host.Services.GetRequiredService<BilibiliHttpClient>());
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
        _downloadStartupTask ??= StartHostAsync(GetShutdownToken());
    }

    private async Task StartHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_host != null)
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(true);
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

    private void OnExit(object sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_shutdownTask is { IsCompleted: false })
        {
            LogManager.Info(nameof(App), "Application lifetime exited before asynchronous cleanup completed.");
        }

        Dispose();
    }

    internal Task RequestShutdownAsync()
    {
        return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        var cleanupTasks = new List<Task>();
        if (_host != null)
        {
            await _host.Services
                .GetRequiredService<ApplicationCancellation>()
                .RequestShutdownAsync()
                .ConfigureAwait(false);
            cleanupTasks.Add(StopHostAsync(_host));
        }

        SettingsManager.Instance.Flush();

        if (_downloadStartupTask != null)
        {
            cleanupTasks.Add(_downloadStartupTask);
        }

        var cleanup = Task.WhenAll(cleanupTasks);
        if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == cleanup)
        {
            await cleanup.ConfigureAwait(false);
        }
        else
        {
            LogManager.Info(nameof(App), "Application cleanup timed out; killing the tracked aria2 process.");
            AriaServer.KillTrackedServer("application exit cleanup timed out.");
            _ = cleanup.ContinueWith(
                task => LogManager.Error(nameof(App), task.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        await LogManager.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static Task StopHostAsync(IHost host)
    {
        return host.StopAsync(CancellationToken.None);
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

    private async void NativeMenuItem_OnClick(object? sender, EventArgs e)
    {
        await RequestShutdownAsync().ConfigureAwait(true);
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

}
