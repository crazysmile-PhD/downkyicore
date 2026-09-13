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
        services.AddTransient<DownloadRuntimeFailureDialogViewModel>();
        services.AddTransient<ViewAlertDialog>();
        services.AddTransient<ViewDownloadSetter>();
        services.AddTransient<ViewParsingSelector>();
        services.AddTransient<ViewAlreadyDownloadedDialog>();
        services.AddTransient<NewVersionAvailableDialog>();
        services.AddTransient<ViewUpgradingDialog>();
        services.AddTransient<DownloadRuntimeFailureDialog>();
    }
}
