using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

/// <summary>In-process logical suffix hint cache; SQLite remains authoritative.</summary>
internal sealed class DownloadOutputReservationIndex : IDisposable
{
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly object _sync = new();
    private readonly HashSet<string> _activeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<DownloadTaskId, string> _taskKeys = [];
    private readonly Dictionary<string, int> _nextSuffixByBase = new(StringComparer.Ordinal);
    private readonly List<DownloadTaskChangedEventArgs> _pendingChanges = [];
    private Task? _initializeTask;
    private bool _initialized;
    private bool _disposed;

    public DownloadOutputReservationIndex(IDownloadTaskApplicationService tasks)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _tasks.TaskChanged += OnTaskChanged;
    }

    public Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _initializeTask ??= InitializeCoreAsync();
            task = _initializeTask;
        }

        return task.WaitAsync(cancellationToken);
    }

    public int GetNextSuffix(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized) throw new InvalidOperationException("Output reservation index is not initialized.");
            var baseKey = CreateKey(basePath);
            var suffix = _nextSuffixByBase.TryGetValue(baseKey, out var remembered) ? remembered : 0;
            while (_activeKeys.Contains(CreateCandidateKey(basePath, suffix))) suffix = checked(suffix + 1);
            _nextSuffixByBase[baseKey] = suffix;
            return suffix;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _activeKeys.Clear(); _taskKeys.Clear(); _nextSuffixByBase.Clear(); _pendingChanges.Clear();
        }

        _tasks.TaskChanged -= OnTaskChanged;
    }

    private async Task InitializeCoreAsync()
    {
        var paths = await _tasks.GetActiveOutputPathsAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var path in paths) _activeKeys.Add(CreateKey(path));
            foreach (var change in _pendingChanges) ApplyChangeCore(change);
            _pendingChanges.Clear();
            _initialized = true;
        }
    }

    private void OnTaskChanged(object? sender, DownloadTaskChangedEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (!_initialized) { _pendingChanges.Add(args); return; }
            ApplyChangeCore(args);
        }
    }

    private void ApplyChangeCore(DownloadTaskChangedEventArgs args)
    {
        if (args.Kind == DownloadTaskChangeKind.HistoryCleared) return;
        if (args.Kind == DownloadTaskChangeKind.Deleted || args.Snapshot?.Phase is DownloadPhase.Completed or DownloadPhase.Deleted)
        {
            ReleaseTaskCore(args.TaskId, args.Snapshot?.Output.BasePath);
        }
        else if (args.Snapshot != null)
        {
            TrackTaskCore(args.Snapshot);
        }
    }

    private void TrackTaskCore(DownloadTask task)
    {
        var key = CreateKey(task.Output.BasePath);
        if (_taskKeys.TryGetValue(task.Id, out var prior) && !string.Equals(prior, key, StringComparison.Ordinal))
        {
            _activeKeys.Remove(prior); LowerHintsForReleasedKeyCore(prior);
        }

        _taskKeys[task.Id] = key; _activeKeys.Add(key);
    }

    private void ReleaseTaskCore(DownloadTaskId taskId, string? snapshotPath)
    {
        if (_taskKeys.Remove(taskId, out var existing))
        {
            _activeKeys.Remove(existing); LowerHintsForReleasedKeyCore(existing); return;
        }

        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            var key = CreateKey(snapshotPath); _activeKeys.Remove(key); LowerHintsForReleasedKeyCore(key);
        }
    }

    private void LowerHintsForReleasedKeyCore(string releasedKey)
    {
        foreach (var baseKey in _nextSuffixByBase.Keys.ToArray())
        {
            if (TryGetSuffix(baseKey, releasedKey, out var suffix) && suffix < _nextSuffixByBase[baseKey]) _nextSuffixByBase[baseKey] = suffix;
        }
    }

    private static bool TryGetSuffix(string baseKey, string candidateKey, out int suffix)
    {
        if (string.Equals(baseKey, candidateKey, StringComparison.Ordinal)) { suffix = 0; return true; }
        var prefix = baseKey + "(";
        if (!candidateKey.StartsWith(prefix, StringComparison.Ordinal) || !candidateKey.EndsWith(')')) { suffix = 0; return false; }
        return int.TryParse(candidateKey[prefix.Length..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out suffix) && suffix > 0;
    }

    private static string CreateCandidateKey(string basePath, int suffix) => CreateKey(suffix == 0 ? basePath : $"{basePath}({suffix})");
    private static string CreateKey(string path)
    {
        var key = DownloadOutputPathKey.NormalizeLogicalPath(path);
        return DownloadOutputPathKey.UsesCaseInsensitiveComparison ? key.ToUpperInvariant() : key;
    }
}
