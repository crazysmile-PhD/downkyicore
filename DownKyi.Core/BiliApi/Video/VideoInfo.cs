using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video.Models;
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
    public static async Task<VideoView?> VideoViewInfoAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        string? bvid = null,
        long aid = -1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
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
        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(
            parameters,
            keys.ImgKey,
            keys.SubKey,
            unixTimeSeconds));
        var url = $"https://api.bilibili.com/x/web-interface/wbi/view?{query}";
        const string referer = "https://www.bilibili.com";
        var videoView = await BiliApiRequest.RequestJsonAsync<VideoViewOrigin>(
            client,
            url,
            referer,
            nameof(VideoViewInfoAsync),
            "VideoInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ValidateVideoView(BiliApiRequest.RequirePayload(videoView.Data));
    }

    internal static VideoView ValidateVideoView(VideoView videoView)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        if (videoView.Aid <= 0 || string.IsNullOrWhiteSpace(videoView.Bvid))
        {
            throw new BilibiliApiResponseException(
                nameof(VideoViewInfoAsync),
                "Video information payload did not contain valid AV/BV identifiers.");
        }

        if (videoView.Pages is not { Count: > 0 })
        {
            throw new BilibiliApiResponseException(
                nameof(VideoViewInfoAsync),
                "Video information payload did not contain any pages.");
        }

        if (videoView.Pages.Any(page => page.Cid <= 0))
        {
            throw new BilibiliApiResponseException(
                nameof(VideoViewInfoAsync),
                "Video information payload contained a page without a valid CID.");
        }

        return videoView;
    }

    /// <summary>
    /// 获取视频简介
    /// </summary>
    /// <param name="bvid"></param>
    /// <param name="aid"></param>
    /// <returns></returns>
    public static async Task<string?> VideoDescriptionAsync(
        this IBilibiliApiClient client,
        string? bvid = null,
        long aid = -1,
        CancellationToken cancellationToken = default)
    {
        const string baseUrl = "https://api.bilibili.com/x/web-interface/archive/desc";
        const string referer = "https://www.bilibili.com";
        string url;
        if (bvid != null) { url = $"{baseUrl}?bvid={bvid}"; }
        else if (aid >= -1) { url = $"{baseUrl}?aid={aid}"; }
        else { return null; }

        var desc = await BiliApiRequest.RequestJsonAsync<VideoDescription>(
            client,
            url,
            referer,
            nameof(VideoDescriptionAsync),
            "VideoInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(desc.Data);
    }

    /// <summary>
    /// 查询视频分P列表 (avid/bvid转cid)
    /// </summary>
    /// <param name="bvid"></param>
    /// <param name="aid"></param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<VideoPage>?> VideoPagelistAsync(
        this IBilibiliApiClient client,
        string? bvid = null,
        long aid = -1,
        CancellationToken cancellationToken = default)
    {
        const string baseUrl = "https://api.bilibili.com/x/player/pagelist";
        const string referer = "https://www.bilibili.com";
        string url;
        if (bvid != null) { url = $"{baseUrl}?bvid={bvid}"; }
        else if (aid > -1) { url = $"{baseUrl}?aid={aid}"; }
        else { return null; }

        var pagelist = await BiliApiRequest.RequestJsonAsync<VideoPagelist>(
            client,
            url,
            referer,
            nameof(VideoPagelistAsync),
            "VideoInfo",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(pagelist.Data);
    }

    public static async Task<IReadOnlyList<BiliTagInfo>?> GetBiliTagInfoAsync(
        this IBilibiliApiClient client,
        string bvid,
        long? cid = null,
        CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        string cidStr = cid.HasValue ? $"&cid={cid}" : "";
        string api = $"https://api.bilibili.com/x/web-interface/view/detail/tag?bvid={bvid}{cidStr}";
        var result = await BiliApiRequest.RequestJsonAsync<TagResult>(
            client,
            api,
            referer,
            nameof(GetBiliTagInfoAsync),
            "GetBiliTagInfo()",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(result.Data);
    }


}
