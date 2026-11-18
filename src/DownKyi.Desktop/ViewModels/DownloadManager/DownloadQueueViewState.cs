using System;
using System.Collections.Generic;
using System.Linq;

namespace DownKyi.ViewModels.DownloadManager;

// Session-only view state survives tab recreation without changing stored download tasks.
internal sealed class DownloadQueueViewState
{
    private readonly Dictionary<string, DownloadQueueSourceViewState> _sources = new(StringComparer.Ordinal);
    private long _nextSourceOrder;
    public string SearchText { get; set; } = string.Empty;
    public bool FailedOnly { get; set; }
    public DownloadQueueSourceViewState GetSource(string key)
    {
        if (_sources.TryGetValue(key, out var source)) return source;
        source = new DownloadQueueSourceViewState(_nextSourceOrder++);
        _sources.Add(key, source);
        return source;
    }

    public void ResetFilterExpansion()
    {
        foreach (var source in _sources.Values) source.FilterExpanded = true;
    }

    public void RetainSources(ISet<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var removed in _sources.Keys.Where(key => !sources.Contains(key)).ToArray())
            _sources.Remove(removed);
    }
}

internal sealed class DownloadQueueSourceViewState(long order)
{
    public long Order { get; } = order;
    public bool Expanded { get; set; } = true;
    public bool FilterExpanded { get; set; } = true;
    public bool HasHeader { get; set; }
}
