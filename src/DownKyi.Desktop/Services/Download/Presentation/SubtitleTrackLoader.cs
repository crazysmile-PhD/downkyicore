using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal interface ISubtitleTrackLoader
{
    Task<DownloadSettingsDialog.SubtitleTrackDiscovery> LoadAsync(
        DownloadSettingsDialog.SubtitlePage page, CancellationToken cancellationToken);
}

internal sealed class SubtitleTrackLoader(
    IWbiKeyProvider wbiKeyProvider,
    IBilibiliApiClient client,
    ILogger<SubtitleTrackLoader> logger) : ISubtitleTrackLoader
{
    public async Task<DownloadSettingsDialog.SubtitleTrackDiscovery> LoadAsync(
        DownloadSettingsDialog.SubtitlePage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var player = await WbiRequestExecutor.ExecuteAsync(
                wbiKeyProvider,
                (keys, unixTimeSeconds) => client.PlayerV2Async(
                    keys, unixTimeSeconds, page.Avid, page.Bvid, page.Cid, cancellationToken),
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var tracks = player?.Subtitle?.Subtitles?.Select(track =>
                new DownloadSettingsDialog.SubtitleTrack(
                    track.Id, track.Lan, track.LanDoc, track.Type, track.SubtitleAddress)).ToArray() ?? [];
            return new DownloadSettingsDialog.SubtitleTrackDiscovery(
                tracks.Length > 0
                    ? DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Available
                    : DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NoResource,
                tracks);
        }
        catch (Exception exception) when (exception is HttpRequestException or BilibiliApiResponseException
                                          || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarningMessage("Subtitle tracks could not be loaded; subtitle preferences remain available.", exception);
            return new DownloadSettingsDialog.SubtitleTrackDiscovery(
                DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Failed, []);
        }
    }
}
