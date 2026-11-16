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
            AppRoute.Index => typeof(IndexViewModel),
            AppRoute.Login => typeof(LoginViewModel),
            AppRoute.VideoDetail => typeof(VideoDetailViewModel),
            AppRoute.Settings => typeof(SettingsViewModel),
            AppRoute.Toolbox => typeof(ToolboxViewModel),
            AppRoute.DownloadManager => typeof(DownloadManagerViewModel),
            AppRoute.PublicFavorites => typeof(PublicFavoritesViewModel),
            AppRoute.UserSpace => typeof(UserSpaceViewModel),
            AppRoute.Publication => typeof(PublicationViewModel),
            AppRoute.SeasonsSeries => typeof(SeasonsSeriesDetailViewModel),
            AppRoute.Friends => typeof(FriendsViewModel),
            AppRoute.MySpace => typeof(MySpaceViewModel),
            AppRoute.MyFavorites => typeof(MyFavoritesViewModel),
            AppRoute.MyBangumiFollow => typeof(MyBangumiFollowViewModel),
            AppRoute.MyToViewVideo => typeof(MyToViewVideoViewModel),
            AppRoute.MyHistory => typeof(MyHistoryViewModel),
            AppRoute.Downloading => typeof(DownloadingViewModel),
            AppRoute.DownloadFinished => typeof(DownloadFinishedViewModel),
            AppRoute.Following => typeof(FollowingViewModel),
            AppRoute.Follower => typeof(FollowerViewModel),
            AppRoute.SettingsBasic => typeof(BasicViewModel),
            AppRoute.SettingsNetwork => typeof(NetworkViewModel),
            AppRoute.SettingsVideo => typeof(VideoViewModel),
            AppRoute.SettingsDanmaku => typeof(DanmakuViewModel),
            AppRoute.SettingsAbout => typeof(AboutViewModel),
            AppRoute.BiliHelper => typeof(BiliHelperViewModel),
            AppRoute.Delogo => typeof(DelogoViewModel),
            AppRoute.ExtractMedia => typeof(ExtractMediaViewModel),
            AppRoute.Archive => typeof(ArchiveViewModel),
            AppRoute.UserSpaceSeasonsSeries => typeof(UserSpaceSeasonsSeriesViewModel),
            AppRoute.UserSpaceFavorites => typeof(FavoritesViewModel),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
        };
    }
}
