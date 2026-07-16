using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using Prism.Navigation.Regions;
using RootSeasonsSeriesViewModel = DownKyi.ViewModels.ViewSeasonsSeriesViewModel;
using UserSpaceSeasonsSeriesViewModel = DownKyi.ViewModels.UserSpace.ViewSeasonsSeriesViewModel;

namespace DownKyi.Platform;

internal sealed class PrismNavigationService : IAppNavigationService
{
    private const string MainRegion = "ContentRegion";
    private const string SettingsRegion = "SettingsContentRegion";
    private const string DownloadManagerRegion = "DownloadManagerContentRegion";
    private const string FriendsRegion = "FriendContentRegion";
    private const string UserSpaceRegion = "UserSpaceContentRegion";
    private const string ToolboxRegion = "ToolboxContentRegion";
    private readonly IRegionManager _regionManager;

    public PrismNavigationService(IRegionManager regionManager)
    {
        _regionManager = regionManager ?? throw new ArgumentNullException(nameof(regionManager));
    }

    public void Navigate(AppNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new NavigationParameters
        {
            { "Parent", request.Parent is { } parent ? GetRouteName(parent) : string.Empty },
            { "Parameter", request.Parameter ?? string.Empty }
        };
        if (request.Parent is { } parentRoute)
        {
            parameters.Add("ParentRoute", parentRoute);
        }

        _regionManager.RequestNavigate(MainRegion, GetRouteName(request.Route), parameters);
    }

    public void NavigateRegion(
        AppNavigationRegion region,
        AppRoute route,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var navigationParameters = new NavigationParameters();
        if (parameters != null)
        {
            foreach (var pair in parameters)
            {
                navigationParameters.Add(pair.Key, pair.Value ?? string.Empty);
            }
        }

        _regionManager.RequestNavigate(GetRegionName(region), GetRouteName(route), navigationParameters);
    }

    public void ClearRegion(AppNavigationRegion region)
    {
        _regionManager.RequestNavigate(GetRegionName(region), string.Empty);
    }

    public object? GetActiveView(AppNavigationRegion region)
    {
        return _regionManager.Regions[GetRegionName(region)].ActiveViews.FirstOrDefault();
    }

    internal static string GetRegionName(AppNavigationRegion region)
    {
        return region switch
        {
            AppNavigationRegion.Main => MainRegion,
            AppNavigationRegion.Settings => SettingsRegion,
            AppNavigationRegion.DownloadManager => DownloadManagerRegion,
            AppNavigationRegion.Friends => FriendsRegion,
            AppNavigationRegion.UserSpace => UserSpaceRegion,
            AppNavigationRegion.Toolbox => ToolboxRegion,
            _ => throw new ArgumentOutOfRangeException(nameof(region), region, null)
        };
    }

    internal static string GetRouteName(AppRoute route)
    {
        return route switch
        {
            AppRoute.Index => ViewIndexViewModel.Tag,
            AppRoute.Login => ViewLoginViewModel.Tag,
            AppRoute.VideoDetail => ViewVideoDetailViewModel.Tag,
            AppRoute.Settings => ViewSettingsViewModel.Tag,
            AppRoute.Toolbox => ViewToolboxViewModel.Tag,
            AppRoute.DownloadManager => ViewDownloadManagerViewModel.Tag,
            AppRoute.PublicFavorites => ViewPublicFavoritesViewModel.Tag,
            AppRoute.UserSpace => ViewUserSpaceViewModel.Tag,
            AppRoute.Publication => ViewPublicationViewModel.Tag,
            AppRoute.SeasonsSeries => RootSeasonsSeriesViewModel.Tag,
            AppRoute.Friends => ViewFriendsViewModel.Tag,
            AppRoute.MySpace => ViewMySpaceViewModel.Tag,
            AppRoute.MyFavorites => ViewMyFavoritesViewModel.Tag,
            AppRoute.MyBangumiFollow => ViewMyBangumiFollowViewModel.Tag,
            AppRoute.MyToViewVideo => ViewMyToViewVideoViewModel.Tag,
            AppRoute.MyHistory => ViewMyHistoryViewModel.Tag,
            AppRoute.Downloading => ViewDownloadingViewModel.Tag,
            AppRoute.DownloadFinished => ViewDownloadFinishedViewModel.Tag,
            AppRoute.Following => ViewFollowingViewModel.Tag,
            AppRoute.Follower => ViewFollowerViewModel.Tag,
            AppRoute.SettingsBasic => ViewBasicViewModel.Tag,
            AppRoute.SettingsNetwork => ViewNetworkViewModel.Tag,
            AppRoute.SettingsVideo => ViewVideoViewModel.Tag,
            AppRoute.SettingsDanmaku => ViewDanmakuViewModel.Tag,
            AppRoute.SettingsAbout => ViewAboutViewModel.Tag,
            AppRoute.BiliHelper => ViewBiliHelperViewModel.Tag,
            AppRoute.Delogo => ViewDelogoViewModel.Tag,
            AppRoute.ExtractMedia => ViewExtractMediaViewModel.Tag,
            AppRoute.Archive => ViewArchiveViewModel.Tag,
            AppRoute.UserSpaceChannel => ViewChannelViewModel.Tag,
            AppRoute.UserSpaceSeasonsSeries => UserSpaceSeasonsSeriesViewModel.Tag,
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
        };
    }
}
