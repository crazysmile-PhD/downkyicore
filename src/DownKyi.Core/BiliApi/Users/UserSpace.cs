using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users;

/// <summary>
/// 用户空间信息
/// </summary>
public static partial class UserSpace
{
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
        bool hasSavedLoginInfo,
        CancellationToken cancellationToken = default)
    {
        const int pn = 1;
        const int ps = 1;
        var publication = await client.GetPublicationPageAsync(
            keys,
            unixTimeSeconds,
            mid,
            pn,
            ps,
            hasSavedLoginInfo,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return GetPublicationType(publication?.List);
    }

    /// <summary>
    /// 获取用户投稿视频的所有分区
    /// </summary>
    /// <param name="publication"></param>
    /// <returns></returns>
    public static IReadOnlyList<SpacePublicationListTypeVideoZone>? GetPublicationType(SpacePublicationList? publication)
    {
        if (publication == null)
        {
            return null;
        }

        return publication.Tlist.Values
            .Where(static zone => zone.Count > 0)
            .ToList();
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
        bool hasSavedLoginInfo,
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
        if (!hasSavedLoginInfo)
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

}
