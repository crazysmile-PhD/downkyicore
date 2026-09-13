using System;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Platform;

internal sealed class NavigationViewModelFactory(IServiceProvider services)
{
    private readonly IServiceProvider _services = services
        ?? throw new ArgumentNullException(nameof(services));

    public object Create(AppRoute route) => _services.GetRequiredService(GetViewModelType(route));

    internal static Type GetViewModelType(AppRoute route)
    {
        return route switch
        {
            AppRoute.Index => typeof(ViewIndexViewModel),
            AppRoute.Login => typeof(ViewLoginViewModel),
            AppRoute.VideoDetail => typeof(ViewVideoDetailViewModel),
            AppRoute.Settings => typeof(ViewSettingsViewModel),
            AppRoute.Toolbox => typeof(ViewToolboxViewModel),
            AppRoute.DownloadManager => typeof(ViewDownloadManagerViewModel),
            AppRoute.PublicFavorites => typeof(ViewPublicFavoritesViewModel),
            AppRoute.UserSpace => typeof(ViewUserSpaceViewModel),
            AppRoute.Publication => typeof(ViewPublicationViewModel),
            AppRoute.SeasonsSeries => typeof(ViewSeasonsSeriesDetailViewModel),
            AppRoute.Friends => typeof(ViewFriendsViewModel),
            AppRoute.MySpace => typeof(ViewMySpaceViewModel),
            AppRoute.MyFavorites => typeof(ViewMyFavoritesViewModel),
            AppRoute.MyBangumiFollow => typeof(ViewMyBangumiFollowViewModel),
            AppRoute.MyToViewVideo => typeof(ViewMyToViewVideoViewModel),
            AppRoute.MyHistory => typeof(ViewMyHistoryViewModel),
            AppRoute.Downloading => typeof(ViewDownloadingViewModel),
            AppRoute.DownloadFinished => typeof(ViewDownloadFinishedViewModel),
            AppRoute.Following => typeof(ViewFollowingViewModel),
            AppRoute.Follower => typeof(ViewFollowerViewModel),
            AppRoute.SettingsBasic => typeof(ViewBasicViewModel),
            AppRoute.SettingsNetwork => typeof(ViewNetworkViewModel),
            AppRoute.SettingsVideo => typeof(ViewVideoViewModel),
            AppRoute.SettingsDanmaku => typeof(ViewDanmakuViewModel),
            AppRoute.SettingsAbout => typeof(ViewAboutViewModel),
            AppRoute.BiliHelper => typeof(ViewBiliHelperViewModel),
            AppRoute.Delogo => typeof(ViewDelogoViewModel),
            AppRoute.ExtractMedia => typeof(ViewExtractMediaViewModel),
            AppRoute.Archive => typeof(ViewArchiveViewModel),
            AppRoute.UserSpaceChannel => typeof(ViewChannelViewModel),
            AppRoute.UserSpaceSeasonsSeries => typeof(ViewUserSpaceSeasonsSeriesViewModel),
            AppRoute.UserSpaceFavorites => typeof(ViewFavoritesViewModel),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
        };
    }
}
