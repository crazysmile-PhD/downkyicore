using System.Diagnostics;
using DownKyi.Platform;

namespace DownKyi.Tests;

public sealed class ProcessRestartLauncherTests
{
    [Theory]
    [InlineData("--restart-after-pid", "1", "1", "--restart-authorization-pipe", "pipe-1", 1, 1)]
    [InlineData("--restart-after-pid", "2147483647", "3155378975999999999", "--restart-authorization-pipe", "pipe-2", int.MaxValue, 3155378975999999999)]
    public void RestartHelperArgumentsRequireProcessAndAuthorizationChannel(
        string option,
        string value,
        string parentStartedAtUtcTicks,
        string pipeOption,
        string pipeHandle,
        int expectedProcessId,
        long expectedParentStartedAtUtcTicks)
    {
        var parsed = ProcessRestartLauncher.TryParseRestartRequest(
            [
                option,
                value,
                ProcessRestartLauncher.ParentStartedAtArgument,
                parentStartedAtUtcTicks,
                pipeOption,
                pipeHandle
            ],
            out var processId,
            out var parsedParentStartedAtUtcTicks,
            out var parsedPipeHandle);

        Assert.True(parsed);
        Assert.Equal(expectedProcessId, processId);
        Assert.Equal(expectedParentStartedAtUtcTicks, parsedParentStartedAtUtcTicks);
        Assert.Equal(pipeHandle, parsedPipeHandle);
    }

    [Theory]
    [InlineData()]
    [InlineData("--restart-after-pid")]
    [InlineData("--restart-after-pid", "0")]
    [InlineData("--restart-after-pid", "-1")]
    [InlineData("--restart-after-pid", "not-a-process")]
    [InlineData("--unrelated", "42")]
    [InlineData("--restart-after-pid", "42", "extra")]
    [InlineData("--restart-after-pid", "42", "--restart-parent-started-at-utc-ticks", "0", "--restart-authorization-pipe", "pipe")]
    [InlineData("--restart-after-pid", "42", "--restart-parent-started-at-utc-ticks", "invalid", "--restart-authorization-pipe", "pipe")]
    [InlineData("--restart-after-pid", "42", "--restart-parent-started-at-utc-ticks", "9223372036854775807", "--restart-authorization-pipe", "pipe")]
    [InlineData("--restart-after-pid", "42", "--restart-parent-started-at-utc-ticks", "1", "--restart-authorization-pipe", "")]
    [InlineData("--restart-after-pid", "42", "--restart-parent-started-at-utc-ticks", "1", "--wrong-pipe-option", "pipe")]
    public void RestartHelperArgumentsRejectMalformedInput(params string[] arguments)
    {
        Assert.False(ProcessRestartLauncher.TryParseRestartRequest(arguments, out _, out _, out _));
    }

