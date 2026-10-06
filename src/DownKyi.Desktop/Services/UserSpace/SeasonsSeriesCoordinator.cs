using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.Media;

namespace DownKyi.Services.UserSpace;

internal enum SeasonsSeriesKind
{
    Season = 1,
    Series = 2
}

internal sealed record SeasonsSeriesDownloadItem(string Bvid, bool IsSelected);

internal sealed record SeasonsSeriesPageSnapshot(
    IReadOnlyList<SpaceSeasonsSeriesArchives> Archives,
    string Title,
    int TotalCount);

internal interface ISeasonsSeriesCoordinator
{
    Task<SeasonsSeriesPageSnapshot> LoadPageAsync(
        long mid,
        long id,
        SeasonsSeriesKind kind,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<ContentDownloadBatchResult?> AddToDownloadAsync(
        IReadOnlyList<SeasonsSeriesDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken);
}

internal sealed class SeasonsSeriesCoordinator : ISeasonsSeriesCoordinator
{
    private readonly IContentDownloadCoordinator _downloadCoordinator;
    private readonly IBilibiliApiClient _client;

    public SeasonsSeriesCoordinator(
        IContentDownloadCoordinator downloadCoordinator,
        IBilibiliApiClient client)
    {
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<SeasonsSeriesPageSnapshot> LoadPageAsync(
        long mid,
        long id,
        SeasonsSeriesKind kind,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (kind)
        {
            case SeasonsSeriesKind.Season:
                var season = await _client.GetSeasonsDetailAsync(
                    mid,
                    id,
                    page,
                    pageSize,
                    cancellationToken).ConfigureAwait(false);
                if (season == null)
                {
                    throw new InvalidOperationException("The requested Bilibili season returned no data.");
                }

                return new SeasonsSeriesPageSnapshot(
                    season.Archives,
                    season.Meta.Name,
                    season.Meta.Total);
            case SeasonsSeriesKind.Series:
                var meta = await _client.GetSeriesMetaAsync(id, cancellationToken)
                    .ConfigureAwait(false);
                var series = await _client.GetSeriesDetailAsync(
                    mid,
                    id,
                    page,
                    pageSize,
                    cancellationToken).ConfigureAwait(false);
                if (series == null || meta == null)
                {
                    throw new InvalidOperationException("The requested Bilibili series returned no data.");
                }

                if (meta.Meta.Mid != mid || meta.Meta.SeriesId != id)
                {
                    throw new InvalidOperationException("The requested Bilibili series did not match the supplied uploader and series IDs.");
                }

                return new SeasonsSeriesPageSnapshot(
                    series.Archives,
                    meta.Meta.Name,
                    meta.Meta.Total);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public Task<ContentDownloadBatchResult?> AddToDownloadAsync(
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

}
