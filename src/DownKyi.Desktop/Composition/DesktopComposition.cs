using System;
using System.IO;
using System.Net.Http;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;
using DownKyi.Infrastructure.Bilibili;
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
using DownKyi.Views;
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
        services.AddSingleton<IVideoTagProvider, VideoTagProvider>();
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
        services.AddDesktopInteractions();
        services.AddSingleton<SearchService>();

        services.AddDownloadModule();
        services.AddSingleton<IHostedService, StorageMaintenanceHostedService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }

}
