using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadCommittedTaskDispatcher
{
    private readonly IDownloadTaskQueue _taskQueue;
    private readonly DownloadTaskStateWriter _stateWriter;

    public DownloadCommittedTaskDispatcher(
        IDownloadTaskQueue taskQueue,
        DownloadTaskStateWriter stateWriter)
    {
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    }

    public async Task EnqueueCommittedAsync(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        try
        {
            await _taskQueue.EnqueueAsync(taskId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DownloadRuntimeUnavailableException)
        {
            await _stateWriter.FailRuntimeUnavailableAsync(
                taskId,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
