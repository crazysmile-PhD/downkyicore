using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DownKyi.Core.Settings;
using DownKyi.Presentation;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadListState
{
    private readonly object _sync = new();
    private readonly RangeObservableCollection<DownloadingItem> _downloading = new();
    private readonly RangeObservableCollection<DownloadedItem> _downloaded = new();

    public DownloadListState()
    {
        Downloading = new ReadOnlyObservableCollection<DownloadingItem>(_downloading);
        Downloaded = new ReadOnlyObservableCollection<DownloadedItem>(_downloaded);
    }

    public ReadOnlyObservableCollection<DownloadingItem> Downloading { get; }

    public ReadOnlyObservableCollection<DownloadedItem> Downloaded { get; }

    public void AddDownloading(DownloadingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            _downloading.Add(item);
        }
    }

    public void AddDownloadingRange(IEnumerable<DownloadingItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_sync)
        {
            _downloading.AddRange(items);
        }
    }

    public bool RemoveDownloading(DownloadingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            return _downloading.Remove(item);
        }
    }

    public void AddDownloaded(DownloadedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            _downloaded.Add(item);
        }
    }

    public void AddDownloadedRange(IEnumerable<DownloadedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_sync)
        {
            _downloaded.AddRange(items);
        }
    }

    public bool RemoveDownloaded(DownloadedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            return _downloaded.Remove(item);
        }
    }

    public void ClearDownloaded()
    {
        lock (_sync)
        {
            _downloaded.Clear();
        }
    }

    public void ReplaceDownloaded(IEnumerable<DownloadedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_sync)
        {
            ReplaceDownloadedCore(items.ToList());
        }
    }

    public void SortDownloaded(DownloadFinishedSort finishedSort)
    {
        lock (_sync)
        {
            var items = _downloaded.ToList();
            items.Sort(finishedSort switch
            {
                DownloadFinishedSort.DownloadAsc => CompareFinishedAscending,
                DownloadFinishedSort.DownloadDesc => CompareFinishedDescending,
                DownloadFinishedSort.Number => CompareTitleAndOrder,
                _ => static (_, _) => 0
            });
            ReplaceDownloadedCore(items);
        }
    }

    public IReadOnlyList<DownloadingItem> GetDownloadingSnapshot()
    {
        lock (_sync)
        {
            return _downloading.ToArray();
        }
    }

    public IReadOnlyList<DownloadedItem> GetDownloadedSnapshot()
    {
        lock (_sync)
        {
            return _downloaded.ToArray();
        }
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
        _downloaded.ReplaceRange(items);
    }
}
