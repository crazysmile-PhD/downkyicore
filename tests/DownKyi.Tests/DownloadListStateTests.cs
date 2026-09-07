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
        state.AddDownloaded(later);
        state.AddDownloaded(earlier);

        state.SortDownloaded(DownloadFinishedSort.DownloadAsc);

        Assert.Same(collection, state.Downloaded);
        Assert.Equal([earlier, later], state.Downloaded);
    }

    [Fact]
    public void ReplaceDownloadedSnapshotsItsInputBeforeClearing()
    {
        var state = new DownloadListState();
        var item = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        state.AddDownloaded(item);

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
        state.AddDownloadedRange([secondEpisode, otherTitle, firstEpisode]);

        state.SortDownloaded(DownloadFinishedSort.Number);

        Assert.Equal([otherTitle, firstEpisode, secondEpisode], state.Downloaded);
    }

    [Fact]
    public void AddDownloadedRangeDoesNotDuplicateItemsAcrossPages()
    {
        var state = new DownloadListState();
        var first = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        var duplicate = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        var second = CreateDownloadedItem("B", order: 2, finishedTimestamp: 20);

        state.AddDownloadedRange([first]);
        state.AddDownloadedRange([duplicate, second]);

        Assert.Equal([first, second], state.Downloaded);
    }

    [Fact]
    public void ExposedCollectionsRejectExternalMutation()
    {
        var state = new DownloadListState();
        var item = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<DownloadedItem>)state.Downloaded).Add(item));
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
