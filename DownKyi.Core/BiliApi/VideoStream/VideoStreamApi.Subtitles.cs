using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream;

public static partial class VideoStreamApi
{
    public static async Task<PlayerV2?> PlayerV2Async(
        this IBilibiliApiClient client,
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
        var playUrl = await BiliApiRequest.RequestJsonAsync<PlayerV2Origin>(
            client,
            url,
            referer,
            nameof(PlayerV2Async),
            "PlayerV2()",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(playUrl.Data);
    }

    public static Task<IReadOnlyList<SubRipText>> GetSubtitleAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string? bvid,
        long cid,
        CancellationToken cancellationToken = default)
    {
        return client.GetSubtitleAsync(
            keys,
            unixTimeSeconds,
            avid,
            bvid,
            cid,
            reportParseFailure: null,
            cancellationToken);
    }

    public static async Task<IReadOnlyList<SubRipText>> GetSubtitleAsync(
        this IBilibiliApiClient client,
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
        var player = await client.PlayerV2Async(
            keys,
            unixTimeSeconds,
            avid,
            bvid,
            cid,
            cancellationToken).ConfigureAwait(false);
        if (player?.Subtitle?.Subtitles == null || player.Subtitle.Subtitles.Count == 0)
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

            var response = await BiliApiRequest.RequestTextAsync(
                client,
                subtitleUrl,
                referer,
                nameof(GetSubtitleAsync),
                "GetSubtitle()",
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
            catch (JsonException exception)
            {
                reportParseFailure?.Invoke(exception);
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

        var normalizedUrl = subtitleUrl.Trim();
        if (normalizedUrl.StartsWith("//", StringComparison.Ordinal))
        {
            normalizedUrl = $"https:{normalizedUrl}";
        }

        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var absoluteUri))
        {
            return IsSupportedSubtitleUri(absoluteUri)
                ? absoluteUri.ToString()
                : null;
        }

        var httpsUrl = $"https://{normalizedUrl.TrimStart('/')}";
        return Uri.TryCreate(httpsUrl, UriKind.Absolute, out var httpsUri)
               && IsSupportedSubtitleUri(httpsUri)
            ? httpsUri.ToString()
            : null;
    }

    private static bool IsSupportedSubtitleUri(Uri uri)
    {
        return !string.IsNullOrEmpty(uri.Host)
               && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
