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
        var selection = new FinalizedPlaybackSelection(
            input.Metadata.Resolution.Id,
            context.NeedsPendingVideo
                ? ResolveVideoCodecId(input.Metadata.VideoCodecName)
                : null,
            context.NeedsPendingAudio
            && input.RequestedContent.MediaKind == DownloadMediaKind.Dash
                ? input.Metadata.AudioCodec.Id
                : null,
            input.RequestedContent.MediaKind switch
            {
                DownloadMediaKind.Dash => PlayUrlStreamKind.Dash,
                DownloadMediaKind.Durl => PlayUrlStreamKind.Durl,
                _ => null
            },
            context.NeedsPendingVideo);
        PlayUrl? playUrl;
        try
        {
            playUrl = await (input.StreamType switch
            {
                PlayStreamType.Video => WbiRequestExecutor.ExecuteAsync(
                    _wbiKeyProvider,
                    (keys, unixTimeSeconds) => _client.GetVideoFinalizedPlaybackAsync(
                        keys,
                        unixTimeSeconds,
                        media.Avid,
                        media.Bvid,
                        media.Cid,
                        media.Page,
                        selection,
                        preferWebPage: input.VideoSettings.VideoParseType switch
                        {
                            0 => false,
                            1 => true,
                            _ => throw new ArgumentException(
                                "Invalid video parse type. Valid values are: 0 (WebAPI) or 1 (WebPage).")
                        },
                        cancellationToken: cancellationToken),
                    _timeProvider,
                    cancellationToken),
                PlayStreamType.Bangumi => _client.GetBangumiFinalizedPlaybackAsync(
                    media.Avid,
                    media.Bvid,
                    media.Cid,
                    media.EpisodeId,
                    selection,
                    cancellationToken: cancellationToken),
                PlayStreamType.Cheese => _client.GetCheeseFinalizedPlaybackAsync(
                    media.Avid,
                    media.Bvid,
                    media.Cid,
                    media.EpisodeId,
                    selection,
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
