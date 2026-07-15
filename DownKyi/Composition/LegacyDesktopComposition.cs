using System;
using DownKyi.Application.Desktop;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Composition;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.ViewModels;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prism.Events;
using Prism.Ioc;
using Prism.Navigation.Regions;

namespace DownKyi.Composition;

// Temporary bridge: PR 25-29 removes Prism and moves these registrations into DownKyi.Desktop.
internal static class LegacyDesktopComposition
{
    public static IHost CreateLegacyDesktopHost(this IContainerProvider container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var downloadLists = container.Resolve<DownloadListState>();
        var settingsStore = container.Resolve<ISettingsStore>();
        var loggerFactory = container.Resolve<ILoggerFactory>();
        var logService = container.Resolve<IApplicationLogService>();
        return DownKyiHost.Create(services =>
        {
            services.AddSingleton(loggerFactory);
            services.AddSingleton(logService);
            services.AddDownKyiBilibiliHttpClient(settingsStore);
            services.AddSingleton<IHostedService, StorageMaintenanceHostedService>();
            services.AddSingleton<AriaServer>();
            services.AddSingleton(downloadLists);
            services.AddSingleton(container.Resolve<FfmpegProcessor>());
            services.AddSingleton(container.Resolve<DownloadStorageService>());
            services.AddSingleton(container.Resolve<IAddToDownloadServiceFactory>());
            services.AddLegacyDesktopShell(
                container.Resolve<IRegionManager>(),
                container.Resolve<IEventAggregator>(),
                container.Resolve<IDialogService>(),
                container.Resolve<IClipboardService>(),
                container.Resolve<IPlatformLauncher>(),
                settingsStore);
            services.AddSingleton<DownloadDiagnosticLogger>();
            services.AddSingleton<IDownloadRuntimeFactory, DownloadRuntimeFactory>();
            services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
            services.AddSingleton<IHostedService, DownloadBootstrapHostedService>();
        });
    }

    public static IServiceCollection AddLegacyDesktopShell(
        this IServiceCollection services,
        IRegionManager regionManager,
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        IClipboardService clipboardService,
        IPlatformLauncher platformLauncher,
        ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(regionManager);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(platformLauncher);
        ArgumentNullException.ThrowIfNull(settingsStore);

        services.AddSingleton(regionManager);
        services.AddSingleton(eventAggregator);
        services.AddSingleton(dialogService);
        services.AddSingleton(clipboardService);
        services.AddSingleton(platformLauncher);
        services.AddSingleton(settingsStore);
        services.AddSingleton<IUserSessionCoordinator, UserSessionCoordinator>();
        services.AddTransient<IVideoDetailWorkflowCoordinator, VideoDetailWorkflowCoordinator>();
        services.AddSingleton<IVideoDetailDownloadCoordinator, VideoDetailDownloadCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<ViewIndexViewModel>();
        services.AddTransient<ViewVideoDetailViewModel>();
        services.AddTransient<ViewDownloadManagerViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
