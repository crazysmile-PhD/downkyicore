using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.DownloadManager;

// A view-only grouping of existing tasks; it never owns scheduling or task identity.
internal sealed class DownloadQueueGroup(DownloadQueueSourceViewState viewState) : ObservableObject
{
    private bool _filtering;
    public IReadOnlyList<DownloadingItem> Items { get; private set; } = [];
    public IReadOnlyList<DownloadingItem> Matches { get; private set; } = [];
    public string Title => Items.Count == 0 ? string.Empty
        : string.IsNullOrWhiteSpace(Items[0].MainTitle) ? SourceLabel : Items[0].MainTitle;
    public string SourceLabel => Items.Count == 0 ? string.Empty
        : !string.IsNullOrWhiteSpace(Items[0].DownloadBase.Bvid) ? Items[0].DownloadBase.Bvid
        : $"av{Items[0].DownloadBase.Avid.ToString(CultureInfo.InvariantCulture)}";
    public long SortOrder => viewState.Order;
    public bool HasHeader => viewState.HasHeader;
    public bool IsExpanded
    {
        get => _filtering ? viewState.FilterExpanded : viewState.Expanded;
        set { if (_filtering) viewState.FilterExpanded = value; else viewState.Expanded = value; }
    }
    public string Chevron => IsExpanded ? "M 3,5 L 8,10 L 13,5" : "M 5,3 L 10,8 L 5,13";
    public string ExpansionHint => DictionaryResource.GetString(
        IsExpanded ? "DownloadCollapseGroup" : "DownloadExpandGroup");
    public bool HasFailures => Matches.Any(IsFailed);
    public bool? SelectionState => Matches.Count == 0 || Matches.All(item => !item.IsSelected)
        ? false
        : Matches.All(item => item.IsSelected) ? true : null;

    public string Summary
    {
        get
        {
            var active = Matches.Count(item => item.Downloading?.DownloadStatus == DownloadStatus.Downloading);
            var paused = Matches.Count(item => item.Downloading?.DownloadStatus is DownloadStatus.Pause or DownloadStatus.PauseStarted);
            var failed = Matches.Count(IsFailed);
            return Matches.Count == Items.Count
                ? DictionaryResource.Format("DownloadGroupSummary", "{0} tasks · Active {1} · Paused {2} · Failed {3}",
                    Items.Count, active, paused, failed)
                : DictionaryResource.Format("DownloadGroupFilteredSummary", "{0}/{1} match · Active {2} · Paused {3} · Failed {4}",
                    Matches.Count, Items.Count, active, paused, failed);
        }
    }

    public void Update(IReadOnlyList<DownloadingItem> items, IReadOnlyList<DownloadingItem> matches, bool filtering)
    {
        _filtering = filtering;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Matches = matches ?? throw new ArgumentNullException(nameof(matches));
        viewState.HasHeader |= items.Count > 1;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(Chevron));
        OnPropertyChanged(nameof(ExpansionHint));
    }

    internal static bool IsFailed(DownloadingItem item) =>
        item.Downloading?.DownloadStatus == DownloadStatus.DownloadFailed;
}
