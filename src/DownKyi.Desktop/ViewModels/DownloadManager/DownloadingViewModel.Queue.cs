using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Commands;
using DownKyi.Services;
using DownKyi.Utils;

namespace DownKyi.ViewModels.DownloadManager;

internal partial class DownloadingViewModel
{
    private readonly ObservableCollection<object> _queueRows = [];
    private readonly Dictionary<string, DownloadQueueGroup> _groups = new(StringComparer.Ordinal);
    private readonly HashSet<DownloadingItem> _observedItems = [];
    private readonly DownloadQueueViewState _queueView;
    private bool _updatingSelection;
    private DownloadingItem? _selectionAnchor;

    public ReadOnlyObservableCollection<object> QueueRows { get; private set; } = null!;
    public int SelectedCount => DownloadingList.Count(item => item.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool HasFilter => FailedOnly || !string.IsNullOrWhiteSpace(SearchText);
    public bool CanOperateQueue => HasSelection || !HasFilter;
    public bool HasNoMatches => DownloadingList.Count > 0 && QueueRows.Count == 0;
    public bool HasVisibleTasks => _queueRows.Any(row => row is DownloadingItem);
    public bool HasVisibleGroups => _queueRows.Any(row => row is DownloadQueueGroup);
    public bool? VisibleSelectionState
    {
        get
        {
            var visible = _queueRows.OfType<DownloadingItem>().ToArray();
            return visible.Length == 0 || visible.All(item => !item.IsSelected)
                ? false : visible.All(item => item.IsSelected) ? true : null;
        }
    }

    public string SearchText
    {
        get => _queueView.SearchText;
        set
        {
            value ??= string.Empty;
            if (SearchText == value) return;
            _queueView.SearchText = value;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public bool FailedOnly
    {
        get => _queueView.FailedOnly;
        set
        {
            if (FailedOnly == value) return;
            _queueView.FailedOnly = value;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public string SelectionSummary => DictionaryResource.Format(
        "DownloadSelectedCount", "{0} selected", SelectedCount);
    public string DeleteScopeText => DictionaryResource.GetString(
        HasSelection ? "DownloadRemoveSelected" : "DeleteAllDownloading");

    public RelayCommand<DownloadQueueGroup> ToggleGroupCommand { get; private set; } = null!;
    public RelayCommand<DownloadQueueGroup> SelectGroupCommand { get; private set; } = null!;
    public RelayCommand ExpandGroupsCommand { get; private set; } = null!;
    public RelayCommand CollapseGroupsCommand { get; private set; } = null!;
    public RelayCommand SelectVisibleCommand { get; private set; } = null!;
    public RelayCommand ClearSelectionCommand { get; private set; } = null!;
    public RelayCommand ClearFiltersCommand { get; private set; } = null!;
    public DownKyiAsyncDelegateCommand DeleteScopeCommand { get; private set; } = null!;

    private void InitializeQueue()
    {
        QueueRows = new ReadOnlyObservableCollection<object>(_queueRows);
        ToggleGroupCommand = new RelayCommand<DownloadQueueGroup>(group =>
        {
            if (group == null) return;
            group.IsExpanded = !group.IsExpanded;
            RebuildQueue();
        });
        SelectGroupCommand = new RelayCommand<DownloadQueueGroup>(group =>
        {
            if (group == null) return;
            var select = group.SelectionState != true;
            group.IsExpanded = true;
            RebuildQueue();
            SetSelection(group.Matches, select);
        });
        ExpandGroupsCommand = new RelayCommand(() => SetGroupsExpanded(true));
        CollapseGroupsCommand = new RelayCommand(() => SetGroupsExpanded(false));
        SelectVisibleCommand = new RelayCommand(() =>
            SetSelection(_queueRows.OfType<DownloadingItem>().ToArray(), VisibleSelectionState != true));
        ClearSelectionCommand = new RelayCommand(() => { _selectionAnchor = null; SetSelection(DownloadingList, false); });
        ClearFiltersCommand = new RelayCommand(() => { SearchText = string.Empty; FailedOnly = false; });
        DeleteScopeCommand = new DownKyiAsyncDelegateCommand(DeleteScopeAsync, _logger);
        ((INotifyCollectionChanged)DownloadingList).CollectionChanged += OnQueueChanged;
        RebuildQueue();
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildQueue();

    private void RefreshFilter()
    {
        _queueView.ResetFilterExpansion();
        RebuildQueue();
    }

    private void RebuildQueue()
    {
        var current = DownloadingList.ToHashSet();
        foreach (var removed in _observedItems.Except(current).ToArray())
        {
            removed.PropertyChanged -= OnQueueItemChanged;
            removed.IsSelected = false;
            removed.IsGrouped = false;
            _observedItems.Remove(removed);
        }
        foreach (var added in current.Except(_observedItems))
        {
            added.PropertyChanged += OnQueueItemChanged;
            _observedItems.Add(added);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var desired = new List<object>();
        foreach (var source in DownloadingList.GroupBy(SourceKey, StringComparer.Ordinal))
        {
            keys.Add(source.Key);
            if (!_groups.TryGetValue(source.Key, out var group))
            {
                group = new DownloadQueueGroup(_queueView.GetSource(source.Key));
                _groups.Add(source.Key, group);
            }
            var items = source.ToArray();
            var matches = items.Where(MatchesFilter).ToArray();
            group.Update(items, matches, HasFilter);
            foreach (var item in items) item.IsGrouped = group.HasHeader;
        }
        foreach (var stale in _groups.Keys.Where(key => !keys.Contains(key)).ToArray()) _groups.Remove(stale);
        _queueView.RetainSources(keys);
        foreach (var group in _groups.Values.OrderBy(group => group.SortOrder))
        {
            if (group.Matches.Count == 0) continue;
            if (group.HasHeader) desired.Add(group);
            if (!group.HasHeader || group.IsExpanded) desired.AddRange(group.Matches);
        }

        // Keep unaffected containers, focus and scroll position. Progress never rebuilds this list.
        var desiredSet = desired.ToHashSet();
        if (_selectionAnchor != null && !desiredSet.Contains(_selectionAnchor)) _selectionAnchor = null;
        SetSelection(DownloadingList.Where(item => !desiredSet.Contains(item)).ToArray(), false);
        for (var index = _queueRows.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(_queueRows[index])) _queueRows.RemoveAt(index);
        }
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < _queueRows.Count && ReferenceEquals(_queueRows[index], desired[index])) continue;
            var previous = _queueRows.IndexOf(desired[index]);
            if (previous >= 0) _queueRows.Move(previous, index);
            else _queueRows.Insert(index, desired[index]);
        }
        RefreshSelection();
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(CanOperateQueue));
    }

    private bool MatchesFilter(DownloadingItem item)
    {
        if (FailedOnly && !DownloadQueueGroup.IsFailed(item)) return false;
        var query = SearchText.Trim();
        return query.Length == 0
            || item.MainTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.DownloadBase.Bvid.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceKey(DownloadingItem item)
    {
        var metadata = item.DownloadBase;
        return !string.IsNullOrWhiteSpace(metadata.Bvid) ? $"bv:{metadata.Bvid}"
            : metadata.Avid > 0 ? $"av:{metadata.Avid.ToString(CultureInfo.InvariantCulture)}"
            : $"task:{metadata.Id}";
    }

    private void OnQueueItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadingItem.IsSelected))
        {
            if (!_updatingSelection) RefreshSelection();
        }
        else if (e.PropertyName is nameof(DownloadingItem.Downloading) or nameof(DownloadingItem.DownloadBase))
        {
            RebuildQueue();
        }
    }

    private void SetGroupsExpanded(bool expanded)
    {
        foreach (var group in _groups.Values) group.IsExpanded = expanded;
        RebuildQueue();
    }

    private void SetSelection(IEnumerable<DownloadingItem> items, bool selected)
    {
        _updatingSelection = true;
        try { foreach (var item in items) item.IsSelected = selected; }
        finally { _updatingSelection = false; }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var group in _groups.Values) group.Refresh();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(VisibleSelectionState));
        OnPropertyChanged(nameof(HasVisibleTasks));
        OnPropertyChanged(nameof(HasVisibleGroups));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(DeleteScopeText));
        OnPropertyChanged(nameof(CanOperateQueue));
    }

    private DownloadingItem[] SelectedSnapshot() => DownloadingList.Where(item => item.IsSelected).ToArray();

    internal void SelectVisibleItems() => SetSelection(_queueRows.OfType<DownloadingItem>().ToArray(), true);

    internal void SelectTask(DownloadingItem item, bool toggle, bool extend)
    {
        var visible = _queueRows.OfType<DownloadingItem>().ToArray();
        var target = Array.IndexOf(visible, item);
        if (target < 0) return;
        var anchor = _selectionAnchor == null ? -1 : Array.IndexOf(visible, _selectionAnchor);
        if (extend && anchor >= 0)
        {
            if (!toggle) SetSelection(DownloadingList, false);
            SetSelection(visible.Skip(Math.Min(anchor, target)).Take(Math.Abs(anchor - target) + 1), true);
        }
        else
        {
            var select = !toggle || !item.IsSelected;
            if (!toggle) SetSelection(DownloadingList, false);
            item.IsSelected = select;
            _selectionAnchor = item;
        }
    }

    private async Task DeleteScopeAsync()
    {
        // Capture the targets before awaiting confirmation, so later selection changes cannot widen deletion.
        var targets = HasSelection ? SelectedSnapshot() : DownloadingList.ToArray();
        if (targets.Length == 0 || !CanOperateQueue) return;
        var message = DictionaryResource.Format("DownloadConfirmRemoveCount",
            "Remove {0} download tasks?", targets.Length);
        var result = await new AlertService(AppDialogs).ShowWarning(message, 2).ConfigureAwait(true);
        if (result != AppDialogOutcome.Accepted) return;
        var remaining = targets.Where(DownloadingList.Contains).ToArray();
        await _downloadManagerCoordinator.DeleteAllAsync(remaining).ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ((INotifyCollectionChanged)DownloadingList).CollectionChanged -= OnQueueChanged;
            foreach (var item in _observedItems) item.PropertyChanged -= OnQueueItemChanged;
            _observedItems.Clear();
        }
        base.Dispose(disposing);
    }
}
