using System.Collections.Concurrent;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

internal sealed class RecordingDownloadTaskQueue : IDownloadTaskQueue
{
    private readonly ConcurrentQueue<DownloadTaskId> _enqueued = new();
    private readonly ConcurrentQueue<DownloadTaskId> _canceled = new();

    public IReadOnlyCollection<DownloadTaskId> Enqueued => _enqueued.ToArray();

    public IReadOnlyCollection<DownloadTaskId> Canceled => _canceled.ToArray();

    public Task EnqueueAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        _enqueued.Enqueue(taskId);
        return Task.CompletedTask;
    }

    public Task<bool> CancelAsync(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        _canceled.Enqueue(taskId);
        return Task.FromResult(true);
    }
}

internal sealed class ReadyDownloadRuntimeAvailability : IDownloadRuntimeAvailability
{
    public void EnsureAcceptingTasks()
    {
    }

    public Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DownloadRuntimeStartupOutcome.Ready());
    }
}
