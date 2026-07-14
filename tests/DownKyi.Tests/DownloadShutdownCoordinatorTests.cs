using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadShutdownCoordinatorTests
{
    [Fact]
    public async Task StopAsyncCancellationWhileDispatchWaitsStillRecoversState()
    {
        using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var occupiedSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchTask = occupiedSlot.Task.WaitAsync(tokenSource.Token);
        var workerStopped = false;
        var workerTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, tokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
            {
            }
            finally
            {
                workerStopped = true;
            }
        }, TestContext.Current.CancellationToken);
        var recoveryCount = 0;

        await DownloadShutdownCoordinator.StopAsync(
            tokenSource,
            dispatchTask,
            [workerTask],
            TimeSpan.FromSeconds(1),
            _ => { },
            () =>
            {
                Assert.True(workerStopped);
                recoveryCount++;
                return Task.CompletedTask;
            });

        Assert.True(dispatchTask.IsCanceled);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task StopAsyncUnexpectedDispatchFailureRecoversBeforeRethrowing()
    {
        var recovered = false;
        var failure = new InvalidOperationException("dispatch failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadShutdownCoordinator.StopAsync(
                null,
                Task.FromException(failure),
                [],
                TimeSpan.FromSeconds(1),
                _ => { },
                () =>
                {
                    recovered = true;
                    return Task.CompletedTask;
                }));

        Assert.Same(failure, exception);
        Assert.True(recovered);
    }
}
