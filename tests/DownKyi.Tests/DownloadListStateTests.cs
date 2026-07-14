using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadListStateTests
{
    [Fact]
    public void SortDownloadedPreservesCollectionIdentity()
    {
        var state = new DownloadListState();
        var collection = state.Downloaded;
        var later = CreateDownloadedItem("B", order: 2, finishedTimestamp: 20);
        var earlier = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        collection.Add(later);
        collection.Add(earlier);

        state.SortDownloaded(DownloadFinishedSort.DownloadAsc);

        Assert.Same(collection, state.Downloaded);
        Assert.Equal([earlier, later], state.Downloaded);
    }

    [Fact]
    public void ReplaceDownloadedSnapshotsItsInputBeforeClearing()
    {
        var state = new DownloadListState();
        var item = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        state.Downloaded.Add(item);

        state.ReplaceDownloaded(state.Downloaded);

        Assert.Same(item, Assert.Single(state.Downloaded));
    }

    [Fact]
    public void SortDownloadedOrdersEqualTitlesByEpisodeOrder()
    {
        var state = new DownloadListState();
        var secondEpisode = CreateDownloadedItem("Series", order: 2, finishedTimestamp: 10);
        var otherTitle = CreateDownloadedItem("Another", order: 5, finishedTimestamp: 30);
        var firstEpisode = CreateDownloadedItem("Series", order: 1, finishedTimestamp: 20);
        state.Downloaded.AddRange([secondEpisode, otherTitle, firstEpisode]);

        state.SortDownloaded(DownloadFinishedSort.Number);

        Assert.Equal([otherTitle, firstEpisode, secondEpisode], state.Downloaded);
    }

    private static DownloadedItem CreateDownloadedItem(
        string title,
        int order,
        long finishedTimestamp)
    {
        return new DownloadedItem
        {
            DownloadBase = new DownloadBase
            {
                Id = $"{title}-{order}",
                MainTitle = title,
                Order = order
            },
            Downloaded = new Downloaded
            {
                Id = $"{title}-{order}",
                FinishedTimestamp = finishedTimestamp
            }
        };
    }
}
