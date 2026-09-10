using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadTaskQueueGatewayTests
{
    [Fact]
    public async Task TasksAdmittedBeforeRuntimeStartAreFlushedOnceOnReady()
    {
        var gateway = new DownloadTaskQueueGateway();
        var taskId = new DownloadTaskId("early-task");
        using var runtime = new RecordingRuntime();

        await gateway.EnqueueAsync(taskId, TestContext.Current.CancellationToken);
        await gateway.EnqueueAsync(taskId, TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Enqueued);

        await gateway.AttachAsync(runtime, TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Enqueued);
        await gateway.MarkReadyAsync(runtime, TestContext.Current.CancellationToken);

        Assert.Equal(taskId, Assert.Single(runtime.Enqueued));
        var outcome = await gateway.WaitForStartupOutcomeAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(DownloadRuntimeStartupState.Ready, outcome.State);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public async Task CancelBeforeRuntimeStartRemovesPendingAdmission()
    {
        var gateway = new DownloadTaskQueueGateway();
        var taskId = new DownloadTaskId("canceled-early-task");
        using var runtime = new RecordingRuntime();

        await gateway.EnqueueAsync(taskId, TestContext.Current.CancellationToken);
        Assert.False(await gateway.CancelAsync(taskId));
        await gateway.AttachAsync(runtime, TestContext.Current.CancellationToken);
        await gateway.MarkReadyAsync(runtime, TestContext.Current.CancellationToken);

        Assert.Empty(runtime.Enqueued);
    }

    [Fact]
    public async Task TerminalFailureReturnsPendingTasksAndRejectsFurtherAdmission()
    {
        var gateway = new DownloadTaskQueueGateway();
        var pendingTask = new DownloadTaskId("pending-before-failure");
        await gateway.EnqueueAsync(pendingTask, TestContext.Current.CancellationToken);

        var failure = new InvalidOperationException("Synthetic bootstrap failure.");
        var returned = gateway.MarkFaulted(failure);

        Assert.Equal(pendingTask, Assert.Single(returned));
        Assert.Throws<DownloadRuntimeUnavailableException>(() => gateway.EnsureAcceptingTasks());
        await Assert.ThrowsAsync<DownloadRuntimeUnavailableException>(() => gateway.EnqueueAsync(
            new DownloadTaskId("after-failure"),
            TestContext.Current.CancellationToken));
        var outcome = await gateway.WaitForStartupOutcomeAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(DownloadRuntimeStartupState.Faulted, outcome.State);
        Assert.Same(failure, outcome.Failure);
    }

    private sealed class RecordingRuntime : IDownloadRuntime
    {
        public List<DownloadTaskId> Enqueued { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enqueued.Add(taskId);
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId)
        {
            return Task.FromResult(false);
        }

        public void Dispose()
        {
        }
    }
}