    [Fact]
    public void RestartStartInfoUsesArgumentListWithoutShellParsing()
    {
        var startInfo = ProcessRestartLauncher.CreateStartInfo(42, 123456789, "pipe-handle");

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ProcessRestartLauncher.WaitForParentArgument,
            startInfo.ArgumentList[^6]);
        Assert.Equal("42", startInfo.ArgumentList[^5]);
        Assert.Equal(
            ProcessRestartLauncher.ParentStartedAtArgument,
            startInfo.ArgumentList[^4]);
        Assert.Equal("123456789", startInfo.ArgumentList[^3]);
        Assert.Equal(
            ProcessRestartLauncher.AuthorizationPipeArgument,
            startInfo.ArgumentList[^2]);
        Assert.Equal("pipe-handle", startInfo.ArgumentList[^1]);
        Assert.DoesNotContain(ProcessRestartLauncher.WaitForParentArgument, startInfo.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void RestartHelperCapturesAStableParentHandleBeforeAuthorizationWait()
    {
        using var current = Process.GetCurrentProcess();
        var parentStartedAtUtcTicks = current.StartTime.ToUniversalTime().Ticks;
        using var parent = ProcessRestartLauncher.CaptureParentProcess(
            Environment.ProcessId,
            parentStartedAtUtcTicks);

        Assert.NotNull(parent);
        Assert.Equal(Environment.ProcessId, parent.Id);
        Assert.NotEqual(nint.Zero, parent.Handle);
    }

    [Fact]
    public void RestartHelperRejectsAReusedParentProcessId()
    {
        using var current = Process.GetCurrentProcess();
        var staleStartedAtUtcTicks = current.StartTime.ToUniversalTime().Ticks + 1;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProcessRestartLauncher.CaptureParentProcess(
                Environment.ProcessId,
                staleStartedAtUtcTicks));

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task UncommittedAuthorizationNeverInvokesRestart(int? authorization)
    {
        var bytes = authorization.HasValue ? new[] { (byte)authorization.Value } : [];
        using var stream = new MemoryStream(bytes);
        var restartCount = 0;

        var committed = await ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
            stream,
            _ => throw new InvalidOperationException("Uncommitted restart must not wait for the parent."),
            _ =>
            {
                restartCount++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.False(committed);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task ExplicitCommitAuthorizationInvokesRestartExactlyOnce()
    {
        using var stream = new MemoryStream([1]);
        var parentExitWaitCount = 0;
        var restartCount = 0;

        var committed = await ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
            stream,
            _ =>
            {
                parentExitWaitCount++;
                return Task.CompletedTask;
            },
            _ =>
            {
                restartCount++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.True(committed);
        Assert.Equal(1, parentExitWaitCount);
        Assert.Equal(1, restartCount);
    }

    [Fact]
    public async Task CommittedRestartFailsClosedWhenParentDoesNotExitByItsDeadline()
    {
        using var stream = new MemoryStream([1]);
        var parentExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restartCount = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
                stream,
                _ => parentExit.Task,
                _ =>
                {
                    restartCount++;
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task CommittedRestartPreservesParentWaitFailureWithoutRelaunching()
    {
        using var stream = new MemoryStream([1]);
        var parentFailure = new IOException("parent wait failed");
        var restartCount = 0;

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
                stream,
                _ => Task.FromException(parentFailure),
                _ =>
                {
                    restartCount++;
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        Assert.Same(parentFailure, exception);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task CommittedRestartCancellationStopsTheParentWaitWithoutRelaunching()
    {
        using var stream = new MemoryStream([1]);
        using var cancellation = new CancellationTokenSource();
        var restartCount = 0;
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
                stream,
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                _ =>
                {
                    restartCount++;
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(1),
                cancellation.Token));
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task CommittedRestartPreservesRelaunchFailure()
    {
        using var stream = new MemoryStream([1]);
        var restartFailure = new InvalidOperationException("relaunch failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
                stream,
                _ => Task.CompletedTask,
                _ => Task.FromException(restartFailure),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        Assert.Same(restartFailure, exception);
    }

    [Fact]
    public async Task OwnedRestartHelperTerminationFailsClosedAtItsDeadline()
    {
        var neverExits = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = false;
        var released = false;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRestartLauncher.RevokeOwnedHelperAsync(
                () => ValueTask.CompletedTask,
                () => false,
                () => terminated = true,
                () => neverExits.Task,
                () => released = true,
                TimeSpan.FromMilliseconds(50)));

        Assert.True(terminated);
        Assert.True(released);
    }

    [Fact]
    public async Task OwnedRestartHelperRevocationPreservesConcurrentFailures()
    {
        var closeFailure = new IOException("authorization close failed");
        var terminationFailure = new InvalidOperationException("termination failed");
        var releaseFailure = new ObjectDisposedException("process release failed");

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            ProcessRestartLauncher.RevokeOwnedHelperAsync(
                () => ValueTask.FromException(closeFailure),
                () => false,
                () => throw terminationFailure,
                () => Task.CompletedTask,
                () => throw releaseFailure,
                TimeSpan.FromMilliseconds(50)));

        Assert.Equal([closeFailure, terminationFailure, releaseFailure], exception.InnerExceptions);
    }
}
