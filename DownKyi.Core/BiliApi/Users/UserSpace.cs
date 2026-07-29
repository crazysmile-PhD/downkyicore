using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户空间信息
/// </summary>
public static partial class UserSpace
{
    /// <summary>
    /// 查询空间设置
    /// </summary>
    /// <param name="mid"></param>
    /// <returns></returns>
    public static async Task<SpaceSettings?> GetSpaceSettingsAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://space.bilibili.com/ajax/settings/getSettings?mid={mid}";
        const string referer = "https://www.bilibili.com";
        var settings = await BiliApiRequest.RequestJsonAsync<SpaceSettingsOrigin>(
            client,
            url,
            referer,
            nameof(GetSpaceSettingsAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!settings.Status)
        {
            return null;
        }

        return BiliApiRequest.RequirePayload(settings.Data);
    }

    #region 投稿

    /// <summary>
    /// 获取用户投稿视频的所有分区
    /// </summary>
    /// <param name="mid">用户id</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<SpacePublicationListTypeVideoZone>?> GetPublicationTypeAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long mid,
        CancellationToken cancellationToken = default)
    {
        const int pn = 1;
        const int ps = 1;
        var publication = await client.GetPublicationAsync(
            keys,
            unixTimeSeconds,
            mid,
            pn,
            ps,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
            if (value is { Count: > 0 })
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
    public static async Task<IReadOnlyList<SpacePublicationListVideo>> GetAllPublicationAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long mid,
        int tid = 0,
        PublicationOrder order = PublicationOrder.PUBDATE,
        string keyword = "",
        CancellationToken cancellationToken = default)
    {
        var result = new List<SpacePublicationListVideo>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 100;

            var data = await client.GetPublicationAsync(
                keys,
                unixTimeSeconds,
                mid,
                i,
                ps,
                tid,
                order,
                keyword,
                cancellationToken).ConfigureAwait(false);
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
    public static async Task<SpacePublicationList?> GetPublicationAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long mid,
        int pn,
        int ps,
        long tid = 0,
        PublicationOrder order = PublicationOrder.PUBDATE,
        string keyword = "",
        CancellationToken cancellationToken = default)
    {
        var page = await client.GetPublicationPageAsync(
            keys,
            unixTimeSeconds,
            mid,
            pn,
            ps,
            tid,
            order,
            keyword,
            cancellationToken).ConfigureAwait(false);
        return page?.List;
    }

    /// <summary>
    /// 查询用户投稿视频及服务端分页信息。
    /// </summary>
    public static async Task<SpacePublication?> GetPublicationPageAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long mid,
        int pn,
        int ps,
        long tid = 0,
        PublicationOrder order = PublicationOrder.PUBDATE,
        string keyword = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var parameters = new Dictionary<string, object?>
        {
            { "mid", mid },
            { "pn", pn },
            { "ps", ps },
            { "order", GetPublicationOrderValue(order) },
            { "tid", tid },
            { "keyword", keyword },
        };
        if (!File.Exists(ApplicationStorage.GetLogin()))
        {
            parameters.Add("dm_img_str", "V2ViR0wgMS");
            parameters.Add("dm_img_list", "[]");
            parameters.Add("dm_cover_img_str", "QU5HTEUgKE5WSURJQSwgTlZJRElBIEdlRm9yY2UgR1RYIDk4MCBEaXJlY3QzRDExIHZzXzVfMCBwc181XzApLCBvciBzaW1pbGFyR29vZ2xlIEluYy4gKE5WSURJQS");
            parameters.Add("dm_img_inter", "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}");
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(
            parameters,
            keys.ImgKey,
            keys.SubKey,
            unixTimeSeconds));
        var url = $"https://api.bilibili.com/x/space/wbi/arc/search?{query}";
        const string referer = "https://www.bilibili.com";

        var serializerSettings = new JsonSerializerSettings
        {
            // 忽略play的值为“--”时的类型错误
            Error = (sender, args) =>
            {
                if (Equals(args.ErrorContext.Member, "play") && args.ErrorContext.OriginalObject?.GetType() == typeof(SpacePublicationListVideo))
                {
                    args.ErrorContext.Handled = true;
                }
            }
        };
        var spacePublication = await BiliApiRequest.RequestJsonAsync<SpacePublicationOrigin>(
            client,
            url,
            referer,
            nameof(GetPublicationPageAsync),
            "UserSpace",
            serializerSettings,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(spacePublication.Data);
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

    #region 频道

    /// <summary>
    /// 查询用户频道列表
    /// </summary>
    /// <param name="mid">用户id</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<SpaceChannelList>?> GetChannelListAsync(
        this IBilibiliApiClient client,
        long mid,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/space/channel/list?mid={mid}";
        const string referer = "https://www.bilibili.com";
        var spaceChannel = await BiliApiRequest.RequestJsonAsync<SpaceChannelOrigin>(
            client,
            url,
            referer,
            nameof(GetChannelListAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(spaceChannel.Data).List;
    }

    /// <summary>
    /// 查询用户频道中的所有视频
    /// </summary>
    /// <param name="mid"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<SpaceChannelArchive?>> GetAllChannelVideoListAsync(
        this IBilibiliApiClient client,
        long mid,
        long cid,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SpaceChannelArchive?>();

        var i = 0;
        while (true)
        {
            i++;
            const int ps = 100;

            var data = await client.GetChannelVideoListAsync(
                mid,
                cid,
                i,
                ps,
                cancellationToken).ConfigureAwait(false);
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
    public static async Task<IReadOnlyList<SpaceChannelArchive>?> GetChannelVideoListAsync(
        this IBilibiliApiClient client,
        long mid,
        long cid,
        int pn,
        int ps,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/space/channel/video?mid={mid}&cid={cid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var spaceChannelVideo = await BiliApiRequest.RequestJsonAsync<SpaceChannelVideoOrigin>(
            client,
            url,
            referer,
            nameof(GetChannelVideoListAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(spaceChannelVideo.Data).List.Archives;
    }

    #endregion

}
