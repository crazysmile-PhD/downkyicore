using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户空间信息
/// </summary>
public static class UserSpace
{
    /// <summary>
    /// 查询空间设置
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static SpaceSettings? GetSpaceSettings(long mid)
    {
        var url = $"https://space.bilibili.com/ajax/settings/getSettings?mid={mid}";
        const string referer = "https://www.bilibili.com";
        var settings = BiliApiRequest.RequestJson<SpaceSettingsOrigin>(
            url,
            referer,
            nameof(GetSpaceSettings),
            "UserSpace");

        return settings is not { Status: true } ? null : settings.Data;
    }

    #region 投稿

    /// <summary>
    /// 获取用户投稿视频的所有分区
    /// </summary>
    /// <param name="mid">用户id</param>
    /// <returns></returns>
    public static IReadOnlyList<SpacePublicationListTypeVideoZone>? GetPublicationType(long mid)
    {
        const int pn = 1;
        const int ps = 1;
        var publication = GetPublication(mid, pn, ps);
        return GetPublicationType(publication);
    }

    /// <summary>
    /// 获取用户投稿视频的所有分区
    /// </summary>
    /// <param name="publication"></param>
    /// <returns></returns>
    public static IReadOnlyList<SpacePublicationListTypeVideoZone>? GetPublicationType(SpacePublicationList? publication)
    {
        if (publication?.Tlist == null)
        {
            return null;
        }

        var result = new List<SpacePublicationListTypeVideoZone>();
        var typeList = JObject.Parse(publication.Tlist.ToString("N"));
        foreach (var item in typeList)
        {
            if (item.Value == null) continue;
            var value = JsonConvert.DeserializeObject<SpacePublicationListTypeVideoZone>(item.Value.ToString());
            if (value != null)
                result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// 查询用户所有的投稿视频明细
    /// </summary>
    /// <param name="mid">用户id</param>
    /// <param name="order">排序</param>
    /// <param name="tid">视频分区</param>
    /// <param name="keyword">搜索关键词</param>
    /// <returns></returns>
    public static IReadOnlyList<SpacePublicationListVideo> GetAllPublication(long mid, int tid = 0, PublicationOrder order = PublicationOrder.PUBDATE, string keyword = "")
    {
        var result = new List<SpacePublicationListVideo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 100;

            var data = GetPublication(mid, i, ps, tid, order, keyword);
            if (data?.Vlist == null || data.Vlist.Count == 0)
            {
                break;
            }

            result.AddRange(data.Vlist);
        }

        return result;
    }

    /// <summary>
    /// 查询用户投稿视频明细
    /// </summary>
    /// <param name="mid">用户id</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页的视频数</param>
    /// <param name="order">排序</param>
    /// <param name="tid">视频分区</param>
    /// <param name="keyword">搜索关键词</param>
    /// <returns></returns>
    public static SpacePublicationList? GetPublication(long mid, int pn, int ps, long tid = 0, PublicationOrder order = PublicationOrder.PUBDATE, string keyword = "")
    {
        return GetPublicationResult(mid, pn, ps, tid, order, keyword)?.List;
    }

    /// <summary>
    /// 查询用户投稿视频和筛选后的分页信息。
    /// </summary>
    public static SpacePublication? GetPublicationResult(
        long mid,
        int pn,
        int ps,
        long tid = 0,
        PublicationOrder order = PublicationOrder.PUBDATE,
        string keyword = "")
    {
        var parameters = new Dictionary<string, object?>
        {
            { "mid", mid },
            { "pn", pn },
            { "ps", ps },
            { "order", GetPublicationOrderValue(order) },
            { "tid", tid },
            { "keyword", keyword },
        };
        if (!File.Exists(StorageManager.GetLogin()))
        {
            parameters.Add("dm_img_str", "V2ViR0wgMS");
            parameters.Add("dm_img_list", "[]");
            parameters.Add("dm_cover_img_str", "QU5HTEUgKE5WSURJQSwgTlZJRElBIEdlRm9yY2UgR1RYIDk4MCBEaXJlY3QzRDExIHZzXzVfMCBwc181XzApLCBvciBzaW1pbGFyR29vZ2xlIEluYy4gKE5WSURJQS");
            parameters.Add("dm_img_inter", "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}");
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(parameters));
        var url = $"https://api.bilibili.com/x/space/wbi/arc/search?{query}";
        const string referer = "https://www.bilibili.com";

        try
        {
            var response = WebClient.RequestWeb(url, referer);
            // 忽略play的值为“--”时的类型错误
            var settings = new JsonSerializerSettings
            {
                Error = (sender, args) =>
                {
                    if (Equals(args.ErrorContext.Member, "play") && args.ErrorContext.OriginalObject?.GetType() == typeof(SpacePublicationListVideo))
                    {
                        args.ErrorContext.Handled = true;
                    }
                }
            };

            var spacePublication = JsonConvert.DeserializeObject<SpacePublicationOrigin>(response, settings);
            return spacePublication?.Data;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            Console.PrintLine("GetPublication()发生异常: {0}", e);
            LogManager.Error("UserSpace", e);
            return null;
        }
        catch (JsonException e)
        {
            Console.PrintLine("GetPublication()JSON解析异常: {0}", e);
            LogManager.Error("UserSpace", e);
            return null;
        }
    }

    internal static string GetPublicationOrderValue(PublicationOrder order)
    {
        return order switch
        {
            PublicationOrder.None => "none",
            PublicationOrder.PUBDATE => "pubdate",
            PublicationOrder.CLICK => "click",
            PublicationOrder.STOW => "stow",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unsupported publication order.")
        };
    }

    #endregion

    #region UP主视频列表

    /// <summary>
    /// 查询 /list/{mid} 页面中的UP主全部视频。
    /// </summary>
    public static UserVideoListData? GetUserVideoList(
        long mid,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/space/arc/list?mid={mid}&pn={pageNumber}&ps={pageSize}";
        var referer = $"https://www.bilibili.com/list/{mid}";
        var origin = BiliApiRequest.RequestJson<UserVideoListOrigin>(
            url,
            referer,
            nameof(GetUserVideoList),
            "UserSpace",
            cancellationToken);

        return origin?.Data;
    }

    #endregion

    #region 频道

    /// <summary>
    /// 查询用户频道列表
    /// </summary>
    /// <param name="mid">用户id</param>
    /// <returns></returns>
    public static IReadOnlyList<SpaceChannelList>? GetChannelList(long mid)
    {
        var url = $"https://api.bilibili.com/x/space/channel/list?mid={mid}";
        const string referer = "https://www.bilibili.com";
        var spaceChannel = BiliApiRequest.RequestJson<SpaceChannelOrigin>(
            url,
            referer,
            nameof(GetChannelList),
            "UserSpace");

        return spaceChannel?.Data.List;
    }

    /// <summary>
    /// 查询用户频道中的所有视频
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    public static IReadOnlyList<SpaceChannelArchive?> GetAllChannelVideoList(long mid, long cid)
    {
        var result = new List<SpaceChannelArchive?>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 100;

            var data = GetChannelVideoList(mid, cid, i, ps);
            if (data == null || data.Count == 0)
            {
                break;
            }

            result.AddRange(data);
        }

        return result;
    }

