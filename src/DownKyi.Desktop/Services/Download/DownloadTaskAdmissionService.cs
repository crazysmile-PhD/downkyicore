using System;
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
    }

    public async Task AdmitAsync(
        DownloadingItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var unfinishedTasks = await _tasks
                .GetUnfinishedAsync(cancellationToken)
                .ConfigureAwait(true);
            var reservedPaths = unfinishedTasks
                .Where(task => ReservesOutputPath(task.Phase))
                .Select(task => task.Output.BasePath);
            item.DownloadBase.FilePath = DownloadOutputPathResolver.ResolveActiveCollision(
                item.DownloadBase.FilePath,
                reservedPaths);

            await _projections.AddDownloadingAsync(item, cancellationToken).ConfigureAwait(true);

            // Once persisted, admission must finish even if the originating UI operation is canceled.
            _downloadLists.AddDownloading(item);
            await _taskQueue.EnqueueAsync(
                new DownloadTaskId(item.DownloadBase.Id),
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    private static bool ReservesOutputPath(DownloadPhase phase) =>
        phase is DownloadPhase.Queued or
            DownloadPhase.Downloading or
            DownloadPhase.Pausing or
            DownloadPhase.Paused or
            DownloadPhase.Failed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _admissionGate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
