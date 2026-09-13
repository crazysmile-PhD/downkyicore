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
    void EnsureAcceptingTasks();

    Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
        CancellationToken cancellationToken = default);
}

internal enum DownloadRuntimeStartupState
{
    Ready,
    Faulted
}

internal sealed record DownloadRuntimeStartupOutcome
{
    private DownloadRuntimeStartupOutcome(
        DownloadRuntimeStartupState state,
        Exception? failure)
    {
        State = state;
        Failure = failure;
    }

    public DownloadRuntimeStartupState State { get; }

    public Exception? Failure { get; }

    public static DownloadRuntimeStartupOutcome Ready() =>
        new(DownloadRuntimeStartupState.Ready, null);

    public static DownloadRuntimeStartupOutcome Faulted(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new DownloadRuntimeStartupOutcome(
            DownloadRuntimeStartupState.Faulted,
            failure);
    }
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
