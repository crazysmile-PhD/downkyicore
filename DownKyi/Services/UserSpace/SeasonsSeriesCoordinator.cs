using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services.Download;
using Prism.Events;

namespace DownKyi.Services.UserSpace;

internal enum SeasonsSeriesKind
{
    Season = 1,
    Series = 2
}

internal sealed record SeasonsSeriesDownloadItem(string Bvid, bool IsSelected);

internal interface ISeasonsSeriesCoordinator
{
    Task<IReadOnlyList<SpaceSeasonsSeriesArchives>> LoadPageAsync(
        long mid,
        long id,
        SeasonsSeriesKind kind,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<int> AddToDownloadAsync(
        AddToDownloadService addToDownloadService,
        IReadOnlyList<SeasonsSeriesDownloadItem> items,
        bool onlySelected,
        string directory,
        IEventAggregator eventAggregator,
        IDialogService? dialogService,
        CancellationToken cancellationToken);
}

internal sealed class SeasonsSeriesCoordinator : ISeasonsSeriesCoordinator
{
    public Task<IReadOnlyList<SpaceSeasonsSeriesArchives>> LoadPageAsync(
        long mid,
        long id,
        SeasonsSeriesKind kind,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<SpaceSeasonsSeriesArchives>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archives = kind switch
            {
                SeasonsSeriesKind.Season => LoadSeason(mid, id, page, pageSize),
                SeasonsSeriesKind.Series => LoadSeries(mid, id, page, pageSize),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
            cancellationToken.ThrowIfCancellationRequested();
            return archives;
        }, cancellationToken);
    }

    public Task<int> AddToDownloadAsync(
        AddToDownloadService addToDownloadService,
        IReadOnlyList<SeasonsSeriesDownloadItem> items,
        bool onlySelected,
        string directory,
        IEventAggregator eventAggregator,
        IDialogService? dialogService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addToDownloadService);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(eventAggregator);

        return Task.Run(async () =>
        {
            var addedCount = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (onlySelected && !item.IsSelected)
                {
                    continue;
                }

                var videoInfoService = new VideoInfoService(item.Bvid);
                addToDownloadService.SetVideoInfoService(videoInfoService);
                addToDownloadService.GetVideo();
                addToDownloadService.ParseVideo(videoInfoService);
                cancellationToken.ThrowIfCancellationRequested();
                addedCount += await addToDownloadService
                    .AddToDownload(eventAggregator, dialogService, directory)
                    .ConfigureAwait(false);
            }

            return addedCount;
        }, cancellationToken);
    }

    private static IReadOnlyList<SpaceSeasonsSeriesArchives> LoadSeason(
        long mid,
        long id,
        int page,
        int pageSize)
    {
        var season = Core.BiliApi.Users.UserSpace.GetSeasonsDetail(mid, id, page, pageSize);
        return season == null || season.Meta.Total == 0
            ? Array.Empty<SpaceSeasonsSeriesArchives>()
            : season.Archives;
    }

    private static IReadOnlyList<SpaceSeasonsSeriesArchives> LoadSeries(
        long mid,
        long id,
        int page,
        int pageSize)
    {
        var meta = Core.BiliApi.Users.UserSpace.GetSeriesMeta(id);
        var series = Core.BiliApi.Users.UserSpace.GetSeriesDetail(mid, id, page, pageSize);
        return series == null || meta?.Meta.Total == 0
            ? Array.Empty<SpaceSeasonsSeriesArchives>()
            : series.Archives;
    }
}
