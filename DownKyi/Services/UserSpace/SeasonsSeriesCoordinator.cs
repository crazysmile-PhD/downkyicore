using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.Media;

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

    Task<int?> AddToDownloadAsync(
        IReadOnlyList<SeasonsSeriesDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken);
}

internal sealed class SeasonsSeriesCoordinator : ISeasonsSeriesCoordinator
{
    private readonly IContentDownloadCoordinator _downloadCoordinator;

    public SeasonsSeriesCoordinator(IContentDownloadCoordinator downloadCoordinator)
    {
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
    }

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

    public Task<int?> AddToDownloadAsync(
        IReadOnlyList<SeasonsSeriesDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var downloadItems = new List<ContentDownloadItem>(items.Count);
        foreach (var item in items)
        {
            downloadItems.Add(new ContentDownloadItem(item.Bvid, DownloadInfoKind.Video, item.IsSelected));
        }

        return _downloadCoordinator.AddAsync(
            downloadItems,
            onlySelected,
            cancellationToken);
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
