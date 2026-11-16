using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DownKyi.Application.Settings;
using DownKyi.Presentation;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal interface IDownloadedListProjection
{
    void RemoveDownloaded(DownloadedItem item);
}

internal sealed class DownloadListState : IDownloadedListProjection
{
    private readonly RangeObservableCollection<DownloadingItem> _downloading = new();
    private readonly RangeObservableCollection<DownloadedItem> _downloaded = new();
    private readonly HashSet<string> _removedDownloadedIds = new(StringComparer.Ordinal);

    public DownloadListState()
    {
        Downloading = new ReadOnlyObservableCollection<DownloadingItem>(_downloading);
        Downloaded = new ReadOnlyObservableCollection<DownloadedItem>(_downloaded);
    }

    public ReadOnlyObservableCollection<DownloadingItem> Downloading { get; }

    public ReadOnlyObservableCollection<DownloadedItem> Downloaded { get; }

    public bool IsDownloadedHistoryLoaded { get; private set; }

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
            || _downloaded.Any(candidate => GetTaskId(candidate) == taskId))
        {
            return;
        }

        _downloaded.Add(item);
    }

    public bool RemoveDownloaded(DownloadedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var taskId = GetTaskId(item);
        _removedDownloadedIds.Add(taskId);
        var loadedItem = _downloaded.FirstOrDefault(candidate => GetTaskId(candidate) == taskId);
        return loadedItem != null && _downloaded.Remove(loadedItem);
    }

    void IDownloadedListProjection.RemoveDownloaded(DownloadedItem item)
    {
        RemoveDownloaded(item);
    }

    public void ClearDownloaded()
    {
        _downloaded.Clear();
    }

    public void LoadDownloadedHistory(IEnumerable<DownloadedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (IsDownloadedHistoryLoaded)
        {
            return;
        }

        var loadedItems = items
            .Where(item => !_removedDownloadedIds.Contains(GetTaskId(item)))
            .ToList();
        var loadedIds = loadedItems
            .Select(GetTaskId)
            .ToHashSet(StringComparer.Ordinal);
        loadedItems.AddRange(_downloaded.Where(item => loadedIds.Add(GetTaskId(item))));
        ReplaceDownloadedCore(loadedItems);
        IsDownloadedHistoryLoaded = true;
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

    private static string GetTaskId(DownloadedItem item)
    {
        return item.HistoryRecord?.Id.Value ?? item.DownloadBase.Id;
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
