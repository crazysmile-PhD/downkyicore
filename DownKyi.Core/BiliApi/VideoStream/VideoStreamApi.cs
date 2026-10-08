using System.Text.RegularExpressions;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream;

public static partial class VideoStreamApi
{
    private const int BangumiFnval = 4048;

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
        int quality = 125,
        int? preferredAudioQuality = null,
        bool requireVideo = true,
        CancellationToken cancellationToken = default)
    {
        var url = BuildVideoPlayPageUrl(avid, bvid, p);
        PlayUrl? webpage;
        try
        {
            webpage = await GetPlayUrlWebPageAsync(client, url, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRetryableApiFailure(exception, cancellationToken))
        {
            webpage = null;
        }

        var webAvailability = webpage == null
            ? null
            : PlayUrlAvailability.From(webpage);
        if (webAvailability != null
            && webAvailability.HasPlayableMedia
            && HasPreferredPlayback(
                webAvailability,
                quality,
                preferredAudioQuality,
                requireVideo))
        {
            return webpage;
        }

        try
        {
            var api = await client.GetVideoPlayUrlAsync(
                keys,
                unixTimeSeconds,
                avid,
                bvid,
                cid,
                quality,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (api == null)
            {
                return webpage;
            }

            if (webpage == null || webAvailability?.HasPlayableMedia != true)
            {
                return api;
            }

            var merged = BangumiPlaybackResolver.MergePlayback(webpage, api, quality);
            merged.Diagnostics = new PlayUrlDiagnostics(
                quality,
                null,
                null,
                PlayUrlResolutionSource.Mixed,
                "api-supplement-selected");
            return merged;
        }
        catch (Exception exception) when (
            webAvailability?.HasPlayableMedia == true
            && IsRetryableApiFailure(exception, cancellationToken))
        {
            webpage!.Diagnostics = new PlayUrlDiagnostics(
                quality,
                null,
                null,
                PlayUrlResolutionSource.WebPage,
                "webpage-after-api-failure",
                DescribeApiFailure(exception));
            return webpage;
        }
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
    public static Task<PlayUrl?> GetBangumiPlaybackDiscoveryAsync(
        this IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        int? preferredVideoQuality = null,
        int? preferredAudioQuality = null,
        CancellationToken cancellationToken = default)
    {
        return GetBangumiPlaybackCoreAsync(
            client,
            avid,
            bvid,
            cid,
            episodeId,
            preferredVideoQuality ?? PlaybackQualityCatalog.MaximumProbeQuality,
            discoverAvailability: true,
            preferredVideoQuality,
            preferredAudioQuality,
            videoCodecId: null,
            audioId: null,
            streamKind: null,
            requireVideo: true,
            cancellationToken);
    }

    public static Task<PlayUrl?> GetBangumiPlayUrlAsync(
        this IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        int quality = PlaybackQualityCatalog.MaximumProbeQuality,
        int? videoCodecId = null,
        int? audioId = null,
        PlayUrlStreamKind? streamKind = null,
        bool requireVideo = true,
        CancellationToken cancellationToken = default)
    {
        return GetBangumiPlaybackCoreAsync(
            client,
            avid,
            bvid,
            cid,
            episodeId,
            quality,
            discoverAvailability: false,
            preferredVideoQuality: null,
            preferredAudioQuality: null,
            videoCodecId,
            audioId,
            streamKind,
            requireVideo,
            cancellationToken);
    }

    private static async Task<PlayUrl?> GetBangumiPlaybackCoreAsync(
        IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        int quality,
        bool discoverAvailability,
        int? preferredVideoQuality,
        int? preferredAudioQuality,
        int? videoCodecId,
        int? audioId,
        PlayUrlStreamKind? streamKind,
        bool requireVideo,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episodeId);
        if (!discoverAvailability && !requireVideo && audioId == null)
        {
            throw new PlaybackSelectionUnavailableException(
                quality,
                videoCodecId,
                audioId,
                streamKind);
        }

        var referer = BuildBangumiPlayPageUrl(episodeId);
        var apiFallbackReason = "embedded-playback-unavailable";
        PlayUrl? partialEmbeddedPlayUrl = null;
        string? partialEmbeddedPlayDetail = null;
        PlayUrl CompletePartialEmbeddedPlayback(string? apiFailure = null) => CompleteBangumiPlayback(
            partialEmbeddedPlayUrl!,
            discoverAvailability,
            quality,
            videoCodecId,
            audioId,
            streamKind,
            requireVideo,
            partialEmbeddedPlayDetail,
            PlayUrlResolutionSource.WebPage,
            apiFailure == null
                ? "embedded-availability-after-api-unusable-response"
                : "embedded-availability-after-api-failure",
            apiFailure);
        try
        {
            var webpage = await BiliApiRequest.RequestTextAsync(
                client,
                referer,
                referer,
                nameof(GetBangumiPlayUrlAsync),
                "GetBangumiPlayPage()",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!BangumiPlaybackResolver.TryParseEmbeddedPayload(
                    webpage,
                    nameof(GetBangumiPlayUrlAsync),
                    out var embeddedPlayUrl,
                    out var embeddedPlayDetail)
                || embeddedPlayUrl == null)
            {
                apiFallbackReason = "embedded-playback-unavailable";
            }
            else
            {
                var embeddedAvailability = BangumiPlaybackResolver.DiscoverAvailability(
                    embeddedPlayUrl);
                BangumiPlaybackResolver.MarkSource(
                    embeddedPlayUrl,
                    PlayUrlResolutionSource.WebPage);
                var hasDiscoverableEmbeddedPlayback = embeddedAvailability.HasPlayableMedia;
                var hasUsableEmbeddedPlayback = HasRequiredMedia(
                    embeddedAvailability,
                    requireVideo);
                var hasSufficientEmbeddedPlayback = !discoverAvailability
                    || HasPreferredPlayback(
                        embeddedAvailability,
                        preferredVideoQuality,
                        preferredAudioQuality);
                if (discoverAvailability && hasUsableEmbeddedPlayback
                    && hasSufficientEmbeddedPlayback)
                {
                    return CompleteBangumiPlayback(
                        embeddedPlayUrl,
                        discoverAvailability,
                        quality,
                        videoCodecId,
                        audioId,
                        streamKind,
                        requireVideo,
                        embeddedPlayDetail,
                        PlayUrlResolutionSource.WebPage,
                        discoverAvailability
                            ? "embedded-availability-selected"
                            : "embedded-playback-selected");
                }

                if (hasDiscoverableEmbeddedPlayback)
                {
                    partialEmbeddedPlayUrl = embeddedPlayUrl;
                    partialEmbeddedPlayDetail = embeddedPlayDetail;
                }

                if (!discoverAvailability
                    && BangumiPlaybackResolver.TrySelectDownloadPlayback(
                        embeddedPlayUrl,
                        supplement: null,
                        quality,
                        videoCodecId,
                        audioId,
                        streamKind,
                        requireVideo,
                        out var selectedEmbeddedPlayUrl))
                {
                    return AttachBangumiDiagnostics(
                        selectedEmbeddedPlayUrl!,
                        quality,
                        embeddedPlayDetail,
                        PlayUrlResolutionSource.WebPage,
                        "embedded-playback-selected");
                }

                apiFallbackReason = ClassifyEmbeddedFallbackReason(
                    hasUsableEmbeddedPlayback,
                    hasDiscoverableEmbeddedPlayback);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            or BilibiliApiResponseException or JsonException)
        {
            apiFallbackReason = $"web-request-failed:{exception.GetType().Name}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            apiFallbackReason = "web-request-timeout";
        }

        var url = BuildBangumiApiUrl(avid, bvid, cid, episodeId, quality);
        if (url == null)
        {
            return null;
        }

        try
        {
            var (response, playUrl) = await QueryBangumiApiAsync(
                client,
                url,
                referer,
                cancellationToken).ConfigureAwait(false);

            BangumiPlaybackResolver.MarkSource(playUrl, PlayUrlResolutionSource.Api);
            if (ShouldUsePartialEmbedded(partialEmbeddedPlayUrl, playUrl))
            {
                return CompletePartialEmbeddedPlayback();
            }

            if (partialEmbeddedPlayUrl != null)
            {
                playUrl = BangumiPlaybackResolver.MergePlayback(
                    partialEmbeddedPlayUrl,
                    playUrl,
                    discoverAvailability ? null : quality);
            }

            return CompleteBangumiPlayback(
                playUrl,
                discoverAvailability,
                quality,
                videoCodecId,
                audioId,
                streamKind,
                requireVideo,
                response.Result?.PlayCheck?.PlayDetail,
                partialEmbeddedPlayUrl == null
                    ? PlayUrlResolutionSource.Api
                    : PlayUrlResolutionSource.Mixed,
                $"api-fallback-selected:{apiFallbackReason}");
        }
        catch (Exception exception) when (
            partialEmbeddedPlayUrl != null
            && IsRetryableApiFailure(exception, cancellationToken))
        {
            return CompletePartialEmbeddedPlayback(DescribeApiFailure(exception));
        }
        catch (Exception exception) when (
            discoverAvailability && IsRetryableApiFailure(exception, cancellationToken))
        {
            return CreateFailedDiscovery(quality, exception);
        }
    }

    private static PlayUrl CreateFailedDiscovery(int quality, Exception exception) =>
        PlayUrl.FailedDiscovery(quality, DescribeApiFailure(exception), BangumiFnval);

    private static bool HasRequiredMedia(PlayUrlAvailability availability, bool requireVideo) =>
        requireVideo ? availability.Video.Count > 0 : availability.Audio.Count > 0;

    private static string ClassifyEmbeddedFallbackReason(
        bool hasRequiredMedia,
        bool hasAnyMedia) =>
        hasRequiredMedia
            ? "embedded-playback-selection-unavailable"
            : hasAnyMedia
                ? "embedded-playback-without-required-media"
                : "embedded-playback-without-usable-address";

    private static bool ShouldUsePartialEmbedded(PlayUrl? embedded, PlayUrl api) =>
        embedded != null
        && !BangumiPlaybackResolver.DiscoverAvailability(api).HasPlayableMedia;

    private static string? BuildBangumiApiUrl(
        long avid,
        string bvid,
        long cid,
        long episodeId,
        int quality)
    {
        var baseUrl = $"https://api.bilibili.com/pgc/player/web/v2/playurl?cid={cid}&ep_id={episodeId}&qn={quality}&fourk=1&fnver=0&fnval={BangumiFnval}";
        if (bvid != null)
        {
            return $"{baseUrl}&bvid={bvid}";
        }

        return avid > -1 ? $"{baseUrl}&aid={avid}" : null;
    }

    private static Task<(BangumiPlayUrlV2Origin Response, PlayUrl Playback)>
        QueryBangumiApiAsync(
            IBilibiliApiClient client,
            string url,
            string referer,
            CancellationToken cancellationToken) =>
        QueryPlaybackWithRetryAsync(
            () => BiliApiRequest.RequestJsonAsync<BangumiPlayUrlV2Origin>(
                client,
                url,
                referer,
                nameof(GetBangumiPlayUrlAsync),
                "GetBangumiPlayUrl()",
                cancellationToken: cancellationToken),
            response => RequireUsablePlayback(
                BangumiPlayUrlV2Contract.SelectPayload(
                    response,
                    nameof(GetBangumiPlayUrlAsync)),
                nameof(GetBangumiPlayUrlAsync)),
            cancellationToken);

    private static PlayUrl RequireUsablePlayback(PlayUrl playback, string operationName)
    {
        if (!PlayUrlAvailability.From(playback).HasPlayableMedia)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned playback without a usable media address.");
        }

        return playback;
    }

    private static async Task<(TResponse Response, PlayUrl Playback)>
        QueryPlaybackWithRetryAsync<TResponse>(
            Func<Task<TResponse>> request,
            Func<TResponse, PlayUrl> select,
            CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await request().ConfigureAwait(false);
                return (response, select(response));
            }
            catch (Exception exception) when (
                attempt == 0 && IsRetryableApiFailure(exception, cancellationToken))
            {
                continue;
            }
        }
    }

    private static bool IsRetryableApiFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is HttpRequestException or BilibiliApiResponseException or JsonException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static string DescribeApiFailure(Exception exception) =>
        exception is BilibiliApiResponseException apiResponse
            ? $"{nameof(BilibiliApiResponseException)}:{apiResponse.Code}"
            : exception.GetType().Name;

    private static bool HasPreferredPlayback(
        PlayUrlAvailability availability,
        int? preferredVideoQuality,
        int? preferredAudioQuality,
        bool requireVideo = true)
    {
        var videoIsSufficient = !requireVideo
            || preferredVideoQuality == null
            || availability.Video.Any(video => video.Quality >= preferredVideoQuality);
        if (!videoIsSufficient)
        {
            return false;
        }

        if (preferredAudioQuality == null)
        {
            return true;
        }

        var catalog = PlaybackQualityCatalog.GetAudioQualities();
        var requested = preferredAudioQuality >= 31000
            ? preferredAudioQuality - 1000
            : preferredAudioQuality;
        var requestedRank = Array.FindIndex(catalog.ToArray(), quality => quality.Id == requested);
        return requestedRank >= 0 && availability.Audio.Any(id =>
            Array.FindIndex(catalog.ToArray(), quality => quality.Id == id) >= requestedRank);
    }

    private static PlayUrl CompleteBangumiPlayback(
        PlayUrl playUrl,
        bool discoverAvailability,
        int quality,
        int? videoCodecId,
        int? audioId,
        PlayUrlStreamKind? streamKind,
        bool requireVideo,
        string? playDetail,
        PlayUrlResolutionSource source,
        string outcome,
        string? apiFailure = null)
    {
        if (discoverAvailability)
        {
            playUrl.Availability ??= BangumiPlaybackResolver.DiscoverAvailability(playUrl);
            return AttachBangumiDiagnostics(
                playUrl,
                quality,
                playDetail,
                source,
                outcome,
                apiFailure);
        }

        if (!BangumiPlaybackResolver.TrySelectDownloadPlayback(
                playUrl,
                supplement: null,
                quality,
                videoCodecId,
                audioId,
                streamKind,
                requireVideo,
                out var selected))
        {
            throw new PlaybackSelectionUnavailableException(
                quality,
                videoCodecId,
                audioId,
                streamKind);
        }

        return AttachBangumiDiagnostics(
            selected!,
            quality,
            playDetail,
            source,
            outcome,
            apiFailure);
    }

    internal static string BuildBangumiPlayPageUrl(long episodeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episodeId);
        return $"https://www.bilibili.com/bangumi/play/ep{episodeId}";
    }

    private static PlayUrl AttachBangumiDiagnostics(
        PlayUrl playUrl,
        int requestedQuality,
        string? playDetail,
        PlayUrlResolutionSource source,
        string outcome,
        string? apiFailure = null)
    {
        var mediaSources = playUrl.Dash.Video
            .Concat(playUrl.Dash.Audio)
            .Concat(playUrl.Dash.Dolby?.Audio ?? [])
            .Concat(playUrl.Dash.Flac?.Audio is { } flac ? [flac] : [])
            .Select(stream => stream.Source)
            .Concat(playUrl.Durl.Select(segment => segment.Source))
            .Where(item => item != null)
            .Distinct()
            .ToArray();
        if (mediaSources.Length == 1)
        {
            source = mediaSources[0]!.Value;
        }
        else if (mediaSources.Length > 1)
        {
            source = PlayUrlResolutionSource.Mixed;
        }

        playUrl.Diagnostics = new PlayUrlDiagnostics(
            requestedQuality,
            source == PlayUrlResolutionSource.Api ? BangumiFnval : null,
            playDetail,
            source,
            outcome,
            apiFailure);
        return playUrl;
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
        var (_, playback) = await QueryPlaybackWithRetryAsync(
            () => BiliApiRequest.RequestJsonAsync<PlayUrlOrigin>(
                client,
                url,
                referer,
                operationName,
                "GetPlayUrl()",
                cancellationToken: cancellationToken),
            response => RequireUsablePlayback(
                SelectPlayUrlPayload(response, payloadField, operationName),
                operationName),
            cancellationToken).ConfigureAwait(false);
        BangumiPlaybackResolver.MarkSource(playback, PlayUrlResolutionSource.Api);
        return playback;
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

        if (!payload.HasMediaEntries)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned an empty '{fieldName}' playback payload.");
        }

        if (payload.IsPreview == true)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned preview-only playback content.");
        }

        return payload;
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

            var playback = SelectPlayUrlPayload(
                playUrl,
                PlayUrlPayloadField.Data,
                nameof(GetPlayUrlWebPageAsync));
            BangumiPlaybackResolver.MarkSource(playback, PlayUrlResolutionSource.WebPage);
            return playback;
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
