using System;
using System.Linq;
using DownKyi.Application.Desktop;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;

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
    public bool BiliInput(
        string input,
        AppRoute parentRoute,
        bool suppressDuplicateVideoNavigation = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch (ParseInput(input))
        {
            case BilibiliMediaInput media:
                NavigateToVideo(parentRoute, media, suppressDuplicateVideoNavigation);
                break;
            case BilibiliSeriesInput series:
                _navigationService.Navigate(new AppNavigationRequest(
                    AppRoute.SeasonsSeries,
                    parentRoute,
                    new SeriesNavigationPayload(series.Mid, series.SeriesId)));
                break;
            case BilibiliPublicationInput publication:
                _navigationService.Navigate(new AppNavigationRequest(
                    AppRoute.Publication,
                    parentRoute,
                    PublicationNavigationPayload.All(publication.Mid)));
                break;
            case BilibiliUserSpaceInput user:
                NavigateToUserSpace(user.Mid);
                break;
            case BilibiliFavoritesInput favorites:
                _navigationService.Navigate(new AppNavigationRequest(
                    AppRoute.PublicFavorites,
                    parentRoute,
                    favorites.MediaId));
                break;
            default:
                return false;
        }

        return true;
    }

    private static BilibiliInput? ParseInput(string input)
    {
        var value = input.Trim();
        var direct = BilibiliInputParser.Parse(value);
        if (direct != null && !value.Any(char.IsWhiteSpace) && !value.Contains('】', StringComparison.Ordinal))
        {
            return direct;
        }

        string? link = null;
        Range? previous = null;
        var count = 0;
        var standalone = false;
        for (var offset = 0; offset < value.Length;)
        {
            var separator = value.IndexOf("://", offset, StringComparison.Ordinal);
            if (separator < 0)
            {
                break;
            }

            var start = separator;
            while (start > offset && (char.IsAsciiLetterOrDigit(value[start - 1])
                                      || value[start - 1] is '+' or '-' or '.'))
            {
                start--;
            }

            if (start == separator || (start > 0 && !char.IsWhiteSpace(value[start - 1])
                                                 && value[start - 1] is not ('【' or '】' or '(')))
            {
                return null;
            }

            var end = separator + 3;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != '】')
            {
                end++;
            }

            if (previous is { } prior && !KeepSameLink(value, prior, start, ref link))
            {
                return null;
            }

            if (count++ == 0)
            {
                standalone = start == 0;
            }

            previous = new Range(start, end);
            offset = end;
        }

        if (previous is not { } last)
        {
            return null;
        }

        // A standalone URI is accepted or rejected as a whole, never retried as a prefix.
        if (count == 1 && standalone)
        {
            return direct;
        }

        return KeepSameLink(value, last, value.Length, ref link) ? BilibiliInputParser.Parse(link) : null;
    }

    private static bool KeepSameLink(string value, Range token, int boundary, ref string? link)
    {
        var start = token.Start.Value;
        var end = token.End.Value;
        // Query/fragment whitespace belongs to the URI, including potentially invalid suffixes.
        if (value.AsSpan(start, end - start).IndexOfAny('?', '#') >= 0)
        {
            end = boundary;
        }

        var candidate = value.AsSpan(start, end - start).Trim();
        if (link != null)
        {
            return candidate.SequenceEqual(link.AsSpan());
        }

        link = candidate.ToString();
        return true;
    }

    private void NavigateToVideo(
        AppRoute parentRoute,
        BilibiliMediaInput input,
        bool suppressDuplicate)
    {
        _navigationService.Navigate(new AppNavigationRequest(
            AppRoute.VideoDetail,
            parentRoute,
            new VideoNavigationPayload(input, suppressDuplicate)));
    }

    private void NavigateToUserSpace(long mid)
    {
        var route = _settingsStore.Current.User.Mid == mid
            ? AppRoute.MySpace
            : AppRoute.UserSpace;
        _navigationService.Navigate(new AppNavigationRequest(route, AppRoute.Index, mid));
    }
}
