using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskShutdownRecovery
{
    private readonly IDownloadTaskLookup _tasks;
    private readonly DownloadTaskStateWriter _stateWriter;

    public DownloadTaskShutdownRecovery(
        IDownloadTaskLookup tasks,
        DownloadTaskStateWriter stateWriter)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    }

    public async Task PersistAsync()
    {
        var tasks = await _tasks.GetUnfinishedAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            if (task.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing)
            {
                await _stateWriter.RecoverInterruptedAsync(task.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }
}
