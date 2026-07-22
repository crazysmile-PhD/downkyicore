using System;
using System.Globalization;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Settings;

namespace DownKyi.Services;

internal class SearchService
{
    private readonly IAppNavigationService _navigationService;
    private readonly ISettingsStore _settingsStore;

    public SearchService(ISettingsStore settingsStore, IAppNavigationService navigationService)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
    }

    /// <summary>
    /// 解析支持的输入，
    /// 支持的格式有：<para/>
    /// av号：av170001, AV170001, https://www.bilibili.com/video/av170001 <para/>
    /// BV号：BV17x411w7KC, https://www.bilibili.com/video/BV17x411w7KC, https://b23.tv/BV17x411w7KC <para/>
    /// 番剧（电影、电视剧）ss号：ss32982, SS32982, https://www.bilibili.com/bangumi/play/ss32982 <para/>
    /// 番剧（电影、电视剧）ep号：ep317925, EP317925, https://www.bilibili.com/bangumi/play/ep317925 <para/>
    /// 番剧（电影、电视剧）md号：md28228367, MD28228367, https://www.bilibili.com/bangumi/media/md28228367 <para/>
    /// 课程ss号：https://www.bilibili.com/cheese/play/ss205 <para/>
    /// 课程ep号：https://www.bilibili.com/cheese/play/ep3489 <para/>
    /// 收藏夹：ml1329019876, ML1329019876, https://www.bilibili.com/medialist/detail/ml1329019876, https://www.bilibili.com/medialist/play/ml1329019876/ <para/>
    /// UP主全部投稿：https://www.bilibili.com/list/3546801722362343 <para/>
    /// 用户空间：uid928123, UID928123, uid:928123, UID:928123, https://space.bilibili.com/928123
    /// </summary>
    /// <param name="input"></param>
    /// <param name="parentRoute"></param>
    /// <returns></returns>
    public bool BiliInput(string input, AppRoute parentRoute)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 移除剪贴板id
        var justId = input.Replace(AppConstant.ClipboardId, "", StringComparison.Ordinal);

        // 视频
        if (ParseEntrance.IsAvId(justId))
        {
            var avid = ParseEntrance.GetAvId(justId).ToString(CultureInfo.InvariantCulture);
            NavigateToVideo(parentRoute, $"{ParseEntrance.VideoUrl}av{avid}");
        }
        else if (ParseEntrance.IsAvUrl(justId))
        {
            NavigateToVideo(parentRoute, input);
        }
        else if (ParseEntrance.IsBvId(justId))
        {
            NavigateToVideo(parentRoute, $"{ParseEntrance.VideoUrl}{input}");
        }
        else if (ParseEntrance.IsBvUrl(justId))
        {
            NavigateToVideo(parentRoute, input);
        }
        // 番剧（电影、电视剧）
        else if (ParseEntrance.IsBangumiSeasonId(justId))
        {
            var seasonId = ParseEntrance.GetBangumiSeasonId(justId).ToString(CultureInfo.InvariantCulture);
            NavigateToVideo(parentRoute, $"{ParseEntrance.BangumiUrl}ss{seasonId}");
        }
        else if (ParseEntrance.IsBangumiSeasonUrl(justId))
        {
            NavigateToVideo(parentRoute, input);
        }
        else if (ParseEntrance.IsBangumiEpisodeId(justId))
        {
            var episodeId = ParseEntrance.GetBangumiEpisodeId(justId).ToString(CultureInfo.InvariantCulture);
            NavigateToVideo(parentRoute, $"{ParseEntrance.BangumiUrl}ep{episodeId}");
        }
        else if (ParseEntrance.IsBangumiEpisodeUrl(justId))
        {
            NavigateToVideo(parentRoute, input);
        }
        else if (ParseEntrance.IsBangumiMediaId(justId))
        {
            var mediaId = ParseEntrance.GetBangumiMediaId(justId).ToString(CultureInfo.InvariantCulture);
            NavigateToVideo(parentRoute, $"{ParseEntrance.BangumiMediaUrl}md{mediaId}");
        }
        else if (ParseEntrance.IsBangumiMediaUrl(justId))
        {
            NavigateToVideo(parentRoute, input);
        }
        // 课程
        else if (ParseEntrance.IsCheeseSeasonUrl(justId) || ParseEntrance.IsCheeseEpisodeUrl(justId))
        {
            NavigateToVideo(parentRoute, input);
        }
        // UP主全部投稿列表
        else if (ParseEntrance.IsUserVideoListUrl(justId))
        {
            _navigationService.Navigate(new AppNavigationRequest(
                AppRoute.Publication,
                parentRoute,
                PublicationNavigationPayload.All(ParseEntrance.GetUserVideoListId(justId))));
        }
        // 用户（参数传入mid）
        else if (ParseEntrance.IsUserId(justId))
        {
            NavigateToUserSpace(ParseEntrance.GetUserId(justId));
        }
        else if (ParseEntrance.IsUserUrl(justId))
        {
            NavigateToUserSpace(ParseEntrance.GetUserId(justId));
        }
        // 收藏夹
        else if (ParseEntrance.IsFavoritesId(justId))
        {
            _navigationService.Navigate(new AppNavigationRequest(
                AppRoute.PublicFavorites,
                parentRoute,
                ParseEntrance.GetFavoritesId(justId)));
        }
        else if (ParseEntrance.IsFavoritesUrl(justId))
        {
            _navigationService.Navigate(new AppNavigationRequest(
                AppRoute.PublicFavorites,
                parentRoute,
                ParseEntrance.GetFavoritesId(justId)));
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 搜索关键词
    /// </summary>
    /// <param name="key"></param>
    /// <param name="parentRoute"></param>
    public static void SearchKey(string key, AppRoute parentRoute)
    {
        // TODO
    }

    private void NavigateToVideo(AppRoute parentRoute, string input)
    {
        _navigationService.Navigate(new AppNavigationRequest(AppRoute.VideoDetail, parentRoute, input));
    }

    private void NavigateToUserSpace(long mid)
    {
        var route = _settingsStore.Current.User.Mid == mid
            ? AppRoute.MySpace
            : AppRoute.UserSpace;
        _navigationService.Navigate(new AppNavigationRequest(route, AppRoute.Index, mid));
    }
}
