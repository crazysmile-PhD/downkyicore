using System;
using System.Collections.Generic;
using System.IO;
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

    public DownloadTaskAdmissionService(
        DownloadListState downloadLists,
        IDownloadTaskApplicationService tasks,
        DownloadTaskProjectionStore projections,
        IDownloadTaskQueue taskQueue)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _reservationIndex = new DownloadOutputReservationIndex(_tasks);
    }

    public async Task AdmitAsync(
        DownloadingItem item,
        bool autoAddNumberSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _admissionGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);

        try
        {
            await AdmitCoreAsync(
                    item,
                    autoAddNumberSuffix,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    public async Task AdmitManyAsync(
        IReadOnlyList<DownloadingItem> items,
        bool autoAddNumberSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (items.Count == 0)
        {
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            ArgumentNullException.ThrowIfNull(items[index]);
        }

        await _admissionGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);

        try
        {
            // Capability fallback keeps test doubles and non-SQLite stores
            // on the already verified single-item semantics.
            if (items.Count == 1 ||
                _tasks is not IDownloadTaskAtomicBatchApplicationService)
            {
                await AdmitSequentialCoreAsync(
                        items,
                        autoAddNumberSuffix,
                        cancellationToken)
                    .ConfigureAwait(true);

                return;
            }

            await _reservationIndex
                .EnsureInitializedAsync(cancellationToken)
                .ConfigureAwait(true);

            var originalPaths =
                new string[items.Count];

            var occupiedPaths =
                DownloadOutputPathResolver
                    .CaptureExistingBasePaths(
                        items.Select(
                            static item =>
                                item.DownloadBase.FilePath));

            for (var index = 0; index < items.Count; index++)
            {
                originalPaths[index] =
                    items[index].DownloadBase.FilePath;
            }

            var planningComparer =
                DownloadOutputPathKey
                    .UsesCaseInsensitiveComparison
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

            var plannedKeys =
                new HashSet<string>(
                    planningComparer);

            // The durable reservation index cannot advance until the
            // atomic batch commits and publishes TaskChanged events.
            // Track the next suffix locally so repeated basenames inside
            // this batch do not restart their search from the same suffix.
            var batchNextSuffixes =
                new Dictionary<string, int>(
                    planningComparer);

            try
            {
                for (var index = 0;
                     index < items.Count;
                     index++)
                {
                    var item = items[index];
                    var requestedBasePath =
                        originalPaths[index];

                    var requestedKey =
                        DownloadOutputPathKey
                            .NormalizeLogicalPath(
                                requestedBasePath);

                    var initialSuffix = 0;

                    if (autoAddNumberSuffix)
                    {
                        if (batchNextSuffixes.TryGetValue(
                                requestedKey,
                                out var batchNextSuffix))
                        {
                            initialSuffix =
                                batchNextSuffix;
                        }
                        else
                        {
                            initialSuffix =
                                _reservationIndex.GetNextSuffix(
                                    requestedBasePath);
                        }
                    }

                    item.DownloadBase.FilePath =
                        await DownloadOutputPathResolver
                            .ResolveAdmissionCollisionAsync(
                                requestedBasePath,
                                autoAddNumberSuffix,
                                (candidate, _) =>
                                {
                                    var key =
                                        DownloadOutputPathKey
                                            .NormalizeLogicalPath(
                                                candidate);

                                    return Task.FromResult(
                                        plannedKeys.Contains(key));
                                },
                                cancellationToken,
                                initialSuffix:
                                    initialSuffix,
                                occupiedPaths:
                                    occupiedPaths)
                            .ConfigureAwait(true);

                    var plannedKey =
                        DownloadOutputPathKey
                            .NormalizeLogicalPath(
                                item.DownloadBase.FilePath);

                    if (!plannedKeys.Add(plannedKey))
                    {
                        throw new InvalidOperationException(
                            "Batch admission produced a duplicate planned reservation.");
                    }

                    if (autoAddNumberSuffix)
                    {
                        batchNextSuffixes[requestedKey] =
                            checked(
                                GetResolvedSuffix(
                                    requestedBasePath,
                                    item.DownloadBase.FilePath)
                                + 1);
                    }
                }
            }
            catch (IOException)
            {
                // Preserve old partial-success semantics for fail-closed
                // collisions by replaying the batch one item at a time.
                RestoreOriginalPaths(
                    items,
                    originalPaths);

                await AdmitSequentialCoreAsync(
                        items,
                        autoAddNumberSuffix,
                        cancellationToken)
                    .ConfigureAwait(true);

                return;
            }
            catch
            {
                RestoreOriginalPaths(
                    items,
                    originalPaths);

                throw;
            }

            DownloadProjectionAddResult addResult;

            try
            {
                addResult =
                    await _projections
                        .TryAddDownloadingManyAtomicAsync(
                            items,
                            cancellationToken)
                        .ConfigureAwait(true);
            }
            catch
            {
                RestoreOriginalPaths(
                    items,
                    originalPaths);

                throw;
            }

            if (!addResult.IsSuccess)
            {
                // Atomic DB batch was rolled back. Replay through the
                // verified optimistic single-item path so ID conflicts,
                // stale reservation snapshots and legacy cases keep their
                // existing semantics.
                RestoreOriginalPaths(
                    items,
                    originalPaths);

                await AdmitSequentialCoreAsync(
                        items,
                        autoAddNumberSuffix,
                        cancellationToken)
                    .ConfigureAwait(true);

                return;
            }

            var taskIds =
                new DownloadTaskId[items.Count];

            // Persistence is durable now. Finish list + queue even if the
            // originating UI operation gets canceled afterwards.
            for (var index = 0;
                 index < items.Count;
                 index++)
            {
                var item = items[index];

                _downloadLists.AddDownloading(item);

                taskIds[index] =
                    new DownloadTaskId(
                        item.DownloadBase.Id);
            }

            await _taskQueue
                .EnqueueManyAsync(
                    taskIds,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    private async Task AdmitSequentialCoreAsync(
        IReadOnlyList<DownloadingItem> items,
        bool autoAddNumberSuffix,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            await AdmitCoreAsync(
                    item,
                    autoAddNumberSuffix,
                    cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private async Task AdmitCoreAsync(
        DownloadingItem item,
        bool autoAddNumberSuffix,
        CancellationToken cancellationToken)
    {
        var requestedBasePath =
            item.DownloadBase.FilePath;

        await _reservationIndex
            .EnsureInitializedAsync(cancellationToken)
            .ConfigureAwait(true);

        var initialSuffix =
            autoAddNumberSuffix
                ? _reservationIndex.GetNextSuffix(
                    requestedBasePath)
                : 0;

        while (true)
        {
            item.DownloadBase.FilePath =
                await DownloadOutputPathResolver
                    .ResolveAdmissionCollisionAsync(
                        requestedBasePath,
                        autoAddNumberSuffix,
                        static (_, _) =>
                            Task.FromResult(false),
                        cancellationToken,
                        initialSuffix:
                            initialSuffix)
                    .ConfigureAwait(true);

            var addResult =
                await _projections
                    .TryAddDownloadingAsync(
                        item,
                        cancellationToken)
                    .ConfigureAwait(true);

            if (addResult.IsSuccess)
            {
                break;
            }

            if (!addResult.IsOutputPathConflict)
            {
                throw new InvalidOperationException(
                    addResult.ErrorMessage
                    ?? "Download storage rejected admission.");
            }

            if (!autoAddNumberSuffix)
            {
                throw new IOException(
                    "The selected output path is already in use.");
            }

            initialSuffix =
                checked(
                    GetResolvedSuffix(
                        requestedBasePath,
                        item.DownloadBase.FilePath)
                    + 1);
        }

        _downloadLists.AddDownloading(item);

        await _taskQueue
            .EnqueueAsync(
                new DownloadTaskId(
                    item.DownloadBase.Id),
                CancellationToken.None)
            .ConfigureAwait(true);
    }

    private static void RestoreOriginalPaths(
        IReadOnlyList<DownloadingItem> items,
        string[] originalPaths)
    {
        for (var index = 0;
             index < items.Count;
             index++)
        {
            items[index].DownloadBase.FilePath =
                originalPaths[index];
        }
    }
    private static int GetResolvedSuffix(
        string basePath,
        string resolvedPath)
    {
        var normalizedBase =
            DownloadOutputPathKey.Create(
                basePath,
                ignoreCase: false);

        var comparison =
            DownloadOutputPathKey
                .UsesCaseInsensitiveComparison
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (string.Equals(
                normalizedBase,
                resolvedPath,
                comparison))
        {
            return 0;
        }

        var prefix =
            normalizedBase + "(";

        if (resolvedPath.StartsWith(
                prefix,
                comparison) &&
            resolvedPath.EndsWith(')'))
        {
            var suffixText =
                resolvedPath[
                    prefix.Length..
                    ^1];

            if (int.TryParse(
                    suffixText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var suffix) &&
                suffix > 0)
            {
                return suffix;
            }
        }

        throw new InvalidOperationException(
            $"Resolved output path '{resolvedPath}' " +
            $"is not a numeric suffix of '{normalizedBase}'.");
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _reservationIndex.Dispose();
        _admissionGate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
