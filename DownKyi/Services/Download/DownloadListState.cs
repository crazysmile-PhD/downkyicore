using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Core.Settings;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadListState
{
    public ImmutableObservableCollection<DownloadingItem> Downloading { get; } = new();

    public ImmutableObservableCollection<DownloadedItem> Downloaded { get; } = new();

    public void ReplaceDownloaded(IEnumerable<DownloadedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ReplaceDownloadedCore(items.ToList());
    }

    public void SortDownloaded(DownloadFinishedSort finishedSort)
    {
        var items = Downloaded.ToList();
        items.Sort(finishedSort switch
        {
            DownloadFinishedSort.DownloadAsc => CompareFinishedAscending,
            DownloadFinishedSort.DownloadDesc => CompareFinishedDescending,
            DownloadFinishedSort.Number => CompareTitleAndOrder,
            _ => static (_, _) => 0
        });
        ReplaceDownloadedCore(items);
    }

    private static int CompareFinishedAscending(DownloadedItem left, DownloadedItem right)
    {
        return left.Downloaded.FinishedTimestamp.CompareTo(right.Downloaded.FinishedTimestamp);
    }

    private static int CompareFinishedDescending(DownloadedItem left, DownloadedItem right)
    {
        return right.Downloaded.FinishedTimestamp.CompareTo(left.Downloaded.FinishedTimestamp);
    }

    private static int CompareTitleAndOrder(DownloadedItem left, DownloadedItem right)
    {
        var titleComparison = string.Compare(left.MainTitle, right.MainTitle, StringComparison.Ordinal);
        return titleComparison == 0 ? left.Order.CompareTo(right.Order) : titleComparison;
    }

    private void ReplaceDownloadedCore(IReadOnlyList<DownloadedItem> items)
    {
        Downloaded.Clear();
        foreach (var item in items)
        {
            Downloaded.Add(item);
        }
    }
}
