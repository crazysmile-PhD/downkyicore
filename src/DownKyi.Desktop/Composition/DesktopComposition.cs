using System;
using System.IO;
using System.Net.Http;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Platform;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Services.Download;
using DownKyi.Services.Friends;
using DownKyi.Services.Media;
using DownKyi.Services.Migration;
using DownKyi.Services.Settings;
using DownKyi.Services.Toolbox;
using DownKyi.Services.UserSpace;
using DownKyi.Services.Video;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using DownKyi.Views;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Composition;

internal static class DesktopComposition
{
    public static IServiceCollection AddDownKyiDesktop(
        this IServiceCollection services,
        ILoggerFactory loggerFactory,
        IApplicationLogService logService)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logService);

        services.AddSingleton(loggerFactory);
        services.AddSingleton(logService);
        services.AddSingleton(new SqliteDownloadTaskStoreOptions(ApplicationStorage.GetDbPath()));
        services.AddHttpClient("DownKyi.Images", client =>
            client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient<VersionCheckerService>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("downkyi");
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddSingleton<IAsyncImageLoader>(provider =>
            new DiskCachedWebImageLoader(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("DownKyi.Images"),
                disposeHttpClient: true,
                Path.Combine(ApplicationStorage.GetCache(), "Images")));
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IBilibiliCookieProvider, BilibiliCookieProvider>();
        services.AddDownKyiBilibiliInfrastructure(provider =>
        {
            var network = provider.GetRequiredService<ISettingsStore>().Current.Network;
            return network.NetworkProxy switch
            {
                NetworkProxy.None => new BilibiliNetworkOptions(
                    network.UserAgent,
                    UseProxy: false,
                    ProxyAddress: null),
                NetworkProxy.Custom => new BilibiliNetworkOptions(
                    network.UserAgent,
                    UseProxy: true,
                    network.CustomNetworkProxy),
                _ => new BilibiliNetworkOptions(
                    network.UserAgent,
                    UseProxy: true,
                    ProxyAddress: null)
            };
        });
        services.AddSingleton<IWbiKeyProvider, WbiKeyProvider>();
        services.AddSingleton<FfmpegProcessor>();
        services.AddSingleton<IDownloadTaskStore, SqliteDownloadTaskStore>();
        services.AddSingleton<IDownloadTaskApplicationService, DownloadTaskApplicationService>();
        services.AddSingleton<DownloadTaskProjectionStore>();
        services.AddSingleton<DownloadTaskStateWriter>();
        services.AddSingleton<DownloadTaskQueueGateway>();
        services.AddSingleton<IDownloadTaskQueue>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<DownloadTaskAdmissionService>();
        services.AddSingleton<DownloadListState>();
        services.AddSingleton<DownloadTaskFileService>();
        services.AddSingleton<AriaRuntimeClientRegistry>();
        services.AddSingleton<IDownloadManagerCoordinator, DownloadManagerCoordinator>();
        services.AddSingleton<IVideoTagProvider, VideoTagProvider>();
        services.AddSingleton<DownloadDuplicatePolicy>();
        services.AddSingleton<DownloadMovieMetadataBuilder>();
        services.AddSingleton<IAddToDownloadServiceFactory, AddToDownloadServiceFactory>();
        services.AddTransient<IVideoDetailWorkflowCoordinator, VideoDetailWorkflowCoordinator>();
        services.AddSingleton<IVideoDetailDownloadCoordinator, VideoDetailDownloadCoordinator>();
        services.AddSingleton<IContentDownloadCoordinator, ContentDownloadCoordinator>();
        services.AddSingleton<IContentInfoServiceFactory, ContentInfoServiceFactory>();
        services.AddSingleton<IPersonalMediaCoordinator, PersonalMediaCoordinator>();
        services.AddSingleton<ILegacyUpgradeCoordinator, LegacyUpgradeCoordinator>();
        services.AddSingleton<IFavoritesService, FavoritesService>();
        services.AddSingleton<IFavoritesCoordinator, FavoritesCoordinator>();
        services.AddSingleton<IBiliHelperCoordinator, BiliHelperCoordinator>();
        services.AddSingleton<IUserSessionCoordinator, UserSessionCoordinator>();
        services.AddSingleton<ILoginCoordinator, LoginCoordinator>();
        services.AddSingleton<IFriendRelationCoordinator, FriendRelationCoordinator>();
        services.AddSingleton<ISeasonsSeriesCoordinator, SeasonsSeriesCoordinator>();
        services.AddSingleton<IUserSpacePageCoordinator, UserSpacePageCoordinator>();
        services.AddSingleton<IUserSpaceLoadCoordinator, UserSpaceLoadCoordinator>();
        services.AddSingleton<INetworkSettingsCoordinator, NetworkSettingsCoordinator>();

        services.AddSingleton<AvaloniaDesktopContext>();
        services.AddSingleton<IProcessRestartLauncher, ProcessRestartLauncher>();
        services.AddSingleton<AvaloniaApplicationLifecycle>();
        services.AddSingleton<IApplicationLifecycle>(provider =>
            provider.GetRequiredService<AvaloniaApplicationLifecycle>());
        services.AddSingleton<IClipboardMonitor, AvaloniaClipboardMonitor>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.AddSingleton<IPlatformLauncher, AvaloniaPlatformLauncher>();
        services.AddSingleton<ILoginQrCodeRenderer, LoginQrCodeRenderer>();
        services.AddSingleton<IUserNotificationService, DesktopNotificationService>();
        services.AddSingleton<IAppNavigationService, AvaloniaNavigationService>();
        services.AddSingleton<IAppDialogService, AvaloniaDialogService>();
        services.AddSingleton<IDesktopInteractionContext, DesktopInteractionContext>();
        services.AddSingleton<SearchService>();

        services.AddSingleton<AriaServer>();
        services.AddSingleton<DownloadDiagnosticLogger>();
        services.AddSingleton<IDownloadRuntimeFactory, DownloadRuntimeFactory>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IHostedService, StorageMaintenanceHostedService>();
        services.AddSingleton<DownloadBootstrapHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<DownloadBootstrapHostedService>());

        AddRouteViewModels(services);
        AddDialogViewModelsAndViews(services);
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }

    private static void AddRouteViewModels(IServiceCollection services)
    {
        services.AddTransient<ViewIndexViewModel>();
        services.AddTransient<ViewLoginViewModel>();
        services.AddTransient<ViewVideoDetailViewModel>();
        services.AddTransient<ViewSettingsViewModel>();
        services.AddTransient<ViewToolboxViewModel>();
        services.AddTransient<ViewDownloadManagerViewModel>();
        services.AddTransient<ViewPublicFavoritesViewModel>();
        services.AddTransient<ViewUserSpaceViewModel>();
        services.AddTransient<ViewPublicationViewModel>();
        services.AddTransient<ViewSeasonsSeriesDetailViewModel>();
        services.AddTransient<ViewFriendsViewModel>();
        services.AddTransient<ViewMySpaceViewModel>();
        services.AddTransient<ViewMyFavoritesViewModel>();
        services.AddTransient<ViewMyBangumiFollowViewModel>();
        services.AddTransient<ViewMyToViewVideoViewModel>();
        services.AddTransient<ViewMyHistoryViewModel>();
        services.AddTransient<ViewDownloadingViewModel>();
        services.AddTransient<ViewDownloadFinishedViewModel>();
        services.AddTransient<ViewFollowingViewModel>();
        services.AddTransient<ViewFollowerViewModel>();
        services.AddTransient<ViewBasicViewModel>();
        services.AddTransient<ViewNetworkViewModel>();
        services.AddTransient<ViewVideoViewModel>();
        services.AddTransient<ViewDanmakuViewModel>();
        services.AddTransient<ViewAboutViewModel>();
        services.AddTransient<ViewBiliHelperViewModel>();
        services.AddTransient<ViewDelogoViewModel>();
        services.AddTransient<ViewExtractMediaViewModel>();
        services.AddTransient<ViewArchiveViewModel>();
        services.AddTransient<ViewChannelViewModel>();
        services.AddTransient<ViewUserSpaceSeasonsSeriesViewModel>();
        services.AddTransient<ViewFavoritesViewModel>();
    }

    private static void AddDialogViewModelsAndViews(IServiceCollection services)
    {
        services.AddTransient<ViewAlertDialogViewModel>();
        services.AddTransient<ViewDownloadSetterViewModel>();
        services.AddTransient<ViewParsingSelectorViewModel>();
        services.AddTransient<ViewAlreadyDownloadedDialogViewModel>();
        services.AddTransient<NewVersionAvailableDialogViewModel>();
        services.AddTransient<ViewUpgradingDialogViewModel>();
        services.AddTransient<ViewAlertDialog>();
        services.AddTransient<ViewDownloadSetter>();
        services.AddTransient<ViewParsingSelector>();
        services.AddTransient<ViewAlreadyDownloadedDialog>();
        services.AddTransient<NewVersionAvailableDialog>();
        services.AddTransient<ViewUpgradingDialog>();
    }
}
