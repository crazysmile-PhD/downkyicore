using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal interface IDownloadTaskQueue
{
    Task EnqueueAsync(DownloadTaskId taskId, CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(DownloadTaskId taskId);
}

internal interface IDownloadRuntimeAvailability
{
    void EnsureReady();
}

internal sealed class DownloadRuntimeUnavailableException : InvalidOperationException
{
    public DownloadRuntimeUnavailableException()
    {
    }

    public DownloadRuntimeUnavailableException(string message)
        : base(message)
    {
    }

    public DownloadRuntimeUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal interface IDownloadRuntime : IDownloadTaskQueue, IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
