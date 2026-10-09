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
        CancellationToken cancellationToken = default)
    {
        var url = BuildVideoPlayPageUrl(avid, bvid, p);
        var playUrl = await GetPlayUrlWebPageAsync(client, url, cancellationToken)
            .ConfigureAwait(false);
        if (playUrl != null)
        {
            if (BangumiPlaybackResolver.HasCompleteDiscovery(playUrl, quality))
            {
                return AttachVideoDiagnostics(playUrl, quality, "webpage-selected");
            }
        }

        var api = await client.GetVideoPlayUrlAsync(
            keys, unixTimeSeconds, avid, bvid, cid, quality,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var selected = playUrl != null && api != null
            ? BangumiPlaybackResolver.CombineDiscovery(playUrl, api, quality)
            : api;
        return selected == null
            ? null : AttachVideoDiagnostics(selected, quality, "api-selected");
    }

    public static async Task<PlayUrl?> GetVideoFinalizedPlaybackAsync(
        this IBilibiliApiClient client,
        WbiKeys keys,
        long unixTimeSeconds,
        long avid,
        string bvid,
        long cid,
        int page,
        FinalizedPlaybackSelection selection,
        bool preferWebPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IsActionable)
        {
            throw SelectionUnavailable(selection);
        }

        if (preferWebPage)
        {
            var webpage = await GetPlayUrlWebPageAsync(
                client,
                BuildVideoPlayPageUrl(avid, bvid, page),
                cancellationToken).ConfigureAwait(false);
            if (webpage != null && BangumiPlaybackResolver.TrySelectDownloadPlayback(
                    webpage,
                    supplement: null,
                    selection,
                    out var selectedWebpage))
            {
                return AttachVideoDiagnostics(
                    selectedWebpage!, selection.VideoQuality, "finalized-selected");
            }

            var apiSupplement = await client.GetVideoPlayUrlAsync(
                keys, unixTimeSeconds, avid, bvid, cid, selection.ProbeQuality,
                cancellationToken).ConfigureAwait(false);
            if (webpage != null && apiSupplement != null
                && BangumiPlaybackResolver.TrySelectDownloadPlayback(
                    webpage, apiSupplement, selection, out var combined))
            {
                return AttachVideoDiagnostics(
                    combined!, selection.VideoQuality, "finalized-selected");
            }

            var selectedApiSupplement = SelectFinalizedPlayback(apiSupplement, selection);
            return selectedApiSupplement == null ? null : AttachVideoDiagnostics(
                selectedApiSupplement, selection.VideoQuality, "finalized-selected");
        }

        var api = await client.GetVideoPlayUrlAsync(
            keys,
            unixTimeSeconds,
            avid,
            bvid,
            cid,
            selection.ProbeQuality,
            cancellationToken).ConfigureAwait(false);
        var selectedApi = SelectFinalizedPlayback(api, selection);
        return selectedApi == null ? null : AttachVideoDiagnostics(
            selectedApi, selection.VideoQuality, "finalized-selected");
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
        CancellationToken cancellationToken = default)
    {
        return GetBangumiPlaybackCoreAsync(
            client,
            avid,
            bvid,
            cid,
            episodeId,
            PlaybackQualityCatalog.MaximumProbeQuality,
            discoverAvailability: true,
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
            videoCodecId,
            audioId,
            streamKind,
            requireVideo,
            cancellationToken);
    }

    public static Task<PlayUrl?> GetBangumiFinalizedPlaybackAsync(
        this IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        FinalizedPlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IsActionable)
        {
            throw SelectionUnavailable(selection);
        }

        return GetBangumiPlaybackCoreAsync(
            client,
            avid,
            bvid,
            cid,
            episodeId,
            selection.ProbeQuality,
            discoverAvailability: false,
            selection.VideoCodecId,
            selection.AudioId,
            selection.StreamKind,
            selection.RequireVideo,
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
        PlayUrl? embeddedPlayUrl = null;
        string? embeddedPlayDetail = null;
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
                    out embeddedPlayUrl,
                    out embeddedPlayDetail)
                || embeddedPlayUrl == null)
            {
                apiFallbackReason = "embedded-playback-unavailable";
            }
            else
            {
                var embeddedAvailability = BangumiPlaybackResolver.DiscoverAvailability(
                    embeddedPlayUrl);
                PlaybackSourceProvenance.Mark(
                    embeddedPlayUrl, PlayUrlResolutionSource.WebPage);
                var hasUsableEmbeddedPlayback = requireVideo
                    ? embeddedAvailability.Video.Count > 0
                    : embeddedAvailability.Audio.Count > 0;
                if (discoverAvailability
                    && BangumiPlaybackResolver.HasCompleteDiscovery(embeddedPlayUrl))
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

                apiFallbackReason = hasUsableEmbeddedPlayback
                    ? "embedded-playback-selection-unavailable"
                    : "embedded-playback-without-usable-address";
            }
        }
        catch (HttpRequestException exception)
        {
            apiFallbackReason = $"web-request-failed:{exception.GetType().Name}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            apiFallbackReason = "web-request-timeout";
        }

        var baseUrl = $"https://api.bilibili.com/pgc/player/web/v2/playurl?cid={cid}&ep_id={episodeId}&qn={quality}&fourk=1&fnver=0&fnval={BangumiFnval}";
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

        var (response, playUrl) = await RequestPlaybackWithApiCodeRetryAsync(
            () => BiliApiRequest.RequestJsonAsync<BangumiPlayUrlV2Origin>(
                client,
                url,
                referer,
                nameof(GetBangumiPlayUrlAsync),
                "GetBangumiPlayUrl()",
                cancellationToken: cancellationToken),
            response => BangumiPlayUrlV2Contract.SelectPayload(
                response,
                nameof(GetBangumiPlayUrlAsync)),
            cancellationToken).ConfigureAwait(false);
        PlaybackSourceProvenance.Mark(playUrl, PlayUrlResolutionSource.Api);
        return CompleteBangumiWithApi(
            embeddedPlayUrl,
            playUrl,
            discoverAvailability,
            quality,
            videoCodecId,
            audioId,
            streamKind,
            requireVideo,
            embeddedPlayDetail ?? response.Result?.PlayCheck?.PlayDetail,
            apiFallbackReason);
    }

    private static PlayUrl CompleteBangumiWithApi(
        PlayUrl? embeddedPlayUrl,
        PlayUrl apiPlayUrl,
        bool discoverAvailability,
        int quality,
        int? videoCodecId,
        int? audioId,
        PlayUrlStreamKind? streamKind,
        bool requireVideo,
        string? playDetail,
        string apiFallbackReason)
    {
        if (embeddedPlayUrl != null)
        {
            if (discoverAvailability)
            {
                return CompleteBangumiPlayback(
                    BangumiPlaybackResolver.CombineDiscovery(embeddedPlayUrl, apiPlayUrl),
                    discoverAvailability,
                    quality,
                    videoCodecId,
                    audioId,
                    streamKind,
                    requireVideo,
                    playDetail,
                    PlayUrlResolutionSource.Api,
                    $"api-fallback-selected:{apiFallbackReason}");
            }

            if (BangumiPlaybackResolver.TrySelectDownloadPlayback(
                    embeddedPlayUrl, apiPlayUrl, quality, videoCodecId, audioId,
                    streamKind, requireVideo, out var combined))
            {
                return AttachBangumiDiagnostics(
                    combined!, quality,
                    playDetail,
                    PlayUrlResolutionSource.Api,
                    $"api-fallback-selected:{apiFallbackReason}");
            }
        }

        return CompleteBangumiPlayback(
            apiPlayUrl,
            discoverAvailability,
            quality,
            videoCodecId,
            audioId,
            streamKind,
            requireVideo,
            playDetail,
            PlayUrlResolutionSource.Api,
            $"api-fallback-selected:{apiFallbackReason}");
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
        string outcome)
    {
        if (discoverAvailability)
        {
            playUrl.Availability = BangumiPlaybackResolver.DiscoverAvailability(playUrl);
            return AttachBangumiDiagnostics(
                playUrl,
                quality,
                playDetail,
                source,
                outcome);
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
            outcome);
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
        string outcome)
    {
        var retainedSource = PlaybackSourceProvenance.FromRetainedStreams(playUrl, source);
        playUrl.Diagnostics = new PlayUrlDiagnostics(
            requestedQuality,
            retainedSource is PlayUrlResolutionSource.Api or PlayUrlResolutionSource.Mixed
                ? BangumiFnval : null,
            playDetail,
            retainedSource,
            outcome);
        return playUrl;
    }

    private static PlayUrl AttachVideoDiagnostics(
        PlayUrl playUrl,
        int requestedQuality,
        string outcome)
    {
        var source = PlaybackSourceProvenance.FromRetainedStreams(
            playUrl, PlayUrlResolutionSource.Api);
        playUrl.Diagnostics = new PlayUrlDiagnostics(
            requestedQuality,
            source is PlayUrlResolutionSource.Api or PlayUrlResolutionSource.Mixed
                ? BangumiFnval : null,
            null,
            source,
            outcome);
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

    public static async Task<PlayUrl?> GetCheeseFinalizedPlaybackAsync(
        this IBilibiliApiClient client,
        long avid,
        string bvid,
        long cid,
        long episodeId,
        FinalizedPlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IsActionable)
        {
            throw SelectionUnavailable(selection);
        }

        var playback = await client.GetCheesePlayUrlAsync(
            avid,
            bvid,
            cid,
            episodeId,
            selection.ProbeQuality,
            cancellationToken).ConfigureAwait(false);
        return SelectFinalizedPlayback(playback, selection);
    }

    private static PlayUrl? SelectFinalizedPlayback(
        PlayUrl? playback,
        FinalizedPlaybackSelection selection)
    {
        if (playback == null)
        {
            return null;
        }

        return BangumiPlaybackResolver.TrySelectDownloadPlayback(
            playback,
            supplement: null,
            selection,
            out var selected)
            ? selected
            : throw SelectionUnavailable(selection);
    }

    private static PlaybackSelectionUnavailableException SelectionUnavailable(
        FinalizedPlaybackSelection selection) =>
        new(
            selection.VideoQuality,
            selection.VideoCodecId,
            selection.AudioId,
            selection.StreamKind);

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
        var (_, playback) = await RequestPlaybackWithApiCodeRetryAsync(
            () => BiliApiRequest.RequestJsonAsync<PlayUrlOrigin>(
                client,
                url,
                referer,
                operationName,
                "GetPlayUrl()",
                cancellationToken: cancellationToken),
            response => SelectPlayUrlPayload(response, payloadField, operationName),
            cancellationToken).ConfigureAwait(false);
        return PlaybackSourceProvenance.Mark(playback, PlayUrlResolutionSource.Api);
    }

    private static async Task<(TResponse Response, PlayUrl Playback)> RequestPlaybackWithApiCodeRetryAsync<TResponse>(
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
            catch (BilibiliApiResponseException exception) when (
                attempt == 0
                && !cancellationToken.IsCancellationRequested
                && exception.Code is not null and not -403)
            {
                // Transport retries and WBI -403 refresh belong to their existing owners.
                continue;
            }
        }
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

        if (payload.IsPreview == true)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned preview-only playback content.");
        }

        return PlayUrlAvailability.From(payload).HasPlayableMedia
            ? payload
            : throw new PlaybackResourceUnavailableException(operationName);
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

            return PlaybackSourceProvenance.Mark(SelectPlayUrlPayload(
                playUrl,
                PlayUrlPayloadField.Data,
                nameof(GetPlayUrlWebPageAsync)), PlayUrlResolutionSource.WebPage);
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
        catch (PlaybackResourceUnavailableException)
        {
            return null;
        }
    }
}
