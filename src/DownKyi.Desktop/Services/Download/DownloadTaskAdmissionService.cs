using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskAdmissionService : IDisposable
{
    private readonly DownloadListState _downloadLists;
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly DownloadTaskProjectionStore _projections;
    private readonly IDownloadTaskQueue _taskQueue;
    private readonly DownloadOutputReservationIndex _reservationIndex;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private bool _disposed;

    public DownloadTaskAdmissionService(DownloadListState downloadLists, IDownloadTaskApplicationService tasks, DownloadTaskProjectionStore projections, IDownloadTaskQueue taskQueue)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _reservationIndex = new DownloadOutputReservationIndex(_tasks);
    }

    public async Task AdmitAsync(DownloadingItem item, bool autoAddNumberSuffix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try { await AdmitCoreAsync(item, autoAddNumberSuffix, cancellationToken).ConfigureAwait(true); }
        finally { _admissionGate.Release(); }
    }

    public async Task AdmitManyAsync(IReadOnlyList<DownloadingItem> items, bool autoAddNumberSuffix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (items.Count == 0) return;
        foreach (var item in items) ArgumentNullException.ThrowIfNull(item);

        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (items.Count == 1 || _tasks is not IDownloadTaskAtomicBatchApplicationService)
            {
                await AdmitSequentialCoreAsync(items, autoAddNumberSuffix, cancellationToken).ConfigureAwait(true);
                return;
            }

            await _reservationIndex.EnsureInitializedAsync(cancellationToken).ConfigureAwait(true);
            var originalPaths = items.Select(static item => item.DownloadBase.FilePath).ToArray();
            var occupiedPaths = DownloadOutputPathResolver.CaptureExistingBasePaths(originalPaths);
            var comparer = DownloadOutputPathKey.UsesCaseInsensitiveComparison ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var plannedPaths = new HashSet<string>(comparer);
            var nextSuffixes = new Dictionary<string, int>(comparer);

            try
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var requestedPath = originalPaths[index];
                    var requestedKey = DownloadOutputPathKey.NormalizeLogicalPath(requestedPath);
                    var suffix = autoAddNumberSuffix
                        ? nextSuffixes.TryGetValue(requestedKey, out var next) ? next : _reservationIndex.GetNextSuffix(requestedPath)
                        : 0;
                    items[index].DownloadBase.FilePath = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
                        requestedPath, autoAddNumberSuffix,
                        (candidate, _) => Task.FromResult(plannedPaths.Contains(DownloadOutputPathKey.NormalizeLogicalPath(candidate))),
                        cancellationToken, initialSuffix: suffix, occupiedPaths: occupiedPaths).ConfigureAwait(true);
                    var plannedKey = DownloadOutputPathKey.NormalizeLogicalPath(items[index].DownloadBase.FilePath);
                    if (!plannedPaths.Add(plannedKey)) throw new InvalidOperationException("Batch admission produced a duplicate planned path.");
                    if (autoAddNumberSuffix) nextSuffixes[requestedKey] = checked(GetResolvedSuffix(requestedPath, items[index].DownloadBase.FilePath) + 1);
                }
            }
            catch (IOException)
            {
                RestoreOriginalPaths(items, originalPaths);
                await AdmitSequentialCoreAsync(items, autoAddNumberSuffix, cancellationToken).ConfigureAwait(true);
                return;
            }
            catch { RestoreOriginalPaths(items, originalPaths); throw; }

            DownloadProjectionAddResult persisted;
            try { persisted = await _projections.TryAddDownloadingManyAtomicAsync(items, cancellationToken).ConfigureAwait(true); }
            catch { RestoreOriginalPaths(items, originalPaths); throw; }
            if (!persisted.IsSuccess)
            {
                RestoreOriginalPaths(items, originalPaths);
                await AdmitSequentialCoreAsync(items, autoAddNumberSuffix, cancellationToken).ConfigureAwait(true);
                return;
            }

            var taskIds = new DownloadTaskId[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                _downloadLists.AddDownloading(items[index]);
                taskIds[index] = new DownloadTaskId(items[index].DownloadBase.Id);
            }

            await _taskQueue.EnqueueManyAsync(taskIds, CancellationToken.None).ConfigureAwait(true);
        }
        finally { _admissionGate.Release(); }
    }

    private async Task AdmitSequentialCoreAsync(IReadOnlyList<DownloadingItem> items, bool autoAddNumberSuffix, CancellationToken cancellationToken)
    {
        foreach (var item in items) await AdmitCoreAsync(item, autoAddNumberSuffix, cancellationToken).ConfigureAwait(true);
    }

    private async Task AdmitCoreAsync(DownloadingItem item, bool autoAddNumberSuffix, CancellationToken cancellationToken)
    {
        var requestedPath = item.DownloadBase.FilePath;
        await _reservationIndex.EnsureInitializedAsync(cancellationToken).ConfigureAwait(true);
        var suffix = autoAddNumberSuffix ? _reservationIndex.GetNextSuffix(requestedPath) : 0;
        while (true)
        {
            item.DownloadBase.FilePath = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
                requestedPath, autoAddNumberSuffix,
                (candidate, token) => _tasks.IsOutputPathReservedAsync(
                    candidate, DownloadOutputPathKey.UsesCaseInsensitiveComparison, token),
                cancellationToken, initialSuffix: suffix).ConfigureAwait(true);
            var result = await _projections.TryAddDownloadingAsync(item, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess) break;
            if (!result.IsOutputPathConflict) throw new InvalidOperationException(result.ErrorMessage ?? "Download storage rejected admission.");
            if (!autoAddNumberSuffix) throw new IOException("The selected output path is already in use.");
            suffix = checked(GetResolvedSuffix(requestedPath, item.DownloadBase.FilePath) + 1);
        }

        _downloadLists.AddDownloading(item);
        await _taskQueue.EnqueueAsync(new DownloadTaskId(item.DownloadBase.Id), CancellationToken.None).ConfigureAwait(true);
    }

    private static void RestoreOriginalPaths(IReadOnlyList<DownloadingItem> items, string[] originalPaths)
    {
        for (var index = 0; index < items.Count; index++) items[index].DownloadBase.FilePath = originalPaths[index];
    }

    private static int GetResolvedSuffix(string basePath, string resolvedPath)
    {
        // This is logical planning. Create() is the physical SQLite identity.
        var normalizedBase = DownloadOutputPathKey.NormalizeLogicalPath(basePath);
        var normalizedResolved = DownloadOutputPathKey.NormalizeLogicalPath(resolvedPath);
        var comparison = DownloadOutputPathKey.UsesCaseInsensitiveComparison ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedBase, normalizedResolved, comparison)) return 0;
        var prefix = normalizedBase + "(";
        if (normalizedResolved.StartsWith(prefix, comparison) && normalizedResolved.EndsWith(')') &&
            int.TryParse(normalizedResolved[prefix.Length..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var suffix) && suffix > 0) return suffix;
        throw new InvalidOperationException($"Resolved output path '{resolvedPath}' is not a numeric suffix of '{normalizedBase}'.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _reservationIndex.Dispose();
        _admissionGate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
