using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

/// <summary>
/// Maintains an in-process index of active output reservations.
///
/// SQLite remains the durable authority. This index exists only to avoid
/// probing SQLite once for every historical numeric suffix.
/// </summary>
internal sealed class DownloadOutputReservationIndex : IDisposable
{
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly object _sync = new();

    private readonly HashSet<string> _activeKeys =
        new(StringComparer.Ordinal);

    private readonly Dictionary<DownloadTaskId, string> _taskKeys =
        new();

    private readonly Dictionary<string, int> _nextSuffixByBase =
        new(StringComparer.Ordinal);

    private readonly List<DownloadTaskChangedEventArgs> _pendingChanges =
        [];

    private Task? _initializeTask;
    private bool _initialized;
    private bool _disposed;

    public DownloadOutputReservationIndex(
        IDownloadTaskApplicationService tasks)
    {
        _tasks =
            tasks
            ?? throw new ArgumentNullException(nameof(tasks));

        // Subscribe before the initial load so changes racing with startup
        // can be replayed after the snapshot has been materialized.
        _tasks.TaskChanged += OnTaskChanged;
    }

    public Task EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        Task initializeTask;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _initializeTask ??=
                InitializeCoreAsync();

            initializeTask = _initializeTask;
        }

        // Caller cancellation should not poison the shared initialization.
        return initializeTask.WaitAsync(cancellationToken);
    }

    public int GetNextSuffix(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "Output reservation index is not initialized.");
            }

            var baseKey =
                CreateKey(basePath);

            var suffix =
                _nextSuffixByBase.TryGetValue(
                    baseKey,
                    out var remembered)
                    ? remembered
                    : 0;

            while (_activeKeys.Contains(
                       CreateCandidateKey(
                           basePath,
                           suffix)))
            {
                suffix =
                    checked(suffix + 1);
            }

            _nextSuffixByBase[baseKey] =
                suffix;

            return suffix;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeKeys.Clear();
            _taskKeys.Clear();
            _nextSuffixByBase.Clear();
            _pendingChanges.Clear();
        }

        _tasks.TaskChanged -= OnTaskChanged;
    }

    private async Task InitializeCoreAsync()
    {
        var activeOutputPaths =
            await _tasks
                .GetActiveOutputPathsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);

            foreach (var path in activeOutputPaths)
            {
                _activeKeys.Add(
                    CreateKey(path));
            }

            // Events may have happened while the active reservation snapshot was loading.
            // Replaying them makes the loaded snapshot converge to the newest
            // state. Add/remove operations are idempotent.
            foreach (var change in _pendingChanges)
            {
                ApplyChangeCore(change);
            }

            _pendingChanges.Clear();
            _initialized = true;
        }
    }

    private void OnTaskChanged(
        object? sender,
        DownloadTaskChangedEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (!_initialized)
            {
                _pendingChanges.Add(args);
                return;
            }

            ApplyChangeCore(args);
        }
    }

    private void ApplyChangeCore(
        DownloadTaskChangedEventArgs args)
    {
        if (args.Kind == DownloadTaskChangeKind.HistoryCleared)
        {
            return;
        }

        var snapshot = args.Snapshot;

        if (args.Kind == DownloadTaskChangeKind.Deleted ||
            snapshot?.Phase is DownloadPhase.Completed
                or DownloadPhase.Deleted)
        {
            ReleaseTaskCore(
                args.TaskId,
                snapshot?.Output.BasePath);

            return;
        }

        if (snapshot != null)
        {
            TrackTaskCore(snapshot);
        }
    }

    private void TrackTaskCore(DownloadTask task)
    {
        var key =
            CreateKey(task.Output.BasePath);

        if (_taskKeys.TryGetValue(
                task.Id,
                out var previousKey) &&
            !string.Equals(
                previousKey,
                key,
                StringComparison.Ordinal))
        {
            _activeKeys.Remove(previousKey);
            LowerHintsForReleasedKeyCore(previousKey);
        }

        _taskKeys[task.Id] = key;
        _activeKeys.Add(key);
    }

    private void ReleaseTaskCore(
        DownloadTaskId taskId,
        string? snapshotPath)
    {
        if (_taskKeys.Remove(
                taskId,
                out var existingKey))
        {
            _activeKeys.Remove(existingKey);
            LowerHintsForReleasedKeyCore(existingKey);
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            var key =
                CreateKey(snapshotPath);

            _activeKeys.Remove(key);
            LowerHintsForReleasedKeyCore(key);
        }
    }

    private void LowerHintsForReleasedKeyCore(
        string releasedKey)
    {
        foreach (var baseKey in
                 _nextSuffixByBase.Keys.ToArray())
        {
            if (!TryGetSuffix(
                    baseKey,
                    releasedKey,
                    out var releasedSuffix))
            {
                continue;
            }

            if (releasedSuffix <
                _nextSuffixByBase[baseKey])
            {
                _nextSuffixByBase[baseKey] =
                    releasedSuffix;
            }
        }
    }

    private static bool TryGetSuffix(
        string baseKey,
        string candidateKey,
        out int suffix)
    {
        if (string.Equals(
                baseKey,
                candidateKey,
                StringComparison.Ordinal))
        {
            suffix = 0;
            return true;
        }

        var prefix = baseKey + "(";

        if (!candidateKey.StartsWith(
                prefix,
                StringComparison.Ordinal) ||
            !candidateKey.EndsWith(
                ')'))
        {
            suffix = 0;
            return false;
        }

        var suffixText =
            candidateKey[
                prefix.Length..
                ^1];

        return
            int.TryParse(
                suffixText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out suffix) &&
            suffix > 0;
    }

    private static string CreateCandidateKey(
        string basePath,
        int suffix)
    {
        var candidate =
            suffix == 0
                ? basePath
                : $"{basePath}({suffix})";

        return CreateKey(candidate);
    }

    private static string CreateKey(string path)
    {
        // This index is only an in-process acceleration structure.
        // SQLite remains the durable physical-identity authority.
        var key =
            DownloadOutputPathKey
                .NormalizeLogicalPath(path);

        return DownloadOutputPathKey
            .UsesCaseInsensitiveComparison
            ? key.ToUpperInvariant()
            : key;
    }
}