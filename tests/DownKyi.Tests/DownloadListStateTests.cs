using System.Collections.Specialized;
using DownKyi.Application.Settings;
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
    public void SortDownloadedOrdersEqualTitlesByEpisodeOrder()
    {
        var state = new DownloadListState();
        var secondEpisode = CreateDownloadedItem("Series", order: 2, finishedTimestamp: 10);
        var otherTitle = CreateDownloadedItem("Another", order: 5, finishedTimestamp: 30);
        var firstEpisode = CreateDownloadedItem("Series", order: 1, finishedTimestamp: 20);
        state.AddDownloaded(secondEpisode);
        state.AddDownloaded(otherTitle);
        state.AddDownloaded(firstEpisode);

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
    public void RemovedDownloadedIdRejectsDelayedAdd()
    {
        var state = new DownloadListState();
        var item = CreateDownloadedItem("A", order: 1, finishedTimestamp: 10);
        state.AddDownloaded(item);

        Assert.True(state.RemoveDownloaded(item));
        state.AddDownloaded(item);

        Assert.Empty(state.Downloaded);
    }

    [Theory]
    [InlineData(DownloadFinishedSort.DownloadAsc, "A-1,C-3,B-2")]
    [InlineData(DownloadFinishedSort.DownloadDesc, "B-2,C-3,A-1")]
    [InlineData(DownloadFinishedSort.Number, "A-1,B-2,C-3")]
    public void CompletionInsertsInCurrentOrderWithoutResettingTheCollection(
        DownloadFinishedSort sort, string expectedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        var state = new DownloadListState();
        state.LoadDownloadedHistory([
            CreateDownloadedItem("B", 2, 20), CreateDownloadedItem("A", 1, 10)]);
        state.SortDownloaded(sort);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)state.Downloaded).CollectionChanged += (_, args) => changes.Add(args.Action);

        state.AddDownloaded(CreateDownloadedItem("C", 3, 15));
        state.AddDownloaded(CreateDownloadedItem("C", 3, 99));
        state.SortDownloaded(sort);

        Assert.Equal(expectedIds.Split(','), state.Downloaded.Select(item => item.DownloadBase.Id));
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
    }

    [Fact]
    public void HistoryMergeAndClearKeepIdentityAndDelayedDeleteSemantics()
    {
        var state = new DownloadListState(DownloadFinishedSort.DownloadDesc);
        var first = CreateDownloadedItem("A", 1, 10);
        var second = CreateDownloadedItem("B", 2, 20);
        var removed = CreateDownloadedItem("removed", 3, 30);
        state.AddDownloaded(first);
        state.RemoveDownloaded(removed);
        state.LoadDownloadedHistory([first, first, second, removed]);
        Assert.Equal([second, first], state.Downloaded);
        state.AddDownloaded(second);
        Assert.Equal(2, state.Downloaded.Count);

        state.ClearDownloaded();
        state.AddDownloaded(first);
        state.AddDownloaded(CreateDownloadedItem("new", 4, 40));

        Assert.Equal("new-4", Assert.Single(state.Downloaded).DownloadBase.Id);
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
