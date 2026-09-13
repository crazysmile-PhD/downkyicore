using System.Text.RegularExpressions;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream;

public static partial class VideoStreamApi
{
    internal enum PlayUrlPayloadField
    {
        Data,
        Result
    }

    /// <summary>
    /// 获取普通视频的视频流
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <param name="quality"></param>
    /// <returns></returns>
    public static Task<PlayUrl?> GetVideoPlayUrlAsync(
        this IBilibiliApiClient client,
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
            return Task.FromResult<PlayUrl?>(null);
        }

        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(
            parameters,
            keys.ImgKey,
            keys.SubKey,
            unixTimeSeconds));
        var url = $"https://api.bilibili.com/x/player/wbi/playurl?{query}";

        return GetPlayUrlAsync(
            client,
            url,
            PlayUrlPayloadField.Data,
            nameof(GetVideoPlayUrlAsync),
            cancellationToken);
    }

    /// <summary>
    /// 获取普通视频的视频流（WebPage方式）
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="p"></param>
    /// <returns></returns>
    public static async Task<PlayUrl?> GetVideoPlayUrlWebPageAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string bvid,
        long cid,
        int p,
        CancellationToken cancellationToken = default)
    {
        var url = BuildVideoPlayPageUrl(avid, bvid, p);
        var playUrl = await GetPlayUrlWebPageAsync(client, url, cancellationToken)
            .ConfigureAwait(false);
        if (playUrl == null)
        {
            playUrl = await client.GetVideoPlayUrlAsync(
                keys,
                unixTimeSeconds,
                avid,
                bvid,
                cid,
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
    public static async Task<PlayUrl?> GetBangumiPlayUrlAsync(
        this IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        int quality = 125,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episodeId);
        var baseUrl = $"https://api.bilibili.com/pgc/player/web/v2/playurl?cid={cid}&ep_id={episodeId}&qn={quality}&fourk=1&fnver=0&fnval=4048";
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

        const string referer = "https://www.bilibili.com";
        var response = await BiliApiRequest.RequestJsonAsync<BangumiPlayUrlV2Origin>(
            client,
            url,
            referer,
            nameof(GetBangumiPlayUrlAsync),
            "GetBangumiPlayUrl()",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BangumiPlayUrlV2Contract.SelectPayload(response, nameof(GetBangumiPlayUrlAsync));
    }

    /// <summary>
    /// 获取课程的视频流
    /// </summary>
    /// <param name="avid"></param>
    /// <param name="bvid"></param>
    /// <param name="cid"></param>
    /// <param name="quality"></param>
    /// <returns></returns>
    public static Task<PlayUrl?> GetCheesePlayUrlAsync(
        this IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        int quality = 125,
        CancellationToken cancellationToken = default)
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
            return Task.FromResult<PlayUrl?>(null);
        }

        // 必须有episodeId，否则会返回请求错误
        if (episodeId != 0)
        {
            url += $"&ep_id={episodeId}";
        }

        return GetPlayUrlAsync(
            client,
            url,
            PlayUrlPayloadField.Data,
            nameof(GetCheesePlayUrlAsync),
            cancellationToken);
    }

    /// <summary>
    /// 获取视频流
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static async Task<PlayUrl?> GetPlayUrlAsync(
        IBilibiliApiClient client,
        string url,
        PlayUrlPayloadField payloadField,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        var response = await BiliApiRequest.RequestJsonAsync<PlayUrlOrigin>(
            client,
            url,
            referer,
            operationName,
            "GetPlayUrl()",
            cancellationToken: cancellationToken).ConfigureAwait(false);

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
    private static async Task<PlayUrl?> GetPlayUrlWebPageAsync(
        IBilibiliApiClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        const string referer = "https://www.bilibili.com";
        var response = await BiliApiRequest.RequestTextAsync(
            client,
            url,
            referer,
            nameof(GetPlayUrlWebPageAsync),
            "GetPlayUrlPc()",
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
                nameof(GetPlayUrlWebPageAsync));
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
