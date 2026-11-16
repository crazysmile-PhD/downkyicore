using DownKyi.Application.Desktop;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Platform;

internal static class DesktopInteractionComposition
{
    public static IServiceCollection AddDesktopInteractions(this IServiceCollection services)
    {
        services.AddSingleton<NavigationViewModelFactory>();
        services.AddSingleton<IAppNavigationService, AvaloniaNavigationService>();
        services.AddSingleton<DialogContentFactory>();
        services.AddSingleton<IAppDialogService, AvaloniaDialogService>();
        services.AddSingleton<IUserNotificationService, DesktopNotificationService>();
        services.AddSingleton<IDesktopInteractionContext, DesktopInteractionContext>();

        AddRouteViewModels(services);
        AddDialogViewModelsAndViews(services);
        return services;
    }

    private static void AddRouteViewModels(IServiceCollection services)
    {
        services.AddTransient<IndexViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<VideoDetailViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ToolboxViewModel>();
        services.AddTransient<DownloadManagerViewModel>();
        services.AddTransient<PublicFavoritesViewModel>();
        services.AddTransient<UserSpaceViewModel>();
        services.AddTransient<PublicationViewModel>();
        services.AddTransient<SeasonsSeriesDetailViewModel>();
        services.AddTransient<FriendsViewModel>();
        services.AddTransient<MySpaceViewModel>();
        services.AddTransient<MyFavoritesViewModel>();
        services.AddTransient<MyBangumiFollowViewModel>();
        services.AddTransient<MyToViewVideoViewModel>();
        services.AddTransient<MyHistoryViewModel>();
        services.AddTransient<DownloadingViewModel>();
        services.AddTransient<DownloadFinishedViewModel>();
        services.AddTransient<FollowingViewModel>();
        services.AddTransient<FollowerViewModel>();
        services.AddTransient<BasicViewModel>();
        services.AddTransient<NetworkViewModel>();
        services.AddTransient<VideoViewModel>();
        services.AddTransient<DanmakuViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<BiliHelperViewModel>();
        services.AddTransient<DelogoViewModel>();
        services.AddTransient<ExtractMediaViewModel>();
        services.AddTransient<ArchiveViewModel>();
        services.AddTransient<UserSpaceSeasonsSeriesViewModel>();
        services.AddTransient<FavoritesViewModel>();
    }

    private static void AddDialogViewModelsAndViews(IServiceCollection services)
    {
        services.AddTransient<AlertDialogViewModel>();
        services.AddTransient<DownloadSetterViewModel>();
        services.AddTransient<NewVersionAvailableDialogViewModel>();
        services.AddTransient<UpgradingDialogViewModel>();
        services.AddTransient<StartupDiagnosticsDialogViewModel>();
        services.AddTransient<DownloadContentConflictDialogViewModel>();
        services.AddTransient<DownloadDuplicateBatchDialogViewModel>();
        services.AddTransient<DownloadUnavailableBatchDialogViewModel>();
        services.AddTransient<ViewAlertDialog>();
        services.AddTransient<ViewDownloadSetter>();
        services.AddTransient<NewVersionAvailableDialog>();
        services.AddTransient<ViewUpgradingDialog>();
        services.AddTransient<StartupDiagnosticsDialog>();
        services.AddTransient<DownloadContentConflictDialog>();
        services.AddTransient<DownloadDuplicateBatchDialog>();
        services.AddTransient<DownloadUnavailableBatchDialog>();
    }
}
