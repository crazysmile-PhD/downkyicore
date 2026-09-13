using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;

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

    public Task<PlayUrl?> ResolveAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.Input;
        var media = input.Metadata.Media;
        return input.StreamType switch
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
                        cancellationToken: cancellationToken),
                    1 => _client.GetVideoPlayUrlWebPageAsync(
                        keys,
                        unixTimeSeconds,
                        media.Avid,
                        media.Bvid,
                        media.Cid,
                        media.Page,
                        cancellationToken),
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
                cancellationToken: cancellationToken),
            PlayStreamType.Cheese => _client.GetCheesePlayUrlAsync(
                media.Avid,
                media.Bvid,
                media.Cid,
                media.EpisodeId,
                cancellationToken: cancellationToken),
            _ => Task.FromResult<PlayUrl?>(null)
        };
    }
}
