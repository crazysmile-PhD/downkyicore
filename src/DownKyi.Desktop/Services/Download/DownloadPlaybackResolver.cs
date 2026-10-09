using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

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
        PlayUrl? playUrl;
        try
        {
            playUrl = await (input.StreamType switch
            {
                PlayStreamType.Video => WbiRequestExecutor.ExecuteAsync(
                    _wbiKeyProvider,
                    (keys, unixTimeSeconds) => input.VideoSettings.VideoParseType switch
                    {
                        0 => _client.GetVideoPlayUrlAsync(
                            keys,
                            unixTimeSeconds,
                            media.Avid,
                            media.Bvid,
                            media.Cid,
                            quality: input.Metadata.Resolution.Id,
                            cancellationToken: cancellationToken),
                        1 => _client.GetVideoPlayUrlWebPageAsync(
                            keys,
                            unixTimeSeconds,
                            media.Avid,
                            media.Bvid,
                            media.Cid,
                            media.Page,
                            quality: input.Metadata.Resolution.Id,
                            cancellationToken: cancellationToken),
                        _ => throw new ArgumentException(
                            "Invalid video parse type. Valid values are: 0 (WebAPI) or 1 (WebPage).")
                    },
                    _timeProvider,
                    cancellationToken),
                PlayStreamType.Bangumi => _client.GetBangumiPlayUrlAsync(
                    media.Avid,
                    media.Bvid,
                    media.Cid,
                    media.EpisodeId,
                    quality: input.Metadata.Resolution.Id,
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
                    quality: input.Metadata.Resolution.Id,
                    cancellationToken: cancellationToken),
                _ => Task.FromResult<PlayUrl?>(null)
            }).ConfigureAwait(false);
        }
        catch (PlaybackUnavailableException exception)
        {
            return OperationResult.Failure<PlayUrl>(
                DownloadMediaContract.SelectionUnavailable(exception.Message));
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