    /// <summary>
    /// 查询用户频道中的视频
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="cid"></param>
    /// <param name="pn"></param>
    /// <param name="ps"></param>
    /// <returns></returns>
    public static IReadOnlyList<SpaceChannelArchive>? GetChannelVideoList(long mid, long cid, int pn, int ps)
    {
        var url = $"https://api.bilibili.com/x/space/channel/video?mid={mid}&cid={cid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var spaceChannelVideo = BiliApiRequest.RequestJson<SpaceChannelVideoOrigin>(
            url,
            referer,
            nameof(GetChannelVideoList),
            "UserSpace");

        return spaceChannelVideo?.Data.List.Archives;
    }

    #endregion

    #region 合集和列表

    /// <summary>
    /// 查询用户的合集和列表
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="pageNum">第几页</param>
    /// <param name="pageSize">每页的数量；最大值为20</param>
    /// <returns></returns>
    public static SpaceSeasonsSeries? GetSeasonsSeries(long mid, int pageNum, int pageSize)
    {
        // https://api.bilibili.com/x/polymer/space/seasons_series_list?mid=49246269&page_num=1&page_size=18
        var url = $"https://api.bilibili.com/x/polymer/web-space/seasons_series_list?mid={mid}&page_num={pageNum}&page_size={pageSize}";
        const string referer = "https://www.bilibili.com";
        var origin = BiliApiRequest.RequestJson<SpaceSeasonsSeriesOrigin>(
            url,
            referer,
            nameof(GetSeasonsSeries),
            "UserSpace");

        return origin?.Data.ItemsLists;
    }

    /// <summary>
    /// 查询用户的合集的视频详情
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="seasonId"></param>
    /// <param name="pageNum"></param>
    /// <param name="pageSize"></param>
    public static SpaceSeasonsDetail? GetSeasonsDetail(long mid, long seasonId, int pageNum, int pageSize)
    {
        // https://api.bilibili.com/x/polymer/space/seasons_archives_list?mid=23947287&season_id=665&sort_reverse=false&page_num=1&page_size=30
        var url = $"https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid={mid}&season_id={seasonId}&page_num={pageNum}&page_size={pageSize}&sort_reverse=false";
        const string referer = "https://www.bilibili.com";
        var origin = BiliApiRequest.RequestJson<SpaceSeasonsDetailOrigin>(
            url,
            referer,
            nameof(GetSeasonsDetail),
            "UserSpace");

        return origin?.Data;
    }

    /// <summary>
    /// 查询用户的列表元数据
    /// </summary>
    /// <param name="seriesId"></param>
    /// <returns></returns>
    public static SpaceSeriesMetaData? GetSeriesMeta(long seriesId)
    {
        // https://api.bilibili.com/x/series/series?series_id=1253087
        var url = $"https://api.bilibili.com/x/series/series?series_id={seriesId}";
        const string referer = "https://www.bilibili.com";
        var origin = BiliApiRequest.RequestJson<SpaceSeriesMetaOrigin>(
            url,
            referer,
            nameof(GetSeriesMeta),
            "UserSpace");

        return origin?.Data;
    }

    /// <summary>
    /// 查询用户的列表的视频详情
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="seriesId"></param>
    /// <param name="pn"></param>
    /// <param name="ps"></param>
    /// <returns></returns>
    public static SpaceSeriesDetail? GetSeriesDetail(long mid, long seriesId, int pn, int ps)
    {
        // https://api.bilibili.com/x/series/archives?mid=27899754&series_id=1253087&only_normal=true&sort=desc&pn=1&ps=30

        var url = $"https://api.bilibili.com/x/series/archives?mid={mid}&series_id={seriesId}&only_normal=true&sort=desc&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var origin = BiliApiRequest.RequestJson<SpaceSeriesDetailOrigin>(
            url,
            referer,
            nameof(GetSeriesDetail),
            "UserSpace");

        return origin?.Data;
    }

    #endregion

    #region 课程

    /// <summary>
    /// 查询用户发布的课程列表
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static IReadOnlyList<SpaceCheese>? GetCheese(long mid, int pn, int ps)
    {
        var url = $"https://api.bilibili.com/pugv/app/web/season/page?mid={mid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var cheese = BiliApiRequest.RequestJson<SpaceCheeseOrigin>(
            url,
            referer,
            nameof(GetCheese),
            "UserSpace");

        return cheese?.Data.Items;
    }

    /// <summary>
    /// 查询用户发布的所有课程列表
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <returns></returns>
    public static IReadOnlyList<SpaceCheese> GetAllCheese(long mid)
    {
        var result = new List<SpaceCheese>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 50;

            var data = GetCheese(mid, i, ps);
            if (data == null || data.Count == 0)
            {
                break;
            }

            result.AddRange(data);
        }

        return result;
    }

    #endregion

    #region 订阅

    /// <summary>
    /// 查询用户追番（追剧）明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="type">查询类型</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数</param>
    /// <returns></returns>
    public static BangumiFollowData? GetBangumiFollow(long mid, BangumiType type, int pn, int ps)
    {
        var url = $"https://api.bilibili.com/x/space/bangumi/follow/list?vmid={mid}&type={type:D}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var bangumiFollow = BiliApiRequest.RequestJson<BangumiFollowOrigin>(
            url,
            referer,
            nameof(GetBangumiFollow),
            "UserSpace");

        return bangumiFollow?.Data;
    }

    /// <summary>
    /// 查询用户所有的追番（追剧）明细
    /// </summary>
    /// <param name="mid">目标用户UID</param>
    /// <param name="type">查询类型</param>
    /// <returns></returns>
    public static IReadOnlyList<BangumiFollow> GetAllBangumiFollow(long mid, BangumiType type)
    {
        var result = new List<BangumiFollow>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 30;

            var data = GetBangumiFollow(mid, type, i, ps);
            if (data?.List == null || data.List.Count == 0)
            {
                break;
            }

            result.AddRange(data.List);
        }

        return result;
    }

    #endregion
}
