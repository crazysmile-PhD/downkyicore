using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal interface IDownloadTaskQueue
{
    Task EnqueueAsync(DownloadTaskId taskId, CancellationToken cancellationToken = default);

    async Task EnqueueManyAsync(
        IReadOnlyList<DownloadTaskId> taskIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);

        foreach (var taskId in taskIds)
        {
            ArgumentNullException.ThrowIfNull(taskId);

            await EnqueueAsync(
                    taskId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
    Task<bool> CancelAsync(DownloadTaskId taskId);
}

internal interface IDownloadRuntime : IDownloadTaskQueue, IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
