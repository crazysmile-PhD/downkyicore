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
    public void ExposedCollectionsRejectExternalMutation()
    {
        var state = new DownloadListState();
        var item = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<DownloadedItem>)state.Downloaded).Add(item));
    }

    [Fact]
    public void DownloadedSnapshotRemainsStableWhenSourceChanges()
    {
        var state = new DownloadListState();
        var first = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        var second = CreateDownloadedItem("B", order: 2, finishedTimestamp: 20);
        state.AddDownloaded(first);

        var snapshot = state.GetDownloadedSnapshot();
        state.AddDownloaded(second);
        state.RemoveDownloaded(first);

        Assert.Equal([first], snapshot);
        Assert.Equal([second], state.Downloaded);
    }

    [Fact]
    public async Task DownloadedSnapshotsCanBeEnumeratedWhileCollectionChanges()
    {
        var state = new DownloadListState();
        var cancellationToken = TestContext.Current.CancellationToken;
        var items = Enumerable.Range(0, 20)
            .Select(index => CreateDownloadedItem($"Item {index}", index, index))
            .ToArray();

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 2_000; index++)
            {
                var item = items[index % items.Length];
                state.AddDownloaded(item);
                state.RemoveDownloaded(item);
            }
        }, cancellationToken);
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 2_000; index++)
            {
                foreach (var item in state.GetDownloadedSnapshot())
                {
                    Assert.NotNull(item);
                }
            }
        }, cancellationToken);

        await Task.WhenAll(writer, reader);
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
