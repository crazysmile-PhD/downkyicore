using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Newtonsoft.Json;

namespace DownKyi.Services.Download;

internal sealed class DownloadPlaybackResolver
{
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IBilibiliApiClient _client;

    public DownloadPlaybackResolver(
        IWbiKeyProvider wbiKeyProvider,
        TimeProvider timeProvider,
        IBilibiliApiClient client)
    {
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<OperationResult<PlayUrl>> ResolveAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.Input;
        var media = input.Metadata.Media;
        var playbackQuality = !context.NeedsVideo && input.Metadata.Resolution.Id <= 0
            ? PlaybackQualityCatalog.MaximumProbeQuality
            : input.Metadata.Resolution.Id;
        PlayUrl? playUrl;
        try
        {
            playUrl = await (input.StreamType switch
            {
                PlayStreamType.Video => WbiRequestExecutor.ExecuteAsync(
                    _wbiKeyProvider,
                    (keys, unixTimeSeconds) => _client.GetVideoPlayUrlWebPageAsync(
                        keys,
                        unixTimeSeconds,
                        media.Avid,
                        media.Bvid,
                        media.Cid,
                        media.Page,
                        quality: playbackQuality,
                        preferredAudioQuality: context.NeedsPendingAudio
                            && input.RequestedContent.MediaKind == DownloadMediaKind.Dash
                                ? input.Metadata.AudioCodec.Id
                                : null,
                        requireVideo: context.NeedsPendingVideo,
                        cancellationToken: cancellationToken),
                    _timeProvider,
                    cancellationToken),
                PlayStreamType.Bangumi => _client.GetBangumiPlayUrlAsync(
                    media.Avid,
                    media.Bvid,
                    media.Cid,
                    media.EpisodeId,
                    quality: playbackQuality,
                    videoCodecId: context.NeedsPendingVideo
                        ? ResolveVideoCodecId(input.Metadata.VideoCodecName)
                        : null,
                    audioId: context.NeedsPendingAudio
                        && input.RequestedContent.MediaKind == DownloadMediaKind.Dash
                            ? input.Metadata.AudioCodec.Id
                            : null,
                    requireVideo: context.NeedsPendingVideo,
                    streamKind: input.RequestedContent.MediaKind switch
                    {
                        DownloadMediaKind.Dash => PlayUrlStreamKind.Dash,
                        DownloadMediaKind.Durl => PlayUrlStreamKind.Durl,
                        _ => null
                    },
                    cancellationToken: cancellationToken),
                PlayStreamType.Cheese => _client.GetCheesePlayUrlAsync(
                    media.Avid,
                    media.Bvid,
                    media.Cid,
                    media.EpisodeId,
                    quality: playbackQuality,
                    cancellationToken: cancellationToken),
                _ => Task.FromResult<PlayUrl?>(null)
            }).ConfigureAwait(false);
        }
        catch (PlaybackSelectionUnavailableException exception)
        {
            return OperationResult.Failure<PlayUrl>(
                DownloadMediaContract.SelectionUnavailable(exception.Message));
        }
        catch (Exception exception) when (exception is HttpRequestException
            or BilibiliApiResponseException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var reason = exception is BilibiliApiResponseException apiFailure
                ? $"API response code {apiFailure.Code}"
                : exception.GetType().Name;
            return OperationResult.Failure<PlayUrl>(new OperationError(
                "download.playback.api-failure",
                $"Playback query failed after retry: {reason}.",
                OperationErrorKind.Network));
        }

        if (playUrl == null)
        {
            return OperationResult.Failure<PlayUrl>(OperationError.Unexpected(
                "download.resolve.playback",
                "Playback data could not be resolved."));
        }

        var contractFailure = DownloadMediaContract.Validate(context, playUrl);
        return contractFailure == null
            ? OperationResult.Success(playUrl)
            : OperationResult.Failure<PlayUrl>(contractFailure);
    }

    private static int? ResolveVideoCodecId(string codecName)
    {
        return PlaybackQualityCatalog.GetCodecIds()
            .FirstOrDefault(codec => string.Equals(
                codec.Name,
                codecName,
                StringComparison.Ordinal))
            ?.Id;
    }
}
