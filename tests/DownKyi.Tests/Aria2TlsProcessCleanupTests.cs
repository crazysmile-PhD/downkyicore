using System.Diagnostics;

namespace DownKyi.Tests;

public sealed class Aria2TlsProcessCleanupTests
{
    [Fact]
    public async Task CompletedShutdownThatDoesNotExitIsKilledAndReapedWithoutFailure()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alive = true;
        var killed = false;

        await Aria2TlsProcessCleanup.RunAsync(
            new Aria2TlsProcessCleanupOperations(
                () => !alive,
                _ => Task.CompletedTask,
                token => exit.Task.WaitAsync(token),
                () =>
                {
                    killed = true;
                    alive = false;
                    exit.TrySetResult();
                },
                Task.CompletedTask,
                Task.CompletedTask,
                () => Task.CompletedTask),
            TimeSpan.FromSeconds(1)).ConfigureAwait(true);

        Assert.True(killed);
        Assert.True(exit.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HungShutdownIsKilledReapedAndJoinedWithinTheSameDeadline()
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alive = true;
        var killed = false;
        var clock = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => Aria2TlsProcessCleanup.RunAsync(
                new Aria2TlsProcessCleanupOperations(
                    () => !alive,
                    token =>
                    {
                        token.Register(() => shutdown.TrySetCanceled(token));
                        return shutdown.Task;
                    },
                    token => exit.Task.WaitAsync(token),
                    () =>
                    {
                        killed = true;
                        alive = false;
                        exit.TrySetResult();
                        output.TrySetResult();
                        error.TrySetResult();
                    },
                    output.Task,
                    error.Task,
                    () => Task.CompletedTask),
                TimeSpan.FromSeconds(2))).ConfigureAwait(true);

        Assert.Contains("force-shutdown", exception.Message, StringComparison.Ordinal);
        Assert.True(killed);
        Assert.True(shutdown.Task.IsCanceled);
        Assert.True(exit.Task.IsCompletedSuccessfully);
        Assert.True(output.Task.IsCompletedSuccessfully);
        Assert.True(error.Task.IsCompletedSuccessfully);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task HungOutputIsCanceledAndJoinedBeforeTheDeadlineReturns()
    {
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputCanceled = false;

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => Aria2TlsProcessCleanup.RunAsync(
                new Aria2TlsProcessCleanupOperations(
                    () => true,
                    RequestShutdownAsync: null,
                    _ => Task.CompletedTask,
                    () => throw new InvalidOperationException("An exited process must not be killed."),
                    output.Task,
                    Task.CompletedTask,
                    () =>
                    {
                        outputCanceled = true;
                        output.TrySetResult();
                        return Task.CompletedTask;
                    }),
                TimeSpan.FromSeconds(2))).ConfigureAwait(true);

        Assert.Contains("stdout/stderr drain", exception.Message, StringComparison.Ordinal);
        Assert.True(outputCanceled);
        Assert.True(output.Task.IsCompletedSuccessfully);
    }
}
