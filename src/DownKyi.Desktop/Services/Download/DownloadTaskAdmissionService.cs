using System;
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
    private readonly IDownloadRuntimeAvailability _runtimeAvailability;
    private readonly IPhysicalOutputPathResolver _physicalOutputPathResolver;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private bool _disposed;

    public DownloadTaskAdmissionService(
        DownloadListState downloadLists,
        IDownloadTaskApplicationService tasks,
        DownloadTaskProjectionStore projections,
        IDownloadTaskQueue taskQueue,
        IDownloadRuntimeAvailability runtimeAvailability,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _runtimeAvailability = runtimeAvailability
            ?? throw new ArgumentNullException(nameof(runtimeAvailability));
        _physicalOutputPathResolver = physicalOutputPathResolver
            ?? throw new ArgumentNullException(nameof(physicalOutputPathResolver));
    }

    public async Task AdmitAsync(
        DownloadingItem item,
        bool autoAddNumberSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            _runtimeAvailability.EnsureReady();
            var physicalBasePath = _physicalOutputPathResolver.ResolvePhysicalBasePath(
                item.DownloadBase.FilePath);
            var admittedBasePath = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
                physicalBasePath,
                autoAddNumberSuffix,
                (candidate, token) => _tasks.IsOutputPathReservedAsync(
                    candidate,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                    token),
                cancellationToken).ConfigureAwait(true);
            item.DownloadBase.FilePath = admittedBasePath;

            await _projections.AddDownloadingAsync(item, cancellationToken).ConfigureAwait(true);

            // Once persisted, admission must finish even if the originating UI operation is canceled.
            _downloadLists.AddDownloading(item);
            var taskId = new DownloadTaskId(item.DownloadBase.Id);
            try
            {
                await _taskQueue.EnqueueAsync(taskId, CancellationToken.None).ConfigureAwait(true);
            }
            catch (DownloadRuntimeUnavailableException)
            {
                var failure = await _tasks.FailAsync(
                    taskId,
                    new DownloadFailure(
                        "download.runtime.unavailable",
                        "Download runtime is unavailable.",
                        true),
                    CancellationToken.None).ConfigureAwait(true);
                if (!failure.IsSuccess)
                {
                    throw new InvalidOperationException(
                        failure.Error?.Message
                            ?? "The unavailable download runtime state could not be persisted.");
                }

                throw;
            }
        }
        finally
        {
            _admissionGate.Release();
        }
    }

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
