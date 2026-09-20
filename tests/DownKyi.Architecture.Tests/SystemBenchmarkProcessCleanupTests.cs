using System.Diagnostics;
using DownKyi.SystemBenchmarks;

namespace DownKyi.Architecture.Tests;

public sealed class SystemBenchmarkProcessCleanupTests
{
    [Fact]
    public async Task CancellationCleanupKillsAndJoinsTheOwnedChild()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var killed = false;

        await SystemBenchmarkProcessCleanup.RunAsync(
            new SystemBenchmarkProcessCleanupOperations(
                () => exited,
                () =>
                {
                    killed = true;
                    exited = true;
                    exit.TrySetResult();
                    output.TrySetResult();
                    error.TrySetResult();
                },
                token => exit.Task.WaitAsync(token),
                output.Task,
                error.Task,
                () => Task.CompletedTask),
            TimeSpan.FromSeconds(1)).ConfigureAwait(true);

        Assert.True(killed);
        Assert.True(exit.Task.IsCompletedSuccessfully);
        Assert.True(output.Task.IsCompletedSuccessfully);
        Assert.True(error.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationCleanupBoundsAndJoinsOutputDrain()
    {
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputCanceled = false;
        var clock = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => SystemBenchmarkProcessCleanup.RunAsync(
                new SystemBenchmarkProcessCleanupOperations(
                    () => true,
                    () => throw new InvalidOperationException("An exited process must not be killed."),
                    _ => Task.CompletedTask,
                    output.Task,
                    Task.CompletedTask,
                    () =>
                    {
                        outputCanceled = true;
                        output.TrySetResult();
                        return Task.CompletedTask;
                    }),
                TimeSpan.FromSeconds(1))).ConfigureAwait(true);

        Assert.Contains("output did not drain", exception.Message, StringComparison.Ordinal);
        Assert.True(outputCanceled);
        Assert.True(output.Task.IsCompletedSuccessfully);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }
}
