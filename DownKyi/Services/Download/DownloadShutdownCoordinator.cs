using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

internal static class DownloadShutdownCoordinator
{
    public static async Task StopAsync(
        CancellationTokenSource? tokenSource,
        Task? dispatchTask,
        IReadOnlyCollection<Task> workerTasks,
        TimeSpan workerTimeout,
        Action<TimeoutException> timeoutObserver,
        Func<Task> recoverStateAsync)
    {
        ArgumentNullException.ThrowIfNull(workerTasks);
        ArgumentNullException.ThrowIfNull(timeoutObserver);
        ArgumentNullException.ThrowIfNull(recoverStateAsync);

        var shutdownToken = CancellationToken.None;

        try
        {
            if (tokenSource != null)
            {
                shutdownToken = tokenSource.Token;
                await tokenSource.CancelAsync().ConfigureAwait(false);
            }

            if (dispatchTask != null)
            {
                try
                {
                    await dispatchTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            try
            {
                try
                {
                    await Task.WhenAll(workerTasks)
                        .WaitAsync(workerTimeout)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException e)
                {
                    timeoutObserver(e);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                }
            }
            finally
            {
                await recoverStateAsync().ConfigureAwait(false);
            }
        }
    }
}
