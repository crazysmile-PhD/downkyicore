using System.Text.RegularExpressions;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.BiliApi.VideoStream;

public static class VideoStream
{
    /// <summary>
    /// 获取播放器信息（web端）
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    public static PlayerV2? PlayerV2(long avid, string? bvid, long cid, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>();

        if (avid > 0)
        {
            parameters.Add("bvid", bvid);
        }

        if (bvid != null)
        {
            parameters.Add("avid", avid);
        }

        if (cid > 0)
        {
            parameters.Add("cid", cid);
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(parameters));
        var url = $"https://api.bilibili.com/x/player/wbi/v2?{query}";
        const string referer = "https://www.bilibili.com";
        var playUrl = BiliApiRequest.RequestJson<PlayerV2Origin>(
            url,
            referer,
            nameof(PlayerV2),
            "PlayerV2()",
            cancellationToken);

        return playUrl?.Data;
    }

    /// <summary>
    /// 获取所有字幕<br/>
    /// 若视频没有字幕，返回空列表
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    public static List<SubRipText> GetSubtitle(long avid, string? bvid, long cid, CancellationToken cancellationToken = default)
    {
        var subRipTexts = new List<SubRipText>();

        // 获取播放器信息
        var player = PlayerV2(avid, bvid, cid, cancellationToken);
        if (player == null)
        {
            return subRipTexts;
        }

        if (player.Subtitle?.Subtitles == null || player.Subtitle.Subtitles.Count == 0)
        {
            return subRipTexts;
        }

        foreach (var subtitle in player.Subtitle.Subtitles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string referer = "https://www.bilibili.com";
            var subtitleUrl = NormalizeSubtitleUrl(subtitle.SubtitleUrl);
            if (subtitleUrl == null)
            {
                LogManager.Debug(nameof(GetSubtitle), $"Skip empty subtitle url. lan={subtitle.Lan}, lan_doc={subtitle.LanDoc}");
                continue;
            }

            var response = BiliApiRequest.RequestText(
                subtitleUrl,
                referer,
                nameof(GetSubtitle),
                "GetSubtitle()",
                cancellationToken);
            if (string.IsNullOrWhiteSpace(response))
            {
                LogManager.Debug(nameof(GetSubtitle), $"Skip empty subtitle response. lan={subtitle.Lan}, lan_doc={subtitle.LanDoc}, type={subtitle.Type}");
                continue;
            }

            try
            {
                var subtitleJson = JsonConvert.DeserializeObject<SubtitleJson>(response);
                if (subtitleJson?.Body == null || subtitleJson.Body.Count == 0)
                {
                    LogManager.Debug(nameof(GetSubtitle), $"Skip subtitle with empty body. lan={subtitle.Lan}, lan_doc={subtitle.LanDoc}, type={subtitle.Type}");
                    continue;
                }

                var srt = subtitleJson.ToSubRip();
                if (string.IsNullOrWhiteSpace(srt))
                {
                    continue;
                }

                subRipTexts.Add(new SubRipText
                {
                    Lan = subtitle.Lan,
                    LanDoc = subtitle.LanDoc,
                    SrtString = srt
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Console.PrintLine("GetSubtitle()发生异常: {0}", e);
                LogManager.Error("GetSubtitle()", e);
            }
        }

        return subRipTexts;
    }

    private static string? NormalizeSubtitleUrl(string? subtitleUrl)
    {
        if (string.IsNullOrWhiteSpace(subtitleUrl))
        {
            return null;
        }

        if (Uri.TryCreate(subtitleUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return subtitleUrl.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{subtitleUrl}"
            : $"https://{subtitleUrl.TrimStart('/')}";
    }

    /// <summary>
    /// 获取普通视频的视频流
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <param name="quality"></param>
    /// <returns></returns>
    public static PlayUrl? GetVideoPlayUrl(long avid, string bvid, long cid, int quality = 125, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            { "fourk", 1 },
            { "fnver", 0 },
            { "fnval", 4048 },
            { "cid", cid },
            { "qn", quality },
        };

        if (bvid != null)
        {
            parameters.Add("bvid", bvid);
        }
        else if (avid > -1)
        {
            parameters.Add("aid", avid);
        }
        else
        {
            return null;
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(parameters));
        var url = $"https://api.bilibili.com/x/player/wbi/playurl?{query}";

        return GetPlayUrl(url, cancellationToken);
    }

    /// <summary>
    /// 获取普通视频的视频流（WebPage方式）
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="p"></param>
    /// <returns></returns>
    public static PlayUrl? GetVideoPlayUrlWebPage(long avid, string bvid, long cid, int p, CancellationToken cancellationToken = default)
    {
        var url = "https://www.bilibili.com/video";
        if (bvid == string.Empty)
        {
            url = $"{url}/{bvid}/?p={p}";
        }
        else if (avid > -1)
        {
            url = $"{url}/av{avid}/?p={p}";
        }

        var playUrl = GetPlayUrlWebPage(url, cancellationToken);
        if (playUrl == null)
        {
            playUrl = GetVideoPlayUrl(avid, bvid, cid, cancellationToken: cancellationToken);
        }

        return playUrl;
    }

    // /// <summary>
    // /// 获取番剧的视频流
    // /// </summary>
    // /// <param name="avid"></param>
    // /// <param name="bvid"></param>
    // /// <param name="cid"></param>
    // /// <param name="quality"></param>
    // /// <returns></returns>
    public static PlayUrl? GetBangumiPlayUrl(long avid, string bvid, long cid, int quality = 125, CancellationToken cancellationToken = default)
    {
        var baseUrl = $"https://api.bilibili.com/pgc/player/web/playurl?cid={cid}&qn={quality}&fourk=1&fnver=0&fnval=4048";
        string url;
        if (bvid != null)
        {
            url = $"{baseUrl}&bvid={bvid}";
        }
        else if (avid > -1)
        {
            url = $"{baseUrl}&aid={avid}";
        }
        else
        {
            return null;
        }

        return GetPlayUrl(url, cancellationToken);
    }

    /// <summary>
    /// 获取课程的视频流
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <param name="quality"></param>
    /// <returns></returns>
    public static PlayUrl? GetCheesePlayUrl(long avid, string bvid, long cid, long episodeId, int quality = 125, CancellationToken cancellationToken = default)
    {
        var baseUrl = $"https://api.bilibili.com/pugv/player/web/playurl?cid={cid}&qn={quality}&fourk=1&fnver=0&fnval=4048";
        string url;
        if (bvid != null)
        {
            url = $"{baseUrl}&bvid={bvid}";
        }
        else if (avid > -1)
        {
            url = $"{baseUrl}&aid={avid}";
        }
        else
        {
            return null;
        }

        // 必须有episodeId，否则会返回请求错误
        if (episodeId != 0)
        {
            url += $"&ep_id={episodeId}";
        }

        return GetPlayUrl(url, cancellationToken);
    }

    /// <summary>
    /// 获取视频流
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static PlayUrl? GetPlayUrl(string url, CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        var playUrl = BiliApiRequest.RequestJson<PlayUrlOrigin>(
            url,
            referer,
            nameof(GetPlayUrl),
            "GetPlayUrl()",
            cancellationToken);

        if (playUrl == null)
        {
            return null;
        }

        if (playUrl.Data != null)
        {
            return playUrl.Data;
        }

        if (playUrl.Result != null)
        {
            return playUrl.Result;
        }

        return null;
    }

    /// <summary>
    /// 获取视频流（WebPage方式）
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static PlayUrl? GetPlayUrlWebPage(string url, CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        var response = BiliApiRequest.RequestText(
            url,
            referer,
            nameof(GetPlayUrlWebPage),
            "GetPlayUrlPc()",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        try
        {
            var regex = new Regex(@"<script>window\.__playinfo__=(.*?)<\/script>");
            var m = regex.Match(response);
            PlayUrlOrigin? playUrl = null;
            if (m.Success)
            {
                playUrl = JsonConvert.DeserializeObject<PlayUrlOrigin>(m.Groups[1].ToString());
            }

            if (playUrl == null)
            {
                return null;
            }

            if (playUrl.Data != null)
            {
                return playUrl.Data;
            }

            if (playUrl.Result != null)
            {
                return playUrl.Result;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Console.PrintLine("GetPlayUrlPc()发生异常: {0}", e);
            LogManager.Error("GetPlayUrlPc()", e);
            return null;
        }
    }
}
