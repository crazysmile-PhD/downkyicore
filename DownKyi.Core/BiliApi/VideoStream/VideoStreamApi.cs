using System.Text.RegularExpressions;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream;

public static class VideoStreamApi
{
    internal enum PlayUrlPayloadField
    {
        Data,
        Result
    }

    /// <summary>
    /// 获取播放器信息（web端）
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    public static PlayerV2? PlayerV2(
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string? bvid,
        long cid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(bvid))
        {
            parameters.Add("bvid", bvid);
        }

        if (avid > 0)
        {
            parameters.Add("aid", avid);
        }

        if (cid > 0)
        {
            parameters.Add("cid", cid);
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(
            parameters,
            keys.ImgKey,
            keys.SubKey,
            unixTimeSeconds));
        var url = $"https://api.bilibili.com/x/player/wbi/v2?{query}";
        const string referer = "https://www.bilibili.com";
        var playUrl = BiliApiRequest.RequestJson<PlayerV2Origin>(
            url,
            referer,
            nameof(PlayerV2),
            "PlayerV2()",
            cancellationToken);

        return BiliApiRequest.RequirePayload(playUrl.Data);
    }

    /// <summary>
    /// 获取所有字幕<br/>
    /// 若视频没有字幕，返回空列表
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    public static IReadOnlyList<SubRipText> GetSubtitle(
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string? bvid,
        long cid,
        CancellationToken cancellationToken = default)
    {
        return GetSubtitle(
            keys,
            unixTimeSeconds,
            avid,
            bvid,
            cid,
            reportParseFailure: null,
            cancellationToken);
    }

    public static IReadOnlyList<SubRipText> GetSubtitle(
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string? bvid,
        long cid,
        Action<Exception>? reportParseFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var subRipTexts = new List<SubRipText>();

        // 获取播放器信息
        var player = PlayerV2(keys, unixTimeSeconds, avid, bvid, cid, cancellationToken);
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
            var subtitleUrl = NormalizeSubtitleUrl(subtitle.SubtitleAddress);
            if (subtitleUrl == null)
            {
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
                continue;
            }

            try
            {
                var subtitleJson = JsonConvert.DeserializeObject<SubtitleJson>(response);
                if (subtitleJson?.Body == null || subtitleJson.Body.Count == 0)
                {
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
            catch (JsonException e)
            {
                reportParseFailure?.Invoke(e);
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
    public static PlayUrl? GetVideoPlayUrl(
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string bvid,
        long cid,
        int quality = 125,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
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

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(
            parameters,
            keys.ImgKey,
            keys.SubKey,
            unixTimeSeconds));
        var url = $"https://api.bilibili.com/x/player/wbi/playurl?{query}";

        return GetPlayUrl(
            url,
            PlayUrlPayloadField.Data,
            nameof(GetVideoPlayUrl),
            cancellationToken);
    }

    /// <summary>
    /// 获取普通视频的视频流（WebPage方式）
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="p"></param>
    /// <returns></returns>
    public static PlayUrl? GetVideoPlayUrlWebPage(
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string bvid,
        long cid,
        int p,
        CancellationToken cancellationToken = default)
    {
        var url = BuildVideoPlayPageUrl(avid, bvid, p);
        var playUrl = GetPlayUrlWebPage(url, cancellationToken);
        if (playUrl == null)
        {
            playUrl = GetVideoPlayUrl(
                keys,
                unixTimeSeconds,
                avid,
                bvid,
                cid,
                cancellationToken: cancellationToken);
        }

        return playUrl;
    }

    internal static string BuildVideoPlayPageUrl(long avid, string bvid, int p)
    {
        const string baseUrl = "https://www.bilibili.com/video";
        if (!string.IsNullOrEmpty(bvid))
        {
            return $"{baseUrl}/{bvid}/?p={p}";
        }

        if (avid > -1)
        {
            return $"{baseUrl}/av{avid}/?p={p}";
        }

        return baseUrl;
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

        return GetPlayUrl(
            url,
            PlayUrlPayloadField.Result,
            nameof(GetBangumiPlayUrl),
            cancellationToken);
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

        return GetPlayUrl(
            url,
            PlayUrlPayloadField.Data,
            nameof(GetCheesePlayUrl),
            cancellationToken);
    }

    /// <summary>
    /// 获取视频流
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static PlayUrl GetPlayUrl(
        string url,
        PlayUrlPayloadField payloadField,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        var response = BiliApiRequest.RequestJson<PlayUrlOrigin>(
            url,
            referer,
            operationName,
            "GetPlayUrl()",
            cancellationToken);

        return SelectPlayUrlPayload(response, payloadField, operationName);
    }

    internal static PlayUrl SelectPlayUrlPayload(
        PlayUrlOrigin response,
        PlayUrlPayloadField payloadField,
        string operationName)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var payload = payloadField switch
        {
            PlayUrlPayloadField.Data => response.Data,
            PlayUrlPayloadField.Result => response.Result,
            _ => throw new ArgumentOutOfRangeException(nameof(payloadField), payloadField, null)
        };
        var fieldName = payloadField == PlayUrlPayloadField.Data ? "data" : "result";
        if (payload == null)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned no '{fieldName}' playback payload.");
        }

        if (!HasPlayableMedia(payload))
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned an empty '{fieldName}' playback payload.");
        }

        return payload;
    }

    private static bool HasPlayableMedia(PlayUrl payload)
    {
        return payload.Durl.Count > 0
               || payload.Dash.Video.Count > 0
               || payload.Dash.Audio.Count > 0;
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

            return SelectPlayUrlPayload(
                playUrl,
                PlayUrlPayloadField.Data,
                nameof(GetPlayUrlWebPage));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (BilibiliApiResponseException)
        {
            return null;
        }
    }
}
