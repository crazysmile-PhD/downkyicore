using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Application.Time;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Services.Download;
using DownKyi.Services.Friends;
using DownKyi.Services.Media;
using DownKyi.Services.Migration;
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
using DownKyi.Views.DownloadManager;
using DownKyi.Views.Friends;
using DownKyi.Views.Settings;
using DownKyi.Views.Toolbox;
using DownKyi.Views.UserSpace;
using Prism.Ioc;
using ViewSeasonsSeries = DownKyi.Views.ViewSeasonsSeries;
using ViewSeasonsSeriesViewModel = DownKyi.ViewModels.ViewSeasonsSeriesViewModel;

namespace DownKyi.Composition;

// Temporary bridge: PR 25-29 removes Prism navigation and dialog registration.
internal static class LegacyPrismComposition
{
    public static void RegisterLegacyApplication(this IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterInstance(new SqliteDownloadTaskStoreOptions(StorageManager.GetDbPath()));
        containerRegistry.RegisterSingleton<IClock, SystemClock>();
        containerRegistry.RegisterSingleton<ISettingsStore, SettingsStore>();
        containerRegistry.RegisterSingleton<FfmpegProcessor>();
        containerRegistry.RegisterSingleton<IDownloadTaskStore, SqliteDownloadTaskStore>();
        containerRegistry.RegisterSingleton<DownloadStorageService>();
        containerRegistry.RegisterSingleton<DownloadListState>();
        containerRegistry.RegisterSingleton<DownloadTaskFileService>();
        containerRegistry.RegisterSingleton<IDownloadManagerCoordinator, DownloadManagerCoordinator>();
        containerRegistry.RegisterSingleton<IAddToDownloadServiceFactory, AddToDownloadServiceFactory>();
        containerRegistry.Register<IVideoDetailWorkflowCoordinator, VideoDetailWorkflowCoordinator>();
        containerRegistry.RegisterSingleton<IVideoDetailDownloadCoordinator, VideoDetailDownloadCoordinator>();
        containerRegistry.RegisterSingleton<IContentDownloadCoordinator, ContentDownloadCoordinator>();
        containerRegistry.RegisterSingleton<IContentInfoServiceFactory, ContentInfoServiceFactory>();
        containerRegistry.RegisterSingleton<IPersonalMediaCoordinator, PersonalMediaCoordinator>();
        containerRegistry.RegisterSingleton<ILegacyUpgradeCoordinator, LegacyUpgradeCoordinator>();
        containerRegistry.RegisterSingleton<IFavoritesService, FavoritesService>();
        containerRegistry.RegisterSingleton<IFavoritesCoordinator, FavoritesCoordinator>();
        containerRegistry.RegisterSingleton<IBiliHelperCoordinator, BiliHelperCoordinator>();
        containerRegistry.RegisterSingleton<IUserSessionCoordinator, UserSessionCoordinator>();
        containerRegistry.RegisterSingleton<ILoginCoordinator, LoginCoordinator>();
        containerRegistry.RegisterSingleton<IFriendRelationCoordinator, FriendRelationCoordinator>();
        containerRegistry.RegisterSingleton<ISeasonsSeriesCoordinator, SeasonsSeriesCoordinator>();
        containerRegistry.RegisterSingleton<IUserSpacePageCoordinator, UserSpacePageCoordinator>();
        containerRegistry.RegisterSingleton<AvaloniaDesktopContext>();
        containerRegistry.RegisterSingleton<IProcessRestartLauncher, ProcessRestartLauncher>();
        containerRegistry.RegisterSingleton<IApplicationLifecycle, AvaloniaApplicationLifecycle>();
        containerRegistry.RegisterSingleton<IClipboardMonitor, AvaloniaClipboardMonitor>();
        containerRegistry.RegisterSingleton<IClipboardService, AvaloniaClipboardService>();
        containerRegistry.RegisterSingleton<IFilePickerService, AvaloniaFilePickerService>();
        containerRegistry.RegisterSingleton<IPlatformLauncher, AvaloniaPlatformLauncher>();
        containerRegistry.RegisterSingleton<IDialogService, DialogService>();
        containerRegistry.Register<IDialogWindow, DialogWindow>();

        containerRegistry.RegisterForNavigation<ViewIndex>(ViewIndexViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewLogin>(ViewLoginViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewVideoDetail>(ViewVideoDetailViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewSettings>(ViewSettingsViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewToolbox>(ViewToolboxViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDownloadManager>(ViewDownloadManagerViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewPublicFavorites>(ViewPublicFavoritesViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewUserSpace>(ViewUserSpaceViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewPublication>(ViewPublicationViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewSeasonsSeries>(ViewSeasonsSeriesViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewFriends>(ViewFriendsViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMySpace>(ViewMySpaceViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyFavorites>(ViewMyFavoritesViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyBangumiFollow>(ViewMyBangumiFollowViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyToViewVideo>(ViewMyToViewVideoViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewMyHistory>(ViewMyHistoryViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDownloading>(ViewDownloadingViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDownloadFinished>(ViewDownloadFinishedViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewFollowing>(ViewFollowingViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewFollower>(ViewFollowerViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewBasic>(ViewBasicViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewNetwork>(ViewNetworkViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewVideo>(ViewVideoViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDanmaku>(ViewDanmakuViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewAbout>(ViewAboutViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewBiliHelper>(ViewBiliHelperViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewDelogo>(ViewDelogoViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewExtractMedia>(ViewExtractMediaViewModel.Tag);
        containerRegistry.RegisterForNavigation<ViewArchive>(ViewArchiveViewModel.Tag);
        containerRegistry.RegisterForNavigation<Views.UserSpace.ViewSeasonsSeries>(
            ViewModels.UserSpace.ViewSeasonsSeriesViewModel.Tag);

        containerRegistry.RegisterDialog<ViewAlertDialog>(ViewAlertDialogViewModel.Tag);
        containerRegistry.RegisterDialog<ViewDownloadSetter>(ViewDownloadSetterViewModel.Tag);
        containerRegistry.RegisterDialog<ViewParsingSelector>(ViewParsingSelectorViewModel.Tag);
        containerRegistry.RegisterDialog<ViewAlreadyDownloadedDialog>(ViewAlreadyDownloadedDialogViewModel.Tag);
        containerRegistry.RegisterDialog<NewVersionAvailableDialog>(NewVersionAvailableDialogViewModel.Tag);
        containerRegistry.RegisterDialog<ViewUpgradingDialog>(ViewUpgradingDialogViewModel.Tag);
    }
}
