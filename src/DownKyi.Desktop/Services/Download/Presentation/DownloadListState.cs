using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Application.Settings;
using DownKyi.Presentation;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal interface IDownloadedListProjection
{
    void RemoveDownloaded(DownloadedItem item);
}

internal sealed class DownloadListState : ObservableObject, IDownloadedListProjection
{
    private readonly RangeObservableCollection<DownloadingItem> _downloading = new();
    private readonly RangeObservableCollection<DownloadedItem> _downloaded = new();
    private readonly HashSet<string> _removedDownloadedIds = new(StringComparer.Ordinal);
    private HashSet<string> _downloadedIds = new(StringComparer.Ordinal);
    private DownloadFinishedSort _finishedSort;
    private Comparison<DownloadedItem> _finishedComparison;
    private bool _hasMoreDownloadedHistory;

    public DownloadListState() : this(DownloadFinishedSort.DownloadAsc)
    {
    }

    public DownloadListState(DownloadFinishedSort finishedSort)
    {
        _finishedSort = finishedSort;
        _finishedComparison = GetFinishedComparison(finishedSort);
        Downloading = new ReadOnlyObservableCollection<DownloadingItem>(_downloading);
        Downloaded = new ReadOnlyObservableCollection<DownloadedItem>(_downloaded);
        _downloaded.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DownloadedCountDisplay));
    }

    public ReadOnlyObservableCollection<DownloadingItem> Downloading { get; }

    public ReadOnlyObservableCollection<DownloadedItem> Downloaded { get; }

    public DownloadQueueViewState QueueView { get; } = new();

    public bool IsDownloadedHistoryLoaded { get; private set; }

    public DownloadFinishedSort FinishedSort => _finishedSort;

    public bool HasMoreDownloadedHistory
    {
        get => _hasMoreDownloadedHistory;
        set
        {
            if (SetProperty(ref _hasMoreDownloadedHistory, value))
                OnPropertyChanged(nameof(DownloadedCountDisplay));
        }
    }

    public string DownloadedCountDisplay => _downloaded.Count.ToString(CultureInfo.CurrentCulture)
        + (HasMoreDownloadedHistory ? "+" : string.Empty);

    public IReadOnlySet<string> CaptureDownloadedIds() => new HashSet<string>(_downloadedIds, StringComparer.Ordinal);

    public void AddDownloading(DownloadingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _downloading.Add(item);
    }

    public void AddDownloadingRange(IEnumerable<DownloadingItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _downloading.AddRange(items);
    }

    public bool RemoveDownloading(DownloadingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _downloading.Remove(item);
    }

    public void AddDownloaded(DownloadedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var taskId = GetTaskId(item);
        if (_removedDownloadedIds.Contains(taskId)
            || !_downloadedIds.Add(taskId))
        {
            return;
        }

        var low = 0;
        var high = _downloaded.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_finishedComparison(_downloaded[middle], item) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        _downloaded.Insert(low, item);
    }

    public bool RemoveDownloaded(DownloadedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var taskId = GetTaskId(item);
        _removedDownloadedIds.Add(taskId);
        _downloadedIds.Remove(taskId);
        var loadedItem = _downloaded.FirstOrDefault(candidate => GetTaskId(candidate) == taskId);
        return loadedItem != null && _downloaded.Remove(loadedItem);
    }

    void IDownloadedListProjection.RemoveDownloaded(DownloadedItem item)
    {
        RemoveDownloaded(item);
    }

    public void ClearDownloaded()
    {
        _removedDownloadedIds.UnionWith(_downloadedIds);
        _downloadedIds.Clear();
        _downloaded.Clear();
        IsDownloadedHistoryLoaded = true;
        HasMoreDownloadedHistory = false;
    }

    public void LoadDownloadedHistory(IEnumerable<DownloadedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (IsDownloadedHistoryLoaded)
        {
            return;
        }

        ReplaceDownloadedHistoryPage(items, new HashSet<string>(StringComparer.Ordinal));
    }

    public void ReplaceDownloadedHistoryPage(IEnumerable<DownloadedItem> items, IReadOnlySet<string> precedingIds)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(precedingIds);

        var capacity = checked((items.TryGetNonEnumeratedCount(out var count) ? count : 0) + _downloaded.Count);
        var loadedItems = new List<DownloadedItem>(capacity);
        var loadedIds = new HashSet<string>(capacity, StringComparer.Ordinal);
        foreach (var item in items.Concat(_downloaded.Where(item => !precedingIds.Contains(GetTaskId(item)))))
        {
            var taskId = GetTaskId(item);
            if (!_removedDownloadedIds.Contains(taskId) && loadedIds.Add(taskId))
            {
                loadedItems.Add(item);
            }
        }

        loadedItems.Sort(_finishedComparison);
        _downloadedIds = loadedIds;
        ReplaceDownloadedCore(loadedItems);
        IsDownloadedHistoryLoaded = true;
    }

    public void SortDownloaded(DownloadFinishedSort finishedSort)
    {
        if (_finishedSort == finishedSort)
        {
            return;
        }

        _finishedSort = finishedSort;
        _finishedComparison = GetFinishedComparison(finishedSort);
        var items = Downloaded.ToList();
        items.Sort(_finishedComparison);
        ReplaceDownloadedCore(items);
    }

    private static Comparison<DownloadedItem> GetFinishedComparison(DownloadFinishedSort finishedSort) =>
        finishedSort switch
        {
            DownloadFinishedSort.DownloadAsc => CompareFinishedAscending,
            DownloadFinishedSort.DownloadDesc => CompareFinishedDescending,
            DownloadFinishedSort.Number => CompareTitleAndOrder,
            _ => static (_, _) => 0
        };

    private static int CompareFinishedAscending(DownloadedItem left, DownloadedItem right)
    {
        var comparison = left.Downloaded.FinishedTimestamp.CompareTo(right.Downloaded.FinishedTimestamp);
        return comparison != 0 ? comparison : string.CompareOrdinal(GetTaskId(left), GetTaskId(right));
    }

    private static string GetTaskId(DownloadedItem item)
    {
        return item.HistoryRecord?.Id.Value ?? item.DownloadBase.Id;
    }

    private static int CompareFinishedDescending(DownloadedItem left, DownloadedItem right)
    {
        return CompareFinishedAscending(right, left);
    }

    private static int CompareTitleAndOrder(DownloadedItem left, DownloadedItem right)
    {
        var titleComparison = string.Compare(left.MainTitle, right.MainTitle, StringComparison.Ordinal);
        if (titleComparison != 0) return titleComparison;
        var orderComparison = left.Order.CompareTo(right.Order);
        return orderComparison != 0 ? orderComparison : string.CompareOrdinal(GetTaskId(left), GetTaskId(right));
    }

    private void ReplaceDownloadedCore(IReadOnlyList<DownloadedItem> items)
    {
        _downloaded.ReplaceRange(items);
    }
}
