using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video;

public static class VideoInfo
{
    /// <summary>
    /// 获取视频详细信息(web端)
    /// </summary>
    /// <param name="bvid"></param>
    /// <param name="aid"></param>
    /// <returns></returns>
    public static VideoView? VideoViewInfo(
        ISettingsStore settingsStore,
        string? bvid = null,
        long aid = -1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        // https://api.bilibili.com/x/web-interface/view/detail?bvid=BV1Sg411F7cb&aid=969147110&need_operation_card=1&web_rm_repeat=1&need_elec=1&out_referer=https%3A%2F%2Fspace.bilibili.com%2F42018135%2Ffavlist%3Ffid%3D94341835

        var parameters = new Dictionary<string, object?>();
        if (bvid != null)
        {
            parameters.Add("bvid", bvid);
        }
        else if (aid > -1)
        {
            parameters.Add("aid", aid);
        }
        else
        {
            return null;
        }
        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(parameters, settingsStore));
        var url = $"https://api.bilibili.com/x/web-interface/wbi/view?{query}";
        const string referer = "https://www.bilibili.com";
        var videoView = BiliApiRequest.RequestJson<VideoViewOrigin>(
            url,
            referer,
            nameof(VideoViewInfo),
            "VideoInfo",
            cancellationToken);

        return BiliApiRequest.RequirePayload(videoView.Data);
    }

    /// <summary>
    /// 获取视频简介
    /// </summary>
    /// <param name="bvid"></param>
    /// <param name="aid"></param>
    /// <returns></returns>
    public static string? VideoDescription(string? bvid = null, long aid = -1, CancellationToken cancellationToken = default)
    {
        const string baseUrl = "https://api.bilibili.com/x/web-interface/archive/desc";
        const string referer = "https://www.bilibili.com";
        string url;
        if (bvid != null) { url = $"{baseUrl}?bvid={bvid}"; }
        else if (aid >= -1) { url = $"{baseUrl}?aid={aid}"; }
        else { return null; }

        var desc = BiliApiRequest.RequestJson<VideoDescription>(
            url,
            referer,
            nameof(VideoDescription),
            "VideoInfo",
            cancellationToken);

        return BiliApiRequest.RequirePayload(desc.Data);
    }

    /// <summary>
    /// 查询视频分P列表 (avid/bvid转cid)
    /// </summary>
    /// <param name="bvid"></param>
    /// <param name="aid"></param>
    /// <returns></returns>
    public static IReadOnlyList<VideoPage>? VideoPagelist(string? bvid = null, long aid = -1, CancellationToken cancellationToken = default)
    {
        const string baseUrl = "https://api.bilibili.com/x/player/pagelist";
        const string referer = "https://www.bilibili.com";
        string url;
        if (bvid != null) { url = $"{baseUrl}?bvid={bvid}"; }
        else if (aid > -1) { url = $"{baseUrl}?aid={aid}"; }
        else { return null; }

        var pagelist = BiliApiRequest.RequestJson<VideoPagelist>(
            url,
            referer,
            nameof(VideoPagelist),
            "VideoInfo",
            cancellationToken);

        return BiliApiRequest.RequirePayload(pagelist.Data);
    }

    public static IReadOnlyList<BiliTagInfo>? GetBiliTagInfo(string bvid, long? cid = null, CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        string cidStr = cid.HasValue ? $"&cid={cid}" : "";
        string api = $"https://api.bilibili.com/x/web-interface/view/detail/tag?bvid={bvid}{cidStr}";
        var result = BiliApiRequest.RequestJson<TagResult>(
            api,
            referer,
            nameof(GetBiliTagInfo),
            "GetBiliTagInfo()",
            cancellationToken);

        return BiliApiRequest.RequirePayload(result.Data);
    }


}
