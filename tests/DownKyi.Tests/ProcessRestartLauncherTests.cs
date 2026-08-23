using DownKyi.Platform;

namespace DownKyi.Tests;

public sealed class ProcessRestartLauncherTests
{
    [Theory]
    [InlineData("--restart-after-pid", "1", "--restart-authorization-pipe", "pipe-1", 1)]
    [InlineData("--restart-after-pid", "2147483647", "--restart-authorization-pipe", "pipe-2", int.MaxValue)]
    public void RestartHelperArgumentsRequireProcessAndAuthorizationChannel(
        string option,
        string value,
        string pipeOption,
        string pipeHandle,
        int expectedProcessId)
    {
        var parsed = ProcessRestartLauncher.TryParseRestartRequest(
            [option, value, pipeOption, pipeHandle],
            out var processId,
            out var parsedPipeHandle);

        Assert.True(parsed);
        Assert.Equal(expectedProcessId, processId);
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
    [InlineData("--restart-after-pid", "42", "--restart-authorization-pipe", "")]
    [InlineData("--restart-after-pid", "42", "--wrong-pipe-option", "pipe")]
    public void RestartHelperArgumentsRejectMalformedInput(params string[] arguments)
    {
        Assert.False(ProcessRestartLauncher.TryParseRestartRequest(arguments, out _, out _));
    }

    [Fact]
    public void RestartStartInfoUsesArgumentListWithoutShellParsing()
    {
        var startInfo = ProcessRestartLauncher.CreateStartInfo(42, "pipe-handle");

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ProcessRestartLauncher.WaitForParentArgument,
            startInfo.ArgumentList[^4]);
        Assert.Equal("42", startInfo.ArgumentList[^3]);
        Assert.Equal(
            ProcessRestartLauncher.AuthorizationPipeArgument,
            startInfo.ArgumentList[^2]);
        Assert.Equal("pipe-handle", startInfo.ArgumentList[^1]);
        Assert.DoesNotContain(ProcessRestartLauncher.WaitForParentArgument, startInfo.Arguments, StringComparison.Ordinal);
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
            _ =>
            {
                restartCount++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.False(committed);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task ExplicitCommitAuthorizationInvokesRestartExactlyOnce()
    {
        using var stream = new MemoryStream([1]);
        var restartCount = 0;

        var committed = await ProcessRestartLauncher.ExecuteAuthorizedRestartAsync(
            stream,
            _ =>
            {
                restartCount++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(committed);
        Assert.Equal(1, restartCount);
    }
}
