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
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskQueue _taskQueue;
    private readonly IDownloadRuntimeAvailability _runtimeAvailability;
    private readonly IPhysicalOutputPathResolver _physicalOutputPathResolver;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private bool _disposed;

    public DownloadTaskAdmissionService(
        DownloadListState downloadLists,
        IDownloadTaskApplicationService tasks,
        DownloadTaskProjectionStore projections,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskQueue taskQueue,
        IDownloadRuntimeAvailability runtimeAvailability,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
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
            _runtimeAvailability.EnsureAcceptingTasks();
            var physicalBasePath = _physicalOutputPathResolver.ResolvePhysicalBasePath(
                item.DownloadBase.FilePath);
            var admittedBasePath = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
                physicalBasePath,
                autoAddNumberSuffix,
                (path, token) => _tasks.IsOutputPathReservedAsync(
                    path,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                    token),
                token => _tasks.GetActiveOutputReservationKeysAsync(
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
                await _stateWriter.FailRuntimeUnavailableAsync(
                    taskId,
                    CancellationToken.None).ConfigureAwait(true);
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
