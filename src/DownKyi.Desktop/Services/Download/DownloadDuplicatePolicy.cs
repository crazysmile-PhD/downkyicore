using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Presentation;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadDuplicatePolicy
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly IUserNotificationService _notificationService;
    private readonly IAppDialogService _dialogService;

    public DownloadDuplicatePolicy(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IUserNotificationService notificationService,
        IAppDialogService dialogService)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public async Task<bool> ShouldSkipAsync(
        VideoPage page,
        VideoQuality videoQuality,
        RepeatDownloadStrategy strategy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(videoQuality);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in _downloadLists.Downloading)
        {
            if (!IsSameVideo(item, page, videoQuality))
            {
                continue;
            }

            _notificationService.Show(
                $"{page.Name}{DictionaryResource.GetString("TipAlreadyToAddDownloading")}");
            return true;
        }

        foreach (var item in _downloadLists.Downloaded)
        {
            if (!IsSameVideo(item, page, videoQuality))
            {
                continue;
            }

            return strategy switch
            {
                RepeatDownloadStrategy.Ask => await ResolveAskAsync(item, cancellationToken)
                    .ConfigureAwait(true),
                RepeatDownloadStrategy.ReDownload => false,
                RepeatDownloadStrategy.JumpOver => true,
                _ => true
            };
        }

        return false;
    }

    private async Task<bool> ResolveAskAsync(
        DownloadedItem item,
        CancellationToken cancellationToken)
    {
        var result = await _dialogService.ShowAsync(
            new AppDialogRequest(
                AppDialog.AlreadyDownloaded,
                new Dictionary<string, object?>
                {
                    ["message"] = $"{item.Name}已下载，是否重新下载"
                }),
            cancellationToken).ConfigureAwait(true);
        if (result.Outcome != AppDialogOutcome.Accepted)
        {
            return true;
        }

        await _projectionStore
            .RemoveDownloadedAsync(item, cancellationToken)
            .ConfigureAwait(true);
        _downloadLists.RemoveDownloaded(item);
        return false;
    }

    private static bool IsSameVideo(
        DownloadBaseItem item,
        VideoPage page,
        VideoQuality videoQuality)
    {
        var downloadBase = item.DownloadBase;
        var isSameVideo = downloadBase.Cid == page.Cid
            && item.Resolution.Id == videoQuality.Quality
            && item.VideoCodecName == videoQuality.SelectedVideoCodec;
        if (page.PlayUrl?.Dash != null)
        {
            isSameVideo = isSameVideo && item.AudioCodec.Name == page.AudioQualityFormat;
        }

        return isSameVideo;
    }
}
